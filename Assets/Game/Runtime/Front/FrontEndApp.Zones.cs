using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Race;
using UnityEngine;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Challenge-zone evidence (<c>-nsZoneTour</c>): offline freeplay races on C03 (CH17 link corners), C05 (CH19
        /// demonstration zone), C09 (CH22 outer clips) and C19 (CH27 transitions to the bank gate), each driven alone in the
        /// measured drift car (V09 on T2 drift tyres, CH25's loaner) by the validator autopilot holding one slide through the
        /// challenge's zones (<see cref="OfflineRaceSession.AutopilotDrivesChallengeZones"/>) at the drift skill the PlayMode
        /// measurement published. The game's own judge measures every chain against the real course; the tour logs the chains and
        /// holds and the challenges the race predicates grant, and checks each grant against the raw chain facts read
        /// independently of the predicates' helpers. At least one challenge must be granted. Automation, not a person.
        /// </summary>
        /// <summary>The zone tour's runs: each course, its challenge, and the drift skill ZoneChallengeMeasureTests found (or its middle one).</summary>
        static readonly (string Course, string Challenge, float Skill)[] ZoneTourRuns = { ("C03", "CH17", 0.8f), ("C05", "CH19", 0.95f), ("C09", "CH22", 0.8f), ("C19", "CH27", 0.8f) };

        IEnumerator ZoneTour()
        {
            var failures = new List<string>();
            int grantedCount = 0;
            void Note(string n) => Debug.Log("[NightSignal.ZoneTour] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            ContentLibrary lib = ContentLibrary.Load();
            CarDef car = lib.Catalogue.Car("V09");
            var loaner = new TrialLoaner { Car = "V09", Parts = new Dictionary<string, string> { ["tyres"] = "TYR-T2-DRIFT" } };
            ResolveResult resolved = TrialLoaners.Resolve(loaner, car, lib.Catalogue.CarTunings[car.Id], lib.Parts, out PiEstimate pi);
            if (!resolved.Ok) { Fail("the drift car does not resolve"); Application.Quit(1); yield break; }
            Note($"{car.Name} on T2 drift tyres, PI {pi.Value}");
            OfflineRaceSession.AutopilotDrivesChallengeZones = true;
            yield return new WaitForSeconds(3f);
            foreach ((string course, string challenge, float skill) in ZoneTourRuns)
            {
                var free = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 10 };
                List<RaceEntrantResult> results = null;
                pendingAutopilotDriftSkill = skill;
                StartCoroutine(RunOfflineRace(course, car.Id, free, new List<string>(), false, (r, rev) => results = r, resolved.Spec));
                float until = Time.realtimeSinceStartup + 60f;
                while ((activeRace == null || activeRace.Phase != MatchPhase.Countdown) && Time.realtimeSinceStartup < until) yield return null;
                if (activeRace == null) { Fail($"{course}: the race did not start"); continue; }
                activeRace.Autopilot = true;
                OfflineRaceSession race = activeRace;
                until = Time.realtimeSinceStartup + 900f;
                while (results == null && Time.realtimeSinceStartup < until) yield return null;
                RaceEntrant me = race.Player;
                ZoneChainRun z = me?.ZoneChains;
                if (results == null || me == null || z == null) { Fail($"{course}: no finished run to judge"); continue; }
                RaceEntrantResult mine = results.FirstOrDefault(r => r.Entrant == me);
                var granted = Net.ChallengePredicates.Evaluate(course, me.Progress, me.Drift, "sprint", race.Rules.Surface, me.GateRun, null, z).ToList();
                string chains = string.Join("; ", z.Chains.Select(c => $"[{string.Join(", ", c.Zones.Select(i => z.Zones[i].Id))}] {c.Seconds:F1} s " +
                    $"{(c.Banked ? "banked" : "lost")} ({c.End}{(c.BankGate != "" ? " at the " + c.BankGate + " gate" : "")}){(c.Touched ? " touched" : "")}"));
                string holds = string.Join(", ", Enumerable.Range(0, z.Zones.Count).Where(i => z.Zones[i].Kind == ChallengeZone.Demo).Select(i => $"{z.Zones[i].Id} {z.LongestHold[i]:F2} s"));
                Note($"{course}: {mine?.Outcome} in {(mine != null ? RaceClassification.ToReportedMillis(mine.FinishTimeMicros) / 1000.0 : 0):F1} s at skill {skill:F2}; " +
                     $"walls {me.Progress.WallIncidents}, resets {me.Progress.Resets}; chains {(chains == "" ? "none" : chains)}; lost {z.ChainsLost}" +
                     $"{(holds != "" ? "; longest 20–35° holds " + holds : "")}; challenges granted: {string.Join(", ", granted)}");
                // What the raw chain list implies, read independently of the predicates' helpers.
                List<int> serving = Enumerable.Range(0, z.Zones.Count).Where(i => z.Zones[i].Challenge == challenge).ToList();
                bool implied;
                if (challenge == "CH19")
                    implied = serving.Any(i => z.Zones[i].Kind == ChallengeZone.Demo && z.LongestHold[i] >= 3f);
                else
                {
                    int need = challenge == "CH17" ? 3 : serving.Count;
                    implied = serving.Count > 0 && z.Chains.Any(c => c.Banked
                        && c.Zones.Count(serving.Contains) >= need
                        && (challenge != "CH22" || !c.Touched)
                        && (challenge != "CH27" || (c.BankGate == "CH27" && c.Zones.Where(serving.Contains).Select(i => z.Zones[i].StartMetres).SequenceEqual(
                            c.Zones.Where(serving.Contains).Select(i => z.Zones[i].StartMetres).OrderBy(x => x)))));
                }
                bool finished = mine != null && mine.Outcome == RunOutcome.Finished;
                Note($"{course}: the facts {(implied && finished ? "earn" : "do not earn")} {challenge}; the game {(granted.Contains(challenge) ? "granted" : "withheld")} it");
                if (!finished) Fail($"{course}: did not finish");
                else if (granted.Contains(challenge) != implied) Fail($"{course}: {challenge} {(implied ? "withheld" : "granted")} against the facts");
                if (granted.Contains(challenge)) grantedCount++;
                yield return new WaitForSeconds(3f);
            }
            OfflineRaceSession.AutopilotDrivesChallengeZones = false;
            if (grantedCount == 0) Fail("no challenge was granted on any course");
            Note(failures.Count == 0 ? $"PASS ({grantedCount} of {ZoneTourRuns.Length} granted)" : "FAILED: " + string.Join("; ", failures));
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}
