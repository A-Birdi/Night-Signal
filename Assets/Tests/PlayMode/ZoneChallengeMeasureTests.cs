using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.AI;
using NightSignal.Race;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Whether the challenge-zone predicates (CH17 C03 link corners, CH19 C05 demonstration zone, CH22 C09 outer clips, CH27
    /// C19 transitions to the bank gate) are reachable with legal inputs: the validator autopilot, told to hold one slide
    /// through each challenge's zones and to aim at clip lines (<see cref="OfflineRaceSession.AutopilotDrivesChallengeZones"/>),
    /// drives each course solo in a drift car at drift skills 0.95, 0.80 and 0.65; the game's own judge measures every chain
    /// and the race predicates say what a finish would grant. Reports to Evidence/challenges/zones.txt; publishes nothing (the
    /// predicates have no measured targets). Explicit. Automation, not a person.
    /// </summary>
    [Explicit("drives the challenge-zone courses with the autopilot")]
    public sealed class ZoneChallengeMeasureTests
    {
        const string ReportPath = "Evidence/challenges/zones.txt";

        /// <summary>The drift car the measurement drives: CH25's supplied loaner (V09 on T2 drift tyres).</summary>
        static readonly TrialLoaner Car = new TrialLoaner { Car = "V09", Parts = new Dictionary<string, string> { ["tyres"] = "TYR-T2-DRIFT" } };

        static readonly (string Course, string Challenge)[] Courses = { ("C03", "CH17"), ("C05", "CH19"), ("C09", "CH22"), ("C19", "CH27") };

        static IEnumerator Run(string course, CarDef car, ResolvedCarSpec spec, float skill, Action<string, bool> done)
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode($"Assets/Content/Courses/{course}/{course}.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("Editor-only scene loading");
#endif
            yield return null;
            var go = new GameObject("ZoneChallengeMeasure");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = car.Id;
            session.PlayerSpec = spec;
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            session.AutopilotDriftSkill = skill;
            OfflineRaceSession.AutopilotDrivesChallengeZones = true;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 10, CarCapPi = PerformanceIndex.Max };
            session.OpposingAi = new List<string>();
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - t0 < 600f) yield return null;
            OfflineRaceSession.AutopilotDrivesChallengeZones = false;
            RaceEntrant me = session.Player;
            RaceEntrantResult r = session.Results?.FirstOrDefault(x => x.Entrant.Human);
            string line = Describe(course, me, r, session.Pilot, out bool granted);
            UnityEngine.Object.Destroy(go);
            yield return null;
            done(line, granted);
        }

        /// <summary>The run's chain facts in words (shared with the built-player zone tour's log format).</summary>
        static string Describe(string course, RaceEntrant me, RaceEntrantResult r, RouteFollower pilot, out bool granted)
        {
            ZoneChainRun z = me?.ZoneChains;
            var sb = new StringBuilder();
            bool finished = r != null && r.Outcome == RunOutcome.Finished;
            sb.Append(finished ? $"finished {r.FinishTimeMicros / 1e6:F1} s" : $"not finished ({r?.Outcome})");
            if (me != null) sb.Append($"; walls {me.Progress.WallIncidents}, resets {me.Progress.Resets}");
            if (pilot != null)
                sb.Append($"; autopilot flicks {pilot.DriftFlicks}, holds {pilot.DriftHolds}, ended by edge {pilot.DriftEndEdge} / spin {pilot.DriftEndSpin} / slow {pilot.DriftEndSlow} / wrong way {pilot.DriftEndWrongWay}");
            granted = false;
            if (z == null) { sb.Append("; no zone facts"); return sb.ToString(); }
            sb.Append($"; chains with zones {z.Chains.Count}, chains lost {z.ChainsLost}");
            foreach (ZoneChain c in z.Chains)
                sb.Append($"\n    chain [{string.Join(", ", c.Zones.Select(i => z.Zones[i].Id))}] {c.Seconds:F1} s, {(c.Banked ? "banked" : "LOST")} ({c.End}{(c.BankGate != "" ? " at the " + c.BankGate + " gate" : "")}){(c.Touched ? ", barrier touched" : "")}");
            for (int i = 0; i < z.Zones.Count; i++)
                if (z.Zones[i].Kind == ChallengeZone.Demo) sb.Append($"\n    {z.Zones[i].Id}: longest 20–35° hold {z.LongestHold[i]:F2} s");
            List<string> grants = me.Progress.Finished ? Net.ChallengePredicates.Evaluate(course, me.Progress, me.Drift, "sprint", null, me.GateRun, null, z).ToList() : new List<string>();
            string challenge = Courses.First(c => c.Course == course).Challenge;
            granted = grants.Contains(challenge);
            sb.Append($"\n    predicates grant: {(grants.Count == 0 ? "none" : string.Join(", ", grants))}");
            return sb.ToString();
        }

        /// <summary>
        /// Diagnostics: one course with the autopilot's drift trace, over the slide knobs listed in Builds/diag/zone-knobs.txt
        /// ("slip rateSteer rateThrottle skill pathFollow pathThrottle entrySpeed grace clipInset" per line; the defaults when absent) — Builds/diag/zone-trace-&lt;course&gt;.txt, not evidence.
        /// </summary>
        [UnityTest, Timeout(1800000)]
        public IEnumerator TraceZoneDrift([Values("C05", "C03", "C09", "C19")] string course)
        {
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = lib.Catalogue;
            CarDef car = cat.Car(Car.Car);
            ResolveResult resolved = TrialLoaners.Resolve(Car, car, cat.CarTunings[car.Id], lib.Parts, out PiEstimate _);
            var knobs = new List<float[]>();
            if (File.Exists("Builds/diag/zone-knobs.txt"))
                foreach (string l in File.ReadAllLines("Builds/diag/zone-knobs.txt"))
                {
                    string[] p = l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 9) knobs.Add(p.Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
                }
            if (knobs.Count == 0)
                knobs.Add(new[] { OfflineRaceSession.ZoneSlideSlipDeg, OfflineRaceSession.ZoneSlideRateSteer, OfflineRaceSession.ZoneSlideRateThrottle, 0.8f,
                    OfflineRaceSession.ZoneSlidePathFollow, OfflineRaceSession.ZoneSlidePathThrottle, OfflineRaceSession.ZoneSlideEntrySpeed,
                    OfflineRaceSession.ZoneSlideTransitionGrace, OfflineRaceSession.ZoneSlideClipInset });
            float[] saved = { OfflineRaceSession.ZoneSlideSlipDeg, OfflineRaceSession.ZoneSlideRateSteer, OfflineRaceSession.ZoneSlideRateThrottle,
                OfflineRaceSession.ZoneSlidePathFollow, OfflineRaceSession.ZoneSlidePathThrottle, OfflineRaceSession.ZoneSlideEntrySpeed,
                OfflineRaceSession.ZoneSlideTransitionGrace, OfflineRaceSession.ZoneSlideClipInset };
            var summary = new StringBuilder();
            var traces = new StringBuilder();
            foreach (float[] k in knobs)
            {
                var trace = new StringBuilder();
#if UNITY_EDITOR
                yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode($"Assets/Content/Courses/{course}/{course}.unity",
                    new LoadSceneParameters(LoadSceneMode.Single));
#endif
                yield return null;
                var go = new GameObject("ZoneChallengeTrace");
                var session = go.AddComponent<OfflineRaceSession>();
                session.CarId = car.Id;
                session.PlayerSpec = resolved.Spec;
                session.Autopilot = true;
                session.Headless = true;
                session.SimulationSpeed = 30;
                session.AutopilotDriftSkill = k[3];
                OfflineRaceSession.AutopilotDrivesChallengeZones = true;
                OfflineRaceSession.ZoneSlideSlipDeg = k[0];
                OfflineRaceSession.ZoneSlideRateSteer = k[1];
                OfflineRaceSession.ZoneSlideRateThrottle = k[2];
                OfflineRaceSession.ZoneSlidePathFollow = k[4];
                OfflineRaceSession.ZoneSlidePathThrottle = k[5];
                OfflineRaceSession.ZoneSlideEntrySpeed = k[6];
                OfflineRaceSession.ZoneSlideTransitionGrace = k[7];
                OfflineRaceSession.ZoneSlideClipInset = k[8];
                session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 10, CarCapPi = PerformanceIndex.Max };
                session.OpposingAi = new List<string>();
                yield return null;
                float t0 = Time.realtimeSinceStartup;
                while (session.Pilot == null && Time.realtimeSinceStartup - t0 < 60f) yield return null;
                if (session.Pilot != null) session.Pilot.Trace = l => trace.AppendLine(l);
                while (session.Results == null && Time.realtimeSinceStartup - t0 < 600f) yield return null;
                OfflineRaceSession.AutopilotDrivesChallengeZones = false;
                string line = Describe(course, session.Player, session.Results?.FirstOrDefault(x => x.Entrant.Human), session.Pilot, out bool _);
                string head = $"### slip {k[0]} rateSteer {k[1]} rateThrottle {k[2]} skill {k[3]} pathFollow {k[4]} pathThrottle {k[5]} entry {k[6]} grace {k[7]} inset {k[8]}: {line}";
                summary.AppendLine(head);
                traces.AppendLine(head).Append(trace);
                UnityEngine.Object.Destroy(go);
                yield return null;
            }
            OfflineRaceSession.ZoneSlideSlipDeg = saved[0];
            OfflineRaceSession.ZoneSlideRateSteer = saved[1];
            OfflineRaceSession.ZoneSlideRateThrottle = saved[2];
            OfflineRaceSession.ZoneSlidePathFollow = saved[3];
            OfflineRaceSession.ZoneSlidePathThrottle = saved[4];
            OfflineRaceSession.ZoneSlideEntrySpeed = saved[5];
            OfflineRaceSession.ZoneSlideTransitionGrace = saved[6];
            OfflineRaceSession.ZoneSlideClipInset = saved[7];
            Directory.CreateDirectory("Builds/diag");
            File.WriteAllText($"Builds/diag/zone-trace-{course}.txt", summary + System.Environment.NewLine + traces);
        }

        [UnityTest, Timeout(3600000)]
        public IEnumerator MeasureZoneChallenges()
        {
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = lib.Catalogue;
            CarDef car = cat.Car(Car.Car);
            ResolveResult resolved = TrialLoaners.Resolve(Car, car, cat.CarTunings[car.Id], lib.Parts, out PiEstimate pi);
            Assert.That(resolved.Ok, "the drift car resolves");
            var report = new StringBuilder();
            report.AppendLine("# Challenge-zone chains measured by ZoneChallengeMeasureTests (PlayMode, explicit): solo, non-contact freeplay, the validator");
            report.AppendLine($"# autopilot holding one slide per challenge's zones (clip lines aimed at), {car.Name} on T2 drift tyres (PI {pi.Value}), drift skills 0.95 / 0.80 / 0.65.");
            report.AppendLine($"# Zone-tour slide settings (automation only; the AI and the published references keep the tuned controller): the zones' marked lines held,");
            report.AppendLine($"# clip lines {OfflineRaceSession.ZoneSlideClipInset} m inside the clip, entry {OfflineRaceSession.ZoneSlideEntrySpeed} m/s, target slip {OfflineRaceSession.ZoneSlideSlipDeg}°, slip-rate damping " +
                              $"{OfflineRaceSession.ZoneSlideRateSteer} / {OfflineRaceSession.ZoneSlideRateThrottle}, transition grace {OfflineRaceSession.ZoneSlideTransitionGrace} s.");
            report.AppendLine("# The game's ZoneChainJudge measures; ChallengePredicates says what the finish grants. Automation, not a person.");
            var reached = new List<string>();
            foreach ((string course, string challenge) in Courses)
            {
                report.AppendLine().AppendLine($"## {challenge} on {course}");
                foreach (float skill in new[] { 0.95f, 0.8f, 0.65f })
                {
                    string line = null;
                    bool granted = false;
                    yield return Run(course, car, resolved.Spec, skill, (l, g) => { line = l; granted = g; });
                    report.AppendLine($"- skill {skill:F2}: {line}");
                    Debug.Log($"[NightSignal.ZoneMeasure] {challenge} {course} skill {skill:F2}: {line}");
                    if (granted) { reached.Add(challenge); break; }
                }
            }
            report.AppendLine().AppendLine($"Reached by the autopilot: {(reached.Count == 0 ? "none" : string.Join(", ", reached))} of CH17, CH19, CH22, CH27.");
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, report.ToString().Replace("\r\n", "\n"));
            Debug.Log("[NightSignal.ZoneMeasure] " + report);
        }
    }
}
