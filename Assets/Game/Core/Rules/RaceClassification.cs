using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    public sealed class EntrantFinish
    {
        public string EntrantId = "";
        public RunOutcome Outcome;
        /// <summary>Server race-clock finish time in microseconds (sub-tick interpolated).</summary>
        public long FinishTimeMicros;
        /// <summary>Legal progress in metres, used to order DNFs.</summary>
        public double LegalProgressMetres;
    }

    public sealed class Placing
    {
        public string EntrantId = "";
        /// <summary>1-based placing; 0 when unplaced (quit, DQ).</summary>
        public int Place;
        public bool Tied;
        public RunOutcome Outcome;
    }

    /// <summary>
    /// Finish classification, spec §18. Times are compared at the documented 1 ms measurement precision;
    /// equal milliseconds are an explicit tie sharing the placing — never broken by account ID.
    /// DNFs follow finishers by legal progress; quits and DQs are unplaced.
    /// </summary>
    public static class RaceClassification
    {
        public const long MeasurementPrecisionMicros = 1000;

        public static long ToReportedMillis(long micros) => micros / MeasurementPrecisionMicros;

        public static IReadOnlyList<Placing> Classify(IReadOnlyList<EntrantFinish> entrants)
        {
            if (entrants == null) throw new ArgumentNullException(nameof(entrants));
            if (entrants.Count > Limits.MaxRaceEntrants)
                throw new ArgumentException($"At most {Limits.MaxRaceEntrants} entrants");

            var result = new List<Placing>();
            var finishers = entrants.Where(e => e.Outcome == RunOutcome.Finished)
                .OrderBy(e => ToReportedMillis(e.FinishTimeMicros)).ToList();
            int place = 0;
            long previousMs = -1;
            for (int i = 0; i < finishers.Count; i++)
            {
                long ms = ToReportedMillis(finishers[i].FinishTimeMicros);
                if (ms != previousMs)
                    place = i + 1;
                bool tied = finishers.Count(f => ToReportedMillis(f.FinishTimeMicros) == ms) > 1;
                result.Add(new Placing { EntrantId = finishers[i].EntrantId, Place = place, Tied = tied, Outcome = RunOutcome.Finished });
                previousMs = ms;
            }

            int next = finishers.Count;
            foreach (EntrantFinish dnf in entrants.Where(e => e.Outcome == RunOutcome.DidNotFinish)
                         .OrderByDescending(e => e.LegalProgressMetres))
                result.Add(new Placing { EntrantId = dnf.EntrantId, Place = ++next, Outcome = RunOutcome.DidNotFinish });

            foreach (EntrantFinish other in entrants.Where(e => e.Outcome != RunOutcome.Finished && e.Outcome != RunOutcome.DidNotFinish))
                result.Add(new Placing { EntrantId = other.EntrantId, Place = 0, Outcome = other.Outcome });
            return result;
        }
    }
}
