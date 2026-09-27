using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Profiles
{
    /// <summary>
    /// Per-car-INSTANCE part ownership read live from a Local profile (<see cref="OwnedCar.Parts"/>). A part owned by one
    /// instance is never owned by another, and <see cref="LocalProfile.UnassignedParts"/> never counts as ownership.
    /// </summary>
    public sealed class LocalPartOwnership : IPartOwnership
    {
        readonly LocalProfile profile;

        public LocalPartOwnership(LocalProfile profile)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public bool Owns(string carInstanceId, string partId)
        {
            OwnedCar car = carInstanceId == null ? null : profile.FindCar(carInstanceId);
            return car != null && car.OwnsPart(partId);
        }

        public IReadOnlyList<string> OwnedBy(string carInstanceId)
        {
            OwnedCar car = carInstanceId == null ? null : profile.FindCar(carInstanceId);
            return car?.Parts == null ? new List<string>() : car.Parts.Where(p => p != null).Select(p => p.PartId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Durable settled-quote lookup over a Local profile. A quote id is settled when parts bought with it are owned
    /// (<see cref="OwnedPart.Reference"/>); parts are never removed, so the lookup cannot expire like the bounded
    /// operation-id list can.
    /// </summary>
    public sealed class LocalQuoteLedger : IQuoteLedger
    {
        readonly LocalProfile profile;

        public LocalQuoteLedger(LocalProfile profile)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public bool TryGet(string quoteId, out SettlementRecord record)
        {
            record = null;
            if (string.IsNullOrEmpty(quoteId)) return false;
            foreach (OwnedCar car in profile.Cars)
            {
                List<OwnedPart> bought = (car.Parts ?? new List<OwnedPart>()).Where(p => p != null && p.Reference == quoteId).ToList();
                if (bought.Count == 0) continue;
                WalletEntry entry = profile.WalletHistory.LastOrDefault(w => w.Kind == LocalGarage.WalletKind && w.Reference == quoteId);
                record = new SettlementRecord
                {
                    QuoteId = quoteId, InstanceId = car.InstanceId, Debit = bought.Sum(p => p.PricePaid), Grants = bought.Select(p => p.PartId).ToList(),
                    BalanceAfter = entry?.BalanceAfter ?? 0, SettledUtc = bought[0].AcquiredUtc,
                };
                return true;
            }
            return false;
        }
    }

    /// <summary>What a Garage operation did to one car (attached to <see cref="LocalProgressionResult.Garage"/>).</summary>
    public sealed class LocalGarageOutcome
    {
        public string InstanceId = "";
        /// <summary>The car's workspace after the operation (a copy; the profile holds it as documents). Null when unreadable.</summary>
        public CarBuildWorkspace Workspace;
        /// <summary>Migration/repair explanations from reading the stored documents.</summary>
        public List<string> Notices = new List<string>();
        /// <summary>Buy and Apply: the authoritative settlement decision (outcome, debit, grants, current prices on a change).</summary>
        public SettlementResult Settlement;
        /// <summary>Record Last Race Build: the Builds operation result.</summary>
        public OperationResult Operation;
        public List<string> GrantedPartIds = new List<string>();
    }

    /// <summary>A car's build workspace as read from the profile (pure read: the profile is not changed).</summary>
    public sealed class LocalWorkspaceLoad
    {
        /// <summary>False when the car is unknown or its stored documents cannot be read (see <see cref="Reason"/>).</summary>
        public bool Ok => Workspace != null;
        public string Reason = "";
        public CarBuildWorkspace Workspace;
        /// <summary>Nothing was stored yet: a new stock workspace (revision 1) that is stored by the first save.</summary>
        public bool Created;
        /// <summary>The stored documents were upgraded on read (legacy loadouts, capacities); saving keeps the upgrade.</summary>
        public bool Migrated;
        /// <summary>Revision of the stored workspace (0 = none): pass it as SaveWorkspace's expectedStoredRevision.</summary>
        public long StoredRevision;
        public List<string> Notices = new List<string>();
    }

    /// <summary>
    /// The Local (offline) home of the Garage (Addendum 02 §8–10, D204/D205): per-instance part ownership, per-car build
    /// workspaces (≥ 8 named mechanical loadouts, ≥ 5 visual presets, Before Workshop / Before Last Apply / Last Race Build),
    /// Buy and Apply against the Local wallet, and Last Race Build when a Local race begins.
    /// <para>
    /// Same contract as <see cref="LocalProgression"/>: pure functions over (profile, catalogues, request) that return a new
    /// profile copy and an itemised <see cref="LocalProgressionResult"/>; the input is never modified, a rejection changes
    /// nothing, and the caller persists the returned profile atomically with <see cref="ProfileRepository.Save"/>. Workspace
    /// documents are stored in <see cref="OwnedCar.Workspace"/> through <see cref="BuildDocumentCodec"/> (which also migrates
    /// legacy documents).
    /// </para>
    /// <para>
    /// Nothing here changes RP, records, campaign clears, challenges, course rights or soundtrack, and owning or applying a
    /// part never does either. The Test Yard has no entry point here: driving it records nothing and earns nothing.
    /// </para>
    /// </summary>
    public static class LocalGarage
    {
        /// <summary>Wallet history kind of a Buy-and-Apply debit (reference = quote id).</summary>
        public const string WalletKind = "part-purchase";
        /// <summary><see cref="OwnedPart.Source"/> of a part bought with Buy and Apply.</summary>
        public const string PurchaseSource = "buy-and-apply";
        public const string MigratedSourcePrefix = "migrated:";

        static readonly JsonSerializer BuildSerializer = JsonSerializer.Create(BuildJson.Settings);

        // ---------------- ownership and build context ----------------

        public static IPartOwnership Ownership(LocalProfile profile) => new LocalPartOwnership(profile);

        /// <summary>Highest act whose parts shop is open: the act of the Normal campaign frontier (4 once Normal is complete).</summary>
        public static int ShopAct(LocalProfile profile, ContentCatalogue catalogue)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            int frontier = CampaignProgress.Frontier(profile.Campaign.Flags(CampaignMode.Normal));
            return ShopAvailability.ActForNormalFrontier(frontier, catalogue.Stages);
        }

        /// <summary>Build context for one owned instance: its model, live Local ownership and the frontier's shop act.</summary>
        public static BuildContext Context(LocalProfile profile, ContentCatalogue catalogue, PartsCatalogue parts, string instanceId)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            OwnedCar car = instanceId == null ? null : profile.FindCar(instanceId);
            if (car == null) throw new KeyNotFoundException($"Car instance {instanceId} is not in this Local garage.");
            return BuildContext.Create(catalogue, parts, car.ModelId, new LocalPartOwnership(profile), ShopAct(profile, catalogue));
        }

        // ---------------- workspace storage ----------------

        /// <summary>
        /// Reads one car's <see cref="CarBuildWorkspace"/> from its stored documents (legacy documents are migrated by
        /// <see cref="BuildDocumentCodec.FromDocuments"/>; nothing is dropped), or a new stock workspace when nothing is stored
        /// yet. Pure: the profile is not changed; store changes with <see cref="SaveWorkspace"/>.
        /// </summary>
        public static LocalWorkspaceLoad LoadWorkspace(LocalProfile profile, ContentCatalogue catalogue, PartsCatalogue parts, string instanceId, DateTime utc)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            var load = new LocalWorkspaceLoad();
            OwnedCar car = instanceId == null ? null : profile.FindCar(instanceId);
            if (car == null)
            {
                load.Reason = $"Car instance {instanceId} is not in this Local garage.";
                return load;
            }
            if (!catalogue.TryCar(car.ModelId, out _) || !catalogue.CarTunings.ContainsKey(car.ModelId))
            {
                load.Reason = $"Car model {car.ModelId} is not in this content catalogue; its Garage documents are kept unchanged.";
                return load;
            }
            BuildContext ctx = Context(profile, catalogue, parts, instanceId);
            CarWorkspace stored = car.Workspace ?? new CarWorkspace();
            if (stored.IsEmpty)
            {
                load.Workspace = GarageOperations.NewWorkspace(car.InstanceId, ctx, utc);
                load.Created = true;
                return load;
            }
            load.StoredRevision = StoredRevision(stored);
            try
            {
                MigrationResult m = BuildDocumentCodec.FromDocuments(car.InstanceId, car.ModelId, ToStoredDocuments(stored), parts, utc);
                CarBuildWorkspace ws = m.Workspace;
                load.Notices.AddRange(m.Notices);
                if (stored.AppliedBuild == null && ws.Applied.Build.ContentEquals(MechanicalSnapshot.Stock()))
                {
                    AppliedVehicleBuild fresh = GarageOperations.NewWorkspace(car.InstanceId, ctx, utc).Applied;
                    ws.Applied.BuildHash = fresh.BuildHash;
                    ws.Applied.Pi = fresh.Pi;
                    ws.Applied.PiClass = fresh.PiClass;
                    ws.Applied.HandlingModelVersion = fresh.HandlingModelVersion;
                    ws.Applied.PartsCatalogueRevision = fresh.PartsCatalogueRevision;
                    load.Notices.Add("No applied build was stored; this car starts from its stock build.");
                }
                // A read that upgraded the documents is a change of its own: the next save must be able to store it.
                if (!SameDocuments(ToCarWorkspace(ws, stored), stored))
                {
                    load.Migrated = true;
                    ws.Revision = Math.Max(ws.Revision, load.StoredRevision) + 1;
                    load.Notices.Add("Stored build documents were upgraded on read; save the Garage to keep the upgrade.");
                }
                load.Workspace = ws;
            }
            catch (Exception e) when (e is JsonException || e is BuildDataException || e is ArgumentException || e is FormatException ||
                                      e is InvalidCastException || e is OverflowException || e is InvalidOperationException)
            {
                load.Reason = $"The stored build documents of {car.InstanceId} could not be read ({e.Message}). They are kept unchanged.";
            }
            return load;
        }

        /// <summary>
        /// Stores a car's workspace (no wallet, ownership or progression change). Conflict-checked like the Online store:
        /// <paramref name="expectedStoredRevision"/> (from <see cref="LocalWorkspaceLoad.StoredRevision"/>) must match when
        /// given, the workspace revision must be newer than the stored one, and a stale or divergent copy is refused so it
        /// can never overwrite an accepted setup. Saving the identical workspace again is AlreadyApplied (idempotent by
        /// revision; garage saves deliberately do not use the bounded operation-id list). A changed applied build may only
        /// use parts THIS instance owns: a workspace can never smuggle a preview part into the applied (race) build.
        /// </summary>
        public static LocalProgressionResult SaveWorkspace(LocalProfile profile, string instanceId, CarBuildWorkspace workspace, long? expectedStoredRevision = null)
        {
            LocalProgressionResult result = LocalProgression.Begin(profile);
            OwnedCar car = instanceId == null ? null : profile.FindCar(instanceId);
            if (car == null) return LocalProgression.Reject(result, $"Car instance {instanceId} is not in this Local garage.");
            result.Garage = new LocalGarageOutcome { InstanceId = car.InstanceId };
            if (workspace == null) return LocalProgression.Reject(result, "No workspace to save.");
            string invalid = WorkspaceProblem(car, workspace);
            if (invalid != null) return LocalProgression.Reject(result, invalid);

            CarWorkspace stored = car.Workspace ?? new CarWorkspace();
            long storedRevision = StoredRevision(stored);
            if (expectedStoredRevision.HasValue && expectedStoredRevision.Value != storedRevision)
                return LocalProgression.Reject(result, $"This car's Garage changed since it was loaded (stored revision {storedRevision}, expected {expectedStoredRevision.Value}). Reload before saving.");
            CarWorkspace next = ToCarWorkspace(workspace, stored);
            if (SameDocuments(next, stored))
            {
                result.Garage.Workspace = workspace.Clone();
                return LocalProgression.Already(result, "Nothing to save: the stored workspace is identical.");
            }
            if (workspace.Revision <= storedRevision)
                return LocalProgression.Reject(result, $"Not saved: this copy (revision {workspace.Revision}) is not newer than the stored Garage (revision {storedRevision}). Reload before editing.");

            AppliedVehicleBuild storedApplied;
            try
            {
                storedApplied = StoredApplied(stored);
            }
            catch (Exception e) when (e is JsonException || e is ArgumentException || e is FormatException || e is InvalidCastException)
            {
                return LocalProgression.Reject(result, $"Not saved: the stored applied build could not be read ({e.Message}); it is kept unchanged.");
            }
            MechanicalSnapshot storedBuild = storedApplied?.Build ?? MechanicalSnapshot.Stock();
            if (storedApplied != null && workspace.Applied.Revision < storedApplied.Revision)
                return LocalProgression.Reject(result, $"Not saved: the applied build would go back from revision {storedApplied.Revision} to {workspace.Applied.Revision}.");
            if (!workspace.Applied.Build.ContentEquals(storedBuild))
            {
                List<string> unowned = workspace.Applied.Build.AllPartIds().Where(id => !car.OwnsPart(id)).Distinct(StringComparer.Ordinal).ToList();
                if (unowned.Count > 0)
                    return LocalProgression.Reject(result, $"Not saved: the applied build uses parts this car does not own ({string.Join(", ", unowned)}). Buy and Apply them, or keep them in the draft.");
            }

            LocalProfile p = ProfileJson.Clone(profile);
            p.FindCar(car.InstanceId).Workspace = next;
            LocalProgression.Add(result, ProgressionChangeKind.WorkspaceSaved, car.InstanceId, workspace.Revision, $"Garage saved (revision {workspace.Revision}).");
            result.Garage.Workspace = workspace.Clone();
            result.Status = LocalOperationStatus.Applied;
            return LocalProgression.Finish(result, p);
        }

        // ---------------- Buy and Apply ----------------

        /// <summary>
        /// Explicit Buy-and-Apply quote for a candidate on the stored workspace (Addendum 02 §10.4): the exact missing parts at
        /// current prices, pinned to the stored applied revision. Selecting or testing a candidate never charges anything.
        /// </summary>
        public static QuoteResult Quote(LocalProfile profile, ContentCatalogue catalogue, PartsCatalogue parts, string instanceId,
            MechanicalSnapshot candidate, DateTime utc, EventConstraints constraints = null)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            LocalWorkspaceLoad load = LoadWorkspace(profile, catalogue, parts, instanceId, utc);
            if (!load.Ok) return new QuoteResult { Status = QuoteStatus.NotPurchasable, WalletBalance = profile.WalletBalance, Message = load.Reason };
            return PurchaseQuotes.Create(load.Workspace, candidate, Context(profile, catalogue, parts, instanceId), profile.WalletBalance, constraints, utc);
        }

        /// <summary>
        /// Settles a confirmed quote with <see cref="PurchaseQuotes.Settle"/> against the STORED workspace and the Local wallet,
        /// then commits in one profile copy: debit exactly the settled amount, grant the parts to THIS instance only, store the
        /// newly applied workspace (Before Last Apply = the prior applied build) and record the wallet entry — or nothing.
        /// Replaying <paramref name="operationId"/>, or settling an already settled quote under another id, is AlreadyApplied
        /// with no second charge or grant. Insufficient funds, a stale or expired quote, a price/catalogue change, a locked,
        /// incompatible or already owned part leave the profile untouched (the planning draft stays available).
        /// </summary>
        public static LocalProgressionResult BuyAndApply(LocalProfile profile, ContentCatalogue catalogue, PartsCatalogue parts, string instanceId,
            PurchaseAndApplyQuote quote, bool confirmed, string operationId, DateTime utc, EventConstraints constraints = null)
        {
            LocalProgressionResult result = LocalProgression.Begin(profile);
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            if (!LocalProgression.IsValidOperationId(operationId)) return LocalProgression.Reject(result, "Buy and Apply needs an operation id.");
            if (profile.HasApplied(operationId)) return LocalProgression.Already(result, "This purchase was already completed; nothing more was charged.");
            if (quote == null) return LocalProgression.Reject(result, "Buy and Apply needs a quote.");
            OwnedCar car = instanceId == null ? null : profile.FindCar(instanceId);
            if (car == null) return LocalProgression.Reject(result, $"Car instance {instanceId} is not in this Local garage.");
            result.Garage = new LocalGarageOutcome { InstanceId = car.InstanceId };
            if (quote.InstanceId != car.InstanceId || quote.ModelId != car.ModelId)
                return LocalProgression.Reject(result, "This quote is for a different car; nothing was charged.");

            LocalWorkspaceLoad load = LoadWorkspace(profile, catalogue, parts, instanceId, utc);
            if (!load.Ok) return LocalProgression.Reject(result, load.Reason);
            result.Garage.Notices.AddRange(load.Notices);
            result.Garage.Workspace = load.Workspace;
            BuildContext ctx = Context(profile, catalogue, parts, instanceId);
            SettlementResult s = PurchaseQuotes.Settle(quote, confirmed, load.Workspace, ctx, profile.WalletBalance, new LocalQuoteLedger(profile), constraints, utc);
            result.Garage.Settlement = s;
            if (s.Outcome == SettlementOutcome.AlreadySettled)
                return LocalProgression.Already(result, "This quote was already settled; nothing more was charged or granted.");
            if (!s.Success) return LocalProgression.Reject(result, s.Message);
            if (s.Workspace == null || s.Grants.Count == 0 || s.Debit != quote.Total)
                return LocalProgression.Reject(result, "The settlement is incomplete; nothing was charged.");

            LocalProfile p = ProfileJson.Clone(profile);
            if (!Wallet.TryDebit(p.WalletBalance, s.Debit, out long balance) || balance != s.NewBalance)
                return LocalProgression.Reject(result, "The wallet changed during the purchase; nothing was charged.");
            OwnedCar target = p.FindCar(car.InstanceId);
            var lines = new List<QuoteLine>();
            foreach (string partId in s.Grants)
            {
                if (target.OwnsPart(partId)) return LocalProgression.Reject(result, $"{partId} is already owned by this car and is never bought again; nothing was charged.");
                QuoteLine line = quote.Lines.First(l => l.PartId == partId);
                target.Parts.Add(new OwnedPart
                {
                    PartId = partId, Quantity = 1, Source = PurchaseSource, Reference = quote.QuoteId, PricePaid = line.Price, AcquiredUtc = utc,
                });
                lines.Add(line);
            }
            p.WalletBalance = balance;
            target.Workspace = ToCarWorkspace(s.Workspace, target.Workspace);
            LocalProgression.History(p, utc, WalletKind, quote.QuoteId, -s.Debit, -s.Debit, 0);
            LocalProgression.MarkApplied(p, operationId);
            IReadOnlyList<string> errors = p.Validate();
            if (errors.Count > 0) return LocalProgression.Reject(result, "Not applied: " + errors[0]);

            LocalProgression.Add(result, ProgressionChangeKind.Debit, quote.QuoteId, -s.Debit, $"Bought {s.Grants.Count} part(s).");
            foreach (QuoteLine line in lines)
                LocalProgression.Add(result, ProgressionChangeKind.PartGranted, line.PartId, line.Price, $"{line.Name} is now owned by this car only.");
            LocalProgression.Add(result, ProgressionChangeKind.BuildApplied, car.InstanceId, s.Workspace.Applied.Revision,
                $"Applied revision {s.Workspace.Applied.Revision} (PI {s.Workspace.Applied.Pi} {s.Workspace.Applied.PiClass}).");
            result.Notes.Add("Parts belong to this car only. Owning or applying parts never changes RP, records, clears, challenges or soundtrack.");
            result.Garage.Workspace = s.Workspace.Clone();
            result.Garage.GrantedPartIds.AddRange(s.Grants);
            result.Status = LocalOperationStatus.Applied;
            return LocalProgression.Finish(result, p);
        }

        // ---------------- Last Race Build ----------------

        /// <summary>The build a Local race on this car uses: a frozen copy of the STORED applied build (never the draft).</summary>
        public static AppliedVehicleBuild FrozenRaceBuild(LocalProfile profile, ContentCatalogue catalogue, PartsCatalogue parts, string instanceId, DateTime utc)
        {
            LocalWorkspaceLoad load = LoadWorkspace(profile, catalogue, parts, instanceId, utc);
            return load.Ok ? load.Workspace.Applied.Clone() : null;
        }

        /// <summary>
        /// Records Last Race Build (<see cref="GarageOperations.RecordRaceBegan"/>) when a Local full-size race actually begins
        /// driving with <paramref name="frozenBuild"/>. Test Yard, toys and menus are refused. Idempotent per event id (the
        /// same event and build again is AlreadyApplied). It does NOT mark <paramref name="eventId"/> as an applied operation:
        /// the event's result is still settled by <see cref="LocalProgression.ApplyEvent"/> under that id.
        /// </summary>
        public static LocalProgressionResult RecordLocalRaceBuild(LocalProfile profile, ContentCatalogue catalogue, PartsCatalogue parts, string instanceId,
            AppliedVehicleBuild frozenBuild, string eventId, DateTime utc, DrivingSessionKind kind = DrivingSessionKind.FullSizeEvent)
        {
            LocalProgressionResult result = LocalProgression.Begin(profile);
            if (!LocalProgression.IsValidOperationId(eventId)) return LocalProgression.Reject(result, "Last Race Build needs the race's event id.");
            OwnedCar car = instanceId == null ? null : profile.FindCar(instanceId);
            if (car == null) return LocalProgression.Reject(result, $"Car instance {instanceId} is not in this Local garage.");
            result.Garage = new LocalGarageOutcome { InstanceId = car.InstanceId };
            if (kind != DrivingSessionKind.FullSizeEvent) return LocalProgression.Reject(result, $"{kind} never records Last Race Build.");
            if (frozenBuild?.Build == null) return LocalProgression.Reject(result, "No frozen race build.");

            LocalWorkspaceLoad load = LoadWorkspace(profile, catalogue, parts, instanceId, utc);
            if (!load.Ok) return LocalProgression.Reject(result, load.Reason);
            CarBuildWorkspace ws = load.Workspace;
            result.Garage.Notices.AddRange(load.Notices);
            result.Garage.Workspace = ws;
            BuildReference existing = ws.Reference(BuildReferenceKind.LastRaceBuild);
            if (existing != null && existing.Context == eventId && existing.SourceAppliedRevision == frozenBuild.Revision && existing.Build.ContentEquals(frozenBuild.Build))
                return LocalProgression.Already(result, "Last Race Build is already recorded for this race.");
            if (frozenBuild.Revision == ws.Applied.Revision && !frozenBuild.Build.ContentEquals(ws.Applied.Build))
                return LocalProgression.Reject(result, "The frozen race build is not this car's applied build of that revision.");
            List<string> unowned = frozenBuild.Build.AllPartIds().Where(id => !car.OwnsPart(id)).Distinct(StringComparer.Ordinal).ToList();
            if (unowned.Count > 0)
                return LocalProgression.Reject(result, $"A preview build cannot race: this car does not own {string.Join(", ", unowned)}.");

            OperationResult op = GarageOperations.RecordRaceBegan(ws, kind, frozenBuild, eventId, utc);
            result.Garage.Operation = op;
            if (!op.Accepted) return LocalProgression.Reject(result, op.Message);
            LocalProfile p = ProfileJson.Clone(profile);
            OwnedCar target = p.FindCar(car.InstanceId);
            target.Workspace = ToCarWorkspace(ws, target.Workspace);
            LocalProgression.Add(result, ProgressionChangeKind.RaceBuildRecorded, car.InstanceId, frozenBuild.Revision, $"Last Race Build: applied revision {frozenBuild.Revision} ({eventId}).");
            result.Garage.Workspace = ws.Clone();
            result.Status = LocalOperationStatus.Applied;
            return LocalProgression.Finish(result, p);
        }

        // ---------------- profile schema migration ----------------

        /// <summary>
        /// Profile schema 1 → 2 (registered in <see cref="ProfileMigrations.Default"/>). v1 kept performance-part stacks at
        /// profile level ("parts", reserved and never granted by a v1 build); v2 owns parts per car instance. A stack moves to
        /// a car only when that is unambiguous — the profile has exactly one car — as one owned entry (source
        /// "migrated:{v1 source}"); a quantity above one leaves the rest unassigned. Everything else (several or no cars,
        /// malformed or empty stacks) is preserved verbatim in "unassignedParts", which grants no ownership. Nothing is dropped.
        /// </summary>
        public static JObject MigrateV1ToV2(JObject doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            JArray legacy = doc["parts"] as JArray ?? new JArray();
            doc.Remove("parts");
            JArray unassigned = doc["unassignedParts"] as JArray ?? new JArray();
            List<JObject> cars = (doc["cars"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            foreach (JObject car in cars)
                if (!(car["parts"] is JArray)) car["parts"] = new JArray();
            JArray soleCarParts = cars.Count == 1 ? (JArray)cars[0]["parts"] : null;

            foreach (JToken token in legacy)
            {
                var stack = token as JObject;
                string partId = stack?["partId"]?.Type == JTokenType.String ? (string)stack["partId"] : null;
                JToken q = stack?["quantity"];
                long quantity = q == null || q.Type == JTokenType.Null ? 1 : q.Type == JTokenType.Integer ? (long)q : 0;
                bool attribute = soleCarParts != null && !string.IsNullOrEmpty(partId) && quantity >= 1 &&
                                 !soleCarParts.OfType<JObject>().Any(o => o["partId"]?.Type == JTokenType.String && (string)o["partId"] == partId);
                if (!attribute)
                {
                    unassigned.Add(token.DeepClone());
                    continue;
                }
                var owned = (JObject)stack.DeepClone();
                string source = stack["source"]?.Type == JTokenType.String ? (string)stack["source"] : "";
                owned["quantity"] = 1;
                owned["source"] = MigratedSourcePrefix + source;
                if (owned["reference"] == null) owned["reference"] = "";
                if (owned["pricePaid"] == null) owned["pricePaid"] = 0;
                soleCarParts.Add(owned);
                if (quantity > 1)
                {
                    var rest = (JObject)stack.DeepClone();
                    rest["quantity"] = quantity - 1;
                    unassigned.Add(rest);
                }
            }
            doc["unassignedParts"] = unassigned;
            return doc;
        }

        // ---------------- helpers ----------------

        static string WorkspaceProblem(OwnedCar car, CarBuildWorkspace ws)
        {
            if (ws.Schema != CarBuildWorkspace.SchemaId || ws.SchemaVersion != CarBuildWorkspace.CurrentSchemaVersion)
                return $"Not a current car build workspace ({ws.Schema}@{ws.SchemaVersion}).";
            if (ws.Car == null || ws.Car.InstanceId != car.InstanceId) return "This workspace belongs to a different car instance.";
            if (ws.Car.ModelId != car.ModelId) return $"This workspace is for a {ws.Car.ModelId}, not a {car.ModelId}.";
            if (ws.Revision < 1) return "Workspace revision must be positive.";
            if (ws.Applied?.Build == null || ws.Loadouts == null || ws.VisualPresets == null || ws.References == null || ws.Workshop == null)
                return "The workspace is incomplete.";
            if (ws.LoadoutCapacity < CarBuildWorkspace.MinLoadoutSlots || ws.Loadouts.Count > ws.LoadoutCapacity)
                return $"Loadouts must fit a capacity of at least {CarBuildWorkspace.MinLoadoutSlots}.";
            if (ws.VisualPresetCapacity < CarBuildWorkspace.MinVisualPresetSlots || ws.VisualPresets.Count > ws.VisualPresetCapacity)
                return $"Visual presets must fit a capacity of at least {CarBuildWorkspace.MinVisualPresetSlots}.";
            if (ws.Loadouts.Any(l => l?.Build == null || string.IsNullOrEmpty(l.LoadoutId) || string.IsNullOrEmpty(l.Name)))
                return "Every loadout needs an id, a name and a build.";
            if (ws.Loadouts.Select(l => l.LoadoutId).Distinct(StringComparer.Ordinal).Count() != ws.Loadouts.Count ||
                ws.Loadouts.Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ws.Loadouts.Count)
                return "Loadout ids and names must be unique on this car.";
            if (ws.Loadouts.Any(l => l.CarInstanceId != car.InstanceId)) return "A loadout of another car instance cannot be stored here (copy it as a plan instead).";
            if (ws.Loadouts.Count(l => l.Pinned) > CarBuildWorkspace.MaxPinnedLoadouts) return $"At most {CarBuildWorkspace.MaxPinnedLoadouts} pinned loadouts per car.";
            if (ws.VisualPresets.Any(v => v == null || string.IsNullOrEmpty(v.PresetId)) ||
                ws.VisualPresets.Select(v => v.PresetId).Distinct(StringComparer.Ordinal).Count() != ws.VisualPresets.Count)
                return "Visual preset ids must be present and unique.";
            foreach (KeyValuePair<string, BuildReference> kv in ws.References)
                if (!BuildReferenceKinds.TryParse(kv.Key, out _) || kv.Value?.Build == null) return $"Unknown or empty protected reference '{kv.Key}'.";
            return null;
        }

        static long StoredRevision(CarWorkspace stored)
        {
            if (stored == null || stored.IsEmpty) return 0;
            if (stored.WorkspaceState?.Data is JObject o && o["revision"] != null && o["revision"].Type == JTokenType.Integer) return (long)o["revision"];
            return 1; // documents without a state document read as revision 1 (BuildDocumentCodec.FromDocuments)
        }

        static AppliedVehicleBuild StoredApplied(CarWorkspace stored)
        {
            VersionedDocument d = stored?.AppliedBuild;
            if (d?.Data == null || d.Schema != BuildDocumentCodec.AppliedSchema) return null;
            return d.Data.ToObject<AppliedVehicleBuild>(BuildSerializer);
        }

        static StoredBuildDocuments ToStoredDocuments(CarWorkspace w)
        {
            var d = new StoredBuildDocuments();
            foreach (NamedDocument n in w.MechanicalLoadouts ?? new List<NamedDocument>())
                d.MechanicalLoadouts.Add(Stored(n?.SlotId, n?.Name, n?.Document));
            foreach (NamedDocument n in w.VisualPresets ?? new List<NamedDocument>())
                d.VisualPresets.Add(Stored(n?.SlotId, n?.Name, n?.Document));
            foreach (KeyValuePair<string, VersionedDocument> kv in w.References ?? new Dictionary<string, VersionedDocument>())
                d.References[kv.Key] = Stored(kv.Key, kv.Key, kv.Value);
            if (w.AppliedBuild != null) d.AppliedBuild = Stored("applied", "applied", w.AppliedBuild);
            if (w.GarageDraft != null) d.GarageDraft = Stored("draft", "draft", w.GarageDraft);
            if (w.WorkspaceState != null) d.WorkspaceState = Stored("state", "state", UtcStateDates(w.WorkspaceState));
            return d;
        }

        static StoredBuildDocument Stored(string slotId, string name, VersionedDocument doc) => new StoredBuildDocument
        {
            SlotId = slotId ?? "",
            Name = name ?? "",
            Schema = doc?.Schema ?? "",
            SchemaVersion = doc?.SchemaVersion ?? 0,
            UpdatedUtc = doc?.UpdatedUtc ?? default(DateTime),
            Data = doc?.Data?.DeepClone(),
        };

        /// <summary>
        /// Profile documents keep dates as ISO strings; the codec reads the workshop time with a culture/local-time
        /// conversion, so hand it an explicit UTC value instead.
        /// </summary>
        static VersionedDocument UtcStateDates(VersionedDocument state)
        {
            VersionedDocument c = state.Clone();
            if (c.Data is JObject o && o["workshopOpenedUtc"] is JValue v && v.Type == JTokenType.String &&
                DateTime.TryParse((string)v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc))
                o["workshopOpenedUtc"] = new JValue(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            return c;
        }

        /// <summary>The workspace as the profile stores it: one versioned document per loadout/preset/reference/applied/draft/state.</summary>
        static CarWorkspace ToCarWorkspace(CarBuildWorkspace ws, CarWorkspace previous)
        {
            StoredBuildDocuments d = BuildDocumentCodec.ToDocuments(ws);
            var w = new CarWorkspace();
            if (previous?.Extra != null)
                foreach (KeyValuePair<string, JToken> kv in previous.Extra) w.Extra[kv.Key] = kv.Value?.DeepClone();
            foreach (StoredBuildDocument s in d.MechanicalLoadouts)
                w.MechanicalLoadouts.Add(new NamedDocument { SlotId = s.SlotId, Name = s.Name, Document = Versioned(s) });
            foreach (StoredBuildDocument s in d.VisualPresets)
                w.VisualPresets.Add(new NamedDocument { SlotId = s.SlotId, Name = s.Name, Document = Versioned(s) });
            foreach (KeyValuePair<string, StoredBuildDocument> kv in d.References)
                w.References[kv.Key] = Versioned(kv.Value);
            w.AppliedBuild = Versioned(d.AppliedBuild);
            w.GarageDraft = d.GarageDraft == null ? null : Versioned(d.GarageDraft);
            w.WorkspaceState = Versioned(d.WorkspaceState);
            return w;
        }

        static VersionedDocument Versioned(StoredBuildDocument s) => new VersionedDocument
        {
            Schema = s.Schema, SchemaVersion = s.SchemaVersion, UpdatedUtc = s.UpdatedUtc, Data = Persisted(s.Data),
        };

        /// <summary>Exactly what a save and load would give back (dates as ISO-8601 UTC strings), so copies compare equal.</summary>
        static JToken Persisted(JToken token)
        {
            if (token == null) return null;
            using (var reader = new JsonTextReader(new StringReader(ProfileJson.Serialize(token, indented: false))))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                reader.MaxDepth = 128;
                return JToken.ReadFrom(reader);
            }
        }

        static bool SameDocuments(CarWorkspace a, CarWorkspace b) =>
            string.Equals(ProfileJson.Serialize(a, indented: false), ProfileJson.Serialize(b ?? new CarWorkspace(), indented: false), StringComparison.Ordinal);
    }
}
