using System.Collections.Concurrent;
using System.Text.Json;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Control;

/// <summary>Reply to a client request: <c>{"requestId","ok","result"|"error"}</c> inside a "reply" message.</summary>
public sealed record Reply(string? RequestId, bool Ok, object? Result = null, ConvoyError? Error = null);

/// <summary>
/// Transport-independent dispatcher for control-channel requests. Every request with a requestId is executed at
/// most once per account: a retried requestId (same connection or after reconnecting) receives the original reply.
/// </summary>
public sealed class ControlCommandHandler(ConvoyDirectory directory, IPlayerStore store, ContentService content,
    MatchAllocator allocator, TicketIssuer tickets, IConvoyNotifier notifier, TimeProvider clock,
    IHostApplicationLifetime lifetime, ILogger<ControlCommandHandler> log)
{
    static readonly TimeSpan ReplyRetention = TimeSpan.FromMinutes(10);
    readonly ConcurrentDictionary<string, (DateTimeOffset At, Lazy<Task<Reply>> Reply)> replies = new();
    int callsSincePrune;

    sealed record CreatePayload(string? Privacy);
    sealed record JoinPayload(string? Code, string? ConvoyId);
    sealed record CodePayload(string? Code);
    sealed record PresencePayload(string? Presence);
    sealed record LoadoutPayload(string? CarId, string? PerformanceHash, string? CosmeticHash);
    sealed record DestinationPayload(string? Destination);
    sealed record ConsentPayload(long ProposalRevision, bool Consent = true);
    sealed record RevisionPayload(long ProposalRevision);
    sealed record ReadyPayload(long ProposalRevision, long LoadoutRevision, bool Ready = true);
    sealed record TicketPayload(string? Role);

    public async Task<Reply> HandleAsync(string accountId, string type, string? requestId, JsonElement payload, CancellationToken ct)
    {
        if (requestId is { Length: 0 or > 64 })
            return new Reply(requestId, false, Error: new ConvoyError("invalid_request", "requestId must be 1–64 characters."));
        if (type is "ping" or "convoy.state" or "convoy.list" || requestId is null)
            return await ExecuteSafeAsync(accountId, type, requestId, payload, ct);

        if (Interlocked.Increment(ref callsSincePrune) % 256 == 0) Prune();
        var entry = replies.GetOrAdd($"{accountId}\n{type}\n{requestId}",
            _ => (clock.GetUtcNow(), new Lazy<Task<Reply>>(() => ExecuteSafeAsync(accountId, type, requestId, payload.Clone(), lifetime.ApplicationStopping))));
        return await entry.Reply.Value;
    }

    void Prune()
    {
        DateTimeOffset cutoff = clock.GetUtcNow() - ReplyRetention;
        foreach (var kv in replies)
            if (kv.Value.At < cutoff)
                replies.TryRemove(kv.Key, out _);
    }

    async Task<Reply> ExecuteSafeAsync(string accountId, string type, string? requestId, JsonElement payload, CancellationToken ct)
    {
        try
        {
            ConvoyResult r = await ExecuteAsync(accountId, type, payload, ct);
            return new Reply(requestId, r.Ok, r.Value, r.Error);
        }
        catch (JsonException)
        {
            return new Reply(requestId, false, Error: new ConvoyError("invalid_request", $"Malformed payload for '{type}'."));
        }
    }

    static T Read<T>(JsonElement payload) where T : class =>
        (payload.ValueKind == JsonValueKind.Object ? payload.Deserialize<T>(ControlConnection.Json) : JsonSerializer.Deserialize<T>("{}", ControlConnection.Json))
        ?? throw new JsonException();

    async Task<ConvoyResult> ExecuteAsync(string a, string type, JsonElement payload, CancellationToken ct)
    {
        switch (type)
        {
            case "ping":
                return ConvoyResult.Success(new { pong = true, serverTime = clock.GetUtcNow() });
            case "activity":
                directory.Touch(a);
                return ConvoyResult.Success();
            case "convoy.state":
                (long revision, object? snapshot) = directory.SnapshotFor(a);
                return ConvoyResult.Success(new { revision, convoy = snapshot });
            case "convoy.list":
                return ConvoyResult.Success(new { convoys = directory.ListDiscoverable() });
            case "convoy.create":
            {
                ConvoyPrivacy? privacy = ConvoyRules.ParsePrivacy(Read<CreatePayload>(payload).Privacy);
                if (privacy is null) return Invalid("privacy must be \"invite-only\" or \"discoverable\".");
                return directory.Create(a, await MemberInfoAsync(a, ct), privacy.Value);
            }
            case "convoy.join":
            {
                JoinPayload p = Read<JoinPayload>(payload);
                MemberInfo info = await MemberInfoAsync(a, ct);
                return p.Code is not null ? directory.JoinByCode(a, info, p.Code) : directory.JoinDiscoverable(a, info, p.ConvoyId);
            }
            case "convoy.leave":
                return directory.Leave(a);
            case "convoy.invite.create":
                return directory.CreateInvite(a);
            case "convoy.invite.revoke":
                return directory.RevokeInvite(a, Read<CodePayload>(payload).Code ?? "");
            case "presence.set":
                return Enum.TryParse(Read<PresencePayload>(payload).Presence, ignoreCase: false, out Presence presence) && Enum.IsDefined(presence)
                    ? directory.SetPresence(a, presence)
                    : Invalid("Unknown presence.");
            case "loadout.set":
                return await SetLoadoutAsync(a, Read<LoadoutPayload>(payload), ct);
            case "destination.propose":
            {
                Destination? d = ConvoyRules.ParseDestination(Read<DestinationPayload>(payload).Destination);
                return d is null ? Invalid("destination must be campaign-normal, campaign-hard or freeplay.") : directory.ProposeDestination(a, d.Value);
            }
            case "destination.consent":
            {
                ConsentPayload p = Read<ConsentPayload>(payload);
                return directory.Consent(a, p.ProposalRevision, p.Consent);
            }
            case "destination.commit":
                return directory.CommitDestination(a, Read<RevisionPayload>(payload).ProposalRevision);
            case "event.propose":
                return directory.ProposeEvent(a, Read<EventRequest>(payload));
            case "event.ready":
            {
                ReadyPayload p = Read<ReadyPayload>(payload);
                return directory.SetReady(a, p.ProposalRevision, p.LoadoutRevision, p.Ready);
            }
            case "event.start":
                return await StartAsync(a, Read<RevisionPayload>(payload).ProposalRevision, ct);
            case "match.ticket":
                return IssueOnRequest(a, Read<TicketPayload>(payload).Role ?? "spectator");
            default:
                return ConvoyResult.Fail("unknown_type", $"Unknown message type '{type}'.");
        }
    }

    static ConvoyResult Invalid(string message) => ConvoyResult.Fail("invalid_request", message);

    async Task<MemberInfo> MemberInfoAsync(string accountId, CancellationToken ct)
    {
        PlayerSnapshot s = await store.GetSnapshotAsync(accountId, ct);
        return new MemberInfo(s.Card?.DisplayName ?? "New driver", s.ToProgress());
    }

    /// <summary>Only owned cars can be selected; PI comes from the trusted catalogue, not the client.</summary>
    async Task<ConvoyResult> SetLoadoutAsync(string a, LoadoutPayload p, CancellationToken ct)
    {
        if (p.CarId is null || !content.Catalogue.TryCar(p.CarId, out CarDef car))
            return Invalid("Unknown car.");
        if (p.PerformanceHash is not { Length: > 0 and <= 128 } || p.CosmeticHash is not { Length: > 0 and <= 128 })
            return Invalid("performanceHash and cosmeticHash are required (≤128 characters).");
        PlayerSnapshot s = await store.GetSnapshotAsync(a, ct);
        if (s.Cars.All(c => c.CarId != car.Id))
            return ConvoyResult.Fail("not_owned", $"You do not own the {car.Name}.");
        return directory.UpdateLoadout(a, new LoadoutInfo(car.Id, car.BasePI, p.PerformanceHash, p.CosmeticHash));
    }

    /// <summary>Atomic revalidation happens in the directory with freshly loaded progress; allocation then runs in the
    /// background so this request (and the socket) is not held for the server's acknowledgement.</summary>
    async Task<ConvoyResult> StartAsync(string a, long proposalRevision, CancellationToken ct)
    {
        IReadOnlyList<string> entrants = directory.PendingEntrants(a);
        IReadOnlyDictionary<string, MemberProgress> progress = await store.GetProgressAsync(entrants, ct);
        (ConvoyError? error, MatchPlan? plan) = directory.BeginStart(a, proposalRevision, progress);
        if (error is not null) return new ConvoyResult(error);
        _ = Task.Run(() => AllocateAsync(plan!), CancellationToken.None);
        return ConvoyResult.Success(new { status = "allocating", entrants = plan!.Entrants.Count, aiEntrants = plan.AiEntrants.Count });
    }

    async Task AllocateAsync(MatchPlan plan)
    {
        try
        {
            (ActiveMatch? match, string? error) = await allocator.AllocateAsync(plan, lifetime.ApplicationStopping);
            if (match is null)
            {
                directory.FailStart(plan, error!);
                return;
            }
            directory.CompleteStart(plan, match);
            foreach (PlannedEntrant e in plan.Entrants)
                SendTicket(e.AccountId, plan.ConvoyId, match, "racer");
        }
        catch (Exception e)
        {
            log.LogError(e, "Allocation failed for convoy {ConvoyId}", plan.ConvoyId);
            directory.FailStart(plan, "Allocation failed because of a server error. Nobody was charged; try again.");
        }
    }

    ConvoyResult IssueOnRequest(string a, string role)
    {
        if (role is not ("racer" or "spectator")) return Invalid("role must be racer or spectator.");
        (ActiveMatch? match, string? convoyId, bool isEntrant) = directory.CurrentMatch(a);
        if (match is null) return ConvoyResult.Fail("not_found", "Your convoy has no active match.");
        if (role == "racer" && !isEntrant)
            return ConvoyResult.Fail("not_entrant", "Only entrants frozen at allocation can race; you can spectate.");
        return ConvoyResult.Success(TicketMessage(tickets.Issue(a, convoyId!, match, role), match, role));
    }

    void SendTicket(string accountId, string convoyId, ActiveMatch match, string role) =>
        notifier.Send(accountId, "match.allocated", 0, TicketMessage(tickets.Issue(accountId, convoyId, match, role), match, role));

    static object TicketMessage(IssuedTicket t, ActiveMatch match, string role) => new
    {
        matchId = match.MatchId,
        role,
        ticket = t.Ticket,
        expiresAt = t.ExpiresAt,
        server = new { host = match.Host, port = match.Port },
        build = match.Version.Build,
        protocol = match.Version.Protocol,
        contentHash = match.Version.ContentHash,
    };
}
