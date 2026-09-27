using System;
using UnityEngine;

namespace NightSignal.Vehicle
{
    public enum DriveLayout : byte { FWD = 0, RWD = 1, AWD = 2 }
    public enum EngineFamily : byte { Inline4 = 0, Six = 1, Triple = 2, Rotary = 3 }

    /// <summary>Driver assists. Anything that changes measured performance is tagged into ghost/challenge compatibility.</summary>
    [Serializable]
    public struct AssistSettings
    {
        public bool AutomaticGearbox;
        /// <summary>0 off, 1 low, 2 high.</summary>
        public byte TractionControl;
        public bool CountersteerAssist;
        public bool AntiLockBrakes;

        public static AssistSettings Default => new AssistSettings { AutomaticGearbox = true, TractionControl = 1, CountersteerAssist = false, AntiLockBrakes = true };

        /// <summary>Compact flags stored in ghost headers and results.</summary>
        public byte Flags => (byte)((AutomaticGearbox ? 1 : 0) | (TractionControl << 1) | (CountersteerAssist ? 8 : 0) | (AntiLockBrakes ? 16 : 0));
    }

    /// <summary>
    /// Fully resolved chassis parameters for one vehicle build (model + tune + assists). Every field is a
    /// measured tuning target; nothing here is hidden from the player-facing stat harness.
    /// </summary>
    [Serializable]
    public sealed class VehicleParams
    {
        // Mass and geometry
        public float MassKg = 1200f;
        public float LengthM = 4.3f;
        public float WidthM = 1.7f;
        public float HeightM = 1.32f;
        public float WheelbaseM = 2.5f;
        public float TrackM = 1.46f;
        public float CgHeightM = 0.48f;
        public float FrontWeightFraction = 0.53f;
        public float InertiaScale = 1.1f;

        // Suspension (per corner)
        public float RestLengthM = 0.22f;
        public float MaxCompressionM = 0.16f;
        public float WheelRadiusM = 0.31f;
        public float SpringFront = 30000f;
        public float SpringRear = 28000f;
        public float DamperFront = 2200f;
        public float DamperRear = 2000f;
        public float AntiRollFront = 14000f;
        public float AntiRollRear = 9000f;
        /// <summary>Fraction of CG height at which tyre lateral forces act (arcade roll-centre raise; 0 = contact patch).</summary>
        public float RollCentreRaise = 0.45f;

        // Tyres
        public float TyreGrip = 1.12f;
        public float PeakSlipDeg = 7.5f;
        public float SlideGripFraction = 0.72f;
        public float SlideFalloffDeg = 12f;
        public float RearGripBias = 1.0f;
        /// <summary>
        /// Lateral grip a sliding driven wheel loses at full throttle (spun-up tyre). This is what lets throttle
        /// hold, deepen or (by lifting) recover a slide even on low-power cars.
        /// </summary>
        public float PowerSlideGripLoss = 0.32f;
        /// <summary>
        /// Yaw damping (1/s) reached at 70° body slip, starting from zero at 30°. Approximates tyre relaxation at
        /// extreme angles so a large slide is catchable with countersteer rather than an instant spin (spec §6:
        /// forgiving recovery, no permanent spin-locks). Identical rule for every car and for AI.
        /// </summary>
        public float HighSlipYawDamping = 2.6f;

        // Engine and driveline
        public EngineFamily Engine = EngineFamily.Inline4;
        public float PeakPowerKw = 150f;
        public float PeakTorqueNm = 220f;
        public float IdleRpm = 900f;
        public float PeakPowerRpm = 6800f;
        public float RedlineRpm = 7400f;
        public float LaunchRpm = 3200f;
        public bool Turbocharged;
        /// <summary>Seconds for boost to spool from off-throttle to full.</summary>
        public float TurboLagSeconds = 0.6f;
        public float[] GearRatios = { 3.3f, 2.1f, 1.5f, 1.15f, 0.93f, 0.78f };
        public float ReverseRatio = 3.2f;
        public float FinalDrive = 4.1f;
        public float DrivelineEfficiency = 0.86f;
        public float ShiftSeconds = 0.14f;
        public DriveLayout Drive = DriveLayout.RWD;
        /// <summary>AWD share of drive force sent to the front axle.</summary>
        public float AwdFrontShare = 0.4f;

