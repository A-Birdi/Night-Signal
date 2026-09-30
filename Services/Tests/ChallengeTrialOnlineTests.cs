using System.Text.Json;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Garage;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Challenge trials online (docs/CHALLENGE_TRIALS.md): proposed under the Challenges intent as a non-contact Time Attack on
/// the trial's course with no sponsor; every entrant races the supplied loaner; settlement grants the challenge from the game
/// server's verdict and the account's settled passes (a grouped challenge needs every trial of its group).
/// </summary>
public sealed class ChallengeTrialOnlineTests : ConvoyTestBase
{
    static EventRequest TrialRequest(string trialId, int? carCapPi = null) =>
        new(null, null, null, null, null, carCapPi, null, ChallengeTrialId: trialId);

    static EntrantBuild Loaner(string car = "V07") => new() { InstanceId = "loaner:TR-CH51", CarId = car, BuildHash = "loaner-hash", Pi = 485 };

    long ProposeTrial(string trialId)
    {
        clock.Advance(TimeSpan.FromSeconds(15));
        ConvoyResult r = dir.ProposeEvent(Id(1), TrialRequest(trialId));
        Assert.True(r.Ok, r.Error?.Message);
        return State(1).GetProperty("eventProposal").GetProperty("revision").GetInt64();
    }

    [Fact]
    public void ATrial_IsANonContactTimeAttackOnItsCourse_ThatNeedsNoSponsor()
    {
        Convoy(2); // nobody owns C08
        EnterMode(Challenges());
        ProposeTrial("TR-CH51");
        JsonElement s = State(1).GetProperty("eventProposal").GetProperty("settings");
        Assert.Equal("freeplay", s.GetProperty("kind").GetString());
        Assert.Equal("time-attack", s.GetProperty("freeplayMode").GetString());
        Assert.Equal("C08", s.GetProperty("courseId").GetString());
        Assert.Equal("non-contact", s.GetProperty("collision").GetString());
        Assert.Equal(0, s.GetProperty("aiCount").GetInt32());
        Assert.Equal("TR-CH51", s.GetProperty("challengeTrialId").GetString());
        Assert.Equal(TestData.Content.Catalogue.ChallengeTrials.Trials.Count(t => t.Published && t.Conditions == "course" && !t.IsRace), // racecraft trials are offline-only
            State(1).GetProperty("challengeTrials").GetArrayLength());

        // A new member who owns nothing does not withdraw it (the trial supplies its course).
        Join(3);
        Assert.Equal(JsonValueKind.Object, State(1).GetProperty("eventProposal").ValueKind);
    }

