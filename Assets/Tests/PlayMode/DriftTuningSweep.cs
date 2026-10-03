using System.Collections;
using System.Collections.Generic;
using System.IO;
using NightSignal.AI;
using NightSignal.Race;
using NightSignal.Track;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Diagnostic sweep (explicit, not part of any suite): the validator autopilot alone in V04 on a drift course with one
    /// drift-controller knob changed at a time, to learn which knobs move the banked score. Writes
    /// Builds/Diagnostics/drift-sweep.txt. Automation, not a human.
    /// </summary>
    [Explicit("diagnostic sweep")]
    [ResetAutomationStatics]
    public sealed class DriftTuningSweep
    {
        static readonly (string Name, System.Action<RouteFollower> Set)[] Configs =
        {
            ("baseline", f => { }),
            ("slip 22", f => f.DriftSlipDeg = 22f),
            ("slip 34", f => f.DriftSlipDeg = 34f),
            ("margin 0.3", f => f.EdgeMargin = 0.3f),
            ("margin 1.5", f => f.EdgeMargin = 1.5f),
            ("gain 0.25", f => f.CountersteerGain = 0.25f),
            ("gain 0.55", f => f.CountersteerGain = 0.55f),
            ("path 0.3", f => f.PathFollow = 0.3f),
            ("path 1.0", f => f.PathFollow = 1.0f),
            ("window 0.3", f => f.FlickWindow = 0.3f),
            ("window 0.7", f => f.FlickWindow = 0.7f),
            ("entry 16", f => f.DriftEntrySpeed = 16f),
            ("entry 22", f => f.DriftEntrySpeed = 22f),
        };

        [UnityTest, Timeout(3600000)]
        public IEnumerator Sweep([Values("C01", "C08")] string course)
        {
#if UNITY_EDITOR
            Directory.CreateDirectory("Builds/Diagnostics");
            foreach ((string name, System.Action<RouteFollower> set) in Configs)
            {
                AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                    $"Assets/Content/Courses/{course}/{course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
                yield return load;
                yield return null;
                var go = new GameObject("Sweep");
                var session = go.AddComponent<OfflineRaceSession>();
                session.CarId = "V04";
                session.Autopilot = true;
                session.SimulationSpeed = 30;
                session.Rules = new RaceEventRules { Kind = "freeplay", DriftRanking = true, Surface = CourseRuntime.Active.Route?.Surface ?? "dry" };
                session.OpposingAi = new List<string>();
                yield return null;
                set(session.Pilot);
                float t0 = Time.realtimeSinceStartup;
                while (session.Results == null && Time.realtimeSinceStartup - t0 < 400f) yield return null;
                RouteFollower f = session.Pilot;
                string line = session.Results == null ? $"{course} {name}: no result"
                    : $"{course} {name}: {session.Results[0].RawDriftScore} pts, {session.Results[0].FinishTimeMicros / 1e6:F1}s, flicks {f.DriftFlicks} holds {f.DriftHolds} end edge {f.DriftEndEdge} spin {f.DriftEndSpin} slow {f.DriftEndSlow} wrong {f.DriftEndWrongWay}";
                File.AppendAllText("Builds/Diagnostics/drift-sweep.txt", line + System.Environment.NewLine);
                Debug.Log("[NightSignal.DriftSweep] " + line);
                Object.Destroy(go);
                yield return null;
            }
#else
            Assert.Ignore("Editor-only");
            yield break;
#endif
        }
    }
}
