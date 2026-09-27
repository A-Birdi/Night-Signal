using UnityEngine;

namespace NightSignal.Vehicle
{
    public struct WheelTelemetry
    {
        public bool Grounded;
        public float Compression;
        public float LoadN;
        public float SlipAngleDeg;
        public bool Sliding;
        /// <summary>Signed wheel rotation speed, rad/s (for visuals and audio).</summary>
        public float AngularSpeed;
        public SurfaceKind Surface;
    }

    /// <summary>Per-tick facts consumed by visuals, audio, drift scoring and the race server's incident tracking.</summary>
    public struct StepTelemetry
    {
        public WheelTelemetry W0, W1, W2, W3;
        public int GroundedWheels;
        public float BodySlipDeg;
        public float LateralG;
        public float LongitudinalG;
        public bool WallContact;
        public float WallImpactSpeed;
        public int WallSurfaceId;
        public Vector3 WallNormal;
        public bool Wheelspin;
        public bool OnLooseSurface;

        public WheelTelemetry Wheel(int i)
        {
            switch (i) { case 0: return W0; case 1: return W1; case 2: return W2; default: return W3; }
        }

        internal void SetWheel(int i, WheelTelemetry w)
        {
            switch (i) { case 0: W0 = w; break; case 1: W1 = w; break; case 2: W2 = w; break; default: W3 = w; break; }
        }
    }

    /// <summary>
    /// Fixed-step arcade chassis (spec §6): four raycast suspension contacts, a friction-circle tyre model with
    /// a saturating lateral curve (so drifts live around 15–45° with countersteer/throttle control), explicit
    /// engine/gearbox, aero, and barrier depenetration with anti-tunnelling sweeps. Pure function of
    /// (state, quantized input, params, static world), so the server simulates it authoritatively and clients
    /// replay it for prediction. Never depends on PhysX rigidbody integration.
    /// </summary>
    public sealed class VehicleSimulation
    {
        public const int TickRate = 60;
        public const float TickDt = 1f / TickRate;
        public const int Substeps = 2;
        const float Gravity = 9.81f;
        const float AirDensity = 1.225f;
        const float WallRestitution = 0.12f;

        readonly VehicleParams p;
        readonly IVehicleWorld world;
        readonly BarrierContact[] contacts = new BarrierContact[8];
        readonly float[] springForce = new float[4];
        readonly bool[] grounded = new bool[4];
        readonly GroundHit[] hits = new GroundHit[4];

        public VehicleParams Params => p;
        /// <summary>Weather/surface grip for the whole course: dry 1.0, damp 0.88, wet 0.76.</summary>
        public float SurfaceGripScale = 1f;
        public StepTelemetry Telemetry;

        public VehicleSimulation(VehicleParams parameters, IVehicleWorld world)
        {
            p = parameters;
            this.world = world;
        }

        public void Step(ref VehicleState s, DriverInput input)
        {
            Telemetry = default;
            TickControls(ref s, input);
            float dt = TickDt / Substeps;
            Vector3 v0 = s.Velocity;
            for (int k = 0; k < Substeps; k++)
                Integrate(ref s, input, dt);

            Vector3 accel = (s.Velocity - v0) / TickDt;
            Vector3 fwd = s.Rotation * Vector3.forward;
            Vector3 right = s.Rotation * Vector3.right;
            Telemetry.LongitudinalG = Vector3.Dot(accel, fwd) / Gravity;
            Telemetry.LateralG = Vector3.Dot(accel, right) / Gravity;
            float vf = Vector3.Dot(s.Velocity, fwd);
            float vr = Vector3.Dot(s.Velocity, right);
            Telemetry.BodySlipDeg = s.Velocity.sqrMagnitude > 4f ? Mathf.Atan2(vr, Mathf.Abs(vf)) * Mathf.Rad2Deg : 0f;
            s.Tick++;
        }

