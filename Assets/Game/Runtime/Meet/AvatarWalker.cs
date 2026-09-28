using NightSignal.Art;
using NightSignal.Characters;
using UnityEngine;

namespace NightSignal.Meet
{
    /// <summary>
    /// Walking for a meet avatar: a CharacterController (so scenery, parked cars and the perimeter ring stop it; no jump),
    /// walk and jog speeds with eased acceleration, turning toward the direction of travel, gravity onto the apron. Feeds
    /// its ground speed and turn rate to <see cref="CharacterMotion"/>. Avatars do not collide with each other
    /// (spec: nobody can imprison another player).
    /// </summary>
    [RequireComponent(typeof(CharacterRig))]
    public sealed class AvatarWalker : MonoBehaviour
    {
        public const float Radius = Core.Meet.MeetLayout.AvatarRadius;

        /// <summary>World-space desired direction (y ignored), magnitude 0–1; set each frame by the owner.</summary>
        public Vector3 Intent;
        public bool Jog;
        /// <summary>Stop all movement (menus, emotes that root the avatar, photo mode).</summary>
        public bool Frozen;

        public float Speed { get; private set; }
        public Vector3 Velocity => velocity;

        CharacterController cc;
        CharacterMotion motion;
        Vector3 velocity;
        float fall, lastYaw;

        void Awake() => Setup();

        void Setup()
        {
            if (cc != null) return;
            var rig = GetComponent<CharacterRig>();
            float h = rig.Skeleton != null ? rig.Skeleton.H : 1.72f;
            cc = gameObject.AddComponent<CharacterController>();
            cc.radius = Radius;
            cc.height = h * 0.96f;
            cc.center = new Vector3(0f, cc.height * 0.5f + 0.02f, 0f);
            cc.stepOffset = 0.3f;
            cc.slopeLimit = 42f;
            cc.skinWidth = 0.03f;
            cc.minMoveDistance = 0f;
            motion = GetComponent<CharacterMotion>();
            if (motion == null) motion = gameObject.AddComponent<CharacterMotion>();
            lastYaw = transform.eulerAngles.y;
        }

        /// <summary>Places the avatar (validated spawn, rescue, getting out of the car) without sweeping through anything.</summary>
        public void Teleport(Vector3 position, float yaw)
        {
            Setup();
            cc.enabled = false;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            cc.enabled = true;
            velocity = Vector3.zero;
            fall = 0f;
            lastYaw = yaw;
        }

        void Update()
        {
            Setup();
            float dt = Time.deltaTime;
            Vector3 want = Frozen ? Vector3.zero : new Vector3(Intent.x, 0f, Intent.z);
            if (want.sqrMagnitude > 1f) want.Normalize();
            float top = Jog ? CharacterMotion.JogSpeed : CharacterMotion.WalkSpeed;
            Vector3 target = want * top;
            float accel = target.sqrMagnitude > velocity.sqrMagnitude ? 7f : 10f;
            velocity = Vector3.MoveTowards(velocity, target, accel * dt);
            if (velocity.sqrMagnitude > 0.01f)
            {
                Quaternion face = Quaternion.LookRotation(new Vector3(velocity.x, 0f, velocity.z), Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, face, 560f * dt);
            }
            fall = cc.isGrounded ? -1f : fall - 9.81f * dt;
            CollisionFlags hit = cc.Move((velocity + Vector3.up * fall) * dt);
            // Walking into a wall: keep the speed the controller actually achieved, so the legs don't run on the spot.
            Vector3 flat = new Vector3(cc.velocity.x, 0f, cc.velocity.z);
            if ((hit & CollisionFlags.Sides) != 0) velocity = Vector3.ClampMagnitude(velocity, flat.magnitude + 0.2f);
            Speed = flat.magnitude;
            float yaw = transform.eulerAngles.y;
            float turn = dt > 0f ? Mathf.DeltaAngle(lastYaw, yaw) / dt : 0f;
            lastYaw = yaw;
            if (motion != null)
            {
                motion.Speed = Speed;
                motion.TurnRate = turn;
            }
        }
    }
}
