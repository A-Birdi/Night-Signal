using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.Meet;
using NightSignal.Race;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// The §14 performance profile (<c>-nsPerfProfileTour</c>), measured separately as the spec asks: cold course loads (one
        /// course per region, then one warm reload), six racers (the player's car on the autopilot and five authored AI,
        /// light contact, real speed) and the populated offline meet (the host and the rivals at their cars, the avatar walking
        /// a loop). Per section: frame time p50/p95/p99/max and main-thread managed allocation per frame — the "no per-frame
        /// allocations in the driving hot path" rule. The report is labelled with the machine (no device or user name), the
        /// build, the window and the quality settings: a measurement on this machine, not a universal frame-rate claim.
        /// Isolated profile folder. Automation, not a person.
        /// </summary>
        IEnumerator PerfProfileTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "perf"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            var csv = new StringBuilder("section,subject,frames,frameMsP50,frameMsP95,frameMsP99,frameMsMax,over16.7ms,over33.3ms,allocBytesPerFrameMean,allocBytesPerFrameP99,framesAllocating,gcCollections,extra\n");
            var md = new StringBuilder();
            void Note(string n) => Debug.Log("[NightSignal.PerfProfile] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { Fail($"button not available: {name}"); return false; }
                b.onClick.Invoke();
                return true;
            }
            IEnumerator Until(Func<bool> condition, float seconds)
            {
                float until = Time.realtimeSinceStartup + seconds;
                while (!condition() && Time.realtimeSinceStartup < until) yield return null;
            }

            // Managed allocation per frame: Unity's "GC Allocated In Frame" counter where this player provides it (exact, every
            // thread); else GC.GetAllocatedBytesForCurrentThread (main thread); else the positive steps of the managed heap
            // (approximate: the heap grows in blocks, and a collection in between hides an allocation).
            var gcFrame = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Memory, "GC Allocated In Frame");
            yield return null;
            yield return null;
            bool recorder = gcFrame.Valid;
            bool exact = !recorder;
            try { if (exact && GC.GetAllocatedBytesForCurrentThread() <= 0) exact = false; }
            catch (Exception) { exact = false; }
            long AllocCounter() => exact ? GC.GetAllocatedBytesForCurrentThread() : UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            string allocMeasure = recorder ? "Unity's \"GC Allocated In Frame\" counter (exact, every thread)"
                : exact ? "GC.GetAllocatedBytesForCurrentThread (main thread, exact)" : "positive steps of the managed heap (approximate: block-sized steps)";

            // Samples every frame while a section is open.
            var frames = new List<float>();
            var allocs = new List<long>();
            // The managed heap's positive steps, always: comparable across runs whichever exact measure the player offers.
            var heapSteps = new List<long>();
            long lastAlloc = 0, lastHeap = 0;
            int gcAtStart = 0;
            IEnumerator Sample(float seconds, Func<bool> keepGoing = null)
            {
                frames.Clear();
                allocs.Clear();
                heapSteps.Clear();
                gcAtStart = GC.CollectionCount(0);
                yield return null;
                lastAlloc = AllocCounter();
                lastHeap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                float until = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < until && (keepGoing == null || keepGoing()))
                {
                    yield return null;
                    frames.Add(Time.unscaledDeltaTime * 1000f);
                    long heap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                    heapSteps.Add(Math.Max(0, heap - lastHeap));
                    lastHeap = heap;
                    if (recorder) allocs.Add(gcFrame.LastValue);
                    else
                    {
                        long now = AllocCounter();
                        allocs.Add(Math.Max(0, now - lastAlloc));
                        lastAlloc = now;
                    }
                }
            }
            float Pct(List<float> sorted, float p) => sorted.Count == 0 ? 0f : sorted[Mathf.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1)];
            string Row(string section, string subject, string extra)
            {
                var f = frames.OrderBy(x => x).ToList();
                var a = allocs.OrderBy(x => x).ToList();
                int gcs = GC.CollectionCount(0) - gcAtStart;
                double allocMean = a.Count > 0 ? a.Average() : 0;
                long allocP99 = a.Count > 0 ? a[Mathf.Clamp((int)(a.Count * 0.99f), 0, a.Count - 1)] : 0;
                int allocating = a.Count(x => x > 0);
                double heapMean = heapSteps.Count > 0 ? heapSteps.Average() : 0;
                extra += $"; heap steps {heapMean:F0} B/frame";
                csv.AppendLine(string.Join(",", section, subject, f.Count, Pct(f, 0.5f).ToString("F2"), Pct(f, 0.95f).ToString("F2"), Pct(f, 0.99f).ToString("F2"),
                    (f.Count > 0 ? f[f.Count - 1] : 0f).ToString("F2"), f.Count(x => x > 16.7f), f.Count(x => x > 33.3f), allocMean.ToString("F0"), allocP99, allocating, gcs, extra));
                string line = $"| {subject} | {f.Count} | {Pct(f, 0.5f):F2} | {Pct(f, 0.95f):F2} | {Pct(f, 0.99f):F2} | {(f.Count > 0 ? f[f.Count - 1] : 0f):F2} | " +
                              $"{f.Count(x => x > 16.7f)} | {allocMean:F0} | {allocating} of {a.Count} | {gcs} | {extra} |";
                Note($"{section} {subject}: {f.Count} frames, p50 {Pct(f, 0.5f):F2} / p95 {Pct(f, 0.95f):F2} / p99 {Pct(f, 0.99f):F2} / max {(f.Count > 0 ? f[f.Count - 1] : 0f):F2} ms; " +
                     $"alloc mean {allocMean:F0} B/frame, p99 {allocP99} B, {allocating} of {a.Count} frames allocating; {gcs} GC; {extra}");
                return line;
            }

            // ---- label
            ContentCatalogue cat = Content.ContentLibrary.Load()?.Catalogue;
            md.AppendLine("# Performance profile (spec §14) — a measurement on this machine, not a universal frame-rate claim");
            md.AppendLine();
            md.AppendLine($"- Machine: {SystemInfo.processorType} ({SystemInfo.processorCount} threads), {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}, " +
                          $"{SystemInfo.graphicsMemorySize} MB), {SystemInfo.systemMemorySize} MB RAM, {SystemInfo.operatingSystem}");
            md.AppendLine($"- Build: {Application.version}, {(Debug.isDebugBuild ? "development" : "non-development")} player, Unity {Application.unityVersion}, content {cat?.ContentHash?.Substring(0, Math.Min(12, cat?.ContentHash?.Length ?? 0))}");
            md.AppendLine($"- Window {Screen.width}x{Screen.height}, quality \"{QualitySettings.names[QualitySettings.GetQualityLevel()]}\", vSync {QualitySettings.vSyncCount}, " +
                          $"target frame rate {Application.targetFrameRate}, race simulation {Vehicle.VehicleSimulation.TickRate} Hz on its own fixed tick " +
                          $"(Unity's FixedUpdate step {Time.fixedDeltaTime * 1000f:F0} ms drives no race code), transport: offline (no network)");
            md.AppendLine($"- Allocation measure: {allocMeasure}");
            md.AppendLine();

            // ---- a Local profile (buttons), for the meet
            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Profile Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            if (LocalSession.Current?.Profile == null) { Fail("no profile"); Finish(); yield break; }

            // ---- cold loads and six racers: the first regular course of each region, then a warm reload of the first
            List<string> courses = cat == null ? new List<string>() : cat.Courses.Where(c => c.Kind == "regular" && Application.CanStreamedLevelBeLoaded(c.Id))
                .GroupBy(c => c.Region).Select(g => g.OrderBy(c => c.Id, StringComparer.Ordinal).First().Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            List<string> field = cat == null ? new List<string>() : cat.Rivals.Select(r => r.Id).Where(id => FinalRivals.Allowed(id, AiPlacementContext.RandomPool))
                .OrderBy(id => id, StringComparer.Ordinal).Take(5).ToList();
            Note($"courses {string.Join(", ", courses)}; field {string.Join(", ", field)}; allocation measure: {allocMeasure}");
            var loadRows = new List<string>();
            var raceRows = new List<string>();
            foreach ((string course, bool warm) in courses.Select(c => (c, false)).Concat(courses.Take(1).Select(c => (c, true))))
            {
                var rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10, CarCapPi = 999 };
                bool over = false;
                float t0 = Time.realtimeSinceStartup;
                StartCoroutine(RunOfflineRace(course, "V01", rules, field.ToList(), false, (r, rev) => over = true));
                yield return Until(() => activeRace != null && activeRace.Phase != MatchPhase.Loading, 90f);
                if (activeRace == null) { Fail($"{course}: the race did not load"); continue; }
                float loadSeconds = Time.realtimeSinceStartup - t0;
                OfflineRaceSession race = activeRace;
                // The countdown right after a load: shader compilation and first-use spikes land here.
                yield return Sample(3f, () => activeRace == race);
                loadRows.Add(Row(warm ? "warm-load" : "cold-load", course, $"load {loadSeconds:F2} s") .Replace($"| {course} |", $"| {course}{(warm ? " (warm)" : "")} | {loadSeconds:F2} s |"));
                if (warm) { race.SimulationSpeed = 12; race.Autopilot = true; yield return Until(() => over, 300f); continue; }
                yield return Until(() => race.Phase == MatchPhase.Racing, 20f);
                race.Autopilot = true;
                race.SimulationSpeed = 1;
                yield return new WaitForSeconds(2f);
                int cars = race.Sim?.Entrants.Count ?? 0;
                yield return Sample(30f, () => activeRace == race && race.Phase == MatchPhase.Racing);
                raceRows.Add(Row("six-racers", course, $"{cars} cars"));
                if (cars != 6) Fail($"{course}: {cars} cars, not six");
                race.SimulationSpeed = 12;
                yield return Until(() => over, 300f);
                yield return new WaitForSeconds(0.5f);
            }

            // ---- the populated meet
            string meetRow = null;
            LocalSession s = LocalSession.Current;
            OwnedCar car = s.Profile.Cars.FirstOrDefault();
            StartOfflineMeet(new LocalCarChoice { ModelId = car?.ModelId, InstanceId = car?.InstanceId }, OfflineHub);
            yield return Until(() => ActiveMeet != null && ActiveMeet.Ready, 60f);
            MeetSession m = ActiveMeet;
            if (m == null || !m.Ready) Fail("the meet did not open");
            else
            {
                int rigs = FindObjectsByType<Characters.CharacterRig>(FindObjectsSortMode.None).Length;
                int views = FindObjectsByType<Vehicle.VehicleView>(FindObjectsSortMode.None).Length;
                // Walk a slow loop round the plaza: forward with the camera turning, so the view sweeps the whole meet.
                m.ScriptMove = _ => new Vector2(0f, 1f);
                float yaw = 90f;
                IEnumerator turn = Sample(45f);
                while (turn.MoveNext())
                {
                    yaw += Time.unscaledDeltaTime * 12f;
                    m.Camera.Yaw = yaw;
                    yield return turn.Current;
                }
                m.ScriptMove = null;
                meetRow = Row("meet", "Cedar Lantern Terrace", $"{rigs} characters, {views} cars");
                m.Leave();
                yield return new WaitForSeconds(2f);
            }

            md.AppendLine("## Cold course loads (first load of each region's course this session; one warm reload)");
            md.AppendLine();
            md.AppendLine("| Course | Load to grid | Frames (3 s) | p50 ms | p95 ms | p99 ms | max ms | > 16.7 ms | alloc B/frame | frames allocating | GC | note |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (string r in loadRows) md.AppendLine(r);
            md.AppendLine();
            md.AppendLine("## Six racers (autopilot + five authored AI, light contact, real speed, 30 s of racing per course)");
            md.AppendLine();
            md.AppendLine("| Course | Frames | p50 ms | p95 ms | p99 ms | max ms | > 16.7 ms | alloc B/frame | frames allocating | GC | note |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (string r in raceRows) md.AppendLine(r);
            md.AppendLine();
            md.AppendLine("## Populated offline meet (45 s walking a loop, the camera sweeping)");
            md.AppendLine();
            md.AppendLine("| Meet | Frames | p50 ms | p95 ms | p99 ms | max ms | > 16.7 ms | alloc B/frame | frames allocating | GC | note |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            if (meetRow != null) md.AppendLine(meetRow);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "perf-profile.md"), md.ToString());
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "perf-profile.csv"), csv.ToString());
            gcFrame.Dispose();
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
