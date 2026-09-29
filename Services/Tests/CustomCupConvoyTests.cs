using System.Text.Json;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Matches;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// The Custom Cup online (spec §8; Addendum 01 §5.2): the published schedule raced leg by leg as ordinary matches; the table
/// kept by the convoy from each settled leg; "Next leg" as the post-event step; access frozen with the first leg so a
/// guest's passes survive the sponsor leaving; a departed member keeps its line; an aborted leg ends the cup.
/// </summary>
public sealed class CustomCupConvoyTests : ConvoyTestBase
{
    static readonly string[] Legs = { "C05", "C06", "C07" }; // not starter courses: they need a sponsor

    /// <summary>Races the open proposal for <paramref name="members"/> and ends it with the given leg placings.</summary>
    MatchPlan RaceLeg(string matchId, IEnumerable<int> members, params (string Id, int? Place)[] placings)
    {
        long rev = State(1).GetProperty("eventProposal").GetProperty("revision").GetInt64();
        ReadyAll(rev);
        (ConvoyError? error, MatchPlan? plan) = dir.BeginStart(Id(1), rev, FreshFor(members));
        Assert.True(plan is not null, error?.Message);
        dir.CompleteStart(plan!, new ActiveMatch(matchId, "srv", "h", 1, V, plan!.Entrants.Select(e => e.AccountId).ToList()));
        dir.MatchEnded(plan.ConvoyId, matchId, FreshFor(members), null,
            placings.Select(p => new CupLegResult { Id = p.Id, Name = p.Id, Human = p.Id.StartsWith("0000"), Place = p.Place }).ToList());
        return plan;
    }

    JsonElement Cup() => State(1).GetProperty("cup");

    void ContinueAndAdvance(params int[] others)
    {
        long dr = State(1).GetProperty("postEvent").GetProperty("destinationRevision").GetInt64();
        foreach (int m in others) Assert.True(dir.ChoosePostEvent(Id(m), dr, "continue").Ok);
        Assert.True(dir.AdvancePostEvent(Id(1), dr).Ok);
    }

    [Fact]
    public void ThreeLegs_RaceAsMatches_WithTheTable_AndTheFirstLegsPassesSurviveTheSponsorLeaving()
    {
        // Member 2 is the only sponsor of the three courses; 1 and 3 race on guest passes.
        Convoy(3, courses: new Dictionary<int, string[]> { [2] = Legs });
        EnterMode(Freeplay("cup"));
        ProposeFreeplay("C05", "cup", ai: 2, legs: Legs);

        MatchPlan leg1 = RaceLeg("m_leg1", new[] { 1, 2, 3 }, (Id(1), 2), (Id(2), 1), (Id(3), null), ("R08", 3));
        Assert.Equal("C05", leg1.Settings.CourseId);
        Assert.Equal(0, leg1.Settings.CupLeg);
        Assert.Contains(leg1.GuestPasses, g => g.AccountId == Id(3) && g.CourseId == "C07"); // every leg frozen up front
        Assert.Equal(1, Cup().GetProperty("legsRaced").GetInt32());
        JsonElement destination = State(1).GetProperty("postEvent").GetProperty("destination");
        Assert.Equal("next-cup-leg", destination.GetProperty("kind").GetString());
        Assert.Contains("(2 of 3)", destination.GetProperty("label").GetString());

        // The sponsor leaves between legs: the passes granted with the first leg still carry 1 and 3.
        Assert.True(dir.Leave(Id(2)).Ok);
        ContinueAndAdvance(3);
        Assert.Equal("cup-leg", State(1).GetProperty("eventProposal").GetProperty("origin").GetString());
        MatchPlan leg2 = RaceLeg("m_leg2", new[] { 1, 3 }, (Id(1), 1), (Id(3), 2), ("R08", 3));
        Assert.Equal("C06", leg2.Settings.CourseId);
        Assert.Equal(1, leg2.Settings.CupLeg);
        Assert.Contains(leg2.GuestPasses, g => g.AccountId == Id(3) && g.CourseId == "C06");

        ContinueAndAdvance(3);
        MatchPlan leg3 = RaceLeg("m_leg3", new[] { 1, 3 }, (Id(1), 1), (Id(3), 3), ("R08", 2));
        Assert.Equal("C07", leg3.Settings.CourseId);
        JsonElement cup = Cup();
        Assert.True(cup.GetProperty("complete").GetBoolean());
        List<JsonElement> table = cup.GetProperty("standings").EnumerateArray().ToList();
        Assert.Equal(Id(1), table[0].GetProperty("id").GetString());
        Assert.Equal(8 + 10 + 10, table[0].GetProperty("points").GetInt32());
        JsonElement departed = table.Single(e => e.GetProperty("id").GetString() == Id(2));
        Assert.Equal(10, departed.GetProperty("points").GetInt32()); // keeps its line; the missed legs are not regained
        Assert.Equal("cup-complete", State(1).GetProperty("postEvent").GetProperty("destination").GetProperty("kind").GetString());
        ContinueAndAdvance(3);
        Assert.Equal(JsonValueKind.Null, State(1).GetProperty("cup").ValueKind);
        Assert.Equal("EventSelection", State(1).GetProperty("phase").GetString());
    }

    [Fact]
    public void AnAbortedLeg_EndsTheCup()
    {
        Convoy(2, courses: new Dictionary<int, string[]> { [1] = Legs, [2] = Legs });
        EnterMode(Freeplay("cup"));
        long rev = ProposeFreeplay("C05", "cup", ai: 1, legs: Legs);
        ReadyAll(rev);
        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(2)).Plan!;
        dir.CompleteStart(plan, new ActiveMatch("m_abort", "srv", "h", 1, V, new[] { Id(1), Id(2) }));
        Assert.Equal(JsonValueKind.Object, State(1).GetProperty("cup").ValueKind);
        dir.MatchAborted(plan.ConvoyId, "m_abort", "server lost");
        Assert.Equal(JsonValueKind.Null, State(1).GetProperty("cup").ValueKind);
        Assert.Contains("Custom Cup ends", State(1).GetProperty("notice").GetString());
    }
}
