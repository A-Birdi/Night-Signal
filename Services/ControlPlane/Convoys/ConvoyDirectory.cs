using System.Security.Cryptography;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using CoreBallot = NightSignal.Core.Rules.Ballot;

namespace NightSignal.ControlPlane.Convoys;

/// <summary>Receives outgoing control messages. Implementations must not block (they are called under the directory lock
/// so that per-convoy message order matches revision order).</summary>
public interface IConvoyNotifier
{
    void Send(string accountId, string type, long revision, object payload);
}

/// <summary>Authoritative randomness for ballot draws. Production uses the OS CSPRNG; tests may inject a fixed source.</summary>
public interface IRandomSource
{
    uint NextUInt32();
}

public sealed class CryptoRandomSource : IRandomSource
{
    public uint NextUInt32()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }
}

/// <summary>
/// Authoritative convoy state (spec §4 as revised by Addendum 01 §6, §7, §10): membership, invites, leader and leadership
/// epoch, server-owned rejoin grants, coarse presence, Intent → Mode Ready → (course vote) → Event Ready, course access
/// sponsorship, roster planning and the match lifecycle. Transport-independent and synchronous: every operation runs under
/// one lock, bumps the convoy's monotonic revision and publishes a snapshot. Time comes from the injected
/// <see cref="TimeProvider"/>; <see cref="Tick"/> applies time-based rules.
///
/// A confirmed control-connection loss removes ACTIVE membership at once (no reserved seat) and records a rejoin grant
/// keyed to the leadership epoch. A disconnected leader keeps leadership for a 15 s leader-unavailable grace; after it the
/// longest-connected member leads and the epoch increments, which invalidates every older grant.
/// </summary>
public sealed class ConvoyDirectory
{
    readonly TimeProvider clock;
    readonly ContentService content;
    readonly IConvoyNotifier notifier;
    readonly TeamTrialCatalog trials;
    readonly IRandomSource random;
    readonly IReadOnlyList<IConvoySessionObserver> observers;

    readonly object gate = new();
    readonly Dictionary<string, Convoy> convoys = new();
    readonly Dictionary<string, string> membership = new();        // account -> convoy (ACTIVE members only)
    readonly Dictionary<string, Session> sessions = new();         // account -> connection state (connected accounts)
    readonly Dictionary<string, Invite> invites = new();           // code -> invite
    readonly Dictionary<string, FriendInvite> friendInvites = new(); // inviteId -> directed friend invite
    readonly Dictionary<string, RejoinGrant> grants = new();       // account -> the one current rejoin grant
    readonly Dictionary<string, Queue<DateTimeOffset>> joinAttempts = new();

    public ConvoyDirectory(TimeProvider clock, ContentService content, IConvoyNotifier notifier, TeamTrialCatalog? trials = null,
        IRandomSource? random = null, IEnumerable<IConvoySessionObserver>? observers = null)
    {
        this.clock = clock;
        this.content = content;
        this.notifier = notifier;
        this.trials = trials ?? TeamTrialCatalog.Fixture(content.Catalogue);
        this.random = random ?? new CryptoRandomSource();
        this.observers = observers?.ToList() ?? new List<IConvoySessionObserver>();
    }

    void Observe(Action<IConvoySessionObserver> call)
    {
        foreach (IConvoySessionObserver o in observers) call(o);
    }

    sealed class Session
    {
        public bool Connected;
        public DateTimeOffset ConnectedSince;
        public DateTimeOffset LastSeen;
        public DateTimeOffset LastInteraction;
        public Presence Presence = Presence.InMenus;
        public ClientVersion? Version;
    }

    sealed class Member
    {
        public required string AccountId;
        public required string DisplayName;
        public required Session Session;
        public DateTimeOffset JoinedAt;
        public bool Away;
        public required MemberProgress Progress;
        public HashSet<string> OwnedCourses = new(StringComparer.Ordinal);
        public LoadoutInfo? Loadout;
        public long LoadoutRevision;
        public long CosmeticRevision;
        /// <summary>Per-membership generation within this convoy session (Addendum 02 §1.5).</summary>
        public long Generation;
        /// <summary>Coarse diversion participation (which toy, if any). Never affects Mode Ready or Event Ready.</summary>
        public string? Diversion;
    }

    sealed class EventProposal
    {
        public long Revision;
        public long RosterRevision;
        public required EventSettings Settings;
        public DateTimeOffset OpenedAt;
        public string Origin = "leader";          // leader | draw
        public long? BallotRevision;
        public readonly Dictionary<string, long> Ready = new(); // account -> loadout revision readied with
    }

    enum BallotState { Open, Frozen, Resolved }

    sealed record BallotResult(string CourseId, string Method, int BallotIndex, int TotalBallots, int Votes, bool Overridden, DateTimeOffset At,
        IReadOnlyList<KeyValuePair<string, string>> Order);

    sealed class BallotSession
    {
        public long Revision;
        public long ModeRevision;
        public required string Mode;
        public int DurationSeconds;
        public DateTimeOffset OpenedAt;
        public DateTimeOffset Deadline;
        public BallotState State;
        public required BallotOptions Options;
        public readonly Dictionary<string, string> Ballots = new(StringComparer.Ordinal); // account -> course
        public BallotResult? Result;
    }

    sealed class RejoinGrant
    {
        public required string GrantId;
        public required string AccountId;
        public required string ConvoyId;
        public long LeadershipEpoch;
        public required string Reason;
        public string Permissions = "member";
        public bool WasLeader;
        public DateTimeOffset CreatedAt;
        public string? LeaderName;
        public bool Revoked;
        public string? RevokedReason;
        public bool PromptDismissed;
    }

    enum GrantStatus { None, Valid, Full, LeaderChanged, Disbanded, Revoked }

    enum Removal { Left, Disconnected, Kicked }

    /// <summary>Where Continue leads after a settled event (Addendum 02 §7): a real, server-validated destination.</summary>
    sealed record PostEventDestination(string Kind, string Label, string? StageId, string? Mode, IReadOnlyList<string> Needs)
    {
        public bool Same(PostEventDestination other) => Kind == other.Kind && StageId == other.StageId && Mode == other.Mode;
    }

    /// <summary>Per-current-member Continue / ServiceBreak / Undecided for one settled result (Addendum 02 §7.1).</summary>
    sealed class PostEventDecision
    {
        public required string SourceResultId;
        public required EventSettings Source;
        public long RosterRevision;
        public long DestinationRevision;
        public required PostEventDestination Destination;
        public DateTimeOffset OpenedAt;
        public readonly Dictionary<string, string> Choices = new(StringComparer.Ordinal); // absent = undecided
        public readonly Dictionary<string, Queue<DateTimeOffset>> Changes = new(StringComparer.Ordinal);
    }

    sealed class Convoy
    {
        public required string Id;
        /// <summary>Stable ConvoySessionId (Addendum 02 D207): survives leadership changes, events and dormancy.</summary>
        public required string SessionId;
        public long MembershipCounter;
        /// <summary>Set while Dormant: every member was lost to disconnection (Addendum 02 D208).</summary>
        public DateTimeOffset? DormantSince;
        public bool Dormant => DormantSince is not null;
        /// <summary>The frozen settings of the event being allocated/raced (source of the post-event destination).</summary>
        public EventSettings? RunningSettings;
        public PostEventDecision? PostEvent;
        public ConvoyPrivacy Privacy;
        public long Revision;
        public long RosterRevision;
        public long ProposalCounter;
        public required string LeaderId;
        public required string LeaderName;
        public long LeadershipEpoch = 1;
        public DateTimeOffset? LeaderUnavailableSince;
        public readonly List<Member> Members = new();
        public ConvoyPhase Phase = ConvoyPhase.Idle;
        public ConvoyIntent? Intent;
        public long ModeRevision;
        public DateTimeOffset ModeOpenedAt;
        public bool ModeEntered;
        public readonly Dictionary<string, long> ModeReady = new(); // account -> mode revision agreed to
        public bool VotingEnabled;
        public int VotingSeconds = Limits.BallotDefaultSeconds;
        public BallotSession? Ballot;
        public EventProposal? EventProposal;
        public DateTimeOffset? LastReadyRequest;
        public string? PendingPlanId;
        public readonly HashSet<string> FrozenEntrants = new(StringComparer.Ordinal);
        public readonly HashSet<string> DepartedEntrants = new(StringComparer.Ordinal);
        public ActiveMatch? Match;
        public DateTimeOffset MatchStartedAt;
        public string? Notice;
        public string? NoticeCode;

        public Member? Find(string accountId) => Members.FirstOrDefault(m => m.AccountId == accountId);
        public long NextProposalRevision() => ++ProposalCounter;
    }

    sealed record Invite(string Code, string ConvoyId, DateTimeOffset ExpiresAt);

    sealed record FriendInvite(string InviteId, string ConvoyId, string FromAccountId, string ToAccountId, DateTimeOffset ExpiresAt);

    DateTimeOffset Now => clock.GetUtcNow();
    ContentCatalogue Catalogue => content.Catalogue;

    // ================================================================== connection lifecycle

    /// <summary>Control channel opened. Membership is never restored implicitly: a returning player uses convoy.rejoin.</summary>
    public void Connected(string accountId, ClientVersion? version)
    {
        lock (gate)
        {
            Session s = SessionFor(accountId);
            s.Connected = true;
            s.ConnectedSince = Now;
            s.LastSeen = Now;
            s.LastInteraction = Now;
            s.Presence = Presence.InMenus;
            s.Version = version;
            if (ConvoyOf(accountId) is { } convoy)
            {
                convoy.Find(accountId)!.Away = false;
                Changed(convoy);
            }
        }
    }

