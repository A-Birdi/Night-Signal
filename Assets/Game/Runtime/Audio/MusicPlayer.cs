using System;
using System.Collections.Generic;
using NightSignal.AudioSynth;
using UnityEngine;

namespace NightSignal.GameAudio
{
    /// <summary>
    /// The game's single music output. Loads night-signal/score@1 TextAssets, compiles them on the main thread
    /// (cached), and plays them through the engine-free <see cref="MusicDeck"/> in OnAudioFilterRead: crossfades
    /// between cues, fades out, ducks, and applies the music volume setting. Playing the cue that is already
    /// playing is a no-op, so small submenu changes never restart a song. Music playback is local presentation
    /// only and never drives race clocks.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    public sealed class MusicPlayer : MonoBehaviour
    {
        [SerializeField] TextAsset instrumentLibrary;
        [SerializeField] TextAsset[] cues = new TextAsset[0];
        [SerializeField] string playOnStart = "";
        [SerializeField, Min(0f)] float defaultCrossfadeSeconds = 1.5f;
        [SerializeField, Range(0f, 1f)] float localVolume = 1f;
        [SerializeField] bool persistAcrossScenes = true;

        public static MusicPlayer Instance { get; private set; }

        MusicDeck deck;
        InstrumentLibrary library;
        int sampleRate;
        readonly Dictionary<string, TextAsset> sources = new Dictionary<string, TextAsset>(StringComparer.Ordinal);
        readonly Dictionary<string, CompiledScore> compiled = new Dictionary<string, CompiledScore>(StringComparer.Ordinal);

        public string CurrentCue { get; private set; }

        /// <summary>
        /// The game's music output, created on first use from Resources/MusicLibrary (persists across scenes). None on a
        /// headless (batch-mode) process: the room and race servers play no audio.
        /// </summary>
        public static MusicPlayer Ensure()
        {
            if (Instance != null) return Instance;
            if (Application.isBatchMode) return null;
            var lib = Resources.Load<MusicLibrary>("MusicLibrary");
            if (lib == null || lib.Instruments == null || lib.Cues == null || lib.Cues.Length == 0) return null;
            var go = new GameObject("Music");
            go.SetActive(false);
            go.AddComponent<AudioSource>();
            var mp = go.AddComponent<MusicPlayer>();
            mp.instrumentLibrary = lib.Instruments;
            mp.cues = lib.Cues;
            go.SetActive(true); // Awake runs now, with the library assigned
            return Instance;
        }
        public IEnumerable<string> AvailableCues => sources.Keys;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // One music output only: never leave two decks playing over each other.
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (persistAcrossScenes) DontDestroyOnLoad(gameObject);

            SynthMath.Warm();
            sampleRate = ProceduralAudio.SampleRate;
            library = instrumentLibrary != null ? InstrumentLibrary.Parse(instrumentLibrary.text) : InstrumentLibrary.Empty;
            foreach (var asset in cues)
            {
                if (asset == null) continue;
                try
                {
                    string id = Json.Parse(asset.text).Str("id");
                    if (!string.IsNullOrEmpty(id)) sources[id] = asset;
                }
                catch (JsonException e)
                {
                    Debug.LogError("MusicPlayer: " + asset.name + " is not valid JSON: " + e.Message, this);
                }
            }
            var src = GetComponent<AudioSource>();
            src.priority = 0;
            src.bypassReverbZones = true;
            ProceduralAudio.Prepare(src, 0f);
            deck = new MusicDeck(sampleRate) { Volume = 0f };
        }

        void Start()
        {
            if (!string.IsNullOrEmpty(playOnStart)) Play(playOnStart, 0.5f);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (deck == null) return;
            deck.Volume = GameAudioSettings.ToGain(GameAudioSettings.Master) * GameAudioSettings.ToGain(GameAudioSettings.Music) * localVolume;
        }

        /// <summary>Compiles (and caches) a cue ahead of time so a later <see cref="Play"/> has no compile hitch.</summary>
        public bool Preload(string cueId) => GetScore(cueId) != null;

        CompiledScore GetScore(string cueId)
        {
            CompiledScore score;
            if (compiled.TryGetValue(cueId, out score)) return score;
            TextAsset asset;
            if (!sources.TryGetValue(cueId, out asset))
            {
                Debug.LogWarning("MusicPlayer: unknown cue '" + cueId + "'", this);
                return null;
            }
            try
            {
                score = ScoreCompiler.Compile(asset.text, library);
            }
            catch (ScoreException e)
            {
                Debug.LogError("MusicPlayer: cue '" + cueId + "' failed to compile:\n" + e.Message, this);
                return null;
            }
            compiled[cueId] = score;
            return score;
        }

        public bool HasCue(string cueId) => cueId != null && sources.ContainsKey(cueId);

        /// <summary>A cue's display title and category from its score metadata (the boombox list); false when unknown.</summary>
        public bool CueInfo(string cueId, out string displayTitle, out string category)
        {
            displayTitle = cueId;
            category = "";
            if (cueId == null || !sources.TryGetValue(cueId, out TextAsset asset)) return false;
            try
            {
                JsonValue root = Json.Parse(asset.text);
                JsonValue cue = root.Get("cue");
                displayTitle = cue?.Str("displayTitle") ?? root.Str("title", cueId);
                category = cue?.Str("category", "") ?? "";
                return true;
            }
            catch (JsonException) { return false; }
        }

        /// <summary>One full pass of a cue (intro + loop body) in seconds; compiles it if needed. 0 when unknown.</summary>
        public double CueSeconds(string cueId)
        {
            if (!HasCue(cueId)) return 0;
            CompiledScore score = GetScore(cueId);
            return score != null ? score.TotalSeconds : 0;
        }

        /// <summary>Crossfades to <paramref name="cueId"/>. Returns false if the cue is unknown or invalid.</summary>
        public bool Play(string cueId, float crossfadeSeconds = -1f)
        {
            if (deck == null || string.IsNullOrEmpty(cueId)) return false;
            if (cueId == CurrentCue) return true;
            var score = GetScore(cueId);
            if (score == null) return false;
            var player = new ScorePlayer(score, sampleRate); // allocates here, on the main thread
            if (!deck.Play(player, crossfadeSeconds < 0f ? defaultCrossfadeSeconds : crossfadeSeconds)) return false;
            CurrentCue = cueId;
            return true;
        }

        public void Stop(float fadeSeconds = 1f)
        {
            if (deck == null) return;
            deck.Stop(fadeSeconds);
            CurrentCue = null;
        }

        /// <summary>Ducks music by <paramref name="db"/> (negative) over <paramref name="seconds"/>.</summary>
        public void Duck(float db, float seconds = 0.25f) => deck?.SetDuck(db, seconds);

        public void Unduck(float seconds = 0.6f) => deck?.SetDuck(0f, seconds);

        void OnAudioFilterRead(float[] data, int channels)
        {
            var d = deck;
            if (d == null) { Array.Clear(data, 0, data.Length); return; }
            d.Render(data, data.Length / channels, channels);
        }
    }
}
