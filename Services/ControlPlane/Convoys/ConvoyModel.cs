using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Convoys;

/// <summary>
/// Coarse actual presence (spec §4, Addendum 01 §7). Reported by the client for any connected account (convoy member or
/// not); Reconnecting/Offline are server-only. Deliberately coarse: no password/reset screens, typed text or fine-grained
/// customization state is ever reported.
/// </summary>
public enum Presence { InMenus, Garage, AtMeet, Browsing, LoadingRace, InRace, Spectating, Reconnecting, Offline }

/// <summary>Friend-list status (Addendum 01 §9.1). <see cref="Unknown"/> means stale data, never a false Online/Offline claim.</summary>
public enum FriendStatus { Available, Garage, AtMeet, Preparing, Loading, Racing, Spectating, Away, Offline, Unknown }

public enum ConvoyPrivacy { InviteOnly, Discoverable }

/// <summary>What the leader intends the convoy to do next (Addendum 01 §7). Separate from anyone's actual presence.</summary>
public enum IntentKind { Campaign, Freeplay, Challenges }

/// <summary>
/// Convoy coordination as seen by the control plane (Addendum 01 §6.2, §7): Idle → ModeCheck (intent proposed, Mode
/// Ready collected) → EventSelection (mode entered; optional course vote) → ReadyCheck (event proposal, Event Ready) →
/// Allocating → InMatch. The game server owns Loading/Countdown/Racing/Results inside InMatch.
/// </summary>
public enum ConvoyPhase { Idle, ModeCheck, EventSelection, ReadyCheck, Allocating, InMatch }

/// <summary>
/// Persistent, replicated leader intent. <see cref="Mode"/> is "normal"/"hard" for Campaign; <see cref="Submode"/> is the
/// Freeplay format (sprint, circuit, drift-attack, time-attack, cup); <see cref="TrialId"/> optionally narrows Challenges.
/// </summary>
public sealed record ConvoyIntent(IntentKind Kind, string? Mode, string? Submode, string? TrialId)
{
    public CampaignMode CampaignMode => Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal;

    public string KindWire => Kind switch { IntentKind.Campaign => "campaign", IntentKind.Freeplay => "freeplay", _ => "challenges" };

    public string Label => Kind switch
    {
        IntentKind.Campaign => Mode == "hard" ? "Hard Campaign" : "Normal Campaign",
        IntentKind.Freeplay => Submode is null ? "Freeplay" : "Freeplay — " + FreeplayRules.Label(Submode),
        _ => TrialId is null ? "Challenges" : "Challenges — " + TrialId,
    };

    public object Wire() => new { kind = KindWire, mode = Mode, submode = Submode, trialId = TrialId, label = Label };
}

public static class ConvoyRules
{
    public const int InviteCodeLength = 8;
    /// <summary>No 0/O, 1/I/L: unambiguous when read aloud or typed from a screenshot.</summary>
    public const string InviteAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(15);
    public const int MaxActiveInvitesPerConvoy = 5;
    public const int MaxActiveFriendInvitesPerConvoy = 12;
    public const int JoinAttemptLimit = 10;
    public static readonly TimeSpan JoinAttemptWindow = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ReadyRequestCooldown = TimeSpan.FromMilliseconds(Limits.ReadyRequestCooldownMs);
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMilliseconds(Limits.ProposalAwayAfterMs);
    public static readonly TimeSpan LeaderTransferAfter = TimeSpan.FromMilliseconds(Limits.LeaderTransferMs);
    /// <summary>A connected account whose client has sent nothing (not even <c>ping</c>) for this long shows as Unknown.</summary>
    public static readonly TimeSpan PresenceStaleAfter = TimeSpan.FromSeconds(90);

    public const int MinCupLegs = 2;
    public const int MaxCupLegs = 5;

    /// <summary>Addendum 02 D208: a room whose members were all lost to disconnection stays Dormant at most this long.</summary>
    public static readonly TimeSpan DormantLifetime = TimeSpan.FromHours(24);

    /// <summary>Addendum 02 §7.1: after this the post-event strip calms down (presentation only — nothing automatic happens).</summary>
    public static readonly TimeSpan PostEventQuietAfter = TimeSpan.FromSeconds(30);
    /// <summary>Ordinary reconsideration is allowed; repeated flipping is bounded.</summary>
    public const int PostEventChoiceLimit = 12;
    public static readonly TimeSpan PostEventChoiceWindow = TimeSpan.FromMinutes(1);

