using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Art;
using NightSignal.Characters;
using NightSignal.Core.Meet;
using NightSignal.GameAudio;
using NightSignal.UI;
using NightSignal.Vehicle;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NightSignal.Meet
{
    public sealed partial class MeetSession
    {
        /// <summary>The online link (null offline). Set before Start.</summary>
        public MeetNet Net;
        /// <summary>Automation: remote participants seen, emotes seen on them, ribbon notices shown.</summary>
        public readonly List<string> Seen = new List<string>();
        public int RemoteCount => remotes.Count;

        sealed class Remote
        {
            public string AccountId = "", Name = "", CarId = "", Livery = "", State = "", LookJson = "";
            /// <summary>The room's visit number for this account: a new visit (left and came back) is built afresh.</summary>
            public long Generation;
            public int Bay = -1;
            public long StateSinceMs, EmoteStartMs = -1;
            public string Emote;
            public int ChatIndex = -1;
            public long ChatAt;
            public int Pi, Likes;
            public string PiClass = "", Tune = "";
            public bool LikedByMe, Blocked;
            /// <summary>Their Player Card look (validated again on arrival), or null for the default look from the name.</summary>
            public CharacterLook Look;
            public CharacterRig Rig;
            public CharacterMotion Motion;
            public VehicleView Car;
            public GameObject CarCollider;
            public float LeaveT = -1f;
            public bool Removed;
        }

        readonly Dictionary<string, Remote> remotes = new Dictionary<string, Remote>();
        long joinedServerMs;
        bool boomboxOpen, leavingOnline, arrivalConfirmed;
        JObject lastState;

        // ------------------------------------------------------------------ join, arrive, leave

        IEnumerator JoinOnline()
        {
            System.Threading.Tasks.Task<bool> join = Net.Join();
            while (!join.IsCompleted) yield return null;
            if (!join.Result)
            {
                Hud.SetMode("Could not join the meet: " + Net.LastError);
                Note("online join failed: " + Net.LastError);
                yield return new WaitForSeconds(3f);
                ExitRequested = true;
                yield break;
            }
            JObject me = Member(Net.State, Net.Me);
            PlayerBay = (int)me["bay"] - 1;
            // The car the room admitted (the one others see): model, applied livery, legal PI and tune.
            CarId = (string)me["carId"] ?? CarId;
            string liv = (string)me["livery"];
            Appearance = string.IsNullOrEmpty(liv) ? null : AppearanceMapping.ForWire(lib.Customization, CarId, liv);
            if (Appearance != null) Paint = Appearance.Primary;
            CarPi = (int?)me["pi"] ?? CarPi;
            TuneSummary = (string)me["tune"] ?? TuneSummary;
            CarName = CarDisplay(CarId);
            joinedServerMs = (long)Net.State["serverTimeMs"] - 1000;
            SyncRemotes(Net.State);
            occupied.Add(PlayerBay);
            Note($"joined meet {Net.RoomId} ({(string)Net.State["kind"]}) in bay {PlayerBay + 1}; {remotes.Count} other(s) here");
            BeginArrival();
            UpdateMode();
        }

        static JObject Member(JObject state, string account) =>
            (state?["members"] as JArray)?.OfType<JObject>().FirstOrDefault(m => (string)m["accountId"] == account);

        IEnumerator ArrivedOnline()
        {
            System.Threading.Tasks.Task<JToken> t = Net.Ask("meet.arrived");
            while (!t.IsCompleted) yield return null;
            JToken r = t.Result;
            if (r == null || Player == null) yield break;
            // The server's validated spot is authoritative; it is the same rule, so it is normally where we already stand.
            var at = new Vector3((float)r["x"], 0f, (float)r["z"]);
            if (Vector3.Distance(Player.transform.position, at) > 0.6f) Player.Teleport(at, (float)r["yaw"]);
            arrivalConfirmed = true; // poses start from the room's spot, so the first one is never a "jump"
            Note("arrival confirmed by the room");
        }

        IEnumerator LeaveOnline()
        {
            leavingOnline = true;
            System.Threading.Tasks.Task<JToken> t = Net.Ask("meet.leave");
            float until = Time.realtimeSinceStartup + 3f;
            while (!t.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            // Leaving: the avatar and car dissolve (about half a second), then back to the menus.
            float fade = 0f;
            while (fade < 0.5f)
            {
                fade += Time.deltaTime;
                float k = Mathf.Clamp01(1f - fade / 0.5f);
                if (Player != null) Player.transform.localScale = Vector3.one * Mathf.Max(0.01f, k);
                yield return null;
            }
            ExitRequested = true;
        }

        /// <summary>An agreed convoy event allocated: stop emotes and panels, leave the room quietly; the race flow takes over.</summary>
        public void LeaveForRace()
        {
            if (Net == null || leavingOnline) return;
            leavingOnline = true;
            PlayerMotion?.Stop();
            Hud?.HidePanel();
            Net.Fire("meet.leave", new { reason = "race" });
            Note("left the meet for a race");
            ExitRequested = true;
        }

        void UpdateMode()
        {
            if (Net?.State == null) return;
            int here = (Net.State["members"] as JArray)?.Count(m => (string)m["state"] != "leaving") ?? 0;
            string kind = (string)Net.State["kind"] == "convoy" ? "Convoy meet" : "Public meet";
            Hud.SetMode($"{kind} · {here} of {(int?)Net.State["capacity"] ?? 6} drivers here");
        }

        // ------------------------------------------------------------------ per frame

        void OnlineFrame(float dt)
        {
            if (Net.State != null && !ReferenceEquals(Net.State, lastState))
            {
                lastState = Net.State;
                SyncRemotes(Net.State);
                Ribbon(Net.State);
                UpdateMode();
                if (Hud.PanelOpen && boomboxOpen) RefreshBoomboxOnline();
            }
            if (Net.Correction.HasValue && Player != null)
            {
                Vector3 c = Net.Correction.Value;
                Net.Correction = null;
                Player.Teleport(new Vector3(c.x, 0f, c.z), c.y);
                Note("pose corrected by the room");
            }
            if (arrivalConfirmed && Player != null && Player.gameObject.activeSelf && (State == Phase.Walking || State == Phase.Wheel || State == Phase.Panel || State == Phase.Photo))
                Net.SendPose(Player.transform.position, Player.transform.eulerAngles.y, Player.Speed);
            UpdateRemotes(dt);
            ConvoyHeader();
        }

        // ------------------------------------------------------------------ the convoy while at the meet

        bool readyRequested;
        long readyRequestRevision = -1;
        bool convoyHooked;

        /// <summary>
        /// The compact convoy header (spec §12): members, phase and readiness, and a pinned line while the leader's ready
        /// request waits for an answer. The menu offers Mode/Event Ready; an allocation leaves the meet for the race.
        /// </summary>
        void ConvoyHeader()
        {
            var s = Net.Session;
            if (!convoyHooked)
            {
                convoyHooked = true;
                s.Client.ReadyRequested += OnReadyRequested;
                s.Client.MeetChallenge += OnMeetChallenge;
            }
            JObject c = s.Convoy;
            if (!s.InConvoy || c == null)
            {
                Hud.SetConvoy("");
                return;
            }
            JArray members = c["members"] as JArray ?? new JArray();
            string phase = (string)c["phase"] ?? "Idle";
            JToken me = s.MyMember;
            string line = $"<b>CONVOY</b> · {members.Count} · leader {(string)c["leaderName"]} · {(c["postEvent"] is JObject ? "race finished: continue or service break" : PhaseText(phase))}";
            if (c["eventProposal"] is JObject)
                line += $" · event ready {members.Count(m => (bool?)m["eventReady"] == true)}/{members.Count}" + ((bool?)me?["eventReady"] == true ? " (you: ready)" : " (you: not ready)");
            else if (phase == "ModeCheck")
                line += $" · mode ready {(int?)c["modeReadyCount"] ?? 0}/{members.Count}" + ((bool?)me?["modeReady"] == true ? " (you: ready)" : "");
            bool pending = readyRequested && ((c["eventProposal"] is JObject && (bool?)me?["eventReady"] != true) || (phase == "ModeCheck" && (bool?)me?["modeReady"] != true));
            if (!pending) readyRequested = false;
            if (pending) line += $"\n<color=#F2A541>The leader asks for Ready — {controls.BindingLabel("Menu")} to answer</color>";
            else if (s.IsLeader && AllEventReady(c)) line += $"\n<color=#3EC6D8>Everyone is ready — {controls.BindingLabel("Menu")} to start the event</color>";
            Hud.SetConvoy(line);
        }

        /// <summary>Every racing member has answered Event Ready to the current proposal (the leader may start).</summary>
        static bool AllEventReady(JObject c) =>
            c?["eventProposal"] is JObject && c["members"] is JArray m && m.Count > 0
            && m.Where(x => (bool?)x["spectator"] != true).All(x => (bool?)x["eventReady"] == true);

        static string PhaseText(string phase)
        {
            switch (phase)
            {
                case "ModeCheck": return "choosing a mode";
                case "EventSelection": return "choosing an event";
                case "ReadyCheck": return "ready check";
                case "Allocating": return "starting…";
                case "InMatch": return "racing";
                default: return "at ease";
            }
        }

        void OnMeetChallenge(JObject p)
        {
            if (this == null || Net == null || p == null) return;
            ChallengeCompleted((string)p["challengeId"], (string)p["name"] ?? (string)p["challengeId"], (long?)p["cash"] ?? 0);
            _ = Net.Session.RefreshMe();
        }

        void OnReadyRequested(JObject p)
        {
            if (this == null || Net == null) return;
            readyRequested = true;
            long rev = (long?)p?["proposalRevision"] ?? (long?)p?["modeRevision"] ?? 0;
            if (rev != readyRequestRevision)
            {
                readyRequestRevision = rev;
                // A leader's request outranks arrival notices: it goes to the front of the ribbon and stays pinned in the header.
                Hud.Ribbon.Post(NoticeKind.Info, $"ready:{(string)p?["kind"]}:{rev}", "The convoy leader asks: ready?");
            }
        }

        void OpenMenuOnline()
        {
            var s = Net.Session;
            var actions = new List<(string, System.Action)>();
            JObject c = s.Convoy;
            JToken me = s.MyMember;
            if (s.InConvoy && c?["eventProposal"] is JObject proposal)
            {
                bool ready = (bool?)me?["eventReady"] == true;
                actions.Add((ready ? "Convoy: Unready" : "Convoy: Event Ready", () =>
                {
                    Net.Fire("event.ready", new { proposalRevision = (long)proposal["revision"], loadoutRevision = (long?)me?["loadoutRevision"] ?? 0, ready = !ready });
                    Note(ready ? "event unready from the meet" : "event ready from the meet");
                    ClosePanel();
                }));
                // The leader commits from the meet once everyone is ready; the allocation then moves everyone into race loading.
                if (s.IsLeader && AllEventReady(c))
                    actions.Add(("Convoy: Start the event", () =>
                    {
                        Net.Fire("event.start", new { proposalRevision = (long)proposal["revision"] });
                        Note("event started from the meet");
                        ClosePanel();
                    }));
            }
            else if (s.InConvoy && (string)c?["phase"] == "ModeCheck")
            {
                bool ready = (bool?)me?["modeReady"] == true;
                actions.Add((ready ? "Convoy: Mode Unready" : "Convoy: Mode Ready", () =>
                {
                    Net.Fire("mode.ready", new { modeRevision = (long)c["modeRevision"], ready = !ready });
                    ClosePanel();
                }));
            }
            actions.Add(("Invite a friend to this meet", OpenInvite));
            actions.Add(("Leave the meet", Leave));
            string kind = (string)Net.State?["kind"] == "convoy" ? "your convoy's meet" : "a public meet";
            ShowPanel("Meet", $"Cedar Lantern Terrace · {kind}.\n\nLeaving (or opening the Garage) leaves the meet; an event your convoy agrees on takes you straight to the race. Only the terrace's touring challenges (CH61–CH65) pay out here; nothing else costs or earns anything.", actions);
        }

        void OpenInvite() => StartCoroutine(InviteRoutine());

        IEnumerator InviteRoutine()
        {
            System.Threading.Tasks.Task<JObject> t = Net.Session.Client.Get("/v1/friends");
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted || t.Result == null)
            {
                Hud.Notify("Friends are unavailable right now", null);
                yield break;
            }
            var here = new HashSet<string>(remotes.Keys) { Net.Me };
            var actions = new List<(string, System.Action)>();
            foreach (JObject f in (t.Result["friends"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string id = (string)f["accountId"], name = (string)f["displayName"] ?? (string)f["handle"] ?? "Friend";
                string status = (string)f["status"] ?? "";
                if (here.Contains(id) || status == "Offline") continue;
                actions.Add(($"Invite {name}", () => { Net.Fire("meet.invite", new { accountId = id }); Hud.Notify($"A place is held for {name} for 30 seconds", null); ClosePanel(); }));
                if (actions.Count >= 8) break;
            }
            ShowPanel("Invite a friend", actions.Count == 0 ? "No friends online right now." : "Your friend gets an invitation and a place held for 30 seconds.", actions);
        }

        void Ribbon(JObject state)
        {
            foreach (JToken e in state["events"] as JArray ?? new JArray())
            {
                if ((string)e["accountId"] == Net.Me || (long)e["atMs"] < joinedServerMs) continue;
                string kind = (string)e["kind"];
                NoticeKind k = kind == "arrived" ? NoticeKind.Arrived : kind == "departed" ? NoticeKind.Departed
                    : kind == "lefttorace" ? NoticeKind.LeftToRace : NoticeKind.Disconnected;
                if (Hud.Ribbon.Post(k, (string)e["key"], (string)e["name"])) Seen.Add($"notice {kind} {(string)e["name"]}");
            }
        }

        void SyncRemotes(JObject state)
        {
            var present = new HashSet<string>();
            occupied.Clear();
            foreach (int b in MeetLayout.AmbienceBays) occupied.Add(b);
            if (PlayerBay >= 0) occupied.Add(PlayerBay);
            foreach (JObject m in (state["members"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string id = (string)m["accountId"];
                if (id == Net.Me) continue;
                present.Add(id);
                string carId = (string)m["carId"] ?? "V01", livery = (string)m["livery"] ?? "";
                string lookJson = m["look"] is JObject lookObject ? lookObject.ToString(Newtonsoft.Json.Formatting.None) : "";
                long generation = (long?)m["generation"] ?? 0;
                if (remotes.TryGetValue(id, out Remote r) && (r.Generation != generation || r.CarId != carId || r.Livery != livery || r.LookJson != lookJson))
                {
                    // A new visit, or back from a lost connection in another car, livery or look (the Garage, the Player
                    // Card): built again from what the room now says, never left showing the old car.
                    Seen.Add($"remote {r.Name} refreshed ({(r.Generation != generation ? "new visit" : r.CarId != carId ? "car" : r.Livery != livery ? "livery" : "look")})");
                    DestroyRemote(r);
                    remotes.Remove(id);
                    r = null;
                }
                if (r == null)
                {
                    r = new Remote
                    {
                        AccountId = id, Name = (string)m["displayName"] ?? "", CarId = carId, Livery = livery, LookJson = lookJson,
                        Generation = generation, Bay = (int)m["bay"] - 1,
                    };
                    if (lookJson.Length > 0)
                    {
                        CharacterLook l = PlayerLooks.Parse(lookJson);
                        if (l != null && PlayerLooks.Problems(l).Count == 0) r.Look = l;
                    }
                    remotes[id] = r;
                    SpawnRemote(r);
                    Seen.Add($"remote {r.Name} in bay {r.Bay + 1}");
                }
                string was = r.State;
                r.State = (string)m["state"];
                r.StateSinceMs = (long)m["stateSinceMs"];
                r.Pi = (int?)m["pi"] ?? 0;
                r.PiClass = (string)m["piClass"] ?? "";
                r.Tune = (string)m["tune"] ?? "";
                r.Likes = (int?)m["likes"] ?? 0;
                r.LikedByMe = (bool?)m["likedByYou"] == true;
                r.Blocked = (bool?)m["blocked"] == true;
                if (r.Bay >= 0 && r.State != "leaving") occupied.Add(r.Bay);
                // Replicated emote: ID + server start; animated locally from where it should be by now.
                string emote = m["emote"]?.Type == JTokenType.String ? (string)m["emote"] : null;
                long start = (long?)m["emoteStartMs"] ?? 0;
                if (emote != null && start != r.EmoteStartMs && Emotes.TryParse(emote, out Emote e))
                {
                    r.EmoteStartMs = start;
                    r.Motion.Play(e, Mathf.Max(0f, (float)((Net.ServerNowMs - start) / 1000.0)));
                    Seen.Add($"emote {e} from {r.Name}");
                }
                JToken chat = m["chat"];
                if (chat != null && chat.Type == JTokenType.Object)
                {
                    int index = (int)chat["index"];
                    long at = (long)chat["atMs"];
                    if (at != r.ChatAt)
                    {
                        r.ChatIndex = index;
                        r.ChatAt = at;
                        Seen.Add($"chat {index} from {r.Name}");
                    }
                }
                if (r.State == "leaving" && was != "leaving") r.LeaveT = 0f;
                if (r.State == "present" && was == "arriving") PlaceRemoteParked(r);
            }
            foreach (Remote gone in remotes.Values.Where(r => !present.Contains(r.AccountId)).ToList())
            {
                DestroyRemote(gone);
                remotes.Remove(gone.AccountId);
            }
        }

        /// <summary>The style of the driver card last viewed (canonical; tours check replication).</summary>
        public string ViewedCardStyle { get; private set; }

        /// <summary>A remote visitor's car as drawn (null when not here); tours check its livery.</summary>
        public VehicleView RemoteCar(string accountId) => remotes.TryGetValue(accountId, out Remote r) ? r.Car : null;

        /// <summary>The look a remote visitor's avatar was built from (null = the default look); tours check replication.</summary>
        public CharacterLook RemoteLook(string accountId) => remotes.TryGetValue(accountId, out Remote r) ? r.Look : null;

        void SpawnRemote(Remote r)
        {
            var carMats = Resources.Load<CarMaterialSet>("CarMaterialSet");
            CarAppearance look = string.IsNullOrEmpty(r.Livery) ? null : AppearanceMapping.ForWire(lib.Customization, r.CarId, r.Livery);
            Color paint = look != null ? look.Primary : Color.HSVToRGB(Mathf.Abs(r.Name.GetHashCode() % 360) / 360f, 0.55f, 0.7f);
            VehicleParams p = lib.Params(r.CarId, AssistSettings.Default);
            r.Car = VehicleView.Create($"Remote_{r.Name}_{r.CarId}", p, lib.Body(r.CarId), carMats, paint, look);
            r.Car.SetHeadlights(true);
            spawned.Add(r.Car.gameObject);
            r.Rig = CharacterRig.Create(r.Look ?? DefaultPlayerLook(r.Name), null, null, $"Remote_{r.Name}");
            r.Motion = r.Rig.gameObject.AddComponent<CharacterMotion>();
            spawned.Add(r.Rig.gameObject);
            PlaceRemoteParked(r);
        }

        void PlaceRemoteParked(Remote r)
        {
            if (r.Bay < 0) return;
            MeetBay b = MeetLayout.Bays[r.Bay];
            r.Car.ShowParked(new Vector3(b.X, 0f, b.Z), Quaternion.Euler(0f, b.Yaw, 0f));
            if (r.CarCollider == null)
            {
                MeetBox f = b.Footprint;
                r.CarCollider = new GameObject($"RemoteCar{r.Bay + 1}Collision") { layer = GameLayers.Scenery };
                r.CarCollider.transform.SetPositionAndRotation(new Vector3(f.X, 0f, f.Z), Quaternion.Euler(0f, f.Yaw, 0f));
                var bc = r.CarCollider.AddComponent<BoxCollider>();
                bc.center = new Vector3(0f, 0.7f, 0f);
                bc.size = new Vector3(f.HalfW * 2f - 0.1f, 1.4f, f.HalfL * 2f - 0.1f);
                spawned.Add(r.CarCollider);
            }
        }

        void DestroyRemote(Remote r)
        {
            if (r.Rig != null) Destroy(r.Rig.gameObject);
            if (r.Car != null) Destroy(r.Car.gameObject);
            if (r.CarCollider != null) Destroy(r.CarCollider);
            Seen.Add($"removed {r.Name}");
        }

        void UpdateRemotes(float dt)
        {
            double now = Net.ServerNowMs;
            foreach (Remote r in remotes.Values)
            {
                if (r.State == "arriving")
                {
                    // The same presented drive the arriving player sees, from its server start; the avatar steps out when present.
                    float u = Mathf.Clamp01((float)((now - r.StateSinceMs) / MeetRoom.ArrivalMs));
                    List<MeetPoint> path = MeetLayout.ArrivalPath(r.Bay);
                    float startS = MeetLayout.PathLength(path) - MeetLayout.ArrivalMetres;
                    float travel = MeetLayout.ArrivalMetres * (1f - Mathf.Pow(1f - u, 1.5f));
                    MeetPoint here = Along(path, startS + travel), behind = Along(path, startS + travel - 2.2f), ahead = Along(path, startS + travel + 2.2f);
                    float yaw = u >= 1f ? MeetLayout.Bays[r.Bay].Yaw : Mathf.Atan2(ahead.X - behind.X, ahead.Z - behind.Z) * Mathf.Rad2Deg;
                    r.Car.ShowRolling(new Vector3(here.X, 0f, here.Z), Quaternion.Euler(0f, yaw, 0f), 0f, travel);
                    r.Rig.gameObject.SetActive(false);
                    continue;
                }
                r.Rig.gameObject.SetActive(!r.Blocked);
                if (r.LeaveT >= 0f)
                {
                    // Leaving: dissolve over about half a second (a plain fade when motion is reduced).
                    r.LeaveT += dt;
                    float k = Mathf.Clamp01(1f - r.LeaveT / 0.5f);
                    float scale = SignalTheme.ReducedMotion ? (k > 0.01f ? 1f : 0.001f) : Mathf.Max(0.001f, k);
                    r.Rig.transform.localScale = Vector3.one * scale;
                    r.Car.transform.localScale = Vector3.one * scale;
                    continue;
                }
                if (Net.PoseOf(r.AccountId, out MeetPoseSample pose))
                {
                    r.Rig.transform.SetPositionAndRotation(new Vector3(pose.X, 0f, pose.Z), Quaternion.Euler(0f, pose.Yaw, 0f));
                    r.Motion.Speed = r.State == "disconnected" ? 0f : pose.Speed;
                }
                else if (lastState != null && Member(lastState, r.AccountId) is JObject m)
                    r.Rig.transform.SetPositionAndRotation(new Vector3((float)m["x"], 0f, (float)m["z"]), Quaternion.Euler(0f, (float)m["yaw"], 0f));
            }
        }

        // ------------------------------------------------------------------ interactions with other people

        Spot NearestRemote(Vector3 me, Vector3 fwd, Spot best, ref float bestScore)
        {
            foreach (Remote r in remotes.Values)
            {
                if (r.Blocked || r.State == "leaving" || r.State == "arriving") continue;
                Vector3 person = r.Rig.transform.position;
                float d = Vector3.Distance(new Vector3(me.x, 0f, me.z), new Vector3(person.x, 0f, person.z));
                if (d < 2.2f && d - 0.2f < bestScore)
                {
                    bestScore = d - 0.2f;
                    best = new Spot { Kind = "remote-person", Id = r.AccountId, Label = $"Greet {r.Name}", Pos = person, Range = 2.2f };
                }
                MeetBay b = MeetLayout.Bays[r.Bay];
                float cd = Vector3.Distance(new Vector3(me.x, 0f, me.z), new Vector3(b.X, 0f, b.Z));
                if (cd < 3.4f && cd - 1.2f < bestScore)
                {
                    bestScore = cd - 1.2f;
                    best = new Spot { Kind = "remote-car", Id = r.AccountId, Label = $"Inspect {r.Name}'s {CarDisplay(r.CarId)}", Pos = new Vector3(b.X, 0.7f, b.Z), Range = 3.4f };
                }
            }
            return best;
        }

        public void InspectRemoteCar(string account)
        {
            if (!remotes.TryGetValue(account, out Remote r)) return;
            string custom = string.IsNullOrEmpty(r.Livery) ? "stock appearance" : "a custom livery (applied in their Garage)";
            string body = $"Owner: {r.Name}\n" +
                          $"PI {r.Pi} · class {(string.IsNullOrEmpty(r.PiClass) ? "—" : r.PiClass)} (their applied build)\n" +
                          $"Tune: {r.Tune}\n" +
                          $"Visible customization: {custom}\n" +
                          $"Likes: {r.Likes}{(r.LikedByMe ? " (including yours)" : "")} — cosmetic only: no currency or RP.";
            ShowPanel($"{r.CarId} {CarDisplay(r.CarId)}", body, new List<(string, System.Action)>
            {
                (r.LikedByMe ? "Unlike" : "Like", () => { Net.Fire("meet.like", new { accountId = r.AccountId }); ClosePanel(); Hud.Notify(r.LikedByMe ? "Like removed" : $"You liked {r.Name}'s car", null); }),
                ($"View {r.Name}'s driver card", () => StartCoroutine(ViewCard(r))),
            });
            Note($"inspected {r.Name}'s car");
        }

        /// <summary>
        /// The public Player Card (spec §11): name, @username, pronouns, rank, campaign and challenge progress and the car
        /// they brought. Local UI only — the other driver is not interrupted or told.
        /// </summary>
        IEnumerator ViewCard(Remote r)
        {
            System.Threading.Tasks.Task<JObject> t = Net.Session.Client.Get($"/v1/players/{r.AccountId}/card");
            while (!t.IsCompleted) yield return null;
            JObject c = t.IsFaulted ? null : t.Result;
            if (c == null || c["accountId"] == null)
            {
                Hud.Notify("That driver card is unavailable right now", null);
                yield break;
            }
            string Esc(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");
            string handle = (string)c["handle"], pronouns = (string)c["pronouns"];
            JToken rank = c["rank"], camp = c["campaign"], ch = c["challenges"];
            string body = $"<b>{Esc((string)c["displayName"] ?? r.Name)}</b>" + (handle != null ? $"   @{Esc(handle)}" : "") +
                          (string.IsNullOrEmpty(pronouns) ? "" : $"   <color=#9A968D>({Esc(pronouns)})</color>") + "\n\n" +
                          $"Rank: {Esc((string)rank?["name"])} · {(int?)rank?["rankPoints"] ?? 0:N0} RP\n" +
                          $"Campaign: Normal {(int?)camp?["normalClears"] ?? 0}/{(int?)camp?["stages"] ?? 30} · Hard {(int?)camp?["hardClears"] ?? 0}/{(int?)camp?["stages"] ?? 30}\n" +
                          $"Challenges: {(int?)ch?["completed"] ?? 0}/{(int?)ch?["total"] ?? 75}\n" +
                          $"Here with: {r.CarId} {CarDisplay(r.CarId)} · PI {r.Pi} {(string.IsNullOrEmpty(r.PiClass) ? "" : "class " + r.PiClass)}";
            ShowPanel("Driver card", body, new List<(string, System.Action)>
            {
                ($"Back to {r.Name}'s car", () => InspectRemoteCar(r.AccountId)),
            });
            // The card as its owner styled it (the catalogue's default when they have not).
            Core.Customization.CardStyle cardStyle = c["style"] is JObject so ? Core.Customization.CardStyle.Parse(so.ToString(Newtonsoft.Json.Formatting.None)) : null;
            Core.Customization.CardStyleCatalogue styles = lib.Customization?.Card;
            string preferred = cardStyle != null && cardStyle.PreferredCar.Length > 0 ? CarDisplay(cardStyle.PreferredCar) : "";
            Hud.ShowCard(styles, cardStyle ?? styles?.Default, (string)c["displayName"] ?? r.Name, pronouns,
                new List<string>
                {
                    $"Rank {(string)rank?["name"]} · {(int?)rank?["rankPoints"] ?? 0:N0} RP",
                    $"Campaign N {(int?)camp?["normalClears"] ?? 0}/{(int?)camp?["stages"] ?? 30} · H {(int?)camp?["hardClears"] ?? 0}/{(int?)camp?["stages"] ?? 30}",
                    $"Challenges {(int?)ch?["completed"] ?? 0}/{(int?)ch?["total"] ?? 75}",
                }, preferred);
            ViewedCardStyle = (cardStyle ?? styles?.Default)?.Canonical();
            Note($"viewed {r.Name}'s driver card");
        }

        void GreetRemote(string account)
        {
            if (!remotes.TryGetValue(account, out Remote r)) return;
            Player.transform.rotation = Quaternion.LookRotation(Flat(r.Rig.transform.position - Player.transform.position));
            PlayEmote(Vector3.Distance(r.Rig.transform.position, Player.transform.position) < 1.6f ? Emote.Bow : Emote.Wave);
        }

        void RemoteLabels(UnityEngine.Camera cam, bool show)
        {
            foreach (Remote r in remotes.Values)
            {
                if (r.Rig == null) continue;
                bool visible = show && r.Rig.gameObject.activeSelf && !r.Blocked;
                string line = r.State == "disconnected" ? "<size=70%>connection lost</size>"
                    : r.ChatIndex >= 0 && Net.ServerNowMs - r.ChatAt < 4000 && r.ChatIndex < Text.QuickChat.Count ? $"<size=80%>“{Text.QuickChat[r.ChatIndex]}”</size>"
                    : "<size=70%>driver</size>";
                Hud.Nameplate(r, cam, r.Rig.transform.position + Vector3.up * (r.Rig.Skeleton.H + 0.35f), $"{r.Name}\n{line}", visible);
            }
        }

        // ------------------------------------------------------------------ boombox (server-owned)

        string OnlineTrack() => (string)Net?.State?["boombox"]?["trackId"] ?? BoomboxState.DefaultCue;

        void OpenBoomboxOnline()
        {
            boomboxOpen = true;
            BoomboxOp("acquire", null, true);
        }

        void BoomboxOp(string op, string track, bool thenShow = false)
        {
            StartCoroutine(BoomboxOpRoutine(op, track, thenShow));
        }

        IEnumerator BoomboxOpRoutine(string op, string track, bool thenShow)
        {
            System.Threading.Tasks.Task<JToken> t = Net.Ask("meet.boombox", new { op, trackId = track });
            while (!t.IsCompleted) yield return null;
            if (t.Result == null)
            {
                string err = Net.LastError ?? "";
                Hud.Notify(err.Contains("leaseheld") ? "Someone else is choosing music" : err.Contains("outofrange") ? "Walk up to the boombox"
                    : err.Contains("toosoon") ? "The music changed a moment ago — try again shortly" : err.Contains("notowned") ? "Only music you have unlocked can be queued"
                    : err.Contains("queuefull") ? "The queue is full" : err, null);
                if (op == "acquire") { boomboxOpen = false; yield break; }
            }
            else Note($"boombox {op} {track}: {(string)t.Result["status"]}");
            if (thenShow || Hud.PanelOpen && boomboxOpen) RefreshBoomboxOnline();
        }

        void RefreshBoomboxOnline()
        {
            JObject b = Net.State?["boombox"] as JObject;
            if (b == null) return;
            MusicPlayer mp = MusicPlayer.Instance;
            bool protect = DrivingPreferences.Current.ProtectBossMusic;
            string Title(string id)
            {
                string t = id;
                if (mp != null) mp.CueInfo(id, out t, out _);
                return BoomboxState.TitleFor(id, t, OwnsCue, protect);
            }
            string Who(string account) => account == null ? "" : account == Net.Me ? " (yours)" : remotes.TryGetValue(account, out Remote r) ? $" ({r.Name})" : "";
            string track = (string)b["trackId"];
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Now playing: <b>{Title(track)}</b>{Who((string)b["submittedBy"])}" +
                          (BoomboxState.AudibleFor(track, OwnsCue, protect) != track ? " <color=#9A968D>(you hear the meet bed: spoiler protection)</color>" : ""));
            var queue = (b["queue"] as JArray ?? new JArray()).Select(q => Title((string)q["trackId"]) + Who((string)q["accountId"])).ToList();
            sb.AppendLine(queue.Count == 0 ? "Queue: empty" : "Queue: " + string.Join(" · ", queue));
            string holder = (string)b["leaseHolder"];
            sb.AppendLine(holder == null ? "" : holder == Net.Me ? "You are choosing." : $"{Who(holder).Trim(' ', '(', ')')} is choosing.");
            sb.AppendLine($"One request each · up to {BoomboxState.MaxQueue} queued · a change at most every {BoomboxState.MinChangeIntervalMs / 1000} s. Shared with everyone here.");
            var actions = new List<(string, System.Action)>
            {
                ("Skip to the next request", () => BoomboxOp("skip", null)),
                ("Withdraw my request", () => BoomboxOp("withdraw", null)),
            };
            AddCuePage(actions, Title, cue => BoomboxOp("queue", cue), RefreshBoomboxOnline);
            ShowPanel("Boombox", sb.ToString(), actions);
        }
    }
}