    [Fact]
    public void ATrial_IsRefused_WithAChosenCap_OrUnknown()
    {
        Convoy(1);
        EnterMode(Challenges());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), TrialRequest("TR-CH51", carCapPi: 999)).Error?.Code);
        Assert.Equal("unknown_trial", dir.ProposeEvent(Id(1), TrialRequest("TR-NOPE")).Error?.Code);
    }

    [Fact]
    public void ARacecraftTrial_IsRefusedOnline_ForNow()
    {
        // Its fixed AI field (roles, grid, paces) is offline-only until the control plane can place it.
        Convoy(1);
        EnterMode(Challenges());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("trial_unsupported", dir.ProposeEvent(Id(1), TrialRequest("TR-CH41")).Error?.Code);
    }

    [Fact]
    public void ATrial_IsRefused_WhenTheConvoyAgreedToATeamTrial()
    {
        Convoy(1);
        string teamTrial = TeamTrialCatalog.Fixture(TestData.Content.Catalogue).Trials[0].Id;
        EnterMode(Challenges(teamTrial));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), TrialRequest("TR-CH51")).Error?.Code);
    }

    [Fact]
    public void Start_RacesEveryEntrantInTheSuppliedLoaner()
    {
        Convoy(2);
        EnterMode(Challenges());
        long rev = ProposeTrial("TR-CH51");
        ReadyAll(rev);
        Assert.Equal("trial_loaner", dir.BeginStart(Id(1), rev, Fresh(2)).Error?.Code);
        Assert.Equal("trial_loaner", dir.BeginStart(Id(1), rev, Fresh(2), null, null, Loaner("V01")).Error?.Code);

        (ConvoyError? error, MatchPlan? plan) = dir.BeginStart(Id(1), rev, Fresh(2), null, null, Loaner());
        Assert.True(plan is not null, error?.Message);
        Assert.Equal("TR-CH51", plan!.Settings.ChallengeTrialId);
        Assert.Empty(plan.GuestPasses);
        Assert.All(plan.Entrants, e =>
        {
            Assert.Equal("V07", e.Loadout.CarId);
            Assert.Equal("loaner-hash", e.Loadout.PerformanceHash);
            AssignedEntrant a = MatchAllocator.Entrant(e);
            Assert.Equal("V07", a.CarId);
            Assert.Equal("loaner-hash", a.VehicleBuild!.BuildHash);
        });
    }

    // ---------------- settlement

    static string H(int i) => Id(i);

    static SettlementService Service() => new(null!, null!, TestData.Content, null!, null!, null!, MusicUnlockManifest.Parse(
        """{"schema":"night-signal/music-unlocks@1","cues":[{"cueId":"menu-main","source":{"kind":"baseline"}}]}""",
        TestData.Content.Catalogue, TeamTrialCatalog.Fixture(TestData.Content.Catalogue)));

    static MatchAssignment TrialMatch(string matchId, string trialId, string? challengeTrialId = "")
    {
        ChallengeTrialDef t = TestData.Content.Catalogue.ChallengeTrials.Find(trialId)!;
        return new MatchAssignment
        {
            MatchId = matchId, ConvoyId = "cv", ServerId = "srv", Kind = "freeplay", CourseId = t.Course, FreeplayMode = "time-attack",
            Weather = "stage-default", Collision = "non-contact", CarCapPi = 999, ChallengeTrialId = challengeTrialId == "" ? t.Id : challengeTrialId,
            Entrants = new List<AssignedEntrant> { new(H(1), H(1), "racer", t.Loaner.Car, 400, "loaner-hash", "c", 1) },
            AiEntrants = new List<string>(), Roster = new[] { new RosterSlot(H(1), "human", "player", "driver", H(1)) },
            Build = "b", Protocol = 1, ContentHash = "c", Seed = 1, ResultsUrl = "", TicketIssuer = "i", TicketAudience = "a",
        };
    }

    static ResultSubmission Result(MatchAssignment m, bool? passed, string? trialId = "") => new()
    {
        MatchId = m.MatchId, ContentHash = "c",
        Entrants = new List<EntrantFacts>
        {
            new()
            {
                EntrantId = H(1), Human = true, Outcome = RunOutcome.Finished, FinishTimeMicros = 150_000_000, Placement = 1,
                CheckpointFraction = 1, ActiveProgressVerified = true, ActivelyDroveLegalCourse = true, LegalProgressMetres = 3000,
                TrialId = trialId == "" ? m.ChallengeTrialId : trialId, TrialPassed = passed, TrialSummary = passed == true ? "ok: the supplied loaner" : "MISSED: time",
            },
        },
    };

    static EntrantSettlement Settle(MatchAssignment m, bool? passed, params string[] passedBefore)
    {
        (MatchSettlement? s, string? e) = Service().Compute(m, Result(m, passed), "h", null, null, null, null,
            new Dictionary<string, IReadOnlyCollection<string>> { [H(1)] = passedBefore });
        Assert.Null(e);
        return s!.Entrants.Single(x => x.AccountId == H(1));
    }

    [Fact]
    public void Settlement_GrantsTheChallenge_OnlyWhenTheGameServerPassedTheTrial()
    {
        EntrantSettlement pass = Settle(TrialMatch("m1", "TR-CH55"), true);
        Assert.Contains(pass.Challenges, g => g.ChallengeId == "CH55");
        Assert.True(pass.Receipt.ChallengeTrial!.Passed);
        Assert.Equal((1, 1), (pass.Receipt.ChallengeTrial.GroupPassed, pass.Receipt.ChallengeTrial.GroupSize));

        EntrantSettlement miss = Settle(TrialMatch("m2", "TR-CH55"), false);
        Assert.DoesNotContain(miss.Challenges, g => g.ChallengeId == "CH55");
        Assert.False(miss.Receipt.ChallengeTrial!.Passed);
        Assert.Equal("MISSED: time", miss.Receipt.ChallengeTrial.Summary);
    }

    [Fact]
    public void AGroupedChallenge_NeedsEveryTrialOfItsGroup_AcrossSettledMatches()
    {
        EntrantSettlement first = Settle(TrialMatch("m1", "TR-CH54-FWD"), true);
        Assert.DoesNotContain(first.Challenges, g => g.ChallengeId == "CH54");
        Assert.Equal((1, 2), (first.Receipt.ChallengeTrial!.GroupPassed, first.Receipt.ChallengeTrial.GroupSize));

        EntrantSettlement second = Settle(TrialMatch("m2", "TR-CH54-RWD"), true, "TR-CH54-FWD");
        Assert.Contains(second.Challenges, g => g.ChallengeId == "CH54");
        Assert.Equal(2, second.Receipt.ChallengeTrial!.GroupPassed);
    }

    [Fact]
    public void TrialFacts_ThatDoNotBelongToTheMatch_AreRefused()
    {
        MatchAssignment plain = TrialMatch("m1", "TR-CH55", challengeTrialId: null);
        Assert.NotNull(Service().Compute(plain, Result(plain, true, "TR-CH55"), "h").Error);
        MatchAssignment trial = TrialMatch("m2", "TR-CH55");
        Assert.NotNull(Service().Compute(trial, Result(trial, true, "TR-CH11"), "h").Error);
        MatchAssignment unknown = TrialMatch("m3", "TR-CH55", challengeTrialId: "TR-NOPE");
        Assert.NotNull(Service().Compute(unknown, Result(unknown, true), "h").Error);
    }

    [Fact]
    public async Task TheStore_ReadsThePassesBackFromSettledReceipts()
    {
        using var tmp = new TempDir();
        var store = new SqliteGameStore(tmp.File("trials.db"));
        await store.InitializeAsync();
        await store.EnsureAccountAsync(H(1));
        async Task Record(MatchAssignment m, bool passed, bool settle = true)
        {
            await store.RecordAllocationAsync(new MatchRecord
            {
                MatchId = m.MatchId, ConvoyId = "cv", ServerId = "srv", ConfigJson = JsonSerializer.Serialize(m, MatchAllocator.Json), ResultsSecret = "x",
            });
            if (!settle)
            {
                await store.MarkAbortedAsync(m.MatchId);
                return;
            }
            EntrantSettlement e = Settle(m, passed);
            await store.SettleAsync(new MatchSettlement { MatchId = m.MatchId, ResultsSha256 = "h-" + m.MatchId, Entrants = new[] { e } });
        }
        await Record(TrialMatch("m1", "TR-CH54-FWD"), true);
        await Record(TrialMatch("m2", "TR-CH11"), false);
        await Record(TrialMatch("m3", "TR-CH25"), true, settle: false); // aborted: nothing
        Assert.Equal(new[] { "TR-CH54-FWD" }, await store.TrialPassesAsync(H(1)));
    }
}
