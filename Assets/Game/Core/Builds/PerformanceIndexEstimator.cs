using System;
using NightSignal.Core.Rules;

namespace NightSignal.Core.Builds
{
    /// <summary>Engine-free figures derived from a resolved spec with the same formulas VehicleFactory uses. Estimates, not measurements.</summary>
    public sealed class DerivedFigures
    {
        /// <summary>Drag/rolling-limited top speed (VehicleFactory.DragLimitedTopSpeed mirror).</summary>
        public double DragLimitedTopSpeedKmh;
        /// <summary>min(drag limit, redline in top gear) — gearing can cap it.</summary>
        public double ReachableTopSpeedKmh;
        public bool GearLimited;
        public double FirstGearTopKmh;
        public double PowerToWeightKwPerTonne;
        /// <summary>Power-to-weight after gearing use, turbo lag, shift time and traction factors.</summary>
        public double EffectivePowerToWeight;
        /// <summary>Lateral grip coefficient at 100 km/h including downforce and chassis factors.</summary>
        public double CorneringGrip;
        public double BrakingDecelG;
        public double RpmDropPerShift;
    }

    public sealed class PiEstimate
    {
        public int Value;
        public PerformanceClass Class;
        /// <summary>Always true in this revision: labelled "estimated" until the Unity handling harness calibrates it.</summary>
        public bool IsEstimate = true;
        public string Basis = PerformanceIndexEstimator.Basis;
        public double LapTimeProxy;
        public DerivedFigures Figures;
    }

    /// <summary>
    /// Estimated Performance Index of a resolved build, consistent with Rules.PerformanceIndex (100–999, classes D–S).
    /// The stock build of every model returns exactly its catalogue BasePI; a build moves away from it by
    /// −k × Δln(lapTimeProxy), where the proxy weights cornering, acceleration, braking and reachable top speed for mountain
    /// courses and k was fitted by least squares over the 18 stock models (see tests: fitted k and residual RMS).
    /// It is an ESTIMATE pending calibration against the Unity HandlingHarness and real laps; it is not a measurement.
    /// </summary>
    public static class PerformanceIndexEstimator
    {
        public const string Basis = "PI estimate (hm-1 analytic lap-time proxy anchored to catalogue BasePI; pending Unity handling-harness calibration)";

        /// <summary>PI points per unit of ln(lap-time proxy). Fitted to the 18 catalogue BasePIs (see BuildsTests).</summary>
        public const double PiPerLogLapTime = 3185.0;

        public const double WeightCorner = 0.45;
        public const double WeightAccel = 0.30;
        public const double WeightBrake = 0.13;
        public const double WeightTop = 0.12;

        const double Gravity = 9.81;
        const double AirDensity = 1.225;
        /// <summary>Reference cornering speed for the downforce term (100 km/h).</summary>
        const double CornerRefSpeed = 27.8;

        public static PiEstimate Estimate(ResolvedCarSpec build, ResolvedCarSpec stock, int basePi)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            if (stock == null) throw new ArgumentNullException(nameof(stock));
            DerivedFigures f = Figures(build);
            double tBuild = LapTimeProxy(f);
            double tStock = LapTimeProxy(Figures(stock));
            double raw = basePi - PiPerLogLapTime * (Math.Log(tBuild) - Math.Log(tStock));
            int pi = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
            pi = Math.Max(PerformanceIndex.Min, Math.Min(PerformanceIndex.Max, pi));
            return new PiEstimate { Value = pi, Class = PerformanceIndex.ClassOf(pi), LapTimeProxy = tBuild, Figures = f };
        }

        public static double LapTimeProxy(DerivedFigures f) =>
            WeightCorner * Math.Pow(f.CorneringGrip / 1.15, -0.5) +
            WeightAccel * Math.Pow(f.EffectivePowerToWeight / 0.18, -1.0 / 3.0) +
            WeightBrake * Math.Pow(f.BrakingDecelG / 1.10, -0.5) +
            WeightTop * Math.Pow(f.ReachableTopSpeedKmh / 3.6 / 80.0, -1.0);

