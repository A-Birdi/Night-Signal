using System.Text.Json;
using NightSignal.ControlPlane.Convoys;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Addendum 01 §1, §3, §5.2, §12 at the convoy/allocation boundary: capacity H = 1..6 and ≤ 12 vehicles, Time Attack
/// without AI, live finale duels, finale-only rivals rejected server-side, Team Trial 6 v 6 rosters, convoy course access
/// with non-leader sponsors and event-scoped guest passes.
/// </summary>
public sealed class RosterAndAccessTests : ConvoyTestBase
{
    static readonly string[] FinaleOnly = { FinalRivals.NormalFinal, FinalRivals.HardFinal };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void EveryHumanCount_AllowsAiUpToTwelveVehicles_AndRejectsAThirteenth(int humans)
    {
        Convoy(humans);
        EnterMode(Freeplay("sprint"));
        clock.Advance(TimeSpan.FromSeconds(15));
        int maxAi = Limits.MaxRaceVehicles - humans;
        Assert.Equal("capacity_exceeded", dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, maxAi + 1, null, null)).Error?.Code);
        long rev = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, maxAi, null, null)), "proposalRevision");
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(humans)).Plan!;
        Assert.Equal(humans, plan.Roster.Count(r => r.Kind == "human"));
        Assert.Equal(maxAi, plan.Roster.Count(r => r.Kind == "ai"));
        Assert.Equal(Limits.MaxRaceVehicles, plan.Roster.Count);
        Assert.DoesNotContain(plan.Roster, r => FinaleOnly.Contains(r.DriverId)); // random pools never draw R40/R48
        Assert.Equal("light-contact", plan.Settings.Collision);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void TimeAttack_IsHumansOnly_AndNonContact_AndStaleAiCannotSlipIn(int humans)
    {
        Convoy(humans);
        EnterMode(Freeplay("time-attack"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), new EventRequest(null, "C03", "time-attack", null, 1, null, null)).Error?.Code);
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), new EventRequest(null, "C03", "time-attack", null, 0, null, "light-contact")).Error?.Code);
        long rev = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C03", "time-attack", null, 0, null, null)), "proposalRevision");
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(humans)).Plan!;
        Assert.Empty(plan.AiEntrants);
        Assert.Equal("non-contact", plan.Settings.Collision);
        Assert.False(plan.PurePvP); // no pure-PvP winner bonus in Time Attack
    }

    [Theory]
    [InlineData("R40")]
    [InlineData("R48")]
    public void FinaleOnlyRivals_AreRejectedAsFreeplayOpponents_EvenWhenForged(string rival)
    {
        Convoy(2);
        EnterMode(Freeplay("sprint"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("rival_not_allowed", dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 2, null, null, AiRivals: new[] { "R01", rival })).Error?.Code);
        Assert.Equal("rival_not_allowed", dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 1, null, null, AiRivals: new[] { "R99" })).Error?.Code);
        long rev = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 3, null, null, AiRivals: new[] { "R08", "R01" })), "proposalRevision");
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(2)).Plan!;
        Assert.Equal(new[] { "R08", "R01" }, plan.Roster.Where(r => r.Kind == "ai").Take(2).Select(r => r.DriverId)); // a defeated lieutenant is fine
        Assert.DoesNotContain(plan.Roster, r => FinaleOnly.Contains(r.DriverId));
    }

    [Fact]
    public void FinaleOnlyRivals_AreRejectedAsCupSubstitutions()
    {
        Convoy(1);
        EnterMode(Freeplay("cup"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("rival_not_allowed", dir.ProposeEvent(Id(1),
            new EventRequest(null, null, "cup", null, 1, null, null, CupLegs: new[] { "C01", "C02" }, AiRivals: new[] { "R40" })).Error?.Code);
    }

    [Theory]
    [InlineData(1, "normal")]
    [InlineData(6, "normal")]
    [InlineData(1, "hard")]
    [InlineData(6, "hard")]
    public void Finale_IsALiveDuelWithTheFinalRival_AtEveryPartySize(int humans, string mode)
    {
        bool hard = mode == "hard";
        Convoy(humans, normalCleared: hard ? 30 : 29, hardCleared: hard ? 29 : 0);
        long rev = OpenEvent(stage: "S30", mode: mode);
        JsonElement settings = State(1).GetProperty("eventProposal").GetProperty("settings");
        Assert.True(settings.GetProperty("requiresBeatingFeaturedRival").GetBoolean());
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(humans, hard ? 30 : 29, hard ? 29 : 0)).Plan!;
        string final = hard ? FinalRivals.HardFinal : FinalRivals.NormalFinal;
        Assert.Equal(new[] { final }, plan.AiEntrants); // H + 1: a solid live rival, no replay, no six filler rivals
        Assert.Equal(final, plan.FeaturedRival);
        Assert.Equal("featured-rival", plan.Roster.Single(r => r.Kind == "ai").Role);
        Assert.Equal(humans + 1, plan.Roster.Count);
    }

    [Fact]
    public void EncounterStages_RequireBeatingTheFeaturedRival_RegularStagesDoNot()
    {
        Convoy(1, normalCleared: 6);
        OpenEvent(stage: "S07"); // lieutenant
        Assert.True(State(1).GetProperty("eventProposal").GetProperty("settings").GetProperty("requiresBeatingFeaturedRival").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(15));
        dir.ProposeEvent(Id(1), new EventRequest("S06", null, null, null, null, null, null));
        Assert.False(State(1).GetProperty("eventProposal").GetProperty("settings").GetProperty("requiresBeatingFeaturedRival").GetBoolean());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void TeamTrial_FillsTheSixSeatSide_WithFriendlyAi_AgainstSixOpponents(int humans)
    {
        Convoy(humans);
        EnterMode(Challenges("TT_MEAN"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), new EventRequest(null, null, null, null, 3, null, null, TrialId: "TT_MEAN")).Error?.Code);
        long rev = Value(dir.ProposeEvent(Id(1), new EventRequest(null, null, null, null, null, null, null, TrialId: "TT_MEAN", Difficulty: "expert")), "proposalRevision");
        JsonElement preview = State(1).GetProperty("eventProposal").GetProperty("rosterPreview");
        Assert.Equal(6 - humans, preview.GetProperty("friendlyAi").GetInt32());
        Assert.Equal(6, preview.GetProperty("opposingAi").GetInt32());
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(humans)).Plan!;
        Assert.Equal(6, plan.Roster.Count(r => r.Team == "player"));
        Assert.Equal(6 - humans, plan.Roster.Count(r => r.Role == "friendly-ai"));
        Assert.Equal(6, plan.Roster.Count(r => r.Team == "opposing"));
        Assert.Equal(12, plan.Roster.Count);
        Assert.DoesNotContain(plan.Roster, r => FinaleOnly.Contains(r.DriverId));
        Assert.Equal("trial", plan.Settings.Kind);
        Assert.Equal("expert", plan.Settings.Difficulty);
    }

    [Fact]
    public void UnknownTrial_IsRejected()
    {
        Convoy(1);
        EnterMode(Challenges());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("unknown_trial", dir.ProposeEvent(Id(1), new EventRequest(null, null, null, null, null, null, null, TrialId: "TT_NOPE")).Error?.Code);
        Assert.Equal("unknown_trial", dir.SetIntent(Id(1), Challenges("TT_NOPE")).Error?.Code);
    }

    // ------------------------------------------------------------------ course access and guest passes

    [Fact]
    public void ANonLeaderSponsor_MakesACourseAvailable_OthersGetEventScopedGuestPasses()
    {
        Convoy(3, courses: new() { [2] = new[] { "FP01" } });
        EnterMode(Freeplay("circuit"));
        JsonElement access = State(1).GetProperty("freeplayAccess").GetProperty("courses").EnumerateArray()
            .Single(c => c.GetProperty("courseId").GetString() == "FP01");
        Assert.Equal(new[] { Id(2) }, access.GetProperty("sponsors").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { Id(1), Id(3) }, access.GetProperty("guests").EnumerateArray().Select(x => x.GetString()));

        long rev = ProposeFreeplay("FP01", "circuit");
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(3), Owned(3, (2, "FP01"))).Plan!;
        Assert.Equal(new[] { Id(2) }, plan.Sponsors["FP01"]);
        Assert.Equal(2, plan.GuestPasses.Count);
        Assert.All(plan.GuestPasses, p => Assert.Equal((Id(2), "FP01"), (p.SponsorId, p.CourseId)));
        Assert.DoesNotContain(plan.GuestPasses, p => p.AccountId == Id(2));
    }

    [Fact]
    public void NobodyOwningACourse_MakesItUnavailable_AndUnsupportedModesAreRejected()
    {
        Convoy(2);
        EnterMode(Freeplay("sprint"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("course_locked", dir.ProposeEvent(Id(1), new EventRequest(null, "C20", "sprint", null, 0, null, null)).Error?.Code);
        Assert.Equal("mode_unsupported", dir.ProposeEvent(Id(1), new EventRequest(null, "C03", "sprint", null, 0, null, null)).Error?.Code);
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "circuit", null, 0, null, null)).Error?.Code); // not the agreed mode
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), new EventRequest(null, "T00", "sprint", null, 0, null, null)).Error?.Code);
    }

    [Fact]
    public void LosingTheLastSponsor_BeforeAllocation_WithdrawsTheProposalWithAnExplanation()
    {
        Convoy(3, courses: new() { [2] = new[] { "C20" } });
        EnterMode(Freeplay("sprint"));
        ProposeFreeplay("C20", "sprint");
        dir.Disconnected(Id(2));
        JsonElement s = State(1);
        Assert.Equal("EventSelection", s.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("eventProposal").ValueKind);
        Assert.Equal("proposal_withdrawn", s.GetProperty("noticeCode").GetString());
        Assert.Contains("sponsor", s.GetProperty("notice").GetString());
    }

    [Fact]
    public void LosingTheSponsor_AfterAllocation_KeepsEveryIssuedPass()
    {
        Convoy(3, courses: new() { [3] = new[] { "C20" } });
        EnterMode(Freeplay("sprint"));
        long rev = ProposeFreeplay("C20", "sprint");
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(3), Owned(3, (3, "C20"))).Plan!;
        dir.Disconnected(Id(3)); // the sponsor leaves while allocating
        dir.CompleteStart(plan, new ActiveMatch("m_1", "srv", "h", 1, V, plan.Entrants.Select(e => e.AccountId).ToList()));
        Assert.Equal(2, plan.GuestPasses.Count); // frozen into the plan/assignment; the passes survive the sponsor's departure
        Assert.True(dir.RacerEligible(plan.ConvoyId, Id(1)));
        Assert.True(dir.RacerEligible(plan.ConvoyId, Id(2)));
        Assert.Equal("InMatch", State(1).GetProperty("phase").GetString());
    }

    [Fact]
    public void Start_RevalidatesSponsorship_WithFreshStoredEntitlements()
    {
        Convoy(2, courses: new() { [2] = new[] { "FP03" } });
        EnterMode(Freeplay("circuit"));
        long rev = ProposeFreeplay("FP03", "circuit");
        ReadyAll(rev);
        Assert.Equal("course_locked", dir.BeginStart(Id(1), rev, Fresh(2), Owned(2)).Error?.Code); // storage says nobody owns it
    }

    [Fact]
    public void Cup_ValidatesAndFreezesAccessForEveryLeg()
    {
        Convoy(2, courses: new() { [2] = new[] { "FP02" } });
        EnterMode(Freeplay("cup"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), new EventRequest(null, null, "cup", null, 0, null, null, CupLegs: new[] { "C01" })).Error?.Code);
        Assert.Equal("course_locked", dir.ProposeEvent(Id(1), new EventRequest(null, null, "cup", null, 0, null, null, CupLegs: new[] { "C01", "FP01" })).Error?.Code);
        long rev = Value(dir.ProposeEvent(Id(1), new EventRequest(null, null, "cup", null, 0, null, null, CupLegs: new[] { "C01", "FP02", "C03" })), "proposalRevision");
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(2), Owned(2, (2, "FP02"))).Plan!;
        Assert.Equal(new[] { "C01", "FP02", "C03" }, plan.Settings.CupLegs);
        GuestPass pass = Assert.Single(plan.GuestPasses);
        Assert.Equal((Id(1), "FP02", Id(2)), (pass.AccountId, pass.CourseId, pass.SponsorId));
    }

    [Fact]
    public void APurchase_UpdatesTheAccessiblePool_WithoutChangingCarsOrSelectingTheCourse()
    {
        Convoy(2);
        EnterMode(Freeplay("sprint"));
        long cosmetic = MemberState(1, 2).GetProperty("loadoutRevision").GetInt64();
        Assert.DoesNotContain(State(1).GetProperty("freeplayAccess").GetProperty("courses").EnumerateArray(), c => c.GetProperty("courseId").GetString() == "C20");
        dir.UpdateOwnedCourses(Id(2), new[] { "C20" });
        JsonElement s = State(1);
        Assert.Contains(s.GetProperty("freeplayAccess").GetProperty("courses").EnumerateArray(), c => c.GetProperty("courseId").GetString() == "C20");
        Assert.Equal(cosmetic, MemberState(1, 2).GetProperty("loadoutRevision").GetInt64());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("eventProposal").ValueKind);
    }

    // ------------------------------------------------------------------ friend invitations and friend status

    [Fact]
    public void FriendInvites_AreCheckedWhenUsed()
    {
        Convoy(2);
        dir.Connected(Id(5), V);
        Assert.Equal("not_leader", dir.InviteFriend(Id(2), Id(5)).Error?.Code); // private convoy: leader invites
        string inviteId = Result(dir.InviteFriend(Id(1), Id(5))).GetProperty("inviteId").GetString()!;
        Assert.Equal(inviteId, Result(dir.InviteFriend(Id(1), Id(5))).GetProperty("inviteId").GetString()); // idempotent re-invite
        Assert.Single(notifier.To(Id(5), "convoy.invited").Select(m => m.GetProperty("inviteId").GetString()).Distinct());

        Assert.Equal("invite_denied", dir.JoinByFriendInvite(Id(5), Info(5), inviteId, relationshipOk: false).Error?.Code);
        Assert.Equal("invite_invalid", dir.JoinByFriendInvite(Id(6), Info(6), inviteId, relationshipOk: true).Error?.Code); // not addressed to 6
        Assert.True(dir.JoinByFriendInvite(Id(5), Info(5), inviteId, relationshipOk: true).Ok);
        Assert.Equal("already_in_convoy", dir.JoinByFriendInvite(Id(5), Info(5), inviteId, relationshipOk: true).Error?.Code);
        Assert.Equal(3, MemberCount(1));
    }

    [Fact]
    public void FriendInvite_FromADepartedInviter_OrIntoAFullConvoy_Fails()
    {
        Convoy(5);
        dir.Connected(Id(7), V);
        dir.Connected(Id(8), V);
        string a = Result(dir.InviteFriend(Id(1), Id(7))).GetProperty("inviteId").GetString()!;
        string b = Result(dir.InviteFriend(Id(1), Id(8))).GetProperty("inviteId").GetString()!;
        Assert.True(dir.JoinByFriendInvite(Id(7), Info(7), a, true).Ok);
        Assert.Equal("convoy_full", dir.JoinByFriendInvite(Id(8), Info(8), b, true).Error?.Code); // no seventh human
        Assert.True(dir.Leave(Id(1)).Ok);
        Assert.Equal("invite_invalid", dir.JoinByFriendInvite(Id(8), Info(8), b, true).Error?.Code); // the inviter left
    }

    [Fact]
    public void FriendView_ReportsCoarseStatus_StaleAsUnknown_AndServerValidatedRejoin()
    {
        Convoy(3);
        dir.SetPresence(Id(2), Presence.Garage);
        dir.Disconnected(Id(3));
        dir.Connected(Id(3), V); // holds a valid grant into member 1's convoy
        dir.Connected(Id(4), V);
        var view = dir.FriendView(Id(3), new[] { Id(1), Id(2), Id(4), Id(9) });
        Assert.Equal(FriendStatus.Available, view[Id(1)].Status);
        Assert.Equal(FriendStatus.Garage, view[Id(2)].Status);
        Assert.Equal(FriendStatus.Offline, view[Id(9)].Status);
        Assert.True(view[Id(1)].CanRejoin);    // Rejoin only where the server's grant is valid
        Assert.False(view[Id(4)].CanRejoin);
        Assert.NotNull(view[Id(1)].ConvoyId);
        Assert.Null(view[Id(4)].ConvoyId);

        clock.Advance(TimeSpan.FromSeconds(91));
        dir.Seen(Id(3));
        Assert.Equal(FriendStatus.Unknown, dir.FriendView(Id(3), new[] { Id(4) })[Id(4)].Status); // stale is not a false Online/Offline

        Assert.True(dir.Leave(Id(1)).Ok); // leadership changed: Rejoin disappears from the friend row
        Assert.False(dir.FriendView(Id(3), new[] { Id(2) })[Id(2)].CanRejoin);
    }

    Dictionary<string, IReadOnlyCollection<string>> Owned(int members, params (int Member, string Course)[] owned) =>
        Enumerable.Range(1, members).ToDictionary(Id, i => (IReadOnlyCollection<string>)owned.Where(o => o.Member == i).Select(o => o.Course).ToList());
}
