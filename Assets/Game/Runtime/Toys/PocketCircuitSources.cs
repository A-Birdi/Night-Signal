using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.PocketCircuit;
using NightSignal.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NightSignal.Toys
{
    /// <summary>Where the Pocket Circuit table lives: this process (Local) or the convoy's hosted session (Online).</summary>
    public interface IPocketCircuitSource
    {
        string Member { get; }
        /// <summary>The table to render (authoritative locally; a mirror of the server's state online).</summary>
        PocketCircuitTable Table { get; }
        /// <summary>Short status line (e.g. "Paused for the convoy — progress kept"), or empty.</summary>
        string Status { get; }
        bool Online { get; }
        void Send(string kind, JObject payload);
        /// <summary>Lets the source apply the player's throttle locally at once (prediction) before the authority answers.</summary>
        void PredictThrottle(float value);
        void Tick();
        /// <summary>Leaving the table: parks this member's car; everything else stays exactly as it is.</summary>
        void Leave();
    }

    /// <summary>Local play: the in-process authoritative session, saved in the profile's non-progression toy workspace.</summary>
    public sealed class LocalCircuitSource : IPocketCircuitSource
    {
        readonly LocalToyHost host;
        readonly Func<string, bool> save;
        string status = "";

        public LocalCircuitSource(LocalToyHost host, Func<string, bool> save)
        {
            this.host = host;
            this.save = save;
            host.Advance();
        }

        public string Member => host.Member;
        public PocketCircuitTable Table => host.Session.PocketCircuit;
        public string Status => status;
        public bool Online => false;
        public void PredictThrottle(float value) { }

        public void Send(string kind, JObject payload)
        {
            ToyResult r = host.Do(ToyActivityId.PocketCircuit, kind, payload);
            status = r.Accepted ? (status.StartsWith("Rejected") ? "" : status) : $"{r.Verdict}: {r.Reason}" + (r.Detail != null ? " — " + r.Detail : "");
        }

        public void Tick() => host.Advance();

        public void Leave()
        {
            host.Close(ToyActivityId.PocketCircuit);
            host.Advance();
            if (!save(host.SnapshotJson())) Debug.LogWarning("[NightSignal.Toys] the Local toy table could not be saved");
        }
    }

    /// <summary>
    /// Online play at the convoy's shared table (Addendum 02 §1, §5): commands go to the control plane's hosted session
    /// in the Core envelope; the server pushes the table state (≤ 10 Hz) and this client mirrors it with the same Core
    /// types, dead-reckoning cars between pushes with the shared <see cref="SlotSim"/>. The server stays the authority:
    /// every push replaces the mirror. Using the table never changes Mode/Event readiness.
    /// </summary>
    public sealed class OnlineCircuitSource : IPocketCircuitSource
    {
        readonly ControlPlaneClient client;
        readonly Func<JObject> convoy;
        readonly ToyContent content;
        readonly string requestPrefix = Guid.NewGuid().ToString("N").Substring(0, 10);
        long sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10; // strictly increasing across reconnects
        int requests;
        int epoch;
        bool frozen;
        string notice = "";
        string error = "";
        PocketCircuitTable table;
        double pendingSeconds;
        float myThrottle;
        bool myThrottleFresh;

        public OnlineCircuitSource(ControlPlaneClient client, Func<JObject> convoy, ToyContent content)
        {
            this.client = client;
            this.convoy = convoy;
            this.content = content;
            table = new PocketCircuitTable(new MirrorHost(), content.PocketCircuit, new PocketCircuitState());
            client.ToyActivity += OnActivity;
            client.ToyState += OnOverview;
            _ = Enter();
        }

        public string Member => client.AccountId;
        public PocketCircuitTable Table => table;
        public bool Online => true;
        public string Status => !string.IsNullOrEmpty(error) ? error : frozen ? (string.IsNullOrEmpty(notice) ? "Paused for the convoy — progress kept" : notice) : notice;

        async System.Threading.Tasks.Task Enter()
        {
            try
            {
                await client.Request("diversion.set", new { toy = "pocket-circuit" });
                JToken snap = await client.Request("toy.snapshot", new { activity = "PocketCircuit" });
                if (snap is JObject o) OnActivity(o);
            }
            catch (Exception e)
            {
                error = "The shared table is not available: " + e.Message;
            }
        }

        void OnActivity(JObject p)
        {
            if (p == null || (string)p["activity"] != "PocketCircuit") return;
            epoch = (int?)p["epoch"] ?? epoch;
            frozen = (bool?)p["frozen"] ?? false;
            if (p["state"] is JObject state)
            {
                PocketCircuitState s = state.ToObject<PocketCircuitState>(JsonSerializer.CreateDefault());
                table = new PocketCircuitTable(new MirrorHost(), content.PocketCircuit, s);
                pendingSeconds = 0;
                if (myThrottleFresh) ApplyMyThrottle();
            }
            else if ((bool?)p["omitted"] == true) _ = Refetch();
        }

        async System.Threading.Tasks.Task Refetch()
        {
            try
            {
                JToken snap = await client.Request("toy.snapshot", new { activity = "PocketCircuit" });
                if (snap is JObject o && o["state"] is JObject) OnActivity(o);
            }
            catch (Exception) { /* the next push will try again */ }
        }

        void OnOverview(JObject p)
        {
            if (p == null) return;
            bool paused = (bool?)p["pausedForEvent"] == true;
            notice = paused ? (string)p["notice"] ?? "Paused for the convoy — progress kept" : "";
            string warn = (string)p["saveWarning"] ?? (string)p["restoreWarning"] ?? (string)p["faultWarning"];
            if (!string.IsNullOrEmpty(warn)) notice = warn;
        }

        public async void Send(string kind, JObject payload)
        {
            JObject c = convoy();
            JToken me = c?["members"]?.FirstOrDefault(m => (string)m["accountId"] == client.AccountId);
            if (c == null || me == null) { error = "Not in a convoy."; return; }
            var envelope = new JObject
            {
                ["session"] = (string)c["convoySessionId"], ["activity"] = "PocketCircuit", ["epoch"] = epoch,
                ["member"] = client.AccountId, ["gen"] = (long?)me["membershipGeneration"] ?? 0, ["seq"] = ++sequence,
                ["req"] = requestPrefix + "-" + (++requests), ["kind"] = kind, ["payload"] = payload ?? new JObject(),
            };
            try
            {
                await client.Request("toy.command", envelope);
                if (!error.StartsWith("The shared table")) error = "";
            }
            catch (ControlError e) when (kind == "throttle")
            {
                // Throttle refreshes are transient; a paused or re-oriented table rejects them harmlessly.
                if (e.Code != "toy_deferred") error = e.Message;
            }
            catch (Exception e)
            {
                error = e.Message;
            }
        }

        public void PredictThrottle(float value)
        {
            myThrottle = value;
            myThrottleFresh = true;
            ApplyMyThrottle();
        }

        void ApplyMyThrottle()
        {
            SlotCarState mine = table.Car(client.AccountId);
            if (mine != null && mine.Mode == SlotCarMode.Driving && !frozen) mine.Throttle = myThrottle;
        }

        /// <summary>Dead reckoning between pushes (≤ 0.5 s): the same fixed-step toy physics as the authority.</summary>
        public void Tick()
        {
            if (frozen) return;
            pendingSeconds = Math.Min(0.5, pendingSeconds + Time.unscaledDeltaTime);
            double dt = 1.0 / content.PocketCircuit.Physics.StepHz;
            SlotTrack track = table.Track;
            while (pendingSeconds >= dt)
            {
                pendingSeconds -= dt;
                foreach (SlotCarState car in table.Board.Cars)
                    if (car.Mode == SlotCarMode.Driving) SlotSim.Step(track.Lane(car.Lane), content.PocketCircuit.Physics, car);
            }
        }

        public void Leave()
        {
            client.ToyActivity -= OnActivity;
            client.ToyState -= OnOverview;
            Send("close", new JObject());
            _ = client.Request("diversion.set", new { toy = (string)null });
        }

        /// <summary>A mirror never applies commands itself; it only renders and dead-reckons the server's state.</summary>
        sealed class MirrorHost : IToyHost
        {
            public string SessionId => "mirror";
            public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            public List<string> ActiveUsers(ToyActivityId activity) => new List<string>();
            public bool IsActiveMember(string member) => true;
            public ulong NextSeed() => 1;
            public string NextId(string prefix) => prefix + "-mirror";
        }
    }
}
