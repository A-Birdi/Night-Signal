using System;
using System.Collections.Generic;
using NightSignal.Core.Builds;

namespace NightSignal.Core.Customization
{
    /// <summary>
    /// Workshop challenges judged from an applied livery (Appendix E). CH48 Sign Your Car: a saved, legal livery with at
    /// least one decal (owned or free — the Garage refuses any other when it applies a livery) and two paint regions, seen
    /// on the car parked at the meet. "Two paint regions" is a two-tone scheme (lower, roof, hood or side stripe) whose
    /// second colour differs from the body colour, so the body really shows two painted areas. CH50 Change Without Losing:
    /// two visual presets saved, switched between, and the first restored exactly — judged when an apply switches from one
    /// saved preset to a different-looking preset saved before it and the applied livery is exactly that preset's saved
    /// look; the server confirms it with the car's new saved revision, and the meet shows the applied livery (V-089).
    /// </summary>
    public static class LiveryChallenges
    {
        public const string SignYourCar = "CH48", ChangeWithoutLosing = "CH50";

        /// <summary>
        /// CH50: this apply took the car from preset <paramref name="previousPresetId"/> (look <paramref name="previousHash"/>) to
        /// preset <paramref name="appliedPresetId"/>, saved before it, with a different look, and the applied livery is
        /// exactly that preset's saved payload (canonical JSON). <paramref name="presets"/> are the car's presets in save order.
        /// </summary>
        public static bool RestoresFirstPreset(IList<VisualPreset> presets, string previousPresetId, string previousHash, string appliedPresetId,
            string appliedLivery, string appliedHash)
        {
            if (presets == null || string.IsNullOrEmpty(previousPresetId) || string.IsNullOrEmpty(appliedPresetId) || previousPresetId == appliedPresetId) return false;
            int first = -1, second = -1;
            for (int i = 0; i < presets.Count; i++)
            {
                if (presets[i]?.PresetId == appliedPresetId) first = i;
                if (presets[i]?.PresetId == previousPresetId) second = i;
            }
            return first >= 0 && second > first && !string.IsNullOrEmpty(appliedLivery) && presets[first].PayloadJson == appliedLivery &&
                   (appliedHash ?? "") != (previousHash ?? "");
        }

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
