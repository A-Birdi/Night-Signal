using System;
using System.Collections.Generic;

namespace NightSignal.Core.Rules
{
    /// <summary>One fixed simulation step of server-observed driving facts.</summary>
    public struct DriftSample
    {
        public float DeltaSeconds;
        public float SpeedKmh;
        /// <summary>Signed slip angle between heading and velocity, degrees.</summary>
        public float SlipAngleDegrees;
        /// <summary>Distance along the legal course centreline for the current lap, metres.</summary>
        public double ProgressMetres;
        public bool MovingInLegalDirection;
        public bool OnRoad;
        /// <summary>Onset of a meaningful wall impact this step (already debounced by the caller).</summary>
        public bool WallImpact;
        public bool Reset;
        /// <summary>Route-owned judged zone ID, or -1 outside judged zones.</summary>
        public int JudgedZone;
        /// <summary>Lateral distance from the zone's intended arc/clip line, metres.</summary>
        public float LineOffsetMetres;
        /// <summary>The zone's line tolerance, metres (offset ≥ tolerance gives the minimum line factor).</summary>
        public float LineToleranceMetres;
    }

    public enum ChainEnd { None = 0, Banked = 1, LostWall = 2, LostOffCourse = 3, LostReset = 4, LostSpin = 5 }

    public struct DriftStepResult
    {
        public double RawDelta;
        public ChainEnd ChainEnd;
        public double AmountBankedOrLost;
    }

    /// <summary>
    /// Raw drift scoring, spec §7. Per step: rawDelta = legalForwardMetres × 100 × angleFactor × lineFactor × chain.
    /// Documented curves:
    /// <list type="bullet">
    /// <item>angleFactor(|slip|): 0 below 10°, linear to 1.00 at 25°, linear to 1.25 at 45°, linear to 0 at 80°, 0 beyond.</item>
    /// <item>lineFactor: 1.25 on the intended line, linear to 0.80 at the zone tolerance, 0.80 beyond.</item>
    /// <item>chain: 1.00 for the first zone, +0.25 per further distinct linked zone, hard maximum 3.00.</item>
    /// </list>
    /// A valid scoring step needs ≥35 km/h, legal direction, road contact, a judged zone, and forward progress
    /// beyond the lap's high-water mark (so spins, donuts, reverse loops and re-entry score nothing new).
    /// A chain banks after 1.0 s without a valid step (straightening interval) or at a judged-sector end;
    /// wall impacts, leaving the course, resets and spins lose only the unbanked chain.
    /// </summary>
    /// <summary>One banked drift chain: its raw points and the slowest valid scoring step in it.</summary>
    public struct BankedChain
    {
        public double Raw;
        public float MinSpeedKmh;
    }

    public sealed class DriftScorer
    {
        public const float MinimumSpeedKmh = 35f;
        public const float StraighteningIntervalSeconds = 1.0f;
        public const float SpinAngleDegrees = 100f;
        public const float SpinHoldSeconds = 0.3f;
        public const double ChainStep = 0.25;
        public const double MaxChain = 3.0;

        readonly HashSet<int> zonesInChain = new HashSet<int>();
        double highWaterMetres;
        float secondsSinceValid;
        float secondsSpinning;

        public double BankedRaw { get; private set; }
        public double UnbankedRaw { get; private set; }
        public double LostRaw { get; private set; }
        public double EarnedRaw => BankedRaw + UnbankedRaw + LostRaw;
        public double ChainMultiplier => zonesInChain.Count <= 1 ? 1.0 : Math.Min(MaxChain, 1.0 + ChainStep * (zonesInChain.Count - 1));
        public int ChainLength => zonesInChain.Count;
        public int ChainsBanked { get; private set; }
        /// <summary>Every banked chain in order: its raw points and the slowest valid scoring step in it (km/h) — the
        /// facts drift challenges read (CH18 two chains, CH24 one chain above a speed floor).</summary>
        public readonly List<BankedChain> BankedChains = new List<BankedChain>();
        float chainMinSpeed = float.MaxValue;

        public static double AngleFactor(double slipDegrees)
        {
            double a = Math.Abs(slipDegrees);
            if (a < 10) return 0;
            if (a <= 25) return (a - 10) / 15.0;
            if (a <= 45) return 1.0 + 0.25 * (a - 25) / 20.0;
            if (a <= 80) return 1.25 * (80 - a) / 35.0;
            return 0;
        }

