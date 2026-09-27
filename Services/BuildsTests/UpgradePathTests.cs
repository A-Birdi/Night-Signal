using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using Xunit.Abstractions;

namespace NightSignal.BuildsTests;

/// <summary>
/// Addendum 02 §8 / D206 / F09 (data level only): every one of the 18 models has an authored, legal favorite-car path
/// across the Normal acts and Hard that meets the authored band demands on the PI ESTIMATE, respects caps and shop
/// acts, starts with fundamentals, never needs T4 in every slot, and is affordable before each step under the
/// deterministic economy model. This proves the data is consistent — NOT that the cars are fast enough when driven.
/// </summary>
public sealed class UpgradePathTests
{
    readonly ITestOutputHelper output;
    public UpgradePathTests(ITestOutputHelper output) => this.output = output;

    static RecipeBook Book => TestData.Recipes;

    static int ActOf(StageRef r) => r.Mode == CampaignMode.Hard ? 4 : TestData.Content.Stages.First(s => s.Number == r.Stage).Act;

    public static IEnumerable<object[]> Cars() => TestData.AllCars();

    [Fact]
    public void Recipes_CoverAll18Cars_AndTheThreeStarters()
    {
        Assert.Equal(18, TestData.Content.Cars.Count);
        foreach (CarDef car in TestData.Content.Cars)
        {
            CarRecipe r = Book.Car(car.Id);
            Assert.NotNull(r);
            Assert.Equal(car.Starter ? "starter" : "purchase", r.Role);
            Assert.True(car.Starter || r.BuyBy != null, $"{car.Id} must say when it is bought");
            Assert.NotEmpty(r.Path);
        }
        Assert.Equal(18, Book.File.Cars.Select(c => c.Car).Distinct().Count());
    }

    [Fact]
    public void Bands_ShareOneCapPerBand_AndDemandsAreBelowCaps()
    {
        foreach (ProgressionBand b in Book.File.Bands)
        {
            int first = int.Parse(b.FirstStage.Substring(1)), last = int.Parse(b.LastStage.Substring(1));
            var caps = TestData.Content.Stages.Where(s => s.Number >= first && s.Number <= last).Select(s => s.MaxPI).Distinct().ToList();
            Assert.Single(caps);
            Assert.True(b.DemandPi < caps[0], $"{b.Id} demand {b.DemandPi} must be below its cap {caps[0]}");
        }
        // Every stage of both modes is in exactly one band.
        foreach (string mode in new[] { "normal", "hard" })
            for (int n = 1; n <= Limits.CampaignStages; n++)
                Assert.Single(Book.File.Bands, b => b.Mode == mode && int.Parse(b.FirstStage.Substring(1)) <= n && int.Parse(b.LastStage.Substring(1)) >= n);
    }

