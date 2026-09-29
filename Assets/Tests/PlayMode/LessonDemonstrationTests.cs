using System.Collections;
using System.Collections.Generic;
using NightSignal.AI;
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
    /// The T00 lesson demonstrations that need the autopilot to drift (grip versus drift, countersteering): its raw drift
    /// on the loop per car and drift skill, with the pilot's attempt statistics. Explicit — a measurement for choosing the
    /// demonstration car and skill.
    /// </summary>
    [Explicit("T00 drift demonstration measurement")]
    public sealed class LessonDemonstrationTests
    {
        [UnityTest, Timeout(900000)]
        public IEnumerator DriftDemonstration_OnT00()
        {
#if UNITY_EDITOR
            foreach (string car in new[] { "V01", "V04" })
                foreach (float skill in new[] { 0.8f, 0.95f })
                {
                    yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Content/Courses/T00/T00.unity",
                        new LoadSceneParameters(LoadSceneMode.Single));
                    yield return null;
                    var go = new GameObject("LessonDemo");
                    var session = go.AddComponent<OfflineRaceSession>();
                    session.CarId = car;
                    session.Autopilot = true;
                    session.AutopilotDriftSkill = skill;
                    session.Headless = true;
                    session.SimulationSpeed = 30;
                    session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 1, CarCapPi = PerformanceIndex.Max, DriftRanking = true };
                    session.OpposingAi = new List<string>();
                    float maxSlip = 0f;
                    double at688 = -1, at918 = -1, at1590 = -1;
                    session.TickObserver = (sim, tick) =>
                    {
                        RaceEntrant me = session.Player;
                        double raw = me.Drift.BankedRaw + me.Drift.UnbankedRaw;
                        float m = me.Progress.RaceDistance + sim.Track.StartMetres; // route metres
                        if (m < 689f) at688 = raw;
                        if (m < 918f) at918 = raw;
                        if (m < 1591f) at1590 = raw;
                        Vector3 v = me.State.Velocity, f = me.State.Rotation * Vector3.forward;
                        v.y = 0f; f.y = 0f;
                        if (v.magnitude > 4f) maxSlip = Mathf.Max(maxSlip, Vector3.Angle(f, v));
                    };
                    yield return null;
                    float t0 = Time.realtimeSinceStartup;
                    while (session.Results == null && Time.realtimeSinceStartup - t0 < 300f) yield return null;
                    RaceEntrantResult r = session.Results?[0];
                    RouteFollower p = session.Pilot;
                    Debug.Log($"[NightSignal.LessonDemo] T00 {car} skill {skill:0.00}: raw {r?.RawDriftScore} (Demonstration Bend 689–918 m: {at918 - at688:F0}; to 1590 m: {at1590:F0}), max slip {maxSlip:F0}°, zones {session.Sim.Drift.Zones.Count}, " +
                              $"entry {p.DriftEntrySpeed:F1} m/s, flicks {p.DriftFlicks}, holds {p.DriftHolds}, ended edge {p.DriftEndEdge} spin {p.DriftEndSpin} slow {p.DriftEndSlow}");
                    Object.Destroy(go);
                    yield return null;
                }
#else
            Assert.Ignore("Editor-only scene loading");
            yield break;
#endif
        }
    }
}
