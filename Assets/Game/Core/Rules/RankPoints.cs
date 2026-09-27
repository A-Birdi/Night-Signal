using System;
using System.Collections.Generic;

namespace NightSignal.Core.Rules
{
    public sealed class RankDefinition
    {
        public RankDefinition(int index, string name, int threshold)
        {
            Index = index;
            Name = name;
            Threshold = threshold;
        }

        public int Index { get; }
        public string Name { get; }
        public int Threshold { get; }
    }

    /// <summary>Finite mastery Rank Points, spec §11. Not Elo; no demotion; no repeatable sources.</summary>
    public static class RankPoints
    {
        public const int NormalFirstClear = 100;
        public const int HardFirstClear = 200;
        public const int BronzeChallenge = 40;
        public const int SilverChallenge = 80;
        public const int GoldChallenge = 120;
        public const int ChallengesPerTier = 25;
        public const int TotalChallenges = 75;

        public const int NormalBudget = Limits.CampaignStages * NormalFirstClear;                 // 3,000
        public const int HardBudget = Limits.CampaignStages * HardFirstClear;                     // 6,000
        public const int ChallengeBudget = ChallengesPerTier * (BronzeChallenge + SilverChallenge + GoldChallenge); // 6,000
        public const int MaximumTotal = NormalBudget + HardBudget + ChallengeBudget;              // 15,000

        static readonly RankDefinition[] Ranks =
        {
            new RankDefinition(0, "New Signal", 0),
            new RankDefinition(1, "Local Line", 200),
            new RankDefinition(2, "Night Runner", 600),
            new RankDefinition(3, "Corner Scholar", 1200),
            new RankDefinition(4, "Pass Specialist", 2400),
            new RankDefinition(5, "Convoy Ace", 4000),
            new RankDefinition(6, "Sector Master", 6500),
            new RankDefinition(7, "Mountain Elite", 9000),
            new RankDefinition(8, "Midnight Vanguard", 12000),
            new RankDefinition(9, "Living Legend", 15000),
        };

        public static IReadOnlyList<RankDefinition> All => Ranks;

        public static int ForChallenge(ChallengeTier tier)
        {
            switch (tier)
            {
                case ChallengeTier.Bronze: return BronzeChallenge;
                case ChallengeTier.Silver: return SilverChallenge;
                case ChallengeTier.Gold: return GoldChallenge;
                default: throw new ArgumentOutOfRangeException(nameof(tier));
            }
        }

        public static long ChallengeCash(ChallengeTier tier)
        {
            switch (tier)
            {
                case ChallengeTier.Bronze: return 3_000;
                case ChallengeTier.Silver: return 8_000;
                case ChallengeTier.Gold: return 15_000;
                default: throw new ArgumentOutOfRangeException(nameof(tier));
            }
        }

        /// <summary>Recomputes RP from the unique first-clear/challenge records. Rejects malformed counts.</summary>
        public static int Total(int normalStagesCleared, int hardStagesCleared, int bronze, int silver, int gold)
        {
            Check(normalStagesCleared, Limits.CampaignStages, nameof(normalStagesCleared));
            Check(hardStagesCleared, Limits.CampaignStages, nameof(hardStagesCleared));
            Check(bronze, ChallengesPerTier, nameof(bronze));
            Check(silver, ChallengesPerTier, nameof(silver));
            Check(gold, ChallengesPerTier, nameof(gold));
            return normalStagesCleared * NormalFirstClear + hardStagesCleared * HardFirstClear +
                   bronze * BronzeChallenge + silver * SilverChallenge + gold * GoldChallenge;
        }

        /// <summary>
        /// Rank for a point total. Living Legend additionally requires all 30 Hard stages and all 75 challenges;
        /// a malformed or imported record that reaches 15,000 without them stays Midnight Vanguard.
        /// </summary>
        public static RankDefinition RankFor(int rankPoints, int hardStagesCleared, int challengesCompleted)
        {
            if (rankPoints < 0 || rankPoints > MaximumTotal)
                throw new ArgumentOutOfRangeException(nameof(rankPoints));
            RankDefinition best = Ranks[0];
            foreach (RankDefinition r in Ranks)
                if (rankPoints >= r.Threshold)
                    best = r;
            if (best.Index == Ranks.Length - 1 &&
                (hardStagesCleared < Limits.CampaignStages || challengesCompleted < TotalChallenges))
                best = Ranks[Ranks.Length - 2];
            return best;
        }

        static void Check(int value, int max, string name)
        {
            if (value < 0 || value > max)
                throw new ArgumentOutOfRangeException(name, $"{name} must be 0..{max}, was {value}");
        }
    }
}
