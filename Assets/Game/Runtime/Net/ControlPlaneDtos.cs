using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace NightSignal.Net
{
    // Shapes of the control-plane protocol (docs/NETWORKING.md §5–6). Unknown members are ignored on read.

    public sealed class AssignmentEntrant
    {
        public string AccountId, DisplayName, Role, CarId, PerformanceHash, CosmeticHash;
        public int CarPi, LoadoutRevision;
        /// <summary>The frozen server-resolved applied build (null from an older control plane: race the stock car).</summary>
        public AssignmentVehicleBuild VehicleBuild;
        /// <summary><c>entrants[].livery</c>: the applied livery in the compact wire form (null = stock appearance). Visual only.</summary>
        public string Livery;
    }

    /// <summary>
    /// <c>entrants[].vehicleBuild</c> (docs/NETWORKING.md §2.6): parts by slot and tuning integers of the car instance's
    /// applied build, and the BuildHash Core computed from them on the control plane. The game server re-resolves the same
    /// snapshot with Core and must arrive at the same hash.
    /// </summary>
    public sealed class AssignmentVehicleBuild
    {
        public string InstanceId, CarId, BuildHash, UtilityPartId, PartsCatalogueHash, HandlingModelVersion;
        public long AppliedRevision;
        public int Pi, TuningVersion;
        public Dictionary<string, string> Parts = new Dictionary<string, string>();
        public Dictionary<string, int> Tuning = new Dictionary<string, int>();

        public Core.Builds.MechanicalSnapshot Snapshot()
        {
            var s = new Core.Builds.MechanicalSnapshot
            {
                UtilityPartId = string.IsNullOrEmpty(UtilityPartId) ? null : UtilityPartId,
                Tuning = new Core.Builds.TuningSetup { Version = TuningVersion > 0 ? TuningVersion : Core.Builds.TuningModel.CurrentVersion },
            };
            foreach (KeyValuePair<string, string> kv in Parts ?? new Dictionary<string, string>()) s.Parts[kv.Key] = kv.Value;
            foreach (KeyValuePair<string, int> kv in Tuning ?? new Dictionary<string, int>()) s.Tuning.Values[kv.Key] = kv.Value;
            return s;
        }
    }

    public sealed class AssignmentBenchmark
    {
        public string Kind, Source;
        public long TargetTimeMs, RawDriftTarget, HardTimeoutMs;
        public bool Provisional;
    }

    /// <summary>One frozen roster actor: kind human|ai, team player|opposing, role, driver identity.</summary>
    public sealed class AssignmentRosterSlot
    {
        public string EntrantId, Kind, Team, Role, DriverId;
    }

    /// <summary>A Team Trial's frozen terms (Addendum 01 §3).</summary>
    public sealed class AssignmentTrial
    {
        public string TrialId, Kind, Difficulty, TiePolicy;
        public long HardTimeoutMs, ParticipationEnvelopeMs;
        public int VictoryPlacement, DefeatPlacement;
        public bool Provisional;
    }

    public sealed class MatchAssignment
    {
        public List<AssignmentRosterSlot> Roster = new List<AssignmentRosterSlot>();
        public AssignmentTrial Trial;
        public string MatchId, ConvoyId, ServerId, Kind, Mode, StageId, StageType, CourseId, FreeplayMode, Weather, Collision;
        public int StageNumber, CarCapPi, Protocol;
        public List<AssignmentEntrant> Entrants = new List<AssignmentEntrant>();
        public List<string> AiEntrants = new List<string>();
        public AssignmentBenchmark Benchmark;
        public bool PurePvP;
        public string GridNote, Build, ContentHash, ResultsUrl, TicketIssuer, TicketAudience, ResultsSecret;
        public long Seed;
    }

    public sealed class AssignmentsResponse { public List<MatchAssignment> Assignments = new List<MatchAssignment>(); }

    public sealed class RegisterResponse
    {
        public string ServerId, AssignmentsUrl, TicketIssuer, TicketAudience, TicketJwksUrl;
        public int HeartbeatIntervalSeconds;
    }

    public sealed class ResultEntrant
    {
        public string EntrantId;
        public bool Human;
        public string Outcome;
        public long FinishTimeMicros;
        public int Placement;
        public bool Clean;
        public double CheckpointFraction;
        public bool ActiveProgressVerified;
        public bool ActivelyDroveLegalCourse;
        public double LegalProgressMetres;
        public long RawDriftScore;
        public int ContractsPassed;
        public List<string> ChallengesCompleted = new List<string>();
    }

    public sealed class MatchResults
    {
        public string MatchId, ContentHash, AbortReason;
        public bool Aborted;
        public List<ResultEntrant> Entrants = new List<ResultEntrant>();
    }

    /// <summary>JSON + HTTP helpers shared by the game server and clients.</summary>
    public static class ControlPlaneHttp
    {
        public static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include,
        };

        public static string Serialize(object o) => JsonConvert.SerializeObject(o, Json);
        public static T Deserialize<T>(string s) => JsonConvert.DeserializeObject<T>(s, Json);

        public static StringContent Body(object o) => new StringContent(Serialize(o), Encoding.UTF8, "application/json");

        /// <summary>HMAC-SHA256 over the exact bytes sent (docs §6), hex lowercase.</summary>
        public static string Sign(byte[] body, string resultsSecretBase64Url)
        {
            byte[] key = Core.Security.Base64Url.Decode(resultsSecretBase64Url);
            using (var h = new HMACSHA256(key))
            {
                byte[] mac = h.ComputeHash(body);
                var sb = new StringBuilder("sha256=");
                foreach (byte b in mac) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static async Task<string> ReadOrThrow(HttpResponseMessage r, string what)
        {
            string body = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException($"{what} failed: {(int)r.StatusCode} {Truncate(body, 300)}");
            return body;
        }

        static string Truncate(string s, int n) => s == null || s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
