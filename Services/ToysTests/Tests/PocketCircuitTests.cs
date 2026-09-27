using NightSignal.Core.Toys;
using NightSignal.Core.Toys.PocketCircuit;
using Xunit.Abstractions;
using static NightSignal.Core.Toys.ToyActivityId;

namespace NightSignal.Toys.Tests;

/// <summary>Pocket Circuit (Addendum 02 §5, acceptance C05/C06).</summary>
public sealed class PocketCircuitTests
{
    readonly ITestOutputHelper output;
    public PocketCircuitTests(ITestOutputHelper output) { this.output = output; }

    /// <summary>A human-like 20 Hz driver: releases for corners using the lane's speed profile.</summary>
    public static double CarefulThrottle(PocketCircuitTable t, SlotCarState car)
    {
        SlotLane lane = t.Track.Lane(car.Lane);
        double[] profile = Profiles(t, car.Lane);
        double target = Math.Min(profile[lane.IndexAt(car.S)], profile[lane.IndexAt(lane.Wrap(car.S + 0.04 + car.V * 0.08))]);
        if (car.V > target) return 0;
        return Math.Clamp(target / t.Physics.MotorTopSpeed + 2.0 * (target - car.V) / t.Physics.MotorTopSpeed, 0, 1);
    }

    static readonly Dictionary<string, double[]> profiles = new();
    static double[] Profiles(PocketCircuitTable t, int lane)
    {
        string key = t.ActiveLayout.Id + "/" + lane;
        lock (profiles)
        {
            if (!profiles.TryGetValue(key, out double[] p)) profiles[key] = p = ReferenceDriver.SpeedProfile(t.Track.Lane(lane), t.Physics);
            return p;
        }
    }

    /// <summary>Sends throttle for each listed member every 50 ms (a real client's input rate) while time advances.</summary>
    public static void Drive(Rig rig, string[] members, long ms, Func<SlotCarState, double> policy = null)
    {
        long end = rig.Now + ms;
        while (rig.Now < end)
        {
            foreach (string m in members)
            {
                SlotCarState car = rig.S.PocketCircuit.Car(m);
                if (car == null) continue;
                double u = policy != null ? policy(car) : CarefulThrottle(rig.S.PocketCircuit, car);
                rig.Expect(rig.Do(m, PocketCircuit, "throttle", Payload.Of("value", u)));
            }
            rig.Tick(50);
        }
    }

    static void SelectLayout(Rig rig, string member, string layout)
    {
        if (rig.S.PocketCircuit.State.ActiveLayout == layout) return;
        rig.Expect(rig.Do(member, PocketCircuit, "layout.select", Payload.Of("layout", layout)));
        Assert.Equal(layout, rig.S.PocketCircuit.State.ActiveLayout);
    }

    static void DriveUntil(Rig rig, string m, Func<SlotCarState, bool> until, long maxMs = 60_000, Func<SlotCarState, double> policy = null)
    {
        long end = rig.Now + maxMs;
        while (rig.Now < end && !until(rig.S.PocketCircuit.Car(m))) Drive(rig, new[] { m }, 50, policy);
        Assert.True(until(rig.S.PocketCircuit.Car(m)), "condition not reached");
    }

    [Theory]
    [InlineData("workshop-oval", 1)]
    [InlineData("coil-pass", 6)]
    [InlineData("midnight-figure-eight", 3)]
    public void Every_layout_is_playable_with_real_throttle_input(string layout, int lane)
    {
        var rig = new Rig("a");
        SelectLayout(rig, "a", layout);
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", lane)));
        Drive(rig, new[] { "a" }, 60_000);
        List<SlotLap> laps = rig.S.PocketCircuit.Board.RecentLaps;
        foreach (SlotLap l in laps) output.WriteLine($"{layout} lane {l.Lane} lap {l.LapNumber}: {l.ActiveMs} ms {l.Category} norm {l.NormalizedMs}");
        List<SlotLap> clean = laps.Where(l => l.Category == LapCategory.Clean).ToList();
        Assert.True(clean.Count >= 2, "at least two clean laps");
        Assert.All(clean, l => Assert.InRange(l.ActiveMs, 10_000, 25_000)); // clean-lap tuning target
        Assert.Equal(clean.Min(l => l.ActiveMs), rig.S.PocketCircuit.LaneBest(lane).BestMs);
    }

