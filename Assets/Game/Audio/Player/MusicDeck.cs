using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// The game's single music output: crossfades between cues, fades out, ducks under dialogue/notifications and
    /// applies the player's music volume. Up to four players can overlap during rapid changes; every fade uses
    /// the same linear rate for in and out, so the summed gain never exceeds 1 and the output stays below the
    /// players' limiter ceiling (no clipping, no abandoned tracks left playing).
    ///
    /// Threading: <see cref="Play"/>, <see cref="Stop"/>, <see cref="SetDuck"/> and <see cref="Volume"/> are
    /// called from the game thread (single producer); <see cref="Render"/> from the audio thread. Commands cross
    /// through a lock-free queue; nothing allocates on the audio thread.
    /// </summary>
    public sealed class MusicDeck
    {
        const int MaxSlots = 4;
        const int ChunkFrames = 512;

        struct Command
        {
            public byte Kind; // 1 play, 2 stop, 3 duck
            public ScorePlayer Player;
            public float Seconds;
            public float Value;
        }

        readonly int sampleRate;
        readonly ScorePlayer[] players = new ScorePlayer[MaxSlots];
        readonly float[] gain = new float[MaxSlots];
        readonly float[] target = new float[MaxSlots];
        readonly float[] step = new float[MaxSlots];
        readonly SpscRing<Command> commands = new SpscRing<Command>(64);
        readonly float[] scratch = new float[ChunkFrames * 2];
        readonly float[] mixL = new float[ChunkFrames];
        readonly float[] mixR = new float[ChunkFrames];
        readonly float smoothCoef;

        float volume = 1f;
        float volumeSmoothed = 1f;
        float duckGain = 1f, duckTarget = 1f, duckStep;
        volatile int activeCount;

        public MusicDeck(int sampleRate)
        {
            this.sampleRate = sampleRate;
            smoothCoef = SynthMath.SmoothCoef(0.03f, sampleRate);
            SynthMath.Warm();
        }

        public int SampleRate => sampleRate;

        /// <summary>Linear music volume 0..1 (settings × context). Smoothed on the audio thread.</summary>
        public float Volume
        {
            get => volume;
            set => volume = SynthMath.Clamp01(value);
        }

        /// <summary>Number of players currently audible or fading (audio-thread view; approximate).</summary>
        public int ActiveCount => activeCount;

        /// <summary>Starts <paramref name="player"/>, fading everything else out over the same time.</summary>
        public bool Play(ScorePlayer player, float crossfadeSeconds)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (player.SampleRate != sampleRate) throw new ArgumentException("player sample rate differs from deck");
            return commands.TryEnqueue(new Command { Kind = 1, Player = player, Seconds = crossfadeSeconds });
        }

        public bool Stop(float fadeSeconds) => commands.TryEnqueue(new Command { Kind = 2, Seconds = fadeSeconds });

        /// <summary>Ducks music by <paramref name="db"/> (≤ 0) reaching it over <paramref name="seconds"/>; 0 dB releases.</summary>
        public bool SetDuck(float db, float seconds) => commands.TryEnqueue(new Command { Kind = 3, Seconds = seconds, Value = Math.Min(0f, db) });

        float RateFor(float seconds) => seconds <= 0.005f ? 1f : 1f / (seconds * sampleRate);

        void Drain()
        {
            Command c;
            while (commands.TryDequeue(out c))
            {
                switch (c.Kind)
                {
                    case 1:
                    {
                        float r = RateFor(c.Seconds);
                        int free = -1;
                        float quietest = float.MaxValue;
                        int quietSlot = 0;
                        for (int i = 0; i < MaxSlots; i++)
                        {
                            if (players[i] == null) { if (free < 0) free = i; continue; }
                            target[i] = 0f;
                            step[i] = r;
                            if (gain[i] < quietest) { quietest = gain[i]; quietSlot = i; }
                        }
                        int slot = free >= 0 ? free : quietSlot;
                        players[slot] = c.Player;
                        gain[slot] = r >= 1f ? 1f : 0f;
                        target[slot] = 1f;
                        step[slot] = r;
                        break;
                    }
                    case 2:
                    {
                        float r = RateFor(c.Seconds);
                        for (int i = 0; i < MaxSlots; i++) { target[i] = 0f; step[i] = r; }
                        break;
                    }
                    case 3:
                    {
                        duckTarget = SynthMath.DbToGain(c.Value);
                        float seconds = Math.Max(0.01f, c.Seconds);
                        duckStep = Math.Abs(duckTarget - duckGain) / (seconds * sampleRate);
                        break;
                    }
                }
            }
        }

        /// <summary>Overwrites interleaved <paramref name="data"/> with the music mix.</summary>
        public void Render(float[] data, int frames, int channels)
        {
            Drain();
            int written = 0;
            while (written < frames)
            {
                int n = Math.Min(ChunkFrames, frames - written);
                Array.Clear(mixL, 0, n);
                Array.Clear(mixR, 0, n);
                int active = 0;
                for (int s = 0; s < MaxSlots; s++)
                {
                    var p = players[s];
                    if (p == null) continue;
                    active++;
                    p.Render(scratch, n, 2);
                    float g = gain[s], tg = target[s], st = step[s];
                    for (int i = 0; i < n; i++)
                    {
                        if (g < tg) { g += st; if (g > tg) g = tg; }
                        else if (g > tg) { g -= st; if (g < tg) g = tg; }
                        mixL[i] += scratch[i * 2] * g;
                        mixR[i] += scratch[i * 2 + 1] * g;
                    }
                    gain[s] = g;
                    if ((g <= 0f && tg <= 0f) || (p.Finished && !p.Loop)) players[s] = null; // faded out: release
                }
                activeCount = active;

                float v = volumeSmoothed, vt = volume, k = smoothCoef;
                float dg = duckGain, dt = duckTarget, ds = duckStep;
                int idx = written * channels;
                for (int i = 0; i < n; i++)
                {
                    v = vt + (v - vt) * k;
                    if (dg < dt) { dg += ds; if (dg > dt) dg = dt; }
                    else if (dg > dt) { dg -= ds; if (dg < dt) dg = dt; }
                    float g = v * dg;
                    float l = mixL[i] * g, r = mixR[i] * g;
                    if (channels == 1) data[idx++] = 0.5f * (l + r);
                    else
                    {
                        data[idx++] = l;
                        data[idx++] = r;
                        for (int c = 2; c < channels; c++) data[idx++] = 0f;
                    }
                }
                volumeSmoothed = v;
                duckGain = dg;
                written += n;
            }
        }
    }
}
