using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Persistence;

// Small persistence interfaces (spec §3.2: PlayerStore, ResultLedger). Implementations: SqliteGameStore
// (local development/tests, executed) and PostgresGameStore (Supabase, compiles but not executed here).

public sealed record PlayerCard(string DisplayName, long Revision);

public sealed record OwnedCar(string CarId, string Source);

public sealed record ChallengeUnlock(string ChallengeId, string Tier);

public sealed class PlayerSnapshot
{
    public required string AccountId { get; init; }
    public PlayerCard? Card { get; init; }
    public long Balance { get; init; }
    public required IReadOnlyList<OwnedCar> Cars { get; init; }
    public required bool[] NormalCleared { get; init; }
    public required bool[] HardCleared { get; init; }
    public required IReadOnlyList<ChallengeUnlock> Challenges { get; init; }
    public required IReadOnlyList<string> Cosmetics { get; init; }
    public string? StarterCarId { get; init; }

    public MemberProgress ToProgress() => new(AccountId, NormalCleared, HardCleared);
}

public enum WriteStatus { Ok, Replayed, Conflict, InsufficientFunds, AlreadyOwned, Invalid }

public sealed record CardWriteResult(WriteStatus Status, PlayerCard? Card);

public sealed record StarterResult(WriteStatus Status, string? CarId, long Balance, long Credited);

/// <summary>Price comes from the trusted catalogue, never from the client.</summary>
public sealed record PurchaseRequest(string AccountId, string IdempotencyKey, string ItemKind, string ItemId, long Price);

public sealed record PurchaseResult(WriteStatus Status, long Balance, string? Message = null);

public interface IPlayerStore
{
    /// <summary>Creates the account row (id = JWT sub) and an empty wallet if absent.</summary>
    Task EnsureAccountAsync(string accountId, CancellationToken ct = default);
    Task<PlayerSnapshot> GetSnapshotAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, MemberProgress>> GetProgressAsync(IReadOnlyCollection<string> accountIds, CancellationToken ct = default);
    /// <summary>Versioned write: when <paramref name="expectedRevision"/> is given it must match (0 = no card yet).</summary>
    Task<CardWriteResult> UpsertCardAsync(string accountId, string displayName, long? expectedRevision, CancellationToken ct = default);
    /// <summary>Once per account: owns the starter car and credits the starter grant.</summary>
    Task<StarterResult> ClaimStarterAsync(string accountId, string carId, long credits, CancellationToken ct = default);
    /// <summary>Exactly-once under the idempotency key; rejects insufficient funds and invalid prices.</summary>
    Task<PurchaseResult> PurchaseAsync(PurchaseRequest request, CancellationToken ct = default);
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

/// <summary>One human entrant's settlement input, computed from server-observed facts with Core rules.</summary>
public sealed class EntrantSettlement
{
    public required string AccountId { get; init; }
    /// <summary>First-clear bonus and challenge cash are filled in by the ledger inside the transaction.</summary>
    public required PayoutFacts Facts { get; init; }
    /// <summary>Present only when <see cref="StageOutcome"/> says this entrant earned the stage clear.</summary>
    public StageClearCandidate? Clear { get; init; }
    public IReadOnlyList<ChallengeGrant> Challenges { get; init; } = Array.Empty<ChallengeGrant>();
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
    public StageVerdictInfo? Stage { get; set; }
    public PayoutInfo Payout { get; set; } = new();
    public List<CreditLine> Credits { get; set; } = new();
    public long BalanceAfter { get; set; }
    public long ClampedAwayTotal { get; set; }
    public bool FirstClearAwarded { get; set; }
    public List<string> ChallengesUnlocked { get; set; } = new();
    public List<string> CosmeticsGranted { get; set; } = new();
    public int RankPointsBefore { get; set; }
    public int RankPointsAfter { get; set; }
    public string Rank { get; set; } = "";
    public List<string> Notes { get; set; } = new();
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
