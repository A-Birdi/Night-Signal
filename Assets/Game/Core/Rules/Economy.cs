using System;

namespace NightSignal.Core.Rules
{
    /// <summary>Server-side facts about one entrant's completed event. Clients never supply these.</summary>
    public sealed class PayoutFacts
    {
        public int AuthoredExpectedSeconds;
        public EventKind Kind;
        public CampaignMode Mode;
        public RunOutcome Outcome;
        /// <summary>1-based legal placing (ties share a placing). Ignored for time trials.</summary>
        public int Placement;
        public bool ReferenceBeaten;
        /// <summary>No meaningful wall impacts, no resets, all checkpoints legal.</summary>
        public bool Clean;
        /// <summary>0, 4 or 8 — from the single valid income utility item.</summary>
        public int UtilityIncomePercent;
        public bool PvPWinnerBonusEligible;
        public long FirstClearBonus;
        public long NewlyCompletedChallengeCash;
        /// <summary>For DNF only: fraction of legal checkpoints crossed (0..1).</summary>
        public double CheckpointFraction;
        /// <summary>For DNF only: server verified meaningful control/progress (not parked).</summary>
        public bool ServerVerifiedActiveProgress;
        public bool TutorialRepeat;
    }

    /// <summary>Itemized payout for results screens and the ledger. Multipliers are in hundredths.</summary>
    public sealed class PayoutBreakdown
    {
        public long Base;
        public int DifficultyX100 = 100;
        public int PlacementX100 = 100;
        public int CleanlinessX100 = 100;
        public int UtilityX100 = 100;
        public int PvPX100 = 100;
        public long EventCredits;
        public long FirstClearBonus;
        public long ChallengeCash;
        public long Total => EventCredits + FirstClearBonus + ChallengeCash;
        public string Note = "";
    }

    /// <summary>Economy formula, spec §10. All arithmetic is exact integer math.</summary>
    public static class Economy
    {
        public const int MinExpectedSeconds = 120;
        public const int MaxExpectedSeconds = 420;
        public const long BaseConstant = 2500;
        public const long BasePerSecond = 22;
        public const double DnfMinimumCheckpointFraction = 0.80;

        /// <summary>
        /// B = 2,500 + 22 × clamp(expected, 120, 420). Short custom configurations below 120 s receive a
        /// proportional reduction of the 120-second value rather than the 120-second minimum:
        /// B = floor((2,500 + 22 × 120) × expected / 120).
        /// </summary>
        public static long BaseCompletion(int authoredExpectedSeconds)
        {
            if (authoredExpectedSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(authoredExpectedSeconds));
            if (authoredExpectedSeconds < MinExpectedSeconds)
                return (BaseConstant + BasePerSecond * MinExpectedSeconds) * authoredExpectedSeconds / MinExpectedSeconds;
            return BaseConstant + BasePerSecond * Math.Min(authoredExpectedSeconds, MaxExpectedSeconds);
        }

        public static int DifficultyX100(EventKind kind, CampaignMode mode) =>
            kind == EventKind.CampaignStage && mode == CampaignMode.Hard ? 135 : 100;

        public static int PlacementX100(int placement)
        {
            if (placement < 1 || placement > Limits.MaxRaceVehicles) // 4th–12th all 1.00 (Addendum 01 §14)
                throw new ArgumentOutOfRangeException(nameof(placement));
            switch (placement)
            {
                case 1: return 135;
                case 2: return 120;
                case 3: return 110;
                default: return 100;
            }
        }

        public static int TimeTrialX100(bool referenceBeaten) => referenceBeaten ? 120 : 100;

        public static int UtilityX100(int utilityIncomePercent)
        {
            if (utilityIncomePercent != 0 && utilityIncomePercent != 4 && utilityIncomePercent != 8)
                throw new ArgumentOutOfRangeException(nameof(utilityIncomePercent), "Only +4% or +8% income utilities exist");
            return 100 + utilityIncomePercent;
        }

        /// <summary>
        /// First-place 1.20 bonus only for pure PvP (zero live AI) with at least two authenticated human
        /// entrants who both legally finished, in an eligible configuration. Never for ghosts/AI/forfeits.
        /// </summary>
        public static bool PvPWinnerBonusEligible(int placement, int liveAiCount, int authenticatedHumansLegallyFinished,
            bool configurationEligible) =>
            placement == 1 && liveAiCount == 0 && authenticatedHumansLegallyFinished >= 2 && configurationEligible;

