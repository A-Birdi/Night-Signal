using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Ping-pong feedback delay with damped repeats. The mono sum enters the left line; each repeat crosses to
    /// the other side. Buffer is allocated once (maximum delay); processing is allocation-free.
    /// </summary>
    public sealed class StereoDelay
    {
        readonly float[] bufL;
        readonly float[] bufR;
        readonly int length;
        readonly float sampleRate;
        int write;
        int delay;
        float feedback;
        OnePole dampL, dampR;

        public StereoDelay(int sampleRate, float maxSeconds = 1.25f)
        {
            this.sampleRate = sampleRate;
            length = Math.Max(16, (int)(sampleRate * maxSeconds) + 1);
            bufL = new float[length];
            bufR = new float[length];
            Set(0.375f, 0.35f, 4000f);
        }

        public void Set(float seconds, float feedbackAmount, float dampHz)
        {
            int d = (int)(seconds * sampleRate);
            delay = d < 1 ? 1 : (d > length - 1 ? length - 1 : d);
            feedback = SynthMath.Clamp(feedbackAmount, 0f, 0.92f);
            dampL.SetHz(dampHz, sampleRate);
            dampR.SetHz(dampHz, sampleRate);
        }

        /// <summary>Adds wet echoes of (inL + inR)/2 into outL/outR.</summary>
        public void Process(float[] inL, float[] inR, float[] outL, float[] outR, int count)
        {
            int w = write, len = length, d = delay;
            float fb = feedback;
            for (int i = 0; i < count; i++)
            {
                int r = w - d;
                if (r < 0) r += len;
                float yl = bufL[r];
                float yr = bufR[r];
                float x = (inL[i] + inR[i]) * 0.5f;
                bufL[w] = x + dampR.LowPass(yr) * fb + 1e-18f;
                bufR[w] = dampL.LowPass(yl) * fb;
                if (++w >= len) w = 0;
                outL[i] += yl;
                outR[i] += yr;
            }
            write = w;
        }

        public void Clear()
        {
            Array.Clear(bufL, 0, length);
            Array.Clear(bufR, 0, length);
        }
    }
}
