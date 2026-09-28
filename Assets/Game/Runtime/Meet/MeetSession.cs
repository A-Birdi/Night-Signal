using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Art;
using NightSignal.Characters;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Meet;
using NightSignal.GameAudio;
using NightSignal.InputBindings;
using NightSignal.UI;
using NightSignal.Vehicle;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NightSignal.Meet
{
    /// <summary>
    /// A meet visit at Cedar Lantern Terrace (spec §12). The player's car drives the arrival spline into its bay (about
    /// 3.5 s, skippable), the avatar gets out at a validated free point, and the player walks and jogs, looks around,
    /// interacts (the host at the radio bench, the boombox, the timing board, the viewpoint placards and photo points,
    /// their own car, other display cars and the people beside them), uses the twelve emotes by wheel or keys, quick chat,
    /// photo mode, sits in their car (headlight preset, turn the wheels, a rate-limited rev) and can be rescued back to
    /// their car. Offline, the same interactions run locally: rivals and the host are story characters standing by
    /// their cars, labelled as such — nobody pretends to be a connected player.
    /// </summary>
    public sealed partial class MeetSession : MonoBehaviour
    {
        // ------------------------------------------------------------------ inputs (set before Start)

        public string CarId = "V01";
        public string CarName = "";
        public Color Paint = new Color(0.8f, 0.12f, 0.12f);
        public CarAppearance Appearance;
        public int CarPi;
        public string TuneSummary = "Stock factory build";
        public string PlayerName = "You";
        public CharacterLook PlayerLook;
        public Func<string, bool> OwnsCue = _ => false;
        public List<string> RecentSlips = new List<string>();
        /// <summary>Automation: skip the arrival flourish.</summary>
        public bool SkipArrival;
        /// <summary>Automation: scripted movement (camera-relative, like the stick) replacing the controls, and jogging.</summary>
        public Func<float, Vector2> ScriptMove;
        public bool ScriptJog;

        // ------------------------------------------------------------------ state

        public enum Phase { Loading, Arriving, Walking, Wheel, Panel, Photo, InCar }
        public Phase State { get; private set; } = Phase.Loading;
        public bool ExitRequested { get; private set; }
        public bool Ready => State != Phase.Loading && State != Phase.Arriving;
        public int PlayerBay { get; private set; } = -1;
        public AvatarWalker Player { get; private set; }
        public CharacterMotion PlayerMotion { get; private set; }
        public VehicleView PlayerCar { get; private set; }
        public MeetCamera Camera { get; private set; }
        public MeetHud Hud { get; private set; }
        public BoomboxState Boombox { get; private set; }
        public MeetText Text { get; private set; }
        /// <summary>What the host's lesson has seen (wave, bow, emote help read).</summary>
        public bool HostWaved, HostBowed, HostHelpRead;
        public readonly List<string> Log = new List<string>();

        sealed class Npc
        {
            public string Id, Name, Crew, Intro;
            public CharacterRig Rig;
            public CharacterMotion Motion;
            public int Bay = -1;
            public string CarId;
            public VehicleView Car;
            public float NextEmote;
            public bool Liked;
        }

        sealed class Spot
        {
            public string Kind, Id, Label;
            public Vector3 Pos;
            public float Range;
            public Npc Npc;
        }

        ContentLibrary lib;
        WalkingControls controls;
        readonly List<Npc> npcs = new List<Npc>();
        Npc host;
        readonly HashSet<int> occupied = new HashSet<int>();
        readonly List<Spot> spots = new List<Spot>();
        readonly List<GameObject> spawned = new List<GameObject>();
        List<MeetPoint> arrivalPath;
        float arrivalStartS, arrivalT, rescueHeld, bubbleUntil, hostReplyAt = -1f;
        Emote hostReply;
        string bubbleText = "";
        Vector2 wheelVec;
        int wheelSel = -1;
        Spot focus;
        EngineAudio engine;
        int headlightPreset;
        float carSteer;
        bool nearBoombox;
        TMPro.TextMeshPro boardText;
        Dictionary<string, string> unlockHints;
        int driverSide = 1;

        static long NowMs => (long)(Time.unscaledTimeAsDouble * 1000.0);

        // ------------------------------------------------------------------ setup

        void Start()
        {
            lib = ContentLibrary.Load();
            if (MeetRuntime.Active == null || lib == null)
            {
                Debug.LogError("[NightSignal.Meet] Needs the meet scene and the content library.");
                ExitRequested = true;
                enabled = false;
                return;
            }
            Text = MeetText.Parse(lib.MeetText != null ? lib.MeetText.text : null);
            Physics.IgnoreLayerCollision(GameLayers.Avatar, GameLayers.Avatar, true);
            controls = new WalkingControls();
            controls.Enable();
            Hud = new MeetHud("Cedar Lantern Terrace", Net != null ? "Connecting to the meet…" : "Offline meet · no other drivers are connected");
            Hud.SetHints(Hints());
            Camera = MeetCamera.Create();
            spawned.Add(Camera.gameObject);
            Boombox = new BoomboxState("offline", id => MusicPlayer.Instance != null ? MusicPlayer.Instance.CueSeconds(id) : 0, NowMs);
            unlockHints = UnlockHints();
            PlaceNpcs();
            BuildSpots();
            SpawnEpilogue(lib, lib.Catalogue);
            BuildBoard();
            MusicPlayer.Ensure()?.Play(BoomboxState.DefaultCue, 2f);
            if (Net != null)
            {
                // Online: the room server chooses the bay (never a local pick); the arrival starts once it has.
                StartCoroutine(JoinOnline());
                return;
            }
            PlayerBay = MeetLayout.AllocateBay(occupied);
            occupied.Add(PlayerBay);
            BeginArrival();
            Hud.Notify("Offline meet: no other drivers are connected", "offline");
            Note($"meet opened; player bay {PlayerBay + 1}; {npcs.Count - 1} rivals at their cars; host {Text.HostName}");
        }

        void OnDestroy()
        {
            controls?.Disable();
            controls?.Dispose();
            Hud?.Dispose();
            if (Net != null && convoyHooked)
            {
                Net.Session.Client.ReadyRequested -= OnReadyRequested;
                Net.Session.Client.MeetChallenge -= OnMeetChallenge;
            }
            Net?.Dispose();
            foreach (GameObject go in spawned) if (go != null) Destroy(go);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void Note(string s)
        {
            Log.Add(s);
            Debug.Log("[NightSignal.Meet] " + s);
        }

        /// <summary>The default look for a player without a saved one: a tidy jacket, varied by name.</summary>
        public static CharacterLook DefaultPlayerLook(string seed)
        {
            int h = 17;
            foreach (char ch in seed ?? "") h = h * 31 + ch;
            h = Math.Abs(h);
            string[] skins = { "#F1D3BC", "#E3B996", "#C68E63", "#9A6644", "#6E4630" };
            string[] hairs = { "cropped", "swept", "bob", "ponytail", "curly", "undercut" };
            string[] jackets = { "#23313F", "#5A2A2A", "#2F4A3A", "#3A3A44", "#6B4E2E" };
            return new CharacterLook
            {
                Id = "PLAYER", Height = 1.66f + (h % 5) * 0.035f, Build = "average", Skin = skins[h % skins.Length],
                Hair = hairs[(h / 7) % hairs.Length], HairColour = "#231C18", Outfit = "jacket", Sleeves = "long",
                Primary = jackets[(h / 3) % jackets.Length], Secondary = "#E6E0D2", Accent = "#D7263D",
                Lower = "trousers", LowerColour = "#2B2D33", Shoes = "sneakers", ShoeColour = "#EDEBE4", Face = "smile",
            };
        }

        void PlaceNpcs()
        {
            ContentCatalogue cat = lib.Catalogue;
            // Rivals by their own cars (story characters, not connected players): a few from different crews.
            string[] ids = { "R09", "R14", "R25", "R36", "R42" };
            int[] bays = MeetLayout.AmbienceBays;
            CarMaterialSet carMats = Resources.Load<CarMaterialSet>("CarMaterialSet");
            for (int i = 0; i < ids.Length; i++)
            {
                RivalDef r = cat.Rivals.FirstOrDefault(x => x.Id == ids[i]);
                CharacterLook look = lib.Look(ids[i]);
                if (r == null || look == null) continue;
                var n = new Npc { Id = r.Id, Name = r.Name, Crew = r.Crew, Intro = r.IntroSample, Bay = bays[i], CarId = r.PrimaryCar };
                MeetBay b = MeetLayout.Bays[n.Bay];
                Color paint = CharacterMaterialSet.Parse(look.Primary, "#808080");
                VehicleParams p = lib.Params(n.CarId, AssistSettings.Default);
                n.Car = VehicleView.Create($"Display_{n.Id}_{n.CarId}", p, lib.Body(n.CarId), carMats, paint);
                n.Car.ShowParked(new Vector3(b.X, 0f, b.Z), Quaternion.Euler(0f, b.Yaw, 0f));
                n.Car.SetHeadlights(true);
                spawned.Add(n.Car.gameObject);
                occupied.Add(n.Bay);
                AddCarCollider(n.Bay);
                MeetPoint front = b.Footprint.FromLocal(i % 2 == 0 ? -2.3f : 2.3f, 1.6f);
                n.Rig = CharacterRig.Create(look);
                n.Rig.transform.SetPositionAndRotation(new Vector3(front.X, 0f, front.Z), Quaternion.LookRotation(new Vector3(b.X - front.X, 0f, b.Z - front.Z)));
                n.Motion = n.Rig.gameObject.AddComponent<CharacterMotion>();
                n.NextEmote = Time.time + 4f + i * 3.3f;
                spawned.Add(n.Rig.gameObject);
                npcs.Add(n);
            }
            // The host at the radio bench.
            CharacterLook hostLook = lib.Look("NPC-GENZO");
            if (hostLook != null)
            {
                host = new Npc { Id = "NPC-GENZO", Name = Text.HostName, Crew = "", Intro = Text.HostRole };
                host.Rig = CharacterRig.Create(hostLook);
                host.Rig.transform.SetPositionAndRotation(new Vector3(MeetLayout.HostSpot.X, 0f, MeetLayout.HostSpot.Z), Quaternion.Euler(0f, 200f, 0f));
                host.Motion = host.Rig.gameObject.AddComponent<CharacterMotion>();
                host.NextEmote = float.MaxValue;
                spawned.Add(host.Rig.gameObject);
                npcs.Add(host);
            }
        }

        void AddCarCollider(int bay)
        {
            MeetBox f = MeetLayout.Bays[bay].Footprint;
            var go = new GameObject($"BayCar{bay + 1}Collision") { layer = GameLayers.Scenery };
            go.transform.SetPositionAndRotation(new Vector3(f.X, 0f, f.Z), Quaternion.Euler(0f, f.Yaw, 0f));
            var bc = go.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, 0.7f, 0f);
            bc.size = new Vector3(f.HalfW * 2f - 0.1f, 1.4f, f.HalfL * 2f - 0.1f);
            spawned.Add(go);
        }

        void BuildSpots()
        {
            var bb = MeetLayout.Boombox;
            spots.Add(new Spot { Kind = "boombox", Id = "boombox", Label = "Boombox", Pos = new Vector3(bb.X, 0.8f, bb.Z), Range = 2.2f });
            var tb = MeetLayout.TimingBoard;
            spots.Add(new Spot { Kind = "board", Id = "board", Label = "Timing board", Pos = new Vector3(tb.X, 1.5f, tb.Z - 0.3f), Range = 3.2f });
            foreach (MeetBox pl in MeetLayout.Placards)
            {
                MeetText.Placard t = Text.Find(pl.Id);
                spots.Add(new Spot { Kind = "placard", Id = pl.Id, Label = t != null ? t.Title : pl.Id, Pos = new Vector3(pl.X, 1f, pl.Z), Range = 2.2f });
            }
            // Photo points at the fixtures their words describe.
            // The three named photo points (CH67), at the Core layout's points so the room validates the same places.
            foreach ((string id, MeetPoint at) in MeetLayout.PhotoPoints)
            {
                string label = id == "PHOTO-TEA-KIOSK" ? "The Tea Kiosk" : id == "PHOTO-RADIO-BENCH" ? "The Radio Bench" : "The Maintenance Gate";
                float y = id == "PHOTO-RADIO-BENCH" ? 0.6f : 1.1f, range = id == "PHOTO-RADIO-BENCH" ? 1.8f : id == "PHOTO-TEA-KIOSK" ? 2.6f : 3f;
                spots.Add(new Spot { Kind = "placard", Id = id, Label = label, Pos = new Vector3(at.X, y, at.Z), Range = range });
            }
            spots.Add(new Spot { Kind = "photo-marker", Id = "photo", Label = "Photo marker", Pos = new Vector3(MeetLayout.PhotoMarker.X, 0.2f, MeetLayout.PhotoMarker.Z), Range = 1.3f });
            foreach (Npc n in npcs)
            {
                if (n == host) spots.Add(new Spot { Kind = "host", Id = n.Id, Label = $"Talk to {n.Name}", Pos = n.Rig.transform.position + Vector3.up * 1.2f, Range = 2.4f, Npc = n });
                else
                {
                    spots.Add(new Spot { Kind = "npc", Id = n.Id, Label = $"Talk to {n.Name}", Pos = n.Rig.transform.position + Vector3.up * 1.2f, Range = 2f, Npc = n });
                    MeetBay b = MeetLayout.Bays[n.Bay];
                    spots.Add(new Spot { Kind = "car", Id = n.Id, Label = $"Inspect {n.Name}'s {CarDisplay(n.CarId)}", Pos = new Vector3(b.X, 0.7f, b.Z), Range = 3.4f, Npc = n });
                }
            }
        }

        string CarDisplay(string carId)
        {
            CarDef c = lib.Catalogue.Cars.FirstOrDefault(x => x.Id == carId);
            return c != null ? c.Name : carId;
        }

        Dictionary<string, string> UnlockHints()
        {
            var hints = new Dictionary<string, string>();
            TextAsset doc = lib.Documents.FirstOrDefault(t => t != null && t.name == "music.unlocks");
            if (doc == null) return hints;
            foreach (JToken c in JObject.Parse(doc.text)["cues"] ?? new JArray())
            {
                string id = (string)c["cueId"], kind = (string)c["source"]?["kind"], stage = (string)c["source"]?["stageId"], trial = (string)c["source"]?["trialId"];
                switch (kind)
                {
                    case "baseline": hints[id] = "Held from the start"; break;
                    case "stage-first-normal-clear": hints[id] = $"First Normal clear of {stage}"; break;
                    case "stage-first-hard-clear": hints[id] = $"First Hard clear of {stage}"; break;
                    case "lieutenant-first-defeat": hints[id] = $"Beat the lieutenant of {stage} (Normal)"; break;
                    case "trial-first-victory": hints[id] = $"First win in the {trial} Team Trial"; break;
                }
            }
            return hints;
        }

        void BuildBoard()
        {
            var tb = MeetLayout.TimingBoard;
            var go = new GameObject("TimingBoardText");
            go.transform.SetPositionAndRotation(new Vector3(tb.X, 2.05f, tb.Z - 0.1f), Quaternion.identity);
            boardText = go.AddComponent<TMPro.TextMeshPro>();
            boardText.rectTransform.sizeDelta = new Vector2(4.3f, 1.55f);
            boardText.fontSize = 1.6f;
            boardText.enableAutoSizing = true;
            boardText.fontSizeMin = 0.8f;
            boardText.fontSizeMax = 2.2f;
            boardText.alignment = TMPro.TextAlignmentOptions.TopLeft;
            boardText.color = new Color(0.92f, 0.9f, 0.84f);
            // The board faces south (the plaza); text reads from the front.
            go.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
            boardText.text = BoardText(false);
            spawned.Add(go);
        }

        string BoardText(bool full)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(full ? Text.BoardHeader : "<b>TIMING BOARD</b>");
            sb.AppendLine(full ? "Convoy proposal: none — this is an offline meet." : "<color=#9A968D>Convoy: none (offline)</color>");
            sb.AppendLine();
            if (RecentSlips == null || RecentSlips.Count == 0) sb.AppendLine(Text.BoardEmpty);
            else foreach (string s in RecentSlips.Take(full ? 8 : 3)) sb.AppendLine(s);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ arrival

        void BeginArrival()
        {
            ContentCatalogue cat = lib.Catalogue;
            if (string.IsNullOrEmpty(CarName)) CarName = CarDisplay(CarId);
            if (CarPi <= 0) CarPi = cat.Cars.FirstOrDefault(c => c.Id == CarId)?.BasePI ?? 0;
            VehicleParams p = lib.Params(CarId, AssistSettings.Default);
            PlayerCar = VehicleView.Create($"PlayerCar_{CarId}", p, lib.Body(CarId), Resources.Load<CarMaterialSet>("CarMaterialSet"), Paint, Appearance);
            PlayerCar.SetHeadlights(true);
            spawned.Add(PlayerCar.gameObject);
            driverSide = PlayerCar.EnsureCockpit()?.Frame != null ? PlayerCar.Cockpit.Frame.DriverSide : 1;
            PlayerCar.Cockpit?.SetVisible(false);
            engine = PlayerCar.gameObject.AddComponent<EngineAudio>();
            engine.Configure(p, CarId);
            arrivalPath = MeetLayout.ArrivalPath(PlayerBay);
            arrivalStartS = MeetLayout.PathLength(arrivalPath) - MeetLayout.ArrivalMetres;
            arrivalT = 0f;
            State = Phase.Arriving;
            Camera.Presenting = PlayerCar.transform;
            PoseArrival(0f, 0f);
            Camera.transform.position = PlayerCar.transform.position - PlayerCar.transform.forward * 9f + Vector3.up * 3f;
            Camera.transform.LookAt(PlayerCar.transform);
            if (SkipArrival) arrivalT = MeetLayout.ArrivalSeconds;
        }

        static MeetPoint Along(List<MeetPoint> path, float s)
        {
            for (int i = 1; i < path.Count; i++)
            {
                float seg = path[i - 1].DistanceTo(path[i]);
                if (s <= seg || i == path.Count - 1)
                {
                    float t = seg > 0f ? Mathf.Clamp01(s / seg) : 1f;
                    return new MeetPoint(path[i - 1].X + (path[i].X - path[i - 1].X) * t, path[i - 1].Z + (path[i].Z - path[i - 1].Z) * t);
                }
                s -= seg;
            }
            return path[path.Count - 1];
        }

        void PoseArrival(float u, float dt)
        {
            float travel = MeetLayout.ArrivalMetres * (1f - Mathf.Pow(1f - Mathf.Clamp01(u), 1.5f));
            float s = arrivalStartS + travel;
            MeetPoint here = Along(arrivalPath, s);
            // Heading from a point behind to a point ahead rounds the corners of the polyline.
            MeetPoint behind = Along(arrivalPath, s - 2.2f), ahead = Along(arrivalPath, s + 2.2f);
            MeetBay b = MeetLayout.Bays[PlayerBay];
            float yaw = u >= 1f ? b.Yaw : Mathf.Atan2(ahead.X - behind.X, ahead.Z - behind.Z) * Mathf.Rad2Deg;
            float steer = u >= 1f ? 0f : Mathf.Clamp(Mathf.DeltaAngle(Mathf.Atan2(here.X - behind.X, here.Z - behind.Z) * Mathf.Rad2Deg, yaw) * 2.2f, -30f, 30f);
            PlayerCar.ShowRolling(new Vector3(here.X, 0f, here.Z), Quaternion.Euler(0f, yaw, 0f), steer, travel);
            if (engine != null)
            {
                float speed = u >= 1f ? 0f : MeetLayout.ArrivalMetres * 1.5f * Mathf.Pow(1f - u, 0.5f) / MeetLayout.ArrivalSeconds;
                VehicleParams p = PlayerCar.Params;
                engine.SetInputs(Mathf.Lerp(p.IdleRpm, p.IdleRpm * 2.6f, Mathf.Clamp01(speed / 14f)), u < 0.4f ? 0.3f : 0f, 0.2f, 0f, speed > 1f ? 2 : 0, false,
                    speed, 0f, SurfaceKind.Asphalt, 1f, 0f, 0f);
            }
        }

        void FinishArrival()
        {
            PoseArrival(1f, 0f);
            AddCarCollider(PlayerBay);
            Camera.Presenting = null;
            if (engine != null) { Destroy(engine); engine = null; }
            CharacterLook look = PlayerLook ?? DefaultPlayerLook(PlayerName);
            CharacterRig rig = CharacterRig.Create(look, null, null, "PlayerAvatar");
            spawned.Add(rig.gameObject);
            PlayerMotion = rig.gameObject.AddComponent<CharacterMotion>();
            Player = rig.gameObject.AddComponent<AvatarWalker>();
            GetOut();
            Camera.Target = Player.transform;
            Camera.PivotHeight = rig.Skeleton.H * 0.86f;
            Camera.Recenter();
            State = Phase.Walking;
            Hud.Notify($"Parked in bay {PlayerBay + 1}: {CarName}", "parked");
            if (Net != null) StartCoroutine(ArrivedOnline());
            Note($"arrived in bay {PlayerBay + 1} after {arrivalT:F2} s (skippable flourish)");
            Touring(TouringAct.Arrived);
        }

        /// <summary>Out of the car at a validated free point beside it (the driver's door when clear).</summary>
        void GetOut()
        {
            var others = npcs.Select(n => new MeetPoint(n.Rig.transform.position.x, n.Rig.transform.position.z)).ToList();
            MeetLayout.TryFreeSpot(PlayerBay, occupied, others, out MeetPoint p, driverSide);
            MeetBay b = MeetLayout.Bays[PlayerBay];
            Player.Teleport(new Vector3(p.X, 0f, p.Z), b.Yaw);
            Player.gameObject.SetActive(true);
        }

        // ------------------------------------------------------------------ frame

        void Update()
        {
            float dt = Time.deltaTime;
            if (State == Phase.Loading)
            {
                Hud?.Tick(dt);
                return;
            }
            Hud.Tick(dt);
            if (Net != null) OnlineFrame(dt);
            Boombox.Tick(NowMs);
            UpdateNpcs(dt);
            switch (State)
            {
                case Phase.Arriving:
                    // Any of interact, menu or jog skips the flourish (never teleporting through anything: the bay is reserved).
                    if (controls.InteractPressed || controls.MenuPressed || controls.JogHeld) arrivalT = MeetLayout.ArrivalSeconds;
                    arrivalT += dt;
                    float u = arrivalT / MeetLayout.ArrivalSeconds;
                    PoseArrival(Mathf.Min(1f, u), dt);
                    Hud.SetPrompt($"Arriving at bay {PlayerBay + 1} · {controls.BindingLabel("Interact")} to skip");
                    if (u >= 1f) FinishArrival();
                    break;
                case Phase.Walking: Walking(dt); break;
                case Phase.Wheel: Wheel(dt); break;
                case Phase.Panel: PanelFrame(dt); break;
                case Phase.Photo: PhotoFrame(dt); break;
                case Phase.InCar: InCarFrame(dt); break;
            }
            Labels();
            Music();
            bool lockCursor = State == Phase.Walking || State == Phase.Photo || State == Phase.InCar && !Hud.PanelOpen;
            Cursor.lockState = lockCursor && Application.isFocused ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !lockCursor;
        }

        Vector2 LookDegrees(float dt)
        {
            float sens = DrivingPreferences.Current.LookSensitivity;
            Vector2 l = controls.Look;
            return controls.LookIsRate ? l * 60f * dt * sens : l * sens;
        }

        void Walking(float dt)
        {
            Hud.SetStatus("");
            Vector2 move = ScriptMove != null ? ScriptMove(Time.time) : controls.Move;
            Quaternion yaw = Quaternion.Euler(0f, Camera.Yaw, 0f);
            Player.Frozen = false;
            Player.Intent = yaw * new Vector3(move.x, 0f, move.y);
            Player.Jog = ScriptJog || controls.JogHeld;
            if (move.sqrMagnitude > 0.04f && PlayerMotion.EmoteActive && PlayerMotion.Emote != Emote.Admire) PlayerMotion.Stop();
            if (move.sqrMagnitude > 0.04f && PlayerMotion.Emote == Emote.Admire) PlayerMotion.Stop();
            Camera.Look(LookDegrees(dt), controls.Zoom, move.sqrMagnitude > 0.04f, dt);
            if (controls.RecenterPressed) Camera.Recenter();

            focus = Nearest();
            Hud.SetPrompt(focus != null ? $"{controls.BindingLabel("Interact")}  {focus.Label}" : "");
            if (controls.InteractPressed && focus != null) Open(focus);
            int key = controls.EmoteKeyPressed;
            if (key >= 0) PlayEmote(Emotes.Wheel[key]);
            if (controls.EmoteWheelHeld)
            {
                State = Phase.Wheel;
                wheelVec = Vector2.zero;
                wheelSel = -1;
            }
            if (controls.QuickChatPressed) OpenQuickChat();
            if (controls.PhotoPressed) EnterPhoto(false);
            if (controls.MenuPressed) OpenMenu();
            // Rescue: hold to return to a validated free point beside your own car.
            if (controls.RescueHeld)
            {
                rescueHeld += dt;
                Hud.SetRescue(rescueHeld / 1.2f);
                if (rescueHeld >= 1.2f)
                {
                    rescueHeld = 0f;
                    Hud.SetRescue(0f);
                    Rescue();
                }
            }
            else if (rescueHeld > 0f)
            {
                rescueHeld = 0f;
                Hud.SetRescue(0f);
            }
        }

        /// <summary>Back to a validated free point beside your own car (never a teleport anywhere else).</summary>
        public void Rescue()
        {
            GetOut();
            Camera.Recenter();
            Hud.Notify("Back beside your car", null);
            Note("rescue to car");
        }

        Spot Nearest()
        {
            if (Player == null) return null;
            Vector3 me = Player.transform.position, fwd = Player.transform.forward;
            Spot best = null;
            float bestScore = float.MaxValue;
            foreach (Spot s in spots)
            {
                Vector3 d = s.Pos - me;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist > s.Range) continue;
                float facing = dist < 0.6f ? 1f : Vector3.Dot(fwd, d / dist);
                if (facing < -0.3f) continue;
                float score = dist - facing * 0.8f;
                if (score < bestScore) { bestScore = score; best = s; }
            }
            if (Net != null) best = NearestRemote(me, fwd, best, ref bestScore);
            // Your own car.
            MeetBay b = MeetLayout.Bays[PlayerBay];
            Vector3 car = new Vector3(b.X, 0f, b.Z);
            float cd = Vector3.Distance(new Vector3(me.x, 0f, me.z), car);
            if (cd < 3.4f && (best == null || cd - 1.2f < bestScore))
                best = new Spot { Kind = "own-car", Id = "own", Label = $"Your {CarName}", Pos = car, Range = 3.4f };
            return best;
        }

        // ------------------------------------------------------------------ emotes

        public void PlayEmote(Emote e)
        {
            if (Player == null) return;
            Player.Intent = Vector3.zero;
            PlayerMotion.Play(e);
            Net?.Fire("meet.emote", new { emote = e.ToString() });
            Note($"emote {e}");
            // The host returns a wave or a bow made within a few metres (the tutorial lesson).
            if (host != null && (e == Emote.Wave || e == Emote.Bow) && Vector3.Distance(host.Rig.transform.position, Player.transform.position) < 7f)
            {
                hostReply = e;
                hostReplyAt = Time.time + 0.6f;
                if (e == Emote.Wave) HostWaved = true; else HostBowed = true;
                CheckLesson();
                if (Net == null) Touring(e == Emote.Wave ? TouringAct.WaveAtHost : TouringAct.BowToHost);
            }
        }

        void CheckLesson()
        {
            if (HostWaved && HostBowed && HostHelpRead && Text.AfterGreeting.Count > 0)
            {
                Hud.Notify($"{Text.HostName}: {Text.AfterGreeting[0]}", "lesson-done");
                Note("host lesson: wave, bow and emote help done");
            }
        }

        void Wheel(float dt)
        {
            Player.Frozen = true;
            Vector2 look = controls.Look, stick = controls.Move;
            wheelVec = Vector2.ClampMagnitude(wheelVec + (controls.LookIsRate ? Vector2.zero : look * 2f), 120f);
            Vector2 v = stick.sqrMagnitude > 0.25f ? stick * 120f : controls.LookIsRate && look.sqrMagnitude > 0.1f ? look.normalized * 120f : wheelVec;
            if (v.magnitude > 40f)
            {
                float ang = Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;
                wheelSel = Mathf.RoundToInt(Mathf.Repeat(ang, 360f) / 30f) % 12;
            }
            int key = controls.EmoteKeyPressed;
            if (key >= 0) wheelSel = key;
            Hud.ShowWheel(Emotes.Wheel.Select(Emotes.Label).ToList(), wheelSel, wheelSel >= 0 ? Emotes.Label(Emotes.Wheel[wheelSel]) : "Pick an emote");
            if (!controls.EmoteWheelHeld || controls.EmoteWheelReleased)
            {
                Hud.HideWheel();
                State = Phase.Walking;
                Player.Frozen = false;
                if (wheelSel >= 0) PlayEmote(Emotes.Wheel[wheelSel]);
            }
            if (controls.MenuPressed)
            {
                Hud.HideWheel();
                State = Phase.Walking;
            }
        }

        // ------------------------------------------------------------------ panels

        void ShowPanel(string title, string body, List<(string, Action)> actions)
        {
            if (actions == null) actions = new List<(string, Action)>();
            actions.Add(("Close", ClosePanel));
            Hud.ShowPanel(title, body, actions);
            if (State != Phase.InCar) State = Phase.Panel;
            if (Player != null) Player.Frozen = true;
        }

        public void ClosePanel()
        {
            Hud.HidePanel();
            if (Boombox.LeaseHolder == PlayerName) Boombox.Release(PlayerName);
            if (Net != null && boomboxOpen) Net.Fire("meet.boombox", new { op = "release" });
            boomboxOpen = false;
            if (State == Phase.Panel) State = Phase.Walking;
            if (Player != null) Player.Frozen = false;
        }

        void PanelFrame(float dt)
        {
            Player.Frozen = true;
            if (controls.MenuPressed) { ClosePanel(); return; }
            // Controls release when the player walks out of range (they cannot here, but the rule belongs to the lease).
            if (Hud.PanelTitle == "Boombox")
            {
                bool inRange = Vector3.Distance(Player.transform.position, new Vector3(MeetLayout.Boombox.X, 0f, MeetLayout.Boombox.Z)) < 3f;
                if (Net != null)
                {
                    // The room's lease lasts 15 s: renew it while the panel stays open and in range.
                    if (!inRange) { ClosePanel(); return; }
                    if (Time.unscaledTime >= nextLeaseRenew)
                    {
                        nextLeaseRenew = Time.unscaledTime + 8f;
                        Net.Fire("meet.boombox", new { op = "acquire" });
                    }
                }
                else if (Boombox.Acquire(PlayerName, NowMs, inRange) != BoomboxStatus.Ok) { ClosePanel(); return; }
            }
        }

        public void Open(string kind, string id = null)
        {
            Spot s = spots.FirstOrDefault(x => x.Kind == kind && (id == null || x.Id == id));
            if (kind == "own-car") s = new Spot { Kind = "own-car", Id = "own", Label = CarName };
            if (s != null) Open(s);
        }

        void Open(Spot s)
        {
            Note($"interact {s.Kind} {s.Id}");
            switch (s.Kind)
            {
                case "host": OpenHost(0); break;
                case "npc":
                    ShowPanel(s.Npc.Name, $"{CrewName(s.Npc.Crew)} · rival (story character)\n\n\"{s.Npc.Intro}\"", null);
                    s.Npc.Rig.transform.rotation = Quaternion.LookRotation(Flat(Player.transform.position - s.Npc.Rig.transform.position));
                    s.Npc.Motion.Play(Emote.Nod);
                    break;
                case "car": InspectNpcCar(s.Npc); break;
                case "own-car": OwnCar(); break;
                case "epilogue": ShowEpilogue(0); break;
                case "remote-car": InspectRemoteCar(s.Id); break;
                case "remote-person": GreetRemote(s.Id); break;
                case "placard":
                {
                    MeetText.Placard t = Text.Find(s.Id);
                    ShowPanel(t != null ? t.Title : s.Label, t != null ? t.Text : "", null);
                    Touring(TouringAct.ReadPlacard, s.Id);
                    break;
                }
                case "board":
                    ShowPanel("Timing board", BoardText(true), null);
                    Touring(TouringAct.ReadResultSlip);
                    break;
                case "boombox": OpenBoombox(); break;
                case "photo-marker": EnterPhoto(true); break;
            }
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z).sqrMagnitude < 1e-4f ? Vector3.forward : new Vector3(v.x, 0f, v.z);

        string CrewName(string crew) => lib.Catalogue.Crews.FirstOrDefault(c => c.Id == crew)?.Name ?? crew;

        void OpenHost(int page)
        {
            var lines = new List<string>();
            foreach (string l in Text.Greeting) lines.Add(l.Replace("{player}", PlayerName));
            host.Rig.transform.rotation = Quaternion.LookRotation(Flat(Player.transform.position - host.Rig.transform.position));
            if (page == 0) host.Motion.Play(Emote.Wave);
            var actions = new List<(string, Action)>();
            if (page == 0) actions.Add(("How do greetings work?", () => { HostHelpRead = true; OpenHostHelp(); CheckLesson(); Touring(TouringAct.ReadEmoteHelp); }));
            ShowPanel(Text.HostName, $"<color=#9A968D>{Text.HostRole}</color>\n\n" + string.Join("\n\n", lines), actions);
        }

        void OpenHostHelp()
        {
            string wave = controls.BindingLabel("EmoteWheel");
            ShowPanel($"{Text.HostName}: emote help", string.Join("\n\n", Text.EmoteHelp) +
                $"\n\n<color=#9A968D>Hold {wave} for the emote wheel, or press 1 (wave) and 2 (bow). Quick chat: {controls.BindingLabel("QuickChat")}.</color>", null);
        }

        void InspectNpcCar(Npc n)
        {
            CarDef c = lib.Catalogue.Cars.FirstOrDefault(x => x.Id == n.CarId);
            int pi = c?.BasePI ?? 0;
            CharacterLook look = lib.Look(n.Id);
            string body = $"Owner: {n.Name} · {CrewName(n.Crew)} (rival, story character — not a connected player)\n" +
                          $"PI {pi} · class {Core.Rules.PerformanceIndex.ClassOf(pi)} · stock build\n" +
                          $"Tune: stock factory settings\n" +
                          $"Visible customization: paint {look?.Primary ?? "—"}\n\n" +
                          $"{c?.Maker} {c?.Model} · {c?.Drive} · {c?.EngineLayout}-engined\n\n" +
                          (n.Liked ? "You liked this car (cosmetic only: no currency or RP)." : "Likes are cosmetic feedback only: no currency or RP.");
            ShowPanel($"{n.CarId} {c?.Name}", body, new List<(string, Action)>
            {
                (n.Liked ? "Unlike" : "Like", () => { n.Liked = !n.Liked; InspectNpcCar(n); }),
            });
        }

        void OwnCar()
        {
            Touring(TouringAct.InspectOwnCar);
            ShowPanel($"Your {CarName}", $"{CarId} · PI {CarPi} · class {Core.Rules.PerformanceIndex.ClassOf(CarPi)}\nTune: {TuneSummary}\n\nParked in bay {PlayerBay + 1}.",
                new List<(string, Action)>
                {
                    ("Sit in the car", SitIn),
                    ("Walk round it (inspect)", () => { ClosePanel(); Camera.Distance = 5.5f; Hud.Notify("Walk round your car; the camera pulls back", null); }),
                });
        }

        public void SitIn()
        {
            Hud.HidePanel();
            Hud.SetPrompt("");
            State = Phase.InCar;
            Player.gameObject.SetActive(false);
            PlayerCar.SetCockpitMode(true);
            engine = PlayerCar.gameObject.AddComponent<EngineAudio>();
            engine.Configure(PlayerCar.Params, CarId);
            Camera.Target = null;
            Camera.enabled = false;
            Camera.Cam.nearClipPlane = 0.03f;
            inCarYaw = inCarPitch = 0f;
            Note("sat in own car");
            ShowCarOptions();
        }

        public bool RevNow() => engine != null && engine.TryMeetRev();

        void ShowCarOptions()
        {
            string[] presets = { "Off", "Low beam", "Low + fog glow" };
            Hud.ShowPanel("In your car", $"Headlights: {presets[headlightPreset]}\nWheels: {(carSteer == 0f ? "straight" : carSteer < 0f ? "turned left" : "turned right")}\n\nThe car stays parked: meet cars never drive into the plaza.",
                new List<(string, Action)>
                {
                    ("Rev (brief)", () =>
                    {
                        bool ok = engine != null && engine.TryMeetRev();
                        Hud.Notify(ok ? "Rev" : $"Rev available again in a moment ({EngineAudio.MeetRevCooldown:0} s limit)", null);
                        Note(ok ? "rev" : "rev refused (rate limit)");
                    }),
                    ("Headlight preset", () => { headlightPreset = (headlightPreset + 1) % 3; PlayerCar.SetHeadlights(headlightPreset > 0); ShowCarOptions(); }),
                    ("Turn the wheels", () => { carSteer = carSteer == 0f ? -28f : carSteer < 0f ? 28f : 0f; PoseParked(); ShowCarOptions(); }),
                    ("Get out", GetOutOfCar),
                });
        }

        void PoseParked()
        {
            MeetBay b = MeetLayout.Bays[PlayerBay];
            PlayerCar.ShowParked(new Vector3(b.X, 0f, b.Z), Quaternion.Euler(0f, b.Yaw, 0f), carSteer);
        }

        void InCarFrame(float dt)
        {
            CockpitRig rig = PlayerCar.Cockpit;
            Vector3 eye = rig?.Frame != null ? PlayerCar.Body.TransformPoint(rig.Frame.Eye) : PlayerCar.transform.position + Vector3.up * 1.1f;
            Vector2 look = LookDegrees(dt);
            inCarYaw = Mathf.Clamp(inCarYaw + look.x, -75f, 75f);
            inCarPitch = Mathf.Clamp(inCarPitch - look.y, -25f, 30f);
            Camera.transform.SetPositionAndRotation(eye, PlayerCar.transform.rotation * Quaternion.Euler(inCarPitch, inCarYaw, 0f));
            if (engine != null) engine.SetInputs(PlayerCar.Params.IdleRpm, 0f, 0.1f, 0f, 0, false, 0f, 0f, SurfaceKind.Asphalt, 1f, 0f, 0f);
            rig?.Update(carSteer * Mathf.Deg2Rad, 0f, DrivingPreferences.Current.Unit, PlayerCar.Params.IdleRpm, PlayerCar.Params.RedlineRpm, 0);
            if (controls.MenuPressed)
            {
                if (Hud.PanelOpen) Hud.HidePanel();
                else GetOutOfCar();
            }
            else if (controls.InteractPressed && !Hud.PanelOpen) ShowCarOptions();
        }

        float inCarYaw, inCarPitch, nextLeaseRenew;

        public void GetOutOfCar()
        {
            Hud.HidePanel();
            PlayerCar.SetCockpitMode(false);
            if (engine != null) { Destroy(engine); engine = null; }
            Camera.enabled = true;
            Camera.Cam.nearClipPlane = 0.08f;
            Camera.Target = Player.transform;
            GetOut();
            Camera.Recenter();
            State = Phase.Walking;
            Note("got out of the car");
        }

        // ------------------------------------------------------------------ boombox

        public void OpenBoombox()
        {
            if (Net != null)
            {
                OpenBoomboxOnline();
                return;
            }
            BoomboxStatus s = Boombox.Acquire(PlayerName, NowMs);
            if (s != BoomboxStatus.Ok)
            {
                Hud.Notify(s == BoomboxStatus.LeaseHeld ? "Someone else is choosing music" : "Walk up to the boombox", null);
                return;
            }
            RefreshBoombox();
        }

        void RefreshBoombox()
        {
            if (Net != null)
            {
                RefreshBoomboxOnline();
                return;
            }
            MusicPlayer mp = MusicPlayer.Instance;
            bool protect = DrivingPreferences.Current.ProtectBossMusic;
            string Title(string id)
            {
                string t = id;
                if (mp != null) mp.CueInfo(id, out t, out _);
                return BoomboxState.TitleFor(id, t, OwnsCue, protect);
            }
            var sb = new System.Text.StringBuilder();
            string playing = BoomboxState.AudibleFor(Boombox.TrackId, OwnsCue, protect);
            sb.AppendLine($"Now playing: <b>{Title(Boombox.TrackId)}</b>" + (playing != Boombox.TrackId ? " <color=#9A968D>(you hear the meet bed: spoiler protection)</color>" : ""));
            sb.AppendLine(Boombox.Queue.Count == 0 ? "Queue: empty" : "Queue: " + string.Join(" · ", Boombox.Queue.Select(r => Title(r.TrackId) + (r.PlayerId == PlayerName ? " (yours)" : ""))));
            sb.AppendLine();
            var locked = new List<string>();
            var actions = new List<(string, Action)>();
            foreach (string id in MusicCueIdsAll())
                if (!OwnsCue(id)) locked.Add(BoomboxState.IsProtected(id) ? $"Locked encounter theme — {Hint(id)}" : $"Locked: {Title(id)} — {Hint(id)}");
            AddCuePage(actions, Title, Queue, RefreshBoombox);
            if (locked.Count > 0) sb.AppendLine("<color=#9A968D>" + string.Join("\n", locked.Take(8)) + (locked.Count > 8 ? $"\n… and {locked.Count - 8} more" : "") + "</color>");
            sb.AppendLine();
            sb.AppendLine($"One request each · up to {BoomboxState.MaxQueue} queued · a change at most every {BoomboxState.MinChangeIntervalMs / 1000} s.");
            actions.Insert(0, ("Skip to the next request", () => { Hud.Notify(Status(Boombox.Skip(PlayerName, NowMs)), null); RefreshBoombox(); }));
            actions.Insert(1, ($"Protect unreached boss music: {(protect ? "ON" : "OFF")}", () =>
            {
                DrivingPreferences prefs = DrivingPreferences.Current;
                prefs.ProtectBossMusic = !prefs.ProtectBossMusic;
                prefs.Save();
                RefreshBoombox();
            }));
            ShowPanel("Boombox", sb.ToString(), actions);
        }

        public void Queue(string cue)
        {
            if (Net != null)
            {
                BoomboxOp("queue", cue);
                return;
            }
            BoomboxStatus s = Boombox.Enqueue(PlayerName, cue, OwnsCue(cue), NowMs);
            Hud.Notify(Status(s), null);
            Note($"boombox queue {cue}: {s}");
            RefreshBoombox();
        }

        static string Status(BoomboxStatus s)
        {
            switch (s)
            {
                case BoomboxStatus.Ok: return "Boombox updated";
                case BoomboxStatus.TooSoon: return "The music changed a moment ago — try again shortly";
                case BoomboxStatus.QueueFull: return "The queue is full";
                case BoomboxStatus.NotOwned: return "Only music you have unlocked can be queued";
                case BoomboxStatus.NothingQueued: return "Nothing queued";
                case BoomboxStatus.LeaseHeld: return "Someone else is choosing music";
                default: return s.ToString();
            }
        }

        string Hint(string id) => unlockHints != null && unlockHints.TryGetValue(id, out string h) ? h : "keep racing";

        static IEnumerable<string> MusicCueIdsAll() => NightSignal.AudioSynth.MusicCueIds.All;

        int cuePage;
        const int CuesPerPage = 8;

        /// <summary>The owned cues as queue buttons, eight at a time, with a button to the next page when there are more.</summary>
        void AddCuePage(List<(string, Action)> actions, Func<string, string> title, Action<string> queue, Action refresh)
        {
            List<string> owned = MusicCueIdsAll().Where(id => OwnsCue(id)).ToList();
            int pages = Mathf.Max(1, (owned.Count + CuesPerPage - 1) / CuesPerPage);
            cuePage = Mathf.Clamp(cuePage, 0, pages - 1);
            foreach (string id in owned.Skip(cuePage * CuesPerPage).Take(CuesPerPage))
            {
                string cue = id;
                actions.Add(($"Queue: {title(id)}", () => queue(cue)));
            }
            if (pages > 1)
                actions.Add(($"More music ({cuePage + 1} of {pages})", () => { cuePage = (cuePage + 1) % pages; refresh(); }));
        }

        /// <summary>Near the boombox you hear its track; further out the meet bed (never two full-volume tracks at once).</summary>
        void Music()
        {
            MusicPlayer mp = MusicPlayer.Instance;
            if (mp == null || Player == null) return;
            float d = Vector3.Distance(Player.transform.position, new Vector3(MeetLayout.Boombox.X, 0f, MeetLayout.Boombox.Z));
            nearBoombox = nearBoombox ? d < 26f : d < 20f;
            string track = Net != null ? OnlineTrack() : Boombox.TrackId;
            string want = nearBoombox ? BoomboxState.AudibleFor(track, OwnsCue, DrivingPreferences.Current.ProtectBossMusic) : BoomboxState.DefaultCue;
            mp.Play(want, 2.5f);
        }

        // ------------------------------------------------------------------ quick chat, photo, menu

        void OpenQuickChat()
        {
            var actions = new List<(string, Action)>();
            foreach (string phrase in Text.QuickChat.Take(8))
            {
                string p = phrase;
                actions.Add((p, () => { Say(p); ClosePanel(); }));
            }
            ShowPanel("Quick chat", "Short, friendly phrases — no typing. Offline, only you see them.", actions);
        }

        public void Say(string phrase)
        {
            bubbleText = phrase;
            bubbleUntil = Time.time + 4f;
            if (Net != null && Text.QuickChat.IndexOf(phrase) >= 0) Net.Fire("meet.chat", new { index = Text.QuickChat.IndexOf(phrase) });
            Note($"quick chat: {phrase}");
        }

        public void EnterPhoto(bool atMarker)
        {
            if (atMarker)
            {
                Player.Teleport(new Vector3(MeetLayout.PhotoMarker.X, 0f, MeetLayout.PhotoMarker.Z), MeetLayout.PhotoYaw);
                Camera.Yaw = MeetLayout.PhotoYaw;
                Camera.Pitch = 4f;
            }
            State = Phase.Photo;
            Camera.Photo = true;
            Player.Frozen = true;
            photoHint = $"PHOTO MODE · move {controls.BindingLabel("Move")} · look · zoom · {controls.BindingLabel("Interact")} save a photo · {controls.BindingLabel("Photo")} leave · nameplates hidden";
            Hud.SetPhotoMode(true, photoHint);
            Note(atMarker ? "photo mode at the marker" : "photo mode");
        }

        string photoHint = "";

        bool SavePhoto()
        {
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "Photos");
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, $"meet-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            ScreenCapture.CaptureScreenshot(file);
            bool composed = PhotoComposition(out bool marker, out bool car, out bool horizon);
            Note($"photo saved (composition: marker {marker}, car {car}, horizon {horizon})");
            if (composed) Touring(TouringAct.PhotoComposed);
            return composed;
        }

        public void ExitPhoto()
        {
            Camera.Photo = false;
            Hud.SetPhotoMode(false, "");
            Player.Frozen = false;
            State = Phase.Walking;
        }

        void PhotoFrame(float dt)
        {
            Camera.Look(LookDegrees(dt), controls.Zoom, false, dt);
            Camera.PhotoMove(controls.Move, dt);
            Hud.SetPhotoMode(true, photoHint + "\n" + CompositionLine());
            if (controls.InteractPressed) SavePhoto();
            if (controls.PhotoPressed || controls.MenuPressed) ExitPhoto();
        }

        /// <summary>Automation: open the meet menu as the Menu control does.</summary>
        public void OpenMeetMenu() => OpenMenu();

        void OpenMenu()
        {
            if (Net != null)
            {
                OpenMenuOnline();
                return;
            }
            ShowPanel("Meet", "Cedar Lantern Terrace · offline meet.\n\nLeaving returns you to the menus. Only the terrace's touring challenges (CH61–CH65) pay out here; nothing else costs or earns anything.",
                new List<(string, Action)> { ("Leave the meet", Leave) });
        }

        public void Leave()
        {
            Note("leave requested");
            if (Net != null) StartCoroutine(LeaveOnline());
            else ExitRequested = true;
        }

        string Hints() =>
            $"Move {controls.BindingLabel("Move")} · Jog {controls.BindingLabel("Jog")} · Interact {controls.BindingLabel("Interact")}\n" +
            $"Emotes {controls.BindingLabel("EmoteWheel")} (or 1–0, -, =) · Quick chat {controls.BindingLabel("QuickChat")} · Camera {controls.BindingLabel("Recenter")}\n" +
            $"Photo {controls.BindingLabel("Photo")} · Rescue to car: hold {controls.BindingLabel("Rescue")} · Menu {controls.BindingLabel("Menu")}";

        // ------------------------------------------------------------------ NPCs and labels

        void UpdateNpcs(float dt)
        {
            if (host != null && hostReplyAt > 0f && Time.time >= hostReplyAt)
            {
                host.Rig.transform.rotation = Quaternion.LookRotation(Flat(Player.transform.position - host.Rig.transform.position));
                host.Motion.Play(hostReply);
                hostReplyAt = -1f;
                Note($"host returned the {hostReply}");
            }
            Emote[] idle = { Emote.Admire, Emote.Nod, Emote.Point, Emote.Stretch, Emote.ThumbsUp, Emote.Shrug };
            foreach (Npc n in npcs)
            {
                if (n == host || Time.time < n.NextEmote) continue;
                n.Motion.Play(idle[(n.Id.GetHashCode() & 0x7fffffff) % idle.Length]);
                n.NextEmote = Time.time + 14f + (n.Id[n.Id.Length - 1] % 7) * 2.3f;
            }
        }

        void Labels()
        {
            UnityEngine.Camera cam = Camera != null ? Camera.Cam : null;
            bool show = State == Phase.Walking || State == Phase.Panel || State == Phase.Wheel;
            foreach (Npc n in npcs)
            {
                string tag = n == host ? $"{n.Name}\n<size=70%>Terrace host</size>" : $"{n.Name}\n<size=70%>{CrewName(n.Crew)} · rival</size>";
                Vector3 p = n.Rig.transform.position;
                Hud.Nameplate(n, cam, p + Vector3.up * (n.Rig.Skeleton.H + 0.35f), tag, show);
            }
            if (Net != null) RemoteLabels(cam, show);
            if (Player != null)
                Hud.Bubble(cam, Player.transform.position + Vector3.up * (Player.GetComponent<CharacterRig>().Skeleton.H + 0.4f), Time.time < bubbleUntil ? bubbleText : "");
        }
    }
}
