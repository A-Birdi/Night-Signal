using System;
using System.Security.Cryptography;
using System.Text;
using NightSignal.Core.Security;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    /// <summary>
    /// P-256 verification against the RFC 6979 §A.2.5 deterministic test vectors (SHA-256). Cross-verification
    /// with .NET's ECDsa signing lives in Services/Tests (Unity's Mono cannot sign ECDSA).
    /// </summary>
    public sealed class P256Tests
    {
        static readonly byte[] Ux = Hex("60FED4BA255A9D31C961EB74C6356D68C049B8923B61FA6CE669622E60F29FB6");
        static readonly byte[] Uy = Hex("7903FE1008B8BC99A41AE9E95628BC64F2F1B20C2D7E9F5177A3C294D4462299");

        static byte[] Sha(string m)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(m));
        }

        static byte[] Sig(string r, string s)
        {
            var sig = new byte[64];
            Buffer.BlockCopy(Hex(r), 0, sig, 0, 32);
            Buffer.BlockCopy(Hex(s), 0, sig, 32, 32);
            return sig;
        }

        [Test]
        public void Rfc6979_Sample_Verifies()
        {
            byte[] sig = Sig("EFD48B2AACB6A8FD1140DD9CD45E81D69D2C877B56AAF991C34D0EA84EAF3716",
                             "F7CB1C942D657C41D436C7A1B6E29F65F3E900DBB9AFF4064DC4AB2F843ACDA8");
            Assert.That(P256.Verify(Ux, Uy, Sha("sample"), sig), Is.True);
        }

        [Test]
        public void Rfc6979_Test_Verifies()
        {
            byte[] sig = Sig("F1ABB023518351CD71D881567B1EA663ED3EFCF6C5132B354F28D3B0B7D38367",
                             "019F4113742A2B14BD25926B49C649155F267E60D3814B4C0CC84250E46F0083");
            Assert.That(P256.Verify(Ux, Uy, Sha("test"), sig), Is.True);
        }

        [Test]
        public void TamperedMessageSignatureOrKey_AreRejected()
        {
            byte[] sig = Sig("EFD48B2AACB6A8FD1140DD9CD45E81D69D2C877B56AAF991C34D0EA84EAF3716",
                             "F7CB1C942D657C41D436C7A1B6E29F65F3E900DBB9AFF4064DC4AB2F843ACDA8");
            Assert.That(P256.Verify(Ux, Uy, Sha("samplf"), sig), Is.False, "message changed");
            byte[] bad = (byte[])sig.Clone();
            bad[40] ^= 0x01;
            Assert.That(P256.Verify(Ux, Uy, Sha("sample"), bad), Is.False, "signature changed");
            byte[] badKey = (byte[])Uy.Clone();
            badKey[31] ^= 0x01;
            Assert.That(P256.Verify(Ux, badKey, Sha("sample"), sig), Is.False, "point not on curve");
            Assert.That(P256.Verify(Ux, Uy, Sha("sample"), new byte[64]), Is.False, "zero r/s");
        }

        static byte[] Hex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
