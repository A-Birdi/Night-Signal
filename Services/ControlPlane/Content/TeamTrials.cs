using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Convoys;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Content;

/// <summary>A published difficulty choice: which rivals fill the friendly side and the opposing side (no hidden catch-up).</summary>
public sealed class TeamTrialDifficulty
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Friendly AI in priority order; the first 6 − H are used. Never R40/R48.</summary>
    public List<string> AllyPool { get; set; } = new();
    /// <summary>Opposing AI; the first six are used. Never R40/R48; disjoint from the ally pool.</summary>
    public List<string> OpponentPool { get; set; } = new();
}

/// <summary>
/// One authored Team Trial (Addendum 01 §3, D06). Proposed file: <c>Assets/Content/Data/authored/team.trials.json</c>
/// with schema <c>night-signal/team-trials@1</c> — see docs/NETWORKING.md §2.4 for the field list.
/// </summary>
public sealed class TeamTrialDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>mean | best | drift</summary>
    public string Kind { get; set; } = "";
    public string Course { get; set; } = "";
    /// <summary>Race format on the course: sprint | circuit (mean/best), drift-attack (drift).</summary>
    public string Format { get; set; } = "";
    /// <summary>Published hard timeout; a DNF/DQ contributes this plus 30 s to the MEAN.</summary>
    public long HardTimeoutMs { get; set; }
    /// <summary>BEST: a human must finish within this for human completion pay (no money for an AFK human's AI win).</summary>
    public long ParticipationEnvelopeMs { get; set; }
    public int CarCapPi { get; set; } = PerformanceIndex.Max;
    public string Assists { get; set; } = "standard";
    public string TiePolicy { get; set; } = "tie-is-not-victory";
    /// <summary>Declared bounded modifier: team victory pays as this placement (Core Economy), otherwise the defeat placement.</summary>
    public int VictoryPlacement { get; set; } = 1;
    public int DefeatPlacement { get; set; } = 4;
    public bool Provisional { get; set; }
    public List<TeamTrialDifficulty> Difficulties { get; set; } = new();

    public TeamTrialKind CoreKind => Kind switch
    {
        "mean" => TeamTrialKind.Mean,
        "best" => TeamTrialKind.Best,
        _ => TeamTrialKind.Drift,
    };

    /// <summary>MEAN/BEST: lower wins; DRIFT: higher wins.</summary>
    public bool LowerIsBetter => Kind != "drift";

    public TeamTrialDifficulty? Difficulty(string? id) => id is null ? Difficulties.FirstOrDefault() : Difficulties.FirstOrDefault(d => d.Id == id);
}

public sealed class TeamTrialFile
{
    public string? Schema { get; set; }
    public List<TeamTrialDef> Trials { get; set; } = new();
}

/// <summary>
/// The Team Trial definitions the server allocates and settles against. Loaded from <c>content/authored/team.trials.json</c>
/// when the coordinator has authored it; until then a PROVISIONAL in-code fixture (clearly flagged) is used so the server
/// path is exercised. Thresholds are not calibrated; the file is not yet part of Core's ContentHash (recommendation in
/// the report: add it to <c>ContentCatalogue.AuthoredFiles</c>).
/// </summary>
public sealed partial class TeamTrialCatalog
{
    public const string Schema = "night-signal/team-trials@1";
    public const string FileName = "team.trials.json";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    TeamTrialCatalog(IReadOnlyList<TeamTrialDef> trials, bool fixture)
    {
        Trials = trials;
        IsFixture = fixture;
    }

    public IReadOnlyList<TeamTrialDef> Trials { get; }
    public bool IsFixture { get; }

    public TeamTrialDef? Find(string? id) => id is null ? null : Trials.FirstOrDefault(t => t.Id == id);

    /// <summary>DI entry point: authored file when present, otherwise the provisional fixture.</summary>
    public static TeamTrialCatalog FromContentDirectory(IOptions<ContentOptions> options, ContentService content, ILogger<TeamTrialCatalog> log)
    {
        string path = Path.Combine(PathResolver.Resolve(AppContext.BaseDirectory, options.Value.Directory), "authored", FileName);
        if (File.Exists(path)) return Parse(File.ReadAllText(path), content.Catalogue);
        log.LogWarning("authored/{File} not found: using the PROVISIONAL built-in Team Trial fixture (uncalibrated)", FileName);
        return Fixture(content.Catalogue);
    }

    public static TeamTrialCatalog Parse(string json, ContentCatalogue catalogue)
    {
        TeamTrialFile file = JsonSerializer.Deserialize<TeamTrialFile>(json, Json) ?? throw new ContentLoadException($"{FileName} is empty");
        if (file.Schema != Schema) throw new ContentLoadException($"{FileName}: expected schema {Schema}, found {file.Schema ?? "none"}");
        Validate(file.Trials, catalogue);
        return new TeamTrialCatalog(file.Trials, fixture: false);
    }

