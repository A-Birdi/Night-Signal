using Newtonsoft.Json.Linq;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.Canvas;
using static NightSignal.Core.Toys.ToyActivityId;

namespace NightSignal.Toys.Tests;

/// <summary>Shared downtime framework: envelope, identity, sequence, idempotency, rate/size limits, membership, lifetime.</summary>
public sealed class SessionTests
{
    static JObject StrokePayload(DowntimeSession s, params int[] pts)
    {
        CanvasSheet sheet = s.Canvas.Document.Sheets[0];
        return Payload.Of("sheet", sheet.SheetId, "sheetEpoch", sheet.Epoch, "color", 0xFF2266FFL, "width", 6, "points", pts);
    }

    [Fact]
    public void Duplicate_request_id_returns_the_original_result_and_applies_once()
    {
        var rig = new Rig("a");
        ToyCommand cmd = rig.Cmd("a", Canvas, "stroke.begin", StrokePayload(rig.S, 10, 10, 20, 20));
        ToyResult first = rig.S.Submit(cmd, rig.Now);
        rig.Expect(first);
        ToyResult again = rig.Resend(cmd);
        Assert.True(again.Accepted);
        Assert.True(again.Duplicate);
        Assert.Equal(first.Value, again.Value);
        Assert.Equal(first.Revision, again.Revision);
        Assert.Single(rig.S.Canvas.Document.Sheets[0].Objects);
    }

    [Fact]
    public void Old_or_replayed_sequence_numbers_are_rejected()
    {
        var rig = new Rig("a");
        rig.Expect(rig.Do("a", Canvas, "stroke.begin", StrokePayload(rig.S, 1, 1)));
        ToyCommand stale = rig.Cmd("a", Canvas, "stroke.begin", StrokePayload(rig.S, 2, 2));
        stale.Sequence = 1; // not above the last accepted sequence
        Assert.Equal(ToyReason.OutOfOrder, rig.S.Submit(stale, rig.Now).Reason);
    }

    [Fact]
    public void Non_members_other_sessions_and_stale_generations_cannot_submit()
    {
        var rig = new Rig("a");
        ToyCommand visitor = rig.S.Command("visitor", Canvas, "stroke.begin", StrokePayload(rig.S, 1, 1), 1, "v-1");
        Assert.Equal(ToyReason.NotMember, rig.S.Submit(visitor, rig.Now).Reason); // A07: a public visitor cannot edit

        ToyCommand wrongSession = rig.Cmd("a", Canvas, "stroke.begin", StrokePayload(rig.S, 1, 1));
        wrongSession.SessionId = "convoy-2";
        Assert.Equal(ToyReason.WrongSession, rig.S.Submit(wrongSession, rig.Now).Reason);

        // Rejoin with a newer membership generation: the old generation's commands are dead.
        rig.S.Disconnect("a", rig.Now);
        Assert.Equal(JoinOutcome.RestoredDormant, rig.S.Join("a", 2, rig.Now));
        ToyCommand oldGen = rig.S.Command("a", Canvas, "stroke.begin", StrokePayload(rig.S, 1, 1), 99, "old-gen");
        oldGen.Generation = 1;
        Assert.Equal(ToyReason.StaleMembership, rig.S.Submit(oldGen, rig.Now).Reason);
        Assert.Equal(JoinOutcome.RejectedStaleGeneration, rig.S.Join("a", 1, rig.Now));
    }

    [Fact]
    public void New_generation_restarts_its_sequence_space_and_holds_no_control()
    {
        var rig = new Rig("a");
        for (int i = 0; i < 5; i++) rig.Expect(rig.Do("a", Canvas, "stroke.begin", StrokePayload(rig.S, i, i)));
        Assert.Equal(Canvas, rig.S.Seat("a").Controlled);
        rig.S.Disconnect("a", rig.Now);
        Assert.Equal(JoinOutcome.RestoredDormant, rig.S.Join("a", 2, rig.Now));
        Assert.Null(rig.S.Seat("a").Controlled);
        Assert.Equal(0, rig.S.Seat("a").LastSequence);
        rig.ResetSequence("a");
        rig.Expect(rig.Do("a", Canvas, "stroke.begin", StrokePayload(rig.S, 9, 9)));
    }

