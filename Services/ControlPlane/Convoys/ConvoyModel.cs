using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Convoys;

/// <summary>Coarse presence (spec §4). Reconnecting/Offline are set by the server, the rest by the client.</summary>
public enum Presence { InMenus, AtMeet, LoadingRace, InRace, Spectating, Reconnecting, Offline }

public enum ConvoyPrivacy { InviteOnly, Discoverable }

public enum Destination { CampaignNormal, CampaignHard, Freeplay }

/// <summary>
/// Convoy coordination as seen by the control plane. Spec §4's Loading/Intro/Countdown/Racing/Results belong to
/// the authoritative game server; here they are the single <see cref="InMatch"/> phase plus member presence.
/// </summary>
public enum ConvoyPhase { Idle, DestinationCheck, EventSelection, ReadyCheck, Allocating, InMatch }

public static class ConvoyRules
{
    public const int InviteCodeLength = 8;
    /// <summary>No 0/O, 1/I/L: unambiguous when read aloud or typed from a screenshot.</summary>
    public const string InviteAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(15);
    public const int MaxActiveInvitesPerConvoy = 5;
    public const int JoinAttemptLimit = 10;
    public static readonly TimeSpan JoinAttemptWindow = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ReadyRequestCooldown = TimeSpan.FromMilliseconds(Limits.ReadyRequestCooldownMs);
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMilliseconds(Limits.ProposalAwayAfterMs);
    public static readonly TimeSpan LeaderTransferAfter = TimeSpan.FromMilliseconds(Limits.LeaderTransferMs);

    // Provisional value sets until the content catalogue defines weather presets and Freeplay rules.
    public static readonly string[] Weathers = { "stage-default", "dry-night", "wet-night", "dawn", "blue-hour", "fog" };
    public static readonly string[] FreeplayModes = { "sprint", "circuit", "drift-attack", "time-attack" };
    /// <summary>Derived from the mode, never chosen freely (Addendum 01 §2): Time Attack is non-contact, all else light contact.</summary>
    public static readonly string[] CollisionRules = { "light-contact", "non-contact" };
    public static string CollisionFor(string? freeplayMode) => freeplayMode == "time-attack" ? "non-contact" : "light-contact";

    public const string LeaderLabel = "Convoy leader";

    public static string Wire(this Destination d) => d switch
    {
        Destination.CampaignNormal => "campaign-normal",
        Destination.CampaignHard => "campaign-hard",
        _ => "freeplay",
    };

    public static Destination? ParseDestination(string? s) => s switch
    {
        "campaign-normal" => Destination.CampaignNormal,
        "campaign-hard" => Destination.CampaignHard,
        "freeplay" => Destination.Freeplay,
        _ => null,
    };

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

public sealed record ConvoyError(string Code, string Message, long? RetryAfterMs = null);

public sealed record ConvoyResult(ConvoyError? Error, object? Value = null)
{
    public bool Ok => Error is null;
    public static ConvoyResult Success(object? value = null) => new(null, value);
    public static ConvoyResult Fail(string code, string message, long? retryAfterMs = null) => new(new ConvoyError(code, message, retryAfterMs));
}

/// <summary>What the directory needs to know about a joining member (loaded from the PlayerStore by the caller).</summary>
public sealed record MemberInfo(string DisplayName, MemberProgress Progress);

/// <summary>The member's chosen car. Hashes identify the performance build and the visual build (opaque here).</summary>
public sealed record LoadoutInfo(string CarId, int CarPi, string PerformanceHash, string CosmeticHash);

/// <summary>Client build/protocol/content reported when the control channel connects.</summary>
public sealed record ClientVersion(string Build, int Protocol, string ContentHash);

/// <summary>Leader's event selection. Unset fields take the stage/Freeplay defaults.</summary>
public sealed record EventRequest(string? StageId, string? CourseId, string? FreeplayMode, string? Weather, int? AiCount,
    int? CarCapPi, string? Collision);

/// <summary>Event settings locked into a proposal revision (spec §4.2, §2.1).</summary>
public sealed record EventSettings
{
    public required string Kind { get; init; }           // campaign | freeplay
    public string? Mode { get; init; }                   // normal | hard (campaign)
    public string? StageId { get; init; }
    public int StageNumber { get; init; }
    public string? StageType { get; init; }
    public required string CourseId { get; init; }
    public string? FreeplayMode { get; init; }
    public required string Weather { get; init; }
    /// <summary>Freeplay: requested live AI. Campaign: AI fills to six (computed at allocation).</summary>
    public int AiCount { get; init; }
    public required int CarCapPi { get; init; }
    public required string Collision { get; init; }
    public long? BenchmarkTargetMs { get; init; }
    public bool BenchmarkProvisional { get; init; }
    public string? BenchmarkSource { get; init; }
}

public sealed record PlannedEntrant(string AccountId, string DisplayName, LoadoutInfo Loadout, long LoadoutRevision);

/// <summary>Everything frozen when the leader presses Start (spec §4.3): roster, settings, loadouts, grid.</summary>
public sealed class MatchPlan
{
    public required string PlanId { get; init; }
    public required string ConvoyId { get; init; }
    public required long ProposalRevision { get; init; }
    public required long RosterRevision { get; init; }
    public required EventSettings Settings { get; init; }
    public required IReadOnlyList<PlannedEntrant> Entrants { get; init; }
    /// <summary>Live AI entrant IDs (campaign: rival IDs, featured first; Freeplay: ai-1..ai-n).</summary>
    public required IReadOnlyList<string> AiEntrants { get; init; }
    public string? GridNote { get; init; }
    public bool PurePvP { get; init; }
    public required ClientVersion Version { get; init; }
}

/// <summary>A successful allocation, as far as convoy members need to know.</summary>
public sealed record ActiveMatch(string MatchId, string ServerId, string Host, int Port, ClientVersion Version,
    IReadOnlyList<string> Entrants);
