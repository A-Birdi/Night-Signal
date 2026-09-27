using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Security;
using NightSignal.ControlPlane.Toys;
using NightSignal.Core.Rules;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.Canvas;
using NightSignal.Core.Toys.CapClash;
using NightSignal.Core.Toys.PocketCircuit;
using NightSignal.Services.Tests.Infrastructure;
using static NightSignal.Core.Toys.ToyActivityId;

namespace NightSignal.Services.Tests;

/// <summary>Records the low-priority toy pushes (serialized exactly as the control channel would send them).</summary>
public sealed class RecordingToyNotifier : IToyNotifier
{
    public readonly ConcurrentQueue<(string Account, string Key, string Type, JsonElement Payload)> Sent = new();

    public void SendLowPriority(string accountId, string key, string type, long revision, object payload) =>
        Sent.Enqueue((accountId, key, type, JsonSerializer.SerializeToElement(payload, ConvoyTestBase.Web)));

    public List<JsonElement> To(string account, string type) =>
        Sent.Where(m => m.Account == account && m.Type == type).Select(m => m.Payload).ToList();

    public JsonElement? Activity(string account, ToyActivityId activity) =>
        To(account, "toy.activity").LastOrDefault(p => p.GetProperty("activity").GetString() == activity.ToString()) is { ValueKind: JsonValueKind.Object } p
            ? p
            : null;

    public void Clear() => Sent.Clear();
}

/// <summary>In-memory snapshot store that can be made slow (a gate) or failing, for B11.</summary>
public sealed class MemoryToyStore : IToySnapshotStore
{
    public readonly ConcurrentDictionary<string, ToySnapshotRecord> Rows = new();
    public readonly TaskCompletionSource SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? Gate;
    public volatile bool Fail;
    public int Saves;

    public async Task SaveToySnapshotAsync(ToySnapshotRecord record, CancellationToken ct = default)
    {
        SaveEntered.TrySetResult();
        if (Gate is not null) await Gate.Task;
        if (Fail) throw new IOException("simulated storage outage");
        Interlocked.Increment(ref Saves);
        Rows.AddOrUpdate(record.SessionId, record, (_, old) => record.Revision >= old.Revision ? record : old);
    }

    public Task DeleteToySnapshotAsync(string sessionId, CancellationToken ct = default)
    {
        Rows.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ToySnapshotRecord>> LoadToySnapshotsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        foreach (ToySnapshotRecord r in Rows.Values.Where(r => r.DormantExpiresAt <= now).ToList()) Rows.TryRemove(r.SessionId, out _);
        return Task.FromResult<IReadOnlyList<ToySnapshotRecord>>(Rows.Values.OrderBy(r => r.SessionId, StringComparer.Ordinal).ToList());
    }
}

/// <summary>
/// The control plane hosting the five 'While We Wait' diversions (Addendum 02 §1, §11): one Core DowntimeSession per convoy
/// session under the directory lock, membership by generation, coalesced low-priority pushes, preemption by a committed race
/// (never delaying it), durable snapshots for dormant rooms across a restart with the 24 h D208 grace, readiness untouched,
/// and the envelope/size/rate guards.
/// </summary>
public sealed class ToyHostTests : ConvoyTestBase, IDisposable
{
    readonly TempDir temp = new();
    readonly RecordingToyNotifier pushes = new();
    readonly MemoryToyStore memory = new();
    readonly Dictionary<string, long> sequences = new();
    RecordingObserver observer = null!;
    ConvoyToys toys = null!;
    ToyService service = null!;
    ToySnapshotPersistence persistence = null!;

    public ToyHostTests() => Host(memory);

    public void Dispose() => temp.Dispose();

    /// <summary>A fresh "process": directory, toy host, service and persistence over <paramref name="store"/>.</summary>
    void Host(IToySnapshotStore store)
    {
        observer = new RecordingObserver();
        toys = new ConvoyToys(TestData.Toys, clock);
        dir = new ConvoyDirectory(clock, TestData.Content, notifier, TeamTrialCatalog.Fixture(TestData.Content.Catalogue), null,
            new IConvoySessionObserver[] { observer, toys });
        service = new ToyService(dir, toys, pushes, new RateLimiter(clock));
        persistence = new ToySnapshotPersistence(dir, toys, store, clock, NullLogger<ToySnapshotPersistence>.Instance);
        sequences.Clear();
    }

    string Session(int member = 1) => dir.MembershipOf(Id(member)).SessionId!;

    DowntimeSession? Live(string session) => dir.Exclusive(() => toys.LiveSession(session));

