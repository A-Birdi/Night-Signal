using System.Collections.Concurrent;
using System.Text.Json;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Garage;
using NightSignal.ControlPlane.Matches;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Security;
using NightSignal.ControlPlane.Toys;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Control;

/// <summary>Reply to a client request: <c>{"requestId","ok","result"|"error"}</c> inside a "reply" message.</summary>
public sealed record Reply(string? RequestId, bool Ok, object? Result = null, ConvoyError? Error = null);

/// <summary>
/// Transport-independent dispatcher for control-channel requests. Every request with a requestId is executed at
/// most once per account: a retried requestId (same connection or after reconnecting) receives the original reply.
/// </summary>
public sealed class ControlCommandHandler(ConvoyDirectory directory, IPlayerStore store, ISocialStore social, ContentService content,
    MatchAllocator allocator, TicketIssuer tickets, IConvoyNotifier notifier, RateLimiter limiter, TimeProvider clock,
    IHostApplicationLifetime lifetime, ILogger<ControlCommandHandler> log, ToyService toys, GarageService garage)
{
    static readonly TimeSpan ReplyRetention = TimeSpan.FromMinutes(10);
    readonly ConcurrentDictionary<string, (DateTimeOffset At, Lazy<Task<Reply>> Reply)> replies = new();
    int callsSincePrune;

    /// <summary>Requests that read state only; they are never cached by requestId and do not count as user interaction.</summary>
    public static readonly string[] ReadOnlyTypes = { "ping", "convoy.state", "convoy.list", "rejoin.status", "toy.snapshot" };

    /// <summary>
    /// Requests with their own exactly-once rule and a high rate (toy commands: Core's per-member request id inside the
    /// convoy session). They count as interaction but bypass the reply cache, which would otherwise fill with toy input.
    /// </summary>
    public static readonly string[] SelfIdempotentTypes = { "toy.command" };

    /// <summary>Replies that can be large and are sent on the connection's low-priority lane (after race/control traffic).</summary>
    public static readonly string[] LowPriorityReplyTypes = { "toy.snapshot" };

    sealed record CreatePayload(string? Privacy);
    sealed record JoinPayload(string? Code, string? ConvoyId, string? InviteId);
    sealed record CodePayload(string? Code);
    sealed record InvitePayload(string? InviteId);
    sealed record AccountPayload(string? AccountId);
    sealed record PresencePayload(string? Presence);
    /// <summary><c>performanceHash</c> is accepted for older clients but NEVER used: the server resolves it from the applied build.</summary>
    sealed record LoadoutPayload(string? CarId, string? InstanceId, string? PerformanceHash, string? CosmeticHash);
    sealed record IntentPayload(string? Kind, string? Mode, string? Submode, string? TrialId);
    sealed record ModeReadyPayload(long ModeRevision, bool Ready = true);
    sealed record ModeRevisionPayload(long ModeRevision);
    sealed record VotingPayload(bool Enabled, int? DurationSeconds);
    sealed record BallotOpenPayload(int? DurationSeconds, string? Weather, int? AiCount, int? CarCapPi, List<string>? AiRivals);
    sealed record VotePayload(long BallotRevision, string? CourseId);
    sealed record BallotRevisionPayload(long BallotRevision);
    sealed record RevisionPayload(long ProposalRevision);
    sealed record ReadyPayload(long ProposalRevision, long LoadoutRevision, bool Ready = true);
    sealed record TicketPayload(string? Role);
    sealed record DismissPayload(bool Forget = false);
    sealed record DiversionPayload(string? Toy);
    sealed record PostEventChoicePayload(long DestinationRevision, string? Choice);
    sealed record PostEventRevisionPayload(long DestinationRevision);

    public async Task<Reply> HandleAsync(string accountId, string type, string? requestId, JsonElement payload, CancellationToken ct)
    {
        if (requestId is { Length: 0 or > 64 })
            return new Reply(requestId, false, Error: new ConvoyError("invalid_request", "requestId must be 1–64 characters."));
        if (ReadOnlyTypes.Contains(type) || SelfIdempotentTypes.Contains(type) || requestId is null)
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

            // ---- membership
            case "convoy.create":
            {
                ConvoyPrivacy? privacy = ConvoyRules.ParsePrivacy(Read<CreatePayload>(payload).Privacy);
                if (privacy is null) return Invalid("privacy must be \"invite-only\" or \"discoverable\".");
                return directory.Create(a, await MemberInfoAsync(a, ct), privacy.Value);
            }
            case "convoy.join":
            {
                JoinPayload p = Read<JoinPayload>(payload);
                if (p.InviteId is not null) return await JoinByFriendInviteAsync(a, p.InviteId, ct);
                MemberInfo info = await MemberInfoAsync(a, ct);
                return p.Code is not null ? directory.JoinByCode(a, info, p.Code) : directory.JoinDiscoverable(a, info, p.ConvoyId);
            }
            case "convoy.leave":
                return directory.Leave(a);
            case "convoy.disband":
                return directory.DisbandByLeader(a);
            case "convoy.kick":
            {
                string? target = Read<AccountPayload>(payload).AccountId;
                return target is null ? Invalid("accountId is required.") : directory.Kick(a, target);
            }
            case "convoy.invite.create":
                return directory.CreateInvite(a);
            case "convoy.invite.revoke":
                return directory.RevokeInvite(a, Read<CodePayload>(payload).Code ?? "");
            case "convoy.invite.friend":
                return await InviteFriendAsync(a, Read<AccountPayload>(payload).AccountId, ct);
            case "convoy.invite.decline":
            {
                string? inviteId = Read<InvitePayload>(payload).InviteId;
                return inviteId is null ? Invalid("inviteId is required.") : directory.DeclineFriendInvite(a, inviteId);
            }

            // ---- rejoin grants (Addendum 01 §10.2)
            case "rejoin.status":
                return ConvoyResult.Success(directory.RejoinStatus(a));
            case "convoy.rejoin":
                return await RejoinAsync(a, ct);
            case "rejoin.dismiss":
                return directory.DismissRejoin(a, Read<DismissPayload>(payload).Forget);

            // ---- presence and loadout
            case "presence.set":
                return Enum.TryParse(Read<PresencePayload>(payload).Presence, ignoreCase: false, out Presence presence) && Enum.IsDefined(presence)
                    ? directory.SetPresence(a, presence)
                    : Invalid("Unknown presence.");
            case "loadout.set":
                return await SetLoadoutAsync(a, Read<LoadoutPayload>(payload), ct);
            case "diversion.set":
                return directory.SetDiversion(a, Read<DiversionPayload>(payload).Toy);

            // ---- 'While We Wait' toys (Addendum 02 §1, §11): never touch readiness, proposals or the economy
            case "toy.command":
                return toys.Command(a, payload);
            case "toy.snapshot":
                return toys.Snapshot(a, payload);

            // ---- post-event Continue / Service Break (Addendum 02 §7)
            case "postevent.choose":
            {
                PostEventChoicePayload p = Read<PostEventChoicePayload>(payload);
                return directory.ChoosePostEvent(a, p.DestinationRevision, p.Choice);
            }
            case "postevent.advance":
                return directory.AdvancePostEvent(a, Read<PostEventRevisionPayload>(payload).DestinationRevision);

            // ---- intent → Mode Ready → Enter Mode (Addendum 01 §7)
            case "intent.set":
            {
                IntentPayload p = Read<IntentPayload>(payload);
                ConvoyIntent? intent = ConvoyRules.ParseIntent(p.Kind, p.Mode, p.Submode, p.TrialId, out string error);
                return intent is null ? Invalid(error) : directory.SetIntent(a, intent);
            }
            case "mode.ready":
            {
                ModeReadyPayload p = Read<ModeReadyPayload>(payload);
                return directory.SetModeReady(a, p.ModeRevision, p.Ready);
            }
            case "mode.enter":
                return directory.EnterMode(a, Read<ModeRevisionPayload>(payload).ModeRevision);

            // ---- Freeplay voting (Addendum 01 §6.2)
            case "voting.set":
            {
                VotingPayload p = Read<VotingPayload>(payload);
                return directory.ConfigureVoting(a, p.Enabled, p.DurationSeconds);
            }
            case "ballot.open":
            {
                BallotOpenPayload p = Read<BallotOpenPayload>(payload);
                return directory.OpenBallot(a, p.DurationSeconds, new BallotOptions(p.Weather, p.AiCount ?? 0, p.CarCapPi, p.AiRivals));
            }
            case "ballot.vote":
            {
                VotePayload p = Read<VotePayload>(payload);
                return directory.Vote(a, p.BallotRevision, p.CourseId);
            }
            case "ballot.draw":
                return directory.DrawBallot(a, Read<BallotRevisionPayload>(payload).BallotRevision);
            case "ballot.cancel":
                return directory.CancelBallotByLeader(a, Read<BallotRevisionPayload>(payload).BallotRevision);

            // ---- event proposal → Event Ready → start
            case "event.propose":
                return directory.ProposeEvent(a, Read<EventRequest>(payload));
            case "event.ready":
            {
                ReadyPayload p = Read<ReadyPayload>(payload);
                if (!p.Ready) return directory.SetReady(a, p.ProposalRevision, p.LoadoutRevision, false);
                // Readiness is always against the SERVER's current applied build of the selected car (Addendum 02 §9–10).
                (LoadoutInfo? fresh, ConvoyError? invalid) = await garage.FreshSelectionAsync(a, ct);
                return invalid is not null ? new ConvoyResult(invalid) : directory.SetReady(a, p.ProposalRevision, p.LoadoutRevision, true, fresh);
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
        return new MemberInfo(s.Card?.DisplayName ?? "New driver", s.ToProgress(), s.OwnedCourses(content.Catalogue).Keys.ToList());
    }

    /// <summary>Rejoin revalidates atomically in the directory; a block between the player and the current leader revokes it.</summary>
    async Task<ConvoyResult> RejoinAsync(string a, CancellationToken ct)
    {
        (string? _, string? leaderId) = directory.PeekRejoin(a);
        bool blocked = leaderId is not null && leaderId != a && (await social.GetRelationshipAsync(a, leaderId, ct)).BlockedEitherWay;
        return directory.Rejoin(a, await MemberInfoAsync(a, ct), blocked);
    }

    /// <summary>Friends only, never across a block; permissions and capacity are checked again when the invite is used.</summary>
    async Task<ConvoyResult> InviteFriendAsync(string a, string? target, CancellationToken ct)
    {
        if (target is null) return Invalid("accountId is required.");
        if (!limiter.TryAcquire($"invite/{a}", SocialLimits.ConvoyInvite, out long retry))
            return ConvoyResult.Fail("rate_limited", "Too many invitations. Try again later.", retry);
        Relationship rel = await social.GetRelationshipAsync(a, target, ct);
        if (rel.BlockedEitherWay || rel.State != FriendState.Friends)
            return ConvoyResult.Fail("invite_denied", "You can only invite accepted friends.");
        return directory.InviteFriend(a, target);
    }

    async Task<ConvoyResult> JoinByFriendInviteAsync(string a, string inviteId, CancellationToken ct)
    {
        (string? inviterId, string? leaderId) = directory.PeekFriendInvite(a, inviteId);
        bool ok = true;
        if (inviterId is not null)
        {
            Relationship withInviter = await social.GetRelationshipAsync(a, inviterId, ct);
            ok = withInviter.State == FriendState.Friends && !withInviter.BlockedEitherWay;
            if (ok && leaderId is not null && leaderId != inviterId)
                ok = !(await social.GetRelationshipAsync(a, leaderId, ct)).BlockedEitherWay;
        }
        return directory.JoinByFriendInvite(a, await MemberInfoAsync(a, ct), inviteId, ok);
    }

    /// <summary>
    /// Only owned car INSTANCES can be selected. The performance hash, PI and applied revision come from the server-resolved
    /// applied build of that instance (ONLINE Garage); a client-sent performanceHash is ignored, only the cosmetic hash is taken.
    /// </summary>
    async Task<ConvoyResult> SetLoadoutAsync(string a, LoadoutPayload p, CancellationToken ct)
    {
        if (p.CarId is null && p.InstanceId is null) return Invalid("carId (or instanceId) is required.");
        if (p.CarId is not null && !content.Catalogue.TryCar(p.CarId, out _)) return Invalid("Unknown car.");
        if (p.InstanceId is { Length: 0 or > 64 }) return Invalid("instanceId is 1–64 characters.");
        if (p.CosmeticHash is not { Length: > 0 and <= 128 }) return Invalid("cosmeticHash is required (≤128 characters).");
        if (p.PerformanceHash is { Length: > 128 }) return Invalid("performanceHash is ignored but must be ≤128 characters.");
        (LoadoutInfo? loadout, ConvoyError? error) = await garage.SelectionAsync(a, p.CarId, p.InstanceId, p.CosmeticHash, ct);
        return loadout is null ? new ConvoyResult(error) : directory.UpdateLoadout(a, loadout);
    }

    /// <summary>Atomic revalidation happens in the directory with freshly loaded progress and course entitlements; allocation
    /// then runs in the background so this request (and the socket) is not held for the server's acknowledgement.</summary>
    async Task<ConvoyResult> StartAsync(string a, long proposalRevision, CancellationToken ct)
    {
        IReadOnlyList<string> entrants = directory.PendingEntrants(a);
        IReadOnlyDictionary<string, MemberProgress> progress = await store.GetProgressAsync(entrants, ct);
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> courses = await store.GetOwnedCoursesAsync(entrants, content.Catalogue, ct);
        // The applied builds are read and resolved by the server now; the directory checks them against what everyone readied
        // with, checks the car caps with these server PIs and freezes them into the plan (assignment entrants[].vehicleBuild).
        IReadOnlyDictionary<string, EntrantBuild> builds = await garage.FreezeSelectionsAsync(entrants, ct);
        (ConvoyError? error, MatchPlan? plan) = directory.BeginStart(a, proposalRevision, progress, courses, builds);
        if (error is not null) return new ConvoyResult(error);
        _ = Task.Run(() => AllocateAsync(plan!), CancellationToken.None);
        return ConvoyResult.Success(new
        {
            status = "allocating", entrants = plan!.Entrants.Count, aiEntrants = plan.AiEntrants.Count,
            vehicles = plan.Entrants.Count + plan.AiEntrants.Count, guestPasses = plan.GuestPasses.Count,
        });
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
            List<PlannedEntrant> racers = plan.Entrants.Where(e => directory.RacerEligible(plan.ConvoyId, e.AccountId)).ToList();
            foreach (PlannedEntrant e in racers)
                SendTicket(e.AccountId, plan.ConvoyId, match, "racer");
            // Authorized start: the game server accepted the frozen assignment and each racer holds a ticket. Record the frozen
            // build as Last Race Build (Core GarageOperations.RecordRaceBegan). Never on a failed allocation, a test or a toy.
            foreach (PlannedEntrant e in racers.Where(e => e.Build is not null))
            {
                try { await garage.RecordRaceBeganAsync(e.AccountId, e.Build!, match.MatchId, lifetime.ApplicationStopping); }
                catch (Exception ex) { log.LogError(ex, "Last Race Build not recorded for match {MatchId}", match.MatchId); }
            }
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
            return ConvoyResult.Fail("not_entrant", "Only entrants frozen at allocation who stayed connected can race; you can spectate.");
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
