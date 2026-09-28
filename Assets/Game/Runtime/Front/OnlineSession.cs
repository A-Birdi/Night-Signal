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
        /// <summary>The latest server notice; a time-limited one (a meet invitation's held place) clears when it lapses.</summary>
        public string LastNotice => Time.unscaledTime < noticeUntil ? lastNotice : "";
        string lastNotice = "";
        float noticeUntil = float.MaxValue;

        void SetNotice(string text, float seconds = 0f)
        {
            lastNotice = text ?? "";
            noticeUntil = seconds > 0f ? Time.unscaledTime + seconds : float.MaxValue;
        }

        /// <summary>Clears the notice (e.g. the invitation it announced was used).</summary>
        public void ClearNotice() => SetNotice("");
        public event Action Changed;
        float nextPing;

        OnlineSession(ControlPlaneClient client)
        {
            Client = client;
            client.ConvoyChanged += _ => Changed?.Invoke();
            client.RejoinChanged += _ => Changed?.Invoke();
            client.Notice += n => { SetNotice((string)n?["message"]); Changed?.Invoke(); };
            client.ConvoyClosed += c => { SetNotice(ClosedText((string)c?["reason"])); Changed?.Invoke(); };
            client.MeetInvited += i =>
            {
                if (i == null) return;
                MeetInvites.RemoveAll(x => (string)x["fromAccountId"] == (string)i["fromAccountId"]);
                MeetInvites.Add(i);
                SetNotice($"{(string)i["fromName"] ?? "A friend"} is holding a place for you at their meet (30 s) — open Friends to join.", 30f);
                Changed?.Invoke();
            };
            client.Invited += i =>
            {
                if (i == null) return;
                Invites.RemoveAll(x => (string)x["inviteId"] == (string)i["inviteId"]);
                Invites.Add(i);
                SetNotice($"{(string)i["fromName"] ?? "A friend"} invited you to their convoy — open Friends to accept.");
                Changed?.Invoke();
            };
        }

        /// <summary>Friend invitations received this session (server-side they expire; accepting re-checks everything).</summary>
        public readonly List<JObject> Invites = new List<JObject>();
        /// <summary>Invitations to a friend's meet (each holds a bay for 30 s; joining re-checks everything).</summary>
        public readonly List<JObject> MeetInvites = new List<JObject>();

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
                await s.CheckSideContent();
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

        /// <summary>
        /// Set when this client's While We Wait documents differ from the control plane's: the shared tables would mirror
        /// different data, so they stay closed online (Local toys are unaffected). Null when they match or cannot be told.
        /// </summary>
        public string ToyMismatch { get; private set; }

        /// <summary>
        /// Set when this client's customization.json differs from the control plane's (published as customizationContentHash):
        /// liveries could validate differently, so the online Appearance screen previews but does not apply. Null when they match
        /// or cannot be told.
        /// </summary>
        public string CustomizationMismatch { get; private set; }

        /// <summary>The non-race documents (toys, appearance) are published with their own hashes: compare ours with the server's.</summary>
        async Task CheckSideContent()
        {
            try
            {
                (int status, JObject health) = await Client.GetWithStatus("/healthz");
                string server = status == 200 ? (string)health["toyContentHash"] : null;
                string serverLooks = status == 200 ? (string)health["customizationContentHash"] : null;
                string localLooks = ContentLibrary.Load()?.CustomizationHash;
                CustomizationMismatch = serverLooks != null && localLooks != null && serverLooks != localLooks
                    ? "This game version has different appearance data than the server — you can try things on, but applying needs an update."
                    : null;
                string local = ContentLibrary.Load()?.Toys?.ContentHash;
                ToyMismatch = server != null && local != null && server != local
                    ? "The shared toys on this server use different data than this game version — update the game to join them."
                    : null;
                if (ToyMismatch != null) Debug.LogWarning($"[NightSignal.Toys] toy content differs: server {server.Substring(0, 12)}, client {local.Substring(0, 12)}");
            }
            catch (Exception)
            {
                ToyMismatch = null; // unknown: the tables stay available; the server still validates every command
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
