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
        }

        public float TargetSpeed { get; private set; }

        public DriverInput Drive(VehicleState s)
        {
            Vector3 fwd = s.Rotation * Vector3.forward;
            TrackLocation here = locator.Locate(s.Position, fwd);
            float speed = s.Velocity.magnitude;

            // Steering: pure pursuit on the racing line.
            float look = Mathf.Max(Profile.MinLookahead, speed * Profile.LookaheadSeconds);
            Vector3 aim = LinePoint(here.Distance + look);
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
            float target = 70f;
            float horizon = Mathf.Max(40f, speed * speed / (2f * Profile.BrakingDecel) + 30f);
            for (float d = 0f; d <= horizon; d += 4f)
            {
                float k = Mathf.Abs(track.SampleAt(here.Distance + d).Curvature);
                float vCorner = k > 1e-4f ? Mathf.Sqrt(p.TyreGrip * 9.81f / k) * Profile.CornerSpeedFactor : 70f;
                float allowed = Mathf.Sqrt(vCorner * vCorner + 2f * Profile.BrakingDecel * d);
                target = Mathf.Min(target, allowed);
            }
            TargetSpeed = target;
            float err = target - speed;
            float throttle = Mathf.Clamp01(err * 0.35f + 0.1f);
            // Lift progressively as the car slides (driver traction management).
            throttle *= Mathf.Clamp01(1f - (bodySlip * Mathf.Rad2Deg - 5f) / 12f);
            float brake = err < -1.2f ? Mathf.Clamp01(-err * 0.18f) : 0f;
            if (brake > 0f) throttle = 0f;
            return DriverInput.Quantize(steer, throttle, brake, InputButtons.None);
        }

        Vector3 LinePoint(float distance)
        {
            TrackSample s = track.SampleAt(distance);
            // Move toward the inside of upcoming curvature; stay a car-width from the edge.
            float k = track.SampleAt(distance + 10f).Curvature;
            float half = s.Width * 0.5f - 1.3f;
            float lateral = Mathf.Clamp(k * 900f * Profile.LineAggression, -1f, 1f) * half;
            return s.Position + s.Right * lateral;
        }
    }
}
