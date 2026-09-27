using System.Collections.Concurrent;
using System.Text.Json;
using NightSignal.ControlPlane.Convoys;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>Convoy rules (spec §4.1, §4.2, §4.4, §5.1) with an injectable clock.</summary>
public sealed class ConvoyDirectoryTests
{
    sealed class RecordingNotifier : IConvoyNotifier
    {
        public readonly ConcurrentQueue<(string Account, string Type, long Revision, object Payload)> Sent = new();
        public void Send(string accountId, string type, long revision, object payload) => Sent.Enqueue((accountId, type, revision, payload));
    }

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    static readonly ClientVersion V = new("build-1", 1, "content-1");
    readonly ManualClock clock = new(new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero));
    readonly RecordingNotifier notifier = new();
    readonly ConvoyDirectory dir;

    public ConvoyDirectoryTests() => dir = new ConvoyDirectory(clock, TestData.Content, notifier);

    static string Id(int i) => $"00000000-0000-4000-8000-{i:000000000000}";

    static MemberProgress Progress(string id, int normalCleared = 0, int hardCleared = 0) =>
        new(id, Enumerable.Range(0, 30).Select(i => i < normalCleared).ToArray(), Enumerable.Range(0, 30).Select(i => i < hardCleared).ToArray());

    static MemberInfo Info(int i, int normalCleared = 0) => new($"Driver {i}", Progress(Id(i), normalCleared));

    static long Value(ConvoyResult r, string name)
    {
        Assert.True(r.Ok, r.Error?.Message);
        return JsonSerializer.SerializeToElement(r.Value).GetProperty(name).GetInt64();
    }

    JsonElement State(int member) => JsonSerializer.SerializeToElement(dir.SnapshotFor(Id(member)).Snapshot, Web);

    JsonElement MemberState(int viewer, int member) =>
        State(viewer).GetProperty("members").EnumerateArray().Single(m => m.GetProperty("accountId").GetString() == Id(member));

    static LoadoutInfo Car(string perf = "perf-1", string cosmetic = "cos-1", string car = "V01", int pi = 220) => new(car, pi, perf, cosmetic);

    /// <summary>Leader 1 plus members 2..n, each connected and with a car.</summary>
    void Convoy(int members, int normalCleared = 0)
    {
        for (int i = 1; i <= members; i++) dir.Connected(Id(i), V);
        Assert.True(dir.Create(Id(1), Info(1, normalCleared), ConvoyPrivacy.InviteOnly).Ok);
        string code = Code(dir.CreateInvite(Id(1)));
        for (int i = 2; i <= members; i++) Assert.True(dir.JoinByCode(Id(i), Info(i, normalCleared), code).Ok);
        for (int i = 1; i <= members; i++) Assert.True(dir.UpdateLoadout(Id(i), Car()).Ok);
    }

    static string Code(ConvoyResult r)
    {
        Assert.True(r.Ok, r.Error?.Message);
        return JsonSerializer.SerializeToElement(r.Value).GetProperty("code").GetString()!;
    }

    /// <summary>Commits campaign-normal and opens an event proposal; returns its revision.</summary>
    long OpenEvent(int members, string stage = "S01", string destination = "campaign-normal")
    {
        long dRev = Value(dir.ProposeDestination(Id(1), ConvoyRules.ParseDestination(destination)!.Value), "proposalRevision");
        for (int i = 2; i <= members; i++) Assert.True(dir.Consent(Id(i), dRev, true).Ok);
        Assert.True(dir.CommitDestination(Id(1), dRev).Ok);
        clock.Advance(TimeSpan.FromSeconds(15));
        return Value(dir.ProposeEvent(Id(1), new EventRequest(stage, null, null, null, null, null, null)), "proposalRevision");
    }

    void ReadyAll(int members, long rev)
    {
        for (int i = 1; i <= members; i++)
            Assert.True(dir.SetReady(Id(i), rev, MemberState(i, i).GetProperty("loadoutRevision").GetInt64(), true).Ok);
    }

    bool Ready(int member) => MemberState(1, member).GetProperty("eventReady").GetBoolean();

    Dictionary<string, MemberProgress> Fresh(int members, int normalCleared = 0) =>
        Enumerable.Range(1, members).ToDictionary(Id, i => Progress(Id(i), normalCleared));

    [Fact]
    public void ConvoyHoldsAtMostSixMembers()
    {
        Convoy(6);
        dir.Connected(Id(7), V);
        string code = Code(dir.CreateInvite(Id(1)));
        ConvoyResult r = dir.JoinByCode(Id(7), Info(7), code);
        Assert.Equal("convoy_full", r.Error?.Code);
        Assert.Equal(6, State(1).GetProperty("members").GetArrayLength());
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
    public void DestinationCommit_NeedsEveryConnectedMembersConsent_ToThatRevision()
    {
        Convoy(3);
        long rev = Value(dir.ProposeDestination(Id(1), Destination.CampaignNormal), "proposalRevision");
        Assert.Equal("not_all_ready", dir.CommitDestination(Id(1), rev).Error?.Code);
        Assert.True(dir.Consent(Id(2), rev, true).Ok);
        Assert.Equal("stale_revision", dir.Consent(Id(3), rev - 1, true).Error?.Code);
        Assert.Equal("not_all_ready", dir.CommitDestination(Id(1), rev).Error?.Code);
        dir.Disconnected(Id(3)); // a disconnected member is not required to consent
        Assert.Equal("not_leader", dir.CommitDestination(Id(2), rev).Error?.Code);
        Assert.True(dir.CommitDestination(Id(1), rev).Ok);
        Assert.Equal("campaign-normal", State(1).GetProperty("committedDestination").GetString());
    }

    [Fact]
    public void LeaderMayRequestReadiness_AtMostOncePer15Seconds()
    {
        Convoy(2);
        Assert.True(dir.ProposeDestination(Id(1), Destination.Freeplay).Ok);
        ConvoyResult again = dir.ProposeDestination(Id(1), Destination.CampaignNormal);
        Assert.Equal("rate_limited", again.Error?.Code);
        Assert.InRange(again.Error!.RetryAfterMs!.Value, 1, 15_000);
        clock.Advance(TimeSpan.FromSeconds(14.9));
        Assert.Equal("rate_limited", dir.ProposeDestination(Id(1), Destination.CampaignNormal).Error?.Code);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(dir.ProposeDestination(Id(1), Destination.CampaignNormal).Ok);
    }

    [Fact]
    public void StaleReadyMessages_AreRejected()
    {
        Convoy(2);
        long first = OpenEvent(2);
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
        long rev = OpenEvent(3);
        ReadyAll(3, rev);
        Assert.True(Ready(1) && Ready(2) && Ready(3));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest("S01", null, null, "fog", null, null, null)).Ok);
        Assert.False(Ready(1) || Ready(2) || Ready(3));
    }

    [Fact]
    public void PerformanceLoadoutChange_UnreadiesOnlyThatMember_CosmeticChangeDoesNot()
    {
        Convoy(3);
        long rev = OpenEvent(3);
        ReadyAll(3, rev);

        Assert.True(dir.UpdateLoadout(Id(2), Car(perf: "perf-1", cosmetic: "new-livery")).Ok);
        Assert.True(Ready(1) && Ready(2) && Ready(3));

        Assert.True(dir.UpdateLoadout(Id(3), Car(perf: "perf-2")).Ok);
        Assert.True(Ready(1) && Ready(2));
        Assert.False(Ready(3));
        Assert.Equal(2, MemberState(1, 3).GetProperty("loadoutRevision").GetInt64());
        Assert.Equal(rev, State(1).GetProperty("eventProposal").GetProperty("revision").GetInt64()); // proposal itself unchanged
    }

    [Fact]
    public void RosterChange_IssuesNewRevisions_AndUnreadiesEveryone()
    {
        Convoy(2);
        long rev = OpenEvent(2);
        ReadyAll(2, rev);
        long roster = State(1).GetProperty("rosterRevision").GetInt64();
        dir.Connected(Id(3), V);
        Assert.True(dir.JoinByCode(Id(3), Info(3), Code(dir.CreateInvite(Id(1)))).Ok);
        JsonElement s = State(1);
        Assert.Equal(roster + 1, s.GetProperty("rosterRevision").GetInt64());
        Assert.NotEqual(rev, s.GetProperty("eventProposal").GetProperty("revision").GetInt64());
        Assert.False(Ready(1) || Ready(2));
        Assert.Equal("stale_revision", dir.SetReady(Id(1), rev, 1, true).Error?.Code);
    }

    [Fact]
    public void MembersWithoutInteraction_For120Seconds_BecomeAwayAndUnready()
    {
        Convoy(2);
        long rev = OpenEvent(2);
        ReadyAll(2, rev);
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
    public void LeaderTransfersAfter15s_ToLongestConnected_AndDoesNotReturn()
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

        dir.Disconnected(Id(1));
        clock.Advance(TimeSpan.FromSeconds(14.9));
        dir.Tick();
        Assert.Equal(Id(1), State(2).GetProperty("leaderId").GetString());
        Assert.Equal("Reconnecting", MemberState(2, 1).GetProperty("presence").GetString());
        clock.Advance(TimeSpan.FromSeconds(0.1));
        dir.Tick();
        Assert.Equal(Id(3), State(2).GetProperty("leaderId").GetString());
        Assert.Equal(ConvoyRules.LeaderLabel, State(2).GetProperty("leaderLabel").GetString());

        dir.Connected(Id(1), V); // the former leader returns within the 60 s hold
        dir.Tick();
        Assert.Equal(Id(3), State(1).GetProperty("leaderId").GetString());
        Assert.Equal(3, State(1).GetProperty("members").GetArrayLength());
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
    public void DisconnectedSlot_IsReservedFor60Seconds()
    {
        Convoy(3);
        dir.Disconnected(Id(3));
        clock.Advance(TimeSpan.FromSeconds(59));
        dir.Tick();
        Assert.Equal(3, State(1).GetProperty("members").GetArrayLength());
        Assert.Equal("reconnecting", MemberState(1, 3).GetProperty("connection").GetString());
        clock.Advance(TimeSpan.FromSeconds(1));
        dir.Tick();
        Assert.Equal(2, State(1).GetProperty("members").GetArrayLength());
        Assert.Contains(notifier.Sent, m => m.Account == Id(3) && m.Type == "convoy.closed");
        Assert.Null(dir.SnapshotFor(Id(3)).Snapshot);
    }

    [Fact]
    public void HardMode_RequiresEveryMembersNormalFinale()
    {
        dir.Connected(Id(1), V);
        dir.Connected(Id(2), V);
        dir.Create(Id(1), new MemberInfo("Veteran", Progress(Id(1), normalCleared: 30)), ConvoyPrivacy.InviteOnly);
        dir.JoinByCode(Id(2), new MemberInfo("Newer", Progress(Id(2), normalCleared: 29)), Code(dir.CreateInvite(Id(1))));
        ConvoyResult r = dir.ProposeDestination(Id(1), Destination.CampaignHard);
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
        long d = Value(dir.ProposeDestination(Id(1), Destination.CampaignNormal), "proposalRevision");
        dir.Consent(Id(2), d, true);
        dir.CommitDestination(Id(1), d);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("stage_locked", dir.ProposeEvent(Id(1), new EventRequest("S09", null, null, null, null, null, null)).Error?.Code);
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest("S08", null, null, null, null, null, null)).Ok);
        JsonElement access = State(1).GetProperty("campaignAccess").GetProperty("normal");
        Assert.Equal(8, access.GetProperty("maxSelectableStage").GetInt32());
        Assert.Contains("S08", access.GetProperty("explanation").GetString());
    }

    [Fact]
    public void Start_RevalidatesReadinessLoadoutsAndVersions_ThenFreezes()
    {
        Convoy(2);
        long rev = OpenEvent(2);
        ReadyAll(2, rev);

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
        Assert.Equal("Allocating", State(1).GetProperty("phase").GetString());
        Assert.Equal("event_frozen", dir.UpdateLoadout(Id(2), Car(perf: "perf-10")).Error?.Code);
        Assert.Equal("event_frozen", dir.SetReady(Id(2), rev, 2, false).Error?.Code);
    }

    [Fact]
    public void Start_ReevaluatesStageAccess_WithFreshServerProgress()
    {
        Convoy(2, normalCleared: 2);
        long rev = OpenEvent(2, stage: "S03");
        ReadyAll(2, rev);
        var stale = Fresh(2, normalCleared: 2);
        stale[Id(2)] = Progress(Id(2), normalCleared: 1); // stored progress says member 2 cannot select S03
        Assert.Equal("stage_locked", dir.BeginStart(Id(1), rev, stale).Error?.Code);
    }

    [Fact]
    public void SixHumans_RaceTheLiveFeaturedRival_NoReplaySubstitute()
    {
        // Addendum 01 §1.3 supersedes the six-human benchmark replay: the featured rival is always a live car.
        Convoy(6);
        long rev = OpenEvent(6);
        ReadyAll(6, rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(6)).Plan!;
        Assert.Equal(6, plan.Entrants.Count);
        Assert.Equal(TestData.Content.Catalogue.Stage("S01").Normal.Lead, plan.AiEntrants[0]);
        Assert.True(plan.Entrants.Count + plan.AiEntrants.Count <= Limits.MaxRaceVehicles);
    }

    [Fact]
    public void Leaving_PassesLeadership_AndTheLastMemberDisbandsTheConvoy()
    {
        Convoy(3);
        string code = Code(dir.CreateInvite(Id(1)));
        Assert.True(dir.Leave(Id(1)).Ok);
        Assert.Equal(Id(2), State(2).GetProperty("leaderId").GetString()); // connected earliest (tie → lowest ID)
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
        // Addendum 01 D01: 1–6 humans, at most 12 vehicles. 11 AI is the most any request may ask for.
        Convoy(2);
        long d = Value(dir.ProposeDestination(Id(1), Destination.Freeplay), "proposalRevision");
        dir.Consent(Id(2), d, true);
        dir.CommitDestination(Id(1), d);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), new EventRequest(null, "C05", "sprint", null, 12, null, null)).Error?.Code);
        long rev = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C05", "sprint", null, 11, null, null)), "proposalRevision");
        ReadyAll(2, rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(2)).Plan!;
        Assert.Equal(10, plan.AiEntrants.Count); // 2 humans + 10 AI = 12 vehicles; the stale request for 11 is clamped
        Assert.Contains("12 cars", plan.GridNote);
        Assert.False(plan.PurePvP);

        dir.FailStart(plan, "test");
        clock.Advance(TimeSpan.FromSeconds(15));
        long pvp = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C05", "sprint", null, 0, null, null)), "proposalRevision");
        ReadyAll(2, pvp);
        Assert.True(dir.BeginStart(Id(1), pvp, Fresh(2)).Plan!.PurePvP);
    }

    [Fact]
    public void ConvoyRevisions_AreStrictlyMonotonic()
    {
        Convoy(3);
        long rev = OpenEvent(3);
        ReadyAll(3, rev);
        dir.Disconnected(Id(3));
        clock.Advance(TimeSpan.FromSeconds(61));
        dir.Tick();
        long[] revisions = notifier.Sent.Where(m => m.Account == Id(1) && m.Type == "convoy.state").Select(m => m.Revision).ToArray();
        Assert.True(revisions.Length > 10);
        Assert.True(revisions.Zip(revisions.Skip(1)).All(p => p.Second > p.First));
    }
}