    [Fact]
    public void Rate_limit_bounds_each_member_and_refills_over_time()
    {
        var rig = new Rig("a");
        int accepted = 0, limited = 0;
        for (int i = 0; i < 120; i++)
        {
            ToyResult r = rig.Do("a", Greenlight, "series.create", Payload.Of("variant", "LightsOut", "count", 1));
            if (r.Accepted) accepted++;
            else if (r.Reason == ToyReason.RateLimited) limited++;
        }
        Assert.Equal((int)ToyLimits.RateBurst, accepted);
        Assert.Equal(120 - accepted, limited);
        rig.Tick(1000);
        rig.Expect(rig.Do("a", Greenlight, "series.create", Payload.Of("variant", "LightsOut", "count", 1)));
    }

    [Fact]
    public void Oversized_or_deep_wire_commands_are_rejected_before_interpretation()
    {
        var rig = new Rig("a");
        ToyCommand cmd = rig.Cmd("a", Canvas, "text.add", Payload.Of("text", new string('x', 9000)));
        string json = ToyCommandCodec.Serialize(cmd);
        Assert.False(ToyCommandCodec.TryParse(json, out _, out ToyReason reason));
        Assert.Equal(ToyReason.TooLarge, reason);

        string deep = "{\"session\":\"convoy-1\",\"activity\":\"Canvas\",\"epoch\":1,\"member\":\"a\",\"gen\":1,\"seq\":1,\"req\":\"x\",\"kind\":\"k\",\"payload\":" +
                      new string('[', 20) + new string(']', 20) + "}";
        Assert.False(ToyCommandCodec.TryParse(deep, out _, out reason));
        Assert.Equal(ToyReason.Malformed, reason);

        Assert.False(ToyCommandCodec.TryParse("{not json", out _, out reason));
        Assert.Equal(ToyReason.Malformed, reason);

        ToyCommand ok = rig.Cmd("a", Canvas, "stroke.begin", StrokePayload(rig.S, 5, 5));
        Assert.True(ToyCommandCodec.TryParse(ToyCommandCodec.Serialize(ok), out ToyCommand parsed, out _));
        rig.Expect(rig.S.Submit(parsed, rig.Now));
    }

    [Fact]
    public void Malformed_payload_numbers_are_rejected_without_side_effects()
    {
        var rig = new Rig("a");
        long before = rig.S.Revision;
        Assert.Equal(ToyReason.Malformed, rig.Do("a", CapClash, "shot.submit", Payload.Of("angle", "left", "power", 0.5, "launchX", 0)).Reason);
        Assert.Equal(ToyReason.OutOfRange, rig.Do("a", CapClash, "shot.submit", Payload.Of("angle", 0, "power", 7.0, "launchX", 0)).Reason);
        Assert.Equal(ToyReason.Malformed, rig.Do("a", CapClash, "shot.submit", Payload.Of("power", 0.5, "launchX", 0)).Reason);
        Assert.Equal(ToyReason.UnknownKind, rig.Do("a", CapClash, "shot.teleport", null).Reason);
        Assert.Equal(before, rig.S.Revision);
        Assert.Empty(rig.S.CapClash.State.Queue);
    }

    [Fact]
    public void One_active_control_at_a_time_and_idle_control_is_released()
    {
        var rig = new Rig("a");
        rig.Expect(rig.Do("a", PocketCircuit, "lane.take", Payload.Of("lane", 1)));
        Assert.Equal(PocketCircuit, rig.S.Seat("a").Controlled);
        rig.Expect(rig.Do("a", Greenlight, "attempt.start", Payload.Of("variant", "LightsOut")));
        Assert.Equal(Greenlight, rig.S.Seat("a").Controlled);
        // Switching toys parked the slot car (the other board stays available without consuming control).
        Assert.Equal(NightSignal.Core.Toys.PocketCircuit.SlotCarMode.Parked, rig.S.PocketCircuit.Car("a").Mode);

        rig.Tick(ToyLimits.ControlLeaseMs + 100);
        Assert.Null(rig.S.Seat("a").Controlled); // idle: control lease released (no heartbeat renews it)
        Assert.Null(rig.S.Greenlight.OpenAttempt("a")); // closing/idle is neutral, not a failure
    }

