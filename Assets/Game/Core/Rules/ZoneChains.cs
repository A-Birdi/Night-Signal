using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    /// <summary>
    /// A route-owned challenge zone judged as part of a drift chain (spec §7, Appendix E): C03's link corners (CH17), C05's
    /// demonstration zone (CH19), C09's outer clip zones (CH22), C19's transition zones (CH27). Not a raw-score drift zone —
    /// the raw scorer and Drift Attack keep their own "drift-zone" gates.
    /// </summary>
    public sealed class ChallengeZone
    {
        public const string Transition = "transition-zone", Clip = "clip-zone", Demo = "demo-zone";

        public string Id = "";
        public string Challenge = "";
        /// <summary><see cref="Transition"/> | <see cref="Clip"/> | <see cref="Demo"/>.</summary>
        public string Kind = "";
        public float StartMetres, EndMetres;
        /// <summary>The marked line (m from the centreline, + = right) and its tolerance: a clip zone links only on it.</summary>
        public float LineOffset, LineTolerance;
    }

    /// <summary>One fixed simulation step of server-observed facts for <see cref="ZoneChainRun"/>.</summary>
    public struct ZoneChainSample
    {
        public float DeltaSeconds;
        public float SpeedKmh;
        /// <summary>Signed slip angle between heading and velocity, degrees.</summary>
        public float SlipAngleDegrees;
        /// <summary>Legal race distance; only progress beyond the run's high-water mark is forward.</summary>
        public double ProgressMetres;
        public bool MovingInLegalDirection;
        public bool OnRoad;
        /// <summary>A meaningful wall impact this step (loses the chain, as in the raw scorer).</summary>
        public bool WallImpact;
        /// <summary>Any barrier contact at all this step (a touch — CH22's "without wall contact").</summary>
        public bool WallContact;
        public bool Reset;
        /// <summary>Index of the challenge zone containing the car, or -1.</summary>
        public int Zone;
        /// <summary>The car's signed offset from the centreline (m, + = right).</summary>
        public float LateralMetres;
        /// <summary>The challenge whose bank gate the car crossed this step ("" or null = none).</summary>
        public string BankGate;
        /// <summary>The car crossed into another route sector this step.</summary>
        public bool SectorEnd;
        /// <summary>The car finished this step.</summary>
        public bool Finished;
        /// <summary>The car's route distance (recoveries are allowed a short run-out past a zone's end).</summary>
        public float RouteMetres;
    }

    /// <summary>One chain that linked at least one challenge zone, as it ended.</summary>
    public sealed class ZoneChain
    {
        /// <summary>Zones linked (indices into the run's zones), in the order the chain first linked them.</summary>
        public readonly List<int> Zones = new List<int>();
        /// <summary>True when the chain banked; false when it was lost (<see cref="End"/> says why).</summary>
        public bool Banked;
        public ChainEnd End;
        /// <summary>The challenge whose bank gate banked it ("" = released by straightening, a sector end or the finish).</summary>
        public string BankGate = "";
        /// <summary>Any barrier contact while the chain was alive.</summary>
        public bool Touched;
        public float Seconds;
    }

    /// <summary>
    /// Challenge zones judged as drift chains (spec §7; Appendix E CH17, CH19, CH22, CH27). A chain is one continuous legal
    /// slide — at least 35 km/h, forward along the legal direction, on the road, in the scoring angle band (10–80°) — and it
    /// survives the spec's 1.0-second straightening interval between linked zones. It links every challenge zone it slides
    /// through (a clip zone only with the car on the zone's marked line). It banks when the slide straightens for longer,
    /// at a sector end, at the finish or at its challenge's bank gate, and it is lost — as the raw scorer loses an unbanked
    /// chain — on a meaningful wall impact, leaving the road, a reset or a spin. Unlike the raw scorer, a slide held between
    /// zones keeps the chain: the raw scorer only scores inside drift zones and banks after 1.0 s without scoring, which on
    /// C09 and C19 (zones 90–140 m apart) would make "one chain" through them impossible. The same facts offline and on the
    /// game server; only the predicates read them.
    /// </summary>
    public sealed class ZoneChainRun
    {
        /// <summary>CH19's legal demonstration angle band (degrees, inclusive).</summary>
        public const float HoldMinDegrees = 20f, HoldMaxDegrees = 35f;

        readonly IReadOnlyList<ChallengeZone> zones;
        /// <summary>Every ended chain that linked a zone (banked or lost), in order.</summary>
        public readonly List<ZoneChain> Chains = new List<ZoneChain>();
        /// <summary>Per zone: the longest continuous legal drift held inside it within <see cref="HoldMinDegrees"/>–<see cref="HoldMaxDegrees"/> (s).</summary>
        public readonly float[] LongestHold;
        /// <summary>Chains lost (with or without linked zones).</summary>
        public int ChainsLost { get; private set; }

        /// <summary>
        /// CH23: a valid recovery — a slide past <see cref="SlideDegrees"/> inside a transition zone caught back to
        /// <see cref="RecoverDegrees"/> or less, inside the zone or within <see cref="RecoveryRunOutMetres"/> after it, without a
        /// spin, wall impact, leaving the road or a reset in between.
        /// </summary>
        public const float SlideDegrees = 12f, RecoverDegrees = 5f, RecoveryRunOutMetres = 20f;
        /// <summary>
        /// Every valid recovery in order: the zone and the slide's direction (+1 / −1, the sign of its slip); a reset or a spin in
        /// between is kept as a break (zone −1), so a drill's recoveries must follow one another without one.
        /// </summary>
        public readonly List<(int Zone, int Direction)> Recoveries = new List<(int, int)>();
        /// <summary>A spin at any time (the slip beyond 100° for 0.3 s).</summary>
        public bool Spun { get; private set; }
        int slideZone = -1, slideSign;

        /// <summary>Feed bookkeeping for the runtime judge (last route distance, sector and wall count seen).</summary>
        public float LastDistance = -1f;
        public int LastSector = -1, WallsSeen;

        ZoneChain current;
        double highWater;
        float sinceDrift, spinning, holdSeconds;
        int holdZone = -1;

        public ZoneChainRun(IReadOnlyList<ChallengeZone> zones)
        {
            this.zones = zones ?? throw new ArgumentNullException(nameof(zones));
            LongestHold = new float[zones.Count];
        }

        public IReadOnlyList<ChallengeZone> Zones => zones;
        /// <summary>The live chain's linked zones (empty when no chain is alive).</summary>
        public IReadOnlyList<int> CurrentZones => current != null ? (IReadOnlyList<int>)current.Zones : Array.Empty<int>();
        public bool ChainAlive => current != null;

        public void Step(ZoneChainSample s)
        {
            float absSlip = Math.Abs(s.SlipAngleDegrees);
            double forward = s.ProgressMetres - highWater;
            if (s.ProgressMetres > highWater) highWater = s.ProgressMetres;

            if (current != null && s.WallContact) current.Touched = true;
            ChainEnd lost = s.Reset ? ChainEnd.LostReset : s.WallImpact ? ChainEnd.LostWall : !s.OnRoad ? ChainEnd.LostOffCourse : ChainEnd.None;
            spinning = absSlip >= DriftScorer.SpinAngleDegrees ? spinning + s.DeltaSeconds : 0f;
            if (spinning >= DriftScorer.SpinHoldSeconds) Spun = true;
            if (lost == ChainEnd.None && spinning >= DriftScorer.SpinHoldSeconds) lost = ChainEnd.LostSpin;
            if (lost != ChainEnd.None)
            {
                Lose(lost);
                EndHold();
                slideZone = -1;
                if ((lost == ChainEnd.LostReset || lost == ChainEnd.LostSpin || s.Reset) && (Recoveries.Count == 0 || Recoveries[Recoveries.Count - 1].Zone >= 0))
                    Recoveries.Add((-1, 0));
                return;
            }
            StepRecovery(s, absSlip);

            bool drifting = forward > 0 && s.MovingInLegalDirection && s.SpeedKmh >= DriftScorer.MinimumSpeedKmh && DriftScorer.AngleFactor(absSlip) > 0;
            ChallengeZone z = s.Zone >= 0 && s.Zone < zones.Count ? zones[s.Zone] : null;

            // CH19: a continuous legal drift inside a demonstration zone within the angle band.
            if (drifting && z != null && z.Kind == ChallengeZone.Demo && absSlip >= HoldMinDegrees && absSlip <= HoldMaxDegrees)
            {
                if (holdZone != s.Zone) { holdZone = s.Zone; holdSeconds = 0f; }
                holdSeconds += s.DeltaSeconds;
                if (holdSeconds > LongestHold[holdZone]) LongestHold[holdZone] = holdSeconds;
            }
            else EndHold();

            if (drifting)
            {
                if (current == null) current = new ZoneChain();
                sinceDrift = 0f;
                if (z != null && !current.Zones.Contains(s.Zone) && Links(z, s.LateralMetres)) current.Zones.Add(s.Zone);
            }
            else if (current != null)
            {
                sinceDrift += s.DeltaSeconds;
                if (sinceDrift > DriftScorer.StraighteningIntervalSeconds) Bank("");
            }
            if (current != null) current.Seconds += s.DeltaSeconds;

            if (!string.IsNullOrEmpty(s.BankGate)) Bank(s.BankGate);
            else if (s.SectorEnd || s.Finished) Bank("");
        }

        void StepRecovery(ZoneChainSample s, float absSlip)
        {
            ChallengeZone z = s.Zone >= 0 && s.Zone < zones.Count ? zones[s.Zone] : null;
            if (z != null && z.Kind == ChallengeZone.Transition && absSlip >= SlideDegrees)
            {
                slideZone = s.Zone;
                slideSign = Math.Sign(s.SlipAngleDegrees);
                return;
            }
            if (slideZone < 0) return;
            if (s.RouteMetres > zones[slideZone].EndMetres + RecoveryRunOutMetres || s.RouteMetres < zones[slideZone].StartMetres - 1f)
            {
                slideZone = -1; // not caught in time (or a jump): no recovery
                return;
            }
            if (absSlip <= RecoverDegrees)
            {
                Recoveries.Add((slideZone, slideSign));
                slideZone = -1;
            }
        }

        /// <summary>
        /// CH23: every zone of <paramref name="challenge"/> recovered, consecutively and in route order, the slide's direction
        /// alternating from one to the next, with no reset or spin between them (false when the course has none).
        /// </summary>
        public bool AlternatingRecoveries(string challenge)
        {
            List<int> mine = Enumerable.Range(0, zones.Count).Where(i => zones[i].Challenge == challenge && zones[i].Kind == ChallengeZone.Transition)
                .OrderBy(i => zones[i].StartMetres).ToList();
            if (mine.Count == 0) return false;
            for (int start = 0; start + mine.Count <= Recoveries.Count; start++)
            {
                bool ok = true;
                for (int k = 0; k < mine.Count && ok; k++)
                {
                    (int zone, int dir) = Recoveries[start + k];
                    ok = zone == mine[k] && dir != 0 && (k == 0 || dir == -Recoveries[start + k - 1].Direction);
                }
                if (ok) return true;
            }
            return false;
        }

        /// <summary>A drifting step inside a zone links it; a clip zone only with the car on its marked line.</summary>
        public static bool Links(ChallengeZone z, float lateralMetres) =>
            z.Kind != ChallengeZone.Clip || Math.Abs(lateralMetres - z.LineOffset) <= Math.Max(0.5f, z.LineTolerance);

        /// <summary>How many zones serve <paramref name="challenge"/> on this course.</summary>
        public int ZonesOf(string challenge) => zones.Count(z => z.Challenge == challenge);

        /// <summary>
        /// One banked chain linked at least <paramref name="count"/> distinct zones of <paramref name="challenge"/> (with
        /// <paramref name="untouched"/>: and touched no barrier while alive).
        /// </summary>
        public bool Linked(string challenge, int count, bool untouched = false) =>
            count > 0 && Chains.Any(c => c.Banked && (!untouched || !c.Touched) && c.Zones.Count(i => zones[i].Challenge == challenge) >= count);

        /// <summary>
        /// One banked chain linked every zone of <paramref name="challenge"/>, each once and in forward route order (with
        /// <paramref name="atBankGate"/>: banked at that challenge's bank gate; with <paramref name="untouched"/>: no barrier
        /// touch while alive). False when the course has no such zone.
        /// </summary>
        public bool LinkedAll(string challenge, bool atBankGate = false, bool untouched = false)
        {
            int n = ZonesOf(challenge);
            if (n == 0) return false;
            foreach (ZoneChain c in Chains)
            {
                if (!c.Banked || (untouched && c.Touched) || (atBankGate && c.BankGate != challenge)) continue;
                List<int> mine = c.Zones.Where(i => zones[i].Challenge == challenge).ToList();
                bool forward = true;
                for (int k = 1; k < mine.Count; k++)
                    if (zones[mine[k]].StartMetres <= zones[mine[k - 1]].StartMetres) forward = false;
                if (mine.Count == n && forward) return true;
            }
            return false;
        }

        /// <summary>The longest legal in-band hold (s) in any demonstration zone of <paramref name="challenge"/>.</summary>
        public float LongestHoldFor(string challenge)
        {
            float best = 0f;
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].Challenge == challenge && zones[i].Kind == ChallengeZone.Demo && LongestHold[i] > best) best = LongestHold[i];
            return best;
        }

        void Bank(string gate)
        {
            if (current == null) return;
            current.Banked = true;
            current.End = ChainEnd.Banked;
            current.BankGate = gate ?? "";
            if (current.Zones.Count > 0) Chains.Add(current);
            Clear();
        }

        void Lose(ChainEnd reason)
        {
            if (current == null) return;
            current.Banked = false;
            current.End = reason;
            if (current.Zones.Count > 0) Chains.Add(current);
            ChainsLost++;
            Clear();
        }

        void Clear()
        {
            current = null;
            sinceDrift = 0f;
        }

        void EndHold()
        {
            holdZone = -1;
            holdSeconds = 0f;
        }
    }
}
