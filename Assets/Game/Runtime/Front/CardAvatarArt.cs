using System.Collections.Generic;
using NightSignal.Core.Customization;
using UnityEngine;

namespace NightSignal.Front
{
    /// <summary>
    /// The Player Card's avatar emblems (customization.json "card" → "avatars"): nine original compositions drawn from
    /// signed-distance shapes with soft edges — two free, seven challenge rewards, each its own drawing (spec: "avatar icons are
    /// original art … not a generic icon renamed fifteen times"). Static images, so reduced motion needs nothing.
    /// </summary>
    public static class CardAvatarArt
    {
        public const int Size = 128;

        static Color Hex(string hex, Color fallback) => hex != null && ColorUtility.TryParseHtmlString(hex, out Color c) ? c : fallback;

        static float Cov(float sdf) => Mathf.Clamp01(0.5f - sdf);

        static float Circle(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;

        static float Segment(Vector2 p, Vector2 a, Vector2 b, float r)
        {
            Vector2 pa = p - a, ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Mathf.Max(1e-4f, ba.sqrMagnitude));
            return (pa - ba * h).magnitude - r;
        }

        static float Polyline(Vector2 p, IList<Vector2> pts, float r)
        {
            float d = float.MaxValue;
            for (int i = 0; i + 1 < pts.Count; i++) d = Mathf.Min(d, Segment(p, pts[i], pts[i + 1], r));
            return d;
        }

        static float Box(Vector2 p, Vector2 c, Vector2 half, float round = 0f)
        {
            Vector2 q = new Vector2(Mathf.Abs(p.x - c.x), Mathf.Abs(p.y - c.y)) - half + Vector2.one * round;
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - round;
        }

        /// <summary>Signed distance to a convex polygon (counter-clockwise points).</summary>
        static float Convex(Vector2 p, IList<Vector2> poly)
        {
            float d = float.MinValue;
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
                Vector2 n = new Vector2(b.y - a.y, a.x - b.x).normalized;
                d = Mathf.Max(d, Vector2.Dot(p - a, n));
            }
            return d;
        }

