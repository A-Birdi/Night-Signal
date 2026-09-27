using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NightSignal.UI;
using NightSignal.Vehicle;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Instrument extremes in a built Test Yard session (<c>-nsInstrumentExtremes</c>, Addendum 03 G04/G05): scripted
        /// inputs through standstill wheelspin (handbrake + full throttle), a full-throttle launch through the gears,
        /// braking to rest and reversing, a reset at speed, and a handbrake slide on the skid pad — sampling every frame
        /// what the Instrument Dial actually shows (number and needle) against the simulation's road speed. Checks: the
        /// number is the road speed (never wheel speed, never negative, never NaN), a gearshift never jumps it, the needle
        /// never leaves its sweep, and a reset shows the new car at once (no stale speed). Isolated preferences/profiles.
        /// </summary>
        IEnumerator InstrumentExtremes()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "instrument-extremes"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            var csv = new StringBuilder("phase,t,roadKmh,shown,needleDeg,gear,rpm,slipDeg,airborne\n");
            void Note(string s) => Debug.Log("[NightSignal.InstrumentExtremes] " + s);
            void Shot(string name) => ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { failures.Add("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }

            yield return new WaitForSeconds(3f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName").GetComponent<TMP_InputField>().text = "Gauge Tester";
            Click("Create");
            yield return new WaitForSeconds(1.2f);
            Click("Garage");
            yield return new WaitForSeconds(1.5f);
            Click("TestYardA");
            float until = Time.realtimeSinceStartup + 40f;
            while ((ActiveYard == null || !ActiveYard.Ready || ActiveYard.Hud == null) && Time.realtimeSinceStartup < until) yield return null;
            if (ActiveYard == null || ActiveYard.Hud == null) { Note("FAILED: the yard did not open"); Application.Quit(1); yield break; }
            SpeedCluster cluster = ActiveYard.Hud.Cluster;
            if (!DrivingPreferences.Current.Dial) { DrivingPreferences.Current.SpeedStyle = "dial"; DrivingPreferences.Current.Save(); }

            string phase = "";
            float phaseStart = 0f;
            int badNumber = 0, negative = 0, wrapped = 0, nan = 0, samples = 0;
            float lastShown = -1f, lastRoad = 0f;
            int lastGear = int.MinValue, shifts = 0, shiftJumps = 0;
            float maxWheelspinShown = 0f, maxWheelspinRpm = 0f, maxReverseShown = 0f;
            // One sample per rendered frame (after the HUD has drawn it).
            IEnumerator Watch(float seconds, System.Func<bool> stop = null)
            {
                float end = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < end && (stop == null || !stop()))
                {
                    yield return new WaitForEndOfFrame();
                    StepTelemetry t = ActiveYard.Telemetry;
                    VehicleState s = ActiveYard.State;
                    float road = t.RoadSpeedMps * 3.6f;
                    string text = cluster.ShownNumber;
                    float needle = Mathf.DeltaAngle(0f, cluster.NeedleAngle);
                    samples++;
                    if (text.Contains("NaN") || text == "—") nan++;
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int shown)) { badNumber++; continue; }
                    if (shown < 0) negative++;
                    if (needle > 125.5f || needle < -125.5f) wrapped++;
                    if (Mathf.Abs(shown - road) > 1.01f) badNumber++;
                    if (phase == "launch" && lastGear != int.MinValue && s.Gear != lastGear)
                    {
                        shifts++;
                        if (lastShown >= 0f && Mathf.Abs(shown - lastShown) > Mathf.Abs(road - lastRoad) + 2f) shiftJumps++;
                    }
                    if (phase == "wheelspin") { maxWheelspinShown = Mathf.Max(maxWheelspinShown, shown); maxWheelspinRpm = Mathf.Max(maxWheelspinRpm, s.EngineRpm); }
                    if (phase == "reverse" && s.Gear < 0) maxReverseShown = Mathf.Max(maxReverseShown, shown);
                    lastGear = s.Gear;
                    lastShown = shown;
                    lastRoad = road;
                    if (samples % 3 == 0)
                        csv.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:F2},{2:F1},{3},{4:F1},{5},{6:F0},{7:F1},{8}",
                            phase, Time.realtimeSinceStartup - phaseStart, road, shown, needle, s.Gear, s.EngineRpm, t.BodySlipDeg, t.Airborne));
                }
            }
            void Phase(string name) { phase = name; phaseStart = Time.realtimeSinceStartup; lastGear = int.MinValue; lastShown = -1f; }

            // 1) Standstill wheelspin: handbrake held, full throttle.
            ActiveYard.Script = (st, t) => DriverInput.Quantize(0f, 1f, 0f, InputButtons.Handbrake);
            ActiveYard.ResetAndDrive(false, 0);
            yield return new WaitForSeconds(0.5f);
            Phase("wheelspin");
            yield return Watch(2.5f);
            Shot("01-wheelspin");
            Note($"standstill wheelspin: shown ≤ {maxWheelspinShown:0} km/h while the engine reached {maxWheelspinRpm:0} rpm");
            if (maxWheelspinShown > 2f) failures.Add($"wheelspin showed {maxWheelspinShown} km/h at a standstill");

            // 2) Full-throttle launch through the gears.
            ActiveYard.Script = (st, t) => DriverInput.Quantize(0f, 1f, 0f, InputButtons.None);
            ActiveYard.ResetAndDrive(false, 0);
            Phase("launch");
            yield return Watch(9f);
            Shot("02-launch");
            Note($"launch: {shifts} gear changes, {shiftJumps} displayed jumps at a shift beyond the physical change");
            if (shifts < 2) failures.Add($"launch saw only {shifts} gear changes");
            if (shiftJumps > 0) failures.Add($"{shiftJumps} gearshifts jumped the displayed speed");

            // 3) Reset at speed: the next frames show the new car, not a glide down from the old speed.
            float before = ActiveYard.Telemetry.RoadSpeedMps * 3.6f;
            ActiveYard.ResetAndDrive(false, 0);
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            int.TryParse(cluster.ShownNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out int afterShown);
            float afterNeedle = Mathf.DeltaAngle(0f, cluster.NeedleAngle);
            Note($"reset at {before:0} km/h: two frames later shown {afterShown} km/h, needle {afterNeedle:F1}° (zero is 125°)");
            if (afterShown > 3 || afterNeedle < 120f) failures.Add($"reset showed stale speed ({afterShown} km/h, needle {afterNeedle:F1}°)");

            // 4) Brake to rest, keep the brake held: reverse engages; the number shows the magnitude, never a minus.
            bool stopped = false;
            ActiveYard.Script = (st, t) =>
            {
                if (st.SpeedKmh < 0.5f) stopped = true;
                return DriverInput.Quantize(0f, 0f, 1f, InputButtons.None);
            };
            ActiveYard.ResetAndDrive(false, 0);
            Phase("reverse");
            yield return Watch(7f);
            Shot("03-reverse");
            Note($"reverse: gear {ActiveYard.State.Gear}, shown up to {maxReverseShown:0} km/h going backwards");
            if (maxReverseShown <= 0f) Note("reverse did not engage within 7 s (not a display failure)");

            // 5) Handbrake slide on the skid pad: the number follows the road speed of the sliding car.
            float slipMax = 0f;
            ActiveYard.Script = (st, t) =>
            {
                bool flick = t > 3f && (t % 2.5f) < 0.35f;
                return DriverInput.Quantize(0.8f, st.SpeedKmh < 55f ? 0.9f : 0.5f, 0f, flick ? InputButtons.Handbrake : InputButtons.None);
            };
            ActiveYard.ResetAndDrive(false, 1);
            Phase("slide");
            float slideEnd = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < slideEnd)
            {
                yield return Watch(0.5f);
                slipMax = Mathf.Max(slipMax, Mathf.Abs(ActiveYard.Telemetry.BodySlipDeg));
            }
            Shot("04-slide");
            Note($"skid-pad slide: body slip up to {slipMax:0}°");

            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "samples.csv"), csv.ToString());
            Note($"{samples} frames sampled: {badNumber} where the number differed from the road speed by more than 1 km/h, {negative} negative, {wrapped} needle outside its sweep, {nan} NaN/blank");
            if (badNumber > 0) failures.Add($"{badNumber} frames showed a number other than the road speed");
            if (negative + wrapped + nan > 0) failures.Add($"negative {negative}, wrapped {wrapped}, NaN {nan}");
            ActiveYard.RequestExit();
            yield return new WaitForSeconds(2f);
            Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
            yield return new WaitForSeconds(1f);
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}
