using System.Text.Json;
using NightSignal.Core.Toys;

namespace NightSignal.ControlPlane.Toys;

/// <summary>
/// Host-side budgets for the 'While We Wait' toys (Addendum 02 §1.6: cap message sizes/rates, queued actions and memory;
/// prioritize race/control traffic over toy snapshots). The toys' own structural limits live in Core
/// <see cref="ToyLimits"/> (8 KiB commands, 40/s + burst 80 per member, 3 MiB snapshots).
/// </summary>
public static class ToyHostLimits
{
    /// <summary>Toy pushes are coalesced per convoy and produced at most this often (≈10 Hz).</summary>
    public static readonly TimeSpan BroadcastInterval = TimeSpan.FromMilliseconds(100);
    /// <summary>Largest single <c>toy.activity</c> push; a bigger state is announced as omitted and fetched with <c>toy.snapshot</c>.</summary>
    public const int MaxActivityPushBytes = 32 * 1024;
    /// <summary>Budget for all <c>toy.activity</c> pushes of one convoy in one broadcast tick.</summary>
    public const int MaxPushBytesPerTick = 96 * 1024;
    /// <summary>Rooms with no new command are still re-checked this often for time-based changes (lease/proposal expiry).</summary>
    public const long RecheckMs = 1_000;
    /// <summary>
    /// Envelope flood guard per account, applied BEFORE the command is parsed. Deliberately looser than the session's own
    /// per-member token bucket, which remains the normal <c>RateLimited</c> answer.
    /// </summary>
    public static readonly (int Limit, TimeSpan Window) CommandFlood = (120, TimeSpan.FromSeconds(1));
    /// <summary>Full or per-toy snapshots are larger replies: at most this many per account.</summary>
    public static readonly (int Limit, TimeSpan Window) SnapshotRequests = (10, TimeSpan.FromSeconds(10));
    /// <summary>After a failed durable write the last verified snapshot stays authoritative; retry after this long.</summary>
    public const long PersistRetryMs = 5_000;
    /// <summary>Shown while a committed convoy event holds the toys (Addendum 02 §1.3).</summary>
    public const string PausedNotice = "Paused for the convoy — progress kept";
}

/// <summary>Pre-serialized JSON embedded verbatim in a System.Text.Json message (toy states are serialized by Core's Newtonsoft codecs).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(RawJsonConverter))]
public sealed record RawJson(string Json);

public sealed class RawJsonConverter : System.Text.Json.Serialization.JsonConverter<RawJson>
{
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return new RawJson(doc.RootElement.GetRawText());
    }

    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.Json, skipInputValidation: true); // produced by our own serializer, never by a client
}

/// <summary>Low-priority, coalescing delivery (latest value per key wins; race/control messages always go first).</summary>
public interface IToyNotifier
{
    void SendLowPriority(string accountId, string key, string type, long revision, object payload);
}

/// <summary>One push collected under the directory lock and delivered after it is released.</summary>
public sealed record ToyOutgoing(string AccountId, string Key, string Type, object Payload);

/// <summary>A durable-write request for one convoy session's toys, processed off the lock by <see cref="ToySnapshotPersistence"/>.</summary>
public sealed record ToyPersistRequest(string SessionId, bool Delete);

/// <summary>
/// A stored toy snapshot (table <c>toy_snapshots</c>): the Core <see cref="DowntimeSnapshot"/> JSON at <paramref name="Revision"/>,
/// and — while the convoy is Dormant (D208) — the fixed server-clock expiry after which it is retired.
/// </summary>
public sealed record ToySnapshotRecord(string SessionId, long Revision, string Json, DateTimeOffset? DormantExpiresAt);

/// <summary>Wire shapes shared by replies and pushes. Enum values use Core's names (as Core's own JSON does).</summary>
public static class ToyWire
{
    public static object Result(ToyResult r) => new
    {
        verdict = r.Verdict.ToString(),
        reason = r.Reason == ToyReason.None ? null : r.Reason.ToString(),
        detail = r.Detail,
        revision = r.Revision,
        value = r.Value,
        duplicate = r.Duplicate,
    };

    public static string Message(ToyResult r) =>
        r.Reason.ToString() + (r.Detail is null ? "" : ": " + r.Detail);

    /// <summary>Strict activity name: a Core <see cref="ToyActivityId"/> name (case-insensitive), never a number.</summary>
    public static bool TryActivity(string? name, out ToyActivityId id)
    {
        id = default;
        return !string.IsNullOrEmpty(name) && !char.IsAsciiDigit(name[0]) && name[0] != '-' &&
               Enum.TryParse(name, ignoreCase: true, out id) && Enum.IsDefined(id);
    }
}
