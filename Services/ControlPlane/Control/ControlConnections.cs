using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Toys;

namespace NightSignal.ControlPlane.Control;

/// <summary>
/// One authenticated control-channel socket. All sends go through a single writer loop. Ordinary (race/control) messages
/// are sent in order; low-priority messages (toy pushes, toy snapshots) are coalesced per key — only the latest value of a
/// key waits — and are written only when no ordinary message is waiting (Addendum 02 §1.6).
/// </summary>
public sealed class ControlConnection(WebSocket socket, string accountId)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Distinct low-priority keys that may wait per connection; beyond it new keys are dropped (never ordinary traffic).</summary>
    public const int MaxLowPriorityKeys = 64;
    static readonly object LowPriorityTurn = new();
    readonly Channel<object> outbox = Channel.CreateBounded<object>(new BoundedChannelOptions(512) { SingleReader = true });
    readonly object lowGate = new();
    readonly Dictionary<string, string> lowPending = new(StringComparer.Ordinal);
    readonly Queue<string> lowOrder = new();
    bool lowScheduled;

    sealed record CloseRequest(WebSocketCloseStatus Status, string Reason);

    public string AccountId { get; } = accountId;
    public DateTimeOffset TokenExpiresAt { get; set; }

    /// <summary>Queues a server message <c>{"type","revision","payload"}</c>. A client that cannot keep up is disconnected.</summary>
    public void Send(string type, long revision, object payload)
    {
        string json = JsonSerializer.Serialize(new { type, revision, payload }, Json);
        if (!outbox.Writer.TryWrite(json) && outbox.Writer.TryComplete())
            socket.Abort(); // queue overflow: drop the client rather than silently losing ordered state
    }

    /// <summary>
    /// Queues a low-priority message under <paramref name="key"/>: a newer message with the same key replaces a waiting
    /// one (latest state wins), and it is written only when no ordinary message is waiting. Never disconnects the client.
    /// </summary>
    public void SendLowPriority(string key, string type, long revision, object payload)
    {
        string json = JsonSerializer.Serialize(new { type, revision, payload }, Json);
        lock (lowGate)
        {
            if (!lowPending.ContainsKey(key))
            {
                if (lowPending.Count >= MaxLowPriorityKeys) return;
                lowOrder.Enqueue(key);
            }
            lowPending[key] = json;
            if (lowScheduled) return;
            lowScheduled = true;
        }
        ScheduleLowPriority();
    }

    /// <summary>Puts the single low-priority turn marker at the back of the queue (at most one is ever queued).</summary>
    void ScheduleLowPriority()
    {
        if (!outbox.Writer.TryWrite(LowPriorityTurn))
            lock (lowGate) lowScheduled = false; // full or closing: the next low-priority send schedules again
    }

    bool TakeLowPriority(out string json, out bool more)
    {
        lock (lowGate)
        {
            while (lowOrder.TryDequeue(out string? key))
                if (lowPending.Remove(key, out string? value))
                {
                    json = value;
                    more = lowOrder.Count > 0;
                    if (!more) lowScheduled = false;
                    return true;
                }
            json = "";
            more = false;
            lowScheduled = false;
            return false;
        }
    }

    public void Close(WebSocketCloseStatus status, string reason)
    {
        outbox.Writer.TryWrite(new CloseRequest(status, reason));
        outbox.Writer.TryComplete();
    }

    public async Task RunWriterAsync(CancellationToken ct)
    {
        try
        {
            await foreach (object item in outbox.Reader.ReadAllAsync(ct))
            {
                if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
                if (item is CloseRequest close)
                {
                    await socket.CloseOutputAsync(close.Status, close.Reason, ct);
                    break;
                }
                string json;
                if (ReferenceEquals(item, LowPriorityTurn))
                {
                    if (outbox.Reader.Count > 0)
                    {
                        ScheduleLowPriority(); // race/control messages are waiting: they go first
                        continue;
                    }
                    if (!TakeLowPriority(out json, out bool more)) continue;
                    if (more) ScheduleLowPriority();
                }
                else json = (string)item;
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The reader loop observes the broken socket and cleans up.
        }
    }
}

/// <summary>Routes directory notifications to live sockets; enforces one active control session per account.</summary>
public sealed class ControlConnections : IConvoyNotifier, IToyNotifier
{
    readonly ConcurrentDictionary<string, ControlConnection> byAccount = new();

    /// <summary>Registers <paramref name="connection"/>. Without <paramref name="takeover"/> an existing session wins.</summary>
    public bool TryRegister(ControlConnection connection, bool takeover, out ControlConnection? replaced)
    {
        replaced = null;
        while (true)
        {
            if (byAccount.TryAdd(connection.AccountId, connection)) return true;
            if (!byAccount.TryGetValue(connection.AccountId, out ControlConnection? existing)) continue;
            if (!takeover) return false;
            if (byAccount.TryUpdate(connection.AccountId, connection, existing))
            {
                replaced = existing;
                return true;
            }
        }
    }

    /// <summary>Returns true when <paramref name="connection"/> was still the account's current session.</summary>
    public bool Unregister(ControlConnection connection) =>
        byAccount.TryRemove(new KeyValuePair<string, ControlConnection>(connection.AccountId, connection));

    public bool IsConnected(string accountId) => byAccount.ContainsKey(accountId);

    public void Send(string accountId, string type, long revision, object payload)
    {
        if (byAccount.TryGetValue(accountId, out ControlConnection? c))
            c.Send(type, revision, payload);
    }

    public void SendLowPriority(string accountId, string key, string type, long revision, object payload)
    {
        if (byAccount.TryGetValue(accountId, out ControlConnection? c))
            c.SendLowPriority(key, type, revision, payload);
    }
}
