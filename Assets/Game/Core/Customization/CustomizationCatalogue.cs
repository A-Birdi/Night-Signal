using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using Newtonsoft.Json;

namespace NightSignal.Core.Customization
{
    // Authored appearance catalogue (Assets/Content/Data/authored/customization.json, schema night-signal/customization@1).
    // Public fields keep the Newtonsoft model simple and identical in Unity and the .NET services, like Core/Builds.

    /// <summary>Inclusive integer range.</summary>
    public sealed class IntRange
    {
        public int Min;
        public int Max;

        public bool Contains(int value) => value >= Min && value <= Max;

        public override string ToString() => Min.ToString(CultureInfo.InvariantCulture) + "–" + Max.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class PaintFinishDef
    {
        public string Id;
        public string Name;
    }

    /// <summary>A two-tone style and the paint zone the secondary colour covers.</summary>
    public sealed class TwoToneDef
    {
        public string Id;
        public string Name;
        public string Zone;
    }

    /// <summary>
    /// A named paint for the primary (body) colour. Free swatches are palette shortcuts; a signature swatch is unlocked by
    /// its paint cosmetic and may carry a <see cref="FlipTint"/> (the colour-shift seen at glancing angles), which only
    /// signature swatches provide.
    /// </summary>
    public sealed class PaintSwatchDef
    {
        public string Id;
        public string Name;
        public string Color;
        public string Finish;
        /// <summary>Glancing-angle tint for pearl/metallic signature paints, or null.</summary>
        public string FlipTint;
        /// <summary>Unlocking cosmetic (cosmetics.json category "paint"), or null = free.</summary>
        public string CosmeticId;
    }

    public sealed class RimDesignDef
    {
        public string Id;
        public string Name;
        /// <summary>Render style (see <see cref="AppearanceVocabulary.RimStyles"/>).</summary>
        public string Style;
        /// <summary>Whole-inch rim diameters this design is made in.</summary>
        public IntRange DiameterIn = new IntRange();
    }

    public sealed class RimFinishDef
    {
        public string Id;
        public string Name;
        public string Color;
        /// <summary>Sheen, from the paint finish vocabulary.</summary>
        public string Finish;
    }

    /// <summary>A safe lamp tint. <see cref="Transmission"/> ≥ 0.6: nothing makes a lamp invisible.</summary>
    public sealed class LampPresetDef
    {
        public string Id;
        public string Name;
        public bool Head;
        /// <summary>Tail lamps stay red: only presets that keep them readable are allowed at the rear.</summary>
        public bool Tail;
        public double Transmission;
    }

    /// <summary>Window tint in readable bounds (visible light transmission ≥ 0.35).</summary>
    public sealed class GlassTintDef
    {
        public string Id;
        public string Name;
        public double Transmission;
    }

    /// <summary>Plate style; background/text contrast is at least 4.5:1 so the plate always reads.</summary>
    public sealed class PlateStyleDef
    {
        public string Id;
        public string Name;
        public string Background;
        public string Text;
    }

    /// <summary>One entry in the vetted decal library (no uploads, no player-typed decal text).</summary>
    public sealed class DecalShapeDef
    {
        public string Id;
        public string Name;
        /// <summary>Procedural render kind (see <see cref="AppearanceVocabulary.DecalRenders"/>).</summary>
        public string Render;
        /// <summary>"0".."9" for digit; 1–12 plate-rule characters for text; otherwise null.</summary>
        public string Glyph;
        /// <summary>Unlocking cosmetic (cosmetics.json category "decal"), or null = free.</summary>
        public string CosmeticId;
    }

    /// <summary>Variants a chassis offers per family (each list includes "stock").</summary>
    public sealed class ChassisFamilies
    {
        public List<string> Front = new List<string>();
        public List<string> Rear = new List<string>();
        public List<string> Side = new List<string>();
        public List<string> RearAero = new List<string>();
        public List<string> Exhaust = new List<string>();

        public IReadOnlyList<string> Of(string family)
        {
            switch (family)
            {
                case AppearanceVocabulary.Front: return Front;
                case AppearanceVocabulary.Rear: return Rear;
                case AppearanceVocabulary.Side: return Side;
                case AppearanceVocabulary.RearAero: return RearAero;
                case AppearanceVocabulary.Exhaust: return Exhaust;
                default: return Array.Empty<string>();
            }
        }
    }

    /// <summary>Wheel geometry and fitment for one chassis (tyre size is fixed; rims and offsets are visual).</summary>
    public sealed class ChassisWheels
    {
        /// <summary>Tyre outer radius, identical to cars.body.json wheelRadius. Never changes with the rim.</summary>
        public double TyreRadiusM;
        public double TyreWidthM;
        /// <summary>The stock rim radius / tyre radius (cars.body.json rimFraction); the stock look is unchanged.</summary>
        public double StockRimFraction;
        /// <summary>Arch clearance outside the stock tyre face: width/2 − track/2 − tyreWidth/2 (cars.tuning.json).</summary>
        public int ArchClearanceMm;
        public string StockRim;
        public int StockDiameterIn;
        public string StockFinish;
        public IntRange DiameterIn = new IntRange();
        public IntRange OffsetStep = new IntRange();
    }

    public sealed class StockPaintDef
    {
        public string Primary;
        public string Secondary;
        public string Accent;
        public string Finish;
    }

