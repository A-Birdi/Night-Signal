namespace NightSignal.ControlPlane.Convoys;

/// <summary>
/// Convoy-session-scoped extension point (Addendum 02 §1.3–§1.5, §11). Shared downtime state ("While We Wait" toys) will
/// hang off the stable <c>ConvoySessionId</c> and per-membership generation — not the leader, leadership epoch or race.
/// The directory calls observers UNDER ITS LOCK: implementations must not block (enqueue work instead).
/// </summary>
public interface IConvoySessionObserver
{
    /// <summary>An account became an ACTIVE member (join or rejoin) with a new generation; stale generations never regain input rights.</summary>
    void MembershipStarted(string sessionId, string accountId, long generation) { }

    /// <summary>
    /// An active membership ended. <paramref name="reason"/>: "disconnected" (keep a dormant toy seat; an authorized rejoin
    /// restores it) or "left" / "kicked" / "disbanded" (retire the seat, release leases, drop queued actions).
    /// </summary>
    void MembershipEnded(string sessionId, string accountId, long generation, string reason) { }

    /// <summary>A mode transition or race allocation was committed with valid consent: suspend every diversion at this revision.</summary>
    void Preempted(string sessionId, long convoyRevision, string cause) { }

    /// <summary>The last active member was lost to disconnection: persist this compact snapshot (no seats, no race).</summary>
    void Dormant(DormantRoomSnapshot snapshot) { }

    /// <summary>An authorized rejoin restored a dormant room (a new active period).</summary>
    void Restored(string sessionId) { }

    /// <summary>The session ended for good: explicit disband, last voluntary leave, or dormant expiry. Retire its data.</summary>
    void Ended(string sessionId, string reason) { }
}

/// <summary>A rejoin grant carried in a dormant-room snapshot.</summary>
public sealed record DormantGrant(string AccountId, string GrantId, long LeadershipEpoch, bool WasLeader, string? LeaderName,
    DateTimeOffset CreatedAt, bool PromptDismissed);

/// <summary>
/// Compact, persistable state of a Dormant convoy (Addendum 02 D208): identity, leadership, intent and the valid rejoin
/// grants. Deliberately excludes seats, readiness, proposals, ballots and any running race.
/// </summary>
public sealed record DormantRoomSnapshot(
    string SessionId,
    string ConvoyId,
    string Privacy,
    string LeaderId,
    string LeaderName,
    long LeadershipEpoch,
    string? IntentKind,
    string? IntentMode,
    string? IntentSubmode,
    string? IntentTrialId,
    bool VotingEnabled,
    int VotingSeconds,
    long MembershipCounter,
    long ProposalCounter,
    long RosterRevision,
    DateTimeOffset DormantSince,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<DormantGrant> Grants);