    [Fact]
    public void Excess_speed_deslots_harmlessly_and_returns_to_the_last_safe_point()
    {
        var rig = new Rig("a");
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 1)));
        DriveUntil(rig, "a", c => c.Mode == SlotCarMode.DeSlotted, 30_000, c => 1.0); // flat out into the first sweeper
        SlotCarState car = rig.S.PocketCircuit.Car("a");
        double safe = car.LastSafeS;
        Assert.Equal(0, car.V);
        Assert.True((car.Flags & LapFlags.DeSlotted) != 0);
        rig.Tick(rig.S.PocketCircuit.Physics.DeslotReturnMs + 100);
        Assert.Equal(SlotCarMode.Driving, car.Mode);
        Assert.Equal(safe, car.S);
        // Finish that lap gently: it is completed for fun, but categorized as de-slotted and kept out of clean bests.
        int laps = car.TotalLaps;
        DriveUntil(rig, "a", c => c.TotalLaps > laps);
        SlotLap lap = rig.S.PocketCircuit.Board.RecentLaps.Last();
        Assert.Equal(LapCategory.DeSlotted, lap.Category);
        Assert.DoesNotContain(rig.S.PocketCircuit.Board.Bests, b => b.BestMs == lap.ActiveMs);
    }

    [Fact]
    public void Lap_categories_distinguish_voluntary_pause_preemption_and_interruption()
    {
        var rig = new Rig("a", "b", "c");
        for (int i = 0; i < 3; i++) rig.Expect(rig.Do(new[] { "a", "b", "c" }[i], PocketCircuit, "lane.take", Payload.Of("lane", i + 1)));
        string[] all = { "a", "b", "c" };
        // Everyone gets onto a timed lap.
        long end = rig.Now + 30_000;
        while (rig.Now < end && all.Any(m => rig.S.PocketCircuit.Car(m).Lap < 1 || rig.S.PocketCircuit.Car(m).LapTicks < 360)) Drive(rig, all, 50);

        rig.S.Close("a", PocketCircuit, rig.Now);   // a closes the view mid-lap
        rig.S.Disconnect("c", rig.Now);             // c drops
        rig.Tick(2_000);
        rig.S.Pause(rig.Now);                       // the main event starts with b mid-lap
        rig.Tick(30_000);
        rig.S.EndPause(rig.Now);
        rig.S.Join("c", 2, rig.Now);
        rig.ResetSequence("c");
        rig.Expect(rig.Do("b", PocketCircuit, "resume"));
        foreach (string m in all)
        {
            int before = rig.S.PocketCircuit.Car(m).TotalLaps;
            DriveUntil(rig, m, c => c.TotalLaps > before);
        }
        Dictionary<string, LapCategory> cat = all.ToDictionary(m => m, m => rig.S.PocketCircuit.Board.RecentLaps.Last(l => l.Member == m).Category);
        Assert.Equal(LapCategory.Preempted, cat["a"]);  // a's paused lap was ALSO preempted by the event
        Assert.Equal(LapCategory.Preempted, cat["b"]);
        Assert.Equal(LapCategory.Preempted, cat["c"]);

        // Without a main event: voluntary close vs disconnect.
        var rig2 = new Rig("a", "c");
        rig2.Expect(rig2.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 1)));
        rig2.Expect(rig2.Do("c", PocketCircuit, "lane.take", Payload.Of("lane", 2)));
        end = rig2.Now + 30_000;
        while (rig2.Now < end && new[] { "a", "c" }.Any(m => rig2.S.PocketCircuit.Car(m).Lap < 1 || rig2.S.PocketCircuit.Car(m).LapTicks < 360)) Drive(rig2, new[] { "a", "c" }, 50);
        rig2.S.Close("a", PocketCircuit, rig2.Now);
        rig2.S.Disconnect("c", rig2.Now);
        rig2.Tick(3_000);
        rig2.S.Join("c", 2, rig2.Now);
        rig2.ResetSequence("c");
        foreach (string m in new[] { "a", "c" })
        {
            int before = rig2.S.PocketCircuit.Car(m).TotalLaps;
            DriveUntil(rig2, m, c => c.TotalLaps > before);
        }
        Assert.Equal(LapCategory.VoluntarilyPaused, rig2.S.PocketCircuit.Board.RecentLaps.Last(l => l.Member == "a").Category);
        Assert.Equal(LapCategory.Interrupted, rig2.S.PocketCircuit.Board.RecentLaps.Last(l => l.Member == "c").Category);
        Assert.All(rig2.S.PocketCircuit.Board.Bests, b => Assert.Contains(rig2.S.PocketCircuit.Board.RecentLaps, l => l.Category == LapCategory.Clean && l.ActiveMs == b.BestMs));
    }

    [Fact]
    public void Lap_time_counts_active_time_only()
    {
        var rig = new Rig("a");
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 3)));
        DriveUntil(rig, "a", c => c.Lap >= 1 && c.LapTicks > 240);
        SlotCarState car = rig.S.PocketCircuit.Car("a");
        rig.S.Close("a", PocketCircuit, rig.Now);
        long ticks = car.LapTicks;
        rig.Tick(30_000); // reading a panel, racing, waiting
        Assert.Equal(ticks, car.LapTicks);
        rig.Expect(rig.Do("a", PocketCircuit, "throttle", Payload.Of("value", 0.5))); // returns: orientation first
        Assert.Equal(SlotCarMode.Orienting, car.Mode);
        rig.Tick(ToyLimits.OrientationMs - 100);
        Assert.Equal(ticks, car.LapTicks);
        Drive(rig, new[] { "a" }, 1000);
        Assert.True(car.LapTicks > ticks);
    }

    [Fact]
    public void Closing_the_view_parks_only_that_players_car()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 1)));
        rig.Expect(rig.Do("b", PocketCircuit, "lane.take", Payload.Of("lane", 2)));
        Drive(rig, new[] { "a", "b" }, 5_000);
        rig.S.Close("a", PocketCircuit, rig.Now);
        SlotCarState a = rig.S.PocketCircuit.Car("a"), b = rig.S.PocketCircuit.Car("b");
        double aS = a.S, bS = b.S;
        Drive(rig, new[] { "b" }, 3_000);
        Assert.Equal(SlotCarMode.Parked, a.Mode);
        Assert.Equal(aS, a.S);
        Assert.NotEqual(bS, b.S);
        Assert.Equal(SlotCarMode.Driving, b.Mode);
    }

    [Fact]
    public void Resume_never_applies_a_stale_held_throttle()
    {
        var rig = new Rig("a");
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 3)));
        Drive(rig, new[] { "a" }, 3_000, c => 0.6);
        SlotCarState car = rig.S.PocketCircuit.Car("a");
        Assert.True(car.V > 0.2 && car.Throttle > 0.5);
        rig.S.Pause(rig.Now);
        double v = car.V, s = car.S;
        Assert.Equal(0, car.Throttle);
        Assert.Equal(SlotCarMode.Parked, car.Mode);
        rig.Tick(60_000);
        rig.S.EndPause(rig.Now);
        Assert.Equal(s, car.S);
        Assert.Equal(v, car.V); // velocity kept for the resume
        rig.Expect(rig.Do("a", PocketCircuit, "throttle", Payload.Of("value", 1.0))); // first input: orientation
        Assert.Equal(SlotCarMode.Orienting, car.Mode);
        rig.Expect(rig.Do("a", PocketCircuit, "throttle", Payload.Of("value", 1.0))); // ignored while orienting
        Assert.Equal(0, car.Throttle);
        rig.Tick(ToyLimits.OrientationMs + 100);
        Assert.Equal(SlotCarMode.Driving, car.Mode);
        Assert.Equal(0, car.Throttle); // nothing held over: control returns at zero throttle
        Assert.True(car.V < v); // coasting/braking from the saved velocity, not launching
        rig.Expect(rig.Do("a", PocketCircuit, "throttle", Payload.Of("value", 0.4)));
        Assert.Equal(0.4, car.Throttle);
    }

    [Fact]
    public void Held_input_expires_without_refresh()
    {
        var rig = new Rig("a");
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 2)));
        rig.Tick(ToyLimits.OrientationMs + 100);
        rig.Expect(rig.Do("a", PocketCircuit, "throttle", Payload.Of("value", 0.7)));
        Assert.Equal(0.7, rig.S.PocketCircuit.Car("a").Throttle);
        rig.Tick(rig.S.PocketCircuit.Physics.ThrottleHoldMs + 100);
        Assert.Equal(0, rig.S.PocketCircuit.Car("a").Throttle);
        rig.Expect(rig.Do("a", PocketCircuit, "throttle", Payload.Of("value", 0.7)));
        rig.S.FocusLost("a", rig.Now);
        Assert.Equal(0, rig.S.PocketCircuit.Car("a").Throttle);
        Assert.Equal(SlotCarMode.Parked, rig.S.PocketCircuit.Car("a").Mode);
    }

    [Fact]
    public void Layout_change_needs_active_users_consent_and_keeps_each_layouts_board()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 1)));
        rig.Expect(rig.Do("b", PocketCircuit, "lane.take", Payload.Of("lane", 2)));
        Drive(rig, new[] { "a", "b" }, 30_000);
        int ovalLaps = rig.S.PocketCircuit.Board.RecentLaps.Count;
        Assert.True(ovalLaps > 0);

        string p1 = rig.Do("a", PocketCircuit, "layout.select", Payload.Of("layout", "coil-pass")).Value;
        Drive(rig, new[] { "b" }, ToyLimits.ProposalLifetimeMs + 500); // b keeps lapping, says nothing
        Assert.Equal("workshop-oval", rig.S.PocketCircuit.State.ActiveLayout);
        Assert.Equal(ProposalState.Lapsed, rig.S.PocketCircuit.State.LastProposal.State);

        string p2 = rig.Do("a", PocketCircuit, "layout.select", Payload.Of("layout", "coil-pass")).Value;
        rig.Expect(rig.Do("b", PocketCircuit, "proposal.vote", Payload.Of("proposal", p2, "accept", false)));
        Assert.Equal("workshop-oval", rig.S.PocketCircuit.State.ActiveLayout);
        Assert.NotEqual(p1, p2);

        int epoch = rig.S.PocketCircuit.Run.Epoch;
        string p3 = rig.Do("a", PocketCircuit, "layout.select", Payload.Of("layout", "coil-pass")).Value;
        rig.Expect(rig.Do("b", PocketCircuit, "proposal.vote", Payload.Of("proposal", p3, "accept", true)));
        Assert.Equal("coil-pass", rig.S.PocketCircuit.State.ActiveLayout);
        Assert.Equal(epoch + 1, rig.S.PocketCircuit.Run.Epoch);
        // A delayed throttle packet for the old layout epoch cannot touch the new table.
        Assert.Equal(ToyReason.StaleEpoch, rig.Do("b", PocketCircuit, "throttle", Payload.Of("value", 1.0), epoch: epoch).Reason);
        SlotLayoutBoard oval = rig.S.PocketCircuit.State.Boards["workshop-oval"];
        Assert.True(oval.RecentLaps.Count >= ovalLaps);
        Assert.All(oval.Cars, c => Assert.Equal(SlotCarMode.Parked, c.Mode));
    }

    [Fact]
    public void Lane_fairness_numbers_are_measured_and_disclosed()
    {
        foreach (SlotLayoutDef l in ToyData.Content.PocketCircuit.Layouts)
        {
            SlotTrack t = l.Track;
            output.WriteLine($"{l.Name}: closure {t.ClosureErrorM * 1000:0.00} mm, crossing {(t.HasCrossing ? $"yes, clearance {t.CrossingClearanceM * 100:0.0} cm" : "no")}");
            foreach (SlotLaneStats s in t.Stats)
                output.WriteLine($"  lane {s.Lane}: length {s.LengthM:0.000} m, max curvature {s.MaxCurvature:0.00}/m, reference lap {s.ReferenceLapMs / 1000:0.00} s " +
                                 $"(de-slots {s.ReferenceDeslots}), common input {s.CommonInputLapMs / 1000:0.00} s (de-slots {s.CommonInputDeslots}), lane ratio {s.LaneRatio:0.0000}");
            Assert.Equal(6, t.Stats.Count);
            Assert.All(t.Stats, s => Assert.Equal(0, s.ReferenceDeslots));
            Assert.All(t.Stats, s => Assert.InRange(s.ReferenceLapMs, 10_000, 25_000));
            Assert.All(t.Stats, s => Assert.True(double.IsFinite(s.CommonInputLapMs)));
            Assert.All(t.Stats, s => Assert.InRange(s.LaneRatio, 0.9, 1.1));
            Assert.Equal(1.0, t.Stats.Average(s => s.LaneRatio), 3);
        }
        SlotTrack eight = ToyData.Content.PocketCircuit.Layout("midnight-figure-eight").Track;
        Assert.True(eight.HasCrossing);
        Assert.True(eight.CrossingClearanceM >= SlotTrack.MinCrossingClearance);
        Assert.False(ToyData.Content.PocketCircuit.Layout("workshop-oval").Track.HasCrossing);
        SlotTrack coil = ToyData.Content.PocketCircuit.Layout("coil-pass").Track;
        Assert.True(coil.Centreline.Max(p => p.Z) - coil.Centreline.Min(p => p.Z) >= 0.05); // real change of elevation
    }

    [Fact]
    public void All_cars_have_equal_performance_on_the_same_lane()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 4)));
        DriveUntil(rig, "a", c => c.TotalLaps >= 2, 60_000);
        rig.Expect(rig.Do("a", PocketCircuit, "lane.leave"));
        rig.Expect(rig.Do("b", PocketCircuit, "lane.take", Payload.Of("lane", 4)));
        DriveUntil(rig, "b", c => c.TotalLaps >= 2, 60_000);
        long aLap = rig.S.PocketCircuit.Board.RecentLaps.Where(l => l.Member == "a" && l.LapNumber == 2).Single().ActiveMs;
        long bLap = rig.S.PocketCircuit.Board.RecentLaps.Where(l => l.Member == "b" && l.LapNumber == 2).Single().ActiveMs;
        Assert.InRange(Math.Abs(aLap - bLap), 0, 50); // same input policy, same physics; only 50 ms input sampling differs
        // There is no way to feed parts, purchases, ranks or car classes into the toy model.
        string[] fields = typeof(SlotCarState).GetFields().Select(f => f.Name).ToArray();
        Assert.DoesNotContain(fields, f => f.Contains("Part") || f.Contains("Tun") || f.Contains("Class") || f.Contains("Rank") || f.Contains("Power") || f.Contains("Grip"));
    }

    [Fact]
    public void Six_clients_lap_independently_and_one_dropout_does_not_stop_the_others()
    {
        string[] six = { "a", "b", "c", "d", "e", "f" };
        var rig = new Rig(six);
        for (int i = 0; i < 6; i++) rig.Expect(rig.Do(six[i], PocketCircuit, "lane.take", Payload.Of("lane", i + 1)));
        Assert.Equal(ToyReason.LeaseHeld, rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 2)).Reason);
        Drive(rig, six, 30_000);
        Assert.All(six, m => Assert.True(rig.S.PocketCircuit.Car(m).TotalLaps >= 1, m));
        rig.S.Disconnect("f", rig.Now);
        var laps = six.Take(5).ToDictionary(m => m, m => rig.S.PocketCircuit.Car(m).TotalLaps);
        Drive(rig, six.Take(5).ToArray(), 30_000);
        Assert.All(six.Take(5), m => Assert.True(rig.S.PocketCircuit.Car(m).TotalLaps > laps[m], m));
        Assert.Equal(SlotCarMode.Parked, rig.S.PocketCircuit.Car("f").Mode);
        // Late join: a newcomer can reuse the dormant member's lane; f's records stay in the history.
        Assert.Equal(JoinOutcome.NewSeat, rig.S.Join("g", 1, rig.Now));
        rig.Expect(rig.Do("g", PocketCircuit, "lane.take", Payload.Of("lane", 6)));
        Assert.Null(rig.S.PocketCircuit.Car("f"));
        Assert.Contains(rig.S.PocketCircuit.Board.RecentLaps, l => l.Member == "f");
    }

    [Fact]
    public void Cooperative_clean_lap_collection_can_be_filled_solo()
    {
        var rig = new Rig("a");
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 3)));
        SlotLayoutBoard b = rig.S.PocketCircuit.Board;
        DriveUntil(rig, "a", c => b.CoopCompleted >= 1, 400_000);
        Assert.Equal(b.CoopTarget, b.CoopContributions["a"]);
    }
}
