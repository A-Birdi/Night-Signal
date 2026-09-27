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
        public const int ProtocolVersion = 1;
        public const string MsgInput = "ns.input";         // client → server, unreliable sequenced, 30 Hz, redundant
        public const string MsgSnapshot = "ns.snap";       // server → client, unreliable sequenced, 20 Hz
        public const string MsgMatch = "ns.match";         // server → client, reliable: match roster/config (JSON)
        public const string MsgLoaded = "ns.loaded";       // client → server, reliable: loading progress / ready
        public const string MsgPhase = "ns.phase";         // server → client, reliable: phase + start tick
        public const string MsgResults = "ns.results";     // server → client, reliable: results summary (JSON)
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

    /// <summary>Entrant status as broadcast in snapshots (spec §4 entrant status machine).</summary>
    public enum EntrantStatus : byte { Reserved = 0, Loading = 1, Loaded = 2, Racing = 3, Finished = 4, Dnf = 5, DqDisconnected = 6, DqQuit = 7, Spectator = 8 }

    /// <summary>Match phase broadcast on the reliable channel.</summary>
    public enum MatchPhase : byte { WaitingForEntrants = 0, Loading = 1, Countdown = 2, Racing = 3, Results = 4, Aborted = 5 }
}
