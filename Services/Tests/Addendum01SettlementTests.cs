using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Settlement inputs under Addendum 01: placements 1–12 with AI never receiving transactions, pure-PvP exclusions, the
/// live featured-rival condition on encounter stages, Team Trial scoring/payability, course and soundtrack grants.
/// </summary>
public sealed class Addendum01SettlementTests
{
    static string H(int i) => $"00000000-0000-4000-8000-{i:000000000000}";

    static readonly MusicUnlockManifest Music = MusicUnlockManifest.Parse("""
        {"schema":"night-signal/music-unlocks@1","cues":[
          {"cueId":"menu-main","source":{"kind":"baseline"}},
          {"cueId":"region-kiyose","source":{"kind":"stage-first-normal-clear","stageId":"S05"}},
          {"cueId":"lt-kagero","source":{"kind":"lieutenant-first-defeat","stageId":"S07"}},
          {"cueId":"final-hard","source":{"kind":"stage-first-hard-clear","stageId":"S30"}},
          {"cueId":"trial-mean","source":{"kind":"trial-first-victory","trialId":"TT_MEAN"}}
        ]}
        """, TestData.Content.Catalogue, TeamTrialCatalog.Fixture(TestData.Content.Catalogue));

    readonly SettlementService service = new(null!, null!, TestData.Content, null!, null!, null!, Music);

    static AssignedEntrant Entrant(string id) => new(id, id, "racer", "V01", 220, "p", "c", 1);

    static MatchAssignment Freeplay(string mode, int humans, int ai, bool purePvP = false, IReadOnlyList<GuestPass>? passes = null) => new()
    {
        MatchId = "m_fp", ConvoyId = "cv", ServerId = "srv", Kind = "freeplay", CourseId = mode == "time-attack" ? "C03" : "C01", FreeplayMode = mode,
        Weather = "stage-default", Collision = mode == "time-attack" ? "non-contact" : "light-contact", CarCapPi = 999,
        Entrants = Enumerable.Range(1, humans).Select(i => Entrant(H(i))).ToList(),
        AiEntrants = Enumerable.Range(1, ai).Select(i => $"ai-{i}").ToList(),
        PurePvP = purePvP, GuestPasses = passes ?? Array.Empty<GuestPass>(),
        Build = "b", Protocol = 1, ContentHash = "c", Seed = 1, ResultsUrl = "", TicketIssuer = "i", TicketAudience = "a",
    };

    static MatchAssignment Campaign(string stageId, string mode, int humans, bool requiresRival = false)
    {
        StageDef stage = TestData.Content.Catalogue.Stage(stageId);
        List<string> opponents = (mode == "hard" ? stage.Hard : stage.Normal).Opponents;
        return new MatchAssignment
        {
            MatchId = "m_c", ConvoyId = "cv", ServerId = "srv", Kind = "campaign", Mode = mode, StageId = stageId, StageNumber = stage.Number,
            StageType = stage.Type, CourseId = stage.Course, Weather = "stage-default", Collision = "light-contact", CarCapPi = stage.MaxPI,
            Entrants = Enumerable.Range(1, humans).Select(i => Entrant(H(i))).ToList(), AiEntrants = opponents, FeaturedRival = opponents[0],
            Benchmark = new AssignedBenchmark("Time", 300_000, 0, 600_000, true, "test", requiresRival),
            Build = "b", Protocol = 1, ContentHash = "c", Seed = 1, ResultsUrl = "", TicketIssuer = "i", TicketAudience = "a",
        };
    }

    static MatchAssignment Trial(string kind, int humans)
    {
        var roster = new List<RosterSlot>();
        for (int i = 1; i <= humans; i++) roster.Add(new RosterSlot(H(i), "human", "player", "driver", H(i)));
        for (int i = 1; i <= 6 - humans; i++) roster.Add(new RosterSlot($"ally-{i}", "ai", "player", "friendly-ai", $"R0{i}"));
        for (int i = 1; i <= 6; i++) roster.Add(new RosterSlot($"opp-{i}", "ai", "opposing", "opposing-ai", $"R1{i}"));
        return new MatchAssignment
        {
            MatchId = "m_t", ConvoyId = "cv", ServerId = "srv", Kind = "trial", CourseId = kind == "drift" ? "C02" : "C03",
            FreeplayMode = kind == "drift" ? "drift-attack" : "circuit", Weather = "stage-default", Collision = "light-contact", CarCapPi = 999,
            Entrants = Enumerable.Range(1, humans).Select(i => Entrant(H(i))).ToList(),
            AiEntrants = roster.Where(r => r.Kind == "ai").Select(r => r.EntrantId).ToList(), Roster = roster,
            Trial = new TrialAssignment("TT_" + kind.ToUpperInvariant(), kind, "standard", 480_000, 345_000, 1, 4, "tie-is-not-victory", true),
            Build = "b", Protocol = 1, ContentHash = "c", Seed = 1, ResultsUrl = "", TicketIssuer = "i", TicketAudience = "a",
        };
    }

