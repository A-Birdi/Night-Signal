using System;
using NightSignal.Core.Rules;

namespace NightSignal.Core.Profiles
{
    /// <summary>
    /// Typed objective/record metrics (Addendum 01 §4.3). Each event shows only the metrics meaningful to it; values are
    /// integers at the authoritative measurement precision (1 ms for times, whole points/counts for scores).
    /// </summary>
    public enum MetricKind
    {
        /// <summary>Whole-event elapsed time including ordinary penalties, milliseconds.</summary>
        ElapsedTime = 0,
        /// <summary>Fastest legal lap, milliseconds.</summary>
        BestLap = 1,
        /// <summary>Banked, validated RAW drift score (never the showcase score).</summary>
        RawDriftScore = 2,
        /// <summary>Number of clean judged sectors.</summary>
        CleanSectors = 3,
        /// <summary>One award per eligible judged corner meeting the server-tested perfect-drift predicate.</summary>
        PerfectDriftCount = 4,
        /// <summary>Signed difference from the published target, milliseconds (negative = under target).</summary>
        TargetDelta = 5,
        /// <summary>Team Trial MEAN: stored as the exact SUM of the six contributions (ms); displayed as sum / 6.</summary>
        TeamMeanTime = 6,
        /// <summary>Team Trial BEST: the team's fastest legal finisher, milliseconds.</summary>
        TeamBestTime = 7,
        /// <summary>Team Trial DRIFT: summed raw score of the six positions.</summary>
        TeamCombinedScore = 8,
    }

    public enum MetricDirection { LowerIsBetter = 0, HigherIsBetter = 1 }

    public enum MetricUnit
    {
        /// <summary>Non-negative duration in whole milliseconds; a valid time is strictly positive.</summary>
        Milliseconds = 0,
        /// <summary>Signed duration in whole milliseconds.</summary>
        SignedMilliseconds = 1,
        /// <summary>Non-negative integer score.</summary>
        Points = 2,
        /// <summary>Non-negative integer count.</summary>
        Count = 3,
    }

    public sealed class MetricDefinition
    {
        internal MetricDefinition(MetricKind kind, MetricDirection direction, MetricUnit unit, bool team, string name)
        {
            Kind = kind;
            Direction = direction;
            Unit = unit;
            IsTeamMetric = team;
            Name = name;
        }

        public MetricKind Kind { get; }
        public MetricDirection Direction { get; }
        public MetricUnit Unit { get; }
        /// <summary>Team results are labelled and keyed separately and never compared with a personal score.</summary>
        public bool IsTeamMetric { get; }
        public string Name { get; }
        public bool IsTime => Unit == MetricUnit.Milliseconds || Unit == MetricUnit.SignedMilliseconds;

        public bool IsValidValue(long value)
        {
            switch (Unit)
            {
                case MetricUnit.Milliseconds: return value > 0;
                case MetricUnit.SignedMilliseconds: return value > long.MinValue;
                case MetricUnit.Points: return value >= 0;
                case MetricUnit.Count: return value >= 0 && value <= int.MaxValue;
                default: return false;
            }
        }
    }

    public static class Metrics
    {
        static readonly MetricDefinition[] Definitions =
        {
            new MetricDefinition(MetricKind.ElapsedTime, MetricDirection.LowerIsBetter, MetricUnit.Milliseconds, false, "Time"),
            new MetricDefinition(MetricKind.BestLap, MetricDirection.LowerIsBetter, MetricUnit.Milliseconds, false, "Best lap"),
            new MetricDefinition(MetricKind.RawDriftScore, MetricDirection.HigherIsBetter, MetricUnit.Points, false, "Raw drift score"),
            new MetricDefinition(MetricKind.CleanSectors, MetricDirection.HigherIsBetter, MetricUnit.Count, false, "Clean sectors"),
            new MetricDefinition(MetricKind.PerfectDriftCount, MetricDirection.HigherIsBetter, MetricUnit.Count, false, "Perfect drifts"),
            new MetricDefinition(MetricKind.TargetDelta, MetricDirection.LowerIsBetter, MetricUnit.SignedMilliseconds, false, "Target delta"),
            new MetricDefinition(MetricKind.TeamMeanTime, MetricDirection.LowerIsBetter, MetricUnit.Milliseconds, true, "Team mean time"),
            new MetricDefinition(MetricKind.TeamBestTime, MetricDirection.LowerIsBetter, MetricUnit.Milliseconds, true, "Team best time"),
            new MetricDefinition(MetricKind.TeamCombinedScore, MetricDirection.HigherIsBetter, MetricUnit.Points, true, "Team combined score"),
        };

        public static MetricDefinition Get(MetricKind kind)
        {
            int i = (int)kind;
            if (i < 0 || i >= Definitions.Length) throw new ArgumentOutOfRangeException(nameof(kind));
            return Definitions[i];
        }

        /// <summary>
        /// Positive when <paramref name="a"/> is better than <paramref name="b"/> under the metric's direction, zero for a
        /// tie at the stored (authoritative) precision, negative when worse.
        /// </summary>
        public static int Compare(MetricKind kind, long a, long b)
        {
            if (a == b) return 0;
            bool aLower = a < b;
            return Get(kind).Direction == MetricDirection.LowerIsBetter ? (aLower ? 1 : -1) : (aLower ? -1 : 1);
        }

        public static bool IsBetter(MetricKind kind, long candidate, long incumbent) => Compare(kind, candidate, incumbent) > 0;

        /// <summary>The team metric recorded for a Team Trial kind (MEAN → sum of six, BEST → fastest, DRIFT → summed raw).</summary>
        public static MetricKind ForTeamTrial(TeamTrialKind kind)
        {
            switch (kind)
            {
                case TeamTrialKind.Mean: return MetricKind.TeamMeanTime;
                case TeamTrialKind.Best: return MetricKind.TeamBestTime;
                case TeamTrialKind.Drift: return MetricKind.TeamCombinedScore;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
