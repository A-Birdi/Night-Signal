using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Identity;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Players;

/// <summary>Account-facing player bootstrap endpoints. The account is always the verified JWT subject.</summary>
public static partial class PlayerEndpoints
{
    public static readonly string[] StarterCars = { "V01", "V02", "V03" };

    public sealed record CardRequest(string? DisplayName, long? Revision);
    public sealed record StarterRequest(string? CarId);
    /// <summary><c>ExpectedPrice</c> is the price the client showed the player; the server's catalogue price decides.</summary>
    public sealed record PurchaseBody(string? IdempotencyKey, string? ItemKind, string? ItemId, JsonElement? ExpectedPrice);

    public static void MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder me = app.MapGroup("/v1/me").RequireAuthorization();

        me.MapGet("", async (ClaimsPrincipal user, IPlayerStore store, CancellationToken ct) =>
        {
            string id = user.AccountId();
            await store.EnsureAccountAsync(id, ct);
            return Results.Ok(Describe(await store.GetSnapshotAsync(id, ct)));
        });

        me.MapPost("/card", async (CardRequest body, ClaimsPrincipal user, IPlayerStore store, CancellationToken ct) =>
        {
            if (!DisplayNameRules.TryNormalize(body.DisplayName, out string name, out string error))
                return Problem(400, "invalid_display_name", error);
            CardWriteResult result = await store.UpsertCardAsync(user.AccountId(), name, body.Revision, ct);
            return result.Status == WriteStatus.Conflict
                ? Problem(409, "revision_conflict", "Your card changed elsewhere; reload it and apply your edit again.")
                : Results.Ok(new { displayName = result.Card!.DisplayName, revision = result.Card.Revision });
        });

        me.MapPost("/starter", async (StarterRequest body, ClaimsPrincipal user, IPlayerStore store, ContentService content, CancellationToken ct) =>
        {
            if (body.CarId is null || !StarterCars.Contains(body.CarId) || !content.Catalogue.TryCar(body.CarId, out CarDef car) || !car.Starter)
                return Problem(400, "invalid_starter", "Choose one of the starter cars V01, V02 or V03.");
            StarterResult result = await store.ClaimStarterAsync(user.AccountId(), car.Id, Limits.StarterGrantCredits, ct);
            return result.Status switch
            {
                WriteStatus.Conflict => Problem(409, "starter_already_claimed", $"You already chose {result.CarId} as your starter."),
                _ => Results.Ok(new { carId = result.CarId, balance = result.Balance, credited = result.Credited, replayed = result.Status == WriteStatus.Replayed }),
            };
        });

        me.MapPost("/purchases", async (PurchaseBody body, ClaimsPrincipal user, IPlayerStore store, ContentService content, CancellationToken ct) =>
        {
            if (body.IdempotencyKey is null || !IdempotencyKeyPattern().IsMatch(body.IdempotencyKey))
                return Problem(400, "invalid_idempotency_key", "Send a unique idempotencyKey (8–64 letters, digits, '-' or '_').");
            if (body.ItemKind != PriceRules.Car || body.ItemId is null || !content.Catalogue.TryCar(body.ItemId, out CarDef car))
                return Problem(400, "unknown_item", "Unknown item.");
            if (!PriceRules.IsValid(PriceRules.Car, car.Price))
                return Problem(500, "catalogue_price_invalid", "The catalogue price for this item is invalid.");
            if (body.ExpectedPrice is { } shown)
            {
                // The client may echo the price it displayed; reject anything malformed and anything stale.
                if (shown.ValueKind != JsonValueKind.Number || !shown.TryGetDouble(out double price) || !PriceRules.IsValid(PriceRules.Car, price))
                    return Problem(400, "invalid_price", "Prices are whole positive credits within the item's cap.");
                if ((long)price != car.Price)
                    return Problem(409, "price_changed", $"{car.Name} now costs {car.Price:N0} credits.");
            }
            PurchaseResult r = await store.PurchaseAsync(new PurchaseRequest(user.AccountId(), body.IdempotencyKey, PriceRules.Car, car.Id, car.Price), ct);
            return r.Status switch
            {
                WriteStatus.Ok or WriteStatus.Replayed => Results.Ok(new { itemId = car.Id, itemName = car.Name, price = car.Price, balance = r.Balance, replayed = r.Status == WriteStatus.Replayed }),
                WriteStatus.InsufficientFunds => Problem(409, "insufficient_funds", $"{car.Name} costs {car.Price:N0}; your balance is {r.Balance:N0}."),
                WriteStatus.AlreadyOwned => Problem(409, "already_owned", $"You already own the {car.Name}."),
                WriteStatus.Conflict => Problem(409, "idempotency_conflict", r.Message ?? "Idempotency key reused."),
                _ => Problem(400, "invalid_purchase", r.Message ?? "Invalid purchase."),
            };
        });
    }

    /// <summary>The bootstrap document: card, wallet, garage, campaign flags and Rank Points recomputed with Core.</summary>
    public static object Describe(PlayerSnapshot s)
    {
        int Tier(string t) => s.Challenges.Count(c => c.Tier == t);
        RankSummary rank = RankSummary.Compute(s.NormalCleared.Count(x => x), s.HardCleared.Count(x => x), Tier("bronze"), Tier("silver"), Tier("gold"));
        MemberProgress progress = s.ToProgress();
        return new
        {
            accountId = s.AccountId,
            card = s.Card is null ? null : new { displayName = s.Card.DisplayName, revision = s.Card.Revision },
            wallet = new { balance = s.Balance, cap = Limits.WalletCap },
            starterCarId = s.StarterCarId,
            ownedCars = s.Cars.Select(c => new { carId = c.CarId, source = c.Source }),
            campaign = new
            {
                normalCleared = s.NormalCleared,
                hardCleared = s.HardCleared,
                normalFrontier = CampaignProgress.Frontier(s.NormalCleared),
                hardFrontier = CampaignProgress.Frontier(s.HardCleared),
                hardUnlocked = progress.HardUnlocked,
            },
            challengesCompleted = s.Challenges.Select(c => c.ChallengeId),
            cosmeticsOwned = s.Cosmetics,
            rank = new { rankPoints = rank.RankPoints, index = rank.Index, name = rank.Name, threshold = rank.Threshold, next = rank.NextName, nextThreshold = rank.NextThreshold },
        };
    }

    public static IResult Problem(int status, string code, string message) => Results.Json(new { error = code, message }, statusCode: status);

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex IdempotencyKeyPattern();
}
