using UnityEngine;

namespace NightSignal.Cameras
{
    public enum RaceCameraMode { Chase = 0, Hood = 1, Bumper = 2 }

    /// <summary>
    /// Race camera (spec §6): chase default with collision against scenery, stable horizon, look-back, adjustable
    /// FOV and shake; hood and bumper alternatives. Motion blur stays off (no post blur is used).
    /// </summary>
    public sealed class ChaseCamera : MonoBehaviour
    {
        public Transform Target;
        public RaceCameraMode Mode = RaceCameraMode.Chase;
        public float BaseFov = 62f;
        public float SpeedFovBoost = 8f;
        public float Distance = 5.6f;
        public float Height = 1.75f;
        public float ShakeAmount = 0.2f;
        public LayerMask CollisionMask = (1 << 8) | (1 << 9) | (1 << 11);
        public bool LookBack;

        Camera cam;
        Vector3 velocity;
        Vector3 smoothedForward = Vector3.forward;
        Vector3 lastTargetPos;

        void Awake()
        {
            cam = GetComponent<Camera>();
            if (cam == null) cam = gameObject.AddComponent<Camera>();
            cam.nearClipPlane = 0.08f;
            cam.farClipPlane = 4000f;
        }

        public void Cycle() => Mode = (RaceCameraMode)(((int)Mode + 1) % 3);

        void LateUpdate()
        {
            if (Target == null) return;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            Vector3 targetVel = (Target.position - lastTargetPos) / dt;
            lastTargetPos = Target.position;
            float speed = targetVel.magnitude;

            Vector3 fwd = Target.forward;
            Vector3 flatFwd = Vector3.ProjectOnPlane(fwd, Vector3.up).normalized;
            if (flatFwd.sqrMagnitude < 0.01f) flatFwd = smoothedForward;
            smoothedForward = Vector3.Slerp(smoothedForward, flatFwd, 1f - Mathf.Exp(-6f * dt)).normalized;
            Vector3 viewFwd = LookBack ? -smoothedForward : smoothedForward;

            if (Mode == RaceCameraMode.Chase)
            {
                Vector3 pivot = Target.position + Vector3.up * 1.1f;
                Vector3 desired = pivot - viewFwd * Distance + Vector3.up * (Height - 1.1f);
                // Keep the camera out of scenery and barriers.
                Vector3 dir = desired - pivot;
                if (Physics.SphereCast(pivot, 0.25f, dir.normalized, out RaycastHit hit, dir.magnitude, CollisionMask, QueryTriggerInteraction.Ignore))
                    desired = pivot + dir.normalized * Mathf.Max(0.8f, hit.distance - 0.1f);
                transform.position = Vector3.SmoothDamp(transform.position, desired, ref velocity, 0.06f, Mathf.Infinity, dt);
                // Stable horizon: look along the smoothed heading, never roll with the car.
                transform.rotation = Quaternion.LookRotation((pivot + viewFwd * 6f) - transform.position, Vector3.up);
            }
            else
            {
                Vector3 local = Mode == RaceCameraMode.Hood ? new Vector3(0f, 0.9f, 0.4f) : new Vector3(0f, 0.45f, 2.1f);
                transform.position = Target.TransformPoint(local);
                transform.rotation = Quaternion.LookRotation(LookBack ? -fwd : fwd, Vector3.up);
            }

            if (ShakeAmount > 0f && speed > 30f)
            {
                float s = ShakeAmount * 0.012f * Mathf.InverseLerp(30f, 70f, speed);
                transform.position += new Vector3(Mathf.PerlinNoise(Time.time * 17f, 0f) - 0.5f, Mathf.PerlinNoise(0f, Time.time * 19f) - 0.5f, 0f) * s;
            }
            cam.fieldOfView = BaseFov + SpeedFovBoost * Mathf.InverseLerp(20f, 70f, speed);
        }
    }
}
