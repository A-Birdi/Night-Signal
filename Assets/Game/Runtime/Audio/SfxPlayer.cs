using System;
using NightSignal.AudioSynth;
using UnityEngine;

namespace NightSignal.GameAudio
{
    /// <summary>
    /// UI sound bus: SIGNAL ribbon chime, menu move/confirm/back, ready chime, countdown 3-2-1-GO and results
    /// stings, synthesized by <see cref="SfxEngine"/>. Independent of the music volume, so muting music never
    /// mutes start lights or alerts. Raises <see cref="CaptionRaised"/> for important non-verbal cues so the
    /// UI can show captions (spec §16).
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    public sealed class SfxPlayer : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] float localVolume = 1f;
        [SerializeField] bool persistAcrossScenes = true;

        public static SfxPlayer Instance { get; private set; }

        /// <summary>Main-thread event: (cue, caption text such as "[Countdown: 3]").</summary>
        public static event Action<UiCue, string> CaptionRaised;

        SfxEngine engine;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (persistAcrossScenes) DontDestroyOnLoad(gameObject);
            SynthMath.Warm();
            var src = GetComponent<AudioSource>();
            src.priority = 0;
            src.bypassReverbZones = true;
            ProceduralAudio.Prepare(src, 0f);
            engine = new SfxEngine(ProceduralAudio.SampleRate);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (engine != null)
                engine.Volume = GameAudioSettings.ToGain(GameAudioSettings.Master) * GameAudioSettings.ToGain(GameAudioSettings.Ui) * localVolume;
        }

        /// <summary>Plays a UI cue through the shared instance (no-op if none exists).</summary>
        public static void PlayUi(UiCue cue, float gain = 1f)
        {
            if (Instance != null) Instance.Play(cue, gain);
        }

        public void Play(UiCue cue, float gain = 1f)
        {
            if (engine == null) return;
            engine.Play(cue, gain);
            string caption = UiCueLibrary.Caption(cue);
            if (caption != null) CaptionRaised?.Invoke(cue, caption);
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            var e = engine;
            if (e == null) { Array.Clear(data, 0, data.Length); return; }
            e.Render(data, data.Length / channels, channels);
        }
    }
}