        static List<Vector2> Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, int n)
        {
            var pts = new List<Vector2>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n, u = 1f - t;
                pts.Add(u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d);
            }
            return pts;
        }

        static Color Over(Color under, Color c, float cov)
        {
            float k = Mathf.Clamp01(cov * c.a);
            Color o = Color.Lerp(under, c, k);
            o.a = Mathf.Max(under.a, k);
            return o;
        }

        /// <summary>Draws <paramref name="def"/> into <paramref name="px"/> (Size × Size, row 0 at the bottom).</summary>
        public static void Draw(CardAvatarDef def, Color32[] px)
        {
            string art = def?.Art ?? "initial";
            Color c0 = Hex(def != null && def.Colors.Count > 0 ? def.Colors[0] : null, new Color(0.1f, 0.12f, 0.15f));
            Color c1 = Hex(def != null && def.Colors.Count > 1 ? def.Colors[1] : null, new Color(0.24f, 0.78f, 0.85f));
            Color c2 = Hex(def != null && def.Colors.Count > 2 ? def.Colors[2] : null, new Color(0.91f, 0.89f, 0.85f));
            var centre = new Vector2(Size * 0.5f, Size * 0.5f);
            const float R = 60f;

            // Shapes that are not the disc (precomputed geometry).
            List<Vector2> branch = Bezier(new Vector2(10, 34), new Vector2(40, 96), new Vector2(76, 58), new Vector2(118, 96), 28);
            List<Vector2> twig1 = Bezier(new Vector2(46, 72), new Vector2(50, 92), new Vector2(58, 100), new Vector2(64, 108), 10);
            List<Vector2> twig2 = Bezier(new Vector2(84, 74), new Vector2(88, 62), new Vector2(96, 54), new Vector2(104, 48), 10);
            Vector2[] blossoms = { new Vector2(28, 66), new Vector2(64, 108), new Vector2(60, 78), new Vector2(96, 82), new Vector2(104, 48), new Vector2(116, 96) };
            Vector2[] buds = { new Vector2(20, 50), new Vector2(72, 70), new Vector2(54, 98), new Vector2(110, 88), new Vector2(90, 58) };
            List<Vector2> ghost = Bezier(new Vector2(18, 16), new Vector2(30, 92), new Vector2(64, 104), new Vector2(98, 86), 36);
            var hex = new List<Vector2>();
            for (int i = 0; i < 6; i++) hex.Add(centre + new Vector2(Mathf.Cos(i * Mathf.PI / 3f), Mathf.Sin(i * Mathf.PI / 3f)) * 60f);
            Color[] regions = { Hex("#5E8C4A", c1), Hex("#2E6E8E", c1), Hex("#8E4A6E", c1), Hex("#B8862E", c1), Hex("#A83A32", c1), Hex("#4A5E8E", c1) };
            // Shield: a heater shield (flat top, straight sides, a curved point).
            var shieldPts = new List<Vector2> { new Vector2(14, 118), new Vector2(114, 118), new Vector2(114, 62) };
            for (int i = 1; i <= 12; i++)
            {
                float t = i / 12f;
                shieldPts.Add(new Vector2(Mathf.Lerp(114, 64, t), Mathf.Lerp(62, 6, Mathf.Sin(t * Mathf.PI * 0.5f))));
            }
            for (int i = 11; i >= 1; i--)
            {
                float t = i / 12f;
                shieldPts.Add(new Vector2(Mathf.Lerp(14, 64, t), Mathf.Lerp(62, 6, Mathf.Sin(t * Mathf.PI * 0.5f))));
            }
            shieldPts.Add(new Vector2(14, 62));
            var shieldCcw = new List<Vector2>(shieldPts);
            shieldCcw.Reverse();
            var road = new List<Vector2>();
            for (int i = 0; i <= 24; i++)
            {
                float t = i / 24f;
                road.Add(new Vector2(64 + Mathf.Sin(t * Mathf.PI * 2.2f) * 22f * (0.4f + 0.6f * t), Mathf.Lerp(16, 112, t)));
            }
            // 26 road studs round the shield border.
            var studs = new List<Vector2>();
            {
                float perimeter = 0f;
                for (int i = 0; i < shieldPts.Count; i++) perimeter += (shieldPts[(i + 1) % shieldPts.Count] - shieldPts[i]).magnitude;
                for (int k = 0; k < 26; k++)
                {
                    float want = (k + 0.5f) / 26f * perimeter, run = 0f;
                    for (int i = 0; i < shieldPts.Count; i++)
                    {
                        Vector2 a = shieldPts[i], b = shieldPts[(i + 1) % shieldPts.Count];
                        float len = (b - a).magnitude;
                        if (run + len >= want)
                        {
                            Vector2 p = Vector2.Lerp(a, b, (want - run) / len);
                            studs.Add(p + (centre - p).normalized * 6.5f);
                            break;
                        }
                        run += len;
                    }
                }
            }
            var ridgeBack = new List<Vector2> { new Vector2(0, 50), new Vector2(20, 68), new Vector2(42, 52), new Vector2(64, 57), new Vector2(86, 50), new Vector2(108, 68), new Vector2(128, 52) };
            var ridgeFront = new List<Vector2> { new Vector2(0, 30), new Vector2(30, 52), new Vector2(52, 36), new Vector2(80, 48), new Vector2(104, 30), new Vector2(128, 40) };

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    Color o = new Color(0f, 0f, 0f, 0f);
                    float disc = Circle(p, centre, R);
                    switch (art)
                    {
                        case "night-road":
                        {
                            float horizon = 56f;
                            Color sky = Color.Lerp(Color.Lerp(c0, c2, 0.55f), c0, Mathf.Clamp01((p.y - horizon) / 60f));
                            Color ground = c0 * 0.65f;
                            ground.a = 1f;
                            o = Over(o, p.y > horizon ? sky : ground, Cov(disc));
                            float half = Mathf.Lerp(42f, 2f, Mathf.Clamp01(p.y / horizon));
                            float roadSd = Mathf.Max(Mathf.Abs(p.x - 64f) - half, p.y - horizon);
                            o = Over(o, Color.Lerp(c2, c0, 0.45f), Cov(Mathf.Max(roadSd, disc)));
                            // Dashes closer together toward the horizon.
                            float depth = 1f / Mathf.Max(0.05f, 1f - p.y / horizon);
                            bool dash = Mathf.Repeat(depth * 2.2f, 1f) < 0.5f;
                            if (dash && p.y < horizon - 2f) o = Over(o, c1, Cov(Mathf.Max(Mathf.Abs(p.x - 64f) - Mathf.Max(0.6f, half * 0.06f), disc)));
                            foreach (Vector2 st in new[] { new Vector2(30, 100), new Vector2(84, 108), new Vector2(102, 84), new Vector2(50, 116), new Vector2(18, 78) })
                                o = Over(o, c2, Cov(Circle(p, st, 1.3f)));
                            break;
                        }
                        case "cherry-branch":
                        {
                            Color sky = Color.Lerp(c0, Color.Lerp(c0, c2, 0.35f), Mathf.Clamp01(1f - p.y / 128f));
                            o = Over(o, sky, Cov(disc));
                            float t = Mathf.Clamp01((p.x - 10f) / 108f);
                            o = Over(o, c1, Cov(Mathf.Max(Polyline(p, branch, Mathf.Lerp(5f, 1.6f, t)), disc)));
                            o = Over(o, c1, Cov(Mathf.Max(Mathf.Min(Polyline(p, twig1, 1.6f), Polyline(p, twig2, 1.5f)), disc)));
                            foreach (Vector2 bud in buds) o = Over(o, Color.Lerp(c2, c1, 0.35f), Cov(Mathf.Max(Circle(p, bud, 3.2f), disc)));
                            foreach (Vector2 fl in blossoms)
                            {
                                float petals = float.MaxValue;
                                for (int k = 0; k < 5; k++)
                                {
                                    float a = k * Mathf.PI * 2f / 5f + fl.x * 0.1f;
                                    petals = Mathf.Min(petals, Circle(p, fl + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 5.2f, 4.6f));
                                }
                                o = Over(o, c2, Cov(Mathf.Max(petals, disc)));
                                o = Over(o, Color.Lerp(c2, Color.white, 0.55f), Cov(Circle(p, fl, 2.2f)));
                            }
                            o = Over(o, Color.Lerp(c2, c0, 0.2f), Cov(Mathf.Abs(disc + 1.5f) - 1.5f));
                            break;
                        }
                        case "six-region":
                        {
                            float hexSd = Convex(p, hex);
                            float ang = Mathf.Repeat(Mathf.Atan2(p.y - centre.y, p.x - centre.x), Mathf.PI * 2f);
                            int wedge = Mathf.FloorToInt(ang / (Mathf.PI / 3f)) % 6;
                            o = Over(o, regions[wedge], Cov(hexSd));
                            // Spokes, the rim and the centre.
                            float spokes = float.MaxValue;
                            for (int i = 0; i < 6; i++) spokes = Mathf.Min(spokes, Segment(p, centre, hex[i], 1.4f));
                            o = Over(o, c1, Cov(Mathf.Max(spokes, hexSd)));
                            o = Over(o, c1, Cov(Mathf.Abs(hexSd + 2f) - 2f));
                            o = Over(o, c0, Cov(Circle(p, centre, 15f)));
                            o = Over(o, c2, Cov(Circle(p, centre, 11f)));
                            o = Over(o, c1, Cov(Mathf.Abs(Circle(p, centre, 15f)) - 1.2f));
                            break;
                        }
                        case "ghostline":
                        {
                            o = Over(o, c0, Cov(disc));
                            float line = Polyline(p, ghost, 0f);
                            // Faint road edges either side of the line.
                            o = Over(o, new Color(c1.r, c1.g, c1.b, 0.22f), Cov(Mathf.Max(Mathf.Abs(line - 17f) - 1.1f, disc)));
                            // The dashed ghost line: dash by position along the curve.
                            int nearest = 0;
                            float best = float.MaxValue;
                            for (int i = 0; i < ghost.Count; i++)
                            {
                                float dd = (p - ghost[i]).sqrMagnitude;
                                if (dd < best) { best = dd; nearest = i; }
                            }
                            if (nearest % 3 != 2 && nearest < ghost.Count - 3)
                                o = Over(o, new Color(c1.r, c1.g, c1.b, 0.9f), Cov(Mathf.Max(line - 2.6f, disc)));
                            // The ghost car at the end of the line, along its heading.
                            Vector2 end = ghost[ghost.Count - 1], dir = (end - ghost[ghost.Count - 4]).normalized;
                            Vector2 local = new Vector2(Vector2.Dot(p - end, dir), Vector2.Dot(p - end, new Vector2(-dir.y, dir.x)));
                            float car = Box(local, Vector2.zero, new Vector2(13f, 7f), 4f);
                            o = Over(o, new Color(c2.r, c2.g, c2.b, 0.32f), Cov(car));
                            o = Over(o, c2, Cov(Mathf.Abs(car) - 1.2f));
                            o = Over(o, new Color(c2.r, c2.g, c2.b, 0.6f), Cov(Box(local, new Vector2(3f, 0f), new Vector2(3.5f, 4.5f), 1.5f)));
                            o = Over(o, Color.Lerp(c1, c0, 0.3f), Cov(Mathf.Abs(disc + 1.5f) - 1.5f));
                            break;
                        }
                        case "radio-dial":
                        {
                            // Wood case with a grain, a cream dial with ticks, a red needle and a knurled knob.
                            float grain = 0.08f * Mathf.Sin(p.y * 0.55f + Mathf.Sin(p.x * 0.07f) * 3f);
                            Color wood = c0 * (1f + grain);
                            wood.a = 1f;
                            o = Over(o, wood, Cov(disc));
                            var hub = new Vector2(64, 50);
                            float face = Mathf.Max(Circle(p, hub, 50f), hub.y - 4f - p.y);
                            o = Over(o, c1, Cov(Mathf.Max(face, disc)));
                            float a = Mathf.Atan2(p.y - hub.y, p.x - hub.x) * Mathf.Rad2Deg;
                            float r = (p - hub).magnitude;
                            if (a > 8f && a < 172f)
                            {
                                float step = (172f - 8f) / 20f;
                                float k = (a - 8f) / step, frac = Mathf.Abs(k - Mathf.Round(k)) * step * Mathf.Deg2Rad * r;
                                bool major = Mathf.RoundToInt(k) % 5 == 0;
                                float inner = major ? 34f : 39f;
                                o = Over(o, Color.Lerp(c0, Color.black, 0.3f), Cov(Mathf.Max(frac - (major ? 1.3f : 0.8f), Mathf.Max(inner - r, r - 45f))));
                            }
                            o = Over(o, Color.Lerp(c0, Color.black, 0.2f), Cov(Mathf.Abs(Circle(p, hub, 28f)) - 0.7f));
                            float na = 118f * Mathf.Deg2Rad;
                            o = Over(o, c2, Cov(Segment(p, hub, hub + new Vector2(Mathf.Cos(na), Mathf.Sin(na)) * 46f, 1.6f)));
                            var knob = new Vector2(64, 26);
                            float teeth = 1.2f * Mathf.Sign(Mathf.Sin(Mathf.Atan2(p.y - knob.y, p.x - knob.x) * 14f));
                            o = Over(o, new Color(0.28f, 0.27f, 0.26f), Cov(Circle(p, knob, 14f + teeth)));
                            o = Over(o, new Color(0.45f, 0.44f, 0.42f), Cov(Circle(p, knob, 9f)));
                            o = Over(o, Color.Lerp(c1, c0, 0.5f), Cov(Mathf.Abs(disc + 2f) - 2f));
                            break;
                        }
                        case "road-crest":
                        {
                            float shield = Convex(p, shieldCcw);
                            o = Over(o, c1, Cov(shield));
                            o = Over(o, c0, Cov(shield + 7f));
                            float roadSd = Polyline(p, road, 6.5f);
                            o = Over(o, c2, Cov(Mathf.Max(roadSd, shield + 7f)));
                            int nearest = 0;
                            float best = float.MaxValue;
                            for (int i = 0; i < road.Count; i++)
                            {
                                float dd = (p - road[i]).sqrMagnitude;
                                if (dd < best) { best = dd; nearest = i; }
                            }
                            if (nearest % 2 == 0) o = Over(o, c0, Cov(Mathf.Max(Polyline(p, road, 1.1f), shield + 7f)));
                            foreach (Vector2 st in studs) o = Over(o, c2, Cov(Circle(p, st, 2.2f)));
                            break;
                        }
                        case "mosaic":
                        {
                            // Twelve tiles (4 × 3), each its own tone and its own short waveform — twelve voices.
                            float plate = Box(p, centre, new Vector2(56f, 56f), 10f);
                            o = Over(o, c0, Cov(plate));
                            const float gap = 3f, x0 = 12f, y0 = 12f, w = (104f - 5f * gap) / 4f, h = (104f - 4f * gap) / 3f;
                            for (int i = 0; i < 12; i++)
                            {
                                int col = i % 4, row = i / 4;
                                var tc = new Vector2(x0 + gap + col * (w + gap) + w * 0.5f, y0 + gap + row * (h + gap) + h * 0.5f);
                                float tile = Box(p, tc, new Vector2(w * 0.5f, h * 0.5f), 2f);
                                if (tile > 1f) continue;
                                float shade = 0.72f + 0.28f * ((i * 7) % 4) / 3f;
                                Color tone = Color.Lerp(c1, c2, (i % 6) / 5f) * shade;
                                tone.a = 1f;
                                o = Over(o, tone, Cov(tile));
                                float freq = 0.18f + 0.05f * (i % 5), amp = 3f + 2f * ((i * 5) % 3);
                                float wave = Mathf.Abs(p.y - (tc.y + Mathf.Sin((p.x - tc.x) * freq + i) * amp)) - 1.1f;
                                o = Over(o, new Color(1f, 1f, 1f, 0.75f), Cov(Mathf.Max(wave, tile + 2.5f)));
                            }
                            break;
                        }
                        case "dawn-horizon":
                        {
                            float horizon = 48f;
                            // Banded sky: five bands from night to the warm horizon.
                            float k = Mathf.Clamp01((p.y - horizon) / 72f);
                            float band = Mathf.Floor((1f - k) * 5f) / 4f;
                            Color sky = Color.Lerp(c0, c2, Mathf.Clamp01(band));
                            o = Over(o, sky, Cov(disc));
                            var sun = new Vector2(64, horizon + 8f);
                            float rays = Mathf.Abs(Mathf.Sin(Mathf.Atan2(p.y - sun.y, p.x - sun.x) * 9f));
                            if (p.y > horizon && (p - sun).magnitude > 30f && rays < 0.12f)
                                o = Over(o, new Color(c2.r, c2.g, c2.b, 0.55f), Cov(Mathf.Max(disc, (p - sun).magnitude - 56f)));
                            o = Over(o, Color.Lerp(c1, c2, Mathf.Clamp01((p.y - horizon) / 26f)), Cov(Mathf.Max(Circle(p, sun, 26f), disc)));
                            float back = p.y - Interp(ridgeBack, p.x);
                            o = Over(o, Color.Lerp(c0, c1, 0.28f), Cov(Mathf.Max(back, disc)));
                            float front = p.y - Interp(ridgeFront, p.x);
                            Color dark = c0 * 0.6f;
                            dark.a = 1f;
                            o = Over(o, dark, Cov(Mathf.Max(front, disc)));
                            break;
                        }
                        case "stopwatch":
                        {
                            // The timing crew's mark (story portraits): a stopwatch face, a crown and a sweep hand.
                            o = Over(o, c0, Cov(disc));
                            var face = new Vector2(64, 58);
                            o = Over(o, c1, Cov(Circle(p, face, 40f)));
                            o = Over(o, c2, Cov(Circle(p, face, 35f)));
                            o = Over(o, c1, Cov(Box(p, new Vector2(64, 104), new Vector2(7f, 6f), 2f)));
                            o = Over(o, c1, Cov(Box(p, new Vector2(64, 112), new Vector2(11f, 3f), 1.5f)));
                            float ta = Mathf.Atan2(p.y - face.y, p.x - face.x), tr = (p - face).magnitude;
                            float tick = Mathf.Abs(Mathf.Repeat(ta / (Mathf.PI * 2f) * 12f + 0.5f, 1f) - 0.5f) * Mathf.PI * 2f / 12f * tr;
                            o = Over(o, c0, Cov(Mathf.Max(tick - 1.1f, Mathf.Max(27f - tr, tr - 33f))));
                            float hand = 62f * Mathf.Deg2Rad;
                            o = Over(o, Hex("#D7263D", c1), Cov(Segment(p, face, face + new Vector2(Mathf.Cos(hand), Mathf.Sin(hand)) * 30f, 1.6f)));
                            o = Over(o, c0, Cov(Circle(p, face, 3.5f)));
                            break;
                        }
                        default: // initial: a ringed disc; the letter is text laid over it
                            o = Over(o, c0, Cov(disc));
                            o = Over(o, c1, Cov(Mathf.Abs(Circle(p, centre, R - 4f)) - 2.2f));
                            break;
                    }
                    px[y * Size + x] = o;
                }
            }
        }

        /// <summary>The height of a polyline (sorted by x) at <paramref name="x"/>.</summary>
        static float Interp(List<Vector2> pts, float x)
        {
            for (int i = 0; i + 1 < pts.Count; i++)
                if (x <= pts[i + 1].x) return Mathf.Lerp(pts[i].y, pts[i + 1].y, Mathf.InverseLerp(pts[i].x, pts[i + 1].x, x));
            return pts[pts.Count - 1].y;
        }
    }
}
