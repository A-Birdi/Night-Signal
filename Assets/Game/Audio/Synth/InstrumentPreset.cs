using System;
using System.Collections.Generic;

namespace NightSignal.AudioSynth
{
    public enum VoiceKind : byte { Subtractive = 0, Supersaw = 1, Fm = 2, Pluck = 3, Drums = 4 }

    public enum DrumPiece : byte
    {
        Kick = 0, Snare = 1, Clap = 2, ClosedHat = 3, OpenHat = 4, Ride = 5, Crash = 6,
        TomHigh = 7, TomMid = 8, TomLow = 9, Rim = 10, Shaker = 11,
    }

    public sealed class EnvelopeSpec
    {
        public float A, D, S, R;
        public EnvelopeSpec(float a, float d, float s, float r) { A = a; D = d; S = s; R = r; }
    }

    /// <summary>Synthesis parameters for one drum piece. Meaning of Tune/Tone depends on the piece.</summary>
    public sealed class DrumPieceSpec
    {
        public float Tune;        // Hz (kick/snare/tom/rim body) or ratio (metals)
        public float Sweep;       // pitch-envelope depth, multiples of Tune
        public float PitchDecay;  // seconds
        public float Decay;       // seconds to -60 dB
        public float Click;       // transient level 0..1
        public float Noise;       // noise mix 0..1
        public float Tone;        // filter frequency, Hz
        public float Drive;       // saturation amount 0..3
        public float Level = 1f;  // linear
        public float Pan;         // -1..1
    }

    /// <summary>
    /// Data-defined instrument (night-signal/instruments@1). Every sound in the score is one of these presets
    /// rendered by our own oscillators, filters and envelopes — no samples.
    /// </summary>
    public sealed class InstrumentPreset
    {
        public const int MaxSuperVoices = 7;

        public string Id = "";
        public VoiceKind Kind = VoiceKind.Subtractive;

        // Oscillators (subtractive)
        public Waveform Osc1 = Waveform.Saw;
        public Waveform Osc2 = Waveform.Saw;
        public float Osc2Semitones;
        public float Osc2DetuneCents = 7f;
        public float Osc2Mix;
        public float SubMix;
        public float NoiseMix;
        public float PulseWidth = 0.5f;

        // Supersaw
        public int SuperVoices = 7;
        public float SuperDetune = 0.2f;
        public float SuperSide = 0.6f;
        public float Width = 0.6f;

        // FM (2-operator, index follows the filter envelope when FmIndexEnv > 0)
        public float FmRatio = 2f;
        public float FmIndex = 1f;
        public float FmIndexEnv;
        public float FmFeedback;
        public float FmCarrierRatio = 1f;

        // Pluck (Karplus-Strong)
        public float PluckDecay = 1.2f;
        public float PluckBright = 0.5f;

        // Filter
        public FilterMode Filter = FilterMode.LowPass;
        public float Cutoff = 8000f;
        public float Resonance = 0.1f;
        public float FilterEnvOct;
        public float KeyTrack;
        public float VelocityOct;
        public float Drive;

        public EnvelopeSpec Amp = new EnvelopeSpec(0.005f, 0.2f, 0.8f, 0.2f);
        public EnvelopeSpec FilterEnv = new EnvelopeSpec(0.002f, 0.3f, 0f, 0.3f);

        // LFO
        public float LfoRate = 5f;
        public float LfoPitchCents;
        public float LfoCutoffOct;
        public float LfoAmp;
        public float LfoPwm;
        public float LfoDelay;

        // Voice handling
        public float Glide;
        public int Polyphony = 8;
        public bool Mono;
        public float GainDb = -12f;
        public float VelocitySens = 0.5f;
        public float Spread;
        public bool ResetPhase;

        // Drums
        public DrumPieceSpec[] Pieces;

        public float Gain => SynthMath.DbToGain(GainDb);

        static Waveform ParseWave(string s, Waveform fallback)
        {
            switch (s)
            {
                case "sine": return Waveform.Sine;
                case "tri": case "triangle": return Waveform.Triangle;
                case "saw": return Waveform.Saw;
                case "square": return Waveform.Square;
                case "pulse": return Waveform.Pulse;
                case "noise": return Waveform.Noise;
                case null: return fallback;
                default: throw new FormatException("unknown waveform '" + s + "'");
            }
        }

