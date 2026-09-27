using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Toys
{
    /// <summary>The five approved 'While We Wait' diversions (Addendum 02 §0.1). Mystery Garage and card games are out of scope.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ToyActivityId
    {
        CapClash = 0,
        PitCrew = 1,
        Greenlight = 2,
        PocketCircuit = 3,
        Canvas = 4,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ToyVerdict
    {
        Accepted = 0,
        /// <summary>Not applied and never will be (stale, invalid, not permitted).</summary>
        Rejected = 1,
        /// <summary>Not applied because the session is paused for the convoy; resubmit (with the new epoch) after resuming.</summary>
        Deferred = 2,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ToyReason
    {
        None = 0,
        Malformed,
        TooLarge,
        RateLimited,
        WrongSession,
        SessionEnded,
        SessionPaused,
        NotMember,
        StaleMembership,
        SeatNotActive,
        OutOfOrder,
        StaleEpoch,
        UnknownKind,
        NotAllowed,
        NotOwner,
        NoLease,
        LeaseHeld,
        Conflict,
        NotFound,
        Busy,
        LimitReached,
        Orienting,
        InvalidState,
        OutOfRange,
        ConsentRequired,
        ProposalClosed,
        AlreadyDone,
    }

    /// <summary>
    /// Outcome of one <see cref="ToyCommand"/>. <see cref="Duplicate"/> is set when the request id was already accepted: the
    /// original result is returned and nothing is applied twice (idempotency, Addendum 02 §1.4 / B06).
    /// </summary>
    public struct ToyResult
    {
        public ToyVerdict Verdict;
        public ToyReason Reason;
        public string Detail;
        /// <summary>Session revision at which the command was accepted (0 when not accepted).</summary>
        public long Revision;
        /// <summary>Optional created/affected id (object id, attempt id, shot id, lap number ...).</summary>
        public string Value;
        public bool Duplicate;

        public bool Accepted => Verdict == ToyVerdict.Accepted;

        public static ToyResult Ok(string value = null) => new ToyResult { Verdict = ToyVerdict.Accepted, Value = value };
        public static ToyResult Reject(ToyReason reason, string detail = null) => new ToyResult { Verdict = ToyVerdict.Rejected, Reason = reason, Detail = detail };
        public static ToyResult Defer(ToyReason reason, string detail = null) => new ToyResult { Verdict = ToyVerdict.Deferred, Reason = reason, Detail = detail };

        public override string ToString() => Verdict + (Reason != ToyReason.None ? "(" + Reason + (Detail != null ? ": " + Detail : "") + ")" : "") +
                                             (Duplicate ? " [duplicate]" : "");
    }

    /// <summary>
    /// Structural limits of the downtime protocol (Addendum 02 §1.6: cap message sizes/rates, queued actions, active
    /// simulations and memory). Initial engineering budgets; change only with a recorded reason.
    /// </summary>
    public static class ToyLimits
    {
        public const int SchemaVersion = 1;
        public const int MaxMembers = 6;
        public const int MaxIdLength = 64;
        public const int MaxKindLength = 32;
        /// <summary>Largest accepted serialized command (bytes of UTF-8 JSON).</summary>
        public const int MaxCommandBytes = 8 * 1024;
        public const int MaxPayloadDepth = 6;

        /// <summary>Per-member token bucket across all toys.</summary>
        public const double RatePerSecond = 40;
        public const double RateBurst = 80;

        /// <summary>Accepted request ids remembered for idempotency (and persisted in snapshots).</summary>
        public const int IdempotencyWindow = 1024;
        public const int SnapshotIdempotencyIds = 256;
        /// <summary>Durable accepted commands kept after the last snapshot before the host must snapshot again.</summary>
        public const int MaxJournalEntries = 1024;
        /// <summary>Hard upper bound of a serialized session snapshot (Canvas has its own 1 MiB document budget).</summary>
        public const int MaxSnapshotBytes = 3 * 1024 * 1024;

        /// <summary>Real interaction keeps the control lease; nothing else (heartbeats, animations) renews it.</summary>
        public const long ControlLeaseMs = 45_000;
        /// <summary>Recent real interaction that makes someone an 'active user' of a toy for consent purposes.</summary>
        public const long ActiveUserWindowMs = 120_000;
        /// <summary>Local reorientation window after an explicit resume, during which no moving/timing input applies.</summary>
        public const long OrientationMs = 1_500;
        /// <summary>Consent proposals lapse (= declined; silence is never consent).</summary>
        public const long ProposalLifetimeMs = 60_000;
        /// <summary>Dormant room grace after the last active member was lost to disconnection (D208).</summary>
        public const long DormantGraceMs = 24L * 60 * 60 * 1000;
        /// <summary>Periodic durable snapshot interval while anything changed (Canvas 'snapshots at safe intervals').</summary>
        public const long SnapshotIntervalMs = 30_000;
        /// <summary>Safety bound on simulated catch-up per Advance call.</summary>
        public const long MaxCatchUpMs = 10 * 60 * 1000;
    }

    /// <summary>
    /// One toy input (Addendum 02 §11 'ToyCommand'): convoy session, activity + activity epoch, member identity + membership
    /// generation, per-member sequence, a unique request id and a payload that each toy validates strictly.
    /// </summary>
    public sealed class ToyCommand
    {
        [JsonProperty("session")] public string SessionId;
        [JsonProperty("activity")] public ToyActivityId Activity;
        /// <summary>The activity epoch the client last saw. Bumped by pause, layout/arrangement changes and resets.</summary>
        [JsonProperty("epoch")] public int Epoch;
        [JsonProperty("member")] public string MemberId;
        [JsonProperty("gen")] public long Generation;
        [JsonProperty("seq")] public long Sequence;
        [JsonProperty("req")] public string RequestId;
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("payload")] public JObject Payload;

        public ToyCommand Clone() => new ToyCommand
        {
            SessionId = SessionId, Activity = Activity, Epoch = Epoch, MemberId = MemberId, Generation = Generation,
            Sequence = Sequence, RequestId = RequestId, Kind = Kind, Payload = Payload == null ? null : (JObject)Payload.DeepClone(),
        };
    }

    /// <summary>Wire parsing with hard size/depth bounds before any payload is interpreted.</summary>
    public static class ToyCommandCodec
    {
        static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            MaxDepth = ToyLimits.MaxPayloadDepth + 2,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            FloatParseHandling = FloatParseHandling.Double,
            DateParseHandling = DateParseHandling.None,
        };

        public static string Serialize(ToyCommand cmd) => JsonConvert.SerializeObject(cmd, Formatting.None, Settings);

        public static bool TryParse(string json, out ToyCommand cmd, out ToyReason reason)
        {
            cmd = null;
            reason = ToyReason.None;
            if (json == null) { reason = ToyReason.Malformed; return false; }
            // UTF-8 byte count is at most 3 × UTF-16 length; check cheaply first, then exactly.
            if (json.Length > ToyLimits.MaxCommandBytes || System.Text.Encoding.UTF8.GetByteCount(json) > ToyLimits.MaxCommandBytes)
            {
                reason = ToyReason.TooLarge;
                return false;
            }
            try
            {
                cmd = JsonConvert.DeserializeObject<ToyCommand>(json, Settings);
            }
            catch (JsonException)
            {
                reason = ToyReason.Malformed;
                return false;
            }
            if (!Validate(cmd, out reason)) { cmd = null; return false; }
            return true;
        }

        /// <summary>Envelope checks shared by the wire path and in-process submissions.</summary>
        public static bool Validate(ToyCommand cmd, out ToyReason reason)
        {
            reason = ToyReason.Malformed;
            if (cmd == null) return false;
            if (!ValidId(cmd.SessionId) || !ValidId(cmd.MemberId) || !ValidId(cmd.RequestId)) return false;
            if (string.IsNullOrEmpty(cmd.Kind) || cmd.Kind.Length > ToyLimits.MaxKindLength) return false;
            if (!Enum.IsDefined(typeof(ToyActivityId), cmd.Activity)) return false;
            if (cmd.Sequence <= 0 || cmd.Generation <= 0 || cmd.Epoch < 0) return false;
            if (cmd.Payload != null && Depth(cmd.Payload) > ToyLimits.MaxPayloadDepth) return false;
            reason = ToyReason.None;
            return true;
        }

        public static bool ValidId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > ToyLimits.MaxIdLength) return false;
            foreach (char c in s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == ':';
                if (!ok) return false;
            }
            return true;
        }

        static int Depth(JToken t)
        {
            int best = 0;
            if (t is JContainer c)
                foreach (JToken child in c.Children())
                {
                    int d = Depth(child);
                    if (d > best) best = d;
                }
            return (t is JContainer ? 1 : 0) + best;
        }
    }

    /// <summary>Thrown by <see cref="PayloadReader"/> for a malformed/out-of-range field; the session converts it to a rejection.</summary>
    public sealed class ToyPayloadException : Exception
    {
        public readonly ToyReason Reason;
        public ToyPayloadException(ToyReason reason, string message) : base(message) { Reason = reason; }
    }

    /// <summary>Strict typed access to a command payload. Every number is range-checked and must be finite.</summary>
    public sealed class PayloadReader
    {
        readonly JObject obj;

        public PayloadReader(JObject payload) { obj = payload ?? new JObject(); }

        public bool Has(string name) => obj.TryGetValue(name, out JToken t) && t.Type != JTokenType.Null;

        JToken Need(string name)
        {
            if (!obj.TryGetValue(name, out JToken t) || t.Type == JTokenType.Null)
                throw new ToyPayloadException(ToyReason.Malformed, "missing '" + name + "'");
            return t;
        }

        public double Double(string name, double min, double max)
        {
            JToken t = Need(name);
            if (t.Type != JTokenType.Float && t.Type != JTokenType.Integer) throw new ToyPayloadException(ToyReason.Malformed, "'" + name + "' is not a number");
            double v = t.Value<double>();
            if (!ToyMath.Finite(v)) throw new ToyPayloadException(ToyReason.Malformed, "'" + name + "' is not finite");
            if (v < min || v > max) throw new ToyPayloadException(ToyReason.OutOfRange, "'" + name + "' out of range");
            return v;
        }

        public double Double(string name, double min, double max, double fallback) => Has(name) ? Double(name, min, max) : fallback;

        public long Long(string name, long min, long max)
        {
            JToken t = Need(name);
            if (t.Type != JTokenType.Integer) throw new ToyPayloadException(ToyReason.Malformed, "'" + name + "' is not an integer");
            long v;
            try { v = t.Value<long>(); }
            catch (Exception) { throw new ToyPayloadException(ToyReason.OutOfRange, "'" + name + "' out of range"); }
            if (v < min || v > max) throw new ToyPayloadException(ToyReason.OutOfRange, "'" + name + "' out of range");
            return v;
        }

        public int Int(string name, int min, int max) => (int)Long(name, min, max);
        public int Int(string name, int min, int max, int fallback) => Has(name) ? Int(name, min, max) : fallback;

        public bool Bool(string name)
        {
            JToken t = Need(name);
            if (t.Type != JTokenType.Boolean) throw new ToyPayloadException(ToyReason.Malformed, "'" + name + "' is not a boolean");
            return t.Value<bool>();
        }

        public bool Bool(string name, bool fallback) => Has(name) ? Bool(name) : fallback;

        public string Id(string name)
        {
            string s = String(name, ToyLimits.MaxIdLength);
            if (!ToyCommandCodec.ValidId(s)) throw new ToyPayloadException(ToyReason.Malformed, "'" + name + "' is not a valid id");
            return s;
        }

        public string OptionalId(string name) => Has(name) ? Id(name) : null;

        public string String(string name, int maxLength)
        {
            JToken t = Need(name);
            if (t.Type != JTokenType.String) throw new ToyPayloadException(ToyReason.Malformed, "'" + name + "' is not a string");
            string s = t.Value<string>();
            if (s.Length > maxLength) throw new ToyPayloadException(ToyReason.TooLarge, "'" + name + "' too long");
            return s;
        }

        /// <summary>Flat integer array (e.g. quantized points) with an element bound and value range.</summary>
        public int[] IntArray(string name, int maxCount, int min, int max)
        {
            JToken t = Need(name);
            if (t.Type != JTokenType.Array) throw new ToyPayloadException(ToyReason.Malformed, "'" + name + "' is not an array");
            var arr = (JArray)t;
            if (arr.Count > maxCount) throw new ToyPayloadException(ToyReason.TooLarge, "'" + name + "' has too many elements");
            var result = new int[arr.Count];
            for (int i = 0; i < arr.Count; i++)
            {
                JToken e = arr[i];
                if (e.Type != JTokenType.Integer) throw new ToyPayloadException(ToyReason.Malformed, "'" + name + "' element is not an integer");
                long v = e.Value<long>();
                if (v < min || v > max) throw new ToyPayloadException(ToyReason.OutOfRange, "'" + name + "' element out of range");
                result[i] = (int)v;
            }
            return result;
        }

        public T Enum<T>(string name) where T : struct
        {
            string s = String(name, 32);
            if (!System.Enum.TryParse(s, true, out T v) || !System.Enum.IsDefined(typeof(T), v) || int.TryParse(s, out _))
                throw new ToyPayloadException(ToyReason.Malformed, "'" + name + "' is not a known value");
            return v;
        }
    }

    /// <summary>Builds payloads for clients and tests with the same field names the toys validate.</summary>
    public static class Payload
    {
        public static JObject Of(params object[] nameValuePairs)
        {
            var o = new JObject();
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2)
                o[(string)nameValuePairs[i]] = nameValuePairs[i + 1] == null ? JValue.CreateNull() : JToken.FromObject(nameValuePairs[i + 1]);
            return o;
        }
    }
}
