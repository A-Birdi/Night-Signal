using System.Text;
using System.Text.Json;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Toys;

namespace NightSignal.ControlPlane.Toys;

/// <summary>
/// Control-channel entry points for the toys (<c>toy.command</c>, <c>toy.snapshot</c>) and the broadcast pump. Cheap,
/// stateless checks happen first (flood limit, size, envelope parsing, identity); only then is the directory lock taken
/// for the session work in <see cref="ConvoyToys"/>. Nothing here touches readiness, proposals or the economy.
/// </summary>
public sealed class ToyService(ConvoyDirectory directory, ConvoyToys toys, IToyNotifier notifier, RateLimiter limiter)
{
    /// <summary>
    /// <c>toy.command</c>: <paramref name="payload"/> is the Core <see cref="ToyCommand"/> envelope
    /// <c>{session, activity, epoch, member, gen, seq, req, kind, payload}</c>. Order: per-account flood limit, size (before
    /// any parsing), envelope parse/validation, member = the signed-in account, then Core's session checks. Every answer
    /// carries the <see cref="ToyResult"/>; a rejection or deferral is <c>ok:false</c> with code <c>toy_rejected</c> /
    /// <c>toy_deferred</c> (or <c>rate_limited</c> from the flood guard, <c>toys_unavailable</c>).
    /// </summary>
    public ConvoyResult Command(string accountId, JsonElement payload)
    {
        if (!toys.Available) return ConvoyResult.Fail("toys_unavailable", toys.Unavailable ?? "The toys are unavailable on this server.");
        if (!limiter.TryAcquire("toy/" + accountId, ToyHostLimits.CommandFlood, out long retry))
            return Answer(ToyResult.Reject(ToyReason.RateLimited, "too many toy commands"), "rate_limited", retry);
        if (payload.ValueKind != JsonValueKind.Object)
            return Answer(ToyResult.Reject(ToyReason.Malformed, "payload must be the toy command envelope"));
        string raw = payload.GetRawText();
        if (raw.Length > ToyLimits.MaxCommandBytes || Encoding.UTF8.GetByteCount(raw) > ToyLimits.MaxCommandBytes)
            return Answer(ToyResult.Reject(ToyReason.TooLarge, $"a toy command is at most {ToyLimits.MaxCommandBytes} bytes"));
        if (!ToyCommandCodec.TryParse(raw, out ToyCommand cmd, out ToyReason reason))
            return Answer(ToyResult.Reject(reason, "not a valid toy command envelope"));
        if (cmd.MemberId != accountId)
            return Answer(ToyResult.Reject(ToyReason.NotMember, "member must be the signed-in account"));
        return Answer(directory.Exclusive(() => toys.Submit(accountId, cmd)));
    }

    /// <summary>
    /// <c>toy.snapshot</c>: <c>{activity?}</c> — every toy of the requester's convoy (Core DowntimeSnapshot JSON minus
    /// server-only fields) or one toy (<c>CapClash</c>, <c>PitCrew</c>, <c>Greenlight</c>, <c>PocketCircuit</c>, <c>Canvas</c>).
    /// </summary>
    public ConvoyResult Snapshot(string accountId, JsonElement payload)
    {
        if (!toys.Available) return ConvoyResult.Fail("toys_unavailable", toys.Unavailable ?? "The toys are unavailable on this server.");
        ToyActivityId? activity = null;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("activity", out JsonElement a) && a.ValueKind != JsonValueKind.Null)
        {
            if (a.ValueKind != JsonValueKind.String || !ToyWire.TryActivity(a.GetString(), out ToyActivityId id))
                return ConvoyResult.Fail("invalid_request", "activity must be CapClash, PitCrew, Greenlight, PocketCircuit or Canvas.");
            activity = id;
        }
        if (!limiter.TryAcquire("toy-snapshot/" + accountId, ToyHostLimits.SnapshotRequests, out long retry))
            return ConvoyResult.Fail("rate_limited", "Too many toy snapshot requests.", retry);
        (ToyResult result, object? value) = directory.Exclusive(() => toys.ClientSnapshot(accountId, activity));
        return result.Accepted ? ConvoyResult.Success(value) : Answer(result);
    }

    /// <summary>One broadcast tick (≈10 Hz): collected under the lock, delivered after it on the low-priority lane.</summary>
    public int Pump()
    {
        if (!toys.Available) return 0;
        List<ToyOutgoing> outgoing = directory.Exclusive(toys.CollectBroadcasts);
        foreach (ToyOutgoing m in outgoing) notifier.SendLowPriority(m.AccountId, m.Key, m.Type, 0, m.Payload);
        return outgoing.Count;
    }

    static ConvoyResult Answer(ToyResult r, string? code = null, long? retryAfterMs = null) =>
        r.Accepted
            ? ConvoyResult.Success(ToyWire.Result(r))
            : new ConvoyResult(new ConvoyError(code ?? (r.Verdict == ToyVerdict.Deferred ? "toy_deferred" : "toy_rejected"), ToyWire.Message(r), retryAfterMs),
                ToyWire.Result(r));
}
