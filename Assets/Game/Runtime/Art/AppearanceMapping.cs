using NightSignal.Core.Customization;
using UnityEngine;

namespace NightSignal.Art
{
    /// <summary>
    /// Core's resolved appearance (every id already valid for the chassis, stock where anything fell back) → the renderer's
    /// <see cref="CarAppearance"/>. Colours arrive as canonical #RRGGBB; nothing here is a simulation input. A signature
    /// swatch's flip tint (resolved only when the swatch is owned) goes to the Car Paint shader.
    /// </summary>
    public static class AppearanceMapping
    {
        public static CarAppearance From(ResolvedAppearance r)
        {
            var stock = new CarAppearance();
            var a = new CarAppearance
            {
                Front = r.Front, Rear = r.Rear, Side = r.Side, RearAero = r.RearAero, Exhaust = r.Exhaust,
                RimStyle = string.IsNullOrEmpty(r.RimStyle) ? null : r.RimStyle,
                RimFraction = (float)r.RimFraction,
                RimColor = Hex(r.RimColor, stock.RimColor),
                RimFinish = string.IsNullOrEmpty(r.RimSheen) ? null : r.RimSheen,
                WheelOffsetM = (float)r.OffsetM,
                Primary = Hex(r.Primary, stock.Primary),
                Secondary = Hex(r.Secondary, stock.Secondary),
                Accent = Hex(r.Accent, stock.Accent),
                Finish = string.IsNullOrEmpty(r.Finish) ? "gloss" : r.Finish,
                FlipTint = string.IsNullOrEmpty(r.FlipTint) ? (Color?)null : Hex(r.FlipTint, Color.white),
                TwoTone = string.IsNullOrEmpty(r.TwoTone) ? "none" : r.TwoTone,
                HeadTint = string.IsNullOrEmpty(r.HeadTint) ? "clear" : r.HeadTint,
                TailTint = string.IsNullOrEmpty(r.TailTint) ? "clear" : r.TailTint,
                GlassTransmission = (float)r.GlassTransmission,
                PlateText = r.PlateText ?? "",
                PlateBackground = Hex(r.PlateBackground, stock.PlateBackground),
                PlateTextColor = Hex(r.PlateTextColor, stock.PlateTextColor),
            };
            foreach (ResolvedDecal d in r.Decals)
                a.Decals.Add(new CarDecal
                {
                    ShapeId = d.ShapeId, Render = d.Render, Glyph = d.Glyph ?? "", Color = Hex(d.Color, Color.white), Zone = d.Zone,
                    U = (float)d.U, V = (float)d.V, Scale = (float)d.Scale, RotationDeg = d.RotationDeg, Opacity = (float)d.Opacity,
                    Mirror = d.Mirror, Flip = d.Flip,
                });
            return a;
        }

        /// <summary>Resolves a stored livery (canonical JSON, "" = stock) for a car and maps it; notices are dropped here.</summary>
        public static CarAppearance ForLivery(CustomizationCatalogue catalogue, string carId, string liveryJson, CosmeticOwnership owned = null)
        {
            LiveryDocument doc = null;
            if (!string.IsNullOrEmpty(liveryJson))
            {
                LiveryParseResult parsed = LiveryJson.Parse(liveryJson);
                if (parsed.Ok) doc = parsed.Document;
            }
            return From(AppearanceResolver.Resolve(catalogue, carId, doc, owned));
        }

        /// <summary>
        /// A race roster's livery (compact wire form, <see cref="LiveryWire"/>) → the renderer's appearance, or null when there is
        /// none or it cannot be shown on this car (the car then shows its palette colour). Resolved without an ownership check:
        /// the Garage that applied it already enforced ownership.
        /// </summary>
        public static CarAppearance ForWire(CustomizationCatalogue catalogue, string carId, string wire)
        {
            if (catalogue == null || string.IsNullOrEmpty(wire) || !catalogue.TryChassis(carId, out ChassisAppearanceDef _)) return null;
            LiveryWireResult r = LiveryWire.Decode(wire);
            if (!r.Ok || r.Document.Car != carId) return null;
            return From(AppearanceResolver.Resolve(catalogue, carId, r.Document));
        }

        /// <summary>Canonical livery JSON (as a Garage stores it) → the compact wire form for a roster; "" for stock or unreadable.</summary>
        public static string WireOf(string liveryJson)
        {
            if (string.IsNullOrEmpty(liveryJson)) return "";
            LiveryParseResult p = LiveryJson.Parse(liveryJson);
            return p.Ok && LiveryWire.EncodeProblem(p.Document) == null ? LiveryWire.Encode(p.Document) : "";
        }

        static Color Hex(string hex, Color fallback) =>
            !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out Color c) ? c : fallback;
    }
}
