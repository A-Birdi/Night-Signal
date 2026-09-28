using System.Buffers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Identity;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Security;

namespace NightSignal.ControlPlane.Control;

/// <summary>
/// The authenticated control/presence WebSocket at /v1/control (spec §3.2, §4). The upgrade request carries
/// "Authorization: Bearer &lt;access token&gt;" (validated by JwtBearer like every other API). The socket is closed
/// with 4401 when that token expires unless the client sends "session.reauth" with a refreshed token.
/// </summary>
public sealed class ControlChannel(ControlConnections connections, ControlCommandHandler handler, ConvoyDirectory directory,
    IPlayerStore store, AccessTokenValidator tokens, TimeProvider clock, IOptions<CompatibilityOptions> compatibility,
    ILogger<ControlChannel> log, Meet.MeetService meets)
{
    public const string Path = "/v1/control";
    const int MaxMessageBytes = 16 * 1024;

    public async Task RunAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        string accountId = http.User.AccountId();
        long exp = long.TryParse(http.User.FindFirstValue("exp"), out long e) ? e : 0;
        IQueryCollection q = http.Request.Query;
        ClientVersion? version = int.TryParse(q["protocol"], out int protocol) && !string.IsNullOrEmpty(q["build"]) && !string.IsNullOrEmpty(q["content"])
            ? new ClientVersion(q["build"]!, protocol, q["content"]!)
            : null;
        bool takeover = q["takeover"] == "1";

        await store.EnsureAccountAsync(accountId, http.RequestAborted);
        using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync();
        var connection = new ControlConnection(socket, accountId) { TokenExpiresAt = DateTimeOffset.FromUnixTimeSeconds(exp) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        Task writer = connection.RunWriterAsync(cts.Token);

        if (version is not null && version.Protocol != compatibility.Value.Protocol)
        {
            connection.Close((WebSocketCloseStatus)4400, "unsupported_protocol");
            await writer;
            return;
        }
        if (!connections.TryRegister(connection, takeover, out ControlConnection? replaced))
        {
            // One active session per account: a second client must explicitly take over (spec §3.2a).
            connection.Send("session.rejected", 0, new { reason = "active_elsewhere", message = "This account is connected on another client. Reconnect with takeover=1 to move the session here." });
            connection.Close((WebSocketCloseStatus)4409, "active_elsewhere");
            await writer;
            return;
        }
        if (replaced is not null)
        {
            replaced.Send("session.superseded", 0, new { message = "Your session was taken over by another client you signed in with." });
            replaced.Close((WebSocketCloseStatus)4410, "superseded");
        }

        directory.Connected(accountId, version);
        // "rejoin" tells a reconnecting client whether the server holds a valid rejoin grant (canRejoin + reason) so it can
        // offer one "Rejoin [convoy]?" prompt; it is never inferred from local storage (Addendum 01 §10.2).
        connection.Send("hello", 0, new
        {
            accountId, serverTime = clock.GetUtcNow(), protocol = compatibility.Value.Protocol, tokenExpiresAt = connection.TokenExpiresAt,
            rejoin = directory.RejoinStatus(accountId),
        });
        (long revision, object? snapshot) = directory.SnapshotFor(accountId);
        if (snapshot is not null) connection.Send("convoy.state", revision, snapshot);

        Task expiry = WatchExpiryAsync(connection, cts.Token);
        try
        {
            await ReadLoopAsync(socket, connection, cts.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            if (connections.Unregister(connection))
            {
                directory.Disconnected(accountId);
                meets.Disconnected(accountId); // the meet shows "disconnected" and holds the bay for the grace
            }
            // Let the writer flush and send the close frame before cancelling anything (graceful close handshake).
            connection.Close(WebSocketCloseStatus.NormalClosure, "bye");
            try { await writer.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { socket.Abort(); }
            await cts.CancelAsync();
            await expiry.ContinueWith(_ => { });
        }
    }

    async Task WatchExpiryAsync(ControlConnection connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan left = connection.TokenExpiresAt - clock.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                connection.Send("session.expired", 0, new { message = "Access token expired; reconnect or send session.reauth." });
                connection.Close((WebSocketCloseStatus)4401, "token_expired");
                return;
            }
            await Task.Delay(left < TimeSpan.FromSeconds(5) ? left : TimeSpan.FromSeconds(5), clock, ct);
        }
    }

    async Task ReadLoopAsync(WebSocket socket, ControlConnection connection, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxMessageBytes);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                int length = 0;
                WebSocketReceiveResult result;
                do
                {
                    if (length >= MaxMessageBytes)
                    {
                        connection.Close(WebSocketCloseStatus.MessageTooBig, "message_too_big");
                        return;
                    }
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, MaxMessageBytes - length), ct);
                    length += result.Count;
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                await HandleMessageAsync(connection, new ReadOnlyMemory<byte>(buffer, 0, length), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Client envelope: <c>{"type": string, "requestId": string, "payload": object}</c>.</summary>
    async Task HandleMessageAsync(ControlConnection connection, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        string type;
        string? requestId;
        JsonElement payload;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(message);
            JsonElement root = doc.RootElement;
            type = root.GetProperty("type").GetString() ?? throw new JsonException();
            requestId = root.TryGetProperty("requestId", out JsonElement rid) ? rid.GetString() : null;
            payload = root.TryGetProperty("payload", out JsonElement p) ? p.Clone() : default;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            connection.Send("error", 0, new { error = "malformed", message = "Expected {\"type\",\"requestId\",\"payload\"}." });
            return;
        }

        if (type == "session.reauth")
        {
            string? token = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("accessToken", out JsonElement t) ? t.GetString() : null;
            (ClaimsPrincipal? principal, DateTimeOffset expires) = token is null ? (null, default) : await tokens.ValidateAsync(token, ct);
            bool ok = principal is not null && principal.AccountId() == connection.AccountId;
            if (ok) connection.TokenExpiresAt = expires;
            else log.LogInformation("Rejected control-channel re-authentication for {AccountId}", connection.AccountId);
            connection.Send("reply", 0, ok
                ? new Reply(requestId, true, new { tokenExpiresAt = expires })
                : new Reply(requestId, false, Error: new ConvoyError("unauthorized", "Token invalid or for another account.")));
            return;
        }

        directory.Seen(connection.AccountId);
        if (!ControlCommandHandler.ReadOnlyTypes.Contains(type))
            directory.Touch(connection.AccountId);
        Reply reply = await handler.HandleAsync(connection.AccountId, type, requestId, payload, ct);
        (long revision, _) = directory.SnapshotFor(connection.AccountId);
        if (ControlCommandHandler.LowPriorityReplyTypes.Contains(type))
            connection.SendLowPriority("reply/" + (requestId ?? Guid.NewGuid().ToString("N")), "reply", revision, reply); // large toy data waits for race/control traffic
        else
            connection.Send("reply", revision, reply);
    }
}
