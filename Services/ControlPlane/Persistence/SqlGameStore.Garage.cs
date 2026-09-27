using System.Data.Common;
using System.Text.Json;
using NightSignal.ControlPlane.Security;

namespace NightSignal.ControlPlane.Persistence;

/// <summary>
/// ONLINE Garage storage (Addendum 02 §8–10): car instances, per-instance part ownership, whole-workspace documents with
/// optimistic concurrency, issued quotes and the quote ledger. See migrations 0005_garage.sql.
/// </summary>
public abstract partial class SqlGameStore : IGarageStore
{
    /// <summary>Deterministic instance id for (account, model, ordinal): a concurrent creation resolves via the primary key.</summary>
    public static string InstanceIdFor(string accountId, string carId, int ordinal) =>
        "ci_" + Hashing.Sha256Hex($"car-instance|{accountId}|{carId}|{ordinal}")[..24];

    static CarInstance ReadInstance(DbDataReader r) => new(r.Str(0), r.Str(1), r.Str(2), r.Int(3), r.Str(4));

    const string InstanceColumns = "instance_id, account_id, car_id, ordinal, source";

    public Task<IReadOnlyList<CarInstance>> EnsureCarInstancesAsync(string accountId, CancellationToken ct = default) =>
        WriteAsync<IReadOnlyList<CarInstance>>(async (c, tx) =>
        {
            await EnsureAccount(c, tx, accountId);
            var missing = await c.QueryAsync(tx,
                "SELECT o.car_id, o.source FROM owned_cars o WHERE o.account_id = @a AND NOT EXISTS " +
                "(SELECT 1 FROM car_instances i WHERE i.account_id = o.account_id AND i.car_id = o.car_id) ORDER BY o.car_id",
                r => (Car: r.Str(0), Source: r.Str(1)), ("@a", accountId));
            foreach ((string car, string source) in missing)
                await c.ExecAsync(tx,
                    "INSERT INTO car_instances (instance_id, account_id, car_id, ordinal, source) VALUES (@i, @a, @c, 1, @s) ON CONFLICT DO NOTHING",
                    ("@i", InstanceIdFor(accountId, car, 1)), ("@a", accountId), ("@c", car), ("@s", source));
            return await c.QueryAsync(tx, $"SELECT {InstanceColumns} FROM car_instances WHERE account_id = @a ORDER BY car_id, ordinal",
                ReadInstance, ("@a", accountId));
        }, ct);

    public Task<CarInstance?> GetCarInstanceAsync(string accountId, string instanceId, CancellationToken ct = default) =>
        ReadAsync((c, tx) => c.FirstOrDefaultAsync(tx, $"SELECT {InstanceColumns} FROM car_instances WHERE instance_id = @i AND account_id = @a",
            ReadInstance, ("@i", instanceId), ("@a", accountId)), ct);

    public Task<StoredWorkspace?> GetWorkspaceAsync(string instanceId, CancellationToken ct = default) =>
        ReadAsync((c, tx) => c.FirstOrDefaultAsync(tx,
            "SELECT instance_id, account_id, revision, workspace_json FROM car_workspaces WHERE instance_id = @i",
            r => new StoredWorkspace(r.Str(0), r.Str(1), r.Long(2), r.Str(3)), ("@i", instanceId)), ct);

