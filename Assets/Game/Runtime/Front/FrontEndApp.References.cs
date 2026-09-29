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
        /// Challenge-reference evidence (<c>-nsReferenceTour</c>): offline freeplay races judged against the published
        /// references — C02 (CH04 exit floors), C08 (CH08 wet braking envelopes), C20 (CH12 late-braking windows), C11
        /// (CH10 equal splits inside the Silver time) with the validator autopilot in V01, and C24 (CH29) and a wet C12
        /// (CH26, the surface set by the tour) with the autopilot drifting in V04. For each, the facts the game measured are
        /// logged beside the published values, and the grant must be exactly what those facts imply (computed here, apart
        /// from the predicates). At least one grant is required. Automation, not a person.
        /// </summary>
        IEnumerator ReferenceTour()
        {
            var failures = new List<string>();
            int grantedCount = 0;
            void Note(string n) => Debug.Log("[NightSignal.ReferenceTour] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            ChallengeReferencesFile refs = NightSignal.Content.ContentLibrary.Load().Catalogue.ChallengeReferences;
            yield return new WaitForSeconds(3f);
            foreach ((string course, string challenge, string car, string surface, float drift) in new[]
                     {
                         ("C02", "CH04", "V01", (string)null, 0f), ("C08", "CH08", "V01", null, 0f), ("C20", "CH12", "V01", null, 0f),
                         ("C11", "CH10", "V01", null, 0f), ("C24", "CH29", "V04", null, 0.95f), ("C12", "CH26", "V04", "wet", 0.95f),
                     })
            {
                var rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 10, Surface = surface, DriftRanking = drift > 0f };
                pendingAutopilotDriftSkill = drift;
                List<RaceEntrantResult> results = null;
                StartCoroutine(RunOfflineRace(course, car, rules, new List<string>(), false, (r, rev) => results = r));
                float until = Time.realtimeSinceStartup + 60f;
                while ((activeRace == null || activeRace.Phase != MatchPhase.Countdown) && Time.realtimeSinceStartup < until) yield return null;
                if (activeRace == null) { Fail($"{course}: the race did not start"); continue; }
                activeRace.Autopilot = true;
                activeRace.SimulationSpeed = 8;
                OfflineRaceSession race = activeRace;
                until = Time.realtimeSinceStartup + 600f;
                while (results == null && Time.realtimeSinceStartup < until) yield return null;
                RaceEntrant me = race.Player;
                RaceEntrantResult mine = results?.FirstOrDefault(r => r.Entrant == me);
                if (me == null || mine == null) { Fail($"{course}: no run to judge"); continue; }
                GateRun g = me.GateRun;
                EntrantProgress p = me.Progress;
                var granted = Net.ChallengePredicates.Evaluate(course, p, me.Drift, "sprint", race.Rules.Surface, g).ToList();

                bool implied;
                string facts;
                if (challenge == "CH10")
                {
                    TimeReference t = refs.Times.First(x => x.Challenge == "CH10");
                    long raceMs = (p.FinishTimeMicros - p.PenaltyMicros) / 1000;
                    List<long> laps = p.LapMicros.Select(x => x / 1000).ToList();
                    implied = p.Finished && !p.CorridorCut && laps.Count >= 2 && laps.Max() - laps.Min() <= t.MaxLapDifferenceMs && raceMs <= t.ReferenceMs;
                    facts = $"{raceMs / 1000.0:F3} s (Silver {t.ReferenceMs / 1000.0:F1} s), laps {string.Join(" / ", laps.Select(l => (l / 1000.0).ToString("F3")))} " +
                            $"(allowed difference {t.MaxLapDifferenceMs / 1000.0:F1} s)";
                }
                else if (challenge == "CH29" || challenge == "CH26")
                {
                    DriftReference d = refs.Drift.First(x => x.Challenge == challenge);
                    double banked = me.Drift.BankedRaw, earned = me.Drift.EarnedRaw, lost = me.Drift.LostRaw;
                    bool surfaceOk = string.IsNullOrEmpty(d.Surface) || d.Surface == (surface ?? "dry");
                    implied = p.Finished && surfaceOk && banked >= d.Raw && (d.MaxLostFraction <= 0 || lost <= d.MaxLostFraction * earned)
                              && (challenge == "CH29" ? p.Resets == 0 : p.WallIncidents == 0);
                    facts = $"banked {banked:F0} raw (Gold {d.Raw:N0}), earned {earned:F0}, lost {lost:F0}; walls {p.WallIncidents}, resets {p.Resets}; surface {surface ?? "dry"}";
                }
                else
                {
                    List<GateSpeedReference> published = refs.Gates.Where(x => x.Challenge == challenge && x.Course == course).ToList();
                    bool all = published.Count > 0;
                    var parts = new List<string>();
                    foreach (GateSpeedReference r in published)
                    {
                        GateSpeedFact f = g?.SpeedFact(r.Gate) ?? default;
                        bool ok = f.Crossed && (r.Kind == "exit-speed" ? f.SpeedKmh >= r.MinKmh
                            : f.Braked && f.ExitKmh >= r.MinKmh && (r.MaxKmh <= 0f || f.ExitKmh <= r.MaxKmh) && f.ResetsInside == 0
                              && (r.BrakeByMetres <= 0f || (f.BrakeOnMetres >= 0f && f.BrakeOnMetres <= r.BrakeByMetres))
                              && (challenge != "CH12" || (f.WallsInside == 0 && f.ContactsInside == 0)));
                        all &= ok;
                        parts.Add(r.Kind == "exit-speed" ? $"{r.Gate} {f.SpeedKmh:F1} km/h (floor {r.MinKmh})"
                            : $"{r.Gate} out {f.ExitKmh:F1} km/h (window {r.MinKmh}–{r.MaxKmh}), braked {f.Braked} from {f.BrakeOnMetres:F0} m" +
                              (r.BrakeByMetres > 0f ? $" (by {r.BrakeByMetres})" : "") + $", walls {f.WallsInside}, contacts {f.ContactsInside}, resets {f.ResetsInside}");
                    }
                    implied = p.Finished && all && (challenge != "CH08" || p.WallIncidents == 0);
                    facts = string.Join("; ", parts) + $"; race walls {p.WallIncidents}";
                }
                Note($"{course} {challenge}: {mine.Outcome}; {facts}; granted [{string.Join(", ", granted)}]");
                Note($"{course}: the facts {(implied ? "earn" : "do not earn")} {challenge}; the game {(granted.Contains(challenge) ? "granted" : "withheld")} it");
                if (mine.Outcome != RunOutcome.Finished) Fail($"{course}: did not finish");
                else if (granted.Contains(challenge) != implied) Fail($"{course}: {challenge} {(implied ? "withheld" : "granted")} against the facts");
                if (granted.Contains(challenge)) grantedCount++;
                yield return new WaitForSeconds(2f);
            }
            if (grantedCount == 0) Fail("no challenge was granted on any course");
            Note(failures.Count == 0 ? $"PASS ({grantedCount} granted)" : "FAILED: " + string.Join("; ", failures));
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}
