using Newtonsoft.Json;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Customization;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Garage;

/// <summary>HTTP-shaped answer of a Garage request (the pattern SettlementService uses): status code and JSON body.</summary>
public sealed record GarageReply(int Status, object Body)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// The ONLINE Garage (Addendum 02 §8–10) on top of the engine-free Core build model. The server owns part ownership per car
/// INSTANCE, the whole CarBuildWorkspace (optimistic concurrency by revision) and Buy-and-Apply quotes; every rule is Core's
/// (<see cref="GarageOperations"/>, <see cref="PurchaseQuotes"/>, <see cref="BuildResolver"/>, <see cref="PerformanceIndexEstimator"/>).
/// Performance truth for the convoy (hash, PI, frozen race build) is always re-resolved here from the stored APPLIED build.
/// Appearance truth likewise: liveries are parsed, validated (Core <see cref="LiveryValidator"/>, ownership from
/// <c>cosmetics_owned</c>) and hashed HERE; the convoy's cosmetic hash and the assignment's livery come from the stored
/// applied livery, never from a client claim.
/// </summary>
public sealed class GarageService(IGarageStore store, IPlayerStore players, ContentService content, GarageContent garage,
    CustomizationContent customization, ConvoyDirectory convoys, TimeProvider clock, ILogger<GarageService> log)
{
    public GarageContent Data => garage;

    DateTime Now => clock.GetUtcNow().UtcDateTime;
    ContentCatalogue Catalogue => content.Catalogue;

    /// <summary>One car instance loaded for a request: its workspace, ownership and evaluation context.</summary>
    sealed class LoadedCar
    {
        public required CarInstance Instance { get; init; }
        public required CarBuildWorkspace Workspace { get; init; }
        /// <summary>The stored revision the workspace was read at (the compare-and-swap value for the write).</summary>
        public required long StoredRevision { get; init; }
        public required BuildContext Context { get; init; }
        public required IReadOnlyCollection<string> OwnedParts { get; init; }
        public required int ShopAct { get; init; }
    }

    // ================================================================== reads

    /// <summary>GET /v1/me/garage/cars — every owned instance with a workspace summary (instances/workspaces created on demand).</summary>
    public async Task<GarageReply> ListCarsAsync(string account, CancellationToken ct)
    {
        IReadOnlyList<CarInstance> instances = await store.EnsureCarInstancesAsync(account, ct);
        int shopAct = await ShopActAsync(account, ct);
        PlayerSnapshot snapshot = await players.GetSnapshotAsync(account, ct);
        var cars = new List<object>();
        foreach (CarInstance i in instances)
        {
            if (!Catalogue.TryCar(i.CarId, out CarDef car)) continue; // a retired catalogue car is not listed (its rows are kept)
            LoadedCar c = await LoadAsync(i, shopAct, ct);
            cars.Add(Summary(c, car));
        }
        return new GarageReply(200, new
        {
            domain = "online",
            shopAct,
            wallet = new { balance = snapshot.Balance, cap = Limits.WalletCap },
            partsCatalogue = CatalogueInfo(),
            handlingModelVersion = BuildResolver.HandlingModelVersion,
            cars,
        });
    }

    /// <summary>GET /v1/me/garage/cars/{instanceId} — the whole workspace plus server evaluations of the applied build and draft.</summary>
    public async Task<GarageReply> GetCarAsync(string account, string instanceId, CancellationToken ct)
    {
        LoadedCar? c = await LoadAsync(account, instanceId, ct);
        if (c is null) return NotFound();
        CarBuildWorkspace ws = c.Workspace;
        BuildEvaluation applied = BuildEvaluator.Evaluate(ws.Applied.Build, ws.Car.InstanceId, c.Context);
        return new GarageReply(200, new
        {
            domain = "online",
            instanceId = c.Instance.InstanceId,
            carId = c.Instance.CarId,
            carName = Catalogue.Car(c.Instance.CarId).Name,
            source = c.Instance.Source,
            ordinal = c.Instance.Ordinal,
            shopAct = c.ShopAct,
            partsCatalogue = CatalogueInfo(),
            handlingModelVersion = BuildResolver.HandlingModelVersion,
            ownedParts = c.OwnedParts,
            buildFrozen = convoys.BuildFrozenFor(account, instanceId),
            workspace = GarageWire.Workspace(ws),
            appliedEvaluation = GarageWire.Evaluation(applied),
            draftEvaluation = ws.Draft is null ? null : GarageWire.Evaluation(BuildEvaluator.Evaluate(ws.Draft.Build, ws.Car.InstanceId, c.Context)),
            draftComparison = ws.Draft is null ? null : GarageWire.Comparison(GarageOperations.Compare(ws.Applied.Build, ws.Draft.Build, ws.Car.InstanceId, c.Context)),
        });
    }

    /// <summary>
    /// GET /v1/me/garage/cars/{instanceId}/parts — every part compatible with this car by slot, with price, shop availability
    /// (by act, never by spending), ownership by THIS instance and tuning controls; plus the authored upgrade recipes for the
    /// model with their exact missing parts and cost (explained options, never auto-purchases).
    /// </summary>
    public async Task<GarageReply> PartsAsync(string account, string instanceId, CancellationToken ct)
    {
        LoadedCar? c = await LoadAsync(account, instanceId, ct);
        if (c is null) return NotFound();
        BuildContext ctx = c.Context;
        CarBuildWorkspace ws = c.Workspace;
        var owned = c.OwnedParts.ToHashSet(StringComparer.Ordinal);
        var applied = ws.Applied.Build.AllPartIds().ToHashSet(StringComparer.Ordinal);
        var draft = ws.Draft?.Build.AllPartIds().ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<PartDef> compatible = garage.Parts.CompatibleParts(ctx.Car, ctx.Tuning);
        var slots = Enum.GetValues<PartSlot>().Select(slot => new
        {
            slot = PartSlots.Id(slot),
            parts = compatible.Where(p => p.SlotValue == slot).Select(p => new
            {
                partId = p.Id, name = p.Name, tier = p.Tier, price = p.Price, unlockAct = p.UnlockAct, retired = p.Retired,
                available = !p.Retired && p.UnlockAct <= c.ShopAct,
                owned = owned.Contains(p.Id), installed = applied.Contains(p.Id), inDraft = draft.Contains(p.Id),
                tradeoff = p.Tradeoff, appearance = p.Appearance,
                utility = p.Utility is null ? null : new { kind = p.Utility.Kind, percent = p.Utility.Percent },
                tuning = TuningModel.Controls(new[] { p }, ctx.Stock.Get).Select(t => new { key = t.Key, min = t.Min, max = t.Max, step = t.Step, @default = t.Default, unit = t.Unit }).ToList(),
            }).ToList(),
        }).Where(s => s.parts.Count > 0).ToList();

        CarRecipe? recipe = garage.Recipes.Car(c.Instance.CarId);
        object? recipes = recipe is null ? null : new
        {
            role = recipe.Role, identity = recipe.Identity, capExcludedBands = recipe.CapExcludedBands,
            steps = recipe.Path.Concat(recipe.Alternatives).Select(step =>
            {
                MechanicalSnapshot build = RecipeBook.ToSnapshot(step, garage.Parts);
                BuildEvaluation ev = BuildEvaluator.Evaluate(build, ws.Car.InstanceId, ctx);
                return new
                {
                    id = step.Id, label = step.Label, kind = step.Kind, by = step.By, bands = step.For, note = step.Note,
                    build = GarageWire.Snapshot(build), pi = ev.Pi?.Value, piClass = ev.Pi?.Class.ToString(), piIsEstimate = true,
                    missingParts = ev.MissingParts.Select(r => new { partId = r.PartId, name = r.PartName, price = r.Price }).ToList(),
                    missingTotal = ev.MissingParts.Sum(r => r.Price),
                    lockedParts = ev.Repairs.Where(r => r.Kind == RepairKind.Locked).Select(r => new { partId = r.PartId, name = r.PartName, detail = r.Detail }).ToList(),
                    canApplyNow = ev.CanApply,
                };
            }).ToList(),
        };
        return new GarageReply(200, new
        {
            instanceId = c.Instance.InstanceId, carId = c.Instance.CarId, shopAct = c.ShopAct, partsCatalogue = CatalogueInfo(),
            slots, recipes,
        });
    }

    object CatalogueInfo() => new { revision = garage.Parts.Revision, priceRevision = garage.Parts.PriceRevision, hash = garage.Hash };

    object Summary(LoadedCar c, CarDef car)
    {
        CarBuildWorkspace ws = c.Workspace;
        (EntrantBuild? build, List<RepairItem> repairs) = Resolve(c);
        return new
        {
            instanceId = c.Instance.InstanceId, carId = car.Id, carName = car.Name, source = c.Instance.Source, ordinal = c.Instance.Ordinal,
            revision = ws.Revision,
            applied = new
            {
                revision = ws.Applied.Revision, buildHash = build?.BuildHash, pi = build?.Pi, piClass = build?.PiClass, piIsEstimate = true,
                source = ws.Applied.Source, appliedUtc = ws.Applied.AppliedUtc, needsRepair = build is null,
                repairs = repairs.Select(GarageWire.Repair).ToList(),
            },
            loadouts = new
            {
                count = ws.Loadouts.Count, capacity = ws.LoadoutCapacity,
                pinned = ws.Loadouts.Where(l => l.Pinned).Select(l => new { loadoutId = l.LoadoutId, name = l.Name, derivedPi = l.DerivedPi, derivedClass = l.DerivedClass, needsParts = l.NeedsParts }).ToList(),
            },
            visualPresets = new { count = ws.VisualPresets.Count, capacity = ws.VisualPresetCapacity },
            draft = ws.Draft is null ? null : new { dirty = ws.DraftIsDirty, loadedFrom = ws.Draft.LoadedFrom },
            references = ws.References.Keys.ToList(),
            ownedParts = c.OwnedParts.Count,
        };
    }

    // ================================================================== operations

    /// <summary>POST /v1/me/garage/cars/{instanceId}/operations — exactly one Core GarageOperations call, then a compare-and-swap write.</summary>
    public async Task<GarageReply> OperateAsync(string account, string instanceId, GarageOpRequest req, CancellationToken ct)
    {
        string op = req.Op ?? "";
        if (!GarageWire.Operations.Contains(op))
            return Error(400, "invalid_request", $"op must be one of: {string.Join(", ", GarageWire.Operations)}.");
        if (req.ExpectedRevision is null && op != "begin-workshop")
            return Error(400, "invalid_request", "expectedRevision (the workspace revision you edited) is required.");
        if (op is "visual-preset-save" or "visual-preset-update" && GarageWire.ValidatePayload(req.PayloadSchema, req.PayloadJson) is { } payloadError)
            return Error(400, "invalid_request", payloadError);
        if (op is "livery-apply" && req.LiveryJson is { Length: > GarageWire.MaxLiveryJsonChars })
            return Error(400, "invalid_request", $"liveryJson is at most {GarageWire.MaxLiveryJsonChars} characters.");
        if (req.ConfirmationToken is { Length: > GarageWire.MaxIdLength } || req.Note is { Length: > 1000 } || req.Name is { Length: > 200 } ||
            req.PresetId is { Length: > GarageWire.MaxIdLength })
            return Error(400, "invalid_request", "A field is too long.");

        LoadedCar? c = await LoadAsync(account, instanceId, ct);
        if (c is null) return NotFound();
        CarBuildWorkspace ws = c.Workspace;
        long revisionBefore = ws.Revision;
        long appliedBefore = ws.Applied.Revision;
        string liveryHashBefore = ws.AppliedLiveryHash ?? "";
        long expected = req.ExpectedRevision ?? ws.Revision;
        EventConstraints? constraints = Constraints(account, instanceId);
        DateTime now = Now;

        OperationResult? r;
        string? inputError = null;
        GarageReply? refusal = null; // a structured input refusal (invalid_livery), returned instead of inputError
        switch (op)
        {
            case "save-as":
                r = GarageOperations.SaveAs(ws, expected, req.Name, req.Note, c.Context, now, req.FromApplied ?? false);
                break;
            case "rename":
                r = RequireLoadout(req, out inputError) ? GarageOperations.Rename(ws, expected, req.LoadoutId!, req.Name, now) : null;
                break;
            case "note":
                r = RequireLoadout(req, out inputError) ? GarageOperations.EditNote(ws, expected, req.LoadoutId!, req.Note, now) : null;
                break;
            case "overwrite":
                r = RequireLoadout(req, out inputError)
                    ? GarageOperations.Overwrite(ws, expected, req.LoadoutId!, req.ConfirmationToken, c.Context, now, req.FromApplied ?? false)
                    : null;
                break;
            case "duplicate":
                r = RequireLoadout(req, out inputError) ? GarageOperations.Duplicate(ws, expected, req.LoadoutId!, req.Name, now) : null;
                break;
            case "delete":
                r = RequireLoadout(req, out inputError) ? GarageOperations.Delete(ws, expected, req.LoadoutId!, req.ConfirmationToken) : null;
                break;
            case "pin":
                r = RequireLoadout(req, out inputError) ? GarageOperations.SetPinned(ws, expected, req.LoadoutId!, req.Pinned ?? true) : null;
                break;
            case "visual-preset-save":
            {
                refusal = PresetPayload(req, ws.Car.ModelId, out string? payload);
                r = refusal is null ? GarageOperations.SaveVisualPreset(ws, expected, req.Name, req.PayloadSchema, payload, now) : null;
                break;
            }
            case "visual-preset-update":
            {
                if (!RequirePreset(req, out inputError))
                {
                    r = null;
                    break;
                }
                refusal = PresetPayload(req, ws.Car.ModelId, out string? payload);
                r = refusal is null
                    ? GarageOperations.UpdateVisualPreset(ws, expected, req.PresetId!, req.PayloadSchema, payload, req.ConfirmationToken, now)
                    : null;
                break;
            }
            case "visual-preset-rename":
                r = RequirePreset(req, out inputError) ? GarageOperations.RenameVisualPreset(ws, expected, req.PresetId!, req.Name, now) : null;
                break;
            case "visual-preset-delete":
                r = RequirePreset(req, out inputError) ? GarageOperations.DeleteVisualPreset(ws, expected, req.PresetId!, req.ConfirmationToken) : null;
                break;
            case "livery-apply":
            {
                string presetId = req.PresetId ?? "";
                string canonical = "", hash = "";
                if (!string.IsNullOrEmpty(req.LiveryJson))
                {
                    // Apply mode: every locked item must be owned by THIS account (cosmetics_owned), not claimed by the client.
                    IReadOnlyList<string> cosmetics = (await players.GetSnapshotAsync(account, ct)).Cosmetics;
                    refusal = CheckLivery(req.LiveryJson, ws.Car.ModelId, CosmeticOwnership.FromIds(cosmetics), LiveryValidationMode.Apply,
                        out LiveryDocument? doc);
                    if (doc is not null)
                    {
                        canonical = LiveryJson.ToCanonicalJson(doc);
                        hash = LiveryHash.Of(doc);
                    }
                }
                if (refusal is null && presetId.Length > 0 && ws.VisualPresets.FirstOrDefault(v => v.PresetId == presetId) is { } preset &&
                    !HoldsLivery(preset, canonical))
                    refusal = Error(400, "invalid_request",
                        $"presetId: \"{preset.Name}\" holds a different look than liveryJson; send presetId \"\" for an edited livery.");
                r = refusal is null ? GarageOperations.ApplyLivery(ws, expected, canonical, hash, presetId, now) : null;
                break;
            }
            case "load-into-draft":
                r = GarageWire.TryDraftSource(req.Source, out DraftSource source, out inputError)
                    ? GarageOperations.LoadIntoDraft(ws, expected, source, req.ConfirmationToken, c.Context, now)
                    : null;
                break;
            case "edit-draft":
            {
                MechanicalSnapshot? build = GarageWire.ToSnapshot(req.Build, out inputError);
                r = build is null ? null : GarageOperations.EditDraft(ws, expected, build, c.Context, now);
                break;
            }
            case "discard-draft":
                r = GarageOperations.DiscardDraft(ws, expected);
                break;
            case "apply":
                r = GarageOperations.Apply(ws, expected, c.Context, constraints, now);
                break;
            case "apply-loadout":
                r = RequireLoadout(req, out inputError) ? GarageOperations.ApplyLoadout(ws, expected, req.LoadoutId!, c.Context, constraints, now) : null;
                break;
            case "begin-workshop":
                r = GarageOperations.BeginWorkshopSession(ws, c.Context, now);
                break;
            case "end-workshop":
                r = GarageOperations.EndWorkshopSession(ws, expected);
                break;
            default: // accept-baseline
                r = GarageOperations.AcceptNewWorkshopBaseline(ws, expected, c.Context, now);
                break;
        }
        if (r is null) return refusal ?? Error(400, "invalid_request", inputError ?? "Invalid request.");
        if (!r.Accepted) return OperationFailure(r, ws);

        bool changed = ws.Revision != revisionBefore;
        if (changed)
        {
            WorkspaceSaveStatus saved = await store.SaveWorkspaceAsync(account, instanceId, c.StoredRevision, Write(ws), ct);
            if (saved != WorkspaceSaveStatus.Saved)
                return Error(409, "stale_revision", "This car changed elsewhere while your change was being saved. Reload before editing.",
                    new { revision = (await store.GetWorkspaceAsync(instanceId, ct))?.Revision });
        }
        // A new applied build refreshes the convoy's performance truth; a new applied livery only its cosmetic hash (readiness kept).
        bool performance = changed && ws.Applied.Revision != appliedBefore;
        bool appearance = changed && (ws.AppliedLiveryHash ?? "") != liveryHashBefore;
        object? convoy = performance || appearance ? RefreshConvoy(account, c.Instance, ws, performance) : null;
        return new GarageReply(200, new
        {
            status = changed ? "ok" : "unchanged",
            op,
            message = r.Message,
            revision = ws.Revision,
            loadoutId = r.LoadoutId,
            performanceChanged = r.PerformanceChanged,
            evaluation = r.Evaluation is null ? null : GarageWire.Evaluation(r.Evaluation),
            comparison = r.Comparison is null ? null : GarageWire.Comparison(r.Comparison),
            repairs = r.Repairs.Select(GarageWire.Repair).ToList(),
            changes = r.Changes,
            convoy,
            workspace = GarageWire.Workspace(ws),
        });
    }

    static bool RequireLoadout(GarageOpRequest req, out string? error)
    {
        error = string.IsNullOrEmpty(req.LoadoutId) || req.LoadoutId.Length > GarageWire.MaxIdLength ? "loadoutId is required." : null;
        return error is null;
    }

    static bool RequirePreset(GarageOpRequest req, out string? error)
    {
        error = string.IsNullOrEmpty(req.PresetId) || req.PresetId.Length > GarageWire.MaxIdLength ? "presetId is required." : null;
        return error is null;
    }

    /// <summary>
    /// The payload to store for visual-preset-save/-update. A livery (payloadSchema night-signal/livery@1) must be a
    /// structurally valid livery for THIS car in Preview mode (locked items may be kept in a preset as a plan; applying still
    /// needs them owned) and is stored in its canonical form. Other schemas stay opaque (bounded JSON, see GarageWire).
    /// </summary>
    GarageReply? PresetPayload(GarageOpRequest req, string carId, out string? payload)
    {
        payload = req.PayloadJson;
        if (req.PayloadSchema != LiveryDocument.SchemaId) return null;
        GarageReply? refused = CheckLivery(req.PayloadJson ?? "", carId, null, LiveryValidationMode.Preview, out LiveryDocument? doc);
        if (refused is not null) return refused;
        payload = LiveryJson.ToCanonicalJson(doc!);
        return payload.Length <= GarageWire.MaxPayloadJsonChars
            ? null
            : Error(400, "invalid_request", $"The canonical livery exceeds {GarageWire.MaxPayloadJsonChars} characters.");
    }

    /// <summary>
    /// Strict parse (Core <see cref="LiveryJson"/>) and validation (Core <see cref="LiveryValidator"/>) of a client livery for
    /// <paramref name="carId"/> against the trusted customization catalogue. On success <paramref name="doc"/> is the parsed
    /// document (colours canonical, placement quantised); otherwise the refusal lists the validator's exact messages.
    /// </summary>
    GarageReply? CheckLivery(string json, string carId, CosmeticOwnership? owned, LiveryValidationMode mode, out LiveryDocument? doc)
    {
        doc = null;
        LiveryParseResult parsed = LiveryJson.Parse(json);
        if (!parsed.Ok) return InvalidLivery("The livery is not a readable livery document", parsed.Errors, Array.Empty<LockedItem>());
        LiveryValidation v = LiveryValidator.Validate(parsed.Document, customization.Catalogue, carId, owned, mode);
        if (!v.IsValid)
        {
            string message = parsed.Document.Car != carId ? $"This livery is for {(parsed.Document.Car is { Length: > 0 } other ? other : "no car")}, not {carId}"
                : mode == LiveryValidationMode.Apply && v.Errors.Count == v.Locked.Count ? "The livery uses items you have not unlocked"
                : "The livery is not valid for this car";
            return InvalidLivery(message, v.Errors, v.Locked);
        }
        doc = parsed.Document;
        return null;
    }

    static GarageReply InvalidLivery(string message, IReadOnlyList<string> errors, IReadOnlyList<LockedItem> locked) =>
        Error(400, "invalid_livery", $"{message}: {string.Join("; ", errors.Take(3))}{(errors.Count > 3 ? $" (+{errors.Count - 3} more)" : "")}.",
            new Dictionary<string, object?>
            {
                ["errors"] = errors.ToList(),
                ["locked"] = locked.Select(l => new { path = l.Path, itemId = l.ItemId, name = l.Name, cosmeticId = l.CosmeticId }).ToList(),
            });

    /// <summary>True when <paramref name="preset"/> holds exactly the livery <paramref name="canonical"/> (same canonical form).</summary>
    static bool HoldsLivery(VisualPreset preset, string canonical)
    {
        if (canonical.Length == 0 || preset.PayloadSchema != LiveryDocument.SchemaId) return false;
        if (preset.PayloadJson == canonical) return true;
        LiveryParseResult parsed = LiveryJson.Parse(preset.PayloadJson);
        return parsed.Ok && LiveryJson.ToCanonicalJson(parsed.Document) == canonical;
    }

    static GarageReply OperationFailure(OperationResult r, CarBuildWorkspace ws)
    {
        var extra = new Dictionary<string, object?> { ["revision"] = ws.Revision };
        if (r.Repairs.Count > 0) extra["repairs"] = r.Repairs.Select(GarageWire.Repair).ToList();
        if (r.Evaluation is not null) extra["evaluation"] = GarageWire.Evaluation(r.Evaluation);
        if (r.Comparison is not null) extra["comparison"] = GarageWire.Comparison(r.Comparison);
        switch (r.Status)
        {
            case OpStatus.StaleRevision: return Error(409, "stale_revision", r.Message, extra);
            case OpStatus.NotFound: return Error(404, "not_found", r.Message, extra);
            case OpStatus.ConfirmationRequired:
                extra["confirmationToken"] = r.ConfirmationToken;
                return Error(409, "confirmation_required", r.Message, extra);
            case OpStatus.InvalidName: return Error(400, "invalid_name", r.Message, extra);
            case OpStatus.DuplicateName: return Error(409, "duplicate_name", r.Message, extra);
            case OpStatus.CapacityFull: return Error(409, "capacity_full", r.Message, extra);
            case OpStatus.PinLimit: return Error(409, "pin_limit", r.Message, extra);
            case OpStatus.NeedsRepair: return Error(409, "needs_repair", r.Message, extra);
            case OpStatus.BuildLocked: return Error(409, "build_locked", r.Message, extra);
            case OpStatus.NoDraft: return Error(409, "no_draft", r.Message, extra);
            default: return Error(409, "rejected", r.Message, extra);
        }
    }

    // ================================================================== Buy-and-Apply

    /// <summary>POST …/quote — Core PurchaseQuotes.Create for the draft (default) or an explicit build; the quote is stored server-side.</summary>
    public async Task<GarageReply> CreateQuoteAsync(string account, string instanceId, QuoteRequest req, CancellationToken ct)
    {
        LoadedCar? c = await LoadAsync(account, instanceId, ct);
        if (c is null) return NotFound();
        CarBuildWorkspace ws = c.Workspace;
        MechanicalSnapshot? build;
        if (req.Build is not null)
        {
            build = GarageWire.ToSnapshot(req.Build, out string? error);
            if (build is null) return Error(400, "invalid_request", error!);
        }
        else if (req.Source is null or "draft")
        {
            build = ws.Draft?.Build;
            if (build is null) return Error(409, "no_draft", "Nothing to quote: load or edit a draft first (or send a build).");
        }
        else return Error(400, "invalid_request", "source must be \"draft\" (or send an explicit build).");

        long balance = (await players.GetSnapshotAsync(account, ct)).Balance;
        QuoteResult q = PurchaseQuotes.Create(ws, build, c.Context, balance, Constraints(account, instanceId), Now);
        switch (q.Status)
        {
            case QuoteStatus.NothingToBuy:
                return Error(409, "nothing_to_buy", q.Message);
            case QuoteStatus.NotPurchasable:
                return Error(409, "not_purchasable", q.Message, new { repairs = q.Repairs.Select(GarageWire.Repair).ToList() });
        }
        PurchaseAndApplyQuote quote = q.Quote;
        await store.SaveQuoteAsync(new StoredQuote(quote.QuoteId, account, instanceId, JsonConvert.SerializeObject(quote, BuildJson.Settings),
            quote.Total, new DateTimeOffset(DateTime.SpecifyKind(quote.ExpiresUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()), ct);
        return new GarageReply(200, new
        {
            quote = GarageWire.Quote(quote),
            affordableNow = q.AffordableNow,
            walletBalance = q.WalletBalance,
            message = q.Message,
            confirmation = "Settle with {\"confirm\": true}. Prices, wallet, availability, compatibility and ownership are rechecked then.",
        });
    }

    sealed record SettleOutcome(SettlementResult Result, PurchaseAndApplyQuote? Quote, CarBuildWorkspace? Workspace, long BalanceSeen,
        CarInstance? Instance);

    /// <summary>
    /// POST …/quote/{quoteId}/settle — Core PurchaseQuotes.Settle inside ONE store transaction with the wallet debit, part
    /// grants, workspace change and the quote ledger row (unique quote id). A retry returns the settled record, never a
    /// second charge; every rejection leaves wallet, ownership and workspace untouched.
    /// </summary>
    public async Task<GarageReply> SettleQuoteAsync(string account, string instanceId, string quoteId, bool confirmed, CancellationToken ct)
    {
        StoredQuote? stored = await store.GetQuoteAsync(account, quoteId, ct);
        if (stored is null || stored.InstanceId != instanceId) return Error(404, "unknown_quote", "No such quote for this car.");
        int shopAct = await ShopActAsync(account, ct);
        EventConstraints? constraints = Constraints(account, instanceId);
        DateTime now = Now;

        SettleOutcome o = await store.SettleQuoteAsync(account, quoteId, view => Decide(view, confirmed, shopAct, constraints, now), ct);
        SettlementResult s = o.Result;
        switch (s.Outcome)
        {
            case Core.Builds.SettlementOutcome.Settled:
            {
                CarBuildWorkspace next = o.Workspace!;
                object? convoy = RefreshConvoy(account, o.Instance!, next, performance: true);
                log.LogInformation("Buy-and-Apply {QuoteId}: {Count} part(s), {Debit} credits, applied revision {Revision}", quoteId, s.Grants.Count, s.Debit,
                    next.Applied.Revision);
                return new GarageReply(200, new
                {
                    status = "settled", replayed = false, quoteId, charged = s.Debit, debit = s.Debit, grants = s.Grants, balance = s.NewBalance,
                    appliedRevision = next.Applied.Revision, buildHash = next.Applied.BuildHash, pi = next.Applied.Pi, piClass = next.Applied.PiClass,
                    piIsEstimate = true, performanceChanged = s.PerformanceChanged, message = s.Message, convoy, revision = next.Revision,
                    workspace = GarageWire.Workspace(next),
                });
            }
            case Core.Builds.SettlementOutcome.AlreadySettled:
            {
                SettlementRecord rec = s.Record;
                return new GarageReply(200, new
                {
                    status = "settled", replayed = true, quoteId, charged = 0L, debit = rec.Debit, grants = rec.Grants, balance = o.BalanceSeen,
                    balanceAfterSettlement = rec.BalanceAfter, appliedRevision = rec.AppliedRevision, buildHash = rec.BuildHash,
                    settledUtc = rec.SettledUtc, message = s.Message,
                });
            }
            case Core.Builds.SettlementOutcome.RejectedNotConfirmed:
                return Error(409, "confirmation_required", s.Message, new { quoteId, total = o.Quote?.Total });
            case Core.Builds.SettlementOutcome.RejectedWrongCar:
                return Error(409, "wrong_car", s.Message);
            case Core.Builds.SettlementOutcome.RejectedExpired:
                return Error(409, "quote_expired", s.Message);
            case Core.Builds.SettlementOutcome.RejectedPriceChanged:
                return Error(409, "price_changed", s.Message, new { currentLines = s.CurrentLines.Select(GarageWire.Line).ToList() });
            case Core.Builds.SettlementOutcome.RejectedStaleBuild:
                return Error(409, "stale_revision", s.Message);
            case Core.Builds.SettlementOutcome.RejectedUnavailable:
                return Error(409, "unavailable", s.Message);
            case Core.Builds.SettlementOutcome.RejectedIncompatible:
                return Error(409, "incompatible", s.Message);
            case Core.Builds.SettlementOutcome.RejectedAlreadyOwned:
                return Error(409, "already_owned", s.Message);
            case Core.Builds.SettlementOutcome.RejectedInsufficientFunds:
                return Error(409, "insufficient_funds", s.Message, new { total = o.Quote?.Total, balance = o.BalanceSeen });
            default: // RejectedNotApplicable
                return Error(409, "needs_repair", s.Message, new { repairs = s.Repairs.Select(GarageWire.Repair).ToList() });
        }
    }

    /// <summary>Pure decision run INSIDE the settlement transaction (may be re-run after a lost UNIQUE race).</summary>
    GarageSettleDecision<SettleOutcome> Decide(GarageSettleView view, bool confirmed, int shopAct, EventConstraints? constraints, DateTime now)
    {
        if (view.Quote is null || view.Instance is null || view.Workspace is null)
            return new GarageSettleDecision<SettleOutcome>
            {
                Result = new SettleOutcome(new SettlementResult { Outcome = Core.Builds.SettlementOutcome.RejectedWrongCar, Message = "The quote's car is not available." },
                    null, null, view.Balance, view.Instance),
            };
        PurchaseAndApplyQuote quote = JsonConvert.DeserializeObject<PurchaseAndApplyQuote>(view.Quote.QuoteJson, BuildJson.Settings)!;
        CarBuildWorkspace ws = ReadWorkspace(view.Workspace, view.Instance);
        var ledger = new InMemoryQuoteLedger();
        if (view.PriorRecordJson is { } prior) ledger.TryAdd(JsonConvert.DeserializeObject<SettlementRecord>(prior, BuildJson.Settings)!);
        BuildContext ctx = Context(view.Instance.CarId, view.Instance.InstanceId, view.OwnedParts, shopAct);
        SettlementResult s = PurchaseQuotes.Settle(quote, confirmed, ws, ctx, view.Balance, ledger, constraints, now);
        var outcome = new SettleOutcome(s, quote, s.Workspace, view.Balance, view.Instance);
        if (s.Outcome != Core.Builds.SettlementOutcome.Settled) return new GarageSettleDecision<SettleOutcome> { Result = outcome };
        CarBuildWorkspace next = s.Workspace;
        return new GarageSettleDecision<SettleOutcome>
        {
            Result = outcome,
            Commit = true,
            Debit = s.Debit,
            NewBalance = s.NewBalance,
            Grants = s.Grants.Select(id => new PartGrant(id, quote.Lines.First(l => l.PartId == id).Price)).ToList(),
            Workspace = Write(next),
            AppliedRevision = next.Applied.Revision,
            BuildHash = next.Applied.BuildHash,
            RecordJson = JsonConvert.SerializeObject(s.Record, BuildJson.Settings),
        };
    }

    // ================================================================== convoy / match integration

    /// <summary>
    /// <c>loadout.set</c>: the member's selected car instance (by id, or the account's instance of <paramref name="carId"/>)
    /// with the performance hash and PI computed HERE from its stored applied build, and the cosmetic hash of its stored
    /// applied livery (<see cref="EntrantAppearance.CosmeticHash"/>). Client performance and cosmetic claims are never used.
    /// </summary>
    public async Task<(LoadoutInfo? Loadout, ConvoyError? Error)> SelectionAsync(string account, string? carId, string? instanceId,
        CancellationToken ct)
    {
        IReadOnlyList<CarInstance> instances = await store.EnsureCarInstancesAsync(account, ct);
        CarInstance? instance = instanceId is not null
            ? instances.FirstOrDefault(i => i.InstanceId == instanceId && (carId is null || i.CarId == carId))
            : instances.Where(i => i.CarId == carId).OrderBy(i => i.Ordinal).FirstOrDefault();
        if (instance is null)
        {
            string name = carId is not null && Catalogue.TryCar(carId, out CarDef car) ? car.Name : "that car";
            return (null, new ConvoyError("not_owned", $"You do not own the {name}."));
        }
        (EntrantBuild? build, ConvoyError? error) = await FreezeAsync(account, instance.InstanceId, ct);
        if (build is null) return (null, error);
        return (new LoadoutInfo(instance.CarId, build.Pi, build.BuildHash, build.Appearance!.CosmeticHash, instance.InstanceId, build.AppliedRevision), null);
    }

    /// <summary>The member's selected instance re-resolved now (event.ready): null when the member has no instance selection.</summary>
    public async Task<(LoadoutInfo? Fresh, ConvoyError? Error)> FreshSelectionAsync(string account, CancellationToken ct)
    {
        if (convoys.LoadoutOf(account) is not { InstanceId: { } instanceId } current) return (null, null);
        (EntrantBuild? build, ConvoyError? error) = await FreezeAsync(account, instanceId, ct);
        return build is null
            ? (null, error)
            : (current with
            {
                CarPi = build.Pi, PerformanceHash = build.BuildHash, AppliedRevision = build.AppliedRevision, CosmeticHash = build.Appearance!.CosmeticHash,
            }, null);
    }

    /// <summary>
    /// The entrant's CURRENT applied build, re-resolved with Core from the stored workspace (never a draft or preview), with the
    /// applied appearance read from the same workspace (<see cref="EntrantBuild.Appearance"/>).
    /// </summary>
    public async Task<(EntrantBuild? Build, ConvoyError? Error)> FreezeAsync(string account, string instanceId, CancellationToken ct)
    {
        LoadedCar? c = await LoadAsync(account, instanceId, ct);
        if (c is null) return (null, new ConvoyError("not_owned", "That car instance is not yours."));
        (EntrantBuild? build, List<RepairItem> repairs) = Resolve(c);
        return build is not null
            ? (build with { Appearance = AppearanceOf(c.Workspace) }, null)
            : (null, new ConvoyError("loadout_illegal", "Your applied build needs repair in the Garage: " + string.Join("; ", repairs)));
    }

    /// <summary>
    /// The applied appearance of a workspace as the game server receives it: the stored applied livery re-read with Core
    /// (strict parse, catalogue validation for its car), its server-computed <see cref="LiveryHash"/> and compact
    /// <see cref="LiveryWire"/> form. No livery → the stock appearance (<c>Livery</c> null, the stock livery's hash). A stored
    /// livery that no longer resolves against the current customization catalogue races with the stock appearance (logged)
    /// rather than sending the game server something it cannot build.
    /// </summary>
    public EntrantAppearance AppearanceOf(CarBuildWorkspace ws)
    {
        string carId = ws.Car.ModelId;
        if (string.IsNullOrEmpty(ws.AppliedLivery)) return new EntrantAppearance(customization.StockHash(carId), null);
        LiveryParseResult parsed = LiveryJson.Parse(ws.AppliedLivery);
        if (parsed.Ok && LiveryValidator.Validate(parsed.Document, customization.Catalogue, carId, null, LiveryValidationMode.Preview).IsValid &&
            LiveryWire.EncodeProblem(parsed.Document) is null)
            return new EntrantAppearance(LiveryHash.Of(parsed.Document), LiveryWire.Encode(parsed.Document));
        log.LogWarning("Applied livery of {InstanceId} does not resolve against customization revision {Revision}; the stock appearance is used",
            ws.Car.InstanceId, customization.Revision);
        return new EntrantAppearance(customization.StockHash(carId), null);
    }

    /// <summary>The car a player brings to the meet: its model and applied livery (compact wire form, null = stock); null if not theirs.</summary>
    public async Task<(string CarId, string? Livery)?> MeetAppearanceAsync(string account, string instanceId, CancellationToken ct)
    {
        LoadedCar? c = await LoadAsync(account, instanceId, ct);
        if (c is null) return null;
        return (c.Workspace.Car.ModelId, AppearanceOf(c.Workspace).Livery);
    }

    /// <summary>Frozen builds of several entrants' selected instances (event.start); entrants without a valid build are omitted.</summary>
    public async Task<IReadOnlyDictionary<string, EntrantBuild>> FreezeSelectionsAsync(IEnumerable<string> accounts, CancellationToken ct)
    {
        var result = new Dictionary<string, EntrantBuild>(StringComparer.Ordinal);
        foreach (string account in accounts)
        {
            if (convoys.LoadoutOf(account) is not { InstanceId: { } instanceId }) continue;
            (EntrantBuild? build, _) = await FreezeAsync(account, instanceId, ct);
            if (build is not null) result[account] = build;
        }
        return result;
    }

    /// <summary>
    /// Last Race Build (Addendum 02 §9.3): Core GarageOperations.RecordRaceBegan with the FROZEN build the match was allocated
    /// with. Called once the game server acknowledged the assignment and the entrant was issued a racer ticket.
    /// </summary>
    public async Task<bool> RecordRaceBeganAsync(string account, EntrantBuild frozen, string matchId, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            LoadedCar? c = await LoadAsync(account, frozen.InstanceId, ct);
            if (c is null) return false;
            OperationResult r = GarageOperations.RecordRaceBegan(c.Workspace, DrivingSessionKind.FullSizeEvent, frozen.ToApplied(), matchId, Now);
            if (!r.Accepted)
            {
                log.LogWarning("Last Race Build not recorded for match {MatchId}: {Message}", matchId, r.Message);
                return false;
            }
            if (await store.SaveWorkspaceAsync(account, frozen.InstanceId, c.StoredRevision, Write(c.Workspace), ct) == WorkspaceSaveStatus.Saved)
                return true;
        }
        log.LogWarning("Last Race Build not recorded for match {MatchId}: the workspace kept changing", matchId);
        return false;
    }

    /// <summary>
    /// The Garage changed the member's SELECTED car: a new applied build (<paramref name="performance"/>) refreshes its hash/PI
    /// (a performance change unreadies only this member); every refresh also carries the server cosmetic hash of the applied
    /// livery, and a cosmetic-only change (livery-apply) keeps readiness (ConvoyDirectory.RefreshLoadoutFromGarage).
    /// </summary>
    object? RefreshConvoy(string account, CarInstance instance, CarBuildWorkspace ws, bool performance)
    {
        if (convoys.LoadoutOf(account) is not { } selected || selected.InstanceId != instance.InstanceId) return null;
        string cosmetic = AppearanceOf(ws).CosmeticHash;
        LoadoutInfo fresh = performance
            ? selected with { CarPi = ws.Applied.Pi, PerformanceHash = ws.Applied.BuildHash, AppliedRevision = ws.Applied.Revision, CosmeticHash = cosmetic }
            : selected with { CosmeticHash = cosmetic };
        bool unreadied = convoys.RefreshLoadoutFromGarage(account, fresh);
        return new { selected = true, performanceChanged = unreadied };
    }

    // ================================================================== loading and helpers

    EventConstraints? Constraints(string account, string instanceId) =>
        convoys.BuildFrozenFor(account, instanceId) is { } label ? new EventConstraints { BuildLocked = true, Label = label } : null;

    async Task<int> ShopActAsync(string account, CancellationToken ct)
    {
        IReadOnlyDictionary<string, MemberProgress> progress = await players.GetProgressAsync(new[] { account }, ct);
        bool[] normal = progress.TryGetValue(account, out MemberProgress? p) ? p.NormalCleared : new bool[Limits.CampaignStages];
        return ShopAvailability.ActForNormalFrontier(CampaignProgress.Frontier(normal), Catalogue.Stages);
    }

    BuildContext Context(string carId, string instanceId, IEnumerable<string> ownedParts, int shopAct)
    {
        var inventory = new PartInventory();
        foreach (string part in ownedParts) inventory.Grant(instanceId, part);
        return BuildContext.Create(Catalogue, garage.Parts, carId, inventory, shopAct);
    }

    async Task<LoadedCar?> LoadAsync(string account, string instanceId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(instanceId) || instanceId.Length > GarageWire.MaxIdLength) return null;
        CarInstance? instance = await store.GetCarInstanceAsync(account, instanceId, ct);
        if (instance is null || !Catalogue.TryCar(instance.CarId, out _)) return null;
        return await LoadAsync(instance, await ShopActAsync(account, ct), ct);
    }

    async Task<LoadedCar> LoadAsync(CarInstance instance, int shopAct, CancellationToken ct)
    {
        IReadOnlyCollection<string> owned = await store.GetOwnedPartsAsync(instance.InstanceId, ct);
        BuildContext ctx = Context(instance.CarId, instance.InstanceId, owned, shopAct);
        StoredWorkspace? stored = await store.GetWorkspaceAsync(instance.InstanceId, ct);
        if (stored is null)
        {
            // First use: the stock applied build (revision 1) with server-derived stats and an empty library (no invented presets).
            CarBuildWorkspace fresh = GarageOperations.NewWorkspace(instance.InstanceId, ctx, Now);
            await store.CreateWorkspaceAsync(instance.AccountId, instance.InstanceId, Write(fresh), ct);
            stored = await store.GetWorkspaceAsync(instance.InstanceId, ct)
                     ?? throw new InvalidOperationException("Workspace could not be created.");
        }
        return new LoadedCar
        {
            Instance = instance, Workspace = ReadWorkspace(stored, instance), StoredRevision = stored.Revision, Context = ctx, OwnedParts = owned,
            ShopAct = shopAct,
        };
    }

    CarBuildWorkspace ReadWorkspace(StoredWorkspace stored, CarInstance instance)
    {
        CarBuildWorkspace ws = BuildDocumentCodec.DeserializeWorkspace(stored.Json, garage.Parts, Now).Workspace;
        if (ws.Car.InstanceId != instance.InstanceId || ws.Car.ModelId != instance.CarId)
            throw new InvalidOperationException($"Stored workspace of {instance.InstanceId} names another car.");
        ws.Revision = stored.Revision; // the row revision is the concurrency token (written together with the document)
        return ws;
    }

    /// <summary>Re-resolves the applied build with Core: legal (owned, compatible, valid tune) → the frozen record.</summary>
    (EntrantBuild? Build, List<RepairItem> Repairs) Resolve(LoadedCar c)
    {
        CarBuildWorkspace ws = c.Workspace;
        BuildEvaluation ev = BuildEvaluator.Evaluate(ws.Applied.Build, ws.Car.InstanceId, c.Context);
        if (!ev.Resolved || ev.Repairs.Count > 0) return (null, ev.Repairs);
        return (EntrantBuild.From(ws.Car.InstanceId, ws.Car.ModelId, ws.Applied, ev.Spec, ev.Pi, garage.Hash), ev.Repairs);
    }

    static WorkspaceWrite Write(CarBuildWorkspace ws) =>
        new(ws.Revision, ws.SchemaVersion, BuildDocumentCodec.SerializeWorkspace(ws), ws.Applied.Revision, ws.Applied.BuildHash ?? "", ws.Applied.Pi);

    static GarageReply NotFound() => Error(404, "not_found", "No such car in your ONLINE garage.");

    public static GarageReply Error(int status, string code, string message, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["error"] = code, ["message"] = message };
        if (extra is IDictionary<string, object?> dict)
            foreach (var kv in dict) body[kv.Key] = kv.Value;
        else if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        return new GarageReply(status, body);
    }
}
