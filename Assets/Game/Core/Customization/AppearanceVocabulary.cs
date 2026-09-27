using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NightSignal.Core.Customization
{
    /// <summary>
    /// The fixed appearance vocabulary shared with the Unity renderer (Assets/Game/Runtime/Art/CarAppearance.cs). Every id in
    /// customization.json and in a livery document comes from these lists. Appearance NEVER changes a simulation input:
    /// mechanical parts live in Core/Builds and are separate.
    /// </summary>
    public static class AppearanceVocabulary
    {
        public const string Stock = "stock";

        public const string Front = "front";
        public const string Rear = "rear";
        public const string Side = "side";
        public const string RearAero = "rearAero";
        public const string Exhaust = "exhaust";

        /// <summary>Every body family, in canonical order.</summary>
        public static readonly string[] Families = { Front, Rear, Side, RearAero, Exhaust };

        /// <summary>Families with the Addendum 01 §13 coverage floor: stock plus at least two distinct non-stock choices.</summary>
        public static readonly string[] FloorFamilies = { Front, Rear, Side, RearAero };

        public const int MinNonStockPerFloorFamily = 2;
        public const int MinNonStockExhaust = 1;

        static readonly Dictionary<string, string[]> variants = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { Front, new[] { Stock, "lip", "aero", "track" } },
            { Rear, new[] { Stock, "diffuser", "valance" } },
            { Side, new[] { Stock, "skirt", "sculpted" } },
            // "lip-spoiler" is the documented equivalent for bodies where a deck ducktail or pedestal wing is inappropriate
            // (hatches, wagon, roadster); a chassis offering it must explain why in its notes.
            { RearAero, new[] { Stock, "ducktail", "wing", "gt-wing", "lip-spoiler" } },
            { Exhaust, new[] { Stock, "dual", "quad", "center" } },
        };

        static readonly Dictionary<string, string[]> equivalents = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { RearAero, new[] { "lip-spoiler" } },
        };

        public static readonly string[] PaintFinishes = { "gloss", "metallic", "pearl", "matte", "satin" };
        public static readonly string[] TwoToneStyles = { "none", "lower", "roof", "hood-stripe", "side-stripe" };
        public static readonly string[] PaintZones = { "body", "lower", "roof", "hood", "sides", "trim" };
        public static readonly string[] DecalZones = { "hood", "roof", "left", "right", "rear", "front" };
        public static readonly string[] RimStyles = { "5-spoke", "6-spoke", "mesh", "split", "dish", "turbofan", "multi-spoke", "3-spoke" };

        /// <summary>Procedural decal render kinds the renderer draws. digit and text carry a glyph.</summary>
        public static readonly string[] DecalRenders =
            { "stripe", "twin-stripe", "pinstripe", "circle", "ring", "arrow", "chevron", "star", "bars", "arcs", "flame", "hex", "digit", "text" };

        public const string RenderDigit = "digit";
        public const string RenderText = "text";

        /// <summary>Attachment anchors every chassis defines (normalised body bounding-box coordinates).</summary>
        public static readonly string[] AnchorIds = { "rearAero", "exhaust", "plateFront", "plateRear" };

        /// <summary>Keys allowed in a chassis' notes: the families plus paint, wheels and decals.</summary>
        public static readonly string[] NoteKeys = Families.Concat(new[] { "paint", "wheels", "decals" }).ToArray();

        public static bool IsFamily(string family) => family != null && variants.ContainsKey(family);

        public static IReadOnlyList<string> Variants(string family) =>
            family != null && variants.TryGetValue(family, out string[] v) ? v : Array.Empty<string>();

        public static bool IsVariant(string family, string variant) => Variants(family).Contains(variant ?? "");

        /// <summary>True for a documented-equivalent variant (the chassis must carry a note for that family).</summary>
        public static bool IsEquivalent(string family, string variant) =>
            family != null && equivalents.TryGetValue(family, out string[] e) && e.Contains(variant ?? "");
    }

    /// <summary>Hard bounds of a livery document (spec §9: 64 decal layers, ≥ 50 undo steps, length-limited plate text).</summary>
    public static class LiveryLimits
    {
        public const int MaxDecalLayers = 64;
        /// <summary>Spec minimum of meaningful undo steps; <see cref="LiveryEditor.MaxUndoSteps"/> keeps more.</summary>
        public const int MinUndoSteps = 50;
        public const int MaxPlateLength = 8;
        public const int MaxTextGlyphLength = 12;
        /// <summary>Longest id any catalogue entry may use (bounds the wire form).</summary>
        public const int MaxIdLength = 24;
        /// <summary>Largest stored livery JSON accepted by <see cref="LiveryJson.Parse"/> (a full 64-layer document is ~10 KB).</summary>
        public const int MaxPayloadChars = 32768;

        // Decal placement is quantised so the canonical form and the hash are identical on every runtime.
        /// <summary>u and v in thousandths of the zone (0..1000 = 0..1).</summary>
        public const int UvScale = 1000;
        /// <summary>Decal size on the body in hundredths of a metre: 0.05..1.60 m (the renderer's clamp), default 0.30 m.</summary>
        public const int MinScaleCenti = 5, MaxScaleCenti = 160, DefaultScaleCenti = 30;
        /// <summary>Whole degrees, 0..359.</summary>
        public const int MaxRotationDeg = 359;
        /// <summary>Opacity in percent: 10..100 (a layer can never be fully transparent clutter).</summary>
        public const int MinOpacityPercent = 10, MaxOpacityPercent = 100;
    }

    /// <summary>
    /// Wheel fitment rules. A rim size change keeps the tyre's outer radius (the sidewall absorbs it), so gearing, ride height
    /// and grip never change; offsets stay inside the measured arch clearance, so a tyre never clips the body or leaves the
    /// collision bounds. The physics track and wheel radius always come from cars.tuning.json.
    /// </summary>
    public static class WheelFitment
    {
        public const double MetersPerInch = 0.0254;
        public const int MinRimInches = 13, MaxRimInches = 22;
        /// <summary>Rim radius / tyre radius bounds (the renderer clamps to the same 0.5..0.78).</summary>
        public const double MinRimFraction = 0.5, MaxRimFraction = 0.78;
        /// <summary>Smaller than two inches under stock would not clear the stock brakes.</summary>
        public const int MaxShrinkBelowStockIn = 2;
        public const int OffsetStepMm = 5;
        /// <summary>Clearance kept between the tyre's outer face and the arch lip at full outward offset.</summary>
        public const int ArchMarginMm = 5;
        public const int MaxInwardSteps = 3;

        /// <summary>Rim radius as a fraction of the tyre radius for a whole-inch rim.</summary>
        public static double RimFraction(int diameterIn, double tyreRadiusM) => diameterIn * MetersPerInch / 2.0 / tyreRadiusM;

        /// <summary>Wheel-face offset in metres (+ outward).</summary>
        public static double OffsetM(int offsetStep) => offsetStep * OffsetStepMm / 1000.0;
    }

    /// <summary>Strict "#RRGGBB" colours (canonical form is upper case).</summary>
    public static class HexColor
    {
        /// <summary>Accepts #RRGGBB in either case; <paramref name="canonical"/> is upper case.</summary>
        public static bool TryNormalize(string value, out string canonical)
        {
            canonical = null;
            if (value == null || value.Length != 7 || value[0] != '#') return false;
            for (int i = 1; i < 7; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            canonical = value.ToUpperInvariant();
            return true;
        }

        /// <summary>True only for the canonical upper-case form.</summary>
        public static bool IsCanonical(string value) => TryNormalize(value, out string c) && c == value;

        /// <summary>WCAG relative luminance of a valid colour.</summary>
        public static double Luminance(string value)
        {
            if (!TryNormalize(value, out string c)) throw new ArgumentException("Not a #RRGGBB colour", nameof(value));
            double Channel(int offset)
            {
                double s = int.Parse(c.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
        }

        /// <summary>WCAG contrast ratio (1..21).</summary>
        public static double Contrast(string a, string b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
    }

    /// <summary>Plate and authored decal text: A–Z, 0–9, space and '-', no leading or trailing space.</summary>
    public static class PlateText
    {
        public static bool IsAllowedChar(char c) => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ' || c == '-';

        /// <summary>Valid when at most <paramref name="maxLength"/> allowed characters without leading/trailing spaces.</summary>
        public static bool IsValid(string text, int maxLength, bool allowEmpty)
        {
            if (text == null) return false;
            if (text.Length == 0) return allowEmpty;
            if (text.Length > maxLength || text[0] == ' ' || text[text.Length - 1] == ' ') return false;
            foreach (char c in text) if (!IsAllowedChar(c)) return false;
            return true;
        }

        /// <summary>Editor input normalisation: trim and upper-case (invariant). Does not remove disallowed characters.</summary>
        public static string Normalize(string text) => (text ?? "").Trim().ToUpperInvariant();
    }

    /// <summary>Stable id rule for catalogue entries: 1–24 characters of A–Z, a–z, 0–9 and '-'.</summary>
    public static class CatalogueIds
    {
        public static bool IsValid(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > LiveryLimits.MaxIdLength) return false;
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok) return false;
            }
            return true;
        }
    }
}
