using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Core.Story;

namespace NightSignal.CoreTests;

/// <summary>
/// The authored campaign story as the game presents it (spec §5.3): every stage has both sides' intro, reactions and diary;
/// speakers are real rivals or the three narrators; a rematch intro is the opening and closing lines; each outcome has its
/// reaction (the rival-beaten-but-benchmark-missed case uses the rival's generic line); the race diary holds exactly what
/// cleared stages unlock — stage entries, crew introductions, radio and timing-slip records.
/// </summary>
public sealed class StoryTextTests
{
    static string Story(string file) => File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", "story", file));
    static readonly Lazy<StoryText> story = new(() =>
        StoryText.Load(Story("stages.story.json"), Story("crews.diary.json"), Story("radio-records.json"), Story("rivals.story.json"), Story("endings.json")));
    static StoryText S => story.Value;
    static ContentCatalogue Cat => TestContent.Catalogue;

    [Fact]
    public void EveryStage_HasBothSides_WithIntroReactionsAndDiary_SpokenByKnownVoices()
    {
        var narrators = new HashSet<string> { "radio", "timing-crew", "narration" };
        Assert.Equal(30, Cat.Stages.Count);
        foreach (StageDef def in Cat.Stages)
        {
            Assert.True(S.TryStage(def.Id, out StageStory st), def.Id);
            foreach (CampaignMode mode in new[] { CampaignMode.Normal, CampaignMode.Hard })
            {
                StageStorySide side = st.Side(mode);
                Assert.False(string.IsNullOrEmpty(side.Title), $"{def.Id} {mode} title");
                Assert.NotEmpty(side.Intro);
                Assert.NotEmpty(side.Win);
                Assert.NotEmpty(side.Loss);
                Assert.NotEmpty(side.ClearedButLost);
                Assert.False(string.IsNullOrEmpty(side.Diary), $"{def.Id} {mode} diary");
                foreach (StoryLine l in side.Intro.Concat(side.Win).Concat(side.Loss).Concat(side.ClearedButLost))
                    Assert.True(narrators.Contains(l.Speaker) || Cat.TryRival(l.Speaker, out _), $"{def.Id} {mode}: unknown speaker {l.Speaker}");
            }
        }
        Assert.Equal(4, S.Acts.Count);
        Assert.Equal(6, S.Crews.Count);
        Assert.Equal(6, S.Records.Count);
    }

    [Fact]
    public void RematchIntro_KeepsTheOpeningAndClosingLines()
    {
        List<StoryLine> full = S.Intro("S01", CampaignMode.Normal, rematch: false);
        List<StoryLine> shortened = S.Intro("S01", CampaignMode.Normal, rematch: true);
        Assert.True(full.Count > StoryText.RematchLines);
        Assert.Equal(new[] { full[0].Line, full[^1].Line }, shortened.Select(l => l.Line));
        Assert.Empty(S.Intro("S99", CampaignMode.Normal, false));
    }

    [Fact]
    public void Reactions_ForEachOutcome_TheFourthFromTheRivalsOwnLines()
    {
        StageStorySide side = S.TryStage("S01", out StageStory st) ? st.Normal : null!;
        Assert.Equal(side.Win[0].Line, S.Reaction("S01", CampaignMode.Normal, StoryOutcome.Win, "R01")[0].Line);
        Assert.Equal(side.Loss[0].Line, S.Reaction("S01", CampaignMode.Normal, StoryOutcome.Loss, "R01")[0].Line);
        Assert.Equal(side.ClearedButLost[0].Line, S.Reaction("S01", CampaignMode.Normal, StoryOutcome.ClearedButLost, "R01")[0].Line);
        List<StoryLine> missed = S.Reaction("S01", CampaignMode.Normal, StoryOutcome.BeatRivalMissedBenchmark, "R01");
        Assert.Single(missed);
        Assert.Equal("R01", missed[0].Speaker);
        Assert.Equal(S.RivalLine("R01", "loss"), missed[0].Line);
        Assert.Equal("Well driven, Aki. The Night Riders wait.", StoryText.Fill("Well driven, {player}. The {convoy} wait.", "Aki", "Night Riders"));
        Assert.Equal("Well driven, you.", StoryText.Fill("Well driven, {player}.", "", ""));
    }

