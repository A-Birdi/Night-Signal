using System.Collections.Generic;
using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// The regional kit of a course's biome (spec: "reuse regional kits"): what grows and stands along the roads of each
    /// region — cedar forest in Kasumi, broadleaf trees and bamboo in the Mizuhana foothills, pines and rock by the
    /// Kurogawa reservoirs, wind-bent pines and shore rock on the Akebono coast, outcrops and a pole line on the Hoshimi
    /// uplands, snow poles, firs and snow patches in the Tsukishiro highland, lamps and hedges on the Hinode campus.
    /// Deterministic per course; visual only (no colliders, skipped by the collision-only server build); kept off every leg
    /// of the road, landmark footprints, open water and the ground under bridge decks; built in 250 m chunks so the
    /// cameras cull what they do not see.
    /// </summary>
    public static partial class LandmarkKits
    {
        sealed class RegionKit
        {
            public float Step = 11f;   // metres between placements along the road
            public int Rows = 3;       // rows outward from the barrier
            public float Near = 5f;    // first row, metres outside the barrier
            public float RowGap = 9f;
            public float Conifer, Broadleaf, Rock, Bamboo, Snow, Hedge, Pine;
            public float TreeMin = 10f, TreeMax = 18f;
            public float SnowPoles, LampPosts, PoleLine; // roadside furniture spacing (0 = none)
        }

        static RegionKit RegionFor(string biome)
        {
            switch (biome)
            {
                case "kasumi-forest": return new RegionKit { Step = 8f, Rows = 5, Near = 4f, RowGap = 7f, Conifer = 0.85f, Rock = 0.03f, TreeMin = 16f, TreeMax = 28f };
                case "mizuhana-foothills": return new RegionKit { Step = 16f, Rows = 3, Near = 6f, RowGap = 12f, Broadleaf = 0.3f, Bamboo = 0.12f, Hedge = 0.1f, TreeMin = 7f, TreeMax = 12f };
                case "kurogawa-reservoir": return new RegionKit { Step = 12f, Rows = 3, Near = 5f, RowGap = 9f, Conifer = 0.35f, Rock = 0.18f, TreeMin = 12f, TreeMax = 20f };
                case "akebono-coast": return new RegionKit { Step = 15f, Rows = 2, Near = 5f, RowGap = 10f, Pine = 0.28f, Rock = 0.2f, TreeMin = 8f, TreeMax = 13f };
                case "hoshimi-uplands": return new RegionKit { Step = 14f, Rows = 3, Near = 5f, RowGap = 11f, Rock = 0.32f, Broadleaf = 0.06f, TreeMin = 6f, TreeMax = 9f, PoleLine = 45f };
                case "tsukishiro-highland":
                case "amanagi-finale": return new RegionKit { Step = 13f, Rows = 3, Near = 5f, RowGap = 10f, Conifer = 0.3f, Rock = 0.22f, Snow = 0.2f, TreeMin = 9f, TreeMax = 16f, SnowPoles = 24f };
                case "hinode-campus": return new RegionKit { Step = 20f, Rows = 1, Near = 3f, RowGap = 6f, Hedge = 0.6f, Broadleaf = 0.2f, TreeMin = 6f, TreeMax = 9f, LampPosts = 32f };
                default: return new RegionKit { Step = 16f, Rows = 2, Broadleaf = 0.25f };
            }
        }

        const float RegionalChunkMetres = 250f;

        /// <summary>Builds the course's regional scatter under <c>root/Regional</c> (after the landmarks, which it keeps clear of).</summary>
        public static void BuildRegional(TrackData track, RouteDefinition route, TerrainCollider ground, Transform root, CourseMaterialSet mats, GenerationProfile profile)
        {
            if (profile == GenerationProfile.CollisionOnly) return;
            Physics.SyncTransforms();
            RegionKit kit = RegionFor(route.Biome);
            var scatter = new GameObject("Regional").transform;
            scatter.SetParent(root, false);
            RegionObstacles avoid = ObstaclesOf(root);
            var rng = new System.Random(unchecked((track.CourseId ?? "").GetHashCode() * 7919 + 17));
            for (float c0 = 0f; c0 < track.LengthMetres; c0 += RegionalChunkMetres)
            {
                var mb = Kit();
                float c1 = Mathf.Min(track.LengthMetres, c0 + RegionalChunkMetres);
                for (float d = c0; d < c1; d += kit.Step)
                foreach (int side in new[] { -1, 1 })
                for (int row = 0; row < kit.Rows; row++)
                {
                    float jitter = ((float)rng.NextDouble() - 0.5f) * kit.Step * 0.8f;
                    float lateral = kit.Near + row * kit.RowGap + (float)rng.NextDouble() * kit.RowGap * 0.6f;
                    float roll = (float)rng.NextDouble(), size = (float)rng.NextDouble();
                    Vector3 p = Beside(track, ground, Mathf.Clamp(d + jitter, 0f, track.LengthMetres), side, lateral);
                    float hgt = Mathf.Lerp(kit.TreeMin, kit.TreeMax, size);
                    // Choose the prop first: each keeps its own reach (crown, clump, length) clear of every leg of the road.
                    int prop = 0;
                    float acc = 0f;
                    if (roll < (acc += kit.Conifer)) prop = 1;
                    else if (roll < (acc += kit.Pine)) prop = 2;
                    else if (roll < (acc += kit.Broadleaf)) prop = 3;
                    else if (roll < (acc += kit.Bamboo)) prop = 4;
                    else if (roll < (acc += kit.Rock)) prop = 5;
                    else if (roll < (acc += kit.Snow)) prop = 6;
                    else if (roll < (acc += kit.Hedge)) prop = 7;
                    if (prop == 0) continue;
                    float reach;
                    switch (prop)
                    {
                        case 1: reach = hgt * (row >= 2 ? 0.2f : 0.24f); break;
                        case 2: reach = hgt * 0.55f; break;
                        case 3: reach = hgt * 0.32f; break;
                        case 4: reach = 2.5f; break;
                        case 5: reach = 1.2f + size * 3f; break;
                        case 6: reach = (2f + size * 4f) * 1.6f; break;
                        default: reach = (3f + size * 3f) * 0.5f; break;
                    }
                    if (!RegionAllowed(track, p, avoid, reach + 1.5f)) continue;
                    switch (prop)
                    {
                        case 1: Tree(mb, p + Vector3.down * 0.2f, hgt, true, 1f, row >= 2); break;
                        case 2: WindPine(mb, p, hgt, rng); break;
                        case 3: Tree(mb, p + Vector3.down * 0.2f, hgt, false); break;
                        case 4: Bamboo(mb, p, rng); break;
                        case 5: Outcrop(mb, p, 1.2f + size * 3f, rng); break;
                        case 6: SnowPatch(mb, p, 2f + size * 4f, rng); break;
                        default: Hedge(mb, p, 3f + size * 3f); break;
                    }
                }
                foreach (int side in new[] { -1, 1 })
                {
                    if (kit.SnowPoles > 0f)
                        for (float d = c0 + kit.SnowPoles * 0.5f; d < c1; d += kit.SnowPoles)
                        {
                            Vector3 p = Beside(track, ground, d, side, 0.9f);
                            if (RegionAllowed(track, p, avoid, 0.4f)) SnowPole(mb, p);
                        }
                    if (kit.LampPosts > 0f)
                        for (float d = c0 + kit.LampPosts * 0.5f; d < c1; d += kit.LampPosts)
                        {
                            Vector3 p = Beside(track, ground, d, side, 1.2f);
                            if (RegionAllowed(track, p, avoid, 0.6f)) LampPost(mb, p, track.SampleAt(d), side);
                        }
                }
                if (kit.PoleLine > 0f)
                {
                    Vector3 prev = default;
                    bool has = false;
                    for (float d = c0; d < c1; d += kit.PoleLine)
                    {
                        Vector3 p = Beside(track, ground, d, 1, 7f);
                        if (!RegionAllowed(track, p, avoid, 1f)) { has = false; continue; }
                        UtilityPole(mb, p);
                        // A straight span cuts across the road on a bend: no wire over any road.
                        bool crosses = false;
                        for (int k = 1; k < 8 && has && !crosses; k++) crosses = OverRoad(track, Vector3.Lerp(prev, p, k / 8f), 1f, out _);
                        if (has && !crosses) foreach (float dy in new[] { 7.6f, 7.0f }) Cable(mb, M.Graphite, prev + Vector3.up * dy, p + Vector3.up * dy, 0.6f, 0.012f, 6);
                        prev = p;
                        has = true;
                    }
                }
                if (mb.VertexCount == 0) continue;
                Mesh mesh = mb.BuildCompact($"{track.CourseId}_regional_{Mathf.RoundToInt(c0)}", out int[] used);
                var materials = new Material[used.Length];
                for (int i = 0; i < used.Length; i++) materials[i] = Mat(mats, (M)used[i]);
                Place($"Chunk{Mathf.RoundToInt(c0)}", mesh, materials, scatter, Vector3.zero, Quaternion.identity, false, GameLayers.Scenery, profile);
            }
        }

        sealed class RegionObstacles
        {
            public readonly List<Bounds> Footprints = new List<Bounds>();
            public readonly List<Bounds> Water = new List<Bounds>();
        }

        /// <summary>Areas scatter keeps out of: landmark footprints (plus a margin) and open-water planes.</summary>
        static RegionObstacles ObstaclesOf(Transform root)
        {
            var o = new RegionObstacles();
            Transform landmarks = root.Find("Landmarks");
            if (landmarks == null) return o;
            foreach (Transform lm in landmarks)
            foreach (Renderer r in lm.GetComponentsInChildren<Renderer>(true))
            {
                Bounds b = r.bounds;
                if (b.size.y < 0.2f && b.size.x > 50f) o.Water.Add(b); // an open-water plane
                else if (b.size.x < 400f && b.size.z < 400f) // long thin things (railways, walls) are not areas
                {
                    b.Expand(new Vector3(6f, 0f, 6f));
                    o.Footprints.Add(b);
                }
            }
            return o;
        }

        static bool RegionAllowed(TrackData track, Vector3 p, RegionObstacles avoid, float margin)
        {
            if (!Clear(track, p, Vector3.right, Vector3.forward, 0f, 0f, margin, 1)) return false; // every sample: small margins near hairpins
            foreach (Bounds b in avoid.Footprints)
                if (p.x > b.min.x && p.x < b.max.x && p.z > b.min.z && p.z < b.max.z) return false;
            foreach (Bounds w in avoid.Water)
                if (p.x > w.min.x && p.x < w.max.x && p.z > w.min.z && p.z < w.max.z && p.y < w.center.y + 0.4f) return false;
            // Under a bridge or viaduct deck (anything the cars drive on or hit, overhead) nothing grows.
            return !Physics.Raycast(p + Vector3.up * 0.5f, Vector3.up, 40f, GameLayers.DrivableMask | GameLayers.BarrierMask, QueryTriggerInteraction.Ignore);
        }

        // ------------------------------------------------------------------ regional props

        /// <summary>A coastal pine: a leaning trunk and flat, wind-swept foliage pads.</summary>
        static void WindPine(MeshBuilder mb, Vector3 foot, float height, System.Random rng)
        {
            Vector3 lean = new Vector3((float)rng.NextDouble() - 0.5f, 0f, (float)rng.NextDouble() - 0.5f).normalized * height * 0.25f;
            Vector3 top = foot + Vector3.up * height * 0.8f + lean;
            Beam(mb, M.Bark, foot, top, 0.18f + height * 0.01f);
            for (int i = 0; i < 3; i++)
            {
                Vector3 c = Vector3.Lerp(foot + Vector3.up * height * 0.55f, top, i / 2f) + lean.normalized * (i * 0.6f);
                float r = height * (0.28f - i * 0.06f);
                Lathe(mb, M.Foliage, c, new[] { new Vector2(r * 0.6f, -0.3f), new Vector2(r, 0.1f), new Vector2(r * 0.4f, 0.8f), new Vector2(0.05f, 1f) }, 8, true);
            }
        }

        /// <summary>A clump of bamboo culms with leaf tufts.</summary>
        static void Bamboo(MeshBuilder mb, Vector3 foot, System.Random rng)
        {
            int culms = 5 + rng.Next(5);
            for (int i = 0; i < culms; i++)
            {
                Vector3 b = foot + new Vector3(((float)rng.NextDouble() - 0.5f) * 2f, 0f, ((float)rng.NextDouble() - 0.5f) * 2f);
                float h = 6f + (float)rng.NextDouble() * 5f;
                Vector3 t = b + Vector3.up * h + new Vector3(((float)rng.NextDouble() - 0.5f) * 1.5f, 0f, ((float)rng.NextDouble() - 0.5f) * 1.5f);
                Beam(mb, M.Foliage, b, t, 0.05f);
                mb.AddBox((int)M.Foliage, t + Vector3.down * 0.6f, new Vector3(0.9f, 0.5f, 0.9f), Quaternion.Euler(0f, (float)rng.NextDouble() * 90f, 20f), 0.5f);
            }
        }

        /// <summary>A rock outcrop: a few tilted stone blocks half sunk into the ground.</summary>
        static void Outcrop(MeshBuilder mb, Vector3 at, float size, System.Random rng)
        {
            int blocks = 2 + rng.Next(3);
            for (int i = 0; i < blocks; i++)
            {
                Vector3 c = at + new Vector3(((float)rng.NextDouble() - 0.5f) * size, size * 0.2f * (float)rng.NextDouble(), ((float)rng.NextDouble() - 0.5f) * size);
                Vector3 half = new Vector3(size * (0.35f + 0.3f * (float)rng.NextDouble()), size * (0.25f + 0.3f * (float)rng.NextDouble()), size * (0.3f + 0.3f * (float)rng.NextDouble()));
                mb.AddBox((int)M.Stone, c, half, Quaternion.Euler((float)rng.NextDouble() * 25f, (float)rng.NextDouble() * 180f, (float)rng.NextDouble() * 25f), 0.3f);
            }
        }

        /// <summary>A lingering patch of snow: a low irregular slab on the ground.</summary>
        static void SnowPatch(MeshBuilder mb, Vector3 at, float size, System.Random rng)
        {
            var prof = new List<Vector2> { new Vector2(size, -0.1f), new Vector2(size * 0.8f, 0.12f), new Vector2(0.05f, 0.2f) };
            Lathe(mb, M.OffWhite, at, prof, 7);
            if (rng.NextDouble() < 0.5)
                Lathe(mb, M.OffWhite, at + new Vector3(size * 0.7f, 0f, size * 0.3f), new[] { new Vector2(size * 0.5f, -0.1f), new Vector2(size * 0.4f, 0.1f), new Vector2(0.05f, 0.16f) }, 6);
        }

        /// <summary>A clipped hedge block.</summary>
        static void Hedge(MeshBuilder mb, Vector3 at, float length)
        {
            mb.AddBox((int)M.Foliage, at + Vector3.up * 0.55f, new Vector3(length * 0.5f, 0.6f, 0.6f), Quaternion.Euler(0f, at.x * 13f % 180f, 0f), 0.4f);
        }

        /// <summary>A red-and-white snow pole marking the road edge for the plough.</summary>
        static void SnowPole(MeshBuilder mb, Vector3 at)
        {
            for (int i = 0; i < 6; i++)
                mb.AddBox((int)(i % 2 == 0 ? M.SteelRed : M.OffWhite), at + Vector3.up * (0.25f + i * 0.5f), new Vector3(0.04f, 0.25f, 0.04f), Quaternion.identity, 0.5f);
            mb.AddBox((int)M.SteelYellow, at + Vector3.up * 3.05f, new Vector3(0.05f, 0.05f, 0.05f), Quaternion.identity, 0.5f);
        }

        /// <summary>A campus lamp post leaning its lamp over the road edge.</summary>
        static void LampPost(MeshBuilder mb, Vector3 at, TrackSample s, int side)
        {
            Vector3 toRoad = -new Vector3(s.Right.x, 0f, s.Right.z).normalized * side;
            // The head reaches toward the road above the overhead clearance.
            mb.AddBox((int)M.SteelGrey, at + Vector3.up * 3.6f, new Vector3(0.07f, 3.6f, 0.07f), Quaternion.identity, 0.5f);
            Beam(mb, M.SteelGrey, at + Vector3.up * 7.1f, at + Vector3.up * 7.1f + toRoad * 1.2f, 0.05f);
            mb.AddBox((int)M.WindowLit, at + Vector3.up * 6.95f + toRoad * 1.2f, new Vector3(0.25f, 0.08f, 0.18f), Quaternion.LookRotation(toRoad), 0.5f);
        }

        /// <summary>A timber utility pole with a cross-arm and insulators.</summary>
        static void UtilityPole(MeshBuilder mb, Vector3 at)
        {
            Frustum(mb, M.WoodDark, at + Vector3.down * 0.5f, 0.16f, 0.11f, 8.5f, 7);
            mb.AddBox((int)M.WoodDark, at + Vector3.up * 7.6f, new Vector3(0.9f, 0.06f, 0.06f), Quaternion.identity, 0.5f);
            foreach (float x in new[] { -0.75f, 0.75f })
                mb.AddBox((int)M.OffWhite, at + new Vector3(x, 7.72f, 0f), new Vector3(0.05f, 0.08f, 0.05f), Quaternion.identity, 0.5f);
        }
    }
}
