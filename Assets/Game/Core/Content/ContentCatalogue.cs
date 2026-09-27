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

        /// <summary>
        /// Authored overlays loaded from Assets/Content/Data/authored/ when present. Every loaded document is hashed: the
        /// performance parts and upgrade recipes joined once bought parts could change an online race car, so a client with
        /// different build data is refused at connect rather than at the start of a match.
        /// </summary>
        public static readonly string[] AuthoredFiles =
            { "cars.tuning.json", "stages.opposition.json", "courses.addendum.json", "music.unlocks.json", "parts.json", "build-recipes.json", "stage-benchmarks.json" };

        /// <summary>
        /// Authored overlays that must be present: they carry Addendum 01 rules (live opposition, 29 courses, course
        /// access), so loading the original catalogue alone cannot silently restore superseded behaviour.
        /// </summary>
        public static readonly string[] RequiredAuthoredFiles = { "stages.opposition.json", "courses.addendum.json" };

        public CourseAccessRules CourseAccess { get; private set; }

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

        /// <summary>How the certified benchmarks were produced ("" when none are loaded).</summary>
        public string BenchmarkMethod { get; private set; } = "";
        Dictionary<string, CertifiedBenchmark> certified = new Dictionary<string, CertifiedBenchmark>(StringComparer.Ordinal);

        /// <summary>The certified benchmark for a stage side, if the certification run has produced one.</summary>
        public bool TryCertifiedBenchmark(string stageId, NightSignal.Core.Rules.CampaignMode mode, out CertifiedBenchmark b) =>
            certified.TryGetValue((stageId ?? "") + "/" + (mode == NightSignal.Core.Rules.CampaignMode.Hard ? "hard" : "normal"), out b);

        /// <summary>
        /// Raw text of a loaded document that other Core systems parse themselves (e.g. music.unlocks.json for
        /// <c>MusicUnlockTable</c>). Every loaded document is covered by <see cref="ContentHash"/>.
        /// </summary>
        public bool TryDocument(string name, out string text) => documentText.TryGetValue(name ?? "", out text);

        Dictionary<string, string> documentText = new Dictionary<string, string>();

        /// <param name="documents">File name → JSON text for every entry in <see cref="RequiredFiles"/>.</param>
        public static ContentCatalogue Load(IReadOnlyDictionary<string, string> documents)
        {
            if (documents == null) throw new ArgumentNullException(nameof(documents));
            foreach (string f in RequiredFiles.Concat(RequiredAuthoredFiles))
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

            CoursesAddendumFile addendum = Parse<CoursesAddendumFile>("courses.addendum.json", "night-signal/courses-addendum@1");
            var cat = new ContentCatalogue
            {
                Courses = Parse<CoursesFile>("courses.json", "night-signal/courses@1").Courses.Concat(addendum.Courses).ToList(),
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
            cat.CourseAccess = addendum.Access;

            StageOppositionFile opposition = Parse<StageOppositionFile>("stages.opposition.json", "night-signal/stage-opposition@1");
            foreach (StageOppositionEntry entry in opposition.Stages)
            {
                if (!cat.stageById.TryGetValue(entry.Id ?? "", out StageDef stage))
                    throw new ContentLoadException($"stages.opposition.json references unknown stage {entry.Id}");
                stage.Normal.Opponents = entry.Normal;
                stage.Hard.Opponents = entry.Hard;
            }
            if (documents.ContainsKey("stage-benchmarks.json"))
            {
                StageBenchmarksFile file = Parse<StageBenchmarksFile>("stage-benchmarks.json", "night-signal/stage-benchmarks@1");
                foreach (CertifiedBenchmark b in file.Stages)
                {
                    if (!cat.stageById.ContainsKey(b.Stage ?? "")) throw new ContentLoadException($"stage-benchmarks.json references unknown stage {b.Stage}");
                    if (b.Mode != "normal" && b.Mode != "hard") throw new ContentLoadException($"stage-benchmarks.json: {b.Stage} mode must be normal or hard");
                    if (b.TargetMs <= 0 || b.ReferenceMs <= 0 || b.FeaturedRivalPace <= 0 || b.FeaturedRivalPace > 1.5)
                        throw new ContentLoadException($"stage-benchmarks.json: {b.Stage}/{b.Mode} has an invalid target, reference or rival pace");
                    string key = b.Stage + "/" + b.Mode;
                    if (cat.certified.ContainsKey(key)) throw new ContentLoadException($"stage-benchmarks.json: duplicate {key}");
                    cat.certified[key] = b;
                }
                cat.BenchmarkMethod = file.Method ?? "";
            }
            cat.documentText = documents.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
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
