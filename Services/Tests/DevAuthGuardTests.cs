using System.Net;
using NightSignal.ControlPlane.DevAuth;
using NightSignal.ControlPlane.Security;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>DevAuth must be impossible to enable outside Development or on a non-loopback binding.</summary>
public sealed class DevAuthGuardTests : IDisposable
{
    readonly TempDir dir = new();
    readonly string seed;

    public DevAuthGuardTests()
    {
        seed = dir.File("seed.json");
        TestData.WriteSeed(seed, 1);
    }

    public void Dispose() => dir.Dispose();

    static Exception StartupFailure(ControlPlaneHost host)
    {
        Exception e = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        while (e is not InvalidOperationException && e.InnerException is not null) e = e.InnerException;
        return e;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void RefusesToStart_OutsideDevelopment(string environment)
    {
        using var host = new ControlPlaneHost(dir.Path, seed, environment: environment);
        Exception e = StartupFailure(host);
        Assert.IsType<InvalidOperationException>(e);
        Assert.Contains("only runs in Development", e.Message);
    }

    [Theory]
    [InlineData("urls", "http://0.0.0.0:5080")]
    [InlineData("urls", "http://127.0.0.1:5080;http://*:5081")]
    [InlineData("urls", "http://+:5080")]
    [InlineData("urls", "http://[::]:5080")]
    [InlineData("urls", "http://gameserver.example.com:5080")]
    [InlineData("http_ports", "8080")]
    [InlineData("Kestrel:Endpoints:Public:Url", "http://0.0.0.0:6000")]
    public void RefusesToStart_OnNonLoopbackBinding(string key, string value)
    {
        using var host = new ControlPlaneHost(dir.Path, seed, overrides: new() { [key] = value });
        Exception e = StartupFailure(host);
        Assert.IsType<InvalidOperationException>(e);
        Assert.Contains("loopback", e.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5080", true)]
    [InlineData("http://localhost:5080", true)]
    [InlineData("https://[::1]:5443", true)]
    [InlineData("http://127.0.0.2:5080", true)]
    [InlineData("http://0.0.0.0:5080", false)]
    [InlineData("http://*:5080", false)]
    [InlineData("http://+:5080", false)]
    [InlineData("http://[::]:5080", false)]
    [InlineData("http://192.168.1.20:5080", false)]
    [InlineData("http://localhost.evil.example:5080", false)]
    public void LoopbackDetection(string url, bool loopback) => Assert.Equal(loopback, DevAuthGuard.IsLoopbackUrl(url));

    [Fact]
    public void BoundAddressCheck_ThrowsForWildcardBinding() =>
        Assert.Throws<InvalidOperationException>(() => DevAuthGuard.EnsureBoundAddressesAreLoopback(new[] { "http://127.0.0.1:5080", "http://[::]:5080" }));

    [Fact]
    public async Task Production_WithoutDevAuth_HasNoDevAuthEndpoints()
    {
        string pem = dir.File("ticket.pem");
        EcSigningKey.LoadOrCreate(pem, allowCreate: true).Dispose();
        using var host = new ControlPlaneHost(dir.Path, seed, environment: "Production", overrides: new()
        {
            ["DevAuth:Enabled"] = "false",
            ["Identity:Issuer"] = "https://example-project.supabase.co/auth/v1",
            ["Identity:JwksUrl"] = "https://example-project.supabase.co/auth/v1/.well-known/jwks.json",
            ["Tickets:SigningKeyFile"] = pem,
        });
        HttpClient client = host.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/dev/auth/token", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/auth/.well-known/jwks.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public void Production_RefusesToStart_WithoutTicketSigningKey()
    {
        using var host = new ControlPlaneHost(dir.Path, seed, environment: "Production", overrides: new()
        {
            ["DevAuth:Enabled"] = "false",
            ["Identity:Issuer"] = "https://example-project.supabase.co/auth/v1",
            ["Identity:JwksUrl"] = "https://example-project.supabase.co/auth/v1/.well-known/jwks.json",
        });
        Assert.Contains("SigningKeyFile", StartupFailure(host).Message);
    }
}
