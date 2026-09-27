using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Control;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.DevAuth;
using NightSignal.ControlPlane.Identity;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Players;
using NightSignal.ControlPlane.Security;

namespace NightSignal.ControlPlane;

/// <summary>Composition root. All configuration is read through options so test hosts can override it.</summary>
public static class ControlPlaneSetup
{
    public static IServiceCollection AddControlPlane(this IServiceCollection services)
    {
        services.AddOptions<IdentityOptions>().BindConfiguration(IdentityOptions.Section);
        services.AddOptions<DevAuthOptions>().BindConfiguration(DevAuthOptions.Section);
        services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section);
        services.AddOptions<KeyOptions>().BindConfiguration(KeyOptions.Section);
        services.AddOptions<TicketOptions>().BindConfiguration(TicketOptions.Section);
        services.AddOptions<GameServerOptions>().BindConfiguration(GameServerOptions.Section);
        services.AddOptions<CompatibilityOptions>().BindConfiguration(CompatibilityOptions.Section);
        services.AddOptions<ContentOptions>().BindConfiguration(ContentOptions.Section);
        services.AddSingleton<IPostConfigureOptions<GameServerOptions>, DevServerCredentialSetup>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ContentService>();
        services.AddSingleton(sp => TeamTrialCatalog.FromContentDirectory(sp.GetRequiredService<IOptions<ContentOptions>>(),
            sp.GetRequiredService<ContentService>(), sp.GetRequiredService<ILogger<TeamTrialCatalog>>()));
        services.AddSingleton(sp => MusicUnlockManifest.FromContentDirectory(sp.GetRequiredService<IOptions<ContentOptions>>(),
            sp.GetRequiredService<ContentService>(), sp.GetRequiredService<TeamTrialCatalog>(), sp.GetRequiredService<ILogger<MusicUnlockManifest>>()));
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<DevAuthKeys>();
        services.AddSingleton<DevAuthService>();
        services.AddNightSignalIdentity();

        services.AddSingleton<SqlGameStore>(sp =>
        {
            StorageOptions o = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            IHostEnvironment env = sp.GetRequiredService<IHostEnvironment>();
            return o.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
                ? new PostgresGameStore(o.PostgresConnectionString ?? throw new InvalidOperationException("Storage:PostgresConnectionString is required."))
                : new SqliteGameStore(PathResolver.Resolve(env.ContentRootPath, o.SqlitePath));
        });
        services.AddSingleton<IPlayerStore>(sp => sp.GetRequiredService<SqlGameStore>());
        services.AddSingleton<IResultLedger>(sp => sp.GetRequiredService<SqlGameStore>());
        services.AddSingleton<ISocialStore>(sp => sp.GetRequiredService<SqlGameStore>());
        services.AddHostedService<StoreInitializer>();
        services.AddSingleton<IDormantRoomStore>(sp => sp.GetRequiredService<SqlGameStore>());
        services.AddSingleton<DormantRoomPersistence>();
        services.AddSingleton<IConvoySessionObserver>(sp => sp.GetRequiredService<DormantRoomPersistence>());
        services.AddHostedService(sp => sp.GetRequiredService<DormantRoomPersistence>()); // after migrations: restores dormant rooms

        services.AddSingleton<ControlConnections>();
        services.AddSingleton<IConvoyNotifier>(sp => sp.GetRequiredService<ControlConnections>());
        services.AddSingleton<ConvoyDirectory>();
        services.AddHostedService<ConvoySweeper>();
        services.AddSingleton<GameServerRegistry>();
        services.AddSingleton<TicketIssuer>();
        services.AddSingleton<MatchAllocator>();
        services.AddSingleton<SettlementService>();
        services.AddSingleton<MatchWatchdog>();
        services.AddHostedService(sp => sp.GetRequiredService<MatchWatchdog>());
        services.AddSingleton<ControlCommandHandler>();
        services.AddSingleton<ControlChannel>();
        services.AddHostedService<DevAuthBindingCheck>();
        return services;
    }

    public static WebApplication UseControlPlane(this WebApplication app)
    {
        DevAuthOptions dev = app.Services.GetRequiredService<IOptions<DevAuthOptions>>().Value;
        if (dev.Enabled)
        {
            DevAuthGuard.EnsureAllowed(app.Environment, app.Configuration); // throws outside Development / off loopback
            app.Services.GetRequiredService<DevAuthService>().EnsureSeedLoaded();
            app.Logger.LogWarning("DevAuth is ENABLED (Development, loopback only). Tokens are issued by a local test issuer.");
        }
        else
        {
            IdentityOptions id = app.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;
            if (string.IsNullOrWhiteSpace(id.Issuer) || !Uri.TryCreate(id.JwksUrl, UriKind.Absolute, out Uri? jwks) || jwks.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Identity:Issuer and an https Identity:JwksUrl are required when DevAuth is disabled.");
        }
        // Fail fast on bad content or missing keys instead of on the first request.
        _ = app.Services.GetRequiredService<ContentService>();
        _ = app.Services.GetRequiredService<TeamTrialCatalog>();
        _ = app.Services.GetRequiredService<MusicUnlockManifest>();
        _ = app.Services.GetRequiredService<TicketIssuer>();
        GameServerOptions servers = app.Services.GetRequiredService<IOptions<GameServerOptions>>().Value; // creates the dev key if enabled
        app.Logger.LogInformation("Game-server credentials configured: {Count} ({Ids})", servers.Credentials.Count,
            string.Join(", ", servers.Credentials.Select(c => c.Id)));

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/healthz", (ContentService content) => Results.Ok(new { status = "ok", contentHash = content.ContentHash }));
        if (dev.Enabled) app.MapDevAuth();
        app.MapPlayerEndpoints();
        app.MapSocialEndpoints();
        app.MapServerEndpoints();
        app.Map(ControlChannel.Path, (HttpContext ctx, ControlChannel channel) => channel.RunAsync(ctx)).RequireAuthorization();
        return app;
    }
}

/// <summary>Applies schema migrations before the server takes traffic (when Storage:ApplyMigrations is true).</summary>
internal sealed class StoreInitializer(SqlGameStore store, IOptions<StorageOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => options.Value.ApplyMigrations ? store.InitializeAsync(ct) : Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Applies the directory's time-based rules once a second.</summary>
internal sealed class ConvoySweeper(ConvoyDirectory directory, ILogger<ConvoySweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { directory.Tick(); }
            catch (Exception e) { log.LogError(e, "Convoy sweep failed"); }
        }
    }
}
