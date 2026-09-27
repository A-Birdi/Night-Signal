using System;
using System.Numerics;

namespace NightSignal.Core.Security
{
    /// <summary>
    /// ECDSA P-256 (secp256r1) signature verification in managed code. Unity's Mono runtime does not implement
    /// <c>ECDsa</c>, so the game server verifies ES256 match tickets with this. Verify-only (no secret material),
    /// Jacobian coordinates, curve parameters from SEC 2 / FIPS 186-4. Cross-tested against .NET's ECDsa in
    /// Services/Tests.
    /// </summary>
    public static class P256
    {
        static readonly BigInteger P = Hex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
        static readonly BigInteger N = Hex("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
        static readonly BigInteger B = Hex("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");
        static readonly BigInteger Gx = Hex("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296");
        static readonly BigInteger Gy = Hex("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5");

        /// <summary>Verifies a raw 64-byte (r‖s, IEEE P1363) signature over a SHA-256 digest.</summary>
        public static bool Verify(byte[] publicX, byte[] publicY, byte[] sha256Digest, byte[] signature64)
        {
            if (publicX == null || publicY == null || sha256Digest == null || signature64 == null) return false;
            if (publicX.Length != 32 || publicY.Length != 32 || sha256Digest.Length != 32 || signature64.Length != 64) return false;

            BigInteger qx = FromBigEndian(publicX, 0, 32), qy = FromBigEndian(publicY, 0, 32);
            BigInteger r = FromBigEndian(signature64, 0, 32), s = FromBigEndian(signature64, 32, 32);
            if (r.Sign <= 0 || r >= N || s.Sign <= 0 || s >= N) return false;
            if (qx >= P || qy >= P || !OnCurve(qx, qy)) return false;

            BigInteger e = FromBigEndian(sha256Digest, 0, 32);
            BigInteger w = BigInteger.ModPow(s, N - 2, N);
            BigInteger u1 = e * w % N, u2 = r * w % N;

            Jacobian x = ShamirMultiply(u1, new Jacobian(Gx, Gy, 1), u2, new Jacobian(qx, qy, 1));
            if (x.IsInfinity) return false;
            BigInteger zInv = BigInteger.ModPow(x.Z, P - 2, P);
            BigInteger ax = Mod(x.X * zInv * zInv);
            return ax % N == r;
        }

        static bool OnCurve(BigInteger x, BigInteger y) =>
            Mod(y * y) == Mod(x * x * x - 3 * x + B);

        struct Jacobian
        {
            public BigInteger X, Y, Z;
            public Jacobian(BigInteger x, BigInteger y, BigInteger z) { X = x; Y = y; Z = z; }
            public bool IsInfinity => Z.IsZero;
            public static Jacobian Infinity => new Jacobian(1, 1, 0);
        }

        static Jacobian ShamirMultiply(BigInteger k1, Jacobian p1, BigInteger k2, Jacobian p2)
        {
            Jacobian sum = Add(p1, p2);
            Jacobian acc = Jacobian.Infinity;
            int bits = Math.Max(BitLength(k1), BitLength(k2));
            for (int i = bits - 1; i >= 0; i--)
            {
                acc = Double(acc);
                bool b1 = !(k1 >> i).IsEven, b2 = !(k2 >> i).IsEven;
                if (b1 && b2) acc = Add(acc, sum);
                else if (b1) acc = Add(acc, p1);
                else if (b2) acc = Add(acc, p2);
            }
            return acc;
        }

        static Jacobian Double(Jacobian p)
        {
            if (p.IsInfinity || p.Y.IsZero) return Jacobian.Infinity;
            // a = -3: M = 3(X − Z²)(X + Z²)
            BigInteger zz = Mod(p.Z * p.Z);
            BigInteger m = Mod(3 * (p.X - zz) * (p.X + zz));
            BigInteger yy = Mod(p.Y * p.Y);
            BigInteger s = Mod(4 * p.X * yy);
            BigInteger x3 = Mod(m * m - 2 * s);
            BigInteger y3 = Mod(m * (s - x3) - 8 * yy * yy);
            BigInteger z3 = Mod(2 * p.Y * p.Z);
            return new Jacobian(x3, y3, z3);
        }

        static Jacobian Add(Jacobian p, Jacobian q)
        {
            if (p.IsInfinity) return q;
            if (q.IsInfinity) return p;
            BigInteger z1z1 = Mod(p.Z * p.Z), z2z2 = Mod(q.Z * q.Z);
            BigInteger u1 = Mod(p.X * z2z2), u2 = Mod(q.X * z1z1);
            BigInteger s1 = Mod(p.Y * q.Z * z2z2), s2 = Mod(q.Y * p.Z * z1z1);
            if (u1 == u2) return s1 == s2 ? Double(p) : Jacobian.Infinity;
            BigInteger h = Mod(u2 - u1), r = Mod(s2 - s1);
            BigInteger hh = Mod(h * h), hhh = Mod(h * hh);
            BigInteger v = Mod(u1 * hh);
            BigInteger x3 = Mod(r * r - hhh - 2 * v);
            BigInteger y3 = Mod(r * (v - x3) - s1 * hhh);
            BigInteger z3 = Mod(h * p.Z * q.Z);
            return new Jacobian(x3, y3, z3);
        }

        static BigInteger Mod(BigInteger a)
        {
            BigInteger r = a % P;
            return r.Sign < 0 ? r + P : r;
        }

        static int BitLength(BigInteger v)
        {
            int bits = 0;
            while (!v.IsZero) { v >>= 1; bits++; }
            return bits;
        }

        public static BigInteger FromBigEndian(byte[] data, int offset, int length)
        {
            var le = new byte[length + 1]; // trailing zero keeps the value non-negative
            for (int i = 0; i < length; i++) le[i] = data[offset + length - 1 - i];
            return new BigInteger(le);
        }

        static BigInteger Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return FromBigEndian(bytes, 0, bytes.Length);
        }
    }
}
