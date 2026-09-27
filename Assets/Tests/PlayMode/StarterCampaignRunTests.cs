using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.Front;
using NightSignal.Race;
using NightSignal.Track;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Addendum 02 F08: a complete Normal campaign for each starter from a fresh Local profile, played through the same Core
    /// calls the game makes — the event plan (roster, live featured rivals, benchmark, cap), a real race on the stage's
    /// course driven by the validator autopilot (legal inputs, automation), Core Local progression settling it, and the
    /// Garage buying the starter's intended upgrade path (build-recipes.json) with the credits earned, when the shop has it.
    /// A stage that is not cleared is retried like a player would (lost attempts still pay) — and, like a player with savings,
    /// after each lost attempt the run may buy the next step of the same recipe ahead of its schedule (marked "ahead" in the
    /// evidence) when the shop has it and the wallet allows; after four attempts the run stops there and says so. Races are
    /// deterministic, so a retry without a change repeats the same result. Evidence per starter in
    /// Evidence/progression/campaign/. Nothing is written as a clear.
    /// </summary>
    public sealed class StarterCampaignRunTests
    {
        const int MaxAttempts = 4;

        [UnityTest, Timeout(3600000)]
        public IEnumerator NormalCampaign_WithTheIntendedPath([Values("V01", "V02", "V03")] string starter)
        {
            string folder = Path.GetFullPath(Path.Combine("Builds", "ProgressionRuns", starter));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            LocalSession.UseFolder(folder);
            LocalSession session = LocalSession.Current;
            Assert.That(session.Create("Progression " + starter, starter, out string created), Is.True, created);
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = session.Catalogue;
            PartsCatalogue parts = lib.Parts;
            OwnedCar car = session.Profile.Cars.First(c => c.ModelId == starter);
            CarRecipe recipe = lib.Recipes.Car(starter);

            var evidence = new CampaignEvidence
            {
                starter = starter, startingCredits = session.Profile.WalletBalance,
                driver = "RouteFollower validator autopilot (automation, legal inputs) — not a human run", unityVersion = Application.unityVersion,
            };
            string stoppedAt = null;
            for (int n = 1; n <= Limits.CampaignStages && stoppedAt == null; n++)
            {
                StageDef stage = cat.Stage("S" + n.ToString("00"));
                bool cleared = false;
                for (int attempt = 1; attempt <= MaxAttempts && !cleared; attempt++)
                {
                    evidence.purchases.AddRange(Develop(session, cat, parts, car.InstanceId, recipe, n, attempt - 1));
                    var choice = new LocalCarChoice { ModelId = starter, InstanceId = car.InstanceId };
                    LocalEventPlan plan = LocalEvents.Campaign(session, stage, CampaignMode.Normal, choice);
                    ResolvedCarSpec spec = session.RaceSpec(car.InstanceId, out AppliedVehicleBuild frozen, out string problem);
                    Assert.That(problem, Is.Null, problem);
                    int pi = session.AppliedPi(car);
                    List<RaceEntrantResult> results = null;
                    yield return Race(plan, starter, spec, r => results = r);
                    Assert.That(results, Is.Not.Null, $"{stage.Id}: the race completed");
                    LocalEventFacts facts = LocalEvents.Facts(session, plan, results, CourseRuntime.Active?.SourceHash ?? "");
                    long before = session.Profile.WalletBalance;
                    LocalProgressionResult applied = LocalProgression.ApplyEvent(session.Profile, cat, session.Music, facts);
                    Assert.That(!applied.Changed || session.Commit(applied, out string saveNote), Is.True, "saved");
                    cleared = session.Profile.Campaign.IsCleared(CampaignMode.Normal, n);
                    RaceEntrantResult me = results.First(r => r.Entrant.Human);
                    RaceEntrantResult featured = results.FirstOrDefault(r => r.Entrant.Roster.Role == "featured");
                    evidence.attempts.Add(new AttemptRow
                    {
                        stage = stage.Id, type = stage.Type, course = stage.Course, attempt = attempt, capPi = stage.MaxPI, pi = pi,
                        build = frozen?.Build == null ? "stock" : string.Join("+", frozen.Build.AllPartIds()),
                        outcome = me.Outcome.ToString(), placement = me.Placement, of = results.Count, timeMs = me.FinishTimeMicros / 1000,
                        targetMs = plan.Benchmark.TargetTimeMs, featured = featured?.Entrant.Roster.EntrantId ?? "",
                        featuredTimeMs = featured != null && featured.Outcome == RunOutcome.Finished ? featured.FinishTimeMicros / 1000 : 0,
                        beatFeatured = me.BeatFeaturedRival, cleared = cleared, earned = session.Profile.WalletBalance - before,
                        wallet = session.Profile.WalletBalance, reason = applied.Reason ?? "",
                        contracts = me.ContractsPassed, contractDetail = me.ContractDetail ?? "",
                    });
                    Debug.Log($"[NightSignal.Campaign] {starter} {stage.Id} ({stage.Type}, {stage.Course}, cap {stage.MaxPI}) try {attempt}: PI {pi}, " +
                              $"{me.Outcome} P{me.Placement}/{results.Count} {me.FinishTimeMicros / 1e6:F1}s vs target {plan.Benchmark.TargetTimeMs / 1000.0:F0}s" +
                              (featured != null ? $", featured {featured.Entrant.Roster.EntrantId} {(featured.Outcome == RunOutcome.Finished ? (featured.FinishTimeMicros / 1e6).ToString("F1") + "s" : featured.Outcome.ToString())}" : "") +
                              (me.ContractsPassed >= 0 ? $", Four Signals {me.ContractsPassed}/4 ({me.ContractDetail})" : "") +
                              $" → {(cleared ? "CLEARED" : "not cleared")}; wallet {session.Profile.WalletBalance:N0}");
                }
                if (!cleared) stoppedAt = stage.Id;
            }
            evidence.stagesCleared = session.Profile.Campaign.Count(CampaignMode.Normal);
            evidence.finalWallet = session.Profile.WalletBalance;
            evidence.stoppedAt = stoppedAt ?? "";
            Directory.CreateDirectory("Evidence/progression/campaign");
            File.WriteAllText($"Evidence/progression/campaign/{starter}-normal.json", JsonUtility.ToJson(evidence, true));
            Debug.Log($"[NightSignal.Campaign] {starter}: {evidence.stagesCleared}/30 Normal stages cleared in {evidence.attempts.Count} attempts, " +
                      $"{evidence.purchases.Count} purchases, wallet {evidence.finalWallet:N0}{(stoppedAt != null ? ", stopped at " + stoppedAt : "")}");
            Assert.That(stoppedAt, Is.Null, $"{starter}: the campaign stopped at {stoppedAt} after {MaxAttempts} attempts (see the evidence)");
        }

        /// <summary>
        /// Buys (or applies, when owned) the latest recipe step meant by this stage — or, after lost attempts, up to
        /// <paramref name="ahead"/> steps further along the same Normal path — if the shop has it and the wallet allows.
        /// </summary>
        static List<PurchaseRow> Develop(LocalSession session, ContentCatalogue cat, PartsCatalogue parts, string instanceId, CarRecipe recipe, int stageNumber, int ahead)
        {
            var rows = new List<PurchaseRow>();
            var at = new StageRef { Mode = CampaignMode.Normal, Stage = stageNumber };
            List<RecipeStep> normal = recipe.Path.Where(s => s.Kind == "main" && StageRef.Parse(s.By).Mode == CampaignMode.Normal)
                .OrderBy(s => StageRef.Parse(s.By)).ToList();
            int due = normal.FindLastIndex(s => StageRef.Parse(s.By).CompareTo(at) <= 0);
            int pick = Math.Min(normal.Count - 1, due + ahead);
            if (pick < 0) return rows;
            RecipeStep step = normal[pick];
            bool early = pick > due;
            MechanicalSnapshot target = RecipeBook.ToSnapshot(step, parts);
            LocalWorkspaceLoad load = LocalGarage.LoadWorkspace(session.Profile, cat, parts, instanceId, DateTime.UtcNow);
            if (!load.Ok || load.Workspace.Applied.Build.ContentEquals(target)) return rows;
            QuoteResult q = LocalGarage.Quote(session.Profile, cat, parts, instanceId, target, DateTime.UtcNow);
            var row = new PurchaseRow { beforeStage = "S" + stageNumber.ToString("00"), step = step.Id + (early ? " (ahead, after a lost attempt)" : ""), wallet = session.Profile.WalletBalance };
            if (q.Status == QuoteStatus.Ok && q.Quote.Total <= session.Profile.WalletBalance)
            {
                LocalProgressionResult r = LocalGarage.BuyAndApply(session.Profile, cat, parts, instanceId, q.Quote, true, "run-" + Guid.NewGuid().ToString("N").Substring(0, 16), DateTime.UtcNow);
                row.cost = q.Quote.Total;
                row.result = r.Changed && session.Commit(r, out string note) ? "bought" : "refused: " + r.Reason;
            }
            else if (q.Status == QuoteStatus.NothingToBuy)
            {
                CarBuildWorkspace ws = load.Workspace;
                BuildContext ctx = LocalGarage.Context(session.Profile, cat, parts, instanceId);
                GarageOperations.EditDraft(ws, ws.Revision, target, ctx, DateTime.UtcNow);
                OperationResult a = GarageOperations.Apply(ws, ws.Revision, ctx, null, DateTime.UtcNow);
                LocalProgressionResult save = LocalGarage.SaveWorkspace(session.Profile, instanceId, ws, load.StoredRevision);
                row.result = a.Accepted && save.Changed && session.Commit(save, out string note) ? "applied (owned)" : "not applied: " + a.Message;
            }
            else row.result = q.Status == QuoteStatus.Ok ? $"waiting: costs {q.Quote.Total:N0}, wallet {session.Profile.WalletBalance:N0}" : "not in the shop yet: " + q.Message;
            rows.Add(row);
            return rows;
        }

        static IEnumerator Race(LocalEventPlan plan, string carId, ResolvedCarSpec spec, Action<List<RaceEntrantResult>> done)
        {
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                $"Assets/Content/Courses/{plan.CourseId}/{plan.CourseId}.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#else
            yield break;
#endif
            yield return null;
            // Stage-default conditions (the stage side's authored surface and lighting), as the game does.
            plan.Rules.Surface = RaceConditions.Surface(ContentLibrary.Load().Catalogue, plan.Rules.Kind, plan.Rules.StageId, plan.Rules.Mode, CourseRuntime.Active);
            var go = new GameObject("CampaignRun");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = carId;
            session.PlayerSpec = spec;
            session.Rules = plan.Rules;
            session.OpposingAi = plan.OpposingAi;
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            yield return null;
            float start = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - start < 600f) yield return null;
            done(session.Results);
            UnityEngine.Object.Destroy(go);
            yield return null;
        }

        [Serializable]
        sealed class CampaignEvidence
        {
            public string starter, driver, unityVersion, stoppedAt;
            public long startingCredits, finalWallet;
            public int stagesCleared;
            public List<AttemptRow> attempts = new List<AttemptRow>();
            public List<PurchaseRow> purchases = new List<PurchaseRow>();
        }

        [Serializable]
        sealed class AttemptRow
        {
            public string stage, type, course, build, outcome, featured, reason, contractDetail;
            public int attempt, capPi, pi, placement, of, contracts = -1;
            public long timeMs, targetMs, featuredTimeMs, earned, wallet;
            public bool beatFeatured, cleared;
        }

        [Serializable]
        sealed class PurchaseRow
        {
            public string beforeStage, step, result;
            public long cost, wallet;
        }
    }
}
