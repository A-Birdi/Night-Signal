using System;

namespace NightSignal.AudioSynth
{
    /// <summary>Mirrors NightSignal.Vehicle.EngineFamily (same numeric values) without referencing the engine.</summary>
    public enum EngineFamilyKind : byte { Inline4 = 0, Six = 1, Triple = 2, Rotary = 3 }

    /// <summary>Mirrors NightSignal.Vehicle.SurfaceKind (same numeric values).</summary>
    public enum TyreSurface : byte { Asphalt = 0, Kerb = 1, Shoulder = 2, Grass = 3, Concrete = 4 }

    public enum BlowOffStyle : byte { None = 0, Flutter = 1, Valve = 2 }

    /// <summary>Per-car engine sound configuration, filled from the car's VehicleParams by the Unity wrapper.</summary>
    public sealed class EngineConfig
    {
        public EngineFamilyKind Family = EngineFamilyKind.Inline4;
        public float IdleRpm = 900f;
        public float RedlineRpm = 7400f;
        public bool Turbo;
        public BlowOffStyle BlowOff = BlowOffStyle.Flutter;
        /// <summary>Stable per-car seed (e.g. hash of the car model id): gives two cars of one family slightly different voices.</summary>
        public uint Seed = 1u;
    }

    /// <summary>
    /// Tonal family of an engine architecture. The partial table holds the level of each half engine order
    /// (0.5, 1.0, ... 12.0 × crank rotation frequency); the firing order is where a four-stroke engine's
    /// combustion pulses fall: inline-four 2nd order, six 3rd, triple 1.5th, two-rotor rotary 2nd (with a flat,
    /// buzzy harmonic roll-off). Non-firing "sub-orders" come from cylinder-to-cylinder imbalance and give the
    /// triple its off-beat thrum and the idle its lope. Three bands (idle / load / coast) are blended by state.
    /// </summary>
    public sealed class EngineProfile
    {
        public const int Partials = 24;

        public EngineFamilyKind Family;
        /// <summary>Combustion events per 720° cycle.</summary>
        public int FiringsPerCycle;
        /// <summary>Half-order index of the firing frequency (h = 2 × order).</summary>
        public int FiringHarmonic;
        public readonly float[] Orders = new float[Partials + 1];
        public readonly bool[] IsFiring = new bool[Partials + 1];

        public float TiltIdle, TiltLoad, TiltCoast;
        public float SubIdle, SubLoad, SubCoast;
        public float PulseMs;
        public float PulseIdle, PulseLoad, PulseCoast;
        public float RaspIdle, RaspLoad, RaspCoast;
        public float Roughness;
        public float Formant1Hz, Formant2Hz, FormantRes, FormantMix;
        public float IntakeHz, IntakeLevel;
        public float ToneIdleHz, ToneLoadHz, ToneCoastHz;
        public float LevelIdle, LevelLoad, LevelCoast;
        public float IdleLope;
        public float CrackleRate;
        public float LimiterHz;
        public float Gain;

