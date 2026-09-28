using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

/// <summary>CH38 Three Different Rivals and CH73 Twelve Different Voices (Freeplay rival archetypes = tendencies).</summary>
public sealed class ArchetypeChallengesTests
{
    static ContentCatalogue Cat => TestContent.Catalogue;

    static ArchetypeRace Race(RunOutcome outcome, int placement, params string[] rivals) =>
        new() { AiRivals = rivals, Outcome = outcome, Placement = placement };

    static ArchetypeRace Win(params string[] rivals) => Race(RunOutcome.Finished, 1, rivals);

    [Fact]
    public void TheCatalogueHasNineteenArchetypes_AndTheTestRivalsCoverDistinctOnes()
    {
        Assert.Equal(19, Cat.Rivals.Select(r => r.Tendency).Distinct().Count());
        // R07 and R11 share an archetype (rhythm-linker); R01, R08 and R03 do not.
        Assert.Equal(ArchetypeChallenges.TendencyOf(Cat, "R07"), ArchetypeChallenges.TendencyOf(Cat, "R11"));
        Assert.Equal(3, new[] { "R01", "R08", "R03" }.Select(id => ArchetypeChallenges.TendencyOf(Cat, id)).Distinct().Count());
        Assert.Null(ArchetypeChallenges.TendencyOf(Cat, "ai-1"));
    }

    [Fact]
    public void ThreeWinsAgainstThreeDifferentLeadArchetypes_SatisfyCh38_ALossKeepsThem()
    {
        ArchetypeState s = ArchetypeChallenges.Replay(Cat, new[]
        {
            Win("R01", "R02"), Race(RunOutcome.Finished, 4, "R08"), Win("R08"), Race(RunOutcome.DidNotFinish, 0, "R05"), Win("R03", "R01"),
        });
        Assert.Equal(3, s.WonStreak.Count);
        Assert.Contains(ArchetypeChallenges.ThreeDifferentRivals, ArchetypeChallenges.Satisfied(s));
    }

    [Fact]
    public void OnlyTheLeadCounts_TheSameArchetypeTwiceCountsOnce_ATieIsNotAWin()
    {
        ArchetypeState s = ArchetypeChallenges.Replay(Cat, new[]
        {
            Win("R07", "R01", "R08"), Win("R11"), new ArchetypeRace { AiRivals = new[] { "R03" }, Outcome = RunOutcome.Finished, Placement = 1, Tied = true },
        });
        Assert.Single(s.WonStreak); // rhythm-linker only: R01/R08 were not the lead, R11 is the same archetype, the tie is not a win
        Assert.DoesNotContain(ArchetypeChallenges.ThreeDifferentRivals, ArchetypeChallenges.Satisfied(s));
    }

    [Fact]
    public void AQuitClearsTheWins_ADisconnectDoesNot()
    {
        ArchetypeState quit = ArchetypeChallenges.Replay(Cat, new[] { Win("R01"), Win("R08"), Race(RunOutcome.Quit, 0, "R05"), Win("R03") });
        Assert.Single(quit.WonStreak);
        Assert.Empty(ArchetypeChallenges.Satisfied(quit));
        ArchetypeState dropped = ArchetypeChallenges.Replay(Cat, new[] { Win("R01"), Win("R08"), Race(RunOutcome.DisqualifiedDisconnect, 0, "R05"), Win("R03") });
        Assert.Contains(ArchetypeChallenges.ThreeDifferentRivals, ArchetypeChallenges.Satisfied(dropped));
        // A quit against anonymous AI is not an archetype race and clears nothing.
        ArchetypeState anonymous = ArchetypeChallenges.Replay(Cat, new[] { Win("R01"), Win("R08"), Race(RunOutcome.Quit, 0, "ai-1"), Win("R03") });
        Assert.Contains(ArchetypeChallenges.ThreeDifferentRivals, ArchetypeChallenges.Satisfied(anonymous));
    }

    [Fact]
    public void TwelveDistinctArchetypesRacedToAFinish_SatisfyCh73_UnfinishedRacesDoNotCount()
    {
        var races = new List<ArchetypeRace>
        {
            Race(RunOutcome.Finished, 6, "R01", "R02", "R03", "R04", "R05", "R06"),  // 6 archetypes
            Race(RunOutcome.DidNotFinish, 0, "R14", "R15", "R17"),                  // not finished: nothing
            Race(RunOutcome.Finished, 3, "R07", "R08", "R09", "R10", "R11", "R12"),  // R11 repeats R07: +5 = 11
        };
        ArchetypeState eleven = ArchetypeChallenges.Replay(Cat, races);
        Assert.Equal(11, eleven.Raced.Count);
        Assert.DoesNotContain(ArchetypeChallenges.TwelveDifferentVoices, ArchetypeChallenges.Satisfied(eleven));
        races.Add(Race(RunOutcome.Finished, 2, "R14"));
        ArchetypeState twelve = ArchetypeChallenges.Replay(Cat, races);
        Assert.Equal(12, twelve.Raced.Count);
        Assert.Contains(ArchetypeChallenges.TwelveDifferentVoices, ArchetypeChallenges.Satisfied(twelve));
    }

    // ---------------- the Local profile keeps the two sets ----------------

    static LocalEventFacts Run(LocalProfile p, int placement, RunOutcome outcome, params string[] rivals)
    {
        LocalEventFacts f = LocalProgressionTests.FreeplayRun(p, "C01", placement, outcome);
        f.OpposingAi = rivals.ToList();
        return f;
    }

    [Fact]
    public void Local_ThreeLeadArchetypesBeaten_GrantCh38Once_AQuitClearsTheSet()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        p = LocalProgressionTests.Apply(p, Run(p, 1, RunOutcome.Finished, "R01", "R02"));
        p = LocalProgressionTests.Apply(p, Run(p, 1, RunOutcome.Finished, "R08"));
        p = LocalProgressionTests.Apply(p, Run(p, 0, RunOutcome.Quit, "R05"));
        Assert.Empty(p.ArchetypeWinStreak);
        Assert.Equal(3, p.ArchetypesRaced.Count); // momentum-reader, early-set-cornerer, late-brake-anchor
        p = LocalProgressionTests.Apply(p, Run(p, 1, RunOutcome.Finished, "R01"));
        p = LocalProgressionTests.Apply(p, Run(p, 1, RunOutcome.Finished, "R08"));
        Assert.False(p.HasCompletedChallenge(ArchetypeChallenges.ThreeDifferentRivals));
        p = LocalProgressionTests.Apply(p, Run(p, 1, RunOutcome.Finished, "R03"));
        Assert.True(p.HasCompletedChallenge(ArchetypeChallenges.ThreeDifferentRivals));
        Assert.Empty(p.Validate());
    }

    [Fact]
    public void Local_TwelveArchetypes_GrantCh73()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        p = LocalProgressionTests.Apply(p, Run(p, 5, RunOutcome.Finished, "R01", "R02", "R03", "R04", "R05"));
        p = LocalProgressionTests.Apply(p, Run(p, 5, RunOutcome.Finished, "R06", "R07", "R08", "R09", "R10"));
        Assert.False(p.HasCompletedChallenge(ArchetypeChallenges.TwelveDifferentVoices));
        p = LocalProgressionTests.Apply(p, Run(p, 3, RunOutcome.Finished, "R12", "R14"));
        Assert.Equal(12, p.ArchetypesRaced.Count);
        Assert.True(p.HasCompletedChallenge(ArchetypeChallenges.TwelveDifferentVoices));
    }
}