        void TickControls(ref VehicleState s, DriverInput input)
        {
            float dt = TickDt;
            Vector3 fwd = s.Rotation * Vector3.forward;
            Vector3 right = s.Rotation * Vector3.right;
            float vFwd = Vector3.Dot(s.Velocity, fwd);
            float vRight = Vector3.Dot(s.Velocity, right);
            float speed = s.Velocity.magnitude;

            // Speed-sensitive steering: full stick maps to the wheel angle the front tyres can actually use at this
            // speed (kinematic angle for ~1.35× grip-limited lateral acceleration plus the tyre's peak slip), but
            // never less than the current body slip + 8° so the driver can always countersteer a drift.
            float maxSteer = p.MaxSteerDeg * Mathf.Deg2Rad;
            float bodySlip = speed > 3f ? Mathf.Abs(Mathf.Atan2(vRight, Mathf.Abs(vFwd))) : 0f;
            float target = input.Steer * SteeringLimit(p, speed, bodySlip);
            if (p.Assists.CountersteerAssist && speed > 6f && vFwd > 0f)
            {
                float slip = Mathf.Atan2(vRight, vFwd);
                if (Mathf.Abs(slip) > 4f * Mathf.Deg2Rad)
                    target += slip * p.CountersteerAssistGain;
            }
            target = Mathf.Clamp(target, -maxSteer, maxSteer);
            s.SteerAngle = Mathf.MoveTowards(s.SteerAngle, target, p.SteerRateDegPerSec * Mathf.Deg2Rad * dt);

            // Gearbox.
            if (s.ShiftTimer > 0f) s.ShiftTimer = Mathf.Max(0f, s.ShiftTimer - dt);
            if (p.Assists.AutomaticGearbox)
            {
                if (s.Gear >= 1 && vFwd < 0.6f && input.Brake > 0.3f && input.Throttle < 0.05f)
                {
                    s.ReverseTimer += dt;
                    if (s.ReverseTimer > 0.35f) { s.Gear = -1; s.ReverseTimer = 0f; }
                }
                else if (s.Gear == -1 && vFwd > -0.6f && input.Throttle > 0.3f)
                {
                    s.Gear = 1;
                }
                else
                {
                    s.ReverseTimer = 0f;
                }

                if (s.Gear >= 1 && s.ShiftTimer <= 0f)
                {
                    if (s.EngineRpm > p.RedlineRpm * 0.95f && s.Gear < p.TopGear)
                        Shift(ref s, s.Gear + 1);
                    else if (s.Gear > 1 && s.EngineRpm < p.RedlineRpm * 0.42f)
                        Shift(ref s, s.Gear - 1);
                }
            }
            else
            {
                if (input.ShiftUp && s.Gear < p.TopGear) Shift(ref s, s.Gear + 1);
                if (input.ShiftDown && s.Gear > -1) Shift(ref s, s.Gear - 1);
            }

            // Forced induction: boost builds with throttle and rpm, with lag; naturally aspirated stays at 1.
            if (p.Turbocharged)
            {
                float rpmFactor = Mathf.Clamp01((s.EngineRpm - p.RedlineRpm * 0.28f) / (p.RedlineRpm * 0.25f));
                float targetBoost = DriveAxis(s, input) * rpmFactor;
                s.Boost = Mathf.MoveTowards(s.Boost, targetBoost, dt / Mathf.Max(0.05f, p.TurboLagSeconds));
            }
            else
            {
                s.Boost = 1f;
            }
        }

        /// <summary>Front wheel angle (radians) that full stick deflection maps to at this speed and body slip.</summary>
        public static float SteeringLimit(VehicleParams p, float speed, float absBodySlipRad)
        {
            float maxSteer = p.MaxSteerDeg * Mathf.Deg2Rad;
            float kinematic = Mathf.Atan(p.WheelbaseM * p.TyreGrip * Gravity * 1.35f / Mathf.Max(speed * speed, 1f))
                              + p.PeakSlipDeg * 1.2f * Mathf.Deg2Rad;
            return Mathf.Clamp(Mathf.Max(kinematic, absBodySlipRad + 8f * Mathf.Deg2Rad), maxSteer * p.HighSpeedSteerFraction, maxSteer);
        }

        void Shift(ref VehicleState s, int gear)
        {
            if (gear == 0 && !p.Assists.AutomaticGearbox) gear = s.Gear > 0 ? -1 : 1; // manual passes through neutral instantly
            s.Gear = (sbyte)gear;
            s.ShiftTimer = p.ShiftSeconds;
        }

        static float DriveAxis(VehicleState s, DriverInput input) => s.Gear == -1 ? input.Brake : input.Throttle;
        static float BrakeAxis(VehicleState s, DriverInput input) => s.Gear == -1 ? input.Throttle : input.Brake;

