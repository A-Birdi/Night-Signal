using System.Collections.Generic;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Art
{
    /// <summary>Materials used by generated cars (paint is a per-instance livery material).</summary>
    public struct CarMaterials
    {
        public Material Paint, Glass, Trim, HeadLamp, TailLamp, Chrome, Rubber, Rim, Paint2, Accent, Interior;
        /// <summary>
        /// Body submeshes 0–8: paint, glass, trim, head lamps, tail lamps, chrome, second paint zone, accent parts, interior
        /// (seat cloth seen through the glass).
        /// </summary>
        public Material[] BodyArray => new[]
        {
            Paint, Glass, Trim, HeadLamp, TailLamp, Chrome, Paint2 != null ? Paint2 : Paint, Accent != null ? Accent : Trim,
            Interior != null ? Interior : Trim,
        };
        public Material[] WheelArray => new[] { Rubber, Rim };
    }

    /// <summary>
    /// Builds an original car body from a <see cref="CarBodyDef"/> and the chassis dimensions: a lofted lower body
    /// (plan taper, nose/hood/cowl/deck/tail profile, tuck-under, fender flares, rounded ends, wheel arches cut to the
    /// authored shape), a lofted greenhouse (windshield rake, roof, rear-window rake by style) with A-pillars, drip rails,
    /// belt mouldings, blacked-out pillars and side glass, then the details in <c>CarBodyDetail.cs</c>: lamp clusters and
    /// grille openings conformed to the body surface, arch lips with wheel-well liners, mirrors on stalks, shut lines and
    /// handles, the interior silhouette seen through the glass, exhaust, aero and body-kit families.
    /// Model space: ground at y = 0, axles at the chassis' FrontAxleZ/RearAxleZ.
    /// Body submeshes: 0 paint, 1 glass, 2 trim, 3 head lamps, 4 tail lamps, 5 chrome, 6 second paint zone (two-tone), 7 accent
    /// (body-kit parts), 8 interior. A <see cref="CarAppearance"/> selects the visible families; it never changes the simulation.
    /// Wheel: 0 tyre (and the dark barrel/brake disc), 1 rim.
    /// </summary>
    public static partial class CarBodyGenerator
    {
        const int FullStations = 80; // ~5 cm spacing so wheel arches read as curves
        /// <summary>Loft stations for the body being built (fewer for the distant levels of detail).</summary>
        static int Stations = FullStations;
        /// <summary>Level of detail being built: 0 full, 1 mid distance, 2 far.</summary>
        static int buildLod;

        internal sealed class Profile
        {
            public CarBodyDef D;
            public VehicleParams P;
            public CarAppearance A;
            public float Zf, Zr, ZWs, ZRoofF, ZRoofR, ZRw, H, HalfW;
            // Authored shape switches read once (the loft queries run thousands of times per body).
            public bool BoxedHaunches, SquareArches, FlaredArches;
        }

        const int PaintSub = 0, GlassSub = 1, TrimSub = 2, HeadSub = 3, TailSub = 4, ChromeSub = 5, Paint2 = 6, Accent = 7, InteriorSub = 8;
        const int BodySubmeshes = 9;

        /// <summary>
        /// The body mesh. <paramref name="openCabin"/> leaves out the lower body's top skin over the cabin (the lid the
        /// greenhouse sits on) so the fitted cockpit below it can be seen from the driver's seat; the exterior views use the
        /// closed body, whose lid is dark interior with the seats, dash and wheel standing on it as seen through the glass.
        /// </summary>
        public static Mesh BuildBody(CarBodyDef d, VehicleParams p, CarAppearance appearance = null, bool openCabin = false) =>
            BuildBody(d, p, appearance, openCabin, 0);

        /// <summary>
        /// A body at a level of detail (spec §15 vehicle LOD tiers). 0 is the full body. 1, for mid distance, lofts 28 stations
        /// instead of 80, draws the lamp and trim patches and the arch lips about half as finely, and leaves out the feature
        /// lines and the steering-wheel rim (the seats and dash still show through the glass). 2, for far away, lofts 14
        /// stations, samples the glasshouse half as finely, draws patches a third as finely and leaves out the mirrors and
        /// the interior as well. Every level keeps the fascia and the lamps, so a car still reads (and its lamps still glow)
        /// at night; the silhouette, the height and length and the paint zones are the same.
        /// </summary>
        public static Mesh BuildBody(CarBodyDef d, VehicleParams p, CarAppearance appearance, bool openCabin, int lod)
        {
            int keepStations = Stations, keepLod = buildLod;
            Stations = lod <= 0 ? FullStations : lod == 1 ? 28 : 14;
            buildLod = lod;
            try
            {
                Profile pr = Layout(d, p);
                pr.A = appearance ?? new CarAppearance();
                var mb = new MeshBuilder(BodySubmeshes);
                LowerBody(mb, pr, openCabin ? CabinRear(pr) : float.PositiveInfinity);
                if (d.Style == "roadster") Roadster(mb, pr);
                else Greenhouse(mb, pr);
                Fascia(mb, pr);
                ArchLips(mb, pr);
                if (lod < 1) Lines(mb, pr);
                if (lod < 2)
                {
                    Mirrors(mb, pr);
                    if (!openCabin) InteriorSilhouette(mb, pr);
                }
                Details(mb, pr);
                Mesh m = mb.Build(lod == 0 ? $"{d.Id}_body" : $"{d.Id}_body_lod{lod}");
                m.RecalculateNormals();
                m.RecalculateTangents();
                return m;
            }
            finally
            {
                Stations = keepStations;
                buildLod = keepLod;
            }
        }

        /// <summary>The loft of one car body, for placing things on its surface (decals).</summary>
        public static BodySurface Surface(CarBodyDef d, VehicleParams p) => new BodySurface(Layout(d, p));

        /// <summary>
        /// Body-surface queries from the same loft as the mesh: a decal zone frame (u across, v along/up, both 0..1) with the
        /// outward normal and a tangent (the decal "right"), and conforming any point of a decal back onto the surface so
        /// large shapes follow the curvature instead of standing off it.
        /// </summary>
        public sealed class BodySurface
        {
            readonly Profile pr;

            internal BodySurface(Profile profile) => pr = profile;

            float SideX(float z, float y)
            {
                float yb = Bottom(pr, z), yt = TopLine(pr, z);
                float t = Mathf.Clamp01((y - yb) / Mathf.Max(0.01f, yt - yb));
                float w = HalfWidth(pr, z);
                return t >= 0.38f ? w * (1f - pr.D.Tumblehome * 0.15f * (t - 0.38f) / 0.46f) : w * (0.97f + 0.03f * t / 0.38f);
            }

            float HoodY(float x, float z)
            {
                float wTop = Mathf.Max(0.01f, HalfWidth(pr, z) * 0.55f);
                return TopLine(pr, z) + pr.D.Crown * (1f - 0.25f * Sq(Mathf.Clamp(x / wTop, -1.5f, 1.5f)));
            }

            public bool Frame(string zone, float u, float v, out Vector3 pos, out Vector3 normal, out Vector3 tangent)
            {
                CarBodyDef d = pr.D;
                u = Mathf.Clamp01(u);
                v = Mathf.Clamp01(v);
                pos = normal = tangent = Vector3.zero;
                switch (zone)
                {
                    case "left":
                    case "right":
                    {
                        float side = zone == "right" ? 1f : -1f;
                        float z = Mathf.Lerp(pr.Zr + 0.3f, pr.Zf - 0.3f, u);
                        float yb = Bottom(pr, z), yt = TopLine(pr, z);
                        float y = yb + Mathf.Lerp(0.12f, 0.82f, v) * (yt - yb);
                        pos = new Vector3(side * SideX(z, y), y, z);
                        normal = new Vector3(side, (y - yb) / Mathf.Max(0.01f, yt - yb) > 0.38f ? d.Tumblehome * 0.3f : 0f, 0f).normalized;
                        tangent = side > 0f ? Vector3.back : Vector3.forward; // reads front-to-back on both sides
                        return true;
                    }
                    case "hood":
                    {
                        float z = Mathf.Lerp(pr.ZWs + 0.12f, pr.Zf - 0.18f, v);
                        float wTop = HalfWidth(pr, z) * 0.55f;
                        float x = Mathf.Lerp(-wTop, wTop, u);
                        float slope = (TopLine(pr, z + 0.05f) - TopLine(pr, z - 0.05f)) / 0.1f;
                        pos = new Vector3(x, HoodY(x, z), z);
                        normal = new Vector3(0f, 1f, -slope).normalized;
                        tangent = Vector3.right;
                        return true;
                    }
                    case "roof":
                    {
                        if (d.Style == "roadster") return false;
                        float z = Mathf.Lerp(pr.ZRoofR + 0.06f, pr.ZRoofF - 0.06f, v);
                        float wRoof = HalfWidth(pr, z) * d.RoofTaper * 0.8f;
                        pos = new Vector3(Mathf.Lerp(-wRoof, wRoof, u), CabinTop(pr, z) + d.Crown * 0.2f, z);
                        normal = Vector3.up;
                        tangent = Vector3.right;
                        return true;
                    }
                    case "rear":
                    case "front":
                    {
                        bool front = zone == "front";
                        float z = front ? pr.Zf : pr.Zr;
                        float w = HalfWidth(pr, z + (front ? -0.05f : 0.05f)) * 0.8f;
                        float yb = Bottom(pr, z) + 0.08f, yt = TopLine(pr, z) - 0.04f;
                        pos = new Vector3(front ? Mathf.Lerp(-w, w, u) : Mathf.Lerp(w, -w, u), Mathf.Lerp(yb, yt, v), z);
                        normal = front ? Vector3.forward : Vector3.back;
                        tangent = front ? Vector3.right : Vector3.left;
                        return true;
                    }
                }
                return false;
            }

            /// <summary>
            /// Whether a decal point lies on the paintable panel of its zone — not past the body's ends or edges, not in a wheel
            /// arch, not up on the glass. Only the axes a zone keeps are tested, so it holds before and after <see cref="Conform"/>.
            /// </summary>
            public bool OnZone(string zone, Vector3 q)
            {
                switch (zone)
                {
                    case "left":
                    case "right":
                        if (q.z < pr.Zr + 0.06f || q.z > pr.Zf - 0.06f) return false;
                        return q.y >= Bottom(pr, q.z) + 0.015f && q.y <= TopLine(pr, q.z) - 0.015f;
                    case "hood":
                        if (q.z < pr.ZWs + 0.06f || q.z > pr.Zf - 0.1f) return false;
                        return Mathf.Abs(q.x) <= HalfWidth(pr, q.z) * 0.82f;
                    case "roof":
                        if (pr.D.Style == "roadster" || q.z < pr.ZRoofR + 0.02f || q.z > pr.ZRoofF - 0.02f) return false;
                        return Mathf.Abs(q.x) <= HalfWidth(pr, q.z) * pr.D.RoofTaper * 0.9f;
                    case "front":
                    case "rear":
                    {
                        float z = zone == "front" ? pr.Zf : pr.Zr;
                        float yb = Bottom(pr, z), yt = TopLine(pr, z), yc = (yb + yt) * 0.5f;
                        return Mathf.Abs(q.x) <= HalfWidth(pr, z) * 0.9f && q.y >= yc + (yb - yc) * 0.88f && q.y <= yc + (yt - yc) * 0.88f;
                    }
                }
                return true;
            }

            /// <summary>Moves a point of a decal in <paramref name="zone"/> back onto the body surface, <paramref name="lift"/> above it.</summary>
            public Vector3 Conform(string zone, Vector3 q, float lift)
            {
                float z = Mathf.Clamp(q.z, pr.Zr + 0.02f, pr.Zf - 0.02f);
                float zRoof = Mathf.Clamp(q.z, pr.ZRoofR, pr.ZRoofF);
                switch (zone)
                {
                    case "left": return new Vector3(-(SideX(z, q.y) + lift), q.y, z);
                    case "right": return new Vector3(SideX(z, q.y) + lift, q.y, z);
                    case "hood": return new Vector3(q.x, HoodY(q.x, z) + lift, z);
                    case "roof": return new Vector3(q.x, CabinTop(pr, zRoof) + pr.D.Crown * 0.2f + lift, zRoof);
                    case "front": return new Vector3(q.x, q.y, pr.Zf + lift);
                    case "rear": return new Vector3(q.x, q.y, pr.Zr - lift);
                    default: return q;
                }
            }
        }

        /// <summary>
        /// Model-space driving-camera and cabin anchors from the same loft as the body mesh (Addendum 03 §2): the seated eye
        /// (driver side as authored), the hood and bumper viewpoints, and the cabin shape the cockpit is built into. The ground
        /// is y = 0, +z forward, like the body mesh.
        /// </summary>
        public sealed class CabinFrame
        {
            public float NoseZ, TailZ, WindshieldBaseZ, RoofFrontZ, RoofRearZ, RearWindowZ;
            /// <summary>Rear end of the fitted cabin (behind the seats); the open-cabin body is open from here to the windscreen.</summary>
            public float CabinRearZ;
            public float CowlY, RoofY, NoseY, DeckY, HalfWidth, SillY;
            /// <summary>+1 = right-hand drive, −1 = left-hand drive.</summary>
            public int DriverSide;
            public bool OpenTop;
            public Vector3 Eye, Hood, Bumper;
            internal Profile Loft;

            /// <summary>Half width of the cabin interior at a station (inside the body side).</summary>
            public float InteriorHalfWidth(float z) => CarBodyGenerator.HalfWidth(Loft, z) * (1f - Loft.D.Tumblehome * 0.25f) - 0.06f;
            /// <summary>Belt line (top of the doors / base of the side glass) at a station.</summary>
            public float BeltY(float z) => TopLine(Loft, z);
            /// <summary>Underside of the roof at a station (open tops: the belt line).</summary>
            public float RoofUndersideY(float z) => OpenTop ? TopLine(Loft, z) : CabinTop(Loft, z) - 0.03f;
        }

        public static CabinFrame Cabin(CarBodyDef d, VehicleParams p) => Cabin(Layout(d, p));

        static CabinFrame Cabin(Profile pr)
        {
            CarBodyDef d = pr.D;
            int side = d.DriverSide == "left" ? -1 : 1;
            float roofMid = Mathf.Lerp(pr.ZRoofR, pr.ZRoofF, 0.62f);
            float eyeZ = Mathf.Lerp(pr.ZRoofR, pr.ZRoofF, 0.55f);
            float cowl = TopLine(pr, pr.ZWs);
            var c = new CabinFrame
            {
                Loft = pr, NoseZ = pr.Zf, TailZ = pr.Zr, WindshieldBaseZ = pr.ZWs, RoofFrontZ = pr.ZRoofF, RoofRearZ = pr.ZRoofR, RearWindowZ = pr.ZRw,
                CabinRearZ = CabinRear(pr),
                CowlY = cowl, RoofY = pr.H, NoseY = d.NoseHeight, DeckY = d.DeckHeight, HalfWidth = pr.HalfW, SillY = d.Clearance + 0.12f,
                DriverSide = side, OpenTop = d.Style == "roadster",
            };
            float halfInside = c.InteriorHalfWidth(eyeZ);
            // Seated eye: over the driver's seat, a hand's width under the roof and low enough that the windscreen header sits
            // well above the horizon (about 10 degrees or more), with the cowl some 12-17 degrees below it.
            float eyeY = c.OpenTop ? cowl + 0.42f : Mathf.Min(Mathf.Min(CabinTop(pr, pr.ZRoofF) - 0.15f, CabinTop(pr, roofMid) - 0.16f), cowl + 0.45f);
            c.Eye = new Vector3(side * halfInside * 0.42f, eyeY, eyeZ);
            // Hood: over the cowl, the bonnet in the lower part of the view.
            c.Hood = new Vector3(0f, cowl + 0.3f, pr.ZWs + 0.12f);
            // Bumper / road: just ahead of the nose, low but clear of the road and of the car's own body.
            c.Bumper = new Vector3(0f, Mathf.Max(0.32f, d.NoseHeight * 0.62f), pr.Zf + 0.08f);
            return c;
        }

        /// <summary>Centre of the rear number plate in model space (the plate panel faces −z).</summary>
        public static Vector3 RearPlateCentre(CarBodyDef d, VehicleParams p)
        {
            Profile pr = Layout(d, p);
            return new Vector3(0f, Bottom(pr, pr.Zr) + 0.18f, pr.Zr - 0.02f);
        }

        // ---------------------------------------------------------------- wheels

        public static Mesh BuildWheel(CarBodyDef d, CarAppearance appearance = null)
        {
            string style = appearance?.RimStyle ?? LegacyRim(d.RimStyle);
            float fraction = appearance != null && appearance.RimFraction > 0f ? appearance.RimFraction : d.RimFraction;
            var mb = new MeshBuilder(2);
            float r = d.WheelRadius, w = d.TyreWidth, rim = r * Mathf.Clamp(fraction, 0.5f, 0.78f), hw = w * 0.5f;
            float wall = r - rim;
            // Tyre: a revolved section — beads at the rim, sidewalls bulging a little past the tread width, rounded shoulders
            // and two circumferential grooves in the tread, so the tyre reads as rubber rather than a cylinder.
            var tyre = new List<Vector2>
            {
                new Vector2(hw - 0.006f, rim - 0.004f),
                new Vector2(hw + 0.002f, rim + wall * 0.3f),
                new Vector2(hw + 0.006f, rim + wall * 0.62f),
                new Vector2(hw + 0.002f, r - 0.02f),
                new Vector2(hw - 0.01f, r - 0.004f),
                new Vector2(hw * 0.42f, r),
                new Vector2(hw * 0.36f, r - 0.007f),
                new Vector2(hw * 0.3f, r),
                new Vector2(-hw * 0.3f, r),
                new Vector2(-hw * 0.36f, r - 0.007f),
                new Vector2(-hw * 0.42f, r),
                new Vector2(-hw + 0.01f, r - 0.004f),
                new Vector2(-hw - 0.002f, r - 0.02f),
                new Vector2(-hw - 0.006f, rim + wall * 0.62f),
                new Vector2(-hw - 0.002f, rim + wall * 0.3f),
                new Vector2(-hw + 0.006f, rim - 0.004f),
            };
            Revolve(mb, 0, tyre, 40);
            // Dark barrel face deep behind the spokes (the wheel's depth), the brake disc and its hat in front of it.
            Annulus(mb, 0, hw - 0.06f, 0f, rim, 32, true);
            Annulus(mb, 0, hw - 0.04f, rim * 0.34f, rim * 0.84f, 32, true);
            mb.AddCylinder(0, new Vector3(hw - 0.02f, 0f, 0f), rim * 0.34f, 0.02f, 20, Quaternion.Euler(0f, 0f, 90f));
            // Rim: the barrel's inner surface and the outer lip that meets the tyre bead.
            Revolve(mb, 1, new List<Vector2>
            {
                new Vector2(hw - 0.06f, rim - 0.014f),
                new Vector2(hw + 0.001f, rim - 0.014f),
                new Vector2(hw + 0.009f, rim - 0.006f),
                new Vector2(hw + 0.007f, rim + 0.004f),
            }, 40);
            RimFace(mb, style, w, rim);
            Mesh m = mb.Build($"{d.Id}_wheel");
            m.RecalculateNormals();
            return m;
        }

        /// <summary>
        /// A surface of revolution about the x axis from a (x, radius) section, faces outward from the section's travel
        /// direction (left of travel in the section plane is inside).
        /// </summary>
        static void Revolve(MeshBuilder mb, int sub, IList<Vector2> section, int segments)
        {
            int n = section.Count, start = mb.VertexCount;
            for (int j = 0; j < segments; j++)
            {
                float a = j * Mathf.PI * 2f / segments;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                foreach (Vector2 q in section) mb.AddVertex(new Vector3(q.x, q.y * c, q.y * s), Vector3.right, new Vector2(j / (float)segments, q.x));
            }
            Vector3 P(Vector2 q, float a) => new Vector3(q.x, q.y * Mathf.Cos(a), q.y * Mathf.Sin(a));
            for (int i = 0; i < n - 1; i++)
            {
                Vector2 d2 = section[i + 1] - section[i];
                // Decide the winding once per ring from the first segment: outward is (dRadius, −dx) in the section plane.
                float a0 = 0f, a1 = Mathf.PI * 2f / segments, am = a1 * 0.5f;
                Vector3 want = new Vector3(d2.y, -d2.x * Mathf.Cos(am), -d2.x * Mathf.Sin(am));
                Vector3 p0 = P(section[i], a0), p1 = P(section[i + 1], a0), p2 = P(section[i + 1], a1);
                bool forward = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), want) >= 0f;
                for (int j = 0; j < segments; j++)
                {
                    int ja = start + j * n, jb = start + ((j + 1) % segments) * n;
                    if (forward) mb.AddQuad(sub, ja + i, ja + i + 1, jb + i + 1, jb + i);
                    else mb.AddQuad(sub, ja + i, jb + i, jb + i + 1, ja + i + 1);
                }
            }
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

        /// <summary>
        /// The eight shared rim designs (Addendum 01 §13): distinct spoke layouts on the same hub and barrel, with a centre cap
        /// and five lug nuts on the hub.
        /// </summary>
        static void RimFace(MeshBuilder mb, string style, float w, float rim)
        {
            Quaternion toX = Quaternion.Euler(0f, 0f, 90f), outX = Quaternion.Euler(0f, 0f, -90f);
            void Spoke(float angle, float thick, float lengthFraction, float depth, float twistDeg)
            {
                Vector3 dir = new Vector3(0f, Mathf.Cos(angle), Mathf.Sin(angle));
                // Box axes: x tangential (spoke width), y radial (length), z axial (depth toward the hub).
                Quaternion rot = Quaternion.LookRotation(Vector3.right, dir) * Quaternion.Euler(0f, twistDeg, 0f);
                mb.AddBox(1, new Vector3(w * 0.5f + 0.012f, 0f, 0f) + dir * rim * (0.2f + lengthFraction * 0.5f), new Vector3(thick, rim * lengthFraction * 0.5f, depth), rot);
            }
            void Ring(float radius, float depth) => mb.AddCylinder(1, new Vector3(w * 0.5f + 0.004f, 0f, 0f), radius, depth, 28, toX);
            // Hub: a short drum from the disc hat out to the spoke face, closed by a face plate, lug nuts and a centre cap.
            float hubX = w * 0.55f;
            mb.AddCylinder(1, new Vector3(hubX, 0f, 0f), rim * 0.22f, 0.07f, 16, toX);
            Annulus(mb, 1, hubX, 0f, rim * 0.22f, 16, true);
            for (int i = 0; i < 5; i++)
            {
                float a = i * Mathf.PI * 2f / 5f + 0.3f;
                mb.AddCylinder(1, new Vector3(hubX, Mathf.Cos(a) * rim * 0.14f, Mathf.Sin(a) * rim * 0.14f), 0.009f, 0.012f, 6, outX);
            }
            mb.AddCylinder(1, new Vector3(hubX, 0f, 0f), rim * 0.075f, 0.016f, 12, outX);
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

        // ---------------------------------------------------------------- layout and profiles

        static Profile Layout(CarBodyDef d, VehicleParams p)
        {
            var pr = new Profile
            {
                D = d, P = p, H = p.HeightM, HalfW = p.WidthM * 0.5f,
                BoxedHaunches = d.Features.Contains("boxed-haunches"), SquareArches = d.Arches == "square", FlaredArches = d.Arches == "flared",
            };
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
            return body + d.FenderFlare * Mathf.Max(flareF, flareR * (pr.BoxedHaunches ? 1.8f : 1f));
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

        /// <summary>Lower edge of the body: the sill, lifted toward the ends, and each wheel-arch opening above the tyres.</summary>
        static float Bottom(Profile pr, float z)
        {
            float y = SillY(pr, z);
            y = Mathf.Max(y, ArchTop(pr, z - pr.P.FrontAxleZ));
            return Mathf.Max(y, ArchTop(pr, z - pr.P.RearAxleZ));
        }

        /// <summary>Radius of the wheel-arch opening: a hand's width above the tyre (more for flared arches).</summary>
        static float ArchRadius(Profile pr) => pr.D.WheelRadius + (pr.FlaredArches ? 0.07f : 0.06f);

        /// <summary>Height of the arch opening's edge <paramref name="dz"/> from the axle (−∞ clear of the arch).</summary>
        static float ArchTop(Profile pr, float dz)
        {
            float r = ArchRadius(pr), t = Mathf.Abs(dz) / r;
            if (t >= 1f) return float.NegativeInfinity;
            float shape = pr.SquareArches ? Mathf.Pow(1f - t * t * t * t, 0.25f) : Mathf.Sqrt(1f - t * t);
            return pr.D.WheelRadius + r * shape;
        }

        /// <summary>A point on the arch opening's edge at angle θ (0 = toward the front, π/2 = the top), relative to the wheel centre (dz, dy).</summary>
        static Vector2 ArchEdge(Profile pr, float theta, float radius)
        {
            float c = Mathf.Cos(theta), s = Mathf.Sin(theta);
            if (pr.SquareArches)
            {
                c = Mathf.Sign(c) * Mathf.Sqrt(Mathf.Abs(c));
                s = Mathf.Sign(s) * Mathf.Sqrt(Mathf.Abs(s));
            }
            return new Vector2(c * radius, s * radius);
        }

        /// <summary>1 inside a wheel-arch opening, blending to 0 just clear of it (the side runs straight down to a clean cut there).</summary>
        static float ArchOpening(Profile pr, float z)
        {
            float r = ArchRadius(pr);
            float f = Mathf.InverseLerp(r * 1.12f, r * 0.9f, Mathf.Abs(z - pr.P.FrontAxleZ));
            float b = Mathf.InverseLerp(r * 1.12f, r * 0.9f, Mathf.Abs(z - pr.P.RearAxleZ));
            return Mathf.SmoothStep(0f, 1f, Mathf.Max(f, b));
        }

        const float EndRoundLength = 0.075f;

        /// <summary>Rounding of the nose and tail: the last few centimetres of the loft pull in (x) and toward mid-height (y).</summary>
        static void EndRound(Profile pr, float z, out float sx, out float sy)
        {
            float s = Mathf.Max(Mathf.InverseLerp(pr.Zf - EndRoundLength, pr.Zf, z), Mathf.InverseLerp(pr.Zr + EndRoundLength, pr.Zr, z));
            float f = 1f - Mathf.Sqrt(Mathf.Max(0f, 1f - s * s));
            sx = 1f - 0.05f * f;
            sy = 1f - 0.08f * f;
        }

        const int HalfRingPoints = 11;

        /// <summary>
        /// Right half of the lower-body cross-section at station <paramref name="z"/>: 11 points from the bottom centre round
        /// the side to the top centre. Points 3–5 and 9 also bound the paint zones (lower two-tone below 3, side stripe 4–5,
        /// hood stripe 9–10).
        /// </summary>
        static void HalfRing(Profile pr, float z, Vector2[] pts)
        {
            CarBodyDef d = pr.D;
            float w = HalfWidth(pr, z), yb = Bottom(pr, z), yt = TopLine(pr, z);
            float h = yt - yb, crown = d.Crown, tuck = d.Tumblehome;
            float arch = ArchOpening(pr, z);
            float stripe = Mathf.Min(StripeHalfWidth, w * 0.3f);
            pts[0] = new Vector2(0f, yb);
            pts[1] = new Vector2(w * Mathf.Lerp(0.84f, 0.95f, arch), yb);
            pts[2] = new Vector2(w * Mathf.Lerp(0.97f, 0.99f, arch), yb + Mathf.Min(0.1f, 0.25f * h));
            pts[3] = new Vector2(w, yb + 0.38f * h);
            pts[4] = new Vector2(w * (1f - tuck * 0.06f), yb + 0.55f * h);
            pts[5] = new Vector2(w * (1f - tuck * 0.09f), yb + 0.66f * h);
            pts[6] = new Vector2(w * (1f - tuck * 0.15f), yt - 0.16f * h);
            pts[7] = new Vector2(w * (0.94f - tuck * 0.1f), yt - 0.025f);
            pts[8] = new Vector2(w * 0.55f, yt + crown * 0.75f);
            pts[9] = new Vector2(stripe, yt + crown * 0.97f);
            pts[10] = new Vector2(0f, yt + crown);
            EndRound(pr, z, out float sx, out float sy);
            if (sx < 1f)
            {
                float yc = (yb + yt) * 0.5f;
                for (int k = 0; k < HalfRingPoints; k++) pts[k] = new Vector2(pts[k].x * sx, yc + (pts[k].y - yc) * sy);
            }
        }

        // ---------------------------------------------------------------- lower body

        /// <summary>Rear end of the fitted cabin: behind the seats under a roof; a roadster's tub is shorter.</summary>
        static float CabinRear(Profile pr) => pr.D.Style == "roadster" ? pr.ZWs - 1.35f : pr.ZRoofR - 0.25f;

        /// <param name="openFromZ">Leave out the top skin (ring segments 7-12) between this station and the windscreen base.</param>
        static void LowerBody(MeshBuilder mb, Profile pr, float openFromZ)
        {
            var stationZ = new List<float>();
            for (int i = 0; i <= Stations; i++) stationZ.Add(Mathf.Lerp(pr.Zr, pr.Zf, i / (float)Stations));
            // Extra stations in the rounded ends, and either side of each arch's vertical cut so the cut stays vertical.
            foreach (float e in new[] { 0.045f, 0.022f, 0.008f })
            {
                stationZ.Add(pr.Zf - e);
                stationZ.Add(pr.Zr + e);
            }
            float archR = ArchRadius(pr);
            foreach (float axle in new[] { pr.P.FrontAxleZ, pr.P.RearAxleZ })
            foreach (float e in new[] { -archR - 0.0015f, -archR + 0.0015f, archR - 0.0015f, archR + 0.0015f })
                stationZ.Add(axle + e);
            stationZ.Sort();
            var rings = new List<int>();
            var half = new Vector2[HalfRingPoints];
            const int ringSize = HalfRingPoints * 2 - 2; // logical points: right 0..10, then left 9..1
            // Creases: the shoulder (point 6) and the sill (point 2) get two vertices each, so the side, the shoulder
            // round-over and the tuck-under meet at crisp feature lines instead of one smooth-shaded slab.
            var first = new int[ringSize];
            var second = new int[ringSize];
            int perRing = 0;
            for (int j = 0; j < ringSize; j++)
            {
                int k = j <= 10 ? j : ringSize - j;
                first[j] = perRing++;
                second[j] = k == 6 || k == 2 ? perRing++ : first[j];
            }
            foreach (float z in stationZ)
            {
                HalfRing(pr, z, half);
                rings.Add(mb.VertexCount);
                for (int j = 0; j < ringSize; j++)
                {
                    int k = j <= 10 ? j : ringSize - j;
                    var v = new Vector3(j <= 10 ? half[k].x : -half[k].x, half[k].y, z);
                    mb.AddVertex(v, Vector3.up, new Vector2(z * 0.5f, half[k].y));
                    if (second[j] != first[j]) mb.AddVertex(v, Vector3.up, new Vector2(z * 0.5f, half[k].y));
                }
            }
            // Under the glass (behind the windscreen to the rear window) the top skin is the cabin: dark interior, not paint.
            float glassFrom = pr.D.Style == "roadster" ? CabinRear(pr) : pr.ZRw;
            for (int i = 0; i < rings.Count - 1; i++)
            for (int k = 0; k < ringSize; k++)
            {
                bool lid = k >= 7 && k <= 12 && stationZ[i] >= glassFrom - 1e-4f && stationZ[i + 1] <= pr.ZWs + 0.03f;
                if (lid && stationZ[i] >= openFromZ - 1e-4f) continue; // the cabin opening
                int k1 = (k + 1) % ringSize;
                int sub = k == 0 || k == ringSize - 1 ? TrimSub : lid ? InteriorSub : LowerZone(pr.A.TwoTone, k);
                mb.AddQuad(sub, rings[i] + second[k], rings[i] + first[k1], rings[i + 1] + first[k1], rings[i + 1] + second[k]);
            }
            Cap(mb, pr, rings[0], perRing, pr.Zr, false);
            Cap(mb, pr, rings[rings.Count - 1], perRing, pr.Zf, true);
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

        /// <summary>Stations of the pillars that split the side glass (B, and C on wagons), as fractions of the roof.</summary>
        static List<float> SidePillars(Profile pr)
        {
            var z = new List<float>();
            switch (pr.D.Style)
            {
                case "wagon":
                    z.Add(Mathf.Lerp(pr.ZRoofR, pr.ZRoofF, 0.56f));
                    z.Add(Mathf.Lerp(pr.ZRoofR, pr.ZRoofF, 0.14f));
                    break;
                case "midship":
                    break;
                default:
                    z.Add(Mathf.Lerp(pr.ZRoofR, pr.ZRoofF, pr.D.Features.Contains("four-door") ? 0.5f : 0.45f));
                    break;
            }
            return z;
        }

        /// <summary>What fills the side window band of the greenhouse at station <paramref name="zm"/>: glass, a blacked-out pillar or paint.</summary>
        static int SideWindow(Profile pr, List<float> pillars, float zm, int roofPaint)
        {
            foreach (float p in pillars) if (Mathf.Abs(zm - p) < 0.045f) return TrimSub;
            string style = pr.D.Style;
            bool glassToTheEnd = style == "hatch" || style == "wagon" || style == "liftback";
            if (style == "midship") return zm < pr.ZRoofR + 0.1f ? roofPaint : GlassSub; // door glass only; buttresses behind
            if (zm < pr.ZRoofR && !glassToTheEnd) return roofPaint;                     // C-pillar
            if (glassToTheEnd && zm < pr.ZRw + (style == "liftback" ? 0.2f : 0.13f)) return roofPaint; // D-pillar
            return GlassSub;
        }

        /// <summary>
        /// The glasshouse: per station a section from the belt up the side to the roof edge and across — belt moulding, side
        /// glass (or pillar), drip rail, roof-edge band (the A-pillar's face beside the windscreen, the C/D-pillar's beside the
        /// rear window), then roof or glass. Windscreen frit at the base and header, wipers on the cowl.
        /// </summary>
        static void Greenhouse(MeshBuilder mb, Profile pr)
        {
            CarBodyDef d = pr.D;
            int n = buildLod >= 2 ? 18 : 36; // the far body's glasshouse is sampled half as finely
            const int per = 12;
            float z0 = pr.ZRw, z1 = pr.ZWs;
            var rows = new List<int>();
            var zs = new List<float>();
            for (int i = 0; i <= n; i++)
            {
                float z = Mathf.Lerp(z0, z1, i / (float)n);
                float w = HalfWidth(pr, z) * (1f - d.Tumblehome * 0.25f);
                float yb = TopLine(pr, z) - 0.01f, yt = Mathf.Max(yb + 0.02f, CabinTop(pr, z));
                float wt = HalfWidth(pr, z) * d.RoofTaper;
                float st = Mathf.Min(StripeHalfWidth, wt * 0.5f);
                var B = new Vector2(w, yb);
                var R = new Vector2(wt, yt);
                Vector2 side = R - B;
                float len = Mathf.Max(0.001f, side.magnitude);
                Vector2 dir = side / len;
                float pillarBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(pr.ZRoofF - 0.06f, pr.ZRoofF + 0.06f, z));
                float belt = Mathf.Min(0.02f, len * 0.2f);
                float drip = Mathf.Min(Mathf.Lerp(0.028f, 0.055f, pillarBlend), len * 0.35f);
                Vector2 B2 = B + dir * belt, G = R - dir * drip;
                float edge = Mathf.Min(0.05f, (wt - st) * 0.4f);
                float roofY = yt + d.Crown * 0.2f;
                var R2 = new Vector2(wt - edge, Mathf.Lerp(yt, roofY, edge / Mathf.Max(0.01f, wt - st)));
                var S = new Vector2(st, roofY);
                rows.Add(mb.VertexCount);
                zs.Add(z);
                // Left belt → left roof edge → centre → right roof edge → right belt.
                var right = new[] { B, B2, G, R, R2, S };
                foreach (Vector2 q in right) mb.AddVertex(new Vector3(-q.x, q.y, z), Vector3.up, Vector2.zero);
                for (int k = right.Length - 1; k >= 0; k--) mb.AddVertex(new Vector3(right[k].x, right[k].y, z), Vector3.up, Vector2.zero);
            }
            List<float> pillars = SidePillars(pr);
            string tone = pr.A.TwoTone;
            int roofPaint = tone == "roof" ? Paint2 : PaintSub;
            bool wrapGlass = d.Features.Contains("wrap-glass");
            int beltSub = d.Features.Contains("chrome-trim") ? ChromeSub : TrimSub;
            for (int i = 0; i < n; i++)
            {
                int a = rows[i], b = rows[i + 1];
                float zm = (zs[i] + zs[i + 1]) * 0.5f;
                bool windshield = zm > pr.ZRoofF, rearWindow = zm < pr.ZRoofR;
                bool frit = windshield && (zm > pr.ZWs - 0.07f || zm < pr.ZRoofF + 0.03f);
                int sideSub = SideWindow(pr, pillars, zm, roofPaint);
                int pillarSub = wrapGlass && windshield ? TrimSub : roofPaint;
                int topSub = windshield ? (frit ? TrimSub : GlassSub) : rearWindow ? GlassSub : roofPaint;
                int centreSub = !windshield && !rearWindow && tone == "hood-stripe" ? Paint2 : topSub;
                // Segments left → right: belt, side, drip, edge, top, centre, top, edge, drip, side, belt.
                int[] subs = { beltSub, sideSub, pillarSub, pillarSub, topSub, centreSub, topSub, pillarSub, pillarSub, sideSub, beltSub };
                for (int k = 0; k < per - 1; k++) mb.AddQuad(subs[k], a + k, b + k, b + k + 1, a + k + 1);
            }
            // Wipers parked on the cowl, lying on the glass.
            float cowl = TopLine(pr, pr.ZWs);
            var glassNormal = new Vector3(0f, pr.ZWs - pr.ZRoofF, pr.H - cowl).normalized;
            foreach (int s in new[] { -1, 1 })
            {
                float zw = pr.ZWs - 0.07f;
                var at = new Vector3(s * 0.26f, CabinTop(pr, zw) + 0.012f, zw);
                Quaternion lay = Quaternion.LookRotation(Vector3.Cross(Vector3.right, glassNormal), glassNormal);
                mb.AddBox(TrimSub, at, new Vector3(0.25f, 0.006f, 0.009f), Quaternion.AngleAxis(s * 7f, glassNormal) * lay);
            }
            if (d.Features.Contains("roof-rails"))
            {
                float zm = (pr.ZRoofF + pr.ZRoofR) * 0.5f, half = (pr.ZRoofF - pr.ZRoofR) * 0.5f;
                foreach (float x in new[] { -1f, 1f })
                {
                    float rx = x * HalfWidth(pr, zm) * d.RoofTaper * 0.85f;
                    mb.AddBox(TrimSub, new Vector3(rx, pr.H + 0.065f, zm), new Vector3(0.018f, 0.014f, half * 0.96f), Quaternion.identity);
                    foreach (float f in new[] { -0.9f, 0f, 0.9f })
                        mb.AddBox(TrimSub, new Vector3(rx, pr.H + 0.035f, zm + f * half), new Vector3(0.016f, 0.03f, 0.03f), Quaternion.identity);
                }
            }
        }

        static void Roadster(MeshBuilder mb, Profile pr)
        {
            // Raked windshield in a frame, two roll hoops behind the seats; the cabin stays open.
            float zb = pr.ZWs, zt = pr.ZWs - 0.3f, yb = TopLine(pr, pr.ZWs), yt = yb + 0.36f;
            float w = HalfWidth(pr, zb) * 0.86f;
            mb.AddFlatQuad(GlassSub, new Vector3(-w, yb, zb), new Vector3(-w * 0.95f, yt, zt), new Vector3(w * 0.95f, yt, zt), new Vector3(w, yb, zb), Vector2.one);
            mb.AddFlatQuad(GlassSub, new Vector3(w, yb, zb), new Vector3(w * 0.95f, yt, zt), new Vector3(-w * 0.95f, yt, zt), new Vector3(-w, yb, zb), Vector2.one);
            // Frame: header rail and the two side posts.
            mb.AddBox(TrimSub, new Vector3(0f, yt, zt), new Vector3(w * 0.96f, 0.02f, 0.02f), Quaternion.identity);
            float rake = Mathf.Atan2(0.3f, 0.36f) * Mathf.Rad2Deg;
            foreach (int s in new[] { -1, 1 })
                mb.AddBox(TrimSub, new Vector3(s * w * 0.975f, (yb + yt) * 0.5f, (zb + zt) * 0.5f), new Vector3(0.018f, 0.2f, 0.018f), Quaternion.Euler(-rake, 0f, 0f));
            float zh = pr.ZWs - 1.25f;
            foreach (float x in new[] { -0.35f, 0.35f })
            {
                mb.AddBox(ChromeSub, new Vector3(x, TopLine(pr, zh) + 0.2f, zh), new Vector3(0.03f, 0.2f, 0.03f), Quaternion.identity);
                mb.AddBox(ChromeSub, new Vector3(x, TopLine(pr, zh) + 0.41f, zh), new Vector3(0.1f, 0.025f, 0.03f), Quaternion.identity);
            }
            mb.AddBox(TrimSub, new Vector3(0f, TopLine(pr, zh + 0.5f) - 0.02f, zh + 0.5f), new Vector3(HalfWidth(pr, zh) * 0.8f, 0.02f, 0.6f), Quaternion.identity);
        }

        static float Sq(float v) => v * v;
    }
}
