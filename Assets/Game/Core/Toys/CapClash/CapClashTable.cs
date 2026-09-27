using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NightSignal.Core.Toys.CapClash
{
    /// <summary>One participant's reusable cap while it is on the tray (off the tray = available to shoot).</summary>
    public sealed class CapBody
    {
        public string CapId;
        public string Owner;
        public Vec2 Pos;
        public Vec2 Vel;
        public bool Moving;
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ShotQueueState { Queued = 0, AwaitingReconfirm = 1 }

    /// <summary>A submitted shot waiting in the one-request-per-person serialized queue.</summary>
    public sealed class ShotRequest
    {
        public string ShotId;
        public string Member;
        /// <summary>Degrees from straight up-table; positive aims right (+x).</summary>
        public double AngleDeg;
        /// <summary>0..1 of the equal maximum launch speed.</summary>
        public double Power;
        public double LaunchX;
        /// <summary>Board revision the shooter's preview was made against.</summary>
        public long SeenBoardRevision;
        public ShotQueueState State;
        public long DeadlineMs;
    }

    public sealed class ShotInFlight
    {
        public string ShotId;
        public string Member;
        public string TargetId;
        public bool Banked;
        public int CapContacts;
        public bool FellOff;
    }

    /// <summary>A settled shot. Session fun only: no Credits/RP/records (non-progression domain).</summary>
    public sealed class ShotOutcome : INonProgressionRecord
    {
        [JsonIgnore] public string Domain => NonProgression.Domain;
        public string ShotId;
        public string Member;
        public string TargetId;
        public bool OnBoard;
        public double? Distance;
        public int Points;
        public bool Banked;
        public int CapContacts;
        public string MarkFilled;
        public bool CardCompleted;
    }

    /// <summary>The saved board of ONE arrangement (switching arrangement keeps the previous board).</summary>
    public sealed class CapBoardState
    {
        public string ArrangementId;
        public string TargetId;
        /// <summary>Incremented whenever settled positions change (reconfirm check for queued previews).</summary>
        public long BoardRevision = 1;
        public List<CapBody> Caps = new List<CapBody>();
        public ShotInFlight InFlight;
        /// <summary>Cooperative six-mark card: mark id → member who filled it.</summary>
        public Dictionary<string, string> Card = new Dictionary<string, string>();
        public int CardsCompleted;
        public List<ShotOutcome> History = new List<ShotOutcome>();
    }

    /// <summary>Personal session best per arrangement + target (smallest settled distance; ties stay ties).</summary>
    public sealed class CapBest : INonProgressionRecord
    {
        [JsonIgnore] public string Domain => NonProgression.Domain;
        public string Member;
        public string ArrangementId;
        public string TargetId;
        public double? BestDistance;
        public int BestPoints;
        public int Shots;
    }

    public sealed class CapClashState
    {
        public ToyRunState Run = new ToyRunState();
        public string Active;
        public Dictionary<string, CapBoardState> Boards = new Dictionary<string, CapBoardState>();
        public List<ShotRequest> Queue = new List<ShotRequest>();
        public List<CapBest> Bests = new List<CapBest>();
        public ConsentProposal Proposal;
        public ConsentProposal LastProposal;
        public long SimTick;
    }

    public sealed class CapStanding
    {
        public string Member;
        public double? CurrentDistance;
        public int CurrentPoints;
        public bool Crown;
        public double? BestDistance;
        public int BestPoints;
    }

    /// <summary>
    /// Cap Clash (Addendum 02 §2): bounded planar disc physics with friction, restitution, cap-cap contact, rails/bumpers
    /// and rubberized obstacles; one reusable cap per participant; a serialized one-request-per-person shot queue with a
    /// reconfirm window; bodies settle before the next shot unless a pause freezes them first.
    /// </summary>
    public sealed class CapClashTable : IToyActivity
    {
        public const long ReconfirmWindowMs = 5_000;
        public const int HistoryLimit = 24;

        readonly IToyHost host;
        readonly CapClashContent content;
        public CapClashState State { get; private set; }

        public CapClashTable(IToyHost host, CapClashContent content, CapClashState state)
        {
            this.host = host;
            this.content = content;
            State = state ?? new CapClashState();
            if (State.Active == null || content.Arrangement(State.Active) == null) State.Active = content.Arrangements[0].Id;
            EnsureBoard(State.Active);
        }

        public ToyActivityId Id => ToyActivityId.CapClash;
        public ToyRunState Run => State.Run;
        public bool RequiresExplicitResume => true;
        public object StateObject => State;
        public CapPhysicsDef Physics => content.Physics;
        public CapArrangementDef ActiveArrangement => content.Arrangement(State.Active);
        public CapBoardState Board => State.Boards[State.Active];
        public CapTargetDef ActiveTarget => ActiveArrangement.Target(Board.TargetId);

        public bool NeedsSimulation => !Run.Frozen && Board.Caps.Any(c => c.Moving);
        public bool IsSettled => Board.InFlight == null && !Board.Caps.Any(c => c.Moving);

        public bool IsTransient(string kind) => false;
        public bool TakesControl(string kind) => kind == "shot.submit" || kind == "shot.confirm" || kind == "shot.cancel";

        CapBoardState EnsureBoard(string arrangementId)
        {
            if (!State.Boards.TryGetValue(arrangementId, out CapBoardState b))
            {
                CapArrangementDef a = content.Arrangement(arrangementId);
                b = new CapBoardState { ArrangementId = arrangementId, TargetId = a.DefaultTarget };
                State.Boards[arrangementId] = b;
            }
            return b;
        }

        // --------------------------------------------------------------------------------------------------------------
        // Commands

        public ToyResult Apply(ToyCommandContext ctx)
        {
            switch (ctx.Kind)
            {
                case "shot.submit": return Submit(ctx);
                case "shot.cancel": return Cancel(ctx.Member);
                case "shot.confirm": return Confirm(ctx);
                case "target.select": return Propose(ctx, "target", ctx.P.Id("target"));
                case "arrangement.select": return Propose(ctx, "arrangement", ctx.P.Id("arrangement"));
                case "proposal.vote": return Vote(ctx);
                case "card.reset": return ResetCard();
                default: return ToyResult.Reject(ToyReason.UnknownKind);
            }
        }

        ToyResult Submit(ToyCommandContext ctx)
        {
            CapArrangementDef a = ActiveArrangement;
            double angle = ctx.P.Double("angle", -a.MaxAngleDeg, a.MaxAngleDeg);
            double power = ctx.P.Double("power", Physics.MinPower, 1.0);
            double launchX = ctx.P.Double("launchX", a.LaunchMinX, a.LaunchMaxX);
            long seen = ctx.P.Has("seen") ? ctx.P.Long("seen", 0, long.MaxValue) : 0;
            if (Run.Frozen) Resume(ctx.Member, ctx.NowMs); // submitting is an explicit participant action
            ShotRequest existing = State.Queue.FirstOrDefault(q => q.Member == ctx.Member);
            if (existing != null)
            {
                // One request per person: a resubmission updates the aim and keeps the queue position.
                existing.AngleDeg = angle; existing.Power = power; existing.LaunchX = launchX; existing.SeenBoardRevision = seen;
                existing.ShotId = ctx.RequestId;
                if (existing.State == ShotQueueState.AwaitingReconfirm && seen == Board.BoardRevision) Fire(existing, ctx.NowMs);
                return ToyResult.Ok(existing.ShotId);
            }
            if (State.Queue.Count >= ToyLimits.MaxMembers) return ToyResult.Reject(ToyReason.LimitReached);
            State.Queue.Add(new ShotRequest
            {
                ShotId = ctx.RequestId, Member = ctx.Member, AngleDeg = angle, Power = power, LaunchX = launchX, SeenBoardRevision = seen,
                State = ShotQueueState.Queued,
            });
            ProcessQueue(ctx.NowMs);
            return ToyResult.Ok(ctx.RequestId);
        }

        ToyResult Cancel(string member)
        {
            int removed = State.Queue.RemoveAll(q => q.Member == member);
            return removed > 0 ? ToyResult.Ok() : ToyResult.Reject(ToyReason.NotFound, "no queued shot");
        }

        ToyResult Confirm(ToyCommandContext ctx)
        {
            ShotRequest head = State.Queue.Count > 0 ? State.Queue[0] : null;
            if (head == null || head.Member != ctx.Member || head.State != ShotQueueState.AwaitingReconfirm)
                return ToyResult.Reject(ToyReason.InvalidState, "not your turn to confirm");
            CapArrangementDef a = ActiveArrangement;
            head.AngleDeg = ctx.P.Double("angle", -a.MaxAngleDeg, a.MaxAngleDeg, head.AngleDeg);
            head.Power = ctx.P.Double("power", Physics.MinPower, 1.0, head.Power);
            head.LaunchX = ctx.P.Double("launchX", a.LaunchMinX, a.LaunchMaxX, head.LaunchX);
            head.SeenBoardRevision = Board.BoardRevision;
            Fire(head, ctx.NowMs);
            return ToyResult.Ok(head.ShotId);
        }

        ToyResult Propose(ToyCommandContext ctx, string kind, string argument)
        {
            if (kind == "arrangement" && content.Arrangement(argument) == null) return ToyResult.Reject(ToyReason.NotFound);
            if (kind == "target" && ActiveArrangement.Target(argument) == null) return ToyResult.Reject(ToyReason.NotFound);
            if (State.Proposal != null && State.Proposal.State == ProposalState.Open) return ToyResult.Reject(ToyReason.Busy, "another proposal is open");
            var p = ConsentProposal.Open(host.NextId("prop"), kind, argument, ctx.Member, host.ActiveUsers(Id), ctx.NowMs);
            State.Proposal = p;
            ResolveProposal(ctx.NowMs);
            return ToyResult.Ok(p.Id);
        }

        ToyResult Vote(ToyCommandContext ctx)
        {
            string id = ctx.P.Id("proposal");
            bool accept = ctx.P.Bool("accept");
            if (State.Proposal == null || State.Proposal.Id != id) return ToyResult.Reject(ToyReason.NotFound);
            if (!State.Proposal.Vote(ctx.Member, accept)) return ToyResult.Reject(ToyReason.ProposalClosed);
            ResolveProposal(ctx.NowMs);
            return ToyResult.Ok(id);
        }

        ToyResult ResetCard()
        {
            CapBoardState b = Board;
            if (b.Card.Count < ActiveArrangement.Card.Count) return ToyResult.Reject(ToyReason.ConsentRequired, "only a completed card can be restarted");
            b.Card.Clear();
            return ToyResult.Ok();
        }

        void ResolveProposal(long nowMs)
        {
            ConsentProposal p = State.Proposal;
            if (p == null) return;
            p.Retain(host.IsActiveMember);
            p.Tick(nowMs);
            if (p.State == ProposalState.Open) return;
            if (p.State == ProposalState.Accepted)
            {
                // A switch never interrupts a moving board: it applies once everything has settled.
                if (!IsSettled) return;
                if (p.Kind == "arrangement")
                {
                    State.Active = p.Argument;
                    EnsureBoard(p.Argument);
                    State.Queue.Clear(); // queued aims belonged to the other table; that board itself is preserved
                    Run.Epoch++;
                }
                else
                {
                    Board.TargetId = p.Argument;
                    Board.BoardRevision++;
                }
            }
            State.LastProposal = p;
            State.Proposal = null;
        }

        // --------------------------------------------------------------------------------------------------------------
        // Queue and simulation

        void ProcessQueue(long nowMs)
        {
            if (Run.Frozen || nowMs < Run.OrientUntilMs || !IsSettled || State.Queue.Count == 0) return;
            ShotRequest head = State.Queue[0];
            if (head.State == ShotQueueState.Queued)
            {
                if (head.SeenBoardRevision == Board.BoardRevision) { Fire(head, nowMs); return; }
                head.State = ShotQueueState.AwaitingReconfirm;
                head.DeadlineMs = nowMs + ReconfirmWindowMs;
            }
            else if (nowMs >= head.DeadlineMs)
            {
                State.Queue.RemoveAt(0); // no response: release the table to the next person
                ProcessQueue(nowMs);
            }
        }

        void Fire(ShotRequest shot, long nowMs)
        {
            State.Queue.Remove(shot);
            CapBoardState b = Board;
            CapArrangementDef a = ActiveArrangement;
            CapBody cap = b.Caps.FirstOrDefault(c => c.Owner == shot.Member);
            if (cap == null)
            {
                cap = new CapBody { CapId = "cap:" + shot.Member, Owner = shot.Member };
                b.Caps.Add(cap);
            }
            CapPhysics.Launch(a, Physics, cap, shot.AngleDeg, shot.Power, shot.LaunchX);
            b.InFlight = new ShotInFlight { ShotId = shot.ShotId, Member = shot.Member, TargetId = b.TargetId };
            State.SimTick = TickAt(nowMs);
        }

        long TickAt(long nowMs) => nowMs * Physics.StepHz / 1000;

        public void Advance(long nowMs)
        {
            long target = TickAt(nowMs);
            if (Run.Frozen)
            {
                // Frozen state is left exactly as it was at the boundary; Resume re-bases the simulation clock.
                ResolveProposal(nowMs);
                return;
            }
            if (nowMs < Run.OrientUntilMs || !Board.Caps.Any(c => c.Moving))
            {
                State.SimTick = target;
                if (Board.InFlight != null && nowMs >= Run.OrientUntilMs) Settle();
            }
            else
            {
                long maxSteps = ToyLimits.MaxCatchUpMs * Physics.StepHz / 1000;
                long steps = Math.Min(target - State.SimTick, maxSteps);
                for (long i = 0; i < steps; i++)
                {
                    Step();
                    if (!Board.Caps.Any(c => c.Moving)) { Settle(); break; }
                }
                State.SimTick = target;
            }
            ResolveProposal(nowMs);
            ProcessQueue(nowMs);
        }

        /// <summary>One fixed physics step of the active board.</summary>
        public void Step() => CapPhysics.Step(ActiveArrangement, Physics, Board.Caps, Board.InFlight);

        void Settle()
        {
            CapBoardState b = Board;
            CapArrangementDef a = ActiveArrangement;
            ShotInFlight shot = b.InFlight;
            b.Caps.RemoveAll(c => c.Pos.Y < a.FoulLineY); // short caps return to their owners
            b.BoardRevision++;
            b.InFlight = null;
            if (shot == null) return;
            CapTargetDef target = a.Target(shot.TargetId) ?? ActiveTarget;
            CapBody cap = b.Caps.FirstOrDefault(c => c.Owner == shot.Member);
            var outcome = new ShotOutcome
            {
                ShotId = shot.ShotId, Member = shot.Member, TargetId = target.Id, OnBoard = cap != null, Banked = shot.Banked, CapContacts = shot.CapContacts,
            };
            if (cap != null)
            {
                double dist = (cap.Pos - new Vec2(target.X, target.Y)).Length();
                outcome.Distance = dist;
                outcome.Points = target.PointsAt(dist);
            }
            CapBest best = State.Bests.FirstOrDefault(x => x.Member == shot.Member && x.ArrangementId == a.Id && x.TargetId == target.Id);
            if (best == null)
            {
                best = new CapBest { Member = shot.Member, ArrangementId = a.Id, TargetId = target.Id };
                State.Bests.Add(best);
            }
            best.Shots++;
            if (outcome.Distance.HasValue && (!best.BestDistance.HasValue || outcome.Distance.Value < best.BestDistance.Value))
                best.BestDistance = outcome.Distance;
            if (outcome.Points > best.BestPoints) best.BestPoints = outcome.Points;

            foreach (CapMarkDef m in a.Card)
            {
                if (b.Card.ContainsKey(m.Id) || m.Target != target.Id || outcome.Points < m.MinPoints || (m.RequireBank && !outcome.Banked)) continue;
                b.Card[m.Id] = shot.Member;
                outcome.MarkFilled = m.Id;
                if (b.Card.Count == a.Card.Count) { b.CardsCompleted++; outcome.CardCompleted = true; }
                break;
            }
            b.History.Add(outcome);
            if (b.History.Count > HistoryLimit) b.History.RemoveAt(0);
        }

        // --------------------------------------------------------------------------------------------------------------
        // Standings: current-board crown vs personal best (different metrics, Addendum 02 §2.1)

        public List<CapStanding> Standings()
        {
            CapBoardState b = Board;
            CapTargetDef t = ActiveTarget;
            var target = new Vec2(t.X, t.Y);
            var list = new List<CapStanding>();
            var members = new SortedSet<string>(b.Caps.Select(c => c.Owner).Concat(State.Bests.Where(x => x.ArrangementId == b.ArrangementId && x.TargetId == t.Id).Select(x => x.Member)), StringComparer.Ordinal);
            double? crown = null;
            foreach (CapBody c in b.Caps)
            {
                double d = (c.Pos - target).Length();
                if (d <= t.Zones[2] && (!crown.HasValue || d < crown.Value)) crown = d;
            }
            foreach (string m in members)
            {
                var s = new CapStanding { Member = m };
                CapBody cap = b.Caps.FirstOrDefault(c => c.Owner == m);
                if (cap != null)
                {
                    s.CurrentDistance = (cap.Pos - target).Length();
                    s.CurrentPoints = t.PointsAt(s.CurrentDistance.Value);
                    s.Crown = crown.HasValue && s.CurrentDistance.Value <= t.Zones[2] && s.CurrentDistance.Value - crown.Value <= Physics.TieTolerance;
                }
                CapBest best = State.Bests.FirstOrDefault(x => x.Member == m && x.ArrangementId == b.ArrangementId && x.TargetId == t.Id);
                if (best != null) { s.BestDistance = best.BestDistance; s.BestPoints = best.BestPoints; }
                list.Add(s);
            }
            return list;
        }

        // --------------------------------------------------------------------------------------------------------------
        // Pause / resume / membership

        public void Freeze(long nowMs)
        {
            Run.Frozen = true;
            foreach (ShotRequest q in State.Queue)
                if (q.State == ShotQueueState.AwaitingReconfirm) { q.State = ShotQueueState.Queued; q.DeadlineMs = 0; }
        }

        public void EndPause(long nowMs) { }

        public void Resume(string member, long nowMs)
        {
            if (!Run.Frozen) return;
            Run.Frozen = false;
            Run.OrientUntilMs = nowMs + ToyLimits.OrientationMs;
            State.SimTick = TickAt(nowMs);
        }

        public void ReleaseControl(string member, ReleaseCause cause, long nowMs)
        {
            // A closed, idle, minimised or disconnected panel never holds a place in the shot queue.
            State.Queue.RemoveAll(q => q.Member == member);
            if (State.Proposal != null) ResolveProposal(nowMs);
        }

        public void Retire(string member, long nowMs)
        {
            State.Queue.RemoveAll(q => q.Member == member);
            foreach (CapBoardState b in State.Boards.Values)
            {
                if (b.Caps.RemoveAll(c => c.Owner == member) > 0) b.BoardRevision++;
                if (b.InFlight != null && b.InFlight.Member == member) b.InFlight = null;
            }
        }
    }
}