    public sealed class ChassisPaint
    {
        /// <summary>Paint zones this body has (see <see cref="AppearanceVocabulary.PaintZones"/>).</summary>
        public List<string> Zones = new List<string>();
        /// <summary>Two-tone styles offered (each style's zone must exist).</summary>
        public List<string> TwoTone = new List<string>();
        public StockPaintDef Stock = new StockPaintDef();
    }

    /// <summary>A decal mask rectangle in zone (u, v) space; the renderer never paints decals inside it.</summary>
    public sealed class KeepOutRect
    {
        public string Id;
        public double U0;
        public double V0;
        public double U1;
        public double V1;
    }

    public sealed class ChassisDecals
    {
        public List<string> Zones = new List<string>();
        /// <summary>Zone → masked rectangles (lamps, plates, intakes).</summary>
        public Dictionary<string, List<KeepOutRect>> KeepOut = new Dictionary<string, List<KeepOutRect>>(StringComparer.Ordinal);
    }

    /// <summary>An attachment anchor in normalised body bounding-box coordinates (see the catalogue conventions).</summary>
    public sealed class AnchorDef
    {
        public string Mount;
        public double X;
        public double Y;
        public double Z;
    }

    /// <summary>Everything appearance-related about one car model, keyed by its stable car id.</summary>
    public sealed class ChassisAppearanceDef
    {
        public string Car;
        /// <summary>Body style from cars.body.json (liftback, hatch, notchback, roadster, wagon, fastback, midship).</summary>
        public string Style;
        public ChassisFamilies Families = new ChassisFamilies();
        /// <summary>Documented equivalents and chassis-specific explanations, keyed by family or paint/wheels/decals.</summary>
        public Dictionary<string, string> Notes = new Dictionary<string, string>(StringComparer.Ordinal);
        public ChassisWheels Wheels = new ChassisWheels();
        public ChassisPaint Paint = new ChassisPaint();
        public ChassisDecals Decals = new ChassisDecals();
        public Dictionary<string, AnchorDef> Anchors = new Dictionary<string, AnchorDef>(StringComparer.Ordinal);

        public bool Offers(string family, string variant) => Families.Of(family).Contains(variant ?? "");

        public string Note(string key) => Notes != null && Notes.TryGetValue(key ?? "", out string n) ? n : null;

        public IReadOnlyList<KeepOutRect> KeepOut(string zone) =>
            Decals.KeepOut != null && Decals.KeepOut.TryGetValue(zone ?? "", out List<KeepOutRect> r) ? (IReadOnlyList<KeepOutRect>)r : Array.Empty<KeepOutRect>();
    }

    public sealed class CustomizationFile
    {
        public string Schema;
        public int Revision;
        public string Note;
        public Dictionary<string, string> Conventions = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<PaintFinishDef> PaintFinishes = new List<PaintFinishDef>();
        public List<TwoToneDef> TwoToneStyles = new List<TwoToneDef>();
        public List<PaintSwatchDef> PaintSwatches = new List<PaintSwatchDef>();
        public List<RimDesignDef> RimDesigns = new List<RimDesignDef>();
        public List<RimFinishDef> RimFinishes = new List<RimFinishDef>();
        public List<LampPresetDef> LampPresets = new List<LampPresetDef>();
        public List<GlassTintDef> GlassTints = new List<GlassTintDef>();
        public List<PlateStyleDef> PlateStyles = new List<PlateStyleDef>();
        public List<DecalShapeDef> DecalShapes = new List<DecalShapeDef>();
        public List<ChassisAppearanceDef> Chassis = new List<ChassisAppearanceDef>();
    }

    /// <summary>Thrown when customization.json is structurally invalid; <see cref="Errors"/> holds the exact list.</summary>
    public sealed class CustomizationDataException : Exception
    {
        public IReadOnlyList<string> Errors { get; }

        public CustomizationDataException(IReadOnlyList<string> errors)
            : base("customization.json invalid:\n - " + string.Join("\n - ", errors)) => Errors = errors;
    }

    /// <summary>
    /// The trusted, validated appearance catalogue shared by the Unity client, the Unity server and the control plane.
    /// Loading enforces the Addendum 01 §13 coverage floor (stock + ≥ 2 non-stock per bumper/front, rear, side and rear-aero
    /// family, documented equivalents carry a note), ≥ 8 rim designs that each fit every chassis, clip-free fitment, safe lamp
    /// and glass tints, readable plates and a well-formed decal library.
    /// </summary>
    public sealed class CustomizationCatalogue
    {
        public const string SchemaId = "night-signal/customization@1";
        public const string FileName = "customization.json";
        public const int MinRimDesigns = 8;
        public const double MinLampTransmission = 0.6;
        public const double MinGlassTransmission = 0.35;
        public const double MinPlateContrast = 4.5;
        public const string DefaultLamp = "clear";
        public const string DefaultGlass = "clear";
        public const string DefaultPlateStyle = "standard";

        public int Revision { get; private set; }
        /// <summary>SHA-256 of the LF-normalised document text.</summary>
        public string Hash { get; private set; }
        public IReadOnlyList<PaintFinishDef> PaintFinishes { get; private set; }
        public IReadOnlyList<TwoToneDef> TwoToneStyles { get; private set; }
        public IReadOnlyList<PaintSwatchDef> PaintSwatches { get; private set; }
        public IReadOnlyList<RimDesignDef> RimDesigns { get; private set; }
        public IReadOnlyList<RimFinishDef> RimFinishes { get; private set; }
        public IReadOnlyList<LampPresetDef> LampPresets { get; private set; }
        public IReadOnlyList<GlassTintDef> GlassTints { get; private set; }
        public IReadOnlyList<PlateStyleDef> PlateStyles { get; private set; }
        public IReadOnlyList<DecalShapeDef> DecalShapes { get; private set; }
        public IReadOnlyList<ChassisAppearanceDef> Chassis { get; private set; }

