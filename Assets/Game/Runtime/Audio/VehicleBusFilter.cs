using NightSignal.AudioSynth;
using UnityEngine;

namespace NightSignal.GameAudio
{
    /// <summary>
    /// Renders one bus of a car's <see cref="VehicleSoundModel"/> into its own spatialised AudioSource, so engine,
    /// road (tyres/kerbs/scrape/wind) and impacts can each be routed to a separate AudioMixer group.
    /// Created by <see cref="EngineAudio"/>; not intended to be added by hand.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [AddComponentMenu("")]
    public sealed class VehicleBusFilter : MonoBehaviour
    {
        internal volatile VehicleSoundModel Model;
        internal VehicleBus Bus;

        void OnAudioFilterRead(float[] data, int channels)
        {
            var m = Model;
            if (m == null)
            {
                System.Array.Clear(data, 0, data.Length);
                return;
            }
            m.RenderBus(Bus, data, data.Length / channels, channels, true);
        }
    }
}
