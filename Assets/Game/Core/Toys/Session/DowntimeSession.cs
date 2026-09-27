using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NightSignal.Core.Toys.Canvas;
using NightSignal.Core.Toys.CapClash;
using NightSignal.Core.Toys.Greenlight;
using NightSignal.Core.Toys.PitCrew;
using NightSignal.Core.Toys.PocketCircuit;

namespace NightSignal.Core.Toys
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SessionLifecycle { Active = 0, Paused = 1, Dormant = 2, Retired = 3 }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum SeatStatus { Active = 0, Dormant = 1, Retired = 2 }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum DepartureReason { Left = 0, Kicked = 1, JoinedOtherConvoy = 2, Revoked = 3 }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum JoinOutcome { NewSeat = 0, RestoredDormant = 1, AlreadyActive = 2, RejectedStaleGeneration = 3, RejectedEnded = 4, RejectedFull = 5 }

    /// <summary>
    /// A member's downtime participation (Addendum 02 §11 'ActivityParticipation'): membership generation, active/dormant/
    /// retired status, the ONE toy they actively control, its control lease (renewed only by real interaction) and the
    /// per-member command sequence. Toy identity (cap, lane, contributions) is keyed by member id inside each toy.
    /// </summary>
    public sealed class MemberSeat
    {
        public string MemberId;
        public long Generation;
        public SeatStatus Status;
        /// <summary>The toy this member is actively controlling, if any.</summary>
        public ToyActivityId? Controlled;
        public long ControlUntilMs;
        /// <summary>Last accepted command sequence within this generation.</summary>
        public long LastSequence;
        public Dictionary<ToyActivityId, long> LastTouch = new Dictionary<ToyActivityId, long>();
        /// <summary>What the member is currently looking at (spectating is not controlling).</summary>
        public ToyActivityId? Viewing;
        public double RateTokens = ToyLimits.RateBurst;
        public long RateStampMs;
    }

    public sealed class RecentRequest
    {
        public string Key;
        public long Revision;
        public string Value;
    }

    public sealed class ToyJournalEntry
    {
        public long Revision;
        public long AtMs;
        public ToyCommand Command;
    }

    public struct PauseReceipt
    {
        /// <summary>Every command accepted at or before this revision is part of the frozen state; nothing after it.</summary>
        public long BoundaryRevision;
        public int PauseGeneration;
        public long AtMs;
    }

    /// <summary>
    /// The shared downtime state of one convoy session (Addendum 02 §1, §11 'DowntimeSession'). Owned by a stable
    /// ConvoySessionId, not by a leader, race or panel. Hosts (control plane authority, or the Local profile offline) call
    /// every method from a single thread per session. Time is a host clock in milliseconds; the session enforces
    /// monotonicity and keeps its own clock so snapshots survive process restarts.
    /// </summary>
    public sealed class DowntimeSession : IToyHost
    {
        public string SessionId { get; private set; }
        /// <summary>Session revision (version); incremented by every accepted command and lifecycle change.</summary>
        public long Revision { get; private set; }
        public long NowMs { get; private set; }
        public int PauseGeneration { get; private set; }
        public bool PausedForEvent { get; private set; }
        public bool Ended { get; private set; }
        public long DormantSinceMs { get; private set; }
        public long DormantExpiresMs { get; private set; }
        /// <summary>Revision of the last snapshot the host reported as durably stored.</summary>
        public long LastDurableRevision { get; private set; }
        public bool JournalOverflowed { get; private set; }

        public readonly ToyContent Content;
        public readonly CapClashTable CapClash;
        public readonly PitCrewWorkshop PitCrew;
        public readonly GreenlightStation Greenlight;
        public readonly PocketCircuitTable PocketCircuit;
        public readonly ConvoyCanvasBoard Canvas;

        readonly Dictionary<string, MemberSeat> seats = new Dictionary<string, MemberSeat>(StringComparer.Ordinal);
        readonly Dictionary<string, RecentRequest> recent = new Dictionary<string, RecentRequest>(StringComparer.Ordinal);
        readonly Queue<string> recentOrder = new Queue<string>();
        readonly List<ToyJournalEntry> journal = new List<ToyJournalEntry>();
        readonly IToyActivity[] activities;
        ToyRandom random;
        long idCounter;
        bool replaying;

        public DowntimeSession(string sessionId, ToyContent content, long nowMs, ulong seed)
            : this(sessionId, content, nowMs, seed, null) { }

        DowntimeSession(string sessionId, ToyContent content, long nowMs, ulong seed, DowntimeSnapshot snap)
        {
            if (!ToyCommandCodec.ValidId(sessionId)) throw new ArgumentException("invalid session id", nameof(sessionId));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SessionId = sessionId;
            NowMs = nowMs;
            random = new ToyRandom(seed);
            CapClash = new CapClashTable(this, content.CapClash, snap?.CapClash);
            PitCrew = new PitCrewWorkshop(this, content.PitCrew, snap?.PitCrew);
            Greenlight = new GreenlightStation(this, snap?.Greenlight);
            PocketCircuit = new PocketCircuitTable(this, content.PocketCircuit, snap?.PocketCircuit);
            Canvas = new ConvoyCanvasBoard(this, content.Stamps, snap == null ? null : CanvasCodec.Decode(snap.Canvas));
            activities = new IToyActivity[] { CapClash, PitCrew, Greenlight, PocketCircuit, Canvas };
        }

        public IToyActivity Activity(ToyActivityId id) => activities[(int)id];

        public SessionLifecycle Lifecycle =>
            Ended ? SessionLifecycle.Retired : DormantSinceMs > 0 ? SessionLifecycle.Dormant : PausedForEvent ? SessionLifecycle.Paused : SessionLifecycle.Active;

        public IReadOnlyList<ToyJournalEntry> Journal => journal;

        public MemberSeat Seat(string member) => member != null && seats.TryGetValue(member, out MemberSeat s) ? s : null;

        public IEnumerable<MemberSeat> Seats => seats.Values;

        // ----------------------------------------------------------------------------------------------------------------
        // IToyHost

        public List<string> ActiveUsers(ToyActivityId activity)
        {
            var list = new List<string>();
            foreach (MemberSeat s in seats.Values)
                if (s.Status == SeatStatus.Active && s.LastTouch.TryGetValue(activity, out long t) && NowMs - t <= ToyLimits.ActiveUserWindowMs)
                    list.Add(s.MemberId);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public bool IsActiveMember(string member) => seats.TryGetValue(member ?? "", out MemberSeat s) && s.Status == SeatStatus.Active;

        public ulong NextSeed() => random.NextUInt64();

        public string NextId(string prefix) => prefix + "-" + (++idCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ----------------------------------------------------------------------------------------------------------------
        // Membership (Addendum 02 §1.5)

        /// <summary>
        /// An AUTHORIZED member (the control plane has already checked the grant/invite and leadership epoch) joins or
        /// rejoins. A dormant seat is restored without spawning another piece. A newer membership generation never inherits
        /// the old generation's input rights: its sequence space restarts and it holds no control or leases.
        /// </summary>
        public JoinOutcome Join(string member, long generation, long nowMs)
        {
            Tick(nowMs);
            if (Ended) return JoinOutcome.RejectedEnded;
            if (!ToyCommandCodec.ValidId(member) || generation <= 0) return JoinOutcome.RejectedStaleGeneration;
            if (seats.TryGetValue(member, out MemberSeat seat))
            {
                if (generation < seat.Generation) return JoinOutcome.RejectedStaleGeneration;
                if (generation == seat.Generation && seat.Status == SeatStatus.Active) return JoinOutcome.AlreadyActive;
                if (generation == seat.Generation && seat.Status == SeatStatus.Retired) return JoinOutcome.RejectedStaleGeneration;
                if (ActiveSeatCount() >= ToyLimits.MaxMembers) return JoinOutcome.RejectedFull;
                bool wasDormant = seat.Status == SeatStatus.Dormant;
                if (generation > seat.Generation)
                {
                    seat.Generation = generation;
                    seat.LastSequence = 0;
                }
                seat.Status = SeatStatus.Active;
                seat.Controlled = null;
                seat.ControlUntilMs = 0;
                seat.RateTokens = ToyLimits.RateBurst;
                seat.RateStampMs = NowMs;
                OnBecameActive();
                Revision++;
                return wasDormant ? JoinOutcome.RestoredDormant : JoinOutcome.NewSeat;
            }
            if (ActiveSeatCount() >= ToyLimits.MaxMembers) return JoinOutcome.RejectedFull;
            PruneRetiredSeats();
            seats[member] = new MemberSeat { MemberId = member, Generation = generation, Status = SeatStatus.Active, RateStampMs = NowMs };
            OnBecameActive();
            Revision++;
            return JoinOutcome.NewSeat;
        }

        /// <summary>Network loss: seat becomes dormant, control and leases are released, records remain.</summary>
        public void Disconnect(string member, long nowMs)
        {
            Tick(nowMs);
            if (!seats.TryGetValue(member ?? "", out MemberSeat seat) || seat.Status != SeatStatus.Active) return;
            foreach (IToyActivity a in activities) a.ReleaseControl(member, ReleaseCause.Disconnected, NowMs);
            seat.Status = SeatStatus.Dormant;
            seat.Controlled = null;
            Revision++;
            if (ActiveSeatCount() == 0 && !Ended)
            {
                // D208: all members lost only to disconnection → dormant room for at most 24 h (server time).
                DormantSinceMs = NowMs;
                DormantExpiresMs = NowMs + ToyLimits.DormantGraceMs;
            }
        }

        /// <summary>Focus loss / minimise: cancel held inputs and park moving pieces without changing membership.</summary>
        public void FocusLost(string member, long nowMs)
        {
            Tick(nowMs);
            if (!IsActiveMember(member)) return;
            foreach (IToyActivity a in activities) a.ReleaseControl(member, ReleaseCause.FocusLost, NowMs);
            seats[member].Controlled = null;
            Revision++;
        }

        /// <summary>Closing a panel: releases that member's control of that toy (not a departure from the convoy).</summary>
        public void Close(string member, ToyActivityId activity, long nowMs)
        {
            Tick(nowMs);
            if (!IsActiveMember(member)) return;
            Activity(activity).ReleaseControl(member, ReleaseCause.Closed, NowMs);
            MemberSeat seat = seats[member];
            if (seat.Controlled == activity) seat.Controlled = null;
            Revision++;
        }

        /// <summary>
        /// Explicit Leave/Forget, kick, joining another convoy or revocation: retire the member's moving pieces and queued
        /// actions at this revision; keep incorporated artwork/project work. The last member leaving ends the session;
        /// if only dormant (disconnected) members remain, the dormant grace applies instead.
        /// </summary>
        public void Leave(string member, DepartureReason reason, long nowMs)
        {
            Tick(nowMs);
            if (!seats.TryGetValue(member ?? "", out MemberSeat seat) || seat.Status == SeatStatus.Retired) return;
            foreach (IToyActivity a in activities)
            {
                a.ReleaseControl(member, ReleaseCause.Departed, NowMs);
                a.Retire(member, NowMs);
            }
            seat.Status = SeatStatus.Retired;
            seat.Controlled = null;
            Revision++;
            if (ActiveSeatCount() == 0)
            {
                if (seats.Values.Any(s => s.Status == SeatStatus.Dormant))
                {
                    if (DormantSinceMs == 0) { DormantSinceMs = NowMs; DormantExpiresMs = NowMs + ToyLimits.DormantGraceMs; }
                }
                else End();
            }
        }

        /// <summary>Explicit disband by the leader: the session and its shared toys end.</summary>
        public void Disband(long nowMs)
        {
            Tick(nowMs);
            End();
        }

        /// <summary>True when the dormant grace has elapsed; the host then retires the stored data.</summary>
        public bool IsDormantExpired(long nowMs) => !Ended && DormantSinceMs > 0 && nowMs >= DormantExpiresMs;

        void End()
        {
            if (Ended) return;
            Ended = true;
            foreach (MemberSeat s in seats.Values) { s.Controlled = null; if (s.Status == SeatStatus.Active) s.Status = SeatStatus.Dormant; }
            Revision++;
        }

        void OnBecameActive()
        {
            DormantSinceMs = 0;
            DormantExpiresMs = 0;
        }

        int ActiveSeatCount()
        {
            int n = 0;
            foreach (MemberSeat s in seats.Values) if (s.Status == SeatStatus.Active) n++;
            return n;
        }

        void PruneRetiredSeats()
        {
            if (seats.Count < 32) return;
            string victim = seats.Values.Where(s => s.Status == SeatStatus.Retired).Select(s => s.MemberId).OrderBy(m => m, StringComparer.Ordinal).FirstOrDefault();
            if (victim != null) seats.Remove(victim);
        }

        // ----------------------------------------------------------------------------------------------------------------
        // Main-event preemption (Addendum 02 §1.3, §1.4)

        /// <summary>
        /// Suspends every toy at the current revision WITHOUT waiting for anything: sliding caps and toy cars keep position
        /// and velocity, lap timers stop accruing, open strokes finalize at their last accepted point, exposed reaction cues
        /// become neutral restarts and task leases are released with their accepted progress kept. Every activity epoch is
        /// bumped so in-flight commands from before the boundary can never apply afterwards.
        /// </summary>
        public PauseReceipt Pause(long nowMs)
        {
            Tick(nowMs);
            if (!PausedForEvent && !Ended)
            {
                PausedForEvent = true;
                PauseGeneration++;
                foreach (IToyActivity a in activities)
                {
                    a.Freeze(NowMs);
                    a.Run.Epoch++;
                    a.Run.FrozenInPause = PauseGeneration;
                }
                foreach (MemberSeat s in seats.Values) s.Controlled = null;
                Revision++;
            }
            return new PauseReceipt { BoundaryRevision = Revision, PauseGeneration = PauseGeneration, AtMs = NowMs };
        }

        /// <summary>
        /// The main event ended (or its load was aborted): boards, projects and records are available again. Moving toys
        /// stay frozen until an actual participant resumes them; nobody is forced back into their last toy.
        /// </summary>
        public void EndPause(long nowMs)
        {
            Tick(nowMs);
            if (!PausedForEvent) return;
            PausedForEvent = false;
            foreach (IToyActivity a in activities) a.EndPause(NowMs);
            Revision++;
        }

        /// <summary>Explicit resume of one toy instance by an actual participant (starts the orientation window).</summary>
        public ToyResult Resume(string member, ToyActivityId activity, long nowMs)
        {
            Tick(nowMs);
            if (Ended) return ToyResult.Reject(ToyReason.SessionEnded);
            if (PausedForEvent) return ToyResult.Defer(ToyReason.SessionPaused);
            if (!IsActiveMember(member)) return ToyResult.Reject(ToyReason.SeatNotActive);
            Activity(activity).Resume(member, NowMs);
            Touch(seats[member], activity);
            Revision++;
            return new ToyResult { Verdict = ToyVerdict.Accepted, Revision = Revision };
        }

        // ----------------------------------------------------------------------------------------------------------------
        // Commands

        /// <summary>Advances the session clock and every toy (fixed-step simulation, lease/proposal expiry).</summary>
        public void Advance(long nowMs) => Tick(nowMs);

        void Tick(long nowMs)
        {
            if (nowMs > NowMs) NowMs = nowMs;
            foreach (MemberSeat s in seats.Values)
                if (s.Status == SeatStatus.Active && s.Controlled.HasValue && NowMs >= s.ControlUntilMs)
                {
                    Activity(s.Controlled.Value).ReleaseControl(s.MemberId, ReleaseCause.Idle, NowMs);
                    s.Controlled = null;
                }
            foreach (IToyActivity a in activities) a.Advance(NowMs);
        }

        /// <summary>
        /// Validates and applies one command. Order of checks: session, idempotency (an already accepted request id returns
        /// its original result), membership + generation, per-member sequence, rate, pause (Deferred), activity epoch, then
        /// the toy's own payload validation.
        /// </summary>
        public ToyResult Submit(ToyCommand cmd, long nowMs)
        {
            Tick(nowMs);
            if (!ToyCommandCodec.Validate(cmd, out ToyReason envelope)) return ToyResult.Reject(envelope);
            if (cmd.SessionId != SessionId) return ToyResult.Reject(ToyReason.WrongSession);
            if (Ended) return ToyResult.Reject(ToyReason.SessionEnded);

            string key = cmd.MemberId + "/" + cmd.RequestId;
            if (recent.TryGetValue(key, out RecentRequest prior))
                return new ToyResult { Verdict = ToyVerdict.Accepted, Revision = prior.Revision, Value = prior.Value, Duplicate = true };

            if (!seats.TryGetValue(cmd.MemberId, out MemberSeat seat)) return ToyResult.Reject(ToyReason.NotMember);
            if (seat.Generation != cmd.Generation || seat.Status == SeatStatus.Retired) return ToyResult.Reject(ToyReason.StaleMembership);
            if (seat.Status != SeatStatus.Active) return ToyResult.Reject(ToyReason.SeatNotActive);
            if (cmd.Sequence <= seat.LastSequence) return ToyResult.Reject(ToyReason.OutOfOrder);
            if (cmd.Payload != null && cmd.Payload.ToString(Formatting.None).Length > ToyLimits.MaxCommandBytes) return ToyResult.Reject(ToyReason.TooLarge);
            if (!replaying && !TakeToken(seat)) return ToyResult.Reject(ToyReason.RateLimited);
            if (PausedForEvent) return ToyResult.Defer(ToyReason.SessionPaused, "paused for the convoy — progress kept");

            IToyActivity activity = Activity(cmd.Activity);
            if (cmd.Epoch != activity.Run.Epoch) return ToyResult.Reject(ToyReason.StaleEpoch);

            ToyResult result;
            if (cmd.Kind == "resume")
            {
                activity.Resume(cmd.MemberId, NowMs);
                result = ToyResult.Ok();
            }
            else if (cmd.Kind == "close")
            {
                activity.ReleaseControl(cmd.MemberId, ReleaseCause.Closed, NowMs);
                if (seat.Controlled == cmd.Activity) seat.Controlled = null;
                result = ToyResult.Ok();
            }
            else if (cmd.Kind == "view")
            {
                seat.Viewing = cmd.Activity;
                result = ToyResult.Ok();
            }
            else
            {
                var ctx = new ToyCommandContext
                {
                    Command = cmd, Member = cmd.MemberId, Generation = cmd.Generation, NowMs = NowMs, P = new PayloadReader(cmd.Payload), Host = this,
                };
                try
                {
                    result = activity.Apply(ctx);
                }
                catch (ToyPayloadException e)
                {
                    result = ToyResult.Reject(e.Reason, e.Message);
                }
            }
            if (!result.Accepted) return result;

            Revision++;
            result.Revision = Revision;
            seat.LastSequence = cmd.Sequence;
            Touch(seat, cmd.Activity);
            if (cmd.Kind != "close" && cmd.Kind != "view" && (cmd.Kind == "resume" || activity.TakesControl(cmd.Kind)))
            {
                if (seat.Controlled.HasValue && seat.Controlled.Value != cmd.Activity)
                    Activity(seat.Controlled.Value).ReleaseControl(seat.MemberId, ReleaseCause.Switched, NowMs);
                seat.Controlled = cmd.Activity;
                seat.ControlUntilMs = NowMs + ToyLimits.ControlLeaseMs;
            }
            Remember(key, result);
            if (!activity.IsTransient(cmd.Kind) && !replaying) AppendJournal(cmd);
            return result;
        }

        void Touch(MemberSeat seat, ToyActivityId activity)
        {
            seat.LastTouch[activity] = NowMs;
            seat.Viewing = activity;
        }

        bool TakeToken(MemberSeat seat)
        {
            long dt = NowMs - seat.RateStampMs;
            if (dt > 0)
            {
                seat.RateTokens = Math.Min(ToyLimits.RateBurst, seat.RateTokens + dt * ToyLimits.RatePerSecond / 1000.0);
                seat.RateStampMs = NowMs;
            }
            if (seat.RateTokens < 1) return false;
            seat.RateTokens -= 1;
            return true;
        }

        void Remember(string key, ToyResult result)
        {
            recent[key] = new RecentRequest { Key = key, Revision = result.Revision, Value = result.Value };
            recentOrder.Enqueue(key);
            while (recentOrder.Count > ToyLimits.IdempotencyWindow) recent.Remove(recentOrder.Dequeue());
        }

        void AppendJournal(ToyCommand cmd)
        {
            journal.Add(new ToyJournalEntry { Revision = Revision, AtMs = NowMs, Command = cmd.Clone() });
            if (journal.Count > ToyLimits.MaxJournalEntries)
            {
                journal.RemoveAt(0);
                JournalOverflowed = true;
            }
        }

        /// <summary>The host should store a new snapshot (journal nearly full or overflowed).</summary>
        public bool NeedsSnapshot => JournalOverflowed || journal.Count >= ToyLimits.MaxJournalEntries * 3 / 4;

        public List<ToyOverview> Overview()
        {
            var list = new List<ToyOverview>();
            foreach (IToyActivity a in activities)
                list.Add(new ToyOverview
                {
                    Activity = a.Id, Players = ActiveUsers(a.Id), Paused = PausedForEvent || a.Run.Frozen, Epoch = a.Run.Epoch,
                    Status = StatusOf(a.Id),
                });
            return list;
        }

        string StatusOf(ToyActivityId id)
        {
            switch (id)
            {
                case ToyActivityId.CapClash: return CapClash.ActiveArrangement.Name;
                case ToyActivityId.PitCrew: return PitCrew.StatusLine();
                case ToyActivityId.PocketCircuit: return PocketCircuit.ActiveLayout.Name;
                case ToyActivityId.Canvas: return Canvas.Document.Sheets.Count + " sheets";
                default: return "Greenlight";
            }
        }

        // ----------------------------------------------------------------------------------------------------------------
        // Snapshot / journal (Addendum 02 §1.3 durable acceptance, §1.5 compact persisted state)

        public DowntimeSnapshot Snapshot()
        {
            var snap = new DowntimeSnapshot
            {
                SessionId = SessionId, Revision = Revision, ClockMs = NowMs, PauseGeneration = PauseGeneration, PausedForEvent = PausedForEvent,
                Ended = Ended, DormantSinceMs = DormantSinceMs, DormantExpiresMs = DormantExpiresMs, LastDurableRevision = LastDurableRevision,
                RandomState = unchecked((long)random.State), IdCounter = idCounter, ContentHash = Content.ContentHash,
                Seats = seats.Values.OrderBy(s => s.MemberId, StringComparer.Ordinal).ToList(),
                RecentRequests = recentOrder.Skip(Math.Max(0, recentOrder.Count - ToyLimits.SnapshotIdempotencyIds)).Where(recent.ContainsKey).Select(k => recent[k]).ToList(),
                CapClash = CapClash.State, PitCrew = PitCrew.State, Greenlight = Greenlight.State, PocketCircuit = PocketCircuit.State,
                Canvas = CanvasCodec.Encode(Canvas.Document),
            };
            return snap;
        }

        /// <summary>Serializes a snapshot immediately (<see cref="Snapshot"/> returns views over live state).</summary>
        public string SnapshotJson() => DowntimeCodec.Serialize(Snapshot());

        /// <summary>The host confirms a snapshot at <paramref name="revision"/> is durably stored; the journal before it is dropped.</summary>
        public void MarkDurable(long revision, long nowMs = 0)
        {
            if (revision > LastDurableRevision) LastDurableRevision = revision;
            if (nowMs > LastDurableAtMs) LastDurableAtMs = nowMs;
            journal.RemoveAll(e => e.Revision <= revision);
            JournalOverflowed = false;
        }

        /// <summary>Host clock time of the last durable snapshot (0 = never).</summary>
        public long LastDurableAtMs { get; private set; }

        /// <summary>
        /// True when accepted changes are not yet in a durable snapshot and either the journal is filling up or
        /// <see cref="ToyLimits.SnapshotIntervalMs"/> has passed. Hosts persist off the critical race-start path.
        /// </summary>
        public bool SnapshotDue(long nowMs) =>
            Revision > LastDurableRevision && (NeedsSnapshot || nowMs - LastDurableAtMs >= ToyLimits.SnapshotIntervalMs);

        /// <summary>
        /// Set after <see cref="Restore"/> when the snapshot was written against different toy content: the host should
        /// surface an honest "some toy state may not match" notice rather than claim complete preservation.
        /// </summary>
        public bool RestoredAgainstDifferentContent { get; private set; }

        /// <summary>
        /// Rebuilds a session from a stored snapshot (and optionally replays the journal recorded after it). Transient
        /// runtime state (open aiming drafts, held throttle) is not restored; held throttle is always released.
        /// </summary>
        public static DowntimeSession Restore(DowntimeSnapshot snap, ToyContent content, long hostNowMs, IEnumerable<ToyJournalEntry> journalAfter = null)
        {
            if (snap == null) throw new ArgumentNullException(nameof(snap));
            if (snap.Schema != ToyLimits.SchemaVersion) throw new InvalidOperationException("unsupported downtime snapshot schema " + snap.Schema);
            var s = new DowntimeSession(snap.SessionId, content, snap.ClockMs, 1, snap);
            s.random.State = unchecked((ulong)snap.RandomState);
            s.Revision = snap.Revision;
            s.PauseGeneration = snap.PauseGeneration;
            s.PausedForEvent = snap.PausedForEvent;
            s.Ended = snap.Ended;
            s.DormantSinceMs = snap.DormantSinceMs;
            s.DormantExpiresMs = snap.DormantExpiresMs;
            s.LastDurableRevision = snap.LastDurableRevision;
            s.idCounter = snap.IdCounter;
            s.RestoredAgainstDifferentContent = snap.ContentHash != content.ContentHash;
            foreach (MemberSeat seat in snap.Seats ?? new List<MemberSeat>()) s.seats[seat.MemberId] = seat;
            foreach (RecentRequest r in snap.RecentRequests ?? new List<RecentRequest>())
            {
                s.recent[r.Key] = r;
                s.recentOrder.Enqueue(r.Key);
            }
            if (journalAfter != null) s.Replay(journalAfter);
            s.RecoverAfterRestore(hostNowMs);
            return s;
        }

        /// <summary>
        /// A restored room behaves like a preempted one: anything still running is frozen at the restored state (no time
        /// spent offline accrues to laps or slides), open strokes finalize, and every running toy's epoch moves on so
        /// commands sent to the previous process can never apply. Moving toys wait for an explicit resume.
        /// </summary>
        void RecoverAfterRestore(long hostNowMs)
        {
            bool changed = false;
            foreach (IToyActivity a in activities)
            {
                if (a.Run.Frozen) continue;
                a.Freeze(NowMs);
                a.Run.Epoch++;
                changed = true;
            }
            if (hostNowMs > NowMs) NowMs = hostNowMs;
            if (!PausedForEvent && changed)
                foreach (IToyActivity a in activities) a.EndPause(NowMs);
            foreach (MemberSeat seat in seats.Values) seat.Controlled = null;
        }

        /// <summary>Re-applies journaled commands recorded after the snapshot (already validated when first accepted).</summary>
        public void Replay(IEnumerable<ToyJournalEntry> entries)
        {
            replaying = true;
            try
            {
                foreach (ToyJournalEntry e in entries.OrderBy(e => e.Revision))
                {
                    if (e.Revision <= Revision) continue;
                    Submit(e.Command, e.AtMs);
                    journal.Add(new ToyJournalEntry { Revision = Revision, AtMs = e.AtMs, Command = e.Command.Clone() });
                }
            }
            finally { replaying = false; }
        }

        /// <summary>Helper for hosts/tests: a command envelope for this session with the member's current generation.</summary>
        public ToyCommand Command(string member, ToyActivityId activity, string kind, Newtonsoft.Json.Linq.JObject payload, long sequence, string requestId)
        {
            MemberSeat seat = Seat(member);
            return new ToyCommand
            {
                SessionId = SessionId, Activity = activity, Epoch = Activity(activity).Run.Epoch, MemberId = member,
                Generation = seat?.Generation ?? 1, Sequence = sequence, RequestId = requestId, Kind = kind, Payload = payload,
            };
        }
    }
}
