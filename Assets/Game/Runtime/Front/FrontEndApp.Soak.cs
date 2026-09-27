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
using UnityEngine.SceneManagement;

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
        /// <summary>
        /// Soak diagnostic: element counts of every non-empty static collection and handler count of every static delegate in
        /// the game's own assemblies, by field name — anything that accumulates between races and is reachable from a
        /// static shows up by name.
        /// </summary>
        static Dictionary<string, int> StaticCensus()
        {
            var counts = new Dictionary<string, int>();
            foreach (System.Reflection.Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.StartsWith("NightSignal")) continue;
                System.Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (System.Type t in types)
                {
                    if (t.ContainsGenericParameters) continue;
                    foreach (System.Reflection.FieldInfo f in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (f.IsLiteral) continue;
                        object v;
                        try { v = f.GetValue(null); }
                        catch { continue; }
                        int c = -1;
                        if (v is System.Delegate d) c = d.GetInvocationList().Length;
                        else if (v is IEnumerable && !(v is string))
                        {
                            object count = v.GetType().GetProperty("Count")?.GetValue(v);
                            if (count is int i) c = i;
                            else if (v is System.Array a) c = a.Length;
                        }
                        if (c > 0) counts[t.FullName + "." + f.Name] = c;
                    }
                }
            }
            return counts;
        }

        static readonly string[] MemoryCounters =
        {
            "System Used Memory", "Profiler Used Memory", "Audio Used Memory", "Video Used Memory", "Gfx Used Memory", "Texture Memory",
            "Mesh Memory", "Material Memory", "Object Count", "Asset Count", "Scene Object Count", "Material Count",
        };

        /// <summary>
        /// Soak diagnostic (<c>-nsSoakQueryProbe</c>): Unity's allocated native memory across idle menu frames, then across
        /// frames running the driving camera's physics queries (sphere casts on the collision mask, bumper raycasts) against
        /// the loaded backdrop course, then the same queries on a course without terrain hits — bytes per query.
        /// </summary>
        IEnumerator QueryProbe(System.Action<string> note)
        {
            long Alloc() => UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            NightSignal.Track.CourseRuntime course = NightSignal.Track.CourseRuntime.Active;
            if (course == null || course.Track == null) { note("query probe: no course loaded"); yield break; }
            const int Frames = 600, PerFrame = 100;
            int mask = (1 << NightSignal.Art.GameLayers.Drivable) | (1 << NightSignal.Art.GameLayers.Barrier) | (1 << NightSignal.Art.GameLayers.Scenery);
            yield return new WaitForSeconds(2f);
            long a0 = Alloc();
            for (int f = 0; f < Frames; f++) yield return null;
            long a1 = Alloc();
            float length = course.Track.LengthMetres;
            for (int f = 0; f < Frames; f++)
            {
                for (int q = 0; q < PerFrame; q++)
                {
                    var s = course.Track.SampleAt((f * PerFrame + q) * 0.37f % length);
                    Vector3 pivot = s.Position + Vector3.up * 0.8f;
                    Vector3 cam = pivot - s.Tangent * 6f + Vector3.up * 2f;
                    Vector3 to = cam - pivot;
                    Physics.SphereCast(pivot, 0.28f, to.normalized, out RaycastHit _, to.magnitude, mask, QueryTriggerInteraction.Ignore);
                }
                yield return null;
            }
            long a2 = Alloc();
            for (int f = 0; f < Frames; f++)
            {
                for (int q = 0; q < PerFrame; q++)
                {
                    var s = course.Track.SampleAt((f * PerFrame + q) * 0.37f % length);
                    Physics.Raycast(s.Position + Vector3.up * 0.9f, Vector3.down, out RaycastHit _, 0.76f, NightSignal.Art.GameLayers.DrivableMask, QueryTriggerInteraction.Ignore);
                }
                yield return null;
            }
            long a3 = Alloc();
            note($"query probe on {course.Route?.Course}: idle {Frames} frames {(a1 - a0) / 1024f:F0} KB; {Frames * PerFrame} sphere casts {(a2 - a1) / 1024f:F0} KB " +
                 $"({(a2 - a1) / (float)(Frames * PerFrame):F1} B each); {Frames * PerFrame} raycasts {(a3 - a2) / 1024f:F0} KB ({(a3 - a2) / (float)(Frames * PerFrame):F1} B each)");
        }

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
            var report = new StringBuilder("race,course,cameras,drivingCameras,vehicleViews,raceHuds,speedLines,prefListeners,lights,managedMB,meshes,materials,textures,audioClips,gameObjects,terrainData,scriptableObjects,unityAllocatedMB,unityReservedMB,monoHeapMB,gfxDriverMB,physicsBodies," + string.Join(",", MemoryCounters.Select(m => m.Replace(" ", ""))) + ",frameMsMean,frameMsP99,raceFinished,playerResets,playerCheckpoints,playerFinishS\n");
            // Loaded Unity objects of the kinds a race creates at run time (including assets outside any scene), to find what
            // the managed-heap growth holds on to.
            // Unity's per-category memory counters (Memory Profiler module), read between races.
            var recorders = MemoryCounters.Select(m => Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Memory, m)).ToList();
            string Counters() => string.Join(",", recorders.Select((r, i) => !r.Valid ? "-1"
                : MemoryCounters[i].EndsWith("Memory") ? (r.LastValue / 1048576f).ToString("F1") : r.LastValue.ToString()));
            string Objects() => string.Join(",", Resources.FindObjectsOfTypeAll<Mesh>().Length, Resources.FindObjectsOfTypeAll<Material>().Length,
                Resources.FindObjectsOfTypeAll<Texture>().Length, Resources.FindObjectsOfTypeAll<AudioClip>().Length, Resources.FindObjectsOfTypeAll<GameObject>().Length,
                Resources.FindObjectsOfTypeAll<TerrainData>().Length, Resources.FindObjectsOfTypeAll<ScriptableObject>().Length,
                // Unity's own allocators (native memory the object census cannot see), the Mono heap's reserved size and
                // the graphics driver's share, plus live physics bodies.
                (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576f).ToString("F0"),
                (UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576f).ToString("F0"),
                (UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576f).ToString("F0"),
                (UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576f).ToString("F0"),
                FindObjectsByType<Rigidbody>().Length, Counters());
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
            Dictionary<string, int> staticsAfterFirst = null;
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-nsSoakQueryProbe") >= 0) yield return QueryProbe(Note);
            Note($"baseline in the menus: cameras {baseline.cams}, driving cameras {baseline.driving}, views {baseline.views}, HUDs {baseline.huds}, speed lines {baseline.lines}, listeners {baseline.listeners}, lights {baseline.lights}, managed {baseline.mb:F1} MB");

            // -nsSoakLoadsOnly: the course scenes alone (generation, terrain, colliders, back to the menus), no cars or race —
            // separates native growth from course loading from growth from racing.
            bool loadsOnly = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-nsSoakLoadsOnly") >= 0;
            // -nsSoakPlain: races without the view/look-back/speedometer switching (resets kept) — separates presentation
            // switching from the race itself.
            bool plain = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-nsSoakPlain") >= 0;
            // -nsSoakAi N: only the first N AI rivals (0 = the player's car alone) — separates per-car growth.
            int aiArg = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-nsSoakAi");
            if (aiArg >= 0) ai = ai.Take(int.Parse(System.Environment.GetCommandLineArgs()[aiArg + 1])).ToList();
            // -nsSoakDisable camera,hud,lines,audio: switch those off for each race (bisecting per-frame native growth).
            int offArg = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-nsSoakDisable");
            var disabled = new HashSet<string>(offArg >= 0 ? System.Environment.GetCommandLineArgs()[offArg + 1].Split(',') : new string[0]);
            for (int n = 0; n < races; n++)
            {
                string course = courses[n % courses.Length];
                if (loadsOnly)
                {
                    AsyncOperation load = SceneManager.LoadSceneAsync(course, LoadSceneMode.Single);
                    while (!load.isDone) yield return null;
                    yield return new WaitForSeconds(2f);
                    yield return LoadBackdrop();
                    yield return new WaitForSeconds(1f);
                    var lc = Census();
                    string lo = Objects();
                    Note($"load {n + 1} {course}: objects {lo}; managed {lc.mb:F1} MB");
                    report.AppendLine(string.Join(",", n + 1, course, lc.cams, lc.driving, lc.views, lc.huds, lc.lines, lc.listeners, lc.lights, lc.mb.ToString("F1"),
                        lo, 0, 0, false, 0, 0, -1));
                    if (n == 0) staticsAfterFirst = StaticCensus();
                    continue;
                }
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
                if (disabled.Contains("camera")) foreach (Camera rc in cam.GetComponentsInChildren<Camera>()) rc.enabled = false;
                if (disabled.Contains("hud")) foreach (RaceHud h in FindObjectsByType<RaceHud>()) h.gameObject.SetActive(false);
                if (disabled.Contains("lines")) foreach (SpeedLines l in FindObjectsByType<SpeedLines>()) l.gameObject.SetActive(false);
                if (disabled.Contains("audio")) foreach (AudioSource a in FindObjectsByType<AudioSource>()) a.enabled = false;
                if (disabled.Contains("drivingcam")) cam.enabled = false;
                OfflineRaceSession.SoakSkipRender = disabled.Contains("views");
                if (disabled.Contains("input")) activeRace.SoakDropControls();
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
                    if (!plain && now - lastCycle > 1.5f)
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
                Note($"race {n + 1} {course}: objects (meshes, materials, textures, clips, GameObjects, terrain data, ScriptableObjects; Unity allocated/reserved, Mono heap, graphics driver MB; rigidbodies) {objects}");
                report.AppendLine(string.Join(",", n + 1, course, c.cams, c.driving, c.views, c.huds, c.lines, c.listeners, c.lights, c.mb.ToString("F1"),
                    objects, mean.ToString("F2"), p99.ToString("F2"), finished, playerResets, checkpoints, finishS.ToString("F1")));
                Note($"race {n + 1} {course}: {frameMs.Count} frames{(disabled.Count > 0 ? " with " + string.Join("+", disabled) + " off" : "")}");
                Note($"race {n + 1} {course}: {resetsRequested} reset requests → {playerResets} resets, {checkpoints} gates; then cameras {c.cams}, driving cameras {c.driving}, views {c.views}, HUDs {c.huds}, speed lines {c.lines}, listeners {c.listeners}, lights {c.lights}, managed {c.mb:F1} MB; frame {mean:F2}/{p99:F2} ms");
                if (c.driving > baseline.driving || c.views > baseline.views || c.huds > baseline.huds || c.lines > baseline.lines || c.listeners > baseline.listeners || c.cams > baseline.cams + 1)
                    failures.Add($"race {n + 1}: something accumulated (cameras {c.cams}, driving {c.driving}, views {c.views}, HUDs {c.huds}, lines {c.lines}, listeners {c.listeners})");
                if (playerResets <= 0 || playerResets > resetsRequested) failures.Add($"race {n + 1}: {resetsRequested} reset requests gave {playerResets} resets");
                if (checkpoints <= 0) failures.Add($"race {n + 1}: no authoritative progress");
                if (n == 0) staticsAfterFirst = StaticCensus();
            }
            if (staticsAfterFirst != null)
            {
                Dictionary<string, int> now = StaticCensus();
                var grew = now.Where(kv => kv.Value > (staticsAfterFirst.TryGetValue(kv.Key, out int b) ? b : 0))
                    .Select(kv => $"{kv.Key} {(staticsAfterFirst.TryGetValue(kv.Key, out int b2) ? b2 : 0)}→{kv.Value}").ToList();
                Note($"static collections/handlers after race 1 vs the end ({now.Count} non-empty): " + (grew.Count == 0 ? "none grew" : "grew: " + string.Join("; ", grew)));
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
