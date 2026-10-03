using System.Collections.Generic;
using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Characters
{
    public static partial class CharacterBuilder
    {
        // ------------------------------------------------------------------ reward wardrobe pieces

        // The challenge-reward garments and accessories (customization.json "wardrobe"), each built from its shape tokens over
        // the dressed body: stripes, bands and panels follow the torso loft and the sleeves/legs they sit on, accessories hang
        // from the belt line, the hands or the shoulders. Colours A and B are the piece's own submeshes (GearSlot); reflective
        // parts use the Light slot, fittings the Metal slot.

        /// <summary>Outer surface of the dressed torso at height fraction <paramref name="y"/>: radii with the garment's cloth, and the bone there.</summary>
        static void TorsoAt(Ctx c, List<TorsoRing> rings, float y, out float rx, out float rz, out Bone bone)
        {
            int i = 0;
            while (i < rings.Count - 2 && rings[i + 1].Y < y) i++;
            float t = Mathf.Clamp01(Mathf.InverseLerp(rings[i].Y, rings[i + 1].Y, y));
            rx = Mathf.Lerp(rings[i].Rx, rings[i + 1].Rx, t);
            rz = Mathf.Lerp(rings[i].Rz, rings[i + 1].Rz, t);
            bone = t < 0.5f ? rings[i].B : rings[i + 1].B;
            float extra = y >= c.G.Hem - 0.001f || c.G.Overall ? c.G.Cloth : 0.005f;
            if (y > 0.82f) extra *= 0.5f;
            rx += extra;
            rz += extra;
        }

        /// <summary>A point on the dressed torso at height fraction y and angle (degrees; 0 = +x, 90 = front), <paramref name="off"/> metres out.</summary>
        static Vector3 TorsoPoint(Ctx c, List<TorsoRing> rings, float y, float deg, float off)
        {
            TorsoAt(c, rings, y, out float rx, out float rz, out _);
            float a = deg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(a) * (rx + off), y * c.H, Mathf.Sin(a) * (rz + off));
        }

        static Vector3 TorsoNormal(float deg) => new Vector3(Mathf.Cos(deg * Mathf.Deg2Rad), 0f, Mathf.Sin(deg * Mathf.Deg2Rad));

        /// <summary>A band round the dressed torso between height fractions y0 and y1, <paramref name="off"/> metres proud; <paramref name="include"/> picks segments.</summary>
        static void Hoop(Ctx c, List<TorsoRing> rings, Slot slot, float y0, float y1, float off, System.Func<int, bool> include = null)
        {
            // Through every torso ring in between, so the band follows the chest and shoulders instead of cutting into them.
            var ys = new List<float> { y0 };
            foreach (TorsoRing r in rings) if (r.Y > y0 + 0.002f && r.Y < y1 - 0.002f) ys.Add(r.Y);
            ys.Add(y1);
            int prev = -1;
            foreach (float y in ys)
            {
                TorsoAt(c, rings, y, out float rx, out float rz, out Bone bone);
                c.Mb.Bone = (int)bone;
                int ring = Ring(c.Mb, new Vector3(0f, y * c.H, 0f), Vector3.right, Vector3.forward, rx + off, rz + off, TorsoN);
                if (prev >= 0) Band(c.Mb, slot, prev, ring, TorsoN, include);
                prev = ring;
            }
        }

        /// <summary>
        /// A strip over the dressed torso from (y0, deg0) to (y1, deg1) — height fractions and angles (0 = +x, 90 = front) —
        /// in short pieces that follow the surface; <paramref name="dz"/> shifts it along z (twin stripes, pinlines).
        /// </summary>
        static void TorsoPath(Ctx c, List<TorsoRing> rings, Slot slot, float y0, float deg0, float y1, float deg1, float off, float halfWidth, float dz = 0f)
        {
            int segs = Mathf.Max(2, Mathf.CeilToInt(Mathf.Max(Mathf.Abs(y1 - y0) / 0.03f, Mathf.Abs(Mathf.DeltaAngle(deg0, deg1)) / 12f)));
            var shift = new Vector3(0f, 0f, dz);
            for (int n = 0; n < segs; n++)
            {
                float t0 = n / (float)segs, t1 = (n + 1) / (float)segs, tm = (t0 + t1) * 0.5f;
                float ya = Mathf.Lerp(y0, y1, t0), yb = Mathf.Lerp(y0, y1, t1), da = Mathf.LerpAngle(deg0, deg1, t0), db = Mathf.LerpAngle(deg0, deg1, t1);
                TorsoAt(c, rings, Mathf.Lerp(y0, y1, tm), out _, out _, out Bone bone);
                Strip(c, slot, bone, TorsoPoint(c, rings, ya, da, off) + shift, TorsoPoint(c, rings, yb, db, off) + shift, TorsoNormal(Mathf.LerpAngle(deg0, deg1, tm)), halfWidth);
            }
        }

        /// <summary>
        /// A flat strip from a to b lying on a surface whose outward normal is <paramref name="normal"/>, <paramref name="lift"/>
        /// metres proud of it: one outward-facing quad (4 vertices — a box would be 24, and stripes, ticks and seams are many).
        /// </summary>
        static void Strip(Ctx c, Slot slot, Bone bone, Vector3 a, Vector3 b, Vector3 normal, float halfWidth, float lift = 0.002f)
        {
            Vector3 d = b - a;
            if (d.sqrMagnitude < 1e-8f) return;
            Vector3 n = Vector3.ProjectOnPlane(normal, d.normalized);
            if (n.sqrMagnitude < 1e-6f) n = Vector3.Cross(d, Vector3.right);
            n.Normalize();
            Vector3 side = Vector3.Cross(n, d.normalized) * halfWidth, up = n * lift;
            MeshBuilder mb = c.Mb;
            mb.Bone = (int)bone;
            int v0 = mb.AddVertex(a - side + up, n, Vector2.zero), v1 = mb.AddVertex(a + side + up, n, Vector2.zero);
            int v2 = mb.AddVertex(b + side + up, n, Vector2.zero), v3 = mb.AddVertex(b - side + up, n, Vector2.zero);
            if (Vector3.Dot(Vector3.Cross(mb.Position(v1) - mb.Position(v0), mb.Position(v2) - mb.Position(v0)), n) >= 0f) mb.AddQuad((int)slot, v0, v1, v2, v3);
            else mb.AddQuad((int)slot, v0, v3, v2, v1);
        }

        /// <summary>The dressed arm on one side, as <see cref="Arms"/> builds it.</summary>
        struct ArmGeo
        {
            public Vector3 Sh, El, Wr, Down, Cuff;
            public float Ru, Re, Rw, Cl;
            public Bone Ua, Fa, Hand;
            public int Side;
            public bool LongSleeve, Covered;

            /// <summary>Sleeve surface radius at fraction t of the upper arm (shoulder → elbow).</summary>
            public float UpperR(float t) => Mathf.Lerp(Ru + (Covered ? Cl : 0f), Re + (Covered ? Cl : 0f), t);
            /// <summary>Sleeve surface radius at fraction t of the forearm (elbow → wrist; the long sleeve ends at the cuff, 0.84).</summary>
            public float ForeR(float t) => LongSleeve && t <= 0.84f ? Mathf.Lerp(Re + Cl, Rw * 1.2f + Cl, t / 0.84f) : Mathf.Lerp(Re * 0.97f, Rw, t);
            /// <summary>Away from the body, square to the arm segment.</summary>
            public Vector3 Out(Vector3 a, Vector3 b) => Vector3.ProjectOnPlane(new Vector3(Side, 0f, 0f), (b - a).normalized).normalized;
        }

        /// <summary>How far out a bag or radio at the hip must sit: the waist or hips, over a coat's skirt when there is one.</summary>
        static float SideRadius(Ctx c, List<TorsoRing> rings) =>
            Mathf.Max(rings[2].Rx, rings[1].Rx * (c.G.Skirted ? 1.1f : 1f)) + c.G.Cloth;

        static ArmGeo ArmOf(Ctx c, int side)
        {
            Skeleton sk = c.Sk;
            bool left = side < 0;
            float gi = sk.Girth * c.S;
            var a = new ArmGeo
            {
                Side = side,
                Ua = left ? Bone.UpperArmL : Bone.UpperArmR,
                Fa = left ? Bone.ForearmL : Bone.ForearmR,
                Hand = left ? Bone.HandL : Bone.HandR,
                Ru = 0.047f * gi, Re = 0.037f * gi, Rw = 0.027f * gi,
                Cl = c.G.Arm == Slot.Top ? c.G.Cloth * 0.85f : 0.004f,
                LongSleeve = c.G.Sleeves == "long",
                Covered = c.G.Sleeves != "none",
            };
            a.Sh = sk[a.Ua];
            a.El = sk[a.Fa];
            a.Wr = sk[a.Hand];
            a.Down = (a.Wr - a.El).normalized;
            a.Cuff = Vector3.Lerp(a.El, a.Wr, 0.84f);
            return a;
        }

        /// <summary>A band round the upper arm (forearm when <paramref name="fore"/>) centred at fraction t, half-length in metres.</summary>
        static void ArmBand(Ctx c, ArmGeo a, Slot slot, bool fore, float t, float halfLen, float extra)
        {
            Vector3 p0 = fore ? a.El : a.Sh, p1 = fore ? a.Wr : a.El;
            Vector3 axis = (p1 - p0).normalized, p = Vector3.Lerp(p0, p1, t);
            float r = (fore ? a.ForeR(t) : a.UpperR(t)) + extra;
            Tube(c, slot, fore ? a.Fa : a.Ua, p - axis * halfLen, p + axis * halfLen, r, r, fore ? 0.9f : 0.95f, 12);
        }

        /// <summary>A line of strips down the outside of the sleeve, from fraction t0 of the upper arm to t1 of the forearm, <paramref name="lateral"/> metres off the outer line.</summary>
        static void SleeveLine(Ctx c, ArmGeo a, Slot slot, float lateral, float halfWidth, float foreEnd = 0.82f)
        {
            Vector3 outU = a.Out(a.Sh, a.El), outF = a.Out(a.El, a.Wr);
            Vector3 sideU = Vector3.Cross(outU, (a.El - a.Sh).normalized).normalized * lateral;
            Vector3 sideF = Vector3.Cross(outF, (a.Wr - a.El).normalized).normalized * lateral;
            Vector3 u1 = a.El + outU * (a.UpperR(1f) + 0.0015f) + sideU;
            Strip(c, slot, a.Ua, Vector3.Lerp(a.Sh, a.El, 0.08f) + outU * (a.UpperR(0.08f) + 0.0015f) + sideU, u1, outU, halfWidth);
            Vector3 f1 = Vector3.Lerp(a.El, a.Wr, foreEnd);
            Strip(c, slot, a.Fa, a.El + outF * (a.ForeR(0f) + 0.0015f) + sideF, f1 + outF * (a.ForeR(foreEnd) + 0.0015f) + sideF, outF, halfWidth);
        }

        /// <summary>The dressed leg on one side, as <see cref="Legs"/> builds trousers.</summary>
        struct LegGeo
        {
            public Vector3 Hip, Knee, Ankle;
            public float Rt, Rk, Ra;
            public Bone Th, Sh, Ft;
            public int Side;
        }

        static LegGeo LegOf(Ctx c, int side)
        {
            Skeleton sk = c.Sk;
            float gi = sk.Girth * c.S;
            bool narrow = c.Look.Lower == "narrow", left = side < 0;
            var l = new LegGeo
            {
                Side = side,
                Th = left ? Bone.ThighL : Bone.ThighR, Sh = left ? Bone.ShinL : Bone.ShinR, Ft = left ? Bone.FootL : Bone.FootR,
                Rt = (narrow ? 0.072f : 0.08f) * gi, Rk = (narrow ? 0.05f : 0.058f) * gi, Ra = (narrow ? 0.038f : 0.048f) * gi,
            };
            l.Hip = sk[l.Th];
            l.Knee = sk[l.Sh];
            l.Ankle = sk[l.Ft];
            return l;
        }

        static void WornPieces(Ctx c)
        {
            List<WornPiece> worn = c.Look.Worn;
            if (worn == null || worn.Count == 0) return;
            for (int i = 0; i < worn.Count; i++)
            {
                if (worn[i].Shapes == null) continue;
                var a = (Slot)GearSlot(i, false);
                var b = (Slot)GearSlot(i, true);
                foreach (string shape in worn[i].Shapes) Shape(c, shape, a, b);
            }
        }

        static void Shape(Ctx c, string shape, Slot a, Slot b)
        {
            Skeleton sk = c.Sk;
            float h = c.H, s = c.S, k = sk.HeadScale, cl = c.G.Cloth;
            List<TorsoRing> rings = TorsoRings(c);
            TorsoRing waist = rings[2], shoulders = rings[7];
            Vector3 hc = HeadCentre(c), hr = HeadRadii(c);
            float hem = c.G.Overall ? rings[0].Y : Mathf.Max(c.G.Hem, rings[0].Y);
            switch (shape)
            {
                // ---------------------------------------------------------------- hands and head
                case "driving-gloves":
                    // The hands themselves take colour A (Arms); here the wrist strap and stud, open knuckles, perforations.
                    foreach (int side in new[] { -1, 1 })
                    {
                        ArmGeo g = ArmOf(c, side);
                        Vector3 outH = new Vector3(side, 0f, 0f);
                        float hg = Mathf.Lerp(1f, sk.Girth, 0.4f) * s;
                        Tube(c, b, g.Hand, g.Wr + g.Down * 0.006f * s, g.Wr + g.Down * 0.022f * s, g.Rw + 0.005f, g.Rw + 0.006f, 0.9f, 10);
                        Blob(c, Slot.Metal, g.Hand, g.Wr + g.Down * 0.014f * s + outH * (g.Rw + 0.006f), new Vector3(0.003f, 0.005f, 0.005f) * s, Quaternion.identity, 3, 6);
                        for (int n = -1; n <= 1; n++) // open knuckles
                            Blob(c, Slot.Skin, g.Hand, g.Wr + g.Down * 0.084f * s + outH * 0.021f * hg + new Vector3(0f, 0f, n * 0.012f * s), new Vector3(0.0035f, 0.0055f, 0.0055f) * s, Quaternion.identity, 3, 6);
                        for (int row = 0; row < 2; row++) // perforations on the back of the hand
                            for (int n = -1; n <= 1; n++)
                                Blob(c, Slot.Dark, g.Hand, g.Wr + g.Down * (0.04f + row * 0.016f) * s + outH * 0.0235f * hg + new Vector3(0f, 0f, n * 0.011f * s), Vector3.one * 0.0022f * s, Quaternion.identity, 2, 4);
                    }
                    break;
                case "crew-cap":
                {
                    Vector3 cc = hc + new Vector3(0f, 0.02f * k, -0.004f);
                    Vector3 cr = hr + new Vector3(0.016f, 0.024f, 0.016f) * k;
                    Blob(c, a, Bone.Head, cc, cr, Quaternion.identity, 7, 18, 0.56f, 1f);
                    // Six panel seams from the band to the button.
                    for (int j = 0; j < 6; j++)
                    {
                        float th = j * 60f * Mathf.Deg2Rad;
                        Vector3 prev = Vector3.zero;
                        for (int n = 0; n <= 4; n++)
                        {
                            float phi = (Mathf.Lerp(0.57f, 0.985f, n / 4f) - 0.5f) * Mathf.PI;
                            Vector3 p = cc + new Vector3(Mathf.Cos(phi) * Mathf.Sin(th) * cr.x, Mathf.Sin(phi) * cr.y, Mathf.Cos(phi) * Mathf.Cos(th) * cr.z) * 1.012f;
                            if (n > 0) Strip(c, b, Bone.Head, prev, p, (prev + p) * 0.5f - cc, 0.0013f * k, 0.0006f);
                            prev = p;
                        }
                    }
                    Blob(c, b, Bone.Head, cc + new Vector3(0f, cr.y * 1.01f, 0f), new Vector3(0.008f, 0.004f, 0.008f) * k, Quaternion.identity, 3, 8);
                    // Brim and the cedar badge on the front panel.
                    Box(c, a, Bone.Head, cc + new Vector3(0f, 0.034f * k, cr.z + 0.034f * k), new Vector3(0.072f, 0.004f, 0.048f) * k, Quaternion.Euler(-8f, 0f, 0f));
                    Box(c, b, Bone.Head, cc + new Vector3(0f, 0.035f * k, cr.z + 0.0815f * k), new Vector3(0.072f, 0.0045f, 0.003f) * k, Quaternion.Euler(-8f, 0f, 0f));
                    float by = 0.072f * k, bz = cr.z * Mathf.Sqrt(Mathf.Max(0f, 1f - Sq(by / cr.y))) + 0.002f;
                    Box(c, b, Bone.Head, cc + new Vector3(0f, by, bz), new Vector3(0.013f, 0.014f, 0.0015f) * k, Quaternion.Euler(-35f, 0f, 0f));
                    for (int n = 0; n < 3; n++)
                        Box(c, Slot.Dark, Bone.Head, cc + new Vector3(0f, by - 0.007f * k + n * 0.006f * k, bz + 0.0012f), new Vector3((0.009f - n * 0.0027f), 0.0022f, 0.001f) * k, Quaternion.Euler(-35f, 0f, 0f));
                    // Back strap with a steel slide.
                    Box(c, Slot.Metal, Bone.Head, cc + new Vector3(0f, 0.028f * k, -cr.z - 0.001f), new Vector3(0.016f, 0.004f, 0.002f) * k, Quaternion.identity);
                    break;
                }

                // ---------------------------------------------------------------- garment details
                case "track-stripes":
                    foreach (int side in new[] { -1, 1 })
                    {
                        ArmGeo g = ArmOf(c, side);
                        SleeveLine(c, g, a, -0.008f * s, 0.0035f * s);
                        SleeveLine(c, g, a, 0.008f * s, 0.0035f * s);
                        // Ribbed cuffs in the jacket colour over the trim cuff.
                        if (g.LongSleeve) Tube(c, Slot.Top, g.Fa, g.Cuff - g.Down * 0.02f, g.Cuff + g.Down * 0.005f, g.Rw * 1.2f + g.Cl + 0.0055f, g.Rw * 1.2f + g.Cl + 0.0055f, 0.9f, 10);
                        // Down the sides of the body, armpit to hem.
                        float deg = side > 0 ? 0f : 180f;
                        for (int n = -1; n <= 1; n += 2)
                            TorsoPath(c, rings, a, 0.765f, deg, hem + 0.005f, deg, 0.0015f, 0.0035f * s, n * 0.008f * s);
                    }
                    // A pale zip line over the placket, with its pull.
                    Box(c, b, Bone.Chest, new Vector3(0f, (0.8f + hem) * 0.5f * h, rings[5].Rz + cl + 0.0075f), new Vector3(0.0035f, (0.8f - hem) * 0.5f * h, 0.002f), Quaternion.identity);
                    Box(c, Slot.Metal, Bone.Chest, new Vector3(0f, 0.785f * h, rings[5].Rz + cl + 0.0105f), new Vector3(0.005f, 0.011f, 0.002f) * s, Quaternion.identity);
                    break;
                case "turn-ups":
                    foreach (int side in new[] { -1, 1 })
                    {
                        LegGeo l = LegOf(c, side);
                        Vector3 bottom = l.Ankle + new Vector3(0f, 0.025f, 0f), up = (l.Knee - bottom).normalized;
                        Tube(c, a, l.Sh, bottom - up * 0.004f, bottom + up * 0.05f * s, l.Ra + 0.007f, l.Ra + 0.0085f, 1f, 12);
                    }
                    break;
                case "knee-panels":
                    foreach (int side in new[] { -1, 1 })
                    {
                        LegGeo l = LegOf(c, side);
                        Vector3 centre = l.Knee + new Vector3(0f, -0.012f * s, l.Rk + 0.002f);
                        Box(c, b, l.Sh, centre, new Vector3(0.034f, 0.055f, 0.003f) * s, Quaternion.Euler(-6f, 0f, 0f));
                        foreach (int e in new[] { -1, 1 })
                            Box(c, a, l.Sh, centre + new Vector3(0f, e * 0.05f * s, 0.0035f), new Vector3(0.03f, 0.0012f, 0.001f) * s, Quaternion.Euler(-6f, 0f, 0f));
                    }
                    break;
                case "recovery-boots":
                    foreach (int side in new[] { -1, 1 })
                    {
                        LegGeo l = LegOf(c, side);
                        float ss = c.S * Mathf.Lerp(1f, sk.Girth, 0.3f), ht = 0.046f * ss;
                        var foot = new Vector3(l.Ankle.x, 0f, l.Ankle.z);
                        var centre = foot + new Vector3(0f, ht * 0.94f, 0.052f * ss);
                        var radii = new Vector3(0.047f * ss, ht, 0.118f * ss);
                        // A thick lug sole, a toe cap, a reflective heel band and an orange pull loop.
                        Blob(c, Slot.Dark, l.Ft, centre + new Vector3(0f, 0.004f * ss, 0f), radii + new Vector3(0.005f, 0.004f, 0.006f) * ss, Quaternion.identity, 8, 14, 0f, 1f, (t, th) => t < 0.34f);
                        Blob(c, b, l.Ft, centre, radii + Vector3.one * 0.0025f * ss, Quaternion.identity, 8, 14, 0f, 1f, (t, th) => t >= 0.3f && Mathf.Cos(th * Mathf.PI * 2f) > 0.72f);
                        Box(c, Slot.Light, l.Ft, foot + new Vector3(0f, 0.06f * ss, -0.058f * ss), new Vector3(0.034f, 0.008f, 0.004f) * ss, Quaternion.identity);
                        Box(c, a, l.Ft, l.Ankle + new Vector3(0f, 0.1f * ss + 0.012f, -0.057f * ss), new Vector3(0.008f, 0.016f, 0.0035f) * ss, Quaternion.identity);
                        Tube(c, a, l.Ft, l.Ankle + Vector3.up * 0.088f * ss, l.Ankle + Vector3.up * 0.1f * ss, 0.0575f * ss, 0.0575f * ss, 1f, 10);
                    }
                    break;
                case "storm-hood":
                    // The hood rolled and stowed behind the collar, a pale tape seam along the roll.
                    Blob(c, Slot.Top, Bone.Chest, new Vector3(0f, 0.818f * h, -0.072f * s), new Vector3(0.1f, 0.042f, 0.05f) * s, Quaternion.Euler(18f, 0f, 0f), 6, 16);
                    Box(c, a, Bone.Chest, new Vector3(0f, 0.825f * h, -0.118f * s), new Vector3(0.07f, 0.003f, 0.002f) * s, Quaternion.Euler(18f, 0f, 0f));
                    break;
                case "taped-seams":
                    foreach (int side in new[] { -1, 1 })
                    {
                        ArmGeo g = ArmOf(c, side);
                        // Collar to shoulder point, then along the top of the sleeve.
                        Vector3 collar = new Vector3(side * 0.062f * s, 0.828f * h, 0f), point = g.Sh + new Vector3(0f, g.UpperR(0f) + 0.002f, 0f);
                        Strip(c, a, Bone.Chest, collar, point, Vector3.up, 0.0028f * s);
                        SleeveLine(c, g, a, 0f, 0.0028f * s);
                        float deg = side > 0 ? 0f : 180f;
                        TorsoPath(c, rings, a, 0.765f, deg, hem + 0.004f, deg, 0.0015f, 0.0028f * s);
                    }
                    break;
                case "chest-zip":
                {
                    Vector3 z1 = TorsoPoint(c, rings, 0.715f, 100f, 0.002f);
                    TorsoPath(c, rings, b, 0.765f, 120f, 0.715f, 100f, 0.002f, 0.0035f * s);
                    Box(c, Slot.Metal, Bone.Chest, z1 + TorsoNormal(100f) * 0.004f + new Vector3(0f, -0.008f * s, 0f), new Vector3(0.004f, 0.009f, 0.002f) * s, Quaternion.identity);
                    break;
                }
                case "reflector-cuffs":
                    foreach (int side in new[] { -1, 1 })
                    {
                        ArmGeo g = ArmOf(c, side);
                        if (!g.LongSleeve) break;
                        Vector3 axis = g.Down;
                        float r = g.Rw * 1.2f + g.Cl + 0.006f;
                        Tube(c, Slot.Light, g.Fa, g.Cuff - axis * 0.058f * s, g.Cuff, r, r, 0.9f, 12);
                        foreach (float e in new[] { -0.06f, 0f })
                            Tube(c, b, g.Fa, g.Cuff + axis * (e - 0.002f) * s, g.Cuff + axis * (e + 0.004f) * s, r + 0.0012f, r + 0.0012f, 0.9f, 12);
                    }
                    break;
                case "button-placket":
                {
                    // Buttoned up: the shirt closes over the open front below the collar's V, a placket with buttons down it.
                    float top = 0.745f, bottom = hem + 0.002f;
                    float gap = c.Look.Outfit == "shirt" ? 22f : 13f;
                    if (c.G.OpenFront) Hoop(c, rings, Slot.Top, bottom, top, 0.0012f, i => FrontStrip(i, gap));
                    TorsoPath(c, rings, Slot.Accent, top, 90f, bottom + 0.008f, 90f, 0.0035f, 0.0105f * s);
                    for (int n = 0; n < 5; n++)
                    {
                        float y = Mathf.Lerp(top - 0.01f, bottom + 0.02f, n / 4f);
                        Vector3 p = TorsoPoint(c, rings, y, 90f, 0.006f);
                        TorsoAt(c, rings, y, out _, out _, out Bone bone);
                        Blob(c, a, bone, p, new Vector3(0.0045f, 0.0045f, 0.002f) * s, Quaternion.identity, 3, 8);
                    }
                    break;
                }
                case "pit-vest":
                {
                    foreach (float y in new[] { 0.585f, 0.665f })
                        Hoop(c, rings, Slot.Light, y - 0.011f, y + 0.011f, 0.003f);
                    foreach (int side in new[] { -1, 1 })
                    {
                        Vector3 f0 = TorsoPoint(c, rings, 0.69f, side > 0 ? 62f : 118f, 0.0035f), b0 = TorsoPoint(c, rings, 0.69f, side > 0 ? -62f : 242f, 0.0035f);
                        Vector3 top = new Vector3(side * 0.078f * s, 0.817f * h, 0f);
                        Strip(c, Slot.Light, Bone.Chest, f0, top + new Vector3(0f, 0f, 0.04f * s), TorsoNormal(side > 0 ? 62f : 118f) + Vector3.up * 0.5f, 0.01f * s);
                        Strip(c, Slot.Light, Bone.Chest, top + new Vector3(0f, 0f, 0.04f * s), top + new Vector3(0f, 0f, -0.04f * s), Vector3.up, 0.01f * s);
                        Strip(c, Slot.Light, Bone.Chest, top + new Vector3(0f, 0f, -0.04f * s), b0, TorsoNormal(side > 0 ? -62f : 242f) + Vector3.up * 0.5f, 0.01f * s);
                    }
                    // The radio tab on the left chest, a stub aerial.
                    Vector3 tab = TorsoPoint(c, rings, 0.745f, 122f, 0.007f);
                    Box(c, b, Bone.Chest, tab, new Vector3(0.016f, 0.03f, 0.006f) * s, Quaternion.Euler(0f, -32f, 0f));
                    Beam(c, Slot.Dark, Bone.Chest, tab + new Vector3(0.006f * s, 0.03f * s, 0f), tab + new Vector3(0.006f * s, 0.07f * s, 0f), 0.0025f * s);
                    break;
                }
                case "marshal-chevrons":
                {
                    // Two chevrons across the back.
                    foreach (float y in new[] { 0.64f, 0.71f })
                        foreach (int side in new[] { -1, 1 })
                            TorsoPath(c, rings, a, y + 0.045f, side > 0 ? 318f : 222f, y, 270f, 0.0025f, 0.011f * s);
                    foreach (int side in new[] { -1, 1 })
                    {
                        ArmGeo g = ArmOf(c, side);
                        ArmBand(c, g, a, false, 0.5f, 0.011f * s, 0.003f);
                        ArmBand(c, g, a, false, 0.72f, 0.011f * s, 0.003f);
                    }
                    break;
                }
                case "double-breasted":
                    foreach (int side in new[] { -1, 1 })
                        foreach (float y in new[] { 0.735f, 0.675f, 0.615f })
                        {
                            Vector3 p = TorsoPoint(c, rings, y, side > 0 ? 72f : 108f, 0.005f);
                            TorsoAt(c, rings, y, out _, out _, out Bone bone);
                            Blob(c, Slot.Metal, bone, p, new Vector3(0.0065f, 0.0065f, 0.003f) * s, Quaternion.identity, 3, 8);
                        }
                    break;
                case "knee-pads":
                    foreach (int side in new[] { -1, 1 })
                    {
                        LegGeo l = LegOf(c, side);
                        Blob(c, b, l.Sh, l.Knee + new Vector3(0f, -0.006f * s, l.Rk + 0.004f), new Vector3(0.038f, 0.048f, 0.013f) * s, Quaternion.identity, 5, 10);
                        Beam(c, Slot.Dark, l.Sh, l.Knee + new Vector3(-l.Rk - 0.004f, -0.03f * s, 0f), l.Knee + new Vector3(l.Rk + 0.004f, -0.03f * s, 0f), 0.003f * s);
                    }
                    break;
                case "chest-pocket":
                {
                    Vector3 p = TorsoPoint(c, rings, 0.715f, 118f, 0.003f);
                    Box(c, Slot.Top, Bone.Chest, p, new Vector3(0.034f, 0.04f, 0.004f) * s, Quaternion.Euler(0f, -28f, 0f));
                    Box(c, a, Bone.Chest, p + new Vector3(0f, 0.035f * s, 0.002f), new Vector3(0.036f, 0.011f, 0.0045f) * s, Quaternion.Euler(0f, -28f, 0f));
                    break;
                }
                case "name-patch":
                {
                    Vector3 p = TorsoPoint(c, rings, 0.735f, 62f, 0.003f);
                    Box(c, b, Bone.Chest, p, new Vector3(0.033f, 0.0145f, 0.0018f) * s, Quaternion.Euler(0f, 28f, 0f));
                    Box(c, Slot.Light, Bone.Chest, p + TorsoNormal(62f) * 0.0015f, new Vector3(0.03f, 0.0115f, 0.0018f) * s, Quaternion.Euler(0f, 28f, 0f));
                    break;
                }
                case "racing-stripes":
                    foreach (int side in new[] { -1, 1 })
                    {
                        float deg = side > 0 ? 0f : 180f;
                        TorsoPath(c, rings, a, 0.795f, deg, rings[0].Y, deg, 0.0015f, 0.012f * s);
                        TorsoPath(c, rings, b, 0.795f, deg, rings[0].Y, deg, 0.0015f, 0.0025f * s, 0.017f * s);
                        LegGeo l = LegOf(c, side);
                        var outL = new Vector3(side, 0f, 0f);
                        Vector3 lo = l.Ankle + new Vector3(0f, 0.03f, 0f);
                        Strip(c, a, l.Th, l.Hip + outL * (l.Rt + 0.0015f), l.Knee + outL * (l.Rk + 0.0015f), outL, 0.012f * s);
                        Strip(c, a, l.Sh, l.Knee + outL * (l.Rk + 0.0015f), lo + outL * (l.Ra + 0.0015f), outL, 0.012f * s);
                        var fwd = new Vector3(0f, 0f, 0.017f * s);
                        Strip(c, b, l.Th, l.Hip + outL * (l.Rt + 0.0015f) + fwd, l.Knee + outL * (l.Rk + 0.0015f) + fwd, outL, 0.0025f * s);
                        Strip(c, b, l.Sh, l.Knee + outL * (l.Rk + 0.0015f) + fwd, lo + outL * (l.Ra + 0.0015f) + fwd, outL, 0.0025f * s);
                    }
                    break;
                case "racing-collar":
                    Tube(c, a, Bone.Chest, new Vector3(0f, 0.815f * h, -0.004f), new Vector3(0f, 0.842f * h, -0.002f), 0.072f * s + cl, 0.066f * s + cl, 0.9f, 14);
                    Blob(c, Slot.Metal, Bone.Chest, new Vector3(0f, 0.83f * h, 0.066f * s + cl), new Vector3(0.006f, 0.006f, 0.003f) * s, Quaternion.identity, 3, 8);
                    break;
                case "shoulder-boards":
                    foreach (int side in new[] { -1, 1 })
                    {
                        Box(c, b, Bone.Chest, new Vector3(side * shoulders.Rx * 0.75f, 0.818f * h, 0f), new Vector3(0.036f, 0.005f, 0.05f) * s, Quaternion.Euler(0f, 0f, side * -12f));
                        Blob(c, Slot.Metal, Bone.Chest, new Vector3(side * shoulders.Rx * 0.5f, 0.825f * h, 0f), Vector3.one * 0.005f * s, Quaternion.identity, 3, 6);
                    }
                    break;
                case "yoke-block":
                    Hoop(c, rings, a, 0.738f, 0.8f, 0.0025f);
                    foreach (int side in new[] { -1, 1 })
                    {
                        ArmGeo g = ArmOf(c, side);
                        float gi = sk.Girth * c.S, capCl = g.Covered ? g.Cl : 0f;
                        Blob(c, a, g.Ua, g.Sh + new Vector3(-side * 0.012f, -0.014f, 0f) * c.S, new Vector3(0.05f * gi + capCl + 0.003f, 0.043f * gi + capCl + 0.003f, 0.048f * gi + capCl + 0.003f), Quaternion.identity, 6, 10, 0.45f, 1f);
                    }
                    break;
                case "hem-toggles":
                    foreach (int side in new[] { -1, 1 })
                    {
                        Vector3 p = TorsoPoint(c, rings, hem + 0.004f, side > 0 ? 55f : 125f, 0.004f);
                        Beam(c, Slot.Dark, Bone.Hips, p, p + new Vector3(0f, -0.022f * s, 0.004f), 0.0012f * s);
                        Tube(c, b, Bone.Hips, p + new Vector3(0f, -0.022f * s, 0.004f), p + new Vector3(0f, -0.038f * s, 0.005f), 0.0055f * s, 0.0055f * s, 1f, 8, true, true);
                    }
                    break;
                case "horizon-band":
                case "flat-line":
                {
                    // One line at one world height across the body and both arms — a horizon, or a flat (zero-frequency) trace.
                    bool horizon = shape == "horizon-band";
                    float y = horizon ? 0.735f : 0.72f, half = horizon ? 0.015f : 0.0032f;
                    Hoop(c, rings, a, y - half, y + half, 0.0025f);
                    if (horizon) Hoop(c, rings, b, y + half + 0.006f, y + half + 0.0095f, 0.0025f);
                    foreach (int side in new[] { -1, 1 })
                    {
                        ArmGeo g = ArmOf(c, side);
                        float t = Mathf.InverseLerp(g.Sh.y, g.El.y, y * h);
                        if (t <= 0f || t >= 1f) continue;
                        float len = (g.El - g.Sh).magnitude;
                        ArmBand(c, g, a, false, t, half * h, 0.003f);
                        if (horizon) ArmBand(c, g, b, false, Mathf.Clamp01(t - (half * h + 0.0078f * h) / len), 0.00175f * h, 0.003f);
                    }
                    break;
                }
                case "coat-belt":
                    Hoop(c, rings, Slot.Top, waist.Y - 0.012f, waist.Y + 0.006f, 0.006f);
                    Box(c, Slot.Metal, Bone.Spine, TorsoPoint(c, rings, waist.Y - 0.003f, 90f, 0.009f), new Vector3(0.02f, 0.016f, 0.003f) * s, Quaternion.identity);
                    Box(c, Slot.Top, Bone.Spine, TorsoPoint(c, rings, waist.Y - 0.003f, 90f, 0.012f), new Vector3(0.012f, 0.008f, 0.002f) * s, Quaternion.identity);
                    break;
                case "survey-ticks":
                    // Survey-staff marks: alternating A and B ticks down every sleeve and trouser seam.
                    foreach (int side in new[] { -1, 1 })
                    {
                        ArmGeo g = ArmOf(c, side);
                        int n = 0;
                        foreach (bool fore in new[] { false, true })
                        {
                            Vector3 p0 = fore ? g.El : g.Sh, p1 = fore ? Vector3.Lerp(g.El, g.Wr, 0.8f) : g.El;
                            Vector3 o = g.Out(fore ? g.El : g.Sh, fore ? g.Wr : g.El);
                            int count = Mathf.Max(4, Mathf.RoundToInt((p1 - p0).magnitude / 0.026f));
                            for (int m = 0; m < count; m++, n++)
                            {
                                float t0 = (m + 0.1f) / count, t1 = (m + 0.9f) / count;
                                float r0 = fore ? g.ForeR(t0 * 0.8f) : g.UpperR(t0), r1 = fore ? g.ForeR(t1 * 0.8f) : g.UpperR(t1);
                                Strip(c, n % 2 == 0 ? a : b, fore ? g.Fa : g.Ua, Vector3.Lerp(p0, p1, t0) + o * (r0 + 0.0015f), Vector3.Lerp(p0, p1, t1) + o * (r1 + 0.0015f), o, (n % 5 == 0 ? 0.008f : 0.005f) * s);
                            }
                        }
                        LegGeo l = LegOf(c, side);
                        var outL = new Vector3(side, 0f, 0f);
                        foreach (bool shin in new[] { false, true })
                        {
                            Vector3 p0 = shin ? l.Knee : l.Hip, p1 = shin ? l.Ankle + new Vector3(0f, 0.04f, 0f) : l.Knee;
                            float ra = shin ? l.Rk : l.Rt, rb = shin ? l.Ra : l.Rk;
                            int count = Mathf.Max(4, Mathf.RoundToInt((p1 - p0).magnitude / 0.03f));
                            for (int m = 0; m < count; m++, n++)
                            {
                                float t0 = (m + 0.1f) / count, t1 = (m + 0.9f) / count;
                                Strip(c, n % 2 == 0 ? a : b, shin ? l.Sh : l.Th, Vector3.Lerp(p0, p1, t0) + outL * (Mathf.Lerp(ra, rb, t0) + 0.0015f),
                                    Vector3.Lerp(p0, p1, t1) + outL * (Mathf.Lerp(ra, rb, t1) + 0.0015f), outL, (n % 5 == 0 ? 0.009f : 0.0055f) * s);
                            }
                        }
                    }
                    break;
                case "storm-collar":
                    Tube(c, b, Bone.Chest, new Vector3(0f, 0.812f * h, -0.006f), new Vector3(0f, 0.868f * h, -0.012f), 0.078f * s + cl, 0.074f * s + cl, 0.9f, 14);
                    Box(c, b, Bone.Chest, new Vector3(0.03f * s, 0.85f * h, 0.07f * s + cl), new Vector3(0.026f, 0.018f, 0.004f) * s, Quaternion.Euler(0f, 18f, 0f));
                    Blob(c, Slot.Metal, Bone.Chest, new Vector3(0.048f * s, 0.85f * h, 0.068f * s + cl), Vector3.one * 0.004f * s, Quaternion.identity, 3, 6);
                    break;

                // ---------------------------------------------------------------- accessories
                case "lantern-keychain":
                {
                    Vector3 clip = TorsoPoint(c, rings, waist.Y - 0.012f, 62f, 0.006f);
                    Tube(c, Slot.Metal, Bone.Hips, clip, clip + new Vector3(0f, -0.012f * s, 0f), 0.007f * s, 0.007f * s, 1f, 8);
                    Beam(c, Slot.Metal, Bone.Hips, clip + new Vector3(0f, -0.012f * s, 0f), clip + new Vector3(0f, -0.038f * s, 0.004f), 0.0012f * s);
                    Vector3 lc = clip + new Vector3(0f, -0.058f * s, 0.006f);
                    Box(c, a, Bone.Hips, lc + new Vector3(0f, 0.019f * s, 0f), new Vector3(0.012f, 0.003f, 0.012f) * s, Quaternion.identity);
                    Box(c, a, Bone.Hips, lc - new Vector3(0f, 0.019f * s, 0f), new Vector3(0.012f, 0.003f, 0.012f) * s, Quaternion.identity);
                    for (int cx = -1; cx <= 1; cx += 2)
                        for (int cz = -1; cz <= 1; cz += 2)
                            Beam(c, a, Bone.Hips, lc + new Vector3(cx * 0.0105f, -0.017f, cz * 0.0105f) * s, lc + new Vector3(cx * 0.0105f, 0.017f, cz * 0.0105f) * s, 0.0016f * s);
                    Box(c, Slot.Light, Bone.Hips, lc, new Vector3(0.009f, 0.016f, 0.009f) * s, Quaternion.identity); // the glowing panes
                    Blob(c, b, Bone.Hips, lc + new Vector3(0f, 0.025f * s, 0f), new Vector3(0.006f, 0.004f, 0.006f) * s, Quaternion.identity, 3, 8);
                    foreach (int e in new[] { -1, 1 })
                        Box(c, Slot.Metal, Bone.Hips, clip + new Vector3(e * 0.012f * s, -0.032f * s, 0.003f), new Vector3(0.0025f, 0.018f, 0.0012f) * s, Quaternion.Euler(0f, 0f, e * 14f));
                    break;
                }
                case "route-pin":
                {
                    Vector3 p = TorsoPoint(c, rings, 0.77f, 120f, 0.006f);
                    Blob(c, a, Bone.Chest, p, new Vector3(0.012f, 0.012f, 0.004f) * s, Quaternion.Euler(0f, -30f, 0f), 4, 10);
                    Tube(c, a, Bone.Chest, p + new Vector3(0f, -0.006f * s, 0f), p + new Vector3(0f, -0.026f * s, 0.001f), 0.0095f * s, 0.0008f, 0.4f, 8);
                    Blob(c, b, Bone.Chest, p + TorsoNormal(120f) * 0.0035f + new Vector3(0f, 0.001f * s, 0f), new Vector3(0.0045f, 0.0045f, 0.0015f) * s, Quaternion.Euler(0f, -30f, 0f), 3, 8);
                    break;
                }
                case "woven-cuff":
                {
                    // Right wrist: four rows of eight strands, alternating colours row to row (a basket weave).
                    ArmGeo g = ArmOf(c, 1);
                    float r = (g.LongSleeve ? g.Rw * 1.2f + g.Cl + 0.0075f : g.Rw + 0.0065f);
                    Vector3 axis = g.Down;
                    Frame(axis, out Vector3 u, out Vector3 v);
                    c.Mb.Bone = (int)g.Fa;
                    Vector3 start = g.Wr - axis * 0.046f * s;
                    for (int row = 0; row < 4; row++)
                    {
                        int r0 = Ring(c.Mb, start + axis * (row * 0.009f * s), u, v, r, r * 0.9f, 8);
                        int r1 = Ring(c.Mb, start + axis * ((row + 1) * 0.009f * s), u, v, r, r * 0.9f, 8);
                        int parity = row % 2;
                        Band(c.Mb, a, r0, r1, 8, j => j % 2 == parity);
                        Band(c.Mb, b, r0, r1, 8, j => j % 2 != parity);
                    }
                    break;
                }
                case "timing-slip-charm":
                {
                    Vector3 loop = TorsoPoint(c, rings, waist.Y - 0.012f, 122f, 0.006f);
                    for (int n = 0; n < 5; n++) // ball chain
                        Blob(c, Slot.Metal, Bone.Hips, loop + new Vector3(0f, -n * 0.007f * s, 0.001f * n), Vector3.one * 0.0026f * s, Quaternion.identity, 3, 6);
                    Vector3 card = loop + new Vector3(0f, -0.056f * s, 0.006f);
                    Quaternion tilt = Quaternion.Euler(0f, -32f, 6f);
                    Box(c, Slot.Light, Bone.Hips, card, new Vector3(0.017f, 0.025f, 0.0014f) * s, tilt);
                    Box(c, b, Bone.Hips, card + tilt * new Vector3(0f, 0.019f, 0.0016f) * s, new Vector3(0.016f, 0.0035f, 0.0006f) * s, tilt);
                    float[] widths = { 0.012f, 0.008f, 0.014f };
                    for (int n = 0; n < 3; n++) // printed sector bars
                        Box(c, a, Bone.Hips, card + tilt * new Vector3(-0.012f + widths[n], 0.006f - n * 0.009f, 0.0016f) * s, new Vector3(widths[n], 0.0025f, 0.0006f) * s, tilt);
                    break;
                }
                case "workshop-satchel":
                {
                    // Leather strap over the right shoulder, across the chest and back to the left hip; the bag at the left hip.
                    float strapTop = 0.79f, strapEnd = waist.Y - 0.02f;
                    TorsoPath(c, rings, b, strapTop, 52f, strapEnd, 150f, 0.006f, 0.013f * s);
                    TorsoPath(c, rings, b, strapTop, 308f, strapEnd, 210f, 0.006f, 0.013f * s);
                    Vector3 front = TorsoPoint(c, rings, strapTop, 52f, 0.006f), back = TorsoPoint(c, rings, strapTop, 308f, 0.006f);
                    var topF = new Vector3(0.088f * s * sk.Shoulder, 0.838f * h, 0.03f * s);
                    var topB = new Vector3(0.088f * s * sk.Shoulder, 0.838f * h, -0.03f * s);
                    Strip(c, b, Bone.Chest, front, topF, (Vector3.forward + Vector3.up).normalized, 0.013f * s);
                    Strip(c, b, Bone.Chest, topF, topB, Vector3.up, 0.013f * s);
                    Strip(c, b, Bone.Chest, topB, back, (Vector3.back + Vector3.up).normalized, 0.013f * s);
                    float bx = -(SideRadius(c, rings) + 0.045f * s);
                    var body = new Vector3(bx, (waist.Y - 0.075f) * h, 0f);
                    Box(c, a, Bone.Hips, body, new Vector3(0.042f, 0.085f, 0.125f) * s, Quaternion.identity);
                    Box(c, b, Bone.Hips, body + new Vector3(-0.044f * s, 0.035f * s, 0f), new Vector3(0.004f, 0.052f, 0.128f) * s, Quaternion.Euler(0f, 0f, -4f));
                    foreach (int e in new[] { -1, 1 })
                        Box(c, Slot.Metal, Bone.Hips, body + new Vector3(-0.05f * s, -0.008f * s, e * 0.06f * s), new Vector3(0.002f, 0.011f, 0.009f) * s, Quaternion.identity);
                    // Two pencils in a loop on the front end.
                    Box(c, b, Bone.Hips, body + new Vector3(0f, 0.02f * s, 0.128f * s), new Vector3(0.03f, 0.01f, 0.004f) * s, Quaternion.identity);
                    for (int n = 0; n < 2; n++)
                    {
                        Vector3 p = body + new Vector3((n == 0 ? -0.01f : 0.012f) * s, 0f, 0.132f * s);
                        Tube(c, n == 0 ? Slot.Light : Slot.Metal, Bone.Hips, p, p + new Vector3(0f, 0.11f * s, 0f), 0.0035f * s, 0.0035f * s, 1f, 6);
                        Tube(c, Slot.Dark, Bone.Hips, p + new Vector3(0f, 0.11f * s, 0f), p + new Vector3(0f, 0.124f * s, 0f), 0.0035f * s, 0.0005f, 1f, 6);
                    }
                    break;
                }
                case "road-atlas":
                {
                    Vector3 hand = sk[Bone.HandL];
                    Vector3 centre = hand + new Vector3(-0.012f, -0.075f, 0.065f) * s;
                    foreach (int e in new[] { -1, 1 }) // covers
                        Box(c, a, Bone.HandL, centre + new Vector3(e * 0.0095f * s, 0f, 0f), new Vector3(0.0016f, 0.112f, 0.082f) * s, Quaternion.identity);
                    Box(c, Slot.Light, Bone.HandL, centre + new Vector3(0f, 0f, 0.002f * s), new Vector3(0.008f, 0.107f, 0.079f) * s, Quaternion.identity); // page block
                    for (int n = 0; n < 6; n++) // spiral rings on the spine
                        Tube(c, Slot.Metal, Bone.HandL, centre + new Vector3(-0.012f, -0.09f + n * 0.036f, -0.083f) * s, centre + new Vector3(0.012f, -0.09f + n * 0.036f, -0.083f) * s, 0.0045f * s, 0.0045f * s, 1f, 6);
                    // A yellow route line across the outer cover.
                    Vector3 face = centre + new Vector3(-0.0115f * s, 0f, 0f);
                    Vector3[] route = { new Vector3(0f, -0.08f, -0.05f), new Vector3(0f, -0.02f, 0.02f), new Vector3(0f, 0.02f, -0.03f), new Vector3(0f, 0.075f, 0.05f) };
                    for (int n = 0; n + 1 < route.Length; n++)
                        Strip(c, b, Bone.HandL, face + route[n] * s, face + route[n + 1] * s, Vector3.left, 0.004f * s, 0.0012f);
                    Blob(c, Slot.Light, Bone.HandL, face + route[3] * s + new Vector3(-0.001f, 0f, 0f), new Vector3(0.0015f, 0.008f, 0.008f) * s, Quaternion.identity, 3, 8);
                    break;
                }
                case "travel-scarf":
                {
                    TorsoRing chest = rings[6];
                    float chestZ = rings[5].Rz + cl + 0.004f;
                    Blob(c, a, Bone.Chest, new Vector3(0f, 0.83f * h, -0.004f), new Vector3(0.086f * s + cl, 0.04f * s, 0.078f * s + cl), Quaternion.identity, 5, 16);
                    Blob(c, a, Bone.Chest, new Vector3(0f, 0.808f * h, -0.002f), new Vector3(0.092f * s + cl, 0.026f * s, 0.083f * s + cl), Quaternion.identity, 4, 16);
                    Blob(c, b, Bone.Chest, new Vector3(0f, 0.83f * h, -0.004f), new Vector3(0.0875f * s + cl, 0.04f * s, 0.0795f * s + cl), Quaternion.identity, 5, 16, 0.42f, 0.5f);
                    // The front end, striped and fringed.
                    Quaternion fr = Quaternion.Euler(0f, 0f, 4f);
                    Vector3 front = new Vector3(0.035f * s, 0.7f * h, chestZ + 0.012f);
                    Box(c, a, Bone.Chest, front, new Vector3(0.027f, 0.12f, 0.007f) * s, fr);
                    foreach (float y in new[] { -0.07f, -0.085f })
                        Box(c, b, Bone.Chest, front + fr * new Vector3(0f, y, 0f) * s, new Vector3(0.0275f, 0.005f, 0.0075f) * s, fr);
                    for (int n = 0; n < 5; n++)
                    {
                        Vector3 top = front + fr * new Vector3(-0.022f + n * 0.011f, -0.12f, 0f) * s;
                        Strip(c, a, Bone.Chest, top, top + new Vector3(0f, -0.02f * s, 0f), Vector3.forward, 0.0018f * s, 0.0075f * s);
                    }
                    // The other end thrown over the left shoulder, hanging down the back.
                    Beam(c, a, Bone.Chest, new Vector3(-0.07f * s, 0.84f * h, 0.03f * s), new Vector3(-0.085f * s, 0.825f * h, -chest.Rz - cl - 0.01f), 0.012f * s);
                    Vector3 back = new Vector3(-0.08f * s, 0.73f * h, -chest.Rz - cl - 0.014f);
                    Box(c, a, Bone.Chest, back, new Vector3(0.027f, 0.1f, 0.007f) * s, Quaternion.identity);
                    foreach (float y in new[] { -0.055f, -0.07f })
                        Box(c, b, Bone.Chest, back + new Vector3(0f, y * s, 0f), new Vector3(0.0275f, 0.005f, 0.0075f) * s, Quaternion.identity);
                    for (int n = 0; n < 5; n++)
                    {
                        Vector3 top = back + new Vector3((-0.022f + n * 0.011f) * s, -0.1f * s, 0f);
                        Strip(c, a, Bone.Chest, top, top + new Vector3(0f, -0.02f * s, 0f), Vector3.back, 0.0018f * s, 0.0075f * s);
                    }
                    break;
                }
                case "receiver":
                {
                    // On the left hip, turned a third of the way to the front so the dial shows past the arm.
                    Quaternion rot = Quaternion.Euler(0f, 30f, 0f);
                    float rr = SideRadius(c, rings) + 0.021f * s;
                    var body = new Vector3(-Mathf.Cos(30f * Mathf.Deg2Rad) * rr, (waist.Y - 0.055f) * h, Mathf.Sin(30f * Mathf.Deg2Rad) * rr);
                    System.Func<float, float, float, Vector3> at = (x, y, z) => body + rot * new Vector3(x, y, z) * s;
                    Box(c, Slot.Metal, Bone.Hips, at(0.012f, 0.03f, 0f), new Vector3(0.003f, 0.02f, 0.008f) * s, rot); // belt clip
                    Box(c, a, Bone.Hips, body, new Vector3(0.017f, 0.044f, 0.034f) * s, rot);
                    Blob(c, b, Bone.Hips, at(-0.0175f, 0.017f, 0f), new Vector3(0.0015f, 0.015f, 0.015f) * s, rot, 3, 12); // tuning dial
                    Box(c, Slot.Dark, Bone.Hips, at(-0.019f, 0.019f, 0.002f), new Vector3(0.0006f, 0.011f, 0.0012f) * s, rot * Quaternion.Euler(28f, 0f, 0f)); // needle
                    for (int n = 0; n < 4; n++) // grille
                        Box(c, Slot.Metal, Bone.Hips, at(-0.018f, -0.012f - n * 0.007f, 0f), new Vector3(0.001f, 0.0018f, 0.024f) * s, rot);
                    Tube(c, Slot.Metal, Bone.Hips, at(0f, 0.044f, -0.018f), at(0f, 0.052f, -0.018f), 0.006f * s, 0.006f * s, 1f, 8, false, true); // knob
                    Beam(c, Slot.Metal, Bone.Hips, at(0f, 0.044f, 0.024f), at(-0.01f, 0.17f, 0.045f), 0.0012f * s); // aerial
                    break;
                }
            }
        }
    }
}
