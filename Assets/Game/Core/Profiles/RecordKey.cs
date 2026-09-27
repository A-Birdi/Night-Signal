using System;
using System.Collections.Generic;
using System.Text;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using Newtonsoft.Json;

namespace NightSignal.Core.Profiles
{
    public enum RecordEventType
    {
        CampaignStage = 0,
        /// <summary>Freeplay Sprint/Circuit/Drift Attack/Time Attack/Cup leg; the event id is course + format.</summary>
        Freeplay = 1,
        TeamTrial = 2,
        Tutorial = 3,
    }

    /// <summary>
    /// The rules a record was measured under, shared by every metric of one event. All strings are compared exactly
    /// (ordinal); changing any of them (a new physics or scoring version, a course revision) makes older records
    /// incompatible — they are kept as legacy results, never silently compared.
    /// </summary>
    public sealed class RecordRuleset
    {
        /// <summary>Direction/layout variant, e.g. "forward".</summary>
        public string CourseVariant = "forward";
        public string CourseRevision = "";
        public string PhysicsVersion = "";
        public string ScoringVersion = "";
        /// <summary>Light Contact vs the explicit non-contact (Time Attack / ghosted) ruleset.</summary>
        public ContactPolicy Contact = ContactPolicy.LightContact;
        /// <summary>Lighting/weather/surface preset, e.g. "night-dry".</summary>
        public string Conditions = "";
        /// <summary>Event car cap as a Performance Index ceiling; 0 = uncapped.</summary>
        public int CarCapPi;
        /// <summary>Assist category, e.g. "standard".</summary>
        public string AssistCategory = "standard";
    }

    /// <summary>
    /// Identifies which records may be compared (Addendum 01 §4.3). Two records are comparable only when their keys are
    /// equal in every field: event, format, metric, mode/difficulty, course/variant/revision, physics/scoring versions,
    /// contact policy, conditions, car cap, assist category, team composition and verification domain. So a ghosted
    /// (non-contact) time never meets a contact time, Normal never meets Hard, a Local unverified record never meets an
    /// Online verified one, and a team sum never meets one driver's personal score. The exact car/build is NOT part of
    /// the key; it is stored on the entry and used by the optional same-car filter.
    /// </summary>
    public sealed class RecordKey : IEquatable<RecordKey>
    {
        public const int CurrentKeyVersion = 1;
        public const string NormalDifficulty = "normal";
        public const string HardDifficulty = "hard";

        public int KeyVersion { get; set; } = CurrentKeyVersion;
        public ProgressionDomain Domain { get; set; } = ProgressionDomain.Local;
        public RecordEventType EventType { get; set; }
        /// <summary>Stage id (S05), trial id (TT_MEAN), or "course/format" for Freeplay (C05/sprint).</summary>
        public string EventId { get; set; } = "";
        /// <summary>sprint | circuit | drift-attack | time-attack | cup | training …</summary>
        public string Format { get; set; } = "";
        public MetricKind Metric { get; set; }
        /// <summary>"normal"/"hard" for campaign stages, the published difficulty id for Team Trials, "" when not applicable.</summary>
        public string Difficulty { get; set; } = "";
        public string CourseId { get; set; } = "";
        public string CourseVariant { get; set; } = "forward";
        public string CourseRevision { get; set; } = "";
        public string PhysicsVersion { get; set; } = "";
        public string ScoringVersion { get; set; } = "";
        public ContactPolicy Contact { get; set; }
        public string Conditions { get; set; } = "";
        public int CarCapPi { get; set; }
        public string AssistCategory { get; set; } = "";
        /// <summary>Team size for team metrics (6); 0 for personal metrics.</summary>
        public int TeamSize { get; set; }
        /// <summary>Humans on the player side for team metrics (composition policy); 0 for personal metrics.</summary>
        public int TeamHumans { get; set; }

