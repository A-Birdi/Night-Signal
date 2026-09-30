using System.Text.Json;
using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

/// <summary>
/// Challenge-zone chains (Core <see cref="ZoneChainRun"/>; Appendix E CH17, CH19, CH22, CH27): one continuous legal slide
/// links the zones it crosses, survives 1.0 s of straightening, banks on release / sector end / finish / its bank gate,
/// and is lost on a wall impact, leaving the road, a reset or a spin; a demonstration zone keeps the longest in-band hold.
/// </summary>
public sealed class ZoneChainTests
{
    const float Dt = 1f / 60f;

    static ChallengeZone Z(string id, string challenge, string kind, float start, float end, float offset = 0f, float tol = 3f) =>
        new() { Id = id, Challenge = challenge, Kind = kind, StartMetres = start, EndMetres = end, LineOffset = offset, LineTolerance = tol };

    /// <summary>Drives the run along the course at 72 km/h (1/3 m per step).</summary>
    sealed class Driver(ZoneChainRun run)
    {
        public double At;
        public float Lateral;

        int ZoneAt(double d)
        {
            for (int i = 0; i < run.Zones.Count; i++)
                if (d >= run.Zones[i].StartMetres && d <= run.Zones[i].EndMetres) return i;
            return -1;
        }

        /// <summary>Drives <paramref name="metres"/> at <paramref name="slip"/> degrees (signed: + one way, − the other).</summary>
        public Driver Drive(double metres, float slip, string bankGateAt = null, float gateMetres = -1f, bool touch = false)
        {
            double to = At + metres;
            while (At < to)
            {
                double before = At;
                At += 20.0 * Dt;
                string gate = gateMetres >= 0f && before < gateMetres && At >= gateMetres ? bankGateAt ?? "" : "";
                run.Step(new ZoneChainSample { DeltaSeconds = Dt, SpeedKmh = 72f, SlipAngleDegrees = slip, ProgressMetres = At, MovingInLegalDirection = true,
                    OnRoad = true, Zone = ZoneAt(At), LateralMetres = Lateral, BankGate = gate, WallContact = touch, RouteMetres = (float)At });
            }
            return this;
        }

        public Driver Event(bool wall = false, bool offRoad = false, bool reset = false)
        {
            At += 20.0 * Dt;
            run.Step(new ZoneChainSample { DeltaSeconds = Dt, SpeedKmh = 72f, SlipAngleDegrees = 25f, ProgressMetres = At, MovingInLegalDirection = true,
                OnRoad = !offRoad, WallImpact = wall, Reset = reset, Zone = ZoneAt(At), LateralMetres = Lateral });
            return this;
        }

        /// <summary>Straightens (2° slip) long enough for any chain to bank.</summary>
        public Driver Release() => Drive(30, 2f);
    }

    static ZoneChainRun C03Like() => new(new[]
    {
        Z("L1", "CH17", ChallengeZone.Transition, 100, 145), Z("L2", "CH17", ChallengeZone.Transition, 145, 214), Z("L3", "CH17", ChallengeZone.Transition, 212, 305),
    });

    [Fact]
    public void ThreeLinkedCorners_InOneBankedChain()
    {
        var run = C03Like();
        new Driver(run).Drive(90, 2f).Drive(230, 28f).Release();
        Assert.True(run.Linked("CH17", 3));
        Assert.Equal(new[] { 0, 1, 2 }, run.Chains.Single().Zones);
        Assert.Equal(ChainEnd.Banked, run.Chains.Single().End);
    }

    [Fact]
    public void ALostChain_LinksNothing()
    {
        foreach (Func<Driver, Driver> end in new Func<Driver, Driver>[] { d => d.Event(wall: true), d => d.Event(offRoad: true), d => d.Event(reset: true), d => d.Drive(10, 120f) })
        {
            var run = C03Like();
            end(new Driver(run).Drive(90, 2f).Drive(220, 28f)).Release();
            Assert.False(run.Linked("CH17", 3));
            Assert.False(run.Chains.Single().Banked);
            Assert.Equal(1, run.ChainsLost);
        }
    }

    [Fact]
    public void Straightening_OverOneSecond_Banks_UnderOneSecondLinks()
    {
        var run = C03Like();
        // 1.2 s at 20 m/s = 24 m straight between the second and third corner: two chains.
        new Driver(run).Drive(90, 2f).Drive(110, 28f).Drive(24, 3f).Drive(90, 28f).Release();
        Assert.False(run.Linked("CH17", 3));
        Assert.True(run.Linked("CH17", 2));
        Assert.Equal(2, run.Chains.Count);

        var brief = C03Like();
        // 0.8 s (16 m) through the transition: one chain.
        new Driver(brief).Drive(90, 2f).Drive(110, 28f).Drive(16, 3f).Drive(90, 28f).Release();
        Assert.True(brief.Linked("CH17", 3));
    }