        void Integrate(ref VehicleState s, DriverInput input, float dt)
        {
            Quaternion rot = s.Rotation;
            Vector3 up = rot * Vector3.up;
            Vector3 fwd = rot * Vector3.forward;
            Vector3 pos = s.Position;
            float m = p.MassKg;
            float cornerMass = m * 0.25f;

            Vector3 force = new Vector3(0f, -Gravity * m, 0f);
            Vector3 torque = Vector3.zero;

            // Aerodynamics: drag at the CG, downforce split between the axles.
            float speed = s.Velocity.magnitude;
            force += -0.5f * AirDensity * p.DragAreaCdA * speed * s.Velocity;
            float vFwd = Vector3.Dot(s.Velocity, fwd);
            float downforce = 0.5f * AirDensity * p.LiftAreaClA * vFwd * vFwd;
            AddForceAt(ref force, ref torque, pos, pos + rot * new Vector3(0f, 0f, p.FrontAxleZ), -up * downforce * p.AeroFrontShare);
            AddForceAt(ref force, ref torque, pos, pos + rot * new Vector3(0f, 0f, p.RearAxleZ), -up * downforce * (1f - p.AeroFrontShare));

            // Suspension pass.
            int groundedCount = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector3 mount = pos + rot * p.WheelMount(i);
                float reach = p.RestLengthM + p.WheelRadiusM;
                grounded[i] = world.CastWheel(mount, -up, reach, out hits[i]);
                float prev = s.GetCompression(i);
                float comp = grounded[i] ? Mathf.Clamp(p.RestLengthM - (hits[i].Distance - p.WheelRadiusM), 0f, p.MaxCompressionM) : 0f;
                float compVel = (comp - prev) / dt;
                s.SetCompression(i, comp);
                bool front = i < 2;
                float k = front ? p.SpringFront : p.SpringRear;
                float c = front ? p.DamperFront : p.DamperRear;
                springForce[i] = grounded[i] ? Mathf.Max(0f, k * comp + c * compVel) : 0f;
                if (grounded[i]) groundedCount++;
            }
            ApplyAntiRoll(s, 0, 1, p.AntiRollFront);
            ApplyAntiRoll(s, 2, 3, p.AntiRollRear);

            // Engine.
            float drive = DriveAxis(s, input);
            float brakeIn = BrakeAxis(s, input);
            float gearRatio = s.Gear > 0 ? p.GearRatios[s.Gear - 1] : s.Gear < 0 ? p.ReverseRatio : 0f;
            float wheelRpm = Mathf.Abs(vFwd) / p.WheelRadiusM * (30f / Mathf.PI);
            float rpm;
            if (s.Gear == 0)
            {
                rpm = Mathf.MoveTowards(s.EngineRpm, p.IdleRpm + drive * (p.RedlineRpm - p.IdleRpm), 12000f * dt);
            }
            else
            {
                float clutchRpm = p.IdleRpm + drive * (p.LaunchRpm - p.IdleRpm);
                rpm = Mathf.Max(wheelRpm * gearRatio * p.FinalDrive, clutchRpm);
            }
            rpm = Mathf.Min(rpm, p.RedlineRpm + 150f);
            s.EngineRpm = rpm;

            float engineTorque = rpm >= p.RedlineRpm || s.ShiftTimer > 0f || s.Gear == 0 ? 0f : p.TorqueAt(rpm) * drive;
            if (p.Turbocharged) engineTorque *= Mathf.Lerp(0.68f, 1f, s.Boost);
            float driveForce = engineTorque * gearRatio * p.FinalDrive * p.DrivelineEfficiency / p.WheelRadiusM;
            if (s.Gear < 0) driveForce = -driveForce;
            if (s.Gear != 0 && drive < 0.05f && Mathf.Abs(vFwd) > 1f)
                driveForce -= Mathf.Sign(vFwd) * 0.1f * p.PeakTorqueNm * gearRatio * p.FinalDrive / p.WheelRadiusM * (rpm / p.RedlineRpm);

            float frontShare = p.Drive == DriveLayout.FWD ? 1f : p.Drive == DriveLayout.RWD ? 0f : p.AwdFrontShare;

