using System.Text;
using System.Text.Json;
using NightSignal.Core.Builds;
using NightSignal.Core.Customization;

namespace NightSignal.ControlPlane.Garage;

// Wire shapes of the ONLINE Garage (docs/NETWORKING.md §2.6). Output is camelCase System.Text.Json; ids (slots, part ids,
// tuning keys) are map KEYS and are never renamed. Everything the client sends is bounded here before Core sees it.

/// <summary>
/// POST /v1/me/garage/cars/{instanceId}/operations — one Core GarageOperations call. <see cref="LiveryJson"/> is the livery
/// document for <c>livery-apply</c> (null or "" = back to the stock appearance).
/// </summary>
public sealed record GarageOpRequest(string? Op, long? ExpectedRevision = null, string? ConfirmationToken = null, string? LoadoutId = null,
    string? Name = null, string? Note = null, bool? Pinned = null, bool? FromApplied = null, string? PresetId = null, string? PayloadSchema = null,
    string? PayloadJson = null, DraftSourceInput? Source = null, BuildInput? Build = null, string? LiveryJson = null);

/// <summary>Load-into-draft source: <c>applied</c>, <c>loadout</c> (+ id) or <c>reference</c> (+ before-workshop | before-last-apply | last-race-build).</summary>
public sealed record DraftSourceInput(string? Kind, string? Id = null);

/// <summary>A mechanical selection: part id by slot id (absent/empty = stock), the utility item and integer tuning values.</summary>
public sealed record BuildInput(Dictionary<string, string?>? Parts = null, string? UtilityPartId = null, TuningInput? Tuning = null);

public sealed record TuningInput(int? Version = null, Dictionary<string, int>? Values = null);

/// <summary>POST …/quote: the build to buy and apply — the stored draft (default) or an explicit build.</summary>
public sealed record QuoteRequest(string? Source = null, BuildInput? Build = null);

/// <summary>POST …/quote/{quoteId}/settle: <c>confirm</c> must be true (explicit Buy-and-Apply confirmation).</summary>
public sealed record SettleRequest(bool? Confirm = null);

public static class GarageWire
{
    public const int MaxIdLength = 64;
    public const int MaxKeyLength = 32;
    public const int MaxBuildEntries = 16;
    public const int MaxTuningEntries = 32;
    /// <summary>
    /// Bound of an opaque visual preset payload, and of every STORED payload. A livery (schema night-signal/livery@1) is
    /// accepted up to <see cref="MaxLiveryJsonChars"/> on input (pretty-printed documents), but what is stored is its canonical
    /// form, which for a maximal 64-layer livery is about 10 KB (&lt; this bound; checked again before storing).
    /// </summary>
    public const int MaxPayloadJsonChars = 16 * 1024;
    /// <summary>Input bound of a livery document (<c>liveryJson</c>, or a livery-schema <c>payloadJson</c>): Core LiveryLimits.MaxPayloadChars.</summary>
    public const int MaxLiveryJsonChars = LiveryLimits.MaxPayloadChars;
    public const int MaxPayloadSchemaChars = 64;

    public static readonly string[] Operations =
    {
        "save-as", "rename", "note", "overwrite", "duplicate", "delete", "pin", "visual-preset-save", "visual-preset-update",
        "visual-preset-rename", "visual-preset-delete", "livery-apply",
        "load-into-draft", "edit-draft", "discard-draft", "apply", "apply-loadout", "begin-workshop", "end-workshop", "accept-baseline",
    };

    /// <summary>Client build → Core snapshot, or an error message. Unknown ids are left for Core to report as repairs.</summary>
    public static MechanicalSnapshot? ToSnapshot(BuildInput? input, out string? error)
    {
        error = null;
        if (input is null)
        {
            error = "build is required: {parts:{slot:partId}, utilityPartId?, tuning?:{version, values:{key:int}}}.";
            return null;
        }
        var s = new MechanicalSnapshot();
        if (input.Parts is { } parts)
        {
            if (parts.Count > MaxBuildEntries) { error = $"At most {MaxBuildEntries} part slots."; return null; }
            foreach (var kv in parts)
            {
                if (!Bounded(kv.Key, MaxKeyLength)) { error = "Slot ids are 1–32 characters."; return null; }
                if (string.IsNullOrEmpty(kv.Value)) continue; // stock
                if (!Bounded(kv.Value, MaxIdLength)) { error = "Part ids are 1–64 characters."; return null; }
                s.Parts[kv.Key] = kv.Value;
            }
        }
        if (!string.IsNullOrEmpty(input.UtilityPartId))
        {
            if (!Bounded(input.UtilityPartId, MaxIdLength)) { error = "Part ids are 1–64 characters."; return null; }
            s.UtilityPartId = input.UtilityPartId;
        }
        if (input.Tuning is { } tuning)
        {
            s.Tuning.Version = tuning.Version ?? TuningModel.CurrentVersion;
            if (tuning.Values is { } values)
            {
                if (values.Count > MaxTuningEntries) { error = $"At most {MaxTuningEntries} tuning values."; return null; }
                foreach (var kv in values)
                {
                    if (!Bounded(kv.Key, MaxKeyLength)) { error = "Tuning keys are 1–32 characters."; return null; }
                    s.Tuning.Values[kv.Key] = kv.Value;
                }
            }
        }
        return s;
    }

