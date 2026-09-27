using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>A linear depression carved into the terrain (creeks under bridges, drainage).</summary>
    public struct TerrainCarve
    {
        public Vector3 A, B;
        public float HalfWidth;
        public float Depth;
    }

    [System.Serializable]
    public sealed class TerrainStyle
    {
        public float HillAmplitude = 38f;
        public float HillScale = 650f;
        public float ValleyRise = 0.2f;
        public float ValleyStart = 380f;
        public float TeaRowCoverage;
        public int Seed = 1;
    }

    /// <summary>
    /// Generates the terrain around a route: road-following corridor, embankment blend, rolling hills, valley walls
    /// rising to distant ridges, carved creeks, and a slope/distance splat map. Near-road cells use exact nearest
    /// samples stamped from the centreline; far cells interpolate a coarse nearest field.
    /// </summary>
    public static class TerrainGeometry
    {
        const int HeightRes = 1025;
        const int AlphaRes = 1024;
        const float Margin = 480f;
        const float Blend = 30f;
        const float NearRadius = 48f;
        const int CoarseRes = 129;

        public static GameObject Build(TrackData track, Transform parent, CourseMaterialSet mats, TerrainStyle style,
            List<TerrainCarve> carves, GenerationProfile profile, List<TerrainPad> pads = null)
        {
            TrackSample[] all = track.Samples;
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (TrackSample s in all)
            {
                min = Vector2.Min(min, new Vector2(s.Position.x, s.Position.z));
                max = Vector2.Max(max, new Vector2(s.Position.x, s.Position.z));
                minY = Mathf.Min(minY, s.Position.y);
                maxY = Mathf.Max(maxY, s.Position.y);
            }
            // Off-route areas (training pads, Test Yard) extend the terrain bounds.
            if (pads != null)
                foreach (TerrainPad pad in pads)
                {
                    float reach = pad.HalfSize.magnitude + AreaGeometry.PadBlendMetres;
                    min = Vector2.Min(min, new Vector2(pad.Centre.x - reach, pad.Centre.z - reach));
                    max = Vector2.Max(max, new Vector2(pad.Centre.x + reach, pad.Centre.z + reach));
                    minY = Mathf.Min(minY, pad.Centre.y);
                    maxY = Mathf.Max(maxY, pad.Centre.y);
                }
            min -= Vector2.one * Margin;
            max += Vector2.one * Margin;
            float size = Mathf.Ceil(Mathf.Max(max.x - min.x, max.y - min.y) / 8f) * 8f;
            float baseY = minY - 40f;
            float heightRange = (maxY - minY) + 320f;
            float step = size / (HeightRes - 1);

            // 1) Stamp exact nearest samples (every 2 m) into cells within NearRadius of the centreline.
            var nearIdx = new int[HeightRes * HeightRes];
            var nearD2 = new float[HeightRes * HeightRes];
            for (int i = 0; i < nearIdx.Length; i++) { nearIdx[i] = -1; nearD2[i] = float.MaxValue; }
            int rad = Mathf.CeilToInt(NearRadius / step);
            for (int si = 0; si < all.Length; si += 2)
            {
                Vector3 p = all[si].Position;
                int cx = Mathf.RoundToInt((p.x - min.x) / step), cz = Mathf.RoundToInt((p.z - min.y) / step);
                for (int dz = -rad; dz <= rad; dz++)
                {
                    int z = cz + dz;
                    if (z < 0 || z >= HeightRes) continue;
                    for (int dx = -rad; dx <= rad; dx++)
                    {
                        int x = cx + dx;
                        if (x < 0 || x >= HeightRes) continue;
                        float wx = min.x + x * step - p.x, wz = min.y + z * step - p.z;
                        float d2 = wx * wx + wz * wz;
                        int k = z * HeightRes + x;
                        if (d2 < nearD2[k]) { nearD2[k] = d2; nearIdx[k] = si; }
                    }
                }
            }

            // 2) Coarse nearest field for far cells (distance + index), brute force over every 12th sample.
            var coarseIdx = new int[CoarseRes * CoarseRes];
            var coarseDist = new float[CoarseRes * CoarseRes];
            float coarseStep = size / (CoarseRes - 1);
            for (int z = 0; z < CoarseRes; z++)
            for (int x = 0; x < CoarseRes; x++)
            {
                float wx = min.x + x * coarseStep, wz = min.y + z * coarseStep;
                float best = float.MaxValue;
                int bi = 0;
                for (int si = 0; si < all.Length; si += 12)
                {
                    float d2 = Sq(all[si].Position.x - wx) + Sq(all[si].Position.z - wz);
                    if (d2 < best) { best = d2; bi = si; }
                }
                coarseIdx[z * CoarseRes + x] = bi;
                coarseDist[z * CoarseRes + x] = Mathf.Sqrt(best);
            }

            // 3) Heights.
            var heights = new float[HeightRes, HeightRes];
            var edgeDist = new float[HeightRes * HeightRes];
            for (int z = 0; z < HeightRes; z++)
            for (int x = 0; x < HeightRes; x++)
            {
                float wx = min.x + x * step, wz = min.y + z * step;
                int k = z * HeightRes + x;
                TrackSample s;
                float dist;
                if (nearIdx[k] >= 0)
                {
                    s = all[nearIdx[k]];
                    dist = Mathf.Sqrt(nearD2[k]);
                }
                else
                {
                    float fx = wx - min.x, fz = wz - min.y;
                    int cx = Mathf.Clamp(Mathf.RoundToInt(fx / coarseStep), 0, CoarseRes - 1);
                    int cz = Mathf.Clamp(Mathf.RoundToInt(fz / coarseStep), 0, CoarseRes - 1);
                    s = all[coarseIdx[cz * CoarseRes + cx]];
                    dist = Mathf.Max(NearRadius, Mathf.Sqrt(Sq(s.Position.x - wx) + Sq(s.Position.z - wz)));
                }
                Vector3 flatRight = new Vector3(s.Right.x, 0f, s.Right.z).normalized;
                float lateral = (wx - s.Position.x) * flatRight.x + (wz - s.Position.z) * flatRight.z;
                float h = HeightAt(wx, wz, s, dist, lateral, style, carves);
                if (pads != null && dist > Corridor(s, lateral) + 0.5f) h = AreaGeometry.ApplyPads(wx, wz, h, pads);
                heights[z, x] = Mathf.Clamp01((h - baseY) / heightRange);
                edgeDist[k] = dist - Corridor(s, lateral);
            }

            var data = new TerrainData { heightmapResolution = HeightRes, size = new Vector3(size, heightRange, size) };
            data.SetHeights(0, 0, heights);

            GameObject go;
            if (profile == GenerationProfile.Full)
            {
                data.alphamapResolution = AlphaRes;
                data.terrainLayers = mats.TerrainLayers;
                data.SetAlphamaps(0, 0, Splat(data, edgeDist, style));
                go = Terrain.CreateTerrainGameObject(data);
                var terrain = go.GetComponent<Terrain>();
                terrain.materialTemplate = mats.TerrainTemplate;
                terrain.heightmapPixelError = 4f;
                terrain.basemapDistance = 900f;
                terrain.drawInstanced = true;
            }
            else
            {
                go = new GameObject("Terrain");
                go.AddComponent<TerrainCollider>().terrainData = data;
            }
            go.name = "Terrain";
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(min.x, baseY, min.y);
            go.layer = GameLayers.Drivable;
            go.AddComponent<SurfaceTag>().Surface = SurfaceKind.Grass;
            go.isStatic = true;
            return go;
        }

        static float Corridor(TrackSample s, float lateral) =>
            s.Width * 0.5f + (lateral < 0 ? s.ShoulderLeft : s.ShoulderRight) + 1.2f;

        static float HeightAt(float x, float z, TrackSample s, float dist, float lateral, TerrainStyle style, List<TerrainCarve> carves)
        {
            float corridor = Corridor(s, lateral);
            float lat = Mathf.Clamp(lateral, -corridor, corridor);
            float roadY = s.Position.y + s.Right.y * lat - 0.45f;

            float hills = (Fbm(x / style.HillScale, z / style.HillScale, style.Seed) - 0.45f) * style.HillAmplitude;
            float nearRamp = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(corridor + 10f, 220f, dist));
            float valley = Mathf.Max(0f, dist - style.ValleyStart) * style.ValleyRise;
            float ridges = Mathf.Max(0f, dist - style.ValleyStart - 150f) * 0.12f * Fbm(x / 250f, z / 250f, style.Seed + 7);
            float natural = s.Position.y + hills * nearRamp + valley + ridges;

            float h = dist <= corridor ? roadY : Mathf.Lerp(roadY, natural, Mathf.SmoothStep(0f, 1f, (dist - corridor) / Blend));
            if (carves != null && dist > corridor + 0.5f)
            {
                foreach (TerrainCarve c in carves)
                {
                    float d = DistanceToSegmentXZ(new Vector2(x, z), new Vector2(c.A.x, c.A.z), new Vector2(c.B.x, c.B.z));
                    if (d < c.HalfWidth * 2.2f)
                    {
                        float t = Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(c.HalfWidth * 0.6f, c.HalfWidth * 2.2f, d));
                        h -= c.Depth * t;
                    }
                }
            }
            return h;
        }

        static float[,,] Splat(TerrainData data, float[] edgeDist, TerrainStyle style)
        {
            var map = new float[AlphaRes, AlphaRes, 5];
            for (int zi = 0; zi < AlphaRes; zi++)
            for (int xi = 0; xi < AlphaRes; xi++)
            {
                float u = xi / (float)(AlphaRes - 1), v = zi / (float)(AlphaRes - 1);
                float slope = data.GetSteepness(u, v);
                int hk = Mathf.RoundToInt(v * (HeightRes - 1)) * HeightRes + Mathf.RoundToInt(u * (HeightRes - 1));
                float edge = edgeDist[hk];
                float wx = u * data.size.x, wz = v * data.size.z;
                float patch = Fbm(wx / 90f, wz / 90f, style.Seed + 3);
                float verge = Mathf.Clamp01(1f - edge / 7f);
                float rock = Mathf.Clamp01((slope - 30f) / 12f);
                float tea = style.TeaRowCoverage > 0f && edge > 12f && edge < 260f && slope < 14f && patch > 1f - style.TeaRowCoverage ? 1f : 0f;
                float soil = Mathf.Clamp01((patch - 0.62f) * 4f) * (1f - tea);
                float w0 = verge * (1f - rock);
                float w2 = tea * (1f - rock) * (1f - verge);
                float w3 = soil * (1f - verge) * (1f - rock);
                float w4 = rock;
                float w1 = Mathf.Max(0f, 1f - w0 - w2 - w3 - w4);
                float sum = w0 + w1 + w2 + w3 + w4;
                map[zi, xi, 0] = w0 / sum;
                map[zi, xi, 1] = w1 / sum;
                map[zi, xi, 2] = w2 / sum;
                map[zi, xi, 3] = w3 / sum;
                map[zi, xi, 4] = w4 / sum;
            }
            return map;
        }

        static float DistanceToSegmentXZ(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

        static float Sq(float v) => v * v;

        public static float Fbm(float x, float z, int seed)
        {
            float sum = 0f, amp = 1f, norm = 0f, freq = 1f;
            for (int o = 0; o < 5; o++)
            {
                sum += Mathf.PerlinNoise(x * freq + seed * 17.13f + o * 3.7f, z * freq - seed * 9.71f + o * 5.3f) * amp;
                norm += amp;
                amp *= 0.5f;
                freq *= 2.03f;
            }
            return sum / norm;
        }
    }
}
