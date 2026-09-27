using System.Collections.Generic;
using UnityEngine;

namespace NightSignal.Vehicle
{
    public enum SurfaceKind : byte { Asphalt = 0, Kerb = 1, Shoulder = 2, Grass = 3, Concrete = 4 }

    public struct GroundHit
    {
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
        public SurfaceKind Surface;
    }

    public struct BarrierContact
    {
        public Vector3 Normal;
        public float Depth;
        /// <summary>Stable per-surface ID for the 0.75 s wall-impact debounce.</summary>
        public int SurfaceId;
    }

    /// <summary>Static course geometry as the chassis sees it. Implemented over Unity physics or analytic test worlds.</summary>
    public interface IVehicleWorld
    {
        bool CastWheel(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit);
        /// <summary>Barrier penetrations for the body box; returns the number written to <paramref name="buffer"/>.</summary>
        int ResolveBody(Vector3 center, Quaternion rotation, Vector3 halfExtents, BarrierContact[] buffer);
        /// <summary>Sweeps the body box against barriers (anti-tunnelling). Fraction is 0..1 of the path.</summary>
        bool SweepBody(Vector3 from, Vector3 to, Quaternion rotation, Vector3 halfExtents, out float fraction, out Vector3 normal);
    }

    public static class SurfaceGrip
    {
        public static float Grip(SurfaceKind s)
        {
            switch (s)
            {
                case SurfaceKind.Kerb: return 0.95f;
                case SurfaceKind.Shoulder: return 0.62f;
                case SurfaceKind.Grass: return 0.5f;
                case SurfaceKind.Concrete: return 0.97f;
                default: return 1f;
            }
        }

        /// <summary>Extra rolling drag on loose surfaces, as a fraction of normal load.</summary>
        public static float Drag(SurfaceKind s) => s == SurfaceKind.Shoulder ? 0.06f : s == SurfaceKind.Grass ? 0.09f : 0.012f;
    }

    /// <summary>Marks a collider's surface kind for wheel queries.</summary>
    public sealed class SurfaceTag : MonoBehaviour
    {
        public SurfaceKind Surface = SurfaceKind.Asphalt;
    }

    /// <summary>
    /// Physics-backed world for a specific <see cref="PhysicsScene"/>. Wheels hit the drivable layer only;
    /// the body resolves against the barrier layer. Cars never collide with each other here (non-contact
    /// default); Light Contact is applied separately by the race server.
    /// </summary>
    public sealed class PhysicsVehicleWorld : IVehicleWorld
    {
        readonly PhysicsScene scene;
        readonly int drivableMask;
        readonly int barrierMask;
        readonly Collider[] overlap = new Collider[16];
        readonly Dictionary<Collider, SurfaceKind> surfaceCache = new Dictionary<Collider, SurfaceKind>();

        public PhysicsVehicleWorld(PhysicsScene scene, int drivableMask, int barrierMask)
        {
            this.scene = scene;
            this.drivableMask = drivableMask;
            this.barrierMask = barrierMask;
        }

        public bool CastWheel(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit)
        {
            if (scene.Raycast(origin, direction, out RaycastHit h, maxDistance, drivableMask, QueryTriggerInteraction.Ignore))
            {
                hit = new GroundHit { Point = h.point, Normal = h.normal, Distance = h.distance, Surface = SurfaceOf(h.collider) };
                return true;
            }
            hit = default;
            return false;
        }

        public int ResolveBody(Vector3 center, Quaternion rotation, Vector3 halfExtents, BarrierContact[] buffer)
        {
            // Body resolves against barriers and the road itself (an overturned car rests on the surface).
            int n = scene.OverlapBox(center, halfExtents, overlap, rotation, barrierMask | drivableMask, QueryTriggerInteraction.Ignore);
            int written = 0;
            BoxProbe probe = BoxProbe.Get(halfExtents);
            for (int i = 0; i < n && written < buffer.Length; i++)
            {
                Collider c = overlap[i];
                if (Physics.ComputePenetration(probe.Box, center, rotation, c, c.transform.position, c.transform.rotation,
                        out Vector3 dir, out float depth) && depth > 0f)
                    buffer[written++] = new BarrierContact { Normal = dir, Depth = depth, SurfaceId = c.GetHashCode() };
            }
            return written;
        }