    static bool Bounded(string? s, int max) => !string.IsNullOrEmpty(s) && s.Length <= max && s.All(ch => !char.IsControl(ch));

    public static bool TryDraftSource(DraftSourceInput? input, out DraftSource source, out string? error)
    {
        source = DraftSource.Applied();
        error = null;
        switch (input?.Kind)
        {
            case "applied":
                return true;
            case "loadout" when Bounded(input.Id, MaxIdLength):
                source = DraftSource.Loadout(input.Id!);
                return true;
            case "reference" when input.Id is not null && BuildReferenceKinds.TryParse(input.Id, out BuildReferenceKind kind):
                source = DraftSource.Reference(kind);
                return true;
            default:
                error = "source must be {kind:\"applied\"}, {kind:\"loadout\", id} or {kind:\"reference\", id:\"before-workshop\"|\"before-last-apply\"|\"last-race-build\"}.";
                return false;
        }
    }

    /// <summary>
    /// A visual preset payload is data for the customization system; it must be bounded, well-formed JSON. A livery-schema
    /// payload may be up to <see cref="MaxLiveryJsonChars"/> here; GarageService then validates it as a livery and stores the
    /// canonical form.
    /// </summary>
    public static string? ValidatePayload(string? schema, string? payloadJson)
    {
        if (schema is { Length: > MaxPayloadSchemaChars }) return $"payloadSchema is at most {MaxPayloadSchemaChars} characters.";
        if (string.IsNullOrEmpty(payloadJson)) return null;
        int max = schema == LiveryDocument.SchemaId ? MaxLiveryJsonChars : MaxPayloadJsonChars;
        if (payloadJson.Length > max) return $"payloadJson is at most {max} characters.";
        try
        {
            using JsonDocument _ = JsonDocument.Parse(payloadJson);
            return null;
        }
        catch (JsonException)
        {
            return "payloadJson must be a JSON document (stored as data, never markup).";
        }
    }

    // ------------------------------------------------------------------ output

