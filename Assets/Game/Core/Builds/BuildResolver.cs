using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using NightSignal.Core.Content;

namespace NightSignal.Core.Builds
{
    /// <summary>
    /// Multipliers/overrides VehicleFactory applies AFTER <c>VehicleFactory.Build(spec.Car, spec.Tuning, assists)</c>
    /// has derived gearing, springs, dampers and brakes from the adjusted car/tuning. All neutral for a stock build.
    /// </summary>
    public sealed class ChassisAdjustments
    {
        public double FinalDriveScale = 1;
        /// <summary>Exponent about top gear: ratio_i = top × (ratio_i / top)^GearSpreadScale (&lt;1 = closer ratios).</summary>
        public double GearSpreadScale = 1;
        public double ShiftSecondsScale = 1;
        public double BrakeForceScale = 1;
        public double BrakeFrontBias = SimulationDefaults.BrakeFrontBias;
        public double SpringScaleFront = 1;
        public double SpringScaleRear = 1;
        public double DamperScaleFront = 1;
        public double DamperScaleRear = 1;
        public double AntiRollScaleFront = 1;
        public double AntiRollScaleRear = 1;
        /// <summary>Added to the factory-derived CgHeightM.</summary>
        public double CgHeightOffsetM;
        /// <summary>Added to VehicleParams.RestLengthM (droop travel).</summary>
        public double RestLengthOffsetM;
        /// <summary>Added to VehicleParams.MaxCompressionM (bump travel).</summary>
        public double MaxCompressionOffsetM;
        public double PeakSlipDeg = SimulationDefaults.PeakSlipDeg;
        public double SlideGripFraction = SimulationDefaults.SlideGripFraction;
        public double SlideFalloffDeg = SimulationDefaults.SlideFalloffDeg;
        public double PowerSlideGripLoss = SimulationDefaults.PowerSlideGripLoss;
        public double AeroFrontShare = SimulationDefaults.AeroFrontShare;
    }

    /// <summary>Economic/showcase effect of the single utility item. Never a physics, raw-score or RP input.</summary>
    public sealed class UtilityEffect
    {
        public string PartId = "";
        /// <summary>0, 4 or 8 — feeds Rules.PayoutFacts.UtilityIncomePercent.</summary>
        public int IncomePercent;
        /// <summary>0, 5 or 10 — feeds Rules.DriftScoring.ShowcaseScore.</summary>
        public int ShowcasePercent;
    }

    /// <summary>
    /// Fully resolved, engine-free numeric spec of one build. Unity: <c>VehicleFactory.Build(Car, Tuning, assists)</c>, then
    /// apply <see cref="Chassis"/>. Values are exact fixed-point (1e-6) so the client, the game server and the control
    /// plane compute the same <see cref="BuildHash"/> on any runtime.
    /// </summary>
    public sealed class ResolvedCarSpec
    {
        public string CarModelId = "";
        public string HandlingModelVersion = "";
        public int PartsCatalogueRevision;
        /// <summary>Adjusted copy: MassKg, PowerKw, TorqueNm changed by parts; everything else as authored.</summary>
        public CarDef Car;
        /// <summary>Adjusted copy of the model's CarTuningDef.</summary>
        public CarTuningDef Tuning;
        public ChassisAdjustments Chassis = new ChassisAdjustments();
        public UtilityEffect Utility = new UtilityEffect();
        /// <summary>SHA-256 over the handling version, model geometry and every resolved parameter (physics only; utility excluded).</summary>
        public string BuildHash = "";
        public List<string> PartIds = new List<string>();
        public List<string> PerformanceAppearance = new List<string>();

        internal long[] Micro = new long[SimParams.All.Length];

        public double Get(SimParam p) => Micro[(int)p] / 1_000_000.0;
        public long GetMicro(SimParam p) => Micro[(int)p];
    }

    public sealed class ResolveResult
    {
        public ResolvedCarSpec Spec;
        public List<RepairItem> Issues = new List<RepairItem>();
        public bool Ok => Spec != null;
    }

