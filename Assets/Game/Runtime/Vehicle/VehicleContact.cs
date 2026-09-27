using UnityEngine;

namespace NightSignal.Vehicle
{
    public struct ContactResult
    {
        public bool Touching;
        /// <summary>Normal impulse applied (N·s); zero when the cars were separating.</summary>
        public float Impulse;
        /// <summary>Largest velocity change either car received this tick (m/s), after caps.</summary>
        public float DeltaV;
        /// <summary>Horizontal unit normal pointing from car A towards car B.</summary>
        public Vector3 Normal;
        public float Depth;
    }

    /// <summary>
    /// Bounded "light contact" between two cars (Addendum 01 §2.1): oriented body boxes on the ground plane, one
    /// restitution/friction impulse with a yaw-only angular response, and a small positional separation — all capped so
    /// contact is predictable side-to-side nudging, never a pit manoeuvre, launch or wall pin. Pure and deterministic:
    /// the server resolves every pair in entrant order after stepping; clients use the same function to predict their
    /// own car against extrapolated remote cars, and reconciliation corrects the rest. No damage state exists (D10).
    /// </summary>
    public static class VehicleContact
    {
        public const float Restitution = 0.15f;
        public const float Friction = 0.25f;
        /// <summary>Largest velocity change one contact may give a car in a tick (m/s).</summary>
        public const float MaxDeltaV = 3.5f;
        /// <summary>Largest yaw-rate change one contact may give a car in a tick (rad/s).</summary>
        public const float MaxYawDelta = 1.2f;
        /// <summary>Largest separation applied per pair per tick (m); deeper overlaps resolve over several ticks.</summary>
        public const float MaxSeparationPerTick = 0.2f;
        public const float Slop = 0.01f;
        /// <summary>Velocity change that counts as a vehicle-contact incident (debounced by the caller).</summary>
        public const float IncidentDeltaV = 1.0f;

        struct Box
        {
            public Vector3 Centre, Forward, Right;
            public float HalfLength, HalfWidth, HalfHeight;
        }

        static Box BoxOf(in VehicleState s, VehicleParams p)
        {
            Vector3 f = s.Rotation * Vector3.forward;
            f.y = 0f;
            f = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            Vector3 half = p.BodyHalfExtents;
            return new Box
            {
                Centre = s.Position + s.Rotation * p.BodyCentreOffset,
                Forward = f,
                Right = new Vector3(f.z, 0f, -f.x),
                HalfLength = half.z,
                HalfWidth = half.x,
                HalfHeight = Mathf.Max(half.y, p.HeightM * 0.5f),
            };
        }

        static float Radius(in Box b, Vector3 axis) =>
            Mathf.Abs(Vector3.Dot(b.Forward, axis)) * b.HalfLength + Mathf.Abs(Vector3.Dot(b.Right, axis)) * b.HalfWidth;

        /// <summary>Furthest point of the box along a horizontal direction; near-parallel faces use the face centre.</summary>
        static Vector3 Support(in Box b, Vector3 dir)
        {
            float df = Vector3.Dot(b.Forward, dir), dr = Vector3.Dot(b.Right, dir);
            float sf = Mathf.Abs(df) < 0.08f ? 0f : Mathf.Sign(df);
            float sr = Mathf.Abs(dr) < 0.08f ? 0f : Mathf.Sign(dr);
            return b.Centre + b.Forward * (sf * b.HalfLength) + b.Right * (sr * b.HalfWidth);
        }

        /// <summary>True when the two body boxes overlap (used for safety-ghost release and spawn checks).</summary>
        public static bool Overlapping(in VehicleState a, VehicleParams pa, in VehicleState b, VehicleParams pb) =>
            Separation(BoxOf(a, pa), BoxOf(b, pb), out _, out _);

