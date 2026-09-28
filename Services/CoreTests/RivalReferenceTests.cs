using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

/// <summary>The authored rival reference ghost's rival per course (spec §8).</summary>
public sealed class RivalReferenceTests
{
    static ContentCatalogue Cat => TestContent.Catalogue;

    [Fact]
    public void EachCourseTakesTheLeadOfItsFirstStage_TheFinaleCourseAFinaleSafeSupport_VenuesTheLieutenants_TheTutorialNone()
    {
        Assert.Equal("R01", RivalReference.For(Cat, "C01")!.Id);
        Assert.Equal("R11", RivalReference.For(Cat, "C07")!.Id); // S08 is the first stage on C07
        Assert.Equal("R33", RivalReference.For(Cat, "C25")!.Id); // S30's lead R40 is finale-only: its first support
        Assert.Null(RivalReference.For(Cat, "T00"));
        Assert.Null(RivalReference.For(Cat, "nope"));
        string[] lieutenants = Cat.Rivals.Where(r => r.Role == "lieutenant").Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(lieutenants[0], RivalReference.For(Cat, "FP01")!.Id);
        Assert.Equal(lieutenants[1], RivalReference.For(Cat, "FP02")!.Id);
        // Every counted course has one, and none is a finale-only rival.
        foreach (CourseDef c in Cat.Courses.Where(c => c.Kind != "tutorial"))
        {
            RivalDef? r = RivalReference.For(Cat, c.Id);
            Assert.NotNull(r);
            Assert.False(FinalRivals.IsFinaleOnly(r!.Id), c.Id);
        }
    }
}