        static FilterMode ParseFilter(string s)
        {
            switch (s)
            {
                case "lp": case "lowpass": return FilterMode.LowPass;
                case "bp": case "bandpass": return FilterMode.BandPass;
                case "hp": case "highpass": return FilterMode.HighPass;
                case "off": case "none": return FilterMode.Off;
                default: throw new FormatException("unknown filter mode '" + s + "'");
            }
        }

        static EnvelopeSpec ParseEnv(JsonValue v, EnvelopeSpec fallback)
        {
            if (v == null) return fallback;
            if (!v.IsArray || v.Count != 4) throw new FormatException("envelope must be [attack, decay, sustain, release]");
            return new EnvelopeSpec((float)v[0].Number, (float)v[1].Number, (float)v[2].Number, (float)v[3].Number);
        }

        public static InstrumentPreset FromJson(string id, JsonValue j)
        {
            var p = new InstrumentPreset { Id = id };
            string kind = j.Str("kind", "sub");
            switch (kind)
            {
                case "sub": case "subtractive": p.Kind = VoiceKind.Subtractive; break;
                case "supersaw": p.Kind = VoiceKind.Supersaw; break;
                case "fm": p.Kind = VoiceKind.Fm; break;
                case "pluck": p.Kind = VoiceKind.Pluck; break;
                case "drums": p.Kind = VoiceKind.Drums; break;
                default: throw new FormatException("unknown kind '" + kind + "'");
            }

            if (p.Kind == VoiceKind.Drums)
            {
                p.Pieces = DrumDefaults();
                var pieces = j.Get("pieces");
                if (pieces != null && pieces.Members != null)
                {
                    foreach (var kv in pieces.Members)
                    {
                        int idx;
                        if (!TryDrumLane(kv.Key, out idx)) throw new FormatException("unknown drum piece '" + kv.Key + "'");
                        var d = p.Pieces[idx];
                        var v = kv.Value;
                        d.Tune = v.Num("tune", d.Tune);
                        d.Sweep = v.Num("sweep", d.Sweep);
                        d.PitchDecay = v.Num("pitchDecay", d.PitchDecay);
                        d.Decay = v.Num("decay", d.Decay);
                        d.Click = v.Num("click", d.Click);
                        d.Noise = v.Num("noise", d.Noise);
                        d.Tone = v.Num("tone", d.Tone);
                        d.Drive = v.Num("drive", d.Drive);
                        if (v.Has("level")) d.Level = SynthMath.DbToGain(v.Num("level", 0f));
                        d.Pan = v.Num("pan", d.Pan);
                    }
                }
                p.GainDb = j.Num("gain", 0f);
                return p;
            }

            var osc1 = j.Get("osc1");
            if (osc1 != null) p.Osc1 = ParseWave(osc1.IsString ? osc1.String : osc1.Str("wave"), p.Osc1);
            var osc2 = j.Get("osc2");
            if (osc2 != null)
            {
                p.Osc2 = ParseWave(osc2.Str("wave"), p.Osc2);
                p.Osc2Semitones = osc2.Num("semi", 0f);
                p.Osc2DetuneCents = osc2.Num("detune", p.Osc2DetuneCents);
                p.Osc2Mix = osc2.Num("mix", 0.5f);
            }
            p.SubMix = j.Num("sub", p.SubMix);
            p.NoiseMix = j.Num("noise", p.NoiseMix);
            p.PulseWidth = j.Num("pw", p.PulseWidth);

            p.SuperVoices = Math.Max(1, Math.Min(MaxSuperVoices, j.Int("voices", p.SuperVoices)));
            p.SuperDetune = j.Num("detune", p.SuperDetune);
            p.SuperSide = j.Num("side", p.SuperSide);
            p.Width = j.Num("width", p.Width);

            p.FmRatio = j.Num("ratio", p.FmRatio);
            p.FmCarrierRatio = j.Num("carrierRatio", p.FmCarrierRatio);
            p.FmIndex = j.Num("index", p.FmIndex);
            p.FmIndexEnv = j.Num("indexEnv", p.FmIndexEnv);
            p.FmFeedback = j.Num("feedback", p.FmFeedback);
            if (p.Kind == VoiceKind.Fm && !j.Has("filter")) p.Filter = FilterMode.Off;

            p.PluckDecay = j.Num("pluckDecay", p.PluckDecay);
            p.PluckBright = j.Num("bright", p.PluckBright);

            var f = j.Get("filter");
            if (f != null)
            {
                if (f.Has("mode")) p.Filter = ParseFilter(f.Str("mode"));
                p.Cutoff = f.Num("cutoff", p.Cutoff);
                p.Resonance = f.Num("res", p.Resonance);
                p.FilterEnvOct = f.Num("envOct", p.FilterEnvOct);
                p.KeyTrack = f.Num("keyTrack", p.KeyTrack);
                p.VelocityOct = f.Num("velOct", p.VelocityOct);
            }
            p.Drive = j.Num("drive", p.Drive);
            p.Amp = ParseEnv(j.Get("amp"), p.Amp);
            p.FilterEnv = ParseEnv(j.Get("fenv"), p.FilterEnv);

            var lfo = j.Get("lfo");
            if (lfo != null)
            {
                p.LfoRate = lfo.Num("rate", p.LfoRate);
                p.LfoPitchCents = lfo.Num("pitch", 0f);
                p.LfoCutoffOct = lfo.Num("cutoff", 0f);
                p.LfoAmp = lfo.Num("amp", 0f);
                p.LfoPwm = lfo.Num("pwm", 0f);
                p.LfoDelay = lfo.Num("delay", 0f);
            }

            p.Glide = j.Num("glide", p.Glide);
            p.Mono = j.Flag("mono", p.Mono);
            p.Polyphony = p.Mono ? 1 : Math.Max(1, Math.Min(16, j.Int("poly", p.Polyphony)));
            p.GainDb = j.Num("gain", p.GainDb);
            p.VelocitySens = j.Num("vel", p.VelocitySens);
            p.Spread = j.Num("spread", p.Spread);
            p.ResetPhase = j.Flag("resetPhase", p.ResetPhase);
            return p;
        }