        Dictionary<string, ChassisAppearanceDef> chassisByCar;
        Dictionary<string, RimDesignDef> rimById;
        Dictionary<string, RimFinishDef> rimFinishById;
        Dictionary<string, PaintSwatchDef> swatchById;
        Dictionary<string, TwoToneDef> twoToneById;
        Dictionary<string, LampPresetDef> lampById;
        Dictionary<string, GlassTintDef> glassById;
        Dictionary<string, PlateStyleDef> plateById;
        Dictionary<string, DecalShapeDef> shapeById;

        public bool TryChassis(string carId, out ChassisAppearanceDef c) => chassisByCar.TryGetValue(carId ?? "", out c);
        public bool TryRim(string id, out RimDesignDef r) => rimById.TryGetValue(id ?? "", out r);
        public bool TryRimFinish(string id, out RimFinishDef f) => rimFinishById.TryGetValue(id ?? "", out f);
        public bool TrySwatch(string id, out PaintSwatchDef s) => swatchById.TryGetValue(id ?? "", out s);
        public bool TryTwoTone(string id, out TwoToneDef t) => twoToneById.TryGetValue(id ?? "", out t);
        public bool TryLamp(string id, out LampPresetDef l) => lampById.TryGetValue(id ?? "", out l);
        public bool TryGlass(string id, out GlassTintDef g) => glassById.TryGetValue(id ?? "", out g);
        public bool TryPlateStyle(string id, out PlateStyleDef p) => plateById.TryGetValue(id ?? "", out p);
        public bool TryShape(string id, out DecalShapeDef s) => shapeById.TryGetValue(id ?? "", out s);
        public bool IsFinish(string id) => AppearanceVocabulary.PaintFinishes.Contains(id ?? "");

        public ChassisAppearanceDef ChassisFor(string carId)
        {
            if (!TryChassis(carId, out ChassisAppearanceDef c)) throw new KeyNotFoundException($"No customization chassis for {carId}");
            return c;
        }

        /// <summary>Rim sizes of <paramref name="rim"/> that fit the chassis (design range ∩ chassis fitment), ascending.</summary>
        public static IReadOnlyList<int> FittingDiameters(RimDesignDef rim, ChassisAppearanceDef chassis)
        {
            var sizes = new List<int>();
            if (rim == null || chassis == null) return sizes;
            int lo = Math.Max(rim.DiameterIn.Min, chassis.Wheels.DiameterIn.Min), hi = Math.Min(rim.DiameterIn.Max, chassis.Wheels.DiameterIn.Max);
            for (int d = lo; d <= hi; d++) sizes.Add(d);
            return sizes;
        }

        /// <summary>The stock livery of a chassis: every family stock, stock rim/size/finish, factory paint, clear lamps.</summary>
        public LiveryDocument StockLivery(string carId) => LiveryDocument.Stock(ChassisFor(carId));

        public static CustomizationCatalogue Load(string json)
        {
            if (!TryLoad(json, out CustomizationCatalogue catalogue, out List<string> errors)) throw new CustomizationDataException(errors);
            return catalogue;
        }

        /// <summary>Parses and validates; on failure <paramref name="errors"/> is the exact, ordered problem list.</summary>
        public static bool TryLoad(string json, out CustomizationCatalogue catalogue, out List<string> errors)
        {
            catalogue = null;
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) { errors.Add("customization.json is empty"); return false; }
            CustomizationFile file;
            try
            {
                file = JsonConvert.DeserializeObject<CustomizationFile>(json, new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Error,
                    DateParseHandling = DateParseHandling.None,
                });
            }
            catch (JsonException e)
            {
                errors.Add("customization.json: " + e.Message);
                return false;
            }
            if (file == null) { errors.Add("customization.json is empty"); return false; }
            if (file.Schema != SchemaId) { errors.Add($"customization.json: expected schema {SchemaId}, found {file.Schema ?? "none"}"); return false; }
            if (file.Revision < 1) errors.Add("revision must be ≥ 1");

            var cat = new CustomizationCatalogue { Revision = file.Revision };
            cat.PaintFinishes = ValidateFinishes(file.PaintFinishes, errors);
            cat.twoToneById = ValidateTwoTone(file.TwoToneStyles, errors);
            cat.TwoToneStyles = file.TwoToneStyles ?? new List<TwoToneDef>();
            cat.swatchById = ValidateSwatches(file.PaintSwatches, errors);
            cat.PaintSwatches = file.PaintSwatches ?? new List<PaintSwatchDef>();
            cat.rimById = ValidateRims(file.RimDesigns, errors);
            cat.RimDesigns = file.RimDesigns ?? new List<RimDesignDef>();
            cat.rimFinishById = ValidateRimFinishes(file.RimFinishes, errors);
            cat.RimFinishes = file.RimFinishes ?? new List<RimFinishDef>();
            cat.lampById = ValidateLamps(file.LampPresets, errors);
            cat.LampPresets = file.LampPresets ?? new List<LampPresetDef>();
            cat.glassById = ValidateGlass(file.GlassTints, errors);
            cat.GlassTints = file.GlassTints ?? new List<GlassTintDef>();
            cat.plateById = ValidatePlates(file.PlateStyles, errors);
            cat.PlateStyles = file.PlateStyles ?? new List<PlateStyleDef>();
            cat.shapeById = ValidateShapes(file.DecalShapes, errors);
            cat.DecalShapes = file.DecalShapes ?? new List<DecalShapeDef>();

