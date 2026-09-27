using System.Text.Json;
using NightSignal.ControlPlane.Convoys;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;
using CoreBallot = NightSignal.Core.Rules.Ballot;

namespace NightSignal.Services.Tests;

/// <summary>
/// Freeplay voting (Addendum 01 §6.2, D07): no vote before unanimous Mode Ready; one changeable ballot per active member;
/// server deadline; weighted single draw persisted with its revision; empty pool, single candidate, leader override,
/// cancellation on roster/mode/leadership change; Event Ready still required afterwards.
/// </summary>
public sealed class VotingTests : ConvoyTestBase
{
    // Draw value 5: with n canonical ballots the server picks index 5 % n (Core Ballot.Draw).
    public VotingTests() : base(new FixedRandom(5, 5, 5, 5)) { }

    long OpenVote(int members, int seconds = 30)
    {
        Convoy(members, courses: new() { [2] = new[] { "FP02" } }); // member 2 (not the leader) owns the FP02 sprint
        EnterMode(Freeplay("sprint"));
        Assert.True(dir.ConfigureVoting(Id(1), true, seconds).Ok);
        return Value(dir.OpenBallot(Id(1), null, new BallotOptions(null, 0, null, null)), "ballotRevision");
    }

    JsonElement Ballot() => State(1).GetProperty("ballot");

    [Fact]
    public void NoVoteOpens_BeforeEveryMemberIncludingTheLeader_IsModeReady()
    {
        Convoy(3);
        EnterMode(Freeplay("sprint"));
        Assert.Equal("voting_off", dir.OpenBallot(Id(1), null, new BallotOptions(null, 0, null, null)).Error?.Code);
        Assert.True(dir.ConfigureVoting(Id(1), true, 45).Ok); // the leader may prepare the toggle and duration any time
        long mode = State(1).GetProperty("modeRevision").GetInt64();
        Assert.True(dir.SetModeReady(Id(2), mode, false).Ok);
        Assert.Equal("not_all_ready", dir.OpenBallot(Id(1), null, new BallotOptions(null, 0, null, null)).Error?.Code);
        Assert.True(dir.SetModeReady(Id(2), mode, true).Ok);
        Assert.True(dir.SetModeReady(Id(1), mode, false).Ok);
        Assert.Equal("not_all_ready", dir.OpenBallot(Id(1), null, new BallotOptions(null, 0, null, null)).Error?.Code);
        Assert.True(dir.SetModeReady(Id(1), mode, true).Ok);
        Assert.Equal("not_leader", dir.OpenBallot(Id(2), null, new BallotOptions(null, 0, null, null)).Error?.Code);
        Assert.Equal("invalid_request", dir.OpenBallot(Id(1), 20, new BallotOptions(null, 0, null, null)).Error?.Code); // 15/30/45/60 only
        Assert.True(dir.OpenBallot(Id(1), null, new BallotOptions(null, 0, null, null)).Ok);
        Assert.Equal(45, Ballot().GetProperty("durationSeconds").GetInt32());
    }

    [Fact]
    public void Ballots_AreChangeable_Validated_Weighted_AndFrozenAtTheServerDeadline()
    {
        long b = OpenVote(4);
        Assert.Equal(30, Ballot().GetProperty("durationSeconds").GetInt32());
        Assert.Equal(30_000, Ballot().GetProperty("remainingMs").GetInt64());

        Assert.True(dir.Vote(Id(1), b, "C01").Ok);
        Assert.True(dir.Vote(Id(2), b, "C02").Ok);
        Assert.True(dir.Vote(Id(2), b, "C01").Ok); // changeable: still one ballot per member
        Assert.True(dir.Vote(Id(3), b, "C01").Ok);
        Assert.True(dir.Vote(Id(4), b, "FP02").Ok); // a member who owns nothing extra votes for a convoy-sponsored route
        Assert.Equal("stale_revision", dir.Vote(Id(3), b - 1, "C02").Error?.Code);
        Assert.Equal("course_locked", dir.Vote(Id(3), b, "C20").Error?.Code);     // a sprint nobody in the convoy owns
        Assert.Equal("mode_unsupported", dir.Vote(Id(3), b, "C03").Error?.Code);  // a circuit in a Sprint vote
        Assert.Equal("mode_unsupported", dir.Vote(Id(3), b, "T00").Error?.Code);  // the tutorial is not a Freeplay venue
        Assert.Equal("invalid_request", dir.Vote(Id(3), b, "C99").Error?.Code);

        JsonElement ballot = Ballot();
        Assert.Equal(4, ballot.GetProperty("totalBallots").GetInt32());
        JsonElement c01 = ballot.GetProperty("tallies").EnumerateArray().First(t => t.GetProperty("courseId").GetString() == "C01");
        Assert.Equal(3, c01.GetProperty("votes").GetInt32());
        Assert.Equal(0.75, c01.GetProperty("chance").GetDouble()); // three of four tickets, not "one of two names"

        Assert.Equal("ballot_open", dir.DrawBallot(Id(1), b).Error?.Code); // no early draw
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("ballot_closed", dir.Vote(Id(3), b, "C02").Error?.Code);
        Assert.Equal("frozen", Ballot().GetProperty("state").GetString());
    }

