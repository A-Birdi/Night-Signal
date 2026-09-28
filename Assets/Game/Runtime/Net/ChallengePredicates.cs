using System.Collections.Generic;
using NightSignal.Race;

namespace NightSignal.Net
{
    /// <summary>
    /// Server-evaluated challenge predicates (spec §11, Appendix E) from authoritative race facts. The control plane
    /// accepts these only for legal finishes. Implemented so far: predicates expressible with current telemetry;
    /// the rest are tracked in REQUIREMENTS (R11.3) until their gates/metrics exist.
    /// </summary>
    public static class ChallengePredicates
    {
        public static IEnumerable<string> Evaluate(MatchAssignment a, EntrantProgress p) => Evaluate(a.CourseId, p);

        /// <summary>The same predicates for any race (online on the game server, offline in the Local race).</summary>
        public static IEnumerable<string> Evaluate(string courseId, EntrantProgress p)
        {
            if (!p.Finished) yield break;
            // CH01 First Clean Signal: finish C01 with no meaningful wall impacts and no reset.
            if (courseId == "C01" && p.WallIncidents == 0 && p.Resets == 0)
                yield return "CH01";
            // CH05 No Recovery Needed: finish C04 without reset, wrong-way warning, or leaving the legal corridor.
            if (courseId == "C04" && p.Resets == 0 && p.WrongWaySeconds <= 0f && p.OutOfCorridorSeconds <= 0f)
                yield return "CH05";
            // CH33 Clean Opening: the first sector with no wall impact, reset or off-course excursion (inputs before GO are
            // ignored, so there is no false start to penalise), then finish.
            if (p.FirstSectorJudged && p.FirstSectorClean)
                yield return "CH33";
            // CH35 One Reset, Then Clean: exactly one permitted reset, no meaningful wall impact after it, every checkpoint legal.
            if (p.Resets == 1 && p.WallsAtFirstReset >= 0 && p.WallIncidents == p.WallsAtFirstReset && !p.CorridorCut)
                yield return "CH35";
        }
    }
}
