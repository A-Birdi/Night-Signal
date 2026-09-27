using System.Collections.Generic;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Art
{
    /// <summary>Materials used by generated cars (paint is a per-instance livery material).</summary>
    public struct CarMaterials
    {
        public Material Paint, Glass, Trim, HeadLamp, TailLamp, Chrome, Rubber, Rim, Paint2, Accent;
        /// <summary>Body submeshes 0–7: paint, glass, trim, head lamps, tail lamps, chrome, second paint zone, accent parts.</summary>
        public Material[] BodyArray => new[] { Paint, Glass, Trim, HeadLamp, TailLamp, Chrome, Paint2 != null ? Paint2 : Paint, Accent != null ? Accent : Trim };
        public Material[] WheelArray => new[] { Rubber, Rim };
    }

    /// <summary>
    /// Builds an original car body from a <see cref="CarBodyDef"/> and the chassis dimensions: a lofted lower body
    /// (plan taper, nose/hood/cowl/deck/tail profile, tuck-under, fender flares), a lofted greenhouse (windshield
    /// rake, roof, rear-window rake by style) with paint pillars and roof panel, lamps, grille, mirrors, exhaust,
    /// spoiler and features. Model space: ground at y = 0, axles at the chassis' FrontAxleZ/RearAxleZ.
    /// Body submeshes: 0 paint, 1 glass, 2 trim, 3 head lamps, 4 tail lamps, 5 chrome, 6 second paint zone (two-tone), 7 accent
    /// (body-kit parts). A <see cref="CarAppearance"/> selects the visible families; it never changes the simulation.
    /// Wheel: 0 tyre, 1 rim.
    /// </summary>
    public static class CarBodyGenerator
    {
        const int Stations = 80; // ~5 cm spacing so wheel arches read as curves

        sealed class Profile
        {
            public CarBodyDef D;
            public VehicleParams P;
            public CarAppearance A;
            public float Zf, Zr, ZWs, ZRoofF, ZRoofR, ZRw, H, HalfW;
        }

        const int Paint2 = 6, Accent = 7;

        public static Mesh BuildBody(CarBodyDef d, VehicleParams p, CarAppearance appearance = null)
        {
            Profile pr = Layout(d, p);
            pr.A = appearance ?? new CarAppearance();
            var mb = new MeshBuilder(8);
            LowerBody(mb, pr);
            if (d.Style == "roadster") Roadster(mb, pr);
            else Greenhouse(mb, pr);
            Lamps(mb, pr);
            Details(mb, pr);
            Mesh m = mb.Build($"{d.Id}_body");
            m.RecalculateNormals();
            m.RecalculateTangents();
            return m;
        }

        /// <summary>Centre of the rear number plate in model space (the plate panel faces −z).</summary>
        public static Vector3 RearPlateCentre(CarBodyDef d, VehicleParams p)
        {
            Profile pr = Layout(d, p);
            return new Vector3(0f, Bottom(pr, pr.Zr) + 0.18f, pr.Zr - 0.02f);
        }

        public static Mesh BuildWheel(CarBodyDef d, CarAppearance appearance = null)
        {
            string style = appearance?.RimStyle ?? LegacyRim(d.RimStyle);
            float fraction = appearance != null && appearance.RimFraction > 0f ? appearance.RimFraction : d.RimFraction;
            var mb = new MeshBuilder(2);
            float r = d.WheelRadius, w = d.TyreWidth, rim = r * Mathf.Clamp(fraction, 0.5f, 0.78f);
            Quaternion toX = Quaternion.Euler(0f, 0f, 90f);
            // Tyre: tread cylinder closed by both sidewalls (rim to tread), so no view looks into a hollow tube.
            mb.AddCylinder(0, new Vector3(w * 0.5f, 0f, 0f), r, w, 28, toX);
            Annulus(mb, 0, w * 0.5f, rim, r, 32, true);
            Annulus(mb, 0, -w * 0.5f, rim, r, 32, false);
            // Dark barrel face behind the spokes (reads as the wheel's depth and brake shadow), then the rim design.
            Annulus(mb, 0, w * 0.5f - 0.05f, 0f, rim, 32, true);
            mb.AddCylinder(1, new Vector3(w * 0.5f + 0.004f, 0f, 0f), rim, 0.02f, 28, toX);
            mb.AddCylinder(1, new Vector3(w * 0.55f, 0f, 0f), rim * 0.22f, 0.07f, 12, toX); // hub
            RimFace(mb, style, w, rim);
            Mesh m = mb.Build($"{d.Id}_wheel");
            m.RecalculateNormals();
            return m;
        }

        /// <summary>A flat ring in the y–z plane at <paramref name="x"/> (a disc when <paramref name="inner"/> is 0), facing ±x.</summary>
        static void Annulus(MeshBuilder mb, int sub, float x, float inner, float outer, int segments, bool facePlusX)
        {
            Vector3 n = facePlusX ? Vector3.right : Vector3.left;
            int start = mb.VertexCount;
            for (int i = 0; i <= segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                var dir = new Vector3(0f, Mathf.Cos(a), Mathf.Sin(a));
                mb.AddVertex(new Vector3(x, 0f, 0f) + dir * inner, n, Vector2.zero);
                mb.AddVertex(new Vector3(x, 0f, 0f) + dir * outer, n, Vector2.zero);
            }
            for (int i = 0; i < segments; i++)
            {
                int a0 = start + i * 2, b0 = a0 + 1, a1 = a0 + 2, b1 = a0 + 3;
                if (facePlusX) mb.AddQuad(sub, a0, b0, b1, a1);
                else mb.AddQuad(sub, a0, a1, b1, b0);
            }
        }

        /// <summary>The authored body's rim style in the shared rim vocabulary.</summary>
        static string LegacyRim(string style)
        {
            switch (style)
            {
                case "6": return "6-spoke";
                case "mesh": return "mesh";
                case "split": return "split";
                case "dish": return "dish";
                default: return "5-spoke";
            }
        }

        /// <summary>The eight shared rim designs (Addendum 01 §13): distinct spoke layouts on the same hub and barrel.</summary>
        static void RimFace(MeshBuilder mb, string style, float w, float rim)
        {
            Quaternion toX = Quaternion.Euler(0f, 0f, 90f);
            void Spoke(float angle, float thick, float lengthFraction, float depth, float twistDeg)
            {
                Vector3 dir = new Vector3(0f, Mathf.Cos(angle), Mathf.Sin(angle));
                // Box axes: x tangential (spoke width), y radial (length), z axial (depth toward the hub).
                Quaternion rot = Quaternion.LookRotation(Vector3.right, dir) * Quaternion.Euler(0f, twistDeg, 0f);
                mb.AddBox(1, new Vector3(w * 0.5f + 0.012f, 0f, 0f) + dir * rim * (0.2f + lengthFraction * 0.5f), new Vector3(thick, rim * lengthFraction * 0.5f, depth), rot);
            }
            void Ring(float radius, float depth) => mb.AddCylinder(1, new Vector3(w * 0.5f + 0.004f, 0f, 0f), radius, depth, 28, toX);
            Ring(rim, 0.02f); // barrel lip
            mb.AddCylinder(1, new Vector3(w * 0.55f, 0f, 0f), rim * 0.22f, 0.07f, 12, toX); // hub
            switch (style)
            {
                case "6-spoke":
                    for (int i = 0; i < 6; i++) Spoke(i * Mathf.PI * 2f / 6f, 0.028f, 0.8f, 0.012f, 0f);
                    break;
                case "mesh":
                    for (int i = 0; i < 12; i++)
                    {
                        Spoke(i * Mathf.PI * 2f / 12f, 0.009f, 0.8f, 0.01f, 22f);
                        Spoke(i * Mathf.PI * 2f / 12f, 0.009f, 0.8f, 0.01f, -22f);
                    }
                    break;
                case "split":
                    for (int i = 0; i < 5; i++)
                    {
                        float a = i * Mathf.PI * 2f / 5f;
                        Spoke(a - 0.1f, 0.013f, 0.8f, 0.012f, 0f);
                        Spoke(a + 0.1f, 0.013f, 0.8f, 0.012f, 0f);
                    }
                    break;
                case "dish":
                    // Solid flat face with a ring of raised bolts and an outer step.
                    Annulus(mb, 1, w * 0.5f + 0.01f, rim * 0.24f, rim * 0.93f, 32, true);
                    Annulus(mb, 1, w * 0.5f + 0.022f, rim * 0.8f, rim * 0.97f, 32, true);
                    for (int i = 0; i < 8; i++) Spoke(i * Mathf.PI * 2f / 8f, 0.016f, 0.12f, 0.02f, 0f);
                    break;
                case "turbofan":
                    Annulus(mb, 1, w * 0.5f + 0.008f, rim * 0.24f, rim * 0.9f, 32, true);
                    for (int i = 0; i < 14; i++) Spoke(i * Mathf.PI * 2f / 14f, 0.02f, 0.62f, 0.02f, 35f);
                    break;
                case "multi-spoke":
                    for (int i = 0; i < 16; i++) Spoke(i * Mathf.PI * 2f / 16f, 0.01f, 0.82f, 0.012f, 0f);
                    Ring(rim * 0.45f, 0.014f);
                    break;
                case "3-spoke":
                    for (int i = 0; i < 3; i++) Spoke(i * Mathf.PI * 2f / 3f + 0.4f, 0.05f, 0.82f, 0.016f, 0f);
                    break;
                default: // 5-spoke
                    for (int i = 0; i < 5; i++) Spoke(i * Mathf.PI * 2f / 5f, 0.03f, 0.8f, 0.012f, 0f);
                    break;
            }
        }

        static Profile Layout(CarBodyDef d, VehicleParams p)
        {
            var pr = new Profile { D = d, P = p, H = p.HeightM, HalfW = p.WidthM * 0.5f };
            float overhang = Mathf.Max(0.6f, p.LengthM - p.WheelbaseM);
            float fShare = d.FrontOverhang / Mathf.Max(0.01f, d.FrontOverhang + d.RearOverhang);
            pr.Zf = p.FrontAxleZ + overhang * fShare;
            pr.Zr = p.RearAxleZ - overhang * (1f - fShare);
            pr.ZWs = p.FrontAxleZ - d.WindshieldBaseFromFrontAxle;
            float rise = Mathf.Max(0.2f, pr.H - d.CowlHeight);
            pr.ZRoofF = pr.ZWs - rise * Mathf.Tan(d.WindshieldRakeDeg * Mathf.Deg2Rad);
            pr.ZRoofR = pr.ZRoofF - d.RoofLength;
            float rearRise = Mathf.Max(0.15f, pr.H - d.DeckHeight);
            pr.ZRw = Mathf.Max(pr.Zr + 0.12f, pr.ZRoofR - rearRise * Mathf.Tan(d.RearWindowRakeDeg * Mathf.Deg2Rad));
            return pr;
        }

        // ---------------------------------------------------------------- profiles

        static float HalfWidth(Profile pr, float z)
        {
            CarBodyDef d = pr.D;
            float w = pr.HalfW;
            float front = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(pr.Zf - 0.9f, pr.Zf, z));
            float rear = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(pr.Zr + 0.7f, pr.Zr, z));
            w *= 1f - d.PlanTaperNose * front * front - d.PlanTaperTail * rear * rear;
            float flareF = Mathf.Exp(-Sq((z - pr.P.FrontAxleZ) / 0.55f));
            float flareR = Mathf.Exp(-Sq((z - pr.P.RearAxleZ) / 0.55f));
            float body = w - d.FenderFlare;
            return body + d.FenderFlare * Mathf.Max(flareF, flareR * (d.Features.Contains("boxed-haunches") ? 1.8f : 1f));
        }

        /// <summary>Top line of the lower body (hood, sill under the windows, deck, tail).</summary>
        static float TopLine(Profile pr, float z)
        {
            CarBodyDef d = pr.D;
            if (z >= pr.ZWs)
            {
                float s = Mathf.InverseLerp(pr.ZWs, pr.Zf, z);
                return Mathf.Lerp(d.CowlHeight, d.NoseHeight, Mathf.Pow(s, 1.7f));
            }
            if (z >= pr.ZRw)
                return Mathf.Lerp(d.DeckHeight, d.CowlHeight, Mathf.InverseLerp(pr.ZRw, pr.ZWs, z));
            float t = Mathf.InverseLerp(pr.ZRw, pr.Zr, z);
            return Mathf.Lerp(d.DeckHeight, d.TailHeight, Mathf.Pow(t, 1.4f));
        }

        static float Bottom(Profile pr, float z)
        {
            float lift = 0.1f * Mathf.Max(Mathf.InverseLerp(pr.Zf - 0.35f, pr.Zf, z), Mathf.InverseLerp(pr.Zr + 0.3f, pr.Zr, z));
            float y = pr.D.Clearance + lift;
            // Wheel arches: the lower edge follows a circle just above each tyre so the wheels show.
            float archR = pr.D.WheelRadius + 0.06f;
            foreach (float axle in new[] { pr.P.FrontAxleZ, pr.P.RearAxleZ })
            {
                float dz = z - axle;
                if (Mathf.Abs(dz) < archR)
                    y = Mathf.Max(y, pr.D.WheelRadius + Mathf.Sqrt(archR * archR - dz * dz));
            }
            return y;
        }

        // ---------------------------------------------------------------- lower body

        static void LowerBody(MeshBuilder mb, Profile pr)
        {
            var rings = new List<int>();
            int ringSize = 0;
            for (int i = 0; i <= Stations; i++)
            {
                float z = Mathf.Lerp(pr.Zr, pr.Zf, i / (float)Stations);
                float w = HalfWidth(pr, z), yb = Bottom(pr, z), yt = TopLine(pr, z);
                float crown = pr.D.Crown, tuck = pr.D.Tumblehome;
                float stripe = Mathf.Min(StripeHalfWidth, w * 0.3f);
                // Ring points 3–5 and 9 also bound the paint zones: lower two-tone below 3, side stripe 4–5, hood stripe 9–10.
                var pts = new List<Vector2>
                {
                    new Vector2(0f, yb),
                    new Vector2(w * 0.84f, yb),
                    new Vector2(w * 0.97f, yb + 0.1f),
                    new Vector2(w, yb + 0.38f * (yt - yb)),
                    new Vector2(w * (1f - tuck * 0.06f), yb + 0.55f * (yt - yb)),
                    new Vector2(w * (1f - tuck * 0.09f), yb + 0.66f * (yt - yb)),
                    new Vector2(w * (1f - tuck * 0.15f), yt - 0.16f * (yt - yb)),
                    new Vector2(w * (0.94f - tuck * 0.1f), yt - 0.025f),
                    new Vector2(w * 0.55f, yt + crown * 0.75f),
                    new Vector2(stripe, yt + crown * 0.97f),
                    new Vector2(0f, yt + crown),
                };
                for (int k = pts.Count - 2; k >= 1; k--) pts.Add(new Vector2(-pts[k].x, pts[k].y));
                ringSize = pts.Count;
                rings.Add(mb.VertexCount);
                foreach (Vector2 q in pts)
                    mb.AddVertex(new Vector3(q.x, q.y, z), Vector3.up, new Vector2(z * 0.5f, q.y));
            }
            for (int i = 0; i < rings.Count - 1; i++)
            for (int k = 0; k < ringSize; k++)
            {
                int k1 = (k + 1) % ringSize;
                mb.AddQuad(LowerZone(pr.A.TwoTone, k), rings[i] + k, rings[i] + k1, rings[i + 1] + k1, rings[i + 1] + k);
            }
            Cap(mb, pr, rings[0], ringSize, pr.Zr, false);
            Cap(mb, pr, rings[rings.Count - 1], ringSize, pr.Zf, true);
        }

        const float StripeHalfWidth = 0.17f;

        /// <summary>
        /// Paint submesh of lower-body ring segment <paramref name="k"/> (20 segments: 0–9 right side bottom → top centre,
        /// 10–19 left side top centre → bottom).
        /// </summary>
        static int LowerZone(string twoTone, int k)
        {
            switch (twoTone)
            {
                case "lower": return k <= 2 || k >= 17 ? Paint2 : 0;
                case "side-stripe": return k == 4 || k == 15 ? Paint2 : 0;
                case "hood-stripe": return k == 9 || k == 10 ? Paint2 : 0;
                default: return 0;
            }
        }

        static void Cap(MeshBuilder mb, Profile pr, int ring, int n, float z, bool front)
        {
            float yc = (Bottom(pr, z) + TopLine(pr, z)) * 0.5f;
            int c = mb.AddVertex(new Vector3(0f, yc, z), front ? Vector3.forward : Vector3.back, Vector2.zero);
            for (int k = 0; k < n; k++)
            {
                int k1 = (k + 1) % n;
                // Ring order runs clockwise when viewed from the front, counter-clockwise from the rear.
                if (front) mb.AddTriangle(0, c, ring + k, ring + k1);
                else mb.AddTriangle(0, c, ring + k1, ring + k);
            }
        }

        // ---------------------------------------------------------------- greenhouse

        static float CabinTop(Profile pr, float z)
        {
            if (z >= pr.ZRoofF) return Mathf.Lerp(pr.H, TopLine(pr, pr.ZWs), Mathf.InverseLerp(pr.ZRoofF, pr.ZWs, z));
            if (z >= pr.ZRoofR) return pr.H + pr.D.Crown * 0.5f * Mathf.Sin(Mathf.InverseLerp(pr.ZRoofR, pr.ZRoofF, z) * Mathf.PI);
            return Mathf.Lerp(TopLine(pr, pr.ZRw), pr.H, Mathf.InverseLerp(pr.ZRw, pr.ZRoofR, z));
        }

        static void Greenhouse(MeshBuilder mb, Profile pr)
        {
            const int n = 24;
            float z0 = pr.ZRw, z1 = pr.ZWs;
            var rows = new List<int>();
            var zs = new List<float>();
            for (int i = 0; i <= n; i++)
            {
                float z = Mathf.Lerp(z0, z1, i / (float)n);
                float w = HalfWidth(pr, z) * (1f - pr.D.Tumblehome * 0.25f);
                float yb = TopLine(pr, z) - 0.01f, yt = Mathf.Max(yb + 0.02f, CabinTop(pr, z));
                float wt = HalfWidth(pr, z) * pr.D.RoofTaper;
                float st = Mathf.Min(StripeHalfWidth, wt * 0.5f);
                rows.Add(mb.VertexCount);
                zs.Add(z);
                mb.AddVertex(new Vector3(-w, yb, z), Vector3.left, Vector2.zero);
                mb.AddVertex(new Vector3(-wt, yt, z), Vector3.up, Vector2.zero);
                mb.AddVertex(new Vector3(-st, yt + pr.D.Crown * 0.2f, z), Vector3.up, Vector2.zero);
                mb.AddVertex(new Vector3(st, yt + pr.D.Crown * 0.2f, z), Vector3.up, Vector2.zero);
                mb.AddVertex(new Vector3(wt, yt, z), Vector3.up, Vector2.zero);
                mb.AddVertex(new Vector3(w, yb, z), Vector3.right, Vector2.zero);
            }
            float bPillar = Mathf.Lerp(pr.ZRoofR, pr.ZRoofF, pr.D.Style == "wagon" ? 0.62f : 0.45f);
            for (int i = 0; i < n; i++)
            {
                int a = rows[i], b = rows[i + 1];
                float zm = (zs[i] + zs[i + 1]) * 0.5f;
                bool windshield = zm > pr.ZRoofF, rearWindow = zm < pr.ZRoofR, roof = !windshield && !rearWindow;
                bool pillar = windshield || Mathf.Abs(zm - bPillar) < 0.07f ||
                              (rearWindow && pr.D.Style != "hatch" && pr.D.Style != "wagon" && pr.D.Style != "liftback");
                string tone = pr.A.TwoTone;
                int paintedTop = tone == "roof" ? Paint2 : 0;
                int side = pillar ? (tone == "roof" ? Paint2 : 0) : 1;
                // Clockwise when seen from outside (a = rear station, b = front station).
                mb.AddQuad(side, a + 0, b + 0, b + 1, a + 1);  // left side
                mb.AddQuad(side, a + 5, a + 4, b + 4, b + 5);  // right side
                int top = roof ? paintedTop : 1;
                int centre = roof ? (tone == "hood-stripe" ? Paint2 : paintedTop) : 1;
                mb.AddQuad(top, a + 1, b + 1, b + 2, a + 2);     // roof / glass, left
                mb.AddQuad(centre, a + 2, b + 2, b + 3, a + 3);  // roof / glass, centre (stripe)
                mb.AddQuad(top, a + 3, b + 3, b + 4, a + 4);     // roof / glass, right
            }
            if (pr.D.Features.Contains("roof-rails"))
                foreach (float x in new[] { -1f, 1f })
                    mb.AddBox(2, new Vector3(x * HalfWidth(pr, (pr.ZRoofF + pr.ZRoofR) * 0.5f) * pr.D.RoofTaper * 0.85f, pr.H + 0.06f,
                        (pr.ZRoofF + pr.ZRoofR) * 0.5f), new Vector3(0.02f, 0.025f, (pr.ZRoofF - pr.ZRoofR) * 0.5f), Quaternion.identity);
        }

        static void Roadster(MeshBuilder mb, Profile pr)
        {
            // Raked windshield in a frame, two roll hoops behind the seats; the cabin stays open.
            float zb = pr.ZWs, zt = pr.ZWs - 0.3f, yb = TopLine(pr, pr.ZWs), yt = yb + 0.36f;
            float w = HalfWidth(pr, zb) * 0.86f;
            mb.AddFlatQuad(1, new Vector3(-w, yb, zb), new Vector3(-w * 0.95f, yt, zt), new Vector3(w * 0.95f, yt, zt), new Vector3(w, yb, zb), Vector2.one);
            mb.AddFlatQuad(1, new Vector3(w, yb, zb), new Vector3(w * 0.95f, yt, zt), new Vector3(-w * 0.95f, yt, zt), new Vector3(-w, yb, zb), Vector2.one);
            mb.AddBox(2, new Vector3(0f, yt, zt), new Vector3(w * 0.96f, 0.02f, 0.02f), Quaternion.identity);
            float zh = pr.ZWs - 1.25f;
            foreach (float x in new[] { -0.35f, 0.35f })
                mb.AddBox(5, new Vector3(x, TopLine(pr, zh) + 0.2f, zh), new Vector3(0.03f, 0.2f, 0.03f), Quaternion.identity);
            mb.AddBox(2, new Vector3(0f, TopLine(pr, zh + 0.5f) - 0.02f, zh + 0.5f), new Vector3(HalfWidth(pr, zh) * 0.8f, 0.02f, 0.6f), Quaternion.identity);
        }

        // ---------------------------------------------------------------- lamps, grille, details

        static void Lamps(MeshBuilder mb, Profile pr)
        {
            CarBodyDef d = pr.D;
            float zf = pr.Zf - 0.02f, zr = pr.Zr + 0.02f;
            float noseTop = TopLine(pr, pr.Zf - 0.12f);
            float wF = HalfWidth(pr, pr.Zf - 0.15f), wR = HalfWidth(pr, pr.Zr + 0.12f);
            foreach (int s in new[] { -1, 1 })
            {
                switch (d.HeadLamps)
                {
                    case "round":
                    case "oval":
                        mb.AddCylinder(3, new Vector3(s * (wF - 0.22f), noseTop - 0.1f, zf - 0.02f), d.HeadLamps == "oval" ? 0.085f : 0.075f, 0.04f, 16, Quaternion.Euler(90f, 0f, 0f));
                        break;
                    case "slim":
                        mb.AddBox(3, new Vector3(s * (wF - 0.28f), noseTop - 0.06f, zf), new Vector3(0.2f, 0.025f, 0.02f), Quaternion.Euler(0f, s * -12f, 0f));
                        break;
                    case "stacked":
                        mb.AddBox(3, new Vector3(s * (wF - 0.18f), noseTop - 0.07f, zf), new Vector3(0.1f, 0.04f, 0.02f), Quaternion.identity);
                        mb.AddBox(3, new Vector3(s * (wF - 0.18f), noseTop - 0.17f, zf), new Vector3(0.1f, 0.04f, 0.02f), Quaternion.identity);
                        break;
                    case "triangle":
                        mb.AddBox(3, new Vector3(s * (wF - 0.25f), noseTop - 0.08f, zf), new Vector3(0.17f, 0.05f, 0.02f), Quaternion.Euler(0f, 0f, s * 14f));
                        break;
                    case "wedge":
                        mb.AddBox(3, new Vector3(s * (wF - 0.3f), noseTop - 0.04f, zf - 0.05f), new Vector3(0.22f, 0.03f, 0.05f), Quaternion.Euler(-12f, s * -18f, 0f));
                        break;
                    default: // rect
                        mb.AddBox(3, new Vector3(s * (wF - 0.24f), noseTop - 0.09f, zf), new Vector3(0.16f, 0.06f, 0.02f), Quaternion.identity);
                        break;
                }

                float tailY = TopLine(pr, pr.Zr + 0.1f) - 0.12f;
                switch (d.TailLamps)
                {
                    case "oval":
                        mb.AddCylinder(4, new Vector3(s * (wR - 0.25f), tailY, zr + 0.02f), 0.07f, 0.03f, 14, Quaternion.Euler(-90f, 0f, 0f));
                        break;
                    case "twin-slot":
                        mb.AddBox(4, new Vector3(s * (wR - 0.22f), tailY + 0.03f, zr), new Vector3(0.14f, 0.018f, 0.02f), Quaternion.identity);
                        mb.AddBox(4, new Vector3(s * (wR - 0.22f), tailY - 0.03f, zr), new Vector3(0.14f, 0.018f, 0.02f), Quaternion.identity);
                        break;
                    case "divided":
                        mb.AddBox(4, new Vector3(s * (wR - 0.18f), tailY, zr), new Vector3(0.1f, 0.05f, 0.02f), Quaternion.identity);
                        mb.AddBox(4, new Vector3(s * (wR - 0.42f), tailY, zr), new Vector3(0.1f, 0.05f, 0.02f), Quaternion.identity);
                        break;
                    case "round":
                        mb.AddCylinder(4, new Vector3(s * (wR - 0.2f), tailY, zr + 0.02f), 0.06f, 0.03f, 14, Quaternion.Euler(-90f, 0f, 0f));
                        mb.AddCylinder(4, new Vector3(s * (wR - 0.38f), tailY, zr + 0.02f), 0.06f, 0.03f, 14, Quaternion.Euler(-90f, 0f, 0f));
                        break;
                    case "wrap":
                        mb.AddBox(4, new Vector3(s * (wR - 0.12f), tailY, zr + 0.08f), new Vector3(0.12f, 0.045f, 0.1f), Quaternion.Euler(0f, s * 30f, 0f));
                        break;
                    case "bar":
                        break; // full-width bar added once below
                    default: // block
                        mb.AddBox(4, new Vector3(s * (wR - 0.24f), tailY, zr), new Vector3(0.16f, 0.055f, 0.02f), Quaternion.identity);
                        break;
                }
            }
            if (d.TailLamps == "bar")
                mb.AddBox(4, new Vector3(0f, TopLine(pr, pr.Zr + 0.1f) - 0.1f, zr), new Vector3(wR - 0.12f, 0.02f, 0.02f), Quaternion.identity);
            // Grille and front/rear plates in trim.
            mb.AddBox(2, new Vector3(0f, TopLine(pr, pr.Zf - 0.1f) - 0.2f, zf + 0.005f), new Vector3(wF * 0.42f, 0.07f, 0.015f), Quaternion.identity);
            mb.AddBox(2, new Vector3(0f, Bottom(pr, pr.Zr) + 0.18f, zr - 0.005f), new Vector3(0.26f, 0.06f, 0.012f), Quaternion.identity);
        }

        static void Details(MeshBuilder mb, Profile pr)
        {
            CarBodyDef d = pr.D;
            // Mirrors at the A-pillar base.
            foreach (int s in new[] { -1, 1 })
            {
                float z = pr.ZWs - 0.08f;
                mb.AddBox(0, new Vector3(s * (HalfWidth(pr, z) + 0.07f), TopLine(pr, z) + 0.1f, z), new Vector3(0.07f, 0.045f, 0.05f), Quaternion.identity);
            }
            CarAppearance a = pr.A;
            // Exhaust tips (visible options; the engine part decides the sound and power, not these).
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
                mb.AddCylinder(5, new Vector3(x, exY, exZ), radius, 0.14f, 12, Quaternion.Euler(-90f, 0f, 0f));
            BodyKit(mb, pr);
            // Rear aero.
            float wR = HalfWidth(pr, pr.Zr + 0.2f);
            float deckY = TopLine(pr, pr.Zr + 0.25f);
            string aero = a.RearAero == "stock" ? d.Spoiler : a.RearAero == "lip-spoiler" ? "lip" : a.RearAero;
            switch (aero)
            {
                case "lip":
                    mb.AddBox(0, new Vector3(0f, deckY + 0.02f, pr.Zr + 0.12f), new Vector3(wR * 0.8f, 0.015f, 0.06f), Quaternion.Euler(-12f, 0f, 0f));
                    break;
                case "ducktail":
                    mb.AddBox(0, new Vector3(0f, deckY + 0.05f, pr.Zr + 0.15f), new Vector3(wR * 0.85f, 0.04f, 0.12f), Quaternion.Euler(-20f, 0f, 0f));
                    break;
                case "blade":
                    mb.AddBox(0, new Vector3(0f, TopLine(pr, pr.ZRw) + 0.08f, pr.ZRw - 0.05f), new Vector3(wR * 0.9f, 0.012f, 0.09f), Quaternion.Euler(-6f, 0f, 0f));
                    break;
                case "wing":
                    int wingSub = a.RearAero == "wing" ? Accent : 2;
                    mb.AddBox(wingSub, new Vector3(0f, deckY + 0.24f, pr.Zr + 0.25f), new Vector3(wR * 0.95f, 0.015f, 0.14f), Quaternion.Euler(-8f, 0f, 0f));
                    foreach (int s in new[] { -1, 1 })
                        mb.AddBox(2, new Vector3(s * wR * 0.6f, deckY + 0.12f, pr.Zr + 0.25f), new Vector3(0.015f, 0.12f, 0.06f), Quaternion.identity);
                    break;
                case "gt-wing":
                    // Swan-neck mounts over the deck, a wide main plane with endplates and a gurney strip.
                    float wingY = deckY + 0.36f, wingZ = pr.Zr + 0.32f;
                    mb.AddBox(Accent, new Vector3(0f, wingY, wingZ), new Vector3(wR * 1.02f, 0.013f, 0.19f), Quaternion.Euler(-11f, 0f, 0f));
                    mb.AddBox(Accent, new Vector3(0f, wingY + 0.04f, wingZ - 0.17f), new Vector3(wR * 1.02f, 0.02f, 0.006f), Quaternion.identity);
                    foreach (int s in new[] { -1, 1 })
                    {
                        mb.AddBox(Accent, new Vector3(s * wR * 1.03f, wingY - 0.02f, wingZ), new Vector3(0.008f, 0.08f, 0.21f), Quaternion.identity);
                        mb.AddBox(2, new Vector3(s * wR * 0.45f, wingY + 0.02f, wingZ + 0.05f), new Vector3(0.012f, 0.03f, 0.05f), Quaternion.identity);
                        mb.AddBox(2, new Vector3(s * wR * 0.45f, deckY + 0.18f, wingZ + 0.13f), new Vector3(0.012f, 0.18f, 0.03f), Quaternion.Euler(18f, 0f, 0f));
                    }
                    break;
            }
            if (d.Features.Contains("hood-scoop"))
                mb.AddBox(2, new Vector3(0f, TopLine(pr, pr.ZWs + 0.5f) + 0.035f, pr.ZWs + 0.5f), new Vector3(0.22f, 0.035f, 0.18f), Quaternion.Euler(-4f, 0f, 0f));
            if (d.Features.Contains("side-intakes"))
                foreach (int s in new[] { -1, 1 })
                    mb.AddBox(2, new Vector3(s * (HalfWidth(pr, pr.P.RearAxleZ + 0.9f) - 0.005f), 0.55f, pr.P.RearAxleZ + 0.9f), new Vector3(0.01f, 0.1f, 0.26f), Quaternion.identity);
            if (d.Features.Contains("diffuser"))
                for (int i = -2; i <= 2; i++)
                    mb.AddBox(2, new Vector3(i * 0.18f, Bottom(pr, pr.Zr) + 0.05f, pr.Zr + 0.22f), new Vector3(0.008f, 0.05f, 0.22f), Quaternion.identity);
            if (d.Features.Contains("side-cooling"))
                foreach (int s in new[] { -1, 1 })
                    mb.AddBox(2, new Vector3(s * (HalfWidth(pr, pr.P.RearAxleZ + 1.1f) - 0.01f), 0.62f, pr.P.RearAxleZ + 1.1f), new Vector3(0.012f, 0.05f, 0.35f), Quaternion.identity);
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
                    mb.AddBox(2, new Vector3(0f, ybF + 0.08f, zf - 0.005f), new Vector3(wF * 0.5f, 0.045f, 0.012f), Quaternion.identity);
                    break;
                case "track":
                    mb.AddBox(Accent, new Vector3(0f, ybF - 0.016f, zf + 0.02f), new Vector3(wF * 0.97f, 0.012f, 0.14f), Quaternion.identity);
                    foreach (int s in new[] { -1, 1 })
                    {
                        mb.AddBox(2, new Vector3(s * wF * 0.55f, ybF + 0.09f, zf - 0.005f), new Vector3(wF * 0.2f, 0.06f, 0.012f), Quaternion.identity);
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
                        mb.AddBox(2, new Vector3(s * wR * 0.62f, ybR + 0.05f, zr - 0.03f), new Vector3(0.12f, 0.022f, 0.015f), Quaternion.identity);
                    break;
            }
            if (a.Side == "skirt" || a.Side == "sculpted")
            {
                float archR = pr.D.WheelRadius + 0.08f;
                float z0 = pr.P.RearAxleZ + archR, z1 = pr.P.FrontAxleZ - archR;
                if (z1 > z0)
                {
                    float zm = (z0 + z1) * 0.5f;
                    int skirtSub = a.TwoTone == "lower" ? Paint2 : 0;
                    foreach (int s in new[] { -1, 1 })
                    {
                        mb.AddBox(skirtSub, new Vector3(s * (HalfWidth(pr, zm) * 0.99f), Bottom(pr, zm) + 0.035f, zm), new Vector3(0.03f, 0.045f, (z1 - z0) * 0.5f), Quaternion.identity);
                        if (a.Side == "sculpted")
                        {
                            mb.AddBox(Accent, new Vector3(s * (HalfWidth(pr, zm) * 0.99f + 0.02f), Bottom(pr, zm) - 0.004f, zm), new Vector3(0.02f, 0.006f, (z1 - z0) * 0.5f), Quaternion.identity);
                            float zv = pr.P.FrontAxleZ - archR - 0.08f;
                            for (int k = 0; k < 3; k++)
                                mb.AddBox(2, new Vector3(s * (HalfWidth(pr, zv) + 0.002f), Bottom(pr, zv) + 0.16f + k * 0.045f, zv), new Vector3(0.008f, 0.012f, 0.1f), Quaternion.identity);
                        }
                    }
                }
            }
        }

        static float Sq(float v) => v * v;
    }
}
