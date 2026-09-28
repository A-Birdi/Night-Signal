using NightSignal.Core.Content;

namespace NightSignal.CoreTests;

/// <summary>
/// The §14 course-uniqueness rule on synthetic centrelines: a copy shares everything; a distinct course shares nothing;
/// a shared approach within the first/last 100 m is allowed; a course sharing half its road fails the 70% floor.
/// </summary>
public sealed class CourseUniquenessTests
{
    static IReadOnlyList<(double, double)> Line(params (double, double)[] p) => p;

    [Fact]
    public void Copies_Fail_Distinct_Pass_ApproachesAllowed()
    {
        var straight = Line((0, 0), (0, 1000));
        var rows = CourseUniqueness.Evaluate(new Dictionary<string, IReadOnlyList<(double, double)>>
        {
            ["A"] = straight,
            ["A-copy"] = Line((0, 0), (0, 1000)),
            ["B"] = Line((500, 0), (500, 1000)),
            // Shares A's first 90 m (an approach), then turns away east.
            ["C"] = Line((0, 0), (0, 90), (900, 90)),
            // Shares A for 500 m, then leaves: half its road is A's.
            ["D"] = Line((0, 0), (0, 500), (500, 500), (500, 520)),
        }).ToDictionary(r => r.CourseId);
        Assert.False(rows["A"].Passes);
        Assert.Equal("A-copy", rows["A"].ClosestCourse);
        Assert.True(rows["B"].Passes);
        Assert.True(rows["B"].Exclusive > 0.95, $"B only crosses others: {rows["B"].Exclusive:F3}");
        Assert.True(rows["C"].Passes, $"C exclusive {rows["C"].Exclusive:F2}");
        Assert.False(rows["D"].Passes, $"D exclusive {rows["D"].Exclusive:F2}");
        Assert.True(CourseUniqueness.Counted("regular") && CourseUniqueness.Counted("finale") && CourseUniqueness.Counted("freeplay"));
        Assert.False(CourseUniqueness.Counted("tutorial"));
    }
}
