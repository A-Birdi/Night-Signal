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
        public static IEnumerable<string> Evaluate(MatchAssignment a, EntrantProgress p)
        {
            if (!p.Finished) yield break;
            // CH01 First Clean Signal: finish C01 with no meaningful wall impacts and no reset.
            if (a.CourseId == "C01" && p.WallIncidents == 0 && p.Resets == 0)
                yield return "CH01";
            // CH05 No Recovery Needed: finish C04 without reset, wrong-way warning, or leaving the legal corridor.
            if (a.CourseId == "C04" && p.Resets == 0 && p.WrongWaySeconds <= 0f && p.OutOfCorridorSeconds <= 0f)
                yield return "CH05";
        }
    }
}