    public Task<bool> CreateWorkspaceAsync(string accountId, string instanceId, WorkspaceWrite w, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) => await c.ExecAsync(tx,
            "INSERT INTO car_workspaces (instance_id, account_id, revision, schema_version, workspace_json, applied_revision, applied_build_hash, applied_pi) " +
            "VALUES (@i, @a, @r, @sv, @j, @ar, @h, @pi) ON CONFLICT DO NOTHING",
            ("@i", instanceId), ("@a", accountId), ("@r", w.Revision), ("@sv", w.SchemaVersion), ("@j", w.Json),
            ("@ar", w.AppliedRevision), ("@h", w.AppliedBuildHash), ("@pi", w.AppliedPi)) == 1, ct);

    public Task<WorkspaceSaveStatus> SaveWorkspaceAsync(string accountId, string instanceId, long expectedRevision, WorkspaceWrite w,
        CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            if (await UpdateWorkspace(c, tx, accountId, instanceId, expectedRevision, w)) return WorkspaceSaveStatus.Saved;
            bool exists = await c.FirstOrDefaultAsync(tx, "SELECT 1 FROM car_workspaces WHERE instance_id = @i AND account_id = @a",
                r => true, ("@i", instanceId), ("@a", accountId));
            return exists ? WorkspaceSaveStatus.Stale : WorkspaceSaveStatus.NotFound;
        }, ct);

    static async Task<bool> UpdateWorkspace(DbConnection c, DbTransaction tx, string accountId, string instanceId, long expectedRevision, WorkspaceWrite w) =>
        await c.ExecAsync(tx,
            "UPDATE car_workspaces SET revision = @r, schema_version = @sv, workspace_json = @j, applied_revision = @ar, applied_build_hash = @h, " +
            "applied_pi = @pi, updated_at = CURRENT_TIMESTAMP WHERE instance_id = @i AND account_id = @a AND revision = @expected",
            ("@r", w.Revision), ("@sv", w.SchemaVersion), ("@j", w.Json), ("@ar", w.AppliedRevision), ("@h", w.AppliedBuildHash),
            ("@pi", w.AppliedPi), ("@i", instanceId), ("@a", accountId), ("@expected", expectedRevision)) == 1;

    public Task<IReadOnlyCollection<string>> GetOwnedPartsAsync(string instanceId, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyCollection<string>>(async (c, tx) => await OwnedParts(c, tx, instanceId), ct);

    static Task<List<string>> OwnedParts(DbConnection c, DbTransaction tx, string instanceId) =>
        c.QueryAsync(tx, "SELECT part_id FROM car_part_ownership WHERE instance_id = @i ORDER BY part_id", r => r.Str(0), ("@i", instanceId));

    public Task SaveQuoteAsync(StoredQuote q, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) => await c.ExecAsync(tx,
            "INSERT INTO garage_quotes (quote_id, account_id, instance_id, quote_json, total, expires_at_ms) VALUES (@q, @a, @i, @j, @t, @e) " +
            "ON CONFLICT DO NOTHING",
            ("@q", q.QuoteId), ("@a", q.AccountId), ("@i", q.InstanceId), ("@j", q.QuoteJson), ("@t", q.Total), ("@e", q.ExpiresAtMs)), ct);

    public Task<StoredQuote?> GetQuoteAsync(string accountId, string quoteId, CancellationToken ct = default) =>
        ReadAsync((c, tx) => ReadQuote(c, tx, accountId, quoteId), ct);

    static Task<StoredQuote?> ReadQuote(DbConnection c, DbTransaction tx, string accountId, string quoteId) =>
        c.FirstOrDefaultAsync(tx,
            "SELECT quote_id, account_id, instance_id, quote_json, total, expires_at_ms FROM garage_quotes WHERE quote_id = @q AND account_id = @a",
            r => new StoredQuote(r.Str(0), r.Str(1), r.Str(2), r.Str(3), r.Long(4), r.Long(5)), ("@q", quoteId), ("@a", accountId));

    /// <summary>
    /// Buy-and-Apply in ONE transaction: lock the wallet (serialising this account's purchases, course buys and settlements),
    /// read the quote ledger, the workspace (row-locked) and this instance's ownership, let Core decide, then write the
    /// settlement row, ledger debit, grants, workspace (compare-and-swap) and balance together — or nothing.
    /// </summary>
    public Task<T> SettleQuoteAsync<T>(string accountId, string quoteId, Func<GarageSettleView, GarageSettleDecision<T>> decide,
        CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            await EnsureAccount(c, tx, accountId);
            long balance = await LockWallet(c, tx, accountId);
            StoredQuote? quote = await ReadQuote(c, tx, accountId, quoteId);
            string? prior = await c.FirstOrDefaultAsync(tx, "SELECT record_json FROM garage_quote_settlements WHERE quote_id = @q AND account_id = @a",
                r => r.NStr(0), ("@q", quoteId), ("@a", accountId));
            CarInstance? instance = null;
            StoredWorkspace? workspace = null;
            List<string> owned = new();
            if (quote is not null)
            {
                instance = await c.FirstOrDefaultAsync(tx, $"SELECT {InstanceColumns} FROM car_instances WHERE instance_id = @i AND account_id = @a",
                    ReadInstance, ("@i", quote.InstanceId), ("@a", accountId));
                workspace = await c.FirstOrDefaultAsync(tx,
                    "SELECT instance_id, account_id, revision, workspace_json FROM car_workspaces WHERE instance_id = @i AND account_id = @a" + ForUpdate,
                    r => new StoredWorkspace(r.Str(0), r.Str(1), r.Long(2), r.Str(3)), ("@i", quote.InstanceId), ("@a", accountId));
                owned = await OwnedParts(c, tx, quote.InstanceId);
            }

            GarageSettleDecision<T> d = decide(new GarageSettleView
            {
                AccountId = accountId, QuoteId = quoteId, Quote = quote, Instance = instance, Workspace = workspace, Balance = balance,
                OwnedParts = owned, PriorRecordJson = prior,
            });
            if (!d.Commit) return d.Result;

            // Defensive invariants (Core already decided): a real debit within the wallet, a workspace to change.
            if (quote is null || workspace is null || d.Workspace is null || d.Debit <= 0 || d.NewBalance != balance - d.Debit || d.NewBalance < 0)
                throw new InvalidOperationException("Inconsistent Buy-and-Apply decision; nothing was written.");
            string ledgerKey = $"garage-quote/{accountId}/{quoteId}";
            await c.ExecAsync(tx,
                "INSERT INTO garage_quote_settlements (quote_id, account_id, instance_id, debit, grants_json, applied_revision, build_hash, balance_after, ledger_key, record_json) " +
                "VALUES (@q, @a, @i, @d, @g, @ar, @h, @bal, @k, @rec)",
                ("@q", quoteId), ("@a", accountId), ("@i", quote.InstanceId), ("@d", d.Debit),
                ("@g", JsonSerializer.Serialize(d.Grants.Select(g => g.PartId))), ("@ar", d.AppliedRevision), ("@h", d.BuildHash),
                ("@bal", d.NewBalance), ("@k", ledgerKey), ("@rec", d.RecordJson));
            await InsertLedger(c, tx, accountId, ledgerKey, "part-purchase", null,
                $"parts:{quote.InstanceId}:{string.Join(",", d.Grants.Select(g => g.PartId))}", -d.Debit, -d.Debit, 0, d.NewBalance);
            foreach (PartGrant g in d.Grants) // no ON CONFLICT: a second grant of an owned part is a hard failure (rolled back)
                await c.ExecAsync(tx,
                    "INSERT INTO car_part_ownership (instance_id, part_id, account_id, source, quote_id, price) VALUES (@i, @p, @a, 'purchase', @q, @price)",
                    ("@i", quote.InstanceId), ("@p", g.PartId), ("@a", accountId), ("@q", quoteId), ("@price", g.Price));
            if (!await UpdateWorkspace(c, tx, accountId, quote.InstanceId, workspace.Revision, d.Workspace))
                throw new InvalidOperationException("The workspace changed inside the settlement transaction; nothing was written.");
            await SetBalance(c, tx, accountId, d.NewBalance);
            return d.Result;
        }, ct);
}
