using System.Collections;
using System.IO;
using NightSignal.Race;
using NightSignal.Track;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Drives each validated course start-to-finish with the route-following autopilot (same inputs and chassis as a
    /// player, no teleporting) and checks every checkpoint legality. Evidence: Evidence/courses/&lt;ID&gt;-autopilot.json.
    /// This proves drivability and checkpoint data, not that the course is fun or that a human has played it.
    /// </summary>
    public sealed class CourseDriveTests
    {
        [UnityTest, Timeout(600000)]
        public IEnumerator C01_AutopilotFinishesLegally_V01() => Drive("C01", "V01");

        [UnityTest, Timeout(600000)]
        public IEnumerator C01_AutopilotFinishesLegally_V03() => Drive("C01", "V03");

        static IEnumerator Drive(string course, string car)
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
            Assert.That(CourseRuntime.Active.Track, Is.Not.Null);

            var go = new GameObject("DriveSession");
            var session = go.AddComponent<LocalDriveSession>();
            session.CarId = car;
            session.Autopilot = true;
            session.SimulationSpeed = 25;
            session.ShowDebugHud = false;
            yield return null;
            Assert.That(session.Ready, Is.True);

            float realStart = Time.realtimeSinceStartup;
            while (!session.Progress.Finished && session.RaceTimeMicros < 420_000_000L && Time.realtimeSinceStartup - realStart < 300f)
                yield return null;

            EntrantProgress p = session.Progress;
            var tracker = new RaceProgressTracker(CourseRuntime.Active.Track);
            string report = JsonUtility.ToJson(new DriveEvidence
            {
                course = course, car = car, driver = "RouteFollower (Validator profile) — scripted, not a human",
                finished = p.Finished, finishSeconds = p.FinishTimeMicros / 1e6f, checkpointsPassed = p.CheckpointsPassed,
                checkpointsTotal = tracker.TotalCheckpoints, wallIncidents = p.WallIncidents, resets = p.Resets,
                corridorCut = p.CorridorCut, outOfCorridorSeconds = p.OutOfCorridorSeconds, wrongWaySeconds = p.WrongWaySeconds,
                routeSourceHash = CourseRuntime.Active.SourceHash, unityVersion = Application.unityVersion,
            }, true);
            Directory.CreateDirectory("Evidence/courses");
            File.WriteAllText($"Evidence/courses/{course}-{car}-autopilot.json", report);
            Debug.Log($"[NightSignal.CourseDrive] {report}");

            Assert.That(p.Finished, Is.True, $"did not finish; reached {p.CheckpointsPassed}/{tracker.TotalCheckpoints} checkpoints at {p.Location.Distance:F0} m");
            Assert.That(p.CheckpointsPassed, Is.EqualTo(tracker.TotalCheckpoints));
            Assert.That(p.CorridorCut, Is.False);
            Assert.That(p.Resets, Is.EqualTo(0));
            Assert.That(p.WallIncidents, Is.LessThanOrEqualTo(2));
            Object.Destroy(go);
        }

        [System.Serializable]
        sealed class DriveEvidence
        {
            public string course, car, driver, routeSourceHash, unityVersion;
            public bool finished, corridorCut;
            public float finishSeconds, outOfCorridorSeconds, wrongWaySeconds;
            public int checkpointsPassed, checkpointsTotal, wallIncidents, resets;
        }
    }
}
