using NightSignal.ControlPlane.Players;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Persistence;

// Small persistence interfaces (spec §3.2: PlayerStore, ResultLedger). Implementations: SqliteGameStore
// (local development/tests, executed) and PostgresGameStore (Supabase, compiles but not executed here).

/// <summary>The Player Card: display name, optional driver look (canonical JSON, Core PlayerLooks) and pronouns.</summary>
public sealed record PlayerCard(string DisplayName, long Revision, string? LookJson = null, string? Pronouns = null, string? StyleJson = null,
    string? ShowcaseJson = null);

/// <summary>
/// One of a player's own records, derived from their settled results (spec §11 "chosen showcase records"): a campaign
/// stage's best finish per mode, a freeplay course's best finish per format (Drift Attack: the best banked raw score), a
/// Team Trial's team best. <see cref="Key"/> is stable ("stage:S07:normal", "course:C01:sprint", "course:C01:drift-attack",
/// "team:TT_BEST:normal:2"); label and value are display text.
/// </summary>
public sealed record PersonalRecord(string Key, string Label, string Value);

/// <summary>Optional card fields for a write; null leaves the stored value unchanged, "" clears it.</summary>
public sealed record CardExtras(string? LookJson, string? Pronouns, string? StyleJson = null, string? ShowcaseJson = null);

public sealed record OwnedCar(string CarId, string Source);

public sealed record ChallengeUnlock(string ChallengeId, string Tier);

/// <summary>A stored course entitlement (ONLINE domain): "purchase" or "campaign-clear". Starters are implicit.</summary>
public sealed record CourseEntitlement(string CourseId, string Source);

public sealed record MusicEntitlement(string CueId, string SourceKind, string SourceRef);

public sealed record PlayerHandle(string Display, string Canonical, long Revision);

public sealed record TeamBestRecord(string TrialId, string Difficulty, int Humans, string Kind, long Value, string MatchId);

public sealed class PlayerSnapshot
{
    public required string AccountId { get; init; }
    public PlayerCard? Card { get; init; }
    public PlayerHandle? Handle { get; init; }
    public long Balance { get; init; }
    public required IReadOnlyList<OwnedCar> Cars { get; init; }
    public required bool[] NormalCleared { get; init; }
    public required bool[] HardCleared { get; init; }
    public required IReadOnlyList<ChallengeUnlock> Challenges { get; init; }
    public required IReadOnlyList<string> Cosmetics { get; init; }
    public string? StarterCarId { get; init; }
    public IReadOnlyList<CourseEntitlement> StoredCourses { get; init; } = Array.Empty<CourseEntitlement>();
    public IReadOnlyList<MusicEntitlement> Music { get; init; } = Array.Empty<MusicEntitlement>();
    public IReadOnlyList<TeamBestRecord> TeamBests { get; init; } = Array.Empty<TeamBestRecord>();

    public MemberProgress ToProgress() => new(AccountId, NormalCleared, HardCleared);

    /// <summary>Stored entitlements plus courses derived from stored Normal clears (see <see cref="CourseOwnership"/>).</summary>
    public IReadOnlyDictionary<string, string> OwnedCourses(ContentCatalogue catalogue) => CourseOwnership.Owned(catalogue, StoredCourses, NormalCleared);
}

