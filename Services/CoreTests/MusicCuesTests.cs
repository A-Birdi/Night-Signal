using NightSignal.Core.Meet;
using NightSignal.Core.Profiles;

namespace NightSignal.CoreTests;

/// <summary>
/// The soundtrack manifest the meet's shared boombox runs from (authored/music.cues.json, read by <see cref="MusicCues"/>
/// on the room server and the clients) and its agreement with the unlock rules (authored/music.unlocks.json, Addendum 01
/// §11.3 "one declared acquisition rule per collectible cue"). Uses the real authored files.
/// </summary>
public sealed class MusicCuesTests
{
    static string Authored(string file) =>
        File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", file));

    [Fact]
    public void AuthoredManifest_EveryCueHasATitleACategoryAndALength()
    {
        MusicCues cues = MusicCues.Parse(Authored("music.cues.json"));
        Assert.Equal(24, cues.All.Count());
        foreach (MusicCues.Cue c in cues.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title), c.Id);
            Assert.NotEqual(c.Id, c.Title);
            Assert.False(string.IsNullOrWhiteSpace(c.Category), c.Id);
            Assert.InRange(c.Seconds, 30.0, 600.0);
            Assert.Equal(c.Seconds, cues.Seconds(c.Id));
        }
    }

    [Fact]
    public void AuthoredUnlockRules_NameEveryCueInTheManifest_ExactlyOnce()
    {
        MusicCues cues = MusicCues.Parse(Authored("music.cues.json"));
        MusicUnlockTable table = MusicUnlockTable.Parse(Authored("music.unlocks.json"), TestContent.Catalogue, TestContent.TrialIds);
        foreach (MusicUnlockRule r in table.Rules)
            Assert.True(cues.Has(r.CueId), r.CueId + " is granted but not in music.cues.json");
        foreach (MusicCues.Cue c in cues.All)
            Assert.Equal(1, table.Rules.Count(r => r.CueId == c.Id));
        Assert.NotEmpty(table.Baseline());
    }

    [Fact]
    public void Parse_PrefersTheDisplayTitle_AndAnUnknownCueHasNoLength()
    {
        MusicCues cues = MusicCues.Parse("""
        { "cues": [
          { "id": "A", "title": "Plain", "displayTitle": "Shown", "category": "menu", "loopEndSeconds": 12.5 },
          { "id": "B", "title": "Only title" },
          { "id": "C" },
          { "title": "no id is skipped" }
        ] }
        """);
        Assert.Equal(3, cues.All.Count());
        Assert.Equal("Shown", cues.Title("A"));
        Assert.Equal("menu", cues.All.Single(c => c.Id == "A").Category);
        Assert.Equal(12.5, cues.Seconds("A"));
        Assert.Equal("Only title", cues.Title("B"));
        Assert.Equal(0.0, cues.Seconds("B"));
        Assert.Equal("C", cues.Title("C"));

        Assert.False(cues.Has("NOPE"));
        Assert.False(cues.Has(null!));
        Assert.Equal(0.0, cues.Seconds("NOPE"));
        Assert.Equal(0.0, cues.Seconds(null!));
        Assert.Equal("NOPE", cues.Title("NOPE"));
        Assert.Empty(MusicCues.Parse("").All);
        Assert.Empty(MusicCues.Parse("{}").All);
    }
}
