using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Matches;

public sealed record AssignedEntrant(string AccountId, string DisplayName, string Role, string CarId, int CarPi,
    string PerformanceHash, string CosmeticHash, long LoadoutRevision);

public sealed record AssignedBenchmark(string Kind, long TargetTimeMs, long RawDriftTarget, long HardTimeoutMs, bool Provisional, string Source,
    bool RequiresBeatingFeaturedRival = false);

/// <summary>Frozen Team Trial rules for the game server and settlement (Addendum 01 §3).</summary>
public sealed record TrialAssignment(string TrialId, string Kind, string Difficulty, long HardTimeoutMs, long ParticipationEnvelopeMs,
    int VictoryPlacement, int DefeatPlacement, string TiePolicy, bool Provisional);

/// <summary>The frozen match configuration delivered to the game server (spec §4.3). Stored (minus the secret) in matches.config_json.</summary>
public sealed record MatchAssignment
{
    public required string MatchId { get; init; }
    public required string ConvoyId { get; init; }
    public required string ServerId { get; init; }
    public required string Kind { get; init; }
    public string? Mode { get; init; }
    public string? StageId { get; init; }
    public int StageNumber { get; init; }
    public string? StageType { get; init; }
    public required string CourseId { get; init; }
    public string? FreeplayMode { get; init; }
    public required string Weather { get; init; }
    public required string Collision { get; init; }
    public required int CarCapPi { get; init; }
    public required IReadOnlyList<AssignedEntrant> Entrants { get; init; }
    public required IReadOnlyList<string> AiEntrants { get; init; }
    /// <summary>Typed roster: every human and AI with kind, team (player/opposing), role and driver identity.</summary>
    public IReadOnlyList<RosterSlot> Roster { get; init; } = Array.Empty<RosterSlot>();
    /// <summary>Campaign: the live featured rival (entrant ID) a qualifying human must beat on encounter stages.</summary>
    public string? FeaturedRival { get; init; }
    /// <summary>Event-scoped guest passes frozen at allocation (Addendum 01 §5.2).</summary>
    public IReadOnlyList<GuestPass> GuestPasses { get; init; } = Array.Empty<GuestPass>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Sponsors { get; init; }
    public IReadOnlyList<string>? CupLegs { get; init; }
    public TrialAssignment? Trial { get; init; }
    public AssignedBenchmark? Benchmark { get; init; }
    public bool PurePvP { get; init; }
    public string? GridNote { get; init; }
    public required string Build { get; init; }
    public required int Protocol { get; init; }
    public required string ContentHash { get; init; }
    public required int Seed { get; init; }
    public required string ResultsUrl { get; init; }
    public required string TicketIssuer { get; init; }
    public required string TicketAudience { get; init; }
    /// <summary>Base64url HMAC key for signing the results body. Delivered once to the server; never to clients.</summary>
    public string? ResultsSecret { get; init; }
}

/// <summary>Allocates a registered game server for a frozen plan, records the match and waits for the server's ack.</summary>
public sealed class MatchAllocator(GameServerRegistry registry, IResultLedger ledger, ContentService content, TicketIssuer tickets,
    ILogger<MatchAllocator> log, TeamTrialCatalog? trials = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(ActiveMatch? Match, string? Error)> AllocateAsync(MatchPlan plan, CancellationToken ct)
    {
        GameServerRegistration? server = registry.Select(plan.Version);
        if (server is null)
            return (null, "No compatible game server is available for this build and content version.");

        string matchId = Hashing.RandomId("m_", 12);
        string secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        MatchAssignment assignment = Build(plan, server, matchId, secret);
        await ledger.RecordAllocationAsync(new MatchRecord
        {
            MatchId = matchId, ConvoyId = plan.ConvoyId, ServerId = server.ServerId, ResultsSecret = secret,
            ConfigJson = JsonSerializer.Serialize(assignment with { ResultsSecret = null }, Json),
        }, ct);

        if (!await registry.AssignAsync(server.ServerId, assignment, ct))
        {
            await ledger.MarkAbortedAsync(matchId, CancellationToken.None);
            log.LogWarning("Match {MatchId} not acknowledged by server {ServerId}; aborted without results", matchId, server.ServerId);
            return (null, "The game server did not accept the match in time. Nobody was charged; try again.");
        }
        log.LogInformation("Match {MatchId} allocated on {ServerId} for convoy {ConvoyId} ({Humans} humans, {Ai} AI)",
            matchId, server.ServerId, plan.ConvoyId, plan.Entrants.Count, plan.AiEntrants.Count);
        return (new ActiveMatch(matchId, server.ServerId, server.Host, server.Port, plan.Version,
            plan.Entrants.Select(e => e.AccountId).ToList()), null);
    }

    MatchAssignment Build(MatchPlan plan, GameServerRegistration server, string matchId, string secret)
    {
        EventSettings s = plan.Settings;
        AssignedBenchmark? benchmark = null;
        if (s.Kind == "campaign")
        {
            StageDef stage = content.Catalogue.Stage(s.StageId!);
            BenchmarkInfo b = content.BenchmarkFor(stage, s.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal);
            benchmark = new AssignedBenchmark(b.Benchmark.Kind.ToString(), b.Benchmark.TargetTimeMs, b.Benchmark.RawDriftTarget,
                b.Benchmark.HardTimeoutMs, b.Provisional, b.Source, b.Benchmark.RequiresBeatingFeaturedRival);
        }
        // Frozen before readiness: roster, team membership, car cap, metric, tie policy and hard timeout.
        TrialAssignment? trial = null;
        if (s.Kind == "trial" && (trials ?? TeamTrialCatalog.Fixture(content.Catalogue)).Find(s.TrialId) is { } t)
            trial = new TrialAssignment(t.Id, t.Kind, s.Difficulty ?? t.Difficulties[0].Id, t.HardTimeoutMs, t.ParticipationEnvelopeMs,
                t.VictoryPlacement, t.DefeatPlacement, t.TiePolicy, t.Provisional);
        return new MatchAssignment
        {
            MatchId = matchId, ConvoyId = plan.ConvoyId, ServerId = server.ServerId, Kind = s.Kind, Mode = s.Mode,
            StageId = s.StageId, StageNumber = s.StageNumber, StageType = s.StageType, CourseId = s.CourseId,
            FreeplayMode = s.FreeplayMode, Weather = s.Weather, Collision = s.Collision, CarCapPi = s.CarCapPi,
            Entrants = plan.Entrants.Select(e => new AssignedEntrant(e.AccountId, e.DisplayName, "racer", e.Loadout.CarId,
                e.Loadout.CarPi, e.Loadout.PerformanceHash, e.Loadout.CosmeticHash, e.LoadoutRevision)).ToList(),
            AiEntrants = plan.AiEntrants, Roster = plan.Roster, FeaturedRival = plan.FeaturedRival, GuestPasses = plan.GuestPasses,
            Sponsors = plan.Sponsors.Count > 0 ? plan.Sponsors : null, CupLegs = s.CupLegs, Trial = trial, Benchmark = benchmark,
            PurePvP = plan.PurePvP, GridNote = plan.GridNote, Build = plan.Version.Build, Protocol = plan.Version.Protocol,
            ContentHash = plan.Version.ContentHash, Seed = RandomNumberGenerator.GetInt32(int.MaxValue),
            ResultsUrl = $"/v1/matches/{matchId}/results", TicketIssuer = tickets.Issuer,
            TicketAudience = Configuration.TicketOptions.Audience, ResultsSecret = secret,
        };
    }
}
