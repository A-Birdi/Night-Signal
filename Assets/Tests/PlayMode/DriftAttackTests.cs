using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Race;
using NightSignal.Track;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Drift Attack on generated courses with judged drift zones: the validator autopilot and three AI drivers drift the
    /// zones on purpose, the shared race simulation scores every car with Core DriftScorer (banked raw score) and ranks the
    /// finishers by it. Evidence per course in Evidence/courses/drift/. Automation, not a human playtest.
    /// </summary>
    public sealed class DriftAttackTests
    {
        static IEnumerable<string> Courses() => new[] { "C01", "C08", "C12" };

        [UnityTest, Timeout(900000)]
        public IEnumerator DriftAttack_ScoresAndRanksByDrift([ValueSource(nameof(Courses))] string course)
        {
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                $"Assets/Content/Courses/{course}/{course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#else
            Assert.Ignore("Editor-only scene loading");
            yield break;
#endif
            yield return null;
            Assert.That(CourseRuntime.Active, Is.Not.Null, "course generated");

            var go = new GameObject("DriftSession");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = "V04"; // rear-drive, light: a natural drifter
            session.Autopilot = true;
            session.SimulationSpeed = 30;
            session.Rules = new RaceEventRules { Kind = "freeplay", DriftRanking = true, Surface = CourseRuntime.Active.Route?.Surface ?? "dry" };
            session.OpposingAi = new List<string> { "d1", "d2", "d3" };
            yield return null;
            Assert.That(session.Ready, Is.True);
            Assert.That(session.Sim.Drift.Zones.Count, Is.GreaterThan(0), $"{course} has judged drift zones");

            float realStart = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - realStart < 600f) yield return null;
            Assert.That(session.Results, Is.Not.Null, $"{course}: the event completed");

            var evidence = new DriftEvidence
            {
                course = course, zones = session.Sim.Drift.Zones.Count,
                driver = "RouteFollower drift mode (autopilot + AI) — scripted, not a human", unityVersion = Application.unityVersion,
            };
            foreach (RaceEntrantResult r in session.Results.OrderBy(x => x.Placement == 0 ? int.MaxValue : x.Placement))
            {
                RaceEntrant e = r.Entrant;
                evidence.entrants.Add(new DriftEntrant
                {
                    id = e.Roster.EntrantId, car = e.Roster.CarId, human = e.Human, outcome = r.Outcome.ToString(), placement = r.Placement,
                    rawDriftScore = r.RawDriftScore, chainsBanked = e.Drift.ChainsBanked, lostRaw = (long)e.Drift.LostRaw,
                    finishSeconds = r.FinishTimeMicros / 1e6f, resets = e.Progress.Resets + e.AutoRecoveries, wallIncidents = e.Progress.WallIncidents,
                    endMetres = e.Progress.Location.Distance, endSpeedKmh = e.State.Velocity.magnitude * 3.6f, inCorridor = e.Progress.Location.InCorridor,
                    wrongWaySeconds = e.Progress.WrongWaySeconds,
                });
            }
            Directory.CreateDirectory("Evidence/courses/drift");
            File.WriteAllText($"Evidence/courses/drift/{course}.json", JsonUtility.ToJson(evidence, true));
            Debug.Log($"[NightSignal.DriftTest] {course}: " + string.Join("; ", evidence.entrants.Select(x =>
                $"P{x.placement} {x.id} {x.car} {x.outcome} {x.rawDriftScore:N0} pts ({x.chainsBanked} chains, lost {x.lostRaw:N0}, resets {x.resets}, walls {x.wallIncidents}) {x.finishSeconds:F1}s, ended at {x.endMetres:F0} m {x.endSpeedKmh:F0} km/h{(x.inCorridor ? "" : " off-corridor")}")));
            Object.Destroy(go);

            List<DriftEntrant> finishers = evidence.entrants.Where(x => x.outcome == "Finished").ToList();
            Assert.That(finishers.Count, Is.EqualTo(4), $"{course}: every car finished");
            Assert.That(finishers.Count(x => x.rawDriftScore > 0), Is.GreaterThanOrEqualTo(3), $"{course}: most cars scored drift points");
            for (int i = 1; i < finishers.Count; i++)
                Assert.That(finishers[i].rawDriftScore, Is.LessThanOrEqualTo(finishers[i - 1].rawDriftScore), "finishers ranked by raw drift score");
        }

        [System.Serializable]
        sealed class DriftEvidence
        {
            public string course, driver, unityVersion;
            public int zones;
            public List<DriftEntrant> entrants = new List<DriftEntrant>();
        }

        [System.Serializable]
        sealed class DriftEntrant
        {
            public string id, car, outcome;
            public bool human;
            public int placement, chainsBanked, resets, wallIncidents;
            public long rawDriftScore, lostRaw;
            public float finishSeconds, endMetres, endSpeedKmh, wrongWaySeconds;
            public bool inCorridor;
        }
    }
}