        public static double LineFactor(double offsetMetres, double toleranceMetres)
        {
            if (toleranceMetres <= 0) throw new ArgumentOutOfRangeException(nameof(toleranceMetres));
            double t = Math.Min(1.0, Math.Abs(offsetMetres) / toleranceMetres);
            return 1.25 - 0.45 * t;
        }

        /// <summary>Showcase score is display-only; raw score drives qualification, challenges and records.</summary>
        public static long ShowcaseScore(double raw, int showcaseUtilityPercent)
        {
            if (showcaseUtilityPercent != 0 && showcaseUtilityPercent != 5 && showcaseUtilityPercent != 10)
                throw new ArgumentOutOfRangeException(nameof(showcaseUtilityPercent));
            return (long)Math.Floor(raw * (100 + showcaseUtilityPercent) / 100.0);
        }

        public DriftStepResult Step(DriftSample s)
        {
            var result = new DriftStepResult();

            if (s.Reset) return Lose(ref result, ChainEnd.LostReset, s);
            if (s.WallImpact) return Lose(ref result, ChainEnd.LostWall, s);
            if (!s.OnRoad) return Lose(ref result, ChainEnd.LostOffCourse, s);

            float absSlip = Math.Abs(s.SlipAngleDegrees);
            secondsSpinning = absSlip >= SpinAngleDegrees ? secondsSpinning + s.DeltaSeconds : 0f;
            if (secondsSpinning >= SpinHoldSeconds && UnbankedRaw > 0)
                return Lose(ref result, ChainEnd.LostSpin, s);

            double forward = Math.Max(0.0, s.ProgressMetres - highWaterMetres);
            if (s.ProgressMetres > highWaterMetres)
                highWaterMetres = s.ProgressMetres;

            bool valid = forward > 0 && s.MovingInLegalDirection && s.SpeedKmh >= MinimumSpeedKmh &&
                         s.JudgedZone >= 0 && AngleFactor(absSlip) > 0;
            if (valid)
            {
                zonesInChain.Add(s.JudgedZone);
                if (s.SpeedKmh < chainMinSpeed) chainMinSpeed = s.SpeedKmh;
                double delta = forward * 100.0 * AngleFactor(absSlip) *
                               LineFactor(s.LineOffsetMetres, s.LineToleranceMetres) * ChainMultiplier;
                UnbankedRaw += delta;
                result.RawDelta = delta;
                secondsSinceValid = 0f;
                return result;
            }

            secondsSinceValid += s.DeltaSeconds;
            if (UnbankedRaw > 0 && secondsSinceValid > StraighteningIntervalSeconds)
                Bank(ref result);
            return result;
        }

        /// <summary>Called when the car crosses the end gate of a judged sector.</summary>
        public DriftStepResult BankAtSectorEnd()
        {
            var result = new DriftStepResult();
            if (UnbankedRaw > 0) Bank(ref result);
            return result;
        }

        /// <summary>Called at a legal finish; any clean unbanked chain is banked.</summary>
        public DriftStepResult BankAtFinish() => BankAtSectorEnd();

        /// <summary>Circuits: progress restarts each lap, so the visited high-water mark does too.</summary>
        public void StartNewLap()
        {
            highWaterMetres = 0;
        }

        void Bank(ref DriftStepResult result)
        {
            result.ChainEnd = ChainEnd.Banked;
            result.AmountBankedOrLost = UnbankedRaw;
            BankedRaw += UnbankedRaw;
            BankedChains.Add(new BankedChain { Raw = UnbankedRaw, MinSpeedKmh = chainMinSpeed });
            UnbankedRaw = 0;
            ChainsBanked++;
            ClearChain();
        }

        DriftStepResult Lose(ref DriftStepResult result, ChainEnd reason, DriftSample s)
        {
            if (s.ProgressMetres > highWaterMetres)
                highWaterMetres = s.ProgressMetres;
            if (UnbankedRaw > 0)
            {
                result.ChainEnd = reason;
                result.AmountBankedOrLost = UnbankedRaw;
                LostRaw += UnbankedRaw;
                UnbankedRaw = 0;
            }
            ClearChain();
            return result;
        }

        void ClearChain()
        {
            chainMinSpeed = float.MaxValue;
            zonesInChain.Clear();
            secondsSinceValid = 0f;
            secondsSpinning = 0f;
        }
    }
}
