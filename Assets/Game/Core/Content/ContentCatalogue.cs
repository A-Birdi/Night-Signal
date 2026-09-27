using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace NightSignal.Core.Content
{
    /// <summary>
    /// The trusted content catalogue. Built from named JSON documents so the Unity client, the Unity server and
    /// the .NET control plane load identical data; <see cref="ContentHash"/> identifies the exact version.
    /// </summary>
    public sealed class ContentCatalogue
    {
        public static readonly string[] RequiredFiles =
            { "courses.json", "cars.json", "crews.json", "rivals.json", "stages.json", "challenges.json", "cosmetics.json" };

        /// <summary>Authored overlays merged by ID when present (Assets/Content/Data/authored/).</summary>
        public static readonly string[] OptionalFiles = { "cars.tuning.json" };

        public IReadOnlyDictionary<string, CarTuningDef> CarTunings { get; private set; } = new Dictionary<string, CarTuningDef>();

        public IReadOnlyList<CourseDef> Courses { get; private set; }
        public IReadOnlyList<CarDef> Cars { get; private set; }
        public IReadOnlyList<CrewDef> Crews { get; private set; }
        public IReadOnlyList<TendencyDef> Tendencies { get; private set; }
        public IReadOnlyList<RivalDef> Rivals { get; private set; }
        public IReadOnlyList<StageDef> Stages { get; private set; }
        public IReadOnlyList<ChallengeDef> Challenges { get; private set; }
        public IReadOnlyList<CosmeticDef> Cosmetics { get; private set; }
        /// <summary>SHA-256 over the loaded documents (name + LF-normalized text, ordinal name order).</summary>
        public string ContentHash { get; private set; }

        Dictionary<string, CourseDef> courseById;
        Dictionary<string, CarDef> carById;
        Dictionary<string, RivalDef> rivalById;
        Dictionary<string, StageDef> stageById;
        Dictionary<string, ChallengeDef> challengeById;
        Dictionary<string, CosmeticDef> cosmeticById;

        public CourseDef Course(string id) => Lookup(courseById, id, "course");
        public CarDef Car(string id) => Lookup(carById, id, "car");
        public RivalDef Rival(string id) => Lookup(rivalById, id, "rival");
        public StageDef Stage(string id) => Lookup(stageById, id, "stage");
        public ChallengeDef Challenge(string id) => Lookup(challengeById, id, "challenge");
        public CosmeticDef Cosmetic(string id) => Lookup(cosmeticById, id, "cosmetic");
        public bool TryCourse(string id, out CourseDef c) => courseById.TryGetValue(id ?? "", out c);
        public bool TryCar(string id, out CarDef c) => carById.TryGetValue(id ?? "", out c);
        public bool TryRival(string id, out RivalDef r) => rivalById.TryGetValue(id ?? "", out r);
        public bool TryCosmetic(string id, out CosmeticDef c) => cosmeticById.TryGetValue(id ?? "", out c);

        /// <param name="documents">File name → JSON text for every entry in <see cref="RequiredFiles"/>.</param>
        public static ContentCatalogue Load(IReadOnlyDictionary<string, string> documents)
        {
            if (documents == null) throw new ArgumentNullException(nameof(documents));
            foreach (string f in RequiredFiles)
                if (!documents.ContainsKey(f))
                    throw new ContentLoadException($"Missing content document {f}");

            var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Ignore };
            T Parse<T>(string name, string schema) where T : class
            {
                string text = documents[name];
                T value = JsonConvert.DeserializeObject<T>(text, settings);
                if (value == null) throw new ContentLoadException($"{name} is empty");
                string found = (string)typeof(T).GetField("Schema").GetValue(value);
                if (found != schema) throw new ContentLoadException($"{name}: expected schema {schema}, found {found ?? "none"}");
                return value;
            }

            var cat = new ContentCatalogue
            {
                Courses = Parse<CoursesFile>("courses.json", "night-signal/courses@1").Courses,
                Cars = Parse<CarsFile>("cars.json", "night-signal/cars@1").Cars,
                Rivals = Parse<RivalsFile>("rivals.json", "night-signal/rivals@1").Rivals,
                Stages = Parse<StagesFile>("stages.json", "night-signal/stages@1").Stages,
                Challenges = Parse<ChallengesFile>("challenges.json", "night-signal/challenges@1").Challenges,
                Cosmetics = Parse<CosmeticsFile>("cosmetics.json", "night-signal/cosmetics@1").Cosmetics,
            };
            CrewsFile crews = Parse<CrewsFile>("crews.json", "night-signal/crews@1");
            cat.Crews = crews.Crews;
            cat.Tendencies = crews.Tendencies;

            cat.courseById = Index(cat.Courses, c => c.Id, "course");
            cat.carById = Index(cat.Cars, c => c.Id, "car");
            cat.rivalById = Index(cat.Rivals, r => r.Id, "rival");
            cat.stageById = Index(cat.Stages, s => s.Id, "stage");
            cat.challengeById = Index(cat.Challenges, c => c.Id, "challenge");
            cat.cosmeticById = Index(cat.Cosmetics, c => c.Id, "cosmetic");

            if (documents.ContainsKey("cars.tuning.json"))
            {
                CarTuningFile tuning = Parse<CarTuningFile>("cars.tuning.json", "night-signal/car-tuning@1");
                var map = Index(tuning.Cars, t => t.Id, "car tuning");
                foreach (string id in map.Keys)
                    if (!cat.carById.ContainsKey(id))
                        throw new ContentLoadException($"cars.tuning.json references unknown car {id}");
                cat.CarTunings = map;
            }
            cat.ContentHash = Hash(documents);
            return cat;
        }

        public static string Hash(IReadOnlyDictionary<string, string> documents)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (string name in documents.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    sb.Append(name).Append('\n');
                    sb.Append(documents[name].Replace("\r\n", "\n")).Append('\n');
                }
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return string.Concat(digest.Select(b => b.ToString("x2")));
            }
        }

        static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key, string kind)
        {
            var map = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (T item in items)
            {
                string k = key(item);
                if (string.IsNullOrEmpty(k)) throw new ContentLoadException($"A {kind} has no ID");
                if (map.ContainsKey(k)) throw new ContentLoadException($"Duplicate {kind} ID {k}");
                map.Add(k, item);
            }
            return map;
        }

        static T Lookup<T>(Dictionary<string, T> map, string id, string kind)
        {
            if (id != null && map.TryGetValue(id, out T value)) return value;
            throw new KeyNotFoundException($"Unknown {kind} ID '{id}'");
        }
    }

    public sealed class ContentLoadException : Exception
    {
        public ContentLoadException(string message) : base(message) { }
    }
}
