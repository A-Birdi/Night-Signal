using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Identity;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Players;

namespace NightSignal.ControlPlane.Matches;

/// <summary>
/// Game-server facing API (server credential, never a player token) plus the player receipt read.
/// </summary>
public static class ServerEndpoints
{
    public const string KeyHeader = "X-NightSignal-Server-Key";
    public const string TicketJwksPath = "/v1/servers/ticket-jwks.json";

    public sealed record EndpointBody(string? Host, int Port);
    public sealed record RegisterBody(EndpointBody? Endpoint, string? Build, int Protocol, string? ContentHash, int MaxMatches = 1);
    public sealed record HeartbeatBody(int ActiveMatches);

    static string? AuthenticateServer(HttpContext ctx, GameServerRegistry registry, IOptions<GameServerOptions> options) =>
        registry.Authenticate(ctx.Request.Headers[KeyHeader].ToString(), options.Value.Credentials);

    static IResult Unauthorized() => PlayerEndpoints.Problem(401, "unauthorized", "A valid game-server key is required.");

    public static void MapServerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(TicketJwksPath, (TicketIssuer tickets) => Results.Content(tickets.JwksJson(), "application/json"));

        app.MapPost("/v1/servers/register", (RegisterBody body, HttpContext ctx, GameServerRegistry registry,
            IOptions<GameServerOptions> options, IOptions<CompatibilityOptions> compat, ContentService content, TicketIssuer tickets) =>
        {
            if (AuthenticateServer(ctx, registry, options) is not { } serverId) return Unauthorized();
            if (body.Endpoint is not { Host: { Length: > 0 and <= 253 } host, Port: > 0 and <= 65535 } || string.IsNullOrWhiteSpace(body.Build) ||
                body.MaxMatches is < 1 or > 64)
                return PlayerEndpoints.Problem(400, "invalid_registration", "endpoint.host, endpoint.port, build and maxMatches (1–64) are required.");
            if (body.Protocol != compat.Value.Protocol)
                return PlayerEndpoints.Problem(409, "protocol_mismatch", $"This control plane speaks protocol {compat.Value.Protocol}.");
            if (body.ContentHash != content.ContentHash)
                return PlayerEndpoints.Problem(409, "content_mismatch", "The server's content catalogue differs from the control plane's.");
            registry.Register(new GameServerRegistration(serverId, host, body.Endpoint.Port, body.Build, body.Protocol, body.ContentHash, body.MaxMatches));
            return Results.Ok(new
            {
                serverId,
                heartbeatIntervalSeconds = options.Value.HeartbeatIntervalSeconds,
                assignmentsUrl = $"/v1/servers/{serverId}/assignments",
                ticketIssuer = tickets.Issuer,
                ticketAudience = TicketOptions.Audience,
                ticketJwksUrl = TicketJwksPath,
            });
        });

        app.MapPost("/v1/servers/{serverId}/heartbeat", (string serverId, HeartbeatBody body, HttpContext ctx,
            GameServerRegistry registry, IOptions<GameServerOptions> options) =>
        {
            if (AuthenticateServer(ctx, registry, options) != serverId) return Unauthorized();
            return registry.Heartbeat(serverId, body.ActiveMatches)
                ? Results.Ok(new { ok = true })
                : PlayerEndpoints.Problem(404, "not_registered", "Register first.");
        });

