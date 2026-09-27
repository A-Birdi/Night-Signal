using System.Security.Cryptography;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Convoys;

/// <summary>Receives outgoing control messages. Implementations must not block (they are called under the directory lock
/// so that per-convoy message order matches revision order).</summary>
public interface IConvoyNotifier
{
    void Send(string accountId, string type, long revision, object payload);
}

/// <summary>
/// Authoritative convoy state (spec §4, §4.1, §4.2, §4.4): membership, invites, leader, presence, the two ready checks,
/// invalidation, Away detection, disconnect reservation and leader transfer. Transport-independent and synchronous:
/// every operation runs under one lock, bumps the convoy's monotonic revision and publishes a snapshot. Time comes
/// from the injected <see cref="TimeProvider"/>; <see cref="Tick"/> applies time-based rules.
/// </summary>
public sealed class ConvoyDirectory(TimeProvider clock, ContentService content, IConvoyNotifier notifier)
{
    readonly object gate = new();
    readonly Dictionary<string, Convoy> convoys = new();
    readonly Dictionary<string, string> membership = new();        // account -> convoy
    readonly Dictionary<string, Session> sessions = new();         // account -> connection state
    readonly Dictionary<string, Invite> invites = new();           // code -> invite
    readonly Dictionary<string, Queue<DateTimeOffset>> joinAttempts = new();

    sealed class Session
    {
        public bool Connected;
        public DateTimeOffset ConnectedSince;
        public DateTimeOffset? DisconnectedAt;
        public ClientVersion? Version;
    }

    sealed class Member
    {
        public required string AccountId;
        public required string DisplayName;
        public required Session Session;
        public DateTimeOffset JoinedAt;
        public Presence Presence = Presence.InMenus;
        public DateTimeOffset LastInteraction;
        public bool Away;
        public required MemberProgress Progress;
        public LoadoutInfo? Loadout;
        public long LoadoutRevision;
        public long CosmeticRevision;
    }

    sealed class DestinationProposal
    {
        public long Revision;
        public Destination Destination;
        public DateTimeOffset OpenedAt;
        public readonly HashSet<string> Consents = new();
    }

    sealed class EventProposal
    {
        public long Revision;
        public long RosterRevision;
        public required EventSettings Settings;
        public DateTimeOffset OpenedAt;
        public readonly Dictionary<string, long> Ready = new(); // account -> loadout revision readied with
    }

    sealed class Convoy
    {
        public required string Id;
        public ConvoyPrivacy Privacy;
        public long Revision;
        public long RosterRevision;
        public long ProposalCounter;
        public required string LeaderId;
        public readonly List<Member> Members = new();
        public ConvoyPhase Phase = ConvoyPhase.Idle;
        public Destination? Committed;
        public DestinationProposal? DestinationProposal;
        public EventProposal? EventProposal;
        public DateTimeOffset? LastReadyRequest;
        public string? PendingPlanId;
        public ActiveMatch? Match;
        public DateTimeOffset MatchStartedAt;
        public string? Notice;

        public Member? Find(string accountId) => Members.FirstOrDefault(m => m.AccountId == accountId);
        public long NextProposalRevision() => ++ProposalCounter;
    }

    sealed record Invite(string Code, string ConvoyId, DateTimeOffset ExpiresAt);

    DateTimeOffset Now => clock.GetUtcNow();

    // ------------------------------------------------------------------ connection lifecycle

    /// <summary>Control channel opened. A reconnect within the 60 s hold restores the member (leadership is not restored).</summary>
    public void Connected(string accountId, ClientVersion? version)
    {
        lock (gate)
        {
            Session s = SessionFor(accountId);
            s.Connected = true;
            s.ConnectedSince = Now;
            s.DisconnectedAt = null;
            s.Version = version;
            if (ConvoyOf(accountId) is { } convoy)
            {
                Member m = convoy.Find(accountId)!;
                m.Presence = Presence.InMenus;
                m.LastInteraction = Now;
                m.Away = false;
                Changed(convoy);
            }
        }
    }

