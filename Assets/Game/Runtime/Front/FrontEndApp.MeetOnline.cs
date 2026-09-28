using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Meet;
using NightSignal.Meet;
using NightSignal.Net;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        bool meetLeftForRace;
        /// <summary>True from joining an online meet until its menus are back (or the race took over).</summary>
        bool onlineMeetRunning;

        /// <summary>
        /// The meet a race took the player from (spec §12: after the race, a clear option to return to the prior meet): the
        /// request kind ("convoy", "friend" or "public") and the friend for a friend's meet; null when there is none.
        /// </summary>
        public string ReturnMeetKind { get; private set; }
        string returnMeetFriend;

        /// <summary>Back to the meet the race took the player from; the server allocates the bay afresh.</summary>
        public void ReturnToMeet(UIScreen from)
        {
            if (ReturnMeetKind == null) return;
            StartOnlineMeet(ReturnMeetKind, returnMeetFriend, from);
        }

        /// <summary>Visits the meet online (kind: public | convoy | friend) with the room server; back to <paramref name="returnTo"/> on leaving.</summary>
        public void StartOnlineMeet(string kind, string friendAccountId, UIScreen returnTo) => StartCoroutine(RunOnlineMeet(kind, friendAccountId, returnTo));

        IEnumerator RunOnlineMeet(string kind, string friendAccountId, UIScreen returnTo)
        {
            OnlineSession s = OnlineSession.Current;
            if (s == null || ActiveMeet != null) yield break;
            meetLeftForRace = false;
            onlineMeetRunning = true;
            ReturnMeetKind = null;
            returnMeetFriend = null;
            var owned = new HashSet<string>(((s.Me?["music"] as JObject)?["owned"] as JArray ?? new JArray()).Select(m => (string)m["cueId"]));
            string instance = s.Convoy?["members"] is JArray members
                ? (string)members.OfType<JObject>().FirstOrDefault(m => (string)m["accountId"] == s.AccountId)?["loadout"]?["instanceId"]
                : null;
            Canvas.gameObject.SetActive(false);
            if (backdropCamera != null) backdropCamera.SetActive(false);
            AsyncOperation load = SceneManager.LoadSceneAsync("Meet", LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            yield return null;
            var go = new GameObject("Meet");
            ActiveMeet = go.AddComponent<MeetSession>();
            ActiveMeet.Net = new MeetNet(s, kind, friendAccountId, instance);
            ActiveMeet.PlayerName = s.DisplayName;
            ActiveMeet.PlayerLook = s.CardLook; // the Player Card's look (null: the default look from the name)
            ActiveMeet.OwnsCue = id => owned.Contains(id);
            _ = s.Request("presence.set", new { presence = "AtMeet" }, quiet: true);
            while (ActiveMeet != null && !ActiveMeet.ExitRequested && !meetLeftForRace) yield return null;
            if (meetLeftForRace)
            {
                onlineMeetRunning = false;
                yield break; // the race flow owns the screen now (RunOnlineRace)
            }
            if (ActiveMeet != null) Destroy(ActiveMeet.gameObject);
            ActiveMeet = null;
            _ = s.Request("presence.set", new { presence = "InMenus" }, quiet: true);
            Canvas.gameObject.SetActive(true);
            yield return LoadBackdrop();
            Router.Show(returnTo ?? Convoy, false);
            onlineMeetRunning = false;
        }

        /// <summary>Called when the convoy's event allocates while at the meet: leave the room safely, the race takes over.</summary>
        void LeaveMeetForRace()
        {
            if (ActiveMeet == null) return;
            meetLeftForRace = true;
            MeetNet net = ActiveMeet.Net;
            // A convoy room is found again by the convoy; a friend's room by the friend; otherwise any public instance.
            ReturnMeetKind = (string)net?.State?["kind"] == "convoy" ? "convoy" : net?.FriendAccountId != null ? "friend" : "public";
            returnMeetFriend = ReturnMeetKind == "friend" ? net.FriendAccountId : null;
            ActiveMeet.LeaveForRace();
            ActiveMeet = null; // the meet scene unloads with the race scene load
        }

        /// <summary>
        /// Convoy meet evidence (<c>-nsMeetTourConvoy host|guest</c>, <c>-nsDevAccount N</c>; the two accounts are friends): the
        /// host creates a convoy the guest joins, proposes Campaign S01; both go to the Convoy Meet, see the compact convoy
        /// header and answer Event Ready from the meet menu; the guest leaves, the host invites it back with a held place
        /// and the guest joins the friend's meet from the Friends screen. With <c>-nsMeetTourConvoyRace</c> (a game server is
        /// running) the leader then starts the event from the meet menu, the allocation takes both out of the meet into the
        /// race (validator autopilot), and after it both use Back to the Meet and meet again. Automation over real sockets.
        /// </summary>
        IEnumerator MeetTourConvoy(string role)
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "meet-convoy"));
            string share = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "NetRuns", "meet-convoy"));
            System.IO.Directory.CreateDirectory(dir);
            System.IO.Directory.CreateDirectory(share);
            string codeFile = System.IO.Path.Combine(share, "convoy-code.txt");
            var failures = new List<string>();
            void Note(string n) => Debug.Log($"[NightSignal.MeetConvoy:{role}] {n}");
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            IEnumerator Snap(string name)
            {
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"{role}-{name}.png"));
                yield return new WaitForEndOfFrame();
                yield return null;
            }
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable || !b.gameObject.activeInHierarchy) { Fail("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }
            bool Interactable(string name) => GameObject.Find(name)?.GetComponent<Button>() is Button b && b.interactable && b.gameObject.activeInHierarchy;
            IEnumerator Until(Func<bool> condition, float seconds, string what)
            {
                float until = Time.realtimeSinceStartup + seconds;
                while (!condition() && Time.realtimeSinceStartup < until) yield return null;
                if (!condition()) Fail("timed out: " + what + (string.IsNullOrEmpty(OnlineSession.Current?.LastError) ? "" : " (" + OnlineSession.Current.LastError + ")"));
            }
            OnlineSession S() => OnlineSession.Current;
            JObject State() => S()?.Convoy;
            bool AllMembers(string flag) => (State()?["members"] as JArray)?.All(m => (bool?)m[flag] == true) == true;
            NetConfig cfg = NetConfig.FromCommandLine();
            JToken account = JObject.Parse(System.IO.File.ReadAllText(cfg.DevSeedFile))["accounts"][cfg.DevAccount];
            bool host = role == "host";
            bool race = Array.IndexOf(Environment.GetCommandLineArgs(), "-nsMeetTourConvoyRace") >= 0;
            if (race) OnlineAutopilot = true;
            if (host && System.IO.File.Exists(codeFile)) System.IO.File.Delete(codeFile);
            string ReadCode()
            {
                try { return System.IO.File.Exists(codeFile) ? System.IO.File.ReadAllText(codeFile).Trim() : ""; }
                catch (System.IO.IOException) { return ""; }
            }

            yield return new WaitForSeconds(3f);
            Click("OnlineLogin");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("Email").GetComponent<TMP_InputField>().text = (string)account["email"];
            GameObject.Find("Password").GetComponent<TMP_InputField>().text = (string)account["devOnlyPassword"];
            Click("SignIn");
            yield return Until(() => Router.Current == Convoy && S() != null && S().Me != null, 25f, "signed in");
            if (S() == null) { Finish(); yield break; }
            yield return new WaitForSeconds(1.5f);
            if (S().StarterCarId == null) { Click("ChooseStarter"); yield return Until(() => S().StarterCarId != null, 10f, "starter chosen"); }
            if (S().InConvoy) { Click("Leave"); yield return Until(() => !S().InConvoy, 10f, "left an earlier convoy"); }

            // The host's Player Card: a starting look, locs, saved through the screen (the guest checks it at the meet).
            const int CardPreset = 3; // Look 3: athletic, hoodie
            if (host)
            {
                Router.Show(PlayerCard, true);
                yield return Until(() => Router.Current == PlayerCard, 10f, "the Player Card");
                yield return new WaitForSeconds(1.2f);
                PlayerCard.ChooseStart(CardPreset);
                PlayerCard.SetField("Hair", Array.IndexOf(Characters.CharacterVocabulary.Hair, "locs"));
                yield return new WaitForSeconds(1.5f);
                yield return Snap("00a-player-card");
                // The card's style (free items), then back to the look.
                PlayerCard.ShowSection(1);
                PlayerCard.SetStyle(new Core.Customization.CardStyle
                {
                    Background = "tea-rows", Frame = "double", Motif = "lantern", Title = "night-driver", Layout = "standard", Region = "JP",
                    PreferredCar = S().StarterCarId ?? "",
                });
                yield return new WaitForSeconds(1.2f);
                yield return Snap("00a2-card-style");
                Click("SaveCard");
                yield return Until(() => !PlayerCard.Busy && PlayerCard.Status.StartsWith("Saved"), 15f, "card saved (" + PlayerCard.Status + ")");
                Characters.CharacterLook saved = S().CardLook;
                Note($"card look saved: {saved?.Hair} {saved?.Outfit} {saved?.Build}; status '{PlayerCard.Status}'");
                if (saved?.Hair != "locs" || saved.Outfit != Characters.PlayerLooks.Presets[CardPreset - 1].Outfit) Fail("the saved card look is not the chosen one");
                Core.Customization.CardStyle savedStyle = (S().Me?["card"] as JObject)?["style"] is JObject st
                    ? Core.Customization.CardStyle.Parse(st.ToString(Newtonsoft.Json.Formatting.None)) : null;
                Note($"card style saved: {savedStyle?.Canonical() ?? "none"}");
                if (savedStyle == null || !savedStyle.ContentEquals(PlayerCard.Style)) Fail("the saved card style is not the chosen one");
                yield return new WaitForSeconds(1.5f);
                yield return Snap("00b-player-card-saved");
                PlayerCard.SavePreview(System.IO.Path.Combine(dir, "host-00c-card-preview.png"));
                Click("Back");
                yield return Until(() => Router.Current == Convoy, 10f, "back at the convoy screen");
                yield return new WaitForSeconds(1f);
            }

            // The convoy: the host creates it and shares a code; the guest joins with the code.
            if (host)
            {
                Click("CreatePrivate");
                yield return Until(() => S().InConvoy, 10f, "convoy created");
                System.Threading.Tasks.Task<JToken> inv = S().Request("convoy.invite.create");
                while (!inv.IsCompleted) yield return null;
                // Written aside and moved into place, so the guest never reads a half-written file.
                System.IO.File.WriteAllText(codeFile + ".tmp", (string)inv.Result?["code"] ?? "");
                System.IO.File.Move(codeFile + ".tmp", codeFile);
                yield return Until(() => (State()?["members"] as JArray)?.Count >= 2, 60f, "the guest joined");
                yield return Until(() => S().MyMember?["carId"]?.Type == JTokenType.String, 10f, "a loadout in the convoy");
                yield return new WaitForSeconds(1f);
                yield return Until(() => !Convoy.Busy, 10f, "request settled");
                Convoy.SelectIntent(0); // Campaign · Normal (S01 is open to everyone)
                Click("ProposeIntent");
                yield return Until(() => State()?["intent"]?.Type == JTokenType.Object, 20f, "intent set");
                if ((bool?)S().MyMember?["modeReady"] != true && Interactable("ModeReady")) Click("ModeReady");
                yield return Until(() => AllMembers("modeReady"), 60f, "both mode ready");
                yield return new WaitForSeconds(0.8f);
                Click("EnterMode");
                yield return Until(() => Interactable("ProposeEvent"), 30f, "propose available");
                Click("ProposeEvent");
                yield return Until(() => State()?["eventProposal"]?.Type == JTokenType.Object, 25f, "event proposed");
            }
            else
            {
                yield return Until(() => ReadCode().Length > 0, 60f, "the host's convoy code");
                TMP_InputField codeField = GameObject.Find("InviteCode")?.GetComponent<TMP_InputField>();
                if (codeField != null) codeField.text = ReadCode();
                else Fail("no invite code field");
                Click("JoinCode");
                yield return Until(() => S().InConvoy, 15f, "joined the convoy");
                yield return Until(() => State()?["intent"]?.Type == JTokenType.Object && Interactable("ModeReady"), 90f, "the host proposed a mode");
                Click("ModeReady");
                yield return Until(() => State()?["eventProposal"]?.Type == JTokenType.Object, 90f, "the host proposed an event");
            }

            // Both at the convoy meet: the header shows the convoy; Event Ready from the meet menu.
            yield return new WaitForSeconds(host ? 0f : 6f);
            yield return Until(() => Interactable("MeetConvoy"), 15f, "Convoy Meet available");
            Click("MeetConvoy");
            yield return Until(() => ActiveMeet != null && ActiveMeet.Ready, 40f, "at the convoy meet");
            MeetSession m = ActiveMeet;
            if (m == null) { Finish(); yield break; }
            if ((string)m.Net.State?["kind"] != "convoy") Fail("not a convoy room: " + (string)m.Net.State?["kind"]);
            string room = m.Net.RoomId;
            yield return Until(() => m.RemoteCount >= 1 && (m.Net.State["members"] as JArray).Count(x => (string)x["state"] == "present") >= 2, 60f, "both at the convoy meet");
            int bayA = m.PlayerBay;
            if (host)
            {
                // CH61 online: arrived, then the own car inspected beside it — the room checks the server-held position.
                m.Open("own-car");
                yield return new WaitForSeconds(0.5f);
                m.ClosePanel();
                bool HasCh61() => (S().Me?["challengesCompleted"] as JArray)?.Any(c => (string)c == Core.Meet.MeetTouring.FirstParking) == true;
                float ch61Until = Time.realtimeSinceStartup + 10f;
                while (!HasCh61() && Time.realtimeSinceStartup < ch61Until)
                {
                    System.Threading.Tasks.Task refresh = S().RefreshMe();
                    while (!refresh.IsCompleted) yield return null;
                    if (!HasCh61()) yield return new WaitForSeconds(1f);
                }
                Note($"CH61 on the account: {HasCh61()} (ribbon notice this visit: {m.Log.Any(l => l.StartsWith("challenge CH61"))})");
                if (!HasCh61()) Fail("CH61 was not granted by the meet room");
            }
            if (!host)
            {
                // The host's avatar is built from their Player Card look, replicated by the room.
                string hostAccount = (m.Net.State["members"] as JArray).OfType<JObject>().Select(x => (string)x["accountId"]).FirstOrDefault(a => a != S().AccountId);
                Characters.CharacterLook hl = m.RemoteLook(hostAccount);
                Note($"the host's avatar look: {(hl == null ? "default" : hl.Hair + " " + hl.Outfit + " " + hl.Build)}");
                if (hl?.Hair != "locs" || hl.Outfit != Characters.PlayerLooks.Presets[CardPreset - 1].Outfit) Fail("the host's card look did not reach the meet");
                // The host's public driver card, from their car at the meet (local UI; the host is not interrupted).
                m.InspectRemoteCar(hostAccount);
                yield return new WaitForSeconds(0.6f);
                m.Hud.PanelButtons.FirstOrDefault(b => b.GetComponentInChildren<TextMeshProUGUI>()?.text.Contains("driver card") == true)?.onClick.Invoke();
                yield return Until(() => m.Hud.PanelOpen && m.Hud.PanelTitle == "Driver card", 10f, "the host's driver card");
                yield return new WaitForSeconds(0.8f);
                Note("host's driver card: " + m.Hud.PanelBody.Replace("\n", " | "));
                if (!m.Hud.PanelBody.Contains("Rank:") || !m.Hud.PanelBody.Contains("Campaign: Normal")) Fail("the driver card is incomplete");
                Core.Customization.CardStyle seen = Core.Customization.CardStyle.Parse(m.ViewedCardStyle ?? "");
                Note($"host's card style as drawn here: {m.ViewedCardStyle}; card shown {m.Hud.Card?.Root.gameObject.activeSelf == true}");
                if (seen == null || seen.Background != "tea-rows" || seen.Frame != "double" || seen.Motif != "lantern" || seen.Title != "night-driver" ||
                    seen.Region != "JP" || seen.PreferredCar.Length == 0 || m.Hud.Card?.Shown?.ContentEquals(seen) != true)
                    Fail("the host's card style did not reach the guest's view");
                yield return Snap("01a-host-driver-card");
                m.ClosePanel();
                yield return new WaitForSeconds(0.4f);
            }
            yield return new WaitForSeconds(host ? 1f : 4f);
            m.OpenMeetMenu();
            yield return new WaitForSeconds(0.6f);
            Button ready = m.Hud.PanelButtons.FirstOrDefault(b => b.GetComponentInChildren<TextMeshProUGUI>()?.text.Contains("Event Ready") == true);
            if (ready == null) Fail("no Event Ready in the meet menu");
            else ready.onClick.Invoke();
            yield return Until(() => AllMembers("eventReady"), 60f, "both event ready (answered from the meet)");
            yield return new WaitForSeconds(1f);
            yield return Snap("01-convoy-header-ready");
            Note($"room {room} bay {bayA + 1}; convoy event ready: {AllMembers("eventReady")}");

            // A friend's meet by invitation: the guest leaves, the host invites it back, the guest joins from Friends.
            string otherId = (m.Net.State["members"] as JArray).OfType<JObject>().Select(x => (string)x["accountId"]).FirstOrDefault(a => a != S().AccountId);
            if (!host)
            {
                m.Leave();
                yield return Until(() => ActiveMeet == null && !onlineMeetRunning && Router.Current == Convoy, 20f, "left the meet");
                yield return Until(() => S().MeetInvites.Count > 0, 60f, "an invitation to the host's meet");
                Router.Show(Friends, true);
                yield return Until(() => Router.Current == Friends && Interactable("Invite0A"), 15f, "the invitation on the Friends screen");
                yield return new WaitForSeconds(0.8f);
                yield return Snap("02-meet-invitation");
                Click("Invite0A");
                yield return Until(() => ActiveMeet != null && ActiveMeet.Ready, 40f, "joined the friend's meet");
                if (ActiveMeet != null && ActiveMeet.Net.RoomId != room) Fail($"joined {ActiveMeet.Net.RoomId}, not the host's {room}");
                yield return new WaitForSeconds(1.5f);
                yield return Snap("03-back-at-friends-meet");
                if (race) yield return RaceFromTheMeet();
                else
                {
                    ActiveMeet?.Leave();
                    yield return Until(() => ActiveMeet == null && !onlineMeetRunning, 20f, "left again");
                }
            }
            else
            {
                yield return Until(() => m.RemoteCount == 0 || (m.Net.State["members"] as JArray).Count == 1, 40f, "the guest left");
                yield return new WaitForSeconds(1f);
                System.Threading.Tasks.Task<JToken> invite = m.Net.Ask("meet.invite", new { accountId = otherId ?? "" });
                while (!invite.IsCompleted) yield return null;
                if (invite.Result == null) Fail("invite refused: " + m.Net.LastError);
                yield return Until(() => (m.Net.State["members"] as JArray).Count(x => (string)x["state"] == "present") >= 2, 90f, "the guest came back through the invitation");
                yield return new WaitForSeconds(1f);
                yield return Snap("02-guest-back");
                if (race) yield return RaceFromTheMeet();
                else
                {
                    yield return Until(() => (m.Net.State["members"] as JArray).Count == 1, 60f, "the guest left again");
                    m.Leave();
                    yield return Until(() => ActiveMeet == null && !onlineMeetRunning, 20f, "host left the meet");
                }
            }
            if (Router.Current != Convoy) Router.Show(Convoy, false);
            yield return new WaitForSeconds(1f);
            if (S().InConvoy) { Click("Leave"); yield return Until(() => !S().InConvoy, 10f, "left the convoy"); }
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }

            // Race from the meet (spec §12, Gate 4 "race launch from meet"): ready at the meet, the leader starts from the
            // meet menu, the allocation moves both into race loading; after the race, Back to the Meet.
            IEnumerator RaceFromTheMeet()
            {
                MeetSession here = ActiveMeet;
                if (here == null) { Fail("not at the meet before the race"); yield break; }
                if ((bool?)S().MyMember?["eventReady"] != true)
                {
                    here.OpenMeetMenu();
                    yield return new WaitForSeconds(0.6f);
                    here.Hud.PanelButtons.FirstOrDefault(b => b.GetComponentInChildren<TextMeshProUGUI>()?.text.Contains("Event Ready") == true)?.onClick.Invoke();
                }
                yield return Until(() => AllMembers("eventReady"), 30f, "everyone event ready at the meet");
                if (host)
                {
                    yield return new WaitForSeconds(1.5f);
                    here.OpenMeetMenu();
                    yield return new WaitForSeconds(0.6f);
                    yield return Snap("03-start-from-meet");
                    Button go = here.Hud.PanelButtons.FirstOrDefault(b => b.GetComponentInChildren<TextMeshProUGUI>()?.text.Contains("Start the event") == true);
                    if (go == null) Fail("no Start the event in the leader's meet menu");
                    else go.onClick.Invoke();
                }
                yield return Until(() => InOnlineRace, 60f, "the race took over from the meet");
                Note($"race from the meet: in race {InOnlineRace}, meet left {ActiveMeet == null}, return to {ReturnMeetKind}");
                if (ActiveMeet != null) Fail("still at the meet during the race");
                yield return Until(() => !InOnlineRace && Router.Current == Convoy, 900f, "back from the race");
                yield return new WaitForSeconds(2.5f);
                Note("after the race: " + (LastOnlineResult ?? "").Replace("\n", " | "));
                yield return Until(() => Interactable("ReturnToMeet"), 15f, "Back to the Meet offered");
                yield return Snap("04-back-to-the-meet-offered");
                Click("ReturnToMeet");
                yield return Until(() => ActiveMeet != null && ActiveMeet.Ready, 40f, "back at the meet after the race");
                MeetSession again = ActiveMeet;
                if (again == null) yield break;
                if ((string)again.Net.State?["kind"] != "convoy") Fail("after the race: not the convoy's meet");
                yield return Until(() => (again.Net.State["members"] as JArray).Count(x => (string)x["state"] == "present") >= 2, 60f, "both back at the meet after the race");
                yield return new WaitForSeconds(1.5f);
                yield return Snap("05-back-at-meet-after-race");
                Note($"back at the meet after the race: room {again.Net.RoomId} bay {again.PlayerBay + 1}");
                yield return new WaitForSeconds(host ? 4f : 1f);
                again.Leave();
                yield return Until(() => ActiveMeet == null && !onlineMeetRunning, 20f, "left the meet after the race");
            }
        }

        /// <summary>
        /// Online meet evidence (<c>-nsMeetTourOnline host|guest1|guest2</c>, <c>-nsDevAccount N</c>): three real clients sign in
        /// and join the same public meet through the Convoy screen. Each sees the others arrive; guest1 walks, waves, says a
        /// quick-chat phrase and likes the host's car; the host jogs to the boombox and queues a cue everyone then plays;
        /// guest2 leaves (everyone sees "left" and the car fade), guest1 drops its connection (everyone sees "disconnected").
        /// Every client checks what it saw and exits 0 on PASS. Automation over real sockets on the loopback control plane.
        /// With <c>-nsMeetTourLivery</c> guest2 first leaves for the Garage, applies another paint colour and comes back: the
        /// host must then draw guest2's car in the new livery.
        /// </summary>
        IEnumerator MeetTourOnline(string role)
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "meet-online"));
            System.IO.Directory.CreateDirectory(dir);
            var failures = new List<string>();
            void Note(string n) => Debug.Log($"[NightSignal.MeetOnline:{role}] {n}");
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            IEnumerator Snap(string name)
            {
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"{role}-{name}.png"));
                yield return new WaitForEndOfFrame();
                yield return null;
            }
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable || !b.gameObject.activeInHierarchy) { Fail("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }
            IEnumerator Until(Func<bool> condition, float seconds, string what)
            {
                float until = Time.realtimeSinceStartup + seconds;
                while (!condition() && Time.realtimeSinceStartup < until) yield return null;
                if (!condition()) Fail("timed out: " + what);
            }
            OnlineSession S() => OnlineSession.Current;
            NetConfig cfg = NetConfig.FromCommandLine();
            JToken account = JObject.Parse(System.IO.File.ReadAllText(cfg.DevSeedFile))["accounts"][cfg.DevAccount];
            bool host = role == "host", g1 = role == "guest1", g2 = role == "guest2";
            bool liveryLeg = Array.IndexOf(Environment.GetCommandLineArgs(), "-nsMeetTourLivery") >= 0;
            IEnumerator Next(string row, int times)
            {
                for (int i = 0; i < times; i++)
                {
                    Click(row + "/Next");
                    yield return new WaitForSeconds(0.3f);
                }
            }

            yield return new WaitForSeconds(3f);
            Click("OnlineLogin");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("Email").GetComponent<TMP_InputField>().text = (string)account["email"];
            GameObject.Find("Password").GetComponent<TMP_InputField>().text = (string)account["devOnlyPassword"];
            Click("SignIn");
            yield return Until(() => Router.Current == Convoy && S() != null && S().Me != null, 25f, "signed in");
            if (S() == null) { Finish(); yield break; }
            yield return new WaitForSeconds(1.5f);
            if (S().StarterCarId == null) { Click("ChooseStarter"); yield return Until(() => S().StarterCarId != null, 10f, "starter chosen"); }
            if (S().InConvoy) { Click("Leave"); yield return Until(() => !S().InConvoy, 10f, "left an earlier convoy"); }
            string me = S().DisplayName;
            Note($"signed in as {me}");
            // Stagger the arrivals so each one is announced to the people already there.
            yield return new WaitForSeconds(host ? 0f : g1 ? 9f : 18f);
            yield return Until(() => GameObject.Find("MeetPublic")?.activeInHierarchy == true, 10f, "meet button");
            Click("MeetPublic");
            yield return Until(() => ActiveMeet != null && ActiveMeet.Ready, 40f, "joined and arrived");
            MeetSession m = ActiveMeet;
            if (m == null || !m.Ready) { Finish(); yield break; }
            Note($"in room {m.Net.RoomId} bay {m.PlayerBay + 1}");
            yield return new WaitForSeconds(1f);
            yield return Snap("01-arrived");

            // Everyone waits until all three are present.
            yield return Until(() => m.RemoteCount >= 2 && m.Net.State?["members"] is JArray a && a.Count(x => (string)x["state"] == "present") >= 3, 60f, "all three present");
            Note($"remotes: {m.RemoteCount}; seen: {string.Join(", ", m.Seen)}");
            if (host && m.Seen.Count(x => x.StartsWith("notice arrived")) < 2) Fail("the host did not see both arrivals announced");
            yield return new WaitForSeconds(2f);

            string hostId = (m.Net.State["members"] as JArray).OfType<JObject>()
                .OrderBy(x => (long)x["stateSinceMs"]).Select(x => (string)x["accountId"]).First();
            JObject lastIn = (m.Net.State["members"] as JArray).OfType<JObject>().OrderBy(x => (long)x["stateSinceMs"]).Last();
            string guest2Id = (string)lastIn["accountId"], guest2Livery = (string)lastIn["livery"] ?? "";
            if (g1)
            {
                // Walk toward the plaza for a few seconds, then greet: a wave, a quick-chat phrase and a like for the host's car.
                m.Camera.Yaw = MeetLayout.Bays[m.PlayerBay].Yaw;
                m.ScriptMove = _ => new Vector2(0f, 1f);
                yield return new WaitForSeconds(3f);
                m.ScriptMove = null;
                yield return new WaitForSeconds(0.5f);
                m.PlayEmote(Emote.Wave);
                m.Say("Nice car!");
                m.Net.Fire("meet.like", new { accountId = hostId });
                yield return new WaitForSeconds(1.2f);
                yield return Snap("02-guest-waves");
            }
            if (host)
            {
                yield return Until(() => m.Seen.Any(x => x.StartsWith("emote Wave from")), 30f, "saw the guest's wave");
                yield return Until(() => m.Seen.Any(x => x.StartsWith("chat ")), 10f, "saw the guest's quick chat");
                yield return Until(() => (int?)((m.Net.State["members"] as JArray).OfType<JObject>().FirstOrDefault(x => (string)x["accountId"] == S().AccountId)?["likes"]) >= 1, 10f, "got a like");
                // Face the waving guest for the photo.
                yield return new WaitForSeconds(0.3f);
                yield return Snap("02-host-sees-wave");

                // Jog to the boombox (real, validated steps) and queue a cue the room then plays for everyone.
                var target = new Vector3(13.6f, 0f, 39.9f);
                m.ScriptJog = true;
                m.ScriptMove = _ => new Vector2(0f, 1f);
                float until = Time.realtimeSinceStartup + 45f;
                while (Vector3.Distance(m.Player.transform.position, target) > 1.2f && Time.realtimeSinceStartup < until)
                {
                    Vector3 d = target - m.Player.transform.position;
                    m.Camera.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    yield return null;
                }
                m.ScriptMove = null;
                m.ScriptJog = false;
                Note($"at the boombox after walking: {m.Player.transform.position}; corrections {m.Net.CorrectionsReceived}, poses sent {m.Net.PosesSent}");
                if (m.Net.CorrectionsReceived > 2) Fail($"the room corrected the host's walk {m.Net.CorrectionsReceived} times");
                yield return new WaitForSeconds(1f);
                m.Open("boombox");
                yield return Until(() => m.Hud.PanelOpen && m.Hud.PanelTitle == "Boombox", 10f, "boombox panel");
                yield return new WaitForSeconds(0.6f);
                m.Queue("MUS_GARAGE");
                yield return Until(() => (string)m.Net.State?["boombox"]?["trackId"] == "MUS_GARAGE", 10f, "room plays the queued cue");
                yield return new WaitForSeconds(0.5f);
                yield return Snap("03-boombox");
                m.ClosePanel();
            }
            if (!host)
            {
                yield return Until(() => (string)m.Net.State?["boombox"]?["trackId"] == "MUS_GARAGE", 90f, "the host's boombox pick reached this client");
                Note($"boombox now {(string)m.Net.State?["boombox"]?["trackId"]}, submitted by the host: {(string)m.Net.State?["boombox"]?["submittedBy"] == hostId}");
            }

            // The livery leg: guest2 goes to the Garage, repaints, applies and comes back; the host draws the new livery.
            if (liveryLeg && g2)
            {
                yield return new WaitForSeconds(3f);
                m.Leave();
                yield return Until(() => ActiveMeet == null && Router.Current == Convoy, 20f, "left for the Garage");
                yield return new WaitForSeconds(1f);
                Click("OpenGarage");
                yield return Until(() => Router.Current == Garage && Garage.Workspace != null && !Garage.Busy, 20f, "garage loaded");
                yield return new WaitForSeconds(0.8f);
                Click("OpenAppearance");
                yield return Until(() => Router.Current == Appearance && Appearance.Editor != null, 10f, "appearance open");
                yield return new WaitForSeconds(0.8f);
                string before = Appearance.Workspace.AppliedLiveryHash;
                yield return Next("Section", 2); // paint
                yield return Next("Colour", 3);
                Click("Appearance-Apply");
                yield return Until(() => !Appearance.Busy && !Appearance.Editor.IsDirty, 20f, "new livery applied");
                yield return new WaitForSeconds(0.6f);
                Note($"garage: {Appearance.Message} livery {before} -> {Appearance.Workspace.AppliedLiveryHash}");
                if (Appearance.Workspace.AppliedLiveryHash == before) Fail("the new livery was not applied: " + Appearance.Message);
                Click("Back");
                yield return Until(() => Router.Current == Garage, 10f, "back at the garage");
                yield return new WaitForSeconds(0.6f);
                Click("Back");
                yield return Until(() => Router.Current == Convoy, 10f, "back at the convoy screen");
                yield return Until(() => GameObject.Find("MeetPublic")?.activeInHierarchy == true, 10f, "meet button");
                Click("MeetPublic");
                yield return Until(() => ActiveMeet != null && ActiveMeet.Ready, 40f, "back in the meet");
                m = ActiveMeet;
                if (m == null || !m.Ready) { Finish(); yield break; }
                Note($"back in room {m.Net.RoomId} bay {m.PlayerBay + 1} with livery {(m.Appearance != null ? ColorUtility.ToHtmlStringRGB(m.Appearance.Primary) : "stock")}");
                yield return new WaitForSeconds(8f); // the host looks
            }
            if (liveryLeg && host)
            {
                // Guest2 comes back in the new livery: the room says so, and the car drawn here matches it.
                var content = NightSignal.Content.ContentLibrary.Load();
                string seenPrimary = null, roomPrimary = null;
                bool Matches()
                {
                    JObject g = (m.Net.State?["members"] as JArray)?.OfType<JObject>().FirstOrDefault(x => (string)x["accountId"] == guest2Id);
                    string liv = (string)g?["livery"] ?? "";
                    if (g == null || (string)g["state"] != "present" || liv == guest2Livery) return false;
                    Art.CarAppearance want = liv.Length == 0 ? null : Art.AppearanceMapping.ForWire(content.Customization, (string)g["carId"], liv);
                    Vehicle.VehicleView car = m.RemoteCar(guest2Id);
                    if (want == null || car == null || car.Appearance == null) return false;
                    roomPrimary = ColorUtility.ToHtmlStringRGB(want.Primary);
                    seenPrimary = ColorUtility.ToHtmlStringRGB(car.Appearance.Primary);
                    return roomPrimary == seenPrimary;
                }
                yield return Until(Matches, 150f, "guest2 back in the new livery");
                Note($"guest2 back: room livery primary #{roomPrimary}, drawn #{seenPrimary}; seen: {string.Join(", ", m.Seen.Where(x => x.Contains("refreshed") || x.StartsWith("removed") || x.StartsWith("remote ")))}");
                yield return new WaitForSeconds(1f);
                yield return Snap("04-livery-back");
            }

            // Departures: guest2 leaves properly; guest1 drops its connection; the host sees both, worded apart.
            if (g2)
            {
                yield return new WaitForSeconds(4f);
                m.Leave();
                yield return Until(() => ActiveMeet == null && Router.Current == Convoy, 20f, "back at the convoy screen");
                yield return new WaitForSeconds(1f);
                yield return Snap("04-left");
                Finish();
                yield break;
            }
            if (g1)
            {
                yield return new WaitForSeconds(10f);
                Note("dropping the connection without leaving");
                Finish(); // quits: the control connection closes without meet.leave
                yield break;
            }
            yield return Until(() => m.Seen.Any(x => x.StartsWith("notice departed")), 30f, "saw a departure announced");
            yield return Until(() => m.Seen.Any(x => x.StartsWith("notice disconnected")), 40f, "saw a lost connection announced as disconnected");
            yield return new WaitForSeconds(1.5f);
            yield return Snap("05-departures");
            Note("seen: " + string.Join(", ", m.Seen));
            foreach (string l in m.Log) Note("log: " + l);
            m.Leave();
            yield return Until(() => ActiveMeet == null && Router.Current == Convoy, 20f, "host back at the convoy screen");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
