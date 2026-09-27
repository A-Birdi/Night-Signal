using System;

namespace NightSignal.Core.Rules
{
    /// <summary>Where an AI driver is being placed; used to enforce campaign-finale-only rivals server-side.</summary>
    public enum AiPlacementContext
    {
        CampaignEncounter = 0,
        FreeplayOpponent = 1,
        RandomPool = 2,
        SupportFiller = 3,
        FriendlyAi = 4,
        TeamTrial = 5,
        CupSubstitution = 6,
        ReplayTarget = 7,
        ChallengeOpponent = 8,
    }

    /// <summary>
    /// R40 Reina Kurogane and R48 Shiori Kuze race ONLY in their own Normal/Hard S30 finale (Addendum 01 §12),
    /// including after being defeated. Every other placement — Freeplay, random pools, supports, friendly AI,
    /// Team Trials, Cup substitutions, ordinary ghost targets, challenge opponents — is rejected here, so a forged ID
    /// fails even when the menu never showed that portrait.
    /// </summary>
    public static class FinalRivals
    {
        public const string NormalFinal = "R40";
        public const string HardFinal = "R48";
        public const string FinaleStage = "S30";

        public static bool IsFinaleOnly(string rivalId) => rivalId == NormalFinal || rivalId == HardFinal;

        /// <summary>True when <paramref name="rivalId"/> may be placed in this context.</summary>
        public static bool Allowed(string rivalId, AiPlacementContext context, string stageId = null, CampaignMode mode = CampaignMode.Normal)
        {
            if (!IsFinaleOnly(rivalId)) return true;
            if (context != AiPlacementContext.CampaignEncounter || stageId != FinaleStage) return false;
            return mode == CampaignMode.Normal ? rivalId == NormalFinal : rivalId == HardFinal;
        }

        public static void Require(string rivalId, AiPlacementContext context, string stageId = null, CampaignMode mode = CampaignMode.Normal)
        {
            if (!Allowed(rivalId, context, stageId, mode))
                throw new ArgumentException($"{rivalId} is a campaign-finale-only rival and cannot be placed as {context}.");
        }
    }
}
