using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Garage;
using NightSignal.ControlPlane.Identity;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Players;

/// <summary>Account-facing player bootstrap endpoints. The account is always the verified JWT subject.</summary>
public static partial class PlayerEndpoints
{
    public static readonly string[] StarterCars = { "V01", "V02", "V03" };
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary><c>Look</c> (a driver look object) and <c>Pronouns</c> are optional: absent = unchanged, empty = cleared.</summary>
    public sealed record CardRequest(string? DisplayName, long? Revision, JsonElement Look = default, string? Pronouns = null, JsonElement Style = default,
        List<string>? Showcase = null);

    /// <summary>At most this many records on a card's showcase.</summary>
    public const int MaxShowcase = 3;
    public sealed record StarterRequest(string? CarId);
    public sealed record HandleRequest(string? Handle);
    /// <summary><c>ExpectedPrice</c> is the price the client showed the player; the server's catalogue price decides.</summary>
    public sealed record PurchaseBody(string? IdempotencyKey, string? ItemKind, string? ItemId, JsonElement? ExpectedPrice);

    public static void MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder me = app.MapGroup("/v1/me").RequireAuthorization();

        me.MapGet("", async (ClaimsPrincipal user, IPlayerStore store, ContentService content, MusicUnlockManifest music, CancellationToken ct) =>
        {
            string id = user.AccountId();
            await store.EnsureAccountAsync(id, ct);
            return Results.Ok(Describe(await store.GetSnapshotAsync(id, ct), content.Catalogue, music));
        });

        // The account's own records a card may showcase (spec §11).
        me.MapGet("/records", async (ClaimsPrincipal user, IPlayerStore store, ContentService content, CancellationToken ct) =>
            Results.Ok(new { records = (await store.PersonalRecordsAsync(user.AccountId(), content.Catalogue, ct)).Select(r => new { key = r.Key, label = r.Label, value = r.Value }) }));

