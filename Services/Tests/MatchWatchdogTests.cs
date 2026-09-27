using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>System failure (spec §4.4): lost or silent game servers abort the event without results and free the convoy.</summary>
public sealed class MatchWatchdogTests : IAsyncLifetime
{
    sealed class Notifier : IConvoyNotifier
    {
        public readonly ConcurrentQueue<(string Account, string Type)> Sent = new();
        public void Send(string accountId, string type, long revision, object payload) => Sent.Enqueue((accountId, type));
    }

    static readonly ClientVersion V = new("b", 1, "c");
    static string Id(int i) => $"00000000-0000-4000-8000-{i:000000000000}";
    readonly TempDir dir = new();
    readonly ManualClock clock = new(new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero));
    readonly Notifier notifier = new();
    readonly IOptions<GameServerOptions> options = Options.Create(new GameServerOptions { StaleAfterSeconds = 20, MaxMatchMinutes = 45 });
    ConvoyDirectory convoys = null!;
    GameServerRegistry registry = null!;
    SqliteGameStore store = null!;
    MatchWatchdog watchdog = null!;

    public async Task InitializeAsync()
    {
        store = new SqliteGameStore(dir.File("w.db"));
        await store.InitializeAsync();
        convoys = new ConvoyDirectory(clock, TestData.Content, notifier);
        registry = new GameServerRegistry(clock, options);
        watchdog = new MatchWatchdog(convoys, registry, store, clock, options, NullLogger<MatchWatchdog>.Instance);
    }

    public Task DisposeAsync()
    {
        dir.Dispose();
        return Task.CompletedTask;
    }

    async Task<string> StartMatchAsync()
    {
        static MemberProgress P(string id) => new(id, new bool[30], new bool[30]);
        convoys.Connected(Id(1), V);
        convoys.Create(Id(1), new MemberInfo("Solo", P(Id(1))), ConvoyPrivacy.InviteOnly);
        convoys.UpdateLoadout(Id(1), new LoadoutInfo("V01", 220, "p", "c"));
        long d = JsonSerializer.SerializeToElement(convoys.ProposeDestination(Id(1), Destination.CampaignNormal).Value).GetProperty("proposalRevision").GetInt64();
        convoys.CommitDestination(Id(1), d);
        clock.Advance(TimeSpan.FromSeconds(15));
        long e = JsonSerializer.SerializeToElement(convoys.ProposeEvent(Id(1), new EventRequest("S01", null, null, null, null, null, null)).Value)
            .GetProperty("proposalRevision").GetInt64();
        convoys.SetReady(Id(1), e, 1, true);
        MatchPlan plan = convoys.BeginStart(Id(1), e, new Dictionary<string, MemberProgress> { [Id(1)] = P(Id(1)) }).Plan!;

        registry.Register(new GameServerRegistration("srv", "127.0.0.1", 7777, "b", 1, "c", 1));
        await store.RecordAllocationAsync(new MatchRecord { MatchId = "m_w", ConvoyId = plan.ConvoyId, ServerId = "srv", ConfigJson = "{}", ResultsSecret = "s" });
        convoys.CompleteStart(plan, new ActiveMatch("m_w", "srv", "127.0.0.1", 7777, V, new[] { Id(1) }));
        return "m_w";
    }

    string Phase() => JsonSerializer.SerializeToElement(convoys.SnapshotFor(Id(1)).Snapshot).GetProperty("phase").GetString()!;

    [Fact]
    public async Task HealthyMatch_IsLeftAlone()
    {
        await StartMatchAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await watchdog.SweepAsync(CancellationToken.None);
        Assert.Equal("InMatch", Phase());
        Assert.Equal("allocated", (await store.GetMatchAsync("m_w"))!.State);
    }

    [Fact]
    public async Task LostServer_AbortsWithoutResults_AndReleasesTheConvoy()
    {
        string m = await StartMatchAsync();
        clock.Advance(TimeSpan.FromSeconds(21)); // no heartbeat or poll
        await watchdog.SweepAsync(CancellationToken.None);
        Assert.Equal("aborted", (await store.GetMatchAsync(m))!.State);
        Assert.Equal("EventSelection", Phase());
        Assert.Contains(notifier.Sent, s => s.Type == "match.aborted");
        Assert.Equal(0, (await store.GetSnapshotAsync(Id(1))).Balance);
    }

    [Fact]
    public async Task OverdueMatch_IsAborted_EvenWithAHealthyServer()
    {
        string m = await StartMatchAsync();
        for (int i = 0; i < 46; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            registry.Heartbeat("srv", 1);
        }
        await watchdog.SweepAsync(CancellationToken.None);
        Assert.Equal("aborted", (await store.GetMatchAsync(m))!.State);
    }

    [Fact]
    public async Task SettledMatch_IsNeverAborted()
    {
        string m = await StartMatchAsync();
        await store.SettleAsync(new MatchSettlement
        {
            MatchId = m, ResultsSha256 = "h",
            Entrants = new[] { new EntrantSettlement { AccountId = Id(1), Receipt = new Receipt(), Facts = new PayoutFacts
                { AuthoredExpectedSeconds = 180, Kind = EventKind.CampaignStage, Outcome = RunOutcome.Finished, Placement = 1 } } },
        });
        clock.Advance(TimeSpan.FromMinutes(60));
        await watchdog.SweepAsync(CancellationToken.None);
        Assert.Equal("settled", (await store.GetMatchAsync(m))!.State);
        Assert.Equal("EventSelection", Phase());
        Assert.True((await store.GetSnapshotAsync(Id(1))).Balance > 0);
    }
}
