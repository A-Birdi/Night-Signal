using System.Data.Common;
using NightSignal.ControlPlane.Players;

namespace NightSignal.ControlPlane.Persistence;

/// <summary>
/// Public handles, Player Cards and friend relationships (Addendum 01 §9). Every write runs in one transaction; uniqueness
/// (canonical handle, one friendship row per unordered account pair, one block per direction) is enforced by database
/// constraints, and a lost race is re-run by <see cref="SqlGameStore.WriteAsync{T}"/> so it observes the winner — retries
/// can never duplicate an edge.
/// </summary>
public abstract partial class SqlGameStore
{
    public Task<HandleClaimResult> ClaimHandleAsync(string accountId, string display, string canonical, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            await EnsureAccount(c, tx, accountId);
            string? owner = await c.FirstOrDefaultAsync(tx, "SELECT account_id FROM player_handles WHERE handle_canonical = @h" + ForUpdate,
                r => r.Str(0), ("@h", canonical));
            if (owner is not null && owner != accountId) return new HandleClaimResult(HandleClaimStatus.Taken, null);
            var current = await c.FirstOrDefaultAsync(tx, "SELECT handle_display, revision FROM player_handles WHERE account_id = @a" + ForUpdate,
                r => (Display: r.Str(0), Revision: r.Long(1)), ("@a", accountId));
            if (current.Display is null)
            {
                await c.ExecAsync(tx, "INSERT INTO player_handles (account_id, handle_display, handle_canonical, revision) VALUES (@a, @d, @h, 1)",
                    ("@a", accountId), ("@d", display), ("@h", canonical));
                return new HandleClaimResult(HandleClaimStatus.Claimed, new PlayerHandle(display, canonical, 1));
            }
            if (current.Display == display)
                return new HandleClaimResult(HandleClaimStatus.Unchanged, new PlayerHandle(display, canonical, current.Revision));
            await c.ExecAsync(tx,
                "UPDATE player_handles SET handle_display = @d, handle_canonical = @h, revision = revision + 1, updated_at = CURRENT_TIMESTAMP WHERE account_id = @a",
                ("@a", accountId), ("@d", display), ("@h", canonical));
            return new HandleClaimResult(HandleClaimStatus.Changed, new PlayerHandle(display, canonical, current.Revision + 1));
        }, ct);

    public Task<PublicCard?> FindByHandleAsync(string canonical, CancellationToken ct = default) =>
        ReadAsync(async (c, tx) =>
        {
            string? id = await c.FirstOrDefaultAsync(tx, "SELECT account_id FROM player_handles WHERE handle_canonical = @h", r => r.Str(0), ("@h", canonical));
            return id is null ? null : await LoadPublicCard(c, tx, id);
        }, ct);

    public Task<IReadOnlyDictionary<string, PublicCard>> GetPublicCardsAsync(IReadOnlyCollection<string> accountIds, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyDictionary<string, PublicCard>>(async (c, tx) =>
        {
            var cards = new Dictionary<string, PublicCard>(StringComparer.Ordinal);
            foreach (string id in accountIds.Distinct())
                if (await c.FirstOrDefaultAsync(tx, "SELECT 1 FROM accounts WHERE account_id = @a", r => true, ("@a", id)))
                    cards[id] = await LoadPublicCard(c, tx, id);
            return cards;
        }, ct);

    /// <summary>Handle, display name and rank only: never e-mail, tokens, wallet or inventories.</summary>
    static async Task<PublicCard> LoadPublicCard(DbConnection c, DbTransaction tx, string accountId)
    {
        string? handle = await c.FirstOrDefaultAsync(tx, "SELECT handle_display FROM player_handles WHERE account_id = @a", r => r.Str(0), ("@a", accountId));
        string? name = await c.FirstOrDefaultAsync(tx, "SELECT display_name FROM player_cards WHERE account_id = @a", r => r.Str(0), ("@a", accountId));
        return new PublicCard(accountId, handle, name, await LoadRank(c, tx, accountId));
    }

    // ---------------------------------------------------------------- friends

    static (string Low, string High) Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    async Task<FriendState> LoadFriendState(DbConnection c, DbTransaction tx, string me, string other, bool lockRow = true)
    {
        (string low, string high) = Pair(me, other);
        var row = await c.FirstOrDefaultAsync(tx, "SELECT state, requested_by FROM friendships WHERE account_low = @l AND account_high = @h" + (lockRow ? ForUpdate : ""),
            r => (State: r.Str(0), By: r.Str(1)), ("@l", low), ("@h", high));
        return row.State switch
        {
            null => FriendState.None,
            "accepted" => FriendState.Friends,
            _ => row.By == me ? FriendState.OutgoingPending : FriendState.IncomingPending,
        };
    }

    static Task<int> DeleteFriendship(DbConnection c, DbTransaction tx, string a, string b)
    {
        (string low, string high) = Pair(a, b);
        return c.ExecAsync(tx, "DELETE FROM friendships WHERE account_low = @l AND account_high = @h", ("@l", low), ("@h", high));
    }

    static Task<List<(string Blocker, string Blocked)>> LoadBlocks(DbConnection c, DbTransaction tx, string a, string b) =>
        c.QueryAsync(tx, "SELECT blocker_id, blocked_id FROM blocks WHERE (blocker_id = @a AND blocked_id = @b) OR (blocker_id = @b AND blocked_id = @a)",
            r => (r.Str(0), r.Str(1)), ("@a", a), ("@b", b));

    static Task<bool> Exists(DbConnection c, DbTransaction tx, string accountId) =>
        c.FirstOrDefaultAsync(tx, "SELECT 1 FROM accounts WHERE account_id = @a", r => true, ("@a", accountId));

    static FriendOpResult Invalid(FriendState state, string message) => new(FriendOpStatus.Invalid, state, false, message);

    public Task<FriendOpResult> SendFriendRequestAsync(string from, string to, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            if (from == to) return Invalid(FriendState.None, "You cannot add yourself.");
            await EnsureAccount(c, tx, from);
            if (!await Exists(c, tx, to)) return new FriendOpResult(FriendOpStatus.NotFound, FriendState.None, false, "No such player.");
            if ((await LoadBlocks(c, tx, from, to)).Count > 0)
                return new FriendOpResult(FriendOpStatus.Blocked, FriendState.None, false, "You can't send a request to this player.");
            (string low, string high) = Pair(from, to);
            switch (await LoadFriendState(c, tx, from, to))
            {
                case FriendState.None:
                    await c.ExecAsync(tx, "INSERT INTO friendships (account_low, account_high, state, requested_by) VALUES (@l, @h, 'pending', @by)",
                        ("@l", low), ("@h", high), ("@by", from));
                    return new FriendOpResult(FriendOpStatus.Ok, FriendState.OutgoingPending, true);
                case FriendState.IncomingPending: // they already asked: a request back is mutual consent
                    await c.ExecAsync(tx, "UPDATE friendships SET state = 'accepted', updated_at = CURRENT_TIMESTAMP WHERE account_low = @l AND account_high = @h",
                        ("@l", low), ("@h", high));
                    return new FriendOpResult(FriendOpStatus.Ok, FriendState.Friends, true);
                case FriendState.OutgoingPending:
                    return new FriendOpResult(FriendOpStatus.Ok, FriendState.OutgoingPending, false);
                default:
                    return new FriendOpResult(FriendOpStatus.Ok, FriendState.Friends, false);
            }
        }, ct);

    public Task<FriendOpResult> CancelFriendRequestAsync(string from, string to, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            FriendState state = await LoadFriendState(c, tx, from, to);
            return state switch
            {
                FriendState.OutgoingPending => new FriendOpResult(FriendOpStatus.Ok, FriendState.None, await DeleteFriendship(c, tx, from, to) > 0),
                FriendState.None => new FriendOpResult(FriendOpStatus.Ok, FriendState.None, false),
                FriendState.IncomingPending => Invalid(state, "That request was sent to you; decline it instead."),
                _ => Invalid(state, "You are already friends; remove the friend instead."),
            };
        }, ct);

    public Task<FriendOpResult> AcceptFriendRequestAsync(string me, string from, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            FriendState state = await LoadFriendState(c, tx, me, from);
            switch (state)
            {
                case FriendState.IncomingPending:
                    (string low, string high) = Pair(me, from);
                    await c.ExecAsync(tx, "UPDATE friendships SET state = 'accepted', updated_at = CURRENT_TIMESTAMP WHERE account_low = @l AND account_high = @h",
                        ("@l", low), ("@h", high));
                    return new FriendOpResult(FriendOpStatus.Ok, FriendState.Friends, true);
                case FriendState.Friends:
                    return new FriendOpResult(FriendOpStatus.Ok, FriendState.Friends, false);
                case FriendState.OutgoingPending:
                    return Invalid(state, "You sent that request; wait for them to accept.");
                default:
                    return new FriendOpResult(FriendOpStatus.NotFound, state, false, "No pending request from that player.");
            }
        }, ct);

    public Task<FriendOpResult> DeclineFriendRequestAsync(string me, string from, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            FriendState state = await LoadFriendState(c, tx, me, from);
            return state switch
            {
                FriendState.IncomingPending => new FriendOpResult(FriendOpStatus.Ok, FriendState.None, await DeleteFriendship(c, tx, me, from) > 0),
                FriendState.None => new FriendOpResult(FriendOpStatus.Ok, FriendState.None, false),
                FriendState.OutgoingPending => Invalid(state, "You sent that request; cancel it instead."),
                _ => Invalid(state, "You are already friends; remove the friend instead."),
            };
        }, ct);

    public Task<FriendOpResult> RemoveFriendAsync(string me, string other, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            FriendState state = await LoadFriendState(c, tx, me, other);
            return state switch
            {
                FriendState.Friends => new FriendOpResult(FriendOpStatus.Ok, FriendState.None, await DeleteFriendship(c, tx, me, other) > 0),
                FriendState.None => new FriendOpResult(FriendOpStatus.Ok, FriendState.None, false),
                _ => Invalid(state, "That is a pending request; cancel or decline it instead."),
            };
        }, ct);

    /// <summary>Blocking removes any friendship or pending request and prevents new requests/invitations either way.</summary>
    public Task<FriendOpResult> BlockAsync(string me, string other, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            if (me == other) return Invalid(FriendState.None, "You cannot block yourself.");
            await EnsureAccount(c, tx, me);
            if (!await Exists(c, tx, other)) return new FriendOpResult(FriendOpStatus.NotFound, FriendState.None, false, "No such player.");
            bool inserted = await c.ExecAsync(tx, "INSERT INTO blocks (blocker_id, blocked_id) VALUES (@a, @b) ON CONFLICT DO NOTHING",
                ("@a", me), ("@b", other)) == 1;
            bool removed = await DeleteFriendship(c, tx, me, other) > 0;
            return new FriendOpResult(FriendOpStatus.Ok, FriendState.None, inserted || removed);
        }, ct);

    public Task<FriendOpResult> UnblockAsync(string me, string other, CancellationToken ct = default) =>
        WriteAsync(async (c, tx) =>
        {
            bool removed = await c.ExecAsync(tx, "DELETE FROM blocks WHERE blocker_id = @a AND blocked_id = @b", ("@a", me), ("@b", other)) > 0;
            return new FriendOpResult(FriendOpStatus.Ok, FriendState.None, removed);
        }, ct);

    public Task<FriendGraph> GetFriendGraphAsync(string me, CancellationToken ct = default) =>
        ReadAsync(async (c, tx) =>
        {
            var rows = await c.QueryAsync(tx,
                "SELECT account_low, account_high, state, requested_by, updated_at FROM friendships WHERE account_low = @a OR account_high = @a",
                r => (Low: r.Str(0), High: r.Str(1), State: r.Str(2), By: r.Str(3), At: r.Time(4)), ("@a", me));
            var friends = new List<FriendEdge>();
            var incoming = new List<FriendEdge>();
            var outgoing = new List<FriendEdge>();
            foreach (var row in rows)
            {
                var edge = new FriendEdge(row.Low == me ? row.High : row.Low, row.At);
                if (row.State == "accepted") friends.Add(edge);
                else if (row.By == me) outgoing.Add(edge);
                else incoming.Add(edge);
            }
            var blocked = await c.QueryAsync(tx, "SELECT blocked_id, created_at FROM blocks WHERE blocker_id = @a ORDER BY blocked_id",
                r => new FriendEdge(r.Str(0), r.Time(1)), ("@a", me));
            return new FriendGraph(friends, incoming, outgoing, blocked);
        }, ct);

    public Task<Relationship> GetRelationshipAsync(string me, string other, CancellationToken ct = default) =>
        ReadAsync(async (c, tx) =>
        {
            FriendState state = await LoadFriendState(c, tx, me, other, lockRow: false);
            var blocks = await LoadBlocks(c, tx, me, other);
            return new Relationship(state, blocks.Any(b => b.Blocker == me), blocks.Any(b => b.Blocker == other));
        }, ct);
}
