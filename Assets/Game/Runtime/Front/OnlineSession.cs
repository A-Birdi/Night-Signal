using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NightSignal.Content;
using NightSignal.Net;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NightSignal.Front
{
    /// <summary>
    /// The signed-in Online domain for the interactive client: the authenticated control-plane client, the player's
    /// server-owned profile (/v1/me), the control channel (pumped on the main thread) and friendly request errors.
    /// All convoy state shown in the UI is the server's snapshot; nothing here decides readiness or progression.
    /// </summary>
    public sealed class OnlineSession : IDisposable
    {
        public static OnlineSession Current { get; private set; }

        public ControlPlaneClient Client { get; }
        public JObject Me { get; private set; }
        public string LastError { get; private set; } = "";
        public string LastNotice { get; private set; } = "";
        public event Action Changed;
        float nextPing;

        OnlineSession(ControlPlaneClient client)
        {
            Client = client;
            client.ConvoyChanged += _ => Changed?.Invoke();
            client.RejoinChanged += _ => Changed?.Invoke();
            client.Notice += n => { LastNotice = (string)n?["message"] ?? ""; Changed?.Invoke(); };
            client.ConvoyClosed += c => { LastNotice = ClosedText((string)c?["reason"]); Changed?.Invoke(); };
            client.Invited += i =>
            {
                if (i == null) return;
                Invites.RemoveAll(x => (string)x["inviteId"] == (string)i["inviteId"]);
                Invites.Add(i);
                LastNotice = $"{(string)i["fromName"] ?? "A friend"} invited you to their convoy — open Friends to accept.";
                Changed?.Invoke();
            };
        }

        /// <summary>Friend invitations received this session (server-side they expire; accepting re-checks everything).</summary>
        public readonly List<JObject> Invites = new List<JObject>();

        public string AccountId => Client.AccountId;
        public JObject Convoy => Client.ConvoyState;
        public bool InConvoy => Convoy != null && Convoy["convoyId"] != null && Convoy["convoyId"].Type != JTokenType.Null;
        public bool IsLeader => InConvoy && (string)Convoy["leaderId"] == AccountId;
        public JToken MyMember => Convoy?["members"]?.FirstOrDefault(m => (string)m["accountId"] == AccountId);
        public string DisplayName => (string)(Me?["card"] as JObject)?["displayName"] ?? "Driver";
        /// <summary>The public @username, or null before one is claimed (Addendum 01 §9.2).</summary>
        public string Handle => (string)(Me?["handle"] as JObject)?["handle"];
        public string StarterCarId => Me?["starterCarId"]?.Type == JTokenType.String ? (string)Me["starterCarId"] : null;

        /// <summary>Signs in through the identity provider, loads /v1/me and opens the control channel.</summary>
        public static async Task<OnlineSession> SignIn(string baseUrl, string email, string password)
        {
            var client = new ControlPlaneClient(baseUrl);
            try
            {
                await client.SignInDev(email, password);
                var s = new OnlineSession(client);
                await s.RefreshMe();
                await client.ConnectControl(NetConfig.Build, Wire.ProtocolVersion, ContentLibrary.Load().Catalogue.ContentHash);
                Current?.Dispose();
                Current = s;
                return s;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task RefreshMe()
        {
            Me = await Client.Get("/v1/me");
            Changed?.Invoke();
        }

        /// <summary>Main-thread pump: server pushes, keep-alive ping (the server shows silent accounts as Unknown after 90 s).</summary>
        public void Tick()
        {
            Client.Pump();
            if (Time.unscaledTime >= nextPing && Client.Connected)
            {
                nextPing = Time.unscaledTime + 20f;
                _ = Request("ping", null, quiet: true);
            }
        }

        /// <summary>A control request whose failure becomes a readable message instead of an exception.</summary>
        public async Task<JToken> Request(string type, object payload = null, bool quiet = false)
        {
            try
            {
                JToken r = await Client.Request(type, payload);
                if (!quiet) LastError = "";
                return r ?? new JObject();
            }
            catch (ControlError e)
            {
                LastError = Friendly(e.Code, e.Message);
            }
            catch (TimeoutException)
            {
                LastError = "The online service did not answer in time. Try again.";
            }
            catch (Exception e)
            {
                LastError = "Connection problem: " + e.Message;
            }
            Changed?.Invoke();
            return null;
        }

        /// <summary>A REST call (friends, handle, purchases): the body on success, null with <see cref="LastError"/> set otherwise.</summary>
        public async Task<JObject> Rest(System.Net.Http.HttpMethod method, string path, object payload = null, IDictionary<string, string> headers = null)
        {
            try
            {
                (int status, JObject body) = await Client.Send(method, path, payload, headers);
                if (status >= 200 && status < 300)
                {
                    LastError = "";
                    return body;
                }
                string code = (string)body["error"] ?? status.ToString();
                LastError = code == "rate_limited" ? "Too many requests. Try again in a moment." : (string)body["message"] ?? $"The online service refused that ({status}).";
            }
            catch (Exception e)
            {
                LastError = "Connection problem: " + e.Message;
            }
            Changed?.Invoke();
            return null;
        }

        static string Friendly(string code, string message)
        {
            switch (code)
            {
                case "rate_limited": return "Please wait a moment before asking everyone again.";
                case "not_all_ready": return "Not everyone is ready yet.";
                case "stale_revision": return "The plan changed; check it and confirm again.";
                case "convoy_full": return "That convoy is full.";
                case "convoy_dormant": return "That convoy is dormant; only its former members can rejoin it.";
                default:
                    int colon = message.IndexOf(": ", StringComparison.Ordinal);
                    return colon >= 0 ? message.Substring(colon + 2) : message;
            }
        }

        static string ClosedText(string reason)
        {
            switch (reason)
            {
                case "kicked": return "You were removed from the convoy by its leader.";
                case "disbanded": return "The leader ended the convoy.";
                case "disconnected": return "You were disconnected from the convoy.";
                default: return "You left the convoy.";
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            if (Current == this) Current = null;
        }
    }
}
