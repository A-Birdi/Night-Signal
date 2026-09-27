using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Cameras;
using NightSignal.Content;
using NightSignal.InputBindings;
using NightSignal.Track;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Garage
{
    /// <summary>A frozen build to drive in the Test Yard (A = baseline, B = candidate).</summary>
    public sealed class TestBuild
    {
        public string Label;
        public string CarId;
        /// <summary>Stable hash of the exact mechanical build (parts + tuning + handling version).</summary>
        public string BuildHash;
        public VehicleParams Params;
        public Color Paint = new Color(0.82f, 0.12f, 0.12f);
        /// <summary>Contains parts the player does not own: 'Preview only — not owned'.</summary>
        public bool PreviewOnly;
    }

    public enum YardStart { LoopStart = 0, BrakingLane = 1, SkidPad = 2, Slalom = 3 }

    /// <summary>One observed run of one build (only what was actually measured).</summary>
    public sealed class YardRun
    {
        public string Build;
        public string BuildHash;
        public YardStart Start;
        public string Surface;
        public string AssistsTag;
        /// <summary>0–100 km/h in seconds, or -1 when not reached from a standing start in this run.</summary>
        public float ZeroTo100 = -1f;
        public float MaxSpeedKmh;
        /// <summary>Braking distance from ≥ 100 km/h to stop (m), or -1 if no such stop happened.</summary>
        public float Brake100To0Metres = -1f;
        /// <summary>Distance from a painted stop box centre when the car stopped inside the lane (m), or NaN.</summary>
        public float StopBoxErrorMetres = float.NaN;
        public float PeakLateralG;
        public float PeakSlipDeg;
        public float Seconds;
    }

    /// <summary>
    /// Garage Test Yard (Addendum 02 §10): the player drives the SAME vehicle simulation, controller, assists and surface
    /// grip rules as a race, privately, on a compact authored facility (braking lane with stop boxes, skid pad, slalom,
    /// an 834 m loop with a tightening corner, crest and banked return). A/B switching happens only by reset to a matched
    /// start at rest, never at speed. Nothing here spends money, grants parts or progression, or writes a record; results
    /// are labelled observations with their conditions, and anything not measured (e.g. top speed on a short straight)
    /// is reported as not reached.
    /// </summary>
    public sealed class TestYardSession : MonoBehaviour
    {
        [System.NonSerialized] public TestBuild A, B;
        public bool DrivingB;
        public YardStart StartPoint = YardStart.LoopStart;
        public string Surface = "dry";
        public string AssistsTag = "default";
        public readonly List<YardRun> Runs = new List<YardRun>();

        public TestBuild Current => DrivingB && B != null ? B : A;
        public VehicleState State => state;
        public bool Ready { get; private set; }

        VehicleSimulation sim;
        VehicleState state, previous;
        VehicleView view;
        DrivingControls controls;
        ChaseCamera chase;
        TrackData track;
        double accumulator;
        bool latchUp, latchDown;
        YardRun run;
        float runTime, zeroStart = -1f, brakeStartSpeed, lastLateral;
        Vector3 brakeStartPos;
        bool braking;

        void Start()
        {
            CourseRuntime course = CourseRuntime.Active;
            if (course == null || course.Track == null || A == null)
            {
                Debug.LogError("[NightSignal.TestYard] needs the Test Yard scene and a baseline build.");
                enabled = false;
                return;
            }
            track = course.Track;
            controls = new DrivingControls();
            controls.Enable();
            Camera cam = CameraRig.EnsureMain("YardCamera");
            chase = cam.GetComponent<ChaseCamera>() ?? cam.gameObject.AddComponent<ChaseCamera>();
            ResetTo(StartPoint, DrivingB);
            Ready = true;
        }

        /// <summary>
        /// Matched reset: clears transient dynamics, places the chosen build at rest on the chosen start, and applies the
        /// surface preset. The only way A/B switches (no hot swap at speed).
        /// </summary>
        public void ResetTo(YardStart start, bool driveB)
        {
            FinishRun();
            StartPoint = start;
            DrivingB = driveB && B != null;
            TestBuild build = Current;
            var world = new PhysicsVehicleWorld(Physics.defaultPhysicsScene, GameLayers.DrivableMask, GameLayers.BarrierMask);
            sim = new VehicleSimulation(build.Params, world) { SurfaceGripScale = CourseRuntime.SurfaceGrip(Surface) };
            Pose(start, out Vector3 pos, out Quaternion rot);
            state = VehicleState.AtRest(pos, rot);
            previous = state;
            accumulator = 0;
            if (view != null) Destroy(view.gameObject);
            ContentLibrary lib = ContentLibrary.Load();
            view = VehicleView.Create($"TestCar_{build.Label}", build.Params, lib.Body(build.CarId), Resources.Load<CarMaterialSet>("CarMaterialSet"), build.Paint);
            if (chase != null) chase.Target = view.transform;
            run = new YardRun { Build = build.Label, BuildHash = build.BuildHash, Start = start, Surface = Surface, AssistsTag = AssistsTag };
            runTime = 0f;
            zeroStart = -1f;
            braking = false;
        }

        public void SetSurface(string surface)
        {
            Surface = surface == "wet" ? "wet" : "dry";
            ResetTo(StartPoint, DrivingB); // explicit reset whenever the surface changes
        }

        void Pose(YardStart start, out Vector3 pos, out Quaternion rot)
        {
            RouteDefinition route = CourseRuntime.Active.Route;
            string area = start == YardStart.BrakingLane ? "braking-lane" : start == YardStart.SkidPad ? "skid-pad" : start == YardStart.Slalom ? "slalom" : null;
            RouteAreaDef a = area == null ? null : route.Areas.Find(x => x.Kind == area);
            if (a == null)
            {
                GridSlot g = track.Grid[0];
                pos = g.Position;
                rot = g.Rotation;
                return;
            }
            rot = Quaternion.Euler(0f, a.HeadingDeg, 0f);
            Vector3 c = new Vector3(a.Centre[0], a.Centre[1], a.Centre[2]);
            Vector3 local = a.Kind == "skid-pad" ? new Vector3(0f, 0f, -(Mathf.Min(a.Size[0], a.Size[1]) * 0.5f - 8f)) : new Vector3(0f, 0f, -a.Size[1] * 0.5f + 6f);
            pos = c + rot * local + Vector3.up * 0.5f;
        }

        void Update()
        {
            if (!Ready) return;
            if (controls.ShiftUpPressedThisFrame) latchUp = true;
            if (controls.ShiftDownPressedThisFrame) latchDown = true;
            if (controls.CameraPressed) chase.Cycle();
            chase.LookBack = controls.LookBackHeld;
            accumulator += Time.deltaTime;
            int steps = 0;
            while (accumulator >= VehicleSimulation.TickDt && steps < 8)
            {
                DriverInput input = controls.Sample(latchUp, latchDown);
                latchUp = latchDown = false;
                previous = state;
                sim.Step(ref state, input);
                Measure(input);
                accumulator -= VehicleSimulation.TickDt;
                steps++;
            }
            if (steps == 8) accumulator = 0;
            view.Render(previous, state, (float)(accumulator / VehicleSimulation.TickDt), sim.Telemetry, Time.deltaTime);
        }

        /// <summary>Observed metrics only — nothing is inferred beyond what this run actually did.</summary>
        void Measure(DriverInput input)
        {
            float dt = VehicleSimulation.TickDt;
            runTime += dt;
            float kmh = state.SpeedKmh;
            run.Seconds = runTime;
            run.MaxSpeedKmh = Mathf.Max(run.MaxSpeedKmh, kmh);
            if (zeroStart < 0f && kmh < 1f && input.Throttle > 0.9f) zeroStart = runTime;
            if (zeroStart >= 0f && run.ZeroTo100 < 0f && kmh >= 100f) run.ZeroTo100 = runTime - zeroStart;
            if (kmh < 1f && input.Throttle < 0.1f) zeroStart = -1f;
            Vector3 right = state.Rotation * Vector3.right;
            float lateral = Vector3.Dot(state.Velocity - previous.Velocity, right) / dt / 9.81f;
            lastLateral = Mathf.Lerp(lastLateral, lateral, 0.2f);
            run.PeakLateralG = Mathf.Max(run.PeakLateralG, Mathf.Abs(lastLateral));
            run.PeakSlipDeg = Mathf.Max(run.PeakSlipDeg, sim.Telemetry.BodySlipDeg);
            if (!braking && kmh >= 100f && input.Brake > 0.8f)
            {
                braking = true;
                brakeStartSpeed = kmh;
                brakeStartPos = state.Position;
            }
            if (braking && input.Brake < 0.3f && kmh > 5f) braking = false;
            if (braking && kmh < 0.5f)
            {
                run.Brake100To0Metres = Vector3.Distance(brakeStartPos, state.Position);
                run.StopBoxErrorMetres = StopBoxError(state.Position);
                braking = false;
            }
        }

        float StopBoxError(Vector3 p)
        {
            RouteAreaDef lane = CourseRuntime.Active.Route.Areas.Find(x => x.Kind == "braking-lane");
            if (lane == null || lane.Gates == null) return float.NaN;
            Quaternion rot = Quaternion.Euler(0f, lane.HeadingDeg, 0f);
            Vector3 c = new Vector3(lane.Centre[0], lane.Centre[1], lane.Centre[2]);
            Vector3 local = Quaternion.Inverse(rot) * (p - c);
            if (Mathf.Abs(local.x) > lane.Size[0] * 0.5f) return float.NaN;
            float along = local.z + lane.Size[1] * 0.5f;
            float best = float.NaN;
            foreach (RouteGateDef g in lane.Gates)
            {
                if (g.Kind != "brake-zone") continue;
                float err = along - (g.StartMetres + g.EndMetres) * 0.5f;
                if (float.IsNaN(best) || Mathf.Abs(err) < Mathf.Abs(best)) best = err;
            }
            return best;
        }

        void FinishRun()
        {
            if (run != null && run.Seconds > 1f) Runs.Add(run);
            run = null;
        }

        /// <summary>The most recent valid runs for one build (the comparison keeps at least three each).</summary>
        public List<YardRun> RecentRuns(string buildLabel, int count = 3)
        {
            var list = new List<YardRun>();
            for (int i = Runs.Count - 1; i >= 0 && list.Count < count; i--)
                if (Runs[i].Build == buildLabel) list.Add(Runs[i]);
            return list;
        }

        void OnDestroy()
        {
            FinishRun();
            controls?.Dispose();
            if (view != null) Destroy(view.gameObject);
        }
    }
}
