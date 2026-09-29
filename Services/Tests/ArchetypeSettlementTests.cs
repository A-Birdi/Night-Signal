using System.Text.Json;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>CH38 / CH73 online: settlement replays the settled Freeplay history plus this race; the store reads it back.</summary>
public sealed class ArchetypeSettlementTests
{
    static string H(int i) => $"00000000-0000-4000-8000-{i:000000000000}";

    static SettlementService Service() => new(null!, null!, TestData.Content, null!, null!, null!, MusicUnlockManifest.Parse(
        """{"schema":"night-signal/music-unlocks@1","cues":[{"cueId":"menu-main","source":{"kind":"baseline"}}]}""",
        TestData.Content.Catalogue, TeamTrialCatalog.Fixture(TestData.Content.Catalogue)));

    /// <summary>As the allocator builds it: Freeplay AI race under slot ids ("ai-1"…) with the rival as the roster's driver.</summary>
    static MatchAssignment Sprint(string matchId, params string[] rivals) => new()
    {
        MatchId = matchId, ConvoyId = "cv", ServerId = "srv", Kind = "freeplay", CourseId = "C01", FreeplayMode = "sprint",
        Weather = "stage-default", Collision = "light-contact", CarCapPi = 999,
        Entrants = new List<AssignedEntrant> { new(H(1), H(1), "racer", "V01", 220, "p", "c", 1) },
        AiEntrants = rivals.Select((_, i) => $"ai-{i + 1}").ToList(),
        Roster = new[] { new RosterSlot(H(1), "human", "player", "driver", H(1)) }
            .Concat(rivals.Select((r, i) => new RosterSlot($"ai-{i + 1}", "ai", "opposing", "opposing-ai", r))).ToList(),
        Build = "b", Protocol = 1, ContentHash = "c", Seed = 1, ResultsUrl = "", TicketIssuer = "i", TicketAudience = "a",
    };

    /// <summary>The human wins (placement 1) or finishes behind every AI.</summary>
    static List<EntrantFacts> Result(MatchAssignment m, bool win)
    {
        EntrantFacts F(string id, bool human, int place) => new()
        {
            EntrantId = id, Human = human, Outcome = RunOutcome.Finished, FinishTimeMicros = (180_000 + place * 1000) * 1000L, Placement = place,
            CheckpointFraction = 1, ActiveProgressVerified = true, ActivelyDroveLegalCourse = true, LegalProgressMetres = 3100,
        };
        int n = m.AiEntrants.Count;
        var list = new List<EntrantFacts> { F(H(1), true, win ? 1 : n + 1) };
        list.AddRange(m.AiEntrants.Select((id, i) => F(id, false, win ? i + 2 : i + 1)));
        return list;
    }

    static ArchetypeRace Won(params string[] rivals) => new() { AiRivals = rivals, Outcome = RunOutcome.Finished, Placement = 1 };

    static IReadOnlyList<string> Granted(MatchAssignment m, bool win, params ArchetypeRace[] before)
    {
        (MatchSettlement? s, string? e) = Service().Compute(m, new ResultSubmission { MatchId = m.MatchId, ContentHash = "c", Entrants = Result(m, win) },
            "h", null, null, null, new Dictionary<string, IReadOnlyList<ArchetypeRace>> { [H(1)] = before });
        Assert.Null(e);
        return s!.Entrants.Single(x => x.AccountId == H(1)).Challenges.Select(g => g.ChallengeId).ToList();
    }

    [Fact]
    public void ThirdLeadArchetypeBeaten_GrantsCh38_AQuitInBetweenDoesNot_ALossNever()
    {
        Assert.Contains("CH38", Granted(Sprint("m3", "R03", "R05"), true, Won("R01"), Won("R08")));
        Assert.DoesNotContain("CH38", Granted(Sprint("m3", "R03"), true,
            Won("R01"), new ArchetypeRace { AiRivals = new[] { "R05" }, Outcome = RunOutcome.Quit }, Won("R08")));
        Assert.DoesNotContain("CH38", Granted(Sprint("m3", "R03"), false, Won("R01"), Won("R08")));
    }

    [Fact]
    public void TwelfthArchetypeRaced_GrantsCh73()
    {
        ArchetypeRace six = new() { AiRivals = new[] { "R01", "R02", "R03", "R04", "R05" }, Outcome = RunOutcome.Finished, Placement = 6 };
        ArchetypeRace more = new() { AiRivals = new[] { "R06", "R07", "R08", "R09", "R10" }, Outcome = RunOutcome.Finished, Placement = 6 };
        Assert.DoesNotContain("CH73", Granted(Sprint("m3", "R11"), false, six, more)); // R11 repeats R07's archetype: 10
        Assert.Contains("CH73", Granted(Sprint("m3", "R12", "R14"), false, six, more)); // 12
    }

    [Fact]
    public async Task TheStore_ReadsSettledFreeplayRacesInOrder_SkippingCampaignAbortedAndAiFreeFields()
    {
        using var dir = new TempDir();
        var store = new SqliteGameStore(dir.File("archetypes.db"));
        await store.InitializeAsync();
        await store.EnsureAccountAsync(H(1));
        async Task Settle(MatchAssignment m, string outcome, int placement, bool settle = true)
        {
            await store.RecordAllocationAsync(new MatchRecord
            {
                MatchId = m.MatchId, ConvoyId = "cv", ServerId = "srv", ConfigJson = JsonSerializer.Serialize(m, MatchAllocator.Json), ResultsSecret = "x",
            });
            if (!settle)
            {
                await store.MarkAbortedAsync(m.MatchId);
                return;
            }
            await store.SettleAsync(new MatchSettlement
            {
                MatchId = m.MatchId, ResultsSha256 = "h-" + m.MatchId,
                Entrants = new[]
                {
                    new EntrantSettlement
                    {
                        AccountId = H(1), Receipt = new Receipt { MatchId = m.MatchId, AccountId = H(1), Outcome = outcome, Placement = placement },
                        Facts = new PayoutFacts { AuthoredExpectedSeconds = 180, Kind = EventKind.FreeplaySprint, Outcome = Enum.Parse<RunOutcome>(outcome), Placement = placement },
                    },
                },
            });
        }
        await Settle(Sprint("m1", "R01", "R02"), "Finished", 1);
        await Settle(Sprint("m2", "R08"), "Quit", 0);
        await Settle(Sprint("m3"), "Finished", 1);                                  // no AI: skipped
        await Settle(Sprint("m4", "R03") with { Kind = "campaign" }, "Finished", 1); // not Freeplay: skipped
        await Settle(Sprint("m5", "R05"), "Finished", 1, settle: false);            // aborted: skipped
        await Settle(Sprint("m6", "R09", "R10"), "Finished", 2);
        IReadOnlyList<ArchetypeRace> races = await store.FreeplayRacesAsync(H(1));
        Assert.Equal(new[] { "R01|Finished|1", "R08|Quit|0", "R09|Finished|2" },
            races.Select(r => $"{r.AiRivals[0]}|{r.Outcome}|{r.Placement}"));
        Assert.Equal(new[] { "R01", "R02" }, races[0].AiRivals); // the rivals from the roster, not the "ai-N" slot ids
    }
}
