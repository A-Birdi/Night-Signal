using System;
using UnityEngine;

namespace NightSignal.Vehicle
{
    /// <summary>Measured (not estimated) handling figures from the repeatable harness, with test conditions.</summary>
    [Serializable]
    public struct HandlingReport
    {
        public float ZeroTo100Seconds;       // flat dry asphalt, launch from rest, automatic gearbox
        public float SpeedAfter1000mKmh;     // same run, speed after 1,000 m
        public float Brake100To0Metres;      // full braking with ABS from 100 km/h
        public float SkidpadLateralG;        // highest steady lateral acceleration on a 60 m radius circle
        public float DriftHoldSeconds;       // seconds held between 15° and 45° slip under a scripted driver
        public float DriftMeanSlipDeg;
        public bool DriftSpun;               // exceeded 80° slip (a spin) during the drift test
        public string Conditions;
    }

    /// <summary>
    /// Scripted manoeuvres on an analytic plane (spec §9: "Run a repeatable test track/dyno harness").
    /// Deterministic: identical params give identical reports. Used by the garage stat screen and tests.
    /// </summary>
    public static class HandlingHarness
    {
        public const string ConditionsText = "Harness v1: flat dry asphalt plane, sea-level air, automatic gearbox, ABS on, traction control low, 60 Hz.";

        public static HandlingReport Measure(VehicleParams baseParams)
        {
            VehicleParams p = baseParams.Clone();
            p.Assists = new AssistSettings { AutomaticGearbox = true, TractionControl = 1, AntiLockBrakes = true, CountersteerAssist = false };
            var report = new HandlingReport { Conditions = ConditionsText };
            Accelerate(p, ref report);
            report.Brake100To0Metres = Brake(p);
            report.SkidpadLateralG = Skidpad(p);
            Drift(p, ref report);
            return report;
        }

        static VehicleState Settled(VehicleSimulation sim)
        {
            VehicleParams p = sim.Params;
            var s = VehicleState.AtRest(new Vector3(0f, p.CgHeightM, 0f), Quaternion.identity);
            for (int i = 0; i < 90; i++) sim.Step(ref s, DriverInput.Quantize(0f, 0f, 1f, InputButtons.None));
            s.Velocity = Vector3.zero;
            s.AngularVelocity = Vector3.zero;
            s.Gear = 1;
            return s;
        }

        static void Accelerate(VehicleParams p, ref HandlingReport r)
        {
            var sim = new VehicleSimulation(p, PlaneVehicleWorld.Flat);
            VehicleState s = Settled(sim);
            Vector3 start = s.Position;
            DriverInput full = DriverInput.Quantize(0f, 1f, 0f, InputButtons.None);
            r.ZeroTo100Seconds = -1f;
            for (int tick = 1; tick <= 60 * 90; tick++)
            {
                sim.Step(ref s, full);
                if (r.ZeroTo100Seconds < 0f && s.SpeedKmh >= 100f) r.ZeroTo100Seconds = tick * VehicleSimulation.TickDt;
                if (Vector3.Distance(s.Position, start) >= 1000f) { r.SpeedAfter1000mKmh = s.SpeedKmh; break; }
            }
        }

        static float Brake(VehicleParams p)
        {
            var sim = new VehicleSimulation(p, PlaneVehicleWorld.Flat);
            VehicleState s = Settled(sim);
            s.Velocity = s.Rotation * Vector3.forward * (100f / 3.6f);
            s.Gear = 3;
            Vector3 start = s.Position;
            DriverInput brake = DriverInput.Quantize(0f, 0f, 1f, InputButtons.None);
            for (int tick = 0; tick < 60 * 20 && s.Velocity.magnitude > 0.1f; tick++)
                sim.Step(ref s, brake);
            return Vector3.Distance(s.Position, start);
        }

