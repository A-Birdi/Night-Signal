using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// One-shot impacts: suspension/kerb thumps and collisions whose loudness, body weight, metallic ring and
    /// debris all scale with severity (0..1). Six overlapping slots; allocation-free.
    /// </summary>
    public sealed class ImpactBank
    {
        const int Slots = 6;
        readonly float sr;
        Rng rng;
        readonly bool[] active = new bool[Slots];
        readonly bool[] isThump = new bool[Slots];
        readonly float[] level = new float[Slots];
        readonly float[] bodyPhase = new float[Slots];
        readonly float[] bodyHz = new float[Slots];
        readonly float[] bodyEnv = new float[Slots];
        readonly float[] bodyCoef = new float[Slots];
        readonly float[] bodyDrop = new float[Slots];
        readonly float[] noiseEnv = new float[Slots];
        readonly float[] noiseCoef = new float[Slots];
        readonly float[] debrisTime = new float[Slots];
        readonly float[] debrisEnv = new float[Slots];
        readonly int[] life = new int[Slots];
        readonly Svf[] ring1 = new Svf[Slots];
        readonly Svf[] ring2 = new Svf[Slots];
        readonly Svf[] ring3 = new Svf[Slots];
        readonly Svf[] noiseLp = new Svf[Slots];
        int next;

        public ImpactBank(float sampleRate, uint seed)
        {
            sr = sampleRate;
            rng = new Rng(seed * 3266489917u + 668265263u);
        }

        int Slot()
        {
            for (int i = 0; i < Slots; i++) if (!active[i]) return i;
            int s = next;
            next = (next + 1) % Slots;
            return s;
        }

        /// <summary>Suspension/kerb thump, amount 0..1.</summary>
        public void Thump(float amount)
        {
            amount = SynthMath.Clamp01(amount);
            if (amount < 0.02f) return;
            int k = Slot();
            active[k] = true;
            isThump[k] = true;
            level[k] = 0.55f * amount;
            bodyPhase[k] = 0f;
            bodyHz[k] = 72f + 20f * rng.NextFloat();
            bodyEnv[k] = 1f;
            bodyCoef[k] = SynthMath.DecayCoef(0.14f, sr);
            bodyDrop[k] = SynthMath.DecayCoef(0.08f, sr);
            noiseEnv[k] = 0.6f;
            noiseCoef[k] = SynthMath.DecayCoef(0.05f, sr);
            noiseLp[k].Reset();
            noiseLp[k].Set(420f, 0.1f, sr);
            debrisTime[k] = 0f;
            life[k] = (int)(0.3f * sr);
        }

        /// <summary>Collision, severity 0..1 (light scrape-tap .. heavy crash).</summary>
        public void Collide(float severity)
        {
            float s = SynthMath.Clamp01(severity);
            if (s < 0.02f) return;
            int k = Slot();
            active[k] = true;
            isThump[k] = false;
            level[k] = 0.25f + 0.75f * MathF.Pow(s, 0.7f);
            bodyPhase[k] = 0f;
            bodyHz[k] = 44f + 16f * (1f - s) + 6f * rng.NextFloat();
            bodyEnv[k] = s;
            bodyCoef[k] = SynthMath.DecayCoef(0.12f + 0.3f * s, sr);
            bodyDrop[k] = SynthMath.DecayCoef(0.1f, sr);
            noiseEnv[k] = 1f;
            noiseCoef[k] = SynthMath.DecayCoef(0.05f + 0.35f * s, sr);
            float spread = 0.9f + 0.2f * rng.NextFloat();
            ring1[k].Reset(); ring2[k].Reset(); ring3[k].Reset(); noiseLp[k].Reset();
            ring1[k].Set(640f * spread, 0.995f, sr);
            ring2[k].Set(1470f * spread, 0.993f, sr);
            ring3[k].Set(3150f * spread, 0.99f, sr);
            noiseLp[k].Set(2500f + 4000f * s, 0.05f, sr);
            debrisTime[k] = s > 0.55f ? 0.35f + 0.4f * s : 0f;
            debrisEnv[k] = 0f;
            life[k] = (int)((0.5f + 0.6f * s) * sr);
        }

        public void Render(float[] dst, int offset, int count, float gain)
        {
            float debrisDecay = SynthMath.DecayCoef(0.01f, sr);
            float dt = 1f / sr;
            for (int k = 0; k < Slots; k++)
            {
                if (!active[k]) continue;
                float lv = level[k] * gain;
                float bh = bodyHz[k], be = bodyEnv[k], bc = bodyCoef[k], bp = bodyPhase[k];
                float ne = noiseEnv[k], nc = noiseCoef[k];
                float drop = 1f;
                for (int i = 0; i < count; i++)
                {
                    drop = 0.7f + 0.3f * be;
                    bp += bh * drop / sr;
                    if (bp >= 1f) bp -= 1f;
                    float body = SynthMath.Sin01(bp) * be;
                    be *= bc;
                    float n = rng.NextBipolar();
                    float burst = noiseLp[k].LowPass(n) * ne;
                    ne *= nc;
                    float s;
                    if (isThump[k]) s = body * 0.9f + burst * 0.5f;
                    else
                    {
                        float ring = ring1[k].BandPass(burst) * 1.5f + ring2[k].BandPass(burst) * 1.1f + ring3[k].BandPass(burst) * 0.7f;
                        s = body * 0.8f + burst * 0.55f + ring * 1.4f;
                        if (debrisTime[k] > 0f)
                        {
                            debrisTime[k] -= dt;
                            if (rng.NextFloat() < 90f / sr) debrisEnv[k] = 0.2f + 0.3f * rng.NextFloat();
                            s += n * debrisEnv[k] * 0.35f;
                            debrisEnv[k] *= debrisDecay;
                        }
                    }
                    dst[offset + i] += s * lv;
                }
                bodyEnv[k] = be;
                bodyPhase[k] = bp;
                noiseEnv[k] = ne;
                life[k] -= count;
                if (life[k] <= 0 && be < 1e-4f && ne < 1e-4f && debrisTime[k] <= 0f)
                {
                    active[k] = false;
                    ring1[k].Flush(); ring2[k].Flush(); ring3[k].Flush();
                }
            }
        }
    }
}
