namespace NightSignal.ControlPlane.Persistence;

public abstract partial class SqlGameStore
{
    /// <summary>Records a race-diary entry as read (idempotent); true when it was not read before.</summary>
    public Task<bool> RecordDiaryReadAsync(string accountId, string entryId, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
            await c.ExecAsync(tx, "INSERT INTO diary_reads (account_id, entry_id) VALUES (@a, @e) ON CONFLICT DO NOTHING",
                ("@a", accountId), ("@e", entryId)) > 0, ct);

    /// <summary>The race-diary entries the account has read.</summary>
    public Task<IReadOnlyList<string>> DiaryReadsAsync(string accountId, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<string>>(async (c, tx) =>
            await c.QueryAsync(tx, "SELECT entry_id FROM diary_reads WHERE account_id = @a ORDER BY entry_id", r => r.Str(0), ("@a", accountId)), ct);
}
