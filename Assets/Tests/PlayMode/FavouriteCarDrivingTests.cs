using System;
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
    /// Addendum 02 F09, driving half (the data half — every path resolvable, shop/cap legal, meeting band demands and
    /// affordable — is the .NET <c>UpgradePathTests</c>): every one of the 18 models, i.e. every handling family (FR, FF,
    /// AWD, MR, front-mid), driven on its favourite-car path at representative stage sides — the stage it is meant to be
    /// bought for, a mid-campaign stage, the S28 lieutenant and Hard S15 — with the build its recipe intends there, solo,
    /// under the side's authored conditions, by the validator autopilot (legal inputs; automation, not a human). Each run
    /// must finish, be cap-legal, and meet the side's certified target: a favourite car that cannot is a compulsory model
    /// change in disguise. Evidence: Evidence/progression/favourite-cars.json.
    /// </summary>
    public sealed class FavouriteCarDrivingTests
    {
        [UnityTest, Timeout(3600000)]
        public IEnumerator EveryModel_OnItsPath_MeetsTheCertifiedTargets()
        {
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = lib.Catalogue;
            var rows = new List<Row>();
            var failures = new List<string>();
            foreach (CarDef car in cat.Cars.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                CarRecipe recipe = lib.Recipes.Car(car.Id);
                Assert.That(recipe, Is.Not.Null, car.Id + " has a recipe");
                int buy = recipe.BuyBy != null ? StageRef.Parse(recipe.BuyBy).Stage : 1;
                int mid = Math.Min(27, buy + (28 - buy) / 2);
                var sides = new List<StageRef>
                {
                    new StageRef { Mode = CampaignMode.Normal, Stage = Math.Max(buy, 1) },
                    new StageRef { Mode = CampaignMode.Normal, Stage = mid },
                    new StageRef { Mode = CampaignMode.Normal, Stage = 28 },
                    new StageRef { Mode = CampaignMode.Hard, Stage = 15 },
                };
                foreach (StageRef at in sides.Distinct())
                {
                    StageDef stage = cat.Stage("S" + at.Stage.ToString("00"));
                    RecipeStep step = recipe.Path
                        .Where(s => s.Kind == "main" && s.By != null && StageRef.Parse(s.By).CompareTo(at) <= 0)
                        .OrderBy(s => StageRef.Parse(s.By)).LastOrDefault();
                    ResolvedCarSpec stock = BuildResolver.ResolveStock(car, cat.CarTunings[car.Id], lib.Parts);
                    ResolvedCarSpec spec = step == null ? stock : BuildResolver.Resolve(car, cat.CarTunings[car.Id], lib.Parts, RecipeBook.ToSnapshot(step, lib.Parts)).Spec;
                    int pi = PerformanceIndexEstimator.Estimate(spec, stock, car.BasePI).Value;
#if UNITY_EDITOR
                    AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                        $"Assets/Content/Courses/{stage.Course}/{stage.Course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
                    yield return load;
#else
                    Assert.Ignore("Editor-only scene loading");
                    yield break;
#endif
                    yield return null;
                    string surface = RaceConditions.Surface(cat, "campaign", stage.Id, at.Mode, CourseRuntime.Active);
                    double[] run = null;
                    yield return ReferenceRunTests.Drive(car.Id, spec, surface, r => run = r);
                    long target = StageBenchmarks.For(cat, stage, at.Mode).TargetTimeMs;
                    var row = new Row
                    {
                        car = car.Id, drive = car.Drive, layout = car.EngineLayout, side = (at.Mode == CampaignMode.Hard ? "H:" : "N:") + stage.Id,
                        course = stage.Course, surface = surface, build = step?.Id ?? "stock", pi = pi, capPi = stage.MaxPI,
                        ms = (long)run[0], finished = run[1] > 0, walls = (int)run[2], targetMs = target,
                    };
                    row.legal = pi <= stage.MaxPI;
                    row.meetsTarget = row.finished && row.ms <= target;
                    rows.Add(row);
                    Debug.Log($"[NightSignal.Favourite] {row.car} ({row.drive} {row.layout}) {row.side} {row.course} {row.surface}: {row.build} PI {row.pi}/{row.capPi} " +
                              $"{row.ms / 1000.0:F1}s vs target {row.targetMs / 1000.0:F1}s {(row.meetsTarget ? "OK" : "MISSES")}");
                    if (!row.finished) failures.Add($"{row.car} {row.side}: did not finish");
                    else if (!row.legal) failures.Add($"{row.car} {row.side}: {row.build} PI {row.pi} over the {row.capPi} cap");
                    else if (!row.meetsTarget) failures.Add($"{row.car} {row.side}: {row.build} {row.ms / 1000.0:F1}s misses the {row.targetMs / 1000.0:F1}s target");
                }
            }
            Directory.CreateDirectory("Evidence/progression");
            File.WriteAllText("Evidence/progression/favourite-cars.json", JsonUtility.ToJson(new Rows { rows = rows }, true));
            Assert.That(failures, Is.Empty, string.Join("; ", failures));
        }

        [Serializable]
        sealed class Rows { public List<Row> rows; }

        [Serializable]
        sealed class Row
        {
            public string car, drive, layout, side, course, surface, build;
            public int pi, capPi, walls;
            public long ms, targetMs;
            public bool finished, legal, meetsTarget;
        }
    }
}
