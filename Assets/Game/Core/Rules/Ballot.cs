using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    public sealed class BallotTally
    {
        public string CourseId;
        public int Votes;
        /// <summary>Chance of being drawn: Votes / total accepted ballots (one ticket per ballot, Addendum 01 D07).</summary>
        public double Chance;
    }

    /// <summary>
    /// Freeplay course vote (Addendum 01 §6.2, D07). One changeable ballot per active member; at the server deadline the
    /// ballots freeze. A draw picks ONE ACCEPTED BALLOT uniformly, so three of six votes give a course three of six
    /// chances. The server draws once, persists the result with the ballot revision, and every client animates towards
    /// that stored winner; presentation never decides it.
    /// </summary>
    public static class Ballot
    {
        public static bool ValidDuration(int seconds) => Array.IndexOf(Limits.BallotSecondsChoices, seconds) >= 0;

        /// <summary>Accepted ballots in canonical order (account ID ordinal) so a stored draw index is reproducible.</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> Canonical(IReadOnlyDictionary<string, string> ballotsByAccount) =>
            ballotsByAccount.Where(kv => !string.IsNullOrEmpty(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

        public static IReadOnlyList<BallotTally> Tally(IReadOnlyDictionary<string, string> ballotsByAccount)
        {
            IReadOnlyList<KeyValuePair<string, string>> accepted = Canonical(ballotsByAccount);
            int n = accepted.Count;
            return accepted.GroupBy(kv => kv.Value)
                .Select(g => new BallotTally { CourseId = g.Key, Votes = g.Count(), Chance = n == 0 ? 0 : (double)g.Count() / n })
                .OrderByDescending(t => t.Votes).ThenBy(t => t.CourseId, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Draws from frozen ballots using one server random value. Returns null when no ballots were cast ("No votes
        /// received": the leader selects directly; nothing is drawn from locked or unvoted routes).
        /// </summary>
        public static string Draw(IReadOnlyDictionary<string, string> frozenBallots, uint serverRandom, out int ballotIndex)
        {
            IReadOnlyList<KeyValuePair<string, string>> accepted = Canonical(frozenBallots);
            ballotIndex = -1;
            if (accepted.Count == 0) return null;
            ballotIndex = (int)(serverRandom % (uint)accepted.Count);
            return accepted[ballotIndex].Value;
        }
    }
}
