using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// The race diary online (spec §5.3) and CH70 "The Other Side of the Card": a crew introduction is recorded as read only
/// once the account's Normal clear of its crew's stage has opened it; settlement grants CH70 for a legal finish in a race
/// with a crew member when every crew introduction was read beforehand — not with generic AI, not with five of six read.
/// </summary>
public sealed class DiaryTests : IDisposable
{
    readonly TempDir dir = new();
    readonly IReadOnlyList<DevAccountFixture> accounts;

    public DiaryTests() => accounts = TestData.WriteSeed(dir.File("seed.json"), 1);

    public void Dispose() => dir.Dispose();

    static string H(int i) => $"00000000-0000-4000-8000-{i:000000000000}";

    [Fact]
    public async Task CrewIntroduction_IsReadOnlyOnceItsStageIsClearedOnNormal()
    {
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), new ManualClock());
        HttpClient http = host.Authed(await host.SignInAsync(accounts[0]));
        (await http.GetAsync("/v1/me")).EnsureSuccessStatusCode(); // the account row
        HttpResponseMessage early = await http.PostAsJsonAsync("/v1/me/diary/read", new { entry = "crew:tea-hour" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/v1/me/diary/read", new { entry = "crew:nobody" })).StatusCode);

        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dir.File("controlplane.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO stage_clears (account_id, mode, stage, match_id) VALUES ($a, 'normal', 1, 'test-s01')";
            cmd.Parameters.AddWithValue("$a", accounts[0].AccountId);
            cmd.ExecuteNonQuery();
        }
        JsonElement first = await (await http.PostAsJsonAsync("/v1/me/diary/read", new { entry = "crew:tea-hour" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(first.GetProperty("recorded").GetBoolean());
        Assert.False(first.GetProperty("allCrewsRead").GetBoolean());
        JsonElement again = await (await http.PostAsJsonAsync("/v1/me/diary/read", new { entry = "crew:tea-hour" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(again.GetProperty("recorded").GetBoolean());
        JsonElement list = await http.GetFromJsonAsync<JsonElement>("/v1/me/diary");
        Assert.Equal(new[] { "crew:tea-hour" }, list.GetProperty("read").EnumerateArray().Select(x => x.GetString()));
        // A later crew stays closed until its own stage is cleared.
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/v1/me/diary/read", new { entry = "crew:rainline" })).StatusCode);
    }

    [Fact]
    public void Settlement_GrantsCh70_AfterAllSixRead_ForARaceWithACrewMember()
    {
        var service = new SettlementService(null!, null!, TestData.Content, null!, null!, null!, MusicUnlockManifest.Parse(
            """{"schema":"night-signal/music-unlocks@1","cues":[{"cueId":"menu-main","source":{"kind":"baseline"}}]}""",
            TestData.Content.Catalogue, TeamTrialCatalog.Fixture(TestData.Content.Catalogue)));
        Assert.Equal(6, TestData.Content.Crews.Count);
        MatchAssignment Race(params string[] ai) => new()
        {
            MatchId = "m_fp", ConvoyId = "cv", ServerId = "srv", Kind = "freeplay", CourseId = "C01", FreeplayMode = "sprint",
            Weather = "stage-default", Collision = "light-contact", CarCapPi = 999,
            Entrants = new List<AssignedEntrant> { new(H(1), H(1), "racer", "V01", 220, "p", "c", 1) },
            AiEntrants = ai.ToList(),
            Build = "b", Protocol = 1, ContentHash = "c", Seed = 1, ResultsUrl = "", TicketIssuer = "i", TicketAudience = "a",
        };
        static EntrantFacts F(string id, bool human, int place) => new()
        {
            EntrantId = id, Human = human, Outcome = RunOutcome.Finished, FinishTimeMicros = (200_000 + place * 1000) * 1000L, Placement = place,
            CheckpointFraction = 1, ActiveProgressVerified = true, ActivelyDroveLegalCourse = true, LegalProgressMetres = 3000,
        };
        bool Granted(MatchAssignment m, IReadOnlySet<string> readers)
        {
            var facts = new List<EntrantFacts> { F(H(1), true, 1) };
            facts.AddRange(m.AiEntrants.Select((id, i) => F(id, false, i + 2)));
            (MatchSettlement? s, string? e) = service.Compute(m, new ResultSubmission { MatchId = m.MatchId, ContentHash = "c", Entrants = facts }, "h", null, readers);
            Assert.Null(e);
            return s!.Entrants.Single(x => x.AccountId == H(1)).Challenges.Any(g => g.ChallengeId == "CH70");
        }
        var readAll = new HashSet<string> { H(1) };
        Assert.True(Granted(Race("R01"), readAll), "all six read, raced a Tea Hour member");
        Assert.False(Granted(Race("ai-1"), readAll), "generic AI is no crew member");
        Assert.False(Granted(Race("R01"), new HashSet<string>()), "not every introduction read");
    }
}