            // Tyres.
            bool loose = false;
            bool spin = false;
            float steer = s.SteerAngle;
            for (int i = 0; i < 4; i++)
            {
                var wt = new WheelTelemetry { Compression = s.GetCompression(i) };
                bool front = i < 2;
                Vector3 mountLocal = p.WheelMount(i);
                Vector3 wheelFwdBody = front ? rot * (Quaternion.Euler(0f, steer * Mathf.Rad2Deg, 0f) * Vector3.forward) : fwd;
                if (!grounded[i])
                {
                    wt.AngularSpeed = Vector3.Dot(s.Velocity, wheelFwdBody) / p.WheelRadiusM;
                    Telemetry.SetWheel(i, wt);
                    continue;
                }

                GroundHit h = hits[i];
                Vector3 n = h.Normal;
                Vector3 wheelFwd = Vector3.ProjectOnPlane(wheelFwdBody, n).normalized;
                Vector3 wheelRight = Vector3.Cross(n, wheelFwd);
                Vector3 contact = h.Point;
                Vector3 r = contact - pos;
                Vector3 vContact = s.Velocity + Vector3.Cross(s.AngularVelocity, r);
                float vx = Vector3.Dot(vContact, wheelFwd);
                float vy = Vector3.Dot(vContact, wheelRight);
                float load = springForce[i];
                float mu = p.TyreGrip * SurfaceGrip.Grip(h.Surface) * SurfaceGripScale * (front ? 1f : p.RearGripBias);
                float maxF = mu * load;
                if (h.Surface == SurfaceKind.Shoulder || h.Surface == SurfaceKind.Grass) loose = true;

                // Longitudinal: drive, brakes, rolling drag; capped by the friction circle.
                float fx = (front ? frontShare : 1f - frontShare) * 0.5f * driveForce;
                // Share of this tyre's grip the engine is asking for (drives power-slide strength below).
                float driveUsage = maxF > 1f ? Mathf.Clamp01(Mathf.Abs(fx) / maxF) : 0f;
                if (p.Assists.TractionControl > 0)
                {
                    float tcCap = maxF * (p.Assists.TractionControl == 2 ? 0.7f : 0.85f);
                    fx = Mathf.Clamp(fx, -tcCap, tcCap);
                }
                float brake = brakeIn * p.BrakeForceN * 0.5f * (front ? p.BrakeFrontBias : 1f - p.BrakeFrontBias);
                if (p.Assists.AntiLockBrakes) brake = Mathf.Min(brake, maxF * 0.96f);
                brake += SurfaceGrip.Drag(h.Surface) * load;
                float stopCap = Mathf.Abs(vx) * cornerMass / dt;
                fx -= Mathf.Sign(vx) * Mathf.Min(brake, stopCap);

                float alpha = Mathf.Atan2(vy, Mathf.Max(Mathf.Abs(vx), 4f)) * Mathf.Rad2Deg;
                bool sliding = false;
                float fy;
                bool handbrake = !front && input.Handbrake;
                float patchSpeed = Mathf.Sqrt(vx * vx + vy * vy);
                if (handbrake && patchSpeed > 0.5f)
                {
                    // Locked wheel: sliding friction opposes the contact patch's velocity, so almost all of it is
                    // braking and very little is lateral — the rear steps out (brake-to-set / handbrake entry).
                    float slide = maxF * p.SlideGripFraction;
                    fx = -vx / patchSpeed * slide;
                    fy = -vy / patchSpeed * slide;
                    sliding = true;
                }
                else
                {
                    if (Mathf.Abs(fx) > maxF)
                    {
                        fx = Mathf.Sign(fx) * maxF * 0.92f;
                        sliding = true;
                        if (Mathf.Sign(fx) == Mathf.Sign(driveForce) && Mathf.Abs(driveForce) > 0f) spin = true;
                    }
                    // Lateral: saturating slip-angle curve inside the friction circle left by the longitudinal force.
                    float latMu = LateralCurve(Mathf.Abs(alpha));
                    // A sliding driven tyre loses lateral grip in proportion to how hard the engine can spin it:
                    // strong in low gears / high power, weak for a small engine at speed.
                    if (Mathf.Abs(alpha) > p.PeakSlipDeg && driveUsage > 0f && s.Gear > 0)
                        latMu *= 1f - p.PowerSlideGripLoss * Mathf.Clamp01(driveUsage * 1.6f);
                    float fyCap = Mathf.Sqrt(Mathf.Max(0f, maxF * maxF - fx * fx)) * (sliding ? 0.85f : 1f);
                    fy = -Mathf.Sign(vy) * Mathf.Min(latMu * maxF, fyCap);
                }
                float antiOvershoot = Mathf.Abs(vy) * cornerMass / dt;
                fy = Mathf.Clamp(fy, -antiOvershoot, antiOvershoot);

                // Suspension force along the body axis at the contact; tyre forces raised toward the roll centre.
                Vector3 tyrePoint = contact + up * (p.CgHeightM * p.RollCentreRaise);
                AddForceAt(ref force, ref torque, pos, contact, up * load);
                AddForceAt(ref force, ref torque, pos, tyrePoint, wheelFwd * fx + wheelRight * fy);

                wt.Grounded = true;
                wt.LoadN = load;
                wt.SlipAngleDeg = alpha;
                wt.Sliding = sliding || Mathf.Abs(alpha) > p.PeakSlipDeg * 1.6f;
                wt.Surface = h.Surface;
                wt.AngularSpeed = (sliding && Mathf.Abs(driveForce) > 0f && Mathf.Sign(fx) == Mathf.Sign(driveForce) ? vx * 1.4f + Mathf.Sign(fx) * 6f : vx) / p.WheelRadiusM;
                if (handbrake) wt.AngularSpeed = 0f;
                Telemetry.SetWheel(i, wt);
            }
            Telemetry.GroundedWheels = Mathf.Max(Telemetry.GroundedWheels, groundedCount);
            Telemetry.Wheelspin |= spin;
            Telemetry.OnLooseSurface |= loose;

