using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NightSignal.AudioSynth;

namespace NightSignal.AudioTool
{
    /// <summary>Scripted vehicle and UI renders with objective metrics.</summary>
    public static class Scenarios
    {
        const int SR = Program.SR;
        const int Frame = SR / 60; // inputs change at game frame rate, as in Unity

        static readonly float[] FiringPerRev = { 2f, 3f, 1.5f, 2f }; // I4, six, triple, 2-rotor

        delegate void Driver(double t, VehicleSoundModel m);

        static float[] RenderBus(VehicleSoundModel m, VehicleBus bus, double seconds, Driver drive, out double msPerSec)
        {
            int frames = (int)(seconds * SR);
            var mono = new float[frames];
            var sw = new Stopwatch();
            for (int f = 0; f < frames; f += Frame)
            {
                drive(f / (double)SR, m);
                int n = Math.Min(Frame, frames - f);
                sw.Start();
                m.RenderBusMono(bus, mono, f, n);
                sw.Stop();
            }
            msPerSec = sw.Elapsed.TotalMilliseconds / seconds;
            return mono;
        }

        /// <summary>Idle → free-rev sweep to redline → limiter → coast → idle → pull through gears → lift.</summary>
        static Driver EngineSweep(EngineConfig cfg)
        {
            float idle = cfg.IdleRpm, red = cfg.RedlineRpm;
            float[] ratios = { 3.3f, 2.1f, 1.5f, 1.15f, 0.93f };
            double speed = 0, boost = 0, shiftUntil = -1, coastStartRpm = red;
            int gear = 1;
            return (t, m) =>
            {
                float rpm, thr, load;
                bool shifting = false;
                const double dt = 1.0 / 60;
                if (t < 2) { rpm = idle; thr = 0; load = 0; }
                else if (t < 6) { rpm = idle + (red - idle) * (float)((t - 2) / 4); thr = 1; load = 1; }
                else if (t < 7) { rpm = red; thr = 1; load = 1; }
                else if (t < 11) { rpm = (float)(idle * 1.1 + (coastStartRpm - idle * 1.1) * Math.Exp(-(t - 7) * 1.1)); thr = 0; load = 0; }
                else if (t < 12) { rpm = idle; thr = 0; load = 0; speed = 0; gear = 1; }
                else if (t < 22)
                {
                    thr = 1;
                    shifting = t < shiftUntil;
                    load = shifting ? 0 : 1;
                    double accel = shifting ? -0.3 : (6.0 - 0.06 * speed) * (ratios[gear - 1] / 2.0);
                    speed = Math.Max(0, speed + accel * dt);
                    rpm = (float)Math.Max(idle * 1.4, speed * ratios[gear - 1] * 4.1 * 60 / (2 * Math.PI * 0.31));
                    if (!shifting && rpm > red * 0.95f && gear < ratios.Length) { gear++; shiftUntil = t + 0.14; }
                    rpm = Math.Min(rpm, red);
                }
                else { thr = 0; load = 0; rpm = Math.Max(idle, red * 0.6f * (float)Math.Exp(-(t - 22) * 0.8)); }
                double target = thr * Math.Min(1.0, rpm / (red * 0.55));
                boost += (target - boost) * (1 - Math.Exp(-dt / 0.6));
                m.SetDrive(rpm, thr, load, (float)boost, t < 12 ? 0 : gear, shifting);
            };
        }

        static Driver Steady(float rpm, float thr, float load)
        {
            return (t, m) => m.SetDrive(rpm, thr, load, 0.8f, 3, false);
        }

        static double[] Profile(EngineConfig cfg, float rpm, float thr, float load)
        {
            var m = new VehicleSoundModel(cfg, SR);
            double ms;
            var x = RenderBus(m, VehicleBus.Engine, 3.0, Steady(rpm, thr, load), out ms);
            var mag = Analysis.AverageSpectrum(x, SR, 2 * SR, 8192);
            return Analysis.ThirdOctaveBands(mag, 8192, SR);
        }

