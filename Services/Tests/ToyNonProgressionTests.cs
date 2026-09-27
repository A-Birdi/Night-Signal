using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Core.Toys;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Addendum 02 D209 / §11 / C09: the toys are an explicit non-progression domain. A toy result kind — however it is spelled
/// — is refused by settlement AND by the ledger, so no Credits, RP, first clears, records, challenges or unlocks can come
/// from a diversion; and the toy host itself has no path to the economy at all.
/// </summary>
public sealed class ToyNonProgressionTests : IAsyncLifetime
{
    const string H1 = "00000000-0000-4000-8000-000000000001";
    readonly TempDir dir = new();
    SqliteGameStore store = null!;

    public async Task InitializeAsync()
    {
        store = new SqliteGameStore(dir.File("np.db"));
        await store.InitializeAsync();
        await store.EnsureAccountAsync(H1);
    }

    public Task DisposeAsync()
    {
        dir.Dispose();
        return Task.CompletedTask;
    }

    static MatchAssignment Assignment(string matchId, string kind, string? freeplayMode = null) => new()
    {
        MatchId = matchId, ConvoyId = "cv_np", ServerId = "srv", Kind = kind, Mode = null, StageId = null, StageNumber = 0,
        StageType = null, CourseId = "C01", FreeplayMode = freeplayMode, Weather = "stage-default", Collision = "light-contact", CarCapPi = 999,
        Entrants = new[] { new AssignedEntrant(H1, "Driver", "racer", "V01", 220, "p", "c", 1) }, AiEntrants = Array.Empty<string>(),
        Build = "b", Protocol = 2, ContentHash = "content", Seed = 1, ResultsUrl = $"/v1/matches/{matchId}/results", TicketIssuer = "i",
        TicketAudience = "a",
    };

    [Theory]
    [InlineData("toy-nonprogression", null)]
    [InlineData("toy.capclash.best", null)]
    [InlineData("TOY-pocketcircuit-lap", null)]
    [InlineData("freeplay", "toy.pocketcircuit")]
    public async Task Settlement_RefusesAToyKind_WithoutSettlingAbortingOrPaying(string kind, string? freeplayMode)
    {
        string matchId = "m_np_" + Guid.NewGuid().ToString("N")[..8];
        string secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        await store.RecordAllocationAsync(new MatchRecord
        {
            MatchId = matchId, ConvoyId = "cv_np", ServerId = "srv", ResultsSecret = secret,
            ConfigJson = JsonSerializer.Serialize(Assignment(matchId, kind, freeplayMode), MatchAllocator.Json),
        });
        var settlement = new SettlementService(store, store, TestData.Content, new ConvoyDirectory(new ManualClock(), TestData.Content, new RecordingNotifier()),
            new GameServerRegistry(TimeProvider.System, Options.Create(new GameServerOptions())), NullLogger<SettlementService>.Instance);
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            matchId, contentHash = "content", aborted = false,
            entrants = new[]
            {
                new
                {
                    entrantId = H1, human = true, outcome = "Finished", finishTimeMicros = 60_000_000L, placement = 1, clean = true,
                    checkpointFraction = 1.0, activeProgressVerified = true, activelyDroveLegalCourse = true, legalProgressMetres = 3000.0,
                    rawDriftScore = 0L, contractsPassed = 0, challengesCompleted = Array.Empty<string>(),
                },
            },
        }));

        SubmissionResult result = await settlement.SubmitAsync("srv", matchId, body, SettlementService.Sign(secret, body), CancellationToken.None);

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(ProgressionDomain.ErrorCode, JsonSerializer.SerializeToElement(result.Body).GetProperty("error").GetString());
        Assert.Equal("allocated", (await store.GetMatchAsync(matchId))!.State); // neither settled nor aborted by a spoofed kind
        PlayerSnapshot player = await store.GetSnapshotAsync(H1);
        Assert.Equal(0, player.Balance);
        Assert.Null(await store.GetReceiptJsonAsync(matchId, H1));
    }

    [Fact]
    public void Compute_RefusesAToyKind_EvenWhenCalledDirectly()
    {
        var service = new SettlementService(null!, null!, TestData.Content, null!, null!, null!);
        (MatchSettlement? s, string? error) = service.Compute(Assignment("m_x", NonProgression.Domain),
            new ResultSubmission { MatchId = "m_x", ContentHash = "content" }, "h");
        Assert.Null(s);
        Assert.Contains("non-progression", error);
    }

    [Fact]
    public async Task TheLedger_RefusesToySettlements_AndToySoundtrackGrants_WithNoEconomicEffect()
    {
        await store.RecordAllocationAsync(new MatchRecord { MatchId = "m_toy", ConvoyId = "cv", ServerId = "srv", ConfigJson = "{}", ResultsSecret = "x" });
        var spoofed = new MatchSettlement
        {
            MatchId = "m_toy",
            ResultsSha256 = "h",
            Entrants = new[]
            {
                new EntrantSettlement
                {
                    AccountId = H1,
                    Facts = new PayoutFacts { AuthoredExpectedSeconds = 180, Kind = EventKind.FreeplaySprint, Outcome = RunOutcome.Finished, Placement = 1 },
                    Challenges = new[] { new ChallengeGrant("CH01", ChallengeTier.Bronze, 3_000, 40, "COS-CH01") },
                    Receipt = new Receipt { MatchId = "m_toy", AccountId = H1, EventKind = NonProgression.Domain },
                },
            },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SettleAsync(spoofed));
        Assert.Equal("allocated", (await store.GetMatchAsync("m_toy"))!.State);
        PlayerSnapshot player = await store.GetSnapshotAsync(H1);
        Assert.Equal(0, player.Balance);
        Assert.Empty(player.Challenges);

        Assert.False(await store.GrantMusicCueAsync(H1, "OST-01", "toy.greenlight.chain", "chain-10", null));
        Assert.Empty((await store.GetSnapshotAsync(H1)).Music);
    }

    [Fact]
    public void TheToyHost_HasNoPathToTheEconomy()
    {
        // Structural: nothing in the toy host references the wallet/ledger/player stores or settlement.
        Type[] economy = { typeof(IResultLedger), typeof(IPlayerStore), typeof(SettlementService), typeof(MatchAllocator) };
        foreach (Type t in typeof(NightSignal.ControlPlane.Toys.ConvoyToys).Assembly.GetTypes()
                     .Where(t => t.Namespace == "NightSignal.ControlPlane.Toys"))
        {
            IEnumerable<Type> used = t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Concat(t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                    .Select(f => f.FieldType));
            Assert.DoesNotContain(used, u => economy.Contains(u));
        }
        Assert.True(NonProgression.IsToyDomain(DowntimeCodec.Deserialize(
            new DowntimeSession("cs-np", TestData.Toys.Content!, 1, 1).SnapshotJson()).Domain));
    }
}
