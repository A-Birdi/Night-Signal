using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NightSignal.Core.Customization
{
    /// <summary>One decal layer ready to draw: render kind, glyph and placement already resolved.</summary>
    public sealed class ResolvedDecal
    {
        /// <summary>Position in the effective stack (0 = bottom).</summary>
        public int Layer;
        public string ShapeId = "";
        /// <summary>Procedural render kind (<see cref="AppearanceVocabulary.DecalRenders"/>).</summary>
        public string Render = "";
        /// <summary>"0".."9" for digit, authored text for text, otherwise null.</summary>
        public string Glyph;
        public string Color = "#FFFFFF";
        public string Zone = "";
        public double U;
        public double V;
        public double Scale;
        public int RotationDeg;
        public double Opacity;
        public bool Mirror;
        public bool Flip;
    }

    /// <summary>
    /// Every visible choice for one car on its chassis, fully resolved: valid ids only, concrete colours and numbers, and the
    /// chassis masks/anchors, so the renderer never guesses. Anything invalid (or locked, when ownership is checked) falls
    /// back to stock with a <see cref="Notices"/> entry. Nothing here is a simulation input.
    /// </summary>
    public sealed class ResolvedAppearance
    {
        public string CarId = "";
        public string BodyStyle = "";

        public string Front = AppearanceVocabulary.Stock;
        public string Rear = AppearanceVocabulary.Stock;
        public string Side = AppearanceVocabulary.Stock;
        public string RearAero = AppearanceVocabulary.Stock;
        public string Exhaust = AppearanceVocabulary.Stock;
        /// <summary>Where the rear aero mounts on this body (trunk-lid, hatch-edge, roof-edge, rear-deck, engine-cover).</summary>
        public string RearAeroMount = "";

        public string RimId = "";
        public string RimName = "";
        /// <summary>Rim render style (<see cref="AppearanceVocabulary.RimStyles"/>).</summary>
        public string RimStyle = "";
        public int RimDiameterIn;
        /// <summary>Rim radius / tyre radius (the renderer's rim fraction). Exactly the chassis' stock value at the stock size.</summary>
        public double RimFraction;
        /// <summary>Tyre outer radius: always the chassis value (fitment never changes it).</summary>
        public double TyreRadiusM;
        public double TyreWidthM;
        public int OffsetStep;
        /// <summary>Visual wheel-face offset in metres (+ outward); the physics track is unchanged.</summary>
        public double OffsetM;
        public string RimFinishId = "";
        public string RimColor = "";
        public string RimSheen = "";

        public string Primary = "";
        public string Secondary = "";
        public string Accent = "";
        public string Finish = "";
        public string TwoTone = "none";
        /// <summary>Paint zone the secondary colour covers ("body" when two-tone is none).</summary>
        public string TwoToneZone = "body";
        public string SwatchId = "";
        public string SwatchName = "";
        /// <summary>Glancing-angle tint of a signature swatch, or null.</summary>
        public string FlipTint;

        public string HeadTint = CustomizationCatalogue.DefaultLamp;
        public double HeadTransmission = 1;
        public string TailTint = CustomizationCatalogue.DefaultLamp;
        public double TailTransmission = 1;
        public string Glass = CustomizationCatalogue.DefaultGlass;
        public double GlassTransmission = 1;

        public string PlateText = "";
        public string PlateStyle = CustomizationCatalogue.DefaultPlateStyle;
        public string PlateBackground = "";
        public string PlateTextColor = "";

        public List<ResolvedDecal> Decals = new List<ResolvedDecal>();
        /// <summary>Decal masks per zone (lamps, plates, intakes): never paint decals inside these.</summary>
        public IReadOnlyDictionary<string, List<KeepOutRect>> DecalKeepOut = new Dictionary<string, List<KeepOutRect>>();
        public IReadOnlyDictionary<string, AnchorDef> Anchors = new Dictionary<string, AnchorDef>();

        /// <summary>The effective livery after fallbacks (what is actually shown) and its hash — a cache/texture key.</summary>
        public LiveryDocument Effective;
        public string EffectiveHash = "";
        /// <summary>Hash of the input document ("" when there was none).</summary>
        public string SourceHash = "";
        /// <summary>Why something shows as stock or was dropped. Empty = shown exactly as chosen.</summary>
        public List<string> Notices = new List<string>();
    }

    public static class AppearanceResolver
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Resolves <paramref name="document"/> for <paramref name="carId"/>. Pass <paramref name="owned"/> to hide locked items
        /// (local preview outside the editor); pass null for a livery the server already accepted (remote cars at a meet or race).
        /// </summary>
        public static ResolvedAppearance Resolve(CustomizationCatalogue catalogue, string carId, LiveryDocument document, CosmeticOwnership owned = null)
        {
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            ChassisAppearanceDef chassis = catalogue.ChassisFor(carId);
            var notices = new List<string>();
            LiveryDocument stock = LiveryDocument.Stock(chassis);
            string sourceHash = document == null ? "" : LiveryHash.Of(document);
            LiveryDocument src;
            if (document == null) { notices.Add("No livery: showing the stock appearance."); src = stock.Clone(); }
            else if (document.Car != carId) { notices.Add($"Livery is for {document.Car}, not {carId}: showing the stock appearance."); src = stock.Clone(); }
            else src = document;

            LiveryDocument eff = Effective(catalogue, chassis, src, stock, owned, notices);
            return Build(catalogue, chassis, eff, sourceHash, notices);
        }

        static LiveryDocument Effective(CustomizationCatalogue cat, ChassisAppearanceDef chassis, LiveryDocument src, LiveryDocument stock,
            CosmeticOwnership owned, List<string> notices)
        {
            string car = chassis.Car;
            var eff = new LiveryDocument { Car = car };

            BodySelection body = src.Body ?? new BodySelection();
            foreach (string family in AppearanceVocabulary.Families)
            {
                string v = body.Get(family);
                if (chassis.Offers(family, v)) eff.Body.Set(family, v);
                else notices.Add($"body.{family}: '{v}' is not offered on {car}; showing stock.");
            }

            WheelSelection w = src.Wheels ?? new WheelSelection();
            ChassisWheels fit = chassis.Wheels;
            if (cat.TryRim(w.Rim, out RimDesignDef rim))
            {
                IReadOnlyList<int> sizes = CustomizationCatalogue.FittingDiameters(rim, chassis);
                eff.Wheels.Rim = rim.Id;
                if (sizes.Contains(w.DiameterIn)) eff.Wheels.DiameterIn = w.DiameterIn;
                else
                {
                    int nearest = sizes.OrderBy(s => Math.Abs(s - fit.StockDiameterIn)).ThenBy(s => s).First();
                    eff.Wheels.DiameterIn = nearest;
                    notices.Add($"wheels.diameterIn: {w.DiameterIn} in does not fit {rim.Id} on {car}; showing {nearest} in.");
                }
            }
            else
            {
                eff.Wheels.Rim = stock.Wheels.Rim;
                eff.Wheels.DiameterIn = stock.Wheels.DiameterIn;
                notices.Add($"wheels.rim: unknown rim design '{w.Rim}'; showing the stock wheel.");
            }
            if (fit.OffsetStep.Contains(w.OffsetStep)) eff.Wheels.OffsetStep = w.OffsetStep;
            else notices.Add($"wheels.offsetStep: {w.OffsetStep} is outside the {car} fitment; showing 0.");
            if (cat.TryRimFinish(w.Finish, out _)) eff.Wheels.Finish = w.Finish;
            else { eff.Wheels.Finish = stock.Wheels.Finish; notices.Add($"wheels.finish: unknown rim finish '{w.Finish}'; showing stock."); }

            PaintSelection p = src.Paint ?? new PaintSelection();
            eff.Paint.Primary = ColorOr(p.Primary, stock.Paint.Primary, "paint.primary", notices);
            eff.Paint.Secondary = ColorOr(p.Secondary, stock.Paint.Secondary, "paint.secondary", notices);
            eff.Paint.Accent = ColorOr(p.Accent, stock.Paint.Accent, "paint.accent", notices);
            if (cat.IsFinish(p.Finish)) eff.Paint.Finish = p.Finish;
            else { eff.Paint.Finish = stock.Paint.Finish; notices.Add($"paint.finish: unknown finish '{p.Finish}'; showing stock."); }
            if (chassis.Paint.TwoTone.Contains(p.TwoTone ?? "")) eff.Paint.TwoTone = p.TwoTone;
            else notices.Add($"paint.twoTone: '{p.TwoTone}' is not offered on {car}; showing a single colour.");
            eff.Paint.Swatch = "";
            if (!string.IsNullOrEmpty(p.Swatch))
            {
                if (!cat.TrySwatch(p.Swatch, out PaintSwatchDef sw)) notices.Add($"paint.swatch: unknown swatch '{p.Swatch}'; showing the colour only.");
                else if (sw.Color != eff.Paint.Primary || sw.Finish != eff.Paint.Finish) notices.Add($"paint.swatch: {sw.Id} does not match the chosen colour; showing the colour only.");
                else if (owned != null && !owned.Unlocks(sw.CosmeticId)) notices.Add($"paint.swatch: {sw.Name} is locked; showing the colour without its flip tint.");
                else eff.Paint.Swatch = sw.Id;
            }

            LampSelection l = src.Lamps ?? new LampSelection();
            if (cat.TryLamp(l.Head, out LampPresetDef head) && head.Head) eff.Lamps.Head = head.Id;
            else notices.Add($"lamps.head: '{l.Head}' is not a head-lamp preset; showing clear.");
            if (cat.TryLamp(l.Tail, out LampPresetDef tail) && tail.Tail) eff.Lamps.Tail = tail.Id;
            else notices.Add($"lamps.tail: '{l.Tail}' is not a tail-lamp preset; showing clear.");
            if (cat.TryGlass(src.Glass, out GlassTintDef glass)) eff.Glass = glass.Id;
            else notices.Add($"glass: '{src.Glass}' is not a window tint; showing clear.");

            PlateSelection plate = src.Plate ?? new PlateSelection();
            if (PlateText.IsValid(plate.Text, LiveryLimits.MaxPlateLength, allowEmpty: true)) eff.Plate.Text = plate.Text;
            else notices.Add("plate.text: invalid plate text; showing a blank plate.");
            if (cat.TryPlateStyle(plate.Style, out _)) eff.Plate.Style = plate.Style;
            else notices.Add($"plate.style: unknown plate style '{plate.Style}'; showing standard.");

            List<DecalLayer> decals = src.Decals ?? new List<DecalLayer>();
            for (int i = 0; i < decals.Count; i++)
            {
                string at = "decals[" + i.ToString(Inv) + "]";
                if (i >= LiveryLimits.MaxDecalLayers)
                {
                    notices.Add($"decals: layers above {LiveryLimits.MaxDecalLayers} are not shown ({decals.Count - LiveryLimits.MaxDecalLayers} dropped).");
                    break;
                }
                string why = DecalProblem(cat, chassis, decals[i], owned);
                if (why != null) { notices.Add($"{at}: {why}; layer not shown."); continue; }
                DecalLayer d = decals[i].Clone();
                HexColor.TryNormalize(d.Color, out string c);
                d.Color = c;
                eff.Decals.Add(d);
            }
            return eff;
        }

        static string DecalProblem(CustomizationCatalogue cat, ChassisAppearanceDef chassis, DecalLayer d, CosmeticOwnership owned)
        {
            if (d == null) return "empty layer";
            if (!cat.TryShape(d.Shape, out DecalShapeDef shape)) return $"unknown decal shape '{d.Shape}'";
            if (owned != null && !owned.Unlocks(shape.CosmeticId)) return $"{shape.Name} is locked";
            if (!HexColor.TryNormalize(d.Color, out _)) return $"'{d.Color}' is not a #RRGGBB color";
            if (!chassis.Decals.Zones.Contains(d.Zone ?? "")) return $"'{d.Zone}' is not a decal zone on {chassis.Car}";
            if (d.UMilli < 0 || d.UMilli > LiveryLimits.UvScale || d.VMilli < 0 || d.VMilli > LiveryLimits.UvScale) return "placement outside the zone";
            if (d.ScaleCenti < LiveryLimits.MinScaleCenti || d.ScaleCenti > LiveryLimits.MaxScaleCenti) return "scale out of range";
            if (d.RotationDeg < 0 || d.RotationDeg > LiveryLimits.MaxRotationDeg) return "rotation out of range";
            if (d.OpacityPercent < LiveryLimits.MinOpacityPercent || d.OpacityPercent > LiveryLimits.MaxOpacityPercent) return "opacity out of range";
            return null;
        }

        static string ColorOr(string value, string fallback, string path, List<string> notices)
        {
            if (HexColor.TryNormalize(value, out string c)) return c;
            notices.Add($"{path}: '{value}' is not a #RRGGBB color; showing stock.");
            return fallback;
        }

        static ResolvedAppearance Build(CustomizationCatalogue cat, ChassisAppearanceDef chassis, LiveryDocument eff, string sourceHash, List<string> notices)
        {
            ChassisWheels fit = chassis.Wheels;
            cat.TryRim(eff.Wheels.Rim, out RimDesignDef rim);
            cat.TryRimFinish(eff.Wheels.Finish, out RimFinishDef rimFinish);
            cat.TryTwoTone(eff.Paint.TwoTone, out TwoToneDef twoTone);
            cat.TryLamp(eff.Lamps.Head, out LampPresetDef head);
            cat.TryLamp(eff.Lamps.Tail, out LampPresetDef tail);
            cat.TryGlass(eff.Glass, out GlassTintDef glass);
            cat.TryPlateStyle(eff.Plate.Style, out PlateStyleDef plate);
            PaintSwatchDef swatch = null;
            if (!string.IsNullOrEmpty(eff.Paint.Swatch)) cat.TrySwatch(eff.Paint.Swatch, out swatch);

            var r = new ResolvedAppearance
            {
                CarId = chassis.Car,
                BodyStyle = chassis.Style ?? "",
                Front = eff.Body.Front,
                Rear = eff.Body.Rear,
                Side = eff.Body.Side,
                RearAero = eff.Body.RearAero,
                Exhaust = eff.Body.Exhaust,
                RearAeroMount = chassis.Anchors.TryGetValue("rearAero", out AnchorDef aero) ? aero.Mount : "",
                RimId = rim.Id,
                RimName = rim.Name,
                RimStyle = rim.Style,
                RimDiameterIn = eff.Wheels.DiameterIn,
                RimFraction = eff.Wheels.DiameterIn == fit.StockDiameterIn ? fit.StockRimFraction : WheelFitment.RimFraction(eff.Wheels.DiameterIn, fit.TyreRadiusM),
                TyreRadiusM = fit.TyreRadiusM,
                TyreWidthM = fit.TyreWidthM,
                OffsetStep = eff.Wheels.OffsetStep,
                OffsetM = WheelFitment.OffsetM(eff.Wheels.OffsetStep),
                RimFinishId = rimFinish.Id,
                RimColor = rimFinish.Color,
                RimSheen = rimFinish.Finish,
                Primary = eff.Paint.Primary,
                Secondary = eff.Paint.Secondary,
                Accent = eff.Paint.Accent,
                Finish = eff.Paint.Finish,
                TwoTone = eff.Paint.TwoTone,
                TwoToneZone = twoTone?.Zone ?? "body",
                SwatchId = swatch?.Id ?? "",
                SwatchName = swatch?.Name ?? "",
                FlipTint = swatch?.FlipTint,
                HeadTint = head.Id,
                HeadTransmission = head.Transmission,
                TailTint = tail.Id,
                TailTransmission = tail.Transmission,
                Glass = glass.Id,
                GlassTransmission = glass.Transmission,
                PlateText = eff.Plate.Text,
                PlateStyle = plate.Id,
                PlateBackground = plate.Background,
                PlateTextColor = plate.Text,
                DecalKeepOut = chassis.Decals.KeepOut,
                Anchors = chassis.Anchors,
                Effective = eff,
                EffectiveHash = LiveryHash.Of(eff),
                SourceHash = sourceHash,
                Notices = notices,
            };
            for (int i = 0; i < eff.Decals.Count; i++)
            {
                DecalLayer d = eff.Decals[i];
                cat.TryShape(d.Shape, out DecalShapeDef shape);
                r.Decals.Add(new ResolvedDecal
                {
                    Layer = i, ShapeId = shape.Id, Render = shape.Render, Glyph = shape.Glyph, Color = d.Color, Zone = d.Zone,
                    U = d.U, V = d.V, Scale = d.Scale, RotationDeg = d.RotationDeg, Opacity = d.Opacity, Mirror = d.Mirror, Flip = d.Flip,
                });
            }
            return r;
        }
    }
}