        public static void RunVehicles(string outDir, bool wav, ref int failures)
        {
            Console.WriteLine();
            Console.WriteLine("== Engine families: idle -> redline sweep -> limiter -> coast -> gear pulls -> lift (24 s, engine bus) ==");
            Console.WriteLine(string.Format("{0,-16} {1,7} {2,7} {3,7} {4,8} {5,9} {6,10}", "engine", "peak", "RMS", "ms/s", "firingHz", "domHz", "firing-dB"));
            var families = new[] { EngineFamilyKind.Inline4, EngineFamilyKind.Six, EngineFamilyKind.Triple, EngineFamilyKind.Rotary };
            var profiles = new Dictionary<EngineFamilyKind, double[]>();
            foreach (var fam in families)
            {
                foreach (bool turbo in new[] { false, true })
                {
                    var cfg = new EngineConfig
                    {
                        Family = fam,
                        IdleRpm = fam == EngineFamilyKind.Rotary ? 1000 : 850,
                        RedlineRpm = fam == EngineFamilyKind.Rotary ? 9000 : (fam == EngineFamilyKind.Triple ? 7000 : 7600),
                        Turbo = turbo,
                        BlowOff = fam == EngineFamilyKind.Six ? BlowOffStyle.Valve : BlowOffStyle.Flutter,
                        Seed = (uint)fam * 11u + (turbo ? 5u : 1u),
                    };
                    var m = new VehicleSoundModel(cfg, SR);
                    double ms;
                    var x = RenderBus(m, VehicleBus.Engine, 24.0, EngineSweep(cfg), out ms);
                    double peak = Analysis.Db(Analysis.Peak(x, 0, x.Length));
                    double rms = Analysis.Db(Analysis.Rms(x, 0, x.Length));
                    string name = fam + (turbo ? "-turbo" : "");
                    // Firing-order check at a steady 3000 rpm under load.
                    var m2 = new VehicleSoundModel(cfg, SR);
                    double ms2;
                    var steady = RenderBus(m2, VehicleBus.Engine, 3.0, Steady(3000, 1, 1), out ms2);
                    var mag = Analysis.AverageSpectrum(steady, SR, 2 * SR, 16384);
                    double binHz = SR / 16384.0;
                    double firingHz = 3000 / 60.0 * FiringPerRev[(int)fam];
                    int fb = (int)Math.Round(firingHz / binHz);
                    double fmag = 0;
                    for (int k = fb - 3; k <= fb + 3; k++) fmag = Math.Max(fmag, mag[k]);
                    double best = 0; int bestK = 0;
                    for (int k = (int)(20 / binHz); k < (int)(2000 / binHz); k++) if (mag[k] > best) { best = mag[k]; bestK = k; }
                    double firingDb = Analysis.Db(fmag / best);
                    Console.WriteLine(string.Format("{0,-16} {1,7:0.00} {2,7:0.0} {3,7:0.00} {4,8:0.0} {5,9:0.0} {6,10:0.0}", name, peak, rms, ms, firingHz, bestK * binHz, firingDb));
                    if (peak > Program.PeakLimitDb) { Program.Fail(name + ": peak above " + Program.PeakLimitDb + " dBFS"); }
                    if (firingDb < -12) { Program.Fail(name + ": firing order is " + firingDb.ToString("0.0") + " dB below the strongest partial"); }
                    if (ms > 15) Console.WriteLine("  note " + name + ": engine bus " + ms.ToString("0.0") + " ms/s");
                    if (wav) Wav.Write(Path.Combine(outDir, "engine", name.ToLowerInvariant() + "_sweep.wav"), x, x.Length, 1, SR);
                    if (!turbo)
                    {
                        var p = new List<double>();
                        p.AddRange(Profile(cfg, cfg.IdleRpm, 0, 0));
                        p.AddRange(Profile(cfg, 3000, 1, 1));
                        p.AddRange(Profile(cfg, 6000, 1, 1));
                        p.AddRange(Profile(cfg, 5000, 0, 0));
                        profiles[fam] = p.ToArray();
                    }
                }
            }
            Console.WriteLine("  firing-dB = level at the expected firing frequency relative to the strongest partial (0 = firing order dominates)");
            Console.WriteLine("  Timbre distance between families (mean |dB| over 1/3-octave profiles at idle, 3000/6000 rpm load, 5000 rpm coast):");
            double minD = double.MaxValue;
            for (int a = 0; a < families.Length; a++)
                for (int b = a + 1; b < families.Length; b++)
                {
                    double d = Analysis.ProfileDistance(profiles[families[a]], profiles[families[b]]);
                    minD = Math.Min(minD, d);
                    Console.WriteLine(string.Format("    {0,-8} vs {1,-8} {2,5:0.0} dB", families[a], families[b], d));
                }
            if (minD < 1.5) { Program.Fail("engine families are not spectrally distinct (min distance " + minD.ToString("0.0") + " dB)"); }

            // Meet rev blip.
            {
                var cfg = new EngineConfig { Family = EngineFamilyKind.Rotary, IdleRpm = 1000, RedlineRpm = 9000, Seed = 99 };
                var m = new VehicleSoundModel(cfg, SR);
                bool sent = false;
                double ms;
                var x = RenderBus(m, VehicleBus.Engine, 3.0, (t, mm) => { mm.SetDrive(1000, 0, 0, 0, 0, false); if (!sent && t > 0.5) { mm.RevBlip(); sent = true; } }, out ms);
                double peak = Analysis.Db(Analysis.Peak(x, 0, x.Length));
                Console.WriteLine(string.Format("  meet rev blip (rotary, parked): peak {0:0.00} dBFS", peak));
                if (peak > Program.PeakLimitDb) { Program.Fail("rev blip peak"); }
                if (wav) Wav.Write(Path.Combine(outDir, "engine", "rotary_meet_rev.wav"), x, x.Length, 1, SR);
            }

            Console.WriteLine();
            Console.WriteLine("== Road bus: asphalt slip ramp, gravel, grass, kerb, wall scrape, wind 0-200 km/h (12 s) ==");
            {
                var cfg = new EngineConfig { Seed = 7 };
                var m = new VehicleSoundModel(cfg, SR);
                double ms;
                var x = RenderBus(m, VehicleBus.Road, 12.0, (t, mm) =>
                {
                    float speed, slip = 0, kerb = 0, scrape = 0;
                    var surf = TyreSurface.Asphalt;
                    if (t < 6) { speed = (float)(t / 6 * 55); slip = t < 2 ? 0 : (t < 3.5 ? (float)((t - 2) / 1.5 * 0.85) : (t < 4.5 ? 0.85f : (float)Math.Max(0, 0.85 - (t - 4.5) * 0.85))); }
                    else if (t < 8.5) { speed = 25; surf = TyreSurface.Shoulder; slip = 0.3f; }
                    else if (t < 10) { speed = 18; surf = TyreSurface.Grass; slip = 0.2f; }
                    else if (t < 11) { speed = 20; kerb = 1; }
                    else { speed = 15; scrape = 1; }
                    mm.SetRoad(speed, slip, surf, 1f, kerb, scrape);
                }, out ms);
                double peak = Analysis.Db(Analysis.Peak(x, 0, x.Length));
                Console.WriteLine(string.Format("  road: peak {0:0.00} dBFS, RMS {1:0.0} dBFS, {2:0.00} ms/s", peak, Analysis.Db(Analysis.Rms(x, 0, x.Length)), ms));
                for (int seg = 0; seg < 6; seg++)
                {
                    string[] names = { "0-2 s roll", "2-4.5 s squeal", "4.5-6 s fast roll+wind", "6-8.5 s gravel", "8.5-10 s grass", "10-12 s kerb/scrape" };
                    double[] bounds = { 0, 2, 4.5, 6, 8.5, 10, 12 };
                    int s0 = (int)(bounds[seg] * SR), s1 = (int)(bounds[seg + 1] * SR);
                    Console.WriteLine(string.Format("    {0,-24} RMS {1,6:0.0} dBFS", names[seg], Analysis.Db(Analysis.Rms(x, s0, s1))));
                }
                if (peak > Program.PeakLimitDb) { Program.Fail("road bus peak"); }
                if (wav) Wav.Write(Path.Combine(outDir, "engine", "road_scenario.wav"), x, x.Length, 1, SR);
            }

            Console.WriteLine();
            Console.WriteLine("== Impact bus: thumps 0.3/0.7, collisions 0.15/0.5/0.95 ==");
            {
                var m = new VehicleSoundModel(new EngineConfig { Seed = 3 }, SR);
                var fired = new bool[5];
                double[] at = { 0.2, 0.8, 1.5, 2.5, 3.5 };
                double ms;
                var x = RenderBus(m, VehicleBus.Impacts, 5.5, (t, mm) =>
                {
                    for (int i = 0; i < 5; i++)
                        if (!fired[i] && t >= at[i])
                        {
                            fired[i] = true;
                            if (i == 0) mm.Thump(0.3f); else if (i == 1) mm.Thump(0.7f);
                            else mm.Impact(i == 2 ? 0.15f : (i == 3 ? 0.5f : 0.95f));
                        }
                }, out ms);
                for (int i = 0; i < 5; i++)
                {
                    int s0 = (int)(at[i] * SR), s1 = (int)((i < 4 ? at[i + 1] : 5.5) * SR);
                    string label = i < 2 ? "thump " + (i == 0 ? "0.3" : "0.7") : "collision " + (i == 2 ? "0.15" : (i == 3 ? "0.50" : "0.95"));
                    Console.WriteLine(string.Format("    {0,-16} peak {1,6:0.0} dBFS", label, Analysis.Db(Analysis.Peak(x, s0, s1))));
                }
                double peak = Analysis.Db(Analysis.Peak(x, 0, x.Length));
                if (peak > Program.PeakLimitDb) { Program.Fail("impact bus peak"); }
                if (wav) Wav.Write(Path.Combine(outDir, "engine", "impacts.wav"), x, x.Length, 1, SR);
            }
        }

