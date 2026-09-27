using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Firing-frequency engine synthesis driven by rpm/throttle/load/boost. Three layers, all locked to one
    /// crank-cycle phase so pitch follows rpm without drift: (1) 24 additive half-order partials (Chebyshev
    /// recurrence, one table lookup per sample), shaped by the family profile and blended between idle, load
    /// and coast bands; (2) a raised-cosine exhaust pulse per combustion event with per-event amplitude jitter
    /// (roughness) and pulse-gated noise (rasp); (3) two exhaust formant resonators and a load-dependent tone
    /// filter. On top: intake roar, turbo whistle, flutter/blow-off, shift cut and clack, rev-limiter cut and
    /// overrun crackle. Allocation-free after construction.
    /// </summary>
    public sealed class EngineVoice
    {
        const int H = EngineProfile.Partials;
        /// <summary>Headroom: full-load peaks sit around -6 dBFS so the bus limiter rarely engages.</summary>
        const float OutputTrim = 0.5f;

        readonly EngineConfig cfg;
        readonly EngineProfile prof;
        readonly float sr;
        Rng rng;

        readonly float[] phaseCos = new float[H + 1];
        readonly float[] phaseSin = new float[H + 1];
        readonly float[] log2n = new float[H + 1];
        readonly float[] ca = new float[H + 1];
        readonly float[] cb = new float[H + 1];
        readonly float formantScale;

        // Targets (control inputs) and smoothed state.
        float rpmTarget, throttleTarget, loadTarget, boostTarget;
        int gear = 1;
        bool shifting;
        float rpmS, throttleS, loadS, boostS, prevThrottle;
        readonly float rpmCoef, jitCoef;

        // Per-block derived parameters.
        float hNorm, pulseLevel, raspLevel, level, roughness, rpmNorm, idleW, coastW, loadW;
        int pulseLen;

        // Per-sample state.
        float phi;
        int lastFire = -1;
        float jitter = 1f, jitS = 1f, pulseAmp = 1f;
        int pulseT = int.MaxValue;
        Svf form1, form2, tone, intake, whistleBp, bovBp, popBp, clackBp;
        float lopeValue, lopeTarget;
        int lopeCounter;

        // Events and modulators.
        float cut = 1f, cutTarget = 1f;
        int shiftHold;
        float gate = 1f, gatePhase;
        bool limiterOn;
        float whistlePhase;
        float bovEnv, bovTime, bovCooldown;
        bool bovFlutter;
        float popEnv, popCoef;
        float clackEnv, clackCoef, thunkPhase;
        float revBlipTime = -1f;

        public EngineVoice(EngineConfig config, float sampleRate)
        {
            cfg = config;
            prof = EngineProfile.Create(config.Family);
            sr = sampleRate;
            rng = new Rng(config.Seed * 747796405u + 2891336453u);
            for (int h = 1; h <= H; h++)
            {
                float ph = rng.NextFloat();
                phaseCos[h] = SynthMath.Cos01(ph);
                phaseSin[h] = SynthMath.Sin01(ph);
                log2n[h] = MathF.Log(Math.Max(1f, h / (float)prof.FiringHarmonic)) / MathF.Log(2f);
            }
            // ±7% per-car formant variation so two cars of one family are not identical.
            formantScale = 0.93f + 0.14f * rng.NextFloat();
            rpmCoef = SynthMath.SmoothCoef(0.03f, sr);
            jitCoef = SynthMath.SmoothCoef(0.0015f, sr);
            rpmS = rpmTarget = Math.Max(300f, config.IdleRpm);
            popCoef = SynthMath.DecayCoef(0.012f, sr);
            clackCoef = SynthMath.DecayCoef(0.05f, sr);
            bovFlutter = config.BlowOff == BlowOffStyle.Flutter;
        }

        public EngineFamilyKind Family => cfg.Family;
        public float SmoothedRpm => rpmS;

        public void SetInputs(float rpm, float throttle, float load, float boost, int gearIndex, bool shiftActive)
        {
            rpmTarget = SynthMath.Clamp(rpm, 0f, cfg.RedlineRpm * 1.08f);
            throttleTarget = SynthMath.Clamp01(throttle);
            loadTarget = SynthMath.Clamp01(load);
            boostTarget = cfg.Turbo ? SynthMath.Clamp01(boost) : 0f;
            if (gearIndex != gear)
            {
                bool up = gearIndex > gear && gear > 0;
                if (gear != 0 && gearIndex != 0) TriggerShift(up);
                gear = gearIndex;
            }
            shifting = shiftActive;
        }

        /// <summary>Meet rev: a short free-rev blip that ignores the (parked) simulation rpm.</summary>
        public void RevBlip() { revBlipTime = 0f; }

        void TriggerShift(bool up)
        {
            shiftHold = (int)(0.12f * sr);
            clackEnv = 0.9f;
            thunkPhase = 0f;
            if (up && cfg.Turbo && boostS > 0.45f) TriggerBov(boostS * 0.6f);
        }

        void TriggerBov(float strength)
        {
            if (bovCooldown > 0f || cfg.BlowOff == BlowOffStyle.None) return;
            bovEnv = strength;
            bovTime = 0f;
            bovCooldown = 0.7f;
        }

        /// <summary>Control-rate update once per block.</summary>
        void Control(int count)
        {
            float bs = count / sr;
            float kT = 1f - MathF.Exp(-bs / 0.04f);
            float kL = 1f - MathF.Exp(-bs / 0.06f);
            float kB = 1f - MathF.Exp(-bs / 0.09f);

            float effThrottle = throttleTarget, effLoad = loadTarget;
            float effRpm = rpmTarget;
            if (revBlipTime >= 0f)
            {
                // 0.22 s up to ~72% redline, then fall back over ~1 s (rpm smoothing adds the inertia).
                float t = revBlipTime;
                float peak = cfg.IdleRpm + (cfg.RedlineRpm * 0.72f - cfg.IdleRpm);
                float blip = t < 0.22f ? t / 0.22f : Math.Max(0f, 1f - (t - 0.22f) / 1.0f);
                effRpm = Math.Max(effRpm, cfg.IdleRpm + (peak - cfg.IdleRpm) * blip);
                effThrottle = Math.Max(effThrottle, t < 0.22f ? 1f : 0f);
                effLoad = Math.Max(effLoad, t < 0.22f ? 0.8f : 0f);
                revBlipTime += bs;
                if (revBlipTime > 1.4f) revBlipTime = -1f;
                rpmTarget = Math.Max(rpmTarget, effRpm);
            }

            prevThrottle = throttleS;
            throttleS += (effThrottle - throttleS) * kT;
            loadS += (effLoad - loadS) * kL;
            boostS += (boostTarget - boostS) * kB;
            if (bovCooldown > 0f) bovCooldown -= bs;
            if (cfg.Turbo && prevThrottle > 0.55f && throttleS < 0.3f && boostS > 0.4f) TriggerBov(boostS);

            float idle = cfg.IdleRpm, red = cfg.RedlineRpm;
            rpmNorm = SynthMath.Clamp01((rpmS - idle) / Math.Max(500f, red - idle));
            idleW = SynthMath.Clamp01(1f - rpmNorm * 5f) * (1f - throttleS);
            loadW = loadS * (1f - idleW);
            coastW = (1f - loadS) * (1f - idleW);

            float tilt = idleW * prof.TiltIdle + loadW * prof.TiltLoad + coastW * prof.TiltCoast;
            float sub = idleW * prof.SubIdle + loadW * prof.SubLoad + coastW * prof.SubCoast;
            float sumSq = 0f;
            for (int h = 1; h <= H; h++)
            {
                float a = prof.Orders[h];
                if (!prof.IsFiring[h]) a *= sub;
                if (tilt != 0f && log2n[h] > 0f) a *= SynthMath.Exp2(-tilt * log2n[h]);
                sumSq += a * a;
                ca[h] = a * phaseSin[h];
                cb[h] = a * phaseCos[h];
            }
            hNorm = 0.55f / MathF.Sqrt(Math.Max(1e-6f, sumSq));

            pulseLevel = idleW * prof.PulseIdle + loadW * prof.PulseLoad + coastW * prof.PulseCoast;
            raspLevel = idleW * prof.RaspIdle + loadW * prof.RaspLoad + coastW * prof.RaspCoast;
            roughness = prof.Roughness * (1f + idleW * 0.8f);
            pulseLen = Math.Max(4, (int)(prof.PulseMs * 0.001f * sr * (1.3f - 0.3f * rpmNorm)));
            float bandLevel = idleW * prof.LevelIdle + loadW * prof.LevelLoad + coastW * prof.LevelCoast;
            level = prof.Gain * bandLevel * (0.55f + 0.45f * rpmNorm);

            float track = 0.9f + 0.25f * rpmNorm;
            form1.Set(prof.Formant1Hz * formantScale * track, prof.FormantRes, sr);
            form2.Set(prof.Formant2Hz * formantScale * track, prof.FormantRes, sr);
            float toneHz = MathF.Exp(idleW * MathF.Log(prof.ToneIdleHz) + loadW * MathF.Log(prof.ToneLoadHz) + coastW * MathF.Log(prof.ToneCoastHz));
            tone.Set(toneHz * (0.8f + 0.4f * rpmNorm), 0.05f, sr);
            intake.Set(prof.IntakeHz * formantScale * (0.7f + 0.6f * rpmNorm), 0.7f, sr);

            // Shift cut (torque interruption) and rev limiter (fuel cut) targets.
            limiterOn = rpmTarget >= red * 0.985f && throttleS > 0.5f;
            cutTarget = (shifting || shiftHold > 0) ? 0.3f : 1f;

            if (cfg.Turbo)
            {
                float wf = (1600f + 5400f * boostS) * (0.85f + 0.3f * rpmNorm);
                whistleBp.Set(wf, 0.85f, sr);
            }
            if (bovEnv > 0f)
            {
                float f = bovFlutter ? 850f : 2600f - 1400f * SynthMath.Clamp01(bovTime / 0.4f);
                bovBp.Set(f, bovFlutter ? 0.5f : 0.35f, sr);
            }
            clackBp.Set(1900f, 0.4f, sr);
        }

        /// <summary>Adds <paramref name="count"/> engine samples × <paramref name="gain"/> into <paramref name="dst"/>.</summary>
        public void Render(float[] dst, int offset, int count, float gain)
        {
            Control(count);
            float inc0 = 1f / (120f * sr);
            int fpc = prof.FiringsPerCycle;
            float lvl = level * gain;
            float mix = prof.FormantMix;
            float intakeLvl = prof.IntakeLevel * throttleS * (0.25f + 0.75f * rpmNorm) * gain;
            bool turbo = cfg.Turbo;
            float whistleLvl = turbo ? 0.06f * boostS * boostS * (0.35f + 0.65f * throttleS) * gain : 0f;
            float whistleInc = turbo ? (1600f + 5400f * boostS) * (0.85f + 0.3f * rpmNorm) / sr : 0f;
            float crackleP = prof.CrackleRate * coastW * SynthMath.Clamp01((rpmNorm - 0.35f) * 2f) / sr;
            float gateInc = prof.LimiterHz / sr;
            float cutUp = SynthMath.SmoothCoef(0.03f, sr), cutDown = SynthMath.SmoothCoef(0.006f, sr);
            float gateCoef = SynthMath.SmoothCoef(0.002f, sr);
            float bovDecay = SynthMath.DecayCoef(bovFlutter ? 0.55f : 0.45f, sr);

            for (int i = 0; i < count; i++)
            {
                rpmS = rpmTarget + (rpmS - rpmTarget) * rpmCoef;

                // Idle lope: slow random walk on audible rpm, only near idle.
                if (--lopeCounter <= 0)
                {
                    lopeCounter = (int)(sr * (0.18f + 0.2f * rng.NextFloat()));
                    lopeTarget = rng.NextBipolar();
                }
                lopeValue += (lopeTarget - lopeValue) * 0.0004f;
                float rpmA = rpmS * (1f + idleW * prof.IdleLope * 0.035f * lopeValue);

                phi += rpmA * inc0;
                if (phi >= 1f) phi -= 1f;

                int fire = (int)(phi * fpc);
                if (fire != lastFire)
                {
                    lastFire = fire;
                    jitter = 1f + roughness * rng.NextBipolar();
                    pulseAmp = jitter;
                    pulseT = 0;
                }
                jitS = jitter + (jitS - jitter) * jitCoef;

                // Harmonic sum over half orders via the Chebyshev recurrence.
                float s1 = SynthMath.Sin01(phi);
                float c1 = SynthMath.Sin01(phi + 0.25f);
                float sum = ca[1] * c1 + cb[1] * s1;
                float sp = 0f, sc = s1, cp = 1f, cc = c1, k2 = c1 + c1;
                for (int h = 2; h <= H; h++)
                {
                    float sn = k2 * sc - sp;
                    float cn = k2 * cc - cp;
                    sum += ca[h] * cn + cb[h] * sn;
                    sp = sc; sc = sn; cp = cc; cc = cn;
                }

                float pulse = 0f;
                if (pulseT < pulseLen)
                {
                    float x = pulseT / (float)pulseLen;
                    pulse = 0.5f - 0.5f * SynthMath.Sin01(x + 0.25f);
                    pulseT++;
                }
                float noise = rng.NextBipolar();
                float src = sum * hNorm * jitS + pulse * pulseAmp * pulseLevel + noise * pulse * raspLevel;
                float y = src * (1f - mix) + mix * (form1.BandPass(src) * 1.3f + form2.BandPass(src) * 0.8f);
                y = tone.LowPass(y);

                // Shift cut / limiter gate.
                float ct = cutTarget;
                cut = ct + (cut - ct) * (ct < cut ? cutDown : cutUp);
                float gt = 1f;
                if (limiterOn)
                {
                    gatePhase += gateInc;
                    if (gatePhase >= 1f)
                    {
                        gatePhase -= 1f;
                        popEnv = Math.Max(popEnv, 0.35f); // fuel-cut pop at each cut
                    }
                    gt = gatePhase < 0.55f ? 1f : 0.22f;
                }
                gate = gt + (gate - gt) * gateCoef;

                float outS = y * lvl * cut * gate;

                // Intake roar: band-passed noise opening with throttle.
                outS += intake.BandPass(rng.NextBipolar()) * intakeLvl;

                if (turbo)
                {
                    whistlePhase += whistleInc;
                    if (whistlePhase >= 1f) whistlePhase -= 1f;
                    outS += (SynthMath.Sin01(whistlePhase) * 0.6f + whistleBp.BandPass(noise) * 0.8f) * whistleLvl;
                }

                if (bovEnv > 1e-4f)
                {
                    float am = 1f;
                    if (bovFlutter)
                    {
                        float fr = 24f - 12f * SynthMath.Clamp01(bovTime / 0.5f);
                        am = 0.5f + 0.5f * SynthMath.Sin01(SynthMath.Wrap01(bovTime * fr));
                        am *= am;
                    }
                    outS += bovBp.BandPass(rng.NextBipolar()) * bovEnv * am * 0.35f * gain;
                    bovEnv *= bovDecay;
                    bovTime += 1f / sr;
                }

                // Overrun crackle: sparse pops while coasting at high rpm.
                if (crackleP > 0f && rng.NextFloat() < crackleP)
                {
                    popEnv = 0.25f + 0.45f * rng.NextFloat();
                    popBp.Set(800f + 1400f * rng.NextFloat(), 0.45f, sr);
                }
                if (popEnv > 1e-4f)
                {
                    outS += popBp.BandPass(rng.NextBipolar()) * popEnv * 0.5f * gain;
                    popEnv *= popCoef;
                }

                // Gearshift clack: short mechanical tick plus a low thunk.
                if (clackEnv > 1e-4f)
                {
                    thunkPhase += 70f / sr;
                    if (thunkPhase >= 1f) thunkPhase -= 1f;
                    outS += (clackBp.BandPass(rng.NextBipolar()) * 0.5f + SynthMath.Sin01(thunkPhase) * 0.6f) * clackEnv * 0.18f * gain;
                    clackEnv *= clackCoef;
                }
                if (shiftHold > 0) shiftHold--;

                dst[offset + i] += outS * OutputTrim;
            }
            form1.Flush(); form2.Flush(); tone.Flush();
        }
    }
}
