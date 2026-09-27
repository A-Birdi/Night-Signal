using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
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
    /// S29 "Four Signals" in the real campaign race (live featured rival and support, light contact, certified targets):
    /// each starter on the build its path intends by S29, driven by the validator autopilot (legal inputs, drifting the
    /// Arc), judged by the same ContractJudge as the dedicated server. Evidence: Evidence/progression/four-signals.json.
    /// Automation, not a human run.
    /// </summary>
    public sealed class FourSignalsRaceTests
    {
        [UnityTest, Timeout(1800000)]
        public IEnumerator S29_EachStarter_InTheCampaignRace()
        {
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = lib.Catalogue;
            StageDef stage = cat.Stage("S29");
            StageBenchmark benchmark = StageBenchmarks.For(cat, stage, CampaignMode.Normal);
            var rows = new List<string>();
            int passedAll = 0;
            foreach (string car in new[] { "V01", "V02", "V03" })
            {
#if UNITY_EDITOR
                AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                    $"Assets/Content/Courses/{stage.Course}/{stage.Course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
                yield return load;
#else
                Assert.Ignore("Editor-only scene loading");
                yield break;
#endif
                yield return null;
                var at = new StageRef { Mode = CampaignMode.Normal, Stage = stage.Number };
                RecipeStep step = lib.Recipes.Car(car).Path
                    .Where(s => s.Kind == "main" && s.By != null && StageRef.Parse(s.By).Mode == CampaignMode.Normal && StageRef.Parse(s.By).CompareTo(at) <= 0)
                    .OrderBy(s => StageRef.Parse(s.By)).LastOrDefault();
                ResolvedCarSpec spec = BuildResolver.Resolve(cat.Car(car), cat.CarTunings[car], lib.Parts, RecipeBook.ToSnapshot(step, lib.Parts)).Spec;

                var go = new GameObject("FourSignals");
                var session = go.AddComponent<OfflineRaceSession>();
                session.CarId = car;
                session.PlayerSpec = spec;
                session.Autopilot = true;
                session.Headless = true;
                session.SimulationSpeed = 30;
                session.Rules = new RaceEventRules
                {
                    Kind = "campaign", Mode = CampaignMode.Normal, StageId = stage.Id, StageNumber = stage.Number, CarCapPi = stage.MaxPI,
                    Contact = ContactPolicy.LightContact, Surface = CourseRuntime.Active?.Route?.Surface ?? "dry",
                    BenchmarkTargetMs = benchmark.TargetTimeMs, HardTimeoutMs = benchmark.HardTimeoutMs, RequiresBeatingFeaturedRival = benchmark.RequiresBeatingFeaturedRival,
                };
                session.OpposingAi = new List<string>(stage.Normal.Opponents);
                yield return null;
                float start = Time.realtimeSinceStartup;
                while (session.Results == null && Time.realtimeSinceStartup - start < 600f) yield return null;
                RaceEntrantResult me = session.Results?.FirstOrDefault(r => r.Entrant.Human);
                Assert.That(me, Is.Not.Null, car + ": the race completed");
                string row = $"{car} {step.Id}: {me.Outcome} P{me.Placement}/{session.Results.Count} {me.FinishTimeMicros / 1e6:F1}s (limit {benchmark.TargetTimeMs / 1000.0:F1}s), " +
                             $"contracts {me.ContractsPassed}/4 — {me.ContractDetail}";
                rows.Add(row);
                Debug.Log("[NightSignal.FourSignals] " + row);
                if (me.ContractsPassed == 4) passedAll++;
                Object.Destroy(go);
                yield return null;
            }
            Directory.CreateDirectory("Evidence/progression");
            File.WriteAllText("Evidence/progression/four-signals.json", JsonUtility.ToJson(new Rows { rows = rows }, true));
            Assert.That(passedAll, Is.GreaterThan(0), "no starter passed all four contracts: " + string.Join(" | ", rows));
        }

        [System.Serializable]
        sealed class Rows { public List<string> rows; }
    }
}
