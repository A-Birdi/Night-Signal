using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Meet;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    /// <summary>
    /// Cedar Lantern Terrace geometry (bays, clearances, exits, arrival paths) and the shared boombox rules (lease, one
    /// request per person, six-request queue, ten-second change interval, ownership, leaving, spoiler protection).
    /// </summary>
    public sealed class MeetRulesTests
    {
        [Test]
        public void Layout_TwelveBays_SixEachSide_ClearOfFixturesAndEachOther()
        {
            Assert.That(MeetLayout.Bays.Length, Is.EqualTo(12));
            Assert.That(MeetLayout.Bays.Count(b => b.Side == "west"), Is.EqualTo(6));
            Assert.That(MeetLayout.Bays.Count(b => b.Side == "east"), Is.EqualTo(6));
            foreach (MeetBay b in MeetLayout.Bays)
            {
                MeetBox f = b.Footprint;
                // Every corner of the car inside the enclosure and outside every fixture.
                foreach (var c in new[] { f.FromLocal(-f.HalfW, -f.HalfL), f.FromLocal(f.HalfW, -f.HalfL), f.FromLocal(-f.HalfW, f.HalfL), f.FromLocal(f.HalfW, f.HalfL) })
                {
                    Assert.That(MeetLayout.Walkable(c.X, c.Z, 0f), Is.True, $"bay {b.Index + 1} corner {c} clear");
                    foreach (MeetBay o in MeetLayout.Bays)
                        if (o != b) Assert.That(o.Footprint.Contains(c.X, c.Z, 0.6f), Is.False, $"bay {b.Index + 1} overlaps bay {o.Index + 1}");
                }
                // The nose points into the plaza.
                MeetPoint nose = f.FromLocal(0f, 1f);
                Assert.That(System.Math.Abs(nose.X), Is.LessThan(System.Math.Abs(b.X)), $"bay {b.Index + 1} nose toward the plaza");
            }
        }

        [Test]
        public void Layout_KeepsAisleAndCirculationClear()
        {
            var all = Enumerable.Range(0, 12).ToList();
            // The 6-metre central aisle runs from the entry to the kiosk awning, splitting round the garden island into two
            // more 6-metre aisles; a cross aisle joins the bay rows. All clear of fixtures and parked cars.
            for (float z = -38f; z <= 34f; z += 0.5f)
                for (float x = -3f; x <= 3f; x += 0.5f)
                {
                    bool island = MeetLayout.GardenIsland.Contains(x, z, MeetLayout.AvatarRadius);
                    if (!island) Assert.That(MeetLayout.Walkable(x, z, MeetLayout.AvatarRadius, all), Is.True, $"aisle blocked at ({x}, {z})");
                }
            foreach (float side in new[] { -1f, 1f })
                for (float z = -38f; z <= 34f; z += 0.5f)
                    for (float x = 12f; x <= 18f; x += 0.5f)
                        Assert.That(MeetLayout.Walkable(side * x, z, MeetLayout.AvatarRadius, all), Is.True, $"garden-side aisle blocked at ({side * x}, {z})");
            for (float x = -40f; x <= 40f; x += 0.5f)
                for (float z = 18f; z <= 24f; z += 0.5f)
                    Assert.That(MeetLayout.Walkable(x, z, MeetLayout.AvatarRadius, all), Is.True, $"cross aisle blocked at ({x}, {z})");
            // At least 3 m of clear circulation behind every parked car.
            foreach (MeetBay b in MeetLayout.Bays)
            {
                MeetBox f = b.Footprint;
                for (float u = -f.HalfW; u <= f.HalfW; u += 0.45f)
                    for (float v = 0.4f; v <= 3f; v += 0.4f)
                    {
                        MeetPoint p = f.FromLocal(u, -f.HalfL - v);
                        Assert.That(MeetLayout.Walkable(p.X, p.Z, 0f, all), Is.True, $"bay {b.Index + 1}: behind the car at {p}");
                    }
            }
        }

        [Test]
        public void Layout_DoorAndRescueSpotsAreValidatedAndFree()
        {
            var all = Enumerable.Range(0, 12).ToList();
            for (int i = 0; i < 12; i++)
            {
                Assert.That(MeetLayout.TryFreeSpot(i, all, new List<MeetPoint>(), out MeetPoint p), Is.True, $"bay {i + 1}");
                Assert.That(MeetLayout.Walkable(p.X, p.Z, MeetLayout.AvatarRadius, all), Is.True);
                Assert.That(p.DistanceTo(MeetLayout.DoorPoint(i)), Is.LessThan(0.01f), $"bay {i + 1}: the door point itself when nobody stands there");
                // Somebody standing at the door: the next free point, not on top of them.
                var crowd = new List<MeetPoint> { MeetLayout.DoorPoint(i) };
                Assert.That(MeetLayout.TryFreeSpot(i, all, crowd, out MeetPoint q), Is.True);
                Assert.That(q.DistanceTo(crowd[0]), Is.GreaterThanOrEqualTo(0.8f));
            }
        }

        [Test]
        public void Layout_ArrivalPathsStayOutOfThePedestrianCentre()
        {
            for (int i = 0; i < 12; i++)
            {
                List<MeetPoint> path = MeetLayout.ArrivalPath(i);
                MeetPoint end = path[path.Count - 1];
                Assert.That(end.DistanceTo(new MeetPoint(MeetLayout.Bays[i].X, MeetLayout.Bays[i].Z)), Is.LessThan(0.01f));
                Assert.That(path[0].Z, Is.LessThan(-MeetLayout.PlateauHalfZ), "starts on the scenic road below the south edge");
                float len = MeetLayout.PathLength(path);
                Assert.That(len, Is.InRange(60f, 200f));
                // The presented arrival is the last stretch, from the service lane into the bay, at a believable speed.
                MeetPoint start = MeetLayout.PointFromEnd(path, MeetLayout.ArrivalMetres, out float yaw);
                bool inLane = System.Math.Abs(System.Math.Abs(start.X) - MeetLayout.LaneX) < MeetLayout.LaneHalfWidth + 1f || start.Z <= MeetLayout.EntryLaneZ + 3f;
                Assert.That(inLane, Is.True, $"bay {i + 1}: arrival starts in the service or entry lane, not the plaza ({start})");
                Assert.That(MeetLayout.ArrivalMetres / MeetLayout.ArrivalSeconds * 3.6f, Is.InRange(25f, 45f), "arrival speed km/h");
                Assert.That(MeetLayout.ArrivalSeconds, Is.InRange(3f, 4f));
                // Sample the polyline: inside the plateau it stays in the south entry lane or the side service lane, or within the bay's own sweep.
                for (int k = 1; k < path.Count; k++)
                    for (float t = 0f; t <= 1f; t += 0.05f)
                    {
                        float x = path[k - 1].X + (path[k].X - path[k - 1].X) * t, z = path[k - 1].Z + (path[k].Z - path[k - 1].Z) * t;
                        if (z < MeetLayout.WalkMinZ) continue;
                        bool entryLane = z <= MeetLayout.EntryLaneZ + 8f;
                        bool sideLane = System.Math.Abs(System.Math.Abs(x) - MeetLayout.LaneX) <= MeetLayout.LaneHalfWidth + 0.5f;
                        bool bayApproach = System.Math.Abs(x) >= 49f;
                        Assert.That(entryLane || sideLane || bayApproach, Is.True, $"bay {i + 1}: arrival crosses the plaza at ({x:0.0}, {z:0.0})");
                        Assert.That(MeetLayout.GardenIsland.Contains(x, z, 2f), Is.False);
                    }
            }
        }

        [Test]
        public void Layout_AllocatesFreeBaysAndKeepsConvoysTogether()
        {
            var occupied = new HashSet<int> { 0, 1 };
            int a = MeetLayout.AllocateBay(occupied);
            Assert.That(a, Is.EqualTo(2));
            int near = MeetLayout.AllocateBay(new HashSet<int> { 8 }, 8);
            Assert.That(MeetLayout.Bays[near].Side, Is.EqualTo("east"));
            Assert.That(System.Math.Abs(near - 8), Is.EqualTo(1));
            Assert.That(MeetLayout.AllocateBay(new HashSet<int>(Enumerable.Range(0, 12))), Is.EqualTo(-1));
        }

        // ------------------------------------------------------------------ boombox

        static double Len(string id) => id == "MUS_MEET" ? 120 : id.StartsWith("MUS_") ? 100 : 0;

        [Test]
        public void Boombox_LeaseIsExclusiveRenewableAndExpires()
        {
            var b = new BoomboxState("room", Len, 0);
            Assert.That(b.Acquire("a", 0), Is.EqualTo(BoomboxStatus.Ok));
            Assert.That(b.Acquire("b", 5_000), Is.EqualTo(BoomboxStatus.LeaseHeld));
            Assert.That(b.Acquire("a", 10_000), Is.EqualTo(BoomboxStatus.Ok), "renew");
            Assert.That(b.Acquire("b", 20_000), Is.EqualTo(BoomboxStatus.LeaseHeld), "renewed to 25 s");
            b.Tick(25_000);
            Assert.That(b.LeaseHolder, Is.Empty);
            Assert.That(b.Acquire("b", 25_001), Is.EqualTo(BoomboxStatus.Ok));
            Assert.That(b.Acquire("c", 25_002, inRange: false), Is.EqualTo(BoomboxStatus.OutOfRange));
            b.Release("b");
            Assert.That(b.Acquire("c", 25_003), Is.EqualTo(BoomboxStatus.Ok));
        }

        [Test]
        public void Boombox_QueueRules_OnePerPerson_SixMax_OwnershipAndInterval()
        {
            long t = 100_000;
            var b = new BoomboxState("room", Len, 0);
            Assert.That(b.Enqueue("a", "MUS_GARAGE", true, t), Is.EqualTo(BoomboxStatus.NoLease));
            b.Acquire("a", t);
            Assert.That(b.Enqueue("a", "MUS_RACE_KASUMI", false, t), Is.EqualTo(BoomboxStatus.NotOwned));
            Assert.That(b.Enqueue("a", "NOPE", true, t), Is.EqualTo(BoomboxStatus.UnknownTrack));
            Assert.That(b.Enqueue("a", "MUS_GARAGE", true, t), Is.EqualTo(BoomboxStatus.Ok));
            Assert.That(b.TrackId, Is.EqualTo("MUS_GARAGE"), "starts at once over the default bed");
            Assert.That(b.SubmittedBy, Is.EqualTo("a"));
            // One request per person: a second pick replaces the first.
            b.Enqueue("a", "MUS_MENU_A", true, t + 1);
            b.Enqueue("a", "MUS_MENU_B", true, t + 2);
            Assert.That(b.Queue.Count, Is.EqualTo(1));
            Assert.That(b.Queue[0].TrackId, Is.EqualTo("MUS_MENU_B"));
            b.Release("a");
            // Five more people fill the queue to six; the seventh is refused.
            for (int i = 0; i < 6; i++)
            {
                string p = "p" + i;
                b.Acquire(p, t + 10 + i);
                BoomboxStatus s = b.Enqueue(p, "MUS_TITLE", true, t + 10 + i);
                Assert.That(s, Is.EqualTo(i < 5 ? BoomboxStatus.Ok : BoomboxStatus.QueueFull), p);
                b.Release(p);
            }
            Assert.That(b.Queue.Count, Is.EqualTo(BoomboxState.MaxQueue));
            // Skips are rate-limited room-wide.
            b.Acquire("p0", t + 20);
            Assert.That(b.Skip("p0", t + 20), Is.EqualTo(BoomboxStatus.TooSoon));
            Assert.That(b.Skip("p0", t + 10_000), Is.EqualTo(BoomboxStatus.Ok));
            Assert.That(b.TrackId, Is.EqualTo("MUS_MENU_B"));
            Assert.That(b.Skip("p0", t + 15_000), Is.EqualTo(BoomboxStatus.TooSoon));
        }

        [Test]
        public void Boombox_LeaversLoseRequests_PlayingTrackFinishes_ThenDefaultReturns()
        {
            var b = new BoomboxState("room", Len, 0);
            b.Acquire("a", 0);
            b.Enqueue("a", "MUS_GARAGE", true, 0);
            b.Release("a");
            b.Acquire("b", 1);
            b.Enqueue("b", "MUS_TITLE", true, 1);
            b.Release("b");
            b.Acquire("a", 2);
            b.Enqueue("a", "MUS_MENU_A", true, 2); // a's next pick
            b.PlayerLeft("a", 3);
            Assert.That(b.TrackId, Is.EqualTo("MUS_GARAGE"), "a's playing track may finish");
            Assert.That(b.Queue.Select(r => r.PlayerId), Is.EquivalentTo(new[] { "b" }), "a's unplayed request removed");
            Assert.That(b.LeaseHolder, Is.Empty);
            b.Tick(99_000);
            Assert.That(b.TrackId, Is.EqualTo("MUS_GARAGE"));
            b.Tick(100_000);
            Assert.That(b.TrackId, Is.EqualTo("MUS_TITLE"));
            Assert.That(b.PositionSeconds(130_000), Is.EqualTo(30).Within(1e-6), "late joiners seek into the track");
            b.Tick(200_000);
            Assert.That(b.TrackId, Is.EqualTo(BoomboxState.DefaultCue));
            Assert.That(b.SubmittedBy, Is.Empty);
        }

        [Test]
        public void Boombox_SpoilerProtectionIsLocalAndGrantsNothing()
        {
            System.Func<string, bool> owns = id => id == "MUS_LT_DAIGO";
            Assert.That(BoomboxState.AudibleFor("MUS_FINAL_REINA", owns, true), Is.EqualTo(BoomboxState.DefaultCue));
            Assert.That(BoomboxState.AudibleFor("MUS_FINAL_REINA", owns, false), Is.EqualTo("MUS_FINAL_REINA"), "explicit opt-in");
            Assert.That(BoomboxState.AudibleFor("MUS_LT_DAIGO", owns, true), Is.EqualTo("MUS_LT_DAIGO"));
            Assert.That(BoomboxState.AudibleFor("MUS_RACE_KASUMI", owns, true), Is.EqualTo("MUS_RACE_KASUMI"), "ordinary cues are never protected");
            Assert.That(BoomboxState.TitleFor("MUS_FINAL_SHIORI", "Shiori's theme", owns, true), Does.Not.Contain("Shiori"));
        }
    }
}