    /// <summary>
    /// Base car + tuning + installed parts + tune values → <see cref="ResolvedCarSpec"/>. Deterministic integer math:
    /// per parameter, v = (Set ?? base) + ΣAdd − chassis weight reduction, then each Scale in slot order, then AtLeast;
    /// then tuning targets (set/add/scale) in slot order; then every value is checked against its safe range.
    /// </summary>
    public static class BuildResolver
    {
        /// <summary>
        /// Version of the part/tune → simulation-input mapping (this resolver + parts.json semantics). Bump when either
        /// changes meaning; builds, ghosts and references record it.
        /// </summary>
        public const string HandlingModelVersion = "hm-1";

        const long M = 1_000_000L;

        public static ResolvedCarSpec ResolveStock(CarDef car, CarTuningDef tuning, PartsCatalogue parts)
        {
            ResolveResult r = Resolve(car, tuning, parts, MechanicalSnapshot.Stock());
            if (!r.Ok) throw new BuildDataException($"Stock {car?.Id} does not resolve: {string.Join("; ", r.Issues)}");
            return r.Spec;
        }

        public static ResolveResult Resolve(CarDef car, CarTuningDef tuning, PartsCatalogue parts, MechanicalSnapshot build)
        {
            if (car == null) throw new ArgumentNullException(nameof(car));
            if (tuning == null) throw new ArgumentNullException(nameof(tuning));
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            build = build ?? MechanicalSnapshot.Stock();
            var result = new ResolveResult();

            // 1. Installed parts by slot (structure and compatibility).
            var installed = new SortedDictionary<PartSlot, PartDef>();
            foreach (var kv in TuningSetup.Ordinal(build.Parts))
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (!PartSlots.TryParse(kv.Key, out PartSlot slot) || slot == PartSlot.Utility)
                {
                    result.Issues.Add(new RepairItem { Kind = RepairKind.UnknownSlot, Slot = kv.Key, PartId = kv.Value, Detail = "Unknown mechanical slot." });
                    continue;
                }
                PartDef part = CheckPart(parts, car, tuning, kv.Key, kv.Value, slot, result.Issues);
                if (part != null) installed[slot] = part;
            }
            var utility = new UtilityEffect();
            if (!string.IsNullOrEmpty(build.UtilityPartId))
            {
                PartDef u = CheckPart(parts, car, tuning, PartSlots.Id(PartSlot.Utility), build.UtilityPartId, PartSlot.Utility, result.Issues);
                if (u != null)
                {
                    utility.PartId = u.Id;
                    if (u.Utility.Kind == "income") utility.IncomePercent = u.Utility.Percent;
                    else utility.ShowcasePercent = u.Utility.Percent;
                }
            }

            // 2. Base values.
            long[] baseV = BaseValues(car, tuning);

            // 3. Tuning controls and validation (defaults per part combination).
            List<TuningControlInfo> controls = TuningModel.Controls(installed.Values, p => baseV[(int)p] / (double)M);
            foreach (TuningIssue ti in TuningModel.Validate(build.Tuning, controls))
                result.Issues.Add(new RepairItem { Kind = RepairKind.TuningInvalid, Slot = ti.Key ?? "", Detail = ti.Message });
            if (result.Issues.Count > 0) return result;