    [Fact]
    public void Diary_HoldsWhatClearedStagesUnlock()
    {
        List<string> order = Cat.Stages.OrderBy(s => s.Number).Select(s => s.Id).ToList();
        Assert.Empty(S.Diary(order, (_, _) => false));

        // S01 on Normal: its entry and the Tea Hour introduction.
        List<DiaryEntry> one = S.Diary(order, (id, m) => id == "S01" && m == CampaignMode.Normal);
        Assert.Equal(new[] { "stage:S01", "crew:tea-hour" }, one.Select(e => e.Kind + ":" + e.Id));

        // Through S06 on Normal and S01 on Hard: six stage entries, the Hard entry after them, one crew more, the first radio record.
        var normal = new HashSet<string>(order.Take(6));
        List<DiaryEntry> more = S.Diary(order, (id, m) => m == CampaignMode.Normal ? normal.Contains(id) : id == "S01");
        Assert.Equal(7, more.Count(e => e.Kind == "stage"));
        Assert.Equal(CampaignMode.Hard, more.Where(e => e.Kind == "stage").Last().Mode);
        Assert.Equal(new[] { "tea-hour", "rainline" }, more.Where(e => e.Kind == "crew").Select(e => e.Id));
        Assert.Equal(new[] { "RADIO-01" }, more.Where(e => e.Kind == "record").Select(e => e.Id));

        // Everything cleared: 60 stage entries, six crews, six records.
        List<DiaryEntry> all = S.Diary(order, (_, _) => true);
        Assert.Equal(60, all.Count(e => e.Kind == "stage"));
        Assert.Equal(6, all.Count(e => e.Kind == "crew"));
        Assert.Equal(6, all.Count(e => e.Kind == "record"));
    }

    [Fact]
    public void Pacing_EveryFullIntro_Fits10To18Seconds_RematchesAreShort()
    {
        foreach (StageDef def in Cat.Stages)
            foreach (CampaignMode mode in new[] { CampaignMode.Normal, CampaignMode.Hard })
            {
                List<StoryLine> full = S.Intro(def.Id, mode, false);
                float[] holds = StoryText.Holds(full, false);
                Assert.Equal(full.Count, holds.Length);
                Assert.All(holds, h => Assert.True(h >= StoryText.LineMinSeconds));
                float scene = holds.Sum();
                Assert.True(scene >= 10f - 0.01f && scene <= 18.5f, $"{def.Id} {mode}: {scene:F1} s");
                float rematch = StoryText.Holds(S.Intro(def.Id, mode, true), true).Sum();
                Assert.True(rematch < scene && rematch <= 10f, $"{def.Id} {mode} rematch: {rematch:F1} s");
            }
        Assert.Empty(StoryText.Holds(new List<StoryLine>(), false));
    }

    [Fact]
    public void Endings_AfterTheFinale_AndTheRadioBenchEpilogue()
    {
        var narrators = new HashSet<string> { "radio", "timing-crew", "narration" };
        Assert.Equal(3, S.NormalEnding.Count);
        Assert.Equal(3, S.HardEnding.Count);
        Assert.False(string.IsNullOrEmpty(S.PostGameNote));
        foreach (StoryScene sc in S.NormalEnding.Concat(S.HardEnding))
        {
            Assert.False(string.IsNullOrEmpty(sc.Setting));
            Assert.NotEmpty(sc.Lines);
            Assert.All(sc.Lines, l => Assert.True(narrators.Contains(l.Speaker) || Cat.TryRival(l.Speaker, out _), l.Speaker));
        }
        // Normal: the whole terrace ending after the S30 clear; Hard: the dawn-run finish, then the epilogue at the bench with Shiori.
        Assert.Equal(3, S.EndingAfterFinale(CampaignMode.Normal).Count);
        Assert.Single(S.EndingAfterFinale(CampaignMode.Hard));
        List<StoryScene> epilogue = S.Epilogue();
        Assert.Equal(2, epilogue.Count);
        Assert.All(epilogue, sc => Assert.Contains("radio bench", sc.Setting, StringComparison.OrdinalIgnoreCase));
        Assert.All(epilogue, sc => Assert.Contains(sc.Lines, l => l.Speaker == "R48"));
        Assert.Equal("S30", Cat.Stages.Single(s => s.Number == StoryText.FinaleStage).Id);
    }
}
