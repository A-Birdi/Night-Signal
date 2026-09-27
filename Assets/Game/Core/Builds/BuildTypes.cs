using System;
using System.Collections.Generic;

namespace NightSignal.Core.Builds
{
    /// <summary>
    /// Performance-part slots (spec §9). Utility is a separate economic/showcase slot: it never changes a simulation
    /// input, the Performance Index, raw drift score or RP.
    /// </summary>
    public enum PartSlot
    {
        Tyres = 0,
        Suspension = 1,
        Differential = 2,
        /// <summary>Gearbox and final drive.</summary>
        Gearbox = 3,
        /// <summary>Intake/exhaust (T1), breathing package (T2), internals (T3), full build (T4).</summary>
        Engine = 4,
        ForcedInduction = 5,
        Brakes = 6,
        WeightReduction = 7,
        Aero = 8,
        /// <summary>Visual body package that ALSO changes mass/aero/track (spec §9: "clearly specified mechanical package").</summary>
        BodyKit = 9,
        Utility = 10,
    }

    public static class PartSlots
    {
        /// <summary>Slots whose parts may change simulation inputs, in resolver order.</summary>
        public static readonly PartSlot[] Mechanical =
        {
            PartSlot.Tyres, PartSlot.Suspension, PartSlot.Differential, PartSlot.Gearbox, PartSlot.Engine,
            PartSlot.ForcedInduction, PartSlot.Brakes, PartSlot.WeightReduction, PartSlot.Aero, PartSlot.BodyKit,
        };

        static readonly string[] Ids =
        {
            "tyres", "suspension", "differential", "gearbox", "engine", "forced-induction", "brakes", "weight-reduction",
            "aero", "body-kit", "utility",
        };

        /// <summary>Stable document id of a slot (stored in loadouts; never an array position).</summary>
        public static string Id(PartSlot slot) => Ids[(int)slot];

        public static bool TryParse(string id, out PartSlot slot)
        {
            for (int i = 0; i < Ids.Length; i++)
            {
                if (string.Equals(Ids[i], id, StringComparison.Ordinal))
                {
                    slot = (PartSlot)i;
                    return true;
                }
            }
            slot = PartSlot.Tyres;
            return false;
        }
    }

    /// <summary>
    /// Every simulation input a part or tune may change. Each value maps to a field the Unity vehicle simulation
    /// actually reads (<c>NightSignal.Vehicle.VehicleParams</c>) or to a <c>CarDef</c>/<c>CarTuningDef</c> field that
    /// <c>VehicleFactory</c> derives those parameters from. Nothing here is a stat the simulation cannot express.
    /// Names are stable: they appear in parts.json, in the BuildHash canonical text and in UI delta lists.
    /// </summary>
    public enum SimParam
    {
        // CarDef inputs (VehicleFactory derives gearing, springs, dampers and brake force from these).
        MassKg,
        PowerKw,
        TorqueNm,
        // CarTuningDef inputs.
        FrontWeight,
        TrackM,
        TyreGrip,
        RearGripBias,
        InertiaScale,
        DragAreaCdA,
        LiftAreaClA,
        AwdFrontShare,
        /// <summary>0 = naturally aspirated, 1 = turbocharged (VehicleParams.Turbocharged).</summary>
        Turbo,
        TurboLagSeconds,
        RedlineRpm,
        Gears,
        // Applied by VehicleFactory AFTER its derivations (see ChassisAdjustments).
        FinalDriveScale,
        GearSpreadScale,
        ShiftSecondsScale,
        BrakeForceScale,
        BrakeFrontBias,
        SpringScaleFront,
        SpringScaleRear,
        DamperScaleFront,
        DamperScaleRear,
        AntiRollScaleFront,
        AntiRollScaleRear,
        /// <summary>Metres; negative lowers. Mapped to CgHeightM/RestLengthM/MaxCompressionM offsets by the resolver.</summary>
        RideHeightOffsetM,
        PeakSlipDeg,
        SlideGripFraction,
        SlideFalloffDeg,
        PowerSlideGripLoss,
        AeroFrontShare,
    }