            cat.chassisByCar = new Dictionary<string, ChassisAppearanceDef>(StringComparer.Ordinal);
            foreach (ChassisAppearanceDef c in file.Chassis ?? new List<ChassisAppearanceDef>())
            {
                if (c == null || string.IsNullOrEmpty(c.Car)) { errors.Add("A chassis entry has no car id"); continue; }
                if (cat.chassisByCar.ContainsKey(c.Car)) { errors.Add($"Duplicate chassis entry {c.Car}"); continue; }
                cat.chassisByCar.Add(c.Car, c);
                cat.ValidateChassis(c, errors);
            }
            cat.Chassis = file.Chassis ?? new List<ChassisAppearanceDef>();
            if (cat.Chassis.Count == 0) errors.Add("No chassis entries");

            if (errors.Count > 0) return false;
            cat.Hash = BuildHashing.Sha256Hex(json.Replace("\r\n", "\n"));
            catalogue = cat;
            return true;
        }

        // ------------------------------------------------------------------ shared lists

        static IReadOnlyList<PaintFinishDef> ValidateFinishes(List<PaintFinishDef> list, List<string> errors)
        {
            list = list ?? new List<PaintFinishDef>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (PaintFinishDef f in list)
            {
                if (f == null || !AppearanceVocabulary.PaintFinishes.Contains(f.Id ?? "")) { errors.Add($"Paint finish {f?.Id}: not in the finish vocabulary"); continue; }
                if (!seen.Add(f.Id)) errors.Add($"Duplicate paint finish {f.Id}");
                if (string.IsNullOrWhiteSpace(f.Name)) errors.Add($"Paint finish {f.Id}: no name");
            }
            foreach (string id in AppearanceVocabulary.PaintFinishes)
                if (!seen.Contains(id)) errors.Add($"Paint finish {id} is missing");
            return list;
        }

        static Dictionary<string, TwoToneDef> ValidateTwoTone(List<TwoToneDef> list, List<string> errors)
        {
            var map = new Dictionary<string, TwoToneDef>(StringComparer.Ordinal);
            foreach (TwoToneDef t in list ?? new List<TwoToneDef>())
            {
                if (t == null || !AppearanceVocabulary.TwoToneStyles.Contains(t.Id ?? "")) { errors.Add($"Two-tone style {t?.Id}: not in the two-tone vocabulary"); continue; }
                if (map.ContainsKey(t.Id)) { errors.Add($"Duplicate two-tone style {t.Id}"); continue; }
                map.Add(t.Id, t);
                if (string.IsNullOrWhiteSpace(t.Name)) errors.Add($"Two-tone style {t.Id}: no name");
                if (!AppearanceVocabulary.PaintZones.Contains(t.Zone ?? "")) errors.Add($"Two-tone style {t.Id}: unknown paint zone {t.Zone}");
            }
            foreach (string id in AppearanceVocabulary.TwoToneStyles)
                if (!map.ContainsKey(id)) errors.Add($"Two-tone style {id} is missing");
            return map;
        }

        static Dictionary<string, PaintSwatchDef> ValidateSwatches(List<PaintSwatchDef> list, List<string> errors)
        {
            var map = new Dictionary<string, PaintSwatchDef>(StringComparer.Ordinal);
            foreach (PaintSwatchDef s in list ?? new List<PaintSwatchDef>())
            {
                if (s == null || !CatalogueIds.IsValid(s.Id)) { errors.Add($"Paint swatch {s?.Id}: invalid id"); continue; }
                if (map.ContainsKey(s.Id)) { errors.Add($"Duplicate paint swatch {s.Id}"); continue; }
                map.Add(s.Id, s);
                if (string.IsNullOrWhiteSpace(s.Name)) errors.Add($"Paint swatch {s.Id}: no name");
                if (!HexColor.IsCanonical(s.Color)) errors.Add($"Paint swatch {s.Id}: color must be upper-case #RRGGBB");
                if (!AppearanceVocabulary.PaintFinishes.Contains(s.Finish ?? "")) errors.Add($"Paint swatch {s.Id}: unknown finish {s.Finish}");
                if (s.FlipTint != null && !HexColor.IsCanonical(s.FlipTint)) errors.Add($"Paint swatch {s.Id}: flipTint must be upper-case #RRGGBB or null");
                if (s.FlipTint != null && s.CosmeticId == null) errors.Add($"Paint swatch {s.Id}: only signature (cosmetic) swatches carry a flip tint");
                if (s.CosmeticId != null && s.CosmeticId.Length == 0) errors.Add($"Paint swatch {s.Id}: cosmeticId must be null or an id");
            }
            return map;
        }

