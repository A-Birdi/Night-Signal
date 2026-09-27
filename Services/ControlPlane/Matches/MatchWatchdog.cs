using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Persistence;

namespace NightSignal.ControlPlane.Matches;

/// <summary>
/// System-failure handling (spec §4.4): a match whose game server stopped heartbeating, or that has no accepted
/// results after GameServers:MaxMatchMinutes, is marked Aborted — no winners invented, no rank or progression, no
/// fees — and its convoy returns to event selection so the leader can retry. Results already accepted stay settled.
/// </summary>
public sealed class MatchWatchdog(ConvoyDirectory convoys, GameServerRegistry registry, IResultLedger ledger, TimeProvider clock,
    IOptions<GameServerOptions> options, ILogger<MatchWatchdog> log) : BackgroundService
{
    public async Task SweepAsync(CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        foreach ((string convoyId, ActiveMatch match, DateTimeOffset startedAt) in convoys.ActiveMatches())
        {
            bool serverLost = !registry.IsHealthy(match.ServerId);
            bool overdue = now - startedAt > TimeSpan.FromMinutes(options.Value.MaxMatchMinutes);
            if (!serverLost && !overdue) continue;

            if (await ledger.MarkAbortedAsync(match.MatchId, ct))
            {
                registry.MatchFinished(match.ServerId);
                string reason = serverLost
                    ? "The race server stopped responding. The event was aborted: no results, rank or progression; retry any time."
                    : "The event produced no results in time and was aborted: no results, rank or progression; retry any time.";
                log.LogWarning("Match {MatchId} aborted ({Cause})", match.MatchId, serverLost ? "server lost" : "overdue");
                convoys.MatchAborted(convoyId, match.MatchId, reason);
            }
            else if ((await ledger.GetMatchAsync(match.MatchId, ct))?.State == "settled")
                convoys.MatchEnded(convoyId, match.MatchId, null); // settled elsewhere; just release the convoy
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Match watchdog sweep failed"); }
        }
    }
}
