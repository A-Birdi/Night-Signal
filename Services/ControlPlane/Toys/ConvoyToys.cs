using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NightSignal.ControlPlane.Convoys;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.Canvas;
using NJ = Newtonsoft.Json;

namespace NightSignal.ControlPlane.Toys;

/// <summary>
/// Hosts the five 'While We Wait' diversions (Addendum 02 §1, §11) for every convoy session: one Core
/// <see cref="DowntimeSession"/> per stable ConvoySessionId, created lazily on first use.
///
/// Locking: this class has NO lock of its own. Every member is called under the <see cref="ConvoyDirectory"/> lock —
/// the <see cref="IConvoySessionObserver"/> callbacks by the directory itself, everything else through
/// <see cref="ConvoyDirectory.Exclusive{T}"/> (see <see cref="ToyService"/> and <see cref="ToySnapshotPersistence"/>). That
/// one lock serializes each session exactly as Core requires, and there is no second lock to order against it. Nothing here
/// blocks: durable writes are queued (<see cref="PersistRequests"/>) and pushes are returned to the caller to send.
///
/// Membership comes from the directory's observer events with its membership generation (join/rejoin → Join, disconnect →
/// Disconnect, leave/kick/disband → Leave). Preemption: a committed race allocation pauses the session until the event
/// finishes, is aborted or its start fails; a committed mode entry is an instant boundary (pause, then end the pause).
/// A Dormant room (D208) keeps only a compact snapshot (no live session, no simulation) until it is restored or retired.
/// </summary>
public sealed class ConvoyToys : IConvoySessionObserver
{
    static readonly NJ.JsonSerializerSettings StateJson = new()
    {
        Formatting = NJ.Formatting.None,
        NullValueHandling = NJ.NullValueHandling.Ignore,
        MaxDepth = 32,
        FloatParseHandling = NJ.FloatParseHandling.Double,
        DateParseHandling = NJ.DateParseHandling.None,
    };

    static readonly ToyActivityId[] Activities = Enum.GetValues<ToyActivityId>();

