using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Compiles a night-signal/score@1 JSON document into a <see cref="CompiledScore"/>. Every note comes from
    /// authored data: melodies written note by note, drum patterns written step by step, and chord progressions
    /// whose pads/basslines/arpeggios are expanded by fixed, deterministic rules (voice-leading, chord-degree
    /// rhythms, index patterns). Nothing is random. Validation errors are collected and thrown together.
    /// Load-time only (allocates freely); never call on the audio thread.
    /// </summary>
    public static class ScoreCompiler
    {
        public const string Schema = "night-signal/score@1";

        sealed class PatternNote
        {
            public int Tick;
            public int RawTicks;
            public int Duration;
            public float Pitch;
            public float Velocity;
            public byte Flags;
            public bool Staccato;
        }

        sealed class Pattern
        {
            public string Id;
            public int Track;
            public int Bars;
            public int LengthTicks;
            public bool Pitched;
            public readonly List<PatternNote> Notes = new List<PatternNote>();
        }

        sealed class Ctx
        {
            public CompiledScore Score;
            public List<string> Errors = new List<string>();
            public Dictionary<string, int> TrackIndex = new Dictionary<string, int>();
            public float Swing;
        }

        static readonly Dictionary<string, string> BassStyles = new Dictionary<string, string>
        {
            { "whole", "R---------------" },
            { "half", "R-------R-------" },
            { "root4", "R---R---R---R---" },
            { "pulse8", "R.R.R.R.R.R.R.R." },
            { "octave8", "R.o.R.o.R.o.R.o." },
            { "offbeat", "..R...R...R...R." },
            { "drive16", "RRRRRRRRRRRRRRRR" },
        };

        public static CompiledScore Compile(string json, InstrumentLibrary library)
        {
            var ctx = new Ctx();
            JsonValue root;
            try { root = Json.Parse(json); }
            catch (JsonException e) { throw new ScoreException(new[] { e.Message }); }

            var s = new CompiledScore();
            ctx.Score = s;
            if (root.Str("schema") != Schema) ctx.Errors.Add("schema must be \"" + Schema + "\"");
            s.Id = root.Str("id", "");
            if (s.Id.Length == 0) ctx.Errors.Add("missing id");
            s.Title = root.Str("title", s.Id);
            s.Description = root.Str("description", "");
            s.Key = root.Str("key", "");
            s.Style = root.Str("style", "");
            var motifs = root.Get("motifs");
            if (motifs != null && motifs.IsArray)
            {
                s.Motifs = new string[motifs.Count];
                for (int i = 0; i < motifs.Count; i++) s.Motifs[i] = motifs[i].String ?? "";
            }
            s.Tempo = root.Num("tempo", 0f);
            if (s.Tempo < 40f || s.Tempo > 260f) ctx.Errors.Add("tempo must be 40..260 BPM");
            var ts = root.Get("timeSignature");
            if (ts != null)
            {
                if (!ts.IsArray || ts.Count != 2) ctx.Errors.Add("timeSignature must be [beats, unit]");
                else { s.BeatsPerBar = (int)ts[0].Number; s.BeatUnit = (int)ts[1].Number; }
            }
            s.StepsPerBeat = root.Int("stepsPerBeat", 4);
            if (s.StepsPerBeat <= 0 || CompiledScore.Ppq % s.StepsPerBeat != 0) { ctx.Errors.Add("stepsPerBeat must divide 96"); s.StepsPerBeat = 4; }
            if (s.BeatUnit != 4 && s.BeatUnit != 8) { ctx.Errors.Add("time signature unit must be 4 or 8"); s.BeatUnit = 4; }
            s.StepsPerBar = s.BeatsPerBar * s.StepsPerBeat * 4 / s.BeatUnit;
            s.TicksPerStep = CompiledScore.Ppq / s.StepsPerBeat;
            s.TicksPerBar = s.StepsPerBar * s.TicksPerStep;
            ctx.Swing = SynthMath.Clamp(root.Num("swing", 0f), 0f, 0.5f);

            ParseMix(root.Get("mix"), s.Mix);

            var lib = library ?? InstrumentLibrary.Empty;
            if (root.Has("instruments"))
            {
                try { lib = lib.WithOverrides(root.Get("instruments")); }
                catch (FormatException e) { ctx.Errors.Add("instruments: " + e.Message); }
            }

            // Tracks
            var tracks = new List<TrackSpec>();
            var tj = root.Get("tracks");
            if (tj == null || !tj.IsArray || tj.Count == 0) ctx.Errors.Add("tracks must be a non-empty array");
            else
            {
                for (int i = 0; i < tj.Count; i++)
                {
                    var t = tj[i];
                    var spec = new TrackSpec
                    {
                        Id = t.Str("id", "track" + i),
                        GainDb = t.Num("gain", 0f),
                        Pan = SynthMath.Clamp(t.Num("pan", 0f), -1f, 1f),
                        Reverb = SynthMath.Clamp01(t.Num("reverb", 0f)),
                        Delay = SynthMath.Clamp01(t.Num("delay", 0f)),
                        Duck = SynthMath.Clamp01(t.Num("duck", 0f)),
                        Drive = SynthMath.Clamp01(t.Num("drive", 0f)),
                        DriveTone = t.Num("driveTone", 5000f),
                        SidechainSource = t.Flag("sidechain", false),
                    };
                    string inst = t.Str("instrument");
                    InstrumentPreset preset;
                    if (inst == null || !lib.TryGet(inst, out preset))
                    {
                        ctx.Errors.Add("track '" + spec.Id + "': unknown instrument '" + inst + "'");
                        preset = new InstrumentPreset { Id = "missing" };
                    }
                    spec.Preset = preset;
                    if (ctx.TrackIndex.ContainsKey(spec.Id)) ctx.Errors.Add("duplicate track id '" + spec.Id + "'");
                    ctx.TrackIndex[spec.Id] = tracks.Count;
                    tracks.Add(spec);
                }
            }
            s.Tracks = tracks.ToArray();

            // Patterns
            var patterns = new Dictionary<string, Pattern>();
            var pj = root.Get("patterns");
            if (pj == null || !pj.IsObject) ctx.Errors.Add("patterns must be an object");
            else
            {
                foreach (var kv in pj.Members)
                {
                    var pat = CompilePattern(kv.Key, kv.Value, ctx);
                    if (pat != null) patterns[kv.Key] = pat;
                }
            }

            // Sections and form
            var sj = root.Get("sections");
            var fj = root.Get("form");
            var events = new List<ScoreEvent>();
            var marks = new List<SectionMark>();
            if (sj == null || !sj.IsObject) ctx.Errors.Add("sections must be an object");
            if (fj == null || !fj.IsArray || fj.Count == 0) ctx.Errors.Add("form must be a non-empty array");
            int loopFrom = 0;
            var lf = root.Get("loopFrom");
            if (lf != null && fj != null && fj.IsArray)
            {
                if (lf.IsNumber) loopFrom = (int)lf.Number;
                else if (lf.IsString)
                {
                    loopFrom = -1;
                    for (int i = 0; i < fj.Count; i++) if (fj[i].String == lf.String) { loopFrom = i; break; }
                    if (loopFrom < 0) { ctx.Errors.Add("loopFrom '" + lf.String + "' is not in form"); loopFrom = 0; }
                }
                if (loopFrom < 0 || loopFrom >= fj.Count) { ctx.Errors.Add("loopFrom out of range"); loopFrom = 0; }
            }
            var usedTracks = new bool[s.Tracks.Length];
            if (sj != null && sj.IsObject && fj != null && fj.IsArray)
            {
                int cursor = 0;
                for (int f = 0; f < fj.Count; f++)
                {
                    string name = fj[f].String;
                    var sec = name != null ? sj.Get(name) : null;
                    if (sec == null) { ctx.Errors.Add("form[" + f + "]: unknown section '" + name + "'"); continue; }
                    if (f == loopFrom) s.LoopStartTick = cursor;
                    int bars = sec.Int("bars", 0);
                    if (bars <= 0) { ctx.Errors.Add("section '" + name + "': bars must be > 0"); continue; }
                    int secTicks = bars * s.TicksPerBar;
                    int secTranspose = sec.Int("transpose", 0);
                    marks.Add(new SectionMark { Name = name, StartTick = cursor, Bars = bars, Looping = f >= loopFrom });
                    var play = sec.Get("play");
                    if (play == null || !play.IsArray) ctx.Errors.Add("section '" + name + "': play must be an array");
                    else
                    {
                        var spans = new List<int[]>();
                        for (int k = 0; k < play.Count; k++)
                            Place(play[k], name, cursor, secTicks, secTranspose, patterns, events, usedTracks, ctx, spans);
                        // Two pitched placements on one track sounding together is almost always an authoring slip.
                        for (int a = 0; a < spans.Count; a++)
                            for (int b = a + 1; b < spans.Count; b++)
                                if (spans[a][0] == spans[b][0] && spans[a][1] < spans[b][2] && spans[b][1] < spans[a][2])
                                    s.Warnings.Add("section '" + name + "': overlapping placements on pitched track '" + s.Tracks[spans[a][0]].Id + "'");
                    }
                    cursor += secTicks;
                }
                s.EndTick = cursor;
            }
            s.Sections = marks.ToArray();
            for (int i = 0; i < usedTracks.Length; i++)
                if (!usedTracks[i]) s.Warnings.Add("track '" + s.Tracks[i].Id + "' is never played");

            if (ctx.Errors.Count > 0) throw new ScoreException(ctx.Errors);

            events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Track.CompareTo(b.Track));
            s.Events = events.ToArray();
            s.LoopStartEvent = s.Events.Length;
            for (int i = 0; i < s.Events.Length; i++)
                if (s.Events[i].Tick >= s.LoopStartTick) { s.LoopStartEvent = i; break; }
            if (s.EndTick <= s.LoopStartTick) throw new ScoreException(new[] { "loop region is empty" });
            return s;
        }

        static void ParseMix(JsonValue m, MixSpec mix)
        {
            if (m == null) return;
            mix.MasterDb = m.Num("master", mix.MasterDb);
            mix.CeilingDb = m.Num("ceiling", mix.CeilingDb);
            mix.DuckReleaseBeats = m.Num("duckRelease", mix.DuckReleaseBeats);
            var r = m.Get("reverb");
            if (r != null)
            {
                mix.ReverbSize = r.Num("size", mix.ReverbSize);
                mix.ReverbDamp = r.Num("damp", mix.ReverbDamp);
                mix.ReverbWidth = r.Num("width", mix.ReverbWidth);
                mix.ReverbWet = r.Num("wet", mix.ReverbWet);
            }
            var d = m.Get("delay");
            if (d != null)
            {
                mix.DelayBeats = d.Num("beats", mix.DelayBeats);
                mix.DelayFeedback = d.Num("feedback", mix.DelayFeedback);
                mix.DelayDampHz = d.Num("damp", mix.DelayDampHz);
                mix.DelayWet = d.Num("wet", mix.DelayWet);
                mix.DelayToReverb = d.Num("toReverb", mix.DelayToReverb);
            }
        }

        // ---------------------------------------------------------------- placement

        static void Place(JsonValue item, string section, int secStart, int secTicks, int secTranspose,
                          Dictionary<string, Pattern> patterns, List<ScoreEvent> events, bool[] usedTracks, Ctx ctx, List<int[]> spans)
        {
            string id;
            int at = 0, times = -1, transpose = 0;
            float vel = 1f;
            if (item.IsString)
            {
                if (!ParsePlacement(item.String, out id, out at, out times, out transpose))
                {
                    ctx.Errors.Add("section '" + section + "': bad placement '" + item.String + "'");
                    return;
                }
            }
            else if (item.IsObject)
            {
                id = item.Str("p");
                at = item.Int("at", 0);
                times = item.Int("times", -1);
                transpose = item.Int("transpose", 0);
                vel = item.Num("vel", 1f);
            }
            else { ctx.Errors.Add("section '" + section + "': placement must be a string or object"); return; }

            Pattern pat;
            if (id == null || !patterns.TryGetValue(id, out pat))
            {
                ctx.Errors.Add("section '" + section + "': unknown pattern '" + id + "'");
                return;
            }
            var s = ctx.Score;
            int start = secStart + at * s.TicksPerBar;
            int end = secStart + secTicks;
            if (start >= end) { ctx.Errors.Add("section '" + section + "': '" + id + "' starts after the section ends"); return; }
            if (pat.LengthTicks <= 0) return;
            if (times < 0) times = (end - start + pat.LengthTicks - 1) / pat.LengthTicks;
            usedTracks[pat.Track] = true;
            if (pat.Pitched) spans.Add(new[] { pat.Track, start, Math.Min(end, start + times * pat.LengthTicks) });
            int shift = pat.Pitched ? transpose + secTranspose : 0;
            for (int r = 0; r < times; r++)
            {
                int baseTick = start + r * pat.LengthTicks;
                if (baseTick >= end) break;
                for (int n = 0; n < pat.Notes.Count; n++)
                {
                    var pn = pat.Notes[n];
                    int t = baseTick + pn.Tick;
                    if (t >= end) continue;
                    int dur = Math.Min(pn.Duration, end - t);
                    float pitch = pn.Pitch + shift;
                    if (pat.Pitched && (pitch < 12f || pitch > 120f))
                    {
                        ctx.Errors.Add("pattern '" + id + "' in section '" + section + "': pitch " + pitch + " out of range");
                        continue;
                    }
                    events.Add(new ScoreEvent
                    {
                        Tick = t,
                        Duration = Math.Max(1, dur),
                        Track = (short)pat.Track,
                        Flags = pn.Flags,
                        Pitch = pitch,
                        Velocity = SynthMath.Clamp(pn.Velocity * vel, 0.05f, 1f),
                    });
                }
            }
        }

        /// <summary>"name", "name@bar", "name@bar*times", "name*times", each optionally with "^semitones".</summary>
        static bool ParsePlacement(string text, out string id, out int at, out int times, out int transpose)
        {
            id = text;
            at = 0;
            times = -1;
            transpose = 0;
            int caret = text.IndexOf('^');
            if (caret >= 0)
            {
                if (!int.TryParse(text.Substring(caret + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out transpose)) return false;
                text = text.Substring(0, caret);
            }
            int star = text.IndexOf('*');
            if (star >= 0)
            {
                if (!int.TryParse(text.Substring(star + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out times) || times <= 0) return false;
                text = text.Substring(0, star);
            }
            int atSign = text.IndexOf('@');
            if (atSign >= 0)
            {
                if (!int.TryParse(text.Substring(atSign + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out at) || at < 0) return false;
                text = text.Substring(0, atSign);
            }
            id = text.Trim();
            return id.Length > 0;
        }

        // ---------------------------------------------------------------- patterns

        static int StepToTick(double step, Ctx ctx, float swing)
        {
            var s = ctx.Score;
            int t = (int)Math.Round(step * s.TicksPerStep);
            double whole = Math.Floor(step + 1e-9);
            if (swing > 0f && Math.Abs(step - whole) < 1e-6 && ((long)whole & 1L) == 1L)
                t += (int)Math.Round(swing * s.TicksPerStep);
            return t;
        }

        static Pattern CompilePattern(string id, JsonValue pj, Ctx ctx)
        {
            var s = ctx.Score;
            string where = "pattern '" + id + "'";
            if (pj == null || !pj.IsObject) { ctx.Errors.Add(where + " must be an object"); return null; }
            string track = pj.Str("track");
            int trackIndex;
            if (track == null || !ctx.TrackIndex.TryGetValue(track, out trackIndex))
            {
                ctx.Errors.Add(where + ": unknown track '" + track + "'");
                return null;
            }
            var pat = new Pattern { Id = id, Track = trackIndex, Bars = pj.Int("bars", 0) };
            bool drumsTrack = s.Tracks[trackIndex].Preset.Kind == VoiceKind.Drums;
            float swing = pj.Has("swing") ? SynthMath.Clamp(pj.Num("swing", 0f), 0f, 0.5f) : ctx.Swing;
            float velScale = pj.Num("velocity", 1f);
            int transpose = pj.Int("transpose", 0);

            int kinds = 0;
            foreach (var k in new[] { "melody", "notes", "drums", "chords", "bass", "arp" }) if (pj.Has(k)) kinds++;
            if (kinds != 1) { ctx.Errors.Add(where + ": needs exactly one of melody/notes/drums/chords/bass/arp"); return null; }

            if (pj.Has("drums"))
            {
                if (!drumsTrack) { ctx.Errors.Add(where + ": drums pattern on a non-drum track"); return null; }
                pat.Pitched = false;
                CompileDrums(pat, pj.Get("drums"), velScale, swing, where, ctx);
            }
            else
            {
                if (drumsTrack) { ctx.Errors.Add(where + ": pitched pattern on a drum track"); return null; }
                pat.Pitched = true;
                if (pj.Has("melody")) CompileMelody(pat, pj.Get("melody"), pj.Num("gate", 0.9f), transpose, velScale, swing, where, ctx);
                else if (pj.Has("notes")) CompileNotes(pat, pj.Get("notes"), pj.Num("gate", 1f), transpose, velScale, swing, where, ctx);
                else if (pj.Has("chords")) CompileChords(pat, pj.Get("chords"), transpose, velScale, swing, where, ctx);
                else if (pj.Has("bass")) CompileBass(pat, pj.Get("bass"), transpose, velScale, swing, where, ctx);
                else CompileArp(pat, pj.Get("arp"), transpose, velScale, swing, where, ctx);
            }
            if (pat.Bars <= 0) { ctx.Errors.Add(where + ": could not determine bars"); pat.Bars = 1; }
            pat.LengthTicks = pat.Bars * s.TicksPerBar;
            return pat;
        }

        static string JoinText(JsonValue v)
        {
            if (v == null) return null;
            if (v.IsString) return v.String;
            if (v.IsArray)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < v.Count; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append(v[i].String);
                }
                return sb.ToString();
            }
            return null;
        }

        // Melody notation: tokens "C5:4" (pitch:steps), "r:2" rest, "-:2" tie, '|' bar check.
        // Length suffix 't' = triplet (x 2/3). Modifiers: '>' accent, '\'' soft, '~' glide into, '*' staccato.
        static void CompileMelody(Pattern pat, JsonValue mv, float gate, int transpose, float velScale, float swing, string where, Ctx ctx)
        {
            var s = ctx.Score;
            string text = JoinText(mv);
            if (text == null) { ctx.Errors.Add(where + ": melody must be a string or array of strings"); return; }
            text = text.Replace("|", " | ");
            var tokens = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            double pos = 0, lastLen = 2;
            int bar = 0;
            PatternNote last = null;
            foreach (var raw in tokens)
            {
                if (raw == "|")
                {
                    bar++;
                    double expected = bar * s.StepsPerBar;
                    if (Math.Abs(pos - expected) > 1e-6)
                    {
                        ctx.Errors.Add(where + ": melody bar " + bar + " has " + (pos - (bar - 1) * s.StepsPerBar).ToString("0.###", CultureInfo.InvariantCulture)
                                       + " steps, expected " + s.StepsPerBar);
                        pos = expected;
                    }
                    continue;
                }
                string tok = raw;
                bool accent = false, soft = false, glide = false, stacc = false;
                while (tok.Length > 0)
                {
                    char c = tok[tok.Length - 1];
                    if (c == '>') accent = true;
                    else if (c == '\'') soft = true;
                    else if (c == '~') glide = true;
                    else if (c == '*') stacc = true;
                    else break;
                    tok = tok.Substring(0, tok.Length - 1);
                }
                string head = tok, lenText = null;
                int colon = tok.IndexOf(':');
                if (colon >= 0) { head = tok.Substring(0, colon); lenText = tok.Substring(colon + 1); }
                double len = lastLen;
                if (lenText != null)
                {
                    bool triplet = lenText.EndsWith("t", StringComparison.Ordinal);
                    if (triplet) lenText = lenText.Substring(0, lenText.Length - 1);
                    if (!double.TryParse(lenText, NumberStyles.Float, CultureInfo.InvariantCulture, out len) || len <= 0)
                    {
                        ctx.Errors.Add(where + ": bad length in '" + raw + "'");
                        len = 1;
                    }
                    if (triplet) len = len * 2.0 / 3.0;
                }
                lastLen = len;
                int lenTicks = (int)Math.Round(len * s.TicksPerStep);
                if (head == "r") { pos += len; last = null; continue; }
                if (head == "-")
                {
                    if (last == null) ctx.Errors.Add(where + ": tie '-' without a preceding note");
                    else last.RawTicks += lenTicks;
                    pos += len;
                    continue;
                }
                int midi;
                if (!MusicTheory.TryParseNote(head, out midi))
                {
                    ctx.Errors.Add(where + ": bad note '" + raw + "'");
                    pos += len;
                    continue;
                }
                float vel = accent ? 1f : (soft ? 0.55f : 0.8f);
                last = new PatternNote
                {
                    Tick = StepToTick(pos, ctx, swing),
                    RawTicks = lenTicks,
                    Pitch = midi + transpose,
                    Velocity = vel * velScale,
                    Flags = glide ? ScoreEvent.FlagGlide : (byte)0,
                    Staccato = stacc,
                };
                pat.Notes.Add(last);
                pos += len;
            }
            FinishLength(pat, pos, where, ctx);
            foreach (var n in pat.Notes)
                n.Duration = Math.Max(1, (int)(n.RawTicks * (n.Staccato ? gate * 0.5f : gate)));
        }

        static void FinishLength(Pattern pat, double steps, string where, Ctx ctx)
        {
            var s = ctx.Score;
            if (pat.Bars > 0)
            {
                double expected = pat.Bars * s.StepsPerBar;
                if (Math.Abs(steps - expected) > 1e-6)
                    ctx.Errors.Add(where + ": content is " + steps.ToString("0.###", CultureInfo.InvariantCulture) + " steps, declared "
                                   + pat.Bars + " bars = " + expected + " steps");
            }
            else pat.Bars = Math.Max(1, (int)Math.Ceiling(steps / s.StepsPerBar - 1e-9));
        }

        // Explicit notes: [step, length, pitch, velocity?, "~"?]
        static void CompileNotes(Pattern pat, JsonValue nv, float gate, int transpose, float velScale, float swing, string where, Ctx ctx)
        {
            var s = ctx.Score;
            if (nv == null || !nv.IsArray) { ctx.Errors.Add(where + ": notes must be an array"); return; }
            double maxEnd = 0;
            for (int i = 0; i < nv.Count; i++)
            {
                var n = nv[i];
                if (!n.IsArray || n.Count < 3) { ctx.Errors.Add(where + ": note " + i + " must be [step, length, pitch, velocity?]"); continue; }
                double step = n[0].Number, len = n[1].Number;
                int midi;
                bool ok = n[2].IsNumber ? ((midi = (int)n[2].Number) >= 0 && midi <= 127) : MusicTheory.TryParseNote(n[2].String, out midi);
                if (!ok || len <= 0) { ctx.Errors.Add(where + ": bad note " + i); continue; }
                float vel = n.Count > 3 && n[3].IsNumber ? (float)n[3].Number : 0.8f;
                bool glide = n.Count > 4 && n[4].IsString && n[4].String == "~";
                pat.Notes.Add(new PatternNote
                {
                    Tick = StepToTick(step, ctx, swing),
                    Duration = Math.Max(1, (int)(len * s.TicksPerStep * gate)),
                    Pitch = midi + transpose,
                    Velocity = vel * velScale,
                    Flags = glide ? ScoreEvent.FlagGlide : (byte)0,
                });
                maxEnd = Math.Max(maxEnd, step + len);
            }
            if (pat.Bars <= 0) pat.Bars = Math.Max(1, (int)Math.Ceiling(maxEnd / s.StepsPerBar - 1e-9));
            else if (maxEnd > pat.Bars * s.StepsPerBar + 1e-6) ctx.Errors.Add(where + ": notes extend past " + pat.Bars + " bars");
        }

        // Drum lanes: 'x' hit, 'X' accent, 'o' ghost, 'g' very soft, '.'/'-' rest. '|' and spaces are ignored.
        static void CompileDrums(Pattern pat, JsonValue dv, float velScale, float swing, string where, Ctx ctx)
        {
            var s = ctx.Score;
            if (dv == null || !dv.IsObject) { ctx.Errors.Add(where + ": drums must be an object of lanes"); return; }
            var lanes = new List<KeyValuePair<int, string>>();
            int longest = 0;
            foreach (var kv in dv.Members)
            {
                int lane;
                if (!InstrumentPreset.TryDrumLane(kv.Key, out lane)) { ctx.Errors.Add(where + ": unknown drum lane '" + kv.Key + "'"); continue; }
                string text = JoinText(kv.Value);
                if (text == null) { ctx.Errors.Add(where + ": lane '" + kv.Key + "' must be a string"); continue; }
                var sb = new StringBuilder();
                foreach (char c in text) if (c != '|' && c != ' ') sb.Append(c);
                string clean = sb.ToString();
                if (clean.Length == 0) continue;
                lanes.Add(new KeyValuePair<int, string>(lane, clean));
                longest = Math.Max(longest, clean.Length);
            }
            if (pat.Bars <= 0) pat.Bars = Math.Max(1, (longest + s.StepsPerBar - 1) / s.StepsPerBar);
            int steps = pat.Bars * s.StepsPerBar;
            foreach (var lane in lanes)
            {
                string p = lane.Value;
                if (steps % p.Length != 0) { ctx.Errors.Add(where + ": lane length " + p.Length + " does not divide " + steps + " steps"); continue; }
                for (int st = 0; st < steps; st++)
                {
                    char c = p[st % p.Length];
                    float vel;
                    switch (c)
                    {
                        case 'x': vel = 0.8f; break;
                        case 'X': vel = 1f; break;
                        case 'o': vel = 0.45f; break;
                        case 'g': vel = 0.28f; break;
                        case '.': case '-': continue;
                        default: ctx.Errors.Add(where + ": bad drum character '" + c + "'"); continue;
                    }
                    pat.Notes.Add(new PatternNote
                    {
                        Tick = StepToTick(st, ctx, swing),
                        Duration = s.TicksPerStep,
                        Pitch = lane.Key,
                        Velocity = vel * velScale,
                    });
                }
            }
        }

        static Progression ReadProgression(JsonValue obj, string where, Ctx ctx, out JsonValue options)
        {
            options = obj != null && obj.IsObject ? obj : null;
            string text = obj == null ? null : (obj.IsString ? obj.String : JoinText(obj.Get("progression")));
            if (text == null) { ctx.Errors.Add(where + ": missing progression"); return null; }
            return MusicTheory.ParseProgression(text, ctx.Score.StepsPerBar, ctx.Errors, where);
        }

        static void SetBarsFromProgression(Pattern pat, Progression prog, Ctx ctx)
        {
            if (pat.Bars <= 0) pat.Bars = Math.Max(1, prog.TotalSteps / ctx.Score.StepsPerBar);
        }

        static string ReadRhythm(JsonValue options, string key, string fallback)
        {
            string r = options != null ? JoinText(options.Get(key)) : null;
            if (r == null) r = fallback;
            var sb = new StringBuilder();
            foreach (char c in r) if (c != '|' && c != ' ') sb.Append(c);
            return sb.ToString();
        }

        // Chords/pads: rhythm 'x' strike, '-' hold, '.' silence; a chord change while holding re-strikes.
        static void CompileChords(Pattern pat, JsonValue cv, int transpose, float velScale, float swing, string where, Ctx ctx)
        {
            var s = ctx.Score;
            JsonValue o;
            var prog = ReadProgression(cv, where, ctx, out o);
            if (prog == null || prog.TotalSteps == 0) return;
            SetBarsFromProgression(pat, prog, ctx);
            int octave = o != null ? o.Int("octave", 4) : 4;
            string voicing = o != null ? o.Str("voicing", "lead") : "lead";
            float gate = o != null ? o.Num("gate", 0.98f) : 0.98f;
            float vel = (o != null ? o.Num("velocity", 0.75f) : 0.75f) * velScale;
            bool withBass = o != null && o.Flag("withBass", false);
            string rhythm = ReadRhythm(o, "rhythm", "x" + new string('-', s.StepsPerBar - 1));
            int steps = pat.Bars * s.StepsPerBar;
            int[] prev = null;
            int centre = (octave + 1) * 12 + 7;
            int holdStart = -1, holdChord = -1;
            int[] holdNotes = null;
            for (int st = 0; st <= steps; st++)
            {
                char c = st < steps ? rhythm[st % rhythm.Length] : '.';
                int ci = st < steps ? prog.IndexAt(st) : -1;
                bool strike = c == 'x' || c == 'X' || (c == '-' && holdStart >= 0 && ci != holdChord);
                bool stop = c == '.' || strike || st == steps;
                if (stop && holdStart >= 0)
                {
                    int dur = (int)((st - holdStart) * s.TicksPerStep * gate);
                    foreach (int n in holdNotes)
                        pat.Notes.Add(new PatternNote { Tick = StepToTick(holdStart, ctx, swing), Duration = Math.Max(1, dur), Pitch = n + transpose, Velocity = vel });
                    holdStart = -1;
                }
                if (strike && st < steps)
                {
                    var chord = prog.Chords[ci];
                    int[] v = voicing == "close" ? MusicTheory.CloseVoicing(chord, octave) : MusicTheory.LeadVoicing(chord, prev, centre);
                    prev = v;
                    if (withBass)
                    {
                        var list = new List<int>(v) { (octave) * 12 + chord.Bass };
                        v = list.ToArray();
                    }
                    holdNotes = v;
                    holdStart = st;
                    holdChord = ci;
                }
                else if (c != '-' && c != 'x' && c != 'X' && c != '.' && st < steps)
                    ctx.Errors.Add(where + ": bad chord rhythm character '" + c + "'");
            }
        }

        // Bass: rhythm of chord degrees. R root(bass) o +8va O +2 8va 5 fifth f fifth+8va 3 third 7 seventh
        // 4 fourth 6 sixth 2 ninth L fifth below a/A chromatic approach from below/above to the next chord,
        // '-' hold, '.' rest.
        static void CompileBass(Pattern pat, JsonValue bv, int transpose, float velScale, float swing, string where, Ctx ctx)
        {
            var s = ctx.Score;
            JsonValue o;
            var prog = ReadProgression(bv, where, ctx, out o);
            if (prog == null || prog.TotalSteps == 0) return;
            SetBarsFromProgression(pat, prog, ctx);
            int octave = o != null ? o.Int("octave", 2) : 2;
            float gate = o != null ? o.Num("gate", 0.85f) : 0.85f;
            float vel = (o != null ? o.Num("velocity", 0.85f) : 0.85f) * velScale;
            string style = o != null ? o.Str("style") : null;
            string rhythm;
            if (style != null)
            {
                if (!BassStyles.TryGetValue(style, out rhythm)) { ctx.Errors.Add(where + ": unknown bass style '" + style + "'"); rhythm = BassStyles["root4"]; }
            }
            else rhythm = ReadRhythm(o, "rhythm", BassStyles["root4"]);
            int steps = pat.Bars * s.StepsPerBar;
            PatternNote last = null;
            int lastStart = 0;
            for (int st = 0; st < steps; st++)
            {
                char c = rhythm[st % rhythm.Length];
                if (c == '-') continue;
                if (last != null) { last.Duration = Math.Max(1, (int)((st - lastStart) * s.TicksPerStep * gate)); last = null; }
                if (c == '.') continue;
                var chord = prog.ChordAt(st);
                int root = (octave + 1) * 12 + chord.Root;
                int bass = (octave + 1) * 12 + chord.Bass;
                int pitch;
                switch (c)
                {
                    case 'R': pitch = bass; break;
                    case 'o': pitch = bass + 12; break;
                    case 'O': pitch = bass + 24; break;
                    case '5': pitch = root + chord.Fifth; break;
                    case 'f': pitch = root + chord.Fifth + 12; break;
                    case '3': pitch = root + chord.Third; break;
                    case '7': pitch = root + chord.Seventh; break;
                    case '4': pitch = root + 5; break;
                    case '6': pitch = root + 9; break;
                    case '2': pitch = root + 2; break;
                    case 'L': pitch = root + chord.Fifth - 12; break;
                    case 'a':
                    case 'A':
                    {
                        int change = prog.NextChangeAfter(st);
                        var next = prog.ChordAt(change);
                        int target = (octave + 1) * 12 + next.Bass;
                        if (target - bass > 6) target -= 12;
                        if (bass - target > 6) target += 12;
                        pitch = c == 'a' ? target - 1 : target + 1;
                        break;
                    }
                    default:
                        ctx.Errors.Add(where + ": bad bass rhythm character '" + c + "'");
                        continue;
                }
                bool onBeat = st % s.StepsPerBeat == 0;
                last = new PatternNote
                {
                    Tick = StepToTick(st, ctx, swing),
                    Pitch = pitch + transpose,
                    Velocity = vel * (onBeat ? 1f : 0.86f),
                };
                lastStart = st;
                pat.Notes.Add(last);
            }
            if (last != null) last.Duration = Math.Max(1, (int)((steps - lastStart) * s.TicksPerStep * gate));
        }

        // Arpeggio: index pattern over the chord's tones (indices past the chord wrap up an octave), '.' rest.
        static void CompileArp(Pattern pat, JsonValue av, int transpose, float velScale, float swing, string where, Ctx ctx)
        {
            var s = ctx.Score;
            JsonValue o;
            var prog = ReadProgression(av, where, ctx, out o);
            if (prog == null || prog.TotalSteps == 0) return;
            SetBarsFromProgression(pat, prog, ctx);
            int octave = o != null ? o.Int("octave", 4) : 4;
            double rate = o != null ? o.Num("rate", 1f) : 1.0;
            if (rate <= 0) { ctx.Errors.Add(where + ": arp rate must be > 0"); rate = 1; }
            float gate = o != null ? o.Num("gate", 0.6f) : 0.6f;
            float vel = (o != null ? o.Num("velocity", 0.7f) : 0.7f) * velScale;
            string voicing = o != null ? o.Str("voicing", "close") : "close";
            string reset = o != null ? o.Str("reset", "bar") : "bar";
            string ptext = o != null ? JoinText(o.Get("pattern")) : null;
            if (ptext == null) ptext = "0 1 2 1";
            var toks = ptext.Split(new[] { ' ', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries);
            var idx = new int?[toks.Length];
            for (int i = 0; i < toks.Length; i++)
            {
                if (toks[i] == ".") { idx[i] = null; continue; }
                int v;
                if (!int.TryParse(toks[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) { ctx.Errors.Add(where + ": bad arp index '" + toks[i] + "'"); idx[i] = null; continue; }
                idx[i] = v;
            }
            if (idx.Length == 0) return;
            int steps = pat.Bars * s.StepsPerBar;
            int[] prev = null;
            int centre = (octave + 1) * 12 + 7;
            int lastChord = -1;
            int[] tones = null;
            int chordStartK = 0;
            int count = (int)Math.Floor(steps / rate + 1e-9);
            for (int k = 0; k < count; k++)
            {
                double step = k * rate;
                int st = (int)Math.Floor(step + 1e-9);
                int ci = prog.IndexAt(st);
                if (ci != lastChord)
                {
                    var chord = prog.Chords[ci];
                    tones = voicing == "lead" ? MusicTheory.LeadVoicing(chord, prev, centre) : MusicTheory.CloseVoicing(chord, octave);
                    prev = tones;
                    lastChord = ci;
                    chordStartK = k;
                }
                int pi;
                if (reset == "bar")
                {
                    int perBar = (int)Math.Round(s.StepsPerBar / rate);
                    pi = perBar > 0 ? (k % perBar) % idx.Length : k % idx.Length;
                }
                else if (reset == "chord") pi = (k - chordStartK) % idx.Length;
                else pi = k % idx.Length;
                if (idx[pi] == null) continue;
                int ix = idx[pi].Value;
                int n = tones.Length;
                int oct = ix >= 0 ? ix / n : -((-ix + n - 1) / n);
                int tone = tones[((ix % n) + n) % n] + 12 * oct;
                bool onBeat = Math.Abs(step / s.StepsPerBeat - Math.Round(step / s.StepsPerBeat)) < 1e-6;
                pat.Notes.Add(new PatternNote
                {
                    Tick = StepToTick(step, ctx, swing),
                    Duration = Math.Max(1, (int)(rate * s.TicksPerStep * gate)),
                    Pitch = tone + transpose,
                    Velocity = vel * (onBeat ? 1f : 0.85f),
                });
            }
        }
    }
}
