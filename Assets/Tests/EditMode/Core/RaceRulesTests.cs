using System;
using System.Linq;
using NightSignal.Core.Rules;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    public sealed class RaceRulesTests
    {
        // ---------------- six-entrant cap ----------------

        [Test]
        public void Freeplay_EveryLegalCombinationIsAccepted()
        {
            for (int humans = 1; humans <= 6; humans++)
            for (int ai = 0; ai <= 6 - humans; ai++)
            {
                FreeplayGrid g = GridPlanner.ValidateFreeplay(humans, ai);
                Assert.That(g.WasClamped, Is.False, $"H={humans} AI={ai}");
                Assert.That(g.Humans + g.LiveAi, Is.LessThanOrEqualTo(Limits.MaxRaceEntrants));
            }
        }

        [Test]
        public void Freeplay_OverCapAiIsClampedWithExplanation_HumansNeverEjected()
        {
            FreeplayGrid g = GridPlanner.ValidateFreeplay(4, 5);
            Assert.That(g.Humans, Is.EqualTo(4));
            Assert.That(g.LiveAi, Is.EqualTo(2));
            Assert.That(g.WasClamped, Is.True);
            Assert.That(g.Explanation, Does.Contain("six entrants"));
            Assert.Throws<ArgumentOutOfRangeException>(() => GridPlanner.ValidateFreeplay(7, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => GridPlanner.ValidateFreeplay(0, 3));
        }

        [Test]
        public void Freeplay_Flavors()
        {
            Assert.That(GridPlanner.ValidateFreeplay(2, 0).Flavor, Is.EqualTo(FreeplayFlavor.PurePvP));
            Assert.That(GridPlanner.ValidateFreeplay(1, 0).Flavor, Is.EqualTo(FreeplayFlavor.TimeTrial));
            Assert.That(GridPlanner.ValidateFreeplay(1, 5).Flavor, Is.EqualTo(FreeplayFlavor.SoloVersusAi));
            Assert.That(GridPlanner.ValidateFreeplay(3, 1).Flavor, Is.EqualTo(FreeplayFlavor.MixedGrid));
        }

        [Test]
        public void Campaign_FillsFeaturedRivalFirstUpToSix()
        {
            string[] pool = { "R05", "R06", "R07", "R02" };
            for (int humans = 1; humans <= 5; humans++)
            {
                CampaignGrid g = GridPlanner.PlanCampaign(humans, "R01", pool);
                Assert.That(g.Entrants, Is.EqualTo(6), $"H={humans}");
                Assert.That(g.LiveAiRivals[0], Is.EqualTo("R01"));
                Assert.That(g.BenchmarkReplayRival, Is.Null);
            }
            Assert.That(GridPlanner.PlanCampaign(1, "R01", pool).LiveAiRivals, Is.EqualTo(new[] { "R01", "R05", "R06", "R07", "R02" }));
        }

        [Test]
        public void Campaign_SixHumansGetLabelledReplay_NeverASeventhRacer()
        {
            CampaignGrid g = GridPlanner.PlanCampaign(6, "R40", new[] { "R33", "R34" });
            Assert.That(g.LiveAiRivals, Is.Empty);
            Assert.That(g.Entrants, Is.EqualTo(6));
            Assert.That(g.BenchmarkReplayRival, Is.EqualTo("R40"));
            Assert.That(GridPlanner.BenchmarkReplayLabel, Is.EqualTo("Rival benchmark — replay, not an entrant"));
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