        static Dictionary<string, RimDesignDef> ValidateRims(List<RimDesignDef> list, List<string> errors)
        {
            var map = new Dictionary<string, RimDesignDef>(StringComparer.Ordinal);
            foreach (RimDesignDef r in list ?? new List<RimDesignDef>())
            {
                if (r == null || !CatalogueIds.IsValid(r.Id)) { errors.Add($"Rim design {r?.Id}: invalid id"); continue; }
                if (map.ContainsKey(r.Id)) { errors.Add($"Duplicate rim design {r.Id}"); continue; }
                map.Add(r.Id, r);
                if (string.IsNullOrWhiteSpace(r.Name)) errors.Add($"Rim design {r.Id}: no name");
                if (!AppearanceVocabulary.RimStyles.Contains(r.Style ?? "")) errors.Add($"Rim design {r.Id}: unknown render style {r.Style}");
                r.DiameterIn = r.DiameterIn ?? new IntRange();
                if (r.DiameterIn.Min > r.DiameterIn.Max || r.DiameterIn.Min < WheelFitment.MinRimInches || r.DiameterIn.Max > WheelFitment.MaxRimInches)
                    errors.Add($"Rim design {r.Id}: diameter range {r.DiameterIn} must lie within {WheelFitment.MinRimInches}–{WheelFitment.MaxRimInches} in");
            }
            if (map.Count < MinRimDesigns) errors.Add($"At least {MinRimDesigns} rim designs are required (found {map.Count})");
            return map;
        }

        static Dictionary<string, RimFinishDef> ValidateRimFinishes(List<RimFinishDef> list, List<string> errors)
        {
            var map = new Dictionary<string, RimFinishDef>(StringComparer.Ordinal);
            foreach (RimFinishDef f in list ?? new List<RimFinishDef>())
            {
                if (f == null || !CatalogueIds.IsValid(f.Id)) { errors.Add($"Rim finish {f?.Id}: invalid id"); continue; }
                if (map.ContainsKey(f.Id)) { errors.Add($"Duplicate rim finish {f.Id}"); continue; }
                map.Add(f.Id, f);
                if (string.IsNullOrWhiteSpace(f.Name)) errors.Add($"Rim finish {f.Id}: no name");
                if (!HexColor.IsCanonical(f.Color)) errors.Add($"Rim finish {f.Id}: color must be upper-case #RRGGBB");
                if (!AppearanceVocabulary.PaintFinishes.Contains(f.Finish ?? "")) errors.Add($"Rim finish {f.Id}: unknown finish {f.Finish}");
            }
            if (map.Count == 0) errors.Add("At least one rim finish is required");
            return map;
        }

        static Dictionary<string, LampPresetDef> ValidateLamps(List<LampPresetDef> list, List<string> errors)
        {
            var map = new Dictionary<string, LampPresetDef>(StringComparer.Ordinal);
            foreach (LampPresetDef l in list ?? new List<LampPresetDef>())
            {
                if (l == null || !CatalogueIds.IsValid(l.Id)) { errors.Add($"Lamp preset {l?.Id}: invalid id"); continue; }
                if (map.ContainsKey(l.Id)) { errors.Add($"Duplicate lamp preset {l.Id}"); continue; }
                map.Add(l.Id, l);
                if (string.IsNullOrWhiteSpace(l.Name)) errors.Add($"Lamp preset {l.Id}: no name");
                if (!l.Head && !l.Tail) errors.Add($"Lamp preset {l.Id}: applies to neither head nor tail lamps");
                if (double.IsNaN(l.Transmission) || l.Transmission < MinLampTransmission || l.Transmission > 1.0)
                    errors.Add($"Lamp preset {l.Id}: transmission must be {MinLampTransmission.ToString(CultureInfo.InvariantCulture)}–1 so lamps stay visible");
            }
            if (!map.TryGetValue(DefaultLamp, out LampPresetDef d) || !d.Head || !d.Tail) errors.Add($"Lamp preset {DefaultLamp} must exist for head and tail lamps");
            return map;
        }

        static Dictionary<string, GlassTintDef> ValidateGlass(List<GlassTintDef> list, List<string> errors)
        {
            var map = new Dictionary<string, GlassTintDef>(StringComparer.Ordinal);
            foreach (GlassTintDef g in list ?? new List<GlassTintDef>())
            {
                if (g == null || !CatalogueIds.IsValid(g.Id)) { errors.Add($"Glass tint {g?.Id}: invalid id"); continue; }
                if (map.ContainsKey(g.Id)) { errors.Add($"Duplicate glass tint {g.Id}"); continue; }
                map.Add(g.Id, g);
                if (string.IsNullOrWhiteSpace(g.Name)) errors.Add($"Glass tint {g.Id}: no name");
                if (double.IsNaN(g.Transmission) || g.Transmission < MinGlassTransmission || g.Transmission > 1.0)
                    errors.Add($"Glass tint {g.Id}: transmission must be {MinGlassTransmission.ToString(CultureInfo.InvariantCulture)}–1 (readable bounds)");
            }
            if (!map.ContainsKey(DefaultGlass)) errors.Add($"Glass tint {DefaultGlass} must exist");
            return map;
        }

        static Dictionary<string, PlateStyleDef> ValidatePlates(List<PlateStyleDef> list, List<string> errors)
        {
            var map = new Dictionary<string, PlateStyleDef>(StringComparer.Ordinal);
            foreach (PlateStyleDef p in list ?? new List<PlateStyleDef>())
            {
                if (p == null || !CatalogueIds.IsValid(p.Id)) { errors.Add($"Plate style {p?.Id}: invalid id"); continue; }
                if (map.ContainsKey(p.Id)) { errors.Add($"Duplicate plate style {p.Id}"); continue; }
                map.Add(p.Id, p);
                if (string.IsNullOrWhiteSpace(p.Name)) errors.Add($"Plate style {p.Id}: no name");
                if (!HexColor.IsCanonical(p.Background) || !HexColor.IsCanonical(p.Text)) { errors.Add($"Plate style {p.Id}: colors must be upper-case #RRGGBB"); continue; }
                if (HexColor.Contrast(p.Background, p.Text) < MinPlateContrast)
                    errors.Add($"Plate style {p.Id}: text contrast below {MinPlateContrast.ToString(CultureInfo.InvariantCulture)}:1");
            }
            if (!map.ContainsKey(DefaultPlateStyle)) errors.Add($"Plate style {DefaultPlateStyle} must exist");
            return map;
        }

