using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.CoreTests;

/// <summary>CH04, CH08, CH12, CH10, CH26, CH29 against the published references (authored/challenge-references.json).</summary>
public sealed class ChallengeReferencesTests
{
    static ChallengeReferencesFile Refs => TestContent.Catalogue.ChallengeReferences;

    static GateSpeedFact? Exit(float kmh) => new GateSpeedFact { Crossed = true, SpeedKmh = kmh };
    static GateSpeedFact? Zone(float exitKmh, float brakeOn, bool braked = true, int walls = 0, int contacts = 0, int resets = 0) => new GateSpeedFact
    {
        Crossed = true, ExitKmh = exitKmh, Braked = braked, BrakeOnMetres = braked ? brakeOn : -1f, ReleaseMetres = -1f,
        WallsInside = walls, ContactsInside = contacts, ResetsInside = resets,
    };

    [Fact]
    public void ThePublishedReferences_LoadIntoTheHashedCatalogue()
    {
        Assert.Equal(3, Refs.Gates.Count(g => g.Challenge == "CH04"));
        Assert.Equal(2, Refs.Gates.Count(g => g.Challenge == "CH08" && g.BrakeByMetres > 0));
        Assert.Equal(4, Refs.Gates.Count(g => g.Challenge == "CH12"));
        Assert.Single(Refs.Times, t => t.Challenge == "CH10" && t.MaxLapDifferenceMs == 2000);
        Assert.Contains(Refs.Drift, d => d.Challenge == "CH26" && d.Surface == "wet");
        Assert.Contains(Refs.Drift, d => d.Challenge == "CH29" && Math.Abs(d.MaxLostFraction - 0.05) < 1e-9);
        Assert.Contains(ContentCatalogue.AuthoredFiles, f => f == "challenge-references.json");
    }

    [Fact]
    public void Ch04_EveryExitGateAboveItsFloor()
    {
        Dictionary<string, float> floor = Refs.Gates.Where(g => g.Challenge == "CH04").ToDictionary(g => g.Gate, g => g.MinKmh);
        Assert.True(ChallengeReferenceJudge.GatesPassed(Refs, "CH04", "C02", id => Exit(floor[id] + 1f)));
        string slow = floor.Keys.First();
        Assert.False(ChallengeReferenceJudge.GatesPassed(Refs, "CH04", "C02", id => Exit(id == slow ? floor[id] - 1f : floor[id] + 1f)));
        Assert.False(ChallengeReferenceJudge.GatesPassed(Refs, "CH04", "C02", id => null)); // a gate never crossed
        Assert.False(ChallengeReferenceJudge.GatesPassed(Refs, "CH04", "C03", id => Exit(200f))); // the wrong course has none
    }

    [Fact]
    public void Ch08_And_Ch12_BrakingZonesInsideTheirEnvelopes()
    {
        List<GateSpeedReference> wet = Refs.Gates.Where(g => g.Challenge == "CH08").ToList();
        GateSpeedFact? Good(string id)
        {
            GateSpeedReference g = wet.First(x => x.Gate == id);
            return Zone((g.MinKmh + g.MaxKmh) / 2f, g.BrakeByMetres - 5f);
        }
        Assert.True(ChallengeReferenceJudge.GatesPassed(Refs, "CH08", "C08", Good));
        Assert.False(ChallengeReferenceJudge.GatesPassed(Refs, "CH08", "C08", id => { var f = Good(id)!.Value; f.BrakeOnMetres = wet.First(x => x.Gate == id).BrakeByMetres + 5f; return f; }));
        Assert.False(ChallengeReferenceJudge.GatesPassed(Refs, "CH08", "C08", id => { var f = Good(id)!.Value; f.Braked = false; return f; }));
        Assert.False(ChallengeReferenceJudge.GatesPassed(Refs, "CH08", "C08", id => { var f = Good(id)!.Value; f.ExitKmh = wet.First(x => x.Gate == id).MaxKmh + 5f; return f; }));

        List<GateSpeedReference> late = Refs.Gates.Where(g => g.Challenge == "CH12").ToList();
        GateSpeedFact? InWindow(string id) { GateSpeedReference g = late.First(x => x.Gate == id); return Zone((g.MinKmh + g.MaxKmh) / 2f, 0f); }
        Assert.True(ChallengeReferenceJudge.GatesPassed(Refs, "CH12", "C20", InWindow, contactFree: true));
        Assert.False(ChallengeReferenceJudge.GatesPassed(Refs, "CH12", "C20", id => { var f = InWindow(id)!.Value; f.ContactsInside = 1; return f; }, contactFree: true));
        Assert.False(ChallengeReferenceJudge.GatesPassed(Refs, "CH12", "C20", id => { var f = InWindow(id)!.Value; f.ResetsInside = 1; return f; }, contactFree: true));
    }

    [Fact]
    public void Ch10_EqualSplitsInsideTheSilverTime()
    {
        long silver = Refs.Times.Single(t => t.Challenge == "CH10").ReferenceMs;
        long lap = silver * 1000 / 2 - 2_000_000; // two laps comfortably inside Silver
        Assert.True(ChallengeReferenceJudge.EqualSplits(Refs, "CH10", "C11", new[] { lap, lap + 1_500_000 }, 2 * lap + 1_500_000));
        Assert.False(ChallengeReferenceJudge.EqualSplits(Refs, "CH10", "C11", new[] { lap, lap + 2_500_000 }, 2 * lap + 2_500_000)); // 2.5 s apart
        Assert.False(ChallengeReferenceJudge.EqualSplits(Refs, "CH10", "C11", new[] { silver * 600, silver * 600 }, silver * 1200)); // too slow
        Assert.False(ChallengeReferenceJudge.EqualSplits(Refs, "CH10", "C11", new[] { lap }, lap));
    }

    [Fact]
    public void Ch26_And_Ch29_DriftReferences()
    {
        long wetGold = Refs.Drift.Single(d => d.Challenge == "CH26").Raw;
        Assert.True(ChallengeReferenceJudge.BeatsDrift(Refs, "CH26", "C12", "wet", wetGold, wetGold * 1.5, wetGold * 0.5));
        Assert.False(ChallengeReferenceJudge.BeatsDrift(Refs, "CH26", "C12", "dry", wetGold * 2, wetGold * 2, 0)); // only the wet reference
        Assert.False(ChallengeReferenceJudge.BeatsDrift(Refs, "CH26", "C12", "wet", wetGold - 1, wetGold, 0));
        long gold = Refs.Drift.Single(d => d.Challenge == "CH29").Raw;
        Assert.True(ChallengeReferenceJudge.BeatsDrift(Refs, "CH29", "C24", null, gold, gold / 0.96, gold / 0.96 - gold)); // 4 % lost
        Assert.False(ChallengeReferenceJudge.BeatsDrift(Refs, "CH29", "C24", null, gold, gold / 0.94, gold / 0.94 - gold)); // 6 % lost
    }

    [Fact]
    public void BrokenReferences_AreRefusedByTheCatalogue()
    {
        Dictionary<string, string> docs = TestContent.LoadDocuments();
        docs["challenge-references.json"] = """{"schema":"night-signal/challenge-references@1","gates":[{"challenge":"CH04","course":"C99","gate":"x","kind":"exit-speed","minKmh":50}]}""";
        Assert.Throws<ContentLoadException>(() => ContentCatalogue.Load(docs));
        docs["challenge-references.json"] = """{"schema":"night-signal/challenge-references@1","drift":[{"challenge":"CH29","course":"C24","raw":0}]}""";
        Assert.Throws<ContentLoadException>(() => ContentCatalogue.Load(docs));
    }
}
