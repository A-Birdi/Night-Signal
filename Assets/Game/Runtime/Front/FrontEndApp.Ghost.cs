using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Ghosts;
using NightSignal.Race;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Ghost evidence (<c>-nsGhostTour</c>), buttons only, isolated profile folder: three offline Time Attack runs on C07 with
        /// the validator autopilot (the course SEEDED as owned when the new profile lacks it). Run 1 holds 3 s at GO — a slower
        /// but valid, reset-free run: the first ghost. Run 2 drives straight away with that ghost on the road: at least 1 s
        /// faster, so CH68 completes and the ghost is replaced. Run 3 holds 2 s: the new ghost runs ahead (screenshot) and every
        /// checkpoint delta is behind it; the stored ghost stays the faster run. Automation, not a person.
        /// </summary>
        IEnumerator GhostTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "ghost"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.GhostTour] " + n);
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
                float until = Time.realtimeSinceStartup + seconds;
                while (!condition() && Time.realtimeSinceStartup < until) yield return null;
            }
            IEnumerator Snap(string name)
            {
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
                yield return new WaitForEndOfFrame();
                yield return null;
            }
            string Value(string stepper) => GameObject.Find(stepper + "/Value")?.GetComponent<TMPro.TextMeshProUGUI>()?.text ?? "";

            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Ghost Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            var p = LocalSession.Current?.Profile;
            if (p == null) { Fail("no profile"); Finish(); yield break; }
            if (!p.OwnsCourse(LocalSession.Current.Catalogue, GhostChallenges.Course))
            {
                p.Courses.Add(new Core.Profiles.CourseEntitlement { CourseId = GhostChallenges.Course, Reference = "tour-seed", AcquiredUtc = DateTime.UtcNow });
                Note($"seeded: {GhostChallenges.Course} marked owned on the tour's profile");
            }
            OfflineHub.OnShow(); // re-label the course locks
            yield return new WaitForSeconds(0.5f);
            Click("Format/Next"); // Time Attack
            yield return new WaitForSeconds(0.3f);
            for (int i = 0; i < 40 && !Value("Course").StartsWith(GhostChallenges.Course); i++) { Click("Course/Next"); yield return new WaitForSeconds(0.1f); }
            Note($"freeplay: {Value("Course")} · {Value("Format")}");
            if (!Value("Course").StartsWith(GhostChallenges.Course)) { Fail("C07 could not be chosen"); Finish(); yield break; }

            var results = new List<(float hold, int ghosts, long result, bool valid, int deltas, long firstDelta)>();
            foreach (float hold in new[] { 3f, 0f, 2f })
            {
                yield return Until(() => Router.Current == OfflineHub, 10f);
                yield return new WaitForSeconds(1f);
                OfflineRaceSession.AutopilotHoldSeconds = hold;
                Click("Start");
                yield return Until(() => activeRace != null && activeRace.Phase != MatchPhase.Loading, 60f);
                if (activeRace == null) { Fail("the race did not start"); break; }
                activeRace.Autopilot = true;
                OfflineRaceSession race = activeRace;
                int ghosts = race.Ghosts.Count;
                Note($"run (hold {hold:F0} s): ghosts on the road {ghosts}{(ghosts > 0 ? " — " + race.Ghosts[0].Label : "")}");
                if (hold == 2f)
                {
                    yield return Until(() => race.Phase == MatchPhase.Racing, 20f);
                    yield return new WaitForSeconds(7f);
                    yield return Snap("01-ghost-ahead");
                }
                race.SimulationSpeed = 12;
                yield return Until(() => Router.Current == Results, 500f);
                OfflineRaceSession.AutopilotHoldSeconds = 0f;
                GhostRecording run = LastRunGhost;
                results.Add((hold, ghosts, run?.Header.ResultMicros ?? 0, run?.ValidPersonal == true, LastGhostDeltas.Count, LastGhostDeltas.Count > 0 ? LastGhostDeltas[0] : 0));
                Note($"run (hold {hold:F0} s): {run?.Count} samples, result {(run?.Header.ResultMicros ?? 0) / 1e6:F3} s, resets {run?.Header.Resets}, valid {run?.ValidPersonal}; " +
                     $"deltas vs ghost [{string.Join(", ", LastGhostDeltas.Select(d => (d / 1e6).ToString("+0.00;-0.00")))}]; CH68 {LocalSession.Current.Profile.HasCompletedChallenge("CH68")}");
                if (hold == 2f)
                {
                    yield return new WaitForSeconds(1.5f); // past the screen wipe
                    yield return Snap("02-results");
                }
                yield return new WaitForSeconds(0.8f);
                Click("Continue");
            }
            GhostRecording stored = LocalGhosts.Best(LocalSession.Current, GhostChallenges.Course, "time-attack");
            Note($"stored ghost: {(stored != null ? (stored.Header.ResultMicros / 1e6).ToString("F3") + " s, valid " + stored.ValidPersonal : "none")}");
            if (results.Count == 3)
            {
                if (results[0].ghosts != 0) Fail("the first run had a ghost");
                if (!results[0].valid) Fail("the first run is not a valid ghost");
                if (results[1].ghosts != 1 || results[2].ghosts != 1) Fail("later runs did not race the personal ghost");
                if (results[0].result - results[1].result < GhostChallenges.MarginMicros) Fail("the second run was not a second faster");
                if (!LocalSession.Current.Profile.HasCompletedChallenge("CH68")) Fail("CH68 was not granted");
                if (results[2].deltas == 0 || results[2].firstDelta <= 0) Fail("the third run shows no deficit against the ghost");
                if (stored == null || stored.Header.ResultMicros != results[1].result) Fail("the stored ghost is not the fastest run");
            }
            Finish();

            void Finish()
            {
                OfflineRaceSession.AutopilotHoldSeconds = 0f;
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
