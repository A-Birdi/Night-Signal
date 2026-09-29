using NightSignal.Core.Ghosts;

namespace NightSignal.CoreTests;

/// <summary>The post-race route/elevation chart (spec §8): distance, elevation, braking points and per-sector time.</summary>
public sealed class RouteChartTests
{
    /// <summary>
    /// 30 s along +x at 10 Hz: 20 m/s, a peak of 30 m/s at 10 s braking to 15 m/s by 11 s, back up to 20 m/s; climbing 1 m
    /// per 10 m from 400 m; checkpoints at 10 s and 20 s.
    /// </summary>
    static GhostRecording Run()
    {
        var g = new GhostRecording { Header = new GhostHeader { CourseId = "C01", ResultMicros = 30_000_000 } };
        float x = 0f;
        for (int k = 0; k <= 300; k++)
        {
            float t = k / 10f;
            float v = t < 8f ? 20f : t <= 10f ? 20f + (t - 8f) * 5f : t <= 11f ? 30f - (t - 10f) * 15f : Math.Min(20f, 15f + (t - 11f) * 2f);
            if (k > 0) x += v / 10f;
            g.Add(t, x, x > 400f ? (x - 400f) / 10f : 0f, 0f, 0f, 0f, 0f, 1f, v);
        }
        g.CheckpointMicros.Add(10_000_000);
        g.CheckpointMicros.Add(20_000_000);
        return g;
    }

    [Fact]
    public void DistanceElevationAndBounds_FollowTheRecording()
    {
        RouteChart c = RouteChart.Build(Run());
        Assert.Equal(301, c.Metres.Count);
        Assert.InRange(c.LengthMetres, 560f, 620f);
        Assert.Equal(0f, c.MinElevation);
        Assert.InRange(c.MaxElevation, 16f, 22f);
        Assert.Equal(0f, c.MinZ);
        Assert.Equal(0f, c.MaxZ);
    }

    [Fact]
    public void TheBrakingPoint_IsThePeakBeforeTheDrop()
    {
        RouteChart c = RouteChart.Build(Run());
        int i = Assert.Single(c.BrakingPoints);
        Assert.Equal(100, i); // the 30 m/s peak at 10 s
    }

    [Fact]
    public void Sectors_TakeTheirTimeFromTheCumulativeDeltas()
    {
        RouteChart c = RouteChart.Build(Run(), new long[] { 500_000, 300_000 });
        Assert.Equal(3, c.Sectors.Count);            // two checkpoints: three sectors, the last to the end
        Assert.Equal(500_000, c.Sectors[0].DeltaMicros);
        Assert.Equal(-200_000, c.Sectors[1].DeltaMicros);
        Assert.Null(c.Sectors[2].DeltaMicros);        // no delta after the last checkpoint
        Assert.Equal(100, c.Sectors[0].ToSample);
        Assert.Equal(200, c.Sectors[1].ToSample);
        Assert.Equal(0, c.WorstSector!.Index);
        Assert.Equal(1, c.BestSector!.Index);
        Assert.Contains("most lost in sector 1 (+0.50 s", c.Summary("R01's reference"));
        Assert.Contains("most gained in sector 2", c.Summary("R01's reference"));
    }

    [Fact]
    public void WithoutAReference_TheSummaryDescribesTheRoute()
    {
        RouteChart c = RouteChart.Build(Run());
        Assert.All(c.Sectors, s => Assert.Null(s.DeltaMicros));
        Assert.Contains("braking points", c.Summary("x"));
        Assert.DoesNotContain("vs", c.Summary("x"));
        Assert.Empty(RouteChart.Build(null).Metres);
    }
}
