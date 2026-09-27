using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Toys;
using NightSignal.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NightSignal.Toys
{
    /// <summary>
    /// Host for client-side views of Core toy state (queries such as standings, prediction and dead reckoning). It never
    /// applies commands: the authority does.
    /// </summary>
    public sealed class MirrorToyHost : IToyHost
    {
        public static readonly MirrorToyHost Instance = new MirrorToyHost();
        public string SessionId => "mirror";
        public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public List<string> ActiveUsers(ToyActivityId activity) => new List<string>();
        public bool IsActiveMember(string member) => true;
        public ulong NextSeed() => 1;
        public string NextId(string prefix) => prefix + "-mirror";
    }

    /// <summary>The answer to one toy command, from the in-process session or the control plane.</summary>
    public struct ToyAnswer
    {
        public bool Accepted;
        public string Value;
        public string Reason;

        public override string ToString() => Accepted ? "accepted" + (Value != null ? " " + Value : "") : Reason;
    }

    /// <summary>
    /// A connection to the While We Wait session for ANY diversion: Local (this process hosts the authoritative Core
    /// session) or Online (the convoy's hosted session over the control channel). Views read typed Core state and send
    /// commands; they never decide outcomes themselves.
    /// </summary>
    public abstract class ToyConnection
    {
        public abstract string Member { get; }
        public abstract bool Online { get; }
        public abstract string Status { get; }
        public abstract void Send(ToyActivityId activity, string kind, JObject payload, Action<ToyAnswer> done = null);
        /// <summary>The latest authoritative state of one activity (typed Core state object), or null before the first one.</summary>
        public abstract T State<T>(ToyActivityId activity) where T : class;
        public abstract bool Frozen(ToyActivityId activity);
        public abstract void Tick();
        public abstract void Enter(ToyActivityId activity);
        public abstract void Leave(ToyActivityId activity);

        /// <summary>Local when a Local profile is open outside an online convoy; otherwise the convoy's shared session.</summary>
        public static ToyConnection Create(ToyContent content, Front.FrontEndApp app)
        {
            Front.OnlineSession online = Front.OnlineSession.Current;
            if (app.Domain == Front.SessionDomain.Online && online != null && online.InConvoy)
                return new OnlineToyConnection(online.Client, () => online.Convoy);
            Front.LocalSession local = Front.LocalSession.Current;
            if (local?.Profile == null) return null;
            var host = new LocalToyHost(content, local.Profile.ProfileId, local.ToySnapshot(LocalToyHost.DocumentKey));
            return new LocalToyConnection(host, json => local.SaveToys(LocalToyHost.DocumentKey, LocalToyHost.DocumentSchema, json, out _));
        }
    }

    public sealed class LocalToyConnection : ToyConnection
    {
        readonly LocalToyHost host;
        readonly Func<string, bool> save;
        string status = "";

        public LocalToyConnection(LocalToyHost host, Func<string, bool> save)
        {
            this.host = host;
            this.save = save;
            host.Advance();
        }

        public override string Member => host.Member;
        public override bool Online => false;
        public override string Status => status;

        public override void Send(ToyActivityId activity, string kind, JObject payload, Action<ToyAnswer> done = null)
        {
            ToyResult r = host.Do(activity, kind, payload);
            var a = new ToyAnswer { Accepted = r.Accepted, Value = r.Value, Reason = r.Accepted ? null : r.Reason + (r.Detail != null ? ": " + r.Detail : "") };
            status = a.Accepted ? "" : a.Reason;
            done?.Invoke(a);
        }

        public override T State<T>(ToyActivityId activity) => host.Session.Activity(activity).StateObject as T;
        public override bool Frozen(ToyActivityId activity) => host.Session.Activity(activity).Run.Frozen;
        public override void Tick() => host.Advance();
        public override void Enter(ToyActivityId activity) { }

        public override void Leave(ToyActivityId activity)
        {
            host.Close(activity);
            host.Advance();
            if (!save(host.SnapshotJson())) Debug.LogWarning("[NightSignal.Toys] the Local toy table could not be saved");
        }
    }

    /// <summary>Online: Core envelope over toy.command, pushed state mirrored per activity (replaced on every push).</summary>
    public sealed class OnlineToyConnection : ToyConnection
    {
        static readonly Dictionary<ToyActivityId, string> DiversionIds = new Dictionary<ToyActivityId, string>
        {
            { ToyActivityId.CapClash, "cap-clash" }, { ToyActivityId.PitCrew, "pit-crew" }, { ToyActivityId.Greenlight, "greenlight" },
            { ToyActivityId.PocketCircuit, "pocket-circuit" }, { ToyActivityId.Canvas, "convoy-canvas" },
        };

        readonly ControlPlaneClient client;
        readonly Func<JObject> convoy;
        readonly string requestPrefix = Guid.NewGuid().ToString("N").Substring(0, 10);
        long sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10;
        int requests;
        readonly Dictionary<ToyActivityId, JObject> raw = new Dictionary<ToyActivityId, JObject>();
        readonly Dictionary<ToyActivityId, object> typed = new Dictionary<ToyActivityId, object>();
        readonly Dictionary<ToyActivityId, int> epochs = new Dictionary<ToyActivityId, int>();
        readonly Dictionary<ToyActivityId, bool> frozen = new Dictionary<ToyActivityId, bool>();
        string notice = "", error = "";

        public OnlineToyConnection(ControlPlaneClient client, Func<JObject> convoy)
        {
            this.client = client;
            this.convoy = convoy;
            client.ToyActivity += OnActivity;
            client.ToyState += OnOverview;
        }

        public override string Member => client.AccountId;
        public override bool Online => true;
        public override string Status => !string.IsNullOrEmpty(error) ? error : notice;

        public override async void Enter(ToyActivityId activity)
        {
            try
            {
                await client.Request("diversion.set", new { toy = DiversionIds[activity] });
                JToken snap = await client.Request("toy.snapshot", new { activity = activity.ToString() });
                if (snap is JObject o) OnActivity(o);
            }
            catch (Exception e)
            {
                error = "The shared toy is not available: " + e.Message;
            }
        }

        void OnActivity(JObject p)
        {
            if (p == null || !Enum.TryParse((string)p["activity"], out ToyActivityId id)) return;
            epochs[id] = (int?)p["epoch"] ?? (epochs.TryGetValue(id, out int e) ? e : 0);
            frozen[id] = (bool?)p["frozen"] ?? false;
            if (p["state"] is JObject state)
            {
                raw[id] = state;
                typed.Remove(id);
            }
            else if ((bool?)p["omitted"] == true) Refetch(id);
        }

        async void Refetch(ToyActivityId id)
        {
            try
            {
                JToken snap = await client.Request("toy.snapshot", new { activity = id.ToString() });
                if (snap is JObject o && o["state"] is JObject) OnActivity(o);
            }
            catch (Exception) { /* the next push tries again */ }
        }

        void OnOverview(JObject p)
        {
            if (p == null) return;
            bool paused = (bool?)p["pausedForEvent"] == true;
            notice = paused ? (string)p["notice"] ?? "Paused for the convoy — progress kept" : "";
            string warn = (string)p["saveWarning"] ?? (string)p["restoreWarning"] ?? (string)p["faultWarning"];
            if (!string.IsNullOrEmpty(warn)) notice = warn;
        }

        public override T State<T>(ToyActivityId activity)
        {
            if (typed.TryGetValue(activity, out object t)) return t as T;
            if (!raw.TryGetValue(activity, out JObject j)) return null;
            T value = j.ToObject<T>(JsonSerializer.CreateDefault());
            typed[activity] = value;
            return value;
        }

        public override bool Frozen(ToyActivityId activity) => frozen.TryGetValue(activity, out bool f) && f;

        public override async void Send(ToyActivityId activity, string kind, JObject payload, Action<ToyAnswer> done = null)
        {
            JObject c = convoy();
            JToken me = c?["members"]?.FirstOrDefault(m => (string)m["accountId"] == client.AccountId);
            if (c == null || me == null)
            {
                done?.Invoke(new ToyAnswer { Reason = "not in a convoy" });
                return;
            }
            var envelope = new JObject
            {
                ["session"] = (string)c["convoySessionId"], ["activity"] = activity.ToString(), ["epoch"] = epochs.TryGetValue(activity, out int e) ? e : 0,
                ["member"] = client.AccountId, ["gen"] = (long?)me["membershipGeneration"] ?? 0, ["seq"] = ++sequence,
                ["req"] = requestPrefix + "-" + (++requests), ["kind"] = kind, ["payload"] = payload ?? new JObject(),
            };
            try
            {
                JToken r = await client.Request("toy.command", envelope);
                error = "";
                done?.Invoke(new ToyAnswer { Accepted = true, Value = (string)r?["value"] });
            }
            catch (ControlError ex)
            {
                if (ex.Code != "toy_deferred") error = ex.Message;
                done?.Invoke(new ToyAnswer { Reason = ex.Message });
            }
            catch (Exception ex)
            {
                error = ex.Message;
                done?.Invoke(new ToyAnswer { Reason = ex.Message });
            }
        }

        public override void Tick() { }

        public override void Leave(ToyActivityId activity)
        {
            client.ToyActivity -= OnActivity;
            client.ToyState -= OnOverview;
            Send(activity, "close", new JObject());
            _ = client.Request("diversion.set", new { toy = (string)null });
        }
    }
}
