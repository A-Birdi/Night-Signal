using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Matches;
using NightSignal.Core.Security;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// The Unity game server cannot use System.Security.Cryptography.ECDsa (not implemented on Unity's Mono), so it
/// validates tickets with Core's managed P256 + MatchTicketValidator. These tests prove parity with .NET's ECDSA
/// and with the control plane's real TicketIssuer.
/// </summary>
public sealed class CoreTicketParityTests : IDisposable
{
    readonly TempDir dir = new();
    readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    readonly TicketIssuer issuer;
    static readonly ClientVersion Version = new("build-7", 1, "content-abc");
    static readonly ActiveMatch Match = new("m_1", "srv", "127.0.0.1", 7777, Version, new[] { "acct-0001" });

    public CoreTicketParityTests()
    {
        issuer = new TicketIssuer(Options.Create(new TicketOptions()), Options.Create(new KeyOptions { DevKeyDirectory = dir.Path }),
            new TestHostEnvironment(dir.Path), clock);
    }

    public void Dispose()
    {
        issuer.Dispose();
        dir.Dispose();
    }

    MatchTicketValidator Validator(string match = "m_1") =>
        new(issuer.Issuer, MatchTicketValidator.ParseJwks(issuer.JwksJson()), match, "build-7", 1, "content-abc",
            () => clock.GetUtcNow().ToUnixTimeSeconds());

    [Fact]
    public void ControlPlaneTicket_ValidatesWithCoreValidator_OnceOnly()
    {
        MatchTicketValidator v = Validator();
        IssuedTicket t = issuer.Issue("acct-0001", "cv_1", Match, "racer");
        Assert.Equal(TicketFailure.None, v.Validate(t.Ticket, out MatchTicketClaims? claims));
        Assert.Equal("acct-0001", claims!.Subject);
        Assert.Equal("racer", claims.Role);
        Assert.Equal(TicketFailure.Replayed, v.Validate(t.Ticket, out _));
    }

    [Fact]
    public void CoreValidator_RejectsTamperedExpiredAndForeignMatchTickets()
    {
        IssuedTicket t = issuer.Issue("acct-0001", "cv_1", Match, "racer");
        string[] parts = t.Ticket.Split('.');
        char c = parts[2][10];
        string tampered = $"{parts[0]}.{parts[1]}.{parts[2][..10]}{(c == 'A' ? 'B' : 'A')}{parts[2][11..]}";
        Assert.Equal(TicketFailure.BadSignature, Validator().Validate(tampered, out _));
        Assert.Equal(TicketFailure.WrongMatch, Validator("m_other").Validate(t.Ticket, out _));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TicketFailure.Expired, Validator().Validate(t.Ticket, out _));
    }

    [Fact]
    public void ManagedP256_AgreesWithDotNetEcdsa_OnRandomSignatures()
    {
        for (int i = 0; i < 200; i++)
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ECParameters p = ec.ExportParameters(false);
            byte[] message = RandomNumberGenerator.GetBytes(1 + i);
            byte[] sig = ec.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            byte[] digest = SHA256.HashData(message);
            Assert.True(P256.Verify(p.Q.X!, p.Q.Y!, digest, sig), $"valid signature rejected (iteration {i})");
            sig[i % 64] ^= 0x40;
            Assert.False(P256.Verify(p.Q.X!, p.Q.Y!, digest, sig), $"tampered signature accepted (iteration {i})");
        }
    }
}
