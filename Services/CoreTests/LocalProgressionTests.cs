using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

public sealed class LocalProgressionTests
{
    static ContentCatalogue Cat => TestContent.Catalogue;
    static int eventCounter;

    internal static LocalProfile NewProfile(string name = "Robin", string starter = "V01")
    {
        LocalProgressionResult r = LocalProgression.NewProfile(Cat, TestContent.Music,
            new NewLocalProfileRequest { DisplayName = name, StarterCarModelId = starter, Utc = TestContent.T0 }, new SequenceIds());
        Assert.Equal(LocalOperationStatus.Applied, r.Status);
        return r.Profile;
    }

    static string NextId(string tag) => $"le_{tag}_{Interlocked.Increment(ref eventCounter)}";

    /// <summary>A legal campaign run. qualify=false gives a finish inside the support envelope but slower than the target.</summary>
    internal static LocalEventFacts StageRun(LocalProfile p, string stageId, CampaignMode mode = CampaignMode.Normal, int placement = 1,
        bool qualify = true, bool beatRival = true, RunOutcome outcome = RunOutcome.Finished, long? finishMs = null)
    {
        StageDef stage = Cat.Stage(stageId);
        var benchmark = new StageBenchmark
        {
            Kind = stage.Type == "penultimate" ? BenchmarkKind.FourContracts : BenchmarkKind.Time,
            TargetTimeMs = 200_000,
            HardTimeoutMs = 400_000,
            RequiresBeatingFeaturedRival = StageBenchmark.IsFeaturedEncounter(stage.Type),
        };
        long ms = finishMs ?? (qualify ? 190_000 : 260_000);
        return new LocalEventFacts
        {
            EventId = NextId(stageId), Kind = EventKind.CampaignStage, CourseId = stage.Course, StageId = stageId, Mode = mode,
            CompletedUtc = TestContent.T0.AddHours(1), Outcome = outcome, FinishTimeMicros = outcome == RunOutcome.Finished ? ms * 1000 : 0,
            Placement = placement, CheckpointFraction = outcome == RunOutcome.Finished ? 1 : 0.5, ActiveProgressVerified = true,
            ActivelyDroveLegalCourse = true, ContractsPassed = 4, CarModelId = p.Cars[0].ModelId, CarInstanceId = p.Cars[0].InstanceId,
            Benchmark = benchmark, BeatFeaturedRival = beatRival,
        };
    }

    internal static LocalEventFacts FreeplayRun(LocalProfile p, string courseId, int placement, RunOutcome outcome = RunOutcome.Finished,
        EventKind kind = EventKind.FreeplaySprint, double checkpoints = 1.0) => new()
    {
        EventId = NextId(courseId), Kind = kind, CourseId = courseId, CompletedUtc = TestContent.T0.AddHours(2), Outcome = outcome,
        FinishTimeMicros = outcome == RunOutcome.Finished ? 185_000_000 : 0, Placement = placement, CheckpointFraction = checkpoints,
        ActiveProgressVerified = true, ActivelyDroveLegalCourse = true, CarModelId = p.Cars[0].ModelId, CarInstanceId = p.Cars[0].InstanceId,
    };

