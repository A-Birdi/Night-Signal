using System;
using System.Collections;
using System.Collections.Generic;
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
        /// First your car in each of the five driving views: it must be drawn with its full body every frame, and in Cockpit
        /// view with the open-cabin body and the fitted cockpit (Addendum 03: a cabin lost to exterior culling is a defect).
        /// Then alternating blocks with the other cars on their automatic levels and forced to the full body: the level each
        /// car was actually drawn at (<see cref="Renderer.isVisible"/>) against its distance from the camera, and the
        /// rendered triangles and frame time of each block. Measured in the built player, not modelled.
        /// </summary>
        IEnumerator CarLodTour()
        {
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.CarLodTour] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }

            // The level a car was drawn at in the last rendered frame: 0-2, −1 not drawn (off screen); a car seen at two
            // levels at once would report the lower one and count as a conflict.
            int conflicts = 0;
            int Drawn(VehicleView v)
            {
                Renderer full = v.Body.GetComponent<Renderer>();
                Renderer mid = v.Body.Find("BodyLod1")?.GetComponent<Renderer>();
                Renderer far = v.Body.Find("BodyLod2")?.GetComponent<Renderer>();
                int shown = (full.isVisible ? 1 : 0) + (mid != null && mid.isVisible ? 1 : 0) + (far != null && far.isVisible ? 1 : 0);
                if (shown > 1) conflicts++;
                if (full.isVisible) return 0;
                if (mid != null && mid.isVisible) return 1;
                if (far != null && far.isVisible) return 2;
                return -1;
            }

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
            Note($"racing: {activeRace.Views.Count} cars; quality level {QualitySettings.names[QualitySettings.GetQualityLevel()]}, " +
                 $"LOD bias {QualitySettings.lodBias}, vSync {QualitySettings.vSyncCount}, target {Application.targetFrameRate} fps");

            // 1. Your car in every view.
            Mesh closed = own.Body.GetComponent<MeshFilter>().sharedMesh;
            DrivingView before = cam.View;
            foreach (DrivingView v in Enum.GetValues(typeof(DrivingView)))
            {
                cam.SetView(v, save: false);
                for (int i = 0; i < 10; i++) yield return null; // settle after the cut
                int frames = 0, full = 0, cockpitShown = 0, openBody = 0;
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 3f)
                {
                    yield return new WaitForEndOfFrame();
                    frames++;
                    if (Drawn(own) == 0) full++;
                    if (own.Cockpit != null && own.Cockpit.Root.GetComponentsInChildren<Renderer>().Any(r => r.isVisible)) cockpitShown++;
                    if (own.Body.GetComponent<MeshFilter>().sharedMesh != closed) openBody++;
                }
                Note($"view {v}: own car full body {full}/{frames} frames, open-cabin body {openBody}, cockpit drawn {cockpitShown}; fov {cam.Camera.fieldOfView:F1}");
                if (full < frames) Fail($"{v}: your car left its full body in {frames - full} of {frames} frames");
                if (v == DrivingView.Cockpit && (cockpitShown < frames || openBody < frames))
                    Fail($"Cockpit: cockpit drawn {cockpitShown}/{frames}, open-cabin body {openBody}/{frames}");
                if (v != DrivingView.Cockpit && openBody > 0) Fail($"{v}: the open-cabin body showed outside Cockpit view");
            }
            cam.SetView(DrivingView.ChaseClose, save: false);

            // 2. Automatic levels against everything forced to the full body, in alternating blocks.
            var triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            var others = activeRace.Views.Where(v => v != own && v.Lods != null).ToList();
            float[] edges = { 25f, 50f, 100f, 200f, 400f, float.MaxValue };
            string[] bandNames = { "<25 m", "25-50 m", "50-100 m", "100-200 m", "200-400 m", ">400 m" };
            var byBand = new int[edges.Length, 4]; // level 0, 1, 2, not drawn
            int farOnFull = 0;
            float farthest = 0f;
            var blockTris = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
            var blockMs = new Dictionary<bool, List<float>> { [true] = new List<float>(), [false] = new List<float>() };
            for (int block = 0; block < 8 && activeRace != null && activeRace.Phase == MatchPhase.Racing; block++)
            {
                bool auto = block % 2 == 0;
                foreach (VehicleView v in others) v.Lods.ForceLOD(auto ? -1 : 0);
                for (int i = 0; i < 5; i++) yield return null;
                var tris = new List<double>();
                var ms = new List<float>();
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 6f && activeRace != null)
                {
                    yield return new WaitForEndOfFrame();
                    ms.Add(Time.unscaledDeltaTime * 1000f);
                    if (triangles.Valid) tris.Add(triangles.LastValue);
                    if (!auto) continue;
                    Vector3 eye = cam.Camera.transform.position;
                    foreach (VehicleView v in others)
                    {
                        float d = Vector3.Distance(eye, v.transform.position);
                        farthest = Mathf.Max(farthest, d);
                        int level = Drawn(v);
                        int band = 0;
                        while (d >= edges[band]) band++;
                        byBand[band, level < 0 ? 3 : level]++;
                        if (level == 0 && d > 120f) farOnFull++;
                    }
                }
                ms.Sort();
                double meanTris = tris.Count > 0 ? tris.Average() : -1;
                float meanMs = ms.Average(), p95 = ms[(int)(ms.Count * 0.95f)];
                blockTris[auto].Add(meanTris);
                blockMs[auto].Add(meanMs);
                Note($"block {block} ({(auto ? "automatic LOD" : "all forced full")}): {ms.Count} frames, mean {meanMs:F2} ms, p95 {p95:F2} ms; " +
                     $"triangles {(tris.Count > 0 ? $"{meanTris / 1000.0:F0} k" : "unavailable")}");
            }
            foreach (VehicleView v in others) v.Lods.ForceLOD(-1);
            triangles.Dispose();
            cam.SetView(before, save: false);

            for (int b = 0; b < edges.Length; b++)
                Note($"{bandNames[b]}: full {byBand[b, 0]}, mid {byBand[b, 1]}, far {byBand[b, 2]}, not drawn {byBand[b, 3]}");
            int midOrFar = Enumerable.Range(0, edges.Length).Sum(b => byBand[b, 1] + byBand[b, 2]);
            Note($"farthest car {farthest:F0} m; samples on a lower level {midOrFar}; beyond 120 m on the full body {farOnFull}; two levels at once {conflicts}");
            if (blockTris[true].Count > 0 && blockTris[true][0] >= 0 && blockTris[false].Count > 0)
            {
                double a = blockTris[true].Average(), f = blockTris[false].Average();
                Note($"triangles: automatic {a / 1000.0:F0} k vs forced full {f / 1000.0:F0} k per frame ({(f > 0 ? (1 - a / f) * 100 : 0):F0} % fewer); " +
                     $"frame time automatic {blockMs[true].Average():F2} ms vs forced {blockMs[false].Average():F2} ms");
                if (midOrFar > 0 && a >= f) Fail("automatic levels drew no fewer triangles than the full bodies");
            }
            else Note("triangle counter unavailable in this player: the level table stands alone");
            if (midOrFar == 0) Fail($"no car was ever drawn on a lower level (farthest {farthest:F0} m): LOD not exercised");
            if (farOnFull > 0) Fail($"{farOnFull} samples of a car beyond 120 m on its full body");
            if (conflicts > 0) Fail($"{conflicts} samples of a car drawn at two levels at once");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