        public static long FirstClearBonus(StageType type, CampaignMode mode)
        {
            bool hard = mode == CampaignMode.Hard;
            switch (type)
            {
                case StageType.Regular: return hard ? 12_000 : 8_000;
                case StageType.Lieutenant: return hard ? 30_000 : 20_000;
                case StageType.Penultimate: return hard ? 35_000 : 25_000;
                case StageType.Finale: return hard ? 60_000 : 40_000;
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        /// <summary>
        /// Credits = floor(B × difficulty × placement × cleanliness × utility × PvP) + firstClear + challengeCash.
        /// DNF (active, ≥80% checkpoints, server-verified) earns floor(0.25 × B) and nothing else.
        /// Quit, DQ, AFK, invalid runs and tutorial repeats earn zero.
        /// </summary>
        public static PayoutBreakdown Compute(PayoutFacts f)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            if (f.FirstClearBonus < 0 || f.NewlyCompletedChallengeCash < 0)
                throw new ArgumentOutOfRangeException(nameof(f), "Bonuses cannot be negative");

            var b = new PayoutBreakdown();
            if (f.TutorialRepeat)
            {
                b.Note = "Tutorial repetitions do not pay.";
                return b;
            }

            long baseCompletion = BaseCompletion(f.AuthoredExpectedSeconds);
            switch (f.Outcome)
            {
                case RunOutcome.Finished:
                    break;
                case RunOutcome.DidNotFinish:
                    b.Base = baseCompletion;
                    if (f.CheckpointFraction >= DnfMinimumCheckpointFraction && f.ServerVerifiedActiveProgress)
                    {
                        b.EventCredits = baseCompletion / 4;
                        b.Note = "Did not finish: completion allowance (25% of base).";
                    }
                    else
                    {
                        b.Note = "Did not finish: below 80% of checkpoints or no verified progress.";
                    }
                    return b;
                default:
                    b.Note = "No payout for quit, disqualification, AFK or invalid runs.";
                    return b;
            }

            b.Base = baseCompletion;
            b.DifficultyX100 = DifficultyX100(f.Kind, f.Mode);
            b.PlacementX100 = f.Kind == EventKind.FreeplayTimeTrial ? TimeTrialX100(f.ReferenceBeaten) : PlacementX100(f.Placement);
            b.CleanlinessX100 = f.Clean ? 105 : 100;
            b.UtilityX100 = UtilityX100(f.UtilityIncomePercent);
            b.PvPX100 = f.PvPWinnerBonusEligible ? 120 : 100;

            long numerator = baseCompletion * b.DifficultyX100 * b.PlacementX100 * b.CleanlinessX100 * b.UtilityX100 * b.PvPX100;
            b.EventCredits = numerator / 10_000_000_000L; // five factors of 100
            b.FirstClearBonus = f.FirstClearBonus;
            b.ChallengeCash = f.NewlyCompletedChallengeCash;
            return b;
        }
    }

    /// <summary>Result of applying a credit to a capped wallet.</summary>
    public struct WalletCredit
    {
        public long NewBalance;
        public long Credited;
        public long ClampedAway;
    }

    public static class Wallet
    {
        public static WalletCredit Credit(long balance, long amount)
        {
            if (balance < 0 || balance > Limits.WalletCap) throw new ArgumentOutOfRangeException(nameof(balance));
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            long room = Limits.WalletCap - balance;
            long credited = Math.Min(room, amount);
            return new WalletCredit { NewBalance = balance + credited, Credited = credited, ClampedAway = amount - credited };
        }

        /// <summary>Returns false (and leaves the balance) when funds are insufficient.</summary>
        public static bool TryDebit(long balance, long price, out long newBalance)
        {
            if (balance < 0 || balance > Limits.WalletCap) throw new ArgumentOutOfRangeException(nameof(balance));
            if (price < 0) throw new ArgumentOutOfRangeException(nameof(price));
            if (price > balance)
            {
                newBalance = balance;
                return false;
            }
            newBalance = balance - price;
            return true;
        }
    }
}
