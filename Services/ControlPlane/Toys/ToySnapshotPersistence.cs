using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Persistence;

namespace NightSignal.ControlPlane.Toys;

/// <summary>
/// Durable toy snapshots (Addendum 02 §1.3 durable acceptance, §1.5 D208, §11 "persist compact recoverable snapshots across
/// restarts/dormancy"). A single background loop takes queued requests from <see cref="ConvoyToys"/>, captures the JSON
/// under the directory lock (CPU only), writes it WITHOUT the lock, then reports durability so Core can drop its journal.
/// A slow or failing store therefore never blocks a race start, a pause or any convoy request (B11); a failure leaves the
/// last verified snapshot authoritative and shows an honest warning.
///
/// Startup: after migrations and the dormant-room restore, stored snapshots of rooms the directory recovered are adopted;
/// the rest (their convoy is gone, or the 24 h Dormant grace has passed) are deleted — toy data never fabricates a convoy.
/// </summary>
public sealed class ToySnapshotPersistence(ConvoyDirectory directory, ConvoyToys toys, IToySnapshotStore store, TimeProvider clock,
    ILogger<ToySnapshotPersistence> log) : IHostedService
{
    Task? loop;

    public async Task StartAsync(CancellationToken ct)
    {
        await RestoreAsync(ct);
        loop = Task.Run(RunAsync, CancellationToken.None);
    }

    /// <summary>Adopts stored snapshots for recovered (Dormant) rooms; returns how many were adopted.</summary>
    public async Task<int> RestoreAsync(CancellationToken ct = default)
    {
        if (!toys.Available) return 0;
        try
        {
            IReadOnlyList<ToySnapshotRecord> stored = await store.LoadToySnapshotsAsync(clock.GetUtcNow(), ct);
            (int adopted, IReadOnlyList<string> orphans) = directory.Exclusive(() => toys.Adopt(stored, directory.SessionExpiries()));
            foreach (string orphan in orphans) await store.DeleteToySnapshotAsync(orphan, ct);
            if (adopted > 0 || orphans.Count > 0)
                log.LogInformation("Toy snapshots: {Adopted} adopted for dormant rooms, {Orphans} retired (no recovered convoy)", adopted, orphans.Count);
            return adopted;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Saved toy state could not be read; recovered rooms report that their toys may start fresh");
            directory.Exclusive(() =>
            {
                toys.MarkRestoreUnavailable(directory.SessionExpiries().Where(kv => kv.Value is not null).Select(kv => kv.Key).ToList());
                return true;
            });
            return 0;
        }
    }

    async Task RunAsync()
    {
        try
        {
            await foreach (ToyPersistRequest request in toys.PersistRequests.ReadAllAsync())
                await ProcessAsync(request, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogError(e, "Toy snapshot persistence loop stopped");
        }
    }

    /// <summary>Processes every request queued right now (tests use this instead of the background loop).</summary>
    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        int n = 0;
        while (toys.PersistRequests.TryRead(out ToyPersistRequest? request))
        {
            await ProcessAsync(request, ct);
            n++;
        }
        return n;
    }

    async Task ProcessAsync(ToyPersistRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Delete)
            {
                await store.DeleteToySnapshotAsync(request.SessionId, ct);
                return;
            }
            ToySnapshotRecord? record = directory.Exclusive(() => toys.Capture(request.SessionId));
            if (record is null) return;
            await store.SaveToySnapshotAsync(record, ct);
            directory.Exclusive(() =>
            {
                toys.Durable(request.SessionId, record.Revision);
                return true;
            });
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(e, "Toy snapshot {Operation} failed for session {SessionId}", request.Delete ? "delete" : "save", request.SessionId);
            if (!request.Delete)
                directory.Exclusive(() =>
                {
                    toys.SaveFailed(request.SessionId);
                    return true;
                });
        }
    }

    /// <summary>Graceful shutdown: one final snapshot of every live room, then the queue drains (bounded wait).</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        directory.Exclusive(toys.RequestFinalSnapshots);
        toys.CompletePersistQueue();
        if (loop is not null) await loop.WaitAsync(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { }, CancellationToken.None);
    }
}

/// <summary>Drives <see cref="ToyService.Pump"/> at <see cref="ToyHostLimits.BroadcastInterval"/> (≈10 Hz).</summary>
internal sealed class ToyPump(ToyService toys, ILogger<ToyPump> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ToyHostLimits.BroadcastInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { toys.Pump(); }
            catch (Exception e) { log.LogError(e, "Toy broadcast tick failed"); }
        }
    }
}
