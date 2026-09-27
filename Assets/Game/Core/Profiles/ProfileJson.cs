using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace NightSignal.Core.Profiles
{
    /// <summary>
    /// The one JSON contract for Local profile documents: camelCase members, camelCase enum strings, ISO-8601 UTC dates,
    /// dictionary keys and opaque embedded documents preserved verbatim (no date re-parsing inside opaque data), no type
    /// metadata (never TypeNameHandling). Used by the save codec and for deep copies.
    /// </summary>
    public static class ProfileJson
    {
        public static readonly JsonSerializerSettings Settings = Create(Formatting.Indented);
        static readonly JsonSerializerSettings Compact = Create(Formatting.None);

        static JsonSerializerSettings Create(Formatting formatting)
        {
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = false },
                },
                DateParseHandling = DateParseHandling.None,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                FloatParseHandling = FloatParseHandling.Double,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Include,
                ObjectCreationHandling = ObjectCreationHandling.Replace,
                TypeNameHandling = TypeNameHandling.None,
                MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                MaxDepth = 128,
                Formatting = formatting,
            };
            settings.Converters.Add(new StringEnumConverter(new CamelCaseNamingStrategy(), allowIntegerValues: false));
            return settings;
        }

        public static string Serialize(object value, bool indented = true) => JsonConvert.SerializeObject(value, indented ? Settings : Compact);

        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        public static T ToObject<T>(JToken token) => token.ToObject<T>(JsonSerializer.Create(Settings));

        public static JObject FromObject(object value) => JObject.FromObject(value, JsonSerializer.Create(Settings));

        /// <summary>Deep copy through the persisted contract (what you get back is what a save/load would give you).</summary>
        public static T Clone<T>(T value) where T : class => value == null ? null : Deserialize<T>(Serialize(value, indented: false));

        /// <summary>Parses a JSON object without converting date-like strings (opaque data stays byte-for-byte meaningful).</summary>
        public static JObject ParseObject(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json ?? "")))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                reader.MaxDepth = 128;
                JToken token = JToken.ReadFrom(reader);
                if (!(token is JObject obj)) throw new JsonReaderException("Expected a JSON object.");
                // Reject trailing content after the object.
                if (reader.Read() && reader.TokenType != JsonToken.Comment) throw new JsonReaderException("Unexpected content after the JSON object.");
                return obj;
            }
        }
    }
}
