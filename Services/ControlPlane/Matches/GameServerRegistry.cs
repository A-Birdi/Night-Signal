using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Security;

namespace NightSignal.ControlPlane.Matches;

public sealed record GameServerRegistration(string ServerId, string Host, int Port, string Build, int Protocol, string ContentHash, int MaxMatches);

/// <summary>
/// Registered authoritative game servers. Servers authenticate with a configured server key (never a player token),
/// register their endpoint and versions, heartbeat, and LONG-POLL for match assignments. Long-polling was chosen over
/// pushing to a server admin endpoint: the game server needs no inbound admin port or second secret, it works behind
/// NAT on a developer machine, and an unacknowledged assignment simply fails allocation.
/// </summary>
public sealed class GameServerRegistry(TimeProvider clock, IOptions<GameServerOptions> options)
{
    readonly object gate = new();
    readonly Dictionary<string, ServerState> servers = new();

    sealed class ServerState(GameServerRegistration registration)
    {
        public GameServerRegistration Registration = registration;
        public DateTimeOffset LastSeen;
        public int ActiveMatches;
        public readonly Dictionary<string, Pending> Pending = new();
        public TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed record Pending(MatchAssignment Assignment, TaskCompletionSource<bool> Acked);

    GameServerOptions Options => options.Value;

    /// <summary>Maps a presented server key to its configured server ID (constant-time hash comparison).</summary>
    public string? Authenticate(string? presentedKey, IReadOnlyList<GameServerCredential> credentials)
    {
        if (string.IsNullOrEmpty(presentedKey) || presentedKey.Length > 256) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        string? match = null;
        foreach (GameServerCredential c in credentials)
        {
            byte[] expected;
            try { expected = Convert.FromHexString(c.KeySha256); }
            catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(hash, expected)) match = c.Id;
        }
        return match;
    }

    public void Register(GameServerRegistration registration)
    {
        lock (gate)
        {
            if (!servers.TryGetValue(registration.ServerId, out ServerState? s))
                servers[registration.ServerId] = s = new ServerState(registration);
            s.Registration = registration;
            s.LastSeen = clock.GetUtcNow();
        }
    }

    public bool Heartbeat(string serverId, int activeMatches)
    {
        lock (gate)
        {
            if (!servers.TryGetValue(serverId, out ServerState? s)) return false;
            s.LastSeen = clock.GetUtcNow();
            s.ActiveMatches = Math.Max(0, activeMatches);
            return true;
        }
    }

    /// <summary>Least-loaded healthy server running exactly the entrants' build, protocol and content.</summary>
    public GameServerRegistration? Select(ClientVersion version)
    {
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            return servers.Values
                .Where(s => now - s.LastSeen <= TimeSpan.FromSeconds(Options.StaleAfterSeconds))
                .Where(s => s.Registration.Build == version.Build && s.Registration.Protocol == version.Protocol &&
                            s.Registration.ContentHash == version.ContentHash)
                .Where(s => s.ActiveMatches + s.Pending.Count < s.Registration.MaxMatches)
                .OrderBy(s => s.ActiveMatches + s.Pending.Count).ThenBy(s => s.Registration.ServerId, StringComparer.Ordinal)
                .Select(s => s.Registration)
                .FirstOrDefault();
        }
    }

    /// <summary>Queues an assignment and waits for the server's acknowledgement (false on timeout).</summary>
    public async Task<bool> AssignAsync(string serverId, MatchAssignment assignment, CancellationToken ct)
    {
        var pending = new Pending(assignment, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (gate)
        {
            if (!servers.TryGetValue(serverId, out ServerState? s)) return false;
            s.Pending[assignment.MatchId] = pending;
            s.Signal.TrySetResult();
        }
        try
        {
            return await pending.Acked.Task.WaitAsync(TimeSpan.FromSeconds(Options.AssignmentAckTimeoutSeconds), ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            lock (gate)
                if (servers.TryGetValue(serverId, out ServerState? s))
                    s.Pending.Remove(assignment.MatchId);
        }
    }

    public bool Acknowledge(string serverId, string matchId)
    {
        lock (gate)
        {
            if (!servers.TryGetValue(serverId, out ServerState? s) || !s.Pending.TryGetValue(matchId, out Pending? p)) return false;
            s.ActiveMatches++;
            return p.Acked.TrySetResult(true);
        }
    }

    /// <summary>Long-poll: returns unacknowledged assignments immediately, or waits up to <paramref name="wait"/> for one.
    /// Re-delivers until acknowledged, so a lost response is harmless (servers de-duplicate by match ID).</summary>
    public async Task<IReadOnlyList<MatchAssignment>?> PollAsync(string serverId, TimeSpan wait, CancellationToken ct)
    {
        Task signal;
        lock (gate)
        {
            if (!servers.TryGetValue(serverId, out ServerState? s)) return null;
            s.LastSeen = clock.GetUtcNow();
            if (s.Pending.Count > 0) return s.Pending.Values.Select(p => p.Assignment).ToList();
            if (s.Signal.Task.IsCompleted) s.Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = s.Signal.Task;
        }
        try
        {
            await signal.WaitAsync(wait, ct);
        }
        catch (TimeoutException)
        {
        }
        lock (gate)
        {
            if (!servers.TryGetValue(serverId, out ServerState? s)) return null;
            s.LastSeen = clock.GetUtcNow();
            return s.Pending.Values.Select(p => p.Assignment).ToList();
        }
    }

    /// <summary>Registered and heard from (heartbeat or poll) within GameServers:StaleAfterSeconds.</summary>
    public bool IsHealthy(string serverId)
    {
        lock (gate)
            return servers.TryGetValue(serverId, out ServerState? s) && clock.GetUtcNow() - s.LastSeen <= TimeSpan.FromSeconds(Options.StaleAfterSeconds);
    }

    public void MatchFinished(string serverId)
    {
        lock (gate)
            if (servers.TryGetValue(serverId, out ServerState? s))
                s.ActiveMatches = Math.Max(0, s.ActiveMatches - 1);
    }
}

/// <summary>Development convenience: a server key generated into the git-ignored dev key folder as "dev-local".</summary>
internal sealed class DevServerCredentialSetup(IOptions<KeyOptions> keys, IHostEnvironment env) : IPostConfigureOptions<GameServerOptions>
{
    public const string DevServerId = "dev-local";
    public const string KeyFileName = "gameserver-dev.key";

    public void PostConfigure(string? name, GameServerOptions options)
    {
        if (!options.GenerateDevCredential || !env.IsDevelopment() || options.Credentials.Any(c => c.Id == DevServerId)) return;
        string path = PathResolver.Resolve(PathResolver.Resolve(env.ContentRootPath, keys.Value.DevKeyDirectory), KeyFileName);
        string key = SecretFiles.ReadOrCreateRandomKey(path);
        options.Credentials.Add(new GameServerCredential { Id = DevServerId, KeySha256 = Hashing.Sha256Hex(key) });
    }
}
