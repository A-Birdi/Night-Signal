using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NightSignal.AudioSynth;

namespace NightSignal.AudioTool
{
    /// <summary>
    /// ns-audio: renders Night Signal's synthesized music/engine/SFX and runs objective checks.
    ///   check      validate + render everything to Tools/audio/out + all checks (default; exit 1 on failure)
    ///   validate   compile every score, print errors/warnings
    ///   cue ID     render one cue (full length + one loop) and print its metrics
    ///   engines    render engine/road/impact scenarios and print metrics
    ///   sfx        render UI cues
    ///   bench      CPU cost only
    ///   normalize  adjust each score's mix.master toward the loudness target
    ///   manifest   (re)write Assets/Content/Data/authored/music.cues.json
    /// Listening quality is NOT judged by any of these.
    /// </summary>
    public static class Program
    {
        public const int SR = 48000;
        public const double TargetLufs = -16.0;
        public const double LufsTolerance = 1.5;
        public const double PeakLimitDb = -0.3;
        public const string ManifestSchema = "night-signal/music-cues@1";

        static string root, scoresDir, outDir, manifestPath;
        static int failures;

        public static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            root = FindRoot();
            if (root == null) { Console.Error.WriteLine("Cannot find the repository root (Assets/Game/Audio)."); return 2; }
            scoresDir = Path.Combine(root, "Assets", "Content", "Audio", "Scores");
            outDir = Path.Combine(root, "Tools", "audio", "out");
            manifestPath = Path.Combine(root, "Assets", "Content", "Data", "authored", "music.cues.json");
            string cmd = args.Length > 0 ? args[0] : "check";
            bool wav = !args.Contains("--no-wav");
            // Music previews are large (~25 MB each); 'check' writes them only with --wav.
            bool musicWav = cmd == "check" ? args.Contains("--wav") : wav;
            switch (cmd)
            {
                case "validate": Validate(); break;
                case "cue": if (args.Length < 2) return Usage(); RunCues(args[1], musicWav, false); break;
                case "cues": RunCues(null, musicWav, false); break;
                case "engines": Scenarios.RunVehicles(outDir, wav, ref failures); break;
                case "sfx": Scenarios.RunSfx(outDir, wav, ref failures); break;
                case "bench": Bench(); break;
                case "stems": if (args.Length < 2) return Usage(); Stems(args[1]); break;
                case "harmony": Harmony(args.Length > 1 && args[1] != "--no-wav" ? args[1] : null, true); break;
                case "normalize": Normalize(args.Length > 1 && args[1] != "--no-wav" ? args[1] : null); break;
                case "manifest": WriteManifest(); break;
                case "check":
                    Console.WriteLine("Night Signal audio checks — objective measurements only (listening quality is not verified).");
                    Console.WriteLine("Output: " + outDir + (musicWav ? "" : " (music WAV previews off; add --wav)"));
                    Validate();
                    RunCues(null, musicWav, true);
                    Harmony(null, true);
                    Console.WriteLine("  (informational: flagged notes are reviewed by hand; see docs/AUDIO.md)");
                    Scenarios.RunVehicles(outDir, wav, ref failures);
                    Scenarios.RunSfx(outDir, wav, ref failures);
                    Bench();
                    CheckManifest();
                    Console.WriteLine();
                    Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
                    break;
                default: return Usage();
            }
            return failures == 0 ? 0 : 1;
        }

        static int Usage()
        {
            Console.Error.WriteLine("usage: ns-audio [check [--wav]|validate|cue ID|cues|stems ID|harmony [ID]|engines|sfx|bench|normalize [ID]|manifest] [--no-wav]");
            return 2;
        }

        public static void Fail(string message)
        {
            failures++;
            Console.WriteLine("  FAIL: " + message);
        }

