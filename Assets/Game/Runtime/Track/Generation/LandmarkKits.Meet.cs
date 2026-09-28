using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Core.Meet;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Cedar Lantern Terrace (spec §12, Appendix F), generated from <see cref="MeetLayout"/> with the course kit palette:
    /// a level paved apron with bay rows, service and entry lanes, a dry garden island, cherry trees in planters, lamps and
    /// stone lanterns, the tea kiosk with its awning, lantern strings, timing board, radio bench and boombox; hedges, the
    /// timber fence on a repaired stone retaining wall and a planted berm with a maintenance gate at the edges, backed by a
    /// continuous collision ring; and the wooded overlook falling to a valley with ridges beyond, so the horizon is land,
    /// not an empty skybox. Every fixture's collider comes from the same layout the room server validates spawns against.
    /// </summary>
    public static partial class LandmarkKits
    {
        const float MeetEdgeX = 70f, MeetEdgeZ = 50f;

        public static GameObject BuildMeet(CourseMaterialSet mats, Transform parent, GenerationProfile profile)
        {
            var root = new GameObject("CedarLanternTerrace");
            root.transform.SetParent(parent, false);
            Transform t = root.transform;
            MeetGround(mats, t, profile);
            if (profile == GenerationProfile.Full)
            {
                MeetHorizon(mats, t);
                MeetForest(mats, t);
            }
            MeetPerimeter(mats, t, profile);
            MeetKiosk(mats, t, profile);
            MeetPlaza(mats, t, profile);
            MeetColliders(t);
            return root;
        }

        static GameObject MeetEmit(string name, MeshBuilder mb, CourseMaterialSet mats, Transform parent, bool collider = false)
        {
            Mesh mesh = mb.BuildCompact("Meet_" + name, out int[] used);
            var materials = new Material[used.Length];
            for (int i = 0; i < used.Length; i++) materials[i] = Mat(mats, (M)used[i]);
            return Place(name, mesh, materials, parent, Vector3.zero, Quaternion.identity, collider, GameLayers.Scenery, GenerationProfile.Full);
        }

        static void Bx(MeshBuilder mb, M sub, Vector3 c, Vector3 half, float yaw = 0f, float uv = 1f) =>
            mb.AddBox((int)sub, c, half, Quaternion.Euler(0f, yaw, 0f), uv);

        /// <summary>An upward-facing ground rectangle at height y (UVs in metres × <paramref name="uv"/>).</summary>
        static void Floor(MeshBuilder mb, M sub, float x0, float z0, float x1, float z1, float y = 0f, float uv = 0.5f) =>
            mb.AddFlatQuad((int)sub, new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0), new Vector2(uv, uv));

        static Vector3 V(MeetPoint p, float y = 0f) => new Vector3(p.X, y, p.Z);

        // ------------------------------------------------------------------ ground

        static void MeetGround(CourseMaterialSet mats, Transform parent, GenerationProfile profile)
        {
            // The apron collider: one level slab under the whole enclosure (avatars walk on y = 0).
            var slab = new GameObject("ApronCollider") { layer = GameLayers.Scenery };
            slab.transform.SetParent(parent, false);
            var bc = slab.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, -0.5f, 0f);
            bc.size = new Vector3(MeetEdgeX * 2f + 4f, 1f, MeetEdgeZ * 2f + 4f);
            slab.isStatic = true;
            if (profile != GenerationProfile.Full) return;

            var mb = Kit();
            float lx0 = MeetLayout.LaneX - MeetLayout.LaneHalfWidth, lx1 = MeetLayout.LaneX + MeetLayout.LaneHalfWidth;
            const float rowZ0 = -38f, rowZ1 = 37f, entryZ0 = -44f;
            // Plaza pavers, bay rows in concrete, asphalt lanes, gravel verges, a flagstone forecourt before the kiosk.
            Floor(mb, M.Paver, -50f, rowZ0, 50f, rowZ1, 0f, 0.6f);
            foreach (float s in new[] { -1f, 1f })
            {
                Floor(mb, M.Concrete, s < 0 ? -lx0 : 50f, rowZ0, s < 0 ? -50f : lx0, rowZ1, 0f, 0.3f);
                Floor(mb, M.Asphalt, s < 0 ? -lx1 : lx0, rowZ0, s < 0 ? -lx0 : lx1, rowZ1, 0f, 0.25f);
                Floor(mb, M.Gravel, s < 0 ? -MeetEdgeX : lx1, -MeetEdgeZ, s < 0 ? -lx1 : MeetEdgeX, MeetEdgeZ, 0f, 0.3f);
                Floor(mb, M.Gravel, s < 0 ? -lx1 : 5.5f, -MeetEdgeZ, s < 0 ? -5.5f : lx1, entryZ0, 0f, 0.3f);
            }
            Floor(mb, M.Asphalt, -lx1, entryZ0, lx1, rowZ0, 0f, 0.25f);
            Floor(mb, M.Asphalt, -5.5f, -MeetEdgeZ, 5.5f, entryZ0, 0f, 0.25f);
            Floor(mb, M.Stone, -lx1, rowZ1, lx1, MeetEdgeZ, 0f, 0.3f);
            // Paver bands marking the central aisle and the cross aisle.
            Floor(mb, M.Stone, -3f, rowZ0, -2.8f, rowZ1, 0.004f, 0.3f);
            Floor(mb, M.Stone, 2.8f, rowZ0, 3f, rowZ1, 0.004f, 0.3f);
            Floor(mb, M.Stone, -50f, 17.9f, 50f, 18.1f, 0.004f, 0.3f);
            Floor(mb, M.Stone, -50f, 23.9f, 50f, 24.1f, 0.004f, 0.3f);
            // Bay markings: side lines and a rear line, painted on the concrete.
            foreach (MeetBay b in MeetLayout.Bays)
            {
                MeetBox f = b.Footprint;
                float hw = MeetBay.HalfWidth + 0.25f, hl = MeetBay.HalfLength + 0.35f;
                foreach (float u in new[] { -hw, hw })
                    Beam(mb, M.LinePaint, V(f.FromLocal(u, -hl), 0.006f), V(f.FromLocal(u, hl), 0.006f), 0.06f, 0.004f);
                Beam(mb, M.LinePaint, V(f.FromLocal(-hw, -hl), 0.006f), V(f.FromLocal(hw, -hl), 0.006f), 0.06f, 0.004f);
                // A low wheel stop at the nose so the display line is tidy (below walking step height).
                Beam(mb, M.Concrete, V(f.FromLocal(-0.8f, hl - 0.25f), 0.06f), V(f.FromLocal(0.8f, hl - 0.25f), 0.06f), 0.1f, 0.06f);
            }
            // Lane edge dashes and the entry lane centre line.
            foreach (float s in new[] { -1f, 1f })
                for (float z = rowZ0 + 1f; z < rowZ1 - 2f; z += 4f)
                    Beam(mb, M.LinePaint, new Vector3(s * lx0, 0.006f, z), new Vector3(s * lx0, 0.006f, z + 2f), 0.05f, 0.004f);
            for (float x = -lx0 + 2f; x < lx0 - 2f; x += 4f)
                Beam(mb, M.LinePaint, new Vector3(x, 0.006f, (entryZ0 + rowZ0) * 0.5f), new Vector3(x + 2f, 0.006f, (entryZ0 + rowZ0) * 0.5f), 0.05f, 0.004f);
            // Low planted borders between the entry lane and the plaza, with gaps for walking.
            foreach (float s in new[] { -1f, 1f })
                for (float x = 6f; x < 48f; x += 9f)
                {
                    Bx(mb, M.Stone, new Vector3(s * (x + 3f), 0.14f, rowZ0 - 0.6f), new Vector3(3f, 0.14f, 0.45f));
                    Bx(mb, M.Foliage, new Vector3(s * (x + 3f), 0.42f, rowZ0 - 0.6f), new Vector3(2.8f, 0.2f, 0.36f));
                }
            DryGarden(mb);
            MeetEmit("Apron", mb, mats, parent);
        }

        /// <summary>The dry garden island: stone edging, raked gravel with ripples round three rocks, low shrubs and a maple.</summary>
        static void DryGarden(MeshBuilder mb)
        {
            MeetBox g = MeetLayout.GardenIsland;
            float hw = g.HalfW, hl = g.HalfL;
            Floor(mb, M.Gravel, g.X - hw, g.Z - hl, g.X + hw, g.Z + hl, 0.14f, 0.6f);
            // Edging stones: a run of slightly varied blocks round the rim.
            var rng = new System.Random(71);
            void Edge(Vector3 a, Vector3 b)
            {
                float len = Vector3.Distance(a, b);
                int n = Mathf.Max(1, Mathf.RoundToInt(len / 0.9f));
                Vector3 d = (b - a) / n;
                float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                for (int i = 0; i < n; i++)
                {
                    float h = 0.16f + (float)rng.NextDouble() * 0.05f;
                    Bx(mb, M.Stone, a + d * (i + 0.5f) + Vector3.up * h, new Vector3(0.2f, h, d.magnitude * 0.48f), yaw);
                }
            }
            Vector3 c0 = new Vector3(g.X - hw, 0f, g.Z - hl), c1 = new Vector3(g.X - hw, 0f, g.Z + hl), c2 = new Vector3(g.X + hw, 0f, g.Z + hl), c3 = new Vector3(g.X + hw, 0f, g.Z - hl);
            Edge(c0, c1); Edge(c1, c2); Edge(c2, c3); Edge(c3, c0);
            // Rocks with raked ripples.
            var rocks = new[] { new Vector3(g.X - 4.2f, 0.14f, g.Z + 1.2f), new Vector3(g.X + 3.1f, 0.14f, g.Z - 2.1f), new Vector3(g.X + 5.8f, 0.14f, g.Z + 2.6f) };
            float[] size = { 1.1f, 0.8f, 0.55f };
            for (int k = 0; k < rocks.Length; k++)
            {
                float r = size[k];
                Lathe(mb, M.Stone, rocks[k], new[] { new Vector2(r, 0f), new Vector2(r * 1.05f, r * 0.35f), new Vector2(r * 0.7f, r * 0.8f), new Vector2(0.05f, r * 1.05f) }, 7, false, true);
                for (int ring = 1; ring <= 3; ring++)
                {
                    float rr = r + 0.35f * ring;
                    for (int i = 0; i < 18; i++)
                    {
                        float a0 = i / 18f * Mathf.PI * 2f, a1 = (i + 1) / 18f * Mathf.PI * 2f;
                        Beam(mb, M.OffWhite, rocks[k] + new Vector3(Mathf.Cos(a0) * rr, 0.012f, Mathf.Sin(a0) * rr),
                            rocks[k] + new Vector3(Mathf.Cos(a1) * rr, 0.012f, Mathf.Sin(a1) * rr), 0.025f, 0.01f);
                    }
                }
            }
            // Straight rake lines across the rest.
            for (float z = g.Z - hl + 0.5f; z < g.Z + hl - 0.3f; z += 0.45f)
                Beam(mb, M.OffWhite, new Vector3(g.X - hw + 0.4f, 0.15f, z), new Vector3(g.X - hw + 1.6f, 0.15f, z), 0.02f, 0.008f);
            // Low clipped shrubs and a small maple in the north-west corner.
            foreach (var p in new[] { new Vector3(g.X - 7.8f, 0.14f, g.Z - 4.2f), new Vector3(g.X + 7.6f, 0.14f, g.Z + 4.4f), new Vector3(g.X - 1.5f, 0.14f, g.Z - 4.5f) })
                Lathe(mb, M.Foliage, p, new[] { new Vector2(0.75f, 0f), new Vector2(0.85f, 0.3f), new Vector2(0.5f, 0.65f), new Vector2(0.05f, 0.72f) }, 9, false, true);
            Tree(mb, new Vector3(g.X - 7.2f, 0.14f, g.Z + 4.1f), 3.4f, false, 0.8f);
        }

        // ------------------------------------------------------------------ horizon

        /// <summary>Ground height outside the enclosure: wooded slopes up to the west and north, the overlook falling east into a valley, ridges beyond.</summary>
        static float MeetTerrain(float x, float z)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(x) - MeetEdgeX), dz = Mathf.Max(0f, Mathf.Abs(z) - MeetEdgeZ);
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            float r = Mathf.Sqrt(x * x + z * z);
            // The overlook: east of the retaining wall the ground is 5 m down at once, then falls into the valley.
            float east = Mathf.Clamp01((x - 64f) / 8f) * Mathf.Clamp01(1f - Mathf.Max(0f, Mathf.Abs(z) - 45f) / Mathf.Max(80f, x * 1.4f));
            float rise = (x < 0f || z > 0f ? 0.32f : -0.12f) * d;
            rise = Mathf.Min(rise, 90f);
            float valley = -5f * east - 165f * east * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / 380f)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((r - 1400f) / 1300f)));
            float noise = (Mathf.PerlinNoise(x * 0.006f + 11f, z * 0.006f + 7f) - 0.5f) * 24f * Mathf.Clamp01(d / 60f);
            float ridgeAmp = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((r - 700f) / 1800f)) * (380f + 180f * Mathf.Clamp01(x / 2000f));
            float n1 = Mathf.PerlinNoise(x * 0.0009f + 3f, z * 0.0009f + 5f), n2 = Mathf.PerlinNoise(x * 0.0027f + 1f, z * 0.0027f + 9f);
            float ridged = 1f - Mathf.Abs(n1 * 2f - 1f);
            float ridge = ridgeAmp * (ridged * 0.75f + n2 * 0.35f);
            return (1f - east) * rise + valley + noise + ridge - (d < 1f ? 0f : 0.3f);
        }

        static void MeetHorizon(CourseMaterialSet mats, Transform parent)
        {
            const int angles = 144, rings = 44;
            const float far = 6500f;
            var near = Kit();
            var farMb = Kit();
            // Polar grid whose inner ring hugs the enclosure's rectangle, spacing growing geometrically outward.
            var pos = new Vector3[rings + 1, angles];
            for (int j = 0; j < angles; j++)
            {
                float a = j * Mathf.PI * 2f / angles;
                float cx = Mathf.Cos(a), sz = Mathf.Sin(a);
                float rin = Mathf.Min((MeetEdgeX + 1.5f) / Mathf.Max(1e-4f, Mathf.Abs(cx)), (MeetEdgeZ + 1.5f) / Mathf.Max(1e-4f, Mathf.Abs(sz)));
                for (int i = 0; i <= rings; i++)
                {
                    float t = i / (float)rings;
                    float r = rin * Mathf.Pow(far / rin, t * t * 0.35f + t * 0.65f);
                    float x = cx * r, z = sz * r;
                    float y = MeetTerrain(x, z);
                    pos[i, j] = new Vector3(x, y, z);
                }
            }
            const int split = 26;
            for (int i = 0; i < rings; i++)
            {
                MeshBuilder mb = i < split ? near : farMb;
                M sub = i < split ? M.Grass : M.Mountain;
                for (int j = 0; j < angles; j++)
                {
                    int j1 = (j + 1) % angles;
                    Vector3 a = pos[i, j], b = pos[i + 1, j], c = pos[i + 1, j1], d = pos[i, j1];
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    int ia = mb.AddVertex(a, Vector3.up, new Vector2(a.x, a.z) * 0.05f), ib = mb.AddVertex(b, Vector3.up, new Vector2(b.x, b.z) * 0.05f);
                    int ic = mb.AddVertex(c, Vector3.up, new Vector2(c.x, c.z) * 0.05f), id = mb.AddVertex(d, Vector3.up, new Vector2(d.x, d.z) * 0.05f);
                    if (n.y >= 0f) mb.AddQuad((int)sub, ia, ib, ic, id);
                    else mb.AddQuad((int)sub, ia, id, ic, ib);
                }
            }
            MeetEmit("NearSlopes", near, mats, parent);
            MeetEmit("DistantRidges", farMb, mats, parent);
        }

        /// <summary>Cedar forest on the slopes round the terrace and down the overlook (visual only, in sectors).</summary>
        static void MeetForest(CourseMaterialSet mats, Transform parent)
        {
            var rng = new System.Random(4242);
            var sectors = new MeshBuilder[8];
            for (int i = 0; i < sectors.Length; i++) sectors[i] = Kit();
            int placed = 0;
            for (int k = 0; k < 5200 && placed < 1100; k++)
            {
                float a = (float)(rng.NextDouble() * Mathf.PI * 2f);
                float d = 9f + Mathf.Pow((float)rng.NextDouble(), 1.6f) * 420f;
                float cx = Mathf.Cos(a), sz = Mathf.Sin(a);
                float rin = Mathf.Min((MeetEdgeX + 1.5f) / Mathf.Max(1e-4f, Mathf.Abs(cx)), (MeetEdgeZ + 1.5f) / Mathf.Max(1e-4f, Mathf.Abs(sz)));
                float x = cx * (rin + d), z = sz * (rin + d);
                // Keep the photo view east open near the wall, the scenic road south clear, and the entry approach.
                if (x > 60f && d < 40f && Mathf.Abs(z) < 40f && rng.NextDouble() < 0.7) continue;
                if (z < -50f && Mathf.Abs(x) < 16f) continue;
                float y = MeetTerrain(x, z);
                float h = (d < 40f ? 9f : 13f) + (float)rng.NextDouble() * (d < 40f ? 6f : 11f);
                bool simple = d > 90f;
                int sector = Mathf.Clamp((int)((a / (Mathf.PI * 2f)) * sectors.Length), 0, sectors.Length - 1);
                Tree(sectors[sector], new Vector3(x, y - 0.3f, z), h, rng.NextDouble() > 0.12, 0.9f + (float)rng.NextDouble() * 0.3f, simple);
                placed++;
            }
            for (int i = 0; i < sectors.Length; i++) MeetEmit($"Forest{i}", sectors[i], mats, parent);
        }

        // ------------------------------------------------------------------ perimeter

        static void MeetPerimeter(CourseMaterialSet mats, Transform parent, GenerationProfile profile)
        {
            // Continuous collision ring (overlapping at the corners) where the hedges, kiosk, fence and berm stand.
            var ring = new GameObject("PerimeterCollision") { layer = GameLayers.Scenery };
            ring.transform.SetParent(parent, false);
            ring.isStatic = true;
            void Wall(Vector3 centre, Vector3 size)
            {
                var b = ring.AddComponent<BoxCollider>();
                b.center = centre;
                b.size = size;
            }
            float wx = MeetLayout.WalkMaxX, wz0 = MeetLayout.WalkMinZ, wz1 = MeetLayout.WalkMaxZ;
            Wall(new Vector3(-wx - 1f, 1.5f, (wz0 + wz1) * 0.5f), new Vector3(2f, 3f, wz1 - wz0 + 6f));
            Wall(new Vector3(wx + 1f, 1.5f, (wz0 + wz1) * 0.5f), new Vector3(2f, 3f, wz1 - wz0 + 6f));
            Wall(new Vector3(0f, 1.5f, wz1 + 1f), new Vector3(wx * 2f + 6f, 3f, 2f));
            Wall(new Vector3(0f, 1.5f, wz0 - 1f), new Vector3(wx * 2f + 6f, 3f, 2f));
            if (profile != GenerationProfile.Full) return;

            var mb = Kit();
            var rng = new System.Random(9);
            // West and north: clipped hedges, slightly uneven, on a stone kerb.
            void Hedge(Vector3 a, Vector3 b)
            {
                float len = Vector3.Distance(a, b);
                int n = Mathf.Max(1, Mathf.RoundToInt(len / 2.4f));
                Vector3 d = (b - a) / n;
                float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                for (int i = 0; i < n; i++)
                {
                    float h = 0.85f + (float)rng.NextDouble() * 0.12f;
                    Vector3 c = a + d * (i + 0.5f);
                    Bx(mb, M.Stone, c + Vector3.up * 0.14f, new Vector3(0.75f, 0.14f, d.magnitude * 0.5f), yaw);
                    Bx(mb, M.Grass, c + Vector3.up * (0.28f + h), new Vector3(0.66f, h, d.magnitude * 0.52f), yaw, 0.5f);
                    Bx(mb, M.Grass, c + Vector3.up * (0.28f + h * 2f), new Vector3(0.5f, 0.14f, d.magnitude * 0.5f), yaw, 0.5f);
                }
            }
            float ex = MeetEdgeX - 0.8f;
            Hedge(new Vector3(-ex, 0f, -MeetEdgeZ + 1f), new Vector3(-ex, 0f, MeetEdgeZ - 0.5f));
            Hedge(new Vector3(-ex, 0f, MeetEdgeZ - 0.8f), new Vector3(-7.6f, 0f, MeetEdgeZ - 0.8f));
            Hedge(new Vector3(7.6f, 0f, MeetEdgeZ - 0.8f), new Vector3(ex, 0f, MeetEdgeZ - 0.8f));
            // East: the repaired stone retaining wall down to the overlook, a low timber fence along its top.
            float wallX = MeetEdgeX + 0.2f;
            for (float z = -MeetEdgeZ; z < MeetEdgeZ; z += 1.6f)
            {
                bool repaired = z > 4f && z < 14f; // newer concrete-faced blocks where the wall was rebuilt
                for (float y = -5.2f; y < 0.2f; y += 0.55f)
                {
                    float off = ((int)((y + 6f) / 0.55f) % 2) * 0.8f;
                    Bx(mb, repaired ? M.Concrete : M.Stone, new Vector3(wallX + (y + 5.2f) * -0.06f, y + 0.27f, z + off * 0.5f + 0.4f), new Vector3(0.35f, 0.26f, 0.78f));
                }
                Bx(mb, M.Stone, new Vector3(wallX - 0.2f, 0.26f, z + 0.8f), new Vector3(0.42f, 0.08f, 0.8f)); // coping
            }
            for (float z = -MeetEdgeZ + 1f; z <= MeetEdgeZ - 1f; z += 2.2f)
                Bx(mb, M.WoodDark, new Vector3(wallX - 0.25f, 0.9f, z), new Vector3(0.07f, 0.56f, 0.07f));
            foreach (float y in new[] { 0.8f, 1.3f })
                Bx(mb, M.WoodDark, new Vector3(wallX - 0.25f, y, 0f), new Vector3(0.04f, 0.05f, MeetEdgeZ - 1f));
            // South: a planted berm with shrubs, and a closed maintenance gate across the old entry road.
            foreach (float s in new[] { -1f, 1f })
            {
                float x0 = s * 6.5f, x1 = s * (MeetEdgeX - 0.5f);
                Vector3 c = new Vector3((x0 + x1) * 0.5f, 0f, -MeetEdgeZ + 0.6f);
                float half = Mathf.Abs(x1 - x0) * 0.5f;
                Bx(mb, M.Grass, c + Vector3.up * 0.45f, new Vector3(half, 0.45f, 1.5f), 0f, 0.3f);
                Bx(mb, M.Grass, c + Vector3.up * 1.05f, new Vector3(half - 0.3f, 0.3f, 0.9f), 0f, 0.3f);
                for (float x = Mathf.Min(x0, x1) + 1.5f; x < Mathf.Max(x0, x1) - 1f; x += 3.1f)
                    Lathe(mb, M.Foliage, new Vector3(x, 1.35f, -MeetEdgeZ + 0.5f), new[] { new Vector2(0.9f, 0f), new Vector2(1f, 0.5f), new Vector2(0.5f, 1.1f), new Vector2(0.05f, 1.2f) }, 7, false, true);
            }
            foreach (float s in new[] { -1f, 1f })
                Bx(mb, M.Stone, new Vector3(s * 6f, 0.8f, -MeetEdgeZ + 0.6f), new Vector3(0.45f, 0.8f, 0.45f));
            Bx(mb, M.SteelYellow, new Vector3(0f, 1.05f, -MeetEdgeZ + 0.6f), new Vector3(5.5f, 0.07f, 0.07f));
            Bx(mb, M.SteelYellow, new Vector3(0f, 0.55f, -MeetEdgeZ + 0.6f), new Vector3(5.5f, 0.05f, 0.05f));
            for (float x = -5f; x <= 5f; x += 1.25f) Bx(mb, M.SteelYellow, new Vector3(x, 0.55f, -MeetEdgeZ + 0.6f), new Vector3(0.03f, 0.5f, 0.03f));
            // The scenic approach road beyond the gate, falling away toward the valley.
            for (float z = -MeetEdgeZ - 1f; z > -120f; z -= 6f)
            {
                float y0 = MeetTerrain(0f, z) + 0.15f, y1 = MeetTerrain(0f, z - 6f) + 0.15f;
                float xa = Mathf.Sin(z * 0.03f) * 6f, xb = Mathf.Sin((z - 6f) * 0.03f) * 6f;
                mb.AddFlatQuad((int)M.Asphalt, new Vector3(xa - 3.5f, y0, z), new Vector3(xb - 3.5f, y1, z - 6f), new Vector3(xb + 3.5f, y1, z - 6f), new Vector3(xa + 3.5f, y0, z), new Vector2(0.25f, 0.25f));
            }
            MeetEmit("Perimeter", mb, mats, parent);
        }

        // ------------------------------------------------------------------ kiosk, boards, bench, boombox

        static void MeetKiosk(CourseMaterialSet mats, Transform parent, GenerationProfile profile)
        {
            if (profile != GenerationProfile.Full) return;
            var mb = Kit();
            MeetBox k = MeetLayout.Kiosk;
            float hw = k.HalfW, hd = k.HalfL, eave = 3f;
            var c = new Vector3(k.X, 0f, k.Z);
            float front = k.Z - hd;
            Bx(mb, M.Stone, c + Vector3.up * 0.15f, new Vector3(hw + 0.2f, 0.15f, hd + 0.2f));
            // Plastered walls in a timber frame; the front wall has the service counter opening.
            Bx(mb, M.OffWhite, c + new Vector3(0f, 1.65f, hd - 0.1f), new Vector3(hw, 1.35f, 0.1f));
            foreach (float s in new[] { -1f, 1f }) Bx(mb, M.OffWhite, c + new Vector3(s * (hw - 0.1f), 1.65f, 0f), new Vector3(0.1f, 1.35f, hd));
            Bx(mb, M.OffWhite, c + new Vector3(-(hw + 4.2f) * 0.5f, 1.65f, -hd + 0.1f), new Vector3((hw - 4.2f) * 0.5f, 1.35f, 0.1f));
            Bx(mb, M.OffWhite, c + new Vector3((hw + 4.2f) * 0.5f, 1.65f, -hd + 0.1f), new Vector3((hw - 4.2f) * 0.5f, 1.35f, 0.1f));
            Bx(mb, M.OffWhite, c + new Vector3(0f, 0.65f, -hd + 0.1f), new Vector3(4.2f, 0.35f, 0.1f));
            Bx(mb, M.OffWhite, c + new Vector3(0f, 2.75f, -hd + 0.1f), new Vector3(4.2f, 0.25f, 0.1f));
            // Lit interior seen through the opening, the counter shelf and the half-raised slatted shutter.
            Bx(mb, M.WindowLit, c + new Vector3(0f, 1.7f, -hd + 0.4f), new Vector3(4.1f, 0.8f, 0.05f));
            Bx(mb, M.WoodLight, c + new Vector3(0f, 1.02f, -hd - 0.12f), new Vector3(4.3f, 0.05f, 0.34f));
            for (float y = 2.18f; y < 2.5f; y += 0.09f) Bx(mb, M.SteelGrey, c + new Vector3(0f, y, -hd - 0.02f), new Vector3(4.15f, 0.035f, 0.02f));
            Bx(mb, M.SteelGrey, c + new Vector3(0f, 2.52f, -hd - 0.06f), new Vector3(4.25f, 0.06f, 0.08f)); // shutter box
            // Timber frame: posts and the eave beam.
            for (float x = -hw; x <= hw + 0.01f; x += hw / 3f)
            {
                Bx(mb, M.WoodDark, c + new Vector3(x, 1.65f, -hd - 0.02f), new Vector3(0.1f, 1.35f, 0.1f));
                Bx(mb, M.WoodDark, c + new Vector3(x, 1.65f, hd - 0.02f), new Vector3(0.1f, 1.35f, 0.1f));
            }
            Bx(mb, M.WoodDark, c + new Vector3(0f, eave - 0.1f, -hd - 0.02f), new Vector3(hw + 0.1f, 0.12f, 0.12f));
            Bx(mb, M.WoodDark, c + new Vector3(0f, eave - 0.1f, hd - 0.02f), new Vector3(hw + 0.1f, 0.12f, 0.12f));
            Roof(mb, M.RoofTiles, c, Quaternion.identity, hw * 2f, hd * 2f, eave, "gable", 0.7f);
            Bx(mb, M.WoodDark, c + new Vector3(0f, eave + 0.02f, 0f), new Vector3(hw + 0.05f, 0.04f, hd + 0.05f)); // ceiling under the roof
            // Door on the east end.
            Bx(mb, M.WoodDark, c + new Vector3(hw + 0.02f, 1.05f, 1.2f), new Vector3(0.04f, 1.05f, 0.5f));
            // The awning: a solid shed roof on posts (seen from under as well as above), a fascia beam.
            MeetBox aw = MeetLayout.Awning;
            float az0 = aw.Z - aw.HalfL, az1 = front;
            var awc = new Vector3(0f, 0f, (az0 + az1) * 0.5f);
            float aHalf = (az1 - az0) * 0.5f;
            Bx(mb, M.RoofTiles, awc + new Vector3(0f, 2.85f, 0f), new Vector3(aw.HalfW + 0.4f, 0.06f, aHalf + 0.35f), 0f, 0.3f);
            Bx(mb, M.WoodLight, awc + new Vector3(0f, 2.77f, 0f), new Vector3(aw.HalfW + 0.3f, 0.03f, aHalf + 0.25f));
            Bx(mb, M.WoodDark, new Vector3(0f, 2.62f, az0), new Vector3(aw.HalfW + 0.2f, 0.12f, 0.1f));
            foreach (MeetBox post in MeetLayout.Boxes)
                if (post.Id.StartsWith("awning-post", System.StringComparison.Ordinal))
                    Bx(mb, M.WoodDark, new Vector3(post.X, 1.35f, post.Z), new Vector3(0.12f, 1.35f, 0.12f));
            // Lantern string under the fascia and two strings out to poles over the plaza edge.
            LanternString(mb, new Vector3(-aw.HalfW, 2.5f, az0 + 0.05f), new Vector3(aw.HalfW, 2.5f, az0 + 0.05f), 0.25f, 12);
            foreach (float s in new[] { -1f, 1f })
            {
                var pole = new Vector3(s * 17f, 0f, 31.5f);
                Frustum(mb, M.WoodDark, pole, 0.09f, 0.07f, 3.4f, 7);
                LanternString(mb, new Vector3(s * aw.HalfW, 2.55f, az0), pole + Vector3.up * 3.2f, 0.45f, 8);
            }
            // Benches (from the layout) under and before the awning and round the garden.
            foreach (MeetBox b in MeetLayout.Boxes)
                if (b.Id.StartsWith("bench", System.StringComparison.Ordinal)) Bench(mb, b);
            // Timing board: a framed dark panel on two posts (the runtime writes the convoy proposal and recent results on it).
            MeetBox tb = MeetLayout.TimingBoard;
            foreach (float s in new[] { -1f, 1f }) Bx(mb, M.WoodDark, new Vector3(tb.X + s * (tb.HalfW - 0.1f), 1.5f, tb.Z), new Vector3(0.09f, 1.5f, 0.09f));
            Bx(mb, M.WoodDark, new Vector3(tb.X, 2.05f, tb.Z), new Vector3(tb.HalfW, 0.95f, 0.08f));
            Bx(mb, M.Graphite, new Vector3(tb.X, 2.05f, tb.Z - 0.06f), new Vector3(tb.HalfW - 0.14f, 0.83f, 0.03f));
            Roof(mb, M.RoofTiles, new Vector3(tb.X, 0f, tb.Z), Quaternion.identity, tb.HalfW * 2f + 0.2f, 0.5f, 3.05f, "gable", 0.18f);
            // Radio bench and the boombox on its crate (a clearly lit, reachable interaction spot).
            Bench(mb, MeetLayout.RadioBench);
            MeetBox bb = MeetLayout.Boombox;
            var bbc = new Vector3(bb.X, 0f, bb.Z);
            Bx(mb, M.WoodLight, bbc + Vector3.up * 0.25f, new Vector3(0.42f, 0.25f, 0.28f));
            Bx(mb, M.Graphite, bbc + Vector3.up * 0.7f, new Vector3(0.38f, 0.2f, 0.14f));
            foreach (float s in new[] { -1f, 1f })
            {
                Bx(mb, M.SteelGrey, bbc + new Vector3(s * 0.2f, 0.7f, -0.145f), new Vector3(0.11f, 0.11f, 0.008f));
                Bx(mb, M.Graphite, bbc + new Vector3(s * 0.2f, 0.7f, -0.152f), new Vector3(0.04f, 0.04f, 0.004f));
                Bx(mb, M.SteelGrey, bbc + new Vector3(s * 0.28f, 0.99f, 0f), new Vector3(0.02f, 0.09f, 0.02f));
            }
            Bx(mb, M.SteelGrey, bbc + new Vector3(0f, 1.08f, 0f), new Vector3(0.3f, 0.02f, 0.02f));
            Bx(mb, M.LanternPaper, bbc + new Vector3(0f, 0.8f, -0.145f), new Vector3(0.06f, 0.035f, 0.004f)); // lit dial
            MeetEmit("Kiosk", mb, mats, parent);

            var awningLight = new GameObject("AwningLight", typeof(Light));
            awningLight.transform.SetParent(parent, false);
            awningLight.transform.position = new Vector3(0f, 2.4f, az0 + 1.4f);
            Light l = awningLight.GetComponent<Light>();
            l.type = LightType.Point;
            l.range = 14f;
            l.intensity = 2.2f;
            l.color = new Color(1f, 0.78f, 0.52f);
            l.shadows = LightShadows.None;
        }

        static void Bench(MeshBuilder mb, MeetBox b)
        {
            var c = new Vector3(b.X, 0f, b.Z);
            Bx(mb, M.WoodLight, c + Vector3.up * 0.45f, new Vector3(b.HalfW, 0.04f, b.HalfL), b.Yaw);
            foreach (float s in new[] { -1f, 1f })
            {
                MeetPoint p = b.FromLocal(s * (b.HalfW - 0.15f), 0f);
                Bx(mb, M.WoodDark, new Vector3(p.X, 0.21f, p.Z), new Vector3(0.06f, 0.21f, b.HalfL - 0.04f), b.Yaw);
            }
        }

        static void LanternString(MeshBuilder mb, Vector3 a, Vector3 b, float sag, int lanterns)
        {
            Cable(mb, M.Rope, a, b, sag, 0.012f, 12);
            for (int i = 1; i < lanterns; i++)
            {
                float t = i / (float)lanterns;
                Vector3 p = Vector3.Lerp(a, b, t) - Vector3.up * (sag * 4f * t * (1f - t) + 0.14f);
                Lathe(mb, M.LanternPaper, p, new[] { new Vector2(0.06f, -0.1f), new Vector2(0.11f, -0.02f), new Vector2(0.11f, 0.04f), new Vector2(0.06f, 0.1f) }, 8, true, true);
            }
        }

        // ------------------------------------------------------------------ plaza

        static void MeetPlaza(CourseMaterialSet mats, Transform parent, GenerationProfile profile)
        {
            if (profile != GenerationProfile.Full) return;
            var mb = Kit();
            var rng = new System.Random(310);
            var lights = new List<Vector3>();
            foreach (MeetCircle c in MeetLayout.Circles)
            {
                var p = new Vector3(c.X, 0f, c.Z);
                if (c.Id.StartsWith("cherry", System.StringComparison.Ordinal))
                {
                    // Round stone planter, soil, and a spreading cherry with a pink crown; petals round it.
                    Lathe(mb, M.Stone, p, new[] { new Vector2(c.R, 0f), new Vector2(c.R, 0.42f), new Vector2(c.R - 0.2f, 0.42f), new Vector2(c.R - 0.2f, 0.3f) }, 14);
                    Cap(mb, M.Bark, p + Vector3.up * 0.3f, c.R - 0.2f, 14, Vector3.up);
                    CherryTree(mb, p + Vector3.up * 0.3f, 5.8f + (float)rng.NextDouble() * 1.4f, rng);
                    Petals(mb, p, 4.5f, 26, rng);
                }
                else if (c.Id.StartsWith("lamp", System.StringComparison.Ordinal))
                {
                    Frustum(mb, M.Post, p, 0.1f, 0.07f, 3.6f, 8);
                    Beam(mb, M.Post, p + Vector3.up * 3.5f, p + new Vector3(-0.6f, 3.5f, 0f), 0.03f);
                    Lathe(mb, M.LanternPaper, p + new Vector3(-0.6f, 3.08f, 0f), new[] { new Vector2(0.14f, 0f), new Vector2(0.18f, 0.2f), new Vector2(0.16f, 0.36f) }, 10, true, true);
                    Lathe(mb, M.Graphite, p + new Vector3(-0.6f, 3.44f, 0f), new[] { new Vector2(0.26f, 0f), new Vector2(0.04f, 0.12f) }, 10, true);
                    lights.Add(p + new Vector3(-0.6f, 3.2f, 0f));
                }
                else if (c.Id == "lantern")
                {
                    StoneLantern(mb, p);
                    lights.Add(p + Vector3.up * 1.15f);
                }
            }
            // Petals scattered on the garden and the west paving.
            Petals(mb, new Vector3(0f, 0.15f, -2f), 8f, 60, rng);
            Petals(mb, new Vector3(-40f, 0f, 0f), 14f, 70, rng);
            // Viewpoint placards: a post and a sloped reading board facing the view.
            foreach (MeetBox pl in MeetLayout.Placards)
            {
                var pc = new Vector3(pl.X, 0f, pl.Z);
                Quaternion face = Quaternion.Euler(0f, pl.Yaw, 0f);
                Bx(mb, M.WoodDark, pc + Vector3.up * 0.45f, new Vector3(0.06f, 0.45f, 0.06f), pl.Yaw);
                mb.AddBox((int)M.WoodDark, pc + Vector3.up * 0.98f, new Vector3(0.45f, 0.28f, 0.035f), face * Quaternion.Euler(35f, 0f, 0f), 0.5f);
                mb.AddBox((int)M.OffWhite, pc + Vector3.up * 0.98f + face * Quaternion.Euler(35f, 0f, 0f) * new Vector3(0f, 0f, -0.037f), new Vector3(0.4f, 0.23f, 0.004f), face * Quaternion.Euler(35f, 0f, 0f), 0.5f);
            }
            // Photo marker: a brass-edged stone disc with a direction notch facing the east bays and the mountains.
            var pm = V(MeetLayout.PhotoMarker, 0.006f);
            Lathe(mb, M.OffWhite, pm, new[] { new Vector2(0.55f, 0f), new Vector2(0.55f, 0.012f) }, 24, false, true);
            Lathe(mb, M.SteelYellow, pm + Vector3.up * 0.013f, new[] { new Vector2(0.55f, 0f), new Vector2(0.47f, 0f) }, 24);
            Beam(mb, M.SteelYellow, pm + new Vector3(0.2f, 0.016f, 0f), pm + new Vector3(0.52f, 0.016f, 0f), 0.05f, 0.004f);
            MeetEmit("Plaza", mb, mats, parent);
            foreach (Vector3 lp in lights)
            {
                var go = new GameObject("PlazaLight", typeof(Light));
                go.transform.SetParent(parent, false);
                go.transform.position = lp;
                Light l = go.GetComponent<Light>();
                l.type = LightType.Point;
                l.range = lp.y > 2f ? 11f : 6f;
                l.intensity = lp.y > 2f ? 1.8f : 1.1f;
                l.color = new Color(1f, 0.8f, 0.55f);
                l.shadows = LightShadows.None;
            }
        }

        static void CherryTree(MeshBuilder mb, Vector3 foot, float height, System.Random rng)
        {
            // A leaning trunk that forks, and a broad, layered crown of blossom clouds.
            Vector3 lean = new Vector3((float)rng.NextDouble() - 0.5f, 0f, (float)rng.NextDouble() - 0.5f) * 0.6f;
            Vector3 fork = foot + Vector3.up * height * 0.38f + lean;
            Frustum(mb, M.Bark, foot, 0.18f, 0.13f, height * 0.38f, 8);
            for (int i = 0; i < 3; i++)
            {
                float a = i * 2.1f + (float)rng.NextDouble();
                Vector3 tip = fork + new Vector3(Mathf.Cos(a) * height * 0.28f, height * 0.26f, Mathf.Sin(a) * height * 0.28f);
                Beam(mb, M.Bark, fork, tip, 0.07f);
                Blossom(mb, tip + Vector3.up * 0.2f, height * (0.24f + (float)rng.NextDouble() * 0.06f));
            }
            Blossom(mb, fork + Vector3.up * height * 0.42f, height * 0.3f);
        }

        static void Blossom(MeshBuilder mb, Vector3 c, float r)
        {
            var prof = new List<Vector2>();
            for (int i = 0; i <= 5; i++)
            {
                float a = -Mathf.PI * 0.5f + i / 5f * Mathf.PI;
                prof.Add(new Vector2(Mathf.Max(0.03f, Mathf.Cos(a) * r), Mathf.Sin(a) * r * 0.62f));
            }
            Lathe(mb, M.Blossom, c, prof, 9);
        }

        static void Petals(MeshBuilder mb, Vector3 c, float radius, int count, System.Random rng)
        {
            for (int i = 0; i < count; i++)
            {
                float a = (float)(rng.NextDouble() * Mathf.PI * 2f), d = Mathf.Sqrt((float)rng.NextDouble()) * radius;
                var p = c + new Vector3(Mathf.Cos(a) * d, 0.008f, Mathf.Sin(a) * d);
                float yaw = (float)rng.NextDouble() * 180f;
                Bx(mb, M.Blossom, p, new Vector3(0.035f, 0.002f, 0.05f), yaw);
            }
        }

        /// <summary>A stone lantern: base, post, lit firebox and a curved cap (original proportions, not a copied design).</summary>
        static void StoneLantern(MeshBuilder mb, Vector3 p)
        {
            Lathe(mb, M.Stone, p, new[] { new Vector2(0.42f, 0f), new Vector2(0.42f, 0.12f), new Vector2(0.3f, 0.2f), new Vector2(0.14f, 0.24f) }, 8, false, true);
            Frustum(mb, M.Stone, p + Vector3.up * 0.24f, 0.13f, 0.11f, 0.6f, 8);
            Bx(mb, M.Stone, p + Vector3.up * 0.88f, new Vector3(0.26f, 0.05f, 0.26f));
            Bx(mb, M.LanternPaper, p + Vector3.up * 1.12f, new Vector3(0.16f, 0.19f, 0.16f));
            foreach (float sx in new[] { -1f, 1f })
                foreach (float sz in new[] { -1f, 1f })
                    Bx(mb, M.Stone, p + new Vector3(sx * 0.17f, 1.12f, sz * 0.17f), new Vector3(0.045f, 0.2f, 0.045f));
            Lathe(mb, M.Stone, p + Vector3.up * 1.32f, new[] { new Vector2(0.44f, 0f), new Vector2(0.4f, 0.1f), new Vector2(0.16f, 0.26f), new Vector2(0.06f, 0.4f), new Vector2(0.02f, 0.46f) }, 6, true);
        }

        // ------------------------------------------------------------------ colliders from the layout

        static void MeetColliders(Transform parent)
        {
            var root = new GameObject("FixtureCollision") { layer = GameLayers.Scenery };
            root.transform.SetParent(parent, false);
            root.isStatic = true;
            foreach (MeetBox b in MeetLayout.Boxes)
            {
                var go = new GameObject(b.Id) { layer = GameLayers.Scenery };
                go.transform.SetParent(root.transform, false);
                go.transform.SetPositionAndRotation(new Vector3(b.X, 0f, b.Z), Quaternion.Euler(0f, b.Yaw, 0f));
                var bc = go.AddComponent<BoxCollider>();
                float h = b.Id.StartsWith("bench", System.StringComparison.Ordinal) || b.Id == "dry-garden" || b.Id == "boombox" ? 1.1f : 3.2f;
                bc.center = new Vector3(0f, h * 0.5f, 0f);
                bc.size = new Vector3(b.HalfW * 2f, h, b.HalfL * 2f);
                go.isStatic = true;
            }
            foreach (MeetCircle c in MeetLayout.Circles)
            {
                var go = new GameObject(c.Id) { layer = GameLayers.Scenery };
                go.transform.SetParent(root.transform, false);
                go.transform.position = new Vector3(c.X, 0f, c.Z);
                var cc = go.AddComponent<CapsuleCollider>();
                cc.radius = c.R;
                cc.height = 3f + c.R * 2f;
                cc.center = new Vector3(0f, cc.height * 0.5f, 0f);
                go.isStatic = true;
            }
        }
    }
}