    public static string Kebab(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (int i = 0; i < pascal.Length; i++)
        {
            char ch = pascal[i];
            if (char.IsUpper(ch) && i > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    public static object Snapshot(MechanicalSnapshot s) => new
    {
        parts = s.Parts ?? new SortedDictionary<string, string>(),
        utilityPartId = s.UtilityPartId,
        tuning = new { version = (s.Tuning ?? new TuningSetup()).Version, values = (s.Tuning ?? new TuningSetup()).Values },
    };

    public static object Repair(RepairItem r) => new
    {
        kind = Kebab(r.Kind.ToString()), slot = r.Slot, partId = r.PartId, partName = r.PartName, detail = r.Detail, price = r.Price,
        text = r.ToString(),
    };

    public static object Applied(AppliedVehicleBuild a) => new
    {
        revision = a.Revision, build = Snapshot(a.Build), buildHash = a.BuildHash, pi = a.Pi, piClass = a.PiClass, piIsEstimate = true,
        handlingModelVersion = a.HandlingModelVersion, partsCatalogueRevision = a.PartsCatalogueRevision, appliedUtc = a.AppliedUtc, source = a.Source,
    };

    public static object Loadout(MechanicalLoadout l) => new
    {
        loadoutId = l.LoadoutId, name = l.Name, carInstanceId = l.CarInstanceId, carModelId = l.CarModelId, build = Snapshot(l.Build),
        performanceAppearance = l.PerformanceAppearance, derivedPi = l.DerivedPi, derivedClass = l.DerivedClass, buildHash = l.BuildHash,
        handlingModelVersion = l.HandlingModelVersion, partsCatalogueRevision = l.PartsCatalogueRevision, note = l.Note, updatedUtc = l.UpdatedUtc,
        pinned = l.Pinned, needsParts = l.NeedsParts, unresolvedPartIds = l.UnresolvedPartIds, notices = l.Notices,
        keptLegacyDocument = !string.IsNullOrEmpty(l.LegacyDocumentJson),
    };

    public static object Visual(VisualPreset v) => new
    {
        presetId = v.PresetId, name = v.Name, payloadSchema = v.PayloadSchema, payloadJson = v.PayloadJson, updatedUtc = v.UpdatedUtc,
    };

    public static object Reference(BuildReference r) => new
    {
        kind = r.Kind, build = Snapshot(r.Build), sourceAppliedRevision = r.SourceAppliedRevision, buildHash = r.BuildHash, pi = r.Pi,
        handlingModelVersion = r.HandlingModelVersion, partsCatalogueRevision = r.PartsCatalogueRevision, capturedUtc = r.CapturedUtc,
        context = r.Context,
    };

    public static object Workspace(CarBuildWorkspace ws) => new
    {
        schema = ws.Schema,
        schemaVersion = ws.SchemaVersion,
        instanceId = ws.Car.InstanceId,
        carId = ws.Car.ModelId,
        revision = ws.Revision,
        applied = Applied(ws.Applied),
        loadouts = ws.Loadouts.Select(Loadout).ToList(),
        loadoutCapacity = ws.LoadoutCapacity,
        pinnedLimit = CarBuildWorkspace.MaxPinnedLoadouts,
        visualPresets = ws.VisualPresets.Select(Visual).ToList(),
        visualPresetCapacity = ws.VisualPresetCapacity,
        appliedVisualPresetId = ws.AppliedVisualPresetId,
        appliedLiveryHash = ws.AppliedLiveryHash,
        appliedLivery = ws.AppliedLivery ?? "",
        references =ws.References.ToDictionary(kv => kv.Key, kv => Reference(kv.Value)),
        draft = ws.Draft is null ? null : new
        {
            build = Snapshot(ws.Draft.Build), loadedFrom = ws.Draft.LoadedFrom, basedOnAppliedRevision = ws.Draft.BasedOnAppliedRevision,
            previewPartIds = ws.Draft.PreviewPartIds, unresolvedPartIds = ws.Draft.UnresolvedPartIds, updatedUtc = ws.Draft.UpdatedUtc,
        },
        draftDirty = ws.DraftIsDirty,
        workshop = new { open = ws.Workshop.Open, openedUtc = ws.Workshop.Open ? ws.Workshop.OpenedUtc : (DateTime?)null },
    };

    public static object Evaluation(BuildEvaluation ev) => new
    {
        resolved = ev.Resolved,
        buildHash = ev.Spec?.BuildHash,
        pi = ev.Pi is null ? null : new { value = ev.Pi.Value, @class = ev.Pi.Class.ToString(), isEstimate = ev.Pi.IsEstimate, basis = ev.Pi.Basis },
        canPreview = ev.CanPreview,
        canSaveAsPlan = ev.CanSaveAsPlan,
        canApply = ev.CanApply,
        previewPartIds = ev.PreviewPartIds,
        missingParts = ev.MissingParts.Select(r => new { partId = r.PartId, slot = r.Slot, name = r.PartName, price = r.Price }).ToList(),
        missingTotal = ev.MissingParts.Sum(r => r.Price),
        repairs = ev.Repairs.Select(Repair).ToList(),
        utility = ev.Spec is null ? null : new { partId = ev.Spec.Utility.PartId, incomePercent = ev.Spec.Utility.IncomePercent, showcasePercent = ev.Spec.Utility.ShowcasePercent },
        performanceAppearance = ev.Spec?.PerformanceAppearance,
        handlingModelVersion = ev.Spec?.HandlingModelVersion,
        partsCatalogueRevision = ev.Spec?.PartsCatalogueRevision,
    };

    static object Delta(PartDelta d) => new { slot = d.Slot, from = d.From, fromName = d.FromName, to = d.To, toName = d.ToName };

    public static object Comparison(BuildComparison c) => new
    {
        parts = c.Parts.Select(Delta).ToList(),
        tuning = c.Tuning.Select(t => new { key = t.Key, from = t.From, to = t.To }).ToList(),
        utility = c.Utility is null ? null : Delta(c.Utility),
        parameters = c.Parameters.Select(p => new { param = p.Param.ToString(), from = p.From, to = p.To, unit = p.Unit }).ToList(),
        piFrom = c.PiFrom, piTo = c.PiTo, classFrom = c.ClassFrom, classTo = c.ClassTo, classChanged = c.ClassChanged, identical = c.Identical,
        repairsFrom = c.RepairsFrom.Select(Repair).ToList(),
        repairsTo = c.RepairsTo.Select(Repair).ToList(),
        lines = c.Lines,
    };

    public static object Quote(PurchaseAndApplyQuote q) => new
    {
        quoteId = q.QuoteId, instanceId = q.InstanceId, carId = q.ModelId, build = Snapshot(q.Build), buildHash = q.BuildHash, pi = q.Pi,
        piClass = q.PiClass, piIsEstimate = true,
        lines = q.Lines.Select(Line).ToList(),
        total = q.Total, priceRevision = q.PriceRevision, catalogueRevision = q.CatalogueRevision, appliedRevision = q.AppliedRevision,
        issuedUtc = q.IssuedUtc, expiresUtc = q.ExpiresUtc,
    };

    public static object Line(QuoteLine l) => new { partId = l.PartId, slot = l.Slot, name = l.Name, tier = l.Tier, price = l.Price };
}
