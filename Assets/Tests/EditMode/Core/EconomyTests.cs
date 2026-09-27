using System;
using NightSignal.Core.Rules;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    public sealed class EconomyTests
    {
        [TestCase(180, 6460)]
        [TestCase(120, 5140)]
        [TestCase(420, 11740)]
        [TestCase(600, 11740)]   // clamped at 420
        [TestCase(60, 2570)]     // short custom: proportional reduction of the 120 s value
        [TestCase(1, 42)]
        public void BaseCompletion_FollowsFormula(int expectedSeconds, long expectedBase)
        {
            Assert.That(Economy.BaseCompletion(expectedSeconds), Is.EqualTo(expectedBase));
        }

        [Test]
        public void BaseCompletion_RejectsNonPositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Economy.BaseCompletion(0));
        }

        [Test]
        public void Compute_HardWinCleanWithUtility_UsesExactIntegerMath()
        {
            var facts = new PayoutFacts
            {
                AuthoredExpectedSeconds = 180, Kind = EventKind.CampaignStage, Mode = CampaignMode.Hard,
                Outcome = RunOutcome.Finished, Placement = 1, Clean = true, UtilityIncomePercent = 8,
                FirstClearBonus = 12_000, NewlyCompletedChallengeCash = 3_000,
            };
            PayoutBreakdown b = Economy.Compute(facts);
            // floor(6460 × 1.35 × 1.35 × 1.05 × 1.08) = floor(13350.97...) = 13350
            Assert.That(b.EventCredits, Is.EqualTo(13_350));
            Assert.That(b.Total, Is.EqualTo(13_350 + 12_000 + 3_000));
            Assert.That(b.DifficultyX100, Is.EqualTo(135));
            Assert.That(b.PlacementX100, Is.EqualTo(135));
        }

        [TestCase(1, 135)]
        [TestCase(2, 120)]
        [TestCase(3, 110)]
        [TestCase(4, 100)]
        [TestCase(6, 100)]
        public void Placement_Multipliers(int place, int x100)
        {
            Assert.That(Economy.PlacementX100(place), Is.EqualTo(x100));
        }

        [Test]
        public void Placement_OutOfRange_Throws()
        {
            for (int place = 4; place <= 12; place++) Assert.That(Economy.PlacementX100(place), Is.EqualTo(100), $"place {place}");
            Assert.Throws<ArgumentOutOfRangeException>(() => Economy.PlacementX100(13));
            Assert.Throws<ArgumentOutOfRangeException>(() => Economy.PlacementX100(0));
        }

        [Test]
        public void LosingLegalFinish_StillEarnsFullBase()
        {
            PayoutBreakdown b = Economy.Compute(new PayoutFacts
            {
                AuthoredExpectedSeconds = 180, Kind = EventKind.CampaignStage, Mode = CampaignMode.Normal,
                Outcome = RunOutcome.Finished, Placement = 6,
            });
            Assert.That(b.EventCredits, Is.EqualTo(6460));
        }

        [Test]
        public void TimeTrial_UsesPerformanceBands()
        {
            var f = new PayoutFacts { AuthoredExpectedSeconds = 180, Kind = EventKind.FreeplayTimeTrial, Outcome = RunOutcome.Finished, ReferenceBeaten = true };
            Assert.That(Economy.Compute(f).EventCredits, Is.EqualTo(7752)); // 6460 × 1.20
            f.ReferenceBeaten = false;
            Assert.That(Economy.Compute(f).EventCredits, Is.EqualTo(6460));
        }

        [Test]
        public void Dnf_PaysQuarterBaseOnlyWhenActiveAndPast80Percent()
        {
            var f = new PayoutFacts
            {
                AuthoredExpectedSeconds = 180, Kind = EventKind.CampaignStage, Outcome = RunOutcome.DidNotFinish,
                CheckpointFraction = 0.85, ServerVerifiedActiveProgress = true, Placement = 1, Clean = true,
                UtilityIncomePercent = 8, FirstClearBonus = 8000, NewlyCompletedChallengeCash = 3000,
            };
            PayoutBreakdown b = Economy.Compute(f);
            Assert.That(b.EventCredits, Is.EqualTo(1615));
            Assert.That(b.Total, Is.EqualTo(1615), "No multipliers, first clear or challenge cash for a DNF");

            f.CheckpointFraction = 0.79;
            Assert.That(Economy.Compute(f).Total, Is.EqualTo(0));
            f.CheckpointFraction = 0.95;
            f.ServerVerifiedActiveProgress = false;
            Assert.That(Economy.Compute(f).Total, Is.EqualTo(0));
        }

        [TestCase(RunOutcome.Quit)]
        [TestCase(RunOutcome.DisqualifiedDisconnect)]
        [TestCase(RunOutcome.DisqualifiedAfk)]
        [TestCase(RunOutcome.DisqualifiedInvalid)]
        public void QuitAndDisqualification_EarnNothing(RunOutcome outcome)
        {
            PayoutBreakdown b = Economy.Compute(new PayoutFacts
            {
                AuthoredExpectedSeconds = 420, Outcome = outcome, Placement = 1, FirstClearBonus = 60_000, NewlyCompletedChallengeCash = 15_000,
            });
            Assert.That(b.Total, Is.EqualTo(0));
        }

        [Test]
        public void TutorialRepeat_PaysNothing()
        {
            Assert.That(Economy.Compute(new PayoutFacts { AuthoredExpectedSeconds = 180, Outcome = RunOutcome.Finished, Placement = 1, TutorialRepeat = true }).Total, Is.EqualTo(0));
        }

        [Test]
        public void PvPBonus_OnlyForPurePvPWithTwoLegalHumanFinishers()
        {
            Assert.That(Economy.PvPWinnerBonusEligible(1, 0, 2, true), Is.True);
            Assert.That(Economy.PvPWinnerBonusEligible(1, 1, 2, true), Is.False, "live AI present");
            Assert.That(Economy.PvPWinnerBonusEligible(1, 0, 1, true), Is.False, "last car left after everyone quit");
            Assert.That(Economy.PvPWinnerBonusEligible(2, 0, 2, true), Is.False, "not first");
            Assert.That(Economy.PvPWinnerBonusEligible(1, 0, 2, false), Is.False, "ineligible configuration");
        }

        [Test]
        public void UtilityOnlyAllowsFourOrEightPercent()
        {
            Assert.That(Economy.UtilityX100(0), Is.EqualTo(100));
            Assert.That(Economy.UtilityX100(4), Is.EqualTo(104));
            Assert.That(Economy.UtilityX100(8), Is.EqualTo(108));
            Assert.Throws<ArgumentOutOfRangeException>(() => Economy.UtilityX100(12));
        }

        [Test]
        public void FirstClearBonuses_MatchSpecification()
        {
            Assert.That(Economy.FirstClearBonus(StageType.Regular, CampaignMode.Normal), Is.EqualTo(8_000));
            Assert.That(Economy.FirstClearBonus(StageType.Regular, CampaignMode.Hard), Is.EqualTo(12_000));
            Assert.That(Economy.FirstClearBonus(StageType.Lieutenant, CampaignMode.Normal), Is.EqualTo(20_000));
            Assert.That(Economy.FirstClearBonus(StageType.Lieutenant, CampaignMode.Hard), Is.EqualTo(30_000));
            Assert.That(Economy.FirstClearBonus(StageType.Penultimate, CampaignMode.Normal), Is.EqualTo(25_000));
            Assert.That(Economy.FirstClearBonus(StageType.Penultimate, CampaignMode.Hard), Is.EqualTo(35_000));
            Assert.That(Economy.FirstClearBonus(StageType.Finale, CampaignMode.Normal), Is.EqualTo(40_000));
            Assert.That(Economy.FirstClearBonus(StageType.Finale, CampaignMode.Hard), Is.EqualTo(60_000));
        }

        [Test]
        public void Wallet_ClampsAtCapWithoutOverflow()
        {
            WalletCredit c = Wallet.Credit(9_999_998, 5_000);
            Assert.That(c.NewBalance, Is.EqualTo(9_999_999));
            Assert.That(c.Credited, Is.EqualTo(1));
            Assert.That(c.ClampedAway, Is.EqualTo(4_999));
            Assert.That(Wallet.Credit(9_999_999, long.MaxValue / 2).NewBalance, Is.EqualTo(9_999_999));
        }

        [Test]
        public void Wallet_RejectsNegativeAndInvalidAmounts()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Wallet.Credit(0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Wallet.Credit(10_000_000, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Wallet.TryDebit(100, -5, out _));
            Assert.That(Wallet.TryDebit(100, 150, out long unchanged), Is.False);
            Assert.That(unchanged, Is.EqualTo(100));
            Assert.That(Wallet.TryDebit(100, 100, out long empty), Is.True);
            Assert.That(empty, Is.EqualTo(0));
        }
    }
}
