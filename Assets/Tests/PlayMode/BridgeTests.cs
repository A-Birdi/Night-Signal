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
    /// Every authored bridge/viaduct section is carried by a structure on its real generated course (T04, C09 prerequisite):
    /// open ground under free spans (no embankment), the road still drivable on the deck, nothing of the bridge in the
    /// driving corridor, piers present, and no pier or girder standing in the corridor of any other road that passes
    /// beneath (overpasses, the two-level bridge). Side and approach renders for review (Builds/Screenshots/bridges/).
    /// Structural checks by physics queries, not a visual judgement.
    /// </summary>
    public sealed class BridgeTests
    {
        static readonly string[] Courses = { "C03", "C11", "C13", "C17", "C20", "C25" };

        [UnityTest, Timeout(600000)]
        public IEnumerator EveryBridgeSection_IsCarried_Clear_AndDrivable([ValueSource(nameof(Courses))] string course)
        {
            yield return LoadCourse(course);
            CourseRuntime rt = CourseRuntime.Active;
            TrackData track = rt.Track;
            var sections = BridgeGeometry.Bridges(rt.Route).ToList();
            Assert.That(sections.Count, Is.GreaterThan(0));
            var spans = BridgeGeometry.Spans(rt.Route);
            TerrainCollider ground = Object.FindAnyObjectByType<TerrainCollider>();
            string dir = Path.GetFullPath(Path.Combine("Builds", "Screenshots", "bridges"));
            Directory.CreateDirectory(dir);
            var lines = new List<string>();
            Transform bridges = GameObject.Find("Bridges")?.transform;
            Assert.That(bridges, Is.Not.Null, "bridges built");
            int piers = bridges.GetComponentsInChildren<MeshCollider>().Where(c => c.name.EndsWith("_Piers")).Sum(c => c.sharedMesh.vertexCount / 24);

            foreach (RouteSectionDef sec in sections)
            {
                bool free = spans.Any(sp => Mathf.Approximately(sp.From, sec.FromMetres));
                int probes = 0, drivable = 0, intrusions = 0, clear = 0;
                float minClearance = float.MaxValue;
                for (float d = sec.FromMetres + 2f; d < sec.ToMetres - 2f; d += 5f)
                {
                    TrackSample s = track.SampleAt(d);
                    probes++;
                    if (Physics.Raycast(s.Position + s.Up * 2f, -s.Up, out RaycastHit road, 3f, GameLayers.DrivableMask, QueryTriggerInteraction.Ignore) && road.collider != ground) drivable++;
                    if (CorridorHasBridge(s)) intrusions++;
                    bool inner = d > sec.FromMetres + 45f && d < sec.ToMetres - 45f;
                    if (free && inner && ground != null && ground.Raycast(new Ray(s.Position - Vector3.up * 1.7f, Vector3.down), out RaycastHit g, 400f))
                    {
                        minClearance = Mathf.Min(minClearance, g.distance);
                        if (g.distance >= 6f) clear++; else clear += 0;
                    }
                }
                // Other stretches of this course passing under the deck: their corridors must be free of bridge structure.
                int under = 0, underBlocked = 0;
                for (float d = 0f; d < track.LengthMetres; d += 4f)
                {
                    if (d > sec.FromMetres - 30f && d < sec.ToMetres + 30f) continue;
                    TrackSample o = track.SampleAt(d);
                    bool beneath = false;
                    for (float b = sec.FromMetres; b <= sec.ToMetres; b += 6f)
                    {
                        Vector3 p = track.SampleAt(b).Position;
                        if (new Vector2(p.x - o.Position.x, p.z - o.Position.z).magnitude < 14f && p.y - o.Position.y > 3f) { beneath = true; break; }
                    }
                    if (!beneath) continue;
                    under++;
                    if (CorridorHasBridge(o)) underBlocked++;
                }
                lines.Add($"{course} {sec.Kind} {sec.Style} {sec.FromMetres:F0}–{sec.ToMetres:F0} m: drivable {drivable}/{probes}, corridor intrusions {intrusions}, " +
                          (free ? $"ground ≥ 6 m below the deck at {clear} inner probes (min {minClearance:F1} m)" : "over another road (terrain kept)") +
                          $", other road under it at {under} probes, blocked {underBlocked}");
                Assert.That(drivable, Is.EqualTo(probes), $"{course} {sec.Style}: deck drivable");
                Assert.That(intrusions, Is.EqualTo(0), $"{course} {sec.Style}: nothing of the bridge in the driving corridor");
                Assert.That(underBlocked, Is.EqualTo(0), $"{course} {sec.Style}: no pier in the corridor of a road beneath");
                if (free) Assert.That(minClearance, Is.GreaterThan(6f), $"{course} {sec.Style}: open ground under the span");
                Render(track, sec, Path.Combine(dir, $"{course}-{sec.Style}-side.png"), true);
                Render(track, sec, Path.Combine(dir, $"{course}-{sec.Style}-approach.png"), false);
            }
            lines.Add($"{course}: {piers} pier columns");
            foreach (string l in lines) TestContext.WriteLine(l);
            File.AppendAllLines(Path.Combine(dir, "bridges.txt"), lines);
            Assert.That(piers, Is.GreaterThan(0), "piers");
        }

        static bool CorridorHasBridge(TrackSample s)
        {
            float ll = RoadGeometry.BarrierLateral(s, -1), lr = RoadGeometry.BarrierLateral(s, 1);
            Vector3 centre = s.Position + s.Right * ((ll + lr) * 0.5f) + s.Up * 2.15f;
            Collider[] hits = Physics.OverlapBox(centre, new Vector3((lr - ll) * 0.5f - 0.3f, 1.85f, 0.9f), Quaternion.LookRotation(s.Tangent, s.Up), ~0, QueryTriggerInteraction.Ignore);
            return hits.Any(h => h.transform.parent != null && h.transform.parent.name == "Bridges");
        }

        static void Render(TrackData track, RouteSectionDef sec, string path, bool side)
        {
            TrackSample mid = track.SampleAt((sec.FromMetres + sec.ToMetres) * 0.5f);
            var go = new GameObject("BridgeReviewCamera", typeof(Camera));
            Camera cam = go.GetComponent<Camera>();
            cam.fieldOfView = side ? 50f : 58f;
            cam.farClipPlane = 4000f;
            if (side)
            {
                float span = Mathf.Min(sec.ToMetres - sec.FromMetres, 300f);
                Vector3 at = mid.Position + Vector3.Cross(Vector3.up, mid.Tangent).normalized * (span * 0.9f + 40f) + Vector3.up * 4f;
                go.transform.position = at;
                go.transform.LookAt(mid.Position - Vector3.up * 6f);
            }
            else
            {
                TrackSample s = track.SampleAt(Mathf.Max(0f, sec.FromMetres - 40f));
                go.transform.SetPositionAndRotation(s.Position + s.Up * 2.2f, Quaternion.LookRotation(s.Tangent, s.Up) * Quaternion.Euler(4f, 0f, 0f));
            }
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
