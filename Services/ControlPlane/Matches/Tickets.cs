using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Security;

namespace NightSignal.ControlPlane.Matches;

public sealed record IssuedTicket(string Ticket, string Jti, DateTimeOffset ExpiresAt);

/// <summary>
/// Single-use match connection tickets (spec §3.2): ES256 JWTs signed with the control plane's own key,
/// aud "night-signal-gameserver", at most 60 s, binding account, convoy, match, role, build, protocol and content.
/// The game server verifies them with the public key from /v1/servers/ticket-jwks.json and burns each jti.
/// </summary>
public sealed class TicketIssuer : IDisposable
{
    readonly EcSigningKey key;
    readonly TicketOptions options;
    readonly TimeProvider clock;

    public TicketIssuer(IOptions<TicketOptions> options, IOptions<KeyOptions> keys, IHostEnvironment env, TimeProvider clock)
    {
        this.options = options.Value;
        this.clock = clock;
        if (!string.IsNullOrWhiteSpace(this.options.SigningKeyFile))
            key = EcSigningKey.LoadOrCreate(PathResolver.Resolve(env.ContentRootPath, this.options.SigningKeyFile), allowCreate: false);
        else if (env.IsDevelopment())
            key = EcSigningKey.LoadOrCreate(
                PathResolver.Resolve(PathResolver.Resolve(env.ContentRootPath, keys.Value.DevKeyDirectory), "ticket-signing.pem"), allowCreate: true);
        else
            throw new InvalidOperationException("Tickets:SigningKeyFile must be configured outside Development.");
    }

    public string Issuer => options.Issuer;
    public string KeyId => key.KeyId;
    public string JwksJson() => key.JwksJson();

    TimeSpan Lifetime => TimeSpan.FromSeconds(Math.Clamp(options.LifetimeSeconds, 5, TicketOptions.MaxLifetimeSeconds));

    public IssuedTicket Issue(string accountId, string convoyId, ActiveMatch match, string role)
    {
        if (role is not ("racer" or "spectator")) throw new ArgumentOutOfRangeException(nameof(role));
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset expires = now + Lifetime;
        string jti = Hashing.RandomId("", 16);
        string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = TicketOptions.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = key.Credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = accountId,
                ["convoy"] = convoyId,
                ["match"] = match.MatchId,
                ["role"] = role,
                ["build"] = match.Version.Build,
                ["protocol"] = match.Version.Protocol,
                ["content"] = match.Version.ContentHash,
                ["jti"] = jti,
            },
        });
        return new IssuedTicket(token, jti, expires);
    }

    public void Dispose() => key.Dispose();
}