        static Dictionary<string, DecalShapeDef> ValidateShapes(List<DecalShapeDef> list, List<string> errors)
        {
            var map = new Dictionary<string, DecalShapeDef>(StringComparer.Ordinal);
            foreach (DecalShapeDef s in list ?? new List<DecalShapeDef>())
            {
                if (s == null || !CatalogueIds.IsValid(s.Id)) { errors.Add($"Decal shape {s?.Id}: invalid id"); continue; }
                if (map.ContainsKey(s.Id)) { errors.Add($"Duplicate decal shape {s.Id}"); continue; }
                map.Add(s.Id, s);
                if (string.IsNullOrWhiteSpace(s.Name)) errors.Add($"Decal shape {s.Id}: no name");
                if (!AppearanceVocabulary.DecalRenders.Contains(s.Render ?? "")) { errors.Add($"Decal shape {s.Id}: unknown render kind {s.Render}"); continue; }
                if (s.Render == AppearanceVocabulary.RenderDigit)
                {
                    if (s.Glyph == null || s.Glyph.Length != 1 || s.Glyph[0] < '0' || s.Glyph[0] > '9') errors.Add($"Decal shape {s.Id}: a digit needs a glyph \"0\"–\"9\"");
                }
                else if (s.Render == AppearanceVocabulary.RenderText)
                {
                    if (!PlateText.IsValid(s.Glyph, LiveryLimits.MaxTextGlyphLength, allowEmpty: false))
                        errors.Add($"Decal shape {s.Id}: text glyph must be 1–{LiveryLimits.MaxTextGlyphLength} characters of A–Z, 0–9, space and '-'");
                }
                else if (s.Glyph != null) errors.Add($"Decal shape {s.Id}: only digit and text shapes carry a glyph");
                if (s.CosmeticId != null && s.CosmeticId.Length == 0) errors.Add($"Decal shape {s.Id}: cosmeticId must be null or an id");
            }
            if (map.Count == 0) errors.Add("The decal library is empty");
            return map;
        }

        // ------------------------------------------------------------------ chassis

        void ValidateChassis(ChassisAppearanceDef c, List<string> errors)
        {
            string where = "Chassis " + c.Car;
            if (string.IsNullOrWhiteSpace(c.Style)) errors.Add($"{where}: no body style");
            c.Families = c.Families ?? new ChassisFamilies();
            c.Notes = c.Notes ?? new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in c.Notes)
            {
                if (!AppearanceVocabulary.NoteKeys.Contains(kv.Key)) errors.Add($"{where}: note for unknown key {kv.Key}");
                if (string.IsNullOrWhiteSpace(kv.Value)) errors.Add($"{where}: empty note for {kv.Key}");
            }

