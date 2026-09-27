using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// Addendum 02 F10: each starter's unchanged hardware against every step of its intended upgrade path
    /// (build-recipes.json), under the handling harness' identical scripted inputs (launch, 1,000 m, ABS stop, 60 m skid pad).
    /// The developed build must change the car meaningfully — not a purchase count — and every step must stay legal for the
    /// stages it is meant for. Evidence per starter in Evidence/progression/starter-paths/.
    /// </summary>
    public sealed class StarterPathTests
    {
        [Test]
        public void IntendedPath_MeaningfullyDevelopsTheStarter([Values("V01", "V02", "V03")] string carId)
        {
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = lib.Catalogue;
            CarDef car = cat.Car(carId);
            CarTuningDef tuning = cat.CarTunings[carId];
            ResolvedCarSpec stock = BuildResolver.ResolveStock(car, tuning, lib.Parts);
            CarRecipe recipe = lib.Recipes.Car(carId);

            var evidence = new PathEvidence { car = carId, conditions = HandlingHarness.ConditionsText };
            evidence.steps.Add(Row("stock", "—", stock, stock, car, cat, null));
            foreach (RecipeStep step in recipe.Path.Where(s => s.Kind == "main").OrderBy(s => StageRef.Parse(s.By)))
            {
                ResolveResult r = BuildResolver.Resolve(car, tuning, lib.Parts, RecipeBook.ToSnapshot(step, lib.Parts));
                Assert.That(r.Ok, Is.True, $"{step.Id}: {string.Join("; ", r.Issues)}");
                evidence.steps.Add(Row(step.Id, step.By, r.Spec, stock, car, cat, step));
            }
            Directory.CreateDirectory("Evidence/progression/starter-paths");
            File.WriteAllText($"Evidence/progression/starter-paths/{carId}.json", JsonUtility.ToJson(evidence, true));
            TestContext.WriteLine($"{carId}: step / by / PI (cap) / 0-100 s / 1000 m km/h / 100-0 m / skid pad g");
            foreach (PathRow s in evidence.steps)
                TestContext.WriteLine($"  {s.step,-8} {s.by,-6} PI {s.pi,3} ({s.capPi,3})  {s.zeroTo100:F2}  {s.speedAfter1000m:F1}  {s.brake100To0:F1}  {s.skidpadG:F3}");

            PathRow first = evidence.steps[0];
            foreach (PathRow s in evidence.steps.Skip(1))
                Assert.That(s.pi, Is.LessThanOrEqualTo(s.capPi), $"{s.step} (by {s.by}): PI {s.pi} over the {s.capPi} cap of its stage");
            // The Normal campaign's last intended build against stock: every measure better, and at least two of them by 10 % or
            // more — a different car, not a purchase count.
            PathRow normalEnd = evidence.steps.Where(s => s.by.StartsWith("N:")).Last();
            var gains = new Dictionary<string, float>
            {
                { "0-100", 1f - normalEnd.zeroTo100 / first.zeroTo100 },
                { "1000 m", normalEnd.speedAfter1000m / first.speedAfter1000m - 1f },
                { "100-0", 1f - normalEnd.brake100To0 / first.brake100To0 },
                { "skid pad", normalEnd.skidpadG / first.skidpadG - 1f },
            };
            TestContext.WriteLine($"{carId} {normalEnd.step} vs stock: " + string.Join(", ", gains.Select(g => $"{g.Key} {g.Value * 100f:+0.0;-0.0}%")));
            foreach (KeyValuePair<string, float> g in gains)
                Assert.That(g.Value, Is.GreaterThan(0f), $"{normalEnd.step}: {g.Key} must improve on stock");
            Assert.That(gains.Count(g => g.Value >= 0.10f), Is.GreaterThanOrEqualTo(2), $"{normalEnd.step}: at least two measures improve by 10 % or more");
        }

        static PathRow Row(string id, string by, ResolvedCarSpec spec, ResolvedCarSpec stock, CarDef car, ContentCatalogue cat, RecipeStep step)
        {
            HandlingReport h = HandlingHarness.Measure(VehicleFactory.Build(spec, AssistSettings.Default));
            int cap = 0;
            if (step != null)
            {
                StageRef at = StageRef.Parse(step.By);
                cap = cat.Stage("S" + at.Stage.ToString("00")).MaxPI;
            }
            return new PathRow
            {
                step = id, by = by, parts = step?.Parts.Count ?? 0,
                pi = PerformanceIndexEstimator.Estimate(spec, stock, car.BasePI).Value, capPi = cap == 0 ? car.BasePI : cap,
                zeroTo100 = h.ZeroTo100Seconds, speedAfter1000m = h.SpeedAfter1000mKmh, brake100To0 = h.Brake100To0Metres, skidpadG = h.SkidpadLateralG,
            };
        }

        [System.Serializable]
        sealed class PathEvidence
        {
            public string car, conditions;
            public List<PathRow> steps = new List<PathRow>();
        }

        [System.Serializable]
        sealed class PathRow
        {
            public string step, by;
            public int parts, pi, capPi;
            public float zeroTo100, speedAfter1000m, brake100To0, skidpadG;
        }
    }
}
