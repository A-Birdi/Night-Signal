using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Full twelve-car grid with light contact on C01 through the shared RaceSimulation (the same code the dedicated
    /// server runs), fast-forwarded with the scripted autopilot in the human seat. Evidence:
    /// Evidence/courses/C01-12car-contact.json. Proves the start layout, bounded contact and AI traffic handling at the
    /// largest allowed grid in-process; it is not a network run and not a human playtest.
    /// </summary>
    [ResetAutomationStatics]
    public sealed class FullGridContactTests
    {
        [UnityTest, Timeout(900000)]
        public IEnumerator C01_TwelveCarLightContactRace_CompletesWithBoundedContact()
        {
            yield return LoadCourse("C01");
            var go = new GameObject("OfflineRace");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = "V01";
            session.Autopilot = true;
            session.SimulationSpeed = 20;
            session.Headless = true;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10 };
            session.OpposingAi = new List<string> { "R01", "R02", "R03", "R05", "R06", "R07", "R09", "ai-8", "ai-9", "ai-10", "ai-11" };
            yield return null;
            Assert.That(session.Ready, Is.True);
            Assert.That(session.Sim.Entrants.Count, Is.EqualTo(Limits.MaxRaceVehicles));

            float realStart = Time.realtimeSinceStartup;
            while (session.Phase != MatchPhase.Results && Time.realtimeSinceStartup - realStart < 600f)
                yield return null;

            Assert.That(session.Phase, Is.EqualTo(MatchPhase.Results), "race did not complete");
            List<RaceEntrantResult> results = session.Results;
            int finished = results.Count(r => r.Outcome == RunOutcome.Finished);
            var evidence = new GridEvidence
            {
                course = "C01", vehicles = results.Count, humans = 1, contact = "light-contact",
                driver = "RouteFollower autopilot in the human seat + 11 server-style AI — scripted, not a human; in-process, not networked",
                finished = finished, dnf = results.Count(r => r.Outcome == RunOutcome.DidNotFinish),
                totalVehicleContacts = session.Sim.Entrants.Sum(e => e.Progress.VehicleContacts),
                maxVehicleContactsOneCar = session.Sim.Entrants.Max(e => e.Progress.VehicleContacts),
                totalWallIncidents = session.Sim.Entrants.Sum(e => e.Progress.WallIncidents),
                totalResets = session.Sim.Entrants.Sum(e => e.Progress.Resets),
                corridorCuts = session.Sim.Entrants.Count(e => e.Progress.CorridorCut),
                maxVerticalSpeed = session.MaxVerticalSpeed, anyNonFinite = session.AnyNonFinite,
                tickMsAverage = (float)session.TickMsAverage, tickMsMax = (float)session.TickMsMax,
                winnerSeconds = results.Where(r => r.Outcome == RunOutcome.Finished).Select(r => r.FinishTimeMicros / 1e6f).DefaultIfEmpty(0).Min(),
                routeSourceHash = CourseRuntime.Active.SourceHash, unityVersion = Application.unityVersion,
                entrants = results.Select(r =>
                {
                    session.MaxVerticalByEntrant.TryGetValue(r.Entrant, out Vector4 v);
                    return $"{r.Placement,2} {r.Entrant.Roster.EntrantId,-6} {r.Entrant.Roster.CarId} {r.Outcome} {r.FinishTimeMicros / 1e6:F2}s " +
                           $"contacts {r.Entrant.Progress.VehicleContacts} walls {r.Entrant.Progress.WallIncidents} resets {r.Entrant.Progress.Resets} " +
                           $"recoveries {r.Entrant.AutoRecoveries} progress {r.LegalProgressMetres:F0}m maxVy {v.w:F1} at ({v.x:F0},{v.y:F0},{v.z:F0})" +
                           (session.FirstCorridorExit.TryGetValue(r.Entrant, out string exit) ? " firstExit " + exit : "");
                }).ToArray(),
            };
            evidence.exitTraces = session.ExitTraces.SelectMany(kv => new[] { "== " + kv.Key.Roster.EntrantId }.Concat(kv.Value)).ToArray();
            string json = JsonUtility.ToJson(evidence, true);
            Directory.CreateDirectory("Evidence/courses");
            File.WriteAllText("Evidence/courses/C01-12car-contact.json", json);
            Debug.Log("[NightSignal.FullGrid] " + json);

            Assert.That(session.AnyNonFinite, Is.False, "a car state became NaN/Infinity");
            Assert.That(session.MaxVerticalSpeed, Is.LessThan(12f), "contact launched a car");
            Assert.That(finished, Is.GreaterThanOrEqualTo(10), "most of a twelve-car field should finish C01 legally");
            Assert.That(results.Single(r => r.Entrant.Human).Outcome, Is.EqualTo(RunOutcome.Finished));
            Assert.That(evidence.corridorCuts, Is.EqualTo(0));
            Object.Destroy(go);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator TimeAttack_IsNonContact_AndRejectsLiveAi()
        {
            yield return LoadCourse("C01");
            var go = new GameObject("OfflineTimeAttack");
            var session = go.AddComponent<OfflineRaceSession>();
            session.Autopilot = true;
            session.SimulationSpeed = 25;
            session.Headless = true;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact };
            session.OpposingAi = new List<string> { "R01" };
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Time Attack"));
            yield return null;
            Assert.That(session.Ready, Is.False, "a stale live-AI request cannot slip into Time Attack");
            Object.Destroy(go);
        }

        static IEnumerator LoadCourse(string course)
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
        }

        [System.Serializable]
        sealed class GridEvidence
        {
            public string course, contact, driver, routeSourceHash, unityVersion;
            public int vehicles, humans, finished, dnf, totalVehicleContacts, maxVehicleContactsOneCar, totalWallIncidents, totalResets, corridorCuts;
            public float maxVerticalSpeed, tickMsAverage, tickMsMax, winnerSeconds;
            public bool anyNonFinite;
            public string[] entrants;
            public string[] exitTraces;
        }
    }
}
