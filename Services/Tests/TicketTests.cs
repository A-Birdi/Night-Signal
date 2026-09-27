using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Security;
using NightSignal.Services.Tests.Infrastructure;
using NightSignal.TicketValidation;

namespace NightSignal.Services.Tests;

/// <summary>Control-plane ticket issuance verified by the netstandard2.1 validator the game server will use.</summary>
public sealed class TicketTests : IDisposable
{
    readonly TempDir dir = new();
    readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    readonly TicketIssuer issuer;
    readonly EcSigningKey key; // the same PEM the issuer generated, to craft malicious variants
    readonly TicketValidator validator;
    static readonly ClientVersion Version = new("build-7", 1, "content-abc");
    static readonly ActiveMatch Match = new("m_1", "srv", "127.0.0.1", 7777, Version, new[] { "acct-0001" });
    static readonly ExpectedTicketContext Expected = new() { MatchId = "m_1", Build = "build-7", Protocol = 1, ContentHash = "content-abc" };

    public TicketTests()
    {
        issuer = new TicketIssuer(Options.Create(new TicketOptions()), Options.Create(new KeyOptions { DevKeyDirectory = dir.Path }),
            new TestHostEnvironment(dir.Path), clock);
        key = EcSigningKey.LoadOrCreate(Path.Combine(dir.Path, "ticket-signing.pem"), allowCreate: false);
        validator = NewValidator();
    }

    TicketValidator NewValidator() => new(TicketKeySet.FromJwks(issuer.JwksJson()), new TicketValidationParameters { Issuer = issuer.Issuer },
        new InMemoryTicketReplayCache(), () => clock.GetUtcNow());

    public void Dispose()
    {
        issuer.Dispose();
        key.Dispose();
        dir.Dispose();
    }

