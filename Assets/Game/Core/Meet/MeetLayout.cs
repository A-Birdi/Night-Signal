using System;
using System.Collections.Generic;

namespace NightSignal.Core.Meet
{
    /// <summary>A point on the meet plateau (metres; x east, z north; the apron is level at y = 0).</summary>
    public struct MeetPoint
    {
        public float X, Z;
        public MeetPoint(float x, float z) { X = x; Z = z; }
        public float DistanceTo(MeetPoint o) => (float)Math.Sqrt((X - o.X) * (X - o.X) + (Z - o.Z) * (Z - o.Z));
        public override string ToString() => $"({X:0.0}, {Z:0.0})";
    }

    /// <summary>An oriented rectangle (centre, half extents, heading in degrees: 0 = north/+z, 90 = east/+x).</summary>
    public struct MeetBox
    {
        public float X, Z, HalfW, HalfL, Yaw;
        public string Id;

        public MeetBox(string id, float x, float z, float halfW, float halfL, float yaw = 0f)
        {
            Id = id; X = x; Z = z; HalfW = halfW; HalfL = halfL; Yaw = yaw;
        }

        /// <summary>The point in the box's frame (u across, v along the heading).</summary>
        public void Local(float px, float pz, out float u, out float v)
        {
            double r = Yaw * Math.PI / 180.0;
            float dx = px - X, dz = pz - Z;
            float s = (float)Math.Sin(r), c = (float)Math.Cos(r);
            // Heading vector (sin, cos); right vector (cos, -sin).
            v = dx * s + dz * c;
            u = dx * c - dz * s;
        }

        public bool Contains(float px, float pz, float margin = 0f)
        {
            Local(px, pz, out float u, out float v);
            return Math.Abs(u) <= HalfW + margin && Math.Abs(v) <= HalfL + margin;
        }

        public MeetPoint FromLocal(float u, float v)
        {
            double r = Yaw * Math.PI / 180.0;
            float s = (float)Math.Sin(r), c = (float)Math.Cos(r);
            return new MeetPoint(X + u * c + v * s, Z - u * s + v * c);
        }
    }

    /// <summary>A round obstacle (tree trunk and planter, lamp post, bollard).</summary>
    public struct MeetCircle
    {
        public float X, Z, R;
        public string Id;
        public MeetCircle(string id, float x, float z, float r) { Id = id; X = x; Z = z; R = r; }
        public bool Contains(float px, float pz, float margin = 0f) => (px - X) * (px - X) + (pz - Z) * (pz - Z) <= (R + margin) * (R + margin);
    }

    /// <summary>One display bay: the parked car's centre and heading, and its side of the plaza.</summary>
    public sealed class MeetBay
    {
        public int Index;
        /// <summary>west | east</summary>
        public string Side = "";
        public float X, Z, Yaw;
        public const float HalfWidth = 1.35f, HalfLength = 2.75f;
        public MeetBox Footprint => new MeetBox($"bay{Index + 1}", X, Z, HalfWidth, HalfLength, Yaw);
    }

    /// <summary>
    /// Cedar Lantern Terrace (spec §12, Appendix F) as engine-free geometry shared by the client scene and the room server:
    /// the 150 × 110 m plateau, twelve angled display bays (six west beneath cherry trees, six east along the timber fence
    /// and the stone retaining wall), the service lanes behind them that the non-colliding arrival spline uses, the tea
    /// kiosk, timing board and radio bench (boombox, tutorial host) to the north, the dry garden island in the middle, the
    /// photo marker, and the perimeter. The server validates bays, exits and rescue spawns against the same numbers the
    /// scene is built from, so a spawn it approves is never inside something the player can see.
    /// </summary>
    public static class MeetLayout
    {
        public const string Id = "cedar-lantern-terrace";
        public const float PlateauHalfX = 75f, PlateauHalfZ = 55f;
        /// <summary>The walkable enclosure: hedges (west), kiosk walls and hedges (north), fence and retaining wall (east), berm and gate (south).</summary>
        public const float WalkMinX = -68.5f, WalkMaxX = 68.5f, WalkMinZ = -46.5f, WalkMaxZ = 48.5f;
        /// <summary>Service lanes (the arrival spline's approach to the bays) and the south entry lane.</summary>
        public const float LaneX = 63f, LaneHalfWidth = 2.6f, EntryLaneZ = -41f;
        /// <summary>Avatar collision radius used for validation (matches the walking controller).</summary>
        public const float AvatarRadius = 0.32f;

