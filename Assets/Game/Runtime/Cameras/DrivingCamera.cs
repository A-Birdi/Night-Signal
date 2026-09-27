using NightSignal.Art;
using NightSignal.UI;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Cameras
{
    public enum DrivingView { ChaseClose = 0, ChaseFar = 1, Hood = 2, Bumper = 3, Cockpit = 4 }

    /// <summary>What the driven car is doing this frame, from its simulation telemetry (never from the display).</summary>
    public struct CameraMotion
    {
        /// <summary>Canonical road speed (m/s).</summary>
        public float Speed;
        /// <summary>Signed body slip (degrees; − = velocity left of heading).</summary>
        public float SlipDeg;
        public float LateralG, LongitudinalG;
        public bool Airborne;
        /// <summary>Onset of a wall impact this frame (m/s into the wall; 0 = none) and the wall's normal.</summary>
        public float ImpactSpeed;
        public Vector3 ImpactNormal;
    }

    /// <summary>
    /// The driving camera (Addendum 03 §2–4): five views — Chase Close, Chase Far, Hood, Bumper / Road and a genuine fitted
    /// Cockpit — anchored per car from its loft; a responsive arcade chase (critically damped follow of the car's travel,
    /// bounded look-ahead, drift framing that eases to the car's side with entry/exit hysteresis, small load-transfer and
    /// capped decaying impact responses) and restrained head motion for the body-mounted views. Every effect scales with the
    /// stored strengths (Arcade / Comfort / Custom; Reduced Motion turns all of them off) and none touches the simulation.
    /// No idle wobble: at rest or on a smooth straight nothing moves. Collision uses the near-plane volume and recovers
    /// slowly; a teleport resets all history. The view persists as a preference; look-back is a hold.
    /// </summary>
    public sealed class DrivingCamera : MonoBehaviour
    {
        public DrivingView View = DrivingView.ChaseClose;
        public bool LookBack;
        public LayerMask CollisionMask = (1 << GameLayers.Drivable) | (1 << GameLayers.Barrier) | (1 << GameLayers.Scenery);

        Camera cam;
        VehicleView target;
        CarBodyGenerator.CabinFrame frame;
        CameraMotion motion;
        Vector3 pos, posVel, travelDir = Vector3.forward;
        float driftSide, driftAmount, driftHold, holdSide;
        Vector3 impulse, impulseVel;
        float lastImpact = -10f, collisionDistance = -1f, fovNow, clock;
        Vector3 headOffset, headVel;
        bool hasHistory, lastExterior, lastLookBack;
        DrivingView applied = (DrivingView)(-1);

        public Camera Camera => cam;
        public VehicleView Target => target;
        /// <summary>Tests/evidence: the current drift framing (−1..1 side × amount) and the last collision distance.</summary>
        public float DriftFraming => driftSide * driftAmount;
        /// <summary>Tests/evidence: the current impact offset (metres, bounded) and the chase collision distance (−1 = none yet).</summary>
        public float ImpactOffset => impulse.magnitude;
        public float CollisionDistance => collisionDistance;
        /// <summary>The road speed last fed from the simulation (m/s).</summary>
        public float LastSpeedMps => motion.Speed;

        void Awake()
        {
            EnsureCamera();
            View = ParseView(DrivingPreferences.Current.View);
        }

        void EnsureCamera()
        {
            cam = GetComponent<Camera>();
            if (cam == null) cam = gameObject.AddComponent<Camera>();
            cam.farClipPlane = 4000f;
        }

        void OnEnable() => DrivingPreferences.Changed += OnPrefs;
        void OnDisable() => DrivingPreferences.Changed -= OnPrefs;

        void OnPrefs(DrivingPreferences p)
        {
            DrivingView v = ParseView(p.View);
            if (v != View) SetView(v, save: false);
        }

        public static DrivingView ParseView(string id)
        {
            int i = System.Array.IndexOf(DrivingPreferences.Views, id);
            return i < 0 ? DrivingView.ChaseClose : (DrivingView)i;
        }

        /// <summary>Follow this car (its anchors come from its own body). Resets all camera history.</summary>
        public void SetTarget(VehicleView view)
        {
            if (target != null && target != view) target.SetCockpitMode(false); // the previous car closes up again
            target = view;
            frame = view != null && view.Def != null ? CarBodyGenerator.Cabin(view.Def, view.Params) : null;
            applied = (DrivingView)(-1);
            NotifyTeleport();
        }

        /// <summary>A discontinuity (reset, respawn, car switch): cut to the new pose, forget velocities and effects.</summary>
        public void NotifyTeleport()
        {
            hasHistory = false;
            posVel = headVel = impulseVel = Vector3.zero;
            impulse = headOffset = Vector3.zero;
            driftAmount = 0f;
            driftHold = holdSide = 0f;
            collisionDistance = -1f;
            if (target != null) lastCarPos = target.transform.position;
        }

        public void SetMotion(CameraMotion m) => motion = m;

        /// <summary>Next view in order (Chase Close → Chase Far → Hood → Bumper → Cockpit), saved as the preference.</summary>
        public void Cycle() => SetView((DrivingView)(((int)View + 1) % 5), save: true);

        public void SetView(DrivingView v, bool save)
        {
            View = v;
            if (save)
            {
                DrivingPreferences p = DrivingPreferences.Current;
                p.View = DrivingPreferences.Views[(int)v];
                p.Save();
            }
        }

        void LateUpdate() => Step(Time.deltaTime);

        /// <summary>One presentation frame (LateUpdate; tests step it directly with a synthetic clock).</summary>
        public void Step(float frameDt)
        {
            if (target == null || frame == null) return;
            if (cam == null) EnsureCamera();
            DrivingPreferences prefs = DrivingPreferences.Current;
            MotionStrengths k = prefs.Effective;
            float dt = Mathf.Clamp(frameDt, 1e-4f, 0.1f);
            clock += dt;
            ApplyViewChange(prefs);

            Transform car = target.transform;
            // Filtered travel direction (not the raw heading): follows a drift's path, never snaps on a spin.
            Vector3 fwd = car.forward;
            Vector3 vel = motion.Speed > 3f ? Vector3.ProjectOnPlane(TravelEstimate(car), Vector3.up) : Vector3.ProjectOnPlane(fwd, Vector3.up);
            if (vel.sqrMagnitude < 1e-4f) vel = Vector3.ProjectOnPlane(fwd, Vector3.up);
            travelDir = hasHistory ? Vector3.Slerp(travelDir, vel.normalized, 1f - Mathf.Exp(-5f * dt)).normalized : vel.normalized;
            UpdateDrift(k, dt);
            UpdateImpulse(k, dt);

            bool exterior = View == DrivingView.ChaseClose || View == DrivingView.ChaseFar || LookBack;
            // Look-back and its release are cuts: never swing through the car or the roof.
            if (exterior != lastExterior || LookBack != lastLookBack) { hasHistory = false; lastExterior = exterior; lastLookBack = LookBack; }
            if (exterior) Chase(car, k, dt, prefs);
            else Mounted(car, k, dt);

            float speedFov = k.SpeedFov * 6f * Mathf.InverseLerp(20f, 60f, motion.Speed);
            float fov = prefs.VerticalFov + speedFov + (View == DrivingView.Cockpit && !LookBack ? 4f : 0f);
            fovNow = hasHistory ? Mathf.Lerp(fovNow, fov, 1f - Mathf.Exp(-3f * dt)) : fov;
            cam.fieldOfView = fovNow; // Unity's Camera.fieldOfView is vertical, as the setting says
            hasHistory = true;
        }

        void ApplyViewChange(DrivingPreferences prefs)
        {
            if (View == applied) return;
            applied = View;
            bool cockpit = View == DrivingView.Cockpit;
            target.SetCockpitMode(cockpit);
            cam.nearClipPlane = cockpit ? 0.03f : View == DrivingView.Hood || View == DrivingView.Bumper ? 0.05f : 0.08f;
            hasHistory = false; // a clean cut between viewpoints (never blend through the roof or the body)
        }

        Vector3 lastCarPos;
        Vector3 TravelEstimate(Transform car)
        {
            Vector3 d = car.position - lastCarPos;
            lastCarPos = car.position;
            return d.sqrMagnitude > 1e-6f ? d : car.forward;
        }

        /// <summary>Drift framing state with hysteresis: a sustained, meaningful slide at speed on the ground picks a side.</summary>
        void UpdateDrift(MotionStrengths k, float dt)
        {
            float slip = motion.SlipDeg;
            bool valid = !motion.Airborne && motion.Speed > 10f && Mathf.Abs(slip) >= 12f && Mathf.Abs(slip) < 75f;
            float side = Mathf.Sign(slip);
            // The slide must hold one side: slip noise flickering across the threshold restarts the clock (no side chatter).
            if (!valid) driftHold = 0f;
            else if (side != holdSide) { holdSide = side; driftHold = dt; }
            else driftHold += dt;
            float targetAmount = 0f;
            if (driftHold > 0.18f)
            {
                // Linked transitions pass through neutral: a new side only once the old framing has released.
                if (side != driftSide && driftAmount < 0.03f) driftSide = side;
                if (side == driftSide) targetAmount = Mathf.InverseLerp(12f, 35f, Mathf.Abs(slip)) * Mathf.InverseLerp(10f, 25f, motion.Speed);
            }
            bool reversing = valid && side != driftSide;
            // Entry ~0.12–0.22 s, release ~0.25–0.45 s; a linked reversal releases a little quicker.
            float tau = targetAmount > driftAmount ? 0.17f : reversing ? 0.2f : 0.35f;
            driftAmount = Mathf.Lerp(driftAmount, targetAmount * k.DriftFraming, 1f - Mathf.Exp(-dt / tau));
        }

        /// <summary>A capped, decaying nudge away from a wall on the onset of a real impact (not every tick of contact).</summary>
        void UpdateImpulse(MotionStrengths k, float dt)
        {
            if (motion.ImpactSpeed > 3f && clock - lastImpact > 0.4f && k.ImpactShake > 0f)
            {
                lastImpact = clock;
                float size = Mathf.Min(0.35f, motion.ImpactSpeed * 0.025f) * k.ImpactShake;
                impulseVel += motion.ImpactNormal.normalized * size * 12f;
            }
            // Critically damped return to zero; the total is bounded so contact against a wall cannot accumulate.
            impulse = Vector3.SmoothDamp(impulse, Vector3.zero, ref impulseVel, 0.12f, Mathf.Infinity, dt);
            if (impulse.magnitude > 0.4f) impulse = impulse.normalized * 0.4f;
        }

        void Chase(Transform car, MotionStrengths k, float dt, DrivingPreferences prefs)
        {
            bool far = View == DrivingView.ChaseFar;
            float length = target.Params.LengthM, height = target.Params.HeightM;
            float distance = far ? length * 2.1f + 1.8f : length * 1.2f + 0.9f;
            float up = far ? height + 1.7f : height + 0.45f;
            float scale = far ? 0.8f : 1f;
            Vector3 dir = LookBack ? -Vector3.ProjectOnPlane(car.forward, Vector3.up).normalized : travelDir;
            // Drift framing: swing a few degrees round and slide to the car's side to show its angle and the path.
            float yaw = LookBack ? 0f : -driftSide * driftAmount * 10f * scale;
            float lateral = LookBack ? 0f : -driftSide * driftAmount * 0.55f * scale;
            dir = Quaternion.AngleAxis(yaw, Vector3.up) * dir;
            Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
            Vector3 pivot = car.position + Vector3.up * (height * 0.55f);
            // Small load transfer: braking eases the camera in, acceleration back out; lateral g swings it a little.
            float body = k.BodyMotion;
            Vector3 load = -dir * Mathf.Clamp(motion.LongitudinalG, -1.2f, 1.2f) * 0.25f * body + right * Mathf.Clamp(motion.LateralG, -1.5f, 1.5f) * 0.12f * body;
            Vector3 desired = pivot - dir * distance + Vector3.up * (up - height * 0.55f) + right * lateral + load + impulse;
            // Near-plane-volume collision: compress toward the car, recover slowly (no pumping).
            Vector3 toCam = desired - pivot;
            float full = toCam.magnitude;
            float hitDist = full;
            if (Physics.SphereCast(pivot, 0.28f, toCam / Mathf.Max(1e-4f, full), out RaycastHit hit, full, CollisionMask, QueryTriggerInteraction.Ignore))
                hitDist = Mathf.Max(1.2f, hit.distance - 0.1f);
            collisionDistance = collisionDistance < 0f || hitDist < collisionDistance ? hitDist
                : Mathf.Lerp(collisionDistance, hitDist, 1f - Mathf.Exp(-1.5f * dt));
            desired = pivot + toCam / Mathf.Max(1e-4f, full) * Mathf.Min(full, collisionDistance);
            // Tight but responsive: a stiff critically damped follow (no overshoot, no idle motion).
            pos = hasHistory ? Vector3.SmoothDamp(transform.position, desired, ref posVel, far ? 0.09f : 0.06f, Mathf.Infinity, dt) : desired;
            // Bounded look-ahead along the travel direction.
            float ahead = LookBack ? 0f : Mathf.Clamp(motion.Speed * 0.3f, 0f, 12f);
            Vector3 aim = pivot + dir * (6f + ahead) + Vector3.up * (far ? -0.4f : 0f); // look-back: from ahead of the car, back along it
            float roll = Mathf.Clamp(motion.LateralG, -1.5f, 1.5f) * 2f * k.Roll;
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(aim - pos, Vector3.up) * Quaternion.Euler(0f, 0f, roll));
        }

        /// <summary>Hood, Bumper and Cockpit keep their physical viewpoint; only restrained head motion from real load transfer.</summary>
        void Mounted(Transform car, MotionStrengths k, float dt)
        {
            Transform mount = View == DrivingView.Bumper ? car : target.Body; // the bumper view stays level with the car, not the body lean
            Vector3 local = View == DrivingView.Cockpit ? frame.Eye : View == DrivingView.Hood ? frame.Hood : frame.Bumper;
            float body = View == DrivingView.Bumper ? k.BodyMotion * 0.3f : k.BodyMotion;
            Vector3 head = new Vector3(-Mathf.Clamp(motion.LateralG, -1.5f, 1.5f) * 0.025f, 0f, -Mathf.Clamp(motion.LongitudinalG, -1.2f, 1.2f) * 0.03f) * body;
            headOffset = hasHistory ? Vector3.SmoothDamp(headOffset, head, ref headVel, 0.12f, Mathf.Infinity, dt) : head;
            // The body transform sits at the body's ground origin; the anchors are in that model space.
            Vector3 world = mount.TransformPoint(local + headOffset + (View == DrivingView.Bumper ? Vector3.down * BodyGroundOffset() : Vector3.zero));
            if (View == DrivingView.Bumper)
            {
                // Low over the road: where the road rises under the nose (a dip's far side, a crest into a compression) keep
                // the near plane clear of it rather than clipping into the asphalt.
                Vector3 up = car.up;
                if (Physics.Raycast(world + up * 0.6f, -up, out RaycastHit road, 0.6f + BumperClearance, GameLayers.DrivableMask, QueryTriggerInteraction.Ignore))
                    world = road.point + up * BumperClearance;
            }
            Quaternion look = Quaternion.LookRotation(mount.forward, mount.up);
            transform.SetPositionAndRotation(world, look);
            if (View == DrivingView.Cockpit && target.Cockpit != null)
            {
                SpeedUnit unit = DrivingPreferences.Current.Unit;
                target.Cockpit.Update(target.SteerRad, motion.Speed, unit, LastRpm, LastRedline, LastGear);
            }
        }

        const float BumperClearance = 0.16f;

        /// <summary>The car root is the simulation origin (centre of gravity); anchors use the body's ground-level origin.</summary>
        float BodyGroundOffset() => target.Body != null ? -target.Body.localPosition.y : 0f;

        /// <summary>Engine state for the cockpit instruments (the same values the HUD shows).</summary>
        public float LastRpm, LastRedline;
        public int LastGear;
    }
}
