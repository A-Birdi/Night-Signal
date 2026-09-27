using System.Collections.Generic;
using System.Diagnostics;
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
        public int CountdownTicks = 60 * 3;

        public RaceSimulation Sim { get; private set; }
        public RaceEntrant Player { get; private set; }
        /// <summary>The player's car as drawn (null headless or before the views exist).</summary>
        public VehicleView PlayerView => Player != null && views.TryGetValue(Player, out VehicleView v) ? v : null;
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
        RouteFollower pilot;
        ChaseCamera chase;
        UI.RaceHud hud;
        readonly UI.HudState hudState = new UI.HudState();
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
            pilot = new RouteFollower(course.Track, Player.Params, DriverProfile.Validator) { DriftZones = Sim.DriftZonesForAi, ResetWhenStuck = true };
            foreach (RaceEntrant e in Sim.Entrants) previous[e] = e.State;

            if (!Headless)
            {
                var mats = Resources.Load<CarMaterialSet>("CarMaterialSet");
                foreach (RaceEntrant e in Sim.Entrants)
                {
                    float[] c = e.Roster.Paint;
                    VehicleView v = VehicleView.Create($"Car_{e.Roster.Index}_{e.Roster.CarId}", e.Params, lib.Body(e.Roster.CarId), mats, new Color(c[0], c[1], c[2]),
                        AppearanceMapping.ForWire(lib.Customization, e.Roster.CarId, e.Roster.Livery));
                    v.SetHeadlights(course.DefaultTimeOfDay == "night");
                    views[e] = v;
                }
                var camGo = CameraRig.EnsureMain("RaceCamera").gameObject;
                camGo.tag = "MainCamera";
                chase = camGo.GetComponent<ChaseCamera>() ?? camGo.AddComponent<ChaseCamera>();
                chase.Target = views[Player].transform;
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
        }

        DriverInput LocalInput(RaceEntrant e, int tick)
        {
            if (Autopilot) return pilot.Drive(e.State, Sim.TrafficFor(e));
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
                if (chase != null && controls.CameraPressed) chase.Cycle();
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
            if (!Headless) Render();
        }

        void FixedTick()
        {
            CurrentTick++;
            foreach (RaceEntrant e in Sim.Entrants) previous[e] = e.State;
            if (CurrentTick < Sim.StartTick) return;
            Phase = MatchPhase.Racing;
            watch.Restart();
            Sim.Tick(CurrentTick);
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
                Results = Sim.Classify();
                Phase = MatchPhase.Results;
            }
        }

        static bool IsFinite(Vector3 v) => !float.IsNaN(v.x + v.y + v.z) && !float.IsInfinity(v.x + v.y + v.z);

        void Render()
        {
            float alpha = (float)(accumulator / VehicleSimulation.TickDt);
            foreach (KeyValuePair<RaceEntrant, VehicleView> kv in views)
                kv.Value.Render(previous[kv.Key], kv.Key.State, alpha, kv.Key.Sim.Telemetry, Time.deltaTime);
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
            hudState.SpeedKmh = s.SpeedKmh;
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

        void OnDestroy()
        {
            controls?.Dispose();
            if (hud != null) Destroy(hud.gameObject);
            foreach (VehicleView v in views.Values)
                if (v != null) Destroy(v.gameObject);
        }
    }
}
