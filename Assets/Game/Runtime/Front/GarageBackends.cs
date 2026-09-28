using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Customization;
using NightSignal.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace NightSignal.Front
{
    /// <summary>One owned car instance as the Garage lists it.</summary>
    public sealed class GarageCarRef
    {
        public string InstanceId = "";
        public string ModelId = "";
    }

    /// <summary>What the Garage shows for one car: the Core workspace and the context its evaluations use.</summary>
    public sealed class GarageState
    {
        public CarBuildWorkspace Workspace;
        public BuildContext Context;
        /// <summary>Online: the applied build is locked for an event being allocated or raced.</summary>
        public bool BuildLocked;
        public string Notice = "";
    }

    /// <summary>One Core GarageOperations call, described so either domain can run it.</summary>
    public sealed class GarageOp
    {
        /// <summary>
        /// edit-draft | apply | discard-draft | save-as | load-into-draft | begin-workshop | end-workshop | livery-apply |
        /// visual-preset-save | visual-preset-update | visual-preset-rename | visual-preset-delete
        /// </summary>
        public string Kind = "";
        public MechanicalSnapshot Build;
        public string Name, Note, ConfirmationToken;
        public DraftSource Source;
        /// <summary>livery-apply: the canonical livery JSON ("" = back to stock) and the preset it came from ("" = edited).</summary>
        public string LiveryJson, PresetId;
        /// <summary>visual-preset-save/-update: the preset payload.</summary>
        public string PayloadSchema, PayloadJson;
    }

    public sealed class GarageAnswer
    {
        public bool Accepted;
        /// <summary>A workshop challenge this operation completed (CH50), as a notice; "" = none.</summary>
        public string Challenge = "";
        public bool ConfirmationRequired;
        public string ConfirmationToken;
        public string Message = "";
        public List<string> Repairs = new List<string>();
        /// <summary>The car after the call (accepted or not, when known).</summary>
        public GarageState State;
    }

    public sealed class GarageQuote
    {
        public bool Ok;
        public string Message = "";
        public string QuoteId = "";
        public long Total;
        public List<string> Lines = new List<string>();
        /// <summary>Local: the Core quote to settle.</summary>
        public PurchaseAndApplyQuote Core;
    }

    /// <summary>
    /// Where the Garage's decisions happen. Local: Core runs in-process against the Local profile and saves atomically.
    /// Online: the control plane runs the same Core operations (docs/NETWORKING.md §2.6) and answers with the workspace.
    /// Callbacks may run at once (Local) or later on the main thread (Online).
    /// </summary>
    public abstract class GarageBackend
    {
        public abstract bool Online { get; }
        public abstract long Balance { get; }
        /// <summary>Stable key of the profile/account (per-device notes).</summary>
        public abstract string OwnerKey { get; }
        public abstract void Cars(Action<List<GarageCarRef>, string> done);
        public abstract void Load(string instanceId, Action<GarageState, string> done);
        public abstract void Run(string instanceId, CarBuildWorkspace ws, GarageOp op, Action<GarageAnswer> done);
        public abstract void Quote(string instanceId, CarBuildWorkspace ws, MechanicalSnapshot build, Action<GarageQuote> done);
        public abstract void Settle(string instanceId, GarageQuote quote, Action<GarageAnswer> done);
        /// <summary>Cosmetic ids this owner may apply (liveries may try on others; applying needs them owned).</summary>
        public abstract IEnumerable<string> OwnedCosmetics();
    }

    // ================================================================== Local

    public sealed class LocalGarageBackend : GarageBackend
    {
        readonly ContentCatalogue cat;
        readonly PartsCatalogue parts;
        readonly Dictionary<string, long> storedRevision = new Dictionary<string, long>();
        LocalSession L => LocalSession.Current;

        public LocalGarageBackend(ContentCatalogue cat, PartsCatalogue parts)
        {
            this.cat = cat;
            this.parts = parts;
        }

        public override bool Online => false;
        public override long Balance => L?.Profile?.WalletBalance ?? 0;
        public override string OwnerKey => "local." + (L?.Profile?.ProfileId ?? "");

        public override void Cars(Action<List<GarageCarRef>, string> done) =>
            done(L?.Profile?.Cars.Select(c => new GarageCarRef { InstanceId = c.InstanceId, ModelId = c.ModelId }).ToList() ?? new List<GarageCarRef>(), "");

        public override void Load(string instanceId, Action<GarageState, string> done)
        {
            LocalWorkspaceLoad load = LocalGarage.LoadWorkspace(L.Profile, cat, parts, instanceId, DateTime.UtcNow);
            if (!load.Ok)
            {
                done(null, load.Reason);
                return;
            }
            storedRevision[instanceId] = load.StoredRevision;
            BuildContext ctx = LocalGarage.Context(L.Profile, cat, parts, instanceId);
            // A catalogue or handling revision may have changed since this car was stored: re-derive and keep notices.
            OperationResult rv = GarageOperations.Revalidate(load.Workspace, ctx, DateTime.UtcNow);
            string notice = load.Notices.Count > 0 ? string.Join(" ", load.Notices) : rv.Changes.Count > 0 ? string.Join(" ", rv.Changes) : "";
            done(new GarageState { Workspace = load.Workspace, Context = ctx, Notice = notice }, "");
        }

        public override void Run(string instanceId, CarBuildWorkspace ws, GarageOp op, Action<GarageAnswer> done)
        {
            BuildContext ctx = LocalGarage.Context(L.Profile, cat, parts, instanceId);
            DateTime now = DateTime.UtcNow;
            string presetBefore = ws.AppliedVisualPresetId ?? "", hashBefore = ws.AppliedLiveryHash ?? "";
            long revisionBefore = ws.Revision;
            OperationResult r;
            switch (op.Kind)
            {
                case "edit-draft": r = GarageOperations.EditDraft(ws, ws.Revision, op.Build, ctx, now); break;
                case "apply": r = GarageOperations.Apply(ws, ws.Revision, ctx, null, now); break;
                case "discard-draft": r = GarageOperations.DiscardDraft(ws, ws.Revision); break;
                case "save-as": r = GarageOperations.SaveAs(ws, ws.Revision, op.Name, op.Note ?? "", ctx, now); break;
                case "load-into-draft": r = GarageOperations.LoadIntoDraft(ws, ws.Revision, op.Source, op.ConfirmationToken, ctx, now); break;
                case "begin-workshop": r = GarageOperations.BeginWorkshopSession(ws, ctx, now); break;
                case "end-workshop": r = GarageOperations.EndWorkshopSession(ws, ws.Revision); break;
                case "livery-apply": r = ApplyLivery(ws, op, now); break;
                case "visual-preset-save": r = PresetPayload(ws, op, out string savePayload) ?? GarageOperations.SaveVisualPreset(ws, ws.Revision, op.Name, op.PayloadSchema, savePayload, now); break;
                case "visual-preset-update":
                    r = PresetPayload(ws, op, out string canonical) ?? GarageOperations.UpdateVisualPreset(ws, ws.Revision, op.PresetId, op.PayloadSchema, canonical, op.ConfirmationToken, now);
                    break;
                case "visual-preset-rename": r = GarageOperations.RenameVisualPreset(ws, ws.Revision, op.PresetId, op.Name, now); break;
                case "visual-preset-delete": r = GarageOperations.DeleteVisualPreset(ws, ws.Revision, op.PresetId, op.ConfirmationToken); break;
                default: throw new ArgumentException("unknown garage operation " + op.Kind);
            }
            var a = new GarageAnswer
            {
                Accepted = r.Accepted, Message = r.Message ?? "", ConfirmationRequired = r.Status == OpStatus.ConfirmationRequired,
                ConfirmationToken = r.ConfirmationToken, Repairs = r.Repairs.Select(x => x.ToString()).ToList(),
            };
            if (r.Accepted)
            {
                // Store the workspace atomically in the profile; an identical workspace is already stored.
                LocalProgressionResult save = LocalGarage.SaveWorkspace(L.Profile, instanceId, ws, storedRevision.TryGetValue(instanceId, out long sr) ? sr : 0);
                if (save.Changed)
                {
                    if (!L.Commit(save, out string note))
                    {
                        a.Accepted = false;
                        a.Message = "Not saved: " + note;
                    }
                    else storedRevision[instanceId] = ws.Revision;
                }
                else if (save.Status != LocalOperationStatus.AlreadyApplied)
                {
                    a.Accepted = false;
                    a.Message = "Not saved: " + save.Reason;
                }
            }
            // CH50 Change Without Losing: switched back from a later preset to the first, exactly, and saved.
            if (a.Accepted && op.Kind == "livery-apply" && ws.Revision != revisionBefore &&
                LiveryChallenges.RestoresFirstPreset(ws.VisualPresets, presetBefore, hashBefore, ws.AppliedVisualPresetId ?? "", ws.AppliedLivery ?? "", ws.AppliedLiveryHash ?? ""))
            {
                LocalProgressionResult ch = LocalProgression.CompleteGarageChallenge(L.Profile, cat, LiveryChallenges.ChangeWithoutLosing, now);
                if (ch.Status == LocalOperationStatus.Applied && L.Commit(ch, out _))
                    a.Challenge = $"Challenge complete · {cat.Challenge(LiveryChallenges.ChangeWithoutLosing).Name} · +{ch.BalanceAfter - ch.BalanceBefore:N0} cr.";
            }
            if (a.Accepted) a.State = new GarageState { Workspace = ws, Context = LocalGarage.Context(L.Profile, cat, parts, instanceId) };
            done(a);
        }

        public override IEnumerable<string> OwnedCosmetics() => L?.Profile?.Cosmetics.Select(c => c.CosmeticId) ?? Enumerable.Empty<string>();

        static CustomizationCatalogue Appearance => ContentLibrary.Load()?.Customization;

        /// <summary>
        /// Local livery apply: the same checks the control plane makes online — a well-formed livery for THIS car, valid in the
        /// catalogue, every cosmetic owned by the profile — then Core stores the canonical JSON and its hash.
        /// </summary>
        OperationResult ApplyLivery(CarBuildWorkspace ws, GarageOp op, DateTime now)
        {
            if (string.IsNullOrEmpty(op.LiveryJson))
                return GarageOperations.ApplyLivery(ws, ws.Revision, "", "", "", now); // back to the stock appearance
            LiveryParseResult p = LiveryJson.Parse(op.LiveryJson);
            if (!p.Ok) return Refused(ws, "That livery could not be read: " + string.Join("; ", p.Errors.Take(2)));
            if (p.Document.Car != ws.Car.ModelId) return Refused(ws, $"That livery is for {p.Document.Car}, not this car.");
            LiveryValidation v = LiveryValidator.Validate(p.Document, Appearance, ws.Car.ModelId, CosmeticOwnership.FromIds(OwnedCosmetics()), LiveryValidationMode.Apply);
            if (!v.IsValid) return Refused(ws, string.Join(" ", v.Errors.Take(3)));
            return GarageOperations.ApplyLivery(ws, ws.Revision, LiveryJson.ToCanonicalJson(p.Document), LiveryHash.Of(p.Document), op.PresetId ?? "", now);
        }

        /// <summary>A livery payload must be valid for this car (Preview: locked items may be kept as a plan); others pass as-is.</summary>
        OperationResult PresetPayload(CarBuildWorkspace ws, GarageOp op, out string canonical)
        {
            canonical = op.PayloadJson ?? "";
            if (op.PayloadSchema != LiveryDocument.SchemaId) return null;
            LiveryParseResult p = LiveryJson.Parse(op.PayloadJson ?? "");
            if (!p.Ok) return Refused(ws, "That livery could not be read: " + string.Join("; ", p.Errors.Take(2)));
            LiveryValidation v = LiveryValidator.Validate(p.Document, Appearance, ws.Car.ModelId, null, LiveryValidationMode.Preview);
            if (!v.IsValid) return Refused(ws, string.Join(" ", v.Errors.Take(3)));
            canonical = LiveryJson.ToCanonicalJson(p.Document);
            return null;
        }

        static OperationResult Refused(CarBuildWorkspace ws, string message) =>
            new OperationResult { Status = OpStatus.Rejected, Revision = ws.Revision, Message = message };

        public override void Quote(string instanceId, CarBuildWorkspace ws, MechanicalSnapshot build, Action<GarageQuote> done)
        {
            QuoteResult q = LocalGarage.Quote(L.Profile, cat, parts, instanceId, build, DateTime.UtcNow);
            if (q.Status != QuoteStatus.Ok)
            {
                done(new GarageQuote { Message = q.Message + (q.Repairs.Count > 0 ? "  " + string.Join("; ", q.Repairs.Take(3).Select(x => x.ToString())) : "") });
                return;
            }
            done(new GarageQuote { Ok = true, QuoteId = q.Quote.QuoteId, Total = q.Quote.Total, Lines = q.Quote.Lines.Select(l => l.Name).ToList(), Core = q.Quote });
        }

        public override void Settle(string instanceId, GarageQuote quote, Action<GarageAnswer> done)
        {
            LocalProgressionResult r = LocalGarage.BuyAndApply(L.Profile, cat, parts, instanceId, quote.Core, true, "buy-" + quote.QuoteId, DateTime.UtcNow);
            if (!r.Changed)
            {
                done(new GarageAnswer { Message = r.Status == LocalOperationStatus.AlreadyApplied ? "Already bought and applied." : "Not bought: " + r.Reason });
                return;
            }
            if (!L.Commit(r, out string note))
            {
                done(new GarageAnswer { Message = "Not saved: " + note });
                return;
            }
            Load(instanceId, (state, error) => done(new GarageAnswer
            {
                Accepted = state != null, State = state,
                Message = state != null ? $"Bought and applied — {quote.Total:N0} cr. The parts belong to this car." : error,
            }));
        }
    }

    // ================================================================== Online

    /// <summary>The ONLINE Garage over /v1/me/garage. The server decides; the client renders with the same Core evaluations.</summary>
    public sealed class OnlineGarageBackend : GarageBackend
    {
        readonly ContentCatalogue cat;
        readonly PartsCatalogue parts;
        long balance;
        OnlineSession S => OnlineSession.Current;

        public OnlineGarageBackend(ContentCatalogue cat, PartsCatalogue parts)
        {
            this.cat = cat;
            this.parts = parts;
        }

        public override bool Online => true;
        public override long Balance => balance;
        public override string OwnerKey => "online." + (S?.AccountId ?? "");

        public override async void Cars(Action<List<GarageCarRef>, string> done)
        {
            JObject r = await S.Rest(HttpMethod.Get, "/v1/me/garage/cars");
            if (r == null)
            {
                done(new List<GarageCarRef>(), S.LastError);
                return;
            }
            balance = (long?)(r["wallet"] as JObject)?["balance"] ?? balance;
            var list = ((r["cars"] as JArray) ?? new JArray()).OfType<JObject>()
                .Select(c => new GarageCarRef { InstanceId = (string)c["instanceId"], ModelId = (string)c["carId"] }).ToList();
            done(list, "");
        }

        public override async void Load(string instanceId, Action<GarageState, string> done)
        {
            JObject r = await S.Rest(HttpMethod.Get, "/v1/me/garage/cars/" + Uri.EscapeDataString(instanceId));
            if (r == null)
            {
                done(null, S.LastError);
                return;
            }
            await RefreshBalance();
            done(State(r, (string)r["carId"]), "");
        }

        public override IEnumerable<string> OwnedCosmetics() =>
            ((S?.Me?["cosmeticsOwned"] as JArray) ?? new JArray()).Where(x => x.Type == JTokenType.String).Select(x => (string)x);

        async System.Threading.Tasks.Task RefreshBalance()
        {
            await S.RefreshMe();
            balance = (long?)(S.Me?["wallet"] as JObject)?["balance"] ?? balance;
        }

        GarageState State(JObject car, string modelId)
        {
            var owned = new PartInventory();
            string instanceId = (string)car["instanceId"];
            foreach (JToken p in (car["ownedParts"] as JArray) ?? new JArray()) owned.Grant(instanceId, (string)p);
            int shopAct = (int?)car["shopAct"] ?? 1;
            return new GarageState
            {
                Workspace = OnlineGarageCodec.Workspace((JObject)car["workspace"]),
                Context = BuildContext.Create(cat, parts, modelId, owned, shopAct),
                BuildLocked = (bool?)car["buildFrozen"] == true,
            };
        }

        public override async void Run(string instanceId, CarBuildWorkspace ws, GarageOp op, Action<GarageAnswer> done)
        {
            var body = new JObject { ["op"] = op.Kind, ["expectedRevision"] = ws.Revision };
            if (op.Build != null) body["build"] = OnlineGarageCodec.Build(op.Build);
            if (op.Name != null) body["name"] = op.Name;
            if (op.Note != null) body["note"] = op.Note;
            if (op.ConfirmationToken != null) body["confirmationToken"] = op.ConfirmationToken;
            if (op.Source != null) body["source"] = new JObject { ["kind"] = op.Source.Kind, ["id"] = op.Source.Id };
            if (op.LiveryJson != null) body["liveryJson"] = op.LiveryJson;
            if (op.PresetId != null) body["presetId"] = op.PresetId;
            if (op.PayloadSchema != null) body["payloadSchema"] = op.PayloadSchema;
            if (op.PayloadJson != null) body["payloadJson"] = op.PayloadJson;
            string path = "/v1/me/garage/cars/" + Uri.EscapeDataString(instanceId);
            (int status, JObject reply) = await S.Client.Send(HttpMethod.Post, path + "/operations", body);
            var a = new GarageAnswer();
            if (status >= 200 && status < 300)
            {
                a.Accepted = true;
                a.Message = (string)reply["message"] ?? "";
                // A workshop challenge the Garage completed with this operation (CH50), granted once by the server.
                if (reply["challenge"] is JObject ch)
                    a.Challenge = $"Challenge complete · {(string)ch["name"]} · +{(long?)ch["cash"] ?? 0:N0} cr.";
                a.Repairs = Repairs(reply);
            }
            else
            {
                string code = (string)reply["error"] ?? status.ToString(CultureInfo.InvariantCulture);
                a.Message = (string)reply["message"] ?? $"The garage refused that ({status}).";
                a.ConfirmationRequired = code == "confirmation_required";
                a.ConfirmationToken = (string)reply["confirmationToken"] ?? (string)(reply["details"] as JObject)?["confirmationToken"];
                a.Repairs = Repairs(reply);
            }
            // Accepted or not, show the server's car (a stale edit reloads the current one).
            JObject car = await S.Rest(HttpMethod.Get, path);
            if (car != null) a.State = State(car, (string)car["carId"]);
            done(a);
        }

        static List<string> Repairs(JObject reply) =>
            ((reply["repairs"] as JArray) ?? new JArray()).Select(x => (string)x["text"] ?? (string)x["detail"]).Where(x => !string.IsNullOrEmpty(x)).ToList();

        public override async void Quote(string instanceId, CarBuildWorkspace ws, MechanicalSnapshot build, Action<GarageQuote> done)
        {
            (int status, JObject r) = await S.Client.Send(HttpMethod.Post, "/v1/me/garage/cars/" + Uri.EscapeDataString(instanceId) + "/quote",
                new JObject { ["build"] = OnlineGarageCodec.Build(build) });
            if (status < 200 || status >= 300)
            {
                done(new GarageQuote { Message = (string)r["message"] ?? $"No quote ({status})." });
                return;
            }
            JObject q = r["quote"] as JObject ?? r;
            done(new GarageQuote
            {
                Ok = true, QuoteId = (string)q["quoteId"], Total = (long?)q["total"] ?? 0,
                Lines = ((q["lines"] as JArray) ?? new JArray()).Select(l => (string)l["name"]).ToList(),
            });
        }

        public override async void Settle(string instanceId, GarageQuote quote, Action<GarageAnswer> done)
        {
            string path = "/v1/me/garage/cars/" + Uri.EscapeDataString(instanceId);
            (int status, JObject r) = await S.Client.Send(HttpMethod.Post, path + "/quote/" + Uri.EscapeDataString(quote.QuoteId) + "/settle", new JObject { ["confirm"] = true });
            var a = new GarageAnswer();
            if (status >= 200 && status < 300)
            {
                a.Accepted = true;
                long charged = (long?)r["charged"] ?? quote.Total;
                a.Message = (bool?)r["replayed"] == true ? "Already bought and applied." : $"Bought and applied — {charged:N0} cr. The parts belong to this car.";
            }
            else a.Message = (string)r["message"] ?? $"Not bought ({status}).";
            JObject car = await S.Rest(HttpMethod.Get, path);
            if (car != null) a.State = State(car, (string)car["carId"]);
            await RefreshBalance();
            done(a);
        }
    }

    /// <summary>The control plane's garage wire shapes (camelCase) ↔ Core objects.</summary>
    public static class OnlineGarageCodec
    {
        public static JObject Build(MechanicalSnapshot s) => new JObject
        {
            ["parts"] = JObject.FromObject(s.Parts ?? new SortedDictionary<string, string>()),
            ["utilityPartId"] = s.UtilityPartId,
            ["tuning"] = new JObject { ["version"] = (s.Tuning ?? new TuningSetup()).Version, ["values"] = JObject.FromObject((s.Tuning ?? new TuningSetup()).Values) },
        };

        public static MechanicalSnapshot Snapshot(JToken t)
        {
            var s = new MechanicalSnapshot();
            if (!(t is JObject o)) return s;
            foreach (JProperty p in ((o["parts"] as JObject) ?? new JObject()).Properties())
                if (p.Value.Type == JTokenType.String && !string.IsNullOrEmpty((string)p.Value)) s.Parts[p.Name] = (string)p.Value;
            string utility = (string)o["utilityPartId"];
            s.UtilityPartId = string.IsNullOrEmpty(utility) ? null : utility;
            if (o["tuning"] is JObject tuning)
            {
                s.Tuning.Version = (int?)tuning["version"] ?? TuningModel.CurrentVersion;
                foreach (JProperty p in ((tuning["values"] as JObject) ?? new JObject()).Properties()) s.Tuning.Values[p.Name] = (int)p.Value;
            }
            return s;
        }

        /// <summary>Wire timestamps are ISO-8601 UTC; parsed invariantly and kept in UTC (never through the local zone).</summary>
        static DateTime Utc(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return default(DateTime);
            if (t.Type == JTokenType.Date)
            {
                var d = (DateTime)((JValue)t).Value;
                return d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);
            }
            return DateTime.TryParse((string)t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime u)
                ? DateTime.SpecifyKind(u, DateTimeKind.Utc) : default(DateTime);
        }

        public static CarBuildWorkspace Workspace(JObject w)
        {
            var ws = new CarBuildWorkspace
            {
                Schema = (string)w["schema"] ?? CarBuildWorkspace.SchemaId,
                SchemaVersion = (int?)w["schemaVersion"] ?? CarBuildWorkspace.CurrentSchemaVersion,
                Car = new CarInstanceRef { InstanceId = (string)w["instanceId"], ModelId = (string)w["carId"] },
                Revision = (long?)w["revision"] ?? 0,
                LoadoutCapacity = (int?)w["loadoutCapacity"] ?? CarBuildWorkspace.MinLoadoutSlots,
                VisualPresetCapacity = (int?)w["visualPresetCapacity"] ?? CarBuildWorkspace.MinVisualPresetSlots,
                AppliedVisualPresetId = (string)w["appliedVisualPresetId"] ?? "",
                AppliedLiveryHash = (string)w["appliedLiveryHash"] ?? "",
                AppliedLivery = (string)w["appliedLivery"] ?? "",
            };
            if (w["applied"] is JObject a)
                ws.Applied = new AppliedVehicleBuild
                {
                    Revision = (long?)a["revision"] ?? 0, Build = Snapshot(a["build"]), BuildHash = (string)a["buildHash"] ?? "", Pi = (int?)a["pi"] ?? 0,
                    PiClass = (string)a["piClass"] ?? "", HandlingModelVersion = (string)a["handlingModelVersion"] ?? "",
                    PartsCatalogueRevision = (int?)a["partsCatalogueRevision"] ?? 0, AppliedUtc = Utc(a["appliedUtc"]), Source = (string)a["source"] ?? "",
                };
            foreach (JObject l in ((w["loadouts"] as JArray) ?? new JArray()).OfType<JObject>())
                ws.Loadouts.Add(new MechanicalLoadout
                {
                    LoadoutId = (string)l["loadoutId"] ?? "", Name = (string)l["name"] ?? "", CarInstanceId = (string)l["carInstanceId"] ?? "",
                    CarModelId = (string)l["carModelId"] ?? "", Build = Snapshot(l["build"]), DerivedPi = (int?)l["derivedPi"] ?? 0,
                    DerivedClass = (string)l["derivedClass"] ?? "", BuildHash = (string)l["buildHash"] ?? "", Note = (string)l["note"] ?? "",
                    UpdatedUtc = Utc(l["updatedUtc"]), Pinned = (bool?)l["pinned"] == true, NeedsParts = (bool?)l["needsParts"] == true,
                    UnresolvedPartIds = ((l["unresolvedPartIds"] as JArray) ?? new JArray()).Select(x => (string)x).ToList(),
                    Notices = ((l["notices"] as JArray) ?? new JArray()).Select(x => (string)x).ToList(),
                });
            foreach (JObject v in ((w["visualPresets"] as JArray) ?? new JArray()).OfType<JObject>())
                ws.VisualPresets.Add(new VisualPreset
                {
                    PresetId = (string)v["presetId"] ?? "", Name = (string)v["name"] ?? "", PayloadSchema = (string)v["payloadSchema"] ?? "",
                    PayloadJson = (string)v["payloadJson"] ?? "", UpdatedUtc = Utc(v["updatedUtc"]),
                });
            foreach (JProperty p in ((w["references"] as JObject) ?? new JObject()).Properties())
            {
                if (!(p.Value is JObject r)) continue;
                ws.References[p.Name] = new BuildReference
                {
                    Kind = (string)r["kind"] ?? p.Name, Build = Snapshot(r["build"]), SourceAppliedRevision = (long?)r["sourceAppliedRevision"] ?? 0,
                    BuildHash = (string)r["buildHash"] ?? "", Pi = (int?)r["pi"] ?? 0, HandlingModelVersion = (string)r["handlingModelVersion"] ?? "",
                    PartsCatalogueRevision = (int?)r["partsCatalogueRevision"] ?? 0, CapturedUtc = Utc(r["capturedUtc"]), Context = (string)r["context"] ?? "",
                };
            }
            if (w["draft"] is JObject d)
                ws.Draft = new GarageDraft
                {
                    Build = Snapshot(d["build"]), LoadedFrom = (string)d["loadedFrom"] ?? "", BasedOnAppliedRevision = (long?)d["basedOnAppliedRevision"] ?? 0,
                    PreviewPartIds = ((d["previewPartIds"] as JArray) ?? new JArray()).Select(x => (string)x).ToList(),
                    UnresolvedPartIds = ((d["unresolvedPartIds"] as JArray) ?? new JArray()).Select(x => (string)x).ToList(),
                    UpdatedUtc = Utc(d["updatedUtc"]),
                };
            if (w["workshop"] is JObject k)
                ws.Workshop = new WorkshopSession { Open = (bool?)k["open"] == true, OpenedUtc = Utc(k["openedUtc"]) };
            return ws;
        }
    }
}
