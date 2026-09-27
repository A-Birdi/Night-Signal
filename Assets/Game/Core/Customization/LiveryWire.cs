using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Customization
{
    public sealed class LiveryWireResult
    {
        public LiveryDocument Document;
        public List<string> Errors = new List<string>();
        public bool Ok => Document != null && Errors.Count == 0;
    }

    /// <summary>
    /// Compact positional form of an APPLIED livery for race rosters and meet refreshes (bounded decal definitions, never
    /// textures). Version 1:
    /// <code>[1,car,[front,rear,side,rearAero,exhaust],[rim,diameterIn,offsetStep,rimFinish],[primary,secondary,accent,finish,twoTone,swatch],
    /// [head,tail,glass],[plateText,plateStyle],[[shape,color,zone,uMilli,vMilli,scaleCenti,rotationDeg,opacityPercent,flags],…]]</code>
    /// Colours drop the '#'; flags: 1 = mirror, 2 = flip. Decoding restores exactly the same document, so the receiver
    /// computes the same <see cref="LiveryHash"/>. The receiver still validates/resolves against its catalogue.
    /// <para>
    /// Size bound: <see cref="MaxWireBytes"/> = 5120 bytes (UTF-8). Worst case (every catalogue-controlled id at the
    /// 24-character limit, longest vocabulary ids, 64 layers at maximal numeric widths): 354-byte header + 64 × 69-byte layers
    /// + 63 separators = 4833 bytes, pinned by CustomizationTests. A three-layer livery is ≈ 0.3 KB, ten layers ≈ 0.7 KB.
    /// Anything larger is refused before parsing.
    /// </para>
    /// </summary>
    public static class LiveryWire
    {
        public const int Version = 1;
        public const int MaxWireBytes = 5120;
        const int FlagMirror = 1, FlagFlip = 2;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Encodes a structurally valid livery. Throws <see cref="ArgumentException"/> for a document that is not.</summary>
        public static string Encode(LiveryDocument d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            string problem = EncodeProblem(d);
            if (problem != null) throw new ArgumentException("Livery cannot be sent: " + problem, nameof(d));

            var sb = new StringBuilder(512 + 48 * d.Decals.Count);
            sb.Append('[').Append(Version).Append(',').Append(Q(d.Car)).Append(",[");
            for (int i = 0; i < AppearanceVocabulary.Families.Length; i++)
                sb.Append(i > 0 ? "," : "").Append(Q(d.Body.Get(AppearanceVocabulary.Families[i])));
            sb.Append("],[").Append(Q(d.Wheels.Rim)).Append(',').Append(d.Wheels.DiameterIn.ToString(Inv)).Append(',')
              .Append(d.Wheels.OffsetStep.ToString(Inv)).Append(',').Append(Q(d.Wheels.Finish));
            sb.Append("],[").Append(Q(Hex(d.Paint.Primary))).Append(',').Append(Q(Hex(d.Paint.Secondary))).Append(',').Append(Q(Hex(d.Paint.Accent)))
              .Append(',').Append(Q(d.Paint.Finish)).Append(',').Append(Q(d.Paint.TwoTone)).Append(',').Append(Q(d.Paint.Swatch ?? ""));
            sb.Append("],[").Append(Q(d.Lamps.Head)).Append(',').Append(Q(d.Lamps.Tail)).Append(',').Append(Q(d.Glass));
            sb.Append("],[").Append(Q(d.Plate.Text)).Append(',').Append(Q(d.Plate.Style)).Append("],[");
            for (int i = 0; i < d.Decals.Count; i++)
            {
                DecalLayer x = d.Decals[i];
                int flags = (x.Mirror ? FlagMirror : 0) | (x.Flip ? FlagFlip : 0);
                sb.Append(i > 0 ? ",[" : "[").Append(Q(x.Shape)).Append(',').Append(Q(Hex(x.Color))).Append(',').Append(Q(x.Zone)).Append(',')
                  .Append(x.UMilli.ToString(Inv)).Append(',').Append(x.VMilli.ToString(Inv)).Append(',').Append(x.ScaleCenti.ToString(Inv)).Append(',')
                  .Append(x.RotationDeg.ToString(Inv)).Append(',').Append(x.OpacityPercent.ToString(Inv)).Append(',').Append(flags.ToString(Inv)).Append(']');
            }
            sb.Append("]]");
            string wire = sb.ToString();
            if (Encoding.UTF8.GetByteCount(wire) > MaxWireBytes) throw new InvalidOperationException($"Encoded livery exceeds {MaxWireBytes} bytes");
            return wire;
        }

        /// <summary>Why a document cannot be encoded (null = encodable). Guarantees the size bound by construction.</summary>
        public static string EncodeProblem(LiveryDocument d)
        {
            if (d.Body == null || d.Wheels == null || d.Paint == null || d.Lamps == null || d.Plate == null || d.Decals == null) return "missing section";
            if (!CatalogueIds.IsValid(d.Car)) return "car id";
            foreach (string family in AppearanceVocabulary.Families)
                if (!AppearanceVocabulary.IsVariant(family, d.Body.Get(family))) return "body." + family;
            if (!CatalogueIds.IsValid(d.Wheels.Rim) || !CatalogueIds.IsValid(d.Wheels.Finish)) return "wheel ids";
            if (d.Wheels.DiameterIn < WheelFitment.MinRimInches || d.Wheels.DiameterIn > WheelFitment.MaxRimInches) return "wheels.diameterIn";
            if (d.Wheels.OffsetStep < -9 || d.Wheels.OffsetStep > 9) return "wheels.offsetStep";
            if (!HexColor.TryNormalize(d.Paint.Primary, out _) || !HexColor.TryNormalize(d.Paint.Secondary, out _) || !HexColor.TryNormalize(d.Paint.Accent, out _)) return "paint colors";
            if (!AppearanceVocabulary.PaintFinishes.Contains(d.Paint.Finish ?? "") || !AppearanceVocabulary.TwoToneStyles.Contains(d.Paint.TwoTone ?? "")) return "paint finish/two-tone";
            if (!string.IsNullOrEmpty(d.Paint.Swatch) && !CatalogueIds.IsValid(d.Paint.Swatch)) return "paint.swatch";
            if (!CatalogueIds.IsValid(d.Lamps.Head) || !CatalogueIds.IsValid(d.Lamps.Tail) || !CatalogueIds.IsValid(d.Glass)) return "lamp/glass ids";
            if (!PlateText.IsValid(d.Plate.Text, LiveryLimits.MaxPlateLength, allowEmpty: true) || !CatalogueIds.IsValid(d.Plate.Style)) return "plate";
            if (d.Decals.Count > LiveryLimits.MaxDecalLayers) return "more than " + LiveryLimits.MaxDecalLayers + " decal layers";
            for (int i = 0; i < d.Decals.Count; i++)
            {
                DecalLayer x = d.Decals[i];
                bool ok = x != null && CatalogueIds.IsValid(x.Shape) && HexColor.TryNormalize(x.Color, out _) && AppearanceVocabulary.DecalZones.Contains(x.Zone ?? "") &&
                          x.UMilli >= 0 && x.UMilli <= LiveryLimits.UvScale && x.VMilli >= 0 && x.VMilli <= LiveryLimits.UvScale &&
                          x.ScaleCenti >= LiveryLimits.MinScaleCenti && x.ScaleCenti <= LiveryLimits.MaxScaleCenti &&
                          x.RotationDeg >= 0 && x.RotationDeg <= LiveryLimits.MaxRotationDeg &&
                          x.OpacityPercent >= LiveryLimits.MinOpacityPercent && x.OpacityPercent <= LiveryLimits.MaxOpacityPercent;
                if (!ok) return "decals[" + i.ToString(Inv) + "]";
            }
            return null;
        }

        /// <summary>Strict decode with the byte bound checked first. Returns the exact first-level problems.</summary>
        public static LiveryWireResult Decode(string wire)
        {
            var r = new LiveryWireResult();
            List<string> e = r.Errors;
            if (string.IsNullOrEmpty(wire)) { e.Add("wire: empty"); return r; }
            if (wire.Length > MaxWireBytes || Encoding.UTF8.GetByteCount(wire) > MaxWireBytes) { e.Add($"wire: larger than {MaxWireBytes} bytes"); return r; }
            JArray a;
            try
            {
                using (var text = new StringReader(wire))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 4, SupportMultipleContent = true })
                {
                    a = JToken.ReadFrom(reader) as JArray;
                    if (reader.Read()) { e.Add("wire: trailing content"); return r; }
                }
            }
            catch (JsonException) { e.Add("wire: not valid JSON"); return r; }
            if (a == null || a.Count != 8) { e.Add("wire: expected an 8-element array"); return r; }
            if (a[0].Type != JTokenType.Integer || (long)a[0] != Version) { e.Add($"wire: unsupported version {a[0]}"); return r; }

            var d = new LiveryDocument { Car = Id(a[1], "car", e) };
            JArray body = Arr(a[2], "body", AppearanceVocabulary.Families.Length, e);
            if (body != null)
                for (int i = 0; i < AppearanceVocabulary.Families.Length; i++)
                {
                    string family = AppearanceVocabulary.Families[i];
                    string v = Str(body[i], "body." + family, e);
                    if (v != null && !AppearanceVocabulary.IsVariant(family, v)) e.Add($"body.{family}: unknown variant '{v}'");
                    d.Body.Set(family, v ?? "");
                }
            JArray wheels = Arr(a[3], "wheels", 4, e);
            if (wheels != null)
            {
                d.Wheels.Rim = Id(wheels[0], "wheels.rim", e);
                d.Wheels.DiameterIn = Int(wheels[1], "wheels.diameterIn", WheelFitment.MinRimInches, WheelFitment.MaxRimInches, e);
                d.Wheels.OffsetStep = Int(wheels[2], "wheels.offsetStep", -9, 9, e);
                d.Wheels.Finish = Id(wheels[3], "wheels.finish", e);
            }
            JArray paint = Arr(a[4], "paint", 6, e);
            if (paint != null)
            {
                d.Paint.Primary = Color(paint[0], "paint.primary", e);
                d.Paint.Secondary = Color(paint[1], "paint.secondary", e);
                d.Paint.Accent = Color(paint[2], "paint.accent", e);
                d.Paint.Finish = OneOf(paint[3], "paint.finish", AppearanceVocabulary.PaintFinishes, e);
                d.Paint.TwoTone = OneOf(paint[4], "paint.twoTone", AppearanceVocabulary.TwoToneStyles, e);
                string swatch = Str(paint[5], "paint.swatch", e) ?? "";
                if (swatch.Length > 0 && !CatalogueIds.IsValid(swatch)) e.Add("paint.swatch: invalid id");
                d.Paint.Swatch = swatch;
            }
            JArray lamps = Arr(a[5], "lamps", 3, e);
            if (lamps != null)
            {
                d.Lamps.Head = Id(lamps[0], "lamps.head", e);
                d.Lamps.Tail = Id(lamps[1], "lamps.tail", e);
                d.Glass = Id(lamps[2], "glass", e);
            }
            JArray plate = Arr(a[6], "plate", 2, e);
            if (plate != null)
            {
                string text = Str(plate[0], "plate.text", e) ?? "";
                if (!PlateText.IsValid(text, LiveryLimits.MaxPlateLength, allowEmpty: true)) e.Add("plate.text: invalid");
                d.Plate.Text = text;
                d.Plate.Style = Id(plate[1], "plate.style", e);
            }
            if (!(a[7] is JArray decals)) e.Add("decals: expected an array");
            else if (decals.Count > LiveryLimits.MaxDecalLayers) e.Add($"decals: {decals.Count} layers exceed the {LiveryLimits.MaxDecalLayers}-layer limit");
            else
                for (int i = 0; i < decals.Count; i++)
                {
                    string at = "decals[" + i.ToString(Inv) + "]";
                    JArray x = Arr(decals[i], at, 9, e);
                    if (x == null) continue;
                    int flags = Int(x[8], at + ".flags", 0, FlagMirror | FlagFlip, e);
                    d.Decals.Add(new DecalLayer
                    {
                        Shape = Id(x[0], at + ".shape", e),
                        Color = Color(x[1], at + ".color", e),
                        Zone = OneOf(x[2], at + ".zone", AppearanceVocabulary.DecalZones, e),
                        UMilli = Int(x[3], at + ".u", 0, LiveryLimits.UvScale, e),
                        VMilli = Int(x[4], at + ".v", 0, LiveryLimits.UvScale, e),
                        ScaleCenti = Int(x[5], at + ".scale", LiveryLimits.MinScaleCenti, LiveryLimits.MaxScaleCenti, e),
                        RotationDeg = Int(x[6], at + ".rotationDeg", 0, LiveryLimits.MaxRotationDeg, e),
                        OpacityPercent = Int(x[7], at + ".opacity", LiveryLimits.MinOpacityPercent, LiveryLimits.MaxOpacityPercent, e),
                        Mirror = (flags & FlagMirror) != 0,
                        Flip = (flags & FlagFlip) != 0,
                    });
                }
            if (e.Count == 0) r.Document = d;
            return r;
        }

        static string Q(string s) => JsonConvert.ToString(s ?? "");

        static string Hex(string color) => HexColor.TryNormalize(color, out string c) ? c.Substring(1) : "";

        static JArray Arr(JToken t, string path, int count, List<string> e)
        {
            if (t is JArray a && a.Count == count) return a;
            e.Add($"{path}: expected {count} elements");
            return null;
        }

        static string Str(JToken t, string path, List<string> e)
        {
            if (t != null && t.Type == JTokenType.String) return (string)t;
            e.Add($"{path}: expected a string");
            return null;
        }

        static string Id(JToken t, string path, List<string> e)
        {
            string s = Str(t, path, e);
            if (s == null) return "";
            if (!CatalogueIds.IsValid(s)) e.Add($"{path}: invalid id");
            return s;
        }

        static string OneOf(JToken t, string path, string[] allowed, List<string> e)
        {
            string s = Str(t, path, e);
            if (s == null) return "";
            if (!allowed.Contains(s)) e.Add($"{path}: unknown value '{s}'");
            return s;
        }

        static string Color(JToken t, string path, List<string> e)
        {
            string s = Str(t, path, e);
            if (s == null) return "";
            if (!HexColor.TryNormalize("#" + s, out string c)) { e.Add($"{path}: expected RRGGBB"); return ""; }
            return c;
        }

        static int Int(JToken t, string path, int min, int max, List<string> e)
        {
            if (t != null && t.Type == JTokenType.Integer && ((JValue)t).Value is long l && l >= min && l <= max) return (int)l;
            e.Add($"{path}: expected a whole number {min}..{max}");
            return 0;
        }
    }

    /// <summary>
    /// Network-safe cadence for livery refreshes at a meet (spec §9: a safe revisioned refresh on meaningful actions, never
    /// every slider movement). Offer only APPLIED liveries. An unchanged hash is never re-sent; a change within
    /// <see cref="MinIntervalSeconds"/> of the last send is held (the newest wins) until <see cref="Poll"/> releases it.
    /// Pure: the caller supplies the clock.
    /// </summary>
    public sealed class LiveryPublishGate
    {
        public const double MinIntervalSeconds = 2.0;

        DateTime lastSentUtc = DateTime.MinValue;
        string pendingHash;

        /// <summary>Increments with every published change (the livery revision peers display/compare).</summary>
        public long Revision { get; private set; }
        public string PublishedHash { get; private set; } = "";
        public bool HasPending => pendingHash != null;

        /// <summary>True when <paramref name="liveryHash"/> must be sent now (Revision and PublishedHash advance).</summary>
        public bool Offer(string liveryHash, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(liveryHash)) throw new ArgumentException("A livery hash is required", nameof(liveryHash));
            if (liveryHash == PublishedHash) { pendingHash = null; return false; }
            if (Due(nowUtc)) return Send(liveryHash, nowUtc);
            pendingHash = liveryHash;
            return false;
        }

        /// <summary>Releases a held change once the interval has passed; true when it must be sent now.</summary>
        public bool Poll(DateTime nowUtc)
        {
            if (pendingHash == null || !Due(nowUtc)) return false;
            string h = pendingHash;
            pendingHash = null;
            return Send(h, nowUtc);
        }

        bool Due(DateTime nowUtc) => lastSentUtc == DateTime.MinValue || (nowUtc - lastSentUtc).TotalSeconds >= MinIntervalSeconds;

        bool Send(string hash, DateTime nowUtc)
        {
            PublishedHash = hash;
            Revision++;
            lastSentUtc = nowUtc;
            pendingHash = null;
            return true;
        }
    }
}
