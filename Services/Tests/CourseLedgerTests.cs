using Microsoft.Data.Sqlite;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// ONLINE course-access ledger (Addendum 01 §5.1, §16 "Course rights"): starter access, every dual-unlock mapping, C25
/// reward-only, the three currency-only courses, failed payment, double submit, purchase/unlock races (never a double
/// charge, never a refund or duplicate), plus soundtrack grants and Team Trial team bests exactly once.
/// </summary>
public sealed class CourseLedgerTests : IAsyncLifetime
{
    const string A = "00000000-0000-4000-8000-0000000000aa";
    readonly TempDir dir = new();
    SqliteGameStore store = null!;
    string DbPath => dir.File("courses.db");
    static ContentCatalogue Catalogue => TestData.Content.Catalogue;

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
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    void Exec(string sql) => Scalar(sql + "; SELECT 0");
    long Balance() => Scalar($"SELECT balance FROM wallets WHERE account_id = '{A}'");
    void SetBalance(long b) => Exec($"UPDATE wallets SET balance = {b} WHERE account_id = '{A}'");
    void ClearStages(int through) { for (int s = 1; s <= through; s++) Exec($"INSERT INTO stage_clears (account_id, mode, stage, match_id) VALUES ('{A}', 'normal', {s}, 'legacy')"); }

    async Task<SettlementOutcome> SettleClear(string matchId, int stage, IReadOnlyList<string> courses, IReadOnlyList<MusicGrant>? music = null)
    {
        await store.RecordAllocationAsync(new MatchRecord { MatchId = matchId, ConvoyId = "cv", ServerId = "srv", ConfigJson = "{}", ResultsSecret = "x" });
        return await store.SettleAsync(new MatchSettlement
        {
            MatchId = matchId, ResultsSha256 = "h-" + matchId,
            Entrants = new[]
            {
                new EntrantSettlement
                {
                    AccountId = A, Receipt = new Receipt { MatchId = matchId, AccountId = A },
                    Facts = new PayoutFacts { AuthoredExpectedSeconds = 230, Kind = EventKind.CampaignStage, Mode = CampaignMode.Normal, Outcome = RunOutcome.Finished, Placement = 2 },
                    Clear = new StageClearCandidate(CampaignMode.Normal, stage, StageType.Regular), ClearCourseGrants = courses,
                    ClearMusicGrants = music ?? Array.Empty<MusicGrant>(),
                },
            },
        });
    }

    static long RacePay() => Economy.Compute(new PayoutFacts
    {
        AuthoredExpectedSeconds = 230, Kind = EventKind.CampaignStage, Mode = CampaignMode.Normal, Outcome = RunOutcome.Finished, Placement = 2,
        FirstClearBonus = Economy.FirstClearBonus(StageType.Regular, CampaignMode.Normal),
    }).Total;

    [Fact]
    public void DualUnlockMapping_IsDerivedFromRegularStages_AndPinned()
    {
        // docs/EFFECTIVE_RULES.md: C05 S05 · C06 S06 · C07 S08 … C24 S27; reused encounter stages never unlock a course.
        var expected = new Dictionary<string, string>
        {
            ["C05"] = "S05", ["C06"] = "S06", ["C07"] = "S08", ["C08"] = "S09", ["C09"] = "S10", ["C10"] = "S11", ["C11"] = "S12",
            ["C12"] = "S13", ["C13"] = "S15", ["C14"] = "S16", ["C15"] = "S17", ["C16"] = "S18", ["C17"] = "S19", ["C18"] = "S20",
            ["C19"] = "S22", ["C20"] = "S23", ["C21"] = "S24", ["C22"] = "S25", ["C23"] = "S26", ["C24"] = "S27",
        };
        Assert.Equal(expected.OrderBy(k => k.Key), CourseAccess.RegularStageUnlocks(Catalogue).OrderBy(k => k.Key));
        foreach (string reused in new[] { "S07", "S14", "S21", "S28", "S29" })
            Assert.Empty(CourseAccess.GrantedByNormalClear(Catalogue, reused));
        Assert.Equal(new[] { "C25" }, CourseAccess.GrantedByNormalClear(Catalogue, "S30"));
        foreach ((string course, string stage) in expected)
            Assert.Equal(new[] { course }, CourseAccess.GrantedByNormalClear(Catalogue, stage));
    }

