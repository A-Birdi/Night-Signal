using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Profiles
{
    /// <summary>
    /// A complete LOCAL progression profile (Addendum 01 D05, §8.2): wallet, cars, course rights, campaign clears,
    /// challenges, cosmetics, soundtrack, records and local mastery RP for one offline player on this PC.
    /// <para>
    /// It is a separate domain from the authenticated Online profile. Nothing here is uploaded, merged or accepted as Online
    /// state, and it carries no signature, secret, handle, password, e-mail or token: a local save proves nothing about how
    /// it was earned and is not presented as if it did. The checksum written by <see cref="ProfileRepository"/> only detects
    /// accidental corruption.
    /// </para>
    /// <para>
    /// Mutate it only through <see cref="LocalProgression"/> and <see cref="LocalGarage"/> (pure functions that return a new
    /// copy) and persist it through
    /// <see cref="ProfileRepository"/> (atomic, versioned saves with recovery copies). Unknown members found in a save are
    /// preserved on round-trip (<see cref="Extra"/>), so a newer optional field is never silently dropped by an older build.
    /// </para>
    /// </summary>
    public sealed class LocalProfile
    {
        /// <summary>Document family id; the shape revision is <see cref="SchemaVersion"/>.</summary>
        public const string SchemaId = "night-signal/local-profile@1";
        /// <summary>
        /// Bump with a registered <see cref="ProfileMigrations"/> step whenever the document shape changes.
        /// v2: performance parts are owned per car INSTANCE (<see cref="OwnedCar.Parts"/>); the v1 profile-level stack list
        /// became <see cref="UnassignedParts"/> (see <see cref="LocalGarage.MigrateV1ToV2"/>).
        /// </summary>
        public const int CurrentSchemaVersion = 2;
        public const int MaxAppliedOperations = 512;
        public const int MaxWalletHistory = 100;

        static readonly Regex IdPattern = new Regex("^[a-z0-9][a-z0-9_-]{2,63}$", RegexOptions.CultureInvariant);

        public string Schema { get; set; } = SchemaId;
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        /// <summary>Always <see cref="ProgressionDomain.Local"/>; a document claiming otherwise is refused on load.</summary>
        public ProgressionDomain Domain { get; set; } = ProgressionDomain.Local;
        /// <summary>Stable local profile id ("lp_…"), also the storage folder name.</summary>
        public string ProfileId { get; set; } = "";
        /// <summary>Validated by <see cref="LocalDisplayName"/>; rendered as literal text.</summary>
        public string DisplayName { get; set; } = "";
        public CardAppearance Card { get; set; } = new CardAppearance();
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        /// <summary>Incremented by every successful save; used for stale-copy conflict checks.</summary>
        public long Revision { get; set; }
        /// <summary>Content catalogue hash in force at the last applied operation (informational).</summary>
        public string ContentHash { get; set; } = "";

        public long WalletBalance { get; set; }
        /// <summary>Bounded itemised history for the Local wallet view (newest last).</summary>
        public List<WalletEntry> WalletHistory { get; set; } = new List<WalletEntry>();

        public string StarterCarModelId { get; set; } = "";
        /// <summary>Owned car INSTANCES (two instances of one model are independent).</summary>
        public List<OwnedCar> Cars { get; set; } = new List<OwnedCar>();
        /// <summary>
        /// Legacy (schema v1) part stacks that could not be attributed to exactly one car instance during migration. They
        /// grant NO ownership (parts are owned per instance, <see cref="OwnedCar.Parts"/>); they are kept so nothing is dropped.
        /// </summary>
        public List<OwnedPart> UnassignedParts { get; set; } = new List<OwnedPart>();
        /// <summary>Stored course entitlements (purchases and campaign unlocks). Starter courses are implicit (Core CourseAccess).</summary>
        public List<CourseEntitlement> Courses { get; set; } = new List<CourseEntitlement>();
        public CampaignClears Campaign { get; set; } = new CampaignClears();
        public List<CompletedChallenge> Challenges { get; set; } = new List<CompletedChallenge>();
        public List<OwnedCosmetic> Cosmetics { get; set; } = new List<OwnedCosmetic>();
        public List<MusicUnlock> Music { get; set; } = new List<MusicUnlock>();
        public PersonalBests Records { get; set; } = new PersonalBests { Domain = ProgressionDomain.Local };
        public TutorialState Tutorial { get; set; } = new TutorialState();
        /// <summary>Idempotency keys of applied local operations (bounded, newest last).</summary>
        public List<string> AppliedOperations { get; set; } = new List<string>();
        /// <summary>Explicit NON-PROGRESSION activity domain for the five diversions (Addendum 02 D209).</summary>
        public ToyWorkspace Toys { get; set; } = new ToyWorkspace();

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();

        public static bool IsValidId(string id) => id != null && IdPattern.IsMatch(id);

        // ---------------- read helpers ----------------

        public OwnedCar FindCar(string instanceId) => Cars.FirstOrDefault(c => c.InstanceId == instanceId);

        public HashSet<string> StoredCourseIds() => new HashSet<string>(Courses.Select(c => c.CourseId), StringComparer.Ordinal);

        /// <summary>Permanent access in this Local domain (starters implicit + stored entitlements).</summary>
        public bool OwnsCourse(ContentCatalogue catalogue, string courseId) => CourseAccess.Owns(catalogue, courseId, StoredCourseIds());

        public bool HasCue(string cueId) => Music.Any(m => m.CueId == cueId);
        public bool HasCompletedChallenge(string challengeId) => Challenges.Any(c => c.ChallengeId == challengeId);
        public bool OwnsCosmetic(string cosmeticId) => Cosmetics.Any(c => c.CosmeticId == cosmeticId);
        public bool HasApplied(string operationId) => operationId != null && AppliedOperations.Contains(operationId);

        /// <summary>Local mastery RP, recomputed from unique clears and challenges (finite: at most 15,000).</summary>
        public int ComputeRankPoints() =>
            RankPoints.Total(Campaign.Count(CampaignMode.Normal), Campaign.Count(CampaignMode.Hard),
                Challenges.Count(c => c.Tier == ChallengeTier.Bronze), Challenges.Count(c => c.Tier == ChallengeTier.Silver),
                Challenges.Count(c => c.Tier == ChallengeTier.Gold));

        public RankDefinition ComputeRank() => RankPoints.RankFor(ComputeRankPoints(), Campaign.Count(CampaignMode.Hard), Challenges.Count);

        /// <summary>Structural validation used before saving and after loading. Empty when the document is sound.</summary>
        public IReadOnlyList<string> Validate()
        {
            var e = new List<string>();
            if (Schema != SchemaId) e.Add($"Schema must be {SchemaId}.");
            if (SchemaVersion != CurrentSchemaVersion) e.Add($"Schema version must be {CurrentSchemaVersion}.");
            if (Domain != ProgressionDomain.Local) e.Add("A local profile belongs to the Local domain.");
            if (!IsValidId(ProfileId)) e.Add("Profile id is missing or malformed.");
            if (string.IsNullOrEmpty(DisplayName) || LocalDisplayName.CountGraphemes(DisplayName) > LocalDisplayName.MaxGraphemes)
                e.Add("Display name is missing or too long.");
            if (Revision < 0) e.Add("Revision cannot be negative.");
            if (WalletBalance < 0 || WalletBalance > Limits.WalletCap) e.Add($"Wallet must be 0..{Limits.WalletCap}.");
            if (Card == null) e.Add("Card appearance missing.");
            else
            {
                if (!string.IsNullOrEmpty(Card.Look))
                {
                    Characters.CharacterLook look = Characters.PlayerLooks.Parse(Card.Look);
                    if (look == null || Characters.PlayerLooks.Problems(look).Count > 0) e.Add("The card's driver look is not a valid look.");
                }
                if (!LocalProgression.PronounsOk(Card.Pronouns ?? "")) e.Add("Pronouns: up to 24 plain characters.");
            }
            if (Cars == null || UnassignedParts == null || Courses == null || Campaign == null || Challenges == null || Cosmetics == null ||
                Music == null || Records == null || Tutorial == null || AppliedOperations == null || Toys == null || WalletHistory == null)
            {
                e.Add("A required section is missing.");
                return e;
            }
            var instances = new HashSet<string>(StringComparer.Ordinal);
            foreach (OwnedCar car in Cars)
            {
                if (car == null || !IsValidId(car.InstanceId) || !instances.Add(car.InstanceId)) e.Add("Car instance ids must be present and unique.");
                else if (string.IsNullOrEmpty(car.ModelId)) e.Add($"Car {car.InstanceId} has no model id.");
                else if (car.Workspace == null) e.Add($"Car {car.InstanceId} has no workspace.");
                else if (car.Parts == null) e.Add($"Car {car.InstanceId} has no parts list.");
                else if (car.Parts.Any(p => p == null || string.IsNullOrEmpty(p.PartId) || p.Quantity != 1) ||
                         car.Parts.Select(p => p?.PartId).Distinct(StringComparer.Ordinal).Count() != car.Parts.Count)
                    e.Add($"Car {car.InstanceId}: owned parts must be present, unique and held once each.");
            }
            if (Courses.Any(c => c == null || string.IsNullOrEmpty(c.CourseId)) || Courses.Select(c => c?.CourseId).Distinct().Count() != Courses.Count)
                e.Add("Course entitlements must be present and unique.");
            e.AddRange(Campaign.Validate());
            if (Challenges.Any(c => c == null || string.IsNullOrEmpty(c.ChallengeId)) ||
                Challenges.Select(c => c?.ChallengeId).Distinct().Count() != Challenges.Count)
                e.Add("Completed challenges must be present and unique.");
            foreach (ChallengeTier t in new[] { ChallengeTier.Bronze, ChallengeTier.Silver, ChallengeTier.Gold })
                if (Challenges.Count(c => c != null && c.Tier == t) > RankPoints.ChallengesPerTier) e.Add($"Too many {t} challenges.");
            if (Music.Any(m => m == null || string.IsNullOrEmpty(m.CueId)) || Music.Select(m => m?.CueId).Distinct().Count() != Music.Count)
                e.Add("Soundtrack unlocks must be present and unique.");
            if (Records.Domain != ProgressionDomain.Local) e.Add("Local records belong to the Local domain.");
            else if (Records.Entries == null || Records.Archived == null || Records.Attempts == null || Records.Pending == null)
                e.Add("Record lists are missing.");
            else if (Records.Entries.Any(r => r?.Key == null || r.Key.Domain != ProgressionDomain.Local || r.Verification != RecordVerification.LocalUnverified))
                e.Add("Local records must be Local / Unverified.");
            else if (Records.Pending.Count > 0)
                e.Add("Local records are never pending verification.");
            if (Toys.ActivityDomain != ToyWorkspace.NonProgressionDomain) e.Add("The toy workspace must stay in the non-progression domain.");
            return e;
        }
    }

    public sealed class CardAppearance
    {
        public string AvatarId { get; set; } = "";
        public string BackgroundId { get; set; } = "";
        public string FrameId { get; set; } = "";
        public string MotifId { get; set; } = "";
        public string TitleId { get; set; } = "";
        /// <summary>Card layout id (customization.json "card"); "" = the default. Background/Frame/Motif/Title above likewise.</summary>
        public string LayoutId { get; set; } = "";
        /// <summary>Self-selected ISO 3166-1 alpha-2 region shown on the card ("" = none).</summary>
        public string Region { get; set; } = "";
        /// <summary>A car model the profile owns, shown on the card ("" = none).</summary>
        public string PreferredCar { get; set; } = "";
        /// <summary>The driver's look (canonical <c>CharacterLook</c> JSON, the online card's form); "" = the default look from the name.</summary>
        public string Look { get; set; } = "";
        /// <summary>Optional pronouns shown with the name (up to 24 plain characters).</summary>
        public string Pronouns { get; set; } = "";

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
    }

    public enum CarSource { Starter = 0, Purchased = 1, Reward = 2 }

    /// <summary>One owned car INSTANCE (Addendum 02 §9.1): stable instance id + model id + its own build workspace.</summary>
    public sealed class OwnedCar
    {
        /// <summary>Stable instance id ("ci_…"); two instances of the same model are independent.</summary>
        public string InstanceId { get; set; } = "";
        /// <summary>Car model id from the catalogue (V01…V18).</summary>
        public string ModelId { get; set; } = "";
        public CarSource Source { get; set; }
        /// <summary>What granted it (starter choice, purchase request id, …).</summary>
        public string SourceReference { get; set; } = "";
        public long PricePaid { get; set; }
        public DateTime AcquiredUtc { get; set; }
        public CarWorkspace Workspace { get; set; } = new CarWorkspace();
        /// <summary>
        /// Performance parts owned by THIS instance (schema v2). Never shared with another instance, never removed by
        /// applying, restoring or swapping a build; granted only by <see cref="LocalGarage.BuyAndApply"/> (or migration).
        /// </summary>
        public List<OwnedPart> Parts { get; set; } = new List<OwnedPart>();

        public bool OwnsPart(string partId) => partId != null && Parts != null && Parts.Any(p => p != null && p.PartId == partId);

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
    }

    /// <summary>
    /// Per-car-instance container for the build documents defined by <c>NightSignal.Core.Builds</c> (MechanicalLoadout,
    /// VisualPreset, AppliedVehicleBuild, BuildReference, GarageDraft, workspace state — Addendum 02 §9). This package
    /// deliberately stores them as schema-tagged opaque documents (<see cref="VersionedDocument"/>) so it neither duplicates
    /// nor constrains that model; the Builds package reads/writes them by schema id and version (via
    /// <see cref="LocalGarage.LoadWorkspace"/> / <see cref="LocalGarage.SaveWorkspace"/>).
    /// <para>
    /// Capacity rules for the Builds owner: at least <see cref="MinMechanicalLoadoutSlots"/> named mechanical loadouts and
    /// <see cref="MinVisualPresetSlots"/> visual presets per instance. There is NO maximum enforced here and no migration in
    /// this package ever trims these lists: an upgrade must never discard an existing named preset. Protected references are
    /// keyed by <see cref="BeforeWorkshop"/>, <see cref="BeforeLastApply"/> and <see cref="LastRaceBuild"/>.
    /// </para>
    /// </summary>
    public sealed class CarWorkspace
    {
        public const int MinMechanicalLoadoutSlots = 8;
        public const int MinVisualPresetSlots = 5;
        public const string BeforeWorkshop = "before-workshop";
        public const string BeforeLastApply = "before-last-apply";
        public const string LastRaceBuild = "last-race-build";
        public static readonly string[] ProtectedReferences = { BeforeWorkshop, BeforeLastApply, LastRaceBuild };

        public List<NamedDocument> MechanicalLoadouts { get; set; } = new List<NamedDocument>();
        public List<NamedDocument> VisualPresets { get; set; } = new List<NamedDocument>();
        /// <summary>Protected references keyed by the constants above.</summary>
        public Dictionary<string, VersionedDocument> References { get; set; } = new Dictionary<string, VersionedDocument>(StringComparer.Ordinal);
        /// <summary>The current applied build (never a draft or a preview).</summary>
        public VersionedDocument AppliedBuild { get; set; }
        /// <summary>The unsaved garage draft, kept separate from the applied build.</summary>
        public VersionedDocument GarageDraft { get; set; }
        /// <summary>Workspace state: revision (concurrency token), capacities, workshop session, applied visual/livery ids.</summary>
        public VersionedDocument WorkspaceState { get; set; }

        /// <summary>True when no build document has ever been stored for this car.</summary>
        [JsonIgnore]
        public bool IsEmpty =>
            (MechanicalLoadouts == null || MechanicalLoadouts.Count == 0) && (VisualPresets == null || VisualPresets.Count == 0) &&
            (References == null || References.Count == 0) && AppliedBuild == null && GarageDraft == null && WorkspaceState == null;

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
    }

    /// <summary>A user-named slot holding one opaque document (a mechanical loadout or visual preset).</summary>
    public sealed class NamedDocument
    {
        /// <summary>Stable slot id (not an array position).</summary>
        public string SlotId { get; set; } = "";
        public string Name { get; set; } = "";
        public VersionedDocument Document { get; set; }
    }

    /// <summary>
    /// An opaque, schema-tagged JSON document owned by another package. Its <see cref="Data"/> is preserved as-is through
    /// save/load (date-like strings are not re-parsed).
    /// </summary>
    public sealed class VersionedDocument
    {
        public string Schema { get; set; } = "";
        public int SchemaVersion { get; set; } = 1;
        public DateTime UpdatedUtc { get; set; }
        public JToken Data { get; set; }

        public VersionedDocument Clone() => new VersionedDocument
        {
            Schema = Schema, SchemaVersion = SchemaVersion, UpdatedUtc = UpdatedUtc, Data = Data?.DeepClone(),
        };
    }

    /// <summary>
    /// One performance part owned by one car instance (<see cref="OwnedCar.Parts"/>; ids from the parts catalogue). Ownership
    /// is a set: an instance holds a part once (<see cref="Quantity"/> = 1) and reusing it in any build is free.
    /// </summary>
    public sealed class OwnedPart
    {
        public string PartId { get; set; } = "";
        /// <summary>Always 1 for per-instance ownership (schema v1 stacks in <see cref="LocalProfile.UnassignedParts"/> may differ).</summary>
        public int Quantity { get; set; } = 1;
        /// <summary>buy-and-apply | migrated:{v1 source}</summary>
        public string Source { get; set; } = "";
        /// <summary>The settled Buy-and-Apply quote id (the durable local quote ledger), or "" for migrated parts.</summary>
        public string Reference { get; set; } = "";
        public long PricePaid { get; set; }
        public DateTime AcquiredUtc { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
    }

    /// <summary>A stored course right (purchase or campaign unlock). Starters are implicit and never stored.</summary>
    public sealed class CourseEntitlement
    {
        public string CourseId { get; set; } = "";
        public CourseEntitlementSource Source { get; set; }
        /// <summary>Unlocking stage id, or the purchase operation id.</summary>
        public string Reference { get; set; } = "";
        public long PricePaid { get; set; }
        public DateTime AcquiredUtc { get; set; }
    }

    /// <summary>Normal/Hard first clears as stage numbers (1..30).</summary>
    public sealed class CampaignClears
    {
        public List<int> Normal { get; set; } = new List<int>();
        public List<int> Hard { get; set; } = new List<int>();

        public List<int> For(CampaignMode mode) => mode == CampaignMode.Hard ? Hard : Normal;

        public int Count(CampaignMode mode) => For(mode).Count;

        public bool IsCleared(CampaignMode mode, int stageNumber) => For(mode).Contains(stageNumber);

        /// <summary>Flags in the shape Core <see cref="CampaignProgress"/> expects (index 0 = S01).</summary>
        public bool[] Flags(CampaignMode mode)
        {
            var flags = new bool[Limits.CampaignStages];
            foreach (int n in For(mode))
                if (n >= 1 && n <= Limits.CampaignStages) flags[n - 1] = true;
            return flags;
        }

        /// <summary>Hard unlocks after this profile's legitimate Normal finale clear (Core MemberProgress rule).</summary>
        [JsonIgnore]
        public bool HardUnlocked => new MemberProgress("local", Flags(CampaignMode.Normal), Flags(CampaignMode.Hard)).HardUnlocked;

        public IReadOnlyList<string> Validate()
        {
            var e = new List<string>();
            foreach (CampaignMode mode in new[] { CampaignMode.Normal, CampaignMode.Hard })
            {
                List<int> list = For(mode);
                if (list == null) { e.Add($"{mode} clears missing."); continue; }
                if (list.Any(n => n < 1 || n > Limits.CampaignStages) || list.Distinct().Count() != list.Count)
                    e.Add($"{mode} clears must be unique stage numbers 1..{Limits.CampaignStages}.");
            }
            return e;
        }
    }

    public sealed class CompletedChallenge
    {
        public string ChallengeId { get; set; } = "";
        public ChallengeTier Tier { get; set; }
        public string EventId { get; set; } = "";
        public DateTime CompletedUtc { get; set; }
    }

    public sealed class OwnedCosmetic
    {
        public string CosmeticId { get; set; } = "";
        /// <summary>Granting challenge id.</summary>
        public string Source { get; set; } = "";
        public DateTime AcquiredUtc { get; set; }
    }

    public sealed class MusicUnlock
    {
        public string CueId { get; set; } = "";
        public MusicUnlockSourceKind SourceKind { get; set; }
        /// <summary>Source stage or trial id ("" for baseline cues).</summary>
        public string SourceRef { get; set; } = "";
        public string EventId { get; set; } = "";
        public DateTime UnlockedUtc { get; set; }
    }

    public sealed class TutorialState
    {
        public bool Completed { get; set; }
        public DateTime? CompletedUtc { get; set; }
    }

    /// <summary>One itemised Local wallet movement. Debits are negative.</summary>
    public sealed class WalletEntry
    {
        public DateTime Utc { get; set; }
        /// <summary>starter | tutorial | event | first-clear | challenge | course-purchase | car-purchase | part-purchase</summary>
        public string Kind { get; set; } = "";
        public string Reference { get; set; } = "";
        public long Amount { get; set; }
        public long Applied { get; set; }
        public long ClampedAway { get; set; }
        public long BalanceAfter { get; set; }
    }

    /// <summary>
    /// The Local "toy workspace" for the five diversions (Cap Clash boards, Pit-Crew project and shelf, Greenlight results,
    /// Pocket Circuit boards/records, Canvas sheets — Addendum 02 §1.5). It lives in an explicit NON-PROGRESSION activity
    /// domain (D209): nothing stored here can grant Credits, RP, challenges, records, course rights, cars, parts or music,
    /// and no <see cref="LocalProgression"/> operation reads it. Documents are opaque, versioned and keyed by activity id;
    /// their models are owned by the toy packages. They persist atomically with the profile until the owner resets them.
    /// </summary>
    public sealed class ToyWorkspace
    {
        public const string NonProgressionDomain = "non-progression";
        public const string CapClash = "cap-clash";
        public const string PitCrew = "pit-crew";
        public const string Greenlight = "greenlight";
        public const string PocketCircuit = "pocket-circuit";
        public const string Canvas = "canvas";
        public static readonly string[] KnownActivities = { CapClash, PitCrew, Greenlight, PocketCircuit, Canvas };
        /// <summary>Upper bound for one serialized activity document (characters), keeping saves bounded.</summary>
        public const int MaxDocumentChars = 2 * 1024 * 1024;

        static readonly Regex ActivityIdPattern = new Regex("^[a-z0-9][a-z0-9-]{1,47}$", RegexOptions.CultureInvariant);

        /// <summary>Always <see cref="NonProgressionDomain"/>.</summary>
        public string ActivityDomain { get; set; } = NonProgressionDomain;
        public Dictionary<string, VersionedDocument> Documents { get; set; } = new Dictionary<string, VersionedDocument>(StringComparer.Ordinal);

        public static bool IsValidActivityId(string id) => id != null && ActivityIdPattern.IsMatch(id);

        public VersionedDocument Get(string activityId) =>
            activityId != null && Documents.TryGetValue(activityId, out VersionedDocument d) ? d : null;

        /// <summary>Stores a copy of <paramref name="document"/> for <paramref name="activityId"/>.</summary>
        public void Put(string activityId, VersionedDocument document)
        {
            if (!IsValidActivityId(activityId)) throw new ArgumentException($"Malformed activity id '{activityId}'", nameof(activityId));
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrEmpty(document.Schema)) throw new ArgumentException("Toy documents need a schema id", nameof(document));
            string text = document.Data == null ? "" : document.Data.ToString(Formatting.None);
            if (text.Length > MaxDocumentChars) throw new ArgumentException($"Toy document for {activityId} exceeds {MaxDocumentChars} characters");
            Documents[activityId] = document.Clone();
        }

        /// <summary>Owner-requested reset of one activity. Returns false when there was nothing to reset.</summary>
        public bool Reset(string activityId) => activityId != null && Documents.Remove(activityId);
    }
}