    static EntrantFacts F(string id, bool human, RunOutcome outcome, long ms, int place, long drift = 0, bool? started = null) => new()
    {
        EntrantId = id, Human = human, Outcome = outcome, FinishTimeMicros = outcome == RunOutcome.Finished ? ms * 1000 : 0, Placement = place,
        CheckpointFraction = outcome == RunOutcome.Finished ? 1 : 0.5, ActiveProgressVerified = true, ActivelyDroveLegalCourse = true,
        LegalProgressMetres = outcome == RunOutcome.Finished ? 3000 : 1500, RawDriftScore = drift, Started = started,
    };

    (MatchSettlement? S, string? Error) Compute(MatchAssignment m, IEnumerable<EntrantFacts> facts) =>
        service.Compute(m, new ResultSubmission { MatchId = m.MatchId, ContentHash = "c", Entrants = facts.ToList() }, "h");

    static EntrantSettlement For(MatchSettlement s, string id) => s.Entrants.Single(e => e.AccountId == id);

    [Fact]
    public void FourthToTwelfthPlaces_PayTheParticipationModifier_AndAiNeverGetReceipts()
    {
        // 3 humans + 9 AI = 12 vehicles; humans place 4th, 8th and 12th.
        var facts = new List<EntrantFacts>();
        int place = 0;
        int ai = 0;
        for (int slot = 1; slot <= 12; slot++)
        {
            place++;
            bool human = slot is 4 or 8 or 12;
            string id = human ? H(slot / 4) : $"ai-{++ai}";
            facts.Add(F(id, human, RunOutcome.Finished, 100_000 + slot * 1000, place));
        }
        (MatchSettlement? s, string? error) = Compute(Freeplay("sprint", 3, 9), facts);
        Assert.Null(error);
        Assert.Equal(3, s!.Entrants.Count); // AI get no transactions
        Assert.Equal(new[] { 4, 8, 12 }, s.Entrants.Select(e => e.Facts.Placement).OrderBy(x => x));
        Assert.All(s.Entrants, e => Assert.Equal(100, Economy.Compute(e.Facts).PlacementX100));
        Assert.All(s.Entrants, e => Assert.False(e.Facts.PvPWinnerBonusEligible));
    }

    [Fact]
    public void PurePvPBonus_NeverAppliesToTimeAttack_EvenWithAForgedFlag()
    {
        MatchAssignment m = Freeplay("time-attack", 2, 0, purePvP: true);
        (MatchSettlement? s, _) = Compute(m, new[] { F(H(1), true, RunOutcome.Finished, 170_000, 1), F(H(2), true, RunOutcome.Finished, 180_000, 2) });
        Assert.False(For(s!, H(1)).Facts.PvPWinnerBonusEligible);
        Assert.Equal(100, Economy.Compute(For(s!, H(1)).Facts).PvPX100);
    }

    [Fact]
    public void GuestPasses_AreRecordedOnReceipts()
    {
        MatchAssignment m = Freeplay("sprint", 2, 0, passes: new[] { new GuestPass(H(2), "C01", H(1)) });
        (MatchSettlement? s, _) = Compute(m, new[] { F(H(1), true, RunOutcome.Finished, 170_000, 1), F(H(2), true, RunOutcome.Finished, 180_000, 2) });
        Assert.Null(For(s!, H(1)).Receipt.GuestPass);
        Assert.Equal((H(1), "C01"), (For(s!, H(2)).Receipt.GuestPass!.SponsorId, For(s!, H(2)).Receipt.GuestPass!.CourseId));
    }

