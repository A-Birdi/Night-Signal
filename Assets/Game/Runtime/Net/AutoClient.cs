using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
                await Retrying(() => cp.Request("destination.propose", new { destination = "campaign-normal" }));
                await WaitFor(() => AllMembers(m => (bool?)m["destinationConsent"] == true), 60, "destination consent");
                await cp.Request("destination.commit", new { proposalRevision = (long)cp.ConvoyState["destinationProposal"]["revision"] });
                await Retrying(() => cp.Request("event.propose", new { stageId = cfg.AutoStage }));
                await WaitFor(() => AllMembers(m => (bool?)m["eventReady"] == true), 60, "event readiness");
                await cp.Request("event.start", new { proposalRevision = (long)cp.ConvoyState["eventProposal"]["revision"] });
                Note("event started");
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
                rttMs = race.Rtt(), snapshots = race.SnapshotsReceived, inputPacketsSent = race.InputsSent,
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
                long rev = (long)p["proposalRevision"];
                if ((string)p["kind"] == "destination")
                    await cp.Request("destination.consent", new { proposalRevision = rev, consent = true });
                else
                {
                    JToken me = cp.ConvoyState["members"].First(m => (string)m["accountId"] == cp.AccountId);
                    await cp.Request("event.ready", new { proposalRevision = rev, loadoutRevision = (long)me["loadoutRevision"], ready = true });
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

        static async Task Retrying(Func<Task<JToken>> action)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { await action(); return; }
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