    internal static LocalProfile Apply(LocalProfile p, LocalEventFacts f, LocalOperationStatus expected = LocalOperationStatus.Applied)
    {
        LocalProgressionResult r = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, f);
        Assert.True(expected == r.Status, $"{r.Status}: {r.Reason}");
        return r.Profile;
    }

    internal static LocalProfile ClearNormalThrough(LocalProfile p, int lastStage)
    {
        for (int n = 1; n <= lastStage; n++)
            p = Apply(p, StageRun(p, CampaignProgress.StageLabel(n)));
        return p;
    }

    static long B(string courseId) => Economy.BaseCompletion(Cat.Course(courseId).ExpectedSeconds);

    // ---------------- new profile ----------------

    [Fact]
    public void NewProfile_HasChosenStarterInstance_StarterGrant_BaselineSoundtrack_AndNothingElse()
    {
        LocalProgressionResult r = LocalProgression.NewProfile(Cat, TestContent.Music,
            new NewLocalProfileRequest { DisplayName = "  Robin  ", StarterCarModelId = "V02", Utc = TestContent.T0 }, new SequenceIds());
        LocalProfile p = r.Profile;
        Assert.Equal(ProgressionDomain.Local, r.Domain);
        Assert.Equal("Robin", p.DisplayName);
        Assert.Equal(Limits.StarterGrantCredits, p.WalletBalance);
        OwnedCar car = Assert.Single(p.Cars);
        Assert.Equal("V02", car.ModelId);
        Assert.Equal(CarSource.Starter, car.Source);
        Assert.True(LocalProfile.IsValidId(car.InstanceId));
        Assert.Equal(new[] { "MUS_GARAGE", "MUS_MENU_A", "MUS_TITLE" }, p.Music.Select(m => m.CueId).OrderBy(x => x));
        Assert.All(p.Music, m => Assert.Equal(MusicUnlockSourceKind.Baseline, m.SourceKind));
        Assert.Empty(p.Courses); // starters are implicit
        Assert.True(p.OwnsCourse(Cat, "C01"));
        Assert.False(p.OwnsCourse(Cat, "C05"));
        Assert.Equal(0, p.ComputeRankPoints());
        Assert.Empty(p.Validate());
        Assert.Contains(r.Changes, c => c.Kind == ProgressionChangeKind.Credits && c.Amount == Limits.StarterGrantCredits);

        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.NewProfile(Cat, null,
            new NewLocalProfileRequest { DisplayName = "Robin", StarterCarModelId = "V18", Utc = TestContent.T0 }).Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.NewProfile(Cat, null,
            new NewLocalProfileRequest { DisplayName = "", StarterCarModelId = "V01", Utc = TestContent.T0 }).Status);
    }

    // ---------------- economy ----------------

    [Fact]
    public void Payouts_GoThroughCoreEconomy_IncludingPlacementsFourToTwelve()
    {
        LocalProfile p = NewProfile();
        long b = B("C01");
        for (int place = 1; place <= Limits.MaxRaceVehicles; place++)
        {
            LocalProgressionResult r = LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C01", place));
            Assert.Equal(LocalOperationStatus.Applied, r.Status);
            long expected = Economy.Compute(new PayoutFacts
            {
                AuthoredExpectedSeconds = Cat.Course("C01").ExpectedSeconds, Kind = EventKind.FreeplaySprint, Outcome = RunOutcome.Finished, Placement = place,
            }).EventCredits;
            Assert.Equal(expected, r.Payout.EventCredits);
            Assert.Equal(p.WalletBalance + expected, r.BalanceAfter);
            if (place >= 4) Assert.Equal(b, r.Payout.EventCredits); // 4th–12th all 1.00
        }
        Assert.Equal(b * 135 / 100, LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C01", 1)).Payout.EventCredits);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C01", 13)).Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C01", 0)).Status);
    }

    [Fact]
    public void DidNotFinish_Quit_AndTimeAttack_FollowTheSameEconomy()
    {
        LocalProfile p = NewProfile();
        long b = B("C01");
        Assert.Equal(b / 4, LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C01", 0, RunOutcome.DidNotFinish, checkpoints: 0.85)).Payout.EventCredits);
        Assert.Equal(0, LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C01", 0, RunOutcome.DidNotFinish, checkpoints: 0.5)).Payout.EventCredits);
        LocalProgressionResult quit = LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C01", 0, RunOutcome.Quit));
        Assert.Equal(0, quit.Payout.EventCredits);
        Assert.Equal(p.WalletBalance, quit.BalanceAfter);
        // Time Attack: reference band (185 s beats the C01 180 s? no → 1.00) and never a PvP bonus.
        LocalProgressionResult ta = LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C01", 0, kind: EventKind.FreeplayTimeTrial));
        Assert.Equal(b, ta.Payout.EventCredits);
        LocalEventFacts fast = FreeplayRun(p, "C01", 0, kind: EventKind.FreeplayTimeTrial);
        fast.FinishTimeMicros = 170_000_000;
        Assert.Equal(b * 120 / 100, LocalProgression.ApplyEvent(p, Cat, null, fast).Payout.EventCredits);
    }

    [Fact]
    public void WalletCap_ClampsWithAVisibleExplanation()
    {
        LocalProfile p = NewProfile();
        p.WalletBalance = Limits.WalletCap - 100;
        LocalProgressionResult r = LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C01", 1));
        Assert.Equal(Limits.WalletCap, r.BalanceAfter);
        ProgressionChange clamp = Assert.Single(r.Of(ProgressionChangeKind.CreditsClamped));
        Assert.Equal(r.Payout.EventCredits - 100, clamp.Amount);
    }

    [Fact]
    public void Operations_NeverModifyTheInputProfile_AndAreIdempotentPerEventId()
    {
        LocalProfile p = NewProfile();
        string before = ProfileJson.Serialize(p);
        LocalEventFacts run = StageRun(p, "S01");
        LocalProgressionResult first = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, run);
        Assert.Equal(before, ProfileJson.Serialize(p));
        Assert.NotSame(p, first.Profile);

        LocalProgressionResult again = LocalProgression.ApplyEvent(first.Profile, Cat, TestContent.Music, run);
        Assert.Equal(LocalOperationStatus.AlreadyApplied, again.Status);
        Assert.Empty(again.Changes);
        Assert.Same(first.Profile, again.Profile);
    }

    // ---------------- campaign ----------------

    [Fact]
    public void FirstClear_PaysBonusAndRpExactlyOncePerStageAndMode()
    {
        LocalProfile p = NewProfile();
        LocalProgressionResult r = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, StageRun(p, "S01"));
        Assert.True(r.Stage.EarnedClear);
        Assert.True(r.Stage.FirstClear);
        Assert.Equal(8_000, r.Payout.FirstClearBonus);
        Assert.Equal(B("C01") * 135 / 100, r.Payout.EventCredits);
        Assert.Equal(Limits.StarterGrantCredits + B("C01") * 135 / 100 + 8_000, r.BalanceAfter);
        Assert.Equal(RankPoints.NormalFirstClear, r.RankPointsAfter);
        Assert.Contains(r.Changes, c => c.Kind == ProgressionChangeKind.StageCleared && c.Subject == "S01");

        LocalProgressionResult replay = LocalProgression.ApplyEvent(r.Profile, Cat, TestContent.Music, StageRun(r.Profile, "S01", placement: 2));
        Assert.True(replay.Stage.EarnedClear);
        Assert.False(replay.Stage.FirstClear);
        Assert.Equal(0, replay.Payout.FirstClearBonus);
        Assert.Equal(B("C01") * 120 / 100, replay.Payout.EventCredits); // ordinary race money still paid
        Assert.Equal(RankPoints.NormalFirstClear, replay.RankPointsAfter);
        Assert.Contains(replay.Notes, n => n.Contains("already cleared"));
        Assert.Equal(new[] { 1 }, replay.Profile.Campaign.Normal);
    }

    [Fact]
    public void LosingLegalRun_PaysParticipation_NoClear_ButCanSetAPersonalBest()
    {
        LocalProfile p = NewProfile();
        LocalEventFacts run = StageRun(p, "S01", placement: 5, qualify: false);
        RecordKey key = RecordKey.ForCampaignStage(ProgressionDomain.Local, Cat.Stage("S01"), Cat.Course("C01"), CampaignMode.Normal, MetricKind.ElapsedTime, TestContent.Rules);
        run.Records.Add(new RecordCandidate { Key = key, Value = 260_000 });
        LocalProgressionResult r = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, run);
        Assert.False(r.Stage.EarnedClear);
        Assert.Equal(B("C01"), r.Payout.EventCredits);
        Assert.Equal(0, r.Payout.FirstClearBonus);
        Assert.False(r.Profile.Campaign.IsCleared(CampaignMode.Normal, 1));
        Assert.Equal(RecordUpdateOutcome.NewPersonalBest, Assert.Single(r.Records).Outcome);
        RecordView view = r.Profile.Records.View(key);
        Assert.Equal(RecordDisplayState.HasValue, view.State); // not cleared ≠ no record
        Assert.Equal("04:20.000", view.ValueText);

        // Clearing later keeps the bests visible; the faster clearing run becomes the new PB.
        LocalEventFacts clear = StageRun(r.Profile, "S01", finishMs: 199_000);
        clear.Records.Add(new RecordCandidate { Key = key, Value = 199_000 });
        LocalProgressionResult cleared = LocalProgression.ApplyEvent(r.Profile, Cat, TestContent.Music, clear);
        Assert.True(cleared.Stage.EarnedClear);
        Assert.Equal("03:19.000", cleared.Profile.Records.View(key).ValueText);
        Assert.Contains(cleared.Changes, c => c.Kind == ProgressionChangeKind.PersonalBest);
    }

    [Fact]
    public void Frontier_AndHardAccess_UseCampaignProgress()
    {
        LocalProfile p = NewProfile();
        Assert.False(LocalProgression.CanStartCampaignStage(p, Cat, "S02", CampaignMode.Normal, out string why));
        Assert.Contains("frontier", why);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, StageRun(p, "S02")).Status);
        Assert.False(LocalProgression.CanStartCampaignStage(p, Cat, "S01", CampaignMode.Hard, out why));
        Assert.Contains("Hard unlocks", why);
        Assert.Equal("Next stage: S01.", LocalProgression.CampaignAccess(p, CampaignMode.Normal).Explanation);

        p = ClearNormalThrough(p, 29);
        Assert.False(p.Campaign.HardUnlocked);
        Assert.False(LocalProgression.CampaignAccess(p, CampaignMode.Hard).ModeAllowed);
        p = Apply(p, StageRun(p, "S30"));
        Assert.True(p.Campaign.HardUnlocked);
        Assert.Equal(RankPoints.NormalBudget, p.ComputeRankPoints());
        Assert.True(LocalProgression.CanStartCampaignStage(p, Cat, "S01", CampaignMode.Hard, out _));
        Assert.False(LocalProgression.CanStartCampaignStage(p, Cat, "S02", CampaignMode.Hard, out _));
        Assert.Equal("Campaign complete: every stage is open for replay.", LocalProgression.CampaignAccess(p, CampaignMode.Normal).Explanation);

        LocalProgressionResult hard = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, StageRun(p, "S01", CampaignMode.Hard));
        Assert.Equal(12_000, hard.Payout.FirstClearBonus);
        Assert.Equal(135, hard.Payout.DifficultyX100);
        Assert.Equal(B("C01") * 135 * 135 / 10_000, hard.Payout.EventCredits);
        Assert.Equal(RankPoints.NormalBudget + RankPoints.HardFirstClear, hard.RankPointsAfter);
    }

    [Fact]
    public void NormalCampaign_UnlocksEveryDualCourse_AndC25_FromTheRightStages()
    {
        LocalProfile p = ClearNormalThrough(NewProfile(), 30);
        var expected = CourseAccess.RegularStageUnlocks(Cat).Keys.Concat(new[] { "C25" }).OrderBy(x => x).ToList();
        Assert.Equal(expected, p.Courses.Select(c => c.CourseId).OrderBy(x => x));
        Assert.All(p.Courses, c => Assert.Equal(CourseEntitlementSource.CampaignClear, c.Source));
        Assert.Equal("S23", p.Courses.Single(c => c.CourseId == "C20").Reference);
        Assert.Equal("S30", p.Courses.Single(c => c.CourseId == "C25").Reference);
        Assert.False(p.OwnsCourse(Cat, "FP01")); // currency-only courses are never campaign unlocks
    }

    [Fact]
    public void EncounterStages_NeedTheLiveRivalBeaten_AndABrokenEventIsAborted()
    {
        LocalProfile p = ClearNormalThrough(NewProfile(), 6);
        LocalProgressionResult lost = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, StageRun(p, "S07", beatRival: false));
        Assert.False(lost.Stage.EarnedClear);
        Assert.Contains("featured rival", lost.Stage.Reason);
        Assert.False(lost.Profile.HasCue("MUS_LT_DAIGO"));

        LocalEventFacts broken = StageRun(p, "S07");
        broken.FeaturedRivalStarted = false;
        LocalProgressionResult aborted = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, broken);
        Assert.Equal(LocalOperationStatus.Aborted, aborted.Status);
        Assert.Equal(p.WalletBalance, aborted.BalanceAfter);
        Assert.False(aborted.Profile.Campaign.IsCleared(CampaignMode.Normal, 7));
        Assert.Equal(LocalOperationStatus.AlreadyApplied, LocalProgression.ApplyEvent(aborted.Profile, Cat, TestContent.Music, broken).Status);

        LocalProgressionResult won = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, StageRun(p, "S07"));
        Assert.True(won.Stage.EarnedClear);
        Assert.Equal(20_000, won.Payout.FirstClearBonus);
        Assert.True(won.Profile.HasCue("MUS_LT_DAIGO"));
    }

    // ---------------- course access ----------------

    [Fact]
    public void CoursePurchase_IsAtomic_AndGrantsNothingButFreeplayAccess()
    {
        LocalProfile p = NewProfile();
        LocalProgressionResult poor = LocalProgression.PurchaseCourse(p, Cat, "C20", TestContent.T0);
        Assert.Equal(LocalOperationStatus.Rejected, poor.Status);
        Assert.Equal(CoursePurchaseOutcome.InsufficientFunds, poor.PurchaseOutcome);
        Assert.Same(p, poor.Profile);

        p.WalletBalance = 100_000;
        LocalProgressionResult bought = LocalProgression.PurchaseCourse(p, Cat, "C20", TestContent.T0);
        Assert.Equal(LocalOperationStatus.Applied, bought.Status);
        Assert.Equal(55_000, bought.BalanceAfter);
        Assert.True(bought.Profile.OwnsCourse(Cat, "C20"));
        Assert.Equal(CourseEntitlementSource.Purchased, bought.Profile.Courses.Single().Source);
        Assert.Equal(45_000, bought.Profile.Courses.Single().PricePaid);
        // No campaign stage, Hard access, RP or soundtrack from a purchase.
        Assert.False(LocalProgression.CanStartCampaignStage(bought.Profile, Cat, "S23", CampaignMode.Normal, out _));
        Assert.Equal(0, bought.RankPointsAfter);
        Assert.Equal(p.Music.Count, bought.Profile.Music.Count);
        Assert.Empty(bought.Profile.Campaign.Normal);

        LocalProgressionResult twice = LocalProgression.PurchaseCourse(bought.Profile, Cat, "C20", TestContent.T0);
        Assert.Equal(LocalOperationStatus.AlreadyApplied, twice.Status);
        Assert.Equal(CoursePurchaseOutcome.AlreadyOwned, twice.PurchaseOutcome);
        Assert.Equal(55_000, twice.BalanceAfter);

        Assert.Equal(CoursePurchaseOutcome.NotPurchasable, LocalProgression.PurchaseCourse(p, Cat, "C25", TestContent.T0).PurchaseOutcome);
        Assert.Equal(CoursePurchaseOutcome.AlreadyOwned, LocalProgression.PurchaseCourse(p, Cat, "C01", TestContent.T0).PurchaseOutcome);
        Assert.Equal(55_000, LocalProgression.PurchaseCourse(p, Cat, "FP01", TestContent.T0).BalanceAfter);
        Assert.Equal(46_000, LocalProgression.PurchaseCourse(p, Cat, "FP02", TestContent.T0).BalanceAfter);
        Assert.Equal(37_000, LocalProgression.PurchaseCourse(p, Cat, "FP03", TestContent.T0).BalanceAfter);
    }

    [Fact]
    public void UnlockThenPurchase_IsAlreadyOwnedWithNoDebit()
    {
        LocalProfile p = ClearNormalThrough(NewProfile(), 23);
        Assert.Equal(CourseEntitlementSource.CampaignClear, p.Courses.Single(c => c.CourseId == "C20").Source);
        long balance = p.WalletBalance;
        LocalProgressionResult r = LocalProgression.PurchaseCourse(p, Cat, "C20", TestContent.T0);
        Assert.Equal(CoursePurchaseOutcome.AlreadyOwned, r.PurchaseOutcome);
        Assert.Equal(balance, r.BalanceAfter);
        Assert.Contains(r.Changes, c => c.Kind == ProgressionChangeKind.CourseAlreadyOwned && c.Amount == 0);
    }

    [Fact]
    public void PurchaseThenUnlock_KeepsThePurchase_NoRefundNoDuplicate()
    {
        LocalProfile p = ClearNormalThrough(NewProfile(), 22);
        p = LocalProgression.PurchaseCourse(p, Cat, "C20", TestContent.T0).Profile;
        long afterPurchase = p.WalletBalance;
        LocalProgressionResult clear = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, StageRun(p, "S23"));
        Assert.True(clear.Stage.FirstClear);
        CourseEntitlement c20 = clear.Profile.Courses.Single(c => c.CourseId == "C20");
        Assert.Equal(CourseEntitlementSource.Purchased, c20.Source);
        Assert.Single(clear.Profile.Courses, c => c.CourseId == "C20");
        Assert.DoesNotContain(clear.Changes, c => c.Kind == ProgressionChangeKind.CourseUnlocked);
        Assert.Contains(clear.Notes, n => n.Contains("no refund"));
        Assert.Equal(afterPurchase + clear.Payout.Total, clear.BalanceAfter);
    }

    [Fact]
    public void Freeplay_RequiresLocalCourseAccess()
    {
        LocalProfile p = NewProfile();
        Assert.True(LocalProgression.CanStartFreeplay(p, Cat, "C04", out _));
        Assert.False(LocalProgression.CanStartFreeplay(p, Cat, "C05", out string why));
        Assert.Contains("45,000", why);
        Assert.Contains("S05", why);
        Assert.False(LocalProgression.CanStartFreeplay(p, Cat, "C25", out why));
        Assert.Contains("Not sold", why);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, FreeplayRun(p, "C05", 1)).Status);
    }

    // ---------------- challenges, cosmetics, soundtrack ----------------

    [Fact]
    public void ChallengeReward_IsAwardedExactlyOnce_AndNeedsAValidFinish()
    {
        LocalProfile p = NewProfile();
        LocalEventFacts dnf = FreeplayRun(p, "C01", 0, RunOutcome.DidNotFinish, checkpoints: 0.9);
        dnf.ChallengesCompleted.Add("CH01");
        LocalProgressionResult ignored = LocalProgression.ApplyEvent(p, Cat, null, dnf);
        Assert.False(ignored.Profile.HasCompletedChallenge("CH01"));
        Assert.Contains(ignored.Notes, n => n.Contains("require a valid finish"));

        LocalEventFacts run = FreeplayRun(p, "C01", 3);
        run.ChallengesCompleted.Add("CH01");
        LocalProgressionResult r = LocalProgression.ApplyEvent(p, Cat, null, run);
        Assert.True(r.Profile.HasCompletedChallenge("CH01"));
        Assert.True(r.Profile.OwnsCosmetic("COS-CH01"));
        Assert.Equal(RankPoints.BronzeChallenge, r.RankPointsAfter);
        Assert.Equal(3_000, r.Payout.ChallengeCash);
        Assert.Equal(p.WalletBalance + r.Payout.EventCredits + 3_000, r.BalanceAfter);
        Assert.Contains(r.Changes, c => c.Kind == ProgressionChangeKind.CosmeticGranted && c.Subject == "COS-CH01");

        LocalEventFacts repeat = FreeplayRun(r.Profile, "C01", 3);
        repeat.ChallengesCompleted.Add("CH01");
        LocalProgressionResult again = LocalProgression.ApplyEvent(r.Profile, Cat, null, repeat);
        Assert.Equal(0, again.Payout.ChallengeCash);
        Assert.Equal(RankPoints.BronzeChallenge, again.RankPointsAfter);
        Assert.Single(again.Profile.Challenges);
        Assert.Single(again.Profile.Cosmetics);
        Assert.Contains(again.Notes, n => n.Contains("already completed"));

        LocalEventFacts unknown = FreeplayRun(p, "C01", 1);
        unknown.ChallengesCompleted.Add("CH99");
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, unknown).Status);
    }

    [Fact]
    public void Soundtrack_UnlocksOnceFromItsDeclaredSource_NeverFromPurchaseOrLoss()
    {
        LocalProfile p = NewProfile();
        LocalProfile won = Apply(p, StageRun(p, "S01"));
        Assert.True(won.HasCue("MUS_RACE_MIZUHANA"));
        LocalProgressionResult replay = LocalProgression.ApplyEvent(won, Cat, TestContent.Music, StageRun(won, "S01"));
        Assert.Single(replay.Profile.Music, m => m.CueId == "MUS_RACE_MIZUHANA");
        Assert.DoesNotContain(replay.Changes, c => c.Kind == ProgressionChangeKind.MusicUnlocked);

        LocalProfile atS05 = ClearNormalThrough(NewProfile(), 4);
        atS05.WalletBalance = 100_000;
        LocalProfile bought = LocalProgression.PurchaseCourse(atS05, Cat, "C05", TestContent.T0).Profile;
        Assert.False(bought.HasCue("MUS_RACE_KASUMI"));
        LocalProfile lost = Apply(bought, StageRun(bought, "S05", qualify: false));
        Assert.False(lost.HasCue("MUS_RACE_KASUMI"));
        LocalProfile freeplayOnC05 = Apply(lost, FreeplayRun(lost, "C05", 1));
        Assert.False(freeplayOnC05.HasCue("MUS_RACE_KASUMI"));
        LocalProfile cleared = Apply(freeplayOnC05, StageRun(freeplayOnC05, "S05"));
        Assert.True(cleared.HasCue("MUS_RACE_KASUMI"));
        MusicUnlock cue = cleared.Music.Single(m => m.CueId == "MUS_RACE_KASUMI");
        Assert.Equal(MusicUnlockSourceKind.StageFirstNormalClear, cue.SourceKind);
        Assert.Equal("S05", cue.SourceRef);
        Assert.Equal(0, cleared.ComputeRankPoints() - RankPoints.NormalFirstClear * 5); // OST adds no RP
    }

    [Fact]
    public void MusicManifest_IsValidatedAgainstTheCatalogue()
    {
        Assert.Equal(3, TestContent.Music.Baseline().Count);
        Assert.Equal(new[] { "MUS_LT_DAIGO" }, TestContent.Music.ForStageClear("S07", CampaignMode.Normal).Select(r => r.CueId));
        Assert.Empty(TestContent.Music.ForStageClear("S07", CampaignMode.Hard));
        Assert.Equal(new[] { "MUS_FINAL_SHIORI" }, TestContent.Music.ForStageClear("S30", CampaignMode.Hard).Select(r => r.CueId));
        Assert.Throws<ContentLoadException>(() => MusicUnlockTable.Create(new[] { new MusicUnlockRule("MUS_X", MusicUnlockSourceKind.LieutenantFirstDefeat, "S05") }, Cat));
        Assert.Throws<ContentLoadException>(() => MusicUnlockTable.Create(new[] { new MusicUnlockRule("MUS_X", MusicUnlockSourceKind.StageFirstNormalClear, "S99") }, Cat));
        Assert.Throws<ContentLoadException>(() => MusicUnlockTable.Create(new[]
        {
            new MusicUnlockRule("MUS_X", MusicUnlockSourceKind.Baseline), new MusicUnlockRule("MUS_X", MusicUnlockSourceKind.Baseline),
        }, Cat));
        Assert.Throws<ContentLoadException>(() => MusicUnlockTable.Create(new[] { new MusicUnlockRule("MUS_X", MusicUnlockSourceKind.TrialFirstVictory, trialId: "TT_NOPE") },
            Cat, TestContent.TrialIds));
        Assert.Throws<ContentLoadException>(() => MusicUnlockTable.Parse("{\"schema\":\"wrong\",\"cues\":[]}", Cat));
    }

    [Fact]
    public void Tutorial_PaysOnce_AndRepeatsPayNothing()
    {
        LocalProfile p = NewProfile();
        LocalEventFacts tut = new()
        {
            EventId = NextId("tut"), Kind = EventKind.Tutorial, CourseId = "T00", CompletedUtc = TestContent.T0, Outcome = RunOutcome.Finished,
            FinishTimeMicros = 100_000_000, CheckpointFraction = 1, ActiveProgressVerified = true, ActivelyDroveLegalCourse = true,
            CarModelId = "V01", Loaner = true,
        };
        LocalProgressionResult first = LocalProgression.ApplyEvent(p, Cat, null, tut);
        Assert.Equal(p.WalletBalance + Limits.TutorialCompletionCredits, first.BalanceAfter);
        Assert.True(first.Profile.Tutorial.Completed);
        Assert.Equal(0, first.RankPointsAfter);
        tut.EventId = NextId("tut");
        LocalProgressionResult repeat = LocalProgression.ApplyEvent(first.Profile, Cat, null, tut);
        Assert.Equal(first.BalanceAfter, repeat.BalanceAfter);
        Assert.Contains(repeat.Notes, n => n.Contains("do not pay"));
    }

    // ---------------- Team Trials ----------------

    static LocalEventFacts TrialRun(LocalProfile p, TeamTrialKind kind, bool playerWins, RunOutcome humanOutcome = RunOutcome.Finished)
    {
        var mine = new List<TeamMemberResult>
        {
            new() { EntrantId = "local", Human = true, Outcome = humanOutcome, AdjustedFinishMs = humanOutcome == RunOutcome.Finished ? 180_000 : 0, RawDriftScore = 9_000 },
        };
        for (int i = 1; i < 6; i++)
            mine.Add(new TeamMemberResult { EntrantId = $"ally{i}", Outcome = RunOutcome.Finished, AdjustedFinishMs = 185_000 + i * 1_000, RawDriftScore = 8_000 });
        var theirs = Enumerable.Range(1, 6).Select(i => new TeamMemberResult
        {
            EntrantId = $"opp{i}", Outcome = RunOutcome.Finished,
            AdjustedFinishMs = playerWins ? 200_000 + i * 1_000 : 150_000 + i * 1_000, RawDriftScore = playerWins ? 1_000 : 50_000,
        }).ToList();
        string trial = kind == TeamTrialKind.Mean ? "TT_MEAN" : kind == TeamTrialKind.Best ? "TT_BEST" : "TT_DRIFT";
        return new LocalEventFacts
        {
            EventId = NextId(trial), Kind = kind == TeamTrialKind.Drift ? EventKind.FreeplayDriftAttack : EventKind.FreeplaySprint, CourseId = "C01",
            CompletedUtc = TestContent.T0, Outcome = humanOutcome, FinishTimeMicros = humanOutcome == RunOutcome.Finished ? 180_000_000 : 0,
            CheckpointFraction = 1, ActiveProgressVerified = true, ActivelyDroveLegalCourse = true, CarModelId = p.Cars[0].ModelId,
            CarInstanceId = p.Cars[0].InstanceId, Placement = 1,
            TeamTrial = new LocalTeamTrialFacts
            {
                TrialId = trial, Kind = kind, Difficulty = "standard", HardTimeoutMs = 300_000, ParticipationEnvelopeMs = 270_000,
                PlayerSide = mine, OpposingSide = theirs, LocalEntrantId = "local",
                TeamRecordKey = RecordKey.ForTeamTrial(ProgressionDomain.Local, trial, "C01", kind == TeamTrialKind.Drift ? "drift-attack" : "sprint",
                    "standard", kind, 1, TestContent.Rules),
            },
        };
    }

    [Fact]
    public void TeamTrial_Victory_PaysBoundedModifier_UnlocksItsCueOnce_RecordsTheTeamSum_AndGivesNoRp()
    {
        LocalProfile p = NewProfile();
        LocalProgressionResult r = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, TrialRun(p, TeamTrialKind.Mean, playerWins: true));
        Assert.Equal(LocalOperationStatus.Applied, r.Status);
        Assert.Equal(TeamTrialVerdict.PlayerTeamWins, r.TeamTrial.Verdict);
        Assert.Equal(135, r.Payout.PlacementX100); // declared victory placement 1, not ×6 or ×12
        Assert.Equal(B("C01") * 135 / 100, r.Payout.EventCredits);
        Assert.Equal(0, r.RankPointsAfter);
        Assert.True(r.Profile.HasCue("MUS_TT_MEAN"));
        long sum = 180_000 + Enumerable.Range(1, 5).Sum(i => 185_000L + i * 1_000);
        Assert.Equal(sum, r.TeamTrial.PlayerTeamValue);
        RecordKey teamKey = TrialRun(p, TeamTrialKind.Mean, true).TeamTrial.TeamRecordKey;
        Assert.Equal(sum, r.Profile.Records.Best(teamKey).Value);
        Assert.Equal(RecordFormat.Time(sum / 6), r.Profile.Records.View(teamKey).ValueText);

        LocalProgressionResult again = LocalProgression.ApplyEvent(r.Profile, Cat, TestContent.Music, TrialRun(r.Profile, TeamTrialKind.Mean, playerWins: true));
        Assert.Single(again.Profile.Music, m => m.CueId == "MUS_TT_MEAN");
        Assert.Equal(RecordUpdateOutcome.Tie, Assert.Single(again.Records).Outcome);
    }

    [Fact]
    public void TeamTrial_DefeatOrHumanDnf_GrantsNoCue_AndAnAfkHumanCollectsNothingForAnAiWin()
    {
        LocalProfile p = NewProfile();
        LocalProgressionResult lost = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, TrialRun(p, TeamTrialKind.Drift, playerWins: false));
        Assert.Equal(TeamTrialVerdict.OpposingTeamWins, lost.TeamTrial.Verdict);
        Assert.Equal(100, lost.Payout.PlacementX100); // defeat placement 4
        Assert.False(lost.Profile.HasCue("MUS_TT_DRIFT"));

        LocalProgressionResult dnf = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, TrialRun(p, TeamTrialKind.Best, playerWins: true, RunOutcome.DidNotFinish));
        Assert.Equal(TeamTrialVerdict.PlayerTeamWins, dnf.TeamTrial.Verdict); // the AI allies won…
        Assert.False(dnf.TeamTrial.CompletionPayable);                        // …but the human did not earn completion pay
        Assert.Equal(0, dnf.Payout.EventCredits);
        Assert.Equal(p.WalletBalance, dnf.BalanceAfter);
        Assert.Empty(dnf.Profile.Records.Entries);
    }

    [Fact]
    public void TeamTrial_FactsAreValidated_OneLocalHumanAndSixPerSide()
    {
        LocalProfile p = NewProfile();
        LocalEventFacts twoHumans = TrialRun(p, TeamTrialKind.Mean, true);
        twoHumans.TeamTrial.PlayerSide[1].Human = true;
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, twoHumans).Status);
        LocalEventFacts five = TrialRun(p, TeamTrialKind.Mean, true);
        five.TeamTrial.OpposingSide.RemoveAt(0);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, five).Status);
        LocalEventFacts personalTeam = FreeplayRun(p, "C01", 1);
        personalTeam.Records.Add(new RecordCandidate { Key = TrialRun(p, TeamTrialKind.Mean, true).TeamTrial.TeamRecordKey, Value = 1_000_000 });
        LocalProgressionResult r = LocalProgression.ApplyEvent(p, Cat, null, personalTeam);
        Assert.Empty(r.Profile.Records.Entries); // a team value is never accepted as a submitted personal record
        Assert.Contains(r.Notes, n => n.StartsWith("Record skipped"));
    }

    // ---------------- records validation in events ----------------

    [Fact]
    public void EventRecords_MustMatchTheEvent_Domain_AndContactPolicy()
    {
        LocalProfile p = NewProfile();
        LocalEventFacts run = FreeplayRun(p, "C01", 1, kind: EventKind.FreeplayTimeTrial);
        var contactRules = TestContent.Rules;
        var ghostRules = new RecordRuleset { CourseRevision = "r1", PhysicsVersion = "phys-3", ScoringVersion = "score-2", Conditions = "night-dry", Contact = ContactPolicy.NonContact };
        run.Records.Add(new RecordCandidate { Key = RecordKey.ForFreeplay(ProgressionDomain.Local, "C01", "time-attack", MetricKind.ElapsedTime, contactRules), Value = 185_000 });
        run.Records.Add(new RecordCandidate { Key = RecordKey.ForFreeplay(ProgressionDomain.Online, "C01", "time-attack", MetricKind.ElapsedTime, ghostRules), Value = 185_000 });
        run.Records.Add(new RecordCandidate { Key = RecordKey.ForFreeplay(ProgressionDomain.Local, "C02", "time-attack", MetricKind.ElapsedTime, ghostRules), Value = 185_000 });
        RecordKey good = RecordKey.ForFreeplay(ProgressionDomain.Local, "C01", "time-attack", MetricKind.ElapsedTime, ghostRules);
        run.Records.Add(new RecordCandidate { Key = good, Value = 185_000 });
        LocalProgressionResult r = LocalProgression.ApplyEvent(p, Cat, null, run);
        Assert.Equal(3, r.Notes.Count(n => n.StartsWith("Record skipped")));
        RecordEntry e = Assert.Single(r.Profile.Records.Entries);
        Assert.Equal(good, e.Key);
        Assert.Equal(RecordVerification.LocalUnverified, e.Verification);
        Assert.Equal(RecordEntry.LocalOrigin, e.Provenance.Origin);
        Assert.Equal(p.Cars[0].InstanceId, e.CarInstanceId);
        Assert.Equal(Cat.ContentHash, e.Provenance.RulesVersion);
    }

    [Fact]
    public void EventFacts_AreValidated_BeforeAnythingChanges()
    {
        LocalProfile p = NewProfile();
        LocalEventFacts noCar = FreeplayRun(p, "C01", 1);
        noCar.CarInstanceId = "ci_doesnotexist0000";
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, noCar).Status);
        LocalEventFacts wrongModel = FreeplayRun(p, "C01", 1);
        wrongModel.CarModelId = "V03";
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, wrongModel).Status);
        LocalEventFacts badUtility = FreeplayRun(p, "C01", 1);
        badUtility.UtilityIncomePercent = 50;
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, badUtility).Status);
        LocalEventFacts badId = FreeplayRun(p, "C01", 1);
        badId.EventId = "";
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, badId).Status);
        LocalEventFacts wrongCourse = StageRun(p, "S01");
        wrongCourse.CourseId = "C02";
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, null, wrongCourse).Status);
        LocalEventFacts loaner = StageRun(p, "S01");
        loaner.CarInstanceId = null;
        loaner.Loaner = true;
        loaner.CarModelId = "V05";
        Assert.Equal(LocalOperationStatus.Applied, LocalProgression.ApplyEvent(p, Cat, null, loaner).Status); // class-legal loaner
    }

    // ---------------- cars ----------------

    [Fact]
    public void CarPurchases_CreateIndependentInstances_AndDoubleSubmitNeverChargesTwice()
    {
        LocalProfile p = NewProfile(starter: "V01");
        p.WalletBalance = 100_000;
        var ids = new SequenceIds(start: 1000);
        LocalProgressionResult first = LocalProgression.PurchaseCar(p, Cat, "V01", "buy_v01_a", TestContent.T0, ids);
        Assert.Equal(LocalOperationStatus.Applied, first.Status);
        Assert.Equal(100_000 - Cat.Car("V01").Price, first.BalanceAfter);
        LocalProgressionResult retry = LocalProgression.PurchaseCar(first.Profile, Cat, "V01", "buy_v01_a", TestContent.T0, ids);
        Assert.Equal(LocalOperationStatus.AlreadyApplied, retry.Status);
        Assert.Equal(first.BalanceAfter, retry.BalanceAfter);
        LocalProgressionResult second = LocalProgression.PurchaseCar(first.Profile, Cat, "V01", "buy_v01_b", TestContent.T0, ids);
        List<OwnedCar> v01s = second.Profile.Cars.Where(c => c.ModelId == "V01").ToList();
        Assert.Equal(3, v01s.Count);
        Assert.Equal(3, v01s.Select(c => c.InstanceId).Distinct().Count());
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.PurchaseCar(second.Profile, Cat, "V18", "buy_v18", TestContent.T0, ids).Status);
    }
}