    /// <summary>
    /// Confirmed loss of the control connection (Addendum 01 §10.1): the member leaves ACTIVE membership now — readiness and
    /// ballot cleared, seat released, one roster change published — and a server-owned rejoin grant is recorded.
    /// </summary>
    public void Disconnected(string accountId)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(accountId, out Session? s) || !s.Connected) return;
            s.Connected = false;
            if (ConvoyOf(accountId) is { } convoy) RemoveMember(convoy, accountId, Removal.Disconnected);
            sessions.Remove(accountId);
        }
    }

    /// <summary>Any client message (including ping): keeps the friend-list status fresh.</summary>
    public void Seen(string accountId)
    {
        lock (gate)
            if (sessions.TryGetValue(accountId, out Session? s)) s.LastSeen = Now;
    }

    Session SessionFor(string accountId)
    {
        if (!sessions.TryGetValue(accountId, out Session? s))
            sessions[accountId] = s = new Session { Connected = true, ConnectedSince = Now, LastSeen = Now, LastInteraction = Now };
        return s;
    }

    // ================================================================== membership and invites

    public ConvoyResult Create(string accountId, MemberInfo info, ConvoyPrivacy privacy)
    {
        lock (gate)
        {
            if (membership.ContainsKey(accountId))
                return ConvoyResult.Fail("already_in_convoy", "Leave your current convoy first.");
            var convoy = new Convoy
            {
                Id = Hashing.RandomId("cv_", 8), SessionId = Hashing.RandomId("cs_", 10), Privacy = privacy, LeaderId = accountId,
                LeaderName = info.DisplayName,
            };
            convoys[convoy.Id] = convoy;
            DropGrant(accountId); // joining a different convoy invalidates any old rejoin grant
            AddMember(convoy, accountId, info);
            Changed(convoy);
            return ConvoyResult.Success(new { convoyId = convoy.Id });
        }
    }

    public ConvoyResult CreateInvite(string accountId)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            PurgeExpiredInvites();
            if (invites.Values.Count(i => i.ConvoyId == convoy!.Id) >= ConvoyRules.MaxActiveInvitesPerConvoy)
                return ConvoyResult.Fail("too_many_invites", "Revoke an existing invite code first.");
            string code;
            do code = RandomCode(); while (invites.ContainsKey(code));
            var invite = new Invite(code, convoy!.Id, Now + ConvoyRules.InviteLifetime);
            invites[code] = invite;
            Changed(convoy);
            return ConvoyResult.Success(new { code, expiresAt = invite.ExpiresAt });
        }
    }

    public ConvoyResult RevokeInvite(string accountId, string code)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            string key = ConvoyRules.NormalizeCode(code);
            if (!invites.TryGetValue(key, out Invite? invite) || invite.ConvoyId != convoy!.Id)
                return ConvoyResult.Fail("not_found", "No such invite code for this convoy.");
            invites.Remove(key);
            Changed(convoy);
            return ConvoyResult.Success();
        }
    }

    public ConvoyResult JoinByCode(string accountId, MemberInfo info, string? code)
    {
        lock (gate)
        {
            // Rate-limit every attempt (valid or not) per account, so codes cannot be guessed quickly.
            Queue<DateTimeOffset> attempts = joinAttempts.TryGetValue(accountId, out var q) ? q : joinAttempts[accountId] = new();
            while (attempts.Count > 0 && Now - attempts.Peek() >= ConvoyRules.JoinAttemptWindow) attempts.Dequeue();
            if (attempts.Count >= ConvoyRules.JoinAttemptLimit)
            {
                long retry = (long)(attempts.Peek() + ConvoyRules.JoinAttemptWindow - Now).TotalMilliseconds;
                return ConvoyResult.Fail("rate_limited", "Too many join attempts. Try again later.", retry);
            }
            attempts.Enqueue(Now);

            if (membership.ContainsKey(accountId))
                return ConvoyResult.Fail("already_in_convoy", "Leave your current convoy first.");
            PurgeExpiredInvites();
            if (!invites.TryGetValue(ConvoyRules.NormalizeCode(code), out Invite? invite) || !convoys.TryGetValue(invite.ConvoyId, out Convoy? convoy))
                return ConvoyResult.Fail("invite_invalid", "This invite code is not valid or has expired.");
            return Join(convoy, accountId, info);
        }
    }

    public ConvoyResult JoinDiscoverable(string accountId, MemberInfo info, string? convoyId)
    {
        lock (gate)
        {
            if (membership.ContainsKey(accountId))
                return ConvoyResult.Fail("already_in_convoy", "Leave your current convoy first.");
            if (convoyId is null || !convoys.TryGetValue(convoyId, out Convoy? convoy))
                return ConvoyResult.Fail("not_found", "That convoy no longer exists.");
            if (convoy.Privacy != ConvoyPrivacy.Discoverable)
                return ConvoyResult.Fail("invite_required", "This convoy is private; ask for an invite code.");
            return Join(convoy, accountId, info);
        }
    }

    public IReadOnlyList<object> ListDiscoverable()
    {
        lock (gate)
            return convoys.Values
                .Where(c => c.Privacy == ConvoyPrivacy.Discoverable && !c.Dormant && c.Members.Count < Limits.MaxConvoyHumans)
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => (object)new
                {
                    convoyId = c.Id,
                    leaderName = c.LeaderName,
                    members = c.Members.Count,
                    maxMembers = Limits.MaxConvoyHumans,
                    privacy = c.Privacy.Wire(),
                    privacyLabel = c.Privacy.Label(),
                    phase = c.Phase.ToString(),
                    intent = c.Intent?.Wire(),
                })
                .ToList();
    }

    ConvoyResult Join(Convoy convoy, string accountId, MemberInfo info)
    {
        if (convoy.Dormant)
            return ConvoyResult.Fail("convoy_dormant", "That convoy is dormant; only its former members can rejoin it.");
        if (convoy.Members.Count >= Limits.MaxConvoyHumans)
            return ConvoyResult.Fail("convoy_full", $"A convoy holds at most {Limits.MaxConvoyHumans} members.");
        DropGrant(accountId);
        AddMember(convoy, accountId, info);
        // Joining during a match makes this member a spectator until the next event (spec §4.4).
        if (convoy.Phase is ConvoyPhase.Allocating or ConvoyPhase.InMatch)
            SetNotice(convoy, "joined_as_spectator", $"{info.DisplayName} joined as a spectator until the next event.");
        RosterChanged(convoy, $"{info.DisplayName} joined");
        Changed(convoy);
        return ConvoyResult.Success(new { convoyId = convoy.Id, spectator = convoy.Phase is ConvoyPhase.Allocating or ConvoyPhase.InMatch });
    }

    void AddMember(Convoy convoy, string accountId, MemberInfo info)
    {
        Session session = SessionFor(accountId);
        session.LastInteraction = Now;
        var member = new Member
        {
            AccountId = accountId, DisplayName = info.DisplayName, Progress = info.Progress, Session = session, JoinedAt = Now,
            OwnedCourses = new HashSet<string>(info.OwnedCourses ?? Array.Empty<string>(), StringComparer.Ordinal),
            Generation = ++convoy.MembershipCounter, // a new membership never inherits a stale generation's input rights
        };
        convoy.Members.Add(member);
        membership[accountId] = convoy.Id;
        Observe(o => o.MembershipStarted(convoy.SessionId, accountId, member.Generation));
    }

    /// <summary>Explicit Leave: no rejoin grant (Leave/Forget revokes eligibility).</summary>
    public ConvoyResult Leave(string accountId)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy)
                return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            RemoveMember(convoy, accountId, Removal.Left);
            return ConvoyResult.Success();
        }
    }

    /// <summary>Leader removes an active member (no grant), or revokes a departed member's rejoin grant.</summary>
    public ConvoyResult Kick(string leaderId, string targetId)
    {
        lock (gate)
        {
            if (LeaderConvoy(leaderId, out Convoy? convoy) is { } error) return error;
            if (targetId == leaderId) return ConvoyResult.Fail("invalid_request", "Use Leave to leave your own convoy.");
            if (convoy!.Find(targetId) is not null)
            {
                RemoveMember(convoy, targetId, Removal.Kicked);
                return ConvoyResult.Success(new { removed = true, rejoinRevoked = true });
            }
            if (grants.TryGetValue(targetId, out RejoinGrant? g) && g.ConvoyId == convoy.Id && !g.Revoked)
            {
                g.Revoked = true;
                g.RevokedReason = "removed by the leader";
                NotifyGrant(targetId);
                GrantChanged(g.ConvoyId);
                return ConvoyResult.Success(new { removed = false, rejoinRevoked = true });
            }
            return ConvoyResult.Fail("not_found", "That player is not in this convoy and holds no rejoin permission for it.");
        }
    }

    void RemoveMember(Convoy convoy, string accountId, Removal how)
    {
        Member? m = convoy.Find(accountId);
        if (m is null) return;
        bool wasLeader = convoy.LeaderId == accountId;
        convoy.Members.Remove(m);
        membership.Remove(accountId);
        convoy.ModeReady.Remove(accountId);
        convoy.EventProposal?.Ready.Remove(accountId);
        convoy.Ballot?.Ballots.Remove(accountId);
        convoy.PostEvent?.Choices.Remove(accountId);
        if (convoy.FrozenEntrants.Contains(accountId)) convoy.DepartedEntrants.Add(accountId); // a DQ entry is never restored

        switch (how)
        {
            case Removal.Disconnected:
                grants[accountId] = new RejoinGrant
                {
                    GrantId = Hashing.RandomId("rj_", 8), AccountId = accountId, ConvoyId = convoy.Id, LeadershipEpoch = convoy.LeadershipEpoch,
                    Reason = "disconnected", WasLeader = wasLeader, CreatedAt = Now, LeaderName = convoy.LeaderName,
                };
                break;
            case Removal.Kicked:
                grants[accountId] = new RejoinGrant
                {
                    GrantId = Hashing.RandomId("rj_", 8), AccountId = accountId, ConvoyId = convoy.Id, LeadershipEpoch = convoy.LeadershipEpoch,
                    Reason = "kicked", CreatedAt = Now, LeaderName = convoy.LeaderName, Revoked = true, RevokedReason = "removed by the leader",
                };
                break;
            default:
                grants.Remove(accountId);
                break;
        }
        string reason = how switch { Removal.Left => "left", Removal.Kicked => "kicked", _ => "disconnected" };
        notifier.Send(accountId, "convoy.closed", convoy.Revision, new { convoyId = convoy.Id, reason });
        Observe(o => o.MembershipEnded(convoy.SessionId, accountId, m.Generation, reason));
        if (how == Removal.Kicked) NotifyGrant(accountId);

        if (convoy.Members.Count == 0)
        {
            // Addendum 02 D208: lost solely to disconnection → Dormant for at most 24 h; a voluntary last leave ends it at once.
            if (how == Removal.Disconnected) GoDormant(convoy);
            else Disband(convoy, "last-member-left");
            return;
        }
        if (wasLeader)
        {
            if (how == Removal.Disconnected)
            {
                convoy.LeaderUnavailableSince = Now;
                SetNotice(convoy, "leader_unavailable",
                    $"{m.DisplayName} (leader) disconnected. Leader-only actions pause; leadership passes on in {(int)ConvoyRules.LeaderTransferAfter.TotalSeconds} s unless they rejoin.");
            }
            else
                TransferLeadership(convoy, $"{m.DisplayName} left");
        }
        RosterChanged(convoy, $"{m.DisplayName} {(how == Removal.Disconnected ? "disconnected" : how == Removal.Kicked ? "was removed" : "left")}");
        Changed(convoy);
    }

    /// <summary>The session ends: explicit disband, last voluntary leave, or dormant expiry. Its grants now report "disbanded".</summary>
    void Disband(Convoy convoy, string reason)
    {
        convoys.Remove(convoy.Id);
        PurgeInvitesOf(convoy.Id);
        Observe(o => o.Ended(convoy.SessionId, reason));
        NotifyGrantHolders(convoy.Id);
    }

    void PurgeInvitesOf(string convoyId)
    {
        foreach (string code in invites.Values.Where(i => i.ConvoyId == convoyId).Select(i => i.Code).ToList()) invites.Remove(code);
        foreach (string id in friendInvites.Values.Where(i => i.ConvoyId == convoyId).Select(i => i.InviteId).ToList()) friendInvites.Remove(id);
    }

    /// <summary>
    /// Every member was lost to disconnection (Addendum 02 D208): keep a compact Dormant room with its valid rejoin grants for
    /// at most 24 h from now. No seat is held, readiness/votes/proposals end, invites are withdrawn, nothing runs.
    /// </summary>
    void GoDormant(Convoy convoy)
    {
        convoy.DormantSince = Now;
        convoy.LeaderUnavailableSince = null;
        convoy.Ballot = null;
        convoy.PostEvent = null;
        convoy.ModeEntered = false;
        convoy.ModeReady.Clear();
        if (convoy.Phase is not (ConvoyPhase.Allocating or ConvoyPhase.InMatch))
        {
            convoy.EventProposal = null;
            convoy.Phase = convoy.Intent is null ? ConvoyPhase.Idle : ConvoyPhase.ModeCheck;
        }
        PurgeInvitesOf(convoy.Id);
        convoy.Revision++;
        SaveDormant(convoy);
        NotifyGrantHolders(convoy.Id);
    }

    void SaveDormant(Convoy c)
    {
        if (!c.Dormant) return;
        var snapshot = new DormantRoomSnapshot(c.SessionId, c.Id, c.Privacy.Wire(), c.LeaderId, c.LeaderName, c.LeadershipEpoch,
            c.Intent?.KindWire, c.Intent?.Mode, c.Intent?.Submode, c.Intent?.TrialId, c.VotingEnabled, c.VotingSeconds, c.MembershipCounter,
            c.ProposalCounter, c.RosterRevision, c.DormantSince!.Value, c.DormantSince.Value + ConvoyRules.DormantLifetime,
            grants.Values.Where(g => g.ConvoyId == c.Id && !g.Revoked)
                .Select(g => new DormantGrant(g.AccountId, g.GrantId, g.LeadershipEpoch, g.WasLeader, g.LeaderName, g.CreatedAt, g.PromptDismissed))
                .OrderBy(g => g.AccountId, StringComparer.Ordinal).ToList());
        Observe(o => o.Dormant(snapshot));
    }

    /// <summary>A grant changed: if it belongs to a Dormant room, re-persist that room's compact snapshot.</summary>
    void GrantChanged(string? convoyId)
    {
        if (convoyId is not null && convoys.TryGetValue(convoyId, out Convoy? c) && c.Dormant) SaveDormant(c);
    }

    /// <summary>
    /// Startup recovery from durable Dormant snapshots (never a fabricated live convoy): rooms come back Dormant with their
    /// original expiry and grants; expired rooms are skipped. Returns how many were restored.
    /// </summary>
    public int RestoreDormant(IEnumerable<DormantRoomSnapshot> rooms)
    {
        lock (gate)
        {
            int restored = 0;
            foreach (DormantRoomSnapshot r in rooms)
            {
                if (Now >= r.ExpiresAt || convoys.ContainsKey(r.ConvoyId)) continue;
                ConvoyIntent? intent = r.IntentKind is null ? null : ConvoyRules.ParseIntent(r.IntentKind, r.IntentMode, r.IntentSubmode, r.IntentTrialId, out _);
                var convoy = new Convoy
                {
                    Id = r.ConvoyId, SessionId = r.SessionId, Privacy = ConvoyRules.ParsePrivacy(r.Privacy) ?? ConvoyPrivacy.InviteOnly,
                    LeaderId = r.LeaderId, LeaderName = r.LeaderName, LeadershipEpoch = r.LeadershipEpoch, Intent = intent,
                    VotingEnabled = r.VotingEnabled, VotingSeconds = r.VotingSeconds, MembershipCounter = r.MembershipCounter,
                    ProposalCounter = r.ProposalCounter, RosterRevision = r.RosterRevision, DormantSince = r.DormantSince,
                    Phase = intent is null ? ConvoyPhase.Idle : ConvoyPhase.ModeCheck,
                };
                convoys[convoy.Id] = convoy;
                foreach (DormantGrant g in r.Grants.Where(g => !grants.ContainsKey(g.AccountId)))
                    grants[g.AccountId] = new RejoinGrant
                    {
                        GrantId = g.GrantId, AccountId = g.AccountId, ConvoyId = convoy.Id, LeadershipEpoch = g.LeadershipEpoch, Reason = "disconnected",
                        WasLeader = g.WasLeader, CreatedAt = g.CreatedAt, LeaderName = g.LeaderName, PromptDismissed = g.PromptDismissed,
                    };
                restored++;
            }
            return restored;
        }
    }

    /// <summary>Leader explicitly ends the convoy session now (Addendum 02 §1.5): members are released, grants end.</summary>
    public ConvoyResult DisbandByLeader(string accountId)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            foreach (Member m in convoy!.Members.ToList())
            {
                convoy.Members.Remove(m);
                membership.Remove(m.AccountId);
                grants.Remove(m.AccountId);
                notifier.Send(m.AccountId, "convoy.closed", convoy.Revision, new { convoyId = convoy.Id, reason = "disbanded" });
                Observe(o => o.MembershipEnded(convoy.SessionId, m.AccountId, m.Generation, "disbanded"));
            }
            Disband(convoy, "disbanded");
            return ConvoyResult.Success(new { convoyId = convoy.Id, disbanded = true });
        }
    }

    /// <summary>The caller's current membership (session and generation) for session-scoped services such as toys.</summary>
    public (string? SessionId, long Generation) MembershipOf(string accountId)
    {
        lock (gate)
            return ConvoyOf(accountId) is { } c ? (c.SessionId, c.Find(accountId)!.Generation) : (null, 0);
    }

    /// <summary>True only for the account's CURRENT active membership generation in that session (stale commands are rejected).</summary>
    public bool IsCurrentMembership(string sessionId, string accountId, long generation)
    {
        lock (gate)
            return ConvoyOf(accountId) is { } c && c.SessionId == sessionId && c.Find(accountId)!.Generation == generation;
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the directory lock. Session-scoped state that observers own (the toys'
    /// DowntimeSessions) is only ever touched under this one lock, so there is no second lock and no ordering hazard.
    /// <paramref name="work"/> must be short and must not block (no I/O, no awaiting).
    /// </summary>
    public T Exclusive<T>(Func<T> work)
    {
        lock (gate) return work();
    }

    /// <summary>Every live convoy session → its Dormant expiry (null while active). Used by startup recovery of session state.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset?> SessionExpiries()
    {
        lock (gate)
            return convoys.Values.ToDictionary(c => c.SessionId, c => c.DormantSince is { } d ? d + ConvoyRules.DormantLifetime : (DateTimeOffset?)null,
                StringComparer.Ordinal);
    }


    /// <summary>Longest continuously connected active member (stable account-ID tie-break) leads; the epoch increments.</summary>
    void TransferLeadership(Convoy convoy, string why)
    {
        Member successor = convoy.Members
            .OrderBy(m => m.Session.ConnectedSince)
            .ThenBy(m => m.AccountId, StringComparer.Ordinal)
            .First();
        convoy.LeaderId = successor.AccountId;
        convoy.LeaderName = successor.DisplayName;
        convoy.LeadershipEpoch++;
        convoy.LeaderUnavailableSince = null;
        CancelBallot(convoy, "the leadership changed");
        ReissueModeProposal(convoy);
        if (convoy.Phase == ConvoyPhase.ReadyCheck) ReissueEventProposal(convoy);
        SetNotice(convoy, "leader_changed", $"{why}. {successor.DisplayName} is now the convoy leader.");
        NotifyGrantHolders(convoy.Id); // every older grant now fails the leadership-epoch condition
    }

    // ================================================================== rejoin grants (Addendum 01 §10.2, D09)

    (GrantStatus Status, RejoinGrant? Grant, Convoy? Convoy) EvaluateGrant(string accountId)
    {
        if (!grants.TryGetValue(accountId, out RejoinGrant? g)) return (GrantStatus.None, null, null);
        if (g.Revoked) return (GrantStatus.Revoked, g, null);
        if (!convoys.TryGetValue(g.ConvoyId, out Convoy? c) || (c.Dormant && Now >= c.DormantSince + ConvoyRules.DormantLifetime))
            return (GrantStatus.Disbanded, g, null);
        if (c.LeadershipEpoch != g.LeadershipEpoch) return (GrantStatus.LeaderChanged, g, c);
        if (c.Members.Count >= Limits.MaxConvoyHumans) return (GrantStatus.Full, g, c);
        return (GrantStatus.Valid, g, c);
    }

    /// <summary>The convoy a rejoin would target and its current leader (for block checks before <see cref="Rejoin"/>).</summary>
    public (string? ConvoyId, string? LeaderId) PeekRejoin(string accountId)
    {
        lock (gate)
        {
            (GrantStatus _, RejoinGrant? g, Convoy? c) = EvaluateGrant(accountId);
            return (g?.ConvoyId, c?.LeaderId);
        }
    }

    /// <summary>
    /// Atomic revalidation of the caller's rejoin grant. Distinct failures: <c>rejoin_unavailable</c> (no grant),
    /// <c>rejoin_revoked</c>, <c>rejoin_disbanded</c>, <c>rejoin_leader_changed</c>, <c>convoy_full</c>. Success restores
    /// membership only: never a seat beyond six, never a DQ'd race entry; during a match the member spectates.
    /// </summary>
    public ConvoyResult Rejoin(string accountId, MemberInfo info, bool blockedWithLeader = false)
    {
        lock (gate)
        {
            if (membership.ContainsKey(accountId))
                return ConvoyResult.Fail("already_in_convoy", "You are already in a convoy.");
            (GrantStatus status, RejoinGrant? grant, Convoy? convoy) = EvaluateGrant(accountId);
            if (status == GrantStatus.Valid && blockedWithLeader)
            {
                grant!.Revoked = true;
                grant.RevokedReason = "blocked";
                status = GrantStatus.Revoked;
                GrantChanged(grant.ConvoyId);
            }
            switch (status)
            {
                case GrantStatus.None:
                    return ConvoyResult.Fail("rejoin_unavailable", "There is no convoy to rejoin.");
                case GrantStatus.Revoked:
                    return ConvoyResult.Fail("rejoin_revoked", "You can no longer rejoin that convoy.");
                case GrantStatus.Disbanded:
                    return ConvoyResult.Fail("rejoin_disbanded", "That convoy has disbanded; there is no active convoy to rejoin.");
                case GrantStatus.LeaderChanged:
                    return ConvoyResult.Fail("rejoin_leader_changed",
                        $"The convoy's leader changed (now {convoy!.LeaderName}); ask for a new invitation.");
                case GrantStatus.Full:
                    return ConvoyResult.Fail("convoy_full", $"That convoy is full ({Limits.MaxConvoyHumans} of {Limits.MaxConvoyHumans}); your permission to rejoin remains.");
            }

            bool restoreLeader = grant!.WasLeader && convoy!.LeaderId == accountId && convoy.LeaderUnavailableSince is not null;
            bool wasDormant = convoy!.Dormant;
            grants.Remove(accountId);
            if (wasDormant)
            {
                convoy.DormantSince = null; // an explicit authorized rejoin starts a new active period (heartbeats never do)
                Observe(o => o.Restored(convoy.SessionId));
            }
            AddMember(convoy, accountId, info);
            if (restoreLeader)
            {
                convoy.LeaderUnavailableSince = null;
                convoy.LeaderName = info.DisplayName;
                SetNotice(convoy, "leader_returned", $"{info.DisplayName} rejoined and remains the convoy leader.");
            }
            else if (wasDormant && convoy.LeaderId != accountId)
            {
                convoy.LeaderUnavailableSince = Now; // the absent leader gets the ordinary short grace, then the epoch rules apply
                SetNotice(convoy, "leader_unavailable",
                    $"The convoy is back. {convoy.LeaderName} (leader) has {(int)ConvoyRules.LeaderTransferAfter.TotalSeconds} s to rejoin before leadership passes on.");
            }
            if (wasDormant) NotifyGrantHolders(convoy.Id);
            bool spectator = convoy!.Phase is ConvoyPhase.Allocating or ConvoyPhase.InMatch;
            if (spectator)
                SetNotice(convoy, "joined_as_spectator", $"{info.DisplayName} rejoined as a spectator until the next event.");
            RosterChanged(convoy, $"{info.DisplayName} rejoined");
            Changed(convoy);
            return ConvoyResult.Success(new { convoyId = convoy.Id, leader = convoy.LeaderId == accountId, spectator });
        }
    }

    /// <summary>"Not Now" keeps eligibility and suppresses the prompt; "Forget" discards the grant.</summary>
    public ConvoyResult DismissRejoin(string accountId, bool forget)
    {
        lock (gate)
        {
            string? convoyId = grants.TryGetValue(accountId, out RejoinGrant? g) ? g.ConvoyId : null;
            if (forget) grants.Remove(accountId);
            else if (g is not null) g.PromptDismissed = true;
            GrantChanged(convoyId);
            return ConvoyResult.Success(RejoinStatusWire(accountId));
        }
    }

    /// <summary>What a reconnecting client needs: <c>canRejoin</c>, a reason when not, and whether to prompt once.</summary>
    public object RejoinStatus(string accountId)
    {
        lock (gate) return RejoinStatusWire(accountId);
    }

    object RejoinStatusWire(string accountId)
    {
        if (ConvoyOf(accountId) is { } current)
            return new { canRejoin = false, reason = "in_convoy", convoyId = current.Id, prompt = false, message = "You are in a convoy." };
        (GrantStatus status, RejoinGrant? g, Convoy? c) = EvaluateGrant(accountId);
        string reason = status switch
        {
            GrantStatus.Valid => "eligible",
            GrantStatus.Full => "full",
            GrantStatus.LeaderChanged => "leader_changed",
            GrantStatus.Disbanded => "disbanded",
            GrantStatus.Revoked => "revoked",
            _ => "none",
        };
        string message = status switch
        {
            GrantStatus.Valid => $"Rejoin {c!.LeaderName}'s convoy?",
            GrantStatus.Full => "That convoy is full right now; your permission to rejoin remains.",
            GrantStatus.LeaderChanged => "The convoy's leader changed; ask for a new invitation.",
            GrantStatus.Disbanded => "That convoy has disbanded; there is no active convoy to rejoin.",
            GrantStatus.Revoked => "You can no longer rejoin that convoy.",
            _ => "There is no convoy to rejoin.",
        };
        return new
        {
            canRejoin = status == GrantStatus.Valid,
            reason,
            grantId = g?.GrantId,
            convoyId = g?.ConvoyId,
            leaderName = c?.LeaderName ?? g?.LeaderName,
            members = c?.Members.Count,
            maxMembers = Limits.MaxConvoyHumans,
            leadershipEpoch = g?.LeadershipEpoch,
            youAreLeaderInGrace = status == GrantStatus.Valid && g!.WasLeader && c!.LeaderId == accountId && c.LeaderUnavailableSince is not null,
            dormant = status == GrantStatus.Valid && c!.Dormant,
            dormantExpiresAt = status == GrantStatus.Valid && c!.Dormant ? c.DormantSince + ConvoyRules.DormantLifetime : null,
            prompt = status == GrantStatus.Valid && !g!.PromptDismissed,
            message,
        };
    }

    void NotifyGrant(string accountId) => notifier.Send(accountId, "rejoin.status", 0, RejoinStatusWire(accountId));

    void NotifyGrantHolders(string convoyId)
    {
        foreach (string account in grants.Values.Where(g => g.ConvoyId == convoyId).Select(g => g.AccountId).ToList())
            NotifyGrant(account);
    }

    void DropGrant(string accountId)
    {
        if (grants.Remove(accountId, out RejoinGrant? g)) GrantChanged(g.ConvoyId);
    }

    /// <summary>
    /// A block revokes rejoin permission between the two accounts: the blocked account's grant into a convoy the blocker
    /// leads is revoked, and the blocker's own grant into a convoy the blocked account leads is dropped.
    /// </summary>
    public void RevokeForBlock(string blockerId, string blockedId)
    {
        lock (gate)
        {
            if (grants.TryGetValue(blockedId, out RejoinGrant? g) && convoys.TryGetValue(g.ConvoyId, out Convoy? c) && c.LeaderId == blockerId && !g.Revoked)
            {
                g.Revoked = true;
                g.RevokedReason = "blocked";
                NotifyGrant(blockedId);
                GrantChanged(g.ConvoyId);
            }
            if (grants.TryGetValue(blockerId, out RejoinGrant? own) && convoys.TryGetValue(own.ConvoyId, out Convoy? oc) && oc.LeaderId == blockedId)
                DropGrant(blockerId);
        }
    }

    // ================================================================== friend invitations (Addendum 01 §9.1)

    /// <summary>
    /// Sends a directed convoy invitation to a friend (friendship/block checks are done by the caller against the store).
    /// Private convoys: leader only. Re-inviting the same friend refreshes the same invitation (idempotent).
    /// </summary>
    public ConvoyResult InviteFriend(string inviterId, string targetId)
    {
        lock (gate)
        {
            if (ConvoyOf(inviterId) is not { } convoy) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            if (convoy.Privacy == ConvoyPrivacy.InviteOnly && convoy.LeaderId != inviterId)
                return ConvoyResult.Fail("not_leader", "Only the leader can invite people to a private convoy.");
            if (convoy.Find(targetId) is not null) return ConvoyResult.Fail("already_member", "They are already in this convoy.");
            if (convoy.Members.Count >= Limits.MaxConvoyHumans)
                return ConvoyResult.Fail("convoy_full", $"A convoy holds at most {Limits.MaxConvoyHumans} members.");
            PurgeExpiredInvites();
            FriendInvite? existing = friendInvites.Values.FirstOrDefault(i => i.ConvoyId == convoy.Id && i.ToAccountId == targetId);
            if (existing is null && friendInvites.Values.Count(i => i.ConvoyId == convoy.Id) >= ConvoyRules.MaxActiveFriendInvitesPerConvoy)
                return ConvoyResult.Fail("too_many_invites", "Too many pending invitations for this convoy.");
            var invite = new FriendInvite(existing?.InviteId ?? Hashing.RandomId("fi_", 8), convoy.Id, inviterId, targetId, Now + ConvoyRules.InviteLifetime);
            friendInvites[invite.InviteId] = invite;
            Member from = convoy.Find(inviterId)!;
            bool delivered = sessions.TryGetValue(targetId, out Session? s) && s.Connected;
            notifier.Send(targetId, "convoy.invited", 0, new
            {
                inviteId = invite.InviteId, convoyId = convoy.Id, fromAccountId = inviterId, fromName = from.DisplayName,
                leaderName = convoy.LeaderName, members = convoy.Members.Count, maxMembers = Limits.MaxConvoyHumans,
                privacy = convoy.Privacy.Wire(), intent = convoy.Intent?.Wire(), expiresAt = invite.ExpiresAt,
            });
            return ConvoyResult.Success(new { inviteId = invite.InviteId, expiresAt = invite.ExpiresAt, delivered });
        }
    }

    /// <summary>Who issued an invitation and who leads its convoy now (for the caller's friendship/block checks).</summary>
    public (string? InviterId, string? LeaderId) PeekFriendInvite(string targetId, string inviteId)
    {
        lock (gate)
        {
            PurgeExpiredInvites();
            if (!friendInvites.TryGetValue(inviteId, out FriendInvite? invite) || invite.ToAccountId != targetId) return (null, null);
            return (invite.FromAccountId, convoys.TryGetValue(invite.ConvoyId, out Convoy? c) ? c.LeaderId : null);
        }
    }

    /// <summary>Revalidated at use: invitation, convoy, inviter still an active member, relationship, capacity.</summary>
    public ConvoyResult JoinByFriendInvite(string targetId, MemberInfo info, string inviteId, bool relationshipOk)
    {
        lock (gate)
        {
            if (membership.ContainsKey(targetId)) return ConvoyResult.Fail("already_in_convoy", "Leave your current convoy first.");
            PurgeExpiredInvites();
            if (!friendInvites.TryGetValue(inviteId, out FriendInvite? invite) || invite.ToAccountId != targetId)
                return ConvoyResult.Fail("invite_invalid", "This invitation is not valid or has expired.");
            if (!convoys.TryGetValue(invite.ConvoyId, out Convoy? convoy))
                return ConvoyResult.Fail("invite_invalid", "That convoy no longer exists.");
            if (convoy.Find(invite.FromAccountId) is null)
                return ConvoyResult.Fail("invite_invalid", "The friend who invited you is no longer in that convoy.");
            if (!relationshipOk)
                return ConvoyResult.Fail("invite_denied", "You can no longer join through this invitation.");
            ConvoyResult joined = Join(convoy, targetId, info);
            if (joined.Ok) friendInvites.Remove(inviteId);
            return joined;
        }
    }

    public ConvoyResult DeclineFriendInvite(string targetId, string inviteId)
    {
        lock (gate)
        {
            if (friendInvites.TryGetValue(inviteId, out FriendInvite? invite) && invite.ToAccountId == targetId)
                friendInvites.Remove(inviteId);
            return ConvoyResult.Success();
        }
    }

    /// <summary>Server-computed friend rows: coarse status, and Rejoin/Invite only where the server would allow them now.</summary>
    public IReadOnlyDictionary<string, FriendPresence> FriendView(string viewerId, IReadOnlyCollection<string> friendIds)
    {
        lock (gate)
        {
            Convoy? viewerConvoy = ConvoyOf(viewerId);
            (GrantStatus grant, RejoinGrant? g, Convoy? _) = EvaluateGrant(viewerId);
            bool mayInvite = viewerConvoy is not null && viewerConvoy.Members.Count < Limits.MaxConvoyHumans &&
                             (viewerConvoy.Privacy == ConvoyPrivacy.Discoverable || viewerConvoy.LeaderId == viewerId);
            var result = new Dictionary<string, FriendPresence>(StringComparer.Ordinal);
            foreach (string id in friendIds.Distinct())
            {
                Convoy? convoy = ConvoyOf(id);
                FriendStatus status = StatusOf(id, convoy);
                bool together = convoy is not null && convoy == viewerConvoy;
                bool canRejoin = viewerConvoy is null && grant == GrantStatus.Valid && convoy is not null && g!.ConvoyId == convoy.Id;
                bool canInvite = mayInvite && !together && status is not (FriendStatus.Offline or FriendStatus.Unknown) && convoy is null;
                // A friend's convoy ID is shown only where it is actionable or public: yours, one you may rejoin, or a
                // discoverable one. Private convoys are not revealed to the whole friend list.
                string? visibleConvoy = convoy is not null && (together || canRejoin || convoy.Privacy == ConvoyPrivacy.Discoverable) ? convoy.Id : null;
                result[id] = new FriendPresence(status, visibleConvoy, together, canRejoin, canInvite);
            }
            return result;
        }
    }

    FriendStatus StatusOf(string accountId, Convoy? convoy)
    {
        if (!sessions.TryGetValue(accountId, out Session? s) || !s.Connected) return FriendStatus.Offline;
        if (Now - s.LastSeen > ConvoyRules.PresenceStaleAfter) return FriendStatus.Unknown;
        if (convoy?.Find(accountId) is { Away: true }) return FriendStatus.Away;
        return s.Presence switch
        {
            Presence.LoadingRace => FriendStatus.Loading,
            Presence.InRace => FriendStatus.Racing,
            Presence.Spectating => FriendStatus.Spectating,
            Presence.Garage => FriendStatus.Garage,
            Presence.AtMeet => FriendStatus.AtMeet,
            _ when convoy is not null && (convoy.Phase is ConvoyPhase.ModeCheck or ConvoyPhase.ReadyCheck or ConvoyPhase.Allocating ||
                                          convoy.Ballot is { State: BallotState.Open }) => FriendStatus.Preparing,
            _ => FriendStatus.Available,
        };
    }

    // ================================================================== presence, activity, loadout, course access

    /// <summary>Coarse presence for any connected account (friend status); convoy members also publish it to the convoy.</summary>
    public ConvoyResult SetPresence(string accountId, Presence presence)
    {
        if (presence is Presence.Reconnecting or Presence.Offline)
            return ConvoyResult.Fail("invalid_request", "That presence is set by the server.");
        lock (gate)
        {
            Session s = SessionFor(accountId);
            s.Presence = presence;
            s.LastInteraction = Now;
            s.LastSeen = Now;
            if (ConvoyOf(accountId) is { } convoy)
            {
                convoy.Find(accountId)!.Away = false;
                Changed(convoy);
            }
            return ConvoyResult.Success();
        }
    }

    /// <summary>Any deliberate user interaction (including long menu operations reported by the client) clears Away.</summary>
    public void Touch(string accountId)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(accountId, out Session? s)) s.LastInteraction = Now;
            if (ConvoyOf(accountId) is not { } convoy) return;
            Member m = convoy.Find(accountId)!;
            bool wasAway = m.Away;
            TouchMember(m);
            if (wasAway) Changed(convoy);
        }
    }

    void TouchMember(Member m)
    {
        m.Session.LastInteraction = Now;
        m.Away = false;
    }

    /// <summary>
    /// A performance change (car, car instance or performance build) bumps the member's loadout revision and unreadies only
    /// them; a cosmetic-only change keeps readiness (spec §4.2, Addendum 02 §9.2). Refused while the event is being allocated
    /// (frozen). The control channel fills <paramref name="loadout"/> from the SERVER-resolved applied build (never a client
    /// performance claim).
    /// </summary>
    public ConvoyResult UpdateLoadout(string accountId, LoadoutInfo loadout)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            if (convoy.Phase == ConvoyPhase.Allocating)
                return ConvoyResult.Fail("event_frozen", "The event is being allocated; loadouts are frozen.");
            Member m = convoy.Find(accountId)!;
            TouchMember(m);
            bool performance = m.Loadout is null || m.Loadout.CarId != loadout.CarId || m.Loadout.InstanceId != loadout.InstanceId ||
                               m.Loadout.PerformanceHash != loadout.PerformanceHash;
            bool cosmetic = m.Loadout is null || m.Loadout.CosmeticHash != loadout.CosmeticHash;
            m.Loadout = loadout;
            if (performance)
            {
                m.LoadoutRevision++;
                convoy.EventProposal?.Ready.Remove(accountId);
            }
            if (cosmetic) m.CosmeticRevision++;
            Changed(convoy);
            return ConvoyResult.Success(new
            {
                loadoutRevision = m.LoadoutRevision, cosmeticRevision = m.CosmeticRevision, performanceChanged = performance,
                carId = loadout.CarId, instanceId = loadout.InstanceId, performanceHash = loadout.PerformanceHash, carPi = loadout.CarPi,
                piClass = PerformanceIndex.IsLegalFor(loadout.CarPi, PerformanceIndex.Max) ? PerformanceIndex.ClassOf(loadout.CarPi).ToString() : null,
                appliedRevision = loadout.AppliedRevision,
            });
        }
    }

    /// <summary>The member's current car selection (null when not in a convoy or no car chosen yet).</summary>
    public LoadoutInfo? LoadoutOf(string accountId)
    {
        lock (gate)
            return ConvoyOf(accountId)?.Find(accountId)?.Loadout;
    }

    /// <summary>
    /// Builds of this car instance are frozen while its owner's convoy allocates an event with it (Addendum 02 §9.2: the
    /// selector cannot bypass a frozen event). Returns the event label, or null when the Garage may change it.
    /// </summary>
    public string? BuildFrozenFor(string accountId, string instanceId)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy || convoy.Phase != ConvoyPhase.Allocating) return null;
            return convoy.Find(accountId)?.Loadout?.InstanceId == instanceId ? "the event being allocated" : null;
        }
    }

    /// <summary>
    /// The Garage changed the applied build of <paramref name="fresh"/>'s instance (apply, restore, Buy-and-Apply). When that
    /// instance is the member's selected car, its server performance hash/PI replace the old ones; a PERFORMANCE change bumps
    /// the loadout revision and unreadies only this member, a same-hash change (utility-only) keeps readiness. While
    /// Allocating nothing changes here (the start already froze the build; the next event.ready re-reads it).
    /// </summary>
    public bool RefreshLoadoutFromGarage(string accountId, LoadoutInfo fresh)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy || convoy.Phase == ConvoyPhase.Allocating) return false;
            Member m = convoy.Find(accountId)!;
            if (m.Loadout is null || m.Loadout.InstanceId is null || m.Loadout.InstanceId != fresh.InstanceId) return false;
            bool performance = ApplyFresh(convoy, m, fresh);
            Changed(convoy);
            return performance;
        }
    }

    /// <summary>Takes the server-resolved hash/PI of the member's selected instance; true when the performance hash changed.</summary>
    static bool ApplyFresh(Convoy convoy, Member m, LoadoutInfo fresh)
    {
        bool performance = m.Loadout!.PerformanceHash != fresh.PerformanceHash;
        m.Loadout = m.Loadout with { CarPi = fresh.CarPi, PerformanceHash = fresh.PerformanceHash, AppliedRevision = fresh.AppliedRevision };
        if (performance)
        {
            m.LoadoutRevision++;
            convoy.EventProposal?.Ready.Remove(m.AccountId);
        }
        return performance;
    }

    /// <summary>
    /// A member's stored course entitlements changed (purchase or campaign unlock): the convoy-accessible pool updates without
    /// touching anyone's car or selecting the course.
    /// </summary>
    public void UpdateOwnedCourses(string accountId, IReadOnlyCollection<string> courses)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return;
            convoy.Find(accountId)!.OwnedCourses = new HashSet<string>(courses, StringComparer.Ordinal);
            Changed(convoy);
        }
    }

    Dictionary<string, ICollection<string>> Ownership(IEnumerable<Member> members) =>
        members.ToDictionary(m => m.AccountId, m => (ICollection<string>)m.OwnedCourses, StringComparer.Ordinal);

    IReadOnlyList<string> SponsorsOf(Convoy convoy, string courseId) => CourseAccess.Sponsors(Catalogue, courseId, Ownership(convoy.Members));

    // ================================================================== intent and Mode Ready (Addendum 01 §6.2, §7)

    ConvoyResult? ValidateIntent(Convoy convoy, ConvoyIntent intent)
    {
        if (intent.Kind == IntentKind.Campaign && intent.CampaignMode == CampaignMode.Hard)
        {
            ConvoyStageAccess access = CampaignProgress.Evaluate(CampaignMode.Hard, convoy.Members.Select(m => m.Progress).ToList());
            if (!access.ModeAllowed) return ConvoyResult.Fail("mode_locked", access.Explanation);
        }
        if (intent.Kind == IntentKind.Challenges && intent.TrialId is not null && trials.Find(intent.TrialId) is null)
            return ConvoyResult.Fail("unknown_trial", "Unknown Team Trial.");
        return null;
    }

    /// <summary>
    /// Leader sets the persistent Intent: a new mode-proposal revision. Old Mode Ready and Event Ready cannot authorize it;
    /// an open ballot and any event proposal end; the leader's proposal counts as their own Mode Ready.
    /// </summary>
    public ConvoyResult SetIntent(string accountId, ConvoyIntent intent)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Phase is ConvoyPhase.Allocating or ConvoyPhase.InMatch)
                return ConvoyResult.Fail("event_frozen", "Wait until the current event has finished.");
            if (Cooldown(convoy) is { } cooling) return cooling;
            if (ValidateIntent(convoy, intent) is { } invalid) return invalid;
            CancelBallot(convoy, "the leader changed the mode");
            if (convoy.Ballot is not null) convoy.Ballot = null;
            convoy.PostEvent = null; // a new intent is a new destination
            convoy.Intent = intent;
            convoy.ModeEntered = false;
            convoy.EventProposal = null;
            convoy.Phase = ConvoyPhase.ModeCheck;
            ReissueModeProposal(convoy);
            convoy.LastReadyRequest = Now;
            TouchMember(convoy.Find(accountId)!);
            Changed(convoy);
            Broadcast(convoy, "ready.requested", new { kind = "mode", modeRevision = convoy.ModeRevision, intent = intent.Wire() });
            return ConvoyResult.Success(new { modeRevision = convoy.ModeRevision });
        }
    }

    public ConvoyResult SetModeReady(string accountId, long modeRevision, bool ready)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            if (convoy.Intent is null) return ConvoyResult.Fail("bad_phase", "There is no mode proposal.");
            if (convoy.Phase is ConvoyPhase.Allocating or ConvoyPhase.InMatch)
                return ConvoyResult.Fail("event_frozen", "The event has already started.");
            if (modeRevision != convoy.ModeRevision)
                return ConvoyResult.Fail("stale_revision", "The mode proposal changed; review the current one.");
            Member m = convoy.Find(accountId)!;
            TouchMember(m);
            if (ready) convoy.ModeReady[accountId] = modeRevision;
            else if (convoy.ModeReady.Remove(accountId)) CancelBallot(convoy, $"{m.DisplayName} is no longer Mode Ready");
            Changed(convoy);
            return ConvoyResult.Success();
        }
    }

    /// <summary>Leader commits the mode once every current member is Mode Ready for this revision. Never starts an event.</summary>
    public ConvoyResult EnterMode(string accountId, long modeRevision)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Intent is null || convoy.Phase != ConvoyPhase.ModeCheck)
                return ConvoyResult.Fail("bad_phase", "There is no mode proposal waiting to be entered.");
            if (modeRevision != convoy.ModeRevision)
                return ConvoyResult.Fail("stale_revision", "The mode proposal changed.");
            string[] waiting = NotModeReady(convoy);
            if (waiting.Length > 0)
                return ConvoyResult.Fail("not_all_ready", $"Waiting for {waiting.Length} member(s) to be Mode Ready: {string.Join(", ", waiting)}.");
            if (ValidateIntent(convoy, convoy.Intent) is { } invalid) return invalid;
            convoy.ModeEntered = true;
            convoy.Phase = ConvoyPhase.EventSelection;
            Changed(convoy);
            Observe(o => o.Preempted(convoy.SessionId, convoy.Revision, "mode-entered")); // suspend diversions at this boundary
            return ConvoyResult.Success(new { intent = convoy.Intent.Wire(), modeRevision });
        }
    }

    string[] NotModeReady(Convoy convoy) => convoy.Members
        .Where(m => m.Away || !convoy.ModeReady.TryGetValue(m.AccountId, out long rev) || rev != convoy.ModeRevision)
        .Select(m => m.DisplayName).ToArray();

    /// <summary>New mode-proposal revision: every Mode Ready is cleared; the (present) leader re-affirms automatically.</summary>
    void ReissueModeProposal(Convoy convoy)
    {
        if (convoy.Intent is null) return;
        convoy.ModeRevision = convoy.NextProposalRevision();
        convoy.ModeReady.Clear();
        if (convoy.Find(convoy.LeaderId) is not null) convoy.ModeReady[convoy.LeaderId] = convoy.ModeRevision;
        convoy.ModeOpenedAt = Now;
    }

    // ================================================================== Freeplay course vote (Addendum 01 §6.2, D07)

    /// <summary>The Voting toggle and its duration are leader-prepared at any time except while a vote is running.</summary>
    public ConvoyResult ConfigureVoting(string accountId, bool enabled, int? durationSeconds)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Ballot is { State: BallotState.Open })
                return ConvoyResult.Fail("ballot_open", "Wait for the vote to close or cancel it first.");
            if (durationSeconds is { } d && !CoreBallot.ValidDuration(d))
                return ConvoyResult.Fail("invalid_request", "Voting lasts 15, 30, 45 or 60 seconds.");
            convoy.VotingEnabled = enabled;
            if (durationSeconds is { } seconds) convoy.VotingSeconds = seconds;
            Changed(convoy);
            return ConvoyResult.Success(new { enabled, durationSeconds = convoy.VotingSeconds });
        }
    }

    /// <summary>
    /// Opens a server-deadline course vote. Only in an entered Freeplay mode with a known submode, voting ON, and only
    /// when EVERY current member (leader included) is Mode Ready for the current revision. One draw per mode agreement.
    /// </summary>
    public ConvoyResult OpenBallot(string accountId, int? durationSeconds, BallotOptions options)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Intent is not { Kind: IntentKind.Freeplay } intent || !convoy.ModeEntered ||
                convoy.Phase is not (ConvoyPhase.EventSelection or ConvoyPhase.ReadyCheck))
                return ConvoyResult.Fail("bad_phase", "Enter a Freeplay mode before opening a course vote.");
            if (intent.Submode is null)
                return ConvoyResult.Fail("invalid_request", "Choose Sprint, Circuit, Drift Attack or Time Attack before opening a vote.");
            if (intent.Submode == "cup")
                return ConvoyResult.Fail("ballot_unsupported", "Custom Cup legs are selected directly by the leader.");
            if (convoy.PostEvent is not null)
                return ConvoyResult.Fail("post_event_open", "Finish the post-event decision first (Continue, then the leader's Advance).");
            if (!convoy.VotingEnabled) return ConvoyResult.Fail("voting_off", "Turn voting ON first.");
            if (convoy.Ballot is { State: BallotState.Open or BallotState.Frozen })
                return ConvoyResult.Fail("ballot_open", "A course vote is already open.");
            if (convoy.Ballot is { State: BallotState.Resolved } resolved && resolved.ModeRevision == convoy.ModeRevision)
                return ConvoyResult.Fail("ballot_already_drawn", "This vote was already resolved. Select directly, or agree the mode again to hold a new vote.");
            string[] waiting = NotModeReady(convoy);
            if (waiting.Length > 0)
                return ConvoyResult.Fail("not_all_ready", $"Voting opens once every member is Mode Ready. Waiting for: {string.Join(", ", waiting)}.");
            int duration = durationSeconds ?? convoy.VotingSeconds;
            if (!CoreBallot.ValidDuration(duration)) return ConvoyResult.Fail("invalid_request", "Voting lasts 15, 30, 45 or 60 seconds.");
            if (ValidateFreeplayOptions(convoy, intent.Submode, options.AiCount, options.CarCapPi, options.Weather, options.AiRivals) is { } bad) return bad;

            if (convoy.Phase == ConvoyPhase.ReadyCheck)
            {
                convoy.EventProposal = null; // an unstarted proposal gives way to the vote
                convoy.Phase = ConvoyPhase.EventSelection;
            }
            var ballot = new BallotSession
            {
                Revision = convoy.NextProposalRevision(), ModeRevision = convoy.ModeRevision, Mode = intent.Submode, DurationSeconds = duration,
                OpenedAt = Now, Deadline = Now + TimeSpan.FromSeconds(duration), State = BallotState.Open, Options = options,
            };
            convoy.Ballot = ballot;
            TouchMember(convoy.Find(accountId)!);
            Changed(convoy);
            Broadcast(convoy, "ready.requested", new { kind = "vote", ballotRevision = ballot.Revision, mode = ballot.Mode, deadline = ballot.Deadline });
            return ConvoyResult.Success(new { ballotRevision = ballot.Revision, deadline = ballot.Deadline, durationSeconds = duration });
        }
    }

    /// <summary>
    /// One changeable ballot per active member until the server deadline. The course must support the vote's mode and be
    /// convoy-accessible (some current member owns it). <paramref name="courseId"/> null withdraws the ballot.
    /// </summary>
    public ConvoyResult Vote(string accountId, long ballotRevision, string? courseId)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            if (convoy.Ballot is not { } b) return ConvoyResult.Fail("bad_phase", "No course vote is open.");
            if (FreezeIfExpired(b)) Changed(convoy);
            if (ballotRevision != b.Revision) return ConvoyResult.Fail("stale_revision", "That vote has ended or was replaced.");
            if (b.State != BallotState.Open) return ConvoyResult.Fail("ballot_closed", "Voting has closed.");
            Member m = convoy.Find(accountId)!;
            TouchMember(m);
            if (courseId is null)
            {
                b.Ballots.Remove(accountId);
                Changed(convoy);
                return ConvoyResult.Success(new { ballotRevision = b.Revision, courseId = (string?)null });
            }
            if (!Catalogue.TryCourse(courseId, out CourseDef course))
                return ConvoyResult.Fail("invalid_request", "Unknown course.");
            if (!FreeplayRules.Supports(course, b.Mode))
                return ConvoyResult.Fail("mode_unsupported", $"{course.Name} does not support {FreeplayRules.Label(b.Mode)}.");
            if (SponsorsOf(convoy, courseId).Count == 0)
                return ConvoyResult.Fail("course_locked", $"Nobody in this convoy has access to {course.Name}.");
            b.Ballots[accountId] = courseId;
            Changed(convoy);
            return ConvoyResult.Success(new { ballotRevision = b.Revision, courseId });
        }
    }

    /// <summary>
    /// Leader draws once from the frozen ballots (one ticket per accepted ballot, server CSPRNG, Core Ballot.Draw). The
    /// result and ballot revision are stored; a retransmit returns the same winner. The drawn course becomes a frozen
    /// event proposal that still needs Event Ready from everyone.
    /// </summary>
    public ConvoyResult DrawBallot(string accountId, long ballotRevision)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Ballot is not { } b) return ConvoyResult.Fail("bad_phase", "No course vote to draw from.");
            if (FreezeIfExpired(b)) Changed(convoy);
            if (ballotRevision != b.Revision) return ConvoyResult.Fail("stale_revision", "That vote has ended or was replaced.");
            if (b.State == BallotState.Resolved)
                return b.Result!.Method == "draw"
                    ? ConvoyResult.Success(DrawResultWire(b, replayed: true))
                    : ConvoyResult.Fail("ballot_resolved", "The leader already selected a course for this vote.");
            if (b.State == BallotState.Open)
                return ConvoyResult.Fail("ballot_open", "The vote is still open; the draw happens after the deadline.");
            if (b.Ballots.Count == 0)
                return ConvoyResult.Fail("no_votes", "No votes received. Select a course directly.");

            var frozen = new Dictionary<string, string>(b.Ballots, StringComparer.Ordinal);
            string winner = CoreBallot.Draw(frozen, random.NextUInt32(), out int index)!;
            ConvoyResult built = BuildSettings(convoy, new EventRequest(null, winner, b.Mode, b.Options.Weather, b.Options.AiCount,
                b.Options.CarCapPi, null, AiRivals: b.Options.AiRivals));
            if (!built.Ok) return built; // unreachable in practice: roster/sponsor changes cancel the ballot first
            b.State = BallotState.Resolved;
            b.Result = new BallotResult(winner, "draw", index, frozen.Count, frozen.Values.Count(v => v == winner), false, Now, CoreBallot.Canonical(frozen));
            OpenEventProposal(convoy, (EventSettings)built.Value!, "draw", b.Revision);
            Changed(convoy);
            Broadcast(convoy, "ready.requested", new { kind = "event", proposalRevision = convoy.EventProposal!.Revision });
            return ConvoyResult.Success(DrawResultWire(b, replayed: false));
        }
    }

    object DrawResultWire(BallotSession b, bool replayed)
    {
        BallotResult r = b.Result!;
        return new
        {
            ballotRevision = b.Revision, courseId = r.CourseId, method = r.Method, ballotIndex = r.BallotIndex, totalBallots = r.TotalBallots,
            votes = r.Votes, replayed,
        };
    }

    /// <summary>Leader cancels an undrawn vote (one notice); a resolved draw cannot be cancelled to fish for another result.</summary>
    public ConvoyResult CancelBallotByLeader(string accountId, long ballotRevision)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Ballot is not { } b || b.Revision != ballotRevision)
                return ConvoyResult.Fail("stale_revision", "That vote has ended or was replaced.");
            if (b.State == BallotState.Resolved)
                return ConvoyResult.Fail("ballot_resolved", "A resolved vote cannot be cancelled; select a different event directly instead.");
            CancelBallot(convoy, "the leader cancelled it");
            Changed(convoy);
            return ConvoyResult.Success();
        }
    }

    bool FreezeIfExpired(BallotSession b)
    {
        if (b.State != BallotState.Open || Now < b.Deadline) return false;
        b.State = BallotState.Frozen;
        return true;
    }

    void CancelBallot(Convoy convoy, string reason)
    {
        if (convoy.Ballot is not { State: BallotState.Open or BallotState.Frozen }) return;
        convoy.Ballot = null;
        SetNotice(convoy, "ballot_cancelled", $"The course vote was cancelled: {reason}. Everyone must be Mode Ready again before a new vote.");
    }

    // ================================================================== diversions (Addendum 02 §1.2)

    /// <summary>
    /// Coarse diversion participation ("which toy, if any"). A deliberate user action (clears Away) that never changes Mode
    /// Ready, Event Ready or the proposal — only real event, rules or roster changes, or an applied performance change do.
    /// </summary>
    public ConvoyResult SetDiversion(string accountId, string? toy)
    {
        if (toy is not null && !ConvoyRules.Diversions.Contains(toy))
            return ConvoyResult.Fail("invalid_request", $"toy must be one of: {string.Join(", ", ConvoyRules.Diversions)}, or null.");
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            Member m = convoy.Find(accountId)!;
            TouchMember(m);
            if (m.Diversion == toy) return ConvoyResult.Success(new { diversion = toy });
            m.Diversion = toy;
            Changed(convoy);
            return ConvoyResult.Success(new { diversion = toy });
        }
    }

    // ================================================================== post-event decision (Addendum 02 §7, D203)

    /// <summary>
    /// The real next step after a settled event: Next Stage when every current member may select it, otherwise Retry Stage
    /// on the limiting frontier; after S30 "campaign complete" (never an S31, never a silent Hard start); Freeplay and
    /// Challenges return to their selection.
    /// </summary>
    PostEventDestination ComputeDestination(Convoy c, EventSettings s)
    {
        if (s.Kind != "campaign")
            return new PostEventDestination("event-setup", s.Kind == "trial" ? "Return to Challenges" : "Return to Event Setup", null, null, Array.Empty<string>());
        CampaignMode mode = s.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal;
        List<MemberProgress> progress = c.Members.Select(m => m.Progress).ToList();
        ConvoyStageAccess access = CampaignProgress.Evaluate(mode, progress);
        int n = s.StageNumber;
        string label = mode == CampaignMode.Hard ? "Hard" : "Normal";
        IReadOnlyList<string> needs = c.Members.Where(m => !m.Progress.Cleared(mode)[n - 1]).Select(m => m.AccountId).ToList();
        if (n >= Limits.CampaignStages && needs.Count == 0)
            return new PostEventDestination("campaign-complete", $"{label} campaign complete — return to the map", null, s.Mode, needs);
        if (n < Limits.CampaignStages && access.CanSelect(n + 1))
        {
            string next = CampaignProgress.StageLabel(n + 1);
            return new PostEventDestination("next-stage", $"Next Stage — {next}", next, s.Mode, Array.Empty<string>());
        }
        if (access.CanSelect(n))
            return new PostEventDestination("retry-stage", $"Retry Stage — {CampaignProgress.StageLabel(n)}", CampaignProgress.StageLabel(n), s.Mode, needs);
        return new PostEventDestination("event-setup", "Return to the campaign map", null, s.Mode, needs);
    }

    void OpenPostEvent(Convoy convoy, string sourceResultId, EventSettings source)
    {
        var decision = new PostEventDecision
        {
            SourceResultId = sourceResultId, Source = source, RosterRevision = convoy.RosterRevision,
            DestinationRevision = convoy.NextProposalRevision(), Destination = ComputeDestination(convoy, source), OpenedAt = Now,
        };
        convoy.PostEvent = decision;
        Broadcast(convoy, "ready.requested", new
        {
            kind = "post-event", destinationRevision = decision.DestinationRevision, destination = decision.Destination.Kind,
            label = decision.Destination.Label,
        });
    }

    /// <summary>A roster change re-checks the destination; a different destination is a new revision (choices reset, one notice).</summary>
    void PostEventRosterChanged(Convoy convoy)
    {
        if (convoy.PostEvent is not { } d) return;
        d.RosterRevision = convoy.RosterRevision;
        PostEventDestination now = ComputeDestination(convoy, d.Source);
        if (now.Same(d.Destination)) return;
        d.Destination = now;
        d.DestinationRevision = convoy.NextProposalRevision();
        d.Choices.Clear();
        SetNotice(convoy, "post_event_changed", $"The next step changed to \"{now.Label}\" after a roster change; choose again.");
    }

    /// <summary>Continue / ServiceBreak / Undecided for the current destination revision. Silence is never consent.</summary>
    public ConvoyResult ChoosePostEvent(string accountId, long destinationRevision, string? choice)
    {
        if (choice is not ("continue" or "service-break" or "undecided"))
            return ConvoyResult.Fail("invalid_request", "choice must be \"continue\", \"service-break\" or \"undecided\".");
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            if (convoy.PostEvent is not { } d) return ConvoyResult.Fail("bad_phase", "There is no post-event decision open.");
            if (destinationRevision != d.DestinationRevision)
                return ConvoyResult.Fail("stale_revision", "The next step changed; review it and choose again.");
            Queue<DateTimeOffset> changes = d.Changes.TryGetValue(accountId, out var q) ? q : d.Changes[accountId] = new();
            while (changes.Count > 0 && Now - changes.Peek() >= ConvoyRules.PostEventChoiceWindow) changes.Dequeue();
            if (changes.Count >= ConvoyRules.PostEventChoiceLimit)
                return ConvoyResult.Fail("rate_limited", "You changed your choice too often; wait a moment.",
                    (long)Math.Ceiling((changes.Peek() + ConvoyRules.PostEventChoiceWindow - Now).TotalMilliseconds));
            changes.Enqueue(Now);
            TouchMember(convoy.Find(accountId)!);
            bool intermissionBefore = d.Choices.ContainsValue("service-break");
            if (choice == "undecided") d.Choices.Remove(accountId);
            else d.Choices[accountId] = choice;
            if (!intermissionBefore && choice == "service-break")
                SetNotice(convoy, "service_break", "A service break was requested: the convoy is in intermission. Take your time — Garage, meet, a toy or a quiet menu.");
            Changed(convoy);
            return ConvoyResult.Success(new { destinationRevision, choice });
        }
    }

    /// <summary>
    /// Leader's Advance: only on unanimous Continue (the leader's Advance is their own Continue; solo is immediate). Revalidates
    /// roster, progress and destination; moves to the stage briefing (a new event proposal that still needs everyone's Event
    /// Ready) or back to selection. It never starts a race.
    /// </summary>
    public ConvoyResult AdvancePostEvent(string accountId, long destinationRevision)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.PostEvent is not { } d) return ConvoyResult.Fail("bad_phase", "There is no post-event decision open.");
            if (destinationRevision != d.DestinationRevision)
                return ConvoyResult.Fail("stale_revision", "The next step changed; review it again.");
            PostEventDestination now = ComputeDestination(convoy, d.Source);
            if (!now.Same(d.Destination))
            {
                PostEventRosterChanged(convoy);
                Changed(convoy);
                return ConvoyResult.Fail("stale_revision", "The next step changed; everyone must choose again.");
            }
            string[] waiting = convoy.Members
                .Where(m => m.AccountId != convoy.LeaderId && (!d.Choices.TryGetValue(m.AccountId, out string? c) || c != "continue"))
                .Select(m => m.DisplayName).ToArray();
            if (waiting.Length > 0)
                return ConvoyResult.Fail("not_all_continue", $"Advance needs everyone's Continue. Waiting for: {string.Join(", ", waiting)}.");

            EventSettings? briefing = null;
            if (d.Destination.StageId is { } stageId)
            {
                ConvoyResult built = BuildSettings(convoy, new EventRequest(stageId, null, null, d.Source.Weather, null, null, null));
                if (!built.Ok) return built;
                briefing = (EventSettings)built.Value!;
            }
            d.Choices[accountId] = "continue";
            convoy.PostEvent = null;
            convoy.Notice = null;
            convoy.NoticeCode = null;
            if (briefing is not null)
            {
                OpenEventProposal(convoy, briefing, "post-event", null);
                Changed(convoy);
                Broadcast(convoy, "ready.requested", new { kind = "event", proposalRevision = convoy.EventProposal!.Revision });
                return ConvoyResult.Success(new { destination = d.Destination.Kind, stageId = briefing.StageId, proposalRevision = convoy.EventProposal.Revision });
            }
            convoy.Phase = ConvoyPhase.EventSelection;
            Changed(convoy);
            return ConvoyResult.Success(new { destination = d.Destination.Kind });
        }
    }

    object? PostEventWire(Convoy c)
    {
        if (c.PostEvent is not { } d) return null;
        var choices = c.Members.Select(m => new
        {
            accountId = m.AccountId,
            choice = d.Choices.TryGetValue(m.AccountId, out string? v) ? v : "undecided",
        }).ToList();
        bool unanimous = c.Members.Where(m => m.AccountId != c.LeaderId).All(m => d.Choices.TryGetValue(m.AccountId, out string? v) && v == "continue");
        return new
        {
            sourceResultId = d.SourceResultId,
            rosterRevision = d.RosterRevision,
            destinationRevision = d.DestinationRevision,
            destination = new { kind = d.Destination.Kind, label = d.Destination.Label, stageId = d.Destination.StageId, mode = d.Destination.Mode, needs = d.Destination.Needs },
            state = d.Choices.ContainsValue("service-break") ? "intermission" : "deciding",
            choices,
            continueCount = choices.Count(x => x.choice == "continue"),
            serviceBreakCount = choices.Count(x => x.choice == "service-break"),
            undecidedCount = choices.Count(x => x.choice == "undecided"),
            advanceEnabled = unanimous,
            solo = c.Members.Count == 1,
            openedAt = d.OpenedAt,
            quietAfter = d.OpenedAt + ConvoyRules.PostEventQuietAfter,
            quiet = Now >= d.OpenedAt + ConvoyRules.PostEventQuietAfter,
        };
    }

    // ================================================================== event proposal and Event Ready

    public ConvoyResult ProposeEvent(string accountId, EventRequest request)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Phase is not (ConvoyPhase.EventSelection or ConvoyPhase.ReadyCheck) || !convoy.ModeEntered || convoy.Intent is null)
                return ConvoyResult.Fail("bad_phase", "Enter a mode before proposing an event.");
            if (convoy.PostEvent is not null)
                return ConvoyResult.Fail("post_event_open", "Everyone chooses Continue or Service Break first; the leader then Advances (or changes the intent).");
            if (convoy.Ballot is { } open && FreezeIfExpired(open)) Changed(convoy);
            if (convoy.Ballot is { State: BallotState.Open })
                return ConvoyResult.Fail("ballot_open", "A course vote is open: wait for the deadline or cancel it.");
            if (Cooldown(convoy) is { } cooling) return cooling;
            ConvoyResult built = BuildSettings(convoy, request);
            if (!built.Ok) return built;
            var settings = (EventSettings)built.Value!;

            convoy.Notice = null;
            convoy.NoticeCode = null;
            if (convoy.Ballot is { State: BallotState.Frozen } frozen)
            {
                // Leader selection after the deadline: clearly not a draw.
                frozen.State = BallotState.Resolved;
                frozen.Result = new BallotResult(settings.CourseId, "leader-selection", -1, frozen.Ballots.Count,
                    frozen.Ballots.Values.Count(v => v == settings.CourseId), false, Now, CoreBallot.Canonical(frozen.Ballots));
            }
            else if (convoy.Ballot is { State: BallotState.Resolved, Result: { Method: "draw", Overridden: false } drawn } resolved &&
                     resolved.ModeRevision == convoy.ModeRevision && drawn.CourseId != settings.CourseId)
            {
                resolved.Result = drawn with { Overridden = true };
                SetNotice(convoy, "draw_overridden",
                    $"The leader replaced the drawn course {drawn.CourseId} with {settings.CourseId}. Everyone must ready again.");
            }
            OpenEventProposal(convoy, settings, "leader", null);
            TouchMember(convoy.Find(accountId)!);
            Changed(convoy);
            Broadcast(convoy, "ready.requested", new { kind = "event", proposalRevision = convoy.EventProposal!.Revision });
            return ConvoyResult.Success(new { proposalRevision = convoy.EventProposal.Revision });
        }
    }

    void OpenEventProposal(Convoy convoy, EventSettings settings, string origin, long? ballotRevision)
    {
        convoy.EventProposal = new EventProposal
        {
            Revision = convoy.NextProposalRevision(), RosterRevision = convoy.RosterRevision, Settings = settings, OpenedAt = Now,
            Origin = origin, BallotRevision = ballotRevision,
        };
        convoy.Phase = ConvoyPhase.ReadyCheck;
        convoy.LastReadyRequest = Now;
    }

    void ReissueEventProposal(Convoy convoy)
    {
        if (convoy.EventProposal is not { } ep) return;
        ep.Revision = convoy.NextProposalRevision();
        ep.RosterRevision = convoy.RosterRevision;
        ep.Ready.Clear();
        ep.OpenedAt = Now;
    }

    ConvoyResult? ValidateFreeplayOptions(Convoy convoy, string? submode, int aiCount, int? carCapPi, string? weather, IReadOnlyList<string>? aiRivals)
    {
        if (weather is not null && !ConvoyRules.Weathers.Contains(weather)) return ConvoyResult.Fail("invalid_request", "Unknown weather preset.");
        int cap = carCapPi ?? PerformanceIndex.Max;
        if (cap < PerformanceIndex.Min || cap > PerformanceIndex.Max) return ConvoyResult.Fail("invalid_request", "Car cap must be a PI 100–999.");
        if (aiCount < 0) return ConvoyResult.Fail("invalid_request", "AI count cannot be negative.");
        if (submode == "time-attack" && (aiCount > 0 || aiRivals is { Count: > 0 }))
            return ConvoyResult.Fail("invalid_request", "Time Attack has no live opponents; target recordings are replays.");
        int capacity = Limits.MaxRaceVehicles - convoy.Members.Count;
        if (aiCount > capacity)
            return ConvoyResult.Fail("capacity_exceeded",
                $"{convoy.Members.Count} drivers leave room for at most {capacity} AI ({Limits.MaxRaceVehicles} cars per race).");
        if (aiRivals is not null)
        {
            if (aiRivals.Count > aiCount || aiRivals.Distinct().Count() != aiRivals.Count)
                return ConvoyResult.Fail("invalid_request", "Choose each opponent once, no more than the AI count.");
            AiPlacementContext context = submode == "cup" ? AiPlacementContext.CupSubstitution : AiPlacementContext.FreeplayOpponent;
            foreach (string id in aiRivals)
            {
                if (!Catalogue.TryRival(id, out _)) return ConvoyResult.Fail("rival_not_allowed", $"Unknown rival {id}.");
                if (!FinalRivals.Allowed(id, context))
                    return ConvoyResult.Fail("rival_not_allowed", $"{id} races only in its own campaign finale.");
            }
        }
        return null;
    }

    ConvoyResult BuildSettings(Convoy convoy, EventRequest r)
    {
        ConvoyIntent intent = convoy.Intent!;
        string weather = r.Weather ?? "stage-default";
        if (!ConvoyRules.Weathers.Contains(weather)) return ConvoyResult.Fail("invalid_request", "Unknown weather preset.");

        switch (intent.Kind)
        {
            case IntentKind.Freeplay:
            {
                string? mode = intent.Submode ?? r.FreeplayMode;
                if (mode is null || !FreeplayRules.Submodes.Contains(mode)) return ConvoyResult.Fail("invalid_request", "Choose a Freeplay mode.");
                if (r.FreeplayMode is not null && r.FreeplayMode != mode)
                    return ConvoyResult.Fail("invalid_request", $"The convoy agreed to {FreeplayRules.Label(mode)}; change the intent to switch modes.");
                if (r.Collision is not null && r.Collision != ConvoyRules.CollisionFor(mode))
                    return ConvoyResult.Fail("invalid_request", "Contact follows the mode: Time Attack is non-contact, every other event uses light contact.");
                int ai = r.AiCount ?? 0;
                if (ValidateFreeplayOptions(convoy, mode, ai, r.CarCapPi, weather, r.AiRivals) is { } bad) return bad;

                List<string> courses;
                if (mode == "cup")
                {
                    courses = (r.CupLegs ?? Array.Empty<string>()).ToList();
                    if (courses.Count < ConvoyRules.MinCupLegs || courses.Count > ConvoyRules.MaxCupLegs || courses.Distinct().Count() != courses.Count)
                        return ConvoyResult.Fail("invalid_request", $"A Custom Cup has {ConvoyRules.MinCupLegs}–{ConvoyRules.MaxCupLegs} different legs.");
                }
                else
                {
                    if (r.CourseId is null) return ConvoyResult.Fail("invalid_request", "Choose a course.");
                    courses = new List<string> { r.CourseId };
                }
                foreach (string courseId in courses)
                {
                    if (!Catalogue.TryCourse(courseId, out CourseDef course) || course.Kind == "tutorial")
                        return ConvoyResult.Fail("invalid_request", $"Unknown Freeplay course {courseId}.");
                    if (!FreeplayRules.Supports(course, mode))
                        return ConvoyResult.Fail("mode_unsupported", $"{course.Name} does not support {FreeplayRules.Label(mode)}.");
                    if (SponsorsOf(convoy, courseId).Count == 0)
                        return ConvoyResult.Fail("course_locked", $"Nobody in this convoy has access to {course.Name}. Buy it or clear its Normal stage first.");
                }
                return ConvoyResult.Success(new EventSettings
                {
                    Kind = "freeplay", CourseId = courses[0], FreeplayMode = mode, Weather = weather, AiCount = ai,
                    CarCapPi = r.CarCapPi ?? PerformanceIndex.Max, Collision = ConvoyRules.CollisionFor(mode),
                    CupLegs = mode == "cup" ? courses : null, AiRivals = r.AiRivals is { Count: > 0 } ? r.AiRivals.ToList() : null,
                });
            }
            case IntentKind.Challenges:
            {
                string? trialId = r.TrialId ?? intent.TrialId;
                if (intent.TrialId is not null && r.TrialId is not null && r.TrialId != intent.TrialId)
                    return ConvoyResult.Fail("invalid_request", "The convoy agreed to a different trial; change the intent first.");
                if (trials.Find(trialId) is not { } trial) return ConvoyResult.Fail("unknown_trial", "Choose a Team Trial.");
                if (trial.Difficulty(r.Difficulty) is not { } difficulty)
                    return ConvoyResult.Fail("invalid_request", $"Difficulty must be one of: {string.Join(", ", trial.Difficulties.Select(d => d.Id))}.");
                if (r.Collision is not null && r.Collision != "light-contact")
                    return ConvoyResult.Fail("invalid_request", "Team Trials use light contact.");
                if (r.AiCount is not null || r.AiRivals is not null || r.CarCapPi is not null)
                    return ConvoyResult.Fail("invalid_request", "A Team Trial's roster, AI and car cap are fixed by the trial.");
                if (SponsorsOf(convoy, trial.Course).Count == 0)
                    return ConvoyResult.Fail("course_locked", $"Nobody in this convoy has access to {trial.Course}.");
                return ConvoyResult.Success(new EventSettings
                {
                    Kind = "trial", CourseId = trial.Course, FreeplayMode = trial.Format, Weather = weather,
                    AiCount = Limits.TeamTrialSideSize * 2 - convoy.Members.Count, CarCapPi = trial.CarCapPi, Collision = "light-contact",
                    TrialId = trial.Id, Difficulty = difficulty.Id,
                });
            }
            default:
            {
                if (r.Collision is not null && r.Collision != "light-contact")
                    return ConvoyResult.Fail("invalid_request", "Campaign races use light contact.");
                if (r.AiCount is not null || r.AiRivals is not null)
                    return ConvoyResult.Fail("invalid_request", "Campaign opposition is authored per stage.");
                CampaignMode campaignMode = intent.CampaignMode;
                if (!content.TryStage(r.StageId, out StageDef stage))
                    return ConvoyResult.Fail("invalid_request", "Choose a campaign stage.");
                ConvoyStageAccess access = CampaignProgress.Evaluate(campaignMode, convoy.Members.Select(m => m.Progress).ToList());
                if (!access.CanSelect(stage.Number))
                    return ConvoyResult.Fail(access.ModeAllowed ? "stage_locked" : "mode_locked", access.Explanation);
                BenchmarkInfo benchmark = content.BenchmarkFor(stage, campaignMode);
                return ConvoyResult.Success(new EventSettings
                {
                    Kind = "campaign", Mode = campaignMode == CampaignMode.Hard ? "hard" : "normal", StageId = stage.Id,
                    StageNumber = stage.Number, StageType = stage.Type, CourseId = stage.Course, Weather = weather,
                    AiCount = (campaignMode == CampaignMode.Hard ? stage.Hard : stage.Normal).Opponents.Count,
                    CarCapPi = stage.MaxPI, Collision = "light-contact", BenchmarkTargetMs = benchmark.Benchmark.TargetTimeMs,
                    BenchmarkProvisional = benchmark.Provisional, BenchmarkSource = benchmark.Source,
                    RequiresBeatingFeaturedRival = benchmark.Benchmark.RequiresBeatingFeaturedRival,
                });
            }
        }
    }

    static IReadOnlyList<string> CoursesOf(EventSettings s) => s.CupLegs is { Count: > 0 } legs ? legs : new[] { s.CourseId };

    /// <summary>Why an unstarted proposal is no longer valid for the current roster (campaign frontier, course sponsors).</summary>
    string? ProposalInvalidReason(Convoy convoy, EventSettings s)
    {
        if (s.Kind == "campaign")
        {
            CampaignMode mode = s.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal;
            ConvoyStageAccess access = CampaignProgress.Evaluate(mode, convoy.Members.Select(m => m.Progress).ToList());
            return access.CanSelect(s.StageNumber) ? null : $"{s.StageId} is no longer available to this convoy. {access.Explanation}";
        }
        foreach (string courseId in CoursesOf(s))
            if (SponsorsOf(convoy, courseId).Count == 0)
                return $"{Catalogue.Course(courseId).Name} is no longer available: no current member owns it (its last sponsor left). Choose another course.";
        // A Freeplay AI request that no longer fits beside the new roster is clamped with an explanation at allocation
        // (Core RosterPlanner), never by ejecting a human.
        return null;
    }

    /// <summary>
    /// Readies against the exact proposal revision AND the member's current loadout revision. <paramref name="fresh"/> is the
    /// server-resolved applied build of the member's selected instance read just before this call: a performance change
    /// found here (the Garage applied something else) bumps the loadout revision and answers <c>stale_revision</c>; the car
    /// cap is checked against the server PI.
    /// </summary>
    public ConvoyResult SetReady(string accountId, long proposalRevision, long loadoutRevision, bool ready, LoadoutInfo? fresh = null)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            if (convoy.Phase is ConvoyPhase.Allocating or ConvoyPhase.InMatch)
                return ConvoyResult.Fail("event_frozen", "The event has already started.");
            if (convoy.Phase != ConvoyPhase.ReadyCheck || convoy.EventProposal is not { } p)
                return ConvoyResult.Fail("bad_phase", "There is no event proposal open.");
            if (proposalRevision != p.Revision)
                return ConvoyResult.Fail("stale_revision", "The event changed; review it and ready again.");
            Member m = convoy.Find(accountId)!;
            TouchMember(m);
            if (!ready)
            {
                p.Ready.Remove(accountId);
                Changed(convoy);
                return ConvoyResult.Success();
            }
            if (m.Loadout is null)
                return ConvoyResult.Fail("loadout_required", "Choose a car first.");
            if (fresh is not null && m.Loadout.InstanceId is not null && m.Loadout.InstanceId == fresh.InstanceId && ApplyFresh(convoy, m, fresh))
            {
                Changed(convoy);
                return ConvoyResult.Fail("stale_revision", "Your car's applied build changed in the Garage; ready again with the current loadout.");
            }
            if (loadoutRevision != m.LoadoutRevision)
                return ConvoyResult.Fail("stale_revision", "Your loadout changed; ready again with the current one.");
            if (!PerformanceIndex.IsLegalFor(m.Loadout.CarPi, p.Settings.CarCapPi))
                return ConvoyResult.Fail("loadout_illegal", $"Your car (PI {m.Loadout.CarPi}) is over this event's cap of {p.Settings.CarCapPi}.");
            p.Ready[accountId] = loadoutRevision;
            Changed(convoy);
            return ConvoyResult.Success();
        }
    }

    // ================================================================== start and match lifecycle

    /// <summary>Active members, i.e. the entrants if the leader started now.</summary>
    public IReadOnlyList<string> PendingEntrants(string accountId)
    {
        lock (gate)
            return ConvoyOf(accountId) is { } convoy ? ActiveMembers(convoy).Select(m => m.AccountId).ToList() : Array.Empty<string>();
    }

    /// <summary>
    /// Revalidates everything atomically (spec §4.2): leader, proposal revision, every entrant ready against this revision
    /// and their current loadout, legality, one shared client version, stage access with FRESH stored progress, course
    /// sponsorship with FRESH stored entitlements. Plans the typed roster with Core <see cref="RosterPlanner"/> and freezes
    /// guest passes. On success the convoy is frozen in Allocating and the plan is returned for allocation.
    /// </summary>
    /// <param name="frozenBuilds">Server-resolved applied builds of the entrants' selected instances, read just before this
    /// call (the control channel passes them; directory-level tests may omit them). Each must still match the performance
    /// hash the entrant readied with, otherwise that entrant is unreadied and the start is refused; the car cap is checked
    /// against these server PIs and the builds are frozen into the plan.</param>
    public (ConvoyError? Error, MatchPlan? Plan) BeginStart(string accountId, long proposalRevision,
        IReadOnlyDictionary<string, MemberProgress> freshProgress, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? freshCourses = null,
        IReadOnlyDictionary<string, Garage.EntrantBuild>? frozenBuilds = null)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return (error.Error, null);
            if (convoy!.Phase != ConvoyPhase.ReadyCheck || convoy.EventProposal is not { } p)
                return (new ConvoyError("bad_phase", "There is no event proposal to start."), null);
            if (proposalRevision != p.Revision || p.RosterRevision != convoy.RosterRevision)
                return (new ConvoyError("stale_revision", "The event or roster changed; everyone must ready again."), null);

            List<Member> entrants = ActiveMembers(convoy).ToList();
            string[] notReady = entrants.Where(m => m.Away || m.Loadout is null || !p.Ready.TryGetValue(m.AccountId, out long rev) || rev != m.LoadoutRevision)
                .Select(m => m.AccountId).ToArray();
            if (entrants.Count == 0 || notReady.Length > 0)
                return (new ConvoyError("not_all_ready", $"Not ready: {string.Join(", ", notReady)}."), null);
            if (frozenBuilds is not null)
                foreach (Member m in entrants.Where(m => m.Loadout!.InstanceId is not null))
                {
                    if (!frozenBuilds.TryGetValue(m.AccountId, out Garage.EntrantBuild? frozen) || frozen.InstanceId != m.Loadout!.InstanceId)
                        return (new ConvoyError("loadout_illegal", $"{m.DisplayName}'s applied build could not be validated by the server; it needs repair in the Garage."), null);
                    if (ApplyFresh(convoy, m, m.Loadout with { CarPi = frozen.Pi, PerformanceHash = frozen.BuildHash, AppliedRevision = frozen.AppliedRevision }))
                    {
                        Changed(convoy);
                        return (new ConvoyError("not_all_ready", $"{m.DisplayName}'s applied build changed; they must ready again."), null);
                    }
                }
            if (entrants.FirstOrDefault(m => !PerformanceIndex.IsLegalFor(m.Loadout!.CarPi, p.Settings.CarCapPi)) is { } illegal)
                return (new ConvoyError("loadout_illegal", $"{illegal.AccountId}'s car is over the cap."), null);

            ClientVersion? version = entrants[0].Session.Version;
            if (version is null || entrants.Any(m => m.Session.Version != version))
                return (new ConvoyError("version_mismatch", "Entrants are running different game builds or content versions."), null);

            foreach (Member m in entrants)
            {
                if (!freshProgress.TryGetValue(m.AccountId, out MemberProgress? progress))
                    return (new ConvoyError("stale_revision", "Progress changed; try again."), null);
                m.Progress = progress;
                if (freshCourses is not null)
                {
                    if (!freshCourses.TryGetValue(m.AccountId, out IReadOnlyCollection<string>? courses))
                        return (new ConvoyError("stale_revision", "Course access changed; try again."), null);
                    m.OwnedCourses = new HashSet<string>(courses, StringComparer.Ordinal);
                }
            }

            EventSettings settings = p.Settings;
            List<string> humans = entrants.Select(m => m.AccountId).ToList();
            RaceRoster roster;
            try
            {
                switch (settings.Kind)
                {
                    case "campaign":
                    {
                        CampaignMode mode = settings.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal;
                        ConvoyStageAccess access = CampaignProgress.Evaluate(mode, entrants.Select(m => m.Progress).ToList());
                        if (!access.CanSelect(settings.StageNumber))
                            return (new ConvoyError("stage_locked", access.Explanation), null);
                        StageDef stage = Catalogue.Stage(settings.StageId!);
                        StageSide side = mode == CampaignMode.Hard ? stage.Hard : stage.Normal;
                        roster = RosterPlanner.PlanCampaign(humans, side.Opponents, stage.Id, mode);
                        break;
                    }
                    case "trial":
                    {
                        TeamTrialDef trial = trials.Find(settings.TrialId) ?? throw new ArgumentException($"Unknown Team Trial {settings.TrialId}");
                        TeamTrialDifficulty difficulty = trial.Difficulty(settings.Difficulty) ?? throw new ArgumentException("Unknown trial difficulty");
                        roster = RosterPlanner.PlanTeamTrial(humans, difficulty.AllyPool, difficulty.OpponentPool);
                        break;
                    }
                    default:
                        roster = RosterPlanner.PlanFreeplay(humans, FreeplayRules.Format(settings.FreeplayMode), settings.AiCount,
                            AiPool(settings.AiRivals, settings.AiCount));
                        break;
                }
            }
            catch (ArgumentException e)
            {
                return (new ConvoyError("roster_invalid", e.Message), null);
            }

            // Course access (Addendum 01 §5.2): every course needs a sponsor among the entrants NOW; the others receive
            // event-scoped guest passes frozen into the plan (they survive the sponsor's later departure or DQ).
            var passes = new List<GuestPass>();
            var sponsors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (settings.Kind != "campaign")
            {
                Dictionary<string, ICollection<string>> owned = Ownership(entrants);
                foreach (string courseId in CoursesOf(settings))
                {
                    IReadOnlyList<string> owners = CourseAccess.Sponsors(Catalogue, courseId, owned);
                    if (owners.Count == 0)
                        return (new ConvoyError("course_locked", $"{Catalogue.Course(courseId).Name} has no sponsor in the convoy any more."), null);
                    string primary = owners.Contains(convoy.LeaderId) ? convoy.LeaderId : owners[0];
                    sponsors[courseId] = owners;
                    foreach (Member m in entrants.Where(m => !owners.Contains(m.AccountId)))
                        passes.Add(new GuestPass(m.AccountId, courseId, primary));
                }
            }

            string? note = roster.WasClamped ? roster.Explanation : null;
            bool purePvP = settings.Kind == "freeplay" && roster.Vehicles == roster.Humans && roster.Humans >= 2 && settings.FreeplayMode is "sprint" or "circuit";
            List<string> ai = roster.Entries.Where(e => e.Kind == ActorKind.Ai).Select(e => e.EntrantId).ToList();
            var plan = new MatchPlan
            {
                PlanId = Hashing.RandomId("plan_", 8), ConvoyId = convoy.Id, ProposalRevision = p.Revision,
                RosterRevision = convoy.RosterRevision, Settings = settings with { AiCount = ai.Count },
                Entrants = entrants.Select(m => new PlannedEntrant(m.AccountId, m.DisplayName, m.Loadout!, m.LoadoutRevision,
                    frozenBuilds is not null && frozenBuilds.TryGetValue(m.AccountId, out Garage.EntrantBuild? b) && b.InstanceId == m.Loadout!.InstanceId ? b : null)).ToList(),
                AiEntrants = ai, Roster = roster.Entries.Select(RosterSlot.From).ToList(), GuestPasses = passes, Sponsors = sponsors,
                FeaturedRival = settings.Kind == "campaign" ? roster.FeaturedRival : null,
                GridNote = note, PurePvP = purePvP, Version = version,
            };
            convoy.Phase = ConvoyPhase.Allocating;
            convoy.PendingPlanId = plan.PlanId;
            convoy.RunningSettings = plan.Settings;
            convoy.FrozenEntrants.Clear();
            convoy.FrozenEntrants.UnionWith(humans);
            convoy.DepartedEntrants.Clear();
            if (note is not null) SetNotice(convoy, "grid_clamped", note);
            Changed(convoy);
            Observe(o => o.Preempted(convoy.SessionId, convoy.Revision, "race-allocation")); // toys pause; the start never waits for them
            return (null, plan);
        }
    }

    /// <summary>Freeplay opponents: the leader's explicit (validated) picks first, then a server-shuffled random pool that
    /// excludes campaign-finale-only rivals (Core FinalRivals, RandomPool context).</summary>
    List<string> AiPool(IReadOnlyList<string>? requested, int count)
    {
        var pool = new List<string>(requested ?? Array.Empty<string>());
        List<string> others = Catalogue.Rivals.Select(r => r.Id)
            .Where(id => !pool.Contains(id) && FinalRivals.Allowed(id, AiPlacementContext.RandomPool)).ToList();
        for (int i = others.Count - 1; i > 0; i--)
        {
            int j = (int)(random.NextUInt32() % (uint)(i + 1));
            (others[i], others[j]) = (others[j], others[i]);
        }
        pool.AddRange(others.Take(Math.Max(0, count - pool.Count)));
        return pool;
    }

    public void CompleteStart(MatchPlan plan, ActiveMatch match)
    {
        lock (gate)
        {
            if (!convoys.TryGetValue(plan.ConvoyId, out Convoy? convoy) || convoy.PendingPlanId != plan.PlanId) return;
            convoy.PendingPlanId = null;
            convoy.Match = match;
            convoy.MatchStartedAt = Now;
            convoy.Phase = ConvoyPhase.InMatch;
            Changed(convoy);
        }
    }

    public void FailStart(MatchPlan plan, string reason)
    {
        lock (gate)
        {
            if (!convoys.TryGetValue(plan.ConvoyId, out Convoy? convoy) || convoy.PendingPlanId != plan.PlanId) return;
            convoy.PendingPlanId = null;
            convoy.RunningSettings = null;
            convoy.FrozenEntrants.Clear();
            convoy.DepartedEntrants.Clear();
            Observe(o => o.PreemptionEnded(convoy.SessionId, convoy.Revision, "start-failed")); // the prior toys resume with the same snapshot
            if (convoy.Dormant)
            {
                convoy.EventProposal = null;
                convoy.Phase = convoy.Intent is null ? ConvoyPhase.Idle : ConvoyPhase.ModeCheck;
                return;
            }
            convoy.Phase = ConvoyPhase.ReadyCheck; // readiness kept; the leader may retry
            if (convoy.EventProposal is { } p && p.RosterRevision != convoy.RosterRevision)
            {
                ReissueEventProposal(convoy); // the roster changed while allocating: everyone readies again
                if (ProposalInvalidReason(convoy, p.Settings) is { } invalid)
                {
                    convoy.EventProposal = null;
                    convoy.Phase = ConvoyPhase.EventSelection;
                    SetNotice(convoy, "proposal_withdrawn", invalid);
                }
            }
            SetNotice(convoy, "allocation_failed", reason);
            Changed(convoy);
        }
    }

    /// <summary>Whether a frozen entrant may still receive a racer ticket (not departed since allocation).</summary>
    public bool RacerEligible(string convoyId, string accountId)
    {
        lock (gate)
            return convoys.TryGetValue(convoyId, out Convoy? c) && c.FrozenEntrants.Contains(accountId) && !c.DepartedEntrants.Contains(accountId) &&
                   c.Find(accountId) is not null;
    }

    /// <summary>Settlement (or abort) finished: back to event selection in the same mode, with refreshed progress/courses.</summary>
    public void MatchEnded(string convoyId, string matchId, IReadOnlyDictionary<string, MemberProgress>? progress,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? courses = null)
    {
        lock (gate)
        {
            if (!convoys.TryGetValue(convoyId, out Convoy? convoy) || convoy.Match?.MatchId != matchId) return;
            foreach (Member m in convoy.Members)
            {
                if (progress is not null && progress.TryGetValue(m.AccountId, out MemberProgress? p)) m.Progress = p;
                if (courses is not null && courses.TryGetValue(m.AccountId, out IReadOnlyCollection<string>? owned))
                    m.OwnedCourses = new HashSet<string>(owned, StringComparer.Ordinal);
            }
            EventSettings? ran = convoy.RunningSettings;
            EndMatch(convoy);
            // Results are recorded: open the shared Continue / Service Break decision (Addendum 02 §7).
            if (ran is not null && convoy.Members.Count > 0 && !convoy.Dormant) OpenPostEvent(convoy, matchId, ran);
            Changed(convoy);
            Observe(o => o.PreemptionEnded(convoy.SessionId, convoy.Revision, "event-finished"));
        }
    }

    void EndMatch(Convoy convoy)
    {
        convoy.Match = null;
        convoy.EventProposal = null;
        convoy.Ballot = null;
        convoy.PostEvent = null;
        convoy.RunningSettings = null;
        convoy.FrozenEntrants.Clear();
        convoy.DepartedEntrants.Clear();
        convoy.Phase = convoy.ModeEntered ? ConvoyPhase.EventSelection : convoy.Intent is null ? ConvoyPhase.Idle : ConvoyPhase.ModeCheck;
    }

    /// <summary>Every convoy currently in a match, with when the match was allocated (for the match watchdog).</summary>
    public IReadOnlyList<(string ConvoyId, ActiveMatch Match, DateTimeOffset StartedAt)> ActiveMatches()
    {
        lock (gate)
            return convoys.Values.Where(c => c.Match is not null).Select(c => (c.Id, c.Match!, c.MatchStartedAt)).ToList();
    }

    /// <summary>Tells the convoy its match was aborted (system failure): no results, rank or progression.</summary>
    public void MatchAborted(string convoyId, string matchId, string reason)
    {
        lock (gate)
        {
            if (!convoys.TryGetValue(convoyId, out Convoy? convoy) || convoy.Match?.MatchId != matchId) return;
            EndMatch(convoy);
            SetNotice(convoy, "match_aborted", reason);
            Changed(convoy);
            Broadcast(convoy, "match.aborted", new { matchId, reason });
            Observe(o => o.PreemptionEnded(convoy.SessionId, convoy.Revision, "event-aborted"));
        }
    }

    /// <summary>The caller's current match, and whether they are a frozen entrant who has not departed since allocation.</summary>
    public (ActiveMatch? Match, string? ConvoyId, bool IsEntrant) CurrentMatch(string accountId)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { Match: { } match } convoy) return (null, null, false);
            return (match, convoy.Id, match.Entrants.Contains(accountId) && !convoy.DepartedEntrants.Contains(accountId));
        }
    }

    // ================================================================== time-based rules

    /// <summary>Invite expiry, leader-unavailable grace (15 s), ballot deadlines, Away detection (120 s).</summary>
    public void Tick()
    {
        lock (gate)
        {
            DateTimeOffset now = Now;
            PurgeExpiredInvites();
            foreach (Convoy convoy in convoys.Values.ToList())
            {
                if (convoy.DormantSince is { } dormant)
                {
                    // Only the server clock since the loss counts: reconnect attempts and heartbeats never extend it.
                    if (now - dormant >= ConvoyRules.DormantLifetime) Disband(convoy, "dormant-expired");
                    continue;
                }
                bool changed = false;
                if (convoy.LeaderUnavailableSince is { } since && now - since >= ConvoyRules.LeaderTransferAfter)
                {
                    TransferLeadership(convoy, $"{convoy.LeaderName} did not return in time");
                    changed = true;
                }
                if (convoy.Ballot is { } ballot && FreezeIfExpired(ballot)) changed = true;

                DateTimeOffset? opened = convoy.Phase switch
                {
                    ConvoyPhase.ModeCheck => convoy.ModeOpenedAt,
                    ConvoyPhase.ReadyCheck => convoy.EventProposal?.OpenedAt,
                    _ => null,
                };
                if (opened is { } openedAt)
                    foreach (Member m in convoy.Members.Where(m => !m.Away))
                    {
                        DateTimeOffset lastActive = m.Session.LastInteraction > openedAt ? m.Session.LastInteraction : openedAt;
                        if (now - lastActive < ConvoyRules.AwayAfter) continue;
                        m.Away = true;
                        convoy.EventProposal?.Ready.Remove(m.AccountId);
                        if (convoy.Phase == ConvoyPhase.ModeCheck) convoy.ModeReady.Remove(m.AccountId);
                        changed = true;
                    }
                if (changed) Changed(convoy);
            }
        }
    }

    // ================================================================== invalidation and helpers

    /// <summary>
    /// Roster changed: new roster revision; an open or frozen ballot is cancelled; a new mode-proposal revision clears Mode
    /// Ready; an open event proposal gets a new revision with readiness cleared, and is withdrawn (with the reason) when the
    /// new roster cannot access it — a campaign stage beyond the shared frontier or a course whose last sponsor left.
    /// </summary>
    void RosterChanged(Convoy convoy, string why)
    {
        convoy.RosterRevision++;
        CancelBallot(convoy, $"the roster changed ({why})");
        ReissueModeProposal(convoy);
        PostEventRosterChanged(convoy);
        if (convoy.EventProposal is { } ep && convoy.Phase == ConvoyPhase.ReadyCheck)
        {
            ReissueEventProposal(convoy);
            if (ProposalInvalidReason(convoy, ep.Settings) is { } invalid)
            {
                convoy.EventProposal = null;
                convoy.Phase = ConvoyPhase.EventSelection;
                SetNotice(convoy, "proposal_withdrawn", invalid);
            }
        }
    }

    /// <summary>Sets the persistent notice and sends it once as <c>convoy.notice</c>.</summary>
    void SetNotice(Convoy convoy, string code, string message)
    {
        convoy.Notice = message;
        convoy.NoticeCode = code;
        Broadcast(convoy, "convoy.notice", new { code, message });
    }

    ConvoyResult? Cooldown(Convoy convoy)
    {
        if (convoy.LastReadyRequest is { } last && Now - last < ConvoyRules.ReadyRequestCooldown)
            return ConvoyResult.Fail("rate_limited", "Readiness can be requested once every 15 seconds.",
                (long)Math.Ceiling((last + ConvoyRules.ReadyRequestCooldown - Now).TotalMilliseconds));
        return null;
    }

    ConvoyResult? LeaderConvoy(string accountId, out Convoy? convoy)
    {
        convoy = ConvoyOf(accountId);
        if (convoy is null) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
        if (convoy.LeaderId != accountId)
            return convoy.LeaderUnavailableSince is not null
                ? ConvoyResult.Fail("leader_unavailable", "The leader is reconnecting; leader actions are paused.")
                : ConvoyResult.Fail("not_leader", "Only the convoy leader can do that.");
        return null;
    }

    Convoy? ConvoyOf(string accountId) =>
        membership.TryGetValue(accountId, out string? id) && convoys.TryGetValue(id, out Convoy? c) ? c : null;

    static IEnumerable<Member> ActiveMembers(Convoy convoy) => convoy.Members.Where(m => m.Session.Connected);

    void PurgeExpiredInvites()
    {
        foreach (string code in invites.Values.Where(i => i.ExpiresAt <= Now).Select(i => i.Code).ToList())
            invites.Remove(code);
        foreach (string id in friendInvites.Values.Where(i => i.ExpiresAt <= Now).Select(i => i.InviteId).ToList())
            friendInvites.Remove(id);
    }

    static string RandomCode()
    {
        Span<char> chars = stackalloc char[ConvoyRules.InviteCodeLength];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = ConvoyRules.InviteAlphabet[RandomNumberGenerator.GetInt32(ConvoyRules.InviteAlphabet.Length)];
        return new string(chars);
    }

    void Changed(Convoy convoy)
    {
        convoy.Revision++;
        Broadcast(convoy, "convoy.state", Snapshot(convoy));
    }

    void Broadcast(Convoy convoy, string type, object payload)
    {
        foreach (Member m in convoy.Members)
            notifier.Send(m.AccountId, type, convoy.Revision, payload);
    }

    public (long Revision, object? Snapshot) SnapshotFor(string accountId)
    {
        lock (gate)
            return ConvoyOf(accountId) is { } convoy ? (convoy.Revision, Snapshot(convoy)) : (0, null);
    }

    /// <summary>Coarse, shareable convoy state. No wallet, email or private selections (spec §4).</summary>
    object Snapshot(Convoy c)
    {
        var progress = c.Members.Select(m => m.Progress).ToList();
        object Access(CampaignMode mode)
        {
            // A Dormant room has no present members (Addendum 02 D208) but is still snapshotted, e.g. when the watchdog
            // aborts its lost match. Nothing is selectable until someone rejoins.
            if (progress.Count == 0)
                return new { allowed = false, maxSelectableStage = 0, explanation = "No members are present.", limitingPlayers = Array.Empty<string>() };
            ConvoyStageAccess a = CampaignProgress.Evaluate(mode, progress);
            return new { allowed = a.ModeAllowed, maxSelectableStage = a.MaxSelectableStage, explanation = a.Explanation, limitingPlayers = a.LimitingPlayers };
        }
        long cooldownMs = c.LastReadyRequest is { } last ? Math.Max(0, (long)(last + ConvoyRules.ReadyRequestCooldown - Now).TotalMilliseconds) : 0;
        int modeReadyCount = c.Members.Count(m => c.ModeReady.TryGetValue(m.AccountId, out long rev) && rev == c.ModeRevision && !m.Away);
        return new
        {
            convoyId = c.Id,
            convoySessionId = c.SessionId,
            privacy = c.Privacy.Wire(),
            privacyLabel = c.Privacy.Label(),
            phase = c.Phase.ToString(),
            rosterRevision = c.RosterRevision,
            leaderId = c.LeaderId,
            leaderName = c.LeaderName,
            leaderLabel = ConvoyRules.LeaderLabel,
            leadershipEpoch = c.LeadershipEpoch,
            leaderUnavailable = c.LeaderUnavailableSince is { } since
                ? new { since, transferAt = since + ConvoyRules.LeaderTransferAfter }
                : null,
            maxMembers = Limits.MaxConvoyHumans,
            members = c.Members.Select((m, slot) => new
            {
                slot,
                accountId = m.AccountId,
                displayName = m.DisplayName,
                membershipGeneration = m.Generation,
                isLeader = m.AccountId == c.LeaderId,
                connection = m.Session.Connected ? "connected" : "reconnecting",
                presence = (m.Session.Connected ? m.Session.Presence : Presence.Reconnecting).ToString(),
                away = m.Away,
                modeReady = c.ModeReady.TryGetValue(m.AccountId, out long mrev) && mrev == c.ModeRevision,
                eventReady = c.EventProposal is { } ep && ep.Ready.TryGetValue(m.AccountId, out long rev) && rev == m.LoadoutRevision,
                carId = m.Loadout?.CarId,
                loadoutRevision = m.LoadoutRevision,
                cosmeticRevision = m.CosmeticRevision,
                diversion = m.Diversion,
                spectator = c.Match is { } match && (!match.Entrants.Contains(m.AccountId) || c.DepartedEntrants.Contains(m.AccountId)),
            }).ToList(),
            intent = c.Intent?.Wire(),
            modeRevision = c.ModeRevision,
            modeEntered = c.ModeEntered,
            modeReadyCount,
            voting = new { enabled = c.VotingEnabled, durationSeconds = c.VotingSeconds, choices = Limits.BallotSecondsChoices },
            ballot = BallotWire(c),
            freeplayAccess = FreeplayAccessWire(c),
            eventProposal = c.EventProposal is { } e ? EventProposalWire(c, e) : null,
            postEvent = PostEventWire(c),
            campaignAccess = new { normal = Access(CampaignMode.Normal), hard = Access(CampaignMode.Hard) },
            match = c.Match is { } am ? new { matchId = am.MatchId, entrants = am.Entrants, departed = c.DepartedEntrants.Order(StringComparer.Ordinal).ToList() } : null,
            readyRequestCooldownMs = cooldownMs,
            notice = c.Notice,
            noticeCode = c.NoticeCode,
        };
    }

    object? BallotWire(Convoy c)
    {
        if (c.Ballot is not { } b) return null;
        IReadOnlyList<BallotTally> tallies = CoreBallot.Tally(b.Ballots);
        return new
        {
            revision = b.Revision,
            modeRevision = b.ModeRevision,
            state = b.State.ToString().ToLowerInvariant(),
            mode = b.Mode,
            durationSeconds = b.DurationSeconds,
            openedAt = b.OpenedAt,
            deadline = b.Deadline,
            remainingMs = b.State == BallotState.Open ? Math.Max(0, (long)(b.Deadline - Now).TotalMilliseconds) : 0,
            ballots = b.Ballots.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
            tallies = tallies.Select(t => new { courseId = t.CourseId, votes = t.Votes, chance = t.Chance }).ToList(),
            totalBallots = b.Ballots.Count,
            noVotes = b.State != BallotState.Open && b.Ballots.Count == 0,
            options = new { weather = b.Options.Weather, aiCount = b.Options.AiCount, carCapPi = b.Options.CarCapPi, aiRivals = b.Options.AiRivals },
            result = b.Result is { } r
                ? new
                {
                    courseId = r.CourseId, method = r.Method, ballotIndex = r.BallotIndex, totalBallots = r.TotalBallots, votes = r.Votes,
                    chance = r.TotalBallots == 0 ? 0.0 : (double)r.Votes / r.TotalBallots, overridden = r.Overridden, at = r.At,
                    order = r.Order.Select(kv => new { accountId = kv.Key, courseId = kv.Value }).ToList(),
                }
                : null,
        };
    }

    object? FreeplayAccessWire(Convoy c)
    {
        if (c.Intent is not { Kind: IntentKind.Freeplay } intent) return null;
        Dictionary<string, ICollection<string>> owned = Ownership(c.Members);
        string mode = intent.Submode ?? "time-attack"; // unknown submode: every non-tutorial course is listed
        var courses = Catalogue.Courses
            .Where(course => FreeplayRules.Supports(course, mode))
            .Select(course => (course.Id, Sponsors: CourseAccess.Sponsors(Catalogue, course.Id, owned)))
            .Where(x => x.Sponsors.Count > 0)
            .Select(x => new
            {
                courseId = x.Id,
                sponsors = x.Sponsors,
                guests = c.Members.Where(m => !x.Sponsors.Contains(m.AccountId)).Select(m => m.AccountId).ToList(),
            })
            .ToList();
        return new { submode = intent.Submode, courses };
    }

    object EventProposalWire(Convoy c, EventProposal e)
    {
        int humans = c.Members.Count;
        EventSettings s = e.Settings;
        (int friendly, int opposing) = s.Kind switch
        {
            "trial" => (Limits.TeamTrialSideSize - humans, Limits.TeamTrialSideSize),
            "campaign" => (0, s.AiCount),
            _ => (0, s.FreeplayMode == "time-attack" ? 0 : Math.Min(s.AiCount, Limits.MaxRaceVehicles - humans)),
        };
        Dictionary<string, ICollection<string>> owned = Ownership(c.Members);
        return new
        {
            revision = e.Revision,
            rosterRevision = e.RosterRevision,
            origin = e.Origin,
            ballotRevision = e.BallotRevision,
            settings = s,
            ready = e.Ready,
            sponsors = s.Kind == "campaign"
                ? new Dictionary<string, IReadOnlyList<string>>()
                : CoursesOf(s).Distinct().ToDictionary(id => id, id => CourseAccess.Sponsors(Catalogue, id, owned)),
            rosterPreview = new { humans, friendlyAi = friendly, opposingAi = opposing, vehicles = humans + friendly + opposing, contact = s.Collision },
        };
    }
}
