using Microsoft.Data.Sqlite;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Handles and friends in the store (Addendum 01 §9, §16 "Online/Offline and friends"): case-insensitive handle uniqueness
/// is atomic (a database constraint, also across concurrent writers and store instances); friend operations are idempotent
/// and keyed by stable account IDs, so retries and crossed requests never duplicate an edge.
/// </summary>
public sealed class SocialStoreTests : IAsyncLifetime
{
    static string Id(int i) => $"00000000-0000-4000-8000-{i:000000000000}";
    readonly TempDir dir = new();
    SqliteGameStore store = null!;
    string DbPath => dir.File("social.db");

    public async Task InitializeAsync()
    {
        store = new SqliteGameStore(DbPath);
        await store.InitializeAsync();
        for (int i = 1; i <= 8; i++) await store.EnsureAccountAsync(Id(i));
    }

    public Task DisposeAsync()
    {
        dir.Dispose();
        return Task.CompletedTask;
    }

    long Scalar(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task HandleUniqueness_IsCaseInsensitive_AndAtomicUnderConcurrentClaims()
    {
        string[] casings = { "Robin_Birdi", "robin_birdi", "ROBIN_BIRDI", "rObIn_bIrDi" };
        var second = new SqliteGameStore(DbPath);
        HandleClaimResult[] results = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Task.Run(() =>
            (i % 2 == 0 ? store : second).ClaimHandleAsync(Id(i), casings[i % casings.Length], Handles.Canonical(casings[i % casings.Length])!))));
        Assert.Single(results, r => r.Status == HandleClaimStatus.Claimed);
        Assert.Equal(7, results.Count(r => r.Status == HandleClaimStatus.Taken));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM player_handles"));

