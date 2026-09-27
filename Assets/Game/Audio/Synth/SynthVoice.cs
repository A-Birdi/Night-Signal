using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// One polyphonic voice of a melodic instrument. Pitch, LFO and filter coefficients update once per render
    /// block (control rate); oscillators, envelopes and filtering run per sample. Allocation-free after
    /// construction. Retriggering or stealing never resets oscillator phase or envelope level, so it is click-free.
    /// </summary>
    public sealed class SynthVoice
    {
        readonly InstrumentPreset p;
        readonly float sr;
        readonly float gain;
        readonly float panL, panR;

        Adsr amp, fenv;
        Oscillator o1, o2, sub;
        readonly Oscillator[] saws;
        readonly float[] sawRatio;
        readonly bool[] sawLeft;
        Svf filterL, filterR;
        Rng rng;

        // Karplus-Strong state
        readonly float[] ks;
        int ksLen, ksPos;
        float ksFeedback, ksApCoef, ksApX1, ksApY1, ksPrev;

        float targetNote, currentNote, velocity, velGain;
        float glideCoef;
        int holdSamples;
        int startDelay;
        float lfoPhase, lfoTime;
        float modPrev;
        float carPhase, modPhase;

        public int Age;
        public bool IsActive => amp.IsActive;
        public bool IsReleasing => amp.IsReleasing;
        public float Level => amp.Level;
        public float Note => targetNote;

        public SynthVoice(InstrumentPreset preset, float sampleRate, uint seed, int index)
        {
            p = preset;
            sr = sampleRate;
            gain = preset.Gain;
            rng = new Rng(seed * 2654435761u + (uint)index * 40503u + 1u);
            float pan = 0f;
            if (preset.Spread > 0f)
                pan = preset.Spread * ((index & 1) == 0 ? -1f : 1f) * (0.5f + 0.5f * ((index >> 1) & 1));
            SynthMath.PanGains(pan, out panL, out panR);
            amp.Configure(p.Amp.A, p.Amp.D, p.Amp.S, p.Amp.R, sr);
            fenv.Configure(p.FilterEnv.A, p.FilterEnv.D, p.FilterEnv.S, p.FilterEnv.R, sr);

            o1.Phase = rng.NextFloat();
            o2.Phase = rng.NextFloat();
            sub.Phase = 0f;

            if (p.Kind == VoiceKind.Supersaw)
            {
                int n = Math.Max(1, Math.Min(InstrumentPreset.MaxSuperVoices, p.SuperVoices));
                saws = new Oscillator[n];
                sawRatio = new float[n];
                sawLeft = new bool[n];
                float half = (n - 1) * 0.5f;
                for (int i = 0; i < n; i++)
                {
                    float off = half > 0f ? (i - half) / half : 0f;
                    // Slightly non-linear spread (outer voices further out), max ±100 cents × detune.
                    float cents = p.SuperDetune * 100f * off * (0.6f + 0.4f * Math.Abs(off));
                    sawRatio[i] = MathF.Pow(2f, cents / 1200f);
                    saws[i].Phase = rng.NextFloat();
                    sawLeft[i] = (i & 1) == 0;
                }
            }
            if (p.Kind == VoiceKind.Pluck)
                ks = new float[(int)(sr / 25f) + 4];
        }

        /// <param name="holdSamplesFromOffset">Samples from the note start until note-off.</param>
        /// <param name="offset">Start position within the next rendered block.</param>
        public void NoteOn(float note, float vel, int holdSamplesFromOffset, int offset, bool glide)
        {
            bool wasActive = amp.IsActive && amp.Level > 0.001f;
            targetNote = note;
            // Slide only from a sounding note: explicit '~' in the score, or legato overlap on a mono glide preset.
            bool doGlide = wasActive && (glide || (p.Mono && p.Glide > 0f && !amp.IsReleasing));
            if (!doGlide) currentNote = note;
            velocity = vel;
            velGain = 1f - p.VelocitySens + p.VelocitySens * vel;
            holdSamples = Math.Max(1, holdSamplesFromOffset);
            startDelay = offset;
            float glideTime = doGlide ? Math.Max(p.Glide, 0.03f) : 0f;
            glideCoef = glideTime > 0f ? SynthMath.SmoothCoef(glideTime, sr) : 0f;
            if (!wasActive)
            {
                lfoTime = 0f;
                if (p.ResetPhase) { o1.Phase = 0f; o2.Phase = 0f; sub.Phase = 0f; carPhase = 0f; modPhase = 0f; }
                filterL.Reset();
                filterR.Reset();
            }
            if (p.Kind == VoiceKind.Pluck) Excite(note, vel);
            amp.Trigger();
            fenv.Trigger();
            Age = 0;
        }

        public void Release()
        {
            amp.Release();
            fenv.Release();
            holdSamples = 0;
        }

        public void Kill()
        {
            amp.Kill();
            fenv.Kill();
        }

        void Excite(float note, float vel)
        {
            float period = sr / SynthMath.MidiToHz(note);
            float d = period - 0.5f;
            int n = (int)d;
            float frac = d - n;
            if (frac < 0.1f) { n -= 1; frac += 1f; }
            if (n < 2) n = 2;
            if (n > ks.Length - 1) n = ks.Length - 1;
            ksLen = n;
            ksPos = 0;
            ksApCoef = (1f - frac) / (1f + frac);
            ksApX1 = 0f;
            ksApY1 = 0f;
            ksPrev = 0f;
            // -60 dB after PluckDecay seconds: per-period loss.
            ksFeedback = MathF.Pow(10f, -3f * period / (Math.Max(0.05f, p.PluckDecay) * sr));
            float bright = SynthMath.Clamp(p.PluckBright * (0.6f + 0.4f * vel), 0.02f, 1f);
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                lp += bright * (rng.NextBipolar() - lp);
                ks[i] = lp;
            }
            // Remove DC from the excitation so the string settles to silence.
            float mean = 0f;
            for (int i = 0; i < n; i++) mean += ks[i];
            mean /= n;
            float norm = 0f;
            for (int i = 0; i < n; i++) { ks[i] -= mean; norm = Math.Max(norm, Math.Abs(ks[i])); }
            if (norm > 1e-6f)
            {
                float s = 0.9f / norm;
                for (int i = 0; i < n; i++) ks[i] *= s;
            }
        }

        public void Render(float[] left, float[] right, int count)
        {
            if (!amp.IsActive) return;
            int i0 = 0;
            if (startDelay > 0)
            {
                if (startDelay >= count) { startDelay -= count; return; }
                i0 = startDelay;
                startDelay = 0;
            }
            Age += count;
            float blockSeconds = count / sr;

            // Control rate: glide, LFO, pitch, filter cutoff.
            if (glideCoef > 0f)
            {
                float c = MathF.Pow(glideCoef, count);
                currentNote = targetNote + (currentNote - targetNote) * c;
            }
            else currentNote = targetNote;

            float lfo = 0f;
            if (p.LfoPitchCents != 0f || p.LfoCutoffOct != 0f || p.LfoAmp != 0f || p.LfoPwm != 0f)
            {
                lfoPhase += p.LfoRate * blockSeconds;
                if (lfoPhase >= 1f) lfoPhase -= (float)Math.Floor(lfoPhase);
                float fade = p.LfoDelay > 0f ? SynthMath.Clamp01((lfoTime - p.LfoDelay * 0.5f) / (p.LfoDelay * 0.5f + 1e-4f)) : 1f;
                lfo = SynthMath.Sin01(lfoPhase) * fade;
            }
            lfoTime += blockSeconds;

            float pitch = currentNote + lfo * p.LfoPitchCents * 0.01f;
            float hz = SynthMath.MidiToHz(pitch);
            float ampLfo = 1f - p.LfoAmp * (0.5f + 0.5f * lfo);

            if (p.Filter != FilterMode.Off)
            {
                float oct = fenv.Level * p.FilterEnvOct + p.KeyTrack * (pitch - 60f) / 12f
                            + p.VelocityOct * (velocity - 0.7f) + lfo * p.LfoCutoffOct;
                float cutoff = p.Cutoff * SynthMath.Exp2(oct);
                filterL.Set(cutoff, p.Resonance, sr);
                if (p.Kind == VoiceKind.Supersaw) filterR.Set(cutoff, p.Resonance, sr);
            }

            float g = gain * velGain;
            switch (p.Kind)
            {
                case VoiceKind.Supersaw: RenderSupersaw(left, right, i0, count, hz, g * ampLfo); break;
                case VoiceKind.Fm: RenderFm(left, right, i0, count, hz, g * ampLfo); break;
                case VoiceKind.Pluck: RenderPluck(left, right, i0, count, g * ampLfo); break;
                default: RenderSubtractive(left, right, i0, count, hz, g * ampLfo, lfo); break;
            }

            if (!amp.IsActive)
            {
                filterL.Reset();
                filterR.Reset();
            }
        }

        bool TickHold()
        {
            if (holdSamples > 0 && --holdSamples == 0)
            {
                amp.Release();
                fenv.Release();
            }
            return amp.IsActive;
        }

        void RenderSubtractive(float[] left, float[] right, int i0, int count, float hz, float g, float lfo)
        {
            o1.SetFrequency(hz, sr);
            o2.SetFrequency(hz * SynthMath.Exp2((p.Osc2Semitones + p.Osc2DetuneCents * 0.01f) / 12f), sr);
            sub.SetFrequency(hz * 0.5f, sr);
            float pw = SynthMath.Clamp(p.PulseWidth + p.LfoPwm * lfo, 0.05f, 0.95f);
            float mix2 = p.Osc2Mix, subMix = p.SubMix, noise = p.NoiseMix;
            float norm = 1f / (1f + mix2 + subMix * 0.7f + noise * 0.5f);
            float drive = p.Drive;
            float driveIn = 1f + drive * 4f;
            float driveNorm = drive > 0f ? 1f / SynthMath.SoftClip(driveIn) : 1f;
            FilterMode mode = p.Filter;
            for (int i = i0; i < count; i++)
            {
                if (!TickHold() && amp.Level <= 0f) break;
                float a = amp.Next();
                fenv.Next();
                float s = o1.Next(p.Osc1, pw, ref rng);
                if (mix2 > 0f) s += mix2 * o2.Next(p.Osc2, pw, ref rng);
                if (subMix > 0f) s += subMix * 0.7f * sub.Next(Waveform.Square, 0.5f, ref rng);
                if (noise > 0f) s += noise * rng.NextBipolar() * 0.5f;
                s *= norm;
                if (drive > 0f) s = SynthMath.SoftClip(s * driveIn) * driveNorm;
                if (mode != FilterMode.Off) s = filterL.Process(s, mode);
                s *= a * g;
                left[i] += s * panL;
                right[i] += s * panR;
            }
        }

        void RenderSupersaw(float[] left, float[] right, int i0, int count, float hz, float g)
        {
            int n = saws.Length;
            for (int k = 0; k < n; k++) saws[k].SetFrequency(hz * sawRatio[k], sr);
            float side = p.SuperSide;
            float centreGain = 1f - side * 0.35f;
            float norm = 1f / MathF.Sqrt(1f + side * side * (n - 1));
            float w = SynthMath.Clamp01(p.Width);
            float near = 0.5f + 0.5f * w, far = 0.5f - 0.5f * w;
            int centre = n / 2;
            FilterMode mode = p.Filter;
            for (int i = i0; i < count; i++)
            {
                if (!TickHold() && amp.Level <= 0f) break;
                float a = amp.Next();
                fenv.Next();
                float c = 0f, l = 0f, r = 0f;
                for (int k = 0; k < n; k++)
                {
                    float v = saws[k].NextSaw();
                    if (k == centre) c += v;
                    else if (sawLeft[k]) l += v;
                    else r += v;
                }
                float sl = (c * centreGain + side * (l * near + r * far)) * norm;
                float srr = (c * centreGain + side * (r * near + l * far)) * norm;
                if (mode != FilterMode.Off)
                {
                    sl = filterL.Process(sl, mode);
                    srr = filterR.Process(srr, mode);
                }
                float ga = a * g;
                left[i] += sl * ga;
                right[i] += srr * ga;
            }
        }

        void RenderFm(float[] left, float[] right, int i0, int count, float hz, float g)
        {
            float carInc = hz * p.FmCarrierRatio / sr;
            float modInc = hz * p.FmRatio / sr;
            float fb = p.FmFeedback;
            float indexEnvAmt = p.FmIndexEnv;
            FilterMode mode = p.Filter;
            for (int i = i0; i < count; i++)
            {
                if (!TickHold() && amp.Level <= 0f) break;
                float a = amp.Next();
                float fe = fenv.Next();
                float index = p.FmIndex * (1f - indexEnvAmt + indexEnvAmt * fe);
                float mp = modPhase + fb * modPrev + 8f;
                mp -= (int)mp;
                float m = SynthMath.Sin01(mp);
                modPrev = m;
                float cp = carPhase + index * m * 0.159154943f + 8f; // index in radians -> cycles
                cp -= (int)cp;
                float s = SynthMath.Sin01(cp);
                modPhase += modInc;
                if (modPhase >= 1f) modPhase -= 1f;
                carPhase += carInc;
                if (carPhase >= 1f) carPhase -= 1f;
                if (mode != FilterMode.Off) s = filterL.Process(s, mode);
                s *= a * g;
                left[i] += s * panL;
                right[i] += s * panR;
            }
        }

        void RenderPluck(float[] left, float[] right, int i0, int count, float g)
        {
            int len = ksLen;
            if (len < 2) return;
            float fbk = ksFeedback, c = ksApCoef;
            FilterMode mode = p.Filter;
            float drive = p.Drive;
            float driveIn = 1f + drive * 6f;
            float driveNorm = drive > 0f ? 1f / SynthMath.SoftClip(driveIn) : 1f;
            for (int i = i0; i < count; i++)
            {
                if (!TickHold() && amp.Level <= 0f) break;
                float a = amp.Next();
                fenv.Next();
                float cur = ks[ksPos];
                float avg = 0.5f * (cur + ksPrev);
                ksPrev = cur;
                // First-order allpass supplies the fractional part of the loop delay (tuning).
                float y = c * avg + ksApX1 - c * ksApY1;
                ksApX1 = avg;
                ksApY1 = y;
                ks[ksPos] = y * fbk;
                if (++ksPos >= len) ksPos = 0;
                float s = cur;
                if (drive > 0f) s = SynthMath.SoftClip(s * driveIn) * driveNorm;
                if (mode != FilterMode.Off) s = filterL.Process(s, mode);
                s *= a * g;
                left[i] += s * panL;
                right[i] += s * panR;
            }
        }
    }
}
