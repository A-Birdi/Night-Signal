using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Art;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Every authored tunnel/gallery section is built on its real generated course (T04, C03, C09 prerequisites): a roof over
    /// the whole section the camera can collide with, lining walls outside the road's own barriers (never in the driving
    /// corridor), the road still drivable under it, lights along it, and portal/interior renders for review
    /// (Builds/Screenshots/tunnels/). Structural checks by physics queries, not a visual judgement.
    /// </summary>
    public sealed class TunnelShellTests
    {
        static readonly string[] Courses = { "C08", "C12", "C21", "C22", "C24", "C25", "FP02" };

        [UnityTest, Timeout(600000)]
        public IEnumerator EveryTunnelSection_IsBuilt_Roofed_Clear_AndDrivable([ValueSource(nameof(Courses))] string course)
        {
            yield return LoadCourse(course);
            CourseRuntime rt = CourseRuntime.Active;
            TrackData track = rt.Track;
            var sections = TunnelGeometry.Tunnels(rt.Route).ToList();
            Assert.That(sections.Count, Is.GreaterThan(0), $"{course} authors a tunnel");
            string dir = Path.GetFullPath(Path.Combine("Builds", "Screenshots", "tunnels"));
            Directory.CreateDirectory(dir);
            var lines = new List<string>();
            int lights = Object.FindObjectsByType<Light>().Count(l => l.name == "TunnelLight");
            foreach (RouteSectionDef sec in sections)
            {
                int roofed = 0, probes = 0, drivable = 0, intrusions = 0;
                float minRoof = float.MaxValue, maxRoof = 0f;
                for (float d = sec.FromMetres + 1f; d < sec.ToMetres - 1f; d += 5f)
                {
                    TrackSample s = track.SampleAt(d);
                    probes++;
                    Vector3 onRoad = s.Position + s.Up * 1f;
                    if (Physics.Raycast(onRoad, s.Up, out RaycastHit roof, 12f, 1 << GameLayers.Scenery, QueryTriggerInteraction.Ignore))
                    {
                        roofed++;
                        minRoof = Mathf.Min(minRoof, roof.distance + 1f);
                        maxRoof = Mathf.Max(maxRoof, roof.distance + 1f);
                    }
                    if (Physics.Raycast(s.Position + s.Up * 2f, -s.Up, 3f, GameLayers.DrivableMask, QueryTriggerInteraction.Ignore)) drivable++;
                    // The driving corridor between the road's own barriers, 0.3–4 m up, holds no tunnel structure.
                    float ll = RoadGeometry.BarrierLateral(s, -1), lr = RoadGeometry.BarrierLateral(s, 1);
                    Vector3 centre = s.Position + s.Right * ((ll + lr) * 0.5f) + s.Up * 2.15f;
                    Collider[] hits = Physics.OverlapBox(centre, new Vector3((lr - ll) * 0.5f - 0.3f, 1.85f, 0.9f), Quaternion.LookRotation(s.Tangent, s.Up),
                        GameLayers.BarrierMask | (1 << GameLayers.Scenery), QueryTriggerInteraction.Ignore);
                    if (hits.Any(h => h.transform.parent != null && h.transform.parent.name == "Tunnels")) intrusions++;
                }
                lines.Add($"{course} {sec.Style} {sec.FromMetres:F0}–{sec.ToMetres:F0} m: roofed {roofed}/{probes} (roof {minRoof:F1}–{maxRoof:F1} m above the road), drivable {drivable}/{probes}, corridor intrusions {intrusions}");
                Assert.That(roofed, Is.EqualTo(probes), $"{course} {sec.Style}: roof over the whole section");
                Assert.That(drivable, Is.EqualTo(probes), $"{course} {sec.Style}: road still drivable inside");
                Assert.That(intrusions, Is.EqualTo(0), $"{course} {sec.Style}: nothing of the tunnel inside the driving corridor");
                Assert.That(minRoof, Is.GreaterThan(4.8f), "headroom for the tallest car and the chase camera to compress under");

                // Review renders: approaching the portal at chase height, and inside at mid-section.
                Render(track, sec.FromMetres - 28f, Path.Combine(dir, $"{course}-{sec.Style}-portal.png"));
                Render(track, (sec.FromMetres + sec.ToMetres) * 0.5f, Path.Combine(dir, $"{course}-{sec.Style}-inside.png"));
            }
            lines.Add($"{course}: {lights} tunnel lights");
            foreach (string l in lines) TestContext.WriteLine(l);
            File.AppendAllLines(Path.Combine(dir, "tunnels.txt"), lines);
            Assert.That(lights, Is.GreaterThan(0), "lit");
        }

        static void Render(TrackData track, float at, string path)
        {
            TrackSample s = track.SampleAt(Mathf.Clamp(at, 0f, track.LengthMetres));
            var go = new GameObject("TunnelReviewCamera", typeof(Camera));
            Camera cam = go.GetComponent<Camera>();
            cam.fieldOfView = 58f;
            cam.nearClipPlane = 0.08f;
            cam.farClipPlane = 3000f;
            go.transform.SetPositionAndRotation(s.Position + s.Up * 2.2f - s.Tangent * 2f, Quaternion.LookRotation(s.Tangent, s.Up) * Quaternion.Euler(4f, 0f, 0f));
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            cam.targetTexture = null;
            Object.Destroy(rt);
            Object.Destroy(tex);
            Object.Destroy(go);
        }

        static IEnumerator LoadCourse(string course)
        {
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                $"Assets/Content/Courses/{course}/{course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#else
            Assert.Ignore("Editor-only scene loading");
            yield break;
#endif
            yield return null;
            Assert.That(CourseRuntime.Active, Is.Not.Null, "course generated");
        }
    }
}
