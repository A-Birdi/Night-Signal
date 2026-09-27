using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>How server-observed facts become Core inputs (no database needed).</summary>
public sealed class SettlementComputeTests
{
    const string H1 = "00000000-0000-4000-8000-000000000001";
    const string H2 = "00000000-0000-4000-8000-000000000002";
    readonly SettlementService service = new(null!, null!, TestData.Content, null!, null!, null!);

    static MatchAssignment Match(string kind = "campaign", string? freeplayMode = null, int ai = 0, bool purePvP = false, string mode = "normal") => new()
    {
        MatchId = "m_x", ConvoyId = "cv", ServerId = "srv", Kind = kind, Mode = kind == "campaign" ? mode : null,
        StageId = kind == "campaign" ? "S01" : null, StageNumber = 1, StageType = "regular", CourseId = "C01", FreeplayMode = freeplayMode,
        Weather = "stage-default", Collision = "off", CarCapPi = 299,
        Entrants = new[] { Entrant(H1), Entrant(H2) },
        AiEntrants = Enumerable.Range(1, ai).Select(i => $"ai-{i}").ToList(),
        Benchmark = new AssignedBenchmark("Time", 180_000, 0, 390_000, true, "test"),
        PurePvP = purePvP, Build = "b", Protocol = 1, ContentHash = "c", Seed = 1, ResultsUrl = "", TicketIssuer = "i", TicketAudience = "a",
    };

    static AssignedEntrant Entrant(string id) => new(id, id, "racer", "V01", 220, "p", "c", 1);

    static EntrantFacts F(string id, RunOutcome outcome, long seconds, int place, bool human = true, double checkpoints = 1, long drift = 0,
        params string[] challenges) => new()
    {
        EntrantId = id, Human = human, Outcome = outcome, FinishTimeMicros = outcome == RunOutcome.Finished ? seconds * 1_000_000 : 0,
        Placement = place, Clean = false, CheckpointFraction = checkpoints, ActiveProgressVerified = true, ActivelyDroveLegalCourse = true,
        LegalProgressMetres = checkpoints * 3000, RawDriftScore = drift, ChallengesCompleted = challenges.ToList(),
    };

    (MatchSettlement? S, string? Error) Compute(MatchAssignment m, params EntrantFacts[] facts) =>
        service.Compute(m, new ResultSubmission { MatchId = m.MatchId, ContentHash = "c", Entrants = facts.ToList() }, "h");

    EntrantSettlement For(MatchSettlement s, string id) => s.Entrants.Single(e => e.AccountId == id);

    [Fact]
    public void ActiveDnf_With80PercentCheckpoints_GetsTheCompletionAllowance_QuitGetsNothing()
    {
        (MatchSettlement? s, string? error) = Compute(Match(), F(H1, RunOutcome.DidNotFinish, 0, 1, checkpoints: 0.85), F(H2, RunOutcome.Quit, 0, 0));
        Assert.Null(error);
        PayoutBreakdown dnf = Economy.Compute(For(s!, H1).Facts);
        Assert.Equal(Economy.BaseCompletion(180) / 4, dnf.EventCredits);
        Assert.Equal(0, Economy.Compute(For(s!, H2).Facts).Total);
        Assert.Null(For(s!, H1).Clear); // no clear, no RP for a DNF
        Assert.False(For(s!, H1).Receipt.Stage!.TeamSuccess);
    }

    [Fact]
    public void HardMode_NeedsHalfTheHumansToQualify_AndDqsStillCount()
    {
        // H = 2 frozen at allocation; one qualifies, the other is DQ'd: ceil(2/2) = 1 qualifier needed → success.
        (MatchSettlement? s, _) = Compute(Match(mode: "hard"), F(H1, RunOutcome.Finished, 150, 1), F(H2, RunOutcome.DisqualifiedDisconnect, 0, 0));
        Assert.True(For(s!, H1).Receipt.Stage!.TeamSuccess);
        Assert.NotNull(For(s!, H1).Clear);
        Assert.Null(For(s!, H2).Clear); // a DQ never earns a clear
        Assert.Equal(135, Economy.Compute(For(s!, H1).Facts).DifficultyX100);
    }

    [Fact]
    public void SlowFinishOutsideTheSupportEnvelope_EarnsMoneyButNoClear()
    {
        (MatchSettlement? s, _) = Compute(Match(), F(H1, RunOutcome.Finished, 170, 1), F(H2, RunOutcome.Finished, 271, 2));
        Assert.NotNull(For(s!, H1).Clear);
        Assert.Null(For(s!, H2).Clear); // 271 s > 180 s × 1.50
        Assert.True(Economy.Compute(For(s!, H2).Facts).EventCredits > 0);
    }

