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
        public bool ShowDebugHud = true;
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
        ChaseCamera chase;
        double accumulator;
        bool latchUp, latchDown;
        float resetHeld;

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

            var camGo = Camera.main != null ? Camera.main.gameObject : new GameObject("RaceCamera", typeof(Camera));
            camGo.tag = "MainCamera";
            chase = camGo.GetComponent<ChaseCamera>() ?? camGo.AddComponent<ChaseCamera>();
            chase.Target = view.transform;
            camGo.transform.position = slot.Position - slot.Rotation * Vector3.forward * 7f + Vector3.up * 2f;

            controls = new DrivingControls();
            controls.Enable();
            Ready = true;
        }

        void OnDestroy()
        {
            controls?.Dispose();
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
        }

        void Tick()
        {
            DriverInput input = Autopilot ? autopilot.Drive(current) : controls.Sample(latchUp, latchDown);
            latchUp = latchDown = false;
            previous = current;
            sim.Step(ref current, input);
            RaceTimeMicros += 1_000_000 / VehicleSimulation.TickRate;
            tracker.Step(Progress, previous, current, sim.Telemetry, RaceTimeMicros, VehicleSimulation.TickDt);

            // Hold-to-reset (0.7 s) returns to the last safe checkpoint with the 3 s penalty.
            resetHeld = input.ResetHeld ? resetHeld + VehicleSimulation.TickDt : 0f;
            if (resetHeld >= 0.7f && !Progress.Finished)
            {
                current = tracker.ResetPose(Progress, parameters);
                previous = current;
                RaceTimeMicros += Limits.ResetPenaltyMs * 1000L;
                resetHeld = 0f;
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
