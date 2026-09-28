using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;

namespace NightSignal.Core.Profiles
{
    /// <summary>
    /// The Local profile's records a card can showcase (spec §11 "chosen showcase records"), in the same key form as the
    /// online card ("stage:S07:normal", "course:C01:sprint"): the best elapsed time per campaign stage and difficulty and per
    /// freeplay course and format, and the best raw score per Drift Attack course. Local records only ever show on the
    /// Local card (the domains never mix).
    /// </summary>
    public static class LocalShowcase
    {
        public const int MaxShowcase = 3;

        public static List<(string Key, string Label, string Value)> Records(LocalProfile profile, ContentCatalogue catalogue)
        {
            var best = new Dictionary<string, (long Value, string Label, bool Raw)>(StringComparer.Ordinal);
            foreach (RecordEntry e in profile?.Records?.Entries ?? new List<RecordEntry>())
            {
                RecordKey k = e?.Key;
                if (k == null) continue;
                bool raw = k.Metric == MetricKind.RawDriftScore;
                if (k.Metric != MetricKind.ElapsedTime && !raw) continue;
                string course = k.CourseId ?? "";
                string name = catalogue != null && catalogue.TryCourse(course, out CourseDef c) ? c.Name : course;
                string key, label;
                if (k.EventType == RecordEventType.CampaignStage)
                {
                    key = $"stage:{k.EventId}:{k.Difficulty}";
                    label = $"{k.EventId} {(k.Difficulty == RecordKey.HardDifficulty ? "Hard" : "Normal")} · {name}";
                }
                else if (k.EventType == RecordEventType.Freeplay)
                {
                    key = $"course:{course}:{k.Format}";
                    label = $"{course} {name} · {Words(k.Format)}";
                }
                else continue;
                if (!best.TryGetValue(key, out var b) || (raw ? e.Value > b.Value : e.Value < b.Value)) best[key] = (e.Value, label, raw);
            }
            return best.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => (x.Key, x.Value.Label, x.Value.Raw ? $"{x.Value.Value:N0} raw" : Time(x.Value.Value))).ToList();
        }

        /// <summary>Why <paramref name="keys"/> cannot be the showcase (null = it can): too many, repeated, or not this profile's.</summary>
        public static string Problem(IReadOnlyList<string> keys, LocalProfile profile, ContentCatalogue catalogue)
        {
            if (keys == null) return null;
            if (keys.Count > MaxShowcase || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count) return $"Choose up to {MaxShowcase} different records.";
            var mine = new HashSet<string>(Records(profile, catalogue).Select(r => r.Key), StringComparer.Ordinal);
            string unknown = keys.FirstOrDefault(k => !mine.Contains(k ?? ""));
            return unknown == null ? null : $"That record is not one of yours: {unknown}.";
        }

        static string Time(long ms) => $"{ms / 60000}:{ms / 1000 % 60:00}.{ms % 1000:000}";

        static string Words(string format) => format == "time-trial" || format == "time-attack" ? "time attack" : (format ?? "").Replace('-', ' ');
    }
}
