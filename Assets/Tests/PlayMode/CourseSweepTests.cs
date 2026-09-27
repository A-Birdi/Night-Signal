using System.Collections;
using System.Collections.Generic;
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
    /// Drivability sweep over EVERY authored course scene: the validator autopilot drives start to finish with the
    /// real chassis (same inputs as a player, no teleporting), fast-forwarded. Evidence per course:
    /// Evidence/courses/sweep/&lt;ID&gt;.json. Proves checkpoint legality and that the generated road is drivable end to end
    /// by a scripted driver — not that the course is finished art, fun, or human-tested.
    /// </summary>
    public sealed class CourseSweepTests
    {
        static IEnumerable<string> Courses()
        {
            var ids = new List<string>();
            foreach (string dir in Directory.GetDirectories("Assets/Content/Courses"))
            {
                string id = Path.GetFileName(dir);
                if (File.Exists($"{dir}/{id}.unity")) ids.Add(id);
            }
            ids.Sort(System.StringComparer.Ordinal);
            return ids;
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator Autopilot_DrivesStartToFinish([ValueSource(nameof(Courses))] string course)
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
            TrackData track = CourseRuntime.Active.Track;

            var go = new GameObject("SweepSession");
            var session = go.AddComponent<LocalDriveSession>();
            session.CarId = "V05"; // a mid-field all-rounder
            session.Autopilot = true;
            session.SimulationSpeed = 30;
            yield return null;
            Assert.That(session.Ready, Is.True);

            float realStart = Time.realtimeSinceStartup;
            float bestDistance = 0f;
            long bestAt = 0;
            while (!session.Progress.Finished && session.RaceTimeMicros < 1_200_000_000L && Time.realtimeSinceStartup - realStart < 420f)
            {
                // Give up once the car has made no forward progress for 30 s of race time (stuck/off the road).
                float d = session.Progress.RaceDistance;
                if (d > bestDistance + 1f) { bestDistance = d; bestAt = session.RaceTimeMicros; }
                else if (session.RaceTimeMicros - bestAt > 30_000_000L) break;
                yield return null;
            }

            EntrantProgress p = session.Progress;
            var tracker = new RaceProgressTracker(track);
            var evidence = new SweepEvidence
            {
                course = course, car = "V05", driver = "RouteFollower validator autopilot — scripted, not a human",
                finished = p.Finished, finishSeconds = p.FinishTimeMicros / 1e6f, lengthMetres = track.LengthMetres,
                checkpointsPassed = p.CheckpointsPassed, checkpointsTotal = tracker.TotalCheckpoints,
                wallIncidents = p.WallIncidents, resets = p.Resets, corridorCut = p.CorridorCut,
                outOfCorridorSeconds = p.OutOfCorridorSeconds, stoppedAtMetres = p.Location.Distance,
                routeSourceHash = CourseRuntime.Active.SourceHash, unityVersion = Application.unityVersion,
            };
            Directory.CreateDirectory("Evidence/courses/sweep");
            File.WriteAllText($"Evidence/courses/sweep/{course}.json", JsonUtility.ToJson(evidence, true));
            Object.Destroy(go);

            Assert.That(p.Finished, Is.True, $"{course}: stopped at {p.Location.Distance:F0} m of {track.LengthMetres:F0} ({p.CheckpointsPassed}/{tracker.TotalCheckpoints} checkpoints)");
            Assert.That(p.CheckpointsPassed, Is.EqualTo(tracker.TotalCheckpoints));
            Assert.That(p.CorridorCut, Is.False);
        }

        [System.Serializable]
        sealed class SweepEvidence
        {
            public string course, car, driver, routeSourceHash, unityVersion;
            public bool finished, corridorCut;
            public float finishSeconds, lengthMetres, outOfCorridorSeconds, stoppedAtMetres;
            public int checkpointsPassed, checkpointsTotal, wallIncidents, resets;
        }
    }
}
