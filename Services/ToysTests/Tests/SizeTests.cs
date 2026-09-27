using NightSignal.Core.Toys;
using NightSignal.Core.Toys.Canvas;
using NightSignal.Core.Toys.PitCrew;
using Xunit.Abstractions;
using static NightSignal.Core.Toys.ToyActivityId;

namespace NightSignal.Toys.Tests;

/// <summary>Measured wire/snapshot sizes (reported, and bounded by the protocol limits).</summary>
public sealed class SizeTests
{
    readonly ITestOutputHelper output;
    public SizeTests(ITestOutputHelper output) { this.output = output; }

    [Fact]
    public void Measure_snapshot_and_command_sizes()
    {
        var fresh = new Rig("a", "b", "c", "d", "e", "f");
        string freshJson = DowntimeCodec.Serialize(fresh.S.Snapshot());
        output.WriteLine($"fresh six-member session snapshot: {freshJson.Length} bytes");

        PreemptionTests.Busy busy = PreemptionTests.MakeBusyConvoy();
        busy.Rig.S.Pause(busy.Rig.Now);
        string busyJson = DowntimeCodec.Serialize(busy.Rig.S.Snapshot());
        output.WriteLine($"busy convoy pause snapshot (B03 situation): {busyJson.Length} bytes");

        // An evening's worth: a finished model on the shelf, a new project half done, 300 strokes, many laps and attempts.
        Rig rig = busy.Rig;
        rig.S.EndPause(rig.Now);
        foreach (OperationDef op in rig.S.PitCrew.Blueprint.TopologicalOrder())
        {
            if (rig.S.PitCrew.IsDone(op.Id)) continue;
            rig.Expect(rig.Do("crew", PitCrew, "task.claim", Payload.Of("op", op.Id)));
            OperationProgress p = rig.S.PitCrew.State.Project.Ops[op.Id];
            for (int st = p.StepsDone; st < op.Steps; st++) { rig.Tick(300); rig.Expect(rig.Do("crew", PitCrew, "task.step", Payload.Of("op", op.Id, "step", st, "error", 0.0))); }
        }
        CanvasSheet sheet = rig.S.Canvas.Document.Sheets[0];
        var rnd = new Random(3);
        for (int i = 0; i < 300; i++)
        {
            int[] pts = Enumerable.Range(0, 64).SelectMany(k => new[] { rnd.Next(0, 4096), rnd.Next(0, 2048) }).ToArray();
            rig.Expect(rig.Do("art", Canvas, "stroke.begin", Payload.Of("sheet", sheet.SheetId, "sheetEpoch", sheet.Epoch, "color", 0xFF112233L, "width", 4, "points", pts)));
            if (i % 40 == 39) rig.Tick(1000);
        }
        rig.Expect(rig.Do("car", PocketCircuit, "resume"));
        PocketCircuitTests.Drive(rig, new[] { "car" }, 120_000);
        string eveningJson = DowntimeCodec.Serialize(rig.S.Snapshot());
        output.WriteLine($"evening snapshot (shelf model, 300 x 64-point strokes, ~10 laps): {eveningJson.Length} bytes; canvas compressed {CanvasCodec.Compress(rig.S.Canvas.Document).Length} bytes");
        output.WriteLine($"journal since start: {rig.S.Journal.Count} durable entries, {DowntimeCodec.SerializeJournal(rig.S.Journal).Length} bytes");

        ToyCommand throttle = rig.Cmd("car", PocketCircuit, "throttle", Payload.Of("value", 0.62));
        ToyCommand append = rig.Cmd("art", Canvas, "stroke.append", Payload.Of("sheet", "sheet-1", "sheetEpoch", 1, "object", "o-123", "points", Enumerable.Range(0, 256).Select(k => 4000 - k).ToArray()));
        output.WriteLine($"throttle command: {ToyCommandCodec.Serialize(throttle).Length} bytes; 128-point stroke.append: {ToyCommandCodec.Serialize(append).Length} bytes (limit {ToyLimits.MaxCommandBytes})");
        Assert.True(ToyCommandCodec.Serialize(append).Length < ToyLimits.MaxCommandBytes);
        Assert.True(eveningJson.Length < ToyLimits.MaxSnapshotBytes);
    }
}