            // 4. Part effects.
            long[] v = (long[])baseV.Clone();
            parts.TryChassis(car.Id, out ChassisDef chassis);
            foreach (SimParam p in SimParams.All)
            {
                int i = (int)p;
                long value = baseV[i];
                long reductionFraction = 0;
                foreach (PartDef part in installed.Values)
                    foreach (PartEffect e in part.Effects)
                        if (e.ParsedParam == p && e.ParsedOp == ModifierOp.Set) value = ToMicro(e.Value);
                foreach (PartDef part in installed.Values)
                    foreach (PartEffect e in part.Effects)
                    {
                        if (e.ParsedParam != p) continue;
                        if (e.ParsedOp == ModifierOp.Add) value += ToMicro(e.Value);
                        else if (e.ParsedOp == ModifierOp.ChassisWeightReduction) reductionFraction += ToMicro(e.Value);
                    }
                if (reductionFraction > 0)
                {
                    long capKg = ToMicro(chassis?.WeightReductionMaxKg ?? 0);
                    value -= RoundDiv(capKg * Math.Min(reductionFraction, M), M);
                }
                foreach (PartDef part in installed.Values)
                    foreach (PartEffect e in part.Effects)
                        if (e.ParsedParam == p && e.ParsedOp == ModifierOp.Scale) value = RoundDiv(value * ToMicro(e.Value), M);
                foreach (PartDef part in installed.Values)
                    foreach (PartEffect e in part.Effects)
                        if (e.ParsedParam == p && e.ParsedOp == ModifierOp.AtLeast) value = Math.Max(value, ToMicro(e.Value));
                v[i] = value;
            }

            // 5. Tuning targets (after parts, slot order).
            foreach (TuningControlInfo c in controls)
            {
                int tv = TuningModel.ValueOrDefault(build.Tuning, c);
                foreach (TuningTarget t in c.Source.Targets)
                {
                    long a = ToMicro(t.AtMin), b = ToMicro(t.AtMax);
                    long mapped = a + RoundDiv((b - a) * (tv - c.Min), c.Max - c.Min);
                    int i = (int)t.ParsedParam;
                    switch (t.ParsedOp)
                    {
                        case ModifierOp.Set: v[i] = mapped; break;
                        case ModifierOp.Add: v[i] += mapped; break;
                        case ModifierOp.Scale: v[i] = RoundDiv(v[i] * mapped, M); break;
                    }
                }
            }

            // 6. Safe ranges (authoring errors surface; nothing is clamped silently).
            foreach (SimParam p in SimParams.All)
            {
                SimParamInfo info = SimParams.Info(p);
                long lo = ToMicro(info.Min), hi = ToMicro(info.Max);
                if (v[(int)p] < lo || v[(int)p] > hi)
                    result.Issues.Add(new RepairItem
                    {
                        Kind = RepairKind.OutOfSafeRange, Slot = p.ToString(),
                        Detail = string.Format(CultureInfo.InvariantCulture, "{0} = {1} is outside {2}–{3} {4}.", p, v[(int)p] / (double)M, info.Min, info.Max, info.Unit),
                    });
            }
            if (result.Issues.Count > 0) return result;

            result.Spec = BuildSpec(car, tuning, parts, installed.Values.ToList(), utility, v);
            return result;
        }

        static PartDef CheckPart(PartsCatalogue parts, CarDef car, CarTuningDef tuning, string slotId, string partId, PartSlot slot, List<RepairItem> issues)
        {
            if (!parts.TryPart(partId, out PartDef part))
            {
                issues.Add(new RepairItem { Kind = RepairKind.RemovedPart, Slot = slotId, PartId = partId, Detail = "This part is no longer in the catalogue." });
                return null;
            }
            if (part.SlotValue != slot)
            {
                issues.Add(new RepairItem { Kind = RepairKind.WrongSlot, Slot = slotId, PartId = partId, PartName = part.Name, Detail = $"Belongs in {part.Slot}." });
                return null;
            }
            CompatibilityResult c = parts.CheckCompatibility(part, car, tuning);
            if (!c.Compatible)
            {
                issues.Add(new RepairItem { Kind = RepairKind.Incompatible, Slot = slotId, PartId = partId, PartName = part.Name, Detail = c.Reason });
                return null;
            }
            return part;
        }

