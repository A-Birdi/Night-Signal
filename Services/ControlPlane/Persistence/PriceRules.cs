using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Persistence;

/// <summary>Price sanity (spec §9/§10): whole positive credits within the category cap. NaN/∞/negative/fractional fail.</summary>
public static class PriceRules
{
    public const string Car = "car";
    public const string Part = "part";
    public const string Cosmetic = "cosmetic";

    public static long? CapFor(string itemKind) => itemKind switch
    {
        Car => Limits.MaxCarPrice,
        Part => Limits.MaxPerformancePartPrice,
        Cosmetic => Limits.MaxCosmeticPrice,
        _ => null,
    };

    public static bool IsValid(string itemKind, long price) => CapFor(itemKind) is { } cap && price > 0 && price <= cap;

    /// <summary>For numbers arriving as floating point (e.g. JSON): must be finite and integral before the range check.</summary>
    public static bool IsValid(string itemKind, double price) =>
        double.IsFinite(price) && Math.Floor(price) == price && price is > 0 and <= long.MaxValue / 2 && IsValid(itemKind, (long)price);
}
