using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    public enum TeamTrialKind { Mean = 0, Best = 1, Drift = 2 }

    /// <summary>One of the six positions on a Team Trial side (human or AI), as observed by the server.</summary>
    public sealed class TeamMemberResult
    {
        public string EntrantId;
        public bool Human;
        public RunOutcome Outcome;
        /// <summary>Adjusted legal finish time including ordinary penalties (ms). Only meaningful when finished.</summary>
        public long AdjustedFinishMs;
        /// <summary>Banked, validated RAW drift score (anti-loop/anti-stationary rules already applied).</summary>
        public long RawDriftScore;
    }

    public sealed class TeamScore
    {
        public TeamTrialKind Kind;
        /// <summary>Mean: SUM of six contributions (ms; lower wins; divide by 6 for display). Best: fastest time (ms;
        /// lower wins; long.MaxValue when nobody finished). Drift: summed raw score (higher wins).</summary>
        public long Value;
        public IReadOnlyList<KeyValuePair<string, long>> Contributions = Array.Empty<KeyValuePair<string, long>>();
        public double DisplayMeanMs => Kind == TeamTrialKind.Mean ? Value / (double)Limits.TeamTrialSideSize : double.NaN;
    }

    public enum TeamTrialVerdict { PlayerTeamWins = 0, OpposingTeamWins = 1, Tie = 2 }

    /// <summary>
    /// Team Trials (Addendum 01 §3, D06): six versus six, repeatable, no mastery RP, not part of the 75 challenges.
    /// MEAN counts every one of the six starting positions — a DNF/DQ contributes the hard timeout plus a fixed 30 s, so
    /// losing a slow teammate can never help the team. BEST compares each side's fastest legal finisher. DRIFT sums six
    /// banked raw scores, a DQ contributing zero. Team sums never complete personal challenges.
    /// </summary>
    public static class TeamTrials
    {
        public static TeamScore Score(TeamTrialKind kind, IReadOnlyList<TeamMemberResult> side, long hardTimeoutMs)
        {
            if (side == null || side.Count != Limits.TeamTrialSideSize)
                throw new ArgumentException($"A Team Trial side has exactly {Limits.TeamTrialSideSize} starting positions");
            if (hardTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(hardTimeoutMs));
            var contributions = new List<KeyValuePair<string, long>>();
            long value;
            switch (kind)
            {
                case TeamTrialKind.Mean:
                    foreach (TeamMemberResult m in side)
                    {
                        long c = m.Outcome == RunOutcome.Finished ? Math.Min(m.AdjustedFinishMs, hardTimeoutMs) : hardTimeoutMs + Limits.TeamTrialFailurePenaltyMs;
                        contributions.Add(new KeyValuePair<string, long>(m.EntrantId, c));
                    }
                    value = contributions.Sum(c => c.Value);
                    break;
                case TeamTrialKind.Best:
                    foreach (TeamMemberResult m in side)
                        contributions.Add(new KeyValuePair<string, long>(m.EntrantId, m.Outcome == RunOutcome.Finished ? m.AdjustedFinishMs : long.MaxValue));
                    value = contributions.Min(c => c.Value);
                    break;
                case TeamTrialKind.Drift:
                    foreach (TeamMemberResult m in side)
                    {
                        bool disqualified = m.Outcome == RunOutcome.DisqualifiedDisconnect || m.Outcome == RunOutcome.Quit;
                        contributions.Add(new KeyValuePair<string, long>(m.EntrantId, disqualified ? 0 : Math.Max(0, m.RawDriftScore)));
                    }
                    value = contributions.Sum(c => c.Value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
            return new TeamScore { Kind = kind, Value = value, Contributions = contributions };
        }

        public static TeamTrialVerdict Compare(TeamScore player, TeamScore opposing)
        {
            if (player.Kind != opposing.Kind) throw new ArgumentException("Scores are from different trial kinds");
            if (player.Value == opposing.Value) return TeamTrialVerdict.Tie;
            bool lowerWins = player.Kind != TeamTrialKind.Drift;
            return (player.Value < opposing.Value) == lowerWins ? TeamTrialVerdict.PlayerTeamWins : TeamTrialVerdict.OpposingTeamWins;
        }

        /// <summary>
        /// Human completion pay needs a legal finish; for BEST at least one human must also finish within the published
        /// participation envelope, so an AFK human cannot collect money for an AI win.
        /// </summary>
        public static bool HumanCompletionPayable(TeamTrialKind kind, TeamMemberResult human, IReadOnlyList<TeamMemberResult> playerSide, long participationEnvelopeMs)
        {
            if (!human.Human || human.Outcome != RunOutcome.Finished) return false;
            if (kind != TeamTrialKind.Best) return true;
            return playerSide.Any(m => m.Human && m.Outcome == RunOutcome.Finished && m.AdjustedFinishMs <= participationEnvelopeMs);
        }
    }
}
