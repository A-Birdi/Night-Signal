using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Look-ahead peak limiter with a provable ceiling: the gain applied to each (delayed) sample is the box-filtered
    /// minimum of the required gains over the look-ahead window, so no output sample can exceed the ceiling.
    /// A final clamp at −0.3 dBFS guards against float rounding only. Allocation-free after construction.
    /// </summary>
    public sealed class Limiter
    {
        /// <summary>−0.3 dBFS: the absolute output bound of every synth path in the game.</summary>
        public const float HardCeiling = 0.96605f;

        readonly int look;
        readonly int delayLength;
        readonly float[] delayL;
        readonly float[] delayR;
        int delayPos;

        readonly float[] dqValue;
        readonly int[] dqIndex;
        readonly int dqMask;
        int dqHead, dqTail;

        readonly float[] box;
        double boxSum;
        int boxPos;

        readonly float releaseCoef;
        float released = 1f;
        float ceiling;
        int counter;
        float minGainSeen = 1f;

        public Limiter(int sampleRate, float ceilingDb = -1f, float lookaheadMs = 2f, float releaseMs = 90f)
        {
            look = Math.Max(4, (int)(sampleRate * lookaheadMs * 0.001f));
            delayLength = look - 1;
            delayL = new float[delayLength];
            delayR = new float[delayLength];
            int cap = 1;
            while (cap < look + 2) cap <<= 1;
            dqValue = new float[cap];
            dqIndex = new int[cap];
            dqMask = cap - 1;
            box = new float[look];
            for (int i = 0; i < look; i++) box[i] = 1f;
            boxSum = look;
            releaseCoef = MathF.Exp(-1f / (releaseMs * 0.001f * sampleRate));
            CeilingDb = ceilingDb;
        }

        public float CeilingDb
        {
            set
            {
                float g = SynthMath.DbToGain(value);
                ceiling = g > HardCeiling ? HardCeiling : g;
            }
        }

        /// <summary>Smallest gain applied since the last call to <see cref="TakeMinGain"/> (diagnostics).</summary>
        public float TakeMinGain()
        {
            float g = minGainSeen;
            minGainSeen = 1f;
            return g;
        }

        float NextGain(float peak)
        {
            float target = peak > ceiling ? ceiling / peak : 1f;
            while (dqTail != dqHead && dqValue[(dqTail - 1) & dqMask] >= target) dqTail--;
            dqValue[dqTail & dqMask] = target;
            dqIndex[dqTail & dqMask] = counter;
            dqTail++;
            while (unchecked(counter - dqIndex[dqHead & dqMask]) >= look) dqHead++;
            float minG = dqValue[dqHead & dqMask];
            counter = unchecked(counter + 1);

            released = minG < released ? minG : minG + (released - minG) * releaseCoef;

            boxSum += released - box[boxPos];
            box[boxPos] = released;
            if (++boxPos == look)
            {
                boxPos = 0;
                double s = 0.0; // periodic exact re-sum removes accumulated rounding drift
                for (int i = 0; i < look; i++) s += box[i];
                boxSum = s;
            }
            float g = (float)(boxSum / look);
            if (g < minGainSeen) minGainSeen = g;
            return g;
        }

        public void ProcessStereo(float[] left, float[] right, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float xl = left[i], xr = right[i];
                float al = xl < 0f ? -xl : xl;
                float ar = xr < 0f ? -xr : xr;
                float g = NextGain(al > ar ? al : ar);
                float yl = delayL[delayPos];
                float yr = delayR[delayPos];
                delayL[delayPos] = xl;
                delayR[delayPos] = xr;
                if (++delayPos == delayLength) delayPos = 0;
                yl *= g;
                yr *= g;
                left[i] = yl > HardCeiling ? HardCeiling : (yl < -HardCeiling ? -HardCeiling : yl);
                right[i] = yr > HardCeiling ? HardCeiling : (yr < -HardCeiling ? -HardCeiling : yr);
            }
        }

        public void ProcessMono(float[] mono, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float x = mono[i];
                float g = NextGain(x < 0f ? -x : x);
                float y = delayL[delayPos];
                delayL[delayPos] = x;
                if (++delayPos == delayLength) delayPos = 0;
                y *= g;
                mono[i] = y > HardCeiling ? HardCeiling : (y < -HardCeiling ? -HardCeiling : y);
            }
        }
    }
}
