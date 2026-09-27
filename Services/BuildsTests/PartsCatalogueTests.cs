using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using Newtonsoft.Json.Linq;

namespace NightSignal.BuildsTests;

public sealed class PartsCatalogueTests
{
    static PartsCatalogue P => TestData.Parts;

    [Fact]
    public void Loads_WithUniqueStableIds_AndRevisions()
    {
        Assert.True(P.Parts.Count >= 60);
        Assert.Equal(P.Parts.Count, P.Parts.Select(p => p.Id).Distinct().Count());
        Assert.True(P.Revision >= 1 && P.PriceRevision >= 1);
        Assert.Matches("^[0-9a-f]{64}$", P.Hash);
        Assert.Empty(P.ValidateAgainst(TestData.Content));
    }

    [Fact]
    public void Prices_AreInsideTheSpecTierRanges_AndUnderThePartCap()
    {
        foreach (PartDef p in P.Parts)
        {
            var (min, max) = PartsCatalogue.TierPriceRange(p.Tier);
            Assert.InRange(p.Price, min, max);
            Assert.True(p.Price <= Limits.MaxPerformancePartPrice);
        }
        Assert.Equal((6_000L, 18_000L), PartsCatalogue.TierPriceRange(1));
        Assert.Equal((24_000L, 55_000L), PartsCatalogue.TierPriceRange(2));
        Assert.Equal((70_000L, 120_000L), PartsCatalogue.TierPriceRange(3));
        Assert.Equal((150_000L, 200_000L), PartsCatalogue.TierPriceRange(4));
    }

