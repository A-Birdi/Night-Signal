using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NightSignal.Cameras;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.UI;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Soak run (<c>-nsSoakTour [races]</c>, Addendum 03 I04): back-to-back twelve-car races (the player's car on the
        /// autopilot + eleven AI, light contact) on alternating courses; during each race the view is cycled every 1.5 s
        /// through all five, look-back is held, speed/units/style are switched, and the player's car is reset by a held
        /// request every 12 s. Between races, with the menus back, it counts what must not accumulate: cameras,
        /// driving cameras, vehicle views, race HUD and speed-line canvases, preference listeners, lights, managed memory;
        /// and per race the frame time and the player's authoritative progress. Isolated preferences/profiles.
        /// Automation, not a human session.
        /// </summary>
        IEnumerator SoakTour(int races)
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "soak"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            if (DrivingPreferences.FolderOverride == null)
            {
                DrivingPreferences.FolderOverride = System.IO.Path.Combine(dir, "prefs");
                DrivingPreferences.ResetCache();
            }
            var failures = new List<string>();
            var report = new StringBuilder("race,course,cameras,drivingCameras,vehicleViews,raceHuds,speedLines,prefListeners,lights,managedMB,meshes,materials,textures,audioClips,gameObjects,frameMsMean,frameMsP99,raceFinished,playerResets,playerCheckpoints,playerFinishS\n");
            // Loaded Unity objects of the kinds a race creates at run time (including assets outside any scene), to find what
            // the managed-heap growth holds on to.
            string Objects() => string.Join(",", Resources.FindObjectsOfTypeAll<Mesh>().Length, Resources.FindObjectsOfTypeAll<Material>().Length,
                Resources.FindObjectsOfTypeAll<Texture>().Length, Resources.FindObjectsOfTypeAll<AudioClip>().Length, Resources.FindObjectsOfTypeAll<GameObject>().Length);
            void Note(string s) => Debug.Log("[NightSignal.SoakTour] " + s);
            string[] courses = { "C01", "C08", "C12", "C03" };
            var ai = new List<string> { "R01", "R02", "R03", "R05", "R06", "R07", "R09", "ai-8", "ai-9", "ai-10", "ai-11" };
            yield return new WaitForSeconds(3f);
            (int cams, int driving, int views, int huds, int lines, int listeners, int lights, float mb) Census()
            {
                System.GC.Collect();
                return (FindObjectsByType<Camera>().Length, FindObjectsByType<DrivingCamera>().Length, FindObjectsByType<VehicleView>().Length,
                    FindObjectsByType<RaceHud>().Length, FindObjectsByType<SpeedLines>().Length, DrivingPreferences.ChangedListenerCount,
                    FindObjectsByType<Light>().Length, System.GC.GetTotalMemory(true) / (1024f * 1024f));
            }
            var baseline = Census();
            Note($"baseline in the menus: cameras {baseline.cams}, driving cameras {baseline.driving}, views {baseline.views}, HUDs {baseline.huds}, speed lines {baseline.lines}, listeners {baseline.listeners}, lights {baseline.lights}, managed {baseline.mb:F1} MB");

            for (int n = 0; n < races; n++)
            {
                string course = courses[n % courses.Length];
                bool over = false;
                List<RaceEntrantResult> results = null;
                var rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10 };
                string soakCar = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-nsSoakSameCar") >= 0 ? "V01" : "V0" + (1 + n % 9);
                StartCoroutine(RunOfflineRace(course, soakCar, rules, new List<string>(ai), false, (r, rev) => { results = r; over = true; }));
                float until = Time.realtimeSinceStartup + 60f;
                while ((activeRace == null || activeRace.Camera == null) && Time.realtimeSinceStartup < until) yield return null;
                if (activeRace == null || activeRace.Camera == null) { failures.Add($"race {n + 1} did not start"); continue; }
                activeRace.Autopilot = true;
                DrivingCamera cam = activeRace.Camera;
                OfflineRaceSession race = activeRace;
                var frameMs = new List<float>();
                float raceStart = -1f, lastCycle = 0f, lastReset = 0f;
                int resetsRequested = 0;
                // Scripted resets: hold the reset bit for 1 s every 12 s on top of the autopilot (judged by the simulation).
                System.Func<RaceEntrant, int, DriverInput> pilot = race.Sim.HumanInput;
                float holdUntil = -1f;
                race.Sim.HumanInput = (e, tick) =>
                {
                    DriverInput i = pilot(e, tick);
                    if (Time.realtimeSinceStartup < holdUntil) i.Buttons |= InputButtons.ResetHeld;
                    return i;
                };
                while (race != null && race.Phase != MatchPhase.Results && Time.realtimeSinceStartup < until + 420f)
                {
                    yield return null;
                    if (race == null) break;
                    frameMs.Add(Time.unscaledDeltaTime * 1000f);
                    if (race.Phase != MatchPhase.Racing) continue;
                    float now = Time.realtimeSinceStartup;
                    if (raceStart < 0f) raceStart = now;
                    if (now - lastCycle > 1.5f)
                    {
                        lastCycle = now;
                        cam.SetView((DrivingView)(((int)cam.View + 1) % 5), save: false);
                        cam.LookBack = Random.value < 0.15f;
                        DrivingPreferences p = DrivingPreferences.Current; // presentation switches mid-race (in memory)
                        p.SpeedStyle = p.Dial ? "strip" : "dial";
                        if (Random.value < 0.5f) p.Units = p.Unit == SpeedUnit.Kmh ? "mph" : "kmh";
                    }
                    if (now - lastReset > 12f && now - raceStart > 6f)
                    {
                        lastReset = now;
                        holdUntil = now + 1f;
                        resetsRequested++;
                    }
                    if (now - raceStart > 70f) break; // long enough per race; the soak is the repetition
                }
                cam.LookBack = false;
                int playerResets = race != null ? race.Player.Progress.Resets : -1;
                int checkpoints = race != null ? race.Player.Progress.CheckpointsPassed : -1;
                bool finished = race != null && race.Player.Progress.Finished;
                float finishS = finished ? race.Player.Progress.FinishTimeMicros / 1e6f : -1f;
                if (race != null) Destroy(race.gameObject);
                until = Time.realtimeSinceStartup + 20f;
                while (!over && Time.realtimeSinceStartup < until) yield return null;
                // Back to the menus, as a player would be between events.
                Canvas.gameObject.SetActive(true);
                yield return LoadBackdrop();
                yield return new WaitForSeconds(1f);
                var c = Census();
                frameMs.Sort();
                float mean = frameMs.Count > 0 ? frameMs.Average() : 0f, p99 = frameMs.Count > 0 ? frameMs[(int)(frameMs.Count * 0.99f)] : 0f;
                string objects = Objects();
                Note($"race {n + 1} {course}: objects (meshes, materials, textures, clips, GameObjects) {objects}");
                report.AppendLine(string.Join(",", n + 1, course, c.cams, c.driving, c.views, c.huds, c.lines, c.listeners, c.lights, c.mb.ToString("F1"),
                    objects, mean.ToString("F2"), p99.ToString("F2"), finished, playerResets, checkpoints, finishS.ToString("F1")));
                Note($"race {n + 1} {course}: {resetsRequested} reset requests → {playerResets} resets, {checkpoints} gates; then cameras {c.cams}, driving cameras {c.driving}, views {c.views}, HUDs {c.huds}, speed lines {c.lines}, listeners {c.listeners}, lights {c.lights}, managed {c.mb:F1} MB; frame {mean:F2}/{p99:F2} ms");
                if (c.driving > baseline.driving || c.views > baseline.views || c.huds > baseline.huds || c.lines > baseline.lines || c.listeners > baseline.listeners || c.cams > baseline.cams + 1)
                    failures.Add($"race {n + 1}: something accumulated (cameras {c.cams}, driving {c.driving}, views {c.views}, HUDs {c.huds}, lines {c.lines}, listeners {c.listeners})");
                if (playerResets <= 0 || playerResets > resetsRequested) failures.Add($"race {n + 1}: {resetsRequested} reset requests gave {playerResets} resets");
                if (checkpoints <= 0) failures.Add($"race {n + 1}: no authoritative progress");
            }
            var end = Census();
            if (end.mb > baseline.mb + 64f) failures.Add($"managed memory grew {baseline.mb:F1} → {end.mb:F1} MB");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "soak.csv"), report.ToString());
            Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
            yield return new WaitForSeconds(1f);
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}