/// <summary>
/// Effective ONLINE course ownership (excluding the implicit starters, which Core CourseAccess.Owns adds): every stored
/// entitlement, plus every course Core <c>CourseAccess.GrantedByNormalClear</c> maps to a stored Normal clear. Settlement
/// stores the campaign-clear rows explicitly; the derivation also covers clears recorded before the course ledger existed,
/// so no earned progress is lost and no migration has to guess the stage mapping in SQL.
/// </summary>
public static class CourseOwnership
{
    public static IReadOnlyDictionary<string, string> Owned(ContentCatalogue catalogue, IEnumerable<CourseEntitlement> stored, bool[] normalCleared)
    {
        var owned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CourseEntitlement e in stored) owned.TryAdd(e.CourseId, e.Source);
        foreach (StageDef stage in catalogue.Stages)
            if (stage.Number >= 1 && stage.Number <= normalCleared.Length && normalCleared[stage.Number - 1])
                foreach (string course in CourseAccess.GrantedByNormalClear(catalogue, stage.Id))
                    owned.TryAdd(course, "campaign-clear");
        return owned;
    }

    /// <summary>The /v1/me listing: starters, then owned courses, each with its source (catalogue order).</summary>
    public static IReadOnlyList<CourseEntitlement> Listing(ContentCatalogue catalogue, IReadOnlyDictionary<string, string> owned) =>
        catalogue.Courses
            .Select(c => CourseAccess.RuleFor(catalogue, c.Id).Kind == CourseAccessKind.Starter
                ? new CourseEntitlement(c.Id, "starter")
                : owned.TryGetValue(c.Id, out string? source) ? new CourseEntitlement(c.Id, source) : null)
            .Where(e => e is not null).Select(e => e!).ToList();
}

public enum WriteStatus { Ok, Replayed, Conflict, InsufficientFunds, AlreadyOwned, Invalid }

public sealed record CardWriteResult(WriteStatus Status, PlayerCard? Card);

public sealed record StarterResult(WriteStatus Status, string? CarId, long Balance, long Credited);

/// <summary>Price comes from the trusted catalogue, never from the client.</summary>
public sealed record PurchaseRequest(string AccountId, string IdempotencyKey, string ItemKind, string ItemId, long Price);

public sealed record PurchaseResult(WriteStatus Status, long Balance, string? Message = null);

public enum CoursePurchaseStatus { Purchased, Replayed, AlreadyOwned, NotPurchasable, InsufficientFunds, Conflict }

/// <summary>Outcome of a course purchase; <see cref="Charged"/> is what this call (or the replayed original) debited.</summary>
public sealed record CoursePurchaseResult(CoursePurchaseStatus Status, long Balance, long Price, long Charged);

public enum HandleClaimStatus { Claimed, Changed, Unchanged, Taken }

public sealed record HandleClaimResult(HandleClaimStatus Status, PlayerHandle? Handle);

/// <summary>Public Player Card: never e-mail, tokens, wallet or private inventory.</summary>
public sealed record PublicCard(string AccountId, string? Handle, string? DisplayName, RankSummary Rank,
    string? Pronouns = null, int NormalClears = 0, int HardClears = 0, int Challenges = 0, string? StyleJson = null, string? ShowcaseJson = null);

public enum FriendState { None, OutgoingPending, IncomingPending, Friends }

public sealed record Relationship(FriendState State, bool BlockedByMe, bool BlockedMe)
{
    public bool BlockedEitherWay => BlockedByMe || BlockedMe;
}

public enum FriendOpStatus { Ok, NotFound, Blocked, Invalid }

/// <summary>Result of an idempotent friend operation: the resulting state and whether this call changed anything.</summary>
public sealed record FriendOpResult(FriendOpStatus Status, FriendState State, bool Changed, string? Message = null);

public sealed record FriendEdge(string AccountId, DateTimeOffset Since);

public sealed record FriendGraph(IReadOnlyList<FriendEdge> Friends, IReadOnlyList<FriendEdge> Incoming, IReadOnlyList<FriendEdge> Outgoing,
    IReadOnlyList<FriendEdge> Blocked);