        static string FindRoot()
        {
            foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                var d = new DirectoryInfo(start);
                while (d != null)
                {
                    if (Directory.Exists(Path.Combine(d.FullName, "Assets", "Game", "Audio"))) return d.FullName;
                    d = d.Parent;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ scores

        public sealed class CueFile
        {
            public string Path;
            public string Json;
            public CompiledScore Score;
            public JsonValue Meta;
        }

        static InstrumentLibrary library;

        public static InstrumentLibrary Library()
        {
            if (library == null) library = InstrumentLibrary.Parse(File.ReadAllText(Path.Combine(scoresDir, "instruments.json")));
            return library;
        }

        public static List<CueFile> LoadCues(bool report)
        {
            var list = new List<CueFile>();
            var lib = Library();
            foreach (var file in Directory.GetFiles(scoresDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                if (System.IO.Path.GetFileName(file) == "instruments.json") continue;
                var cf = new CueFile { Path = file, Json = File.ReadAllText(file) };
                try
                {
                    cf.Score = ScoreCompiler.Compile(cf.Json, lib);
                    cf.Meta = Json.Parse(cf.Json).Get("cue");
                }
                catch (ScoreException e)
                {
                    if (report) Fail(System.IO.Path.GetFileName(file) + " does not compile:\n    " + string.Join("\n    ", e.Errors.Take(25)));
                    continue;
                }
                list.Add(cf);
            }
            return list;
        }

        static void Validate()
        {
            Console.WriteLine();
            Console.WriteLine("== Score validation ==");
            var lib = Library();
            Console.WriteLine("instrument presets: " + lib.Count);
            var cues = LoadCues(true);
            var ids = new HashSet<string>();
            foreach (var c in cues)
            {
                if (!ids.Add(c.Score.Id)) Fail("duplicate cue id " + c.Score.Id);
                string expectedFile = c.Score.Id.ToLowerInvariant() + ".json";
                if (System.IO.Path.GetFileName(c.Path) != expectedFile) Fail(c.Score.Id + ": file should be named " + expectedFile);
                if (c.Meta == null) Fail(c.Score.Id + ": missing \"cue\" metadata (displayTitle/category/contexts)");
                foreach (var w in c.Score.Warnings) Console.WriteLine("  warn " + c.Score.Id + ": " + w);
            }
            Console.WriteLine("scores compiled: " + cues.Count);
            foreach (var need in RequiredCues)
                if (!ids.Contains(need)) Fail("required cue missing: " + need);
            Console.WriteLine("required cue inventory: " + RequiredCues.Count(ids.Contains) + "/" + RequiredCues.Length);
        }

        public static readonly string[] RequiredCues =
        {
            "MUS_TITLE", "MUS_MENU_A", "MUS_MENU_B", "MUS_GARAGE", "MUS_MEET", "MUS_TUTORIAL",
            "MUS_RESULTS_WIN", "MUS_RESULTS_LOSS",
            "MUS_RACE_MIZUHANA", "MUS_RACE_KASUMI", "MUS_RACE_KUROGAWA", "MUS_RACE_AKEBONO", "MUS_RACE_HOSHIMI", "MUS_RACE_TSUKISHIRO",
            "MUS_LT_DAIGO", "MUS_LT_EMI", "MUS_LT_JUN", "MUS_LT_MAKO",
            "MUS_PENULTIMATE", "MUS_FINAL_REINA", "MUS_FINAL_SHIORI",
            "MUS_TT_MEAN", "MUS_TT_BEST", "MUS_TT_DRIFT",
        };

        public sealed class CueMetrics
        {
            public string Id;
            public double Intro, Loop, Rendered, PeakDb, RmsDb, Lufs, SeamStep, LocalStep, SpectralMatch, Silence, CpuMsPerSec, LimiterGrDb;
            public bool Deterministic;
            public int Events;
        }

        static bool IsResults(CueFile c) => c.Score.Id.StartsWith("MUS_RESULTS", StringComparison.Ordinal);

        public static CueMetrics Measure(CueFile c, bool wav, bool detCheck)
        {
            var s = c.Score;
            var m = new CueMetrics { Id = s.Id, Intro = s.IntroSeconds, Loop = s.LoopSeconds, Events = s.Events.Length };
            int introF = (int)Math.Round(s.IntroSeconds * SR);
            int seamF = (int)Math.Round(s.TotalSeconds * SR);
            int loopF = seamF - introF;
            int frames = seamF + loopF;
            m.Rendered = frames / (double)SR;
            var buf = new float[frames * 2];
            var player = new ScorePlayer(s, SR);
            var tmp = new float[1024 * 2];
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < frames; f += 1024)
            {
                int n = Math.Min(1024, frames - f);
                player.Render(tmp, n, 2);
                Array.Copy(tmp, 0, buf, f * 2, n * 2);
            }
            sw.Stop();
            m.CpuMsPerSec = sw.Elapsed.TotalMilliseconds / m.Rendered;
            m.LimiterGrDb = Analysis.Db(player.TakeLimiterMinGain());
            m.PeakDb = Analysis.Db(Analysis.Peak(buf, 0, buf.Length));
            m.RmsDb = Analysis.Db(Analysis.Rms(buf, introF * 2, seamF * 2));
            var loopRegion = new float[loopF * 2];
            Array.Copy(buf, introF * 2, loopRegion, 0, loopF * 2);
            m.Lufs = Analysis.IntegratedLoudness(loopRegion, loopF, 2, SR);
            Analysis.SeamJump(buf, 2, seamF, SR, out m.SeamStep, out m.LocalStep);
            int seg = Math.Min(6 * SR, loopF - SR);
            var a = Analysis.MonoOf(buf, 2, introF + SR / 2, seg);
            var b = Analysis.MonoOf(buf, 2, seamF + SR / 2, seg);
            var ba = Analysis.ThirdOctaveBands(Analysis.AverageSpectrum(a, 0, seg, 4096), 4096, SR);
            var bb = Analysis.ThirdOctaveBands(Analysis.AverageSpectrum(b, 0, seg, 4096), 4096, SR);
            m.SpectralMatch = Analysis.Correlation(ba, bb);
            m.Silence = Analysis.LongestSilence(buf, 2, introF, seamF, SR);
            if (detCheck)
            {
                int dn = 6 * SR;
                var x1 = RenderWithBlock(s, dn, 1024);
                var x2 = RenderWithBlock(s, dn, 333);
                m.Deterministic = Analysis.Hash(x1, x1.Length) == Analysis.Hash(x2, x2.Length)
                                  && Analysis.Hash(x1, x1.Length) == Analysis.Hash(buf, dn * 2);
            }
            else m.Deterministic = true;
            // Preview = the whole piece once plus 6 s past the loop seam (enough to hear the seam).
            if (wav) Wav.Write(System.IO.Path.Combine(outDir, "music", s.Id.ToLowerInvariant() + ".wav"), buf, Math.Min(frames, seamF + 6 * SR), 2, SR);
            return m;
        }

        static float[] RenderWithBlock(CompiledScore s, int frames, int block)
        {
            var p = new ScorePlayer(s, SR);
            var outBuf = new float[frames * 2];
            var tmp = new float[block * 2];
            for (int f = 0; f < frames; f += block)
            {
                int n = Math.Min(block, frames - f);
                p.Render(tmp, n, 2);
                Array.Copy(tmp, 0, outBuf, f * 2, n * 2);
            }
            return outBuf;
        }

        static void RunCues(string only, bool wav, bool strict)
        {
            Console.WriteLine();
            Console.WriteLine("== Music cues: rendered full length + one loop at 48 kHz stereo ==");
            var cues = LoadCues(true);
            if (only != null) cues = cues.Where(c => c.Score.Id.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (only != null && cues.Count == 0) { Fail("no cue " + only); return; }
            Console.WriteLine(string.Format("{0,-20} {1,5} {2,-12} {3,6} {4,6} {5,6} {6,5} {7,7} {8,6} {9,6} {10,6} {11,6} {12,6} {13,5}",
                "cue", "bpm", "key", "intro", "loop", "total", "bars", "peak", "LUFS", "RMS", "seam", "specM", "ms/s", "det"));
            double cpuSum = 0;
            foreach (var c in cues)
            {
                var s = c.Score;
                var m = Measure(c, wav, true);
                cpuSum += m.CpuMsPerSec;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-20} {1,5:0} {2,-12} {3,6:0.0} {4,6:0.0} {5,6:0.0} {6,5} {7,7:0.00} {8,6:0.0} {9,6:0.0} {10,6:0.00} {11,6:0.000} {12,6:0.0} {13,5}",
                    s.Id, s.Tempo, Trim(s.Key, 12), m.Intro, m.Loop, m.Intro + m.Loop, s.TotalBars, m.PeakDb, m.Lufs, m.RmsDb,
                    m.LocalStep > 0 ? m.SeamStep / m.LocalStep : 0, m.SpectralMatch, m.CpuMsPerSec, m.Deterministic ? "yes" : "NO"));
                if (m.PeakDb > PeakLimitDb) Fail(s.Id + ": peak " + m.PeakDb.ToString("0.00") + " dBFS exceeds " + PeakLimitDb);
                if (m.LocalStep > 0 && m.SeamStep > m.LocalStep) Fail(s.Id + ": loop seam step is an outlier");
                if (m.Silence > 2.0) Fail(s.Id + ": " + m.Silence.ToString("0.0") + " s of silence inside the loop");
                if (!m.Deterministic) Fail(s.Id + ": render differs between host buffer sizes");
                double minLoop = IsResults(c) ? 30 : 88;
                if (m.Loop < minLoop) Fail(s.Id + ": loop body " + m.Loop.ToString("0.0") + " s is shorter than " + minLoop + " s");
                if (strict && Math.Abs(m.Lufs - TargetLufs) > LufsTolerance)
                    Fail(s.Id + ": loudness " + m.Lufs.ToString("0.0") + " LUFS outside " + TargetLufs + " ± " + LufsTolerance);
                if (m.CpuMsPerSec > 50) Fail(s.Id + ": CPU " + m.CpuMsPerSec.ToString("0.0") + " ms/s exceeds 5% of a core");
                if (m.SpectralMatch < 0.9) Console.WriteLine("  note " + s.Id + ": loop pass 2 spectrum correlation " + m.SpectralMatch.ToString("0.000"));
            }
            if (cues.Count > 0)
            {
                Console.WriteLine("  seam = largest sample step at the loop seam / largest step within ±50 ms (≤ 1.00 means no discontinuity)");
                Console.WriteLine("  specM = 1/3-octave spectrum correlation, loop start (pass 1) vs loop start (pass 2)");
                Console.WriteLine(string.Format("  average CPU {0:0.0} ms per second of audio ({1:0.00}% of one core) per active cue", cpuSum / cues.Count, cpuSum / cues.Count / 10));
            }
        }