    [Fact]
    public void Draw_PicksOneAcceptedBallotOnce_CreatesAFrozenProposal_AndCannotBeRerolled()
    {
        long b = OpenVote(3);
        dir.Vote(Id(1), b, "C01");
        dir.Vote(Id(2), b, "FP02");
        dir.Vote(Id(3), b, "C02");
        clock.Advance(TimeSpan.FromSeconds(30));
        dir.Tick(); // the server freezes ballots at the deadline even without traffic

        var frozen = new Dictionary<string, string> { [Id(1)] = "C01", [Id(2)] = "FP02", [Id(3)] = "C02" };
        string expected = CoreBallot.Draw(frozen, 5, out int expectedIndex)!;
        Assert.Equal("not_leader", dir.DrawBallot(Id(2), b).Error?.Code);
        JsonElement draw = Result(dir.DrawBallot(Id(1), b));
        Assert.Equal(expected, draw.GetProperty("courseId").GetString());
        Assert.Equal(expectedIndex, draw.GetProperty("ballotIndex").GetInt32());
        Assert.Equal("draw", draw.GetProperty("method").GetString());

        JsonElement s = State(3);
        Assert.Equal("ReadyCheck", s.GetProperty("phase").GetString());
        Assert.Equal("draw", s.GetProperty("eventProposal").GetProperty("origin").GetString());
        Assert.Equal(expected, s.GetProperty("eventProposal").GetProperty("settings").GetProperty("courseId").GetString());
        Assert.Equal("resolved", s.GetProperty("ballot").GetProperty("state").GetString());
        Assert.Equal(expected, s.GetProperty("ballot").GetProperty("result").GetProperty("courseId").GetString());

        // A retransmit (new request) or a reconnecting viewer sees the same stored winner; nothing is re-drawn.
        JsonElement again = Result(dir.DrawBallot(Id(1), b));
        Assert.True(again.GetProperty("replayed").GetBoolean());
        Assert.Equal(expected, again.GetProperty("courseId").GetString());
        Assert.Equal("ballot_already_drawn", dir.OpenBallot(Id(1), null, new BallotOptions(null, 0, null, null)).Error?.Code);
        Assert.Equal("ballot_resolved", dir.CancelBallotByLeader(Id(1), b).Error?.Code); // no fishing for another result

        // Event Ready from everyone is still required after the draw.
        long rev = s.GetProperty("eventProposal").GetProperty("revision").GetInt64();
        Assert.Equal("not_all_ready", dir.BeginStart(Id(1), rev, Fresh(3)).Error?.Code);
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(3), FreshCourses(3, (2, "FP02"))).Plan!;
        Assert.Equal(expected, plan.Settings.CourseId);
    }

    [Fact]
    public void NoVotes_SaysSo_AndAllowsDirectSelection_ASingleVoteResolvesToIt()
    {
        long b = OpenVote(2);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("no_votes", dir.DrawBallot(Id(1), b).Error?.Code);
        Assert.True(Ballot().GetProperty("noVotes").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest(null, "C02", "sprint", null, 0, null, null)).Ok);
        Assert.Equal("leader-selection", Ballot().GetProperty("result").GetProperty("method").GetString());

        // Next event: a new mode agreement lets the convoy vote again; one vote resolves to that course.
        Join(3);
        EnterModeAgainForVote();
        long b2 = Value(dir.OpenBallot(Id(1), 15, new BallotOptions(null, 0, null, null)), "ballotRevision");
        dir.Vote(Id(3), b2, "C04");
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("C04", Result(dir.DrawBallot(Id(1), b2)).GetProperty("courseId").GetString());
    }

    void EnterModeAgainForVote()
    {
        long mode = State(1).GetProperty("modeRevision").GetInt64();
        foreach (JsonElement m in State(1).GetProperty("members").EnumerateArray())
            dir.SetModeReady(m.GetProperty("accountId").GetString()!, mode, true);
    }

    [Fact]
    public void LeaderOverride_AfterADraw_IsVisible_AndInvalidatesEventReady()
    {
        long b = OpenVote(2);
        dir.Vote(Id(1), b, "C01");
        dir.Vote(Id(2), b, "C01");
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("C01", Result(dir.DrawBallot(Id(1), b)).GetProperty("courseId").GetString());
        long rev = State(1).GetProperty("eventProposal").GetProperty("revision").GetInt64();
        ReadyAll(rev);

        clock.Advance(TimeSpan.FromSeconds(15));
        long overridden = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C02", "sprint", null, 0, null, null)), "proposalRevision");
        Assert.NotEqual(rev, overridden);
        Assert.False(Ready(2));
        Assert.True(Ballot().GetProperty("result").GetProperty("overridden").GetBoolean());
        Assert.Equal(1, NoticeCount(2, "draw_overridden"));
    }

    [Fact]
    public void RosterChange_CancelsTheVote_WithOneNotice_AndNeedsNewModeAgreement()
    {
        long b = OpenVote(3);
        dir.Vote(Id(3), b, "C01");
        Join(4);
        Assert.Equal(JsonValueKind.Null, Ballot().ValueKind);
        Assert.Equal(1, NoticeCount(2, "ballot_cancelled"));
        Assert.Equal("not_all_ready", dir.OpenBallot(Id(1), null, new BallotOptions(null, 0, null, null)).Error?.Code);
        Assert.Equal("bad_phase", dir.Vote(Id(3), b, "C01").Error?.Code); // stale server votes are not carried over
    }

    [Fact]
    public void ModeUnready_CancelsTheVote()
    {
        long b = OpenVote(3);
        dir.Vote(Id(2), b, "C01");
        long mode = State(1).GetProperty("modeRevision").GetInt64();
        Assert.True(dir.SetModeReady(Id(3), mode, false).Ok);
        Assert.Equal(JsonValueKind.Null, Ballot().ValueKind);
        Assert.Equal(1, NoticeCount(1, "ballot_cancelled"));
    }

    [Fact]
    public void IntentChange_CancelsTheVote()
    {
        long b = OpenVote(2);
        dir.Vote(Id(2), b, "C01");
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(dir.SetIntent(Id(1), Freeplay("circuit")).Ok);
        Assert.Equal(JsonValueKind.Null, Ballot().ValueKind);
        Assert.Equal(1, NoticeCount(2, "ballot_cancelled"));
    }

    [Fact]
    public void LeadershipChange_CancelsTheVote()
    {
        long b = OpenVote(3);
        dir.Vote(Id(2), b, "C01");
        dir.Disconnected(Id(1)); // leader loss is a roster change: the vote ends at once, not after the 15 s grace
        Assert.Equal(JsonValueKind.Null, State(2).GetProperty("ballot").ValueKind);
        clock.Advance(TimeSpan.FromSeconds(15));
        dir.Tick();
        Assert.Equal(Id(2), State(2).GetProperty("leaderId").GetString());
        Assert.Equal(1, NoticeCount(2, "ballot_cancelled"));
    }

    [Fact]
    public void LosingTheOnlySponsorOfAVotedCourse_CancelsTheVote_WithAnExplanation()
    {
        long b = OpenVote(3);
        dir.Vote(Id(3), b, "FP02"); // sponsored only by member 2
        Assert.True(dir.Leave(Id(2)).Ok);
        Assert.Equal(JsonValueKind.Null, State(1).GetProperty("ballot").ValueKind);
        Assert.Equal(1, NoticeCount(3, "ballot_cancelled"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("course_locked", dir.ProposeEvent(Id(1), new EventRequest(null, "FP02", "sprint", null, 0, null, null)).Error?.Code);
    }

    [Fact]
    public void AnOpenVote_BlocksDirectSelection_UntilItEndsOrIsCancelled()
    {
        long b = OpenVote(2);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("ballot_open", dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 0, null, null)).Error?.Code);
        Assert.True(dir.CancelBallotByLeader(Id(1), b).Ok);
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", null, 0, null, null)).Ok);
    }

    [Fact]
    public void VotingIsFreeplayOnly_AndCupsAreSelectedDirectly()
    {
        Convoy(2);
        EnterMode(Campaign());
        dir.ConfigureVoting(Id(1), true, 30);
        Assert.Equal("bad_phase", dir.OpenBallot(Id(1), null, new BallotOptions(null, 0, null, null)).Error?.Code);
        clock.Advance(TimeSpan.FromSeconds(15));
        EnterMode(Freeplay("cup"));
        Assert.Equal("ballot_unsupported", dir.OpenBallot(Id(1), null, new BallotOptions(null, 0, null, null)).Error?.Code);
    }

    Dictionary<string, IReadOnlyCollection<string>> FreshCourses(int members, params (int Member, string Course)[] owned) =>
        Enumerable.Range(1, members).ToDictionary(Id, i => (IReadOnlyCollection<string>)owned.Where(o => o.Member == i).Select(o => o.Course).ToList());
}
