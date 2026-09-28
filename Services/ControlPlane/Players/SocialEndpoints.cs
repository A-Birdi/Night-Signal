using System.Security.Claims;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Identity;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Players;

/// <summary>
/// Public Player Card lookup and friends (Addendum 01 §9). Relationships are keyed by stable account IDs; every operation
/// is idempotent (retries never duplicate edges) and rate-limited. Responses never contain e-mail, IP, tokens or wallets.
/// </summary>
public static class SocialEndpoints
{
    public sealed record FriendRequestBody(string? Handle, string? AccountId);

    static readonly System.Text.RegularExpressions.Regex AccountIdPattern = new("^[A-Za-z0-9-]{8,64}$");

    public static void MapSocialEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder players = app.MapGroup("/v1/players").RequireAuthorization();

        // Exact @handle lookup (a leading '@' is stripped). Case-insensitive via the canonical key.
        players.MapGet("/by-handle/{handle}", async (string handle, ClaimsPrincipal user, ISocialStore social, RateLimiter limiter, CancellationToken ct) =>
        {
            if (!limiter.TryAcquire($"lookup/{user.AccountId()}", SocialLimits.HandleLookup, out long retry)) return PlayerEndpoints.RateLimited(retry);
            string? canonical = Handles.Canonical(Handles.StripAt(handle));
            if (canonical is null) return PlayerEndpoints.Problem(400, "invalid_handle", "Usernames are 3–20 letters, digits or underscores, starting with a letter.");
            PublicCard? card = await social.FindByHandleAsync(canonical, ct);
            return card is null ? PlayerEndpoints.Problem(404, "not_found", "No player has that username.") : Results.Ok(CardWire(card));
        });

        players.MapGet("/{accountId}/card", async (string accountId, ClaimsPrincipal user, ISocialStore social, RateLimiter limiter, CancellationToken ct) =>
        {
            if (!limiter.TryAcquire($"lookup/{user.AccountId()}", SocialLimits.HandleLookup, out long retry)) return PlayerEndpoints.RateLimited(retry);
            if (!AccountIdPattern.IsMatch(accountId)) return PlayerEndpoints.Problem(400, "invalid_account", "Malformed account ID.");
            IReadOnlyDictionary<string, PublicCard> cards = await social.GetPublicCardsAsync(new[] { accountId }, ct);
            return cards.TryGetValue(accountId, out PublicCard? card) ? Results.Ok(CardWire(card)) : PlayerEndpoints.Problem(404, "not_found", "No such player.");
        });

        RouteGroupBuilder friends = app.MapGroup("/v1/friends").RequireAuthorization();

        friends.MapGet("", async (ClaimsPrincipal user, ISocialStore social, ConvoyDirectory directory, TimeProvider clock, CancellationToken ct) =>
        {
            string me = user.AccountId();
            FriendGraph graph = await social.GetFriendGraphAsync(me, ct);
            var ids = graph.Friends.Concat(graph.Incoming).Concat(graph.Outgoing).Concat(graph.Blocked).Select(e => e.AccountId).Distinct().ToList();
            IReadOnlyDictionary<string, PublicCard> cards = await social.GetPublicCardsAsync(ids, ct);
            IReadOnlyDictionary<string, FriendPresence> live = directory.FriendView(me, graph.Friends.Select(f => f.AccountId).ToList());
            object Person(FriendEdge e) => new
            {
                accountId = e.AccountId,
                handle = cards.TryGetValue(e.AccountId, out PublicCard? c) ? c.Handle : null,
                displayName = c?.DisplayName,
                since = e.Since,
            };
            return Results.Ok(new
            {
                statusAsOf = clock.GetUtcNow(),
                friends = graph.Friends.Select(f =>
                {
                    cards.TryGetValue(f.AccountId, out PublicCard? c);
                    FriendPresence p = live[f.AccountId];
                    return new
                    {
                        accountId = f.AccountId, handle = c?.Handle, displayName = c?.DisplayName,
                        rank = c is null ? null : new { name = c.Rank.Name, index = c.Rank.Index },
                        status = p.Status.ToString(), convoyId = p.ConvoyId, inYourConvoy = p.InYourConvoy,
                        canRejoin = p.CanRejoin, canInvite = p.CanInvite, since = f.Since,
                    };
                }).OrderBy(f => f.status == nameof(FriendStatus.Offline)).ThenBy(f => f.handle ?? f.accountId, StringComparer.OrdinalIgnoreCase).ToList(),
                incoming = graph.Incoming.Select(Person).ToList(),
                outgoing = graph.Outgoing.Select(Person).ToList(),
                blocked = graph.Blocked.Select(Person).ToList(),
                counts = new { friends = graph.Friends.Count, incoming = graph.Incoming.Count, outgoing = graph.Outgoing.Count },
            });
        });

