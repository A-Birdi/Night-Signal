using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Ghosts
{
    /// <summary>
    /// What a ghost was recorded under (spec §8: "course ID/revision, direction, weather/surface, physics version, tuning/car
    /// hash, class, assist flags, game version, raw result, resets, provenance, and validity"). Two ghosts compare only when
    /// <see cref="GhostRecording.CompatibleWith"/> says so; otherwise one is reference-only.
    /// </summary>
    public sealed class GhostHeader
    {
        public string CourseId = "", CourseRevision = "", Direction = "forward", Format = "", Surface = "dry";
        public string PhysicsVersion = "", ScoringVersion = "", GameVersion = "";
        public string CarModelId = "", BuildHash = "", CarClass = "", Assists = "standard";
        public int Pi;
        /// <summary>The legal finish time (microseconds) or 0 when the run did not finish.</summary>
        public long ResultMicros;
        public int Resets;
        public bool CorridorCut;
        /// <summary>local-simulation (a Local practice ghost: never a trusted online record) | server-settlement.</summary>
        public string Provenance = "local-simulation";
        public string Driver = "";
        public DateTime RecordedUtc;
    }

    /// <summary>
    /// A replay ghost (spec §8): sampled transforms of one car over race time — never an input stream to re-simulate — plus the
    /// time at each checkpoint for sector deltas. Playback interpolates between samples. Positions are metres, rotations a
    /// unit quaternion, speed m/s, time seconds from the start.
    /// </summary>
    public sealed class GhostRecording
    {
        public const string SchemaId = "night-signal/ghost@1";
        public const int SampleHz = 10;
        /// <summary>Fifteen minutes at 10 Hz: the longest event a ghost may cover (bounded storage).</summary>
        public const int MaxSamples = 15 * 60 * SampleHz;

        public GhostHeader Header = new GhostHeader();
        public readonly List<float> T = new List<float>(), Px = new List<float>(), Py = new List<float>(), Pz = new List<float>();
        public readonly List<float> Qx = new List<float>(), Qy = new List<float>(), Qz = new List<float>(), Qw = new List<float>(), Speed = new List<float>();
        /// <summary>Race time (microseconds) at each checkpoint passed, in order.</summary>
        public readonly List<long> CheckpointMicros = new List<long>();

        public int Count => T.Count;

        public void Add(float t, float x, float y, float z, float qx, float qy, float qz, float qw, float speed)
        {
            if (T.Count >= MaxSamples) return;
            T.Add(t); Px.Add(x); Py.Add(y); Pz.Add(z); Qx.Add(qx); Qy.Add(qy); Qz.Add(qz); Qw.Add(qw); Speed.Add(speed);
        }

        /// <summary>A valid personal ghost: well-formed, a legal finish, no reset (a reset-slowed run never becomes a target).</summary>
        public bool ValidPersonal => Problems().Count == 0 && Header.ResultMicros > 0 && Header.Resets == 0 && !Header.CorridorCut;

        /// <summary>Why this recording cannot be used at all (empty when it can).</summary>
        public List<string> Problems()
        {
            var p = new List<string>();
            int n = T.Count;
            if (n < 2) p.Add("fewer than two samples");
            if (n > MaxSamples) p.Add("too many samples");
            if (new[] { Px.Count, Py.Count, Pz.Count, Qx.Count, Qy.Count, Qz.Count, Qw.Count, Speed.Count }.Any(c => c != n)) p.Add("sample arrays differ in length");
            for (int i = 1; i < n && p.Count == 0; i++)
                if (!(T[i] > T[i - 1])) p.Add("sample times are not increasing");
            if (p.Count == 0 && Enumerable.Range(0, n).Any(i => !Finite(Px[i]) || !Finite(Py[i]) || !Finite(Pz[i]) || !Finite(Qw[i]))) p.Add("a sample is not finite");
            for (int i = 1; i < CheckpointMicros.Count; i++)
                if (CheckpointMicros[i] <= CheckpointMicros[i - 1]) { p.Add("checkpoint times are not increasing"); break; }
            if (string.IsNullOrEmpty(Header.CourseId)) p.Add("no course");
            if (Header.ResultMicros > 0 && n > 0 && Math.Abs(T[n - 1] - Header.ResultMicros / 1e6) > 1.5) p.Add("the samples do not end at the result");
            return p;
        }

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        /// <summary>Compared only under the same course revision, direction, format, surface and physics/scoring rules.</summary>
        public bool CompatibleWith(GhostHeader other) =>
            other != null && Header.CourseId == other.CourseId && Header.CourseRevision == other.CourseRevision && Header.Direction == other.Direction &&
            Header.Format == other.Format && Header.Surface == other.Surface && Header.PhysicsVersion == other.PhysicsVersion &&
            Header.ScoringVersion == other.ScoringVersion;

        /// <summary>The rules a ghost is kept and compared under (with its course and format): one best ghost per key.</summary>
        public static string RulesKey(GhostHeader h) =>
            h == null ? "" : $"{h.CourseRevision}|{h.Direction}|{h.Surface}|{h.PhysicsVersion}|{h.ScoringVersion}";

        /// <summary>The last sample at or before <paramref name="t"/> (0 before the first; the last after the end).</summary>
        public int IndexAt(float t)
        {
            if (T.Count == 0 || t <= T[0]) return 0;
            int lo = 0, hi = T.Count - 1;
            if (t >= T[hi]) return hi;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (T[mid] <= t) lo = mid; else hi = mid;
            }
            return lo;
        }

        /// <summary>This run's time at checkpoint <paramref name="index"/> minus the ghost's (negative = ahead); null when the ghost has none.</summary>
        public long? SectorDeltaMicros(int index, long myMicros) =>
            index >= 0 && index < CheckpointMicros.Count ? myMicros - CheckpointMicros[index] : (long?)null;

        // ------------------------------------------------------------------ storage

        public string ToJson()
        {
            var h = Header;
            var o = new JObject
            {
                ["schema"] = SchemaId,
                ["header"] = new JObject
                {
                    ["courseId"] = h.CourseId, ["courseRevision"] = h.CourseRevision, ["direction"] = h.Direction, ["format"] = h.Format, ["surface"] = h.Surface,
                    ["physicsVersion"] = h.PhysicsVersion, ["scoringVersion"] = h.ScoringVersion, ["gameVersion"] = h.GameVersion,
                    ["carModelId"] = h.CarModelId, ["buildHash"] = h.BuildHash, ["carClass"] = h.CarClass, ["assists"] = h.Assists, ["pi"] = h.Pi,
                    ["resultMicros"] = h.ResultMicros, ["resets"] = h.Resets, ["corridorCut"] = h.CorridorCut, ["provenance"] = h.Provenance,
                    ["driver"] = h.Driver, ["recordedUtc"] = h.RecordedUtc.ToString("o", CultureInfo.InvariantCulture),
                },
                ["sampleHz"] = SampleHz,
                ["t"] = Arr(T, 3), ["px"] = Arr(Px, 3), ["py"] = Arr(Py, 3), ["pz"] = Arr(Pz, 3),
                ["qx"] = Arr(Qx, 4), ["qy"] = Arr(Qy, 4), ["qz"] = Arr(Qz, 4), ["qw"] = Arr(Qw, 4), ["speed"] = Arr(Speed, 2),
                ["checkpoints"] = new JArray(CheckpointMicros.Cast<object>().ToArray()),
            };
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        static JArray Arr(List<float> v, int digits) => new JArray(v.Select(x => (object)Math.Round((double)x, digits)).ToArray());

        /// <summary>Reads a stored ghost; null with <paramref name="error"/> when it is not one or is malformed.</summary>
        public static GhostRecording Parse(string json, out string error)
        {
            error = null;
            try
            {
                JObject o = JObject.Parse(json);
                if ((string)o["schema"] != SchemaId) { error = "not a ghost@1 document"; return null; }
                var g = new GhostRecording();
                JObject h = o["header"] as JObject ?? new JObject();
                g.Header = new GhostHeader
                {
                    CourseId = (string)h["courseId"] ?? "", CourseRevision = (string)h["courseRevision"] ?? "", Direction = (string)h["direction"] ?? "forward",
                    Format = (string)h["format"] ?? "", Surface = (string)h["surface"] ?? "dry", PhysicsVersion = (string)h["physicsVersion"] ?? "",
                    ScoringVersion = (string)h["scoringVersion"] ?? "", GameVersion = (string)h["gameVersion"] ?? "", CarModelId = (string)h["carModelId"] ?? "",
                    BuildHash = (string)h["buildHash"] ?? "", CarClass = (string)h["carClass"] ?? "", Assists = (string)h["assists"] ?? "standard",
                    Pi = (int?)h["pi"] ?? 0, ResultMicros = (long?)h["resultMicros"] ?? 0, Resets = (int?)h["resets"] ?? 0, CorridorCut = (bool?)h["corridorCut"] ?? false,
                    Provenance = (string)h["provenance"] ?? "local-simulation", Driver = (string)h["driver"] ?? "",
                    RecordedUtc = DateTime.TryParse((string)h["recordedUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime at) ? at : default,
                };
                Fill(g.T, o["t"]); Fill(g.Px, o["px"]); Fill(g.Py, o["py"]); Fill(g.Pz, o["pz"]);
                Fill(g.Qx, o["qx"]); Fill(g.Qy, o["qy"]); Fill(g.Qz, o["qz"]); Fill(g.Qw, o["qw"]); Fill(g.Speed, o["speed"]);
                foreach (JToken c in (o["checkpoints"] as JArray) ?? new JArray()) g.CheckpointMicros.Add((long)c);
                if (g.T.Count > MaxSamples) { error = "too many samples"; return null; }
                return g;
            }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                error = "unreadable ghost";
                return null;
            }
        }

        static void Fill(List<float> list, JToken t)
        {
            foreach (JToken v in (t as JArray) ?? new JArray()) list.Add((float)v);
        }
    }

    /// <summary>
    /// Appendix E CH68 "Chasing Your Yesterday": record a valid C07 personal ghost, then beat it by at least 1 second in a
    /// compatible ruleset; a reset-created artificially slow ghost does not qualify (only a valid ghost counts, and so must
    /// the new run be).
    /// </summary>
    public static class GhostChallenges
    {
        public const string ChasingYourYesterday = "CH68";
        public const string Course = "C07";
        public const long MarginMicros = 1_000_000;

        public static bool BeatsYesterday(GhostRecording yesterday, GhostRecording today) =>
            yesterday != null && today != null && yesterday.Header.CourseId == Course && yesterday.ValidPersonal && today.ValidPersonal &&
            yesterday.CompatibleWith(today.Header) && yesterday.Header.ResultMicros - today.Header.ResultMicros >= MarginMicros;
    }
}
