using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NightSignal.Content;
using NightSignal.Core.Security;
using UnityEngine;

namespace NightSignal.Net
{
    /// <summary>
    /// Dedicated game-server process role (docs/NETWORKING.md §5): register with the control plane using the server
    /// key, heartbeat, long-poll for assignments, ack, run one authoritative match at a time, then submit
    /// HMAC-signed results. The server key never appears in logs or evidence.
    /// </summary>
    public sealed class GameServerHost : MonoBehaviour
    {
        NetConfig cfg;
        HttpClient http;
        RegisterResponse registration;
        List<TicketKey> ticketKeys;
        RaceServer active;
        MatchAssignment activeAssignment;
        string contentHash;
        bool running = true;
        public int MatchesCompleted { get; private set; }

        async void Start()
        {
            cfg = NetConfig.FromCommandLine();
            Application.targetFrameRate = 120;
            // Addendum 04: an unusable listen/advertise pair stops the host before it registers (fail closed).
            string bindProblem = cfg.BindProblem();
            if (bindProblem != null)
            {
                Debug.LogError($"[NightSignal.Server] refusing to start: {bindProblem}");
                Quit(4);
                return;
            }
            Debug.Log($"[NightSignal.Server] network boundary: bind {cfg.BindHost} ({cfg.BindClass()}), advertise {cfg.PublicHost}, UDP {cfg.Port}, " +
                      $"LAN opt-in {(cfg.AllowLan ? "yes" : "no")}");
            try
            {
                string key = File.ReadAllText(cfg.ServerKeyFile).Trim();
                http = new HttpClient { BaseAddress = new Uri(cfg.ControlPlaneUrl), Timeout = TimeSpan.FromSeconds(45) };
                http.DefaultRequestHeaders.Add("X-NightSignal-Server-Key", key);
                contentHash = ContentLibrary.Load().Catalogue.ContentHash;
                await Register();
                _ = HeartbeatLoop();
                await PollLoop();
            }
            catch (Exception e)
            {
                Debug.LogError($"[NightSignal.Server] host failed: {e.Message}");
                Quit(3);
            }
        }

        async Task Register()
        {
            var body = new
            {
                endpoint = new { host = cfg.PublicHost, port = cfg.Port },
                build = NetConfig.Build,
                protocol = Wire.ProtocolVersion,
                contentHash,
                maxMatches = 1,
            };
            HttpResponseMessage r = await http.PostAsync("/v1/servers/register", ControlPlaneHttp.Body(body));
            registration = ControlPlaneHttp.Deserialize<RegisterResponse>(await ControlPlaneHttp.ReadOrThrow(r, "register"));
            string jwks = await ControlPlaneHttp.ReadOrThrow(await http.GetAsync(registration.TicketJwksUrl), "ticket jwks");
            ticketKeys = MatchTicketValidator.ParseJwks(jwks);
            Debug.Log($"[NightSignal.Server] registered as {registration.ServerId}; build {NetConfig.Build}; content {contentHash.Substring(0, 12)}; {ticketKeys.Count} ticket key(s)");
        }

        async Task HeartbeatLoop()
        {
            while (running)
            {
                await Task.Delay(TimeSpan.FromSeconds(Mathf.Max(1, registration.HeartbeatIntervalSeconds)));
                try
                {
                    await http.PostAsync($"/v1/servers/{registration.ServerId}/heartbeat",
                        ControlPlaneHttp.Body(new { activeMatches = active != null ? 1 : 0 }));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[NightSignal.Server] heartbeat failed: {e.Message}");
                }
            }
        }

