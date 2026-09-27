using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using Xunit.Abstractions;

namespace NightSignal.BuildsTests;

public sealed class PerformanceIndexTests
{
    readonly ITestOutputHelper output;
    public PerformanceIndexTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [MemberData(nameof(TestData.AllCars), MemberType = typeof(TestData))]
    public void StockEstimate_IsExactlyTheCatalogueBasePi_AndLabelledAnEstimate(string carId)
    {
        BuildContext ctx = TestData.Ctx(carId);
        PiEstimate e = PerformanceIndexEstimator.Estimate(ctx.Stock, ctx.Stock, ctx.Car.BasePI);
        Assert.Equal(ctx.Car.BasePI, e.Value);
        Assert.Equal(PerformanceIndex.ClassOf(ctx.Car.BasePI), e.Class);
        Assert.True(e.IsEstimate);
        Assert.Contains("pending Unity handling-harness calibration", e.Basis);
    }

    [Fact]
    public void Slope_MatchesALeastSquaresFitOverThe18CatalogueCars()
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (CarDef car in TestData.Content.Cars)
        {
            xs.Add(Math.Log(PerformanceIndexEstimator.LapTimeProxy(PerformanceIndexEstimator.Figures(TestData.Ctx(car.Id).Stock))));
            ys.Add(car.BasePI);
        }
        double mx = xs.Average(), my = ys.Average();
        double k = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / xs.Sum(x => (x - mx) * (x - mx));
        double a = my - k * mx;
        double rms = Math.Sqrt(xs.Zip(ys, (x, y) => Math.Pow(y - (a + k * x), 2)).Average());
        output.WriteLine($"fitted PI per ln(proxy) = {-k:F1} (constant {PerformanceIndexEstimator.PiPerLogLapTime}), residual RMS {rms:F1} PI");
        Assert.InRange(-k, PerformanceIndexEstimator.PiPerLogLapTime * 0.95, PerformanceIndexEstimator.PiPerLogLapTime * 1.05);
        Assert.True(rms < 40, $"proxy explains the catalogue PIs poorly (RMS {rms:F1})");
    }

    [Theory]
    [MemberData(nameof(TestData.AllCars), MemberType = typeof(TestData))]
    public void BetterTyres_RaiseThePi_AndEveryBuildStaysInsideTheIndexRange(string carId)
    {
        int stock = TestData.Ctx(carId).Car.BasePI;
        int t1 = TestData.Pi(carId, TestData.Build("TYR-T1-STREET"));
        int t2 = TestData.Pi(carId, TestData.Build("TYR-T2-SPORT"));
        int t3 = TestData.Pi(carId, TestData.Build("TYR-T3-SEMISLICK"));
        int t4 = TestData.Pi(carId, TestData.Build("TYR-T4-COMPETITION"));
        if (stock < PerformanceIndex.Max)
        {
            Assert.True(stock < t1 || t1 == PerformanceIndex.Max);
            Assert.True(t1 <= t2 && t2 <= t3 && t3 <= t4);
        }
        Assert.All(new[] { t1, t2, t3, t4 }, v => Assert.InRange(v, PerformanceIndex.Min, PerformanceIndex.Max));
    }

    [Fact]
    public void ClassBoundaries_FollowRulesPerformanceIndex()
    {
        Assert.Equal(PerformanceClass.D, PerformanceIndex.ClassOf(299));
        Assert.Equal(PerformanceClass.C, PerformanceIndex.ClassOf(300));
        // A strong build on the S-class flagship clamps at 999 instead of overflowing the index.
        MechanicalSnapshot big = TestData.Build("TYR-T4-COMPETITION", "ENG-T4-SIX-RACE", "FI-T4-TURBOKIT", "WGT-T3-CARBON", "GBX-T4-DOG");
        PiEstimate e = PerformanceIndexEstimator.Estimate(TestData.Resolve("V18", big), TestData.Ctx("V18").Stock, 835);
        Assert.Equal(999, e.Value);
        Assert.Equal(PerformanceClass.S, e.Class);
        // Starter reaches C class with fundamentals; the estimate never crosses a cap silently (callers compare).
        int b = TestData.Pi("V01", TestData.Build("TYR-T2-SPORT", "ENG-T1-INTAKE"));
        Assert.Equal(PerformanceClass.C, PerformanceIndex.ClassOf(b));
    }

    [Fact]
    public void DerivedFigures_AreHonestEstimates_NotMeasurements()
    {
        DerivedFigures f = PerformanceIndexEstimator.Figures(TestData.Ctx("V01").Stock);
        // Mirrors VehicleFactory.DragLimitedTopSpeed for the stock V01 (the harness measured 183 km/h after 1 km, below the limit).
        Assert.InRange(f.DragLimitedTopSpeedKmh, 205, 216);
        Assert.False(f.GearLimited);
        Assert.InRange(f.FirstGearTopKmh, 57, 59);
        Assert.Equal(120, f.PowerToWeightKwPerTonne, 0);
    }
}
