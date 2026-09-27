using System.Collections.Generic;
using System.IO;
using NightSignal.Art;
using NightSignal.Cameras;
using NightSignal.Content;
using NightSignal.UI;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// Addendum 03 §2–4 camera behaviour with a synthetic clock (C04–C08, C10) and the per-car anchors behind the views
    /// (supporting C01–C03 only: a valid anchor does not prove the view — the driven contact sheet does). Preferences
    /// live in a throw-away folder; nothing touches the player's own settings.
    /// </summary>
    public sealed class CameraTests
    {
        const float Dt = 1f / 60f;
        string folder;
        readonly List<Object> made = new List<Object>();
        ContentLibrary lib;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "ns-camera-" + System.Guid.NewGuid().ToString("N"));
            DrivingPreferences.FolderOverride = folder;
            DrivingPreferences.ResetCache();
            lib = ContentLibrary.Load();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
            DrivingPreferences.FolderOverride = null;
            DrivingPreferences.ResetCache();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        VehicleView Car(string carId)
        {
            VehicleParams p = lib.Params(carId, AssistSettings.Default);
            VehicleView view = VehicleView.Create("CameraTestCar", p, lib.Body(carId), Resources.Load<CarMaterialSet>("CarMaterialSet"), Color.red);
            made.Add(view.gameObject);
            view.transform.SetPositionAndRotation(new Vector3(0f, p.CgHeightM + 500f, 0f), Quaternion.identity); // clear of any scene collider
            VehicleState rest = VehicleState.AtRest(view.transform.position, view.transform.rotation);
            view.Render(rest, rest, 1f, default, 0f);
            return view;
        }

        DrivingCamera Rig(VehicleView car, DrivingView v)
        {
            var go = new GameObject("CameraTestRig", typeof(Camera));
            made.Add(go);
            DrivingCamera cam = go.AddComponent<DrivingCamera>();
            cam.SetView(v, save: false);
            cam.SetTarget(car);
            return cam;
        }

        static string FirstCar(ContentLibrary lib) => lib.Catalogue.Cars[0].Id;

        [Test]
        public void C06_AtRestNothingMoves_InEveryView()
        {
            VehicleView car = Car(FirstCar(lib));
            foreach (DrivingView v in System.Enum.GetValues(typeof(DrivingView)))
            {
                DrivingCamera cam = Rig(car, v);
                cam.SetMotion(new CameraMotion());
                cam.Step(Dt);
                Vector3 p0 = cam.transform.position;
                Quaternion r0 = cam.transform.rotation;
                float fov0 = cam.Camera.fieldOfView;
                for (int i = 0; i < 180; i++) cam.Step(Dt);
                Assert.That(Vector3.Distance(cam.transform.position, p0), Is.LessThan(1e-4f), $"{v}: no idle drift of position");
                Assert.That(Quaternion.Angle(cam.transform.rotation, r0), Is.LessThan(1e-3f), $"{v}: no idle wobble");
                Assert.That(cam.Camera.fieldOfView, Is.EqualTo(fov0).Within(1e-4f), $"{v}: no idle zoom");
            }
        }

        [Test]
        public void C10_RepeatedWallContactStaysBoundedAndDecays()
        {
            VehicleView car = Car(FirstCar(lib));
            DrivingCamera cam = Rig(car, DrivingView.ChaseClose);
            float peak = 0f;
            // Five seconds grinding a wall: a fresh "impact" reported every frame.
            for (int i = 0; i < 300; i++)
            {
                cam.SetMotion(new CameraMotion { Speed = 20f, ImpactSpeed = 25f, ImpactNormal = Vector3.right });
                cam.Step(Dt);
                peak = Mathf.Max(peak, cam.ImpactOffset);
            }
            Assert.That(peak, Is.GreaterThan(0.01f), "an impact is felt at Arcade strength");
            Assert.That(peak, Is.LessThanOrEqualTo(0.4f + 1e-4f), "never accumulates past the cap");
            cam.SetMotion(new CameraMotion { Speed = 20f });
            for (int i = 0; i < 60; i++) cam.Step(Dt);
            Assert.That(cam.ImpactOffset, Is.LessThan(0.01f), "decays within a second once contact ends");

            DrivingPreferences prefs = DrivingPreferences.Current;
            prefs.MotionPreset = "comfort";
            cam.NotifyTeleport();
            for (int i = 0; i < 60; i++)
            {
                cam.SetMotion(new CameraMotion { Speed = 20f, ImpactSpeed = 25f, ImpactNormal = Vector3.right });
                cam.Step(Dt);
                Assert.That(cam.ImpactOffset, Is.EqualTo(0f), "Comfort: no impact response at all");
            }
        }

        [Test]
        public void C06_DriftFramingHasHysteresis_LinkedDriftsPassThroughNeutral_NoChatter()
        {
            VehicleView car = Car(FirstCar(lib));
            DrivingCamera cam = Rig(car, DrivingView.ChaseClose);

            // A 0.1 s flick is not a drift.
            for (int i = 0; i < 6; i++) { cam.SetMotion(new CameraMotion { Speed = 30f, SlipDeg = 30f }); cam.Step(Dt); }
            for (int i = 0; i < 30; i++) { cam.SetMotion(new CameraMotion { Speed = 30f }); cam.Step(Dt); }
            Assert.That(Mathf.Abs(cam.DriftFraming), Is.LessThan(0.02f), "a brief flick does not frame");

            // Frame-to-frame slip noise at the threshold (the "random side chatter" case).
            for (int i = 0; i < 180; i++) { cam.SetMotion(new CameraMotion { Speed = 30f, SlipDeg = i % 2 == 0 ? 14f : -14f }); cam.Step(Dt); }
            Assert.That(Mathf.Abs(cam.DriftFraming), Is.LessThan(0.02f), "alternating noise never frames either side");

            // Linked drift: 1.5 s right, then 1.5 s left.
            float heldRight = 0f;
            for (int i = 0; i < 90; i++) { cam.SetMotion(new CameraMotion { Speed = 30f, SlipDeg = 30f }); cam.Step(Dt); heldRight = cam.DriftFraming; }
            Assert.That(heldRight, Is.GreaterThan(0.3f), "a sustained right drift frames to one side");
            int sideChanges = 0;
            float last = heldRight, atChange = 1f;
            for (int i = 0; i < 90; i++)
            {
                cam.SetMotion(new CameraMotion { Speed = 30f, SlipDeg = -30f });
                cam.Step(Dt);
                float now = cam.DriftFraming;
                if (Mathf.Sign(now) != Mathf.Sign(last) && Mathf.Abs(now) > 1e-4f) { sideChanges++; atChange = Mathf.Abs(last); }
                if (Mathf.Abs(now) > 1e-4f) last = now;
            }
            Assert.That(sideChanges, Is.EqualTo(1), "one clean change of side");
            Assert.That(atChange, Is.LessThan(0.03f), "the side changes only after the framing has released (no jump across)");
            Assert.That(cam.DriftFraming, Is.LessThan(-0.3f), "then frames the new side");

            // Airborne and slow never frame.
            cam.NotifyTeleport();
            for (int i = 0; i < 90; i++) { cam.SetMotion(new CameraMotion { Speed = 30f, SlipDeg = 30f, Airborne = true }); cam.Step(Dt); }
            Assert.That(cam.DriftFraming, Is.EqualTo(0f), "no framing in the air");
            for (int i = 0; i < 90; i++) { cam.SetMotion(new CameraMotion { Speed = 4f, SlipDeg = 60f }); cam.Step(Dt); }
            Assert.That(cam.DriftFraming, Is.EqualTo(0f), "no framing for a spin at walking pace");
        }

        [Test]
        public void C05_TeleportClearsHistoryAndCutsToTheNewPose()
        {
            VehicleView car = Car(FirstCar(lib));
            DrivingCamera cam = Rig(car, DrivingView.ChaseFar);
            for (int i = 0; i < 90; i++) { cam.SetMotion(new CameraMotion { Speed = 30f, SlipDeg = 30f, ImpactSpeed = 20f, ImpactNormal = Vector3.left }); cam.Step(Dt); }
            Vector3 rel = cam.transform.position - car.transform.position;
            car.transform.position += new Vector3(400f, 20f, -300f);
            cam.NotifyTeleport();
            Assert.That(cam.DriftFraming, Is.EqualTo(0f));
            Assert.That(cam.ImpactOffset, Is.EqualTo(0f));
            cam.SetMotion(new CameraMotion());
            cam.Step(Dt);
            float distance = Vector3.Distance(cam.transform.position, car.transform.position);
            Assert.That(distance, Is.LessThan(rel.magnitude + 1f), "the camera is at the car on the first frame, not flying across the course");
        }

        [Test]
        public void C04_CycleOrderPersistence_TemporaryOverride_AndLookBack()
        {
            VehicleView car = Car(FirstCar(lib));
            DrivingCamera cam = Rig(car, DrivingView.ChaseClose);
            var seen = new List<DrivingView>();
            for (int i = 0; i < 5; i++) { cam.Cycle(); cam.Step(Dt); seen.Add(cam.View); }
            CollectionAssert.AreEqual(new[] { DrivingView.ChaseFar, DrivingView.Hood, DrivingView.Bumper, DrivingView.Cockpit, DrivingView.ChaseClose }, seen);
            cam.Cycle();
            DrivingPreferences.ResetCache();
            Assert.That(DrivingPreferences.Current.View, Is.EqualTo("chase-far"), "the cycled view is saved and survives a reload");

            cam.SetView(DrivingView.Cockpit, save: false); // e.g. a replay/cinematic override
            DrivingPreferences.ResetCache();
            Assert.That(DrivingPreferences.Current.View, Is.EqualTo("chase-far"), "a temporary override does not overwrite the preference");

            cam.SetView(DrivingView.Hood, save: false);
            cam.LookBack = true;
            cam.Step(Dt);
            Vector3 back = cam.transform.forward;
            cam.LookBack = false;
            cam.Step(Dt);
            Assert.That(cam.View, Is.EqualTo(DrivingView.Hood), "releasing look-back restores the view");
            Assert.That(Vector3.Dot(back, car.transform.forward), Is.LessThan(-0.5f), "look-back looks behind");
            Assert.That(Vector3.Dot(cam.transform.forward, car.transform.forward), Is.GreaterThan(0.9f), "and forward again on release");
        }

        [Test]
        public void C07_PresetsChangeObservableMotion_ReducedMotionWins()
        {
            VehicleView car = Car(FirstCar(lib));
            float Framing(string preset, bool reduced)
            {
                DrivingPreferences prefs = DrivingPreferences.Current;
                prefs.MotionPreset = preset;
                prefs.ReducedMotion = reduced;
                DrivingCamera cam = Rig(car, DrivingView.ChaseClose);
                for (int i = 0; i < 120; i++) { cam.SetMotion(new CameraMotion { Speed = 40f, SlipDeg = 30f, LateralG = 1f }); cam.Step(Dt); }
                return Mathf.Abs(cam.DriftFraming);
            }
            float Fov(string preset)
            {
                DrivingPreferences.Current.MotionPreset = preset;
                DrivingPreferences.Current.ReducedMotion = false;
                DrivingCamera cam = Rig(car, DrivingView.ChaseClose);
                for (int i = 0; i < 240; i++) { cam.SetMotion(new CameraMotion { Speed = 60f }); cam.Step(Dt); }
                return cam.Camera.fieldOfView;
            }
            Assert.That(Framing("arcade", false), Is.GreaterThan(0.3f));
            Assert.That(Framing("comfort", false), Is.EqualTo(0f), "Comfort: no drift framing");
            Assert.That(Framing("arcade", true), Is.EqualTo(0f), "Reduced Motion overrides Arcade");
            DrivingPreferences.Current.Custom = new MotionStrengths { DriftFraming = 0.25f };
            float custom = Framing("custom", false);
            Assert.That(custom, Is.GreaterThan(0.05f).And.LessThan(Framing("arcade", false)), "Custom strength scales it");
            Assert.That(Fov("arcade"), Is.GreaterThan(DrivingPreferences.DefaultFov + 1f), "speed FOV widens at Arcade");
            Assert.That(Fov("comfort"), Is.EqualTo(DrivingPreferences.DefaultFov).Within(0.01f), "and not at Comfort");
        }

        [Test]
        public void C08_SpeedLinesArePeripheral_AndOnlyForRealSpeed()
        {
            var canvasGo = new GameObject("SpeedLinesTest", typeof(RectTransform), typeof(Canvas));
            made.Add(canvasGo);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace; // a fixed 1920×1080 rect in edit mode
            ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(1920f, 1080f);
            SpeedLines lines = SpeedLines.CreateOn(canvasGo.GetComponent<Canvas>());
            for (int i = 0; i < 120; i++) lines.Render(0f, 0f, 2, Dt); // standstill wheelspin: road speed is 0
            Assert.That(lines.Strength, Is.LessThan(0.01f), "no lines at a standstill");
            for (int i = 0; i < 120; i++) lines.Render(20f, 0f, 2, Dt);
            Assert.That(lines.Strength, Is.LessThan(0.01f), "none at ordinary speed");
            for (int i = 0; i < 180; i++) lines.Render(65f, 0f, 1, Dt);
            float subtle = lines.Strength;
            for (int i = 0; i < 180; i++) lines.Render(65f, 0f, 2, Dt);
            float strong = lines.Strength;
            for (int i = 0; i < 180; i++) lines.Render(65f, 1f, 2, Dt);
            float drifting = lines.Strength;
            Assert.That(subtle, Is.GreaterThan(0.05f));
            Assert.That(strong, Is.GreaterThan(subtle), "Strong is stronger than Subtle");
            Assert.That(drifting, Is.GreaterThan(strong), "a valid fast drift strengthens them");
            Assert.That(lines.AllOutsideClearCentre(), Is.True, "every visible streak stays outside the central 70 %");
            Assert.That(SpeedLines.SortOrder, Is.LessThan(10), "below the race HUD canvas (order 10)");
            for (int i = 0; i < 180; i++) lines.Render(65f, 1f, 0, Dt);
            Assert.That(lines.Strength, Is.LessThan(0.01f), "Off is off");
        }

        [Test]
        public void C01_AnchorsSitInsideEachCarsOwnBody_AllCars()
        {
            var failures = new List<string>();
            foreach (var def in lib.Catalogue.Cars)
            {
                VehicleParams p = lib.Params(def.Id, AssistSettings.Default);
                CarBodyGenerator.CabinFrame f = CarBodyGenerator.Cabin(lib.Body(def.Id), p);
                Vector3 e = f.Eye;
                if (Mathf.Abs(e.x) + 0.12f > f.InteriorHalfWidth(e.z)) failures.Add($"{def.Id}: eye outside the cabin side");
                if (e.x * f.DriverSide <= 0.05f) failures.Add($"{def.Id}: eye not over the driver's seat");
                if (e.y < f.BeltY(e.z) - 0.05f) failures.Add($"{def.Id}: eye below the belt line");
                if (!f.OpenTop && e.y + 0.08f > f.RoofUndersideY(e.z)) failures.Add($"{def.Id}: eye in the roof");
                if (e.z > f.WindshieldBaseZ || e.z < f.RearWindowZ) failures.Add($"{def.Id}: eye outside the glasshouse");
                if (f.Hood.y <= f.CowlY || f.Hood.z > f.NoseZ) failures.Add($"{def.Id}: hood camera not above the bonnet");
                if (f.Bumper.z < f.NoseZ || f.Bumper.y < 0.3f) failures.Add($"{def.Id}: bumper camera not ahead of the nose / above the road");
                if (f.Hood.y - f.Bumper.y < 0.3f || f.Bumper.z - f.Hood.z < 0.5f) failures.Add($"{def.Id}: Hood and Bumper are not distinct viewpoints");
                if (e.z > f.Hood.z) failures.Add($"{def.Id}: cockpit eye ahead of the hood camera");
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
            Assert.That(lib.Catalogue.Cars.Count, Is.EqualTo(18));
        }
    }
}