        static string Trim(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n));

        // ------------------------------------------------------------------ stems (per-track balance)

        static void Stems(string id)
        {
            var c = LoadCues(true).FirstOrDefault(x => x.Score.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (c == null) { Fail("no cue " + id); return; }
            var s = c.Score;
            int frames = (int)Math.Round(s.TotalSeconds * SR);
            int introF = (int)Math.Round(s.IntroSeconds * SR);
            Console.WriteLine("== Stems of " + s.Id + " over the loop region (after master gain) ==");
            Console.WriteLine(string.Format("{0,-10} {1,-22} {2,8} {3,8} {4,9} {5,9}", "track", "instrument", "RMS", "peak", "lowRMS", "highRMS"));
            for (int k = -1; k < s.Tracks.Length; k++)
            {
                var p = new ScorePlayer(s, SR);
                if (k >= 0) for (int j = 0; j < s.Tracks.Length; j++) p.TrackGain[j] = j == k ? 1f : 0f;
                var buf = new float[frames * 2];
                var tmp = new float[1024 * 2];
                for (int f = 0; f < frames; f += 1024)
                {
                    int n = Math.Min(1024, frames - f);
                    p.Render(tmp, n, 2);
                    Array.Copy(tmp, 0, buf, f * 2, n * 2);
                }
                var mono = Analysis.MonoOf(buf, 2, introF, frames - introF);
                var mag = Analysis.AverageSpectrum(mono, 0, mono.Length, 4096);
                double lo = 0, hi = 0;
                for (int b = 1; b < mag.Length; b++) { double hz = b * SR / 4096.0; if (hz < 250) lo += mag[b] * mag[b]; else if (hz > 2500) hi += mag[b] * mag[b]; }
                double tot = 0; for (int b = 1; b < mag.Length; b++) tot += mag[b] * mag[b];
                string name = k < 0 ? "(mix)" : s.Tracks[k].Id;
                string inst = k < 0 ? "" : s.Tracks[k].Preset.Id;
                Console.WriteLine(string.Format("{0,-10} {1,-22} {2,8:0.0} {3,8:0.0} {4,8:0}% {5,8:0}%", name, inst,
                    Analysis.Db(Analysis.Rms(buf, introF * 2, frames * 2)), Analysis.Db(Analysis.Peak(buf, introF * 2, frames * 2)),
                    100 * lo / (tot + 1e-30), 100 * hi / (tot + 1e-30)));
            }
        }

        // ------------------------------------------------------------------ harmony audit

        /// <summary>
        /// Flags sustained notes (≥ one beat) whose pitch class is not sounding on any other pitched track but sits a
        /// semitone from one that is (overlap ≥ half the note). A heuristic for authoring slips, not a judgement of taste.
        /// </summary>
        public static int Harmony(string only, bool verbose)
        {
            Console.WriteLine();
            Console.WriteLine("== Harmony audit: sustained semitone clashes between tracks ==");
            int total = 0;
            foreach (var c in LoadCues(true))
            {
                var s = c.Score;
                if (only != null && !s.Id.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                var ev = s.Events;
                int beat = CompiledScore.Ppq;
                var clashes = new List<string>();
                var unpitched = new bool[s.Tracks.Length];
                for (int t = 0; t < s.Tracks.Length; t++)
                    unpitched[t] = s.Tracks[t].Preset.Kind == VoiceKind.Drums
                                   || (s.Tracks[t].Preset.Kind == VoiceKind.Subtractive && s.Tracks[t].Preset.Osc1 == Waveform.Noise);
                for (int i = 0; i < ev.Length; i++)
                {
                    var e = ev[i];
                    if (unpitched[e.Track] || e.Duration < beat) continue;
                    int pc = ((int)e.Pitch % 12 + 12) % 12;
                    int e0 = e.Tick, e1 = e.Tick + e.Duration;
                    var present = new bool[12];
                    var own = new bool[12];
                    for (int j = 0; j < ev.Length; j++)
                    {
                        var f = ev[j];
                        if (f.Tick >= e1) break;
                        if (j == i || unpitched[f.Track]) continue;
                        int ov = Math.Min(e1, f.Tick + f.Duration) - Math.Max(e0, f.Tick);
                        if (ov < e.Duration / 2 || f.Duration < beat / 2) continue;
                        int fpc = ((int)f.Pitch % 12 + 12) % 12;
                        if (f.Track == e.Track) own[fpc] = true; else present[fpc] = true;
                    }
                    if (present[pc]) continue;
                    int up = (pc + 1) % 12, down = (pc + 11) % 12;
                    // A neighbour that the note's own track also sounds is an intentional voicing (maj7, m9...).
                    if ((present[up] && !own[up]) || (present[down] && !own[down]))
                    {
                        int bar = e.Tick / s.TicksPerBar + 1;
                        double beatIn = (e.Tick % s.TicksPerBar) / (double)beat + 1;
                        string sec = "";
                        foreach (var m in s.Sections) if (e.Tick >= m.StartTick) sec = m.Name;
                        clashes.Add(string.Format(CultureInfo.InvariantCulture, "bar {0} beat {1:0.##} ({2}) {3} {4} len {5:0.##} beats",
                            bar, beatIn, sec, s.Tracks[e.Track].Id, MusicTheory.NoteName((int)e.Pitch), e.Duration / (double)beat));
                    }
                }
                total += clashes.Count;
                Console.WriteLine(string.Format("  {0,-20} {1,3} flagged", s.Id, clashes.Count));
                if (verbose) foreach (var l in clashes.Take(40)) Console.WriteLine("      " + l);
            }
            return total;
        }

        // ------------------------------------------------------------------ bench

        static void Bench()
        {
            Console.WriteLine();
            Console.WriteLine("== CPU: full in-race stack (1 music cue + 6 cars x 3 buses + UI bus), 20 s at 48 kHz ==");
            var cues = LoadCues(false);
            var race = cues.FirstOrDefault(c => c.Score.Id == "MUS_RACE_TSUKISHIRO") ?? cues.FirstOrDefault();
            if (race == null) { Console.WriteLine("  (no cues)"); return; }
            var music = new MusicDeck(SR);
            music.Play(new ScorePlayer(race.Score, SR), 0f);
            var cars = new VehicleSoundModel[6];
            var fams = new[] { EngineFamilyKind.Inline4, EngineFamilyKind.Six, EngineFamilyKind.Triple, EngineFamilyKind.Rotary, EngineFamilyKind.Inline4, EngineFamilyKind.Six };
            for (int i = 0; i < 6; i++)
                cars[i] = new VehicleSoundModel(new EngineConfig { Family = fams[i], IdleRpm = 900, RedlineRpm = 7800, Turbo = i % 2 == 0, Seed = (uint)(i + 1) }, SR);
            var sfx = new SfxEngine(SR);
            int block = 1024, total = 20 * SR;
            var buf = new float[block * 2];
            double tMusic = 0, tCars = 0, tSfx = 0;
            var sw = new Stopwatch();
            for (int f = 0; f < total; f += block)
            {
                double t = f / (double)SR;
                for (int i = 0; i < 6; i++)
                {
                    float rpm = 2500 + 4500 * (float)(0.5 + 0.5 * Math.Sin(t * 0.7 + i));
                    cars[i].SetDrive(rpm, 0.8f, 0.8f, 0.7f, 3, false);
                    cars[i].SetRoad(30 + i, 0.3f, TyreSurface.Asphalt, 1f, 0f, 0f);
                }
                if (f % (SR / 2) < block) sfx.Play(UiCue.MenuMove);
                sw.Restart(); music.Render(buf, block, 2); sw.Stop(); tMusic += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                for (int i = 0; i < 6; i++)
                    for (int b = 0; b < 3; b++) { Array.Fill(buf, 1f); cars[i].RenderBus((VehicleBus)b, buf, block, 2, true); }
                sw.Stop(); tCars += sw.Elapsed.TotalMilliseconds;
                sw.Restart(); sfx.Render(buf, block, 2); sw.Stop(); tSfx += sw.Elapsed.TotalMilliseconds;
            }
            double secs = total / (double)SR;
            Console.WriteLine(string.Format("  music ({0}): {1:0.0} ms/s  ({2:0.00}% core)", race.Score.Id, tMusic / secs, tMusic / secs / 10));
            Console.WriteLine(string.Format("  6 cars x 3 buses: {0:0.0} ms/s  ({1:0.00}% core; {2:0.0} ms/s per car)", tCars / secs, tCars / secs / 10, tCars / secs / 6));
            Console.WriteLine(string.Format("  UI bus: {0:0.0} ms/s", tSfx / secs));
            double all = (tMusic + tCars + tSfx) / secs;
            Console.WriteLine(string.Format("  total: {0:0.0} ms/s  ({1:0.00}% of one core)", all, all / 10));
            Console.WriteLine("  note: .NET 10 JIT on this machine; Unity Mono/IL2CPP timings will differ and should be profiled in the editor.");
        }

        // ------------------------------------------------------------------ loudness normalisation

        static void Normalize(string only)
        {
            Console.WriteLine("== Loudness normalisation toward " + TargetLufs + " LUFS (edits mix.master) ==");
            var rx = new Regex("(\"master\"\\s*:\\s*)(-?[0-9]+(\\.[0-9]+)?)");
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (var c in LoadCues(true))
                {
                    if (only != null && !c.Score.Id.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                    double lufs = LoopLoudness(c.Score);
                    double delta = TargetLufs - lufs;
                    if (Math.Abs(delta) < 0.3) continue;
                    var mt = rx.Match(c.Json);
                    if (!mt.Success) { Fail(c.Score.Id + ": no \"master\" value in mix to adjust"); continue; }
                    double master = double.Parse(mt.Groups[2].Value, CultureInfo.InvariantCulture);
                    double nm = Math.Round(master + delta, 1);
                    string json = c.Json.Substring(0, mt.Groups[2].Index) + nm.ToString("0.0", CultureInfo.InvariantCulture) + c.Json.Substring(mt.Groups[2].Index + mt.Groups[2].Length);
                    File.WriteAllText(c.Path, json);
                    Console.WriteLine(string.Format("  pass {0}: {1,-20} {2,6:0.0} LUFS  master {3:0.0} -> {4:0.0}", pass + 1, c.Score.Id, lufs, master, nm));
                }
            }
        }

        public static double LoopLoudness(CompiledScore s)
        {
            int introF = (int)Math.Round(s.IntroSeconds * SR);
            int loopF = (int)Math.Round(s.LoopSeconds * SR);
            var p = new ScorePlayer(s, SR);
            var skip = new float[4096 * 2];
            for (int f = 0; f < introF; f += 4096) p.Render(skip, Math.Min(4096, introF - f), 2);
            var buf = new float[loopF * 2];
            var tmp = new float[4096 * 2];
            for (int f = 0; f < loopF; f += 4096)
            {
                int n = Math.Min(4096, loopF - f);
                p.Render(tmp, n, 2);
                Array.Copy(tmp, 0, buf, f * 2, n * 2);
            }
            return Analysis.IntegratedLoudness(buf, loopF, 2, SR);
        }

        // ------------------------------------------------------------------ manifest

        static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string BuildManifest(List<CueFile> cues, Dictionary<string, double> lufs)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": " + Q(ManifestSchema) + ",\n");
            sb.Append("  \"generatedBy\": \"Tools/audio: dotnet run --project Tools/audio/AudioTool -c Release -- manifest\",\n");
            sb.Append("  \"note\": \"Generated from Assets/Content/Audio/Scores/*.json; do not hand-edit. Loop points are exact score positions. Unlock rules are defined elsewhere.\",\n");
            sb.Append("  \"sampleRate\": " + SR + ",\n");
            sb.Append("  \"ppq\": " + CompiledScore.Ppq + ",\n");
            sb.Append("  \"loudnessTargetLufs\": " + TargetLufs.ToString("0.0", CultureInfo.InvariantCulture) + ",\n");
            sb.Append("  \"cues\": [\n");
            var ordered = cues.OrderBy(c => Array.IndexOf(RequiredCues, c.Score.Id) < 0 ? 999 : Array.IndexOf(RequiredCues, c.Score.Id)).ThenBy(c => c.Score.Id, StringComparer.Ordinal).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                var s = c.Score;
                var meta = c.Meta;
                var ctx = new List<string>();
                var cj = meta?.Get("contexts");
                if (cj != null && cj.IsArray) for (int k = 0; k < cj.Count; k++) ctx.Add(cj[k].String);
                double spt = s.SecondsPerTick;
                string rel = "Assets/Content/Audio/Scores/" + System.IO.Path.GetFileName(c.Path);
                sb.Append("    {\n");
                sb.Append("      \"id\": " + Q(s.Id) + ",\n");
                sb.Append("      \"title\": " + Q(s.Title) + ",\n");
                sb.Append("      \"displayTitle\": " + Q(meta?.Str("displayTitle", s.Title) ?? s.Title) + ",\n");
                sb.Append("      \"category\": " + Q(meta?.Str("category", "") ?? "") + ",\n");
                sb.Append("      \"allowedContexts\": [" + string.Join(", ", ctx.Select(Q)) + "],\n");
                sb.Append("      \"score\": " + Q(rel) + ",\n");
                sb.Append("      \"style\": " + Q(s.Style) + ",\n");
                sb.Append("      \"tempo\": " + s.Tempo.ToString("0.##", CultureInfo.InvariantCulture) + ",\n");
                sb.Append("      \"key\": " + Q(s.Key) + ",\n");
                sb.Append("      \"timeSignature\": [" + s.BeatsPerBar + ", " + s.BeatUnit + "],\n");
                sb.Append("      \"form\": " + Q(s.FormSummary()) + ",\n");
                sb.Append("      \"introSeconds\": " + (s.LoopStartTick * spt).ToString("0.000", CultureInfo.InvariantCulture) + ",\n");
                sb.Append("      \"loopStartSeconds\": " + (s.LoopStartTick * spt).ToString("0.000", CultureInfo.InvariantCulture) + ",\n");
                sb.Append("      \"loopEndSeconds\": " + (s.EndTick * spt).ToString("0.000", CultureInfo.InvariantCulture) + ",\n");
                sb.Append("      \"loopLengthSeconds\": " + ((s.EndTick - s.LoopStartTick) * spt).ToString("0.000", CultureInfo.InvariantCulture) + ",\n");
                sb.Append("      \"loopStartTick\": " + s.LoopStartTick + ",\n");
                sb.Append("      \"loopEndTick\": " + s.EndTick + ",\n");
                sb.Append("      \"loopStartSample48k\": " + (long)Math.Round(s.LoopStartTick * spt * SR) + ",\n");
                sb.Append("      \"loopEndSample48k\": " + (long)Math.Round(s.EndTick * spt * SR) + ",\n");
                double l;
                sb.Append("      \"loudnessLufs\": " + (lufs.TryGetValue(s.Id, out l) ? l.ToString("0.0", CultureInfo.InvariantCulture) : "null") + ",\n");
                sb.Append("      \"motifs\": [" + string.Join(", ", s.Motifs.Select(Q)) + "],\n");
                sb.Append("      \"provenance\": \"Original composition for Night Signal; score data synthesized at runtime by NightSignal.AudioSynth (no samples, no third-party material).\"\n");
                sb.Append("    }" + (i + 1 < ordered.Count ? "," : "") + "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        static Dictionary<string, double> MeasureAllLoudness(List<CueFile> cues)
        {
            var d = new Dictionary<string, double>();
            foreach (var c in cues) d[c.Score.Id] = Math.Round(LoopLoudness(c.Score), 1);
            return d;
        }

        static void WriteManifest()
        {
            var cues = LoadCues(true);
            var text = BuildManifest(cues, MeasureAllLoudness(cues));
            File.WriteAllText(manifestPath, text);
            Console.WriteLine("wrote " + manifestPath + " (" + cues.Count + " cues)");
        }

        static void CheckManifest()
        {
            Console.WriteLine();
            Console.WriteLine("== Cue manifest ==");
            if (!File.Exists(manifestPath)) { Fail("manifest missing: run 'manifest'"); return; }
            var cues = LoadCues(false);
            var existing = Json.Parse(File.ReadAllText(manifestPath));
            var arr = existing.Get("cues");
            int ok = 0;
            foreach (var c in cues)
            {
                JsonValue e = null;
                for (int i = 0; arr != null && i < arr.Count; i++) if (arr[i].Str("id") == c.Score.Id) e = arr[i];
                if (e == null) { Fail("manifest lacks " + c.Score.Id); continue; }
                if (e.Int("loopStartTick", -1) != c.Score.LoopStartTick || e.Int("loopEndTick", -1) != c.Score.EndTick)
                { Fail("manifest loop points stale for " + c.Score.Id + " (run 'manifest')"); continue; }
                if (e.Get("allowedContexts") == null || e.Get("allowedContexts").Count == 0) { Fail("manifest: no contexts for " + c.Score.Id); continue; }
                ok++;
            }
            if (arr != null && arr.Count != cues.Count) Fail("manifest has " + arr.Count + " cues, scores have " + cues.Count);
            Console.WriteLine("  " + ok + "/" + cues.Count + " cues match the manifest (" + manifestPath.Substring(root.Length + 1).Replace('\\', '/') + ")");
        }
    }
}
