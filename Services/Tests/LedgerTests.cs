using Microsoft.Data.Sqlite;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>SQLite ledger: idempotent settlement, wallet cap clamping, exactly-once purchases, DB-level guards.</summary>
public sealed class LedgerTests : IAsyncLifetime
{
    const string A = "00000000-0000-4000-8000-0000000000aa";
    const string B = "00000000-0000-4000-8000-0000000000bb";
    readonly TempDir dir = new();
    SqliteGameStore store = null!;
    string DbPath => dir.File("ledger.db");

    public async Task InitializeAsync()
    {
        store = new SqliteGameStore(DbPath);
        await store.InitializeAsync();
        await store.EnsureAccountAsync(A);
        await store.EnsureAccountAsync(B);
    }

    public Task DisposeAsync()
    {
        dir.Dispose();
        return Task.CompletedTask;
    }

    long Scalar(string sql, params (string, object)[] args)
    {
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string n, object v) in args) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    void Exec(string sql) => Scalar(sql + "; SELECT 0");

    static PayoutFacts Facts(int placement = 1) => new()
    {
        AuthoredExpectedSeconds = 180, Kind = EventKind.CampaignStage, Mode = CampaignMode.Normal,
        Outcome = RunOutcome.Finished, Placement = placement, Clean = true,
    };

    async Task<string> AllocateAsync(string matchId)
    {
        await store.RecordAllocationAsync(new MatchRecord { MatchId = matchId, ConvoyId = "cv_test", ServerId = "srv", ConfigJson = "{}", ResultsSecret = "x" });
        return matchId;
    }

    static MatchSettlement Settlement(string matchId, string bodyHash, bool clear, bool challenge, string account = A) => new()
    {
        MatchId = matchId,
        ResultsSha256 = bodyHash,
        Entrants = new[]
        {
            new EntrantSettlement
            {
                AccountId = account,
                Facts = Facts(),
                Clear = clear ? new StageClearCandidate(CampaignMode.Normal, 1, StageType.Regular) : null,
                Challenges = challenge ? new[] { new ChallengeGrant("CH01", ChallengeTier.Bronze, 3_000, 40, "COS-CH01") } : Array.Empty<ChallengeGrant>(),
                Receipt = new Receipt { MatchId = matchId, AccountId = account },
            },
        },
    };

    [Fact]
    public async Task Settlement_Retried100Times_HasExactlyOneEconomicEffect()
    {
        string m = await AllocateAsync("m_retry");
        MatchSettlement s = Settlement(m, "hash-1", clear: true, challenge: true);

        var outcomes = new List<SettlementOutcome>();
        for (int i = 0; i < 50; i++) outcomes.Add(await store.SettleAsync(s));
        outcomes.AddRange(await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => store.SettleAsync(s)))));

        Assert.Equal(100, outcomes.Count);
        Assert.Single(outcomes, o => o.Status == SettlementStatus.Settled);
        Assert.Equal(99, outcomes.Count(o => o.Status == SettlementStatus.AlreadySettled));

        PayoutBreakdown expected = Economy.Compute(new PayoutFacts
        {
            AuthoredExpectedSeconds = 180, Kind = EventKind.CampaignStage, Mode = CampaignMode.Normal, Outcome = RunOutcome.Finished,
            Placement = 1, Clean = true, FirstClearBonus = Economy.FirstClearBonus(StageType.Regular, CampaignMode.Normal),
            NewlyCompletedChallengeCash = 3_000,
        });
        Assert.Equal(expected.Total, Scalar("SELECT balance FROM wallets WHERE account_id = @a", ("@a", A)));
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM ledger_entries WHERE match_id = @m", ("@m", m)));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM stage_clears"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM challenge_unlocks"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM cosmetics_owned"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM match_results"));

        Receipt receipt = outcomes.First(o => o.Status == SettlementStatus.Settled).Receipts.Single();
        Assert.True(receipt.FirstClearAwarded);
        Assert.Equal(expected.EventCredits, receipt.Payout.EventCredits);
        Assert.Equal(RankPoints.NormalFirstClear + RankPoints.BronzeChallenge, receipt.RankPointsAfter);
        // Every replay returns the same stored receipt.
        Assert.All(outcomes, o => Assert.Equal(expected.Total, o.Receipts.Single().Payout.Total));
    }

    [Fact]
    public async Task Settlement_WithDifferentBody_AfterSettling_IsAConflict()
    {
        string m = await AllocateAsync("m_conflict");
        Assert.Equal(SettlementStatus.Settled, (await store.SettleAsync(Settlement(m, "hash-1", false, false))).Status);
        Assert.Equal(SettlementStatus.Conflict, (await store.SettleAsync(Settlement(m, "hash-2", false, false))).Status);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM ledger_entries"));
    }

    [Fact]
    public async Task SecondMatch_OnAClearedStage_PaysRaceMoneyButNoFirstClear()
    {
        await store.SettleAsync(Settlement(await AllocateAsync("m_first"), "h1", clear: true, challenge: true));
        SettlementOutcome second = await store.SettleAsync(Settlement(await AllocateAsync("m_second"), "h2", clear: true, challenge: true));
        Receipt r = second.Receipts.Single();
        Assert.False(r.FirstClearAwarded);
        Assert.Empty(r.ChallengesUnlocked);
        Assert.Equal(0, r.Payout.FirstClearBonus);
        Assert.Equal(0, r.Payout.ChallengeCash);
        Assert.True(r.Payout.EventCredits > 0);
        Assert.Equal(r.RankPointsBefore, r.RankPointsAfter);
    }

    [Fact]
    public async Task ClearBeyondTheFrontier_IsRefusedByCoreRules()
    {
        MatchSettlement s = Settlement(await AllocateAsync("m_gap"), "h", clear: false, challenge: false);
        var entrant = s.Entrants[0];
        var skipping = new MatchSettlement
        {
            MatchId = s.MatchId, ResultsSha256 = "h",
            Entrants = new[] { new EntrantSettlement { AccountId = A, Facts = entrant.Facts, Receipt = entrant.Receipt,
                Clear = new StageClearCandidate(CampaignMode.Normal, 5, StageType.Regular) } },
        };
        Receipt r = (await store.SettleAsync(skipping)).Receipts.Single();
        Assert.False(r.FirstClearAwarded);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM stage_clears"));
    }

    [Fact]
    public async Task WalletAt9999998_ReceivingAReward_ClampsTo9999999_AndRecordsTheClampedAmount()
    {
        Exec($"UPDATE wallets SET balance = 9999998 WHERE account_id = '{A}'");
        Receipt r = (await store.SettleAsync(Settlement(await AllocateAsync("m_cap"), "h", false, false))).Receipts.Single();

        long reward = Economy.Compute(Facts()).EventCredits;
        Assert.Equal(Limits.WalletCap, Scalar("SELECT balance FROM wallets WHERE account_id = @a", ("@a", A)));
        Assert.Equal(reward, Scalar("SELECT requested_amount FROM ledger_entries WHERE match_id = 'm_cap'"));
        Assert.Equal(1, Scalar("SELECT applied_amount FROM ledger_entries WHERE match_id = 'm_cap'"));
        Assert.Equal(reward - 1, Scalar("SELECT clamped_amount FROM ledger_entries WHERE match_id = 'm_cap'"));
        Assert.Equal(reward - 1, r.ClampedAwayTotal);
        Assert.Equal(new CreditLine("event", reward, 1, reward - 1), r.Credits.Single());
        Assert.Contains(r.Notes, n => n.Contains("cap"));
    }

    [Fact]
    public async Task ConcurrentDuplicatePurchase_DebitsExactlyOnce_EvenAcrossTwoStoreInstances()
    {
        Exec($"UPDATE wallets SET balance = 100000 WHERE account_id = '{A}'");
        CarDef car = TestData.Content.Catalogue.Car("V04");
        var second = new SqliteGameStore(DbPath); // a second "service instance" with its own connection pool
        var request = new PurchaseRequest(A, "buy-v04-0001", PriceRules.Car, car.Id, car.Price);

        PurchaseResult[] results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => Task.Run(() => (i % 2 == 0 ? store : second).PurchaseAsync(request))));

        Assert.Single(results, r => r.Status == WriteStatus.Ok);
        Assert.Equal(23, results.Count(r => r.Status == WriteStatus.Replayed));
        Assert.Equal(100_000 - car.Price, Scalar("SELECT balance FROM wallets WHERE account_id = @a", ("@a", A)));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM ledger_entries WHERE reward_type = 'purchase'"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM owned_cars WHERE car_id = 'V04'"));
    }

    [Fact]
    public async Task ConcurrentPurchases_WithDifferentKeys_ForTheSameCar_OwnItOnce()
    {
        Exec($"UPDATE wallets SET balance = 200000 WHERE account_id = '{A}'");
        CarDef car = TestData.Content.Catalogue.Car("V04");
        PurchaseResult[] results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => store.PurchaseAsync(new PurchaseRequest(A, $"key-{i:0000000}", PriceRules.Car, car.Id, car.Price)))));
        Assert.Single(results, r => r.Status == WriteStatus.Ok);
        Assert.Equal(9, results.Count(r => r.Status == WriteStatus.AlreadyOwned));
        Assert.Equal(200_000 - car.Price, Scalar("SELECT balance FROM wallets WHERE account_id = @a", ("@a", A)));
    }

    [Fact]
    public async Task Purchase_WithInsufficientFunds_IsRejected_AndChangesNothing()
    {
        Exec($"UPDATE wallets SET balance = 1000 WHERE account_id = '{A}'");
        PurchaseResult r = await store.PurchaseAsync(new PurchaseRequest(A, "poor-key-01", PriceRules.Car, "V04", 42_000));
        Assert.Equal(WriteStatus.InsufficientFunds, r.Status);
        Assert.Equal(1000, Scalar("SELECT balance FROM wallets WHERE account_id = @a", ("@a", A)));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM ledger_entries"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM owned_cars"));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(350_001L)]
    [InlineData(long.MinValue)]
    public async Task Purchase_WithInvalidPrice_IsRejected(long price)
    {
        Exec($"UPDATE wallets SET balance = 9999999 WHERE account_id = '{A}'");
        Assert.Equal(WriteStatus.Invalid, (await store.PurchaseAsync(new PurchaseRequest(A, "bad-price-1", PriceRules.Car, "V04", price))).Status);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM ledger_entries"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-5.0)]
    [InlineData(1.5)]
    [InlineData(1e300)]
    [InlineData(200_001.0)]
    public void PriceRules_RejectNonFiniteNegativeFractionalAndOversizedPrices(double price) =>
        Assert.False(PriceRules.IsValid(PriceRules.Part, price));

    [Fact]
    public void PriceRules_AcceptCatalogueCarPrices() =>
        Assert.All(TestData.Content.Catalogue.Cars, c => Assert.True(PriceRules.IsValid(PriceRules.Car, c.Price)));

    [Fact]
    public async Task Starter_IsGrantedOnce()
    {
        StarterResult first = await store.ClaimStarterAsync(A, "V01", Limits.StarterGrantCredits);
        StarterResult retry = await store.ClaimStarterAsync(A, "V01", Limits.StarterGrantCredits);
        StarterResult other = await store.ClaimStarterAsync(A, "V02", Limits.StarterGrantCredits);
        Assert.Equal(WriteStatus.Ok, first.Status);
        Assert.Equal(WriteStatus.Replayed, retry.Status);
        Assert.Equal(WriteStatus.Conflict, other.Status);
        Assert.Equal(Limits.StarterGrantCredits, Scalar("SELECT balance FROM wallets WHERE account_id = @a", ("@a", A)));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM owned_cars WHERE account_id = @a", ("@a", A)));
    }

    [Fact]
    public async Task Card_UsesVersionedWrites()
    {
        Assert.Equal(WriteStatus.Ok, (await store.UpsertCardAsync(A, "Kestrel", 0)).Status);
        Assert.Equal(2, (await store.UpsertCardAsync(A, "Kestrel Two", 1)).Card!.Revision);
        Assert.Equal(WriteStatus.Conflict, (await store.UpsertCardAsync(A, "Stale", 1)).Status);
    }

    [Fact]
    public void Database_RejectsLedgerUpdatesAndOutOfRangeBalances()
    {
        Exec($"INSERT INTO ledger_entries (account_id, idempotency_key, reward_type, requested_amount, applied_amount, balance_after) VALUES ('{A}', 'k', 't', 1, 1, 1)");
        Assert.Throws<SqliteException>(() => Exec("UPDATE ledger_entries SET applied_amount = 999"));
        Assert.Throws<SqliteException>(() => Exec("DELETE FROM ledger_entries"));
        Assert.Throws<SqliteException>(() => Exec($"INSERT INTO ledger_entries (account_id, idempotency_key, reward_type, requested_amount, applied_amount, balance_after) VALUES ('{A}', 'k', 't', 1, 1, 1)"));
        Assert.Throws<SqliteException>(() => Exec($"UPDATE wallets SET balance = 10000000 WHERE account_id = '{A}'"));
        Assert.Throws<SqliteException>(() => Exec($"UPDATE wallets SET balance = -1 WHERE account_id = '{A}'"));
    }
}
