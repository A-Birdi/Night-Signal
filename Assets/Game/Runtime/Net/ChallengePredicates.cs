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
        /// <summary>The published challenge references in this build's content (hashed, so client and server agree).</summary>
        static ChallengeReferencesFile References => NightSignal.Content.ContentLibrary.Load()?.Catalogue?.ChallengeReferences;

        public static IEnumerable<string> Evaluate(MatchAssignment a, EntrantProgress p, DriftScorer drift = null, string surface = null, GateRun gates = null,
            RacecraftRun racecraft = null) =>
            Evaluate(a.CourseId, p, drift, a.FreeplayMode, surface, gates, racecraft);

        /// <summary>
        /// The same predicates for any race (online on the game server, offline in the Local race). <paramref name="drift"/>
        /// is the entrant's drift scorer (every race scores drift in its judged zones); <paramref name="freeplayMode"/> and
        /// <paramref name="surface"/> describe the event; <paramref name="racecraft"/> is null outside races with live opponents.
        /// </summary>
        public static IEnumerable<string> Evaluate(string courseId, EntrantProgress p, DriftScorer drift = null, string freeplayMode = null, string surface = null,
            GateRun gates = null, RacecraftRun racecraft = null)
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
            if (racecraft != null)
            {
                // CH31 Clean Pass: a live, moving car passed with no touch in the 2 s either side, the place kept 3 s.
                if (racecraft.CleanPasses.Count > 0) yield return "CH31";
                // CH32 Patient Mirror: on C05, 8 s behind the same moving car inside the 1–2 s interval, no touch.
                if (courseId == "C05" && racecraft.FollowLongest >= 8f) yield return "CH32";
            }
            if (gates != null)
            {
                // CH03 Apex Appointment: C03's three designated apex gates, no wall incident.
                if (courseId == "C03" && gates.AllTouched("CH03") && p.WallIncidents == 0) yield return "CH03";
                // CH06 Cedar Accuracy: C05's six alternating precision gates, and not a single guardrail touch.
                if (courseId == "C05" && gates.AllTouched("CH06") && gates.BarrierTouchSteps == 0) yield return "CH06";
                // CH09 Bridge Margin: both C13 viaduct lane zones crossed at least 0.5 m from the barriers throughout.
                if (courseId == "C13" && gates.LanesKept("CH09")) yield return "CH09";
                // Against the published references (authored/challenge-references.json).
                ChallengeReferencesFile refs = References;
                // CH04 The Exit Matters: C02's three uphill exit gates above their Bronze floors.
                if (courseId == "C02" && ChallengeReferenceJudge.GatesPassed(refs, "CH04", courseId, gates.SpeedFact)) yield return "CH04";
                // CH08 Wet Window: C08's two wet braking zones inside their speed/position envelopes, no wall incident.
                if (courseId == "C08" && p.WallIncidents == 0 && ChallengeReferenceJudge.GatesPassed(refs, "CH08", courseId, gates.SpeedFact)) yield return "CH08";
                // CH12 The Last Ten Metres: C20's four late-braking gates inside their windows, no collision or reset in them.
                if (courseId == "C20" && ChallengeReferenceJudge.GatesPassed(refs, "CH12", courseId, gates.SpeedFact, contactFree: true)) yield return "CH12";
            }
            // CH10 Equal Splits: C11's two laps within 2.0 s of each other, both legal, inside the Silver reference time.
            if (courseId == "C11" && !p.CorridorCut && ChallengeReferenceJudge.EqualSplits(References, "CH10", courseId, p.LapMicros, p.FinishTimeMicros - p.PenaltyMicros))
                yield return "CH10";
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
            // CH26 Needle in the Rain: beat the fixed wet C12 Gold drift reference, no meaningful wall impact.
            if (courseId == "C12" && p.WallIncidents == 0 && ChallengeReferenceJudge.BeatsDrift(References, "CH26", courseId, surface, drift.BankedRaw, drift.EarnedRaw, drift.LostRaw))
                yield return "CH26";
            // CH29 No Wasted Motion: bank the C24 Gold raw target losing at most 5 % of the earned raw, no reset.
            if (courseId == "C24" && p.Resets == 0 && ChallengeReferenceJudge.BeatsDrift(References, "CH29", courseId, surface, drift.BankedRaw, drift.EarnedRaw, drift.LostRaw))
                yield return "CH29";
        }
    }
}
