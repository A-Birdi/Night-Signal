using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Track;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>One entrant's facts at the course's challenge gates.</summary>
    public sealed class GateRun
    {
        public float LastDistance = -1f;
        /// <summary>Per touch gate (apex / precision), in route order: times the car crossed it, and times its body was in the band.</summary>
        public readonly int[] Passes, Touches;
        /// <summary>Per lane zone: inside now (entered over its start), passes completed over its end, and the margin never broken.</summary>
        public readonly bool[] LaneInside, LaneMarginKept;
        public readonly int[] LanePasses;
        /// <summary>Fixed steps with any barrier contact at all (a guardrail touch — stricter than a meaningful wall incident).</summary>
        public int BarrierTouchSteps;
        /// <summary>The challenge each touch gate and each lane zone serves (from the route).</summary>
        readonly string[] touchChallenges, laneChallenges;
        /// <summary>Per speed gate (exit-speed gates and braking zones with a challenge tag): its id and what the car did there.</summary>
        public readonly string[] SpeedGateIds = new string[0];
        public readonly GateSpeedFact[] Speed = new GateSpeedFact[0];
        internal bool[] SpeedInside = new bool[0], Braking = new bool[0];
        internal int[] WallsAtEntry = new int[0], ContactsAtEntry = new int[0];

        public GateRun(string[] touchChallenges, string[] laneChallenges, string[] speedGateIds) : this(touchChallenges, laneChallenges)
        {
            SpeedGateIds = speedGateIds;
            Speed = new GateSpeedFact[speedGateIds.Length];
            SpeedInside = new bool[speedGateIds.Length];
            Braking = new bool[speedGateIds.Length];
            WallsAtEntry = new int[speedGateIds.Length];
            ContactsAtEntry = new int[speedGateIds.Length];
        }

        /// <summary>The facts at one speed gate by its route id (null when the course has no such gate).</summary>
        public GateSpeedFact? SpeedFact(string gateId)
        {
            int i = System.Array.IndexOf(SpeedGateIds, gateId);
            return i < 0 ? (GateSpeedFact?)null : Speed[i];
        }

        public GateRun(string[] touchChallenges, string[] laneChallenges)
        {
            this.touchChallenges = touchChallenges;
            this.laneChallenges = laneChallenges;
            Passes = new int[touchChallenges.Length];
            Touches = new int[touchChallenges.Length];
            LaneInside = new bool[laneChallenges.Length];
            LanePasses = new int[laneChallenges.Length];
            LaneMarginKept = Enumerable.Repeat(true, laneChallenges.Length).ToArray();
        }

        /// <summary>
        /// Every touch gate serving <paramref name="challenge"/> crossed, and touched on every crossing (each lap of a circuit);
        /// false when the course has none.
        /// </summary>
        /// <summary>How many touch gates serve <paramref name="challenge"/> on this course.</summary>
        public int Count(string challenge)
        {
            int n = 0;
            for (int i = 0; i < touchChallenges.Length; i++)
                if (touchChallenges[i] == challenge) n++;
            return n;
        }

        public bool AllTouched(string challenge)
        {
            bool any = false;
            for (int i = 0; i < touchChallenges.Length; i++)
            {
                if (touchChallenges[i] != challenge) continue;
                any = true;
                if (Passes[i] == 0 || Touches[i] < Passes[i]) return false;
            }
            return any;
        }

        /// <summary>Every lane zone serving <paramref name="challenge"/> crossed start to end with the margin kept (false when none).</summary>
        public bool LanesKept(string challenge)
        {
            bool any = false;
            for (int i = 0; i < laneChallenges.Length; i++)
            {
                if (laneChallenges[i] != challenge) continue;
                any = true;
                if (LanePasses[i] == 0 || !LaneMarginKept[i]) return false;
            }
            return any;
        }
    }

    /// <summary>
    /// Appendix E precision gates from the course route (each gate names the challenge it serves): whether the car touched
    /// every marked apex / precision gate (its body across the gate's band, as the Four Signals apexes are judged), whether it
    /// crossed each marked lane zone keeping the published safety margin from the barriers (a body widened by
    /// <see cref="LaneMarginMetres"/> must touch no barrier), and any guardrail touch at all. The same server-observed facts
    /// offline and on the dedicated server; every car is measured, only humans are judged.
    /// </summary>
    public sealed class GateJudge
    {
        /// <summary>CH09's published safety margin from barriers in the viaduct lane zones.</summary>
        public const float LaneMarginMetres = 0.5f;

        readonly List<RouteGateDef> touches, lanes, speeds;

        GateJudge(List<RouteGateDef> touches, List<RouteGateDef> lanes, List<RouteGateDef> speeds)
        {
            this.touches = touches;
            this.lanes = lanes;
            this.speeds = speeds;
        }

        /// <summary>Exit-speed gates and braking zones with a challenge tag (their published references judge them).</summary>
        public IReadOnlyList<RouteGateDef> SpeedGates => speeds;
        /// <summary>A brake input above this counts as braking.</summary>
        public const float BrakeThreshold = 0.2f;

        public IReadOnlyList<RouteGateDef> TouchGates => touches;
        public IReadOnlyList<RouteGateDef> LaneZones => lanes;

        /// <summary>A judge for a course with challenge-tagged apex, precision or lane gates; null otherwise.</summary>
        public static GateJudge ForTrack(TrackData track)
        {
            if (track?.Gates == null) return null;
            List<RouteGateDef> t = track.Gates.Where(g => !string.IsNullOrEmpty(g.Challenge) && (g.Kind == "apex" || g.Kind == "precision"))
                .OrderBy(g => g.StartMetres).ToList();
            List<RouteGateDef> l = track.Gates.Where(g => !string.IsNullOrEmpty(g.Challenge) && g.Kind == "lane").OrderBy(g => g.StartMetres).ToList();
            List<RouteGateDef> s = track.Gates.Where(g => !string.IsNullOrEmpty(g.Challenge) && (g.Kind == "exit-speed" || g.Kind == "brake-zone"))
                .OrderBy(g => g.StartMetres).ToList();
            return t.Count + l.Count + s.Count == 0 ? null : new GateJudge(t, l, s);
        }

        public void Step(RaceEntrant e, bool reset, IVehicleWorld world) => Step(e, DriverInput.Neutral, reset, world);

        /// <summary>One fixed step for one entrant, after its simulation, progress and reset handling (<paramref name="input"/>: this step's).</summary>
        public void Step(RaceEntrant e, DriverInput input, bool reset, IVehicleWorld world)
        {
            GateRun r = e.GateRun ?? (e.GateRun = new GateRun(touches.Select(g => g.Challenge).ToArray(), lanes.Select(g => g.Challenge).ToArray(),
                speeds.Select(g => g.Id).ToArray()));
            if (e.Sim.Telemetry.WallContact) r.BarrierTouchSteps++;
            float d = e.Progress.Location.Distance;
            float last = r.LastDistance;
            r.LastDistance = d;
            // No crossings across a discontinuity (a reset, a recovery, the first step, a circuit's start line — route distance
            // starts again each lap): a zone the car was inside cannot be vouched for.
            if (reset || last < 0f || d < last || d - last > 30f)
            {
                for (int i = 0; i < lanes.Count; i++)
                    if (r.LaneInside[i])
                    {
                        r.LaneInside[i] = false;
                        r.LaneMarginKept[i] = false;
                    }
                // A reset inside a braking zone voids that pass and is counted against it.
                for (int i = 0; i < speeds.Count; i++)
                    if (r.SpeedInside[i])
                    {
                        r.SpeedInside[i] = false;
                        if (reset) r.Speed[i].ResetsInside++;
                    }
                return;
            }
            bool Crossed(float m) => last < m && d >= m;
            float lateral = e.Progress.Location.Lateral, halfWidth = e.Params.WidthM * 0.5f;
            for (int i = 0; i < touches.Count; i++)
                if (Crossed(touches[i].StartMetres))
                {
                    r.Passes[i]++;
                    if (Mathf.Abs(lateral - touches[i].LineOffset) <= touches[i].LineTolerance + halfWidth) r.Touches[i]++;
                }
            float kmh = e.State.SpeedKmh;
            for (int i = 0; i < speeds.Count; i++)
            {
                RouteGateDef g = speeds[i];
                if (g.Kind == "exit-speed")
                {
                    // Every pass must clear the floor: keep the slowest crossing.
                    if (Crossed(g.StartMetres))
                    {
                        r.Speed[i].SpeedKmh = r.Speed[i].Crossed ? Mathf.Min(r.Speed[i].SpeedKmh, kmh) : kmh;
                        r.Speed[i].Crossed = true;
                    }
                    continue;
                }
                if (Crossed(g.StartMetres))
                {
                    r.SpeedInside[i] = true;
                    r.Braking[i] = false;
                    r.Speed[i].EntryKmh = kmh;
                    r.Speed[i].Braked = false;
                    r.Speed[i].ReleaseMetres = -1f;
                    r.Speed[i].BrakeOnMetres = -1f;
                    r.WallsAtEntry[i] = e.Progress.WallIncidents;
                    r.ContactsAtEntry[i] = e.Progress.VehicleContacts;
                }
                if (!r.SpeedInside[i]) continue;
                if (input.Brake > BrakeThreshold)
                {
                    if (!r.Speed[i].Braked) r.Speed[i].BrakeOnMetres = d;
                    r.Speed[i].Braked = true;
                    r.Braking[i] = true;
                    r.Speed[i].ReleaseMetres = -1f;
                }
                else if (r.Braking[i])
                {
                    r.Braking[i] = false;
                    r.Speed[i].ReleaseMetres = d;
                }
                if (Crossed(g.EndMetres))
                {
                    r.SpeedInside[i] = false;
                    r.Speed[i].Crossed = true;
                    r.Speed[i].ExitKmh = kmh;
                    r.Speed[i].WallsInside += e.Progress.WallIncidents - r.WallsAtEntry[i];
                    r.Speed[i].ContactsInside += e.Progress.VehicleContacts - r.ContactsAtEntry[i];
                }
            }
            for (int i = 0; i < lanes.Count; i++)
            {
                RouteGateDef z = lanes[i];
                if (Crossed(z.StartMetres)) r.LaneInside[i] = true;
                if (r.LaneInside[i] && world != null && r.LaneMarginKept[i])
                {
                    Vector3 half = e.Params.BodyHalfExtents + new Vector3(LaneMarginMetres, 0f, LaneMarginMetres);
                    if (world.NearBarrier(e.State.Position + e.State.Rotation * e.Params.BodyCentreOffset, e.State.Rotation, half))
                        r.LaneMarginKept[i] = false;
                }
                if (r.LaneInside[i] && Crossed(z.EndMetres))
                {
                    r.LaneInside[i] = false;
                    r.LanePasses[i]++;
                }
            }
        }
    }
}
