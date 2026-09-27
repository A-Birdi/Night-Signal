using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Identity;
using NightSignal.ControlPlane.Security;

namespace NightSignal.ControlPlane.DevAuth;

/// <summary>DevAuth's ES256 signing key, generated on first use into the git-ignored dev key folder.</summary>
public sealed class DevAuthKeys : IDocumentRetriever, IDisposable
{
    readonly Lazy<EcSigningKey> key;

    public DevAuthKeys(IOptions<KeyOptions> keys, IHostEnvironment env)
    {
        string path = PathResolver.Resolve(PathResolver.Resolve(env.ContentRootPath, keys.Value.DevKeyDirectory), "devauth-signing.pem");
        key = new Lazy<EcSigningKey>(() => EcSigningKey.LoadOrCreate(path, allowCreate: true));
    }

    public EcSigningKey Key => key.Value;

    /// <summary>The JWKS JwtBearer consumes is the same document served at /dev/auth/.well-known/jwks.json.</summary>
    public Task<string> GetDocumentAsync(string address, CancellationToken cancel) => Task.FromResult(Key.JwksJson());

    public void Dispose()
    {
        if (key.IsValueCreated) key.Value.Dispose();
    }
}

public sealed class DevAccount
{
    public string AccountId { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

/// <summary>Seeded development accounts (never real users). Extra JSON fields are ignored.</summary>
public sealed class DevAccountSeed
{
    public List<DevAccount> Accounts { get; set; } = new();

    public static DevAccountSeed Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"DevAuth seed file not found: {Path.GetFileName(path)}");
        DevAccountSeed seed = JsonSerializer.Deserialize<DevAccountSeed>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new DevAccountSeed();
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DevAccount a in seed.Accounts)
        {
            if (!IdentitySetup.IsValidAccountId(a.AccountId) || string.IsNullOrWhiteSpace(a.Email) ||
                !Pbkdf2Password.IsWellFormed(a.PasswordHash) || !emails.Add(a.Email))
                throw new InvalidOperationException("DevAuth seed contains an invalid or duplicate account entry.");
        }
        return seed;
    }
}

/// <summary>Supabase-shaped token response so the game client can use one sign-in code path.</summary>
public sealed class DevTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "bearer";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; init; }
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = "";
    [JsonPropertyName("user")] public DevUser User { get; init; } = new();
}

public sealed class DevUser
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("email")] public string Email { get; init; } = "";
}

/// <summary>
/// Development-only issuer mimicking Supabase Auth: ES256 access tokens (15 min, aud "authenticated") and
/// rotating single-use refresh tokens held in memory (a restart signs everyone out, which is fine locally).
/// </summary>
public sealed class DevAuthService
{
    readonly DevAuthKeys keys;
    readonly DevAuthOptions options;
    readonly IdentityOptions identity;
    readonly TimeProvider clock;
    readonly Lazy<DevAccountSeed> seed;
    readonly ConcurrentDictionary<string, RefreshRecord> refreshTokens = new();
    readonly string dummyHash = Pbkdf2Password.Hash("unused", 10_000);

    sealed record RefreshRecord(DevAccount Account, string SessionId, DateTimeOffset ExpiresAt);

    public DevAuthService(DevAuthKeys keys, IOptions<DevAuthOptions> options, IOptions<IdentityOptions> identity,
        TimeProvider clock, IHostEnvironment env)
    {
        this.keys = keys;
        this.options = options.Value;
        this.identity = identity.Value;
        this.clock = clock;
        string seedPath = PathResolver.Resolve(env.ContentRootPath, this.options.SeedFile);
        seed = new Lazy<DevAccountSeed>(() => DevAccountSeed.Load(seedPath));
    }

    public void EnsureSeedLoaded() => _ = seed.Value;

    public DevTokenResponse? SignIn(string? email, string? password)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password) || password.Length > 256)
            return null;
        DevAccount? account = seed.Value.Accounts.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
        // Always spend the PBKDF2 cost so unknown emails are not distinguishable by timing.
        bool ok = Pbkdf2Password.Verify(password, account?.PasswordHash ?? dummyHash) && account is not null;
        return ok ? Issue(account!, Hashing.RandomId("sess_")) : null;
    }

    public DevTokenResponse? Refresh(string? refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken)) return null;
        string key = Hashing.Sha256Hex(refreshToken);
        if (!refreshTokens.TryRemove(key, out RefreshRecord? record) || record.ExpiresAt <= clock.GetUtcNow())
            return null; // single use: a replayed refresh token fails
        return Issue(record.Account, record.SessionId);
    }

    DevTokenResponse Issue(DevAccount account, string sessionId)
    {
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset expires = now.AddMinutes(options.AccessTokenMinutes);
        string access = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = identity.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = keys.Key.Credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = account.AccountId,
                ["email"] = account.Email,
                ["role"] = "authenticated",
                ["session_id"] = sessionId,
                ["jti"] = Hashing.RandomId("", 12),
            },
        });
        string refresh = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        refreshTokens[Hashing.Sha256Hex(refresh)] = new RefreshRecord(account, sessionId, now.AddHours(options.RefreshTokenHours));
        return new DevTokenResponse
        {
            AccessToken = access,
            ExpiresIn = (int)(expires - now).TotalSeconds,
            ExpiresAt = expires.ToUnixTimeSeconds(),
            RefreshToken = refresh,
            User = new DevUser { Id = account.AccountId, Email = account.Email },
        };
    }
}
