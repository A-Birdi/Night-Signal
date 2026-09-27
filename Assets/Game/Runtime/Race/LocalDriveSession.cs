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
    /// Offline single-car session on the active course: human input or the route-following autopilot, fixed 60 Hz
    /// simulation with render interpolation, checkpoint/finish tracking and hold-to-reset. Used for practice,
    /// handling work and automated course validation (the autopilot uses the same inputs as a player).
    /// </summary>
    public sealed class LocalDriveSession : MonoBehaviour
    {
        public string CarId = "V01";
        public Color PaintColor = new Color(0.82f, 0.12f, 0.12f);
        public int GridSlot;
        public bool Autopilot;
        /// <summary>Simulation ticks per real tick (tests fast-forward; 1 = real time).</summary>
        public int SimulationSpeed = 1;
        public bool ShowDebugHud;
        public CarMaterialSet CarMaterials;

        public EntrantProgress Progress { get; private set; }
        public VehicleState State => current;
        public long RaceTimeMicros { get; private set; }
        public bool Ready { get; private set; }

        VehicleParams parameters;
        VehicleSimulation sim;
        VehicleState previous, current;
        VehicleView view;
        DrivingControls controls;
        RouteFollower autopilot;
        RaceProgressTracker tracker;
        DrivingCamera chase;
        UI.SpeedLines speedLines;
        double accumulator;
        bool latchUp, latchDown;
        float resetHeld, overturnedSeconds;
        bool resetNeedsRelease;

        void Start()
        {
            CourseRuntime course = CourseRuntime.Active;
            ContentLibrary lib = ContentLibrary.Load();
            if (course == null || course.Track == null || lib == null)
            {
                Debug.LogError("[NightSignal.Drive] Needs an active generated course and Resources/ContentLibrary.");
                enabled = false;
                return;
            }
            if (CarMaterials == null) CarMaterials = Resources.Load<CarMaterialSet>("CarMaterialSet");
            parameters = lib.Params(CarId, AssistSettings.Default);
            var world = new PhysicsVehicleWorld(Physics.defaultPhysicsScene, GameLayers.DrivableMask, GameLayers.BarrierMask);
            sim = new VehicleSimulation(parameters, world);
            GridSlot slot = course.Track.Grid[Mathf.Clamp(GridSlot, 0, course.Track.Grid.Length - 1)];
            current = VehicleState.AtRest(slot.Position, slot.Rotation);
            previous = current;
            view = VehicleView.Create($"Car_{CarId}", parameters, lib.Body(CarId), CarMaterials, PaintColor);
            tracker = new RaceProgressTracker(course.Track);
            Progress = new EntrantProgress(course.Track);
            tracker.Start(Progress, current.Position);
            autopilot = new RouteFollower(course.Track, parameters, DriverProfile.Validator);

            var camGo = CameraRig.EnsureMain("RaceCamera").gameObject;
            camGo.tag = "MainCamera";
            chase = DrivingCameraFeed.Attach(camGo);
            chase.SetTarget(view);
            speedLines = UI.SpeedLines.Create();
            camGo.transform.position = slot.Position - slot.Rotation * Vector3.forward * 7f + Vector3.up * 2f;

            controls = new DrivingControls();
            controls.Enable();
            if (!Application.isBatchMode)
            {
                hud = UI.RaceHud.Create();
                hud.SetCourse(UI.HudHelpers.Plan(course.Track));
            }
            Ready = true;
        }

        UI.RaceHud hud;
        readonly UI.HudState hudState = new UI.HudState();

        void RenderHud()
        {
            if (hud == null) return;
            hudState.RoadSpeedMps = sim.Telemetry.RoadSpeedMps;
            hudState.EnvelopeMps = UI.SpeedDisplay.EnvelopeMps(parameters);
            hudState.Gear = current.Gear;
            hudState.Rpm = current.EngineRpm;
            hudState.Redline = parameters.RedlineRpm;
            hudState.RaceSeconds = (Progress.Finished ? Progress.FinishTimeMicros : RaceTimeMicros) / 1e6;
            hudState.Position = 1;
            hudState.Entrants = 1;
            hudState.Checkpoints = Progress.CheckpointsPassed;
            hudState.TotalCheckpoints = tracker.TotalCheckpoints;
            hudState.WallIncidents = Progress.WallIncidents;
            hudState.Resets = Progress.Resets;
            hudState.Banner = Progress.Finished ? "FINISH  " + UI.RaceHud.FormatTime(Progress.FinishTimeMicros / 1e6) + "\n<size=40%>PRACTICE — NOT RECORDED ONLINE</size>" : "";
            hudState.Field.Clear();
            hudState.Field.Add(new UI.HudEntrant { Name = CarId, Position = current.Position, IsYou = true, Status = "PRACTICE" });
            DrivingCameraFeed.Feed(chase, speedLines, sim.Telemetry, current, parameters, Time.deltaTime);
            // Practice offers the reset (overturned, well off the route) but never recovers the car by itself.
            var rs = new RecoveryStatus { HoldFraction = resetHeld / RaceSimulation.ResetHoldSeconds, SecondsToAuto = -1f };
            if (!Progress.Finished && overturnedSeconds >= RaceSimulation.OverturnedPromptSeconds) rs.Kind = RecoveryKind.Overturned;
            else if (!Progress.Finished && Progress.OffRouteSeconds > 1f) rs.Kind = RecoveryKind.OffRoute;
            UI.RaceHud.SetRecovery(hudState, rs, controls != null ? controls.BindingLabel("Reset") : "R", true);
            hud.Render(hudState);
        }

        void OnDestroy()
        {
            controls?.Dispose();
            if (hud != null) Destroy(hud.gameObject);
            if (speedLines != null) Destroy(speedLines.gameObject);
        }

        void Update()
        {
            if (!Ready) return;
            if (controls.ShiftUpPressedThisFrame) latchUp = true;
            if (controls.ShiftDownPressedThisFrame) latchDown = true;
            if (controls.CameraPressed) chase.Cycle();
            chase.LookBack = controls.LookBackHeld;

            accumulator += Time.deltaTime * Mathf.Max(1, SimulationSpeed);
            int maxSteps = 8 * Mathf.Max(1, SimulationSpeed);
            int steps = 0;
            while (accumulator >= VehicleSimulation.TickDt && steps < maxSteps)
            {
                Tick();
                accumulator -= VehicleSimulation.TickDt;
                steps++;
            }
            if (steps == maxSteps) accumulator = 0; // do not spiral when the frame rate collapses
            view.Render(previous, current, (float)(accumulator / VehicleSimulation.TickDt), sim.Telemetry, Time.deltaTime);
            RenderHud();
        }

        void Tick()
        {
            DriverInput input = Autopilot ? autopilot.Drive(current) : controls.Sample(latchUp, latchDown);
            latchUp = latchDown = false;
            previous = current;
            sim.Step(ref current, input);
            RaceTimeMicros += 1_000_000 / VehicleSimulation.TickRate;
            tracker.Step(Progress, previous, current, sim.Telemetry, RaceTimeMicros, VehicleSimulation.TickDt);

            // Hold-to-reset (the race's 0.75 s, released before another) returns to the last safe checkpoint with the 3 s penalty.
            if (!input.ResetHeld) resetNeedsRelease = false;
            resetHeld = input.ResetHeld && !resetNeedsRelease ? resetHeld + VehicleSimulation.TickDt : 0f;
            overturnedSeconds = RaceSimulation.IsOverturned(current) ? overturnedSeconds + VehicleSimulation.TickDt : 0f;
            if (resetHeld >= RaceSimulation.ResetHoldSeconds && !Progress.Finished)
            {
                current = tracker.ResetPose(Progress, parameters, 0, "manual"); // adds the 3 s penalty to the finish time
                chase.NotifyTeleport();
                hud?.NotifyDiscontinuity();
                previous = current;
                resetHeld = overturnedSeconds = 0f;
                resetNeedsRelease = true;
            }
        }

        void OnGUI()
        {
            if (!ShowDebugHud || !Ready) return;
            GUI.Label(new Rect(12, 12, 520, 24), $"{CarId}  {current.SpeedKmh:F0} km/h  gear {current.Gear}  {current.EngineRpm:F0} rpm  slip {sim.Telemetry.BodySlipDeg:F0}°");
            GUI.Label(new Rect(12, 34, 520, 24), $"checkpoint {Progress.CheckpointsPassed}/{tracker.TotalCheckpoints}  time {RaceTimeMicros / 1e6:F2} s  walls {Progress.WallIncidents}  resets {Progress.Resets}{(Progress.Finished ? "  FINISHED " + Progress.FinishTimeMicros / 1e6 : "")}");
        }
    }
}
