using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Meet
{
    /// <summary>
    /// Third-person meet camera: orbits the avatar over the shoulder, look with mouse or right stick, zoom, recenter
    /// behind the avatar on demand and drift back behind it gently while walking with no look input; pulls in instead of
    /// passing through scenery or parked cars. Photo mode frees it to orbit and rise within a short radius of the avatar
    /// (it never pauses anything). A presentation mode follows the arrival car.
    /// </summary>
    public sealed class MeetCamera : MonoBehaviour
    {
        public Transform Target;
        public float Yaw, Pitch = 12f, Distance = 3.8f;
        public float PivotHeight = 1.5f;
        public bool Photo;
        /// <summary>Arrival flourish: frame this transform from behind and above instead of the avatar.</summary>
        public Transform Presenting;

        public const float MinDistance = 1.6f, MaxDistance = 7f;
        Camera cam;
        float idleLook;
        Vector3 photoOffset;
        float currentDistance;

        public Camera Cam => cam != null ? cam : cam = GetComponent<Camera>();

        public static MeetCamera Create()
        {
            var go = new GameObject("MeetCamera", typeof(Camera), typeof(AudioListener));
            go.tag = "MainCamera";
            var mc = go.AddComponent<MeetCamera>();
            Camera c = go.GetComponent<Camera>();
            c.nearClipPlane = 0.08f;
            c.farClipPlane = 9000f;
            c.fieldOfView = 56f;
            var data = go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            return mc;
        }

        public void Recenter()
        {
            if (Target == null) return;
            Yaw = Target.eulerAngles.y;
            Pitch = 12f;
            photoOffset = Vector3.zero;
        }

        /// <summary>Applies look input: <paramref name="look"/> in degrees this frame.</summary>
        public void Look(Vector2 look, float zoom, bool moving, float dt)
        {
            Yaw += look.x;
            Pitch = Mathf.Clamp(Pitch - look.y, Photo ? -35f : -20f, 70f);
            Distance = Mathf.Clamp(Distance - zoom * 6f, MinDistance, MaxDistance);
            idleLook = look.sqrMagnitude > 1e-4f ? 0f : idleLook + dt;
            if (!Photo && moving && idleLook > 1.4f && Target != null)
                Yaw = Mathf.MoveTowardsAngle(Yaw, Target.eulerAngles.y, 55f * dt);
        }

        /// <summary>Photo mode: move the orbit pivot within 6 m of the avatar.</summary>
        public void PhotoMove(Vector2 move, float dt)
        {
            Vector3 fwd = Quaternion.Euler(0f, Yaw, 0f) * Vector3.forward, right = Quaternion.Euler(0f, Yaw, 0f) * Vector3.right;
            photoOffset += (fwd * move.y + right * move.x) * 2.5f * dt;
            photoOffset = Vector3.ClampMagnitude(new Vector3(photoOffset.x, 0f, photoOffset.z), 6f);
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (Presenting != null)
            {
                Vector3 back = Presenting.position - Presenting.forward * 7.5f + Vector3.up * 2.6f + Presenting.right * 2.2f;
                transform.position = Vector3.Lerp(transform.position, back, 1f - Mathf.Exp(-4f * dt));
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(Presenting.position + Vector3.up * 0.8f - transform.position), 1f - Mathf.Exp(-6f * dt));
                return;
            }
            if (Target == null) return;
            Vector3 pivot = Target.position + Vector3.up * PivotHeight + (Photo ? photoOffset : Vector3.zero);
            Quaternion rot = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 shoulder = rot * new Vector3(Photo ? 0f : 0.35f, 0f, 0f);
            float want = Distance;
            // Pull in rather than clip through scenery or a parked car.
            if (Physics.SphereCast(pivot + shoulder * 0.2f, 0.22f, rot * Vector3.back, out RaycastHit hit, want, (1 << GameLayers.Scenery) | (1 << GameLayers.Vehicle), QueryTriggerInteraction.Ignore))
                want = Mathf.Max(0.5f, hit.distance - 0.05f);
            currentDistance = want < currentDistance ? want : Mathf.Lerp(currentDistance, want, 1f - Mathf.Exp(-5f * dt));
            Vector3 pos = pivot + shoulder + rot * Vector3.back * currentDistance;
            if (pos.y < 0.25f) pos.y = 0.25f;
            transform.SetPositionAndRotation(pos, rot);
        }
    }
}
