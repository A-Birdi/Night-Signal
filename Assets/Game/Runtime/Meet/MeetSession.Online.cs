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
            public string AccountId = "", Name = "", CarId = "", Livery = "", State = "";
            public int Bay = -1;
            public long StateSinceMs, EmoteStartMs = -1;
            public string Emote;
            public int ChatIndex = -1;
            public long ChatAt;
            public int Pi, Likes;
            public string PiClass = "", Tune = "";
            public bool LikedByMe, Blocked;
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
            Net.Fire("meet.leave");
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
        }

        void Ribbon(JObject state)
        {
            foreach (JToken e in state["events"] as JArray ?? new JArray())
            {
                if ((string)e["accountId"] == Net.Me || (long)e["atMs"] < joinedServerMs) continue;
                string kind = (string)e["kind"];
                NoticeKind k = kind == "arrived" ? NoticeKind.Arrived : kind == "departed" ? NoticeKind.Departed : NoticeKind.Disconnected;
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
                if (!remotes.TryGetValue(id, out Remote r))
                {
                    r = new Remote { AccountId = id, Name = (string)m["displayName"] ?? "", CarId = (string)m["carId"] ?? "V01", Livery = (string)m["livery"] ?? "", Bay = (int)m["bay"] - 1 };
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

        void SpawnRemote(Remote r)
        {
            var carMats = Resources.Load<CarMaterialSet>("CarMaterialSet");
            CarAppearance look = string.IsNullOrEmpty(r.Livery) ? null : AppearanceMapping.ForWire(lib.Customization, r.CarId, r.Livery);
            Color paint = look != null ? look.Primary : Color.HSVToRGB(Mathf.Abs(r.Name.GetHashCode() % 360) / 360f, 0.55f, 0.7f);
            VehicleParams p = lib.Params(r.CarId, AssistSettings.Default);
            r.Car = VehicleView.Create($"Remote_{r.Name}_{r.CarId}", p, lib.Body(r.CarId), carMats, paint, look);
            r.Car.SetHeadlights(true);
            spawned.Add(r.Car.gameObject);
            r.Rig = CharacterRig.Create(DefaultPlayerLook(r.Name), null, null, $"Remote_{r.Name}");
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
            });
            Note($"inspected {r.Name}'s car");
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
