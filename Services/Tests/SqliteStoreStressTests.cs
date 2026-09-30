using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>A stress test that runs only on request (set <c>NS_SQLITE_STRESS</c> to a number of rounds); skipped otherwise.</summary>
public sealed class StressFactAttribute : FactAttribute
{
    public StressFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NS_SQLITE_STRESS")))
            Skip = "stress probe — set NS_SQLITE_STRESS=<rounds> (e.g. 900) to run it";
    }
}

/// <summary>
/// The SQLite game store under concurrent writers, repeated: SocialStoreTests' eight-writer handle race (two store
/// instances, one database) many times over fresh databases. With Microsoft.Data.Sqlite 10.0.12's connection pooling it
/// failed 5–8 rounds in 900 — the pool handed one native connection to two concurrent writers ("cannot start a transaction
/// within a transaction" at BEGIN IMMEDIATE) — which made that test fail about once in a dozen full solution runs. The
/// store no longer pools (<see cref="SqliteGameStore"/>); every round must now give exactly one claim and seven refusals.
/// </summary>
public sealed class SqliteStoreStressTests
{
    static string Id(int i) => $"00000000-0000-4000-8000-{i:000000000000}";

    [StressFact]
    public async Task EightConcurrentHandleClaims_AlwaysSerialize_OverManyRounds()
    {
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("NS_SQLITE_STRESS"), out int n) && n > 0 ? n : 300;
        string[] casings = { "Robin_Birdi", "robin_birdi", "ROBIN_BIRDI", "rObIn_bIrDi" };
        var problems = new List<string>();
        for (int round = 0; round < rounds; round++)
        {
            using var dir = new TempDir();
            try
            {
                string db = dir.File("social.db");
                var store = new SqliteGameStore(db);
                await store.InitializeAsync();
                for (int i = 1; i <= 8; i++) await store.EnsureAccountAsync(Id(i));
                var second = new SqliteGameStore(db);
                HandleClaimResult[] results = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Task.Run(() =>
                    (i % 2 == 0 ? store : second).ClaimHandleAsync(Id(i), casings[i % casings.Length], Handles.Canonical(casings[i % casings.Length])!))));
                int claimed = results.Count(r => r.Status == HandleClaimStatus.Claimed), taken = results.Count(r => r.Status == HandleClaimStatus.Taken);
                if (claimed != 1 || taken != 7) problems.Add($"round {round}: {claimed} claimed, {taken} taken");
            }
            catch (Exception e)
            {
                problems.Add($"round {round}: {e.GetType().Name}: {e.Message}");
            }
        }
        Assert.True(problems.Count == 0, $"{problems.Count} of {rounds} rounds went wrong:\n" + string.Join("\n", problems.Take(20)));
    }
}
