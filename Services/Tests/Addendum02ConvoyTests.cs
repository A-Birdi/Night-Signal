using System.Collections.Concurrent;
using System.Text.Json;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>Records convoy-session events (the extension point a future downtime/toy service will implement).</summary>
public sealed class RecordingObserver : IConvoySessionObserver
{
    public readonly ConcurrentQueue<string> Events = new();
    public readonly ConcurrentQueue<DormantRoomSnapshot> Snapshots = new();
    public void MembershipStarted(string sessionId, string accountId, long generation) => Events.Enqueue($"start {accountId[^2..]} g{generation}");
    public void MembershipEnded(string sessionId, string accountId, long generation, string reason) => Events.Enqueue($"end {accountId[^2..]} g{generation} {reason}");
    public void Preempted(string sessionId, long convoyRevision, string cause) => Events.Enqueue($"preempt {cause}");
    public void Dormant(DormantRoomSnapshot snapshot) { Snapshots.Enqueue(snapshot); Events.Enqueue("dormant"); }
    public void Restored(string sessionId) => Events.Enqueue("restored");
    public void Ended(string sessionId, string reason) => Events.Enqueue($"ended {reason}");
}

/// <summary>
/// Addendum 02 control-plane rules: Dormant rooms (D208), stable ConvoySessionId + membership generation (D207), diversion
/// participation that never clears readiness (§1.2), and the post-event Continue / Service Break decision (§7, D203).
/// </summary>
public sealed class Addendum02ConvoyTests : ConvoyTestBase
{
    readonly RecordingObserver observer = new();

    public Addendum02ConvoyTests()
    {
        dir = new ConvoyDirectory(clock, TestData.Content, notifier, TeamTrialCatalog.Fixture(TestData.Content.Catalogue), null, new[] { observer });
    }

    // ------------------------------------------------------------------ dormant rooms