        public static bool TryDrumLane(string name, out int index)
        {
            switch (name)
            {
                case "kick": case "bd": index = (int)DrumPiece.Kick; return true;
                case "snare": case "sd": index = (int)DrumPiece.Snare; return true;
                case "clap": index = (int)DrumPiece.Clap; return true;
                case "chh": case "hat": index = (int)DrumPiece.ClosedHat; return true;
                case "ohh": index = (int)DrumPiece.OpenHat; return true;
                case "ride": index = (int)DrumPiece.Ride; return true;
                case "crash": index = (int)DrumPiece.Crash; return true;
                case "tomh": index = (int)DrumPiece.TomHigh; return true;
                case "tomm": index = (int)DrumPiece.TomMid; return true;
                case "toml": index = (int)DrumPiece.TomLow; return true;
                case "rim": index = (int)DrumPiece.Rim; return true;
                case "shaker": index = (int)DrumPiece.Shaker; return true;
                default: index = -1; return false;
            }
        }

        static DrumPieceSpec[] DrumDefaults()
        {
            var d = new DrumPieceSpec[12];
            d[(int)DrumPiece.Kick] = new DrumPieceSpec { Tune = 50f, Sweep = 3.5f, PitchDecay = 0.04f, Decay = 0.45f, Click = 0.4f, Drive = 1.2f };
            d[(int)DrumPiece.Snare] = new DrumPieceSpec { Tune = 185f, Decay = 0.22f, Noise = 0.65f, Tone = 7000f, Level = 0.7f };
            d[(int)DrumPiece.Clap] = new DrumPieceSpec { Tone = 1150f, Decay = 0.2f, Level = 0.6f };
            d[(int)DrumPiece.ClosedHat] = new DrumPieceSpec { Tune = 1f, Decay = 0.05f, Noise = 0.35f, Tone = 9000f, Level = 0.28f, Pan = -0.2f };
            d[(int)DrumPiece.OpenHat] = new DrumPieceSpec { Tune = 1f, Decay = 0.35f, Noise = 0.35f, Tone = 8500f, Level = 0.24f, Pan = -0.2f };
            d[(int)DrumPiece.Ride] = new DrumPieceSpec { Tune = 0.8f, Decay = 1.3f, Noise = 0.2f, Tone = 5200f, Level = 0.2f, Pan = 0.3f };
            d[(int)DrumPiece.Crash] = new DrumPieceSpec { Tune = 0.9f, Decay = 1.8f, Noise = 0.6f, Tone = 4500f, Level = 0.3f, Pan = -0.3f };
            d[(int)DrumPiece.TomHigh] = new DrumPieceSpec { Tune = 190f, Sweep = 0.6f, PitchDecay = 0.08f, Decay = 0.35f, Noise = 0.08f, Level = 0.55f, Pan = 0.25f };
            d[(int)DrumPiece.TomMid] = new DrumPieceSpec { Tune = 140f, Sweep = 0.6f, PitchDecay = 0.09f, Decay = 0.4f, Noise = 0.08f, Level = 0.55f };
            d[(int)DrumPiece.TomLow] = new DrumPieceSpec { Tune = 100f, Sweep = 0.6f, PitchDecay = 0.1f, Decay = 0.45f, Noise = 0.08f, Level = 0.55f, Pan = -0.25f };
            d[(int)DrumPiece.Rim] = new DrumPieceSpec { Tune = 820f, Decay = 0.04f, Noise = 0.4f, Tone = 1900f, Level = 0.4f, Pan = 0.15f };
            d[(int)DrumPiece.Shaker] = new DrumPieceSpec { Decay = 0.07f, Tone = 6500f, Level = 0.18f, Pan = 0.35f };
            return d;
        }
    }

