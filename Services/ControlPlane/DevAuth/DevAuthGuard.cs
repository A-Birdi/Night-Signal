using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;

namespace NightSignal.ControlPlane.DevAuth;

/// <summary>
/// DevAuth (a local password-based token issuer) must never be reachable on a production or network-facing
/// endpoint (spec §3.2). It is allowed only when the host environment is Development AND every configured and
/// actually bound address is loopback. Violations throw during startup; there is no override switch.
/// </summary>
public static class DevAuthGuard
{
    public static void EnsureAllowed(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                $"DevAuth is enabled but the environment is '{environment.EnvironmentName}'. DevAuth only runs in Development.");
        foreach (string url in ConfiguredUrls(configuration))
            if (!IsLoopbackUrl(url))
                throw new InvalidOperationException(
                    $"DevAuth is enabled but the server is configured to listen on '{url}'. Bind to loopback only (127.0.0.1, [::1] or localhost).");
    }

    public static void EnsureBoundAddressesAreLoopback(IEnumerable<string> addresses)
    {
        foreach (string address in addresses)
            if (!IsLoopbackUrl(address))
                throw new InvalidOperationException($"DevAuth is enabled but the server is bound to non-loopback address '{address}'.");
    }

    /// <summary>Every listen address the configuration can produce (urls, *_ports, Kestrel endpoints).</summary>
    public static IEnumerable<string> ConfiguredUrls(IConfiguration c)
    {
        foreach (string key in new[] { "urls", "URLS" })
            if (!string.IsNullOrWhiteSpace(c[key]))
                foreach (string url in c[key]!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return url;
        // ASPNETCORE_HTTP_PORTS / HTTPS_PORTS bind every interface.
        foreach (string key in new[] { "http_ports", "https_ports" })
            if (!string.IsNullOrWhiteSpace(c[key]))
                foreach (string port in c[key]!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return $"http://*:{port}";
        foreach (IConfigurationSection endpoint in c.GetSection("Kestrel:Endpoints").GetChildren())
            if (!string.IsNullOrWhiteSpace(endpoint["Url"]))
                yield return endpoint["Url"]!;
    }

    /// <summary>True only for explicit loopback hosts. Wildcards (*, +, 0.0.0.0, [::]) and other names are rejected.</summary>
    public static bool IsLoopbackUrl(string url)
    {
        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        string rest = schemeEnd >= 0 ? url[(schemeEnd + 3)..] : url;
        int slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];
        string host;
        if (rest.StartsWith('['))
        {
            int close = rest.IndexOf(']');
            if (close < 0) return false;
            host = rest[1..close];
        }
        else
        {
            int colon = rest.LastIndexOf(':');
            host = colon >= 0 ? rest[..colon] : rest;
        }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip);
    }
}

/// <summary>Re-checks the addresses Kestrel actually bound once the server has started.</summary>
internal sealed class DevAuthBindingCheck(IServer server, IOptions<DevAuthOptions> options) : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Enabled && server.Features.Get<IServerAddressesFeature>() is { } feature)
            DevAuthGuard.EnsureBoundAddressesAreLoopback(feature.Addresses);
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