    /// <summary>Addendum 02 §0.1: the five approved diversions plus the private Garage Test Yard (coarse participation only).</summary>
    public static readonly string[] Diversions = { "cap-clash", "pit-crew", "greenlight", "pocket-circuit", "convoy-canvas", "test-yard" };

    public static readonly string[] Weathers = { "stage-default", "dry-night", "wet-night", "dawn", "blue-hour", "fog" };
    /// <summary>Derived from the mode, never chosen freely (Addendum 01 §2): Time Attack is non-contact, all else light contact.</summary>
    public static readonly string[] CollisionRules = { "light-contact", "non-contact" };
    public static string CollisionFor(string? freeplayMode) => freeplayMode == "time-attack" ? "non-contact" : "light-contact";

    public const string LeaderLabel = "Convoy leader";

    /// <summary>Parses an <c>intent.set</c> payload into a typed intent (validation of locks happens in the directory).</summary>
    public static ConvoyIntent? ParseIntent(string? kind, string? mode, string? submode, string? trialId, out string error)
    {
        error = "";
        switch (kind)
        {
            case "campaign":
                string m = mode ?? "normal";
                if (m is not ("normal" or "hard")) { error = "Campaign mode must be \"normal\" or \"hard\"."; return null; }
                if (submode is not null || trialId is not null) { error = "Campaign intents take only a mode."; return null; }
                return new ConvoyIntent(IntentKind.Campaign, m, null, null);
            case "freeplay":
                if (submode is not null && !FreeplayRules.Submodes.Contains(submode))
                { error = "Freeplay submode must be sprint, circuit, drift-attack, time-attack or cup."; return null; }
                if (mode is not null || trialId is not null) { error = "Freeplay intents take only a submode."; return null; }
                return new ConvoyIntent(IntentKind.Freeplay, null, submode, null);
            case "challenges":
                if (mode is not null || submode is not null) { error = "Challenges intents take only an optional trialId."; return null; }
                return new ConvoyIntent(IntentKind.Challenges, null, null, trialId);
            default:
                error = "kind must be \"campaign\", \"freeplay\" or \"challenges\".";
                return null;
        }
    }

    public static string Wire(this ConvoyPrivacy p) => p == ConvoyPrivacy.Discoverable ? "discoverable" : "invite-only";

    public static string Label(this ConvoyPrivacy p) =>
        p == ConvoyPrivacy.Discoverable ? "Discoverable — listed for anyone to join" : "Private — invite code only";

    public static ConvoyPrivacy? ParsePrivacy(string? s) => s switch
    {
        "invite-only" or null => ConvoyPrivacy.InviteOnly,
        "discoverable" => ConvoyPrivacy.Discoverable,
        _ => null,
    };

    /// <summary>Uppercases and removes separators so "abcd-2345" and "ABCD 2345" match.</summary>
    public static string NormalizeCode(string? code) =>
        new string((code ?? "").Where(ch => ch != '-' && ch != ' ').Select(char.ToUpperInvariant).ToArray());
}

/// <summary>
/// Freeplay course/mode compatibility. The content catalogue has no per-course mode list yet, so this derives it from the
/// course's authored format (recommended to move into Core with an explicit per-course table): Sprint needs a
/// point-to-point course, Circuit a lapped one, Drift Attack and Time Attack run on every non-tutorial course, and Custom
/// Cup legs are Sprint or Circuit courses. The tutorial course T00 is never a Freeplay venue.
/// </summary>
public static class FreeplayRules
{
    public static readonly string[] Submodes = { "sprint", "circuit", "drift-attack", "time-attack", "cup" };

    public static string Label(string submode) => submode switch
    {
        "sprint" => "Sprint",
        "circuit" => "Circuit",
        "drift-attack" => "Drift Attack",
        "time-attack" => "Time Attack",
        "cup" => "Custom Cup",
        _ => submode,
    };

    /// <summary>
    /// Whether a course offers a Freeplay submode. Drift Attack needs judged drift zones on the course (the catalogue's
    /// course-drift-zones list); without a catalogue every course is assumed to have them.
    /// </summary>
    public static bool Supports(CourseDef course, string submode, ContentCatalogue? catalogue = null)
    {
        if (course.Kind == "tutorial") return false;
        return submode switch
        {
            "sprint" => course.Format == "sprint",
            "circuit" => course.Format == "circuit",
            "drift-attack" => catalogue?.SupportsDriftAttack(course.Id) ?? true,
            "time-attack" => true,
            "cup" => course.Format is "sprint" or "circuit",
            _ => false,
        };
    }

