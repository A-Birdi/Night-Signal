using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NightSignal.Cameras;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.UI;
using NightSignal.Vehicle;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        static readonly string[] TourCourses = { "C01", "C05", "C08", "C12", "C03", "C14" };
        const int TileW = 480, TileH = 270, CarsPerSheet = 6;

        /// <summary>
        /// Camera evidence run (<c>-nsCameraTour</c>, Addendum 03 C01/C03–C05/C08, A3.7): with isolated preferences and
        /// profiles it
        /// (1) opens the Garage Test Yard, cycles the view with a (virtual) keyboard <c>C</c> and a virtual controller's
        ///     Select, holds Look Back, swaps A/B, and shoots all five views in the yard;
        /// (2) for every one of the 18 cars starts a real solo race on one of six courses (day, night, wet, tunnel,
        ///     circuit) driven by the autopilot, and holds each of the five views for a few seconds while the car
        ///     drives, capturing a contact-sheet tile and a ledger row (camera pose in car space, FOV, speed, distance
        ///     driven in that view, drift framing, speed-line strength, frame rate and image statistics);
        /// (3) drives one car into the C08 tunnel and shoots all five views inside it.
        /// The view preference must survive every scene load. Automation evidence, not a human comfort judgement.
        /// </summary>
        IEnumerator CameraTour()
        {
            string[] tourArgs = System.Environment.GetCommandLineArgs();
            int outArg = System.Array.IndexOf(tourArgs, "-nsTourOut"); // separate folders for the frame-rate / FOV passes
            string outName = outArg >= 0 && outArg + 1 < tourArgs.Length ? tourArgs[outArg + 1] : "cameras";
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", outName));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            if (DrivingPreferences.FolderOverride == null)
            {
                DrivingPreferences.FolderOverride = System.IO.Path.Combine(dir, "prefs");
                DrivingPreferences.ResetCache();
            }
            // Automation drives virtual devices; they must be read even if the window is not in front.
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            var failures = new List<string>();
            void Note(string s) => Debug.Log("[NightSignal.CameraTour] " + s);
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { failures.Add("button not available: " + name); Note("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }
            var ledger = new StringBuilder("car,course,where,view,cam_x,cam_y,cam_z,fov,near,road_kmh,driven_m,drift_framing,collision_m,speed_lines,lines_peripheral,cockpit_visible,wheel_deg,luma_mean,luma_sd,dark_fraction,fps,blown_fraction\n");
            Canvas labelCanvas = UIFactory.Root("CameraTourLabel", 100);
            TextMeshProUGUI label = UIFactory.Label("Label", labelCanvas.transform, "", 40f, Color.white, TextAlignmentOptions.TopLeft);
            RectTransform lrt = label.rectTransform;
            lrt.anchorMin = new Vector2(0f, 1f); lrt.anchorMax = new Vector2(0.6f, 1f);
            lrt.offsetMin = new Vector2(24f, -140f); lrt.offsetMax = new Vector2(0f, -170f + 140f);
            label.outlineWidth = 0.25f;
            label.outlineColor = Color.black;
            Keyboard keyboard = Keyboard.current ?? InputSystem.AddDevice<Keyboard>();
            Gamepad pad = InputSystem.AddDevice<Gamepad>("CameraTourPad");

            // ------------------------------------------------------------------ 0. Settings -> Controls: remap Change View to V
            yield return new WaitForSeconds(3f);
            Click("Settings");
            yield return new WaitForSeconds(1.5f);
            Click("Controls");
            yield return new WaitForSeconds(1.5f);
            yield return new WaitForEndOfFrame();
            SaveJpg(System.IO.Path.Combine(dir, "controls-1-defaults.jpg"));
            Click("Camera/Keyboard");
            yield return new WaitForSeconds(0.4f);
            yield return TapKey(keyboard, Key.V);
            yield return new WaitForSeconds(0.6f);
            bool remapped = (DrivingPreferences.Current.BindingOverrides ?? "").Contains("<Keyboard>/v");
            Note($"Controls: Change View remapped through the screen to V: {remapped} (stored {DrivingPreferences.Current.BindingOverrides})");
            if (!remapped) failures.Add("the Controls screen did not remap Change View");
            yield return new WaitForEndOfFrame();
            SaveJpg(System.IO.Path.Combine(dir, "controls-2-view-on-v.jpg"));
            Key viewKey = remapped ? Key.V : Key.C;
            Click("Back");
            yield return new WaitForSeconds(1f);
            Click("Back");
            yield return new WaitForSeconds(1.2f);

            // ------------------------------------------------------------------ 1. Garage Test Yard
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName").GetComponent<TMP_InputField>().text = "Camera Driver";
            Click("Create");
            yield return new WaitForSeconds(1.2f);
            Click("Garage");
            yield return new WaitForSeconds(1.5f);
            Click("TestYardB");
            float until = Time.realtimeSinceStartup + 40f;
            while ((ActiveYard == null || !ActiveYard.Ready) && Time.realtimeSinceStartup < until) yield return null;
            if (ActiveYard == null || ActiveYard.Camera == null) { failures.Add("the yard did not open with a driving camera"); goto races; }
            {
                DrivingCamera cam = ActiveYard.Camera;
                ActiveYard.Script = (st, t) => DriverInput.Quantize(0.55f, st.SpeedKmh < 45f ? 0.7f : 0.25f, 0f, InputButtons.None);
                ActiveYard.ResetAndDrive(true, 1); // skid pad, candidate B
                yield return new WaitForSeconds(4f);
                // C04: cycling by the (remapped) keyboard key and by the controller's Select, in order, saved as the preference.
                var order = new List<string>();
                DrivingView startView = cam.View;
                if (viewKey != Key.C)
                {
                    yield return TapKey(keyboard, Key.C);
                    Note($"the old key C after remapping: view {startView} → {cam.View}");
                    if (cam.View != startView) failures.Add("C still changes the view after remapping it to V");
                }
                for (int i = 0; i < 5; i++)
                {
                    if (i % 2 == 0) yield return TapKey(keyboard, viewKey); else yield return TapPad(pad, GamepadButton.Select);
                    order.Add(cam.View.ToString());
                }
                bool cycled = cam != null && ActiveYard != null && cam.View == startView && order.Distinct().Count() == 5;
                Note($"yard view cycling (keyboard {viewKey} / controller Select alternately) from {startView}: {string.Join(" → ", order)}; saved {DrivingPreferences.Current.View}; yard still open {ActiveYard != null}");
                if (!cycled) failures.Add("keyboard/controller cycling did not visit all five views and return");
                if (cam == null || ActiveYard == null) { failures.Add("the yard closed while cycling views"); goto races; }
                // Typing in a text field must not switch the view (Addendum 03 §2).
                TMP_InputField typing = UIFactory.InputField("TourTyping", labelCanvas.transform, "typing test");
                typing.Select();
                typing.ActivateInputField();
                yield return new WaitForSeconds(0.2f);
                DrivingView beforeTyping = cam.View;
                yield return TapKey(keyboard, viewKey);
                bool focused = typing.isFocused;
                Note($"{viewKey} typed into a focused text field (focused {focused}, text '{typing.text}'): view {beforeTyping} → {cam.View}");
                if (!focused || cam.View != beforeTyping) failures.Add("typing C in a text field changed the view (or the field never took focus)");
                typing.DeactivateInputField();
                UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
                Destroy(typing.gameObject);
                yield return null;
                // Look Back hold (keyboard B), then release restores the view.
                cam.SetView(DrivingView.Hood, save: true);
                yield return null;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.B));
                yield return new WaitForSeconds(0.6f);
                bool heldBack = cam.LookBack;
                Vector3 backFwd = cam.transform.forward;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                yield return new WaitForSeconds(0.6f);
                Transform yardCar = cam.Target.transform;
                bool restored = !cam.LookBack && cam.View == DrivingView.Hood && Vector3.Dot(cam.transform.forward, yardCar.forward) > 0.8f;
                Note($"look back held: {heldBack} (forward·car {Vector3.Dot(backFwd, yardCar.forward):F2}); released → {cam.View}, forward·car {Vector3.Dot(cam.transform.forward, yardCar.forward):F2}");
                if (!heldBack || Vector3.Dot(backFwd, yardCar.forward) > -0.3f || !restored) failures.Add("look back hold/release did not work in the yard");
                // A/B swap and a fresh run keep the chosen view (C05).
                ActiveYard.ResetAndDrive(false, 1);
                yield return new WaitForSeconds(1f);
                if (cam.View != DrivingView.Hood) failures.Add("switching to A changed the view");
                ActiveYard.ResetAndDrive(true, 1);
                yield return new WaitForSeconds(3f);
                if (cam.View != DrivingView.Hood || cam.Target == null || !cam.Target.gameObject.activeInHierarchy) failures.Add("switching back to B lost the view or the target");
                foreach (DrivingView v in System.Enum.GetValues(typeof(DrivingView)))
                {
                    cam.SetView(v, save: false);
                    label.text = $"TEST YARD · {ActiveYard.CarId} B · {v}";
                    yield return new WaitForSeconds(2.2f);
                    yield return new WaitForEndOfFrame();
                    Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"yard-{(int)v}-{v}.jpg"), shot.EncodeToJPG(88));
                    Destroy(shot);
                    ledger.Append(Row(ActiveYard.CarId, "T00", "yard", cam, null, 0f, 0f, null));
                }
                cam.SetView(DrivingView.Hood, save: false);
                // Leave with the Pause/menu action (keyboard Esc), as a player would.
                yield return TapKey(keyboard, Key.Escape);
                if (ActiveYard != null && !ActiveYard.ExitRequested) { failures.Add("Esc did not leave the yard"); ActiveYard.RequestExit(); }
                until = Time.realtimeSinceStartup + 40f;
                while (ActiveYard != null && Time.realtimeSinceStartup < until) yield return null;
                yield return new WaitForSeconds(1f);
            }

            // ------------------------------------------------------------------ 2. every car, every view, driving
            races:
            // Back to the documented default bindings for the rest of the run (as Restore defaults does).
            DrivingPreferences.Current.BindingOverrides = "";
            DrivingPreferences.Current.Save();
            string preferred = DrivingPreferences.Current.View;
            Note($"preference before the races: {preferred}");
            var lib = Content.ContentLibrary.Load();
            var cars = lib.Catalogue.Cars.Select(c => c.Id).ToList();
            // -nsTourCars V01,V11 limits the run (frame-rate and field-of-view passes); -nsTourFov sets the base FOV for
            // this run only (in memory, never saved).
            string[] argv = System.Environment.GetCommandLineArgs();
            int carsArg = System.Array.IndexOf(argv, "-nsTourCars");
            if (carsArg >= 0 && carsArg + 1 < argv.Length)
            {
                var only = new HashSet<string>(argv[carsArg + 1].Split(','));
                cars = cars.Where(only.Contains).ToList();
            }
            int fovArg = System.Array.IndexOf(argv, "-nsTourFov");
            if (fovArg >= 0 && fovArg + 1 < argv.Length && float.TryParse(argv[fovArg + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float tourFov))
                DrivingPreferences.Current.VerticalFov = Mathf.Clamp(tourFov, DrivingPreferences.MinFov, DrivingPreferences.MaxFov);
            Note($"cars {string.Join(",", cars)}; base FOV {DrivingPreferences.Current.VerticalFov:0}°; target fps {Application.targetFrameRate}");
            Texture2D sheet = null;
            var coverage = new Dictionary<string, int>();
            for (int i = 0; i < cars.Count; i++)
            {
                string car = cars[i], course = TourCourses[i % TourCourses.Length];
                bool raceOver = false;
                var rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact };
                StartCoroutine(RunOfflineRace(course, car, rules, new List<string>(), false, (r, rev) => raceOver = true));
                until = Time.realtimeSinceStartup + 60f;
                while ((activeRace == null || activeRace.Camera == null) && Time.realtimeSinceStartup < until) yield return null;
                if (activeRace == null || activeRace.Camera == null) { failures.Add($"{car} on {course}: the race did not start"); continue; }
                activeRace.Autopilot = true;
                DrivingCamera cam = activeRace.Camera;
                if (cam.View != DrivingCamera.ParseView(DrivingPreferences.Current.View)) failures.Add($"{car}: the view preference did not survive the scene load ({cam.View} vs {DrivingPreferences.Current.View})");
                until = Time.realtimeSinceStartup + 30f;
                while (activeRace != null && activeRace.Phase != MatchPhase.Racing && Time.realtimeSinceStartup < until) yield return null;
                yield return new WaitForSeconds(2.5f);
                if (sheet == null) sheet = new Texture2D(TileW * 5, TileH * CarsPerSheet, TextureFormat.RGB24, false);
                int row = i % CarsPerSheet;
                foreach (DrivingView v in System.Enum.GetValues(typeof(DrivingView)))
                {
                    if (activeRace == null) break;
                    cam.SetView(v, save: false);
                    label.text = $"{car} · {course} · {v}";
                    Vector3 before = activeRace.PlayerView.transform.position;
                    float driven = 0f;
                    int frames = 0;
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 3f && activeRace != null)
                    {
                        yield return null;
                        Vector3 now = activeRace.PlayerView.transform.position;
                        driven += Vector3.Distance(before, now);
                        before = now;
                        frames++;
                    }
                    if (activeRace == null) break;
                    float fps = frames / Mathf.Max(0.01f, Time.realtimeSinceStartup - t0);
                    yield return new WaitForEndOfFrame();
                    Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                    float[] stats = Tile(shot, sheet, (int)v * TileW, (CarsPerSheet - 1 - row) * TileH);
                    if (i == 0 || v == DrivingView.Cockpit)
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"{car}-{course}-{(int)v}-{v}.jpg"), shot.EncodeToJPG(88));
                    Destroy(shot);
                    ledger.Append(Row(car, course, "race", cam, activeRace.SpeedLines, driven, fps, stats));
                    string key = car + "/" + v;
                    coverage[key] = driven > 5f ? 1 : 0;
                    if (driven < 5f) failures.Add($"{car} {v}: the car did not drive in this view ({driven:F1} m)");
                    if (stats[1] < 4f) failures.Add($"{car} {v}: a flat, featureless frame (luma sd {stats[1]:F1}) — inside geometry?");
                    // A large blown-out region is never a legitimate driving frame (V-070: NaN shading spread by bloom passed
                    // every other check); the HUD's white figures stay far below this.
                    if (stats[3] > BlownLimit) failures.Add($"{car} {v}: {stats[3]:P0} of the frame blown out to white — NaN shading or a light in the lens?");
                    if (v == DrivingView.Cockpit && (cam.Target.Cockpit == null || !cam.Target.Cockpit.Root.gameObject.activeSelf)) failures.Add($"{car}: no visible cockpit");
                }
                // Back to the player's preference: the next scene must bring it back by itself.
                if (activeRace != null) cam.SetView(DrivingCamera.ParseView(DrivingPreferences.Current.View), save: false);
                if (row == CarsPerSheet - 1 || i == cars.Count - 1)
                {
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"contact-sheet-{i / CarsPerSheet + 1}.jpg"), sheet.EncodeToJPG(90));
                    Destroy(sheet);
                    sheet = null;
                }
                if (activeRace != null) Destroy(activeRace.gameObject);
                until = Time.realtimeSinceStartup + 20f;
                while (!raceOver && Time.realtimeSinceStartup < until) yield return null;
                Note($"{car} on {course}: five views driven");
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "contact-sheet-legend.txt"),
                "Columns (left to right): " + string.Join(", ", System.Enum.GetNames(typeof(DrivingView))) + "\n" +
                string.Join("\n", Enumerable.Range(0, cars.Count).Select(i => $"sheet {i / CarsPerSheet + 1} row {i % CarsPerSheet + 1}: {cars[i]} on {TourCourses[i % TourCourses.Length]}")) + "\n");

            // ------------------------------------------------------------------ 3. inside a tunnel (C08, 3025–3255 m)
            {
                bool raceOver = false;
                var rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact };
                StartCoroutine(RunOfflineRace("C08", cars[0], rules, new List<string>(), false, (r, rev) => raceOver = true));
                until = Time.realtimeSinceStartup + 60f;
                while ((activeRace == null || activeRace.Camera == null) && Time.realtimeSinceStartup < until) yield return null;
                if (activeRace != null && activeRace.Camera != null)
                {
                    activeRace.Autopilot = true;
                    DrivingCamera cam = activeRace.Camera;
                    activeRace.SimulationSpeed = 4;
                    until = Time.realtimeSinceStartup + 120f;
                    while (activeRace != null && activeRace.Player.Progress.Location.Distance < 3040f && Time.realtimeSinceStartup < until) yield return null;
                    if (activeRace != null)
                    {
                        activeRace.SimulationSpeed = 1;
                        Note($"tunnel entered at route {activeRace.Player.Progress.Location.Distance:F0} m");
                        foreach (DrivingView v in System.Enum.GetValues(typeof(DrivingView)))
                        {
                            if (activeRace == null || activeRace.Player.Progress.Location.Distance > 3250f) break;
                            cam.SetView(v, save: false);
                            label.text = $"{cars[0]} · C08 tunnel · {v}";
                            yield return new WaitForSeconds(0.9f);
                            yield return new WaitForEndOfFrame();
                            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"tunnel-C08-{(int)v}-{v}.jpg"), shot.EncodeToJPG(88));
                            float[] stats = Tile(shot, null, 0, 0);
                            Destroy(shot);
                            ledger.Append(Row(cars[0], "C08", $"tunnel@{activeRace.Player.Progress.Location.Distance:F0}m", cam, activeRace.SpeedLines, 0f, 0f, stats));
                            if (stats[3] > BlownLimit) failures.Add($"tunnel {v}: {stats[3]:P0} of the frame blown out to white");
                        }
                    }
                    if (activeRace != null) Destroy(activeRace.gameObject);
                    until = Time.realtimeSinceStartup + 20f;
                    while (!raceOver && Time.realtimeSinceStartup < until) yield return null;
                }
                else failures.Add("the tunnel run did not start");
            }

            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "ledger.csv"), ledger.ToString());
            int covered = coverage.Values.Sum();
            Note($"coverage: {covered}/{cars.Count * 5} car×view combinations driven (>5 m in view)");
            if (covered != cars.Count * 5) failures.Add($"coverage {covered}/{cars.Count * 5}");
            if (DrivingPreferences.Current.View != preferred) failures.Add($"the tour's temporary views overwrote the preference ({DrivingPreferences.Current.View} vs {preferred})");
            InputSystem.RemoveDevice(pad);
            Destroy(labelCanvas.gameObject);
            string summary = failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures);
            Note(summary);
            yield return new WaitForSeconds(1f);
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }

        static void SaveJpg(string path)
        {
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes(path, shot.EncodeToJPG(88));
            Destroy(shot);
        }

        static IEnumerator TapKey(Keyboard kb, Key key)
        {
            InputSystem.QueueStateEvent(kb, new KeyboardState(key));
            yield return new WaitForSeconds(0.25f);
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            yield return new WaitForSeconds(0.35f);
        }

        static IEnumerator TapPad(Gamepad pad, GamepadButton button)
        {
            InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(button));
            yield return new WaitForSeconds(0.25f);
            InputSystem.QueueStateEvent(pad, new GamepadState());
            yield return new WaitForSeconds(0.35f);
        }

        /// <summary>Largest share of a driving frame allowed at full white.</summary>
        const float BlownLimit = 0.06f;

        /// <summary>Downscales a capture into a contact-sheet cell (when a sheet is given); returns luma mean, sd, dark fraction and blown-out fraction.</summary>
        static float[] Tile(Texture2D shot, Texture2D sheet, int x, int y)
        {
            RenderTexture rt = RenderTexture.GetTemporary(TileW, TileH, 0);
            Graphics.Blit(shot, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tile = new Texture2D(TileW, TileH, TextureFormat.RGB24, false);
            tile.ReadPixels(new Rect(0, 0, TileW, TileH), 0, 0);
            tile.Apply();
            if (sheet != null)
            {
                sheet.ReadPixels(new Rect(0, 0, TileW, TileH), x, y);
                sheet.Apply();
            }
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] px = tile.GetPixels32();
            Destroy(tile);
            double sum = 0, sum2 = 0;
            int dark = 0, blown = 0;
            foreach (Color32 c in px)
            {
                float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                sum += l;
                sum2 += l * l;
                if (l < 8f) dark++;
                if (l >= 252f) blown++;
            }
            float mean = (float)(sum / px.Length);
            float sd = Mathf.Sqrt(Mathf.Max(0f, (float)(sum2 / px.Length) - mean * mean));
            return new[] { mean, sd, dark / (float)px.Length, blown / (float)px.Length };
        }

        static string Row(string car, string course, string where, DrivingCamera cam, SpeedLines lines, float driven, float fps, float[] stats)
        {
            Transform t = cam.Target.transform;
            Vector3 local = t.InverseTransformPoint(cam.transform.position);
            var ci = CultureInfo.InvariantCulture;
            Art.CockpitRig rig = cam.Target.Cockpit;
            bool cockpit = rig != null && rig.Root.gameObject.activeSelf;
            float wheel = rig != null && rig.Wheel != null ? Mathf.DeltaAngle(0f, rig.Wheel.localEulerAngles.z) : 0f;
            return string.Format(ci, "{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7:F1},{8:F2},{9:F0},{10:F1},{11:F2},{12:F2},{13:F2},{14},{15},{16:F0},{17:F1},{18:F1},{19:F3},{20:F0},{21:F3}\n",
                car, course, where, cam.View, local.x, local.y, local.z, cam.Camera.fieldOfView, cam.Camera.nearClipPlane,
                cam.LastSpeedMps * 3.6f, driven, cam.DriftFraming, cam.CollisionDistance, lines != null ? lines.Strength : 0f,
                lines == null || lines.AllOutsideClearCentre(), cockpit, wheel,
                stats != null ? stats[0] : -1f, stats != null ? stats[1] : -1f, stats != null ? stats[2] : -1f, fps, stats != null && stats.Length > 3 ? stats[3] : -1f);
        }
    }
}
