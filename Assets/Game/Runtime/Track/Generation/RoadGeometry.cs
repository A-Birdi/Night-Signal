using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Deterministically generates road surface, shoulders, markings and guardrails from track samples into
    /// chunked meshes with colliders on the Drivable/Barrier layers. Ranges owned by landmark kits (e.g. bridge
    /// parapets) suppress the guardrail.
    /// </summary>
    public static class RoadGeometry
    {
        const float ChunkMetres = 120f;
        const float StepMetres = 2f;
        const float ShoulderDrop = 0.05f;
        const float BarrierGap = 0.35f;
        const float BarrierHeight = 1.05f;
        const float BarrierThickness = 0.3f;

        public struct Range
        {
            public float From, To;
            public bool Contains(float d) => d >= From && d <= To;
        }

        public static void Build(TrackData track, Transform parent, CourseMaterialSet mats, List<Range> noBarrier, GenerationProfile profile)
        {
            var roadRoot = new GameObject("Road").transform;
            roadRoot.SetParent(parent, false);
            var barrierRoot = new GameObject("Barriers").transform;
            barrierRoot.SetParent(parent, false);
            bool visuals = profile == GenerationProfile.Full;

            int chunkIndex = 0;
            for (float start = 0f; start < track.LengthMetres; start += ChunkMetres, chunkIndex++)
            {
                float end = Mathf.Min(track.LengthMetres, start + ChunkMetres);
                var sections = new List<TrackSample>();
                for (float d = start; d < end; d += StepMetres) sections.Add(track.SampleAt(d));
                sections.Add(track.SampleAt(end));

                string tag = $"{track.CourseId}_{chunkIndex:000}";
                Emit($"Road_{chunkIndex:000}", roadRoot, BuildSurface(sections, tag + "_road", true), GameLayers.Drivable, SurfaceKind.Asphalt, visuals ? mats.Asphalt : null);
                Emit($"Shoulders_{chunkIndex:000}", roadRoot, BuildSurface(sections, tag + "_shoulder", false), GameLayers.Drivable, SurfaceKind.Shoulder, visuals ? mats.Gravel : null);
                if (visuals)
                    Emit($"Markings_{chunkIndex:000}", roadRoot, BuildMarkings(sections, tag + "_markings"), -1, SurfaceKind.Asphalt, mats.LinePaint);

                foreach (int side in new[] { -1, 1 })
                {
                    int runIndex = 0;
                    foreach (List<TrackSample> run in SplitRuns(sections, noBarrier))
                    {
                        if (run.Count < 2) continue;
                        string n = $"{tag}_{(side < 0 ? "L" : "R")}{runIndex++}";
                        Emit("BarrierCollider_" + n, barrierRoot, BuildBarrierCollider(run, side, n + "_col"), GameLayers.Barrier, SurfaceKind.Concrete, null);
                        if (visuals)
                            Emit("Guardrail_" + n, barrierRoot, BuildGuardrailVisual(run, side, n + "_rail"), -1, SurfaceKind.Concrete, mats.Guardrail, mats.Post);
                    }
                }
            }
        }

        static List<List<TrackSample>> SplitRuns(List<TrackSample> sections, List<Range> noBarrier)
        {
            var runs = new List<List<TrackSample>> { new List<TrackSample>() };
            foreach (TrackSample s in sections)
            {
                bool blocked = false;
                foreach (Range r in noBarrier) if (r.Contains(s.Distance)) blocked = true;
                if (blocked)
                {
                    if (runs[runs.Count - 1].Count > 0) runs.Add(new List<TrackSample>());
                    continue;
                }
                runs[runs.Count - 1].Add(s);
            }
            return runs;
        }

        public static float BarrierLateral(TrackSample s, int side) =>
            side * (s.Width * 0.5f + (side < 0 ? s.ShoulderLeft : s.ShoulderRight) + BarrierGap);

        static Mesh BuildSurface(List<TrackSample> sections, string name, bool paved)
        {
            var mb = new MeshBuilder();
            float[] paveOffsets = { -0.5f, -0.25f, 0f, 0.25f, 0.5f };
            var rows = new List<int>();
            foreach (TrackSample s in sections)
            {
                rows.Add(mb.VertexCount);
                Vector3 up = s.Up;
                if (paved)
                {
                    // UVs in metres / 3 so the asphalt grain has a consistent real-world scale on any width.
                    foreach (float f in paveOffsets)
                        mb.AddVertex(s.Position + s.Right * (f * s.Width), up, new Vector2(f * s.Width / 3f, s.Distance / 3f));
                    continue;
                }
                // Per row: 0 left outer, 1 left inner, 2 left skirt bottom, 3 right inner, 4 right outer, 5 right skirt bottom.
                float half = s.Width * 0.5f;
                mb.AddVertex(s.Position - s.Right * (half + s.ShoulderLeft) - up * ShoulderDrop, up, new Vector2(0f, s.Distance / 6f));
                mb.AddVertex(s.Position - s.Right * half - up * 0.005f, up, new Vector2(s.ShoulderLeft / 3f, s.Distance / 6f));
                mb.AddVertex(s.Position - s.Right * (half + s.ShoulderLeft + 0.6f) - up * 1.8f, -s.Right, new Vector2(0f, s.Distance / 6f));
                mb.AddVertex(s.Position + s.Right * half - up * 0.005f, up, new Vector2(0f, s.Distance / 6f));
                mb.AddVertex(s.Position + s.Right * (half + s.ShoulderRight) - up * ShoulderDrop, up, new Vector2(s.ShoulderRight / 3f, s.Distance / 6f));
                mb.AddVertex(s.Position + s.Right * (half + s.ShoulderRight + 0.6f) - up * 1.8f, s.Right, new Vector2(0f, s.Distance / 6f));
            }
            for (int r = 0; r < rows.Count - 1; r++)
            {
                int a = rows[r], b = rows[r + 1];
                if (paved)
                {
                    for (int c = 0; c < paveOffsets.Length - 1; c++)
                        mb.AddQuad(0, a + c, b + c, b + c + 1, a + c + 1);
                }
                else
                {
                    mb.AddQuad(0, a + 0, b + 0, b + 1, a + 1); // left shoulder
                    mb.AddQuad(0, a + 2, b + 2, b + 0, a + 0); // left skirt
                    mb.AddQuad(0, a + 3, b + 3, b + 4, a + 4); // right shoulder
                    mb.AddQuad(0, a + 4, b + 4, b + 5, a + 5); // right skirt
                }
            }
            return mb.Build(name);
        }

        static Mesh BuildMarkings(List<TrackSample> sections, string name)
        {
            var mb = new MeshBuilder();
            const float lineWidth = 0.15f, lift = 0.012f;
            for (int i = 0; i < sections.Count - 1; i++)
            {
                TrackSample s = sections[i], t = sections[i + 1];
                foreach (int side in new[] { -1, 1 })
                    Strip(mb, s, t, side * (s.Width * 0.5f - 0.3f), side * (t.Width * 0.5f - 0.3f), lineWidth, lift);
                if (Mathf.Repeat(s.Distance, 9f) < 3f)
                    Strip(mb, s, t, 0f, 0f, lineWidth, lift); // centre dash: 3 m every 9 m
            }
            return mb.Build(name);
        }

        static void Strip(MeshBuilder mb, TrackSample s, TrackSample t, float xs, float xt, float w, float lift)
        {
            Vector3 us = s.Up * lift, ut = t.Up * lift;
            int a = mb.AddVertex(s.Position + s.Right * (xs - w * 0.5f) + us, s.Up, new Vector2(0, 0));
            int b = mb.AddVertex(t.Position + t.Right * (xt - w * 0.5f) + ut, t.Up, new Vector2(0, 1));
            int c = mb.AddVertex(t.Position + t.Right * (xt + w * 0.5f) + ut, t.Up, new Vector2(1, 1));
            int d = mb.AddVertex(s.Position + s.Right * (xs + w * 0.5f) + us, s.Up, new Vector2(1, 0));
            mb.AddQuad(0, a, b, c, d);
        }

        /// <summary>Closed box strip (inner face, top, outer face, end caps) so queries work from either side.</summary>
        static Mesh BuildBarrierCollider(List<TrackSample> run, int side, string name)
        {
            var mb = new MeshBuilder();
            var rows = new List<int>();
            foreach (TrackSample s in run)
            {
                float lat = BarrierLateral(s, side);
                Vector3 inner = s.Position + s.Right * lat;
                Vector3 outer = s.Position + s.Right * (lat + side * BarrierThickness);
                rows.Add(mb.VertexCount);
                mb.AddVertex(inner + Vector3.down * 0.4f, -s.Right * side, Vector2.zero);
                mb.AddVertex(inner + Vector3.up * BarrierHeight, Vector3.up, Vector2.zero);
                mb.AddVertex(outer + Vector3.up * BarrierHeight, Vector3.up, Vector2.zero);
                mb.AddVertex(outer + Vector3.down * 0.4f, s.Right * side, Vector2.zero);
            }
            for (int r = 0; r < rows.Count - 1; r++)
            {
                int a = rows[r], b = rows[r + 1];
                for (int k = 0; k < 3; k++)
                {
                    if (side > 0) mb.AddQuad(0, a + k, b + k, b + k + 1, a + k + 1);
                    else mb.AddQuad(0, a + k, a + k + 1, b + k + 1, b + k);
                }
            }
            Cap(mb, rows[0], side < 0);
            Cap(mb, rows[rows.Count - 1], side > 0);
            return mb.Build(name);
        }

        static void Cap(MeshBuilder mb, int row, bool flip)
        {
            if (flip) mb.AddQuad(0, row + 3, row + 2, row + 1, row);
            else mb.AddQuad(0, row, row + 1, row + 2, row + 3);
        }

        /// <summary>W-beam rail (submesh 0) at 0.55–0.8 m and posts every 4 m (submesh 1).</summary>
        static Mesh BuildGuardrailVisual(List<TrackSample> run, int side, string name)
        {
            var mb = new MeshBuilder(2);
            Vector2[] profile = { new Vector2(0f, 0.55f), new Vector2(0.06f, 0.6f), new Vector2(0.02f, 0.675f), new Vector2(0.06f, 0.75f), new Vector2(0f, 0.8f) };
            var rows = new List<int>();
            foreach (TrackSample s in run)
            {
                float lat = BarrierLateral(s, side);
                rows.Add(mb.VertexCount);
                foreach (Vector2 p in profile)
                    mb.AddVertex(s.Position + s.Right * (lat - side * p.x) + Vector3.up * p.y, -s.Right * side, new Vector2(s.Distance / 4f, p.y));
            }
            for (int r = 0; r < rows.Count - 1; r++)
            {
                int a = rows[r], b = rows[r + 1];
                for (int k = 0; k < profile.Length - 1; k++)
                {
                    mb.AddQuad(0, a + k, b + k, b + k + 1, a + k + 1);
                    mb.AddQuad(0, a + k + 1, b + k + 1, b + k, a + k);
                }
            }
            float lastPost = -999f;
            foreach (TrackSample s in run)
            {
                if (s.Distance - lastPost < 4f) continue;
                lastPost = s.Distance;
                Vector3 basePos = s.Position + s.Right * (BarrierLateral(s, side) + side * 0.12f) + Vector3.up * 0.35f;
                mb.AddBox(1, basePos, new Vector3(0.06f, 0.55f, 0.06f), Quaternion.LookRotation(s.Tangent, Vector3.up));
            }
            return mb.Build(name);
        }

        public static GameObject Emit(string name, Transform parent, Mesh mesh, int colliderLayer, SurfaceKind surface, params Material[] materials)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            bool visual = materials != null && materials.Length > 0 && materials[0] != null;
            go.layer = colliderLayer >= 0 ? colliderLayer : GameLayers.Scenery;
            if (visual)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = materials;
            }
            if (colliderLayer >= 0)
            {
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.AddComponent<SurfaceTag>().Surface = surface;
            }
            go.isStatic = true;
            return go;
        }
    }
}
