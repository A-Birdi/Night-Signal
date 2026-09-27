using System;
using System.Collections.Generic;

namespace NightSignal.AudioSynth
{
    /// <summary>A parsed chord symbol: root pitch class, intervals above the root, and bass pitch class.</summary>
    public sealed class Chord
    {
        public string Symbol;
        public int Root;      // 0..11, C = 0
        public int Bass;      // 0..11 (slash bass or root)
        public int[] Intervals;

        public bool HasInterval(int semis)
        {
            for (int i = 0; i < Intervals.Length; i++) if (Intervals[i] % 12 == semis % 12) return true;
            return false;
        }

        /// <summary>Third above root (3 or 4); falls back to 4 for sus/power chords' melodic use.</summary>
        public int Third
        {
            get
            {
                for (int i = 0; i < Intervals.Length; i++) if (Intervals[i] == 3 || Intervals[i] == 4) return Intervals[i];
                for (int i = 0; i < Intervals.Length; i++) if (Intervals[i] == 2 || Intervals[i] == 5) return Intervals[i];
                return 4;
            }
        }

        public int Fifth
        {
            get
            {
                for (int i = 0; i < Intervals.Length; i++) if (Intervals[i] == 6 || Intervals[i] == 7 || Intervals[i] == 8) return Intervals[i];
                return 7;
            }
        }

        public int Seventh
        {
            get
            {
                for (int i = 0; i < Intervals.Length; i++) if (Intervals[i] == 10 || Intervals[i] == 11 || Intervals[i] == 9) return Intervals[i];
                return Third == 3 ? 10 : 10;
            }
        }
    }

    /// <summary>A progression placed on the step grid: chord i covers steps [Start[i], Start[i] + Length[i]).</summary>
    public sealed class Progression
    {
        public readonly List<Chord> Chords = new List<Chord>();
        public readonly List<int> Start = new List<int>();
        public readonly List<int> Length = new List<int>();
        public int TotalSteps;

        /// <summary>Chord index sounding at <paramref name="step"/> (progression repeats cyclically).</summary>
        public int IndexAt(int step)
        {
            if (TotalSteps <= 0) return -1;
            int s = step % TotalSteps;
            if (s < 0) s += TotalSteps;
            for (int i = Chords.Count - 1; i >= 0; i--) if (Start[i] <= s) return i;
            return 0;
        }

        public Chord ChordAt(int step)
        {
            int i = IndexAt(step);
            return i < 0 ? null : Chords[i];
        }

        /// <summary>Step (absolute, not wrapped) where the chord sounding at <paramref name="step"/> ends.</summary>
        public int NextChangeAfter(int step)
        {
            int i = IndexAt(step);
            int cycle = step - ((step % TotalSteps) + TotalSteps) % TotalSteps;
            return cycle + Start[i] + Length[i];
        }
    }

    public static class MusicTheory
    {
        static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static string NoteName(int midi) => Names[((midi % 12) + 12) % 12] + (midi / 12 - 1);

        /// <summary>Parses a letter + accidental prefix ("C", "F#", "Bb") into a pitch class; returns chars consumed or 0.</summary>
        public static int ParsePitchClass(string s, int start, out int pc)
        {
            pc = 0;
            if (start >= s.Length) return 0;
            char c = char.ToUpperInvariant(s[start]);
            switch (c)
            {
                case 'C': pc = 0; break;
                case 'D': pc = 2; break;
                case 'E': pc = 4; break;
                case 'F': pc = 5; break;
                case 'G': pc = 7; break;
                case 'A': pc = 9; break;
                case 'B': pc = 11; break;
                default: return 0;
            }
            int used = 1;
            while (start + used < s.Length)
            {
                char a = s[start + used];
                if (a == '#') { pc++; used++; }
                else if (a == 'b' && start + used + 1 <= s.Length) { pc--; used++; }
                else break;
            }
            pc = ((pc % 12) + 12) % 12;
            return used;
        }