            // Integrate linear motion.
            s.Velocity += force / m * dt;

            // Integrate angular motion in body space with the diagonal inertia tensor.
            Quaternion inv = Quaternion.Inverse(rot);
            Vector3 wLocal = inv * s.AngularVelocity;
            Vector3 tLocal = inv * torque;
            Vector3 inertia = p.Inertia;
            wLocal += new Vector3(tLocal.x / inertia.x, tLocal.y / inertia.y, tLocal.z / inertia.z) * dt;
            if (groundedCount >= 2)
            {
                Vector3 flatV = Vector3.ProjectOnPlane(s.Velocity, up);
                if (flatV.sqrMagnitude > 9f)
                {
                    float slipDeg = Vector3.Angle(flatV, fwd);
                    if (slipDeg > 90f) slipDeg = 180f - slipDeg;
                    float damping = p.HighSlipYawDamping * Mathf.Clamp01((slipDeg - 30f) / 40f);
                    wLocal.y *= Mathf.Max(0f, 1f - damping * dt);
                }
            }
            if (groundedCount == 0)
            {
                // Airborne: damp tumbling and gently self-right toward level so crests do not end in rolls.
                wLocal *= Mathf.Max(0f, 1f - 0.9f * dt);
                Vector3 levelAxis = inv * Vector3.Cross(up, Vector3.up);
                wLocal += levelAxis * (2.2f * dt);
            }
            s.AngularVelocity = rot * wLocal;

            Vector3 oldPos = s.Position;
            s.Position += s.Velocity * dt;
            float angle = s.AngularVelocity.magnitude * dt;
            if (angle > 1e-7f)
                s.Rotation = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, s.AngularVelocity.normalized) * s.Rotation;
            s.Rotation = Normalize(s.Rotation);

