using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

/// <summary>Challenge trials (docs/CHALLENGE_TRIALS.md): the authored file, the supplied loaners and the judge.</summary>
public sealed class ChallengeTrialsTests
{
    static ContentCatalogue Cat => TestContent.Catalogue;
    static ChallengeTrialsFile Trials => Cat.ChallengeTrials;
    static readonly Lazy<PartsCatalogue> parts = new(() =>
        PartsCatalogue.Load(File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", "parts.json"))));

    static ChallengeTrialDef Trial(long timeMs = 0, long driftRaw = 0, TrialRules? rules = null, string id = "TR-X", string challenge = "CH55", string group = "") => new()
    {
        Kind = timeMs > 0 && driftRaw > 0 ? "time+drift" : driftRaw > 0 ? "drift" : "time", Id = id, Challenge = challenge, Course = "C04", Loaner = new TrialLoaner { Car = "V01" }, Rules = rules ?? new TrialRules(),
        Targets = new TrialTargets { TimeMs = timeMs, DriftRaw = driftRaw }, Group = group,
    };

    static TrialRunFacts Run(long timeMs = 90_000, int resets = 0, int walls = 0, long drift = 0, float handbrake = 0f, int banked = 0, int zones = 0) => new()
    {
        Finished = true, TimeMs = timeMs, Resets = resets, WallImpacts = walls, DriftRaw = drift, HandbrakeSeconds = handbrake,
        ZonesBanked = banked, ZonesTotal = zones, DroveLoaner = true,
    };

    [Fact]
    public void TheAuthoredTrials_LoadIntoTheHashedCatalogue_OnePerChallengeOrOneGroup()
    {
        Assert.Contains(ContentCatalogue.AuthoredFiles, f => f == "challenge-trials.json");
        Assert.Equal(14, Trials.Trials.Count);
        Assert.Equal(new[] { "CH11", "CH13", "CH15", "CH25", "CH28", "CH30", "CH36", "CH39", "CH40", "CH41", "CH51", "CH54", "CH55" }, Trials.Trials.Select(t => t.Challenge).Distinct().OrderBy(c => c));
        ChallengeTrialDef ch36 = Trials.Find("TR-CH36")!;
        Assert.True(ch36.IsRace && ch36.Rules.ZonePassRole == "pacing" && ch36.Field.Single().Role == "pacing");
        // The racecraft trials: fixed fields in the loaner's class, the player starting last.
        ChallengeTrialDef ch41 = Trials.Find("TR-CH41")!, ch40 = Trials.Find("TR-CH40")!;
        Assert.True(ch41.IsRace && ch41.PlayerStartsLast && ch41.Field.Count == 5 && ch41.Rules.Win && ch41.Rules.NoReset && ch41.Rules.NoCarContact);
        Assert.All(ch41.Field, c => Assert.Equal(ch41.Loaner.Car, c.Car)); // class-equalized: six identical stock cars
        Assert.True(ch40.IsRace && ch40.Rules.CleanZonePass && ch40.Rules.NoCarContact && ch40.Rules.NoCheckpointCut && !ch40.Rules.Win);
        ChallengeTrialDef ch39 = Trials.Find("TR-CH39")!;
        Assert.True(ch39.IsRace && ch39.Rules.PressureSector && ch39.Field.Single().Role == "pressure" && !ch39.PlayerStartsLast);
        foreach (ChallengeTrialDef race in new[] { ch40, ch41, ch36, ch39 })
            Assert.All(race.Field, c => Assert.True(Cat.Car(c.Car).BasePI <= race.Loaner.PiCap, $"{race.Id}: {c.Car} outside the class"));
        Assert.All(Trials.Trials.Where(t => !t.IsRace), t => Assert.Empty(t.Field));
        Assert.True(Trials.Find("TR-CH15")!.Rules.AllChallengeGates && Trials.Find("TR-CH15")!.Rules.NoReset);
        Assert.True(Trials.Find("TR-CH13")!.Ghost && Trials.Find("TR-CH13")!.Rules.AllTyresPaved); // the fixed Gold ghost, tyres on the paved road
        Assert.Equal(2, Trials.ForChallenge("CH54").Count);
        Assert.All(Trials.ForChallenge("CH54"), t => Assert.Equal("CH54-LAYOUTS", t.Group));
        Assert.Equal(new[] { "TR-CH25", "TR-CH28", "TR-CH30" }, Trials.Trials.Where(t => t.JudgesDrift).Select(t => t.Id));
        Assert.True(Trials.Find("TR-CH28")!.JudgesTime); // time and raw drift in the same run
        Assert.All(Trials.Trials, t => Assert.Equal(Cat.TryChallenge(t.Challenge, out ChallengeDef c) ? c.Tier : "?", t.Tier));
        Assert.Empty(TrialJudge.Problems(Trials, id => Cat.TryCourse(id, out _), id => Cat.TryChallenge(id, out _), id => Cat.Cars.Any(c => c.Id == id)));
    }

    [Fact]
    public void EveryLoaner_ResolvesLikeAGarageBuild_WithinItsCap()
    {
        foreach (ChallengeTrialDef t in Trials.Trials)
        {
            CarDef car = Cat.Car(t.Loaner.Car);
            ResolveResult r = TrialLoaners.Resolve(t.Loaner, car, Cat.CarTunings[car.Id], parts.Value, out PiEstimate? pi);
            Assert.True(r.Ok, $"{t.Id}: {string.Join("; ", r.Issues)}");
            Assert.Equal(t.Loaner.Parts.Values.OrderBy(x => x), r.Spec.PartIds.OrderBy(x => x));
            if (t.Loaner.PiCap > 0) Assert.True(pi!.Value <= t.Loaner.PiCap, $"{t.Id}: PI {pi.Value} over the cap {t.Loaner.PiCap}");
        }
        // The two CH54 layouts are equalised under one cap: front drive and rear drive.
        List<ChallengeTrialDef> layouts = Trials.ForChallenge("CH54").ToList();
        Assert.Single(layouts.Select(t => t.Loaner.PiCap).Distinct());
        Assert.Equal(new[] { "FWD", "RWD" }, layouts.Select(t => Cat.Car(t.Loaner.Car).Drive).OrderBy(d => d));
    }

    [Fact]
    public void TheJudge_NamesEveryCondition_AndPassesOnlyWhenAllHold()
    {
        ChallengeTrialDef t = Trial(timeMs: 100_000, rules: new TrialRules { NoReset = true, MaxWallImpacts = 1 });
        TrialVerdict ok = TrialJudge.Judge(t, Run(timeMs: 99_999, walls: 1));
        Assert.True(ok.Passed, ok.Summary);
        Assert.Equal(5, ok.Checks.Count); // loaner, finish, time, reset, walls

        Assert.False(TrialJudge.Judge(t, Run(timeMs: 100_000)).Passed);          // equal is not faster
        Assert.False(TrialJudge.Judge(t, Run(resets: 1)).Passed);
        TrialVerdict walls = TrialJudge.Judge(t, Run(walls: 2));
        Assert.False(walls.Passed);
        Assert.Contains("MISSED: at most 1 wall impact (2)", walls.Summary);
        Assert.False(TrialJudge.Judge(t, Run() with { Finished = false }).Passed);
        Assert.False(TrialJudge.Judge(t, Run() with { DroveLoaner = false }).Passed); // any other car or build fails the trial
    }

    [Fact]
    public void DriftTrials_JudgeBankedRaw_Handbrake_AndEveryZone()
    {
        ChallengeTrialDef t = Trial(driftRaw: 50_000, rules: new TrialRules { NoHandbrake = true, BankEveryZone = true });
        Assert.True(TrialJudge.Judge(t, Run(drift: 50_000, banked: 4, zones: 4)).Passed);
        Assert.False(TrialJudge.Judge(t, Run(drift: 49_999, banked: 4, zones: 4)).Passed);
        Assert.False(TrialJudge.Judge(t, Run(drift: 60_000, banked: 3, zones: 4)).Passed);
        TrialVerdict hb = TrialJudge.Judge(t, Run(drift: 60_000, handbrake: 0.4f, banked: 4, zones: 4));
        Assert.False(hb.Passed);
        Assert.Contains("held 0.4 s", hb.Summary);
    }

    [Fact]
    public void ThePavedRule_FailsTheTrial_OnAnyTimeOffThePavedRoad()
    {
        ChallengeTrialDef t = Trial(timeMs: 100_000, rules: new TrialRules { AllTyresPaved = true });
        Assert.True(TrialJudge.Judge(t, Run(timeMs: 99_000)).Passed);
        TrialVerdict shoulder = TrialJudge.Judge(t, Run(timeMs: 99_000) with { OffPavedSeconds = 0.3f });
        Assert.False(shoulder.Passed);
        Assert.Contains("MISSED: all tyres on the paved road (off it 0.3 s)", shoulder.Summary);
    }

    [Fact]
    public void TheGateRule_NeedsEveryMarkedGate()
    {
        ChallengeTrialDef t = Trial(timeMs: 100_000, rules: new TrialRules { AllChallengeGates = true });
        Assert.True(TrialJudge.Judge(t, Run(timeMs: 99_000) with { ChallengeGates = 3, ChallengeGatesTouched = true }).Passed);
        Assert.False(TrialJudge.Judge(t, Run(timeMs: 99_000) with { ChallengeGates = 3, ChallengeGatesTouched = false }).Passed);
        Assert.False(TrialJudge.Judge(t, Run(timeMs: 99_000) with { ChallengeGates = 0, ChallengeGatesTouched = true }).Passed); // a course without them
    }

    [Fact]
    public void ARacecraftTrial_IsJudgedByItsRules()
    {
        var race = new ChallengeTrialDef
        {
            Id = "TR-R", Challenge = "CH41", Course = "C18", Kind = "race", Loaner = new TrialLoaner { Car = "V07" },
            Field = { new TrialFieldCar { Car = "V07" } }, Rules = new TrialRules { Win = true, NoReset = true, NoCarContact = true },
        };
        Assert.True(race.Published, "no targets to measure");
        Assert.True(TrialJudge.Judge(race, Run() with { Placement = 1 }).Passed);
        TrialVerdict second = TrialJudge.Judge(race, Run() with { Placement = 2 });
        Assert.False(second.Passed);
        Assert.Contains("MISSED: first across the line (P2)", second.Summary);
        Assert.False(TrialJudge.Judge(race, Run() with { Placement = 1, CarContacts = 1 }).Passed);
        Assert.False(TrialJudge.Judge(race, Run(resets: 1) with { Placement = 1 }).Passed);

        race.Rules = new TrialRules { ZonePassRole = "pacing" };
        Assert.True(TrialJudge.Judge(race, Run() with { ZonePassRoles = new[] { "field", "pacing" } }).Passed);
        Assert.False(TrialJudge.Judge(race, Run() with { ZonePassRoles = new[] { "field" } }).Passed, "another car passed");
        Assert.False(TrialJudge.Judge(race, Run()).Passed, "no marked pass");

        race.Rules = new TrialRules { PressureSector = true };
        Assert.False(race.Published, "the sector pace is measured first");
        race.Targets = new TrialTargets { SectorTimeMs = 30_000 };
        Assert.True(TrialJudge.Judge(race, Run() with { PressureSectorMs = 29_500 }).Passed);
        Assert.False(TrialJudge.Judge(race, Run() with { PressureSectorMs = 30_500 }).Passed, "too slow");
        Assert.False(TrialJudge.Judge(race, Run()).Passed, "never held under pressure");
        race.Targets = new TrialTargets();

        race.Rules = new TrialRules { CleanZonePass = true, NoCheckpointCut = true };
        Assert.True(TrialJudge.Judge(race, Run() with { Placement = 3, CleanZonePass = true }).Passed);
        Assert.False(TrialJudge.Judge(race, Run() with { Placement = 1 }).Passed, "no marked overtake");
        Assert.False(TrialJudge.Judge(race, Run() with { CleanZonePass = true, CheckpointCut = true }).Passed);
    }

    [Fact]
    public void RacecraftTrialContent_IsChecked()
    {
        var file = new ChallengeTrialsFile();
        file.Trials.Add(new ChallengeTrialDef { Id = "TR-A", Challenge = "CH41", Course = "C04", Kind = "race", Loaner = new TrialLoaner { Car = "V01" }, Rules = new TrialRules { Win = true } });
        file.Trials.Add(new ChallengeTrialDef { Id = "TR-B", Challenge = "CH40", Course = "C04", Kind = "time", Loaner = new TrialLoaner { Car = "V01" }, Field = { new TrialFieldCar { Car = "V01" } } });
        file.Trials.Add(new ChallengeTrialDef { Id = "TR-C", Challenge = "CH34", Course = "C04", Kind = "race", Loaner = new TrialLoaner { Car = "V01" },
            Field = { new TrialFieldCar { Car = "V99", Role = "boss", Pace = 3f } } });
        List<string> p = TrialJudge.Problems(file, id => id == "C04", id => true, id => id == "V01");
        Assert.Contains("TR-A: a racecraft trial needs its fixed field", p);
        Assert.Contains("TR-B: only racecraft trials have a field", p);
        Assert.Contains("TR-C: unknown field car V99", p);
        Assert.Contains("TR-C: unknown field role boss", p);
        Assert.Contains("TR-C: field pace 3 out of range", p);
        Assert.Contains("TR-C: a racecraft trial judges nothing of the race", p);

        var noPacer = new ChallengeTrialsFile();
        noPacer.Trials.Add(new ChallengeTrialDef { Id = "TR-D", Challenge = "CH36", Course = "C04", Kind = "race", Loaner = new TrialLoaner { Car = "V01" },
            Field = { new TrialFieldCar { Car = "V01" } }, Rules = new TrialRules { ZonePassRole = "pacing" } });
        Assert.Contains("TR-D: no pacing car in the field to pass", TrialJudge.Problems(noPacer, id => id == "C04", id => true, id => id == "V01"));
    }

    [Fact]
    public void AnUnpublishedTrial_CannotBePassed()
    {
        TrialVerdict v = TrialJudge.Judge(Trial(), Run());
        Assert.False(v.Passed);
        Assert.Contains("targets not published yet", v.Summary);
    }

    [Fact]
    public void AGroupedChallenge_IsEarnedOnlyWhenEveryTrialOfItsGroupIsPassed()
    {
        var file = new ChallengeTrialsFile
        {
            Trials = { Trial(1, id: "TR-A", challenge: "CH54", group: "G"), Trial(1, id: "TR-B", challenge: "CH54", group: "G"), Trial(1, id: "TR-C") },
        };
        Assert.False(TrialJudge.ChallengeEarned(file, "CH54", new HashSet<string> { "TR-A" }));
        Assert.True(TrialJudge.ChallengeEarned(file, "CH54", new HashSet<string> { "TR-A", "TR-B" }));
        Assert.True(TrialJudge.ChallengeEarned(file, "CH55", new HashSet<string> { "TR-C" }));
        Assert.False(TrialJudge.ChallengeEarned(file, "CH11", new HashSet<string> { "TR-A", "TR-B", "TR-C" }));
    }

    [Fact]
    public void Problems_CatchDuplicatesUnknownIdsAndLooseGroups()
    {
        var file = new ChallengeTrialsFile
        {
            Trials =
            {
                Trial(1, id: "TR-A"), Trial(1, id: "TR-A"),
                Trial(1, id: "TR-B", challenge: "CH99"),
                Trial(1, id: "TR-C", challenge: "CH11"), Trial(1, id: "TR-D", challenge: "CH11"),
            },
        };
        List<string> p = TrialJudge.Problems(file, id => id == "C04", id => id != "CH99", id => id == "V01");
        Assert.Contains(p, x => x.Contains("TR-A is listed 2 times"));
        Assert.Contains(p, x => x.Contains("unknown challenge CH99"));
        Assert.Contains(p, x => x.Contains("CH11 has several trials that are not one group"));
    }

    // ---------------- the Local profile ----------------

    static LocalEventFacts TrialRun(LocalProfile p, string trialId, bool passed, RunOutcome outcome = RunOutcome.Finished)
    {
        ChallengeTrialDef t = Trials.Find(trialId)!;
        LocalEventFacts f = LocalProgressionTests.FreeplayRun(p, t.Course, 0, outcome, EventKind.FreeplayTimeTrial);
        f.CarModelId = t.Loaner.Car;
        f.CarInstanceId = null;
        f.Loaner = true;
        f.TrialId = t.Id;
        f.TrialPassed = passed;
        return f;
    }

    [Fact]
    public void ALocalTrialPass_GrantsItsChallengeOnce_AndAGroupNeedsEveryTrial()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        p = LocalProgressionTests.Apply(p, TrialRun(p, "TR-CH55", passed: true));
        Assert.True(p.HasCompletedChallenge("CH55"));
        Assert.Equal(new[] { "TR-CH55" }, p.TrialsPassed);

        // CH54: two layouts, one group — the first pass alone is kept but earns nothing yet.
        p = LocalProgressionTests.Apply(p, TrialRun(p, "TR-CH54-FWD", passed: true));
        Assert.False(p.HasCompletedChallenge("CH54"));
        p = LocalProgressionTests.Apply(p, TrialRun(p, "TR-CH54-RWD", passed: false));
        Assert.False(p.HasCompletedChallenge("CH54"));
        p = LocalProgressionTests.Apply(p, TrialRun(p, "TR-CH54-RWD", passed: true));
        Assert.True(p.HasCompletedChallenge("CH54"));
        Assert.Equal(new[] { "TR-CH54-FWD", "TR-CH54-RWD", "TR-CH55" }, p.TrialsPassed);
        Assert.Empty(p.Validate());

        // A repeat pays nothing again.
        LocalProgressionResult again = LocalProgression.ApplyEvent(p, Cat, TestContent.Music, TrialRun(p, "TR-CH55", passed: true));
        Assert.Equal(p.Challenges.Count, again.Profile.Challenges.Count);
    }

    [Fact]
    public void ALocalTrialRun_MustBeTheTrialsCourseAndLoaner()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        LocalEventFacts wrongCar = TrialRun(p, "TR-CH11", passed: true);
        wrongCar.CarModelId = "V01";
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, TestContent.Music, wrongCar).Status);
        LocalEventFacts owned = TrialRun(p, "TR-CH55", passed: true);
        owned.Loaner = false;
        owned.CarInstanceId = p.Cars[0].InstanceId;
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, TestContent.Music, owned).Status);
        LocalEventFacts unknown = TrialRun(p, "TR-CH55", passed: true);
        unknown.TrialId = "TR-NOPE";
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, TestContent.Music, unknown).Status);
        LocalEventFacts claimed = LocalProgressionTests.FreeplayRun(p, "C04", 0, kind: EventKind.FreeplayTimeTrial);
        claimed.TrialPassed = true; // a pass without its trial
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.ApplyEvent(p, Cat, TestContent.Music, claimed).Status);
        // A trial not passed, or a run without a valid finish, keeps nothing.
        LocalProfile q = LocalProgressionTests.Apply(p, TrialRun(p, "TR-CH55", passed: true, outcome: RunOutcome.DidNotFinish));
        Assert.Empty(q.TrialsPassed);
        Assert.False(q.HasCompletedChallenge("CH55"));
    }
}