            foreach (string family in AppearanceVocabulary.Families)
            {
                IReadOnlyList<string> offered = c.Families.Of(family) ?? Array.Empty<string>();
                if (!offered.Contains(AppearanceVocabulary.Stock)) errors.Add($"{where}/{family}: stock must be offered");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool equivalent = false;
                foreach (string v in offered)
                {
                    if (!AppearanceVocabulary.IsVariant(family, v)) errors.Add($"{where}/{family}: unknown variant {v}");
                    if (!seen.Add(v ?? "")) errors.Add($"{where}/{family}: duplicate variant {v}");
                    if (AppearanceVocabulary.IsEquivalent(family, v)) equivalent = true;
                }
                int nonStock = seen.Count(v => v != AppearanceVocabulary.Stock && AppearanceVocabulary.IsVariant(family, v));
                int floor = AppearanceVocabulary.FloorFamilies.Contains(family) ? AppearanceVocabulary.MinNonStockPerFloorFamily : AppearanceVocabulary.MinNonStockExhaust;
                if (nonStock < floor) errors.Add($"{where}/{family}: needs stock plus at least {floor} non-stock choice(s), found {nonStock}");
                if (equivalent && c.Note(family) == null) errors.Add($"{where}/{family}: a documented-equivalent variant needs a note explaining why");
            }
            ValidateWheels(c, where, errors);
            ValidatePaint(c, where, errors);
            ValidateDecalZones(c, where, errors);
            ValidateAnchors(c, where, errors);
        }

        void ValidateWheels(ChassisAppearanceDef c, string where, List<string> errors)
        {
            ChassisWheels w = c.Wheels;
            if (w == null) { errors.Add($"{where}: no wheel geometry"); c.Wheels = new ChassisWheels(); return; }
            w.DiameterIn = w.DiameterIn ?? new IntRange();
            w.OffsetStep = w.OffsetStep ?? new IntRange();
            where += "/wheels";
            if (!(w.TyreRadiusM >= 0.2 && w.TyreRadiusM <= 0.5)) { errors.Add($"{where}: tyre radius out of range"); return; }
            if (!(w.TyreWidthM > 0.1 && w.TyreWidthM < 0.4)) errors.Add($"{where}: tyre width out of range");
            if (!(w.StockRimFraction >= WheelFitment.MinRimFraction && w.StockRimFraction <= WheelFitment.MaxRimFraction))
                errors.Add($"{where}: stock rim fraction outside {WheelFitment.MinRimFraction.ToString(CultureInfo.InvariantCulture)}–{WheelFitment.MaxRimFraction.ToString(CultureInfo.InvariantCulture)}");
            int expectedStock = (int)Math.Round(2.0 * w.TyreRadiusM * w.StockRimFraction / WheelFitment.MetersPerInch, MidpointRounding.AwayFromZero);
            if (w.StockDiameterIn != expectedStock) errors.Add($"{where}: stock diameter {w.StockDiameterIn} in does not match the stock rim fraction ({expectedStock} in)");
            if (!TryRim(w.StockRim, out RimDesignDef stockRim)) errors.Add($"{where}: unknown stock rim {w.StockRim}");
            else if (!stockRim.DiameterIn.Contains(w.StockDiameterIn)) errors.Add($"{where}: stock rim {w.StockRim} is not made in {w.StockDiameterIn} in");
            if (!TryRimFinish(w.StockFinish, out _)) errors.Add($"{where}: unknown stock rim finish {w.StockFinish}");

            IntRange d = w.DiameterIn;
            if (d.Min > d.Max || !d.Contains(w.StockDiameterIn)) errors.Add($"{where}: fitment {d} in must include the stock {w.StockDiameterIn} in");
            if (d.Min < WheelFitment.MinRimInches || d.Max > WheelFitment.MaxRimInches) errors.Add($"{where}: fitment {d} in outside {WheelFitment.MinRimInches}–{WheelFitment.MaxRimInches} in");
            if (d.Min < w.StockDiameterIn - WheelFitment.MaxShrinkBelowStockIn) errors.Add($"{where}: {d.Min} in would not clear the stock brakes (min {w.StockDiameterIn - WheelFitment.MaxShrinkBelowStockIn} in)");
            if (WheelFitment.RimFraction(d.Min, w.TyreRadiusM) < WheelFitment.MinRimFraction - 1e-9) errors.Add($"{where}: {d.Min} in rim is too small for the tyre");
            if (WheelFitment.RimFraction(d.Max, w.TyreRadiusM) > WheelFitment.MaxRimFraction + 1e-9) errors.Add($"{where}: {d.Max} in rim leaves too little sidewall (tyre would clip)");

            // Proper fit per chassis: every shared rim design must be available on every car in at least one size.
            foreach (RimDesignDef rim in RimDesigns)
                if (rim != null && rim.DiameterIn != null && FittingDiameters(rim, c).Count == 0)
                    errors.Add($"{where}: rim {rim.Id} ({rim.DiameterIn} in) has no size inside the fitment {d} in");

            IntRange o = w.OffsetStep;
            if (o.Min > 0 || o.Max < 0 || o.Min < -WheelFitment.MaxInwardSteps) errors.Add($"{where}: offset range {o.Min}..{o.Max} must include 0 and tuck at most {WheelFitment.MaxInwardSteps} steps");
            if (w.ArchClearanceMm < 0) errors.Add($"{where}: arch clearance cannot be negative");
            if (o.Max > 0 && o.Max * WheelFitment.OffsetStepMm + WheelFitment.ArchMarginMm > w.ArchClearanceMm)
                errors.Add($"{where}: offset +{o.Max} ({o.Max * WheelFitment.OffsetStepMm} mm) would push the tyre past the arch ({w.ArchClearanceMm} mm clearance)");
        }

        void ValidatePaint(ChassisAppearanceDef c, string where, List<string> errors)
        {
            ChassisPaint p = c.Paint;
            if (p == null) { errors.Add($"{where}: no paint data"); c.Paint = new ChassisPaint(); return; }
            p.Zones = p.Zones ?? new List<string>();
            p.TwoTone = p.TwoTone ?? new List<string>();
            where += "/paint";
            foreach (string z in p.Zones) if (!AppearanceVocabulary.PaintZones.Contains(z ?? "")) errors.Add($"{where}: unknown paint zone {z}");
            if (!p.Zones.Contains("body")) errors.Add($"{where}: the body zone is required");
            if (!p.TwoTone.Contains("none")) errors.Add($"{where}: two-tone none must be offered");
            foreach (string t in p.TwoTone)
            {
                if (!TryTwoTone(t, out TwoToneDef def)) { errors.Add($"{where}: unknown two-tone style {t}"); continue; }
                if (!p.Zones.Contains(def.Zone)) errors.Add($"{where}: two-tone {t} needs the {def.Zone} zone");
            }
            StockPaintDef s = p.Stock;
            if (s == null) { errors.Add($"{where}: no stock paint"); p.Stock = new StockPaintDef(); return; }
            if (!HexColor.IsCanonical(s.Primary) || !HexColor.IsCanonical(s.Secondary) || !HexColor.IsCanonical(s.Accent))
                errors.Add($"{where}: stock colors must be upper-case #RRGGBB");
            if (!IsFinish(s.Finish)) errors.Add($"{where}: unknown stock finish {s.Finish}");
        }

        static void ValidateDecalZones(ChassisAppearanceDef c, string where, List<string> errors)
        {
            ChassisDecals d = c.Decals;
            if (d == null) { errors.Add($"{where}: no decal zones"); c.Decals = new ChassisDecals(); return; }
            d.Zones = d.Zones ?? new List<string>();
            d.KeepOut = d.KeepOut ?? new Dictionary<string, List<KeepOutRect>>(StringComparer.Ordinal);
            where += "/decals";
            if (d.Zones.Count == 0) errors.Add($"{where}: at least one decal zone is required");
            foreach (string z in d.Zones) if (!AppearanceVocabulary.DecalZones.Contains(z ?? "")) errors.Add($"{where}: unknown decal zone {z}");
            if (d.Zones.Distinct(StringComparer.Ordinal).Count() != d.Zones.Count) errors.Add($"{where}: duplicate decal zone");
            foreach (var kv in d.KeepOut)
            {
                if (!d.Zones.Contains(kv.Key)) errors.Add($"{where}: keep-out for zone {kv.Key} which the chassis does not offer");
                foreach (KeepOutRect r in kv.Value ?? new List<KeepOutRect>())
                {
                    if (r == null || string.IsNullOrEmpty(r.Id)) { errors.Add($"{where}/{kv.Key}: a keep-out rectangle has no id"); continue; }
                    bool ok = r.U0 >= 0 && r.U0 < r.U1 && r.U1 <= 1 && r.V0 >= 0 && r.V0 < r.V1 && r.V1 <= 1;
                    if (!ok) errors.Add($"{where}/{kv.Key}/{r.Id}: rectangle must satisfy 0 ≤ u0 < u1 ≤ 1 and 0 ≤ v0 < v1 ≤ 1");
                }
            }
        }

        static void ValidateAnchors(ChassisAppearanceDef c, string where, List<string> errors)
        {
            c.Anchors = c.Anchors ?? new Dictionary<string, AnchorDef>(StringComparer.Ordinal);
            foreach (string id in AppearanceVocabulary.AnchorIds)
                if (!c.Anchors.ContainsKey(id)) errors.Add($"{where}: missing anchor {id}");
            foreach (var kv in c.Anchors)
            {
                if (!AppearanceVocabulary.AnchorIds.Contains(kv.Key)) { errors.Add($"{where}: unknown anchor {kv.Key}"); continue; }
                AnchorDef a = kv.Value;
                if (a == null || string.IsNullOrWhiteSpace(a.Mount)) { errors.Add($"{where}/anchors/{kv.Key}: no mount"); continue; }
                if (!(a.X >= -0.5 && a.X <= 0.5 && a.Y >= 0 && a.Y <= 1 && a.Z >= -0.5 && a.Z <= 0.5))
                    errors.Add($"{where}/anchors/{kv.Key}: outside the normalised body box");
            }
        }

        // ------------------------------------------------------------------ content cross-check

        /// <summary>
        /// Cross-checks against the content catalogue: a chassis entry for every car, no entry for an unknown car, every
        /// unlocking cosmetic exists with the right category, and every decal/paint cosmetic is usable in the Garage
        /// (spec: an unlocked item is equippable, not a string in an inventory). Returns problems (empty = consistent).
        /// </summary>
        public IReadOnlyList<string> ValidateAgainst(ContentCatalogue content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            var e = new List<string>();
            foreach (CarDef car in content.Cars)
                if (!chassisByCar.ContainsKey(car.Id)) e.Add($"No customization chassis for {car.Id}");
            foreach (ChassisAppearanceDef c in Chassis)
                if (!content.TryCar(c.Car, out _)) e.Add($"Customization chassis for unknown car {c.Car}");

            var decalUnlocks = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DecalShapeDef s in DecalShapes.Where(s => s.CosmeticId != null))
            {
                if (!content.TryCosmetic(s.CosmeticId, out CosmeticDef cos)) e.Add($"Decal shape {s.Id} names unknown cosmetic {s.CosmeticId}");
                else if (cos.Category != "decal") e.Add($"Decal shape {s.Id}: cosmetic {s.CosmeticId} is a {cos.Category}, not a decal");
                if (decalUnlocks.ContainsKey(s.CosmeticId)) e.Add($"Cosmetic {s.CosmeticId} unlocks more than one decal shape");
                else decalUnlocks.Add(s.CosmeticId, s.Id);
            }
            var paintUnlocks = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PaintSwatchDef s in PaintSwatches.Where(s => s.CosmeticId != null))
            {
                if (!content.TryCosmetic(s.CosmeticId, out CosmeticDef cos)) e.Add($"Paint swatch {s.Id} names unknown cosmetic {s.CosmeticId}");
                else if (cos.Category != "paint") e.Add($"Paint swatch {s.Id}: cosmetic {s.CosmeticId} is a {cos.Category}, not a paint");
                if (paintUnlocks.ContainsKey(s.CosmeticId)) e.Add($"Cosmetic {s.CosmeticId} unlocks more than one paint swatch");
                else paintUnlocks.Add(s.CosmeticId, s.Id);
            }
            foreach (CosmeticDef cos in content.Cosmetics)
            {
                if (cos.Category == "decal" && !decalUnlocks.ContainsKey(cos.Id)) e.Add($"Decal cosmetic {cos.Id} ({cos.Name}) has no decal shape");
                if (cos.Category == "paint" && !paintUnlocks.ContainsKey(cos.Id)) e.Add($"Paint cosmetic {cos.Id} ({cos.Name}) has no paint swatch");
            }
            return e;
        }
    }
}