            ResolveBarriers(ref s, oldPos, dt);
        }

        void ApplyAntiRoll(VehicleState s, int left, int right, float stiffness)
        {
            if (!grounded[left] && !grounded[right]) return;
            float delta = (s.GetCompression(left) - s.GetCompression(right)) * stiffness;
            if (grounded[left]) springForce[left] = Mathf.Max(0f, springForce[left] + delta);
            if (grounded[right]) springForce[right] = Mathf.Max(0f, springForce[right] - delta);
        }

        float LateralCurve(float alphaDeg)
        {
            if (alphaDeg <= p.PeakSlipDeg)
                return Mathf.Sin(alphaDeg / p.PeakSlipDeg * Mathf.PI * 0.5f);
            float t = Mathf.Clamp01((alphaDeg - p.PeakSlipDeg) / p.SlideFalloffDeg);
            t = t * t * (3f - 2f * t);
            return Mathf.Lerp(1f, p.SlideGripFraction, t);
        }

        /// <summary>
        /// Re-applies barrier sweeps/depenetration after an external position change (car-to-car contact separation), so
        /// a nudge can never carry a car through a guardrail. Same code path as a physics substep's barrier pass.
        /// </summary>
        public void ConstrainToBarriers(ref VehicleState s, Vector3 fromPosition)
        {
            if ((s.Position - fromPosition).sqrMagnitude < 1e-8f) return;
            ResolveBarriers(ref s, fromPosition, TickDt / Substeps);
        }

        void ResolveBarriers(ref VehicleState s, Vector3 oldPos, float dt)
        {
            Vector3 half = p.BodyHalfExtents;
            Vector3 offset = s.Rotation * p.BodyCentreOffset;
            Vector3 fromC = oldPos + offset;
            Vector3 toC = s.Position + offset;

            // Anti-tunnelling: never pass through a barrier within one substep at any plausible speed.
            if (world.SweepBody(fromC, toC, s.Rotation, half, out float frac, out Vector3 sweepNormal))
            {
                s.Position = oldPos + (s.Position - oldPos) * Mathf.Max(0f, frac - 0.02f);
                ApplyWallResponse(ref s, WallNormal(sweepNormal, out _), 0, dt);
            }

            for (int iter = 0; iter < 3; iter++)
            {
                int count = world.ResolveBody(s.Position + offset, s.Rotation, half, contacts);
                if (count == 0) break;
                for (int i = 0; i < count; i++)
                {
                    // Barriers are vertical walls: separate and respond horizontally, so an edge/cap normal can never
                    // lift a car over a guardrail or convert its speed into a vertical launch.
                    Vector3 n = WallNormal(contacts[i].Normal, out float horizontal);
                    s.Position += horizontal > 0f ? n * (contacts[i].Depth / Mathf.Max(0.3f, horizontal)) : contacts[i].Normal * contacts[i].Depth;
                    ApplyWallResponse(ref s, n, contacts[i].SurfaceId, dt);
                }
            }
        }

        /// <summary>
        /// Horizontal part of a barrier contact normal. Near-vertical normals (resting on a cap) return the original normal,
        /// which <see cref="ApplyWallResponse"/> treats as ground-like (no bounce).
        /// </summary>
        static Vector3 WallNormal(Vector3 n, out float horizontalMagnitude)
        {
            var h = new Vector3(n.x, 0f, n.z);
            horizontalMagnitude = h.magnitude;
            if (horizontalMagnitude < 0.2f)
            {
                horizontalMagnitude = 0f;
                return n;
            }
            return h / horizontalMagnitude;
        }

        void ApplyWallResponse(ref VehicleState s, Vector3 n, int surfaceId, float dt)
        {
            float vn = Vector3.Dot(s.Velocity, n);
            bool isWall = Mathf.Abs(n.y) < 0.7f;
            if (vn < 0f)
            {
                s.Velocity -= n * vn * (1f + (isWall ? WallRestitution : 0f));
                if (isWall && -vn > Telemetry.WallImpactSpeed)
                {
                    Telemetry.WallImpactSpeed = -vn;
                    Telemetry.WallSurfaceId = surfaceId;
                    Telemetry.WallNormal = n;
                }
            }
            if (!isWall) return;
            // Scraping a wall bleeds speed so wall-riding is never the fast line.
            Telemetry.WallContact = true;
            Vector3 vt = s.Velocity - n * Vector3.Dot(s.Velocity, n);
            float loss = Mathf.Clamp(1.1f * dt + 0.03f * Mathf.Max(0f, -vn), 0f, 0.5f);
            s.Velocity -= vt * loss;
            s.AngularVelocity *= Mathf.Max(0f, 1f - 3f * dt);
        }

        static void AddForceAt(ref Vector3 force, ref Vector3 torque, Vector3 centre, Vector3 point, Vector3 f)
        {
            force += f;
            torque += Vector3.Cross(point - centre, f);
        }

        static Quaternion Normalize(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return mag > 1e-6f ? new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag) : Quaternion.identity;
        }
    }
}