    [Theory]
    [MemberData(nameof(Cars))]
    public void EveryStep_IsResolvable_ShopLegal_CapLegal_AndMeetsDemand(string carId)
    {
        CarRecipe recipe = Book.Car(carId);
        BuildContext ctx = TestData.Ctx(carId);
        foreach (RecipeStep step in recipe.Path.Concat(recipe.Alternatives))
        {
            MechanicalSnapshot s = RecipeBook.ToSnapshot(step, TestData.Parts);
            ResolveResult r = BuildResolver.Resolve(ctx.Car, ctx.Tuning, TestData.Parts, s);
            Assert.True(r.Ok, $"{step.Id}: {string.Join("; ", r.Issues)}");
            StageRef by = StageRef.Parse(step.By);
            foreach (string id in s.AllPartIds())
                Assert.True(TestData.Parts.Part(id).UnlockAct <= ActOf(by), $"{step.Id}: {id} is not in the shop by {step.By}");
            int pi = PerformanceIndexEstimator.Estimate(r.Spec, ctx.Stock, ctx.Car.BasePI).Value;
            Assert.NotEmpty(step.For);
            foreach (string bandId in step.For)
            {
                ProgressionBand band = Book.Band(bandId);
                int cap = RecipeBook.CapOf(band, TestData.Content);
                Assert.True(pi <= cap, $"{step.Id}: PI {pi} over the {band.Id} cap {cap}");
                Assert.True(pi >= band.DemandPi, $"{step.Id}: PI {pi} below the {band.Id} demand {band.DemandPi}");
                Assert.DoesNotContain(bandId, recipe.CapExcludedBands);
                Assert.True(by.CompareTo(StageRef.Parse((band.Mode == "hard" ? "H:" : "N:") + band.LastStage)) <= 0, $"{step.Id} arrives after {band.Id} ends");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cars))]
    public void EveryReachableBand_IsCovered_AndExclusionsAreOnlyWhereStockIsOverTheCap(string carId)
    {
        CarRecipe recipe = Book.Car(carId);
        CarDef car = TestData.Content.Car(carId);
        StageRef entry = recipe.BuyBy == null ? StageRef.Parse("N:S01") : StageRef.Parse(recipe.BuyBy);
        foreach (ProgressionBand band in Book.File.Bands)
        {
            int cap = RecipeBook.CapOf(band, TestData.Content);
            bool excluded = recipe.CapExcludedBands.Contains(band.Id);
            Assert.Equal(car.BasePI > cap, excluded);
            if (excluded) continue;
            StageRef last = StageRef.Parse((band.Mode == "hard" ? "H:" : "N:") + band.LastStage);
            if (last.CompareTo(entry) < 0) continue; // band over before the car is bought
            StageRef first = RecipeBook.FirstStageOf(band);
            StageRef needed = first.CompareTo(entry) < 0 ? entry : first;
            bool stockServes = car.BasePI >= band.DemandPi && car.BasePI <= cap && !recipe.Path.Any(s => StageRef.Parse(s.By).CompareTo(needed) <= 0);
            Assert.True(stockServes || recipe.Path.Any(s => s.For.Contains(band.Id) && StageRef.Parse(s.By).CompareTo(needed) <= 0),
                $"{carId}: no build serves {band.Id} by {needed}");
        }
    }

    [Theory]
    [MemberData(nameof(Cars))]
    public void FundamentalsFirst_SelectiveT4_NotTopTierEverywhere(string carId)
    {
        CarRecipe recipe = Book.Car(carId);
        var owned = new HashSet<string>();
        bool first = true;
        foreach (RecipeStep step in recipe.Path)
        {
            List<PartDef> bought = step.Parts.Where(id => !owned.Contains(id)).Select(TestData.Parts.Part).ToList();
            if (first && bought.Count > 0)
            {
                Assert.Contains(bought, p => p.SlotValue == PartSlot.Tyres || p.SlotValue == PartSlot.Brakes || p.SlotValue == PartSlot.Gearbox);
                Assert.All(bought, p => Assert.True(p.Tier <= 2, $"{step.Id}: first purchase {p.Id} is T{p.Tier}"));
                first = false;
            }
            List<PartDef> build = step.Parts.Select(TestData.Parts.Part).ToList();
            int t4 = build.Count(p => p.Tier == 4);
            Assert.True(t4 <= 4, $"{step.Id}: {t4} T4 parts — the path must not need top tier in every slot");
            StageRef by = StageRef.Parse(step.By);
            if (t4 > 0) Assert.True(by.Mode == CampaignMode.Hard || ActOf(by) == 4, $"{step.Id}: T4 before Act IV");
            foreach (string id in step.Parts) owned.Add(id);
        }
    }

