namespace NightSignal.ControlPlane.Persistence;

/// <summary>A kept ghost: its ruleset key, result and the ghost@1 document.</summary>
public sealed record StoredGhost(string RulesKey, long ResultMicros, string MatchId, string Json);

public abstract partial class SqlGameStore
{
    /// <summary>Holds the ghost the game server recorded for an entrant until the match settles (a resend replaces it).</summary>
    public Task StoreMatchGhostAsync(string matchId, string accountId, string json, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            await c.ExecAsync(tx, "INSERT INTO match_ghosts (match_id, account_id, ghost_json) VALUES (@m, @a, @j) " +
                "ON CONFLICT (match_id, account_id) DO UPDATE SET ghost_json = excluded.ghost_json", ("@m", matchId), ("@a", accountId), ("@j", json));
            return true;
        }, ct);

    public Task<string?> MatchGhostAsync(string matchId, string accountId, CancellationToken ct = default) =>
        ReadAsync(async (c, tx) => await c.FirstOrDefaultAsync(tx, "SELECT ghost_json FROM match_ghosts WHERE match_id = @m AND account_id = @a",
            r => r.Str(0), ("@m", matchId), ("@a", accountId)), ct);

    /// <summary>The account's kept ghosts for a course and format (one per ruleset).</summary>
    public Task<IReadOnlyList<StoredGhost>> GhostsAsync(string accountId, string courseId, string format, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<StoredGhost>>(async (c, tx) => await c.QueryAsync(tx,
            "SELECT rules_key, result_micros, match_id, ghost_json FROM ghosts WHERE account_id = @a AND course_id = @c AND format = @f ORDER BY result_micros",
            r => new StoredGhost(r.Str(0), r.Long(1), r.Str(2), r.Str(3)), ("@a", accountId), ("@c", courseId), ("@f", format)), ct);

    /// <summary>Keeps a ghost as the account's best for its course, format and ruleset when it is faster (or the first); true when kept.</summary>
    public Task<bool> OfferGhostAsync(string accountId, string courseId, string format, string rulesKey, long resultMicros, string matchId, string json,
        CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            long? best = await c.FirstOrDefaultAsync<long?>(tx,
                "SELECT result_micros FROM ghosts WHERE account_id = @a AND course_id = @c AND format = @f AND rules_key = @k",
                r => r.Long(0), ("@a", accountId), ("@c", courseId), ("@f", format), ("@k", rulesKey));
            if (best is { } b && b <= resultMicros) return false;
            await c.ExecAsync(tx, "INSERT INTO ghosts (account_id, course_id, format, rules_key, result_micros, match_id, ghost_json) " +
                "VALUES (@a, @c, @f, @k, @r, @m, @j) ON CONFLICT (account_id, course_id, format, rules_key) DO UPDATE SET " +
                "result_micros = excluded.result_micros, match_id = excluded.match_id, ghost_json = excluded.ghost_json, updated_at = CURRENT_TIMESTAMP",
                ("@a", accountId), ("@c", courseId), ("@f", format), ("@k", rulesKey), ("@r", resultMicros), ("@m", matchId), ("@j", json));
            return true;
        }, ct);
}