        friends.MapPost("/requests", async (FriendRequestBody body, ClaimsPrincipal user, ISocialStore social, RateLimiter limiter, CancellationToken ct) =>
        {
            string me = user.AccountId();
            if (!limiter.TryAcquire($"friend-request/{me}", SocialLimits.FriendRequest, out long retry)) return PlayerEndpoints.RateLimited(retry);
            string? target = body.AccountId;
            if (target is null && body.Handle is not null)
            {
                string? canonical = Handles.Canonical(Handles.StripAt(body.Handle));
                if (canonical is null) return PlayerEndpoints.Problem(400, "invalid_handle", "Usernames are 3–20 letters, digits or underscores, starting with a letter.");
                target = (await social.FindByHandleAsync(canonical, ct))?.AccountId;
                if (target is null) return PlayerEndpoints.Problem(404, "not_found", "No player has that username.");
            }
            if (target is null || !AccountIdPattern.IsMatch(target)) return PlayerEndpoints.Problem(400, "invalid_request", "Send a handle or an accountId.");
            return Result(target, await social.SendFriendRequestAsync(me, target, ct));
        });

        friends.MapDelete("/requests/{accountId}", (string accountId, ClaimsPrincipal user, ISocialStore social, RateLimiter limiter, CancellationToken ct) =>
            Change(user, limiter, accountId, me => social.CancelFriendRequestAsync(me, accountId, ct)));
        friends.MapPost("/requests/{accountId}/accept", (string accountId, ClaimsPrincipal user, ISocialStore social, RateLimiter limiter, CancellationToken ct) =>
            Change(user, limiter, accountId, me => social.AcceptFriendRequestAsync(me, accountId, ct)));
        friends.MapPost("/requests/{accountId}/decline", (string accountId, ClaimsPrincipal user, ISocialStore social, RateLimiter limiter, CancellationToken ct) =>
            Change(user, limiter, accountId, me => social.DeclineFriendRequestAsync(me, accountId, ct)));
        friends.MapDelete("/{accountId}", (string accountId, ClaimsPrincipal user, ISocialStore social, RateLimiter limiter, CancellationToken ct) =>
            Change(user, limiter, accountId, me => social.RemoveFriendAsync(me, accountId, ct)));

        RouteGroupBuilder blocks = app.MapGroup("/v1/blocks").RequireAuthorization();
        blocks.MapPut("/{accountId}", (string accountId, ClaimsPrincipal user, ISocialStore social, ConvoyDirectory directory, RateLimiter limiter,
            CancellationToken ct) => Change(user, limiter, accountId, async me =>
        {
            FriendOpResult r = await social.BlockAsync(me, accountId, ct);
            if (r.Status == FriendOpStatus.Ok) directory.RevokeForBlock(me, accountId); // a block also revokes rejoin permission
            return r;
        }));
        blocks.MapDelete("/{accountId}", (string accountId, ClaimsPrincipal user, ISocialStore social, RateLimiter limiter, CancellationToken ct) =>
            Change(user, limiter, accountId, me => social.UnblockAsync(me, accountId, ct)));
    }

    static async Task<IResult> Change(ClaimsPrincipal user, RateLimiter limiter, string accountId, Func<string, Task<FriendOpResult>> op)
    {
        string me = user.AccountId();
        if (!AccountIdPattern.IsMatch(accountId)) return PlayerEndpoints.Problem(400, "invalid_account", "Malformed account ID.");
        if (!limiter.TryAcquire($"friend-change/{me}", SocialLimits.FriendChange, out long retry)) return PlayerEndpoints.RateLimited(retry);
        return Result(accountId, await op(me));
    }

    static IResult Result(string accountId, FriendOpResult r) => r.Status switch
    {
        FriendOpStatus.Ok => Results.Ok(new { accountId, state = StateWire(r.State), changed = r.Changed }),
        FriendOpStatus.NotFound => PlayerEndpoints.Problem(404, "not_found", r.Message ?? "Not found."),
        FriendOpStatus.Blocked => PlayerEndpoints.Problem(403, "blocked", r.Message ?? "You can't do that with this player."),
        _ => PlayerEndpoints.Problem(409, "invalid_state", r.Message ?? "That does not match the current relationship."),
    };

    static string StateWire(FriendState s) => s switch
    {
        FriendState.OutgoingPending => "outgoing",
        FriendState.IncomingPending => "incoming",
        FriendState.Friends => "friends",
        _ => "none",
    };

    static object CardWire(PublicCard c) => new
    {
        accountId = c.AccountId,
        handle = c.Handle,
        displayName = c.DisplayName,
        rank = new { rankPoints = c.Rank.RankPoints, index = c.Rank.Index, name = c.Rank.Name },
        pronouns = c.Pronouns,
        campaign = new { normalClears = c.NormalClears, hardClears = c.HardClears, stages = Limits.CampaignStages },
        challenges = new { completed = c.Challenges, total = 75 },
        // Spec §11 public cosmetics: the card's style (null = the catalogue's default style).
        style = PlayerEndpoints.LookElement(c.StyleJson),
    };
}
