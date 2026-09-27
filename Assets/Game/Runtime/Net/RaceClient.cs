using System.Collections;
using System.Collections.Generic;
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
        }

        public bool Autopilot;
        public MatchInfo Info { get; private set; }
        long driftBanked, driftUnbanked, driftLost;
        float driftChain = 1f;
        bool driftSeen;
        readonly UI.DriftHudFeed driftFeed = new UI.DriftHudFeed();
        /// <summary>This driver's banked raw drift score as the server last reported it (drift formats; 0 otherwise).</summary>
        public long DriftBanked => driftBanked;
        /// <summary>This client's own car as drawn (null before the views exist or headless).</summary>
        public VehicleView MyView => Info != null && cars.TryGetValue(Info.YourIndex, out Car c) ? c.View : null;
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
        Vector3 visualOffset;
        RouteFollower autopilot;
        DrivingControls controls;
        DrivingCamera chase;
        UI.SpeedLines speedLines;
        Vector3 lastCameraCarPos;
        float resetHoldShown, overturnedShown, recoveryNoticeUntil;
        bool resetHoldSpent;
        bool latchUp, latchDown;
        bool headless;

        public void Connect(string host, ushort port, string ticket)
        {
            headless = Application.isBatchMode;
            lib = ContentLibrary.Load();
            for (int i = 0; i < ticks.Length; i++) ticks[i] = -1;
            nm = NetBootstrap.Ensure();
            NetBootstrap.Transport(nm).SetConnectionData(host, port);
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
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgSnapshot, OnSnapshot);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgResults, (id, r) => Results = JsonConvert.DeserializeObject<MatchResults>(Wire.ReadJson(r)));
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
                    car.View.SetHeadlights(CourseRuntime.Active.DefaultTimeOfDay == "night");
                }
                cars[r.Index] = car;
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
                DriftZones = Info.FreeplayMode == "drift-attack" && new DriftJudge(track).Zones.Count > 0 ? new DriftJudge(track).Zones : null,
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
                bool me = c.Roster.Index == Info.YourIndex;
                if (me) myPos = i + 1;
                Vector3 pos = me ? ownState.Position : c.Buffer.Count > 0 ? c.Buffer[c.Buffer.Count - 1].state.Position : Vector3.zero;
                string status = c.Status == EntrantStatus.Finished ? (c.FinishMillis / 1000f).ToString("F3") + " s"
                    : c.Status == EntrantStatus.DqDisconnected ? "DQ" : c.Status == EntrantStatus.Dnf ? "DNF" : c.Roster.Human ? "" : "AI";
                hudState.Field.Add(new UI.HudEntrant { Name = c.Roster.DisplayName, Status = status, Position = pos, IsYou = me, Distance = c.RaceDistance });
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
            if (overturnedShown >= RaceSimulation.OverturnedPromptSeconds)
            {
                rs.Kind = RecoveryKind.Overturned;
                rs.SecondsToAuto = Mathf.Max(0f, RaceSimulation.OverturnedRescueSeconds - overturnedShown);
            }
            UI.RaceHud.SetRecovery(hudState, rs, controls != null ? controls.BindingLabel("Reset") : "R", true);
            hudState.RecoveryNotice = Time.unscaledTime < recoveryNoticeUntil ? "RECOVERED  <size=80%><color=#9A968D>+3.000 s · clock running</color></size>" : "";
        }

        string ResultLine()
        {
            ResultEntrant me = Results.Entrants.Find(e => Info != null && e.EntrantId == Info.Roster[Info.YourIndex].EntrantId);
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
            Phase = (MatchPhase)p;
            startTick = start;
            deadlineMicros = deadline;
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

            Vector3 before = ownState.Position;
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
            visualOffset += before - ownState.Position; // hide small corrections by blending
            if (visualOffset.magnitude > 3f) visualOffset = Vector3.zero; // large corrections snap (safety)
            Corrections++;
            MaxCorrectionMetres = Mathf.Max(MaxCorrectionMetres, jump);
        }

        void OnTick()
        {
            if (!loaded || Phase < MatchPhase.Countdown || Phase >= MatchPhase.Results) return;
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
            int slot = tick & 255;
            inputs[slot] = input;
            ticks[slot] = tick;
            if (racing)
            {
                ownSim.Step(ref ownState, input);
                PredictContacts(ref ownState, tick);
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
                nm.CustomMessagingManager.SendNamedMessage(Wire.MsgInput, NetworkManager.ServerClientId, w, NetworkDelivery.UnreliableSequenced);
            }
            sendTimes[latestTick & 255] = Time.realtimeSinceStartupAsDouble;
            lastSentTick = latestTick;
            InputsSent++;
        }

        void Update()
        {
            if (controls != null)
            {
                if (controls.ShiftUpPressedThisFrame) latchUp = true;
                if (controls.ShiftDownPressedThisFrame) latchDown = true;
                if (chase != null && controls.CameraPressed) chase.Cycle();
                if (chase != null) chase.LookBack = controls.LookBackHeld;
            }
            if (!loaded || headless) return;
            visualOffset = Vector3.Lerp(visualOffset, Vector3.zero, 1f - Mathf.Exp(-10f * Time.deltaTime));
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
                    float alpha = Mathf.Clamp01((float)(nm.LocalTime.TickWithPartial + InputLeadTicks - lastPredictedTick));
                    prev.Position += visualOffset;
                    cur.Position += visualOffset;
                    car.View.Render(prev, cur, alpha, ownSim.Telemetry, Time.deltaTime);
                    continue;
                }
                RenderRemote(car, renderTick);
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
            return nm != null && nm.IsConnectedClient ? (int)nm.NetworkConfig.NetworkTransport.GetCurrentRtt(NetworkManager.ServerClientId) : -1;
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
