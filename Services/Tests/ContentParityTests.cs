using NightSignal.Core.Content;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>The control plane must present the same ContentHash as the Unity game for the same checkout.</summary>
public sealed class ContentParityTests
{
    static string RepoDataRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "Assets", "Content", "Data");
            if (Directory.Exists(Path.Combine(candidate, "generated"))) return candidate;
        }
        throw new DirectoryNotFoundException("Assets/Content/Data not found above the test output folder.");
    }

    [Fact]
    public void ControlPlaneHash_EqualsTheHashOfTheUnityProjectDocuments()
    {
        // Same selection as Assets/Game/Runtime/Content/ContentFiles.ReadFromProject.
        string root = RepoDataRoot();
        var docs = new Dictionary<string, string>();
        foreach (string f in ContentCatalogue.RequiredFiles) docs[f] = File.ReadAllText(Path.Combine(root, "generated", f));
        foreach (string f in ContentCatalogue.OptionalFiles)
            if (File.Exists(Path.Combine(root, "authored", f))) docs[f] = File.ReadAllText(Path.Combine(root, "authored", f));

        ContentCatalogue project = ContentCatalogue.Load(docs);
        Assert.Equal(project.ContentHash, TestData.Content.ContentHash);
        Assert.Equal(project.CarTunings.Count, TestData.Content.Catalogue.CarTunings.Count);
    }

    [Fact]
    public void TrustedCatalogue_PassesCoreValidation() =>
        Assert.True(CatalogueValidator.Validate(TestData.Content.Catalogue).Passed);
}