public sealed class LocalCardTests
{
    [Fact]
    public void SetCard_StoresTheLookCanonically_RejectsBadInput_AndIsIdempotent()
    {
        LocalProfile p = LocalProgressionTests.NewProfile("Robin");
        Assert.Equal("", p.Card.Look);
        NightSignal.Characters.CharacterLook preset = NightSignal.Characters.PlayerLooks.Copy(NightSignal.Characters.PlayerLooks.Presets[2]);
        string look = NightSignal.Characters.PlayerLooks.Canonical(preset);

        LocalProgressionResult set = LocalProgression.SetCard(p, "Robin Night", look, "they/them");
        Assert.Equal(LocalOperationStatus.Applied, set.Status);
        Assert.Equal("Robin Night", set.Profile.DisplayName);
        Assert.Equal(look, set.Profile.Card.Look);
        Assert.Equal("they/them", set.Profile.Card.Pronouns);
        Assert.Empty(set.Profile.Validate());
        Assert.Contains(set.Changes, c => c.Kind == ProgressionChangeKind.CardChanged);

        // Round trip through the stored document keeps it exactly.
        LocalProfile reread = ProfileJson.Clone(set.Profile);
        Assert.Equal(look, reread.Card.Look);
        Assert.Equal(LocalOperationStatus.AlreadyApplied, LocalProgression.SetCard(reread, "Robin Night", look, "they/them").Status);

        // Bad input is refused and nothing changes.
        preset.Accessories = new List<string>(NightSignal.Characters.CharacterVocabulary.Accessories.Take(5));
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.SetCard(p, "Robin", NightSignal.Characters.PlayerLooks.Canonical(preset), "").Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.SetCard(p, "Robin", "{not a look", "").Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.SetCard(p, "Robin", look, new string('x', 25)).Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.SetCard(p, "Robin", look, "<b>").Status);

