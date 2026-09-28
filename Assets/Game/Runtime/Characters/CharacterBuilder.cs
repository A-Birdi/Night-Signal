using System;
using System.Collections.Generic;
using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Characters
{
    /// <summary>The character skeleton (rigid skinning: every vertex follows one bone).</summary>
    public enum Bone
    {
        Root, Hips, Spine, Chest, Neck, Head,
        UpperArmL, ForearmL, HandL, UpperArmR, ForearmR, HandR,
        ThighL, ShinL, FootL, ThighR, ShinR, FootR,
        Count,
    }

    /// <summary>
    /// Builds an original, fully modelled stylized person from a <see cref="CharacterLook"/> (spec: player/rival drivers
    /// "fully modelled enough for meet/cutscene use, not flat cards"): a skeleton sized from height, build and limb length;
    /// a lofted torso and limbs dressed in an outer garment over an under layer, cut to the authored hem and sleeves; a head
    /// with eyes, brows, nose, mouth, ears and facial hair; the hair style; lower garment and shoes; accessories. One
    /// skinned mesh, a submesh per colour <see cref="Slot"/>. Model space: feet at y = 0, facing +z, arms in a relaxed
    /// A-pose. Deterministic; purely visual.
    /// </summary>
    public static class CharacterBuilder
    {
        /// <summary>Material slots (submeshes).</summary>
        public enum Slot { Skin, Hair, Top, Under, Accent, Lower, Shoes, Dark, Light, Metal, Count }

        public static readonly Bone[] Parent =
        {
            Bone.Root, Bone.Root, Bone.Hips, Bone.Spine, Bone.Chest, Bone.Neck,
            Bone.Chest, Bone.UpperArmL, Bone.ForearmL, Bone.Chest, Bone.UpperArmR, Bone.ForearmR,
            Bone.Hips, Bone.ThighL, Bone.ShinL, Bone.Hips, Bone.ThighR, Bone.ShinR,
        };

        /// <summary>Rest-pose joint positions (model space) and the proportions they came from.</summary>
        public sealed class Skeleton
        {
            public readonly Vector3[] Rest = new Vector3[(int)Bone.Count];
            public float H, Girth = 1f, Shoulder = 1f, HipWidth = 1f, Depth = 1f, Belly = 1f, HeadScale = 1f, Limbs = 1f;
            public Vector3 this[Bone b] => Rest[(int)b];
        }

        public static Skeleton SkeletonFor(CharacterLook look)
        {
            float h = Mathf.Clamp(look.Height, 1.45f, 1.98f);
            var sk = new Skeleton { H = h, Limbs = Mathf.Clamp(look.Limbs <= 0f ? 1f : look.Limbs, 0.92f, 1.1f) };
            switch (look.Build)
            {
                case "slim": sk.Girth = 0.86f; sk.Shoulder = 0.94f; sk.HipWidth = 0.92f; sk.Depth = 0.88f; break;
                case "sturdy": sk.Girth = 1.14f; sk.Shoulder = 1.06f; sk.HipWidth = 1.08f; sk.Depth = 1.12f; sk.Belly = 1.08f; break;
                case "broad": sk.Girth = 1.1f; sk.Shoulder = 1.2f; sk.HipWidth = 1.02f; sk.Depth = 1.06f; break;
                case "heavy": sk.Girth = 1.24f; sk.Shoulder = 1.1f; sk.HipWidth = 1.14f; sk.Depth = 1.2f; sk.Belly = 1.3f; break;
                case "petite": sk.Girth = 0.9f; sk.Shoulder = 0.9f; sk.HipWidth = 0.97f; sk.Depth = 0.92f; break;
                case "athletic": sk.Girth = 1f; sk.Shoulder = 1.1f; sk.HipWidth = 0.98f; sk.Depth = 1f; break;
            }
            sk.HeadScale = Mathf.Pow(h / 1.72f, 0.4f);
            float sw = sk.Shoulder, hw = sk.HipWidth, lim = sk.Limbs;
            // Long limbs lengthen the legs and arms and shorten the torso to keep the height.
            float hip = 0.53f + (lim - 1f) * 0.25f;
            sk.Rest[(int)Bone.Root] = Vector3.zero;
            sk.Rest[(int)Bone.Hips] = new Vector3(0f, hip * h, 0f);
            sk.Rest[(int)Bone.Spine] = new Vector3(0f, Mathf.Lerp(hip, 0.835f, 0.26f) * h, 0f);
            sk.Rest[(int)Bone.Chest] = new Vector3(0f, Mathf.Lerp(hip, 0.835f, 0.52f) * h, 0f);
            sk.Rest[(int)Bone.Neck] = new Vector3(0f, 0.835f * h, 0.003f);
            sk.Rest[(int)Bone.Head] = new Vector3(0f, 0.858f * h, 0.006f);
            foreach (int s in new[] { -1, 1 })
            {
                bool left = s < 0;
                Vector3 shoulder = new Vector3(s * (0.118f * sw * h * 0.96f + 0.012f), 0.808f * h, -0.004f);
                Vector3 elbow = shoulder + new Vector3(s * 0.034f, -0.17f * lim, -0.006f) * h;
                Vector3 wrist = elbow + new Vector3(s * 0.02f, -0.148f * lim, 0.016f) * h;
                Vector3 hipJ = new Vector3(s * 0.056f * hw * h, (hip - 0.018f) * h, 0f);
                Vector3 ankle = new Vector3(s * 0.062f * hw * h, 0.045f * h, -0.008f * h);
                Vector3 knee = Vector3.Lerp(hipJ, ankle, 0.5f) + new Vector3(0f, 0.004f * h, 0.012f * h);
                sk.Rest[(int)(left ? Bone.UpperArmL : Bone.UpperArmR)] = shoulder;
                sk.Rest[(int)(left ? Bone.ForearmL : Bone.ForearmR)] = elbow;
                sk.Rest[(int)(left ? Bone.HandL : Bone.HandR)] = wrist;
                sk.Rest[(int)(left ? Bone.ThighL : Bone.ThighR)] = hipJ;
                sk.Rest[(int)(left ? Bone.ShinL : Bone.ShinR)] = knee;
                sk.Rest[(int)(left ? Bone.FootL : Bone.FootR)] = ankle;
            }
            return sk;
        }

        // ------------------------------------------------------------------ garment model

        /// <summary>How the outfit dresses the body (derived from the look's outfit, sleeves and length).</summary>
        sealed class Garment
        {
            /// <summary>Hem of the outer layer (fraction of height).</summary>
            public float Hem;
            /// <summary>Slot of the upper torso's visible surface (outer layer, or the under layer for capes/aprons).</summary>
            public Slot Upper = Slot.Top;
            /// <summary>Slot and coverage of the visible sleeves.</summary>
            public Slot Arm = Slot.Top;
            public string Sleeves;
            public bool OpenFront, Overall, Thick, Skirted;
            public float Cloth => Thick ? 0.014f : 0.006f;
        }

        static float HemFor(string length, float dflt)
        {
            switch (length)
            {
                case "cropped": return 0.6f;
                case "hip": return 0.5f;
                case "thigh": return 0.4f;
                case "knee": return 0.3f;
                default: return dflt;
            }
        }

        static Garment Dress(CharacterLook look)
        {
            var g = new Garment { Sleeves = string.IsNullOrEmpty(look.Sleeves) ? "long" : look.Sleeves };
            switch (look.Outfit)
            {
                case "tee": g.Hem = HemFor(look.Length, 0.5f); if (g.Sleeves == "long") g.Sleeves = "short"; break;
                case "tunic": case "smock": g.Hem = HemFor(look.Length, 0.42f); break;
                case "vest": g.Hem = HemFor(look.Length, 0.5f); g.Arm = Slot.Under; g.Thick = true; break;
                case "waistcoat": g.Hem = HemFor(look.Length, 0.5f); g.Arm = Slot.Under; g.OpenFront = true; break;
                case "cardigan": case "shirt": g.Hem = HemFor(look.Length, 0.5f); g.OpenFront = true; break;
                case "jacket": case "bomber": case "windbreaker": case "uniform": case "hoodie":
                    g.Hem = HemFor(look.Length, 0.5f); g.Thick = true; break;
                case "coat": g.Hem = HemFor(look.Length, 0.3f); g.Thick = true; break;
                case "cape": case "poncho": case "apron": g.Hem = 0.5f; g.Upper = Slot.Under; g.Arm = Slot.Under; break;
                case "coveralls": g.Hem = 0f; g.Overall = true; g.Thick = true; break;
                case "jumpsuit-tied":
                    g.Hem = 0.5f; g.Overall = true; g.Upper = Slot.Under; g.Arm = Slot.Under;
                    if (g.Sleeves == "long") g.Sleeves = "short";
                    break;
                case "overalls": g.Hem = 0.5f; g.Overall = true; g.Upper = Slot.Under; g.Arm = Slot.Under; break;
                default: g.Hem = HemFor(look.Length, 0.5f); break; // longsleeve, sweater
            }
            g.Skirted = g.Hem < 0.46f && !g.Overall;
            if (look.Accessories != null && look.Accessories.Contains("open") && g.Upper == Slot.Top) g.OpenFront = true;
            return g;
        }

        // ------------------------------------------------------------------ build

        sealed class Ctx
        {
            public MeshBuilder Mb;
            public CharacterLook Look;
            public Skeleton Sk;
            public Garment G;
            public float H => Sk.H;
            public float S => Sk.H / 1.72f;
            public bool Has(string accessory) => Look.Accessories != null && Look.Accessories.Contains(accessory);
        }

        /// <summary>The character's skinned mesh (bone weights and bind poses in <see cref="Bone"/> order).</summary>
        public static Mesh Build(CharacterLook look, Skeleton sk)
        {
            var c = new Ctx { Mb = new MeshBuilder((int)Slot.Count), Look = look, Sk = sk, G = Dress(look) };
            Torso(c);
            Outer(c);
            Arms(c);
            Legs(c);
            Head(c);
            Hair(c);
            Accessories(c);
            c.Mb.RemoveUnused();
            Mesh m = c.Mb.Build($"Character_{look.Id}");
            m.boneWeights = c.Mb.BoneWeights();
            var bind = new Matrix4x4[(int)Bone.Count];
            for (int i = 0; i < bind.Length; i++) bind[i] = Matrix4x4.Translate(-sk.Rest[i]);
            m.bindposes = bind;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ primitives

        /// <summary>A ring of <paramref name="n"/> vertices: an ellipse (rx along <paramref name="u"/>, rz along <paramref name="v"/>); index i sits at angle i·360/n from u toward v.</summary>
        static int Ring(MeshBuilder mb, Vector3 centre, Vector3 u, Vector3 v, float rx, float rz, int n)
        {
            int start = mb.VertexCount;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                Vector3 d = u * Mathf.Cos(a) * rx + v * Mathf.Sin(a) * rz;
                mb.AddVertex(centre + d, d.normalized, new Vector2(i / (float)n, 0f));
            }
            return start;
        }

        /// <summary>Joins two rings (same count) with outward-facing quads; <paramref name="include"/> picks segments.</summary>
        static void Band(MeshBuilder mb, Slot slot, int ra, int rb, int n, Func<int, bool> include = null)
        {
            Vector3 centre = (mb.Position(ra) + mb.Position(ra + n / 2)) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                if (include != null && !include(i)) continue;
                int i1 = (i + 1) % n;
                Vector3 pa = mb.Position(ra + i), pb = mb.Position(ra + i1), pc = mb.Position(rb + i1);
                Vector3 outward = (pa + pb) * 0.5f - centre;
                if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), outward) >= 0f) mb.AddQuad((int)slot, ra + i, ra + i1, rb + i1, rb + i);
                else mb.AddQuad((int)slot, ra + i, rb + i, rb + i1, ra + i1);
            }
        }

        /// <summary>Closes a ring with a fan to <paramref name="tip"/>, facing away from <paramref name="inside"/>.</summary>
        static void Fan(MeshBuilder mb, Slot slot, int ring, int n, Vector3 tip, Vector3 inside)
        {
            int t = mb.AddVertex(tip, (tip - inside).normalized, Vector2.zero);
            for (int i = 0; i < n; i++)
            {
                int i1 = (i + 1) % n;
                Vector3 pa = mb.Position(t), pb = mb.Position(ring + i), pc = mb.Position(ring + i1);
                Vector3 outward = (pa + pb + pc) / 3f - inside;
                if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), outward) >= 0f) mb.AddTriangle((int)slot, t, ring + i, ring + i1);
                else mb.AddTriangle((int)slot, t, ring + i1, ring + i);
            }
        }

        static void Frame(Vector3 axis, out Vector3 u, out Vector3 v)
        {
            Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.forward)) > 0.9f ? Vector3.up : Vector3.forward;
            u = Vector3.Cross(reference, axis).normalized;
            if (Vector3.Dot(u, Vector3.right) < 0f) u = -u;
            v = Vector3.Cross(axis, u).normalized;
        }

        /// <summary>A tapered segment from a to b with elliptical rings (the second radius = r × squash), optionally capped.</summary>
        static void Tube(Ctx c, Slot slot, Bone bone, Vector3 a, Vector3 b, float ra, float rb, float squash = 1f, int n = 10, bool capA = false, bool capB = false)
        {
            MeshBuilder mb = c.Mb;
            mb.Bone = (int)bone;
            Vector3 axis = (b - a).normalized;
            Frame(axis, out Vector3 u, out Vector3 v);
            int r0 = Ring(mb, a, u, v, ra, ra * squash, n), r1 = Ring(mb, b, u, v, rb, rb * squash, n);
            Band(mb, slot, r0, r1, n);
            if (capA) Fan(mb, slot, r0, n, a - axis * ra * 0.6f, (a + b) * 0.5f);
            if (capB) Fan(mb, slot, r1, n, b + axis * rb * 0.6f, (a + b) * 0.5f);
        }

        /// <summary>An ellipsoid, or the band of one between two latitudes (0 = bottom, 1 = top); <paramref name="keep"/>(t, θ) filters faces (θ 0 = +z, 0.25 = +x).</summary>
        static void Blob(Ctx c, Slot slot, Bone bone, Vector3 centre, Vector3 radii, Quaternion rot, int lat = 8, int lon = 12, float from = 0f, float to = 1f,
            Func<float, float, bool> keep = null)
        {
            MeshBuilder mb = c.Mb;
            mb.Bone = (int)bone;
            int start = mb.VertexCount;
            for (int i = 0; i <= lat; i++)
            {
                float t = Mathf.Lerp(from, to, i / (float)lat), phi = (t - 0.5f) * Mathf.PI;
                for (int j = 0; j < lon; j++)
                {
                    float th = j * Mathf.PI * 2f / lon;
                    var local = new Vector3(Mathf.Cos(phi) * Mathf.Sin(th) * radii.x, Mathf.Sin(phi) * radii.y, Mathf.Cos(phi) * Mathf.Cos(th) * radii.z);
                    mb.AddVertex(centre + rot * local, rot * local.normalized, new Vector2(j / (float)lon, t));
                }
            }
            for (int i = 0; i < lat; i++)
            for (int j = 0; j < lon; j++)
            {
                float t = Mathf.Lerp(from, to, (i + 0.5f) / lat), th = (j + 0.5f) / lon;
                if (keep != null && !keep(t, th)) continue;
                int a = start + i * lon + j, b = start + i * lon + (j + 1) % lon, d = a + lon, e = b + lon;
                Vector3 pa = mb.Position(a), pb = mb.Position(b), pe = mb.Position(e), pd = mb.Position(d);
                Vector3 outward = (pa + pb + pd + pe) * 0.25f - centre;
                Vector3 n = Vector3.Cross(pb - pa, pe - pa);
                if (n.sqrMagnitude < 1e-14f) n = Vector3.Cross(pe - pa, pd - pa); // pole quad
                bool keepFirst = (pb - pa).sqrMagnitude > 1e-12f, keepSecond = (pe - pd).sqrMagnitude > 1e-12f;
                // At a pole one triangle of the quad is degenerate: leave it out, so no vertex is used only by zero-area faces.
                if (Vector3.Dot(n, outward) >= 0f)
                {
                    if (keepFirst) mb.AddTriangle((int)slot, a, b, e);
                    if (keepSecond) mb.AddTriangle((int)slot, a, e, d);
                }
                else
                {
                    if (keepSecond) mb.AddTriangle((int)slot, a, d, e);
                    if (keepFirst) mb.AddTriangle((int)slot, a, e, b);
                }
            }
        }

        static void Box(Ctx c, Slot slot, Bone bone, Vector3 centre, Vector3 half, Quaternion rot)
        {
            c.Mb.Bone = (int)bone;
            c.Mb.AddBox((int)slot, centre, half, rot, 0.5f);
        }

        static void Beam(Ctx c, Slot slot, Bone bone, Vector3 a, Vector3 b, float half)
        {
            Vector3 d = b - a;
            if (d.sqrMagnitude < 1e-8f) return;
            c.Mb.Bone = (int)bone;
            Vector3 up = Mathf.Abs(Vector3.Dot(d.normalized, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            c.Mb.AddBox((int)slot, (a + b) * 0.5f, new Vector3(half, half, d.magnitude * 0.5f), Quaternion.LookRotation(d, up), 0.5f);
        }

        /// <summary>
        /// A hanging double-sided sheet around the vertical axis at <paramref name="axis"/> (x, z): angles a0→a1 in degrees
        /// (0 = +x, 90 = front, 180 = −x), from the top ellipse (r0 at y0) to the bottom (r1 at y1(angle)). The inner side has
        /// its own vertices so the two faces never share (and cancel) normals.
        /// </summary>
        static void Sheet(Ctx c, Slot slot, Bone bone, Vector2 axis, float y0, Vector2 r0, Func<float, float> y1, Vector2 r1, float a0, float a1, int segs)
        {
            MeshBuilder mb = c.Mb;
            mb.Bone = (int)bone;
            for (int side = 0; side < 2; side++)
            {
                int start = mb.VertexCount;
                for (int i = 0; i <= segs; i++)
                {
                    float a = Mathf.Lerp(a0, a1, i / (float)segs), rad = a * Mathf.Deg2Rad;
                    var dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                    Vector3 n = side == 0 ? dir : -dir;
                    mb.AddVertex(new Vector3(axis.x + dir.x * r0.x, y0, axis.y + dir.z * r0.y), n, Vector2.zero);
                    mb.AddVertex(new Vector3(axis.x + dir.x * r1.x, y1(a), axis.y + dir.z * r1.y), n, Vector2.zero);
                }
                for (int i = 0; i < segs; i++)
                {
                    int t0 = start + i * 2, b0 = t0 + 1, t1 = t0 + 2, b1 = t0 + 3;
                    Vector3 pa = mb.Position(t0), pb = mb.Position(t1), pc = mb.Position(b1);
                    Vector3 mid = (pa + pb) * 0.5f;
                    Vector3 outward = new Vector3(mid.x - axis.x, 0f, mid.z - axis.y) * (side == 0 ? 1f : -1f);
                    if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), outward) >= 0f) mb.AddQuad((int)slot, t0, t1, b1, b0);
                    else mb.AddQuad((int)slot, t0, b0, b1, t1);
                }
            }
        }

        // ------------------------------------------------------------------ torso

        struct TorsoRing { public float Y, Rx, Rz; public Bone B; }

        /// <summary>The torso's rings (Y as a fraction of height; radii in metres for this height and build).</summary>
        static List<TorsoRing> TorsoRings(Ctx c)
        {
            Skeleton sk = c.Sk;
            float g = sk.Girth, d = sk.Depth, sw = sk.Shoulder, belly = sk.Belly, hip = sk.Rest[(int)Bone.Hips].y / c.H;
            float waist = hip + 0.025f, s = c.S;
            return new List<TorsoRing>
            {
                new TorsoRing { Y = hip - 0.055f, Rx = 0.132f * sk.HipWidth * s, Rz = 0.09f * d * s, B = Bone.Hips },
                new TorsoRing { Y = hip - 0.01f, Rx = 0.148f * sk.HipWidth * s, Rz = 0.1f * d * s, B = Bone.Hips },
                new TorsoRing { Y = waist, Rx = 0.14f * g * Mathf.Lerp(1f, belly, 0.6f) * s, Rz = 0.094f * d * belly * s, B = Bone.Spine },
                new TorsoRing { Y = Mathf.Lerp(waist, 0.7f, 0.33f), Rx = 0.136f * g * Mathf.Lerp(1f, belly, 0.8f) * s, Rz = 0.092f * d * belly * belly * s, B = Bone.Spine },
                new TorsoRing { Y = Mathf.Lerp(waist, 0.7f, 0.66f), Rx = 0.137f * g * Mathf.Lerp(1f, belly, 0.5f) * s, Rz = 0.093f * d * Mathf.Lerp(1f, belly, 0.7f) * s, B = Bone.Spine },
                new TorsoRing { Y = 0.7f, Rx = 0.148f * g * Mathf.Lerp(1f, sw, 0.5f) * s, Rz = 0.1f * d * s, B = Bone.Chest },
                new TorsoRing { Y = 0.77f, Rx = 0.158f * g * sw * s, Rz = 0.097f * d * s, B = Bone.Chest },
                new TorsoRing { Y = 0.806f, Rx = 0.148f * g * sw * s, Rz = 0.082f * d * s, B = Bone.Chest },
                new TorsoRing { Y = 0.829f, Rx = 0.086f * Mathf.Lerp(1f, g, 0.5f) * Mathf.Lerp(1f, sw, 0.5f) * s, Rz = 0.062f * s, B = Bone.Chest },
            };
        }

        const int TorsoN = 16;

        /// <summary>Angle (degrees, 90 = front) of torso ring segment i.</summary>
        static float SegAngle(int i) => (i + 0.5f) * 360f / TorsoN;

        static bool FrontStrip(int i, float halfDeg) => Mathf.Abs(Mathf.DeltaAngle(SegAngle(i), 90f)) < halfDeg;

        static void Torso(Ctx c)
        {
            Garment g = c.G;
            MeshBuilder mb = c.Mb;
            List<TorsoRing> rings = TorsoRings(c);
            float waist = rings[2].Y;
            var starts = new List<int>();
            foreach (TorsoRing r in rings)
            {
                mb.Bone = (int)r.B;
                bool outer = r.Y >= g.Hem - 0.001f || g.Overall;
                float extra = outer ? g.Cloth : 0.005f;
                if (r.Y > 0.82f) extra *= 0.5f;
                starts.Add(Ring(mb, new Vector3(0f, r.Y * c.H, 0f), Vector3.right, Vector3.forward, r.Rx + extra, r.Rz + extra, TorsoN));
            }
            for (int k = 0; k < rings.Count - 1; k++)
            {
                float ym = (rings[k].Y + rings[k + 1].Y) * 0.5f;
                Slot slot;
                if (g.Overall) slot = ym < waist + 0.01f ? Slot.Top : g.Upper;
                else if (ym < g.Hem) slot = ym < waist + 0.01f ? Slot.Lower : Slot.Under;
                else slot = g.Upper;
                if (slot == Slot.Top && g.OpenFront)
                {
                    float gap = c.Look.Outfit == "shirt" ? 22f : 13f;
                    Band(mb, Slot.Under, starts[k], starts[k + 1], TorsoN, i => FrontStrip(i, gap));
                    Band(mb, Slot.Top, starts[k], starts[k + 1], TorsoN, i => !FrontStrip(i, gap));
                }
                else Band(mb, slot, starts[k], starts[k + 1], TorsoN);
            }
            Fan(mb, g.Overall ? Slot.Top : Slot.Lower, starts[0], TorsoN, new Vector3(0f, (rings[0].Y - 0.012f) * c.H, 0f), new Vector3(0f, 0.62f * c.H, 0f));
            // Neck.
            float gn = Mathf.Lerp(1f, c.Sk.Girth, 0.5f);
            Tube(c, Slot.Skin, Bone.Neck, new Vector3(0f, 0.815f * c.H, 0f), new Vector3(0f, 0.875f * c.H, 0.008f), 0.057f * gn * c.S, 0.05f * gn * c.S, 0.92f, 12);
            // Belt when the waist shows (or is wanted over a jacket/coveralls).
            bool belt = c.Has("belt") || c.Has("tool-belt") || (!g.Overall && g.Hem > waist + 0.005f);
            if (belt)
            {
                TorsoRing w = rings[2];
                float over = (g.Hem <= waist || g.Overall ? g.Cloth : 0.005f) + 0.006f;
                mb.Bone = (int)Bone.Hips;
                int b0 = Ring(mb, new Vector3(0f, (w.Y - 0.012f) * c.H, 0f), Vector3.right, Vector3.forward, w.Rx + over, w.Rz + over, TorsoN);
                int b1 = Ring(mb, new Vector3(0f, (w.Y + 0.004f) * c.H, 0f), Vector3.right, Vector3.forward, w.Rx + over, w.Rz + over, TorsoN);
                Band(mb, c.Has("belt") || g.Overall ? Slot.Accent : Slot.Dark, b0, b1, TorsoN);
                Box(c, Slot.Metal, Bone.Hips, new Vector3(0f, (w.Y - 0.004f) * c.H, w.Rz + over + 0.003f), new Vector3(0.022f, 0.013f, 0.003f) * c.S, Quaternion.identity);
            }
        }

        /// <summary>Collars, hoods, capes, skirts, aprons, fastenings — the parts of the outfit beyond the torso loft.</summary>
        static void Outer(Ctx c)
        {
            Garment g = c.G;
            List<TorsoRing> rings = TorsoRings(c);
            TorsoRing chest = rings[6], shoulders = rings[7], hips = rings[1];
            float h = c.H, s = c.S, cl = g.Cloth;
            string outfit = c.Look.Outfit;
            switch (outfit)
            {
                case "jacket": case "bomber": case "windbreaker": case "uniform": case "coat": case "coveralls":
                {
                    bool high = outfit == "uniform" || outfit == "windbreaker" || c.Has("high-collar");
                    // Stand collar round the neck (rib-knit accent on a bomber).
                    Blob(c, outfit == "bomber" ? Slot.Accent : Slot.Top, Bone.Chest, new Vector3(0f, (high ? 0.828f : 0.82f) * h, -0.004f),
                        new Vector3(0.068f * s + cl, (high ? 0.032f : 0.022f) * s, 0.06f * s + cl), Quaternion.identity, 3, 14, 0.2f, 0.8f);
                    // Placket / zip line from collar to hem.
                    float top = 0.8f, bottom = Mathf.Max(g.Hem, rings[2].Y - 0.02f);
                    Box(c, outfit == "uniform" ? Slot.Metal : Slot.Accent, Bone.Chest, new Vector3(0f, (top + bottom) * 0.5f * h, rings[5].Rz + cl + 0.004f),
                        new Vector3(0.006f, (top - bottom) * 0.5f * h, 0.003f), Quaternion.identity);
                    if (outfit != "coveralls")
                        foreach (int sd in new[] { -1, 1 })
                            Box(c, Slot.Top, Bone.Spine, new Vector3(sd * 0.075f * s, (rings[2].Y + 0.03f) * h, rings[3].Rz + cl + 0.004f), new Vector3(0.036f, 0.024f, 0.004f) * s, Quaternion.identity);
                    if (outfit == "uniform" || outfit == "windbreaker")
                        foreach (int sd in new[] { -1, 1 }) // shoulder boards / yoke
                            Box(c, Slot.Accent, Bone.Chest, new Vector3(sd * shoulders.Rx * 0.75f, 0.812f * h, 0f), new Vector3(0.035f, 0.005f, 0.055f) * s, Quaternion.Euler(0f, 0f, sd * -12f));
                    if (outfit == "bomber")
                    {
                        // Ribbed hem.
                        TorsoRing w = rings[2];
                        c.Mb.Bone = (int)Bone.Hips;
                        int r0 = Ring(c.Mb, new Vector3(0f, g.Hem * h, 0f), Vector3.right, Vector3.forward, hips.Rx + cl + 0.003f, hips.Rz + cl + 0.003f, TorsoN);
                        int r1 = Ring(c.Mb, new Vector3(0f, (g.Hem + 0.02f) * h, 0f), Vector3.right, Vector3.forward, w.Rx + cl + 0.003f, w.Rz + cl + 0.003f, TorsoN);
                        Band(c.Mb, Slot.Accent, r0, r1, TorsoN);
                    }
                    break;
                }
                case "hoodie":
                    Blob(c, Slot.Top, Bone.Chest, new Vector3(0f, 0.805f * h, -0.08f * s), new Vector3(0.1f, 0.07f, 0.06f) * s, Quaternion.Euler(-20f, 0f, 0f), 5, 12);
                    Box(c, Slot.Top, Bone.Spine, new Vector3(0f, (rings[2].Y + 0.035f) * h, rings[3].Rz + cl + 0.005f), new Vector3(0.09f, 0.04f, 0.006f) * s, Quaternion.identity);
                    foreach (int sd in new[] { -1, 1 })
                        Box(c, Slot.Light, Bone.Chest, new Vector3(sd * 0.03f * s, 0.765f * h, chest.Rz + cl + 0.006f), new Vector3(0.004f, 0.05f, 0.003f) * s, Quaternion.identity);
                    break;
                case "sweater":
                    Blob(c, Slot.Top, Bone.Chest, new Vector3(0f, 0.822f * h, 0f), new Vector3(0.07f * s + 0.02f, 0.03f * s, 0.062f * s + 0.02f), Quaternion.identity, 3, 14, 0.2f, 0.8f);
                    Box(c, Slot.Accent, Bone.Chest, new Vector3(0f, 0.72f * h, rings[5].Rz + cl + 0.002f), new Vector3(0.12f * s, 0.012f, 0.003f), Quaternion.identity);
                    break;
                case "shirt": case "tunic": case "smock": case "longsleeve": case "tee": case "cardigan":
                    // A soft open collar.
                    foreach (int sd in new[] { -1, 1 })
                        Box(c, outfit == "tunic" ? Slot.Accent : Slot.Top, Bone.Chest, new Vector3(sd * 0.035f * s, 0.815f * h, shoulders.Rz * 0.6f + 0.02f * s),
                            new Vector3(0.028f, 0.004f, 0.03f) * s, Quaternion.Euler(-25f, sd * 30f, sd * -15f));
                    break;
                case "vest":
                case "waistcoat":
                    foreach (int sd in new[] { -1, 1 }) // pocket flaps on the front panels
                        Box(c, Slot.Accent, Bone.Spine, new Vector3(sd * 0.07f * s, (rings[2].Y + 0.04f) * h, rings[3].Rz * c.Sk.Belly + cl + 0.004f), new Vector3(0.03f, 0.003f, 0.003f) * s, Quaternion.identity);
                    break;
                case "cape":
                case "poncho":
                {
                    // A cone of cloth from the collar over the shoulders, to the elbows (cape) or the hips (poncho).
                    bool poncho = outfit == "poncho";
                    float yTop = 0.83f * h, yBot = (poncho ? 0.52f : 0.64f) * h;
                    var rTop = new Vector2(0.085f * s, 0.07f * s);
                    var rBot = new Vector2((poncho ? 0.33f : 0.29f) * s * c.Sk.Shoulder, (poncho ? 0.2f : 0.17f) * s * c.Sk.Depth);
                    Sheet(c, Slot.Top, Bone.Chest, Vector2.zero, yTop, rTop, a => yBot + (poncho ? 0f : 0.03f * h * Mathf.Sin(a * Mathf.Deg2Rad)), rBot, 0f, 360f, 24);
                    Blob(c, Slot.Top, Bone.Chest, new Vector3(0f, 0.835f * h, -0.004f), new Vector3(0.068f * s + 0.01f, 0.03f * s, 0.06f * s + 0.01f), Quaternion.identity, 3, 14, 0.2f, 0.8f);
                    if (!poncho) Beam(c, Slot.Accent, Bone.Chest, new Vector3(-0.04f, 0.79f * h, rBot.y * 0.55f), new Vector3(0.04f, 0.79f * h, rBot.y * 0.55f), 0.006f);
                    break;
                }
                case "apron":
                {
                    // Bib from the chest, strap round the neck, ties at the waist; the skirt is split per leg.
                    float bibTop = 0.76f, zBib = rings[5].Rz + 0.012f;
                    Box(c, Slot.Top, Bone.Chest, new Vector3(0f, (bibTop + 0.66f) * 0.5f * h, zBib), new Vector3(0.1f * s, (bibTop - 0.66f) * 0.5f * h, 0.004f), Quaternion.identity);
                    Box(c, Slot.Top, Bone.Spine, new Vector3(0f, (0.66f + rings[2].Y) * 0.5f * h, rings[3].Rz * c.Sk.Belly + 0.014f), new Vector3(0.12f * s, (0.66f - rings[2].Y) * 0.5f * h + 0.01f, 0.004f), Quaternion.identity);
                    foreach (int sd in new[] { -1, 1 })
                        Beam(c, Slot.Top, Bone.Chest, new Vector3(sd * 0.09f * s, bibTop * h, zBib), new Vector3(sd * 0.045f * s, 0.826f * h, 0f), 0.006f);
                    TorsoRing w = rings[2];
                    c.Mb.Bone = (int)Bone.Hips;
                    int t0 = Ring(c.Mb, new Vector3(0f, (w.Y - 0.008f) * h, 0f), Vector3.right, Vector3.forward, w.Rx + 0.012f, w.Rz + 0.012f, TorsoN);
                    int t1 = Ring(c.Mb, new Vector3(0f, (w.Y + 0.006f) * h, 0f), Vector3.right, Vector3.forward, w.Rx + 0.012f, w.Rz + 0.012f, TorsoN);
                    Band(c.Mb, Slot.Accent, t0, t1, TorsoN);
                    SplitSkirt(c, Slot.Top, w.Y, 0.3f, new Vector2(w.Rx + 0.016f, w.Rz + 0.016f), new Vector2(hips.Rx * 1.05f, hips.Rz * 1.6f), 25f, 155f);
                    break;
                }
                case "overalls":
                {
                    // Bib and braces over the shirt.
                    TorsoRing w = rings[2];
                    float zb = rings[4].Rz * c.Sk.Belly + 0.01f;
                    Box(c, Slot.Top, Bone.Spine, new Vector3(0f, (w.Y + 0.7f) * 0.5f * h, zb), new Vector3(0.1f * s, (0.7f - w.Y) * 0.5f * h, 0.006f), Quaternion.identity);
                    foreach (int sd in new[] { -1, 1 })
                    {
                        Beam(c, Slot.Top, Bone.Chest, new Vector3(sd * 0.08f * s, 0.7f * h, rings[5].Rz + 0.012f), new Vector3(sd * 0.075f * s, 0.815f * h, 0.03f * s), 0.012f * s);
                        Beam(c, Slot.Top, Bone.Chest, new Vector3(sd * 0.075f * s, 0.815f * h, -0.03f * s), new Vector3(sd * 0.03f * s, 0.62f * h, -rings[4].Rz - 0.012f), 0.012f * s);
                        Box(c, Slot.Metal, Bone.Chest, new Vector3(sd * 0.08f * s, 0.7f * h, rings[5].Rz + 0.02f), new Vector3(0.012f, 0.012f, 0.004f) * s, Quaternion.identity);
                    }
                    break;
                }
                case "jumpsuit-tied":
                {
                    // Sleeves tied round the waist, hanging in front.
                    TorsoRing w = rings[2];
                    foreach (int sd in new[] { -1, 1 })
                    {
                        var knot = new Vector3(sd * 0.03f * s, (w.Y - 0.01f) * h, w.Rz + cl + 0.02f);
                        Tube(c, Slot.Top, Bone.Hips, new Vector3(sd * (w.Rx + cl), (w.Y - 0.005f) * h, 0.02f), knot, 0.03f * s, 0.03f * s, 0.8f, 8);
                        Tube(c, Slot.Top, Bone.Hips, knot, knot + new Vector3(sd * 0.03f, -0.2f * h, 0.012f), 0.03f * s, 0.026f * s, 0.8f, 8, false, true);
                    }
                    Blob(c, Slot.Top, Bone.Hips, new Vector3(0f, (w.Y - 0.01f) * h, w.Rz + cl + 0.025f), Vector3.one * 0.03f * s, Quaternion.identity, 4, 8);
                    break;
                }
            }
            if (g.Skirted)
            {
                // Skirt of the outer layer below the hips, split per leg (each half follows its thigh).
                TorsoRing w = rings[1];
                float flare = outfit == "coat" ? 1.34f : 1.22f;
                SplitSkirt(c, Slot.Top, w.Y + 0.01f, g.Hem, new Vector2(w.Rx + cl + 0.004f, w.Rz + cl + 0.004f), new Vector2((w.Rx + cl) * flare, (w.Rz + cl) * flare * 1.25f), 0f, 360f, g.OpenFront ? 11f : 0f);
            }
            if (c.Has("patches"))
            {
                Box(c, Slot.Accent, Bone.Spine, new Vector3(0.07f * s, (rings[3].Y + 0.01f) * h, rings[3].Rz * c.Sk.Belly + cl + 0.004f), new Vector3(0.028f, 0.024f, 0.003f) * s, Quaternion.Euler(0f, 0f, 8f));
                Box(c, Slot.Accent, Bone.Chest, new Vector3(-0.09f * s, 0.74f * h, rings[5].Rz + cl + 0.002f), new Vector3(0.022f, 0.02f, 0.003f) * s, Quaternion.Euler(0f, 0f, -6f));
                Box(c, Slot.Accent, Bone.Chest, new Vector3(0.05f * s, 0.72f * h, -rings[5].Rz - cl - 0.002f), new Vector3(0.03f, 0.026f, 0.003f) * s, Quaternion.Euler(0f, 0f, 4f));
            }
            if (c.Look.Lower == "skirt")
            {
                TorsoRing w = rings[1];
                SplitSkirt(c, Slot.Lower, rings[2].Y, 0.34f, new Vector2(w.Rx + 0.01f, w.Rz + 0.01f), new Vector2(w.Rx * 1.35f, w.Rz * 1.7f), 0f, 360f);
            }
        }

        /// <summary>A flared skirt over degrees a0→a1 (90 = front) split down the middle: the −x half follows the left thigh, the +x half the right.</summary>
        static void SplitSkirt(Ctx c, Slot slot, float yTop, float yBottom, Vector2 rTop, Vector2 rBottom, float a0, float a1, float frontGap = 0f)
        {
            float h = c.H;
            bool full = a1 - a0 >= 359f;
            // Left half: angles 90..270 (x < 0); right half: −90..90 (x > 0).
            for (int k = 0; k < 2; k++)
            {
                float lo = k == 0 ? 90f : -90f, hi = lo + 180f;
                if (!full)
                {
                    // Clip [a0, a1] (a front arc within 0..180) to this half.
                    lo = Mathf.Max(lo, a0);
                    hi = Mathf.Min(hi, a1);
                }
                if (k == 0) lo = Mathf.Max(lo, 90f + frontGap);
                else hi = Mathf.Min(hi, 90f - frontGap);
                if (hi - lo < 1f) continue;
                int segs = Mathf.Max(3, Mathf.RoundToInt((hi - lo) / 15f));
                Sheet(c, slot, k == 0 ? Bone.ThighL : Bone.ThighR, Vector2.zero, yTop * h, rTop, a => yBottom * h, rBottom, lo, hi, segs);
            }
        }

        // ------------------------------------------------------------------ limbs

        static void Arms(Ctx c)
        {
            Skeleton sk = c.Sk;
            Garment g = c.G;
            float gi = sk.Girth * c.S, cl = g.Arm == Slot.Top ? g.Cloth * 0.85f : 0.004f;
            string sleeves = g.Sleeves;
            foreach (int side in new[] { -1, 1 })
            {
                bool left = side < 0;
                Bone ua = left ? Bone.UpperArmL : Bone.UpperArmR, fa = left ? Bone.ForearmL : Bone.ForearmR, hand = left ? Bone.HandL : Bone.HandR;
                Vector3 sh = sk[ua], el = sk[fa], wr = sk[hand];
                Vector3 down = (wr - el).normalized;
                Slot cloth = g.Arm;
                bool cover = sleeves != "none";
                float ru = 0.047f * gi, re = 0.037f * gi, rw = 0.027f * gi, capCl = cover ? cl : 0f;
                // Shoulder cap (hides the joint).
                Blob(c, cover ? cloth : Slot.Skin, ua, sh + new Vector3(-side * 0.012f, -0.014f, 0f) * c.S, new Vector3(0.05f * gi + capCl, 0.043f * gi + capCl, 0.048f * gi + capCl), Quaternion.identity, 6, 10);
                Vector3 mid = Vector3.Lerp(sh, el, 0.55f), along = (el - sh).normalized;
                switch (sleeves)
                {
                    case "none":
                        Tube(c, Slot.Skin, ua, sh, el, ru, re, 0.95f);
                        break;
                    case "short":
                        Tube(c, cloth, ua, sh, mid, ru + cl, ru * 0.96f + cl, 0.95f);
                        Tube(c, cloth, ua, mid - along * 0.01f, mid + along * 0.004f, ru * 0.96f + cl + 0.003f, ru * 0.96f + cl + 0.003f, 0.95f);
                        Tube(c, Slot.Skin, ua, mid, el, ru * 0.9f, re, 0.95f);
                        break;
                    default:
                        Tube(c, cloth, ua, sh, el, ru + cl, re + cl, 0.95f);
                        break;
                }
                bool longSleeve = sleeves == "long", rolled = sleeves == "rolled";
                Blob(c, longSleeve || rolled ? cloth : Slot.Skin, fa, el, Vector3.one * (re + (longSleeve || rolled ? cl : 0f)), Quaternion.identity, 5, 8);
                if (longSleeve)
                {
                    Vector3 cuff = Vector3.Lerp(el, wr, 0.84f);
                    Tube(c, cloth, fa, el, cuff, re + cl, rw * 1.2f + cl, 0.9f);
                    Tube(c, Slot.Skin, fa, cuff, wr, rw * 1.05f, rw, 0.9f);
                    Slot cuffSlot = c.Look.Outfit == "uniform" || c.Look.Outfit == "bomber" || c.Has("cuffs") ? Slot.Accent : cloth;
                    Tube(c, cuffSlot, fa, cuff - down * 0.018f, cuff + down * 0.004f, rw * 1.2f + cl + 0.004f, rw * 1.2f + cl + 0.004f, 0.9f);
                }
                else
                {
                    Tube(c, Slot.Skin, fa, el, wr, re * 0.97f, rw, 0.9f);
                    if (rolled)
                        Tube(c, cloth, fa, el - down * 0.01f, Vector3.Lerp(el, wr, 0.22f), re + cl + 0.006f, re * 0.95f + cl + 0.006f, 0.9f);
                    if (c.Has("cuffs") || c.Has("wristband") && left)
                        Tube(c, Slot.Accent, fa, wr - down * 0.04f, wr - down * 0.008f, rw + 0.006f, rw + 0.006f, 0.9f);
                }
                // Hand: palm and fingers as a mitt, a thumb.
                bool glove = c.Has("gloves") || c.Has("glove-one") && !left;
                Slot hs = glove ? Slot.Dark : Slot.Skin;
                Quaternion hr = Quaternion.FromToRotation(Vector3.up, -down);
                Blob(c, hs, hand, wr + down * 0.05f * c.S, new Vector3(0.024f, 0.055f, 0.042f) * c.S * Mathf.Lerp(1f, sk.Girth, 0.4f), hr * Quaternion.Euler(0f, side * -10f, 0f), 5, 8);
                Blob(c, hs, hand, wr + down * 0.032f * c.S + new Vector3(-side * 0.004f, 0f, 0.028f) * c.S, new Vector3(0.011f, 0.03f, 0.011f) * c.S, Quaternion.FromToRotation(Vector3.up, (down + Vector3.forward * 0.8f).normalized), 4, 6);
                if (c.Has("watch") && left)
                {
                    Tube(c, Slot.Dark, fa, wr - down * 0.034f, wr - down * 0.014f, rw + 0.005f, rw + 0.005f, 0.9f, 10);
                    Blob(c, Slot.Metal, fa, wr - down * 0.024f + new Vector3(-0.022f, 0f, 0f) * c.S, new Vector3(0.006f, 0.014f, 0.014f) * c.S, Quaternion.identity, 3, 8);
                }
                if (c.Has("glove-one") && !left)
                    Box(c, Slot.Accent, hand, wr + down * 0.045f * c.S + new Vector3(side * 0.026f, 0f, 0f) * c.S, new Vector3(0.003f, 0.012f, 0.03f) * c.S, hr);
            }
        }

        static void Legs(Ctx c)
        {
            Skeleton sk = c.Sk;
            float gi = sk.Girth * c.S;
            string lower = c.Look.Lower;
            Slot legSlot = c.G.Overall ? Slot.Top : Slot.Lower;
            bool narrow = lower == "narrow";
            float rt = (narrow ? 0.072f : 0.08f) * gi, rk = (narrow ? 0.05f : 0.058f) * gi, ra = (narrow ? 0.038f : 0.048f) * gi;
            foreach (int side in new[] { -1, 1 })
            {
                bool left = side < 0;
                Bone th = left ? Bone.ThighL : Bone.ThighR, sh = left ? Bone.ShinL : Bone.ShinR, ft = left ? Bone.FootL : Bone.FootR;
                Vector3 hip = sk[th], knee = sk[sh], ankle = sk[ft];
                Blob(c, legSlot, th, hip + new Vector3(side * 0.004f, 0.012f, 0f), new Vector3(0.082f, 0.085f, 0.084f) * gi, Quaternion.identity, 6, 10);
                switch (lower)
                {
                    case "shorts":
                    {
                        Vector3 hem = Vector3.Lerp(hip, knee, 0.6f);
                        Tube(c, legSlot, th, hip, hem, rt, rt * 0.9f, 1f);
                        Tube(c, Slot.Skin, th, hem, knee, 0.058f * gi, 0.046f * gi, 1f);
                        Blob(c, Slot.Skin, sh, knee, Vector3.one * 0.046f * gi, Quaternion.identity, 5, 8);
                        Tube(c, Slot.Skin, sh, knee, ankle, 0.047f * gi, 0.031f * gi, 1f);
                        break;
                    }
                    case "skirt":
                        Tube(c, Slot.Skin, th, hip, knee, 0.064f * gi, 0.046f * gi, 1f);
                        Blob(c, Slot.Skin, sh, knee, Vector3.one * 0.046f * gi, Quaternion.identity, 5, 8);
                        Tube(c, Slot.Skin, sh, knee, ankle, 0.047f * gi, 0.031f * gi, 1f);
                        break;
                    default: // trousers, narrow, cargo
                        Tube(c, legSlot, th, hip, knee, rt, rk, 1f);
                        Blob(c, legSlot, sh, knee, Vector3.one * rk, Quaternion.identity, 5, 8);
                        Tube(c, legSlot, sh, knee, ankle + new Vector3(0f, 0.025f, 0f), rk, ra, 1f);
                        if (lower == "cargo")
                            Box(c, legSlot, th, Vector3.Lerp(hip, knee, 0.58f) + new Vector3(side * rt * 0.92f, 0f, 0f), new Vector3(0.012f, 0.05f, 0.045f) * c.S, Quaternion.identity);
                        break;
                }
                Shoe(c, ft, ankle);
            }
        }

        static void Shoe(Ctx c, Bone ft, Vector3 ankle)
        {
            float s = c.S * Mathf.Lerp(1f, c.Sk.Girth, 0.3f);
            string shoes = c.Look.Shoes;
            var foot = new Vector3(ankle.x, 0f, ankle.z);
            bool loafer = shoes == "loafers";
            // One rounded shoe: the lower band is the sole, the rest the upper.
            float ht = (loafer ? 0.036f : 0.046f) * s;
            var centre = foot + new Vector3(0f, ht * 0.94f, 0.052f * s);
            var radii = new Vector3(0.047f * s, ht, 0.118f * s);
            Blob(c, loafer ? Slot.Dark : Slot.Light, ft, centre, radii, Quaternion.identity, 8, 14, 0f, 1f, (t, th) => t < 0.34f);
            Blob(c, Slot.Shoes, ft, centre, radii, Quaternion.identity, 8, 14, 0f, 1f, (t, th) => t >= 0.34f);
            if (shoes == "boots")
                Tube(c, Slot.Shoes, ft, foot + Vector3.up * 0.02f * s, ankle + Vector3.up * 0.1f * s, 0.058f * s, 0.055f * s, 1f, 10, false, true);
            else
                Tube(c, Slot.Shoes, ft, foot + Vector3.up * 0.02f * s, ankle + Vector3.up * 0.012f * s, 0.05f * s, 0.046f * s, 1f, 10);
            if (shoes == "sneakers")
                Box(c, c.Has("bright-laces") ? Slot.Accent : Slot.Light, ft, foot + new Vector3(0f, 0.08f, 0.078f) * s, new Vector3(0.02f, 0.005f, 0.03f) * s, Quaternion.Euler(-20f, 0f, 0f));
        }

        // ------------------------------------------------------------------ head

        static Vector3 HeadCentre(Ctx c) => c.Sk[Bone.Head] + new Vector3(0f, 0.104f, 0.012f) * c.Sk.HeadScale;
        static Vector3 HeadRadii(Ctx c) => new Vector3(0.088f, 0.112f, 0.1f) * c.Sk.HeadScale;
        static Vector3 JawCentre(Ctx c) => HeadCentre(c) + new Vector3(0f, -0.055f, 0.018f) * c.Sk.HeadScale;
        static Vector3 JawRadii(Ctx c) => new Vector3(0.068f * Mathf.Lerp(1f, c.Sk.Girth, 0.5f), 0.06f, 0.072f) * c.Sk.HeadScale;

        /// <summary>The face surface z at (x, y) (model space): the front of the skull or the jaw, whichever is further forward.</summary>
        static float FaceZ(Ctx c, float x, float y)
        {
            float z = float.MinValue;
            Vector3 hc = HeadCentre(c), r = HeadRadii(c), jc = JawCentre(c), jr = JawRadii(c);
            float q = 1f - Sq((x - hc.x) / r.x) - Sq((y - hc.y) / r.y);
            if (q > 0f) z = Mathf.Max(z, hc.z + r.z * Mathf.Sqrt(q));
            q = 1f - Sq((x - jc.x) / jr.x) - Sq((y - jc.y) / jr.y);
            if (q > 0f) z = Mathf.Max(z, jc.z + jr.z * Mathf.Sqrt(q));
            return z == float.MinValue ? hc.z : z;
        }

        static float Sq(float v) => v * v;

        static void Head(Ctx c)
        {
            Vector3 hc = HeadCentre(c), r = HeadRadii(c);
            float k = c.Sk.HeadScale;
            Blob(c, Slot.Skin, Bone.Head, hc, r, Quaternion.identity, 10, 16);
            Blob(c, Slot.Skin, Bone.Head, JawCentre(c), JawRadii(c), Quaternion.identity, 6, 12);
            foreach (int s in new[] { -1, 1 })
                Blob(c, Slot.Skin, Bone.Head, hc + new Vector3(s * r.x * 0.97f, -0.014f * k, -0.006f * k), new Vector3(0.012f, 0.026f, 0.019f) * k, Quaternion.Euler(0f, s * -15f, 0f), 4, 6);
            // Eyes (whites, irises, highlights), brows, nose, mouth.
            bool sleepy = c.Look.Face == "sleepy";
            foreach (int s in new[] { -1, 1 })
            {
                float ex = hc.x + s * 0.034f * k, ey = hc.y - 0.008f * k;
                float ez = FaceZ(c, ex, ey);
                Quaternion turn = Quaternion.Euler(0f, s * 16f, 0f);
                var eye = new Vector3(ex, ey, ez - 0.004f * k);
                Blob(c, Slot.Light, Bone.Head, eye, new Vector3(0.019f, sleepy ? 0.009f : 0.016f, 0.007f) * k, turn, 4, 10);
                Blob(c, Slot.Dark, Bone.Head, eye + turn * new Vector3(0f, -0.001f, 0.0045f) * k, new Vector3(0.011f, sleepy ? 0.007f : 0.013f, 0.0045f) * k, turn, 4, 8);
                Blob(c, Slot.Light, Bone.Head, eye + turn * new Vector3(s * 0.004f, 0.004f, 0.0085f) * k, Vector3.one * 0.0032f * k, Quaternion.identity, 2, 4);
                float tilt = c.Look.Face == "serious" ? s * 14f : c.Look.Face == "grin" || c.Look.Face == "smile" ? -s * 6f : 0f;
                float by = ey + 0.026f * k;
                Box(c, Slot.Hair, Bone.Head, new Vector3(ex, by, FaceZ(c, ex, by) + 0.001f), new Vector3(0.018f, 0.0032f, 0.003f) * k, turn * Quaternion.Euler(0f, 0f, tilt));
            }
            float ny = hc.y - 0.034f * k;
            Blob(c, Slot.Skin, Bone.Head, new Vector3(hc.x, ny, FaceZ(c, hc.x, ny) - 0.002f * k), new Vector3(0.009f, 0.016f, 0.012f) * k, Quaternion.Euler(-18f, 0f, 0f), 4, 6);
            float my = hc.y - 0.066f * k, mz = FaceZ(c, hc.x, my);
            switch (c.Look.Face)
            {
                case "grin":
                    Blob(c, Slot.Dark, Bone.Head, new Vector3(hc.x, my, mz - 0.002f * k), new Vector3(0.019f, 0.007f, 0.0035f) * k, Quaternion.identity, 3, 8);
                    Box(c, Slot.Light, Bone.Head, new Vector3(hc.x, my + 0.002f * k, mz + 0.0008f), new Vector3(0.014f, 0.0022f, 0.001f) * k, Quaternion.identity);
                    break;
                case "smile":
                    foreach (int s in new[] { -1, 1 })
                        Box(c, Slot.Dark, Bone.Head, new Vector3(hc.x + s * 0.009f * k, my + 0.002f * k, FaceZ(c, hc.x + s * 0.009f * k, my) + 0.0005f), new Vector3(0.01f, 0.0019f, 0.002f) * k, Quaternion.Euler(0f, s * 12f, s * 14f));
                    break;
                default:
                    Box(c, Slot.Dark, Bone.Head, new Vector3(hc.x, my, mz + 0.0005f), new Vector3(0.013f, 0.0019f, 0.002f) * k, Quaternion.identity);
                    break;
            }
            FacialHair(c);
        }

        static void FacialHair(Ctx c)
        {
            string fh = c.Look.FacialHair;
            if (string.IsNullOrEmpty(fh) || fh == "none") return;
            Vector3 hc = HeadCentre(c), jc = JawCentre(c), jr = JawRadii(c);
            float k = c.Sk.HeadScale;
            if (fh == "moustache" || fh == "beard" || fh == "goatee")
            {
                float y = hc.y - 0.052f * k;
                foreach (int s in new[] { -1, 1 })
                    Box(c, Slot.Hair, Bone.Head, new Vector3(hc.x + s * 0.012f * k, y, FaceZ(c, hc.x + s * 0.012f * k, y) + 0.002f), new Vector3(0.014f, 0.0045f, 0.004f) * k, Quaternion.Euler(0f, s * 14f, s * 12f));
            }
            if (fh == "beard" || fh == "stubble")
            {
                float grow = fh == "beard" ? 0.012f * k : 0.0025f;
                // The jaw's lower front and sides, from ear to ear under the mouth.
                Blob(c, Slot.Hair, Bone.Head, jc + new Vector3(0f, fh == "beard" ? -0.008f * k : 0f, 0f), jr + Vector3.one * grow, Quaternion.identity, 6, 14, 0f, 0.62f,
                    (t, th) => Mathf.Cos(th * Mathf.PI * 2f) > -0.3f && (t < 0.4f || Mathf.Abs(Mathf.Sin(th * Mathf.PI * 2f)) > 0.55f));
            }
            if (fh == "goatee")
                Blob(c, Slot.Hair, Bone.Head, new Vector3(jc.x, jc.y - jr.y * 0.72f, jc.z + jr.z * 0.62f), new Vector3(0.02f, 0.022f, 0.016f) * k, Quaternion.identity, 4, 8);
        }

        // ------------------------------------------------------------------ hair

        static void Hair(Ctx c)
        {
            string style = c.Look.Hair;
            if (style == "shaved") return;
            Vector3 hc = HeadCentre(c), r = HeadRadii(c);
            float k = c.Sk.HeadScale, h = c.H;
            float thick = style == "buzz" ? 0.003f : style == "curly" || style == "high-top" ? 0.02f : style == "receding" ? 0.006f : 0.011f;
            Vector3 cap = r + Vector3.one * thick * k;
            float frontLine = style == "receding" ? 0.78f : style == "buzz" ? 0.66f : 0.64f;
            bool sidesShort = style == "undercut" || style == "topknot";
            float sideLine = sidesShort ? 0.62f : 0.5f;
            Blob(c, Slot.Hair, Bone.Head, hc + new Vector3(0f, 0.002f, -0.004f) * k, cap, Quaternion.identity, 12, 18, 0f, 1f, (t, th) =>
            {
                float front = Mathf.Cos(th * Mathf.PI * 2f); // +1 at the face
                if (front > 0.5f) return t > frontLine;
                float line = Mathf.Lerp(sidesShort ? 0.5f : 0.28f, sideLine, Mathf.Clamp01((front + 1f) / 1.5f));
                return t > line;
            });
            if (sidesShort) // clipped sides: a thin shadow of the cap
                Blob(c, Slot.Hair, Bone.Head, hc, r + Vector3.one * 0.0015f, Quaternion.identity, 8, 16, 0.28f, 0.66f, (t, th) => Mathf.Cos(th * Mathf.PI * 2f) < 0.3f);
            Vector3 back = hc + new Vector3(0f, 0f, -r.z);
            Vector3 crown = hc + new Vector3(0f, r.y, -0.02f * k);
            switch (style)
            {
                case "bob":
                case "shoulder":
                case "long":
                case "low-tie":
                {
                    // A curtain from the temples to the jaw (bob) or the shoulder blades (long), open at the face; a fringe.
                    float yTop = hc.y + 0.02f * k;
                    float frontBottom = hc.y - (style == "bob" ? 0.085f : 0.12f) * k;
                    float backBottom = style == "long" ? 0.68f * h : style == "shoulder" ? 0.79f * h : hc.y - (style == "low-tie" ? 0.1f : 0.095f) * k;
                    var rTop = new Vector2(cap.x * 1.02f, cap.z * 1.02f);
                    var rBot = style == "bob" ? new Vector2(cap.x * 1.06f, cap.z) : new Vector2(cap.x * 1.05f, cap.z * 1.12f);
                    Sheet(c, Slot.Hair, Bone.Head, new Vector2(hc.x, hc.z - 0.006f * k), yTop, rTop,
                        a => Mathf.Lerp(frontBottom, backBottom, Mathf.Clamp01((-Mathf.Sin(a * Mathf.Deg2Rad) + 0.3f) / 1.3f)), rBot, 125f, 415f, 20);
                    FrontFringe(c, style == "bob" ? 0.026f : 0.018f, style == "bob" && c.Has("asymmetric") ? 12f : 0f);
                    if (style == "low-tie")
                    {
                        Vector3 nape = back + new Vector3(0f, -0.1f * k, -0.012f);
                        Box(c, Slot.Accent, Bone.Head, nape, new Vector3(0.02f, 0.01f, 0.012f) * k, Quaternion.identity);
                        Tube(c, Slot.Hair, Bone.Head, nape, new Vector3(nape.x, 0.56f * h, nape.z - 0.08f * c.Sk.Depth), 0.026f * k, 0.018f * k, 0.8f, 8, false, true);
                    }
                    break;
                }
                case "ponytail":
                {
                    Vector3 root = back + new Vector3(0f, 0.04f * k, 0.004f);
                    Box(c, Slot.Accent, Bone.Head, root + new Vector3(0f, 0f, -0.01f * k), new Vector3(0.022f, 0.01f, 0.012f) * k, Quaternion.identity);
                    Tube(c, Slot.Hair, Bone.Head, root + new Vector3(0f, 0f, -0.012f * k), root + new Vector3(0f, -0.08f * k, -0.075f * k), 0.028f * k, 0.03f * k, 1f, 8, true);
                    Tube(c, Slot.Hair, Bone.Head, root + new Vector3(0f, -0.08f * k, -0.075f * k), root + new Vector3(c.Has("asymmetric") ? 0.04f : 0f, -0.26f * k, -0.07f * k), 0.03f * k, 0.01f * k, 1f, 8, false, true);
                    FrontFringe(c, 0.012f, 0f);
                    break;
                }
                case "bun":
                    Blob(c, Slot.Hair, Bone.Head, hc + new Vector3(0f, r.y * 0.62f, -r.z * 0.78f), new Vector3(0.042f, 0.038f, 0.04f) * k, Quaternion.identity, 6, 10);
                    break;
                case "topknot":
                    Blob(c, Slot.Hair, Bone.Head, crown + new Vector3(0f, 0.012f * k, -0.01f * k), new Vector3(0.03f, 0.034f, 0.03f) * k, Quaternion.identity, 5, 10);
                    Tube(c, Slot.Accent, Bone.Head, crown + new Vector3(0f, -0.006f * k, -0.01f * k), crown + new Vector3(0f, 0.004f * k, -0.01f * k), 0.024f * k, 0.024f * k, 1f, 10);
                    break;
                case "twin-tails":
                case "twin-braids":
                    foreach (int s in new[] { -1, 1 })
                    {
                        Vector3 root = hc + new Vector3(s * r.x * 0.88f, -0.03f * k, -r.z * 0.45f);
                        Vector3 tip = root + new Vector3(s * 0.02f, -0.22f * k, 0.02f);
                        if (style == "twin-braids") Braid(c, root, tip, 0.02f * k, 5);
                        else Tube(c, Slot.Hair, Bone.Head, root, tip, 0.026f * k, 0.01f * k, 1f, 8, true, true);
                        Box(c, Slot.Accent, Bone.Head, root + new Vector3(0f, -0.02f * k, 0f), new Vector3(0.016f, 0.008f, 0.016f) * k, Quaternion.identity);
                    }
                    FrontFringe(c, 0.02f, 0f);
                    break;
                case "braid":
                    Braid(c, back + new Vector3(0f, -0.03f * k, -0.006f), new Vector3(back.x, 0.66f * h, back.z - 0.075f * c.Sk.Depth), 0.024f * k, 7);
                    break;
                case "side-braid":
                    Braid(c, hc + new Vector3(r.x * 0.8f, -0.05f * k, -r.z * 0.5f), new Vector3(0.08f * c.S, 0.7f * h, 0.07f * c.S), 0.022f * k, 6);
                    break;
                case "crown-braid":
                    for (int i = 0; i < 14; i++)
                    {
                        float a = i / 14f * Mathf.PI * 2f;
                        var p = new Vector3(hc.x + Mathf.Sin(a) * cap.x * 0.9f, hc.y + r.y * 0.52f, hc.z - 0.006f + Mathf.Cos(a) * cap.z * 0.88f);
                        Blob(c, Slot.Hair, Bone.Head, p, new Vector3(0.02f, 0.017f, 0.02f) * k, Quaternion.Euler(0f, a * Mathf.Rad2Deg, 30f), 3, 6);
                    }
                    break;
                case "locs":
                {
                    // Locs gathered at the back of the head, falling to the shoulders.
                    Vector3 tie = back + new Vector3(0f, 0.01f * k, -0.012f);
                    Box(c, Slot.Accent, Bone.Head, tie, new Vector3(0.028f, 0.012f, 0.014f) * k, Quaternion.identity);
                    for (int i = 0; i < 7; i++)
                    {
                        float off = (i - 3) * 0.012f;
                        Tube(c, Slot.Hair, Bone.Head, tie + new Vector3(off, 0f, 0f), tie + new Vector3(off * 2.2f, -0.2f * k - Mathf.Abs(off) * 0.8f, -0.04f), 0.009f * k, 0.007f * k, 1f, 5, false, true);
                    }
                    for (int i = 0; i < 10; i++)
                    {
                        float a = Mathf.Lerp(-110f, 110f, i / 9f) * Mathf.Deg2Rad;
                        Vector3 root = hc + new Vector3(Mathf.Sin(a) * cap.x * 0.9f, r.y * 0.62f, -Mathf.Cos(a) * cap.z * 0.2f);
                        Beam(c, Slot.Hair, Bone.Head, root, tie + new Vector3(Mathf.Sin(a) * 0.012f, 0f, 0f), 0.008f * k);
                    }
                    break;
                }
                case "spiky":
                    for (int i = 0; i < 11; i++)
                    {
                        float a = (i / 11f - 0.5f) * Mathf.PI * 1.6f;
                        Vector3 dir = new Vector3(Mathf.Sin(a) * 0.7f, 0.75f, 0.35f + Mathf.Cos(a) * 0.25f - (i % 2) * 0.35f).normalized;
                        Vector3 root = hc + Vector3.Scale(dir, cap) * 0.9f;
                        Tube(c, Slot.Hair, Bone.Head, root, root + (dir + Vector3.forward * 0.3f).normalized * 0.06f * k, 0.022f * k, 0.002f, 1f, 5);
                    }
                    break;
                case "swept":
                    Blob(c, Slot.Hair, Bone.Head, hc + new Vector3(0.012f * k, r.y * 0.72f, r.z * 0.38f), new Vector3(0.078f, 0.034f, 0.06f) * k, Quaternion.Euler(8f, 0f, -12f), 5, 12);
                    break;
                case "curly":
                    for (int i = 0; i < 22; i++)
                    {
                        float a = i * 2.39996f, lat = 0.25f + (i % 5) * 0.2f;
                        var p = hc + new Vector3(Mathf.Sin(a) * cap.x * Mathf.Cos(lat), Mathf.Sin(lat) * cap.y, Mathf.Cos(a) * cap.z * Mathf.Cos(lat) - 0.008f);
                        if (Mathf.Cos(a) > 0.55f && lat < 0.7f) continue; // keep the face clear
                        Blob(c, Slot.Hair, Bone.Head, p, Vector3.one * 0.032f * k, Quaternion.identity, 4, 6);
                    }
                    break;
                case "high-top":
                    Blob(c, Slot.Hair, Bone.Head, hc + new Vector3(0f, r.y * 0.62f, -0.01f * k), new Vector3(cap.x * 1.02f, 0.075f * k, cap.z * 0.98f), Quaternion.identity, 8, 16, 0.3f, 1f);
                    break;
                case "undercut":
                    Blob(c, Slot.Hair, Bone.Head, hc + new Vector3(-0.012f * k, r.y * 0.74f, 0.018f * k), new Vector3(0.08f, 0.036f, 0.095f) * k, Quaternion.Euler(0f, 0f, 8f), 5, 12);
                    if (c.Has("braided")) Braid(c, crown + new Vector3(0f, 0f, -0.04f * k), back + new Vector3(0f, -0.1f * k, -0.02f), 0.016f * k, 4);
                    break;
            }
        }

        /// <summary>A fringe over the forehead; <paramref name="tilt"/> sweeps it to one side.</summary>
        static void FrontFringe(Ctx c, float drop, float tilt)
        {
            Vector3 hc = HeadCentre(c), r = HeadRadii(c);
            float k = c.Sk.HeadScale;
            float y = hc.y + (0.062f - drop) * k;
            Blob(c, Slot.Hair, Bone.Head, new Vector3(hc.x, y, hc.z + r.z * 0.62f), new Vector3(0.078f, drop + 0.012f, 0.048f) * k, Quaternion.Euler(-10f, 0f, tilt), 5, 12, 0.35f, 1f,
                (t, th) => Mathf.Cos(th * Mathf.PI * 2f) > -0.2f);
        }

        static void Braid(Ctx c, Vector3 from, Vector3 to, float radius, int links)
        {
            for (int i = 0; i < links; i++)
            {
                float t = (i + 0.5f) / links;
                float rr = radius * Mathf.Lerp(1f, 0.7f, t);
                Vector3 p = Vector3.Lerp(from, to, t) + new Vector3((i % 2 == 0 ? 1f : -1f) * rr * 0.25f, 0f, 0f);
                Blob(c, Slot.Hair, Bone.Head, p, new Vector3(rr, (to - from).magnitude / links * 0.62f, rr * 0.9f), Quaternion.FromToRotation(Vector3.up, (from - to).normalized), 4, 8);
            }
            Box(c, Slot.Accent, Bone.Head, to, Vector3.one * radius * 0.5f, Quaternion.identity);
        }

        // ------------------------------------------------------------------ accessories

        static void Accessories(Ctx c)
        {
            if (c.Look.Accessories == null) return;
            Vector3 hc = HeadCentre(c), r = HeadRadii(c);
            float k = c.Sk.HeadScale, h = c.H, s = c.S;
            List<TorsoRing> rings = TorsoRings(c);
            TorsoRing chest = rings[6], waist = rings[2];
            float cl = c.G.Cloth;
            float chestZ = rings[5].Rz + cl + 0.004f;
            foreach (string a in c.Look.Accessories)
            {
                switch (a)
                {
                    case "glasses":
                    case "round-glasses":
                    case "sunglasses":
                    {
                        bool round = a == "round-glasses";
                        float ly = hc.y - 0.008f * k;
                        foreach (int sd in new[] { -1, 1 })
                        {
                            float lx = hc.x + sd * 0.035f * k;
                            var lens = new Vector3(lx, ly, FaceZ(c, lx, ly) + 0.008f * k);
                            float rx = (round ? 0.019f : 0.024f) * k, ry = (round ? 0.019f : 0.013f) * k;
                            if (a == "sunglasses") Blob(c, Slot.Dark, Bone.Head, lens, new Vector3(rx, ry, 0.003f * k), Quaternion.identity, 3, 12);
                            for (int i = 0; i < 12; i++)
                            {
                                float t0 = i / 12f * Mathf.PI * 2f, t1 = (i + 1) / 12f * Mathf.PI * 2f;
                                Beam(c, Slot.Metal, Bone.Head, lens + new Vector3(Mathf.Cos(t0) * rx, Mathf.Sin(t0) * ry, 0f), lens + new Vector3(Mathf.Cos(t1) * rx, Mathf.Sin(t1) * ry, 0f), 0.0017f * k);
                            }
                            Beam(c, Slot.Metal, Bone.Head, lens + new Vector3(sd * rx, 0f, 0f), new Vector3(hc.x + sd * r.x * 1.01f, ly + 0.004f * k, hc.z - 0.01f * k), 0.0017f * k);
                            if (c.Has("glasses-cord"))
                                Beam(c, Slot.Dark, Bone.Head, new Vector3(hc.x + sd * r.x * 1.01f, ly, hc.z - 0.02f * k), new Vector3(sd * 0.05f * s, 0.8f * h, 0.03f), 0.0012f);
                        }
                        float bz = FaceZ(c, hc.x, ly) + 0.009f * k;
                        Beam(c, Slot.Metal, Bone.Head, new Vector3(hc.x - 0.012f * k, ly + 0.003f * k, bz), new Vector3(hc.x + 0.012f * k, ly + 0.003f * k, bz), 0.0017f * k);
                        break;
                    }
                    case "cap":
                        Blob(c, Slot.Accent, Bone.Head, hc + new Vector3(0f, 0.018f * k, -0.004f), r + Vector3.one * 0.018f * k, Quaternion.identity, 6, 16, 0.58f, 1f);
                        Box(c, Slot.Accent, Bone.Head, hc + new Vector3(0f, 0.058f * k, r.z + 0.03f * k), new Vector3(0.068f, 0.004f, 0.045f) * k, Quaternion.Euler(-10f, 0f, 0f));
                        break;
                    case "beanie":
                        Blob(c, Slot.Accent, Bone.Head, hc + new Vector3(0f, 0.022f * k, -0.006f), r + Vector3.one * 0.02f * k, Quaternion.identity, 6, 16, 0.55f, 1f);
                        Blob(c, Slot.Accent, Bone.Head, hc + new Vector3(0f, 0.022f * k, -0.006f), r + Vector3.one * 0.026f * k, Quaternion.identity, 2, 16, 0.55f, 0.64f);
                        break;
                    case "headband":
                    case "bandana":
                        Blob(c, Slot.Accent, Bone.Head, hc + new Vector3(0f, 0.01f * k, -0.004f), r + Vector3.one * 0.016f * k, Quaternion.identity, 2, 16, 0.66f, 0.76f);
                        if (a == "bandana") Box(c, Slot.Accent, Bone.Head, hc + new Vector3(0f, 0.04f * k, -r.z - 0.022f * k), new Vector3(0.012f, 0.03f, 0.018f) * k, Quaternion.Euler(24f, 0f, 0f));
                        break;
                    case "barrette":
                        Box(c, Slot.Accent, Bone.Head, hc + new Vector3(0.06f * k, 0.064f * k, 0.05f * k), new Vector3(0.014f, 0.005f, 0.004f) * k, Quaternion.Euler(0f, 40f, 20f));
                        break;
                    case "goggles":
                        // Worn at the collar.
                        foreach (int sd in new[] { -1, 1 })
                            Tube(c, Slot.Metal, Bone.Chest, new Vector3(sd * 0.036f * s, 0.8f * h, chest.Rz * 0.7f + 0.02f * s), new Vector3(sd * 0.036f * s, 0.8f * h - 0.004f, chest.Rz * 0.7f + 0.045f * s), 0.024f * s, 0.022f * s, 1f, 10, false, true);
                        Blob(c, Slot.Dark, Bone.Chest, new Vector3(0f, 0.815f * h, 0f), new Vector3(0.078f * s, 0.008f * s, 0.066f * s), Quaternion.identity, 2, 14);
                        break;
                    case "helmet":
                        Blob(c, Slot.Accent, Bone.Head, hc + new Vector3(0f, 0.01f * k, -0.006f), r + Vector3.one * 0.03f * k, Quaternion.identity, 8, 16, 0.4f, 1f);
                        break;
                    case "headphones":
                        // Around the neck.
                        foreach (int sd in new[] { -1, 1 })
                            Blob(c, Slot.Dark, Bone.Neck, new Vector3(sd * 0.062f * s, 0.84f * h, 0.035f * s), new Vector3(0.014f, 0.034f, 0.034f) * s, Quaternion.Euler(0f, 0f, sd * 20f), 4, 10);
                        Blob(c, Slot.Accent, Bone.Neck, new Vector3(0f, 0.838f * h, 0.03f * s), new Vector3(0.064f, 0.012f, 0.05f) * s, Quaternion.identity, 3, 14, 0f, 0.5f);
                        break;
                    case "scarf":
                        Blob(c, Slot.Accent, Bone.Chest, new Vector3(0f, 0.826f * h, -0.004f), new Vector3(0.078f * s + cl, 0.034f * s, 0.07f * s + cl), Quaternion.identity, 4, 14);
                        Box(c, Slot.Accent, Bone.Chest, new Vector3(0.032f * s, 0.75f * h, chestZ + 0.006f), new Vector3(0.024f, 0.07f, 0.007f) * s, Quaternion.Euler(0f, 0f, 5f));
                        break;
                    case "earrings":
                    case "ear-cuff":
                        foreach (int sd in a == "earrings" ? new[] { -1, 1 } : new[] { 1 })
                            Blob(c, Slot.Metal, Bone.Head, hc + new Vector3(sd * r.x * 1.02f, (a == "earrings" ? -0.042f : -0.004f) * k, -0.004f * k), new Vector3(0.004f, a == "earrings" ? 0.012f : 0.008f, 0.008f) * k, Quaternion.identity, 3, 6);
                        break;
                    case "key-reel":
                        Tube(c, Slot.Metal, Bone.Hips, new Vector3(waist.Rx + cl + 0.004f, (waist.Y - 0.03f) * h, 0.03f), new Vector3(waist.Rx + cl + 0.018f, (waist.Y - 0.03f) * h, 0.03f), 0.028f * s, 0.028f * s, 1f, 12, true, true);
                        break;
                    case "lanyard":
                        foreach (int sd in new[] { -1, 1 })
                            Beam(c, Slot.Accent, Bone.Chest, new Vector3(sd * 0.05f * s, 0.822f * h, 0.045f * s), new Vector3(0f, 0.725f * h, chestZ + 0.004f), 0.003f);
                        Box(c, Slot.Light, Bone.Chest, new Vector3(0f, 0.7f * h, chestZ + 0.005f), new Vector3(0.028f, 0.038f, 0.003f) * s, Quaternion.identity);
                        break;
                    case "medallion":
                    case "whistle":
                        foreach (int sd in new[] { -1, 1 })
                            Beam(c, Slot.Dark, Bone.Chest, new Vector3(sd * 0.05f * s, 0.822f * h, 0.045f * s), new Vector3(0f, 0.745f * h, chestZ + 0.004f), 0.0018f);
                        if (a == "medallion") Box(c, Slot.Metal, Bone.Chest, new Vector3(0f, 0.735f * h, chestZ + 0.006f), new Vector3(0.016f, 0.016f, 0.003f) * s, Quaternion.Euler(0f, 0f, 45f));
                        else Tube(c, Slot.Accent, Bone.Chest, new Vector3(0f, 0.74f * h, chestZ + 0.006f), new Vector3(0f, 0.715f * h, chestZ + 0.01f), 0.008f * s, 0.008f * s, 1f, 8, true, true);
                        break;
                    case "pin":
                        Blob(c, Slot.Accent, Bone.Chest, new Vector3(0.07f * s, 0.76f * h, chestZ + 0.004f), new Vector3(0.013f, 0.013f, 0.004f) * s, Quaternion.identity, 3, 8);
                        break;
                    case "sash":
                        Beam(c, Slot.Accent, Bone.Chest, new Vector3(-0.12f * s, 0.8f * h, chestZ * 0.7f), new Vector3(0.12f * s, (waist.Y + 0.01f) * h, rings[3].Rz * c.Sk.Belly + cl + 0.01f), 0.012f * s);
                        break;
                    case "bag":
                    case "satchel":
                    case "camera":
                    {
                        // Cross-body strap from the left shoulder to the right hip.
                        var sh = new Vector3(-0.1f * s * c.Sk.Shoulder, 0.815f * h, 0f);
                        var hipF = new Vector3(0.14f * s, (waist.Y - 0.02f) * h, waist.Rz + cl + 0.01f);
                        var hipB = new Vector3(0.14f * s, (waist.Y - 0.02f) * h, -waist.Rz - cl - 0.01f);
                        Beam(c, Slot.Dark, Bone.Chest, sh + new Vector3(0f, 0f, chest.Rz * 0.7f), hipF, 0.01f * s);
                        Beam(c, Slot.Dark, Bone.Chest, sh + new Vector3(0f, 0f, -chest.Rz * 0.7f), hipB, 0.01f * s);
                        if (a == "camera")
                        {
                            Box(c, Slot.Dark, Bone.Chest, new Vector3(0f, 0.7f * h, chestZ + 0.035f * s), new Vector3(0.055f, 0.035f, 0.028f) * s, Quaternion.identity);
                            Tube(c, Slot.Metal, Bone.Chest, new Vector3(0f, 0.7f * h, chestZ + 0.06f * s), new Vector3(0f, 0.7f * h, chestZ + 0.1f * s), 0.022f * s, 0.022f * s, 1f, 10, false, true);
                        }
                        else Box(c, Slot.Accent, Bone.Hips, new Vector3(waist.Rx + cl + 0.035f * s, (waist.Y - 0.06f) * h, 0f), new Vector3(0.035f, a == "satchel" ? 0.1f : 0.075f, a == "satchel" ? 0.13f : 0.1f) * s, Quaternion.identity);
                        break;
                    }
                    case "pouch":
                        Box(c, Slot.Accent, Bone.Hips, new Vector3(-0.07f * s, (waist.Y - 0.035f) * h, waist.Rz + cl + 0.022f * s), new Vector3(0.045f, 0.035f, 0.02f) * s, Quaternion.identity);
                        break;
                    case "tool-belt":
                        foreach (int sd in new[] { -1, 1 })
                            Box(c, Slot.Accent, Bone.Hips, new Vector3(sd * (waist.Rx + cl + 0.01f), (waist.Y - 0.05f) * h, 0.03f * s), new Vector3(0.022f, 0.05f, 0.05f) * s, Quaternion.identity);
                        break;
                    case "clipboard":
                        Box(c, Slot.Light, Bone.HandL, c.Sk[Bone.HandL] + new Vector3(-0.01f, -0.07f, 0.06f) * s, new Vector3(0.005f, 0.12f, 0.09f) * s, Quaternion.identity);
                        break;
                    case "thermos":
                        Tube(c, Slot.Metal, Bone.HandR, c.Sk[Bone.HandR] + new Vector3(0.012f, -0.13f, 0.04f) * s, c.Sk[Bone.HandR] + new Vector3(0.012f, 0.05f, 0.04f) * s, 0.03f * s, 0.03f * s, 1f, 10, true, true);
                        Tube(c, Slot.Accent, Bone.HandR, c.Sk[Bone.HandR] + new Vector3(0.012f, -0.08f, 0.04f) * s, c.Sk[Bone.HandR] + new Vector3(0.012f, -0.02f, 0.04f) * s, 0.034f * s, 0.034f * s, 1f, 10);
                        break;
                }
            }
        }
    }
}
