using Newtonsoft.Json.Linq;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.Canvas;
using NightSignal.Core.Toys.CapClash;
using NightSignal.Core.Toys.Greenlight;
using NightSignal.Core.Toys.PitCrew;
using NightSignal.Core.Toys.PocketCircuit;
using Xunit.Abstractions;
using static NightSignal.Core.Toys.ToyActivityId;

namespace NightSignal.Toys.Tests;

/// <summary>Main-event preemption across all toys at once (Addendum 02 §1.3–1.5; acceptance B03–B06, B10).</summary>
public sealed class PreemptionTests
{
    readonly ITestOutputHelper output;
    public PreemptionTests(ITestOutputHelper output) { this.output = output; }

    internal sealed class Busy
    {
        public Rig Rig;
        public string Stroke;
        public OperationDef Op;
        public GreenlightAttempt Attempt;
    }

    /// <summary>
    /// Six members split across the toys: a cap sliding, a slot car mid-corner, a task mid-way, a stroke in progress and an
    /// exposed reaction cue — exactly the B03 situation.
    /// </summary>
    internal static Busy MakeBusyConvoy()
    {
        var rig = new Rig("cap", "car", "crew", "art", "gl", "idle");
        var busy = new Busy { Rig = rig };
        rig.Expect(rig.Do("car", PocketCircuit, "lane.take", Payload.Of("lane", 2)));
        // Greenlight: start the micro-attempt now; its lights go out after the hidden delay.
        rig.Expect(rig.Do("gl", Greenlight, "attempt.start", Payload.Of("variant", "LightsOut")));
        busy.Attempt = rig.S.Greenlight.OpenAttempt("gl");
        long delay = GreenlightRules.Cue(busy.Attempt.Variant, busy.Attempt.Setting, busy.Attempt.Seed).HiddenDelayMs;
        // Pit crew: half of a 3-step operation.
        busy.Op = rig.S.PitCrew.Blueprint.Operation("co-21");
        rig.Expect(rig.Do("crew", PitCrew, "task.claim", Payload.Of("op", busy.Op.Id)));
        rig.Expect(rig.Do("crew", PitCrew, "task.step", Payload.Of("op", busy.Op.Id, "step", 0, "error", 1.0)));
        // Canvas: a stroke still being drawn.
        CanvasSheet s = rig.S.Canvas.Document.Sheets[0];
        busy.Stroke = rig.Do("art", Canvas, "stroke.begin", Payload.Of("sheet", s.SheetId, "sheetEpoch", s.Epoch, "color", 0xFF00AAFFL, "width", 6, "points", new[] { 100, 100, 110, 104 })).Value;
        rig.Expect(rig.Do("art", Canvas, "stroke.append", Payload.Of("sheet", s.SheetId, "sheetEpoch", s.Epoch, "object", busy.Stroke, "points", new[] { 120, 109, 131, 115 })));

        SlotLane lane = rig.S.PocketCircuit.Track.Lane(2);
        bool InCorner()
        {
            SlotCarState c = rig.S.PocketCircuit.Car("car");
            return c.Lap >= 1 && c.Mode == SlotCarMode.Driving && Math.Abs(lane.Curvature[lane.IndexAt(c.S)]) > 1.5 && c.V > 0.3;
        }
        // Keep driving (fresh throttle every 50 ms) until the car is on a timed lap, entering a corner, with the cue exposed.
        long end = rig.Now + 90_000;
        while (rig.Now < end && !(InCorner() && rig.Now - busy.Attempt.IssuedAtMs > delay + 50)) PocketCircuitTests.Drive(rig, new[] { "car" }, 50);
        Assert.True(InCorner());
        // Cap: fire now; the pause will commit while it is still sliding and the car is still mid-corner.
        rig.Expect(rig.Do("cap", CapClash, "shot.submit", Payload.Of("angle", 2.0, "power", 0.8, "launchX", 0.0, "seen", rig.S.CapClash.Board.BoardRevision)));
        PocketCircuitTests.Drive(rig, new[] { "car" }, 300);
        Assert.True(InCorner(), "still mid-corner");
        return busy;
    }

