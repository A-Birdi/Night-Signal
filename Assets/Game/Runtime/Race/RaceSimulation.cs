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
        /// <summary>Reset safety ghost: non-colliding until this tick AND clear of overlaps (bounded); -1 when solid.</summary>
        public int GhostUntilTick = -1;
        /// <summary>Continuous seconds clearly off the course (far outside the corridor or well below the road).</summary>
        public float OffCourseSeconds;
        /// <summary>Automatic marshal recoveries (counted as resets, with the reset penalty).</summary>
        public int AutoRecoveries;
        /// <summary>Continuous seconds an AI has been effectively stationary while racing.</summary>
        public float StuckSeconds;
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
        /// <summary>dry | damp | wet — one grip rule for races and the Garage Test Yard (CourseRuntime.SurfaceGrip).</summary>
        public string Surface = "dry";
    }

    public sealed class HumanSlot
    {
        public string EntrantId;
        public string DisplayName;
        public string CarId;
        /// <summary>The frozen applied build this human races (parts resolved by Core); null = the model's stock car.</summary>
        public Core.Builds.ResolvedCarSpec Spec;
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
        /// <summary>Finished ahead of the featured live rival, or the rival legally failed to finish (a tie does not beat).</summary>
        public bool BeatFeaturedRival;
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
        public readonly List<RaceEntrant> Entrants = new List<RaceEntrant>();
        /// <summary>
        /// Record ruleset versions (personal records only compare within one version). Bump PhysicsVersion whenever the
        /// chassis, contact or barrier response changes; ScoringVersion when classification or timing changes.
        /// </summary>
        public const string PhysicsVersion = "chassis-2026.09-contact1";
        public const string ScoringVersion = "classify-1";

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
            if (rules.Contact == ContactPolicy.NonContact && friendlyAi.Count + opposingAi.Count > 0)
                throw new InvalidOperationException("Time Attack (non-contact) has no live AI");

            var sim = new RaceSimulation(track, rules);
            ContentCatalogue cat = lib.Catalogue;
            int slot = 0, generic = 0;
            foreach (HumanSlot h in humans)
                sim.Add(lib, world, slot++, h.EntrantId, h.DisplayName, true, h.CarId, "player", "driver", null, h.Spec);
            foreach (string id in friendlyAi)
                sim.AddAi(cat, lib, world, slot++, id, "player", "friendly", AiPlacementContext.FriendlyAi, ref generic);
            for (int i = 0; i < opposingAi.Count; i++)
            {
                AiPlacementContext ctx = rules.Kind == "campaign" ? AiPlacementContext.CampaignEncounter
                    : rules.Kind == "trial" ? AiPlacementContext.TeamTrial : AiPlacementContext.FreeplayOpponent;
                string role = rules.Kind == "campaign" ? (i == 0 ? "featured" : "support") : "opponent";
                sim.AddAi(cat, lib, world, slot++, opposingAi[i], "opposing", role, ctx, ref generic);
            }
            return sim;
        }

        void AddAi(ContentCatalogue cat, ContentLibrary lib, IVehicleWorld world, int slot, string id, string team, string role, AiPlacementContext ctx, ref int generic)
        {
            RaceEntrant e;
            if (cat.TryRival(id, out RivalDef rival))
            {
                FinalRivals.Require(rival.Id, ctx, Rules.StageId, Rules.Mode);
                e = Add(lib, world, slot, rival.Id, rival.Name, false, LegalCarFor(cat, rival.PrimaryCar, Rules.CarCapPi), team, role, null);
                e.Ai = new RouteFollower(Track, e.Params, AiProfiles.For(rival, Rules.StageNumber));
            }
            else
            {
                // Freeplay opponents without a rival identity: a legal car under the cap and a neutral profile.
                CarDef car = cat.Cars.Where(c => Rules.CarCapPi <= 0 || c.BasePI <= Rules.CarCapPi)
                    .OrderByDescending(c => c.BasePI).Skip(generic % 3).FirstOrDefault() ?? cat.Cars.OrderBy(c => c.BasePI).First();
                e = Add(lib, world, slot, id, $"Driver {id.ToUpperInvariant()}", false, car.Id, team, role, null);
                e.Ai = new RouteFollower(Track, e.Params, AiProfiles.Generic(generic++));
            }
        }

        RaceEntrant Add(ContentLibrary lib, IVehicleWorld world, int slot, string id, string name, bool human, string carId, string team, string role, float[] paint,
            Core.Builds.ResolvedCarSpec spec = null)
        {
            if (spec != null && spec.CarModelId != carId) throw new InvalidOperationException($"build for {spec.CarModelId} used on {carId}");
            VehicleParams p = spec != null ? VehicleFactory.Build(spec, AssistSettings.Default, lib.Body(carId).WheelRadius) : lib.Params(carId, AssistSettings.Default);
            GridSlot g = Track.Grid[slot];
            var e = new RaceEntrant
            {
                Params = p,
                Sim = new VehicleSimulation(p, world) { SurfaceGripScale = CourseRuntime.SurfaceGrip(Rules.Surface) },
                State = VehicleState.AtRest(g.Position, g.Rotation),
                Progress = new EntrantProgress(Track),
                Status = human ? EntrantStatus.Reserved : EntrantStatus.Loaded,
                Roster = new RosterEntry
                {
                    Index = slot, EntrantId = id, DisplayName = name, Human = human, CarId = carId, GridSlot = slot,
                    Paint = paint ?? Palette(slot, human, team), Team = team, Role = role,
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
                Tracker.Step(e.Progress, prev, e.State, e.Sim.Telemetry, raceMicros, VehicleSimulation.TickDt);

                e.ResetHeld = input.ResetHeld ? e.ResetHeld + VehicleSimulation.TickDt : 0f;
                if (e.ResetHeld >= 0.7f && !e.Progress.Finished)
                {
                    e.State = Tracker.ResetPose(e.Progress, e.Params);
                    e.ResetHeld = 0f;
                    e.GhostUntilTick = tick + GhostWindowTicks;
                }
                else if (!e.Progress.Finished && (ClearlyOffCourse(e) || AiStuck(e)))
                {
                    // Marshal recovery: a car can never leave the playable world (fall off the terrain edge, drop under the
                    // road). Same pose, penalty and safety ghost as a player reset.
                    e.State = Tracker.ResetPose(e.Progress, e.Params);
                    e.GhostUntilTick = tick + GhostWindowTicks;
                    e.OffCourseSeconds = 0f;
                    e.StuckSeconds = 0f;
                    e.AutoRecoveries++;
                }
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

        static int GhostWindowTicks => Limits.ResetGhostMaxMs * VehicleSimulation.TickRate / 1000;

        /// <summary>
        /// AI only: stationary (under 1 m/s) for 4 s while racing — wedged against a barrier or another car. Humans keep
        /// hold-to-reset; the server never takes a human's car away from them for being slow.
        /// </summary>
        bool AiStuck(RaceEntrant e)
        {
            if (e.Human) return false;
            e.StuckSeconds = e.State.Velocity.sqrMagnitude < 1f ? e.StuckSeconds + VehicleSimulation.TickDt : 0f;
            return e.StuckSeconds > 4f;
        }

        /// <summary>Recovery trigger: 1.5 s far outside the corridor (&gt; 20 m past the road edge or 6 m below it), or 40 m below.</summary>
        bool ClearlyOffCourse(RaceEntrant e)
        {
            TrackLocation loc = e.Progress.Location;
            TrackSample here = Track.SampleAt(loc.Distance);
            bool farOff = !loc.InCorridor && (loc.Vertical < -6f || Mathf.Abs(loc.Lateral) > here.Width * 0.5f + 20f);
            e.OffCourseSeconds = farOff ? e.OffCourseSeconds + VehicleSimulation.TickDt : 0f;
            return e.OffCourseSeconds > 1.5f || loc.Vertical < -40f;
        }

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
        /// reset safety ghosts never collide; a safety ghost ends once its window has passed and it overlaps nobody,
        /// hard-bounded at twice the window.
        /// </summary>
        void ResolveContacts(int tick, long raceMicros)
        {
            if (Rules.Contact == ContactPolicy.NonContact) return;
            foreach (RaceEntrant e in Entrants)
                if (e.GhostUntilTick >= 0 && tick >= e.GhostUntilTick)
                {
                    bool overlapping = Entrants.Any(o => o != e && o.Collides && VehicleContact.Overlapping(e.State, e.Params, o.State, o.Params));
                    if (!overlapping || tick >= e.GhostUntilTick + GhostWindowTicks) e.GhostUntilTick = -1;
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
            Dictionary<string, Placing> placings = RaceClassification.Classify(finishes).ToDictionary(p => p.EntrantId);
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
                });
            }
            return results;
        }
    }
}
