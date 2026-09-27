using Newtonsoft.Json.Linq;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.Canvas;
using Xunit.Abstractions;
using static NightSignal.Core.Toys.ToyActivityId;

namespace NightSignal.Toys.Tests;

/// <summary>Convoy Canvas (Addendum 02 §6, acceptance C07/C08).</summary>
public sealed class CanvasTests
{
    readonly ITestOutputHelper output;
    public CanvasTests(ITestOutputHelper output) { this.output = output; }

    static CanvasSheet Sheet(Rig rig, int index = 0) => rig.S.Canvas.Document.Sheets[index];

    static JObject On(Rig rig, CanvasSheet s, params object[] rest)
    {
        JObject p = Payload.Of(rest);
        p["sheet"] = s.SheetId;
        p["sheetEpoch"] = s.Epoch;
        return p;
    }

    static string Stroke(Rig rig, string m, params int[] pts)
    {
        ToyResult r = rig.Do(m, Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0xFF3355FFL, "width", 5, "points", pts));
        rig.Expect(r);
        rig.Expect(rig.Do(m, Canvas, "stroke.end", On(rig, Sheet(rig), "object", r.Value)));
        return r.Value;
    }

    static CanvasObject Obj(Rig rig, string id) => rig.S.Canvas.Document.Sheets.SelectMany(s => s.Objects).Single(o => o.Id == id);

    [Fact]
    public void Two_members_draw_freehand_at_the_same_time()
    {
        var rig = new Rig("a", "b");
        string sa = rig.Do("a", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0xFFFF0000L, "width", 4, "points", new[] { 10, 10, 12, 14 })).Value;
        string sb = rig.Do("b", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0xFF00FF00L, "width", 8, "points", new[] { 900, 900 })).Value;
        rig.Expect(rig.Do("b", Canvas, "stroke.append", On(rig, Sheet(rig), "object", sb, "points", new[] { 905, 910, 911, 920 })));
        rig.Expect(rig.Do("a", Canvas, "stroke.append", On(rig, Sheet(rig), "object", sa, "points", new[] { 15, 20 })));
        Assert.Equal(ToyReason.NotOwner, rig.Do("a", Canvas, "stroke.append", On(rig, Sheet(rig), "object", sb, "points", new[] { 1, 1 })).Reason);
        rig.Expect(rig.Do("a", Canvas, "stroke.end", On(rig, Sheet(rig), "object", sa)));
        rig.Expect(rig.Do("b", Canvas, "stroke.end", On(rig, Sheet(rig), "object", sb)));
        Assert.Equal(new[] { 10, 10, 12, 14, 15, 20 }, Obj(rig, sa).Points);
        Assert.Equal(new[] { 900, 900, 905, 910, 911, 920 }, Obj(rig, sb).Points);
        Assert.Equal("a", Obj(rig, sa).Author);
        Assert.Equal("b", Obj(rig, sb).Author);
        Assert.NotEqual(sa, sb);
    }

    [Fact]
    public void Own_undo_redo_never_rewinds_a_friends_later_action()
    {
        var rig = new Rig("a", "b");
        string a1 = Stroke(rig, "a", 100, 100, 200, 200);
        string b1 = Stroke(rig, "b", 300, 300, 400, 400);
        rig.Expect(rig.Do("a", Canvas, "undo"));
        Assert.True(Obj(rig, a1).Deleted);
        Assert.False(Obj(rig, b1).Deleted); // b's later stroke untouched
        rig.Expect(rig.Do("a", Canvas, "redo"));
        Assert.False(Obj(rig, a1).Deleted);

        // a shares a mark; b (with the lease) moves it later. a's undo of the share cannot rewind b's move.
        rig.Expect(rig.Do("a", Canvas, "object.share", On(rig, Sheet(rig), "object", a1, "shared", true)));
        rig.Expect(rig.Do("b", Canvas, "object.lease", On(rig, Sheet(rig), "object", a1)));
        rig.Expect(rig.Do("b", Canvas, "object.transform", On(rig, Sheet(rig), "object", a1, "tx", 50, "ty", 0, "rot", 0, "scale", 1000)));
        Assert.Equal(ToyReason.Conflict, rig.Do("a", Canvas, "undo").Reason);
        Assert.Equal(50, Obj(rig, a1).Tx);
        // b's own undo restores b's move.
        rig.Expect(rig.Do("b", Canvas, "undo"));
        Assert.Equal(0, Obj(rig, a1).Tx);
    }

    [Fact]
    public void At_least_fifty_own_undo_steps()
    {
        var rig = new Rig("a");
        var ids = new List<string>();
        for (int i = 0; i < 70; i++)
            ids.Add(rig.Do("a", Canvas, "shape.add", On(rig, Sheet(rig), "shape", "Rect", "color", 0xFF000000L, "width", 2, "points", new[] { i, i, i + 10, i + 10 })).Value);
        rig.Tick(2000);
        int undone = 0;
        while (rig.Do("a", Canvas, "undo").Accepted) { undone++; if (undone % 30 == 0) rig.Tick(1000); }
        Assert.True(undone >= 50, "undone " + undone);
        Assert.Equal(CanvasLimits.UndoDepth, undone);
        Assert.Equal(70 - CanvasLimits.UndoDepth, ids.Count(id => !Obj(rig, id).Deleted));
    }

    [Fact]
    public void Erasing_a_friends_mark_needs_their_share_flag_and_a_short_lease()
    {
        var rig = new Rig("a", "b", "c");
        string a1 = Stroke(rig, "a", 10, 10, 20, 20);
        Assert.Equal(ToyReason.NotOwner, rig.Do("b", Canvas, "object.erase", On(rig, Sheet(rig), "object", a1)).Reason);
        rig.Expect(rig.Do("a", Canvas, "object.share", On(rig, Sheet(rig), "object", a1, "shared", true)));
        Assert.Equal(ToyReason.NoLease, rig.Do("b", Canvas, "object.erase", On(rig, Sheet(rig), "object", a1)).Reason);
        rig.Expect(rig.Do("b", Canvas, "object.lease", On(rig, Sheet(rig), "object", a1)));
        Assert.Equal(ToyReason.LeaseHeld, rig.Do("c", Canvas, "object.lease", On(rig, Sheet(rig), "object", a1)).Reason); // no fighting over it
        rig.Tick(CanvasLimits.TransformLeaseMs + 100);
        Assert.Equal(ToyReason.NoLease, rig.Do("b", Canvas, "object.erase", On(rig, Sheet(rig), "object", a1)).Reason); // lease expired
        rig.Expect(rig.Do("c", Canvas, "object.lease", On(rig, Sheet(rig), "object", a1)));
        rig.Expect(rig.Do("c", Canvas, "object.erase", On(rig, Sheet(rig), "object", a1)));
        Assert.True(Obj(rig, a1).Deleted);
        rig.Expect(rig.Do("a", Canvas, "object.erase", On(rig, Sheet(rig), "object", Stroke(rig, "a", 1, 1)))); // own erase is the default
    }

    [Fact]
    public void Text_is_bounded_plain_data_and_escaped_for_display()
    {
        var rig = new Rig("a");
        string markup = "<color=red>SHIFT</color> & <b>\"late\"</b>";
        string id = rig.Do("a", Canvas, "text.add", On(rig, Sheet(rig), "text", markup, "color", 0xFFFFFFFFL, "size", 48, "points", new[] { 50, 50 })).Value;
        Assert.Equal(markup, Obj(rig, id).Text); // stored verbatim as data
        Assert.Equal("<noparse>" + markup + "</noparse>", CanvasText.ForRichText(markup));
        Assert.Equal("<noparse>x</​noparse><b>y</noparse>", CanvasText.ForRichText("x</noparse><b>y"));
        Assert.DoesNotContain("<", CanvasText.ForHtml(markup));
        Assert.Equal(ToyReason.TooLarge, rig.Do("a", Canvas, "text.add", On(rig, Sheet(rig), "text", new string('w', 81), "color", 0L, "size", 48, "points", new[] { 5, 5 })).Reason);
        Assert.Equal(ToyReason.Malformed, rig.Do("a", Canvas, "text.add", On(rig, Sheet(rig), "text", "bell\u0007", "color", 0L, "size", 48, "points", new[] { 5, 5 })).Reason);
        Assert.Equal(ToyReason.Malformed, rig.Do("a", Canvas, "text.add", On(rig, Sheet(rig), "text", "rtl‮spoof", "color", 0L, "size", 48, "points", new[] { 5, 5 })).Reason);
        Assert.Equal(ToyReason.Malformed, rig.Do("a", Canvas, "text.add", On(rig, Sheet(rig), "text", "half\uD800", "color", 0L, "size", 48, "points", new[] { 5, 5 })).Reason);
        Assert.Equal(ToyReason.Malformed, rig.Do("a", Canvas, "text.add", On(rig, Sheet(rig), "text", "", "color", 0L, "size", 48, "points", new[] { 5, 5 })).Reason);
        rig.Expect(rig.Do("a", Canvas, "text.add", On(rig, Sheet(rig), "text", "Night shift 🏁", "color", 0L, "size", 48, "points", new[] { 5, 5 })));
    }

    [Fact]
    public void Oversized_and_malformed_drawing_payloads_fail_safely()
    {
        var rig = new Rig("a");
        int[] tooMany = Enumerable.Range(0, (CanvasLimits.MaxPointsPerOp + 1) * 2).Select(i => i % 100).ToArray();
        Assert.Equal(ToyReason.TooLarge, rig.Do("a", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0L, "width", 3, "points", tooMany)).Reason);
        Assert.Equal(ToyReason.OutOfRange, rig.Do("a", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0L, "width", 3, "points", new[] { 5000, 5 })).Reason);
        Assert.Equal(ToyReason.OutOfRange, rig.Do("a", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0L, "width", 3, "points", new[] { 5, 2048 })).Reason);
        Assert.Equal(ToyReason.Malformed, rig.Do("a", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0L, "width", 3, "points", new[] { 5 })).Reason);
        Assert.Equal(ToyReason.Malformed, rig.Do("a", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0L, "width", 3, "points", new JArray(1.5, 2.5))).Reason);
        Assert.Equal(ToyReason.OutOfRange, rig.Do("a", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0L, "width", 999, "points", new[] { 5, 5 })).Reason);
        Assert.Equal(ToyReason.NotFound, rig.Do("a", Canvas, "stamp.add", On(rig, Sheet(rig), "stamp", "brand-logo", "color", 0L, "points", new[] { 5, 5 })).Reason);
        Assert.Empty(Sheet(rig).Objects);

        // A long stroke is bounded: after 1024 points the client must start another stroke.
        string id = rig.Do("a", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0L, "width", 3, "points", Enumerable.Range(0, 256).ToArray())).Value;
        ToyResult last = default;
        for (int i = 0; i < 10; i++)
        {
            rig.Tick(100);
            last = rig.Do("a", Canvas, "stroke.append", On(rig, Sheet(rig), "object", id, "points", Enumerable.Range(0, 256).ToArray()));
            if (!last.Accepted) break;
        }
        Assert.Equal(ToyReason.TooLarge, last.Reason);
        Assert.Equal(CanvasLimits.MaxPointsPerStroke, Obj(rig, id).PointCount);
    }

    [Fact]
    public void Document_budget_is_enforced_and_the_full_document_stays_under_one_mebibyte_compressed()
    {
        var rig = new Rig("a", "b", "c", "d", "e", "f");
        CanvasDocument doc = rig.S.Canvas.Document;
        var rnd = new Random(7);
        // Fill the document directly (the protocol rate limit would make this slow); the budget checks are the same code.
        var sheet = doc.Sheets[0];
        for (int i = 0; i < CanvasLimits.MaxObjects - 1; i++)
        {
            int n = CanvasLimits.MaxTotalPoints / CanvasLimits.MaxObjects;
            var pts = new int[n * 2];
            int x = rnd.Next(0, 4000), y = rnd.Next(0, 2000);
            for (int k = 0; k < n; k++) { x = Math.Clamp(x + rnd.Next(-9, 10), 0, 4095); y = Math.Clamp(y + rnd.Next(-9, 10), 0, 2047); pts[2 * k] = x; pts[2 * k + 1] = y; }
            sheet.Objects.Add(new CanvasObject { Id = "o-f" + i, Author = "abcdef"[i % 6].ToString(), Kind = CanvasObjectKind.Stroke, Color = 0xFF000000L | (uint)rnd.Next(), Width = rnd.Next(1, 20), Points = pts });
        }
        rig.Expect(rig.Do("a", Canvas, "shape.add", On(rig, sheet, "shape", "Line", "color", 0L, "width", 2, "points", new[] { 1, 1, 2, 2 })));
        Assert.Equal(ToyReason.LimitReached, rig.Do("a", Canvas, "shape.add", On(rig, sheet, "shape", "Line", "color", 0L, "width", 2, "points", new[] { 1, 1, 2, 2 })).Reason);
        byte[] z = CanvasCodec.Compress(doc);
        output.WriteLine($"full-budget document: {rig.S.Canvas.LiveObjectCount} objects, {rig.S.Canvas.TotalPoints} points, {z.Length} bytes compressed");
        Assert.True(z.Length <= CanvasLimits.MaxCompressedBytes);
        string snapshot = DowntimeCodec.Serialize(rig.S.Snapshot());
        output.WriteLine($"session snapshot with the full canvas: {snapshot.Length} bytes");
        Assert.True(snapshot.Length <= ToyLimits.MaxSnapshotBytes);
    }

    [Fact]
    public void Decompression_is_bounded_against_malicious_payloads()
    {
        byte[] bomb = DowntimeCodec.Deflate(new byte[20 * 1024 * 1024]); // 20 MiB of zeros compresses to ~20 KB
        Assert.Throws<InvalidDataException>(() => DowntimeCodec.Inflate(bomb, CanvasLimits.MaxInflatedBytes));
        Assert.ThrowsAny<Exception>(() => CanvasCodec.Decode(Convert.ToBase64String(bomb)));
        Assert.ThrowsAny<Exception>(() => CanvasCodec.Decode(Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 })));
    }

    [Fact]
    public void Whole_sheet_clear_needs_active_contributors_consent_and_keeps_a_recoverable_checkpoint()
    {
        var rig = new Rig("a", "b");
        Stroke(rig, "a", 1, 1, 50, 50);
        Stroke(rig, "b", 60, 60, 90, 90);
        string before = CanvasCodec.DrawingHash(rig.S.Canvas.Document);
        CanvasSheet s = Sheet(rig);

        // Silence is not consent: the proposal lapses and nothing is cleared.
        rig.Expect(rig.Do("a", Canvas, "clear.propose", On(rig, s)));
        rig.Tick(ToyLimits.ProposalLifetimeMs + 100);
        Assert.Equal(ProposalState.Lapsed, rig.S.Canvas.Document.LastProposal.State);
        Assert.Equal(before, CanvasCodec.DrawingHash(rig.S.Canvas.Document));

        Stroke(rig, "b", 5, 5); // b is still actively drawing
        string pid = rig.Do("a", Canvas, "clear.propose", On(rig, s)).Value;
        int oldEpoch = s.Epoch;
        int objectsBefore = s.Objects.Count(o => !o.Deleted);
        rig.Expect(rig.Do("b", Canvas, "proposal.vote", Payload.Of("proposal", pid, "accept", true)));
        Assert.Empty(s.Objects);
        Assert.Equal(oldEpoch + 1, s.Epoch);
        CanvasCheckpoint cp = rig.S.Canvas.Document.Checkpoints.Single();
        Assert.Equal("pre-clear", cp.Reason);
        Assert.Equal(objectsBefore, cp.Objects.Count);

        // A late packet addressed to the pre-clear epoch cannot paint on the cleared sheet, and undo cannot resurrect.
        JObject late = Payload.Of("sheet", s.SheetId, "sheetEpoch", oldEpoch, "color", 0L, "width", 3, "points", new[] { 9, 9 });
        Assert.Equal(ToyReason.StaleEpoch, rig.Do("a", Canvas, "stroke.begin", late).Reason);
        Assert.False(rig.Do("b", Canvas, "undo").Accepted);
        Assert.Empty(s.Objects);

        // Recover the checkpoint as a new sheet (nothing drawn since is overwritten).
        string restored = rig.Do("a", Canvas, "checkpoint.restore", Payload.Of("checkpoint", cp.CheckpointId)).Value;
        CanvasSheet rs = rig.S.Canvas.Document.Sheet(restored);
        Assert.Equal(cp.Objects.Select(o => string.Join(",", o.Points)), rs.Objects.Select(o => string.Join(",", o.Points)));
        Assert.Equal(cp.Objects.Select(o => o.Author), rs.Objects.Select(o => o.Author));
    }

    [Fact]
    public void Sheets_are_retained_and_ops_for_another_sheet_never_land_elsewhere()
    {
        var rig = new Rig("a");
        for (int i = 1; i < CanvasLimits.MaxSheets; i++) rig.Expect(rig.Do("a", Canvas, "sheet.new"));
        Assert.Equal(CanvasLimits.MaxSheets, rig.S.Canvas.Document.Sheets.Count);
        Assert.True(CanvasLimits.MaxSheets >= CanvasLimits.MinRetainedSheets);
        Assert.Equal(ToyReason.LimitReached, rig.Do("a", Canvas, "sheet.new").Reason);
        CanvasSheet s2 = Sheet(rig, 1);
        string id = rig.Do("a", Canvas, "stroke.begin", On(rig, s2, "color", 0L, "width", 3, "points", new[] { 7, 7 })).Value;
        Assert.Contains(s2.Objects, o => o.Id == id);
        Assert.DoesNotContain(Sheet(rig, 0).Objects, o => o.Id == id);
        Assert.Equal(ToyReason.NotFound, rig.Do("a", Canvas, "stroke.append", On(rig, Sheet(rig, 0), "object", id, "points", new[] { 8, 8 })).Reason);
        Assert.Equal(ToyReason.NotFound, rig.Do("a", Canvas, "stroke.begin", Payload.Of("sheet", "sheet-99", "sheetEpoch", 1, "color", 0L, "width", 3, "points", new[] { 7, 7 })).Reason);
    }

    [Fact]
    public void Pause_finalizes_a_stroke_in_progress_at_its_last_accepted_point()
    {
        var rig = new Rig("a");
        string id = rig.Do("a", Canvas, "stroke.begin", On(rig, Sheet(rig), "color", 0L, "width", 3, "points", new[] { 1, 1, 2, 2 })).Value;
        rig.Expect(rig.Do("a", Canvas, "stroke.append", On(rig, Sheet(rig), "object", id, "points", new[] { 3, 3 })));
        int epoch = rig.S.Canvas.Run.Epoch;
        rig.S.Pause(rig.Now);
        Assert.False(Obj(rig, id).Open);
        Assert.Equal(new[] { 1, 1, 2, 2, 3, 3 }, Obj(rig, id).Points);
        // A delayed append from before the boundary: deferred while paused, rejected afterwards (old epoch).
        Assert.Equal(ToyVerdict.Deferred, rig.Do("a", Canvas, "stroke.append", On(rig, Sheet(rig), "object", id, "points", new[] { 4, 4 }), epoch: epoch).Verdict);
        rig.S.EndPause(rig.Now);
        Assert.Equal(ToyReason.StaleEpoch, rig.Do("a", Canvas, "stroke.append", On(rig, Sheet(rig), "object", id, "points", new[] { 4, 4 }), epoch: epoch).Reason);
        Assert.Equal(ToyReason.InvalidState, rig.Do("a", Canvas, "stroke.append", On(rig, Sheet(rig), "object", id, "points", new[] { 4, 4 })).Reason);
        Assert.Equal(new[] { 1, 1, 2, 2, 3, 3 }, Obj(rig, id).Points);
    }

    [Fact]
    public void Flat_and_hood_views_render_the_same_committed_document_after_rejoin()
    {
        var rig = new Rig("a", "b");
        Stroke(rig, "a", 0, 0, 4095, 2047, 2048, 1024);
        rig.Expect(rig.Do("b", Canvas, "stamp.add", On(rig, Sheet(rig), "stamp", "star", "color", 0xFFFFCC00L, "points", new[] { 1000, 500 })));
        string hash = CanvasCodec.DrawingHash(rig.S.Canvas.Document);
        rig.S.Disconnect("b", rig.Now);
        DowntimeSession rejoined = DowntimeSession.Restore(DowntimeCodec.Deserialize(DowntimeCodec.Serialize(rig.S.Snapshot())), ToyData.Content, rig.Now + 1000);
        Assert.Equal(JoinOutcome.RestoredDormant, rejoined.Join("b", 2, rig.Now + 1000));
        Assert.Equal(hash, CanvasCodec.DrawingHash(rejoined.Canvas.Document));

        var hood = new HoodProjection();
        // Stable, invertible UV mapping inside the margin; corners of the sheet map inside [0,1].
        foreach (var (x, y) in new[] { (0.0, 0.0), (4096.0, 2048.0), (2048.0, 1024.0), (123.0, 1777.0) })
        {
            Vec2 uv = hood.SheetToUv(x, y);
            Assert.InRange(uv.X, 0, 1);
            Assert.InRange(uv.Y, 0, 1);
            Vec2 back = hood.UvToSheet(uv);
            Assert.Equal(x, back.X, 6);
            Assert.Equal(y, back.Y, 6);
        }
        // Arc-length parameterization: equal UV steps are equal distances on the crowned surface.
        double d1 = (hood.UvToSurface(new Vec2(0.1, 0.5)) - hood.UvToSurface(new Vec2(0.2, 0.5))).Length();
        double d2 = (hood.UvToSurface(new Vec2(0.45, 0.5)) - hood.UvToSurface(new Vec2(0.55, 0.5))).Length();
        Assert.Equal(d1, d2, 3);
        Vec3 centre = hood.SheetToSurface(2048, 1024);
        Assert.True(centre.Z > 0);
        hood.BuildGrid(32, 16, out Vec3[] verts, out Vec2[] uvs, out int[] tris);
        Assert.Equal(33 * 17, verts.Length);
        Assert.Equal(32 * 16 * 6, tris.Length);
        Assert.Equal(verts.Length, uvs.Length);
    }
}
