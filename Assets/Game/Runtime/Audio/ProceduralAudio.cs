using UnityEngine;

namespace NightSignal.GameAudio
{
    /// <summary>
    /// Helpers shared by the procedural audio components. Synthesis runs in OnAudioFilterRead; the AudioSource
    /// plays a looping constant-1.0 clip so the source stays active, keeps its mixer routing, and — for 3D
    /// sources — carries Unity's distance attenuation and panning, which the filter multiplies into.
    /// </summary>
    public static class ProceduralAudio
    {
        static AudioClip ones;

        /// <summary>Output sample rate, read on the main thread (AudioSettings is not audio-thread safe).</summary>
        public static int SampleRate => AudioSettings.outputSampleRate;

        public static AudioClip OnesClip()
        {
            if (ones != null) return ones;
            const int length = 4096;
            ones = AudioClip.Create("NS_ProceduralCarrier", length, 1, SampleRate, false);
            var data = new float[length];
            for (int i = 0; i < length; i++) data[i] = 1f;
            ones.SetData(data, 0);
            return ones;
        }

        public static AudioSource Prepare(AudioSource src, float spatialBlend)
        {
            src.playOnAwake = false;
            src.clip = OnesClip();
            src.loop = true;
            src.spatialBlend = spatialBlend;
            src.dopplerLevel = 0f;
            src.volume = 1f;
            src.pitch = 1f;
            if (!src.isPlaying) src.Play();
            return src;
        }
    }
}
