using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace NightSignal.Core.Builds
{
    /// <summary>Stable reference to one owned car INSTANCE (two instances of one model are independent).</summary>
    public sealed class CarInstanceRef
    {
        public string InstanceId = "";
        public string ModelId = "";
    }

    /// <summary>Integer tuning values keyed by <see cref="TuningKeys"/>, versioned.</summary>
    public sealed class TuningSetup
    {
        public int Version = TuningModel.CurrentVersion;
        public SortedDictionary<string, int> Values = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public TuningSetup Clone() => new TuningSetup
        {
            Version = Version,
            Values = new SortedDictionary<string, int>(Values ?? new SortedDictionary<string, int>(), StringComparer.Ordinal),
        };

        /// <summary>Order-independent (a deserialised dictionary may carry a different comparer).</summary>
        public bool ContentEquals(TuningSetup other) =>
            other != null && Version == other.Version && SameEntries(Values, other.Values);

        internal static bool SameEntries<TValue>(IDictionary<string, TValue> a, IDictionary<string, TValue> b)
        {
            int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
            if (ca != cb) return false;
            if (ca == 0) return true;
            foreach (var kv in a)
                if (!b.TryGetValue(kv.Key, out TValue v) || !EqualityComparer<TValue>.Default.Equals(v, kv.Value)) return false;
            return true;
        }

        /// <summary>Entries in ordinal key order regardless of the dictionary's comparer (hashing, canonical text).</summary>
        internal static IEnumerable<KeyValuePair<string, TValue>> Ordinal<TValue>(IDictionary<string, TValue> d) =>
            (d ?? new Dictionary<string, TValue>()).OrderBy(kv => kv.Key, StringComparer.Ordinal);
    }

    /// <summary>
    /// The pure mechanical selection: part ids BY SLOT ID (absent slot = stock), the single utility item and the tune.
    /// Loadouts, drafts, references, applied builds and quotes all carry one. Never array positions.
    /// </summary>
    public sealed class MechanicalSnapshot
    {
        public SortedDictionary<string, string> Parts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Utility part id, or null (no utility). Separate from Parts: it never changes physics.</summary>
        public string UtilityPartId;
        public TuningSetup Tuning = new TuningSetup();

        public static MechanicalSnapshot Stock() => new MechanicalSnapshot();

        public MechanicalSnapshot Clone() => new MechanicalSnapshot
        {
            Parts = new SortedDictionary<string, string>(Parts ?? new SortedDictionary<string, string>(), StringComparer.Ordinal),
            UtilityPartId = UtilityPartId,
            Tuning = (Tuning ?? new TuningSetup()).Clone(),
        };

        public string PartIn(PartSlot slot) => Parts != null && Parts.TryGetValue(PartSlots.Id(slot), out string id) ? id : null;

        /// <summary>Returns a copy with <paramref name="partId"/> in <paramref name="slot"/> (null = stock).</summary>
        public MechanicalSnapshot With(PartSlot slot, string partId)
        {
            MechanicalSnapshot c = Clone();
            if (slot == PartSlot.Utility) c.UtilityPartId = partId;
            else if (partId == null) c.Parts.Remove(PartSlots.Id(slot));
            else c.Parts[PartSlots.Id(slot)] = partId;
            return c;
        }

        public MechanicalSnapshot WithTune(string key, int value)
        {
            MechanicalSnapshot c = Clone();
            c.Tuning.Values[key] = value;
            return c;
        }

        /// <summary>All referenced part ids (mechanical slots then utility).</summary>
        public IEnumerable<string> AllPartIds()
        {
            foreach (var kv in Parts ?? new SortedDictionary<string, string>()) if (!string.IsNullOrEmpty(kv.Value)) yield return kv.Value;
            if (!string.IsNullOrEmpty(UtilityPartId)) yield return UtilityPartId;
        }

        public bool ContentEquals(MechanicalSnapshot other) =>
            other != null && TuningSetup.SameEntries(Parts, other.Parts) &&
            string.Equals(UtilityPartId ?? "", other.UtilityPartId ?? "", StringComparison.Ordinal) &&
            (Tuning ?? new TuningSetup()).ContentEquals(other.Tuning ?? new TuningSetup());

        /// <summary>Hash of the selection (ids, utility, tune) — distinct from the physics BuildHash.</summary>
        public string SelectionHash()
        {
            var sb = new StringBuilder("sel1|");
            foreach (var kv in TuningSetup.Ordinal(Parts)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            sb.Append("|u=").Append(UtilityPartId ?? "").Append("|t").Append((Tuning ?? new TuningSetup()).Version.ToString(CultureInfo.InvariantCulture)).Append('|');
            foreach (var kv in TuningSetup.Ordinal((Tuning ?? new TuningSetup()).Values)) sb.Append(kv.Key).Append('=').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
            return BuildHashing.Sha256Hex(sb.ToString());
        }
    }

    /// <summary>
    /// One of at least eight NAMED mechanical loadouts per owned car instance (Addendum 02 §9.1, D204). Stores ids, not
    /// positions. Derived fields are recomputed and validated whenever the build or the catalogue/handling version changes.
    /// </summary>
    public sealed class MechanicalLoadout
    {
        public string LoadoutId = "";
        public string Name = "";
        public string CarInstanceId = "";
        public string CarModelId = "";
        public MechanicalSnapshot Build = new MechanicalSnapshot();
        /// <summary>Performance-relevant appearance components (aero/body-kit visual ids) implied by the parts.</summary>
        public List<string> PerformanceAppearance = new List<string>();
        public int DerivedPi;
        public string DerivedClass = "";
        public string BuildHash = "";
        public string HandlingModelVersion = "";
        public int PartsCatalogueRevision;
        public string Note = "";
        public DateTime UpdatedUtc;
        public bool Pinned;
        /// <summary>Planning draft: references parts this instance does not own yet (never grants ownership).</summary>
        public bool NeedsParts;
        /// <summary>Migration: ids a legacy document referenced that could not be placed in a slot (never dropped).</summary>
        public List<string> UnresolvedPartIds = new List<string>();
        /// <summary>Migration/repair explanations kept with the preset.</summary>
        public List<string> Notices = new List<string>();
        /// <summary>An unrecognised stored document kept verbatim (never discarded); such a loadout cannot be applied.</summary>
        public string LegacyDocumentJson = "";

        public MechanicalLoadout Clone()
        {
            var c = (MechanicalLoadout)MemberwiseClone();
            c.Build = Build.Clone();
            c.PerformanceAppearance = new List<string>(PerformanceAppearance ?? new List<string>());
            c.UnresolvedPartIds = new List<string>(UnresolvedPartIds ?? new List<string>());
            c.Notices = new List<string>(Notices ?? new List<string>());
            return c;
        }
    }

    /// <summary>
    /// One of at least five independent visual presets per car instance. The payload belongs to the customization
    /// system (paint/decals/wheels/body appearance); mechanical operations never read or write it.
    /// </summary>
    public sealed class VisualPreset
    {
        public string PresetId = "";
        public string Name = "";
        public string PayloadSchema = "";
        public string PayloadJson = "";
        public DateTime UpdatedUtc;

        public VisualPreset Clone() => (VisualPreset)MemberwiseClone();
    }

    /// <summary>The accepted applied mechanical build of one car instance. Each successful apply creates a new revision.</summary>
    public sealed class AppliedVehicleBuild
    {
        public long Revision;
        public MechanicalSnapshot Build = new MechanicalSnapshot();
        public string BuildHash = "";
        public int Pi;
        public string PiClass = "";
        public string HandlingModelVersion = "";
        public int PartsCatalogueRevision;
        public DateTime AppliedUtc;
        /// <summary>What produced it: initial, apply:draft, apply:loadout:{id}, restore:{kind}, quote:{id}.</summary>
        public string Source = "";

        public AppliedVehicleBuild Clone()
        {
            var c = (AppliedVehicleBuild)MemberwiseClone();
            c.Build = Build.Clone();
            return c;
        }
    }

    /// <summary>
    /// The dirty candidate being edited or tested. It may contain PREVIEW parts the instance does not own; those are
    /// recomputed from ownership on every evaluation (a client flag can never make a preview legal).
    /// </summary>
    public sealed class GarageDraft
    {
        public MechanicalSnapshot Build = new MechanicalSnapshot();
        /// <summary>loadout:{id} | reference:{kind} | applied | edit</summary>
        public string LoadedFrom = "";
        public long BasedOnAppliedRevision;
        /// <summary>Informational: unowned parts at the last evaluation ("Preview only — not owned").</summary>
        public List<string> PreviewPartIds = new List<string>();
        /// <summary>
        /// Carried from a migrated loadout: ids that could not be placed. While non-empty the draft cannot be applied (no
        /// silent partial build); an explicit edit of the draft is the player's deliberate fallback choice.
        /// </summary>
        public List<string> UnresolvedPartIds = new List<string>();
        public DateTime UpdatedUtc;

        public GarageDraft Clone()
        {
            var c = (GarageDraft)MemberwiseClone();
            c.Build = Build.Clone();
            c.PreviewPartIds = new List<string>(PreviewPartIds ?? new List<string>());
            c.UnresolvedPartIds = new List<string>(UnresolvedPartIds ?? new List<string>());
            return c;
        }
    }

    public enum BuildReferenceKind
    {
        /// <summary>Applied state on entry into this workshop session; kept until the session ends or a new baseline is accepted.</summary>
        BeforeWorkshop = 0,
        /// <summary>Applied state before the most recent SUCCESSFUL apply.</summary>
        BeforeLastApply = 1,
        /// <summary>Frozen build actually used when authorized full-size driving began.</summary>
        LastRaceBuild = 2,
    }

    public static class BuildReferenceKinds
    {
        // Same keys as NightSignal.Core.Profiles.CarWorkspace.{BeforeWorkshop, BeforeLastApply, LastRaceBuild}.
        public static string Id(BuildReferenceKind k) =>
            k == BuildReferenceKind.BeforeWorkshop ? "before-workshop" : k == BuildReferenceKind.BeforeLastApply ? "before-last-apply" : "last-race-build";

        public static bool TryParse(string id, out BuildReferenceKind kind)
        {
            switch (id)
            {
                case "before-workshop": kind = BuildReferenceKind.BeforeWorkshop; return true;
                case "before-last-apply": kind = BuildReferenceKind.BeforeLastApply; return true;
                case "last-race-build": kind = BuildReferenceKind.LastRaceBuild; return true;
                default: kind = BuildReferenceKind.BeforeWorkshop; return false;
            }
        }
    }

    /// <summary>A protected automatic recovery snapshot (Addendum 02 §9.3). Never deleted on version change.</summary>
    public sealed class BuildReference
    {
        public string Kind = "";
        public MechanicalSnapshot Build = new MechanicalSnapshot();
        public long SourceAppliedRevision;
        public string BuildHash = "";
        public int Pi;
        public string HandlingModelVersion = "";
        public int PartsCatalogueRevision;
        public DateTime CapturedUtc;
        /// <summary>E.g. the race/event id for Last Race Build.</summary>
        public string Context = "";

        public BuildReference Clone()
        {
            var c = (BuildReference)MemberwiseClone();
            c.Build = Build.Clone();
            return c;
        }
    }

    public sealed class WorkshopSession
    {
        public bool Open;
        public DateTime OpenedUtc;
    }

    /// <summary>
    /// Everything the Garage keeps for ONE owned car instance: applied build (revisioned), ≥ 8 named mechanical loadouts,
    /// ≥ 5 visual presets, the three protected references, the draft and the workshop session. <see cref="Revision"/> is
    /// the optimistic-concurrency token: every accepted mutation increments it and every user mutation must quote it.
    /// (Named CarBuildWorkspace to avoid colliding with the storage envelope NightSignal.Core.Profiles.CarWorkspace.)
    /// </summary>
    public sealed class CarBuildWorkspace
    {
        public const string SchemaId = "night-signal/car-build-workspace";
        public const int CurrentSchemaVersion = 2;
        public const int MinLoadoutSlots = 8;
        public const int MinVisualPresetSlots = 5;
        public const int MaxPinnedLoadouts = 3;

        public string Schema = SchemaId;
        public int SchemaVersion = CurrentSchemaVersion;
        public CarInstanceRef Car = new CarInstanceRef();
        public long Revision;
        public AppliedVehicleBuild Applied = new AppliedVehicleBuild();
        public List<MechanicalLoadout> Loadouts = new List<MechanicalLoadout>();
        /// <summary>At least 8; a migrated document that already holds more keeps them all (capacity = count).</summary>
        public int LoadoutCapacity = MinLoadoutSlots;
        public List<VisualPreset> VisualPresets = new List<VisualPreset>();
        public int VisualPresetCapacity = MinVisualPresetSlots;
        /// <summary>Current livery/appearance state owned by customization. Mechanical operations never change these.</summary>
        public string AppliedVisualPresetId = "";
        public string AppliedLiveryHash = "";
        public SortedDictionary<string, BuildReference> References = new SortedDictionary<string, BuildReference>(StringComparer.Ordinal);
        public GarageDraft Draft;
        public WorkshopSession Workshop = new WorkshopSession();

        public static CarBuildWorkspace CreateNew(string instanceId, string modelId, DateTime nowUtc) => new CarBuildWorkspace
        {
            Car = new CarInstanceRef { InstanceId = instanceId, ModelId = modelId },
            Revision = 1,
            Applied = new AppliedVehicleBuild { Revision = 1, Build = MechanicalSnapshot.Stock(), AppliedUtc = nowUtc, Source = "initial" },
        };

        public MechanicalLoadout Loadout(string id) => Loadouts.FirstOrDefault(l => l.LoadoutId == id);

        public BuildReference Reference(BuildReferenceKind kind) =>
            References != null && References.TryGetValue(BuildReferenceKinds.Id(kind), out BuildReference r) ? r : null;

        /// <summary>True when a draft exists and differs from the applied build.</summary>
        [JsonIgnore]
        public bool DraftIsDirty => Draft != null && !Draft.Build.ContentEquals(Applied.Build);

        public CarBuildWorkspace Clone() => JsonConvert.DeserializeObject<CarBuildWorkspace>(JsonConvert.SerializeObject(this, BuildJson.Settings), BuildJson.Settings);
    }

    public static class BuildJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Formatting = Formatting.None,
        };
    }

    public static class BuildHashing
    {
        public static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }
    }
}