    public static EventFormat Format(string? submode) => submode switch
    {
        "circuit" => EventFormat.FreeplayCircuit,
        "drift-attack" => EventFormat.DriftAttack,
        "time-attack" => EventFormat.TimeAttack,
        "cup" => EventFormat.Cup,
        _ => EventFormat.FreeplaySprint,
    };

    public static EventKind Kind(string? submode) => submode switch
    {
        "circuit" => EventKind.FreeplayCircuit,
        "drift-attack" => EventKind.FreeplayDriftAttack,
        "time-attack" => EventKind.FreeplayTimeTrial,
        "cup" => EventKind.FreeplayCustomCup,
        _ => EventKind.FreeplaySprint,
    };
}

public sealed record ConvoyError(string Code, string Message, long? RetryAfterMs = null);

public sealed record ConvoyResult(ConvoyError? Error, object? Value = null)
{
    public bool Ok => Error is null;
    public static ConvoyResult Success(object? value = null) => new(null, value);
    public static ConvoyResult Fail(string code, string message, long? retryAfterMs = null) => new(new ConvoyError(code, message, retryAfterMs));
}

/// <summary>
/// What the directory needs to know about a joining member (loaded from the PlayerStore by the caller).
/// <paramref name="OwnedCourses"/> are the account's stored ONLINE course entitlements (starters are implicit in Core).
/// </summary>
public sealed record MemberInfo(string DisplayName, MemberProgress Progress, IReadOnlyCollection<string>? OwnedCourses = null);

/// <summary>
/// The member's chosen car. <paramref name="PerformanceHash"/> and <paramref name="CarPi"/> come from the SERVER-resolved
/// applied build of <paramref name="InstanceId"/> (the ONLINE Garage, Addendum 02 §9–10), never from a client claim;
/// <paramref name="CosmeticHash"/> identifies the visual build (opaque here; for an instance it is the SERVER hash of the
/// stored applied livery, GarageService.AppearanceOf). <paramref name="AppliedRevision"/> is the
/// workspace's applied-build revision that hash was computed from.
/// </summary>
public sealed record LoadoutInfo(string CarId, int CarPi, string PerformanceHash, string CosmeticHash, string? InstanceId = null,
    long AppliedRevision = 0);

/// <summary>Client build/protocol/content reported when the control channel connects.</summary>
public sealed record ClientVersion(string Build, int Protocol, string ContentHash);

/// <summary>
/// Leader's event selection. Unset fields take the stage/Freeplay/trial defaults. <paramref name="AiRivals"/> is an
/// optional explicit Freeplay opponent selection (validated against Core <c>FinalRivals</c>); <paramref name="CupLegs"/>
/// lists Custom Cup courses; <paramref name="TrialId"/>/<paramref name="Difficulty"/> select a Team Trial.
/// </summary>
public sealed record EventRequest(string? StageId, string? CourseId, string? FreeplayMode, string? Weather, int? AiCount,
    int? CarCapPi, string? Collision, IReadOnlyList<string>? CupLegs = null, string? TrialId = null, string? Difficulty = null,
    IReadOnlyList<string>? AiRivals = null, string? ChallengeTrialId = null);

/// <summary>Freeplay options a leader prepares with a course vote; they are applied to the drawn/selected event.</summary>
public sealed record BallotOptions(string? Weather, int AiCount, int? CarCapPi, IReadOnlyList<string>? AiRivals);