        /// <summary>Parses "C4", "F#5", "Bb2", "C-1" or a bare MIDI number into a MIDI note. Returns false on error.</summary>
        public static bool TryParseNote(string s, out int midi)
        {
            midi = 0;
            if (string.IsNullOrEmpty(s)) return false;
            if (char.IsDigit(s[0]))
            {
                return int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out midi)
                       && midi >= 0 && midi <= 127;
            }
            int pc;
            char first = char.ToUpperInvariant(s[0]);
            if (first < 'A' || first > 'G') return false;
            // Letter, then accidentals ('#' or 'b'), then octave. 'b' directly after the letter is a flat.
            int natural;
            switch (first)
            {
                case 'C': natural = 0; break;
                case 'D': natural = 2; break;
                case 'E': natural = 4; break;
                case 'F': natural = 5; break;
                case 'G': natural = 7; break;
                case 'A': natural = 9; break;
                default: natural = 11; break;
            }
            int i = 1, acc = 0;
            while (i < s.Length && (s[i] == '#' || s[i] == 'b')) { acc += s[i] == '#' ? 1 : -1; i++; }
            if (i >= s.Length) return false;
            int octave;
            if (!int.TryParse(s.Substring(i), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out octave))
                return false;
            pc = natural + acc;
            midi = (octave + 1) * 12 + pc;
            return midi >= 0 && midi <= 127;
        }