        public static EngineProfile Create(EngineFamilyKind family)
        {
            var p = new EngineProfile { Family = family };
            switch (family)
            {
                case EngineFamilyKind.Six:
                    // Smooth, silky: strong 3rd/6th/9th orders, very little low-order content.
                    p.FiringsPerCycle = 6; p.FiringHarmonic = 6;
                    Firing(p, 1.0f, 0.62f, 0.4f, 0.3f);
                    Sub(p, 3, 0.12f); Sub(p, 9, 0.1f); Sub(p, 2, 0.04f); Sub(p, 4, 0.05f); Sub(p, 15, 0.06f); Sub(p, 21, 0.04f);
                    FillSub(p, 0.015f);
                    p.TiltIdle = 0.5f; p.TiltLoad = 0.05f; p.TiltCoast = 0.8f;
                    p.SubIdle = 1.4f; p.SubLoad = 0.8f; p.SubCoast = 1.3f;
                    p.PulseMs = 0.55f; p.PulseIdle = 0.25f; p.PulseLoad = 0.4f; p.PulseCoast = 0.2f;
                    p.RaspIdle = 0.08f; p.RaspLoad = 0.16f; p.RaspCoast = 0.06f;
                    p.Roughness = 0.06f;
                    p.Formant1Hz = 330f; p.Formant2Hz = 1150f; p.FormantRes = 0.55f; p.FormantMix = 0.5f;
                    p.IntakeHz = 700f; p.IntakeLevel = 0.18f;
                    p.ToneIdleHz = 2400f; p.ToneLoadHz = 6500f; p.ToneCoastHz = 1900f;
                    p.IdleLope = 0.12f; p.CrackleRate = 1.5f; p.LimiterHz = 19f; p.Gain = 0.95f;
                    break;
                case EngineFamilyKind.Triple:
                    // Compact triple: 1.5-order fundamental with strong half and first orders (uneven thrum).
                    p.FiringsPerCycle = 3; p.FiringHarmonic = 3;
                    Firing(p, 1.0f, 0.72f, 0.52f, 0.4f, 0.3f, 0.22f, 0.16f, 0.12f);
                    Sub(p, 1, 0.34f); Sub(p, 2, 0.4f); Sub(p, 4, 0.2f); Sub(p, 5, 0.14f); Sub(p, 7, 0.08f); Sub(p, 8, 0.07f);
                    FillSub(p, 0.04f);
                    p.TiltIdle = 0.35f; p.TiltLoad = 0.1f; p.TiltCoast = 0.75f;
                    p.SubIdle = 1.3f; p.SubLoad = 0.85f; p.SubCoast = 1.25f;
                    p.PulseMs = 1.25f; p.PulseIdle = 0.45f; p.PulseLoad = 0.6f; p.PulseCoast = 0.35f;
                    p.RaspIdle = 0.15f; p.RaspLoad = 0.28f; p.RaspCoast = 0.1f;
                    p.Roughness = 0.22f;
                    p.Formant1Hz = 250f; p.Formant2Hz = 880f; p.FormantRes = 0.6f; p.FormantMix = 0.55f;
                    p.IntakeHz = 560f; p.IntakeLevel = 0.22f;
                    p.ToneIdleHz = 1800f; p.ToneLoadHz = 4800f; p.ToneCoastHz = 1500f;
                    p.IdleLope = 0.55f; p.CrackleRate = 3.5f; p.LimiterHz = 17f; p.Gain = 0.9f;
                    break;
                case EngineFamilyKind.Rotary:
                    // Two-rotor rotary-like: 2nd-order firing with an almost flat harmonic series (buzzy rasp),
                    // clean sub-orders, sharp exhaust pulses and a lumpy idle.
                    p.FiringsPerCycle = 4; p.FiringHarmonic = 4;
                    Firing(p, 1.0f, 0.86f, 0.74f, 0.64f, 0.56f, 0.5f);
                    Sub(p, 2, 0.05f); Sub(p, 6, 0.04f); Sub(p, 10, 0.03f);
                    FillSub(p, 0.012f);
                    p.TiltIdle = 0.3f; p.TiltLoad = -0.1f; p.TiltCoast = 0.55f;
                    p.SubIdle = 2.2f; p.SubLoad = 0.7f; p.SubCoast = 1.4f;
                    p.PulseMs = 0.32f; p.PulseIdle = 0.55f; p.PulseLoad = 0.8f; p.PulseCoast = 0.45f;
                    p.RaspIdle = 0.22f; p.RaspLoad = 0.42f; p.RaspCoast = 0.2f;
                    p.Roughness = 0.05f;
                    p.Formant1Hz = 640f; p.Formant2Hz = 2300f; p.FormantRes = 0.5f; p.FormantMix = 0.45f;
                    p.IntakeHz = 1200f; p.IntakeLevel = 0.26f;
                    p.ToneIdleHz = 3200f; p.ToneLoadHz = 9000f; p.ToneCoastHz = 2800f;
                    p.IdleLope = 0.9f; p.CrackleRate = 7f; p.LimiterHz = 29f; p.Gain = 0.82f;
                    break;
                default:
                    // Inline-four: dominant 2nd order and its harmonics, some 1st-order imbalance.
                    p.FiringsPerCycle = 4; p.FiringHarmonic = 4;
                    Firing(p, 1.0f, 0.58f, 0.42f, 0.3f, 0.22f, 0.16f);
                    Sub(p, 2, 0.22f); Sub(p, 6, 0.14f); Sub(p, 10, 0.08f); Sub(p, 14, 0.06f);
                    FillSub(p, 0.03f);
                    p.TiltIdle = 0.4f; p.TiltLoad = 0f; p.TiltCoast = 0.7f;
                    p.SubIdle = 1.3f; p.SubLoad = 0.9f; p.SubCoast = 1.2f;
                    p.PulseMs = 0.8f; p.PulseIdle = 0.35f; p.PulseLoad = 0.5f; p.PulseCoast = 0.28f;
                    p.RaspIdle = 0.1f; p.RaspLoad = 0.22f; p.RaspCoast = 0.08f;
                    p.Roughness = 0.12f;
                    p.Formant1Hz = 420f; p.Formant2Hz = 1550f; p.FormantRes = 0.55f; p.FormantMix = 0.5f;
                    p.IntakeHz = 900f; p.IntakeLevel = 0.22f;
                    p.ToneIdleHz = 2600f; p.ToneLoadHz = 7000f; p.ToneCoastHz = 2200f;
                    p.IdleLope = 0.3f; p.CrackleRate = 2.5f; p.LimiterHz = 24f; p.Gain = 1f;
                    break;
            }
            p.LevelIdle = 0.45f;
            p.LevelLoad = 1f;
            p.LevelCoast = 0.5f;
            return p;
        }

        static void Firing(EngineProfile p, params float[] levels)
        {
            for (int i = 0; i < levels.Length; i++)
            {
                int h = p.FiringHarmonic * (i + 1);
                if (h > Partials) break;
                p.Orders[h] = levels[i];
                p.IsFiring[h] = true;
            }
        }

        static void Sub(EngineProfile p, int h, float level)
        {
            if (h >= 1 && h <= Partials && !p.IsFiring[h]) p.Orders[h] = level;
        }

        static void FillSub(EngineProfile p, float level)
        {
            for (int h = 1; h <= Partials; h++)
                if (!p.IsFiring[h] && p.Orders[h] == 0f) p.Orders[h] = level;
        }
    }
}
