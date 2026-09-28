using System;
using System.Collections.Generic;
using System.Globalization;
using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Shared pieces of the parametric landmark kits (structure, tower, crossing, wall, water, field, rail, sign, gate): the
    /// authored parameters, a site beside the road that keeps a footprint clear of the whole course, a material palette
    /// built into one compact mesh per landmark, and a few solids (frustums, lathes, roofs, lattices, cables).
    /// </summary>
    public static partial class LandmarkKits
    {
        // ------------------------------------------------------------------ parameters

        static float P(RouteLandmarkDef lm, string key, float fallback)
        {
            if (lm.Params == null || !lm.Params.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToSingle(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }

        static string S(RouteLandmarkDef lm, string key, string fallback) =>
            lm.Params != null && lm.Params.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback;

        static bool B(RouteLandmarkDef lm, string key) =>
            lm.Params != null && lm.Params.TryGetValue(key, out object v) && v is bool b && b;

        static string TypeOf(RouteLandmarkDef lm) => S(lm, "type", "");

        /// <summary>Deterministic per-landmark random numbers (the same course always generates the same scenery).</summary>
        static System.Random Rng(TrackData track, RouteLandmarkDef lm) =>
            new System.Random(unchecked((track.CourseId ?? "").GetHashCode() * 31 + (lm.Id ?? "").GetHashCode()));

        // ------------------------------------------------------------------ palette

        /// <summary>Palette slots: one MeshBuilder submesh per course material, compacted when the mesh is built.</summary>
        enum M
        {
            Concrete, Brick, WoodDark, WoodLight, RoofTiles, MetalRoof, SteelGrey, SteelRed, SteelYellow, WindowLit, Stone,
            Graphite, OffWhite, Foliage, Bark, Water, Sea, LanternPaper, Rope, Post, Guardrail, Count,
        }

        static Material Mat(CourseMaterialSet m, M slot)
        {
            switch (slot)
            {
                case M.Concrete: return m.Concrete;
                case M.Brick: return m.Brick;
                case M.WoodDark: return m.WoodDark;
                case M.WoodLight: return m.WoodLight;
                case M.RoofTiles: return m.RoofTiles;
                case M.MetalRoof: return m.MetalRoof;
                case M.SteelGrey: return m.SteelGrey;
                case M.SteelRed: return m.SteelRed;
                case M.SteelYellow: return m.SteelYellow;
                case M.WindowLit: return m.WindowLit;
                case M.Stone: return m.Stone;
                case M.Graphite: return m.Graphite;
                case M.OffWhite: return m.OffWhite;
                case M.Foliage: return m.Foliage;
                case M.Bark: return m.Bark;
                case M.Water: return m.Water;
                case M.Sea: return m.Sea;
                case M.LanternPaper: return m.LanternPaper;
                case M.Rope: return m.Rope;
                case M.Post: return m.Post;
                default: return m.Guardrail;
            }
        }

        static MeshBuilder Kit() => new MeshBuilder((int)M.Count);

        /// <summary>Material slot for an authored material name.</summary>
        static M WallMat(string material)
        {
            switch (material)
            {
                case "brick": return M.Brick;
                case "timber": return M.WoodDark;
                case "steel": return M.SteelGrey;
                case "stone": return M.Stone;
                default: return M.Concrete;
            }
        }

        static M SteelOf(string colour)
        {
            switch (colour)
            {
                case "red":
                case "rust-red": return M.SteelRed;
                case "yellow": return M.SteelYellow;
                default: return M.SteelGrey;
            }
        }

        /// <summary>Builds the kit mesh compactly and places it (visuals for players; a collider only where asked).</summary>
        static GameObject Emit(string name, MeshBuilder mb, TrackData track, RouteLandmarkDef lm, string part, CourseMaterialSet mats, Transform parent,
            Vector3 pos, Quaternion rot, bool collider, int layer, GenerationProfile profile)
        {
            if (profile == GenerationProfile.CollisionOnly && !collider) return null;
            Mesh mesh = mb.BuildCompact($"{track.CourseId}_{lm.Id}{part}", out int[] used);
            var materials = new Material[used.Length];
            for (int i = 0; i < used.Length; i++) materials[i] = Mat(mats, (M)used[i]);
            return Place(name, mesh, materials, parent, pos, rot, collider, layer, profile);
        }

        // ------------------------------------------------------------------ sites

        /// <summary>Where a road-side landmark stands: position on the ground, facing the road (local +z toward it).</summary>
        struct Site
        {
            public Vector3 Pos;
            public Quaternion Rot;
            /// <summary>Horizontal unit vectors: away from the road, and along it.</summary>
            public Vector3 Out, Along;
            public TrackSample S;
            public int Side;
            public float Offset;
        }

        /// <summary>
        /// The site of a landmark beside the road, pushed outward (never inward) until a footprint of the given half extents
        /// (across the road × along it) stays clear of every part of the course — hairpins and loops that pass behind it
        /// included — by the road's half width plus shoulder and a safety margin.
        /// </summary>
        static Site SiteOf(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, float halfAcross, float halfAlong, float margin = 7f)
        {
            TrackSample s = track.SampleAt(lm.AtMetres);
            int side = lm.Side == "left" ? -1 : 1;
            Vector3 outDir = new Vector3(s.Right.x, 0f, s.Right.z).normalized * side;
            Vector3 along = Vector3.Cross(Vector3.up, -outDir); // the site frame's local +x (the mesh faces the road along local +z)
            float offset = Mathf.Max(lm.OffsetMetres, halfAcross + s.Width * 0.5f + margin);
            for (int tries = 0; tries < 80 && !Clear(track, s.Position + outDir * offset, outDir, along, halfAcross, halfAlong, margin); tries++)
                offset += 3f;
            Vector3 pos = s.Position + outDir * offset;
            pos.y = Ground(ground, pos, s.Position.y);
            return new Site { Pos = pos, Rot = Quaternion.LookRotation(-outDir, Vector3.up), Out = outDir, Along = along, S = s, Side = side, Offset = offset };
        }

        /// <summary>Whether a rectangle (centre, axes, half extents) keeps the road plus margin clear along the whole course.</summary>
        static bool Clear(TrackData track, Vector3 centre, Vector3 outDir, Vector3 along, float halfAcross, float halfAlong, float margin, int stride = 4)
        {
            TrackSample[] samples = track.Samples;
            for (int i = 0; i < samples.Length; i += stride)
            {
                TrackSample k = samples[i];
                Vector3 d = k.Position - centre;
                float a = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(d, outDir)) - halfAcross);
                float b = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(d, along)) - halfAlong);
                float need = k.Width * 0.5f + Mathf.Max(k.ShoulderLeft, k.ShoulderRight) + margin;
                if (a * a + b * b < need * need) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether a point lies over (or within <paramref name="margin"/> of) the paved road and its shoulders anywhere on the
        /// course — hairpins and switchbacks included; <paramref name="roadY"/> is that road's height.
        /// </summary>
        static bool OverRoad(TrackData track, Vector3 p, float margin, out float roadY)
        {
            float best = float.MaxValue;
            roadY = p.y;
            bool over = false;
            TrackSample[] samples = track.Samples;
            for (int i = 0; i < samples.Length; i += 2)
            {
                TrackSample k = samples[i];
                float dx = k.Position.x - p.x, dz = k.Position.z - p.z;
                float d = dx * dx + dz * dz;
                float edge = k.Width * 0.5f + Mathf.Max(k.ShoulderLeft, k.ShoulderRight) + margin;
                if (d < edge * edge && d < best)
                {
                    best = d;
                    roadY = k.Position.y;
                    over = true;
                }
            }
            return over;
        }

        /// <summary>Lowest and highest ground under a footprint (to seat a building with a plinth on sloping ground).</summary>
        static void GroundRange(TerrainCollider ground, Site site, float halfAcross, float halfAlong, out float low, out float high)
        {
            low = float.MaxValue;
            high = float.MinValue;
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                Vector3 p = site.Pos + site.Out * (i * halfAcross) + site.Along * (j * halfAlong);
                float y = Ground(ground, p, site.Pos.y);
                low = Mathf.Min(low, y);
                high = Mathf.Max(high, y);
            }
        }

        /// <summary>A point on the ground beside the road at <paramref name="distance"/>, <paramref name="lateral"/> metres outward from the barrier.</summary>
        static Vector3 Beside(TrackData track, TerrainCollider ground, float distance, int side, float lateral)
        {
            TrackSample s = track.SampleAt(distance);
            Vector3 p = s.Position + s.Right * (RoadGeometry.BarrierLateral(s, side) + side * lateral);
            p.y = Ground(ground, p, s.Position.y);
            return p;
        }

        /// <summary>The route section (tunnel, bridge, viaduct) covering <paramref name="metres"/>, if any.</summary>
        static RouteSectionDef SectionAt(RouteDefinition route, float metres)
        {
            if (route?.Sections == null) return null;
            foreach (RouteSectionDef s in route.Sections)
                if (metres >= s.FromMetres - 5f && metres <= s.ToMetres + 5f) return s;
            return null;
        }

        // ------------------------------------------------------------------ solids

        /// <summary>A surface of revolution about local +y from (radius, height) pairs, with optional end caps.</summary>
        static void Lathe(MeshBuilder mb, M sub, Vector3 centre, IList<Vector2> profile, int segments, bool capBottom = false, bool capTop = false)
        {
            int n = profile.Count, start = mb.VertexCount;
            for (int j = 0; j < segments; j++)
            {
                float a = j * Mathf.PI * 2f / segments;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                foreach (Vector2 q in profile) mb.AddVertex(centre + dir * q.x + Vector3.up * q.y, dir, new Vector2(j / (float)segments, q.y * 0.25f));
            }
            for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < segments; j++)
            {
                int a = start + j * n + i, b = start + ((j + 1) % segments) * n + i;
                // Outward for a profile climbing upward (radius as the section's x, height as y).
                Vector2 d = profile[i + 1] - profile[i];
                Vector3 p0 = mb.Position(a), p1 = mb.Position(a + 1), p2 = mb.Position(b + 1);
                float am = (j + 0.5f) * Mathf.PI * 2f / segments;
                Vector3 want = new Vector3(Mathf.Cos(am) * d.y, -d.x, Mathf.Sin(am) * d.y);
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), want) >= 0f) mb.AddQuad((int)sub, a, a + 1, b + 1, b);
                else mb.AddQuad((int)sub, a, b, b + 1, a + 1);
            }
            if (capBottom && profile[0].x > 0f) Cap(mb, sub, centre + Vector3.up * profile[0].y, profile[0].x, segments, Vector3.down);
            if (capTop && profile[n - 1].x > 0f) Cap(mb, sub, centre + Vector3.up * profile[n - 1].y, profile[n - 1].x, segments, Vector3.up);
        }

        static void Cap(MeshBuilder mb, M sub, Vector3 c, float r, int segments, Vector3 facing)
        {
            int centre = mb.AddVertex(c, facing, Vector2.zero), first = mb.VertexCount;
            for (int j = 0; j < segments; j++)
            {
                float a = j * Mathf.PI * 2f / segments;
                mb.AddVertex(c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r, facing, Vector2.zero);
            }
            for (int j = 0; j < segments; j++)
            {
                int a = first + j, b = first + (j + 1) % segments;
                Vector3 pa = mb.Position(centre), pb = mb.Position(a), pc = mb.Position(b);
                if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), facing) >= 0f) mb.AddTriangle((int)sub, centre, a, b);
                else mb.AddTriangle((int)sub, centre, b, a);
            }
        }

        /// <summary>A tapered cylinder (frustum) with caps.</summary>
        static void Frustum(MeshBuilder mb, M sub, Vector3 bottom, float r0, float r1, float height, int segments = 16) =>
            Lathe(mb, sub, bottom, new[] { new Vector2(r0, 0f), new Vector2(r1, height) }, segments, true, true);

        /// <summary>A dome: a hemisphere of radius r standing on <paramref name="baseCentre"/>.</summary>
        static void Dome(MeshBuilder mb, M sub, Vector3 baseCentre, float r, int segments = 20)
        {
            var prof = new List<Vector2>();
            for (int i = 0; i <= 8; i++)
            {
                float a = i / 8f * Mathf.PI * 0.5f;
                prof.Add(new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r));
            }
            prof[8] = new Vector2(0.001f, r);
            Lathe(mb, sub, baseCentre, prof, segments);
        }

        /// <summary>A beam between two points (box of the given half thickness).</summary>
        static void Beam(MeshBuilder mb, M sub, Vector3 a, Vector3 b, float halfW, float halfH = -1f)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) return;
            Vector3 up = Mathf.Abs(Vector3.Dot(d / len, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            mb.AddBox((int)sub, (a + b) * 0.5f, new Vector3(halfW, halfH < 0f ? halfW : halfH, len * 0.5f), Quaternion.LookRotation(d, up), 0.5f);
        }

        /// <summary>A four-legged lattice tower from a square base (half width w0) tapering to w1 at height h, braced in panels.</summary>
        static void Lattice(MeshBuilder mb, M sub, Vector3 baseCentre, Quaternion rot, float w0, float w1, float h, float member = 0.08f, int panels = -1)
        {
            if (panels < 0) panels = Mathf.Max(2, Mathf.RoundToInt(h / Mathf.Max(1.5f, (w0 + w1))));
            Vector3 Corner(int k, float t)
            {
                float w = Mathf.Lerp(w0, w1, t);
                float x = (k == 0 || k == 3) ? -w : w, z = k < 2 ? -w : w;
                return baseCentre + rot * new Vector3(x, t * h, z);
            }
            for (int k = 0; k < 4; k++) Beam(mb, sub, Corner(k, 0f), Corner(k, 1f), member);
            for (int p = 0; p < panels; p++)
            {
                float t0 = p / (float)panels, t1 = (p + 1) / (float)panels;
                for (int k = 0; k < 4; k++)
                {
                    int k1 = (k + 1) % 4;
                    Beam(mb, sub, Corner(k, t1), Corner(k1, t1), member * 0.7f);
                    Beam(mb, sub, Corner(k, t0), Corner(k1, t1), member * 0.5f);
                }
            }
        }

        /// <summary>A hanging cable between two points (sag at mid-span).</summary>
        static void Cable(MeshBuilder mb, M sub, Vector3 a, Vector3 b, float sag, float thickness = 0.025f, int segments = 8)
        {
            for (int i = 0; i < segments; i++)
            {
                float t0 = i / (float)segments, t1 = (i + 1) / (float)segments;
                Vector3 p0 = Vector3.Lerp(a, b, t0) + Vector3.down * (Mathf.Sin(t0 * Mathf.PI) * sag);
                Vector3 p1 = Vector3.Lerp(a, b, t1) + Vector3.down * (Mathf.Sin(t1 * Mathf.PI) * sag);
                Beam(mb, sub, p0, p1, thickness);
            }
        }

        /// <summary>
        /// A tree: trunk and a stack of foliage cones (conifer) or a rounded crown (broadleaf / fruit). <paramref name="simple"/>
        /// (back rows) is one cone on a thin trunk.
        /// </summary>
        static void Tree(MeshBuilder mb, Vector3 foot, float height, bool conifer, float crownScale = 1f, bool simple = false)
        {
            float trunk = height * (conifer ? 0.35f : 0.45f);
            if (simple && conifer)
            {
                Lathe(mb, M.Bark, foot, new[] { new Vector2(height * 0.02f + 0.06f, 0f), new Vector2(0.05f, trunk) }, 5);
                Lathe(mb, M.Foliage, foot + Vector3.up * trunk * 0.6f, new[] { new Vector2(height * 0.2f * crownScale, 0f), new Vector2(0.05f, height * 0.8f) }, 6, true);
                return;
            }
            Frustum(mb, M.Bark, foot, height * 0.022f + 0.08f, height * 0.012f + 0.04f, trunk + height * 0.1f, 7);
            if (conifer)
            {
                for (int i = 0; i < 3; i++)
                {
                    float y0 = trunk * 0.7f + i * height * 0.2f, r = height * (0.24f - i * 0.055f) * crownScale;
                    Lathe(mb, M.Foliage, foot + Vector3.up * y0, new[] { new Vector2(r, 0f), new Vector2(0.05f, height * 0.36f) }, 7, true);
                }
            }
            else
            {
                float r = height * 0.32f * crownScale;
                var prof = new List<Vector2>();
                for (int i = 0; i <= 6; i++)
                {
                    float a = -Mathf.PI * 0.5f + i / 6f * Mathf.PI;
                    prof.Add(new Vector2(Mathf.Max(0.02f, Mathf.Cos(a) * r), trunk + r + Mathf.Sin(a) * r * 0.85f));
                }
                Lathe(mb, M.Foliage, foot, prof, 9);
            }
        }

        // ------------------------------------------------------------------ roofs and windows (local building frame)

        /// <summary>
        /// Roof over a w × d rectangle (local x × z, centred) whose walls stop at <paramref name="eave"/>: the roof planes pass
        /// through the wall tops and run on past them by the overhang, so no gap opens between wall and roof. Gable and hip
        /// ridges run along the longer side.
        /// </summary>
        static void Roof(MeshBuilder mb, M sub, Vector3 c, Quaternion rot, float w, float d, float eave, string kind, float overhang = 0.6f)
        {
            if ((kind == "gable" || kind == "hip" || kind == null || kind == "") && d > w * 1.05f)
            {
                rot = rot * Quaternion.Euler(0f, 90f, 0f);
                float t = w; w = d; d = t;
            }
            float hw = w * 0.5f + overhang, hd = d * 0.5f + overhang;
            Vector3 L(float x, float y, float z) => c + rot * new Vector3(x, y, z);
            switch (kind)
            {
                case "flat":
                    mb.AddBox((int)sub, L(0f, eave + 0.25f, 0f), new Vector3(w * 0.5f + 0.15f, 0.25f, d * 0.5f + 0.15f), rot, 0.3f);
                    mb.AddBox((int)M.Graphite, L(0f, eave + 0.6f, 0f), new Vector3(w * 0.5f + 0.2f, 0.12f, 0.2f), rot, 0.3f);
                    break;
                case "sawtooth":
                {
                    int teeth = Mathf.Max(2, Mathf.RoundToInt(d / 6f));
                    float step = d / teeth, rise = Mathf.Min(3f, step * 0.5f), sw = w * 0.5f;
                    for (int t = 0; t < teeth; t++)
                    {
                        float z0 = -d * 0.5f + t * step, z1 = z0 + step;
                        mb.AddFlatQuad((int)sub, L(-sw, eave, z0), L(-sw, eave + rise, z1), L(sw, eave + rise, z1), L(sw, eave, z0), new Vector2(0.3f, 0.3f));
                        mb.AddFlatQuad((int)M.Graphite, L(sw, eave, z1), L(sw, eave + rise, z1), L(-sw, eave + rise, z1), L(-sw, eave, z1), new Vector2(0.3f, 0.3f));
                        TriFlat(mb, sub, L(-sw, eave, z0), L(-sw, eave, z1), L(-sw, eave + rise, z1));
                        TriFlat(mb, sub, L(sw, eave, z1), L(sw, eave, z0), L(sw, eave + rise, z1));
                    }
                    break;
                }
                case "curved":
                {
                    // A barrel vault along x, springing from the eaves on the long sides.
                    const int segs = 10;
                    float rise = Mathf.Min(d * 0.28f, 6f);
                    for (int i = 0; i < segs; i++)
                    {
                        float a0 = i / (float)segs, a1 = (i + 1) / (float)segs;
                        float z0 = Mathf.Lerp(-hd, hd, a0), z1 = Mathf.Lerp(-hd, hd, a1);
                        float y0 = eave + rise * Mathf.Sin(a0 * Mathf.PI), y1 = eave + rise * Mathf.Sin(a1 * Mathf.PI);
                        mb.AddFlatQuad((int)sub, L(-hw, y0, z0), L(-hw, y1, z1), L(hw, y1, z1), L(hw, y0, z0), new Vector2(0.3f, 0.3f));
                        mb.AddFlatQuad((int)sub, L(hw, y0, z0), L(hw, y1, z1), L(-hw, y1, z1), L(-hw, y0, z0), new Vector2(0.3f, 0.3f));
                        // End walls under the vault.
                        TriFlat(mb, M.Graphite, L(-w * 0.5f, eave, z0), L(-w * 0.5f, y1, z1), L(-w * 0.5f, y0, z0));
                        TriFlat(mb, M.Graphite, L(-w * 0.5f, eave, z0), L(-w * 0.5f, eave, z1), L(-w * 0.5f, y1, z1));
                        TriFlat(mb, M.Graphite, L(w * 0.5f, eave, z0), L(w * 0.5f, y0, z0), L(w * 0.5f, y1, z1));
                        TriFlat(mb, M.Graphite, L(w * 0.5f, eave, z0), L(w * 0.5f, y1, z1), L(w * 0.5f, eave, z1));
                    }
                    break;
                }
                case "hip":
                {
                    float rise = d * 0.32f, k = rise / (d * 0.5f), low = eave - k * overhang;
                    float ridge = Mathf.Max(0f, w * 0.5f - d * 0.5f);
                    Vector3 r0 = L(-ridge, eave + rise, 0f), r1 = L(ridge, eave + rise, 0f);
                    Vector3 a = L(-hw, low, -hd), b = L(hw, low, -hd), e = L(hw, low, hd), f = L(-hw, low, hd);
                    mb.AddFlatQuad((int)sub, a, r0, r1, b, new Vector2(0.3f, 0.3f));
                    mb.AddFlatQuad((int)sub, e, r1, r0, f, new Vector2(0.3f, 0.3f));
                    TriFlat(mb, sub, b, r1, e);
                    TriFlat(mb, sub, f, r0, a);
                    break;
                }
                default: // gable, ridge along x
                {
                    float rise = d * 0.3f, k = rise / (d * 0.5f), low = eave - k * overhang;
                    Vector3 r0 = L(-hw, eave + rise, 0f), r1 = L(hw, eave + rise, 0f);
                    mb.AddFlatQuad((int)sub, L(-hw, low, -hd), r0, r1, L(hw, low, -hd), new Vector2(0.3f, 0.3f));
                    mb.AddFlatQuad((int)sub, L(hw, low, hd), r1, r0, L(-hw, low, hd), new Vector2(0.3f, 0.3f));
                    // Gable ends, from the wall tops up to the ridge.
                    TriFlat(mb, M.WoodDark, L(-w * 0.5f, eave, -d * 0.5f), L(-w * 0.5f, eave, d * 0.5f), L(-w * 0.5f, eave + rise, 0f));
                    TriFlat(mb, M.WoodDark, L(w * 0.5f, eave, d * 0.5f), L(w * 0.5f, eave, -d * 0.5f), L(w * 0.5f, eave + rise, 0f));
                    break;
                }
            }
        }

        /// <summary>A flat triangle, drawn from both sides (thin roof ends and gables are seen from either side).</summary>
        static void TriFlat(MeshBuilder mb, M sub, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            int i0 = mb.AddVertex(a, n, Vector2.zero), i1 = mb.AddVertex(b, n, Vector2.zero), i2 = mb.AddVertex(c, n, Vector2.zero);
            mb.AddTriangle((int)sub, i0, i1, i2);
            // The back face as well (thin roof ends are seen from both sides).
            int j0 = mb.AddVertex(a, -n, Vector2.zero), j1 = mb.AddVertex(b, -n, Vector2.zero), j2 = mb.AddVertex(c, -n, Vector2.zero);
            mb.AddTriangle((int)sub, j0, j2, j1);
        }

        /// <summary>Rows of window panes on the four walls of a w × d block from <paramref name="y0"/> to <paramref name="y1"/>.</summary>
        static void Windows(MeshBuilder mb, Vector3 c, Quaternion rot, float w, float d, float y0, float y1, float pitch, bool lit, float paneW = 1.3f, float paneH = 1.4f)
        {
            M sub = lit ? M.WindowLit : M.Graphite;
            int floors = Mathf.Max(1, Mathf.FloorToInt((y1 - y0) / 3.4f));
            for (int f = 0; f < floors; f++)
            {
                float y = y0 + 1.6f + f * 3.4f;
                if (y + paneH * 0.5f > y1) break;
                foreach (int face in new[] { 0, 1, 2, 3 })
                {
                    float len = face < 2 ? w : d;
                    int count = Mathf.Max(1, Mathf.FloorToInt((len - 2f) / pitch));
                    for (int i = 0; i < count; i++)
                    {
                        float t = -len * 0.5f + 1f + (i + 0.5f) * (len - 2f) / count;
                        Vector3 local, half;
                        switch (face)
                        {
                            case 0: local = new Vector3(t, y, d * 0.5f + 0.03f); half = new Vector3(paneW * 0.5f, paneH * 0.5f, 0.04f); break;
                            case 1: local = new Vector3(t, y, -d * 0.5f - 0.03f); half = new Vector3(paneW * 0.5f, paneH * 0.5f, 0.04f); break;
                            case 2: local = new Vector3(w * 0.5f + 0.03f, y, t); half = new Vector3(0.04f, paneH * 0.5f, paneW * 0.5f); break;
                            default: local = new Vector3(-w * 0.5f - 0.03f, y, t); half = new Vector3(0.04f, paneH * 0.5f, paneW * 0.5f); break;
                        }
                        mb.AddBox((int)sub, c + rot * local, half, rot, 0.5f);
                    }
                }
            }
        }
    }
}
