using System;

namespace NightSignal.Core.Profiles
{
    /// <summary>Verification label carried by every stored record.</summary>
    public enum RecordVerification
    {
        /// <summary>Measured by this PC's local simulation in the Local domain. Never promotable to Online verified.</summary>
        LocalUnverified = 0,
        /// <summary>Settled by the Online server from server-observed facts.</summary>
        OnlineVerified = 1,
        /// <summary>An Online result the server has not settled yet; shown, never ranked.</summary>
        PendingVerification = 2,
        /// <summary>Archived because rules changed (physics/scoring/course/contact); kept, shown as legacy, never ranked.</summary>
        Legacy = 3,
    }

    /// <summary>Where a record came from.</summary>
    public sealed class RecordProvenance
    {
        /// <summary>The local event id (Local) or the settled match id (Online) that produced the value.</summary>
        public string EventInstanceId { get; set; } = "";
        public DateTime AchievedUtc { get; set; }
        /// <summary>Rules/content version in force (e.g. the content catalogue hash).</summary>
        public string RulesVersion { get; set; } = "";
        /// <summary>"local-simulation" for the Local domain, "server-settlement" for Online.</summary>
        public string Origin { get; set; } = "";

        public RecordProvenance Clone() => (RecordProvenance)MemberwiseClone();
    }

    /// <summary>
    /// One stored record value. Times are integer milliseconds (1 ms is the authoritative measurement precision, see
    /// <see cref="NightSignal.Core.Rules.RaceClassification.MeasurementPrecisionMicros"/>); scores and counts are
    /// integers. The exact car model, owned car instance (when not a loaner) and build id are kept for provenance and the
    /// optional same-car filter.
    /// </summary>
    public sealed class RecordEntry
    {
        public const string LocalOrigin = "local-simulation";
        public const string ServerOrigin = "server-settlement";

        public RecordKey Key { get; set; }
        public long Value { get; set; }
        /// <summary>Car model id (V01…V18).</summary>
        public string CarModelId { get; set; } = "";
        /// <summary>Owned car instance id, or "" for a campaign loaner.</summary>
        public string CarInstanceId { get; set; } = "";
        /// <summary>Exact build/tuning hash used for the run ("loaner:V01" style ids for loaners).</summary>
        public string BuildId { get; set; } = "";
        public bool Loaner { get; set; }
        public RecordVerification Verification { get; set; }
        /// <summary>Label before archiving (only for archived entries).</summary>
        public RecordVerification? OriginalVerification { get; set; }
        public string ArchivedReason { get; set; }
        public DateTime? ArchivedUtc { get; set; }
        public RecordProvenance Provenance { get; set; } = new RecordProvenance();

        public RecordEntry Clone()
        {
            var e = (RecordEntry)MemberwiseClone();
            e.Key = Key?.Clone();
            e.Provenance = Provenance?.Clone();
            return e;
        }
    }

    /// <summary>Optional same-car filter for record views and comparisons (by car model).</summary>
    public sealed class RecordFilter
    {
        public string CarModelId { get; set; }

        public static RecordFilter SameCar(string carModelId) => new RecordFilter { CarModelId = carModelId };

        public bool Matches(RecordEntry e) =>
            e != null && (string.IsNullOrEmpty(CarModelId) || string.Equals(e.CarModelId, CarModelId, StringComparison.Ordinal));
    }

    public enum RecordCompareOutcome { Better = 0, Worse = 1, Tie = 2, Incompatible = 3 }

    public static class RecordComparer
    {
        /// <summary>
        /// Labels that may be ranked against each other: Local unverified with Local unverified, Online verified with
        /// Online verified. Pending and legacy results are displayed but never ranked.
        /// </summary>
        public static bool Rankable(RecordVerification a, RecordVerification b) =>
            a == b && (a == RecordVerification.LocalUnverified || a == RecordVerification.OnlineVerified);

        /// <summary>
        /// Compares <paramref name="candidate"/> with <paramref name="incumbent"/>. Incompatible keys or unrankable labels
        /// give <see cref="RecordCompareOutcome.Incompatible"/>; equal values are a tie at the stored precision.
        /// </summary>
        public static RecordCompareOutcome Compare(RecordEntry candidate, RecordEntry incumbent, RecordFilter filter = null)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (incumbent == null) throw new ArgumentNullException(nameof(incumbent));
            if (candidate.Key == null || incumbent.Key == null) return RecordCompareOutcome.Incompatible;
            if (!RecordCompatibility.Check(candidate.Key, incumbent.Key).Compatible) return RecordCompareOutcome.Incompatible;
            if (!Rankable(candidate.Verification, incumbent.Verification)) return RecordCompareOutcome.Incompatible;
            if (filter != null && (!filter.Matches(candidate) || !filter.Matches(incumbent))) return RecordCompareOutcome.Incompatible;
            int c = Metrics.Compare(candidate.Key.Metric, candidate.Value, incumbent.Value);
            return c > 0 ? RecordCompareOutcome.Better : c < 0 ? RecordCompareOutcome.Worse : RecordCompareOutcome.Tie;
        }

        /// <summary>Whether a label is allowed in a domain's record store.</summary>
        public static bool AllowedIn(ProgressionDomain domain, RecordVerification v)
        {
            switch (v)
            {
                case RecordVerification.LocalUnverified: return domain == ProgressionDomain.Local;
                case RecordVerification.OnlineVerified:
                case RecordVerification.PendingVerification: return domain == ProgressionDomain.Online;
                case RecordVerification.Legacy: return true;
                default: return false;
            }
        }
    }
}
