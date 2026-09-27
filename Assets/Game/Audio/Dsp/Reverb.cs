namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Schroeder/Moorer reverb in the public-domain Freeverb topology (8 damped combs + 4 allpasses per side,
    /// stereo-spread). Buffers are allocated once for the sample rate; <see cref="Process"/> is allocation-free
    /// and processes each comb over the whole block so its state stays in registers.
    /// </summary>
    public sealed class Reverb
    {
        static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        static readonly int[] AllpassTuning = { 556, 441, 341, 225 };
        const int StereoSpread = 23;
        const float FixedGain = 0.015f;

        readonly float[][] comb = new float[16][];
        readonly int[] combPos = new int[16];
        readonly float[] combStore = new float[16];
        readonly float[][] allpass = new float[8][];
        readonly int[] allpassPos = new int[8];
        readonly float[] input;
        readonly float[] wetL;
        readonly float[] wetR;
        readonly int maxBlock;

        float feedback = 0.84f, damp1 = 0.2f, damp2 = 0.8f, wet1 = 0.3f, wet2 = 0f;

        public Reverb(int sampleRate, int maxBlock = 256)
        {
            double s = sampleRate / 44100.0;
            for (int i = 0; i < 8; i++)
            {
                comb[i] = new float[System.Math.Max(8, (int)(CombTuning[i] * s))];
                comb[8 + i] = new float[System.Math.Max(8, (int)((CombTuning[i] + StereoSpread) * s))];
            }
            for (int i = 0; i < 4; i++)
            {
                allpass[i] = new float[System.Math.Max(8, (int)(AllpassTuning[i] * s))];
                allpass[4 + i] = new float[System.Math.Max(8, (int)((AllpassTuning[i] + StereoSpread) * s))];
            }
            this.maxBlock = maxBlock;
            input = new float[maxBlock];
            wetL = new float[maxBlock];
            wetR = new float[maxBlock];
            Set(0.7f, 0.4f, 1f, 0.25f);
        }

        /// <param name="roomSize">0..1 (tail length).</param>
        /// <param name="damping">0..1 (high-frequency absorption).</param>
        /// <param name="width">0 mono .. 1 full stereo.</param>
        /// <param name="wet">Output level of the wet signal, 0..1.</param>
        public void Set(float roomSize, float damping, float width, float wet)
        {
            feedback = 0.7f + 0.28f * SynthMath.Clamp01(roomSize);
            damp1 = 0.4f * SynthMath.Clamp01(damping);
            damp2 = 1f - damp1;
            float w = 3f * SynthMath.Clamp01(wet);
            float wd = SynthMath.Clamp01(width);
            wet1 = w * (wd * 0.5f + 0.5f);
            wet2 = w * ((1f - wd) * 0.5f);
        }

        /// <summary>Adds the reverberated (inL + inR) into outL/outR. <paramref name="count"/> ≤ maxBlock.</summary>
        public void Process(float[] inL, float[] inR, float[] outL, float[] outR, int count)
        {
            if (count > maxBlock) count = maxBlock;
            for (int i = 0; i < count; i++)
            {
                input[i] = (inL[i] + inR[i]) * FixedGain + 1e-18f;
                wetL[i] = 0f;
                wetR[i] = 0f;
            }

            float fb = feedback, d1 = damp1, d2 = damp2;
            for (int c = 0; c < 16; c++)
            {
                float[] buf = comb[c];
                float[] dst = c < 8 ? wetL : wetR;
                int pos = combPos[c];
                int len = buf.Length;
                float store = combStore[c];
                for (int i = 0; i < count; i++)
                {
                    float o = buf[pos];
                    store = o * d2 + store * d1;
                    buf[pos] = input[i] + store * fb;
                    if (++pos >= len) pos = 0;
                    dst[i] += o;
                }
                combPos[c] = pos;
                combStore[c] = store;
            }

            for (int a = 0; a < 8; a++)
            {
                float[] buf = allpass[a];
                float[] dst = a < 4 ? wetL : wetR;
                int pos = allpassPos[a];
                int len = buf.Length;
                for (int i = 0; i < count; i++)
                {
                    float x = dst[i];
                    float b = buf[pos];
                    buf[pos] = x + b * 0.5f;
                    if (++pos >= len) pos = 0;
                    dst[i] = b - x;
                }
                allpassPos[a] = pos;
            }

            float w1 = wet1, w2 = wet2;
            for (int i = 0; i < count; i++)
            {
                float l = wetL[i], r = wetR[i];
                outL[i] += l * w1 + r * w2;
                outR[i] += r * w1 + l * w2;
            }
        }

        public void Clear()
        {
            for (int c = 0; c < 16; c++) { System.Array.Clear(comb[c], 0, comb[c].Length); combStore[c] = 0f; }
            for (int a = 0; a < 8; a++) System.Array.Clear(allpass[a], 0, allpass[a].Length);
        }
    }
}
