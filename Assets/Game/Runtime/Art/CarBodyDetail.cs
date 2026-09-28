using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightSignal.Art
{
    /// <summary>
    /// The detail layer of the generated car body (art pass): everything that sits on or in the lofted shell. Lamp clusters,
    /// grille openings and bumper intakes are drawn in front/rear elevation and projected onto the body surface, so they
    /// follow the rounded nose, the hood slope and the corners instead of floating as boxes. Arch lips and wheel-well liners
    /// follow the authored arch shape; mirrors stand on stalks at the A-pillar base; shut lines, handles, the interior
    /// silhouette behind the glass, exhaust tips, rear aero and the body-kit families complete it.
    /// </summary>
    public static partial class CarBodyGenerator
    {
        // ---------------------------------------------------------------- surface queries

        static readonly Vector2[] queryRing = new Vector2[HalfRingPoints];

        /// <summary>Whether (x, y) lies inside the lower body's cross-section at station z.</summary>
        static bool InsideSection(Profile pr, float z, float x, float y)
        {
            HalfRing(pr, z, queryRing);
            x = Mathf.Abs(x) + 1e-4f;
            bool inside = false;
            for (int i = 0, j = HalfRingPoints - 1; i < HalfRingPoints; j = i++)
            {
                Vector2 a = queryRing[i], b = queryRing[j];
                if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>First lower-body surface met travelling from the nose (or tail) plane toward the car at (x, y).</summary>
        static bool EndHit(Profile pr, bool front, float x, float y, out float z)
        {
            float z0 = front ? pr.Zf + 0.002f : pr.Zr - 0.002f, dir = front ? -1f : 1f;
            const float step = 0.008f, depth = 1.2f;
            float prev = z0;
            for (float t = 0f; t <= depth; t += step)
            {
                float zz = z0 + dir * t;
                if (InsideSection(pr, zz, x, y))
                {
                    float outside = prev, inside = zz;
                    for (int k = 0; k < 7; k++)
                    {
                        float m = (outside + inside) * 0.5f;
                        if (InsideSection(pr, m, x, y)) inside = m;
                        else outside = m;
                    }
                    z = (outside + inside) * 0.5f;
                    return true;
                }
                prev = zz;
            }
            z = front ? pr.Zf : pr.Zr;
            return false;
        }

        /// <summary>The body surface seen from the front (or rear) at elevation point (x, y), lifted along its normal.</summary>
        static Vector3 EndPoint(Profile pr, bool front, float x, float y, float lift)
        {
            EndHit(pr, front, x, y, out float z);
            const float e = 0.006f;
            EndHit(pr, front, x + e, y, out float zx);
            EndHit(pr, front, x, y + e, out float zy);
            float dzdx = (zx - z) / e, dzdy = (zy - z) / e;
            Vector3 n = front ? new Vector3(-dzdx, -dzdy, 1f).normalized : new Vector3(dzdx, dzdy, -1f).normalized;
            return new Vector3(x, y, z) + n * lift;
        }

        /// <summary>The body's lower edge at station z ignoring the wheel arches (the sill, lifted toward the ends).</summary>
        static float SillY(Profile pr, float z) =>
            pr.D.Clearance + 0.1f * Mathf.Max(Mathf.InverseLerp(pr.Zf - 0.35f, pr.Zf, z), Mathf.InverseLerp(pr.Zr + 0.3f, pr.Zr, z));

        /// <summary>x of the outer side of the lower body at station z and height y (sill to shoulder).</summary>
        static float SideXAt(Profile pr, float z, float y)
        {
            HalfRing(pr, z, queryRing);
            for (int k = 1; k < 7; k++)
            {
                Vector2 a = queryRing[k], b = queryRing[k + 1];
                if ((y >= a.y && y <= b.y) || (y <= a.y && y >= b.y))
                    return Mathf.Lerp(a.x, b.x, Mathf.Abs(b.y - a.y) < 1e-5f ? 0f : (y - a.y) / (b.y - a.y));
            }
            return y < queryRing[1].y ? queryRing[1].x : queryRing[7].x;
        }

        /// <summary>Height of the lower body's top skin (hood, deck) at (x, z).</summary>
        static float TopYAt(Profile pr, float z, float x)
        {
            HalfRing(pr, z, queryRing);
            x = Mathf.Abs(x);
            for (int k = 10; k > 7; k--)
            {
                Vector2 a = queryRing[k], b = queryRing[k - 1];
                if (x >= a.x && x <= b.x) return Mathf.Lerp(a.y, b.y, (x - a.x) / Mathf.Max(1e-5f, b.x - a.x));
            }
            return queryRing[7].y;
        }

        // ---------------------------------------------------------------- patch primitives

        /// <summary>u-range (0..1) of a shape's row at v (0 = bottom, 1 = top).</summary>
        delegate Vector2 Span(float v);

        static readonly Span Full = v => new Vector2(0f, 1f);

        static Span Rounded(float hx, float hy, float radius)
        {
            float r = Mathf.Min(radius, Mathf.Min(hx, hy));
            return v =>
            {
                float y = Mathf.Abs(v * 2f - 1f) * hy, dy = y - (hy - r);
                float c = dy > 0f ? r - Mathf.Sqrt(Mathf.Max(0f, r * r - dy * dy)) : 0f;
                float u = c / (2f * hx);
                return new Vector2(u, 1f - u);
            };
        }

        static readonly Span Ellipse = v =>
        {
            float t = v * 2f - 1f, u = 0.5f * Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
            return new Vector2(0.5f - u, 0.5f + u);
        };

        /// <summary>Full width along the top, narrowing to the outer bottom corner (u = 1 is the outer side).</summary>
        static readonly Span Triangle = v => new Vector2(Mathf.Lerp(0.92f, 0f, v), 1f);

        /// <summary>Full along the top, the inner end cut back toward the bottom.</summary>
        static readonly Span Wedge = v => new Vector2(Mathf.Lerp(0.45f, 0f, v), Mathf.Lerp(0.96f, 1f, v));

        /// <summary>
        /// A grid patch conformed to the body: rows along v, columns along u inside <paramref name="span"/>, positions from
        /// <paramref name="map"/> (u, v → model space). Wound to face <paramref name="facing"/>.
        /// </summary>
        static void Patch(MeshBuilder mb, int sub, int rows, int cols, Func<float, float, Vector3> map, Vector3 facing, Span span = null)
        {
            span = span ?? Full;
            var index = new int[(rows + 1) * (cols + 1)];
            for (int r = 0; r <= rows; r++)
            {
                float v = r / (float)rows;
                Vector2 s = span(v);
                for (int c = 0; c <= cols; c++)
                {
                    float u = Mathf.Lerp(s.x, s.y, c / (float)cols);
                    index[r * (cols + 1) + c] = mb.AddVertex(map(u, v), facing, new Vector2(u, v));
                }
            }
            Vector3 du = map(0.55f, 0.5f) - map(0.45f, 0.5f), dv = map(0.5f, 0.55f) - map(0.5f, 0.45f);
            bool forward = Vector3.Dot(Vector3.Cross(du, dv), facing) >= 0f;
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int a = index[r * (cols + 1) + c], b = a + 1, d = index[(r + 1) * (cols + 1) + c], e = d + 1;
                if (forward) mb.AddQuad(sub, a, b, e, d);
                else mb.AddQuad(sub, a, d, e, b);
            }
        }

        /// <summary>
        /// A shape drawn in front (or rear) elevation — centre, half size, <paramref name="span"/> — projected onto the nose or
        /// tail and lifted off it. <paramref name="side"/> −1 mirrors u so a shape's "outer" end (u = 1) is always outboard.
        /// </summary>
        static void EndShape(MeshBuilder mb, Profile pr, bool front, int sub, float cx, float cy, float hx, float hy, float lift, Span span = null,
            int side = 1, int rows = 6, int cols = 10)
        {
            Vector3 map(float u, float v)
            {
                float x = cx + (side >= 0 ? Mathf.Lerp(-hx, hx, u) : Mathf.Lerp(hx, -hx, u));
                return EndPoint(pr, front, x, cy + Mathf.Lerp(-hy, hy, v), lift);
            }
            Patch(mb, sub, rows, cols, map, front ? Vector3.forward : Vector3.back, span);
        }

        /// <summary>A thin strip on the body side at constant station z from y0 up to y1 (a shut line).</summary>
        static void SideLine(MeshBuilder mb, Profile pr, int s, float z, float y0, float y1, float width, float lift = 0.0015f)
        {
            Patch(mb, TrimSub, 6, 1, (u, v) =>
            {
                float y = Mathf.Lerp(y0, y1, v), zz = z + (u - 0.5f) * width;
                return new Vector3(s * (SideXAt(pr, zz, y) + lift), y, zz);
            }, new Vector3(s, 0f, 0f));
        }

        /// <summary>A thin strip on the top skin along a polyline of (x, z) points (hood and deck shut lines).</summary>
        static void DeckLine(MeshBuilder mb, Profile pr, IList<Vector2> path, float width, float lift = 0.0015f)
        {
            int segs = path.Count - 1;
            Patch(mb, TrimSub, segs, 1, (u, v) =>
            {
                float f = v * segs;
                int i = Mathf.Min(segs - 1, Mathf.FloorToInt(f));
                Vector2 a = path[i], b = path[i + 1], p = Vector2.Lerp(a, b, f - i);
                Vector2 dir = (b - a).normalized, across = new Vector2(-dir.y, dir.x) * ((u - 0.5f) * width);
                Vector2 q = p + across;
                return new Vector3(q.x, TopYAt(pr, q.y, q.x) + lift, q.y);
            }, Vector3.up);
        }

        /// <summary>
        /// A soft block — a box with chamfered edges (smooth-shaded once normals are recalculated): seats, headrests, the dash
        /// top, mirror housings.
        /// </summary>
        static void Pod(MeshBuilder mb, int sub, Vector3 centre, Vector3 half, float bevel, Quaternion rot)
        {
            float b = Mathf.Min(bevel, Mathf.Min(half.x, Mathf.Min(half.y, half.z)) * 0.9f);
            // Octagonal section in y–z, extruded along x with chamfered ends.
            var sec = new[]
            {
                new Vector2(-half.y + b, -half.z), new Vector2(half.y - b, -half.z), new Vector2(half.y, -half.z + b), new Vector2(half.y, half.z - b),
                new Vector2(half.y - b, half.z), new Vector2(-half.y + b, half.z), new Vector2(-half.y, half.z - b), new Vector2(-half.y, -half.z + b),
            };
            float[] xs = { -half.x, -half.x + b, half.x - b, half.x };
            float[] scale = { 1f - b / Mathf.Max(half.y, half.z), 1f, 1f, 1f - b / Mathf.Max(half.y, half.z) };
            var ring = new int[4];
            for (int k = 0; k < 4; k++)
            {
                ring[k] = mb.VertexCount;
                foreach (Vector2 q in sec) mb.AddVertex(centre + rot * new Vector3(xs[k], q.x * scale[k], q.y * scale[k]), Vector3.up, Vector2.zero);
            }
            for (int k = 0; k < 3; k++)
            for (int i = 0; i < 8; i++)
            {
                int i1 = (i + 1) % 8;
                Vector3 outward = rot * new Vector3((xs[k] + xs[k + 1]) * 0.5f, (sec[i].x + sec[i1].x) * 0.5f, (sec[i].y + sec[i1].y) * 0.5f);
                FaceOut(mb, sub, ring[k] + i, ring[k] + i1, ring[k + 1] + i1, ring[k + 1] + i, outward);
            }
            foreach (int k in new[] { 0, 3 })
            {
                int c = mb.AddVertex(centre + rot * new Vector3(xs[k], 0f, 0f), Vector3.up, Vector2.zero);
                Vector3 outward = rot * new Vector3(k == 0 ? -1f : 1f, 0f, 0f);
                for (int i = 0; i < 8; i++) TriOut(mb, sub, c, ring[k] + i, ring[k] + (i + 1) % 8, outward);
            }
        }

        static void TriOut(MeshBuilder mb, int sub, int a, int b, int c, Vector3 want)
        {
            Vector3 pa = mb.Position(a), pb = mb.Position(b), pc = mb.Position(c);
            if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), want) >= 0f) mb.AddTriangle(sub, a, b, c);
            else mb.AddTriangle(sub, a, c, b);
        }

        /// <summary>A flat disc facing <paramref name="normal"/>.</summary>
        static void Disc(MeshBuilder mb, int sub, Vector3 centre, Vector3 normal, float radius, int segments = 14)
        {
            Quaternion q = Quaternion.LookRotation(normal);
            int c = mb.AddVertex(centre, normal, Vector2.zero);
            int first = mb.VertexCount;
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                mb.AddVertex(centre + q * new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f), normal, Vector2.zero);
            }
            for (int i = 0; i < segments; i++) TriOut(mb, sub, c, first + i, first + (i + 1) % segments, normal);
        }

        // ---------------------------------------------------------------- front and rear faces

        struct FaceBox
        {
            public float HalfWidth, Bottom, Top;
            public float Height => Top - Bottom;
        }

        /// <summary>The flat part of the nose or tail after the end rounding.</summary>
        static FaceBox Face(Profile pr, bool front)
        {
            float z = front ? pr.Zf : pr.Zr;
            float yb = Bottom(pr, z), yt = TopLine(pr, z), yc = (yb + yt) * 0.5f;
            return new FaceBox { HalfWidth = HalfWidth(pr, z) * 0.95f, Bottom = yc + (yb - yc) * 0.92f, Top = yc + (yt - yc) * 0.92f };
        }

        /// <summary>
        /// Nose and tail: head lamp clusters (bezel, lens, internals), the grille and bumper intakes by authored style, tail
        /// lamps, the rear valance, plate recess and reversing lamps. All projected onto the rounded ends.
        /// </summary>
        static void Fascia(MeshBuilder mb, Profile pr)
        {
            CarBodyDef d = pr.D;
            FaceBox f = Face(pr, true);

            // Head lamps.
            float hx, hy, cy;
            switch (d.HeadLamps)
            {
                case "round": hx = hy = 0.07f; cy = f.Top - 0.085f; break;
                case "oval": hx = 0.1f; hy = 0.058f; cy = f.Top - 0.07f; break;
                case "slim": hx = 0.19f; hy = 0.028f; cy = f.Top - 0.004f; break;
                case "stacked": hx = 0.1f; hy = 0.034f; cy = f.Top - 0.05f; break;
                case "triangle": hx = 0.15f; hy = 0.06f; cy = f.Top - 0.05f; break;
                case "wedge": hx = 0.18f; hy = 0.045f; cy = f.Top - 0.012f; break;
                default: hx = 0.14f; hy = 0.05f; cy = f.Top - 0.07f; break; // rect
            }
            hy = Mathf.Min(hy, f.Height * 0.2f);
            if (d.HeadLamps == "round") hx = hy;
            Span shape;
            switch (d.HeadLamps)
            {
                case "round":
                case "oval": shape = Ellipse; break;
                case "slim": shape = Rounded(hx, hy, hy); break;
                case "stacked": shape = Rounded(hx, hy, 0.01f); break;
                case "triangle": shape = Triangle; break;
                case "wedge": shape = Wedge; break;
                default: shape = Rounded(hx, hy, 0.012f); break;
            }
            float cx = Mathf.Max(hx + 0.12f, f.HalfWidth - hx - 0.045f);
            foreach (int s in new[] { -1, 1 })
            {
                float x = s * cx;
                if (d.HeadLamps == "stacked")
                {
                    foreach (float y in new[] { cy, cy - 0.082f })
                    {
                        EndShape(mb, pr, true, TrimSub, x, y, hx + 0.012f, hy + 0.012f, 0.0025f, Rounded(hx + 0.012f, hy + 0.012f, 0.02f), s);
                        EndShape(mb, pr, true, HeadSub, x, y, hx, hy, 0.0055f, shape, s);
                        EndShape(mb, pr, true, ChromeSub, x - s * hx * 0.35f, y, hy * 0.62f, hy * 0.62f, 0.0085f, Ellipse, s, 4, 8);
                    }
                    continue;
                }
                EndShape(mb, pr, true, TrimSub, x, cy, hx + 0.013f, hy + 0.013f, 0.0025f, d.HeadLamps == "rect" || d.HeadLamps == "slim" ? Rounded(hx + 0.013f, hy + 0.013f, 0.02f) : shape, s);
                EndShape(mb, pr, true, HeadSub, x, cy, hx, hy, 0.0055f, shape, s);
                switch (d.HeadLamps)
                {
                    case "rect": // sealed beam: horizontal ribs across the lens
                        for (int i = -1; i <= 1; i++) EndShape(mb, pr, true, TrimSub, x, cy + i * hy * 0.45f, hx * 0.9f, 0.0025f, 0.0075f, Full, s, 1, 10);
                        break;
                    case "round":
                        EndShape(mb, pr, true, ChromeSub, x, cy, hx * 0.62f, hy * 0.62f, 0.0075f, Ellipse, s, 5, 10);
                        EndShape(mb, pr, true, HeadSub, x, cy, hx * 0.3f, hy * 0.3f, 0.009f, Ellipse, s, 4, 8);
                        break;
                    case "oval":
                    case "wedge":
                        EndShape(mb, pr, true, ChromeSub, x + s * hx * 0.35f, cy + hy * 0.1f, hy * 0.55f, hy * 0.55f, 0.0085f, Ellipse, s, 4, 8);
                        if (d.HeadLamps == "wedge") EndShape(mb, pr, true, ChromeSub, x - s * hx * 0.15f, cy - hy * 0.55f, hx * 0.55f, 0.004f, 0.0085f, Full, s, 1, 8);
                        break;
                    case "triangle": // triangular internals
                        EndShape(mb, pr, true, ChromeSub, x + s * hx * 0.45f, cy + hy * 0.25f, hx * 0.3f, hy * 0.5f, 0.0085f, Triangle, s, 3, 6);
                        EndShape(mb, pr, true, ChromeSub, x - s * hx * 0.1f, cy + hy * 0.45f, hx * 0.25f, hy * 0.4f, 0.0085f, Triangle, s, 3, 6);
                        break;
                    case "slim":
                        EndShape(mb, pr, true, ChromeSub, x, cy - hy * 0.15f, hx * 0.8f, hy * 0.3f, 0.0085f, Rounded(hx * 0.8f, hy * 0.3f, hy * 0.3f), s, 2, 10);
                        break;
                }
                if (d.Features.Contains("drl-strips"))
                    EndShape(mb, pr, true, HeadSub, x - s * 0.02f, cy - hy - 0.045f, hx * 0.72f, 0.0085f, 0.004f, Rounded(hx * 0.72f, 0.0085f, 0.0085f), s, 2, 10);
            }

            // Grille and bumper intakes (all openings trim-dark, slats chrome or dark).
            float gap = cx - hx - 0.035f; // half width available between the lamps
            float lowY = f.Bottom + Mathf.Min(0.075f, f.Height * 0.22f), lowHy = Mathf.Min(0.045f, f.Height * 0.12f);
            void Opening(float x, float y, float ox, float oy, int slats, int slatSub)
            {
                EndShape(mb, pr, true, TrimSub, x, y, ox, oy, 0.0032f, Rounded(ox, oy, Mathf.Min(0.02f, oy)), 1, 4, 12);
                for (int i = 1; i <= slats; i++)
                {
                    float sy = y - oy + 2f * oy * i / (slats + 1);
                    EndShape(mb, pr, true, slatSub, x, sy, ox * 0.96f, 0.0035f, 0.0065f, Full, 1, 1, 12);
                }
            }
            switch (d.Grille)
            {
                case "wide":
                    Opening(0f, cy, gap, Mathf.Min(hy + 0.012f, 0.065f), 3, ChromeSub);
                    Opening(0f, lowY, f.HalfWidth * 0.4f, lowHy, 1, TrimSub);
                    break;
                case "split":
                {
                    float each = gap * 0.5f - 0.03f;
                    foreach (int s in new[] { -1, 1 }) Opening(s * (each + 0.035f), cy - hy * 0.1f, each, Mathf.Min(0.035f, hy), 2, ChromeSub);
                    Opening(0f, lowY, f.HalfWidth * 0.5f, lowHy, 1, TrimSub);
                    break;
                }
                case "offset":
                    Opening(-gap * 0.42f, cy - hy * 0.1f, gap * 0.5f, Mathf.Min(0.04f, hy), 3, TrimSub);
                    Opening(0f, lowY, f.HalfWidth * 0.48f, lowHy, 1, TrimSub);
                    break;
                case "twin-intake":
                    Opening(0f, cy, gap * 0.85f, 0.016f, 0, TrimSub);
                    foreach (int s in new[] { -1, 1 }) Opening(s * f.HalfWidth * 0.52f, lowY + 0.01f, f.HalfWidth * 0.24f, Mathf.Min(0.06f, f.Height * 0.17f), 2, ChromeSub);
                    Opening(0f, lowY, 0.1f, lowHy * 0.8f, 1, TrimSub);
                    break;
                case "mouth":
                {
                    float my = Mathf.Min(0.06f, f.Height * 0.19f);
                    EndShape(mb, pr, true, TrimSub, 0f, f.Bottom + my + 0.035f, f.HalfWidth * 0.6f, my, 0.0032f, Rounded(f.HalfWidth * 0.6f, my, my), 1, 6, 14);
                    EndShape(mb, pr, true, TrimSub, 0f, f.Bottom + my + 0.035f, f.HalfWidth * 0.58f, 0.004f, 0.0065f, Full, 1, 1, 14);
                    break;
                }
                case "none":
                    foreach (int s in new[] { -1, 1 }) Opening(s * f.HalfWidth * 0.62f, lowY, 0.14f, lowHy * 0.8f, 1, TrimSub);
                    Opening(0f, lowY - 0.01f, f.HalfWidth * 0.33f, 0.018f, 0, TrimSub);
                    break;
                default: // slot
                    Opening(0f, cy, gap, 0.02f, 0, TrimSub);
                    Opening(0f, lowY, f.HalfWidth * 0.52f, lowHy, 1, TrimSub);
                    break;
            }

            // Bumper split line: across the nose between the lamps and the intakes, wrapping round the corners.
            float splitY = Mathf.Lerp(lowY + lowHy, cy - hy, 0.45f);
            if (d.HeadLamps == "stacked") splitY = Mathf.Min(splitY, cy - 0.082f - hy - 0.02f);
            if (splitY > lowY + lowHy + 0.02f)
                EndShape(mb, pr, true, TrimSub, 0f, splitY, f.HalfWidth * 1.02f, 0.0022f, 0.0028f, Full, 1, 1, 40);

            // Tail.
            FaceBox t = Face(pr, false);
            float thx = 0.14f, thy = 0.05f;
            float ty = t.Top - 0.035f;
            float tcx = Mathf.Max(0.3f, t.HalfWidth - thx - 0.04f);
            void Tail(float x, float y, float ox, float oy, Span sh, int s)
            {
                EndShape(mb, pr, false, TrimSub, x, y, ox + 0.012f, oy + 0.012f, 0.0025f, Rounded(ox + 0.012f, oy + 0.012f, Mathf.Min(0.02f, oy + 0.012f)), s);
                EndShape(mb, pr, false, TailSub, x, y, ox, oy, 0.0055f, sh, s);
            }
            foreach (int s in new[] { -1, 1 })
            {
                switch (d.TailLamps)
                {
                    case "divided":
                        foreach (float dx in new[] { -0.078f, 0.078f })
                            Tail(s * (tcx + dx), ty - thy, 0.068f, thy, Rounded(0.068f, thy, 0.008f), s);
                        break;
                    case "twin-slot":
                        foreach (float dy in new[] { 0.028f, -0.028f })
                            Tail(s * tcx, ty - thy + dy, thx, 0.016f, Rounded(thx, 0.016f, 0.016f), s);
                        break;
                    case "oval": // recessed: a deep dark ring round the lens
                        EndShape(mb, pr, false, TrimSub, s * tcx, ty - 0.06f, 0.11f, 0.075f, 0.0025f, Ellipse, s, 6, 12);
                        EndShape(mb, pr, false, TailSub, s * tcx, ty - 0.06f, 0.082f, 0.052f, 0.0055f, Ellipse, s, 6, 12);
                        EndShape(mb, pr, false, TrimSub, s * tcx, ty - 0.06f, 0.035f, 0.022f, 0.0075f, Ellipse, s, 4, 8);
                        break;
                    case "round":
                        foreach (float dx in new[] { 0.07f, -0.08f })
                        {
                            EndShape(mb, pr, false, TrimSub, s * (tcx + dx), ty - 0.06f, 0.068f, 0.068f, 0.0025f, Ellipse, s, 6, 12);
                            EndShape(mb, pr, false, TailSub, s * (tcx + dx), ty - 0.06f, 0.056f, 0.056f, 0.0055f, Ellipse, s, 6, 12);
                            EndShape(mb, pr, false, ChromeSub, s * (tcx + dx), ty - 0.06f, 0.018f, 0.018f, 0.0075f, Ellipse, s, 3, 6);
                        }
                        break;
                    case "wrap": // reaches round the corner onto the side
                        Tail(s * (t.HalfWidth - 0.1f), ty - thy, 0.12f, thy, Rounded(0.12f, thy, 0.012f), s);
                        break;
                    case "bar":
                        Tail(s * (t.HalfWidth - 0.1f), ty - 0.032f, 0.07f, 0.03f, Rounded(0.07f, 0.03f, 0.01f), s);
                        break;
                    default: // block
                        Tail(s * tcx, ty - thy, thx, thy, Rounded(thx, thy, 0.01f), s);
                        for (int i = -1; i <= 1; i += 2) EndShape(mb, pr, false, TrimSub, s * (tcx + i * thx * 0.34f), ty - thy, 0.003f, thy * 0.92f, 0.0075f, Full, s, 3, 1);
                        break;
                }
            }
            if (d.TailLamps == "bar")
            {
                float barHalf = t.HalfWidth - 0.17f;
                if (d.Features.Contains("split-bar"))
                    foreach (int s in new[] { -1, 1 }) Tail(s * (barHalf + 0.07f) * 0.5f, ty - 0.032f, (barHalf - 0.07f) * 0.5f, 0.016f, Rounded((barHalf - 0.07f) * 0.5f, 0.016f, 0.016f), s);
                else Tail(0f, ty - 0.032f, barHalf, 0.016f, Rounded(barHalf, 0.016f, 0.016f), 1);
            }
            // Rear valance, plate recess (the plate itself is added by the view at RearPlateCentre) and reversing lamps.
            float plateY = Bottom(pr, pr.Zr) + 0.18f;
            EndShape(mb, pr, false, TrimSub, 0f, t.Bottom + 0.042f, t.HalfWidth * 0.82f, Mathf.Min(0.034f, t.Height * 0.1f), 0.0028f,
                Rounded(t.HalfWidth * 0.82f, 0.034f, 0.02f), 1, 4, 16);
            EndShape(mb, pr, false, TrimSub, 0f, plateY, 0.275f, 0.07f, 0.004f, Rounded(0.275f, 0.07f, 0.012f), 1, 4, 12);
            float rearSplit = plateY + 0.07f + 0.045f;
            if (rearSplit < ty - 0.11f)
                EndShape(mb, pr, false, TrimSub, 0f, rearSplit, t.HalfWidth * 1.02f, 0.0022f, 0.0028f, Full, 1, 1, 40);
            foreach (int s in new[] { -1, 1 })
                EndShape(mb, pr, false, ChromeSub, s * 0.36f, plateY + 0.015f, 0.045f, 0.018f, 0.004f, Rounded(0.045f, 0.018f, 0.008f), s, 2, 6);
        }

        // ---------------------------------------------------------------- wheel arches

        /// <summary>
        /// Each arch: a lip following the authored opening (rolled for round/square, pronounced for flared) standing slightly
        /// proud of the body side with a return into the arch, and a matte dark wheel-well liner with an inner wall so the arch never
        /// shows the inside of the shell.
        /// </summary>
        static void ArchLips(MeshBuilder mb, Profile pr)
        {
            CarBodyDef d = pr.D;
            float r = ArchRadius(pr);
            float lipW = d.Arches == "flared" ? 0.07f : d.Arches == "square" ? 0.05f : 0.045f;
            float flare = d.Arches == "flared" ? 0.03f : 0.011f;
            float wr = d.WheelRadius;
            const int arc = 24, leg = 3;
            int lipSub = d.Features.Contains("arch-cladding") ? TrimSub : PaintSub;
            // The opening as the loft cuts it: vertical legs from the sill up to the wheel centre at |dz| = r, and the arch
            // shape over the top. Each point carries its outward direction in the (z, y) plane.
            List<(Vector2 P, Vector2 O)> Opening(float axle, float radius, float bottom)
            {
                var pts = new List<(Vector2, Vector2)>();
                float sillF = SillY(pr, axle + radius), sillR = SillY(pr, axle - radius);
                for (int i = 0; i < leg; i++)
                    pts.Add((new Vector2(axle + radius, Mathf.Lerp(sillF + bottom, wr, i / (float)leg)), new Vector2(1f, 0f)));
                for (int i = 0; i <= arc; i++)
                {
                    float th = Mathf.PI * i / arc;
                    Vector2 e = ArchEdge(pr, th, radius);
                    pts.Add((new Vector2(axle + e.x, wr + e.y), new Vector2(Mathf.Cos(th), Mathf.Sin(th))));
                }
                for (int i = 1; i <= leg; i++)
                    pts.Add((new Vector2(axle - radius, Mathf.Lerp(wr, sillR + bottom, i / (float)leg)), new Vector2(-1f, 0f)));
                return pts;
            }
            foreach (float axle in new[] { pr.P.FrontAxleZ, pr.P.RearAxleZ })
            foreach (int s in new[] { -1, 1 })
            {
                // Lip: outer edge blending into the panel, a proud crown, the edge, and a return into the arch; it fades out
                // into the sill at both ends.
                List<(Vector2 P, Vector2 O)> path = Opening(axle, r, 0.02f);
                int n = path.Count - 1;
                var lip = new int[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    float t = i / (float)n;
                    float taper = Mathf.SmoothStep(0f, 1f, Mathf.Min(t, 1f - t) / 0.14f);
                    Vector2 P = path[i].P, o = path[i].O;
                    Vector2 q0 = P + o * (lipW * Mathf.Lerp(0.4f, 1f, taper)), q1 = P + o * (lipW * 0.4f * Mathf.Lerp(0.5f, 1f, taper));
                    float xEdge = SideXAt(pr, P.x + o.x * 0.004f, P.y + o.y * 0.004f + 0.002f);
                    float x0 = SideXAt(pr, q0.x, q0.y) - 0.001f;
                    float x1 = Mathf.Max(SideXAt(pr, q1.x, q1.y), xEdge) + flare * taper + 0.001f;
                    float x2 = xEdge + flare * 0.7f * taper + 0.001f;
                    lip[i] = mb.AddVertex(new Vector3(s * x0, q0.y, q0.x), Vector3.up, Vector2.zero);
                    mb.AddVertex(new Vector3(s * x1, q1.y, q1.x), Vector3.up, Vector2.zero);
                    mb.AddVertex(new Vector3(s * x2, P.y, P.x), Vector3.up, Vector2.zero);
                    mb.AddVertex(new Vector3(s * (x2 - 0.02f - 0.05f * taper), P.y - o.y * 0.004f, P.x - o.x * 0.004f), Vector3.up, Vector2.zero);
                }
                for (int i = 0; i < n; i++)
                {
                    int a = lip[i], b = lip[i + 1];
                    Vector2 o = (path[i].O + path[i + 1].O).normalized;
                    var inward = new Vector3(0f, -o.y, -o.x);
                    FaceOut(mb, lipSub, a, b, b + 1, a + 1, new Vector3(s, 0f, 0f));
                    FaceOut(mb, lipSub, a + 1, b + 1, b + 2, a + 2, new Vector3(s, 0f, 0f) + inward * 0.5f);
                    FaceOut(mb, lipSub, a + 2, b + 2, b + 3, a + 3, inward);
                }
                // Liner, just outside the opening, from under the lip to well inboard of the tyre (both faces, matte).
                List<(Vector2 P, Vector2 O)> lpath = Opening(axle, r + 0.012f, 0.004f);
                int m = lpath.Count - 1;
                var liner = new int[m + 1];
                float xLinerOut = 0f;
                for (int i = 0; i <= m; i++)
                {
                    Vector2 L = lpath[i].P, o = lpath[i].O;
                    float xo = SideXAt(pr, L.x - o.x * 0.016f, L.y - o.y * 0.016f + 0.002f) - 0.03f;
                    xLinerOut = Mathf.Max(xLinerOut, xo);
                    liner[i] = mb.AddVertex(new Vector3(s * xo, L.y, L.x), Vector3.up, Vector2.zero);
                    mb.AddVertex(new Vector3(s * (xo - d.TyreWidth - 0.17f), L.y, L.x), Vector3.up, Vector2.zero);
                }
                for (int i = 0; i < m; i++)
                {
                    int la = liner[i], lb = liner[i + 1];
                    mb.AddQuad(InteriorSub, la, lb, lb + 1, la + 1);
                    mb.AddQuad(InteriorSub, la, la + 1, lb + 1, lb);
                }
                // Inner wall closing the liner (both faces): a fan from the wheel centre to the liner's inboard edge.
                float xi = xLinerOut - d.TyreWidth - 0.17f;
                int centre = mb.AddVertex(new Vector3(s * xi, (wr + SillY(pr, axle)) * 0.5f, axle), Vector3.up, Vector2.zero);
                for (int i = 0; i < m; i++)
                {
                    mb.AddTriangle(InteriorSub, centre, liner[i] + 1, liner[i + 1] + 1);
                    mb.AddTriangle(InteriorSub, centre, liner[i + 1] + 1, liner[i] + 1);
                }
                mb.AddTriangle(InteriorSub, centre, liner[m] + 1, liner[0] + 1);
                mb.AddTriangle(InteriorSub, centre, liner[0] + 1, liner[m] + 1);
            }
        }

        /// <summary>Adds the quad wound so its face points along <paramref name="want"/>.</summary>
        static void FaceOut(MeshBuilder mb, int sub, int a, int b, int c, int d, Vector3 want)
        {
            Vector3 pa = mb.Position(a), pb = mb.Position(b), pc = mb.Position(c);
            if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), want) >= 0f) mb.AddQuad(sub, a, b, c, d);
            else mb.AddQuad(sub, a, d, c, b);
        }

        // ---------------------------------------------------------------- shut lines, handles, scallops

        static void Lines(MeshBuilder mb, Profile pr)
        {
            CarBodyDef d = pr.D;
            float r = ArchRadius(pr);
            float doorFront = pr.P.FrontAxleZ - r - 0.07f;
            List<float> pillars = SidePillars(pr);
            float doorRear = pillars.Count > 0 ? pillars[0] - 0.02f : Mathf.Lerp(pr.ZRoofR, pr.ZRoofF, 0.2f);
            if (d.Style == "roadster") doorRear = CabinRear(pr) + 0.25f;
            var cuts = new List<float> { doorFront, doorRear };
            bool fourDoor = d.Features.Contains("four-door") || d.Style == "wagon";
            float rearDoorEnd = Mathf.Max(pr.P.RearAxleZ + r + 0.06f, pr.ZRoofR - 0.05f);
            if (fourDoor && rearDoorEnd < doorRear - 0.4f) cuts.Add(rearDoorEnd);
            foreach (int s in new[] { -1, 1 })
            {
                foreach (float z in cuts)
                {
                    float y0 = Bottom(pr, z) + 0.035f, y1 = TopLine(pr, z) - 0.012f;
                    if (y1 > y0 + 0.1f) SideLine(mb, pr, s, z, y0, y1, 0.004f);
                }
                // Door handles just ahead of each door's rear edge, under the belt.
                foreach (float z in fourDoor && cuts.Count > 2 ? new[] { doorRear - 0.1f, rearDoorEnd - 0.1f } : new[] { doorRear - 0.1f })
                {
                    float y = TopLine(pr, z) - 0.085f;
                    Patch(mb, TrimSub, 1, 4, (u, v) =>
                    {
                        float zz = z + (u - 0.5f) * 0.13f, yy = y + (v - 0.5f) * 0.022f;
                        return new Vector3(s * (SideXAt(pr, zz, yy) + 0.006f), yy, zz);
                    }, new Vector3(s, 0f, 0f));
                }
                if (d.Features.Contains("door-scallop"))
                {
                    // Sculpted doors: a ridge that steps out along the upper door and falls back into the lower panel, so the
                    // light breaks across the flank.
                    float zs0 = doorFront + 0.08f, zs1 = pr.P.RearAxleZ + r + 0.12f;
                    float Ridge(float z) { float yb = Bottom(pr, z), yt = TopLine(pr, z); return yb + (yt - yb) * 0.56f; }
                    float Foot(float z) { float yb = Bottom(pr, z), yt = TopLine(pr, z); return yb + (yt - yb) * 0.3f; }
                    Patch(mb, PaintSub, 3, 12, (u, v) =>
                    {
                        float z = Mathf.Lerp(zs1, zs0, u), y = Mathf.Lerp(Foot(z), Ridge(z), v);
                        float out3 = 0.0015f + 0.014f * v * Mathf.Sin(Mathf.PI * Mathf.Clamp01(u * 1.15f));
                        return new Vector3(s * (SideXAt(pr, z, y) + out3), y, z);
                    }, new Vector3(s, 0f, 0f));
                    Patch(mb, PaintSub, 1, 12, (u, v) =>
                    {
                        float z = Mathf.Lerp(zs1, zs0, u), y = Ridge(z) + 0.004f * v;
                        float out3 = 0.0015f + 0.014f * (1f - v) * Mathf.Sin(Mathf.PI * Mathf.Clamp01(u * 1.15f));
                        return new Vector3(s * (SideXAt(pr, z, y) + out3), y, z);
                    }, new Vector3(s * 0.2f, 1f, 0f));
                }
            }
            // Hood shut lines: along the fender tops and across the cowl.
            float hz0 = pr.ZWs + 0.05f, hz1 = pr.Zf - 0.1f;
            if (hz1 > hz0 + 0.3f)
            {
                foreach (int s in new[] { -1, 1 })
                {
                    var path = new List<Vector2>();
                    for (int i = 0; i <= 8; i++)
                    {
                        float z = Mathf.Lerp(hz0, hz1, i / 8f);
                        path.Add(new Vector2(s * HalfWidth(pr, z) * 0.78f, z));
                    }
                    DeckLine(mb, pr, path, 0.004f);
                }
                var across = new List<Vector2>();
                for (int i = 0; i <= 8; i++) across.Add(new Vector2(Mathf.Lerp(-1f, 1f, i / 8f) * HalfWidth(pr, hz0) * 0.78f, hz0));
                DeckLine(mb, pr, across, 0.004f);
            }
            // Boot lid on three-box bodies: across the deck behind the rear window and along its sides.
            if (d.Style == "notchback" || d.Style == "roadster")
            {
                float bz0 = d.Style == "roadster" ? CabinRear(pr) - 0.08f : pr.ZRw - 0.05f, bz1 = pr.Zr + 0.1f;
                if (bz0 > bz1 + 0.25f)
                {
                    float bw = HalfWidth(pr, bz0) * 0.8f;
                    var across = new List<Vector2>();
                    for (int i = 0; i <= 8; i++) across.Add(new Vector2(Mathf.Lerp(-1f, 1f, i / 8f) * bw, bz0));
                    DeckLine(mb, pr, across, 0.004f);
                    foreach (int s in new[] { -1, 1 })
                    {
                        var path = new List<Vector2>();
                        for (int i = 0; i <= 6; i++)
                        {
                            float z = Mathf.Lerp(bz0, bz1, i / 6f);
                            path.Add(new Vector2(s * HalfWidth(pr, z) * 0.8f, z));
                        }
                        DeckLine(mb, pr, path, 0.004f);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- mirrors

        static void Mirrors(MeshBuilder mb, Profile pr)
        {
            CarBodyDef d = pr.D;
            float z = pr.ZWs - (d.Style == "roadster" ? 0.06f : 0.11f);
            float belt = TopLine(pr, z);
            float xs = HalfWidth(pr, z) * (1f - d.Tumblehome * 0.25f);
            foreach (int s in new[] { -1, 1 })
            {
                // Stalk from the door top out to the housing.
                Pod(mb, PaintSub, new Vector3(s * (xs + 0.035f), belt + 0.035f, z), new Vector3(0.045f, 0.012f, 0.03f), 0.008f, Quaternion.Euler(0f, 0f, s * 18f));
                // Housing, slightly toed and swept, with the glass on its rear face.
                var hc = new Vector3(s * (xs + 0.125f), belt + 0.08f, z - 0.01f);
                Quaternion toe = Quaternion.Euler(0f, s * -8f, 0f);
                Pod(mb, PaintSub, hc, new Vector3(0.085f, 0.05f, 0.04f), 0.022f, toe);
                Vector3 back = toe * Vector3.back;
                EndPlate(mb, ChromeSub, hc + back * 0.0415f, toe, 0.072f, 0.04f, back);
            }
        }

        /// <summary>A flat rectangle (half sizes in the frame's x/y) facing <paramref name="facing"/>.</summary>
        static void EndPlate(MeshBuilder mb, int sub, Vector3 centre, Quaternion frame, float hx, float hy, Vector3 facing)
        {
            Patch(mb, sub, 1, 1, (u, v) => centre + frame * new Vector3((u - 0.5f) * 2f * hx, (v - 0.5f) * 2f * hy, 0f), facing);
        }

        // ---------------------------------------------------------------- interior silhouette

        /// <summary>
        /// What the closed body shows through its glass (the fitted cockpit replaces it in the driver's own view): seat backs
        /// and headrests, rear headrests where the roof continues, the dash top and the steering wheel where the cockpit puts it.
        /// </summary>
        static void InteriorSilhouette(MeshBuilder mb, Profile pr)
        {
            CabinFrame f = Cabin(pr);
            float x = Mathf.Abs(f.Eye.x);
            float zs = f.Eye.z - 0.24f;
            float top = f.OpenTop ? f.Eye.y + 0.02f : Mathf.Min(f.Eye.y + 0.03f, f.RoofUndersideY(zs) - 0.05f);
            foreach (int s in new[] { -1, 1 })
            {
                Pod(mb, InteriorSub, new Vector3(s * x, top - 0.36f, zs + 0.03f), new Vector3(0.21f, 0.2f, 0.06f), 0.05f, Quaternion.Euler(-12f, 0f, 0f));
                Pod(mb, InteriorSub, new Vector3(s * x, top - 0.075f, zs - 0.035f), new Vector3(0.12f, 0.075f, 0.05f), 0.035f, Quaternion.Euler(-8f, 0f, 0f));
            }
            float zRear = zs - 0.78f;
            bool rearSeats = !f.OpenTop && pr.D.Style != "midship" && zRear > pr.ZRw + 0.05f && zRear > pr.P.RearAxleZ - 0.2f;
            if (rearSeats)
            {
                float rtop = Mathf.Min(top - 0.03f, f.RoofUndersideY(zRear) - 0.06f);
                foreach (int s in new[] { -1, 1 })
                    Pod(mb, InteriorSub, new Vector3(s * x * 0.95f, rtop - 0.07f, zRear), new Vector3(0.11f, 0.07f, 0.05f), 0.03f, Quaternion.Euler(-8f, 0f, 0f));
            }
            // Dash top under the windscreen.
            float dz = f.WindshieldBaseZ - 0.2f;
            Pod(mb, TrimSub, new Vector3(0f, f.CowlY - 0.05f, dz), new Vector3(f.InteriorHalfWidth(dz) * 0.94f, 0.06f, 0.17f), 0.04f, Quaternion.Euler(-6f, 0f, 0f));
            // Steering wheel rim where the fitted cockpit places it.
            Vector3 wheelAt = f.Eye + new Vector3(0f, -0.3f, 0.46f);
            Quaternion tilt = Quaternion.Euler(24f, 0f, 0f);
            const int seg = 18;
            const float radius = 0.18f;
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                Vector3 c = wheelAt + tilt * (new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * radius);
                mb.AddBox(TrimSub, c, new Vector3(0.017f, radius * Mathf.PI / seg + 0.004f, 0.017f), tilt * Quaternion.Euler(0f, 0f, a * Mathf.Rad2Deg));
            }
            mb.AddBox(TrimSub, wheelAt, new Vector3(0.05f, 0.05f, 0.03f), tilt);
        }

        // ---------------------------------------------------------------- exhaust, aero, features

        static void Details(MeshBuilder mb, Profile pr)
        {
            CarBodyDef d = pr.D;
            CarAppearance a = pr.A;
            // Exhaust tips (visible options; the engine part decides the sound and power, not these): chrome pipes, dark bores.
            float exY = Bottom(pr, pr.Zr) + 0.06f, exZ = pr.Zr + 0.08f;
            var tips = new List<(float X, float R)>();
            switch (a.Exhaust)
            {
                case "dual": tips.Add((-0.5f, 0.038f)); tips.Add((0.5f, 0.038f)); break;
                case "quad": foreach (float x in new[] { -0.58f, -0.45f, 0.45f, 0.58f }) tips.Add((x, 0.032f)); break;
                case "center": tips.Add((0f, 0.055f)); exY += 0.03f; break;
                default:
                    if (d.Features.Contains("twin-exhaust")) { tips.Add((-0.5f, 0.035f)); tips.Add((0.5f, 0.035f)); }
                    else tips.Add((0.45f, 0.035f));
                    break;
            }
            foreach ((float x, float radius) in tips)
            {
                mb.AddCylinder(ChromeSub, new Vector3(x, exY, exZ), radius, 0.14f, 14, Quaternion.Euler(-90f, 0f, 0f));
                Disc(mb, TrimSub, new Vector3(x, exY, exZ - 0.1405f), Vector3.back, radius * 0.74f);
            }
            BodyKit(mb, pr);
            // Rear aero.
            float wR = HalfWidth(pr, pr.Zr + 0.2f);
            float deckY = TopLine(pr, pr.Zr + 0.25f);
            string aero = a.RearAero == "stock" ? d.Spoiler : a.RearAero == "lip-spoiler" ? "lip" : a.RearAero;
            switch (aero)
            {
                case "lip":
                    Pod(mb, PaintSub, new Vector3(0f, deckY + 0.018f, pr.Zr + 0.1f), new Vector3(wR * 0.8f, 0.014f, 0.055f), 0.01f, Quaternion.Euler(-12f, 0f, 0f));
                    break;
                case "ducktail":
                    Pod(mb, PaintSub, new Vector3(0f, deckY + 0.045f, pr.Zr + 0.14f), new Vector3(wR * 0.85f, 0.035f, 0.12f), 0.02f, Quaternion.Euler(-20f, 0f, 0f));
                    break;
                case "blade":
                    Pod(mb, PaintSub, new Vector3(0f, TopLine(pr, pr.ZRw) + 0.07f, pr.ZRw - 0.05f), new Vector3(wR * 0.9f, 0.011f, 0.085f), 0.008f, Quaternion.Euler(-6f, 0f, 0f));
                    foreach (int s in new[] { -1, 1 })
                        mb.AddBox(TrimSub, new Vector3(s * wR * 0.5f, TopLine(pr, pr.ZRw) + 0.035f, pr.ZRw - 0.03f), new Vector3(0.01f, 0.035f, 0.04f), Quaternion.identity);
                    break;
                case "wing":
                {
                    int wingSub = a.RearAero == "wing" ? Accent : TrimSub;
                    Pod(mb, wingSub, new Vector3(0f, deckY + 0.24f, pr.Zr + 0.25f), new Vector3(wR * 0.95f, 0.015f, 0.14f), 0.012f, Quaternion.Euler(-8f, 0f, 0f));
                    bool squared = d.Features.Contains("twin-intake"); // squared wing mounts
                    foreach (int s in new[] { -1, 1 })
                        mb.AddBox(TrimSub, new Vector3(s * wR * 0.6f, deckY + 0.12f, pr.Zr + 0.25f), new Vector3(squared ? 0.03f : 0.015f, 0.12f, squared ? 0.08f : 0.06f), Quaternion.identity);
                    break;
                }
                case "gt-wing":
                {
                    // Swan-neck mounts over the deck, a wide main plane with endplates and a gurney strip.
                    float wingY = deckY + 0.36f, wingZ = pr.Zr + 0.32f;
                    mb.AddBox(Accent, new Vector3(0f, wingY, wingZ), new Vector3(wR * 1.02f, 0.013f, 0.19f), Quaternion.Euler(-11f, 0f, 0f));
                    mb.AddBox(Accent, new Vector3(0f, wingY + 0.04f, wingZ - 0.17f), new Vector3(wR * 1.02f, 0.02f, 0.006f), Quaternion.identity);
                    foreach (int s in new[] { -1, 1 })
                    {
                        mb.AddBox(Accent, new Vector3(s * wR * 1.03f, wingY - 0.02f, wingZ), new Vector3(0.008f, 0.08f, 0.21f), Quaternion.identity);
                        mb.AddBox(TrimSub, new Vector3(s * wR * 0.45f, wingY + 0.02f, wingZ + 0.05f), new Vector3(0.012f, 0.03f, 0.05f), Quaternion.identity);
                        mb.AddBox(TrimSub, new Vector3(s * wR * 0.45f, deckY + 0.18f, wingZ + 0.13f), new Vector3(0.012f, 0.18f, 0.03f), Quaternion.Euler(18f, 0f, 0f));
                    }
                    break;
                }
            }
            if (d.Features.Contains("hood-scoop"))
            {
                float sz = pr.ZWs + 0.5f, sy = TopLine(pr, sz) + d.Crown;
                Pod(mb, PaintSub, new Vector3(0f, sy + 0.028f, sz), new Vector3(0.24f, 0.04f, 0.2f), 0.03f, Quaternion.Euler(-4f, 0f, 0f));
                EndPlate(mb, TrimSub, new Vector3(0f, sy + 0.035f, sz + 0.2f), Quaternion.identity, 0.19f, 0.024f, Vector3.forward);
            }
            if (d.Features.Contains("side-intakes")) // wide intakes ahead of the rear wheels
                foreach (int s in new[] { -1, 1 })
                {
                    float z0 = pr.P.RearAxleZ + ArchRadius(pr) + 0.08f;
                    Patch(mb, TrimSub, 4, 6, (u, v) =>
                    {
                        float z = z0 + u * 0.42f, y = Mathf.Lerp(0.36f, 0.6f - 0.06f * u, v);
                        return new Vector3(s * (SideXAt(pr, z, y) + 0.002f), y, z);
                    }, new Vector3(s, 0f, 0f));
                    for (int k = 0; k < 3; k++)
                        mb.AddBox(PaintSub, new Vector3(s * (HalfWidth(pr, z0 + 0.21f) - 0.004f), 0.42f + k * 0.06f, z0 + 0.21f), new Vector3(0.012f, 0.006f, 0.2f), Quaternion.identity);
                }
            if (d.Features.Contains("side-cooling")) // channels along the flanks toward the engine
                foreach (int s in new[] { -1, 1 })
                {
                    float z0 = pr.P.RearAxleZ + ArchRadius(pr) + 0.1f, z1 = z0 + 0.6f;
                    Patch(mb, TrimSub, 2, 8, (u, v) =>
                    {
                        float z = Mathf.Lerp(z0, z1, u), yb = Bottom(pr, z), yt = TopLine(pr, z);
                        float y = Mathf.Lerp(yb + (yt - yb) * 0.55f, yb + (yt - yb) * (0.66f + 0.1f * u), v);
                        return new Vector3(s * (SideXAt(pr, z, y) + 0.002f), y, z);
                    }, new Vector3(s, 0f, 0f));
                }
            if (d.Features.Contains("buttress")) // flying buttresses from the roof's rear corners to the tail
                foreach (int s in new[] { -1, 1 })
                {
                    float z0 = pr.ZRoofR + 0.02f, z1 = Mathf.Max(pr.Zr + 0.25f, pr.ZRw - 0.25f);
                    Patch(mb, PaintSub, 8, 2, (u, v) =>
                    {
                        float z = Mathf.Lerp(z0, z1, v);
                        float xo = Mathf.Lerp(HalfWidth(pr, z0) * d.RoofTaper, HalfWidth(pr, z) * 0.93f, v);
                        float yo = Mathf.Lerp(CabinTop(pr, z0), TopLine(pr, z) + 0.01f, Mathf.Pow(v, 0.8f));
                        return new Vector3(s * (xo - 0.07f * u), yo + 0.01f * (1f - u), z);
                    }, Vector3.up + new Vector3(s * 0.3f, 0f, 0f));
                }
            if (d.Features.Contains("diffuser"))
                for (int i = -2; i <= 2; i++)
                    mb.AddBox(TrimSub, new Vector3(i * 0.18f, Bottom(pr, pr.Zr) + 0.05f, pr.Zr + 0.22f), new Vector3(0.008f, 0.05f, 0.22f), Quaternion.identity);
        }

        /// <summary>
        /// Front, rear and side families (Addendum 01 §13: stock + at least two non-stock choices each). Accent parts use
        /// submesh 7; skirts follow the lower paint zone so a lower two-tone reads continuous.
        /// </summary>
        static void BodyKit(MeshBuilder mb, Profile pr)
        {
            CarAppearance a = pr.A;
            float zf = pr.Zf, zr = pr.Zr;
            float wF = HalfWidth(pr, zf - 0.2f), wR = HalfWidth(pr, zr + 0.2f);
            float ybF = Bottom(pr, zf - 0.06f), ybR = Bottom(pr, zr + 0.06f);
            switch (a.Front)
            {
                case "lip":
                    mb.AddBox(Accent, new Vector3(0f, ybF - 0.012f, zf - 0.03f), new Vector3(wF * 0.86f, 0.009f, 0.07f), Quaternion.identity);
                    break;
                case "aero":
                    mb.AddBox(Accent, new Vector3(0f, ybF - 0.014f, zf - 0.01f), new Vector3(wF * 0.92f, 0.011f, 0.1f), Quaternion.identity);
                    foreach (int s in new[] { -1, 1 })
                        mb.AddBox(Accent, new Vector3(s * (wF - 0.05f), ybF + 0.14f, zf - 0.03f), new Vector3(0.07f, 0.006f, 0.045f), Quaternion.Euler(0f, 0f, s * -14f));
                    mb.AddBox(TrimSub, new Vector3(0f, ybF + 0.08f, zf - 0.005f), new Vector3(wF * 0.5f, 0.045f, 0.012f), Quaternion.identity);
                    break;
                case "track":
                    mb.AddBox(Accent, new Vector3(0f, ybF - 0.016f, zf + 0.02f), new Vector3(wF * 0.97f, 0.012f, 0.14f), Quaternion.identity);
                    foreach (int s in new[] { -1, 1 })
                    {
                        mb.AddBox(TrimSub, new Vector3(s * wF * 0.55f, ybF + 0.09f, zf - 0.005f), new Vector3(wF * 0.2f, 0.06f, 0.012f), Quaternion.identity);
                        mb.AddBox(Accent, new Vector3(s * (wF - 0.04f), ybF + 0.12f, zf - 0.06f), new Vector3(0.08f, 0.006f, 0.05f), Quaternion.Euler(0f, 0f, s * -18f));
                        mb.AddBox(Accent, new Vector3(s * (wF - 0.04f), ybF + 0.2f, zf - 0.09f), new Vector3(0.06f, 0.006f, 0.04f), Quaternion.Euler(0f, 0f, s * -20f));
                    }
                    break;
            }
            switch (a.Rear)
            {
                case "diffuser":
                    mb.AddBox(Accent, new Vector3(0f, ybR - 0.005f, zr + 0.2f), new Vector3(wR * 0.72f, 0.008f, 0.22f), Quaternion.Euler(-9f, 0f, 0f));
                    for (int i = -3; i <= 3; i++)
                        mb.AddBox(Accent, new Vector3(i * wR * 0.2f, ybR + 0.035f, zr + 0.2f), new Vector3(0.007f, 0.045f, 0.22f), Quaternion.identity);
                    break;
                case "valance":
                    mb.AddBox(Accent, new Vector3(0f, ybR + 0.05f, zr - 0.015f), new Vector3(wR * 0.9f, 0.05f, 0.03f), Quaternion.identity);
                    foreach (int s in new[] { -1, 1 })
                        mb.AddBox(TrimSub, new Vector3(s * wR * 0.62f, ybR + 0.05f, zr - 0.03f), new Vector3(0.12f, 0.022f, 0.015f), Quaternion.identity);
                    break;
            }
            if (a.Side == "skirt" || a.Side == "sculpted")
            {
                float archR = pr.D.WheelRadius + 0.08f;
                float z0 = pr.P.RearAxleZ + archR, z1 = pr.P.FrontAxleZ - archR;
                if (z1 > z0)
                {
                    float zm = (z0 + z1) * 0.5f;
                    int skirtSub = a.TwoTone == "lower" ? Paint2 : PaintSub;
                    foreach (int s in new[] { -1, 1 })
                    {
                        mb.AddBox(skirtSub, new Vector3(s * (HalfWidth(pr, zm) * 0.99f), Bottom(pr, zm) + 0.035f, zm), new Vector3(0.03f, 0.045f, (z1 - z0) * 0.5f), Quaternion.identity);
                        if (a.Side == "sculpted")
                        {
                            mb.AddBox(Accent, new Vector3(s * (HalfWidth(pr, zm) * 0.99f + 0.02f), Bottom(pr, zm) - 0.004f, zm), new Vector3(0.02f, 0.006f, (z1 - z0) * 0.5f), Quaternion.identity);
                            float zv = pr.P.FrontAxleZ - archR - 0.08f;
                            for (int k = 0; k < 3; k++)
                                mb.AddBox(TrimSub, new Vector3(s * (HalfWidth(pr, zv) + 0.002f), Bottom(pr, zv) + 0.16f + k * 0.045f, zv), new Vector3(0.008f, 0.012f, 0.1f), Quaternion.identity);
                        }
                    }
                }
            }
        }
    }
}
