using System.Text.Json;
using NightSignal.ControlPlane.Convoys;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>Convoy rules (spec §4.1, §4.2, §5.1 as revised by Addendum 01 §7, §10) with an injectable clock.</summary>
public sealed class ConvoyDirectoryTests : ConvoyTestBase
{
    [Fact]
    public void ConvoyHoldsAtMostSixMembers_NeverASeventhHuman()
    {
        Convoy(6);
        dir.Connected(Id(7), V);
        string code = Code(dir.CreateInvite(Id(1)));
        ConvoyResult r = dir.JoinByCode(Id(7), Info(7), code);
        Assert.Equal("convoy_full", r.Error?.Code);
        Assert.Equal(6, MemberCount(1));
        Assert.Empty(dir.ListDiscoverable());
    }

    [Fact]
    public void AnAccountBelongsToOneConvoy()
    {
        Convoy(2);
        dir.Connected(Id(3), V);
        Assert.True(dir.Create(Id(3), Info(3), ConvoyPrivacy.Discoverable).Ok);
        Assert.Equal("already_in_convoy", dir.Create(Id(1), Info(1), ConvoyPrivacy.InviteOnly).Error?.Code);
        Assert.Equal("already_in_convoy", dir.JoinByCode(Id(3), Info(3), Code(dir.CreateInvite(Id(1)))).Error?.Code);
        string otherConvoy = State(3).GetProperty("convoyId").GetString()!;
        Assert.Equal("already_in_convoy", dir.JoinDiscoverable(Id(2), Info(2), otherConvoy).Error?.Code);
    }

    [Fact]
    public void PrivateConvoysNeedAnInvite_DiscoverableOnesAreListed()
    {
        Convoy(1);
        dir.Connected(Id(2), V);
        string privateId = State(1).GetProperty("convoyId").GetString()!;
        Assert.Equal("invite_required", dir.JoinDiscoverable(Id(2), Info(2), privateId).Error?.Code);
        Assert.Empty(dir.ListDiscoverable());
        dir.Connected(Id(3), V);
        dir.Create(Id(3), Info(3), ConvoyPrivacy.Discoverable);
        Assert.Single(dir.ListDiscoverable());
        Assert.True(dir.JoinDiscoverable(Id(2), Info(2), State(3).GetProperty("convoyId").GetString()).Ok);
    }

    [Fact]
    public void InviteCodes_AreUnambiguous_Expire_AndCanBeRevoked()
    {
        Convoy(1);
        string code = Code(dir.CreateInvite(Id(1)));
        Assert.Equal(8, code.Length);
        Assert.All(code, ch => Assert.Contains(ch, ConvoyRules.InviteAlphabet));

        dir.Connected(Id(2), V);
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal("invite_invalid", dir.JoinByCode(Id(2), Info(2), code).Error?.Code);

        string revoked = Code(dir.CreateInvite(Id(1)));
        Assert.True(dir.RevokeInvite(Id(1), revoked).Ok);
        Assert.Equal("invite_invalid", dir.JoinByCode(Id(2), Info(2), revoked).Error?.Code);

        string fresh = Code(dir.CreateInvite(Id(1)));
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(dir.JoinByCode(Id(2), Info(2), fresh.ToLowerInvariant().Insert(4, "-")).Ok); // normalised input
    }

    [Fact]
    public void JoinAttempts_AreRateLimitedPerAccount()
    {
        Convoy(1);
        dir.Connected(Id(2), V);
        for (int i = 0; i < ConvoyRules.JoinAttemptLimit; i++)
            Assert.Equal("invite_invalid", dir.JoinByCode(Id(2), Info(2), $"ZZZZZZ{i:00}").Error?.Code);
        string valid = Code(dir.CreateInvite(Id(1)));
        ConvoyResult limited = dir.JoinByCode(Id(2), Info(2), valid);
        Assert.Equal("rate_limited", limited.Error?.Code);
        Assert.True(limited.Error!.RetryAfterMs > 0);
        clock.Advance(ConvoyRules.JoinAttemptWindow);
        Assert.True(dir.JoinByCode(Id(2), Info(2), Code(dir.CreateInvite(Id(1)))).Ok);
    }

