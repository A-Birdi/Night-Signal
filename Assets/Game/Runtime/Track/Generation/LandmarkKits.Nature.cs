using System.Collections.Generic;
using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Water (kit <c>water</c>) and planted ground (kit <c>field</c>). Open water — sea, reservoir — is a plane at a level
    /// kept below the lowest road it covers, so it fills the low ground as shorelines; channels beside the road (canal,
    /// creek, spillway, the pool under a waterfall) are carved into the terrain at planning time and lined. Fields are rows
    /// of tea hedges, greenhouse tunnels, orchard trees and conifer groves, every plant kept clear of the whole course.
    /// </summary>
    public static partial class LandmarkKits
    {
        // ------------------------------------------------------------------ water

        static bool IsChannel(string type) => type == "canal" || type == "creek" || type == "spillway";

        static void ChannelShape(string type, out float halfWidth, out float depth)
        {
            switch (type)
            {
                case "canal": halfWidth = 4f; depth = 3f; break;
                case "creek": halfWidth = 1.6f; depth = 1.4f; break;
                default: halfWidth = 2.2f; depth = 1.6f; break; // spillway
            }
        }

        /// <summary>Lateral distance of a channel's centre outside the barrier.</summary>
        static float ChannelLateral(TrackData track, RouteLandmarkDef lm, string type)
        {
            int side = lm.Side == "left" ? -1 : 1;
            ChannelShape(type, out float hw, out _);
            TrackSample s = track.SampleAt(lm.AtMetres);
            return Mathf.Max(hw * 2.2f + 1.5f, lm.OffsetMetres - Mathf.Abs(RoadGeometry.BarrierLateral(s, side)));
        }

        static Vector3 ChannelPoint(TrackData track, float metres, int side, float lateral)
        {
            TrackSample s = track.SampleAt(metres);
            return s.Position + s.Right * (RoadGeometry.BarrierLateral(s, side) + side * lateral);
        }

        /// <summary>Terrain carves for water landmarks (before the terrain exists).</summary>
        static void PlanWater(TrackData track, RouteLandmarkDef lm, LandmarkPlan plan)
        {
            string type = TypeOf(lm);
            int side = lm.Side == "left" ? -1 : 1;
            if (IsChannel(type))
            {
                ChannelShape(type, out float hw, out float depth);
                float extent = P(lm, "extent", 200f), lateral = ChannelLateral(track, lm, type);
                for (float t = 0f; t < extent; t += 20f)
                {
                    Vector3 a = ChannelPoint(track, lm.AtMetres - extent * 0.5f + t, side, lateral);
                    Vector3 b = ChannelPoint(track, lm.AtMetres - extent * 0.5f + Mathf.Min(extent, t + 20f), side, lateral);
                    // Where the course doubles back (hairpins), the offset line reaches another part of the road: no channel there.
                    if (OverRoad(track, a, hw * 2.2f + 1f, out _) || OverRoad(track, b, hw * 2.2f + 1f, out _)) continue;
                    plan.Carves.Add(new TerrainCarve { A = a, B = b, HalfWidth = hw, Depth = depth });
                }
            }
            else if (type == "waterfall")
            {
                Vector3 pool = ChannelPoint(track, lm.AtMetres, side, Mathf.Max(8f, lm.OffsetMetres - Mathf.Abs(RoadGeometry.BarrierLateral(track.SampleAt(lm.AtMetres), side))));
                plan.Carves.Add(new TerrainCarve { A = pool - Vector3.right * 2f, B = pool + Vector3.right * 2f, HalfWidth = 4f, Depth = 2.2f });
            }
        }

        static GameObject Water(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            if (profile == GenerationProfile.CollisionOnly) return null; // water is scenery only
            string type = TypeOf(lm);
            float extent = P(lm, "extent", 300f);
            int side = lm.Side == "left" ? -1 : 1;
            var mb = Kit();
            if (IsChannel(type))
            {
                ChannelShape(type, out float hw, out float depth);
                float lateral = ChannelLateral(track, lm, type);
                bool steps = type == "spillway";
                float prevY = float.NaN;
                for (float t = 0f; t < extent; t += 5f)
                {
                    float d0 = lm.AtMetres - extent * 0.5f + t, d1 = d0 + 5f;
                    Vector3 a = ChannelPoint(track, d0, side, lateral), b = ChannelPoint(track, d1, side, lateral);
                    if (OverRoad(track, a, hw * 2.2f + 1f, out _) || OverRoad(track, b, hw * 2.2f + 1f, out _)) { prevY = float.NaN; continue; }
                    float ga = Ground(ground, a, a.y - depth), gb = Ground(ground, b, b.y - depth);
                    Vector3 dir = b - a;
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 0.01f) continue;
                    Quaternion r = Quaternion.LookRotation(dir.normalized, Vector3.up);
                    Vector3 c = new Vector3((a.x + b.x) * 0.5f, 0f, (a.z + b.z) * 0.5f);
                    float bed = Mathf.Min(ga, gb);
                    float water = steps ? bed + 0.35f : (float.IsNaN(prevY) ? bed + 0.4f : Mathf.Min(prevY, bed + 0.4f) + (bed + 0.4f - Mathf.Min(prevY, bed + 0.4f)) * 0.5f);
                    prevY = water;
                    float half = dir.magnitude * 0.5f + 0.03f;
                    mb.AddBox((int)M.Water, new Vector3(c.x, water, c.z), new Vector3(hw * 0.85f, 0.02f, half), r, 0.1f);
                    // Stone (creek) or concrete (canal, spillway) lining from the bed up to the banks.
                    M lining = type == "creek" ? M.Stone : M.Concrete;
                    foreach (int k in new[] { -1, 1 })
                    {
                        Vector3 bank = c + r * new Vector3(k * hw * 0.9f, 0f, 0f);
                        float top = Ground(ground, c + r * new Vector3(k * hw * 1.5f, 0f, 0f), water + 1f) + 0.15f;
                        mb.AddBox((int)lining, new Vector3(bank.x, (bed - 0.3f + top) * 0.5f, bank.z), new Vector3(0.25f, Mathf.Max(0.2f, (top - bed + 0.3f) * 0.5f), half), r, 0.3f);
                    }
                    mb.AddBox((int)lining, new Vector3(c.x, bed - 0.35f, c.z), new Vector3(hw, 0.1f, half), r, 0.3f);
                    if (steps && Mathf.Repeat(t, 10f) < 0.1f) // fishway weir with a notch
                    {
                        mb.AddBox((int)M.Concrete, new Vector3(c.x, water + 0.15f, c.z) + r * new Vector3(-hw * 0.45f, 0f, half), new Vector3(hw * 0.4f, 0.5f, 0.15f), r, 0.4f);
                        mb.AddBox((int)M.Concrete, new Vector3(c.x, water + 0.15f, c.z) + r * new Vector3(hw * 0.45f, 0f, half), new Vector3(hw * 0.4f, 0.5f, 0.15f), r, 0.4f);
                        mb.AddBox((int)M.OffWhite, new Vector3(c.x, water + 0.04f, c.z) + r * new Vector3(0f, 0f, half + 0.3f), new Vector3(hw * 0.12f, 0.03f, 0.3f), r, 0.4f);
                    }
                    if (type == "canal" && Mathf.Abs(t - extent * 0.5f) < 2.5f && S(lm, "feature", "") == "sluice-gate")
                    {
                        // Sluice gate: two concrete piers, a steel leaf and the hoist wheel on a walkway.
                        foreach (int k in new[] { -1, 1 })
                            mb.AddBox((int)M.Concrete, new Vector3(c.x, bed + 2.4f, c.z) + r * new Vector3(k * (hw + 0.4f), 0f, 0f), new Vector3(0.6f, 2.8f, 1.2f), r, 0.3f);
                        mb.AddBox((int)M.SteelGrey, new Vector3(c.x, water + 0.6f, c.z), new Vector3(hw, 1.2f, 0.12f), r, 0.3f);
                        mb.AddBox((int)M.Concrete, new Vector3(c.x, bed + 5.3f, c.z), new Vector3(hw + 1f, 0.2f, 1.1f), r, 0.3f);
                        Frustum(mb, M.SteelRed, new Vector3(c.x, bed + 5.5f, c.z), 0.7f, 0.7f, 0.15f, 14);
                    }
                }
            }
            else if (type == "waterfall")
            {
                // A rock face beside the road with a sheet of water falling into a pool.
                float lateral = Mathf.Max(8f, lm.OffsetMetres - Mathf.Abs(RoadGeometry.BarrierLateral(track.SampleAt(lm.AtMetres), side)));
                Vector3 pool = ChannelPoint(track, lm.AtMetres, side, lateral);
                TrackSample s = track.SampleAt(lm.AtMetres);
                Vector3 outDir = new Vector3(s.Right.x, 0f, s.Right.z).normalized * side;
                Quaternion face = Quaternion.LookRotation(-outDir, Vector3.up);
                float g = Ground(ground, pool, pool.y), height = Mathf.Clamp(extent * 0.6f, 10f, 30f);
                Vector3 cliff = new Vector3(pool.x, g, pool.z) + outDir * 5f;
                var rng = Rng(track, lm);
                for (int i = 0; i < 14; i++)
                {
                    float x = ((float)rng.NextDouble() - 0.5f) * 16f, y = (float)rng.NextDouble() * height, z = (float)rng.NextDouble() * 4f;
                    if (Mathf.Abs(x) < 1.8f && z < 1.5f) z += 1.5f;
                    mb.AddBox((int)M.Stone, cliff + face * new Vector3(x, y, -z), new Vector3(2.5f + (float)rng.NextDouble() * 2f, 2f + (float)rng.NextDouble() * 3f, 2f), face * Quaternion.Euler((float)rng.NextDouble() * 20f, (float)rng.NextDouble() * 40f, 0f), 0.3f);
                }
                mb.AddBox((int)M.Stone, cliff + face * new Vector3(0f, height * 0.5f, -3.5f), new Vector3(9f, height * 0.5f + 1f, 2.5f), face, 0.2f);
                mb.AddBox((int)M.Water, cliff + face * new Vector3(0f, height * 0.5f + 0.3f, -0.9f), new Vector3(1.4f, height * 0.5f, 0.05f), face, 0.1f);
                mb.AddBox((int)M.Water, new Vector3(pool.x, g + 0.25f, pool.z), new Vector3(4.5f, 0.02f, 4.5f), face, 0.1f);
                for (int i = 0; i < 6; i++)
                    mb.AddBox((int)M.OffWhite, new Vector3(pool.x, g + 0.3f, pool.z) + outDir * (4f - i * 0.5f) + face * new Vector3(((float)rng.NextDouble() - 0.5f) * 3f, 0f, 0f), new Vector3(0.7f, 0.05f, 0.4f), face, 0.4f);
            }
            else
            {
                // Open water: sea, reservoir, inlet — a plane below every road it covers.
                Site site = SiteOf(track, lm, ground, 1f, 1f, 2f);
                float half = Mathf.Clamp(extent * 0.5f, 60f, 3000f);
                // The water starts at the authored offset and runs away from the road (the shore side stays dry land).
                Vector3 centre = site.Pos + site.Out * half;
                float lowestRoad = float.MaxValue;
                foreach (TrackSample k in track.Samples)
                    if (Mathf.Abs(k.Position.x - centre.x) < half + 20f && Mathf.Abs(k.Position.z - centre.z) < half + 20f) lowestRoad = Mathf.Min(lowestRoad, k.Position.y);
                float level = Mathf.Min(site.Pos.y - 0.5f, lowestRoad - (type == "sea" ? 4f : 3f));
                const int grid = 10;
                for (int i = 0; i < grid; i++)
                for (int j = 0; j < grid; j++)
                {
                    float x0 = -half + i * 2f * half / grid, x1 = x0 + 2f * half / grid, z0 = -half + j * 2f * half / grid, z1 = z0 + 2f * half / grid;
                    mb.AddFlatQuad((int)(type == "sea" ? M.Sea : M.Water), new Vector3(centre.x + x0, level, centre.z + z0), new Vector3(centre.x + x0, level, centre.z + z1),
                        new Vector3(centre.x + x1, level, centre.z + z1), new Vector3(centre.x + x1, level, centre.z + z0), new Vector2(0.02f, 0.02f));
                }
                if (type == "reservoir")
                {
                    // A curved concrete dam near the road side of the lake, lamps along its crest when lit.
                    Vector3 damMid = site.Pos + site.Out * 20f;
                    float gd = Ground(ground, damMid, level);
                    float top = Mathf.Max(level + 2f, gd + 4f), bottom = Mathf.Min(gd, level) - 6f;
                    const int segs = 12;
                    float len = Mathf.Min(half * 0.5f, 160f);
                    for (int i = 0; i < segs; i++)
                    {
                        float a0 = (i / (float)segs - 0.5f) * 0.9f, a1 = ((i + 1) / (float)segs - 0.5f) * 0.9f;
                        Vector3 p0 = damMid + site.Along * (Mathf.Sin(a0) * len) + site.Out * ((1f - Mathf.Cos(a0)) * len * 0.5f);
                        Vector3 p1 = damMid + site.Along * (Mathf.Sin(a1) * len) + site.Out * ((1f - Mathf.Cos(a1)) * len * 0.5f);
                        Vector3 cm = (p0 + p1) * 0.5f;
                        Quaternion rr = Quaternion.LookRotation((p1 - p0).normalized, Vector3.up);
                        mb.AddBox((int)M.Concrete, new Vector3(cm.x, (top + bottom) * 0.5f, cm.z), new Vector3(2.5f, (top - bottom) * 0.5f, (p1 - p0).magnitude * 0.5f + 0.05f), rr, 0.2f);
                        if (B(lm, "lit") && i % 2 == 0)
                        {
                            mb.AddBox((int)M.SteelGrey, new Vector3(p0.x, top + 2.5f, p0.z), new Vector3(0.08f, 2.5f, 0.08f), rr, 0.5f);
                            mb.AddBox((int)M.WindowLit, new Vector3(p0.x, top + 5.1f, p0.z), new Vector3(0.25f, 0.2f, 0.25f), rr, 0.5f);
                        }
                    }
                }
            }
            return Emit(lm.Name, mb, track, lm, "", mats, parent, Vector3.zero, Quaternion.identity, false, GameLayers.Scenery, profile);
        }

        // ------------------------------------------------------------------ fields

        static GameObject Field(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            if (profile == GenerationProfile.CollisionOnly) return null;
            string type = TypeOf(lm);
            float extent = P(lm, "extent", 200f);
            int side = lm.Side == "left" ? -1 : 1;
            TrackSample s0 = track.SampleAt(lm.AtMetres);
            float lateral0 = Mathf.Max(3f, lm.OffsetMetres - Mathf.Abs(RoadGeometry.BarrierLateral(s0, side)));
            var rng = Rng(track, lm);
            var mb = Kit();
            bool OkAt(Vector3 p, float r) => Clear(track, p, Vector3.right, Vector3.forward, r, r, 3.5f);
            switch (type)
            {
                case "tea-terraces":
                {
                    // Contour rows of clipped tea hedges: half-round bushes running along the slope.
                    for (int row = 0; row < 16; row++)
                    {
                        float lat = lateral0 + row * 2.3f;
                        var ring = new List<int>();
                        Vector3 prevP = default;
                        bool open = false;
                        for (float t = 0f; t <= extent; t += 2.5f)
                        {
                            Vector3 p = Beside(track, ground, lm.AtMetres - extent * 0.5f + t, side, lat);
                            bool ok = OkAt(p, 0.8f);
                            if (!ok) { open = false; continue; }
                            Vector3 dir = open ? (p - prevP) : Vector3.forward;
                            dir.y = 0f;
                            Vector3 right = Vector3.Cross(Vector3.up, dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward);
                            int first = mb.VertexCount;
                            for (int k = 0; k <= 6; k++)
                            {
                                float a = k / 6f * Mathf.PI;
                                mb.AddVertex(p + right * (Mathf.Cos(a) * 0.8f) + Vector3.up * (Mathf.Sin(a) * 0.75f - 0.05f), Vector3.up, Vector2.zero);
                            }
                            if (open)
                                for (int k = 0; k < 6; k++)
                                {
                                    int a0 = first - 7 + k, b0 = first + k;
                                    Vector3 pa = mb.Position(a0), pb = mb.Position(a0 + 1), pc = mb.Position(b0 + 1);
                                    Vector3 want = (pa + pc) * 0.5f - p + Vector3.up * 0.2f;
                                    if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), want) >= 0f) mb.AddQuad((int)M.Foliage, a0, a0 + 1, b0 + 1, b0);
                                    else mb.AddQuad((int)M.Foliage, a0, b0, b0 + 1, a0 + 1);
                                }
                            prevP = p;
                            open = true;
                        }
                    }
                    break;
                }
                case "greenhouses":
                {
                    // Rows of hoop greenhouses: white film over steel hoops, gable ends, a path between.
                    const float gw = 6f, gh = 3.2f, gl = 28f;
                    int houses = Mathf.Max(2, Mathf.RoundToInt(extent / (gl + 4f)));
                    for (int row = 0; row < 3; row++)
                    for (int i = 0; i < houses; i++)
                    {
                        float at = lm.AtMetres - extent * 0.5f + (i + 0.5f) * extent / houses;
                        Vector3 c = Beside(track, ground, at, side, lateral0 + gw * 0.6f + row * (gw + 3f));
                        TrackSample s = track.SampleAt(at);
                        Vector3 along = new Vector3(s.Tangent.x, 0f, s.Tangent.z).normalized, right = Vector3.Cross(Vector3.up, along);
                        if (!OkAt(c, gl * 0.5f)) continue;
                        const int segs = 8;
                        for (int k = 0; k < segs; k++)
                        {
                            float a0 = k / (float)segs * Mathf.PI, a1 = (k + 1) / (float)segs * Mathf.PI;
                            Vector3 q0 = right * (Mathf.Cos(a0) * gw * 0.5f) + Vector3.up * (Mathf.Sin(a0) * gh), q1 = right * (Mathf.Cos(a1) * gw * 0.5f) + Vector3.up * (Mathf.Sin(a1) * gh);
                            mb.AddFlatQuad((int)M.OffWhite, c + q0 - along * gl * 0.5f, c + q1 - along * gl * 0.5f, c + q1 + along * gl * 0.5f, c + q0 + along * gl * 0.5f, new Vector2(0.2f, 0.2f));
                            mb.AddFlatQuad((int)M.OffWhite, c + q0 + along * gl * 0.5f, c + q1 + along * gl * 0.5f, c + q1 - along * gl * 0.5f, c + q0 - along * gl * 0.5f, new Vector2(0.2f, 0.2f));
                            foreach (float e in new[] { -gl * 0.5f, gl * 0.5f })
                                TriFlat(mb, M.OffWhite, c + along * e, c + q0 + along * e, c + q1 + along * e);
                        }
                        for (float z = -gl * 0.5f; z <= gl * 0.5f; z += 3.5f)
                            for (int k = 0; k < segs; k++)
                            {
                                float a0 = k / (float)segs * Mathf.PI, a1 = (k + 1) / (float)segs * Mathf.PI;
                                Beam(mb, M.SteelGrey, c + along * z + right * (Mathf.Cos(a0) * gw * 0.51f) + Vector3.up * (Mathf.Sin(a0) * gh * 1.01f),
                                    c + along * z + right * (Mathf.Cos(a1) * gw * 0.51f) + Vector3.up * (Mathf.Sin(a1) * gh * 1.01f), 0.03f);
                            }
                    }
                    break;
                }
                default: // orchard, cedar-grove, white-pine-grove, pine-grove
                {
                    bool orchard = type == "orchard";
                    float spacing = orchard ? 5f : type == "cedar-grove" ? 7.5f : 9.5f;
                    float depth = orchard ? 30f : 55f;
                    for (float t = 0f; t <= extent; t += spacing)
                    for (float lat = 0f; lat <= depth; lat += spacing)
                    {
                        float jx = orchard ? 0f : ((float)rng.NextDouble() - 0.5f) * spacing * 0.6f;
                        float jy = orchard ? 0f : ((float)rng.NextDouble() - 0.5f) * spacing * 0.6f;
                        Vector3 p = Beside(track, ground, lm.AtMetres - extent * 0.5f + t + jx, side, lateral0 + lat + jy);
                        if (!OkAt(p, 2f)) continue;
                        float hgt = orchard ? 4f + (float)rng.NextDouble() : type == "cedar-grove" ? 18f + (float)rng.NextDouble() * 8f : 12f + (float)rng.NextDouble() * 6f;
                        Tree(mb, p + Vector3.down * 0.2f, hgt, !orchard, type == "white-pine-grove" ? 0.8f : 1f);
                    }
                    break;
                }
            }
            return Emit(lm.Name, mb, track, lm, "", mats, parent, Vector3.zero, Quaternion.identity, false, GameLayers.Scenery, profile);
        }
    }
}
