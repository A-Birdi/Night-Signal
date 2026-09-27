using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NightSignal.Core.Builds;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Customization
{
    /// <summary>Result of <see cref="LiveryJson.Parse"/>: a document only when the payload is structurally sound.</summary>
    public sealed class LiveryParseResult
    {
        public LiveryDocument Document;
        /// <summary>Exact structural problems, in document order (semantic checks are <see cref="LiveryValidator"/>'s job).</summary>
        public List<string> Errors = new List<string>();
        public bool Ok => Document != null && Errors.Count == 0;
    }

    /// <summary>
    /// Canonical JSON for a livery (fixed key order, no whitespace, quantised numbers in shortest decimal form) and a strict
    /// reader: unknown, missing, duplicate or wrongly typed fields are reported exactly; numbers are snapped to the livery grid.
    /// </summary>
    public static class LiveryJson
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static readonly string[] RootFields = { "schema", "car", "body", "wheels", "paint", "lamps", "glass", "plate", "decals" };
        static readonly string[] BodyFields = AppearanceVocabulary.Families;
        static readonly string[] WheelFields = { "rim", "diameterIn", "offsetStep", "finish" };
        static readonly string[] PaintFields = { "primary", "secondary", "accent", "finish", "twoTone", "swatch" };
        static readonly string[] LampFields = { "head", "tail" };
        static readonly string[] PlateFields = { "text", "style" };
        static readonly string[] DecalFields = { "shape", "color", "zone", "u", "v", "scale", "rotationDeg", "opacity", "mirror", "flip" };

        /// <summary>The canonical text the <see cref="LiveryHash"/> is computed over. Deterministic on every runtime.</summary>
        public static string ToCanonicalJson(LiveryDocument d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            BodySelection b = d.Body ?? new BodySelection();
            WheelSelection w = d.Wheels ?? new WheelSelection();
            PaintSelection p = d.Paint ?? new PaintSelection();
            LampSelection l = d.Lamps ?? new LampSelection();
            PlateSelection pl = d.Plate ?? new PlateSelection();
            List<DecalLayer> decals = d.Decals ?? new List<DecalLayer>();

            var sb = new StringBuilder(384 + 150 * decals.Count);
            sb.Append("{\"schema\":").Append(Str(LiveryDocument.SchemaId));
            sb.Append(",\"car\":").Append(Str(d.Car));
            sb.Append(",\"body\":{\"front\":").Append(Str(b.Front)).Append(",\"rear\":").Append(Str(b.Rear))
              .Append(",\"side\":").Append(Str(b.Side)).Append(",\"rearAero\":").Append(Str(b.RearAero))
              .Append(",\"exhaust\":").Append(Str(b.Exhaust)).Append('}');
            sb.Append(",\"wheels\":{\"rim\":").Append(Str(w.Rim)).Append(",\"diameterIn\":").Append(Int(w.DiameterIn))
              .Append(",\"offsetStep\":").Append(Int(w.OffsetStep)).Append(",\"finish\":").Append(Str(w.Finish)).Append('}');
            sb.Append(",\"paint\":{\"primary\":").Append(Str(Canon(p.Primary))).Append(",\"secondary\":").Append(Str(Canon(p.Secondary)))
              .Append(",\"accent\":").Append(Str(Canon(p.Accent))).Append(",\"finish\":").Append(Str(p.Finish))
              .Append(",\"twoTone\":").Append(Str(p.TwoTone)).Append(",\"swatch\":").Append(Str(p.Swatch)).Append('}');
            sb.Append(",\"lamps\":{\"head\":").Append(Str(l.Head)).Append(",\"tail\":").Append(Str(l.Tail)).Append('}');
            sb.Append(",\"glass\":").Append(Str(d.Glass));
            sb.Append(",\"plate\":{\"text\":").Append(Str(pl.Text)).Append(",\"style\":").Append(Str(pl.Style)).Append('}');
            sb.Append(",\"decals\":[");
            for (int i = 0; i < decals.Count; i++)
            {
                DecalLayer x = decals[i] ?? new DecalLayer();
                if (i > 0) sb.Append(',');
                sb.Append("{\"shape\":").Append(Str(x.Shape)).Append(",\"color\":").Append(Str(Canon(x.Color))).Append(",\"zone\":").Append(Str(x.Zone))
                  .Append(",\"u\":").Append(Fixed(x.UMilli, LiveryLimits.UvScale, 3)).Append(",\"v\":").Append(Fixed(x.VMilli, LiveryLimits.UvScale, 3))
                  .Append(",\"scale\":").Append(Fixed(x.ScaleCenti, 100, 2)).Append(",\"rotationDeg\":").Append(Int(x.RotationDeg))
                  .Append(",\"opacity\":").Append(Fixed(x.OpacityPercent, 100, 2))
                  .Append(",\"mirror\":").Append(x.Mirror ? "true" : "false").Append(",\"flip\":").Append(x.Flip ? "true" : "false").Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string Str(string s) => JsonConvert.ToString(s ?? "");

        static string Int(int v) => v.ToString(Inv);

        /// <summary>value / scale in the shortest decimal form ("0.5", "1", "0.125"); exact, no floating point.</summary>
        internal static string Fixed(int value, int scale, int decimals)
        {
            long v = Math.Abs((long)value);
            long whole = v / scale, frac = v % scale;
            string s = whole.ToString(Inv);
            if (frac != 0) s += "." + frac.ToString(Inv).PadLeft(decimals, '0').TrimEnd('0');
            return value < 0 ? "-" + s : s;
        }

        /// <summary>Strict structural parse of a stored livery payload (bounded to <see cref="LiveryLimits.MaxPayloadChars"/>).</summary>
        public static LiveryParseResult Parse(string json)
        {
            var result = new LiveryParseResult();
            List<string> errors = result.Errors;
            if (string.IsNullOrWhiteSpace(json)) { errors.Add("livery: empty payload"); return result; }
            if (json.Length > LiveryLimits.MaxPayloadChars) { errors.Add($"livery: payload exceeds {LiveryLimits.MaxPayloadChars} characters"); return result; }

            JToken root;
            try
            {
                using (var text = new StringReader(json))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 8, SupportMultipleContent = true })
                {
                    root = JToken.ReadFrom(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Ignore,
                        LineInfoHandling = LineInfoHandling.Ignore,
                    });
                    while (reader.Read())
                        if (reader.TokenType != JsonToken.Comment) { errors.Add("livery: trailing content after the document"); return result; }
                }
            }
            catch (JsonException)
            {
                errors.Add("livery: not valid JSON");
                return result;
            }
            if (!(root is JObject o)) { errors.Add("livery: expected a JSON object"); return result; }

            var r = new FieldReader(errors);
            var doc = new LiveryDocument();
            r.Fields(o, "livery", RootFields);
            string schema = r.String(o, "schema", "schema");
            if (schema != null && schema != LiveryDocument.SchemaId) errors.Add($"schema: expected {LiveryDocument.SchemaId}, found {schema}");
            doc.Car = r.String(o, "car", "car");

            JObject body = r.Object(o, "body", "body");
            if (body != null)
            {
                r.Fields(body, "body", BodyFields);
                foreach (string family in AppearanceVocabulary.Families) doc.Body.Set(family, r.String(body, family, "body." + family));
            }
            JObject wheels = r.Object(o, "wheels", "wheels");
            if (wheels != null)
            {
                r.Fields(wheels, "wheels", WheelFields);
                doc.Wheels.Rim = r.String(wheels, "rim", "wheels.rim");
                doc.Wheels.DiameterIn = r.Int(wheels, "diameterIn", "wheels.diameterIn");
                doc.Wheels.OffsetStep = r.Int(wheels, "offsetStep", "wheels.offsetStep");
                doc.Wheels.Finish = r.String(wheels, "finish", "wheels.finish");
            }
            JObject paint = r.Object(o, "paint", "paint");
            if (paint != null)
            {
                r.Fields(paint, "paint", PaintFields);
                doc.Paint.Primary = r.String(paint, "primary", "paint.primary");
                doc.Paint.Secondary = r.String(paint, "secondary", "paint.secondary");
                doc.Paint.Accent = r.String(paint, "accent", "paint.accent");
                doc.Paint.Finish = r.String(paint, "finish", "paint.finish");
                doc.Paint.TwoTone = r.String(paint, "twoTone", "paint.twoTone");
                doc.Paint.Swatch = r.String(paint, "swatch", "paint.swatch");
            }
            JObject lamps = r.Object(o, "lamps", "lamps");
            if (lamps != null)
            {
                r.Fields(lamps, "lamps", LampFields);
                doc.Lamps.Head = r.String(lamps, "head", "lamps.head");
                doc.Lamps.Tail = r.String(lamps, "tail", "lamps.tail");
            }
            doc.Glass = r.String(o, "glass", "glass");
            JObject plate = r.Object(o, "plate", "plate");
            if (plate != null)
            {
                r.Fields(plate, "plate", PlateFields);
                doc.Plate.Text = r.String(plate, "text", "plate.text");
                doc.Plate.Style = r.String(plate, "style", "plate.style");
            }
            JArray decals = r.Array(o, "decals", "decals");
            if (decals != null)
            {
                for (int i = 0; i < decals.Count; i++)
                {
                    string at = "decals[" + i.ToString(Inv) + "]";
                    if (!(decals[i] is JObject x)) { errors.Add($"{at}: expected an object"); continue; }
                    r.Fields(x, at, DecalFields);
                    doc.Decals.Add(new DecalLayer
                    {
                        Shape = r.String(x, "shape", at + ".shape"),
                        Color = r.String(x, "color", at + ".color"),
                        Zone = r.String(x, "zone", at + ".zone"),
                        UMilli = r.Fixed(x, "u", at + ".u", LiveryLimits.UvScale),
                        VMilli = r.Fixed(x, "v", at + ".v", LiveryLimits.UvScale),
                        ScaleCenti = r.Fixed(x, "scale", at + ".scale", 100),
                        RotationDeg = r.Int(x, "rotationDeg", at + ".rotationDeg"),
                        OpacityPercent = r.Fixed(x, "opacity", at + ".opacity", 100),
                        Mirror = r.Bool(x, "mirror", at + ".mirror"),
                        Flip = r.Bool(x, "flip", at + ".flip"),
                    });
                }
            }
            if (errors.Count == 0)
            {
                // Colours are accepted in either case and stored canonically (upper case).
                doc.Paint.Primary = Canon(doc.Paint.Primary);
                doc.Paint.Secondary = Canon(doc.Paint.Secondary);
                doc.Paint.Accent = Canon(doc.Paint.Accent);
                foreach (DecalLayer x in doc.Decals) x.Color = Canon(x.Color);
                result.Document = doc;
            }
            return result;
        }

        /// <summary>Valid colours are written upper case, so "#ff0000" and "#FF0000" are the same livery (same hash).</summary>
        static string Canon(string color) => HexColor.TryNormalize(color, out string c) ? c : color;

        /// <summary>Typed field access that records exact errors; missing fields are reported once by <see cref="Fields"/>.</summary>
        sealed class FieldReader
        {
            readonly List<string> errors;

            public FieldReader(List<string> errors) => this.errors = errors;

            /// <summary>Unknown fields (document order), then missing ones (canonical order).</summary>
            public void Fields(JObject o, string path, string[] expected)
            {
                foreach (JProperty p in o.Properties())
                    if (System.Array.IndexOf(expected, p.Name) < 0) errors.Add($"{path}: unknown field '{p.Name}'");
                foreach (string name in expected)
                    if (o.Property(name) == null) errors.Add($"{path}: missing field '{name}'");
            }

            JToken Get(JObject o, string name) => o.Property(name)?.Value;

            public string String(JObject o, string name, string path)
            {
                JToken t = Get(o, name);
                if (t == null) return "";
                if (t.Type != JTokenType.String) { errors.Add($"{path}: expected a string"); return ""; }
                return (string)t;
            }

            public int Int(JObject o, string name, string path)
            {
                JToken t = Get(o, name);
                if (t == null) return 0;
                if (t.Type == JTokenType.Integer && ((JValue)t).Value is long l && l >= int.MinValue && l <= int.MaxValue) return (int)l;
                errors.Add($"{path}: expected a whole number");
                return 0;
            }

            /// <summary>A finite number snapped to 1/<paramref name="scale"/> (half away from zero).</summary>
            public int Fixed(JObject o, string name, string path, int scale)
            {
                JToken t = Get(o, name);
                if (t == null) return 0;
                if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                {
                    double v;
                    try { v = t.Value<double>(); }
                    catch (Exception) { v = double.NaN; }
                    int q = DecalLayer.Quantize(v, scale);
                    if (q != int.MinValue) return q;
                }
                errors.Add($"{path}: expected a finite number");
                return 0;
            }

            public bool Bool(JObject o, string name, string path)
            {
                JToken t = Get(o, name);
                if (t == null) return false;
                if (t.Type != JTokenType.Boolean) { errors.Add($"{path}: expected true or false"); return false; }
                return (bool)t;
            }

            public JObject Object(JObject o, string name, string path)
            {
                JToken t = Get(o, name);
                if (t == null) return null;
                if (t is JObject obj) return obj;
                errors.Add($"{path}: expected an object");
                return null;
            }

            public JArray Array(JObject o, string name, string path)
            {
                JToken t = Get(o, name);
                if (t == null) return null;
                if (t is JArray arr) return arr;
                errors.Add($"{path}: expected an array");
                return null;
            }
        }
    }

    /// <summary>SHA-256 (lower-case hex) of the canonical JSON: the livery revision identity shared by every client.</summary>
    public static class LiveryHash
    {
        public static string Of(LiveryDocument document) => BuildHashing.Sha256Hex(LiveryJson.ToCanonicalJson(document));
    }

    /// <summary>
    /// Car presets: a livery stored in one of the ≥ 5 visual preset slots of a car instance (Core/Builds
    /// <see cref="VisualPreset"/>, Addendum 02 §9.1). Mechanical operations never read or write these.
    /// </summary>
    public static class LiveryPresets
    {
        /// <summary>
        /// Saves <paramref name="document"/> as a named visual preset. The document must pass Preview validation for the
        /// workspace's car (locked items may be kept as a plan; applying still requires ownership).
        /// </summary>
        public static OperationResult Save(CarBuildWorkspace ws, long expectedRevision, string name, LiveryDocument document,
            CustomizationCatalogue catalogue, DateTime nowUtc)
        {
            if (ws == null) throw new ArgumentNullException(nameof(ws));
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            LiveryValidation v = LiveryValidator.Validate(document, catalogue, ws.Car.ModelId, null, LiveryValidationMode.Preview);
            if (!v.IsValid)
                return new OperationResult { Status = OpStatus.Rejected, Revision = ws.Revision, Message = "Livery is not valid for this car: " + string.Join("; ", v.Errors) };
            return GarageOperations.SaveVisualPreset(ws, expectedRevision, name, LiveryDocument.SchemaId, LiveryJson.ToCanonicalJson(document), nowUtc);
        }

        /// <summary>Reads a preset's livery; a payload of another schema is reported, never guessed at.</summary>
        public static LiveryParseResult Read(VisualPreset preset)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            if (preset.PayloadSchema != LiveryDocument.SchemaId)
            {
                var r = new LiveryParseResult();
                r.Errors.Add($"preset: payload schema is '{preset.PayloadSchema}', not {LiveryDocument.SchemaId}");
                return r;
            }
            return LiveryJson.Parse(preset.PayloadJson);
        }
    }
}
