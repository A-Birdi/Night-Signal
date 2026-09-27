using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Lookup tables and cheap approximations shared by every synth component. Engine-free and allocation-free
    /// after the static tables are built (call <see cref="Warm"/> from a non-audio thread first).
    /// </summary>
    public static class SynthMath
    {
        public const float Pi = 3.14159265358979f;
        public const float TwoPi = 6.28318530717959f;

        const int SineSize = 4096;
        const int SineMask = SineSize - 1;
        static readonly float[] sine = BuildSine();

        static float[] BuildSine()
        {
            var t = new float[SineSize + 1];
            for (int i = 0; i <= SineSize; i++)
                t[i] = (float)Math.Sin(2.0 * Math.PI * i / SineSize);
            return t;
        }

        /// <summary>Forces the static tables to be built on the calling thread (never on the audio thread).</summary>
        public static void Warm()
        {
            if (sine.Length != SineSize + 1) throw new InvalidOperationException("sine table");
        }

        /// <summary>sin(2π·phase) by table lookup. Valid for phase in [0, 2); callers keep phases wrapped.</summary>
        public static float Sin01(float phase)
        {
            float x = phase * SineSize;
            int i = (int)x;
            float f = x - i;
            i &= SineMask;
            float a = sine[i];
            return a + (sine[i + 1] - a) * f;
        }

        /// <summary>sin(2π·cycles) for any finite value (wraps with a floor).</summary>
        public static float SinAny(float cycles)
        {
            cycles -= (float)Math.Floor(cycles);
            return Sin01(cycles);
        }

        /// <summary>cos(2π·phase) for phase in [0, 1).</summary>
        public static float Cos01(float phase) => Sin01(phase + 0.25f);

        public static float Wrap01(float x) => x - (float)Math.Floor(x);

        public static float MidiToHz(float note) => 440f * MathF.Pow(2f, (note - 69f) * (1f / 12f));

        public static float DbToGain(float db) => db <= -120f ? 0f : MathF.Pow(10f, db * 0.05f);

        public static float GainToDb(float gain) => gain <= 1e-6f ? -120f : 20f * MathF.Log10(gain);

        public static float Exp2(float x) => MathF.Pow(2f, x);

        /// <summary>Smooth saturator: tanh-like rational curve, exactly ±1 beyond |x| = 3.</summary>
        public static float SoftClip(float x)
        {
            if (x > 3f) return 1f;
            if (x < -3f) return -1f;
            float x2 = x * x;
            return x * (27f + x2) / (27f + 9f * x2);
        }

        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>One-pole smoothing coefficient: y += (x - y) * (1 - coef) reaches 63% after <paramref name="seconds"/>.</summary>
        public static float SmoothCoef(float seconds, float sampleRate) =>
            seconds <= 0f ? 0f : MathF.Exp(-1f / (seconds * sampleRate));

        /// <summary>Per-sample multiplier that decays by 60 dB over <paramref name="seconds"/>.</summary>
        public static float DecayCoef(float seconds, float sampleRate) =>
            seconds <= 0f ? 0f : MathF.Exp(-6.9077553f / (seconds * sampleRate));

        /// <summary>Equal-power pan gains for pan in [-1, 1].</summary>
        public static void PanGains(float pan, out float left, out float right)
        {
            float p = (Clamp(pan, -1f, 1f) + 1f) * 0.125f; // 0..0.25 cycles => 0..90 degrees
            left = Cos01(p);
            right = Sin01(p);
        }
    }
}
