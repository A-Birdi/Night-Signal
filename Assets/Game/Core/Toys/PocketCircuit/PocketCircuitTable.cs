using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NightSignal.Core.Toys.PocketCircuit
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SlotCarMode
    {
        Driving = 0,
        /// <summary>Suspended in place (closed view, idle, disconnect, pause): position AND velocity are kept; no lap time accrues.</summary>
        Parked = 1,
        /// <summary>Explicitly resumed: short reorientation, no throttle applied, no lap time accrues.</summary>
        Orienting = 2,
        /// <summary>Harmless de-slot: returns to the last safe point shortly (lap time keeps running; the lap is flagged).</summary>
        DeSlotted = 3,
    }

    [Flags]
    public enum LapFlags
    {
        None = 0,
        DeSlotted = 1,
        VoluntaryPause = 2,
        /// <summary>Suspended by the main event (or a service restore); annotated as a mandatory-resumed lap.</summary>
        Preempted = 4,
        /// <summary>Disconnect or focus loss mid-lap.</summary>
        Interrupted = 8,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum LapCategory { Clean = 0, DeSlotted = 1, VoluntarilyPaused = 2, Preempted = 3, Interrupted = 4 }

    public sealed class SlotCarState
    {
        public string Member;
        public int Lane;
        /// <summary>Distance along the lane within the current lap.</summary>
        public double S;
        public double V;
        /// <summary>Applied throttle 0..1 (0 whenever not driving; never restored from a stale hold).</summary>
        public double Throttle;
        public long ThrottleUntilMs;
        public SlotCarMode Mode;
        public long ModeUntilMs;
        public double LastSafeS;
        /// <summary>Lap in progress; 0 = the out lap from the grid (not timed).</summary>
        public int Lap;
        /// <summary>ACTIVE time of the lap in progress, in simulation ticks.</summary>
        public long LapTicks;
        public LapFlags Flags;
        public int Deslots;
        public int TotalLaps;
        public int CleanLaps;
    }

    /// <summary>A completed toy lap. Never a full-size course record (non-progression domain).</summary>
    public sealed class SlotLap : INonProgressionRecord
    {
        [JsonIgnore] public string Domain => NonProgression.Domain;
        public string Member;
        public string LayoutId;
        public int Lane;
        public int LapNumber;
        public long ActiveMs;
        public LapCategory Category;
        public LapFlags Flags;
        /// <summary>Raw lap divided by the disclosed lane-reference ratio (cross-lane comparison only).</summary>
        public long NormalizedMs;
    }

    public sealed class SlotLaneBest : INonProgressionRecord
    {
        [JsonIgnore] public string Domain => NonProgression.Domain;
        public string Member;
        public int Lane;
        public long BestMs;
        public long NormalizedMs;
    }

    /// <summary>The saved board of ONE layout (Addendum 02 §5.3: every layout keeps its board and records).</summary>
    public sealed class SlotLayoutBoard
    {
        public string LayoutId;
        public List<SlotCarState> Cars = new List<SlotCarState>();
        public List<SlotLap> RecentLaps = new List<SlotLap>();
        /// <summary>Uninterrupted (clean) bests per member and lane; raw times compare only within a lane.</summary>
        public List<SlotLaneBest> Bests = new List<SlotLaneBest>();
        public int CleanLapCount;
        public int CoopTarget = 12;
        public int CoopProgress;
        public int CoopCompleted;
        public Dictionary<string, int> CoopContributions = new Dictionary<string, int>();
    }

    public sealed class PocketCircuitState
    {
        public ToyRunState Run = new ToyRunState();
        public string ActiveLayout;
        public Dictionary<string, SlotLayoutBoard> Boards = new Dictionary<string, SlotLayoutBoard>();
        public ConsentProposal Proposal;
        public ConsentProposal LastProposal;
        public long SimTick;
    }

    public struct SlotStepResult
    {
        public bool Deslotted;
        public bool LapCompleted;
        public long CompletedLapTicks;
    }

    /// <summary>Fixed-step toy-car integration shared by the authority, the client predictor and the reference driver.</summary>
    public static class SlotSim
    {
        public static SlotStepResult Step(SlotLane lane, SlotCarPhysics ph, SlotCarState car)
        {
            var r = new SlotStepResult();
            double dt = 1.0 / ph.StepHz;
            int i = lane.IndexAt(car.S);
            double a = ph.Acceleration(car.Throttle, car.V, lane.Slope[i]);
            car.V = Math.Max(0, car.V + a * dt);
            double s = car.S + car.V * dt;
            car.LapTicks++;
            if (s >= lane.Length)
            {
                s -= lane.Length;
                r.LapCompleted = true;
                r.CompletedLapTicks = car.Lap >= 1 ? car.LapTicks : 0;
                car.Lap++;
                car.LapTicks = 0;
            }
            car.S = s;
            int j = lane.IndexAt(s);
            if (lane.Piece[j] != lane.Piece[i] || r.LapCompleted) car.LastSafeS = lane.PieceStart[lane.Piece[j]];
            double demand = car.V * car.V * Math.Abs(lane.Curvature[j]);
            if (demand > ph.GripLimit(car.V, lane.VerticalCurvature[j]))
            {
                r.Deslotted = true;
                car.V = 0;
                car.Deslots++;
                car.Flags |= LapFlags.DeSlotted;
            }
            return r;
        }
    }

    /// <summary>
    /// Pocket Circuit (Addendum 02 §5): a shared tabletop slot-car toy with 1–6 independently controlled lanes, analog
    /// throttle, speed²×curvature grip, harmless de-slots, ACTIVE-time lap timing, lap categories, rolling bests,
    /// cooperative clean-lap collection and consented layout changes that keep each layout's board.
    /// </summary>
    public sealed class PocketCircuitTable : IToyActivity
    {
        public const int RecentLapLimit = 40;

        readonly IToyHost host;
        readonly PocketCircuitContent content;
        public PocketCircuitState State { get; private set; }

        public PocketCircuitTable(IToyHost host, PocketCircuitContent content, PocketCircuitState state)
        {
            this.host = host;
            this.content = content;
            State = state ?? new PocketCircuitState();
            if (State.ActiveLayout == null || content.Layout(State.ActiveLayout) == null) State.ActiveLayout = content.Layouts[0].Id;
            EnsureBoard(State.ActiveLayout);
            foreach (SlotCarState c in Board.Cars.ToList())
                if (c.Lane < 1 || c.Lane > Track.Lanes.Length) Board.Cars.Remove(c); // a lane that no longer exists
        }

        public ToyActivityId Id => ToyActivityId.PocketCircuit;
        public ToyRunState Run => State.Run;
        public bool RequiresExplicitResume => true;
        public object StateObject => State;
        public SlotCarPhysics Physics => content.Physics;
        public SlotLayoutDef ActiveLayout => content.Layout(State.ActiveLayout);
        public SlotTrack Track => ActiveLayout.Track;
        public SlotLayoutBoard Board => State.Boards[State.ActiveLayout];
        public SlotCarState Car(string member) => Board.Cars.FirstOrDefault(c => c.Member == member);

        public bool NeedsSimulation => !Run.Frozen && Board.Cars.Any(c => c.Mode == SlotCarMode.Driving || c.Mode == SlotCarMode.DeSlotted);
        public bool IsTransient(string kind) => kind == "throttle";
        public bool TakesControl(string kind) => kind == "throttle" || kind == "lane.take";

        SlotLayoutBoard EnsureBoard(string id)
        {
            if (!State.Boards.TryGetValue(id, out SlotLayoutBoard b))
            {
                b = new SlotLayoutBoard { LayoutId = id };
                State.Boards[id] = b;
            }
            return b;
        }

        long TickAt(long nowMs) => nowMs * Physics.StepHz / 1000;
        long MsOf(long ticks) => ticks * 1000 / Physics.StepHz;

        // --------------------------------------------------------------------------------------------------------------

        public ToyResult Apply(ToyCommandContext ctx)
        {
            switch (ctx.Kind)
            {
                case "lane.take": return TakeLane(ctx);
                case "lane.leave": return LeaveLane(ctx);
                case "throttle": return Throttle(ctx);
                case "car.park": return ParkOwn(ctx);
                case "layout.select": return ProposeLayout(ctx);
                case "proposal.vote": return Vote(ctx);
                default: return ToyResult.Reject(ToyReason.UnknownKind);
            }
        }

        ToyResult TakeLane(ToyCommandContext ctx)
        {
            int lane = ctx.P.Int("lane", 1, Track.Lanes.Length);
            SlotLayoutBoard b = Board;
            SlotCarState holder = b.Cars.FirstOrDefault(c => c.Lane == lane);
            if (holder != null && holder.Member != ctx.Member)
            {
                // A lane kept for someone who is no longer an active member can be reused; their records stay.
                if (host.IsActiveMember(holder.Member)) return ToyResult.Reject(ToyReason.LeaseHeld, "lane in use");
                b.Cars.Remove(holder);
            }
            SlotCarState car = Car(ctx.Member);
            if (car != null && car.Lane == lane) return ToyResult.Ok(lane.ToString());
            if (car != null) b.Cars.Remove(car); // changing lanes discards the incompatible lap in progress
            SlotLane l = Track.Lane(lane);
            car = new SlotCarState
            {
                Member = ctx.Member, Lane = lane, S = l.Wrap(-Physics.GridBehindLine), Mode = SlotCarMode.Orienting,
                ModeUntilMs = ctx.NowMs + ToyLimits.OrientationMs,
            };
            car.LastSafeS = l.PieceStart[l.Piece[l.IndexAt(car.S)]];
            b.Cars.Add(car);
            if (Run.Frozen) Resume(ctx.Member, ctx.NowMs);
            return ToyResult.Ok(lane.ToString());
        }

        ToyResult LeaveLane(ToyCommandContext ctx)
        {
            SlotCarState car = Car(ctx.Member);
            if (car == null) return ToyResult.Reject(ToyReason.NotFound);
            Board.Cars.Remove(car);
            return ToyResult.Ok();
        }

        /// <summary>
        /// Analog throttle 0..1 (or a smoothed digital hold). Held input expires after <see cref="SlotCarPhysics.ThrottleHoldMs"/>
        /// without refresh. A parked car first reorients; input during orientation is ignored, so a stale held throttle is
        /// never applied after a resume.
        /// </summary>
        ToyResult Throttle(ToyCommandContext ctx)
        {
            double value = ctx.P.Double("value", 0, 1);
            SlotCarState car = Car(ctx.Member);
            if (car == null) return ToyResult.Reject(ToyReason.NotFound, "take a lane first");
            if (Run.Frozen) Resume(ctx.Member, ctx.NowMs);
            if (car.Mode == SlotCarMode.Parked)
            {
                car.Mode = SlotCarMode.Orienting;
                car.ModeUntilMs = ctx.NowMs + ToyLimits.OrientationMs;
                car.Throttle = 0;
                return ToyResult.Ok("orienting");
            }
            if (car.Mode == SlotCarMode.Orienting || ctx.NowMs < Run.OrientUntilMs) return ToyResult.Ok("orienting");
            if (car.Mode == SlotCarMode.DeSlotted) return ToyResult.Ok("deslotted");
            car.Throttle = value;
            car.ThrottleUntilMs = ctx.NowMs + Physics.ThrottleHoldMs;
            return ToyResult.Ok();
        }

        ToyResult ParkOwn(ToyCommandContext ctx)
        {
            SlotCarState car = Car(ctx.Member);
            if (car == null) return ToyResult.Reject(ToyReason.NotFound);
            Park(car, LapFlags.VoluntaryPause);
            return ToyResult.Ok();
        }

        void Park(SlotCarState car, LapFlags reason)
        {
            if (car.Mode == SlotCarMode.DeSlotted) { car.S = car.LastSafeS; car.V = 0; }
            if (car.Lap >= 1 && car.Mode != SlotCarMode.Parked) car.Flags |= reason;
            car.Mode = SlotCarMode.Parked;
            car.Throttle = 0;
            car.ThrottleUntilMs = 0;
        }

        ToyResult ProposeLayout(ToyCommandContext ctx)
        {
            string id = ctx.P.Id("layout");
            if (content.Layout(id) == null) return ToyResult.Reject(ToyReason.NotFound);
            if (id == State.ActiveLayout) return ToyResult.Ok();
            if (State.Proposal != null && State.Proposal.State == ProposalState.Open) return ToyResult.Reject(ToyReason.Busy);
            State.Proposal = ConsentProposal.Open(host.NextId("prop"), "layout", id, ctx.Member, host.ActiveUsers(Id), ctx.NowMs);
            string pid = State.Proposal.Id;
            ResolveProposal(ctx.NowMs);
            return ToyResult.Ok(pid);
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

        void ResolveProposal(long nowMs)
        {
            ConsentProposal p = State.Proposal;
            if (p == null) return;
            p.Retain(host.IsActiveMember);
            p.Tick(nowMs);
            if (p.State == ProposalState.Open) return;
            if (p.State == ProposalState.Accepted)
            {
                foreach (SlotCarState c in Board.Cars) Park(c, LapFlags.VoluntaryPause); // the old layout's board is kept as is
                State.ActiveLayout = p.Argument;
                EnsureBoard(p.Argument);
                Run.Epoch++;
            }
            State.LastProposal = p;
            State.Proposal = null;
        }

        // --------------------------------------------------------------------------------------------------------------

        public void Advance(long nowMs)
        {
            long target = TickAt(nowMs);
            if (Run.Frozen) { ResolveProposal(nowMs); return; } // frozen state stays exactly as saved; Resume re-bases the tick
            long maxSteps = ToyLimits.MaxCatchUpMs * Physics.StepHz / 1000;
            long steps = Math.Min(target - State.SimTick, maxSteps);
            long startTick = target - steps;
            for (long k = 1; k <= steps; k++)
            {
                long stepMs = MsOf(startTick + k);
                if (!StepAll(stepMs)) break; // nothing driving: skip idle time
            }
            State.SimTick = target;
            UpdateModes(nowMs);
            ResolveProposal(nowMs);
        }

        bool StepAll(long stepMs)
        {
            UpdateModes(stepMs);
            if (stepMs < Run.OrientUntilMs) return true;
            bool any = false;
            SlotTrack track = Track;
            foreach (SlotCarState car in Board.Cars)
            {
                if (car.Mode == SlotCarMode.DeSlotted) { car.LapTicks++; any = true; continue; }
                if (car.Mode != SlotCarMode.Driving) continue;
                any = true;
                SlotStepResult r = SlotSim.Step(track.Lane(car.Lane), Physics, car);
                if (r.Deslotted)
                {
                    car.Mode = SlotCarMode.DeSlotted;
                    car.ModeUntilMs = stepMs + Physics.DeslotReturnMs;
                    car.Throttle = 0;
                }
                if (r.LapCompleted) CompleteLap(car, r.CompletedLapTicks);
            }
            return any;
        }

        void UpdateModes(long nowMs)
        {
            foreach (SlotCarState car in Board.Cars)
            {
                if (car.Mode == SlotCarMode.Orienting && nowMs >= car.ModeUntilMs && nowMs >= Run.OrientUntilMs)
                {
                    car.Mode = SlotCarMode.Driving;
                    car.Throttle = 0;
                }
                else if (car.Mode == SlotCarMode.DeSlotted && nowMs >= car.ModeUntilMs)
                {
                    car.S = car.LastSafeS;
                    car.V = 0;
                    car.Mode = SlotCarMode.Driving;
                    car.Throttle = 0;
                }
                else if (car.Mode == SlotCarMode.Driving && car.Throttle > 0 && nowMs >= car.ThrottleUntilMs)
                    car.Throttle = 0; // a held input cannot outlive its refresh window
            }
        }

        public static LapCategory Categorize(LapFlags f)
        {
            if ((f & LapFlags.Preempted) != 0) return LapCategory.Preempted;
            if ((f & LapFlags.Interrupted) != 0) return LapCategory.Interrupted;
            if ((f & LapFlags.VoluntaryPause) != 0) return LapCategory.VoluntarilyPaused;
            if ((f & LapFlags.DeSlotted) != 0) return LapCategory.DeSlotted;
            return LapCategory.Clean;
        }

        void CompleteLap(SlotCarState car, long ticks)
        {
            LapFlags flags = car.Flags;
            car.Flags = LapFlags.None;
            if (ticks <= 0) return; // out lap from the grid
            SlotLayoutBoard b = Board;
            double ratio = Track.Stats[car.Lane - 1].LaneRatio;
            var lap = new SlotLap
            {
                Member = car.Member, LayoutId = b.LayoutId, Lane = car.Lane, LapNumber = car.Lap - 1, ActiveMs = MsOf(ticks), Flags = flags,
                Category = Categorize(flags),
            };
            lap.NormalizedMs = (long)Math.Round(lap.ActiveMs / ratio);
            b.RecentLaps.Add(lap);
            while (b.RecentLaps.Count > RecentLapLimit) b.RecentLaps.RemoveAt(0);
            car.TotalLaps++;
            if (lap.Category != LapCategory.Clean) return; // only uninterrupted clean laps enter bests
            car.CleanLaps++;
            b.CleanLapCount++;
            SlotLaneBest best = b.Bests.FirstOrDefault(x => x.Member == car.Member && x.Lane == car.Lane);
            if (best == null) b.Bests.Add(best = new SlotLaneBest { Member = car.Member, Lane = car.Lane, BestMs = long.MaxValue });
            if (lap.ActiveMs < best.BestMs) { best.BestMs = lap.ActiveMs; best.NormalizedMs = lap.NormalizedMs; }
            b.CoopProgress++;
            b.CoopContributions[car.Member] = (b.CoopContributions.TryGetValue(car.Member, out int n) ? n : 0) + 1;
            if (b.CoopProgress >= b.CoopTarget) { b.CoopCompleted++; b.CoopProgress = 0; }
        }

        /// <summary>Rolling session best on one lane (raw times are only compared within the same lane).</summary>
        public SlotLaneBest LaneBest(int lane) => Board.Bests.Where(b => b.Lane == lane).OrderBy(b => b.BestMs).FirstOrDefault();

        /// <summary>Cross-lane comparison uses the disclosed normalized time, shown next to the raw lap.</summary>
        public List<SlotLaneBest> NormalizedBoard() => Board.Bests.OrderBy(b => b.NormalizedMs).ThenBy(b => b.Member, StringComparer.Ordinal).ToList();

        // --------------------------------------------------------------------------------------------------------------

        public void Freeze(long nowMs)
        {
            Run.Frozen = true;
            foreach (SlotCarState c in Board.Cars)
                if (c.Mode != SlotCarMode.Parked) Park(c, LapFlags.Preempted);
                else if (c.Lap >= 1) c.Flags |= LapFlags.Preempted;
        }

        public void EndPause(long nowMs) { }

        /// <summary>An actual participant resumes the table: short orientation for the table and for their own car.</summary>
        public void Resume(string member, long nowMs)
        {
            if (Run.Frozen)
            {
                Run.Frozen = false;
                Run.OrientUntilMs = nowMs + ToyLimits.OrientationMs;
                State.SimTick = TickAt(nowMs);
            }
            SlotCarState car = Car(member);
            if (car != null && car.Mode == SlotCarMode.Parked)
            {
                car.Mode = SlotCarMode.Orienting;
                car.ModeUntilMs = nowMs + ToyLimits.OrientationMs;
                car.Throttle = 0;
            }
        }

        /// <summary>Closing the view, idling, focus loss or disconnect parks ONLY that member's car; other lanes continue.</summary>
        public void ReleaseControl(string member, ReleaseCause cause, long nowMs)
        {
            SlotCarState car = Car(member);
            if (car != null)
            {
                LapFlags reason = cause == ReleaseCause.Disconnected || cause == ReleaseCause.FocusLost ? LapFlags.Interrupted : LapFlags.VoluntaryPause;
                Park(car, reason);
            }
            ResolveProposal(nowMs);
        }

        /// <summary>Explicit departure removes that member's cars from every layout; their completed laps remain history.</summary>
        public void Retire(string member, long nowMs)
        {
            foreach (SlotLayoutBoard b in State.Boards.Values) b.Cars.RemoveAll(c => c.Member == member);
        }
    }
}