    /// <summary>Control channel closed: reserve the slot, withdraw readiness, start the leader-transfer timer.</summary>
    public void Disconnected(string accountId)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(accountId, out Session? s) || !s.Connected) return;
            s.Connected = false;
            s.DisconnectedAt = Now;
            if (ConvoyOf(accountId) is { } convoy)
            {
                Unready(convoy, accountId);
                Changed(convoy);
            }
            else
                sessions.Remove(accountId);
        }
    }

    Session SessionFor(string accountId)
    {
        if (!sessions.TryGetValue(accountId, out Session? s))
            sessions[accountId] = s = new Session { Connected = true, ConnectedSince = Now };
        return s;
    }

    // ------------------------------------------------------------------ membership and invites

    public ConvoyResult Create(string accountId, MemberInfo info, ConvoyPrivacy privacy)
    {
        lock (gate)
        {
            if (membership.ContainsKey(accountId))
                return ConvoyResult.Fail("already_in_convoy", "Leave your current convoy first.");
            var convoy = new Convoy { Id = Hashing.RandomId("cv_", 8), Privacy = privacy, LeaderId = accountId };
            convoys[convoy.Id] = convoy;
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
                .Where(c => c.Privacy == ConvoyPrivacy.Discoverable && c.Members.Count < Limits.MaxConvoyMembers)
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => (object)new
                {
                    convoyId = c.Id,
                    leaderName = c.Find(c.LeaderId)?.DisplayName,
                    members = c.Members.Count,
                    maxMembers = Limits.MaxConvoyMembers,
                    privacy = c.Privacy.Wire(),
                    privacyLabel = c.Privacy.Label(),
                    phase = c.Phase.ToString(),
                })
                .ToList();
    }

    ConvoyResult Join(Convoy convoy, string accountId, MemberInfo info)
    {
        if (convoy.Members.Count >= Limits.MaxConvoyMembers)
            return ConvoyResult.Fail("convoy_full", $"A convoy holds at most {Limits.MaxConvoyMembers} members.");
        AddMember(convoy, accountId, info);
        // Joining during a match makes this member a spectator until the next event (spec §4.4).
        if (convoy.Phase is ConvoyPhase.Allocating or ConvoyPhase.InMatch)
            convoy.Notice = $"{info.DisplayName} joined as a spectator until the next event.";
        RosterChanged(convoy);
        Changed(convoy);
        return ConvoyResult.Success(new { convoyId = convoy.Id });
    }

    void AddMember(Convoy convoy, string accountId, MemberInfo info)
    {
        convoy.Members.Add(new Member
        {
            AccountId = accountId, DisplayName = info.DisplayName, Progress = info.Progress, Session = SessionFor(accountId),
            JoinedAt = Now, LastInteraction = Now,
        });
        membership[accountId] = convoy.Id;
    }

    public ConvoyResult Leave(string accountId)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy)
                return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            RemoveMember(convoy, accountId, "left");
            return ConvoyResult.Success();
        }
    }

    void RemoveMember(Convoy convoy, string accountId, string reason)
    {
        convoy.Members.RemoveAll(m => m.AccountId == accountId);
        membership.Remove(accountId);
        if (sessions.TryGetValue(accountId, out Session? s) && !s.Connected)
            sessions.Remove(accountId);
        notifier.Send(accountId, "convoy.closed", convoy.Revision, new { convoyId = convoy.Id, reason });
        if (convoy.Members.Count == 0)
        {
            convoys.Remove(convoy.Id);
            foreach (string code in invites.Values.Where(i => i.ConvoyId == convoy.Id).Select(i => i.Code).ToList())
                invites.Remove(code);
            return;
        }
        if (convoy.LeaderId == accountId)
            convoy.LeaderId = ChooseLeader(convoy, connectedOnly: false)!.AccountId;
        RosterChanged(convoy);
        Changed(convoy);
    }

    // ------------------------------------------------------------------ presence, activity and loadout

    public ConvoyResult SetPresence(string accountId, Presence presence)
    {
        if (presence is Presence.Reconnecting or Presence.Offline)
            return ConvoyResult.Fail("invalid_request", "That presence is set by the server.");
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            Member m = convoy.Find(accountId)!;
            m.Presence = presence;
            TouchMember(m);
            Changed(convoy);
            return ConvoyResult.Success();
        }
    }

    /// <summary>Any deliberate user interaction (including long menu operations reported by the client) clears Away.</summary>
    public void Touch(string accountId)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return;
            Member m = convoy.Find(accountId)!;
            bool wasAway = m.Away;
            TouchMember(m);
            if (wasAway) Changed(convoy);
        }
    }

    void TouchMember(Member m)
    {
        m.LastInteraction = Now;
        m.Away = false;
    }

    /// <summary>
    /// A performance change (car or performance build) bumps the member's loadout revision and unreadies only them;
    /// a cosmetic-only change keeps readiness (spec §4.2). Refused while the event is being allocated (frozen).
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
            bool performance = m.Loadout is null || m.Loadout.CarId != loadout.CarId || m.Loadout.PerformanceHash != loadout.PerformanceHash;
            bool cosmetic = m.Loadout is null || m.Loadout.CosmeticHash != loadout.CosmeticHash;
            m.Loadout = loadout;
            if (performance)
            {
                m.LoadoutRevision++;
                convoy.EventProposal?.Ready.Remove(accountId);
            }
            if (cosmetic) m.CosmeticRevision++;
            Changed(convoy);
            return ConvoyResult.Success(new { loadoutRevision = m.LoadoutRevision, cosmeticRevision = m.CosmeticRevision });
        }
    }

    // ------------------------------------------------------------------ ready check 1: destination

    public ConvoyResult ProposeDestination(string accountId, Destination destination)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Phase is ConvoyPhase.Allocating or ConvoyPhase.InMatch)
                return ConvoyResult.Fail("event_frozen", "Wait until the current event has finished.");
            if (Cooldown(convoy) is { } cooling) return cooling;
            if (destination == Destination.CampaignHard)
            {
                ConvoyStageAccess access = CampaignProgress.Evaluate(CampaignMode.Hard, convoy.Members.Select(m => m.Progress).ToList());
                if (!access.ModeAllowed)
                    return ConvoyResult.Fail("mode_locked", access.Explanation);
            }
            var proposal = new DestinationProposal { Revision = convoy.NextProposalRevision(), Destination = destination, OpenedAt = Now };
            proposal.Consents.Add(accountId); // proposing is the leader's consent
            convoy.DestinationProposal = proposal;
            convoy.EventProposal = null;
            convoy.Committed = null;
            convoy.Phase = ConvoyPhase.DestinationCheck;
            convoy.LastReadyRequest = Now;
            TouchMember(convoy.Find(accountId)!);
            Changed(convoy);
            Broadcast(convoy, "ready.requested", new { kind = "destination", proposalRevision = proposal.Revision, destination = destination.Wire() });
            return ConvoyResult.Success(new { proposalRevision = proposal.Revision });
        }
    }

    public ConvoyResult Consent(string accountId, long proposalRevision, bool consent)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { } convoy) return ConvoyResult.Fail("not_in_convoy", "You are not in a convoy.");
            if (convoy.Phase != ConvoyPhase.DestinationCheck || convoy.DestinationProposal is not { } p)
                return ConvoyResult.Fail("bad_phase", "There is no destination proposal open.");
            if (proposalRevision != p.Revision)
                return ConvoyResult.Fail("stale_revision", "That proposal has changed; review the current one.");
            TouchMember(convoy.Find(accountId)!);
            if (consent) p.Consents.Add(accountId); else p.Consents.Remove(accountId);
            Changed(convoy);
            return ConvoyResult.Success();
        }
    }

    /// <summary>Commits once every connected member (leader included) consented to THIS revision and remains eligible.</summary>
    public ConvoyResult CommitDestination(string accountId, long proposalRevision)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Phase != ConvoyPhase.DestinationCheck || convoy.DestinationProposal is not { } p)
                return ConvoyResult.Fail("bad_phase", "There is no destination proposal open.");
            if (proposalRevision != p.Revision)
                return ConvoyResult.Fail("stale_revision", "That proposal has changed.");
            string[] waiting = convoy.Members.Where(m => m.Session.Connected && (m.Away || !p.Consents.Contains(m.AccountId)))
                .Select(m => m.AccountId).ToArray();
            if (waiting.Length > 0)
                return ConvoyResult.Fail("not_all_ready", $"Waiting for {waiting.Length} member(s): {string.Join(", ", waiting)}.");
            if (p.Destination == Destination.CampaignHard)
            {
                ConvoyStageAccess access = CampaignProgress.Evaluate(CampaignMode.Hard, convoy.Members.Select(m => m.Progress).ToList());
                if (!access.ModeAllowed) return ConvoyResult.Fail("mode_locked", access.Explanation);
            }
            convoy.Committed = p.Destination;
            convoy.DestinationProposal = null;
            convoy.Phase = ConvoyPhase.EventSelection;
            Changed(convoy);
            return ConvoyResult.Success(new { destination = p.Destination.Wire() });
        }
    }

    // ------------------------------------------------------------------ ready check 2: event

    public ConvoyResult ProposeEvent(string accountId, EventRequest request)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return error;
            if (convoy!.Phase is not (ConvoyPhase.EventSelection or ConvoyPhase.ReadyCheck) || convoy.Committed is not { } destination)
                return ConvoyResult.Fail("bad_phase", "Commit a destination before proposing an event.");
            if (Cooldown(convoy) is { } cooling) return cooling;
            ConvoyResult built = BuildSettings(convoy, destination, request);
            if (!built.Ok) return built;
            var proposal = new EventProposal
            {
                Revision = convoy.NextProposalRevision(), RosterRevision = convoy.RosterRevision,
                Settings = (EventSettings)built.Value!, OpenedAt = Now,
            };
            convoy.EventProposal = proposal;
            convoy.Phase = ConvoyPhase.ReadyCheck;
            convoy.LastReadyRequest = Now;
            convoy.Notice = null;
            TouchMember(convoy.Find(accountId)!);
            Changed(convoy);
            Broadcast(convoy, "ready.requested", new { kind = "event", proposalRevision = proposal.Revision });
            return ConvoyResult.Success(new { proposalRevision = proposal.Revision });
        }
    }

    ConvoyResult BuildSettings(Convoy convoy, Destination destination, EventRequest r)
    {
        string weather = r.Weather ?? "stage-default";
        string collision = r.Collision ?? "off"; // campaign contact defaults off (spec §2.6)
        if (!ConvoyRules.Weathers.Contains(weather)) return ConvoyResult.Fail("invalid_request", "Unknown weather preset.");
        if (!ConvoyRules.CollisionRules.Contains(collision)) return ConvoyResult.Fail("invalid_request", "Unknown collision rule.");

        if (destination == Destination.Freeplay)
        {
            if (r.CourseId is null || !content.Catalogue.TryCourse(r.CourseId, out CourseDef course))
                return ConvoyResult.Fail("invalid_request", "Choose a course.");
            string mode = r.FreeplayMode ?? "sprint";
            if (!ConvoyRules.FreeplayModes.Contains(mode)) return ConvoyResult.Fail("invalid_request", "Unknown Freeplay mode.");
            int ai = r.AiCount ?? 0;
            int cap = r.CarCapPi ?? PerformanceIndex.Max;
            if (ai < 0 || ai >= Limits.MaxRaceEntrants) return ConvoyResult.Fail("invalid_request", "AI count must be 0–5.");
            if (cap < PerformanceIndex.Min || cap > PerformanceIndex.Max) return ConvoyResult.Fail("invalid_request", "Car cap must be a PI 100–999.");
            return ConvoyResult.Success(new EventSettings
            {
                Kind = "freeplay", CourseId = course.Id, FreeplayMode = mode, Weather = weather, AiCount = mode == "time-trial" ? 0 : ai,
                CarCapPi = cap, Collision = mode == "drift-attack" ? "off" : collision,
            });
        }

        CampaignMode campaignMode = destination == Destination.CampaignHard ? CampaignMode.Hard : CampaignMode.Normal;
        if (!content.TryStage(r.StageId, out StageDef stage))
            return ConvoyResult.Fail("invalid_request", "Choose a campaign stage.");
        ConvoyStageAccess access = CampaignProgress.Evaluate(campaignMode, convoy.Members.Select(m => m.Progress).ToList());
        if (!access.CanSelect(stage.Number))
            return ConvoyResult.Fail("stage_locked", access.Explanation);
        BenchmarkInfo benchmark = content.BenchmarkFor(stage, campaignMode);
        return ConvoyResult.Success(new EventSettings
        {
            Kind = "campaign", Mode = campaignMode == CampaignMode.Hard ? "hard" : "normal", StageId = stage.Id,
            StageNumber = stage.Number, StageType = stage.Type, CourseId = stage.Course, Weather = weather,
            AiCount = Limits.MaxRaceEntrants - Math.Min(Limits.MaxRaceEntrants, ConnectedMembers(convoy).Count()),
            CarCapPi = stage.MaxPI, Collision = collision, BenchmarkTargetMs = benchmark.Benchmark.TargetTimeMs,
            BenchmarkProvisional = benchmark.Provisional, BenchmarkSource = benchmark.Source,
        });
    }

    /// <summary>Readies against the exact proposal revision AND the member's current loadout revision.</summary>
    public ConvoyResult SetReady(string accountId, long proposalRevision, long loadoutRevision, bool ready)
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
            if (loadoutRevision != m.LoadoutRevision)
                return ConvoyResult.Fail("stale_revision", "Your loadout changed; ready again with the current one.");
            if (!PerformanceIndex.IsLegalFor(m.Loadout.CarPi, p.Settings.CarCapPi))
                return ConvoyResult.Fail("loadout_illegal", $"Your car (PI {m.Loadout.CarPi}) is over this event's cap of {p.Settings.CarCapPi}.");
            p.Ready[accountId] = loadoutRevision;
            Changed(convoy);
            return ConvoyResult.Success();
        }
    }

    // ------------------------------------------------------------------ start and match lifecycle

    /// <summary>Connected members, i.e. the entrants if the leader started now.</summary>
    public IReadOnlyList<string> PendingEntrants(string accountId)
    {
        lock (gate)
            return ConvoyOf(accountId) is { } convoy ? ConnectedMembers(convoy).Select(m => m.AccountId).ToList() : Array.Empty<string>();
    }

    /// <summary>
    /// Revalidates everything atomically (spec §4.2): leader, proposal revision, every entrant ready against this
    /// revision and their current loadout, legality, stage access with FRESH stored progress, one shared client
    /// version. On success the convoy is frozen in Allocating and the plan is returned for allocation.
    /// </summary>
    public (ConvoyError? Error, MatchPlan? Plan) BeginStart(string accountId, long proposalRevision,
        IReadOnlyDictionary<string, MemberProgress> freshProgress)
    {
        lock (gate)
        {
            if (LeaderConvoy(accountId, out Convoy? convoy) is { } error) return (error.Error, null);
            if (convoy!.Phase != ConvoyPhase.ReadyCheck || convoy.EventProposal is not { } p)
                return (new ConvoyError("bad_phase", "There is no event proposal to start."), null);
            if (proposalRevision != p.Revision || p.RosterRevision != convoy.RosterRevision)
                return (new ConvoyError("stale_revision", "The event or roster changed; everyone must ready again."), null);

            List<Member> entrants = ConnectedMembers(convoy).ToList();
            string[] notReady = entrants.Where(m => m.Away || m.Loadout is null || !p.Ready.TryGetValue(m.AccountId, out long rev) || rev != m.LoadoutRevision)
                .Select(m => m.AccountId).ToArray();
            if (entrants.Count == 0 || notReady.Length > 0)
                return (new ConvoyError("not_all_ready", $"Not ready: {string.Join(", ", notReady)}."), null);
            if (entrants.FirstOrDefault(m => !PerformanceIndex.IsLegalFor(m.Loadout!.CarPi, p.Settings.CarCapPi)) is { } illegal)
                return (new ConvoyError("loadout_illegal", $"{illegal.AccountId}'s car is over the cap."), null);

            ClientVersion? version = entrants[0].Session.Version;
            if (version is null || entrants.Any(m => m.Session.Version != version))
                return (new ConvoyError("version_mismatch", "Entrants are running different game builds or content versions."), null);

            foreach (Member m in entrants)
                if (freshProgress.TryGetValue(m.AccountId, out MemberProgress? progress))
                    m.Progress = progress;
                else
                    return (new ConvoyError("stale_revision", "Progress changed; try again."), null);

            IReadOnlyList<string> ai;
            string? replay = null, note = null;
            bool purePvP = false;
            EventSettings settings = p.Settings;
            if (settings.Kind == "campaign")
            {
                CampaignMode mode = settings.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal;
                ConvoyStageAccess access = CampaignProgress.Evaluate(mode, entrants.Select(m => m.Progress).ToList());
                if (!access.CanSelect(settings.StageNumber))
                    return (new ConvoyError("stage_locked", access.Explanation), null);
                StageDef stage = content.Catalogue.Stage(settings.StageId!);
                StageSide side = mode == CampaignMode.Hard ? stage.Hard : stage.Normal;
                CampaignGrid grid = GridPlanner.PlanCampaign(entrants.Count, side.Lead, side.Support);
                ai = grid.LiveAiRivals;
                replay = grid.BenchmarkReplayRival;
                settings = settings with { AiCount = ai.Count };
            }
            else
            {
                FreeplayGrid grid = GridPlanner.ValidateFreeplay(entrants.Count, settings.AiCount);
                ai = Enumerable.Range(1, grid.LiveAi).Select(i => $"ai-{i}").ToList();
                note = grid.WasClamped ? grid.Explanation : null;
                purePvP = grid.Flavor == FreeplayFlavor.PurePvP && settings.FreeplayMode is "sprint" or "circuit";
                settings = settings with { AiCount = grid.LiveAi };
            }

            var plan = new MatchPlan
            {
                PlanId = Hashing.RandomId("plan_", 8), ConvoyId = convoy.Id, ProposalRevision = p.Revision,
                RosterRevision = convoy.RosterRevision, Settings = settings,
                Entrants = entrants.Select(m => new PlannedEntrant(m.AccountId, m.DisplayName, m.Loadout!, m.LoadoutRevision)).ToList(),
                AiEntrants = ai, BenchmarkReplayRival = replay, GridNote = note, PurePvP = purePvP, Version = version,
            };
            convoy.Phase = ConvoyPhase.Allocating;
            convoy.PendingPlanId = plan.PlanId;
            convoy.Notice = note;
            Changed(convoy);
            return (null, plan);
        }
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
            convoy.Phase = ConvoyPhase.ReadyCheck; // readiness kept; the leader may retry
            convoy.Notice = reason;
            Changed(convoy);
        }
    }

    /// <summary>Settlement (or abort) finished: back to event selection with refreshed progress.</summary>
    public void MatchEnded(string convoyId, string matchId, IReadOnlyDictionary<string, MemberProgress>? progress)
    {
        lock (gate)
        {
            if (!convoys.TryGetValue(convoyId, out Convoy? convoy) || convoy.Match?.MatchId != matchId) return;
            if (progress is not null)
                foreach (Member m in convoy.Members)
                    if (progress.TryGetValue(m.AccountId, out MemberProgress? p)) m.Progress = p;
            convoy.Match = null;
            convoy.EventProposal = null;
            convoy.Phase = ConvoyPhase.EventSelection;
            Changed(convoy);
        }
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
            convoy.Match = null;
            convoy.EventProposal = null;
            convoy.Phase = ConvoyPhase.EventSelection;
            convoy.Notice = reason;
            Changed(convoy);
            Broadcast(convoy, "match.aborted", new { matchId, reason });
        }
    }

    /// <summary>The caller's current match, and whether they are a frozen entrant of it.</summary>
    public (ActiveMatch? Match, string? ConvoyId, bool IsEntrant) CurrentMatch(string accountId)
    {
        lock (gate)
        {
            if (ConvoyOf(accountId) is not { Match: { } match } convoy) return (null, null, false);
            return (match, convoy.Id, match.Entrants.Contains(accountId));
        }
    }

    // ------------------------------------------------------------------ time-based rules

    /// <summary>Invite expiry, Away detection (120 s), leader transfer (15 s) and slot release (60 s).</summary>
    public void Tick()
    {
        lock (gate)
        {
            DateTimeOffset now = Now;
            PurgeExpiredInvites();
            foreach (Convoy convoy in convoys.Values.ToList())
            {
                // Reserved slots expire after 60 s (RemoveMember publishes its own snapshot).
                foreach (Member m in convoy.Members.Where(m => !m.Session.Connected && now - m.Session.DisconnectedAt >= ConvoyRules.SlotHold).ToList())
                    RemoveMember(convoy, m.AccountId, "reservation_expired");
                bool changed = false;
                if (!convoys.ContainsKey(convoy.Id)) continue;

                Member? leader = convoy.Find(convoy.LeaderId);
                if (leader is { Session.Connected: false } && now - leader.Session.DisconnectedAt >= ConvoyRules.LeaderTransferAfter &&
                    ChooseLeader(convoy, connectedOnly: true) is { } successor)
                {
                    convoy.LeaderId = successor.AccountId;
                    convoy.Notice = $"{successor.DisplayName} is now the convoy leader.";
                    changed = true;
                }

                DateTimeOffset? opened = convoy.Phase switch
                {
                    ConvoyPhase.DestinationCheck => convoy.DestinationProposal?.OpenedAt,
                    ConvoyPhase.ReadyCheck => convoy.EventProposal?.OpenedAt,
                    _ => null,
                };
                if (opened is { } since)
                    foreach (Member m in convoy.Members.Where(m => m.Session.Connected && !m.Away))
                    {
                        DateTimeOffset lastActive = m.LastInteraction > since ? m.LastInteraction : since;
                        if (now - lastActive < ConvoyRules.AwayAfter) continue;
                        m.Away = true;
                        Unready(convoy, m.AccountId);
                        changed = true;
                    }
                if (changed) Changed(convoy);
            }
        }
    }

    /// <summary>Longest continuously connected member; stable account-ID tie-break.</summary>
    static Member? ChooseLeader(Convoy convoy, bool connectedOnly) =>
        convoy.Members
            .Where(m => m.AccountId != convoy.LeaderId || !connectedOnly)
            .Where(m => !connectedOnly || m.Session.Connected)
            .OrderByDescending(m => m.Session.Connected)
            .ThenBy(m => m.Session.ConnectedSince)
            .ThenBy(m => m.AccountId, StringComparer.Ordinal)
            .FirstOrDefault();

    // ------------------------------------------------------------------ invalidation and helpers

    /// <summary>Roster changed: a new roster revision; every open proposal gets a new revision and all readiness is
    /// cleared. A campaign stage the new roster cannot access is withdrawn back to event selection.</summary>
    void RosterChanged(Convoy convoy)
    {
        convoy.RosterRevision++;
        if (convoy.DestinationProposal is { } dp)
        {
            dp.Revision = convoy.NextProposalRevision();
            dp.Consents.Clear();
            dp.Consents.Add(convoy.LeaderId);
            dp.OpenedAt = Now;
        }
        if (convoy.EventProposal is { } ep && convoy.Phase == ConvoyPhase.ReadyCheck)
        {
            ep.Revision = convoy.NextProposalRevision();
            ep.RosterRevision = convoy.RosterRevision;
            ep.Ready.Clear();
            ep.OpenedAt = Now;
            if (ep.Settings.Kind == "campaign")
            {
                CampaignMode mode = ep.Settings.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal;
                ConvoyStageAccess access = CampaignProgress.Evaluate(mode, convoy.Members.Select(m => m.Progress).ToList());
                if (!access.CanSelect(ep.Settings.StageNumber))
                {
                    convoy.EventProposal = null;
                    convoy.Phase = ConvoyPhase.EventSelection;
                    convoy.Notice = $"{ep.Settings.StageId} is no longer available to this convoy. {access.Explanation}";
                }
            }
        }
    }

    static void Unready(Convoy convoy, string accountId)
    {
        convoy.DestinationProposal?.Consents.Remove(accountId);
        convoy.EventProposal?.Ready.Remove(accountId);
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
        if (convoy.LeaderId != accountId) return ConvoyResult.Fail("not_leader", "Only the convoy leader can do that.");
        return null;
    }

    Convoy? ConvoyOf(string accountId) =>
        membership.TryGetValue(accountId, out string? id) && convoys.TryGetValue(id, out Convoy? c) ? c : null;

    static IEnumerable<Member> ConnectedMembers(Convoy convoy) => convoy.Members.Where(m => m.Session.Connected);

    void PurgeExpiredInvites()
    {
        foreach (string code in invites.Values.Where(i => i.ExpiresAt <= Now).Select(i => i.Code).ToList())
            invites.Remove(code);
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
            ConvoyStageAccess a = CampaignProgress.Evaluate(mode, progress);
            return new { allowed = a.ModeAllowed, maxSelectableStage = a.MaxSelectableStage, explanation = a.Explanation, limitingPlayers = a.LimitingPlayers };
        }
        long cooldownMs = c.LastReadyRequest is { } last ? Math.Max(0, (long)(last + ConvoyRules.ReadyRequestCooldown - Now).TotalMilliseconds) : 0;
        return new
        {
            convoyId = c.Id,
            privacy = c.Privacy.Wire(),
            privacyLabel = c.Privacy.Label(),
            phase = c.Phase.ToString(),
            rosterRevision = c.RosterRevision,
            leaderId = c.LeaderId,
            leaderLabel = ConvoyRules.LeaderLabel,
            maxMembers = Limits.MaxConvoyMembers,
            members = c.Members.Select((m, slot) => new
            {
                slot,
                accountId = m.AccountId,
                displayName = m.DisplayName,
                isLeader = m.AccountId == c.LeaderId,
                connection = m.Session.Connected ? "connected" : "reconnecting",
                presence = (m.Session.Connected ? m.Presence : Presence.Reconnecting).ToString(),
                away = m.Away,
                destinationConsent = c.DestinationProposal?.Consents.Contains(m.AccountId) ?? false,
                eventReady = c.EventProposal is { } ep && ep.Ready.TryGetValue(m.AccountId, out long rev) && rev == m.LoadoutRevision,
                carId = m.Loadout?.CarId,
                loadoutRevision = m.LoadoutRevision,
                cosmeticRevision = m.CosmeticRevision,
                spectator = c.Match is { } match && !match.Entrants.Contains(m.AccountId),
            }).ToList(),
            destinationProposal = c.DestinationProposal is { } dp
                ? new { revision = dp.Revision, destination = dp.Destination.Wire(), consents = dp.Consents.Order(StringComparer.Ordinal).ToList() }
                : null,
            committedDestination = c.Committed?.Wire(),
            eventProposal = c.EventProposal is { } e
                ? new { revision = e.Revision, rosterRevision = e.RosterRevision, settings = e.Settings, ready = e.Ready }
                : null,
            campaignAccess = new { normal = Access(CampaignMode.Normal), hard = Access(CampaignMode.Hard) },
            match = c.Match is { } am ? new { matchId = am.MatchId, entrants = am.Entrants } : null,
            readyRequestCooldownMs = cooldownMs,
            notice = c.Notice,
        };
    }
}
