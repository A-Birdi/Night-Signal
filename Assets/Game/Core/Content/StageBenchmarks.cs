using NightSignal.Core.Rules;

namespace NightSignal.Core.Content
{
    /// <summary>
    /// The benchmark locked into a campaign stage. The catalogue has no certified benchmarks yet (spec §2.5 needs legal
    /// reference runs), so this derives a PROVISIONAL one from the course's authored ExpectedSeconds. Shared by the
    /// control plane (online proposals and settlement) and the Local campaign so both judge a stage identically; it is
    /// labelled provisional wherever it is shown.
    /// </summary>
    public static class StageBenchmarks
    {
        public const int HardTargetPercent = 95;
        public const long HardTimeoutMarginMs = 120_000;

        public static StageBenchmark Provisional(ContentCatalogue catalogue, StageDef stage, CampaignMode mode)
        {
            CourseDef course = catalogue.Course(stage.Course);
            long expectedMs = course.ExpectedSeconds * 1000L;
            long target = mode == CampaignMode.Hard ? expectedMs * HardTargetPercent / 100 : expectedMs;
            long envelope = StageOutcome.SupportEnvelopeMs(target, mode);
            return new StageBenchmark
            {
                Kind = stage.Type == "penultimate" ? BenchmarkKind.FourContracts : BenchmarkKind.Time,
                TargetTimeMs = target,
                HardTimeoutMs = envelope + HardTimeoutMarginMs, // must be >= the support envelope (StageOutcome.DeadlineMs)
                // Lieutenant, penultimate and finale encounters also need a qualifying human to beat the live featured rival.
                RequiresBeatingFeaturedRival = StageBenchmark.IsFeaturedEncounter(stage.Type),
            };
        }

        public static string ProvisionalSource(ContentCatalogue catalogue, StageDef stage)
        {
            CourseDef course = catalogue.Course(stage.Course);
            return $"provisional: derived from {course.Id}.expectedSeconds={course.ExpectedSeconds}; no certified reference run yet";
        }
    }
}