        static long[] BaseValues(CarDef car, CarTuningDef t)
        {
            var v = new long[SimParams.All.Length];
            void Set(SimParam p, double x) => v[(int)p] = ToMicro(x);
            Set(SimParam.MassKg, car.MassKg);
            Set(SimParam.PowerKw, car.PowerKw);
            Set(SimParam.TorqueNm, car.TorqueNm);
            Set(SimParam.FrontWeight, t.FrontWeight);
            Set(SimParam.TrackM, t.TrackM);
            Set(SimParam.TyreGrip, t.TyreGrip);
            Set(SimParam.RearGripBias, t.RearGripBias);
            Set(SimParam.InertiaScale, t.InertiaScale);
            Set(SimParam.DragAreaCdA, t.DragAreaCdA);
            Set(SimParam.LiftAreaClA, t.LiftAreaClA);
            Set(SimParam.AwdFrontShare, car.Drive == "AWD" ? t.AwdFrontShare : 0);
            Set(SimParam.Turbo, t.Turbo ? 1 : 0);
            Set(SimParam.TurboLagSeconds, t.Turbo ? (t.TurboLagSeconds > 0 ? t.TurboLagSeconds : SimulationDefaults.TurboLagWhenUnset) : 0);
            Set(SimParam.RedlineRpm, t.RedlineRpm);
            Set(SimParam.Gears, Math.Max(4, t.Gears));
            Set(SimParam.FinalDriveScale, 1);
            Set(SimParam.GearSpreadScale, 1);
            Set(SimParam.ShiftSecondsScale, 1);
            Set(SimParam.BrakeForceScale, 1);
            Set(SimParam.BrakeFrontBias, SimulationDefaults.BrakeFrontBias);
            Set(SimParam.SpringScaleFront, 1);
            Set(SimParam.SpringScaleRear, 1);
            Set(SimParam.DamperScaleFront, 1);
            Set(SimParam.DamperScaleRear, 1);
            Set(SimParam.AntiRollScaleFront, 1);
            Set(SimParam.AntiRollScaleRear, 1);
            Set(SimParam.RideHeightOffsetM, 0);
            Set(SimParam.PeakSlipDeg, SimulationDefaults.PeakSlipDeg);
            Set(SimParam.SlideGripFraction, SimulationDefaults.SlideGripFraction);
            Set(SimParam.SlideFalloffDeg, SimulationDefaults.SlideFalloffDeg);
            Set(SimParam.PowerSlideGripLoss, SimulationDefaults.PowerSlideGripLoss);
            Set(SimParam.AeroFrontShare, SimulationDefaults.AeroFrontShare);
            return v;
        }

        static ResolvedCarSpec BuildSpec(CarDef car, CarTuningDef tuning, PartsCatalogue parts, List<PartDef> installed, UtilityEffect utility, long[] v)
        {
            double D(SimParam p) => v[(int)p] / (double)M;
            CarDef c = CopyFields(car);
            c.MassKg = D(SimParam.MassKg);
            c.PowerKw = D(SimParam.PowerKw);
            c.TorqueNm = D(SimParam.TorqueNm);

            CarTuningDef t = CopyFields(tuning);
            t.FrontWeight = D(SimParam.FrontWeight);
            t.TrackM = D(SimParam.TrackM);
            t.TyreGrip = D(SimParam.TyreGrip);
            t.RearGripBias = D(SimParam.RearGripBias);
            t.InertiaScale = D(SimParam.InertiaScale);
            t.DragAreaCdA = D(SimParam.DragAreaCdA);
            t.LiftAreaClA = D(SimParam.LiftAreaClA);
            t.AwdFrontShare = D(SimParam.AwdFrontShare);
            t.Turbo = v[(int)SimParam.Turbo] >= M / 2;
            t.TurboLagSeconds = t.Turbo ? D(SimParam.TurboLagSeconds) : tuning.TurboLagSeconds;
            t.RedlineRpm = D(SimParam.RedlineRpm);
            t.Gears = (int)RoundDiv(v[(int)SimParam.Gears], M);

            long ride = v[(int)SimParam.RideHeightOffsetM];
            var ch = new ChassisAdjustments
            {
                FinalDriveScale = D(SimParam.FinalDriveScale),
                GearSpreadScale = D(SimParam.GearSpreadScale),
                ShiftSecondsScale = D(SimParam.ShiftSecondsScale),
                BrakeForceScale = D(SimParam.BrakeForceScale),
                BrakeFrontBias = D(SimParam.BrakeFrontBias),
                SpringScaleFront = D(SimParam.SpringScaleFront),
                SpringScaleRear = D(SimParam.SpringScaleRear),
                DamperScaleFront = D(SimParam.DamperScaleFront),
                DamperScaleRear = D(SimParam.DamperScaleRear),
                AntiRollScaleFront = D(SimParam.AntiRollScaleFront),
                AntiRollScaleRear = D(SimParam.AntiRollScaleRear),
                CgHeightOffsetM = RoundDiv(ride * ToMicro(SimulationDefaults.RideHeightToCg), M) / (double)M,
                RestLengthOffsetM = RoundDiv(ride * ToMicro(SimulationDefaults.RideHeightToRestLength), M) / (double)M,
                MaxCompressionOffsetM = RoundDiv(ride * ToMicro(SimulationDefaults.RideHeightToMaxCompression), M) / (double)M,
                PeakSlipDeg = D(SimParam.PeakSlipDeg),
                SlideGripFraction = D(SimParam.SlideGripFraction),
                SlideFalloffDeg = D(SimParam.SlideFalloffDeg),
                PowerSlideGripLoss = D(SimParam.PowerSlideGripLoss),
                AeroFrontShare = D(SimParam.AeroFrontShare),
            };

            var spec = new ResolvedCarSpec
            {
                CarModelId = car.Id,
                HandlingModelVersion = HandlingModelVersion,
                PartsCatalogueRevision = parts.Revision,
                Car = c,
                Tuning = t,
                Chassis = ch,
                Utility = utility,
                PartIds = installed.OrderBy(p => (int)p.SlotValue).Select(p => p.Id).ToList(),
                PerformanceAppearance = installed.Where(p => !string.IsNullOrEmpty(p.Appearance)).OrderBy(p => (int)p.SlotValue).Select(p => p.Appearance).ToList(),
                Micro = v,
            };
            spec.BuildHash = Hash(spec, tuning);
            return spec;
        }

