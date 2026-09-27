using System;
using UnityEngine;

namespace NightSignal.Vehicle
{
    [Flags]
    public enum InputButtons : byte
    {
        None = 0,
        Handbrake = 1,
        /// <summary>Edge: pressed this tick.</summary>
        ShiftUp = 2,
        /// <summary>Edge: pressed this tick.</summary>
        ShiftDown = 4,
        /// <summary>Held reset request; the race layer applies the hold-to-confirm timing.</summary>
        ResetHeld = 8,
    }

    /// <summary>
    /// One tick of driving input, quantized exactly as it travels over the network. Clients simulate the
    /// quantized value too, so prediction and the authoritative server see bit-identical input.
    /// </summary>
    [Serializable]
    public struct DriverInput : IEquatable<DriverInput>
    {
        public sbyte SteerQ;
        public byte ThrottleQ;
        public byte BrakeQ;
        public InputButtons Buttons;

        public float Steer => SteerQ / 127f;
        public float Throttle => ThrottleQ / 255f;
        public float Brake => BrakeQ / 255f;
        public bool Handbrake => (Buttons & InputButtons.Handbrake) != 0;
        public bool ShiftUp => (Buttons & InputButtons.ShiftUp) != 0;
        public bool ShiftDown => (Buttons & InputButtons.ShiftDown) != 0;
        public bool ResetHeld => (Buttons & InputButtons.ResetHeld) != 0;

        public static readonly DriverInput Neutral = default;

        public static DriverInput Quantize(float steer, float throttle, float brake, InputButtons buttons)
        {
            return new DriverInput
            {
                SteerQ = (sbyte)Mathf.RoundToInt(Mathf.Clamp(steer, -1f, 1f) * 127f),
                ThrottleQ = (byte)Mathf.RoundToInt(Mathf.Clamp01(throttle) * 255f),
                BrakeQ = (byte)Mathf.RoundToInt(Mathf.Clamp01(brake) * 255f),
                Buttons = buttons,
            };
        }

        /// <summary>
        /// Input the server applies when a client's commands stop arriving: after the starvation window the car
        /// must not hold full throttle; it coasts, then brakes to a stable stop (spec §4.4).
        /// </summary>
        public DriverInput Starved(float secondsWithoutInput, float coastSeconds)
        {
            if (secondsWithoutInput <= coastSeconds)
                return this;
            float braking = Mathf.Clamp01((secondsWithoutInput - coastSeconds) / 0.5f);
            return Quantize(Steer * 0.5f, 0f, braking * 0.6f, InputButtons.None);
        }

        public bool Equals(DriverInput o) => SteerQ == o.SteerQ && ThrottleQ == o.ThrottleQ && BrakeQ == o.BrakeQ && Buttons == o.Buttons;
        public override bool Equals(object obj) => obj is DriverInput o && Equals(o);
        public override int GetHashCode() => (byte)SteerQ | (ThrottleQ << 8) | (BrakeQ << 16) | ((int)Buttons << 24);
        public override string ToString() => $"steer {Steer:F2} thr {Throttle:F2} brk {Brake:F2} {Buttons}";
    }
}