    [Fact]
    public void B03_B04_everything_suspends_without_waiting_and_restores_exactly_after_the_event()
    {
        Busy b = MakeBusyConvoy();
        Rig rig = b.Rig;
        CapBody cap = rig.S.CapClash.Board.Caps.Single(c => c.Owner == "cap");
        SlotCarState car = rig.S.PocketCircuit.Car("car");
        output.WriteLine($"at pause: cap moving={cap.Moving} v={cap.Vel.Length():0.000}; car s={car.S:0.000} v={car.V:0.000} lapTicks={car.LapTicks}");

        PauseReceipt receipt = rig.S.Pause(rig.Now); // returns immediately: nothing is awaited
        Assert.Equal(SessionLifecycle.Paused, rig.S.Lifecycle);
        Assert.True(receipt.BoundaryRevision > 0);

        // Moving bodies keep position + velocity.
        Assert.True(cap.Moving && cap.Vel.Length() > 0.05, "the cap was still sliding at the boundary");
        Assert.Equal(SlotCarMode.Parked, car.Mode);
        Assert.True(car.V > 0.2);
        Assert.Equal(0, car.Throttle);
        Assert.True((car.Flags & LapFlags.Preempted) != 0);
        // Task progress kept, control released; stroke finalized at its last accepted point; exposed cue neutral.
        OperationProgress op = rig.S.PitCrew.State.Project.Ops[b.Op.Id];
        Assert.Equal(1, op.StepsDone);
        Assert.Null(op.Holder);
        CanvasObject stroke = rig.S.Canvas.Document.Sheets[0].Objects.Single(o => o.Id == b.Stroke);
        Assert.False(stroke.Open);
        Assert.Equal(new[] { 100, 100, 110, 104, 120, 109, 131, 115 }, stroke.Points);
        Assert.Equal(AttemptOutcome.Interrupted, b.Attempt.Outcome);

        string saved = DowntimeCodec.Serialize(rig.S.Snapshot());
        output.WriteLine($"pause snapshot: {saved.Length} bytes");
        string drawing = CanvasCodec.DrawingHash(rig.S.Canvas.Document);

        // The real event runs for three minutes. Nothing accrues, nothing moves.
        rig.Tick(180_000);
        DowntimeSnapshot during = rig.S.Snapshot();
        during.ClockMs = DowntimeCodec.Deserialize(saved).ClockMs;
        Assert.Equal(saved, DowntimeCodec.Serialize(during));

        // After the event (here: from the stored snapshot, as after a service restart) the state is identical.
        DowntimeSession restored = DowntimeSession.Restore(DowntimeCodec.Deserialize(saved), ToyData.Content, rig.Now);
        DowntimeSnapshot again = restored.Snapshot();
        again.ClockMs = DowntimeCodec.Deserialize(saved).ClockMs;
        Assert.Equal(saved, DowntimeCodec.Serialize(again));
        CapBody rCap = restored.CapClash.Board.Caps.Single(c => c.Owner == "cap");
        Assert.Equal(cap.Pos, rCap.Pos);
        Assert.Equal(cap.Vel, rCap.Vel);
        SlotCarState rCar = restored.PocketCircuit.Car("car");
        Assert.Equal(car.S, rCar.S);
        Assert.Equal(car.V, rCar.V);
        Assert.Equal(car.LapTicks, rCar.LapTicks);
        Assert.Equal(drawing, CanvasCodec.DrawingHash(restored.Canvas.Document));
        Assert.Equal(1, restored.PitCrew.State.Project.Ops[b.Op.Id].StepsDone);

        // Resume: moving toys wait for an actual participant; nobody else is forced back.
        var r2 = new Rig(restored, rig.Now);
        restored.EndPause(r2.Now);
        r2.Tick(10_000);
        Assert.Equal(cap.Pos, rCap.Pos);
        Assert.Equal(car.S, rCar.S);
        r2.Expect(r2.Do("cap", CapClash, "resume"));
        long settleBy = r2.Now + 20_000;
        while (r2.Now < settleBy && !restored.CapClash.IsSettled) r2.Tick(100);
        Assert.True(restored.CapClash.IsSettled);
        Assert.Equal("cap", restored.CapClash.Board.History.Last().Member);
        Assert.Equal(car.S, rCar.S); // the slot car is still parked: its driver has not come back
    }

