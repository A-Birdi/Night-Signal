using System;

namespace NightSignal.Core.Toys
{
    /// <summary>
    /// Small engine-free 2D vector for toy simulations (Unity types are not available in Core). Computed values are
    /// methods, not properties, so the struct serializes as exactly two numbers.
    /// </summary>
    public struct Vec2 : IEquatable<Vec2>
    {
        public double X;
        public double Y;

        public Vec2(double x, double y) { X = x; Y = y; }

        public static readonly Vec2 Zero = new Vec2(0, 0);

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
        public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator *(double s, Vec2 a) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator /(Vec2 a, double s) => new Vec2(a.X / s, a.Y / s);

        public double Dot(Vec2 b) => X * b.X + Y * b.Y;
        public double Cross(Vec2 b) => X * b.Y - Y * b.X;
        public double LengthSquared() => X * X + Y * Y;
        public double Length() => Math.Sqrt(X * X + Y * Y);
        /// <summary>Left-hand perpendicular (rotated +90°).</summary>
        public Vec2 Perp() => new Vec2(-Y, X);

        public Vec2 Normalized()
        {
            double l = Length();
            return l > 1e-12 ? new Vec2(X / l, Y / l) : Zero;
        }

        public static Vec2 FromAngle(double radians) => new Vec2(Math.Cos(radians), Math.Sin(radians));

        public bool IsFinite() => ToyMath.Finite(X) && ToyMath.Finite(Y);

        public bool Equals(Vec2 other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object obj) => obj is Vec2 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() * 397 ^ Y.GetHashCode();
        public override string ToString() => "(" + X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                                             Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>Small engine-free 3D vector (x, y = table plane, z = up) for toy geometry.</summary>
    public struct Vec3 : IEquatable<Vec3>
    {
        public double X;
        public double Y;
        public double Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);

        public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

        public Vec3 Normalized()
        {
            double l = Length();
            return l > 1e-12 ? new Vec3(X / l, Y / l, Z / l) : new Vec3(0, 0, 0);
        }

        public Vec2 XY() => new Vec2(X, Y);

        public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => (X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode();
    }

    public static class ToyMath
    {
        public const double Deg2Rad = Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / Math.PI;

        public static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        public static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);

        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>Smoothstep ease (0→1) with zero slope at both ends.</summary>
        public static double Ease(double u) { u = Clamp(u, 0, 1); return u * u * (3 - 2 * u); }
        public static double EaseSlope(double u) { u = Clamp(u, 0, 1); return 6 * u * (1 - u); }
        public static double EaseCurve(double u) { u = Clamp(u, 0, 1); return 6 - 12 * u; }

        /// <summary>Wraps an angle to (−π, π].</summary>
        public static double WrapPi(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a <= -Math.PI) a += 2 * Math.PI;
            return a;
        }

        /// <summary>Median of a small list (copy-sorted; the input is not modified).</summary>
        public static double Median(System.Collections.Generic.IReadOnlyList<double> values)
        {
            int n = values.Count;
            if (n == 0) return double.NaN;
            var copy = new double[n];
            for (int i = 0; i < n; i++) copy[i] = values[i];
            Array.Sort(copy);
            return (n & 1) == 1 ? copy[n / 2] : 0.5 * (copy[n / 2 - 1] + copy[n / 2]);
        }
    }

    /// <summary>
    /// Deterministic SplitMix64 generator. The authoritative host owns the seed stream (persisted in the session
    /// snapshot); clients only ever receive derived per-attempt seeds.
    /// </summary>
    public struct ToyRandom
    {
        public ulong State;

        public ToyRandom(ulong seed) { State = seed; }

        public ulong NextUInt64()
        {
            unchecked
            {
                State += 0x9E3779B97F4A7C15UL;
                ulong z = State;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>Pure function: the i-th value of a seed's stream, for rules that must be recomputed on both ends.</summary>
        public static double Unit(ulong seed, int index)
        {
            var r = new ToyRandom(seed ^ (0xA0761D6478BD642FUL * (ulong)(index + 1)));
            return r.NextDouble();
        }
    }
}
