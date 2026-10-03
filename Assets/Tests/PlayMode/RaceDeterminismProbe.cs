using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Art;
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
    /// Diagnostic for V-152: the twelve-car C01 race of FullGridContactTests (fixed field, headless, autopilot in the human
    /// seat) under controlled histories in one Play Mode session — fresh; after only loading C12; after a non-headless drift
    /// race (as DriftAttackTests run them). Before each it counts the barrier/drivable colliders by scene, so leftovers from an
    /// earlier course or race show. Explicit: a measuring tool.
    /// </summary>
    [ResetAutomationStatics]
    public sealed class RaceDeterminismProbe
    {
        static IEnumerator Load(string course)
        {
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                $"Assets/Content/Courses/{course}/{course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#endif
            yield return null;
        }

        static string Census()
        {
            var byScene = new Dictionary<string, int>();
            foreach (Collider c in Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (((1 << c.gameObject.layer) & (GameLayers.BarrierMask | GameLayers.DrivableMask)) == 0) continue;
                string key = (c.gameObject.scene.IsValid() ? c.gameObject.scene.name : "no-scene") + (c.enabled && c.gameObject.activeInHierarchy ? "" : " (inactive)");
                byScene[key] = byScene.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            return string.Join(", ", byScene.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"));
        }

        static IEnumerator TwelveCar(List<string> log, string label)
        {
            yield return Load("C01");
            string census = Census();
            var go = new GameObject("OfflineRace");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = "V01";
            session.Autopilot = true;
            session.SimulationSpeed = 20;
            session.Headless = true;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10 };
            session.OpposingAi = new List<string> { "R01", "R02", "R03", "R05", "R06", "R07", "R09", "ai-8", "ai-9", "ai-10", "ai-11" };
            yield return null;
            float start = Time.realtimeSinceStartup;
            while (session.Phase != MatchPhase.Results && Time.realtimeSinceStartup - start < 300f) yield return null;
            int contacts = session.Sim.Entrants.Sum(e => e.Progress.VehicleContacts), walls = session.Sim.Entrants.Sum(e => e.Progress.WallIncidents);
            RaceEntrantResult me = session.Results?.FirstOrDefault(r => r.Entrant.Human);
            log.Add($"{label}: contacts {contacts}, walls {walls}, player P{me?.Placement} {(me?.FinishTimeMicros ?? 0) / 1e6:F2} s; colliders {census}");
            Object.Destroy(go);
            yield return null;
        }

        static IEnumerator DriftRace(string course)
        {
            yield return Load(course);
            var go = new GameObject("DriftSession");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = "V04";
            session.Autopilot = true;
            session.SimulationSpeed = 30;
            session.Rules = new RaceEventRules { Kind = "freeplay", DriftRanking = true, Surface = CourseRuntime.Active.Route?.Surface ?? "dry" };
            session.OpposingAi = new List<string> { "d1", "d2", "d3" };
            yield return null;
            float start = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - start < 300f) yield return null;
            Object.Destroy(go);
            yield return null;
        }

        [UnityTest, Explicit("diagnostic tool (V-152)"), Timeout(1200000)]
        public IEnumerator TwelveCarRace_UnderDifferentHistories()
        {
            var log = new List<string>();
            yield return TwelveCar(log, "1 fresh");
            yield return TwelveCar(log, "2 again");
            yield return Load("C12");
            yield return TwelveCar(log, "3 after loading C12");
            yield return DriftRace("C12");
            yield return TwelveCar(log, "4 after a C12 drift race");
            yield return DriftRace("C01");
            yield return TwelveCar(log, "5 after a C01 drift race");
            yield return TwelveCar(log, "6 again");
            foreach (string l in log) Debug.Log("[NightSignal.DeterminismProbe] " + l);
        }
    }
}
