using NightSignal.ControlPlane.Content;
using NightSignal.Core.Content;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>Loaders for the proposed authored files team.trials.json and music.unlocks.json (validated on load).</summary>
public sealed class ContentManifestTests
{
    static ContentCatalogue C => TestData.Content.Catalogue;
    static TeamTrialCatalog Trials => TeamTrialCatalog.Fixture(C);

    static string Trial(string course = "C03", string kind = "mean", string format = "circuit", string ally = "R01", string opp = "R09") => $$"""
        {"schema":"night-signal/team-trials@1","trials":[{"id":"TT_X","name":"X","kind":"{{kind}}","course":"{{course}}","format":"{{format}}",
          "hardTimeoutMs":480000,"participationEnvelopeMs":345000,"difficulties":[{"id":"standard","label":"Standard",
          "allyPool":["{{ally}}","R02","R03","R04","R06"],"opponentPool":["{{opp}}","R10","R11","R12","R13","R14"]}]}]}
        """;

    [Fact]
    public void Fixture_HasTheThreeTrials_OnNonExclusiveCourses()
    {
        Assert.Equal(new[] { "TT_MEAN", "TT_BEST", "TT_DRIFT" }, Trials.Trials.Select(t => t.Id));
        Assert.True(Trials.IsFixture);
        Assert.All(Trials.Trials, t => Assert.True(t.Provisional));
    }

    [Fact]
    public void TrialFile_ParsesAndValidates()
    {
        Assert.Single(TeamTrialCatalog.Parse(Trial(), C).Trials);
        Assert.Throws<ContentLoadException>(() => TeamTrialCatalog.Parse(Trial(ally: "R40"), C));          // finale-only as friendly AI
        Assert.Throws<ContentLoadException>(() => TeamTrialCatalog.Parse(Trial(opp: "R48"), C));           // finale-only as opponent
        Assert.Throws<ContentLoadException>(() => TeamTrialCatalog.Parse(Trial(course: "C25", format: "sprint"), C)); // finale route
        Assert.Throws<ContentLoadException>(() => TeamTrialCatalog.Parse(Trial(format: "sprint"), C));     // C03 is a circuit
        Assert.Throws<ContentLoadException>(() => TeamTrialCatalog.Parse(Trial(kind: "drift"), C));        // drift needs drift-attack
        Assert.Throws<ContentLoadException>(() => TeamTrialCatalog.Parse(Trial(opp: "R01"), C));           // on both teams
        Assert.Throws<ContentLoadException>(() => TeamTrialCatalog.Parse(Trial().Replace("team-trials@1", "team-trials@0"), C));
    }

    static string Music(string cue) => "{\"schema\":\"night-signal/music-unlocks@1\",\"cues\":[" + cue + "]}";

    [Fact]
    public void MusicManifest_AcceptsTheFiveSourceKinds_AndRejectsMysteryReferences()
    {
        MusicUnlockManifest m = MusicUnlockManifest.Parse(Music("""
            {"cueId":"title","source":{"kind":"baseline"}},
            {"cueId":"r1","source":{"kind":"stage-first-normal-clear","stageId":"S05"}},
            {"cueId":"f-hard","source":{"kind":"stage-first-hard-clear","stageId":"S30"}},
            {"cueId":"lt1","source":{"kind":"lieutenant-first-defeat","stageId":"S07"}},
            {"cueId":"tt1","source":{"kind":"trial-first-victory","trialId":"TT_MEAN"}}
            """), C, Trials);
        Assert.Equal(new[] { "title" }, m.BaselineCues);
        Assert.Equal(new[] { "r1" }, m.ForStageClear("S05", NightSignal.Core.Rules.CampaignMode.Normal).Select(c => c.CueId));
        Assert.Empty(m.ForStageClear("S05", NightSignal.Core.Rules.CampaignMode.Hard));
        Assert.Equal(new[] { "f-hard" }, m.ForStageClear("S30", NightSignal.Core.Rules.CampaignMode.Hard).Select(c => c.CueId));
        Assert.Equal(new[] { "tt1" }, m.ForTrialVictory("TT_MEAN").Select(c => c.CueId));

        Assert.Throws<ContentLoadException>(() => MusicUnlockManifest.Parse(Music("""{"cueId":"x","source":{"kind":"hear-it"}}"""), C, Trials));
        Assert.Throws<ContentLoadException>(() => MusicUnlockManifest.Parse(Music("""{"cueId":"x","source":{"kind":"lieutenant-first-defeat","stageId":"S05"}}"""), C, Trials));
        Assert.Throws<ContentLoadException>(() => MusicUnlockManifest.Parse(Music("""{"cueId":"x","source":{"kind":"stage-first-normal-clear","stageId":"S99"}}"""), C, Trials));
        Assert.Throws<ContentLoadException>(() => MusicUnlockManifest.Parse(Music("""{"cueId":"x","source":{"kind":"trial-first-victory","trialId":"TT_NONE"}}"""), C, Trials));
        Assert.Throws<ContentLoadException>(() => MusicUnlockManifest.Parse(Music("""
            {"cueId":"x","source":{"kind":"baseline"}},{"cueId":"x","source":{"kind":"baseline"}}
            """), C, Trials)); // one declared acquisition rule per cue
    }
}
