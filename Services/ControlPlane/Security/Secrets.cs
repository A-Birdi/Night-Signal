using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NightSignal.ControlPlane.Security;

/// <summary>PBKDF2-SHA256 password hashes in the form <c>pbkdf2-sha256$iterations$saltB64$hashB64</c>.</summary>
public static class Pbkdf2Password
{
    const string Scheme = "pbkdf2-sha256";
    const int MinIterations = 10_000;
    const int HashBytes = 32;

    public static string Hash(string password, int iterations = 210_000)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        string[] parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations) ||
            iterations < MinIterations)
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool IsWellFormed(string encoded) =>
        encoded.Split('$') is { Length: 4 } p && p[0] == Scheme && int.TryParse(p[1], out int n) && n >= MinIterations;
}

public static class Hashing
{
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string HmacSha256Hex(byte[] key, byte[] data) => Convert.ToHexStringLower(HMACSHA256.HashData(key, data));

    /// <summary>Constant-time comparison of two ASCII strings (e.g. hex digests).</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>Opaque random identifier, URL-safe.</summary>
    public static string RandomId(string prefix, int bytes = 16) =>
        prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}

/// <summary>Removes credentials from text before it reaches a log.</summary>
public static partial class Redaction
{
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?i)(""?(?:password|refresh_token|access_token|accessToken|token|secret|resultsSecret|key)""?\s*[:=]\s*)(""[^""]*""|[^\s,}&]+)")]
    private static partial Regex SecretFieldPattern();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string s = JwtPattern().Replace(text, "[redacted-jwt]");
        return SecretFieldPattern().Replace(s, "$1[redacted]");
    }
}
