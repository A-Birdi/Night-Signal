using System;

namespace NightSignal.Core.Customization
{
    /// <summary>
    /// Workshop challenges judged from an applied livery (Appendix E). CH48 Sign Your Car: a saved, legal livery with at
    /// least one decal (owned or free — the Garage refuses any other when it applies a livery) and two paint regions, seen
    /// on the car parked at the meet. "Two paint regions" is a two-tone scheme (lower, roof, hood or side stripe) whose
    /// second colour differs from the body colour, so the body really shows two painted areas.
    /// </summary>
    public static class LiveryChallenges
    {
        public const string SignYourCar = "CH48";

        /// <summary>At least one decal and a visible two-tone.</summary>
        public static bool Signed(LiveryDocument d) =>
            d != null && d.Decals != null && d.Decals.Count > 0 && d.Paint != null &&
            !string.IsNullOrEmpty(d.Paint.TwoTone) && d.Paint.TwoTone != "none" &&
            !string.Equals(d.Paint.Primary ?? "", d.Paint.Secondary ?? "", StringComparison.OrdinalIgnoreCase);

        /// <summary>The same for a car's applied livery in its compact wire form ("" = stock: never signed).</summary>
        public static bool SignedWire(string wire)
        {
            if (string.IsNullOrEmpty(wire)) return false;
            LiveryWireResult r = LiveryWire.Decode(wire);
            return r.Ok && Signed(r.Document);
        }
    }
}
