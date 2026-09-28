using NightSignal.Core.Ghosts;

namespace NightSignal.CoreTests;

/// <summary>
/// Ghosts (spec §8): a sampled replay with its ruleset header; stored and read back exactly; well-formed or refused; valid as
/// a personal target only for a legal finish with no reset; compared only under the same course revision, format, surface
/// and physics/scoring rules; sector deltas against its checkpoint times; CH68 needs a valid C07 ghost beaten by 1 s.
/// </summary>
public sealed class GhostTests
{
    static GhostRecording Run(string course = "C07", long resultMs = 200_000, int resets = 0, string physics = "p1")
    {
        var g = new GhostRecording
        {
            Header = new GhostHeader
            {
                CourseId = course, CourseRevision = "r1", Format = "time-attack", Surface = "dry", PhysicsVersion = physics, ScoringVersion = "s1",
                CarModelId = "V01", Pi = 220, ResultMicros = resultMs * 1000, Resets = resets, Driver = "Robin", RecordedUtc = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc),
            },
        };
        float end = resultMs / 1000f;
        for (float t = 0f; t <= end + 1e-3f; t += 0.1f) g.Add(t, t * 20f, 1f, 0f, 0f, 0.3826834f, 0f, 0.9238795f, 20f);
        for (int i = 1; i <= 4; i++) g.CheckpointMicros.Add(resultMs * 1000 * i / 5);
        return g;
    }

    [Fact]
    public void RoundTrip_KeepsHeaderSamplesAndCheckpoints()
    {
        GhostRecording g = Run();
        GhostRecording back = GhostRecording.Parse(g.ToJson(), out string error)!;
        Assert.Null(error);
        Assert.Equal(g.Count, back.Count);
        Assert.Equal(g.Header.ResultMicros, back.Header.ResultMicros);
        Assert.Equal(g.CheckpointMicros, back.CheckpointMicros);
        Assert.Equal(g.Px[100], back.Px[100], 3);
        Assert.Empty(back.Problems());
        Assert.True(back.ValidPersonal);
        Assert.Null(GhostRecording.Parse("{\"schema\":\"other\"}", out error));
        Assert.Null(GhostRecording.Parse("not json", out error));
    }

    [Fact]
    public void Validity_AndCompatibility()
    {
        Assert.False(Run(resets: 1).ValidPersonal, "a reset-slowed run is not a target");
        GhostRecording broken = Run();
        broken.T[10] = broken.T[9];
        Assert.Contains("sample times are not increasing", broken.Problems());
        Assert.True(Run().CompatibleWith(Run(resultMs: 190_000).Header));
        Assert.False(Run().CompatibleWith(Run(physics: "p2").Header), "a physics change makes old ghosts reference-only");
        Assert.False(Run().CompatibleWith(Run(course: "C08").Header));
    }

    [Fact]
    public void Interpolation_Index_AndSectorDeltas()
    {
        GhostRecording g = Run();
        Assert.Equal(0, g.IndexAt(-1f));
        Assert.Equal(g.Count - 1, g.IndexAt(1e6f));
        int i = g.IndexAt(12.34f);
        Assert.True(g.T[i] <= 12.34f && g.T[i + 1] > 12.34f);
        Assert.Equal(-500_000, g.SectorDeltaMicros(0, 40_000_000 - 500_000));
        Assert.Null(g.SectorDeltaMicros(9, 1));
    }

    [Fact]
    public void Ch68_AValidC07GhostBeatenByASecond()
    {
        Assert.True(GhostChallenges.BeatsYesterday(Run(resultMs: 200_000), Run(resultMs: 198_900)));
        Assert.False(GhostChallenges.BeatsYesterday(Run(resultMs: 200_000), Run(resultMs: 199_200)), "less than a second");
        Assert.False(GhostChallenges.BeatsYesterday(Run(resultMs: 210_000, resets: 1), Run(resultMs: 200_000)), "yesterday slowed by a reset");
        Assert.False(GhostChallenges.BeatsYesterday(Run(course: "C08", resultMs: 200_000), Run(course: "C08", resultMs: 190_000)), "only C07");
        Assert.False(GhostChallenges.BeatsYesterday(Run(resultMs: 200_000), Run(resultMs: 190_000, physics: "p2")), "an incompatible ruleset");
    }
}