    [Fact]
    public void EverySlot_HasParts_EveryPartStatesItsTradeoff_AndChangesAnImplementedInput()
    {
        foreach (PartSlot slot in Enum.GetValues<PartSlot>())
            Assert.Contains(P.Parts, p => p.SlotValue == slot);
        foreach (PartDef p in P.Parts)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Tradeoff), p.Id);
            if (p.SlotValue == PartSlot.Utility) continue;
            Assert.True(p.Effects.Count > 0 || p.Tuning.Count > 0, p.Id);
            foreach (PartEffect e in p.Effects) Assert.True(SimParams.TryParse(e.Param, out _), $"{p.Id}: {e.Param}");
        }
        // Engine tiers: intake/exhaust (T1), breathing package (T2), internals (T3), full build (T4).
        foreach (int tier in new[] { 1, 2, 3, 4 }) Assert.Contains(P.Parts, p => p.SlotValue == PartSlot.Engine && p.Tier == tier);
    }

    [Fact]
    public void ShopAvailability_IsByAct_AndNeverBySpending()
    {
        foreach (PartDef p in P.Parts)
        {
            Assert.InRange(p.UnlockAct, 1, 4);
            Assert.True(p.UnlockAct <= p.Tier || p.SlotValue == PartSlot.Utility, $"{p.Id}: act {p.UnlockAct} tier {p.Tier}");
        }
        Assert.All(P.Parts.Where(p => p.Tier == 4), p => Assert.Equal(4, p.UnlockAct));
        Assert.All(P.Parts.Where(p => p.Tier == 1), p => Assert.Equal(1, p.UnlockAct));
        // No field in the model can express "unlock after spending X": the only gate is UnlockAct.
        Assert.DoesNotContain(typeof(PartDef).GetFields(), f => f.Name.Contains("Spend", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(TestData.AllCars), MemberType = typeof(TestData))]
    public void EveryCar_HasChassisData_AndACompatiblePartInEveryMechanicalSlot(string carId)
    {
        BuildContext ctx = TestData.Ctx(carId);
        Assert.True(P.TryChassis(carId, out ChassisDef ch));
        Assert.True(ch.WeightReductionMaxKg > 0);
        foreach (PartSlot slot in PartSlots.Mechanical)
        {
            IReadOnlyList<PartDef> list = P.CompatibleParts(ctx.Car, ctx.Tuning, slot);
            // Declared packaging rule: mid-engine naturally aspirated cars take no forced-induction kit (NA route only).
            bool noFiByDesign = slot == PartSlot.ForcedInduction && ctx.Car.EngineLayout == "mid" && !ctx.Tuning.Turbo;
            if (noFiByDesign) Assert.Empty(list);
            else Assert.NotEmpty(list);
        }
    }

    [Fact]
    public void Compatibility_FollowsDriveLayoutEngineFamilyAspirationGearsAndBody()
    {
        CompatibilityResult Check(string part, string car)
        {
            BuildContext c = TestData.Ctx(car);
            return P.CheckCompatibility(P.Part(part), c.Car, c.Tuning);
        }
        // Drive layout.
        Assert.True(Check("DIF-T2-AWD-CENTRE", "V03").Compatible);
        Assert.False(Check("DIF-T2-AWD-CENTRE", "V01").Compatible);
        Assert.False(Check("DIF-T1-RWD-CLUTCH", "V02").Compatible);
        // Factory aspiration: turbo upgrades only on turbo cars; conversions only on NA front/front-mid cars.
        Assert.True(Check("FI-T3-BIGTURBO", "V02").Compatible);
        Assert.False(Check("FI-T3-BIGTURBO", "V01").Compatible);
        Assert.True(Check("FI-T4-TURBOKIT", "V01").Compatible);
        Assert.False(Check("FI-T4-TURBOKIT", "V02").Compatible);
        Assert.True(Check("FI-T4-TURBOKIT", "V18").Compatible); // front-mid NA six
        Assert.False(Check("FI-T4-TURBOKIT", "V11").Compatible); // mid-engine: packaging
        Assert.False(Check("FI-T3-SUPERCHARGER", "V16").Compatible);
        // Explicit per-car exclusions with a reason.
        CompatibilityResult v04 = Check("FI-T3-SUPERCHARGER", "V04");
        Assert.False(v04.Compatible);
        Assert.Contains("bonnet", v04.Reason);
        Assert.False(Check("FI-T3-SUPERCHARGER", "V10").Compatible);
        Assert.True(Check("FI-T4-TURBOKIT", "V10").Compatible);
        // Engine family.
        Assert.True(Check("ENG-T3-ROT-BRIDGEPORT", "V10").Compatible);
        Assert.False(Check("ENG-T3-ROT-BRIDGEPORT", "V01").Compatible);
        Assert.False(Check("ENG-T2-STREET", "V10").Compatible);
        Assert.True(Check("ENG-T3-TRI-HIGHREV", "V02").Compatible);
        Assert.False(Check("ENG-T3-SIX-HIGHREV", "V01").Compatible);
        // Factory gear count.
        Assert.True(Check("GBX-T2-SIX", "V01").Compatible);
        Assert.False(Check("GBX-T2-SIX", "V04").Compatible);
        Assert.True(Check("GBX-T2-CLOSE", "V17").Compatible); // 7-speed
        // Body class.
        Assert.False(Check("AER-T3-GTWING", "V07").Compatible);
        Assert.True(Check("AER-T3-WAGONWING", "V07").Compatible);
        Assert.False(Check("BKT-T2-WIDEARCH", "V04").Compatible);
        Assert.False(Check("BKT-T2-WIDEARCH", "V11").Compatible);
        Assert.True(Check("BKT-T2-WIDEARCH", "V05").Compatible);
        // Reasons are human readable.
        Assert.Contains("AWD", Check("DIF-T2-AWD-CENTRE", "V01").Reason);
    }

    [Fact]
    public void Utility_IsOneSeparateSlot_WithOnlyTheSpecVariants()
    {
        List<PartDef> u = P.Parts.Where(p => p.SlotValue == PartSlot.Utility).ToList();
        Assert.Equal(new[] { "income:4", "income:8", "showcase:10", "showcase:5" },
            u.Select(p => p.Utility.Kind + ":" + p.Utility.Percent).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(u, p => Assert.Empty(p.Effects));
        Assert.All(u, p => Assert.Empty(p.Tuning));
        Assert.All(P.Parts.Where(p => p.SlotValue != PartSlot.Utility), p => Assert.Null(p.Utility));
    }

    [Theory]
    [InlineData("price", "Part TYR-T1-STREET: price")]
    [InlineData("param", "unknown parameter")]
    [InlineData("tuningSlot", "belongs to the brakes slot")]
    [InlineData("utilityEffect", "utility parts never change simulation inputs")]
    [InlineData("utilityPercent", "utility must be income 4/8 or showcase 5/10")]
    [InlineData("duplicate", "Duplicate part id")]
    [InlineData("noTradeoff", "tradeoff")]
    [InlineData("schema", "expected schema")]
    public void MalformedCatalogues_AreRejectedWithTheReason(string defect, string expected)
    {
        JObject doc = JObject.Parse(TestData.PartsJson);
        var parts = (JArray)doc["parts"];
        JObject First(string id) => (JObject)parts.First(p => (string)p["id"] == id);
        switch (defect)
        {
            case "price": First("TYR-T1-STREET")["price"] = 50_000; break;
            case "param": ((JArray)First("TYR-T1-STREET")["effects"])[0]["param"] = "Nitrous"; break;
            case "tuningSlot": ((JArray)First("GBX-T1-FINAL")["tuning"])[0]["key"] = "BrakeBias"; break;
            case "utilityEffect": First("UTL-INC-4")["effects"] = new JArray(new JObject { ["param"] = "PowerKw", ["op"] = "scale", ["value"] = 1.1 }); break;
            case "utilityPercent": First("UTL-INC-8")["utility"]["percent"] = 12; break;
            case "duplicate": parts.Add(First("BRK-T1-PADS").DeepClone()); break;
            case "noTradeoff": First("BRK-T1-PADS")["tradeoff"] = ""; break;
            case "schema": doc["schema"] = "night-signal/parts@0"; break;
        }
        var ex = Assert.Throws<BuildDataException>(() => PartsCatalogue.Load(doc.ToString()));
        Assert.Contains(expected, ex.Message);
    }
}