    /// <summary>A client-built Core ToyCommand envelope (its own session, generation, sequence and unique request id).</summary>
    JsonElement Envelope(int member, ToyActivityId activity, string kind, object? payload = null, string? session = null, long? gen = null)
    {
        string account = Id(member);
        (string? current, long generation) = dir.MembershipOf(account);
        string target = session ?? current ?? "cs-none";
        long g = gen ?? generation;
        string key = $"{account}/{g}";
        long seq = sequences[key] = (sequences.TryGetValue(key, out long s) ? s : 0) + 1;
        int epoch = dir.Exclusive(() => toys.LiveSession(target)?.Activity(activity).Run.Epoch ?? 1);
        return JsonSerializer.SerializeToElement(new
        {
            session = target, activity = activity.ToString(), epoch, member = account, gen = g, seq, req = $"q{member}-{seq}-{Guid.NewGuid():N}",
            kind, payload = payload ?? new { },
        });
    }

    ConvoyResult Toy(int member, ToyActivityId activity, string kind, object? payload = null, string? session = null, long? gen = null) =>
        service.Command(Id(member), Envelope(member, activity, kind, payload, session, gen));

    static JsonElement R(ConvoyResult r) => JsonSerializer.SerializeToElement(r.Value, Web);
    static string? Reason(ConvoyResult r) => R(r).GetProperty("reason").GetString();

    static void Accepted(ConvoyResult r)
    {
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal("Accepted", R(r).GetProperty("verdict").GetString());
    }

    static List<SlotCarState> Cars(DowntimeSession s) => s.PocketCircuit.State.Boards[s.PocketCircuit.State.ActiveLayout].Cars;

    string Stroke(int member, DowntimeSession s)
    {
        CanvasSheet sheet = s.Canvas.Document.Sheets[0];
        ConvoyResult begin = Toy(member, Canvas, "stroke.begin",
            new { sheet = sheet.SheetId, sheetEpoch = sheet.Epoch, color = 0xFF00AAFFL, width = 6, points = new[] { 100, 100, 110, 104 } });
        Accepted(begin);
        string id = R(begin).GetProperty("value").GetString()!;
        Accepted(Toy(member, Canvas, "stroke.append", new { sheet = sheet.SheetId, sheetEpoch = sheet.Epoch, @object = id, points = new[] { 120, 109 } }));
        Accepted(Toy(member, Canvas, "stroke.end", new { sheet = sheet.SheetId, sheetEpoch = sheet.Epoch, @object = id }));
        return id;
    }

