using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NightSignal.ControlPlane.Convoys;

namespace NightSignal.ControlPlane.Control;

/// <summary>One authenticated control-channel socket. All sends go through a single writer loop.</summary>
public sealed class ControlConnection(WebSocket socket, string accountId)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly Channel<object> outbox = Channel.CreateBounded<object>(new BoundedChannelOptions(512) { SingleReader = true });

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
                await socket.SendAsync(Encoding.UTF8.GetBytes((string)item), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The reader loop observes the broken socket and cleans up.
        }
    }
}

/// <summary>Routes directory notifications to live sockets; enforces one active control session per account.</summary>
public sealed class ControlConnections : IConvoyNotifier
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
}
