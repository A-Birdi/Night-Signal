using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Customization
{
    /// <summary>Selected variant per body family ("stock" = factory part). Purely visual.</summary>
    public sealed class BodySelection
    {
        public string Front = AppearanceVocabulary.Stock;
        public string Rear = AppearanceVocabulary.Stock;
        public string Side = AppearanceVocabulary.Stock;
        public string RearAero = AppearanceVocabulary.Stock;
        public string Exhaust = AppearanceVocabulary.Stock;

        public string Get(string family)
        {
            switch (family)
            {
                case AppearanceVocabulary.Front: return Front;
                case AppearanceVocabulary.Rear: return Rear;
                case AppearanceVocabulary.Side: return Side;
                case AppearanceVocabulary.RearAero: return RearAero;
                case AppearanceVocabulary.Exhaust: return Exhaust;
                default: throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown body family");
            }
        }

        public void Set(string family, string variant)
        {
            switch (family)
            {
                case AppearanceVocabulary.Front: Front = variant; break;
                case AppearanceVocabulary.Rear: Rear = variant; break;
                case AppearanceVocabulary.Side: Side = variant; break;
                case AppearanceVocabulary.RearAero: RearAero = variant; break;
                case AppearanceVocabulary.Exhaust: Exhaust = variant; break;
                default: throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown body family");
            }
        }

        public BodySelection Clone() => (BodySelection)MemberwiseClone();
    }

    /// <summary>
    /// Rim design, whole-inch diameter, offset step and finish. The tyre's outer radius never changes (the sidewall absorbs
    /// the rim size), so none of these is a simulation input.
    /// </summary>
    public sealed class WheelSelection
    {
        public string Rim = "";
        public int DiameterIn;
        /// <summary>5 mm per step, + outward.</summary>
        public int OffsetStep;
        public string Finish = "";

        public WheelSelection Clone() => (WheelSelection)MemberwiseClone();
    }

    /// <summary>Primary (body), secondary (two-tone zone) and accent (mirror caps, grille surround, kit contrast) paint.</summary>
    public sealed class PaintSelection
    {
        public string Primary = "#FFFFFF";
        public string Secondary = "#1C1D21";
        public string Accent = "#2A2C31";
        /// <summary>gloss | metallic | pearl | matte | satin (applies to the body paint).</summary>
        public string Finish = "gloss";
        /// <summary>none | lower | roof | hood-stripe | side-stripe</summary>
        public string TwoTone = "none";
        /// <summary>Named swatch for the primary colour, or "" (custom colour). When set, Primary/Finish equal the swatch's.</summary>
        public string Swatch = "";

        public PaintSelection Clone() => (PaintSelection)MemberwiseClone();
    }

    /// <summary>Safe lamp tints (never invisible; tail lamps stay red).</summary>
    public sealed class LampSelection
    {
        public string Head = CustomizationCatalogue.DefaultLamp;
        public string Tail = CustomizationCatalogue.DefaultLamp;

        public LampSelection Clone() => (LampSelection)MemberwiseClone();
    }

    /// <summary>Plate text (≤ 8 of A–Z, 0–9, space, '-'; "" = blank plate) and style.</summary>
    public sealed class PlateSelection
    {
        public string Text = "";
        public string Style = CustomizationCatalogue.DefaultPlateStyle;

        public PlateSelection Clone() => (PlateSelection)MemberwiseClone();
    }

    /// <summary>
    /// One decal layer; list order is layer order (index 0 is painted first, the last layer is on top). Placement is
    /// quantised: u/v in thousandths of the zone, size in centimetres, whole degrees and whole percent, so the canonical
    /// form and the livery hash are identical on every runtime. The renderer reads the double properties. Zone frames follow
    /// the renderer (CarBodyGenerator surface): left/right u = 0 at the rear, 1 at the front; v = 0 low, 1 high. Mirror also
    /// paints a mirrored copy (opposite flank for left/right, across the centre line elsewhere); Flip flips the shape.
    /// </summary>
    public sealed class DecalLayer
    {
        public string Shape = "";
        public string Color = "#FFFFFF";
        /// <summary>hood | roof | left | right | rear | front</summary>
        public string Zone = "left";
        public int UMilli = 500;
        public int VMilli = 500;
        /// <summary>Size on the body in centimetres (5..160 = 0.05..1.6 m).</summary>
        public int ScaleCenti = LiveryLimits.DefaultScaleCenti;
        /// <summary>0..359</summary>
        public int RotationDeg;
        public int OpacityPercent = LiveryLimits.MaxOpacityPercent;
        public bool Mirror;
        public bool Flip;

        public double U => UMilli / (double)LiveryLimits.UvScale;
        public double V => VMilli / (double)LiveryLimits.UvScale;
        /// <summary>Size in metres.</summary>
        public double Scale => ScaleCenti / 100.0;
        public double Opacity => OpacityPercent / 100.0;

        public DecalLayer Clone() => (DecalLayer)MemberwiseClone();

        public static int ToMilli(double value) => Quantize(value, LiveryLimits.UvScale);
        public static int ToCenti(double value) => Quantize(value, 100);

        /// <summary>Round half away from zero onto the grid; non-finite input maps to int.MinValue (always out of range).</summary>
        internal static int Quantize(double value, int scale)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e6) return int.MinValue;
            return (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
        }

        /// <summary>Normalises any whole-degree angle to 0..359.</summary>
        public static int NormalizeDegrees(int degrees) => ((degrees % 360) + 360) % 360;
    }

    /// <summary>
    /// The appearance of one car (schema night-signal/livery@1). Stored as the payload of a Core/Builds
    /// <see cref="NightSignal.Core.Builds.VisualPreset"/> (PayloadSchema = <see cref="SchemaId"/>, PayloadJson = the canonical JSON)
    /// and identified by <see cref="LiveryHash"/>. Appearance never changes a simulation input.
    /// </summary>
    public sealed class LiveryDocument
    {
        public const string SchemaId = "night-signal/livery@1";

        /// <summary>Car model id the livery was made for (a V01 livery never applies to a V02).</summary>
        public string Car = "";
        public BodySelection Body = new BodySelection();
        public WheelSelection Wheels = new WheelSelection();
        public PaintSelection Paint = new PaintSelection();
        public LampSelection Lamps = new LampSelection();
        /// <summary>Window tint id (readable bounds).</summary>
        public string Glass = CustomizationCatalogue.DefaultGlass;
        public PlateSelection Plate = new PlateSelection();
        public List<DecalLayer> Decals = new List<DecalLayer>();

        public LiveryDocument Clone() => new LiveryDocument
        {
            Car = Car,
            Body = (Body ?? new BodySelection()).Clone(),
            Wheels = (Wheels ?? new WheelSelection()).Clone(),
            Paint = (Paint ?? new PaintSelection()).Clone(),
            Lamps = (Lamps ?? new LampSelection()).Clone(),
            Glass = Glass,
            Plate = (Plate ?? new PlateSelection()).Clone(),
            Decals = (Decals ?? new List<DecalLayer>()).Select(d => d?.Clone()).ToList(),
        };

        /// <summary>Equal canonical forms (the same livery hash).</summary>
        public bool ContentEquals(LiveryDocument other) =>
            other != null && string.Equals(LiveryJson.ToCanonicalJson(this), LiveryJson.ToCanonicalJson(other), StringComparison.Ordinal);

        /// <summary>The factory appearance of a chassis.</summary>
        public static LiveryDocument Stock(ChassisAppearanceDef chassis)
        {
            if (chassis == null) throw new ArgumentNullException(nameof(chassis));
            StockPaintDef p = chassis.Paint.Stock;
            return new LiveryDocument
            {
                Car = chassis.Car,
                Wheels = new WheelSelection
                {
                    Rim = chassis.Wheels.StockRim, DiameterIn = chassis.Wheels.StockDiameterIn, OffsetStep = 0, Finish = chassis.Wheels.StockFinish,
                },
                Paint = new PaintSelection { Primary = p.Primary, Secondary = p.Secondary, Accent = p.Accent, Finish = p.Finish },
            };
        }
    }
}