public interface IPlayerStore
{
    /// <summary>Creates the account row (id = JWT sub) and an empty wallet if absent.</summary>
    Task EnsureAccountAsync(string accountId, CancellationToken ct = default);
    Task<PlayerSnapshot> GetSnapshotAsync(string accountId, CancellationToken ct = default);
    /// <summary>The account's personal records (best finishes per stage/mode and course/format, team bests), for the card's showcase.</summary>
    Task<IReadOnlyList<PersonalRecord>> PersonalRecordsAsync(string accountId, ContentCatalogue catalogue, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, MemberProgress>> GetProgressAsync(IReadOnlyCollection<string> accountIds, CancellationToken ct = default);
    /// <summary>Versioned write: when <paramref name="expectedRevision"/> is given it must match (0 = no card yet).</summary>
    Task<CardWriteResult> UpsertCardAsync(string accountId, string displayName, long? expectedRevision, CancellationToken ct = default);
    /// <summary>As above, also setting the appearance and/or pronouns (null fields are left as stored).</summary>
    Task<CardWriteResult> UpsertCardAsync(string accountId, string displayName, long? expectedRevision, CardExtras? extras, CancellationToken ct = default);
    /// <summary>
    /// A challenge completed outside a race (the meet's touring challenges): the unlock, its cosmetic and its cash exactly
    /// once, in one transaction with an append-only ledger entry (<c>challenge/&lt;account&gt;/&lt;id&gt;</c>).
    /// </summary>
    Task<ChallengeGrantResult> GrantChallengeAsync(string accountId, ChallengeGrant grant, string source, CancellationToken ct = default);
    /// <summary>Records a race-diary entry as read (idempotent); true when it was new (CH70 reads them).</summary>
    Task<bool> RecordDiaryReadAsync(string accountId, string entryId, CancellationToken ct = default);
    /// <summary>The race-diary entries the account has read.</summary>
    Task<IReadOnlyList<string>> DiaryReadsAsync(string accountId, CancellationToken ct = default);
    /// <summary>Whether the account has a settled event it finished (touring CH65 reads the result slip after one).</summary>
    Task<bool> HasFinishedEventAsync(string accountId, CancellationToken ct = default);
    /// <summary>Once per account: owns the starter car and credits the starter grant.</summary>
    Task<StarterResult> ClaimStarterAsync(string accountId, string carId, long credits, CancellationToken ct = default);
    /// <summary>Exactly-once under the idempotency key; rejects insufficient funds and invalid prices.</summary>
    Task<PurchaseResult> PurchaseAsync(PurchaseRequest request, CancellationToken ct = default);

    /// <summary>Effective owned courses (stored + derived from Normal clears; starters excluded) for several accounts.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyCollection<string>>> GetOwnedCoursesAsync(IReadOnlyCollection<string> accountIds,
        ContentCatalogue catalogue, CancellationToken ct = default);

    /// <summary>
    /// Course purchase (Addendum 01 §5.1): Core <c>CourseAccess.DecidePurchase</c> runs inside the same transaction as the
    /// wallet debit and the entitlement insert, serialized per wallet. Already owned (including a campaign unlock that
    /// committed first) → no debit. Exactly once per idempotency key.
    /// </summary>
    Task<CoursePurchaseResult> PurchaseCourseAsync(string accountId, string idempotencyKey, string courseId, ContentCatalogue catalogue,
        CancellationToken ct = default);

    /// <summary>Idempotent soundtrack grant keyed by (account, cue). Returns true when this call granted it.</summary>
    Task<bool> GrantMusicCueAsync(string accountId, string cueId, string sourceKind, string sourceRef, string? matchId, CancellationToken ct = default);
}

