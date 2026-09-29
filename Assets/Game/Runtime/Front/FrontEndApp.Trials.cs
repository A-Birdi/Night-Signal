using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Race;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>The autopilot starts slides by power in the next offline race (tours measuring or driving CH25's trial).</summary>
        bool pendingAutopilotNoHandbrake;

        /// <summary>Starts a challenge trial (docs/CHALLENGE_TRIALS.md) in its supplied loaner; the result returns to <paramref name="returnTo"/>.</summary>
        public void StartTrial(ChallengeTrialDef trial, UIScreen returnTo) => StartLocalEvent(LocalEvents.Trial(trial), returnTo);

        /// <summary>The trial's loaner resolved like a garage build; the plan records the build hash driven (null = not driven).</summary>
        ResolvedCarSpec TrialLoanerSpec(LocalEventPlan plan, out string problem)
        {
            problem = null;
            ContentLibrary lib = ContentLibrary.Load();
            ChallengeTrialDef t = lib.Catalogue.ChallengeTrials.Find(plan.TrialId);
            if (t == null) { problem = "unknown challenge trial " + plan.TrialId; return null; }
            CarDef car = lib.Catalogue.Car(t.Loaner.Car);
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

            int passedCount = 0;
            foreach (ChallengeTrialDef t in s.Catalogue.ChallengeTrials.Trials)
            {
                if (!Trials.SelectTrial(t.Id)) { Fail(t.Id + " is not listed"); continue; }
                yield return new WaitForSeconds(0.6f);
                yield return Snap($"02-{t.Id}-brief");
                bool earnedBefore = s.Profile.HasCompletedChallenge(t.Challenge);
                // Drift trials: the reference's own drift skill first, then the other measured skills until one run passes
                // (whether the published targets can be reached at all); time trials: one run.
                float refSkill = t.Targets.ReferenceDriftSkill > 0f ? t.Targets.ReferenceDriftSkill : 0.95f;
                float[] skills = t.JudgesDrift ? new[] { refSkill }.Concat(new[] { 0.95f, 0.8f, 0.65f }.Where(k => Math.Abs(k - refSkill) > 0.001f)).ToArray() : new[] { 0f };
                TrialVerdict v = null;
                foreach (float skill in skills)
                {
                    if (!Trials.SelectTrial(t.Id)) break;
                    yield return new WaitForSeconds(0.4f);
                    pendingAutopilotDriftSkill = skill;
                    pendingAutopilotNoHandbrake = t.Rules.NoHandbrake;
                    LocalEvents.LastTrialVerdict = null;
                    if (!Click("StartTrial")) break;
                    yield return Until(() => activeRace != null, 60f);
                    if (activeRace == null) { Fail(t.Id + " did not start"); break; }
                    activeRace.Autopilot = true; // before the start, as its targets were measured
                    activeRace.SimulationSpeed = 12;
                    yield return Until(() => Router.Current == Results, 900f);
                    yield return new WaitForSeconds(1.2f);
                    if (skill == skills[0]) yield return Snap($"03-{t.Id}-result");
                    Click("Continue");
                    yield return Until(() => Router.Current == Trials, 15f);
                    yield return new WaitForSeconds(0.8f);
                    v = LocalEvents.LastTrialVerdict;
                    if (v == null) { Fail(t.Id + " was not judged"); break; }
                    Note($"{t.Id} ({t.Challenge} {t.Tier}){(t.JudgesDrift ? $" at drift skill {skill:F2}{(skill == refSkill ? " (the reference's)" : "")}" : "")}: " +
                         $"{(v.Passed ? "PASSED" : "not passed")} — {v.Summary}");
                    if (v.Passed) break;
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