    [Fact]
    public void Overview_shows_who_is_playing_each_toy_and_whether_it_is_paused()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", Canvas, "stroke.begin", StrokePayload(rig.S, 1, 1)));
        rig.Expect(rig.Do("b", PocketCircuit, "lane.take", Payload.Of("lane", 2)));
        List<ToyOverview> ov = rig.S.Overview();
        Assert.Equal(new[] { "a" }, ov.Single(o => o.Activity == Canvas).Players);
        Assert.Equal(new[] { "b" }, ov.Single(o => o.Activity == PocketCircuit).Players);
        Assert.All(ov, o => Assert.False(o.Paused));
        rig.S.Pause(rig.Now);
        Assert.All(rig.S.Overview(), o => Assert.True(o.Paused));
    }

    [Fact]
    public void B09_all_disconnected_room_is_dormant_for_24h_and_heartbeats_do_not_renew_it()
    {
        var rig = new Rig("a", "b");
        rig.S.Disconnect("a", rig.Now);
        Assert.Equal(SessionLifecycle.Active, rig.S.Lifecycle);
        rig.Tick(1000);
        rig.S.Disconnect("b", rig.Now);
        Assert.Equal(SessionLifecycle.Dormant, rig.S.Lifecycle);
        long lost = rig.Now;
        Assert.Equal(lost + ToyLimits.DormantGraceMs, rig.S.DormantExpiresMs);

        // Advancing (the host's timers / background retries) never renews the expiry.
        rig.S.Advance(lost + ToyLimits.DormantGraceMs - 1);
        Assert.False(rig.S.IsDormantExpired(lost + ToyLimits.DormantGraceMs - 1));
        Assert.True(rig.S.IsDormantExpired(lost + ToyLimits.DormantGraceMs));
        Assert.Equal(lost + ToyLimits.DormantGraceMs, rig.S.DormantExpiresMs);

        // An explicit authorized rejoin inside the grace restores the room and starts a new active period.
        var rig2 = new Rig("a", "b");
        rig2.S.Disconnect("a", rig2.Now);
        rig2.S.Disconnect("b", rig2.Now);
        rig2.Tick(60_000);
        Assert.Equal(JoinOutcome.RestoredDormant, rig2.S.Join("a", 2, rig2.Now));
        Assert.Equal(SessionLifecycle.Active, rig2.S.Lifecycle);
        Assert.Equal(0, rig2.S.DormantExpiresMs);
        Assert.False(rig2.S.PausedForEvent); // no race is resumed by a toy room
    }

    [Fact]
    public void B09_explicit_disband_or_last_voluntary_leave_ends_the_session()
    {
        var rig = new Rig("a", "b");
        rig.S.Leave("a", DepartureReason.Left, rig.Now);
        Assert.Equal(SessionLifecycle.Active, rig.S.Lifecycle);
        rig.S.Leave("b", DepartureReason.Left, rig.Now);
        Assert.Equal(SessionLifecycle.Retired, rig.S.Lifecycle);
        Assert.Equal(JoinOutcome.RejectedEnded, rig.S.Join("c", 1, rig.Now));

        var rig2 = new Rig("a");
        rig2.S.Disband(rig2.Now);
        Assert.Equal(ToyReason.SessionEnded, rig2.Do("a", Canvas, "stroke.begin", StrokePayload(rig2.S, 1, 1)).Reason);
    }

    [Fact]
    public void Capacity_is_six_active_members()
    {
        var rig = new Rig("a", "b", "c", "d", "e", "f");
        Assert.Equal(JoinOutcome.RejectedFull, rig.S.Join("g", 1, rig.Now));
        rig.S.Disconnect("f", rig.Now);
        Assert.Equal(JoinOutcome.NewSeat, rig.S.Join("g", 1, rig.Now));
    }

    [Fact]
    public void Snapshot_json_roundtrips_exactly_and_is_versioned_and_bounded()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", Canvas, "stroke.begin", StrokePayload(rig.S, 1, 1, 50, 60)));
        rig.Expect(rig.Do("b", PitCrew, "task.claim", Payload.Of("op", "co-01")));
        rig.S.Pause(rig.Now);
        string json = DowntimeCodec.Serialize(rig.S.Snapshot());
        DowntimeSnapshot snap = DowntimeCodec.Deserialize(json);
        Assert.Equal(ToyLimits.SchemaVersion, snap.Schema);
        Assert.Equal(NonProgression.Domain, snap.Domain);
        DowntimeSession restored = DowntimeSession.Restore(snap, ToyData.Content, rig.Now + 5000);
        DowntimeSnapshot again = restored.Snapshot();
        again.ClockMs = snap.ClockMs; // the restored host clock moved on; nothing else may differ
        Assert.Equal(json, DowntimeCodec.Serialize(again));

        Assert.Throws<InvalidOperationException>(() => DowntimeCodec.Deserialize(json.Replace("\"Schema\":1", "\"Schema\":99")));
        Assert.Throws<InvalidOperationException>(() => DowntimeCodec.Deserialize(json.Replace(NonProgression.Domain, "official-results")));
    }

    [Fact]
    public void Journal_replay_after_a_snapshot_reproduces_discrete_state_exactly()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", Canvas, "stroke.begin", StrokePayload(rig.S, 1, 1)));
        DowntimeSnapshot baseSnap = DowntimeCodec.Deserialize(DowntimeCodec.Serialize(rig.S.Snapshot()));
        rig.S.MarkDurable(baseSnap.Revision);
        Assert.Empty(rig.S.Journal);

        rig.Tick(200);
        rig.Expect(rig.Do("b", PitCrew, "task.claim", Payload.Of("op", "co-01")));
        rig.Tick(300);
        rig.Expect(rig.Do("b", PitCrew, "task.step", Payload.Of("op", "co-01", "step", 0, "error", 1.0)));
        rig.Tick(300);
        rig.Expect(rig.Do("a", Canvas, "text.add", Payload.Of("sheet", "sheet-1", "sheetEpoch", 1, "text", "Night shift!", "color", 0xFFFFFFFFL, "size", 64, "points", new[] { 100, 100 })));
        rig.Expect(rig.Do("a", Greenlight, "series.create", Payload.Of("variant", "ShiftWindow", "count", 3)));
        Assert.Equal(4, rig.S.Journal.Count);

        string journalJson = DowntimeCodec.SerializeJournal(rig.S.Journal);
        DowntimeSession replayed = DowntimeSession.Restore(baseSnap, ToyData.Content, rig.Now, DowntimeCodec.DeserializeJournal(journalJson));
        Assert.Equal(rig.S.Revision, replayed.Revision);
        Assert.Equal(CanvasCodec.DrawingHash(rig.S.Canvas.Document), CanvasCodec.DrawingHash(replayed.Canvas.Document));
        Assert.Equal(1, replayed.PitCrew.State.Project.Ops["co-01"].StepsDone);
        Assert.Equal(rig.S.Greenlight.State.Series[0].Seeds, replayed.Greenlight.State.Series[0].Seeds);
        // Replaying the same journal again (a retried recovery) cannot double-apply anything.
        replayed.Replay(DowntimeCodec.DeserializeJournal(journalJson));
        Assert.Equal(rig.S.Revision, replayed.Revision);
    }
}

