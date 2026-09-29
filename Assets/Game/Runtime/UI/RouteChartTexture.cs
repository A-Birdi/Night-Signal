using System;
using NightSignal.Core.Ghosts;
using UnityEngine;

namespace NightSignal.UI
{
    /// <summary>
    /// Draws a <see cref="RouteChart"/> into a texture for the results page: the route in plan on top, each sector coloured
    /// by the time it lost (red) or gained (cyan) against the reference (grey without one), braking points as white
    /// dots; the elevation profile along the distance below, with sector boundaries and braking ticks.
    /// </summary>
    public static class RouteChartTexture
    {
        static readonly Color32 Background = new Color32(18, 20, 23, 255), Grid = new Color32(46, 50, 56, 255), Neutral = new Color32(190, 190, 186, 255);
        static readonly Color32 Lost = new Color32(215, 38, 61, 255), Gained = new Color32(62, 198, 216, 255), Brake = new Color32(255, 255, 255, 255);
        static readonly Color32 Profile = new Color32(242, 165, 65, 255), ProfileFill = new Color32(60, 48, 30, 255);

        public static Texture2D Draw(RouteChart c, int width = 1100, int height = 620)
        {
            var px = new Color32[width * height];
            for (int i = 0; i < px.Length; i++) px[i] = Background;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "RouteChart" };
            if (c == null || c.Metres.Count < 2)
            {
                tex.SetPixels32(px);
                tex.Apply();
                return tex;
            }
            // Layout (texture y grows upwards): the map above, the profile below.
            int pad = 24, profileH = (int)(height * 0.28f), mapTop = height - pad, mapBottom = profileH + pad * 2, profileTop = profileH + pad / 2, profileBottom = pad / 2;
            float spanX = Math.Max(1f, c.MaxX - c.MinX), spanZ = Math.Max(1f, c.MaxZ - c.MinZ);
            float scale = Math.Min((width - pad * 2) / spanX, (mapTop - mapBottom) / spanZ);
            float offX = (width - spanX * scale) / 2f, offY = mapBottom + ((mapTop - mapBottom) - spanZ * scale) / 2f;
            Vector2 Map(int i) => new Vector2(offX + (c.X[i] - c.MinX) * scale, offY + (c.Z[i] - c.MinZ) * scale);
            float elevSpan = Math.Max(5f, c.MaxElevation - c.MinElevation);
            Vector2 Prof(int i) => new Vector2(pad + (c.Metres[i] / Math.Max(1f, c.LengthMetres)) * (width - pad * 2),
                profileBottom + (c.Elevation[i] - c.MinElevation) / elevSpan * (profileTop - profileBottom));

            long worst = 1;
            foreach (RouteChartSector s in c.Sectors) if (s.DeltaMicros != null) worst = Math.Max(worst, Math.Abs(s.DeltaMicros.Value));
            // Profile: filled area, then the line; sector boundaries as faint verticals.
            for (int i = 0; i + 1 < c.Metres.Count; i++)
            {
                Vector2 a = Prof(i), b = Prof(i + 1);
                for (int x = (int)a.x; x <= (int)b.x; x++)
                {
                    float t = b.x > a.x ? (x - a.x) / (b.x - a.x) : 0f;
                    int top = (int)Mathf.Lerp(a.y, b.y, t);
                    for (int y = profileBottom; y < top; y++) Set(px, width, height, x, y, ProfileFill);
                }
            }
            foreach (RouteChartSector s in c.Sectors)
            {
                int x = (int)Prof(s.ToSample).x;
                for (int y = profileBottom; y <= profileTop; y++) Set(px, width, height, x, y, Grid);
            }
            for (int i = 0; i + 1 < c.Metres.Count; i++) Line(px, width, height, Prof(i), Prof(i + 1), Profile, 2);
            // Map: sectors coloured by time lost/gained.
            foreach (RouteChartSector s in c.Sectors)
            {
                Color32 col = Neutral;
                if (s.DeltaMicros != null)
                {
                    float k = 0.35f + 0.65f * Mathf.Clamp01(Math.Abs(s.DeltaMicros.Value) / (float)worst);
                    col = Color32.Lerp(Neutral, s.DeltaMicros.Value > 0 ? Lost : Gained, k);
                }
                for (int i = s.FromSample; i < s.ToSample && i + 1 < c.Metres.Count; i++) Line(px, width, height, Map(i), Map(i + 1), col, 4);
            }
            foreach (int i in c.BrakingPoints)
            {
                Dot(px, width, height, Map(i), 6, Brake);
                Vector2 p = Prof(i);
                for (int y = (int)p.y - 8; y <= (int)p.y + 8; y++) Set(px, width, height, (int)p.x, y, Brake);
            }
            Dot(px, width, height, Map(0), 8, Gained);                    // start
            Dot(px, width, height, Map(c.Metres.Count - 1), 8, Lost);    // finish
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        static void Set(Color32[] px, int w, int h, int x, int y, Color32 c)
        {
            if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c;
        }

        static void Dot(Color32[] px, int w, int h, Vector2 p, int r, Color32 c)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r) Set(px, w, h, (int)p.x + dx, (int)p.y + dy, c);
        }

        static void Line(Color32[] px, int w, int h, Vector2 a, Vector2 b, Color32 c, int thickness)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b)));
            int half = thickness / 2;
            for (int s = 0; s <= steps; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, s / (float)steps);
                for (int dy = -half; dy <= half; dy++)
                    for (int dx = -half; dx <= half; dx++)
                        Set(px, w, h, (int)p.x + dx, (int)p.y + dy, c);
            }
        }
    }
}
