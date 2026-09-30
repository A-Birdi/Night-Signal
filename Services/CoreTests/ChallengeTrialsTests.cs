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
        Assert.Equal(29, Trials.Trials.Count);
        Assert.Equal(new[] { "CH07", "CH11", "CH13", "CH14", "CH15", "CH23", "CH25", "CH28", "CH30", "CH36", "CH39", "CH40", "CH41", "CH42", "CH43", "CH51", "CH52", "CH53", "CH54", "CH55", "CH58", "CH69", "CH72", "CH74" }, Trials.Trials.Select(t => t.Challenge).Distinct().OrderBy(c => c));
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
        // The challenge cups: three legs each, the first leg's course as the trial's; CH14 untimed with no wall impact at all.
        List<ChallengeTrialDef> cups = Trials.Trials.Where(t => t.IsCup).ToList();
        Assert.Equal(new[] { "CH14", "CH42", "CH69", "CH72" }, cups.Select(t => t.Challenge));
        Assert.All(cups, c => Assert.True(c.Legs.Count == 3 && c.Course == c.Legs[0].Course));
        Assert.True(Trials.Find("TR-CH14")!.Rules.MaxWallImpacts == 0 && Trials.Find("TR-CH14")!.LegFactor == 0 && Trials.Find("TR-CH14")!.Published);
        Assert.True(Trials.Find("TR-CH72")!.Rules.NoReset && Trials.Find("TR-CH72")!.LegFactor == 1.20);
        Assert.Equal(new[] { "C06", "C13", "C21" }, Trials.Find("TR-CH42")!.Legs.Select(l => l.Course));
        // CH43: R32's own practice run in R32's V16 is the Gold reference, with the defence gates.
        ChallengeTrialDef ch43 = Trials.Find("TR-CH43")!;
        Assert.True(ch43.ReferenceRival == "R32" && ch43.ReferenceStage == 28 && ch43.Loaner.Car == "V16" && ch43.Rules.AllDefenceZones && ch43.JudgesTime);
        // CH52 and CH58: section trials on the Driving School's marked sections, in groups; each named gate is on T00's route.
        using var t00 = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Courses", "T00", "route.json")));
        var t00Gates = t00.RootElement.GetProperty("gates").EnumerateArray().ToDictionary(g => g.GetProperty("id").GetString()!, g => g.TryGetProperty("challenge", out var c) ? c.GetString() : "");
        List<ChallengeTrialDef> sections = Trials.Trials.Where(t => t.HasSection).ToList();
        Assert.Equal(new[] { "TR-CH52-LIGHT", "TR-CH52-HEAVY", "TR-CH58-FWD", "TR-CH58-RWD", "TR-CH58-AWD" }, sections.Select(t => t.Id));
        Assert.All(sections, t => Assert.True(t.Course == "T00" && t.Group == t.Challenge && t.JudgesTime
            && t00Gates.TryGetValue(t.SectionStartGate, out string? a) && a == t.Challenge && t00Gates.TryGetValue(t.SectionEndGate, out string? b) && b == t.Challenge, t.Id));
        Assert.Equal(new[] { "FWD", "RWD", "AWD" }, sections.Where(t => t.Challenge == "CH58").Select(t => Cat.Car(t.Loaner.Car).Drive));
        // CH53: two diff setups of one car, each a drill on C03's marked apexes and exits.
        List<ChallengeTrialDef> diffs = Trials.Trials.Where(t => t.Challenge == "CH53").ToList();
        Assert.Equal(2, diffs.Count);
        Assert.All(diffs, t => Assert.True(t.IsDrill && t.Group == "CH53" && t.Rules.AllChallengeGates && t.Rules.ChallengeExits && t.Loaner.Car == "V05"
            && t.Targets.ExitFloors.Select(x => x.Gate).SequenceEqual(new[] { "C03-DIFF-EXIT-1", "C03-DIFF-EXIT-2" })));
        Assert.NotEqual(diffs[0].Loaner.Parts.GetValueOrDefault("differential"), diffs[1].Loaner.Parts.GetValueOrDefault("differential"));
        // CH74: the six story records first, then the C24 rival reference's own time in its own car.
        ChallengeTrialDef ch74 = Trials.Find("TR-CH74")!;
        using var c24 = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Resources", "RivalGhosts", "C24.json")));
        System.Text.Json.JsonElement h = c24.RootElement.GetProperty("header");
        Assert.True(ch74.RequiredStoryRecords == 6 && ch74.RaceRivalReference && ch74.Course == "C24");
        Assert.Equal(h.GetProperty("resultMicros").GetInt64() / 1000, ch74.Targets.TimeMs);
        Assert.Equal(h.GetProperty("carModelId").GetString(), ch74.Loaner.Car);
        ChallengeTrialDef ch23 = Trials.Find("TR-CH23")!;
        Assert.True(ch23.IsDrill && ch23.Course == "T00" && ch23.Rules.AlternatingRecoveries && Cat.Car(ch23.Loaner.Car).Drive == "RWD");
        ChallengeTrialDef ch07 = Trials.Find("TR-CH07")!;
        Assert.True(ch07.IsDrill && ch07.Course == "T00" && ch07.Loaner.Car == "V01" && ch07.Rules.NoHandbrake && ch07.Rules.BrakeEnvelope
            && ch07.Targets.Brakes.Single().Gate == "T00-TRAIL-BRAKE" && t00Gates["T00-TRAIL-BRAKE"] == "CH07");
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

        var timed = new ChallengeTrialDef { Id = "TR-S", Challenge = "CH58", Course = "T00", Kind = "time", Loaner = new TrialLoaner { Car = "V06" },
            SectionStartGate = "T00-CFG1-START", SectionEndGate = "T00-CFG1-END", Targets = new TrialTargets { TimeMs = 30_000 } };
        Assert.True(TrialJudge.Judge(timed, Run(timeMs: 200_000) with { SectionMs = 29_000 }).Passed, "the section's time, not the finish");
        Assert.False(TrialJudge.Judge(timed, Run(timeMs: 20_000) with { SectionMs = 31_000 }).Passed);
        Assert.Contains("T00-CFG1-START to T00-CFG1-END faster than 0:30.000 (not driven)", TrialJudge.Judge(timed, Run()).Summary);

        var drill = new ChallengeTrialDef { Id = "TR-D", Challenge = "CH53", Course = "C03", Kind = "drill", Loaner = new TrialLoaner { Car = "V05" },
            Rules = new TrialRules { AllChallengeGates = true, ChallengeExits = true },
            Targets = new TrialTargets { ExitFloors = { new TrialExitFloor { Gate = "E1", Kmh = 80f }, new TrialExitFloor { Gate = "E2", Kmh = 90f } } } };
        TrialRunFacts Exits(float e1, float e2) => Run() with { ChallengeGates = 2, ChallengeGatesTouched = true, ExitGates = new[] { "E1", "E2" }, ExitKmh = new[] { e1, e2 } };
        Assert.True(drill.Published);
        Assert.True(TrialJudge.Judge(drill, Exits(81f, 95f)).Passed, "no time target: the gates and exits are the drill");
        Assert.False(TrialJudge.Judge(drill, Exits(79f, 95f)).Passed);
        Assert.False(TrialJudge.Judge(drill, Exits(81f, 95f) with { ChallengeGatesTouched = false }).Passed, "an apex missed");
        Assert.Contains("MISSED: E2 exit at least 90.0 km/h (not crossed)", TrialJudge.Judge(drill, Run() with { ChallengeGates = 2, ChallengeGatesTouched = true, ExitGates = new[] { "E1" }, ExitKmh = new[] { 85f } }).Summary);
        drill.Targets.ExitFloors[0].Kmh = 0f;
        Assert.False(drill.Published, "the floors are measured first");

        var trail = new ChallengeTrialDef { Id = "TR-B", Challenge = "CH07", Course = "T00", Kind = "drill", Loaner = new TrialLoaner { Car = "V01" },
            Rules = new TrialRules { BrakeEnvelope = true, NoHandbrake = true },
            Targets = new TrialTargets { Brakes = { new TrialBrakeEnvelope { Gate = "Z", BrakeByMetres = 1530, ReleaseAfterMetres = 1590, MinExitKmh = 50, MaxExitKmh = 70 } } } };
        TrialRunFacts Brake(float on, float release, float exit, bool braked = true) => Run() with
        {
            BrakeGates = new[] { "Z" }, BrakeFacts = new[] { new GateSpeedFact { Crossed = true, Braked = braked, BrakeOnMetres = on, ReleaseMetres = release, ExitKmh = exit } },
        };
        Assert.True(trail.Published);
        Assert.True(TrialJudge.Judge(trail, Brake(1520, 1600, 60)).Passed);
        Assert.True(TrialJudge.Judge(trail, Brake(1520, -1, 60)).Passed, "still braking at the zone's end is trailed");
        Assert.False(TrialJudge.Judge(trail, Brake(1540, 1600, 60)).Passed, "braked too late");
        Assert.False(TrialJudge.Judge(trail, Brake(1520, 1560, 60)).Passed, "released too early: not trailed");
        Assert.False(TrialJudge.Judge(trail, Brake(1520, 1600, 75)).Passed, "exit too fast");
        Assert.False(TrialJudge.Judge(trail, Brake(1520, 1600, 60) with { HandbrakeSeconds = 0.5f }).Passed, "the handbrake");
        Assert.False(TrialJudge.Judge(trail, Run()).Passed, "never crossed");

        var story = new ChallengeTrialDef { Id = "TR-R", Challenge = "CH74", Course = "C24", Kind = "time", Loaner = new TrialLoaner { Car = "V14" },
            RequiredStoryRecords = 6, Targets = new TrialTargets { TimeMs = 240_000 } };
        Assert.True(TrialJudge.Judge(story, Run(timeMs: 239_000) with { StoryRecords = 6 }).Passed);
        TrialVerdict locked = TrialJudge.Judge(story, Run(timeMs: 200_000) with { StoryRecords = 5 });
        Assert.False(locked.Passed);
        Assert.Contains("MISSED: the 6 story records collected through Normal progression (5 of 6)", locked.Summary);

        var defended = new ChallengeTrialDef { Id = "TR-G", Challenge = "CH43", Course = "C20", Kind = "time", Loaner = new TrialLoaner { Car = "V16" },
            Rules = new TrialRules { AllDefenceZones = true }, Targets = new TrialTargets { TimeMs = 100_000 } };
        Assert.True(TrialJudge.Judge(defended, Run(timeMs: 99_000) with { DefenceZones = 2, DefenceZonesKept = true }).Passed);
        Assert.False(TrialJudge.Judge(defended, Run(timeMs: 99_000) with { DefenceZones = 2, DefenceZonesKept = false }).Passed, "out of the corridor in a gate");
        Assert.False(TrialJudge.Judge(defended, Run(timeMs: 99_000) with { DefenceZones = 0, DefenceZonesKept = true }).Passed, "a course without them");

        race.Rules = new TrialRules { CleanZonePass = true, NoCheckpointCut = true };
        Assert.True(TrialJudge.Judge(race, Run() with { Placement = 3, CleanZonePass = true }).Passed);
        Assert.False(TrialJudge.Judge(race, Run() with { Placement = 1 }).Passed, "no marked overtake");
        Assert.False(TrialJudge.Judge(race, Run() with { CleanZonePass = true, CheckpointCut = true }).Passed);
    }

    static TrialCupLegFacts Leg(string course, long ms = 60_000, int walls = 0, int resets = 0, bool finished = true) =>
        new() { Course = course, Finished = finished, TimeMs = finished ? ms : 0, WallImpacts = walls, Resets = resets };

    static TrialRunFacts CupRun(params TrialCupLegFacts[] legs) => new() { DroveLoaner = true, Finished = legs.Length > 0 && legs[^1].Finished, CupLegs = legs };

    [Fact]
    public void AChallengeCup_IsJudgedAsAWhole()
    {
        var cup = new ChallengeTrialDef
        {
            Id = "TR-K", Challenge = "CH72", Course = "C21", Kind = "cup", Loaner = new TrialLoaner { Car = "V12" }, LegFactor = 1.2,
            Legs = { new TrialCupLeg { Course = "C21", TimeMs = 70_000 }, new TrialCupLeg { Course = "C22", TimeMs = 80_000 }, new TrialCupLeg { Course = "C23", TimeMs = 90_000 } },
            Rules = new TrialRules { NoReset = true },
        };
        Assert.True(TrialJudge.Judge(cup, CupRun(Leg("C21"), Leg("C22"), Leg("C23"))).Passed);
        Assert.False(TrialJudge.Judge(cup, CupRun(Leg("C21"), Leg("C22"))).Passed, "two legs of three");
        Assert.False(TrialJudge.Judge(cup, CupRun(Leg("C22"), Leg("C21"), Leg("C23"))).Passed, "out of order");
        TrialVerdict slow = TrialJudge.Judge(cup, CupRun(Leg("C21"), Leg("C22", 85_000), Leg("C23")));
        Assert.False(slow.Passed);
        Assert.Contains("MISSED: C22 faster than 1:20.000 (1:25.000)", slow.Summary);
        Assert.False(TrialJudge.Judge(cup, CupRun(Leg("C21"), Leg("C22", resets: 1), Leg("C23"))).Passed, "a reset in any leg");
        Assert.False(TrialJudge.Judge(cup, CupRun(Leg("C21"), Leg("C22", finished: false))).Passed, "a leg not finished ends the cup");

        cup.Rules = new TrialRules { MaxWallImpacts = 0 };
        cup.LegFactor = 0;
        foreach (TrialCupLeg l in cup.Legs) l.TimeMs = 0;
        Assert.True(cup.Published, "untimed legs");
        Assert.True(TrialJudge.Judge(cup, CupRun(Leg("C21", 999_000), Leg("C22"), Leg("C23"))).Passed);
        Assert.False(TrialJudge.Judge(cup, CupRun(Leg("C21"), Leg("C22", walls: 1), Leg("C23"))).Passed, "one wall impact across the cup");

        cup.LegFactor = 1.2;
        Assert.False(cup.Published, "leg times are measured first");
        TrialVerdict leg1 = TrialJudge.JudgeCupLeg(cup, 0, Leg("C21"));
        Assert.False(leg1.Passed, "a leg never passes the trial alone");
        Assert.Contains("ok: leg 1 of 3 (C21) finished", leg1.Summary);
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

        var cupFile = new ChallengeTrialsFile();
        cupFile.Trials.Add(new ChallengeTrialDef { Id = "TR-E", Challenge = "CH14", Course = "C04", Kind = "cup", Loaner = new TrialLoaner { Car = "V01" },
            Legs = { new TrialCupLeg { Course = "C04" }, new TrialCupLeg { Course = "C99" } } });
        cupFile.Trials.Add(new ChallengeTrialDef { Id = "TR-F", Challenge = "CH69", Course = "C04", Kind = "time", Loaner = new TrialLoaner { Car = "V01" },
            Legs = { new TrialCupLeg { Course = "C04" } } });
        List<string> cp = TrialJudge.Problems(cupFile, id => id == "C04", id => true, id => id == "V01");
        Assert.Contains("TR-E: a challenge cup has 3 legs", cp);
        Assert.Contains("TR-E: unknown leg course C99", cp);
        Assert.Contains("TR-F: only challenge cups have legs", cp);
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
