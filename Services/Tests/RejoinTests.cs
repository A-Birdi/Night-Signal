using System.Text.Json;
using NightSignal.ControlPlane.Convoys;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Addendum 01 §10 / D09 rejoin matrix: confirmed disconnect removes ACTIVE membership (no reserved seat) and records a
/// server-owned grant keyed to the leadership epoch. Valid same-epoch rejoin works; Full, LeaderChanged, Disbanded and
/// Revoked are distinct; no hidden time expiry; no seventh human; a DQ'd entrant never resumes driving.
/// </summary>
public sealed class RejoinTests : ConvoyTestBase
{
    [Fact]
    public void ConfirmedDisconnect_RemovesActiveMembershipReadinessAndBallot_AndRecordsOneGrant()
    {
        Convoy(3);
        long rev = OpenEvent();
        ReadyAll(rev);
        long roster = State(1).GetProperty("rosterRevision").GetInt64();
        int statesBefore = notifier.To(Id(1), "convoy.state").Count();

        dir.Disconnected(Id(3));

        JsonElement s = State(1);
        Assert.Equal(2, MemberCount(1));                                  // seat released at once, nothing reserved
        Assert.Equal(roster + 1, s.GetProperty("rosterRevision").GetInt64()); // exactly one roster change
        Assert.Equal(statesBefore + 1, notifier.To(Id(1), "convoy.state").Count());
        Assert.False(Ready(1) || Ready(2));                               // all Event Ready renewed for the new roster
        Assert.Null(dir.SnapshotFor(Id(3)).Snapshot);
        JsonElement grant = Rejoin(3);
        Assert.True(grant.GetProperty("canRejoin").GetBoolean());
        Assert.Equal("eligible", grant.GetProperty("reason").GetString());
        Assert.Equal(s.GetProperty("convoyId").GetString(), grant.GetProperty("convoyId").GetString());
        Assert.Equal(s.GetProperty("leadershipEpoch").GetInt64(), grant.GetProperty("leadershipEpoch").GetInt64());
    }

    [Fact]
    public void ValidRejoin_WithTheSameEpoch_RestoresMembershipOnly_AndNotNowKeepsEligibility()
    {
        Convoy(3);
        long rev = OpenEvent();
        ReadyAll(rev);
        dir.Disconnected(Id(3));
        clock.Advance(TimeSpan.FromHours(6)); // no hidden 60-second (or any) expiry; transport tokens may expire meanwhile
        dir.Connected(Id(3), V);

        JsonElement status = Rejoin(3);
        Assert.True(status.GetProperty("prompt").GetBoolean());
        Assert.True(dir.DismissRejoin(Id(3), forget: false).Ok); // "Not Now"
        status = Rejoin(3);
        Assert.False(status.GetProperty("prompt").GetBoolean()); // no popup spam on reconnect retries
        Assert.True(status.GetProperty("canRejoin").GetBoolean()); // eligibility kept

        JsonElement joined = Result(dir.Rejoin(Id(3), Info(3)));
        Assert.False(joined.GetProperty("spectator").GetBoolean());
        Assert.Equal(3, MemberCount(1));
        Assert.False(Ready(3)); // the next event needs new readiness
        Assert.Equal("in_convoy", Rejoin(3).GetProperty("reason").GetString());
        Assert.Equal("already_in_convoy", dir.Rejoin(Id(3), Info(3)).Error?.Code);
    }

    [Fact]
    public void FullConvoy_SaysFull_KeepsTheFact_AndNeverAdmitsASeventhHuman()
    {
        Convoy(6);
        dir.Disconnected(Id(6));
        Join(7); // the released seat is used by someone new: nobody is ejected to reclaim it
        dir.Connected(Id(6), V);
        Assert.Equal("full", Rejoin(6).GetProperty("reason").GetString());
        Assert.False(Rejoin(6).GetProperty("canRejoin").GetBoolean());
        Assert.Equal("convoy_full", dir.Rejoin(Id(6), Info(6)).Error?.Code);
        Assert.Equal(6, MemberCount(1));

        Assert.True(dir.Leave(Id(5)).Ok);
        Assert.True(dir.Rejoin(Id(6), Info(6)).Ok); // the underlying permission still existed
        Assert.Equal(6, MemberCount(1));
    }

    [Fact]
    public void LeadershipReturningToTheSameAccount_DoesNotReviveAnOldEpoch()
    {
        Convoy(3);
        dir.Disconnected(Id(3)); // grant at epoch 1 under leader 1
        Assert.True(dir.Leave(Id(1)).Ok); // leader 2, epoch 2
        Join(1, leader: 2); // leader 1 comes back as an ordinary member
        Assert.True(dir.Leave(Id(2)).Ok); // leadership passes back to 1, epoch 3
        Assert.Equal(Id(1), State(1).GetProperty("leaderId").GetString());
        Assert.Equal(3, State(1).GetProperty("leadershipEpoch").GetInt64());

        dir.Connected(Id(3), V);
        Assert.Equal("leader_changed", Rejoin(3).GetProperty("reason").GetString());
        Assert.Equal("rejoin_leader_changed", dir.Rejoin(Id(3), Info(3)).Error?.Code);
    }