        public static readonly MeetBay[] Bays = MakeBays();

        /// <summary>
        /// Bays that always hold a rival's display car (scenery ambience, the same offline and online); the other seven take
        /// the humans (at most six per instance, D02).
        /// </summary>
        public static readonly int[] AmbienceBays = { 2, 4, 7, 9, 11 };

        static MeetBay[] MakeBays()
        {
            var bays = new List<MeetBay>();
            // Nose toward the plaza, angled 22° toward the north so cars pull in off the service lane in one sweep.
            float[] zs = { -24f, -14f, -4f, 6f, 16f, 26f };
            for (int i = 0; i < 6; i++) bays.Add(new MeetBay { Index = i, Side = "west", X = -53.5f, Z = zs[i], Yaw = 90f - 22f });
            for (int i = 0; i < 6; i++) bays.Add(new MeetBay { Index = 6 + i, Side = "east", X = 53.5f, Z = zs[i], Yaw = 270f + 22f });
            return bays.ToArray();
        }

        // ------------------------------------------------------------------ fixtures

        public static readonly MeetBox Kiosk = new MeetBox("tea-kiosk", 0f, 45f, 7f, 3.2f);
        /// <summary>Covered awning in front of the kiosk (walkable underneath; posts are obstacles).</summary>
        public static readonly MeetBox Awning = new MeetBox("awning", 0f, 39.5f, 8f, 2.4f);
        public static readonly MeetBox TimingBoard = new MeetBox("timing-board", -14f, 43.5f, 2.4f, 0.35f);
        public static readonly MeetBox RadioBench = new MeetBox("radio-bench", 14f, 42.5f, 1.6f, 0.45f);
        public static readonly MeetBox Boombox = new MeetBox("boombox", 14f, 41.4f, 0.4f, 0.25f);
        /// <summary>Where the tutorial host stands (beside the radio bench, facing the plaza).</summary>
        public static readonly MeetPoint HostSpot = new MeetPoint(17.2f, 41.2f);
        public static readonly MeetBox GardenIsland = new MeetBox("dry-garden", 0f, -2f, 9.5f, 6f);
        /// <summary>The photo marker: stand here and the east bays frame against the mountain horizon.</summary>
        public static readonly MeetPoint PhotoMarker = new MeetPoint(-18f, 8f);
        public const float PhotoYaw = 90f;
        /// <summary>The four viewpoint placards (meet.text.json placards.viewpoints), each facing its view.</summary>
        public static readonly MeetBox[] Placards =
        {
            new MeetBox("VIEW-N", -22f, 47.4f, 0.45f, 0.12f, 0f),
            new MeetBox("VIEW-E", 67.4f, 9f, 0.45f, 0.12f, 90f),
            new MeetBox("VIEW-S", -12f, -45.6f, 0.45f, 0.12f, 180f),
            new MeetBox("VIEW-W", -67.3f, 4f, 0.45f, 0.12f, 270f),
        };

        /// <summary>
        /// The three named non-race photo points (meet.text.json placards.photoPoints): where each is read. Points, not
        /// obstacles — the kiosk, bench and gate they describe are already in the layout.
        /// </summary>
        public static readonly (string Id, MeetPoint At)[] PhotoPoints =
        {
            ("PHOTO-TEA-KIOSK", new MeetPoint(0f, 45f - 3.2f - 0.4f)),
            ("PHOTO-RADIO-BENCH", new MeetPoint(14f - 1.2f, 42.5f)),
            ("PHOTO-MAINTENANCE-GATE", new MeetPoint(0f, WalkMinZ + 0.6f)),
        };

        /// <summary>Where a new arrival's avatar stands before its bay is known, and the fallback rescue spot.</summary>
        public static readonly MeetPoint PlazaCentre = new MeetPoint(0f, 16f);

        static List<MeetBox> boxes;
        static List<MeetCircle> circles;

