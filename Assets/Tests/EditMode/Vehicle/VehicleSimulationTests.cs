using System.Linq;
using System.Text;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    public sealed class VehicleSimulationTests
    {
        static ContentCatalogue catalogue;

        static ContentCatalogue Catalogue => catalogue ?? (catalogue = ContentFiles.LoadProjectCatalogue());

        static VehicleParams Params(string carId) =>
            VehicleFactory.Build(Catalogue.Car(carId), Catalogue.CarTunings[carId], AssistSettings.Default);

        [Test]
        public void EveryCarModelHasTuning()
        {
            foreach (CarDef car in Catalogue.Cars)
                Assert.That(Catalogue.CarTunings.ContainsKey(car.Id), Is.True, car.Id);
        }

        [Test]
        public void Simulation_IsDeterministicForIdenticalInput()
        {
            VehicleParams p = Params("V05");
            var a = new VehicleSimulation(p, PlaneVehicleWorld.Flat);
            var b = new VehicleSimulation(p, PlaneVehicleWorld.Flat);
            VehicleState sa = VehicleState.AtRest(new Vector3(0f, 0.5f, 0f), Quaternion.identity);
            VehicleState sb = sa;
            for (int i = 0; i < 600; i++)
            {
                var input = DriverInput.Quantize(Mathf.Sin(i * 0.05f), i % 200 < 150 ? 1f : 0f, i % 200 >= 150 ? 0.8f : 0f,
                    i % 97 == 0 ? InputButtons.Handbrake : InputButtons.None);
                a.Step(ref sa, input);
                b.Step(ref sb, input);
            }
            Assert.That(sa.Position, Is.EqualTo(sb.Position));
            Assert.That(sa.Rotation, Is.EqualTo(sb.Rotation));
            Assert.That(sa.Velocity, Is.EqualTo(sb.Velocity));
            Assert.That(sa.Tick, Is.EqualTo(600u));
        }

        [Test]
        public void CarSettlesOnFlatGroundWithoutDrifting()
        {
            VehicleParams p = Params("V01");
            var sim = new VehicleSimulation(p, PlaneVehicleWorld.Flat);
            VehicleState s = VehicleState.AtRest(new Vector3(0f, 0.6f, 0f), Quaternion.identity);
            for (int i = 0; i < 240; i++) sim.Step(ref s, DriverInput.Neutral);
            Assert.That(s.Velocity.magnitude, Is.LessThan(0.05f));
            Assert.That(sim.Telemetry.GroundedWheels, Is.EqualTo(4));
            Assert.That(Vector3.Angle(s.Rotation * Vector3.up, Vector3.up), Is.LessThan(1f));
        }

        [Test]
        public void ReleasingInputsAfterStarvationCoastsThenBrakes()
        {
            DriverInput full = DriverInput.Quantize(0.2f, 1f, 0f, InputButtons.None);
            Assert.That(full.Starved(0.2f, 0.25f), Is.EqualTo(full));
            DriverInput starved = full.Starved(1.0f, 0.25f);
            Assert.That(starved.Throttle, Is.EqualTo(0f));
            Assert.That(starved.Brake, Is.GreaterThan(0.5f));
        }

        [Test]
        public void HandlingHarness_AllModelsWithinPlausibleEnvelopes()
        {
            var table = new StringBuilder("car  layout  0-100s  v@1km  brake100m  skidpadG  driftHold  meanSlip  spun\n");
            foreach (CarDef car in Catalogue.Cars)
            {
                HandlingReport r = HandlingHarness.Measure(Params(car.Id));
                table.AppendLine($"{car.Id}  {car.Drive,-4}  {r.ZeroTo100Seconds,6:F2}  {r.SpeedAfter1000mKmh,5:F0}  {r.Brake100To0Metres,9:F1}  {r.SkidpadLateralG,8:F2}  {r.DriftHoldSeconds,9:F2}  {r.DriftMeanSlipDeg,8:F1}  {r.DriftSpun}");
                Assert.That(r.ZeroTo100Seconds, Is.InRange(3.0f, 13.0f), car.Id + " 0-100");
                Assert.That(r.Brake100To0Metres, Is.InRange(28f, 55f), car.Id + " braking");
                Assert.That(r.SkidpadLateralG, Is.InRange(0.8f, 1.6f), car.Id + " lateral grip");
            }
            Debug.Log("[NightSignal.Handling] " + HandlingHarness.ConditionsText + "\n" + table);
        }

        [Test]
        public void LayoutIdentities_AreMeasurablyDifferent()
        {
            HandlingReport rwd = HandlingHarness.Measure(Params("V01"));
            HandlingReport fwd = HandlingHarness.Measure(Params("V02"));
            HandlingReport awd = HandlingHarness.Measure(Params("V03"));
            HandlingReport fastest = HandlingHarness.Measure(Params("V16"));
            Assert.That(rwd.DriftHoldSeconds, Is.GreaterThan(fwd.DriftHoldSeconds), "RWD should sustain a power drift longer than FWD");
            Assert.That(awd.DriftHoldSeconds, Is.GreaterThan(fwd.DriftHoldSeconds), "AWD rotates more than FWD");
            Assert.That(fastest.ZeroTo100Seconds, Is.LessThan(rwd.ZeroTo100Seconds), "flagship should out-accelerate the starter");
            Assert.That(fastest.SkidpadLateralG, Is.GreaterThan(rwd.SkidpadLateralG), "higher-class tyres hold more lateral grip");
        }

        [Test]
        public void ScriptedDrift_NeverSpinsAnyModel_AndRwdHoldsLongestOnAverage()
        {
            double rwd = 0, awd = 0, fwd = 0;
            int nr = 0, na = 0, nf = 0;
            foreach (CarDef car in Catalogue.Cars)
            {
                HandlingReport r = HandlingHarness.Measure(Params(car.Id));
                Assert.That(r.DriftSpun, Is.False, car.Id + " spun under the scripted drifter");
                switch (car.Drive)
                {
                    case "RWD": rwd += r.DriftHoldSeconds; nr++; break;
                    case "AWD": awd += r.DriftHoldSeconds; na++; break;
                    default: fwd += r.DriftHoldSeconds; nf++; break;
                }
            }
            Assert.That(rwd / nr, Is.GreaterThan(awd / na));
            Assert.That(awd / na, Is.GreaterThan(fwd / nf));
        }
    }
}
