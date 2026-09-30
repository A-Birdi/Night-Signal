using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.AI;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Track;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>One car in a <see cref="RaceSimulation"/>: roster row, simulation state and progress.</summary>
    public sealed class RaceEntrant
    {
        public RosterEntry Roster;
        public bool Human => Roster.Human;
        public EntrantStatus Status;
        public VehicleParams Params;
        public VehicleSimulation Sim;
        public VehicleState State;
        public EntrantProgress Progress;
        public RouteFollower Ai;
        public float ResetHeld;
        /// <summary>After a reset the button must be released before another can start (holding it never repeats resets).</summary>
        public bool ResetNeedsRelease;
        /// <summary>Reset safety ghost: non-colliding until this tick AND clear of overlaps (bounded); -1 when solid.</summary>
        public int GhostUntilTick = -1;

        /// <summary>Automatic marshal recoveries (counted as resets, with the reset penalty).</summary>
        public int AutoRecoveries;
        /// <summary>Continuous seconds this car has been effectively stationary while racing (AI: rescued; human: prompted).</summary>
        public float StuckSeconds;
        /// <summary>Continuous seconds on its side or roof and nearly stopped.</summary>
        public float OverturnedSeconds;
        /// <summary>Core drift scoring for this car (every event; only drift formats rank by it).</summary>
        public readonly DriftScorer Drift = new DriftScorer();
        public int DriftWallsSeen, DriftSector = -1;
        /// <summary>How the last chain ended (banked or lost) — for the HUD.</summary>
        public ChainEnd LastChainEnd;
        /// <summary>Four Signals measurements (S29's course; null elsewhere).</summary>
        public ContractRun ContractRun;
        /// <summary>Challenge-gate facts (courses with tagged apex, precision or lane gates; null elsewhere).</summary>
        public GateRun GateRun;
        /// <summary>Racecraft facts: clean passes and follows (races with live opponents; null elsewhere).</summary>
        public RacecraftRun Racecraft;
        /// <summary>Challenge-zone chains and holds (courses with tagged transition, clip or demonstration zones; null elsewhere).</summary>
        public ZoneChainRun ZoneChains;
        public bool Collides => Status == EntrantStatus.Racing || Status == EntrantStatus.Finished;
    }

    /// <summary>Frozen event parameters the simulation needs (no networking, no persistence).</summary>
    [Serializable]
    public sealed class RaceEventRules
    {
        /// <summary>campaign | freeplay | trial</summary>
        public string Kind = "freeplay";
        public CampaignMode Mode = CampaignMode.Normal;
        public string StageId;
        public int StageNumber = 1;
        public int CarCapPi;
        public ContactPolicy Contact = ContactPolicy.LightContact;
        public long BenchmarkTargetMs;
        public long HardTimeoutMs;
        /// <summary>Lieutenant/penultimate/finale: a qualifying human must beat the featured live rival.</summary>
        public bool RequiresBeatingFeaturedRival;
        /// <summary>
        /// Featured rival's driving pace (speed-plan scale). 0 = from the certified benchmark in the content (1 when the stage
        /// side has none); the certification run sets it while calibrating.
        /// </summary>
        public float FeaturedRivalPace;
        /// <summary>
        /// Benchmark certification only: allow live AI in a non-contact event (every car ghosted) so a rival's solo time can
        /// be measured beside the reference car. No player-facing event sets it.
        /// </summary>
        public bool CalibrationGhosts;
        /// <summary>
        /// Benchmark and telemetry runs only: every rival drives this car (not their own), so their behaviour is compared on
        /// equal machinery (spec §13 "measured on shared benchmark corners"). Null in every player-facing event.
        /// </summary>
        public string CalibrationCarId;
        /// <summary>Benchmark certification only: measure the Four Signals on their course outside a campaign event.</summary>
        public bool MeasureContracts;
        /// <summary>dry | damp | wet — one grip rule for races and the Garage Test Yard (CourseRuntime.SurfaceGrip).</summary>
        public string Surface = "dry";
        /// <summary>Drift Attack (freeplay, or a drift Team Trial): finishers rank by banked raw drift score, not time.</summary>
        public bool DriftRanking;
        /// <summary>
        /// A racecraft challenge trial's fixed field: the opposing AI, in order, are these cars (stock, not chosen by the cap),
        /// identities, roles and paces; null for every other event.
        /// </summary>
        public List<TrialFieldCar> TrialField;
        /// <summary>The humans start from the back of the grid, behind every AI car (CH41 "from the last grid position").</summary>
        public bool HumansStartLast;
    }

    public sealed class HumanSlot
    {
        public string EntrantId;
        public string DisplayName;
        public string CarId;
        /// <summary>The frozen applied build this human races (parts resolved by Core); null = the model's stock car.</summary>
        public Core.Builds.ResolvedCarSpec Spec;
        /// <summary>The snapshot <see cref="Spec"/> was resolved from (sent to clients so they predict the same car).</summary>
        public Core.Builds.MechanicalSnapshot Build;
        /// <summary>The applied livery (compact wire form) the other drivers see; null/"" = the palette colour.</summary>
        public string Livery;
    }

    public sealed class RaceEntrantResult
    {
        public RaceEntrant Entrant;
        public RunOutcome Outcome;
        public int Placement;
        public bool Tied;
        public long FinishTimeMicros;
        public double CheckpointFraction;
        public bool ActiveProgressVerified;
        public bool ActivelyDroveLegalCourse;
        public float LegalProgressMetres;
        /// <summary>S29 Four Signals: contracts passed (−1 when the event has none) and what failed.</summary>
        public int ContractsPassed = -1;
        public string ContractDetail = "";
        /// <summary>Finished ahead of the featured live rival, or the rival legally failed to finish (a tie does not beat).</summary>
        public bool BeatFeaturedRival;
        /// <summary>Banked raw drift score (whole points; every event reports it, drift formats rank by it).</summary>
        public long RawDriftScore;
    }

    /// <summary>
    /// The authoritative race loop shared by the dedicated server and offline play (Addendum 01 §8.2: one racing engine,
    /// different persistence). Fixed 60 Hz: every racing car steps with its human or AI input, progress is tracked, light
    /// contact is resolved in entrant order (Time Attack excepted), the finish window opens at the first human finish and
    /// the event completes when every entrant is done or the deadline passes. Owners supply human inputs and read the
    /// classification; they never move cars themselves.
    /// </summary>
    public sealed class RaceSimulation
    {
        public readonly TrackData Track;
        public readonly RaceProgressTracker Tracker;
        public readonly RaceEventRules Rules;
        public readonly DriftJudge Drift;
        public readonly List<RaceEntrant> Entrants = new List<RaceEntrant>();
        /// <summary>
        /// Record ruleset versions (personal records only compare within one version). Bump PhysicsVersion whenever the
        /// chassis, contact or barrier response changes; ScoringVersion when classification or timing changes.
        /// </summary>
        public const string PhysicsVersion = "chassis-2026.09-contact1";
        /// <remarks>classify-2 (Addendum 03): finite directional 3D gates, legal-progress ranking, route/layer/overturned recovery.</remarks>
        public const string ScoringVersion = "classify-2";

        public int StartTick = int.MaxValue;
        public long FirstHumanFinishMicros = -1;
        public long DeadlineMicros = long.MaxValue;
        public bool Complete { get; private set; }
        /// <summary>Supplies a human entrant's command for a tick (network buffer online, local controls offline).</summary>
        public Func<RaceEntrant, int, DriverInput> HumanInput;
        /// <summary>Raised when the finish window opens (first valid human finish).</summary>
        public event Action DeadlineSet;
        readonly List<VehicleState> traffic = new List<VehicleState>(Limits.MaxRaceVehicles);

        public RaceSimulation(TrackData track, RaceEventRules rules)
        {
            Track = track;
            Rules = rules;
            Tracker = new RaceProgressTracker(track);
            Drift = new DriftJudge(track);
        }

        public long RaceMicros(int tick) => (long)(tick - StartTick) * 1_000_000L / VehicleSimulation.TickRate;

        /// <summary>
        /// Builds the frozen roster: humans first, then AI in the given order (featured rival first for campaign stages).
        /// Refuses rosters outside Addendum 01 D01 or larger than the course grid; finale-only rivals are rejected here too.
        /// </summary>
        public static RaceSimulation Build(TrackData track, ContentLibrary lib, RaceEventRules rules, IReadOnlyList<HumanSlot> humans,
            IReadOnlyList<string> opposingAi, IVehicleWorld world, IReadOnlyList<string> friendlyAi = null)
        {
            friendlyAi = friendlyAi ?? Array.Empty<string>();
            RosterPlanner.ValidateCounts(humans.Count, friendlyAi.Count, opposingAi.Count);
            int vehicles = humans.Count + friendlyAi.Count + opposingAi.Count;
            if (vehicles > track.Grid.Length)
                throw new InvalidOperationException($"the course has {track.Grid.Length} grid slots for {vehicles} vehicles");
            if (rules.Contact == ContactPolicy.NonContact && friendlyAi.Count + opposingAi.Count > 0 && !rules.CalibrationGhosts)
                throw new InvalidOperationException("Time Attack (non-contact) has no live AI");

            var sim = new RaceSimulation(track, rules);
            ContentCatalogue cat = lib.Catalogue;
            // Before the AI are placed: an S29 field drifts the Arc like the humans must.
            sim.Contracts = ContractJudge.ForEvent(track, cat, rules.Kind, rules.StageId, rules.Mode, rules.MeasureContracts);
            sim.Gates = GateJudge.ForTrack(track);
            sim.ZoneChains = ZoneChainJudge.ForTrack(track);
            sim.gateWorld = world;
            sim.Racecraft = RacecraftJudge.ForEvent(rules, sim.Entrants, track);
            int slot = 0, generic = 0;
            // Entrant order stays humans first (the local player is entrant 0); only where each car stands on the grid moves.
            int Grid(int order) => !rules.HumansStartLast ? order : order < humans.Count ? vehicles - humans.Count + order : order - humans.Count;
            foreach (HumanSlot h in humans)
            {
                sim.Add(lib, world, slot, h.EntrantId, h.DisplayName, true, h.CarId, "player", "driver", null, h.Spec, h.Build, h.Livery, Grid(slot));
                slot++;
            }
            foreach (string id in friendlyAi)
            {
                sim.AddAi(cat, lib, world, slot, id, "player", "friendly", AiPlacementContext.FriendlyAi, ref generic, Grid(slot));
                slot++;
            }
            for (int i = 0; i < opposingAi.Count; i++)
            {
                AiPlacementContext ctx = rules.Kind == "campaign" ? AiPlacementContext.CampaignEncounter
                    : rules.Kind == "trial" ? AiPlacementContext.TeamTrial : AiPlacementContext.FreeplayOpponent;
                string role = rules.Kind == "campaign" ? (i == 0 ? "featured" : "support") : "opponent";
                if (rules.TrialField != null && i < rules.TrialField.Count)
                    sim.AddFieldCar(cat, lib, world, slot, opposingAi[i], rules.TrialField[i], ref generic, Grid(slot));
                else
                    sim.AddAi(cat, lib, world, slot, opposingAi[i], "opposing", role, ctx, ref generic, Grid(slot));
                slot++;
            }
            return sim;
        }

        /// <summary>The certified (or calibrating) pace for the featured rival of a campaign stage; 1 for everyone else.</summary>
        float FeaturedPace(ContentCatalogue cat, string role)
        {
            if (role != "featured" || string.IsNullOrEmpty(Rules.StageId)) return 1f;
            if (Rules.FeaturedRivalPace > 0f) return Rules.FeaturedRivalPace;
            return cat.TryCertifiedBenchmark(Rules.StageId, Rules.Mode, out CertifiedBenchmark b) ? (float)b.FeaturedRivalPace : 1f;
        }

        /// <summary>How far behind (s) a pressure car keeps beyond a car length — inside CH39's 1 s, clear of a hard-braking car ahead.</summary>
        public const float PressureGapSeconds = 0.25f;

        /// <summary>A racecraft trial's scripted car: its own stock car, identity, role and pace (never chosen by the cap).</summary>
        void AddFieldCar(ContentCatalogue cat, ContentLibrary lib, IVehicleWorld world, int slot, string id, TrialFieldCar fc, ref int generic, int grid)
        {
            DriverProfile profile;
            string name;
            if (!string.IsNullOrEmpty(fc.Rival) && cat.TryRival(fc.Rival, out RivalDef rival))
            {
                FinalRivals.Require(rival.Id, AiPlacementContext.FreeplayOpponent, Rules.StageId, Rules.Mode);
                profile = AiProfiles.For(rival, Rules.StageNumber);
                name = rival.Name;
                id = rival.Id;
            }
            else
            {
                profile = AiProfiles.Generic(generic++);
                name = $"Driver {id.ToUpperInvariant()}";
            }
            if (fc.Pace > 0f) profile.PaceScale = fc.Pace;
            RaceEntrant e = Add(lib, world, slot, id, name, false, fc.Car, "opposing", string.IsNullOrEmpty(fc.Role) ? "field" : fc.Role, null, grid: grid);
            e.Ai = new RouteFollower(Track, e.Params, profile) { DriftZones = DriftZonesForAi, SurfaceGrip = CourseRuntime.SurfaceGrip(Rules.Surface), Seed = slot };
            // A pressure car closes up and follows, never passing (CH39).
            if (fc.Role == "pressure")
            {
                e.Ai.NoPassing = e.Ai.FollowAnyLane = true;
                e.Ai.FollowGapSeconds = PressureGapSeconds;
            }
            // A pacing rival keeps to the far side of each marked lane (CH36's outside lane gate), so the lane stays open for a pass.
            if (fc.Role == "pacing")
                e.Ai.LineZones = Track.Gates.Where(g => g.Kind == "lane" && !string.IsNullOrEmpty(g.Challenge))
                    .Select(g => new RouteGateDef { Id = g.Id, Kind = g.Kind, Challenge = g.Challenge, StartMetres = g.StartMetres, EndMetres = g.EndMetres,
                        LineOffset = -g.LineOffset, LineTolerance = g.LineTolerance })
                    .ToList();
        }

        void AddAi(ContentCatalogue cat, ContentLibrary lib, IVehicleWorld world, int slot, string id, string team, string role, AiPlacementContext ctx, ref int generic,
            int grid = -1)
        {
            RaceEntrant e;
            if (cat.TryRival(id, out RivalDef rival))
            {
                FinalRivals.Require(rival.Id, ctx, Rules.StageId, Rules.Mode);
                e = Add(lib, world, slot, rival.Id, rival.Name, false, Rules.CalibrationCarId ?? LegalCarFor(cat, rival.PrimaryCar, Rules.CarCapPi), team, role, null, grid: grid);
                e.Ai = new RouteFollower(Track, e.Params, AiProfiles.For(rival, Rules.StageNumber, FeaturedPace(cat, role))) { DriftZones = DriftZonesForAi, SurfaceGrip = CourseRuntime.SurfaceGrip(Rules.Surface), Seed = slot };
            }
            else
            {
                // Freeplay opponents without a rival identity: a legal car under the cap and a neutral profile.
                CarDef car = cat.Cars.Where(c => Rules.CarCapPi <= 0 || c.BasePI <= Rules.CarCapPi)
                    .OrderByDescending(c => c.BasePI).Skip(generic % 3).FirstOrDefault() ?? cat.Cars.OrderBy(c => c.BasePI).First();
                e = Add(lib, world, slot, id, $"Driver {id.ToUpperInvariant()}", false, car.Id, team, role, null, grid: grid);
                e.Ai = new RouteFollower(Track, e.Params, AiProfiles.Generic(generic++)) { DriftZones = DriftZonesForAi, SurfaceGrip = CourseRuntime.SurfaceGrip(Rules.Surface), Seed = slot };
            }
        }

        RaceEntrant Add(ContentLibrary lib, IVehicleWorld world, int slot, string id, string name, bool human, string carId, string team, string role, float[] paint,
            Core.Builds.ResolvedCarSpec spec = null, Core.Builds.MechanicalSnapshot build = null, string livery = null, int grid = -1)
        {
            if (spec != null && spec.CarModelId != carId) throw new InvalidOperationException($"build for {spec.CarModelId} used on {carId}");
            VehicleParams p = spec != null ? VehicleFactory.Build(spec, AssistSettings.Default, lib.Body(carId).WheelRadius) : lib.Params(carId, AssistSettings.Default);
            if (grid < 0) grid = slot;
            GridSlot g = Track.Grid[grid];
            var e = new RaceEntrant
            {
                Params = p,
                Sim = new VehicleSimulation(p, world) { SurfaceGripScale = CourseRuntime.SurfaceGrip(Rules.Surface) },
                State = VehicleState.AtRest(g.Position, g.Rotation),
                Progress = new EntrantProgress(Track) { TyreHalfSpan = p.TrackM * 0.5f + 0.1f },
                Status = human ? EntrantStatus.Reserved : EntrantStatus.Loaded,
                Roster = new RosterEntry
                {
                    Index = slot, EntrantId = id, DisplayName = name, Human = human, CarId = carId, GridSlot = grid,
                    Paint = paint ?? Palette(slot, human, team), Team = team, Role = role,
                    Build = spec != null ? build : null, BuildHash = spec?.BuildHash ?? "", Livery = livery ?? "",
                },
            };
            Tracker.Start(e.Progress, e.State.Position);
            Entrants.Add(e);
            return e;
        }

        /// <summary>A rival whose primary car exceeds the event cap uses a declared legal alternate (spec App. B).</summary>
        public static string LegalCarFor(ContentCatalogue cat, string primary, int capPi)
        {
            CarDef p = cat.Car(primary);
            if (capPi <= 0 || p.BasePI <= capPi) return primary;
            CarDef alt = cat.Cars.Where(c => c.BasePI <= capPi && c.Drive == p.Drive).OrderByDescending(c => c.BasePI).FirstOrDefault()
                         ?? cat.Cars.Where(c => c.BasePI <= capPi).OrderByDescending(c => c.BasePI).First();
            return alt.Id;
        }

        static float[] Palette(int slot, bool human, string team)
        {
            Color[] humans = { new Color(0.84f, 0.12f, 0.12f), new Color(0.12f, 0.45f, 0.85f), new Color(0.95f, 0.72f, 0.12f), new Color(0.15f, 0.6f, 0.35f), new Color(0.92f, 0.92f, 0.9f), new Color(0.55f, 0.25f, 0.75f) };
            Color c = human ? humans[slot % humans.Length]
                : team == "player" ? Color.Lerp(humans[slot % humans.Length], Color.white, 0.35f)
                : Color.Lerp(humans[(slot + 3) % humans.Length], Color.gray, 0.45f);
            return new[] { c.r, c.g, c.b };
        }

        // ------------------------------------------------------------------ racing

        /// <summary>Advances one racing tick (tick ≥ StartTick). No-op once complete.</summary>
        public void Tick(int tick)
        {
            if (Complete) return;
            long raceMicros = RaceMicros(tick);
            foreach (RaceEntrant e in Entrants)
            {
                if (e.Status == EntrantStatus.Loaded) e.Status = EntrantStatus.Racing;
                if (e.Status != EntrantStatus.Racing) continue;
                DriverInput input = e.Human ? (HumanInput != null ? HumanInput(e, tick) : DriverInput.Neutral) : e.Ai.Drive(e.State, TrafficFor(e));
                VehicleState prev = e.State;
                e.Sim.Step(ref e.State, input);
                if (input.Handbrake && !e.Progress.Finished) e.Progress.HandbrakeSeconds += VehicleSimulation.TickDt;
                Tracker.Step(e.Progress, prev, e.State, e.Sim.Telemetry, raceMicros, VehicleSimulation.TickDt);
                e.OverturnedSeconds = IsOverturned(e.State) ? e.OverturnedSeconds + VehicleSimulation.TickDt : 0f;
                e.StuckSeconds = e.State.Velocity.sqrMagnitude < 1f ? e.StuckSeconds + VehicleSimulation.TickDt : 0f;

                if (!input.ResetHeld) e.ResetNeedsRelease = false;
                e.ResetHeld = input.ResetHeld && !e.ResetNeedsRelease ? e.ResetHeld + VehicleSimulation.TickDt : 0f;
                bool reset = false;
                if (e.ResetHeld >= ResetHoldSeconds && !e.Progress.Finished)
                {
                    e.State = Tracker.ResetPose(e.Progress, e.Params, raceMicros, "manual", p => Occupied(e, p));
                    e.ResetHeld = 0f;
                    e.ResetNeedsRelease = true;
                    e.GhostUntilTick = tick + GhostWindowTicks;
                    reset = true;
                }
                else if (!e.Progress.Finished && (e.Progress.OffRouteSeconds >= AutoRescueSeconds || e.OverturnedSeconds >= OverturnedRescueSeconds || AiStuck(e)))
                {
                    // Marshal recovery: a car clearly off the legal route (fell from the road, landed on another stretch,
                    // left the corridor far behind), lying on its side or roof, or an AI wedged in place. Same anchor rules,
                    // penalty and protection as a player reset; judged by the route, its road layer and the car's own
                    // orientation, never by one world height.
                    string why = e.Progress.OffRouteSeconds >= AutoRescueSeconds ? "off-route" : e.OverturnedSeconds >= OverturnedRescueSeconds ? "overturned" : "stuck";
                    e.State = Tracker.ResetPose(e.Progress, e.Params, raceMicros, why, p => Occupied(e, p));
                    e.GhostUntilTick = tick + GhostWindowTicks;
                    e.AutoRecoveries++;
                    reset = true;
                }
                if (reset) e.StuckSeconds = e.OverturnedSeconds = 0f;
                Drift.Step(e, reset, e.Progress.Finished);
                Contracts?.Step(e, input, raceMicros, reset);
                Gates?.Step(e, input, reset, gateWorld);
                ZoneChains?.Step(e, reset);
                Racecraft?.Step(e, reset, raceMicros);
                if (e.Progress.Finished)
                {
                    e.Status = EntrantStatus.Finished;
                    if (e.Human && FirstHumanFinishMicros < 0)
                    {
                        FirstHumanFinishMicros = e.Progress.FinishTimeMicros;
                        DeadlineMicros = ComputeDeadline(FirstHumanFinishMicros);
                        DeadlineSet?.Invoke();
                    }
                }
            }
            ResolveContacts(tick, raceMicros);
            Racecraft?.Judge(raceMicros);

            // Finish early only when every remaining entrant (AI included) is done; otherwise run to the deadline. If no
            // human is left racing and none finished, nobody can be rewarded, so settle now.
            bool anyActive = Entrants.Any(e => e.Status == EntrantStatus.Racing);
            bool anyHumanActive = Entrants.Any(e => e.Human && e.Status == EntrantStatus.Racing);
            bool noHumanCanFinish = !anyHumanActive && FirstHumanFinishMicros < 0;
            if (!anyActive || noHumanCanFinish || raceMicros >= DeadlineMicros || raceMicros > 15L * 60 * 1_000_000)
                Finish();
        }

        /// <summary>Marks every still-racing entrant DNF and completes the event.</summary>
        public void Finish()
        {
            if (Complete) return;
            foreach (RaceEntrant e in Entrants.Where(x => x.Status == EntrantStatus.Racing))
                e.Status = EntrantStatus.Dnf;
            Complete = true;
        }

        /// <summary>In drift formats the AI drifts the judged zones like the humans must (null otherwise: race the line).</summary>
        public IReadOnlyList<RouteGateDef> DriftZonesForAi => (Rules.DriftRanking || Contracts != null) && Drift.Zones.Count > 0 ? Drift.Zones : null;

        /// <summary>S29 Four Signals judging on a course with the four contract sectors (null for other events).</summary>
        public ContractJudge Contracts { get; private set; }
        /// <summary>Challenge-gate judging (CH03, CH06, CH09…) where the course tags such gates.</summary>
        public GateJudge Gates { get; private set; }
        /// <summary>Challenge-zone chain judging (CH17, CH19, CH22, CH27) where the course tags such zones.</summary>
        public ZoneChainJudge ZoneChains { get; private set; }
        IVehicleWorld gateWorld;
        /// <summary>Racecraft judging (CH31, CH32) in races with live opponents; null in Time Attack and Drift Attack.</summary>
        public RacecraftJudge Racecraft { get; private set; }

        static int GhostWindowTicks => Limits.ResetGhostMaxMs * VehicleSimulation.TickRate / 1000;
        /// <summary>Hold-to-reset duration (Addendum 03 §7.1: about 0.75 s, cancelled on release).</summary>
        public const float ResetHoldSeconds = 0.75f;
        /// <summary>Seconds clearly off the legal route before the marshal recovers the car (Addendum 03 §7.1: 2–4 s).</summary>
        public const float AutoRescueSeconds = 2.5f;
        /// <summary>On its side or roof and nearly stopped: prompt after this long, recover automatically after the next.</summary>
        public const float OverturnedPromptSeconds = 0.75f, OverturnedRescueSeconds = 3f;
        /// <summary>A human car stopped this long while racing is offered the reset (never taken from them automatically).</summary>
        public const float StuckPromptSeconds = 3f;

        /// <summary>Lying on its side or roof (body up axis within ~72° of down or level) and slower than 4 m/s.</summary>
        public static bool IsOverturned(in VehicleState s) => (s.Rotation * Vector3.up).y < 0.3f && s.Velocity.sqrMagnitude < 16f;

        /// <summary>
        /// What the HUD should offer this car now (Addendum 03 §7.1): the reset hold in progress, an automatic recovery
        /// counting down (off the route, overturned), or a plain offer (stopped). Presentation of simulation state only.
        /// </summary>
        public RecoveryStatus Recovery(RaceEntrant e)
        {
            var r = new RecoveryStatus { HoldFraction = Mathf.Clamp01(e.ResetHeld / ResetHoldSeconds), SecondsToAuto = -1f };
            if (e.Status != EntrantStatus.Racing || e.Progress.Finished) return r;
            if (e.Progress.OffRouteSeconds > 0.5f) { r.Kind = RecoveryKind.OffRoute; r.SecondsToAuto = Mathf.Max(0f, AutoRescueSeconds - e.Progress.OffRouteSeconds); }
            else if (e.OverturnedSeconds >= OverturnedPromptSeconds) { r.Kind = RecoveryKind.Overturned; r.SecondsToAuto = Mathf.Max(0f, OverturnedRescueSeconds - e.OverturnedSeconds); }
            else if (e.Human && e.StuckSeconds >= StuckPromptSeconds) r.Kind = RecoveryKind.Stopped;
            return r;
        }

        /// <summary>A recovery anchor is occupied when another car (colliding or not) sits within a car length and a half of it.</summary>
        bool Occupied(RaceEntrant self, Vector3 anchor)
        {
            foreach (RaceEntrant o in Entrants)
                if (o != self && o.Status != EntrantStatus.DqDisconnected && (o.State.Position - anchor).sqrMagnitude < Mathf.Pow(self.Params.LengthM * 1.5f, 2f))
                    return true;
            return false;
        }

        /// <summary>
        /// AI only: stationary (under 1 m/s) for 4 s while racing — wedged against a barrier or another car. Humans keep
        /// hold-to-reset; the server never takes a human's car away from them for being slow.
        /// </summary>
        static bool AiStuck(RaceEntrant e) => !e.Human && e.StuckSeconds > 4f;

        /// <summary>Cars an AI can touch (so it follows and passes them); none in non-contact events.</summary>
        public List<VehicleState> TrafficFor(RaceEntrant self)
        {
            traffic.Clear();
            if (Rules.Contact == ContactPolicy.NonContact) return traffic;
            foreach (RaceEntrant o in Entrants)
                if (o != self && o.Collides && o.GhostUntilTick < 0) traffic.Add(o.State);
            return traffic;
        }

        /// <summary>
        /// Light contact (Addendum 01 §2.1): every colliding pair in entrant order after all cars stepped. DQ/DNF cars and
        /// reset safety ghosts never collide. A safety ghost lasts at most its 2 s window (Addendum 03 D306): if the car
        /// still overlaps another then, it is moved to a free anchor at or behind its recovery point (the same recovery —
        /// no second penalty) rather than ghosting on.
        /// </summary>
        void ResolveContacts(int tick, long raceMicros)
        {
            if (Rules.Contact == ContactPolicy.NonContact) return;
            foreach (RaceEntrant e in Entrants)
                if (e.GhostUntilTick >= 0 && tick >= e.GhostUntilTick)
                {
                    bool overlapping = Entrants.Any(o => o != e && o.Collides && VehicleContact.Overlapping(e.State, e.Params, o.State, o.Params));
                    if (overlapping) e.State = Tracker.AnchorPose(e.Progress, e.Params, p => Occupied(e, p), out float _);
                    e.GhostUntilTick = -1;
                }
            for (int i = 0; i < Entrants.Count; i++)
            {
                RaceEntrant a = Entrants[i];
                if (!a.Collides || a.GhostUntilTick >= 0) continue;
                for (int j = i + 1; j < Entrants.Count; j++)
                {
                    RaceEntrant b = Entrants[j];
                    if (!b.Collides || b.GhostUntilTick >= 0) continue;
                    Vector3 fromA = a.State.Position, fromB = b.State.Position;
                    ContactResult c = VehicleContact.Resolve(ref a.State, a.Params, ref b.State, b.Params);
                    if (!c.Touching) continue;
                    Racecraft?.Touch(a, b, raceMicros);
                    // A nudge must never carry a car through a guardrail: re-run the barrier pass for both cars.
                    a.Sim.ConstrainToBarriers(ref a.State, fromA);
                    b.Sim.ConstrainToBarriers(ref b.State, fromB);
                    if (c.DeltaV >= VehicleContact.IncidentDeltaV)
                    {
                        NoteContact(a.Progress, raceMicros);
                        NoteContact(b.Progress, raceMicros);
                    }
                }
            }
        }

        static void NoteContact(EntrantProgress p, long raceMicros)
        {
            double now = raceMicros / 1e6;
            if (now - p.LastVehicleContactTime < Limits.WallImpactDebounceMs / 1000.0) return; // one incident per bump, not per tick
            p.VehicleContacts++;
            p.LastVehicleContactTime = now;
        }

        long ComputeDeadline(long firstFinish)
        {
            if (Rules.Kind == "campaign" && Rules.BenchmarkTargetMs > 0)
            {
                long envelope = StageOutcome.SupportEnvelopeMs(Rules.BenchmarkTargetMs, Rules.Mode);
                long hard = Math.Max(envelope, Rules.HardTimeoutMs);
                return StageOutcome.DeadlineMs(firstFinish / 1000, envelope, hard) * 1000L;
            }
            return firstFinish + Limits.FirstFinishGraceMs * 1000L;
        }

        // ------------------------------------------------------------------ classification

        public static RunOutcome OutcomeOf(RaceEntrant e)
        {
            switch (e.Status)
            {
                case EntrantStatus.Finished: return RunOutcome.Finished;
                case EntrantStatus.Dnf: return RunOutcome.DidNotFinish;
                case EntrantStatus.DqQuit: return RunOutcome.Quit;
                default: return RunOutcome.DisqualifiedDisconnect;
            }
        }

        /// <summary>Core classification of every entrant, plus the live-rival condition for featured encounters.</summary>
        public List<RaceEntrantResult> Classify()
        {
            int totalCps = Tracker.TotalCheckpoints;
            var finishes = Entrants.Select(e => new EntrantFinish
            {
                EntrantId = e.Roster.EntrantId,
                Outcome = OutcomeOf(e),
                FinishTimeMicros = e.Progress.FinishTimeMicros,
                LegalProgressMetres = e.Progress.RaceDistance - Track.StartMetres,
            }).ToList();
            Dictionary<string, Placing> placings = Rules.DriftRanking
                ? DriftPlacings(finishes, Entrants.ToDictionary(e => e.Roster.EntrantId, DriftJudge.Reported))
                : RaceClassification.Classify(finishes).ToDictionary(p => p.EntrantId);
            RaceEntrant rival = Entrants.FirstOrDefault(e => e.Roster.Role == "featured");
            bool rivalFinished = rival != null && OutcomeOf(rival) == RunOutcome.Finished;
            var results = new List<RaceEntrantResult>();
            foreach (RaceEntrant e in Entrants)
            {
                RunOutcome outcome = OutcomeOf(e);
                bool finished = outcome == RunOutcome.Finished;
                results.Add(new RaceEntrantResult
                {
                    Entrant = e,
                    Outcome = outcome,
                    Placement = placings[e.Roster.EntrantId].Place,
                    Tied = placings[e.Roster.EntrantId].Tied,
                    FinishTimeMicros = finished ? e.Progress.FinishTimeMicros : 0,
                    CheckpointFraction = totalCps > 0 ? (double)e.Progress.CheckpointsPassed / totalCps : 0,
                    ActiveProgressVerified = e.Progress.RaceDistance - Track.StartMetres > 200f,
                    ActivelyDroveLegalCourse = finished && !e.Progress.CorridorCut && e.Progress.OutOfCorridorSeconds < 5f,
                    LegalProgressMetres = Math.Max(0, e.Progress.RaceDistance - Track.StartMetres),
                    BeatFeaturedRival = e.Human && finished && rival != null && (!rivalFinished || e.Progress.FinishTimeMicros < rival.Progress.FinishTimeMicros),
                    RawDriftScore = DriftJudge.Reported(e),
                });
                if (Contracts != null && e.Human)
                {
                    ContractVerdict v = Contracts.Evaluate(e.ContractRun, finished, e.Progress.FinishTimeMicros / 1000, Rules.BenchmarkTargetMs);
                    results[results.Count - 1].ContractsPassed = v.Passed;
                    results[results.Count - 1].ContractDetail = v.Detail + (Contracts.Provisional ? " (provisional targets)" : "");
                }
            }
            return results;
        }

        /// <summary>
        /// Drift Attack placings, exactly as the control plane recomputes them: finishers by banked raw score (equal scores
        /// share a place), then non-finishers by legal progress, disqualifications unplaced.
        /// </summary>
        static Dictionary<string, Placing> DriftPlacings(List<EntrantFinish> finishes, Dictionary<string, long> scores)
        {
            var result = new Dictionary<string, Placing>();
            List<EntrantFinish> finishers = finishes.Where(f => f.Outcome == RunOutcome.Finished).OrderByDescending(f => scores[f.EntrantId]).ToList();
            foreach (EntrantFinish f in finishers)
            {
                long score = scores[f.EntrantId];
                result[f.EntrantId] = new Placing
                {
                    EntrantId = f.EntrantId, Outcome = RunOutcome.Finished,
                    Place = 1 + finishers.Count(o => scores[o.EntrantId] > score), Tied = finishers.Count(o => scores[o.EntrantId] == score) > 1,
                };
            }
            int next = finishers.Count;
            foreach (EntrantFinish dnf in finishes.Where(f => f.Outcome == RunOutcome.DidNotFinish).OrderByDescending(f => f.LegalProgressMetres))
                result[dnf.EntrantId] = new Placing { EntrantId = dnf.EntrantId, Place = ++next, Outcome = RunOutcome.DidNotFinish };
            foreach (EntrantFinish other in finishes.Where(f => !result.ContainsKey(f.EntrantId)))
                result[other.EntrantId] = new Placing { EntrantId = other.EntrantId, Place = 0, Outcome = other.Outcome };
            return result;
        }
    }
}
