using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Linear-attack, exponential decay/release envelope. Re-triggering attacks from the current level, so a
    /// retriggered or stolen voice never jumps in amplitude (click-free).
    /// </summary>
    public struct Adsr
    {
        const byte StIdle = 0, StAttack = 1, StDecay = 2, StSustain = 3, StRelease = 4;

        public float Level;
        byte stage;
        float attackInc, decayCoef, sustain, releaseCoef;

        public bool IsActive => stage != StIdle;
        public bool IsReleasing => stage == StRelease;
        public float SustainLevel => sustain;

        public void Configure(float attack, float decay, float sustainLevel, float release, float sampleRate)
        {
            attackInc = attack <= 0.0002f ? 1f : 1f / (attack * sampleRate);
            decayCoef = Coef(decay, sampleRate);
            sustain = SynthMath.Clamp01(sustainLevel);
            releaseCoef = Coef(release, sampleRate);
        }

        static float Coef(float seconds, float sampleRate) =>
            seconds <= 0.0002f ? 0f : MathF.Exp(-4.6051702f / (seconds * sampleRate)); // 1% after `seconds`

        public void Trigger() { stage = StAttack; }

        public void Release() { if (stage != StIdle) stage = StRelease; }

        public void Kill() { stage = StIdle; Level = 0f; }

        public float Next()
        {
            switch (stage)
            {
                case StAttack:
                    Level += attackInc;
                    if (Level >= 1f) { Level = 1f; stage = StDecay; }
                    break;
                case StDecay:
                    Level = sustain + (Level - sustain) * decayCoef;
                    if (Level - sustain < 1e-4f)
                    {
                        Level = sustain;
                        stage = sustain <= 1e-4f ? StIdle : StSustain;
                        if (stage == StIdle) Level = 0f;
                    }
                    break;
                case StRelease:
                    Level *= releaseCoef;
                    if (Level < 1e-4f) { Level = 0f; stage = StIdle; }
                    break;
            }
            return Level;
        }
    }
}
