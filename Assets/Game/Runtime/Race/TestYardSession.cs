using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Art;
using NightSignal.Cameras;
using NightSignal.Content;
using NightSignal.Core.Rules;
using NightSignal.InputBindings;
using NightSignal.Track;
using NightSignal.UI;
using NightSignal.Vehicle;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NightSignal.Race
{
    /// <summary>One side of an A/B comparison, frozen when the yard opens (changing a preset elsewhere cannot alter it).</summary>
    public sealed class TestYardBuild
    {
        public string Label = "";
        public string BuildHash = "";
        public int Pi;
        public VehicleParams Params;
    }

    /// <summary>A measured run between two resets. Only what was actually measured is filled in.</summary>
    public sealed class TestYardRun
    {
        public bool B;
        public string Station = "";
        public string Surface = "dry";
        public float Seconds;
        public float TopKmh;
        public float ZeroTo60 = -1f, ZeroTo100 = -1f;
        public float StopMetres = -1f, StopFromKmh;
        public string StopBox = "";
        /// <summary>A braking-lane lesson: speed crossing its speed gate (-1 = not reached), where along the lane the first stop
        /// came to rest (m from the entry; NaN = none) and whether that is inside the lesson's stop gate.</summary>
        public float GateKmh = -1f, StopAlong = float.NaN;
        public bool InStopGate;
        public float PeakLateralG;
        public float LoopSeconds = -1f;

        public bool Comparable(TestYardRun o) => o != null && o.Station == Station && o.Surface == Surface;

        public string Summary()
        {
            var parts = new List<string>();
            if (ZeroTo60 > 0) parts.Add($"0–60 {ZeroTo60:0.00} s");
            if (ZeroTo100 > 0) parts.Add($"0–100 {ZeroTo100:0.00} s");
            else if (ZeroTo60 > 0) parts.Add("0–100 not reached in this test length");
            if (StopMetres > 0) parts.Add($"stop {StopMetres:0.0} m from {StopFromKmh:0} km/h{(StopBox.Length > 0 ? " · " + StopBox : "")}");
            if (PeakLateralG > 0.3f) parts.Add($"peak lateral {PeakLateralG:0.00} g");
            if (LoopSeconds > 0) parts.Add($"loop {LoopSeconds:0.00} s");
            parts.Add($"top {TopKmh:0} km/h seen");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// Garage Test Yard (Addendum 02 §10) on the T00 service campus geometry: the SAME vehicle simulation, controller,
    /// assists and surface rules as a race, driving A (baseline, normally the applied build) or B (the Garage draft,
    /// preview parts allowed). Switching always resets to the chosen station start at rest; nothing here is recorded,
    /// rewarded, bought or counted as a race. The last three runs per side are kept for comparison.
    /// </summary>
    public sealed class TestYardSession : MonoBehaviour
    {
        public string CarId = "V01";
        public Color PaintColor = new Color(0.82f, 0.12f, 0.12f);
        public TestYardBuild A = new TestYardBuild(), B = new TestYardBuild();
        public bool StartWithB;
        public CarMaterialSet CarMaterials;
        /// <summary>Scripted driving for automated evidence runs (state, seconds since the reset) — replaces the controls.</summary>
        public Func<VehicleState, float, DriverInput> Script;
        public int SimulationSpeed = 1;
        /// <summary>
        /// A Driving School braking-lane lesson (CH02, CH47) rather than the Test Yard: the lane station only, A the trial's
        /// supplied car and B (a comparison lesson only) the loaned package, every start kept for the judge. The yard itself
        /// still records nothing (Addendum 02 §10.1); the lesson is judged by the caller. Null: the Test Yard.
        /// </summary>
        public ChallengeTrialDef Lesson;
        /// <summary>Every start of a lesson, in order (the yard keeps only the last three per side).</summary>
        public readonly List<TestYardRun> LessonRuns = new List<TestYardRun>();
        /// <summary>The lesson's speed gate and stop gate along the lane (m from its entry).</summary>
        public float LessonSpeedGateAlong { get; private set; } = -1f;
        public float LessonStopFrom { get; private set; } = -1f;
        public float LessonStopTo { get; private set; } = -1f;
        bool CanDriveB => Lesson == null || !string.IsNullOrEmpty(Lesson.ComparePart);

        /// <summary>Where a point is along the braking lane (m from its entry) and across it (m, + = right).</summary>
        public float LaneAlong(Vector3 p) => Vector3.Dot(p - laneEntry, laneDir);
        public float LaneLateral(Vector3 p) => Vector3.Cross(laneDir, p - laneEntry).y;

        public bool Ready { get; private set; }
        public bool ExitRequested { get; private set; }
        public bool DrivingB { get; private set; }
        public string Surface { get; private set; } = "dry";
        public int StationIndex { get; private set; }
        public VehicleState State => current;
        /// <summary>The run being measured now (null right after an exit).</summary>
        public TestYardRun CurrentRun => run;
        /// <summary>Diagnostics for evidence runs: what the chase camera follows and where it is.</summary>
        public string CameraDebug => chase == null ? "no camera" : $"target {(chase.Target != null ? chase.Target.name + " " + chase.Target.transform.position : "none")} cam {chase.transform.position} enabled {chase.enabled}/{chase.gameObject.activeInHierarchy} view {chase.View} car {(view != null ? view.transform.position.ToString() : "none")} state {current.Position} · views: " +
            string.Join("; ", FindObjectsByType<VehicleView>(FindObjectsSortMode.None).Select(v => $"{v.name} {v.transform.position} active {v.gameObject.activeInHierarchy} " +
                string.Join("/", v.GetComponentsInChildren<Renderer>(true).Take(2).Select(r => $"{r.name}:{r.enabled}:{r.bounds.center}"))));
        public readonly List<TestYardRun> RunsA = new List<TestYardRun>(), RunsB = new List<TestYardRun>();

        sealed class Station
        {
            public string Id, Label;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        readonly List<Station> stations = new List<Station>();
        // Braking-lane frame for stop boxes: entry point, direction and the boxes along it (metres from the entry).
        Vector3 laneEntry, laneDir;
        readonly List<(string Id, float From, float To)> stopBoxes = new List<(string, float, float)>();

        ContentLibrary lib;
        VehicleSimulation sim;
        VehicleState previous, current;
        VehicleView view;
        DrivingControls controls;
        DrivingCamera chase;
        SpeedLines speedLines;
        /// <summary>The driving camera (tours switch its view).</summary>
        public DrivingCamera Camera => chase;
        /// <summary>Evidence runs: the driven car's latest telemetry and the HUD instrument.</summary>
        public StepTelemetry Telemetry => sim != null ? sim.Telemetry : default;
        public RaceHud Hud => hud;
        RaceHud hud;
        readonly HudState hudState = new HudState();
        TextMeshProUGUI panel;
        Canvas overlay;
        double accumulator;
        bool latchUp, latchDown;
        float resetHeld, overturnedSeconds;
        bool resetNeedsRelease;

        // The run being measured.
        TestYardRun run;
        float runSeconds, launchAt = -1f, brakeFromKmh;
        Vector3 brakeFrom;
        bool braking, leftStart;
        float lateralWindow;
        int lateralTicks;

        void Start()
        {
            CourseRuntime course = CourseRuntime.Active;
            lib = ContentLibrary.Load();
            if (course == null || course.Track == null || lib == null || A.Params == null || B.Params == null)
            {
                Debug.LogError("[NightSignal.TestYard] Needs the generated service campus, the content library and both builds.");
                ExitRequested = true;
                enabled = false;
                return;
            }
            if (CarMaterials == null) CarMaterials = Resources.Load<CarMaterialSet>("CarMaterialSet");
            BuildStations(course);
            var camGo = CameraRig.EnsureMain("RaceCamera").gameObject;
            camGo.tag = "MainCamera";
            chase = DrivingCameraFeed.Attach(camGo);
            speedLines = SpeedLines.Create();
            controls = new DrivingControls();
            controls.Enable();
            if (!Application.isBatchMode)
            {
                hud = RaceHud.Create();
                hud.SetCourse(HudHelpers.Plan(course.Track));
                BuildOverlay();
            }
            ResetAndDrive(StartWithB, 0);
            Ready = true;
        }

        void BuildStations(CourseRuntime course)
        {
            RouteDefinition route = course.Route;
            RouteAreaDef lane = route?.Areas?.FirstOrDefault(a => a.Kind == "braking-lane");
            if (lane != null)
            {
                Quaternion rot = Quaternion.Euler(0f, lane.HeadingDeg, 0f);
                Vector3 c = new Vector3(lane.Centre[0], lane.Centre[1], lane.Centre[2]);
                float hl = lane.Size[1] * 0.5f;
                laneDir = rot * Vector3.forward;
                laneEntry = c - laneDir * hl;
                stations.Add(new Station { Id = "straight", Label = $"Launch & braking straight ({lane.Size[1]:0} m)", Position = laneEntry + laneDir * 6f + Vector3.up * 0.4f, Rotation = rot });
                foreach (RouteGateDef g in lane.Gates ?? new List<RouteGateDef>())
                {
                    if (g.Kind == "brake-zone" && g.EndMetres - g.StartMetres <= 20f) stopBoxes.Add((g.Id, g.StartMetres, g.EndMetres));
                    if (Lesson != null && g.Id == Lesson.SpeedGate) LessonSpeedGateAlong = g.StartMetres;
                    if (Lesson != null && g.Id == Lesson.StopGate) { LessonStopFrom = g.StartMetres; LessonStopTo = g.EndMetres; }
                }
            }
            if (Lesson != null)
            {
                // A lesson is the lane alone: no skid pad, no loop.
                if (stations.Count == 0 || LessonSpeedGateAlong < 0f || LessonStopFrom < 0f)
                    Debug.LogError($"[NightSignal.TestYard] {Lesson.Id}: the braking lane or its gates {Lesson.SpeedGate}/{Lesson.StopGate} are missing.");
                return;
            }
            RouteAreaDef pad = route?.Areas?.FirstOrDefault(a => a.Kind == "skid-pad");
            if (pad != null)
            {
                Vector3 c = new Vector3(pad.Centre[0], pad.Centre[1], pad.Centre[2]);
                float r = Mathf.Min(pad.Size[0], pad.Size[1]) * 0.5f - 6f;
                // On the painted ring at its west point, heading north: a right-hand circle (turn the other way for left).
                stations.Add(new Station { Id = "skid-pad", Label = $"Skid pad ({r:0} m ring)", Position = c + Vector3.left * r + Vector3.up * 0.4f, Rotation = Quaternion.LookRotation(Vector3.forward) });
            }
            GridSlot g0 = course.Track.Grid[0];
            stations.Add(new Station { Id = "loop", Label = $"Handling loop ({course.Track.LengthMetres:0} m, slalom, banked bends)", Position = g0.Position, Rotation = g0.Rotation });
        }

        public IReadOnlyList<string> StationLabels => stations.Select(s => s.Label).ToList();
        public string StationId => stations.Count > 0 ? stations[StationIndex].Id : "";

        /// <summary>Stops the current run, then starts A or B at rest on a station start (never a swap at speed).</summary>
        public void ResetAndDrive(bool useB, int stationIndex)
        {
            FinishRun();
            DrivingB = useB;
            StationIndex = Mathf.Clamp(stationIndex, 0, stations.Count - 1);
            TestYardBuild build = useB ? B : A;
            var world = new PhysicsVehicleWorld(Physics.defaultPhysicsScene, GameLayers.DrivableMask, GameLayers.BarrierMask);
            // Same surface rules as a full-size event in these conditions.
            sim = new VehicleSimulation(build.Params, world) { SurfaceGripScale = CourseRuntime.SurfaceGrip(Surface) };
            Station s = stations[StationIndex];
            current = VehicleState.AtRest(s.Position, s.Rotation);
            previous = current;
            if (view != null) Destroy(view.gameObject);
            view = VehicleView.Create($"TestCar_{CarId}_{(useB ? "B" : "A")}", build.Params, lib.Body(CarId), CarMaterials, PaintColor);
            GameAudio.CarAudio.Attach(view, build.Params, CarId, true);
            view.Render(current, current, 1f, default, 0f); // at the start pose this very frame (no frame at the origin)
            chase.SetTarget(view); // a new run is a discontinuity: the camera cuts, keeping the chosen view
            hud?.NotifyDiscontinuity();
            accumulator = 0;
            run = new TestYardRun { B = useB, Station = s.Id, Surface = Surface };
            runSeconds = 0f;
            launchAt = -1f;
            braking = leftStart = false;
            lateralWindow = 0f;
            lateralTicks = 0;
        }

        /// <summary>Dry/wet preset. The surface changes only with an explicit reset (same side, same station).</summary>
        public void SetSurface(string surface)
        {
            Surface = surface == "wet" ? "wet" : "dry";
            ResetAndDrive(DrivingB, StationIndex);
        }

        public void RequestExit()
        {
            FinishRun();
            ExitRequested = true;
        }

        void FinishRun()
        {
            if (run == null || runSeconds < 1f) return;
            run.Seconds = runSeconds;
            if (Lesson != null) LessonRuns.Add(run);
            List<TestYardRun> list = run.B ? RunsB : RunsA;
            list.Add(run);
            if (list.Count > 3) list.RemoveAt(0);
            run = null;
        }

        void Update()
        {
            if (!Ready || ExitRequested) return;
            HandleYardKeys();
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
            if (steps == maxSteps) accumulator = 0;
            view.Render(previous, current, (float)(accumulator / VehicleSimulation.TickDt), sim.Telemetry, Time.deltaTime);
            RenderHud();
        }

        void HandleYardKeys()
        {
            Keyboard k = Keyboard.current;
            Gamepad g = Gamepad.current;
            if ((k != null && k.digit1Key.wasPressedThisFrame) || (g != null && g.dpad.left.wasPressedThisFrame)) ResetAndDrive(false, StationIndex);
            else if (CanDriveB && ((k != null && k.digit2Key.wasPressedThisFrame) || (g != null && g.dpad.right.wasPressedThisFrame))) ResetAndDrive(true, StationIndex);
            // A lesson keeps its lane and its conditions.
            else if (Lesson == null && ((k != null && k.digit3Key.wasPressedThisFrame) || (g != null && g.dpad.up.wasPressedThisFrame))) ResetAndDrive(DrivingB, (StationIndex + 1) % stations.Count);
            else if (Lesson == null && ((k != null && k.digit4Key.wasPressedThisFrame) || (g != null && g.dpad.down.wasPressedThisFrame))) SetSurface(Surface == "dry" ? "wet" : "dry");
            // Leaving is the remappable Pause/menu action (Esc / Start): View/Select is Change View, as in every race.
            else if (controls.PausePressed) RequestExit();
        }

        void Tick()
        {
            DriverInput input = Script != null ? Script(current, runSeconds) : controls.Sample(latchUp, latchDown);
            latchUp = latchDown = false;
            previous = current;
            sim.Step(ref current, input);
            runSeconds += VehicleSimulation.TickDt;
            Measure(input);
            // Hold reset: back to this station's start with the same build (no penalty — nothing is timed officially).
            if (!input.ResetHeld) resetNeedsRelease = false;
            resetHeld = input.ResetHeld && !resetNeedsRelease ? resetHeld + VehicleSimulation.TickDt : 0f;
            overturnedSeconds = RaceSimulation.IsOverturned(current) ? overturnedSeconds + VehicleSimulation.TickDt : 0f;
            if (resetHeld >= RaceSimulation.ResetHoldSeconds)
            {
                resetHeld = overturnedSeconds = 0f;
                resetNeedsRelease = true;
                ResetAndDrive(DrivingB, StationIndex);
            }
        }

        void Measure(DriverInput input)
        {
            if (run == null) return;
            float kmh = current.SpeedKmh;
            run.TopKmh = Mathf.Max(run.TopKmh, kmh);
            // A lesson: the speed crossing its speed gate (the first crossing of this start).
            if (Lesson != null && run.GateKmh < 0f && LessonSpeedGateAlong >= 0f && LaneAlong(previous.Position) < LessonSpeedGateAlong && LaneAlong(current.Position) >= LessonSpeedGateAlong)
                run.GateKmh = kmh;
            // Launch timing from the first movement off the line.
            if (launchAt < 0f && kmh > 1f && run.ZeroTo60 < 0f) launchAt = runSeconds;
            if (launchAt >= 0f && run.ZeroTo60 < 0f && kmh >= 60f) run.ZeroTo60 = runSeconds - launchAt;
            if (launchAt >= 0f && run.ZeroTo100 < 0f && kmh >= 100f) run.ZeroTo100 = runSeconds - launchAt;
            // A stop: firm brake above 30 km/h until at rest.
            float brake = input.Brake;
            if (!braking && brake > 0.3f && kmh > 30f)
            {
                braking = true;
                brakeFrom = current.Position;
                brakeFromKmh = kmh;
            }
            else if (braking && brake < 0.05f && kmh > 5f) braking = false; // released: not a measured stop
            if (braking && kmh < 0.5f)
            {
                braking = false;
                // A lesson counts the first stop of each start only (a start is one attempt).
                if (Lesson == null || float.IsNaN(run.StopAlong))
                {
                    run.StopMetres = Vector3.Distance(Flat(current.Position), Flat(brakeFrom));
                    run.StopFromKmh = brakeFromKmh;
                    run.StopBox = StopBoxResult();
                    if (Lesson != null)
                    {
                        float along = LaneAlong(current.Position);
                        run.StopAlong = along;
                        run.InStopGate = along >= LessonStopFrom && along <= LessonStopTo && Mathf.Abs(LaneLateral(current.Position)) <= 8f;
                    }
                }
            }
            // Peak one-second mean lateral acceleration.
            lateralWindow += Mathf.Abs(sim.Telemetry.LateralG);
            if (++lateralTicks >= VehicleSimulation.TickRate)
            {
                run.PeakLateralG = Mathf.Max(run.PeakLateralG, lateralWindow / lateralTicks);
                lateralWindow = 0f;
                lateralTicks = 0;
            }
            // Loop time: leave the start, then come back through it after most of a lap.
            Station s = stations[StationIndex];
            float fromStart = Vector3.Distance(Flat(current.Position), Flat(s.Position));
            if (s.Id == "loop")
            {
                if (!leftStart && fromStart > 60f) leftStart = true;
                if (leftStart && fromStart < 8f && runSeconds > 20f && run.LoopSeconds < 0f) run.LoopSeconds = runSeconds - Mathf.Max(0f, launchAt);
            }
        }

        string StopBoxResult()
        {
            if (stopBoxes.Count == 0 || laneDir == Vector3.zero) return "";
            float along = Vector3.Dot(current.Position - laneEntry, laneDir);
            float lateral = Vector3.Cross(laneDir, current.Position - laneEntry).y;
            if (Mathf.Abs(lateral) > 8f || along < 0f) return "";
            foreach (var box in stopBoxes)
            {
                if (along >= box.From && along <= box.To) return "stopped IN the box";
                if (along < box.From && box.From - along < 25f) return $"{box.From - along:0.0} m short of the box";
                if (along > box.To && along - box.To < 25f) return $"{along - box.To:0.0} m past the box";
            }
            return "";
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        void BuildOverlay()
        {
            overlay = UIFactory.Root("TestYardOverlay", 50);
            // Bottom-left: the race HUD owns the top corners (time, position, minimap) and the speedometer bottom-right.
            Image bg = UIFactory.Panel("YardPanel", overlay.transform, new Vector2(0, 0), new Vector2(0, 0), Vector2.zero, Vector2.zero, new Color(0.03f, 0.035f, 0.045f, 0.82f));
            var rt = (RectTransform)bg.transform;
            rt.pivot = new Vector2(0, 0);
            rt.sizeDelta = new Vector2(820, 330);
            rt.anchoredPosition = new Vector2(24, 24);
            panel = UIFactory.Label("YardText", rt, "", SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.TopLeft);
            UIFactory.Stretch(panel.rectTransform, 16f);
            panel.richText = true;
            panel.textWrappingMode = TextWrappingModes.Normal;
        }

        void RenderHud()
        {
            if (hud != null)
            {
                hudState.RoadSpeedMps = sim != null ? sim.Telemetry.RoadSpeedMps : 0f;
                hudState.EnvelopeMps = UI.SpeedDisplay.EnvelopeMps((DrivingB ? B : A).Params);
                hudState.Gear = current.Gear;
                hudState.Rpm = current.EngineRpm;
                hudState.Redline = (DrivingB ? B : A).Params.RedlineRpm;
                hudState.RaceSeconds = runSeconds;
                hudState.Position = 1;
                hudState.Entrants = 1;
                hudState.Banner = "";
                hudState.Field.Clear();
                hudState.Field.Add(new HudEntrant { Name = DrivingB ? "B" : "A", Position = current.Position, IsYou = true, Status = "TEST" });
                if (sim != null) DrivingCameraFeed.Feed(chase, speedLines, sim.Telemetry, current, (DrivingB ? B : A).Params, Time.deltaTime);
                // The yard's reset restarts the station (no penalty: nothing here is timed officially).
                var rs = new RecoveryStatus { HoldFraction = resetHeld / RaceSimulation.ResetHoldSeconds, SecondsToAuto = -1f };
                if (overturnedSeconds >= RaceSimulation.OverturnedPromptSeconds) rs.Kind = RecoveryKind.Overturned;
                RaceHud.SetRecovery(hudState, rs, controls != null ? controls.BindingLabel("Reset") : "R", false);
                hud.Render(hudState);
            }
            if (panel == null) return;
            var sb = new System.Text.StringBuilder();
            if (Lesson != null)
            {
                RenderLessonPanel(sb);
                panel.text = sb.ToString();
                return;
            }
            sb.Append("<b>TEST YARD</b>  <size=80%>private practice — no reward, record or purchase; not an online result</size>\n");
            sb.Append(Side("A", A, !DrivingB)).Append('\n').Append(Side("B", B, DrivingB)).Append('\n');
            sb.Append($"<size=85%>{stations[StationIndex].Label}  ·  {Surface.ToUpperInvariant()} (same grip rules as a {Surface} event)</size>\n");
            sb.Append("<size=80%><color=#9A968D>[1] reset & drive A   [2] reset & drive B   [3] next station   [4] dry/wet   [C] change view   [Esc] back to Garage\n" +
                      "Pad: D-pad ← A  → B  ↑ station  ↓ dry/wet  · View = change view · Menu = back. Hold reset to restart this run.</color></size>\n");
            sb.Append($"\n<b>Now</b> ({(DrivingB ? "B" : "A")}): {Esc(run?.Summary() ?? "")}\n");
            AppendRuns(sb, "A", RunsA);
            AppendRuns(sb, "B", RunsB);
            sb.Append("<size=75%><color=#6F6C66>This compact yard cannot prove long high-speed stability, every surface, traffic or latency; the full course is the definitive test.</color></size>");
            panel.text = sb.ToString();
        }

        void RenderLessonPanel(System.Text.StringBuilder sb)
        {
            sb.Append($"<b>DRIVING SCHOOL — BRAKING LANE</b>  <size=80%>{Esc(Lesson.Challenge)} · {Esc(Lesson.Title)} · a lesson, judged when you leave</size>\n");
            sb.Append(Side("A", A, !DrivingB)).Append('\n');
            if (CanDriveB) sb.Append(Side("B", B, DrivingB)).Append('\n');
            float entry = Lesson.Targets.LaneEntryKmh;
            sb.Append($"<size=85%>From rest: past the speed board ({LessonSpeedGateAlong:0} m) at {entry:0} km/h or more, then stop between {LessonStopFrom:0} and {LessonStopTo:0} m" +
                      (Lesson.Rules.BoxStops ? $"; {Lesson.LaneStarts} starts" : "") + (Lesson.Rules.CompareStops ? "; once on A and once on B" : "") + ".</size>\n");
            sb.Append("<size=80%><color=#9A968D>[1] new start on A" + (CanDriveB ? "   [2] new start on B" : "") + "   [C] change view   [Esc] finish the lesson · hold reset to restart this start</color></size>\n");
            sb.Append($"\n<b>Now</b> ({(DrivingB ? "B" : "A")}): {Esc(LessonLine(run))}\n");
            int counted = 0;
            for (int i = 0; i < LessonRuns.Count; i++)
            {
                TestYardRun r = LessonRuns[i];
                bool ok = r.GateKmh >= entry && r.InStopGate;
                if (ok) counted++;
                sb.Append($"<size=85%>{i + 1}. {(r.B ? "B" : "A")}  {Esc(LessonLine(r))}{(ok ? "  <color=#3EC6D8>counts</color>" : "")}</size>\n");
            }
            // The start being driven counts as soon as it has stopped (it joins the list when the next start begins).
            var all = new List<TestYardRun>(LessonRuns);
            if (run != null && !float.IsNaN(run.StopAlong)) all.Add(run);
            if (run != null && !float.IsNaN(run.StopAlong) && run.GateKmh >= entry && run.InStopGate) counted++;
            if (Lesson.Rules.CompareStops)
            {
                TestYardRun a = all.LastOrDefault(r => !r.B && r.GateKmh >= entry && r.InStopGate), b = all.LastOrDefault(r => r.B && r.GateKmh >= entry && r.InStopGate);
                if (a != null && b != null)
                    sb.Append($"<b>Measured difference:</b> A {a.StopMetres:0.0} m, B {b.StopMetres:0.0} m — {(b.StopMetres <= a.StopMetres ? "the loaned tyres stop" : "the loaned tyres need")} {Mathf.Abs(b.StopMetres - a.StopMetres):0.0} m {(b.StopMetres <= a.StopMetres ? "shorter" : "more")}.\n");
            }
            else if (Lesson.Rules.BoxStops) sb.Append($"<b>{counted} of {Lesson.LaneStarts}</b> starts counted.\n");
        }

        string LessonLine(TestYardRun r)
        {
            if (r == null) return "";
            string gate = r.GateKmh >= 0f ? $"{r.GateKmh:0} km/h at the speed board" : "speed board not reached";
            string stop = float.IsNaN(r.StopAlong) ? "no stop yet" : r.InStopGate ? $"stopped IN ({r.StopMetres:0.0} m)" : $"stopped at {r.StopAlong:0} m";
            return gate + " · " + stop;
        }

        static string Side(string tag, TestYardBuild b, bool driving) =>
            $"{(driving ? "<color=#E5484D>></color>" : "  ")} <b>{tag}</b>  {Esc(b.Label)}  <size=80%>PI {b.Pi} · {Short(b.BuildHash)}</size>";

        static void AppendRuns(System.Text.StringBuilder sb, string tag, List<TestYardRun> runs)
        {
            for (int i = runs.Count - 1; i >= 0; i--)
                sb.Append($"<size=85%>{tag}{runs.Count - i}  <color=#9A968D>{runs[i].Station} · {runs[i].Surface}</color>  {Esc(runs[i].Summary())}</size>\n");
        }

        static string Short(string hash) => string.IsNullOrEmpty(hash) ? "stock" : hash.Substring(0, Math.Min(10, hash.Length));

        static string Esc(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");

        void OnDestroy()
        {
            controls?.Dispose();
            if (hud != null) Destroy(hud.gameObject);
            if (speedLines != null) Destroy(speedLines.gameObject);
            if (overlay != null) Destroy(overlay.gameObject);
            if (view != null) Destroy(view.gameObject);
        }
    }
}
