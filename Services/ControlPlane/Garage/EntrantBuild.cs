using NightSignal.Core.Builds;

namespace NightSignal.ControlPlane.Garage;

/// <summary>
/// The frozen, server-resolved APPLIED build of one entrant's car instance (assignment <c>entrants[].vehicleBuild</c>).
/// Taken from the stored workspace at <c>event.start</c> (never a draft, preview or client claim) and re-resolved with Core
/// <see cref="BuildResolver"/>; <see cref="BuildHash"/> is the performance hash the entrant readied with.
/// The game server can rebuild the same vehicle either by resolving <see cref="Parts"/> + <see cref="Tuning"/> with Core
/// (then comparing BuildHash) or from <see cref="ParamsMicro"/>/<see cref="Chassis"/> directly.
/// </summary>
public sealed record EntrantBuild
{
    public string InstanceId { get; init; } = "";
    public string CarId { get; init; } = "";
    public long AppliedRevision { get; init; }
    public string BuildHash { get; init; } = "";
    /// <summary>Server PI estimate of this build (Core PerformanceIndexEstimator; labelled an estimate).</summary>
    public int Pi { get; init; }
    public string PiClass { get; init; } = "";
    public bool PiIsEstimate { get; init; } = true;
    public string HandlingModelVersion { get; init; } = "";
    public int PartsCatalogueRevision { get; init; }
    /// <summary>GarageContent.Hash of the parts/recipe documents the server resolved with.</summary>
    public string PartsCatalogueHash { get; init; } = "";
    /// <summary>Installed mechanical part id by slot id (absent slot = stock).</summary>
    public IReadOnlyDictionary<string, string> Parts { get; init; } = new Dictionary<string, string>();
    public string? UtilityPartId { get; init; }
    public int TuningVersion { get; init; }
    /// <summary>Explicit tuning integers by key (absent key = the installed part's default).</summary>
    public IReadOnlyDictionary<string, int> Tuning { get; init; } = new Dictionary<string, int>();
    public BuildUtility Utility { get; init; } = new();
    /// <summary>Every resolved simulation input (Core SimParam name → value × 10⁶, the exact integers BuildHash covers).</summary>
    public IReadOnlyDictionary<string, long> ParamsMicro { get; init; } = new Dictionary<string, long>();
    /// <summary>Core ChassisAdjustments (applied by VehicleFactory after its derivations).</summary>
    public ChassisValues Chassis { get; init; } = new();

    /// <summary>The Core applied build this record froze (for GarageOperations.RecordRaceBegan).</summary>
    public AppliedVehicleBuild ToApplied() => new()
    {
        Revision = AppliedRevision,
        Build = Snapshot(),
        BuildHash = BuildHash,
        Pi = Pi,
        PiClass = PiClass,
        HandlingModelVersion = HandlingModelVersion,
        PartsCatalogueRevision = PartsCatalogueRevision,
        Source = "frozen",
    };

    public MechanicalSnapshot Snapshot()
    {
        var s = new MechanicalSnapshot { UtilityPartId = UtilityPartId, Tuning = new TuningSetup { Version = TuningVersion } };
        foreach (var kv in Parts) s.Parts[kv.Key] = kv.Value;
        foreach (var kv in Tuning) s.Tuning.Values[kv.Key] = kv.Value;
        return s;
    }

    public static EntrantBuild From(string instanceId, string carId, AppliedVehicleBuild applied, ResolvedCarSpec spec, PiEstimate pi, string partsHash)
    {
        ChassisAdjustments c = spec.Chassis;
        return new EntrantBuild
        {
            InstanceId = instanceId,
            CarId = carId,
            AppliedRevision = applied.Revision,
            BuildHash = spec.BuildHash,
            Pi = pi.Value,
            PiClass = pi.Class.ToString(),
            PiIsEstimate = pi.IsEstimate,
            HandlingModelVersion = spec.HandlingModelVersion,
            PartsCatalogueRevision = spec.PartsCatalogueRevision,
            PartsCatalogueHash = partsHash,
            Parts = new SortedDictionary<string, string>(applied.Build.Parts ?? new SortedDictionary<string, string>(), StringComparer.Ordinal),
            UtilityPartId = string.IsNullOrEmpty(applied.Build.UtilityPartId) ? null : applied.Build.UtilityPartId,
            TuningVersion = applied.Build.Tuning?.Version ?? TuningModel.CurrentVersion,
            Tuning = new SortedDictionary<string, int>(applied.Build.Tuning?.Values ?? new SortedDictionary<string, int>(), StringComparer.Ordinal),
            Utility = new BuildUtility(spec.Utility.PartId, spec.Utility.IncomePercent, spec.Utility.ShowcasePercent),
            ParamsMicro = SimParams.All.ToDictionary(p => p.ToString(), spec.GetMicro),
            Chassis = new ChassisValues
            {
                FinalDriveScale = c.FinalDriveScale, GearSpreadScale = c.GearSpreadScale, ShiftSecondsScale = c.ShiftSecondsScale,
                BrakeForceScale = c.BrakeForceScale, BrakeFrontBias = c.BrakeFrontBias, SpringScaleFront = c.SpringScaleFront,
                SpringScaleRear = c.SpringScaleRear, DamperScaleFront = c.DamperScaleFront, DamperScaleRear = c.DamperScaleRear,
                AntiRollScaleFront = c.AntiRollScaleFront, AntiRollScaleRear = c.AntiRollScaleRear, CgHeightOffsetM = c.CgHeightOffsetM,
                RestLengthOffsetM = c.RestLengthOffsetM, MaxCompressionOffsetM = c.MaxCompressionOffsetM, PeakSlipDeg = c.PeakSlipDeg,
                SlideGripFraction = c.SlideGripFraction, SlideFalloffDeg = c.SlideFalloffDeg, PowerSlideGripLoss = c.PowerSlideGripLoss,
                AeroFrontShare = c.AeroFrontShare,
            },
        };
    }
}

/// <summary>The single utility item's economic/showcase effect (never physics): income 0/4/8 %, showcase 0/5/10 %.</summary>
public sealed record BuildUtility(string PartId = "", int IncomePercent = 0, int ShowcasePercent = 0);

/// <summary>Mirror of Core <see cref="ChassisAdjustments"/> with properties (System.Text.Json serialises properties only).</summary>
public sealed record ChassisValues
{
    public double FinalDriveScale { get; init; } = 1;
    public double GearSpreadScale { get; init; } = 1;
    public double ShiftSecondsScale { get; init; } = 1;
    public double BrakeForceScale { get; init; } = 1;
    public double BrakeFrontBias { get; init; }
    public double SpringScaleFront { get; init; } = 1;
    public double SpringScaleRear { get; init; } = 1;
    public double DamperScaleFront { get; init; } = 1;
    public double DamperScaleRear { get; init; } = 1;
    public double AntiRollScaleFront { get; init; } = 1;
    public double AntiRollScaleRear { get; init; } = 1;
    public double CgHeightOffsetM { get; init; }
    public double RestLengthOffsetM { get; init; }
    public double MaxCompressionOffsetM { get; init; }
    public double PeakSlipDeg { get; init; }
    public double SlideGripFraction { get; init; }
    public double SlideFalloffDeg { get; init; }
    public double PowerSlideGripLoss { get; init; }
    public double AeroFrontShare { get; init; }
}