        /// <summary>Everything solid at walking height inside the enclosure (parked cars are separate: see <see cref="Walkable"/>).</summary>
        public static IReadOnlyList<MeetBox> Boxes
        {
            get
            {
                if (boxes != null) return boxes;
                boxes = new List<MeetBox>
                {
                    Kiosk, TimingBoard, RadioBench, Boombox,
                    GardenIsland,
                    // Kiosk-side benches under the awning and at the plaza edge.
                    new MeetBox("bench-a", -6f, 36.2f, 1.2f, 0.3f), new MeetBox("bench-b", 6f, 36.2f, 1.2f, 0.3f),
                    new MeetBox("bench-garden-n", -5.5f, 5.2f, 1.4f, 0.3f), new MeetBox("bench-garden-s", 5.5f, -9.2f, 1.4f, 0.3f),
                };
                boxes.AddRange(Placards);
                // Awning posts.
                foreach (float x in new[] { -7.6f, 7.6f })
                    boxes.Add(new MeetBox($"awning-post{(x < 0 ? "W" : "E")}", x, 37.4f, 0.12f, 0.12f));
                return boxes;
            }
        }

        public static IReadOnlyList<MeetCircle> Circles
        {
            get
            {
                if (circles != null) return circles;
                circles = new List<MeetCircle>();
                // West: cherry trees in round planters between the bays (roots and kerbs inside the planter, outside the paths).
                for (int i = 0; i < 5; i++) circles.Add(new MeetCircle($"cherry{i + 1}", -55.5f, -19f + i * 10f, 1.25f));
                circles.Add(new MeetCircle("cherry-n", -55.5f, 31f, 1.25f));
                circles.Add(new MeetCircle("cherry-s", -55.5f, -29f, 1.25f));
                // East: lamp posts between bays along the fence side.
                for (int i = 0; i < 5; i++) circles.Add(new MeetCircle($"lamp-e{i + 1}", 57.2f, -19f + i * 10f, 0.2f));
                // Plaza lanterns on stone bases around the garden.
                foreach (var p in new[] { new MeetPoint(-10.6f, 5.2f), new MeetPoint(10.6f, 5.2f), new MeetPoint(-10.6f, -9.2f), new MeetPoint(10.6f, -9.2f) })
                    circles.Add(new MeetCircle("lantern", p.X, p.Z, 0.45f));
                return circles;
            }
        }

        // ------------------------------------------------------------------ queries

        /// <summary>
        /// Can an avatar of the given radius stand here? Inside the enclosure, clear of every fixture, and clear of the
        /// cars parked in <paramref name="occupiedBays"/> (empty bays are open paving).
        /// </summary>
        public static bool Walkable(float x, float z, float radius = AvatarRadius, ICollection<int> occupiedBays = null)
        {
            if (x < WalkMinX + radius || x > WalkMaxX - radius || z < WalkMinZ + radius || z > WalkMaxZ - radius) return false;
            foreach (MeetBox b in Boxes) if (b.Contains(x, z, radius)) return false;
            foreach (MeetCircle c in Circles) if (c.Contains(x, z, radius)) return false;
            if (occupiedBays != null)
                foreach (int i in occupiedBays)
                    if (i >= 0 && i < Bays.Length && Bays[i].Footprint.Contains(x, z, radius)) return false;
            return true;
        }

        /// <summary>The driver's door point beside a bay's car, 0.75 m out from the body (<paramref name="driverSide"/> −1 left-hand drive, +1 right-hand).</summary>
        public static MeetPoint DoorPoint(int bay, int driverSide = -1)
        {
            MeetBox f = Bays[bay].Footprint;
            return f.FromLocal((driverSide >= 0 ? 1f : -1f) * (MeetBay.HalfWidth + 0.75f), 0.35f);
        }

