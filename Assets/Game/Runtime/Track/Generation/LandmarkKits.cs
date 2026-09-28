using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>Planning output of landmarks before geometry exists (terrain carves, guardrail overrides).</summary>
    public sealed class LandmarkPlan
    {
        public readonly List<TerrainCarve> Carves = new List<TerrainCarve>();
        public readonly List<RoadGeometry.Range> NoBarrier = new List<RoadGeometry.Range>();
    }

    /// <summary>
    /// Reusable procedural landmark kits (original designs, deterministic). Each kit first plans its terrain/road
    /// needs, then builds geometry against the generated terrain. The bespoke C01 kits are here; the parametric kits
    /// (structure, tower, crossing, wall, water, field, rail, sign, gate) are in the other <c>LandmarkKits.*.cs</c> files.
    /// </summary>
    public static partial class LandmarkKits
    {
        public static void Plan(TrackData track, RouteLandmarkDef lm, LandmarkPlan plan)
        {
            if (lm.Kit == "water") { PlanWater(track, lm, plan); return; }
            if (lm.Kit != "stone-bridge") return;
            TrackSample s = track.SampleAt(lm.AtMetres);
            Vector3 across = new Vector3(s.Right.x, 0f, s.Right.z).normalized;
            plan.Carves.Add(new TerrainCarve { A = s.Position - across * 140f, B = s.Position + across * 140f, HalfWidth = 5f, Depth = 4.2f });
            plan.NoBarrier.Add(new RoadGeometry.Range { From = lm.AtMetres - 15f, To = lm.AtMetres + 15f });
        }

        public static GameObject Build(TrackData track, RouteDefinition route, RouteLandmarkDef lm, TerrainCollider ground, Transform parent,
            CourseMaterialSet mats, GenerationProfile profile)
        {
            switch (lm.Kit)
            {
                case "tea-shed": return TeaShed(track, lm, ground, parent, mats, profile);
                case "stone-bridge": return StoneBridge(track, lm, parent, mats, profile);
                case "lantern-row": return profile == GenerationProfile.Full ? LanternRow(track, lm, ground, parent, mats) : null;
                case "structure": return Structure(track, lm, ground, parent, mats, profile);
                case "tower": return Tower(track, lm, ground, parent, mats, profile);
                case "crossing": return Crossing(track, route, lm, ground, parent, mats, profile);
                case "wall": return Wall(track, route, lm, ground, parent, mats, profile);
                case "water": return Water(track, lm, ground, parent, mats, profile);
                case "field": return Field(track, lm, ground, parent, mats, profile);
                case "rail": return Rail(track, lm, ground, parent, mats, profile);
                case "sign": return Sign(track, lm, ground, parent, mats, profile);
                case "gate": return Gate(track, lm, ground, parent, mats, profile);
                default:
                    Debug.LogWarning($"[NightSignal.Course] No kit '{lm.Kit}' for {lm.Id}");
                    return null;
            }
        }

        static float Ground(TerrainCollider ground, Vector3 p, float fallback)
        {
            var ray = new Ray(new Vector3(p.x, 2000f, p.z), Vector3.down);
            return ground != null && ground.Raycast(ray, out RaycastHit hit, 4000f) ? hit.point.y : fallback;
        }

        static GameObject Place(string name, Mesh mesh, Material[] mats, Transform parent, Vector3 pos, Quaternion rot,
            bool collider, int layer, GenerationProfile profile)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.layer = layer;
            if (profile == GenerationProfile.Full)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            }
            if (collider)
            {
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                if (layer == GameLayers.Barrier) go.AddComponent<SurfaceTag>().Surface = SurfaceKind.Concrete;
            }
            go.isStatic = true;
            return go;
        }

        // ------------------------------------------------------------------ tea-drying shed

        static GameObject TeaShed(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, Transform parent,
            CourseMaterialSet mats, GenerationProfile profile)
        {
            TrackSample s = track.SampleAt(lm.AtMetres);
            int side = lm.Side == "left" ? -1 : 1;
            Vector3 flatRight = new Vector3(s.Right.x, 0f, s.Right.z).normalized;
            Vector3 pos = s.Position + flatRight * side * lm.OffsetMetres;
            pos.y = Ground(ground, pos, s.Position.y);
            Quaternion rot = Quaternion.LookRotation(-flatRight * side, Vector3.up); // open front faces the road

            // Submeshes: 0 dark timber frame, 1 light slatted walls/trays, 2 roof tiles, 3 stone plinth.
            var mb = new MeshBuilder(4);
            const float w = 9f, d = 5.5f, eave = 3.2f, ridge = 5.4f;
            mb.AddBox(3, new Vector3(0f, 0.25f, 0f), new Vector3(w / 2 + 0.3f, 0.35f, d / 2 + 0.3f), Quaternion.identity, 0.5f);
            mb.AddBox(1, new Vector3(0f, 0.6f + eave / 2, -d / 2), new Vector3(w / 2, eave / 2, 0.06f), Quaternion.identity, 0.35f);
            mb.AddBox(1, new Vector3(-w / 2, 0.6f + eave / 2, 0f), new Vector3(0.06f, eave / 2, d / 2), Quaternion.identity, 0.35f);
            mb.AddBox(1, new Vector3(w / 2, 0.6f + eave / 2, 0f), new Vector3(0.06f, eave / 2, d / 2), Quaternion.identity, 0.35f);
            for (int i = 0; i <= 4; i++)
            {
                float x = -w / 2 + i * w / 4;
                mb.AddBox(0, new Vector3(x, 0.6f + eave / 2, d / 2), new Vector3(0.11f, eave / 2, 0.11f), Quaternion.identity, 0.5f);
                mb.AddBox(0, new Vector3(x, 0.6f + eave / 2, -d / 2 + 0.1f), new Vector3(0.11f, eave / 2, 0.11f), Quaternion.identity, 0.5f);
            }
            mb.AddBox(0, new Vector3(0f, 0.6f + eave, d / 2), new Vector3(w / 2 + 0.2f, 0.12f, 0.12f), Quaternion.identity, 0.5f);
            mb.AddBox(0, new Vector3(0f, 0.6f + eave, -d / 2), new Vector3(w / 2 + 0.2f, 0.12f, 0.12f), Quaternion.identity, 0.5f);
            float rise = ridge - eave, halfSpan = d / 2 + 0.8f;
            float slope = Mathf.Atan2(rise, halfSpan) * Mathf.Rad2Deg;
            float slab = Mathf.Sqrt(rise * rise + halfSpan * halfSpan) / 2f;
            mb.AddBox(2, new Vector3(0f, 0.6f + eave + rise / 2, halfSpan / 2), new Vector3(w / 2 + 0.9f, 0.09f, slab), Quaternion.Euler(slope, 0f, 0f), 0.4f);
            mb.AddBox(2, new Vector3(0f, 0.6f + eave + rise / 2, -halfSpan / 2), new Vector3(w / 2 + 0.9f, 0.09f, slab), Quaternion.Euler(-slope, 0f, 0f), 0.4f);
            mb.AddBox(0, new Vector3(0f, 0.6f + ridge + 0.05f, 0f), new Vector3(w / 2 + 0.9f, 0.14f, 0.18f), Quaternion.identity, 0.5f);
            for (int r = 0; r < 3; r++)
            {
                float rx = -w / 2 + 1.5f + r * 3f;
                for (int level = 0; level < 3; level++)
                    mb.AddBox(1, new Vector3(rx, 0.7f + level * 0.55f, d / 2 + 2.2f), new Vector3(1.1f, 0.03f, 0.7f), Quaternion.identity, 0.6f);
                foreach (float cx in new[] { -1.1f, 1.1f })
                foreach (float cz in new[] { -0.7f, 0.7f })
                    mb.AddBox(0, new Vector3(rx + cx, 0.95f, d / 2 + 2.2f + cz), new Vector3(0.05f, 0.95f, 0.05f), Quaternion.identity, 0.5f);
            }
            return Place(lm.Name, mb.Build($"{track.CourseId}_{lm.Id}"), new[] { mats.WoodDark, mats.WoodLight, mats.RoofTiles, mats.Stone },
                parent, pos, rot, true, GameLayers.Scenery, profile);
        }

        // ------------------------------------------------------------------ low stone bridge

        static GameObject StoneBridge(TrackData track, RouteLandmarkDef lm, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            var root = new GameObject(lm.Name);
            root.transform.SetParent(parent, false);
            var parapets = new MeshBuilder();
            var faces = new MeshBuilder();
            const float half = 15f;
            for (float d = lm.AtMetres - half; d < lm.AtMetres + half; d += 1f)
            {
                TrackSample a = track.SampleAt(d), b = track.SampleAt(d + 1f);
                foreach (int side in new[] { -1, 1 })
                {
                    Vector3 pa = a.Position + a.Right * RoadGeometry.BarrierLateral(a, side);
                    Vector3 pb = b.Position + b.Right * RoadGeometry.BarrierLateral(b, side);
                    Vector3 mid = (pa + pb) * 0.5f + Vector3.up * 0.25f;
                    Quaternion r = Quaternion.LookRotation(pb - pa, Vector3.up);
                    float halfLen = (pb - pa).magnitude * 0.5f + 0.02f;
                    parapets.AddBox(0, mid, new Vector3(0.28f, 0.72f, halfLen), r, 0.5f);
                    faces.AddBox(0, mid + Vector3.down * 2.9f + a.Right * side * 0.1f, new Vector3(0.35f, 2.3f, halfLen), r, 0.4f);
                }
            }
            Place("Parapets", parapets.Build($"{track.CourseId}_{lm.Id}_parapets"), new[] { mats.Stone }, root.transform,
                Vector3.zero, Quaternion.identity, true, GameLayers.Barrier, profile);
            if (profile == GenerationProfile.Full)
            {
                Place("ArchFaces", faces.Build($"{track.CourseId}_{lm.Id}_faces"), new[] { mats.Stone }, root.transform,
                    Vector3.zero, Quaternion.identity, false, GameLayers.Scenery, profile);
                TrackSample c = track.SampleAt(lm.AtMetres);
                Vector3 across = new Vector3(c.Right.x, 0f, c.Right.z).normalized;
                Vector3 along = Vector3.Cross(Vector3.up, across);
                Vector3 water = c.Position + Vector3.down * 3.6f;
                var wm = new MeshBuilder();
                wm.AddFlatQuad(0, water - across * 140f - along * 3.2f, water - across * 140f + along * 3.2f,
                    water + across * 140f + along * 3.2f, water + across * 140f - along * 3.2f, new Vector2(0.1f, 0.1f));
                Place("Creek", wm.Build($"{track.CourseId}_{lm.Id}_water"), new[] { mats.Water }, root.transform,
                    Vector3.zero, Quaternion.identity, false, GameLayers.Scenery, profile);
            }
            return root;
        }

        // ------------------------------------------------------------------ hanging lantern row

        static GameObject LanternRow(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats)
        {
            var mb = new MeshBuilder(3); // 0 poles, 1 rope, 2 paper lanterns
            const float spacing = 12f, length = 300f;
            var tops = new[] { new List<Vector3>(), new List<Vector3>() };
            for (float d = lm.AtMetres; d <= lm.AtMetres + length; d += spacing)
            {
                TrackSample s = track.SampleAt(d);
                for (int k = 0; k < 2; k++)
                {
                    int side = k == 0 ? -1 : 1;
                    Vector3 p = s.Position + s.Right * (RoadGeometry.BarrierLateral(s, side) + side * 1.4f);
                    p.y = Mathf.Max(Ground(ground, p, s.Position.y), s.Position.y - 1.5f);
                    mb.AddCylinder(0, p, 0.09f, 4.6f, 8, Quaternion.identity);
                    tops[k].Add(p + Vector3.up * 4.4f);
                }
            }
            foreach (List<Vector3> line in tops)
            {
                for (int i = 0; i < line.Count - 1; i++)
                {
                    Vector3 a = line[i], b = line[i + 1];
                    const int segs = 6;
                    for (int j = 0; j < segs; j++)
                    {
                        Vector3 p0 = Vector3.Lerp(a, b, j / (float)segs) + Vector3.down * Sag(j / (float)segs);
                        Vector3 p1 = Vector3.Lerp(a, b, (j + 1) / (float)segs) + Vector3.down * Sag((j + 1) / (float)segs);
                        mb.AddBox(1, (p0 + p1) * 0.5f, new Vector3(0.012f, 0.012f, (p1 - p0).magnitude * 0.5f), Quaternion.LookRotation(p1 - p0), 1f);
                    }
                    for (int j = 1; j <= 5; j++)
                    {
                        float t = j / 6f;
                        Vector3 hook = Vector3.Lerp(a, b, t) + Vector3.down * Sag(t);
                        mb.AddCylinder(2, hook + Vector3.down * 0.55f, 0.17f, 0.42f, 10, Quaternion.identity);
                    }
                }
            }
            return Place(lm.Name, mb.Build($"{track.CourseId}_{lm.Id}"), new[] { mats.WoodDark, mats.Rope, mats.LanternPaper },
                parent, Vector3.zero, Quaternion.identity, false, GameLayers.Scenery, GenerationProfile.Full);
        }

        static float Sag(float t) => Mathf.Sin(t * Mathf.PI) * 0.45f;
    }
}
