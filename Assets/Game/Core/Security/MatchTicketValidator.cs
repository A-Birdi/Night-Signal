using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Security
{
    public enum TicketFailure
    {
        None = 0, Malformed, UnsupportedAlgorithm, UnknownKey, BadSignature, WrongIssuer, WrongAudience,
        Expired, NotYetValid, LifetimeTooLong, MissingClaim, WrongMatch, WrongBuild, WrongProtocol, WrongContent,
        UnknownRole, Replayed,
    }

    public sealed class TicketKey
    {
        public string Kid;
        public byte[] X;
        public byte[] Y;
    }

    public sealed class MatchTicketClaims
    {
        public string Subject, Convoy, Match, Role, Build, Content, Jti;
        public int Protocol;
        public long IssuedAt, NotBefore, Expires;
    }

    /// <summary>
    /// Validates control-plane match tickets (docs/NETWORKING.md §4): compact JWS, ES256 only, key chosen solely
    /// by kid from the configured JWKS, issuer/audience/time/lifetime checks, exact match/build/protocol/content,
    /// then single-use jti. Returns a failure code; callers log the code, never the ticket.
    /// </summary>
    public sealed class MatchTicketValidator
    {
        public const string Audience = "night-signal-gameserver";
        const long SkewSeconds = 5;
        const long MaxLifetimeSeconds = 60;

        readonly string issuer;
        readonly Dictionary<string, TicketKey> keys = new Dictionary<string, TicketKey>(StringComparer.Ordinal);
        readonly string match, build, content;
        readonly int protocol;
        readonly Func<long> nowUnix;
        readonly Dictionary<string, long> burned = new Dictionary<string, long>(StringComparer.Ordinal);

        public MatchTicketValidator(string issuer, IEnumerable<TicketKey> jwks, string match, string build, int protocol,
            string content, Func<long> nowUnixSeconds)
        {
            this.issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
            foreach (TicketKey k in jwks) keys[k.Kid] = k;
            this.match = match;
            this.build = build;
            this.protocol = protocol;
            this.content = content;
            nowUnix = nowUnixSeconds ?? throw new ArgumentNullException(nameof(nowUnixSeconds));
        }

        /// <summary>Parses a JWKS document ({"keys":[{"kty":"EC","crv":"P-256","x","y","kid"}]}).</summary>
        public static List<TicketKey> ParseJwks(string json)
        {
            var list = new List<TicketKey>();
            foreach (JToken k in JObject.Parse(json)["keys"] ?? new JArray())
            {
                if ((string)k["kty"] != "EC" || (string)k["crv"] != "P-256") continue;
                list.Add(new TicketKey { Kid = (string)k["kid"], X = Base64Url.Decode((string)k["x"]), Y = Base64Url.Decode((string)k["y"]) });
            }
            return list;
        }

        public TicketFailure Validate(string token, out MatchTicketClaims claims)
        {
            claims = null;
            if (string.IsNullOrEmpty(token) || token.Length > 4096) return TicketFailure.Malformed;
            string[] parts = token.Split('.');
            if (parts.Length != 3) return TicketFailure.Malformed;

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
                return TicketFailure.Malformed;
            }

            if ((string)header["alg"] != "ES256" || header["crit"] != null) return TicketFailure.UnsupportedAlgorithm;
            string kid = (string)header["kid"];
            if (kid == null || !keys.TryGetValue(kid, out TicketKey key)) return TicketFailure.UnknownKey;
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            if (!P256.Verify(key.X, key.Y, digest, signature)) return TicketFailure.BadSignature;

            var c = new MatchTicketClaims
            {
                Subject = (string)payload["sub"], Convoy = (string)payload["convoy"], Match = (string)payload["match"],
                Role = (string)payload["role"], Build = (string)payload["build"], Content = (string)payload["content"],
                Jti = (string)payload["jti"],
            };
            if ((string)payload["iss"] != issuer) return TicketFailure.WrongIssuer;
            if ((string)payload["aud"] != Audience) return TicketFailure.WrongAudience;
            if (c.Subject == null || c.Convoy == null || c.Match == null || c.Role == null || c.Build == null ||
                c.Content == null || c.Jti == null || payload["protocol"] == null || payload["iat"] == null ||
                payload["nbf"] == null || payload["exp"] == null)
                return TicketFailure.MissingClaim;
            c.Protocol = (int)payload["protocol"];
            c.IssuedAt = (long)payload["iat"];
            c.NotBefore = (long)payload["nbf"];
            c.Expires = (long)payload["exp"];

            long now = nowUnix();
            if (now > c.Expires + SkewSeconds) return TicketFailure.Expired;
            if (now + SkewSeconds < c.NotBefore) return TicketFailure.NotYetValid;
            if (c.Expires - c.IssuedAt > MaxLifetimeSeconds) return TicketFailure.LifetimeTooLong;
            if (c.Match != match) return TicketFailure.WrongMatch;
            if (c.Build != build) return TicketFailure.WrongBuild;
            if (c.Protocol != protocol) return TicketFailure.WrongProtocol;
            if (c.Content != content) return TicketFailure.WrongContent;
            if (c.Role != "racer" && c.Role != "spectator") return TicketFailure.UnknownRole;

            Purge(now);
            if (burned.ContainsKey(c.Jti)) return TicketFailure.Replayed;
            burned[c.Jti] = c.Expires + SkewSeconds;
            claims = c;
            return TicketFailure.None;
        }

        void Purge(long now)
        {
            if (burned.Count < 64) return;
            var expired = new List<string>();
            foreach (KeyValuePair<string, long> kv in burned) if (kv.Value < now) expired.Add(kv.Key);
            foreach (string k in expired) burned.Remove(k);
        }
    }

    public static class Base64Url
    {
        public static byte[] Decode(string s)
        {
            if (s == null) throw new FormatException("null base64url");
            string b = s.Replace('-', '+').Replace('_', '/');
            switch (b.Length % 4)
            {
                case 2: b += "=="; break;
                case 3: b += "="; break;
                case 1: throw new FormatException("invalid base64url length");
            }
            return Convert.FromBase64String(b);
        }

        public static string Encode(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
