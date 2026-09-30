using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.Core.Builds
{
    /// <summary>A tunable loaner's setup resolved: the build raced, its PI, why it is not legal, and what the tune sets.</summary>
    public sealed class TrialLoanerBuild
    {
        public MechanicalSnapshot Build;
        public ResolveResult Result;
        public PiEstimate Pi;
        public readonly List<string> Problems = new List<string>();
        /// <summary>The tuning controls of the installed parts, with the values raced.</summary>
        public List<TuningControlInfo> Controls = new List<TuningControlInfo>();
        public bool FinalDriveChanged, AeroAtExtreme;
        /// <summary>The controls whose raced value differs from their default, in control order (CH60).</summary>
        public List<string> ChangedKeys = new List<string>();
        public bool Ok => Result != null && Result.Ok && Problems.Count == 0;
        public int Value(string key) => Controls.Where(c => c.Key == key).Select(c => TuningModel.ValueOrDefault(Build.Tuning, c)).DefaultIfEmpty(0).First();
    }

    /// <summary>A challenge trial's supplied car (docs/CHALLENGE_TRIALS.md) resolved the way a garage build is, never from a garage.</summary>
    public static class TrialLoaners
    {
        /// <summary>
        /// A tunable loaner with the player's setup (null = as supplied): parts from the trial's choices only (the supplied
        /// part is always allowed; a slot cannot be emptied of it), the tune validated by the resolver, the PI inside the
        /// budget. The same on the game server and in the Local race.
        /// </summary>
        public static TrialLoanerBuild ResolveSetup(TrialLoaner loaner, MechanicalSnapshot setup, CarDef car, CarTuningDef tuning, PartsCatalogue parts)
        {
            var result = new TrialLoanerBuild { Build = new MechanicalSnapshot() };
            foreach (var kv in loaner.Parts) result.Build.Parts[kv.Key] = kv.Value;
            if (setup != null && loaner.IsTunable)
            {
                foreach (var kv in setup.Parts ?? new SortedDictionary<string, string>())
                {
                    bool supplied = loaner.Parts.TryGetValue(kv.Key, out string given) && given == kv.Value;
                    bool offered = loaner.Choices != null && loaner.Choices.TryGetValue(kv.Key, out List<string> list) && list.Contains(kv.Value);
                    if (string.IsNullOrEmpty(kv.Value) || !(supplied || offered)) result.Problems.Add($"{kv.Key}: {kv.Value} is not one of this trial's parts");
                    else result.Build.Parts[kv.Key] = kv.Value;
                }
                if (loaner.Tunable) result.Build.Tuning = setup.Tuning?.Clone() ?? new TuningSetup();
                else if (setup.Tuning?.Values != null && setup.Tuning.Values.Count > 0) result.Problems.Add("this loaner's tuning is fixed");
            }
            result.Result = BuildResolver.Resolve(car, tuning, parts, result.Build);
            foreach (RepairItem issue in result.Result.Issues) result.Problems.Add(issue.Detail);
            ResolvedCarSpec stock = BuildResolver.ResolveStock(car, tuning, parts);
            result.Pi = result.Result.Ok ? PerformanceIndexEstimator.Estimate(result.Result.Spec, stock, car.BasePI) : null;
            if (loaner.PiBudget > 0 && result.Pi != null && result.Pi.Value > loaner.PiBudget) result.Problems.Add($"PI {result.Pi.Value} is over the budget of {loaner.PiBudget}");
            var installed = result.Build.Parts.Values.Select(id => parts.TryPart(id, out PartDef p) ? p : null).Where(p => p != null && p.SlotValue != PartSlot.Utility);
            result.Controls = TuningModel.Controls(installed, stock.Get);
            result.ChangedKeys = result.Controls.Where(c => TuningModel.ValueOrDefault(result.Build.Tuning, c) != c.Default).Select(c => c.Key).Distinct().ToList();
            TuningControlInfo fd = result.Controls.FirstOrDefault(c => c.Key == TuningKeys.FinalDrive);
            result.FinalDriveChanged = fd != null && TuningModel.ValueOrDefault(result.Build.Tuning, fd) != fd.Default;
            // CH57 "Balanced, Not Maximum": aero is at an end when the wing level is at its top (front and rear downforce both at
            // their maximum) or the balance is at either end of its range (the front, or the rear, at its maximum share).
            TuningControlInfo ab = result.Controls.FirstOrDefault(c => c.Key == TuningKeys.AeroBalance);
            TuningControlInfo al = result.Controls.FirstOrDefault(c => c.Key == TuningKeys.AeroLevel);
            int abv = ab == null ? 0 : TuningModel.ValueOrDefault(result.Build.Tuning, ab);
            result.AeroAtExtreme = (ab != null && (abv <= ab.Min || abv >= ab.Max)) || (al != null && TuningModel.ValueOrDefault(result.Build.Tuning, al) >= al.Max);
            return result;
        }

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
