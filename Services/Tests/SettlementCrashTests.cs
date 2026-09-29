using Microsoft.Data.Sqlite;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Gate 4 "crashes during transactions": a settlement that fails part-way — here after the money, the challenge and its
/// cosmetic were written, when the receipt row is refused by a trigger standing in for a crash — leaves nothing behind
/// (wallet, ledger, unlocks, cosmetics, receipts and the match state all as before); the retry then settles exactly once,
/// and a replay is recognised without paying twice. (SQLite; the Postgres dialect shares the same single-transaction code.)
/// </summary>
public sealed class SettlementCrashTests : IAsyncLifetime
{
    const string A = "00000000-0000-4000-8000-0000000000c1";
    readonly TempDir dir = new();
    SqliteGameStore store = null!;
    string DbPath => dir.File("crash.db");

    public async Task InitializeAsync()
    {
        store = new SqliteGameStore(DbPath);
        await store.InitializeAsync();
        await store.EnsureAccountAsync(A);
    }

    public Task DisposeAsync()
    {
        dir.Dispose();
        return Task.CompletedTask;
    }

    long Scalar(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        object? v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    void Exec(string sql) => Scalar(sql + "; SELECT 0");

    (long Balance, long Ledger, long Unlocks, long Cosmetics, long Receipts) State() => (
        Scalar($"SELECT balance FROM wallets WHERE account_id = '{A}'"),
        Scalar($"SELECT COUNT(*) FROM ledger_entries WHERE account_id = '{A}'"),
        Scalar($"SELECT COUNT(*) FROM challenge_unlocks WHERE account_id = '{A}'"),
        Scalar($"SELECT COUNT(*) FROM cosmetics_owned WHERE account_id = '{A}'"),
        Scalar($"SELECT COUNT(*) FROM match_results WHERE account_id = '{A}'"));

    static MatchSettlement Settlement(string matchId) => new()
    {
        MatchId = matchId, ResultsSha256 = "h-" + matchId,
        Entrants = new[]
        {
            new EntrantSettlement
            {
                AccountId = A, Receipt = new Receipt { MatchId = matchId, AccountId = A, EventKind = "FreeplaySprint", CourseId = "C01", Outcome = "Finished", Placement = 1 },
                Facts = new PayoutFacts { AuthoredExpectedSeconds = 180, Kind = EventKind.FreeplaySprint, Outcome = RunOutcome.Finished, Placement = 1, Clean = true },
                Challenges = new List<ChallengeGrant> { new("CH01", ChallengeTier.Bronze, RankPoints.ChallengeCash(ChallengeTier.Bronze), RankPoints.ForChallenge(ChallengeTier.Bronze), "COS-CH01") },
            },
        },
    };

    [Fact]
    public async Task ACrashMidSettlement_LeavesNothingPartial_AndTheRetrySettlesOnce()
    {
        await store.RecordAllocationAsync(new MatchRecord { MatchId = "m_crash", ConvoyId = "cv", ServerId = "srv", ConfigJson = "{}", ResultsSecret = "x" });
        var before = State();
        Exec("CREATE TRIGGER crash_mid_settlement BEFORE INSERT ON match_results WHEN NEW.match_id = 'm_crash' " +
             "BEGIN SELECT RAISE(ABORT, 'simulated crash after the money was written'); END");

        await Assert.ThrowsAnyAsync<Exception>(() => store.SettleAsync(Settlement("m_crash")));
        Assert.Equal(before, State());
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM matches WHERE match_id = 'm_crash' AND state = 'settled'"));

        Exec("DROP TRIGGER crash_mid_settlement");
        SettlementOutcome settled = await store.SettleAsync(Settlement("m_crash"));
        Assert.Equal(SettlementStatus.Settled, settled.Status);
        var after = State();
        Assert.True(after.Balance > before.Balance, "the race money and the challenge cash were paid");
        Assert.Equal(1, after.Unlocks);
        Assert.Equal(1, after.Cosmetics);
        Assert.Equal(1, after.Receipts);

        SettlementOutcome replay = await store.SettleAsync(Settlement("m_crash"));
        Assert.Equal(SettlementStatus.AlreadySettled, replay.Status);
        Assert.Equal(after, State());
    }
}
