using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightSignal.ControlPlane.Security;

namespace NightSignal.Services.Tests.Infrastructure;

/// <summary>
/// The real control plane in-process (WebApplicationFactory + TestServer): Development environment, DevAuth,
/// SQLite in a temp folder, keys in a temp folder, a known game-server key. Everything else is production code.
/// </summary>
public sealed class ControlPlaneHost : WebApplicationFactory<Program>
{
    public const string ServerId = "test-server";
    readonly Dictionary<string, string?> settings;
    readonly string environment;

    public ControlPlaneHost(string dataDir, string seedFile, ManualClock? clock = null, string environment = "Development",
        Dictionary<string, string?>? overrides = null, string? serverKey = null)
    {
        Clock = clock;
        this.environment = environment;
        ServerKey = serverKey ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        settings = new Dictionary<string, string?>
        {
            ["DevAuth:Enabled"] = "true",
            ["DevAuth:SeedFile"] = seedFile,
            ["Storage:Provider"] = "Sqlite",
            ["Storage:SqlitePath"] = Path.Combine(dataDir, "controlplane.db"),
            ["Storage:ApplyMigrations"] = "true",
            ["Keys:DevKeyDirectory"] = Path.Combine(dataDir, "keys"),
            ["GameServers:GenerateDevCredential"] = "false",
            ["GameServers:Credentials:0:Id"] = ServerId,
            ["GameServers:Credentials:0:KeySha256"] = Hashing.Sha256Hex(ServerKey),
            ["GameServers:StaleAfterSeconds"] = "3600",
            ["GameServers:AssignmentAckTimeoutSeconds"] = "10",
        };
        foreach (var kv in overrides ?? new()) settings[kv.Key] = kv.Value;
    }

    public ManualClock? Clock { get; }
    public string ServerKey { get; }
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var kv in settings) builder.UseSetting(kv.Key, kv.Value);
        builder.ConfigureLogging(l => l.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            if (Clock is not null) services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
        });
    }

    public async Task<string> SignInAsync(DevAccountFixture account)
    {
        HttpResponseMessage r = await CreateClient().PostAsJsonAsync("/dev/auth/token", new { email = account.Email, password = account.Password });
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sign-in failed: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    public HttpClient Authed(string token)
    {
        HttpClient c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public HttpClient GameServer()
    {
        HttpClient c = CreateClient();
        c.DefaultRequestHeaders.Add("X-NightSignal-Server-Key", ServerKey);
        return c;
    }

    public async Task<ControlClient> ConnectAsync(string token, string query)
    {
        WebSocketClient ws = Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
        WebSocket socket = await ws.ConnectAsync(new Uri($"ws://localhost/v1/control?{query}"), CancellationToken.None);
        return new ControlClient(socket);
    }
}

/// <summary>Minimal control-channel client used by the integration tests.</summary>
public sealed class ControlClient : IAsyncDisposable
{
    readonly WebSocket socket;
    readonly List<JsonElement> received = new();
    readonly SemaphoreSlim arrived = new(0);
    readonly Task reader;
    int nextRequest;

    public ControlClient(WebSocket socket)
    {
        this.socket = socket;
        reader = Task.Run(ReadAsync);
    }

    public WebSocketCloseStatus? CloseStatus => socket.CloseStatus;

    async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                int length = 0;
                WebSocketReceiveResult r;
                do
                {
                    r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), CancellationToken.None);
                    length += r.Count;
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                using JsonDocument doc = JsonDocument.Parse(buffer.AsMemory(0, length));
                lock (received) received.Add(doc.RootElement.Clone());
                arrived.Release();
            }
        }
        catch (Exception) { }
        finally { arrived.Release(); }
    }

    public List<JsonElement> Messages
    {
        get { lock (received) return received.ToList(); }
    }

    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> match, int timeoutMs = 10_000)
    {
        JsonElement found = default;
        await WaitUntilAsync(() => { foreach (JsonElement m in Messages) if (match(m)) { found = m; return true; } return false; }, timeoutMs);
        return found;
    }

    public async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            if (reader.IsCompleted) throw new InvalidOperationException($"Socket closed ({socket.CloseStatus}) before the expected message.");
            await arrived.WaitAsync(cts.Token);
        }
    }

    public Task SendRawAsync(string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Sends a request and returns the "reply" payload with the same requestId.</summary>
    public async Task<JsonElement> RequestAsync(string type, object? payload = null, string? requestId = null)
    {
        requestId ??= $"r{Interlocked.Increment(ref nextRequest)}-{Guid.NewGuid():N}";
        int already = Messages.Count(m => IsReply(m, requestId));
        await SendRawAsync(JsonSerializer.Serialize(new { type, requestId, payload = payload ?? new { } }));
        await WaitUntilAsync(() => Messages.Count(m => IsReply(m, requestId)) > already);
        return Messages.Last(m => IsReply(m, requestId)).GetProperty("payload");
    }

    static bool IsReply(JsonElement m, string requestId) =>
        m.GetProperty("type").GetString() == "reply" && m.GetProperty("payload").TryGetProperty("requestId", out JsonElement id) && id.GetString() == requestId;

    /// <summary>Latest convoy.state payload (highest revision).</summary>
    public JsonElement LatestState() =>
        Messages.Where(m => m.GetProperty("type").GetString() == "convoy.state").MaxBy(m => m.GetProperty("revision").GetInt64())
            .GetProperty("payload");

    public async Task<JsonElement> WaitForStateAsync(Func<JsonElement, bool> match) =>
        (await WaitForAsync(m => m.GetProperty("type").GetString() == "convoy.state" && match(m.GetProperty("payload")))).GetProperty("payload");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", new CancellationTokenSource(2000).Token);
        }
        catch (Exception) { }
        socket.Dispose();
    }
}
