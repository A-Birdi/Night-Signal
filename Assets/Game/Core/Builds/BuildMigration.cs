using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Builds
{
    /// <summary>
    /// One stored document as the profile/database envelope keeps it (mirrors Profiles.NamedDocument + VersionedDocument,
    /// without depending on that package): slot id + name + schema-tagged opaque JSON.
    /// </summary>
    public sealed class StoredBuildDocument
    {
        public string SlotId = "";
        public string Name = "";
        public string Schema = "";
        public int SchemaVersion = 1;
        public DateTime UpdatedUtc;
        public JToken Data;
    }

    /// <summary>The set of stored documents for one car instance (maps 1:1 onto Profiles.CarWorkspace).</summary>
    public sealed class StoredBuildDocuments
    {
        public List<StoredBuildDocument> MechanicalLoadouts = new List<StoredBuildDocument>();
        public List<StoredBuildDocument> VisualPresets = new List<StoredBuildDocument>();
        /// <summary>Keyed by before-workshop / before-last-apply / last-race-build.</summary>
        public Dictionary<string, StoredBuildDocument> References = new Dictionary<string, StoredBuildDocument>(StringComparer.Ordinal);
        public StoredBuildDocument AppliedBuild;
        public StoredBuildDocument GarageDraft;
        /// <summary>Workspace state (revision, capacities, workshop session, applied visual preset id, livery hash and livery).</summary>
        public StoredBuildDocument WorkspaceState;
    }

    public sealed class MigrationResult
    {
        public CarBuildWorkspace Workspace;
        public List<string> Notices = new List<string>();
    }

    /// <summary>
    /// Document schemas and migration. Current: loadout v2, visual preset v1, applied build v1, reference v1, draft v1,
    /// workspace state v1, whole workspace v2. Legacy: "tune preset" loadout v1 and whole workspace v1 (the spec §9
    /// three-tune-preset era shape: part-id ARRAYS and free-form tune numbers). Migration never drops a named preset
    /// (capacity grows to hold them all), keeps unplaceable ids with an explanation, and never invents presets.
    /// <para>
    /// The applied livery (<see cref="CarBuildWorkspace.AppliedLivery"/>) is an additive field of workspace state v1 and whole
    /// workspace v2: a document without it reads as "" (stock appearance). The state document writes <c>appliedLivery</c> only
    /// when a livery is applied, so a stock workspace keeps exactly its earlier stored form (no version bump, no spurious
    /// "upgraded on read" for existing profiles).
    /// </para>
    /// </summary>
    public static class BuildDocumentCodec
    {
        /// <summary>
        /// A stored timestamp as UTC. <c>(DateTime?)token</c> converts an ISO string through the machine's time zone, so a
        /// saved "…Z" came back shifted on any non-UTC machine; strings are parsed invariantly and kept in UTC instead.
        /// </summary>
        internal static DateTime? Utc(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Date)
            {
                DateTime d = (DateTime)((JValue)t).Value;
                return d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);
            }
            if (t.Type == JTokenType.String &&
                DateTime.TryParse((string)t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime u))
                return DateTime.SpecifyKind(u, DateTimeKind.Utc);
            return null;
        }

        public const string LoadoutSchema = "night-signal/mechanical-loadout";
        public const int LoadoutVersion = 2;
        public const string VisualPresetSchema = "night-signal/visual-preset";
        public const string AppliedSchema = "night-signal/applied-build";
        public const string ReferenceSchema = "night-signal/build-reference";
        public const string DraftSchema = "night-signal/garage-draft";
        public const string WorkspaceStateSchema = "night-signal/car-build-workspace-state";

        static readonly JsonSerializer Serializer = JsonSerializer.Create(BuildJson.Settings);

        // ------------------------------------------------------------ whole workspace JSON

        public static string SerializeWorkspace(CarBuildWorkspace ws) => JsonConvert.SerializeObject(ws, BuildJson.Settings);

        /// <summary>Reads a whole-workspace document of any known version (v1 legacy is migrated).</summary>
        public static MigrationResult DeserializeWorkspace(string json, PartsCatalogue parts, DateTime nowUtc)
        {
            JObject o = JObject.Parse(json);
            int version = (int?)o["SchemaVersion"] ?? (int?)o["schemaVersion"] ?? 0;
            string schema = (string)o["Schema"] ?? (string)o["schema"] ?? "";
            if (schema != CarBuildWorkspace.SchemaId) throw new BuildDataException($"Not a car build workspace document ({schema}).");
            if (version == CarBuildWorkspace.CurrentSchemaVersion)
            {
                CarBuildWorkspace ws = o.ToObject<CarBuildWorkspace>(Serializer);
                var r = new MigrationResult { Workspace = ws };
                EnsureCapacities(ws, r.Notices);
                return r;
            }
            if (version == 1) return MigrateLegacyWorkspaceV1(o, parts, nowUtc);
            throw new BuildDataException($"Unsupported car build workspace version {version}.");
        }

        /// <summary>
        /// Legacy v1: { schema, schemaVersion: 1, instanceId, modelId, appliedPartIds[], appliedTune{}, tunePresets[{name,
        /// partIds[], tune{}, note}], visualPresets[{name, payload}] }.
        /// </summary>
        static MigrationResult MigrateLegacyWorkspaceV1(JObject o, PartsCatalogue parts, DateTime nowUtc)
        {
            var result = new MigrationResult();
            string instanceId = (string)o["instanceId"] ?? "";
            string modelId = (string)o["modelId"] ?? "";
            if (instanceId.Length == 0 || modelId.Length == 0) throw new BuildDataException("Legacy workspace lacks instance/model id.");
            CarBuildWorkspace ws = CarBuildWorkspace.CreateNew(instanceId, modelId, nowUtc);
            ws.Applied.Build = LegacySnapshot(o["appliedPartIds"] as JArray, o["appliedTune"] as JObject, parts, out List<string> appliedUnresolved, out List<string> appliedNotes);
            ws.Applied.Source = "migrated:v1";
            foreach (string id in appliedUnresolved) result.Notices.Add($"Applied build: legacy part {id} could not be placed in a slot; kept stock in that position — repair required.");
            result.Notices.AddRange(appliedNotes.Select(n => "Applied build: " + n));

            int index = 0;
            foreach (JObject p in (o["tunePresets"] as JArray ?? new JArray()).OfType<JObject>())
            {
                index++;
                MechanicalLoadout l = LegacyLoadout(p, instanceId, modelId, parts, index, nowUtc);
                ws.Loadouts.Add(l);
            }
            index = 0;
            foreach (JObject v in (o["visualPresets"] as JArray ?? new JArray()).OfType<JObject>())
            {
                index++;
                ws.VisualPresets.Add(new VisualPreset
                {
                    PresetId = "vp-legacy-" + index.ToString(CultureInfo.InvariantCulture),
                    Name = LegacyName((string)v["name"], "Visual " + index.ToString(CultureInfo.InvariantCulture)),
                    PayloadSchema = (string)v["payloadSchema"] ?? "legacy",
                    PayloadJson = v["payload"]?.ToString(Formatting.None) ?? "",
                    UpdatedUtc = nowUtc,
                });
            }
            ws.Revision = 1;
            EnsureCapacities(ws, result.Notices);
            result.Notices.Add($"Migrated legacy workspace v1: {ws.Loadouts.Count} named loadout(s), {ws.VisualPresets.Count} visual preset(s) kept; protected references start empty (none invented).");
            result.Workspace = ws;
            return result;
        }

        // ------------------------------------------------------------ per-document envelope (Profiles storage)

        public static StoredBuildDocuments ToDocuments(CarBuildWorkspace ws)
        {
            var d = new StoredBuildDocuments();
            foreach (MechanicalLoadout l in ws.Loadouts)
                d.MechanicalLoadouts.Add(Doc(l.LoadoutId, l.Name, LoadoutSchema, LoadoutVersion, l.UpdatedUtc, l));
            foreach (VisualPreset v in ws.VisualPresets)
                d.VisualPresets.Add(Doc(v.PresetId, v.Name, VisualPresetSchema, 1, v.UpdatedUtc, v));
            foreach (var kv in ws.References)
                d.References[kv.Key] = Doc(kv.Key, kv.Key, ReferenceSchema, 1, kv.Value.CapturedUtc, kv.Value);
            d.AppliedBuild = Doc("applied", "applied", AppliedSchema, 1, ws.Applied.AppliedUtc, ws.Applied);
            if (ws.Draft != null) d.GarageDraft = Doc("draft", "draft", DraftSchema, 1, ws.Draft.UpdatedUtc, ws.Draft);
            var state = new JObject
            {
                ["instanceId"] = ws.Car.InstanceId,
                ["modelId"] = ws.Car.ModelId,
                ["revision"] = ws.Revision,
                ["loadoutCapacity"] = ws.LoadoutCapacity,
                ["visualPresetCapacity"] = ws.VisualPresetCapacity,
                ["appliedVisualPresetId"] = ws.AppliedVisualPresetId,
                ["appliedLiveryHash"] = ws.AppliedLiveryHash,
            };
            // Only when applied: a stock workspace stores exactly the pre-livery state document (see the class remarks).
            if (!string.IsNullOrEmpty(ws.AppliedLivery)) state["appliedLivery"] = ws.AppliedLivery;
            state["workshopOpen"] = ws.Workshop.Open;
            state["workshopOpenedUtc"] = ws.Workshop.OpenedUtc;
            d.WorkspaceState = Doc("state", "state", WorkspaceStateSchema, 1, default(DateTime), state);
            return d;
        }

        /// <summary>Reads stored documents of any known version. Unknown documents are preserved verbatim with a notice.</summary>
        public static MigrationResult FromDocuments(string instanceId, string modelId, StoredBuildDocuments docs, PartsCatalogue parts, DateTime nowUtc)
        {
            var result = new MigrationResult();
            CarBuildWorkspace ws = CarBuildWorkspace.CreateNew(instanceId, modelId, nowUtc);
            if (docs.WorkspaceState?.Data is JObject st)
            {
                ws.Revision = (long?)st["revision"] ?? 1;
                ws.LoadoutCapacity = (int?)st["loadoutCapacity"] ?? CarBuildWorkspace.MinLoadoutSlots;
                ws.VisualPresetCapacity = (int?)st["visualPresetCapacity"] ?? CarBuildWorkspace.MinVisualPresetSlots;
                ws.AppliedVisualPresetId = (string)st["appliedVisualPresetId"] ?? "";
                ws.AppliedLiveryHash = (string)st["appliedLiveryHash"] ?? "";
                ws.AppliedLivery = (string)st["appliedLivery"] ?? ""; // absent in older documents: the stock appearance
                ws.Workshop = new WorkshopSession { Open = (bool?)st["workshopOpen"] ?? false, OpenedUtc = Utc(st["workshopOpenedUtc"]) ?? default(DateTime) };
            }
            if (docs.AppliedBuild?.Data != null && docs.AppliedBuild.Schema == AppliedSchema)
                ws.Applied = docs.AppliedBuild.Data.ToObject<AppliedVehicleBuild>(Serializer);

            int index = 0;
            foreach (StoredBuildDocument d in docs.MechanicalLoadouts)
            {
                index++;
                if (d.Schema == LoadoutSchema && d.SchemaVersion == LoadoutVersion && d.Data != null)
                {
                    MechanicalLoadout l = d.Data.ToObject<MechanicalLoadout>(Serializer);
                    if (string.IsNullOrEmpty(l.LoadoutId)) l.LoadoutId = string.IsNullOrEmpty(d.SlotId) ? "ld-migrated-" + index : d.SlotId;
                    if (string.IsNullOrEmpty(l.Name)) l.Name = LegacyName(d.Name, "Loadout " + index);
                    ws.Loadouts.Add(l);
                }
                else if (d.Data is JObject legacy && (d.Schema == LoadoutSchema && d.SchemaVersion == 1 || d.Schema == "night-signal/tune-preset"))
                {
                    MechanicalLoadout l = LegacyLoadout(legacy, instanceId, modelId, parts, index, nowUtc);
                    if (!string.IsNullOrEmpty(d.SlotId)) l.LoadoutId = d.SlotId;
                    if (string.IsNullOrEmpty((string)legacy["name"]) && !string.IsNullOrEmpty(d.Name)) l.Name = LegacyName(d.Name, l.Name);
                    ws.Loadouts.Add(l);
                    result.Notices.Add($"Loadout \"{l.Name}\" migrated from tune preset v1.");
                }
                else
                {
                    ws.Loadouts.Add(new MechanicalLoadout
                    {
                        LoadoutId = string.IsNullOrEmpty(d.SlotId) ? "ld-unknown-" + index : d.SlotId,
                        Name = LegacyName(d.Name, "Kept document " + index),
                        CarInstanceId = instanceId,
                        CarModelId = modelId,
                        UpdatedUtc = d.UpdatedUtc,
                        Notices = { $"Unrecognised document {d.Schema}@{d.SchemaVersion} kept verbatim; it cannot be applied until repaired." },
                        LegacyDocumentJson = d.Data?.ToString(Formatting.None) ?? "",
                        NeedsParts = true,
                    });
                    result.Notices.Add($"Unrecognised loadout document {d.Schema}@{d.SchemaVersion} preserved.");
                }
            }
            index = 0;
            foreach (StoredBuildDocument d in docs.VisualPresets)
            {
                index++;
                VisualPreset v = d.Schema == VisualPresetSchema && d.Data != null ? d.Data.ToObject<VisualPreset>(Serializer) : new VisualPreset
                {
                    PresetId = string.IsNullOrEmpty(d.SlotId) ? "vp-migrated-" + index : d.SlotId,
                    Name = LegacyName(d.Name, "Visual " + index),
                    PayloadSchema = d.Schema,
                    PayloadJson = d.Data?.ToString(Formatting.None) ?? "",
                    UpdatedUtc = d.UpdatedUtc,
                };
                ws.VisualPresets.Add(v);
            }
            foreach (var kv in docs.References)
            {
                if (!BuildReferenceKinds.TryParse(kv.Key, out _) || kv.Value?.Data == null)
                {
                    result.Notices.Add($"Unknown protected reference {kv.Key} ignored.");
                    continue;
                }
                ws.References[kv.Key] = kv.Value.Data.ToObject<BuildReference>(Serializer);
            }
            if (docs.GarageDraft?.Data != null && docs.GarageDraft.Schema == DraftSchema)
                ws.Draft = docs.GarageDraft.Data.ToObject<GarageDraft>(Serializer);
            EnsureCapacities(ws, result.Notices);
            result.Workspace = ws;
            return result;
        }

        // ------------------------------------------------------------ legacy helpers

        static MechanicalLoadout LegacyLoadout(JObject p, string instanceId, string modelId, PartsCatalogue parts, int index, DateTime nowUtc)
        {
            MechanicalSnapshot build = LegacySnapshot(p["partIds"] as JArray, p["tune"] as JObject, parts, out List<string> unresolved, out List<string> notes);
            var l = new MechanicalLoadout
            {
                LoadoutId = "ld-legacy-" + index.ToString(CultureInfo.InvariantCulture),
                Name = LegacyName((string)p["name"], "Preset " + index.ToString(CultureInfo.InvariantCulture)),
                CarInstanceId = instanceId,
                CarModelId = modelId,
                Build = build,
                Note = (string)p["note"] ?? "",
                UpdatedUtc = Utc(p["updatedUtc"]) ?? nowUtc,
                Pinned = false,
                UnresolvedPartIds = unresolved,
                Notices = notes,
            };
            foreach (string id in unresolved) l.Notices.Add($"Legacy part {id} could not be placed in a slot; kept here — repair required.");
            if (unresolved.Count > 0) l.NeedsParts = true;
            return l;
        }

        /// <summary>Places legacy part ids into slots by catalogue lookup; unknown ids and second parts for a slot are kept as unresolved.</summary>
        static MechanicalSnapshot LegacySnapshot(JArray partIds, JObject tune, PartsCatalogue parts, out List<string> unresolved, out List<string> notes)
        {
            unresolved = new List<string>();
            notes = new List<string>();
            var s = new MechanicalSnapshot();
            foreach (string id in (partIds ?? new JArray()).Select(t => (string)t).Where(t => !string.IsNullOrEmpty(t)))
            {
                if (!parts.TryPart(id, out PartDef part)) { unresolved.Add(id); continue; }
                if (part.SlotValue == PartSlot.Utility)
                {
                    if (s.UtilityPartId == null) s.UtilityPartId = id;
                    else { unresolved.Add(id); notes.Add($"Second utility item {id} kept aside (only one utility item applies)."); }
                    continue;
                }
                string slot = PartSlots.Id(part.SlotValue);
                if (s.Parts.ContainsKey(slot)) { unresolved.Add(id); notes.Add($"{id} shares the {slot} slot with {s.Parts[slot]}; kept aside."); }
                else s.Parts[slot] = id;
            }
            foreach (var kv in tune ?? new JObject())
            {
                double value = kv.Value.Type == JTokenType.Float || kv.Value.Type == JTokenType.Integer ? (double)kv.Value : double.NaN;
                if (double.IsNaN(value)) { notes.Add($"Legacy tune {kv.Key} kept as a note (not numeric)."); continue; }
                switch (kv.Key)
                {
                    case "finalDrive": s.Tuning.Values[TuningKeys.FinalDrive] = (int)Math.Round(value * 1000); break;
                    case "gearSpread": s.Tuning.Values[TuningKeys.GearSpread] = (int)Math.Round(value * 1000); break;
                    case "brakeBias": s.Tuning.Values[TuningKeys.BrakeBias] = (int)Math.Round(value * 1000); break;
                    case "diffLock": s.Tuning.Values[TuningKeys.DiffLock] = (int)Math.Round(value * 100); break;
                    case "rideHeightMm": s.Tuning.Values[TuningKeys.RideHeight] = (int)Math.Round(value); break;
                    default: notes.Add(string.Format(CultureInfo.InvariantCulture, "Legacy tune {0}={1} has no current control; kept as this note, not applied.", kv.Key, value)); break;
                }
            }
            return s;
        }

        static string LegacyName(string name, string fallback) => GarageOperations.CleanName(name) ?? GarageOperations.CleanName(TrimTo(name, GarageOperations.MaxNameLength)) ?? fallback;

        static string TrimTo(string s, int n) => s == null ? null : s.Trim().Length > n ? s.Trim().Substring(0, n) : s.Trim();

        static void EnsureCapacities(CarBuildWorkspace ws, List<string> notices)
        {
            int loadoutCap = Math.Max(CarBuildWorkspace.MinLoadoutSlots, Math.Max(ws.LoadoutCapacity, ws.Loadouts.Count));
            if (loadoutCap != ws.LoadoutCapacity && ws.Loadouts.Count > CarBuildWorkspace.MinLoadoutSlots)
                notices.Add($"Kept all {ws.Loadouts.Count} named loadouts (capacity raised above {CarBuildWorkspace.MinLoadoutSlots}).");
            ws.LoadoutCapacity = loadoutCap;
            ws.VisualPresetCapacity = Math.Max(CarBuildWorkspace.MinVisualPresetSlots, Math.Max(ws.VisualPresetCapacity, ws.VisualPresets.Count));
            // Duplicate names from old documents are kept but made distinguishable (never dropped).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MechanicalLoadout l in ws.Loadouts)
            {
                string baseName = l.Name;
                int n = 2;
                while (!seen.Add(l.Name))
                {
                    string suffix = " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
                    l.Name = (baseName.Length + suffix.Length > GarageOperations.MaxNameLength ? baseName.Substring(0, GarageOperations.MaxNameLength - suffix.Length) : baseName) + suffix;
                    n++;
                }
                if (l.Name != baseName) notices.Add($"Duplicate preset name \"{baseName}\" kept as \"{l.Name}\".");
            }
        }

        static StoredBuildDocument Doc(string slotId, string name, string schema, int version, DateTime updated, object data) => new StoredBuildDocument
        {
            SlotId = slotId, Name = name, Schema = schema, SchemaVersion = version, UpdatedUtc = updated,
            Data = data as JToken ?? JToken.FromObject(data, Serializer),
        };
    }
}
