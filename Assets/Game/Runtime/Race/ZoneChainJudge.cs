using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Track;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>
    /// Server-observed facts for Core <see cref="ZoneChainRun"/>: the route's challenge-tagged transition, clip and
    /// demonstration zones (C03 CH17, C05 CH19, C09 CH22, C19 CH27), their challenge's bank gate (a timing gate with the same
    /// tag — C19-CHAIN-BANK), sector ends, road contact, legal direction, wall impacts and touches, resets and the finish.
    /// Runs for every entrant offline and in the dedicated server, beside <see cref="DriftJudge"/>; only humans are judged.
    /// </summary>
    public sealed class ZoneChainJudge
    {
        readonly TrackData track;
        readonly List<ChallengeZone> zones;
        readonly List<RouteGateDef> bankGates;
        readonly float[] sectorStarts;

        ZoneChainJudge(TrackData track, List<ChallengeZone> zones, List<RouteGateDef> bankGates)
        {
            this.track = track;
            this.zones = zones;
            this.bankGates = bankGates;
            sectorStarts = track.Sectors.Select(s => s.StartMetres).OrderBy(x => x).ToArray();
        }

        /// <summary>A judge for a course with challenge-tagged transition, clip or demonstration zones; null otherwise.</summary>
        public static ZoneChainJudge ForTrack(TrackData track)
        {
            if (track?.Gates == null) return null;
            List<ChallengeZone> z = track.Gates
                .Where(g => !string.IsNullOrEmpty(g.Challenge) && g.EndMetres > g.StartMetres &&
                            (g.Kind == ChallengeZone.Transition || g.Kind == ChallengeZone.Clip || g.Kind == ChallengeZone.Demo))
                .OrderBy(g => g.StartMetres)
                .Select(g => new ChallengeZone { Id = g.Id, Challenge = g.Challenge, Kind = g.Kind, StartMetres = g.StartMetres, EndMetres = g.EndMetres,
                    LineOffset = g.LineOffset, LineTolerance = g.LineTolerance })
                .ToList();
            if (z.Count == 0) return null;
            List<RouteGateDef> banks = track.Gates.Where(g => g.Kind == "timing" && !string.IsNullOrEmpty(g.Challenge) && z.Any(x => x.Challenge == g.Challenge))
                .OrderBy(g => g.StartMetres).ToList();
            return new ZoneChainJudge(track, z, banks);
        }

        public IReadOnlyList<ChallengeZone> Zones => zones;
        public IReadOnlyList<RouteGateDef> BankGates => bankGates;

        public int ZoneAt(float distance)
        {
            for (int i = 0; i < zones.Count; i++)
                if (distance >= zones[i].StartMetres && distance <= zones[i].EndMetres) return i;
            return -1;
        }

        int SectorAt(float distance)
        {
            int s = 0;
            for (int i = 0; i < sectorStarts.Length; i++)
                if (distance >= sectorStarts[i]) s = i + 1;
            return s;
        }

        /// <summary>One fixed step for one entrant (after its simulation, progress, reset and finish handling).</summary>
        public void Step(RaceEntrant e, bool reset)
        {
            ZoneChainRun r = e.ZoneChains ?? (e.ZoneChains = new ZoneChainRun(zones));
            TrackLocation loc = e.Progress.Location;
            StepTelemetry t = e.Sim.Telemetry;
            float d = loc.Distance;
            float last = r.LastDistance;
            r.LastDistance = d;
            // A bank gate counts only when driven across (not reached by a reset, a recovery or a circuit's lap wrap).
            string gate = "";
            if (!reset && last >= 0f && d >= last && d - last <= 30f)
                foreach (RouteGateDef g in bankGates)
                    if (last < g.StartMetres && d >= g.StartMetres) gate = g.Challenge;
            int sector = SectorAt(d);
            bool sectorEnd = r.LastSector >= 0 && sector != r.LastSector;
            r.LastSector = sector;
            bool wall = e.Progress.WallIncidents > r.WallsSeen;
            r.WallsSeen = e.Progress.WallIncidents;
            r.Step(new ZoneChainSample
            {
                DeltaSeconds = VehicleSimulation.TickDt,
                SpeedKmh = e.State.Velocity.magnitude * 3.6f,
                SlipAngleDegrees = t.BodySlipDeg,
                ProgressMetres = e.Progress.RaceDistance,
                MovingInLegalDirection = Vector3.Dot(e.State.Velocity, track.SampleAt(d).Tangent) > 0f,
                OnRoad = loc.InCorridor && t.GroundedWheels >= 2,
                WallImpact = wall,
                WallContact = t.WallContact,
                Reset = reset,
                Zone = ZoneAt(d),
                LateralMetres = loc.Lateral,
                BankGate = gate,
                SectorEnd = sectorEnd,
                Finished = e.Progress.Finished,
                RouteMetres = d,
            });
        }

        /// <summary>Automation only (CH23's recoveries): a challenge's zones as separate drift zones — one slide started and caught in each.</summary>
        public IReadOnlyList<RouteGateDef> AutopilotZonesOf(string challenge) =>
            zones.Where(z => z.Challenge == challenge).Select(z => new RouteGateDef { Id = z.Id, Kind = "drift-zone", Challenge = z.Challenge,
                StartMetres = z.StartMetres, EndMetres = z.EndMetres, LineOffset = z.LineOffset, LineTolerance = Mathf.Max(0.5f, z.LineTolerance) }).ToList();

        /// <summary>
        /// Automation only (the zone tour and its measurement): where the validator autopilot holds one slide — per challenge,
        /// from its first zone to its bank gate (or its last zone's end) — so a chain can link the zones as a driver links them.
        /// </summary>
        public IReadOnlyList<RouteGateDef> AutopilotSpans()
        {
            var spans = new List<RouteGateDef>();
            foreach (IGrouping<string, ChallengeZone> c in zones.GroupBy(z => z.Challenge))
            {
                RouteGateDef bank = bankGates.FirstOrDefault(g => g.Challenge == c.Key);
                spans.Add(new RouteGateDef { Id = c.Key + "-SPAN", Kind = "drift-zone", Challenge = c.Key, StartMetres = c.Min(z => z.StartMetres),
                    EndMetres = bank != null ? bank.StartMetres + 5f : c.Max(z => z.EndMetres), LineTolerance = 3f });
            }
            return spans.OrderBy(s => s.StartMetres).ToList();
        }

        /// <summary>
        /// Automation only: the lines the autopilot holds — each span on the centreline, then each clip zone on its marked line
        /// (overriding the span there), <paramref name="clipInset"/> inside it (kept within the clip's tolerance) so a slide
        /// running wide still has road before the barrier.
        /// </summary>
        public IReadOnlyList<RouteGateDef> AutopilotLines(float clipInset = 0f) =>
            AutopilotSpans().Concat(zones.Where(z => z.Kind == ChallengeZone.Clip)
                .Select(z => new RouteGateDef { Id = z.Id, Kind = z.Kind, Challenge = z.Challenge, StartMetres = z.StartMetres, EndMetres = z.EndMetres,
                    LineOffset = z.LineOffset - Mathf.Sign(z.LineOffset) * Mathf.Min(clipInset, Mathf.Max(0f, z.LineTolerance - 0.2f)), LineTolerance = z.LineTolerance }))
                .ToList();
    }
}
