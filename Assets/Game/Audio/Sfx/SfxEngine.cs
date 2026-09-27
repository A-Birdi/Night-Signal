using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// UI sound bus: a pool of one-shot voices playing <see cref="UiCueLibrary"/> designs through a small room
    /// reverb and a limiter. The game thread calls <see cref="Play"/>; the audio thread calls <see cref="Render"/>.
    /// Lock-free, allocation-free after construction.
    /// </summary>
    public sealed class SfxEngine
    {
        const int Voices = 24;
        const int Block = 64;

        struct Request
        {
            public UiCue Cue;
            public float Gain;
        }

        sealed class Voice
        {
            public bool Active;
            public int Delay;
            public StrikeKind Kind;
            public float Inc, ModInc;
            public float Phase, ModPhase, Phase2, Phase3;
            public float Env, EnvCoef, Attack, AttackInc;
            public float Gain, PanL, PanR;
            public Svf Filter;
            public float Index;
        }

        readonly int sampleRate;
        readonly Voice[] voices = new Voice[Voices];
        readonly SpscRing<Request> requests = new SpscRing<Request>(64);
        readonly Reverb reverb;
        readonly Limiter limiter;
        readonly float[] l = new float[Block], r = new float[Block];
        readonly float[] sendL = new float[Block], sendR = new float[Block];
        int pos = Block;
        Rng rng = new Rng(0xC0FFEEu);
        float volume = 1f;

        public SfxEngine(int sampleRate)
        {
            SynthMath.Warm();
            this.sampleRate = sampleRate;
            for (int i = 0; i < Voices; i++) voices[i] = new Voice();
            reverb = new Reverb(sampleRate, Block);
            reverb.Set(0.45f, 0.55f, 0.8f, 0.14f);
            limiter = new Limiter(sampleRate, -1f, 1.5f, 80f);
        }

        /// <summary>UI bus volume 0..1.</summary>
        public float Volume
        {
            get => volume;
            set => volume = SynthMath.Clamp01(value);
        }

        public bool Play(UiCue cue, float gain = 1f) => requests.TryEnqueue(new Request { Cue = cue, Gain = gain });

        void Start(UiCue cue, float gain)
        {
            var strikes = UiCueLibrary.Get(cue);
            if (strikes == null) return;
            for (int s = 0; s < strikes.Length; s++)
            {
                var st = strikes[s];
                Voice v = null;
                for (int i = 0; i < Voices; i++) if (!voices[i].Active) { v = voices[i]; break; }
                if (v == null)
                {
                    float quiet = float.MaxValue;
                    for (int i = 0; i < Voices; i++) if (voices[i].Env < quiet) { quiet = voices[i].Env; v = voices[i]; }
                }
                float hz = SynthMath.MidiToHz(st.Note);
                v.Active = true;
                v.Delay = (int)(st.Delay * sampleRate);
                v.Kind = st.Kind;
                v.Inc = hz / sampleRate;
                v.ModInc = hz * 3.5f / sampleRate;
                v.Phase = 0f; v.ModPhase = 0f; v.Phase2 = rng.NextFloat(); v.Phase3 = rng.NextFloat();
                v.Env = 1f;
                v.EnvCoef = SynthMath.DecayCoef(Math.Max(0.01f, st.Duration), sampleRate);
                v.Attack = 0f;
                v.AttackInc = 1f / ((st.Kind == StrikeKind.Pad ? 0.06f : (st.Kind == StrikeKind.Tick ? 0.0005f : 0.003f)) * sampleRate);
                v.Gain = SynthMath.DbToGain(st.GainDb) * gain;
                SynthMath.PanGains(st.Pan, out v.PanL, out v.PanR);
                v.Filter.Reset();
                v.Filter.Set(st.Kind == StrikeKind.Beep ? 3200f : (st.Kind == StrikeKind.Pad ? 1800f : 5000f), 0.1f, sampleRate);
                v.Index = st.Kind == StrikeKind.Bell ? 1.6f : 0f;
            }
        }

        void Fill()
        {
            Request q;
            while (requests.TryDequeue(out q)) Start(q.Cue, q.Gain);
            Array.Clear(l, 0, Block);
            Array.Clear(r, 0, Block);
            for (int k = 0; k < Voices; k++)
            {
                var v = voices[k];
                if (!v.Active) continue;
                for (int i = 0; i < Block; i++)
                {
                    if (v.Delay > 0) { v.Delay--; continue; }
                    if (v.Attack < 1f) { v.Attack += v.AttackInc; if (v.Attack > 1f) v.Attack = 1f; }
                    float s;
                    switch (v.Kind)
                    {
                        case StrikeKind.Bell:
                        {
                            float idx = v.Index * v.Env; // brightness fades with the note
                            float m = SynthMath.Sin01(v.ModPhase);
                            float cp = v.Phase + idx * m * 0.159154943f + 4f;
                            cp -= (int)cp;
                            s = SynthMath.Sin01(cp) * 0.8f + SynthMath.Sin01(v.Phase) * 0.2f;
                            v.ModPhase += v.ModInc; if (v.ModPhase >= 1f) v.ModPhase -= 1f;
                            break;
                        }
                        case StrikeKind.Soft:
                        {
                            float p2 = v.Phase * 2f; p2 -= (int)p2;
                            s = SynthMath.Sin01(v.Phase) + 0.15f * SynthMath.Sin01(p2);
                            break;
                        }
                        case StrikeKind.Beep:
                            s = v.Filter.LowPass((v.Phase < 0.5f ? 0.5f : -0.5f) + 0.5f * SynthMath.Sin01(v.Phase));
                            break;
                        case StrikeKind.Tick:
                            s = v.Filter.BandPass(SynthMath.Sin01(v.Phase) + 0.3f * rng.NextBipolar());
                            break;
                        default:
                        {
                            float a = v.Phase, b = v.Phase2, c = v.Phase3;
                            s = v.Filter.LowPass((2f * a - 1f) + (2f * b - 1f) + (2f * c - 1f)) * 0.3f;
                            v.Phase2 += v.Inc * 1.004f; if (v.Phase2 >= 1f) v.Phase2 -= 1f;
                            v.Phase3 += v.Inc * 0.996f; if (v.Phase3 >= 1f) v.Phase3 -= 1f;
                            break;
                        }
                    }
                    v.Phase += v.Inc; if (v.Phase >= 1f) v.Phase -= 1f;
                    float e = v.Env * v.Attack * v.Gain;
                    v.Env *= v.EnvCoef;
                    l[i] += s * e * v.PanL;
                    r[i] += s * e * v.PanR;
                    if (v.Env < 1e-4f) { v.Active = false; break; }
                }
            }
            Array.Clear(sendL, 0, Block);
            Array.Clear(sendR, 0, Block);
            reverb.Process(l, r, sendL, sendR, Block);
            for (int i = 0; i < Block; i++) { l[i] += sendL[i]; r[i] += sendR[i]; }
            limiter.ProcessStereo(l, r, Block);
        }

        /// <summary>Overwrites interleaved <paramref name="data"/> with the UI bus.</summary>
        public void Render(float[] data, int frames, int channels)
        {
            float vol = volume;
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                if (pos >= Block) { Fill(); pos = 0; }
                float a = l[pos] * vol, b = r[pos] * vol;
                pos++;
                if (channels == 1) data[idx++] = 0.5f * (a + b);
                else
                {
                    data[idx++] = a;
                    data[idx++] = b;
                    for (int c = 2; c < channels; c++) data[idx++] = 0f;
                }
            }
        }
    }
}
