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
        public float RaceDistance;
        public float LastSafeDistance;
        public int WallIncidents;
        /// <summary>Debounced car-to-car contact incidents (kept separate from wall incidents; not a cleanliness fault).</summary>
        public int VehicleContacts;
        public double LastVehicleContactTime = double.NegativeInfinity;
        public int Resets;
        /// <summary>Time penalties (resets: 3 s each) added to the finish time.</summary>
        public long PenaltyMicros;
        public bool CorridorCut;
        public float WrongWaySeconds;
        public float OutOfCorridorSeconds;
        public TrackLocation Location;
        readonly Dictionary<int, double> lastImpactBySurface = new Dictionary<int, double>();

        public EntrantProgress(TrackData track)
        {
            Locator = new TrackLocator(track);
        }

        /// <summary>No meaningful wall impacts, no resets and all checkpoints legal (economy cleanliness).</summary>
        public bool Clean => WallIncidents == 0 && Resets == 0 && !CorridorCut;

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

    /// <summary>
    /// Ordered checkpoint, finish, incident and legality tracking. Pure logic over simulation state, used
    /// identically by the dedicated server and offline sessions.
    /// </summary>
    public sealed class RaceProgressTracker
    {
        /// <summary>Normal-speed onset threshold for a "meaningful" wall impact (m/s into the wall).</summary>
        public const float MeaningfulImpactSpeed = 3.0f;
        public const float CutToleranceMetres = 25f;

        readonly TrackData track;
        readonly float finishMetres;

        public RaceProgressTracker(TrackData track)
        {
            this.track = track;
            finishMetres = CourseGenerator.FinishMetres(track);
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
            bool moving = current.Velocity.sqrMagnitude > 4f;
            if (moving && Vector3.Dot(current.Velocity, track.Samples[loc.Index].Tangent) < 0f && loc.HeadingDot < -0.3f)
                e.WrongWaySeconds += dt;
            else
                e.WrongWaySeconds = Mathf.Max(0f, e.WrongWaySeconds - dt);

            if (telemetry.WallImpactSpeed >= MeaningfulImpactSpeed &&
                e.RegisterImpact(telemetry.WallSurfaceId, raceTimeMicros / 1_000_000.0))
                e.WallIncidents++;

            // Ordered checkpoints: crossed when the legal distance passes the gate inside the corridor. Distances are
            // measured as forward travel so a circuit's gates just past the loop seam (distance 0) work the same way.
            float gate = track.CheckpointMetres[e.NextCheckpoint];
            float moved = Forward(prevDist, loc.Distance);
            bool forwards = !track.ClosedLoop || moved < track.LengthMetres * 0.5f;
            float toGate = Forward(prevDist, gate);
            if (forwards && toGate > 0f && toGate <= moved && loc.InCorridor && moved < CutToleranceMetres)
            {
                e.CheckpointsPassed++;
                e.LastSafeDistance = gate;
                bool lastOfLap = e.NextCheckpoint == track.CheckpointMetres.Length - 1;
                if (lastOfLap && e.Lap == track.Laps - 1 && Mathf.Approximately(gate, finishMetres))
                {
                    float frac = Mathf.Clamp01(toGate / Mathf.Max(1e-4f, moved));
                    long tickMicros = (long)(dt * 1_000_000f);
                    e.FinishTimeMicros = raceTimeMicros - tickMicros + (long)(frac * tickMicros) + e.PenaltyMicros;
                    e.Finished = true;
                }
                else if (lastOfLap)
                {
                    e.Lap++;
                    e.NextCheckpoint = 0;
                    e.Locator.Reset(gate);
                }
                else
                {
                    e.NextCheckpoint++;
                }
            }
            else if (forwards && toGate > 0f && moved > toGate + CutToleranceMetres)
            {
                // Passed a gate without a legal crossing (off-corridor or a jump): the run is no longer clean.
                e.CorridorCut = true;
            }
            e.RaceDistance = RaceDistanceOf(e, loc.Distance);
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

        /// <summary>Reset pose at the last safe checkpoint (spec §6.1): never ahead, never skipping gates.</summary>
        public VehicleState ResetPose(EntrantProgress e, VehicleParams p)
        {
            TrackSample s = track.SampleAt(e.LastSafeDistance);
            var state = VehicleState.AtRest(s.Position + s.Up * (p.CgHeightM + 0.15f), Quaternion.LookRotation(s.Tangent, s.Up));
            e.Resets++;
            e.PenaltyMicros += Limits.ResetPenaltyMs * 1000L;
            e.Locator.Reset(e.LastSafeDistance);
            e.Location = e.Locator.Locate(state.Position, s.Tangent);
            return state;
        }
    }
}
