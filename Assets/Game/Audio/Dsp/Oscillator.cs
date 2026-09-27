namespace NightSignal.AudioSynth
{
    public enum Waveform : byte { Sine = 0, Triangle = 1, Saw = 2, Square = 3, Pulse = 4, Noise = 5 }

    /// <summary>
    /// Phase-accumulator oscillator. Saw/square/pulse are band-limited with polyBLEP residuals; the triangle is
    /// naive (its 1/n² harmonics alias negligibly); the sine uses the shared lookup table.
    /// </summary>
    public struct Oscillator
    {
        /// <summary>Phase in cycles, [0, 1).</summary>
        public float Phase;
        /// <summary>Cycles per sample (frequency / sample rate), clamped below Nyquist.</summary>
        public float Increment;

        public void SetFrequency(float hz, float sampleRate)
        {
            float inc = hz / sampleRate;
            Increment = inc < 0f ? 0f : (inc > 0.49f ? 0.49f : inc);
        }

        public float Next(Waveform wave, float pulseWidth, ref Rng rng)
        {
            float t = Phase;
            float dt = Increment;
            float v;
            switch (wave)
            {
                case Waveform.Sine:
                    v = SynthMath.Sin01(t);
                    break;
                case Waveform.Triangle:
                    v = t < 0.5f ? 4f * t - 1f : 3f - 4f * t;
                    break;
                case Waveform.Saw:
                    v = 2f * t - 1f - PolyBlep(t, dt);
                    break;
                case Waveform.Square:
                case Waveform.Pulse:
                {
                    float pw = wave == Waveform.Square ? 0.5f : pulseWidth;
                    if (pw < 0.05f) pw = 0.05f; else if (pw > 0.95f) pw = 0.95f;
                    v = t < pw ? 1f : -1f;
                    v += PolyBlep(t, dt);
                    float t2 = t - pw;
                    if (t2 < 0f) t2 += 1f;
                    v -= PolyBlep(t2, dt);
                    break;
                }
                default:
                    v = rng.NextBipolar();
                    break;
            }
            t += dt;
            if (t >= 1f) t -= 1f;
            Phase = t;
            return v;
        }

        /// <summary>Band-limited saw only (hot path for supersaw stacks).</summary>
        public float NextSaw()
        {
            float t = Phase;
            float dt = Increment;
            float v = 2f * t - 1f - PolyBlep(t, dt);
            t += dt;
            if (t >= 1f) t -= 1f;
            Phase = t;
            return v;
        }

        /// <summary>Two-sample polynomial band-limited step residual for a discontinuity at phase 0.</summary>
        public static float PolyBlep(float t, float dt)
        {
            if (dt <= 0f) return 0f;
            if (t < dt)
            {
                t /= dt;
                return t + t - t * t - 1f;
            }
            if (t > 1f - dt)
            {
                t = (t - 1f) / dt;
                return t * t + t + t + 1f;
            }
            return 0f;
        }
    }
}
