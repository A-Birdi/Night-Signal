using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Track;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>
    /// A car's recent legal progress against race time, to read the interval to the car ahead: the time since that car
    /// was where this one is now (the usual racing gap: it does not depend on either car's speed at one instant).
    /// Cleared on a recovery, which moves the car back.
    /// </summary>
    public sealed class DistanceHistory
    {
        readonly double[] times;
        readonly float[] distances;
        int head, count;

        public DistanceHistory(int capacity = 300)
        {
            times = new double[capacity];
            distances = new float[capacity];
        }

        public void Clear() => count = 0;

        public void Add(double time, float distance)
        {
            head = (head + 1) % times.Length;
            times[head] = time;
            distances[head] = distance;
            if (count < times.Length) count++;
        }

        /// <summary>When this car was last at <paramref name="distance"/> (interpolated); false when it has not got there yet or it is older than the history.</summary>
        public bool TimeAt(float distance, out double time)
        {
            time = 0;
            if (count == 0 || distances[head] < distance) return false;
            int i = head;
            for (int n = 1; n < count; n++)
            {
                int prev = (i - 1 + times.Length) % times.Length;
                if (distances[prev] <= distance)
                {
                    float span = distances[i] - distances[prev];
                    double f = span > 1e-4f ? (distance - distances[prev]) / span : 1.0;
                    time = times[prev] + (times[i] - times[prev]) * f;
                    return true;
                }
                i = prev;
            }
            return false;
        }

        /// <summary>
        /// Seconds behind the car whose history this is, for a car at <paramref name="distance"/> at <paramref name="now"/>;
        /// false when that car is not ahead or passed there before the history begins (a large gap).
        /// </summary>
        public bool IntervalBehind(float distance, double now, out float seconds)
        {
            seconds = 0f;
            if (!TimeAt(distance, out double at)) return false;
            seconds = (float)(now - at);
            return true;
        }
    }

    /// <summary>One entrant's racecraft facts (CH31 Clean Pass, CH32 Patient Mirror, CH34 marked-zone passes).</summary>
    public sealed class RacecraftRun
    {
        public readonly DistanceHistory History = new DistanceHistory();
        /// <summary>Race time of this car's last touch with any other car (any contact, not only a meaningful incident).</summary>
        public double LastTouch = double.NegativeInfinity;
        public double LastReset = double.NegativeInfinity;
        public float PreviousDistance = float.NaN;

        /// <summary>Passes confirmed clean: the passed entrant (index) and when.</summary>
        public readonly List<(int Passed, double Time)> CleanPasses = new List<(int, double)>();
        /// <summary>Passes waiting for their clean window and the hold to run out.</summary>
        public readonly List<(int Passed, double Time)> Pending = new List<(int, double)>();
        /// <summary>Every pass seen and what became of it (evidence; the first <see cref="MaxLog"/>).</summary>
        public readonly List<string> PassLog = new List<string>();
        public const int MaxLog = 24;

        public void Log(string line)
        {
            if (PassLog.Count < MaxLog) PassLog.Add(line);
        }

        /// <summary>The car directly ahead (index; −1 none) and the interval to it (s; negative when not measurable).</summary>
        public int Ahead = -1;
        public float Interval = -1f;
        /// <summary>The current follow: its target, when it began, and the longest completed or running follow (s).</summary>
        public int FollowTarget = -1;
        public double FollowSince;
        public float FollowLongest;
        public int FollowLongestTarget = -1;

        /// <summary>Passes made inside a challenge's marked overtake zone, waiting for its retain gate: the car passed, the challenge, when.</summary>
        public readonly List<(int Passed, string Challenge, double Time)> ZonePending = new List<(int, string, double)>();
        /// <summary>
        /// Marked-zone passes whose place was still held at the challenge's retain gate: the car passed, the challenge, when,
        /// and whether this car touched no car from 2 s before the pass until the gate.
        /// </summary>
        public readonly List<(int Passed, string Challenge, double Time, bool TouchFree)> ZonePasses = new List<(int, string, double, bool)>();

        /// <summary>
        /// Every complete pass through a challenge's defence zone (CH39's pressure sector): its time, whether a car in the
        /// "pressure" role stayed within 1 s behind for all of it, and whether this car touched a barrier in it.
        /// </summary>
        public readonly List<(string Challenge, double Seconds, bool PressureHeld, bool WallTouched)> DefenceRuns = new List<(string, double, bool, bool)>();
        /// <summary>
        /// Every merge span driven (CH37: two marked lanes of one challenge side by side): the challenge, and whether this car
        /// stayed in its lane with the "merge" pace car in the other, both inside the span together, and no car touched.
        /// </summary>
        public readonly List<(string Challenge, bool Kept)> Merges = new List<(string, bool)>();
        internal int MergePair = -1;
        internal RouteGateDef MergeMine, MergeTheirs;
        internal bool MergeOk;
        internal double MergeEntry;
        internal int DefenceZone = -1;
        internal double DefenceEntry;
        internal bool DefenceHeld, DefenceTouched;
    }

    /// <summary>
    /// Racecraft judging for a race with live opponents (not Time Attack, not Drift Attack). Server-authoritative like
    /// every other predicate: it reads the simulation's own progress, contacts and recoveries, never a client's claim.
    /// <list type="bullet">
    /// <item>A pass: this car's legal progress goes from behind a live, solid, moving car to ahead of it in one fixed step,
    /// neither car recovering. It is clean when this car touched no car in the 2 s before and the 2 s after, and kept the
    /// place for 3 s (or finished ahead first).</item>
    /// <item>A follow: the same live, moving car directly ahead, the interval to it inside the 1–2 s window, no touch and
    /// no recovery; the longest unbroken follow is kept.</item>
    /// <item>A marked-zone pass (CH34, CH40, CH36): a pass of a live, moving car made inside a route overtake zone tagged with a
    /// challenge — not on its approach — or inside a tagged lane with this car within the lane's band, and the place still held
    /// when this car crosses that challenge's retain gate (a timing gate with the same tag; 3 s without one), neither car
    /// recovering in between.</item>
    /// </list>
    /// </summary>
    public sealed class RacecraftJudge
    {
        public const double CleanWindowSeconds = 2.0, HoldSeconds = 3.0;
        public const float FollowMinSeconds = 1f, FollowMaxSeconds = 2f;
        /// <summary>A parked or crashed car is not raced: the other car must be doing at least this (m/s, 18 km/h).</summary>
        public const float MovingMps = 5f;

        readonly List<RaceEntrant> entrants;
        readonly TrackData track;
        readonly List<RouteGateDef> passZones = new List<RouteGateDef>(), retainGates = new List<RouteGateDef>(), defenceZones = new List<RouteGateDef>();
        /// <summary>Two marked lanes of one challenge over the same span (CH37's T00-MERGE-L / -R).</summary>
        readonly List<(RouteGateDef A, RouteGateDef B)> mergePairs = new List<(RouteGateDef, RouteGateDef)>();
        /// <summary>How far apart along the road (m) the merge pace car may be and still be beside the car (CH37).</summary>
        public const float MergeBesideMetres = 15f;
        /// <summary>The interval (s) within which a pressure car counts as following (CH39 "follows within 1 second").</summary>
        public const float PressureSeconds = 1f;

        public RacecraftJudge(List<RaceEntrant> entrants, TrackData track = null)
        {
            this.entrants = entrants;
            this.track = track;
            if (track?.Gates == null) return;
            passZones.AddRange(track.Gates.Where(g => (g.Kind == "overtake-zone" || g.Kind == "lane") && !string.IsNullOrEmpty(g.Challenge) && g.EndMetres > g.StartMetres));
            retainGates.AddRange(track.Gates.Where(g => g.Kind == "timing" && !string.IsNullOrEmpty(g.Challenge) && passZones.Any(z => z.Challenge == g.Challenge)));
            defenceZones.AddRange(track.Gates.Where(g => g.Kind == "defence" && !string.IsNullOrEmpty(g.Challenge) && g.EndMetres > g.StartMetres));
            foreach (IGrouping<string, RouteGateDef> lanes in passZones.Where(z => z.Kind == "lane").GroupBy(z => z.Challenge))
            {
                List<RouteGateDef> l = lanes.OrderBy(z => z.LineOffset).ToList();
                if (l.Count == 2 && Mathf.Approximately(l[0].StartMetres, l[1].StartMetres) && Mathf.Approximately(l[0].EndMetres, l[1].EndMetres))
                    mergePairs.Add((l[0], l[1]));
            }
        }

        /// <summary>Only races with live opponents are judged (a drift or non-contact event has no racecraft).</summary>
        public static RacecraftJudge ForEvent(RaceEventRules rules, List<RaceEntrant> entrants, TrackData track = null) =>
            rules.Contact == ContactPolicy.NonContact || rules.DriftRanking ? null : new RacecraftJudge(entrants, track);

        /// <summary>The route distance of a legal race distance (a circuit's laps count from its start line).</summary>
        float RouteDistance(float raceDistance) =>
            track != null && track.ClosedLoop ? Mathf.Repeat(raceDistance + track.StartMetres, track.LengthMetres) : raceDistance;

        /// <summary>Whether a legal race distance lies inside a marked overtake zone (automation: where the zone tour's autopilot attacks).</summary>
        public bool InPassZone(float raceDistance) => passZones.Count > 0 && PassZoneAt(RouteDistance(raceDistance), float.NaN) != null;

        /// <summary>The challenge of the marked overtake zone at a route distance, or null.</summary>
        /// <param name="lateral">The passer's offset from the centreline; a marked lane counts only with the car inside its band (NaN = any).</param>
        string PassZoneAt(float routeDistance, float lateral)
        {
            foreach (RouteGateDef z in passZones)
                if (routeDistance >= z.StartMetres && routeDistance <= z.EndMetres &&
                    (z.Kind != "lane" || float.IsNaN(lateral) || Mathf.Abs(lateral - z.LineOffset) <= Mathf.Max(0.5f, z.LineTolerance)))
                    return z.Challenge;
            return null;
        }

        static RacecraftRun Run(RaceEntrant e) => e.Racecraft ?? (e.Racecraft = new RacecraftRun());

        static bool Live(RaceEntrant e) => e.Status == EntrantStatus.Racing && e.GhostUntilTick < 0;

        /// <summary>After the entrant stepped: its progress history (cleared by a recovery).</summary>
        public void Step(RaceEntrant e, bool reset, long raceMicros)
        {
            RacecraftRun r = Run(e);
            double now = raceMicros / 1e6;
            if (reset)
            {
                r.LastReset = now;
                r.History.Clear();
            }
            r.History.Add(now, e.Progress.RaceDistance);
        }

        /// <summary>Two cars touched this step.</summary>
        public void Touch(RaceEntrant a, RaceEntrant b, long raceMicros)
        {
            double now = raceMicros / 1e6;
            Run(a).LastTouch = now;
            Run(b).LastTouch = now;
        }

        /// <summary>After every car stepped and contacts resolved: passes, their confirmation, and follows.</summary>
        public void Judge(long raceMicros)
        {
            double now = raceMicros / 1e6;
            for (int i = 0; i < entrants.Count; i++)
            {
                RaceEntrant a = entrants[i];
                RacecraftRun ra = Run(a);
                float da = a.Progress.RaceDistance;
                if (Live(a) && !a.Progress.Finished && !float.IsNaN(ra.PreviousDistance) && now - ra.LastReset > CleanWindowSeconds)
                    for (int j = 0; j < entrants.Count; j++)
                    {
                        RaceEntrant b = entrants[j];
                        RacecraftRun rb = Run(b);
                        if (j == i || !Live(b) || float.IsNaN(rb.PreviousDistance) || now - rb.LastReset <= CleanWindowSeconds) continue;
                        bool passed = ra.PreviousDistance < rb.PreviousDistance && da >= b.Progress.RaceDistance;
                        if (!passed) continue;
                        string zone = passZones.Count > 0 ? PassZoneAt(RouteDistance(da), a.Progress.Location.Lateral) : null;
                        if (zone != null && b.State.Velocity.magnitude >= MovingMps)
                        {
                            ra.ZonePending.Add((j, zone, now));
                            ra.Log($"{now:F1} s: passed #{j} inside {zone}'s marked zone");
                        }
                        if (b.State.Velocity.magnitude < MovingMps) ra.Log($"{now:F1} s: passed #{j} standing — not raced");
                        else if (now - ra.LastTouch <= CleanWindowSeconds) ra.Log($"{now:F1} s: passed #{j} {now - ra.LastTouch:F1} s after a touch — not clean");
                        else ra.Pending.Add((j, now));
                    }
                Confirm(a, ra, now);
                if (ra.ZonePending.Count > 0) ConfirmZonePasses(a, ra, now, da);
                if (defenceZones.Count > 0) Defence(a, ra, now, da);
                if (mergePairs.Count > 0) Merge(a, ra, now, da);
                Follow(i, a, ra, now);
            }
            foreach (RaceEntrant e in entrants) Run(e).PreviousDistance = e.Progress.RaceDistance;
        }

        void Confirm(RaceEntrant a, RacecraftRun ra, double now)
        {
            for (int k = ra.Pending.Count - 1; k >= 0; k--)
            {
                (int j, double at) = ra.Pending[k];
                RaceEntrant b = entrants[j];
                // Any touch in the clean window after the pass spoils it (a touch before it was checked at the pass).
                bool touched = ra.LastTouch >= at && ra.LastTouch - at <= CleanWindowSeconds;
                bool broken = touched || ra.LastReset >= at || Run(b).LastReset >= at || b.Status == EntrantStatus.DqDisconnected ||
                              (!a.Progress.Finished && b.Progress.RaceDistance > a.Progress.RaceDistance) ||
                              (b.Progress.Finished && (!a.Progress.Finished || b.Progress.FinishTimeMicros < a.Progress.FinishTimeMicros));
                if (broken)
                {
                    ra.Log($"{at:F1} s: passed #{j} — " + (touched ? $"touch {ra.LastTouch - at:F1} s after" : ra.LastReset >= at || Run(b).LastReset >= at ? "a recovery" :
                        b.Status == EntrantStatus.DqDisconnected ? "it left the race" : "the place was lost within 3 s"));
                    ra.Pending.RemoveAt(k);
                    continue;
                }
                // The clean window after the pass (2 s) lies inside the hold (3 s); finishing ahead ends the hold early.
                double held = now - at;
                if (held >= HoldSeconds || (a.Progress.Finished && held >= CleanWindowSeconds))
                {
                    ra.CleanPasses.Add((j, at));
                    ra.Log($"{at:F1} s: passed #{j} — clean, the place kept");
                    ra.Pending.RemoveAt(k);
                }
            }
        }

        /// <summary>A marked-zone pass stands once this car crosses the challenge's retain gate still ahead (or, without a gate, after the 3 s hold).</summary>
        void ConfirmZonePasses(RaceEntrant a, RacecraftRun ra, double now, float da)
        {
            float before = float.IsNaN(ra.PreviousDistance) ? da : RouteDistance(ra.PreviousDistance), here = RouteDistance(da);
            for (int k = ra.ZonePending.Count - 1; k >= 0; k--)
            {
                (int j, string challenge, double at) = ra.ZonePending[k];
                RaceEntrant b = entrants[j];
                bool lost = ra.LastReset >= at || Run(b).LastReset >= at || b.Status == EntrantStatus.DqDisconnected ||
                            (!a.Progress.Finished && b.Progress.RaceDistance > a.Progress.RaceDistance) ||
                            (b.Progress.Finished && (!a.Progress.Finished || b.Progress.FinishTimeMicros < a.Progress.FinishTimeMicros));
                if (lost)
                {
                    ra.Log($"{at:F1} s: {challenge} pass of #{j} — the place was not held to the gate");
                    ra.ZonePending.RemoveAt(k);
                    continue;
                }
                RouteGateDef gate = retainGates.FirstOrDefault(g => g.Challenge == challenge);
                bool reached = gate == null ? now - at >= HoldSeconds
                    : (before < gate.StartMetres && here >= gate.StartMetres && here - before < 30f) || a.Progress.Finished;
                if (!reached) continue;
                bool touchFree = ra.LastTouch < at - CleanWindowSeconds;
                ra.ZonePasses.Add((j, challenge, at, touchFree));
                ra.Log($"{at:F1} s: {challenge} pass of #{j} — held to {(gate != null ? gate.Id : "the hold")}{(touchFree ? ", no touch" : ", touched")}");
                ra.ZonePending.RemoveAt(k);
            }
        }

        /// <summary>
        /// A defence zone driven start to end in one go (crossed over its start, then over its end, no recovery between): its
        /// time, whether a live car in the "pressure" role was within <see cref="PressureSeconds"/> behind at every step, and
        /// any barrier touch by this car in it.
        /// </summary>
        void Defence(RaceEntrant a, RacecraftRun ra, double now, float da)
        {
            if (float.IsNaN(ra.PreviousDistance)) return;
            float before = RouteDistance(ra.PreviousDistance), here = RouteDistance(da);
            bool stepped = here >= before && here - before < 30f;
            if (ra.DefenceZone >= 0)
            {
                RouteGateDef z = defenceZones[ra.DefenceZone];
                if (!stepped || ra.LastReset >= ra.DefenceEntry || !Live(a))
                {
                    ra.Log($"{now:F1} s: {z.Id} run void at {here:F0} m — {(ra.LastReset >= ra.DefenceEntry ? "a recovery" : !Live(a) ? "not racing" : "a jump in progress")}");
                    ra.DefenceZone = -1;
                    return;
                }
                bool pressed = false;
                string why = "no pressure car racing";
                foreach (RaceEntrant b in entrants)
                {
                    if (b == a || b.Roster.Role != "pressure") continue;
                    if (!Live(b)) { why = "the pressure car is not racing"; continue; }
                    if (b.Progress.RaceDistance >= da) { why = "the pressure car is ahead"; continue; }
                    if (!ra.History.IntervalBehind(b.Progress.RaceDistance, now, out float gap)) { why = "the gap is not measurable"; continue; }
                    if (gap <= PressureSeconds) pressed = true;
                    else why = $"the pressure car is {gap:F2} s behind";
                }
                if (!pressed && ra.DefenceHeld) ra.Log($"{now:F1} s: {z.Id} pressure lost at {here:F0} m — {why}");
                ra.DefenceHeld &= pressed;
                ra.DefenceTouched |= a.Sim != null && a.Sim.Telemetry.WallContact;
                if (before < z.EndMetres && here >= z.EndMetres)
                {
                    ra.DefenceRuns.Add((z.Challenge, now - ra.DefenceEntry, ra.DefenceHeld, ra.DefenceTouched));
                    ra.Log($"{now:F1} s: {z.Id} in {now - ra.DefenceEntry:F2} s, pressure {(ra.DefenceHeld ? "held throughout" : "not held")}{(ra.DefenceTouched ? ", barrier touched" : "")}");
                    ra.DefenceZone = -1;
                }
                return;
            }
            if (!stepped) return;
            for (int k = 0; k < defenceZones.Count; k++)
                if (before < defenceZones[k].StartMetres && here >= defenceZones[k].StartMetres)
                {
                    ra.DefenceZone = k;
                    ra.DefenceEntry = now;
                    ra.DefenceHeld = true;
                    ra.DefenceTouched = a.Sim != null && a.Sim.Telemetry.WallContact;
                }
        }

        static bool InLane(RaceEntrant e, RouteGateDef lane) => Mathf.Abs(e.Progress.Location.Lateral - lane.LineOffset) <= Mathf.Max(0.5f, lane.LineTolerance);

        /// <summary>
        /// A merge span (CH37): entered over its start in the lane nearer the car, then every step until its end the car in that
        /// lane, a live "merge" pace car inside the span in the other lane, and no touch; a recovery or a jump voids the pass.
        /// </summary>
        void Merge(RaceEntrant a, RacecraftRun ra, double now, float da)
        {
            if (float.IsNaN(ra.PreviousDistance) || a.Roster.Role == "merge") return;
            float before = RouteDistance(ra.PreviousDistance), here = RouteDistance(da);
            bool stepped = here >= before && here - before < 30f;
            if (ra.MergePair < 0)
            {
                if (!stepped) return;
                for (int k = 0; k < mergePairs.Count; k++)
                    if (before < mergePairs[k].A.StartMetres && here >= mergePairs[k].A.StartMetres)
                    {
                        float lat = a.Progress.Location.Lateral;
                        bool nearA = Mathf.Abs(lat - mergePairs[k].A.LineOffset) <= Mathf.Abs(lat - mergePairs[k].B.LineOffset);
                        ra.MergePair = k;
                        ra.MergeMine = nearA ? mergePairs[k].A : mergePairs[k].B;
                        ra.MergeTheirs = nearA ? mergePairs[k].B : mergePairs[k].A;
                        ra.MergeOk = true;
                        ra.MergeEntry = now;
                    }
                return;
            }
            RouteGateDef mine = ra.MergeMine, theirs = ra.MergeTheirs;
            if (!stepped || ra.LastReset >= ra.MergeEntry || !Live(a))
            {
                ra.Log($"{now:F1} s: {mine.Challenge} merge void at {here:F0} m — a recovery or a jump");
                ra.Merges.Add((mine.Challenge, false));
                ra.MergePair = -1;
                return;
            }
            if (ra.MergeOk)
            {
                RaceEntrant pace = entrants.FirstOrDefault(b => b.Roster.Role == "merge" && Live(b));
                float paceRoute = pace == null ? -1f : RouteDistance(pace.Progress.RaceDistance);
                // Beside: the pace car within MergeBesideMetres along the road; its lane is judged while it is inside the span.
                bool inSpan = paceRoute >= theirs.StartMetres && paceRoute <= theirs.EndMetres;
                string why = !InLane(a, mine) ? $"out of {mine.Id} ({a.Progress.Location.Lateral:F1} m)"
                    : pace == null ? "no pace car racing"
                    : Mathf.Abs(paceRoute - here) > MergeBesideMetres ? $"the pace car not beside it ({paceRoute - here:F0} m apart)"
                    : inSpan && !InLane(pace, theirs) ? $"the pace car out of {theirs.Id} ({pace.Progress.Location.Lateral:F1} m)"
                    : ra.LastTouch >= ra.MergeEntry ? "a car touched" : null;
                if (why != null)
                {
                    ra.MergeOk = false;
                    ra.Log($"{now:F1} s: {mine.Challenge} merge broken at {here:F0} m — {why}");
                }
            }
            if (before < mine.EndMetres && here >= mine.EndMetres)
            {
                ra.Merges.Add((mine.Challenge, ra.MergeOk));
                ra.Log($"{now:F1} s: {mine.Challenge} merge {(ra.MergeOk ? "kept, both cars in their lanes, no touch" : "not kept")}");
                ra.MergePair = -1;
            }
        }

        void Follow(int i, RaceEntrant a, RacecraftRun ra, double now)
        {
            ra.Ahead = -1;
            ra.Interval = -1f;
            if (!Live(a) || a.Progress.Finished) { ra.FollowTarget = -1; return; }
            float da = a.Progress.RaceDistance, nearest = float.MaxValue;
            for (int j = 0; j < entrants.Count; j++)
            {
                RaceEntrant b = entrants[j];
                if (j == i || b.Status != EntrantStatus.Racing) continue;
                float db = b.Progress.RaceDistance;
                if (db > da && db < nearest) { nearest = db; ra.Ahead = j; }
            }
            if (ra.Ahead >= 0 && Run(entrants[ra.Ahead]).History.IntervalBehind(da, now, out float gap)) ra.Interval = gap;

            RaceEntrant lead = ra.Ahead >= 0 ? entrants[ra.Ahead] : null;
            // A touch or a recovery this step breaks the follow; the next valid step starts a new one.
            bool valid = lead != null && Live(lead) && lead.State.Velocity.magnitude >= MovingMps &&
                         ra.Interval >= FollowMinSeconds && ra.Interval <= FollowMaxSeconds && ra.LastTouch < now && ra.LastReset < now;
            if (!valid || ra.Ahead != ra.FollowTarget)
            {
                ra.FollowTarget = valid ? ra.Ahead : -1;
                ra.FollowSince = now;
                return;
            }
            float held = (float)(now - ra.FollowSince);
            if (held > ra.FollowLongest)
            {
                ra.FollowLongest = held;
                ra.FollowLongestTarget = ra.Ahead;
            }
        }
    }
}