public sealed class DurabilityTests
{
    [Fact]
    public void Snapshot_is_due_periodically_only_while_something_changed()
    {
        var rig = new Rig("a");
        rig.S.MarkDurable(rig.S.Revision, rig.Now);
        Assert.False(rig.S.SnapshotDue(rig.Now + ToyLimits.SnapshotIntervalMs * 10)); // nothing changed
        rig.Expect(rig.Do("a", ToyActivityId.Canvas, "stroke.begin", Payload.Of("sheet", "sheet-1", "sheetEpoch", 1, "color", 0L, "width", 3, "points", new[] { 1, 1 })));
        Assert.False(rig.S.SnapshotDue(rig.Now + 1000));
        Assert.True(rig.S.SnapshotDue(rig.Now + ToyLimits.SnapshotIntervalMs));
        string json = rig.S.SnapshotJson();
        rig.S.MarkDurable(DowntimeCodec.Deserialize(json).Revision, rig.Now);
        Assert.False(rig.S.SnapshotDue(rig.Now + ToyLimits.SnapshotIntervalMs));
        Assert.Empty(rig.S.Journal);
        Assert.False(DowntimeSession.Restore(DowntimeCodec.Deserialize(json), ToyData.Content, rig.Now).RestoredAgainstDifferentContent);
    }
}
