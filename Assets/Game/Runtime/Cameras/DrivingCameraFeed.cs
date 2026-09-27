using NightSignal.UI;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Cameras
{
    /// <summary>
    /// The one per-frame hand-off from a driving session to its camera, cockpit instruments and speed lines: the local car's
    /// simulation telemetry (physical values, never the displayed integer) and engine state. Presentation only.
    /// </summary>
    public static class DrivingCameraFeed
    {
        /// <summary>Makes the scene's main camera a driving camera (removing an older rig so two scripts never fight).</summary>
        public static DrivingCamera Attach(GameObject camGo)
        {
            foreach (MonoBehaviour legacy in camGo.GetComponents<ChaseCamera>()) Object.Destroy(legacy);
            return camGo.GetComponent<DrivingCamera>() ?? camGo.AddComponent<DrivingCamera>();
        }

        public static void Feed(DrivingCamera cam, SpeedLines lines, in StepTelemetry t, in VehicleState s, VehicleParams p, float dt)
        {
            if (cam == null) return;
            cam.SetMotion(new CameraMotion
            {
                Speed = t.RoadSpeedMps, SlipDeg = t.BodySlipDeg, LateralG = t.LateralG, LongitudinalG = t.LongitudinalG,
                Airborne = t.Airborne, ImpactSpeed = t.WallImpactSpeed, ImpactNormal = t.WallNormal,
            });
            cam.LastRpm = s.EngineRpm;
            cam.LastRedline = p != null ? p.RedlineRpm : 7000f;
            cam.LastGear = s.Gear;
            if (lines != null) lines.Render(t.Airborne ? 0f : t.RoadSpeedMps, cam.DriftFraming, DrivingPreferences.Current.Effective.SpeedLines, dt);
        }
    }
}