    /// <summary>Metadata and validated safe ranges for each <see cref="SimParam"/>.</summary>
    public sealed class SimParamInfo
    {
        public SimParam Param;
        public string Name;
        public double Min;
        public double Max;
        public string Unit;
        /// <summary>Where the value lands in Unity (documentation for the VehicleFactory integration).</summary>
        public string Target;
    }

    public static class SimParams
    {
        /// <summary>Canonical order (resolver, BuildHash, delta lists).</summary>
        public static readonly SimParam[] All = (SimParam[])Enum.GetValues(typeof(SimParam));

        static readonly Dictionary<SimParam, SimParamInfo> info = Build();

        public static SimParamInfo Info(SimParam p) => info[p];

        public static bool TryParse(string name, out SimParam p)
        {
            foreach (SimParam candidate in All)
            {
                if (string.Equals(candidate.ToString(), name, StringComparison.Ordinal))
                {
                    p = candidate;
                    return true;
                }
            }
            p = SimParam.MassKg;
            return false;
        }

        static Dictionary<SimParam, SimParamInfo> Build()
        {
            var d = new Dictionary<SimParam, SimParamInfo>();
            void Add(SimParam p, double min, double max, string unit, string target) =>
                d[p] = new SimParamInfo { Param = p, Name = p.ToString(), Min = min, Max = max, Unit = unit, Target = target };

            Add(SimParam.MassKg, 600, 2200, "kg", "CarDef.MassKg → VehicleParams.MassKg (springs/dampers/brake force re-derived)");
            Add(SimParam.PowerKw, 40, 700, "kW", "CarDef.PowerKw → VehicleParams.PeakPowerKw (gearing re-derived)");
            Add(SimParam.TorqueNm, 60, 900, "Nm", "CarDef.TorqueNm → VehicleParams.PeakTorqueNm");
            Add(SimParam.FrontWeight, 0.35, 0.68, "fraction", "CarTuningDef.FrontWeight → VehicleParams.FrontWeightFraction");
            Add(SimParam.TrackM, 1.30, 1.80, "m", "CarTuningDef.TrackM → VehicleParams.TrackM");
            Add(SimParam.TyreGrip, 0.80, 1.70, "mu", "CarTuningDef.TyreGrip → VehicleParams.TyreGrip");
            Add(SimParam.RearGripBias, 0.85, 1.20, "ratio", "CarTuningDef.RearGripBias → VehicleParams.RearGripBias");
            Add(SimParam.InertiaScale, 0.75, 1.35, "ratio", "CarTuningDef.InertiaScale → VehicleParams.InertiaScale");
            Add(SimParam.DragAreaCdA, 0.40, 1.00, "m2", "CarTuningDef.DragAreaCdA → VehicleParams.DragAreaCdA (gearing re-derived)");
            Add(SimParam.LiftAreaClA, -0.10, 0.90, "m2", "CarTuningDef.LiftAreaClA → VehicleParams.LiftAreaClA");
            Add(SimParam.AwdFrontShare, 0.0, 0.60, "fraction", "CarTuningDef.AwdFrontShare → VehicleParams.AwdFrontShare (AWD only)");
            Add(SimParam.Turbo, 0, 1, "flag", "CarTuningDef.Turbo → VehicleParams.Turbocharged");
            Add(SimParam.TurboLagSeconds, 0, 1.5, "s", "CarTuningDef.TurboLagSeconds → VehicleParams.TurboLagSeconds");
            Add(SimParam.RedlineRpm, 5000, 10500, "rpm", "CarTuningDef.RedlineRpm → VehicleParams.RedlineRpm/PeakPowerRpm");
            Add(SimParam.Gears, 4, 7, "count", "CarTuningDef.Gears → VehicleParams.GearRatios.Length");
            Add(SimParam.FinalDriveScale, 0.85, 1.25, "x", "VehicleParams.FinalDrive ×");
            Add(SimParam.GearSpreadScale, 0.80, 1.15, "exponent", "VehicleParams.GearRatios[i] = top × (ratio_i/top)^s");
            Add(SimParam.ShiftSecondsScale, 0.30, 1.20, "x", "VehicleParams.ShiftSeconds ×");
            Add(SimParam.BrakeForceScale, 0.70, 1.40, "x", "VehicleParams.BrakeForceN ×");
            Add(SimParam.BrakeFrontBias, 0.50, 0.75, "fraction", "VehicleParams.BrakeFrontBias =");
            Add(SimParam.SpringScaleFront, 0.70, 1.70, "x", "VehicleParams.SpringFront ×");
            Add(SimParam.SpringScaleRear, 0.70, 1.70, "x", "VehicleParams.SpringRear ×");
            Add(SimParam.DamperScaleFront, 0.70, 1.60, "x", "VehicleParams.DamperFront ×");
            Add(SimParam.DamperScaleRear, 0.70, 1.60, "x", "VehicleParams.DamperRear ×");
            Add(SimParam.AntiRollScaleFront, 0.50, 1.80, "x", "VehicleParams.AntiRollFront ×");
            Add(SimParam.AntiRollScaleRear, 0.50, 1.80, "x", "VehicleParams.AntiRollRear ×");
            Add(SimParam.RideHeightOffsetM, -0.045, 0.030, "m", "VehicleParams.CgHeightM/RestLengthM/MaxCompressionM + offsets");
            Add(SimParam.PeakSlipDeg, 4, 12, "deg", "VehicleParams.PeakSlipDeg =");
            Add(SimParam.SlideGripFraction, 0.55, 0.90, "fraction", "VehicleParams.SlideGripFraction =");
            Add(SimParam.SlideFalloffDeg, 6, 20, "deg", "VehicleParams.SlideFalloffDeg =");
            Add(SimParam.PowerSlideGripLoss, 0.10, 0.60, "fraction", "VehicleParams.PowerSlideGripLoss =");
            Add(SimParam.AeroFrontShare, 0.30, 0.60, "fraction", "VehicleParams.AeroFrontShare =");
            return d;
        }
    }