    [Fact]
    public async Task StarterCourses_AreOwnedFromTheStart_AndNeverCharged()
    {
        SetBalance(500_000);
        foreach (string starter in new[] { "T00", "C01", "C02", "C03", "C04" })
            Assert.Equal(CoursePurchaseStatus.AlreadyOwned, (await store.PurchaseCourseAsync(A, $"starter-{starter}", starter, Catalogue)).Status);
        Assert.Equal(500_000, Balance());
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM ledger_entries"));
    }

    [Fact]
    public async Task C25_IsRewardOnly_AndGrantedByTheFirstNormalS30Clear()
    {
        SetBalance(500_000);
        Assert.Equal(CoursePurchaseStatus.NotPurchasable, (await store.PurchaseCourseAsync(A, "buy-c25-0001", "C25", Catalogue)).Status);
        ClearStages(29);
        await SettleClear("m_s30", 30, CourseAccess.GrantedByNormalClear(Catalogue, "S30"));
        Assert.Contains("C25", (await store.GetOwnedCoursesAsync(new[] { A }, Catalogue))[A]);
    }

    [Theory]
    [InlineData("FP01", 45_000)]
    [InlineData("FP02", 54_000)]
    [InlineData("FP03", 63_000)]
    [InlineData("C20", 45_000)]
    public async Task PurchasableCourses_ChargeTheCataloguePrice_Once(string course, long price)
    {
        SetBalance(100_000);
        CoursePurchaseResult r = await store.PurchaseCourseAsync(A, $"buy-{course}-001", course, Catalogue);
        Assert.Equal(CoursePurchaseStatus.Purchased, r.Status);
        Assert.Equal((price, price, 100_000 - price), (r.Price, r.Charged, r.Balance));
        Assert.Equal(CoursePurchaseStatus.Replayed, (await store.PurchaseCourseAsync(A, $"buy-{course}-001", course, Catalogue)).Status);
        Assert.Equal(CoursePurchaseStatus.AlreadyOwned, (await store.PurchaseCourseAsync(A, $"buy-{course}-002", course, Catalogue)).Status);
        Assert.Equal(100_000 - price, Balance());
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM ledger_entries WHERE reward_type = 'course-purchase'"));
        Assert.Equal("purchase", (await store.GetSnapshotAsync(A)).StoredCourses.Single().Source);
    }

    [Fact]
    public async Task FailedPayment_ChangesNothing()
    {
        SetBalance(44_999);
        CoursePurchaseResult r = await store.PurchaseCourseAsync(A, "poor-c20-001", "C20", Catalogue);
        Assert.Equal(CoursePurchaseStatus.InsufficientFunds, r.Status);
        Assert.Equal(45_000, r.Price);
        Assert.Equal(44_999, Balance());
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM course_entitlements"));
    }

