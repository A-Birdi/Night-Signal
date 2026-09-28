using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NightSignal.Editor.Courses;
using NightSignal.Track;
using NightSignal.Track.Generation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NightSignal.Editor.ArtTools
{
    /// <summary>
    /// Photographs every route landmark of a course from its road, in the course scene's own lighting: one tile per landmark
    /// in route order (a contact sheet per course) and an index with what was built (object, vertices, collider, distance
    /// from the road, whether a route section draws it instead). Opens each course scene in the editor (its generated
    /// preview never saves). Output: Builds/Screenshots/landmarks/.
    /// </summary>
    public static class LandmarkSheet
    {
        [MenuItem("Night Signal/Art/Render Landmark Sheets")]
        public static void RenderAllMenu() => Debug.Log(RenderAll(null));

        public static string RenderAll(string[] courses)
        {
            string dir = Path.GetFullPath(Path.Combine("Builds", "Screenshots", "landmarks"));
            Directory.CreateDirectory(dir);
            var ids = new List<string>();
            if (courses != null) ids.AddRange(courses);
            else
                foreach (string folder in Directory.GetDirectories("Assets/Content/Courses"))
                {
                    string id = Path.GetFileName(folder);
                    if (File.Exists(Path.Combine(folder, "route.json")) && File.Exists(CourseSceneAuthoring.ScenePath(id))) ids.Add(id);
                }
            ids.Sort(System.StringComparer.Ordinal);
            var report = new StringBuilder();
            foreach (string id in ids) report.AppendLine(RenderCourse(id, dir));
            File.WriteAllText(Path.Combine(dir, "index.txt"), report.ToString());
            return report.ToString();
        }

        public static string RenderCourse(string id, string dir, int tileWidth = 640, int tileHeight = 360, int columns = 4)
        {
            int missingKits = 0;
            void OnLog(string message, string stack, LogType type)
            {
                if (message.Contains("No kit")) missingKits++;
            }
            Application.logMessageReceived += OnLog;
            float seconds;
            try
            {
                EditorSceneManager.OpenScene(CourseSceneAuthoring.ScenePath(id), OpenSceneMode.Single);
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }
            CourseRuntime rt = CourseRuntime.Active != null ? CourseRuntime.Active : Object.FindFirstObjectByType<CourseRuntime>();
            if (rt == null) return $"{id}: no CourseRuntime";
            if (rt.Track == null) rt.Generate();
            seconds = rt.GenerationSeconds;
            TrackData track = rt.Track;
            List<RouteLandmarkDef> lms = rt.Route.Landmarks;
            Transform landmarksRoot = null;
            foreach (Transform t in rt.GetComponentsInChildren<Transform>(true))
                if (t.name == "Landmarks") { landmarksRoot = t; break; }

            var camGo = new GameObject("LandmarkCamera") { hideFlags = HideFlags.DontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 50f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 6000f;
            var rtex = new RenderTexture(tileWidth, tileHeight, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rtex;
            int rows = Mathf.Max(1, (lms.Count + columns - 1) / columns);
            var sheet = new Texture2D(tileWidth * columns, tileHeight * rows, TextureFormat.RGB24, false);
            var read = new Texture2D(tileWidth, tileHeight, TextureFormat.RGB24, false);
            var lines = new StringBuilder();
            var used = new HashSet<Transform>();
            try
            {
                for (int i = 0; i < lms.Count; i++)
                {
                    RouteLandmarkDef lm = lms[i];
                    Transform obj = null;
                    if (landmarksRoot != null)
                        foreach (Transform c in landmarksRoot)
                            if (c.name == lm.Name && !used.Contains(c)) { obj = c; used.Add(c); break; }
                    var b = new Bounds(track.SampleAt(lm.AtMetres).Position, Vector3.one * 10f);
                    int verts = 0;
                    bool collider = false, any = false;
                    if (obj != null)
                    {
                        foreach (MeshFilter mf in obj.GetComponentsInChildren<MeshFilter>(true))
                        {
                            if (mf.sharedMesh == null) continue;
                            verts += mf.sharedMesh.vertexCount;
                            Renderer r = mf.GetComponent<Renderer>();
                            if (r == null) continue;
                            if (!any) { b = r.bounds; any = true; }
                            else b.Encapsulate(r.bounds);
                        }
                        collider = obj.GetComponentInChildren<Collider>(true) != null;
                    }
                    // Nearest road point to the object, and how far the object's bounds keep from the road centre there.
                    float nearest = float.MaxValue, nearestAt = lm.AtMetres;
                    foreach (TrackSample s in track.Samples)
                    {
                        float dd = (new Vector3(s.Position.x, 0f, s.Position.z) - new Vector3(b.center.x, 0f, b.center.z)).sqrMagnitude;
                        if (dd < nearest) { nearest = dd; nearestAt = s.Distance; }
                    }
                    // Camera: on the road some way before the landmark, raised, looking at it; backed off for big objects.
                    TrackSample road = track.SampleAt(Mathf.Repeat(lm.AtMetres - 45f, track.LengthMetres));
                    Vector3 eye = road.Position + Vector3.up * 6f;
                    float size = b.extents.magnitude;
                    if ((eye - b.center).magnitude < size * 1.6f || size > 60f)
                        eye = b.center + (eye - b.center).normalized * Mathf.Max(size * 1.3f, 25f) + Vector3.up * Mathf.Max(size * 0.7f, 15f); // above hills
                    cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(b.center - eye, Vector3.up));
                    cam.Render();
                    RenderTexture.active = rtex;
                    read.ReadPixels(new Rect(0, 0, tileWidth, tileHeight), 0, 0);
                    read.Apply();
                    RenderTexture.active = null;
                    int cx = i % columns, cy = rows - 1 - i / columns;
                    sheet.SetPixels(cx * tileWidth, cy * tileHeight, tileWidth, tileHeight, read.GetPixels());
                    string status = obj != null ? "built" : "none (a route section draws it, or no kit)";
                    lines.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,2} {1,-24} {2,-9} {3,-20} {4} verts={5} collider={6} size={7:F0}m",
                        i + 1, lm.Id, lm.Kit, lm.Params != null && lm.Params.TryGetValue("type", out object ty) ? ty : "-", status, verts, collider, b.size.magnitude));
                }
            }
            finally
            {
                cam.targetTexture = null;
                Object.DestroyImmediate(rtex);
                Object.DestroyImmediate(read);
                Object.DestroyImmediate(camGo);
            }
            sheet.Apply();
            File.WriteAllBytes(Path.Combine(dir, $"landmarks-{id}.png"), sheet.EncodeToPNG());
            Object.DestroyImmediate(sheet);
            return $"{id}: {lms.Count} landmarks, generation {seconds:F2} s, missing kits {missingKits}\n{lines}";
        }
    }
}
