using System.Security.Claims;
using NightSignal.ControlPlane.Identity;
using NightSignal.ControlPlane.Players;
using NightSignal.ControlPlane.Security;

namespace NightSignal.ControlPlane.Garage;

/// <summary>
/// ONLINE Garage REST API (Addendum 02 §8–10; docs/NETWORKING.md §2.6). The account is always the verified JWT subject and
/// only its own car instances are visible. Every mutation quotes the workspace revision it was computed from.
/// </summary>
public static class GarageEndpoints
{
    public static void MapGarageEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/v1/me/garage").RequireAuthorization();

        g.MapGet("/cars", async (ClaimsPrincipal user, GarageService garage, CancellationToken ct) =>
            Reply(await garage.ListCarsAsync(user.AccountId(), ct)));

        g.MapGet("/cars/{instanceId}", async (string instanceId, ClaimsPrincipal user, GarageService garage, CancellationToken ct) =>
            Reply(await garage.GetCarAsync(user.AccountId(), instanceId, ct)));

        g.MapGet("/cars/{instanceId}/parts", async (string instanceId, ClaimsPrincipal user, GarageService garage, CancellationToken ct) =>
            Reply(await garage.PartsAsync(user.AccountId(), instanceId, ct)));

        g.MapPost("/cars/{instanceId}/operations", async (string instanceId, GarageOpRequest body, ClaimsPrincipal user, GarageService garage,
            RateLimiter limiter, CancellationToken ct) =>
        {
            string account = user.AccountId();
            if (!limiter.TryAcquire($"garage-op/{account}", SocialLimits.GarageOperation, out long retry)) return PlayerEndpoints.RateLimited(retry);
            return Reply(await garage.OperateAsync(account, instanceId, body, ct));
        });

        g.MapPost("/cars/{instanceId}/quote", async (string instanceId, QuoteRequest? body, ClaimsPrincipal user, GarageService garage,
            RateLimiter limiter, CancellationToken ct) =>
        {
            string account = user.AccountId();
            if (!limiter.TryAcquire($"garage-quote/{account}", SocialLimits.GarageQuote, out long retry)) return PlayerEndpoints.RateLimited(retry);
            return Reply(await garage.CreateQuoteAsync(account, instanceId, body ?? new QuoteRequest(), ct));
        });

        g.MapPost("/cars/{instanceId}/quote/{quoteId}/settle", async (string instanceId, string quoteId, SettleRequest? body, ClaimsPrincipal user,
            GarageService garage, RateLimiter limiter, CancellationToken ct) =>
        {
            string account = user.AccountId();
            if (quoteId.Length is 0 or > GarageWire.MaxIdLength) return PlayerEndpoints.Problem(404, "unknown_quote", "No such quote for this car.");
            if (!limiter.TryAcquire($"garage-settle/{account}", SocialLimits.GarageSettle, out long retry)) return PlayerEndpoints.RateLimited(retry);
            return Reply(await garage.SettleQuoteAsync(account, instanceId, quoteId, body?.Confirm == true, ct));
        });
    }

    static IResult Reply(GarageReply r) => Results.Json(r.Body, statusCode: r.Status);
}
