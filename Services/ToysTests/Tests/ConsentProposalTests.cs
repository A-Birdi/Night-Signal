using NightSignal.Core.Toys;

namespace NightSignal.Toys.Tests;

/// <summary>
/// Consent for a shared-state change (Addendum 02 §2.2, §3.2, §5.3, §6.2): the proposer approves implicitly, every other
/// current user must accept, one decline closes it, silence lapses at the deadline, and people who leave are no longer
/// asked.
/// </summary>
public sealed class ConsentProposalTests
{
    [Fact]
    public void Alone_TheProposersOwnApprovalIsEnough()
    {
        ConsentProposal p = ConsentProposal.Open("p1", "clear", "", "a", new[] { "a" }, 0);
        Assert.Equal(ProposalState.Accepted, p.State);
        Assert.Empty(p.Required);
    }

    [Fact]
    public void EveryOtherCurrentUserMustAccept_AndOneDeclineCloses()
    {
        ConsentProposal p = ConsentProposal.Open("p1", "clear", "", "a", new[] { "a", "b", "c", "b" }, 0);
        Assert.Equal(new[] { "b", "c" }, p.Required);
        Assert.False(p.Vote("z", true), "not asked");
        Assert.False(p.Vote("a", false), "the proposer is not asked again");
        Assert.True(p.Vote("b", true));
        Assert.Equal(ProposalState.Open, p.State);
        Assert.True(p.Vote("c", true));
        Assert.Equal(ProposalState.Accepted, p.State);
        Assert.False(p.Vote("c", false), "closed");

        ConsentProposal q = ConsentProposal.Open("p2", "reset", "", "a", new[] { "a", "b", "c" }, 0);
        Assert.True(q.Vote("b", false));
        Assert.Equal(ProposalState.Declined, q.State);
        Assert.False(q.Vote("c", true));
        Assert.Equal(ProposalState.Declined, q.State);
    }

    [Fact]
    public void SilenceLapses_AndLeaversAreNoLongerAsked()
    {
        ConsentProposal p = ConsentProposal.Open("p1", "layout", "", "a", new[] { "a", "b" }, 1_000);
        p.Tick(1_000 + ToyLimits.ProposalLifetimeMs - 1);
        Assert.Equal(ProposalState.Open, p.State);
        p.Tick(1_000 + ToyLimits.ProposalLifetimeMs);
        Assert.Equal(ProposalState.Lapsed, p.State);
        Assert.False(p.Vote("b", true));

        ConsentProposal q = ConsentProposal.Open("p2", "layout", "", "a", new[] { "a", "b", "c" }, 0);
        q.Vote("b", true);
        q.Retain(m => m != "c");
        Assert.Equal(ProposalState.Accepted, q.State);

        ConsentProposal r = ConsentProposal.Open("p3", "layout", "", "a", new[] { "a", "b" }, 0);
        r.Retain(m => false);
        Assert.Equal(ProposalState.Accepted, r.State);
        r.Tick(ToyLimits.ProposalLifetimeMs * 2);
        Assert.Equal(ProposalState.Accepted, r.State);
    }
}
