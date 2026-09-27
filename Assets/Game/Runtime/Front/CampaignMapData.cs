using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace NightSignal.Front
{
    /// <summary>Authored layout of the fictional Shiranami region map (Resources/UI/CampaignMap.json).</summary>
    public sealed class CampaignMapData
    {
        public sealed class Region
        {
            public string Id, Name;
            public int Act;
            public float[] Centre = new float[2];
            public float Radius;
        }

        public sealed class Node
        {
            public string Stage;
            public float[] Pos = new float[2];
        }

        public sealed class TutorialNode
        {
            public string Id;
            public float[] Pos = new float[2];
        }

        public sealed class Lake
        {
            public string Id;
            public float[] Centre = new float[2];
            public float Radius;
        }

        public string Schema;
        public float[] Size = { 1000, 600 };
        public List<float[]> Coast = new List<float[]>();
        public List<Lake> Lakes = new List<Lake>();
        public List<Region> Regions = new List<Region>();
        public TutorialNode Tutorial;
        public List<Node> Nodes = new List<Node>();

        public static CampaignMapData Load()
        {
            var text = Resources.Load<TextAsset>("UI/CampaignMap");
            return text == null ? null : JsonConvert.DeserializeObject<CampaignMapData>(text.text);
        }

        public Vector2 PosOf(string stageId)
        {
            foreach (Node n in Nodes) if (n.Stage == stageId) return new Vector2(n.Pos[0], n.Pos[1]);
            if (Tutorial != null && Tutorial.Id == stageId) return new Vector2(Tutorial.Pos[0], Tutorial.Pos[1]);
            return Vector2.zero;
        }

        /// <summary>The act whose region is nearest (relative to its radius) — used to veil areas not yet reached.</summary>
        public int ActAt(float x, float y)
        {
            int act = 4;
            float best = float.MaxValue;
            foreach (Region r in Regions)
            {
                float dx = x - r.Centre[0], dy = y - r.Centre[1];
                float d = (float)Math.Sqrt(dx * dx + dy * dy) / Math.Max(1f, r.Radius);
                if (d < best) { best = d; act = r.Act; }
            }
            return act;
        }
    }

    /// <summary>
    /// Paints the region map: sea with a lit coastline, hill-shaded relief rising to the northern highlands, contour
    /// lines, soft region washes and a road network (minimum spanning tree over the stage towns, gently meandering).
    /// Acts not reached yet are veiled but still hinted (Addendum 01 §4.1: the map grows with the campaign).
    /// Pure C# with its own noise, so <see cref="PaintPixels"/> runs on a worker thread; deterministic output.
    /// </summary>
    public static class CampaignMapPainter
    {
        public static Texture2D Paint(CampaignMapData map, int revealedAct, int width = 1600)
        {
            int height = HeightFor(map, width);
            return ToTexture(PaintPixels(map, revealedAct, width), width, height);
        }

        public static int HeightFor(CampaignMapData map, int width) => (int)Math.Round(width * map.Size[1] / map.Size[0]);

        public static Texture2D ToTexture(Color32[] px, int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, true)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, name = "CampaignMap" };
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        static readonly Color SeaDeep = new Color(0.020f, 0.045f, 0.075f), SeaShallow = new Color(0.035f, 0.085f, 0.12f);
        static readonly Color Coastline = new Color(0.30f, 0.62f, 0.70f);
        static readonly Color LandLow = new Color(0.075f, 0.085f, 0.095f), LandHigh = new Color(0.20f, 0.205f, 0.215f);
        static readonly Color Contour = new Color(0.36f, 0.35f, 0.32f), Road = new Color(0.78f, 0.70f, 0.55f);

        public static Color32[] PaintPixels(CampaignMapData map, int revealedAct, int width)
        {
            int height = HeightFor(map, width);
            var px = new Color32[width * height];
            float sx = map.Size[0] / width, sy = map.Size[1] / height;
            var coast = new List<Vector2>();
            foreach (float[] c in map.Coast) coast.Add(new Vector2(c[0], c[1]));
            Vector3 light = new Vector3(-0.6f, 0.7f, 0.9f).normalized; // from the north-west, like a printed relief map

            // Pass 1: relief once per pixel; gradients come from neighbouring samples.
            var hf = new float[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                hf[y * width + x] = HeightAt((x + 0.5f) * sx, (y + 0.5f) * sy, map);

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float mx = (x + 0.5f) * sx, my = (y + 0.5f) * sy;
                var p = new Vector2(mx, my);
                // Shore distance: positive on land, negative in water (sea polygon or reservoir).
                float shore = 999f;
                if (coast.Count > 2)
                {
                    float d = DistanceToPolyline(coast, p);
                    shore = Inside(coast, p) ? -d : d;
                }
                foreach (CampaignMapData.Lake lake in map.Lakes)
                {
                    float lx = mx - lake.Centre[0], ly = my - lake.Centre[1];
                    float wobble = (Fbm(mx * 0.05f + 5f, my * 0.05f + 9f, 2) - 0.5f) * lake.Radius * 0.5f;
                    float d = (float)Math.Sqrt(lx * lx + ly * ly) - (lake.Radius + wobble);
                    if (Math.Abs(d) < Math.Abs(shore) || d < 0f) shore = d;
                }
                Color c;
                if (shore < 0f)
                {
                    float edge = -shore;
                    float depth = Mathf.Clamp01(edge / 60f);
                    c = Color.Lerp(SeaShallow, SeaDeep, depth);
                    // Faint bathymetric rings parallel to the shore.
                    float ring = Math.Abs(Frac(edge / 14f) - 0.5f);
                    if (edge > 6f && ring < 0.035f) c = Color.Lerp(c, Coastline, 0.10f * (1f - depth));
                    if (edge < 1.6f) c = Color.Lerp(c, Coastline, 0.45f * (1f - edge / 1.6f));
                }
                else
                {
                    int i = y * width + x;
                    float h = hf[i];
                    float dx = (hf[y * width + Math.Min(width - 1, x + 1)] - hf[y * width + Math.Max(0, x - 1)]) / (2 * sx);
                    float dy = (hf[Math.Min(height - 1, y + 1) * width + x] - hf[Math.Max(0, y - 1) * width + x]) / (2 * sy);
                    Vector3 n = new Vector3(-dx * 220f, -dy * 220f, 1f).normalized;
                    float lambert = Mathf.Clamp01(Vector3.Dot(n, light));
                    c = Color.Lerp(LandLow, LandHigh, h);
                    c *= 0.55f + 0.75f * lambert;
                    // Contours every 1/12 of relief, a constant ~1 px wide (distance divided by the local gradient).
                    float k = 12f, grad = (float)Math.Sqrt(dx * dx + dy * dy) * k * sx;
                    float band = Math.Abs(Frac(h * k) - 0.5f) * 2f; // 1 on a contour, 0 midway between two
                    float dist = (1f - band) * 0.5f / Math.Max(grad, 1e-4f);
                    if (dist < 0.9f) c = Color.Lerp(c, Contour, 0.30f * (1f - dist / 0.9f));
                    foreach (CampaignMapData.Region r in map.Regions)
                    {
                        float d = Vector2.Distance(p, new Vector2(r.Centre[0], r.Centre[1]));
                        if (d < r.Radius) c = Color.Lerp(c, RegionTint(r.Id), 0.10f * Smooth(1f - d / r.Radius));
                    }
                    if (shore < 2.2f) c = Color.Lerp(c, Coastline, 0.65f * (1f - shore / 2.2f));
                }
                c.a = 1f;
                px[y * width + x] = c;
            }

            DrawRoads(px, width, height, sx, sy, map);

            // Veil the acts not reached yet: desaturated and dimmed with drifting cloud so the land still reads. The
            // veil blends smoothly between regions (Gaussian weights), so there are no hard act borders.
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float mx = (x + 0.5f) * sx, my = (y + 0.5f) * sy;
                float sum = 0f, weight = 0f;
                foreach (CampaignMapData.Region r in map.Regions)
                {
                    float rx = (mx - r.Centre[0]) / r.Radius, ry = (my - r.Centre[1]) / r.Radius;
                    float w = (float)Math.Exp(-(rx * rx + ry * ry) * 1.6f) + 1e-30f;
                    sum += w * (r.Act <= revealedAct ? 0f : r.Act == revealedAct + 1 ? 0.62f : 0.84f);
                    weight += w;
                }
                float strength = sum / weight;
                if (strength < 0.01f) continue;
                Color c = px[y * width + x];
                float g = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                float cloud = Fbm(mx * 0.012f + 40f, my * 0.012f - 13f, 3);
                Color veiled = new Color(g, g, g, 1f) * (0.45f + 0.25f * cloud);
                veiled.a = 1f;
                px[y * width + x] = Color.Lerp(c, veiled, strength);
            }
            return px;
        }

        static void DrawRoads(Color32[] px, int w, int h, float sx, float sy, CampaignMapData map)
        {
            var towns = new List<Vector2>();
            if (map.Tutorial != null) towns.Add(new Vector2(map.Tutorial.Pos[0], map.Tutorial.Pos[1]));
            foreach (CampaignMapData.Node n in map.Nodes) towns.Add(new Vector2(n.Pos[0], n.Pos[1]));
            // Prim's minimum spanning tree: a plausible road network that joins every stage town.
            int count = towns.Count;
            if (count < 2) return;
            var inTree = new bool[count];
            var best = new float[count];
            var from = new int[count];
            for (int i = 0; i < count; i++) { best[i] = float.MaxValue; from[i] = -1; }
            best[0] = 0f;
            for (int iter = 0; iter < count; iter++)
            {
                int u = -1;
                for (int i = 0; i < count; i++) if (!inTree[i] && (u < 0 || best[i] < best[u])) u = i;
                inTree[u] = true;
                if (from[u] >= 0) Meander(px, w, h, sx, sy, towns[from[u]], towns[u], u);
                for (int v = 0; v < count; v++)
                {
                    if (inTree[v]) continue;
                    float d = Vector2.Distance(towns[u], towns[v]);
                    if (d < best[v]) { best[v] = d; from[v] = u; }
                }
            }
        }

        static void Meander(Color32[] px, int w, int h, float sx, float sy, Vector2 a, Vector2 b, int seed)
        {
            Vector2 dir = b - a;
            float len = dir.magnitude;
            if (len < 1e-3f) return;
            Vector2 side = new Vector2(-dir.y, dir.x) / len;
            int steps = Math.Max(8, (int)(len / Math.Min(sx, sy)));
            Vector2 prev = a;
            for (int s = 1; s <= steps; s++)
            {
                float t = s / (float)steps;
                float bend = (float)Math.Sin(t * Math.PI) * (Fbm(t * 3f + seed * 1.7f, seed * 0.37f, 2) - 0.5f) * len * 0.22f;
                Vector2 q = Vector2.Lerp(a, b, t) + side * bend;
                Stroke(px, w, h, sx, sy, prev, q, Road, 1.1f, 0.85f);
                prev = q;
            }
        }

        /// <summary>Anti-aliased line segment in map units; radius in pixels.</summary>
        static void Stroke(Color32[] px, int w, int h, float sx, float sy, Vector2 a, Vector2 b, Color colour, float radius, float opacity)
        {
            Vector2 pa = new Vector2(a.x / sx, a.y / sy), pb = new Vector2(b.x / sx, b.y / sy);
            int x0 = (int)Math.Floor(Math.Min(pa.x, pb.x) - radius - 1), x1 = (int)Math.Ceiling(Math.Max(pa.x, pb.x) + radius + 1);
            int y0 = (int)Math.Floor(Math.Min(pa.y, pb.y) - radius - 1), y1 = (int)Math.Ceiling(Math.Max(pa.y, pb.y) + radius + 1);
            for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
            {
                float d = DistanceToSegment(new Vector2(x + 0.5f, y + 0.5f), pa, pb);
                float cover = Mathf.Clamp01(radius + 0.5f - d);
                if (cover <= 0f) continue;
                Color c = px[y * w + x];
                Color o = Color.Lerp(c, colour, cover * opacity);
                o.a = 1f;
                px[y * w + x] = o;
            }
        }

        static Color RegionTint(string id)
        {
            switch (id)
            {
                case "hinode": return new Color(0.55f, 0.6f, 0.75f);
                case "mizuhana": return new Color(0.35f, 0.6f, 0.3f);
                case "kasumi": return new Color(0.25f, 0.5f, 0.38f);
                case "kurogawa": return new Color(0.25f, 0.42f, 0.6f);
                case "akebono": return new Color(0.75f, 0.55f, 0.32f);
                case "hoshimi": return new Color(0.55f, 0.45f, 0.65f);
                case "tsukishiro": return new Color(0.62f, 0.68f, 0.75f);
                case "amanagi": return new Color(0.85f, 0.35f, 0.3f);
                default: return new Color(0.5f, 0.5f, 0.5f);
            }
        }

        /// <summary>Relief in 0..1: rises toward the northern highlands, a basin at Kurogawa Reservoir, low near the coast.</summary>
        static float HeightAt(float x, float y, CampaignMapData map)
        {
            float n = Fbm(x * 0.006f + 3.1f, y * 0.006f + 7.7f, 5);
            float north = y / map.Size[1];
            float rx = x - 555f, ry = y - 270f;
            float reservoir = Smooth(Mathf.Clamp01(1f - (float)Math.Sqrt(rx * rx + ry * ry) / 85f));
            float ridge = 1f - Math.Abs(Fbm(x * 0.004f - 11f, y * 0.004f + 5f, 3) * 2f - 1f); // soft ridgelines
            return Mathf.Clamp01(0.08f + n * 0.45f + ridge * ridge * 0.22f * north + north * north * 0.5f - reservoir * 0.35f);
        }

        // ---- deterministic value noise (thread-safe; Mathf.PerlinNoise is not guaranteed off the main thread) ----
        static float Fbm(float x, float y, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Noise(x, y);
                norm += amp;
                x = x * 2.03f + 17.1f; y = y * 2.03f - 9.3f;
                amp *= 0.5f;
            }
            return sum / norm;
        }

        static float Noise(float x, float y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            float ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
            float a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
            return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
        }

        static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)y * 668265263u;
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
            }
        }

        static float Frac(float v) => v - (float)Math.Floor(v);
        static float Smooth(float t) => t * t * (3f - 2f * t);

        static bool Inside(List<Vector2> poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }

        /// <summary>Distance to the coast polygon's shoreline edges (edges along the map border are not shore).</summary>
        static float DistanceToPolyline(List<Vector2> poly, Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 a = poly[j], b = poly[i];
                bool border = (a.x == b.x && (a.x <= 0 || a.x >= 1000)) || (a.y == b.y && (a.y <= 0 || a.y >= 600));
                if (border) continue;
                best = Math.Min(best, DistanceToSegment(p, a, b));
            }
            return best;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
