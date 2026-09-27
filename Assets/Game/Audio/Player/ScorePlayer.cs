using System;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Plays one <see cref="CompiledScore"/>: sample-accurate sequencer, track voices, sidechain ducking, shared
    /// reverb and ping-pong delay buses, master gain and a look-ahead limiter (output ≤ −1 dBFS by default,
    /// never above −0.3 dBFS). The intro plays once, then the loop region repeats seamlessly because voices and
    /// effect tails simply continue across the jump.
    ///
    /// Threading: construct on any non-audio thread; after that only the audio thread calls <see cref="Render"/>.
    /// Rendering is allocation-free and runs in fixed internal blocks, so the output is bit-identical regardless
    /// of the host's buffer size.
    /// </summary>
    public sealed class ScorePlayer
    {
        public const int Block = 32;

        readonly CompiledScore score;
        readonly int sampleRate;
        readonly TrackChannel[] tracks;
        readonly Reverb reverb;
        readonly StereoDelay delay;
        readonly Limiter limiter;
        readonly float[] busL = new float[Block], busR = new float[Block];
        readonly float[] revL = new float[Block], revR = new float[Block];
        readonly float[] dlyL = new float[Block], dlyR = new float[Block];
        readonly float[] dlyOutL = new float[Block], dlyOutR = new float[Block];
        readonly float[] duckEnv = new float[Block];
        readonly float[] outBuf = new float[Block * 2];
        int outPos = Block;

        readonly double ticksPerSample;
        readonly double samplesPerTick;
        readonly float masterGain;
        readonly float delayToReverb;
        readonly float duckCoef;
        readonly int[] sidechainTracks;
        double tick;
        int nextEvent;
        float duck;
        int pendingDuckOffset = -1;
        long framesRendered;
        int loops;

        /// <summary>When false the cue stops at its end (tails ring out) instead of looping.</summary>
        public bool Loop = true;

        /// <summary>Per-track gain multipliers (1 = as scored). For stems/diagnostics; set before rendering.</summary>
        public readonly float[] TrackGain;

        public CompiledScore Score => score;
        public int SampleRate => sampleRate;
        public long FramesRendered => framesRendered;
        public int LoopsCompleted => loops;
        public bool Finished { get; private set; }
        public double PositionSeconds => tick * score.SecondsPerTick;

        public ScorePlayer(CompiledScore score, int sampleRate)
        {
            if (score == null) throw new ArgumentNullException(nameof(score));
            if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            SynthMath.Warm();
            this.score = score;
            this.sampleRate = sampleRate;
            tracks = new TrackChannel[score.Tracks.Length];
            TrackGain = new float[score.Tracks.Length];
            for (int i = 0; i < TrackGain.Length; i++) TrackGain[i] = 1f;
            int sc = 0;
            for (int i = 0; i < tracks.Length; i++)
            {
                tracks[i] = new TrackChannel(score.Tracks[i], sampleRate, Block, Rng.Hash(score.Id + "/" + score.Tracks[i].Id));
                if (score.Tracks[i].SidechainSource) sc++;
            }
            sidechainTracks = new int[sc];
            sc = 0;
            for (int i = 0; i < tracks.Length; i++) if (score.Tracks[i].SidechainSource) sidechainTracks[sc++] = i;

            var mix = score.Mix;
            reverb = new Reverb(sampleRate, Block);
            reverb.Set(mix.ReverbSize, mix.ReverbDamp, mix.ReverbWidth, mix.ReverbWet);
            delay = new StereoDelay(sampleRate, 1.25f);
            float beat = 60f / score.Tempo;
            delay.Set(Math.Min(1.2f, mix.DelayBeats * beat), mix.DelayFeedback, mix.DelayDampHz);
            limiter = new Limiter(sampleRate, mix.CeilingDb);
            masterGain = SynthMath.DbToGain(mix.MasterDb);
            delayToReverb = mix.DelayToReverb;
            duckCoef = SynthMath.DecayCoef(Math.Max(0.02f, mix.DuckReleaseBeats * beat), sampleRate);
            samplesPerTick = score.SecondsPerTick * sampleRate;
            ticksPerSample = 1.0 / samplesPerTick;
        }

        /// <summary>
        /// Writes <paramref name="frames"/> frames (overwriting) into interleaved <paramref name="data"/>.
        /// Mono hosts get (L+R)/2; channels beyond two are silenced.
        /// </summary>
        public void Render(float[] data, int frames, int channels)
        {
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                if (outPos >= Block) { RenderBlock(); outPos = 0; }
                float l = outBuf[outPos * 2], r = outBuf[outPos * 2 + 1];
                outPos++;
                if (channels == 1) data[idx++] = 0.5f * (l + r);
                else
                {
                    data[idx++] = l;
                    data[idx++] = r;
                    for (int c = 2; c < channels; c++) data[idx++] = 0f;
                }
            }
            framesRendered += frames;
        }

        void Trigger(ref ScoreEvent e, int offset)
        {
            var t = tracks[e.Track];
            int hold = (int)(e.Duration * samplesPerTick);
            t.NoteOn(e.Pitch, e.Velocity, hold, offset, (e.Flags & ScoreEvent.FlagGlide) != 0);
            if (t.IsDrums && (int)e.Pitch == (int)DrumPiece.Kick && t.Spec.SidechainSource)
                if (pendingDuckOffset < 0 || offset < pendingDuckOffset) pendingDuckOffset = offset;
        }

        void Sequence()
        {
            var events = score.Events;
            double t0 = tick;
            double t1 = tick + ticksPerSample * Block;
            int end = score.EndTick;
            if (Finished) { tick = t1; return; }
            while (nextEvent < events.Length && events[nextEvent].Tick < t1 && events[nextEvent].Tick < end)
            {
                int off = (int)((events[nextEvent].Tick - t0) * samplesPerTick);
                if (off < 0) off = 0; else if (off >= Block) off = Block - 1;
                Trigger(ref events[nextEvent], off);
                nextEvent++;
            }
            if (t1 >= end)
            {
                if (!Loop) { Finished = true; tick = t1; return; }
                double loopLen = end - score.LoopStartTick;
                double baseOffsetSamples = (end - t0) * samplesPerTick;
                double nt0 = score.LoopStartTick;
                double nt1 = t1 - loopLen;
                nextEvent = score.LoopStartEvent;
                loops++;
                while (nextEvent < events.Length && events[nextEvent].Tick < nt1)
                {
                    int off = (int)(baseOffsetSamples + (events[nextEvent].Tick - nt0) * samplesPerTick);
                    if (off < 0) off = 0; else if (off >= Block) off = Block - 1;
                    Trigger(ref events[nextEvent], off);
                    nextEvent++;
                }
                tick = nt1;
            }
            else tick = t1;
        }

        void RenderBlock()
        {
            Sequence();

            // Sidechain envelope: jumps to 1 at the kick, decays over the configured release.
            float d = duck;
            int trig = pendingDuckOffset;
            pendingDuckOffset = -1;
            for (int i = 0; i < Block; i++)
            {
                if (i == trig) d = 1f;
                duckEnv[i] = d;
                d *= duckCoef;
            }
            duck = d < 1e-5f ? 0f : d;

            Array.Clear(busL, 0, Block);
            Array.Clear(busR, 0, Block);
            Array.Clear(revL, 0, Block);
            Array.Clear(revR, 0, Block);
            Array.Clear(dlyL, 0, Block);
            Array.Clear(dlyR, 0, Block);
            Array.Clear(dlyOutL, 0, Block);
            Array.Clear(dlyOutR, 0, Block);

            for (int k = 0; k < tracks.Length; k++)
            {
                var t = tracks[k];
                t.Render(Block);
                var spec = t.Spec;
                float tg = TrackGain[k];
                if (tg <= 0f) continue;
                float gl = t.GainL * tg, gr = t.GainR * tg, sRev = spec.Reverb, sDly = spec.Delay, dk = spec.Duck;
                float[] l = t.Left, r = t.Right;
                for (int i = 0; i < Block; i++)
                {
                    float g = dk > 0f ? 1f - dk * duckEnv[i] : 1f;
                    float xl = l[i] * gl * g, xr = r[i] * gr * g;
                    busL[i] += xl;
                    busR[i] += xr;
                    if (sRev > 0f) { revL[i] += xl * sRev; revR[i] += xr * sRev; }
                    if (sDly > 0f) { dlyL[i] += xl * sDly; dlyR[i] += xr * sDly; }
                }
            }

            delay.Process(dlyL, dlyR, dlyOutL, dlyOutR, Block);
            float dw = score.Mix.DelayWet;
            for (int i = 0; i < Block; i++)
            {
                float yl = dlyOutL[i] * dw, yr = dlyOutR[i] * dw;
                busL[i] += yl;
                busR[i] += yr;
                revL[i] += yl * delayToReverb;
                revR[i] += yr * delayToReverb;
            }
            reverb.Process(revL, revR, busL, busR, Block);

            float mg = masterGain;
            for (int i = 0; i < Block; i++) { busL[i] *= mg; busR[i] *= mg; }
            limiter.ProcessStereo(busL, busR, Block);
            for (int i = 0; i < Block; i++)
            {
                outBuf[i * 2] = busL[i];
                outBuf[i * 2 + 1] = busR[i];
            }
        }

        /// <summary>Limiter gain-reduction diagnostics (1 = none) since the last call.</summary>
        public float TakeLimiterMinGain() => limiter.TakeMinGain();
    }
}
