using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Drum synthesis from oscillators and noise only: pitch-swept sine kicks and toms, tone+noise snares, a
    /// multi-burst clap, six-oscillator metallic hats/cymbals, rim clicks and shakers. Allocation-free.
    /// </summary>
    public sealed class DrumVoice
    {
        // Inharmonic square ratios for metallic sounds (classic analogue-machine style spacing).
        static readonly float[] MetalRatio = { 1f, 1.4471f, 1.617f, 1.9265f, 2.5028f, 2.6637f };

        readonly float sr;
        Rng rng;
        DrumPiece piece;
        DrumPieceSpec spec;
        bool active;
        int startDelay;
        float amp, ampCoef;
        float toneAmp, toneCoef;
        float pitchEnv, pitchCoef;
        float clickEnv, clickCoef;
        float phase1, phase2;

        float gL, gR;
        float velGain;
        int time;
        Svf f1, f2;
        readonly float[] metalPhase = new float[6];
        readonly float[] metalInc = new float[6];
        float attackEnv, attackInc;

        public bool IsActive => active;
        public DrumPiece Piece => piece;

        public DrumVoice(float sampleRate, uint seed)
        {
            sr = sampleRate;
            rng = new Rng(seed);
        }

        public void Trigger(DrumPiece p, DrumPieceSpec s, float velocity, int offset, float trackGain)
        {
            piece = p;
            spec = s;
            active = true;
            startDelay = offset;
            time = 0;
            velGain = (0.35f + 0.65f * velocity) * s.Level * trackGain;
            SynthMath.PanGains(s.Pan, out gL, out gR);
            amp = 1f;
            ampCoef = SynthMath.DecayCoef(Math.Max(0.005f, s.Decay), sr);
            phase1 = 0f;
            phase2 = 0f;

            f1.Reset();
            f2.Reset();
            switch (p)
            {
                case DrumPiece.Kick:
                    pitchEnv = 1f;
                    pitchCoef = SynthMath.DecayCoef(Math.Max(0.004f, s.PitchDecay) * 1.5f, sr);
                    clickEnv = s.Click;
                    clickCoef = SynthMath.DecayCoef(0.012f, sr);
                    f1.Set(3500f, 0f, sr);
                    break;
                case DrumPiece.Snare:
                    toneAmp = 1f;
                    toneCoef = SynthMath.DecayCoef(Math.Max(0.02f, s.Decay * 0.45f), sr);
                    pitchEnv = 1f;
                    pitchCoef = SynthMath.DecayCoef(0.03f, sr);
                    f1.Set(1400f, 0.1f, sr);
                    f2.Set(Math.Max(1500f, s.Tone), 0.1f, sr);
                    break;
                case DrumPiece.Clap:
                    f1.Set(Math.Max(300f, s.Tone), 0.55f, sr);
                    f2.Set(Math.Max(300f, s.Tone) * 2.2f, 0.2f, sr);
                    break;
                case DrumPiece.ClosedHat:
                case DrumPiece.OpenHat:
                case DrumPiece.Ride:
                case DrumPiece.Crash:
                {
                    float baseHz = 410f * (s.Tune > 0f ? s.Tune : 1f);
                    for (int i = 0; i < 6; i++)
                    {
                        metalPhase[i] = rng.NextFloat();
                        metalInc[i] = baseHz * MetalRatio[i] / sr;
                    }
                    float hp = p == DrumPiece.Ride ? 3500f : (p == DrumPiece.Crash ? 2800f : 6500f);
                    f1.Set(Math.Max(1000f, s.Tone), 0.35f, sr);
                    f2.Set(hp, 0.05f, sr);
                    attackEnv = p == DrumPiece.Crash ? 0.3f : 1f;
                    attackInc = 1f / (0.004f * sr);
                    break;
                }
                case DrumPiece.TomHigh:
                case DrumPiece.TomMid:
                case DrumPiece.TomLow:
                    pitchEnv = 1f;
                    pitchCoef = SynthMath.DecayCoef(Math.Max(0.01f, s.PitchDecay) * 2f, sr);
                    f1.Set(2500f, 0f, sr);
                    break;
                case DrumPiece.Rim:
                    f1.Set(Math.Max(500f, s.Tone), 0.6f, sr);
                    break;
                case DrumPiece.Shaker:
                    attackEnv = 0f;
                    attackInc = 1f / (0.012f * sr);
                    f1.Set(Math.Max(2000f, s.Tone), 0.2f, sr);
                    break;
            }
        }

        /// <summary>Quick fade (open hat choked by a closed hat).</summary>
        public void Choke()
        {
            if (active) ampCoef = Math.Min(ampCoef, SynthMath.DecayCoef(0.03f, sr));
        }

        public void Render(float[] left, float[] right, int count)
        {
            if (!active) return;
            int i0 = 0;
            if (startDelay > 0)
            {
                if (startDelay >= count) { startDelay -= count; return; }
                i0 = startDelay;
                startDelay = 0;
            }
            switch (piece)
            {
                case DrumPiece.Kick: RenderKick(left, right, i0, count); break;
                case DrumPiece.Snare: RenderSnare(left, right, i0, count); break;
                case DrumPiece.Clap: RenderClap(left, right, i0, count); break;
                case DrumPiece.TomHigh:
                case DrumPiece.TomMid:
                case DrumPiece.TomLow: RenderTom(left, right, i0, count); break;
                case DrumPiece.Rim: RenderRim(left, right, i0, count); break;
                case DrumPiece.Shaker: RenderShaker(left, right, i0, count); break;
                default: RenderMetal(left, right, i0, count); break;
            }
            if (amp < 1e-4f) active = false;
        }

        void Out(float[] left, float[] right, int i, float s)
        {
            left[i] += s * gL;
            right[i] += s * gR;
        }

        void RenderKick(float[] left, float[] right, int i0, int count)
        {
            float baseHz = spec.Tune, sweep = spec.Sweep, drive = 1f + spec.Drive;
            float norm = 1f / SynthMath.SoftClip(drive);
            float g = velGain;
            for (int i = i0; i < count; i++)
            {
                float hz = baseHz * (1f + sweep * pitchEnv);
                pitchEnv *= pitchCoef;
                phase1 += hz / sr;
                if (phase1 >= 1f) phase1 -= 1f;
                float body = SynthMath.Sin01(phase1) * amp;
                float click = f1.HighPass(rng.NextBipolar()) * clickEnv * 0.5f;
                clickEnv *= clickCoef;
                float s = SynthMath.SoftClip((body + click) * drive) * norm;
                amp *= ampCoef;
                Out(left, right, i, s * g);
            }
        }

        void RenderSnare(float[] left, float[] right, int i0, int count)
        {
            float hz1 = spec.Tune, hz2 = spec.Tune * 1.47f, noiseMix = spec.Noise;
            float g = velGain;
            for (int i = i0; i < count; i++)
            {
                float bend = 1f + 0.25f * pitchEnv;
                pitchEnv *= pitchCoef;
                phase1 += hz1 * bend / sr;
                if (phase1 >= 1f) phase1 -= 1f;
                phase2 += hz2 * bend / sr;
                if (phase2 >= 1f) phase2 -= 1f;
                float tone = (SynthMath.Sin01(phase1) + 0.6f * SynthMath.Sin01(phase2)) * 0.62f * toneAmp;
                toneAmp *= toneCoef;
                float n = f2.LowPass(f1.HighPass(rng.NextBipolar())) * 1.6f * amp;
                amp *= ampCoef;
                float s = tone * (1f - noiseMix) + n * noiseMix;
                Out(left, right, i, s * g);
            }
        }

        void RenderClap(float[] left, float[] right, int i0, int count)
        {
            int burst = (int)(0.0095f * sr);
            int burstsEnd = burst * 3;
            float burstDecay = SynthMath.DecayCoef(0.012f, sr);
            float g = velGain;
            for (int i = i0; i < count; i++)
            {
                float env;
                if (time < burstsEnd)
                {
                    if (time % burst == 0) toneAmp = 1f;
                    else toneAmp *= burstDecay;
                    env = toneAmp;
                }
                else
                {
                    env = amp;
                    amp *= ampCoef;
                }
                time++;
                float n = rng.NextBipolar();
                float s = (f1.BandPass(n) * 1.4f + f2.BandPass(n) * 0.5f) * env;
                Out(left, right, i, s * g);
            }
        }

        void RenderMetal(float[] left, float[] right, int i0, int count)
        {
            float noiseMix = spec.Noise;
            float g = velGain;
            for (int i = i0; i < count; i++)
            {
                float m = 0f;
                for (int k = 0; k < 6; k++)
                {
                    float ph = metalPhase[k] + metalInc[k];
                    if (ph >= 1f) ph -= 1f;
                    metalPhase[k] = ph;
                    m += ph < 0.5f ? 1f : -1f;
                }
                m *= 0.1667f;
                float src = m * (1f - noiseMix) + rng.NextBipolar() * noiseMix;
                float s = f2.HighPass(f1.BandPass(src) * 0.7f + src * 0.3f);
                if (attackEnv < 1f) { attackEnv += attackInc; if (attackEnv > 1f) attackEnv = 1f; }
                s *= amp * attackEnv * 3.2f;
                amp *= ampCoef;
                Out(left, right, i, s * g);
            }
        }

        void RenderTom(float[] left, float[] right, int i0, int count)
        {
            float baseHz = spec.Tune, sweep = spec.Sweep, noiseMix = spec.Noise;
            float g = velGain;
            for (int i = i0; i < count; i++)
            {
                float hz = baseHz * (1f + sweep * pitchEnv);
                pitchEnv *= pitchCoef;
                phase1 += hz / sr;
                if (phase1 >= 1f) phase1 -= 1f;
                float s = SynthMath.Sin01(phase1) * (1f - noiseMix) + f1.LowPass(rng.NextBipolar()) * noiseMix;
                s = SynthMath.SoftClip(s * amp * 1.3f);
                amp *= ampCoef;
                Out(left, right, i, s * g);
            }
        }

        void RenderRim(float[] left, float[] right, int i0, int count)
        {
            float hz = spec.Tune, noiseMix = spec.Noise;
            float g = velGain;
            for (int i = i0; i < count; i++)
            {
                phase1 += hz / sr;
                if (phase1 >= 1f) phase1 -= 1f;
                float tri = phase1 < 0.5f ? 4f * phase1 - 1f : 3f - 4f * phase1;
                float s = tri * (1f - noiseMix) + f1.BandPass(rng.NextBipolar()) * noiseMix * 2f;
                s *= amp;
                amp *= ampCoef;
                Out(left, right, i, s * g);
            }
        }

        void RenderShaker(float[] left, float[] right, int i0, int count)
        {
            float g = velGain;
            for (int i = i0; i < count; i++)
            {
                float s = f1.HighPass(rng.NextBipolar());
                if (attackEnv < 1f) { attackEnv += attackInc; if (attackEnv > 1f) attackEnv = 1f; }
                else amp *= ampCoef;
                Out(left, right, i, s * amp * attackEnv * g);
            }
        }
    }
}
