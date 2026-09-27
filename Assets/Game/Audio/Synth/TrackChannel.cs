using System;

namespace NightSignal.AudioSynth
{
    /// <summary>Mixer settings for one score track (night-signal/score@1 "tracks" entry).</summary>
    public sealed class TrackSpec
    {
        public string Id = "";
        public InstrumentPreset Preset;
        public float GainDb;
        public float Pan;
        public float Reverb;
        public float Delay;
        /// <summary>Sidechain duck depth 0..1 applied when the kick of a sidechain-source track hits.</summary>
        public float Duck;
        /// <summary>Insert saturation 0..1 (synth-rock guitars, gritty bass); 0 = bypass.</summary>
        public float Drive;
        public float DriveTone = 5000f;
        public bool SidechainSource;
    }

    /// <summary>
    /// One score track: a voice pool for its instrument plus insert drive, gain/pan and send levels. Renders a
    /// block into its own stereo buffers; the <see cref="ScorePlayer"/> mixes those into the buses.
    /// </summary>
    public sealed class TrackChannel
    {
        public readonly TrackSpec Spec;
        public readonly bool IsDrums;
        public readonly float[] Left;
        public readonly float[] Right;
        public readonly float GainL, GainR;

        readonly SynthVoice[] voices;
        readonly DrumVoice[] drums;
        readonly float sr;
        readonly float drumGain;
        readonly bool mono;
        Svf toneL, toneR;
        readonly float driveIn, driveNorm;

        public TrackChannel(TrackSpec spec, float sampleRate, int blockSize, uint seed)
        {
            Spec = spec;
            sr = sampleRate;
            Left = new float[blockSize];
            Right = new float[blockSize];
            IsDrums = spec.Preset.Kind == VoiceKind.Drums;
            float g = SynthMath.DbToGain(spec.GainDb);
            float pl, pr;
            SynthMath.PanGains(spec.Pan, out pl, out pr);
            // Normalise equal-power so a centred track keeps unity gain on each side.
            GainL = g * pl * 1.41421356f;
            GainR = g * pr * 1.41421356f;
            if (IsDrums)
            {
                drums = new DrumVoice[24];
                for (int i = 0; i < drums.Length; i++) drums[i] = new DrumVoice(sr, seed + (uint)i * 7919u + 3u);
                drumGain = spec.Preset.Gain;
            }
            else
            {
                mono = spec.Preset.Mono;
                int n = mono ? 1 : Math.Max(1, spec.Preset.Polyphony);
                voices = new SynthVoice[n];
                for (int i = 0; i < n; i++) voices[i] = new SynthVoice(spec.Preset, sr, seed, i);
            }
            if (spec.Drive > 0f)
            {
                driveIn = 1f + spec.Drive * 8f;
                driveNorm = 1f / SynthMath.SoftClip(driveIn);
                toneL.Set(spec.DriveTone, 0.1f, sr);
                toneR.Set(spec.DriveTone, 0.1f, sr);
            }
        }

        public void NoteOn(float pitch, float velocity, int holdSamples, int offset, bool glide)
        {
            if (IsDrums)
            {
                int pieceIndex = (int)pitch;
                if (pieceIndex < 0 || pieceIndex >= 12) return;
                var piece = (DrumPiece)pieceIndex;
                if (piece == DrumPiece.ClosedHat)
                    for (int i = 0; i < drums.Length; i++)
                        if (drums[i].IsActive && drums[i].Piece == DrumPiece.OpenHat) drums[i].Choke();
                // Two voices per piece, round-robin, so a retrigger never cuts a ringing hit.
                int a = pieceIndex * 2, b = a + 1;
                DrumVoice v;
                if (!drums[a].IsActive) v = drums[a];
                else if (!drums[b].IsActive) v = drums[b];
                else v = flip[pieceIndex] ? drums[b] : drums[a]; // both ringing: the older one (they alternate)
                flip[pieceIndex] = !flip[pieceIndex];
                v.Trigger(piece, Spec.Preset.Pieces[pieceIndex], velocity, offset, drumGain);
                return;
            }

            SynthVoice target;
            if (mono) target = voices[0];
            else
            {
                target = null;
                // 1) same note still sounding (repeat) 2) idle voice 3) quietest releasing 4) oldest
                for (int i = 0; i < voices.Length; i++)
                    if (voices[i].IsActive && voices[i].Note == pitch && voices[i].IsReleasing) { target = voices[i]; break; }
                if (target == null)
                    for (int i = 0; i < voices.Length; i++)
                        if (!voices[i].IsActive) { target = voices[i]; break; }
                if (target == null)
                {
                    float best = float.MaxValue;
                    for (int i = 0; i < voices.Length; i++)
                        if (voices[i].IsReleasing && voices[i].Level < best) { best = voices[i].Level; target = voices[i]; }
                }
                if (target == null)
                {
                    int oldest = -1;
                    for (int i = 0; i < voices.Length; i++)
                        if (voices[i].Age > oldest) { oldest = voices[i].Age; target = voices[i]; }
                }
            }
            target.NoteOn(pitch, velocity, holdSamples, offset, glide);
        }

        readonly bool[] flip = new bool[12];

        public void Render(int count)
        {
            Array.Clear(Left, 0, count);
            Array.Clear(Right, 0, count);
            if (IsDrums)
            {
                for (int i = 0; i < drums.Length; i++) drums[i].Render(Left, Right, count);
            }
            else
            {
                for (int i = 0; i < voices.Length; i++) voices[i].Render(Left, Right, count);
            }
            if (Spec.Drive > 0f)
            {
                for (int i = 0; i < count; i++)
                {
                    Left[i] = toneL.LowPass(SynthMath.SoftClip(Left[i] * driveIn) * driveNorm);
                    Right[i] = toneR.LowPass(SynthMath.SoftClip(Right[i] * driveIn) * driveNorm);
                }
            }
        }

        public bool AnyActive
        {
            get
            {
                if (IsDrums) { for (int i = 0; i < drums.Length; i++) if (drums[i].IsActive) return true; return false; }
                for (int i = 0; i < voices.Length; i++) if (voices[i].IsActive) return true;
                return false;
            }
        }
    }
}
