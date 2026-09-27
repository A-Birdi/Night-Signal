using NightSignal.ControlPlane.Toys;

namespace NightSignal.ControlPlane.Persistence;

/// <summary>
/// Durable 'While We Wait' toy snapshots keyed by the stable convoy session id (Addendum 02 §1.5, §11). Non-progression
/// data only (D209): nothing here is read by the wallet, RP, records or unlock paths.
/// </summary>
public interface IToySnapshotStore
{
    /// <summary>Upsert; an older revision never overwrites a newer stored one.</summary>
    Task SaveToySnapshotAsync(ToySnapshotRecord record, CancellationToken ct = default);
    Task DeleteToySnapshotAsync(string sessionId, CancellationToken ct = default);
    /// <summary>Deletes snapshots whose Dormant expiry has passed (the 24 h D208 grace), then returns the rest.</summary>
    Task<IReadOnlyList<ToySnapshotRecord>> LoadToySnapshotsAsync(DateTimeOffset now, CancellationToken ct = default);
}

public abstract partial class SqlGameStore : IToySnapshotStore
{
    public Task SaveToySnapshotAsync(ToySnapshotRecord record, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) => await c.ExecAsync(tx,
            "INSERT INTO toy_snapshots (session_id, revision, snapshot_json, dormant_expires_ms) VALUES (@s, @r, @j, @e) " +
            "ON CONFLICT (session_id) DO UPDATE SET revision = excluded.revision, snapshot_json = excluded.snapshot_json, " +
            "dormant_expires_ms = excluded.dormant_expires_ms, saved_at = CURRENT_TIMESTAMP WHERE excluded.revision >= toy_snapshots.revision",
            ("@s", record.SessionId), ("@r", record.Revision), ("@j", record.Json),
            ("@e", record.DormantExpiresAt?.ToUnixTimeMilliseconds())), ct);

    public Task DeleteToySnapshotAsync(string sessionId, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) => await c.ExecAsync(tx, "DELETE FROM toy_snapshots WHERE session_id = @s", ("@s", sessionId)), ct);

    public Task<IReadOnlyList<ToySnapshotRecord>> LoadToySnapshotsAsync(DateTimeOffset now, CancellationToken ct = default) =>
        WriteAsync<IReadOnlyList<ToySnapshotRecord>>(async (c, tx) =>
        {
            // Retire dormant rooms' toys under the documented 24 h rule (no artwork is kept forever).
            await c.ExecAsync(tx, "DELETE FROM toy_snapshots WHERE dormant_expires_ms IS NOT NULL AND dormant_expires_ms <= @now",
                ("@now", now.ToUnixTimeMilliseconds()));
            return await c.QueryAsync(tx, "SELECT session_id, revision, snapshot_json, dormant_expires_ms FROM toy_snapshots ORDER BY session_id",
                r => new ToySnapshotRecord(r.Str(0), r.Long(1), r.Str(2), r.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.Long(3))));
        }, ct);
}
