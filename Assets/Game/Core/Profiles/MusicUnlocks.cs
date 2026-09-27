using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Profiles
{
    /// <summary>The single declared acquisition rule of a collectible soundtrack cue (Addendum 01 §11.3).</summary>
    public enum MusicUnlockSourceKind
    {
        /// <summary>Title/menu/garage/meet/tutorial/results cues held from the initial profile state.</summary>
        Baseline = 0,
        StageFirstNormalClear = 1,
        StageFirstHardClear = 2,
        /// <summary>Designated on the lieutenant's Normal campaign encounter.</summary>
        LieutenantFirstDefeat = 3,
        TrialFirstVictory = 4,
    }

    public sealed class MusicUnlockRule
    {
        public MusicUnlockRule(string cueId, MusicUnlockSourceKind kind, string stageId = null, string trialId = null)
        {
            CueId = cueId;
            Kind = kind;
            StageId = stageId;
            TrialId = trialId;
        }

        public string CueId { get; }
        public MusicUnlockSourceKind Kind { get; }
        public string StageId { get; }
        public string TrialId { get; }
        public string SourceRef => StageId ?? TrialId ?? "";

        /// <summary>Manifest token for a kind (kebab-case, shared with the control plane's music-unlocks@1 file).</summary>
        public static string Token(MusicUnlockSourceKind kind)
        {
            switch (kind)
            {
                case MusicUnlockSourceKind.Baseline: return "baseline";
                case MusicUnlockSourceKind.StageFirstNormalClear: return "stage-first-normal-clear";
                case MusicUnlockSourceKind.StageFirstHardClear: return "stage-first-hard-clear";
                case MusicUnlockSourceKind.LieutenantFirstDefeat: return "lieutenant-first-defeat";
                case MusicUnlockSourceKind.TrialFirstVictory: return "trial-first-victory";
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public static bool TryParseToken(string token, out MusicUnlockSourceKind kind)
        {
            foreach (MusicUnlockSourceKind k in (MusicUnlockSourceKind[])Enum.GetValues(typeof(MusicUnlockSourceKind)))
                if (Token(k) == token)
                {
                    kind = k;
                    return true;
                }
            kind = MusicUnlockSourceKind.Baseline;
            return false;
        }
    }

    /// <summary>
    /// The soundtrack-unlock hook used by progression settlement. Cues are granted ONLY from the eligible result named by
    /// their rule, once per profile: never from hearing a cue, spectating, a boombox, viewing a cutscene, losing, a guest
    /// pass or a course purchase. OST collection adds no mastery RP.
    /// </summary>
    public interface IMusicUnlockSource
    {
        IReadOnlyList<MusicUnlockRule> Rules { get; }
        IReadOnlyList<MusicUnlockRule> Baseline();
        /// <summary>Cues whose source is an eligible clear of <paramref name="stageId"/> in <paramref name="mode"/> (Normal also returns lieutenant-defeat cues).</summary>
        IReadOnlyList<MusicUnlockRule> ForStageClear(string stageId, CampaignMode mode);
        IReadOnlyList<MusicUnlockRule> ForTrialVictory(string trialId);
    }

    /// <summary>
    /// Engine-free music-unlock table, validated against the content catalogue. Reads the same document the control plane
    /// uses (<c>authored/music.unlocks.json</c>, schema <c>night-signal/music-unlocks@1</c>):
    /// <c>{"schema":…,"cues":[{"cueId":"MUS_RACE_KASUMI","source":{"kind":"stage-first-normal-clear","stageId":"S08"}}]}</c>.
    /// </summary>
    public sealed class MusicUnlockTable : IMusicUnlockSource
    {
        public const string Schema = "night-signal/music-unlocks@1";
        public const string FileName = "music.unlocks.json";

        static readonly Regex CueIdPattern = new Regex("^[A-Za-z0-9_.-]{2,48}$", RegexOptions.CultureInvariant);

        readonly List<MusicUnlockRule> rules;

        MusicUnlockTable(List<MusicUnlockRule> rules) => this.rules = rules;

        /// <summary>No cues: nothing can be granted (the state before the manifest is authored).</summary>
        public static MusicUnlockTable Empty { get; } = new MusicUnlockTable(new List<MusicUnlockRule>());

        public IReadOnlyList<MusicUnlockRule> Rules => rules;

        public IReadOnlyList<MusicUnlockRule> Baseline() => rules.Where(r => r.Kind == MusicUnlockSourceKind.Baseline).ToList();

        public IReadOnlyList<MusicUnlockRule> ForStageClear(string stageId, CampaignMode mode) =>
            rules.Where(r => r.StageId == stageId && (mode == CampaignMode.Hard
                ? r.Kind == MusicUnlockSourceKind.StageFirstHardClear
                : r.Kind == MusicUnlockSourceKind.StageFirstNormalClear || r.Kind == MusicUnlockSourceKind.LieutenantFirstDefeat)).ToList();

        public IReadOnlyList<MusicUnlockRule> ForTrialVictory(string trialId) =>
            rules.Where(r => r.Kind == MusicUnlockSourceKind.TrialFirstVictory && r.TrialId == trialId).ToList();

        /// <param name="knownTrialIds">Team Trial ids; when null, trial ids are only checked for shape.</param>
        public static MusicUnlockTable Create(IEnumerable<MusicUnlockRule> source, ContentCatalogue catalogue, IEnumerable<string> knownTrialIds = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));
            HashSet<string> trials = knownTrialIds == null ? null : new HashSet<string>(knownTrialIds, StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<MusicUnlockRule>();
            foreach (MusicUnlockRule r in source)
            {
                if (r == null || !CueIdPattern.IsMatch(r.CueId ?? "") || !ids.Add(r.CueId))
                    throw new ContentLoadException($"{FileName}: missing, malformed or duplicate cueId '{r?.CueId}'");
                string where = $"{FileName}: {r.CueId}";
                switch (r.Kind)
                {
                    case MusicUnlockSourceKind.Baseline:
                        if (r.StageId != null || r.TrialId != null) throw new ContentLoadException($"{where}: baseline cues name no source event");
                        break;
                    case MusicUnlockSourceKind.StageFirstNormalClear:
                    case MusicUnlockSourceKind.StageFirstHardClear:
                    case MusicUnlockSourceKind.LieutenantFirstDefeat:
                        StageDef stage = catalogue.Stages.FirstOrDefault(s => s.Id == r.StageId);
                        if (stage == null || r.TrialId != null) throw new ContentLoadException($"{where}: needs an existing stageId (and no trialId)");
                        if (r.Kind == MusicUnlockSourceKind.LieutenantFirstDefeat && stage.Type != "lieutenant")
                            throw new ContentLoadException($"{where}: {stage.Id} is not a lieutenant encounter");
                        break;
                    case MusicUnlockSourceKind.TrialFirstVictory:
                        if (string.IsNullOrEmpty(r.TrialId) || r.StageId != null || (trials != null && !trials.Contains(r.TrialId)))
                            throw new ContentLoadException($"{where}: needs an existing trialId (and no stageId)");
                        break;
                    default:
                        throw new ContentLoadException($"{where}: unknown source kind");
                }
                list.Add(r);
            }
            return new MusicUnlockTable(list);
        }

        /// <summary>The table carried by the content catalogue (hashed with it), or <see cref="Empty"/> when not authored.</summary>
        public static MusicUnlockTable FromCatalogue(ContentCatalogue catalogue, IEnumerable<string> knownTrialIds = null) =>
            catalogue.TryDocument(FileName, out string json) ? Parse(json, catalogue, knownTrialIds) : Empty;

        public static MusicUnlockTable Parse(string json, ContentCatalogue catalogue, IEnumerable<string> knownTrialIds = null)
        {
            JObject root;
            try
            {
                root = ProfileJson.ParseObject(json);
            }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is ArgumentException)
            {
                throw new ContentLoadException($"{FileName} is not valid JSON: {e.Message}");
            }
            string schema = (string)root["schema"];
            if (schema != Schema) throw new ContentLoadException($"{FileName}: expected schema {Schema}, found {schema ?? "none"}");
            var parsed = new List<MusicUnlockRule>();
            if (root["cues"] is JArray cues)
                foreach (JToken cue in cues)
                {
                    string cueId = (string)cue["cueId"];
                    JToken src = cue["source"];
                    if (src == null || src.Type != JTokenType.Object) throw new ContentLoadException($"{FileName}: {cueId}: no source");
                    string kindToken = (string)src["kind"];
                    if (!MusicUnlockRule.TryParseToken(kindToken, out MusicUnlockSourceKind kind))
                        throw new ContentLoadException($"{FileName}: {cueId}: unknown source kind '{kindToken}'");
                    parsed.Add(new MusicUnlockRule(cueId, kind, (string)src["stageId"], (string)src["trialId"]));
                }
            return Create(parsed, catalogue, knownTrialIds);
        }
    }
}
