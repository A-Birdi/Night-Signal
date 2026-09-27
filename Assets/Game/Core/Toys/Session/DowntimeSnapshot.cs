using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using NightSignal.Core.Toys.CapClash;
using NightSignal.Core.Toys.Greenlight;
using NightSignal.Core.Toys.PitCrew;
using NightSignal.Core.Toys.PocketCircuit;

namespace NightSignal.Core.Toys
{
    /// <summary>
    /// Compact, versioned, size-bounded persisted form of a <see cref="DowntimeSession"/> (Addendum 02 §1.5, §11). Holds the
    /// authoritative board/project/attempt/lap state; the Canvas document is embedded as a deflated binary (base64).
    /// Content definitions are referenced by id and <see cref="ContentHash"/>, never copied.
    /// </summary>
    public sealed class DowntimeSnapshot
    {
        public int Schema = ToyLimits.SchemaVersion;
        public string Domain = NonProgression.Domain;
        public string SessionId;
        public long Revision;
        public long ClockMs;
        public int PauseGeneration;
        public bool PausedForEvent;
        public bool Ended;
        public long DormantSinceMs;
        public long DormantExpiresMs;
        public long LastDurableRevision;
        public long RandomState;
        public long IdCounter;
        public string ContentHash;
        public List<MemberSeat> Seats;
        public List<RecentRequest> RecentRequests;
        public CapClashState CapClash;
        public PitCrewState PitCrew;
        public GreenlightState Greenlight;
        public PocketCircuitState PocketCircuit;
        /// <summary>Base64 of the deflated binary Canvas document (<see cref="Canvas.CanvasCodec"/>).</summary>
        public string Canvas;
    }

    public static class DowntimeCodec
    {
        static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            MaxDepth = 32,
            NullValueHandling = NullValueHandling.Ignore,
            FloatParseHandling = FloatParseHandling.Double,
            DateParseHandling = DateParseHandling.None,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        /// <summary>Serializes a snapshot; throws when it exceeds <see cref="ToyLimits.MaxSnapshotBytes"/> (never silently truncates).</summary>
        public static string Serialize(DowntimeSnapshot snap)
        {
            string json = JsonConvert.SerializeObject(snap, Settings);
            if (Encoding.UTF8.GetByteCount(json) > ToyLimits.MaxSnapshotBytes)
                throw new InvalidOperationException("downtime snapshot exceeds its size budget");
            return json;
        }

        public static DowntimeSnapshot Deserialize(string json)
        {
            if (json == null || json.Length > ToyLimits.MaxSnapshotBytes) throw new InvalidOperationException("downtime snapshot too large or missing");
            DowntimeSnapshot snap = JsonConvert.DeserializeObject<DowntimeSnapshot>(json, Settings);
            if (snap == null || snap.Schema != ToyLimits.SchemaVersion) throw new InvalidOperationException("unsupported downtime snapshot");
            if (snap.Domain != NonProgression.Domain) throw new InvalidOperationException("not a toy-domain snapshot");
            return snap;
        }

        public static string SerializeJournal(IEnumerable<ToyJournalEntry> entries) => JsonConvert.SerializeObject(entries, Settings);

        public static List<ToyJournalEntry> DeserializeJournal(string json)
        {
            if (json == null || json.Length > ToyLimits.MaxSnapshotBytes) throw new InvalidOperationException("journal too large");
            List<ToyJournalEntry> list = JsonConvert.DeserializeObject<List<ToyJournalEntry>>(json, Settings) ?? new List<ToyJournalEntry>();
            if (list.Count > ToyLimits.MaxJournalEntries) throw new InvalidOperationException("journal has too many entries");
            return list;
        }

        /// <summary>Stable SHA-256 of a serialized snapshot (evidence/equality checks).</summary>
        public static string Hash(string json)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------------------------------------------------
        // Bounded compression (Addendum 02 §6.2: compression/decompression must also bound malicious payloads).

        public static byte[] Deflate(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        /// <summary>Inflates at most <paramref name="maxOutputBytes"/>; a larger (or corrupt) stream throws instead of exhausting memory.</summary>
        public static byte[] Inflate(byte[] compressed, int maxOutputBytes)
        {
            if (compressed == null) throw new InvalidDataException("no data");
            using (var input = new MemoryStream(compressed))
            using (var ds = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                int n;
                while ((n = ds.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + n > maxOutputBytes) throw new InvalidDataException("decompressed payload exceeds its budget");
                    output.Write(buffer, 0, n);
                }
                return output.ToArray();
            }
        }
    }
}
