using System;
using System.Collections.Generic;

namespace NightSignal.Core.Rules
{
    public sealed class CampaignGrid
    {
        public int Humans;
        /// <summary>Live AI rival IDs in grid order (featured first). Humans + AI never exceed six.</summary>
        public IReadOnlyList<string> LiveAiRivals = Array.Empty<string>();
        /// <summary>Set only for six-human grids: the featured rival's certified pace replay (not an entrant).</summary>
        public string BenchmarkReplayRival;
        public int Entrants => Humans + LiveAiRivals.Count;
    }

    public enum FreeplayFlavor { MixedGrid = 0, PurePvP = 1, TimeTrial = 2, SoloVersusAi = 3 }

    public sealed class FreeplayGrid
    {
        public int Humans;
        public int LiveAi;
        public bool WasClamped;
        public string Explanation = "";
        public FreeplayFlavor Flavor;
    }

    /// <summary>Six-entrant cap and grid filling, spec §2 and §8.</summary>
    public static class GridPlanner
    {
        public const string BenchmarkReplayLabel = "Rival benchmark — replay, not an entrant";

        public static CampaignGrid PlanCampaign(int humans, string featuredRival, IReadOnlyList<string> supportPool)
        {
            if (humans < 1 || humans > Limits.MaxRaceEntrants)
                throw new ArgumentOutOfRangeException(nameof(humans), "Campaign events need 1–6 humans");
            if (string.IsNullOrEmpty(featuredRival))
                throw new ArgumentException("A campaign stage always has a featured rival", nameof(featuredRival));

            var grid = new CampaignGrid { Humans = humans };
            if (humans == Limits.MaxRaceEntrants)
            {
                // Six humans: no seventh live car. The featured rival appears as a labelled replay.
                grid.BenchmarkReplayRival = featuredRival;
                return grid;
            }

            int aiSlots = Limits.MaxRaceEntrants - humans;
            var ai = new List<string> { featuredRival };
            if (supportPool != null)
            {
                foreach (string rival in supportPool)
                {
                    if (ai.Count >= aiSlots) break;
                    if (!string.IsNullOrEmpty(rival) && !ai.Contains(rival))
                        ai.Add(rival);
                }
            }
            grid.LiveAiRivals = ai;
            return grid;
        }

        /// <summary>
        /// Enforces humans + AI ≤ 6 for Freeplay. An invalid stale AI selection is clamped with a visible
        /// explanation; humans are never ejected to make room.
        /// </summary>
        public static FreeplayGrid ValidateFreeplay(int humans, int requestedAi)
        {
            if (humans < 1 || humans > Limits.MaxRaceEntrants)
                throw new ArgumentOutOfRangeException(nameof(humans), "Freeplay events need 1–6 humans");
            if (requestedAi < 0)
                throw new ArgumentOutOfRangeException(nameof(requestedAi));

            int maxAi = Limits.MaxRaceEntrants - humans;
            var grid = new FreeplayGrid { Humans = humans, LiveAi = Math.Min(requestedAi, maxAi) };
            if (requestedAi > maxAi)
            {
                grid.WasClamped = true;
                grid.Explanation = $"AI reduced from {requestedAi} to {maxAi}: {humans} drivers joined and a race holds six entrants.";
            }
            if (grid.LiveAi == 0)
                grid.Flavor = humans >= 2 ? FreeplayFlavor.PurePvP : FreeplayFlavor.TimeTrial;
            else
                grid.Flavor = humans == 1 ? FreeplayFlavor.SoloVersusAi : FreeplayFlavor.MixedGrid;
            return grid;
        }
    }
}
