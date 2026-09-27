using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Net;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Two-client social evidence run (<c>-nsUiTourSocial host|guest</c>, <c>-nsDevAccount N</c>, <c>-nsPeerHandle h</c>;
        /// needs only the local control plane). Each client drives its own REAL screens: claim a username, the host sends a
        /// friend request by @username, the guest accepts it, the host creates a convoy and invites the guest from the
        /// friend list, the guest joins from the invitation, both see each other in the roster, then both leave.
        /// Screenshots in Builds/Screenshots/tour-social. Automation, not a human playtest.
        /// </summary>
        IEnumerator UiTourSocial(string role)
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "tour-social"));
            System.IO.Directory.CreateDirectory(dir);
            var failures = new List<string>();
            bool host = role == "host";
            string tag = host ? "host" : "guest";
            void Shot(string name) => ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, tag + "-" + name + ".png"));
            void Note(string s) => Debug.Log($"[NightSignal.UiTourSocial:{tag}] " + s);
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { failures.Add("button not available: " + name); Note("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }
            IEnumerator Until(Func<bool> condition, float seconds, string what)
            {
                float until = Time.realtimeSinceStartup + seconds;
                while (!condition() && Time.realtimeSinceStartup < until) yield return null;
                if (!condition()) { failures.Add("timed out: " + what); Note("timed out: " + what + " (" + OnlineSession.Current?.LastError + ")"); }
            }
            OnlineSession S() => OnlineSession.Current;
            string Arg(string name)
            {
                string[] args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            int FriendIndex(string handle)
            {
                JArray list = Friends.Graph?["friends"] as JArray;
                if (list == null) return -1;
                for (int i = 0; i < list.Count; i++)
                    if (string.Equals((string)list[i]["handle"], handle, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
            bool Listed(string section, string handle) =>
                ((Friends.Graph?[section] as JArray) ?? new JArray()).Any(p => string.Equals((string)p["handle"], handle, StringComparison.OrdinalIgnoreCase));

            // While We Wait with two humans at the convoy's HOSTED tables: each plays through the real screens and must see
            // the other's shots, operations and marks arrive from the control plane.
            IEnumerator SharedToys(bool isHost)
            {
                if (Router.Current != Convoy) Router.Show(Convoy, false);
                yield return new WaitForSeconds(1f);
                Click("WhileWeWait");
                yield return Until(() => Router.Current == WhileWeWait, 10f, "While We Wait open");
                yield return new WaitForSeconds(1f);

                Click("Toy-CapClash");
                yield return new WaitForSeconds(2.5f);
                CapClash.AutoAim = TourCapAim;
                float capUntil = Time.realtimeSinceStartup + 90f;
                while ((CapClash.MyShots < 2 || CapClash.OthersShots < 2) && Time.realtimeSinceStartup < capUntil)
                {
                    int before = CapClash.MyShots;
                    if (before < 2)
                    {
                        Button shoot = GameObject.Find("CapShoot")?.GetComponent<Button>();
                        if (shoot != null && shoot.interactable) shoot.onClick.Invoke();
                    }
                    yield return new WaitForSeconds(2f);
                }
                Shot("10-shared-cap-clash");
                Note($"cap clash (shared): mine {CapClash.MyShots}, others {CapClash.OthersShots}");
                if (CapClash.MyShots < 2 || CapClash.OthersShots < 2) failures.Add($"shared Cap Clash: mine {CapClash.MyShots}, others {CapClash.OthersShots}");
                CapClash.AutoAim = null;
                Click("Back");
                yield return new WaitForSeconds(1.5f);

                Click("Toy-PitCrew");
                yield return new WaitForSeconds(2.5f);
                PitCrew.AutoLock = rel => rel < 0.05;
                float pitUntil = Time.realtimeSinceStartup + 90f;
                while ((PitCrew.OperationsDoneByMe < 1 || PitCrew.OperationsDoneByOthers < 1) && Time.realtimeSinceStartup < pitUntil)
                {
                    if (PitCrew.OperationsDoneByMe < 1)
                    {
                        // Host takes the first available task, the guest the second, so they do not queue on one claim.
                        Button task = GameObject.Find(isHost ? "Task0" : "Task1")?.GetComponent<Button>() ?? GameObject.Find("Task0")?.GetComponent<Button>();
                        if (task != null && task.interactable) task.onClick.Invoke();
                    }
                    yield return new WaitForSeconds(3f);
                }
                Shot("11-shared-pit-crew");
                Note($"pit-crew (shared): mine {PitCrew.OperationsDoneByMe}, others {PitCrew.OperationsDoneByOthers}");
                if (PitCrew.OperationsDoneByMe < 1 || PitCrew.OperationsDoneByOthers < 1)
                    failures.Add($"shared Pit-Crew: mine {PitCrew.OperationsDoneByMe}, others {PitCrew.OperationsDoneByOthers}");
                PitCrew.AutoLock = null;
                Click("Back");
                yield return new WaitForSeconds(1.5f);

                Click("Toy-Canvas");
                yield return new WaitForSeconds(2.5f);
                ConvoyCanvas.AutoDraw = TourStrokes();
                float canvasUntil = Time.realtimeSinceStartup + 90f;
                while ((ConvoyCanvas.MyObjects < 2 || ConvoyCanvas.OthersObjects < 2) && Time.realtimeSinceStartup < canvasUntil) yield return null;
                yield return new WaitForSeconds(1.5f);
                Shot("12-shared-canvas");
                Note($"canvas (shared): mine {ConvoyCanvas.MyObjects} ({ConvoyCanvas.MyStrokePoints} points), others {ConvoyCanvas.OthersObjects}");
                if (ConvoyCanvas.MyStrokePoints < 94) failures.Add($"shared Canvas: {ConvoyCanvas.MyStrokePoints} of my 94 stroke points reached the hosted sheet");
                if (ConvoyCanvas.MyObjects < 2 || ConvoyCanvas.OthersObjects < 2)
                    failures.Add($"shared Canvas: mine {ConvoyCanvas.MyObjects}, others {ConvoyCanvas.OthersObjects}");
                ConvoyCanvas.AutoDraw = null;
                yield return new WaitForSeconds(4f); // let the other client finish seeing these marks
                Click("Back");
                yield return new WaitForSeconds(1.5f);
                Click("Back");
                yield return Until(() => Router.Current == Convoy, 10f, "back at the convoy");
            }

            NetConfig cfg = NetConfig.FromCommandLine();
            JToken account = JObject.Parse(System.IO.File.ReadAllText(cfg.DevSeedFile))["accounts"][cfg.DevAccount];
            string myHandle = "nsdriver" + cfg.DevAccount;
            string peer = Arg("-nsPeerHandle") ?? (host ? "nsdriver1" : "nsdriver0");

            // Sign in through the real Online Login screen.
            yield return new WaitForSeconds(3f);
            Click("OnlineLogin");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("Email").GetComponent<TMP_InputField>().text = (string)account["email"];
            GameObject.Find("Password").GetComponent<TMP_InputField>().text = (string)account["devOnlyPassword"];
            Click("SignIn");
            yield return Until(() => Router.Current == Convoy && S() != null, 20f, "signed in");
            yield return new WaitForSeconds(1.5f);
            if (S()?.StarterCarId == null) { Click("ChooseStarter"); yield return Until(() => S().StarterCarId != null, 10f, "starter chosen"); }
            if (S().InConvoy) { Click("Leave"); yield return Until(() => !S().InConvoy, 10f, "left an earlier convoy"); }

            // Friends: a username first (claimed once; kept across runs).
            Click("OpenFriends");
            yield return Until(() => Router.Current == Friends && Friends.Graph != null, 15f, "friend list loaded");
            if (!string.Equals(S().Handle, myHandle, StringComparison.OrdinalIgnoreCase))
            {
                GameObject.Find("HandleField").GetComponent<TMP_InputField>().text = myHandle;
                Click("ClaimHandle");
                yield return Until(() => string.Equals(S().Handle, myHandle, StringComparison.OrdinalIgnoreCase), 15f, "username claimed");
            }
            Note($"signed in as @{S().Handle}; peer @{peer}");
            yield return new WaitForSeconds(1f);
            Shot("01-friends");

            if (host)
            {
                // The guest may not have claimed its username yet ("No player has that username"): retry like a player.
                float giveUp = Time.realtimeSinceStartup + 90f;
                while (FriendIndex(peer) < 0 && !Listed("outgoing", peer) && Time.realtimeSinceStartup < giveUp)
                {
                    GameObject.Find("FriendHandle").GetComponent<TMP_InputField>().text = "@" + peer;
                    Click("SendFriendRequest");
                    yield return Until(() => !Friends.Busy, 20f, "request answered");
                    if (!Listed("outgoing", peer) && FriendIndex(peer) < 0)
                    {
                        Note("request not accepted yet: " + Friends.Message);
                        yield return new WaitForSeconds(5f);
                    }
                }
                if (FriendIndex(peer) < 0 && !Listed("outgoing", peer)) failures.Add("request sent");
                yield return new WaitForSeconds(1f);
                Shot("02-request-sent");
                yield return Until(() => { if (!Friends.Busy && Time.frameCount % 240 == 0) Click("RefreshFriends"); return FriendIndex(peer) >= 0; }, 120f, "the guest accepted");
                Note("friends with @" + peer);

                // Convoy, then the invitation from the friend list.
                Click("Back");
                yield return Until(() => Router.Current == Convoy, 10f, "back at the convoy screen");
                yield return new WaitForSeconds(1f);
                Click("CreateConvoy");
                yield return Until(() => S().InConvoy, 10f, "convoy created");
                yield return new WaitForSeconds(1f);
                Click("OpenFriends");
                yield return Until(() => Router.Current == Friends, 10f, "friends open");
                yield return Until(() =>
                {
                    if (!Friends.Busy && Time.frameCount % 240 == 0) Click("RefreshFriends");
                    int i = FriendIndex(peer);
                    return i >= 0 && (bool?)Friends.Graph["friends"][i]["canInvite"] == true;
                }, 90f, "the guest can be invited (online, not in a convoy)");
                yield return new WaitForSeconds(0.8f);
                Shot("03-friend-online");
                Click("Friend" + Math.Max(0, FriendIndex(peer)) + "A");
                yield return new WaitForSeconds(1.5f);
                Shot("04-invited");
                Click("Back");
                yield return Until(() => S().Convoy?["members"]?.Count() >= 2, 90f, "the guest joined the convoy");
                yield return new WaitForSeconds(1.5f);
                Shot("05-together");
                Note("convoy members: " + string.Join(", ", ((JArray)S().Convoy["members"]).Select(m => (string)m["displayName"])));
                yield return SharedToys(true);

                // Course access: buy the first course this profile can afford and does not hold (in-game credits).
                Click("OpenCourses");
                yield return Until(() => Router.Current == Courses, 10f, "courses open");
                var cat = NightSignal.Content.ContentLibrary.Load().Catalogue;
                HashSet<string> Owned() => new HashSet<string>(((S().Me?["courses"] as JObject)?["owned"] as JArray ?? new JArray()).Select(o => (string)o["courseId"]));
                long balance = (long?)(S().Me?["wallet"] as JObject)?["balance"] ?? 0;
                HashSet<string> before = Owned();
                string buyId = cat.Courses.Select(c => Core.Rules.CourseAccess.RuleFor(cat, c.Id))
                    .Where(r => r.Purchasable && !before.Contains(r.CourseId) && r.Price <= balance).Select(r => r.CourseId).FirstOrDefault();
                if (buyId == null) Note("no affordable course left to buy (balance " + balance + ")");
                else
                {
                    Courses.SelectCourse(buyId);
                    yield return new WaitForSeconds(0.8f);
                    Click("BuyCourse"); // first press asks for confirmation
                    yield return new WaitForSeconds(0.8f);
                    Shot("06-course-confirm");
                    Click("BuyCourse");
                    yield return Until(() => !Courses.Busy && Owned().Contains(buyId), 20f, "course " + buyId + " bought");
                    long after = (long?)(S().Me?["wallet"] as JObject)?["balance"] ?? 0;
                    Note($"bought {buyId}: balance {balance:N0} -> {after:N0}");
                    yield return new WaitForSeconds(0.8f);
                    Shot("07-course-bought");
                }
                Click("Back");
                yield return Until(() => Router.Current == Convoy, 10f, "back at the convoy");
                yield return new WaitForSeconds(4f); // let the guest see the roster too
            }
            else
            {
                if (FriendIndex(peer) < 0)
                {
                    yield return Until(() => { if (!Friends.Busy && Time.frameCount % 240 == 0) Click("RefreshFriends"); return Listed("incoming", peer); }, 120f, "the host's request arrived");
                    yield return new WaitForSeconds(1f);
                    Shot("02-request-received");
                    JArray incoming = (JArray)Friends.Graph["incoming"];
                    int row = incoming.ToList().FindIndex(p => string.Equals((string)p["handle"], peer, StringComparison.OrdinalIgnoreCase));
                    Click("Request" + Math.Max(0, row) + "A");
                    yield return Until(() => FriendIndex(peer) >= 0, 20f, "request accepted");
                }
                Note("friends with @" + peer);
                yield return Until(() => S().Invites.Count > 0, 150f, "the host's convoy invitation");
                yield return new WaitForSeconds(1f);
                Shot("03-invitation");
                Click("Invite0A");
                yield return Until(() => S().InConvoy && Router.Current == Convoy, 20f, "joined from the invitation");
                yield return Until(() => S().MyMember?["carId"]?.Type == JTokenType.String, 10f, "loadout set after joining");
                yield return new WaitForSeconds(2f);
                Shot("04-joined");
                Note("joined " + (string)S().Convoy?["leaderName"] + "'s convoy");
                yield return SharedToys(false);
                yield return new WaitForSeconds(3f);
            }

            if (S()?.InConvoy == true)
            {
                if (Router.Current != Convoy) Router.Show(Convoy, false);
                yield return new WaitForSeconds(0.5f);
                Click("Leave");
                yield return Until(() => !S().InConvoy, 10f, "left the convoy");
            }
            string summary = failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures);
            Note(summary);

            yield return new WaitForSeconds(1f);
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}
