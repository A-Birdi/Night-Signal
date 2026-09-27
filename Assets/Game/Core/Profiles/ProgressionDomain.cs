namespace NightSignal.Core.Profiles
{
    /// <summary>
    /// Which progression ledger a piece of state belongs to (Addendum 01 D05, §8.2). The two domains are deliberately
    /// separate: Online state is server-owned and authoritative; Local state is written by this PC for offline play and
    /// is NEVER uploaded, merged or accepted as Online wallet, course rights, rank, records, soundtrack or ownership.
    /// No type in this package converts Local state into anything shaped like an Online receipt.
    /// </summary>
    public enum ProgressionDomain
    {
        Online = 0,
        Local = 1,
    }

    /// <summary>Player-facing wording for the domain boundary (shown before the first local game and on domain switches).</summary>
    public static class DomainNotices
    {
        public const string LocalBadge = "Local / Offline";
        public const string OnlineBadge = "Online";

        public const string LocalSeparation =
            "Local / Offline progress is saved only on this PC. It is separate from your Online profile: credits, cars, " +
            "course access, records, rank and soundtrack unlocks earned offline stay in this Local profile and are never " +
            "uploaded or merged into Online play. This is a deliberate separation, not a lost save.";

        public static string Badge(ProgressionDomain domain) => domain == ProgressionDomain.Local ? LocalBadge : OnlineBadge;
    }
}
