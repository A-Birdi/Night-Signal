using NightSignal.Vehicle;
// FastBufferWriter/Reader are handle-backed structs: passing by value shares the underlying buffer and position.
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace NightSignal.Net
{
    /// <summary>Gameplay message names and binary layouts (docs/NETWORKING.md, gameplay section).</summary>
    public static class Wire
    {
        /// <summary>2: per-client snapshots (own car full, others compact), ghost flags, finish window (12 vehicles).</summary>
        public const int ProtocolVersion = 2;
        public const string MsgInput = "ns.input";         // client → server, unreliable sequenced, 30 Hz, redundant
        public const string MsgSnapshot = "ns.snap";       // server → client, unreliable sequenced, 20 Hz
        public const string MsgMatch = "ns.match";         // server → client, reliable: match roster/config (JSON)
        public const string MsgLoaded = "ns.loaded";       // client → server, reliable: loading progress / ready
        public const string MsgPhase = "ns.phase";         // server → client, reliable: phase + start tick
        public const string MsgResults = "ns.results";     // server → client, reliable: results summary (JSON)
        public const string MsgDrift = "ns.drift";         // server → client, unreliable sequenced, 10 Hz: the driver's own drift figures
        public const string MsgRecovery = "ns.recovery";   // server → client, unreliable sequenced, 10 Hz: the driver's own recovery offer
        public const int InputRedundancy = 8;

        public static void Write(FastBufferWriter w, in VehicleState s)
        {
            w.WriteValueSafe(s.Tick);
            w.WriteValueSafe(s.Position);
            w.WriteValueSafe(s.Rotation);
            w.WriteValueSafe(s.Velocity);
            w.WriteValueSafe(s.AngularVelocity);
            w.WriteValueSafe(s.SteerAngle);
            w.WriteValueSafe(s.EngineRpm);
            w.WriteValueSafe(s.Gear);
            w.WriteValueSafe(s.ShiftTimer);
            w.WriteValueSafe(s.Boost);
            w.WriteValueSafe(s.ReverseTimer);
            w.WriteValueSafe(s.C0);
            w.WriteValueSafe(s.C1);
            w.WriteValueSafe(s.C2);
            w.WriteValueSafe(s.C3);
        }

        public static VehicleState ReadState(FastBufferReader r)
        {
            var s = new VehicleState();
            r.ReadValueSafe(out s.Tick);
            r.ReadValueSafe(out s.Position);
            r.ReadValueSafe(out s.Rotation);
            r.ReadValueSafe(out s.Velocity);
            r.ReadValueSafe(out s.AngularVelocity);
            r.ReadValueSafe(out s.SteerAngle);
            r.ReadValueSafe(out s.EngineRpm);
            r.ReadValueSafe(out s.Gear);
            r.ReadValueSafe(out s.ShiftTimer);
            r.ReadValueSafe(out s.Boost);
            r.ReadValueSafe(out s.ReverseTimer);
            r.ReadValueSafe(out s.C0);
            r.ReadValueSafe(out s.C1);
            r.ReadValueSafe(out s.C2);
            r.ReadValueSafe(out s.C3);
            return s;
        }

        /// <summary>
        /// Compact remote-car state (40 bytes): exact position, smallest-three rotation, half-precision velocities, steer,
        /// rpm, gear and suspension. Enough to render, interpolate and predict contact; the recipient's own car is always
        /// sent in full so reconciliation compares exact values.
        /// </summary>
        public static void WriteCompact(FastBufferWriter w, in VehicleState s)
        {
            w.WriteValueSafe(s.Position);
            WriteRotation(w, s.Rotation);
            w.WriteValueSafe(Mathf.FloatToHalf(s.Velocity.x));
            w.WriteValueSafe(Mathf.FloatToHalf(s.Velocity.y));
            w.WriteValueSafe(Mathf.FloatToHalf(s.Velocity.z));
            w.WriteValueSafe(Mathf.FloatToHalf(s.AngularVelocity.x));
            w.WriteValueSafe(Mathf.FloatToHalf(s.AngularVelocity.y));
            w.WriteValueSafe(Mathf.FloatToHalf(s.AngularVelocity.z));
            w.WriteValueSafe(Mathf.FloatToHalf(s.SteerAngle));
            w.WriteValueSafe((ushort)Mathf.Clamp(s.EngineRpm, 0f, 65535f));
            w.WriteValueSafe(s.Gear);
            w.WriteValueSafe((byte)Mathf.Clamp(s.C0 * 255f, 0f, 255f));
            w.WriteValueSafe((byte)Mathf.Clamp(s.C1 * 255f, 0f, 255f));
            w.WriteValueSafe((byte)Mathf.Clamp(s.C2 * 255f, 0f, 255f));
            w.WriteValueSafe((byte)Mathf.Clamp(s.C3 * 255f, 0f, 255f));
        }

        public static VehicleState ReadCompact(FastBufferReader r)
        {
            var s = new VehicleState();
            r.ReadValueSafe(out s.Position);
            s.Rotation = ReadRotation(r);
            r.ReadValueSafe(out ushort vx);
            r.ReadValueSafe(out ushort vy);
            r.ReadValueSafe(out ushort vz);
            s.Velocity = new Vector3(Mathf.HalfToFloat(vx), Mathf.HalfToFloat(vy), Mathf.HalfToFloat(vz));
            r.ReadValueSafe(out ushort wx);
            r.ReadValueSafe(out ushort wy);
            r.ReadValueSafe(out ushort wz);
            s.AngularVelocity = new Vector3(Mathf.HalfToFloat(wx), Mathf.HalfToFloat(wy), Mathf.HalfToFloat(wz));
            r.ReadValueSafe(out ushort steer);
            s.SteerAngle = Mathf.HalfToFloat(steer);
            r.ReadValueSafe(out ushort rpm);
            s.EngineRpm = rpm;
            r.ReadValueSafe(out s.Gear);
            r.ReadValueSafe(out byte c0);
            r.ReadValueSafe(out byte c1);
            r.ReadValueSafe(out byte c2);
            r.ReadValueSafe(out byte c3);
            s.C0 = c0 / 255f;
            s.C1 = c1 / 255f;
            s.C2 = c2 / 255f;
            s.C3 = c3 / 255f;
            s.Boost = 1f;
            return s;
        }

        /// <summary>Smallest-three quaternion: index of the largest component + three components as int16 (7 bytes).</summary>
        static void WriteRotation(FastBufferWriter w, Quaternion q)
        {
            q.Normalize();
            float x = q.x, y = q.y, z = q.z, qw = q.w;
            int largest = 0;
            float best = Mathf.Abs(x);
            if (Mathf.Abs(y) > best) { largest = 1; best = Mathf.Abs(y); }
            if (Mathf.Abs(z) > best) { largest = 2; best = Mathf.Abs(z); }
            if (Mathf.Abs(qw) > best) largest = 3;
            float sign = (largest == 0 ? x : largest == 1 ? y : largest == 2 ? z : qw) < 0f ? -1f : 1f;
            w.WriteValueSafe((byte)largest);
            if (largest != 0) w.WriteValueSafe(Pack(x * sign));
            if (largest != 1) w.WriteValueSafe(Pack(y * sign));
            if (largest != 2) w.WriteValueSafe(Pack(z * sign));
            if (largest != 3) w.WriteValueSafe(Pack(qw * sign));
        }

        static short Pack(float v) => (short)Mathf.RoundToInt(Mathf.Clamp(v, -0.70711f, 0.70711f) / 0.70711f * 32767f);

        static Quaternion ReadRotation(FastBufferReader r)
        {
            r.ReadValueSafe(out byte largest);
            float x = 0f, y = 0f, z = 0f, qw = 0f, sum = 0f;
            if (largest != 0) { x = Unpack(r); sum += x * x; }
            if (largest != 1) { y = Unpack(r); sum += y * y; }
            if (largest != 2) { z = Unpack(r); sum += z * z; }
            if (largest != 3) { qw = Unpack(r); sum += qw * qw; }
            float big = Mathf.Sqrt(Mathf.Max(0f, 1f - sum));
            if (largest == 0) x = big;
            else if (largest == 1) y = big;
            else if (largest == 2) z = big;
            else qw = big;
            return new Quaternion(x, y, z, qw);
        }

        static float Unpack(FastBufferReader r)
        {
            r.ReadValueSafe(out short v);
            return v / 32767f * 0.70711f;
        }

        public static void Write(FastBufferWriter w, DriverInput i)
        {
            w.WriteValueSafe(i.SteerQ);
            w.WriteValueSafe(i.ThrottleQ);
            w.WriteValueSafe(i.BrakeQ);
            w.WriteValueSafe((byte)i.Buttons);
        }

        public static DriverInput ReadInput(FastBufferReader r)
        {
            var i = new DriverInput();
            r.ReadValueSafe(out i.SteerQ);
            r.ReadValueSafe(out i.ThrottleQ);
            r.ReadValueSafe(out i.BrakeQ);
            r.ReadValueSafe(out byte b);
            i.Buttons = (InputButtons)b;
            return i;
        }

        public static FastBufferWriter JsonWriter(string json)
        {
            var w = new FastBufferWriter(1024, Allocator.Temp, 65536);
            w.WriteValueSafe(json);
            return w;
        }

        public static string ReadJson(FastBufferReader r)
        {
            r.ReadValueSafe(out string json);
            return json;
        }
    }
}
