using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.Race;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>The autopilot starts slides by power in the next offline race (tours measuring or driving CH25's trial).</summary>
        bool pendingAutopilotNoHandbrake;
        /// <summary>The autopilot's edge margin in the next offline race (0 = the validator's own; replaying a trial's reference).</summary>
        float pendingAutopilotEdgeMargin;
        /// <summary>The autopilot's pace in the next offline race (0 = the validator's own; replaying a drill's reference).</summary>
        float pendingAutopilotPaceScale;

        /// <summary>Starts a challenge trial (docs/CHALLENGE_TRIALS.md) in its supplied loaner; the result returns to <paramref name="returnTo"/>.</summary>
        public void StartTrial(ChallengeTrialDef trial, UIScreen returnTo)
        {
            if (trial.IsCup)
            {
                // A challenge cup: its legs in order through the cup page, one continuous session.
                Router.Show(Cup);
                Cup.BeginTrial(trial, trial.Legs.Select((l, i) => LocalEvents.CupLeg(trial, i)).ToList());
                return;
            }
            if (trial.IsLane)
            {
                StartCoroutine(RunLaneLesson(trial, returnTo));
                return;
            }
            StartLocalEvent(LocalEvents.Trial(trial, ContentLibrary.Load()?.Catalogue?.Course(trial.Course)?.Format ?? "sprint"), returnTo);
        }

        /// <summary>A frozen lesson side: the trial's loaner (with a loaned part, for a comparison) resolved like a garage build.</summary>
        static TestYardBuild LaneBuild(TrialLoaner loaner, string label, out string problem)
        {
            ContentLibrary lib = ContentLibrary.Load();
            CarDef car = lib.Catalogue.Car(loaner.Car);
            ResolveResult r = TrialLoaners.Resolve(loaner, car, lib.Catalogue.CarTunings[car.Id], lib.Parts, out PiEstimate pi);
            problem = r.Ok ? null : string.Join("; ", r.Issues.Select(i => i.Detail));
            if (!r.Ok) return null;
            return new TestYardBuild
            {
                Label = label, BuildHash = r.Spec.BuildHash, Pi = pi?.Value ?? car.BasePI,
                Params = Vehicle.VehicleFactory.Build(r.Spec, Vehicle.AssistSettings.Default, lib.Body(car.Id).WheelRadius),
            };
        }

        /// <summary>
        /// A braking-lane lesson (CH02, CH47): T00's braking lane with the trial's supplied car (and, for a comparison, the same car
        /// on the loaned package); judged from every start when the player leaves, and kept like any trial pass — no race, no
        /// record, no payout (<see cref="LocalProgression.ApplyLessonTrial"/>).
        /// </summary>
        IEnumerator RunLaneLesson(ChallengeTrialDef t, UIScreen returnTo)
        {
            ContentLibrary lib = ContentLibrary.Load();
            CarDef car = lib.Catalogue.Car(t.Loaner.Car);
            TestYardBuild a = LaneBuild(t.Loaner, $"Supplied: {car.Name}", out string problem), b = a;
            if (a != null && !string.IsNullOrEmpty(t.ComparePart) && lib.Parts.TryPart(t.ComparePart, out PartDef loanedPart))
            {
                var loaned = new TrialLoaner { Car = t.Loaner.Car, Parts = new Dictionary<string, string>(t.Loaner.Parts) };
                loaned.Parts[loanedPart.Slot] = loanedPart.Id;
                b = LaneBuild(loaned, $"Loaned: {car.Name} on {loanedPart.Name}", out problem);
            }
            if (a == null || b == null)
            {
                Trials.SetVerdict(t.Id, $"{t.Id}: the lesson's car does not resolve ({problem}).");
                yield break;
            }
            LocalEvents.LastTrialVerdict = null;
            Canvas.gameObject.SetActive(false);
            if (backdropCamera != null) backdropCamera.SetActive(false);
            AsyncOperation load = SceneManager.LoadSceneAsync(t.Course, LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            yield return null;
            var go = new GameObject("BrakingLaneLesson");
            ActiveYard = go.AddComponent<TestYardSession>();
            ActiveYard.CarId = car.Id;
            ActiveYard.A = a;
            ActiveYard.B = b;
            ActiveYard.Lesson = t;
            while (ActiveYard != null && !ActiveYard.ExitRequested) yield return null;
            List<TestYardRun> runs = ActiveYard != null ? ActiveYard.LessonRuns.ToList() : new List<TestYardRun>();
            Destroy(go);
            ActiveYard = null;

            var facts = new TrialRunFacts
            {
                DroveLoaner = true, // the lesson offers only the supplied car and its loaned package
                Finished = runs.Count > 0,
                LaneStops = runs.Select(r => new TrialLaneStop { B = r.B, GateKmh = r.GateKmh, Stopped = !float.IsNaN(r.StopAlong), InStopGate = r.InStopGate,
                    StopMetres = r.StopMetres, StopAlong = float.IsNaN(r.StopAlong) ? 0f : r.StopAlong }).ToList(),
            };
            TrialVerdict v = TrialJudge.Judge(t, facts);
            LocalEvents.LastTrialVerdict = v;
            string line = (v.Passed ? "TRIAL PASSED — " : "Trial not passed — ") + v.Summary;
            LocalSession s = LocalSession.Current;
            if (s?.Profile != null && v.Passed)
            {
                LocalProgressionResult applied = LocalProgression.ApplyLessonTrial(s.Profile, lib.Catalogue, t.Id, car.Id, true, DateTime.UtcNow);
                if (applied.Status == LocalOperationStatus.Applied && !s.Commit(applied, out string saveNote)) line += " Not saved: " + saveNote;
                else if (applied.Status == LocalOperationStatus.Rejected) line += " " + applied.Reason;
            }
            for (int i = 0; i < runs.Count; i++)
                Debug.Log($"[NightSignal.Trial] {t.Id} start {i + 1} ({(runs[i].B ? "B" : "A")}): {(runs[i].GateKmh >= 0f ? runs[i].GateKmh.ToString("F1") + " km/h at " + t.SpeedGate : "speed gate not reached")}, " +
                          $"{(float.IsNaN(runs[i].StopAlong) ? "no stop" : $"stopped at {runs[i].StopAlong:F1} m ({runs[i].StopMetres:F1} m from {runs[i].StopFromKmh:F0} km/h){(runs[i].InStopGate ? ", inside " + t.StopGate : "")}")}");
            Debug.Log($"[NightSignal.Trial] {t.Id}: {line}");
            Trials.SetVerdict(t.Id, line);
            Canvas.gameObject.SetActive(true);
            yield return LoadBackdrop();
            if (Router.Current == returnTo) returnTo.OnShow();
            else Router.Show(returnTo, true);
        }

        /// <summary>The trial's loaner resolved like a garage build; the plan records the build hash driven (null = not driven).</summary>
        ResolvedCarSpec TrialLoanerSpec(LocalEventPlan plan, out string problem)
        {
            problem = null;
            ContentLibrary lib = ContentLibrary.Load();
            ChallengeTrialDef t = lib.Catalogue.ChallengeTrials.Find(plan.TrialId);
            if (t == null) { problem = "unknown challenge trial " + plan.TrialId; return null; }
            CarDef car = lib.Catalogue.Car(t.Loaner.Car);
            if (t.Loaner.IsTunable)
            {
                // The player's saved setup (the loaner as supplied without one); an illegal setup races as supplied and fails the trial.
                MechanicalSnapshot saved = null;
                LocalSession.Current?.Profile?.TrialSetups?.TryGetValue(t.Id, out saved);
                TrialLoanerBuild b = TrialLoaners.ResolveSetup(t.Loaner, saved, car, lib.Catalogue.CarTunings[car.Id], lib.Parts);
                plan.TrialSetup = b;
                if (!b.Ok) b = TrialLoaners.ResolveSetup(t.Loaner, null, car, lib.Catalogue.CarTunings[car.Id], lib.Parts);
                if (!b.Result.Ok) { problem = $"{t.Id}: the loaner does not resolve"; return null; }
                plan.TrialBuildHash = b.Result.Spec.BuildHash;
                Debug.Log($"[NightSignal.Trial] {t.Id}: {car.Id} {(saved == null ? "as supplied" : "with your setup")} — parts {string.Join(", ", b.Build.Parts.Select(kv => kv.Key + "=" + kv.Value))}; " +
                          $"tune {string.Join(", ", b.Build.Tuning.Values.Select(kv => kv.Key + "=" + kv.Value))}; PI {b.Pi?.Value}{(plan.TrialSetup.Ok ? "" : "; NOT LEGAL: " + string.Join("; ", plan.TrialSetup.Problems))}");
                return b.Result.Spec;
            }
            ResolveResult r = TrialLoaners.Resolve(t.Loaner, car, lib.Catalogue.CarTunings[car.Id], lib.Parts, out PiEstimate pi);
            if (!r.Ok) { problem = $"{t.Id}: the loaner does not resolve ({string.Join("; ", r.Issues)})"; return null; }
            plan.TrialBuildHash = r.Spec.BuildHash;
            Debug.Log($"[NightSignal.Trial] {t.Id}: {car.Id} {(t.Loaner.Parts.Count == 0 ? "stock" : string.Join("+", t.Loaner.Parts.Values))}, PI {pi.Value}, " +
                      $"build {r.Spec.BuildHash.Substring(0, 12)}, on {t.Course} ({t.Conditions})");
            return r.Spec;
        }

        /// <summary>
        /// Challenge trial evidence (<c>-nsTrialTour</c>), buttons only, isolated profile folder: the offline hub → Challenge
        /// Trials; every trial started from its row with "Start Trial" and driven by the validator autopilot (CH25's without the
        /// handbrake); a drift trial first at its measured reference's drift skill, then at the other measured skills until
        /// one run passes — whether its published targets can be reached at all; the verdict read back on the trials screen. Checks: every trial ran and was judged; the profile keeps exactly the passes the
        /// verdicts report; a grouped challenge (CH54) is earned only once both of its trials are passed. Whether the
        /// autopilot beats each target is reported as measured. Automation, not a person.
        /// </summary>
        /// <summary>
        /// The tour's own setup of each tunable trial's loaner, as a player would make it on Tune the Loaner: clicks on each slot
        /// button (each click installs the slot's next part), then tuning steps by control.
        /// </summary>
        static void TourSetup(string trialId, out int[] slotClicks, out Dictionary<string, int> steps)
        {
            switch (trialId)
            {
                case "TR-CH46": // the final-drive kit, 6% shorter
                    slotClicks = new[] { 1 };
                    steps = new Dictionary<string, int> { [TuningKeys.FinalDrive] = 6 };
                    return;
                case "TR-CH56": // the intake and the final-drive kit, 4% shorter for the climb (slots in order: engine, gearbox, suspension, tyres)
                    slotClicks = new[] { 1, 1, 0, 0 };
                    steps = new Dictionary<string, int> { [TuningKeys.FinalDrive] = 4 };
                    return;
                case "TR-CH59": // the adjustable diff and the final-drive kit, 6% shorter — PI 580, inside the field's (3% would be 584); slots in order: aero, brakes, differential, gearbox
                    slotClicks = new[] { 0, 0, 1, 1 };
                    steps = new Dictionary<string, int> { [TuningKeys.FinalDrive] = 6 };
                    return;
                case "TR-CH57": // touring tyres (slots in order: suspension, tyres — both parts would go over the budget); the wing a little lower, the balance a touch forward
                    slotClicks = new[] { 0, 1 };
                    steps = new Dictionary<string, int> { [TuningKeys.AeroLevel] = -2, [TuningKeys.AeroBalance] = 2 };
                    return;
                default:
                    slotClicks = new int[0];
                    steps = new Dictionary<string, int>();
                    return;
            }
        }

        IEnumerator TrialTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "trials"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.TrialTour] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { Fail($"button not available: {name} (screen {Router.Current?.ScreenName ?? "none"})"); return false; }
                b.onClick.Invoke();
                return true;
            }
            IEnumerator Until(Func<bool> condition, float seconds)
            {
                float t = Time.realtimeSinceStartup + seconds;
                while (!condition() && Time.realtimeSinceStartup < t) yield return null;
            }
            IEnumerator Snap(string name)
            {
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
                AuditBounds(name); // -nsBoundsAudit only
                yield return new WaitForEndOfFrame();
                yield return null;
            }

            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Trial Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            LocalSession s = LocalSession.Current;
            if (s?.Profile == null) { Fail("no profile"); Finish(); yield break; }
            Click("ChallengeTrials");
            yield return Until(() => Router.Current == Trials, 10f);
            yield return new WaitForSeconds(0.8f);
            yield return Snap("01-trials");
            // More trials than one page: the page button shows the rest (a player reaches every trial by the buttons).
            if (Trials.PageCount > 1)
            {
                if (!Click("TrialsPage")) { Finish(); yield break; }
                yield return new WaitForSeconds(0.6f);
                if (Trials.Page != 1) Fail($"the page button showed page {Trials.Page + 1}, not 2");
                yield return Snap("01-trials-page2");
                Click("Trial0"); // the first trial on page 2, by its row
                yield return new WaitForSeconds(0.6f);
                string expected = s.Catalogue.ChallengeTrials.Trials[ChallengeTrialsScreen.Rows].Id;
                if (Trials.Open?.Id != expected) Fail($"page 2's first row opened {Trials.Open?.Id}, not {expected}");
                else Note($"page 2 of {Trials.PageCount}: its first row opens {expected}");
            }

            int passedCount = 0;
            // -nsTrialOnly <id,id,…>: drive only those trials (a targeted run; the full tour stays the regression check).
            string[] args = Environment.GetCommandLineArgs();
            int onlyAt = Array.IndexOf(args, "-nsTrialOnly");
            HashSet<string> only = onlyAt >= 0 && onlyAt + 1 < args.Length ? new HashSet<string>(args[onlyAt + 1].Split(',')) : null;
            if (only != null) Note($"only: {string.Join(", ", only)}");
            foreach (ChallengeTrialDef t in s.Catalogue.ChallengeTrials.Trials)
            {
                if (only != null && !only.Contains(t.Id)) continue;
                if (!Trials.SelectTrial(t.Id)) { Fail(t.Id + " is not listed"); continue; }
                yield return new WaitForSeconds(0.6f);
                yield return Snap($"02-{t.Id}-brief");
                bool earnedBefore = s.Profile.HasCompletedChallenge(t.Challenge);
                // Drift trials: the reference's own drift skill first, then the other measured skills until one run passes
                // (whether the published targets can be reached at all); time trials: one run.
                float refSkill = t.Targets.ReferenceDriftSkill > 0f ? t.Targets.ReferenceDriftSkill : 0.95f;
                float[] skills = t.JudgesDrift || t.Rules.AlternatingRecoveries ? new[] { refSkill }.Concat(new[] { 0.95f, 0.8f, 0.65f }.Where(k => Math.Abs(k - refSkill) > 0.001f)).ToArray()
                    : t.RequiredStoryRecords > 0 ? new[] { 0f, 0f } // once locked (the tour's fresh profile has no records), then with the records seeded
                    : t.Loaner.IsTunable ? new[] { 0f, 0f } // as supplied first (its tuning rules bite), then with the tour's own saved setup
                    : new[] { 0f };
                bool seededRecords = false, tuned = false;
                TrialVerdict v = null;
                foreach (float skill in skills)
                {
                    if (t.Loaner.IsTunable && v != null && !tuned)
                    {
                        // Tune the Loaner, buttons only: the tour's setup for this trial (TourSetup), checked legal, saved with the profile.
                        tuned = true;
                        if (!Trials.SelectTrial(t.Id)) break;
                        yield return new WaitForSeconds(0.4f);
                        if (!Click("TuneLoaner")) break;
                        yield return Until(() => Router.Current == TrialTune, 10f);
                        yield return new WaitForSeconds(0.6f);
                        yield return Snap($"02-{t.Id}-tune-supplied");
                        TourSetup(t.Id, out int[] slotClicks, out Dictionary<string, int> steps);
                        for (int i = 0; i < slotClicks.Length; i++)
                            for (int k = 0; k < slotClicks[i]; k++) { Click("TuneSlot" + i); yield return null; }
                        foreach (KeyValuePair<string, int> step in steps)
                        {
                            int row = TrialTune.Current.Controls.FindIndex(c => c.Key == step.Key);
                            if (row < 0) { Fail($"{t.Id}: no {step.Key} control to tune"); continue; }
                            // Its page of controls first (More Controls), then its row there.
                            for (int turn = 0; turn < 8 && TrialTune.TunePage != row / TrialTuneScreen.TuneRows; turn++) { Click("TrialTunePage"); yield return null; }
                            row %= TrialTuneScreen.TuneRows;
                            for (int k = 0; k < Math.Abs(step.Value); k++) { if (!Click((step.Value > 0 ? "TrialTunePlus" : "TrialTuneMinus") + row)) break; yield return null; }
                        }
                        TrialLoanerBuild tb = TrialTune.Current;
                        Note($"{t.Id} tune: {(tb.Ok ? "legal" : "NOT LEGAL: " + string.Join("; ", tb.Problems))}; parts {string.Join(", ", tb.Build.Parts.Select(kv => kv.Key + "=" + kv.Value))}; " +
                             $"tune {string.Join(", ", tb.Build.Tuning.Values.Select(kv => kv.Key + "=" + kv.Value))}; PI {tb.Pi?.Value}; final drive changed {tb.FinalDriveChanged}; aero at an end {tb.AeroAtExtreme}");
                        Click("TrialTuneSave");
                        yield return new WaitForSeconds(0.6f);
                        yield return Snap($"02-{t.Id}-tune-saved");
                        if (s.Profile.TrialSetups == null || !s.Profile.TrialSetups.ContainsKey(t.Id)) Fail($"{t.Id}: the setup was not saved with the profile");
                        Click("TrialTuneBack");
                        yield return Until(() => Router.Current == Trials, 10f);
                        yield return new WaitForSeconds(0.4f);
                    }
                    if (!Trials.SelectTrial(t.Id)) break;
                    yield return new WaitForSeconds(0.4f);
                    pendingAutopilotDriftSkill = skill;
                    pendingAutopilotNoHandbrake = t.Rules.NoHandbrake;
                    pendingAutopilotEdgeMargin = t.Targets.ReferenceEdgeMargin;
                    pendingAutopilotPaceScale = t.Targets.ReferencePaceScale;
                    OfflineRaceSession.AutopilotAimsChallengeGates = t.Rules.AllChallengeGates;
                    OfflineRaceSession.AutopilotApexHoldMetres = t.IsDrill ? 30f : 0f; // as a drill's reference was measured
                    OfflineRaceSession.AutopilotSlidesZonesOf = t.Rules.AlternatingRecoveries ? t.Challenge : null;
                    OfflineRaceSession.AutopilotShiftAtGates = t.ShiftGates.Count == 0 ? null : t.ShiftGates.ToArray();
                    // A marked overtake (CH40): follow the car ahead and attack only inside the marked zone, as the racecraft tour does.
                    bool zonePass = t.Rules.CleanZonePass || !string.IsNullOrEmpty(t.Rules.ZonePassRole);
                    OfflineRaceSession.AutopilotFollowSeconds = zonePass ? 0.5f : 0f;
                    OfflineRaceSession.AutopilotAttacksMarkedZones = zonePass;
                    OfflineRaceSession.AutopilotHoldsMarkedLanes = !string.IsNullOrEmpty(t.Rules.ZonePassRole) || t.Rules.CleanMerge;
                    OfflineRaceSession.AutopilotLaneHoldMetres = t.Rules.CleanMerge ? 25f : 0f; // a merge lane is judged to its last metre
                    LocalEvents.LastTrialVerdict = null;
                    OfflineRaceSession previousRace = activeRace;
                    if (!Click("StartTrial")) break;
                    // A challenge cup runs its legs through the cup page (Next Leg between them); a braking-lane lesson is driven
                    // in the lane (below); everything else is one race.
                    int legs = t.IsCup ? t.Legs.Count : t.IsLane ? 0 : 1;
                    bool started = true;
                    if (t.IsLane)
                    {
                        yield return Until(() => ActiveYard != null && ActiveYard.Ready, 60f);
                        TestYardSession yard = ActiveYard;
                        if (yard == null || !yard.Ready) { Fail($"{t.Id}: the lesson did not open"); started = false; }
                        else
                        {
                            // Straight-line starts: full throttle, then full brake when the stop predicted from the deceleration
                            // measured so far reaches the middle of the stop gate (refined after every stop).
                            yard.SimulationSpeed = 4;
                            float decel = 9.5f, aim = (yard.LessonStopFrom + yard.LessonStopTo) * 0.5f, gate = yard.LessonSpeedGateAlong;
                            bool braking = false;
                            yard.Script = (st, secs) =>
                            {
                                if (secs < 0.05f) braking = false;
                                float along = yard.LaneAlong(st.Position), mps = st.SpeedKmh / 3.6f;
                                if (!braking && along > gate + 2f && along + mps * mps / (2f * decel) >= aim) braking = true;
                                if (braking && st.SpeedKmh < 0.3f) return NightSignal.Vehicle.DriverInput.Neutral; // at rest: a held brake would select reverse
                                return braking ? NightSignal.Vehicle.DriverInput.Quantize(0f, 0f, 1f, NightSignal.Vehicle.InputButtons.None)
                                    : NightSignal.Vehicle.DriverInput.Quantize(0f, 1f, 0f, NightSignal.Vehicle.InputButtons.None);
                            };
                            // CH47: the supplied car, then the loaned package; CH02: starts until its stops count (at most twice as many).
                            List<bool> starts = t.Rules.CompareStops ? new List<bool> { false, true } : Enumerable.Repeat(false, Math.Max(1, t.LaneStarts) * 2).ToList();
                            int counted = 0;
                            foreach (bool useB in starts)
                            {
                                yard.ResetAndDrive(useB, 0);
                                yield return null;
                                TestYardRun r = yard.CurrentRun;
                                yield return Until(() => r == null || !float.IsNaN(r.StopAlong), 90f);
                                if (r != null && !float.IsNaN(r.StopAlong) && r.StopMetres > 1f)
                                {
                                    float v0 = r.StopFromKmh / 3.6f;
                                    decel = v0 * v0 / (2f * r.StopMetres);
                                    if (r.InStopGate && r.GateKmh >= t.Targets.LaneEntryKmh) counted++;
                                }
                                Note($"{t.Id} start ({(useB ? "B" : "A")}): {(r == null ? "no run" : $"{r.GateKmh:F1} km/h at the speed gate, stopped at {r.StopAlong:F1} m ({r.StopMetres:F1} m from {r.StopFromKmh:F0} km/h){(r.InStopGate ? " inside" : "")}")}");
                                if (!t.Rules.CompareStops && counted >= t.LaneStarts) break;
                            }
                            yield return new WaitForSeconds(0.6f);
                            yield return Snap($"03-{t.Id}-lesson");
                            yard.Script = null;
                            yard.RequestExit();
                            yield return Until(() => ActiveYard == null, 60f);
                        }
                    }
                    for (int leg = 0; leg < legs; leg++)
                    {
                        yield return Until(() => activeRace != null && activeRace != previousRace, 60f);
                        if (activeRace == null || activeRace == previousRace) { Fail($"{t.Id} leg {leg + 1} did not start"); started = false; break; }
                        previousRace = activeRace;
                        activeRace.Autopilot = true; // before the start, as its targets were measured
                        activeRace.SimulationSpeed = 12;
                        yield return Until(() => Router.Current == Results, 900f);
                        yield return new WaitForSeconds(1.2f);
                        if (skill == skills[0]) yield return Snap(t.IsCup ? $"03-{t.Id}-leg{leg + 1}-result" : $"03-{t.Id}-result");
                        Click("Continue");
                        if (!t.IsCup) break;
                        yield return Until(() => Router.Current == Cup, 15f);
                        yield return new WaitForSeconds(0.8f);
                        yield return Snap($"03-{t.Id}-cup-after-leg{leg + 1}");
                        Note($"{t.Id} leg {leg + 1}: {LocalEvents.LastTrialVerdict?.Summary}");
                        if (LocalEvents.CupTrialId != t.Id) break; // the cup ended (its last leg, or a leg not finished)
                        if (!Click("CupNext")) { started = false; break; }
                    }
                    if (!started) break;
                    if (t.IsCup) Click("CupLeave"); // Back to the trials
                    yield return Until(() => Router.Current == Trials, 15f);
                    yield return new WaitForSeconds(0.8f);
                    OfflineRaceSession.AutopilotAimsChallengeGates = false;
                    OfflineRaceSession.AutopilotFollowSeconds = 0f;
                    OfflineRaceSession.AutopilotAttacksMarkedZones = false;
                    OfflineRaceSession.AutopilotHoldsMarkedLanes = false;
                    OfflineRaceSession.AutopilotLaneHoldMetres = 0f;
                    OfflineRaceSession.AutopilotApexHoldMetres = 0f;
                    OfflineRaceSession.AutopilotSlidesZonesOf = null;
                    OfflineRaceSession.AutopilotShiftAtGates = null;
                    v = LocalEvents.LastTrialVerdict;
                    if (v == null) { Fail(t.Id + " was not judged"); break; }
                    Note($"{t.Id} ({t.Challenge} {t.Tier}){(t.JudgesDrift || t.Rules.AlternatingRecoveries ? $" at drift skill {skill:F2}{(skill == refSkill ? " (the reference's)" : "")}" : "")}: " +
                         $"{(v.Passed ? "PASSED" : "not passed")} — {v.Summary}");
                    if (t.IsRace)
                        foreach (string line in LocalEvents.LastTrialRacecraftLog) Note($"{t.Id}:   {line}");
                    if (t.Loaner.IsTunable && !tuned && t.Rules.FinalDriveChanged && !v.Summary.Contains("MISSED: your tune changes the final drive"))
                        Fail($"{t.Id}: the loaner as supplied was not held to its final-drive rule");
                    if (v.Passed && (!t.Loaner.IsTunable || tuned)) break;
                    if (t.RequiredStoryRecords > 0 && !seededRecords)
                    {
                        // The lock first: without the records the trial cannot pass, however fast the run.
                        if (!v.Summary.Contains($"MISSED: the {t.RequiredStoryRecords} story records")) Fail($"{t.Id} did not report its missing story records");
                        // Then the tour's own profile gets the Normal clears that award the records (as the diary tour seeds them).
                        for (int n = 1; n <= 25; n++) if (!s.Profile.Campaign.For(CampaignMode.Normal).Contains(n)) s.Profile.Campaign.For(CampaignMode.Normal).Add(n);
                        seededRecords = true;
                        Note($"{t.Id}: seeded Normal S01–S25 as cleared on the tour's profile — the diary now holds {DiaryScreen.Build(s.Profile, ContentLibrary.Load()).Count(e => e.Kind == "record")} records");
                    }
                }
                if (v == null) continue;
                bool kept = s.Profile.TrialsPassed.Contains(t.Id);
                Note($"{t.Id}: profile keeps it: {kept}; challenge earned: {s.Profile.HasCompletedChallenge(t.Challenge)}");
                yield return Snap($"04-{t.Id}-verdict");
                if (v.Passed) passedCount++;
                if (kept != v.Passed) Fail($"{t.Id}: the profile does not keep the verdict (passed {v.Passed}, kept {kept})");
                bool shouldEarn = TrialJudge.ChallengeEarned(s.Catalogue.ChallengeTrials, t.Challenge, new HashSet<string>(s.Profile.TrialsPassed));
                if (s.Profile.HasCompletedChallenge(t.Challenge) != (shouldEarn || earnedBefore)) Fail($"{t.Challenge}: earned {s.Profile.HasCompletedChallenge(t.Challenge)}, expected {shouldEarn}");
            }
            List<string> earned = s.Catalogue.ChallengeTrials.Trials.Select(t => t.Challenge).Distinct().Where(s.Profile.HasCompletedChallenge).ToList();
            Note($"{passedCount} of {s.Catalogue.ChallengeTrials.Trials.Count} trials passed by the autopilot; challenges earned: {(earned.Count == 0 ? "none" : string.Join(", ", earned))}; " +
                 $"wallet {s.Profile.WalletBalance:N0} cr");
            yield return Snap("05-trials-after");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
