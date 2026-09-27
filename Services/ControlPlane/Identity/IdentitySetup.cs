using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.DevAuth;

namespace NightSignal.ControlPlane.Identity;

/// <summary>
/// Access-token verification (spec §3.2): signature against a trusted JWKS, issuer, audience, expiry and subject.
/// Production reads the Supabase Auth JWKS over HTTPS; DevAuth supplies its own JWKS document in-process.
/// Everything else (handler, parameters, algorithms, subject rule) is the same code path.
/// </summary>
public static partial class IdentitySetup
{
    /// <summary>Asymmetric algorithms only. Supabase projects must use asymmetric JWT signing keys (ES256/RS256).</summary>
    public static readonly string[] AcceptedAlgorithms = { SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256 };

    public static IServiceCollection AddNightSignalIdentity(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<IdentityOptions>, IOptions<DevAuthOptions>, IServiceProvider>((o, identity, dev, sp) =>
            {
                bool devAuth = dev.Value.Enabled;
                string issuer = devAuth ? dev.Value.Issuer : identity.Value.Issuer;
                string address = devAuth ? DevAuthEndpoints.JwksPath : identity.Value.JwksUrl;
                IDocumentRetriever retriever = devAuth
                    ? sp.GetRequiredService<DevAuthKeys>()
                    : new HttpDocumentRetriever { RequireHttps = true };

                o.MapInboundClaims = false;
                o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    address, new JwksConfigurationRetriever(issuer), retriever);
                o.TokenValidationParameters = BuildParameters(issuer, identity.Value.Audience, identity.Value.ClockSkewSeconds);
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                    {
                        if (!IsValidAccountId(ctx.Principal?.FindFirstValue("sub")))
                            ctx.Fail("Token subject is missing or malformed.");
                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();
        services.AddSingleton<AccessTokenValidator>();
        return services;
    }

    public static TokenValidationParameters BuildParameters(string issuer, string audience, int clockSkewSeconds) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        ValidAlgorithms = AcceptedAlgorithms,
        ClockSkew = TimeSpan.FromSeconds(clockSkewSeconds),
        NameClaimType = "sub",
        RoleClaimType = "role",
    };

    /// <summary>Account IDs are the provider's stable <c>sub</c> (Supabase: a UUID). Never a display name.</summary>
    public static bool IsValidAccountId(string? sub) => sub is not null && AccountIdPattern().IsMatch(sub);

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex AccountIdPattern();

    public static string AccountId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? throw new InvalidOperationException("Authenticated principal without subject.");
}

/// <summary>Turns a bare JWKS document into the signing-key configuration JwtBearer consumes.</summary>
public sealed class JwksConfigurationRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        string json = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
        var jwks = new JsonWebKeySet(json);
        var config = new OpenIdConnectConfiguration { Issuer = issuer, JsonWebKeySet = jwks };
        foreach (SecurityKey key in jwks.GetSigningKeys())
            config.SigningKeys.Add(key);
        return config;
    }
}

/// <summary>
/// Validates a raw access token with exactly the JwtBearer configuration (used when a long-lived control channel
/// re-authenticates with a refreshed token).
/// </summary>
public sealed class AccessTokenValidator(IOptionsMonitor<JwtBearerOptions> options)
{
    public async Task<(ClaimsPrincipal? Principal, DateTimeOffset ExpiresAt)> ValidateAsync(string token, CancellationToken ct)
    {
        JwtBearerOptions o = options.Get(JwtBearerDefaults.AuthenticationScheme);
        OpenIdConnectConfiguration cfg = await o.ConfigurationManager!.GetConfigurationAsync(ct);
        TokenValidationParameters p = o.TokenValidationParameters.Clone();
        p.IssuerSigningKeys = cfg.SigningKeys;
        TokenValidationResult result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(token, p);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            return (null, default);
        var principal = new ClaimsPrincipal(result.ClaimsIdentity);
        if (!IdentitySetup.IsValidAccountId(principal.FindFirstValue("sub")))
            return (null, default);
        return (principal, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }
}
