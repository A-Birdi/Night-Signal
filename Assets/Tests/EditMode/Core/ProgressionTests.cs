using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    public sealed class ProgressionTests
    {
        static bool[] Cleared(int contiguousPrefix)
        {
            var flags = new bool[Limits.CampaignStages];
            for (int i = 0; i < contiguousPrefix; i++) flags[i] = true;
            return flags;
        }

        static MemberProgress Member(string id, int normalFrontier, int hardFrontier = 1) =>
            new MemberProgress(id, Cleared(normalFrontier - 1), Cleared(hardFrontier - 1));

        [Test]
        public void RankBudget_IsExactlyFifteenThousand()
        {
            Assert.That(RankPoints.NormalBudget, Is.EqualTo(3_000));
            Assert.That(RankPoints.HardBudget, Is.EqualTo(6_000));
            Assert.That(RankPoints.ChallengeBudget, Is.EqualTo(6_000));
            Assert.That(RankPoints.MaximumTotal, Is.EqualTo(15_000));
            Assert.That(RankPoints.Total(30, 30, 25, 25, 25), Is.EqualTo(15_000));
        }

        [TestCase(0, "New Signal")]
        [TestCase(199, "New Signal")]
        [TestCase(200, "Local Line")]
        [TestCase(600, "Night Runner")]
        [TestCase(1_199, "Night Runner")]
        [TestCase(1_200, "Corner Scholar")]
        [TestCase(2_400, "Pass Specialist")]
        [TestCase(4_000, "Convoy Ace")]
        [TestCase(6_500, "Sector Master")]
        [TestCase(9_000, "Mountain Elite")]
        [TestCase(12_000, "Midnight Vanguard")]
        [TestCase(14_999, "Midnight Vanguard")]
        public void RankThresholds(int rp, string expected)
        {
            Assert.That(RankPoints.RankFor(rp, 0, 0).Name, Is.EqualTo(expected));
        }

        [Test]
        public void LivingLegend_RequiresAllHardStagesAndAllChallenges()
        {
            Assert.That(RankPoints.RankFor(15_000, 30, 75).Name, Is.EqualTo("Living Legend"));
            Assert.That(RankPoints.RankFor(15_000, 29, 75).Name, Is.EqualTo("Midnight Vanguard"));
            Assert.That(RankPoints.RankFor(15_000, 30, 74).Name, Is.EqualTo("Midnight Vanguard"));
        }

        [Test]
        public void RankPoints_RejectMalformedRecords()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RankPoints.Total(31, 0, 0, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RankPoints.Total(0, 0, 26, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RankPoints.Total(-1, 0, 0, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RankPoints.RankFor(15_001, 30, 75));
        }

        [Test]
        public void Frontier_IsOnePlusContiguousPrefix()
        {
            Assert.That(CampaignProgress.Frontier(Cleared(0)), Is.EqualTo(1));
            Assert.That(CampaignProgress.Frontier(Cleared(7)), Is.EqualTo(8));
            Assert.That(CampaignProgress.Frontier(Cleared(30)), Is.EqualTo(31));
            bool[] gap = Cleared(2);
            gap[3] = true; // S04 cleared without S03 (malformed) does not advance past the gap
            Assert.That(CampaignProgress.Frontier(gap), Is.EqualTo(3));
        }

        [Test]
        public void ConvoyAccess_SpecExamples()
        {
            ConvoyStageAccess a = CampaignProgress.Evaluate(CampaignMode.Normal, new[] { Member("a", 1), Member("b", 1) });
            Assert.That(a.MaxSelectableStage, Is.EqualTo(1));
            Assert.That(a.CanSelect(1) && !a.CanSelect(2), Is.True);

            ConvoyStageAccess b = CampaignProgress.Evaluate(CampaignMode.Normal, new[] { Member("a", 8), Member("b", 12), Member("c", 31) });
            Assert.That(b.MaxSelectableStage, Is.EqualTo(8));
            Assert.That(b.LimitingPlayers, Is.EquivalentTo(new[] { "a" }));
            Assert.That(b.Explanation, Is.EqualTo("Next shared stage: S08 — one member has not cleared it."));

            ConvoyStageAccess c = CampaignProgress.Evaluate(CampaignMode.Normal, new[] { Member("a", 31), Member("b", 31) });
            Assert.That(c.MaxSelectableStage, Is.EqualTo(30));
            Assert.That(Enumerable.Range(1, 30).All(c.CanSelect), Is.True);
        }

        [Test]
        public void ConvoyAccess_TwoLimitingMembersAreNamedNeutrally()
        {
            ConvoyStageAccess a = CampaignProgress.Evaluate(CampaignMode.Normal, new[] { Member("a", 8), Member("b", 8), Member("c", 12) });
            Assert.That(a.Explanation, Is.EqualTo("Next shared stage: S08 — two members have not cleared it."));
        }

        [Test]
        public void HardMode_DeniedIfAnyMemberLacksNormalFinale()
        {
            ConvoyStageAccess denied = CampaignProgress.Evaluate(CampaignMode.Hard, new[] { Member("a", 31), Member("b", 30) });
            Assert.That(denied.ModeAllowed, Is.False);
            Assert.That(denied.CanSelect(1), Is.False);
            Assert.That(denied.LimitingPlayers, Is.EquivalentTo(new[] { "b" }));

            ConvoyStageAccess allowed = CampaignProgress.Evaluate(CampaignMode.Hard, new[] { Member("a", 31, 5), Member("b", 31, 1) });
            Assert.That(allowed.ModeAllowed, Is.True);
            Assert.That(allowed.MaxSelectableStage, Is.EqualTo(1));
        }

        [Test]
        public void ConvoyOfSeven_IsRejected()
        {
            var seven = Enumerable.Range(0, 7).Select(i => Member("p" + i, 1)).ToArray();
            Assert.Throws<ArgumentException>(() => CampaignProgress.Evaluate(CampaignMode.Normal, seven));
        }

        [Test]
        public void ApplyClear_ReplayNeverDoubleAwardsAndCannotSkipAhead()
        {
            bool[] flags = Cleared(7);
            Assert.That(CampaignProgress.ApplyClear(flags, 8), Is.True, "first clear of the frontier stage");
            Assert.That(CampaignProgress.Frontier(flags), Is.EqualTo(9));
            Assert.That(CampaignProgress.ApplyClear(flags, 8), Is.False, "replay is not a new first clear");
            Assert.That(CampaignProgress.ApplyClear(flags, 3), Is.False);
            Assert.Throws<InvalidOperationException>(() => CampaignProgress.ApplyClear(flags, 11));
        }

        [TestCase(CampaignMode.Normal, 1, 1)]
        [TestCase(CampaignMode.Normal, 6, 1)]
        [TestCase(CampaignMode.Hard, 1, 1)]
        [TestCase(CampaignMode.Hard, 2, 1)]
        [TestCase(CampaignMode.Hard, 3, 2)]
        [TestCase(CampaignMode.Hard, 4, 2)]
        [TestCase(CampaignMode.Hard, 5, 3)]
        [TestCase(CampaignMode.Hard, 6, 3)]
        public void RequiredQualifiers(CampaignMode mode, int humans, int required)
        {
            Assert.That(StageOutcome.RequiredQualifiers(mode, humans), Is.EqualTo(required));
        }

        static HumanStageResult Finish(string id, long ms) =>
            new HumanStageResult { PlayerId = id, Outcome = RunOutcome.Finished, ActivelyDroveLegalCourse = true, FinishTimeMs = ms };

        [Test]
        public void NormalTeamSuccess_SupportersWithinEnvelopeClear_OthersDoNot()
        {
            var bench = new StageBenchmark { Kind = BenchmarkKind.Time, TargetTimeMs = 200_000, HardTimeoutMs = 400_000 };
            var results = new List<HumanStageResult>
            {
                Finish("fast", 199_000),
                Finish("support", 300_000),   // exactly 1.50×
                Finish("slow", 300_001),
                new HumanStageResult { PlayerId = "dq", Outcome = RunOutcome.DisqualifiedDisconnect },
            };
            StageResolution r = StageOutcome.Resolve(CampaignMode.Normal, bench, 4, results);
            Assert.That(r.TeamSuccess, Is.True);
            Assert.That(r.Players.Single(p => p.PlayerId == "fast").EarnedClear, Is.True);
            Assert.That(r.Players.Single(p => p.PlayerId == "support").EarnedClear, Is.True);
            Assert.That(r.Players.Single(p => p.PlayerId == "slow").EarnedClear, Is.False);
            Assert.That(r.Players.Single(p => p.PlayerId == "dq").EarnedClear, Is.False, "DQ never grants progression");
        }

        [Test]
        public void HardTeamGoal_DqsDoNotLowerTheThreshold()
        {
            var bench = new StageBenchmark { Kind = BenchmarkKind.Time, TargetTimeMs = 200_000, HardTimeoutMs = 400_000 };
            var results = new List<HumanStageResult>
            {
                Finish("a", 190_000),
                new HumanStageResult { PlayerId = "b", Outcome = RunOutcome.DisqualifiedDisconnect },
                new HumanStageResult { PlayerId = "c", Outcome = RunOutcome.Quit },
            };
            StageResolution r = StageOutcome.Resolve(CampaignMode.Hard, bench, 3, results);
            Assert.That(r.RequiredQualifiers, Is.EqualTo(2));
            Assert.That(r.TeamSuccess, Is.False);
            Assert.That(r.Players.Any(p => p.EarnedClear), Is.False);
        }

        [Test]
        public void Resolve_RequiresAResultForEveryFrozenHuman()
        {
            var bench = new StageBenchmark { Kind = BenchmarkKind.Time, TargetTimeMs = 1000, HardTimeoutMs = 2000 };
            Assert.Throws<ArgumentException>(() => StageOutcome.Resolve(CampaignMode.Normal, bench, 2, new[] { Finish("a", 900) }));
        }

        [Test]
        public void AfkFinisher_DoesNotClear()
        {
            var bench = new StageBenchmark { Kind = BenchmarkKind.Time, TargetTimeMs = 200_000, HardTimeoutMs = 400_000 };
            var parked = Finish("parked", 150_000);
            parked.ActivelyDroveLegalCourse = false;
            StageResolution r = StageOutcome.Resolve(CampaignMode.Normal, bench, 2, new[] { Finish("a", 150_000), parked });
            Assert.That(r.Players.Single(p => p.PlayerId == "parked").EarnedClear, Is.False);
        }

        [Test]
        public void DriftBenchmarks_UseRawScoreSupportPercentages()
        {
            var bench = new StageBenchmark { Kind = BenchmarkKind.RawDrift, RawDriftTarget = 10_000, HardTimeoutMs = 1 };
            var h = new HumanStageResult { PlayerId = "a", Outcome = RunOutcome.Finished, ActivelyDroveLegalCourse = true, RawDriftScore = 6_000 };
            var q = new HumanStageResult { PlayerId = "q", Outcome = RunOutcome.Finished, ActivelyDroveLegalCourse = true, RawDriftScore = 10_000 };
            Assert.That(StageOutcome.Resolve(CampaignMode.Normal, bench, 2, new[] { q, h }).Players[1].EarnedClear, Is.True, "60% ≥ 50%");
            Assert.That(StageOutcome.Resolve(CampaignMode.Hard, bench, 2, new[] { q, h }).Players[1].EarnedClear, Is.False, "60% < 65% (and Hard H=2 needs one qualifier)");
        }

        [Test]
        public void FourContracts_QualifyOnAllFour_SupportOnThreeInTime()
        {
            var bench = new StageBenchmark { Kind = BenchmarkKind.FourContracts, TargetTimeMs = 400_000, HardTimeoutMs = 700_000 };
            HumanStageResult Contracts(string id, int passed, long ms)
            {
                HumanStageResult r = Finish(id, ms);
                r.ContractsPassed = passed;
                return r;
            }
            StageResolution res = StageOutcome.Resolve(CampaignMode.Normal, bench, 3,
                new[] { Contracts("all", 4, 390_000), Contracts("three", 3, 590_000), Contracts("two", 2, 395_000) });
            Assert.That(res.Players[0].Qualified, Is.True);
            Assert.That(res.Players[1].EarnedClear, Is.True);
            Assert.That(res.Players[2].EarnedClear, Is.False);
        }

        [Test]
        public void Deadline_IsMaxOfGraceAndEnvelope_BoundedByHardTimeout()
        {
            Assert.That(StageOutcome.DeadlineMs(200_000, 270_000, 400_000), Is.EqualTo(290_000));
            Assert.That(StageOutcome.DeadlineMs(100_000, 270_000, 400_000), Is.EqualTo(270_000));
            Assert.That(StageOutcome.DeadlineMs(200_000, 270_000, 285_000), Is.EqualTo(285_000));
            Assert.Throws<ArgumentException>(() => StageOutcome.DeadlineMs(0, 270_000, 260_000));
        }

        [TestCase(99)]
        [TestCase(1000)]
        public void PerformanceIndex_RejectsOutOfRange(int pi)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceIndex.ClassOf(pi));
        }

        [TestCase(100, PerformanceClass.D)]
        [TestCase(299, PerformanceClass.D)]
        [TestCase(300, PerformanceClass.C)]
        [TestCase(499, PerformanceClass.C)]
        [TestCase(500, PerformanceClass.B)]
        [TestCase(699, PerformanceClass.B)]
        [TestCase(700, PerformanceClass.A)]
        [TestCase(849, PerformanceClass.A)]
        [TestCase(850, PerformanceClass.S)]
        [TestCase(999, PerformanceClass.S)]
        public void PerformanceIndex_Classes(int pi, PerformanceClass cls)
        {
            Assert.That(PerformanceIndex.ClassOf(pi), Is.EqualTo(cls));
        }
    }
}
