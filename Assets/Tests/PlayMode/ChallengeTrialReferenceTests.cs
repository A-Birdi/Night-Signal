using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Measures and publishes the challenge trials' targets (authored/challenge-trials.json; docs/CHALLENGE_TRIALS.md) the way
    /// the challenge references are measured: each trial's supplied loaner, resolved like a garage build, driven solo by the
    /// validator autopilot on the trial's course and conditions. Time targets: Gold 1.02 ×, Silver 1.10 ×, Bronze 1.20 × the
    /// autopilot's time. Drift trials run at drift skills 0.95, 0.80 and 0.65 and use the cleanest run (fewest resets,
    /// then the most scored), time and drift from that one run; drift targets: Gold = the raw it scored in the zones (banked
    /// or earned, the larger — as V-113's drift references, so points lost to walls never soften a target), Silver 0.85 ×,
    /// Bronze 0.70 ×; a trial that forbids the handbrake is measured with slides started by power. A reference that did not finish, used a forbidden input, or could not bank every zone a
    /// trial demands is reported and publishes nothing. Explicit: rerun after a physics, route, part or car change.
    /// Automation with legal inputs, not a human.
    /// </summary>
    [Explicit("measures and publishes the challenge trials' targets")]
    public sealed class ChallengeTrialReferenceTests
    {
        const string FileName = "Assets/Content/Data/authored/challenge-trials.json";

        /// <summary>A drill's reference holds each marked apex's line this far past the gate (the trial tour drives it the same way).</summary>
        public const float DrillApexHoldMetres = 30f;

        static double TimeFactor(string tier) => tier == "gold" ? 1.02 : tier == "silver" ? 1.10 : 1.20;
        static double DriftFactor(string tier) => tier == "gold" ? 1.00 : tier == "silver" ? 0.85 : 0.70;

        sealed class RunFacts
        {
            public float Skill, Handbrake, Margin, Pace;
            public long TimeMs;
            public double Banked, Earned;
            public int Zones, ZonesBanked, Walls, Resets;
            public float OffPaved;
            public bool GatesTouched, DefenceKept;
            public string GateLog = "";
            public string RecoveryLog = "";
            public bool Recovered;
            public long SectionMs;
            public System.Collections.Generic.Dictionary<string, float> Exits = new System.Collections.Generic.Dictionary<string, float>();
            public System.Collections.Generic.Dictionary<string, GateSpeedFact> Brakes = new System.Collections.Generic.Dictionary<string, GateSpeedFact>();
            public string OffPavedWhere = "";
            public string Surface;
            public NightSignal.Core.Ghosts.GhostRecording Ghost;
        }

        static IEnumerator Run(ChallengeTrialDef t, CarDef car, ResolvedCarSpec spec, float skill, Action<RunFacts> done, float margin = 0f, float pace = 0f)
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode($"Assets/Content/Courses/{t.Course}/{t.Course}.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("Editor-only scene loading");
#endif
            yield return null;
            var go = new GameObject("ChallengeTrialReference");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = car.Id;
            session.PlayerSpec = spec;
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            session.AutopilotDriftSkill = skill;
            session.AutopilotNoHandbrake = t.Rules.NoHandbrake;
            session.AutopilotEdgeMargin = margin;
            session.AutopilotPaceScale = pace;
            session.AutopilotRival = string.IsNullOrEmpty(t.ReferenceRival) ? null : t.ReferenceRival;
            session.AutopilotRivalStage = t.ReferenceStage > 0 ? t.ReferenceStage : 1;
            OfflineRaceSession.AutopilotAimsChallengeGates = t.Rules.AllChallengeGates;
            OfflineRaceSession.AutopilotApexHoldMetres = t.IsDrill ? DrillApexHoldMetres : 0f;
            OfflineRaceSession.AutopilotSlidesZonesOf = t.Rules.AlternatingRecoveries ? t.Challenge : null;
            if (t.Ghost)
                session.GhostTemplate = new NightSignal.Core.Ghosts.GhostHeader
                {
                    Format = "trial-" + t.Id, CarModelId = car.Id, BuildHash = spec.BuildHash, Driver = "Gold reference", Provenance = TrialGhosts.Provenance,
                };
            session.Rules = new RaceEventRules
            {
                Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 10,
                CarCapPi = t.Loaner.PiCap > 0 ? t.Loaner.PiCap : PerformanceIndex.Max,
                Surface = t.Conditions == "course" ? CourseRuntime.Active?.Route?.Surface ?? "dry" : t.Conditions, DriftRanking = t.JudgesDrift,
                SectionStartGate = t.HasSection ? t.SectionStartGate : null, SectionEndGate = t.HasSection ? t.SectionEndGate : null,
            };
            session.OpposingAi = new System.Collections.Generic.List<string>();
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - t0 < 600f) yield return null;
            RaceEntrant me = session.Player;
            RaceEntrantResult r = session.Results?.FirstOrDefault(x => x.Entrant.Human);
            var f = new RunFacts
            {
                Skill = skill, Margin = margin, Pace = pace, TimeMs = r != null && r.Outcome == RunOutcome.Finished ? r.FinishTimeMicros / 1000 : 0,
                Banked = me.Drift.BankedRaw, Earned = me.Drift.EarnedRaw, Zones = session.Sim.Drift.Zones.Count, ZonesBanked = me.Drift.ZonesBanked.Count,
                Walls = me.Progress.WallIncidents, Resets = me.Progress.Resets, Handbrake = me.Progress.HandbrakeSeconds, OffPaved = me.Progress.OffPavedSeconds,
                Ghost = session.PlayerGhost,
                GatesTouched = me.GateRun != null && me.GateRun.AllTouched(t.Challenge),
                GateLog = me.GateRun == null ? "" : string.Join(", ", session.Sim.Gates.TouchGates.Select((g, i) => (g, i)).Where(x => x.g.Challenge == t.Challenge)
                    .Select(x => $"{x.g.Id} touched {me.GateRun.Touches[x.i]}/{me.GateRun.Passes[x.i]} (last at {me.GateRun.LastLateral[x.i]:F2} m for {x.g.LineOffset:F1} ± {x.g.LineTolerance:F1})")),
                DefenceKept = me.GateRun != null && me.GateRun.DefenceKept(t.Challenge),
                Recovered = me.ZoneChains != null && me.ZoneChains.AlternatingRecoveries(t.Challenge),
                RecoveryLog = me.ZoneChains == null ? "" : string.Join(", ", me.ZoneChains.Recoveries.Select(x => x.Zone < 0 ? "| reset or spin |" : $"{me.ZoneChains.Zones[x.Zone].Id} {(x.Direction > 0 ? "+" : "−")}"))
                              + (me.ZoneChains.Spun ? "; SPUN" : ""),
                SectionMs = me.SectionMicros > 0 ? me.SectionMicros / 1000 : 0,
                Brakes = me.GateRun == null ? new System.Collections.Generic.Dictionary<string, GateSpeedFact>()
                    : me.GateRun.SpeedGateIds.Where(g => me.GateRun.SpeedFact(g)?.Crossed == true).ToDictionary(g => g, g => me.GateRun.SpeedFact(g).Value),
                Exits = me.GateRun == null ? new System.Collections.Generic.Dictionary<string, float>()
                    : me.GateRun.SpeedGateIds.Where(g => me.GateRun.SpeedFact(g)?.Crossed == true).ToDictionary(g => g, g => me.GateRun.SpeedFact(g).Value.SpeedKmh),
                OffPavedWhere = string.Join(", ", me.Progress.OffPavedAt.Select(v => $"{v.x:F0} m lateral {v.y:F2} of {v.z * 0.5f:F2}")),
                Surface = session.Rules.Surface ?? CourseRuntime.Active?.Route?.Surface ?? "dry",
            };
            OfflineRaceSession.AutopilotAimsChallengeGates = false;
            OfflineRaceSession.AutopilotApexHoldMetres = 0f;
            OfflineRaceSession.AutopilotSlidesZonesOf = null;
            UnityEngine.Object.Destroy(go);
            yield return null;
            done(f);
        }

        /// <summary>
        /// A pressure trial's sector pace (CH39): the loaner alone under contact rules (so the racecraft judge times the challenge's
        /// defence zone), the validator's pace; the fastest pass through the zone.
        /// </summary>
        static IEnumerator RunSector(ChallengeTrialDef t, CarDef car, ResolvedCarSpec spec, Action<double, string> done)
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode($"Assets/Content/Courses/{t.Course}/{t.Course}.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
#endif
            yield return null;
            var go = new GameObject("ChallengeTrialSector");
            var session = go.AddComponent<OfflineRaceSession>();
            session.CarId = car.Id;
            session.PlayerSpec = spec;
            session.Autopilot = true;
            session.Headless = true;
            session.SimulationSpeed = 30;
            session.Rules = new RaceEventRules { Kind = "freeplay", Contact = ContactPolicy.LightContact, StageNumber = 10,
                CarCapPi = t.Loaner.PiCap > 0 ? t.Loaner.PiCap : PerformanceIndex.Max, Surface = t.Conditions == "course" ? CourseRuntime.Active?.Route?.Surface ?? "dry" : t.Conditions };
            session.OpposingAi = new System.Collections.Generic.List<string>();
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (session.Results == null && Time.realtimeSinceStartup - t0 < 600f) yield return null;
            RacecraftRun rc = session.Player?.Racecraft;
            var runs = rc == null ? new System.Collections.Generic.List<double>() : rc.DefenceRuns.Where(d => d.Challenge == t.Challenge).Select(d => d.Seconds).ToList();
            string log = rc == null ? "no racecraft judge" : string.Join("; ", rc.DefenceRuns.Where(d => d.Challenge == t.Challenge)
                .Select(d => $"{d.Seconds:F3} s{(d.WallTouched ? " (barrier touched)" : "")}"));
            UnityEngine.Object.Destroy(go);
            yield return null;
            done(runs.Count > 0 ? runs.Min() : 0, log);
        }

        [UnityTest, Timeout(3600000)]
        public IEnumerator MeasureChallengeTrials()
        {
            ContentLibrary lib = ContentLibrary.Load();
            ContentCatalogue cat = lib.Catalogue;
            var file = JsonConvert.DeserializeObject<ChallengeTrialsFile>(File.ReadAllText(FileName));
            var report = new StringBuilder();
            var problems = new System.Collections.Generic.List<string>();
            report.AppendLine("# Challenge trial targets, measured by ChallengeTrialReferenceTests (PlayMode, explicit): each trial's loaner resolved like a");
            report.AppendLine("# garage build, solo, validator autopilot, the trial's course and conditions; drift trials: the cleanest of three drift skills. Automation.");
            // Builds/diag/measure-trials.txt (one trial id per line) limits a run to those trials; the others keep their targets.
            const string OnlyFile = "Builds/diag/measure-trials.txt";
            var only = File.Exists(OnlyFile) ? new System.Collections.Generic.HashSet<string>(File.ReadAllLines(OnlyFile).Select(l => l.Trim()).Where(l => l.Length > 0)) : null;
            if (only != null) report.AppendLine($"# this run measured only: {string.Join(", ", only)}");

            foreach (ChallengeTrialDef t in file.Trials)
            {
                if (only != null && !only.Contains(t.Id)) continue;
                if (t.RaceRivalReference)
                {
                    // The authored rival reference's own time is the target (CH74's "C24 post-story reference trial").
                    NightSignal.Core.Ghosts.GhostRecording g = RivalReferenceGhosts.For(t.Course);
                    t.Targets.TimeMs = g != null ? g.Header.ResultMicros / 1000 : 0;
                    report.AppendLine().AppendLine($"## {t.Id} ({t.Challenge}, {t.Tier}) on {t.Course}: the authored rival reference — {g?.Header.Driver} in {g?.Header.CarModelId}, " +
                                                   $"{(g != null ? (g.Header.ResultMicros / 1e6).ToString("F3") + " s" : "MISSING")} — is the target");
                    if (g == null) problems.Add($"{t.Id}: no rival reference on {t.Course}");
                    continue;
                }
                if (t.IsCup)
                {
                    // A challenge cup: the loaner alone on each leg, the time × the cup's published leg factor (0 = untimed legs).
                    CarDef ccar = cat.Car(t.Loaner.Car);
                    ResolveResult cres = TrialLoaners.Resolve(t.Loaner, ccar, cat.CarTunings[ccar.Id], lib.Parts, out PiEstimate cpi);
                    if (!cres.Ok) { problems.Add($"{t.Id}: the loaner does not resolve"); continue; }
                    report.AppendLine().AppendLine($"## {t.Id} ({t.Challenge}, {t.Tier}) — a cup {string.Join(" → ", t.Legs.Select(l => l.Course))}: {ccar.Name} stock, PI {cpi.Value}; leg factor {t.LegFactor:F2}");
                    if (t.LegFactor <= 0) { report.AppendLine("→ untimed legs: nothing to measure"); continue; }
                    foreach (TrialCupLeg leg in t.Legs)
                    {
                        var legTrial = new ChallengeTrialDef { Id = t.Id, Challenge = t.Challenge, Course = leg.Course, Conditions = leg.Conditions, Loaner = t.Loaner };
                        RunFacts lf = null;
                        yield return Run(legTrial, ccar, cres.Spec, 0f, f => lf = f);
                        if (lf == null || lf.TimeMs <= 0) { problems.Add($"{t.Id}: the reference did not finish {leg.Course}"); leg.TimeMs = 0; continue; }
                        leg.TimeMs = (long)Math.Ceiling(t.LegFactor * lf.TimeMs / 100.0) * 100;
                        report.AppendLine($"{leg.Course}: time {lf.TimeMs / 1000.0:F3} s, walls {lf.Walls}, resets {lf.Resets} → {leg.TimeMs / 1000.0:F1} s");
                        if (t.Rules.NoReset && lf.Resets > 0) problems.Add($"{t.Id}: the reference reset on {leg.Course} (the rule stays; a person must do better)");
                    }
                    continue;
                }
                if (t.IsRace)
                {
                    if (!t.Rules.PressureSector)
                    {
                        // Racecraft trials are judged by their rules against their fixed field; a pressure trial has a sector pace.
                        report.AppendLine().AppendLine($"## {t.Id} ({t.Challenge}, {t.Tier}) on {t.Course}: a race against its fixed field — no targets to measure");
                        continue;
                    }
                    CarDef rcar = cat.Car(t.Loaner.Car);
                    ResolveResult rres = TrialLoaners.Resolve(t.Loaner, rcar, cat.CarTunings[rcar.Id], lib.Parts, out PiEstimate rpi);
                    if (!rres.Ok) { problems.Add($"{t.Id}: the loaner does not resolve"); continue; }
                    double sector = 0;
                    string sectorLog = "";
                    yield return RunSector(t, rcar, rres.Spec, (sec, log) => { sector = sec; sectorLog = log; });
                    report.AppendLine().AppendLine($"## {t.Id} ({t.Challenge}, {t.Tier}) on {t.Course}: {rcar.Name} stock, PI {rpi.Value}; the loaner alone through the marked sector: {sectorLog}");
                    if (sector <= 0) { problems.Add($"{t.Id}: the reference never drove the marked sector"); t.Targets.SectorTimeMs = 0; continue; }
                    t.Targets.SectorTimeMs = (long)Math.Ceiling(TimeFactor(t.Tier) * sector * 1000 / 100.0) * 100;
                    report.AppendLine($"→ {t.Tier} sector pace {t.Targets.SectorTimeMs / 1000.0:F1} s ({TimeFactor(t.Tier):F2} × the fastest pass)");
                    continue;
                }
                CarDef car = cat.Car(t.Loaner.Car);
                ResolveResult resolved = TrialLoaners.Resolve(t.Loaner, car, cat.CarTunings[car.Id], lib.Parts, out PiEstimate pi);
                if (!resolved.Ok) { problems.Add($"{t.Id}: the loaner does not resolve"); continue; }
                bool drift = t.JudgesDrift;
                // Drift trials: the cleanest of three skills (the drift controller still runs out of road on some courses, and a
                // run full of resets would publish soft targets); time trials: one run.
                var runs = new System.Collections.Generic.List<RunFacts>();
                // A recovery drill (CH23) slides too: the same three skills, the cleanest run that recovers in every zone published.
                foreach (float skill in drift || t.Rules.AlternatingRecoveries ? new[] { 0.95f, 0.8f, 0.65f } : new[] { 0f })
                    yield return Run(t, car, resolved.Spec, skill, f => runs.Add(f));
                // A trial that keeps every tyre on the paved road: if the validator's line cuts an apex onto the shoulder, the
                // fastest wider edge margin that keeps the rule is the reference (so the Gold target is shown reachable within its
                // rules). A narrower apex line was tried first and ran wide on the exits instead (V-121).
                if (t.Rules.AllTyresPaved)
                    foreach (float margin in new[] { 1.6f, 1.9f, 2.2f })
                    {
                        if (runs.Any(r => r.TimeMs > 0 && r.OffPaved <= 0f)) break;
                        yield return Run(t, car, resolved.Spec, 0f, f => runs.Add(f), margin);
                    }
                // A trial that needs every marked gate: if the validator's line misses one (it keeps 1.3 m from the edge, and a marked
                // apex can sit further out), a narrower edge margin that touches them all is the reference, published so a replay
                // drives the same line.
                if (t.Rules.AllChallengeGates && !runs.Any(r => r.TimeMs > 0 && r.GatesTouched))
                    foreach (float margin in new[] { 0.9f, 0.6f })
                    {
                        yield return Run(t, car, resolved.Spec, 0f, f => runs.Add(f), margin);
                        if (runs.Any(r => r.TimeMs > 0 && r.GatesTouched)) break;
                    }
                // A drill's marked apex the validator's pace runs wide of (the speed plan follows the centreline, not the tighter
                // inside line): the fastest slower pace that touches every marked gate, so the exit floors come from a run that
                // meets the turn-in criteria too.
                if (t.IsDrill && t.Rules.AllChallengeGates && !runs.Any(r => r.TimeMs > 0 && r.GatesTouched))
                    foreach (float pace in new[] { 0.95f, 0.9f, 0.85f, 0.8f })
                    {
                        yield return Run(t, car, resolved.Spec, 0f, f => runs.Add(f), 0f, pace);
                        if (runs.Any(r => r.TimeMs > 0 && r.GatesTouched)) break;
                    }
                RunFacts best = t.Rules.AllTyresPaved && runs.Any(r => r.TimeMs > 0 && r.OffPaved <= 0f)
                    ? runs.Where(r => r.TimeMs > 0 && r.OffPaved <= 0f).OrderBy(r => r.TimeMs).First()
                    : t.Rules.AlternatingRecoveries && runs.Any(r => r.TimeMs > 0 && r.Recovered)
                    ? runs.Where(r => r.TimeMs > 0 && r.Recovered).OrderBy(r => r.Resets).ThenBy(r => r.TimeMs).First()
                    : t.Rules.AllChallengeGates && runs.Any(r => r.TimeMs > 0 && r.GatesTouched)
                    ? runs.Where(r => r.TimeMs > 0 && r.GatesTouched).OrderBy(r => r.TimeMs).First()
                    : runs.OrderByDescending(r => r.TimeMs > 0).ThenBy(r => r.Resets).ThenByDescending(r => Math.Max(r.Banked, r.Earned)).ThenBy(r => r.TimeMs).First();
                report.AppendLine().AppendLine($"## {t.Id} ({t.Challenge}, {t.Tier}) on {t.Course} ({best.Surface}): {car.Name} {string.Join(" + ", t.Loaner.Parts.Values)}".TrimEnd(' ', '+') +
                                               $", PI {pi.Value}{(t.Loaner.PiCap > 0 ? $" (cap {t.Loaner.PiCap})" : "")}");
                foreach (RunFacts r in runs)
                    report.AppendLine($"{(drift || t.Rules.AlternatingRecoveries ? $"skill {r.Skill:F2}: " : r.Margin > 0f ? $"edge margin {r.Margin:F1} m: " : r.Pace > 0f ? $"pace {r.Pace:F2}: " : "")}time {(r.TimeMs > 0 ? (r.TimeMs / 1000.0).ToString("F3") + " s" : "DNF")}; banked {r.Banked:F0} raw " +
                                      $"(earned {r.Earned:F0}) in {r.ZonesBanked}/{r.Zones} zones; walls {r.Walls}, resets {r.Resets}, handbrake {r.Handbrake:F1} s, off the paved road {r.OffPaved:F1} s" +
                                      (runs.Count > 1 && r == best ? "  ← used" : "") + (r.OffPaved > 0f && t.Rules.AllTyresPaved ? $"\n  off the paved road at: {r.OffPavedWhere}" : "") +
                                      (t.Rules.AllChallengeGates ? $"\n  gates: {r.GateLog}" : ""));

                if (best.TimeMs <= 0) { problems.Add($"{t.Id}: the reference did not finish"); continue; }
                if (t.Rules.NoHandbrake && best.Handbrake > 0f) { problems.Add($"{t.Id}: the reference used the handbrake"); continue; }
                if (t.Loaner.PiCap > 0 && pi.Value > t.Loaner.PiCap) { problems.Add($"{t.Id}: PI {pi.Value} over the cap"); continue; }
                if (t.Rules.AllChallengeGates) report.AppendLine($"marked gates ({t.Challenge}) all touched: {best.GatesTouched} — {best.GateLog}");
                if (t.Rules.AllChallengeGates && !best.GatesTouched) problems.Add($"{t.Id}: the reference missed a marked gate (the rule stays; a person must do better)");
                if (t.Rules.NoReset && best.Resets > 0) problems.Add($"{t.Id}: the reference reset (the rule stays; a person must do better)");
                if (best.Resets > 0) report.AppendLine($"(the cleanest reference still reset {best.Resets} time(s): its time includes them)");
                // A rival's own practice reference is its time itself (to beat); otherwise the validator's time × the tier's factor.
                t.Targets.TimeMs = !t.JudgesTime ? 0 : !string.IsNullOrEmpty(t.ReferenceRival) ? best.TimeMs
                    : t.HasSection ? (best.SectionMs > 0 ? (long)Math.Ceiling(TimeFactor(t.Tier) * best.SectionMs / 100.0) * 100 : 0)
                    : (long)Math.Ceiling(TimeFactor(t.Tier) * best.TimeMs / 100.0) * 100;
                if (t.HasSection) report.AppendLine($"section {t.SectionStartGate} → {t.SectionEndGate}: {(best.SectionMs > 0 ? $"{best.SectionMs / 1000.0:F3} s" : "NOT DRIVEN")}");
                if (t.HasSection && best.SectionMs <= 0) problems.Add($"{t.Id}: the reference never drove its section");
                if (t.Rules.AlternatingRecoveries)
                {
                    t.Targets.ReferenceDriftSkill = best.Skill;
                    foreach (RunFacts r in runs) report.AppendLine($"skill {r.Skill:F2} recoveries: {r.RecoveryLog}; resets {r.Resets}");
                    report.AppendLine($"recoveries: {best.RecoveryLog} — four alternating: {best.Recovered}");
                    if (!best.Recovered) problems.Add($"{t.Id}: the reference did not make the four alternating recoveries (the rule stays; a person must do better)");
                }
                if (t.Rules.BrakeEnvelope)
                    foreach (TrialBrakeEnvelope env in t.Targets.Brakes)
                    {
                        // The trail-brake envelope from the reference's own trace: braking begun by its brake-on point + 15 m, the
                        // brake held until its release point − 15 m, the exit 0.85–1.15 × its exit speed (as CH08's windows).
                        if (!best.Brakes.TryGetValue(env.Gate, out GateSpeedFact b) || !b.Braked || b.BrakeOnMetres < 0f)
                        {
                            env.BrakeByMetres = 0f;
                            problems.Add($"{t.Id}: the reference did not brake in {env.Gate}");
                            report.AppendLine($"{env.Gate}: NOT BRAKED");
                            continue;
                        }
                        env.BrakeByMetres = (float)Math.Ceiling(b.BrakeOnMetres + 15f);
                        env.ReleaseAfterMetres = b.ReleaseMetres < 0f ? env.BrakeByMetres : (float)Math.Floor(Math.Max(b.BrakeOnMetres, b.ReleaseMetres - 15f));
                        env.MinExitKmh = (float)Math.Floor(b.ExitKmh * 0.85);
                        env.MaxExitKmh = (float)Math.Ceiling(b.ExitKmh * 1.15);
                        report.AppendLine($"{env.Gate}: entry {b.EntryKmh:F1}, brake on at {b.BrakeOnMetres:F1} m, released at {(b.ReleaseMetres < 0f ? "the end" : b.ReleaseMetres.ToString("F1") + " m")}, " +
                                          $"exit {b.ExitKmh:F1} km/h → on by {env.BrakeByMetres:F0}, held to {env.ReleaseAfterMetres:F0}, exit {env.MinExitKmh:F0}–{env.MaxExitKmh:F0}");
                    }
                if (t.Rules.ChallengeExits)
                    foreach (TrialExitFloor floor in t.Targets.ExitFloors)
                    {
                        // The drill's exit criterion: 0.95 × this loaner's slowest exit there (as CH04's floors), rounded down to 0.1 km/h.
                        floor.Kmh = best.Exits.TryGetValue(floor.Gate, out float v) ? (float)Math.Floor(v * 0.95 * 10.0) / 10f : 0f;
                        report.AppendLine($"{floor.Gate}: slowest exit {(best.Exits.TryGetValue(floor.Gate, out float w) ? $"{w:F1} km/h → floor {floor.Kmh:F1}" : "NOT CROSSED")}");
                        if (floor.Kmh <= 0f) problems.Add($"{t.Id}: the reference never crossed {floor.Gate}");
                    }
                if (!string.IsNullOrEmpty(t.ReferenceRival)) report.AppendLine($"(driven by {t.ReferenceRival}'s profile at stage {t.ReferenceStage}: its time is the target)");
                if (t.Rules.AllDefenceZones) report.AppendLine($"marked defence gates inside the corridor: {best.DefenceKept}");
                t.Targets.ReferenceEdgeMargin = best.Margin;
                t.Targets.ReferencePaceScale = best.Pace;
                if (t.Ghost)
                {
                    // The fixed Gold ghost is the reference run itself; beating it means finishing faster than its time.
                    if (best.Ghost == null || best.Ghost.Count < 2) { problems.Add($"{t.Id}: the reference ghost was not recorded"); t.Targets.TimeMs = 0; continue; }
                    if (t.Rules.AllTyresPaved && best.OffPaved > 0f)
                        problems.Add($"{t.Id}: the reference left the paved road for {best.OffPaved:F1} s (the rule stays; a person must do better)");
                    Directory.CreateDirectory("Assets/Content/Resources/" + TrialGhosts.Folder);
                    File.WriteAllText($"Assets/Content/Resources/{TrialGhosts.Folder}/{t.Id}.json", best.Ghost.ToJson());
                    t.Targets.TimeMs = best.TimeMs;
                }
                if (drift)
                {
                    // As the V-113 drift references: what the reference scored in the zones, so points lost to walls never soften a target.
                    double basis = Math.Max(best.Banked, best.Earned);
                    if (basis <= 0) { problems.Add($"{t.Id}: the reference scored nothing"); t.Targets.TimeMs = 0; continue; }
                    if (t.Rules.BankEveryZone && best.ZonesBanked < best.Zones)
                        problems.Add($"{t.Id}: the reference banked a chain in {best.ZonesBanked} of {best.Zones} zones (the rule stays; a person must do better)");
                    t.Targets.DriftRaw = (long)Math.Floor(DriftFactor(t.Tier) * basis / 100.0) * 100;
                    t.Targets.ReferenceDriftSkill = best.Skill;
                }
                report.AppendLine("→ " + string.Join(", ", new[]
                {
                    t.Targets.TimeMs > 0 ? (t.Ghost ? $"beat the Gold ghost's {t.Targets.TimeMs / 1000.0:F3} s (the reference run itself, recorded)"
                        : !string.IsNullOrEmpty(t.ReferenceRival) ? $"beat {t.ReferenceRival}'s {t.Targets.TimeMs / 1000.0:F3} s (its practice run itself)"
                        : $"{t.Tier} time {t.Targets.TimeMs / 1000.0:F1} s ({TimeFactor(t.Tier):F2} ×)") : null,
                    t.Targets.DriftRaw > 0 ? $"{t.Tier} drift {t.Targets.DriftRaw:N0} raw ({DriftFactor(t.Tier):F2} × scored)" : null,
                }.Where(x => x != null)));
            }

            file.Method = $"Measured {DateTime.UtcNow:yyyy-MM-dd} by ChallengeTrialReferenceTests: each trial's loaner resolved like a garage build (hm-1), solo, " +
                          "validator autopilot, the trial's course and conditions; time targets Gold 1.02 ×, Silver 1.10 ×, Bronze 1.20 × its time; drift trials " +
                          "use the cleanest of drift skills 0.95/0.80/0.65 (fewest resets, then most scored; slides started by power where the handbrake is " +
                          "forbidden), time and drift from that one run, drift targets Gold = the raw it scored in the zones (banked or earned, the larger), " +
                          "Silver 0.85 ×, Bronze 0.70 ×; racecraft trials have no targets except a pressure trial's sector pace (the loaner alone through " +
                          "the marked sector under contact rules, the fastest pass × the time factor); challenge cups: the loaner alone on each leg × the " +
                          "cup's published leg factor; " +
                          $"physics {RaceSimulation.PhysicsVersion}, scoring {RaceSimulation.ScoringVersion}. Automation, not a human.";
            report.AppendLine().AppendLine(problems.Count == 0 ? "# every trial measured" : "# problems: " + string.Join("; ", problems));
            File.WriteAllText(FileName, JsonConvert.SerializeObject(file, Formatting.Indented,
                new JsonSerializerSettings { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() }) + "\n");
            Directory.CreateDirectory("Evidence/challenges");
            File.WriteAllText(only == null ? "Evidence/challenges/trials.txt" : $"Evidence/challenges/trials-{string.Join("-", only.OrderBy(x => x))}.txt", report.ToString());
            Debug.Log("[NightSignal.ChallengeTrials] " + report.ToString().Replace("\n", " | "));
            Assert.That(problems.Where(p => !p.Contains("a person must do better")), Is.Empty, string.Join("; ", problems));
        }
    }
}