        // Back to the default look.
        LocalProgressionResult cleared = LocalProgression.SetCard(set.Profile, "Robin Night", "", "");
        Assert.Equal(LocalOperationStatus.Applied, cleared.Status);
        Assert.Equal("", cleared.Profile.Card.Look);
    }

    [Fact]
    public void AProfileSavedBeforeTheCardLook_LoadsWithTheDefaultLook()
    {
        LocalProfile p = LocalProgressionTests.NewProfile("Robin");
        var doc = Newtonsoft.Json.Linq.JObject.Parse(ProfileJson.Serialize(p));
        var card = (Newtonsoft.Json.Linq.JObject)doc["card"]!;
        Assert.True(card.Remove("look") && card.Remove("pronouns"), "the stored card has the new fields");
        LocalProfile old = ProfileJson.Deserialize<LocalProfile>(doc.ToString());
        Assert.Equal("", old.Card.Look ?? "");
        Assert.Empty(old.Validate());
    }
}

public sealed class LocalCardStyleTests
{
    static readonly Lazy<NightSignal.Core.Customization.CustomizationCatalogue> customization = new(() => NightSignal.Core.Customization.CustomizationCatalogue.Load(
        File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", NightSignal.Core.Customization.CustomizationCatalogue.FileName))));

    [Fact]
    public void SetCard_Style_StoredWhenAllowed_RewardsNeedOwnership()
    {
        var card = customization.Value.Card;
        LocalProfile p = LocalProgressionTests.NewProfile("Robin", "V02");
        var style = card.Default.Copy();
        style.Background = "tea-rows";
        style.Motif = "lantern";
        style.Region = "PT";
        style.PreferredCar = "V02";
        LocalProgressionResult set = LocalProgression.SetCard(p, "Robin", "", "", style, card);
        Assert.Equal(LocalOperationStatus.Applied, set.Status);
        Assert.True(LocalProgression.StyleOf(set.Profile.Card, card.Default).ContentEquals(style));
        Assert.Empty(set.Profile.Validate());
        Assert.Equal(LocalOperationStatus.AlreadyApplied, LocalProgression.SetCard(set.Profile, "Robin", "", "", style, card).Status);

        var locked = style.Copy();
        locked.Frame = "balance-point"; // COS-CH57, not owned
        LocalProgressionResult refused = LocalProgression.SetCard(set.Profile, "Robin", "", "", locked, card);
        Assert.Equal(LocalOperationStatus.Rejected, refused.Status);
        Assert.Equal("Not owned yet: Balance Point Frame.", refused.Reason);
        var otherCar = style.Copy();
        otherCar.PreferredCar = "V17";
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.SetCard(set.Profile, "Robin", "", "", otherCar, card).Status);

        LocalProfile owner = ProfileJson.Clone(set.Profile);
        owner.Cosmetics.Add(new OwnedCosmetic { CosmeticId = "COS-CH57", Source = "CH57" });
        Assert.Equal(LocalOperationStatus.Applied, LocalProgression.SetCard(owner, "Robin", "", "", locked, card).Status);
    }
}

public sealed class LocalShowcaseTests
{
    static RecordEntry Entry(RecordEventType type, string eventId, string course, string format, string difficulty, MetricKind metric, long value) => new()
    {
        Key = new RecordKey { EventType = type, EventId = eventId, CourseId = course, Format = format, Difficulty = difficulty, Metric = metric },
        Value = value,
    };

