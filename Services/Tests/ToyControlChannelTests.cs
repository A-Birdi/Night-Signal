using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NightSignal.ControlPlane.Control;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// The toys over the real <c>/v1/control</c> WebSocket (in-process host: DevAuth, SQLite, the real toy pump at ≈10 Hz):
/// <c>toy.command</c> round trip, pushes reaching the other member, <c>toy.snapshot</c>, readiness untouched, and a Dormant
/// room's toys surviving a control-plane restart. Plus the connection's low-priority lane (ordering and coalescing).
/// </summary>
public sealed class ToyControlChannelTests : IDisposable
{
    const string Query = "build=b&protocol=2&content=c";
    readonly TempDir dir = new();
    readonly IReadOnlyList<DevAccountFixture> accounts;

    public ToyControlChannelTests() => accounts = TestData.WriteSeed(dir.File("seed.json"), 2);

    public void Dispose() => dir.Dispose();

    static string Type(JsonElement m) => m.GetProperty("type").GetString()!;

    static void AssertOk(JsonElement reply) =>
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.TryGetProperty("error", out JsonElement e) ? e.GetRawText() : reply.GetRawText());

    static JsonElement Result(JsonElement reply) => reply.GetProperty("result");

    /// <summary>A client-side envelope exactly as a game client would build it from convoy.state and toy.snapshot.</summary>
    static object Envelope(string session, string activity, int epoch, string member, long generation, long seq, string kind, object payload) => new
    {
        session, activity, epoch, member, gen = generation, seq, req = $"c-{Guid.NewGuid():N}", kind, payload,
    };

    static JsonElement Me(JsonElement state, string accountId) =>
        state.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("accountId").GetString() == accountId);

    [Fact]
    public async Task ToyCommand_RoundTrip_PushesToTheOtherMember_WithoutTouchingReadiness()
    {
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"));
        await using ControlClient ca = await host.ConnectAsync(await host.SignInAsync(accounts[0]), Query);
        await using ControlClient cb = await host.ConnectAsync(await host.SignInAsync(accounts[1]), Query);
        AssertOk(await ca.RequestAsync("convoy.create", new { privacy = "invite-only" }));
        string code = Result(await ca.RequestAsync("convoy.invite.create")).GetProperty("code").GetString()!;
        AssertOk(await cb.RequestAsync("convoy.join", new { code }));
        long mode = Result(await ca.RequestAsync("intent.set", new { kind = "freeplay", submode = "sprint" })).GetProperty("modeRevision").GetInt64();
        AssertOk(await cb.RequestAsync("mode.ready", new { modeRevision = mode, ready = true }));
        JsonElement state = await ca.WaitForStateAsync(s => s.GetProperty("members").GetArrayLength() == 2 && s.GetProperty("modeReadyCount").GetInt32() == 2);
        string session = state.GetProperty("convoySessionId").GetString()!;
        string a = accounts[0].AccountId;
        long gen = Me(state, a).GetProperty("membershipGeneration").GetInt64();
        long convoyRevision = (await ca.RequestAsync("convoy.state")).GetProperty("result").GetProperty("revision").GetInt64();

        // The client learns the toy's epoch, then drives it.
        JsonElement circuit = await ca.RequestAsync("toy.snapshot", new { activity = "PocketCircuit" });
        AssertOk(circuit);
        int epoch = Result(circuit).GetProperty("epoch").GetInt32();
        JsonElement took = await ca.RequestAsync("toy.command", Envelope(session, "PocketCircuit", epoch, a, gen, 1, "lane.take", new { lane = 4 }));
        AssertOk(took);
        Assert.Equal("Accepted", Result(took).GetProperty("verdict").GetString());
        Assert.True(Result(took).GetProperty("revision").GetInt64() > 0);

        // The other member sees it through the coalesced, low-priority pushes.
        JsonElement push = await cb.WaitForAsync(m => Type(m) == "toy.activity" &&
            m.GetProperty("payload").GetProperty("activity").GetString() == "PocketCircuit" &&
            m.GetProperty("payload").GetProperty("state").GetRawText().Contains($"\"Member\":\"{a}\""));
        Assert.Equal(session, push.GetProperty("payload").GetProperty("convoySessionId").GetString());
        JsonElement overview = (await cb.WaitForAsync(m => Type(m) == "toy.state")).GetProperty("payload");
        Assert.Equal(session, overview.GetProperty("convoySessionId").GetString());

        // A duplicate request id is answered from Core's idempotency window, not applied twice.
        object same = Envelope(session, "Greenlight", 1, a, gen, 2, "attempt.start", new { variant = "LightsOut" });
        JsonElement first = await ca.RequestAsync("toy.command", same);
        AssertOk(first);
        JsonElement again = await ca.RequestAsync("toy.command", same);
        AssertOk(again);
        Assert.True(Result(again).GetProperty("duplicate").GetBoolean());
        Assert.Equal(Result(first).GetProperty("revision").GetInt64(), Result(again).GetProperty("revision").GetInt64());

        // Oversized, malformed and stale-epoch commands get a ToyResult rejection; the session goes on.
        JsonElement big = await ca.RequestAsync("toy.command", Envelope(session, "Canvas", 1, a, gen, 3, "text.add", new { text = new string('x', 9_000) }));
        Assert.Equal("toy_rejected", big.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("TooLarge", Result(big).GetProperty("reason").GetString());
        JsonElement junk = await ca.RequestAsync("toy.command", new { session, seq = "x" });
        Assert.Equal("Malformed", Result(junk).GetProperty("reason").GetString());
        JsonElement stale = await ca.RequestAsync("toy.command", Envelope(session, "PocketCircuit", epoch + 7, a, gen, 4, "throttle", new { value = 1.0 }));
        Assert.Equal("StaleEpoch", Result(stale).GetProperty("reason").GetString());

        // Readiness and the convoy revision are untouched by all of it.
        JsonElement after = Result(await ca.RequestAsync("convoy.state"));
        Assert.Equal(convoyRevision, after.GetProperty("revision").GetInt64());
        Assert.True(Me(after.GetProperty("convoy"), a).GetProperty("modeReady").GetBoolean());
        Assert.True(Me(after.GetProperty("convoy"), accounts[1].AccountId).GetProperty("modeReady").GetBoolean());

        JsonElement snapshot = await cb.RequestAsync("toy.snapshot");
        AssertOk(snapshot);
        Assert.Equal("toy-nonprogression", Result(snapshot).GetProperty("snapshot").GetProperty("Domain").GetString());
    }

    [Fact]
    public async Task ADormantRoomsToys_SurviveARealControlPlaneRestart()
    {
        string session;
        using (var host = new ControlPlaneHost(dir.Path, dir.File("seed.json")))
        {
            ControlClient ca = await host.ConnectAsync(await host.SignInAsync(accounts[0]), Query);
            ControlClient cb = await host.ConnectAsync(await host.SignInAsync(accounts[1]), Query);
            AssertOk(await ca.RequestAsync("convoy.create", new { privacy = "invite-only" }));
            string code = Result(await ca.RequestAsync("convoy.invite.create")).GetProperty("code").GetString()!;
            AssertOk(await cb.RequestAsync("convoy.join", new { code }));
            JsonElement state = await ca.WaitForStateAsync(s => s.GetProperty("members").GetArrayLength() == 2);
            session = state.GetProperty("convoySessionId").GetString()!;
            long gen = Me(state, accounts[1].AccountId).GetProperty("membershipGeneration").GetInt64();
            AssertOk(await cb.RequestAsync("toy.command",
                Envelope(session, "PocketCircuit", 1, accounts[1].AccountId, gen, 1, "lane.take", new { lane = 5 })));

            await cb.DisposeAsync();
            await ca.WaitForStateAsync(s => s.GetProperty("members").GetArrayLength() == 1);
            await ca.DisposeAsync(); // every member lost to disconnection → Dormant; toys saved in the background
            await using ControlClient probe = await host.ConnectAsync(await host.SignInAsync(accounts[0]), Query);
            Assert.True((await probe.WaitForAsync(m => Type(m) == "hello")).GetProperty("payload").GetProperty("rejoin").GetProperty("dormant").GetBoolean());
        } // shutdown drains pending writes

        using (var restarted = new ControlPlaneHost(dir.Path, dir.File("seed.json")))
        {
            await using ControlClient cb = await restarted.ConnectAsync(await restarted.SignInAsync(accounts[1]), Query);
            await cb.WaitForAsync(m => Type(m) == "hello");
            AssertOk(await cb.RequestAsync("convoy.rejoin"));
            JsonElement circuit = await cb.RequestAsync("toy.snapshot", new { activity = "PocketCircuit" });
            AssertOk(circuit);
            Assert.Equal(session, Result(circuit).GetProperty("convoySessionId").GetString());
            string cars = Result(circuit).GetProperty("state").GetRawText();
            Assert.Contains($"\"Member\":\"{accounts[1].AccountId}\"", cars); // the same car, restored from the durable snapshot
            Assert.Contains("\"Lane\":5", cars);
        }
    }

    // ------------------------------------------------------------------ the connection's low-priority lane

    sealed class RecordingSocket : WebSocket
    {
        public readonly List<string> Sent = new();
        WebSocketState state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            lock (Sent) Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }

        public List<string> Types()
        {
            lock (Sent) return Sent.Select(s => JsonDocument.Parse(s).RootElement).Select(e => $"{e.GetProperty("type").GetString()}:{e.GetProperty("payload")}").ToList();
        }
    }

    [Fact]
    public async Task LowPriorityMessages_WaitForControlTraffic_AndOnlyTheLatestValuePerKeyIsSent()
    {
        var socket = new RecordingSocket();
        var connection = new ControlConnection(socket, "acc");
        connection.Send("convoy.state", 1, 1);
        connection.SendLowPriority("toy.state", "toy.state", 0, 10);
        connection.SendLowPriority("toy.state", "toy.state", 0, 11); // supersedes 10 while waiting
        connection.Send("convoy.notice", 2, 2);
        connection.SendLowPriority("toy.activity/Canvas", "toy.activity", 0, 20);
        connection.Send("match.allocated", 0, 3);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task writer = connection.RunWriterAsync(cts.Token);
        await Task.Run(async () => { while (socket.Types().Count < 5) await Task.Delay(10); }).WaitAsync(TimeSpan.FromSeconds(5));
        connection.Close(WebSocketCloseStatus.NormalClosure, "bye");
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "convoy.state:1", "convoy.notice:2", "match.allocated:3", "toy.state:11", "toy.activity:20" }, socket.Types());
    }
}
