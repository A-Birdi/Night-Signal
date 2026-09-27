using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    /// <summary>Addendum 01 rules: course access, purchases, votes, handles, Team Trials, live-rival encounters.</summary>
    public sealed class AddendumRulesTests
    {
        static ContentCatalogue catalogue;

        internal static Dictionary<string, string> LoadDocuments()
        {
            var docs = ContentCatalogue.RequiredFiles.ToDictionary(f => f, f => File.ReadAllText(Path.Combine("Assets/Content/Data/generated", f)));
            foreach (string f in ContentCatalogue.AuthoredFiles)
            {
                string path = Path.Combine("Assets/Content/Data/authored", f);
                if (File.Exists(path)) docs[f] = File.ReadAllText(path);
            }
            return docs;
        }

        static ContentCatalogue Catalogue => catalogue ?? (catalogue = ContentCatalogue.Load(LoadDocuments()));

        // ---------------- course access (§5.1) ----------------

        [Test]
        public void CourseAccess_DualUnlockMappingUsesEachCoursesRegularStage()
        {
            var expected = new Dictionary<string, string>
            {
                ["C05"] = "S05", ["C06"] = "S06", ["C07"] = "S08", ["C08"] = "S09", ["C09"] = "S10", ["C10"] = "S11",
                ["C11"] = "S12", ["C12"] = "S13", ["C13"] = "S15", ["C14"] = "S16", ["C15"] = "S17", ["C16"] = "S18",
                ["C17"] = "S19", ["C18"] = "S20", ["C19"] = "S22", ["C20"] = "S23", ["C21"] = "S24", ["C22"] = "S25",
                ["C23"] = "S26", ["C24"] = "S27",
            };
            Assert.That(CourseAccess.RegularStageUnlocks(Catalogue), Is.EquivalentTo(expected));
            // Reused lieutenant/penultimate stages never unlock a course.
            foreach (string stage in new[] { "S07", "S14", "S21", "S28", "S29" })
                Assert.That(CourseAccess.GrantedByNormalClear(Catalogue, stage), Is.Empty, stage);
        }

        [Test]
        public void CourseAccess_TableCoversEveryCourse()
        {
            foreach (string id in new[] { "T00", "C01", "C02", "C03", "C04" })
                Assert.That(CourseAccess.RuleFor(Catalogue, id).Kind, Is.EqualTo(CourseAccessKind.Starter), id);
            CourseAccessRule c20 = CourseAccess.RuleFor(Catalogue, "C20");
            Assert.That(c20.Kind, Is.EqualTo(CourseAccessKind.PurchaseOrCampaignClear));
            Assert.That(c20.Price, Is.EqualTo(45_000));
            Assert.That(c20.UnlockStage, Is.EqualTo("S23"));
            CourseAccessRule c25 = CourseAccess.RuleFor(Catalogue, "C25");
            Assert.That(c25.Kind, Is.EqualTo(CourseAccessKind.CampaignRewardOnly));
            Assert.That(c25.Purchasable, Is.False);
            Assert.That(CourseAccess.GrantedByNormalClear(Catalogue, "S30"), Is.EqualTo(new[] { "C25" }));
            Assert.That(CourseAccess.RuleFor(Catalogue, "FP01").Price, Is.EqualTo(45_000));
            Assert.That(CourseAccess.RuleFor(Catalogue, "FP02").Price, Is.EqualTo(54_000));
            Assert.That(CourseAccess.RuleFor(Catalogue, "FP03").Price, Is.EqualTo(63_000));
            foreach (string fp in new[] { "FP01", "FP02", "FP03" })
                Assert.That(CourseAccess.RuleFor(Catalogue, fp).Kind, Is.EqualTo(CourseAccessKind.PurchaseOnly));
            Assert.That(Catalogue.Courses.All(c => CourseAccess.RuleFor(Catalogue, c.Id).Kind != CourseAccessKind.None), Is.True);
        }

        [Test]
        public void CoursePurchase_IsIdempotent_AndNeverDoubleCharges()
        {
            var owned = new HashSet<string>();
            Assert.That(CourseAccess.DecidePurchase(Catalogue, "C20", owned, 44_999, out _), Is.EqualTo(CoursePurchaseOutcome.InsufficientFunds));
            Assert.That(CourseAccess.DecidePurchase(Catalogue, "C20", owned, 50_000, out long price), Is.EqualTo(CoursePurchaseOutcome.Purchased));
            Assert.That(price, Is.EqualTo(45_000));
            owned.Add("C20"); // committed with the debit in one transaction
            Assert.That(CourseAccess.DecidePurchase(Catalogue, "C20", owned, 50_000, out price), Is.EqualTo(CoursePurchaseOutcome.AlreadyOwned));
            Assert.That(price, Is.Zero);
            // A free unlock that committed first makes the purchase a no-charge AlreadyOwned.
            var unlocked = new HashSet<string>(CourseAccess.GrantedByNormalClear(Catalogue, "S23"));
            Assert.That(CourseAccess.DecidePurchase(Catalogue, "C20", unlocked, 50_000, out price), Is.EqualTo(CoursePurchaseOutcome.AlreadyOwned));
            Assert.That(CourseAccess.DecidePurchase(Catalogue, "C25", new HashSet<string>(), 9_000_000, out _), Is.EqualTo(CoursePurchaseOutcome.NotPurchasable));
            Assert.That(CourseAccess.DecidePurchase(Catalogue, "C02", new HashSet<string>(), 0, out _), Is.EqualTo(CoursePurchaseOutcome.AlreadyOwned));
        }

        [Test]
        public void GuestAccess_AnyCurrentOwnerSponsors_WithoutGrantingOthersOwnership()
        {
            var members = new Dictionary<string, ICollection<string>>
            {
                ["leader"] = new HashSet<string>(),
                ["robin"] = new HashSet<string> { "FP02" },
                ["sam"] = new HashSet<string>(),
            };
            Assert.That(CourseAccess.Sponsors(Catalogue, "FP02", members), Is.EqualTo(new[] { "robin" }));
            Assert.That(CourseAccess.Owns(Catalogue, "FP02", members["leader"]), Is.False, "a guest pass is event-scoped, never ownership");
            members.Remove("robin"); // last sponsor leaves before allocation
            Assert.That(CourseAccess.Sponsors(Catalogue, "FP02", members), Is.Empty);
            Assert.That(CourseAccess.Sponsors(Catalogue, "C03", members).Count, Is.EqualTo(2), "starter courses are always accessible");
        }

        // ---------------- voting (§6.2, D07) ----------------

        [Test]
        public void Ballot_DrawIsOneTicketPerBallot_AndReproducible()
        {
            var ballots = new Dictionary<string, string>
            {
                ["a1"] = "C05", ["a2"] = "C05", ["a3"] = "C05", ["a4"] = "FP01", ["a5"] = "C12", ["a6"] = "",
            };
            IReadOnlyList<BallotTally> tally = Ballot.Tally(ballots);
            Assert.That(tally[0].CourseId, Is.EqualTo("C05"));
            Assert.That(tally[0].Chance, Is.EqualTo(3.0 / 5.0).Within(1e-9), "abstentions are not tickets");
            var wins = new Dictionary<string, int>();
            for (uint r = 0; r < 5000; r++)
            {
                string w = Ballot.Draw(ballots, r, out _);
                wins[w] = wins.TryGetValue(w, out int n) ? n + 1 : 1;
            }
            Assert.That(wins["C05"], Is.EqualTo(3000));
            Assert.That(wins["FP01"], Is.EqualTo(1000));
            Assert.That(Ballot.Draw(ballots, 123456u, out int i1), Is.EqualTo(Ballot.Draw(new Dictionary<string, string>(ballots.Reverse().ToDictionary(k => k.Key, k => k.Value)), 123456u, out int i2)));
            Assert.That(i1, Is.EqualTo(i2), "the same stored random value always yields the same winner");
            Assert.That(Ballot.Draw(new Dictionary<string, string>(), 7u, out int none), Is.Null);
            Assert.That(none, Is.EqualTo(-1));
            Assert.That(Ballot.Draw(new Dictionary<string, string> { ["x"] = "C07" }, 99u, out _), Is.EqualTo("C07"));
            Assert.That(Ballot.ValidDuration(30) && Ballot.ValidDuration(15) && Ballot.ValidDuration(60), Is.True);
            Assert.That(Ballot.ValidDuration(20), Is.False);
        }

        // ---------------- handles (§9.2, D08) ----------------

        [TestCase("Robin_Birdi", true)]
        [TestCase("abc", true)]
        [TestCase("ab", false)]
        [TestCase("A2345678901234567890", true)]
        [TestCase("A23456789012345678901", false)]
        [TestCase("1robin", false)]
        [TestCase("_robin", false)]
        [TestCase("robin birdi", false)]
        [TestCase("röbin", false)]
        [TestCase("robin<b>", false)]
        [TestCase("Admin", false)]
        [TestCase("support", false)]
        public void Handles_Validation(string handle, bool valid) => Assert.That(Handles.Validate(handle, out _), Is.EqualTo(valid));

        [Test]
        public void Handles_CanonicalIsCaseInsensitive_AndAtIsLookupOnly()
        {
            Assert.That(Handles.Canonical("Robin_Birdi"), Is.EqualTo(Handles.Canonical("robin_BIRDI")));
            Assert.That(Handles.StripAt("@Robin_Birdi"), Is.EqualTo("Robin_Birdi"));
            Assert.That(Handles.Validate("@Robin_Birdi", out _), Is.False, "the @ is never part of a stored handle");
        }

        // ---------------- Team Trials (§3, D06) ----------------

        static List<TeamMemberResult> Side(params (RunOutcome o, long ms, long drift, bool human)[] m) =>
            m.Select((x, i) => new TeamMemberResult { EntrantId = "e" + i, Outcome = x.o, AdjustedFinishMs = x.ms, RawDriftScore = x.drift, Human = x.human }).ToList();

        [Test]
        public void TeamMean_CountsAllSixPositions_DroppingASlowTeammateNeverHelps()
        {
            const long timeout = 300_000;
            var finished = Side((RunOutcome.Finished, 200_000, 0, true), (RunOutcome.Finished, 205_000, 0, false), (RunOutcome.Finished, 210_000, 0, false),
                (RunOutcome.Finished, 215_000, 0, false), (RunOutcome.Finished, 220_000, 0, false), (RunOutcome.Finished, 290_000, 0, true));
            var slowQuits = Side((RunOutcome.Finished, 200_000, 0, true), (RunOutcome.Finished, 205_000, 0, false), (RunOutcome.Finished, 210_000, 0, false),
                (RunOutcome.Finished, 215_000, 0, false), (RunOutcome.Finished, 220_000, 0, false), (RunOutcome.Quit, 0, 0, true));
            TeamScore a = TeamTrials.Score(TeamTrialKind.Mean, finished, timeout);
            TeamScore b = TeamTrials.Score(TeamTrialKind.Mean, slowQuits, timeout);
            Assert.That(b.Contributions.Last().Value, Is.EqualTo(timeout + 30_000));
            Assert.That(b.Value, Is.GreaterThan(a.Value), "a quitting slow teammate makes the team mean worse, never better");
            Assert.That(b.Contributions.Count, Is.EqualTo(6));
            Assert.Throws<ArgumentException>(() => TeamTrials.Score(TeamTrialKind.Mean, finished.Take(5).ToList(), timeout));
        }

        [Test]
        public void TeamBest_And_Drift_Scoring()
        {
            var player = Side((RunOutcome.Finished, 250_000, 900, true), (RunOutcome.Finished, 240_000, 1200, false), (RunOutcome.DidNotFinish, 0, 400, false),
                (RunOutcome.Finished, 260_000, 0, false), (RunOutcome.DisqualifiedDisconnect, 0, 5000, false), (RunOutcome.Finished, 245_000, 100, false));
            var opp = Side((RunOutcome.Finished, 241_000, 800, false), (RunOutcome.Finished, 250_000, 800, false), (RunOutcome.Finished, 255_000, 800, false),
                (RunOutcome.Finished, 256_000, 0, false), (RunOutcome.Finished, 257_000, 0, false), (RunOutcome.Finished, 258_000, 0, false));
            Assert.That(TeamTrials.Score(TeamTrialKind.Best, player, 300_000).Value, Is.EqualTo(240_000));
            Assert.That(TeamTrials.Compare(TeamTrials.Score(TeamTrialKind.Best, player, 300_000), TeamTrials.Score(TeamTrialKind.Best, opp, 300_000)), Is.EqualTo(TeamTrialVerdict.PlayerTeamWins));
            TeamScore drift = TeamTrials.Score(TeamTrialKind.Drift, player, 300_000);
            Assert.That(drift.Value, Is.EqualTo(900 + 1200 + 400 + 0 + 0 + 100), "a DQ contributes zero");
            // An AFK human cannot collect completion pay for an AI win in BEST.
            var afk = Side((RunOutcome.Finished, 299_000, 0, true), (RunOutcome.Finished, 240_000, 0, false), (RunOutcome.Finished, 241_000, 0, false),
                (RunOutcome.Finished, 242_000, 0, false), (RunOutcome.Finished, 243_000, 0, false), (RunOutcome.Finished, 244_000, 0, false));
            Assert.That(TeamTrials.HumanCompletionPayable(TeamTrialKind.Best, afk[0], afk, 280_000), Is.False);
            Assert.That(TeamTrials.HumanCompletionPayable(TeamTrialKind.Best, player[0], player, 280_000), Is.True);
        }

        // ---------------- live featured rivals (§1.3) ----------------

        [Test]
        public void EncounterStages_RequireBeatingTheLiveFeaturedRival()
        {
            var b = new StageBenchmark { Kind = BenchmarkKind.Time, TargetTimeMs = 200_000, HardTimeoutMs = 500_000, RequiresBeatingFeaturedRival = true };
            var beatTarget = new HumanStageResult { PlayerId = "p1", Outcome = RunOutcome.Finished, ActivelyDroveLegalCourse = true, FinishTimeMs = 190_000, BeatFeaturedRival = false };
            StageResolution lost = StageOutcome.Resolve(CampaignMode.Normal, b, 1, new[] { beatTarget });
            Assert.That(lost.TeamSuccess, Is.False);
            Assert.That(lost.Players[0].Reason, Does.Contain("featured rival"));
            beatTarget.BeatFeaturedRival = true;
            Assert.That(StageOutcome.Resolve(CampaignMode.Normal, b, 1, new[] { beatTarget }).TeamSuccess, Is.True);
            // Hard with six humans still needs ceil(6/2) = 3 qualifiers who beat the rival.
            var six = Enumerable.Range(0, 6).Select(i => new HumanStageResult { PlayerId = "p" + i, Outcome = RunOutcome.Finished, ActivelyDroveLegalCourse = true, FinishTimeMs = 180_000, BeatFeaturedRival = i < 2 }).ToList();
            Assert.That(StageOutcome.Resolve(CampaignMode.Hard, b, 6, six).TeamSuccess, Is.False);
            six[2].BeatFeaturedRival = true;
            Assert.That(StageOutcome.Resolve(CampaignMode.Hard, b, 6, six).TeamSuccess, Is.True);
            // A rival that never started is a broken event, not a free win.
            Assert.Throws<InvalidOperationException>(() => StageOutcome.Resolve(CampaignMode.Normal, b, 1, new[] { beatTarget }, featuredRivalStarted: false));
            Assert.That(StageBenchmark.IsFeaturedEncounter("lieutenant") && StageBenchmark.IsFeaturedEncounter("finale") && !StageBenchmark.IsFeaturedEncounter("regular"), Is.True);
        }

        // ---------------- catalogue overlays ----------------

        [Test]
        public void Catalogue_HasTwentyNineCourses_AuthoredOpposition_AndNoFinaleOnlyMisuse()
        {
            ValidationReport report = CatalogueValidator.Validate(Catalogue);
            Assert.That(report.Errors.Select(e => e.ToString()), Is.Empty);
            Assert.That(report.Counts["courses"], Is.EqualTo(29));
            foreach (StageDef s in Catalogue.Stages)
                foreach (StageSide side in new[] { s.Normal, s.Hard })
                {
                    Assert.That(side.Opponents[0], Is.EqualTo(side.Lead), s.Id);
                    Assert.That(side.Opponents.Count + Limits.MaxEventHumanEntrants, Is.LessThanOrEqualTo(Limits.MaxRaceVehicles), s.Id);
                    if (s.Type != "finale") Assert.That(side.Opponents.Any(FinalRivals.IsFinaleOnly), Is.False, s.Id);
                }
            Assert.That(Catalogue.Stage("S30").Normal.Opponents, Is.EqualTo(new[] { "R40" }));
            Assert.That(Catalogue.Stage("S30").Hard.Opponents, Is.EqualTo(new[] { "R48" }));
        }

        [Test]
        public void Catalogue_RefusesToLoadWithoutTheAddendumOverlays()
        {
            Dictionary<string, string> docs = LoadDocuments();
            docs.Remove("stages.opposition.json");
            Assert.Throws<ContentLoadException>(() => ContentCatalogue.Load(docs));
        }
    }
}
