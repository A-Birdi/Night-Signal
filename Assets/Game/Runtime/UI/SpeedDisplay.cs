using System;
using System.Globalization;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.UI
{
    public enum SpeedUnit { Kmh = 0, Mph = 1 }

    /// <summary>
    /// The one place road speed becomes a displayed number (Addendum 03 §1.2): canonical metres per second in, converted only
    /// for display — km/h = m/s × 3.6, mph = m/s ÷ 0.44704. Simulation, thresholds, assists, AI, rewards and camera effects
    /// keep consuming m/s; nothing here feeds back. Dial scales are chosen once per car/build from its speed envelope, in
    /// round intervals of the chosen unit (an mph dial and a km/h dial need not share a maximum; a needle over 60 mph is at
    /// the same physical speed as one over 96.56064 km/h).
    /// </summary>
    public static class SpeedDisplay
    {
        public const float MetresPerSecondPerMph = 0.44704f;
        public const float KmhPerMetresPerSecond = 3.6f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Speed magnitude in the display unit (reverse motion reads as its magnitude; non-finite reads 0).</summary>
        public static float Convert(float metresPerSecond, SpeedUnit unit)
        {
            if (float.IsNaN(metresPerSecond) || float.IsInfinity(metresPerSecond)) return 0f;
            float mps = Mathf.Abs(metresPerSecond);
            return unit == SpeedUnit.Mph ? mps / MetresPerSecondPerMph : mps * KmhPerMetresPerSecond;
        }

        /// <summary>Whole displayed units, rounded to nearest (96.56064 km/h shows 97).</summary>
        public static string Format(float metresPerSecond, SpeedUnit unit) =>
            Mathf.RoundToInt(Convert(metresPerSecond, unit)).ToString(Inv);

        public static string Label(SpeedUnit unit) => unit == SpeedUnit.Mph ? "mph" : "km/h";

        public static SpeedUnit Parse(string id) => id == "mph" ? SpeedUnit.Mph : SpeedUnit.Kmh;
        public static string Id(SpeedUnit unit) => unit == SpeedUnit.Mph ? "mph" : "kmh";

        /// <summary>
        /// The car's supported speed envelope (m/s): the lower of its drag-limited speed at peak power and its top-gear redline
        /// speed. An estimate for choosing a readable dial range — never a promised top speed, never a limit on the car.
        /// </summary>
        public static float EnvelopeMps(VehicleParams p)
        {
            if (p == null) return 70f;
            const float airDensity = 1.225f;
            float drag = Mathf.Pow(2f * p.PeakPowerKw * 1000f * p.DrivelineEfficiency / (airDensity * Mathf.Max(0.2f, p.DragAreaCdA)), 1f / 3f);
            float topGear = p.GearRatios != null && p.GearRatios.Length > 0 ? p.GearRatios[p.GearRatios.Length - 1] : 1f;
            float gear = p.RedlineRpm * 2f * Mathf.PI / 60f * p.WheelRadiusM / Mathf.Max(0.1f, topGear * p.FinalDrive);
            return Mathf.Min(drag, gear);
        }

        /// <summary>A dial/strip scale: maximum and major/minor tick intervals, all in the display unit.</summary>
        public struct Scale : IEquatable<Scale>
        {
            public SpeedUnit Unit;
            public float Max, Major, Minor;

            public bool Equals(Scale o) => Unit == o.Unit && Max == o.Max && Major == o.Major && Minor == o.Minor;
            public override bool Equals(object obj) => obj is Scale o && Equals(o);
            public override int GetHashCode() => ((int)Unit * 397) ^ Max.GetHashCode();
            public override string ToString() => $"0–{Max.ToString(Inv)} {Label(Unit)} (major {Major.ToString(Inv)}, minor {Minor.ToString(Inv)})";
        }

        /// <summary>
        /// Round scale for an envelope with 10 % headroom: km/h majors every 20 (every 40 above 260), minors every 10 (20);
        /// mph majors every 20, minors every 10 (every 5 up to 100). Never below 120 km/h / 80 mph.
        /// </summary>
        public static Scale ScaleFor(float envelopeMps, SpeedUnit unit)
        {
            float top = Mathf.Max(Convert(envelopeMps * 1.1f, unit), unit == SpeedUnit.Mph ? 80f : 120f);
            float major, minor;
            if (unit == SpeedUnit.Kmh)
            {
                major = top > 260f ? 40f : 20f;
                minor = major / 2f;
            }
            else
            {
                major = 20f;
                minor = top > 100f ? 10f : 5f;
            }
            return new Scale { Unit = unit, Max = Mathf.Ceil(top / major) * major, Major = major, Minor = minor };
        }

        /// <summary>Where the needle/fill sits: 0 at rest, 1 at the scale maximum, above 1 over range (shown bounded, never wrapped).</summary>
        public static float Fraction(float metresPerSecond, Scale scale) => scale.Max > 0f ? Convert(metresPerSecond, scale.Unit) / scale.Max : 0f;
    }
}