        me.MapPost("/card", async (CardRequest body, ClaimsPrincipal user, IPlayerStore store, CustomizationContent customization, ContentService content, CancellationToken ct) =>
        {
            if (!DisplayNameRules.TryNormalize(body.DisplayName, out string name, out string error))
                return Problem(400, "invalid_display_name", error);
            // Spec §11: accessible appearance choices, visual only; the server keeps only a look it can build.
            string? look = null;
            JsonElement lj = body.Look; // Undefined = absent, Null = clear
            if (lj.ValueKind != JsonValueKind.Undefined)
            {
                if (lj.ValueKind == JsonValueKind.Null) look = "";
                else
                {
                    NightSignal.Characters.CharacterLook? parsed = lj.ValueKind == JsonValueKind.Object ? NightSignal.Characters.PlayerLooks.Parse(lj.GetRawText()) : null;
                    List<string> problems = NightSignal.Characters.PlayerLooks.Problems(parsed);
                    if (problems.Count > 0) return Problem(400, "invalid_look", "That appearance cannot be used: " + string.Join("; ", problems.Take(3)));
                    look = NightSignal.Characters.PlayerLooks.Canonical(parsed!);
                }
            }
            string? pronouns = null;
            if (body.Pronouns is { } pr)
            {
                pronouns = pr.Trim();
                if (pronouns.Length > 24 || pronouns.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}' || char.IsSurrogate(ch)))
                    return Problem(400, "invalid_pronouns", "Pronouns: up to 24 plain characters.");
            }
            // Spec §11 background, frame, motif, title, layout, region and preferred car: reward items only when owned,
            // the preferred car only one the account owns (Core CardStyleCatalogue, customization.json "card").
            string? style = null;
            JsonElement sj = body.Style; // Undefined = absent, Null = back to the default style
            if (sj.ValueKind != JsonValueKind.Undefined)
            {
                if (sj.ValueKind == JsonValueKind.Null) style = "";
                else
                {
                    NightSignal.Core.Customization.CardStyle? parsed = sj.ValueKind == JsonValueKind.Object ? NightSignal.Core.Customization.CardStyle.Parse(sj.GetRawText()) : null;
                    if (parsed is null) return Problem(400, "invalid_card_style", "That card style could not be read.");
                    PlayerSnapshot owner = await store.GetSnapshotAsync(user.AccountId(), ct);
                    var cosmetics = new HashSet<string>(owner.Cosmetics, StringComparer.Ordinal);
                    List<string> problems = customization.Catalogue.Card.Problems(parsed, cosmetics.Contains, car => owner.Cars.Any(c => c.CarId == car));
                    if (problems.Count > 0) return Problem(400, "invalid_card_style", string.Join(" ", problems.Take(3)));
                    style = parsed.Canonical();
                }
            }
            // Showcase: up to three of the player's OWN records, by key (null = keep, [] = none).
            string? showcase = null;
            if (body.Showcase is { } keys)
            {
                if (keys.Count > MaxShowcase || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
                    return Problem(400, "invalid_showcase", $"Choose up to {MaxShowcase} different records.");
                var mine = (await store.PersonalRecordsAsync(user.AccountId(), content.Catalogue, ct)).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
                if (keys.FirstOrDefault(k => !mine.Contains(k ?? "")) is { } unknown)
                    return Problem(400, "invalid_showcase", $"That record is not one of yours: {unknown}.");
                showcase = keys.Count == 0 ? "" : JsonSerializer.Serialize(keys);
            }
            CardWriteResult result = await store.UpsertCardAsync(user.AccountId(), name, body.Revision,
                look is null && pronouns is null && style is null && showcase is null ? null : new CardExtras(look, pronouns, style, showcase), ct);
            return result.Status == WriteStatus.Conflict
                ? Problem(409, "revision_conflict", "Your card changed elsewhere; reload it and apply your edit again.")
                : Results.Ok(new { displayName = result.Card!.DisplayName, revision = result.Card.Revision, look = LookElement(result.Card.LookJson), pronouns = result.Card.Pronouns,
                    style = LookElement(result.Card.StyleJson), showcase = LookElement(result.Card.ShowcaseJson) });
        });

        // Public @handle claim/change (Addendum 01 §9.2). Existing accounts claim one here; nothing else is reset.
        me.MapPut("/handle", async (HandleRequest body, ClaimsPrincipal user, ISocialStore social, RateLimiter limiter, CancellationToken ct) =>
        {
            string id = user.AccountId();
            if (!limiter.TryAcquire($"handle/{id}", SocialLimits.HandleChange, out long retry))
                return RateLimited(retry);
            if (!Handles.Validate(body.Handle, out string error)) return Problem(400, "invalid_handle", error);
            HandleClaimResult r = await social.ClaimHandleAsync(id, body.Handle!, Handles.Canonical(body.Handle)!, ct);
            return r.Status == HandleClaimStatus.Taken
                ? Problem(409, "handle_taken", "That username is already taken.")
                : Results.Ok(new { handle = r.Handle!.Display, revision = r.Handle.Revision, status = r.Status.ToString().ToLowerInvariant() });
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

        // Freeplay course access purchase (Addendum 01 §5.1): Idempotency-Key header; Core decides inside the debit transaction.
        me.MapPost("/courses/{courseId}/purchase", async (string courseId, HttpRequest request, ClaimsPrincipal user, IPlayerStore store,
            ContentService content, ConvoyDirectory directory, RateLimiter limiter, CancellationToken ct) =>
        {
            string account = user.AccountId();
            string? key = request.Headers[IdempotencyKeyHeader].FirstOrDefault();
            if (key is null || !IdempotencyKeyPattern().IsMatch(key))
                return Problem(400, "invalid_idempotency_key", $"Send a unique {IdempotencyKeyHeader} header (8–64 letters, digits, '-' or '_').");
            ContentCatalogue catalogue = content.Catalogue;
            if (!catalogue.TryCourse(courseId, out CourseDef course)) return Problem(404, "unknown_course", "Unknown course.");
            CourseAccessRule rule = CourseAccess.RuleFor(catalogue, courseId);
            (long? expected, string? priceError) = await ReadExpectedPriceAsync(request, ct);
            if (priceError is not null) return Problem(400, "invalid_price", priceError);
            if (expected is { } shown && rule.Purchasable && shown != rule.Price)
                return Problem(409, "price_changed", $"{course.Name} costs {rule.Price:N0} credits.");
            if (!limiter.TryAcquire($"course/{account}", SocialLimits.CoursePurchase, out long retry)) return RateLimited(retry);

            CoursePurchaseResult r = await store.PurchaseCourseAsync(account, key, courseId, catalogue, ct);
            if (r.Status is CoursePurchaseStatus.Purchased or CoursePurchaseStatus.AlreadyOwned)
                directory.UpdateOwnedCourses(account, (await store.GetOwnedCoursesAsync(new[] { account }, catalogue, ct))[account]);
            return r.Status switch
            {
                CoursePurchaseStatus.Purchased or CoursePurchaseStatus.Replayed => Results.Ok(new
                {
                    courseId, courseName = course.Name, outcome = "purchased", price = r.Price, charged = r.Charged, balance = r.Balance,
                    replayed = r.Status == CoursePurchaseStatus.Replayed,
                }),
                CoursePurchaseStatus.AlreadyOwned => Results.Ok(new
                {
                    courseId, courseName = course.Name, outcome = "already_owned", price = rule.Price, charged = 0L, balance = r.Balance, replayed = false,
                }),
                CoursePurchaseStatus.InsufficientFunds => Problem(409, "insufficient_funds", $"{course.Name} costs {r.Price:N0}; your balance is {r.Balance:N0}."),
                CoursePurchaseStatus.Conflict => Problem(409, "idempotency_conflict", "That idempotency key was used for a different purchase."),
                _ => Problem(409, "not_purchasable", NotPurchasableReason(rule)),
            };
        });

        // Course-access table for course cards (starter / buy-or-clear / reward-only / purchase-only) and supported modes.
        app.MapGet("/v1/courses", (ContentService content) =>
        {
            ContentCatalogue c = content.Catalogue;
            return Results.Ok(new
            {
                courses = c.Courses.Select(course =>
                {
                    CourseAccessRule rule = CourseAccess.RuleFor(c, course.Id);
                    return new
                    {
                        courseId = course.Id, name = course.Name, kind = course.Kind, format = course.Format,
                        access = new { kind = AccessKindWire(rule.Kind), price = rule.Price, unlockStage = rule.UnlockStage, purchasable = rule.Purchasable },
                        freeplayModes = FreeplayRules.Submodes.Where(m => FreeplayRules.Supports(course, m, c)).ToList(),
                    };
                }).ToList(),
            });
        }).RequireAuthorization();
    }

    static string AccessKindWire(CourseAccessKind kind) => kind switch
    {
        CourseAccessKind.Starter => "starter",
        CourseAccessKind.PurchaseOrCampaignClear => "purchase-or-clear",
        CourseAccessKind.CampaignRewardOnly => "reward-only",
        CourseAccessKind.PurchaseOnly => "purchase-only",
        _ => "none",
    };

    static string NotPurchasableReason(CourseAccessRule rule) => rule.Kind switch
    {
        CourseAccessKind.Starter => "Everyone has this course from the start.",
        CourseAccessKind.CampaignRewardOnly => $"This route is not sold; it unlocks free by clearing Normal {rule.UnlockStage}.",
        _ => "This course cannot be bought.",
    };

    static async Task<(long? Price, string? Error)> ReadExpectedPriceAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is null or 0) return (null, null);
        try
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("expectedPrice", out JsonElement p)) return (null, null);
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out double v) || !double.IsFinite(v) || Math.Floor(v) != v || v <= 0 || v > Limits.WalletCap)
                return (null, "expectedPrice must be a whole positive number of credits.");
            return ((long)v, null);
        }
        catch (JsonException)
        {
            return (null, "The body must be JSON: {\"expectedPrice\": number}.");
        }
    }

    /// <summary>
    /// The bootstrap document: card, handle, wallet, garage, campaign flags, ONLINE course access, soundtrack collection,
    /// team bests and Rank Points recomputed with Core.
    /// </summary>
    public static object Describe(PlayerSnapshot s, ContentCatalogue catalogue, MusicUnlockManifest music)
    {
        int Tier(string t) => s.Challenges.Count(c => c.Tier == t);
        RankSummary rank = RankSummary.Compute(s.NormalCleared.Count(x => x), s.HardCleared.Count(x => x), Tier("bronze"), Tier("silver"), Tier("gold"));
        MemberProgress progress = s.ToProgress();
        var baseline = music.BaselineCues.ToHashSet(StringComparer.Ordinal);
        return new
        {
            accountId = s.AccountId,
            card = s.Card is null ? null : new { displayName = s.Card.DisplayName, revision = s.Card.Revision, look = LookElement(s.Card.LookJson), pronouns = s.Card.Pronouns,
                style = LookElement(s.Card.StyleJson), showcase = LookElement(s.Card.ShowcaseJson) },
            handle = s.Handle is null ? null : new { handle = s.Handle.Display, revision = s.Handle.Revision },
            needsHandle = s.Handle is null,
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
            courses = new
            {
                domain = "online",
                owned = CourseOwnership.Listing(catalogue, s.OwnedCourses(catalogue)).Select(e => new { courseId = e.CourseId, source = e.Source }),
            },
            music = new
            {
                owned = baseline.Select(id => new { cueId = id, source = MusicSourceKinds.Baseline })
                    .Concat(s.Music.Where(m => !baseline.Contains(m.CueId)).Select(m => new { cueId = m.CueId, source = m.SourceKind }))
                    .ToList(),
            },
            teamTrialBests = s.TeamBests.Select(b => new
            {
                trialId = b.TrialId, difficulty = b.Difficulty, humans = b.Humans, kind = b.Kind, value = b.Value,
                displayMeanMs = b.Kind == "mean" ? b.Value / (double)Limits.TeamTrialSideSize : (double?)null, matchId = b.MatchId, category = "team",
            }),
            challengesCompleted = s.Challenges.Select(c => c.ChallengeId),
            cosmeticsOwned = s.Cosmetics,
            rank = new { rankPoints = rank.RankPoints, index = rank.Index, name = rank.Name, threshold = rank.Threshold, next = rank.NextName, nextThreshold = rank.NextThreshold },
        };
    }

    public static IResult Problem(int status, string code, string message) => Results.Json(new { error = code, message }, statusCode: status);

    /// <summary>A stored look as a JSON value in a response (null = the default look).</summary>
    public static JsonElement? LookElement(string? json) => string.IsNullOrEmpty(json) ? null : JsonDocument.Parse(json).RootElement.Clone();

    public static IResult RateLimited(long retryAfterMs) =>
        Results.Json(new { error = "rate_limited", message = "Too many requests. Try again later.", retryAfterMs }, statusCode: 429);

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex IdempotencyKeyPattern();
}
