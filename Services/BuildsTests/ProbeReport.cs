using NightSignal.Core.Builds;
using Xunit.Abstractions;

namespace NightSignal.BuildsTests;

/// <summary>Authoring aid: prints per-car PI ceilings for the strongest compatible build (not an assertion test).</summary>
public sealed class ProbeReport
{
    readonly ITestOutputHelper output;
    public ProbeReport(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Print_Ceilings()
    {
        foreach (var car in TestData.Content.Cars)
        {
            BuildContext ctx = TestData.Ctx(car.Id);
            var best = new MechanicalSnapshot();
            foreach (PartSlot slot in PartSlots.Mechanical)
            {
                PartDef top = TestData.Parts.CompatibleParts(ctx.Car, ctx.Tuning, slot).OrderByDescending(p => p.Tier)
                    .ThenBy(p => p.Id.Contains("LOWDRAG") || p.Id.Contains("RALLY") || p.Id.Contains("TOURING") || p.Id.Contains("DRIFT") || p.Id.Contains("RAIN") ? 1 : 0).FirstOrDefault();
                if (top != null) best.Parts[PartSlots.Id(slot)] = top.Id;
            }
            var r = BuildResolver.Resolve(ctx.Car, ctx.Tuning, TestData.Parts, best);
            string pi = r.Ok ? PerformanceIndexEstimator.Estimate(r.Spec, ctx.Stock, car.BasePI).Value.ToString() : "ERR " + string.Join("; ", r.Issues);
            int stockPi = PerformanceIndexEstimator.Estimate(ctx.Stock, ctx.Stock, car.BasePI).Value;
            output.WriteLine($"{car.Id} base {car.BasePI} stock {stockPi} max {pi} :: {string.Join(",", best.Parts.Values)}");
        }
    }
}
