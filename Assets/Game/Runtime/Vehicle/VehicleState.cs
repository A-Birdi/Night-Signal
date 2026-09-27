using UnityEngine;

namespace NightSignal.Vehicle
{
    /// <summary>
    /// Complete authoritative chassis state. A plain value type with no arrays so history buffers for
    /// reconciliation can copy it cheaply. Visual suspension/lean are derived separately in the view.
    /// </summary>
    public struct VehicleState
    {
        public uint Tick;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        public float SteerAngle;    // front wheel angle, radians (+ = right)
        public float EngineRpm;
        public sbyte Gear;          // -1 reverse, 0 neutral, 1..N
        public float ShiftTimer;    // seconds of torque interruption remaining
        public float Boost;         // 0..1 forced-induction spool (1 for naturally aspirated)
        public float ReverseTimer;

        // Suspension compression per wheel (FL, FR, RL, RR), metres — needed for damper velocity.
        public float C0, C1, C2, C3;

        public float GetCompression(int i)
        {
            switch (i) { case 0: return C0; case 1: return C1; case 2: return C2; default: return C3; }
        }

        public void SetCompression(int i, float v)
        {
            switch (i) { case 0: C0 = v; break; case 1: C1 = v; break; case 2: C2 = v; break; default: C3 = v; break; }
        }

        public float SpeedKmh => Velocity.magnitude * 3.6f;

        public static VehicleState AtRest(Vector3 position, Quaternion rotation) => new VehicleState
        {
            Position = position,
            Rotation = rotation,
            Gear = 1,
            Boost = 1f,
            EngineRpm = 900f,
        };
    }
}
