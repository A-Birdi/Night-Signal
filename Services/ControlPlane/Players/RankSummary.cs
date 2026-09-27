using NightSignal.Core.Rules;
using CoreRankPoints = NightSignal.Core.Rules.RankPoints;

namespace NightSignal.ControlPlane.Players;

/// <summary>Rank Points are always recomputed from unique first-clear and challenge records (Core RankPoints).</summary>
public sealed record RankSummary(int RankPoints, int Index, string Name, int Threshold, string? NextName, int? NextThreshold)
{
    public static RankSummary Compute(int normalClears, int hardClears, int bronze, int silver, int gold)
    {
        int rp = CoreRankPoints.Total(normalClears, hardClears, bronze, silver, gold);
        RankDefinition rank = CoreRankPoints.RankFor(rp, hardClears, bronze + silver + gold);
        RankDefinition? next = CoreRankPoints.All.FirstOrDefault(r => r.Index == rank.Index + 1);
        return new RankSummary(rp, rank.Index, rank.Name, rank.Threshold, next?.Name, next?.Threshold);
    }
}