    [Fact]
    public void EnterMode_NeedsEveryCurrentMembersModeReady_ForThatRevision()
    {
        Convoy(3);
        long rev = Value(dir.SetIntent(Id(1), Campaign()), "modeRevision");
        Assert.Equal("ModeCheck", State(1).GetProperty("phase").GetString());
        Assert.True(MemberState(1, 1).GetProperty("modeReady").GetBoolean()); // proposing is the leader's own consent
        Assert.Equal("not_all_ready", dir.EnterMode(Id(1), rev).Error?.Code);
        Assert.True(dir.SetModeReady(Id(2), rev, true).Ok);
        Assert.Equal("stale_revision", dir.SetModeReady(Id(3), rev - 1, true).Error?.Code);
        Assert.Equal("not_all_ready", dir.EnterMode(Id(1), rev).Error?.Code);
        Assert.Equal(2, State(1).GetProperty("modeReadyCount").GetInt32());

        dir.Disconnected(Id(3)); // leaves ACTIVE membership: nobody waits for an offline member (new revision, though)
        long rev2 = State(1).GetProperty("modeRevision").GetInt64();
        Assert.NotEqual(rev, rev2);
        Assert.Equal("stale_revision", dir.EnterMode(Id(1), rev).Error?.Code);
        Assert.True(dir.SetModeReady(Id(2), rev2, true).Ok);
        Assert.Equal("not_leader", dir.EnterMode(Id(2), rev2).Error?.Code);
        Assert.True(dir.EnterMode(Id(1), rev2).Ok);
        JsonElement s = State(1);
        Assert.Equal("EventSelection", s.GetProperty("phase").GetString());
        Assert.True(s.GetProperty("modeEntered").GetBoolean());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("eventProposal").ValueKind); // entering never starts an event
        Assert.Equal("Normal Campaign", s.GetProperty("intent").GetProperty("label").GetString());
    }

    [Fact]
    public void ChangingIntent_IsANewModeRevision_OldModeReadyCannotAuthorizeIt()
    {
        Convoy(2);
        long first = Value(dir.SetIntent(Id(1), Freeplay("sprint")), "modeRevision");
        Assert.True(dir.SetModeReady(Id(2), first, true).Ok);
        clock.Advance(TimeSpan.FromSeconds(15));
        long second = Value(dir.SetIntent(Id(1), Freeplay("circuit")), "modeRevision");
        Assert.True(second > first);
        Assert.False(MemberState(1, 2).GetProperty("modeReady").GetBoolean());
        Assert.Equal("stale_revision", dir.SetModeReady(Id(2), first, true).Error?.Code);
        Assert.Equal("not_all_ready", dir.EnterMode(Id(1), second).Error?.Code);
        Assert.True(dir.SetModeReady(Id(2), second, true).Ok);
        Assert.True(dir.EnterMode(Id(1), second).Ok);
        Assert.Equal("circuit", State(2).GetProperty("intent").GetProperty("submode").GetString());
    }

    [Fact]
    public void Solo_UsesTheSameIntentSemantics_WithoutWaiting()
    {
        Convoy(1);
        long rev = Value(dir.SetIntent(Id(1), Campaign()), "modeRevision");
        Assert.True(dir.EnterMode(Id(1), rev).Ok);
        Assert.Equal("EventSelection", State(1).GetProperty("phase").GetString());
    }

    [Fact]
    public void Intent_IsSeparateFromEachMembersActualPresence()
    {
        Convoy(2);
        dir.SetIntent(Id(1), Freeplay("sprint"));
        Assert.True(dir.SetPresence(Id(1), Presence.Garage).Ok);
        JsonElement s = State(2);
        Assert.Equal("Freeplay — Sprint", s.GetProperty("intent").GetProperty("label").GetString());
        Assert.Equal("Garage", MemberState(2, 1).GetProperty("presence").GetString());
        Assert.Equal(1, s.GetProperty("modeReadyCount").GetInt32());
    }

    [Fact]
    public void LeaderMayRequestReadiness_AtMostOncePer15Seconds()
    {
        Convoy(2);
        Assert.True(dir.SetIntent(Id(1), Freeplay("sprint")).Ok);
        ConvoyResult again = dir.SetIntent(Id(1), Campaign());
        Assert.Equal("rate_limited", again.Error?.Code);
        Assert.InRange(again.Error!.RetryAfterMs!.Value, 1, 15_000);
        clock.Advance(TimeSpan.FromSeconds(14.9));
        Assert.Equal("rate_limited", dir.SetIntent(Id(1), Campaign()).Error?.Code);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(dir.SetIntent(Id(1), Campaign()).Ok);
    }

    [Fact]
    public void StaleReadyMessages_AreRejected()
    {
        Convoy(2);
        long first = OpenEvent();
        clock.Advance(TimeSpan.FromSeconds(15));
        long second = Value(dir.ProposeEvent(Id(1), new EventRequest("S01", null, null, "wet-night", null, null, null)), "proposalRevision");
        Assert.True(second > first);
        Assert.Equal("stale_revision", dir.SetReady(Id(2), first, 1, true).Error?.Code);  // old proposal revision
        Assert.Equal("stale_revision", dir.SetReady(Id(2), second, 0, true).Error?.Code); // old loadout revision
        Assert.True(dir.SetReady(Id(2), second, 1, true).Ok);
    }

    [Fact]
    public void EventSettingChange_UnreadiesEveryone()
    {
        Convoy(3);
        long rev = OpenEvent();
        ReadyAll(rev);
        Assert.True(Ready(1) && Ready(2) && Ready(3));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest("S01", null, null, "fog", null, null, null)).Ok);
        Assert.False(Ready(1) || Ready(2) || Ready(3));
    }

    [Fact]
    public void PerformanceLoadoutChange_UnreadiesOnlyThatMember_CosmeticChangeDoesNot()
    {
        Convoy(3);
        long rev = OpenEvent();
        ReadyAll(rev);

        Assert.True(dir.UpdateLoadout(Id(2), Car(perf: "perf-1", cosmetic: "new-livery")).Ok);
        Assert.True(Ready(1) && Ready(2) && Ready(3));

        Assert.True(dir.UpdateLoadout(Id(3), Car(perf: "perf-2")).Ok);
        Assert.True(Ready(1) && Ready(2));
        Assert.False(Ready(3));
        Assert.Equal(2, MemberState(1, 3).GetProperty("loadoutRevision").GetInt64());
        Assert.Equal(rev, State(1).GetProperty("eventProposal").GetProperty("revision").GetInt64()); // proposal itself unchanged
    }

    [Fact]
    public void RosterChange_IssuesNewRevisions_AndClearsModeAndEventReadiness()
    {
        Convoy(2);
        long rev = OpenEvent();
        ReadyAll(rev);
        long roster = State(1).GetProperty("rosterRevision").GetInt64();
        long mode = State(1).GetProperty("modeRevision").GetInt64();
        Join(3);
        JsonElement s = State(1);
        Assert.Equal(roster + 1, s.GetProperty("rosterRevision").GetInt64());
        Assert.NotEqual(rev, s.GetProperty("eventProposal").GetProperty("revision").GetInt64());
        Assert.NotEqual(mode, s.GetProperty("modeRevision").GetInt64());
        Assert.False(MemberState(1, 2).GetProperty("modeReady").GetBoolean());
        Assert.True(s.GetProperty("modeEntered").GetBoolean()); // the entered mode stays; agreement must be renewed for a vote
        Assert.False(Ready(1) || Ready(2));
        Assert.Equal("stale_revision", dir.SetReady(Id(1), rev, 1, true).Error?.Code);
    }

    [Fact]
    public void MembersWithoutInteraction_For120Seconds_BecomeAwayAndUnready()
    {
        Convoy(2);
        long rev = OpenEvent();
        ReadyAll(rev);
        clock.Advance(TimeSpan.FromSeconds(119));
        dir.Touch(Id(1));
        dir.Tick();
        Assert.False(MemberState(1, 2).GetProperty("away").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(1));
        dir.Tick();
        Assert.True(MemberState(1, 2).GetProperty("away").GetBoolean());
        Assert.False(Ready(2));
        Assert.True(Ready(1)); // the leader interacted
        dir.Touch(Id(2));
        Assert.False(MemberState(1, 2).GetProperty("away").GetBoolean());
    }

    [Fact]
    public void DisconnectedLeader_KeepsA15sGrace_ThenTheLongestConnectedMemberLeads_WithANewEpoch()
    {
        dir.Connected(Id(1), V);
        dir.Create(Id(1), Info(1), ConvoyPrivacy.InviteOnly);
        string code = Code(dir.CreateInvite(Id(1)));
        clock.Advance(TimeSpan.FromSeconds(1));
        dir.Connected(Id(3), V); // connected first → longest connected
        clock.Advance(TimeSpan.FromSeconds(1));
        dir.Connected(Id(2), V);
        dir.JoinByCode(Id(2), Info(2), code);
        dir.JoinByCode(Id(3), Info(3), code);
        long epoch = State(2).GetProperty("leadershipEpoch").GetInt64();

        dir.Disconnected(Id(1));
        Assert.Equal(2, MemberCount(2)); // active membership removed at once; no reserved seat
        Assert.Equal(Id(1), State(2).GetProperty("leaderId").GetString());
        Assert.NotEqual(JsonValueKind.Null, State(2).GetProperty("leaderUnavailable").ValueKind);
        Assert.Equal("leader_unavailable", dir.SetIntent(Id(2), Campaign()).Error?.Code); // leader-only commits are disabled
        clock.Advance(TimeSpan.FromSeconds(14.9));
        dir.Tick();
        Assert.Equal(Id(1), State(2).GetProperty("leaderId").GetString());
        clock.Advance(TimeSpan.FromSeconds(0.1));
        dir.Tick();
        JsonElement s = State(2);
        Assert.Equal(Id(3), s.GetProperty("leaderId").GetString());
        Assert.Equal(epoch + 1, s.GetProperty("leadershipEpoch").GetInt64());
        Assert.Equal(ConvoyRules.LeaderLabel, s.GetProperty("leaderLabel").GetString());

        dir.Connected(Id(1), V); // the former leader returns: the old grant fails the epoch condition
        Assert.Equal("leader_changed", Rejoin(1).GetProperty("reason").GetString());
        Assert.Equal("rejoin_leader_changed", dir.Rejoin(Id(1), Info(1)).Error?.Code);
        Assert.Equal(Id(3), State(2).GetProperty("leaderId").GetString());
        Assert.Equal(2, MemberCount(2));
    }

    [Fact]
    public void LeaderTransfer_TieBreaksOnStableId()
    {
        dir.Connected(Id(5), V);
        dir.Connected(Id(9), V);
        dir.Connected(Id(4), V); // all three connect at the same instant
        dir.Create(Id(5), Info(5), ConvoyPrivacy.InviteOnly);
        string code = Code(dir.CreateInvite(Id(5)));
        dir.JoinByCode(Id(9), Info(9), code);
        dir.JoinByCode(Id(4), Info(4), code);
        dir.Disconnected(Id(5));
        clock.Advance(TimeSpan.FromSeconds(15));
        dir.Tick();
        Assert.Equal(Id(4), State(9).GetProperty("leaderId").GetString());
    }

    [Fact]
    public void HardMode_RequiresEveryMembersNormalFinale()
    {
        dir.Connected(Id(1), V);
        dir.Connected(Id(2), V);
        dir.Create(Id(1), new MemberInfo("Veteran", Progress(Id(1), normalCleared: 30)), ConvoyPrivacy.InviteOnly);
        dir.JoinByCode(Id(2), new MemberInfo("Newer", Progress(Id(2), normalCleared: 29)), Code(dir.CreateInvite(Id(1))));
        ConvoyResult r = dir.SetIntent(Id(1), Campaign("hard"));
        Assert.Equal("mode_locked", r.Error?.Code);
        Assert.False(State(1).GetProperty("campaignAccess").GetProperty("hard").GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public void CampaignStages_AreLimitedByTheSharedFrontier()
    {
        dir.Connected(Id(1), V);
        dir.Connected(Id(2), V);
        dir.Create(Id(1), new MemberInfo("A", Progress(Id(1), normalCleared: 11)), ConvoyPrivacy.InviteOnly);   // frontier 12
        dir.JoinByCode(Id(2), new MemberInfo("B", Progress(Id(2), normalCleared: 7)), Code(dir.CreateInvite(Id(1)))); // frontier 8
        EnterMode(Campaign());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("stage_locked", dir.ProposeEvent(Id(1), new EventRequest("S09", null, null, null, null, null, null)).Error?.Code);
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest("S08", null, null, null, null, null, null)).Ok);
        JsonElement access = State(1).GetProperty("campaignAccess").GetProperty("normal");
        Assert.Equal(8, access.GetProperty("maxSelectableStage").GetInt32());
        Assert.Contains("S08", access.GetProperty("explanation").GetString());
    }

    [Fact]
    public void EventProposal_NeedsAnEnteredMode()
    {
        Convoy(1);
        Assert.Equal("bad_phase", dir.ProposeEvent(Id(1), new EventRequest("S01", null, null, null, null, null, null)).Error?.Code);
        dir.SetIntent(Id(1), Campaign());
        Assert.Equal("bad_phase", dir.ProposeEvent(Id(1), new EventRequest("S01", null, null, null, null, null, null)).Error?.Code);
    }

    [Fact]
    public void Start_RevalidatesReadinessLoadoutsAndVersions_ThenFreezes()
    {
        Convoy(2);
        long rev = OpenEvent();
        ReadyAll(rev);

        Assert.Equal("not_leader", dir.BeginStart(Id(2), rev, Fresh(2)).Error?.Code);
        Assert.Equal("stale_revision", dir.BeginStart(Id(1), rev - 1, Fresh(2)).Error?.Code);

        // A member edits their car while the leader presses Start: the stale readiness cannot start the event.
        dir.UpdateLoadout(Id(2), Car(perf: "perf-9"));
        Assert.Equal("not_all_ready", dir.BeginStart(Id(1), rev, Fresh(2)).Error?.Code);
        Assert.True(dir.SetReady(Id(2), rev, 2, true).Ok);

        // One client on a different content version blocks allocation.
        dir.Connected(Id(2), new ClientVersion("build-1", 1, "other-content"));
        Assert.True(dir.SetReady(Id(2), rev, 2, true).Ok);
        Assert.Equal("version_mismatch", dir.BeginStart(Id(1), rev, Fresh(2)).Error?.Code);
        dir.Connected(Id(2), V);
        Assert.True(dir.SetReady(Id(2), rev, 2, true).Ok);

        (ConvoyError? error, MatchPlan? plan) = dir.BeginStart(Id(1), rev, Fresh(2));
        Assert.Null(error);
        Assert.Equal(2, plan!.Entrants.Count);
        Assert.Equal(TestData.Content.Catalogue.Stage("S01").Normal.Opponents, plan.AiEntrants); // authored live opposition (Addendum 01 §1.2)
        Assert.Equal(TestData.Content.Catalogue.Stage("S01").Normal.Lead, plan.AiEntrants[0]); // featured rival first
        Assert.Equal(plan.AiEntrants[0], plan.FeaturedRival);
        Assert.Equal("Allocating", State(1).GetProperty("phase").GetString());
        Assert.Equal("event_frozen", dir.UpdateLoadout(Id(2), Car(perf: "perf-10")).Error?.Code);
        Assert.Equal("event_frozen", dir.SetReady(Id(2), rev, 2, false).Error?.Code);
    }

    [Fact]
    public void Start_ReevaluatesStageAccess_WithFreshServerProgress()
    {
        Convoy(2, normalCleared: 2);
        long rev = OpenEvent(stage: "S03");
        ReadyAll(rev);
        var stale = Fresh(2, normalCleared: 2);
        stale[Id(2)] = Progress(Id(2), normalCleared: 1); // stored progress says member 2 cannot select S03
        Assert.Equal("stage_locked", dir.BeginStart(Id(1), rev, stale).Error?.Code);
    }

    [Fact]
    public void SixHumans_RaceTheLiveFeaturedRival_NoReplaySubstitute()
    {
        // Addendum 01 §1.3 supersedes the six-human benchmark replay: the featured rival is always a live car.
        Convoy(6);
        long rev = OpenEvent();
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(6)).Plan!;
        Assert.Equal(6, plan.Entrants.Count);
        Assert.Equal(TestData.Content.Catalogue.Stage("S01").Normal.Lead, plan.AiEntrants[0]);
        Assert.True(plan.Entrants.Count + plan.AiEntrants.Count <= Limits.MaxRaceVehicles);
        Assert.Equal(plan.Entrants.Count + plan.AiEntrants.Count, plan.Roster.Count);
        Assert.All(plan.Roster.Where(r => r.Kind == "ai"), r => Assert.Equal("opposing", r.Team));
    }

    [Fact]
    public void Leaving_PassesLeadership_IncrementsTheEpoch_AndTheLastMemberDisbandsTheConvoy()
    {
        Convoy(3);
        string code = Code(dir.CreateInvite(Id(1)));
        long epoch = State(1).GetProperty("leadershipEpoch").GetInt64();
        Assert.True(dir.Leave(Id(1)).Ok);
        Assert.Equal(Id(2), State(2).GetProperty("leaderId").GetString()); // connected earliest (tie → lowest ID)
        Assert.Equal(epoch + 1, State(2).GetProperty("leadershipEpoch").GetInt64());
        Assert.Equal("none", Rejoin(1).GetProperty("reason").GetString()); // an explicit Leave keeps no rejoin grant
        Assert.Equal("not_in_convoy", dir.Leave(Id(1)).Error?.Code);
        Assert.True(dir.Leave(Id(2)).Ok);
        Assert.True(dir.Leave(Id(3)).Ok);
        Assert.Empty(dir.ListDiscoverable());
        dir.Connected(Id(4), V);
        Assert.Equal("invite_invalid", dir.JoinByCode(Id(4), Info(4), code).Error?.Code); // invites die with the convoy
    }

    [Fact]
    public void Presence_IsCoarse_AndServerOnlyStatesCannotBeClaimed()
    {
        Convoy(2);
        Assert.True(dir.SetPresence(Id(2), Presence.AtMeet).Ok);
        Assert.Equal("AtMeet", MemberState(1, 2).GetProperty("presence").GetString());
        Assert.Equal("invalid_request", dir.SetPresence(Id(2), Presence.Reconnecting).Error?.Code);
        Assert.Equal("invalid_request", dir.SetPresence(Id(2), Presence.Offline).Error?.Code);
        Assert.False(State(1).GetRawText().Contains("balance", StringComparison.OrdinalIgnoreCase)); // no wallet in shared state
    }

    [Fact]
    public void Freeplay_AllowsAiUpToTwelveVehicles_ClampsStaleOverflow_AndFlagsPurePvP()
    {
        // Addendum 01 D01: 1–6 humans, at most 12 vehicles. With two humans the leader may ask for ten AI, not eleven.
        Convoy(2);
        EnterMode(Freeplay("sprint"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("capacity_exceeded", dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 11, null, null)).Error?.Code);
        long rev = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 10, null, null)), "proposalRevision");
        Assert.Equal(12, State(1).GetProperty("eventProposal").GetProperty("rosterPreview").GetProperty("vehicles").GetInt32());

        Join(3); // the request is now stale: three humans leave room for nine AI
        rev = State(1).GetProperty("eventProposal").GetProperty("revision").GetInt64();
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(3)).Plan!;
        Assert.Equal(9, plan.AiEntrants.Count); // clamped with an explanation, no human ejected
        Assert.Contains("12 cars", plan.GridNote);
        Assert.False(plan.PurePvP);

        dir.FailStart(plan, "test");
        clock.Advance(TimeSpan.FromSeconds(15));
        long pvp = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 0, null, null)), "proposalRevision");
        ReadyAll(pvp);
        Assert.True(dir.BeginStart(Id(1), pvp, Fresh(3)).Plan!.PurePvP);
    }

    [Fact]
    public void ConvoyRevisions_AreStrictlyMonotonic()
    {
        Convoy(3);
        long rev = OpenEvent();
        ReadyAll(rev);
        dir.Disconnected(Id(3));
        clock.Advance(TimeSpan.FromSeconds(61));
        dir.Tick();
        long[] revisions = notifier.Sent.Where(m => m.Account == Id(1) && m.Type == "convoy.state").Select(m => m.Revision).ToArray();
        Assert.True(revisions.Length > 10);
        Assert.True(revisions.Zip(revisions.Skip(1)).All(p => p.Second > p.First));
    }
}
