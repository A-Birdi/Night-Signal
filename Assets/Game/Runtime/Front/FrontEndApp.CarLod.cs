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
        /// which level was drawn; every check is made on frozen frames instead (the race paused) from the rendered
        /// triangle count. The whole scene's count drifts by a few thousand triangles even when paused, so a car's level is
        /// measured locally: each hold (full, mid, far) is bracketed by releases, and the hold that counts the same as the
        /// releases on either side is the level Unity drew.
        /// 1. Your car in each of the five views: drawn at its full body, and in Cockpit view the fitted cockpit drawn over
        ///    the open-cabin body (Addendum 03: a cabin lost to exterior culling is a defect).
        /// 2. Snapshots through the race: every other car's drawn level against Unity's screen-height rule at its distance,
        ///    and the triangles saved against every car held full.
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
            string[] levelName = { "full", "mid", "far" };
            string Name(int level) => level >= 0 ? levelName[level] : level == -1 ? "not drawn" : "no match";
            // The level drawn (0-2), −1 not drawn (every held level counts the same: out of view, shadows included), −2 none matches.
            int[] drawn = new int[1];
            string raw = "";
            // Each hold is bracketed by releases (R H0 R H1 R H2 R): holding the level that is already drawn changes nothing,
            // so the drawn level is the hold that counts the same as the releases on either side of it, within their drift.
            IEnumerator Drawn(VehicleView v)
            {
                var released = new double[4];
                var held = new double[3];
                v.HoldLod(-1);
                yield return Count();
                released[0] = tris;
                for (int k = 0; k < 3; k++)
                {
                    v.HoldLod(k);
                    yield return Count();
                    held[k] = tris;
                    v.HoldLod(-1);
                    yield return Count();
                    released[k + 1] = tris;
                }
                var same = new bool[3];
                for (int k = 0; k < 3; k++)
                {
                    double drift = Math.Abs(released[k + 1] - released[k]);
                    same[k] = Math.Abs(held[k] - (released[k] + released[k + 1]) * 0.5) <= drift * 0.5 + 50;
                }
                raw = $"released {string.Join(" / ", released.Select(x => x.ToString("F0")))}, held full {held[0]:F0}, mid {held[1]:F0}, far {held[2]:F0}";
                int count = same.Count(x => x);
                drawn[0] = count == 3 ? -1 : count == 1 ? Array.IndexOf(same, true) : -2;
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
                Color32[] shownPx = pixels;
                yield return Drawn(own);
                int level = drawn[0];
                bool open = own.Body.GetComponent<MeshFilter>().sharedMesh != closed;
                int cockpitPx = -1, noise = -1;
                if (v == DrivingView.Cockpit && own.Cockpit != null)
                {
                    yield return Capture(null);
                    noise = Diff(shownPx, pixels);
                    own.Cockpit.Root.gameObject.SetActive(false);
                    yield return Capture(null);
                    cockpitPx = Diff(shownPx, pixels);
                    own.Cockpit.Root.gameObject.SetActive(true);
                }
                Time.timeScale = 1f;
                Note($"view {v} (fov {cam.Camera.fieldOfView:F1}): your car drawn {Name(level)} (rule {Name(own.RuleLod(own.RelativeHeight(cam.Camera)))}); " +
                     $"open-cabin body {open}" + (cockpitPx >= 0 ? $"; pixels changed with the cockpit hidden {cockpitPx} (repeat {noise})" : ""));
                // Bumper view may not see the body at all (then every level counts the same: not drawn, nothing to lose).
                if (level != 0 && level != -1) Fail($"{v}: your car drawn {Name(level)}, not its full body");
                if (level == -1 && v != DrivingView.Bumper) Fail($"{v}: your car's body not seen at all");
                if (v == DrivingView.Cockpit)
                {
                    if (!open) Fail("Cockpit: the open-cabin body is not shown");
                    if (cockpitPx <= Math.Max(50, noise * 2)) Fail($"Cockpit: the fitted cockpit is not drawn ({cockpitPx} pixels, repeat {noise})");
                }
                else if (open) Fail($"{v}: the open-cabin body showed outside Cockpit view");
            }
            cam.SetView(DrivingView.ChaseClose, save: false);

            // 2. Snapshots through the race.
            int inView = 0, drawnMid = 0, drawnFar = 0, agree = 0, atBoundary = 0, unmatched = 0, savedSnaps = 0, lowerSnaps = 0, snaps = 0;
            float[] lodHeights = own.Lods.GetLODs().Select(l => l.screenRelativeTransitionHeight).ToArray();
            var table = new List<string>();
            var disagreements = new List<string>();
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
                foreach (VehicleView v in others) v.HoldLod(2);
                yield return Count();
                double far = tris;
                foreach (VehicleView v in others) v.HoldLod(-1);
                yield return Capture($"snap{snap}-auto");
                double auto = tris;
                var cars = new List<(float D, float H, int Rule, int Drawn)>();
                var raws = new List<string>();
                foreach (VehicleView v in others.OrderBy(v => Vector3.Distance(c.transform.position, v.transform.position)))
                {
                    yield return Drawn(v);
                    float h = v.RelativeHeight(c);
                    cars.Add((Vector3.Distance(c.transform.position, v.transform.position), h, v.RuleLod(h), drawn[0]));
                    raws.Add(raw);
                }
                Time.timeScale = 1f;
                if (cars.Any(x => x.Drawn > 0)) { lowerSnaps++; if (full - auto > 1000) savedSnaps++; }
                for (int i = 0; i < cars.Count; i++)
                {
                    var car = cars[i];
                    if (car.Drawn == -1) continue;
                    inView++;
                    if (car.Drawn == -2) { unmatched++; continue; }
                    if (car.Drawn == 1) drawnMid++;
                    if (car.Drawn == 2) drawnFar++;
                    string line = $"{car.D:F0} m (h {car.H:F3}): rule {Name(car.Rule)}, drawn {Name(car.Drawn)}";
                    table.Add(line);
                    if (car.Drawn == car.Rule) agree++;
                    else if (lodHeights.Any(t => Mathf.Abs(car.H / t - 1f) < 0.02f)) atBoundary++; // within 2 % of a transition
                    else disagreements.Add($"snapshot {snap} {line} ({raws[i]})");
                }
                Note($"snapshot {snap}: triangles all-full {full / 1000.0:F1} k, all-far {far / 1000.0:F1} k, automatic {auto / 1000.0:F1} k " +
                     $"({(full - auto) / 1000.0:F1} k saved); cars " + string.Join(", ", cars.Select(x => $"{x.D:F0} m {Name(x.Drawn)}")));
            }
            Time.timeScale = 1f;
            foreach (VehicleView v in others) v.HoldLod(-1);
            triangles.Dispose();
            cam.SetView(before, save: false);
            Note("in-view cars: " + string.Join("; ", table));
            Note($"{snaps} snapshots: automatic LOD drew fewer triangles than all-full in {savedSnaps} of the {lowerSnaps} with a car drawn below full; " +
                 $"{inView} in-view car samples — drawn mid {drawnMid}, far {drawnFar}; at the rule's level {agree}; off it within 2 % of a transition {atBoundary}; " +
                 $"unmatched {unmatched}; frames in {shots}");
            if (disagreements.Count > 0) Fail("drawn level differs from Unity's rule: " + string.Join("; ", disagreements));
            if (drawnMid == 0 || drawnFar == 0) Fail($"not every level was drawn (mid {drawnMid}, far {drawnFar}): LOD not fully exercised");
            if (unmatched > inView / 10) Fail($"{unmatched} of {inView} in-view samples matched no level");
            if (savedSnaps < lowerSnaps) Fail($"automatic LOD saved triangles in only {savedSnaps} of {lowerSnaps} snapshots");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