        public static void RunSfx(string outDir, bool wav, ref int failures)
        {
            Console.WriteLine();
            Console.WriteLine("== UI cues (UI bus, 48 kHz stereo) ==");
            Console.WriteLine(string.Format("{0,-14} {1,8} {2,8} {3,8}  {4}", "cue", "audible", "peak", "RMS", "caption"));
            for (int c = 0; c < (int)UiCue.Count; c++)
            {
                var cue = (UiCue)c;
                var e = new SfxEngine(SR);
                e.Play(cue);
                int frames = (int)(2.5 * SR);
                var buf = new float[frames * 2];
                e.Render(buf, frames, 2);
                double peak = Analysis.Peak(buf, 0, buf.Length);
                int last = 0;
                for (int i = 0; i < buf.Length; i++) if (Math.Abs(buf[i]) > 0.001) last = i / 2;
                double rms = Analysis.Rms(buf, 0, Math.Max(2, (last + 1) * 2));
                Console.WriteLine(string.Format("{0,-14} {1,7:0.00}s {2,8:0.0} {3,8:0.0}  {4}", cue, last / (double)SR, Analysis.Db(peak), Analysis.Db(rms), UiCueLibrary.Caption(cue) ?? "-"));
                if (Analysis.Db(peak) > Program.PeakLimitDb) { Program.Fail(cue + ": peak"); }
                if (Analysis.Db(peak) < -40) { Program.Fail(cue + ": inaudible"); }
                if (wav) Wav.Write(Path.Combine(outDir, "sfx", cue.ToString().ToLowerInvariant() + ".wav"), buf, frames, 2, SR);
            }
        }
    }
}
