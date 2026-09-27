using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    /// <summary>One convoy member's mode-specific campaign state, as stored by the server.</summary>
    public sealed class MemberProgress
    {
        public MemberProgress(string playerId, bool[] normalCleared, bool[] hardCleared)
        {
            PlayerId = playerId ?? throw new ArgumentNullException(nameof(playerId));
            NormalCleared = Validate(normalCleared);
            HardCleared = Validate(hardCleared);
        }

        public string PlayerId { get; }
        /// <summary>Index 0 = S01. Length 30.</summary>
        public bool[] NormalCleared { get; }
        public bool[] HardCleared { get; }

        public bool[] Cleared(CampaignMode mode) => mode == CampaignMode.Normal ? NormalCleared : HardCleared;

        /// <summary>Hard unlocks per player after their first legitimate Normal finale clear.</summary>
        public bool HardUnlocked => NormalCleared[Limits.CampaignStages - 1] && CampaignProgress.Frontier(NormalCleared) > Limits.CampaignStages;

        static bool[] Validate(bool[] flags)
        {
            if (flags == null || flags.Length != Limits.CampaignStages)
                throw new ArgumentException($"Expected {Limits.CampaignStages} stage flags");
            return flags;
        }
    }

    public sealed class ConvoyStageAccess
    {
        public bool ModeAllowed;
        /// <summary>Highest selectable stage number (1..30); 0 when the mode is not allowed.</summary>
        public int MaxSelectableStage;
        public IReadOnlyList<string> LimitingPlayers = Array.Empty<string>();
        public string Explanation = "";

        public bool CanSelect(int stageNumber) => ModeAllowed && stageNumber >= 1 && stageNumber <= MaxSelectableStage;
    }

    /// <summary>Ordered campaign frontier rules, spec §5.1.</summary>
    public static class CampaignProgress
    {
        /// <summary>One plus the largest contiguous cleared prefix; 31 when all 30 stages are cleared.</summary>
        public static int Frontier(IReadOnlyList<bool> cleared)
        {
            if (cleared == null || cleared.Count != Limits.CampaignStages)
                throw new ArgumentException($"Expected {Limits.CampaignStages} stage flags");
            int prefix = 0;
            while (prefix < cleared.Count && cleared[prefix])
                prefix++;
            return prefix + 1;
        }

        public static ConvoyStageAccess Evaluate(CampaignMode mode, IReadOnlyList<MemberProgress> members)
        {
            if (members == null || members.Count == 0)
                throw new ArgumentException("A convoy needs at least one member");
            if (members.Count > Limits.MaxConvoyHumans)
                throw new ArgumentException($"A convoy holds at most {Limits.MaxConvoyHumans} members");

            var access = new ConvoyStageAccess();
            if (mode == CampaignMode.Hard)
            {
                string[] locked = members.Where(m => !m.HardUnlocked).Select(m => m.PlayerId).ToArray();
                if (locked.Length > 0)
                {
                    access.LimitingPlayers = locked;
                    access.Explanation = locked.Length == 1
                        ? "Hard is locked: one member has not cleared the Normal finale."
                        : $"Hard is locked: {locked.Length} members have not cleared the Normal finale.";
                    return access;
                }
            }

            int[] frontiers = members.Select(m => Frontier(m.Cleared(mode))).ToArray();
            int shared = frontiers.Min();
            access.ModeAllowed = true;
            access.MaxSelectableStage = Math.Min(shared, Limits.CampaignStages);
            if (shared <= Limits.CampaignStages)
            {
                string[] limiting = members.Where((m, i) => frontiers[i] == shared).Select(m => m.PlayerId).ToArray();
                bool someoneAhead = frontiers.Any(f => f > shared);
                access.LimitingPlayers = someoneAhead ? limiting : Array.Empty<string>();
                string stage = StageLabel(shared);
                access.Explanation = someoneAhead
                    ? $"Next shared stage: {stage} — {CountWord(limiting.Length)} {(limiting.Length == 1 ? "member has" : "members have")} not cleared it."
                    : $"Next shared stage: {stage}.";
            }
            else
            {
                access.Explanation = "Every member has completed this campaign; all stages are open for replay.";
            }
            return access;
        }

        /// <summary>
        /// Applies an eligible clear of <paramref name="stageNumber"/>. Returns true when it is a new first clear.
        /// Only the stage itself is marked; progression cannot skip ahead or fill an unrelated gap.
        /// </summary>
        public static bool ApplyClear(bool[] cleared, int stageNumber)
        {
            if (cleared == null || cleared.Length != Limits.CampaignStages)
                throw new ArgumentException($"Expected {Limits.CampaignStages} stage flags");
            if (stageNumber < 1 || stageNumber > Limits.CampaignStages)
                throw new ArgumentOutOfRangeException(nameof(stageNumber));
            if (stageNumber > Frontier(cleared))
                throw new InvalidOperationException($"{StageLabel(stageNumber)} is beyond this player's frontier");
            if (cleared[stageNumber - 1])
                return false;
            cleared[stageNumber - 1] = true;
            return true;
        }

        public static string StageLabel(int stageNumber) => "S" + stageNumber.ToString("00");

        static string CountWord(int n)
        {
            switch (n)
            {
                case 1: return "one";
                case 2: return "two";
                case 3: return "three";
                case 4: return "four";
                case 5: return "five";
                default: return n.ToString();
            }
        }
    }
}
