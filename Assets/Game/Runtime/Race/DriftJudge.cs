using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Track;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>
    /// Server-observed drift facts for Core <see cref="DriftScorer"/> (spec §7): the route's judged drift zones and their
    /// intended lines, sector ends (where a chain banks), road contact, legal direction, wall impacts and resets. The same
    /// judge runs in the dedicated server and offline, for every entrant; only drift formats rank by the result.
    /// </summary>
    public sealed class DriftJudge
    {
        readonly TrackData track;
        readonly List<RouteGateDef> zones;
        readonly float[] sectorStarts;

        public DriftJudge(TrackData track)
        {
            this.track = track;
            zones = track.Gates.Where(g => g.Kind == "drift-zone" && g.EndMetres > g.StartMetres).OrderBy(g => g.StartMetres).ToList();
            sectorStarts = track.Sectors.Select(s => s.StartMetres).OrderBy(x => x).ToArray();
        }

        /// <summary>Judged drift zones on this course (none = nothing can score).</summary>
        public IReadOnlyList<RouteGateDef> Zones => zones;

        /// <summary>Index of the drift zone containing a course distance, or -1.</summary>
        public int ZoneAt(float distance)
        {
            for (int i = 0; i < zones.Count; i++)
                if (distance >= zones[i].StartMetres && distance <= zones[i].EndMetres) return i;
            return -1;
        }

        /// <summary>Route sector containing a course distance (0 before the first authored boundary).</summary>
        public int SectorAt(float distance)
        {
            int s = 0;
            for (int i = 0; i < sectorStarts.Length; i++)
                if (distance >= sectorStarts[i]) s = i + 1;
            return s;
        }

        /// <summary>One fixed step for one entrant (after its simulation, progress, reset and finish handling).</summary>
        public void Step(RaceEntrant e, bool reset, bool finishedThisTick)
        {
            TrackLocation loc = e.Progress.Location;
            StepTelemetry t = e.Sim.Telemetry;
            TrackSample here = track.SampleAt(loc.Distance);
            int zone = ZoneAt(loc.Distance);
            RouteGateDef g = zone >= 0 ? zones[zone] : null;
            bool wall = e.Progress.WallIncidents > e.DriftWallsSeen;
            e.DriftWallsSeen = e.Progress.WallIncidents;
            DriftStepResult r = e.Drift.Step(new DriftSample
            {
                DeltaSeconds = VehicleSimulation.TickDt,
                SpeedKmh = e.State.Velocity.magnitude * 3.6f,
                SlipAngleDegrees = t.BodySlipDeg,
                ProgressMetres = e.Progress.RaceDistance,
                MovingInLegalDirection = Vector3.Dot(e.State.Velocity, here.Tangent) > 0f,
                OnRoad = loc.InCorridor && t.GroundedWheels >= 2,
                WallImpact = wall,
                Reset = reset,
                JudgedZone = zone,
                LineOffsetMetres = g != null ? loc.Lateral - g.LineOffset : 0f,
                LineToleranceMetres = g != null ? Mathf.Max(0.5f, g.LineTolerance) : 1f,
            });
            e.LastChainEnd = r.ChainEnd != ChainEnd.None ? r.ChainEnd : e.LastChainEnd;
            int sector = SectorAt(loc.Distance);
            if (e.DriftSector >= 0 && sector != e.DriftSector) e.Drift.BankAtSectorEnd();
            e.DriftSector = sector;
            if (finishedThisTick) e.Drift.BankAtFinish();
        }

        /// <summary>The reported raw drift score: banked chains only, whole points (what the control plane settles).</summary>
        public static long Reported(RaceEntrant e) => (long)System.Math.Floor(e.Drift.BankedRaw);
    }
}