        /// <summary>
        /// A validated free point beside the bay's car for getting out or rescue: the door point first, then points around
        /// the car and out into the plaza, skipping anything not walkable or within 0.8 m of another avatar.
        /// </summary>
        public static bool TryFreeSpot(int bay, ICollection<int> occupiedBays, IList<MeetPoint> avatars, out MeetPoint spot, int driverSide = -1)
        {
            var candidates = new List<MeetPoint> { DoorPoint(bay, driverSide), DoorPoint(bay, -driverSide) };
            MeetBox f = Bays[bay].Footprint;
            float w = MeetBay.HalfWidth + 0.75f, l = MeetBay.HalfLength + 0.8f;
            candidates.Add(f.FromLocal(-w, -1.4f));
            candidates.Add(f.FromLocal(-w, 1.6f));
            candidates.Add(f.FromLocal(0f, l));
            candidates.Add(f.FromLocal(w, 0.35f));
            candidates.Add(f.FromLocal(-w - 1.2f, 0.35f));
            candidates.Add(f.FromLocal(0f, l + 1.5f));
            // Then out toward the plaza in steps.
            for (int k = 1; k <= 8; k++)
            {
                float t = k / 8f;
                candidates.Add(new MeetPoint(f.X + (PlazaCentre.X - f.X) * t, f.Z + (PlazaCentre.Z - f.Z) * t));
            }
            foreach (MeetPoint p in candidates)
            {
                if (!Walkable(p.X, p.Z, AvatarRadius, occupiedBays)) continue;
                bool crowded = false;
                if (avatars != null)
                    foreach (MeetPoint a in avatars)
                        if (a.DistanceTo(p) < 0.8f) { crowded = true; break; }
                if (crowded) continue;
                spot = p;
                return true;
            }
            spot = PlazaCentre;
            return false;
        }

        /// <summary>
        /// The arrival spline to a bay (polyline, metres): from the scenic road below the south edge, up the landscaped
        /// entry lane, along the south lane and that side's service lane behind the bays, then a forward sweep into the bay.
        /// Presentation only: arrival cars never collide with avatars, cars or furniture.
        /// </summary>
        public static List<MeetPoint> ArrivalPath(int bay)
        {
            MeetBay b = Bays[bay];
            float side = b.Side == "west" ? -1f : 1f;
            var path = new List<MeetPoint>
            {
                new MeetPoint(side * 8f, -78f),
                new MeetPoint(side * 4f, -60f),
                new MeetPoint(side * 2.5f, -50f),
                new MeetPoint(side * 10f, EntryLaneZ - 1f),
                new MeetPoint(side * (LaneX - 6f), EntryLaneZ),
                new MeetPoint(side * LaneX, EntryLaneZ + 6f),
            };
            // Up the service lane to just short of the bay, then swing in nose-first.
            MeetBox f = b.Footprint;
            MeetPoint entry = f.FromLocal(0f, -MeetBay.HalfLength - 6.5f);
            path.Add(new MeetPoint(side * LaneX, entry.Z - 6f));
            path.Add(entry);
            path.Add(f.FromLocal(0f, -MeetBay.HalfLength - 1.5f));
            path.Add(new MeetPoint(b.X, b.Z));
            return path;
        }

        /// <summary>The presented arrival: the last stretch of the spline, driven in about 3.5 s (spec: target 3–4 s).</summary>
        public const float ArrivalMetres = 34f, ArrivalSeconds = 3.5f;

        /// <summary>The point <paramref name="metresFromEnd"/> back along a path from its end, and the heading there (degrees).</summary>
        public static MeetPoint PointFromEnd(List<MeetPoint> path, float metresFromEnd, out float yaw)
        {
            float left = metresFromEnd;
            for (int i = path.Count - 1; i > 0; i--)
            {
                MeetPoint a = path[i - 1], b = path[i];
                float seg = a.DistanceTo(b);
                yaw = (float)(Math.Atan2(b.X - a.X, b.Z - a.Z) * 180.0 / Math.PI);
                if (left <= seg || i == 1)
                {
                    float t = seg > 0f ? Math.Max(0f, 1f - left / seg) : 0f;
                    return new MeetPoint(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
                }
                left -= seg;
            }
            yaw = 0f;
            return path[0];
        }

        public static float PathLength(List<MeetPoint> path)
        {
            float d = 0f;
            for (int i = 1; i < path.Count; i++) d += path[i - 1].DistanceTo(path[i]);
            return d;
        }

        /// <summary>The first free bay for a new arrival: keeps a convoy together by preferring bays next to <paramref name="nearBay"/>.</summary>
        public static int AllocateBay(ICollection<int> occupied, int nearBay = -1)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < Bays.Length; i++)
            {
                if (occupied != null && occupied.Contains(i)) continue;
                float d = nearBay >= 0 ? Math.Abs(Bays[i].Z - Bays[nearBay].Z) + (Bays[i].Side == Bays[nearBay].Side ? 0f : 200f) : i;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
    }
}