    readonly ToyContent? content;
    readonly TimeProvider clock;
    readonly ILogger log;
    readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);            // convoy session → toys
    readonly Dictionary<string, string> accountRooms = new(StringComparer.Ordinal);   // ACTIVE member → convoy session
    readonly Channel<ToyPersistRequest> persist =
        Channel.CreateUnbounded<ToyPersistRequest>(new UnboundedChannelOptions { SingleReader = true });

    public ConvoyToys(ToyContentProvider content, TimeProvider clock, ILogger<ConvoyToys>? log = null)
    {
        this.content = content.Content;
        Unavailable = content.Error ?? (content.Content is null ? "toy content is not loaded" : null);
        this.clock = clock;
        this.log = log ?? (ILogger)NullLogger.Instance;
    }

    sealed class Room
    {
        public required string SessionId;
        /// <summary>Active members of the convoy session and their membership generation (mirrors the directory).</summary>
        public readonly Dictionary<string, long> Members = new(StringComparer.Ordinal);
        public DowntimeSession? Session;
        /// <summary>Compact Dormant form (D208): the live session is released while every member is away.</summary>
        public string? DormantJson;
        public long DormantRevision;
        public DateTimeOffset? DormantExpiresAt;
        /// <summary>A committed convoy event holds the toys (between allocation and its end, abort or failed start).</summary>
        public bool EventHold;
        public string? HoldCause;
        public bool Dirty = true;
        public bool PersistQueued;
        public long PersistRetryAtMs;
        public long BroadcastRevision = -1;
        public long CheckedAtMs;
        public string? LastOverview;
        public readonly bool[] Simulating = new bool[Activities.Length];
        public readonly string?[] LastState = new string?[Activities.Length];
        public readonly long[] Versions = new long[Activities.Length];
        public string? SaveWarning;
        public string? RestoreWarning;
        public string? FaultWarning;
    }

    /// <summary>Why the toys are unavailable on this server (content missing/invalid), or null.</summary>
    public string? Unavailable { get; }
    public bool Available => content is not null;

    /// <summary>Durable-write requests for <see cref="ToySnapshotPersistence"/> (deduplicated per room while queued).</summary>
    public ChannelReader<ToyPersistRequest> PersistRequests => persist.Reader;

    long NowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();

    // ================================================================== convoy-session events (directory lock held)

    public void MembershipStarted(string sessionId, string accountId, long generation) =>
        Guarded("membership start", sessionId, () => Started(sessionId, accountId, generation));

    public void MembershipEnded(string sessionId, string accountId, long generation, string reason) =>
        Guarded("membership end", sessionId, () => EndedMembership(sessionId, accountId, generation, reason));

    public void Preempted(string sessionId, long convoyRevision, string cause) => Guarded("pause", sessionId, () => Pause(sessionId, cause));

    public void PreemptionEnded(string sessionId, long convoyRevision, string cause) => Guarded("end of pause", sessionId, () => EndPause(sessionId));

    public void Dormant(DormantRoomSnapshot snapshot) => Guarded("dormancy", snapshot.SessionId, () => GoDormant(snapshot));

    public void Restored(string sessionId) => Guarded("restore", sessionId, () =>
    {
        if (rooms.TryGetValue(sessionId, out Room? room) && room.DormantJson is not null && content is not null) Materialize(room);
    });

    public void Ended(string sessionId, string reason) => Guarded("end", sessionId, () => Retire(sessionId));

    /// <summary>
    /// The toys are an optional activity service: a fault in toy code must never break the convoy operation that notified
    /// us (a join, a leave, a race start). It is logged once per room and shown as an honest warning instead.
    /// </summary>
    void Guarded(string what, string sessionId, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            Fault(sessionId, what, e);
        }
    }

    void Fault(string sessionId, string what, Exception e)
    {
        Room? room = rooms.GetValueOrDefault(sessionId);
        if (room?.FaultWarning is null) log.LogError(e, "Toy host fault during {What} for session {SessionId}", what, sessionId);
        if (room is null) return;
        room.FaultWarning = "A toy ran into an error; its board may not show the latest action.";
        room.Dirty = true;
    }

    void Started(string sessionId, string accountId, long generation)
    {
        Room room = RoomFor(sessionId);
        room.Members[accountId] = generation;
        accountRooms[accountId] = sessionId;
        if (room.Session is { } s)
        {
            JoinOutcome outcome = s.Join(accountId, generation, NowMs);
            if (outcome is JoinOutcome.RejectedEnded or JoinOutcome.RejectedFull or JoinOutcome.RejectedStaleGeneration)
                log.LogWarning("Toy seat for a convoy member was refused ({Outcome}) in session {SessionId}", outcome, sessionId);
            room.Dirty = true;
        }
    }

    void EndedMembership(string sessionId, string accountId, long generation, string reason)
    {
        if (accountRooms.TryGetValue(accountId, out string? current) && current == sessionId) accountRooms.Remove(accountId);
        if (!rooms.TryGetValue(sessionId, out Room? room)) return;
        if (room.Members.TryGetValue(accountId, out long g) && g == generation) room.Members.Remove(accountId);
        if (room.Session is not { } s) return;
        // Temporary network loss keeps a dormant seat (records, cap, lane); an explicit departure retires the member's
        // moving piece and queued actions but keeps incorporated artwork/project work (Core DowntimeSession).
        if (reason == "disconnected") s.Disconnect(accountId, NowMs);
        else s.Leave(accountId, reason == "kicked" ? DepartureReason.Kicked : DepartureReason.Left, NowMs);
        room.Dirty = true;
    }

    void Pause(string sessionId, string cause)
    {
        Room room = RoomFor(sessionId);
        // A committed mode entry completes at once (no load): it is a boundary, not a hold. A race allocation holds the
        // toys until the event is over. Neither waits for anything: Pause returns immediately.
        bool instant = cause == "mode-entered";
        if (!instant)
        {
            room.EventHold = true;
            room.HoldCause = cause;
        }
        if (room.Session is not { Ended: false } s) return;
        s.Pause(NowMs);
        if (instant) s.EndPause(NowMs);
        room.Dirty = true;
        RequestPersist(room); // after the boundary, in the background — never on the start path
    }

    void EndPause(string sessionId)
    {
        if (!rooms.TryGetValue(sessionId, out Room? room)) return;
        room.EventHold = false;
        room.HoldCause = null;
        if (room.Session is not { } s) return; // a Dormant snapshot is reconciled when it is next materialized
        s.EndPause(NowMs);
        room.Dirty = true;
        RequestPersist(room);
    }

    void GoDormant(DormantRoomSnapshot snapshot)
    {
        if (!rooms.TryGetValue(snapshot.SessionId, out Room? room)) return;
        room.DormantExpiresAt = snapshot.ExpiresAt;
        if (room.Session is { } s)
        {
            try
            {
                room.DormantJson = s.SnapshotJson();
                room.DormantRevision = s.Revision;
                room.Session = null; // D208: nothing keeps running for a dormant room, only the compact snapshot remains
                room.LastState.AsSpan().Clear();
                room.Simulating.AsSpan().Clear();
                room.LastOverview = null;
            }
            catch (InvalidOperationException e)
            {
                // Over its size budget: keep the live state in memory rather than lose it, and say so.
                room.SaveWarning = "Toy state could not be saved compactly; it is kept only while this server runs.";
                log.LogError(e, "Dormant toy snapshot failed for session {SessionId}", snapshot.SessionId);
                return;
            }
        }
        if (room.DormantJson is not null) RequestPersist(room);
    }

    void Retire(string sessionId)
    {
        if (rooms.Remove(sessionId, out Room? room))
        {
            room.Session?.Disband(NowMs);
            foreach (string account in room.Members.Keys)
                if (accountRooms.TryGetValue(account, out string? current) && current == sessionId) accountRooms.Remove(account);
        }
        persist.Writer.TryWrite(new ToyPersistRequest(sessionId, Delete: true)); // retire the stored snapshot too
    }

    // ================================================================== requests (directory lock held)

    /// <summary>
    /// Applies one already-parsed command for <paramref name="accountId"/>. The envelope must name the account's CURRENT
    /// convoy session: a command for a previous convoy never reaches that convoy's toys (B10). Generation, sequence, rate,
    /// pause and epoch are then checked by Core.
    /// </summary>
    public ToyResult Submit(string accountId, ToyCommand cmd)
    {
        if (content is null) return ToyResult.Reject(ToyReason.InvalidState, Unavailable);
        if (!accountRooms.TryGetValue(accountId, out string? sessionId) || !rooms.TryGetValue(sessionId, out Room? room))
            return ToyResult.Reject(ToyReason.NotMember, "you are not an active member of a convoy");
        if (cmd.SessionId != sessionId) return ToyResult.Reject(ToyReason.WrongSession, "that toy belongs to another convoy session");
        try
        {
            DowntimeSession s = Materialize(room);
            long before = s.Revision;
            long now = NowMs;
            ToyResult result = s.Submit(cmd, now);
            if (s.Revision != before) room.Dirty = true;
            if (!room.PersistQueued && now >= room.PersistRetryAtMs && s.SnapshotDue(now)) RequestPersist(room);
            return result;
        }
        catch (Exception e)
        {
            Fault(sessionId, "command " + cmd.Activity + "/" + cmd.Kind, e);
            return ToyResult.Reject(ToyReason.InvalidState, "the toy could not apply this command");
        }
    }

    /// <summary>
    /// The requester's view of their convoy's toys: every toy (Core <see cref="DowntimeSnapshot"/> JSON without the
    /// server-only seed stream, rate buckets and other members' request ids), or one toy's state.
    /// </summary>
    public (ToyResult Result, object? Value) ClientSnapshot(string accountId, ToyActivityId? activity)
    {
        if (content is null) return (ToyResult.Reject(ToyReason.InvalidState, Unavailable), null);
        if (!accountRooms.TryGetValue(accountId, out string? sessionId) || !rooms.TryGetValue(sessionId, out Room? room))
            return (ToyResult.Reject(ToyReason.NotMember, "you are not an active member of a convoy"), null);
        try
        {
            return ClientSnapshot(room, activity);
        }
        catch (Exception e)
        {
            Fault(sessionId, "snapshot", e);
            return (ToyResult.Reject(ToyReason.InvalidState, "the toys could not be read just now"), null);
        }
    }

    (ToyResult Result, object? Value) ClientSnapshot(Room room, ToyActivityId? activity)
    {
        DowntimeSession s = Materialize(room);
        s.Advance(NowMs);
        if (activity is { } id)
        {
            (object? state, string? encoded, int bytes) = StateOf(s, id);
            IToyActivity a = s.Activity(id);
            return (ToyResult.Ok(), new
            {
                convoySessionId = s.SessionId, activity = id.ToString(), revision = s.Revision, version = room.Versions[(int)id],
                epoch = a.Run.Epoch, frozen = a.Run.Frozen, bytes, state, encoded,
            });
        }
        DowntimeSnapshot snap = s.Snapshot(); // a new object whose fields view live state: only replace fields, never mutate them
        snap.RandomState = 0;
        snap.RecentRequests = null;
        snap.Seats = snap.Seats.Select(m => new MemberSeat
        {
            MemberId = m.MemberId, Generation = m.Generation, Status = m.Status, Controlled = m.Controlled, ControlUntilMs = m.ControlUntilMs,
            LastSequence = m.LastSequence, LastTouch = new Dictionary<ToyActivityId, long>(m.LastTouch), Viewing = m.Viewing,
            RateTokens = 0, RateStampMs = 0,
        }).ToList();
        string json;
        try
        {
            json = DowntimeCodec.Serialize(snap);
        }
        catch (InvalidOperationException e)
        {
            return (ToyResult.Reject(ToyReason.TooLarge, e.Message), null);
        }
        return (ToyResult.Ok(), new
        {
            convoySessionId = s.SessionId,
            revision = s.Revision,
            lifecycle = s.Lifecycle.ToString(),
            pausedForEvent = s.PausedForEvent,
            versions = Activities.ToDictionary(a => a.ToString(), a => room.Versions[(int)a]),
            saveWarning = room.SaveWarning,
            restoreWarning = room.RestoreWarning,
            faultWarning = room.FaultWarning,
            snapshot = new RawJson(json),
        });
    }

    // ================================================================== broadcasting (directory lock held)

    /// <summary>
    /// One broadcast tick: advances every live session (sliding caps, slot cars, lease/proposal expiry), requests durable
    /// snapshots that are due, and returns the coalesced pushes for active members — one <c>toy.state</c> overview plus one
    /// <c>toy.activity</c> per toy whose state changed since the previous tick (size-bounded; larger states are announced
    /// as omitted so the client fetches them with <c>toy.snapshot</c>).
    /// </summary>
    public List<ToyOutgoing> CollectBroadcasts()
    {
        var outgoing = new List<ToyOutgoing>();
        if (content is null) return outgoing;
        long now = NowMs;
        foreach (Room room in rooms.Values)
        {
            try
            {
                Collect(room, now, outgoing);
            }
            catch (Exception e)
            {
                Fault(room.SessionId, "broadcast", e); // one faulty room never stops the others' pushes
            }
        }
        return outgoing;
    }

    void Collect(Room room, long now, List<ToyOutgoing> outgoing)
    {
        if (room.Session is not { } s) return;
        s.Advance(now);
        if (!room.PersistQueued && now >= room.PersistRetryAtMs && s.SnapshotDue(now)) RequestPersist(room);
        if (room.Members.Count == 0) return;

        // Look at a toy when something was accepted, on the periodic re-check, while it moves, and once after it stopped.
        bool full = room.Dirty || s.Revision != room.BroadcastRevision || now - room.CheckedAtMs >= ToyHostLimits.RecheckMs;
        bool anyMoving = false;
        var moving = new bool[Activities.Length];
        foreach (ToyActivityId id in Activities)
            anyMoving |= moving[(int)id] = !s.PausedForEvent && s.Activity(id).NeedsSimulation;
        if (!full && !anyMoving && !room.Simulating.Contains(true)) return;
        if (full) room.CheckedAtMs = now;

        var pushes = new List<(string Key, RawJson Payload)>();
        int budget = ToyHostLimits.MaxPushBytesPerTick;
        foreach (ToyActivityId id in Activities)
        {
            int i = (int)id;
            bool look = full || moving[i] || room.Simulating[i];
            room.Simulating[i] = moving[i];
            if (!look) continue;
            // The Canvas is only re-encoded when its cheap change key moved; the other toys' JSON is its own fingerprint.
            string fingerprint = id == ToyActivityId.Canvas ? CanvasKey(s.Canvas.Document) : "";
            if (id == ToyActivityId.Canvas && fingerprint == room.LastState[i]) continue;
            (RawJson? state, string? encoded, int bytes) = StateOf(s, id);
            if (id != ToyActivityId.Canvas) fingerprint = state!.Json;
            if (fingerprint == room.LastState[i]) continue;
            room.LastState[i] = fingerprint;
            room.Versions[i]++;
            bool omitted = bytes > ToyHostLimits.MaxActivityPushBytes || bytes > budget;
            if (!omitted) budget -= bytes;
            IToyActivity a = s.Activity(id);
            pushes.Add(("toy.activity/" + id, Serialize(new
            {
                convoySessionId = s.SessionId, activity = id.ToString(), revision = s.Revision, version = room.Versions[i],
                epoch = a.Run.Epoch, frozen = a.Run.Frozen, bytes, omitted,
                state = omitted ? null : state, encoded = omitted ? null : encoded,
            })));
        }
        RawJson overview = Serialize(Overview(room, s));
        room.Dirty = false;
        room.BroadcastRevision = s.Revision;
        if (pushes.Count == 0 && overview.Json == room.LastOverview) return;
        room.LastOverview = overview.Json;
        foreach (string account in room.Members.Keys)
        {
            outgoing.Add(new ToyOutgoing(account, "toy.state", "toy.state", overview));
            foreach ((string key, RawJson payload) in pushes) outgoing.Add(new ToyOutgoing(account, key, "toy.activity", payload));
        }
    }

    /// <summary>Serialized once per convoy and tick, then shared by every recipient's connection.</summary>
    static RawJson Serialize(object payload) => new(System.Text.Json.JsonSerializer.Serialize(payload, WireJson));

    static readonly System.Text.Json.JsonSerializerOptions WireJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>A toy's shareable state: JSON for four toys; the Canvas as Core's compact deflated document (base64).</summary>
    static (RawJson? State, string? Encoded, int Bytes) StateOf(DowntimeSession s, ToyActivityId id)
    {
        if (id == ToyActivityId.Canvas)
        {
            try
            {
                string encoded = CanvasCodec.Encode(s.Canvas.Document);
                return (null, encoded, encoded.Length);
            }
            catch (InvalidOperationException)
            {
                return (null, null, int.MaxValue); // over the Canvas budget: announced as omitted, never truncated
            }
        }
        string json = NJ.JsonConvert.SerializeObject(s.Activity(id).StateObject, StateJson);
        return (new RawJson(json), null, Encoding.UTF8.GetByteCount(json));
    }

    /// <summary>Changes whenever anything visible in the Canvas document can have changed (revision, pause/resume, open
    /// strokes finalized, leases expired, proposals resolved) — without encoding the document.</summary>
    static string CanvasKey(CanvasDocument d)
    {
        int objects = 0, open = 0, leased = 0;
        foreach (CanvasSheet sheet in d.Sheets)
            foreach (CanvasObject o in sheet.Objects)
            {
                objects++;
                if (o.Open) open++;
                if (o.LeaseHolder is not null) leased++;
            }
        static string P(ConsentProposal? p) => p is null ? "-" : $"{p.Id}:{p.State}:{p.Required.Count}:{p.Approved.Count}:{p.Declined.Count}";
        return string.Join('|', d.Revision, d.Run.Epoch, d.Run.Frozen, d.Run.OrientUntilMs, d.Sheets.Count, d.Checkpoints.Count, d.Histories.Count,
            objects, open, leased, P(d.Proposal), P(d.LastProposal));
    }

    object Overview(Room room, DowntimeSession s) => new
    {
        convoySessionId = s.SessionId,
        revision = s.Revision,
        lifecycle = s.Lifecycle.ToString(),
        pausedForEvent = s.PausedForEvent,
        pauseGeneration = s.PauseGeneration,
        pauseCause = s.PausedForEvent ? room.HoldCause : null,
        notice = s.PausedForEvent ? ToyHostLimits.PausedNotice : null,
        lastDurableRevision = s.LastDurableRevision,
        saveWarning = room.SaveWarning,
        restoreWarning = room.RestoreWarning,
        faultWarning = room.FaultWarning,
        toys = s.Overview().Select(o => new
        {
            activity = o.Activity.ToString(), players = o.Players, paused = o.Paused, epoch = o.Epoch, status = o.Status,
            version = room.Versions[(int)o.Activity],
        }).ToList(),
        seats = s.Seats.OrderBy(m => m.MemberId, StringComparer.Ordinal).Select(m => new
        {
            memberId = m.MemberId, generation = m.Generation, status = m.Status.ToString(), controlled = m.Controlled?.ToString(),
            viewing = m.Viewing?.ToString(),
        }).ToList(),
    };

    // ================================================================== persistence (directory lock held)

    /// <summary>Captures what to write for a queued request: the live session now, or the Dormant snapshot with its expiry.</summary>
    public ToySnapshotRecord? Capture(string sessionId)
    {
        if (!rooms.TryGetValue(sessionId, out Room? room)) return null;
        room.PersistQueued = false;
        if (room.Session is { } s) return new ToySnapshotRecord(sessionId, s.Revision, s.SnapshotJson(), null); // throws when over budget
        return room.DormantJson is { } json ? new ToySnapshotRecord(sessionId, room.DormantRevision, json, room.DormantExpiresAt) : null;
    }

    /// <summary>The snapshot at <paramref name="revision"/> is durably stored: Core drops the journal before it.</summary>
    public void Durable(string sessionId, long revision)
    {
        if (!rooms.TryGetValue(sessionId, out Room? room)) return;
        room.PersistRetryAtMs = 0;
        if (room.SaveWarning is not null) room.Dirty = true;
        room.SaveWarning = null;
        if (room.Session is { } s)
        {
            s.MarkDurable(revision, NowMs);
            room.Dirty = true;
        }
    }

    /// <summary>A durable write failed: the last verified snapshot stays authoritative, members see an honest warning.</summary>
    public void SaveFailed(string sessionId)
    {
        if (!rooms.TryGetValue(sessionId, out Room? room)) return;
        long durable = room.Session?.LastDurableRevision ?? 0;
        room.SaveWarning = durable > 0
            ? $"Toy progress could not be saved just now; the last saved state is revision {durable}. Play continues."
            : "Toy progress could not be saved just now. Play continues.";
        room.PersistRetryAtMs = NowMs + ToyHostLimits.PersistRetryMs;
        room.Dirty = true;
    }

    /// <summary>
    /// Startup recovery: stored snapshots whose convoy the directory restored (a Dormant room) are adopted in compact form;
    /// all others are orphans to delete — toy data never fabricates a live convoy. Returns (adopted, orphan session ids).
    /// </summary>
    public (int Adopted, IReadOnlyList<string> Orphans) Adopt(IEnumerable<ToySnapshotRecord> stored, IReadOnlyDictionary<string, DateTimeOffset?> sessions)
    {
        int adopted = 0;
        var orphans = new List<string>();
        foreach (ToySnapshotRecord r in stored)
        {
            if (!sessions.TryGetValue(r.SessionId, out DateTimeOffset? expiry))
            {
                orphans.Add(r.SessionId); // its convoy was not recovered: never fabricate one from toy data
                continue;
            }
            Room room = RoomFor(r.SessionId);
            if (room.Session is not null || room.DormantJson is not null) continue; // newer state is already in memory
            room.DormantJson = r.Json;
            room.DormantRevision = r.Revision;
            room.DormantExpiresAt = expiry ?? r.DormantExpiresAt;
            adopted++;
        }
        return (adopted, orphans);
    }

    /// <summary>Saved toy state exists but could not be read: say so instead of silently presenting empty toys.</summary>
    public void MarkRestoreUnavailable(IEnumerable<string> sessionIds)
    {
        foreach (string id in sessionIds)
            RoomFor(id).RestoreWarning = "Saved toy state could not be loaded after a server restart; the toys may start fresh.";
    }

    /// <summary>Queues a final snapshot of every live session (graceful shutdown).</summary>
    public int RequestFinalSnapshots()
    {
        int n = 0;
        foreach (Room room in rooms.Values.Where(r => r.Session is not null))
        {
            RequestPersist(room);
            n++;
        }
        return n;
    }

    public void CompletePersistQueue() => persist.Writer.TryComplete();

    // ================================================================== inspection (tests, diagnostics; directory lock held)

    public DowntimeSession? LiveSession(string sessionId) => rooms.TryGetValue(sessionId, out Room? r) ? r.Session : null;
    public bool HasDormantSnapshot(string sessionId) => rooms.TryGetValue(sessionId, out Room? r) && r.DormantJson is not null;
    public bool IsHeld(string sessionId) => rooms.TryGetValue(sessionId, out Room? r) && r.EventHold;

    // ================================================================== helpers

    Room RoomFor(string sessionId)
    {
        if (!rooms.TryGetValue(sessionId, out Room? room)) rooms[sessionId] = room = new Room { SessionId = sessionId };
        return room;
    }

    void RequestPersist(Room room)
    {
        if (room.PersistQueued) return;
        if (persist.Writer.TryWrite(new ToyPersistRequest(room.SessionId, Delete: false))) room.PersistQueued = true;
    }

    /// <summary>
    /// The live session for a room: restored from its compact snapshot, or created fresh on first use. Current members are
    /// seated with their generations (a dormant seat is restored, never duplicated) and the pause state follows the convoy.
    /// </summary>
    DowntimeSession Materialize(Room room)
    {
        if (room.Session is { } live) return live;
        long now = NowMs;
        DowntimeSession? s = null;
        if (room.DormantJson is { } json)
        {
            try
            {
                s = DowntimeSession.Restore(DowntimeCodec.Deserialize(json), content!, now);
                if (s.SessionId != room.SessionId) throw new InvalidOperationException("snapshot belongs to another convoy session");
                if (s.RestoredAgainstDifferentContent)
                    room.RestoreWarning = "The toys were saved with different toy content; some toy state may not match.";
            }
            catch (Exception e)
            {
                s = null;
                room.RestoreWarning = "Saved toy state could not be restored; the toys start fresh.";
                log.LogError(e, "Toy snapshot restore failed for session {SessionId}", room.SessionId);
            }
            room.DormantJson = null;
            room.DormantExpiresAt = null;
        }
        s ??= new DowntimeSession(room.SessionId, content!, now, BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)));
        room.Session = s;
        foreach ((string account, long generation) in room.Members) s.Join(account, generation, now);
        if (room.EventHold && !s.PausedForEvent) s.Pause(now);
        else if (!room.EventHold && s.PausedForEvent) s.EndPause(now);
        room.Dirty = true;
        return s;
    }
}
