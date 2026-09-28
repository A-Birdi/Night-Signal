using System.Collections.Generic;
using System.IO;
using NightSignal.Editor.Courses;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace NightSignal.Tests.Track
{
    /// <summary>
    /// Course scenery (landmark kits): every route landmark on every course is built — or drawn by the route section it
    /// sits on — and nothing a kit builds reaches onto the road: no vertex over the paved road or its shoulders unless it
    /// clears the road by the overhead clearance (or lies below it), and no landmark collider on a layer the cars use.
    /// Scenery never changes how a course drives.
    /// </summary>
    public sealed class LandmarkKitTests
    {
        static IEnumerable<string> Courses()
        {
            var ids = new List<string>();
            foreach (string folder in Directory.GetDirectories("Assets/Content/Courses"))
                if (File.Exists(Path.Combine(folder, "route.json"))) ids.Add(Path.GetFileName(folder));
            ids.Sort(System.StringComparer.Ordinal);
            return ids;
        }

        [Test]
        public void Landmarks_AreBuilt_AndStayOffTheRoad([ValueSource(nameof(Courses))] string courseId)
        {
            string json = File.ReadAllText(RouteIO.RoutePath(courseId));
            RouteDefinition route = RouteIO.Parse(json);
            TrackData track = CourseGenerator.BuildTrackData(route, RouteIO.SourceHash(json));
            var mats = AssetDatabase.LoadAssetAtPath<CourseMaterialSet>(CourseSceneAuthoring.MaterialSetPath);
            var root = new GameObject("LandmarkTest");
            var failures = new List<string>();
            try
            {
                CourseGenerator.BuildGeometry(track, route, root.transform, mats, CourseSceneAuthoring.StyleFor(route.Biome, 7), GenerationProfile.Full);
                Transform landmarks = root.transform.Find("Landmarks");
                Assert.That(landmarks, Is.Not.Null);
                var built = new List<Transform>();
                foreach (Transform c in landmarks) built.Add(c);
                var used = new HashSet<Transform>();
                // Road index: one bucket per 20 m cell for nearest-sample queries.
                var grid = new Dictionary<long, List<int>>();
                long Key(float x, float z) => ((long)Mathf.FloorToInt(x / 20f) << 32) ^ (uint)Mathf.FloorToInt(z / 20f);
                for (int i = 0; i < track.Samples.Length; i++)
                {
                    long k = Key(track.Samples[i].Position.x, track.Samples[i].Position.z);
                    if (!grid.TryGetValue(k, out List<int> list)) grid[k] = list = new List<int>();
                    list.Add(i);
                }
                // The regional scatter is held to the same rule as the landmarks.
                Transform regional = root.transform.Find("Regional");
                Assert.That(regional != null && regional.childCount > 0, Is.True, "the course's regional kit was built");
                var checks = new List<(string Id, string Kit, Transform Obj)>();
                foreach (Transform chunk in regional) checks.Add((chunk.name, "regional", chunk));
                foreach (RouteLandmarkDef lm in route.Landmarks)
                {
                    Transform obj = null;
                    foreach (Transform c in built)
                        if (c.name == lm.Name && !used.Contains(c)) { obj = c; used.Add(c); break; }
                    if (obj == null)
                    {
                        RouteSectionDef sec = null;
                        foreach (RouteSectionDef s in route.Sections)
                            if (lm.AtMetres >= s.FromMetres - 5f && lm.AtMetres <= s.ToMetres + 5f) sec = s;
                        bool drawnBySection = sec != null && ((lm.Kit == "crossing" && (sec.Kind == "bridge" || sec.Kind == "viaduct")) ||
                                                              (lm.Kit == "wall" && sec.Kind == "tunnel"));
                        if (!drawnBySection) failures.Add($"{lm.Id} ({lm.Kit}) was not built");
                        continue;
                    }
                    // The bespoke stone bridge's parapets are the road's barrier there by design (it removes the guardrail).
                    foreach (Collider col in obj.GetComponentsInChildren<Collider>(true))
                        if (col.gameObject.layer != Art.GameLayers.Scenery && !(lm.Kit == "stone-bridge" && col.gameObject.layer == Art.GameLayers.Barrier))
                            failures.Add($"{lm.Id}: collider on layer {col.gameObject.layer} (cars use Drivable/Barrier)");
                    checks.Add((lm.Id, lm.Kit, obj));
                }
                foreach ((string Id, string Kit, Transform Obj) check in checks)
                {
                    Transform obj = check.Obj;
                    string lmId = check.Id, lmKit = check.Kit;
                    if (lmKit == "regional" && obj.GetComponentInChildren<Collider>(true) != null) failures.Add($"{lmId}: regional scatter has a collider");
                    foreach (MeshFilter mf in obj.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh == null) continue;
                        Vector3[] v = mf.sharedMesh.vertices;
                        int bad = 0;
                        Vector3 firstBad = default;
                        foreach (Vector3 local in v)
                        {
                            Vector3 p = mf.transform.TransformPoint(local);
                            // Nearest road sample among the neighbouring cells.
                            float best = float.MaxValue;
                            int bi = -1;
                            for (int dx = -1; dx <= 1; dx++)
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                if (!grid.TryGetValue(Key(p.x + dx * 20f, p.z + dz * 20f), out List<int> list)) continue;
                                foreach (int i in list)
                                {
                                    Vector3 q = track.Samples[i].Position;
                                    float d = (q.x - p.x) * (q.x - p.x) + (q.z - p.z) * (q.z - p.z);
                                    if (d < best) { best = d; bi = i; }
                                }
                            }
                            if (bi < 0) continue;
                            TrackSample s = track.Samples[bi];
                            Vector3 flatRight = new Vector3(s.Right.x, 0f, s.Right.z).normalized;
                            float signedLateral = Vector3.Dot(p - s.Position, flatRight), lateral = Mathf.Abs(signedLateral);
                            float along = Mathf.Abs(Vector3.Dot(p - s.Position, new Vector3(s.Tangent.x, 0f, s.Tangent.z).normalized));
                            if (along > 1.5f) continue; // the nearest sample is not beside this point (past a road end)
                            float edge = s.Width * 0.5f + (signedLateral < 0f ? s.ShoulderLeft : s.ShoulderRight); // that side's shoulder
                            if (lateral >= edge) continue;
                            float above = p.y - s.Position.y;
                            if (above >= LandmarkKits.OverheadClearance - 0.2f || above <= -2f) continue;
                            if (bad++ == 0) firstBad = p;
                        }
                        if (bad > 0) failures.Add($"{lmId} ({lmKit}): {bad} vertices over the road below the clearance, e.g. {firstBad}");
                    }
                }
            }
            finally
            {
                foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true)) Object.DestroyImmediate(mf.sharedMesh);
                foreach (TerrainCollider tc in root.GetComponentsInChildren<TerrainCollider>(true)) Object.DestroyImmediate(tc.terrainData);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(track);
            }
            Assert.That(failures, Is.Empty, courseId + ":\n" + string.Join("\n", failures));
        }
    }
}
