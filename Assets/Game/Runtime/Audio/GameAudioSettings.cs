using System;
using UnityEngine;

namespace NightSignal.GameAudio
{
    /// <summary>
    /// Local (never server-owned) mix levels, 0..1 each. Independent controls per spec §16 / Addendum 01 §11:
    /// music, engine, tyres, impacts, UI, ambience (incl. wind) and voice. The settings screen writes these;
    /// every audio component reads them each frame. Persisting them is the settings system's job.
    /// </summary>
    public static class GameAudioSettings
    {
        static float master = 1f, music = 0.8f, engine = 1f, tyres = 1f, impacts = 1f, ui = 0.9f, ambient = 0.8f, voice = 1f;

        public static event Action Changed;

        public static float Master { get => master; set => Set(ref master, value); }
        public static float Music { get => music; set => Set(ref music, value); }
        public static float Engine { get => engine; set => Set(ref engine, value); }
        public static float Tyres { get => tyres; set => Set(ref tyres, value); }
        public static float Impacts { get => impacts; set => Set(ref impacts, value); }
        public static float Ui { get => ui; set => Set(ref ui, value); }
        public static float Ambient { get => ambient; set => Set(ref ambient, value); }
        public static float Voice { get => voice; set => Set(ref voice, value); }

        static void Set(ref float field, float value)
        {
            float v = Mathf.Clamp01(value);
            if (Mathf.Approximately(field, v)) return;
            field = v;
            Changed?.Invoke();
        }

        /// <summary>Perceptual fader curve: slider 0..1 → linear gain (≈ −48 dB at 0.05, 0 dB at 1).</summary>
        public static float ToGain(float slider)
        {
            if (slider <= 0.001f) return 0f;
            return slider * slider;
        }
    }
}
