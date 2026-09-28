using UnityEngine;

namespace NightSignal.GameAudio
{
    /// <summary>The soundtrack's score documents and instrument library, in Resources so the game can create its one <see cref="MusicPlayer"/>.</summary>
    [CreateAssetMenu(menuName = "Night Signal/Music Library")]
    public sealed class MusicLibrary : ScriptableObject
    {
        public TextAsset Instruments;
        public TextAsset[] Cues = new TextAsset[0];
    }
}
