using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

public sealed class RecordFormatTests
{
    [Theory]
    [InlineData(222_815L, "03:42.815")]
    [InlineData(0L, "00:00.000")]
    [InlineData(59_999L, "00:59.999")]
    [InlineData(3_599_999L, "59:59.999")]
    [InlineData(3_600_000L, "1:00:00.000")]
    [InlineData(3_723_456L, "1:02:03.456")]
    [InlineData(36_000_000L, "10:00:00.000")]
    public void RaceTimes_UseMinutesSecondsMillis_AndHoursPastSixtyMinutes(long ms, string expected) =>
        Assert.Equal(expected, RecordFormat.Time(ms));

    [Fact]
    public void Micros_AreTruncatedToTheAuthoritativeMillisecond_NeverRoundedUp()
    {
        Assert.Equal("03:42.815", RecordFormat.TimeFromMicros(222_815_999));
        Assert.Equal("03:42.815", RecordFormat.TimeFromMicros(222_815_000));
    }

    [Fact]
    public void NegativeTimes_AreRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => RecordFormat.Time(-1));

    [Fact]
    public void NoValidValues_UseExplicitPlaceholders_NotZero()
    {
        Assert.Equal("--:--.---", RecordFormat.Value(MetricKind.ElapsedTime, null));
        Assert.Equal("--:--.---", RecordFormat.Value(MetricKind.BestLap, null));
        Assert.Equal("--:--.---", RecordFormat.OptionalTime(null));
        Assert.Equal("Score: N/A", RecordFormat.Value(MetricKind.RawDriftScore, null));
        Assert.Equal("Score: N/A", RecordFormat.Value(MetricKind.TeamCombinedScore, null));
        Assert.Equal("Best: — / Target: 5", RecordFormat.Value(MetricKind.PerfectDriftCount, null, 5));
        Assert.Equal("Best: — / Target: 5", RecordFormat.Count(null, 5));
    }

    [Fact]
    public void Values_FormatByMetric()
    {
        Assert.Equal("03:42.815", RecordFormat.Value(MetricKind.ElapsedTime, 222_815));
        Assert.Equal("Score: 12,345", RecordFormat.Value(MetricKind.RawDriftScore, 12_345));
        Assert.Equal("Best: 3 / Target: 5", RecordFormat.Value(MetricKind.CleanSectors, 3, 5));
        Assert.Equal("Best: 0 / Target: 5", RecordFormat.Value(MetricKind.PerfectDriftCount, 0, 5)); // a real zero is a value
        Assert.Equal("Best: 4", RecordFormat.Count(4));
        Assert.Equal("+00:01.234", RecordFormat.Value(MetricKind.TargetDelta, 1_234));
        Assert.Equal("-00:01.234", RecordFormat.Delta(-1_234));
        Assert.Equal("±00:00.000", RecordFormat.Delta(0));
        // Team mean is stored as the exact sum of six contributions and displayed as the mean.
        Assert.Equal("01:40.000", RecordFormat.Value(MetricKind.TeamMeanTime, 6 * 100_000L));
        Assert.Equal("01:40.000", RecordFormat.Value(MetricKind.TeamMeanTime, 6 * 100_000L + 5)); // truncated, never rounded up
    }

    [Fact]
    public void DisplayStates_AreDistinctFromEachOtherAndFromValues()
    {
        var texts = Enum.GetValues<RecordDisplayState>().Where(s => s != RecordDisplayState.HasValue).Select(RecordFormat.State).ToList();
        Assert.Equal(texts.Count, texts.Distinct().Count());
        Assert.Contains("No result", texts);
        Assert.Contains("Not attempted", texts);
        Assert.Contains("Did not finish", texts);
        Assert.Contains("Pending verification", texts);
        Assert.Contains("Legacy/incompatible result", texts);
        Assert.Equal("", RecordFormat.State(RecordDisplayState.HasValue));
        Assert.Equal("Local / Unverified", RecordFormat.Verification(RecordVerification.LocalUnverified));
        Assert.Equal("Local / Offline", DomainNotices.Badge(ProgressionDomain.Local));
    }

    [Fact]
    public void MetricDirections_AndTiesAtStoredPrecision()
    {
        Assert.True(Metrics.IsBetter(MetricKind.ElapsedTime, 100, 101));
        Assert.True(Metrics.IsBetter(MetricKind.RawDriftScore, 101, 100));
        Assert.True(Metrics.IsBetter(MetricKind.TargetDelta, -5, 3));
        Assert.Equal(0, Metrics.Compare(MetricKind.ElapsedTime, 222_815, 222_815));
        Assert.Equal(MetricKind.TeamMeanTime, Metrics.ForTeamTrial(TeamTrialKind.Mean));
        Assert.Equal(MetricKind.TeamBestTime, Metrics.ForTeamTrial(TeamTrialKind.Best));
        Assert.Equal(MetricKind.TeamCombinedScore, Metrics.ForTeamTrial(TeamTrialKind.Drift));
        Assert.True(Metrics.Get(MetricKind.TeamCombinedScore).IsTeamMetric);
        Assert.False(Metrics.Get(MetricKind.RawDriftScore).IsTeamMetric);
        Assert.False(Metrics.Get(MetricKind.ElapsedTime).IsValidValue(0)); // no "0:00.000" pretend time
    }
}