        PublicCard? card = await store.FindByHandleAsync("robin_birdi");
        HandleClaimResult winner = results.Single(r => r.Status == HandleClaimStatus.Claimed);
        Assert.Equal(winner.Handle!.Display, card!.Handle); // the chosen display casing is kept
    }

    [Fact]
    public async Task TheDatabaseItself_RejectsADuplicateCanonicalHandle()
    {
        await store.ClaimHandleAsync(Id(1), "Kestrel", "kestrel");
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO player_handles (account_id, handle_display, handle_canonical) VALUES ('{Id(2)}', 'KESTREL', 'kestrel')";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        cmd.CommandText = $"INSERT INTO player_handles (account_id, handle_display, handle_canonical) VALUES ('{Id(3)}', 'Other', 'OTHER')";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery()); // canonical must be the lowercase display
    }

    [Fact]
    public async Task ChangingAHandle_KeepsTheAccount_AndFreesTheOldName()
    {
        Assert.Equal(HandleClaimStatus.Claimed, (await store.ClaimHandleAsync(Id(1), "Nightfox", "nightfox")).Status);
        Assert.Equal(HandleClaimStatus.Unchanged, (await store.ClaimHandleAsync(Id(1), "Nightfox", "nightfox")).Status);
        HandleClaimResult recased = await store.ClaimHandleAsync(Id(1), "NightFox", "nightfox");
        Assert.Equal((HandleClaimStatus.Changed, 2L), (recased.Status, recased.Handle!.Revision));
        Assert.Equal(HandleClaimStatus.Changed, (await store.ClaimHandleAsync(Id(1), "Dawnfox", "dawnfox")).Status);
        Assert.Equal(HandleClaimStatus.Claimed, (await store.ClaimHandleAsync(Id(2), "nightfox", "nightfox")).Status);
        Assert.Equal(Id(1), (await store.FindByHandleAsync("dawnfox"))!.AccountId);
    }

    [Fact]
    public async Task FriendRequestRetries_AndCrossedRequests_NeverDuplicateEdges()
    {
        FriendOpResult[] sends = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.SendFriendRequestAsync(Id(1), Id(2)))));
        Assert.All(sends, r => Assert.Equal(FriendState.OutgoingPending, r.State));
        Assert.Single(sends, r => r.Changed);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM friendships"));

        FriendOpResult crossed = await store.SendFriendRequestAsync(Id(2), Id(1)); // a request back is mutual consent
        Assert.Equal(FriendState.Friends, crossed.State);
        Assert.False((await store.AcceptFriendRequestAsync(Id(2), Id(1))).Changed); // retried accept is a no-op
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM friendships"));
        Assert.Equal(Id(2), (await store.GetFriendGraphAsync(Id(1))).Friends.Single().AccountId);
    }

    [Fact]
    public async Task RequestLifecycle_CancelAcceptDeclineRemove_AreIdempotent()
    {
        await store.SendFriendRequestAsync(Id(1), Id(3));
        Assert.Equal(FriendOpStatus.Invalid, (await store.AcceptFriendRequestAsync(Id(1), Id(3))).Status); // cannot accept your own request
        Assert.True((await store.CancelFriendRequestAsync(Id(1), Id(3))).Changed);
        Assert.False((await store.CancelFriendRequestAsync(Id(1), Id(3))).Changed);
        Assert.Equal(FriendOpStatus.NotFound, (await store.AcceptFriendRequestAsync(Id(3), Id(1))).Status);

        await store.SendFriendRequestAsync(Id(3), Id(1));
        FriendGraph pending = await store.GetFriendGraphAsync(Id(1));
        Assert.Equal(Id(3), pending.Incoming.Single().AccountId);
        Assert.True((await store.DeclineFriendRequestAsync(Id(1), Id(3))).Changed);
        Assert.False((await store.DeclineFriendRequestAsync(Id(1), Id(3))).Changed);

        await store.SendFriendRequestAsync(Id(3), Id(1));
        Assert.True((await store.AcceptFriendRequestAsync(Id(1), Id(3))).Changed);
        Assert.Equal(FriendState.Friends, (await store.GetRelationshipAsync(Id(3), Id(1))).State);
        Assert.True((await store.RemoveFriendAsync(Id(3), Id(1))).Changed);
        Assert.False((await store.RemoveFriendAsync(Id(3), Id(1))).Changed);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM friendships"));
    }

    [Fact]
    public async Task Blocking_RemovesTheEdge_AndStopsRequestsEitherWay()
    {
        await store.SendFriendRequestAsync(Id(4), Id(5));
        await store.AcceptFriendRequestAsync(Id(5), Id(4));
        Assert.True((await store.BlockAsync(Id(5), Id(4))).Changed);
        Assert.False((await store.BlockAsync(Id(5), Id(4))).Changed);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM friendships"));
        Assert.Equal(FriendOpStatus.Blocked, (await store.SendFriendRequestAsync(Id(4), Id(5))).Status);
        Assert.Equal(FriendOpStatus.Blocked, (await store.SendFriendRequestAsync(Id(5), Id(4))).Status);
        Relationship r = await store.GetRelationshipAsync(Id(4), Id(5));
        Assert.True(r.BlockedMe && !r.BlockedByMe);
        Assert.Empty((await store.GetFriendGraphAsync(Id(4))).Blocked); // the blocked side is not told who blocked them
        Assert.Single((await store.GetFriendGraphAsync(Id(5))).Blocked);
        Assert.True((await store.UnblockAsync(Id(5), Id(4))).Changed);
        Assert.Equal(FriendOpStatus.Ok, (await store.SendFriendRequestAsync(Id(4), Id(5))).Status);
    }

    [Fact]
    public async Task UnknownAccounts_AndSelfRequests_AreRejected()
    {
        Assert.Equal(FriendOpStatus.NotFound, (await store.SendFriendRequestAsync(Id(1), Id(99))).Status);
        Assert.Equal(FriendOpStatus.Invalid, (await store.SendFriendRequestAsync(Id(1), Id(1))).Status);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM friendships"));
    }
}
