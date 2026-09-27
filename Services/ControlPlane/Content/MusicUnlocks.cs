using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Content;

public static class MusicSourceKinds
{
    public const string Baseline = "baseline";
    public const string StageFirstNormalClear = "stage-first-normal-clear";
    public const string StageFirstHardClear = "stage-first-hard-clear";
    public const string LieutenantFirstDefeat = "lieutenant-first-defeat";
    public const string TrialFirstVictory = "trial-first-victory";

    public static readonly string[] All = { Baseline, StageFirstNormalClear, StageFirstHardClear, LieutenantFirstDefeat, TrialFirstVictory };
}

public sealed class MusicCueSource
{
    public string Kind { get; set; } = "";
    public string? StageId { get; set; }
    public string? TrialId { get; set; }
}

public sealed class MusicCueUnlock
{
    public string CueId { get; set; } = "";
    public MusicCueSource Source { get; set; } = new();

    public string SourceRef => Source.StageId ?? Source.TrialId ?? "";
}

public sealed class MusicUnlockFile
{
    public string? Schema { get; set; }
    public List<MusicCueUnlock> Cues { get; set; } = new();
}

/// <summary>
/// OST acquisition manifest (Addendum 01 §11.3): every collectible cue has exactly one declared source event. The server
/// grants a cue only from an eligible settled result (never from hearing, spectating, a boombox or a course purchase),
/// idempotently per (account, cue). Loaded from <c>content/authored/music.unlocks.json</c> (schema
/// <c>night-signal/music-unlocks@1</c>); when the file is absent the manifest is empty and nothing is granted.
/// </summary>
public sealed partial class MusicUnlockManifest
{
    public const string Schema = "night-signal/music-unlocks@1";
    public const string FileName = "music.unlocks.json";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    MusicUnlockManifest(IReadOnlyList<MusicCueUnlock> cues) => Cues = cues;

    public static MusicUnlockManifest Empty { get; } = new(Array.Empty<MusicCueUnlock>());

    public IReadOnlyList<MusicCueUnlock> Cues { get; }

    /// <summary>Cues every online profile holds from the start (title/menu/garage/meet/tutorial/results).</summary>
    public IEnumerable<string> BaselineCues => Cues.Where(c => c.Source.Kind == MusicSourceKinds.Baseline).Select(c => c.CueId);

    public static MusicUnlockManifest FromContentDirectory(IOptions<ContentOptions> options, ContentService content, TeamTrialCatalog trials,
        ILogger<MusicUnlockManifest> log)
    {
        string path = Path.Combine(PathResolver.Resolve(AppContext.BaseDirectory, options.Value.Directory), "authored", FileName);
        if (File.Exists(path)) return Parse(File.ReadAllText(path), content.Catalogue, trials);
        log.LogWarning("authored/{File} not found: no soundtrack cues can be granted until it is authored", FileName);
        return Empty;
    }

    public static MusicUnlockManifest Parse(string json, ContentCatalogue catalogue, TeamTrialCatalog trials)
    {
        MusicUnlockFile file = JsonSerializer.Deserialize<MusicUnlockFile>(json, Json) ?? throw new ContentLoadException($"{FileName} is empty");
        if (file.Schema != Schema) throw new ContentLoadException($"{FileName}: expected schema {Schema}, found {file.Schema ?? "none"}");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (MusicCueUnlock cue in file.Cues)
        {
            if (!CueIdPattern().IsMatch(cue.CueId ?? "") || !ids.Add(cue.CueId!))
                throw new ContentLoadException($"{FileName}: missing, malformed or duplicate cueId '{cue.CueId}'");
            MusicCueSource s = cue.Source ?? throw new ContentLoadException($"{cue.CueId}: no source");
            string where = $"{FileName}: {cue.CueId}";
            switch (s.Kind)
            {
                case MusicSourceKinds.Baseline:
                    if (s.StageId is not null || s.TrialId is not null) throw new ContentLoadException($"{where}: baseline cues name no source event");
                    break;
                case MusicSourceKinds.StageFirstNormalClear:
                case MusicSourceKinds.StageFirstHardClear:
                case MusicSourceKinds.LieutenantFirstDefeat:
                    StageDef? stage = catalogue.Stages.FirstOrDefault(x => x.Id == s.StageId);
                    if (stage is null || s.TrialId is not null) throw new ContentLoadException($"{where}: needs an existing stageId (and no trialId)");
                    if (s.Kind == MusicSourceKinds.LieutenantFirstDefeat && stage.Type != "lieutenant")
                        throw new ContentLoadException($"{where}: {stage.Id} is not a lieutenant encounter");
                    break;
                case MusicSourceKinds.TrialFirstVictory:
                    if (trials.Find(s.TrialId) is null || s.StageId is not null) throw new ContentLoadException($"{where}: needs an existing trialId (and no stageId)");
                    break;
                default:
                    throw new ContentLoadException($"{where}: unknown source kind '{s.Kind}'");
            }
        }
        return new MusicUnlockManifest(file.Cues);
    }

    /// <summary>
    /// Cues whose declared source is an eligible clear of <paramref name="stageId"/> in <paramref name="mode"/>. Lieutenant
    /// cues are designated on the Normal campaign encounter.
    /// </summary>
    public IReadOnlyList<MusicCueUnlock> ForStageClear(string stageId, CampaignMode mode) =>
        Cues.Where(c => c.Source.StageId == stageId && (mode == CampaignMode.Hard
            ? c.Source.Kind == MusicSourceKinds.StageFirstHardClear
            : c.Source.Kind is MusicSourceKinds.StageFirstNormalClear or MusicSourceKinds.LieutenantFirstDefeat)).ToList();

    public IReadOnlyList<MusicCueUnlock> ForTrialVictory(string trialId) =>
        Cues.Where(c => c.Source.Kind == MusicSourceKinds.TrialFirstVictory && c.Source.TrialId == trialId).ToList();

    [GeneratedRegex("^[A-Za-z0-9_.-]{2,48}$")]
    private static partial Regex CueIdPattern();
}
