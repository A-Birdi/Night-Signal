using System.Text.Json;
using NightSignal.ControlPlane.Convoys;

namespace NightSignal.ControlPlane.Persistence;

/// <summary>Durable compact snapshots of Dormant convoy rooms (Addendum 02 D208), so a restart recovers them honestly.</summary>
public interface IDormantRoomStore
{
    Task SaveDormantRoomAsync(DormantRoomSnapshot room, CancellationToken ct = default);
    Task DeleteDormantRoomAsync(string sessionId, CancellationToken ct = default);
    /// <summary>Deletes expired rooms, then returns the rest.</summary>
    Task<IReadOnlyList<DormantRoomSnapshot>> LoadDormantRoomsAsync(DateTimeOffset now, CancellationToken ct = default);
}

public abstract partial class SqlGameStore : IDormantRoomStore
{
    static readonly JsonSerializerOptions RoomJson = new(JsonSerializerDefaults.Web);

    public Task SaveDormantRoomAsync(DormantRoomSnapshot room, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) => await c.ExecAsync(tx,
            "INSERT INTO dormant_rooms (session_id, convoy_id, snapshot_json, dormant_since_ms, expires_at_ms) VALUES (@s, @c, @j, @d, @e) " +
            "ON CONFLICT (session_id) DO UPDATE SET snapshot_json = excluded.snapshot_json, saved_at = CURRENT_TIMESTAMP",
            ("@s", room.SessionId), ("@c", room.ConvoyId), ("@j", JsonSerializer.Serialize(room, RoomJson)),
            ("@d", room.DormantSince.ToUnixTimeMilliseconds()), ("@e", room.ExpiresAt.ToUnixTimeMilliseconds())), ct);

    public Task DeleteDormantRoomAsync(string sessionId, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) => await c.ExecAsync(tx, "DELETE FROM dormant_rooms WHERE session_id = @s", ("@s", sessionId)), ct);

    public Task<IReadOnlyList<DormantRoomSnapshot>> LoadDormantRoomsAsync(DateTimeOffset now, CancellationToken ct = default) =>
        WriteAsync<IReadOnlyList<DormantRoomSnapshot>>(async (c, tx) =>
        {
            // Retire expired rooms (and with them their grants) under the documented 24 h rule.
            await c.ExecAsync(tx, "DELETE FROM dormant_rooms WHERE expires_at_ms <= @now", ("@now", now.ToUnixTimeMilliseconds()));
            return await c.QueryAsync(tx, "SELECT snapshot_json FROM dormant_rooms ORDER BY session_id",
                r => JsonSerializer.Deserialize<DormantRoomSnapshot>(r.Str(0), RoomJson)!);
        }, ct);
}
