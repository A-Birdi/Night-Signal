using NightSignal.Core.Rules;

namespace NightSignal.Core.Content
{
    /// <summary>
    /// The benchmark locked into a campaign stage: the CERTIFIED one when the certification run has produced it
    /// (authored/stage-benchmarks.json — reference run P, target = factor × P), otherwise a PROVISIONAL one derived from the
    /// course's authored ExpectedSeconds. Shared by the control plane (online proposals and settlement) and the Local
    /// campaign so both judge a stage identically; a provisional target is labelled as such wherever it is shown.
    /// </summary>
    public static class StageBenchmarks
    {
        public const int HardTargetPercent = 95;
        public const long HardTimeoutMarginMs = 120_000;

        public static StageBenchmark For(ContentCatalogue catalogue, StageDef stage, CampaignMode mode)
        {
            StageBenchmark b = Provisional(catalogue, stage, mode);
            if (!catalogue.TryCertifiedBenchmark(stage.Id, mode, out CertifiedBenchmark c)) return b;
            b.TargetTimeMs = c.TargetMs;
            b.HardTimeoutMs = StageOutcome.SupportEnvelopeMs(c.TargetMs, mode) + HardTimeoutMarginMs;
            return b;
        }

        public static bool IsCertified(ContentCatalogue catalogue, StageDef stage, CampaignMode mode) =>
            catalogue.TryCertifiedBenchmark(stage.Id, mode, out CertifiedBenchmark _);

        /// <summary>Where the target comes from, for settlement records and the UI.</summary>
        public static string Source(ContentCatalogue catalogue, StageDef stage, CampaignMode mode) =>
            catalogue.TryCertifiedBenchmark(stage.Id, mode, out CertifiedBenchmark c)
                ? $"certified: {c.Factor:0.000} × reference {c.ReferenceMs} ms ({c.ReferenceCar} {c.ReferenceBuild}); {catalogue.BenchmarkMethod}"
                : ProvisionalSource(catalogue, stage);

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