        async Task PollLoop()
        {
            while (running)
            {
                if (active != null)
                {
                    await Task.Delay(250);
                    continue;
                }
                HttpResponseMessage r = await http.GetAsync($"/v1/servers/{registration.ServerId}/assignments?waitSeconds=20");
                AssignmentsResponse list = ControlPlaneHttp.Deserialize<AssignmentsResponse>(await ControlPlaneHttp.ReadOrThrow(r, "assignments"));
                foreach (MatchAssignment a in list.Assignments)
                {
                    if (active != null) break;
                    HttpResponseMessage ack = await http.PostAsync($"/v1/servers/{registration.ServerId}/assignments/{a.MatchId}/ack", ControlPlaneHttp.Body(new { }));
                    await ControlPlaneHttp.ReadOrThrow(ack, "ack");
                    activeAssignment = a;
                    active = new GameObject($"Race_{a.MatchId}").AddComponent<RaceServer>();
                    active.Begin(a, ticketKeys, results => _ = SubmitResults(a, results));
                }
            }
        }

        async Task SubmitResults(MatchAssignment a, MatchResults results)
        {
            // Ghosts first (signed like the results, one per human, too large for the results body); settlement keeps a valid
            // one as the account's ghost only if it matches the settled finish.
            foreach (KeyValuePair<string, Core.Ghosts.GhostRecording> g in active != null ? active.Ghosts : new Dictionary<string, Core.Ghosts.GhostRecording>())
            {
                try
                {
                    byte[] gb = Encoding.UTF8.GetBytes(g.Value.ToJson());
                    var gr = new HttpRequestMessage(HttpMethod.Post, $"/v1/matches/{a.MatchId}/ghosts/{g.Key}") { Content = new ByteArrayContent(gb) };
                    gr.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    gr.Headers.Add("X-NightSignal-Signature", ControlPlaneHttp.Sign(gb, a.ResultsSecret));
                    HttpResponseMessage gresp = await http.SendAsync(gr);
                    Debug.Log($"[NightSignal.Server] ghost for {g.Key}: {g.Value.Count} samples, {gb.Length / 1024} KB, result {g.Value.Header.ResultMicros / 1e6:F3} s -> {(int)gresp.StatusCode}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[NightSignal.Server] ghost for {g.Key} not sent: {e.Message}");
                }
            }
            byte[] body = Encoding.UTF8.GetBytes(ControlPlaneHttp.Serialize(results));
            string status;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, a.ResultsUrl) { Content = new ByteArrayContent(body) };
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                req.Headers.Add("X-NightSignal-Signature", ControlPlaneHttp.Sign(body, a.ResultsSecret));
                HttpResponseMessage r = await http.SendAsync(req);
                status = $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}";
            }
            catch (Exception e)
            {
                status = "submit failed: " + e.Message;
            }
            Debug.Log($"[NightSignal.Server] results for {a.MatchId}: {Truncate(status, 400)}");
            WriteEvidence(a, results, status);
            await Task.Delay(5000); // let clients receive the results summary before the socket closes
            active.Shutdown();
            Destroy(active.gameObject);
            active = null;
            MatchesCompleted++;
            if (Environment.GetCommandLineArgs().Contains("-nsExitAfterMatch")) Quit(0);
        }

        void WriteEvidence(MatchAssignment a, MatchResults results, string status)
        {
            try
            {
                Directory.CreateDirectory(cfg.EvidenceDir);
                var ev = new
                {
                    role = "dedicated game server (separate process)",
                    matchId = a.MatchId, kind = a.Kind, stageId = a.StageId, courseId = a.CourseId,
                    humans = a.Entrants.Count, ai = a.AiEntrants.Count, build = NetConfig.Build, contentHash,
                    results, transport = active != null ? active.Diagnostics() : null, controlPlaneResponse = status, utc = DateTime.UtcNow.ToString("o"),
                };
                File.WriteAllText(Path.Combine(cfg.EvidenceDir, $"server-{a.MatchId}.json"), Newtonsoft.Json.JsonConvert.SerializeObject(ev, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NightSignal.Server] evidence write failed: {e.Message}");
            }
        }

        static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        void Quit(int code)
        {
            running = false;
            Application.Quit(code);
        }

        void OnDestroy()
        {
            running = false;
            http?.Dispose();
        }
    }
}
