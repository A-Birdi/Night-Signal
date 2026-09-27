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
    /// Legal reference runs for benchmark calibration and starter pacing (spec §2.5, Addendum 02 F08/F10): on each campaign
    /// stage's course, under its authored surface, the validator autopilot drives every starter twice, alone on the road —
    /// stock, and with the build its upgrade path intends by that stage (build-recipes.json). Times, PI and cap legality go to
    /// Evidence/progression/reference/. Automation with legal inputs, not a human run: benchmarks derived from these are
    /// labelled as such.
    /// </summary>
    public sealed class ReferenceRunTests
    {
        static readonly string[] Starters = { "V01", "V02", "V03" };

        static IEnumerable<string> NormalStages()
        {
            for (int i = 1; i <= Limits.CampaignStages; i++) yield return "S" + i.ToString("00");
        }

        [UnityTest, Timeout(1800000)]
        public IEnumerator Normal_StockAndIntendedBuild([ValueSource(nameof(NormalStages))] string stageId)
        {
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = lib.Catalogue;
            StageDef stage = cat.Stage(stageId);
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                $"Assets/Content/Courses/{stage.Course}/{stage.Course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#else
            Assert.Ignore("Editor-only scene loading");
            yield break;
#endif
            yield return null;
            Assert.That(CourseRuntime.Active, Is.Not.Null, "course generated");
            string surface = CourseRuntime.Active.Route?.Surface ?? "dry";
            var at = new StageRef { Mode = CampaignMode.Normal, Stage = stage.Number };

            var evidence = new ReferenceEvidence
            {
                stage = stageId, course = stage.Course, type = stage.Type, capPi = stage.MaxPI, surface = surface,
                driver = "RouteFollower validator autopilot (automation, legal inputs; conservative, far from the car's limit) — not a human run",
                provisionalTargetMs = StageBenchmarks.Provisional(cat, stage, CampaignMode.Normal).TargetTimeMs,
                unityVersion = Application.unityVersion,
            };
            foreach (string car in Starters)
            {
                CarDef model = cat.Car(car);
                ResolvedCarSpec stock = BuildResolver.ResolveStock(model, cat.CarTunings[car], lib.Parts);
                RecipeStep step = lib.Recipes.Car(car).Path
                    .Where(s => s.Kind == "main" && s.By != null && StageRef.Parse(s.By).Mode == CampaignMode.Normal && StageRef.Parse(s.By).CompareTo(at) <= 0)
                    .OrderBy(s => StageRef.Parse(s.By)).LastOrDefault();
                ResolvedCarSpec developed = step == null ? stock : BuildResolver.Resolve(model, cat.CarTunings[car], lib.Parts, RecipeBook.ToSnapshot(step, lib.Parts)).Spec;
                Assert.That(developed, Is.Not.Null, $"{car} {step?.Id} resolves");

                var row = new ReferenceRow
                {
                    car = car, stepId = step?.Id ?? "stock",
                    stockPi = PerformanceIndexEstimator.Estimate(stock, stock, model.BasePI).Value,
                    developedPi = PerformanceIndexEstimator.Estimate(developed, stock, model.BasePI).Value,
                };
                double[] stockRun = null, devRun = null;
                yield return Drive(car, stock, surface, r => stockRun = r);
                yield return Drive(car, developed, surface, r => devRun = r);
                row.stockMs = (long)stockRun[0];
                row.stockFinished = stockRun[1] > 0;
                row.stockWalls = (int)stockRun[2];
                row.developedMs = (long)devRun[0];
                row.developedFinished = devRun[1] > 0;
                row.developedWalls = (int)devRun[2];
                row.developedLegal = row.developedPi <= stage.MaxPI;
                evidence.starters.Add(row);
            }

            Directory.CreateDirectory("Evidence/progression/reference");
            File.WriteAllText($"Evidence/progression/reference/N-{stageId}.json", JsonUtility.ToJson(evidence, true));
            Debug.Log($"[NightSignal.Reference] {stageId} {stage.Course} cap {stage.MaxPI} ({surface}), provisional {evidence.provisionalTargetMs / 1000.0:F1}s: " +
                      string.Join("; ", evidence.starters.Select(r =>
                          $"{r.car} stock PI {r.stockPi} {r.stockMs / 1000.0:F1}s | {r.stepId} PI {r.developedPi}{(r.developedLegal ? "" : " OVER CAP")} {r.developedMs / 1000.0:F1}s")));

            foreach (ReferenceRow r in evidence.starters)
            {
                Assert.That(r.stockFinished && r.developedFinished, Is.True, $"{stageId}: {r.car} finished both runs");
                Assert.That(r.developedLegal, Is.True, $"{stageId}: {r.car} {r.stepId} PI {r.developedPi} within the {stage.MaxPI} cap");
            }
        }

        /// <summary>One solo autopilot run; reports [finish ms (penalties included), finished 1/0, wall incidents].</summary>
        internal static IEnumerator Drive(string car, ResolvedCarSpec spec, string surface, System.Action<double[]> done)
        {
            var go = new GameObject("ReferenceRun");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = car;
            session.PlayerSpec = spec;
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact, Surface = surface };
            session.OpposingAi = new List<string>();
            yield return null;
            float start = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - start < 300f) yield return null;
            RaceEntrantResult me = session.Results?.FirstOrDefault(r => r.Entrant.Human);
            done(new double[]
            {
                me != null && me.Outcome == RunOutcome.Finished ? me.FinishTimeMicros / 1000.0 : 0,
                me != null && me.Outcome == RunOutcome.Finished ? 1 : 0,
                me?.Entrant.Progress.WallIncidents ?? -1,
            });
            Object.Destroy(go);
            yield return null;
        }

        [System.Serializable]
        sealed class ReferenceEvidence
        {
            public string stage, course, type, surface, driver, unityVersion;
            public int capPi;
            public long provisionalTargetMs;
            public List<ReferenceRow> starters = new List<ReferenceRow>();
        }

        [System.Serializable]
        sealed class ReferenceRow
        {
            public string car, stepId;
            public int stockPi, developedPi, stockWalls, developedWalls;
            public long stockMs, developedMs;
            public bool stockFinished, developedFinished, developedLegal;
        }
    }
}