        public static DerivedFigures Figures(ResolvedCarSpec s)
        {
            double m = s.Get(SimParam.MassKg);
            double power = s.Get(SimParam.PowerKw);
            ChassisAdjustments ch = s.Chassis;
            var f = new DerivedFigures();

            double vDrag = DragLimitedTopSpeed(power, m, s.Get(SimParam.DragAreaCdA));
            double finalDrive = ch.FinalDriveScale;
            double vGear = SimulationDefaults.TopGearMargin * vDrag / finalDrive;
            f.DragLimitedTopSpeedKmh = vDrag * 3.6;
            f.GearLimited = vGear < vDrag;
            f.ReachableTopSpeedKmh = Math.Min(vDrag, vGear) * 3.6;

            // Gearing: VehicleFactory spreads ratios geometrically between a 58 km/h first gear and top gear; the spread
            // exponent pivots about top gear, the final drive scales everything.
            int gears = Math.Max(4, (int)Math.Round(s.Get(SimParam.Gears)));
            double spread = SimulationDefaults.TopGearMargin * vDrag * 3.6 / SimulationDefaults.FirstGearTopKmh;
            double spreadScaled = Math.Pow(spread, ch.GearSpreadScale);
            double stepRatio = Math.Pow(spreadScaled, 1.0 / (gears - 1));
            f.RpmDropPerShift = 1 - 1 / stepRatio;
            f.FirstGearTopKmh = SimulationDefaults.TopGearMargin * vDrag * 3.6 / spreadScaled / finalDrive;
            double bandUse = 1 - 0.35 * f.RpmDropPerShift;
            double gearing = Math.Pow(finalDrive, 0.30) * bandUse;
            double launch = Math.Pow(58.0 / Math.Max(30.0, f.FirstGearTopKmh), 0.10);

            bool turbo = s.Get(SimParam.Turbo) >= 0.5;
            double lag = turbo ? 1 - 0.10 * s.Get(SimParam.TurboLagSeconds) : 1;
            double shift = 1 - 0.15 * SimulationDefaults.ShiftSeconds * ch.ShiftSecondsScale;
            string drive = s.Car.Drive;
            double pw = power / m;
            double traction = drive == "AWD" ? 1.0 : drive == "RWD" ? 0.97 : 0.95 - 0.3 * Math.Max(0, pw - 0.18);
            f.PowerToWeightKwPerTonne = pw * 1000;
            f.EffectivePowerToWeight = pw * gearing * launch * lag * shift * traction;

            // Cornering: tyre mu + downforce at 100 km/h + small tyre-curve and chassis terms.
            double downforce = 0.5 * AirDensity * s.Get(SimParam.LiftAreaClA) * CornerRefSpeed * CornerRefSpeed;
            double mu = s.Get(SimParam.TyreGrip) * (1 + downforce / (m * Gravity));
            double tyreCurve = 1 + 0.12 * (ch.SlideGripFraction - SimulationDefaults.SlideGripFraction);
            double springAvg = (ch.SpringScaleFront + ch.SpringScaleRear) * 0.5;
            double rollAvg = (ch.AntiRollScaleFront + ch.AntiRollScaleRear) * 0.5;
            double ride = s.Get(SimParam.RideHeightOffsetM);
            double chassis = 1 + 0.02 * Clamp(springAvg - 1, -0.3, 0.5) + 0.01 * Clamp(rollAvg - 1, -0.5, 0.6)
                             - 0.25 * ride - 0.4 * Math.Max(0, -ride - 0.025);
            double aeroBalance = 1 - 0.3 * Sq(ch.AeroFrontShare - SimulationDefaults.AeroFrontShare);
            double track = 1 + 0.10 * (s.Get(SimParam.TrackM) / Math.Max(1.0, s.Tuning.WidthM) - 0.85);
            f.CorneringGrip = mu * tyreCurve * chassis * aeroBalance * track;

            double biasEfficiency = 1 - 1.5 * Sq(ch.BrakeFrontBias - SimulationDefaults.BrakeFrontBias);
            f.BrakingDecelG = Math.Min(SimulationDefaults.BrakeForceG * ch.BrakeForceScale * biasEfficiency, mu * 0.98);
            return f;
        }

        /// <summary>Mirror of VehicleFactory.DragLimitedTopSpeed (m/s): wheel power vs aero drag + 1.3% rolling resistance.</summary>
        public static double DragLimitedTopSpeed(double powerKw, double massKg, double cda)
        {
            double wheelPower = powerKw * 1000 * SimulationDefaults.DrivelineEfficiency;
            double v = 40;
            for (int i = 0; i < 50; i++)
            {
                double resist = 0.5 * AirDensity * cda * v * v * v + 0.013 * massKg * Gravity * v;
                v *= Math.Pow(wheelPower / resist, 0.25);
            }
            return v;
        }

        static double Sq(double x) => x * x;

        static double Clamp(double x, double lo, double hi) => Math.Max(lo, Math.Min(hi, x));
    }
}