    [Fact]
    public void PurePvPWinnerBonus_OnlyWithTwoHumanFinishersAndNoAi()
    {
        (MatchSettlement? pvp, _) = Compute(Match("freeplay", "sprint", purePvP: true), F(H1, RunOutcome.Finished, 170, 1), F(H2, RunOutcome.Finished, 180, 2));
        Assert.True(For(pvp!, H1).Facts.PvPWinnerBonusEligible);
        Assert.False(For(pvp!, H2).Facts.PvPWinnerBonusEligible);

        (MatchSettlement? forfeit, _) = Compute(Match("freeplay", "sprint", purePvP: true), F(H1, RunOutcome.Finished, 170, 1), F(H2, RunOutcome.Quit, 0, 0));
        Assert.False(For(forfeit!, H1).Facts.PvPWinnerBonusEligible); // last car left after a quit
    }

    [Fact]
    public void DriftAttack_PlacesByRawScore()
    {
        (MatchSettlement? s, string? error) = Compute(Match("freeplay", "drift-attack"),
            F(H1, RunOutcome.Finished, 200, 2, drift: 40_000), F(H2, RunOutcome.Finished, 210, 1, drift: 55_000));
        Assert.Null(error);
        Assert.Equal(1, For(s!, H2).Receipt.Placement);
        Assert.Equal(EventKind.FreeplayDriftAttack, For(s!, H1).Facts.Kind);
    }

    [Fact]
    public void ChallengeClaims_NeedAValidFinish_AndKnownIds()
    {
        (MatchSettlement? s, _) = Compute(Match(), F(H1, RunOutcome.Finished, 170, 1, challenges: "CH01"), F(H2, RunOutcome.DidNotFinish, 0, 2, checkpoints: 0.5, challenges: "CH02"));
        Assert.Equal("COS-CH01", For(s!, H1).Challenges.Single().CosmeticId);
        Assert.Equal(3_000, For(s!, H1).Challenges.Single().Cash);
        Assert.Empty(For(s!, H2).Challenges);
        Assert.Contains(For(s!, H2).Receipt.Notes, n => n.Contains("valid finish"));

        Assert.NotNull(Compute(Match(), F(H1, RunOutcome.Finished, 170, 1, challenges: "CH99"), F(H2, RunOutcome.Quit, 0, 0)).Error);
    }

    [Fact]
    public void InconsistentFacts_AreRejected()
    {
        Assert.NotNull(Compute(Match(), F(H1, RunOutcome.Finished, 170, 1)).Error);                                    // missing entrant
        Assert.NotNull(Compute(Match(), F(H1, RunOutcome.Finished, 170, 2), F(H2, RunOutcome.Finished, 180, 1)).Error); // placement vs times
        Assert.NotNull(Compute(Match(), F(H1, RunOutcome.Finished, 0, 1), F(H2, RunOutcome.Quit, 0, 0)).Error);         // zero finish time
        Assert.NotNull(Compute(Match(), F(H1, RunOutcome.Finished, 170, 1, checkpoints: double.NaN), F(H2, RunOutcome.Quit, 0, 0)).Error);
        Assert.NotNull(Compute(Match(), F(H1, RunOutcome.Finished, 170, 1), F(H2, RunOutcome.Quit, 0, 0, human: false)).Error);
        Assert.NotNull(Compute(Match(ai: 1), F(H1, RunOutcome.Finished, 170, 1), F(H2, RunOutcome.Quit, 0, 0),
            F("ai-1", RunOutcome.Finished, 175, 2, human: false, challenges: "CH01")).Error);                              // AI claims
    }

    [Fact]
    public void EqualMillisecondsTie_SharesThePlacing()
    {
        EntrantFacts a = F(H1, RunOutcome.Finished, 170, 1), b = F(H2, RunOutcome.Finished, 170, 1);
        b.FinishTimeMicros += 400; // same reported millisecond
        (MatchSettlement? s, string? error) = Compute(Match(), a, b);
        Assert.Null(error);
        Assert.True(For(s!, H1).Receipt.Tied && For(s!, H2).Receipt.Tied);
    }
}

public sealed class RedactionTests
{
    [Theory]
    [InlineData("{\"password\":\"hunter2\"}", "hunter2")]
    [InlineData("refresh_token=abc123def&x=1", "abc123def")]
    [InlineData("X-NightSignal-Server-Key: s3cr3tvalue", "s3cr3tvalue")]
    [InlineData("\"resultsSecret\": \"q9w8e7\"", "q9w8e7")]
    [InlineData("Bearer eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl", "eyJhbGciOiJFUzI1NiJ9")]
    public void RemovesCredentials(string text, string secret)
    {
        string redacted = Redaction.Redact(text);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("[redacted", redacted);
    }

    [Fact]
    public void LeavesOrdinaryTextAlone() =>
        Assert.Equal("Match m_1 settled: 2 receipts", Redaction.Redact("Match m_1 settled: 2 receipts"));
}
