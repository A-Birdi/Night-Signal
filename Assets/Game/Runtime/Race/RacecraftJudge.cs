using System.Collections.Generic;
using NightSignal.Core.Rules;
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

    /// <summary>One entrant's racecraft facts (CH31 Clean Pass, CH32 Patient Mirror).</summary>
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

        /// <summary>The car directly ahead (index; −1 none) and the interval to it (s; negative when not measurable).</summary>
        public int Ahead = -1;
        public float Interval = -1f;
        /// <summary>The current follow: its target, when it began, and the longest completed or running follow (s).</summary>
        public int FollowTarget = -1;
        public double FollowSince;
        public float FollowLongest;
        public int FollowLongestTarget = -1;
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
    /// </list>
    /// </summary>
    public sealed class RacecraftJudge
    {
        public const double CleanWindowSeconds = 2.0, HoldSeconds = 3.0;
        public const float FollowMinSeconds = 1f, FollowMaxSeconds = 2f;
        /// <summary>A parked or crashed car is not raced: the other car must be doing at least this (m/s, 18 km/h).</summary>
        public const float MovingMps = 5f;

        readonly List<RaceEntrant> entrants;

        public RacecraftJudge(List<RaceEntrant> entrants) => this.entrants = entrants;

        /// <summary>Only races with live opponents are judged (a drift or non-contact event has no racecraft).</summary>
        public static RacecraftJudge ForEvent(RaceEventRules rules, List<RaceEntrant> entrants) =>
            rules.Contact == ContactPolicy.NonContact || rules.DriftRanking ? null : new RacecraftJudge(entrants);

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
                        if (passed && b.State.Velocity.magnitude >= MovingMps && now - ra.LastTouch > CleanWindowSeconds) ra.Pending.Add((j, now));
                    }
                Confirm(a, ra, now);
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
                if (broken) { ra.Pending.RemoveAt(k); continue; }
                // The clean window after the pass (2 s) lies inside the hold (3 s); finishing ahead ends the hold early.
                double held = now - at;
                if (held >= HoldSeconds || (a.Progress.Finished && held >= CleanWindowSeconds))
                {
                    ra.CleanPasses.Add((j, at));
                    ra.Pending.RemoveAt(k);
                }
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
