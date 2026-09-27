using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>A flat paved area the terrain must meet (training pads, Test Yard facilities).</summary>
    public struct TerrainPad
    {
        public Vector3 Centre;
        public Vector2 HalfSize;
        public float HeadingDeg;

        /// <summary>Distance outside the rectangle on the ground plane (0 inside).</summary>
        public float OutsideDistance(float wx, float wz)
        {
            float rad = -HeadingDeg * Mathf.Deg2Rad;
            float dx = wx - Centre.x, dz = wz - Centre.z;
            float lx = dx * Mathf.Cos(rad) - dz * Mathf.Sin(rad);
            float lz = dx * Mathf.Sin(rad) + dz * Mathf.Cos(rad);
            float ox = Mathf.Max(0f, Mathf.Abs(lx) - HalfSize.x), oz = Mathf.Max(0f, Mathf.Abs(lz) - HalfSize.y);
            return Mathf.Sqrt(ox * ox + oz * oz);
        }
    }

    /// <summary>
    /// Off-route areas from <c>route.areas</c> (docs/COURSES.md): paved pads with drivable colliders and kind-specific
    /// markings — skid-pad ring, braking-lane edges and stop boxes from its gates, bay outlines, slalom cones. Pads are
    /// flattened into the terrain with a blended margin. Deterministic from the route document.
    /// </summary>
    public static class AreaGeometry
    {
        public const float PadBlendMetres = 24f;

        public static TerrainPad PadOf(RouteAreaDef a) => new TerrainPad
        {
            Centre = new Vector3(a.Centre[0], a.Centre[1], a.Centre[2]),
            HalfSize = new Vector2(a.Size[0] * 0.5f, a.Size[1] * 0.5f),
            HeadingDeg = a.HeadingDeg,
        };

        /// <summary>Blends a terrain height toward the pads (pads sit 2 cm above the blended ground).</summary>
        public static float ApplyPads(float wx, float wz, float h, List<TerrainPad> pads)
        {
            if (pads == null) return h;
            foreach (TerrainPad p in pads)
            {
                float d = p.OutsideDistance(wx, wz);
                if (d >= PadBlendMetres) continue;
                float t = 1f - d / PadBlendMetres;
                t = t * t * (3f - 2f * t);
                h = Mathf.Lerp(h, p.Centre.y - 0.05f, t);
            }
            return h;
        }

        public static void Build(RouteDefinition route, Transform root, CourseMaterialSet mats, GenerationProfile profile)
        {
            if (route.Areas == null || route.Areas.Count == 0) return;
            var parent = new GameObject("Areas").transform;
            parent.SetParent(root, false);
            bool visuals = profile == GenerationProfile.Full;
            foreach (RouteAreaDef a in route.Areas)
            {
                if (a.Size == null || a.Size.Length < 2 || a.Centre == null || a.Centre.Length < 3) continue;
                Vector3 c = new Vector3(a.Centre[0], a.Centre[1], a.Centre[2]);
                Quaternion rot = Quaternion.Euler(0f, a.HeadingDeg, 0f);
                float hw = a.Size[0] * 0.5f, hl = a.Size[1] * 0.5f;
                // Paved surface (drivable collider) — a thin slab so wheel rays hit it from above.
                var pad = new MeshBuilder();
                pad.AddFlatQuad(0, c + rot * new Vector3(-hw, 0, -hl), c + rot * new Vector3(-hw, 0, hl), c + rot * new Vector3(hw, 0, hl),
                    c + rot * new Vector3(hw, 0, -hl), new Vector2(a.Size[0] / 4f, a.Size[1] / 4f));
                RoadGeometry.Emit($"Area_{a.Id}", parent, pad.Build(a.Id + "_pad"), GameLayers.Drivable, SurfaceKind.Asphalt, visuals ? mats.Asphalt : null);
                if (!visuals) continue;
                var marks = new MeshBuilder();
                switch (a.Kind)
                {
                    case "skid-pad":
                        Ring(marks, c, Mathf.Min(hw, hl) - 6f, 0.35f);
                        Ring(marks, c, Mathf.Min(hw, hl) - 16f, 0.2f);
                        break;
                    case "braking-lane":
                        EdgeLines(marks, c, rot, hw - 1.2f, hl);
                        foreach (RouteGateDef g in a.Gates ?? new List<RouteGateDef>())
                            if (g.Kind == "brake-zone" && g.EndMetres - g.StartMetres <= 20f)
                                Box(marks, c, rot, -hl + g.StartMetres, -hl + g.EndMetres, g.LineTolerance > 0 ? g.LineTolerance : 3f);
                        break;
                    case "slalom":
                        EdgeLines(marks, c, rot, hw - 1f, hl);
                        break;
                    default:
                        Outline(marks, c, rot, hw - 0.6f, hl - 0.6f);
                        break;
                }
                RoadGeometry.Emit($"AreaMarks_{a.Id}", parent, marks.Build(a.Id + "_marks"), -1, SurfaceKind.Asphalt, mats.LinePaint);
                if (a.Kind == "slalom") Cones(parent, c, rot, hl, a.ConeCount > 0 ? a.ConeCount : 7, mats);
            }
        }

        static void Ring(MeshBuilder mb, Vector3 c, float radius, float width)
        {
            const int segments = 96;
            Vector3 up = Vector3.up * 0.02f;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments, a1 = (i + 1) * Mathf.PI * 2f / segments;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                mb.AddFlatQuad(0, c + d0 * (radius - width * 0.5f) + up, c + d0 * (radius + width * 0.5f) + up,
                    c + d1 * (radius + width * 0.5f) + up, c + d1 * (radius - width * 0.5f) + up, Vector2.one);
            }
        }

        static void Strip(MeshBuilder mb, Vector3 c, Quaternion rot, float x0, float x1, float z0, float z1)
        {
            Vector3 up = Vector3.up * 0.02f;
            mb.AddFlatQuad(0, c + rot * new Vector3(x0, 0, z0) + up, c + rot * new Vector3(x0, 0, z1) + up,
                c + rot * new Vector3(x1, 0, z1) + up, c + rot * new Vector3(x1, 0, z0) + up, Vector2.one);
        }

        static void EdgeLines(MeshBuilder mb, Vector3 c, Quaternion rot, float halfWidth, float halfLength)
        {
            Strip(mb, c, rot, -halfWidth - 0.15f, -halfWidth + 0.15f, -halfLength, halfLength);
            Strip(mb, c, rot, halfWidth - 0.15f, halfWidth + 0.15f, -halfLength, halfLength);
        }

        static void Outline(MeshBuilder mb, Vector3 c, Quaternion rot, float hw, float hl)
        {
            EdgeLines(mb, c, rot, hw, hl);
            Strip(mb, c, rot, -hw, hw, -hl - 0.15f, -hl + 0.15f);
            Strip(mb, c, rot, -hw, hw, hl - 0.15f, hl + 0.15f);
        }

        /// <summary>A painted precision-stop box across the lane, <paramref name="z0"/>..<paramref name="z1"/> from its centre.</summary>
        static void Box(MeshBuilder mb, Vector3 c, Quaternion rot, float z0, float z1, float halfWidth)
        {
            Strip(mb, c, rot, -halfWidth, halfWidth, z0 - 0.15f, z0 + 0.15f);
            Strip(mb, c, rot, -halfWidth, halfWidth, z1 - 0.15f, z1 + 0.15f);
            Strip(mb, c, rot, -halfWidth - 0.15f, -halfWidth + 0.15f, z0, z1);
            Strip(mb, c, rot, halfWidth - 0.15f, halfWidth + 0.15f, z0, z1);
        }

        /// <summary>Slalom cones on the lane centre (visual markers; passing is judged by position, not by collision).</summary>
        static void Cones(Transform parent, Vector3 c, Quaternion rot, float halfLength, int count, CourseMaterialSet mats)
        {
            var mb = new MeshBuilder();
            float spacing = (halfLength * 2f - 40f) / Mathf.Max(1, count - 1);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = c + rot * new Vector3(0f, 0f, -halfLength + 20f + i * spacing);
                const int sides = 10;
                for (int s = 0; s < sides; s++)
                {
                    float a0 = s * Mathf.PI * 2f / sides, a1 = (s + 1) * Mathf.PI * 2f / sides;
                    Vector3 b0 = p + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * 0.28f;
                    Vector3 b1 = p + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * 0.28f;
                    Vector3 top = p + Vector3.up * 0.72f;
                    Vector3 n = ((b0 + b1) * 0.5f - p).normalized + Vector3.up * 0.4f;
                    int i0 = mb.AddVertex(b0, n, Vector2.zero), i1 = mb.AddVertex(top, n, Vector2.up), i2 = mb.AddVertex(b1, n, Vector2.right);
                    mb.AddTriangle(0, i0, i1, i2);
                }
            }
            RoadGeometry.Emit("SlalomCones", parent, mb.Build("slalom_cones"), -1, SurfaceKind.Asphalt, mats.SteelYellow);
        }
    }
}
