using NightSignal.Core.Profiles;
using NightSignal.Core.Tutorial;

namespace NightSignal.CoreTests;

/// <summary>Tutorial T00 lessons (spec §16): the authored document, the help index, the drive-lesson judge and progress.</summary>
public sealed class TutorialLessonsTests
{
    static TutorialLessons Lessons() =>
        TutorialLessons.Parse(File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", "tutorial", "lessons.json")));

    [Fact]
    public void TheAuthoredLessons_CoverTheSpecTopics_AndParse()
    {
        TutorialLessons t = Lessons();
        Assert.Equal("T00", t.Course);
        Assert.Equal(8, t.Lessons.Count(l => l.IsDrive));
        Assert.Equal(6, t.Lessons.Count(l => l.Kind == "read"));
        // Spec §16's topics: controls, camera, braking, turn-in, grip vs drift, countersteering, exits/gearing, resets,
        // parts/stats, ghost deltas, raw vs showcase, convoy readiness, public meets, connectivity/DQ, rewards/rank.
        foreach (string topic in new[] { "camera", "braking", "turn-in", "drift", "countersteer", "gear", "reset", "parts", "ghost", "showcase", "convoy", "meet", "disconnect", "rank" })
            Assert.True(t.Search(topic).Count > 0, topic);
        Assert.Empty(t.Problems());
    }

    [Fact]
    public void TheHelpIndex_FindsByKeywordAndText()
    {
        TutorialLessons t = Lessons();
        Assert.Contains(t.Search("opposite lock"), l => l.Id == "countersteer");
        Assert.Contains(t.Search("BRAKE"), l => l.Id == "braking");
        Assert.Equal(t.Lessons.Count, t.Search("").Count);
        Assert.Empty(t.Search("zzzz-not-a-topic"));
    }

    [Fact]
    public void BrokenDocuments_AreRefused()
    {
        Assert.Throws<FormatException>(() => TutorialLessons.Parse("""{"schema":"x","lessons":[]}"""));
        Assert.Throws<FormatException>(() => TutorialLessons.Parse("""{"schema":"night-signal/tutorial-lessons@1","lessons":[{"id":"a","kind":"drive","title":"A","goal":"g","help":"h","check":{"type":"fly"}}]}"""));
        Assert.Throws<FormatException>(() => TutorialLessons.Parse("""{"schema":"night-signal/tutorial-lessons@1","lessons":[{"id":"a","kind":"read","title":"A","goal":"g","help":"h","question":"q","choices":["x"],"answer":3}]}"""));
    }

    static LessonTick T(float m, float kmh = 60f, float brake = 0f, int walls = 0, int resets = 0, int cams = 0, double drift = 0, float slip = 0f, float s = 0f, bool finished = false, bool spun = false) =>
        new() { Metres = m, SpeedKmh = kmh, Brake = brake, WallIncidents = walls, Resets = resets, CameraChanges = cams, DriftRaw = drift, SlipDeg = slip, Seconds = s, Finished = finished, Spun = spun };

    [Fact]
    public void Braking_PassesOnADropInsideTheZone_FailsWithoutSpeedOrOnAWall()
    {
        TutorialLesson braking = Lessons().Find("braking")!;
        var ok = new LessonJudge(braking);
        ok.Tick(T(1400, 80));
        ok.Tick(T(1511, 82));
        ok.Tick(T(1520, 75, brake: 0.9f));
        Assert.Equal(LessonStatus.Passed, ok.Tick(T(1540, 55, brake: 0.9f)));
        var slow = new LessonJudge(braking);
        slow.Tick(T(1500, 50));
        Assert.Equal(LessonStatus.NotYet, slow.Tick(T(1513, 50)));
        var wall = new LessonJudge(braking);
        wall.Tick(T(1500, 85));
        Assert.Equal(LessonStatus.NotYet, wall.Tick(T(1520, 80, brake: 1f, walls: 1)));
        Assert.Contains("wall", wall.Feedback);
    }

    [Fact]
    public void Reach_NeedsTheCamera_Bend_NeedsExitSpeed_Timed_NeedsTheClock()
    {
        TutorialLessons t = Lessons();
        var reach = new LessonJudge(t.Find("controls-camera")!);
        Assert.Equal(LessonStatus.NotYet, reach.Tick(T(301, cams: 0)));
        Assert.Equal(LessonStatus.Passed, new LessonJudge(t.Find("controls-camera")!).Tick(T(301, cams: 1)));
        var bend = new LessonJudge(t.Find("turn-in-exit")!);
        bend.Tick(T(600));
        bend.Tick(T(700, 45));
        Assert.Equal(LessonStatus.Passed, bend.Tick(T(901, 48)));
        var tooSlow = new LessonJudge(t.Find("turn-in-exit")!);
        tooSlow.Tick(T(700, 45));
        Assert.Equal(LessonStatus.NotYet, tooSlow.Tick(T(901, 30)));
        var timed = new LessonJudge(t.Find("exits-gearing")!);
        timed.Tick(T(970, s: 40f));
        Assert.Equal(LessonStatus.Passed, timed.Tick(T(1483, s: 58f))); // 18 s
        var late = new LessonJudge(t.Find("exits-gearing")!);
        late.Tick(T(970, s: 40f));
        Assert.Equal(LessonStatus.NotYet, late.Tick(T(1300, s: 61f))); // over 20 s
    }

    [Fact]
    public void Drift_Catch_Reset_And_Lap()
    {
        TutorialLessons t = Lessons();
        var drift = new LessonJudge(t.Find("grip-drift")!);
        drift.Tick(T(600, drift: 1000));
        drift.Tick(T(700, drift: 1100));
        Assert.Equal(LessonStatus.Passed, drift.Tick(T(750, drift: 1320))); // 320 raw gained inside the zone
        var none = new LessonJudge(t.Find("grip-drift")!);
        none.Tick(T(700, drift: 0));
        Assert.Equal(LessonStatus.NotYet, none.Tick(T(930, drift: 100)));

        var catchIt = new LessonJudge(t.Find("countersteer")!);
        catchIt.Tick(T(650, slip: 5));
        catchIt.Tick(T(660, slip: 20));
        Assert.Equal(LessonStatus.Passed, catchIt.Tick(T(670, slip: 3)));
        var spin = new LessonJudge(t.Find("countersteer")!);
        spin.Tick(T(660, slip: 20));
        Assert.Equal(LessonStatus.NotYet, spin.Tick(T(670, slip: 90, spun: true)));

        var reset = new LessonJudge(t.Find("resets")!);
        reset.Tick(T(400, resets: 0));
        reset.Tick(T(350, resets: 1)); // put back behind
        Assert.Equal(LessonStatus.InProgress, reset.Tick(T(420, resets: 1)));
        Assert.Equal(LessonStatus.Passed, reset.Tick(T(451, resets: 1)));

        var lap = new LessonJudge(t.Find("ghost-deltas")!);
        Assert.Equal(LessonStatus.InProgress, lap.Tick(T(1000)));
        Assert.Equal(LessonStatus.Passed, lap.Tick(T(1793, finished: true)));
    }

    [Fact]
    public void Cards_AnswerWithExplanation_AndProgressIsKeptOnceWithoutReward()
    {
        TutorialLessons t = Lessons();
        TutorialLesson card = t.Find("rewards-rank")!;
        Assert.False(LessonJudge.AnswerCard(card, 0, out string wrong));
        Assert.StartsWith("Not quite.", wrong);
        Assert.True(LessonJudge.AnswerCard(card, card.Answer, out string right));
        Assert.StartsWith("Right.", right);

        LocalProfile p = LocalProgressionTests.NewProfile();
        long wallet = p.WalletBalance;
        LocalProgressionResult r = LocalProgression.MarkLessonPassed(p, t, "braking");
        Assert.Equal(LocalOperationStatus.Applied, r.Status);
        Assert.Equal(new[] { "braking" }, r.Profile.Tutorial.LessonsPassed);
        Assert.Equal(wallet, r.Profile.WalletBalance); // training only: no money
        Assert.Equal(LocalOperationStatus.AlreadyApplied, LocalProgression.MarkLessonPassed(r.Profile, t, "braking").Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.MarkLessonPassed(r.Profile, t, "flying").Status);
        Assert.Empty(r.Profile.Validate());
    }
}
