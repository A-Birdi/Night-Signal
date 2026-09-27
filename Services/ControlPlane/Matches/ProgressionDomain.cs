using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Toys;

namespace NightSignal.ControlPlane.Matches;

/// <summary>
/// The wall between the toys' explicit non-progression domain and the economy (Addendum 02 D209, §1.6, §11, C09): no result,
/// receipt, purchase or grant whose kind is a toy kind (Core <see cref="NonProgression.IsToyDomain"/>) may reach the wallet,
/// RP, first clears, records, challenges, course or soundtrack unlocks. Checked in settlement AND again in the ledger, so a
/// spoofed result-kind string cannot slip through either layer.
/// </summary>
public static class ProgressionDomain
{
    public const string ErrorCode = "non_progression";

    /// <summary>Null when every kind is an official race kind; otherwise the refusal message.</summary>
    public static string? ToyViolation(params string?[] kinds)
    {
        foreach (string? kind in kinds)
            if (NonProgression.IsToyDomain(kind))
                return $"'{kind}' is a toy (non-progression) kind: diversions never grant Credits, RP, records or unlocks.";
        return null;
    }

    /// <summary>Every kind string a frozen match configuration carries.</summary>
    public static string? ToyViolation(MatchAssignment config) =>
        ToyViolation(config.Kind, config.Mode, config.FreeplayMode, config.StageType, config.Trial?.Kind, config.Benchmark?.Kind);

    /// <summary>Every kind string a settlement carries into the ledger.</summary>
    public static string? ToyViolation(MatchSettlement settlement)
    {
        foreach (EntrantSettlement e in settlement.Entrants)
            if (ToyViolation(e.Receipt.EventKind, e.Receipt.Mode, e.Receipt.TeamTrial?.Kind, e.Facts.Kind.ToString()) is { } v)
                return v;
        return null;
    }
}
