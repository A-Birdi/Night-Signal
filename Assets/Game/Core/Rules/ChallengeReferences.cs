using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    /// <summary>A published gate reference: an exit-speed floor, or a braking zone's exit-speed window and release point.</summary>
    public sealed class GateSpeedReference
    {
        public string Challenge, Course, Gate;
        /// <summary>exit-speed | brake-zone (the route gate's kind).</summary>
        public string Kind;
        /// <summary>km/h: an exit gate's floor; a braking zone's window at the zone's end (MaxKmh 0 = no ceiling).</summary>
        public float MinKmh, MaxKmh;
        /// <summary>Route metres by which braking must have begun inside the zone — the position envelope (0 = not judged).</summary>
        public float BrakeByMetres;
    }

    /// <summary>A published race-time reference (CH10's Silver time on C11 and its lap-split tolerance).</summary>
    public sealed class TimeReference
    {
        public string Challenge, Course, Tier;
        public long ReferenceMs;
        public long MaxLapDifferenceMs;
    }

    /// <summary>A published drift reference (a fixed Gold raw score on a course and surface).</summary>
    public sealed class DriftReference
    {
        public string Challenge, Course, Surface = "", Tier;
        public long Raw;
        /// <summary>Largest share of earned raw points that may be lost unbanked (0 = not judged).</summary>
        public double MaxLostFraction;
    }

    public sealed class ChallengeReferencesFile
    {
        public string Schema, Method;
        public List<GateSpeedReference> Gates = new List<GateSpeedReference>();
        public List<TimeReference> Times = new List<TimeReference>();
        public List<DriftReference> Drift = new List<DriftReference>();
    }

    /// <summary>What a car did at one tagged speed gate (measured by the race's gate judge).</summary>
    public struct GateSpeedFact
    {
        public bool Crossed;
        /// <summary>km/h crossing an exit gate; at a braking zone's start and end.</summary>
        public float SpeedKmh, EntryKmh, ExitKmh;
        public bool Braked;
        /// <summary>Route metres where braking began inside the zone, and where it was last released (-1 = none / still braking at its end).</summary>
        public float BrakeOnMetres, ReleaseMetres;
        /// <summary>Meaningful wall impacts, car contacts and resets inside the zone.</summary>
        public int WallsInside, ContactsInside, ResetsInside;
    }

    /// <summary>
    /// Appendix E challenges judged against published references (authored/challenge-references.json, measured by the
    /// PlayMode reference run and part of the content hash): CH04 The Exit Matters (C02's three uphill exit gates above
    /// their Bronze floors), CH08 Wet Window (C08's two wet braking zones inside their speed/position envelopes, no wall
    /// incident), CH12 The Last Ten Metres (C20's four late-braking gates inside their windows, no collision or reset in
    /// them), CH10 Equal Splits (C11's two laps within 2.0 s of each other and inside the Silver time), CH26 Needle in the
    /// Rain (beat the wet C12 Gold drift reference, no meaningful wall impact), CH29 No Wasted Motion (bank the C24 Gold raw
    /// target losing at most 5 % of the earned raw, no reset). Engine-free; the same offline and on the game server.
    /// </summary>
    public static class ChallengeReferenceJudge
    {
        /// <summary>Every published gate of <paramref name="challenge"/> on the course passed as its kind requires (false when none).</summary>
        public static bool GatesPassed(ChallengeReferencesFile refs, string challenge, string courseId, Func<string, GateSpeedFact?> fact, bool contactFree = false)
        {
            List<GateSpeedReference> gates = refs?.Gates?.Where(g => g.Challenge == challenge && g.Course == courseId).ToList();
            if (gates == null || gates.Count == 0) return false;
            foreach (GateSpeedReference g in gates)
            {
                GateSpeedFact? f = fact(g.Gate);
                if (f == null || !f.Value.Crossed) return false;
                GateSpeedFact v = f.Value;
                if (g.Kind == "exit-speed")
                {
                    if (v.SpeedKmh < g.MinKmh) return false;
                }
                else
                {
                    if (!v.Braked || v.ExitKmh < g.MinKmh || (g.MaxKmh > 0f && v.ExitKmh > g.MaxKmh)) return false;
                    if (g.BrakeByMetres > 0f && (v.BrakeOnMetres < 0f || v.BrakeOnMetres > g.BrakeByMetres)) return false;
                    if (v.ResetsInside > 0 || (contactFree && (v.WallsInside > 0 || v.ContactsInside > 0))) return false;
                }
            }
            return true;
        }

        /// <summary>CH10: every lap within <see cref="TimeReference.MaxLapDifferenceMs"/> of the others and the race inside the reference.</summary>
        public static bool EqualSplits(ChallengeReferencesFile refs, string challenge, string courseId, IReadOnlyList<long> lapMicros, long finishMicros)
        {
            TimeReference r = refs?.Times?.FirstOrDefault(t => t.Challenge == challenge && t.Course == courseId);
            if (r == null || lapMicros == null || lapMicros.Count < 2 || finishMicros <= 0) return false;
            return lapMicros.Max() - lapMicros.Min() <= r.MaxLapDifferenceMs * 1000L && finishMicros <= r.ReferenceMs * 1000L;
        }

        /// <summary>CH26 / CH29: the published drift reference on its course and surface beaten (banked raw), lost share within bounds.</summary>
        public static bool BeatsDrift(ChallengeReferencesFile refs, string challenge, string courseId, string surface, double bankedRaw, double earnedRaw, double lostRaw)
        {
            DriftReference r = refs?.Drift?.FirstOrDefault(d => d.Challenge == challenge && d.Course == courseId);
            if (r == null || bankedRaw <= 0) return false;
            if (!string.IsNullOrEmpty(r.Surface) && !string.Equals(r.Surface, string.IsNullOrEmpty(surface) ? "dry" : surface, StringComparison.Ordinal)) return false;
            if (bankedRaw < r.Raw) return false;
            return r.MaxLostFraction <= 0 || earnedRaw <= 0 || lostRaw <= r.MaxLostFraction * earnedRaw;
        }

        /// <summary>Why a references document cannot be used (empty when it can).</summary>
        public static List<string> Problems(ChallengeReferencesFile f, Func<string, bool> courseExists, Func<string, bool> challengeExists)
        {
            var p = new List<string>();
            foreach (GateSpeedReference g in f.Gates ?? new List<GateSpeedReference>())
            {
                if (!challengeExists(g.Challenge ?? "") || !courseExists(g.Course ?? "") || string.IsNullOrEmpty(g.Gate)) p.Add($"gate reference {g.Challenge}/{g.Gate}: unknown challenge, course or gate");
                if (g.Kind != "exit-speed" && g.Kind != "brake-zone") p.Add($"gate reference {g.Gate}: kind must be exit-speed or brake-zone");
                if (!(g.MinKmh > 0f) || (g.MaxKmh > 0f && g.MaxKmh < g.MinKmh)) p.Add($"gate reference {g.Gate}: invalid speed window");
            }
            foreach (TimeReference t in f.Times ?? new List<TimeReference>())
                if (!challengeExists(t.Challenge ?? "") || !courseExists(t.Course ?? "") || t.ReferenceMs <= 0 || t.MaxLapDifferenceMs <= 0) p.Add($"time reference {t.Challenge}: invalid");
            foreach (DriftReference d in f.Drift ?? new List<DriftReference>())
                if (!challengeExists(d.Challenge ?? "") || !courseExists(d.Course ?? "") || d.Raw <= 0 || d.MaxLostFraction < 0 || d.MaxLostFraction >= 1) p.Add($"drift reference {d.Challenge}: invalid");
            return p;
        }
    }
}
