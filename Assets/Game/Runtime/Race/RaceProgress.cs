using System.Collections.Generic;
using NightSignal.Core.Rules;
using NightSignal.Track;
using NightSignal.Track.Generation;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>Authoritative per-entrant progress facts (spec §6.1, §18).</summary>
    public sealed class EntrantProgress
    {
        public readonly TrackLocator Locator;
        public int NextCheckpoint;
        public int Lap;
        public int CheckpointsPassed;
        public bool Finished;
        /// <summary>Race-clock finish time in microseconds, sub-tick interpolated at the finish gate.</summary>
        public long FinishTimeMicros;
        /// <summary>Each completed lap's race-clock time (µs, sub-tick at the line, penalties excluded) — CH10 compares them.</summary>
        public readonly List<long> LapMicros = new List<long>();
        public long LapStartMicros;
        public float RaceDistance;
        public float LastSafeDistance;
        public int WallIncidents;
        /// <summary>Debounced car-to-car contact incidents (kept separate from wall incidents; not a cleanliness fault).</summary>
        public int VehicleContacts;
        public double LastVehicleContactTime = double.NegativeInfinity;
        public int Resets;
        /// <summary>Seconds with the handbrake held while racing (challenge trials: CH25).</summary>
        public float HandbrakeSeconds;
        /// <summary>Time penalties (resets: 3 s each) added to the finish time.</summary>
        public long PenaltyMicros;
        public bool CorridorCut;
        public float WrongWaySeconds;
        public float OutOfCorridorSeconds;
        /// <summary>Continuous seconds clearly off the legal route (lost, off the road layer, or far outside the corridor).</summary>
        public float OffRouteSeconds;
        public TrackLocation Location;
        /// <summary>Every completed recovery (manual or automatic): one penalty each (Addendum 03 §7.3).</summary>
        public readonly List<RecoveryEvent> Recoveries = new List<RecoveryEvent>();
        readonly Dictionary<int, double> lastImpactBySurface = new Dictionary<int, double>();

        public EntrantProgress(TrackData track)
        {
            Locator = new TrackLocator(track) { AllowGlobalRecovery = false }; // progress never jumps to another road
        }

        /// <summary>No meaningful wall impacts, no resets and all checkpoints legal (economy cleanliness).</summary>
        public bool Clean => WallIncidents == 0 && Resets == 0 && !CorridorCut;
        /// <summary>Set once the car has driven the race's first route sector: whether it did so with no wall incident, no
        /// reset, no corridor cut and no time outside the corridor (challenge CH33).</summary>
        public bool FirstSectorJudged, FirstSectorClean;
        /// <summary>Wall incidents counted when the first reset happened (−1 = no reset yet; challenge CH35).</summary>
        public int WallsAtFirstReset = -1;

        internal bool RegisterImpact(int surfaceId, double time)
        {
            if (lastImpactBySurface.TryGetValue(surfaceId, out double last) && time - last < Limits.WallImpactDebounceMs / 1000.0)
            {
                lastImpactBySurface[surfaceId] = time;
                return false;
            }
            lastImpactBySurface[surfaceId] = time;
            return true;
        }
    }

    public enum RecoveryKind { None = 0, OffRoute = 1, Overturned = 2, Stopped = 3 }

    /// <summary>The recovery offer for one car this frame (<see cref="RaceSimulation.Recovery"/>).</summary>
    public struct RecoveryStatus
    {
        public RecoveryKind Kind;
        /// <summary>Seconds until the marshal recovers the car automatically; −1 = no automatic recovery pending.</summary>
        public float SecondsToAuto;
        /// <summary>Progress of a reset hold (0..1).</summary>
        public float HoldFraction;
    }

    /// <summary>One completed authoritative recovery: when, why, from and to where on the route, and its penalty.</summary>
    public struct RecoveryEvent
    {
        public long RaceMicros;
        /// <summary>manual | off-route | overturned | stuck</summary>
        public string Reason;
        public float FromDistance, ToDistance;
        public int PenaltyMs;
    }

    /// <summary>
    /// Ordered checkpoint, finish, incident and legality tracking. Pure logic over simulation state, used
    /// identically by the dedicated server and offline sessions. Checkpoints are finite, directional 3D gates
    /// (Addendum 03 §6): a gate is accepted only for a forward crossing of its plane, inside its lateral and vertical
    /// bounds, by a car tracked on the legal route beside it — never by landing on a lower road, passing under an
    /// overpass, backing over it or a teleport.
    /// </summary>
    public sealed class RaceProgressTracker
    {
        /// <summary>Normal-speed onset threshold for a "meaningful" wall impact (m/s into the wall).</summary>
        public const float MeaningfulImpactSpeed = 3.0f;
        public const float CutToleranceMetres = 25f;
        /// <summary>Gate volume relative to the road surface: the car's centre crosses between these heights.</summary>
        public const float GateBelowMetres = -1.5f, GateAboveMetres = 8f;
        /// <summary>Beyond this many metres outside the corridor the car is off the route (a road edge excursion is not).</summary>
        public const float OffRouteLateralMetres = 12f;

        readonly TrackData track;
        readonly float finishMetres;
        /// <summary>Race distance at the end of the first route sector ahead of the start (the whole course without sectors).</summary>
        readonly float firstSectorMetres;

        public RaceProgressTracker(TrackData track)
        {
            this.track = track;
            finishMetres = CourseGenerator.FinishMetres(track);
            float first = float.MaxValue;
            foreach (RouteSectorDef s in track.Sectors)
            {
                float ahead = Forward(track.StartMetres, s.StartMetres);
                if (ahead > 1f && ahead < first) first = ahead;
            }
            firstSectorMetres = first < float.MaxValue ? first : Forward(track.StartMetres, finishMetres);
        }

        public int CheckpointsPerLap => track.CheckpointMetres.Length;
        public int TotalCheckpoints => track.CheckpointMetres.Length * track.Laps;

        public void Start(EntrantProgress e, Vector3 position)
        {
            e.Locator.Reset(track.StartMetres);
            e.Location = e.Locator.Locate(position, Vector3.forward);
            e.LastSafeDistance = track.StartMetres;
            e.RaceDistance = RaceDistanceOf(e, e.Location.Distance); // grid cars sit behind the line (negative on circuits)
        }

        /// <param name="raceTimeMicros">Race clock at the end of this tick (0 at GO).</param>
        public void Step(EntrantProgress e, in VehicleState previous, in VehicleState current, in StepTelemetry telemetry,
            long raceTimeMicros, float dt)
        {
            if (e.Finished) return;
            Vector3 heading = current.Rotation * Vector3.forward;
            float prevDist = e.Location.Distance;
            TrackLocation loc = e.Locator.Locate(current.Position, heading);
            e.Location = loc;

            if (!loc.InCorridor) e.OutOfCorridorSeconds += dt;
            e.OffRouteSeconds = OffRoute(loc) ? e.OffRouteSeconds + dt : 0f;
            bool moving = current.Velocity.sqrMagnitude > 4f;
            if (moving && Vector3.Dot(current.Velocity, track.Samples[loc.Index].Tangent) < 0f && loc.HeadingDot < -0.3f)
                e.WrongWaySeconds += dt;
            else
                e.WrongWaySeconds = Mathf.Max(0f, e.WrongWaySeconds - dt);

            if (telemetry.WallImpactSpeed >= MeaningfulImpactSpeed &&
                e.RegisterImpact(telemetry.WallSurfaceId, raceTimeMicros / 1_000_000.0))
                e.WallIncidents++;

            // Ordered finite gates: the expected gate is accepted for a forward crossing of its plane inside its bounds
            // (the whole movement of the tick is swept, so a fast car cannot tunnel through), by a car on the legal route.
            float gate = track.CheckpointMetres[e.NextCheckpoint];
            float moved = Forward(prevDist, loc.Distance);
            bool forwards = !track.ClosedLoop || moved < track.LengthMetres * 0.5f;
            float toGate = Forward(prevDist, gate);
            if (GateCrossed(gate, previous.Position, current.Position, loc, out float crossing))
            {
                e.CheckpointsPassed++;
                e.LastSafeDistance = gate;
                bool lastOfLap = e.NextCheckpoint == track.CheckpointMetres.Length - 1;
                long lineMicros = raceTimeMicros - (long)(dt * 1_000_000f) + (long)(Mathf.Clamp01(crossing) * (long)(dt * 1_000_000f));
                if (lastOfLap && e.Lap == track.Laps - 1 && Mathf.Approximately(gate, finishMetres))
                {
                    e.FinishTimeMicros = lineMicros + e.PenaltyMicros;
                    e.LapMicros.Add(lineMicros - e.LapStartMicros);
                    e.Finished = true;
                }
                else if (lastOfLap)
                {
                    e.LapMicros.Add(lineMicros - e.LapStartMicros);
                    e.LapStartMicros = lineMicros;
                    e.Lap++;
                    e.NextCheckpoint = 0;
                    e.Locator.Reset(gate);
                }
                else
                {
                    e.NextCheckpoint++;
                }
            }
            else if (!loc.Lost && forwards && toGate > 0f && moved > toGate + CutToleranceMetres)
            {
                // Passed a gate without a legal crossing (off-corridor or a jump): the run is no longer clean.
                e.CorridorCut = true;
            }
            e.RaceDistance = LegalRaceDistance(e, loc);
            if (!e.FirstSectorJudged && e.RaceDistance >= firstSectorMetres)
            {
                e.FirstSectorJudged = true;
                e.FirstSectorClean = e.WallIncidents == 0 && e.Resets == 0 && !e.CorridorCut && e.OutOfCorridorSeconds <= 0f;
            }
        }

        /// <summary>Clearly off the legal route: lost from its stretch, off the road layer, or far outside the corridor.</summary>
        public bool OffRoute(TrackLocation loc)
        {
            if (loc.Lost || !loc.OnLayer) return true;
            TrackSample here = track.Samples[Mathf.Clamp(loc.Index, 0, track.Samples.Length - 1)];
            float edge = here.Width * 0.5f + (loc.Lateral >= 0f ? here.ShoulderRight : here.ShoulderLeft) + OffRouteLateralMetres;
            return Mathf.Abs(loc.Lateral) > edge;
        }

        /// <summary>
        /// The expected gate's finite 3D test for one tick's movement <paramref name="from"/>→<paramref name="to"/>: a
        /// forward crossing of its plane (tangent normal) whose crossing point lies inside the gate's lateral corridor and
        /// vertical envelope, by a car tracked on the route near the gate. <paramref name="fraction"/> is where along the
        /// movement the plane was crossed.
        /// </summary>
        public bool GateCrossed(float gateDistance, Vector3 from, Vector3 to, TrackLocation loc, out float fraction)
        {
            fraction = 0f;
            if (loc.Lost) return false;
            float apart = track.ClosedLoop
                ? Mathf.Abs(Mathf.Repeat(loc.Distance - gateDistance + track.LengthMetres * 0.5f, track.LengthMetres) - track.LengthMetres * 0.5f)
                : Mathf.Abs(loc.Distance - gateDistance);
            if (apart > CutToleranceMetres) return false; // tracked on another stretch of the route
            TrackSample g = track.SampleAt(gateDistance);
            float a = Vector3.Dot(from - g.Position, g.Tangent), b = Vector3.Dot(to - g.Position, g.Tangent);
            if (!(a < 0f && b >= 0f)) return false; // not a forward crossing of the plane this tick
            // Plausible travel THROUGH the gate: a car dropping into it (mostly vertical, or numerically on its plane) is not a crossing.
            float advance = b - a;
            if (advance < 0.01f || advance < 0.25f * (to - from).magnitude) return false;
            fraction = a / (a - b);
            Vector3 rel = Vector3.Lerp(from, to, fraction) - g.Position;
            float lateral = Vector3.Dot(rel, g.Right), vertical = Vector3.Dot(rel, g.Up);
            float half = g.Width * 0.5f + (lateral >= 0f ? g.ShoulderRight : g.ShoulderLeft) + TrackLocator.CorridorToleranceMetres;
            return Mathf.Abs(lateral) <= half && vertical >= GateBelowMetres && vertical <= GateAboveMetres;
        }

        /// <summary>
        /// Ranking distance from LEGAL progress: accepted gates plus progress on the current stretch, never past the next
        /// gate not yet crossed, and held where it was while the car is off the route (a car that fell toward the bottom of
        /// the mountain loses places; it does not gain them).
        /// </summary>
        float LegalRaceDistance(EntrantProgress e, TrackLocation loc)
        {
            if (OffRoute(loc)) return e.RaceDistance;
            float d = RaceDistanceOf(e, loc.Distance);
            float nextGate = RaceDistanceOf(e, track.CheckpointMetres[e.NextCheckpoint]);
            if (nextGate < d - track.LengthMetres * 0.5f) nextGate += track.LengthMetres; // a circuit gate just past the seam
            return Mathf.Min(d, nextGate + 0.5f);
        }

        /// <summary>Forward travel from one track distance to another (wrapping round a closed loop).</summary>
        float Forward(float from, float to) => track.ClosedLoop ? Mathf.Repeat(to - from, track.LengthMetres) : to - from;

        /// <summary>
        /// Distance covered since the start line. On a circuit, laps count from the start line; a car still behind the
        /// line (on the grid, before its lap's first gate) is short of it, not a lap ahead.
        /// </summary>
        float RaceDistanceOf(EntrantProgress e, float distance)
        {
            if (!track.ClosedLoop) return e.Lap * track.LengthMetres + distance;
            if (e.Finished) return track.Laps * track.LengthMetres; // the finish tick wraps past the line without a new lap
            float u = Mathf.Repeat(distance - track.StartMetres, track.LengthMetres);
            if (e.NextCheckpoint == 0 && u > track.LengthMetres * 0.5f) u -= track.LengthMetres;
            return e.Lap * track.LengthMetres + u;
        }

        /// <summary>
        /// A completed recovery (spec §6.1, Addendum 03 §7): the car is placed at a safe anchor at or behind the last accepted
        /// gate — never ahead, never on another road — at rest facing the course, with one penalty and one event.
        /// </summary>
        public VehicleState ResetPose(EntrantProgress e, VehicleParams p, long raceMicros = 0, string reason = "manual",
            System.Func<Vector3, bool> occupied = null)
        {
            float from = e.Location.Distance;
            VehicleState state = AnchorPose(e, p, occupied, out float at);
            e.Resets++;
            if (e.Resets == 1) e.WallsAtFirstReset = e.WallIncidents;
            e.PenaltyMicros += Limits.ResetPenaltyMs * 1000L;
            e.OffRouteSeconds = 0f;
            e.Recoveries.Add(new RecoveryEvent { RaceMicros = raceMicros, Reason = reason, FromDistance = from, ToDistance = at, PenaltyMs = Limits.ResetPenaltyMs });
            return state;
        }

        /// <summary>
        /// The safe anchor itself (no penalty): the last accepted gate's centre, else a lane to either side, else stepping
        /// back up to 60 m (never before the course start) — the first placement no other car occupies. Also used to move a
        /// recovered car that is still overlapping when its protection ends (the same recovery, no second penalty).
        /// </summary>
        public VehicleState AnchorPose(EntrantProgress e, VehicleParams p, System.Func<Vector3, bool> occupied, out float at)
        {
            float lowest = track.ClosedLoop ? e.LastSafeDistance - 60f : Mathf.Max(0f, e.LastSafeDistance - 60f);
            TrackSample chosen = track.SampleAt(e.LastSafeDistance);
            Vector3 place = chosen.Position;
            bool found = false;
            for (float back = 0f; !found && e.LastSafeDistance - back >= lowest; back += 8f)
            {
                TrackSample s = track.SampleAt(e.LastSafeDistance - back);
                float lane = Mathf.Max(0f, s.Width * 0.25f);
                foreach (float offset in new[] { 0f, -lane, lane })
                {
                    Vector3 candidate = s.Position + s.Right * offset;
                    if (occupied != null && occupied(candidate)) continue;
                    chosen = s;
                    place = candidate;
                    found = true;
                    break;
                }
            }
            at = chosen.Distance;
            var state = VehicleState.AtRest(place + chosen.Up * (p.CgHeightM + 0.15f), Quaternion.LookRotation(chosen.Tangent, chosen.Up));
            e.Locator.Reset(chosen.Distance);
            e.Location = e.Locator.Locate(state.Position, chosen.Tangent);
            return state;
        }
    }
}
