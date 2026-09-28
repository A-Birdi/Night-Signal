using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;

namespace NightSignal.Core.Rules
{
    /// <summary>
    /// Challenges earned across events rather than in one (Appendix E, touring family), judged from the set of courses the
    /// player has legally finished in the domain — the control plane from settled receipts, the Local profile from its
    /// records. CH66 Six Regions: a legal finish in each of the six regular regions. CH71 Every Road: T00's loop and every
    /// course C01–C25 (track ids, not variants).
    /// </summary>
    public static class CumulativeChallenges
    {
        public const string SixRegions = "CH66", EveryRoad = "CH71";
        public static readonly string[] RegularRegions = { "mizuhana", "kasumi", "kurogawa", "akebono", "hoshimi", "tsukishiro" };

        /// <summary>The cumulative challenges the finished-course set satisfies (whether or not they were granted before).</summary>
        public static List<string> Satisfied(ContentCatalogue catalogue, ICollection<string> finishedCourses)
        {
            var met = new List<string>();
            if (catalogue == null || finishedCourses == null) return met;
            var regions = new HashSet<string>();
            foreach (string id in finishedCourses)
                if (catalogue.TryCourse(id, out CourseDef c) && c.Kind == "regular" && c.Region != null) regions.Add(c.Region);
            if (RegularRegions.All(regions.Contains)) met.Add(SixRegions);
            bool every = finishedCourses.Contains("T00");
            for (int i = 1; i <= 25 && every; i++) every = finishedCourses.Contains($"C{i:00}");
            if (every) met.Add(EveryRoad);
            return met;
        }
    }
}
