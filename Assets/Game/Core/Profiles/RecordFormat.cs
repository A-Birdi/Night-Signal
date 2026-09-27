using System;
using System.Globalization;
using NightSignal.Core.Rules;

namespace NightSignal.Core.Profiles
{
    /// <summary>
    /// Consistent record/metric text (Addendum 01 §4.3): race time mm:ss.mmm (03:42.815; an hour or more is h:mm:ss.mmm),
    /// no valid time "--:--.---", no eligible score "Score: N/A", counts "Best: 3 / Target: 5" or "Best: — / Target: 5".
    /// Formatting truncates to the authoritative 1 ms precision; it never rounds a time up.
    /// </summary>
    public static class RecordFormat
    {
        public const string NoTime = "--:--.---";
        public const string NoScore = "Score: N/A";
        /// <summary>Em dash used for "no valid count" (not zero pretending an attempt occurred).</summary>
        public const string NoCount = "—";

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>Race time from whole milliseconds: mm:ss.mmm below one hour, h:mm:ss.mmm from one hour.</summary>
        public static string Time(long milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds), "Race times are never negative");
            long ms = milliseconds % 1000;
            long totalSeconds = milliseconds / 1000;
            long seconds = totalSeconds % 60;
            long totalMinutes = totalSeconds / 60;
            if (totalMinutes < 60)
                return totalMinutes.ToString("00", Invariant) + ":" + seconds.ToString("00", Invariant) + "." + ms.ToString("000", Invariant);
            long hours = totalMinutes / 60;
            long minutes = totalMinutes % 60;
            return hours.ToString(Invariant) + ":" + minutes.ToString("00", Invariant) + ":" + seconds.ToString("00", Invariant) + "." +
                   ms.ToString("000", Invariant);
        }

        /// <summary>Race time from server microseconds, truncated to the 1 ms measurement precision.</summary>
        public static string TimeFromMicros(long micros) => Time(RaceClassification.ToReportedMillis(micros));

        public static string OptionalTime(long? milliseconds) => milliseconds.HasValue && milliseconds.Value > 0 ? Time(milliseconds.Value) : NoTime;

        /// <summary>Signed delta: "+00:01.234" slower, "-00:01.234" faster, "±00:00.000" exactly level.</summary>
        public static string Delta(long milliseconds)
        {
            if (milliseconds == 0) return "±" + Time(0);
            if (milliseconds == long.MinValue) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            return (milliseconds > 0 ? "+" : "-") + Time(Math.Abs(milliseconds));
        }

        public static string Score(long points)
        {
            if (points < 0) throw new ArgumentOutOfRangeException(nameof(points));
            return "Score: " + points.ToString("N0", Invariant);
        }

        public static string OptionalScore(long? points) => points.HasValue && points.Value >= 0 ? Score(points.Value) : NoScore;

        /// <summary>"Best: 3 / Target: 5", "Best: — / Target: 5" (no valid count), or without the target part.</summary>
        public static string Count(long? best, long? target = null)
        {
            string b = best.HasValue && best.Value >= 0 ? best.Value.ToString(Invariant) : NoCount;
            return target.HasValue ? "Best: " + b + " / Target: " + target.Value.ToString(Invariant) : "Best: " + b;
        }

        /// <summary>
        /// Metric-aware value text. <paramref name="value"/> null means no valid value. Team mean values are stored as the
        /// exact sum of six contributions and displayed as the mean (truncated to 1 ms).
        /// </summary>
        public static string Value(MetricKind metric, long? value, long? target = null)
        {
            MetricDefinition def = Metrics.Get(metric);
            switch (def.Unit)
            {
                case MetricUnit.Milliseconds:
                    if (!value.HasValue || value.Value <= 0) return NoTime;
                    return Time(metric == MetricKind.TeamMeanTime ? value.Value / Limits.TeamTrialSideSize : value.Value);
                case MetricUnit.SignedMilliseconds:
                    return value.HasValue ? Delta(value.Value) : NoTime;
                case MetricUnit.Points:
                    return OptionalScore(value);
                case MetricUnit.Count:
                    return Count(value, target);
                default:
                    throw new ArgumentOutOfRangeException(nameof(metric));
            }
        }

        public static string State(RecordDisplayState state)
        {
            switch (state)
            {
                case RecordDisplayState.HasValue: return "";
                case RecordDisplayState.NoResult: return "No result";
                case RecordDisplayState.NotAttempted: return "Not attempted";
                case RecordDisplayState.DidNotFinish: return "Did not finish";
                case RecordDisplayState.PendingVerification: return "Pending verification";
                case RecordDisplayState.LegacyIncompatible: return "Legacy/incompatible result";
                default: throw new ArgumentOutOfRangeException(nameof(state));
            }
        }

        public static string Verification(RecordVerification v)
        {
            switch (v)
            {
                case RecordVerification.LocalUnverified: return "Local / Unverified";
                case RecordVerification.OnlineVerified: return "Verified";
                case RecordVerification.PendingVerification: return "Pending verification";
                case RecordVerification.Legacy: return "Legacy";
                default: throw new ArgumentOutOfRangeException(nameof(v));
            }
        }
    }
}
