using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NightSignal.Core.Builds
{
    /// <summary>A tuning control made available by an installed part, with its bounds and the default for this combination.</summary>
    public sealed class TuningControlInfo
    {
        public string Key;
        public PartSlot Slot;
        public string PartId;
        public int Min;
        public int Max;
        public int Step;
        public int Default;
        public string Unit;
        public PartTuningControl Source;
    }

    public sealed class TuningIssue
    {
        /// <summary>version | unknown-key | not-adjustable | out-of-range | off-step</summary>
        public string Kind;
        public string Key;
        public int Value;
        public string Message;

        public override string ToString() => Message;
    }

    /// <summary>
    /// Bounded, versioned tuning (spec §9, Addendum 02 §9.1). A key is adjustable only while the part in its slot declares
    /// it; absent values take the part's default. Every key maps linearly onto implemented simulation inputs.
    /// </summary>
    public static class TuningModel
    {
        /// <summary>Bump when the meaning of a stored tuning integer changes; older values then need an explicit migration.</summary>
        public const int CurrentVersion = 1;

        /// <param name="installed">Installed mechanical parts (any order; one per slot).</param>
        /// <param name="baseValue">Stock value of a parameter (for defaultFromBase controls).</param>
        public static List<TuningControlInfo> Controls(IEnumerable<PartDef> installed, Func<SimParam, double> baseValue)
        {
            var list = new List<TuningControlInfo>();
            foreach (PartDef part in installed.OrderBy(p => (int)p.SlotValue))
            {
                foreach (PartTuningControl c in part.Tuning)
                {
                    int def = c.Default;
                    if (c.DefaultFromBase)
                    {
                        TuningTarget t = c.Targets[0];
                        double b = baseValue(t.ParsedParam);
                        double frac = Math.Abs(t.AtMax - t.AtMin) < 1e-12 ? 0 : (b - t.AtMin) / (t.AtMax - t.AtMin);
                        double raw = c.Min + frac * (c.Max - c.Min);
                        def = Snap(raw, c.Min, c.Max, c.Step);
                    }
                    list.Add(new TuningControlInfo
                    {
                        Key = c.Key, Slot = part.SlotValue, PartId = part.Id, Min = c.Min, Max = c.Max, Step = c.Step,
                        Default = def, Unit = c.Unit ?? "", Source = c,
                    });
                }
            }
            return list;
        }

        public static int ValueOrDefault(TuningSetup setup, TuningControlInfo control) =>
            setup?.Values != null && setup.Values.TryGetValue(control.Key, out int v) ? v : control.Default;

        /// <summary>Every problem with the stored values (exact, itemised). Empty = valid.</summary>
        public static List<TuningIssue> Validate(TuningSetup setup, IReadOnlyList<TuningControlInfo> controls)
        {
            var issues = new List<TuningIssue>();
            setup = setup ?? new TuningSetup();
            if (setup.Version != CurrentVersion)
                issues.Add(new TuningIssue { Kind = "version", Message = $"Tune version {setup.Version} is not the current version {CurrentVersion}." });
            foreach (var kv in TuningSetup.Ordinal(setup.Values))
            {
                if (!TuningKeys.TrySlotOf(kv.Key, out PartSlot slot))
                {
                    issues.Add(new TuningIssue { Kind = "unknown-key", Key = kv.Key, Value = kv.Value, Message = $"Unknown tuning value {kv.Key}." });
                    continue;
                }
                TuningControlInfo c = controls.FirstOrDefault(x => x.Key == kv.Key);
                if (c == null)
                {
                    issues.Add(new TuningIssue
                    {
                        Kind = "not-adjustable", Key = kv.Key, Value = kv.Value,
                        Message = $"{kv.Key} is not adjustable with the installed {PartSlots.Id(slot)} part.",
                    });
                    continue;
                }
                if (kv.Value < c.Min || kv.Value > c.Max)
                    issues.Add(new TuningIssue { Kind = "out-of-range", Key = kv.Key, Value = kv.Value, Message = $"{kv.Key} {kv.Value} is outside {c.Min}–{c.Max}." });
                else if ((kv.Value - c.Min) % c.Step != 0)
                    issues.Add(new TuningIssue { Kind = "off-step", Key = kv.Key, Value = kv.Value, Message = $"{kv.Key} {kv.Value} is not a multiple of {c.Step} from {c.Min}." });
            }
            return issues;
        }

        /// <summary>
        /// Explicit editor action after a part swap: drops values the new parts cannot adjust and snaps out-of-range values,
        /// listing every change. Never called silently by Apply.
        /// </summary>
        public static TuningSetup Normalize(TuningSetup setup, IReadOnlyList<TuningControlInfo> controls, List<string> changes)
        {
            var result = new TuningSetup();
            setup = setup ?? new TuningSetup();
            if (setup.Version != CurrentVersion) changes?.Add($"Tune version {setup.Version} → {CurrentVersion}.");
            foreach (var kv in TuningSetup.Ordinal(setup.Values))
            {
                TuningControlInfo c = controls.FirstOrDefault(x => x.Key == kv.Key);
                if (c == null)
                {
                    changes?.Add($"{kv.Key} {kv.Value} removed: not adjustable with the installed parts.");
                    continue;
                }
                int snapped = Snap(kv.Value, c.Min, c.Max, c.Step);
                if (snapped != kv.Value) changes?.Add($"{kv.Key} {kv.Value} → {snapped} (range {c.Min}–{c.Max}, step {c.Step}).");
                result.Values[kv.Key] = snapped;
            }
            return result;
        }

        /// <summary>All controls at their defaults (explicit values for display).</summary>
        public static TuningSetup Defaults(IReadOnlyList<TuningControlInfo> controls)
        {
            var t = new TuningSetup();
            foreach (TuningControlInfo c in controls) t.Values[c.Key] = c.Default;
            return t;
        }

        static int Snap(double raw, int min, int max, int step)
        {
            double clamped = Math.Max(min, Math.Min(max, raw));
            int steps = (int)Math.Round((clamped - min) / step, MidpointRounding.AwayFromZero);
            return Math.Min(max, min + steps * step);
        }

        public static string Describe(TuningControlInfo c) =>
            string.Format(CultureInfo.InvariantCulture, "{0} {1}–{2} (step {3}, default {4}{5})", c.Key, c.Min, c.Max, c.Step, c.Default,
                string.IsNullOrEmpty(c.Unit) ? "" : " " + c.Unit);
    }
}