    /// <summary>
    /// Mirror of the <c>VehicleParams</c> field initialisers and <c>VehicleFactory</c> constants that the engine-free
    /// resolver and PI estimate depend on. They MUST equal the Unity values; a Unity EditMode parity test is recommended
    /// (stock ResolvedCarSpec through VehicleFactory == VehicleFactory.Build(car, tuning)).
    /// </summary>
    public static class SimulationDefaults
    {
        public const double PeakSlipDeg = 7.5;
        public const double SlideGripFraction = 0.72;
        public const double SlideFalloffDeg = 12.0;
        public const double PowerSlideGripLoss = 0.32;
        public const double BrakeFrontBias = 0.62;
        public const double AeroFrontShare = 0.45;
        public const double RestLengthM = 0.22;
        public const double MaxCompressionM = 0.16;
        public const double ShiftSeconds = 0.14;
        public const double DrivelineEfficiency = 0.86;
        public const double TurboLagWhenUnset = 0.6;
        /// <summary>VehicleFactory: BrakeForceN = m × g × 1.15.</summary>
        public const double BrakeForceG = 1.15;
        /// <summary>VehicleFactory.BuildGearing: first gear tops out near 58 km/h.</summary>
        public const double FirstGearTopKmh = 58.0;
        /// <summary>VehicleFactory.BuildGearing: top gear reaches redline 4% above the drag-limited top speed.</summary>
        public const double TopGearMargin = 1.04;

