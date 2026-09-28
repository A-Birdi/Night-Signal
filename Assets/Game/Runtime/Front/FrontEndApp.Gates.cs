using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Race;
using UnityEngine;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Challenge-gate evidence (<c>-nsGateTour</c>): offline freeplay races on C03 (CH03 apex gates), C05 (CH06 precision
        /// gates, no guardrail touch) and C13 (CH09 viaduct lanes with the 0.5 m barrier margin), each driven alone by the
        /// validator autopilot told to steer through the challenge gates. The game's own judge measures every crossing against
        /// the real course (barrier colliders included); the tour logs those facts and the challenges the race predicates
        /// grant, and checks each grant against the raw counts (every pass of every gate touched, no wall / guardrail touch,
        /// every lane pass with the margin kept) — whether the autopilot earns a challenge is its driving, whether the game
        /// grants exactly what the facts say is the check. At least one challenge must be granted. Automation, not a person.
        /// </summary>
        IEnumerator GateTour()
        {
            var failures = new List<string>();
            int grantedCount = 0;
            void Note(string n) => Debug.Log("[NightSignal.GateTour] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            OfflineRaceSession.AutopilotAimsChallengeGates = true;
            yield return new WaitForSeconds(3f);
            foreach ((string course, string challenge) in new[] { ("C03", "CH03"), ("C05", "CH06"), ("C13", "CH09") })
            {
                var free = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10 };
                List<RaceEntrantResult> results = null;
                StartCoroutine(RunOfflineRace(course, "V01", free, new List<string>(), false, (r, rev) => results = r));
                float until = Time.realtimeSinceStartup + 60f;
                while ((activeRace == null || activeRace.Phase != MatchPhase.Countdown) && Time.realtimeSinceStartup < until) yield return null;
                if (activeRace == null) { Fail($"{course}: the race did not start"); continue; }
                activeRace.Autopilot = true;
                OfflineRaceSession race = activeRace;
                until = Time.realtimeSinceStartup + 600f;
                while (results == null && Time.realtimeSinceStartup < until) yield return null;
                RaceEntrant me = race.Player;
                GateRun g = me?.GateRun;
                if (results == null || me == null || g == null) { Fail($"{course}: no finished run to judge"); continue; }
                RaceEntrantResult mine = results.FirstOrDefault(r => r.Entrant == me);
                var granted = Net.ChallengePredicates.Evaluate(course, me.Progress, me.Drift, "sprint", race.Rules.Surface, g).ToList();
                string touches = string.Join(", ", race.Sim.Gates.TouchGates.Select((x, i) => $"{x.Id} {g.Touches[i]}/{g.Passes[i]}"));
                string lanes = string.Join(", ", race.Sim.Gates.LaneZones.Select((x, i) => $"{x.Id} passes {g.LanePasses[i]} margin kept {g.LaneMarginKept[i]}"));
                Note($"{course}: {mine?.Outcome} in {(mine != null ? RaceClassification.ToReportedMillis(mine.FinishTimeMicros) / 1000.0 : 0):F1} s; " +
                     $"walls {me.Progress.WallIncidents}, resets {me.Progress.Resets}, guardrail-touch steps {g.BarrierTouchSteps}; " +
                     $"touch gates [{touches}]; lanes [{lanes}]; challenges granted: {string.Join(", ", granted)}");
                // What the raw counts imply, read independently of the predicates' helpers.
                var serving = Enumerable.Range(0, race.Sim.Gates.TouchGates.Count).Where(i => race.Sim.Gates.TouchGates[i].Challenge == challenge).ToList();
                var zones = Enumerable.Range(0, race.Sim.Gates.LaneZones.Count).Where(i => race.Sim.Gates.LaneZones[i].Challenge == challenge).ToList();
                bool gatesOk = serving.All(i => g.Passes[i] > 0 && g.Touches[i] == g.Passes[i]);
                bool implied = challenge == "CH03" ? serving.Count > 0 && gatesOk && me.Progress.WallIncidents == 0
                    : challenge == "CH06" ? serving.Count > 0 && gatesOk && g.BarrierTouchSteps == 0
                    : zones.Count > 0 && zones.All(i => g.LanePasses[i] > 0 && g.LaneMarginKept[i]);
                Note($"{course}: the facts {(implied ? "earn" : "do not earn")} {challenge}; the game {(granted.Contains(challenge) ? "granted" : "withheld")} it");
                if (mine == null || mine.Outcome != RunOutcome.Finished) Fail($"{course}: did not finish");
                else if (granted.Contains(challenge) != implied) Fail($"{course}: {challenge} {(implied ? "withheld" : "granted")} against the facts");
                if (granted.Contains(challenge)) grantedCount++;
                yield return new WaitForSeconds(3f);
            }
            OfflineRaceSession.AutopilotAimsChallengeGates = false;
            if (grantedCount == 0) Fail("no challenge was granted on any course");
            Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}