        static bool Separation(in Box A, in Box B, out Vector3 normal, out float depth)
        {
            normal = Vector3.zero;
            depth = float.MaxValue;
            if (Mathf.Abs(A.Centre.y - B.Centre.y) > A.HalfHeight + B.HalfHeight) return false;
            Vector3 d = B.Centre - A.Centre;
            d.y = 0f;
            Vector3 a0 = A.Forward, a1 = A.Right, a2 = B.Forward, a3 = B.Right;
            for (int i = 0; i < 4; i++)
            {
                Vector3 axis = i == 0 ? a0 : i == 1 ? a1 : i == 2 ? a2 : a3;
                float dist = Vector3.Dot(d, axis);
                float overlap = Radius(A, axis) + Radius(B, axis) - Mathf.Abs(dist);
                if (overlap <= 0f) return false;
                if (overlap < depth)
                {
                    depth = overlap;
                    normal = dist >= 0f ? axis : -axis;
                }
            }
            return true;
        }

        /// <summary>
        /// Resolves contact between A and B. <paramref name="applyToA"/>/<paramref name="applyToB"/> let a client apply the
        /// same physical response to its own car only (the remote car's state is a prediction it does not own).
        /// </summary>
        public static ContactResult Resolve(ref VehicleState a, VehicleParams pa, ref VehicleState b, VehicleParams pb, bool applyToA = true, bool applyToB = true)
        {
            var result = new ContactResult();
            Box A = BoxOf(a, pa), B = BoxOf(b, pb);
            if (!Separation(A, B, out Vector3 n, out float depth)) return result;
            result.Touching = true;
            result.Normal = n;
            result.Depth = depth;

            float ma = Mathf.Max(1f, pa.MassKg), mb = Mathf.Max(1f, pb.MassKg);
            float ia = Mathf.Max(1f, pa.Inertia.y), ib = Mathf.Max(1f, pb.Inertia.y);
            Vector3 p = (Support(A, n) + Support(B, -n)) * 0.5f;
            Vector3 ra = p - a.Position, rb = p - b.Position;
            ra.y = 0f;
            rb.y = 0f;
            Vector3 va = a.Velocity + Vector3.Cross(a.AngularVelocity, ra);
            Vector3 vb = b.Velocity + Vector3.Cross(b.AngularVelocity, rb);
            Vector3 vRel = vb - va;
            vRel.y = 0f;
            float vn = Vector3.Dot(vRel, n);

            if (vn < 0f)
            {
                float ka = ra.z * n.x - ra.x * n.z, kb = rb.z * n.x - rb.x * n.z;
                float invMass = 1f / ma + 1f / mb + ka * ka / ia + kb * kb / ib;
                float jn = -(1f + Restitution) * vn / invMass;
                Vector3 tangent = vRel - vn * n;
                float vt = tangent.magnitude;
                Vector3 impulse = jn * n;
                if (vt > 1e-3f)
                {
                    tangent /= vt;
                    float jt = Mathf.Min(vt / invMass, Friction * jn);
                    impulse -= jt * tangent;
                }
                result.Impulse = jn;
                if (applyToA) result.DeltaV = Mathf.Max(result.DeltaV, ApplyImpulse(ref a, -impulse, ra, ma, ia));
                if (applyToB) result.DeltaV = Mathf.Max(result.DeltaV, ApplyImpulse(ref b, impulse, rb, mb, ib));
            }

            float push = Mathf.Min(depth - Slop, MaxSeparationPerTick);
            if (push > 0f)
            {
                float shareA = mb / (ma + mb), shareB = ma / (ma + mb);
                if (applyToA) a.Position -= n * (push * shareA);
                if (applyToB) b.Position += n * (push * shareB);
            }
            return result;
        }

        static float ApplyImpulse(ref VehicleState s, Vector3 impulse, Vector3 r, float mass, float yawInertia)
        {
            Vector3 dv = impulse / mass;
            float mag = dv.magnitude;
            if (mag > MaxDeltaV)
            {
                dv *= MaxDeltaV / mag;
                mag = MaxDeltaV;
            }
            s.Velocity += dv;
            float dYaw = (r.z * impulse.x - r.x * impulse.z) / yawInertia;
            s.AngularVelocity.y += Mathf.Clamp(dYaw, -MaxYawDelta, MaxYawDelta);
            return mag;
        }
    }
}
