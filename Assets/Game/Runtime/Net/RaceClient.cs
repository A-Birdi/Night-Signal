using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NightSignal.Race;
using NightSignal.AI;
using NightSignal.Art;
using NightSignal.Cameras;
using NightSignal.Content;
using NightSignal.Core.Rules;
using NightSignal.InputBindings;
using NightSignal.Track;
using NightSignal.Vehicle;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NightSignal.Net
{
    /// <summary>
    /// Networked race client (spec §18): immediate local control of the own car with prediction and replay-based
    /// reconciliation against authoritative snapshots; remote cars interpolated ~100 ms in the past with bounded
    /// extrapolation. The client never reports positions, times or results — only inputs.
    /// </summary>
    public sealed class RaceClient : MonoBehaviour
    {
        const int InterpolationTicks = Limits.RemoteInterpolationBufferMs * 60 / 1000;  // 6
        /// <summary>Ticks the local prediction runs ahead of the network clock (commands arrive before they are needed).</summary>
        public const int InputLeadTicks = 2;
        const int MaxExtrapolationTicks = Limits.MaxRemoteExtrapolationMs * 60 / 1000;  // 9

        sealed class Car
        {
            public RosterEntry Roster;
            public EntrantStatus Status;
            public int CheckpointsPassed;
            public float RaceDistance;
            public int FinishMillis;
            public VehicleView View;
            public VehicleParams Params;
            /// <summary>Server flag: not colliding (reset safety ghost, DQ, not racing).</summary>
            public bool Ghost;
            public int LatestTick = -1;
            public VehicleState Latest;
            public readonly List<(int tick, VehicleState state)> Buffer = new List<(int, VehicleState)>();
            /// <summary>Server race distance by snapshot time, for the interval to the car ahead (cleared when it goes back: a recovery).</summary>
            public readonly Race.DistanceHistory History = new Race.DistanceHistory();
        }

        public bool Autopilot;
        /// <summary>
        /// Played after the course has loaded and before this client reports itself loaded (the stage intro, spec §5.3): the
        /// server's loading barrier then starts the race only when every driver has finished or skipped it — a skip leads to
        /// the calm "waiting for all drivers" view, never to anyone else's start. Null or headless: nothing is played.
        /// </summary>
        public System.Func<MatchInfo, IEnumerator> Presentation;
        /// <summary>
        /// Ghosts (spec §8) for online Time Attack: the source fetches the player's kept (server-settled) ghosts; only those
        /// recorded under this event's rules race, at most three, placed by interpolation at this client's race time.
        /// </summary>
        /// <summary>Time Attack ghost offers, each with its label ("your best", "Mika's best"); up to three compatible ones race.</summary>
        public System.Func<MatchInfo, System.Threading.Tasks.Task<List<KeyValuePair<string, Core.Ghosts.GhostRecording>>>> GhostSource;
        public readonly List<GhostPlayback> Ghosts = new List<GhostPlayback>();
        /// <summary>Overlay tints in offer order: your best cyan, a convoy member's amber, a third violet.</summary>
        static readonly Color[] GhostTints = { new Color(0.35f, 0.85f, 1f), new Color(1f, 0.72f, 0.3f), new Color(0.75f, 0.55f, 1f) };
        /// <summary>The time against the first ghost at each checkpoint the server reported (evidence).</summary>
        public readonly List<long> GhostDeltasMicros = new List<long>();
        int ghostCheckpoints;
        string ghostDelta = "";
        float ghostDeltaUntil;

        /// <summary>
        /// Spectating (spec §4.4): the match was joined with a spectator ticket, so there is no car of our own — the camera
        /// follows one entrant at a time (next/previous), moves on by itself when that car leaves (DQ, disconnect), and
        /// shows a safe empty state when nobody is left. Nothing is predicted and no input is sent.
        /// </summary>
        public bool Spectating => Info != null && Info.YourIndex < 0;
        int watching = -1;
        public int Watching => watching;
        /// <summary>Evidence: target switches, targets lost (the watched car left), and a short log.</summary>
        public int SpectateSwitches, SpectateTargetLosses;
        /// <summary>
        /// Evidence: own-car frames/ticks with a non-finite value (seen as NaN wheel transforms at race starts in rendered
        /// clients), by where it first appeared. Each source is logged the first few times; nothing non-finite is drawn.
        /// </summary>
        public int NonFiniteEvents;
        readonly Dictionary<string, int> nonFiniteBySource = new Dictionary<string, int>();

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        static bool Finite(in VehicleState s) =>
            Finite(s.Position.x) && Finite(s.Position.y) && Finite(s.Position.z) && Finite(s.Velocity.x) && Finite(s.Velocity.y) && Finite(s.Velocity.z)
            && Finite(s.Rotation.x) && Finite(s.Rotation.y) && Finite(s.Rotation.z) && Finite(s.Rotation.w) && Finite(s.SteerAngle)
            && Finite(s.C0) && Finite(s.C1) && Finite(s.C2) && Finite(s.C3);

        void NoteNonFinite(string source, string detail)
        {
            NonFiniteEvents++;
            nonFiniteBySource.TryGetValue(source, out int n);
            nonFiniteBySource[source] = n + 1;
            if (n < 3) Debug.LogWarning($"[NightSignal.Client] non-finite own-car value from {source}: {detail}");
        }
        public readonly List<string> SpectateLog = new List<string>();
        public MatchInfo Info { get; private set; }
        long driftBanked, driftUnbanked, driftLost;
        float driftChain = 1f;
        bool driftSeen;
        readonly UI.DriftHudFeed driftFeed = new UI.DriftHudFeed();
        /// <summary>This driver's banked raw drift score as the server last reported it (drift formats; 0 otherwise).</summary>
        public long DriftBanked => driftBanked;
        /// <summary>This client's own car as drawn (null before the views exist or headless).</summary>
        public VehicleView MyView => Info != null && cars.TryGetValue(Info.YourIndex, out Car c) ? c.View : null;
        /// <summary>The drawn car of any entrant (null headless or unknown).</summary>
        public VehicleView ViewOf(int index) => cars.TryGetValue(index, out Car c) ? c.View : null;
        public MatchPhase Phase { get; private set; } = MatchPhase.WaitingForEntrants;
        public MatchResults Results { get; private set; }
        public bool Connected => nm != null && nm.IsConnectedClient;
        public string DisconnectReason { get; private set; }
        public int Corrections { get; private set; }
        public float MaxCorrectionMetres { get; private set; }
        public int SnapshotsReceived { get; private set; }
        public int InputsSent { get; private set; }
        /// <summary>Ticks predicted in catch-up because the network clock advanced more than one tick in a frame.</summary>
        public int TicksFilled { get; private set; }
        /// <summary>Input-to-acknowledgement round trip measured by the game (ms): average and worst sample.</summary>
        public float InputAckMsAverage => ackSamples > 0 ? (float)(ackSumMs / ackSamples) : -1f;
        public float InputAckMsMax { get; private set; }
        /// <summary>How far ahead of the server's simulation our commands arrive (ticks; positive = on time).</summary>
        public int MinInputLeadTicks { get; private set; } = int.MaxValue;
        public EntrantStatus OwnStatus => cars.TryGetValue(Info?.YourIndex ?? -1, out Car c) ? c.Status : EntrantStatus.Reserved;

        NetworkManager nm;
        ContentLibrary lib;
        TrackData track;
        int startTick = int.MaxValue;
        bool loaded;
        readonly Dictionary<int, Car> cars = new Dictionary<int, Car>();
        VehicleParams ownParams;
        VehicleSimulation ownSim;
        VehicleState ownState;
        int lastPredictedTick = -1;
        readonly DriverInput[] inputs = new DriverInput[256];
        readonly VehicleState[] states = new VehicleState[256];
        readonly int[] ticks = new int[256];
        readonly double[] sendTimes = new double[256];
        // Not int.MinValue: "tick - lastSentTick" would overflow negative and inputs would never be sent (found in a real run).
        int lastSentTick = -1000, lastAckTick = -1, ackSamples;
        double ackSumMs;
        float rttSmoothedMs = -1f;
        long deadlineMicros = -1;
        Vector3 visualOffset, visualOffsetVelocity;
        /// <summary>Correction blending keeps velocity continuous as well as position (<c>-nsCorrectionBlend position</c> = off, for A/B runs).</summary>
        static readonly bool VelocityBlend = CorrectionBlendArg() != "position";

        static string CorrectionBlendArg()
        {
            string[] a = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(a, "-nsCorrectionBlend");
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
        RouteFollower autopilot;
        DrivingControls controls;
        DrivingCamera chase;
        UI.SpeedLines speedLines;
        Vector3 lastCameraCarPos;
        float resetHoldShown, overturnedShown, recoveryNoticeUntil;
        bool resetHoldSpent;
        // The server's recovery offer for this driver (kind, seconds to the marshal, when it arrived) and completed count.
        RecoveryKind serverRecoveryKind;
        float serverRecoverySeconds, serverRecoveryAt = -10f;
        int serverRecoveries = -1;
        string recoveryNotice = "", lastOfferLogged = "";
        /// <summary>Automation: hold reset for 1.2 s this many seconds after the start (<c>-nsAutoResetAt</c>).</summary>
        float autoResetAt = -1f;
        /// <summary>Completed recoveries the server reported for this driver (evidence).</summary>
        public int ServerRecoveries => serverRecoveries;

        // Application-level impairment for evidence runs (-nsImpair delayMs,jitterMs,dropPercent): incoming snapshots and
        // outgoing inputs are held back, jittered and dropped here, above the transport (the transport's own simulator is
        // not available in this Netcode version). Stale snapshots after jitter are discarded as the sequenced channel would.
        struct Delayed { public float At; public byte[] Data; public int Tick; }
        readonly List<Delayed> heldSnapshots = new List<Delayed>(), heldInputs = new List<Delayed>();
        bool impair;
        float impairDelayMs, impairJitterMs, impairDropPercent;
        readonly System.Random impairRng = new System.Random(7);
        int lastReleasedSnapshotTick = int.MinValue;
        /// <summary>Evidence: snapshots and input packets the impairment dropped.</summary>
        public int ImpairedSnapshotDrops, ImpairedInputDrops;
        bool latchUp, latchDown;
        bool headless;

        public void Connect(string host, ushort port, string ticket)
        {
            headless = Application.isBatchMode;
            lib = ContentLibrary.Load();
            string[] args = System.Environment.GetCommandLineArgs();
            int ai = System.Array.IndexOf(args, "-nsAutoResetAt");
            if (ai >= 0 && ai + 1 < args.Length) float.TryParse(args[ai + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out autoResetAt);
            int ii = System.Array.IndexOf(args, "-nsImpair");
            if (ii >= 0 && ii + 1 < args.Length)
            {
                string[] parts = args[ii + 1].Split(',');
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                impair = parts.Length == 3 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out impairDelayMs)
                         && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out impairJitterMs)
                         && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out impairDropPercent);
                if (impair) Debug.Log($"[NightSignal.Client] impairment: {impairDelayMs} ms ± {impairJitterMs} ms each way, {impairDropPercent} % loss (application level)");
            }
            for (int i = 0; i < ticks.Length; i++) ticks[i] = -1;
            nm = NetBootstrap.Ensure();
            // Addendum 04: to a loopback server the client's own socket binds loopback too (no all-interface socket for a
            // local run); otherwise the transport's default local endpoint.
            bool loopback = System.Net.IPAddress.TryParse(host, out System.Net.IPAddress hostIp) && System.Net.IPAddress.IsLoopback(hostIp);
            if (loopback) NetBootstrap.Transport(nm).SetConnectionData(host, port, "127.0.0.1");
            else NetBootstrap.Transport(nm).SetConnectionData(host, port);
            nm.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(ticket);
            nm.OnClientDisconnectCallback += id =>
            {
                if (id == nm.LocalClientId || !nm.IsConnectedClient) DisconnectReason = nm.DisconnectReason;
            };
            if (!nm.StartClient())
            {
                DisconnectReason = "could not start client";
                return;
            }
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgMatch, (id, r) => StartCoroutine(OnMatch(Wire.ReadJson(r))));
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgPhase, OnPhase);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgSnapshot, OnSnapshotArrived);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgRecovery, (id, r) =>
            {
                r.ReadValueSafe(out byte kind);
                r.ReadValueSafe(out float seconds);
                r.ReadValueSafe(out int count);
                r.ReadValueSafe(out byte reason);
                serverRecoveryKind = (RecoveryKind)kind;
                serverRecoverySeconds = seconds;
                serverRecoveryAt = Time.unscaledTime;
                if (serverRecoveries >= 0 && count > serverRecoveries)
                {
                    string why = reason == 1 ? "RESET" : reason == 2 ? "RECOVERED — OFF ROUTE" : reason == 3 ? "RECOVERED — OVERTURNED" : "RECOVERED";
                    recoveryNotice = $"{why}  <size=80%><color=#9A968D>+3.000 s · clock running</color></size>";
                    recoveryNoticeUntil = Time.unscaledTime + 2.5f;
                    Debug.Log($"[NightSignal.Client] recovery completed by the server: {(reason == 1 ? "manual" : reason == 2 ? "off-route" : reason == 3 ? "overturned" : "stuck")} (total {count})");
                }
                serverRecoveries = count;
            });
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgResults, (id, r) =>
            {
                Results = JsonConvert.DeserializeObject<MatchResults>(Wire.ReadJson(r));
                if (!headless && Results != null && Info != null && Info.YourIndex >= 0)
                {
                    string me = Info.Roster.Find(x => x.Index == Info.YourIndex)?.EntrantId;
                    ResultEntrant mine = Results.Entrants.Find(x => x.EntrantId == me);
                    GameAudio.RaceMusicPlayer.Results(mine != null && mine.Outcome == "Finished" && mine.Placement == 1);
                }
            });
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgDrift, (id, r) =>
            {
                r.ReadValueSafe(out driftBanked);
                r.ReadValueSafe(out driftUnbanked);
                r.ReadValueSafe(out driftLost);
                r.ReadValueSafe(out driftChain);
                driftSeen = true;
            });
            nm.NetworkTickSystem.Tick += OnTick;
            if (!Autopilot)
            {
                controls = new DrivingControls();
                controls.Enable();
            }
        }

        IEnumerator OnMatch(string json)
        {
            Info = JsonConvert.DeserializeObject<MatchInfo>(json);
            SendLoaded(0.1f);
            if (CourseRuntime.Active == null || CourseRuntime.Active.Route == null || CourseRuntime.Active.Route.Course != Info.CourseId)
            {
                AsyncOperation load = SceneManager.LoadSceneAsync(Info.CourseId, LoadSceneMode.Single);
                while (!load.isDone)
                {
                    SendLoaded(0.1f + load.progress * 0.7f);
                    yield return null;
                }
                yield return null;
            }
            track = CourseRuntime.Active.Track;
            if (!headless) CourseRuntime.Active.ApplyConditions(Info.TimeOfDay); // the event's lighting (a Hard night, a dawn)
            Physics.SyncTransforms();
            var world = new PhysicsVehicleWorld(Physics.defaultPhysicsScene, GameLayers.DrivableMask, GameLayers.BarrierMask);
            var carSet = Resources.Load<CarMaterialSet>("CarMaterialSet");
            foreach (RosterEntry r in Info.Roster)
            {
                var car = new Car { Roster = r, Params = ParamsFor(lib, r) };
                if (!headless)
                {
                    VehicleParams p = car.Params;
                    car.View = VehicleView.Create($"Car_{r.Index}_{r.CarId}", p, lib.Body(r.CarId), carSet, new Color(r.Paint[0], r.Paint[1], r.Paint[2]),
                        AppearanceMapping.ForWire(lib.Customization, r.CarId, r.Livery));
                    GridSlot g = track.Grid[r.GridSlot];
                    car.View.ShowParked(g.Position - g.Rotation * Vector3.up * 0.6f, g.Rotation);
                    car.View.SetHeadlights(CourseRuntime.Active.Dark);
                    GameAudio.CarAudio.Attach(car.View, p, r.CarId, r.Index == Info.YourIndex);
                }
                cars[r.Index] = car;
            }
            if (Info.YourIndex < 0)
            {
                if (!headless)
                {
                    var spectateCam = CameraRig.EnsureMain("RaceCamera").gameObject;
                    spectateCam.tag = "MainCamera";
                    chase = DrivingCameraFeed.Attach(spectateCam);
                    speedLines = UI.SpeedLines.Create();
                    hud = UI.RaceHud.Create();
                    hud.SetCourse(UI.HudHelpers.Plan(track));
                }
                loaded = true;
                EnsureWatchTarget();
                Debug.Log($"[NightSignal.Client] spectating match {Info.MatchId} ({Info.Roster.Count} entrants)");
                yield break;
            }
            RosterEntry me = Info.Roster[Info.YourIndex];
            ownParams = ParamsFor(lib, me);
            // Predict with the grip the server simulates (a wet event on dry prediction was corrected every snapshot).
            ownSim = new VehicleSimulation(ownParams, world) { SurfaceGripScale = CourseRuntime.SurfaceGrip(Info.Surface) };
            GridSlot slot = track.Grid[me.GridSlot];
            ownState = VehicleState.AtRest(slot.Position, slot.Rotation);
            autopilot = new RouteFollower(track, ownParams, DriverProfile.Validator)
            {
                // Drift formats: the automation drifts the judged zones like a player would have to.
                // Drift formats, and S29's Arc contract (a course with the four contract sectors), drift the judged zones.
                DriftZones = (Info.FreeplayMode == "drift-attack" || track.Gates.Count(g => g.Kind == "contract") == 4) && new DriftJudge(track).Zones.Count > 0
                    ? new DriftJudge(track).Zones : null,
                // S29's Entry contract: aim for the marked apex gates.
                ApexGates = track.Gates.Count(g => g.Kind == "contract") == 4 ? track.Gates.Where(g => g.Kind == "apex").ToList() : null,
                ResetWhenStuck = true,
                SurfaceGrip = CourseRuntime.SurfaceGrip(Info.Surface),
            };
            if (!headless)
            {
                var camGo = CameraRig.EnsureMain("RaceCamera").gameObject;
                camGo.tag = "MainCamera";
                chase = DrivingCameraFeed.Attach(camGo);
                chase.SetTarget(cars[me.Index].View);
                speedLines = UI.SpeedLines.Create();
            }
            if (!headless)
            {
                hud = UI.RaceHud.Create();
                hud.SetCourse(UI.HudHelpers.Plan(track));
            }
            if (GhostSource != null && !headless && Info.YourIndex >= 0 && Info.FreeplayMode == "time-attack")
            {
                System.Threading.Tasks.Task<List<KeyValuePair<string, Core.Ghosts.GhostRecording>>> fetch = GhostSource(Info);
                float until = Time.realtimeSinceStartup + 8f;
                while (!fetch.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
                var rules = new Core.Ghosts.GhostHeader
                {
                    CourseId = Info.CourseId, CourseRevision = CourseRuntime.Active != null ? CourseRuntime.Active.SourceHash ?? "" : "",
                    Format = RaceServer.GhostFormat(Info.Kind, Info.StageId, Info.Mode, Info.FreeplayMode),
                    Surface = string.IsNullOrEmpty(Info.Surface) ? "dry" : Info.Surface,
                    PhysicsVersion = RaceSimulation.PhysicsVersion, ScoringVersion = RaceSimulation.ScoringVersion,
                };
                if (fetch.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    foreach (KeyValuePair<string, Core.Ghosts.GhostRecording> offer in fetch.Result)
                    {
                        Core.Ghosts.GhostRecording g = offer.Value;
                        if (Ghosts.Count >= 3) break;
                        if (!g.CompatibleWith(rules) || g.Count < 2 || !lib.Catalogue.TryCar(g.Header.CarModelId, out Core.Content.CarDef gc)) continue;
                        VehicleView gv = VehicleView.Create($"Ghost_{Ghosts.Count}_{gc.Id}", lib.Params(gc.Id, AssistSettings.Default), lib.Body(gc.Id),
                            Resources.Load<CarMaterialSet>("CarMaterialSet"), GhostTints[Ghosts.Count]);
                        Ghosts.Add(new GhostPlayback(g, gv, $"Ghost · {offer.Key} {g.Header.ResultMicros / 1e6:F3} s"));
                    }
                Debug.Log($"[NightSignal.Ghost] online {Info.CourseId} {rules.Format}: {(fetch.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? fetch.Result.Count : 0)} offered, " +
                          $"{Ghosts.Count} racing{(Ghosts.Count > 0 ? ": " + string.Join(", ", Ghosts.Select(x => x.Label)) : "")}");
            }
            if (Presentation != null && !headless && Info.YourIndex >= 0)
            {
                SendLoaded(0.9f); // loaded, reading: real progress for the server's loading window
                yield return Presentation(Info);
            }
            loaded = true;
            SendLoaded(1f);
        }

        UI.RaceHud hud;
        readonly UI.HudState hudState = new UI.HudState();
        public bool ShowDebugOverlay;

        void RenderHud()
        {
            if (hud == null || Info == null) return;
            var order = new List<Car>(cars.Values);
            order.Sort((a, b) =>
            {
                bool fa = a.FinishMillis > 0, fb = b.FinishMillis > 0;
                if (fa && fb) return a.FinishMillis.CompareTo(b.FinishMillis);
                if (fa != fb) return fa ? -1 : 1;
                return b.RaceDistance.CompareTo(a.RaceDistance);
            });
            hudState.Field.Clear();
            int myPos = 0;
            for (int i = 0; i < order.Count; i++)
            {
                Car c = order[i];
                bool me = c.Roster.Index == (Spectating ? watching : Info.YourIndex);
                if (me) myPos = i + 1;
                Vector3 pos = me ? ownState.Position : c.Buffer.Count > 0 ? c.Buffer[c.Buffer.Count - 1].state.Position : Vector3.zero;
                string status = c.Status == EntrantStatus.Finished ? (c.FinishMillis / 1000f).ToString("F3") + " s"
                    : c.Status == EntrantStatus.DqDisconnected ? "DQ" : c.Status == EntrantStatus.Dnf ? "DNF" : c.Roster.Human ? "" : "AI";
                hudState.Field.Add(new UI.HudEntrant { Name = c.Roster.DisplayName, Status = status, Position = pos, IsYou = me, Distance = c.RaceDistance });
            }
            if (Spectating)
            {
                SpectateHud(myPos);
                return;
            }
            Car mine = cars[Info.YourIndex];
            hudState.RoadSpeedMps = ownSim.Telemetry.RoadSpeedMps;
            hudState.EnvelopeMps = UI.SpeedDisplay.EnvelopeMps(ownParams);
            hudState.Gear = ownState.Gear;
            hudState.Rpm = ownState.EngineRpm;
            hudState.Redline = ownParams.RedlineRpm;
            int serverTick = nm.ServerTime.Tick;
            hudState.RaceSeconds = mine.FinishMillis > 0 ? mine.FinishMillis / 1000.0 : serverTick >= startTick ? (serverTick - startTick) / 60.0 : 0;
            hudState.Position = myPos;
            hudState.Entrants = cars.Count;
            // Ghost checkpoint deltas, as the server reports each checkpoint (display only; the server settles the time).
            if (Ghosts.Count > 0)
            {
                while (ghostCheckpoints < mine.CheckpointsPassed)
                {
                    long at = mine.FinishMillis > 0 && ghostCheckpoints == mine.CheckpointsPassed - 1 ? mine.FinishMillis * 1000L
                        : (long)(mine.LatestTick - startTick) * 1_000_000L / VehicleSimulation.TickRate;
                    long? d = Ghosts[0].Recording.SectorDeltaMicros(ghostCheckpoints, at);
                    ghostCheckpoints++;
                    if (d == null) continue;
                    GhostDeltasMicros.Add(d.Value);
                    ghostDelta = $"CHECKPOINT {ghostCheckpoints}  {(d.Value <= 0 ? "−" : "+")}{System.Math.Abs(d.Value) / 1e6:F2} s  vs {Ghosts[0].Label}";
                    ghostDeltaUntil = Time.unscaledTime + 3f;
                }
                float gt = startTick != int.MaxValue ? Mathf.Max(0f, ((float)nm.LocalTime.TickWithPartial - startTick) / VehicleSimulation.TickRate) : 0f;
                foreach (GhostPlayback g in Ghosts)
                    hudState.Field.Add(new UI.HudEntrant { Name = g.Label, Position = g.At(gt).Position, IsReplay = true });
            }
            hudState.GhostDelta = Time.unscaledTime < ghostDeltaUntil ? ghostDelta : "";
            // The interval to the car directly ahead, as of the newest snapshot (the server judges the same interval).
            Car ahead = null;
            foreach (Car c in cars.Values)
                if (c != mine && c.Status == EntrantStatus.Racing && c.RaceDistance > mine.RaceDistance && (ahead == null || c.RaceDistance < ahead.RaceDistance)) ahead = c;
            hudState.GapAheadSeconds = ahead != null && mine.Status == EntrantStatus.Racing &&
                                       ahead.History.IntervalBehind(mine.RaceDistance, (mine.LatestTick - startTick) / 60.0, out float gapAhead) ? gapAhead : -1f;
            hudState.GapAheadName = ahead?.Roster.DisplayName ?? "";
            hudState.Checkpoints = mine.CheckpointsPassed;
            hudState.TotalCheckpoints = track.CheckpointMetres.Length * track.Laps;
            long raceMicros = NetBootstrap.RaceMicros(serverTick, startTick);
            hudState.FinishWindowSeconds = deadlineMicros > 0 && Phase == MatchPhase.Racing ? Mathf.Max(0f, (deadlineMicros - raceMicros) / 1e6f) : -1f;
            hudState.RttMs = Rtt();
            if (chase != null)
            {
                // A server-applied recovery moves the car far in one step: a discontinuity, so the camera cuts.
                if ((ownState.Position - lastCameraCarPos).sqrMagnitude > 64f)
                {
                    chase.NotifyTeleport();
                    hud?.NotifyDiscontinuity();
                    if (Phase == MatchPhase.Racing) recoveryNoticeUntil = Time.unscaledTime + 2.5f;
                }
                lastCameraCarPos = ownState.Position;
                DrivingCameraFeed.Feed(chase, speedLines, ownSim.Telemetry, ownState, ownParams, Time.deltaTime);
            }
            if (driftSeen) driftFeed.Update(hudState, driftBanked, driftUnbanked, driftLost, driftChain, Time.unscaledTime);
            OnlineRecoveryHud(mine);
            string countdown = Phase == MatchPhase.Countdown || Phase == MatchPhase.Racing ? UI.HudHelpers.Countdown((startTick - serverTick) / 60f) : "";
            hudState.Banner = Phase == MatchPhase.Loading ? "LOADING — WAITING FOR ALL DRIVERS"
                : Results != null ? "RESULTS" + ResultLine()
                : mine.Status == EntrantStatus.Finished ? "FINISH" : countdown;
            hud.Render(hudState);
        }

        /// <summary>
        /// Online recovery display: the server decides every recovery; the client shows the hold progress of its own
        /// button (released before another can start), an overturned countdown from its predicted car, and a brief notice
        /// when the server moves the car. Off-route countdowns are not shown online (the server still rescues at 2.5 s).
        /// </summary>
        void OnlineRecoveryHud(Car mine)
        {
            bool racing = Phase == MatchPhase.Racing && mine.Status == EntrantStatus.Racing;
            bool held = racing && controls != null && controls.ResetHeld;
            if (!held) resetHoldSpent = false;
            resetHoldShown = held && !resetHoldSpent ? resetHoldShown + Time.deltaTime : 0f;
            if (resetHoldShown >= RaceSimulation.ResetHoldSeconds) { resetHoldSpent = true; resetHoldShown = 0f; }
            overturnedShown = racing && RaceSimulation.IsOverturned(ownState) ? overturnedShown + Time.deltaTime : 0f;
            var rs = new RecoveryStatus { HoldFraction = resetHoldShown / RaceSimulation.ResetHoldSeconds, SecondsToAuto = -1f };
            float since = Time.unscaledTime - serverRecoveryAt;
            if (racing && since < 0.6f && serverRecoveryKind != RecoveryKind.None)
            {
                // The server's own judgement (off route, overturned, stopped), counted down locally between its messages.
                rs.Kind = serverRecoveryKind;
                rs.SecondsToAuto = serverRecoverySeconds < 0f ? -1f : Mathf.Max(0f, serverRecoverySeconds - since);
            }
            else if (overturnedShown >= RaceSimulation.OverturnedPromptSeconds)
            {
                rs.Kind = RecoveryKind.Overturned;
                rs.SecondsToAuto = Mathf.Max(0f, RaceSimulation.OverturnedRescueSeconds - overturnedShown);
            }
            UI.RaceHud.SetRecovery(hudState, rs, controls != null ? controls.BindingLabel("Reset") : "R", true);
            string offer = rs.Kind == RecoveryKind.None ? "" : rs.Kind.ToString();
            if (offer != lastOfferLogged) { lastOfferLogged = offer; if (offer.Length > 0) Debug.Log($"[NightSignal.Client] recovery offer shown: {offer} ({rs.SecondsToAuto:F1} s to the marshal)"); }
            hudState.RecoveryNotice = Time.unscaledTime < recoveryNoticeUntil
                ? (recoveryNotice.Length > 0 ? recoveryNotice : "RECOVERED  <size=80%><color=#9A968D>+3.000 s · clock running</color></size>") : "";
        }

        // By the entrant's state only (a headless spectator has no car views but follows the same targets).
        bool Watchable(Car c) => c != null &&
            (c.Status == EntrantStatus.Racing || c.Status == EntrantStatus.Finished || c.Status == EntrantStatus.Loaded || c.Status == EntrantStatus.Loading);

        /// <summary>Next (+1) or previous (−1) entrant still in the event; nobody → the empty state.</summary>
        public void SpectateNext(int dir)
        {
            var order = cars.Keys.OrderBy(k => k).ToList();
            if (order.Count == 0) return;
            int start = watching < 0 ? (dir > 0 ? -1 : order.Count) : order.IndexOf(watching);
            for (int step = 1; step <= order.Count; step++)
            {
                int idx = order[((start + dir * step) % order.Count + order.Count) % order.Count];
                if (Watchable(cars[idx])) { Watch(idx, dir > 0 ? "next" : "previous"); return; }
            }
            Watch(-1, "nobody left");
        }

        /// <summary>
        /// Evidence only: send input packets from this connection although it is not racing (a spectator), full throttle and a
        /// held reset, to show the server ignores them.
        /// </summary>
        public void ForgeInputsForTest(int packets)
        {
            if (nm == null || !nm.IsConnectedClient) return;
            for (int k = 0; k < packets; k++)
                using (var w = new FastBufferWriter(64, Allocator.Temp))
                {
                    w.WriteValueSafe(nm.LocalTime.Tick + 2 + k);
                    w.WriteValueSafe((byte)1);
                    Wire.Write(w, DriverInput.Quantize(1f, 1f, 0f, InputButtons.ResetHeld));
                    nm.CustomMessagingManager.SendNamedMessage(Wire.MsgInput, NetworkManager.ServerClientId, w, NetworkDelivery.UnreliableSequenced);
                }
        }

        /// <summary>Automation and UI: watch this entrant if it is still in the event.</summary>
        public void SpectateIndex(int index)
        {
            if (cars.TryGetValue(index, out Car c) && Watchable(c)) Watch(index, "chosen");
        }

        /// <summary>Keeps a valid target: the watched car leaving is a target loss; humans are preferred.</summary>
        void EnsureWatchTarget()
        {
            if (watching >= 0 && cars.TryGetValue(watching, out Car current) && Watchable(current)) return;
            bool lost = watching >= 0;
            if (lost) SpectateTargetLosses++;
            int pick = cars.Values.Where(Watchable).OrderBy(c => c.Roster.Human ? 0 : 1).ThenBy(c => c.Roster.Index).Select(c => c.Roster.Index).DefaultIfEmpty(-1).First();
            if (pick != watching || lost) Watch(pick, lost ? "target left the event" : "first");
        }

        void Watch(int index, string why)
        {
            if (index == watching && why != "target left the event") return;
            watching = index;
            SpectateSwitches++;
            string name = index >= 0 ? cars[index].Roster.DisplayName : "nobody";
            SpectateLog.Add($"{Time.unscaledTime:F1}s {why}: {name}");
            Debug.Log($"[NightSignal.Client] spectating {name} ({why})");
            // A new target is a discontinuity: the camera cuts and forgets the old car; the instrument snaps.
            if (chase != null) chase.SetTarget(index >= 0 ? cars[index].View : null);
            hud?.NotifyDiscontinuity();
        }

        void SpectateHud(int watchedPos)
        {
            Car w = watching >= 0 && cars.TryGetValue(watching, out Car wc) ? wc : null;
            VehicleState ws = w != null ? w.Latest : default;
            int serverTick = nm.ServerTime.Tick;
            hudState.SpeedAvailable = w != null;
            hudState.RoadSpeedMps = w != null ? ws.Velocity.magnitude : 0f;
            hudState.EnvelopeMps = w != null ? UI.SpeedDisplay.EnvelopeMps(w.Params) : 70f;
            hudState.Gear = w != null ? ws.Gear : 0;
            hudState.Rpm = w != null ? ws.EngineRpm : 0f;
            hudState.Redline = w != null ? w.Params.RedlineRpm : 7000f;
            hudState.RaceSeconds = w != null && w.FinishMillis > 0 ? w.FinishMillis / 1000.0 : serverTick >= startTick ? (serverTick - startTick) / 60.0 : 0;
            hudState.Position = w != null ? watchedPos : 0;
            hudState.Entrants = cars.Count;
            hudState.Checkpoints = w != null ? w.CheckpointsPassed : 0;
            hudState.TotalCheckpoints = track.CheckpointMetres.Length * track.Laps;
            hudState.RttMs = Rtt();
            hudState.RecoveryPrompt = "";
            hudState.RecoveryNotice = "";
            hudState.ResetHoldFraction = 0f;
            hudState.Banner = Results != null ? "RESULTS" + ResultLine()
                : w == null ? "NO DRIVERS TO WATCH\n<size=40%>everyone has left this event — return to the menus</size>"
                : $"SPECTATING {w.Roster.DisplayName}\n<size=40%>{(controls != null ? controls.BindingLabel("ShiftUp") + " / " + controls.BindingLabel("ShiftDown") : "E / Q")}: next / previous driver</size>";
            if (w != null && chase != null)
                DrivingCameraFeed.Feed(chase, speedLines, new StepTelemetry { RoadSpeedMps = ws.Velocity.magnitude }, ws, w.Params, Time.deltaTime);
            hud.Render(hudState);
        }

        string ResultLine()
        {
            if (Info == null || Info.YourIndex < 0) return "\n<size=40%>SPECTATED — NOT AN ENTRANT IN THIS EVENT</size>";
            ResultEntrant me = Results.Entrants.Find(e => e.EntrantId == Info.Roster[Info.YourIndex].EntrantId);
            return me == null ? "" : $"\n<size=40%>PLACE {me.Placement}  ·  {me.Outcome.ToUpperInvariant()}  ·  RECEIPT PENDING FROM SERVER</size>";
        }

        void SendLoaded(float progress)
        {
            if (nm == null || !nm.IsConnectedClient) return;
            using (var w = new FastBufferWriter(8, Allocator.Temp))
            {
                w.WriteValueSafe(progress);
                nm.CustomMessagingManager.SendNamedMessage(Wire.MsgLoaded, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
            }
        }

        void OnPhase(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out byte p);
            r.ReadValueSafe(out int start);
            r.ReadValueSafe(out long deadline);
            if ((MatchPhase)p != Phase) Debug.Log($"[NightSignal.Client] phase {(MatchPhase)p} (start tick {start}, local tick {(nm != null ? nm.LocalTime.Tick : -1)})");
            if ((MatchPhase)p == MatchPhase.Countdown && Phase != MatchPhase.Countdown && !headless && Info != null)
                GameAudio.RaceMusicPlayer.Start(ContentLibrary.Load()?.Catalogue, Info.CourseId, Info.Kind, Info.StageId, Info.Mode == "hard",
                    null, Info.FreeplayMode == "drift-attack");
            Phase = (MatchPhase)p;
            startTick = start;
            deadlineMicros = deadline;
        }

        void OnSnapshotArrived(ulong sender, FastBufferReader r)
        {
            if (!impair) { OnSnapshot(sender, r); return; }
            var data = new byte[r.Length - r.Position];
            r.ReadBytesSafe(ref data, data.Length);
            if (impairRng.NextDouble() * 100.0 < impairDropPercent) { ImpairedSnapshotDrops++; return; }
            heldSnapshots.Add(new Delayed { At = Time.unscaledTime + ImpairDelay(), Data = data, Tick = System.BitConverter.ToInt32(data, 0) });
        }

        float ImpairDelay() => Mathf.Max(0f, impairDelayMs + (float)(impairRng.NextDouble() * 2.0 - 1.0) * impairJitterMs) / 1000f;

        /// <summary>Releases held snapshots and inputs whose time has come (impairment runs only).</summary>
        void ReleaseImpaired()
        {
            float now = Time.unscaledTime;
            heldSnapshots.Sort((a, b) => a.At.CompareTo(b.At));
            while (heldSnapshots.Count > 0 && heldSnapshots[0].At <= now)
            {
                Delayed d = heldSnapshots[0];
                heldSnapshots.RemoveAt(0);
                if (d.Tick <= lastReleasedSnapshotTick) continue; // overtaken by a newer one: a sequenced channel drops it
                lastReleasedSnapshotTick = d.Tick;
                using (var nr = new FastBufferReader(d.Data, Allocator.Temp)) OnSnapshot(NetworkManager.ServerClientId, nr);
            }
            heldInputs.Sort((a, b) => a.At.CompareTo(b.At));
            while (heldInputs.Count > 0 && heldInputs[0].At <= now)
            {
                Delayed d = heldInputs[0];
                heldInputs.RemoveAt(0);
                using (var w = new FastBufferWriter(d.Data.Length, Allocator.Temp))
                {
                    w.WriteBytesSafe(d.Data);
                    nm.CustomMessagingManager.SendNamedMessage(Wire.MsgInput, NetworkManager.ServerClientId, w, NetworkDelivery.UnreliableSequenced);
                }
            }
        }

        void OnSnapshot(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out int tick);
            r.ReadValueSafe(out byte phase);
            r.ReadValueSafe(out byte count);
            SnapshotsReceived++;
            bool haveOwn = false;
            VehicleState own = default;
            for (int i = 0; i < count; i++)
            {
                r.ReadValueSafe(out byte index);
                r.ReadValueSafe(out byte status);
                r.ReadValueSafe(out ushort cps);
                r.ReadValueSafe(out float dist);
                r.ReadValueSafe(out int finishMs);
                r.ReadValueSafe(out int ackTick);
                r.ReadValueSafe(out byte flags);
                VehicleState s = (flags & 2) != 0 ? Wire.ReadState(r) : Wire.ReadCompact(r); // bit1: full state (own car)
                bool mine = Info != null && index == Info.YourIndex;
                if (mine) NoteAck(tick, ackTick);
                if (!cars.TryGetValue(index, out Car car)) continue;
                car.Status = (EntrantStatus)status;
                car.CheckpointsPassed = cps;
                if (tick > car.LatestTick && startTick > 0 && tick >= startTick)
                {
                    if (dist < car.RaceDistance - 1f) car.History.Clear();
                    car.History.Add((tick - startTick) / 60.0, dist);
                }
                car.RaceDistance = dist;
                car.FinishMillis = finishMs;
                car.Ghost = (flags & 1) != 0;
                car.LatestTick = tick;
                car.Latest = s;
                if (mine)
                {
                    haveOwn = true;
                    own = s;
                }
                else
                {
                    car.Buffer.Add((tick, s));
                    if (car.Buffer.Count > 32) car.Buffer.RemoveAt(0);
                }
            }
            // Reconcile after every remote car is updated, so the replay predicts contacts against this same tick.
            if (haveOwn) Reconcile(tick, own);
        }

        bool ContactOn => Info != null && Info.Contact != "non-contact";

        /// <summary>
        /// Predicts light contact for our own car against remote cars extrapolated from their latest authoritative
        /// state (≤ 15 ticks). Only our car is changed; the server resolves the real pair and reconciliation corrects
        /// any difference.
        /// </summary>
        void PredictContacts(ref VehicleState state, int tick)
        {
            if (!ContactOn || Info == null || !cars.TryGetValue(Info.YourIndex, out Car me) || me.Ghost) return;
            foreach (Car c in cars.Values)
            {
                if (c == me || c.Ghost || c.Params == null || c.LatestTick < 0) continue;
                if (c.Status != EntrantStatus.Racing && c.Status != EntrantStatus.Finished) continue;
                VehicleState other = Extrapolate(c.Latest, c.LatestTick, tick);
                Vector3 from = state.Position;
                if (VehicleContact.Resolve(ref state, ownParams, ref other, c.Params, true, false).Touching)
                    ownSim.ConstrainToBarriers(ref state, from);
            }
        }

        static VehicleState Extrapolate(VehicleState s, int fromTick, int toTick)
        {
            float dt = Mathf.Clamp(toTick - fromTick, 0, 15) * VehicleSimulation.TickDt;
            s.Position += s.Velocity * dt;
            s.Rotation = Quaternion.AngleAxis(s.AngularVelocity.y * dt * Mathf.Rad2Deg, Vector3.up) * s.Rotation;
            return s;
        }

        /// <summary>Compare the authoritative state at tick T with our prediction; on divergence rewind and replay.</summary>
        void Reconcile(int tick, VehicleState server)
        {
            if (!loaded || lastPredictedTick < 0) return;
            int slot = tick & 255;
            bool havePrediction = ticks[slot] == tick;
            float error = havePrediction ? Vector3.Distance(states[slot].Position, server.Position) : float.MaxValue;
            float velError = havePrediction ? Vector3.Distance(states[slot].Velocity, server.Velocity) : float.MaxValue;
            if (error < 0.03f && velError < 0.2f) return;

            if (!Finite(server)) NoteNonFinite("server snapshot", $"tick {tick}");
            Vector3 before = ownState.Position, beforeVelocity = ownState.Velocity;
            VehicleState replay = server;
            for (int t = tick + 1; t <= lastPredictedTick; t++)
            {
                int s = t & 255;
                if (ticks[s] != t) break;
                if (t >= startTick)
                {
                    ownSim.Step(ref replay, inputs[s]);
                    PredictContacts(ref replay, t);
                }
                states[s] = replay;
            }
            ownState = replay;
            float jump = Vector3.Distance(before, ownState.Position);
            visualOffset += before - ownState.Position; // hide corrections by blending the drawn car back onto the truth
            // Keep the drawn car's velocity continuous too: without this the corrected prediction's new velocity showed at
            // once as a kink in the chase camera (Addendum 03 C11, a heavy-contact client under ~190 ms RTT). Only small
            // differences are absorbed — a real hit should still be felt.
            if (VelocityBlend) visualOffsetVelocity += Vector3.ClampMagnitude(beforeVelocity - ownState.Velocity, 3f);
            // Beyond 8 m it is a discontinuity (a server recovery, a rejoin): snap, and the camera cuts with it.
            if (visualOffset.magnitude > 8f) { visualOffset = Vector3.zero; visualOffsetVelocity = Vector3.zero; }
            Corrections++;
            MaxCorrectionMetres = Mathf.Max(MaxCorrectionMetres, jump);
        }

        void OnTick()
        {
            if (!loaded || Spectating || Phase < MatchPhase.Countdown || Phase >= MatchPhase.Results) return;
            // Predict (and send commands for) a small fixed lead past the network clock, so each command reaches the
            // server before its tick is simulated. Measured without a lead: ~1-3% of ticks starved on loopback.
            int tick = nm.LocalTime.Tick + InputLeadTicks;
            if (tick <= lastPredictedTick) return;
            // The network clock can advance several ticks in one frame (time-sync corrections, frame hitches).
            // Predict every tick so the client and server step the car the same number of times.
            int from = lastPredictedTick < 0 ? tick : Mathf.Max(lastPredictedTick + 1, tick - 30);
            TicksFilled += tick - from;
            for (int t = from; t <= tick; t++) PredictTick(t);
            if (tick - lastSentTick >= 1) SendInputs(tick); // every tick; each packet repeats the last 8 commands
            if (headless && tick > startTick && (tick - startTick) % (60 * 15) < tick - from + 1)
                Debug.Log($"[NightSignal.Client] t={(tick - startTick) / 60f:F0}s {OwnStatus} pos {ownState.Position.x:F0},{ownState.Position.y:F0},{ownState.Position.z:F0} " +
                          $"{ownState.Velocity.magnitude * 3.6f:F0}km/h sent {InputsSent} ack {(lastAckTick < 0 ? "-" : (lastAckTick - tick).ToString())} corrections {Corrections}");
        }

        void PredictTick(int tick)
        {
            bool racing = tick >= startTick && OwnStatus != EntrantStatus.Finished && OwnStatus != EntrantStatus.DqDisconnected;
            DriverInput input = !racing ? DriverInput.Neutral : Autopilot ? autopilot.Drive(ownState) : SampleHuman();
            if (racing && autoResetAt >= 0f && tick >= startTick + autoResetAt * 60f && tick < startTick + (autoResetAt + 1.2f) * 60f)
                input.Buttons |= InputButtons.ResetHeld; // automation: a held reset request, judged by the server like any other
            int slot = tick & 255;
            inputs[slot] = input;
            ticks[slot] = tick;
            if (racing)
            {
                VehicleState before = ownState;
                ownSim.Step(ref ownState, input);
                if (!Finite(ownState)) NoteNonFinite("prediction step", $"tick {tick}, steer {input.Steer}, throttle {input.Throttle}, from finite {Finite(before)}");
                VehicleState stepped = ownState;
                PredictContacts(ref ownState, tick);
                if (Finite(stepped) && !Finite(ownState)) NoteNonFinite("contact prediction", $"tick {tick}");
            }
            states[slot] = ownState;
            lastPredictedTick = tick;
        }

        void NoteAck(int snapshotTick, int ackTick)
        {
            if (ackTick < 0) return;
            if (snapshotTick >= startTick && OwnStatus == EntrantStatus.Racing) MinInputLeadTicks = Mathf.Min(MinInputLeadTicks, ackTick - snapshotTick); // own racing ticks only
            if (ackTick <= lastAckTick) return;
            lastAckTick = ackTick;
            int slot = ackTick & 255;
            if (ticks[slot] != ackTick || sendTimes[slot] <= 0) return;
            float ms = (float)((Time.realtimeSinceStartupAsDouble - sendTimes[slot]) * 1000.0);
            ackSumMs += ms;
            ackSamples++;
            InputAckMsMax = Mathf.Max(InputAckMsMax, ms);
            rttSmoothedMs = rttSmoothedMs < 0 ? ms : Mathf.Lerp(rttSmoothedMs, ms, 0.1f);
        }

        DriverInput SampleHuman()
        {
            DriverInput i = controls.Sample(latchUp, latchDown);
            latchUp = latchDown = false;
            return i;
        }

        void SendInputs(int latestTick)
        {
            using (var w = new FastBufferWriter(64, Allocator.Temp))
            {
                w.WriteValueSafe(latestTick);
                int count = 0;
                for (int t = latestTick - Wire.InputRedundancy + 1; t <= latestTick; t++) if (ticks[t & 255] == t) count++;
                w.WriteValueSafe((byte)count);
                for (int t = latestTick - Wire.InputRedundancy + 1; t <= latestTick; t++)
                    if (ticks[t & 255] == t) Wire.Write(w, inputs[t & 255]);
                if (impair)
                {
                    if (impairRng.NextDouble() * 100.0 < impairDropPercent) ImpairedInputDrops++;
                    else heldInputs.Add(new Delayed { At = Time.unscaledTime + ImpairDelay(), Data = w.ToArray() });
                }
                else nm.CustomMessagingManager.SendNamedMessage(Wire.MsgInput, NetworkManager.ServerClientId, w, NetworkDelivery.UnreliableSequenced);
            }
            sendTimes[latestTick & 255] = Time.realtimeSinceStartupAsDouble;
            lastSentTick = latestTick;
            InputsSent++;
        }

        void Update()
        {
            if (impair && nm != null) ReleaseImpaired();
            if (controls != null)
            {
                if (controls.ShiftUpPressedThisFrame) latchUp = true;
                if (controls.ShiftDownPressedThisFrame) latchDown = true;
                if (chase != null && controls.CameraPressed) chase.Cycle();
                if (chase != null) chase.LookBack = controls.LookBackHeld;
                if (Spectating && controls.ShiftUpPressedThisFrame) SpectateNext(+1);
                if (Spectating && controls.ShiftDownPressedThisFrame) SpectateNext(-1);
            }
            if (loaded && Spectating) EnsureWatchTarget(); // headless spectators keep their target too
            if (!loaded || headless) return;
            // After the connection closes (left, lost, the server finished and shut down) the network clock is gone: its
            // partial tick reads NaN, which drew NaN wheel transforms for the last second on screen. Hold the last frame.
            if (nm == null || !nm.IsListening || nm.ShutdownInProgress) return;
            // Critically damped: the drawn car eases onto the corrected state without a velocity jump, so the chase camera
            // shows no kick when a correction lands (Addendum 03 C11 measured the old exponential decay as hitches).
            visualOffset = Vector3.SmoothDamp(visualOffset, Vector3.zero, ref visualOffsetVelocity, 0.2f, Mathf.Infinity, Time.deltaTime);
            float renderTick = (float)nm.ServerTime.TickWithPartial - InterpolationTicks;
            foreach (Car car in cars.Values)
            {
                if (car.View == null) continue;
                if (car.Roster.Index == Info.YourIndex)
                {
                    // Interpolate between the last two predicted ticks by the network clock's fraction, as offline play
                    // does: rendering the newest tick alone steps the car at any frame rate other than 60 (Addendum 03
                    // C11 measured it as chase-camera shake at 30 and 120 fps).
                    VehicleState cur = ownState, prev = ownState;
                    int prevSlot = (lastPredictedTick - 1) & 255;
                    if (lastPredictedTick > 0 && ticks[prevSlot] == lastPredictedTick - 1) prev = states[prevSlot];
                    double localTick = nm.LocalTime.TickWithPartial;
                    float alpha = Mathf.Clamp01((float)(localTick + InputLeadTicks - lastPredictedTick));
                    if (!Finite(alpha)) { NoteNonFinite("render alpha", $"local tick {localTick}, last predicted {lastPredictedTick}"); alpha = 1f; }
                    if (!Finite(visualOffset.x) || !Finite(visualOffset.y) || !Finite(visualOffset.z))
                    {
                        NoteNonFinite("correction blend", $"offset {visualOffset}, velocity {visualOffsetVelocity}");
                        visualOffset = visualOffsetVelocity = Vector3.zero;
                    }
                    if (!Finite(prev)) { NoteNonFinite("previous predicted tick", $"tick {lastPredictedTick - 1}"); prev = cur; }
                    if (!Finite(cur)) { NoteNonFinite("latest predicted tick", $"tick {lastPredictedTick}"); continue; }
                    prev.Position += visualOffset;
                    cur.Position += visualOffset;
                    car.View.Render(prev, cur, alpha, ownSim.Telemetry, Time.deltaTime);
                    continue;
                }
                RenderRemote(car, renderTick);
            }
            if (Ghosts.Count > 0 && startTick != int.MaxValue)
            {
                float gt = ((float)nm.LocalTime.TickWithPartial + InputLeadTicks - startTick) / VehicleSimulation.TickRate;
                foreach (GhostPlayback g in Ghosts) g.Show(gt, Time.deltaTime);
            }
            RenderHud();
        }

        void RenderRemote(Car car, float renderTick)
        {
            List<(int tick, VehicleState state)> b = car.Buffer;
            if (b.Count == 0) return;
            for (int i = b.Count - 1; i > 0; i--)
            {
                if (b[i - 1].tick <= renderTick && renderTick <= b[i].tick)
                {
                    float a = Mathf.InverseLerp(b[i - 1].tick, b[i].tick, renderTick);
                    car.View.Render(b[i - 1].state, b[i].state, a, default, Time.deltaTime);
                    return;
                }
            }
            // Past the newest snapshot: extrapolate briefly, then hold (degraded connection).
            (int tick, VehicleState state) last = b[b.Count - 1];
            float ahead = Mathf.Min(renderTick - last.tick, MaxExtrapolationTicks) / 60f;
            VehicleState e = last.state;
            if (ahead > 0f) e.Position += e.Velocity * ahead;
            car.View.Render(e, e, 0f, default, Time.deltaTime);
        }

        void OnGUI()
        {
            if (headless || Info == null || !ShowDebugOverlay) return;
            string phaseText = Phase == MatchPhase.Countdown && nm != null
                ? $"START IN {Mathf.CeilToInt((startTick - nm.ServerTime.Tick) / 60f)}"
                : Phase.ToString();
            GUI.Label(new Rect(12, 12, 700, 24), $"{Info.CourseId}  {phaseText}  {ownState.SpeedKmh:F0} km/h  gear {ownState.Gear}  rtt {Rtt()} ms  corrections {Corrections}");
            int y = 34;
            foreach (Car c in cars.Values)
            {
                GUI.Label(new Rect(12, y, 700, 22), $"#{c.Roster.Index} {c.Roster.DisplayName} ({c.Roster.CarId}) {c.Status} cp {c.CheckpointsPassed} {(c.FinishMillis > 0 ? (c.FinishMillis / 1000f).ToString("F3") + " s" : "")}");
                y += 20;
            }
        }

        /// <summary>
        /// Round trip in ms. Prefers the game's input-acknowledgement measurement: the transport's value comes from the
        /// reliable pipeline and goes stale once setup traffic stops (it read ~500 ms on loopback in the first run).
        /// </summary>
        public int Rtt()
        {
            if (rttSmoothedMs >= 0) return Mathf.RoundToInt(rttSmoothedMs);
            if (Spectating) return -1; // a spectator sends no inputs to time, and the transport's figure goes stale (read 2092 ms on loopback)
            return nm != null && nm.IsConnectedClient ? (int)nm.NetworkConfig.NetworkTransport.GetCurrentRtt(NetworkManager.ServerClientId) : -1;
        }

        /// <summary>The player's game leaves the race (quit, crash, lost link): the server disqualifies the entry.</summary>
        public void Leave(string reason)
        {
            Disconnect();
            if (DisconnectReason == null) DisconnectReason = reason;
        }

        public void Disconnect()
        {
            if (nm != null && nm.IsListening)
            {
                nm.NetworkTickSystem.Tick -= OnTick;
                nm.Shutdown();
            }
            controls?.Dispose();
            controls = null;
            if (speedLines != null) Destroy(speedLines.gameObject);
        }

        /// <summary>Interactive clients return to the menus after a race: leave nothing connected or on screen.</summary>
        /// <summary>
        /// An entrant's vehicle as the server races it: the frozen build from the roster resolved with Core (verified against
        /// its BuildHash), or the stock model. A mismatch is logged — the server stays authoritative, prediction may drift.
        /// </summary>
        static VehicleParams ParamsFor(ContentLibrary lib, RosterEntry r)
        {
            if (r.Build == null || lib.Parts == null) return lib.Params(r.CarId, AssistSettings.Default);
            Core.Builds.ResolveResult res = Core.Builds.BuildResolver.Resolve(lib.Catalogue.Car(r.CarId), lib.Catalogue.CarTunings[r.CarId], lib.Parts, r.Build);
            if (!res.Ok)
            {
                Debug.LogWarning($"[NightSignal.Client] entrant {r.Index}'s build does not resolve here ({string.Join("; ", res.Issues)}); predicting the stock car");
                return lib.Params(r.CarId, AssistSettings.Default);
            }
            if (!string.IsNullOrEmpty(r.BuildHash) && res.Spec.BuildHash != r.BuildHash)
                Debug.LogWarning($"[NightSignal.Client] entrant {r.Index}'s build resolves to a different hash here; prediction may drift");
            return VehicleFactory.Build(res.Spec, AssistSettings.Default, lib.Body(r.CarId).WheelRadius);
        }

        void OnDestroy()
        {
            Disconnect();
            if (hud != null) Destroy(hud.gameObject);
        }
    }
}
