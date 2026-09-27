using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NightSignal.Core.Customization
{
    public enum LiveryEditStatus
    {
        /// <summary>The draft changed (one undo step, or merged into the running gesture).</summary>
        Changed = 0,
        /// <summary>Valid request that changes nothing; no undo step is recorded.</summary>
        Unchanged = 1,
        /// <summary>Invalid for this chassis/catalogue; the draft is untouched. See <see cref="LiveryEditResult.Reason"/>.</summary>
        Rejected = 2,
    }

    /// <summary>Immutable outcome of one editor operation.</summary>
    public sealed class LiveryEditResult
    {
        public LiveryEditStatus Status { get; }
        public string Reason { get; }
        public bool Changed => Status == LiveryEditStatus.Changed;

        LiveryEditResult(LiveryEditStatus status, string reason)
        {
            Status = status;
            Reason = reason ?? "";
        }

        internal static readonly LiveryEditResult Same = new LiveryEditResult(LiveryEditStatus.Unchanged, "");
        internal static readonly LiveryEditResult Done = new LiveryEditResult(LiveryEditStatus.Changed, "");

        internal static LiveryEditResult Reject(string reason) => new LiveryEditResult(LiveryEditStatus.Rejected, reason);
    }

    public sealed class LiveryApplyResult
    {
        public bool Applied;
        /// <summary>The accepted document (a copy) and its identity; null when refused.</summary>
        public LiveryDocument Document;
        public string LiveryHash = "";
        public string CanonicalJson = "";
        /// <summary>Apply-mode validation (errors include locked items).</summary>
        public LiveryValidation Validation;
    }

    /// <summary>
    /// Garage livery editing with the spec's draft model: every operation edits a DRAFT copy of the applied livery; Apply
    /// validates (ownership included) and returns the new document and hash; Cancel restores the applied appearance. Each
    /// meaningful change is one undo step (at least <see cref="LiveryLimits.MinUndoSteps"/>, <see cref="MaxUndoSteps"/>
    /// kept); a slider drag passes <c>continuing: true</c> so it becomes ONE step. Pure and deterministic — no clock, no IO,
    /// no randomness. Locked items may be previewed in the draft; applying them is refused.
    /// </summary>
    public sealed class LiveryEditor
    {
        public const int MaxUndoSteps = 64;

        readonly CustomizationCatalogue catalogue;
        readonly ChassisAppearanceDef chassis;
        LiveryDocument applied;
        LiveryDocument draft;
        readonly List<Step> undo = new List<Step>();
        readonly List<Step> redo = new List<Step>();
        /// <summary>Key of the operation that produced the newest undo step while a gesture may still extend it.</summary>
        string gestureKey;

        sealed class Step
        {
            public LiveryDocument Before;
            public string Key;
        }

        /// <param name="applied">The car's current applied livery, or null for the chassis' stock livery.</param>
        public LiveryEditor(CustomizationCatalogue catalogue, string carId, LiveryDocument applied)
        {
            this.catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
            chassis = catalogue.ChassisFor(carId);
            if (applied != null && applied.Car != carId) throw new ArgumentException($"The livery is for {applied.Car}, not {carId}", nameof(applied));
            this.applied = (applied ?? LiveryDocument.Stock(chassis)).Clone();
            draft = this.applied.Clone();
        }

        public string CarId => chassis.Car;
        public ChassisAppearanceDef Chassis => chassis;
        /// <summary>A copy of the applied (accepted) livery.</summary>
        public LiveryDocument Applied => applied.Clone();
        /// <summary>A copy of the draft being edited.</summary>
        public LiveryDocument Draft => draft.Clone();
        public string DraftHash => LiveryHash.Of(draft);
        public string AppliedHash => LiveryHash.Of(applied);
        public bool IsDirty => !draft.ContentEquals(applied);
        public int UndoCount => undo.Count;
        public int RedoCount => redo.Count;
        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public int DecalCount => draft.Decals.Count;

        // ------------------------------------------------------------------ body

        public LiveryEditResult SetVariant(string family, string variant)
        {
            if (!AppearanceVocabulary.IsFamily(family)) return LiveryEditResult.Reject($"Unknown body family '{family}'.");
            if (!chassis.Offers(family, variant)) return LiveryEditResult.Reject($"'{variant}' is not offered for {family} on {CarId}.");
            return Edit("body." + family, false, d => d.Body.Set(family, variant));
        }

        // ------------------------------------------------------------------ wheels

        /// <summary>Selects a rim design. If the current size is not made in that design, the nearest fitting size is used.</summary>
        public LiveryEditResult SetRim(string rimId)
        {
            if (!catalogue.TryRim(rimId, out RimDesignDef rim)) return LiveryEditResult.Reject($"Unknown rim design '{rimId}'.");
            IReadOnlyList<int> sizes = CustomizationCatalogue.FittingDiameters(rim, chassis);
            if (sizes.Count == 0) return LiveryEditResult.Reject($"{rim.Name} does not fit {CarId}.");
            int size = sizes.Contains(draft.Wheels.DiameterIn) ? draft.Wheels.DiameterIn : sizes.OrderBy(s => Math.Abs(s - draft.Wheels.DiameterIn)).ThenBy(s => s).First();
            return Edit("wheels.rim", false, d => { d.Wheels.Rim = rim.Id; d.Wheels.DiameterIn = size; });
        }

        public LiveryEditResult SetRimDiameter(int diameterIn)
        {
            if (!catalogue.TryRim(draft.Wheels.Rim, out RimDesignDef rim)) return LiveryEditResult.Reject("Choose a rim design first.");
            if (!CustomizationCatalogue.FittingDiameters(rim, chassis).Contains(diameterIn))
                return LiveryEditResult.Reject($"{rim.Name} is not available in {diameterIn} in on {CarId} (fits {chassis.Wheels.DiameterIn} in, made in {rim.DiameterIn} in).");
            return Edit("wheels.diameterIn", false, d => d.Wheels.DiameterIn = diameterIn);
        }

        public LiveryEditResult SetRimOffset(int offsetStep)
        {
            if (!chassis.Wheels.OffsetStep.Contains(offsetStep))
                return LiveryEditResult.Reject($"Offset {offsetStep} is outside {chassis.Wheels.OffsetStep.Min} to {chassis.Wheels.OffsetStep.Max} on {CarId}.");
            return Edit("wheels.offsetStep", false, d => d.Wheels.OffsetStep = offsetStep);
        }

        public LiveryEditResult SetRimFinish(string finishId)
        {
            if (!catalogue.TryRimFinish(finishId, out _)) return LiveryEditResult.Reject($"Unknown rim finish '{finishId}'.");
            return Edit("wheels.finish", false, d => d.Wheels.Finish = finishId);
        }

        // ------------------------------------------------------------------ paint

        public enum PaintSlot { Primary = 0, Secondary = 1, Accent = 2 }

        /// <summary>Sets a colour; changing the primary away from the chosen swatch clears the swatch.</summary>
        public LiveryEditResult SetPaintColor(PaintSlot slot, string color, bool continuing = false)
        {
            if (!HexColor.TryNormalize(color, out string c)) return LiveryEditResult.Reject($"'{color}' is not a #RRGGBB color.");
            return Edit("paint." + slot, continuing, d =>
            {
                if (slot == PaintSlot.Primary)
                {
                    d.Paint.Primary = c;
                    if (!SwatchMatches(d.Paint)) d.Paint.Swatch = "";
                }
                else if (slot == PaintSlot.Secondary) d.Paint.Secondary = c;
                else d.Paint.Accent = c;
            });
        }

        public LiveryEditResult SetPaintFinish(string finish)
        {
            if (!catalogue.IsFinish(finish)) return LiveryEditResult.Reject($"Unknown finish '{finish}'.");
            return Edit("paint.finish", false, d => { d.Paint.Finish = finish; if (!SwatchMatches(d.Paint)) d.Paint.Swatch = ""; });
        }

        public LiveryEditResult SetTwoTone(string style)
        {
            if (!chassis.Paint.TwoTone.Contains(style ?? "")) return LiveryEditResult.Reject($"Two-tone '{style}' is not offered on {CarId}.");
            return Edit("paint.twoTone", false, d => d.Paint.TwoTone = style);
        }

        /// <summary>Applies a named swatch to the body (primary colour + finish). Locked swatches can be previewed.</summary>
        public LiveryEditResult ApplySwatch(string swatchId)
        {
            if (!catalogue.TrySwatch(swatchId, out PaintSwatchDef s)) return LiveryEditResult.Reject($"Unknown swatch '{swatchId}'.");
            return Edit("paint.swatch", false, d => { d.Paint.Swatch = s.Id; d.Paint.Primary = s.Color; d.Paint.Finish = s.Finish; });
        }

        bool SwatchMatches(PaintSelection p) =>
            string.IsNullOrEmpty(p.Swatch) || (catalogue.TrySwatch(p.Swatch, out PaintSwatchDef s) && s.Color == p.Primary && s.Finish == p.Finish);

        // ------------------------------------------------------------------ lamps, glass, plate

        public LiveryEditResult SetHeadLamp(string presetId)
        {
            if (!catalogue.TryLamp(presetId, out LampPresetDef l) || !l.Head) return LiveryEditResult.Reject($"'{presetId}' is not a head-lamp preset.");
            return Edit("lamps.head", false, d => d.Lamps.Head = presetId);
        }

        public LiveryEditResult SetTailLamp(string presetId)
        {
            if (!catalogue.TryLamp(presetId, out LampPresetDef l) || !l.Tail) return LiveryEditResult.Reject($"'{presetId}' is not a tail-lamp preset (tail lamps stay red).");
            return Edit("lamps.tail", false, d => d.Lamps.Tail = presetId);
        }

        public LiveryEditResult SetGlass(string tintId)
        {
            if (!catalogue.TryGlass(tintId, out _)) return LiveryEditResult.Reject($"'{tintId}' is not a window tint.");
            return Edit("glass", false, d => d.Glass = tintId);
        }

        /// <summary>Plate text is trimmed and upper-cased; anything outside A–Z, 0–9, space and '-' (or over 8) is refused.</summary>
        public LiveryEditResult SetPlateText(string text, bool continuing = false)
        {
            string t = PlateText.Normalize(text);
            if (!PlateText.IsValid(t, LiveryLimits.MaxPlateLength, allowEmpty: true))
                return LiveryEditResult.Reject($"Plates take up to {LiveryLimits.MaxPlateLength} characters: A–Z, 0–9, space and '-'.");
            return Edit("plate.text", continuing, d => d.Plate.Text = t);
        }

        public LiveryEditResult SetPlateStyle(string styleId)
        {
            if (!catalogue.TryPlateStyle(styleId, out _)) return LiveryEditResult.Reject($"Unknown plate style '{styleId}'.");
            return Edit("plate.style", false, d => d.Plate.Style = styleId);
        }

        // ------------------------------------------------------------------ decals (index 0 = bottom layer)

        /// <summary>Adds a layer on top (centred, default size). Refused at the 64-layer cap.</summary>
        public LiveryEditResult AddDecal(string shapeId, string zone, string color = "#FFFFFF")
        {
            if (!catalogue.TryShape(shapeId, out _)) return LiveryEditResult.Reject($"Unknown decal shape '{shapeId}'.");
            return AddDecal(new DecalLayer { Shape = shapeId, Zone = zone, Color = color });
        }

        /// <summary>Adds a fully specified layer on top (placement values must already be on the livery grid).</summary>
        public LiveryEditResult AddDecal(DecalLayer layer)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (draft.Decals.Count >= LiveryLimits.MaxDecalLayers) return LiveryEditResult.Reject($"All {LiveryLimits.MaxDecalLayers} decal layers are used.");
            string problem = LayerProblem(layer, out DecalLayer clean);
            if (problem != null) return LiveryEditResult.Reject(problem);
            return Edit("decal.add", false, d => d.Decals.Add(clean));
        }

        public LiveryEditResult RemoveDecal(int index)
        {
            if (!HasLayer(index)) return NoLayer(index);
            return Edit("decal.remove", false, d => d.Decals.RemoveAt(index));
        }

        /// <summary>Copies a layer directly above itself. Refused at the 64-layer cap.</summary>
        public LiveryEditResult DuplicateDecal(int index)
        {
            if (!HasLayer(index)) return NoLayer(index);
            if (draft.Decals.Count >= LiveryLimits.MaxDecalLayers) return LiveryEditResult.Reject($"All {LiveryLimits.MaxDecalLayers} decal layers are used.");
            return Edit("decal.duplicate", false, d => d.Decals.Insert(index + 1, d.Decals[index].Clone()));
        }

        /// <summary>Moves a layer in the stack (to = final index; 0 = bottom).</summary>
        public LiveryEditResult ReorderDecal(int from, int to)
        {
            if (!HasLayer(from)) return NoLayer(from);
            if (!HasLayer(to)) return NoLayer(to);
            return Edit("decal.reorder", false, d =>
            {
                DecalLayer x = d.Decals[from];
                d.Decals.RemoveAt(from);
                d.Decals.Insert(to, x);
            });
        }

        /// <summary>Places a layer in its zone; u and v are clamped to 0..1 and snapped to thousandths.</summary>
        public LiveryEditResult MoveDecal(int index, double u, double v, bool continuing = false)
        {
            if (!HasLayer(index)) return NoLayer(index);
            if (!Finite(u) || !Finite(v)) return LiveryEditResult.Reject("Position must be a finite number.");
            int um = DecalLayer.ToMilli(Math.Max(0.0, Math.Min(1.0, u))), vm = DecalLayer.ToMilli(Math.Max(0.0, Math.Min(1.0, v)));
            return Edit(Key("decal.move", index), continuing, d => { d.Decals[index].UMilli = um; d.Decals[index].VMilli = vm; });
        }

        public LiveryEditResult SetDecalZone(int index, string zone)
        {
            if (!HasLayer(index)) return NoLayer(index);
            if (!chassis.Decals.Zones.Contains(zone ?? "")) return LiveryEditResult.Reject($"'{zone}' is not a decal zone on {CarId}.");
            return Edit(Key("decal.zone", index), false, d => d.Decals[index].Zone = zone);
        }

        /// <summary>Size in metres (clamped 0.05..1.6, centimetre steps) and rotation (any whole degrees, normalised to 0..359).</summary>
        public LiveryEditResult TransformDecal(int index, double scale, int rotationDeg, bool continuing = false)
        {
            if (!HasLayer(index)) return NoLayer(index);
            if (!Finite(scale)) return LiveryEditResult.Reject("Scale must be a finite number.");
            int sc = Clamp(DecalLayer.ToCenti(Math.Max(0.0, Math.Min(LiveryLimits.MaxScaleCenti / 100.0, scale))), LiveryLimits.MinScaleCenti, LiveryLimits.MaxScaleCenti);
            int rot = DecalLayer.NormalizeDegrees(rotationDeg);
            return Edit(Key("decal.transform", index), continuing, d => { d.Decals[index].ScaleCenti = sc; d.Decals[index].RotationDeg = rot; });
        }

        public LiveryEditResult SetDecalMirror(int index, bool mirror)
        {
            if (!HasLayer(index)) return NoLayer(index);
            return Edit(Key("decal.mirror", index), false, d => d.Decals[index].Mirror = mirror);
        }

        public LiveryEditResult SetDecalFlip(int index, bool flip)
        {
            if (!HasLayer(index)) return NoLayer(index);
            return Edit(Key("decal.flip", index), false, d => d.Decals[index].Flip = flip);
        }

        public LiveryEditResult RecolorDecal(int index, string color, bool continuing = false)
        {
            if (!HasLayer(index)) return NoLayer(index);
            if (!HexColor.TryNormalize(color, out string c)) return LiveryEditResult.Reject($"'{color}' is not a #RRGGBB color.");
            return Edit(Key("decal.color", index), continuing, d => d.Decals[index].Color = c);
        }

        /// <summary>Opacity clamped to 0.1..1 in hundredths.</summary>
        public LiveryEditResult SetDecalOpacity(int index, double opacity, bool continuing = false)
        {
            if (!HasLayer(index)) return NoLayer(index);
            if (!Finite(opacity)) return LiveryEditResult.Reject("Opacity must be a finite number.");
            int op = Clamp(DecalLayer.ToCenti(Math.Max(0.0, Math.Min(1.0, opacity))), LiveryLimits.MinOpacityPercent, LiveryLimits.MaxOpacityPercent);
            return Edit(Key("decal.opacity", index), continuing, d => d.Decals[index].OpacityPercent = op);
        }

        public LiveryEditResult ClearDecals() => Edit("decal.clear", false, d => d.Decals.Clear());

        // ------------------------------------------------------------------ whole-document

        /// <summary>
        /// Loads a whole livery (e.g. a saved car preset) into the draft as ONE undo step. It must be for this car and pass
        /// Preview validation (locked items allowed for preview).
        /// </summary>
        public LiveryEditResult LoadIntoDraft(LiveryDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            LiveryValidation v = LiveryValidator.Validate(document, catalogue, CarId, null, LiveryValidationMode.Preview);
            if (!v.IsValid) return LiveryEditResult.Reject(string.Join("; ", v.Errors));
            LiveryDocument copy = document.Clone();
            return Edit("load", false, d => Replace(d, copy));
        }

        /// <summary>Resets the draft to the chassis' stock livery (one undo step).</summary>
        public LiveryEditResult ResetToStock()
        {
            LiveryDocument stock = LiveryDocument.Stock(chassis);
            return Edit("stock", false, d => Replace(d, stock));
        }

        public bool Undo()
        {
            if (undo.Count == 0) return false;
            Step s = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            redo.Add(new Step { Before = draft, Key = s.Key });
            draft = s.Before;
            gestureKey = null;
            return true;
        }

        public bool Redo()
        {
            if (redo.Count == 0) return false;
            Step s = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            undo.Add(new Step { Before = draft, Key = s.Key });
            draft = s.Before;
            gestureKey = null;
            return true;
        }

        /// <summary>Ends a running gesture so the next continuing edit starts a new undo step.</summary>
        public void EndGesture() => gestureKey = null;

        public LiveryValidation Validate(CosmeticOwnership owned, LiveryValidationMode mode) =>
            LiveryValidator.Validate(draft, catalogue, CarId, owned, mode);

        /// <summary>The draft as the renderer should show it in the Garage (locked items previewed, invalid ones as stock).</summary>
        public ResolvedAppearance ResolveDraft() => AppearanceResolver.Resolve(catalogue, CarId, draft);

        /// <summary>
        /// Validates the draft in Apply mode (ownership required). On success the draft becomes the applied livery, the undo
        /// history ends, and the new document, canonical JSON and hash are returned. On failure nothing changes.
        /// </summary>
        public LiveryApplyResult Apply(CosmeticOwnership owned)
        {
            LiveryValidation v = Validate(owned, LiveryValidationMode.Apply);
            var result = new LiveryApplyResult { Validation = v };
            if (!v.IsValid) return result;
            applied = draft.Clone();
            ClearHistory();
            result.Applied = true;
            result.Document = applied.Clone();
            result.CanonicalJson = LiveryJson.ToCanonicalJson(applied);
            result.LiveryHash = LiveryHash.Of(applied);
            return result;
        }

        /// <summary>Discards the draft (the applied appearance is restored) and the undo history.</summary>
        public void Cancel()
        {
            draft = applied.Clone();
            ClearHistory();
        }

        // ------------------------------------------------------------------ internals

        LiveryEditResult Edit(string key, bool continuing, Action<LiveryDocument> change)
        {
            LiveryDocument next = draft.Clone();
            change(next);
            if (next.ContentEquals(draft)) return LiveryEditResult.Same;
            bool merge = continuing && gestureKey == key && undo.Count > 0;
            if (!merge)
            {
                undo.Add(new Step { Before = draft, Key = key });
                if (undo.Count > MaxUndoSteps) undo.RemoveAt(0);
            }
            redo.Clear();
            draft = next;
            gestureKey = continuing ? key : null;
            return LiveryEditResult.Done;
        }

        void ClearHistory()
        {
            undo.Clear();
            redo.Clear();
            gestureKey = null;
        }

        static void Replace(LiveryDocument target, LiveryDocument source)
        {
            LiveryDocument s = source.Clone();
            target.Car = s.Car;
            target.Body = s.Body;
            target.Wheels = s.Wheels;
            target.Paint = s.Paint;
            target.Lamps = s.Lamps;
            target.Glass = s.Glass;
            target.Plate = s.Plate;
            target.Decals = s.Decals;
        }

        string LayerProblem(DecalLayer layer, out DecalLayer clean)
        {
            clean = null;
            if (!catalogue.TryShape(layer.Shape, out _)) return $"Unknown decal shape '{layer.Shape}'.";
            if (!chassis.Decals.Zones.Contains(layer.Zone ?? "")) return $"'{layer.Zone}' is not a decal zone on {CarId}.";
            if (!HexColor.TryNormalize(layer.Color, out string c)) return $"'{layer.Color}' is not a #RRGGBB color.";
            if (layer.UMilli < 0 || layer.UMilli > LiveryLimits.UvScale || layer.VMilli < 0 || layer.VMilli > LiveryLimits.UvScale) return "Position must be within the zone (0–1).";
            if (layer.ScaleCenti < LiveryLimits.MinScaleCenti || layer.ScaleCenti > LiveryLimits.MaxScaleCenti)
                return $"Size must be {LiveryJson.Fixed(LiveryLimits.MinScaleCenti, 100, 2)}–{LiveryJson.Fixed(LiveryLimits.MaxScaleCenti, 100, 2)} m.";
            if (layer.RotationDeg < 0 || layer.RotationDeg > LiveryLimits.MaxRotationDeg) return "Rotation must be 0–359 degrees.";
            if (layer.OpacityPercent < LiveryLimits.MinOpacityPercent || layer.OpacityPercent > LiveryLimits.MaxOpacityPercent) return "Opacity must be 0.1–1.";
            clean = layer.Clone();
            clean.Color = c;
            return null;
        }

        bool HasLayer(int index) => index >= 0 && index < draft.Decals.Count;

        static LiveryEditResult NoLayer(int index) => LiveryEditResult.Reject($"There is no decal layer {index.ToString(CultureInfo.InvariantCulture)}.");

        static string Key(string op, int index) => op + ":" + index.ToString(CultureInfo.InvariantCulture);

        static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        static int Clamp(int x, int lo, int hi) => x < lo ? lo : x > hi ? hi : x;
    }
}