    [Fact]
    public void LeaderRejoiningInsideTheGrace_KeepsLeadership_AndTheEpoch()
    {
        Convoy(3);
        long epoch = State(2).GetProperty("leadershipEpoch").GetInt64();
        dir.Disconnected(Id(1));
        clock.Advance(TimeSpan.FromSeconds(10));
        dir.Connected(Id(1), V);
        Assert.True(Rejoin(1).GetProperty("youAreLeaderInGrace").GetBoolean());
        JsonElement r = Result(dir.Rejoin(Id(1), Info(1)));
        Assert.True(r.GetProperty("leader").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(10));
        dir.Tick();
        JsonElement s = State(2);
        Assert.Equal(Id(1), s.GetProperty("leaderId").GetString());
        Assert.Equal(epoch, s.GetProperty("leadershipEpoch").GetInt64());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("leaderUnavailable").ValueKind);
    }

    [Fact]
    public void NoActiveMembers_AfterDisconnection_IsDormant_NotAnOpenRoom_AndAVoluntaryLastLeaveDisbands()
    {
        // Addendum 01 disbanded at once here; Addendum 02 D208 narrows that: an all-disconnected room stays Dormant (no seats,
        // not listed, not joinable) for at most 24 h so its members can rejoin. A voluntary last leave still ends it at once.
        Convoy(2);
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1));
        Assert.Empty(dir.ListDiscoverable());
        dir.Connected(Id(1), V);
        dir.Connected(Id(2), V);
        Assert.True(Rejoin(1).GetProperty("dormant").GetBoolean());
        Assert.True(dir.Rejoin(Id(2), Info(2)).Ok);
        Assert.True(dir.Leave(Id(2)).Ok); // the last active member leaves voluntarily: the session ends now
        Assert.Equal("disbanded", Rejoin(1).GetProperty("reason").GetString());
        Assert.Equal("rejoin_disbanded", dir.Rejoin(Id(1), Info(1)).Error?.Code);
    }

    [Fact]
    public void Kick_RemovesWithoutAGrant_AndRevokesADepartedMembersGrant()
    {
        Convoy(3);
        Assert.Equal("not_leader", dir.Kick(Id(2), Id(3)).Error?.Code);
        Assert.True(dir.Kick(Id(1), Id(2)).Ok);
        Assert.Contains(notifier.To(Id(2), "convoy.closed"), m => m.GetProperty("reason").GetString() == "kicked");
        Assert.Equal("revoked", Rejoin(2).GetProperty("reason").GetString());
        Assert.Equal("rejoin_revoked", dir.Rejoin(Id(2), Info(2)).Error?.Code);

        dir.Disconnected(Id(3));
        Assert.True(Rejoin(3).GetProperty("canRejoin").GetBoolean());
        JsonElement revoked = Result(dir.Kick(Id(1), Id(3)));
        Assert.True(revoked.GetProperty("rejoinRevoked").GetBoolean());
        dir.Connected(Id(3), V);
        Assert.Equal("rejoin_revoked", dir.Rejoin(Id(3), Info(3)).Error?.Code);
    }

    [Fact]
    public void BlockWithTheLeader_RevokesRejoin()
    {
        Convoy(3);
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(3));
        dir.RevokeForBlock(Id(1), Id(2)); // the leader blocked member 2
        Assert.Equal("revoked", Rejoin(2).GetProperty("reason").GetString());
        dir.Connected(Id(3), V);
        Assert.Equal("rejoin_revoked", dir.Rejoin(Id(3), Info(3), blockedWithLeader: true).Error?.Code);
    }

    [Fact]
    public void JoiningAnotherConvoy_OrForget_InvalidatesTheGrant()
    {
        Convoy(3);
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(3));
        dir.Connected(Id(2), V);
        dir.Connected(Id(3), V);
        Assert.True(dir.Create(Id(2), Info(2), ConvoyPrivacy.InviteOnly).Ok); // joining a different convoy
        Assert.True(dir.Leave(Id(2)).Ok);
        Assert.Equal("none", Rejoin(2).GetProperty("reason").GetString());

        Assert.True(dir.DismissRejoin(Id(3), forget: true).Ok); // "Forget convoy"
        Assert.Equal("none", Rejoin(3).GetProperty("reason").GetString());
        Assert.Equal("rejoin_unavailable", dir.Rejoin(Id(3), Info(3)).Error?.Code);
    }

    [Fact]
    public void RejoinDuringAMatch_RestoresMembershipOnly_TheDqEntrantSpectates()
    {
        Convoy(3);
        long rev = OpenEvent();
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(3)).Plan!;
        dir.CompleteStart(plan, new ActiveMatch("m_1", "srv", "127.0.0.1", 7777, V, plan.Entrants.Select(e => e.AccountId).ToList()));
        Assert.True(dir.CurrentMatch(Id(2)).IsEntrant);

        dir.Disconnected(Id(2)); // the race continues for everyone else
        Assert.Equal("InMatch", State(1).GetProperty("phase").GetString());
        dir.Connected(Id(2), V);
        JsonElement r = Result(dir.Rejoin(Id(2), Info(2)));
        Assert.True(r.GetProperty("spectator").GetBoolean());
        Assert.False(dir.CurrentMatch(Id(2)).IsEntrant);          // no racer ticket: the DQ entry is never restored
        Assert.False(dir.RacerEligible(plan.ConvoyId, Id(2)));
        Assert.True(dir.RacerEligible(plan.ConvoyId, Id(3)));
        Assert.True(MemberState(1, 2).GetProperty("spectator").GetBoolean());
        Assert.Equal(3, MemberCount(1));

        dir.MatchEnded(plan.ConvoyId, "m_1", null);
        Assert.False(MemberState(1, 2).GetProperty("spectator").GetBoolean()); // next event: a normal entrant again
    }

    [Fact]
    public void GrantHolders_AreToldWhenTheirGrantStopsBeingValid()
    {
        Convoy(3);
        dir.Disconnected(Id(3));
        dir.Connected(Id(3), V);
        Assert.True(dir.Leave(Id(1)).Ok); // leadership change → epoch change
        JsonElement pushed = notifier.To(Id(3), "rejoin.status").Last();
        Assert.False(pushed.GetProperty("canRejoin").GetBoolean());
        Assert.Equal("leader_changed", pushed.GetProperty("reason").GetString());
    }
}
