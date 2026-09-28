using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Cameras;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Vehicle;
using Unity.Profiling;
using UnityEngine;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Car level-of-detail evidence (<c>-nsCarLodTour</c>): a full grid (you + 11 AI) on C01 driven by the autopilot.
        /// The GPU Resident Drawer draws the cars through BatchRendererGroup, so <see cref="Renderer.isVisible"/> cannot say
        /// which level was drawn; every check is made on frozen frames instead (the race paused, the scene still) from the
        /// rendered triangle count, with levels held and chosen, against a repeat count of the same state.
        /// 1. Your car in each of the five views: chosen and drawn at its full body (the same triangles as holding it
        ///    full), and in Cockpit view the fitted cockpit drawn over the open-cabin body (Addendum 03: a cabin lost to
        ///    exterior culling is a defect).
        /// 2. Snapshots through the race: every other car held full, mid and far, then chosen by distance (the saving);
        ///    then each car on its own held at each level while the rest are chosen — the level whose count equals the
        ///    chosen state is the level that car was actually drawn at, which must be the level <see cref="VehicleView"/>
        ///    chose for it.
        /// Frames are saved under Builds/Screenshots/car-lod.
        /// </summary>
        IEnumerator CarLodTour()
        {
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.CarLodTour] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }

            yield return new WaitForSeconds(3f);
            var free = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10 };
            var field = Enumerable.Range(1, 11).Select(i => $"ai-{i}").ToList();
            StartCoroutine(RunOfflineRace("C01", "V01", free, field, false, null));
            float until = Time.realtimeSinceStartup + 60f;
            while ((activeRace == null || activeRace.Phase != MatchPhase.Countdown) && Time.realtimeSinceStartup < until) yield return null;
            if (activeRace == null) { Fail("the race did not start"); Finish(); yield break; }
            activeRace.Autopilot = true;
            until = Time.realtimeSinceStartup + 30f;
            while (activeRace != null && activeRace.Phase != MatchPhase.Racing && Time.realtimeSinceStartup < until) yield return null;
            VehicleView own = activeRace?.PlayerView;
            DrivingCamera cam = activeRace?.Camera;
            if (own == null || cam == null || own.Lods == null) { Fail("no player car, camera or LOD group"); Finish(); yield break; }
            var others = activeRace.Views.Where(v => v != own && v.Lods != null).ToList();
            Note($"racing: {activeRace.Views.Count} cars; quality level {QualitySettings.names[QualitySettings.GetQualityLevel()]}, " +
                 $"LOD bias {QualitySettings.lodBias}, vSync {QualitySettings.vSyncCount}");

            var triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            string shots = Path.GetFullPath(Path.Combine("Builds", "Screenshots", "car-lod"));
            Directory.CreateDirectory(shots);
            double tris = 0;
            Color32[] pixels = null;
            IEnumerator Count()
            {
                for (int i = 0; i < 3; i++) yield return null;
                double sum = 0;
                for (int i = 0; i < 3; i++)
                {
                    yield return null;
                    sum += triangles.Valid ? triangles.LastValue : 0;
                }
                tris = sum / 3;
            }
            IEnumerator Capture(string keep)
            {
                yield return Count();
                yield return new WaitForEndOfFrame();
                Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                pixels = shot.GetPixels32();
                if (keep != null) File.WriteAllBytes(Path.Combine(shots, keep + ".jpg"), shot.EncodeToJPG(90));
                Destroy(shot);
            }
            int Diff(Color32[] a, Color32[] b)
            {
                int n = 0;
                for (int i = 0; i < a.Length; i++)
                    if (Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b) > 12) n++;
                return n;
            }

            // 1. Your car in every view.
            Mesh closed = own.Body.GetComponent<MeshFilter>().sharedMesh;
            DrivingView before = cam.View;
            foreach (DrivingView v in Enum.GetValues(typeof(DrivingView)))
            {
                cam.SetView(v, save: false);
                yield return new WaitForSeconds(1.5f); // the camera settles behind the moving car
                Time.timeScale = 0f;
                yield return Capture($"own-{v}");
                Color32[] chosenPx = pixels;
                double chosenTris = tris;
                int chosen = own.LodLevel;
                yield return Count();
                double repeat = Math.Abs(tris - chosenTris);
                own.HoldLod(0);
                yield return Count();
                double fullTris = tris;
                own.HoldLod(2);
                yield return Count();
                double farTris = tris;
                own.HoldLod(-1);
                bool open = own.Body.GetComponent<MeshFilter>().sharedMesh != closed;
                int cockpitPx = -1, noise = -1;
                if (v == DrivingView.Cockpit && own.Cockpit != null)
                {
                    yield return Capture(null);
                    noise = Diff(chosenPx, pixels);
                    own.Cockpit.Root.gameObject.SetActive(false);
                    yield return Capture(null);
                    cockpitPx = Diff(chosenPx, pixels);
                    own.Cockpit.Root.gameObject.SetActive(true);
                }
                Time.timeScale = 1f;
                Note($"view {v} (fov {cam.Camera.fieldOfView:F1}): own car chosen level {chosen}; triangles chosen {chosenTris / 1000.0:F1} k (repeat ±{repeat:F0}), " +
                     $"held full {fullTris / 1000.0:F1} k, held far {farTris / 1000.0:F1} k; open-cabin body {open}" +
                     (cockpitPx >= 0 ? $"; pixels changed with the cockpit hidden {cockpitPx} (repeat {noise})" : ""));
                if (chosen != 0) Fail($"{v}: your car chose level {chosen}, not its full body");
                if (Math.Abs(fullTris - chosenTris) > repeat + 50) Fail($"{v}: your car draws {chosenTris - fullTris:F0} triangles different from its full body");
                if (v == DrivingView.Cockpit)
                {
                    if (!open) Fail("Cockpit: the open-cabin body is not shown");
                    if (cockpitPx <= Math.Max(50, noise * 2)) Fail($"Cockpit: the fitted cockpit is not drawn ({cockpitPx} pixels, repeat {noise})");
                }
                else if (open) Fail($"{v}: the open-cabin body showed outside Cockpit view");
            }
            cam.SetView(DrivingView.ChaseClose, save: false);

            // 2. Snapshots through the race.
            string[] levelName = { "full", "mid", "far" };
            int inView = 0, drawnMid = 0, drawnFar = 0, farOnFull = 0, nonMonotonic = 0, mismatched = 0, savedSnaps = 0, lowerSnaps = 0, snaps = 0;
            var table = new List<string>();
            for (int snap = 0; snap < 6; snap++)
            {
                yield return new WaitForSeconds(snap == 0 ? 4f : 9f);
                if (activeRace == null || activeRace.Phase != MatchPhase.Racing) break;
                Time.timeScale = 0f;
                snaps++;
                Camera c = cam.Camera;
                foreach (VehicleView v in others) v.HoldLod(0);
                yield return Capture($"snap{snap}-full");
                double full = tris;
                foreach (VehicleView v in others) v.HoldLod(1);
                yield return Count();
                double mid = tris;
                foreach (VehicleView v in others) v.HoldLod(2);
                yield return Count();
                double far = tris;
                foreach (VehicleView v in others) v.HoldLod(-1);
                yield return Capture($"snap{snap}-chosen");
                double chosenTris = tris;
                yield return Count();
                double repeat = Math.Abs(tris - chosenTris);
                var cars = new List<(float D, float H, int Chosen, int Drawn)>();
                foreach (VehicleView v in others.OrderBy(v => Vector3.Distance(c.transform.position, v.transform.position)))
                {
                    int chosen = v.LodLevel;
                    var perLevel = new double[3];
                    for (int k = 0; k < 3; k++)
                    {
                        v.HoldLod(k);
                        yield return Count();
                        perLevel[k] = tris;
                    }
                    v.HoldLod(-1);
                    int drawn = -1;
                    if (Math.Abs(perLevel[0] - perLevel[2]) > repeat + 50)
                    {
                        double best = double.MaxValue;
                        for (int k = 0; k < 3; k++)
                            if (Math.Abs(perLevel[k] - chosenTris) < best) { best = Math.Abs(perLevel[k] - chosenTris); drawn = k; }
                        if (best > repeat + 50) drawn = -2; // matches no level: the chosen state was not one of them
                    }
                    cars.Add((Vector3.Distance(c.transform.position, v.transform.position), v.RelativeHeight(c), chosen, drawn));
                }
                yield return Count(); // every car back on its chosen level: the count returns to the chosen state
                double back = tris;
                Time.timeScale = 1f;
                if (cars.Any(x => x.Drawn > 0)) { lowerSnaps++; if (full - chosenTris > 1000) savedSnaps++; }
                // Models differ in length by a few per cent, so a car only counts as out of order when it is 10 % farther.
                int lastLevel = 0;
                float lastDist = 0f;
                foreach (var car in cars)
                {
                    if (car.Drawn == -1) continue; // out of view (shadows included)
                    inView++;
                    if (car.Drawn != car.Chosen) mismatched++;
                    if (car.Drawn < 0) continue;
                    if (car.Drawn == 1) drawnMid++;
                    if (car.Drawn == 2) drawnFar++;
                    if (car.Drawn == 0 && car.D > 150f) farOnFull++;
                    if (car.Drawn < lastLevel && car.D > lastDist * 1.1f) nonMonotonic++;
                    if (car.Drawn > lastLevel) { lastLevel = car.Drawn; lastDist = car.D; }
                    table.Add($"{car.D:F0} m (h {car.H:F3}): chose {levelName[car.Chosen]}, drawn {levelName[car.Drawn]}");
                }
                Note($"snapshot {snap}: triangles all-full {full / 1000.0:F1} k, all-mid {mid / 1000.0:F1} k, all-far {far / 1000.0:F1} k, chosen {chosenTris / 1000.0:F1} k " +
                     $"(repeat ±{repeat:F0}, after the per-car holds {back / 1000.0:F1} k); cars " +
                     string.Join(", ", cars.Select(x => $"{x.D:F0} m {(x.Drawn == -1 ? "out of view" : x.Drawn == -2 ? "no match" : x.Chosen == x.Drawn ? levelName[x.Drawn] : $"chose {levelName[x.Chosen]} drew {levelName[x.Drawn]}")}")));
                if (Math.Abs(back - chosenTris) > repeat + 50) Fail($"snapshot {snap}: the scene did not return to the chosen state after the holds ({(back - chosenTris):F0} triangles)");
            }
            Time.timeScale = 1f;
            foreach (VehicleView v in others) v.HoldLod(-1);
            triangles.Dispose();
            cam.SetView(before, save: false);
            Note("in-view cars: " + string.Join("; ", table));
            Note($"{snaps} snapshots: the chosen levels drew fewer triangles than all-full in {savedSnaps} of the {lowerSnaps} with a car drawn below full; " +
                 $"{inView} in-view car samples — drawn mid {drawnMid}, far {drawnFar}; drawn level differs from the chosen one {mismatched}; " +
                 $"full beyond 150 m {farOnFull}; farther car drawn finer than a nearer one {nonMonotonic}; frames in {shots}");
            if (drawnMid == 0 || drawnFar == 0) Fail($"not every level was drawn (mid {drawnMid}, far {drawnFar}): LOD not fully exercised");
            if (mismatched > 0) Fail($"{mismatched} in-view samples drawn at a level other than the one chosen");
            if (farOnFull > 0) Fail($"{farOnFull} in-view samples of a car beyond 150 m on its full body");
            if (nonMonotonic > 0) Fail($"{nonMonotonic} samples where a farther car was drawn finer than a nearer one");
            if (savedSnaps < lowerSnaps) Fail($"chosen levels saved triangles in only {savedSnaps} of {lowerSnaps} snapshots");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
