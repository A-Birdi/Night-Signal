using System.Net;

namespace NightSignal.ControlPlane.DevAuth;

public static class DevAuthEndpoints
{
    public const string JwksPath = "/dev/auth/.well-known/jwks.json";

    public sealed record PasswordGrant(string? Email, string? Password);
    public sealed record RefreshGrant([property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken);

    /// <summary>Mapped only after <see cref="DevAuthGuard.EnsureAllowed"/> succeeded.</summary>
    public static void MapDevAuth(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/dev/auth").AddEndpointFilter(async (ctx, next) =>
        {
            // Defence in depth: even on a loopback binding, refuse anything that did not come from loopback.
            IPAddress? remote = ctx.HttpContext.Connection.RemoteIpAddress;
            if (remote is not null && !IPAddress.IsLoopback(remote))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await next(ctx);
        });

        group.MapGet("/.well-known/jwks.json", (DevAuthKeys keys) => Results.Content(keys.Key.JwksJson(), "application/json"));

        group.MapPost("/token", (PasswordGrant body, DevAuthService auth) =>
            auth.SignIn(body.Email, body.Password) is { } tokens ? Results.Ok(tokens) : InvalidGrant());

        group.MapPost("/refresh", (RefreshGrant body, DevAuthService auth) =>
            auth.Refresh(body.RefreshToken) is { } tokens ? Results.Ok(tokens) : InvalidGrant());
    }

    static IResult InvalidGrant() =>
        Results.Json(new { error = "invalid_grant", error_description = "Invalid login credentials" }, statusCode: 400);
}