        /// <summary>Canonical text → SHA-256. Includes model geometry so a content revision of the chassis changes the hash.</summary>
        static string Hash(ResolvedCarSpec s, CarTuningDef authored)
        {
            var sb = new StringBuilder(1024);
            sb.Append("bh1|").Append(s.HandlingModelVersion).Append('|').Append(s.CarModelId);
            sb.Append("|drive=").Append(s.Car.Drive).Append("|layout=").Append(s.Car.EngineLayout).Append("|family=").Append(authored.EngineFamily);
            AppendMicro(sb, "len", authored.LengthM);
            AppendMicro(sb, "wid", authored.WidthM);
            AppendMicro(sb, "hgt", authored.HeightM);
            AppendMicro(sb, "wb", authored.WheelbaseM);
            foreach (SimParam p in SimParams.All)
                sb.Append('|').Append(p.ToString()).Append('=').Append(s.Micro[(int)p].ToString(CultureInfo.InvariantCulture));
            return BuildHashing.Sha256Hex(sb.ToString());
        }

        static void AppendMicro(StringBuilder sb, string key, double value) =>
            sb.Append('|').Append(key).Append('=').Append(ToMicro(value).ToString(CultureInfo.InvariantCulture));

        internal static long ToMicro(double x) => (long)Math.Round(x * M, MidpointRounding.AwayFromZero);

        /// <summary>Integer division rounding half away from zero.</summary>
        internal static long RoundDiv(long a, long b)
        {
            if (b == 0) throw new DivideByZeroException();
            if (b < 0) { a = -a; b = -b; }
            long q = a / b, r = a % b;
            if (2 * Math.Abs(r) >= b) q += a >= 0 ? 1 : -1;
            return q;
        }

        /// <summary>Shallow copy of every public instance field (future content fields are carried over automatically).</summary>
        static T CopyFields<T>(T source) where T : class, new()
        {
            var copy = new T();
            foreach (FieldInfo f in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = f.GetValue(source);
                if (value is System.Collections.IList list && value.GetType().IsGenericType)
                    value = Activator.CreateInstance(value.GetType(), list);
                f.SetValue(copy, value);
            }
            return copy;
        }
    }
}
