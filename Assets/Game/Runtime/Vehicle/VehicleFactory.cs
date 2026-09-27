using System;
using NightSignal.Core.Content;
using UnityEngine;

namespace NightSignal.Vehicle
{
    /// <summary>
    /// Resolves chassis parameters for a car model from its catalogue entry (mass, power, torque, layout) and
    /// authored tuning (geometry, engine family, tyres). Derived values — gearing, springs, dampers, brakes —
    /// come from documented physical rules so every model is tuned the same way.
    /// </summary>
    public static class VehicleFactory
    {
        public static VehicleParams Build(CarDef car, CarTuningDef tuning, AssistSettings assists, float wheelRadius = 0.31f)
        {
            if (car == null) throw new ArgumentNullException(nameof(car));
            if (tuning == null) throw new ArgumentNullException(nameof(tuning), $"No tuning for {car.Id}");

            var p = new VehicleParams
            {
                MassKg = (float)car.MassKg,
                PeakPowerKw = (float)car.PowerKw,
                PeakTorqueNm = (float)car.TorqueNm,
                Drive = car.Drive == "FWD" ? DriveLayout.FWD : car.Drive == "AWD" ? DriveLayout.AWD : DriveLayout.RWD,
                LengthM = (float)tuning.LengthM,
                WidthM = (float)tuning.WidthM,
                HeightM = (float)tuning.HeightM,
                WheelbaseM = (float)tuning.WheelbaseM,
                TrackM = (float)tuning.TrackM,
                FrontWeightFraction = (float)tuning.FrontWeight,
                TyreGrip = (float)tuning.TyreGrip,
                RearGripBias = (float)tuning.RearGripBias,
                InertiaScale = (float)tuning.InertiaScale,
                DragAreaCdA = (float)tuning.DragAreaCdA,
                LiftAreaClA = (float)tuning.LiftAreaClA,
                AwdFrontShare = (float)tuning.AwdFrontShare,
                Turbocharged = tuning.Turbo,
                TurboLagSeconds = tuning.TurboLagSeconds > 0 ? (float)tuning.TurboLagSeconds : 0.6f,
                Engine = ParseFamily(tuning.EngineFamily),
                Assists = assists,
                WheelRadiusM = wheelRadius,
            };
            p.CgHeightM = Mathf.Clamp(p.HeightM * 0.36f, 0.4f, 0.55f);

            // Engine speeds: the power peak must be reachable with the rated torque (T·ω = P).
            float rpmForPower = p.PeakPowerKw * 1000f / (p.PeakTorqueNm * 0.97f) * (30f / Mathf.PI);
            p.RedlineRpm = Mathf.Max((float)tuning.RedlineRpm, rpmForPower + 300f);
            p.PeakPowerRpm = Mathf.Clamp(p.RedlineRpm - 500f, rpmForPower, p.RedlineRpm - 150f);
            p.LaunchRpm = Mathf.Lerp(p.IdleRpm, p.PeakPowerRpm, 0.42f);

            BuildGearing(p, Mathf.Max(4, tuning.Gears));
            BuildSuspension(p);

            p.BrakeForceN = p.MassKg * 9.81f * 1.15f;
            return p;
        }

        static EngineFamily ParseFamily(string f)
        {
            switch (f)
            {
                case "six": return EngineFamily.Six;
                case "triple": return EngineFamily.Triple;
                case "rotary": return EngineFamily.Rotary;
                default: return EngineFamily.Inline4;
            }
        }

        /// <summary>
        /// Top gear reaches redline 4% above the drag-limited top speed; first gear tops out near 58 km/h;
        /// intermediate ratios are geometric.
        /// </summary>
        static void BuildGearing(VehicleParams p, int gears)
        {
            float vTop = DragLimitedTopSpeed(p);
            float redlineRad = p.RedlineRpm * Mathf.PI / 30f;
            float topOverall = redlineRad * p.WheelRadiusM / (vTop * 1.04f);
            float firstOverall = redlineRad * p.WheelRadiusM / (58f / 3.6f);
            p.FinalDrive = 4.0f;
            p.GearRatios = new float[gears];
            for (int i = 0; i < gears; i++)
            {
                float t = gears == 1 ? 1f : (float)i / (gears - 1);
                p.GearRatios[i] = firstOverall * Mathf.Pow(topOverall / firstOverall, t) / p.FinalDrive;
            }
            p.ReverseRatio = p.GearRatios[0] * 1.05f;
        }

        public static float DragLimitedTopSpeed(VehicleParams p)
        {
            float wheelPower = p.PeakPowerKw * 1000f * p.DrivelineEfficiency;
            float v = 40f;
            for (int i = 0; i < 50; i++)
            {
                float resist = 0.5f * 1.225f * p.DragAreaCdA * v * v * v + 0.013f * p.MassKg * 9.81f * v;
                v *= Mathf.Pow(wheelPower / resist, 0.25f);
            }
            return v;
        }

        /// <summary>Ride frequencies 1.9 Hz front / 1.8 Hz rear, damping ratio 0.32, anti-roll from layout.</summary>
        static void BuildSuspension(VehicleParams p)
        {
            float frontCorner = p.MassKg * p.FrontWeightFraction * 0.5f;
            float rearCorner = p.MassKg * (1f - p.FrontWeightFraction) * 0.5f;
            p.SpringFront = frontCorner * Sq(2f * Mathf.PI * 1.9f);
            p.SpringRear = rearCorner * Sq(2f * Mathf.PI * 1.8f);
            p.DamperFront = 2f * 0.32f * Mathf.Sqrt(p.SpringFront * frontCorner);
            p.DamperRear = 2f * 0.32f * Mathf.Sqrt(p.SpringRear * rearCorner);
            bool fwd = p.Drive == DriveLayout.FWD;
            p.AntiRollFront = p.SpringFront * (fwd ? 0.35f : 0.55f);
            p.AntiRollRear = p.SpringRear * (fwd ? 0.65f : 0.35f);
        }

        static float Sq(float x) => x * x;
    }
}
