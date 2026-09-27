using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    public enum BenchmarkKind { Time = 0, RawDrift = 1, FourContracts = 2 }

    /// <summary>Versioned, measured stage benchmark locked into an event proposal before readiness.</summary>
    public sealed class StageBenchmark
    {
        public BenchmarkKind Kind;
        /// <summary>Qualifying time for time benchmarks (ms). For FourContracts: the full-route time limit.</summary>
        public long TargetTimeMs;
        public long RawDriftTarget;
        /// <summary>Published hard timeout for the whole event (ms); must be ≥ the support envelope.</summary>
        public long HardTimeoutMs;
        /// <summary>
        /// Lieutenant, penultimate and finale encounters (Addendum 01 §1.3): a qualifying human must ALSO beat the
        /// featured live rival. A tie at measurement precision does not beat the rival.
        /// </summary>
        public bool RequiresBeatingFeaturedRival;

        public static bool IsFeaturedEncounter(string stageType) => stageType == "lieutenant" || stageType == "penultimate" || stageType == "finale";
    }

    /// <summary>Server-observed facts for one human entrant (H is frozen at allocation, DQs included).</summary>
    public sealed class HumanStageResult
    {
        public string PlayerId = "";
        public RunOutcome Outcome;
        /// <summary>Remained eligible and actively drove the legal course (not AFK, spectating, or remote menus).</summary>
        public bool ActivelyDroveLegalCourse;
        public long FinishTimeMs;
        public long RawDriftScore;
        /// <summary>S29 only: number of the four contracts passed (0..4).</summary>
        public int ContractsPassed;
        /// <summary>
        /// Server-observed: this human legally finished ahead of the featured live rival, or the rival legally failed to
        /// finish. Only consulted when the benchmark requires beating the featured rival.
        /// </summary>
        public bool BeatFeaturedRival;
    }

    public sealed class PlayerStageVerdict
    {
        public string PlayerId = "";
        public bool Qualified;
        public bool WithinSupport;
        public bool EarnedClear;
        public string Reason = "";
    }

    public sealed class StageResolution
    {
        public int FrozenHumanCount;
        public int RequiredQualifiers;
        public int Qualifiers;
        public bool TeamSuccess;
        public IReadOnlyList<PlayerStageVerdict> Players = Array.Empty<PlayerStageVerdict>();
    }

    /// <summary>Cooperative stage completion, spec §5.2 and the S29 contract.</summary>
    public static class StageOutcome
    {
        public static int SupportTimePercent(CampaignMode mode) => mode == CampaignMode.Normal ? 150 : 135;
        public static int SupportDriftPercent(CampaignMode mode) => mode == CampaignMode.Normal ? 50 : 65;

        public static int RequiredQualifiers(CampaignMode mode, int frozenHumanCount)
        {
            if (frozenHumanCount < 1 || frozenHumanCount > Limits.MaxEventHumanEntrants)
                throw new ArgumentOutOfRangeException(nameof(frozenHumanCount));
            return mode == CampaignMode.Normal ? 1 : Math.Max(1, (frozenHumanCount + 1) / 2);
        }

        /// <summary>Support-envelope time in ms (benchmark × 1.50 Normal, × 1.35 Hard), rounded down.</summary>
        public static long SupportEnvelopeMs(long benchmarkMs, CampaignMode mode) => benchmarkMs * SupportTimePercent(mode) / 100;

        /// <summary>
        /// Event deadline after the first valid human finish: max(first + 90 s, support envelope),
        /// bounded by the stage's hard timeout. All values are race-clock milliseconds.
        /// </summary>
        public static long DeadlineMs(long firstValidHumanFinishMs, long supportEnvelopeMs, long hardTimeoutMs)
        {
            if (hardTimeoutMs < supportEnvelopeMs)
                throw new ArgumentException("Hard timeout must be at least the support envelope");
            return Math.Min(hardTimeoutMs, Math.Max(firstValidHumanFinishMs + Limits.FirstFinishGraceMs, supportEnvelopeMs));
        }

        /// <param name="featuredRivalStarted">
        /// False when the featured rival failed to spawn/initialise: that is a broken event (abort, retry without fees), never
        /// a free win, so resolution refuses it.
        /// </param>
        public static StageResolution Resolve(CampaignMode mode, StageBenchmark benchmark, int frozenHumanCount,
            IReadOnlyList<HumanStageResult> humans, bool featuredRivalStarted = true)
        {
            if (benchmark == null) throw new ArgumentNullException(nameof(benchmark));
            if (humans == null) throw new ArgumentNullException(nameof(humans));
            if (!featuredRivalStarted)
                throw new InvalidOperationException("The featured rival never started: the event is broken and must be aborted, not resolved");
            if (humans.Count != frozenHumanCount)
                throw new ArgumentException("Every allocated human needs a result, including DQs");

            var verdicts = new List<PlayerStageVerdict>();
            foreach (HumanStageResult h in humans)
                verdicts.Add(Judge(mode, benchmark, h));

            var res = new StageResolution
            {
                FrozenHumanCount = frozenHumanCount,
                RequiredQualifiers = RequiredQualifiers(mode, frozenHumanCount),
                Qualifiers = verdicts.Count(v => v.Qualified),
            };
            res.TeamSuccess = res.Qualifiers >= res.RequiredQualifiers;
            foreach (PlayerStageVerdict v in verdicts)
            {
                v.EarnedClear = res.TeamSuccess && v.WithinSupport;
                if (!res.TeamSuccess)
                    v.Reason = $"Team goal missed: {res.Qualifiers} of {res.RequiredQualifiers} required qualifying drivers. {v.Reason}";
            }
            res.Players = verdicts;
            return res;
        }

        static PlayerStageVerdict Judge(CampaignMode mode, StageBenchmark b, HumanStageResult h)
        {
            var v = new PlayerStageVerdict { PlayerId = h.PlayerId };
            if (h.Outcome != RunOutcome.Finished || !h.ActivelyDroveLegalCourse)
            {
                v.Reason = "No valid finish.";
                return v;
            }

            switch (b.Kind)
            {
                case BenchmarkKind.Time:
                    v.Qualified = h.FinishTimeMs <= b.TargetTimeMs;
                    v.WithinSupport = h.FinishTimeMs * 100 <= b.TargetTimeMs * SupportTimePercent(mode);
                    break;
                case BenchmarkKind.RawDrift:
                    v.Qualified = h.RawDriftScore >= b.RawDriftTarget;
                    v.WithinSupport = h.RawDriftScore * 100 >= b.RawDriftTarget * SupportDriftPercent(mode);
                    break;
                case BenchmarkKind.FourContracts:
                    if (h.ContractsPassed < 0 || h.ContractsPassed > 4)
                        throw new ArgumentOutOfRangeException(nameof(h.ContractsPassed));
                    bool inTime = h.FinishTimeMs * 100 <= b.TargetTimeMs * SupportTimePercent(mode);
                    v.Qualified = h.ContractsPassed == 4 && h.FinishTimeMs <= b.TargetTimeMs;
                    v.WithinSupport = v.Qualified || (h.ContractsPassed >= 3 && inTime);
                    break;
            }
            if (v.Qualified && b.RequiresBeatingFeaturedRival && !h.BeatFeaturedRival)
            {
                v.Qualified = false;
                v.Reason = "Target met, but the featured rival finished ahead.";
                return v;
            }
            v.Reason = v.Qualified ? "Qualified." : v.WithinSupport ? "Supporting finish." : "Outside the support envelope.";
            return v;
        }
    }
}