    [Fact]
    public void ASlideHeldBetweenZones_KeepsTheChain_AndTheBankGateBanksIt()
    {
        var run = new ZoneChainRun(new[]
        {
            Z("T1", "CH27", ChallengeZone.Transition, 100, 200), Z("T2", "CH27", ChallengeZone.Transition, 300, 400), Z("T3", "CH27", ChallengeZone.Transition, 500, 600),
        });
        // 100 m gaps (5 s) held at 18° outside the zones; the gate at 700.
        new Driver(run).Drive(90, 2f).Drive(700, 18f, "CH27", 700f).Release();
        Assert.True(run.LinkedAll("CH27", atBankGate: true));
        Assert.Equal("CH27", run.Chains.Single().BankGate);

        var early = new ZoneChainRun(run.Zones);
        // Released after the last zone, before the gate: linked, but not banked at the gate.
        new Driver(early).Drive(90, 2f).Drive(520, 18f).Drive(100, 2f, "CH27", 700f).Release();
        Assert.True(early.LinkedAll("CH27"));
        Assert.False(early.LinkedAll("CH27", atBankGate: true));
    }

    [Fact]
    public void AllZones_MustBeLinkedOnce_InForwardOrder()
    {
        var run = new ZoneChainRun(new[] { Z("A", "CH27", ChallengeZone.Transition, 100, 200), Z("B", "CH27", ChallengeZone.Transition, 300, 400) });
        new Driver(run).Drive(90, 2f).Drive(150, 25f).Release();
        Assert.False(run.LinkedAll("CH27"), "only one of two");
        // Zone indices visited backwards (a route whose later zone comes first cannot be "forward").
        var backwards = new ZoneChainRun(new[] { Z("A", "CH27", ChallengeZone.Transition, 300, 400), Z("B", "CH27", ChallengeZone.Transition, 100, 200) });
        new Driver(backwards).Drive(90, 2f).Drive(350, 25f).Release();
        Assert.Equal(new[] { 1, 0 }, backwards.Chains.Single().Zones);
        Assert.True(backwards.LinkedAll("CH27"), "linked in the order the route places them (by start)");
        Assert.False(new ZoneChainRun(Array.Empty<ChallengeZone>()).LinkedAll("CH27"), "a course without the zones");
    }

    [Fact]
    public void ClipZones_LinkOnlyOnTheirLine_AndATouchSpoilsTheChain()
    {
        ChallengeZone[] clips =
        {
            Z("C1", "CH22", ChallengeZone.Clip, 100, 170, 3.1f, 1.2f), Z("C2", "CH22", ChallengeZone.Clip, 250, 320, -3f, 1.2f),
        };
        var off = new ZoneChainRun(clips);
        var d = new Driver(off).Drive(90, 2f);
        d.Lateral = 3.0f; d.Drive(100, 25f);
        d.Lateral = 0f; d.Drive(150, 25f).Release(); // the second clip crossed on the centreline
        Assert.Equal(new[] { 0 }, off.Chains.Single().Zones);
        Assert.False(off.LinkedAll("CH22"));

        var on = new ZoneChainRun(clips);
        d = new Driver(on).Drive(90, 2f);
        d.Lateral = 3.0f; d.Drive(120, 25f);
        d.Lateral = -2.2f; d.Drive(130, 25f).Release();
        Assert.True(on.LinkedAll("CH22", untouched: true));

        var touched = new ZoneChainRun(clips);
        d = new Driver(touched).Drive(90, 2f);
        d.Lateral = 3.0f; d.Drive(120, 25f, touch: true);
        d.Lateral = -2.2f; d.Drive(130, 25f).Release();
        Assert.True(touched.LinkedAll("CH22"));
        Assert.False(touched.LinkedAll("CH22", untouched: true));
    }

    [Fact]
    public void DemoZone_KeepsTheLongestInBandHold()
    {
        var run = new ZoneChainRun(new[] { Z("D", "CH19", ChallengeZone.Demo, 100, 260) });
        // 30 m at 28° (1.5 s), a moment at 38°, then 64 m at 25° (3.2 s), then below the band to the zone's end and beyond.
        new Driver(run).Drive(100, 2f).Drive(30, 28f).Drive(4, 38f).Drive(64, 25f).Drive(100, 15f).Release();
        Assert.InRange(run.LongestHoldFor("CH19"), 3.15f, 3.25f);

        var shallow = new ZoneChainRun(run.Zones);
        new Driver(shallow).Drive(100, 2f).Drive(150, 15f).Release();
        Assert.Equal(0f, shallow.LongestHoldFor("CH19"));

        var broken = new ZoneChainRun(run.Zones);
        new Driver(broken).Drive(100, 2f).Drive(40, 28f).Event(wall: true).Drive(40, 28f).Release();
        Assert.InRange(broken.LongestHoldFor("CH19"), 1.9f, 2.1f);
    }

    [Fact]
    public void SlidingBackOverVisitedRoad_IsNotDrifting()
    {
        var run = C03Like();
        var d = new Driver(run).Drive(90, 2f).Drive(60, 28f);
        d.At -= 40; // behind the high-water mark (after a reset the chain is lost anyway; here the progress simply is not new)
        d.Drive(30, 28f);
        Assert.False(run.ChainAlive);
        Assert.Equal(new[] { 0, 1 }, run.Chains.Single().Zones);
    }

