using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NightSignal.TicketValidation
{
    /// <summary>Why a ticket was refused. Game servers should log the code, never the ticket itself.</summary>
    public enum TicketFailure
    {
        None = 0,
        Malformed,
        UnsupportedAlgorithm,
        UnknownKey,
        BadSignature,
        WrongIssuer,
        WrongAudience,
        Expired,
        NotYetValid,
        LifetimeTooLong,
        MissingClaim,
        WrongMatch,
        WrongBuild,
        WrongProtocol,
        WrongContent,
        BadRole,
        Replayed,
    }

    /// <summary>Verified ticket claims (see docs/NETWORKING.md "Match tickets").</summary>
    public sealed class TicketClaims
    {
        public string Subject { get; internal set; }
        public string Convoy { get; internal set; }
        public string Match { get; internal set; }
        /// <summary>"racer" or "spectator".</summary>
        public string Role { get; internal set; }
        public string Build { get; internal set; }
        public int Protocol { get; internal set; }
        public string Content { get; internal set; }
        public string Jti { get; internal set; }
        public DateTimeOffset IssuedAt { get; internal set; }
        public DateTimeOffset ExpiresAt { get; internal set; }
    }

    public sealed class TicketValidationResult
    {
        TicketValidationResult(TicketFailure failure, TicketClaims claims)
        {
            Failure = failure;
            Claims = claims;
        }

        public bool IsValid => Failure == TicketFailure.None;
        public TicketFailure Failure { get; }
        public TicketClaims Claims { get; }

        internal static TicketValidationResult Fail(TicketFailure f) => new TicketValidationResult(f, null);
        internal static TicketValidationResult Ok(TicketClaims c) => new TicketValidationResult(TicketFailure.None, c);
    }

    /// <summary>What THIS game server expects: its own match assignment and its own build/protocol/content.</summary>
    public sealed class ExpectedTicketContext
    {
        public string MatchId;
        public string Build;
        public int Protocol;
        public string ContentHash;
    }

    public sealed class TicketValidationParameters
    {
        public const string DefaultAudience = "night-signal-gameserver";

        /// <summary>The control plane's ticket issuer (returned by /v1/servers/register as ticketIssuer).</summary>
        public string Issuer = "night-signal-control-plane";
        public string Audience = DefaultAudience;
        public TimeSpan ClockSkew = TimeSpan.FromSeconds(5);
        public TimeSpan MaxLifetime = TimeSpan.FromSeconds(60);
    }

    /// <summary>Remembers consumed ticket IDs until they expire, so each ticket admits exactly one connection.</summary>
    public interface ITicketReplayCache
    {
        /// <summary>Returns false if <paramref name="jti"/> was already consumed.</summary>
        bool TryConsume(string jti, DateTimeOffset expiresAt, DateTimeOffset now);
    }

    public sealed class InMemoryTicketReplayCache : ITicketReplayCache
    {
        readonly object gate = new object();
        readonly Dictionary<string, DateTimeOffset> consumed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        public bool TryConsume(string jti, DateTimeOffset expiresAt, DateTimeOffset now)
        {
            lock (gate)
            {
                if (consumed.Count > 1024)
                {
                    var expired = new List<string>();
                    foreach (KeyValuePair<string, DateTimeOffset> kv in consumed)
                        if (kv.Value < now) expired.Add(kv.Key);
                    foreach (string key in expired) consumed.Remove(key);
                }
                if (consumed.ContainsKey(jti)) return false;
                consumed[jti] = expiresAt;
                return true;
            }
        }
    }

    /// <summary>ES256 public keys from the control plane's ticket JWKS (/v1/servers/ticket-jwks.json).</summary>
    public sealed class TicketKeySet
    {
        readonly Dictionary<string, ECParameters> keys = new Dictionary<string, ECParameters>(StringComparer.Ordinal);

        public static TicketKeySet FromJwks(string jwksJson)
        {
            var set = new TicketKeySet();
            JObject root = JObject.Parse(jwksJson);
            if (!(root["keys"] is JArray array)) throw new FormatException("JWKS has no keys array");
            foreach (JToken k in array)
            {
                if ((string)k["kty"] != "EC" || (string)k["crv"] != "P-256") continue;
                string use = (string)k["use"];
                if (use != null && use != "sig") continue;
                string kid = (string)k["kid"];
                if (string.IsNullOrEmpty(kid)) continue;
                byte[] x = Base64Url.Decode((string)k["x"]);
                byte[] y = Base64Url.Decode((string)k["y"]);
                if (x.Length != 32 || y.Length != 32) continue;
                set.keys[kid] = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } };
            }
            return set;
        }

        public int Count => keys.Count;
        internal bool TryGet(string kid, out ECParameters key) => keys.TryGetValue(kid ?? "", out key);
    }

    /// <summary>
    /// Validates control-plane match tickets on the authoritative game server: compact JWS, alg ES256 only, key by kid
    /// from the configured JWKS (header keys/URLs are ignored), signature, iss, aud, exp/nbf with small skew, lifetime
    /// at most 60 s, required claims, this server's match/build/protocol/content, role, and single use of jti.
    /// Thread-safe. Consumes the jti only after every other check passed.
    /// </summary>
    public sealed class TicketValidator
    {
        readonly TicketKeySet keys;
        readonly TicketValidationParameters parameters;
        readonly ITicketReplayCache replay;
        readonly Func<DateTimeOffset> clock;

        public TicketValidator(TicketKeySet keys, TicketValidationParameters parameters, ITicketReplayCache replay,
            Func<DateTimeOffset> clock = null)
        {
            this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
            this.parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            this.replay = replay ?? throw new ArgumentNullException(nameof(replay));
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public TicketValidationResult Validate(string ticket, ExpectedTicketContext expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (string.IsNullOrEmpty(ticket) || ticket.Length > 8192) return TicketValidationResult.Fail(TicketFailure.Malformed);
            string[] parts = ticket.Split('.');
            if (parts.Length != 3 || parts[2].Length == 0) return TicketValidationResult.Fail(TicketFailure.Malformed);

            JObject header, payload;
            byte[] signature;
            try
            {
                header = JObject.Parse(Encoding.UTF8.GetString(Base64Url.Decode(parts[0])));
                payload = JObject.Parse(Encoding.UTF8.GetString(Base64Url.Decode(parts[1])));
                signature = Base64Url.Decode(parts[2]);
            }
            catch (Exception)
            {
                return TicketValidationResult.Fail(TicketFailure.Malformed);
            }

            // Only ES256; "none", HS256 (key confusion) and anything else are refused before any key lookup.
            if ((string)header["alg"] != "ES256" || header["crit"] != null) return TicketValidationResult.Fail(TicketFailure.UnsupportedAlgorithm);
            if (!keys.TryGet((string)header["kid"], out ECParameters key)) return TicketValidationResult.Fail(TicketFailure.UnknownKey);
            if (signature.Length != 64) return TicketValidationResult.Fail(TicketFailure.BadSignature);
            using (ECDsa ecdsa = ECDsa.Create(key))
            {
                byte[] signedBytes = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
                if (!ecdsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256)) // IEEE P1363 r||s, as JWS uses
                    return TicketValidationResult.Fail(TicketFailure.BadSignature);
            }

            if ((string)payload["iss"] != parameters.Issuer) return TicketValidationResult.Fail(TicketFailure.WrongIssuer);
            if (!AudienceMatches(payload["aud"])) return TicketValidationResult.Fail(TicketFailure.WrongAudience);

            long? exp = Long(payload["exp"]), iat = Long(payload["iat"]), nbf = Long(payload["nbf"]);
            if (exp == null || iat == null) return TicketValidationResult.Fail(TicketFailure.MissingClaim);
            DateTimeOffset now = clock();
            DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp.Value);
            DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat.Value);
            if (now > expiresAt + parameters.ClockSkew) return TicketValidationResult.Fail(TicketFailure.Expired);
            if (now + parameters.ClockSkew < DateTimeOffset.FromUnixTimeSeconds(nbf ?? iat.Value)) return TicketValidationResult.Fail(TicketFailure.NotYetValid);
            if (expiresAt - issuedAt > parameters.MaxLifetime) return TicketValidationResult.Fail(TicketFailure.LifetimeTooLong);

            var claims = new TicketClaims
            {
                Subject = (string)payload["sub"],
                Convoy = (string)payload["convoy"],
                Match = (string)payload["match"],
                Role = (string)payload["role"],
                Build = (string)payload["build"],
                Content = (string)payload["content"],
                Jti = (string)payload["jti"],
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
            };
            long? protocol = Long(payload["protocol"]);
            if (string.IsNullOrEmpty(claims.Subject) || string.IsNullOrEmpty(claims.Convoy) || string.IsNullOrEmpty(claims.Match) ||
                string.IsNullOrEmpty(claims.Jti) || string.IsNullOrEmpty(claims.Build) || string.IsNullOrEmpty(claims.Content) || protocol == null)
                return TicketValidationResult.Fail(TicketFailure.MissingClaim);
            claims.Protocol = (int)protocol.Value;

            if (claims.Match != expected.MatchId) return TicketValidationResult.Fail(TicketFailure.WrongMatch);
            if (claims.Build != expected.Build) return TicketValidationResult.Fail(TicketFailure.WrongBuild);
            if (claims.Protocol != expected.Protocol) return TicketValidationResult.Fail(TicketFailure.WrongProtocol);
            if (claims.Content != expected.ContentHash) return TicketValidationResult.Fail(TicketFailure.WrongContent);
            if (claims.Role != "racer" && claims.Role != "spectator") return TicketValidationResult.Fail(TicketFailure.BadRole);

            if (!replay.TryConsume(claims.Jti, expiresAt + parameters.ClockSkew, now)) return TicketValidationResult.Fail(TicketFailure.Replayed);
            return TicketValidationResult.Ok(claims);
        }

        bool AudienceMatches(JToken aud)
        {
            if (aud == null) return false;
            if (aud.Type == JTokenType.String) return (string)aud == parameters.Audience;
            if (aud is JArray array)
                foreach (JToken a in array)
                    if (a.Type == JTokenType.String && (string)a == parameters.Audience) return true;
            return false;
        }

        static long? Long(JToken t) => t != null && t.Type == JTokenType.Integer ? (long?)t : null;
    }

    internal static class Base64Url
    {
        public static byte[] Decode(string s)
        {
            if (s == null) throw new FormatException("null base64url");
            string b = s.Replace('-', '+').Replace('_', '/');
            switch (b.Length % 4)
            {
                case 2: b += "=="; break;
                case 3: b += "="; break;
                case 1: throw new FormatException("bad base64url length");
            }
            return Convert.FromBase64String(b);
        }
    }
}
