namespace NightSignal.ControlPlane.Persistence;

// ONLINE Garage persistence (Addendum 02 §8–10). The store is deliberately dumb about builds: the Garage service computes
// every decision with Core (NightSignal.Core.Builds) and the store only guarantees atomicity, optimistic concurrency and
// uniqueness (instance/part, quote id, ledger key).

/// <summary>One owned car INSTANCE (stable id; two instances of one model are independent).</summary>
public sealed record CarInstance(string InstanceId, string AccountId, string CarId, int Ordinal, string Source);

/// <summary>The stored Core CarBuildWorkspace document of one instance and its optimistic-concurrency revision.</summary>
public sealed record StoredWorkspace(string InstanceId, string AccountId, long Revision, string Json);

/// <summary>A new workspace state to write (the JSON is authoritative; the applied_* values are a denormalised copy).</summary>
public sealed record WorkspaceWrite(long Revision, int SchemaVersion, string Json, long AppliedRevision, string AppliedBuildHash, int AppliedPi);

public enum WorkspaceSaveStatus { Saved, Stale, NotFound }

/// <summary>An issued Buy-and-Apply quote (the Core PurchaseAndApplyQuote as JSON).</summary>
public sealed record StoredQuote(string QuoteId, string AccountId, string InstanceId, string QuoteJson, long Total, long ExpiresAtMs);

/// <summary>What a Buy-and-Apply decision sees INSIDE the settlement transaction (wallet row locked).</summary>
public sealed class GarageSettleView
{
    public required string AccountId { get; init; }
    public required string QuoteId { get; init; }
    /// <summary>Null when the quote does not exist or belongs to another account.</summary>
    public StoredQuote? Quote { get; init; }
    public CarInstance? Instance { get; init; }
    public StoredWorkspace? Workspace { get; init; }
    public long Balance { get; init; }
    public IReadOnlyCollection<string> OwnedParts { get; init; } = Array.Empty<string>();
    /// <summary>The stored Core SettlementRecord JSON when this quote id was already settled.</summary>
    public string? PriorRecordJson { get; init; }
}

public sealed record PartGrant(string PartId, long Price);

/// <summary>
/// The decision's writes. When <see cref="Commit"/> is false nothing is written (rejections and replays). When true the
/// store writes the settlement row (unique quote id), the ledger debit, every part grant, the workspace change (compare-and-
/// swap on the revision it read) and the new balance in the same transaction, or nothing.
/// </summary>
public sealed class GarageSettleDecision<T>
{
    public required T Result { get; init; }
    public bool Commit { get; init; }
    public long Debit { get; init; }
    public long NewBalance { get; init; }
    public IReadOnlyList<PartGrant> Grants { get; init; } = Array.Empty<PartGrant>();
    public WorkspaceWrite? Workspace { get; init; }
    public long AppliedRevision { get; init; }
    public string BuildHash { get; init; } = "";
    public string RecordJson { get; init; } = "";
}

public interface IGarageStore
{
    /// <summary>Creates an instance (ordinal 1, deterministic id) for every owned car that has none; returns all instances.</summary>
    Task<IReadOnlyList<CarInstance>> EnsureCarInstancesAsync(string accountId, CancellationToken ct = default);
    Task<CarInstance?> GetCarInstanceAsync(string accountId, string instanceId, CancellationToken ct = default);
    Task<StoredWorkspace?> GetWorkspaceAsync(string instanceId, CancellationToken ct = default);
    /// <summary>Inserts the first workspace of an instance; false when one already exists (a concurrent creator won).</summary>
    Task<bool> CreateWorkspaceAsync(string accountId, string instanceId, WorkspaceWrite write, CancellationToken ct = default);
    /// <summary>Compare-and-swap: writes only when the stored revision still equals <paramref name="expectedRevision"/>.</summary>
    Task<WorkspaceSaveStatus> SaveWorkspaceAsync(string accountId, string instanceId, long expectedRevision, WorkspaceWrite write, CancellationToken ct = default);
    Task<IReadOnlyCollection<string>> GetOwnedPartsAsync(string instanceId, CancellationToken ct = default);
    /// <summary>Stores an issued quote (an identical id is kept once).</summary>
    Task SaveQuoteAsync(StoredQuote quote, CancellationToken ct = default);
    Task<StoredQuote?> GetQuoteAsync(string accountId, string quoteId, CancellationToken ct = default);
    /// <summary>
    /// Runs <paramref name="decide"/> inside ONE write transaction that serialises the account's wallet (SQLite BEGIN
    /// IMMEDIATE / PostgreSQL SELECT … FOR UPDATE) and writes the decision's effects atomically. The delegate must be pure:
    /// it can be re-run after a lost UNIQUE race.
    /// </summary>
    Task<T> SettleQuoteAsync<T>(string accountId, string quoteId, Func<GarageSettleView, GarageSettleDecision<T>> decide, CancellationToken ct = default);
}
