using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NightSignal.AI;
using NightSignal.Art;
using NightSignal.Cameras;
using NightSignal.Content;
using NightSignal.Core.Rules;
using NightSignal.InputBindings;
using NightSignal.Track;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>
    /// Offline race (Addendum 01 §8.2): one local human plus AI opponents/teammates on the active course, running the
    /// same <see cref="RaceSimulation"/> as the dedicated server — same physics, light contact, finish window and
    /// classification — at a fixed 60 Hz with render interpolation. Results belong to the Local progression domain only
    /// and are never uploaded as online state. Also used for automated full-grid contact validation (autopilot + fast
    /// forward).
    /// </summary>
    public sealed class OfflineRaceSession : MonoBehaviour
    {
        public string CarId = "V01";
        /// <summary>The player's frozen applied build (null = stock), resolved by Core before the race.</summary>
        public Core.Builds.ResolvedCarSpec PlayerSpec;
        /// <summary>The player's applied livery (compact wire form; null/"" = the palette colour).</summary>
        public string PlayerLivery;
        public string PlayerName = "You";
        public bool Autopilot;
        /// <summary>Simulation ticks per real tick (tests fast-forward; 1 = real time).</summary>
        public int SimulationSpeed = 1;
        public bool Headless;
        public RaceEventRules Rules = new RaceEventRules();
        public List<string> OpposingAi = new List<string>();
        public List<string> FriendlyAi = new List<string>();
        /// <summary>Tests: the autopilot's drift skill (0 = the validator's tuned point).</summary>
        public float AutopilotDriftSkill;
        /// <summary>The autopilot starts slides with power, never the handbrake (measuring CH25's trial).</summary>
        public bool AutopilotNoHandbrake;
        /// <summary>The margin the autopilot's line keeps from the road's edge (0 = the validator's own; a trial that keeps every tyre paved).</summary>
        public float AutopilotEdgeMargin;
        /// <summary>Automation only: the validator autopilot steers through the course's challenge touch gates (CH03, CH06).</summary>
        public static bool AutopilotAimsChallengeGates;
        /// <summary>
        /// Automation only: the validator autopilot drifts the course's challenge zones as one slide per challenge (first zone
        /// to bank gate) and aims at clip zones' marked lines (CH17, CH19, CH22, CH27 — the zone tour).
        /// </summary>
        public static bool AutopilotDrivesChallengeZones;
        /// <summary>The zone tour's slide knobs (diagnostics may tune them): target slip, slip-rate countersteer and throttle lift.</summary>
        public static float ZoneSlideSlipDeg = 28f, ZoneSlideRateSteer = 0.15f, ZoneSlideRateThrottle = 0.02f, ZoneSlidePathFollow = 0.6f, ZoneSlidePathThrottle,
            ZoneSlideEntrySpeed = 25f, ZoneSlideTransitionGrace = 1f, ZoneSlideClipInset = 0.8f;
        /// <summary>Automation only: the autopilot holds the brakes this long after GO, so the field goes ahead (racecraft tour).</summary>
        public static float AutopilotHoldSeconds;
        /// <summary>Automation only: the autopilot keeps about this interval (s) behind the car ahead; 0 = it races normally.</summary>
        public static float AutopilotFollowSeconds;
        /// <summary>Automation only: with <see cref="AutopilotFollowSeconds"/>, the autopilot stops holding back inside a marked overtake zone (CH34's hairpin exit) and races.</summary>
        public static bool AutopilotAttacksMarkedZones;
        /// <summary>Automation only: the autopilot drives each challenge-tagged lane on its marked line (CH36's outside lane).</summary>
        public static bool AutopilotHoldsMarkedLanes;
        public int CountdownTicks = 60 * 3;

        public RaceSimulation Sim { get; private set; }
        public RaceEntrant Player { get; private set; }
        /// <summary>The player's car as drawn (null headless or before the views exist).</summary>
        public VehicleView PlayerView => Player != null && views.TryGetValue(Player, out VehicleView v) ? v : null;
        /// <summary>Every car as drawn (empty headless).</summary>
        public IReadOnlyCollection<VehicleView> Views => views.Values;
        public int CurrentTick { get; private set; }
        public bool Ready { get; private set; }
        public MatchPhase Phase { get; private set; } = MatchPhase.Loading;
        public List<RaceEntrantResult> Results { get; private set; }
        /// <summary>Measured simulation cost per tick for the whole field (ms): mean and worst.</summary>
        public double TickMsAverage => ticksMeasured > 0 ? tickMsSum / ticksMeasured : 0;
        public double TickMsMax { get; private set; }
        /// <summary>Largest vertical speed any car reached (m/s) — contact must never launch a car.</summary>
        public float MaxVerticalSpeed { get; private set; }
        public bool AnyNonFinite { get; private set; }
        /// <summary>Per car: position (xyz) where its largest vertical speed (w, m/s) occurred — diagnostics for launches.</summary>
        public readonly Dictionary<RaceEntrant, Vector4> MaxVerticalByEntrant = new Dictionary<RaceEntrant, Vector4>();
        /// <summary>Per car: where it first left the legal corridor (diagnostics).</summary>
        public readonly Dictionary<RaceEntrant, string> FirstCorridorExit = new Dictionary<RaceEntrant, string>();
        /// <summary>Per car: the 150 ticks leading up to its first corridor exit (diagnostics).</summary>
        public readonly Dictionary<RaceEntrant, string[]> ExitTraces = new Dictionary<RaceEntrant, string[]>();
        readonly Dictionary<RaceEntrant, Queue<string>> recentTrace = new Dictionary<RaceEntrant, Queue<string>>();

        readonly Dictionary<RaceEntrant, VehicleState> previous = new Dictionary<RaceEntrant, VehicleState>();
        readonly Dictionary<RaceEntrant, VehicleView> views = new Dictionary<RaceEntrant, VehicleView>();
        DrivingControls controls;
        /// <summary>Soak diagnostic only: skip drawing the cars and HUD (the simulation runs on).</summary>
        internal static bool SoakSkipRender;
        /// <summary>Soak diagnostic only: stop reading the local controls.</summary>
        internal void SoakDropControls() { controls?.Dispose(); controls = null; }
        RouteFollower pilot;
        /// <summary>The autopilot driving the player's car (null without autopilot) — diagnostics and tests.</summary>
        public RouteFollower Pilot => Autopilot ? pilot : null;
        DrivingCamera chase;
        UI.SpeedLines speedLines;
        /// <summary>The perimeter speed lines (evidence runs read their strength).</summary>
        public UI.SpeedLines SpeedLines => speedLines;
        int seenRecoveries;
        string recoveryNotice = "";
        float recoveryNoticeUntil;
        /// <summary>The driving camera (tours and tests switch its view).</summary>
        public DrivingCamera Camera => chase;
        UI.RaceHud hud;
        readonly UI.HudState hudState = new UI.HudState();

        /// <summary>
        /// Ghosts (spec §8): the header the player's run is recorded under (null = no recording; course revision and rule
        /// versions are filled in here), the recorded run when the race ends, and up to three replay overlays to show — only
        /// those recorded under this event's rules are shown (<see cref="Core.Ghosts.GhostRecording.CompatibleWith"/>).
        /// </summary>
        public Core.Ghosts.GhostHeader GhostTemplate;
        /// <summary>Automation only: called after every racing tick (telemetry tours read the entrants' states).</summary>
        public System.Action<RaceSimulation, int> TickObserver;
        /// <summary>The player's input on the last tick (tutorial lessons read the brake).</summary>
        public DriverInput LastPlayerInput { get; private set; }
        /// <summary>Camera views the player cycled through this session (the camera button or <see cref="CycleCamera"/>).</summary>
        public int CameraChanges { get; private set; }
        int resetHoldTicks;

        /// <summary>Cycles the driving camera as the camera button does (tours and lessons).</summary>
        public void CycleCamera()
        {
            if (chase == null) return;
            chase.Cycle();
            CameraChanges++;
        }

        /// <summary>Holds reset for <paramref name="seconds"/> of race time, as holding the button does (automation).</summary>
        public void HoldReset(float seconds) => resetHoldTicks = Mathf.CeilToInt(seconds * VehicleSimulation.TickRate);

        /// <summary>Ends the session now, classified as it stands (a judged tutorial lesson; nothing is applied from it).</summary>
        public void EndNow()
        {
            if (Phase == MatchPhase.Results || Sim == null) return;
            if (recorder != null) PlayerGhost = recorder.Finish(Player.Progress);
            Results = Sim.Classify();
            Phase = MatchPhase.Results;
        }
        public readonly List<Core.Ghosts.GhostRecording> GhostCandidates = new List<Core.Ghosts.GhostRecording>();
        public Core.Ghosts.GhostRecording PlayerGhost { get; private set; }
        public readonly List<GhostPlayback> Ghosts = new List<GhostPlayback>();
        public const int MaxGhosts = 3;
        GhostRecorder recorder;
        int ghostCheckpoints;
        string ghostDelta = "";
        float ghostDeltaUntil;
        /// <summary>The sector deltas against the first ghost, in checkpoint order (evidence and results).</summary>
        public readonly List<long> GhostDeltasMicros = new List<long>();
        readonly UI.DriftHudFeed driftFeed = new UI.DriftHudFeed();
        double accumulator, tickMsSum;
        int ticksMeasured;
        bool latchUp, latchDown;
        readonly Stopwatch watch = new Stopwatch();

        void Start()
        {
            CourseRuntime course = CourseRuntime.Active;
            ContentLibrary lib = ContentLibrary.Load();
            if (course == null || course.Track == null || lib == null)
            {
                UnityEngine.Debug.LogError("[NightSignal.Offline] Needs an active generated course and Resources/ContentLibrary.");
                enabled = false;
                return;
            }
            Headless |= Application.isBatchMode;
            var world = new PhysicsVehicleWorld(Physics.defaultPhysicsScene, GameLayers.DrivableMask, GameLayers.BarrierMask);
            var humans = new List<HumanSlot> { new HumanSlot { EntrantId = "local", DisplayName = PlayerName, CarId = CarId, Spec = PlayerSpec, Livery = PlayerLivery } };
            Sim = RaceSimulation.Build(course.Track, lib, Rules, humans, OpposingAi, world, FriendlyAi);
            Player = Sim.Entrants[0];
            Player.Status = EntrantStatus.Loaded;
            Sim.HumanInput = LocalInput;
            Sim.StartTick = CountdownTicks;
            DriverProfile pilotProfile = DriverProfile.Validator;
            pilotProfile.DriftSkill = AutopilotDriftSkill;
            pilotProfile.NoHandbrake = AutopilotNoHandbrake;
            pilotProfile.EdgeMargin = AutopilotEdgeMargin;
            pilot = new RouteFollower(course.Track, Player.Params, pilotProfile)
            {
                DriftZones = AutopilotDrivesChallengeZones && Sim.ZoneChains != null ? Sim.ZoneChains.AutopilotSpans() : Sim.DriftZonesForAi,
                ApexGates = Sim.Contracts?.ApexGates ?? (AutopilotAimsChallengeGates ? Sim.Gates?.TouchGates : null),
                LineZones = AutopilotDrivesChallengeZones ? Sim.ZoneChains?.AutopilotLines(ZoneSlideClipInset)
                    : AutopilotHoldsMarkedLanes ? course.Track.Gates.Where(g => g.Kind == "lane" && !string.IsNullOrEmpty(g.Challenge)).ToList() : null,
                ResetWhenStuck = true, SurfaceGrip = CourseRuntime.SurfaceGrip(Rules.Surface),
            };
            if (AutopilotDrivesChallengeZones)
            {
                // Sustained slides: a shallower target in the middle of CH19's band and slip-rate damping against overshoot.
                pilot.DriftSlipDeg = ZoneSlideSlipDeg;
                pilot.SlipRateSteer = ZoneSlideRateSteer;
                pilot.SlipRateThrottle = ZoneSlideRateThrottle;
                pilot.PathFollow = ZoneSlidePathFollow;
                pilot.PathThrottle = ZoneSlidePathThrottle;
                pilot.DriftEntrySpeed = ZoneSlideEntrySpeed;
                pilot.TransitionGraceSeconds = ZoneSlideTransitionGrace;
            }
            foreach (RaceEntrant e in Sim.Entrants) previous[e] = e.State;

            if (GhostTemplate != null)
            {
                GhostTemplate.CourseId = course.Track.CourseId;
                GhostTemplate.CourseRevision = course.SourceHash ?? "";
                GhostTemplate.Surface = string.IsNullOrEmpty(Rules.Surface) ? "dry" : Rules.Surface;
                GhostTemplate.PhysicsVersion = RaceSimulation.PhysicsVersion;
                GhostTemplate.ScoringVersion = RaceSimulation.ScoringVersion;
                GhostTemplate.GameVersion = Application.version;
                GhostTemplate.RecordedUtc = System.DateTime.UtcNow;
                recorder = new GhostRecorder(GhostTemplate);
            }
            if (!Headless)
            {
                var mats = Resources.Load<CarMaterialSet>("CarMaterialSet");
                foreach (RaceEntrant e in Sim.Entrants)
                {
                    float[] c = e.Roster.Paint;
                    VehicleView v = VehicleView.Create($"Car_{e.Roster.Index}_{e.Roster.CarId}", e.Params, lib.Body(e.Roster.CarId), mats, new Color(c[0], c[1], c[2]),
                        AppearanceMapping.ForWire(lib.Customization, e.Roster.CarId, e.Roster.Livery));
                    v.SetHeadlights(course.Dark);
                    views[e] = v;
                    GameAudio.CarAudio.Attach(v, e.Params, e.Roster.CarId, e == Player);
                }
                // Replay overlays: at most three, only under this event's rules (the rest stay reference-only).
                foreach (Core.Ghosts.GhostRecording g in GhostCandidates)
                {
                    if (Ghosts.Count >= MaxGhosts) break;
                    if (GhostTemplate == null || !g.CompatibleWith(GhostTemplate) || g.Count < 2 || !lib.Catalogue.TryCar(g.Header.CarModelId, out Core.Content.CarDef gc)) continue;
                    VehicleParams gp = lib.Params(gc.Id, AssistSettings.Default);
                    bool reference = RivalReferenceGhosts.IsReference(g), gold = TrialGhosts.IsTrialGhost(g);
                    VehicleView gv = VehicleView.Create($"Ghost_{Ghosts.Count}_{gc.Id}", gp, lib.Body(gc.Id), mats,
                        reference ? new Color(1f, 0.45f, 0.35f) : gold ? TrialGhosts.Tint : new Color(0.35f, 0.85f, 1f));
                    string who = reference ? RivalReferenceGhosts.Owner(g) : gold ? TrialGhosts.Label : string.IsNullOrEmpty(g.Header.Driver) ? "best" : g.Header.Driver;
                    string label = $"Ghost · {who} {g.Header.ResultMicros / 1e6:F3} s";
                    Ghosts.Add(new GhostPlayback(g, gv, label, reference ? RivalReferenceGhosts.Tint : gold ? TrialGhosts.Tint : (Color?)null));
                }
                var camGo = CameraRig.EnsureMain("RaceCamera").gameObject;
                camGo.tag = "MainCamera";
                chase = DrivingCameraFeed.Attach(camGo);
                chase.SetTarget(views[Player]);
                speedLines = UI.SpeedLines.Create();
                hud = UI.RaceHud.Create();
                hud.SetCourse(UI.HudHelpers.Plan(course.Track));
            }
            if (!Autopilot)
            {
                controls = new DrivingControls();
                controls.Enable();
            }
            Phase = MatchPhase.Countdown;
            Ready = true;
            if (!Headless)
                GameAudio.RaceMusicPlayer.Start(lib.Catalogue, course.Track.CourseId, Rules.Kind, Rules.StageId, Rules.Mode.ToString() == "Hard", null, Rules.DriftRanking);
        }

        DriverInput LocalInput(RaceEntrant e, int tick)
        {
            DriverInput input = SampleInput(e, tick);
            if (resetHoldTicks > 0)
            {
                resetHoldTicks--;
                input = DriverInput.Quantize(input.Steer, input.Throttle, input.Brake, input.Buttons | InputButtons.ResetHeld);
            }
            LastPlayerInput = input;
            return input;
        }

        DriverInput SampleInput(RaceEntrant e, int tick)
        {
            if (Autopilot)
            {
                if (tick < Sim.StartTick + (int)(AutopilotHoldSeconds * VehicleSimulation.TickRate)) return DriverInput.Quantize(0f, 0f, 1f, InputButtons.None);
                DriverInput d = pilot.Drive(e.State, Sim.TrafficFor(e));
                RacecraftRun rc = e.Racecraft;
                bool attack = AutopilotAttacksMarkedZones && Sim.Racecraft != null && Sim.Racecraft.InPassZone(e.Progress.RaceDistance);
                if (!attack && AutopilotFollowSeconds > 0f && rc != null && rc.Ahead >= 0 && rc.Interval >= 0f && rc.Interval < AutopilotFollowSeconds)
                {
                    // Closer than wanted: ease off, lift, then brake gently (the route follower still steers).
                    float close = AutopilotFollowSeconds - rc.Interval;
                    d = DriverInput.Quantize(d.Steer, close > 0.15f ? 0f : d.Throttle * 0.5f, close > 0.4f ? Mathf.Clamp01(close) * 0.6f : d.Brake, d.Buttons);
                }
                return d;
            }
            DriverInput i = controls.Sample(latchUp, latchDown);
            latchUp = latchDown = false;
            return i;
        }

        void Update()
        {
            if (!Ready) return;
            if (controls != null)
            {
                if (controls.ShiftUpPressedThisFrame) latchUp = true;
                if (controls.ShiftDownPressedThisFrame) latchDown = true;
                if (chase != null && controls.CameraPressed) CycleCamera();
                if (chase != null) chase.LookBack = controls.LookBackHeld;
            }
            int speed = Mathf.Max(1, SimulationSpeed);
            accumulator += Time.deltaTime * speed;
            int maxSteps = 8 * speed, steps = 0;
            while (accumulator >= VehicleSimulation.TickDt && steps < maxSteps && Phase != MatchPhase.Results)
            {
                FixedTick();
                accumulator -= VehicleSimulation.TickDt;
                steps++;
            }
            if (steps == maxSteps) accumulator = 0;
            if (!Headless && !SoakSkipRender) Render();
        }

        void FixedTick()
        {
            CurrentTick++;
            foreach (RaceEntrant e in Sim.Entrants) previous[e] = e.State;
            if (CurrentTick < Sim.StartTick) return;
            Phase = MatchPhase.Racing;
            watch.Restart();
            Sim.Tick(CurrentTick);
            TickObserver?.Invoke(Sim, CurrentTick);
            recorder?.Step(Player, Sim.RaceMicros(CurrentTick), CurrentTick);
            GhostDeltas();
            watch.Stop();
            double ms = watch.Elapsed.TotalMilliseconds;
            tickMsSum += ms;
            ticksMeasured++;
            if (ms > TickMsMax) TickMsMax = ms;
            foreach (RaceEntrant e in Sim.Entrants)
            {
                if (!e.Collides) continue;
                float vy = Mathf.Abs(e.State.Velocity.y);
                MaxVerticalSpeed = Mathf.Max(MaxVerticalSpeed, vy);
                if (!MaxVerticalByEntrant.TryGetValue(e, out Vector4 worst) || vy > worst.w)
                    MaxVerticalByEntrant[e] = new Vector4(e.State.Position.x, e.State.Position.y, e.State.Position.z, vy);
                TrackLocation loc = e.Progress.Location;
                if (!recentTrace.TryGetValue(e, out Queue<string> trace)) recentTrace[e] = trace = new Queue<string>();
                trace.Enqueue($"{CurrentTick}: d={loc.Distance:F1} lat={loc.Lateral:F2} vert={loc.Vertical:F2} vel=({e.State.Velocity.x:F1},{e.State.Velocity.y:F1},{e.State.Velocity.z:F1}) up={(e.State.Rotation * Vector3.up).y:F2} contacts={e.Progress.VehicleContacts} walls={e.Progress.WallIncidents}");
                if (trace.Count > 150) trace.Dequeue();
                if (!loc.InCorridor && !FirstCorridorExit.ContainsKey(e))
                {
                    FirstCorridorExit[e] = $"t={Sim.RaceMicros(CurrentTick) / 1e6:F1}s d={loc.Distance:F0} lat={loc.Lateral:F1} vert={loc.Vertical:F1} v={e.State.SpeedKmh:F0}km/h contacts={e.Progress.VehicleContacts}";
                    ExitTraces[e] = trace.ToArray();
                }
                if (!IsFinite(e.State.Position) || !IsFinite(e.State.Velocity)) AnyNonFinite = true;
            }
            if (Sim.Complete)
            {
                if (recorder != null) PlayerGhost = recorder.Finish(Player.Progress);
                Results = Sim.Classify();
                Phase = MatchPhase.Results;
                if (!Headless)
                {
                    RaceEntrantResult mine = Results.Find(x => x.Entrant == Player);
                    GameAudio.RaceMusicPlayer.Results(mine != null && mine.Outcome == RunOutcome.Finished && mine.Placement == 1);
                }
            }
        }

        static bool IsFinite(Vector3 v) => !float.IsNaN(v.x + v.y + v.z) && !float.IsInfinity(v.x + v.y + v.z);

        /// <summary>At each checkpoint the player passes: the time against the first ghost's at the same checkpoint.</summary>
        void GhostDeltas()
        {
            if (Ghosts.Count == 0) return;
            while (ghostCheckpoints < Player.Progress.CheckpointsPassed)
            {
                long mine = Player.Progress.Finished && ghostCheckpoints == Player.Progress.CheckpointsPassed - 1 ? Player.Progress.FinishTimeMicros : Sim.RaceMicros(CurrentTick);
                long? d = Ghosts[0].Recording.SectorDeltaMicros(ghostCheckpoints, mine);
                ghostCheckpoints++;
                if (d == null) continue;
                GhostDeltasMicros.Add(d.Value);
                ghostDelta = $"CHECKPOINT {ghostCheckpoints}  {(d.Value <= 0 ? "−" : "+")}{System.Math.Abs(d.Value) / 1e6:F2} s  vs {Ghosts[0].Label}";
                ghostDeltaUntil = Time.unscaledTime + 3f;
            }
        }

        void Render()
        {
            float alpha = (float)(accumulator / VehicleSimulation.TickDt);
            foreach (KeyValuePair<RaceEntrant, VehicleView> kv in views)
                kv.Value.Render(previous[kv.Key], kv.Key.State, alpha, kv.Key.Sim.Telemetry, Time.deltaTime);
            float raceT = CurrentTick >= Sim.StartTick ? (CurrentTick - Sim.StartTick + alpha) / VehicleSimulation.TickRate : -1f;
            foreach (GhostPlayback g in Ghosts) g.Show(raceT, Time.deltaTime);
            RenderHud();
        }

        void RenderHud()
        {
            if (hud == null) return;
            var order = new List<RaceEntrant>(Sim.Entrants);
            order.Sort((a, b) =>
            {
                bool fa = a.Progress.Finished, fb = b.Progress.Finished;
                if (fa && fb) return a.Progress.FinishTimeMicros.CompareTo(b.Progress.FinishTimeMicros);
                if (fa != fb) return fa ? -1 : 1;
                return b.Progress.RaceDistance.CompareTo(a.Progress.RaceDistance);
            });
            hudState.Field.Clear();
            int myPos = 0;
            for (int i = 0; i < order.Count; i++)
            {
                RaceEntrant c = order[i];
                bool me = c == Player;
                if (me) myPos = i + 1;
                string status = c.Status == EntrantStatus.Finished ? (c.Progress.FinishTimeMicros / 1e6).ToString("F3") + " s"
                    : c.Status == EntrantStatus.Dnf ? "DNF" : c.Human ? "" : c.Roster.Team == "player" ? "ALLY" : "AI";
                hudState.Field.Add(new UI.HudEntrant { Name = c.Roster.DisplayName, Status = status, Position = c.State.Position, IsYou = me, Distance = c.Progress.RaceDistance });
            }
            VehicleState s = Player.State;
            hudState.RoadSpeedMps = Player.Sim.Telemetry.RoadSpeedMps;
            hudState.EnvelopeMps = UI.SpeedDisplay.EnvelopeMps(Player.Params);
            hudState.Gear = s.Gear;
            hudState.Rpm = s.EngineRpm;
            hudState.Redline = Player.Params.RedlineRpm;
            hudState.RaceSeconds = Player.Progress.Finished ? Player.Progress.FinishTimeMicros / 1e6 : CurrentTick >= Sim.StartTick ? Sim.RaceMicros(CurrentTick) / 1e6 : 0;
            hudState.Position = myPos;
            hudState.Entrants = Sim.Entrants.Count;
            hudState.Checkpoints = Player.Progress.CheckpointsPassed;
            hudState.TotalCheckpoints = Sim.Tracker.TotalCheckpoints;
            hudState.WallIncidents = Player.Progress.WallIncidents;
            hudState.Resets = Player.Progress.Resets;
            hudState.GhostDelta = Time.unscaledTime < ghostDeltaUntil ? ghostDelta : "";
            float ghostT = CurrentTick >= Sim.StartTick ? (CurrentTick - Sim.StartTick) / (float)VehicleSimulation.TickRate : -1f;
            foreach (GhostPlayback g in Ghosts)
                hudState.Field.Add(new UI.HudEntrant { Name = g.Label, Position = g.At(Mathf.Max(0f, ghostT)).Position, IsReplay = true });
            RacecraftRun rc = Player.Racecraft;
            hudState.GapAheadSeconds = rc != null && rc.Ahead >= 0 ? rc.Interval : -1f;
            hudState.GapAheadName = rc != null && rc.Ahead >= 0 ? Sim.Entrants[rc.Ahead].Roster.DisplayName : "";
            if (chase != null)
            {
                // A recovery is a discontinuity: cut the camera to the new pose instead of flying through the mountain.
                if (Player.Progress.Recoveries.Count != seenRecoveries) { seenRecoveries = Player.Progress.Recoveries.Count; chase.NotifyTeleport(); hud.NotifyDiscontinuity(); }
                DrivingCameraFeed.Feed(chase, speedLines, Player.Sim.Telemetry, Player.State, Player.Params, Time.deltaTime);
            }
            RecoveryHud();
            if (Sim.Rules.DriftRanking)
                driftFeed.Update(hudState, (long)Player.Drift.BankedRaw, (long)Player.Drift.UnbankedRaw, (long)Player.Drift.LostRaw,
                    (float)Player.Drift.ChainMultiplier, Time.unscaledTime);
            hudState.FinishWindowSeconds = Sim.DeadlineMicros != long.MaxValue && Phase == MatchPhase.Racing
                ? Mathf.Max(0f, (Sim.DeadlineMicros - Sim.RaceMicros(CurrentTick)) / 1e6f) : -1f;
            string countdown = Phase != MatchPhase.Results ? UI.HudHelpers.Countdown((Sim.StartTick - CurrentTick) / 60f) : "";
            hudState.Banner = Phase == MatchPhase.Results ? "RESULTS\n<size=40%>LOCAL / OFFLINE — NOT AN ONLINE RESULT</size>"
                : Player.Status == EntrantStatus.Finished ? "FINISH" : countdown;
            hud.Render(hudState);
        }

        /// <summary>Recovery offer/countdown/hold progress from the simulation, and a brief notice after each completed recovery.</summary>
        void RecoveryHud()
        {
            int count = Player.Progress.Recoveries.Count;
            if (count > noticedRecoveries)
            {
                noticedRecoveries = count;
                RecoveryEvent last = Player.Progress.Recoveries[count - 1];
                string why = last.Reason == "manual" ? "RESET" : last.Reason == "off-route" ? "RECOVERED — OFF ROUTE" : last.Reason == "overturned" ? "RECOVERED — OVERTURNED" : "RECOVERED";
                recoveryNotice = $"{why}  <size=80%><color=#9A968D>+{last.PenaltyMs / 1000f:0.000} s · clock running</color></size>";
                recoveryNoticeUntil = Time.unscaledTime + 2.5f;
            }
            hudState.RecoveryNotice = Time.unscaledTime < recoveryNoticeUntil ? recoveryNotice : "";
            UI.RaceHud.SetRecovery(hudState, Sim.Recovery(Player), controls != null ? controls.BindingLabel("Reset") : "R", true);
        }

        int noticedRecoveries;

        void OnDestroy()
        {
            controls?.Dispose();
            if (hud != null) Destroy(hud.gameObject);
            if (speedLines != null) Destroy(speedLines.gameObject);
            foreach (VehicleView v in views.Values)
                if (v != null) Destroy(v.gameObject);
        }
    }
}
