using System.Linq;
using System.Collections.Generic;
using NightSignal.Track;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.AI
{
    /// <summary>Per-driver racing parameters; rival tendencies bias these (spec §13).</summary>
    [System.Serializable]
    public struct DriverProfile
    {
        /// <summary>Fraction of the grip-limited corner speed the driver targets.</summary>
        public float CornerSpeedFactor;
        /// <summary>Braking deceleration assumed when planning (m/s²).</summary>
        public float BrakingDecel;
        /// <summary>How far toward the inside of a corner the line moves (0 = centreline, 1 = full width).</summary>
        public float LineAggression;
        public float LookaheadSeconds;
        public float MinLookahead;
        /// <summary>
        /// Scales the whole speed plan — corner speeds and the straight-line ceiling alike (0 or 1 = unchanged). The benchmark
        /// certification calibrates a featured rival with it so the encounter lands near the stage target.
        /// </summary>
        public float PaceScale;
        /// <summary>
        /// How well this driver drifts a judged zone (0–1; 0 = unset, which drives exactly as <see cref="BaselineDriftSkill"/>).
        /// Below it a driver commits to only some of the judged zones (a novice picks the ones they trust); above it a
        /// driver re-initiates the slide after running out of road if enough of the zone is left. The slide itself is the
        /// tuned controller for everyone: a sweep of its angle, entry speed, countersteer and edge margin (C01, C04, C08,
        /// C12) moved the banked score in different directions per course, so none of them is a skill.
        /// </summary>
        public float DriftSkill;
        /// <summary>
        /// Tendency behaviours (spec §13 "turn-in timing, corner-entry versus exit emphasis, release shape"; all 0 = the
        /// neutral line): <see cref="ApexShift"/> moves the apex along the road (m; + later, − earlier); <see cref="EntryWidth"/>
        /// (0–1) swings the car to the outside before turn-in; <see cref="ThrottleBias"/> (−0.3…0.3) gets on the power sooner
        /// or more gently when the plan allows speed; <see cref="BrakeGain"/> (0 = 1) is how sharply braking comes on.
        /// </summary>
        public float ApexShift, EntryWidth, ThrottleBias, BrakeGain;
        /// <summary>Starts slides with power instead of the handbrake (measuring trials that forbid it: CH25).</summary>
        public bool NoHandbrake;
        /// <summary>How far the line stays from the road's edge (m; 0 = 1.3, a car-width) — a trial that keeps every tyre paved.</summary>
        public float EdgeMargin;

        /// <summary>The skill the drift controller was tuned at (the handling harness' drifter).</summary>
        public const float BaselineDriftSkill = 0.65f;

        public static DriverProfile Validator => new DriverProfile
        {
            CornerSpeedFactor = 0.82f, BrakingDecel = 6.5f, LineAggression = 0.45f, LookaheadSeconds = 0.9f, MinLookahead = 10f,
        };
    }

    /// <summary>
    /// Route-following driver using the same inputs as a human: pure-pursuit steering toward a curvature-biased
    /// line, and throttle/brake from a braking-aware speed plan along the upcoming route. Never teleports and has
    /// no knowledge beyond the authored route geometry and its own car state.
    /// </summary>
    public sealed class RouteFollower
    {
        readonly TrackData track;
        readonly VehicleParams p;
        readonly TrackLocator locator;
        public DriverProfile Profile;

        public RouteFollower(TrackData track, VehicleParams p, DriverProfile profile)
        {
            this.track = track;
            this.p = p;
            Profile = profile;
            locator = new TrackLocator(track);
            ApplyDriftSkill(profile.DriftSkill);
        }

        /// <summary>
        /// Skill → behaviour around the tuned point (0.65: every zone attempted once per visit). Below: the share of zones a
        /// driver commits to falls to 40% at skill 0 (which ones is fixed per driver by <see cref="Seed"/>). Above: up to two
        /// re-initiated slides per zone visit at skill 1. Unset (0) keeps the tuned point exactly.
        /// </summary>
        public void ApplyDriftSkill(float skill)
        {
            float s = skill <= 0f ? DriverProfile.BaselineDriftSkill : Mathf.Clamp01(skill);
            float b = DriverProfile.BaselineDriftSkill;
            AttemptShare = s < b ? Mathf.Lerp(0.4f, 1f, s / b) : 1f;
            ReFlicks = s > b ? Mathf.RoundToInt((s - b) / (1f - b) * 2f) : 0;
        }

        /// <summary>Share of judged zones this driver commits to (1 = all).</summary>
        public float AttemptShare = 1f;
        /// <summary>Slides re-initiated per zone visit after running out of road (0 = one attempt per visit).</summary>
        public int ReFlicks;
        /// <summary>Which zones a less confident driver picks (per driver; the race sets it from the grid).</summary>
        public int Seed;
        int reflicksLeft;

        bool Commits(int zone)
        {
            if (AttemptShare >= 1f) return true;
            uint h = (uint)(zone * 73856093) ^ (uint)(Seed * 19349663) ^ 0x9E3779B9u;
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15;
            return (h % 1000u) / 1000f < AttemptShare;
        }

        /// <summary>Drift controller knobs (set from the skill; diagnostics may set them directly).</summary>
        public float CountersteerGain = 0.4f, EdgeMargin = 0.5f, FlickWindow = 0.5f, PathFollow = 0.6f;

        public float TargetSpeed { get; private set; }

        /// <summary>
        /// Drift events: the route's judged drift zones, where this driver drifts on purpose (null = race the line
        /// everywhere). The same inputs a human has: steering, throttle and a handbrake flick to start the slide.
        /// </summary>
        public IReadOnlyList<RouteGateDef> DriftZones;
        /// <summary>
        /// Marked apex gates to aim for (S29's Entry contract): approaching one, the line blends to the gate's lateral offset
        /// (full at the gate, fading over <see cref="ApexBlendMetres"/>) and no pass is started — a driver committing to the
        /// apex. Null: race the ordinary line.
        /// </summary>
        public IReadOnlyList<RouteGateDef> ApexGates;
        const float ApexBlendMetres = 40f;
        /// <summary>
        /// The event's weather grip (CourseRuntime.SurfaceGrip: dry 1, damp 0.88, wet 0.76). The speed plan and braking use it,
        /// as a driver reads the conditions — planning wet corners with dry grip put every car into the walls.
        /// </summary>
        public float SurfaceGrip = 1f;
        /// <summary>Speed (m/s, dry) the driver arrives at a drift zone with before the flick (~68 km/h, the harness drifter's regime).</summary>
        public float DriftEntrySpeed = 19f;
        /// <summary>Slip angle the driver holds in a drift (degrees; the scoring band peaks at 25–45°).</summary>
        public float DriftSlipDeg = 28f;
        /// <summary>True while the last input was a deliberate drift (diagnostics, tests).</summary>
        public bool Drifting { get; private set; }
        /// <summary>Drift attempt diagnostics: flicks started, holds reached, and why attempts ended (edge, spin, slow, wrong way).</summary>
        public int DriftFlicks, DriftHolds, DriftEndEdge, DriftEndSpin, DriftEndSlow, DriftEndWrongWay;
        int driftZone = -1, flickTicks, wantSign;
        DriftPhase phase;
        readonly HashSet<int> failedZones = new HashSet<int>(); // an attempt went wrong here: race the line on later visits

        /// <summary>
        /// Autopilot for a human car: when wedged (under 1.5 m/s for 3 s) hold reset like a player would. AI cars never
        /// need it — the race's marshal recovers them.
        /// </summary>
        public bool ResetWhenStuck;
        int stuckTicks;
        /// <summary>Lateral passing offset currently held (m, + = right); 0 when following the racing line.</summary>
        public float PassOffset { get; private set; }
        int passSide, passHoldTicks;

        public DriverInput Drive(VehicleState s) => Drive(s, null);

        /// <summary>
        /// Drives the racing line. With <paramref name="traffic"/> (other cars that can be touched) the driver matches a
        /// slower car ahead in its lane with a safe gap and moves to the side with more room to pass — racecraft, not
        /// contact: it never aims at another car (Addendum 01 §2.1, no pit manoeuvres).
        /// </summary>
        public DriverInput Drive(VehicleState s, IReadOnlyList<VehicleState> traffic)
        {
            Vector3 fwd = s.Rotation * Vector3.forward;
            TrackLocation here = locator.Locate(s.Position, fwd);
            float speed = s.Velocity.magnitude;
            if (ResetWhenStuck)
            {
                stuckTicks = speed < 1.5f ? stuckTicks + 1 : 0;
                if (stuckTicks > 3 * VehicleSimulation.TickRate)
                {
                    if (stuckTicks > 5 * VehicleSimulation.TickRate) stuckTicks = 0; // the race resets after 0.7 s held
                    return DriverInput.Quantize(0f, 0f, 0f, InputButtons.ResetHeld);
                }
            }

            float followLimit = float.MaxValue;
            int wantSide = 0;
            if (traffic != null)
            {
                TrackSample me = track.SampleAt(here.Distance);
                float myLateral = Vector3.Dot(s.Position - me.Position, me.Right);
                float lookAhead = p.LengthM + 6f + speed * 1.2f;
                for (int i = 0; i < traffic.Count; i++)
                {
                    Vector3 delta = traffic[i].Position - s.Position;
                    float ahead = Vector3.Dot(delta, me.Tangent);
                    if (ahead <= 0.5f || ahead > lookAhead) continue;
                    TrackSample at = track.SampleAt(here.Distance + ahead);
                    float otherLateral = Vector3.Dot(traffic[i].Position - at.Position, at.Right);
                    if (Mathf.Abs(otherLateral - myLateral) > p.WidthM + 0.7f) continue; // not in our lane
                    float otherSpeed = Vector3.Dot(traffic[i].Velocity, at.Tangent);
                    float safeGap = p.LengthM + 2.5f;
                    followLimit = Mathf.Min(followLimit, Mathf.Max(0f, otherSpeed + (ahead - safeGap) * 0.8f));
                    if (otherSpeed < speed - 0.5f) wantSide = otherLateral <= 0f ? 1 : -1; // pass on the side with more room
                }
            }
            if (ApexGates != null && ApexGates.Any(g => g.StartMetres - here.Distance > -5f && g.StartMetres - here.Distance < ApexBlendMetres + 20f))
                wantSide = 0; // committing to a marked apex: no passing move now
            if (wantSide != 0 && passHoldTicks <= 0) { passSide = wantSide; passHoldTicks = 150; }
            else if (passHoldTicks > 0) passHoldTicks--;
            else passSide = 0;

            // Steering: pure pursuit on the racing line (shifted while passing).
            float look = Mathf.Max(Profile.MinLookahead, speed * Profile.LookaheadSeconds);
            TrackSample aimSample = track.SampleAt(here.Distance + look);
            float maxOffset = Mathf.Max(0f, aimSample.Width * 0.5f - p.WidthM * 0.5f - 0.6f);
            PassOffset = Mathf.MoveTowards(PassOffset, passSide * maxOffset, 0.05f);
            Vector3 aim = LinePoint(here.Distance + look) + aimSample.Right * PassOffset;
            // Stay on the road when the line and the pass offset combine.
            float aimLateral = Vector3.Dot(aim - aimSample.Position, aimSample.Right);
            if (Mathf.Abs(aimLateral) > aimSample.Width * 0.5f - 1.2f)
                aim -= aimSample.Right * (aimLateral - Mathf.Sign(aimLateral) * (aimSample.Width * 0.5f - 1.2f));
            Vector3 toAim = aim - s.Position;
            float alpha = Vector3.SignedAngle(Vector3.ProjectOnPlane(fwd, Vector3.up), Vector3.ProjectOnPlane(toAim, Vector3.up), Vector3.up) * Mathf.Deg2Rad;
            float wheel = Mathf.Atan(2f * p.WheelbaseM * Mathf.Sin(alpha) / Mathf.Max(1f, toAim.magnitude));
            float signedSlip = speed > 3f ? Mathf.Atan2(Vector3.Dot(s.Velocity, s.Rotation * Vector3.right), Mathf.Abs(Vector3.Dot(s.Velocity, fwd))) : 0f;
            float bodySlip = Mathf.Abs(signedSlip);
            // Catch slides like a driver: add countersteer toward the velocity beyond a small slip angle.
            if (bodySlip > 6f * Mathf.Deg2Rad)
                wheel += signedSlip * 0.9f;
            float steer = Mathf.Clamp(wheel / VehicleSimulation.SteeringLimit(p, speed, bodySlip), -1f, 1f);

            // Speed plan: the lowest speed any point ahead allows, given braking distance to reach it.
            float pace = Profile.PaceScale > 0f ? Profile.PaceScale : 1f;
            float target = 70f * pace;
            float decel = Profile.BrakingDecel * SurfaceGrip;
            float horizon = Mathf.Max(40f, speed * speed / (2f * decel) + 30f);
            // A late apex opens the exit: while the corner unwinds, such a driver carries a little more speed (a straighter exit
            // line than the centreline the plan measures); an early apex the opposite.
            float kHere = Mathf.Abs(track.SampleAt(here.Distance).Curvature);
            for (float d = 0f; d <= horizon; d += 4f)
            {
                float k = Mathf.Abs(track.SampleAt(here.Distance + d).Curvature);
                float exitFactor = Profile.ApexShift != 0f && d <= 40f && k < kHere ? 1f + 0.004f * Profile.ApexShift : 1f;
                float vCorner = (k > 1e-4f ? Mathf.Sqrt(p.TyreGrip * SurfaceGrip * 9.81f / k) * Profile.CornerSpeedFactor * exitFactor : 70f) * pace;
                float allowed = Mathf.Sqrt(vCorner * vCorner + 2f * decel * d);
                target = Mathf.Min(target, allowed);
            }
            target = Mathf.Min(target, followLimit);
            if (DriftZones != null)
            {
                // A driver who means to drift brakes to a controllable entry speed first (a handbrake flick at race pace on a
                // wet road is a spin): plan to arrive at each upcoming zone — and stay while attempting it — at that speed.
                float entry = DriftEntrySpeed * Mathf.Sqrt(SurfaceGrip);
                for (int i = 0; i < DriftZones.Count; i++)
                {
                    RouteGateDef z = DriftZones[i];
                    if (failedZones.Contains(i) || here.Distance > z.EndMetres) continue;
                    if (i == driftZone && phase == DriftPhase.Done) continue;
                    float ahead = z.StartMetres - here.Distance;
                    if (ahead > horizon) continue;
                    target = Mathf.Min(target, Mathf.Sqrt(entry * entry + 2f * Profile.BrakingDecel * SurfaceGrip * Mathf.Max(0f, ahead)));
                }
            }
            TargetSpeed = target;
            float err = target - speed;
            float throttle = Mathf.Clamp01(err * 0.35f * (1f + 2f * Profile.ThrottleBias) + 0.1f + 0.5f * Profile.ThrottleBias);
            // Lift progressively as the car slides (driver traction management).
            throttle *= Mathf.Clamp01(1f - (bodySlip * Mathf.Rad2Deg - 5f) / 12f);
            float brake = err < -1.2f ? Mathf.Clamp01(-err * 0.18f * (Profile.BrakeGain > 0f ? Profile.BrakeGain : 1f)) : 0f;
            if (brake > 0f) throttle = 0f;
            Drifting = false;
            if (DriftZones != null && DriftInput(s, here, fwd, speed, signedSlip, aim, target, out DriverInput drift)) return drift;
            return DriverInput.Quantize(steer, throttle, brake, InputButtons.None);
        }

        enum DriftPhase { Idle, Flick, Hold, Done }

        /// <summary>
        /// Deliberate drift inside a judged zone, one attempt per zone visit: when the car is up to speed, straight and in the
        /// first half of the zone, a short handbrake flick toward the corner starts the slide; then the front wheels follow
        /// the front axle's travel with countersteer holding the target slip (the handling harness' drifter), the target
        /// leaning in or out with where the road goes and throttle trimming slip and speed. The attempt ends — and the
        /// racing line (which catches slides) drives the rest of the zone — before the predicted path reaches the road edge,
        /// on a spin, below drifting speed or when the slide goes the wrong way. Returns false to let the line drive.
        /// </summary>
        bool DriftInput(VehicleState s, TrackLocation here, Vector3 fwd, float speed, float signedSlip, Vector3 aim, float plannedSpeed, out DriverInput input)
        {
            input = default;
            int zone = -1;
            for (int i = 0; i < DriftZones.Count; i++)
                if (here.Distance >= DriftZones[i].StartMetres && here.Distance <= DriftZones[i].EndMetres) { zone = i; break; }
            if (zone < 0)
            {
                // Between zones (or sent back by a reset): the next visit starts fresh.
                if (driftZone >= 0 && here.Distance < DriftZones[driftZone].StartMetres) phase = DriftPhase.Idle;
                if (driftZone >= 0 && here.Distance > DriftZones[driftZone].EndMetres) { driftZone = -1; phase = DriftPhase.Idle; }
                return false;
            }
            if (zone != driftZone) { driftZone = zone; phase = DriftPhase.Idle; reflicksLeft = ReFlicks; }
            RouteGateDef z = DriftZones[zone];

            // Which way the road bends just ahead decides the slide's direction (S-bends flip it).
            float k = track.SampleAt(here.Distance + 8f).Curvature + track.SampleAt(here.Distance + 18f).Curvature;
            int turn = k > 2e-4f ? 1 : k < -2e-4f ? -1 : 0;
            float slipDeg = signedSlip * Mathf.Rad2Deg; // − = velocity left of heading (the car rotated right)
            TrackSample at = track.SampleAt(here.Distance);
            float half = at.Width * 0.5f;
            float lateralSpeed = Vector3.Dot(s.Velocity, at.Right);
            float predicted = here.Lateral + lateralSpeed * 0.4f;
            bool edgeAhead = Mathf.Abs(predicted) > half - EdgeMargin && Mathf.Sign(predicted) == Mathf.Sign(lateralSpeed);

            switch (phase)
            {
                case DriftPhase.Idle:
                {
                    float fraction = (here.Distance - z.StartMetres) / Mathf.Max(1f, z.EndMetres - z.StartMetres);
                    if (turn == 0 || speed < 10.5f || Mathf.Abs(slipDeg) > 8f || here.HeadingDot < 0.9f || fraction > FlickWindow || edgeAhead || failedZones.Contains(zone)
                        || !Commits(zone))
                        return false;
                    phase = DriftPhase.Flick;
                    wantSign = turn;
                    flickTicks = 0;
                    DriftFlicks++;
                    goto case DriftPhase.Flick;
                }
                case DriftPhase.Flick:
                {
                    bool caught = Mathf.Abs(slipDeg) >= 12f && Mathf.Sign(slipDeg) == -wantSign;
                    if (caught || flickTicks >= 45)
                    {
                        phase = caught || Mathf.Abs(slipDeg) >= 6f ? DriftPhase.Hold : DriftPhase.Done;
                        if (phase == DriftPhase.Done) return false;
                        DriftHolds++;
                        break;
                    }
                    flickTicks++;
                    Drifting = true;
                    input = Profile.NoHandbrake ? DriverInput.Quantize(wantSign, 1f, 0f, InputButtons.None)
                        : DriverInput.Quantize(wantSign, 0.5f, 0f, InputButtons.Handbrake);
                    return true;
                }
                case DriftPhase.Done:
                {
                    float fraction = (here.Distance - z.StartMetres) / Mathf.Max(1f, z.EndMetres - z.StartMetres);
                    if (reflicksLeft <= 0 || turn == 0 || speed < 10.5f || Mathf.Abs(slipDeg) > 8f || here.HeadingDot < 0.9f || fraction > 0.7f || edgeAhead)
                        return false;
                    reflicksLeft--;
                    phase = DriftPhase.Flick;
                    wantSign = turn;
                    flickTicks = 0;
                    DriftFlicks++;
                    goto case DriftPhase.Flick;
                }
            }

            // Hold.
            if (turn != 0 && turn != wantSign)
            {
                // S-bend: swing the slide the other way through the transition (the countersteer does most of it).
                wantSign = turn;
            }
            bool wrongWay = Mathf.Abs(slipDeg) > 12f && Mathf.Sign(slipDeg) != -wantSign;
            if (edgeAhead || Mathf.Abs(slipDeg) > 70f || speed < 9f || wrongWay)
            {
                phase = DriftPhase.Done;
                if (edgeAhead) DriftEndEdge++;
                else if (Mathf.Abs(slipDeg) > 70f) DriftEndSpin++;
                else if (speed < 9f) DriftEndSlow++;
                else DriftEndWrongWay++;
                if (edgeAhead || Mathf.Abs(slipDeg) > 70f) failedZones.Add(zone);
                return false;
            }
            Vector3 up = Vector3.up;
            Vector3 flatFwd = Vector3.ProjectOnPlane(fwd, up);
            Vector3 frontVel = s.Velocity + Vector3.Cross(s.AngularVelocity, s.Rotation * new Vector3(0f, 0f, p.FrontAxleZ));
            float frontAngle = Vector3.SignedAngle(flatFwd, Vector3.ProjectOnPlane(frontVel, up), up);
            // Where the road goes relative to where the car is going: aim further into the corner → more slip, less → less.
            float pathErr = Vector3.SignedAngle(Vector3.ProjectOnPlane(s.Velocity, up), Vector3.ProjectOnPlane(aim - s.Position, up), up);
            float target = Mathf.Clamp(DriftSlipDeg + pathErr * wantSign * PathFollow, 14f, 38f);
            float excess = Mathf.Abs(slipDeg) - target;
            float wheelDeg = frontAngle + Mathf.Sign(frontAngle) * excess * CountersteerGain;
            float limitDeg = VehicleSimulation.SteeringLimit(p, speed, Mathf.Abs(signedSlip)) * Mathf.Rad2Deg;
            float steerCmd = Mathf.Clamp(wheelDeg / Mathf.Max(1f, limitDeg), -1f, 1f);
            float keepSpeed = Mathf.Clamp(plannedSpeed, 12.5f, 22f);
            float throttleCmd = Mathf.Clamp01(0.45f - excess * 0.04f + (keepSpeed - speed) * 0.05f);
            if (Mathf.Abs(slipDeg) > 55f) throttleCmd = 0f; // near a spin: lift and let the countersteer bring it back
            Drifting = true;
            input = DriverInput.Quantize(steerCmd, throttleCmd, 0f, InputButtons.None);
            return true;
        }

        Vector3 LinePoint(float distance)
        {
            TrackSample s = track.SampleAt(distance);
            // Move toward the inside of upcoming curvature; stay a car-width from the edge.
            float k = track.SampleAt(distance + 10f - Profile.ApexShift).Curvature;
            float half = s.Width * 0.5f - (Profile.EdgeMargin > 0f ? Profile.EdgeMargin : 1.3f);
            float lateral = Mathf.Clamp(k * 900f * Profile.LineAggression, -1f, 1f) * half;
            if (Profile.EntryWidth != 0f)
            {
                // Outside-in: with a corner coming (much more curvature ahead than here), hold the outside before turning in.
                float kAhead = track.SampleAt(distance + 45f).Curvature;
                if (Mathf.Abs(kAhead) > Mathf.Abs(k) * 1.5f + 1e-4f)
                    lateral -= Mathf.Sign(kAhead) * Profile.EntryWidth * Mathf.Clamp01(Mathf.Abs(kAhead) * 900f) * half;
                lateral = Mathf.Clamp(lateral, -half, half);
            }
            if (ApexGates != null)
                foreach (RouteGateDef g in ApexGates)
                {
                    float from = Mathf.Abs(distance - g.StartMetres);
                    if (from < ApexBlendMetres) lateral = Mathf.Lerp(lateral, Mathf.Clamp(g.LineOffset, -half, half), 1f - from / ApexBlendMetres);
                }
            return s.Position + s.Right * lateral;
        }
    }
}