    [Fact]
    public async Task DoubleSubmit_AndConcurrentRequests_DebitExactlyOnce_EvenAcrossTwoStoreInstances()
    {
        SetBalance(100_000);
        var second = new SqliteGameStore(DbPath);
        CoursePurchaseResult[] same = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => (i % 2 == 0 ? store : second).PurchaseCourseAsync(A, "same-key-0001", "FP01", Catalogue))));
        Assert.Single(same, r => r.Status == CoursePurchaseStatus.Purchased);
        Assert.Equal(15, same.Count(r => r.Status == CoursePurchaseStatus.Replayed));

        CoursePurchaseResult[] distinct = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => store.PurchaseCourseAsync(A, $"other-key-{i:000}", "FP01", Catalogue))));
        Assert.All(distinct, r => Assert.Equal(CoursePurchaseStatus.AlreadyOwned, r.Status));
        Assert.Equal(55_000, Balance());
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM course_entitlements"));
        Assert.Equal(CoursePurchaseStatus.Conflict, (await store.PurchaseCourseAsync(A, "same-key-0001", "FP02", Catalogue)).Status);
    }

    [Fact]
    public async Task FreeUnlockFirst_ThenPurchase_IsAlreadyOwned_WithNoDebit()
    {
        ClearStages(4);
        SettlementOutcome settled = await SettleClear("m_s05", 5, new[] { "C05" });
        Assert.Equal(new[] { "C05" }, settled.Receipts.Single().CoursesUnlocked);
        long afterRace = Balance();
        CoursePurchaseResult r = await store.PurchaseCourseAsync(A, "late-buy-c05", "C05", Catalogue);
        Assert.Equal(CoursePurchaseStatus.AlreadyOwned, r.Status);
        Assert.Equal(afterRace, Balance());
        Assert.Equal("campaign-clear", (await store.GetSnapshotAsync(A)).StoredCourses.Single().Source);
    }

    [Fact]
    public async Task PurchaseFirst_ThenClear_KeepsThePurchase_NoRefundAndNoDuplicate()
    {
        SetBalance(50_000);
        Assert.Equal(CoursePurchaseStatus.Purchased, (await store.PurchaseCourseAsync(A, "early-buy-c05", "C05", Catalogue)).Status);
        ClearStages(4);
        Receipt receipt = (await SettleClear("m_s05", 5, new[] { "C05" })).Receipts.Single();
        Assert.Empty(receipt.CoursesUnlocked);
        Assert.Contains(receipt.Notes, n => n.Contains("no refund"));
        Assert.Equal(5_000 + RacePay(), Balance()); // race money only: no refund, no duplicate reward
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM course_entitlements"));
        Assert.Equal("purchase", (await store.GetSnapshotAsync(A)).StoredCourses.Single().Source);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM ledger_entries WHERE applied_amount > 0 AND reward_type = 'course-purchase'"));
    }

    [Fact]
    public async Task SimultaneousPurchaseAndUnlock_ChargeAtMostOnce_WhicheverCommitsFirst()
    {
        for (int round = 0; round < 6; round++)
        {
            Exec("DELETE FROM course_entitlements; DELETE FROM stage_clears; DELETE FROM match_results; DELETE FROM matches");
            SetBalance(60_000);
            ClearStages(4);
            Task<CoursePurchaseResult> buy = Task.Run(() => store.PurchaseCourseAsync(A, $"race-buy-{round:000}", "C05", Catalogue));
            Task<SettlementOutcome> clear = Task.Run(() => SettleClear($"m_race_{round}", 5, new[] { "C05" }));
            await Task.WhenAll(buy, clear);
            CoursePurchaseResult bought = await buy;
            long pay = (await clear).Receipts.Single().Payout.Total;
            long expected = bought.Status == CoursePurchaseStatus.Purchased ? 60_000 - 45_000 + pay : 60_000 + pay;
            Assert.Contains(bought.Status, new[] { CoursePurchaseStatus.Purchased, CoursePurchaseStatus.AlreadyOwned });
            Assert.Equal(expected, Balance());
            Assert.Equal(1, Scalar("SELECT COUNT(*) FROM course_entitlements WHERE course_id = 'C05'"));
        }
    }

    [Fact]
    public async Task ClearsRecordedBeforeTheCourseLedger_StillGrantOwnership()
    {
        ClearStages(9); // legacy clears S01–S09: C05, C06, C07 (S08), C08 (S09)
        IReadOnlyCollection<string> owned = (await store.GetOwnedCoursesAsync(new[] { A }, Catalogue))[A];
        Assert.Equal(new[] { "C05", "C06", "C07", "C08" }, owned.OrderBy(x => x));
        SetBalance(100_000);
        Assert.Equal(CoursePurchaseStatus.AlreadyOwned, (await store.PurchaseCourseAsync(A, "legacy-c07-01", "C07", Catalogue)).Status);
        Assert.Equal(100_000, Balance());
    }

    [Fact]
    public async Task SoundtrackCues_AreGrantedOncePerAccountAndCue()
    {
        Assert.True(await store.GrantMusicCueAsync(A, "region-kiyose", "stage-first-normal-clear", "S05", "m_a"));
        Assert.False(await store.GrantMusicCueAsync(A, "region-kiyose", "stage-first-normal-clear", "S05", "m_b"));
        ClearStages(4);
        var cue = new MusicGrant("region-two", "stage-first-normal-clear", "S05");
        Assert.Equal(new[] { "region-two" }, (await SettleClear("m_1", 5, new[] { "C05" }, new[] { cue })).Receipts.Single().MusicUnlocked);
        Assert.Empty((await SettleClear("m_2", 5, new[] { "C05" }, new[] { cue })).Receipts.Single().MusicUnlocked);
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM ost_entitlements"));
        Assert.Equal(new[] { "region-kiyose", "region-two" }, (await store.GetSnapshotAsync(A)).Music.Select(m => m.CueId));
    }

    [Fact]
    public async Task ClearBeyondTheFrontier_GrantsNoCourseOrMusic()
    {
        Receipt r = (await SettleClear("m_gap", 5, new[] { "C05" }, new[] { new MusicGrant("region-kiyose", "stage-first-normal-clear", "S05") })).Receipts.Single();
        Assert.Empty(r.CoursesUnlocked);
        Assert.Empty(r.MusicUnlocked);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM course_entitlements"));
    }

    [Fact]
    public async Task TeamBests_AreTeamRecords_StoredOnlyWhenImproved_AndAddNoRankPoints()
    {
        async Task<Receipt> Trial(string matchId, long value)
        {
            await store.RecordAllocationAsync(new MatchRecord { MatchId = matchId, ConvoyId = "cv", ServerId = "srv", ConfigJson = "{}", ResultsSecret = "x" });
            return (await store.SettleAsync(new MatchSettlement
            {
                MatchId = matchId, ResultsSha256 = matchId,
                Entrants = new[]
                {
                    new EntrantSettlement
                    {
                        AccountId = A, Receipt = new Receipt { MatchId = matchId, AccountId = A, TeamTrial = new TeamTrialReceipt { TrialId = "TT_MEAN" } },
                        Facts = new PayoutFacts { AuthoredExpectedSeconds = 230, Kind = EventKind.FreeplayCircuit, Outcome = RunOutcome.Finished, Placement = 1 },
                        TeamBest = new TeamBestCandidate("TT_MEAN", "standard", 2, "mean", value, LowerIsBetter: true),
                    },
                },
            })).Receipts.Single();
        }
        Assert.True((await Trial("m_t1", 1_800_000)).TeamTrial!.NewTeamBest);
        Assert.False((await Trial("m_t2", 1_900_000)).TeamTrial!.NewTeamBest);
        Receipt best = await Trial("m_t3", 1_700_000);
        Assert.True(best.TeamTrial!.NewTeamBest);
        Assert.Equal(best.RankPointsBefore, best.RankPointsAfter); // trials are not an RP source
        TeamBestRecord stored = (await store.GetSnapshotAsync(A)).TeamBests.Single();
        Assert.Equal((1_700_000L, "m_t3", 2), (stored.Value, stored.MatchId, stored.Humans));
    }

    [Fact]
    public async Task WithheldTrialPay_CreditsNoEventMoney()
    {
        await store.RecordAllocationAsync(new MatchRecord { MatchId = "m_w", ConvoyId = "cv", ServerId = "srv", ConfigJson = "{}", ResultsSecret = "x" });
        Receipt r = (await store.SettleAsync(new MatchSettlement
        {
            MatchId = "m_w", ResultsSha256 = "w",
            Entrants = new[]
            {
                new EntrantSettlement
                {
                    AccountId = A, Receipt = new Receipt { MatchId = "m_w", AccountId = A },
                    Facts = new PayoutFacts { AuthoredExpectedSeconds = 230, Kind = EventKind.FreeplaySprint, Outcome = RunOutcome.DidNotFinish, CheckpointFraction = 0.9, ServerVerifiedActiveProgress = true },
                    PayoutWithheld = "Team Trials pay completion money only to active eligible human finishers.",
                },
            },
        })).Receipts.Single();
        Assert.Equal(0, r.Payout.Total);
        Assert.Equal(0, Balance());
        Assert.Contains("Team Trials", r.Payout.Note);
    }
}
