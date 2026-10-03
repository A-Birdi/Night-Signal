using System.Collections;
using System.Collections.Generic;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Allocation probe for the driving hot path (spec §14 "no per-frame allocations"; V-151): a six-car light-contact race on
    /// C01 at real speed with the HUD, views and camera, the autopilot in the player's seat. It logs the managed allocation
    /// per frame from Unity's "GC Allocated In Frame" counter (editor play mode: includes some editor work); run it with the
    /// Profiler recording (deep profiling for method-level attribution) to see where the bytes come from. Explicit: a
    /// measuring tool, not a regression test.
    /// </summary>
    [ResetAutomationStatics]
    public sealed class RaceAllocationProbe
    {
        [UnityTest, Explicit("measurement tool: run with the Profiler recording"), Timeout(300000)]
        public IEnumerator SixCarRace_AllocationPerFrame()
        {
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                "Assets/Content/Courses/C01/C01.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#else
            Assert.Ignore("Editor-only scene loading");
            yield break;
#endif
            yield return null;
            Assert.That(CourseRuntime.Active, Is.Not.Null, "course generated");
            var go = new GameObject("OfflineRace");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = "V01";
            session.Autopilot = true;
            session.SimulationSpeed = 1;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10, CarCapPi = 999 };
            session.OpposingAi = new List<string> { "R01", "R02", "R03", "R04", "R05" };
            yield return null;
            Assert.That(session.Ready, Is.True);
            float until = Time.realtimeSinceStartup + 30f;
            while (session.Phase != MatchPhase.Racing && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(session.Phase, Is.EqualTo(MatchPhase.Racing), "the race started");
            yield return new WaitForSeconds(1f);

            using (ProfilerRecorder gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame"))
            {
                yield return null;
                long total = 0;
                int frames = 0, allocating = 0;
                float end = Time.realtimeSinceStartup + 6f;
                while (Time.realtimeSinceStartup < end)
                {
                    yield return null;
                    long b = gc.LastValue;
                    total += b;
                    frames++;
                    if (b > 0) allocating++;
                }
                Debug.Log($"[NightSignal.AllocProbe] counter valid {gc.Valid}: {frames} frames, {allocating} allocating, {total} B, " +
                          $"{(frames > 0 ? total / frames : 0)} B/frame, ticks {session.CurrentTick}");
            }
            Object.Destroy(go);
            yield return null;
        }
    }
}