    [Fact]
    public void AllMembersLostToDisconnection_MakesTheRoomDormant_WithItsGrants()
    {
        Convoy(2);
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1));
        Assert.Empty(dir.ListDiscoverable());
        JsonElement status = Rejoin(1);
        Assert.True(status.GetProperty("canRejoin").GetBoolean());
        Assert.True(status.GetProperty("dormant").GetBoolean());
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromHours(24), status.GetProperty("dormantExpiresAt").GetDateTimeOffset());
        DormantRoomSnapshot snap = observer.Snapshots.Last();
        Assert.Equal(new[] { Id(1), Id(2) }, snap.Grants.Select(g => g.AccountId));
        Assert.Equal(Id(1), snap.LeaderId);
    }

    [Fact]
    public void HeartbeatsAndReconnectAttempts_DoNotExtendExpiry_ButARejoinAt23HoursRestoresTheRoom()
    {
        Convoy(2);
        string session = State(1).GetProperty("convoySessionId").GetString()!;
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1));
        for (int hour = 1; hour <= 23; hour++)
        {
            clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromMinutes(hour == 23 ? 1 : 0));
            dir.Connected(Id(2), V); // background retries, heartbeats and status polls
            dir.Seen(Id(2));
            dir.RejoinStatus(Id(2));
            dir.Disconnected(Id(2));
            dir.Tick();
        }
        dir.Connected(Id(1), V);
        JsonElement back = Result(dir.Rejoin(Id(1), Info(1)));
        Assert.True(back.GetProperty("convoyId").GetString() is not null);
        JsonElement s = State(1);
        Assert.Equal(session, s.GetProperty("convoySessionId").GetString()); // same session, new active period
        Assert.Equal(Id(1), s.GetProperty("leaderId").GetString());
        Assert.Contains("restored", observer.Events);

        // The new active period has no hidden expiry: a later all-disconnect starts a fresh 24 h window.
        clock.Advance(TimeSpan.FromHours(2));
        dir.Tick();
        Assert.NotNull(dir.SnapshotFor(Id(1)).Snapshot);
    }

    [Fact]
    public void AfterTwentyFourHours_TheDormantRoomExpires_AndRejoinIsRefused()
    {
        Convoy(2);
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1));
        clock.Advance(TimeSpan.FromHours(24));
        dir.Connected(Id(2), V);
        Assert.Equal("disbanded", Rejoin(2).GetProperty("reason").GetString());
        Assert.Equal("rejoin_disbanded", dir.Rejoin(Id(2), Info(2)).Error?.Code);
        dir.Tick();
        Assert.Contains("ended dormant-expired", observer.Events);
    }

    [Fact]
    public void ALastVoluntaryLeave_EndsTheSessionAtOnce()
    {
        Convoy(2);
        dir.Disconnected(Id(2)); // holds a grant
        Assert.True(dir.Leave(Id(1)).Ok);
        Assert.DoesNotContain("dormant", observer.Events);
        Assert.Contains("ended last-member-left", observer.Events);
        dir.Connected(Id(2), V);
        Assert.Equal("disbanded", Rejoin(2).GetProperty("reason").GetString());
    }

    [Fact]
    public void ExplicitDisband_EndsTheSession_ForEveryone()
    {
        Convoy(3);
        dir.Disconnected(Id(3));
        Assert.Equal("not_leader", dir.DisbandByLeader(Id(2)).Error?.Code);
        Assert.True(dir.DisbandByLeader(Id(1)).Ok);
        Assert.Null(dir.SnapshotFor(Id(2)).Snapshot);
        Assert.Contains(notifier.To(Id(2), "convoy.closed"), m => m.GetProperty("reason").GetString() == "disbanded");
        dir.Connected(Id(3), V);
        Assert.Equal("rejoin_disbanded", dir.Rejoin(Id(3), Info(3)).Error?.Code);
        Assert.Contains("ended disbanded", observer.Events);
    }

    [Fact]
    public void ADormantRoom_StillAppliesTheLeadershipEpochRules()
    {
        Convoy(2);
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1)); // the leader was the last one lost
        dir.Connected(Id(2), V);
        Assert.True(dir.Rejoin(Id(2), Info(2)).Ok); // restored by a non-leader: the absent leader gets the ordinary grace
        Assert.NotEqual(JsonValueKind.Null, State(2).GetProperty("leaderUnavailable").ValueKind);
        clock.Advance(TimeSpan.FromSeconds(15));
        dir.Tick();
        Assert.Equal(Id(2), State(2).GetProperty("leaderId").GetString());
        dir.Connected(Id(1), V);
        Assert.Equal("rejoin_leader_changed", dir.Rejoin(Id(1), Info(1)).Error?.Code);
    }

    [Fact]
    public void DormantRooms_AreNotJoinableByOutsiders()
    {
        dir.Connected(Id(1), V);
        dir.Create(Id(1), Info(1), ConvoyPrivacy.Discoverable);
        string convoyId = State(1).GetProperty("convoyId").GetString()!;
        dir.Disconnected(Id(1));
        dir.Connected(Id(5), V);
        Assert.Equal("convoy_dormant", dir.JoinDiscoverable(Id(5), Info(5), convoyId).Error?.Code);
    }

    [Fact]
    public void DormantSnapshots_RestoreHonestlyAfterARestart_AndExpiredOnesDoNot()
    {
        Convoy(2);
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1));
        DormantRoomSnapshot snap = observer.Snapshots.Last();

        var restarted = new ConvoyDirectory(clock, TestData.Content, new RecordingNotifier());
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(1, restarted.RestoreDormant(new[] { snap }));
        restarted.Connected(Id(2), V);
        Assert.True(JsonSerializer.SerializeToElement(restarted.RejoinStatus(Id(2)), Web).GetProperty("canRejoin").GetBoolean());
        Assert.True(restarted.Rejoin(Id(2), Info(2)).Ok);
        Assert.Equal(snap.SessionId, JsonSerializer.SerializeToElement(restarted.SnapshotFor(Id(2)).Snapshot, Web).GetProperty("convoySessionId").GetString());

        var late = new ConvoyDirectory(clock, TestData.Content, new RecordingNotifier());
        clock.Advance(TimeSpan.FromHours(21)); // 24 h after the loss
        Assert.Equal(0, late.RestoreDormant(new[] { snap }));
    }

    [Fact]
    public async Task DormantRoomStore_RoundTrips_AndRetiresExpiredRooms()
    {
        using var temp = new TempDir();
        var store = new SqliteGameStore(temp.File("dormant.db"));
        await store.InitializeAsync();
        Convoy(2);
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1));
        DormantRoomSnapshot snap = observer.Snapshots.Last();
        await store.SaveDormantRoomAsync(snap);
        await store.SaveDormantRoomAsync(snap with { LeaderName = "renamed" }); // upsert, one row
        DormantRoomSnapshot loaded = Assert.Single(await store.LoadDormantRoomsAsync(clock.GetUtcNow()));
        Assert.Equal(snap.Grants, loaded.Grants);
        Assert.Equal("renamed", loaded.LeaderName);
        Assert.Empty(await store.LoadDormantRoomsAsync(clock.GetUtcNow() + TimeSpan.FromHours(24)));
        Assert.Empty(await store.LoadDormantRoomsAsync(clock.GetUtcNow()));
    }

    // ------------------------------------------------------------------ session ID and membership generation

    [Fact]
    public void ConvoySessionId_IsStable_AndEveryMembershipGetsANewGeneration()
    {
        Convoy(3);
        string session = State(1).GetProperty("convoySessionId").GetString()!;
        long gen3 = MemberState(1, 3).GetProperty("membershipGeneration").GetInt64();
        Assert.True(dir.IsCurrentMembership(session, Id(3), gen3));

        dir.Disconnected(Id(3));
        Assert.False(dir.IsCurrentMembership(session, Id(3), gen3));
        dir.Connected(Id(3), V);
        Assert.True(dir.Rejoin(Id(3), Info(3)).Ok);
        long regen = MemberState(1, 3).GetProperty("membershipGeneration").GetInt64();
        Assert.True(regen > gen3);
        Assert.False(dir.IsCurrentMembership(session, Id(3), gen3)); // stale input rights are never inherited
        Assert.Equal((session, regen), dir.MembershipOf(Id(3)));

        Assert.True(dir.Leave(Id(1)).Ok); // leadership change and epoch increment: same session
        Assert.Equal(session, State(2).GetProperty("convoySessionId").GetString());
        Assert.Contains($"end {Id(3)[^2..]} g{gen3} disconnected", observer.Events);
        Assert.Contains($"end {Id(1)[^2..]} g1 left", observer.Events);
    }

    [Fact]
    public void ModeEntryAndRaceAllocation_AreThePreemptionBoundaries()
    {
        Convoy(2);
        long rev = OpenEvent();
        Assert.Contains("preempt mode-entered", observer.Events);
        ReadyAll(rev);
        Assert.NotNull(dir.BeginStart(Id(1), rev, Fresh(2)).Plan);
        Assert.Contains("preempt race-allocation", observer.Events);
    }

    // ------------------------------------------------------------------ diversions never clear readiness

    [Fact]
    public void EnteringChangingOrLeavingADiversion_KeepsModeReadyAndEventReady()
    {
        Convoy(3);
        long rev = OpenEvent();
        ReadyAll(rev);
        long mode = State(1).GetProperty("modeRevision").GetInt64();
        Assert.True(dir.SetDiversion(Id(2), "cap-clash").Ok);
        Assert.True(dir.SetDiversion(Id(2), "pocket-circuit").Ok);
        Assert.True(dir.SetPresence(Id(2), Presence.Garage).Ok);
        Assert.True(Ready(2) && Ready(1) && Ready(3));
        Assert.True(MemberState(1, 2).GetProperty("modeReady").GetBoolean());
        Assert.Equal(mode, State(1).GetProperty("modeRevision").GetInt64());
        Assert.Equal(rev, State(1).GetProperty("eventProposal").GetProperty("revision").GetInt64());
        Assert.Equal("pocket-circuit", MemberState(3, 2).GetProperty("diversion").GetString());
        Assert.True(dir.SetDiversion(Id(2), null).Ok);
        Assert.True(Ready(2));
        Assert.Equal("invalid_request", dir.SetDiversion(Id(2), "mystery-garage").Error?.Code); // rejected by the addendum
        Assert.NotNull(dir.BeginStart(Id(1), rev, Fresh(3)).Plan); // still startable: no toy blocks or auto-readies anyone
    }

    // ------------------------------------------------------------------ post-event decision

    /// <summary>Runs the open campaign proposal to settlement; <paramref name="clearedAfter"/> is each member's Normal clear count afterwards.</summary>
    void RaceAndSettle(int members, string stage, Func<int, int> clearedAfter, int normalBefore = 0, int hardBefore = 0, int hardAfter = 0)
    {
        long rev = State(1).GetProperty("eventProposal").GetProperty("revision").GetInt64();
        ReadyAll(rev);
        (ConvoyError? error, MatchPlan? plan) = dir.BeginStart(Id(1), rev, Fresh(members, normalBefore, hardBefore));
        Assert.True(plan is not null, error?.Message);
        dir.CompleteStart(plan!, new ActiveMatch("m_pe", "srv", "h", 1, V, plan!.Entrants.Select(e => e.AccountId).ToList()));
        dir.MatchEnded(plan.ConvoyId, "m_pe", Enumerable.Range(1, members).ToDictionary(Id, i => Progress(Id(i), clearedAfter(i), hardAfter)));
    }

    JsonElement Decision() => State(1).GetProperty("postEvent");

    long DestinationRevision() => Decision().GetProperty("destinationRevision").GetInt64();

    [Fact]
    public void UnanimousContinue_EnablesAdvance_IntoTheBriefing_WhichStillNeedsEventReady()
    {
        Convoy(3);
        OpenEvent("S01");
        RaceAndSettle(3, "S01", _ => 1);
        JsonElement d = Decision();
        Assert.Equal("m_pe", d.GetProperty("sourceResultId").GetString());
        Assert.Equal("next-stage", d.GetProperty("destination").GetProperty("kind").GetString());
        Assert.Equal("S02", d.GetProperty("destination").GetProperty("stageId").GetString());
        Assert.Equal(3, d.GetProperty("undecidedCount").GetInt32());
        long dr = DestinationRevision();

        Assert.Equal("not_leader", dir.AdvancePostEvent(Id(2), dr).Error?.Code);
        Assert.Equal("not_all_continue", dir.AdvancePostEvent(Id(1), dr).Error?.Code);
        Assert.True(dir.ChoosePostEvent(Id(2), dr, "continue").Ok);
        Assert.Equal("stale_revision", dir.ChoosePostEvent(Id(3), dr - 1, "continue").Error?.Code);
        Assert.True(dir.ChoosePostEvent(Id(3), dr, "continue").Ok);
        Assert.True(Decision().GetProperty("advanceEnabled").GetBoolean());

        JsonElement advanced = Result(dir.AdvancePostEvent(Id(1), dr));
        Assert.Equal("S02", advanced.GetProperty("stageId").GetString());
        JsonElement s = State(1);
        Assert.Equal("ReadyCheck", s.GetProperty("phase").GetString());
        Assert.Equal("post-event", s.GetProperty("eventProposal").GetProperty("origin").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("postEvent").ValueKind);
        long proposal = s.GetProperty("eventProposal").GetProperty("revision").GetInt64();
        Assert.Equal("not_all_ready", dir.BeginStart(Id(1), proposal, Fresh(3, 1)).Error?.Code); // no automatic race
    }

    [Fact]
    public void FiveContinuePlusOneServiceBreak_IsAnIntermission_NeverAForcedStart()
    {
        Convoy(6);
        OpenEvent("S01");
        RaceAndSettle(6, "S01", _ => 1);
        long dr = DestinationRevision();
        for (int i = 2; i <= 5; i++) Assert.True(dir.ChoosePostEvent(Id(i), dr, "continue").Ok);
        Assert.True(dir.ChoosePostEvent(Id(6), dr, "service-break").Ok);
        Assert.Equal("intermission", Decision().GetProperty("state").GetString());
        Assert.Equal(1, NoticeCount(2, "service_break"));
        Assert.Equal("not_all_continue", dir.AdvancePostEvent(Id(1), dr).Error?.Code);

        // Garage time, a toy, the meet: nobody else's Continue has to be repeated while the destination is unchanged.
        dir.SetPresence(Id(6), Presence.Garage);
        dir.SetDiversion(Id(3), "greenlight");
        clock.Advance(TimeSpan.FromMinutes(5));
        dir.Tick();
        Assert.Equal(4, Decision().GetProperty("continueCount").GetInt32());
        Assert.True(dir.ChoosePostEvent(Id(6), dr, "continue").Ok);
        Assert.True(dir.AdvancePostEvent(Id(1), dr).Ok);
    }

    [Fact]
    public void SilenceIsNeverConsent_TheStripOnlyCalmsDownAfter30Seconds()
    {
        Convoy(2);
        OpenEvent("S01");
        RaceAndSettle(2, "S01", _ => 1);
        Assert.False(Decision().GetProperty("quiet").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(31));
        dir.Tick();
        JsonElement d = Decision();
        Assert.True(d.GetProperty("quiet").GetBoolean());
        Assert.Equal(2, d.GetProperty("undecidedCount").GetInt32());
        Assert.False(d.GetProperty("advanceEnabled").GetBoolean());
        Assert.Equal(2, MemberCount(1)); // no auto-kick
        Assert.Equal("not_all_continue", dir.AdvancePostEvent(Id(1), DestinationRevision()).Error?.Code);
    }

    [Fact]
    public void MixedClears_RetryTheLimitingStage_NamingWhoStillNeedsIt()
    {
        Convoy(3);
        OpenEvent("S01");
        RaceAndSettle(3, "S01", i => i == 3 ? 0 : 1);
        JsonElement dest = Decision().GetProperty("destination");
        Assert.Equal("retry-stage", dest.GetProperty("kind").GetString());
        Assert.Equal("S01", dest.GetProperty("stageId").GetString());
        Assert.Equal(new[] { Id(3) }, dest.GetProperty("needs").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void NormalFinale_ReturnsToTheMap_WithoutStartingHard()
    {
        Convoy(2, normalCleared: 29);
        OpenEvent("S30");
        RaceAndSettle(2, "S30", _ => 30, normalBefore: 29);
        JsonElement dest = Decision().GetProperty("destination");
        Assert.Equal("campaign-complete", dest.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, dest.GetProperty("stageId").ValueKind); // no S31
        long dr = DestinationRevision();
        dir.ChoosePostEvent(Id(2), dr, "continue");
        Assert.True(dir.AdvancePostEvent(Id(1), dr).Ok);
        JsonElement s = State(1);
        Assert.Equal("EventSelection", s.GetProperty("phase").GetString());
        Assert.Equal("normal", s.GetProperty("intent").GetProperty("mode").GetString()); // Hard needs its own explicit intent
        Assert.Equal(JsonValueKind.Null, s.GetProperty("eventProposal").ValueKind);
    }

    [Fact]
    public void HardFinale_OffersNoS31()
    {
        Convoy(1, normalCleared: 30, hardCleared: 29);
        OpenEvent("S30", mode: "hard");
        RaceAndSettle(1, "S30", _ => 30, normalBefore: 30, hardBefore: 29, hardAfter: 30);
        Assert.Equal("campaign-complete", Decision().GetProperty("destination").GetProperty("kind").GetString());
        Assert.True(Decision().GetProperty("solo").GetBoolean());
        Assert.True(dir.AdvancePostEvent(Id(1), DestinationRevision()).Ok); // solo: immediate, no poll
    }

    [Fact]
    public void Freeplay_ReturnsToEventSetup_AndSoloAdvancesImmediately()
    {
        Convoy(1);
        EnterMode(Freeplay("sprint"));
        long rev = ProposeFreeplay("C01", "sprint");
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(1)).Plan!;
        dir.CompleteStart(plan, new ActiveMatch("m_fp", "srv", "h", 1, V, new[] { Id(1) }));
        dir.MatchEnded(plan.ConvoyId, "m_fp", null);
        Assert.Equal("event-setup", Decision().GetProperty("destination").GetProperty("kind").GetString());
        Assert.Equal("post_event_open", dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 0, null, null)).Error?.Code);
        Assert.True(dir.AdvancePostEvent(Id(1), DestinationRevision()).Ok);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 0, null, null)).Ok); // existing selection applies
    }

    [Fact]
    public void ARosterChangeThatChangesTheDestination_ResetsChoicesOnce()
    {
        Convoy(2);
        OpenEvent("S01");
        RaceAndSettle(2, "S01", _ => 1);
        long dr = DestinationRevision();
        dir.ChoosePostEvent(Id(2), dr, "continue");
        Join(3); // a newcomer who has not cleared S01: the shared frontier now needs a retry
        JsonElement d = Decision();
        Assert.Equal("retry-stage", d.GetProperty("destination").GetProperty("kind").GetString());
        Assert.NotEqual(dr, d.GetProperty("destinationRevision").GetInt64());
        Assert.Equal(0, d.GetProperty("continueCount").GetInt32());
        Assert.Equal(1, NoticeCount(2, "post_event_changed"));
        Assert.Equal("stale_revision", dir.AdvancePostEvent(Id(1), dr).Error?.Code);
    }

    [Fact]
    public void ARosterChangeThatKeepsTheDestination_KeepsTheOthersContinue()
    {
        Convoy(3);
        OpenEvent("S01");
        RaceAndSettle(3, "S01", _ => 1);
        long dr = DestinationRevision();
        dir.ChoosePostEvent(Id(2), dr, "continue");
        dir.ChoosePostEvent(Id(3), dr, "service-break");
        Assert.True(dir.Leave(Id(3)).Ok);
        JsonElement d = Decision();
        Assert.Equal(dr, d.GetProperty("destinationRevision").GetInt64());
        Assert.Equal(1, d.GetProperty("continueCount").GetInt32());
        Assert.Equal("deciding", d.GetProperty("state").GetString());
        Assert.True(dir.AdvancePostEvent(Id(1), dr).Ok);
    }

    [Fact]
    public void ChoiceChanges_AreBoundedAgainstSpam()
    {
        Convoy(2);
        OpenEvent("S01");
        RaceAndSettle(2, "S01", _ => 1);
        long dr = DestinationRevision();
        for (int i = 0; i < ConvoyRules.PostEventChoiceLimit; i++)
            Assert.True(dir.ChoosePostEvent(Id(2), dr, i % 2 == 0 ? "continue" : "service-break").Ok);
        Assert.Equal("rate_limited", dir.ChoosePostEvent(Id(2), dr, "continue").Error?.Code);
        clock.Advance(ConvoyRules.PostEventChoiceWindow);
        Assert.True(dir.ChoosePostEvent(Id(2), dr, "continue").Ok);
        Assert.Equal("invalid_request", dir.ChoosePostEvent(Id(2), dr, "maybe").Error?.Code);
    }

    [Fact]
    public void LeaderLossDuringTheDecision_KeepsResultsChoicesAndDestination()
    {
        Convoy(3);
        OpenEvent("S01");
        RaceAndSettle(3, "S01", _ => 1);
        long dr = DestinationRevision();
        dir.ChoosePostEvent(Id(2), dr, "continue");
        dir.ChoosePostEvent(Id(3), dr, "continue");
        dir.Disconnected(Id(1));
        clock.Advance(TimeSpan.FromSeconds(15));
        dir.Tick();
        Assert.Equal(Id(2), State(2).GetProperty("leaderId").GetString());
        JsonElement d = State(2).GetProperty("postEvent");
        Assert.Equal(dr, d.GetProperty("destinationRevision").GetInt64());
        Assert.True(d.GetProperty("advanceEnabled").GetBoolean()); // the new leader's Advance counts as their Continue
        Assert.True(dir.AdvancePostEvent(Id(2), dr).Ok);
    }

    [Fact]
    public void AnAbortedEvent_OpensNoDecision_AndANewIntentClosesAnOpenOne()
    {
        Convoy(2);
        long rev = OpenEvent("S01");
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(2)).Plan!;
        dir.CompleteStart(plan, new ActiveMatch("m_ab", "srv", "h", 1, V, new[] { Id(1), Id(2) }));
        dir.MatchAborted(plan.ConvoyId, "m_ab", "server lost");
        Assert.Equal(JsonValueKind.Null, State(1).GetProperty("postEvent").ValueKind);

        clock.Advance(TimeSpan.FromSeconds(15));
        dir.ProposeEvent(Id(1), new EventRequest("S01", null, null, null, null, null, null));
        RaceAndSettle(2, "S01", _ => 1);
        Assert.NotEqual(JsonValueKind.Null, State(1).GetProperty("postEvent").ValueKind);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(dir.SetIntent(Id(1), Freeplay("sprint")).Ok);
        Assert.Equal(JsonValueKind.Null, State(1).GetProperty("postEvent").ValueKind);
    }
}
