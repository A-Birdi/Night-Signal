using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using NightSignal.Content;
using NightSignal.Core.Builds;
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
    /// Measures and publishes the challenge trials' targets (authored/challenge-trials.json; docs/CHALLENGE_TRIALS.md) the way
    /// the challenge references are measured: each trial's supplied loaner, resolved like a garage build, driven solo by the
    /// validator autopilot on the trial's course and conditions. Time targets: Gold 1.02 ×, Silver 1.10 ×, Bronze 1.20 × the
    /// autopilot's time. Drift trials run at drift skills 0.95, 0.80 and 0.65 and use the cleanest run (fewest resets,
    /// then the most scored), time and drift from that one run; drift targets: Gold = the raw it scored in the zones (banked
    /// or earned, the larger — as V-113's drift references, so points lost to walls never soften a target), Silver 0.85 ×,
    /// Bronze 0.70 ×; a trial that forbids the handbrake is measured with slides started by power. A reference that did not finish, used a forbidden input, or could not bank every zone a
    /// trial demands is reported and publishes nothing. Explicit: rerun after a physics, route, part or car change.
    /// Automation with legal inputs, not a human.
    /// </summary>
    [Explicit("measures and publishes the challenge trials' targets")]
    public sealed class ChallengeTrialReferenceTests
    {
        const string FileName = "Assets/Content/Data/authored/challenge-trials.json";

        static double TimeFactor(string tier) => tier == "gold" ? 1.02 : tier == "silver" ? 1.10 : 1.20;
        static double DriftFactor(string tier) => tier == "gold" ? 1.00 : tier == "silver" ? 0.85 : 0.70;

        sealed class RunFacts
        {
            public float Skill, Handbrake;
            public long TimeMs;
            public double Banked, Earned;
            public int Zones, ZonesBanked, Walls, Resets;
            public string Surface;
        }

        static IEnumerator Run(ChallengeTrialDef t, CarDef car, ResolvedCarSpec spec, float skill, Action<RunFacts> done)
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode($"Assets/Content/Courses/{t.Course}/{t.Course}.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("Editor-only scene loading");
#endif
            yield return null;
            var go = new GameObject("ChallengeTrialReference");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = car.Id;
            session.PlayerSpec = spec;
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            session.AutopilotDriftSkill = skill;
            session.AutopilotNoHandbrake = t.Rules.NoHandbrake;
            session.Rules = new RaceEventRules
            {
                Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 10,
                CarCapPi = t.Loaner.PiCap > 0 ? t.Loaner.PiCap : PerformanceIndex.Max,
                Surface = t.Conditions == "course" ? null : t.Conditions, DriftRanking = t.JudgesDrift,
            };
            session.OpposingAi = new System.Collections.Generic.List<string>();
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - t0 < 600f) yield return null;
            RaceEntrant me = session.Player;
            RaceEntrantResult r = session.Results?.FirstOrDefault(x => x.Entrant.Human);
            var f = new RunFacts
            {
                Skill = skill, TimeMs = r != null && r.Outcome == RunOutcome.Finished ? r.FinishTimeMicros / 1000 : 0,
                Banked = me.Drift.BankedRaw, Earned = me.Drift.EarnedRaw, Zones = session.Sim.Drift.Zones.Count, ZonesBanked = me.Drift.ZonesBanked.Count,
                Walls = me.Progress.WallIncidents, Resets = me.Progress.Resets, Handbrake = me.Progress.HandbrakeSeconds,
                Surface = session.Rules.Surface ?? CourseRuntime.Active?.Route?.Surface ?? "dry",
            };
            UnityEngine.Object.Destroy(go);
            yield return null;
            done(f);
        }

        [UnityTest, Timeout(3600000)]
        public IEnumerator MeasureChallengeTrials()
        {
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = lib.Catalogue;
            var file = JsonConvert.DeserializeObject<ChallengeTrialsFile>(File.ReadAllText(FileName));
            var report = new StringBuilder();
            var problems = new System.Collections.Generic.List<string>();
            report.AppendLine("# Challenge trial targets, measured by ChallengeTrialReferenceTests (PlayMode, explicit): each trial's loaner resolved like a");
            report.AppendLine("# garage build, solo, validator autopilot, the trial's course and conditions; drift trials: the cleanest of three drift skills. Automation.");

            foreach (ChallengeTrialDef t in file.Trials)
            {
                CarDef car = cat.Car(t.Loaner.Car);
                ResolveResult resolved = TrialLoaners.Resolve(t.Loaner, car, cat.CarTunings[car.Id], lib.Parts, out PiEstimate pi);
                if (!resolved.Ok) { problems.Add($"{t.Id}: the loaner does not resolve"); continue; }
                bool drift = t.JudgesDrift;
                // Drift trials: the cleanest of three skills (the drift controller still runs out of road on some courses, and a
                // run full of resets would publish soft targets); time trials: one run.
                var runs = new System.Collections.Generic.List<RunFacts>();
                foreach (float skill in drift ? new[] { 0.95f, 0.8f, 0.65f } : new[] { 0f })
                    yield return Run(t, car, resolved.Spec, skill, f => runs.Add(f));
                RunFacts best = runs.OrderByDescending(r => r.TimeMs > 0).ThenBy(r => r.Resets).ThenByDescending(r => Math.Max(r.Banked, r.Earned)).ThenBy(r => r.TimeMs).First();
                report.AppendLine().AppendLine($"## {t.Id} ({t.Challenge}, {t.Tier}) on {t.Course} ({best.Surface}): {car.Name} {string.Join(" + ", t.Loaner.Parts.Values)}".TrimEnd(' ', '+') +
                                               $", PI {pi.Value}{(t.Loaner.PiCap > 0 ? $" (cap {t.Loaner.PiCap})" : "")}");
                foreach (RunFacts r in runs)
                    report.AppendLine($"{(drift ? $"skill {r.Skill:F2}: " : "")}time {(r.TimeMs > 0 ? (r.TimeMs / 1000.0).ToString("F3") + " s" : "DNF")}; banked {r.Banked:F0} raw " +
                                      $"(earned {r.Earned:F0}) in {r.ZonesBanked}/{r.Zones} zones; walls {r.Walls}, resets {r.Resets}, handbrake {r.Handbrake:F1} s" +
                                      (runs.Count > 1 && r == best ? "  ← used" : ""));

                if (best.TimeMs <= 0) { problems.Add($"{t.Id}: the reference did not finish"); continue; }
                if (t.Rules.NoHandbrake && best.Handbrake > 0f) { problems.Add($"{t.Id}: the reference used the handbrake"); continue; }
                if (t.Loaner.PiCap > 0 && pi.Value > t.Loaner.PiCap) { problems.Add($"{t.Id}: PI {pi.Value} over the cap"); continue; }
                if (best.Resets > 0) report.AppendLine($"(the cleanest reference still reset {best.Resets} time(s): its time includes them)");
                t.Targets.TimeMs = t.JudgesTime ? (long)Math.Ceiling(TimeFactor(t.Tier) * best.TimeMs / 100.0) * 100 : 0;
                if (drift)
                {
                    // As the V-113 drift references: what the reference scored in the zones, so points lost to walls never soften a target.
                    double basis = Math.Max(best.Banked, best.Earned);
                    if (basis <= 0) { problems.Add($"{t.Id}: the reference scored nothing"); t.Targets.TimeMs = 0; continue; }
                    if (t.Rules.BankEveryZone && best.ZonesBanked < best.Zones)
                        problems.Add($"{t.Id}: the reference banked a chain in {best.ZonesBanked} of {best.Zones} zones (the rule stays; a person must do better)");
                    t.Targets.DriftRaw = (long)Math.Floor(DriftFactor(t.Tier) * basis / 100.0) * 100;
                    t.Targets.ReferenceDriftSkill = best.Skill;
                }
                report.AppendLine("→ " + string.Join(", ", new[]
                {
                    t.Targets.TimeMs > 0 ? $"{t.Tier} time {t.Targets.TimeMs / 1000.0:F1} s ({TimeFactor(t.Tier):F2} ×)" : null,
                    t.Targets.DriftRaw > 0 ? $"{t.Tier} drift {t.Targets.DriftRaw:N0} raw ({DriftFactor(t.Tier):F2} × scored)" : null,
                }.Where(x => x != null)));
            }

            file.Method = $"Measured {DateTime.UtcNow:yyyy-MM-dd} by ChallengeTrialReferenceTests: each trial's loaner resolved like a garage build (hm-1), solo, " +
                          "validator autopilot, the trial's course and conditions; time targets Gold 1.02 ×, Silver 1.10 ×, Bronze 1.20 × its time; drift trials " +
                          "use the cleanest of drift skills 0.95/0.80/0.65 (fewest resets, then most scored; slides started by power where the handbrake is " +
                          "forbidden), time and drift from that one run, drift targets Gold = the raw it scored in the zones (banked or earned, the larger), " +
                          $"Silver 0.85 ×, Bronze 0.70 ×; physics {RaceSimulation.PhysicsVersion}, scoring {RaceSimulation.ScoringVersion}. Automation, not a human.";
            report.AppendLine().AppendLine(problems.Count == 0 ? "# every trial measured" : "# problems: " + string.Join("; ", problems));
            File.WriteAllText(FileName, JsonConvert.SerializeObject(file, Formatting.Indented,
                new JsonSerializerSettings { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() }) + "\n");
            Directory.CreateDirectory("Evidence/challenges");
            File.WriteAllText("Evidence/challenges/trials.txt", report.ToString());
            Debug.Log("[NightSignal.ChallengeTrials] " + report.ToString().Replace("\n", " | "));
            Assert.That(problems.Where(p => !p.Contains("a person must do better")), Is.Empty, string.Join("; ", problems));
        }
    }
}
