using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Benchmark certification for the Normal campaign (spec §2.5 difficulty calibration; after Addendum 03's topology,
    /// progress and recovery work). Per stage, on its course and authored surface:
    /// <list type="bullet">
    /// <item>P — the reference time: the three starters, each on the build its upgrade path intends by that stage
    /// (build-recipes.json — freely available, class-legal), driven solo by the validator autopilot; P is the SLOWEST of
    /// the three, so no starter path needs a compulsory model change (Addendum 02 F09).</item>
    /// <item>Target = factor × P, the factor running 1.18 (S01) → 1.05 (S30); lieutenant stages sit on the same line.</item>
    /// <item>The featured rival's driving pace (a scale on its whole speed plan, never above its stage profile) is bisected
    /// so its solo time lands 0–1 % above the target: a driver who meets the target beats it.</item>
    /// <item>S29 "Four Signals": the reference runs attempt the contracts (drifting the Arc) and are measured by the same
    /// judge the race uses; the published targets come from the weakest starter — Entry sector × factor, Arc drift × 0.6,
    /// the brake-release window 0.85 × lowest … 1.15 × highest exit speed with the release point 15 m past the latest
    /// reference release, Horizon exit speeds × 0.95.</item>
    /// </list>
    /// Writes the authored-format file and per-stage evidence to Evidence/progression/benchmarks/ (copied into
    /// Assets/Content/Data/authored/stage-benchmarks.json after review). Automation with legal inputs, not a human run.
    /// </summary>
    public sealed class BenchmarkCertificationTests
    {
        static readonly string[] Starters = { "V01", "V02", "V03" };
        const double FirstFactor = 1.18, LastFactor = 1.05;       // Normal (spec: ~1.18×P early → ~1.05×P in the final act)
        const double HardFirstFactor = 1.04, HardLastFactor = 1.00; // Hard (spec: near 1.04×P → about P for the legend)
        const string Folder = "Evidence/progression/benchmarks";
        const string FileName = Folder + "/stage-benchmarks.json";

        static string Method() =>
            $"Certification {DateTime.UtcNow:yyyy-MM-dd}: per stage side under its authored conditions (stage-conditions.json), reference = slowest of " +
            $"the three starters' intended builds, solo, validator autopilot (automation, legal inputs, not a human; S29 drives its contracts, drifting " +
            $"the Arc); factor Normal {FirstFactor:0.00} → {LastFactor:0.00}, Hard {HardFirstFactor:0.00} → {HardLastFactor:0.00} over S01–S30; featured " +
            $"rival pace calibrated solo to finish 0–1 % above the target; physics {RaceSimulation.PhysicsVersion}, scoring {RaceSimulation.ScoringVersion}";

        /// <summary>Replaces one mode's entries in the certified file, keeping the other mode's.</summary>
        static void Save(List<CertifiedBenchmark> entries)
        {
            StageBenchmarksFile file = File.Exists(FileName) ? JsonConvert.DeserializeObject<StageBenchmarksFile>(File.ReadAllText(FileName))
                : new StageBenchmarksFile { Schema = "night-signal/stage-benchmarks@1" };
            foreach (CertifiedBenchmark e in entries)
            {
                int at = file.Stages.FindIndex(x => x.Stage == e.Stage && x.Mode == e.Mode);
                if (at >= 0) file.Stages[at] = e; else file.Stages.Add(e);
            }
            file.Stages = file.Stages.OrderBy(x => x.Mode == "hard" ? 1 : 0).ThenBy(x => x.Stage, StringComparer.Ordinal).ToList();
            file.Method = Method();
            File.WriteAllText(FileName, JsonConvert.SerializeObject(file, Formatting.Indented, Json));
        }

        static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver { NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy() },
            NullValueHandling = NullValueHandling.Ignore,
        };

        [UnityTest, Timeout(7200000)]
        public IEnumerator CertifyNormal() => CertifyMode(CampaignMode.Normal);

        [UnityTest, Timeout(7200000)]
        public IEnumerator CertifyHard() => CertifyMode(CampaignMode.Hard);

        static IEnumerator CertifyMode(CampaignMode mode)
        {
            ContentLibrary lib = ContentLibrary.Load();
            var entries = new List<CertifiedBenchmark>();
            Directory.CreateDirectory(Folder);
            var problems = new List<string>();
            for (int n = 1; n <= Limits.CampaignStages; n++)
                yield return CertifyStage(lib, n, mode, problems, c => entries.Add(c));
            Save(entries);
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
            Assert.That(entries.Count, Is.EqualTo(Limits.CampaignStages));
        }

        /// <summary>
        /// Re-certifies only the stage sides listed in Temp/ns-certify-stages.txt ("N1 H4 H30": mode letter and stage number)
        /// and merges them into the file — for a change that moves only some featured rivals (their reference times do not
        /// change). Ignored when the list is absent.
        /// </summary>
        [UnityTest, Timeout(7200000)]
        public IEnumerator CertifyListed()
        {
            const string list = "Temp/ns-certify-stages.txt";
            if (!File.Exists(list)) Assert.Ignore("No " + list);
            ContentLibrary lib = ContentLibrary.Load();
            var entries = new List<CertifiedBenchmark>();
            var problems = new List<string>();
            foreach (string item in File.ReadAllText(list).Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                CampaignMode mode = char.ToUpperInvariant(item[0]) == 'H' ? CampaignMode.Hard : CampaignMode.Normal;
                if (!int.TryParse(item.Substring(1), out int n) || n < 1 || n > Limits.CampaignStages) { problems.Add("bad entry " + item); continue; }
                yield return CertifyStage(lib, n, mode, problems, c => entries.Add(c));
            }
            Save(entries);
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }

        /// <summary>Re-certifies S29 alone (its contracts need contract-driving reference runs) and merges it into the file.</summary>
        [UnityTest, Timeout(3600000)]
        public IEnumerator CertifyFourSignals()
        {
            ContentLibrary lib = ContentLibrary.Load();
            var problems = new List<string>();
            CertifiedBenchmark s29 = null;
            yield return CertifyStage(lib, 29, CampaignMode.Normal, problems, c => s29 = c);
            Assert.That(s29, Is.Not.Null, string.Join("; ", problems));
            Save(new List<CertifiedBenchmark> { s29 });
            Assert.That(problems, Is.Empty, string.Join("; ", problems));
        }

        static IEnumerator CertifyStage(ContentLibrary lib, int n, CampaignMode mode, List<string> problems, Action<CertifiedBenchmark> done)
        {
            bool hard = mode == CampaignMode.Hard;
            string tag = hard ? "H" : "N";
            ContentCatalogue cat = lib.Catalogue;
            StageDef stage = cat.Stage("S" + n.ToString("00"));
            bool contracts = stage.Type == "penultimate";
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                $"Assets/Content/Courses/{stage.Course}/{stage.Course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#else
            Assert.Ignore("Editor-only scene loading");
            yield break;
#endif
            yield return null;
            // The stage side's authored conditions (a Hard side's damp/wet/night), as the race itself uses them.
            string surface = RaceConditions.Surface(cat, "campaign", stage.Id, mode, CourseRuntime.Active);
            var at = new StageRef { Mode = mode, Stage = n };
            var ev = new StageEvidence { stage = stage.Id, course = stage.Course, type = stage.Type, capPi = stage.MaxPI, surface = surface,
                provisionalTargetMs = StageBenchmarks.Provisional(cat, stage, mode).TargetTimeMs, mode = hard ? "hard" : "normal" };

            // P: the slowest intended starter build (S29: driving its contracts).
            ResolvedCarSpec slowestSpec = null;
            var runs = new List<ContractRun>();
            foreach (string car in Starters)
            {
                CarDef model = cat.Car(car);
                RecipeStep step = lib.Recipes.Car(car).Path
                    .Where(s => s.Kind == "main" && s.By != null && StageRef.Parse(s.By).CompareTo(at) <= 0)
                    .OrderBy(s => StageRef.Parse(s.By)).LastOrDefault();
                ResolvedCarSpec spec = step == null ? BuildResolver.ResolveStock(model, cat.CarTunings[car], lib.Parts)
                    : BuildResolver.Resolve(model, cat.CarTunings[car], lib.Parts, RecipeBook.ToSnapshot(step, lib.Parts)).Spec;
                long ms = 0;
                ContractRun measured = null;
                yield return Reference(car, spec, surface, contracts, (t, r) => { ms = t; measured = r; });
                var row = new StarterRun { car = car, build = step?.Id ?? "stock", ms = ms, finished = ms > 0 };
                if (measured != null)
                {
                    runs.Add(measured);
                    row.entryMs = measured.EntryMs;
                    row.apexHits = measured.ApexHit.Count(x => x);
                    row.arcDrift = (long)Math.Max(0, measured.ArcRaw);
                    row.descentWalls = measured.DescentWalls;
                    row.descentResets = measured.DescentResets;
                    row.brakedAndReleased = measured.BrakedNearZone && measured.BrakeReleaseMetres >= 0;
                    row.brakeExitKmh = measured.BrakeExitKmh;
                    row.brakeStartMetres = measured.BrakeStartMetres;
                    row.brakeReleaseMetres = measured.BrakeReleaseMetres;
                    row.brakeOffAtExit = measured.BrakeOffAtExit;
                    row.exitKmh = measured.ExitKmh.ToList();
                }
                ev.starters.Add(row);
                if (ms <= 0) { problems.Add($"{stage.Id}: {car} did not finish its reference run"); continue; }
                if (ms > ev.referenceMs) { ev.referenceMs = ms; ev.referenceCar = car; ev.referenceBuild = step?.Id ?? "stock"; slowestSpec = spec; }
            }
            if (slowestSpec == null) yield break;
            double first = hard ? HardFirstFactor : FirstFactor, last = hard ? HardLastFactor : LastFactor;
            ev.factor = Math.Round(first + (last - first) * (n - 1) / (Limits.CampaignStages - 1.0), 4);
            ev.targetMs = (long)Math.Round(ev.referenceMs * ev.factor);

            FourSignalsTargets signals = null;
            if (contracts && runs.Count == Starters.Length)
            {
                signals = new FourSignalsTargets
                {
                    EntrySectorMs = (long)Math.Round(runs.Max(r => r.EntryMs) * ev.factor),
                    // Solo references drift clean air; 0.6 leaves room for the live field around the Arc.
                    ArcDriftRaw = (long)Math.Floor(runs.Min(r => Math.Max(0, r.ArcRaw)) * 0.6),
                    BrakeExitMinKmh = (float)Math.Round(runs.Min(r => r.BrakeExitKmh) * 0.85, 1),
                    BrakeExitMaxKmh = (float)Math.Round(runs.Max(r => r.BrakeExitKmh) * 1.15, 1),
                    // The release point from calibration (spec note): the latest reference release plus 15 m.
                    BrakeReleaseByMetres = (float)Math.Round(runs.Max(r => r.BrakeReleaseMetres) + 15f, 1),
                };
                for (int i = 0; i < runs[0].ExitKmh.Length; i++)
                    signals.HorizonExitKmh.Add((float)Math.Round(runs.Min(r => r.ExitKmh[i]) * 0.95, 1));
                // What the reference runs themselves could not do is not a contract anyone can be asked to pass.
                foreach (StarterRun s in ev.starters)
                {
                    if (s.apexHits < 2) problems.Add($"{stage.Id}: {s.car} touched {s.apexHits}/2 Entry apex gates in its reference run");
                    if (s.brakeReleaseMetres < 0) problems.Add($"{stage.Id}: {s.car} had no braking phase around the Descent zone");
                    if (s.descentWalls != 0 || s.descentResets != 0) problems.Add($"{stage.Id}: {s.car} Descent not clean ({s.descentWalls} walls, {s.descentResets} resets)");
                    if (s.arcDrift <= 0) problems.Add($"{stage.Id}: {s.car} banked no Arc drift");
                }
                ev.contracts = signals;
            }

            // The featured rival: never faster than its stage profile; slowed until it finishes 0–1 % above the target.
            ev.featured = (hard ? stage.Hard : stage.Normal).Opponents[0];
            double lo = 0.5, hi = 1.0, pace = 1.0;
            double bestPace = 1.0; long bestMs = 0; double bestError = double.MaxValue;
            for (int i = 0; i < 9; i++)
            {
                long ms = 0;
                yield return RivalRun(stage, mode, ev.referenceCar, slowestSpec, surface, ev.featured, (float)pace, r => ms = r);
                ev.tries.Add(new PaceTry { pace = Math.Round(pace, 4), ms = ms });
                double error = ms <= 0 ? double.MaxValue / 2 : ms < ev.targetMs ? (ev.targetMs - ms) * 2.0 : ms > ev.targetMs * 1.01 ? ms - ev.targetMs * 1.01 : 0;
                if (error < bestError) { bestError = error; bestPace = pace; bestMs = ms; }
                if (error == 0) break;
                if (ms > 0 && ms < ev.targetMs) hi = pace; // too fast: slow down
                else if (pace >= 1.0) break;             // too slow at its own profile: leave it as it is
                else lo = pace;
                pace = (lo + hi) / 2;
            }
            ev.featuredPace = Math.Round(bestPace, 4);
            ev.featuredMs = bestMs;
            File.WriteAllText($"{Folder}/{tag}-{stage.Id}.json", JsonConvert.SerializeObject(ev, Formatting.Indented, Json));
            Debug.Log($"[NightSignal.Certify] {tag} {stage.Id} {stage.Course} ({stage.Type}, cap {stage.MaxPI}, {surface}): " +
                      string.Join(", ", ev.starters.Select(s => $"{s.car} {s.build} {s.ms / 1000.0:F1}s")) +
                      $" → P {ev.referenceMs / 1000.0:F1}s ({ev.referenceCar}), ×{ev.factor:0.000} = target {ev.targetMs / 1000.0:F1}s " +
                      $"(provisional {ev.provisionalTargetMs / 1000.0:F0}s); featured {ev.featured} pace {ev.featuredPace:0.000} → {ev.featuredMs / 1000.0:F1}s in {ev.tries.Count} runs" +
                      (signals != null ? $"; contracts: Entry {signals.EntrySectorMs / 1000.0:F2}s, Arc {signals.ArcDriftRaw:N0}, brake window {signals.BrakeExitMinKmh:F0}–{signals.BrakeExitMaxKmh:F0} km/h, " +
                                         $"Horizon {string.Join("/", signals.HorizonExitKmh.Select(k => k.ToString("F0")))} km/h" : ""));
            done(new CertifiedBenchmark
            {
                Stage = stage.Id, Mode = hard ? "hard" : "normal", ReferenceMs = ev.referenceMs, ReferenceCar = ev.referenceCar, ReferenceBuild = ev.referenceBuild,
                Factor = ev.factor, TargetMs = ev.targetMs, FeaturedRivalPace = ev.featuredPace, FeaturedRivalMs = ev.featuredMs, Contracts = signals,
            });
        }

        /// <summary>One solo validator run; with <paramref name="contracts"/> it drives and is measured through the Four Signals.</summary>
        static IEnumerator Reference(string car, ResolvedCarSpec spec, string surface, bool contracts, Action<long, ContractRun> done)
        {
            var go = new GameObject("ReferenceRun");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = car;
            session.PlayerSpec = spec;
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.NonContact, Surface = surface, MeasureContracts = contracts };
            session.OpposingAi = new List<string>();
            yield return null;
            float start = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - start < 300f) yield return null;
            RaceEntrantResult me = session.Results?.FirstOrDefault(r => r.Entrant.Human);
            done(me != null && me.Outcome == RunOutcome.Finished ? me.FinishTimeMicros / 1000 : 0, contracts ? me?.Entrant.ContractRun : null);
            UnityEngine.Object.Destroy(go);
            yield return null;
        }

        /// <summary>The stage's featured rival alone with the reference car (every car ghosted), at a given pace; its finish time (0 = none).</summary>
        static IEnumerator RivalRun(StageDef stage, CampaignMode mode, string car, ResolvedCarSpec spec, string surface, string rival, float pace, Action<long> done)
        {
            var go = new GameObject("RivalCalibration");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = car;
            session.PlayerSpec = spec;
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            session.Rules = new RaceEventRules
            {
                Kind = "campaign", Mode = mode, StageId = stage.Id, StageNumber = stage.Number, CarCapPi = stage.MaxPI,
                Contact = ContactPolicy.NonContact, CalibrationGhosts = true, Surface = surface, FeaturedRivalPace = pace,
            };
            session.OpposingAi = new List<string> { rival };
            yield return null;
            float start = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - start < 300f) yield return null;
            RaceEntrantResult r = session.Results?.FirstOrDefault(x => x.Entrant.Roster.Role == "featured");
            done(r != null && r.Outcome == RunOutcome.Finished ? r.FinishTimeMicros / 1000 : 0);
            UnityEngine.Object.Destroy(go);
            yield return null;
        }

        sealed class StageEvidence
        {
            public string stage, mode, course, type, surface, referenceCar, referenceBuild, featured;
            public int capPi;
            public long provisionalTargetMs, referenceMs, targetMs, featuredMs;
            public double factor, featuredPace;
            public List<StarterRun> starters = new List<StarterRun>();
            public List<PaceTry> tries = new List<PaceTry>();
            public FourSignalsTargets contracts;
        }

        sealed class StarterRun
        {
            public string car, build;
            public long ms;
            public bool finished;
            public long entryMs = -1, arcDrift = -1;
            public int apexHits = -1, descentWalls = -1, descentResets = -1;
            public bool brakedAndReleased;
            public float brakeExitKmh = -1, brakeStartMetres = -1, brakeReleaseMetres = -1;
            public bool brakeOffAtExit;
            public List<float> exitKmh;
        }

        sealed class PaceTry
        {
            public double pace;
            public long ms;
        }
    }
}