        public bool SweepBody(Vector3 from, Vector3 to, Quaternion rotation, Vector3 halfExtents, out float fraction, out Vector3 normal)
        {
            Vector3 delta = to - from;
            float dist = delta.magnitude;
            fraction = 1f;
            normal = Vector3.zero;
            if (dist < 1e-4f) return false;
            if (scene.BoxCast(from, halfExtents, delta / dist, out RaycastHit h, rotation, dist, barrierMask, QueryTriggerInteraction.Ignore))
            {
                fraction = h.distance / dist;
                normal = h.normal;
                return true;
            }
            return false;
        }

        SurfaceKind SurfaceOf(Collider c)
        {
            if (!surfaceCache.TryGetValue(c, out SurfaceKind s))
            {
                SurfaceTag tag = c.GetComponent<SurfaceTag>();
                s = tag != null ? tag.Surface : SurfaceKind.Asphalt;
                surfaceCache[c] = s;
            }
            return s;
        }

        /// <summary>
        /// A hidden box collider used only as the ComputePenetration probe shape. It must be ACTIVE and enabled —
        /// ComputePenetration silently returns false for a probe on an inactive object (measured: barrier depenetration
        /// never ran while it was inactive). It is a trigger on the Ignore Raycast layer, far below the world, so no
        /// gameplay query or rigidbody ever touches it.
        /// </summary>
        sealed class BoxProbe
        {
            const int IgnoreRaycastLayer = 2;
            static readonly Dictionary<Vector3, BoxProbe> Probes = new Dictionary<Vector3, BoxProbe>();
            public BoxCollider Box;

            public static BoxProbe Get(Vector3 halfExtents)
            {
                if (Probes.TryGetValue(halfExtents, out BoxProbe p) && p.Box != null) return p;
                // Reuse a hidden probe that survived an editor domain reload instead of leaking another one.
                BoxCollider box = null;
                foreach (BoxCollider b in Resources.FindObjectsOfTypeAll<BoxCollider>())
                    if (b != null && b.gameObject.name == "VehicleBodyProbe" && b.size == halfExtents * 2f) { box = b; break; }
                if (box == null)
                {
                    var go = new GameObject("VehicleBodyProbe") { hideFlags = HideFlags.HideAndDontSave };
                    box = go.AddComponent<BoxCollider>();
                    box.size = halfExtents * 2f;
                }
                box.gameObject.layer = IgnoreRaycastLayer;
                box.gameObject.SetActive(true);
                box.transform.position = new Vector3(0f, -100000f, 0f);
                box.isTrigger = true;
                box.enabled = true;
                p = new BoxProbe { Box = box };
                Probes[halfExtents] = p;
                return p;
            }
        }
    }

    /// <summary>Analytic infinite plane (optionally inclined) for deterministic unit tests and the dyno harness.</summary>
    public sealed class PlaneVehicleWorld : IVehicleWorld
    {
        readonly Vector3 normal;
        readonly float offset;
        readonly SurfaceKind surface;

        public PlaneVehicleWorld(Vector3 normal, float heightAtOrigin = 0f, SurfaceKind surface = SurfaceKind.Asphalt)
        {
            this.normal = normal.normalized;
            offset = heightAtOrigin;
            this.surface = surface;
        }

        public static PlaneVehicleWorld Flat => new PlaneVehicleWorld(Vector3.up);

        public bool CastWheel(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit)
        {
            float denom = Vector3.Dot(normal, direction);
            hit = default;
            if (denom > -1e-5f) return false;
            float t = (offset * normal.y - Vector3.Dot(normal, origin)) / denom;
            if (t < 0f || t > maxDistance) return false;
            hit = new GroundHit { Point = origin + direction * t, Normal = normal, Distance = t, Surface = surface };
            return true;
        }

        public int ResolveBody(Vector3 center, Quaternion rotation, Vector3 halfExtents, BarrierContact[] buffer) => 0;

        public bool SweepBody(Vector3 from, Vector3 to, Quaternion rotation, Vector3 halfExtents, out float fraction, out Vector3 n)
        {
            fraction = 1f;
            n = Vector3.zero;
            return false;
        }
    }
}