/// <summary>Public handles, cards and friend relationships (Addendum 01 §9). All keyed by stable account IDs.</summary>
public interface ISocialStore
{
    /// <summary>Claims or changes the account's handle; uniqueness is enforced atomically by the canonical UNIQUE constraint.</summary>
    Task<HandleClaimResult> ClaimHandleAsync(string accountId, string display, string canonical, CancellationToken ct = default);
    Task<PublicCard?> FindByHandleAsync(string canonical, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, PublicCard>> GetPublicCardsAsync(IReadOnlyCollection<string> accountIds, CancellationToken ct = default);

    Task<FriendOpResult> SendFriendRequestAsync(string from, string to, CancellationToken ct = default);
    Task<FriendOpResult> CancelFriendRequestAsync(string from, string to, CancellationToken ct = default);
    Task<FriendOpResult> AcceptFriendRequestAsync(string me, string from, CancellationToken ct = default);
    Task<FriendOpResult> DeclineFriendRequestAsync(string me, string from, CancellationToken ct = default);
    Task<FriendOpResult> RemoveFriendAsync(string me, string other, CancellationToken ct = default);
    Task<FriendOpResult> BlockAsync(string me, string other, CancellationToken ct = default);
    Task<FriendOpResult> UnblockAsync(string me, string other, CancellationToken ct = default);
    Task<FriendGraph> GetFriendGraphAsync(string me, CancellationToken ct = default);
    Task<Relationship> GetRelationshipAsync(string me, string other, CancellationToken ct = default);
}

public sealed class MatchRecord
{
    public required string MatchId { get; init; }
    public required string ConvoyId { get; init; }
    public required string ServerId { get; init; }
    public required string ConfigJson { get; init; }
    public required string ResultsSecret { get; init; }
    public string State { get; init; } = "allocated";
    public string? ResultsSha256 { get; init; }
}

public sealed record StageClearCandidate(CampaignMode Mode, int StageNumber, StageType Type);

public sealed record ChallengeGrant(string ChallengeId, ChallengeTier Tier, long Cash, int RankPoints, string CosmeticId);

/// <summary>A challenge granted outside a race: whether it was new, the cash credited and the balance after.</summary>
public sealed record ChallengeGrantResult(bool Granted, long Credited, long Balance);

/// <summary>A soundtrack cue to grant (idempotently) with this settlement.</summary>
public sealed record MusicGrant(string CueId, string SourceKind, string SourceRef);

/// <summary>A Team Trial team result to record as a TEAM best (never a personal best) when it improves the stored one.</summary>
public sealed record TeamBestCandidate(string TrialId, string Difficulty, int Humans, string Kind, long Value, bool LowerIsBetter);

/// <summary>One human entrant's settlement input, computed from server-observed facts with Core rules.</summary>
public sealed class EntrantSettlement
{
    public required string AccountId { get; init; }
    /// <summary>First-clear bonus and challenge cash are filled in by the ledger inside the transaction.</summary>
    public required PayoutFacts Facts { get; init; }
    /// <summary>Present only when <see cref="StageOutcome"/> says this entrant earned the stage clear.</summary>
    public StageClearCandidate? Clear { get; init; }
    public IReadOnlyList<ChallengeGrant> Challenges { get; init; } = Array.Empty<ChallengeGrant>();
    /// <summary>Courses Core <c>CourseAccess.GrantedByNormalClear</c> maps to <see cref="Clear"/>; stored only when that clear is valid.</summary>
    public IReadOnlyList<string> ClearCourseGrants { get; init; } = Array.Empty<string>();
    /// <summary>Soundtrack cues sourced by <see cref="Clear"/>; granted only when that clear is valid.</summary>
    public IReadOnlyList<MusicGrant> ClearMusicGrants { get; init; } = Array.Empty<MusicGrant>();
    /// <summary>Soundtrack cues granted by this result regardless of a stage clear (e.g. a first Team Trial victory).</summary>
    public IReadOnlyList<MusicGrant> MusicGrants { get; init; } = Array.Empty<MusicGrant>();
    /// <summary>When set, no event money is paid and this is the explanation (e.g. a Team Trial human without a legal finish).</summary>
    public string? PayoutWithheld { get; init; }
    public TeamBestCandidate? TeamBest { get; init; }
    /// <summary>Receipt pre-filled with placing/verdict context; money and RP fields are completed by the ledger.</summary>
    public required Receipt Receipt { get; init; }
}

public sealed class MatchSettlement
{
    public required string MatchId { get; init; }
    /// <summary>SHA-256 of the raw result body: an identical retry replays, a different body conflicts.</summary>
    public required string ResultsSha256 { get; init; }
    public required IReadOnlyList<EntrantSettlement> Entrants { get; init; }
}

public enum SettlementStatus { Settled, AlreadySettled, Conflict, NotFound, Aborted }

public sealed record SettlementOutcome(SettlementStatus Status, IReadOnlyList<Receipt> Receipts);

public interface IResultLedger
{
    Task RecordAllocationAsync(MatchRecord match, CancellationToken ct = default);
    Task<MatchRecord?> GetMatchAsync(string matchId, CancellationToken ct = default);
    /// <summary>Marks an unsettled match aborted (no rewards). Returns false if it was already settled.</summary>
    Task<bool> MarkAbortedAsync(string matchId, CancellationToken ct = default);
    /// <summary>Money, first clears, challenges, cosmetics and receipts in ONE transaction; idempotent per match.</summary>
    Task<SettlementOutcome> SettleAsync(MatchSettlement settlement, CancellationToken ct = default);
    Task<string?> GetReceiptJsonAsync(string matchId, string accountId, CancellationToken ct = default);
    /// <summary>Every course the account has a settled finish on (from its receipts; cumulative challenges CH66, CH71).</summary>
    Task<IReadOnlyCollection<string>> FinishedCoursesAsync(string accountId, CancellationToken ct = default);
    /// <summary>The account's settled Freeplay races, oldest first, as the archetype challenges read them (CH38, CH73).</summary>
    Task<IReadOnlyList<ArchetypeRace>> FreeplayRacesAsync(string accountId, CancellationToken ct = default);
    /// <summary>The challenge trials the account has passed in settled matches (from its receipts; docs/CHALLENGE_TRIALS.md).</summary>
    Task<IReadOnlyCollection<string>> TrialPassesAsync(string accountId, CancellationToken ct = default);
    /// <summary>Ghosts (spec §8): the game server's recording for an entrant, held until settlement.</summary>
    Task StoreMatchGhostAsync(string matchId, string accountId, string json, CancellationToken ct = default);
    Task<string?> MatchGhostAsync(string matchId, string accountId, CancellationToken ct = default);
    /// <summary>The account's kept ghosts for a course and format (one per ruleset), fastest first.</summary>
    Task<IReadOnlyList<StoredGhost>> GhostsAsync(string accountId, string courseId, string format, CancellationToken ct = default);
    /// <summary>Keeps a validated ghost as the account's best for its course, format and ruleset when faster; true when kept.</summary>
    Task<bool> OfferGhostAsync(string accountId, string courseId, string format, string rulesKey, long resultMicros, string matchId, string json,
        CancellationToken ct = default);
}

/// <summary>Itemized reward receipt returned to the player (and stored verbatim in match_results).</summary>
public sealed class Receipt
{
    public string MatchId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Status { get; set; } = "settled";
    public string EventKind { get; set; } = "";
    public string? Mode { get; set; }
    public string? StageId { get; set; }
    public string CourseId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public int Placement { get; set; }
    public bool Tied { get; set; }
    public long? FinishTimeMs { get; set; }
    /// <summary>Drift Attack: the banked raw drift score the event was ranked by (finished runs only; null elsewhere).</summary>
    public long? RawDriftScore { get; set; }
    public StageVerdictInfo? Stage { get; set; }
    public PayoutInfo Payout { get; set; } = new();
    public List<CreditLine> Credits { get; set; } = new();
    public long BalanceAfter { get; set; }
    public long ClampedAwayTotal { get; set; }
    public bool FirstClearAwarded { get; set; }
    public List<string> ChallengesUnlocked { get; set; } = new();
    public List<string> CosmeticsGranted { get; set; } = new();
    /// <summary>Courses newly unlocked for free by this result's Normal clear (Addendum 01 §5.1).</summary>
    public List<string> CoursesUnlocked { get; set; } = new();
    /// <summary>Soundtrack cues newly added to the collection by this result (Addendum 01 §11.3).</summary>
    public List<string> MusicUnlocked { get; set; } = new();
    /// <summary>Event-scoped guest access this entrant raced under (never permanent ownership).</summary>
    public GuestPassInfo? GuestPass { get; set; }
    public TeamTrialReceipt? TeamTrial { get; set; }
    public ChallengeTrialReceipt? ChallengeTrial { get; set; }
    public int RankPointsBefore { get; set; }
    public int RankPointsAfter { get; set; }
    public string Rank { get; set; } = "";
    public List<string> Notes { get; set; } = new();
}

public sealed class GuestPassInfo
{
    public string CourseId { get; set; } = "";
    public string SponsorId { get; set; } = "";
}

/// <summary>A challenge trial's verdict on a receipt (docs/CHALLENGE_TRIALS.md): the game server's reasons, and the group's passes.</summary>
public sealed class ChallengeTrialReceipt
{
    public string TrialId { get; set; } = "";
    public string Challenge { get; set; } = "";
    public bool Passed { get; set; }
    public string Summary { get; set; } = "";
    /// <summary>Trials of the challenge passed so far (this run included) and how many it needs (CH54: two layouts).</summary>
    public int GroupPassed { get; set; }
    public int GroupSize { get; set; }
}

/// <summary>Team Trial outcome on a receipt. Team values are TEAM records, never personal bests.</summary>
public sealed class TeamTrialReceipt
{
    public string TrialId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Difficulty { get; set; } = "";
    public int Humans { get; set; }
    public int FriendlyAi { get; set; }
    /// <summary>Core TeamScore.Value per side (MEAN: sum of six contributions, ms; BEST: ms, null when nobody finished;
    /// DRIFT: summed raw score).</summary>
    public long? PlayerTeamValue { get; set; }
    public long? OpposingTeamValue { get; set; }
    public double? PlayerTeamMeanMs { get; set; }
    public string Verdict { get; set; } = "";
    public List<TeamContribution> Contributions { get; set; } = new();
    public bool CompletionPayable { get; set; }
    public bool NewTeamBest { get; set; }
    public string RecordCategory { get; set; } = "team";
    public bool Provisional { get; set; }
}

public sealed class TeamContribution
{
    public string EntrantId { get; set; } = "";
    public bool Human { get; set; }
    public string Team { get; set; } = "";
    public long? Value { get; set; }
}

public sealed class StageVerdictInfo
{
    public bool Qualified { get; set; }
    public bool WithinSupport { get; set; }
    public bool EarnedClear { get; set; }
    public bool TeamSuccess { get; set; }
    public int Qualifiers { get; set; }
    public int RequiredQualifiers { get; set; }
    public int FrozenHumanCount { get; set; }
    public string Reason { get; set; } = "";
    public long BenchmarkTargetMs { get; set; }
    public bool BenchmarkProvisional { get; set; }
    public string BenchmarkSource { get; set; } = "";
    /// <summary>Encounter stages: the qualifying human must also beat this live featured rival (Addendum 01 §1.3).</summary>
    public bool RequiresBeatingFeaturedRival { get; set; }
    public string? FeaturedRival { get; set; }
    public bool BeatFeaturedRival { get; set; }
}

/// <summary>Mirror of Core's PayoutBreakdown (multipliers in hundredths).</summary>
public sealed class PayoutInfo
{
    public long Base { get; set; }
    public int DifficultyX100 { get; set; } = 100;
    public int PlacementX100 { get; set; } = 100;
    public int CleanlinessX100 { get; set; } = 100;
    public int UtilityX100 { get; set; } = 100;
    public int PvPX100 { get; set; } = 100;
    public long EventCredits { get; set; }
    public long FirstClearBonus { get; set; }
    public long ChallengeCash { get; set; }
    public long Total { get; set; }
    public string Note { get; set; } = "";

    public static PayoutInfo From(PayoutBreakdown b) => new()
    {
        Base = b.Base, DifficultyX100 = b.DifficultyX100, PlacementX100 = b.PlacementX100,
        CleanlinessX100 = b.CleanlinessX100, UtilityX100 = b.UtilityX100, PvPX100 = b.PvPX100,
        EventCredits = b.EventCredits, FirstClearBonus = b.FirstClearBonus, ChallengeCash = b.ChallengeCash,
        Total = b.Total, Note = b.Note,
    };
}

/// <summary>One wallet credit: requested by the rules, actually credited, and lost to the wallet cap.</summary>
public sealed record CreditLine(string Type, long Requested, long Credited, long ClampedAway);