        /// <summary>
        /// Pure-pursuit driver on a 60 m circle (clockwise, turning right). Speed ramps slowly; the result is the
        /// highest one-second mean lateral acceleration while the car stays within 2 m of the line.
        /// </summary>
        static float Skidpad(VehicleParams p)
        {
            const float radius = 60f;
            const float lookahead = 10f;
            var sim = new VehicleSimulation(p, PlaneVehicleWorld.Flat);
            VehicleState s = Settled(sim);
            // Start at (-R, 0) heading +z: with the centre at the origin the circle turns right.
            s.Position = new Vector3(-radius, s.Position.y, 0f);
            s.Rotation = Quaternion.LookRotation(Vector3.forward);
            float targetSpeed = 14f;
            float bestG = 0f, windowG = 0f;
            int window = 0;
            for (int tick = 0; tick < 60 * 150; tick++)
            {
                Vector3 flat = new Vector3(s.Position.x, 0f, s.Position.z);
                float error = flat.magnitude - radius;
                // Aim at the point on the circle `lookahead` metres ahead (clockwise from above).
                float angleNow = Mathf.Atan2(flat.z, flat.x);
                float angleAhead = angleNow - lookahead / radius;
                Vector3 aim = new Vector3(Mathf.Cos(angleAhead), 0f, Mathf.Sin(angleAhead)) * radius;
                Vector3 fwd = s.Rotation * Vector3.forward;
                float alpha = Vector3.SignedAngle(new Vector3(fwd.x, 0f, fwd.z), aim - flat, Vector3.up) * Mathf.Deg2Rad;
                float wheelAngle = Mathf.Atan(2f * p.WheelbaseM * Mathf.Sin(alpha) / Mathf.Max(1f, (aim - flat).magnitude));
                float speed = s.Velocity.magnitude;
                float limit = VehicleSimulation.SteeringLimit(p, speed, Mathf.Abs(sim.Telemetry.BodySlipDeg) * Mathf.Deg2Rad);
                float steer = Mathf.Clamp(wheelAngle / limit, -1f, 1f);
                float throttle = Mathf.Clamp01((targetSpeed - speed) * 0.4f + 0.2f);
                sim.Step(ref s, DriverInput.Quantize(steer, throttle, 0f, InputButtons.None));

                targetSpeed += 0.15f * VehicleSimulation.TickDt; // +0.15 m/s per second (reaches >2 g if grip allowed)
                if (Mathf.Abs(error) < 2f)
                {
                    windowG += Mathf.Abs(sim.Telemetry.LateralG);
                    if (++window == 60)
                    {
                        bestG = Mathf.Max(bestG, windowG / window);
                        windowG = 0f;
                        window = 0;
                    }
                }
                else
                {
                    windowG = 0f;
                    window = 0;
                    if (error > 8f) break; // cannot hold the circle any more
                }
            }
            return bestG;
        }

        /// <summary>
        /// Scripted drift: enter at 75 km/h with a handbrake flick, then a simple driver counter-steers toward the
        /// velocity and modulates throttle to hold ~30° of slip. Measures how long the car stays in the 15–45° band.
        /// </summary>
        static void Drift(VehicleParams p, ref HandlingReport r)
        {
            var sim = new VehicleSimulation(p, PlaneVehicleWorld.Flat);
            VehicleState s = Settled(sim);
            s.Velocity = s.Rotation * Vector3.forward * (75f / 3.6f);
            s.Gear = 2;
            float held = 0f, slipSum = 0f;
            int samples = 0;
            bool spun = false;
            const float targetSlip = 28f;
            bool entering = true;
            int controlStart = 0;
            for (int tick = 0; tick < 60 * 8; tick++)
            {
                DriverInput input;
                float slip = sim.Telemetry.BodySlipDeg;                  // − = velocity left of heading (rotating right)
                if (entering && (Mathf.Abs(slip) >= 12f || tick >= 60))
                {
                    entering = false;
                    controlStart = tick;
                }
                if (entering)
                {
                    // Turn in with a held handbrake until the rear has stepped out (or give up after 1 s).
                    input = DriverInput.Quantize(1f, 0.5f, 0f, InputButtons.Handbrake);
                }
                else
                {
                    // Scripted drifter: aim the front wheels where the front axle is travelling, add countersteer when
                    // the slip exceeds the target (and less when below), and trim the slip with throttle.
                    Vector3 fwdNow = s.Rotation * Vector3.forward;
                    Vector3 frontVel = s.Velocity + Vector3.Cross(s.AngularVelocity, s.Rotation * new Vector3(0f, 0f, p.FrontAxleZ));
                    float frontAngle = Vector3.SignedAngle(fwdNow, Vector3.ProjectOnPlane(frontVel, Vector3.up), Vector3.up);
                    float excess = Mathf.Abs(slip) - targetSlip;
                    float wheelDeg = frontAngle + Mathf.Sign(frontAngle) * excess * 0.4f;
                    float speed = s.Velocity.magnitude;
                    float limitDeg = VehicleSimulation.SteeringLimit(p, speed, Mathf.Abs(slip) * Mathf.Deg2Rad) * Mathf.Rad2Deg;
                    float steer = Mathf.Clamp(wheelDeg / limitDeg, -1f, 1f);
                    float throttle = Mathf.Clamp01(0.6f - excess * 0.04f);
                    input = DriverInput.Quantize(steer, throttle, 0f, InputButtons.None);
                }
                sim.Step(ref s, input);
                float a = Mathf.Abs(sim.Telemetry.BodySlipDeg);
                if (a > 80f) spun = true;
                if (!entering && tick > controlStart)
                {
                    if (a >= 15f && a <= 45f) held += VehicleSimulation.TickDt;
                    slipSum += a;
                    samples++;
                }
                if (s.Velocity.magnitude < 3f) break;
            }
            r.DriftHoldSeconds = held;
            r.DriftMeanSlipDeg = samples > 0 ? slipSum / samples : 0f;
            r.DriftSpun = spun;
        }
    }
}