    [Fact]
    public void Showcase_OwnRecordsOnly_BestKept_StoredOnTheCard()
    {
        LocalProfile p = LocalProgressionTests.NewProfile("Robin");
        p.Records.Entries.Add(Entry(RecordEventType.Freeplay, "C01/sprint", "C01", "sprint", "", MetricKind.ElapsedTime, 151_408));
        p.Records.Entries.Add(Entry(RecordEventType.Freeplay, "C01/sprint", "C01", "sprint", "", MetricKind.ElapsedTime, 149_000));
        p.Records.Entries.Add(Entry(RecordEventType.CampaignStage, "S07", "C04", "sprint", "normal", MetricKind.ElapsedTime, 190_329));
        p.Records.Entries.Add(Entry(RecordEventType.Freeplay, "C08/drift-attack", "C08", "drift-attack", "", MetricKind.RawDriftScore, 71_250));
        var records = LocalShowcase.Records(p, TestContent.Catalogue).ToDictionary(r => r.Key, r => r.Value);
        Assert.Equal("2:29.000", records["course:C01:sprint"]);
        Assert.Equal("3:10.329", records["stage:S07:normal"]);
        Assert.Equal("71,250 raw", records["course:C08:drift-attack"]);

        LocalProgressionResult set = LocalProgression.SetCard(p, "Robin", "", "", showcase: new[] { "stage:S07:normal", "course:C01:sprint" }, content: TestContent.Catalogue);
        Assert.Equal(LocalOperationStatus.Applied, set.Status);
        Assert.Equal(new[] { "stage:S07:normal", "course:C01:sprint" }, set.Profile.Card.Showcase);
        Assert.Empty(set.Profile.Validate());
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.SetCard(p, "Robin", "", "", showcase: new[] { "course:C09:sprint" }, content: TestContent.Catalogue).Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.SetCard(p, "Robin", "", "", showcase: new[] { "stage:S07:normal", "stage:S07:normal" }, content: TestContent.Catalogue).Status);
        Assert.Equal(LocalOperationStatus.AlreadyApplied, LocalProgression.SetCard(set.Profile, "Robin", "", "", showcase: new[] { "stage:S07:normal", "course:C01:sprint" }, content: TestContent.Catalogue).Status);
    }
}
