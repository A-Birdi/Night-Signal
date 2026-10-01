using System.Text.Json;
using NightSignal.ControlPlane.Convoys;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Freeplay's lighting/weather presets (spec §8; Core <see cref="ConditionPresets"/>): one table for the control plane, the game
/// server and both clients; chosen by Freeplay only, frozen into the proposal and the match, and a change unreadies everyone.
/// Stages, Team Trials and challenge trials race their own conditions (their benchmarks and targets are certified in them).
/// </summary>
public sealed class ConditionPresetTests : ConvoyTestBase
{
    [Fact]
    public void Presets_AreOneTable_TheDefaultFirst_EveryOtherFixingSurfaceAndLighting()
    {
        Assert.Equal(ConditionPresets.Default, ConditionPresets.Ids[0]);
        Assert.Equal(ConditionPresets.Ids.Length, ConditionPresets.Ids.Distinct().Count());
        Assert.Equal(ConditionPresets.Ids, ConvoyRules.Weathers);
        foreach (ConditionPreset p in ConditionPresets.All.Skip(1))
        {
            Assert.Contains(p.Surface, new[] { "dry", "damp", "wet" });
            Assert.False(string.IsNullOrEmpty(p.Lighting));
            Assert.False(string.IsNullOrEmpty(p.Label));
        }
        Assert.Equal("damp", ConditionPresets.Surface(null, "damp"));          // the default keeps the event's own
        Assert.Equal("damp", ConditionPresets.Surface("stage-default", "damp"));
        Assert.Equal("wet", ConditionPresets.Surface("wet-night", "dry"));
        Assert.Equal("night", ConditionPresets.Lighting("dry-night", "late-afternoon"));
        Assert.Equal("evening", ConditionPresets.Lighting("stage-default", "evening"));
        Assert.Null(ConditionPresets.Find("monsoon"));
        Assert.Equal("Night, wet", ConditionPresets.Describe("wet-night", "Late afternoon, dry"));
        Assert.Equal("Late afternoon, dry", ConditionPresets.Describe(null, "Late afternoon, dry"));
        Assert.Equal("Late afternoon, dry", ConditionPresets.Words("late-afternoon", "dry"));
    }

    [Fact]
    public void Freeplay_CarriesThePreset_IntoTheProposalAndTheMatch()
    {
        Convoy(2);
        EnterMode(Freeplay("sprint"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("invalid_request", dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", "monsoon", 0, null, null)).Error?.Code);
        long rev = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", "wet-night", 0, null, null)), "proposalRevision");
        Assert.Equal("wet-night", State(1).GetProperty("eventProposal").GetProperty("settings").GetProperty("weather").GetString());
        ReadyAll(rev);
        Assert.Equal("wet-night", dir.BeginStart(Id(1), rev, Fresh(2)).Plan!.Settings.Weather);
    }

    [Fact]
    public void Freeplay_WithoutAPreset_RacesTheCoursesOwnConditions()
    {
        Convoy(1);
        EnterMode(Freeplay("time-attack"));
        long rev = ProposeFreeplay("C01", "time-attack");
        Assert.Equal(ConditionPresets.Default, State(1).GetProperty("eventProposal").GetProperty("settings").GetProperty("weather").GetString());
        ReadyAll(rev);
        Assert.Equal(ConditionPresets.Default, dir.BeginStart(Id(1), rev, Fresh(1)).Plan!.Settings.Weather);
    }

    [Fact]
    public void AChangedPreset_UnreadiesEveryone()
    {
        Convoy(2);
        EnterMode(Freeplay("sprint"));
        long course = ProposeFreeplay("C01", "sprint");
        ReadyAll(course);
        Assert.True(Ready(2));
        clock.Advance(TimeSpan.FromSeconds(15));
        long fog = Value(dir.ProposeEvent(Id(1), new EventRequest(null, "C01", "sprint", "fog", 0, null, null)), "proposalRevision");
        Assert.True(fog > course);
        Assert.False(Ready(2));
        Assert.Equal("stale_revision", dir.SetReady(Id(2), course, MemberState(1, 2).GetProperty("loadoutRevision").GetInt64(), true).Error?.Code);
    }

    [Fact]
    public void ACampaignStage_RacesItsOwnAuthoredConditions()
    {
        Convoy(2);
        EnterMode(Campaign());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("conditions_fixed", dir.ProposeEvent(Id(1), new EventRequest("S01", null, null, "wet-night", null, null, null)).Error?.Code);
        Assert.Equal("conditions_fixed", dir.ProposeEvent(Id(1), new EventRequest("S01", null, null, "dry-night", null, null, null)).Error?.Code);
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest("S01", null, null, "stage-default", null, null, null)).Ok);
    }

    [Fact]
    public void ATeamTrial_RacesItsCoursesOwnConditions()
    {
        Convoy(2);
        EnterMode(Challenges("TT_MEAN"));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("conditions_fixed", dir.ProposeEvent(Id(1),
            new EventRequest(null, null, null, "dawn", null, null, null, TrialId: "TT_MEAN", Difficulty: "expert")).Error?.Code);
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest(null, null, null, null, null, null, null, TrialId: "TT_MEAN", Difficulty: "expert")).Ok);
    }

    [Fact]
    public void AChallengeTrial_RacesItsCoursesOwnConditions()
    {
        Convoy(1);
        EnterMode(Challenges());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("conditions_fixed", dir.ProposeEvent(Id(1),
            new EventRequest(null, null, null, "fog", null, null, null, ChallengeTrialId: "TR-CH51")).Error?.Code);
        Assert.True(dir.ProposeEvent(Id(1), new EventRequest(null, null, null, null, null, null, null, ChallengeTrialId: "TR-CH51")).Ok);
    }
}
