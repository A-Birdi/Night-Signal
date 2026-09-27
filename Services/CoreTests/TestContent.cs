using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

/// <summary>Loads the real catalogue exactly like the Unity EditMode tests (generated/ + authored/ overlays).</summary>
internal static class TestContent
{
    static readonly Lazy<string> root = new(FindRepoRoot);
    static readonly Lazy<ContentCatalogue> catalogue = new(() => ContentCatalogue.Load(LoadDocuments()));

    public static string RepoRoot => root.Value;
    public static ContentCatalogue Catalogue => catalogue.Value;

    public static Dictionary<string, string> LoadDocuments()
    {
        string generated = Path.Combine(RepoRoot, "Assets", "Content", "Data", "generated");
        string authored = Path.Combine(RepoRoot, "Assets", "Content", "Data", "authored");
        var docs = ContentCatalogue.RequiredFiles.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(generated, f)));
        foreach (string f in ContentCatalogue.AuthoredFiles)
        {
            string path = Path.Combine(authored, f);
            if (File.Exists(path)) docs[f] = File.ReadAllText(path);
        }
        return docs;
    }

    static string FindRepoRoot()
    {
        for (DirectoryInfo dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Assets", "Content", "Data", "generated", "courses.json")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (Assets/Content/Data/generated) not found above the test binaries");
    }

    /// <summary>
    /// A test soundtrack manifest in the control plane's music-unlocks@1 shape (the authored file does not exist yet).
    /// Cue ids match Assets/Content/Data/authored/music.cues.json.
    /// </summary>
    public const string MusicManifestJson = """
    {
      "schema": "night-signal/music-unlocks@1",
      "cues": [
        { "cueId": "MUS_TITLE", "source": { "kind": "baseline" } },
        { "cueId": "MUS_MENU_A", "source": { "kind": "baseline" } },
        { "cueId": "MUS_GARAGE", "source": { "kind": "baseline" } },
        { "cueId": "MUS_RACE_MIZUHANA", "source": { "kind": "stage-first-normal-clear", "stageId": "S01" } },
        { "cueId": "MUS_RACE_KASUMI", "source": { "kind": "stage-first-normal-clear", "stageId": "S05" } },
        { "cueId": "MUS_LT_DAIGO", "source": { "kind": "lieutenant-first-defeat", "stageId": "S07" } },
        { "cueId": "MUS_FINAL_REINA", "source": { "kind": "stage-first-normal-clear", "stageId": "S30" } },
        { "cueId": "MUS_FINAL_SHIORI", "source": { "kind": "stage-first-hard-clear", "stageId": "S30" } },
        { "cueId": "MUS_TT_MEAN", "source": { "kind": "trial-first-victory", "trialId": "TT_MEAN" } }
      ]
    }
    """;

    public static readonly string[] TrialIds = { "TT_MEAN", "TT_BEST", "TT_DRIFT" };

    static readonly Lazy<MusicUnlockTable> music = new(() => MusicUnlockTable.Parse(MusicManifestJson, Catalogue, TrialIds));
    public static MusicUnlockTable Music => music.Value;

    public static readonly DateTime T0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    public static readonly RecordRuleset Rules = new()
    {
        CourseVariant = "forward", CourseRevision = "r1", PhysicsVersion = "phys-3", ScoringVersion = "score-2",
        Contact = ContactPolicy.LightContact, Conditions = "night-dry", AssistCategory = "standard",
    };
}

/// <summary>Deterministic id source for tests.</summary>
internal sealed class SequenceIds(int start = 0) : ILocalIdSource
{
    int next = start;
    public string NewId(string prefix) => $"{prefix}_{++next:x16}";
}
