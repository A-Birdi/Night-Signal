using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NightSignal.Core.Builds
{
    public enum OpStatus
    {
        Ok = 0,
        /// <summary>The caller's expected revision is not the current one (another device changed this car). Reload.</summary>
        StaleRevision = 1,
        NotFound = 2,
        /// <summary>Repeat the call with <see cref="OperationResult.ConfirmationToken"/> to proceed.</summary>
        ConfirmationRequired = 3,
        InvalidName = 4,
        DuplicateName = 5,
        CapacityFull = 6,
        PinLimit = 7,
        /// <summary>See <see cref="OperationResult.Repairs"/> for the exact list. Nothing changed.</summary>
        NeedsRepair = 8,
        BuildLocked = 9,
        NoDraft = 10,
        Rejected = 11,
    }

    /// <summary>Which kind of driving began. Only a full-size authorized event may record Last Race Build.</summary>
    public enum DrivingSessionKind
    {
        FullSizeEvent = 0,
        TestYard = 1,
        SlotCarToy = 2,
        OtherToy = 3,
        MenuPreview = 4,
    }

    public sealed class OperationResult
    {
        public OpStatus Status;
        public bool Accepted => Status == OpStatus.Ok;
        public string Message = "";
        public List<RepairItem> Repairs = new List<RepairItem>();
        /// <summary>Workspace revision after the call (unchanged unless accepted with changes).</summary>
        public long Revision;
        public string ConfirmationToken;
        public string LoadoutId;
        public BuildEvaluation Evaluation;
        public BuildComparison Comparison;
        /// <summary>The applied build's physics changed: invalidate THIS player's Event Ready only.</summary>
        public bool PerformanceChanged;
        public List<string> Changes = new List<string>();

        public override string ToString() => $"{Status}: {Message}";
    }

    public sealed class DraftSource
    {
        /// <summary>applied | loadout | reference</summary>
        public string Kind = "applied";
        public string Id = "";

        public static DraftSource Applied() => new DraftSource { Kind = "applied" };
        public static DraftSource Loadout(string id) => new DraftSource { Kind = "loadout", Id = id };
        public static DraftSource Reference(BuildReferenceKind k) => new DraftSource { Kind = "reference", Id = BuildReferenceKinds.Id(k) };

        public string Key => Kind + ":" + Id;
    }

    /// <summary>
    /// Garage operations on one car instance's <see cref="CarBuildWorkspace"/> (Addendum 02 §9.2–9.3). Every operation
    /// validates first and then mutates in place all-or-nothing; a rejected call leaves the workspace untouched. User
    /// mutations quote the expected <see cref="CarBuildWorkspace.Revision"/> (optimistic concurrency). No operation touches
    /// the wallet or ownership; the mechanical operations never touch visual presets or the livery, and the visual preset and
    /// livery operations never touch the mechanical applied build (or its revision), the loadouts or the draft.
    /// </summary>
    public static class GarageOperations
    {
        public const int MaxNameLength = 32;
        public const int MaxNoteLength = 200;
        /// <summary>
        /// Longest livery document <see cref="ApplyLivery"/> stores. Equal to the customization vocabulary's
        /// LiveryLimits.MaxPayloadChars (repeated here so Builds stays independent of Customization).
        /// </summary>
        public const int MaxLiveryChars = 32768;
        /// <summary>Longest livery hash accepted (the customization hash is 64 lower-case hex characters).</summary>
        public const int MaxLiveryHashChars = 128;

        /// <summary>A new instance's workspace: stock applied build (revision 1) with derived stats; empty library (no invented presets).</summary>
        public static CarBuildWorkspace NewWorkspace(string instanceId, BuildContext ctx, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(instanceId)) throw new ArgumentException("Instance id required", nameof(instanceId));
            CarBuildWorkspace ws = CarBuildWorkspace.CreateNew(instanceId, ctx.Car.Id, nowUtc);
            ResolvedCarSpec stock = ctx.Stock;
            ws.Applied.BuildHash = stock.BuildHash;
            ws.Applied.Pi = ctx.Car.BasePI;
            ws.Applied.PiClass = Rules.PerformanceIndex.ClassOf(ctx.Car.BasePI).ToString();
            ws.Applied.HandlingModelVersion = stock.HandlingModelVersion;
            ws.Applied.PartsCatalogueRevision = stock.PartsCatalogueRevision;
            return ws;
        }

        // ---------------------------------------------------------------- library

        public static OperationResult SaveAs(CarBuildWorkspace ws, long expectedRevision, string name, string note, BuildContext ctx, DateTime nowUtc, bool fromApplied = false)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            if (!TryName(ws, name, null, out string clean, out OperationResult bad)) return bad;
            if (ws.Loadouts.Count >= ws.LoadoutCapacity)
                return Fail(ws, OpStatus.CapacityFull, $"All {ws.LoadoutCapacity} loadout slots are used. Overwrite or delete one.");
            MechanicalSnapshot build = fromApplied || ws.Draft == null ? ws.Applied.Build : ws.Draft.Build;
            BuildEvaluation ev = BuildEvaluator.Evaluate(build, ws.Car.InstanceId, ctx);
            if (!ev.CanSaveAsPlan) return NeedsRepair(ws, ev, "This build cannot be saved until these items are repaired.");

            var l = new MechanicalLoadout
            {
                LoadoutId = NewId(ws, "ld", clean),
                Name = clean,
                CarInstanceId = ws.Car.InstanceId,
                CarModelId = ws.Car.ModelId,
                Build = build.Clone(),
                Note = TrimNote(note),
                UpdatedUtc = nowUtc,
            };
            Derive(l, ev, ctx);
            ws.Loadouts.Add(l);
            return Ok(ws, $"Saved \"{clean}\"{(l.NeedsParts ? " as a planning draft (needs parts)" : "")}.", l.LoadoutId, ev);
        }

        public static OperationResult Rename(CarBuildWorkspace ws, long expectedRevision, string loadoutId, string newName, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            MechanicalLoadout l = ws.Loadout(loadoutId);
            if (l == null) return Fail(ws, OpStatus.NotFound, "No such loadout.");
            if (!TryName(ws, newName, loadoutId, out string clean, out OperationResult bad)) return bad;
            l.Name = clean;
            l.UpdatedUtc = nowUtc;
            return Ok(ws, $"Renamed to \"{clean}\".", loadoutId);
        }

        public static OperationResult EditNote(CarBuildWorkspace ws, long expectedRevision, string loadoutId, string note, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            MechanicalLoadout l = ws.Loadout(loadoutId);
            if (l == null) return Fail(ws, OpStatus.NotFound, "No such loadout.");
            l.Note = TrimNote(note);
            l.UpdatedUtc = nowUtc;
            return Ok(ws, "Note saved.", loadoutId);
        }

        /// <summary>Replaces a saved loadout's build with the draft (or applied) build. Requires confirmation.</summary>
        public static OperationResult Overwrite(CarBuildWorkspace ws, long expectedRevision, string loadoutId, string confirmationToken, BuildContext ctx, DateTime nowUtc, bool fromApplied = false)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            MechanicalLoadout l = ws.Loadout(loadoutId);
            if (l == null) return Fail(ws, OpStatus.NotFound, "No such loadout.");
            MechanicalSnapshot build = fromApplied || ws.Draft == null ? ws.Applied.Build : ws.Draft.Build;
            string token = Token(ws, "overwrite", loadoutId);
            if (confirmationToken != token)
            {
                OperationResult ask = Confirm(ws, token, $"Overwrite \"{l.Name}\"? Its saved parts and tune are replaced.");
                ask.Comparison = Compare(l.Build, build, ws.Car.InstanceId, ctx);
                return ask;
            }
            BuildEvaluation ev = BuildEvaluator.Evaluate(build, ws.Car.InstanceId, ctx);
            if (!ev.CanSaveAsPlan) return NeedsRepair(ws, ev, "This build cannot be saved until these items are repaired.");
            l.Build = build.Clone();
            l.UnresolvedPartIds.Clear();
            l.LegacyDocumentJson = "";
            l.Notices.Clear();
            l.UpdatedUtc = nowUtc;
            Derive(l, ev, ctx);
            return Ok(ws, $"Overwrote \"{l.Name}\".", loadoutId, ev);
        }

        public static OperationResult Duplicate(CarBuildWorkspace ws, long expectedRevision, string loadoutId, string newName, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            MechanicalLoadout src = ws.Loadout(loadoutId);
            if (src == null) return Fail(ws, OpStatus.NotFound, "No such loadout.");
            if (!TryName(ws, newName, null, out string clean, out OperationResult bad)) return bad;
            if (ws.Loadouts.Count >= ws.LoadoutCapacity)
                return Fail(ws, OpStatus.CapacityFull, $"All {ws.LoadoutCapacity} loadout slots are used.");
            MechanicalLoadout copy = src.Clone();
            copy.LoadoutId = NewId(ws, "ld", clean);
            copy.Name = clean;
            copy.Pinned = false;
            copy.UpdatedUtc = nowUtc;
            ws.Loadouts.Add(copy);
            return Ok(ws, $"Duplicated as \"{clean}\" (a copy of the plan; parts are not duplicated).", copy.LoadoutId);
        }

        public static OperationResult Delete(CarBuildWorkspace ws, long expectedRevision, string loadoutId, string confirmationToken)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            MechanicalLoadout l = ws.Loadout(loadoutId);
            if (l == null) return Fail(ws, OpStatus.NotFound, "No such loadout.");
            string token = Token(ws, "delete", loadoutId);
            if (confirmationToken != token)
                return Confirm(ws, token, $"Delete \"{l.Name}\"? Owned parts and the applied build are not affected.");
            ws.Loadouts.Remove(l);
            return Ok(ws, $"Deleted \"{l.Name}\".", loadoutId);
        }

        public static OperationResult SetPinned(CarBuildWorkspace ws, long expectedRevision, string loadoutId, bool pinned)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            MechanicalLoadout l = ws.Loadout(loadoutId);
            if (l == null) return Fail(ws, OpStatus.NotFound, "No such loadout.");
            if (pinned && !l.Pinned && ws.Loadouts.Count(x => x.Pinned) >= CarBuildWorkspace.MaxPinnedLoadouts)
                return Fail(ws, OpStatus.PinLimit, $"At most {CarBuildWorkspace.MaxPinnedLoadouts} pinned presets per car.");
            if (l.Pinned == pinned) return Unchanged(ws, "No change.");
            l.Pinned = pinned;
            return Ok(ws, pinned ? "Pinned." : "Unpinned.", loadoutId);
        }

        // ---------------------------------------------------------------- visual presets (independent from mechanical)

        public static OperationResult SaveVisualPreset(CarBuildWorkspace ws, long expectedRevision, string name, string payloadSchema, string payloadJson, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            string clean = CleanName(name);
            if (clean == null) return Fail(ws, OpStatus.InvalidName, $"Names are 1–{MaxNameLength} printable characters.");
            if (ws.VisualPresets.Any(v => string.Equals(v.Name, clean, StringComparison.OrdinalIgnoreCase)))
                return Fail(ws, OpStatus.DuplicateName, $"A visual preset named \"{clean}\" already exists.");
            if (ws.VisualPresets.Count >= ws.VisualPresetCapacity)
                return Fail(ws, OpStatus.CapacityFull, $"All {ws.VisualPresetCapacity} visual preset slots are used.");
            var v = new VisualPreset { PresetId = NewId(ws, "vp", clean), Name = clean, PayloadSchema = payloadSchema ?? "", PayloadJson = payloadJson ?? "", UpdatedUtc = nowUtc };
            ws.VisualPresets.Add(v);
            return Ok(ws, $"Saved visual preset \"{clean}\".", v.PresetId);
        }

        /// <summary>
        /// Deletes a visual preset (requires confirmation). The applied livery is a copy and stays applied; if it was taken from
        /// this preset it is no longer linked to one (<see cref="CarBuildWorkspace.AppliedVisualPresetId"/> becomes "").
        /// </summary>
        public static OperationResult DeleteVisualPreset(CarBuildWorkspace ws, long expectedRevision, string presetId, string confirmationToken)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            VisualPreset v = ws.VisualPresets.FirstOrDefault(x => x.PresetId == presetId);
            if (v == null) return Fail(ws, OpStatus.NotFound, "No such visual preset.");
            string token = Token(ws, "delete-visual", presetId);
            if (confirmationToken != token) return Confirm(ws, token, $"Delete visual preset \"{v.Name}\"?");
            ws.VisualPresets.Remove(v);
            if (ws.AppliedVisualPresetId == presetId) ws.AppliedVisualPresetId = "";
            return Ok(ws, $"Deleted visual preset \"{v.Name}\".", presetId);
        }

        /// <summary>
        /// Replaces a saved visual preset's payload (e.g. the livery being edited saved over "Night Blue"). Requires confirmation
        /// exactly like a loadout <see cref="Overwrite"/>: the first call answers ConfirmationRequired with a token valid for this
        /// revision. An identical payload is an unchanged no-op (no confirmation needed). The payload is opaque here: the caller
        /// (customization) validates it. The applied livery is a copy; if it was taken from this preset and now differs from the
        /// new payload, it is no longer linked to the preset.
        /// </summary>
        public static OperationResult UpdateVisualPreset(CarBuildWorkspace ws, long expectedRevision, string presetId, string payloadSchema,
            string payloadJson, string confirmationToken, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            VisualPreset v = ws.VisualPresets.FirstOrDefault(x => x.PresetId == presetId);
            if (v == null) return Fail(ws, OpStatus.NotFound, "No such visual preset.");
            string schema = payloadSchema ?? "", payload = payloadJson ?? "";
            if (v.PayloadSchema == schema && v.PayloadJson == payload) return Unchanged(ws, $"\"{v.Name}\" already holds this look.");
            string token = Token(ws, "overwrite-visual", presetId);
            if (confirmationToken != token)
                return Confirm(ws, token, $"Overwrite visual preset \"{v.Name}\"? Its saved look is replaced.");
            v.PayloadSchema = schema;
            v.PayloadJson = payload;
            v.UpdatedUtc = nowUtc;
            if (ws.AppliedVisualPresetId == presetId && payload != ws.AppliedLivery) ws.AppliedVisualPresetId = "";
            return Ok(ws, $"Overwrote visual preset \"{v.Name}\".", presetId);
        }

        /// <summary>Renames a visual preset: the same name rules and (case-insensitive, per car) uniqueness as <see cref="SaveVisualPreset"/>.</summary>
        public static OperationResult RenameVisualPreset(CarBuildWorkspace ws, long expectedRevision, string presetId, string name, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            VisualPreset v = ws.VisualPresets.FirstOrDefault(x => x.PresetId == presetId);
            if (v == null) return Fail(ws, OpStatus.NotFound, "No such visual preset.");
            string clean = CleanName(name);
            if (clean == null) return Fail(ws, OpStatus.InvalidName, $"Names are 1–{MaxNameLength} printable characters.");
            if (ws.VisualPresets.Any(x => x.PresetId != presetId && string.Equals(x.Name, clean, StringComparison.OrdinalIgnoreCase)))
                return Fail(ws, OpStatus.DuplicateName, $"A visual preset named \"{clean}\" already exists.");
            if (v.Name == clean) return Unchanged(ws, "No change.");
            v.Name = clean;
            v.UpdatedUtc = nowUtc;
            return Ok(ws, $"Renamed visual preset to \"{clean}\".", presetId);
        }

        // ---------------------------------------------------------------- applied livery (independent from mechanical)

        /// <summary>
        /// Sets the applied livery: <paramref name="liveryJson"/> (canonical JSON; "" = back to the stock appearance), its
        /// <paramref name="liveryHash"/> ("" exactly when the livery is "") and the visual preset it was taken from
        /// (<paramref name="presetId"/>; "" = an edited livery, otherwise the preset must exist). Builds never parses the
        /// document: the CALLER validates it (customization LiveryValidator in Apply mode, ownership included) and computes the
        /// hash. The same livery, hash and preset again is an unchanged no-op. Never touches the mechanical applied build, its
        /// revision, loadouts, draft or presets; an accepted change advances the workspace <see cref="CarBuildWorkspace.Revision"/>
        /// like every other operation. <paramref name="nowUtc"/> is the operation time (the livery itself carries no timestamp).
        /// </summary>
        public static OperationResult ApplyLivery(CarBuildWorkspace ws, long expectedRevision, string liveryJson, string liveryHash, string presetId,
            DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            string livery = liveryJson ?? "", hash = liveryHash ?? "", preset = presetId ?? "";
            if (livery.Length > MaxLiveryChars) return Fail(ws, OpStatus.Rejected, $"A livery is at most {MaxLiveryChars} characters.");
            if (hash.Length > MaxLiveryHashChars) return Fail(ws, OpStatus.Rejected, $"A livery hash is at most {MaxLiveryHashChars} characters.");
            if ((livery.Length == 0) != (hash.Length == 0))
                return Fail(ws, OpStatus.Rejected, livery.Length == 0 ? "The stock appearance has no livery hash." : "A livery needs its hash.");
            if (preset.Length > 0 && !ws.VisualPresets.Any(v => v.PresetId == preset))
                return Fail(ws, OpStatus.NotFound, "No such visual preset.");
            if (livery == (ws.AppliedLivery ?? "") && hash == (ws.AppliedLiveryHash ?? "") && preset == (ws.AppliedVisualPresetId ?? ""))
                return Unchanged(ws, livery.Length == 0 ? "Already the stock appearance." : "Already the applied livery.");
            ws.AppliedLivery = livery;
            ws.AppliedLiveryHash = hash;
            ws.AppliedVisualPresetId = preset;
            return Ok(ws, livery.Length == 0 ? "Stock appearance applied." : "Livery applied.", preset.Length == 0 ? null : preset);
        }

        // ---------------------------------------------------------------- compare

        /// <summary>Compact delta list (parts, tune, utility, key parameters) and resulting PI/class of both builds.</summary>
        public static BuildComparison Compare(MechanicalSnapshot a, MechanicalSnapshot b, string instanceId, BuildContext ctx) =>
            BuildComparison.Of(a, b, instanceId, ctx);

        // ---------------------------------------------------------------- draft

        /// <summary>
        /// Loads the whole setup of the applied build, a saved loadout or a protected reference into the draft. Replacing a
        /// dirty draft requires confirmation. Never spends and never changes the applied (race) build. The result carries
        /// the evaluation: exact repairs and "Preview only — not owned" parts.
        /// </summary>
        public static OperationResult LoadIntoDraft(CarBuildWorkspace ws, long expectedRevision, DraftSource source, string confirmationToken, BuildContext ctx, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            MechanicalSnapshot build = Find(ws, source, out string label);
            if (build == null) return Fail(ws, OpStatus.NotFound, "Nothing to load from " + source.Key + ".");
            if (ws.DraftIsDirty && !ws.Draft.Build.ContentEquals(build))
            {
                string token = Token(ws, "replace-draft", source.Key);
                if (confirmationToken != token)
                {
                    OperationResult ask = Confirm(ws, token, "Replace the unsaved draft? Its changes are lost unless saved first.");
                    ask.Comparison = Compare(ws.Draft.Build, build, ws.Car.InstanceId, ctx);
                    return ask;
                }
            }
            BuildEvaluation ev = BuildEvaluator.Evaluate(build, ws.Car.InstanceId, ctx);
            List<RepairItem> unresolved = UnresolvedOf(source.Kind == "loadout" ? ws.Loadout(source.Id) : null);
            ws.Draft = new GarageDraft
            {
                Build = build.Clone(), LoadedFrom = source.Key, BasedOnAppliedRevision = ws.Applied.Revision,
                PreviewPartIds = new List<string>(ev.PreviewPartIds), UpdatedUtc = nowUtc,
                UnresolvedPartIds = unresolved.Select(u => u.PartId).ToList(),
            };
            OperationResult r = Ok(ws, $"Loaded {label} into the draft.", null, ev);
            r.Repairs.AddRange(unresolved);
            r.Repairs.AddRange(ev.Repairs);
            r.Comparison = Compare(ws.Applied.Build, build, ws.Car.InstanceId, ctx);
            return r;
        }

        /// <summary>Stores an edited candidate as the draft (parts/tune chosen in the Garage). May contain preview parts.</summary>
        public static OperationResult EditDraft(CarBuildWorkspace ws, long expectedRevision, MechanicalSnapshot build, BuildContext ctx, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            if (build == null) return Fail(ws, OpStatus.Rejected, "No build.");
            BuildEvaluation ev = BuildEvaluator.Evaluate(build, ws.Car.InstanceId, ctx);
            // An edited candidate is no longer a pure restore/loadout copy: label it as an edit.
            string from = ws.Draft != null && ws.Draft.Build.ContentEquals(build) ? ws.Draft.LoadedFrom : "edit";
            ws.Draft = new GarageDraft
            {
                Build = build.Clone(), LoadedFrom = from, BasedOnAppliedRevision = ws.Applied.Revision,
                PreviewPartIds = new List<string>(ev.PreviewPartIds), UpdatedUtc = nowUtc,
            };
            OperationResult r = Ok(ws, "Draft updated.", null, ev);
            r.Repairs.AddRange(ev.Repairs);
            return r;
        }

        public static OperationResult DiscardDraft(CarBuildWorkspace ws, long expectedRevision)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            if (ws.Draft == null) return Unchanged(ws, "No draft.");
            ws.Draft = null;
            return Ok(ws, "Draft discarded.");
        }

        /// <summary>Evaluates a candidate for the private Test Yard. Pure: no reference, draft or revision changes.</summary>
        public static BuildEvaluation PreviewForTest(CarBuildWorkspace ws, MechanicalSnapshot candidate, BuildContext ctx) =>
            BuildEvaluator.Evaluate(candidate ?? ws.Applied.Build, ws.Car.InstanceId, ctx);

        // ---------------------------------------------------------------- apply

        /// <summary>Atomic whole-build apply of the draft. See <see cref="ApplySnapshot"/>.</summary>
        public static OperationResult Apply(CarBuildWorkspace ws, long expectedRevision, BuildContext ctx, EventConstraints constraints, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            if (ws.Draft == null) return Fail(ws, OpStatus.NoDraft, "Nothing to apply: load or edit a draft first.");
            if (ws.Draft.UnresolvedPartIds.Count > 0)
            {
                OperationResult partial = Fail(ws, OpStatus.NeedsRepair, "Not applied: the draft still references parts that could not be placed. Edit the draft to choose replacements (or stock) explicitly.");
                partial.Repairs.AddRange(ws.Draft.UnresolvedPartIds.Select(id => new RepairItem { Kind = RepairKind.UnresolvedLegacyPart, PartId = id, Detail = "Could not be placed in a slot." }));
                return partial;
            }
            string source = ws.Draft.LoadedFrom.StartsWith("reference:", StringComparison.Ordinal)
                ? "restore:" + ws.Draft.LoadedFrom.Substring("reference:".Length)
                : "apply:draft";
            return ApplySnapshot(ws, ws.Draft.Build, source, ctx, constraints, nowUtc);
        }

        /// <summary>Quick selector: apply a saved owned legal loadout as one whole-build action. The draft is left untouched.</summary>
        public static OperationResult ApplyLoadout(CarBuildWorkspace ws, long expectedRevision, string loadoutId, BuildContext ctx, EventConstraints constraints, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            MechanicalLoadout l = ws.Loadout(loadoutId);
            if (l == null) return Fail(ws, OpStatus.NotFound, "No such loadout.");
            List<RepairItem> unresolved = UnresolvedOf(l);
            if (unresolved.Count > 0)
            {
                OperationResult partial = Fail(ws, OpStatus.NeedsRepair, $"\"{l.Name}\" cannot be applied until it is repaired (no partial build is applied).");
                partial.Repairs.AddRange(unresolved);
                return partial;
            }
            return ApplySnapshot(ws, l.Build, "apply:loadout:" + loadoutId, ctx, constraints, nowUtc);
        }

        /// <summary>
        /// Replaces the ENTIRE applied component/tune set at once: every part owned by this instance, compatible, shop-legal,
        /// tune valid, within the event cap and not build-locked — otherwise nothing changes and the exact repair list is
        /// returned. Reusing owned parts is free. On success: new applied revision, Before Last Apply = the prior applied
        /// state. Applying the already-applied build is a no-op.
        /// </summary>
        internal static OperationResult ApplySnapshot(CarBuildWorkspace ws, MechanicalSnapshot build, string source, BuildContext ctx, EventConstraints constraints, DateTime nowUtc)
        {
            if (constraints != null && constraints.BuildLocked)
            {
                OperationResult locked = Fail(ws, OpStatus.BuildLocked, $"Builds are frozen for {constraints.Label}.");
                locked.Repairs.Add(new RepairItem { Kind = RepairKind.BuildLocked, Detail = locked.Message });
                return locked;
            }
            BuildEvaluation ev = BuildEvaluator.Evaluate(build, ws.Car.InstanceId, ctx, constraints);
            if (!ev.CanApply) return NeedsRepair(ws, ev, "Not applied. Repair these items (nothing was bought, substituted or changed).");
            if (build.ContentEquals(ws.Applied.Build))
            {
                OperationResult same = Unchanged(ws, "Already the applied build.");
                same.Evaluation = ev;
                return same;
            }

            AppliedVehicleBuild previous = ws.Applied;
            var next = new AppliedVehicleBuild
            {
                Revision = previous.Revision + 1,
                Build = build.Clone(),
                BuildHash = ev.Spec.BuildHash,
                Pi = ev.Pi.Value,
                PiClass = ev.Pi.Class.ToString(),
                HandlingModelVersion = ev.Spec.HandlingModelVersion,
                PartsCatalogueRevision = ev.Spec.PartsCatalogueRevision,
                AppliedUtc = nowUtc,
                Source = source,
            };
            string previousHash = string.IsNullOrEmpty(previous.BuildHash) ? HashOf(previous.Build, ctx) : previous.BuildHash;
            ws.References[BuildReferenceKinds.Id(BuildReferenceKind.BeforeLastApply)] = FromApplied(previous, BuildReferenceKind.BeforeLastApply, nowUtc, previousHash, "");
            ws.Applied = next;
            OperationResult r = Ok(ws, $"Applied (revision {next.Revision}, PI {next.Pi} {next.PiClass}).", null, ev);
            r.PerformanceChanged = previousHash != next.BuildHash;
            r.Comparison = Compare(previous.Build, next.Build, ws.Car.InstanceId, ctx);
            return r;
        }

        // ---------------------------------------------------------------- protected references

        /// <summary>
        /// Opening the Garage editing session. Captures Before Workshop only when no session is open, so a reconnect,
        /// a Test Yard run, a toy or a menu hop never replaces it with the current experiment.
        /// </summary>
        public static OperationResult BeginWorkshopSession(CarBuildWorkspace ws, BuildContext ctx, DateTime nowUtc)
        {
            if (ws.Workshop.Open) return Unchanged(ws, "Workshop session already open; Before Workshop kept.");
            ws.Workshop = new WorkshopSession { Open = true, OpenedUtc = nowUtc };
            ws.References[BuildReferenceKinds.Id(BuildReferenceKind.BeforeWorkshop)] =
                FromApplied(ws.Applied, BuildReferenceKind.BeforeWorkshop, nowUtc, HashOrCompute(ws.Applied, ctx), "");
            return Ok(ws, "Workshop session started; Before Workshop captured.");
        }

        /// <summary>Explicitly ends the workshop session (the Before Workshop reference stays restorable until the next session).</summary>
        public static OperationResult EndWorkshopSession(CarBuildWorkspace ws, long expectedRevision)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            if (!ws.Workshop.Open) return Unchanged(ws, "No workshop session open.");
            ws.Workshop = new WorkshopSession { Open = false };
            return Ok(ws, "Workshop session ended.");
        }

        /// <summary>Explicitly accepts the current applied build as the new Before Workshop baseline.</summary>
        public static OperationResult AcceptNewWorkshopBaseline(CarBuildWorkspace ws, long expectedRevision, BuildContext ctx, DateTime nowUtc)
        {
            if (Stale(ws, expectedRevision, out OperationResult stale)) return stale;
            if (!ws.Workshop.Open) return Fail(ws, OpStatus.Rejected, "No workshop session open.");
            ws.References[BuildReferenceKinds.Id(BuildReferenceKind.BeforeWorkshop)] =
                FromApplied(ws.Applied, BuildReferenceKind.BeforeWorkshop, nowUtc, HashOrCompute(ws.Applied, ctx), "accepted baseline");
            return Ok(ws, "New workshop baseline accepted.");
        }

        /// <summary>
        /// Records Last Race Build. Call ONLY when the player actually begins authorized full-size driving, with the FROZEN
        /// build the server accepted for that event (never the mutable Garage state). Test Yard, toys and menus are rejected.
        /// </summary>
        public static OperationResult RecordRaceBegan(CarBuildWorkspace ws, DrivingSessionKind kind, AppliedVehicleBuild frozenBuild, string eventId, DateTime nowUtc)
        {
            if (kind != DrivingSessionKind.FullSizeEvent)
                return Fail(ws, OpStatus.Rejected, $"{kind} never records Last Race Build.");
            if (frozenBuild == null || frozenBuild.Build == null) return Fail(ws, OpStatus.Rejected, "No frozen race build.");
            if (frozenBuild.Revision > ws.Applied.Revision)
                return Fail(ws, OpStatus.Rejected, "Frozen build revision is newer than this car's applied revision.");
            var r = new BuildReference
            {
                Kind = BuildReferenceKinds.Id(BuildReferenceKind.LastRaceBuild),
                Build = frozenBuild.Build.Clone(),
                SourceAppliedRevision = frozenBuild.Revision,
                BuildHash = frozenBuild.BuildHash,
                Pi = frozenBuild.Pi,
                HandlingModelVersion = frozenBuild.HandlingModelVersion,
                PartsCatalogueRevision = frozenBuild.PartsCatalogueRevision,
                CapturedUtc = nowUtc,
                Context = eventId ?? "",
            };
            ws.References[r.Kind] = r;
            return Ok(ws, "Last Race Build recorded.");
        }

        /// <summary>
        /// Recomputes derived PI/class/hash/needs-parts of every loadout and reference after a catalogue or handling
        /// version change. Never deletes: invalid entries keep their build and get an explanation.
        /// </summary>
        public static OperationResult Revalidate(CarBuildWorkspace ws, BuildContext ctx, DateTime nowUtc)
        {
            var changes = new List<string>();
            foreach (MechanicalLoadout l in ws.Loadouts)
            {
                BuildEvaluation ev = BuildEvaluator.Evaluate(l.Build, ws.Car.InstanceId, ctx);
                string before = $"{l.DerivedPi}|{l.BuildHash}|{l.NeedsParts}|{l.HandlingModelVersion}|{l.PartsCatalogueRevision}|{l.Notices.Count}";
                Derive(l, ev, ctx);
                foreach (RepairItem rep in ev.Repairs.Where(x => x.Kind != RepairKind.NotOwned))
                {
                    string notice = "Needs repair: " + rep;
                    if (!l.Notices.Contains(notice)) l.Notices.Add(notice);
                }
                foreach (string id in l.UnresolvedPartIds)
                {
                    string notice = $"Needs repair: legacy part {id} could not be placed in a slot.";
                    if (!l.Notices.Contains(notice)) l.Notices.Add(notice);
                }
                string after = $"{l.DerivedPi}|{l.BuildHash}|{l.NeedsParts}|{l.HandlingModelVersion}|{l.PartsCatalogueRevision}|{l.Notices.Count}";
                if (before != after) changes.Add($"Loadout \"{l.Name}\" re-derived (PI {l.DerivedPi}{(ev.Resolved ? "" : ", needs repair")}).");
            }
            foreach (BuildReference r in ws.References.Values)
            {
                BuildEvaluation ev = BuildEvaluator.Evaluate(r.Build, ws.Car.InstanceId, ctx);
                if (ev.Resolved && (r.BuildHash != ev.Spec.BuildHash || r.HandlingModelVersion != ev.Spec.HandlingModelVersion))
                {
                    changes.Add($"Reference {r.Kind}: parameters changed under {ev.Spec.HandlingModelVersion}/catalogue {ev.Spec.PartsCatalogueRevision}; parts and tune kept.");
                    r.BuildHash = ev.Spec.BuildHash;
                    r.Pi = ev.Pi.Value;
                    r.HandlingModelVersion = ev.Spec.HandlingModelVersion;
                    r.PartsCatalogueRevision = ev.Spec.PartsCatalogueRevision;
                }
                else if (!ev.Resolved) changes.Add($"Reference {r.Kind} kept but needs repair: {string.Join("; ", ev.Repairs)}");
            }
            if (changes.Count == 0) return Unchanged(ws, "All derived stats current.");
            OperationResult ok = Ok(ws, "Derived stats refreshed.");
            ok.Changes = changes;
            return ok;
        }

        // ---------------------------------------------------------------- helpers

        public static string ConfirmationTokenFor(CarBuildWorkspace ws, string op, string target) => Token(ws, op, target);

        static string Token(CarBuildWorkspace ws, string op, string target) =>
            "cf-" + BuildHashing.Sha256Hex($"{op}|{target}|{ws.Car.InstanceId}|{ws.Revision.ToString(CultureInfo.InvariantCulture)}").Substring(0, 16);

        static string NewId(CarBuildWorkspace ws, string prefix, string salt) =>
            prefix + "-" + BuildHashing.Sha256Hex($"{ws.Car.InstanceId}|{ws.Revision.ToString(CultureInfo.InvariantCulture)}|{salt}|{prefix}").Substring(0, 12);

        static MechanicalSnapshot Find(CarBuildWorkspace ws, DraftSource source, out string label)
        {
            label = source.Key;
            switch (source.Kind)
            {
                case "applied":
                    label = "the applied build";
                    return ws.Applied.Build;
                case "loadout":
                    MechanicalLoadout l = ws.Loadout(source.Id);
                    label = l == null ? label : $"\"{l.Name}\"";
                    return l?.Build;
                case "reference":
                    if (!BuildReferenceKinds.TryParse(source.Id, out BuildReferenceKind kind)) return null;
                    label = source.Id;
                    return ws.Reference(kind)?.Build;
                default:
                    return null;
            }
        }

        static List<RepairItem> UnresolvedOf(MechanicalLoadout l)
        {
            var list = new List<RepairItem>();
            if (l == null) return list;
            foreach (string id in l.UnresolvedPartIds)
                list.Add(new RepairItem { Kind = RepairKind.UnresolvedLegacyPart, PartId = id, Detail = "Legacy preset part could not be placed in a slot." });
            if (!string.IsNullOrEmpty(l.LegacyDocumentJson))
                list.Add(new RepairItem { Kind = RepairKind.UnresolvedLegacyPart, Detail = "Unrecognised legacy document kept verbatim; rebuild this preset." });
            return list;
        }

        static void Derive(MechanicalLoadout l, BuildEvaluation ev, BuildContext ctx)
        {
            l.CarModelId = ctx.Car.Id;
            l.NeedsParts = ev.PreviewPartIds.Count > 0 || ev.Repairs.Any(r => r.Kind == RepairKind.Locked || r.Kind == RepairKind.Unavailable);
            if (ev.Resolved)
            {
                l.DerivedPi = ev.Pi.Value;
                l.DerivedClass = ev.Pi.Class.ToString();
                l.BuildHash = ev.Spec.BuildHash;
                l.HandlingModelVersion = ev.Spec.HandlingModelVersion;
                l.PartsCatalogueRevision = ev.Spec.PartsCatalogueRevision;
                l.PerformanceAppearance = new List<string>(ev.Spec.PerformanceAppearance);
            }
            else
            {
                l.DerivedPi = 0;
                l.DerivedClass = "";
                l.BuildHash = "";
                l.HandlingModelVersion = BuildResolver.HandlingModelVersion;
                l.PartsCatalogueRevision = ctx.Parts.Revision;
            }
        }

        static BuildReference FromApplied(AppliedVehicleBuild a, BuildReferenceKind kind, DateTime nowUtc, string hash, string context) => new BuildReference
        {
            Kind = BuildReferenceKinds.Id(kind),
            Build = a.Build.Clone(),
            SourceAppliedRevision = a.Revision,
            BuildHash = hash ?? "",
            Pi = a.Pi,
            HandlingModelVersion = a.HandlingModelVersion,
            PartsCatalogueRevision = a.PartsCatalogueRevision,
            CapturedUtc = nowUtc,
            Context = context ?? "",
        };

        static string HashOrCompute(AppliedVehicleBuild a, BuildContext ctx) =>
            string.IsNullOrEmpty(a.BuildHash) ? HashOf(a.Build, ctx) : a.BuildHash;

        static string HashOf(MechanicalSnapshot build, BuildContext ctx)
        {
            ResolveResult r = BuildResolver.Resolve(ctx.Car, ctx.Tuning, ctx.Parts, build);
            return r.Ok ? r.Spec.BuildHash : "";
        }

        static bool TryName(CarBuildWorkspace ws, string name, string exceptLoadoutId, out string clean, out OperationResult failure)
        {
            failure = null;
            clean = CleanName(name);
            if (clean == null)
            {
                failure = Fail(ws, OpStatus.InvalidName, $"Names are 1–{MaxNameLength} printable characters.");
                return false;
            }
            string c = clean;
            if (ws.Loadouts.Any(l => l.LoadoutId != exceptLoadoutId && string.Equals(l.Name, c, StringComparison.OrdinalIgnoreCase)))
            {
                failure = Fail(ws, OpStatus.DuplicateName, $"A loadout named \"{clean}\" already exists on this car.");
                return false;
            }
            return true;
        }

        /// <summary>Trimmed, 1–32 characters, no control characters (names are data, never markup).</summary>
        public static string CleanName(string name)
        {
            if (name == null) return null;
            string t = name.Trim();
            if (t.Length == 0 || t.Length > MaxNameLength) return null;
            if (t.Any(ch => char.IsControl(ch) || ch == '<' || ch == '>')) return null;
            return t;
        }

        static string TrimNote(string note)
        {
            if (string.IsNullOrEmpty(note)) return "";
            string t = new string(note.Where(ch => !char.IsControl(ch) || ch == '\n').ToArray()).Trim();
            return t.Length > MaxNoteLength ? t.Substring(0, MaxNoteLength) : t;
        }

        static bool Stale(CarBuildWorkspace ws, long expectedRevision, out OperationResult result)
        {
            if (ws == null) throw new ArgumentNullException(nameof(ws));
            result = null;
            if (expectedRevision == ws.Revision) return false;
            result = Fail(ws, OpStatus.StaleRevision, $"This car changed elsewhere (revision {ws.Revision}, you had {expectedRevision}). Reload before editing.");
            return true;
        }

        static OperationResult Ok(CarBuildWorkspace ws, string message, string loadoutId = null, BuildEvaluation ev = null)
        {
            ws.Revision++;
            return new OperationResult { Status = OpStatus.Ok, Message = message, Revision = ws.Revision, LoadoutId = loadoutId, Evaluation = ev };
        }

        static OperationResult Unchanged(CarBuildWorkspace ws, string message) =>
            new OperationResult { Status = OpStatus.Ok, Message = message, Revision = ws.Revision };

        static OperationResult Fail(CarBuildWorkspace ws, OpStatus status, string message) =>
            new OperationResult { Status = status, Message = message, Revision = ws.Revision };

        static OperationResult Confirm(CarBuildWorkspace ws, string token, string message) =>
            new OperationResult { Status = OpStatus.ConfirmationRequired, Message = message, Revision = ws.Revision, ConfirmationToken = token };

        static OperationResult NeedsRepair(CarBuildWorkspace ws, BuildEvaluation ev, string message)
        {
            OperationResult r = Fail(ws, OpStatus.NeedsRepair, message);
            r.Repairs.AddRange(ev.Repairs);
            r.Evaluation = ev;
            return r;
        }
    }
}
