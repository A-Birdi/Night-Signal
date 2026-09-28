using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>CH68 in settlement: granted for a finishing human whose kept C07 ghost the caller found beaten by a second, not otherwise.</summary>
public sealed class GhostSettlementTests
{
    static string H(int i) => $"00000000-0000-4000-8000-{i:000000000000}";

    [Fact]
    public void Ch68_OnlyForTheHumanWhoseGhostWasBeaten()
    {
        var service = new SettlementService(null!, null!, TestData.Content, null!, null!, null!, MusicUnlockManifest.Parse(
            """{"schema":"night-signal/music-unlocks@1","cues":[{"cueId":"menu-main","source":{"kind":"baseline"}}]}""",
            TestData.Content.Catalogue, TeamTrialCatalog.Fixture(TestData.Content.Catalogue)));
        var m = new MatchAssignment
        {
            MatchId = "m_ta", ConvoyId = "cv", ServerId = "srv", Kind = "freeplay", CourseId = "C07", FreeplayMode = "time-attack",
            Weather = "stage-default", Collision = "non-contact", CarCapPi = 999,
            Entrants = new List<AssignedEntrant> { new(H(1), H(1), "racer", "V01", 220, "p", "c", 1), new(H(2), H(2), "racer", "V01", 220, "p", "c", 1) },
            AiEntrants = new List<string>(),
            Build = "b", Protocol = 1, ContentHash = "c", Seed = 1, ResultsUrl = "", TicketIssuer = "i", TicketAudience = "a",
        };
        static EntrantFacts F(string id, int place) => new()
        {
            EntrantId = id, Human = true, Outcome = RunOutcome.Finished, FinishTimeMicros = (120_000 + place * 1000) * 1000L, Placement = place,
            CheckpointFraction = 1, ActiveProgressVerified = true, ActivelyDroveLegalCourse = true, LegalProgressMetres = 4700,
        };
        (MatchSettlement? s, string? e) = service.Compute(m, new ResultSubmission { MatchId = m.MatchId, ContentHash = "c", Entrants = new List<EntrantFacts> { F(H(1), 1), F(H(2), 2) } },
            "h", null, null, new HashSet<string> { H(1) });
        Assert.Null(e);
        Assert.Contains(s!.Entrants.Single(x => x.AccountId == H(1)).Challenges, g => g.ChallengeId == "CH68");
        Assert.DoesNotContain(s.Entrants.Single(x => x.AccountId == H(2)).Challenges, g => g.ChallengeId == "CH68");
    }
}
