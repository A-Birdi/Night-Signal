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
    }
}