    [Fact]
    public void B05_inputs_around_the_pause_boundary_apply_at_most_once_to_the_correct_epoch()
    {
        var rig = new Rig("a", "b");
        CanvasSheet s = rig.S.Canvas.Document.Sheets[0];
        ToyCommand before = rig.Cmd("a", Canvas, "stroke.begin", Payload.Of("sheet", s.SheetId, "sheetEpoch", s.Epoch, "color", 0L, "width", 3, "points", new[] { 1, 1 }));
        rig.Expect(rig.S.Submit(before, rig.Now));
        ToyCommand inFlight = rig.Cmd("b", Canvas, "stroke.begin", Payload.Of("sheet", s.SheetId, "sheetEpoch", s.Epoch, "color", 0L, "width", 3, "points", new[] { 2, 2 }));

        PauseReceipt p = rig.S.Pause(rig.Now);
        Assert.Equal(p.BoundaryRevision, rig.S.Revision);
        // Accepted before the boundary: a retry is a duplicate, never a second stroke.
        Assert.True(rig.Resend(before).Duplicate);
        // Sent before the boundary but arriving after it: explicitly deferred during the pause ...
        ToyResult deferred = rig.S.Submit(inFlight, rig.Now);
        Assert.Equal(ToyVerdict.Deferred, deferred.Verdict);
        Assert.Equal(ToyReason.SessionPaused, deferred.Reason);
        Assert.Equal(p.BoundaryRevision, rig.S.Revision); // nothing sneaked into the frozen snapshot
        rig.S.EndPause(rig.Now);
        // ... and rejected afterwards because it carries the pre-pause epoch (cannot mutate the resumed instance).
        Assert.Equal(ToyReason.StaleEpoch, rig.Resend(inFlight).Reason);
        // Resubmitted by the client with the new epoch: applied exactly once.
        ToyCommand fresh = inFlight.Clone();
        fresh.Epoch = rig.S.Canvas.Run.Epoch;
        ToyResult ok = rig.Resend(fresh);
        rig.Expect(ok);
        Assert.True(rig.Resend(fresh).Duplicate);
        Assert.Equal(2, s.Objects.Count);
    }

    [Fact]
    public void B06_aborted_load_and_retry_duplicate_nothing_and_lose_nothing()
    {
        Busy b = MakeBusyConvoy();
        Rig rig = b.Rig;
        rig.S.Pause(rig.Now);
        string first = DowntimeCodec.Serialize(rig.S.Snapshot());
        long rev = rig.S.Revision;
        rig.S.EndPause(rig.Now); // main loading aborted
        rig.Tick(5_000);
        rig.S.Pause(rig.Now);    // retry start
        rig.S.EndPause(rig.Now); // aborted again
        rig.S.Pause(rig.Now);    // second retry

        Assert.Single(rig.S.CapClash.Board.Caps, c => c.Owner == "cap");
        Assert.Single(rig.S.PocketCircuit.Board.Cars, c => c.Member == "car");
        Assert.Single(rig.S.Canvas.Document.Sheets[0].Objects);
        Assert.Null(rig.S.Greenlight.OpenAttempt("gl"));
        Assert.Equal(1, rig.S.Greenlight.State.Recent.Count(a => a.Member == "gl"));
        Assert.Equal(1, rig.S.PitCrew.State.Project.Ops[b.Op.Id].StepsDone);
        DowntimeSnapshot now = rig.S.Snapshot();
        DowntimeSnapshot then = DowntimeCodec.Deserialize(first);
        Assert.Equal(then.CapClash.Boards["workbench-bullseye"].Caps.Single().Pos, now.CapClash.Boards["workbench-bullseye"].Caps.Single().Pos);
        Assert.Equal(then.PocketCircuit.Boards["workshop-oval"].Cars.Single().LapTicks, now.PocketCircuit.Boards["workshop-oval"].Cars.Single().LapTicks);
        Assert.Equal(then.PocketCircuit.Boards["workshop-oval"].RecentLaps.Count, now.PocketCircuit.Boards["workshop-oval"].RecentLaps.Count);
        Assert.True(rig.S.Revision > rev); // lifecycle changes are revisions, but no toy content was duplicated
    }

