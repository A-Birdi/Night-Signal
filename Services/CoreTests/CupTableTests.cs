using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

/// <summary>The Custom Cup table (spec §8): points per leg, DQs keep their line, countback.</summary>
public sealed class CupTableTests
{
    static CupLegResult R(string id, int? place, bool human = false) => new() { Id = id, Name = id, Human = human, Place = place };

    [Fact]
    public void PointsAccumulate_AndTheLeaderIsFirst()
    {
        var t = new CupTable(new[] { "C01", "C02", "C04" });
        t.AddLeg(new[] { R("you", 1, true), R("R08", 2), R("R03", 3) });
        t.AddLeg(new[] { R("you", 3, true), R("R08", 1), R("R03", 2) });
        t.AddLeg(new[] { R("you", 1, true), R("R08", 3), R("R03", 2) });
        Assert.True(t.Complete);
        List<CupEntrant> s = t.Standings();
        Assert.Equal(new[] { "you", "R08", "R03" }, s.Select(e => e.Id));
        Assert.Equal(10 + 6 + 10, s[0].Points);
        Assert.Equal(8 + 10 + 6, s[1].Points);
        Assert.Equal(1, t.PositionOf("you"));
        Assert.Throws<InvalidOperationException>(() => t.AddLeg(new[] { R("you", 1) }));
    }

    [Fact]
    public void ADisqualifiedEntrant_KeepsItsLine_ButCannotRegainTheMissedLeg()
    {
        var t = new CupTable(new[] { "C01", "C02", "C04" });
        t.AddLeg(new[] { R("you", 1, true), R("R08", 2) });
        t.AddLeg(new[] { R("you", null, true), R("R08", 1) }); // DQ in leg 2
        t.AddLeg(new[] { R("R08", 2) });                        // not started in leg 3
        CupEntrant you = t.Standings().Single(e => e.Id == "you");
        Assert.Equal(10, you.Points);
        Assert.Equal(new int?[] { 1, null, null }, you.Places);
        Assert.Equal(2, t.PositionOf("you"));
    }

    [Fact]
    public void TiesBreakOnCountback()
    {
        var t = new CupTable(new[] { "C01", "C02", "C04" });
        // A and B finish level on points; A has the win.
        t.AddLeg(new[] { R("A", 1), R("B", 2), R("C", 3) });     // A 10, B 8
        t.AddLeg(new[] { R("A", 5), R("B", 3), R("C", 1) });     // A 14, B 14
        t.AddLeg(new[] { R("A", 4), R("B", 4), R("C", 6) });     // A 19, B 19 — A has a win
        List<CupEntrant> s = t.Standings();
        Assert.Equal(s[0].Points, s[1].Points);
        Assert.Equal("A", s.First(e => e.Id == "A" || e.Id == "B").Id);
        Assert.Throws<ArgumentException>(() => new CupTable(new[] { "C01", "C02" }));
    }
}
