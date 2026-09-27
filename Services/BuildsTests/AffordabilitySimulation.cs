using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.BuildsTests;

/// <summary>
/// Small deterministic affordability model (Addendum 02 §8.2): ordinary campaign income through Core Economy.Compute,
/// first clears, typical placements, a realistic loss rate and optional spending, walked stage by stage through Normal
/// then Hard. Purchases for a recipe step happen before its "by" stage. If the wallet is short, the model counts the
/// extra Freeplay races (last cleared course, 1.00 placement) a player would need — "grind". An affordable path needs none.
/// INITIAL MODEL: to be recalibrated with real race durations, placements and loss rates.
/// </summary>
public static class AffordabilitySimulation
{
    public sealed class StepReport
    {
        public string StepId = "";
        public string Kind = "";
        public string By = "";
        public string Bands = "";
        public long Cost;
        public long WalletBefore;
        /// <summary>Campaign races (including lost attempts) since the previous purchase.</summary>
        public int RacesSincePrevious;
        /// <summary>Extra races needed beyond the campaign before "by" (0 = affordable in time).</summary>
        public int GrindRaces;
        public List<string> Bought = new();
    }

    public sealed class CarReport
    {
        public string Car = "";
        public List<StepReport> Steps = new();
        public List<StepReport> Alternatives = new();
        public long TotalSpent;
        public long FinalWallet;
    }

    public static CarReport Run(CarRecipe recipe, RecipeBook book, ContentCatalogue content, PartsCatalogue parts)
    {
        EconomyModelDef eco = book.File.Economy;
        var report = new CarReport { Car = recipe.Car };
        var owned = new HashSet<string>(StringComparer.Ordinal);
        bool carOwned = recipe.Role == "starter";
        long wallet = eco.StartingCredits;
        int attempts = 0, qualifying = 0, racesSincePurchase = 0;
        string lastCourse = content.Stages.First().Course;

        var main = recipe.Path.Select(s => (Ref: StageRef.Parse(s.By), Step: s)).ToList();
        var alts = recipe.Alternatives.Select(s => (Ref: StageRef.Parse(s.By), Step: s)).ToList();
        StageRef? buyBy = recipe.BuyBy == null ? null : StageRef.Parse(recipe.BuyBy);

        foreach (CampaignMode mode in new[] { CampaignMode.Normal, CampaignMode.Hard })
        {
            List<int> failures = mode == CampaignMode.Hard ? eco.HardFailuresPerStage : eco.NormalFailuresPerStage;
            foreach (StageDef stage in content.Stages.OrderBy(s => s.Number))
            {
                var here = new StageRef { Mode = mode, Stage = stage.Number };

                // Alternatives are evaluated against the state just before this stage's main purchases.
                foreach (var a in alts.Where(x => x.Ref.Ordinal == here.Ordinal))
                    report.Alternatives.Add(Price(a.Step, owned, wallet, racesSincePurchase, parts, carOwned ? 0 : CarPrice(content, recipe), lastCourse, content, eco, out _));

                if (!carOwned && buyBy.HasValue && buyBy.Value.Ordinal == here.Ordinal)
                {
                    long price = CarPrice(content, recipe);
                    long before = wallet;
                    int grind = Grind(ref wallet, price, lastCourse, content, eco);
                    report.Steps.Add(new StepReport { StepId = recipe.Car + "-CAR", Kind = "car", By = recipe.BuyBy, Cost = price, WalletBefore = before, RacesSincePrevious = racesSincePurchase, GrindRaces = grind });
                    wallet -= price;
                    report.TotalSpent += price;
                    carOwned = true;
                    racesSincePurchase = 0;
                }
                foreach (var m in main.Where(x => x.Ref.Ordinal == here.Ordinal))
                {
                    StepReport r = Price(m.Step, owned, wallet, racesSincePurchase, parts, 0, lastCourse, content, eco, out long cost);
                    int grind = Grind(ref wallet, cost, lastCourse, content, eco);
                    r.GrindRaces = grind;
                    wallet -= cost;
                    report.TotalSpent += cost;
                    foreach (string id in r.Bought) owned.Add(id);
                    if (cost > 0) racesSincePurchase = 0;
                    report.Steps.Add(r);
                }

                int fails = failures[(stage.Number - 1) % failures.Count];
                for (int k = 0; k <= fails; k++)
                {
                    attempts++;
                    bool qualifies = k == fails;
                    int placement = qualifies ? eco.PlacementCycle[qualifying++ % eco.PlacementCycle.Count] : eco.LostAttemptPlacement;
                    long firstClear = qualifies ? Economy.FirstClearBonus(TypeOf(stage), mode) : 0;
                    wallet = Pay(wallet, content.Course(stage.Course).ExpectedSeconds, mode, placement, attempts % eco.CleanEvery == 0, firstClear, eco);
                    racesSincePurchase++;
                }
                lastCourse = stage.Course;
            }
        }
        report.FinalWallet = wallet;
        return report;
    }

    static StepReport Price(RecipeStep step, HashSet<string> owned, long wallet, int races, PartsCatalogue parts, long extra,
        string lastCourse, ContentCatalogue content, EconomyModelDef eco, out long cost)
    {
        var r = new StepReport { StepId = step.Id, Kind = step.Kind, By = step.By, Bands = string.Join("+", step.For), WalletBefore = wallet, RacesSincePrevious = races };
        cost = extra;
        foreach (string id in step.Parts.Concat(string.IsNullOrEmpty(step.Utility) ? Array.Empty<string>() : new[] { step.Utility }))
            if (!owned.Contains(id))
            {
                cost += parts.Part(id).Price;
                r.Bought.Add(id);
            }
        r.Cost = cost;
        if (step.Kind != "main")
        {
            long w = wallet;
            r.GrindRaces = Grind(ref w, cost, lastCourse, content, eco);
        }
        return r;
    }

    static long CarPrice(ContentCatalogue content, CarRecipe recipe) => content.Car(recipe.Car).Price;

    static int Grind(ref long wallet, long cost, string course, ContentCatalogue content, EconomyModelDef eco)
    {
        int n = 0;
        while (wallet < cost && n < 10_000)
        {
            wallet = Pay(wallet, content.Course(course).ExpectedSeconds, CampaignMode.Normal, 5, false, 0, eco, EventKind.FreeplaySprint);
            n++;
        }
        return n;
    }

    static long Pay(long wallet, int expectedSeconds, CampaignMode mode, int placement, bool clean, long firstClear, EconomyModelDef eco,
        EventKind kind = EventKind.CampaignStage)
    {
        PayoutBreakdown b = Economy.Compute(new PayoutFacts
        {
            AuthoredExpectedSeconds = expectedSeconds, Kind = kind, Mode = mode, Outcome = RunOutcome.Finished,
            Placement = placement, Clean = clean, UtilityIncomePercent = 0, FirstClearBonus = firstClear,
        });
        long kept = b.Total * (100 - eco.DiscretionaryPercent) / 100;
        return Wallet.Credit(wallet, kept).NewBalance;
    }

    static StageType TypeOf(StageDef s) => s.Type switch
    {
        "lieutenant" => StageType.Lieutenant,
        "penultimate" => StageType.Penultimate,
        "finale" => StageType.Finale,
        _ => StageType.Regular,
    };
}