    static ZoneChainRun Slalom() => new(new[]
    {
        Z("S1", "CH23", ChallengeZone.Transition, 100, 180), Z("S2", "CH23", ChallengeZone.Transition, 190, 270),
        Z("S3", "CH23", ChallengeZone.Transition, 280, 360), Z("S4", "CH23", ChallengeZone.Transition, 370, 430),
    });

    /// <summary>A slide in a zone, then caught (3°) before the next zone begins.</summary>
    static Driver SlideAndCatch(Driver d, float slip) => d.Drive(40, slip).Drive(50, slip > 0 ? 3f : -3f);

    [Fact]
    public void FourAlternatingRecoveries_OneInEachZone()
    {
        var run = Slalom();
        var d = new Driver(run).Drive(100, 2f);
        SlideAndCatch(SlideAndCatch(SlideAndCatch(SlideAndCatch(d, 20f), -20f), 20f), -20f);
        Assert.Equal(new[] { 1, -1, 1, -1 }, run.Recoveries.Select(r => r.Direction));
        Assert.Equal(new[] { 0, 1, 2, 3 }, run.Recoveries.Select(r => r.Zone));
        Assert.True(run.AlternatingRecoveries("CH23"));
        Assert.False(run.AlternatingRecoveries("CH99"), "a course without the zones");
    }

    [Fact]
    public void Recoveries_MustAlternate_BeCaught_AndNotSpin()
    {
        var same = Slalom();
        SlideAndCatch(SlideAndCatch(SlideAndCatch(SlideAndCatch(new Driver(same).Drive(100, 2f), 20f), 20f), -20f), 20f);
        Assert.False(same.AlternatingRecoveries("CH23"), "the same way twice");

        var uncaught = Slalom();
        var d = SlideAndCatch(new Driver(uncaught).Drive(100, 2f), 20f);
        d.Drive(95, -20f); // the second slide is still held well past its zone and into the next
        SlideAndCatch(SlideAndCatch(d, 20f), -20f);
        Assert.False(uncaught.AlternatingRecoveries("CH23"), "a slide not caught in time");

        var spun = Slalom();
        d = SlideAndCatch(SlideAndCatch(new Driver(spun).Drive(100, 2f), 20f), -20f);
        d.Drive(10, 120f).Drive(5, 3f);
        SlideAndCatch(SlideAndCatch(d, 20f), -20f);
        Assert.True(spun.Spun);
        Assert.False(spun.AlternatingRecoveries("CH23"), "a spin between the recoveries");

        var resetAfter = Slalom();
        d = SlideAndCatch(SlideAndCatch(SlideAndCatch(SlideAndCatch(new Driver(resetAfter).Drive(100, 2f), 20f), -20f), 20f), -20f);
        d.Event(reset: true);
        Assert.True(resetAfter.AlternatingRecoveries("CH23"), "a reset after the drill does not undo it");

        var resetBetween = Slalom();
        d = SlideAndCatch(SlideAndCatch(new Driver(resetBetween).Drive(100, 2f), 20f), -20f);
        d.Event(reset: true);
        SlideAndCatch(SlideAndCatch(d, 20f), -20f);
        Assert.False(resetBetween.AlternatingRecoveries("CH23"), "a reset between the recoveries");
    }

    /// <summary>The routes carry the zones the four predicates read (no route change was needed for them).</summary>
    [Theory]
    [InlineData("C03", "CH17", "transition-zone", 3, false)]
    [InlineData("C05", "CH19", "demo-zone", 1, false)]
    [InlineData("C09", "CH22", "clip-zone", 3, false)]
    [InlineData("C19", "CH27", "transition-zone", 6, true)]
    public void TheRoutes_TagTheirChallengeZones(string course, string challenge, string kind, int count, bool bankGate)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Courses", course, "route.json")));
        var gates = doc.RootElement.GetProperty("gates").EnumerateArray()
            .Where(g => g.TryGetProperty("challenge", out JsonElement c) && c.GetString() == challenge).ToList();
        var zones = gates.Where(g => g.GetProperty("kind").GetString() == kind).ToList();
        Assert.Equal(count, zones.Count);
        Assert.All(zones, z => Assert.True(z.GetProperty("endMetres").GetSingle() > z.GetProperty("startMetres").GetSingle()));
        var bank = gates.Where(g => g.GetProperty("kind").GetString() == "timing").ToList();
        Assert.Equal(bankGate ? 1 : 0, bank.Count);
        float first = zones.Min(z => z.GetProperty("startMetres").GetSingle()), end = zones.Max(z => z.GetProperty("endMetres").GetSingle());
        if (bankGate)
        {
            float gate = bank[0].GetProperty("startMetres").GetSingle();
            Assert.True(gate > end, "the bank gate follows the last zone");
            end = gate;
        }
        // No sector boundary between the first zone and the chain's end (a sector end would bank the chain first).
        Assert.DoesNotContain(doc.RootElement.GetProperty("sectors").EnumerateArray(), s => s.GetProperty("startMetres").GetSingle() > first && s.GetProperty("startMetres").GetSingle() <= end);
    }
}
