using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Race;
using UnityEngine;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Racecraft evidence (<c>-nsRacecraftTour</c>): two offline freeplay sprints on C05, the player's V07 (PI 430) driven
        /// by the validator autopilot against AI held to PI 300 (their cars V02–V04). In the first (seven AI) it waits 8 s at
        /// GO so the field goes ahead, then races; in the second (five AI) it waits 2.5 s, then keeps about 1.5 s behind the
        /// car ahead. The game's own judge decides what counts (a clean pass: no touch 2 s either side, the place kept 3 s; a
        /// follow: the same moving car inside the 1–2 s interval); the tour logs those facts and every pass with the judge's
        /// reason, samples the HUD's gap line against the judge's interval, and checks that the predicates grant CH31 / CH32
        /// exactly when the facts say so. The follow run must earn CH32. A clean pass is not required: the autopilot is a poor
        /// overtaker (it queues on the narrow road and its passes are bumps; with quicker cars on C01 it never caught the
        /// field), so CH31's positive case rests on the EditMode judge tests. Automation, not a person.
        /// </summary>
        IEnumerator RacecraftTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "racecraft"));
            System.IO.Directory.CreateDirectory(dir);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.RacecraftTour] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            yield return new WaitForSeconds(3f);
            foreach ((string run, string course, string car, int cap, float hold, float follow, string wanted, int aiCount) in new[] { ("pass", "C05", "V07", 300, 8f, 0f, "", 7), ("follow", "C05", "V07", 300, 2.5f, 1.5f, "CH32", 5) })
            {
                OfflineRaceSession.AutopilotHoldSeconds = hold;
                OfflineRaceSession.AutopilotFollowSeconds = follow;
                var free = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10, CarCapPi = cap };
                var field = Enumerable.Range(1, aiCount).Select(i => $"ai-{i}").ToList();
                List<RaceEntrantResult> results = null;
                StartCoroutine(RunOfflineRace(course, car, free, field, false, (r, rev) => results = r));
                float until = Time.realtimeSinceStartup + 60f;
                while ((activeRace == null || activeRace.Phase != MatchPhase.Countdown) && Time.realtimeSinceStartup < until) yield return null;
                if (activeRace == null) { Fail($"{run}: the race did not start"); continue; }
                activeRace.Autopilot = true;
                OfflineRaceSession race = activeRace;
                // While racing: the HUD's gap line against the judge's interval, every half second; one screenshot in the window.
                int samples = 0, shown = 0, agree = 0, inWindow = 0;
                bool snapped = false;
                var trace = new List<string>();
                until = Time.realtimeSinceStartup + 600f;
                float nextSample = 0f;
                while (results == null && Time.realtimeSinceStartup < until)
                {
                    RacecraftRun rc = race.Player?.Racecraft;
                    if (rc != null && race.Phase == MatchPhase.Racing && Time.realtimeSinceStartup >= nextSample && !race.Player.Progress.Finished)
                    {
                        nextSample = Time.realtimeSinceStartup + 0.5f;
                        string hud = GameObject.Find("GapAhead")?.GetComponent<TMPro.TextMeshProUGUI>()?.text ?? "";
                        string plain = System.Text.RegularExpressions.Regex.Replace(hud, "<[^>]+>", "");
                        samples++;
                        if (rc.Ahead >= 0 && rc.Interval >= 0f)
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(plain, @"GAP AHEAD\s+([0-9.]+) s");
                            if (m.Success)
                            {
                                shown++;
                                if (m.Groups[1].Value == rc.Interval.ToString("0.0", CultureInfo.InvariantCulture)) agree++;
                            }
                            if (rc.Interval >= 1f && rc.Interval <= 2f)
                            {
                                inWindow++;
                                if (!snapped && rc.FollowLongest >= 3f)
                                {
                                    snapped = true;
                                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"{run}-gap-ahead.png"));
                                }
                            }
                        }
                        if (samples % 10 == 0 && trace.Count < 12)
                            trace.Add($"{race.Sim.RaceMicros(race.CurrentTick) / 1e6:F0}s: ahead {(rc.Ahead >= 0 ? race.Sim.Entrants[rc.Ahead].Roster.DisplayName : "none")} interval {rc.Interval:F2} / HUD \"{plain.Trim()}\"");
                    }
                    yield return null;
                }
                OfflineRaceSession.AutopilotHoldSeconds = OfflineRaceSession.AutopilotFollowSeconds = 0f;
                RaceEntrant me = race.Player;
                RacecraftRun r = me?.Racecraft;
                if (results == null || me == null || r == null) { Fail($"{run}: no finished run to judge"); continue; }
                RaceEntrantResult mine = results.FirstOrDefault(x => x.Entrant == me);
                var granted = Net.ChallengePredicates.Evaluate(course, me.Progress, me.Drift, "sprint", race.Rules.Surface, me.GateRun, r).ToList();
                string passes = string.Join(", ", r.CleanPasses.Select(p => $"{race.Sim.Entrants[p.Passed].Roster.DisplayName} at {p.Time:F1} s"));
                Note($"{run} ({course}, {car} against {aiCount} AI capped at PI {cap}): {mine?.Outcome} P{(mine != null ? mine.Placement : 0)} in {(mine != null ? RaceClassification.ToReportedMillis(mine.FinishTimeMicros) / 1000.0 : 0):F1} s; " +
                     $"car contacts {me.Progress.VehicleContacts}, walls {me.Progress.WallIncidents}, resets {me.Progress.Resets}; " +
                     $"clean passes [{passes}]; longest follow {r.FollowLongest:F1} s behind {(r.FollowLongestTarget >= 0 ? race.Sim.Entrants[r.FollowLongestTarget].Roster.DisplayName : "nobody")}; " +
                     $"challenges granted: {string.Join(", ", granted)}");
                foreach (string t in trace) Note($"{run}:   {t}");
                foreach (string l in r.PassLog)
                    Note($"{run}:   pass {System.Text.RegularExpressions.Regex.Replace(l, "#([0-9]+)", mm => race.Sim.Entrants[int.Parse(mm.Groups[1].Value)].Roster.DisplayName)}");
                Note($"{run}: HUD gap samples {samples}, with a car ahead in reach {shown}, HUD equal to the judge {agree}, inside 1–2 s {inWindow}");
                bool ch31 = r.CleanPasses.Count > 0, ch32 = course == "C05" && r.FollowLongest >= 8f;
                if (granted.Contains("CH31") != ch31) Fail($"{run}: CH31 {(ch31 ? "withheld" : "granted")} against the facts");
                if (granted.Contains("CH32") != ch32) Fail($"{run}: CH32 {(ch32 ? "withheld" : "granted")} against the facts");
                if (mine == null || mine.Outcome != RunOutcome.Finished) Fail($"{run}: did not finish");
                else if (wanted.Length > 0 && !granted.Contains(wanted)) Fail($"{run}: {wanted} was not earned");
                if (shown > 0 && agree != shown) Fail($"{run}: the HUD's gap differed from the judge's in {shown - agree} of {shown} samples");
                if (shown == 0) Fail($"{run}: the HUD never showed a gap");
                yield return new WaitForSeconds(3f);
            }
            Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}