        public static bool TryParseChord(string symbol, out Chord chord, out string error)
        {
            chord = null;
            error = null;
            if (string.IsNullOrEmpty(symbol)) { error = "empty chord"; return false; }
            int root;
            int used = ParsePitchClass(symbol, 0, out root);
            if (used == 0) { error = "chord '" + symbol + "' has no root"; return false; }
            string rest = symbol.Substring(used);
            int bass = root;
            int slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                string b = rest.Substring(slash + 1);
                int bu = ParsePitchClass(b, 0, out bass);
                if (bu == 0 || bu != b.Length) { error = "chord '" + symbol + "' has a bad slash bass"; return false; }
                rest = rest.Substring(0, slash);
            }
            int[] iv = Quality(rest);
            if (iv == null) { error = "chord '" + symbol + "' has unknown quality '" + rest + "'"; return false; }
            chord = new Chord { Symbol = symbol, Root = root, Bass = bass, Intervals = iv };
            return true;
        }

        static int[] Quality(string q)
        {
            switch (q)
            {
                case "": case "maj": case "M": return new[] { 0, 4, 7 };
                case "m": case "min": case "-": return new[] { 0, 3, 7 };
                case "5": return new[] { 0, 7, 12 };
                case "7": return new[] { 0, 4, 7, 10 };
                case "maj7": case "M7": return new[] { 0, 4, 7, 11 };
                case "m7": case "min7": return new[] { 0, 3, 7, 10 };
                case "mM7": return new[] { 0, 3, 7, 11 };
                case "9": return new[] { 0, 4, 7, 10, 14 };
                case "m9": return new[] { 0, 3, 7, 10, 14 };
                case "maj9": case "M9": return new[] { 0, 4, 7, 11, 14 };
                case "add9": return new[] { 0, 4, 7, 14 };
                case "madd9": return new[] { 0, 3, 7, 14 };
                case "6": return new[] { 0, 4, 7, 9 };
                case "m6": return new[] { 0, 3, 7, 9 };
                case "69": return new[] { 0, 4, 7, 9, 14 };
                case "m69": return new[] { 0, 3, 7, 9, 14 };
                case "11": return new[] { 0, 7, 10, 14, 17 };
                case "m11": return new[] { 0, 3, 7, 10, 14, 17 };
                case "13": return new[] { 0, 4, 10, 14, 21 };
                case "maj7#11": case "M7#11": return new[] { 0, 4, 7, 11, 18 };
                case "sus2": return new[] { 0, 2, 7 };
                case "sus4": case "sus": return new[] { 0, 5, 7 };
                case "7sus4": return new[] { 0, 5, 7, 10 };
                case "dim": return new[] { 0, 3, 6 };
                case "dim7": return new[] { 0, 3, 6, 9 };
                case "m7b5": return new[] { 0, 3, 6, 10 };
                case "aug": case "+": return new[] { 0, 4, 8 };
                case "7b9": return new[] { 0, 4, 7, 10, 13 };
                default: return null;
            }
        }

        /// <summary>
        /// Parses "Am | F G | C:12 G:4 | %" onto a step grid: bars split by '|', chords in a bar share it equally
        /// unless given explicit step lengths; '%' repeats the previous bar.
        /// </summary>
        public static Progression ParseProgression(string text, int stepsPerBar, List<string> errors, string where)
        {
            var prog = new Progression();
            if (string.IsNullOrWhiteSpace(text)) { errors.Add(where + ": empty progression"); return prog; }
            string[] bars = text.Split('|');
            List<string> previousBar = null;
            int step = 0;
            for (int b = 0; b < bars.Length; b++)
            {
                var tokens = new List<string>();
                foreach (var t in bars[b].Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)) tokens.Add(t);
                if (tokens.Count == 0) { errors.Add(where + ": empty bar " + (b + 1)); continue; }
                if (tokens.Count == 1 && tokens[0] == "%")
                {
                    if (previousBar == null) { errors.Add(where + ": '%' in first bar"); continue; }
                    tokens = previousBar;
                }
                int explicitSum = 0, implicitCount = 0;
                var lens = new int[tokens.Count];
                var syms = new string[tokens.Count];
                for (int i = 0; i < tokens.Count; i++)
                {
                    string tk = tokens[i];
                    int colon = tk.IndexOf(':');
                    if (colon >= 0)
                    {
                        int l;
                        if (!int.TryParse(tk.Substring(colon + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out l) || l <= 0)
                        { errors.Add(where + ": bad chord length in '" + tk + "'"); l = 1; }
                        lens[i] = l;
                        explicitSum += l;
                        syms[i] = tk.Substring(0, colon);
                    }
                    else
                    {
                        lens[i] = -1;
                        implicitCount++;
                        syms[i] = tk;
                    }
                }
                if (implicitCount > 0)
                {
                    int remaining = stepsPerBar - explicitSum;
                    if (remaining <= 0 || remaining % implicitCount != 0)
                        errors.Add(where + ": bar " + (b + 1) + " chords do not divide the bar evenly");
                    int each = implicitCount > 0 ? Math.Max(1, remaining / implicitCount) : 0;
                    for (int i = 0; i < lens.Length; i++) if (lens[i] < 0) lens[i] = each;
                }
                int sum = 0;
                for (int i = 0; i < lens.Length; i++) sum += lens[i];
                if (sum != stepsPerBar)
                    errors.Add(where + ": bar " + (b + 1) + " chord lengths sum to " + sum + " steps, expected " + stepsPerBar);
                for (int i = 0; i < syms.Length; i++)
                {
                    Chord ch;
                    string err;
                    if (!TryParseChord(syms[i], out ch, out err)) { errors.Add(where + ": " + err); ch = new Chord { Symbol = "C", Root = 0, Bass = 0, Intervals = new[] { 0, 4, 7 } }; }
                    prog.Chords.Add(ch);
                    prog.Start.Add(step);
                    prog.Length.Add(lens[i]);
                    step += lens[i];
                }
                previousBar = tokens;
            }
            prog.TotalSteps = step;
            return prog;
        }

        /// <summary>Root-position close voicing: root placed in <paramref name="octave"/>, chord tones ascending.</summary>
        public static int[] CloseVoicing(Chord c, int octave)
        {
            int rootMidi = (octave + 1) * 12 + c.Root;
            var v = new int[c.Intervals.Length];
            for (int i = 0; i < v.Length; i++) v[i] = rootMidi + c.Intervals[i];
            Array.Sort(v);
            return v;
        }

        /// <summary>
        /// Inversion of the chord (within an octave-wrapped close position) whose notes move least from
        /// <paramref name="previous"/>, kept near <paramref name="centre"/>. Gives smooth pad voice-leading.
        /// </summary>
        public static int[] LeadVoicing(Chord c, int[] previous, int centre)
        {
            int n = c.Intervals.Length;
            int[] best = null;
            float bestCost = float.MaxValue;
            var pcs = new int[n];
            for (int i = 0; i < n; i++) pcs[i] = (c.Root + c.Intervals[i]) % 12;
            for (int inv = 0; inv < n; inv++)
            {
                for (int shift = -2; shift <= 2; shift++)
                {
                    var v = new int[n];
                    int baseMidi = (centre / 12 + shift) * 12 + pcs[inv] - 12;
                    v[0] = baseMidi;
                    for (int k = 1; k < n; k++)
                    {
                        int pc = pcs[(inv + k) % n];
                        int m = v[k - 1] + ((pc - v[k - 1] % 12) + 12) % 12;
                        if (m == v[k - 1]) m += 12;
                        v[k] = m;
                    }
                    float mean = 0f;
                    for (int k = 0; k < n; k++) mean += v[k];
                    mean /= n;
                    float cost = Math.Abs(mean - centre) * 0.6f;
                    if (previous != null && previous.Length > 0)
                    {
                        for (int k = 0; k < n; k++)
                        {
                            int dmin = int.MaxValue;
                            for (int j = 0; j < previous.Length; j++) dmin = Math.Min(dmin, Math.Abs(v[k] - previous[j]));
                            cost += dmin;
                        }
                    }
                    if (v[0] < 36 || v[n - 1] > 96) cost += 100f;
                    if (cost < bestCost) { bestCost = cost; best = v; }
                }
            }
            return best;
        }
    }
}