        /// <summary>Share of a ride-height offset that moves the centre of gravity (the unsprung mass does not move).</summary>
        public const double RideHeightToCg = 0.85;
        /// <summary>Share of a ride-height offset taken from (lowering) or added to (raising) droop travel.</summary>
        public const double RideHeightToRestLength = 0.5;
        /// <summary>Share of a ride-height offset taken from (lowering) or added to (raising) bump travel.</summary>
        public const double RideHeightToMaxCompression = 0.5;
    }

    /// <summary>How a part effect changes one <see cref="SimParam"/>.</summary>
    public enum ModifierOp
    {
        /// <summary>Multiply (applied after all Set/Add on that parameter, in slot order).</summary>
        Scale,
        Add,
        /// <summary>Replace the base value (before Add/Scale).</summary>
        Set,
        /// <summary>Raise to at least this value (after Scale).</summary>
        AtLeast,
        /// <summary>
        /// MassKg only: remove this FRACTION of the chassis's authored weight-reduction allowance. The sum over all installed
        /// parts is capped at 1.0, so weight reduction is bounded per chassis (spec §9).
        /// </summary>
        ChassisWeightReduction,
    }

    public static class ModifierOps
    {
        public static bool TryParse(string op, out ModifierOp result)
        {
            switch (op)
            {
                case "scale": result = ModifierOp.Scale; return true;
                case "add": result = ModifierOp.Add; return true;
                case "set": result = ModifierOp.Set; return true;
                case "atLeast": result = ModifierOp.AtLeast; return true;
                case "chassisWeightReduction": result = ModifierOp.ChassisWeightReduction; return true;
                default: result = ModifierOp.Scale; return false;
            }
        }
    }

    /// <summary>
    /// Stable tuning keys. Every key belongs to exactly one slot; it is adjustable only while a part in that slot
    /// declares it. Values are integers (permille, percent or millimetres) so stored tunes are exact.
    /// </summary>
    public static class TuningKeys
    {
        public const string FinalDrive = "FinalDrive";
        public const string GearSpread = "GearSpread";
        public const string BrakeBias = "BrakeBias";
        public const string BrakePressure = "BrakePressure";
        public const string DiffLock = "DiffLock";
        public const string AwdFrontShare = "AwdFrontShare";
        public const string SpringFront = "SpringFront";
        public const string SpringRear = "SpringRear";
        public const string DamperFront = "DamperFront";
        public const string DamperRear = "DamperRear";
        public const string AntiRollFront = "AntiRollFront";
        public const string AntiRollRear = "AntiRollRear";
        public const string RideHeight = "RideHeight";
        public const string AeroLevel = "AeroLevel";
        public const string AeroBalance = "AeroBalance";

        static readonly Dictionary<string, PartSlot> slotOf = new Dictionary<string, PartSlot>(StringComparer.Ordinal)
        {
            { FinalDrive, PartSlot.Gearbox }, { GearSpread, PartSlot.Gearbox },
            { BrakeBias, PartSlot.Brakes }, { BrakePressure, PartSlot.Brakes },
            { DiffLock, PartSlot.Differential }, { AwdFrontShare, PartSlot.Differential },
            { SpringFront, PartSlot.Suspension }, { SpringRear, PartSlot.Suspension },
            { DamperFront, PartSlot.Suspension }, { DamperRear, PartSlot.Suspension },
            { AntiRollFront, PartSlot.Suspension }, { AntiRollRear, PartSlot.Suspension },
            { RideHeight, PartSlot.Suspension },
            { AeroLevel, PartSlot.Aero }, { AeroBalance, PartSlot.Aero },
        };

        public static IEnumerable<string> All => slotOf.Keys;

        public static bool TrySlotOf(string key, out PartSlot slot) => slotOf.TryGetValue(key ?? "", out slot);
    }

    /// <summary>Thrown when authored build data (parts.json, build-recipes.json) is structurally invalid.</summary>
    public sealed class BuildDataException : Exception
    {
        public BuildDataException(string message) : base(message) { }
    }
}