    /// <summary>Provisional server fixture on non-exclusive starter courses (every member owns them; no guest passes needed).</summary>
    public static TeamTrialCatalog Fixture(ContentCatalogue catalogue)
    {
        static List<TeamTrialDifficulty> Difficulties() => new()
        {
            new() { Id = "standard", Label = "Standard", AllyPool = new() { "R01", "R02", "R03", "R04", "R06", "R07" }, OpponentPool = new() { "R09", "R10", "R11", "R12", "R13", "R14" } },
            new() { Id = "expert", Label = "Expert", AllyPool = new() { "R17", "R18", "R19", "R20", "R21", "R22" }, OpponentPool = new() { "R33", "R34", "R35", "R36", "R37", "R38" } },
        };
        var trials = new List<TeamTrialDef>
        {
            new() { Id = "TT_MEAN", Name = "Team Trial — Mean Time", Kind = "mean", Course = "C03", Format = "circuit",
                HardTimeoutMs = 480_000, ParticipationEnvelopeMs = 345_000, Provisional = true, Difficulties = Difficulties() },
            new() { Id = "TT_BEST", Name = "Team Trial — Best Time", Kind = "best", Course = "C01", Format = "sprint",
                HardTimeoutMs = 390_000, ParticipationEnvelopeMs = 270_000, Provisional = true, Difficulties = Difficulties() },
            // C01 carries three judged drift zones (C02 has none, so a drift trial there could never score).
            new() { Id = "TT_DRIFT", Name = "Team Trial — Combined Drift", Kind = "drift", Course = "C01", Format = "drift-attack",
                HardTimeoutMs = 420_000, ParticipationEnvelopeMs = 322_500, Provisional = true, Difficulties = Difficulties() },
        };
        Validate(trials, catalogue);
        return new TeamTrialCatalog(trials, fixture: true);
    }

    static void Validate(IReadOnlyList<TeamTrialDef> trials, ContentCatalogue c)
    {
        if (trials.Count == 0) throw new ContentLoadException($"{FileName} defines no trials");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (TeamTrialDef t in trials)
        {
            string where = $"Team Trial {t.Id}";
            if (!IdPattern().IsMatch(t.Id) || !ids.Add(t.Id)) throw new ContentLoadException($"{where}: missing, malformed or duplicate id");
            if (t.Kind is not ("mean" or "best" or "drift")) throw new ContentLoadException($"{where}: kind must be mean, best or drift");
            if (!c.TryCourse(t.Course, out CourseDef course) || course.Kind != "regular")
                throw new ContentLoadException($"{where}: course must be an existing non-exclusive regular course");
            bool formatOk = t.Kind == "drift" ? t.Format == "drift-attack" : t.Format is "sprint" or "circuit";
            if (!formatOk || !FreeplayRules.Supports(course, t.Format))
                throw new ContentLoadException($"{where}: format {t.Format} does not suit a {t.Kind} trial on {t.Course}");
            if (t.HardTimeoutMs <= 0 || t.ParticipationEnvelopeMs <= 0 || t.ParticipationEnvelopeMs > t.HardTimeoutMs)
                throw new ContentLoadException($"{where}: needs 0 < participationEnvelopeMs ≤ hardTimeoutMs");
            if (t.CarCapPi < PerformanceIndex.Min || t.CarCapPi > PerformanceIndex.Max) throw new ContentLoadException($"{where}: car cap outside the PI range");
            if (t.VictoryPlacement < 1 || t.DefeatPlacement > Limits.MaxRaceVehicles || t.VictoryPlacement > t.DefeatPlacement)
                throw new ContentLoadException($"{where}: victory/defeat placements must satisfy 1 ≤ victory ≤ defeat ≤ {Limits.MaxRaceVehicles}");
            if (t.Difficulties.Count == 0 || t.Difficulties.Select(d => d.Id).Distinct().Count() != t.Difficulties.Count)
                throw new ContentLoadException($"{where}: needs uniquely named difficulties");
            foreach (TeamTrialDifficulty d in t.Difficulties)
            {
                if (d.AllyPool.Distinct().Count() < Limits.TeamTrialSideSize - 1)
                    throw new ContentLoadException($"{where}/{d.Id}: ally pool needs {Limits.TeamTrialSideSize - 1} distinct rivals");
                if (d.OpponentPool.Distinct().Count() < Limits.TeamTrialSideSize)
                    throw new ContentLoadException($"{where}/{d.Id}: opponent pool needs {Limits.TeamTrialSideSize} distinct rivals");
                if (d.AllyPool.Intersect(d.OpponentPool).Any()) throw new ContentLoadException($"{where}/{d.Id}: a rival cannot be on both teams");
                foreach (string id in d.AllyPool.Concat(d.OpponentPool))
                    if (!c.TryRival(id, out _)) throw new ContentLoadException($"{where}/{d.Id}: unknown rival {id}");
                foreach (string id in d.AllyPool)
                    if (!FinalRivals.Allowed(id, AiPlacementContext.FriendlyAi)) throw new ContentLoadException($"{where}/{d.Id}: {id} cannot be friendly AI");
                foreach (string id in d.OpponentPool)
                    if (!FinalRivals.Allowed(id, AiPlacementContext.TeamTrial)) throw new ContentLoadException($"{where}/{d.Id}: {id} cannot race in a Team Trial");
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]{2,32}$")]
    private static partial Regex IdPattern();
}