    /// <summary>Assigns placements exactly as the game server must (Core rules: 1 ms ties share; DNFs by progress; DQ unplaced).</summary>
    static List<EntrantFacts> Placed(List<EntrantFacts> facts, bool drift = false)
    {
        var finishers = facts.Where(f => f.Outcome == RunOutcome.Finished).ToList();
        foreach (EntrantFacts f in finishers)
            f.Placement = 1 + finishers.Count(o => drift ? o.RawDriftScore > f.RawDriftScore : o.FinishTimeMicros / 1000 < f.FinishTimeMicros / 1000);
        int next = finishers.Count;
        foreach (EntrantFacts dnf in facts.Where(f => f.Outcome == RunOutcome.DidNotFinish).OrderByDescending(f => f.LegalProgressMetres)) dnf.Placement = ++next;
        foreach (EntrantFacts other in facts.Where(f => f.Outcome is not (RunOutcome.Finished or RunOutcome.DidNotFinish))) other.Placement = 0;
        return facts;
    }

    static List<EntrantFacts> Encounter(long humanMs, RunOutcome rivalOutcome, long rivalMs) => Placed(new List<EntrantFacts>
    {
        F(H(1), true, RunOutcome.Finished, humanMs, 0),
        F("R08", false, rivalOutcome, rivalMs, 0),
        F("R05", false, RunOutcome.Finished, 400_000, 0),
        F("R06", false, RunOutcome.Finished, 410_000, 0),
    });

    [Fact]
    public void LieutenantEncounter_MeetingTheTargetIsNotEnough_TheHumanMustBeatTheLiveRival()
    {
        MatchAssignment m = Campaign("S07", "normal", 1, requiresRival: true);
        Assert.Equal("R08", m.FeaturedRival);

        (MatchSettlement? lost, string? e1) = Compute(m, Encounter(250_000, RunOutcome.Finished, 240_000));
        Assert.Null(e1);
        Assert.False(For(lost!, H(1)).Receipt.Stage!.BeatFeaturedRival);
        Assert.False(For(lost!, H(1)).Receipt.Stage!.Qualified);
        Assert.Null(For(lost!, H(1)).Clear);

        (MatchSettlement? won, _) = Compute(m, Encounter(230_000, RunOutcome.Finished, 240_000));
        Assert.True(For(won!, H(1)).Receipt.Stage!.BeatFeaturedRival);
        Assert.NotNull(For(won!, H(1)).Clear);
        Assert.Contains(For(won!, H(1)).ClearMusicGrants, g => g.CueId == "lt-kagero");
        Assert.Empty(For(won!, H(1)).ClearCourseGrants); // a reused lieutenant stage never unlocks a course

        (MatchSettlement? tie, _) = Compute(m, Encounter(240_000, RunOutcome.Finished, 240_000));
        Assert.False(For(tie!, H(1)).Receipt.Stage!.BeatFeaturedRival); // a tie does not beat the rival

        (MatchSettlement? dnf, _) = Compute(m, Encounter(280_000, RunOutcome.DidNotFinish, 0));
        Assert.True(For(dnf!, H(1)).Receipt.Stage!.BeatFeaturedRival); // a rival that legally fails to finish is beaten by a valid finisher
        Assert.NotNull(For(dnf!, H(1)).Clear);

        (MatchSettlement? slow, _) = Compute(m, Encounter(310_000, RunOutcome.DidNotFinish, 0));
        Assert.Null(For(slow!, H(1)).Clear); // …but the human still needs the published target
    }

    [Fact]
    public void AFeaturedRivalThatNeverStarted_IsABrokenEvent_NotAFreeWin()
    {
        MatchAssignment m = Campaign("S07", "normal", 1, requiresRival: true);
        var facts = Encounter(230_000, RunOutcome.DidNotFinish, 0);
        facts[1].Started = false;
        var submission = new ResultSubmission { MatchId = m.MatchId, ContentHash = "c", Entrants = facts };
        Assert.Contains("featured rival", SettlementService.BrokenEventReason(m, submission));
        Assert.NotNull(Compute(m, facts).Error);
        facts[0].Started = true;
        facts[1].Started = true;
        Assert.NotNull(Compute(m, facts).Error); // "started" is an AI-only fact
    }