        // Long-poll for frozen match configurations (includes the per-match results secret; server-only).
        app.MapGet("/v1/servers/{serverId}/assignments", async (string serverId, int? waitSeconds, HttpContext ctx,
            GameServerRegistry registry, IOptions<GameServerOptions> options) =>
        {
            if (AuthenticateServer(ctx, registry, options) != serverId) return Unauthorized();
            TimeSpan wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds ?? 25, 0, 30));
            IReadOnlyList<MatchAssignment>? assignments = await registry.PollAsync(serverId, wait, ctx.RequestAborted);
            return assignments is null
                ? PlayerEndpoints.Problem(404, "not_registered", "Register first.")
                : Results.Json(new { assignments }, MatchAllocator.Json);
        });

        app.MapPost("/v1/servers/{serverId}/assignments/{matchId}/ack", (string serverId, string matchId, HttpContext ctx,
            GameServerRegistry registry, IOptions<GameServerOptions> options) =>
        {
            if (AuthenticateServer(ctx, registry, options) != serverId) return Unauthorized();
            return registry.Acknowledge(serverId, matchId)
                ? Results.Ok(new { ok = true })
                : PlayerEndpoints.Problem(404, "no_pending_assignment", "No pending assignment with that match ID.");
        });

        // Results: server key AND an HMAC of the exact raw body with the per-match secret.
        app.MapPost("/v1/matches/{matchId}/results", async (string matchId, HttpContext ctx, GameServerRegistry registry,
            IOptions<GameServerOptions> options, SettlementService settlement) =>
        {
            if (AuthenticateServer(ctx, registry, options) is not { } serverId) return Unauthorized();
            byte[]? body = await ReadBodyAsync(ctx.Request, SettlementService.MaxBodyBytes, ctx.RequestAborted);
            if (body is null) return PlayerEndpoints.Problem(413, "too_large", "Result body too large.");
            string? signature = ctx.Request.Headers[SettlementService.SignatureHeader].FirstOrDefault();
            SubmissionResult r = await settlement.SubmitAsync(serverId, matchId, body, signature, ctx.RequestAborted);
            return Results.Json(r.Body, statusCode: r.StatusCode);
        });

        // Ghosts (spec §8): one per human entrant, signed like the results, before them.
        app.MapPost("/v1/matches/{matchId}/ghosts/{accountId}", async (string matchId, string accountId, HttpContext ctx, GameServerRegistry registry,
            IOptions<GameServerOptions> options, SettlementService settlement) =>
        {
            if (AuthenticateServer(ctx, registry, options) is not { } serverId) return Unauthorized();
            byte[]? body = await ReadBodyAsync(ctx.Request, SettlementService.MaxGhostBytes, ctx.RequestAborted);
            if (body is null) return PlayerEndpoints.Problem(413, "too_large", "Ghost body too large.");
            string? signature = ctx.Request.Headers[SettlementService.SignatureHeader].FirstOrDefault();
            SubmissionResult r = await settlement.SubmitGhostAsync(serverId, matchId, accountId, body, signature, ctx.RequestAborted);
            return Results.Json(r.Body, statusCode: r.StatusCode);
        });

        // Players read their own kept ghosts for a course and format (one per ruleset; the client races the compatible one).
        app.MapGet("/v1/me/ghosts/{courseId}/{format}", async (string courseId, string format, ClaimsPrincipal user, IResultLedger ledger, CancellationToken ct) =>
            Results.Content("{\"ghosts\":[" + string.Join(",", (await ledger.GhostsAsync(user.AccountId(), courseId, format, ct)).Select(g => g.Json)) + "]}",
                "application/json")).RequireAuthorization();

        // Players read their own itemized receipt (never submit money, RP or times).
        app.MapGet("/v1/matches/{matchId}/receipt", async (string matchId, ClaimsPrincipal user, IResultLedger ledger, CancellationToken ct) =>
        {
            string account = user.AccountId();
            if (await ledger.GetReceiptJsonAsync(matchId, account, ct) is { } json)
                return Results.Content(json, "application/json");
            MatchRecord? match = await ledger.GetMatchAsync(matchId, ct);
            MatchAssignment? config = match is null ? null : JsonSerializer.Deserialize<MatchAssignment>(match.ConfigJson, MatchAllocator.Json);
            if (config is null || config.Entrants.All(e => e.AccountId != account))
                return PlayerEndpoints.Problem(404, "not_found", "No receipt for you in that match.");
            return match!.State == "aborted"
                ? Results.Json(new { matchId, status = "aborted", message = "The event was aborted: no results, rank or progression were issued." })
                : Results.Json(new { matchId, status = "pending", message = "Settlement pending." }, statusCode: 202);
        }).RequireAuthorization();
    }

    static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        if (request.ContentLength > limit) return null;
        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > limit) return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
