using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
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
        public static IEnumerable<string> Evaluate(MatchAssignment a, EntrantProgress p, DriftScorer drift = null, string surface = null) =>
            Evaluate(a.CourseId, p, drift, a.FreeplayMode, surface);

        /// <summary>
        /// The same predicates for any race (online on the game server, offline in the Local race). <paramref name="drift"/>
        /// is the entrant's drift scorer (every race scores drift in its judged zones); <paramref name="freeplayMode"/> and
        /// <paramref name="surface"/> describe the event.
        /// </summary>
        public static IEnumerable<string> Evaluate(string courseId, EntrantProgress p, DriftScorer drift = null, string freeplayMode = null, string surface = null)
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
            if (drift == null) yield break;
            // CH16 First Arc: one banked chain of at least 8,000 raw on T00's drift route, and the run finished (T00 is the
            // tutorial course; its finish stands for completing the lesson until a lesson system exists).
            if (courseId == "T00" && drift.BankedChains.Any(c => c.Raw >= 8_000))
                yield return "CH16";
            // CH18 Two Clean Chains: on C04, two separately banked chains of at least 6,000 raw (a chain only banks after the
            // car straightens, so two banked chains are separated by a return to grip).
            if (courseId == "C04" && drift.BankedChains.Count(c => c.Raw >= 6_000) >= 2)
                yield return "CH18";
            // CH20 Forward Flow: 25,000 raw in C01's Drift Attack (the scorer counts forward progress only).
            if (courseId == "C01" && freeplayMode == "drift-attack" && drift.BankedRaw >= 25_000)
                yield return "CH20";
            // CH21 Wet Signal: 70,000 raw in C08's wet Drift Attack with at least two banked chains.
            if (courseId == "C08" && freeplayMode == "drift-attack" && surface == "wet" && drift.BankedRaw >= 70_000 && drift.ChainsBanked >= 2)
                yield return "CH21";
            // CH24 Sustained Arc: on C15, one banked chain of at least 60,000 raw that never slowed below 45 km/h while scoring.
            if (courseId == "C15" && drift.BankedChains.Any(c => c.Raw >= 60_000 && c.MinSpeedKmh >= 45f))
                yield return "CH24";
        }
    }
}
