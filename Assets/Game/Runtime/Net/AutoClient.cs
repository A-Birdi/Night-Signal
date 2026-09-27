using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NightSignal.Race;
using NightSignal.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NightSignal.Net
{
    /// <summary>
    /// Automated player for multi-process network tests. Uses the real control plane (DevAuth sign-in, convoy,
    /// both ready checks, start), connects to the dedicated server with its single-use ticket, drives with the
    /// route-following autopilot through normal inputs, then fetches its own receipt. Evidence is labelled as a
    /// scripted driver — it is not a human playtest.
    /// </summary>
    public sealed class AutoClient : MonoBehaviour
    {
        NetConfig cfg;
        ControlPlaneClient cp;
        RaceClient race;
        JObject allocation;
        string log = "";

        async void Start()
        {
            cfg = NetConfig.FromCommandLine();
            DontDestroyOnLoad(gameObject);
            if (Application.isBatchMode)
            {
                // Headless test clients: 60 fps keeps six clients plus a server within one machine's CPU budget.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }
            int exit = 1;
            try
            {
                exit = await Run() ? 0 : 1;
            }
            catch (Exception e)
            {
                Note("FAILED: " + e.Message);
                Debug.LogError("[NightSignal.Auto] " + e);
                WriteEvidence(new { failed = true, error = e.Message, log });
            }
            race?.Disconnect();
            cp?.Dispose();
            await Task.Delay(500);
            Application.Quit(exit);
        }

        void Update() => cp?.Pump();

        async Task<bool> Run()
        {
            (string email, string password) = DevAccount(cfg.DevAccount);
            string contentHash = ContentLibrary.Load().Catalogue.ContentHash;
            cp = new ControlPlaneClient(cfg.ControlPlaneUrl);
            await cp.SignInDev(email, password);
            Note($"signed in as account …{cp.AccountId.Substring(Math.Max(0, cp.AccountId.Length - 6))}");
            await cp.Post("/v1/me/card", new { displayName = $"Driver {cfg.DevAccount + 1}", revision = 0 });
            JObject me = await cp.Get("/v1/me");
            if (me["starterCarId"] == null || me["starterCarId"].Type == JTokenType.Null)
                await cp.Post("/v1/me/starter", new { carId = cfg.AutoCar });
            me = await cp.Get("/v1/me");
            long walletBefore = (long)me["wallet"]["balance"];
            int rpBefore = (int)me["rank"]["rankPoints"];
            string car = (string)me["starterCarId"];

            cp.MatchAllocated += p => allocation = p;
            cp.ReadyRequested += p => _ = AnswerReady(p);
            await cp.ConnectControl(NetConfig.Build, Wire.ProtocolVersion, contentHash);

            bool leader = cfg.AutoRole == "leader";
            if (leader)
            {
                await cp.Request("convoy.create", new { privacy = "discoverable" });
                await WaitFor(() => Members() >= cfg.AutoHumans, 180, "all members joined");
            }
            else
            {
                await WaitFor(async () =>
                {
                    JToken list = await cp.Request("convoy.list");
                    JToken open = (list?["convoys"] ?? list)?.FirstOrDefault();
                    if (open == null) return false;
                    try { await cp.Request("convoy.join", new { convoyId = (string)open["convoyId"] }); return true; }
                    catch (ControlError) { return false; }
                }, 180, "joined a discoverable convoy");
            }
            await cp.Request("loadout.set", new { carId = car, performanceHash = "stock", cosmeticHash = "default" });

            if (leader)
            {
                // Addendum 01 §7: Intent → everyone Mode Ready → Enter Mode → event proposal → Event Ready → Start.
                bool freeplay = !string.IsNullOrEmpty(cfg.AutoFreeplayCourse);
                object intent = freeplay ? (object)new { kind = "freeplay", submode = cfg.AutoFreeplayMode } : new { kind = "campaign", mode = "normal" };
                JToken set = await Retrying(() => cp.Request("intent.set", intent));
                long modeRevision = (long)set["modeRevision"];
                await WaitFor(() => AllMembers(m => (bool?)m["modeReady"] == true), 60, "mode readiness");
                await cp.Request("mode.enter", new { modeRevision });
                Note(freeplay ? $"entered freeplay ({cfg.AutoFreeplayMode})" : "entered Normal campaign");
                object proposal = freeplay
                    ? (object)new { courseId = cfg.AutoFreeplayCourse, freeplayMode = cfg.AutoFreeplayMode, aiCount = cfg.AutoFreeplayAi }
                    : new { stageId = cfg.AutoStage };
                JToken proposed = await Retrying(() => cp.Request("event.propose", proposal));
                long proposalRevision = (long)proposed["proposalRevision"];
                await WaitFor(() => AllMembers(m => (bool?)m["eventReady"] == true), 60, "event readiness");
                JToken started = await cp.Request("event.start", new { proposalRevision });
                Note($"event started ({started?["vehicles"] ?? "?"} vehicles)");
            }

            await WaitFor(() => allocation != null, 120, "match allocation");
            string matchId = (string)allocation["matchId"];
            var go = new GameObject("RaceClient");
            DontDestroyOnLoad(go);
            race = go.AddComponent<RaceClient>();
            race.Autopilot = true;
            race.Connect((string)allocation["server"]["host"], (ushort)(int)allocation["server"]["port"], (string)allocation["ticket"]);
            Note($"connecting to match {matchId}");

            await WaitFor(() => race.Results != null || race.Phase == MatchPhase.Aborted || race.DisconnectReason != null, cfg.ExitAfterSeconds, "race results");
            if (race.Results == null) throw new InvalidOperationException($"no results: phase {race.Phase}, disconnect '{race.DisconnectReason}'");
            ResultEntrant mine = race.Results.Entrants.FirstOrDefault(e => e.EntrantId == cp.AccountId);

            JObject receipt = null;
            await WaitFor(async () =>
            {
                (int status, JObject body) = await cp.GetWithStatus($"/v1/matches/{matchId}/receipt");
                if (status == 200 && (string)body["status"] != "pending") { receipt = body; return true; }
                return false;
            }, 60, "receipt");
            me = await cp.Get("/v1/me");
            long walletAfter = (long)me["wallet"]["balance"];

            WriteEvidence(new
            {
                driver = "AutoClient scripted autopilot (not a human)",
                process = "independent client player process",
                account = "…" + cp.AccountId.Substring(Math.Max(0, cp.AccountId.Length - 6)),
                role = cfg.AutoRole, matchId, stage = cfg.AutoStage, course = race.Info?.CourseId, car,
                rttMs = race.Rtt(), inputAckMsAverage = race.InputAckMsAverage, inputAckMsMax = race.InputAckMsMax,
                minInputLeadTicks = race.MinInputLeadTicks, ticksFilled = race.TicksFilled,
                snapshots = race.SnapshotsReceived, inputPacketsSent = race.InputsSent,
                reconciliations = race.Corrections, maxCorrectionMetres = race.MaxCorrectionMetres,
                result = mine, entrants = race.Results.Entrants.Count, receipt,
                walletBefore, walletAfter, rankPointsBefore = rpBefore, rankPointsAfter = (int)me["rank"]["rankPoints"],
                log, utc = DateTime.UtcNow.ToString("o"),
            });
            bool ok = mine != null && mine.Outcome == "Finished" && walletAfter > walletBefore;
            Note(ok ? "PASS" : "FAIL: no finish or no credit");
            return ok;
        }

        async Task AnswerReady(JObject p)
        {
            try
            {
                switch ((string)p["kind"])
                {
                    case "mode":
                        await cp.Request("mode.ready", new { modeRevision = (long)p["modeRevision"], ready = true });
                        break;
                    case "event":
                        JToken me = cp.ConvoyState["members"].First(m => (string)m["accountId"] == cp.AccountId);
                        await cp.Request("event.ready", new { proposalRevision = (long)p["proposalRevision"], loadoutRevision = (long)me["loadoutRevision"], ready = true });
                        break;
                    default:
                        Note($"ready request '{(string)p["kind"]}' ignored by the scripted client");
                        break;
                }
            }
            catch (Exception e)
            {
                Note($"ready answer failed: {e.Message}");
            }
        }

        int Members() => cp.ConvoyState?["members"]?.Count() ?? 0;

        bool AllMembers(Func<JToken, bool> pred) =>
            cp.ConvoyState?["members"] != null && Members() >= cfg.AutoHumans && cp.ConvoyState["members"].All(m => pred(m));

        /// <summary>Retries leader requests that hit the 15 s ready-request cooldown; returns the server's reply.</summary>
        static async Task<JToken> Retrying(Func<Task<JToken>> action)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return await action(); }
                catch (ControlError e) when (e.Code == "rate_limited" && attempt < 5) { await Task.Delay(4000); }
            }
        }

        static async Task WaitFor(Func<bool> condition, int seconds, string what)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException($"timed out waiting for {what}");
                await Task.Delay(100);
            }
        }

        static async Task WaitFor(Func<Task<bool>> condition, int seconds, string what)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!await condition())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException($"timed out waiting for {what}");
                await Task.Delay(500);
            }
        }

        (string, string) DevAccount(int index)
        {
            JObject seed = JObject.Parse(File.ReadAllText(cfg.DevSeedFile));
            JToken a = seed["accounts"][index];
            return ((string)a["email"], (string)a["devOnlyPassword"]);
        }

        void Note(string s)
        {
            log += $"[{DateTime.UtcNow:HH:mm:ss}] {s}\n";
            Debug.Log("[NightSignal.Auto] " + s);
        }

        void WriteEvidence(object o)
        {
            try
            {
                Directory.CreateDirectory(cfg.EvidenceDir);
                File.WriteAllText(Path.Combine(cfg.EvidenceDir, $"client-{cfg.DevAccount}-{cfg.AutoRole}.json"), JsonConvert.SerializeObject(o, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NightSignal.Auto] evidence write failed: " + e.Message);
            }
        }
    }
}
