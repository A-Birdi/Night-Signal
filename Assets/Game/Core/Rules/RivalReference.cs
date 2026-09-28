using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;

namespace NightSignal.Core.Rules
{
    /// <summary>
    /// The authored rival reference ghost for a course (spec §8 "an authored rival reference"): the Normal lead of the first
    /// stage raced there — a finale-only rival is never a replay target (<see cref="FinalRivals"/>), so a finale course takes
    /// that stage's first allowed support instead; the Freeplay venues take the four lieutenants in order; the tutorial
    /// loop has none. The ghost itself is a recorded solo run of that rival under Time Attack rules.
    /// </summary>
    public static class RivalReference
    {
        public const string Provenance = "authored-rival-reference";

        /// <summary>The rival whose reference ghost belongs to the course, or null.</summary>
        public static RivalDef For(ContentCatalogue catalogue, string courseId)
        {
            if (catalogue == null || !catalogue.TryCourse(courseId ?? "", out CourseDef course)) return null;
            foreach (StageDef stage in catalogue.Stages.Where(s => s.Course == courseId).OrderBy(s => s.Number))
            {
                IEnumerable<string> candidates = new[] { stage.Normal?.Lead }.Concat(stage.Normal?.Support ?? new List<string>());
                string id = candidates.FirstOrDefault(r => !string.IsNullOrEmpty(r) && FinalRivals.Allowed(r, AiPlacementContext.ReplayTarget));
                if (id != null && catalogue.TryRival(id, out RivalDef rival)) return rival;
            }
            if (course.Kind == "freeplay")
            {
                List<RivalDef> lieutenants = catalogue.Rivals.Where(r => r.Role == "lieutenant").OrderBy(r => r.Id, System.StringComparer.Ordinal).ToList();
                List<string> venues = catalogue.Courses.Where(c => c.Kind == "freeplay").Select(c => c.Id).OrderBy(x => x, System.StringComparer.Ordinal).ToList();
                int i = venues.IndexOf(courseId);
                if (lieutenants.Count > 0 && i >= 0) return lieutenants[i % lieutenants.Count];
            }
            return null;
        }
    }
}
