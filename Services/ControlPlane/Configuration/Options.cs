namespace NightSignal.ControlPlane.Configuration;

/// <summary>Production identity provider (Supabase Auth). Only the issuer and JWKS source differ from DevAuth.</summary>
public sealed class IdentityOptions
{
    public const string Section = "Identity";

    /// <summary>Expected <c>iss</c>, e.g. <c>https://&lt;project-ref&gt;.supabase.co/auth/v1</c>.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>Expected <c>aud</c>. Supabase Auth access tokens use "authenticated".</summary>
    public string Audience { get; set; } = "authenticated";

    /// <summary>HTTPS JWKS document, e.g. <c>{Issuer}/.well-known/jwks.json</c>. Ignored when DevAuth is enabled.</summary>
    public string JwksUrl { get; set; } = "";

    public int ClockSkewSeconds { get; set; } = 30;
}

/// <summary>Development-only local identity issuer. Refuses to start outside Development or on a non-loopback binding.</summary>
public sealed class DevAuthOptions
{
    public const string Section = "DevAuth";

    public bool Enabled { get; set; }

    /// <summary><c>iss</c> of DevAuth tokens. Deliberately not a real provider URL.</summary>
    public string Issuer { get; set; } = "urn:night-signal:devauth";

    /// <summary>Seed accounts file (PBKDF2 hashes), relative to the content root.</summary>
    public string SeedFile { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenHours { get; set; } = 24 * 7;
}

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>"Sqlite" (local development/tests) or "Postgres" (Supabase; not executed in this environment).</summary>
    public string Provider { get; set; } = "Sqlite";

    /// <summary>SQLite file, relative to the content root. Keep it under a git-ignored folder.</summary>
    public string SqlitePath { get; set; } = ".data/controlplane.db";

    /// <summary>Server-side connection string (service role). Never shipped to clients.</summary>
    public string? PostgresConnectionString { get; set; }

    public bool ApplyMigrations { get; set; } = true;
}

/// <summary>Where generated development keys live and where production key files are read from.</summary>
public sealed class KeyOptions
{
    public const string Section = "Keys";

    /// <summary>Development key folder (git-ignored), relative to the content root.</summary>
    public string DevKeyDirectory { get; set; } = ".devkeys";
}

public sealed class TicketOptions
{
    public const string Section = "Tickets";
    public const string Audience = "night-signal-gameserver";
    public const int MaxLifetimeSeconds = 60;

    public string Issuer { get; set; } = "night-signal-control-plane";
    public int LifetimeSeconds { get; set; } = MaxLifetimeSeconds;

    /// <summary>PEM EC P-256 private key. Required outside Development; generated into the dev key folder otherwise.</summary>
    public string? SigningKeyFile { get; set; }
}

public sealed class GameServerOptions
{
    public const string Section = "GameServers";

    /// <summary>Accepted server credentials. Only SHA-256 hashes of the keys are configured.</summary>
    public List<GameServerCredential> Credentials { get; set; } = new();

    /// <summary>Development only: generate a key into the dev key folder as server "dev-local".</summary>
    public bool GenerateDevCredential { get; set; }

    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public int StaleAfterSeconds { get; set; } = 20;
    public int AssignmentAckTimeoutSeconds { get; set; } = 10;

    /// <summary>A match with no accepted results after this long is aborted (no rewards) and its convoy released.</summary>
    public int MaxMatchMinutes { get; set; } = 45;
}

public sealed class GameServerCredential
{
    public string Id { get; set; } = "";

    /// <summary>Lowercase hex SHA-256 of the UTF-8 key.</summary>
    public string KeySha256 { get; set; } = "";
}

public sealed class CompatibilityOptions
{
    public const string Section = "Compatibility";

    /// <summary>Control-channel/game protocol version clients and servers must present.</summary>
    public int Protocol { get; set; } = 1;
}

public sealed class ContentOptions
{
    public const string Section = "Content";

    /// <summary>Folder with generated/ and authored/ catalogue JSON (copied at build), relative to the app base directory.</summary>
    public string Directory { get; set; } = "content";
}

internal static class PathResolver
{
    /// <summary>Resolves <paramref name="path"/> against <paramref name="root"/> unless it is already absolute.</summary>
    public static string Resolve(string root, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
}
