using System;
using System.Linq;
using NightSignal.Core.Rules;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    public sealed class RaceRulesTests
    {
        // ---------------- capacity and rosters (Addendum 01 §1, supersedes the six-TOTAL cap) ----------------

        static string[] Humans(int n) => Enumerable.Range(1, n).Select(i => $"acct-{i}").ToArray();

        [Test]
        public void Capacity_EveryLegalHumanAndAiCombinationIsAccepted_UpToTwelveVehicles()
        {
            for (int h = 1; h <= Limits.MaxEventHumanEntrants; h++)
            for (int ai = 0; ai <= Limits.MaxRaceVehicles - h; ai++)
            {
                RaceRoster r = RosterPlanner.PlanFreeplay(Humans(h), EventFormat.FreeplaySprint, ai, null);
                Assert.That(r.WasClamped, Is.False, $"H={h} AI={ai}");
                Assert.That(r.Humans, Is.EqualTo(h));
                Assert.That(r.OpposingAi, Is.EqualTo(ai));
                Assert.That(r.Vehicles, Is.LessThanOrEqualTo(Limits.MaxRaceVehicles));
            }
            // Maximum AI with one human and with six humans.
            Assert.That(RosterPlanner.PlanFreeplay(Humans(1), EventFormat.FreeplayCircuit, 11, null).Vehicles, Is.EqualTo(12));
            Assert.That(RosterPlanner.PlanFreeplay(Humans(6), EventFormat.FreeplayCircuit, 6, null).Vehicles, Is.EqualTo(12));
        }

        [Test]
        public void Capacity_SevenHumansAndThirteenVehiclesAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RosterPlanner.ValidateCounts(7, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RosterPlanner.ValidateCounts(0, 0, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => RosterPlanner.ValidateCounts(6, 0, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => RosterPlanner.ValidateCounts(1, 6, 6));
            Assert.DoesNotThrow(() => RosterPlanner.ValidateCounts(6, 0, 6));
            Assert.Throws<ArgumentOutOfRangeException>(() => RosterPlanner.PlanFreeplay(Humans(7), EventFormat.FreeplaySprint, 0, null));
        }

        [Test]
        public void Freeplay_OverCapAiIsClampedWithExplanation_HumansNeverEjected()
        {
            RaceRoster r = RosterPlanner.PlanFreeplay(Humans(4), EventFormat.FreeplaySprint, 11, null);
            Assert.That(r.Humans, Is.EqualTo(4));
            Assert.That(r.OpposingAi, Is.EqualTo(8));
            Assert.That(r.WasClamped, Is.True);
            Assert.That(r.Explanation, Does.Contain("12 cars"));
        }

        [Test]
        public void TimeAttack_IsHumansOnlyAndNonContact_StaleAiRequestCannotSlipIn()
        {
            for (int h = 1; h <= 6; h++)
            {
                RaceRoster r = RosterPlanner.PlanFreeplay(Humans(h), EventFormat.TimeAttack, 5, new[] { "R01" });
                Assert.That(r.Contact, Is.EqualTo(ContactPolicy.NonContact));
                Assert.That(r.Entries.Any(e => e.Kind == ActorKind.Ai), Is.False, $"H={h}");
                Assert.That(r.WasClamped, Is.True);
            }
            Assert.That(RosterPlanner.PlanFreeplay(Humans(2), EventFormat.FreeplaySprint, 2, null).Contact, Is.EqualTo(ContactPolicy.LightContact));
        }

        [Test]
        public void Campaign_AuthoredOppositionIsLive_FeaturedFirst_NoReplaySubstitute()
        {
            for (int h = 1; h <= 6; h++)
            {
                RaceRoster r = RosterPlanner.PlanCampaign(Humans(h), new[] { "R01", "R05" }, "S02", CampaignMode.Normal);
                Assert.That(r.Humans, Is.EqualTo(h));
                Assert.That(r.OpposingAi, Is.EqualTo(2), "humans never displace authored opponents");
                Assert.That(r.FeaturedRival, Is.EqualTo("R01"));
                Assert.That(r.Contact, Is.EqualTo(ContactPolicy.LightContact));
            }
        }

        [Test]
        public void Finales_HaveALiveSolidFinalRival_AtOneAndSixHumans()
        {
            foreach (int h in new[] { 1, 6 })
            {
                RaceRoster normal = RosterPlanner.PlanCampaign(Humans(h), new[] { "R40" }, "S30", CampaignMode.Normal);
                RaceRoster hard = RosterPlanner.PlanCampaign(Humans(h), new[] { "R48" }, "S30", CampaignMode.Hard);
                Assert.That(normal.Vehicles, Is.EqualTo(h + 1));
                Assert.That(normal.FeaturedRival, Is.EqualTo("R40"));
                Assert.That(hard.FeaturedRival, Is.EqualTo("R48"));
                Assert.That(normal.Entries.Count(e => e.DriverId == "R40"), Is.EqualTo(1), "no duplicate final IDs");
            }
            Assert.Throws<ArgumentException>(() => RosterPlanner.PlanCampaign(Humans(1), new[] { "R48" }, "S30", CampaignMode.Normal));
        }

        [Test]
        public void FinaleOnlyRivals_AreRejectedEverywhereElse()
        {
            foreach (string id in new[] { "R40", "R48" })
            {
                foreach (AiPlacementContext ctx in Enum.GetValues(typeof(AiPlacementContext)))
                    if (ctx != AiPlacementContext.CampaignEncounter)
                        Assert.That(FinalRivals.Allowed(id, ctx), Is.False, $"{id} as {ctx}");
                Assert.That(FinalRivals.Allowed(id, AiPlacementContext.CampaignEncounter, "S29", CampaignMode.Hard), Is.False);
            }
            Assert.That(FinalRivals.Allowed("R40", AiPlacementContext.CampaignEncounter, "S30", CampaignMode.Normal), Is.True);
            Assert.That(FinalRivals.Allowed("R48", AiPlacementContext.CampaignEncounter, "S30", CampaignMode.Hard), Is.True);
            Assert.Throws<ArgumentException>(() => RosterPlanner.PlanFreeplay(Humans(1), EventFormat.FreeplaySprint, 2, new[] { "R12", "R40" }));
            Assert.Throws<ArgumentException>(() => RosterPlanner.PlanCampaign(Humans(1), new[] { "R33", "R48" }, "S29", CampaignMode.Hard));
            Assert.Throws<ArgumentException>(() => RosterPlanner.PlanTeamTrial(Humans(2), new[] { "R01", "R02", "R03", "R40" }, new[] { "R05", "R06", "R07", "R09", "R10", "R11" }));
            Assert.That(FinalRivals.Allowed("R08", AiPlacementContext.FreeplayOpponent), Is.True, "defeated lieutenants follow normal rules");
        }

        [Test]
        public void TeamTrial_IsSixVersusSix_ForEveryHumanCount()
        {
            string[] allies = { "R01", "R02", "R03", "R04", "R05", "R06" };
            string[] opponents = { "R09", "R10", "R11", "R12", "R13", "R14" };
            for (int h = 1; h <= 6; h++)
            {
                RaceRoster r = RosterPlanner.PlanTeamTrial(Humans(h), allies, opponents);
                Assert.That(r.Humans + r.FriendlyAi, Is.EqualTo(6), $"H={h}");
                Assert.That(r.OpposingAi, Is.EqualTo(6));
                Assert.That(r.Vehicles, Is.EqualTo(12));
                Assert.That(r.Entries.Where(e => e.Kind == ActorKind.Ai).All(e => !e.DriverId.StartsWith("acct-")), Is.True, "AI never carry account identities");
            }
            Assert.Throws<ArgumentException>(() => RosterPlanner.PlanTeamTrial(Humans(1), allies.Take(3).ToArray(), opponents));
        }

        // ---------------- drift scoring ----------------

        [TestCase(0.0, 0.0)]
        [TestCase(9.99, 0.0)]
        [TestCase(10.0, 0.0)]
        [TestCase(17.5, 0.5)]
        [TestCase(25.0, 1.0)]
        [TestCase(35.0, 1.125)]
        [TestCase(45.0, 1.25)]
        [TestCase(62.5, 0.625)]
        [TestCase(80.0, 0.0)]
        [TestCase(120.0, 0.0)]
        [TestCase(-25.0, 1.0)]
        public void AngleFactor_DocumentedCurve(double slip, double expected)
        {
            Assert.That(DriftScorer.AngleFactor(slip), Is.EqualTo(expected).Within(1e-9));
        }

        [Test]
        public void AngleFactor_IsContinuous()
        {
            double prev = DriftScorer.AngleFactor(0);
            for (double a = 0.01; a <= 90; a += 0.01)
            {
                double f = DriftScorer.AngleFactor(a);
                Assert.That(Math.Abs(f - prev), Is.LessThan(0.002), $"jump at {a}°");
                prev = f;
            }
        }

        [Test]
        public void LineFactor_RangesFromOnePointTwoFiveToZeroPointEight()
        {
            Assert.That(DriftScorer.LineFactor(0, 2), Is.EqualTo(1.25).Within(1e-9));
            Assert.That(DriftScorer.LineFactor(1, 2), Is.EqualTo(1.025).Within(1e-9));
            Assert.That(DriftScorer.LineFactor(2, 2), Is.EqualTo(0.8).Within(1e-9));
            Assert.That(DriftScorer.LineFactor(-9, 2), Is.EqualTo(0.8).Within(1e-9));
        }

        static DriftSample Drift(double progress, int zone = 1, float slip = 25f, float speed = 60f) => new DriftSample
        {
            DeltaSeconds = 1f / 60f, SpeedKmh = speed, SlipAngleDegrees = slip, ProgressMetres = progress,
            MovingInLegalDirection = true, OnRoad = true, JudgedZone = zone, LineOffsetMetres = 2f, LineToleranceMetres = 2f,
        };

        [Test]
        public void Drift_ForwardProgressScoresPerFormula()
        {
            var s = new DriftScorer();
            s.Step(Drift(0));
            DriftStepResult r = s.Step(Drift(1.0));
            // 1 m × 100 × angle 1.0 × line 0.8 × chain 1.0
            Assert.That(r.RawDelta, Is.EqualTo(80.0).Within(1e-9));
        }

        [Test]
        public void Drift_StationaryDonutScoresNothing()
        {
            var s = new DriftScorer();
            for (int i = 0; i < 600; i++)
                s.Step(Drift(10.0, slip: 60f, speed: 20f));
            Assert.That(s.EarnedRaw, Is.EqualTo(0));
        }

        [Test]
        public void Drift_ReenteringTheSameProgressScoresNothingNew()
        {
            var s = new DriftScorer();
            for (int i = 0; i <= 100; i++) s.Step(Drift(i));
            double first = s.EarnedRaw;
            Assert.That(first, Is.GreaterThan(0));
            // Reset back to a checkpoint at 50 m (chain lost), then drift the same stretch again.
            s.Step(new DriftSample { DeltaSeconds = 1f / 60f, Reset = true, ProgressMetres = 50, OnRoad = true });
            for (int i = 50; i <= 100; i++) s.Step(Drift(i));
            Assert.That(s.EarnedRaw, Is.EqualTo(first).Within(1e-9));
            s.Step(Drift(101));
            Assert.That(s.EarnedRaw, Is.GreaterThan(first));
        }

        [Test]
        public void Drift_WrongWayOrSlowSamplesDoNotScore()
        {
            var s = new DriftScorer();
            DriftSample wrongWay = Drift(5);
            wrongWay.MovingInLegalDirection = false;
            s.Step(wrongWay);
            s.Step(Drift(10, speed: 30f));
            Assert.That(s.EarnedRaw, Is.EqualTo(0));
        }

        [Test]
        public void Drift_ChainMultiplierGrowsPerDistinctZone_CappedAtThree()
        {
            var s = new DriftScorer();
            double p = 0;
            for (int zone = 1; zone <= 12; zone++)
                for (int i = 0; i < 10; i++)
                    s.Step(Drift(p += 1.0, zone));
            Assert.That(s.ChainLength, Is.EqualTo(12));
            Assert.That(s.ChainMultiplier, Is.EqualTo(3.0).Within(1e-9));
        }

        [Test]
        public void Drift_WallImpactLosesOnlyTheUnbankedChain()
        {
            var s = new DriftScorer();
            double p = 0;
            for (int i = 0; i < 60; i++) s.Step(Drift(p += 1));
            s.BankAtSectorEnd();
            double banked = s.BankedRaw;
            for (int i = 0; i < 60; i++) s.Step(Drift(p += 1, zone: 2));
            double unbanked = s.UnbankedRaw;
            DriftSample hit = Drift(p += 1, zone: 2);
            hit.WallImpact = true;
            DriftStepResult r = s.Step(hit);
            Assert.That(r.ChainEnd, Is.EqualTo(ChainEnd.LostWall));
            Assert.That(s.BankedRaw, Is.EqualTo(banked));
            Assert.That(s.LostRaw, Is.EqualTo(unbanked).Within(1e-9));
            Assert.That(s.UnbankedRaw, Is.EqualTo(0));
        }

        [Test]
        public void Drift_BanksAfterTheStraighteningInterval()
        {
            var s = new DriftScorer();
            double p = 0;
            for (int i = 0; i < 30; i++) s.Step(Drift(p += 1));
            DriftSample straight = Drift(p, zone: -1, slip: 2f);
            ChainEnd end = ChainEnd.None;
            for (int i = 0; i < 70 && end == ChainEnd.None; i++)
            {
                straight.ProgressMetres = p += 1;
                end = s.Step(straight).ChainEnd;
            }
            Assert.That(end, Is.EqualTo(ChainEnd.Banked));
            Assert.That(s.UnbankedRaw, Is.EqualTo(0));
            Assert.That(s.BankedRaw, Is.GreaterThan(0));
        }

        [Test]
        public void Drift_ShowcaseIsSeparateFromRaw()
        {
            Assert.That(DriftScorer.ShowcaseScore(10_000, 10), Is.EqualTo(11_000));
            Assert.That(DriftScorer.ShowcaseScore(10_000, 0), Is.EqualTo(10_000));
            Assert.Throws<ArgumentOutOfRangeException>(() => DriftScorer.ShowcaseScore(10_000, 8));
        }

        // ---------------- classification ----------------

        [Test]
        public void Classification_EqualMillisecondsAreAnExplicitTie()
        {
            var placings = RaceClassification.Classify(new[]
            {
                new EntrantFinish { EntrantId = "b", Outcome = RunOutcome.Finished, FinishTimeMicros = 200_000_400 },
                new EntrantFinish { EntrantId = "a", Outcome = RunOutcome.Finished, FinishTimeMicros = 200_000_900 },
                new EntrantFinish { EntrantId = "c", Outcome = RunOutcome.Finished, FinishTimeMicros = 201_000_000 },
                new EntrantFinish { EntrantId = "d", Outcome = RunOutcome.DidNotFinish, LegalProgressMetres = 3000 },
                new EntrantFinish { EntrantId = "e", Outcome = RunOutcome.DisqualifiedDisconnect },
            });
            Assert.That(placings.Single(p => p.EntrantId == "a").Place, Is.EqualTo(1));
            Assert.That(placings.Single(p => p.EntrantId == "b").Place, Is.EqualTo(1));
            Assert.That(placings.Single(p => p.EntrantId == "a").Tied, Is.True);
            Assert.That(placings.Single(p => p.EntrantId == "c").Place, Is.EqualTo(3));
            Assert.That(placings.Single(p => p.EntrantId == "d").Place, Is.EqualTo(4));
            Assert.That(placings.Single(p => p.EntrantId == "e").Place, Is.EqualTo(0));
        }
    }
}
