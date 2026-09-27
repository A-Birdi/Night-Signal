using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NightSignal.Art;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Customization;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Garage appearance for one car instance (Addendum 01 §13): body-kit families, wheels, paint, lamps, plate and decal
    /// layers, edited as a DRAFT of the applied livery with undo/redo (Core <see cref="LiveryEditor"/>) and shown live on a
    /// parked car. Locked cosmetics can be tried on; Apply needs them owned and runs where the Garage's decisions happen
    /// (Local: Core in-process, saved atomically; Online: the control plane validates and stores the canonical livery).
    /// Visual presets (5+ per car) keep liveries to load later. Nothing here changes a simulation input.
    /// </summary>
    public sealed class AppearanceScreen : UIScreen
    {
        public override string ScreenName => "Appearance";
        public override string MusicCue => "MUS_MENU_B";

        static readonly string[] Sections = { "Body kit", "Wheels", "Paint", "Lights & plate", "Decals", "Presets" };
        const float Width = 640f;
        const int PresetRows = 6;

        CustomizationCatalogue cat;
        GarageBackend backend;
        string instanceId, carName;
        CarBuildWorkspace ws;
        Action<GarageState> adopt;
        LiveryEditor editor;
        ChassisAppearanceDef chassis;
        AppearanceStage stage;
        RawImage preview;
        bool busy, dirty = true, discardArmed;
        string loadedPresetId = "", pendingToken, pendingTokenFor;
        int section, layer = -1;

        TextMeshProUGUI title, status, message, notes, layerLine;
        Stepper sectionStep, viewStep;
        readonly List<GameObject>[] sectionRows = Enumerable.Range(0, 6).Select(_ => new List<GameObject>()).ToArray();
        readonly Dictionary<string, Stepper> family = new Dictionary<string, Stepper>();
        Stepper rim, diameter, offset, rimFinish, swatch, finish, twoTone, secondary, accent, head, tail, glass, plateStyle;
        Stepper shape, zone, decalColor, opacity;
        TMP_InputField plateText, presetName;
        Button undo, redo, stock, cancel, apply;
        readonly List<(Button Load, Button Save)> presetButtons = new List<(Button, Button)>();

        // Option lists for the current car (ids; labels come from the catalogue).
        List<string> rims = new List<string>(), rimFinishes = new List<string>(), swatches = new List<string>(), finishes = new List<string>(),
            twoTones = new List<string>(), heads = new List<string>(), tails = new List<string>(), glasses = new List<string>(),
            plates = new List<string>(), shapes = new List<string>(), zones = new List<string>();
        List<int> diameters = new List<int>(), offsets = new List<int>();
        List<(string Name, string Hex)> colours = new List<(string, string)>();
        HashSet<string> owned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The Core editor on screen (tours drive it through the same calls the controls make).</summary>
        public LiveryEditor Editor => editor;
        public string Message => message != null ? message.text : "";
        public bool Busy => busy;
        public CarBuildWorkspace Workspace => ws;
        public string PreviewDebug => stage?.Describe() ?? "no stage";
        public void SavePreview(string path) => stage?.SaveTexture(path);

        /// <summary>Opens the appearance of the Garage's current car. <paramref name="adoptState"/> hands every answered car back to the Garage.</summary>
        public void Open(GarageBackend garage, string carInstanceId, string name, CarBuildWorkspace workspace, Action<GarageState> adoptState)
        {
            backend = garage;
            instanceId = carInstanceId;
            carName = name;
            ws = workspace;
            adopt = adoptState;
            loadedPresetId = ws?.AppliedVisualPresetId ?? "";
            section = 0;
            layer = -1;
            pendingToken = null;
            discardArmed = false;
            editor = null;
        }

        protected override void OnBuild(RectTransform root)
        {
            cat = ContentLibrary.Load()?.Customization;
            UIFactory.Panel("Backdrop", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.035f, 0.04f, 0.05f, 0.94f));

            Image left = UIFactory.Panel("Controls", root, new Vector2(0, 0), new Vector2(0.36f, 1), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.96f));
            RectTransform col = UIFactory.Column("AppearanceColumn", left.transform, new Vector2(0, 0.02f), new Vector2(1, 0.96f), new Vector2(36, 0), new Vector2(-16, 0), 6f);
            title = UIFactory.Row("Heading", col, "APPEARANCE", SignalTheme.Heading, SignalTheme.Label, Width, 0, true);
            status = UIFactory.Row("AppearanceStatus", col, "", SignalTheme.Small, SignalTheme.LabelDim, Width, 48);
            status.richText = true;
            sectionStep = new Stepper(col, "Section", Sections.Length, i => Sections[i], 0, Width, 0.2f);
            sectionStep.Changed += i => { section = i; dirty = true; };

            // Body kit: one row per family (only the variants this chassis offers).
            foreach (string f in AppearanceVocabulary.Families)
            {
                string captured = f;
                var s = new Stepper(col, FamilyLabel(f), 1, i => Pretty(Option(captured, i)), 0, Width, 0.2f);
                s.Changed += i => Edit(editor.SetVariant(captured, Option(captured, i)));
                family[f] = s;
                sectionRows[0].Add(s.Root);
            }

            // Wheels.
            rim = Row(1, col, "Rim", () => rims.Count, i => RimLabel(rims.ElementAtOrDefault(i)), i => Edit(editor.SetRim(rims[i])));
            diameter = Row(1, col, "Diameter", () => diameters.Count, i => i < diameters.Count ? diameters[i] + " in" : "—", i => Edit(editor.SetRimDiameter(diameters[i])));
            offset = Row(1, col, "Offset", () => offsets.Count, i => i < offsets.Count ? OffsetLabel(offsets[i]) : "—", i => Edit(editor.SetRimOffset(offsets[i])));
            rimFinish = Row(1, col, "Rim finish", () => rimFinishes.Count, i => cat.TryRimFinish(rimFinishes.ElementAtOrDefault(i), out RimFinishDef d) ? d.Name : "—",
                i => Edit(editor.SetRimFinish(rimFinishes[i])));

            // Paint.
            swatch = Row(2, col, "Colour", () => swatches.Count, i => SwatchLabel(swatches.ElementAtOrDefault(i)), i => Edit(editor.ApplySwatch(swatches[i])));
            finish = Row(2, col, "Finish", () => finishes.Count, i => Pretty(finishes.ElementAtOrDefault(i)), i => Edit(editor.SetPaintFinish(finishes[i])));
            twoTone = Row(2, col, "Two-tone", () => twoTones.Count, i => cat.TryTwoTone(twoTones.ElementAtOrDefault(i), out TwoToneDef t) ? t.Name : Pretty(twoTones.ElementAtOrDefault(i)),
                i => Edit(editor.SetTwoTone(twoTones[i])));
            secondary = Row(2, col, "Second colour", () => colours.Count, i => colours.ElementAtOrDefault(i).Name ?? "—",
                i => Edit(editor.SetPaintColor(LiveryEditor.PaintSlot.Secondary, colours[i].Hex)));
            accent = Row(2, col, "Accent", () => colours.Count, i => colours.ElementAtOrDefault(i).Name ?? "—",
                i => Edit(editor.SetPaintColor(LiveryEditor.PaintSlot.Accent, colours[i].Hex)));

            // Lights and plate.
            head = Row(3, col, "Headlamps", () => heads.Count, i => cat.TryLamp(heads.ElementAtOrDefault(i), out LampPresetDef l) ? l.Name : "—", i => Edit(editor.SetHeadLamp(heads[i])));
            tail = Row(3, col, "Tail lamps", () => tails.Count, i => cat.TryLamp(tails.ElementAtOrDefault(i), out LampPresetDef l) ? l.Name : "—", i => Edit(editor.SetTailLamp(tails[i])));
            glass = Row(3, col, "Glass", () => glasses.Count, i => cat.TryGlass(glasses.ElementAtOrDefault(i), out GlassTintDef g) ? g.Name : "—", i => Edit(editor.SetGlass(glasses[i])));
            plateStyle = Row(3, col, "Plate", () => plates.Count, i => cat.TryPlateStyle(plates.ElementAtOrDefault(i), out PlateStyleDef p) ? p.Name : "—",
                i => Edit(editor.SetPlateStyle(plates[i])));
            plateText = UIFactory.InputField("Appearance-PlateText", col, "Plate text (up to 8 letters and digits)", false, LiveryLimits.MaxPlateLength, Width, 50);
            plateText.onEndEdit.AddListener(t => Edit(editor.SetPlateText(t ?? "")));
            sectionRows[3].Add(plateText.gameObject);

            // Decals: pick a layer, then move/size/turn/recolour it; add new layers from the library.
            layerLine = UIFactory.Row("DecalLayer", col, "", SignalTheme.Small, SignalTheme.Label, Width, 30);
            layerLine.richText = true;
            sectionRows[4].Add(layerLine.gameObject);
            AddButtons(4, col, ("Appearance-LayerPrev", "‹ Layer", () => SelectLayer(layer - 1)), ("Appearance-LayerNext", "Layer ›", () => SelectLayer(layer + 1)),
                ("Appearance-LayerUp", "Bring up", () => { if (layer >= 0 && Edit(editor.ReorderDecal(layer, layer + 1))) layer = Mathf.Min(layer + 1, editor.DecalCount - 1); }));
            shape = Row(4, col, "Shape", () => shapes.Count, i => ShapeLabel(shapes.ElementAtOrDefault(i)), null);
            zone = Row(4, col, "Zone", () => zones.Count, i => Pretty(zones.ElementAtOrDefault(i)), i => { if (layer >= 0) Edit(editor.SetDecalZone(layer, zones[i])); });
            AddButtons(4, col, ("Appearance-AddDecal", "Add layer", AddDecal), ("Appearance-DuplicateDecal", "Duplicate", () => { if (layer >= 0 && Edit(editor.DuplicateDecal(layer))) layer = editor.DecalCount - 1; }),
                ("Appearance-RemoveDecal", "Remove", () => { if (layer >= 0 && Edit(editor.RemoveDecal(layer))) layer = Mathf.Min(layer, editor.DecalCount - 1); }));
            AddButtons(4, col, ("Appearance-MoveLeft", "Move left", () => Nudge(-0.05, 0)), ("Appearance-MoveRight", "Move right", () => Nudge(0.05, 0)),
                ("Appearance-MoveUp", "Move up", () => Nudge(0, 0.05)), ("Appearance-MoveDown", "Move down", () => Nudge(0, -0.05)));
            AddButtons(4, col, ("Appearance-Smaller", "Smaller", () => Resize(-0.05, 0)), ("Appearance-Larger", "Larger", () => Resize(0.05, 0)),
                ("Appearance-TurnLeft", "Turn −15°", () => Resize(0, -15)), ("Appearance-TurnRight", "Turn +15°", () => Resize(0, 15)));
            AddButtons(4, col, ("Appearance-Mirror", "Mirror to other side", () => { DecalLayer d = Layer(); if (d != null) Edit(editor.SetDecalMirror(layer, !d.Mirror)); }),
                ("Appearance-Flip", "Flip", () => { DecalLayer d = Layer(); if (d != null) Edit(editor.SetDecalFlip(layer, !d.Flip)); }));
            decalColor = Row(4, col, "Layer colour", () => colours.Count, i => colours.ElementAtOrDefault(i).Name ?? "—", i => { if (layer >= 0) Edit(editor.RecolorDecal(layer, colours[i].Hex)); });
            opacity = Row(4, col, "Opacity", () => 10, i => ((i + 1) * 10) + "%", i => { if (layer >= 0) Edit(editor.SetDecalOpacity(layer, (i + 1) / 10.0)); });

            // Presets: at least five liveries per car.
            for (int i = 0; i < PresetRows; i++)
            {
                int index = i;
                RectTransform row = UIFactory.Rect("PresetRow" + i, col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
                row.sizeDelta = new Vector2(Width, 42);
                Button load = UIFactory.Button("Appearance-PresetLoad" + i, row, "", () => LoadPreset(index), Width - 190, 40);
                Button save = UIFactory.Button("Appearance-PresetSave" + i, row, "Save here", () => SavePreset(index), 182, 40);
                Place(load, 0);
                Place(save, Width - 182);
                presetButtons.Add((load, save));
                sectionRows[5].Add(row.gameObject);
            }
            presetName = UIFactory.InputField("Appearance-PresetName", col, "Name for a new preset", false, 32, Width, 48);
            sectionRows[5].Add(presetName.gameObject);

            // Actions.
            RectTransform actions = UIFactory.Rect("AppearanceActions", col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            actions.sizeDelta = new Vector2(Width, 46);
            undo = UIFactory.Button("Appearance-Undo", actions, "Undo", () => { if (editor != null && editor.Undo()) Changed(); }, 150, 44);
            redo = UIFactory.Button("Appearance-Redo", actions, "Redo", () => { if (editor != null && editor.Redo()) Changed(); }, 150, 44);
            stock = UIFactory.Button("Appearance-Stock", actions, "Stock", () => Edit(editor.ResetToStock()), 150, 44);
            cancel = UIFactory.Button("Appearance-Cancel", actions, "Cancel", CancelDraft, 166, 44);
            Place(undo, 0);
            Place(redo, 158);
            Place(stock, 316);
            Place(cancel, 474);
            apply = UIFactory.Button("Appearance-Apply", col, "Apply appearance", Apply, Width, 50);
            message = UIFactory.Row("AppearanceMessage", col, "", SignalTheme.Small, SignalTheme.Caution, Width, 64);
            message.richText = false;
            UIFactory.Button("Back", col, "Back to Garage", () => App.Router.Back(), 300, 42);

            // Right: the live preview.
            var previewGo = new GameObject("AppearancePreview", typeof(RectTransform), typeof(RawImage));
            var prt = (RectTransform)previewGo.transform;
            prt.SetParent(root, false);
            prt.anchorMin = new Vector2(0.38f, 0.2f);
            prt.anchorMax = new Vector2(0.99f, 0.96f);
            prt.offsetMin = prt.offsetMax = Vector2.zero;
            preview = previewGo.GetComponent<RawImage>();
            preview.color = Color.white;
            RectTransform rcol = UIFactory.Column("PreviewColumn", root, new Vector2(0.38f, 0.02f), new Vector2(0.99f, 0.19f), Vector2.zero, Vector2.zero, 6f);
            viewStep = new Stepper(rcol, "View", AppearanceStage.Views.Length, i => AppearanceStage.Views[i].Name, 0, 640, 0.18f);
            viewStep.Changed += i => stage?.SetView(i);
            notes = UIFactory.Row("AppearanceNotes", rcol, "", SignalTheme.Small, SignalTheme.LabelDim, 1100, 90);
            notes.richText = true;
        }

        Stepper Row(int sectionIndex, Transform col, string label, Func<int> count, Func<int, string> format, Action<int> changed)
        {
            var s = new Stepper(col, label, 1, format, 0, Width, 0.26f);
            if (changed != null) s.Changed += i => { if (editor != null && i < count()) changed(i); };
            sectionRows[sectionIndex].Add(s.Root);
            return s;
        }

        void AddButtons(int sectionIndex, Transform col, params (string Name, string Label, Action Click)[] buttons)
        {
            RectTransform row = UIFactory.Rect("Buttons" + buttons[0].Name, col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            row.sizeDelta = new Vector2(Width, 44);
            float w = (Width - 8f * (buttons.Length - 1)) / buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                (string n, string l, Action a) = buttons[i];
                Button b = UIFactory.Button(n, row, l, () => { if (editor != null && !busy) { a(); dirty = true; } }, w, 42);
                Place(b, i * (w + 8f));
            }
            sectionRows[sectionIndex].Add(row.gameObject);
        }

        static void Place(Button btn, float x)
        {
            var r = (RectTransform)btn.transform;
            r.anchorMin = r.anchorMax = new Vector2(0, 0.5f);
            r.pivot = new Vector2(0, 0.5f);
            r.anchoredPosition = new Vector2(x, 0);
            btn.GetComponentInChildren<TextMeshProUGUI>().fontSize = SignalTheme.Small * SignalTheme.TextScale;
        }

        public override void OnShow()
        {
            if (cat == null || backend == null || ws == null)
            {
                App.Router.Back();
                return;
            }
            owned = new HashSet<string>(backend.OwnedCosmetics(), StringComparer.Ordinal);
            if (stage == null)
            {
                // The texture takes the preview's on-screen proportions, so the car is never stretched.
                Rect r = preview.rectTransform.rect;
                float aspect = r.width > 1f && r.height > 1f ? r.width / r.height : 1.6f;
                stage = new AppearanceStage(1280, Mathf.RoundToInt(1280f / aspect));
            }
            preview.texture = stage.Texture;
            if (editor == null) Reset(ws);
            sectionStep.Set(section);
            viewStep.Set(stage.ViewIndex);
            Changed();
        }

        public override void OnHide()
        {
            stage?.Dispose();
            stage = null;
        }

        /// <summary>Back with unapplied changes asks once: a second Back discards the draft.</summary>
        public override bool OnBack()
        {
            if (editor == null || !editor.IsDirty || discardArmed) return false;
            discardArmed = true;
            message.text = "The appearance draft is not applied — press Back again to leave it, or Apply.";
            return true;
        }

        public override void Tick()
        {
            stage?.Update();
            if (!dirty) return;
            dirty = false;
            Render();
        }

        /// <summary>A fresh editor on the car's applied livery (after opening, applying or when the server answers).</summary>
        void Reset(CarBuildWorkspace workspace)
        {
            ws = workspace;
            string carId = ws.Car.ModelId;
            chassis = cat.ChassisFor(carId);
            LiveryDocument applied = null;
            if (!string.IsNullOrEmpty(ws.AppliedLivery))
            {
                LiveryParseResult p = LiveryJson.Parse(ws.AppliedLivery);
                if (p.Ok && p.Document.Car == carId) applied = p.Document;
                else message.text = "The stored appearance could not be read; showing stock.";
            }
            editor = new LiveryEditor(cat, carId, applied);
            layer = Mathf.Min(layer, editor.DecalCount - 1);

            rims = cat.RimDesigns.Select(r => r.Id).ToList();
            rimFinishes = cat.RimFinishes.Select(r => r.Id).ToList();
            swatches = cat.PaintSwatches.Select(s => s.Id).ToList();
            finishes = cat.PaintFinishes.Select(f => f.Id).ToList();
            twoTones = chassis.Paint.TwoTone.ToList();
            heads = cat.LampPresets.Where(l => l.Head).Select(l => l.Id).ToList();
            tails = cat.LampPresets.Where(l => l.Tail).Select(l => l.Id).ToList();
            glasses = cat.GlassTints.Select(g => g.Id).ToList();
            plates = cat.PlateStyles.Select(p => p.Id).ToList();
            shapes = cat.DecalShapes.Select(s => s.Id).ToList();
            zones = chassis.Decals.Zones.ToList();
            offsets = Enumerable.Range(chassis.Wheels.OffsetStep.Min, chassis.Wheels.OffsetStep.Max - chassis.Wheels.OffsetStep.Min + 1).ToList();
            colours = new List<(string, string)> { ("White", "#FFFFFF"), ("Black", "#111111") };
            foreach (PaintSwatchDef s in cat.PaintSwatches)
                if (!colours.Any(c => string.Equals(c.Hex, s.Color, StringComparison.OrdinalIgnoreCase))) colours.Add((s.Name, s.Color));
            foreach (string f in AppearanceVocabulary.Families) family[f].SetCount(chassis.Families.Of(f).Count);
            rim.SetCount(rims.Count);
            offset.SetCount(offsets.Count);
            rimFinish.SetCount(rimFinishes.Count);
            swatch.SetCount(swatches.Count);
            finish.SetCount(finishes.Count);
            twoTone.SetCount(twoTones.Count);
            secondary.SetCount(colours.Count);
            accent.SetCount(colours.Count);
            decalColor.SetCount(colours.Count);
            head.SetCount(heads.Count);
            tail.SetCount(tails.Count);
            glass.SetCount(glasses.Count);
            plateStyle.SetCount(plates.Count);
            shape.SetCount(shapes.Count);
            zone.SetCount(zones.Count);
            opacity.SetCount(10);
        }

        string Option(string familyId, int i) => chassis == null ? AppearanceVocabulary.Stock : chassis.Families.Of(familyId).ElementAtOrDefault(i) ?? AppearanceVocabulary.Stock;

        // ------------------------------------------------------------------ editing

        /// <summary>Shows an editor answer: a refusal explains itself; any change re-renders the car.</summary>
        bool Edit(LiveryEditResult r)
        {
            if (r == null) return false;
            if (r.Status == LiveryEditStatus.Rejected) message.text = r.Reason;
            if (r.Changed) Changed();
            dirty = true;
            return r.Changed;
        }

        void Changed()
        {
            if (editor == null) return;
            discardArmed = false;
            pendingToken = null;
            message.text = "";
            if (stage != null) stage.Show(editor.CarId, AppearanceMapping.From(editor.ResolveDraft()));
            dirty = true;
        }

        DecalLayer Layer() => editor != null && layer >= 0 && layer < editor.DecalCount ? editor.Draft.Decals[layer] : null;

        void SelectLayer(int index)
        {
            if (editor == null || editor.DecalCount == 0) { layer = -1; return; }
            layer = ((index % editor.DecalCount) + editor.DecalCount) % editor.DecalCount;
        }

        void AddDecal()
        {
            if (shapes.Count == 0 || zones.Count == 0) return;
            string colour = colours.ElementAtOrDefault(decalColor.Index).Hex ?? "#FFFFFF";
            if (Edit(editor.AddDecal(shapes[shape.Index], zones[zone.Index], colour))) layer = editor.DecalCount - 1;
        }

        void Nudge(double du, double dv)
        {
            DecalLayer d = Layer();
            if (d == null) { message.text = "Pick a layer first (add one from the library)."; return; }
            Edit(editor.MoveDecal(layer, Clamp01(d.U + du), Clamp01(d.V + dv)));
        }

        void Resize(double dScale, int dRotation)
        {
            DecalLayer d = Layer();
            if (d == null) { message.text = "Pick a layer first (add one from the library)."; return; }
            double scale = Math.Max(LiveryLimits.MinScaleCenti / 100.0, Math.Min(LiveryLimits.MaxScaleCenti / 100.0, d.Scale + dScale));
            Edit(editor.TransformDecal(layer, scale, ((d.RotationDeg + dRotation) % 360 + 360) % 360));
        }

        static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

        void CancelDraft()
        {
            if (editor == null) return;
            editor.Cancel();
            loadedPresetId = ws.AppliedVisualPresetId ?? "";
            layer = Mathf.Min(layer, editor.DecalCount - 1);
            Changed();
            message.text = "Draft cleared — showing the applied appearance.";
        }

        /// <summary>Validates with ownership first; the Garage backend decides and stores (the editor restarts on its answer).</summary>
        public void Apply()
        {
            if (editor == null || busy) return;
            string mismatch = backend.Online ? OnlineSession.Current?.CustomizationMismatch : null;
            if (mismatch != null)
            {
                message.text = mismatch;
                return;
            }
            if (!editor.IsDirty && (ws.AppliedLivery ?? "") != "")
            {
                message.text = "Nothing to apply — the car already looks like this.";
                return;
            }
            LiveryValidation v = editor.Validate(CosmeticOwnership.FromIds(owned), LiveryValidationMode.Apply);
            if (!v.IsValid)
            {
                message.text = v.Locked.Count > 0
                    ? "Not owned yet: " + string.Join(", ", v.Locked.Select(l => l.Name).Distinct()) + ". Remove them or earn them to apply."
                    : string.Join(" ", v.Errors.Take(2));
                dirty = true;
                return;
            }
            LiveryDocument draft = editor.Draft;
            string presetId = PresetMatching(draft);
            Run(new GarageOp { Kind = "livery-apply", LiveryJson = LiveryJson.ToCanonicalJson(draft), PresetId = presetId }, "Appearance applied — it shows in your next race.", true);
        }

        /// <summary>The preset the draft came from when it still matches it exactly ("" otherwise).</summary>
        string PresetMatching(LiveryDocument draft)
        {
            VisualPreset p = ws.VisualPresets.FirstOrDefault(x => x.PresetId == loadedPresetId);
            if (p == null) return "";
            LiveryParseResult r = LiveryPresets.Read(p);
            return r.Ok && r.Document.ContentEquals(draft) ? p.PresetId : "";
        }

        void Run(GarageOp op, string ok, bool restartEditor, Action<GarageAnswer> after = null)
        {
            busy = true;
            dirty = true;
            backend.Run(instanceId, ws, op, a =>
            {
                busy = false;
                if (a.State != null)
                {
                    ws = a.State.Workspace;
                    adopt?.Invoke(a.State);
                }
                if (a.Accepted && restartEditor)
                {
                    Reset(ws);
                    Changed();
                }
                message.text = a.Accepted ? ok : a.Message;
                after?.Invoke(a);
                dirty = true;
            });
        }

        // ------------------------------------------------------------------ presets

        List<VisualPreset> Liveries() => ws.VisualPresets.Where(p => p.PayloadSchema == LiveryDocument.SchemaId).ToList();

        public void LoadPreset(int index)
        {
            List<VisualPreset> list = Liveries();
            if (editor == null || busy || index >= list.Count) return;
            LiveryParseResult r = LiveryPresets.Read(list[index]);
            if (!r.Ok || r.Document.Car != editor.CarId)
            {
                message.text = r.Ok ? $"\"{list[index].Name}\" is for another car." : "That preset could not be read.";
                return;
            }
            if (Edit(editor.LoadIntoDraft(r.Document)) || editor.Draft.ContentEquals(r.Document))
            {
                loadedPresetId = list[index].PresetId;
                layer = Mathf.Min(layer, editor.DecalCount - 1);
                message.text = $"Loaded \"{list[index].Name}\" into the draft — Apply to use it.";
            }
        }

        /// <summary>Saves the draft into slot <paramref name="index"/>: a new preset in an empty slot, or over an existing one (confirmed).</summary>
        public void SavePreset(int index)
        {
            List<VisualPreset> list = Liveries();
            if (editor == null || busy) return;
            LiveryValidation v = editor.Validate(null, LiveryValidationMode.Preview);
            if (!v.IsValid)
            {
                message.text = string.Join(" ", v.Errors.Take(2));
                return;
            }
            string json = LiveryJson.ToCanonicalJson(editor.Draft);
            if (index < list.Count)
            {
                VisualPreset p = list[index];
                string token = pendingTokenFor == p.PresetId ? pendingToken : null;
                Run(new GarageOp { Kind = "visual-preset-update", PresetId = p.PresetId, PayloadSchema = LiveryDocument.SchemaId, PayloadJson = json, ConfirmationToken = token },
                    $"Saved the draft over \"{p.Name}\".", false, a =>
                    {
                        if (a.ConfirmationRequired)
                        {
                            pendingToken = a.ConfirmationToken;
                            pendingTokenFor = p.PresetId;
                            message.text = $"Replace \"{p.Name}\" with this draft? Press Save here again to confirm.";
                        }
                        else pendingToken = null;
                        if (a.Accepted) loadedPresetId = p.PresetId;
                    });
                return;
            }
            string name = string.IsNullOrWhiteSpace(presetName.text) ? NextName(list) : presetName.text.Trim();
            Run(new GarageOp { Kind = "visual-preset-save", Name = name, PayloadSchema = LiveryDocument.SchemaId, PayloadJson = json },
                $"Saved preset \"{name}\".", false, a =>
                {
                    if (!a.Accepted) return;
                    presetName.text = "";
                    loadedPresetId = Liveries().FirstOrDefault(x => x.Name == name)?.PresetId ?? loadedPresetId;
                });
        }

        static string NextName(List<VisualPreset> list)
        {
            for (int n = list.Count + 1; ; n++)
            {
                string name = "Livery " + n.ToString(CultureInfo.InvariantCulture);
                if (!list.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
            }
        }

        // ------------------------------------------------------------------ render

        void Render()
        {
            foreach (List<GameObject> rows in sectionRows)
                foreach (GameObject g in rows) g.SetActive(false);
            if (editor == null) return;
            foreach (GameObject g in sectionRows[section]) g.SetActive(true);

            LiveryDocument d = editor.Draft;
            title.text = "APPEARANCE";
            bool storedStock = string.IsNullOrEmpty(ws.AppliedLivery);
            string appliedName = ws.VisualPresets.FirstOrDefault(p => p.PresetId == ws.AppliedVisualPresetId)?.Name;
            status.text = $"{Esc(carName)}   <color=#9A968D>applied: {(storedStock ? "stock" : appliedName != null ? "“" + Esc(appliedName) + "”" : "custom")}</color>\n" +
                          (editor.IsDirty ? "<color=#F2A541>Draft differs from the applied appearance</color>" : "<color=#9A968D>No unapplied changes</color>") +
                          $"   <color=#9A968D>undo {editor.UndoCount} · redo {editor.RedoCount} · {editor.DecalCount}/{LiveryLimits.MaxDecalLayers} layers</color>";

            foreach (string f in AppearanceVocabulary.Families) family[f].Set(Mathf.Max(0, chassis.Families.Of(f).ToList().IndexOf(d.Body.Get(f))));
            rim.Set(Mathf.Max(0, rims.IndexOf(d.Wheels.Rim)));
            RimDesignDef rd = cat.TryRim(d.Wheels.Rim, out RimDesignDef r) ? r : null;
            int lo = Math.Max(chassis.Wheels.DiameterIn.Min, rd?.DiameterIn.Min ?? chassis.Wheels.DiameterIn.Min);
            int hi = Math.Min(chassis.Wheels.DiameterIn.Max, rd?.DiameterIn.Max ?? chassis.Wheels.DiameterIn.Max);
            diameters = hi >= lo ? Enumerable.Range(lo, hi - lo + 1).ToList() : new List<int> { d.Wheels.DiameterIn };
            if (!diameters.Contains(d.Wheels.DiameterIn)) diameters = diameters.Concat(new[] { d.Wheels.DiameterIn }).OrderBy(x => x).ToList();
            diameter.SetCount(diameters.Count);
            diameter.Set(Mathf.Max(0, diameters.IndexOf(d.Wheels.DiameterIn)));
            offset.Set(Mathf.Max(0, offsets.IndexOf(d.Wheels.OffsetStep)));
            rimFinish.Set(Mathf.Max(0, rimFinishes.IndexOf(d.Wheels.Finish)));
            swatch.Set(Mathf.Max(0, swatches.IndexOf(d.Paint.Swatch)));
            finish.Set(Mathf.Max(0, finishes.IndexOf(d.Paint.Finish)));
            twoTone.Set(Mathf.Max(0, twoTones.IndexOf(d.Paint.TwoTone)));
            secondary.Set(Mathf.Max(0, colours.FindIndex(c => string.Equals(c.Hex, d.Paint.Secondary, StringComparison.OrdinalIgnoreCase))));
            accent.Set(Mathf.Max(0, colours.FindIndex(c => string.Equals(c.Hex, d.Paint.Accent, StringComparison.OrdinalIgnoreCase))));
            head.Set(Mathf.Max(0, heads.IndexOf(d.Lamps.Head)));
            tail.Set(Mathf.Max(0, tails.IndexOf(d.Lamps.Tail)));
            glass.Set(Mathf.Max(0, glasses.IndexOf(d.Glass)));
            plateStyle.Set(Mathf.Max(0, plates.IndexOf(d.Plate.Style)));
            if (!plateText.isFocused && plateText.text != d.Plate.Text) plateText.SetTextWithoutNotify(d.Plate.Text);

            layer = editor.DecalCount == 0 ? -1 : Mathf.Clamp(layer, 0, editor.DecalCount - 1);
            DecalLayer l = Layer();
            layerLine.text = l == null
                ? "<color=#9A968D>No decal layers yet — choose a shape and zone, then Add layer.</color>"
                : $"Layer {layer + 1} of {editor.DecalCount}: {Esc(ShapeLabel(l.Shape))} on {Pretty(l.Zone)}  <color=#9A968D>" +
                  $"at {l.U:0.00}, {l.V:0.00} · {l.Scale:0.00} m · {l.RotationDeg}°{(l.Mirror ? " · mirrored" : "")}{(l.Flip ? " · flipped" : "")}</color>";
            if (l != null)
            {
                zone.Set(Mathf.Max(0, zones.IndexOf(l.Zone)));
                decalColor.Set(Mathf.Max(0, colours.FindIndex(c => string.Equals(c.Hex, l.Color, StringComparison.OrdinalIgnoreCase))));
                opacity.Set(Mathf.Clamp(l.OpacityPercent / 10 - 1, 0, 9));
            }

            List<VisualPreset> list = Liveries();
            int capacity = Math.Max(CarBuildWorkspace.MinVisualPresetSlots, ws.VisualPresetCapacity);
            for (int i = 0; i < presetButtons.Count; i++)
            {
                (Button load, Button save) = presetButtons[i];
                bool used = i < list.Count, open = i < capacity;
                load.transform.parent.gameObject.SetActive(section == 5 && open);
                load.interactable = used && !busy;
                save.interactable = !busy && (used || i == list.Count);
                string name = used ? list[i].Name : "(empty)";
                string mark = used && list[i].PresetId == ws.AppliedVisualPresetId ? "  <color=#3EC6D8>applied</color>" : "";
                TextMeshProUGUI t = load.GetComponentInChildren<TextMeshProUGUI>();
                t.richText = true;
                t.text = $"{i + 1}. {Esc(name)}{mark}";
            }

            undo.interactable = editor.CanUndo && !busy;
            redo.interactable = editor.CanRedo && !busy;
            cancel.interactable = editor.IsDirty && !busy;
            apply.interactable = !busy;

            LiveryValidation locked = editor.Validate(CosmeticOwnership.FromIds(owned), LiveryValidationMode.Apply);
            string note = section == 0 ? string.Join("  ", AppearanceVocabulary.Families.Select(f => chassis.Note(f)).Where(n => !string.IsNullOrEmpty(n)))
                : section == 1 ? chassis.Note("wheels") : section == 2 ? chassis.Note("paint") : section == 4 ? chassis.Note("decals") : null;
            notes.text = (locked.Locked.Count > 0 ? $"<color=#F2A541>Trying on items you do not own yet: {Esc(string.Join(", ", locked.Locked.Select(x => x.Name).Distinct()))}.</color>\n" : "") +
                         (string.IsNullOrEmpty(note) ? "" : Esc(note)) +
                         "\n<color=#9A968D>Appearance never changes how the car drives.</color>";
        }

        // ------------------------------------------------------------------ labels

        static string FamilyLabel(string f) =>
            f == AppearanceVocabulary.Front ? "Front" : f == AppearanceVocabulary.Rear ? "Rear" : f == AppearanceVocabulary.Side ? "Sides"
            : f == AppearanceVocabulary.RearAero ? "Rear aero" : "Exhaust";

        static string Pretty(string id)
        {
            if (string.IsNullOrEmpty(id)) return "—";
            if (id == "gt-wing") return "GT wing";
            string s = id.Replace('-', ' ');
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        string RimLabel(string id) => cat.TryRim(id, out RimDesignDef r) ? r.Name : "—";
        string OffsetLabel(int step) => step == 0 ? "Stock" : $"{(step > 0 ? "+" : "−")}{Math.Abs(step) * WheelFitment.OffsetStepMm} mm {(step > 0 ? "out" : "in")}";

        string SwatchLabel(string id) => cat.TrySwatch(id, out PaintSwatchDef s) ? s.Name + Lock(s.CosmeticId) : "—";
        string ShapeLabel(string id) => cat.TryShape(id, out DecalShapeDef s) ? s.Name + Lock(s.CosmeticId) : "—";
        string Lock(string cosmeticId) => string.IsNullOrEmpty(cosmeticId) || owned.Contains(cosmeticId) ? "" : "  (locked)";

        static string Esc(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");
    }
}
