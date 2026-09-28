using NightSignal.Net;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    /// <summary>
    /// Addendum 04 A04-01/A04-05: the game server's listen address (BindHost) is separate from the address it advertises
    /// (PublicHost); both default to the IPv4 loopback; parsing never cross-assigns them; an empty, non-numeric or
    /// unreachable pair is refused before any socket starts; a LAN or wildcard bind is only what was explicitly asked for.
    /// </summary>
    public sealed class NetConfigTests
    {
        [Test]
        public void Defaults_AreLoopbackOnly()
        {
            NetConfig c = NetConfig.Parse(new string[0]);
            Assert.That(c.BindHost, Is.EqualTo("127.0.0.1"));
            Assert.That(c.PublicHost, Is.EqualTo("127.0.0.1"));
            Assert.That(c.Port, Is.EqualTo(7777));
            Assert.That(c.AllowLan, Is.False);
            Assert.That(c.BindClass(), Is.EqualTo("local"));
            Assert.That(c.BindProblem(), Is.Null);
        }

        [Test]
        public void BindAndPublicHosts_AreParsedSeparately()
        {
            NetConfig c = NetConfig.Parse(new[] { "game.exe", "-nsBindHost", "192.168.1.20", "-nsPublicHost", "192.168.1.21", "-nsAllowLan" });
            Assert.That(c.BindHost, Is.EqualTo("192.168.1.20"));
            Assert.That(c.PublicHost, Is.EqualTo("192.168.1.21"));
            Assert.That(c.AllowLan, Is.True);
            Assert.That(c.BindClass(), Is.EqualTo("lan"));
            Assert.That(c.BindProblem(), Is.Null, "an explicit LAN bind with its own advertised address is a valid pair");

            NetConfig onlyPublic = NetConfig.Parse(new[] { "game.exe", "-nsPublicHost", "127.0.0.1" });
            Assert.That(onlyPublic.BindHost, Is.EqualTo("127.0.0.1"), "setting the advertised host never moves the listener");
            NetConfig onlyBind = NetConfig.Parse(new[] { "game.exe", "-nsBindHost", "0.0.0.0" });
            Assert.That(onlyBind.PublicHost, Is.EqualTo("127.0.0.1"), "setting the listener never changes what is advertised");
            Assert.That(onlyBind.BindClass(), Is.EqualTo("wildcard"));
            Assert.That(onlyBind.AllowLan, Is.False);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("localhost")]
        [TestCase("my-pc.lan")]
        [TestCase("999.1.1.1")]
        public void InvalidBindAddresses_AreRefusedBeforeTheSocket(string bind)
        {
            NetConfig c = NetConfig.Parse(new[] { "game.exe", "-nsBindHost", bind });
            Assert.That(c.BindProblem(), Is.Not.Null);
            Assert.That(c.BindClass(), Is.EqualTo("invalid"));
        }

        [Test]
        public void LoopbackBind_AdvertisingAnotherAddress_IsRefused()
        {
            NetConfig c = NetConfig.Parse(new[] { "game.exe", "-nsBindHost", "127.0.0.1", "-nsPublicHost", "10.0.0.75" });
            StringAssert.Contains("loopback", c.BindProblem());
        }

        [Test]
        public void Classification_TellsLocalLanWildcardAndWan()
        {
            Assert.That(NetConfig.Parse(new[] { "g", "-nsBindHost", "::1" }).BindClass(), Is.EqualTo("local"));
            Assert.That(NetConfig.Parse(new[] { "g", "-nsBindHost", "10.0.0.75" }).BindClass(), Is.EqualTo("lan"));
            Assert.That(NetConfig.Parse(new[] { "g", "-nsBindHost", "172.20.1.1" }).BindClass(), Is.EqualTo("lan"));
            Assert.That(NetConfig.Parse(new[] { "g", "-nsBindHost", "::" }).BindClass(), Is.EqualTo("wildcard"));
            Assert.That(NetConfig.Parse(new[] { "g", "-nsBindHost", "8.8.8.8" }).BindClass(), Is.EqualTo("wan"));
        }
    }
}