    [Fact]
    public void HardEncounter_NeedsCeilHalfTheHumans_EachBeatingTheRival()
    {
        MatchAssignment m = Campaign("S07", "hard", 4, requiresRival: true);
        string rival = m.FeaturedRival!;
        List<string> ai = m.AiEntrants.ToList();
        IEnumerable<EntrantFacts> Race(int humansAhead)
        {
            int place = 0;
            for (int i = 1; i <= humansAhead; i++) yield return F(H(i), true, RunOutcome.Finished, 200_000 + i * 1000, ++place);
            yield return F(rival, false, RunOutcome.Finished, 210_000, ++place);
            for (int i = humansAhead + 1; i <= 4; i++) yield return F(H(i), true, RunOutcome.Finished, 220_000 + i * 1000, ++place);
            foreach (string support in ai.Skip(1)) yield return F(support, false, RunOutcome.Finished, 400_000 + place * 1000, ++place);
        }
        (MatchSettlement? one, _) = Compute(m, Race(1));
        Assert.False(For(one!, H(1)).Receipt.Stage!.TeamSuccess); // Hard with H = 4 needs two qualifiers
        (MatchSettlement? two, _) = Compute(m, Race(2));
        Assert.True(For(two!, H(1)).Receipt.Stage!.TeamSuccess);
        Assert.Equal(2, For(two!, H(1)).Receipt.Stage!.RequiredQualifiers);
        Assert.Empty(For(two!, H(1)).ClearCourseGrants); // Hard clears grant no course access
    }

    [Fact]
    public void NormalRegularClear_CarriesTheCourseUnlockAndStageMusic()
    {
        MatchAssignment m = Campaign("S05", "normal", 1);
        var facts = new[] { F(H(1), true, RunOutcome.Finished, 200_000, 1), F(m.AiEntrants[0], false, RunOutcome.Finished, 210_000, 2) };
        (MatchSettlement? s, string? error) = Compute(m, facts);
        Assert.Null(error);
        Assert.Equal(new[] { "C05" }, For(s!, H(1)).ClearCourseGrants);
        Assert.Equal(new[] { "region-kiyose" }, For(s!, H(1)).ClearMusicGrants.Select(g => g.CueId));
        Assert.Empty(For(s!, H(1)).MusicGrants);
    }

    static List<EntrantFacts> TrialRace(int humans, Func<string, (RunOutcome Outcome, long Ms, long Drift)> result, bool drift = false)
    {
        var facts = new List<EntrantFacts>();
        foreach (string id in Enumerable.Range(1, humans).Select(H).Concat(Enumerable.Range(1, 6 - humans).Select(i => $"ally-{i}"))
                     .Concat(Enumerable.Range(1, 6).Select(i => $"opp-{i}")))
        {
            (RunOutcome o, long ms, long d) = result(id);
            facts.Add(F(id, id.StartsWith("0000"), o, ms, 0, d));
        }
        return Placed(facts, drift);
    }

    [Fact]
    public void TeamMean_CountsEverySeat_ADnfIsPenalised_AndDroppingASlowTeammateCannotHelp()
    {
        MatchAssignment m = Trial("mean", 2);
        (RunOutcome, long, long) Base(string id) => id.StartsWith("opp") ? (RunOutcome.Finished, 325_000, 0) : (RunOutcome.Finished, 290_000, 0);

        (MatchSettlement? slowAlly, string? e1) = Compute(m, TrialRace(2, id => id == "ally-4" ? (RunOutcome.Finished, 470_000, 0) : Base(id)));
        (MatchSettlement? droppedAlly, _) = Compute(m, TrialRace(2, id => id == "ally-4" ? (RunOutcome.DidNotFinish, 0, 0) : Base(id)));
        Assert.Null(e1);
        TeamTrialReceipt slow = For(slowAlly!, H(1)).Receipt.TeamTrial!;
        TeamTrialReceipt dropped = For(droppedAlly!, H(1)).Receipt.TeamTrial!;
        Assert.Equal(5 * 290_000L + 470_000, slow.PlayerTeamValue);
        Assert.Equal(5 * 290_000L + 480_000 + Limits.TeamTrialFailurePenaltyMs, dropped.PlayerTeamValue); // hard timeout + 30 s
        Assert.True(dropped.PlayerTeamValue > slow.PlayerTeamValue); // losing the slow teammate never helps
        Assert.Equal(12, slow.Contributions.Count);

        // Team verdict pays the declared bounded modifier, not six (or twelve) payouts per human.
        Assert.Equal("victory", slow.Verdict);
        Assert.Equal(2, slowAlly!.Entrants.Count);
        Assert.All(slowAlly.Entrants, e => Assert.Equal(135, Economy.Compute(e.Facts).PlacementX100));
        Assert.All(slowAlly.Entrants, e => Assert.Null(e.PayoutWithheld));
        Assert.All(slowAlly.Entrants, e => Assert.Null(e.Clear)); // no stage clear → no RP from a trial
        Assert.Contains(For(slowAlly!, H(1)).MusicGrants, g => g.CueId == "trial-mean");
        Assert.Equal("team", slow.RecordCategory);
        Assert.NotNull(For(slowAlly!, H(1)).TeamBest);

        Assert.Equal("defeat", dropped.Verdict);
        Assert.All(droppedAlly!.Entrants, e => Assert.Equal(100, Economy.Compute(e.Facts).PlacementX100));
        Assert.Empty(For(droppedAlly!, H(1)).MusicGrants);
    }