        /// <summary>Stable, exact identity string (all fields). Used for equality and storage.</summary>
        [JsonIgnore]
        public string Canonical
        {
            get
            {
                var sb = new StringBuilder(160);
                sb.Append('k').Append(KeyVersion);
                Add(sb, Domain.ToString());
                Add(sb, EventType.ToString());
                Add(sb, EventId);
                Add(sb, Format);
                Add(sb, Metric.ToString());
                Add(sb, Difficulty);
                Add(sb, CourseId);
                Add(sb, CourseVariant);
                Add(sb, CourseRevision);
                Add(sb, PhysicsVersion);
                Add(sb, ScoringVersion);
                Add(sb, Contact.ToString());
                Add(sb, Conditions);
                Add(sb, CarCapPi.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Add(sb, AssistCategory);
                Add(sb, TeamSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Add(sb, TeamHumans.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>
        /// The event/metric identity without rules versions: records sharing it but differing in versions, contact
        /// policy, conditions, cap, assists or composition are shown as "Legacy/incompatible result", never ranked.
        /// </summary>
        [JsonIgnore]
        public string Identity
        {
            get
            {
                var sb = new StringBuilder(64);
                sb.Append('i');
                Add(sb, Domain.ToString());
                Add(sb, EventType.ToString());
                Add(sb, EventId);
                Add(sb, Format);
                Add(sb, Metric.ToString());
                Add(sb, Difficulty);
                Add(sb, CourseId);
                Add(sb, CourseVariant);
                return sb.ToString();
            }
        }

        static void Add(StringBuilder sb, string value)
        {
            sb.Append('|');
            foreach (char ch in value ?? "")
            {
                if (ch == '|' || ch == '\\') sb.Append('\\');
                sb.Append(ch);
            }
        }

        public bool Equals(RecordKey other) => other != null && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);
        public override bool Equals(object obj) => Equals(obj as RecordKey);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical);
        public override string ToString() => Canonical;

        public RecordKey Clone() => (RecordKey)MemberwiseClone();

        public RecordKey WithMetric(MetricKind metric)
        {
            RecordKey k = Clone();
            k.Metric = metric;
            return k;
        }

        /// <summary>Structural problems; empty when the key is usable.</summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (KeyVersion != CurrentKeyVersion) errors.Add($"Unknown record key version {KeyVersion}.");
            if (!Enum.IsDefined(typeof(ProgressionDomain), Domain)) errors.Add("Unknown verification domain.");
            if (!Enum.IsDefined(typeof(RecordEventType), EventType)) errors.Add("Unknown event type.");
            if (!Enum.IsDefined(typeof(MetricKind), Metric)) errors.Add("Unknown metric.");
            if (!Enum.IsDefined(typeof(ContactPolicy), Contact)) errors.Add("Unknown contact policy.");
            if (string.IsNullOrEmpty(EventId)) errors.Add("A record needs an event id.");
            if (string.IsNullOrEmpty(CourseId)) errors.Add("A record needs a course id.");
            if (string.IsNullOrEmpty(Format)) errors.Add("A record needs a format.");
            if (CarCapPi < 0) errors.Add("Car cap cannot be negative.");
            if (EventType == RecordEventType.CampaignStage && Difficulty != NormalDifficulty && Difficulty != HardDifficulty)
                errors.Add("Campaign records are keyed to Normal or Hard.");
            bool team = Enum.IsDefined(typeof(MetricKind), Metric) && Metrics.Get(Metric).IsTeamMetric;
            if (team)
            {
                if (EventType != RecordEventType.TeamTrial) errors.Add("Team metrics belong to Team Trials.");
                if (TeamSize != Limits.TeamTrialSideSize) errors.Add($"Team records are keyed to a {Limits.TeamTrialSideSize}-position team.");
                if (TeamHumans < 1 || TeamHumans > TeamSize) errors.Add("Team records need the human composition (1..team size).");
            }
            else if (TeamSize != 0 || TeamHumans != 0)
            {
                errors.Add("Personal records carry no team composition.");
            }
            return errors;
        }

        // ---------------- factories ----------------

        public static RecordKey ForCampaignStage(ProgressionDomain domain, StageDef stage, CourseDef course, CampaignMode mode, MetricKind metric, RecordRuleset rules)
        {
            if (stage == null) throw new ArgumentNullException(nameof(stage));
            if (course == null) throw new ArgumentNullException(nameof(course));
            if (stage.Course != course.Id) throw new ArgumentException($"{stage.Id} runs on {stage.Course}, not {course.Id}");
            RecordKey k = Base(domain, RecordEventType.CampaignStage, stage.Id, course.Format, metric, course.Id, rules);
            k.Difficulty = mode == CampaignMode.Hard ? HardDifficulty : NormalDifficulty;
            if (k.CarCapPi == 0) k.CarCapPi = stage.MaxPI;
            return k;
        }

        public static RecordKey ForFreeplay(ProgressionDomain domain, string courseId, string format, MetricKind metric, RecordRuleset rules)
        {
            if (string.IsNullOrEmpty(courseId)) throw new ArgumentException("Course id required", nameof(courseId));
            if (string.IsNullOrEmpty(format)) throw new ArgumentException("Format required", nameof(format));
            return Base(domain, RecordEventType.Freeplay, courseId + "/" + format, format, metric, courseId, rules);
        }

        public static RecordKey ForTeamTrial(ProgressionDomain domain, string trialId, string courseId, string format, string difficulty,
            TeamTrialKind kind, int humans, RecordRuleset rules)
        {
            if (string.IsNullOrEmpty(trialId)) throw new ArgumentException("Trial id required", nameof(trialId));
            RecordKey k = Base(domain, RecordEventType.TeamTrial, trialId, format, Metrics.ForTeamTrial(kind), courseId, rules);
            k.Difficulty = difficulty ?? "";
            k.TeamSize = Limits.TeamTrialSideSize;
            k.TeamHumans = humans;
            return k;
        }

        static RecordKey Base(ProgressionDomain domain, RecordEventType type, string eventId, string format, MetricKind metric, string courseId, RecordRuleset rules)
        {
            rules = rules ?? new RecordRuleset();
            return new RecordKey
            {
                Domain = domain,
                EventType = type,
                EventId = eventId ?? "",
                Format = format ?? "",
                Metric = metric,
                CourseId = courseId ?? "",
                CourseVariant = rules.CourseVariant ?? "",
                CourseRevision = rules.CourseRevision ?? "",
                PhysicsVersion = rules.PhysicsVersion ?? "",
                ScoringVersion = rules.ScoringVersion ?? "",
                Contact = rules.Contact,
                Conditions = rules.Conditions ?? "",
                CarCapPi = rules.CarCapPi,
                AssistCategory = rules.AssistCategory ?? "",
            };
        }
    }

    /// <summary>Why two record keys cannot be compared.</summary>
    public enum RecordMismatch
    {
        KeyVersion = 0,
        /// <summary>Local unverified vs Online verified.</summary>
        Domain = 1,
        EventType = 2,
        Event = 3,
        Format = 4,
        Metric = 5,
        /// <summary>A team result vs one driver's personal score.</summary>
        TeamVersusPersonal = 6,
        /// <summary>Normal vs Hard (or different trial difficulty).</summary>
        Difficulty = 7,
        Course = 8,
        CourseVariant = 9,
        CourseRevision = 10,
        PhysicsVersion = 11,
        ScoringVersion = 12,
        /// <summary>Ghosted/non-contact vs contact.</summary>
        ContactPolicy = 13,
        Conditions = 14,
        CarCap = 15,
        AssistCategory = 16,
        TeamComposition = 17,
    }

    public sealed class RecordCompatibility
    {
        RecordCompatibility(IReadOnlyList<RecordMismatch> mismatches) => Mismatches = mismatches;

        public IReadOnlyList<RecordMismatch> Mismatches { get; }
        public bool Compatible => Mismatches.Count == 0;

        /// <summary>Plain-language reason for the first mismatch, or "" when compatible.</summary>
        public string Explanation => Mismatches.Count == 0 ? "" : Explain(Mismatches[0]);

        public static RecordCompatibility Check(RecordKey a, RecordKey b)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            var m = new List<RecordMismatch>();
            if (a.KeyVersion != b.KeyVersion) m.Add(RecordMismatch.KeyVersion);
            if (a.Domain != b.Domain) m.Add(RecordMismatch.Domain);
            if (a.EventType != b.EventType) m.Add(RecordMismatch.EventType);
            if (!Same(a.EventId, b.EventId)) m.Add(RecordMismatch.Event);
            if (!Same(a.Format, b.Format)) m.Add(RecordMismatch.Format);
            if (a.Metric != b.Metric)
            {
                bool teamA = Enum.IsDefined(typeof(MetricKind), a.Metric) && Metrics.Get(a.Metric).IsTeamMetric;
                bool teamB = Enum.IsDefined(typeof(MetricKind), b.Metric) && Metrics.Get(b.Metric).IsTeamMetric;
                if (teamA != teamB) m.Add(RecordMismatch.TeamVersusPersonal);
                m.Add(RecordMismatch.Metric);
            }
            if (!Same(a.Difficulty, b.Difficulty)) m.Add(RecordMismatch.Difficulty);
            if (!Same(a.CourseId, b.CourseId)) m.Add(RecordMismatch.Course);
            if (!Same(a.CourseVariant, b.CourseVariant)) m.Add(RecordMismatch.CourseVariant);
            if (!Same(a.CourseRevision, b.CourseRevision)) m.Add(RecordMismatch.CourseRevision);
            if (!Same(a.PhysicsVersion, b.PhysicsVersion)) m.Add(RecordMismatch.PhysicsVersion);
            if (!Same(a.ScoringVersion, b.ScoringVersion)) m.Add(RecordMismatch.ScoringVersion);
            if (a.Contact != b.Contact) m.Add(RecordMismatch.ContactPolicy);
            if (!Same(a.Conditions, b.Conditions)) m.Add(RecordMismatch.Conditions);
            if (a.CarCapPi != b.CarCapPi) m.Add(RecordMismatch.CarCap);
            if (!Same(a.AssistCategory, b.AssistCategory)) m.Add(RecordMismatch.AssistCategory);
            if (a.TeamSize != b.TeamSize || a.TeamHumans != b.TeamHumans) m.Add(RecordMismatch.TeamComposition);
            // Put the most important reason first for UI explanations.
            m.Sort((x, y) => Priority(x).CompareTo(Priority(y)));
            return new RecordCompatibility(m);
        }

        static bool Same(string x, string y) => string.Equals(x ?? "", y ?? "", StringComparison.Ordinal);

        static int Priority(RecordMismatch m)
        {
            switch (m)
            {
                case RecordMismatch.Domain: return 0;
                case RecordMismatch.TeamVersusPersonal: return 1;
                case RecordMismatch.ContactPolicy: return 2;
                case RecordMismatch.Difficulty: return 3;
                default: return 10 + (int)m;
            }
        }

        public static string Explain(RecordMismatch m)
        {
            switch (m)
            {
                case RecordMismatch.Domain: return "Local (unverified) and Online (verified) records are never compared.";
                case RecordMismatch.TeamVersusPersonal: return "A team result is never compared with a personal score.";
                case RecordMismatch.ContactPolicy: return "Non-contact (Time Attack) and contact results are never compared.";
                case RecordMismatch.Difficulty: return "Normal and Hard (and different trial difficulties) keep separate records.";
                case RecordMismatch.PhysicsVersion:
                case RecordMismatch.ScoringVersion:
                case RecordMismatch.CourseRevision:
                case RecordMismatch.KeyVersion:
                    return "Recorded under different physics, scoring or course rules (legacy result).";
                case RecordMismatch.Conditions: return "Recorded in different conditions.";
                case RecordMismatch.CarCap: return "Recorded under a different car class cap.";
                case RecordMismatch.AssistCategory: return "Recorded with a different assist category.";
                case RecordMismatch.TeamComposition: return "Recorded with a different team composition.";
                case RecordMismatch.Metric: return "Different metric.";
                case RecordMismatch.Format: return "Different event format.";
                case RecordMismatch.CourseVariant: return "Different course direction or variant.";
                default: return "Different event.";
            }
        }
    }
}
