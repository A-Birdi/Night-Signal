using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NightSignal.Core.Customization
{
    /// <summary>
    /// The set of cosmetic ids a player owns. Local: <c>LocalProfile.Cosmetics.Select(c =&gt; c.CosmeticId)</c>; Online:
    /// <c>/v1/me cosmeticsOwned</c> (validated again by the server on apply). Only decal shapes and paint swatches with a
    /// cosmetic id are locked; body variants, rims, finishes, lamps, glass and plates are free.
    /// </summary>
    public sealed class CosmeticOwnership
    {
        readonly HashSet<string> ids;

        CosmeticOwnership(IEnumerable<string> ids) =>
            this.ids = new HashSet<string>((ids ?? Enumerable.Empty<string>()).Where(i => !string.IsNullOrEmpty(i)), StringComparer.Ordinal);

        public static readonly CosmeticOwnership None = new CosmeticOwnership(null);

        public static CosmeticOwnership FromIds(IEnumerable<string> cosmeticIds) => new CosmeticOwnership(cosmeticIds);

        public IReadOnlyCollection<string> Ids => ids;

        /// <summary>True for a free item (null/empty cosmetic id) or an owned cosmetic.</summary>
        public bool Unlocks(string cosmeticId) => string.IsNullOrEmpty(cosmeticId) || ids.Contains(cosmeticId);
    }

    public enum LiveryValidationMode
    {
        /// <summary>Trying things on in the Garage: locked items are listed but do not make the draft invalid.</summary>
        Preview = 0,
        /// <summary>Apply / server acceptance: every locked item is also an error.</summary>
        Apply = 1,
    }

    /// <summary>An item the livery uses that the player has not unlocked.</summary>
    public sealed class LockedItem
    {
        /// <summary>Document path, e.g. "paint.swatch" or "decals[3].shape".</summary>
        public string Path = "";
        public string ItemId = "";
        public string Name = "";
        public string CosmeticId = "";
    }

    public sealed class LiveryValidation
    {
        public LiveryValidationMode Mode;
        /// <summary>Exact problems in document order. In Apply mode this includes one entry per locked item.</summary>
        public List<string> Errors = new List<string>();
        /// <summary>Locked items in document order (reported in both modes).</summary>
        public List<LockedItem> Locked = new List<LockedItem>();
        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>
    /// Semantic validation of a livery against the catalogue and a chassis: ids, fitment, colours, plate text, the 64-layer
    /// cap, decal placement bounds and ownership. Deterministic; the error texts are part of the contract (tests pin them).
    /// </summary>
    public static class LiveryValidator
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static LiveryValidation Validate(LiveryDocument doc, CustomizationCatalogue catalogue, string carId,
            CosmeticOwnership owned, LiveryValidationMode mode)
        {
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            owned = owned ?? CosmeticOwnership.None;
            var v = new LiveryValidation { Mode = mode };
            List<string> e = v.Errors;
            if (doc == null) { e.Add("livery: no document"); return v; }
            if (!catalogue.TryChassis(carId, out ChassisAppearanceDef chassis)) { e.Add($"car: no customization chassis for {carId}"); return v; }
            if (doc.Car != carId) { e.Add($"car: livery is for {Show(doc.Car)}, not {carId}"); return v; }

            BodySelection body = doc.Body ?? new BodySelection();
            foreach (string family in AppearanceVocabulary.Families)
            {
                string variant = body.Get(family);
                if (!chassis.Offers(family, variant)) e.Add($"body.{family}: '{variant}' is not offered on {carId}");
            }

            WheelSelection w = doc.Wheels ?? new WheelSelection();
            ChassisWheels fit = chassis.Wheels;
            if (!catalogue.TryRim(w.Rim, out RimDesignDef rim)) e.Add($"wheels.rim: unknown rim design '{w.Rim}'");
            if (!fit.DiameterIn.Contains(w.DiameterIn)) e.Add($"wheels.diameterIn: {w.DiameterIn} in is outside the {carId} fitment {fit.DiameterIn} in");
            else if (rim != null && !rim.DiameterIn.Contains(w.DiameterIn)) e.Add($"wheels.diameterIn: {rim.Id} is made in {rim.DiameterIn} in, not {w.DiameterIn} in");
            if (!fit.OffsetStep.Contains(w.OffsetStep))
                e.Add($"wheels.offsetStep: {w.OffsetStep} is outside the {carId} fitment {fit.OffsetStep.Min} to {fit.OffsetStep.Max}");
            if (!catalogue.TryRimFinish(w.Finish, out _)) e.Add($"wheels.finish: unknown rim finish '{w.Finish}'");

            PaintSelection p = doc.Paint ?? new PaintSelection();
            Color(e, "paint.primary", p.Primary);
            Color(e, "paint.secondary", p.Secondary);
            Color(e, "paint.accent", p.Accent);
            if (!catalogue.IsFinish(p.Finish)) e.Add($"paint.finish: unknown finish '{p.Finish}'");
            if (!chassis.Paint.TwoTone.Contains(p.TwoTone ?? "")) e.Add($"paint.twoTone: '{p.TwoTone}' is not offered on {carId}");
            if (!string.IsNullOrEmpty(p.Swatch))
            {
                if (!catalogue.TrySwatch(p.Swatch, out PaintSwatchDef sw)) e.Add($"paint.swatch: unknown swatch '{p.Swatch}'");
                else
                {
                    if (!string.Equals(p.Primary, sw.Color, StringComparison.OrdinalIgnoreCase) || p.Finish != sw.Finish)
                        e.Add($"paint.swatch: {sw.Id} is {sw.Color} {sw.Finish}; primary and finish must match it");
                    Lock(v, owned, mode, "paint.swatch", sw.Id, sw.Name, sw.CosmeticId);
                }
            }

            LampSelection l = doc.Lamps ?? new LampSelection();
            if (!catalogue.TryLamp(l.Head, out LampPresetDef head) || !head.Head) e.Add($"lamps.head: '{l.Head}' is not a head-lamp preset");
            if (!catalogue.TryLamp(l.Tail, out LampPresetDef tail) || !tail.Tail) e.Add($"lamps.tail: '{l.Tail}' is not a tail-lamp preset");
            if (!catalogue.TryGlass(doc.Glass, out _)) e.Add($"glass: '{doc.Glass}' is not a window tint");

            PlateSelection plate = doc.Plate ?? new PlateSelection();
            if (!PlateText.IsValid(plate.Text, LiveryLimits.MaxPlateLength, allowEmpty: true))
                e.Add($"plate.text: '{plate.Text}' must be at most {LiveryLimits.MaxPlateLength} characters of A–Z, 0–9, space and '-' without leading or trailing spaces");
            if (!catalogue.TryPlateStyle(plate.Style, out _)) e.Add($"plate.style: unknown plate style '{plate.Style}'");

            List<DecalLayer> decals = doc.Decals ?? new List<DecalLayer>();
            if (decals.Count > LiveryLimits.MaxDecalLayers) e.Add($"decals: {decals.Count} layers exceed the {LiveryLimits.MaxDecalLayers}-layer limit");
            for (int i = 0; i < decals.Count; i++) ValidateDecal(v, catalogue, chassis, owned, mode, i, decals[i]);
            return v;
        }

        static void ValidateDecal(LiveryValidation v, CustomizationCatalogue catalogue, ChassisAppearanceDef chassis, CosmeticOwnership owned,
            LiveryValidationMode mode, int i, DecalLayer d)
        {
            List<string> e = v.Errors;
            string at = "decals[" + i.ToString(Inv) + "]";
            if (d == null) { e.Add($"{at}: empty layer"); return; }
            bool known = catalogue.TryShape(d.Shape, out DecalShapeDef shape);
            if (!known) e.Add($"{at}.shape: unknown decal shape '{d.Shape}'");
            Color(e, at + ".color", d.Color);
            if (!chassis.Decals.Zones.Contains(d.Zone ?? "")) e.Add($"{at}.zone: '{d.Zone}' is not a decal zone on {chassis.Car}");
            if (d.UMilli < 0 || d.UMilli > LiveryLimits.UvScale) e.Add($"{at}.u: {Milli(d.UMilli)} is outside 0–1");
            if (d.VMilli < 0 || d.VMilli > LiveryLimits.UvScale) e.Add($"{at}.v: {Milli(d.VMilli)} is outside 0–1");
            if (d.ScaleCenti < LiveryLimits.MinScaleCenti || d.ScaleCenti > LiveryLimits.MaxScaleCenti)
                e.Add($"{at}.scale: {LiveryJson.Fixed(d.ScaleCenti, 100, 2)} is outside {LiveryJson.Fixed(LiveryLimits.MinScaleCenti, 100, 2)}–{LiveryJson.Fixed(LiveryLimits.MaxScaleCenti, 100, 2)}");
            if (d.RotationDeg < 0 || d.RotationDeg > LiveryLimits.MaxRotationDeg) e.Add($"{at}.rotationDeg: {d.RotationDeg} is outside 0–{LiveryLimits.MaxRotationDeg}");
            if (d.OpacityPercent < LiveryLimits.MinOpacityPercent || d.OpacityPercent > LiveryLimits.MaxOpacityPercent)
                e.Add($"{at}.opacity: {LiveryJson.Fixed(d.OpacityPercent, 100, 2)} is outside {LiveryJson.Fixed(LiveryLimits.MinOpacityPercent, 100, 2)}–1");
            if (known) Lock(v, owned, mode, at + ".shape", shape.Id, shape.Name, shape.CosmeticId);
        }

        static void Lock(LiveryValidation v, CosmeticOwnership owned, LiveryValidationMode mode, string path, string itemId, string name, string cosmeticId)
        {
            if (owned.Unlocks(cosmeticId)) return;
            v.Locked.Add(new LockedItem { Path = path, ItemId = itemId, Name = name, CosmeticId = cosmeticId });
            if (mode == LiveryValidationMode.Apply) v.Errors.Add($"{path}: {name} is locked (unlock cosmetic {cosmeticId})");
        }

        static void Color(List<string> e, string path, string value)
        {
            if (!HexColor.TryNormalize(value, out _)) e.Add($"{path}: '{value}' is not a #RRGGBB color");
        }

        static string Milli(int milli) => LiveryJson.Fixed(milli, LiveryLimits.UvScale, 3);

        static string Show(string s) => string.IsNullOrEmpty(s) ? "no car" : s;
    }
}
