using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NightSignal.Net
{
    /// <summary>
    /// Player-side control-plane client (docs/NETWORKING.md §1–3): sign-in, REST, and the authenticated control
    /// WebSocket with request/reply correlation. Tokens are held in memory only and never logged.
    /// </summary>
    public sealed class ControlPlaneClient : IDisposable
    {
        readonly HttpClient http;
        ClientWebSocket socket;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> pending = new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();
        readonly ConcurrentQueue<(long Due, JObject Msg)> inbox = new ConcurrentQueue<(long, JObject)>();

        // Automation (-nsImpairControl delayMs,jitterMs): latency on the control channel in both directions, message order
        // kept (a reliable stream: loss shows as delay, so there is no drop). Seeded, so a run can be repeated.
        static readonly int ImpairDelayMs, ImpairJitterMs;
        readonly System.Random impairRandom = new System.Random(7);
        readonly object impairGate = new object();
        long lastSendDue, lastReceiveDue;
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        static ControlPlaneClient()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-nsImpairControl");
            if (i < 0 || i + 1 >= args.Length) return;
            string[] parts = args[i + 1].Split(',');
            if (parts.Length > 0 && int.TryParse(parts[0], out int d)) ImpairDelayMs = Math.Max(0, d);
            if (parts.Length > 1 && int.TryParse(parts[1], out int j)) ImpairJitterMs = Math.Max(0, j);
            Debug.Log($"[NightSignal.Control] impaired control channel: {ImpairDelayMs} ± {ImpairJitterMs} ms each way");
        }

        /// <summary>The one-way delay a control message is held for (ms, 0 unimpaired), never earlier than the last one.</summary>
        long Due(ref long last)
        {
            long now = clock.ElapsedMilliseconds;
            if (ImpairDelayMs == 0 && ImpairJitterMs == 0) return now;
            lock (impairGate)
            {
                long due = now + ImpairDelayMs + (ImpairJitterMs > 0 ? impairRandom.Next(-ImpairJitterMs, ImpairJitterMs + 1) : 0);
                last = Math.Max(last, due);
                return last;
            }
        }
        // ClientWebSocket allows ONE outstanding send: overlapping SendAsync calls (a stroke's append, then its end) could be
        // refused or reach the server out of order. Every request goes through this FIFO and a single send loop.
        readonly ConcurrentQueue<byte[]> outbox = new ConcurrentQueue<byte[]>();
        readonly SemaphoreSlim outboxSignal = new SemaphoreSlim(0);
        int nextRequest;
        // Request ids must be unique across client sessions: the control plane replays a cached reply for the same
        // (account, type, requestId) for 10 minutes so retransmits stay idempotent. A counter restarting at "r1" in a new
        // process got the PREVIOUS process's replies (found in a real 6-client run: convoy.create returned an old convoy).
        readonly string requestPrefix = Guid.NewGuid().ToString("N").Substring(0, 16);
        string accessToken;

        public string AccountId { get; private set; }
        public JObject ConvoyState { get; private set; }
        public long ConvoyRevision { get; private set; } = -1;
        public event Action<JObject> ConvoyChanged;
        public event Action<JObject> ReadyRequested;
        public event Action<JObject> MatchAllocated;
        public event Action<JObject> MatchAborted;
        /// <summary>convoy.notice {code, message}: shown once per event.</summary>
        public event Action<JObject> Notice;
        /// <summary>Server-owned RejoinStatus (from hello and rejoin.status pushes); never inferred locally.</summary>
        public JObject RejoinStatus { get; private set; }
        public event Action<JObject> RejoinChanged;
        /// <summary>convoy.closed {convoyId, reason}: this account is no longer in that convoy.</summary>
        public event Action<JObject> ConvoyClosed;
        public bool Connected => socket != null && socket.State == WebSocketState.Open;
        /// <summary>While We Wait: coalesced toy overview (toy.state) and per-toy state (toy.activity) pushes.</summary>
        public event Action<JObject> ToyState;
        public event Action<JObject> ToyActivity;
        /// <summary>convoy.invited: a friend invited this account to their convoy (accept = convoy.join {inviteId}).</summary>
        public event Action<JObject> Invited;
        /// <summary>The meet room's state (on change) and poses (≈10 Hz), and an invitation to a friend's meet.</summary>
        public event Action<JObject> MeetState, MeetPoses, MeetInvited, MeetChallenge;

        public ControlPlaneClient(string baseUrl)
        {
            http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task SignInDev(string email, string password)
        {
            HttpResponseMessage r = await http.PostAsync("/dev/auth/token", ControlPlaneHttp.Body(new { email, password }));
            JObject body = JObject.Parse(await ControlPlaneHttp.ReadOrThrow(r, "sign-in"));
            accessToken = (string)body["access_token"];
            AccountId = (string)body["user"]?["id"];
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        public async Task<JObject> Get(string path) =>
            JObject.Parse(await ControlPlaneHttp.ReadOrThrow(await http.GetAsync(path), "GET " + path));

        public async Task<(int status, JObject body)> GetWithStatus(string path)
        {
            HttpResponseMessage r = await http.GetAsync(path);
            string text = await r.Content.ReadAsStringAsync();
            return ((int)r.StatusCode, string.IsNullOrEmpty(text) ? new JObject() : JObject.Parse(text));
        }

        public async Task<(int status, JObject body)> Post(string path, object payload)
        {
            HttpResponseMessage r = await http.PostAsync(path, ControlPlaneHttp.Body(payload));
            string text = await r.Content.ReadAsStringAsync();
            return ((int)r.StatusCode, string.IsNullOrEmpty(text) ? new JObject() : JObject.Parse(text));
        }

        /// <summary>Any REST verb; the body may be null. Extra headers (e.g. Idempotency-Key) apply to this request only.</summary>
        public async Task<(int status, JObject body)> Send(HttpMethod method, string path, object payload = null, IDictionary<string, string> headers = null)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (payload != null) request.Content = ControlPlaneHttp.Body(payload);
                if (headers != null)
                    foreach (KeyValuePair<string, string> h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                HttpResponseMessage r = await http.SendAsync(request);
                string text = await r.Content.ReadAsStringAsync();
                return ((int)r.StatusCode, string.IsNullOrEmpty(text) ? new JObject() : JObject.Parse(text));
            }
        }

        public async Task ConnectControl(string build, int protocol, string contentHash)
        {
            socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
            string ws = http.BaseAddress.ToString().Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/');
            await socket.ConnectAsync(new Uri($"{ws}/v1/control?build={Uri.EscapeDataString(build)}&protocol={protocol}&content={contentHash}"), cts.Token);
            _ = ReceiveLoop();
            _ = SendLoop();
        }

        /// <summary>Sends a request and awaits its reply; throws on error replies.</summary>
        public async Task<JToken> Request(string type, object payload = null)
        {
            string id = requestPrefix + "-" + Interlocked.Increment(ref nextRequest);
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            var envelope = new JObject { ["type"] = type, ["requestId"] = id, ["payload"] = payload != null ? JObject.FromObject(payload) : new JObject() };
            byte[] bytes = Encoding.UTF8.GetBytes(envelope.ToString(Newtonsoft.Json.Formatting.None));
            outbox.Enqueue(bytes); // call order = wire order
            outboxSignal.Release();
            Task done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            pending.TryRemove(id, out _);
            if (done != tcs.Task) throw new TimeoutException($"control request {type} timed out");
            JObject reply = tcs.Task.Result;
            if (!(bool)reply["ok"])
                throw new ControlError((string)reply["error"]?["code"], (string)reply["error"]?["message"]);
            return reply["result"];
        }

        async Task SendLoop()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await outboxSignal.WaitAsync(cts.Token);
                    if (!outbox.TryDequeue(out byte[] bytes)) continue;
                    long wait = Due(ref lastSendDue) - clock.ElapsedMilliseconds;
                    if (wait > 0) await Task.Delay((int)wait, cts.Token);
                    if (socket.State != WebSocketState.Open) continue; // the request times out and reports the problem
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                Debug.LogWarning($"[NightSignal.Control] send loop stopped: {e.Message}");
            }
        }

        async Task ReceiveLoop()
        {
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            try
            {
                while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    WebSocketReceiveResult r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    JObject msg = JObject.Parse(sb.ToString());
                    sb.Clear();
                    long due = Due(ref lastReceiveDue);
                    if ((string)msg["type"] == "reply")
                    {
                        string id = (string)msg["payload"]?["requestId"];
                        if (id != null && pending.TryGetValue(id, out TaskCompletionSource<JObject> tcs))
                        {
                            var reply = (JObject)msg["payload"];
                            long wait = due - clock.ElapsedMilliseconds;
                            if (wait <= 0) tcs.TrySetResult(reply);
                            else _ = Task.Delay((int)wait).ContinueWith(_ => tcs.TrySetResult(reply));
                        }
                    }
                    else
                    {
                        inbox.Enqueue((due, msg));
                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                Debug.LogWarning($"[NightSignal.Control] socket closed: {e.Message}");
            }
        }

        /// <summary>Dispatches server pushes on the calling (main) thread. Call once per frame.</summary>
        public void Pump()
        {
            long now = clock.ElapsedMilliseconds;
            while (inbox.TryPeek(out var next) && next.Due <= now && inbox.TryDequeue(out var item))
            {
                JObject msg = item.Msg;
                string type = (string)msg["type"];
                var payload = msg["payload"] as JObject;
                switch (type)
                {
                    case "convoy.state":
                        long rev = (long?)msg["revision"] ?? 0;
                        if (rev >= ConvoyRevision) // discard stale convoy revisions
                        {
                            ConvoyRevision = rev;
                            ConvoyState = payload;
                            ConvoyChanged?.Invoke(payload);
                        }
                        break;
                    case "hello":
                        RejoinStatus = payload?["rejoin"] as JObject;
                        RejoinChanged?.Invoke(RejoinStatus);
                        break;
                    case "rejoin.status":
                        RejoinStatus = payload;
                        RejoinChanged?.Invoke(payload);
                        break;
                    case "convoy.notice": Notice?.Invoke(payload); break;
                    case "toy.state": ToyState?.Invoke(payload); break;
                    case "toy.activity": ToyActivity?.Invoke(payload); break;
                    case "convoy.invited": Invited?.Invoke(payload); break;
                    case "meet.state": MeetState?.Invoke(payload); break;
                    case "meet.poses": MeetPoses?.Invoke(payload); break;
                    case "meet.invited": MeetInvited?.Invoke(payload); break;
                    case "meet.challenge": MeetChallenge?.Invoke(payload); break;
                    case "convoy.closed":
                        ConvoyState = null;
                        ConvoyRevision = -1;
                        ConvoyClosed?.Invoke(payload);
                        ConvoyChanged?.Invoke(null);
                        break;
                    case "ready.requested": ReadyRequested?.Invoke(payload); break;
                    case "match.allocated": MatchAllocated?.Invoke(payload); break;
                    case "match.aborted": MatchAborted?.Invoke(payload); break;
                }
            }
        }

        public void Dispose()
        {
            cts.Cancel();
            try { socket?.Abort(); } catch (Exception) { }
            socket?.Dispose();
            http.Dispose();
        }
    }

    public sealed class ControlError : Exception
    {
        public ControlError(string code, string message) : base($"{code}: {message}")
        {
            Code = code;
        }

        public string Code { get; }
    }
}
