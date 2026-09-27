using System;

namespace NightSignal.AudioSynth
{
    public enum FilterMode : byte { Off = 0, LowPass = 1, BandPass = 2, HighPass = 3 }

    /// <summary>
    /// Trapezoidal (zero-delay-feedback) state-variable filter after A. Simper. Stable under fast modulation,
    /// so cutoff can follow envelopes and LFOs at control rate without zipper artefacts.
    /// </summary>
    public struct Svf
    {
        float ic1, ic2, a1, a2, a3, k;

        /// <param name="resonance">0 (none) .. 0.98 (near self-oscillation).</param>
        public void Set(float cutoffHz, float resonance, float sampleRate)
        {
            float max = sampleRate * 0.45f;
            if (cutoffHz < 16f) cutoffHz = 16f;
            else if (cutoffHz > max) cutoffHz = max;
            float g = MathF.Tan(SynthMath.Pi * cutoffHz / sampleRate);
            float r = resonance < 0f ? 0f : (resonance > 0.98f ? 0.98f : resonance);
            k = 2f - 2f * r;
            a1 = 1f / (1f + g * (g + k));
            a2 = g * a1;
            a3 = g * a2;
        }

        public float Process(float v0, FilterMode mode)
        {
            float v3 = v0 - ic2;
            float v1 = a1 * ic1 + a2 * v3;
            float v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2f * v1 - ic1;
            ic2 = 2f * v2 - ic2;
            switch (mode)
            {
                case FilterMode.LowPass: return v2;
                case FilterMode.BandPass: return v1 * k;
                case FilterMode.HighPass: return v0 - k * v1 - v2;
                default: return v0;
            }
        }

        public float LowPass(float v0)
        {
            float v3 = v0 - ic2;
            float v1 = a1 * ic1 + a2 * v3;
            float v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2f * v1 - ic1;
            ic2 = 2f * v2 - ic2;
            return v2;
        }

        /// <summary>Band-pass normalised to unity gain at the centre frequency.</summary>
        public float BandPass(float v0)
        {
            float v3 = v0 - ic2;
            float v1 = a1 * ic1 + a2 * v3;
            float v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2f * v1 - ic1;
            ic2 = 2f * v2 - ic2;
            return v1 * k;
        }

        public float HighPass(float v0)
        {
            float v3 = v0 - ic2;
            float v1 = a1 * ic1 + a2 * v3;
            float v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2f * v1 - ic1;
            ic2 = 2f * v2 - ic2;
            return v0 - k * v1 - v2;
        }

        public void Reset() { ic1 = 0f; ic2 = 0f; }

        /// <summary>Flushes tiny states to zero so silent tails never run on denormals.</summary>
        public void Flush()
        {
            if (ic1 < 1e-15f && ic1 > -1e-15f) ic1 = 0f;
            if (ic2 < 1e-15f && ic2 > -1e-15f) ic2 = 0f;
        }
    }

    /// <summary>One-pole low-pass (6 dB/oct); <see cref="HighPass"/> is its complement.</summary>
    public struct OnePole
    {
        public float Z;
        float a;

        public void SetHz(float hz, float sampleRate)
        {
            a = 1f - MathF.Exp(-SynthMath.TwoPi * hz / sampleRate);
        }

        public float LowPass(float x)
        {
            Z += a * (x - Z);
            return Z;
        }

        public float HighPass(float x)
        {
            Z += a * (x - Z);
            return x - Z;
        }
    }

    /// <summary>First-order DC blocker (≈ 10 Hz corner at 48 kHz).</summary>
    public struct DcBlocker
    {
        float x1, y1;

        public float Process(float x)
        {
            float y = x - x1 + 0.9987f * y1;
            x1 = x;
            y1 = y;
            return y;
        }
    }
}
