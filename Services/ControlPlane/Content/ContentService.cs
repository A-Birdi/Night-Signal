using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.ControlPlane.Content;

/// <summary>
/// The trusted catalogue: the same generated JSON the Unity game loads (copied from
/// Assets/Content/Data/generated at build time), loaded with Core's <see cref="ContentCatalogue"/> and validated
/// with Core's <see cref="CatalogueValidator"/>. Startup fails on any validation error.
/// </summary>
public sealed class ContentService
{
    public ContentService(IOptions<ContentOptions> options, ILogger<ContentService> log)
    {
        string root = PathResolver.Resolve(AppContext.BaseDirectory, options.Value.Directory);
        Catalogue = ContentCatalogue.Load(ReadDocuments(root));
        ValidationReport report = CatalogueValidator.Validate(Catalogue);
        if (!report.Passed)
            throw new InvalidOperationException("Content catalogue failed validation: " + string.Join("; ", report.Errors));
        log.LogInformation("Content catalogue loaded: {ContentHash} ({Courses} courses, {Stages} stages, {Warnings} warnings)",
            Catalogue.ContentHash, Catalogue.Courses.Count, Catalogue.Stages.Count, report.Issues.Count);
        // The race diary's crew introductions (presentation text, outside the hash): which stage opens each one (CH70).
        string crews = Path.Combine(root, "authored", "story", "crews.diary.json");
        Crews = File.Exists(crews) ? NightSignal.Core.Story.StoryText.Load(null, File.ReadAllText(crews)).Crews : new List<NightSignal.Core.Story.CrewIntroduction>();
        log.LogInformation("Race diary: {Crews} crew introductions", Crews.Count);
    }

    /// <summary>The race diary's crew introductions and the stage that opens each (empty when the document is missing).</summary>
    public IReadOnlyList<NightSignal.Core.Story.CrewIntroduction> Crews { get; }

    public ContentCatalogue Catalogue { get; }
    public string ContentHash => Catalogue.ContentHash;

    /// <summary>
    /// The exact document set the Unity game hashes (mirrors Assets/Game/Runtime/Content/ContentFiles.ReadFromProject):
    /// every Core RequiredFiles entry from generated/, plus each Core OptionalFiles entry from authored/ when present.
    /// ContentHash covers exactly these documents, so adding or omitting one changes the hash.
    /// </summary>
    public static Dictionary<string, string> ReadDocuments(string root)
    {
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in ContentCatalogue.RequiredFiles)
        {
            string path = Path.Combine(root, "generated", file);
            if (!File.Exists(path))
                throw new InvalidOperationException($"Content file missing: generated/{file} (looked in the configured content directory).");
            docs[file] = File.ReadAllText(path);
        }
        foreach (string file in ContentCatalogue.AuthoredFiles)
        {
            string path = Path.Combine(root, "authored", file);
            if (File.Exists(path)) docs[file] = File.ReadAllText(path);
        }
        return docs;
    }

    public static StageType ParseStageType(string type) => type switch
    {
        "regular" => StageType.Regular,
        "lieutenant" => StageType.Lieutenant,
        "penultimate" => StageType.Penultimate,
        "finale" => StageType.Finale,
        _ => throw new ContentLoadException($"Unknown stage type '{type}'"),
    };

    public static ChallengeTier ParseTier(string tier) => tier switch
    {
        "bronze" => ChallengeTier.Bronze,
        "silver" => ChallengeTier.Silver,
        "gold" => ChallengeTier.Gold,
        _ => throw new ContentLoadException($"Unknown challenge tier '{tier}'"),
    };

    public bool TryStage(string? id, out StageDef stage)
    {
        stage = Catalogue.Stages.FirstOrDefault(s => s.Id == id)!;
        return stage is not null;
    }

    /// <summary>
    /// Benchmark locked into a campaign proposal: certified (reference run × factor, from the content) when the stage side
    /// has one, otherwise PROVISIONAL from the course's ExpectedSeconds and labelled as such everywhere it is shown or settled.
    /// </summary>
    public BenchmarkInfo BenchmarkFor(StageDef stage, CampaignMode mode) =>
        // Shared with the Local campaign (Core) so online and offline judge a stage by the same numbers.
        new(StageBenchmarks.For(Catalogue, stage, mode), Provisional: !StageBenchmarks.IsCertified(Catalogue, stage, mode),
            Source: StageBenchmarks.Source(Catalogue, stage, mode));

    /// <summary>Freeplay time-trial reference (for the 1.20 "reference beaten" band); provisional like the above.</summary>
    public long FreeplayReferenceMs(string courseId) => Catalogue.Course(courseId).ExpectedSeconds * 1000L;
}

public sealed record BenchmarkInfo(StageBenchmark Benchmark, bool Provisional, string Source);