    [Fact]
    public void Starters_HaveAtLeastThreeSubstantialNormalRevisions_AndRealMechanicalDevelopment()
    {
        foreach (CarDef car in TestData.Content.Cars.Where(c => c.Starter))
        {
            CarRecipe recipe = Book.Car(car.Id);
            var owned = new HashSet<string>();
            int substantial = 0;
            var purchaseStages = new List<int>();
            foreach (RecipeStep step in recipe.Path.Where(s => StageRef.Parse(s.By).Mode == CampaignMode.Normal))
            {
                long spend = step.Parts.Where(id => !owned.Contains(id)).Sum(id => TestData.Parts.Part(id).Price);
                if (spend >= 20_000) substantial++;
                if (spend > 0) purchaseStages.Add(StageRef.Parse(step.By).Stage);
                foreach (string id in step.Parts) owned.Add(id);
            }
            Assert.True(substantial >= 3, $"{car.Id}: only {substantial} substantial Normal revisions");
            // A workshop decision roughly every 3–4 regular stages is a pacing TARGET; the data keeps gaps ≤ 7 stages.
            for (int i = 1; i < purchaseStages.Count; i++)
                Assert.True(purchaseStages[i] - purchaseStages[i - 1] <= 7, $"{car.Id}: gap {purchaseStages[i - 1]}→{purchaseStages[i]}");
            // Unchanged starter hardware is not the intended whole-campaign solution: the Normal finale build is well above stock.
            RecipeStep lastNormal = recipe.Path.Last(s => StageRef.Parse(s.By).Mode == CampaignMode.Normal);
            int pi = TestData.Pi(car.Id, RecipeBook.ToSnapshot(lastNormal, TestData.Parts));
            Assert.True(pi >= car.BasePI + 150, $"{car.Id}: Normal finale build PI {pi} is not a real development of {car.BasePI}");
        }
    }

    [Fact]
    public void Paths_OfferAnIncrementalAndALongTermAlternative()
    {
        foreach (CarRecipe r in Book.File.Cars)
        {
            Assert.Contains(r.Alternatives, a => a.Kind == "incremental");
            Assert.Contains(r.Alternatives, a => a.Kind == "longTerm");
        }
    }

    [Fact]
    public void AffordabilitySimulation_AllMainStepsAndIncrementalAlternativesAffordableBeforeTheirStage()
    {
        var failures = new List<string>();
        output.WriteLine("car  step              kind         by      bands      cost     wallet  racesSincePrev  grind");
        foreach (CarRecipe recipe in Book.File.Cars)
        {
            AffordabilitySimulation.CarReport rep = AffordabilitySimulation.Run(recipe, Book, TestData.Content, TestData.Parts);
            foreach (var s in rep.Steps.Concat(rep.Alternatives))
            {
                output.WriteLine($"{recipe.Car}  {s.StepId,-16}  {s.Kind,-11}  {s.By,-6}  {s.Bands,-9}  {s.Cost,7}  {s.WalletBefore,9}  {s.RacesSincePrevious,14}  {s.GrindRaces,5}");
                if (s.GrindRaces > 0) failures.Add($"{s.StepId} ({s.Kind}) needs {s.GrindRaces} extra races before {s.By}");
                RecipeStep src = recipe.Path.Concat(recipe.Alternatives).FirstOrDefault(x => x.Id == s.StepId);
                if (src != null && src.Label.Contains("preset") && s.Cost != 0)
                    failures.Add($"{s.StepId} is labelled a preset of owned parts but costs {s.Cost}");
            }
            output.WriteLine($"{recipe.Car}  total spent {rep.TotalSpent}, wallet after Hard S30 {rep.FinalWallet}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Report_PiPerStep()
    {
        foreach (CarRecipe recipe in Book.File.Cars)
        {
            BuildContext ctx = TestData.Ctx(recipe.Car);
            foreach (RecipeStep step in recipe.Path.Concat(recipe.Alternatives))
            {
                ResolveResult r = BuildResolver.Resolve(ctx.Car, ctx.Tuning, TestData.Parts, RecipeBook.ToSnapshot(step, TestData.Parts));
                string pi = r.Ok ? PerformanceIndexEstimator.Estimate(r.Spec, ctx.Stock, ctx.Car.BasePI).Value.ToString() : "ERR";
                string bands = string.Join("+", step.For.Select(b => $"{b}[{Book.Band(b).DemandPi}-{RecipeBook.CapOf(Book.Band(b), TestData.Content)}]"));
                output.WriteLine($"{step.Id,-10} {step.Kind,-11} {step.By,-6} PI {pi,4} {bands}");
            }
        }
    }
}
