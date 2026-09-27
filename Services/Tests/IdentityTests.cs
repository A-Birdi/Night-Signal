using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.DevAuth;
using NightSignal.ControlPlane.Security;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// The JwtBearer validation path (the same one production uses with Supabase's JWKS) against DevAuth tokens and
/// hostile variants: unsigned, wrong issuer, wrong audience, expired, tampered, foreign key, HS256 key confusion.
/// </summary>
public sealed class IdentityTests : IDisposable
{
    const string Issuer = "urn:night-signal:devauth"; // appsettings.Development.json
    readonly TempDir dir = new();
    readonly ControlPlaneHost host;
    readonly IReadOnlyList<DevAccountFixture> accounts;

    public IdentityTests()
    {
        accounts = TestData.WriteSeed(dir.File("seed.json"), 2);
        host = new ControlPlaneHost(dir.Path, dir.File("seed.json"));
    }

    public void Dispose()
    {
        host.Dispose();
        dir.Dispose();
    }

    EcSigningKey DevKey => host.Services.GetRequiredService<DevAuthKeys>().Key;

    string Token(Action<SecurityTokenDescriptor>? mutate = null, SigningCredentials? credentials = null)
    {
        DateTime now = DateTime.UtcNow;
        var d = new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = "authenticated", IssuedAt = now, NotBefore = now, Expires = now.AddMinutes(15),
            SigningCredentials = credentials ?? DevKey.Credentials,
            Claims = new Dictionary<string, object> { ["sub"] = accounts[0].AccountId, ["role"] = "authenticated" },
        };
        mutate?.Invoke(d);
        return new JsonWebTokenHandler().CreateToken(d);
    }

    async Task<HttpStatusCode> MeStatus(string? token)
    {
        HttpClient c = token is null ? host.CreateClient() : host.Authed(token);
        return (await c.GetAsync("/v1/me")).StatusCode;
    }

    [Fact]
    public async Task DevAuthTokens_AreAccepted_AndIdentifyTheSubject()
    {
        string token = await host.SignInAsync(accounts[0]);
        HttpResponseMessage r = await host.Authed(token).GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(accounts[0].AccountId, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accountId").GetString());
        Assert.Equal(HttpStatusCode.OK, await MeStatus(Token())); // a correctly crafted token with the dev key is equivalent
    }

    [Fact]
    public async Task DevAuthJwks_IsPublishedWithoutThePrivateKey()
    {
        string jwks = await host.CreateClient().GetStringAsync("/dev/auth/.well-known/jwks.json");
        Assert.Contains(DevKey.KeyId, jwks);
        Assert.DoesNotContain("\"d\"", jwks);
    }

    [Fact]
    public async Task MissingToken_IsRejected() => Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus(null));

    [Fact]
    public async Task UnsignedAlgNoneToken_IsRejected()
    {
        string[] parts = Token().Split('.');
        string header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus($"{header}.{parts[1]}."));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus($"{header}.{parts[1]}.{parts[2]}"));
    }

    [Fact]
    public async Task WrongIssuer_IsRejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus(Token(d => d.Issuer = "https://evil.example/auth/v1")));

    [Fact]
    public async Task WrongAudience_IsRejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus(Token(d => d.Audience = "night-signal-gameserver")));

    [Fact]
    public async Task ExpiredToken_IsRejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus(Token(d =>
        {
            d.IssuedAt = DateTime.UtcNow.AddMinutes(-30);
            d.NotBefore = DateTime.UtcNow.AddMinutes(-30);
            d.Expires = DateTime.UtcNow.AddMinutes(-5); // beyond the 30 s clock skew
        })));

    [Fact]
    public async Task TamperedToken_IsRejected()
    {
        string[] p = (await host.SignInAsync(accounts[0])).Split('.');
        string payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(p[1])).Replace(accounts[0].AccountId, accounts[1].AccountId);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus($"{p[0]}.{Base64UrlEncoder.Encode(payload)}.{p[2]}"));
    }

    [Fact]
    public async Task TokenSignedByAnotherKey_IsRejected_EvenWithTheSameKid()
    {
        using EcSigningKey attacker = EcSigningKey.Generate();
        var sameKid = new SigningCredentials(new ECDsaSecurityKey(attacker.SecurityKey.ECDsa) { KeyId = DevKey.KeyId }, SecurityAlgorithms.EcdsaSha256);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus(Token(credentials: sameKid)));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus(Token(credentials: attacker.Credentials)));
    }

    [Fact]
    public async Task SymmetricHs256Token_IsRejected()
    {
        byte[] secret = Encoding.UTF8.GetBytes(DevKey.JwksJson());
        var hmac = new SigningCredentials(new SymmetricSecurityKey(secret) { KeyId = DevKey.KeyId }, SecurityAlgorithms.HmacSha256);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus(Token(credentials: hmac)));
    }

    [Fact]
    public async Task TokenWithoutUsableSubject_IsRejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus(Token(d => d.Claims.Remove("sub"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatus(Token(d => d.Claims["sub"] = "x")));
    }

    [Fact]
    public async Task WrongPassword_IsRefused_AndRefreshTokensAreSingleUse()
    {
        HttpClient c = host.CreateClient();
        HttpResponseMessage bad = await c.PostAsJsonAsync("/dev/auth/token", new { email = accounts[0].Email, password = "not-the-password" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        HttpResponseMessage unknown = await c.PostAsJsonAsync("/dev/auth/token", new { email = "nobody@devauth.localhost", password = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        JsonElement tokens = await (await c.PostAsJsonAsync("/dev/auth/token", new { email = accounts[0].Email, password = accounts[0].Password }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(900, tokens.GetProperty("expires_in").GetInt32());
        string refresh = tokens.GetProperty("refresh_token").GetString()!;
        HttpResponseMessage first = await c.PostAsJsonAsync("/dev/auth/refresh", new { refresh_token = refresh });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        string newAccess = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, await MeStatus(newAccess));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/dev/auth/refresh", new { refresh_token = refresh })).StatusCode);
    }
}
