using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;

namespace NightSignal.Core.Story
{
    /// <summary>
    /// Appendix E CH70 "The Other Side of the Card": read the six crew introductions in the race diary after encountering each
    /// crew, then complete one legal race against any crew member. An introduction opens in the diary when its crew's stage
    /// is cleared on Normal (the encounter); reading one is recorded by entry id ("crew:&lt;crew&gt;"); once all six are read,
    /// the next legal finish in a race with a crew member among the opponents completes the challenge. The same rules serve
    /// the control plane (settlement) and the Local profile.
    /// </summary>
    public static class DiaryChallenges
    {
        public const string OtherSideOfTheCard = "CH70";

        public static string CrewEntry(string crew) => "crew:" + (crew ?? "");

        /// <summary>Whether <paramref name="entry"/> is a crew introduction the player has opened (its stage cleared on Normal).</summary>
        public static bool Unlocked(string entry, IReadOnlyList<CrewIntroduction> crews, Func<string, bool> clearedOnNormal) =>
            crews != null && crews.Any(c => CrewEntry(c.Crew) == entry && clearedOnNormal(c.UnlockAfterStage));

        /// <summary>Every crew introduction has been read.</summary>
        public static bool AllCrewsRead(IEnumerable<string> read, IReadOnlyList<CrewIntroduction> crews)
        {
            var set = new HashSet<string>(read ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            return crews != null && crews.Count > 0 && crews.All(c => set.Contains(CrewEntry(c.Crew)));
        }

        /// <summary>A crew member (a rival of one of the diary's crews) was among the race's opponents.</summary>
        public static bool RacedCrewMember(IEnumerable<string> opponents, ContentCatalogue catalogue, IReadOnlyList<CrewIntroduction> crews)
        {
            if (opponents == null || catalogue == null || crews == null) return false;
            var names = new HashSet<string>(crews.Select(c => c.Crew), StringComparer.Ordinal);
            return opponents.Any(id => catalogue.TryRival(id, out RivalDef r) && names.Contains(r.Crew ?? ""));
        }
    }
}