/// <summary>Event settings locked into a proposal revision (spec §4.2, §2.1; Addendum 01 §1, §3, §5).</summary>
public sealed record EventSettings
{
    public required string Kind { get; init; }           // campaign | freeplay | trial
    public string? Mode { get; init; }                   // normal | hard (campaign)
    public string? StageId { get; init; }
    public int StageNumber { get; init; }
    public string? StageType { get; init; }
    public required string CourseId { get; init; }
    /// <summary>Freeplay submode (sprint, circuit, drift-attack, time-attack, cup); for a trial, the trial's race format.</summary>
    public string? FreeplayMode { get; init; }
    public required string Weather { get; init; }
    /// <summary>Freeplay: requested live opposing AI. Campaign: authored opponents. Trial: friendly + opposing AI.</summary>
    public int AiCount { get; init; }
    public required int CarCapPi { get; init; }
    public required string Collision { get; init; }
    public long? BenchmarkTargetMs { get; init; }
    public bool BenchmarkProvisional { get; init; }
    public string? BenchmarkSource { get; init; }
    public bool RequiresBeatingFeaturedRival { get; init; }
    /// <summary>Custom Cup legs in order; access is validated and frozen for every leg up front.</summary>
    public IReadOnlyList<string>? CupLegs { get; init; }
    /// <summary>Custom Cup: the leg this event races (0-based; <see cref="CourseId"/> is <c>CupLegs[CupLeg]</c>).</summary>
    public int CupLeg { get; init; }
    /// <summary>
    /// A challenge trial (docs/CHALLENGE_TRIALS.md): raced as a non-contact Freeplay Time Attack on the trial's course in its
    /// supplied loaner (every entrant; never a garage build); the game server judges it and settlement keeps the passes.
    /// </summary>
    public string? ChallengeTrialId { get; init; }
    public IReadOnlyList<string>? AiRivals { get; init; }
    public string? TrialId { get; init; }
    public string? Difficulty { get; init; }
}

/// <summary>One frozen human entrant. <paramref name="Build"/> is the server-resolved applied build frozen at start (null only
/// for loadouts that carry no car instance, i.e. directory-level tests).</summary>
public sealed record PlannedEntrant(string AccountId, string DisplayName, LoadoutInfo Loadout, long LoadoutRevision,
    NightSignal.ControlPlane.Garage.EntrantBuild? Build = null);

/// <summary>One frozen roster actor (Addendum 01 §1.1): kind, team, role and driver identity stored explicitly.</summary>
public sealed record RosterSlot(string EntrantId, string Kind, string Team, string Role, string DriverId)
{
    public static RosterSlot From(RaceRosterEntry e) => new(e.EntrantId, e.Kind == ActorKind.Human ? "human" : "ai",
        e.Team == RosterTeam.Player ? "player" : "opposing", e.Role switch
        {
            RosterRole.FeaturedRival => "featured-rival",
            RosterRole.SupportRival => "support-rival",
            RosterRole.FriendlyAi => "friendly-ai",
            RosterRole.OpposingAi => "opposing-ai",
            _ => "driver",
        }, e.DriverId);
}

/// <summary>Event-scoped guest access issued at allocation (Addendum 01 §5.2); never permanent ownership.</summary>
public sealed record GuestPass(string AccountId, string CourseId, string SponsorId);

/// <summary>Everything frozen when the leader presses Start (spec §4.3): roster, settings, loadouts, grid, course passes.</summary>
public sealed class MatchPlan
{
    public required string PlanId { get; init; }
    public required string ConvoyId { get; init; }
    public required long ProposalRevision { get; init; }
    public required long RosterRevision { get; init; }
    public required EventSettings Settings { get; init; }
    public required IReadOnlyList<PlannedEntrant> Entrants { get; init; }
    /// <summary>Live AI entrant IDs (campaign/trial: rival IDs, featured first; Freeplay: ai-1..ai-n).</summary>
    public required IReadOnlyList<string> AiEntrants { get; init; }
    /// <summary>Typed roster (humans and AI) with teams and roles.</summary>
    public IReadOnlyList<RosterSlot> Roster { get; init; } = Array.Empty<RosterSlot>();
    public IReadOnlyList<GuestPass> GuestPasses { get; init; } = Array.Empty<GuestPass>();
    /// <summary>Course → current members who owned it at allocation (the sponsors of the guest passes).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Sponsors { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public string? FeaturedRival { get; init; }
    public string? GridNote { get; init; }
    public bool PurePvP { get; init; }
    public required ClientVersion Version { get; init; }
}

/// <summary>A successful allocation, as far as convoy members need to know.</summary>
public sealed record ActiveMatch(string MatchId, string ServerId, string Host, int Port, ClientVersion Version,
    IReadOnlyList<string> Entrants);

/// <summary>Per-friend live view computed by the directory (status, convoy and server-validated actions).</summary>
public sealed record FriendPresence(FriendStatus Status, string? ConvoyId, bool InYourConvoy, bool CanRejoin, bool CanInvite);