    [Fact]
    public void B10_joining_another_convoy_retires_moving_pieces_rejects_old_commands_and_keeps_artwork()
    {
        Busy b = MakeBusyConvoy();
        Rig rig = b.Rig;
        rig.Expect(rig.Do("art", CapClash, "shot.submit", Payload.Of("angle", 0.0, "power", 0.5, "launchX", 0.1, "seen", 0))); // queued behind the sliding cap
        rig.Expect(rig.Do("art", PocketCircuit, "lane.take", Payload.Of("lane", 5)));
        string drawing = CanvasCodec.DrawingHash(rig.S.Canvas.Document);

        rig.S.Leave("art", DepartureReason.JoinedOtherConvoy, rig.Now);
        Assert.DoesNotContain(rig.S.CapClash.State.Queue, q => q.Member == "art");
        Assert.Null(rig.S.PocketCircuit.Car("art"));
        Assert.Equal(drawing, CanvasCodec.DrawingHash(rig.S.Canvas.Document)); // accepted artwork is not erased
        Assert.Contains(rig.S.Canvas.Document.Sheets[0].Objects, o => o.Author == "art");

        // The old member's commands can no longer touch this convoy's toys.
        CanvasSheet s = rig.S.Canvas.Document.Sheets[0];
        ToyResult old = rig.Do("art", Canvas, "object.erase", Payload.Of("sheet", s.SheetId, "sheetEpoch", s.Epoch, "object", b.Stroke));
        Assert.Equal(ToyReason.StaleMembership, old.Reason);
        Assert.Contains(s.Objects, o => o.Id == b.Stroke && !o.Deleted);

        // Their new convoy is a separate session; its commands never reach this board.
        var other = new DowntimeSession("convoy-2", ToyData.Content, rig.Now, 7);
        other.Join("art", 1, rig.Now);
        ToyCommand elsewhere = other.Command("art", Canvas, "stroke.begin", Payload.Of("sheet", "sheet-1", "sheetEpoch", 1, "color", 0L, "width", 2, "points", new[] { 1, 1 }), 1, "x-1");
        rig.Expect(other.Submit(elsewhere, rig.Now));
        Assert.Equal(ToyReason.WrongSession, rig.S.Submit(elsewhere, rig.Now).Reason);
        Assert.Equal(drawing, CanvasCodec.DrawingHash(rig.S.Canvas.Document));

        // Coming back later is a NEW membership generation with no stale rights (no undo history carried over).
        Assert.Equal(JoinOutcome.NewSeat, rig.S.Join("art", 2, rig.Now));
        Assert.DoesNotContain(rig.S.Canvas.Document.Histories, h => h.Member == "art");
    }

    [Fact]
    public void B07_disconnect_keeps_state_and_rejoin_restores_the_dormant_identity()
    {
        Busy b = MakeBusyConvoy();
        Rig rig = b.Rig;
        rig.S.Disconnect("car", rig.Now);
        rig.S.Disconnect("crew", rig.Now);
        SlotCarState car = rig.S.PocketCircuit.Car("car");
        Assert.Equal(SlotCarMode.Parked, car.Mode);
        Assert.Null(rig.S.PitCrew.State.Project.Ops[b.Op.Id].Holder);
        // Others continue.
        rig.Expect(rig.Do("idle", PitCrew, "task.claim", Payload.Of("op", b.Op.Id)));
        Assert.Equal(JoinOutcome.RestoredDormant, rig.S.Join("car", 2, rig.Now));
        rig.ResetSequence("car");
        Assert.Same(car, rig.S.PocketCircuit.Car("car")); // no second car was spawned
        Assert.Single(rig.S.PocketCircuit.Board.Cars, c => c.Member == "car");
    }
}
