using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Measures and publishes the challenge references (authored/challenge-references.json, night-signal/challenge-references@1)
    /// the way the benchmark certification measures stage targets: the three starters (V01, V02, V03, stock) driven solo by
    /// the validator autopilot on each course's own conditions, read at the route's challenge gates.
    /// <list type="bullet">
    /// <item>CH04 (C02): each uphill exit gate's Bronze floor = 0.95 × the slowest starter's crossing speed.</item>
    /// <item>CH08 (C08, wet) and CH12 (C20): each braking zone's exit-speed window = 0.85 × the lowest … 1.15 × the highest
    /// starter exit speed; CH08 also publishes a brake-on point 15 m past the latest starter's (its "position envelope":
    /// the starters brake to the zone's end, so a release point cannot be published).</item>
    /// <item>CH10 (C11, two laps): Silver = 1.10 × the slowest starter's time, laps within 2.0 s (the spec).</item>
    /// <item>CH26 (C12, wet) and CH29 (C24): Gold raw = the raw the autopilot drifting at skill 0.95 in V04 earned in the zones (the car the
    /// drift-skill measurements use; CH29 also allows 5 % of the earned raw lost, from the spec).</item>
    /// </list>
    /// A reference that did not brake in a zone, or did not finish, is reported. Explicit: rerun after a physics, route or
    /// car change. Automation with legal inputs, not a human.
    /// </summary>
    [Explicit("measures and publishes the challenge references")]
    public sealed class ChallengeReferenceTests
    {
        const string FileName = "Assets/Content/Data/authored/challenge-references.json";
        static readonly string[] Starters = { "V01", "V02", "V03" };

        sealed class RunFacts
        {
            public string Car;
            public long FinishMs;
            public List<long> LapMs = new List<long>();
            public Dictionary<string, GateSpeedFact> Gates = new Dictionary<string, GateSpeedFact>();
            public double Banked, Earned, Lost;
            public int Walls, Resets;
        }

        static IEnumerator Run(string course, string car, string surface, float driftSkill, Action<RunFacts> done)
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode($"Assets/Content/Courses/{course}/{course}.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("Editor-only scene loading");
#endif
            yield return null;
            var go = new GameObject("ChallengeReference");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = car;
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            session.AutopilotDriftSkill = driftSkill;
            session.Rules = new RaceEventRules
            {
                Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 10, CarCapPi = PerformanceIndex.Max, Surface = surface, DriftRanking = driftSkill > 0f,
            };
            session.OpposingAi = new List<string>();
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - t0 < 400f) yield return null;
            RaceEntrant me = session.Player;
            var f = new RunFacts { Car = car };
            RaceEntrantResult r = session.Results?.FirstOrDefault(x => x.Entrant.Human);
            if (r != null && r.Outcome == RunOutcome.Finished) f.FinishMs = (r.FinishTimeMicros - me.Progress.PenaltyMicros) / 1000;
            f.LapMs.AddRange(me.Progress.LapMicros.Select(x => x / 1000));
            if (me.GateRun != null)
                for (int i = 0; i < me.GateRun.SpeedGateIds.Length; i++) f.Gates[me.GateRun.SpeedGateIds[i]] = me.GateRun.Speed[i];
            f.Banked = me.Drift.BankedRaw;
            f.Earned = me.Drift.EarnedRaw;
            f.Lost = me.Drift.LostRaw;
            f.Walls = me.Progress.WallIncidents;
            f.Resets = me.Progress.Resets;
            UnityEngine.Object.Destroy(go);
            yield return null;
            done(f);
        }

        [UnityTest, Timeout(3600000)]
        public IEnumerator MeasureChallengeReferences()
        {
            ContentCatalogue cat = ContentLibrary.Load().Catalogue;
            var file = new ChallengeReferencesFile { Schema = "night-signal/challenge-references@1" };
            var report = new StringBuilder();
            var problems = new List<string>();
            report.AppendLine("# Challenge references (Appendix E), measured by ChallengeReferenceTests (PlayMode, explicit): the three starters stock, solo,");
            report.AppendLine("# validator autopilot, each course's own conditions; drift references by the autopilot drifting (skill 0.95) in V04. Automation.");

            foreach ((string course, string challenge, bool release) in new[] { ("C02", "CH04", false), ("C08", "CH08", true), ("C20", "CH12", false) })
            {
                var runs = new List<RunFacts>();
                foreach (string car in Starters) yield return Run(course, car, null, 0f, f => runs.Add(f));
                TrackData track = CourseRuntime.Active.Track;
                report.AppendLine().AppendLine($"## {challenge} on {course} ({CourseRuntime.Active.Route?.Surface ?? "dry"})");
                foreach (RouteGateDef g in track.Gates.Where(x => x.Challenge == challenge).OrderBy(x => x.StartMetres))
                {
                    List<GateSpeedFact> seen = runs.Where(r => r.Gates.ContainsKey(g.Id) && r.Gates[g.Id].Crossed).Select(r => r.Gates[g.Id]).ToList();
                    string per = string.Join("; ", runs.Select(r => r.Gates.TryGetValue(g.Id, out GateSpeedFact s)
                        ? $"{r.Car} {(g.Kind == "exit-speed" ? s.SpeedKmh.ToString("F1") + " km/h" : $"in {s.EntryKmh:F1} out {s.ExitKmh:F1} km/h, brake on at {s.BrakeOnMetres:F0} m{(s.ReleaseMetres < 0f ? ", braking to the end" : $", released at {s.ReleaseMetres:F0} m")}")}"
                        : $"{r.Car} —"));
                    if (seen.Count < Starters.Length) { problems.Add($"{course} {g.Id}: not every starter crossed it"); report.AppendLine($"{g.Id}: {per} — INCOMPLETE"); continue; }
                    var reference = new GateSpeedReference { Challenge = challenge, Course = course, Gate = g.Id, Kind = g.Kind };
                    if (g.Kind == "exit-speed") reference.MinKmh = (float)Math.Round(0.95 * seen.Min(s => s.SpeedKmh), 1);
                    else
                    {
                        if (seen.Any(s => !s.Braked)) problems.Add($"{course} {g.Id}: a starter did not brake in the zone");
                        reference.MinKmh = (float)Math.Round(0.85 * seen.Min(s => s.ExitKmh), 1);
                        reference.MaxKmh = (float)Math.Round(1.15 * seen.Max(s => s.ExitKmh), 1);
                        if (release) reference.BrakeByMetres = (float)Math.Round(seen.Max(s => s.BrakeOnMetres) + 15f, 0);
                    }
                    file.Gates.Add(reference);
                    report.AppendLine($"{g.Id} ({g.Kind}, {g.StartMetres:F0}–{g.EndMetres:F0} m): {per} → " + (g.Kind == "exit-speed" ? $"floor {reference.MinKmh} km/h"
                        : $"window {reference.MinKmh}–{reference.MaxKmh} km/h" + (reference.BrakeByMetres > 0f ? $", brake on by {reference.BrakeByMetres} m" : "")));
                }
            }

            {
                var runs = new List<RunFacts>();
                foreach (string car in Starters) yield return Run("C11", car, null, 0f, f => runs.Add(f));
                report.AppendLine().AppendLine("## CH10 on C11 (two laps)");
                foreach (RunFacts r in runs) report.AppendLine($"{r.Car}: {r.FinishMs / 1000.0:F3} s, laps {string.Join(" / ", r.LapMs.Select(l => (l / 1000.0).ToString("F3")))}");
                if (runs.Any(r => r.FinishMs <= 0)) problems.Add("C11: a starter did not finish");
                else
                {
                    long silver = (long)Math.Ceiling(1.10 * runs.Max(r => r.FinishMs) / 100.0) * 100;
                    file.Times.Add(new TimeReference { Challenge = "CH10", Course = "C11", Tier = "silver", ReferenceMs = silver, MaxLapDifferenceMs = 2000 });
                    report.AppendLine($"→ Silver {silver / 1000.0:F1} s (1.10 × the slowest), laps within 2.0 s");
                }
            }

            foreach ((string course, string challenge, string surface, double lost) in new[] { ("C12", "CH26", "wet", 0.0), ("C24", "CH29", "", 0.05) })
            {
                RunFacts d = null;
                yield return Run(course, "V04", string.IsNullOrEmpty(surface) ? null : surface, 0.95f, f => d = f);
                report.AppendLine().AppendLine($"## {challenge} on {course}{(string.IsNullOrEmpty(surface) ? "" : " (" + surface + ")")}: V04 drifting, skill 0.95");
                report.AppendLine($"banked {d.Banked:F0} raw, earned {d.Earned:F0}, lost {d.Lost:F0}; walls {d.Walls}, resets {d.Resets}, finished {d.FinishMs > 0}");
                if (d.FinishMs <= 0 || d.Banked <= 0) { problems.Add($"{course}: the drift reference did not finish or bank"); continue; }
                // Gold = what the reference scored in the zones (its earned raw): a messy reference that lost chains to walls must
                // not publish a soft target.
                long gold = (long)Math.Floor(Math.Max(d.Banked, d.Earned) / 100.0) * 100;
                file.Drift.Add(new DriftReference { Challenge = challenge, Course = course, Surface = surface, Tier = "gold", Raw = gold, MaxLostFraction = lost });
                report.AppendLine($"→ Gold {gold:N0} raw" + (lost > 0 ? $", at most {lost:P0} of the earned raw lost" : ""));
            }

            file.Method = $"Measured {DateTime.UtcNow:yyyy-MM-dd} by ChallengeReferenceTests: starters V01/V02/V03 stock, solo, validator autopilot, each course's own " +
                          $"conditions; exit floors 0.95 × the slowest; braking windows 0.85 × lowest … 1.15 × highest exit speed (CH08 braking begun 15 m past the " +
                          $"latest brake-on point); CH10 Silver 1.10 × the slowest; drift Gold = the raw the V04 autopilot earned at drift skill 0.95; physics {RaceSimulation.PhysicsVersion}, " +
                          $"scoring {RaceSimulation.ScoringVersion}. Automation, not a human.";
            report.AppendLine().AppendLine(problems.Count == 0 ? "# every reference measured" : "# problems: " + string.Join("; ", problems));
            File.WriteAllText(FileName, JsonConvert.SerializeObject(file, Formatting.Indented,
                new JsonSerializerSettings { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() }));
            Directory.CreateDirectory("Evidence/challenges");
            File.WriteAllText("Evidence/challenges/references.txt", report.ToString());
            Debug.Log("[NightSignal.ChallengeReferences] " + report.ToString().Replace("\n", " | "));
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }
    }
}