    static async Task Eventually(Func<bool> condition, int timeoutMs = 5_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            if (cts.IsCancellationRequested) Assert.Fail("condition not reached in time");
            await Task.Delay(20);
        }
    }

    // ------------------------------------------------------------------ round trip

    [Fact]
    public void ToysStartLazily_SeatEveryMemberByGeneration_AndACommandReachesEveryoneAsOneCoalescedPush()
    {
        Convoy(2);
        string session = Session();
        Assert.Equal(0, service.Pump());
        Assert.Null(Live(session)); // nothing is created (or simulated) until somebody actually uses a toy

        Accepted(Toy(1, PocketCircuit, "lane.take", new { lane = 2 }));
        DowntimeSession s = Live(session)!;
        foreach (int m in new[] { 1, 2 })
        {
            Assert.Equal(SeatStatus.Active, s.Seat(Id(m))!.Status);
            Assert.Equal(dir.MembershipOf(Id(m)).Generation, s.Seat(Id(m))!.Generation);
        }

        // Several accepted commands between two ticks → ONE toy.state and one toy.activity per changed toy, per member.
        string stroke = Stroke(2, s);
        Assert.True(service.Pump() > 0);
        foreach (int m in new[] { 1, 2 })
        {
            JsonElement state = Assert.Single(pushes.To(Id(m), "toy.state"));
            Assert.Equal(session, state.GetProperty("convoySessionId").GetString());
            Assert.Equal(s.Revision, state.GetProperty("revision").GetInt64());
            Assert.Equal("Active", state.GetProperty("lifecycle").GetString());
            JsonElement circuit = state.GetProperty("toys").EnumerateArray().Single(t => t.GetProperty("activity").GetString() == "PocketCircuit");
            Assert.Equal(new[] { Id(1) }, circuit.GetProperty("players").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(2, state.GetProperty("seats").GetArrayLength());

            JsonElement pc = pushes.Activity(Id(m), PocketCircuit)!.Value;
            JsonElement car = pc.GetProperty("state").GetProperty("Boards").EnumerateObject().Single().Value.GetProperty("Cars").EnumerateArray().Single();
            Assert.Equal(Id(1), car.GetProperty("Member").GetString());
            Assert.Equal(2, car.GetProperty("Lane").GetInt32());

            JsonElement canvas = pushes.Activity(Id(m), Canvas)!.Value;
            CanvasDocument doc = CanvasCodec.Decode(canvas.GetProperty("encoded").GetString()!);
            Assert.Contains(doc.Sheets[0].Objects, o => o.Id == stroke && !o.Open);
            Assert.Equal(5, pushes.To(Id(m), "toy.activity").Count); // first tick: every toy once
        }

        pushes.Clear();
        Assert.Equal(0, service.Pump()); // nothing changed: nothing is re-sent

        // One more change: only that toy (plus the overview) goes out again.
        Accepted(Toy(2, Greenlight, "attempt.start", new { variant = "LightsOut" }));
        service.Pump();
        Assert.Equal(new[] { "Greenlight" }, pushes.To(Id(1), "toy.activity").Select(p => p.GetProperty("activity").GetString()));
        Assert.Single(pushes.To(Id(1), "toy.state"));
        Assert.All(pushes.Sent, m => Assert.StartsWith("toy.", m.Key)); // coalescing keys: one per overview / toy
    }

    [Fact]
    public void MembershipFollowsTheConvoy_DisconnectKeepsADormantSeat_RejoinRestoresIt_AndAStaleGenerationIsRefused()
    {
        Convoy(2);
        string session = Session();
        Accepted(Toy(2, PocketCircuit, "lane.take", new { lane = 3 }));
        DowntimeSession s = Live(session)!;
        long gen = dir.MembershipOf(Id(2)).Generation;

        Join(3);
        Assert.Equal(SeatStatus.Active, s.Seat(Id(3))!.Status); // a late joiner is seated with its own generation

        dir.Disconnected(Id(2));
        Assert.Equal(SeatStatus.Dormant, s.Seat(Id(2))!.Status);
        Assert.Single(Cars(s), c => c.Member == Id(2)); // the car is parked, not erased
        dir.Connected(Id(2), V);
        Assert.True(dir.Rejoin(Id(2), Info(2)).Ok);
        long regen = dir.MembershipOf(Id(2)).Generation;
        Assert.True(regen > gen);
        Assert.Equal(SeatStatus.Active, s.Seat(Id(2))!.Status);
        Assert.Equal(regen, s.Seat(Id(2))!.Generation);
        Assert.Single(Cars(s), c => c.Member == Id(2)); // restored without spawning another piece
        Assert.Equal("StaleMembership", Reason(Toy(2, PocketCircuit, "throttle", new { value = 1.0 }, gen: gen)));
        Accepted(Toy(2, PocketCircuit, "throttle", new { value = 0.4 }));

        // Explicit leave: the moving piece is retired at a safe revision; accepted artwork would stay.
        Assert.True(dir.Leave(Id(2)).Ok);
        Assert.Equal(SeatStatus.Retired, s.Seat(Id(2))!.Status);
        Assert.DoesNotContain(Cars(s), c => c.Member == Id(2));
    }

    [Fact]
    public void JoiningAnotherConvoy_CannotMutateThePreviousConvoysToys()
    {
        Convoy(2);
        string first = Session();
        string art = Stroke(2, Live(first) ?? MaterializeFor(2));
        DowntimeSession s = Live(first)!;
        Assert.True(dir.Leave(Id(2)).Ok);
        Assert.True(dir.Create(Id(2), Info(2), ConvoyPrivacy.InviteOnly).Ok);
        long before = s.Revision;

        ConvoyResult old = Toy(2, PocketCircuit, "lane.take", new { lane = 1 }, session: first);
        Assert.Equal("toy_rejected", old.Error!.Code);
        Assert.Equal("WrongSession", Reason(old));
        Assert.Equal(before, s.Revision);
        Assert.Contains(s.Canvas.Document.Sheets[0].Objects, o => o.Id == art && !o.Deleted); // accepted artwork is not erased

        Assert.True(dir.Leave(Id(2)).Ok);
        Assert.Equal("NotMember", Reason(Toy(2, PocketCircuit, "lane.take", new { lane = 1 }, session: first, gen: 1))); // in no convoy at all
    }

    DowntimeSession MaterializeFor(int member)
    {
        Assert.True(service.Snapshot(Id(member), default).Ok); // toy.snapshot also creates the session lazily
        return Live(Session(member))!;
    }

    // ------------------------------------------------------------------ preemption

    [Fact]
    public async Task ARaceAllocationPausesEveryToyWithoutWaiting_AndTheResultsEndThePause_WhileMovingToysWaitForAResume()
    {
        Convoy(2);
        string session = Session();
        Accepted(Toy(2, PocketCircuit, "lane.take", new { lane = 1 }));
        long rev = OpenEvent(); // committed mode entry: an instant boundary, not a hold
        DowntimeSession s = Live(session)!;
        Assert.Equal(1, s.PauseGeneration);
        Assert.False(s.PausedForEvent);

        // A cap is still sliding when the leader starts the race (the shot launches after the 1.5 s reorientation that
        // follows the mode-entry boundary, driven by the ≈10 Hz pump).
        Accepted(Toy(1, CapClash, "shot.submit", new { angle = 2.0, power = 0.8, launchX = 0.0, seen = s.CapClash.Board.BoardRevision }));
        for (int i = 0; i < 18; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            service.Pump();
        }
        CapBody cap = s.CapClash.Board.Caps.Single(c => c.Owner == Id(1));
        Assert.True(cap.Moving);

        ReadyAll(rev);
        (ConvoyError? error, MatchPlan? plan) = dir.BeginStart(Id(1), rev, Fresh(2));
        Assert.True(plan is not null, error?.Message); // the start never waited for a cap, a lap or an acknowledgement
        Assert.True(s.PausedForEvent);
        Assert.Equal(SessionLifecycle.Paused, s.Lifecycle);
        Assert.True(cap.Moving && cap.Vel.Length() > 0, "frozen WITH its velocity");
        Vec2 frozenAt = cap.Pos;
        Assert.Equal(0, memory.Saves); // nothing is written on the start path

        ConvoyResult late = Toy(1, CapClash, "shot.submit", new { angle = 1.0, power = 0.5, launchX = 0.0, seen = s.CapClash.Board.BoardRevision });
        Assert.Equal("toy_deferred", late.Error!.Code);
        Assert.Equal("SessionPaused", Reason(late));

        pushes.Clear();
        service.Pump();
        JsonElement paused = pushes.To(Id(2), "toy.state").Last();
        Assert.True(paused.GetProperty("pausedForEvent").GetBoolean());
        Assert.Equal(ToyHostLimits.PausedNotice, paused.GetProperty("notice").GetString());
        Assert.Equal("race-allocation", paused.GetProperty("pauseCause").GetString());

        // Persistence happens after the boundary, in the background, then Core is told it is durable.
        Assert.True(await persistence.DrainAsync() > 0);
        ToySnapshotRecord saved = memory.Rows[session];
        Assert.True(DowntimeCodec.Deserialize(saved.Json).PausedForEvent);
        Assert.Equal(saved.Revision, s.LastDurableRevision);

        // The event runs for three minutes: nothing moves, nothing accrues.
        dir.CompleteStart(plan!, new ActiveMatch("m_toys", "srv", "h", 1, V, new[] { Id(1), Id(2) }));
        clock.Advance(TimeSpan.FromMinutes(3));
        service.Pump();
        Assert.Equal(frozenAt, cap.Pos);
        Assert.True(s.PausedForEvent);

        dir.MatchEnded(plan!.ConvoyId, "m_toys", Fresh(2));
        Assert.False(s.PausedForEvent);
        Assert.True(s.CapClash.Run.Frozen); // a moving toy waits for an actual participant; nobody is forced back
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Pump();
        Assert.Equal(frozenAt, cap.Pos);

        Accepted(Toy(1, CapClash, "resume"));
        Assert.False(s.CapClash.Run.Frozen);
        for (int i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            service.Pump();
        }
        Assert.NotEqual(frozenAt, cap.Pos); // it slides on from exactly where it stopped
    }

    [Fact]
    public void AFailedStartOrAnAbortedEvent_EndsThePause_AndARetryDuplicatesNothing()
    {
        Convoy(2);
        string session = Session();
        long rev = OpenEvent();
        Accepted(Toy(1, PocketCircuit, "lane.take", new { lane = 2 }));
        DowntimeSession s = Live(session)!;
        ReadyAll(rev);

        MatchPlan plan = dir.BeginStart(Id(1), rev, Fresh(2)).Plan!;
        Assert.True(s.PausedForEvent);
        dir.FailStart(plan, "No compatible game server is available."); // loading/allocation aborted
        Assert.False(s.PausedForEvent);
        Assert.Single(Cars(s), c => c.Member == Id(1));

        MatchPlan retry = dir.BeginStart(Id(1), rev, Fresh(2)).Plan!; // readiness was kept: the leader retries
        Assert.True(s.PausedForEvent);
        dir.CompleteStart(retry, new ActiveMatch("m_abort", "srv", "h", 1, V, new[] { Id(1), Id(2) }));
        dir.MatchAborted(retry.ConvoyId, "m_abort", "The race server stopped responding.");
        Assert.False(s.PausedForEvent);
        Assert.Single(Cars(s), c => c.Member == Id(1)); // same snapshot, no duplicated car
        Assert.Equal(2, s.PauseGeneration); // first start and retry (the toys did not exist yet at mode entry)
    }

    [Fact]
    public async Task ASlowOrFailingSnapshotStore_NeverDelaysTheRaceStart_AndTheFailureIsReportedHonestly()
    {
        memory.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await persistence.StartAsync(CancellationToken.None); // the real background writer
        Convoy(2);
        string session = Session();
        long rev = OpenEvent();
        Accepted(Toy(1, Greenlight, "attempt.start", new { variant = "LightsOut" })); // first change: a snapshot is due
        await memory.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the writer is now stuck inside the store

        ReadyAll(rev);
        // Throws TimeoutException if the start waited for the toy store.
        var (_, plan) = await Task.Run(() => dir.BeginStart(Id(1), rev, Fresh(2))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(plan);
        Assert.True(Live(session)!.PausedForEvent);
        Assert.Equal(0, memory.Saves);

        // The store now fails: play and the race go on, the last verified state stays authoritative, and members are told.
        memory.Fail = true;
        memory.Gate.SetResult();
        await Eventually(() =>
        {
            service.Pump();
            return pushes.To(Id(2), "toy.state").Any(p => p.GetProperty("saveWarning").ValueKind == JsonValueKind.String);
        });
        Assert.Equal(0, Live(session)!.LastDurableRevision);
        await persistence.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void AFaultInsideToyCode_NeverBreaksTheRaceStart_TheRequest_OrAnotherConvoysToys()
    {
        Convoy(2);
        string session = Session();
        long rev = OpenEvent();
        Accepted(Toy(1, Greenlight, "view"));
        DowntimeSession s = Live(session)!;
        s.CapClash.State.Boards = null!; // simulate a bug inside Core toy code: every Advance of this session now throws

        dir.Connected(Id(5), V); // an unrelated convoy playing at the same time
        Assert.True(dir.Create(Id(5), Info(5), ConvoyPrivacy.InviteOnly).Ok);
        Accepted(Toy(5, Greenlight, "view"));

        Assert.Equal("InvalidState", Reason(Toy(2, Greenlight, "view"))); // an honest rejection, not an exception on the socket
        service.Pump();
        Assert.NotEmpty(pushes.To(Id(5), "toy.state")); // the faulty room is skipped; everyone else still gets pushes

        ReadyAll(rev);
        Assert.NotNull(dir.BeginStart(Id(1), rev, Fresh(2)).Plan); // the pause fault is contained: the race still starts
        Assert.True(dir.Leave(Id(2)).Ok); // and membership changes still work
    }

    // ------------------------------------------------------------------ readiness

    [Fact]
    public void UsingTheToys_NeverChangesModeReadyOrEventReady_AndNeverReadiesAnyone()
    {
        Convoy(3);
        long rev = OpenEvent();
        foreach (int m in new[] { 1, 2 })
            Assert.True(dir.SetReady(Id(m), rev, MemberState(1, m).GetProperty("loadoutRevision").GetInt64(), true).Ok); // member 3 is NOT ready
        long modeRevision = State(1).GetProperty("modeRevision").GetInt64();
        long convoyRevision = dir.SnapshotFor(Id(1)).Revision;

        foreach (int m in new[] { 2, 3 })
        {
            dir.Touch(Id(m)); // what the control channel does for every toy.command (real interaction)
            Accepted(Toy(m, Greenlight, "attempt.start", new { variant = "LightsOut" }));
            dir.Touch(Id(m));
            Accepted(Toy(m, PocketCircuit, "lane.take", new { lane = m })); // switching toys
            dir.Touch(Id(m));
            Accepted(Toy(m, PocketCircuit, "close"));
            Accepted(Toy(m, Canvas, "view")); // spectating
        }
        service.Pump();

        Assert.True(Ready(1) && Ready(2));
        Assert.False(Ready(3)); // no toy readies anyone
        foreach (int m in new[] { 1, 2, 3 }) Assert.True(MemberState(1, m).GetProperty("modeReady").GetBoolean());
        Assert.Equal(modeRevision, State(1).GetProperty("modeRevision").GetInt64());
        Assert.Equal(rev, State(1).GetProperty("eventProposal").GetProperty("revision").GetInt64());
        Assert.Equal(convoyRevision, dir.SnapshotFor(Id(1)).Revision); // not even a convoy change was published

        Assert.True(dir.SetReady(Id(3), rev, MemberState(1, 3).GetProperty("loadoutRevision").GetInt64(), true).Ok);
        Assert.NotNull(dir.BeginStart(Id(1), rev, Fresh(3)).Plan); // still startable: no toy blocks the event
    }

    // ------------------------------------------------------------------ envelope, size and rate guards

    [Fact]
    public void MalformedOrOversizedCommands_AreRejectedBeforeParsing_AndBeforeAnySessionWork()
    {
        Convoy(1);
        string session = Session();

        // Oversized AND structurally wrong (seq is a string): the size check answers first, before the envelope is parsed.
        ConvoyResult big = service.Command(Id(1), JsonSerializer.SerializeToElement(new
        {
            session, activity = "Canvas", epoch = 1, member = Id(1), gen = 1, seq = "one", req = "r1", kind = "text.add",
            payload = new { text = new string('x', ToyLimits.MaxCommandBytes) },
        }));
        Assert.Equal("toy_rejected", big.Error!.Code);
        Assert.Equal("TooLarge", Reason(big));
        // Counted in UTF-8 bytes, not characters.
        ConvoyResult wide = service.Command(Id(1), JsonSerializer.SerializeToElement(new
        {
            session, activity = "Canvas", epoch = 1, member = Id(1), gen = 1, seq = 1, req = "r2", kind = "text.add",
            payload = new { text = new string('€', 3_000) },
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal("TooLarge", Reason(wide));

        object Env(object? seq = null, string activity = "Greenlight", string kind = "view", string? member = null, object? payload = null) => new
        {
            session, activity, epoch = 1, member = member ?? Id(1), gen = 1, seq = seq ?? 1, req = "r-" + Guid.NewGuid().ToString("N"), kind,
            payload = payload ?? new { },
        };
        JsonElement[] malformed =
        {
            JsonSerializer.SerializeToElement(new[] { 1, 2 }),                                          // not an object
            JsonSerializer.SerializeToElement("toy.command"),
            JsonSerializer.SerializeToElement(new { session }),                                         // missing fields
            JsonSerializer.SerializeToElement(Env(seq: 0)),                                              // sequence starts at 1
            JsonSerializer.SerializeToElement(Env(activity: "MysteryGarage")),                           // not an approved diversion
            JsonSerializer.SerializeToElement(Env(kind: new string('k', ToyLimits.MaxKindLength + 1))),
            JsonSerializer.SerializeToElement(Env(member: "has spaces")),
            JsonSerializer.SerializeToElement(Env(payload: new { a = new { b = new { c = new { d = new { e = new { f = new { g = 1 } } } } } } })), // too deep
        };
        foreach (JsonElement bad in malformed)
        {
            ConvoyResult r = service.Command(Id(1), bad);
            Assert.False(r.Ok);
            Assert.Equal("Malformed", Reason(r));
        }

        ConvoyResult spoof = service.Command(Id(1), JsonSerializer.SerializeToElement(Env(member: Id(9))));
        Assert.Equal("NotMember", Reason(spoof)); // the envelope must name the signed-in account
        Assert.Null(Live(session)); // none of the above even created the convoy's toy session

        // A valid envelope with an invalid toy payload reaches the toy, which validates it strictly.
        Assert.Equal("Malformed", Reason(Toy(1, PocketCircuit, "lane.take", new { lane = "two" })));
        Assert.Equal("OutOfRange", Reason(Toy(1, PocketCircuit, "lane.take", new { lane = 99 })));
    }

    [Fact]
    public void RateLimits_MemberBucket_FloodGuardBeforeParsing_AndSnapshotRequests()
    {
        Convoy(2);
        for (int i = 0; i < (int)ToyLimits.RateBurst; i++) Accepted(Toy(1, Greenlight, "view"));
        ConvoyResult limited = Toy(1, Greenlight, "view");
        Assert.Equal("toy_rejected", limited.Error!.Code);
        Assert.Equal("RateLimited", Reason(limited)); // Core's per-member bucket (40/s, burst 80)
        clock.Advance(TimeSpan.FromSeconds(1));
        Accepted(Toy(1, Greenlight, "view")); // refilled

        // The envelope flood guard counts every attempt (even junk) and answers BEFORE parsing.
        for (int i = 0; i < ToyHostLimits.CommandFlood.Limit; i++) service.Command(Id(2), JsonSerializer.SerializeToElement(new[] { i }));
        ConvoyResult flood = service.Command(Id(2), JsonSerializer.SerializeToElement(new[] { 0 }));
        Assert.Equal("rate_limited", flood.Error!.Code); // not "Malformed": the payload was never looked at
        Assert.True(flood.Error.RetryAfterMs > 0);
        Assert.Equal("RateLimited", Reason(flood));

        for (int i = 0; i < ToyHostLimits.SnapshotRequests.Limit; i++) Assert.True(service.Snapshot(Id(1), default).Ok);
        Assert.Equal("rate_limited", service.Snapshot(Id(1), default).Error!.Code);
        Assert.Equal("invalid_request", service.Snapshot(Id(2), JsonSerializer.SerializeToElement(new { activity = "3" })).Error!.Code);
    }

    [Fact]
    public void TheClientSnapshot_KeepsServerOnlyFieldsServerSide_AndOneToyCanBeFetched()
    {
        Convoy(2);
        Accepted(Toy(1, PocketCircuit, "lane.take", new { lane = 2 }));
        JsonElement full = R(service.Snapshot(Id(2), default));
        JsonElement snap = full.GetProperty("snapshot");
        Assert.Equal(NonProgression.Domain, snap.GetProperty("Domain").GetString());
        Assert.Equal(ToyLimits.SchemaVersion, snap.GetProperty("Schema").GetInt32());
        Assert.Equal(0, snap.GetProperty("RandomState").GetInt64()); // the server's seed stream stays server-side
        Assert.False(snap.TryGetProperty("RecentRequests", out _)); // other members' request ids too
        Assert.All(snap.GetProperty("Seats").EnumerateArray(), seat => Assert.Equal(0, seat.GetProperty("RateTokens").GetDouble()));
        Assert.Equal(Session(), snap.GetProperty("SessionId").GetString());

        JsonElement one = R(service.Snapshot(Id(2), JsonSerializer.SerializeToElement(new { activity = "pocketcircuit" })));
        Assert.Equal("PocketCircuit", one.GetProperty("activity").GetString());
        Assert.Equal(1, one.GetProperty("epoch").GetInt32());
        Assert.Contains("\"Lane\":2", one.GetProperty("state").GetRawText());
        JsonElement canvas = R(service.Snapshot(Id(2), JsonSerializer.SerializeToElement(new { activity = "Canvas" })));
        Assert.NotNull(CanvasCodec.Decode(canvas.GetProperty("encoded").GetString()!));
        Assert.Equal("NotMember", Reason(service.Snapshot(Id(7), default)));
    }

    // ------------------------------------------------------------------ dormant rooms, restart, 24 h

    [Fact]
    public async Task ADormantRoomsToys_SurviveAControlPlaneRestart_AndComeBackOnAnAuthorizedRejoin()
    {
        using var db = new TempDir();
        var store = new SqliteGameStore(db.File("toys.db"));
        await store.InitializeAsync();
        Host(store);
        Convoy(2);
        string session = Session();
        Accepted(Toy(1, PocketCircuit, "lane.take", new { lane = 3 }));
        DowntimeSession s = Live(session)!;
        string stroke = Stroke(2, s);
        string drawing = CanvasCodec.DrawingHash(s.Canvas.Document);
        double carAt = Cars(s).Single(c => c.Member == Id(1)).S;

        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1)); // every member lost to disconnection → Dormant (D208)
        Assert.Null(Live(session)); // nothing keeps running for a dormant room: only the compact snapshot
        Assert.True(dir.Exclusive(() => toys.HasDormantSnapshot(session)));
        await persistence.DrainAsync();
        DormantRoomSnapshot room = observer.Snapshots.Last();
        await store.SaveDormantRoomAsync(room); // what DormantRoomPersistence does in the background
        ToySnapshotRecord row = Assert.Single(await store.LoadToySnapshotsAsync(clock.GetUtcNow()));
        Assert.Equal(room.ExpiresAt, row.DormantExpiresAt);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromHours(24), row.DormantExpiresAt);

        // Restart: a new process over the same database, three hours later.
        clock.Advance(TimeSpan.FromHours(3));
        Host(new SqliteGameStore(db.File("toys.db")));
        Assert.Equal(1, dir.RestoreDormant(await store.LoadDormantRoomsAsync(clock.GetUtcNow())));
        Assert.Equal(1, await persistence.RestoreAsync());
        Assert.Null(Live(session)); // still compact until someone is actually back

        dir.Connected(Id(1), V);
        Assert.True(dir.Rejoin(Id(1), Info(1)).Ok); // an explicit authorized rejoin, never a heartbeat
        DowntimeSession back = Live(session)!;
        Assert.Equal(SeatStatus.Active, back.Seat(Id(1))!.Status);
        Assert.Equal(dir.MembershipOf(Id(1)).Generation, back.Seat(Id(1))!.Generation);
        Assert.Equal(SeatStatus.Dormant, back.Seat(Id(2))!.Status);
        SlotCarState car = Assert.Single(Cars(back), c => c.Member == Id(1)); // the same car, not a second one
        Assert.Equal(3, car.Lane);
        Assert.Equal(carAt, car.S);
        Assert.Equal(drawing, CanvasCodec.DrawingHash(back.Canvas.Document));
        Assert.Contains(back.Canvas.Document.Sheets[0].Objects, o => o.Id == stroke);
        Accepted(Toy(1, PocketCircuit, "throttle", new { value = 0.5 })); // the new generation has input rights
        Assert.False(back.PausedForEvent);
    }

    [Fact]
    public async Task DormantToys_AreRetiredAfter24Hours_HeartbeatsDoNotExtendThem_AndARestartFabricatesNothing()
    {
        using var db = new TempDir();
        var store = new SqliteGameStore(db.File("expiry.db"));
        await store.InitializeAsync();
        Host(store);
        Convoy(2);
        Accepted(Toy(1, PocketCircuit, "lane.take", new { lane = 1 }));
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1));
        await persistence.DrainAsync();
        DormantRoomSnapshot room = observer.Snapshots.Last();
        await store.SaveDormantRoomAsync(room);

        // Reconnect attempts, pings and status polls for almost 24 h never extend the grace.
        for (int h = 0; h < 23; h++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            dir.Connected(Id(2), V);
            dir.Seen(Id(2));
            dir.RejoinStatus(Id(2));
            dir.Disconnected(Id(2));
            dir.Tick();
            service.Pump();
        }
        await persistence.DrainAsync();
        Assert.Equal(room.ExpiresAt, Assert.Single(await store.LoadToySnapshotsAsync(clock.GetUtcNow())).DormantExpiresAt);

        // A restart after the grace: nothing is restored and the stored toys are retired.
        clock.Advance(TimeSpan.FromHours(1));
        Host(store);
        Assert.Equal(0, dir.RestoreDormant(await store.LoadDormantRoomsAsync(clock.GetUtcNow())));
        Assert.Equal(0, await persistence.RestoreAsync());
        Assert.Empty(await store.LoadToySnapshotsAsync(DateTimeOffset.MinValue));
        dir.Connected(Id(1), V);
        Assert.Equal("rejoin_unavailable", dir.Rejoin(Id(1), Info(1)).Error?.Code); // no convoy was fabricated from toy data
    }

    [Fact]
    public async Task TheDirectorysDormantExpiry_OrALastVoluntaryLeave_RetiresTheStoredToys_AndOrphansAreNeverAdopted()
    {
        using var db = new TempDir();
        var store = new SqliteGameStore(db.File("retire.db"));
        await store.InitializeAsync();
        Host(store);

        // Same process: after 24 h the directory ends the dormant session and its stored toys go with it.
        Convoy(2);
        Accepted(Toy(1, PocketCircuit, "lane.take", new { lane = 1 }));
        dir.Disconnected(Id(2));
        dir.Disconnected(Id(1));
        await persistence.DrainAsync();
        Assert.Single(await store.LoadToySnapshotsAsync(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromHours(24));
        dir.Tick();
        Assert.Contains("ended dormant-expired", observer.Events);
        await persistence.DrainAsync();
        Assert.Empty(await store.LoadToySnapshotsAsync(DateTimeOffset.MinValue));

        // A last voluntary leave ends the session at once (no dormant grace) and retires its toys.
        dir.Connected(Id(1), V);
        Assert.True(dir.Create(Id(1), Info(1), ConvoyPrivacy.InviteOnly).Ok);
        Accepted(Toy(1, Greenlight, "attempt.start", new { variant = "ShiftWindow" }));
        await persistence.DrainAsync();
        Assert.Single(await store.LoadToySnapshotsAsync(clock.GetUtcNow()));
        Assert.True(dir.Leave(Id(1)).Ok);
        await persistence.DrainAsync();
        Assert.Empty(await store.LoadToySnapshotsAsync(DateTimeOffset.MinValue));

        // Toy data whose convoy was not recovered (e.g. an active room when the process died) is retired, never adopted.
        await store.SaveToySnapshotAsync(new ToySnapshotRecord("cs_orphan", 4, "{}", null));
        Host(store);
        Assert.Equal(0, await persistence.RestoreAsync());
        Assert.Empty(await store.LoadToySnapshotsAsync(DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task TheSnapshotTable_KeepsTheNewestRevision_AndRetiresExpiredDormantRows()
    {
        using var db = new TempDir();
        var store = new SqliteGameStore(db.File("table.db"));
        await store.InitializeAsync();
        DateTimeOffset now = clock.GetUtcNow();
        await store.SaveToySnapshotAsync(new ToySnapshotRecord("cs_a", 10, "{\"v\":10}", null));
        await store.SaveToySnapshotAsync(new ToySnapshotRecord("cs_a", 5, "{\"v\":5}", null)); // a late, older write loses
        await store.SaveToySnapshotAsync(new ToySnapshotRecord("cs_b", 3, "{\"v\":3}", now + TimeSpan.FromHours(1)));
        IReadOnlyList<ToySnapshotRecord> rows = await store.LoadToySnapshotsAsync(now);
        Assert.Equal(new[] { "cs_a", "cs_b" }, rows.Select(r => r.SessionId));
        Assert.Equal((10L, "{\"v\":10}"), (rows[0].Revision, rows[0].Json));
        Assert.Null(rows[0].DormantExpiresAt);
        Assert.Equal((now + TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(), rows[1].DormantExpiresAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(new[] { "cs_a" }, (await store.LoadToySnapshotsAsync(now + TimeSpan.FromHours(2))).Select(r => r.SessionId));
        await store.DeleteToySnapshotAsync("cs_a");
        Assert.Empty(await store.LoadToySnapshotsAsync(now));
    }
}
