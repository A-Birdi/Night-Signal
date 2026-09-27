using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// Core/Builds → VehicleFactory: a stock build must produce exactly today's parameters for every car (so adding
    /// the build path cannot change existing handling or records), and real parts must change the simulation in the
    /// direction the part claims while staying stable.
    /// </summary>
    public sealed class BuildParityTests
    {
        static ContentCatalogue catalogue;
        static PartsCatalogue parts;

        static ContentCatalogue Catalogue => catalogue ?? (catalogue = ContentFiles.LoadProjectCatalogue());
        static PartsCatalogue Parts => parts ?? (parts = PartsCatalogue.Load(File.ReadAllText("Assets/Content/Data/authored/parts.json")));

        static IEnumerable<string> CarIds() => ContentFiles.LoadProjectCatalogue().Cars.Select(c => c.Id);

        [Test]
        public void StockBuild_EqualsTodaysParameters([ValueSource(nameof(CarIds))] string carId)
        {
            CarDef car = Catalogue.Car(carId);
            CarTuningDef tuning = Catalogue.CarTunings[carId];
            VehicleParams stock = VehicleFactory.Build(car, tuning, AssistSettings.Default);
            ResolvedCarSpec spec = BuildResolver.ResolveStock(car, tuning, Parts);
            VehicleParams viaBuild = VehicleFactory.Build(spec, AssistSettings.Default);
            var differences = new List<string>();
            foreach (FieldInfo f in typeof(VehicleParams).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object a = f.GetValue(stock), b = f.GetValue(viaBuild);
                if (a is float fa && b is float fb)
                {
                    if (Math.Abs(fa - fb) > 1e-4f * Math.Max(1f, Math.Abs(fa))) differences.Add($"{f.Name}: {fa} vs {fb}");
                }
                else if (a is float[] xa && b is float[] xb)
                {
                    if (xa.Length != xb.Length || xa.Where((v, i) => Math.Abs(v - xb[i]) > 1e-4f * Math.Max(1f, Math.Abs(v))).Any())
                        differences.Add($"{f.Name}: [{string.Join(", ", xa)}] vs [{string.Join(", ", xb)}]");
                }
                else if (!Equals(a, b) && !(a is AssistSettings)) differences.Add($"{f.Name}: {a} vs {b}");
            }
            Assert.That(differences, Is.Empty, $"{carId}: stock build differs from the base factory");
        }

        [Test]
        public void EveryCompatiblePart_ResolvesToAStableCar()
        {
            // One part at a time on V01 (the common starter): the car must stay finite and drivable for 8 s full throttle.
            CarDef car = Catalogue.Car("V01");
            CarTuningDef tuning = Catalogue.CarTunings["V01"];
            int tried = 0;
            foreach (PartSlot slot in PartSlots.Mechanical)
                foreach (PartDef part in Parts.CompatibleParts(car, tuning, slot))
                {
                    var build = MechanicalSnapshot.Stock();
                    build.Parts[PartSlots.Id(slot)] = part.Id;
                    ResolveResult r = BuildResolver.Resolve(car, tuning, Parts, build);
                    Assert.That(r.Ok, Is.True, $"{part.Id}: {string.Join("; ", r.Issues)}");
                    VehicleParams p = VehicleFactory.Build(r.Spec, AssistSettings.Default);
                    var sim = new VehicleSimulation(p, PlaneVehicleWorld.Flat);
                    VehicleState s = VehicleState.AtRest(new Vector3(0f, 0.6f, 0f), Quaternion.identity);
                    DriverInput input = DriverInput.Quantize(0f, 1f, 0f, default);
                    for (int t = 0; t < 8 * 60; t++) sim.Step(ref s, input);
                    Assert.That(float.IsNaN(s.Position.x) || float.IsInfinity(s.Velocity.z), Is.False, $"{part.Id} produced a non-finite state");
                    Assert.That(s.Velocity.magnitude, Is.GreaterThan(15f), $"{part.Id}: car did not accelerate");
                    tried++;
                }
            Assert.That(tried, Is.GreaterThan(10));
        }

        /// <summary>
        /// Addendum 02 "meaningful upgrades", measured in the real vehicle simulation (flat-plane harness, identical inputs):
        /// on each starter the best compatible tier-2 engine, tyre and brake parts must each improve what they exist for by
        /// a margin a driver feels, and the three together must improve all of it. Brakes are measured at a 60 % pedal: with
        /// full pedal and ABS every car is tyre-limited (only tyres shorten that stop — see docs/EFFECTIVE_RULES.md).
        /// </summary>
        static VehicleParams Params(CarDef car, CarTuningDef tuning, MechanicalSnapshot build)
        {
            ResolveResult r = BuildResolver.Resolve(car, tuning, Parts, build);
            Assert.That(r.Ok, Is.True, string.Join("; ", r.Issues));
            return VehicleFactory.Build(r.Spec, AssistSettings.Default);
        }

        [Test]
        public void Tier2Upgrades_MeasurablyImproveTheStarters([Values("V01", "V02", "V03")] string carId)
        {
            CarDef car = Catalogue.Car(carId);
            CarTuningDef tuning = Catalogue.CarTunings[carId];
            HandlingReport Measure(MechanicalSnapshot build) => HandlingHarness.Measure(Params(car, tuning, build));
            string Best(PartSlot slot) => Parts.CompatibleParts(car, tuning, slot).Where(p => p.Tier <= 2 && !p.Retired)
                .OrderByDescending(p => p.Tier).ThenByDescending(p => p.Price).Select(p => p.Id).FirstOrDefault();

            string engine = Best(PartSlot.Engine), tyres = Best(PartSlot.Tyres), brakes = Best(PartSlot.Brakes);
            Assert.That(engine, Is.Not.Null, "no tier ≤ 2 engine part");
            Assert.That(tyres, Is.Not.Null, "no tier ≤ 2 tyre part");
            Assert.That(brakes, Is.Not.Null, "no tier ≤ 2 brake part");
            HandlingReport stock = Measure(MechanicalSnapshot.Stock());
            HandlingReport eng = Measure(MechanicalSnapshot.Stock().With(PartSlot.Engine, engine));
            HandlingReport tyr = Measure(MechanicalSnapshot.Stock().With(PartSlot.Tyres, tyres));
            HandlingReport brk = Measure(MechanicalSnapshot.Stock().With(PartSlot.Brakes, brakes));
            HandlingReport all = Measure(MechanicalSnapshot.Stock().With(PartSlot.Engine, engine).With(PartSlot.Tyres, tyres).With(PartSlot.Brakes, brakes));
            TestContext.WriteLine($"{carId}  0-100 s / skidpad g / 100-0 m");
            TestContext.WriteLine($"  stock            {stock.ZeroTo100Seconds:F2} / {stock.SkidpadLateralG:F3} / {stock.Brake100To0Metres:F1}");
            TestContext.WriteLine($"  {engine,-16} {eng.ZeroTo100Seconds:F2}   (1000 m: {stock.SpeedAfter1000mKmh:F1} → {eng.SpeedAfter1000mKmh:F1} km/h)");
            TestContext.WriteLine($"  {tyres,-16}        {tyr.SkidpadLateralG:F3}");
            float stockPartial = HandlingHarness.BrakeDistance(Params(car, tuning, MechanicalSnapshot.Stock()), 0.6f);
            float kitPartial = HandlingHarness.BrakeDistance(Params(car, tuning, MechanicalSnapshot.Stock().With(PartSlot.Brakes, brakes)), 0.6f);
            TestContext.WriteLine($"  {brakes,-16}                {brk.Brake100To0Metres:F1}   (60 % pedal: {stockPartial:F1} → {kitPartial:F1} m)");
            TestContext.WriteLine($"  all three        {all.ZeroTo100Seconds:F2} / {all.SkidpadLateralG:F3} / {all.Brake100To0Metres:F1}");

            // Launch is partly traction- and shift-limited; the power shows over distance.
            Assert.That(eng.ZeroTo100Seconds, Is.LessThan(stock.ZeroTo100Seconds), $"{engine}: 0-100 {stock.ZeroTo100Seconds:F2} → {eng.ZeroTo100Seconds:F2} s");
            Assert.That(eng.SpeedAfter1000mKmh, Is.GreaterThan(stock.SpeedAfter1000mKmh * 1.02f), $"{engine}: 1000 m {stock.SpeedAfter1000mKmh:F1} → {eng.SpeedAfter1000mKmh:F1} km/h");
            Assert.That(tyr.SkidpadLateralG, Is.GreaterThan(stock.SkidpadLateralG * 1.02f), $"{tyres}: skidpad {stock.SkidpadLateralG:F3} → {tyr.SkidpadLateralG:F3} g");
            Assert.That(kitPartial, Is.LessThan(stockPartial * 0.95f), $"{brakes}: 100-0 at 60 % pedal {stockPartial:F1} → {kitPartial:F1} m");
            Assert.That(brk.Brake100To0Metres, Is.LessThan(stock.Brake100To0Metres * 1.03f), $"{brakes}: a full ABS stop must not get meaningfully longer");
            Assert.That(all.ZeroTo100Seconds, Is.LessThan(stock.ZeroTo100Seconds), "package: acceleration");
            Assert.That(all.SkidpadLateralG, Is.GreaterThan(stock.SkidpadLateralG), "package: grip");
            Assert.That(all.Brake100To0Metres, Is.LessThan(stock.Brake100To0Metres), "package: braking (the tyres shorten the ABS stop)");
        }
    }
}
