using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace NightSignal.ControlPlane.Security;

/// <summary>
/// An ES256 (ECDSA P-256 / SHA-256) signing key with an RFC 7638 thumbprint as its key ID.
/// Used for DevAuth access tokens and for match tickets. Only the public half is ever published.
/// </summary>
public sealed class EcSigningKey : IDisposable
{
    readonly ECDsa key;

    EcSigningKey(ECDsa key)
    {
        this.key = key;
        ECParameters p = key.ExportParameters(false);
        if (p.Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidOperationException("Signing keys must be EC P-256 (ES256).");
        X = Base64UrlEncoder.Encode(p.Q.X!);
        Y = Base64UrlEncoder.Encode(p.Q.Y!);
        // RFC 7638: members in lexicographic order, no whitespace.
        string canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{X}\",\"y\":\"{Y}\"}}";
        KeyId = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        // A private provider factory: the shared default one caches signature providers by key ID process-wide,
        // which would keep using this ECDsa instance after it is disposed (e.g. host restart in one process).
        SecurityKey = new ECDsaSecurityKey(key) { KeyId = KeyId, CryptoProviderFactory = new CryptoProviderFactory() };
        Credentials = new SigningCredentials(SecurityKey, SecurityAlgorithms.EcdsaSha256);
    }

    public string KeyId { get; }
    public string X { get; }
    public string Y { get; }
    public ECDsaSecurityKey SecurityKey { get; }
    public SigningCredentials Credentials { get; }

    public static EcSigningKey Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static EcSigningKey FromPem(string pem)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        return new EcSigningKey(ecdsa);
    }

    /// <summary>Loads a PEM key, or (when allowed) generates one and writes it with owner-only permissions.</summary>
    public static EcSigningKey LoadOrCreate(string path, bool allowCreate)
    {
        if (File.Exists(path))
            return FromPem(File.ReadAllText(path));
        if (!allowCreate)
            throw new InvalidOperationException($"Signing key file not found: {Path.GetFileName(path)}");
        EcSigningKey created = Generate();
        SecretFiles.WriteNew(path, created.key.ExportECPrivateKeyPem());
        return created;
    }

    /// <summary>Public JWK (never contains the private scalar "d").</summary>
    public Dictionary<string, string> PublicJwk() => new()
    {
        ["kty"] = "EC",
        ["crv"] = "P-256",
        ["x"] = X,
        ["y"] = Y,
        ["kid"] = KeyId,
        ["alg"] = "ES256",
        ["use"] = "sig",
    };

    public string JwksJson() => JsonSerializer.Serialize(new { keys = new[] { PublicJwk() } });

    public void Dispose() => key.Dispose();
}

/// <summary>Helpers for small development secret files (keys) kept in a git-ignored folder.</summary>
public static class SecretFiles
{
    public static void WriteNew(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(contents);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Reads a random key file, generating 32 random bytes (base64url) when it does not exist.</summary>
    public static string ReadOrCreateRandomKey(string path)
    {
        if (!File.Exists(path))
            WriteNew(path, Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)));
        return File.ReadAllText(path).Trim();
    }
}