        // Brakes
        public float BrakeForceN = 14000f;
        public float BrakeFrontBias = 0.62f;

        // Steering
        public float MaxSteerDeg = 33f;
        public float HighSpeedSteerFraction = 0.32f;
        public float SteerRateDegPerSec = 260f;
        public float CountersteerAssistGain = 0.8f;

        // Aerodynamics
        public float DragAreaCdA = 0.68f;
        public float LiftAreaClA = 0.12f;
        public float AeroFrontShare = 0.45f;

        public AssistSettings Assists = AssistSettings.Default;

        public float FrontAxleZ => WheelbaseM * (1f - FrontWeightFraction);
        public float RearAxleZ => -WheelbaseM * FrontWeightFraction;
        public Vector3 BodyHalfExtents => new Vector3(WidthM * 0.5f, HeightM * 0.32f, LengthM * 0.5f);
        public Vector3 BodyCentreOffset => new Vector3(0f, HeightM * 0.12f, (FrontAxleZ + RearAxleZ) * 0.5f);

        public Vector3 WheelMount(int i)
        {
            float x = (i % 2 == 0 ? -0.5f : 0.5f) * TrackM;
            float z = i < 2 ? FrontAxleZ : RearAxleZ;
            float y = -CgHeightM + WheelRadiusM + RestLengthM;
            return new Vector3(x, y, z);
        }

        /// <summary>Diagonal inertia tensor (local x = pitch, y = yaw, z = roll) from a box approximation.</summary>
        public Vector3 Inertia
        {
            get
            {
                float m = MassKg * InertiaScale / 12f;
                return new Vector3(m * (HeightM * HeightM + LengthM * LengthM),
                                   m * (WidthM * WidthM + LengthM * LengthM),
                                   m * (WidthM * WidthM + HeightM * HeightM));
            }
        }

        /// <summary>
        /// Engine torque (Nm) at wide-open throttle. Piecewise: 65% of peak at idle, peak from ~50–70% of the
        /// power-peak rpm, then falling so that power equals <see cref="PeakPowerKw"/> at <see cref="PeakPowerRpm"/>.
        /// </summary>
        public float TorqueAt(float rpm)
        {
            float powerTorque = PeakPowerKw * 1000f / (PeakPowerRpm * Mathf.PI / 30f);
            float tqStart = PeakPowerRpm * 0.5f;
            float tqEnd = PeakPowerRpm * 0.7f;
            if (rpm <= IdleRpm) return PeakTorqueNm * 0.65f;
            if (rpm < tqStart) return Mathf.Lerp(PeakTorqueNm * 0.65f, PeakTorqueNm, (rpm - IdleRpm) / (tqStart - IdleRpm));
            if (rpm < tqEnd) return Mathf.Lerp(PeakTorqueNm, PeakTorqueNm * 0.98f, (rpm - tqStart) / (tqEnd - tqStart));
            if (rpm < PeakPowerRpm) return Mathf.Lerp(PeakTorqueNm * 0.98f, powerTorque, (rpm - tqEnd) / (PeakPowerRpm - tqEnd));
            return Mathf.Lerp(powerTorque, powerTorque * 0.85f, (rpm - PeakPowerRpm) / Mathf.Max(1f, RedlineRpm - PeakPowerRpm));
        }

        public float MaxSpeedInGear(int gear) =>
            RedlineRpm / (GearRatios[gear - 1] * FinalDrive) * (Mathf.PI / 30f) * WheelRadiusM;

        public int TopGear => GearRatios.Length;

        public VehicleParams Clone()
        {
            var c = (VehicleParams)MemberwiseClone();
            c.GearRatios = (float[])GearRatios.Clone();
            return c;
        }
    }
}