    string Craft(Action<SecurityTokenDescriptor>? mutate = null, SigningCredentials? credentials = null)
    {
        DateTime now = clock.GetUtcNow().UtcDateTime;
        var d = new SecurityTokenDescriptor
        {
            Issuer = issuer.Issuer, Audience = TicketOptions.Audience, IssuedAt = now, NotBefore = now, Expires = now.AddSeconds(60),
            SigningCredentials = credentials ?? key.Credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "acct-0001", ["convoy"] = "cv_1", ["match"] = "m_1", ["role"] = "racer", ["build"] = "build-7",
                ["protocol"] = 1, ["content"] = "content-abc", ["jti"] = Guid.NewGuid().ToString("N"),
            },
        };
        mutate?.Invoke(d);
        return new JsonWebTokenHandler().CreateToken(d);
    }

    [Fact]
    public void IssuedTicket_IsValid_OnceOnly()
    {
        IssuedTicket t = issuer.Issue("acct-0001", "cv_1", Match, "racer");
        TicketValidationResult first = validator.Validate(t.Ticket, Expected);
        Assert.True(first.IsValid, first.Failure.ToString());
        Assert.Equal("acct-0001", first.Claims.Subject);
        Assert.Equal("cv_1", first.Claims.Convoy);
        Assert.Equal("racer", first.Claims.Role);
        Assert.Equal(t.Jti, first.Claims.Jti);
        Assert.True(first.Claims.ExpiresAt - first.Claims.IssuedAt <= TimeSpan.FromSeconds(60));
        Assert.Equal(TicketFailure.Replayed, validator.Validate(t.Ticket, Expected).Failure);
    }

    [Fact]
    public void ConcurrentUseOfOneTicket_AdmitsExactlyOneConnection()
    {
        string ticket = issuer.Issue("acct-0001", "cv_1", Match, "racer").Ticket;
        TicketValidationResult[] results = Enumerable.Range(0, 32).AsParallel().Select(_ => validator.Validate(ticket, Expected)).ToArray();
        Assert.Single(results, r => r.IsValid);
        Assert.All(results.Where(r => !r.IsValid), r => Assert.Equal(TicketFailure.Replayed, r.Failure));
    }

    [Fact]
    public void ExpiredTicket_IsRejected()
    {
        string ticket = issuer.Issue("acct-0001", "cv_1", Match, "racer").Ticket;
        clock.Advance(TimeSpan.FromSeconds(66)); // 60 s lifetime + 5 s skew
        Assert.Equal(TicketFailure.Expired, validator.Validate(ticket, Expected).Failure);
    }

    [Fact]
    public void TicketsCannotOutliveSixtySeconds()
    {
        var longLived = new TicketIssuer(Options.Create(new TicketOptions { LifetimeSeconds = 3600 }),
            Options.Create(new KeyOptions { DevKeyDirectory = dir.Path }), new TestHostEnvironment(dir.Path), clock);
        IssuedTicket t = longLived.Issue("acct-0001", "cv_1", Match, "racer");
        Assert.True(t.ExpiresAt - clock.GetUtcNow() <= TimeSpan.FromSeconds(60));
        Assert.Equal(TicketFailure.LifetimeTooLong,
            validator.Validate(Craft(d => d.Expires = d.IssuedAt!.Value.AddMinutes(10)), Expected).Failure);
    }

    [Fact]
    public void WrongAudienceOrIssuer_IsRejected()
    {
        Assert.Equal(TicketFailure.WrongAudience, validator.Validate(Craft(d => d.Audience = "authenticated"), Expected).Failure);
        Assert.Equal(TicketFailure.WrongIssuer, validator.Validate(Craft(d => d.Issuer = "someone-else"), Expected).Failure);
    }

    [Fact]
    public void TamperedOrForeignSignedTicket_IsRejected()
    {
        string good = issuer.Issue("acct-0001", "cv_1", Match, "racer").Ticket;
        string[] p = good.Split('.');
        string payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(p[1])).Replace("\"racer\"", "\"spectator\"");
        string tampered = $"{p[0]}.{Base64UrlEncoder.Encode(payload)}.{p[2]}";
        Assert.Equal(TicketFailure.BadSignature, validator.Validate(tampered, Expected).Failure);

        using EcSigningKey attacker = EcSigningKey.Generate();
        var forged = new SigningCredentials(new ECDsaSecurityKey(attacker.SecurityKey.ECDsa) { KeyId = key.KeyId }, SecurityAlgorithms.EcdsaSha256);
        Assert.Equal(TicketFailure.BadSignature, validator.Validate(Craft(credentials: forged), Expected).Failure);
        Assert.Equal(TicketFailure.UnknownKey, validator.Validate(Craft(credentials: attacker.Credentials), Expected).Failure);
    }

    [Fact]
    public void UnsignedAndSymmetricTickets_AreRejected()
    {
        string[] p = Craft().Split('.');
        string none = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + p[1] + ".";
        Assert.Equal(TicketFailure.Malformed, validator.Validate(none, Expected).Failure);
        string noneWithSig = Base64UrlEncoder.Encode($"{{\"alg\":\"none\",\"kid\":\"{key.KeyId}\"}}") + "." + p[1] + "." + p[2];
        Assert.Equal(TicketFailure.UnsupportedAlgorithm, validator.Validate(noneWithSig, Expected).Failure);

        var hmac = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(issuer.JwksJson().PadRight(64))) { KeyId = key.KeyId },
            SecurityAlgorithms.HmacSha256);
        Assert.Equal(TicketFailure.UnsupportedAlgorithm, validator.Validate(Craft(credentials: hmac), Expected).Failure);
    }

    [Fact]
    public void TicketForAnotherMatchBuildProtocolOrContent_IsRejected()
    {
        Assert.Equal(TicketFailure.WrongMatch, validator.Validate(Craft(d => d.Claims["match"] = "m_2"), Expected).Failure);
        Assert.Equal(TicketFailure.WrongBuild, validator.Validate(Craft(d => d.Claims["build"] = "build-6"), Expected).Failure);
        Assert.Equal(TicketFailure.WrongProtocol, validator.Validate(Craft(d => d.Claims["protocol"] = 2), Expected).Failure);
        Assert.Equal(TicketFailure.WrongContent, validator.Validate(Craft(d => d.Claims["content"] = "other"), Expected).Failure);
        Assert.Equal(TicketFailure.BadRole, validator.Validate(Craft(d => d.Claims["role"] = "admin"), Expected).Failure);
        Assert.Equal(TicketFailure.MissingClaim, validator.Validate(Craft(d => d.Claims.Remove("jti")), Expected).Failure);
    }

    [Fact]
    public void PublishedJwks_ContainsOnlyThePublicKey()
    {
        string jwks = issuer.JwksJson();
        Assert.Contains(key.KeyId, jwks);
        Assert.DoesNotContain("\"d\"", jwks);
        Assert.Equal(1, TicketKeySet.FromJwks(jwks).Count);
    }
}
