using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Tyre and road sound: tonal squeal on paved surfaces scaled by slip, granular crunch on gravel/grass,
    /// rolling rumble and kerb rumble-strip buzz by speed, a continuous wall scrape, and speed-dependent wind
    /// (its own gain). Everything is filtered noise and simple oscillators; allocation-free.
    /// </summary>
    public sealed class RoadVoice
    {
        readonly float sr;
        Rng rng;

        float slipT, speedT, groundedT = 1f, kerbT, scrapeT;
        TyreSurface surface;
        float slipS, speedS, groundedS = 1f, kerbS, scrapeS, looseW, grassW, concreteW;
        float wobble, wobbleTarget, gust, gustTarget;
        int wobbleCounter, gustCounter;
        float squealPhase, kerbPhase;
        float crunchEnv;
        float squealLvl, crunchLvl, crunchRate, rollLvl, kerbLvl, windLvl, scrapeLvl, squealInc, kerbInc;
        Svf sq1, sq2, sq3, crunchLp, crunchBp, rollLp, kerbLp, windBp, windLp, scrapeBp;

        public RoadVoice(float sampleRate, uint seed)
        {
            sr = sampleRate;
            rng = new Rng(seed * 2246822519u + 374761393u);
        }

        public void SetInputs(float speedMps, float slip01, TyreSurface surf, float grounded01, float kerb01, float scrape01)
        {
            speedT = Math.Max(0f, speedMps);
            slipT = SynthMath.Clamp01(slip01);
            surface = surf;
            groundedT = SynthMath.Clamp01(grounded01);
            kerbT = SynthMath.Clamp01(kerb01);
            scrapeT = SynthMath.Clamp01(scrape01);
        }

        void Control(int count)
        {
            float bs = count / sr;
            float kFast = 1f - MathF.Exp(-bs / 0.03f);
            float kMed = 1f - MathF.Exp(-bs / 0.08f);
            float kSlow = 1f - MathF.Exp(-bs / 0.15f);
            slipS += (slipT - slipS) * kFast;
            speedS += (speedT - speedS) * kSlow;
            groundedS += (groundedT - groundedS) * kFast;
            kerbS += (kerbT - kerbS) * kFast;
            scrapeS += (scrapeT - scrapeS) * kFast;
            bool loose = surface == TyreSurface.Shoulder || surface == TyreSurface.Grass;
            looseW += ((loose ? 1f : 0f) - looseW) * kMed;
            grassW += ((surface == TyreSurface.Grass ? 1f : 0f) - grassW) * kMed;
            concreteW += ((surface == TyreSurface.Concrete ? 1f : 0f) - concreteW) * kMed;

            wobbleCounter -= count;
            if (wobbleCounter <= 0) { wobbleCounter = (int)(sr * (0.05f + 0.08f * rng.NextFloat())); wobbleTarget = rng.NextBipolar(); }
            wobble += (wobbleTarget - wobble) * kFast;
            gustCounter -= count;
            if (gustCounter <= 0) { gustCounter = (int)(sr * (0.6f + 1.4f * rng.NextFloat())); gustTarget = rng.NextBipolar(); }
            gust += (gustTarget - gust) * (1f - MathF.Exp(-bs / 0.5f));

            float speedF = SynthMath.Clamp01(speedS / 5f);
            squealLvl = SynthMath.SmoothStep(0.12f, 0.75f, slipS) * groundedS * speedF * (1f - looseW) * 0.32f;
            float f = 980f * (1f + 0.14f * slipS) * (1f + 0.05f * wobble) * (1f + 0.002f * Math.Min(speedS, 60f));
            sq1.Set(f * 0.82f, 0.94f, sr);
            sq2.Set(f * 1.23f, 0.92f, sr);
            sq3.Set(f * 1.96f, 0.88f, sr);
            squealInc = f * 1.02f / sr;

            crunchLvl = looseW * groundedS * (SynthMath.Clamp01(speedS / 18f) * 0.5f + slipS * 0.6f) * 0.5f;
            crunchRate = (30f + 11f * speedS) / sr;
            crunchLp.Set(grassW > 0.5f ? 1000f : 2700f, 0.1f, sr);
            crunchBp.Set(grassW > 0.5f ? 450f : 900f, 0.3f, sr);

            float rollF = SynthMath.Clamp01(speedS / 45f);
            rollLvl = groundedS * MathF.Sqrt(rollF) * 0.12f;
            rollLp.Set(260f + 280f * concreteW + 300f * looseW, 0.2f, sr);

            kerbLvl = kerbS * SynthMath.Clamp01(speedS / 8f) * 0.12f;
            kerbInc = SynthMath.Clamp(speedS / 0.55f, 6f, 95f) / sr;
            kerbLp.Set(240f, 0.3f, sr);

            float windF = SynthMath.Clamp01(speedS / 75f);
            windLvl = Math.Min(0.3f, windF * windF * 0.24f) * (1f + 0.25f * gust);
            windBp.Set(320f + speedS * 22f, 0.25f, sr);
            windLp.Set(900f + speedS * 45f, 0f, sr);

            scrapeLvl = scrapeS * SynthMath.Clamp01(speedS / 4f) * 0.16f;
            scrapeBp.Set(1700f + 25f * speedS, 0.45f, sr);
        }

        /// <summary>Adds tyre/road sound × <paramref name="tyreGain"/> and wind × <paramref name="windGain"/>.</summary>
        public void Render(float[] dst, int offset, int count, float tyreGain, float windGain)
        {
            Control(count);
            float sq = squealLvl * tyreGain, cr = crunchLvl * tyreGain, rl = rollLvl * tyreGain;
            float kb = kerbLvl * tyreGain, sc = scrapeLvl * tyreGain, wd = windLvl * windGain;
            float crunchDecay = SynthMath.DecayCoef(0.006f, sr);
            for (int i = 0; i < count; i++)
            {
                float n1 = rng.NextBipolar();
                float n2 = rng.NextBipolar();
                float s = 0f;
                if (sq > 1e-5f)
                {
                    squealPhase += squealInc * (1f + 0.01f * n2);
                    if (squealPhase >= 1f) squealPhase -= 1f;
                    float tonal = SynthMath.Sin01(squealPhase) * 0.35f;
                    s += (sq1.BandPass(n1) * 1.1f + sq2.BandPass(n1) * 0.8f + sq3.BandPass(n1) * 0.4f + tonal) * sq;
                }
                if (cr > 1e-5f)
                {
                    if (rng.NextFloat() < crunchRate) crunchEnv = 0.4f + 0.6f * rng.NextFloat();
                    float grain = n2 * crunchEnv;
                    crunchEnv *= crunchDecay;
                    s += (crunchLp.LowPass(grain) * 1.2f + crunchBp.BandPass(n1) * 0.35f) * cr;
                }
                if (rl > 1e-5f) s += rollLp.LowPass(n2) * rl * 2f;
                if (kb > 1e-5f)
                {
                    kerbPhase += kerbInc;
                    if (kerbPhase >= 1f) kerbPhase -= 1f;
                    float buzz = kerbPhase < 0.5f ? 1f : -1f;
                    s += kerbLp.LowPass(buzz + 0.3f * n1) * kb;
                }
                if (sc > 1e-5f) s += scrapeBp.BandPass(n1) * (0.6f + 0.4f * n2) * sc;
                if (wd > 1e-5f)
                {
                    float w = windLp.LowPass(windBp.BandPass(n2) * 0.7f + n1 * 0.3f);
                    s += w * wd;
                }
                dst[offset + i] += s;
            }
        }
    }
}
