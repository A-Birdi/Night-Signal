using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using NightSignal.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Friends (Addendum 01 §9–10): your @username, add a friend by username, incoming/outgoing requests, convoy
    /// invitations you received, and the friend list with the server's presence and the actions it allows (invite into
    /// your convoy, join a discoverable convoy, rejoin). Everything shown is the server's answer; relationships are keyed
    /// by account IDs and every call is idempotent server-side.
    /// </summary>
    public sealed class FriendsScreen : UIScreen
    {
        public override string ScreenName => "Friends";
        public override string MusicCue => "MUS_MENU_B";

        const int FriendRows = 8, RequestRows = 4, InviteRows = 3;

        sealed class Line
        {
            public TextMeshProUGUI Label;
            public Button A, B;
            public GameObject Root;
        }

        TextMeshProUGUI handleLine, error, friendsTitle, requestsTitle, invitesTitle;
        TMP_InputField handleField, addField;
        Button claim, add;
        readonly List<Line> friends = new List<Line>(), requests = new List<Line>(), invites = new List<Line>();
        JObject graph;
        float nextRefresh;
        bool busy, dirty = true;
        string confirmRemove; // a removal asks once more before it is sent
        // The outcome of the player's last action. Kept until the next action: list refreshes must not wipe an error.
        string message = "";

        OnlineSession S => OnlineSession.Current;

        /// <summary>The latest friend graph from the server (tours read it).</summary>
        public JObject Graph => graph;

        protected override void OnBuild(RectTransform root)
        {
            UIFactory.Panel("Backdrop", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.035f, 0.04f, 0.05f, 0.9f));

            // Left: you, adding friends, requests and invitations.
            Image left = UIFactory.Panel("Social", root, new Vector2(0, 0), new Vector2(0.44f, 1), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.95f));
            RectTransform lcol = UIFactory.Column("SocialColumn", left.transform, new Vector2(0, 0.03f), new Vector2(1, 0.95f), new Vector2(48, 0), new Vector2(-24, 0), 10f);
            UIFactory.Row("Heading", lcol, "FRIENDS", SignalTheme.Heading, SignalTheme.Label, 700, 0, true);
            handleLine = UIFactory.Row("Handle", lcol, "", SignalTheme.Body, SignalTheme.Label, 740, 34);
            handleLine.richText = true;
            handleField = UIFactory.InputField("HandleField", lcol, "Choose a username (3–20 letters, digits, _)", false, 20, 560, 52);
            claim = UIFactory.Button("ClaimHandle", lcol, "Claim Username", ClaimHandle, 560, 50);
            addField = UIFactory.InputField("FriendHandle", lcol, "@username", false, 21, 560, 52);
            add = UIFactory.Button("SendFriendRequest", lcol, "Send Friend Request", SendRequest, 560, 50);
            error = UIFactory.Row("Error", lcol, "", SignalTheme.Small, SignalTheme.Caution, 740, 48);
            requestsTitle = UIFactory.Row("RequestsTitle", lcol, "REQUESTS", SignalTheme.Small, SignalTheme.LabelDim, 740, 26);
            for (int i = 0; i < RequestRows; i++) requests.Add(LineRow("Request" + i, lcol, 740));
            invitesTitle = UIFactory.Row("InvitesTitle", lcol, "INVITATIONS", SignalTheme.Small, SignalTheme.LabelDim, 740, 26);
            for (int i = 0; i < InviteRows; i++) invites.Add(LineRow("Invite" + i, lcol, 740));

            // Right: the friend list.
            RectTransform col = UIFactory.Column("FriendList", root, new Vector2(0.46f, 0.03f), new Vector2(0.98f, 0.95f), Vector2.zero, Vector2.zero, 10f);
            friendsTitle = UIFactory.Row("FriendsTitle", col, "", SignalTheme.Body, SignalTheme.Label, 980, 40);
            friendsTitle.richText = true;
            for (int i = 0; i < FriendRows; i++) friends.Add(LineRow("Friend" + i, col, 980));
            UIFactory.Button("RefreshFriends", col, "Refresh", () => { nextRefresh = 0; }, 360, 48);
            UIFactory.Button("Back", col, "Back", () => App.Router.Back(), 360, 48);
        }

        /// <summary>One list line: text on the left, up to two action buttons on the right.</summary>
        static Line LineRow(string name, Transform parent, float width)
        {
            RectTransform rt = UIFactory.Rect(name, parent, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            rt.sizeDelta = new Vector2(width, 52);
            var line = new Line { Root = rt.gameObject };
            line.Label = UIFactory.Label("Label", rt, "", SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.MidlineLeft);
            line.Label.rectTransform.anchorMin = new Vector2(0, 0);
            line.Label.rectTransform.anchorMax = new Vector2(1, 1);
            line.Label.rectTransform.offsetMin = Vector2.zero;
            line.Label.rectTransform.offsetMax = new Vector2(-392, 0);
            line.Label.richText = true;
            line.Label.textWrappingMode = TextWrappingModes.Normal;
            line.A = UIFactory.Button(name + "A", rt, "", null, 188, 48);
            line.B = UIFactory.Button(name + "B", rt, "", null, 188, 48);
            Place(line.A, width - 384);
            Place(line.B, width - 188);
            return line;
        }

        static void Place(Button b, float x)
        {
            var r = (RectTransform)b.transform;
            r.anchorMin = r.anchorMax = new Vector2(0, 0.5f);
            r.pivot = new Vector2(0, 0.5f);
            r.anchoredPosition = new Vector2(x, 0);
        }

        static void Bind(Button b, string label, System.Action action)
        {
            b.gameObject.SetActive(label != null);
            if (label == null) return;
            b.GetComponentInChildren<TextMeshProUGUI>().text = label;
            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(() => action());
        }

        public override void OnShow()
        {
            if (S == null)
            {
                App.Router.Show(App.SignIn, false);
                return;
            }
            S.Changed -= MarkDirty;
            S.Changed += MarkDirty;
            confirmRemove = null;
            nextRefresh = 0;
            dirty = true;
        }

        public override void OnHide()
        {
            if (S != null) S.Changed -= MarkDirty;
        }

        void MarkDirty() => dirty = true;

        public override Selectable DefaultFocus => addField;

        /// <summary>The outcome shown for the last action ("" when it succeeded quietly).</summary>
        public string Message => message;

        /// <summary>True while a request is in flight (clicks are ignored then).</summary>
        public bool Busy => busy;

        public override void Tick()
        {
            if (S == null) return;
            if (Time.unscaledTime >= nextRefresh && !busy)
            {
                nextRefresh = Time.unscaledTime + 8f; // presence changes are shown within a few seconds
                _ = Refresh();
            }
            if (dirty)
            {
                dirty = false;
                Render();
            }
        }

        async System.Threading.Tasks.Task Refresh()
        {
            JObject g = await S.Rest(HttpMethod.Get, "/v1/friends");
            if (g != null) graph = g;
            dirty = true;
        }

        void Render()
        {
            string handle = S.Handle;
            handleLine.text = handle != null
                ? $"You are <b>@{Esc(handle)}</b>  <size=80%>({Esc(S.DisplayName)})</size>"
                : "<color=#F2A541>Choose a username so friends can find you.</color>";
            handleField.gameObject.SetActive(handle == null);
            claim.gameObject.SetActive(handle == null);
            error.text = graph == null && !string.IsNullOrEmpty(S.LastError) ? S.LastError : message;

            // Requests: incoming first (Accept / Decline), then your outgoing ones (Cancel).
            var reqs = new List<(string Text, string A, System.Action DoA, string B, System.Action DoB)>();
            foreach (JToken p in (graph?["incoming"] as JArray) ?? new JArray())
            {
                string id = (string)p["accountId"];
                reqs.Add(($"{Who(p)} wants to be friends", "Accept", () => Change(HttpMethod.Post, $"/v1/friends/requests/{id}/accept"),
                    "Decline", () => Change(HttpMethod.Post, $"/v1/friends/requests/{id}/decline")));
            }
            foreach (JToken p in (graph?["outgoing"] as JArray) ?? new JArray())
            {
                string id = (string)p["accountId"];
                reqs.Add(($"<color=#9A968D>Request sent to</color> {Who(p)}", "Cancel", () => Change(HttpMethod.Delete, $"/v1/friends/requests/{id}"), null, null));
            }
            requestsTitle.gameObject.SetActive(reqs.Count > 0);
            for (int i = 0; i < requests.Count; i++)
            {
                bool on = i < reqs.Count;
                requests[i].Root.SetActive(on);
                if (!on) continue;
                requests[i].Label.text = reqs[i].Text;
                Bind(requests[i].A, reqs[i].A, reqs[i].DoA);
                Bind(requests[i].B, reqs[i].B, reqs[i].DoB);
            }

            // Convoy invitations and meet invitations (a place held for 30 s) received this session.
            List<JObject> inv = S.Invites.ToList();
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S.MeetInvites.RemoveAll(x => ((long?)x["untilMs"] ?? 0) < nowMs);
            List<JObject> meetInv = S.MeetInvites.ToList();
            invitesTitle.gameObject.SetActive(inv.Count + meetInv.Count > 0);
            for (int i = 0; i < invites.Count; i++)
            {
                bool on = i < inv.Count + meetInv.Count;
                invites[i].Root.SetActive(on);
                if (!on) continue;
                if (i >= inv.Count)
                {
                    JObject mi = meetInv[i - inv.Count];
                    string from = (string)mi["fromAccountId"];
                    long left = (((long?)mi["untilMs"] ?? nowMs) - nowMs) / 1000;
                    invites[i].Label.text = $"<b>{Esc((string)mi["fromName"])}</b> holds a place for you at the meet  <size=80%>· bay {(int?)mi["bay"]} · {left} s</size>";
                    Bind(invites[i].A, "Join meet", () => { S.MeetInvites.Remove(mi); App.StartOnlineMeet("friend", from, this); });
                    Bind(invites[i].B, "Decline", () => { S.MeetInvites.Remove(mi); dirty = true; });
                    continue;
                }
                JObject x = inv[i];
                string inviteId = (string)x["inviteId"];
                invites[i].Label.text = $"<b>{Esc((string)x["fromName"])}</b> invited you  <size=80%>· {Esc((string)x["leaderName"])}'s convoy {(int?)x["members"]}/{(int?)x["maxMembers"]}</size>";
                Bind(invites[i].A, "Join", () => AcceptInvite(inviteId));
                Bind(invites[i].B, "Decline", () => DeclineInvite(inviteId));
            }

            // Friends with presence (online first, as the server orders them).
            JArray list = (graph?["friends"] as JArray) ?? new JArray();
            int n = list.Count;
            friendsTitle.text = graph == null ? "Loading friends…" : n == 0 ? "No friends yet — send a request by @username." : $"<b>{n}</b> friend{(n == 1 ? "" : "s")}";
            bool inConvoy = S.InConvoy;
            for (int i = 0; i < friends.Count; i++)
            {
                bool on = i < n;
                friends[i].Root.SetActive(on);
                if (!on) continue;
                JToken f = list[i];
                string id = (string)f["accountId"];
                string status = (string)f["status"] ?? "Unknown";
                bool together = (bool?)f["inYourConvoy"] == true;
                string convoyId = (string)f["convoyId"];
                string where = together ? "  <color=#3EC6D8>in your convoy</color>" : convoyId != null ? "  <size=80%>in a convoy</size>" : "";
                friends[i].Label.text = $"{Who(f)}  <size=80%><color={StatusColour(status)}>{StatusText(status)}</color>{Rank(f)}</size>{where}";

                string aLabel = null;
                System.Action aDo = null;
                if ((bool?)f["canInvite"] == true) { aLabel = "Invite"; aDo = () => Invite(id); }
                else if ((bool?)f["canRejoin"] == true) { aLabel = "Rejoin"; aDo = () => Control("convoy.rejoin", null); }
                else if (!inConvoy && convoyId != null) { aLabel = "Join"; aDo = () => Control("convoy.join", new { convoyId }); }
                else if (status == "AtMeet") { aLabel = "Join meet"; aDo = () => App.StartOnlineMeet("friend", id, this); }
                Bind(friends[i].A, aLabel, aDo);
                Bind(friends[i].B, confirmRemove == id ? "Confirm" : "Remove", () =>
                {
                    if (confirmRemove != id)
                    {
                        confirmRemove = id;
                        dirty = true;
                        return;
                    }
                    confirmRemove = null;
                    Change(HttpMethod.Delete, $"/v1/friends/{id}");
                });
            }
        }

        static string Who(JToken p)
        {
            string h = (string)p["handle"];
            string d = (string)p["displayName"];
            return h != null ? $"<b>@{Esc(h)}</b>" + (d != null ? $" <size=80%>{Esc(d)}</size>" : "") : $"<b>{Esc(d ?? "Driver")}</b>";
        }

        static string Rank(JToken f)
        {
            string r = (string)(f["rank"] as JObject)?["name"];
            return string.IsNullOrEmpty(r) ? "" : "  ·  " + Esc(r);
        }

        static string StatusText(string s)
        {
            switch (s)
            {
                case "AtMeet": return "At the meet";
                case "Preparing": return "Preparing an event";
                case "Loading": return "Loading a race";
                case "Unknown": return "Status unknown";
                default: return s;
            }
        }

        static string StatusColour(string s) =>
            s == "Available" ? "#3EC6D8" : s == "Offline" || s == "Unknown" ? "#6F6C66" : s == "Away" ? "#F2A541" : "#D8D4CB";

        static string Esc(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");

        // ------------------------------------------------------------------ actions

        async void ClaimHandle()
        {
            if (busy) return;
            busy = true;
            try
            {
                JObject r = await S.Rest(HttpMethod.Put, "/v1/me/handle", new { handle = handleField.text.Trim().TrimStart('@') });
                message = r != null ? "" : S.LastError;
                if (r != null)
                {
                    handleField.text = "";
                    await S.RefreshMe();
                }
            }
            finally { busy = false; dirty = true; }
        }

        async void SendRequest()
        {
            if (busy) return;
            string h = addField.text.Trim().TrimStart('@');
            if (h.Length == 0) return;
            busy = true;
            try
            {
                JObject r = await S.Rest(HttpMethod.Post, "/v1/friends/requests", new { handle = h });
                message = r == null ? S.LastError : (string)r["state"] == "friends" ? $"You and @{h} are now friends." : $"Request sent to @{h}.";
                if (r != null) addField.text = "";
                await Refresh();
            }
            finally { busy = false; dirty = true; }
        }

        async void Change(HttpMethod method, string path)
        {
            if (busy) return;
            busy = true;
            try
            {
                message = await S.Rest(method, path) != null ? "" : S.LastError;
                await Refresh();
            }
            finally { busy = false; dirty = true; }
        }

        async void Invite(string accountId)
        {
            if (busy) return;
            busy = true;
            try
            {
                JToken r = await S.Request("convoy.invite.friend", new { accountId });
                message = r == null ? S.LastError : (bool?)r["delivered"] == true ? "Invitation sent." : "Invitation saved — they will see it when they come online.";
                await Refresh();
            }
            finally { busy = false; }
        }

        async void AcceptInvite(string inviteId)
        {
            if (busy) return;
            busy = true;
            try
            {
                JToken r = await S.Request("convoy.join", new { inviteId });
                message = r != null ? "" : S.LastError;
                S.Invites.RemoveAll(x => (string)x["inviteId"] == inviteId);
                if (r != null) App.Router.Show(App.Convoy, false);
            }
            finally { busy = false; dirty = true; }
        }

        async void DeclineInvite(string inviteId)
        {
            if (busy) return;
            busy = true;
            try
            {
                message = await S.Request("convoy.invite.decline", new { inviteId }) != null ? "" : S.LastError;
                S.Invites.RemoveAll(x => (string)x["inviteId"] == inviteId);
            }
            finally { busy = false; dirty = true; }
        }

        async void Control(string type, object payload)
        {
            if (busy) return;
            busy = true;
            try
            {
                JToken r = await S.Request(type, payload);
                message = r != null ? "" : S.LastError;
                if (r != null) App.Router.Show(App.Convoy, false);
            }
            finally { busy = false; dirty = true; }
        }
    }
}