    [Fact]
    public void TeamTrial_OnlyActiveHumanFinishersArePaid()
    {
        MatchAssignment m = Trial("mean", 2);
        (MatchSettlement? s, _) = Compute(m, TrialRace(2, id => id == H(2) ? (RunOutcome.DidNotFinish, 0, 0) : (RunOutcome.Finished, 290_000, 0)));
        Assert.Null(For(s!, H(1)).PayoutWithheld);
        Assert.NotNull(For(s!, H(2)).PayoutWithheld);
        Assert.False(For(s!, H(2)).Receipt.TeamTrial!.CompletionPayable);
        Assert.Null(For(s!, H(2)).TeamBest);
    }

    [Fact]
    public void TeamBest_AnAfkHumanCannotCollectMoneyForAnAiWin()
    {
        MatchAssignment m = Trial("best", 1);
        // A friendly AI wins the trial; the only human finishes far outside the 345 s participation envelope.
        (MatchSettlement? afk, _) = Compute(m, TrialRace(1, id => id switch
        {
            "ally-1" => (RunOutcome.Finished, 200_000, 0),
            _ when id == H(1) => (RunOutcome.Finished, 400_000, 0),
            _ => (RunOutcome.Finished, 250_000, 0),
        }));
        Assert.Equal("victory", For(afk!, H(1)).Receipt.TeamTrial!.Verdict);
        Assert.NotNull(For(afk!, H(1)).PayoutWithheld);
        Assert.Empty(For(afk!, H(1)).MusicGrants);

        (MatchSettlement? engaged, _) = Compute(m, TrialRace(1, id => id switch
        {
            "ally-1" => (RunOutcome.Finished, 200_000, 0),
            _ when id == H(1) => (RunOutcome.Finished, 300_000, 0),
            _ => (RunOutcome.Finished, 250_000, 0),
        }));
        Assert.Null(For(engaged!, H(1)).PayoutWithheld);
        Assert.Equal(200_000, For(engaged!, H(1)).Receipt.TeamTrial!.PlayerTeamValue);
    }

    [Fact]
    public void TeamDrift_SumsRawScores_AndADqContributesZero()
    {
        MatchAssignment m = Trial("drift", 3);
        (MatchSettlement? s, string? error) = Compute(m, TrialRace(3, id => id switch
        {
            _ when id == H(3) => (RunOutcome.DisqualifiedDisconnect, 0, 0),
            _ when id.StartsWith("opp") => (RunOutcome.Finished, 250_000, 9_000),
            _ => (RunOutcome.Finished, 260_000, 10_000),
        }, drift: true));
        Assert.Null(error);
        TeamTrialReceipt t = For(s!, H(1)).Receipt.TeamTrial!;
        Assert.Equal(5 * 10_000L, t.PlayerTeamValue);
        Assert.Equal(6 * 9_000L, t.OpposingTeamValue);
        Assert.Equal("defeat", t.Verdict);
        Assert.False(For(s!, H(3)).Receipt.TeamTrial!.CompletionPayable);
    }
}
