using System.IO;
using NightSignal.UI;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// Addendum 03 §1 (G02, G04, G07, G08): canonical speed conversion and the dial/strip mapping, the road-speed telemetry
    /// that feeds them, preference defaults/migration/corruption, and a cluster that does not grow on repeated changes.
    /// </summary>
    public sealed class InstrumentTests
    {
        [Test]
        public void G02_ConversionsAtTheSpecifiedSpeeds()
        {
            float[] mps = { 0f, 10f, 26.8224f, 100f };
            float[] kmh = { 0f, 36f, 96.56064f, 360f };
            float[] mph = { 0f, 22.36936f, 60f, 223.69363f };
            for (int i = 0; i < mps.Length; i++)
            {
                Assert.That(SpeedDisplay.Convert(mps[i], SpeedUnit.Kmh), Is.EqualTo(kmh[i]).Within(1e-3f), $"{mps[i]} m/s in km/h");
                Assert.That(SpeedDisplay.Convert(mps[i], SpeedUnit.Mph), Is.EqualTo(mph[i]).Within(1e-3f), $"{mps[i]} m/s in mph");
            }
            Assert.That(SpeedDisplay.Format(26.8224f, SpeedUnit.Kmh), Is.EqualTo("97"));
            Assert.That(SpeedDisplay.Format(26.8224f, SpeedUnit.Mph), Is.EqualTo("60"));
            Assert.That(SpeedDisplay.Convert(-12f, SpeedUnit.Kmh), Is.EqualTo(43.2f).Within(1e-3f), "reverse reads as its magnitude");
            Assert.That(SpeedDisplay.Convert(float.NaN, SpeedUnit.Kmh), Is.EqualTo(0f), "non-finite never reaches the display");
        }

        [Test]
        public void G02_NeedleAndScaleAgreeInBothUnits_AndOverRangeHoldsTheEndStop()
        {
            var go = new GameObject("InstrumentTestCanvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var cluster = new SpeedCluster(go.transform);
                foreach (SpeedUnit unit in new[] { SpeedUnit.Kmh, SpeedUnit.Mph })
                {
                    SpeedDisplay.Scale scale = SpeedDisplay.ScaleFor(60f, unit);
                    Assert.That(scale.Max % scale.Major, Is.EqualTo(0f), $"{scale}: a whole number of major intervals");
                    cluster.Configure(true, scale);
                    cluster.Render(26.8224f, 3000f, 7000f, 3, true, 1f);
                    float expected = 125f - 250f * (SpeedDisplay.Convert(26.8224f, unit) / scale.Max);
                    Assert.That(Mathf.DeltaAngle(cluster.NeedleAngle, expected), Is.EqualTo(0f).Within(0.01f), $"{unit}: needle at the true physical speed");
                    Assert.That(cluster.ShownNumber, Is.EqualTo(unit == SpeedUnit.Kmh ? "97" : "60"));
                    cluster.ResetFilter();
                    cluster.Render(scale.Max * 2f, 3000f, 7000f, 6, true, 1f); // wildly over range (in m/s)
                    Assert.That(Mathf.DeltaAngle(cluster.NeedleAngle, -125f), Is.EqualTo(0f).Within(0.01f), "over range: the needle rests at the end stop, never wraps");
                    Assert.That(cluster.ShownNumber, Is.EqualTo(SpeedDisplay.Format(scale.Max * 2f, unit)), "the number keeps the true value");
                }
                Assert.That(SpeedDisplay.ScaleFor(60f, SpeedUnit.Kmh), Is.EqualTo(SpeedDisplay.ScaleFor(60f, SpeedUnit.Kmh)), "the scale is stable for a car");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void G04_RoadSpeedTelemetry_StandstillSidewaysReverseAndFall()
        {
            var p = new VehicleParams();
            var sim = new VehicleSimulation(p, PlaneVehicleWorld.Flat);
            VehicleState s = VehicleState.AtRest(new Vector3(0f, p.CgHeightM, 0f), Quaternion.identity);
            for (int i = 0; i < 30; i++) sim.Step(ref s, DriverInput.Quantize(0f, 1f, 1f, InputButtons.Handbrake));
            Assert.That(sim.Telemetry.RoadSpeedMps, Is.LessThan(1f), "revving at a standstill is not road speed");

            s = VehicleState.AtRest(new Vector3(0f, p.CgHeightM, 0f), Quaternion.identity);
            sim.Step(ref s, DriverInput.Neutral);
            s.Velocity = Vector3.right * 20f; // fully sideways
            sim.Step(ref s, DriverInput.Neutral);
            Assert.That(sim.Telemetry.RoadSpeedMps, Is.GreaterThan(18f), "a sideways slide keeps its speed");

            s.Velocity = Vector3.back * 10f;
            sim.Step(ref s, DriverInput.Neutral);
            Assert.That(sim.Telemetry.RoadSpeedMps, Is.EqualTo(10f).Within(1f), "reverse reads its magnitude");

            // Off the ground: forward speed keeps reading on a crest, a vertical fall does not become road speed.
            s.Position += Vector3.up * 30f;
            s.Velocity = new Vector3(0f, -20f, 0f);
            sim.Step(ref s, DriverInput.Neutral);
            Assert.That(sim.Telemetry.Airborne, Is.True);
            Assert.That(sim.Telemetry.RoadSpeedMps, Is.LessThan(1f), "falling is not road speed");
            s.Velocity = new Vector3(0f, -3f, 30f);
            sim.Step(ref s, DriverInput.Neutral);
            Assert.That(sim.Telemetry.RoadSpeedMps, Is.EqualTo(30f).Within(1f), "a crest's airtime keeps reading");
        }

        [Test]
        public void G07_PreferencesDefaultsRoundTripCorruptionAndReducedMotion()
        {
            string folder = Path.Combine(Path.GetTempPath(), "ns-prefs-" + System.Guid.NewGuid().ToString("N"));
            DrivingPreferences.FolderOverride = folder;
            try
            {
                DrivingPreferences fresh = DrivingPreferences.Load();
                Assert.That(fresh.Dial && fresh.Unit == SpeedUnit.Kmh && fresh.View == "chase-close" && fresh.MotionPreset == "arcade", Is.True, "documented defaults");

                fresh.SpeedStyle = "strip";
                fresh.Units = "mph";
                fresh.View = "cockpit";
                fresh.MotionPreset = "custom";
                fresh.Custom = new MotionStrengths { DriftFraming = 0.3f, SpeedLines = 2 };
                Assert.That(fresh.Save(), Is.True);
                DrivingPreferences back = DrivingPreferences.Load();
                Assert.That(!back.Dial && back.Unit == SpeedUnit.Mph && back.View == "cockpit", Is.True, "explicit choices survive a restart");
                Assert.That(back.Effective.DriftFraming, Is.EqualTo(0.3f).Within(1e-4f));

                back.MotionPreset = "comfort";
                Assert.That(back.Effective.SpeedLines, Is.EqualTo(0), "Comfort: no lines");
                Assert.That(back.Custom.SpeedLines, Is.EqualTo(2), "custom values survive switching presets");
                back.MotionPreset = "arcade";
                back.ReducedMotion = true;
                Assert.That(back.Effective.ImpactShake, Is.EqualTo(0f), "Reduced Motion always wins");

                File.WriteAllText(DrivingPreferences.FilePath, "{ not json");
                DrivingPreferences corrupt = DrivingPreferences.Load();
                Assert.That(corrupt.Dial && corrupt.Unit == SpeedUnit.Kmh, Is.True, "a corrupt file fails safe to defaults");
                Assert.That(File.Exists(DrivingPreferences.FilePath + ".bad"), Is.True, "and is kept aside");

                File.WriteAllText(DrivingPreferences.FilePath, "{\"SpeedStyle\":\"hologram\",\"Units\":\"furlongs\",\"View\":\"drone\",\"VerticalFov\":400}");
                DrivingPreferences odd = DrivingPreferences.Load();
                Assert.That(odd.Dial && odd.Unit == SpeedUnit.Kmh && odd.View == "chase-close", Is.True, "unknown values fall back field by field");
                Assert.That(odd.VerticalFov, Is.EqualTo(DrivingPreferences.MaxFov));
            }
            finally
            {
                DrivingPreferences.FolderOverride = null;
                DrivingPreferences.ResetCache();
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Test]
        public void G08_RepeatedStyleAndUnitChanges_DoNotGrowTheCluster()
        {
            var go = new GameObject("InstrumentTestCanvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var cluster = new SpeedCluster(go.transform);
                SpeedDisplay.Scale kmh = SpeedDisplay.ScaleFor(60f, SpeedUnit.Kmh), mph = SpeedDisplay.ScaleFor(60f, SpeedUnit.Mph);
                cluster.Configure(true, kmh);
                int dial = cluster.ObjectCount;
                for (int i = 0; i < 20; i++)
                {
                    cluster.Configure(false, i % 2 == 0 ? kmh : mph);
                    cluster.Configure(true, i % 2 == 0 ? mph : kmh);
                }
                cluster.Configure(true, kmh);
                Assert.That(cluster.ObjectCount, Is.EqualTo(dial), "no accumulated objects");
                for (int i = 0; i < 100; i++) cluster.Render(i * 0.5f, 3000f, 7000f, 3, true, 1f / 60f);
                Assert.That(cluster.ObjectCount, Is.EqualTo(dial), "rendering adds nothing");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
