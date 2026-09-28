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

        /// <summary>Visits the meet online (kind: public | convoy | friend) with the room server; back to <paramref name="returnTo"/> on leaving.</summary>
        public void StartOnlineMeet(string kind, string friendAccountId, UIScreen returnTo) => StartCoroutine(RunOnlineMeet(kind, friendAccountId, returnTo));

        IEnumerator RunOnlineMeet(string kind, string friendAccountId, UIScreen returnTo)
        {
            OnlineSession s = OnlineSession.Current;
            if (s == null || ActiveMeet != null) yield break;
            meetLeftForRace = false;
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
            ActiveMeet.OwnsCue = id => owned.Contains(id);
            _ = s.Request("presence.set", new { presence = "AtMeet" }, quiet: true);
            while (ActiveMeet != null && !ActiveMeet.ExitRequested && !meetLeftForRace) yield return null;
            if (meetLeftForRace) yield break; // the race flow owns the screen now (RunOnlineRace)
            if (ActiveMeet != null) Destroy(ActiveMeet.gameObject);
            ActiveMeet = null;
            _ = s.Request("presence.set", new { presence = "InMenus" }, quiet: true);
            Canvas.gameObject.SetActive(true);
            yield return LoadBackdrop();
            Router.Show(returnTo ?? Convoy, false);
        }

        /// <summary>Called when the convoy's event allocates while at the meet: leave the room safely, the race takes over.</summary>
        void LeaveMeetForRace()
        {
            if (ActiveMeet == null) return;
            meetLeftForRace = true;
            ActiveMeet.LeaveForRace();
            ActiveMeet = null; // the meet scene unloads with the race scene load
        }

        /// <summary>
        /// Online meet evidence (<c>-nsMeetTourOnline host|guest1|guest2</c>, <c>-nsDevAccount N</c>): three real clients sign in
        /// and join the same public meet through the Convoy screen. Each sees the others arrive; guest1 walks, waves, says a
        /// quick-chat phrase and likes the host's car; the host jogs to the boombox and queues a cue everyone then plays;
        /// guest2 leaves (everyone sees "left" and the car fade), guest1 drops its connection (everyone sees "disconnected").
        /// Every client checks what it saw and exits 0 on PASS. Automation over real sockets on the loopback control plane.
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
