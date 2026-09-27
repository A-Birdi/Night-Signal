using System.Threading.Channels;
using NightSignal.ControlPlane.Persistence;

namespace NightSignal.ControlPlane.Convoys;

/// <summary>
/// Persists Dormant convoy rooms (Addendum 02 D208) without blocking the directory lock: observer calls enqueue writes that a
/// background loop applies in order. At startup, after migrations, non-expired snapshots are restored into the directory as
/// Dormant rooms with their grants — never as a fabricated live convoy or a resumed race.
/// </summary>
public sealed class DormantRoomPersistence(IServiceProvider services, IDormantRoomStore store, TimeProvider clock,
    ILogger<DormantRoomPersistence> log) : IConvoySessionObserver, IHostedService
{
    readonly Channel<(string SessionId, DormantRoomSnapshot? Room)> writes =
        Channel.CreateBounded<(string, DormantRoomSnapshot?)>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    Task? loop;

    public void Dormant(DormantRoomSnapshot snapshot) => writes.Writer.TryWrite((snapshot.SessionId, snapshot));
    public void Restored(string sessionId) => writes.Writer.TryWrite((sessionId, null));
    public void Ended(string sessionId, string reason) => writes.Writer.TryWrite((sessionId, null));

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<DormantRoomSnapshot> rooms = await store.LoadDormantRoomsAsync(clock.GetUtcNow(), ct);
            int restored = services.GetRequiredService<ConvoyDirectory>().RestoreDormant(rooms);
            if (restored > 0) log.LogInformation("Restored {Count} dormant convoy room(s) from durable snapshots", restored);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Honest unavailability: rooms that cannot be read are not fabricated.
            log.LogError(e, "Dormant convoy rooms could not be restored; their members will see no rejoin option");
        }
        loop = Task.Run(() => RunAsync(CancellationToken.None), CancellationToken.None);
    }

    async Task RunAsync(CancellationToken ct)
    {
        await foreach ((string sessionId, DormantRoomSnapshot? room) in writes.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (room is not null) await store.SaveDormantRoomAsync(room, ct);
                else await store.DeleteDormantRoomAsync(sessionId, ct);
            }
            catch (Exception e)
            {
                log.LogError(e, "Dormant room persistence failed for {SessionId}", sessionId);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        writes.Writer.TryComplete();
        if (loop is not null) await loop.WaitAsync(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { }, CancellationToken.None);
    }
}
