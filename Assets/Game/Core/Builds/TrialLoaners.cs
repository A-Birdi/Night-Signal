using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.Core.Builds
{
    /// <summary>A challenge trial's supplied car (docs/CHALLENGE_TRIALS.md) resolved the way a garage build is, never from a garage.</summary>
    public static class TrialLoaners
    {
        /// <summary>
        /// The loaner's parts resolved into a physics spec, with its estimated PI (null when it does not resolve). The same
        /// on the game server and in the Local race, so both drive — and judge — the same car.
        /// </summary>
        public static ResolveResult Resolve(TrialLoaner loaner, CarDef car, CarTuningDef tuning, PartsCatalogue parts, out PiEstimate pi)
        {
            var build = new MechanicalSnapshot();
            foreach (var kv in loaner.Parts) build.Parts[kv.Key] = kv.Value;
            ResolveResult r = BuildResolver.Resolve(car, tuning, parts, build);
            pi = r.Ok ? PerformanceIndexEstimator.Estimate(r.Spec, BuildResolver.ResolveStock(car, tuning, parts), car.BasePI) : null;
            return r;
        }
    }
}
