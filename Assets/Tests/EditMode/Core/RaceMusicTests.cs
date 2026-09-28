using System.IO;
using System.Linq;
using NightSignal.Core.Content;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    /// <summary>
    /// Race music selection (spec §16, docs/AUDIO.md contexts): encounter themes only by campaign stage (a course can
    /// never select a final theme), Team Trial themes by kind, every other race its course region's arrangement, and
    /// every chosen cue exists in the cue manifest.
    /// </summary>
    public sealed class RaceMusicTests
    {
        static ContentCatalogue catalogue;
        static ContentCatalogue Catalogue => catalogue ?? (catalogue = ContentCatalogue.Load(AddendumRulesTests.LoadDocuments()));

        [Test]
        public void EncounterThemes_FollowTheStage_NotTheCourse()
        {
            ContentCatalogue c = Catalogue;
            Assert.That(RaceMusic.CueFor(c, "C04", "campaign", "S07", false), Is.EqualTo("MUS_LT_DAIGO"));
            Assert.That(RaceMusic.CueFor(c, "C08", "campaign", "S14", true), Is.EqualTo("MUS_LT_EMI"));
            Assert.That(RaceMusic.CueFor(c, "C12", "campaign", "S21", false), Is.EqualTo("MUS_LT_JUN"));
            Assert.That(RaceMusic.CueFor(c, "C20", "campaign", "S28", false), Is.EqualTo("MUS_LT_MAKO"));
            Assert.That(RaceMusic.CueFor(c, "C24", "campaign", "S29", false), Is.EqualTo("MUS_PENULTIMATE"));
            Assert.That(RaceMusic.CueFor(c, "C25", "campaign", "S30", false), Is.EqualTo("MUS_FINAL_REINA"));
            Assert.That(RaceMusic.CueFor(c, "C25", "campaign", "S30", true), Is.EqualTo("MUS_FINAL_SHIORI"));
            // The same courses outside those stages play their region's arrangement, never an encounter theme.
            foreach (string course in new[] { "C04", "C24", "C25" })
                Assert.That(RaceMusic.CueFor(c, course, "freeplay", null, false), Does.StartWith("MUS_RACE_"), course);
            Assert.That(RaceMusic.CueFor(c, "C25", "campaign", "S01", true), Does.StartWith("MUS_RACE_"));
        }

        [Test]
        public void TrialsTutorialAndRegions()
        {
            ContentCatalogue c = Catalogue;
            Assert.That(RaceMusic.CueFor(c, "C01", "trial", null, false, "mean"), Is.EqualTo("MUS_TT_MEAN"));
            Assert.That(RaceMusic.CueFor(c, "C01", "trial", null, false, "best"), Is.EqualTo("MUS_TT_BEST"));
            Assert.That(RaceMusic.CueFor(c, "C01", "trial", null, false, null, driftRanked: true), Is.EqualTo("MUS_TT_DRIFT"));
            Assert.That(RaceMusic.CueFor(c, "C01", "tutorial", null, false), Is.EqualTo("MUS_TUTORIAL"));
            string manifest = File.ReadAllText("Assets/Content/Data/authored/music.cues.json");
            foreach (CourseDef course in c.Courses)
            {
                string cue = RaceMusic.CueFor(c, course.Id, "freeplay", null, false);
                StringAssert.Contains($"\"{cue}\"", manifest, course.Id);
                if (new[] { "mizuhana", "kasumi", "kurogawa", "akebono", "hoshimi", "tsukishiro" }.Contains(course.Region))
                    Assert.That(cue, Is.EqualTo("MUS_RACE_" + course.Region.ToUpperInvariant()), course.Id);
            }
            foreach (StageDef s in c.Stages)
                foreach (bool hard in new[] { false, true })
                    StringAssert.Contains($"\"{RaceMusic.CueFor(c, s.Course, "campaign", s.Id, hard)}\"", manifest, s.Id);
        }
    }
}