    /// <summary>Parsed night-signal/instruments@1 preset library, with single inheritance via "extends".</summary>
    public sealed class InstrumentLibrary
    {
        public const string Schema = "night-signal/instruments@1";
        readonly Dictionary<string, InstrumentPreset> presets = new Dictionary<string, InstrumentPreset>();
        readonly Dictionary<string, JsonValue> raw = new Dictionary<string, JsonValue>();

        public static readonly InstrumentLibrary Empty = new InstrumentLibrary();

        public int Count => presets.Count;
        public IEnumerable<string> Ids => presets.Keys;

        public bool TryGet(string id, out InstrumentPreset preset) => presets.TryGetValue(id, out preset);

        public static InstrumentLibrary Parse(string json)
        {
            var root = Json.Parse(json);
            if (root.Str("schema") != Schema) throw new FormatException("instrument library schema must be " + Schema);
            var lib = new InstrumentLibrary();
            lib.AddAll(root.Get("presets"), "presets");
            return lib;
        }

        /// <summary>Copy of this library with extra presets (a score's inline "instruments") layered on top.</summary>
        public InstrumentLibrary WithOverrides(JsonValue presetsObject)
        {
            var lib = new InstrumentLibrary();
            foreach (var kv in raw) lib.raw[kv.Key] = kv.Value;
            foreach (var kv in presets) lib.presets[kv.Key] = kv.Value;
            if (presetsObject != null) lib.AddAll(presetsObject, "instruments");
            return lib;
        }

        void AddAll(JsonValue obj, string where)
        {
            if (obj == null || !obj.IsObject) throw new FormatException(where + " must be an object");
            foreach (var kv in obj.Members) raw[kv.Key] = kv.Value;
            foreach (var kv in obj.Members)
            {
                try
                {
                    presets[kv.Key] = InstrumentPreset.FromJson(kv.Key, Resolve(kv.Key, 0));
                }
                catch (FormatException e)
                {
                    throw new FormatException("preset '" + kv.Key + "': " + e.Message);
                }
            }
        }

        JsonValue Resolve(string id, int depth)
        {
            if (depth > 8) throw new FormatException("'extends' chain too deep at " + id);
            JsonValue v;
            if (!raw.TryGetValue(id, out v)) throw new FormatException("unknown preset '" + id + "'");
            string parent = v.Str("extends");
            if (parent == null) return v;
            return JsonValue.Merge(Resolve(parent, depth + 1), v);
        }
    }
}
