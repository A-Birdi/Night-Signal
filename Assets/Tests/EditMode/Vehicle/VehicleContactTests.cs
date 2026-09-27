using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>Light contact (Addendum 01 §2.1): bounded, predictable, deterministic, no damage state.</summary>
    public sealed class VehicleContactTests
    {
        static ContentCatalogue catalogue;
        static ContentCatalogue Catalogue => catalogue ?? (catalogue = ContentFiles.LoadProjectCatalogue());

        static VehicleParams Params(string carId) =>
            VehicleFactory.Build(Catalogue.Car(carId), Catalogue.CarTunings[carId], AssistSettings.Default);

        static VehicleState Car(Vector3 pos, float yawDeg, Vector3 velocity) =>
            new VehicleState { Position = pos, Rotation = Quaternion.Euler(0f, yawDeg, 0f), Velocity = velocity };

        static Vector3 Momentum(VehicleState a, VehicleParams pa, VehicleState b, VehicleParams pb) => a.Velocity * pa.MassKg + b.Velocity * pb.MassKg;

        [Test]
        public void SeparatedCars_AreUntouched()
        {
            VehicleParams p = Params("V01");
            VehicleState a = Car(Vector3.zero, 0f, new Vector3(0, 0, 20)), b = Car(new Vector3(3f, 0, 0), 0f, new Vector3(0, 0, 25));
            VehicleState a0 = a, b0 = b;
            ContactResult c = VehicleContact.Resolve(ref a, p, ref b, p);
            Assert.That(c.Touching, Is.False);
            Assert.That(a.Position, Is.EqualTo(a0.Position));
            Assert.That(b.Velocity, Is.EqualTo(b0.Velocity));
        }

        [Test]
        public void RearContact_TransfersMomentum_WithinCaps_AndSeparates()
        {
            VehicleParams p = Params("V01");
            // B is 4.1 m ahead (overlapping by a little), A closing at 8 m/s.
            VehicleState a = Car(Vector3.zero, 0f, new Vector3(0, 0, 28)), b = Car(new Vector3(0, 0, p.LengthM - 0.1f), 0f, new Vector3(0, 0, 20));
            Vector3 before = Momentum(a, p, b, p);
            float dist0 = (b.Position - a.Position).magnitude;
            ContactResult c = VehicleContact.Resolve(ref a, p, ref b, p);
            Assert.That(c.Touching, Is.True);
            Assert.That(c.Normal.z, Is.GreaterThan(0.99f), "normal points from the rear car to the front car");
            Assert.That(a.Velocity.z, Is.LessThan(28f));
            Assert.That(b.Velocity.z, Is.GreaterThan(20f));
            Assert.That(b.Velocity.z, Is.LessThanOrEqualTo(28f), "no launch: the front car never exceeds the closing car");
            Assert.That(c.DeltaV, Is.LessThanOrEqualTo(VehicleContact.MaxDeltaV + 1e-4f));
            Assert.That((Momentum(a, p, b, p) - before).magnitude / (2 * p.MassKg), Is.LessThan(0.05f), "uncapped equal cars conserve momentum");
            Assert.That((b.Position - a.Position).magnitude, Is.GreaterThan(dist0));
        }

        [Test]
        public void HeadOn_IsCappedPerTick_NotALaunch()
        {
            VehicleParams p = Params("V05");
            // The body box centre is offset along the car; turning B round flips that offset, so place the boxes, not origins.
            float oz = p.BodyCentreOffset.z;
            VehicleState a = Car(Vector3.zero, 0f, new Vector3(0, 0, 30)), b = Car(new Vector3(0, 0, p.LengthM - 0.2f + 2f * oz), 180f, new Vector3(0, 0, -30));
            ContactResult c = VehicleContact.Resolve(ref a, p, ref b, p);
            Assert.That(c.Touching, Is.True);
            Assert.That(Mathf.Abs(30f - a.Velocity.z), Is.LessThanOrEqualTo(VehicleContact.MaxDeltaV + 1e-4f));
            Assert.That(Mathf.Abs(-30f - b.Velocity.z), Is.LessThanOrEqualTo(VehicleContact.MaxDeltaV + 1e-4f));
            Assert.That(a.Velocity.y, Is.EqualTo(0f), "contact never lifts a car");
        }

        [Test]
        public void SideSwipe_PushesApartSideways_WithBoundedYaw()
        {
            VehicleParams p = Params("V01");
            VehicleState a = Car(Vector3.zero, 0f, new Vector3(1.5f, 0, 25)), b = Car(new Vector3(p.WidthM - 0.15f, 0, 0.6f), 0f, new Vector3(-1.5f, 0, 25));
            ContactResult c = VehicleContact.Resolve(ref a, p, ref b, p);
            Assert.That(c.Touching, Is.True);
            Assert.That(Mathf.Abs(c.Normal.x), Is.GreaterThan(0.99f));
            Assert.That(a.Velocity.x, Is.LessThan(1.5f));
            Assert.That(b.Velocity.x, Is.GreaterThan(-1.5f));
            Assert.That(Mathf.Abs(a.AngularVelocity.y), Is.LessThanOrEqualTo(VehicleContact.MaxYawDelta + 1e-4f));
            Assert.That(Mathf.Abs(b.AngularVelocity.y), Is.LessThanOrEqualTo(VehicleContact.MaxYawDelta + 1e-4f));
            Assert.That(a.Velocity.z, Is.EqualTo(25f).Within(1.5f), "a light side bump barely changes forward speed");
        }

        [Test]
        public void SeparatingCars_GetNoImpulse_OnlySeparation()
        {
            VehicleParams p = Params("V01");
            VehicleState a = Car(Vector3.zero, 0f, new Vector3(0, 0, 20)), b = Car(new Vector3(0, 0, p.LengthM - 0.3f), 0f, new Vector3(0, 0, 25));
            ContactResult c = VehicleContact.Resolve(ref a, p, ref b, p);
            Assert.That(c.Touching, Is.True);
            Assert.That(c.Impulse, Is.EqualTo(0f));
            Assert.That(a.Velocity.z, Is.EqualTo(20f));
            Assert.That(b.Position.z - a.Position.z, Is.GreaterThan(p.LengthM - 0.3f));
            Assert.That(b.Position.z - a.Position.z - (p.LengthM - 0.3f), Is.LessThanOrEqualTo(VehicleContact.MaxSeparationPerTick + 1e-4f));
        }

        [Test]
        public void OneSidedPrediction_MatchesTheServersResponseForOwnCar()
        {
            VehicleParams p = Params("V01"), q = Params("V11");
            VehicleState a = Car(Vector3.zero, 0f, new Vector3(0, 0, 27)), b = Car(new Vector3(0.4f, 0, p.LengthM - 0.1f), 5f, new Vector3(0, 0, 21));
            VehicleState serverA = a, serverB = b, clientA = a, clientB = b;
            VehicleContact.Resolve(ref serverA, p, ref serverB, q);
            VehicleContact.Resolve(ref clientA, p, ref clientB, q, true, false);
            Assert.That(clientA.Position, Is.EqualTo(serverA.Position));
            Assert.That(clientA.Velocity, Is.EqualTo(serverA.Velocity));
            Assert.That(clientB.Velocity, Is.EqualTo(b.Velocity), "the client never writes the remote car's state");
        }

        [Test]
        public void Resolution_IsDeterministic()
        {
            VehicleParams p = Params("V03"), q = Params("V09");
            VehicleState a1 = Car(new Vector3(0.1f, 0, 0), 3f, new Vector3(1, 0, 26)), b1 = Car(new Vector3(1.2f, 0, 3.9f), -4f, new Vector3(-1, 0, 22));
            VehicleState a2 = a1, b2 = b1;
            for (int i = 0; i < 20; i++)
            {
                VehicleContact.Resolve(ref a1, p, ref b1, q);
                VehicleContact.Resolve(ref a2, p, ref b2, q);
            }
            Assert.That(a1.Position, Is.EqualTo(a2.Position));
            Assert.That(b1.AngularVelocity, Is.EqualTo(b2.AngularVelocity));
        }

        [Test]
        public void DifferentHeights_DoNotCollide()
        {
            VehicleParams p = Params("V01");
            VehicleState a = Car(Vector3.zero, 0f, Vector3.zero), b = Car(new Vector3(0, 6f, 0), 0f, Vector3.zero); // e.g. a bridge above
            Assert.That(VehicleContact.Overlapping(a, p, b, p), Is.False);
        }
    }
}
