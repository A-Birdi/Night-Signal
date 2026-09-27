using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace NightSignal.Core.Toys.Canvas
{
    /// <summary>An original stamp: normalized polylines in [-1, 1] (ids only are stored in documents).</summary>
    public sealed class StampDef
    {
        public string Id;
        public string Name;
        public List<double[]> Paths = new List<double[]>();
    }

    public sealed class CanvasStampLibrary
    {
        public int Schema;
        public List<StampDef> Stamps = new List<StampDef>();

        public bool Has(string id) => Stamps.Any(s => s.Id == id);

        public static CanvasStampLibrary Parse(string json)
        {
            CanvasStampLibrary lib = JsonConvert.DeserializeObject<CanvasStampLibrary>(json);
            if (lib == null || lib.Stamps.Count < 6) throw new FormatException("the stamp library needs at least six original stamps");
            var ids = new HashSet<string>();
            foreach (StampDef s in lib.Stamps)
            {
                if (!ToyCommandCodec.ValidId(s.Id) || !ids.Add(s.Id)) throw new FormatException("bad or duplicate stamp id " + s.Id);
                if (s.Paths.Count == 0) throw new FormatException("stamp " + s.Id + " has no outline");
                foreach (double[] p in s.Paths)
                    if (p.Length < 4 || p.Length % 2 != 0 || p.Any(v => !(v >= -1 && v <= 1))) throw new FormatException("stamp " + s.Id + " outline out of range");
            }
            return lib;
        }
    }

    /// <summary>
    /// Convoy Canvas (Addendum 02 §6): shared freehand drawing with stable object ids and authorship, authoritative
    /// operation order, per-member own-action undo/redo that never rewinds others' later actions, own-object erase by
    /// default, friends' objects only when shared and under a short transform lease, consented whole-sheet clear with a
    /// recoverable pre-clear checkpoint, and hard budgets that reject malformed/oversized payloads.
    /// </summary>
    public sealed class ConvoyCanvasBoard : IToyActivity
    {
        readonly IToyHost host;
        readonly CanvasStampLibrary stamps;
        public CanvasDocument Document { get; private set; }

        public ConvoyCanvasBoard(IToyHost host, CanvasStampLibrary stamps, CanvasDocument doc)
        {
            this.host = host;
            this.stamps = stamps;
            Document = doc ?? new CanvasDocument();
            if (Document.Sheets.Count == 0) AddSheet("Sheet 1");
        }

        public ToyActivityId Id => ToyActivityId.Canvas;
        public ToyRunState Run => Document.Run;
        public bool RequiresExplicitResume => false;
        public bool NeedsSimulation => false;
        public object StateObject => Document;
        public bool IsTransient(string kind) => false;
        public bool TakesControl(string kind) => kind != "proposal.vote" && kind != "clear.propose" && kind != "sheet.new" && kind != "checkpoint.restore";

        CanvasSheet AddSheet(string name)
        {
            var s = new CanvasSheet { SheetId = "sheet-" + (++Document.SheetCounter), Name = name };
            Document.Sheets.Add(s);
            return s;
        }

        public int LiveObjectCount => Document.Sheets.Sum(s => s.Objects.Count(o => !o.Deleted));
        public int TotalPoints => Document.Sheets.Sum(s => s.Objects.Sum(o => o.PointCount));

        // --------------------------------------------------------------------------------------------------------------

        public ToyResult Apply(ToyCommandContext ctx)
        {
            switch (ctx.Kind)
            {
                case "stroke.begin": return StrokeBegin(ctx);
                case "stroke.append": return StrokeAppend(ctx);
                case "stroke.end": return StrokeEnd(ctx);
                case "shape.add": return ShapeAdd(ctx);
                case "text.add": return TextAdd(ctx);
                case "stamp.add": return StampAdd(ctx);
                case "object.transform": return TransformObject(ctx);
                case "object.style": return StyleObject(ctx);
                case "object.share": return ShareObject(ctx);
                case "object.lease": return LeaseObject(ctx);
                case "object.erase": return EraseObject(ctx);
                case "undo": return UndoRedo(ctx.Member, true);
                case "redo": return UndoRedo(ctx.Member, false);
                case "sheet.new": return NewSheet(ctx);
                case "clear.propose": return ProposeClear(ctx);
                case "proposal.vote": return Vote(ctx);
                case "checkpoint.restore": return RestoreCheckpoint(ctx);
                default: return ToyResult.Reject(ToyReason.UnknownKind);
            }
        }

        /// <summary>Every drawing op names its sheet AND sheet epoch; a late packet can never paint onto another sheet or epoch.</summary>
        CanvasSheet TargetSheet(ToyCommandContext ctx)
        {
            CanvasSheet s = Document.Sheet(ctx.P.Id("sheet"));
            if (s == null) throw new ToyPayloadException(ToyReason.NotFound, "unknown sheet");
            if (ctx.P.Int("sheetEpoch", 1, int.MaxValue) != s.Epoch) throw new ToyPayloadException(ToyReason.StaleEpoch, "sheet was cleared or replaced");
            return s;
        }

        CanvasObject TargetObject(ToyCommandContext ctx, CanvasSheet s)
        {
            string id = ctx.P.Id("object");
            CanvasObject o = s.Objects.FirstOrDefault(x => x.Id == id);
            if (o == null || o.Deleted) throw new ToyPayloadException(ToyReason.NotFound, "unknown or erased object");
            return o;
        }

        int[] Points(ToyCommandContext ctx, int minPoints, int maxPoints)
        {
            int[] pts = ctx.P.IntArray("points", maxPoints * 2, 0, CanvasLimits.SheetWidth - 1);
            if (pts.Length % 2 != 0 || pts.Length < minPoints * 2) throw new ToyPayloadException(ToyReason.Malformed, "points must be x,y pairs");
            for (int i = 1; i < pts.Length; i += 2)
                if (pts[i] >= CanvasLimits.SheetHeight) throw new ToyPayloadException(ToyReason.OutOfRange, "point outside the sheet");
            return pts;
        }

        long Color(ToyCommandContext ctx) => ctx.P.Long("color", 0, 0xFFFFFFFFL);
        int Width(ToyCommandContext ctx) => ctx.P.Int("width", CanvasLimits.MinWidth, CanvasLimits.MaxWidth);

        ToyResult CheckBudget(int newObjects, int newPoints)
        {
            if (LiveObjectCount + newObjects > CanvasLimits.MaxObjects)
                return ToyResult.Reject(ToyReason.LimitReached, "this document is full — start a new sheet or tidy your own marks");
            if (TotalPoints + newPoints > CanvasLimits.MaxTotalPoints)
                return ToyResult.Reject(ToyReason.LimitReached, "this document has reached its ink budget");
            return ToyResult.Ok();
        }

        CanvasObject Create(ToyCommandContext ctx, CanvasSheet s, CanvasObjectKind kind)
        {
            Document.Revision++;
            var o = new CanvasObject { Id = "o-" + (++Document.ObjectCounter), Author = ctx.Member, Kind = kind, Revision = Document.Revision };
            s.Objects.Add(o);
            PushUndo(ctx.Member, new CanvasUndoEntry { Kind = CanvasUndoKind.Presence, ObjectId = o.Id, SheetId = s.SheetId, SheetEpoch = s.Epoch, Deleted = true, RevisionAfter = o.Revision });
            return o;
        }

        ToyResult StrokeBegin(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            long color = Color(ctx);
            int width = Width(ctx);
            int[] pts = Points(ctx, 1, CanvasLimits.MaxPointsPerOp);
            ToyResult budget = CheckBudget(1, pts.Length / 2);
            if (!budget.Accepted) return budget;
            FinalizeOpenStrokes(ctx.Member); // one stroke in progress per person
            CanvasObject o = Create(ctx, s, CanvasObjectKind.Stroke);
            o.Color = color; o.Width = width; o.Points = pts; o.Open = true;
            return ToyResult.Ok(o.Id);
        }

        ToyResult StrokeAppend(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            CanvasObject o = TargetObject(ctx, s);
            int[] pts = Points(ctx, 1, CanvasLimits.MaxPointsPerOp);
            if (o.Author != ctx.Member) return ToyResult.Reject(ToyReason.NotOwner);
            if (!o.Open) return ToyResult.Reject(ToyReason.InvalidState, "stroke already finalized");
            if (o.PointCount + pts.Length / 2 > CanvasLimits.MaxPointsPerStroke) return ToyResult.Reject(ToyReason.TooLarge, "stroke too long — end it and start another");
            ToyResult budget = CheckBudget(0, pts.Length / 2);
            if (!budget.Accepted) return budget;
            var merged = new int[o.Points.Length + pts.Length];
            Array.Copy(o.Points, merged, o.Points.Length);
            Array.Copy(pts, 0, merged, o.Points.Length, pts.Length);
            o.Points = merged;
            Touch(o, ctx.Member, true);
            return ToyResult.Ok(o.Id);
        }

        ToyResult StrokeEnd(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            CanvasObject o = TargetObject(ctx, s);
            if (o.Author != ctx.Member) return ToyResult.Reject(ToyReason.NotOwner);
            if (!o.Open) return ToyResult.Reject(ToyReason.AlreadyDone);
            o.Open = false;
            Touch(o, ctx.Member, true);
            return ToyResult.Ok(o.Id);
        }

        ToyResult ShapeAdd(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            CanvasObjectKind kind = ctx.P.Enum<CanvasObjectKind>("shape");
            if (kind != CanvasObjectKind.Line && kind != CanvasObjectKind.Rect && kind != CanvasObjectKind.Ellipse) return ToyResult.Reject(ToyReason.Malformed, "shape must be Line, Rect or Ellipse");
            long color = Color(ctx);
            int width = Width(ctx);
            int[] pts = Points(ctx, 2, 2);
            ToyResult budget = CheckBudget(1, 2);
            if (!budget.Accepted) return budget;
            CanvasObject o = Create(ctx, s, kind);
            o.Color = color; o.Width = width; o.Points = pts;
            return ToyResult.Ok(o.Id);
        }

        ToyResult TextAdd(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            string text = ctx.P.String("text", CanvasLimits.MaxTextLength);
            if (!CanvasText.IsValid(text)) return ToyResult.Reject(ToyReason.Malformed, "text must be 1-80 printable characters");
            long color = Color(ctx);
            int size = ctx.P.Int("size", CanvasLimits.MinTextSize, CanvasLimits.MaxTextSize);
            int[] pts = Points(ctx, 1, 1);
            ToyResult budget = CheckBudget(1, 1);
            if (!budget.Accepted) return budget;
            CanvasObject o = Create(ctx, s, CanvasObjectKind.Text);
            o.Color = color; o.Size = size; o.Text = text; o.Points = pts;
            return ToyResult.Ok(o.Id);
        }

        ToyResult StampAdd(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            string stamp = ctx.P.Id("stamp");
            if (!stamps.Has(stamp)) return ToyResult.Reject(ToyReason.NotFound, "unknown stamp");
            long color = Color(ctx);
            int size = ctx.P.Int("size", 32, 1024, 160);
            int[] pts = Points(ctx, 1, 1);
            ToyResult budget = CheckBudget(1, 1);
            if (!budget.Accepted) return budget;
            CanvasObject o = Create(ctx, s, CanvasObjectKind.Stamp);
            o.Color = color; o.Size = size; o.StampId = stamp; o.Points = pts;
            return ToyResult.Ok(o.Id);
        }

        /// <summary>Own objects always; a friend's object only when they marked it shared AND the caller holds its lease.</summary>
        ToyResult CanEdit(CanvasObject o, string member, long nowMs)
        {
            if (o.Author == member) return ToyResult.Ok();
            if (!o.Shared) return ToyResult.Reject(ToyReason.NotOwner, "only the author can change this mark");
            if (o.LeaseHolder != member || nowMs >= o.LeaseUntilMs) return ToyResult.Reject(ToyReason.NoLease, "take the short edit lease first");
            return ToyResult.Ok();
        }

        ToyResult LeaseObject(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            CanvasObject o = TargetObject(ctx, s);
            if (o.Author != ctx.Member && !o.Shared) return ToyResult.Reject(ToyReason.NotOwner);
            if (o.LeaseHolder != null && o.LeaseHolder != ctx.Member && ctx.NowMs < o.LeaseUntilMs) return ToyResult.Reject(ToyReason.LeaseHeld, o.LeaseHolder);
            o.LeaseHolder = ctx.Member;
            o.LeaseUntilMs = ctx.NowMs + CanvasLimits.TransformLeaseMs;
            return ToyResult.Ok(o.Id);
        }

        ToyResult TransformObject(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            CanvasObject o = TargetObject(ctx, s);
            int tx = ctx.P.Int("tx", -2 * CanvasLimits.SheetWidth, 2 * CanvasLimits.SheetWidth);
            int ty = ctx.P.Int("ty", -2 * CanvasLimits.SheetWidth, 2 * CanvasLimits.SheetWidth);
            int rot = ctx.P.Int("rot", -36000, 36000);
            int scale = ctx.P.Int("scale", 100, 8000);
            ToyResult can = CanEdit(o, ctx.Member, ctx.NowMs);
            if (!can.Accepted) return can;
            var before = Capture(o, CanvasUndoKind.Transform, s);
            o.Tx = tx; o.Ty = ty; o.Rot = rot; o.Scale = scale;
            if (o.LeaseHolder == ctx.Member) o.LeaseUntilMs = ctx.NowMs + CanvasLimits.TransformLeaseMs;
            Touch(o, ctx.Member);
            before.RevisionAfter = o.Revision;
            PushUndo(ctx.Member, before);
            return ToyResult.Ok(o.Id);
        }

        ToyResult StyleObject(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            CanvasObject o = TargetObject(ctx, s);
            long color = Color(ctx);
            int width = ctx.P.Int("width", CanvasLimits.MinWidth, CanvasLimits.MaxWidth, Math.Max(CanvasLimits.MinWidth, o.Width));
            ToyResult can = CanEdit(o, ctx.Member, ctx.NowMs);
            if (!can.Accepted) return can;
            var before = Capture(o, CanvasUndoKind.Style, s);
            o.Color = color;
            if (o.Kind != CanvasObjectKind.Text && o.Kind != CanvasObjectKind.Stamp) o.Width = width;
            Touch(o, ctx.Member);
            before.RevisionAfter = o.Revision;
            PushUndo(ctx.Member, before);
            return ToyResult.Ok(o.Id);
        }

        ToyResult ShareObject(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            CanvasObject o = TargetObject(ctx, s);
            bool shared = ctx.P.Bool("shared");
            if (o.Author != ctx.Member) return ToyResult.Reject(ToyReason.NotOwner);
            var before = Capture(o, CanvasUndoKind.Share, s);
            o.Shared = shared;
            if (!shared) { o.LeaseHolder = null; o.LeaseUntilMs = 0; }
            Touch(o, ctx.Member);
            before.RevisionAfter = o.Revision;
            PushUndo(ctx.Member, before);
            return ToyResult.Ok(o.Id);
        }

        ToyResult EraseObject(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            CanvasObject o = TargetObject(ctx, s);
            ToyResult can = CanEdit(o, ctx.Member, ctx.NowMs);
            if (!can.Accepted) return can;
            var before = Capture(o, CanvasUndoKind.Presence, s);
            o.Deleted = true;
            o.Open = false;
            Touch(o, ctx.Member);
            before.RevisionAfter = o.Revision;
            PushUndo(ctx.Member, before);
            return ToyResult.Ok(o.Id);
        }

        void Touch(CanvasObject o, string member, bool strokeContinuation = false)
        {
            Document.Revision++;
            o.Revision = Document.Revision;
            // Appending/ending one's own open stroke keeps its creation entry applicable.
            CanvasHistory h = History(member, false);
            if (strokeContinuation && h != null && o.Author == member && !o.Deleted)
                for (int i = h.Undo.Count - 1; i >= 0; i--)
                {
                    CanvasUndoEntry e = h.Undo[i];
                    if (e.ObjectId != o.Id) continue;
                    if (e.Kind == CanvasUndoKind.Presence && e.Deleted) e.RevisionAfter = o.Revision;
                    break;
                }
        }

        static CanvasUndoEntry Capture(CanvasObject o, CanvasUndoKind kind, CanvasSheet s) => new CanvasUndoEntry
        {
            Kind = kind, ObjectId = o.Id, SheetId = s.SheetId, SheetEpoch = s.Epoch, Deleted = o.Deleted, Tx = o.Tx, Ty = o.Ty, Rot = o.Rot,
            Scale = o.Scale, Color = o.Color, Width = o.Width, Shared = o.Shared,
        };

        CanvasHistory History(string member, bool create)
        {
            CanvasHistory h = Document.Histories.FirstOrDefault(x => x.Member == member);
            if (h == null && create) Document.Histories.Add(h = new CanvasHistory { Member = member });
            return h;
        }

        void PushUndo(string member, CanvasUndoEntry e)
        {
            CanvasHistory h = History(member, true);
            h.Undo.Add(e);
            if (h.Undo.Count > CanvasLimits.UndoDepth) h.Undo.RemoveAt(0);
            h.Redo.Clear();
            CompactTombstones();
        }

        /// <summary>
        /// Own-action undo/redo: swaps the object back to its recorded state only when nobody changed that object since
        /// (otherwise the entry is dropped — undo never rewinds someone else's later action) and its sheet was not cleared.
        /// </summary>
        ToyResult UndoRedo(string member, bool undo)
        {
            CanvasHistory h = History(member, false);
            List<CanvasUndoEntry> from = h == null ? null : (undo ? h.Undo : h.Redo);
            if (from == null || from.Count == 0) return ToyResult.Reject(ToyReason.NotFound, undo ? "nothing to undo" : "nothing to redo");
            CanvasUndoEntry e = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            CanvasSheet s = Document.Sheet(e.SheetId);
            CanvasObject o = s?.Objects.FirstOrDefault(x => x.Id == e.ObjectId);
            if (s == null || s.Epoch != e.SheetEpoch || o == null) return ToyResult.Reject(ToyReason.StaleEpoch, "that mark no longer exists");
            if (o.Revision != e.RevisionAfter) return ToyResult.Reject(ToyReason.Conflict, "someone changed that mark since");
            if (e.Kind == CanvasUndoKind.Presence && e.Deleted == false && LiveObjectCount + 1 > CanvasLimits.MaxObjects)
                return ToyResult.Reject(ToyReason.LimitReached);
            CanvasUndoEntry inverse = Capture(o, e.Kind, s);
            switch (e.Kind)
            {
                case CanvasUndoKind.Presence: o.Deleted = e.Deleted; o.Open = false; break;
                case CanvasUndoKind.Transform: o.Tx = e.Tx; o.Ty = e.Ty; o.Rot = e.Rot; o.Scale = e.Scale; break;
                case CanvasUndoKind.Style: o.Color = e.Color; o.Width = e.Width; break;
                case CanvasUndoKind.Share: o.Shared = e.Shared; if (!o.Shared) o.LeaseHolder = null; break;
            }
            Document.Revision++;
            o.Revision = Document.Revision;
            inverse.RevisionAfter = o.Revision;
            (undo ? h.Redo : h.Undo).Add(inverse);
            if (h.Redo.Count > CanvasLimits.UndoDepth) h.Redo.RemoveAt(0);
            return ToyResult.Ok(o.Id);
        }

        /// <summary>Erased marks are kept only while some history entry can still bring them back.</summary>
        void CompactTombstones()
        {
            int tombstones = Document.Sheets.Sum(s => s.Objects.Count(o => o.Deleted));
            if (tombstones < 256) return;
            var referenced = new HashSet<string>(Document.Histories.SelectMany(h => h.Undo.Concat(h.Redo)).Select(e => e.ObjectId));
            foreach (CanvasSheet s in Document.Sheets) s.Objects.RemoveAll(o => o.Deleted && !referenced.Contains(o.Id));
        }

        ToyResult NewSheet(ToyCommandContext ctx)
        {
            if (Document.Sheets.Count >= CanvasLimits.MaxSheets) return ToyResult.Reject(ToyReason.LimitReached, "six sheets are kept; clear one with everyone's agreement first");
            string name = ctx.P.Has("name") ? ctx.P.String("name", 24) : "Sheet " + (Document.SheetCounter + 1);
            if (!CanvasText.IsValid(name)) return ToyResult.Reject(ToyReason.Malformed);
            Document.Revision++;
            return ToyResult.Ok(AddSheet(name).SheetId);
        }

        // --------------------------------------------------------------------------------------------------------------
        // Whole-sheet clear: explicit proposal, current active contributors must accept (silence ≠ consent), checkpoint first.

        List<string> ActiveContributors(CanvasSheet s)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (CanvasObject o in s.Objects) if (!o.Deleted && host.IsActiveMember(o.Author)) set.Add(o.Author);
            foreach (string m in host.ActiveUsers(Id)) set.Add(m);
            return set.ToList();
        }

        ToyResult ProposeClear(ToyCommandContext ctx)
        {
            CanvasSheet s = TargetSheet(ctx);
            if (Document.Proposal != null && Document.Proposal.State == ProposalState.Open) return ToyResult.Reject(ToyReason.Busy);
            Document.Proposal = ConsentProposal.Open(host.NextId("prop"), "clear", s.SheetId, ctx.Member, ActiveContributors(s), ctx.NowMs);
            string id = Document.Proposal.Id;
            ResolveProposal(ctx.NowMs);
            return ToyResult.Ok(id);
        }

        ToyResult Vote(ToyCommandContext ctx)
        {
            string id = ctx.P.Id("proposal");
            bool accept = ctx.P.Bool("accept");
            if (Document.Proposal == null || Document.Proposal.Id != id) return ToyResult.Reject(ToyReason.NotFound);
            if (!Document.Proposal.Vote(ctx.Member, accept)) return ToyResult.Reject(ToyReason.ProposalClosed);
            ResolveProposal(ctx.NowMs);
            return ToyResult.Ok(id);
        }

        void ResolveProposal(long nowMs)
        {
            ConsentProposal p = Document.Proposal;
            if (p == null) return;
            p.Retain(host.IsActiveMember);
            p.Tick(nowMs);
            if (p.State == ProposalState.Open) return;
            if (p.State == ProposalState.Accepted)
            {
                CanvasSheet s = Document.Sheet(p.Argument);
                if (s != null) ClearSheet(s);
            }
            Document.LastProposal = p;
            Document.Proposal = null;
        }

        void ClearSheet(CanvasSheet s)
        {
            Document.Revision++;
            Document.Checkpoints.Add(new CanvasCheckpoint
            {
                CheckpointId = "cp-" + Document.Revision, SheetId = s.SheetId, SheetName = s.Name, Reason = "pre-clear", AtRevision = Document.Revision,
                Objects = s.Objects.Where(o => !o.Deleted).Select(Clone).ToList(),
            });
            while (Document.Checkpoints.Count > CanvasLimits.MaxCheckpoints) Document.Checkpoints.RemoveAt(0);
            s.Objects.Clear();
            s.Epoch++;
            foreach (CanvasHistory h in Document.Histories)
            {
                h.Undo.RemoveAll(e => e.SheetId == s.SheetId);
                h.Redo.RemoveAll(e => e.SheetId == s.SheetId);
            }
        }

        /// <summary>Restores a pre-clear checkpoint as a NEW sheet (never overwrites anything drawn since).</summary>
        ToyResult RestoreCheckpoint(ToyCommandContext ctx)
        {
            string id = ctx.P.Id("checkpoint");
            CanvasCheckpoint cp = Document.Checkpoints.FirstOrDefault(c => c.CheckpointId == id);
            if (cp == null) return ToyResult.Reject(ToyReason.NotFound);
            if (Document.Sheets.Count >= CanvasLimits.MaxSheets) return ToyResult.Reject(ToyReason.LimitReached, "no free sheet to restore into");
            ToyResult budget = CheckBudget(cp.Objects.Count, cp.Objects.Sum(o => o.PointCount));
            if (!budget.Accepted) return budget;
            CanvasSheet s = AddSheet((cp.SheetName ?? "Sheet") + " (restored)");
            Document.Revision++;
            foreach (CanvasObject o in cp.Objects)
            {
                CanvasObject c = Clone(o);
                c.Id = "o-" + (++Document.ObjectCounter);
                c.Revision = Document.Revision;
                c.LeaseHolder = null;
                s.Objects.Add(c);
            }
            return ToyResult.Ok(s.SheetId);
        }

        static CanvasObject Clone(CanvasObject o) => new CanvasObject
        {
            Id = o.Id, Author = o.Author, Kind = o.Kind, Color = o.Color, Width = o.Width, Points = o.Points == null ? null : (int[])o.Points.Clone(),
            Text = o.Text, StampId = o.StampId, Size = o.Size, Tx = o.Tx, Ty = o.Ty, Rot = o.Rot, Scale = o.Scale, Shared = o.Shared, Revision = o.Revision,
        };

        // --------------------------------------------------------------------------------------------------------------

        void FinalizeOpenStrokes(string member)
        {
            foreach (CanvasSheet s in Document.Sheets)
                foreach (CanvasObject o in s.Objects)
                    if (o.Open && (member == null || o.Author == member)) o.Open = false;
        }

        public void Advance(long nowMs)
        {
            foreach (CanvasSheet s in Document.Sheets)
                foreach (CanvasObject o in s.Objects)
                    if (o.LeaseHolder != null && nowMs >= o.LeaseUntilMs) o.LeaseHolder = null;
            ResolveProposal(nowMs);
        }

        /// <summary>Pause: every stroke in progress is finalized through its last ACCEPTED point; edit leases end.</summary>
        public void Freeze(long nowMs)
        {
            Run.Frozen = true;
            FinalizeOpenStrokes(null);
            foreach (CanvasSheet s in Document.Sheets)
                foreach (CanvasObject o in s.Objects) o.LeaseHolder = null;
        }

        public void EndPause(long nowMs) { Run.Frozen = false; }
        public void Resume(string member, long nowMs) { Run.Frozen = false; }

        public void ReleaseControl(string member, ReleaseCause cause, long nowMs)
        {
            FinalizeOpenStrokes(member);
            foreach (CanvasSheet s in Document.Sheets)
                foreach (CanvasObject o in s.Objects)
                    if (o.LeaseHolder == member) o.LeaseHolder = null;
            ResolveProposal(nowMs);
        }

        /// <summary>Explicit departure: accepted artwork stays; the old membership's undo rights do not carry over.</summary>
        public void Retire(string member, long nowMs)
        {
            ReleaseControl(member, ReleaseCause.Departed, nowMs);
            Document.Histories.RemoveAll(h => h.Member == member);
        }
    }
}
