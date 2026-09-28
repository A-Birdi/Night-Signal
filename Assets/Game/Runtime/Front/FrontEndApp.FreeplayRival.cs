using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Race;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Freeplay rivals and the rival reference (<c>-nsFreeplayRivalTour</c>), buttons only, isolated profile folder. On C01:
        /// a Freeplay race with one opponent named on the Lead rival row (R08, a late-brake-anchor) — the field must lead with
        /// that rival and the finish must record its archetype (CH73 progress; a win also counts towards CH38); then a Time
        /// Attack, where the course's authored rival reference (R01's recorded run) must be on the road beside the player.
        /// Validator autopilot; automation, not a person.
        /// </summary>
        IEnumerator FreeplayRivalTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "freeplay-rival"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.FreeplayRivalTour] " + n);
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
            string Line() => GameObject.Find("Archetypes")?.GetComponent<TMPro.TextMeshProUGUI>()?.text ?? "";
            const string Course = "C01", Lead = "R08";

            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Rival Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            if (LocalSession.Current?.Profile == null) { Fail("no profile"); Finish(); yield break; }
            yield return new WaitForSeconds(0.5f);
            for (int i = 0; i < 40 && !Value("Course").StartsWith(Course); i++) { Click("Course/Next"); yield return new WaitForSeconds(0.1f); }
            for (int i = 0; i < 10 && Value("Opponents") != "1 AI"; i++) { Click("Opponents/Prev"); yield return new WaitForSeconds(0.1f); }
            if (!OfflineHub.SelectRival(Lead)) Fail("the lead rival row does not offer " + Lead);
            yield return new WaitForSeconds(0.5f);
            Note($"freeplay: {Value("Course")} · {Value("Format")} · {Value("Opponents")} · lead {Value("Lead rival")} · {Line()}");
            yield return Snap("01-lead-rival");

            // The race against the named rival.
            OfflineRaceSession.AutopilotHoldSeconds = 0f;
            Click("Start");
            yield return Until(() => activeRace != null && activeRace.Phase != MatchPhase.Loading, 60f);
            if (activeRace == null) { Fail("the race did not start"); Finish(); yield break; }
            activeRace.Autopilot = true;
            Note("field: " + string.Join(", ", activeRace.OpposingAi));
            if (activeRace.OpposingAi.FirstOrDefault() != Lead) Fail("the field does not lead with the named rival");
            activeRace.SimulationSpeed = 12;
            yield return Until(() => Router.Current == Results, 500f);
            var p = LocalSession.Current.Profile;
            string tendency = ArchetypeChallenges.TendencyOf(LocalSession.Current.Catalogue, Lead);
            Note($"after the race: raced [{string.Join(", ", p.ArchetypesRaced)}], won since a quit [{string.Join(", ", p.ArchetypeWinStreak)}]");
            if (!p.ArchetypesRaced.Contains(tendency)) Fail($"the race against {Lead} did not record {tendency}");
            yield return new WaitForSeconds(1.5f);
            yield return Snap("02-results");
            Click("Continue");
            yield return Until(() => Router.Current == OfflineHub, 10f);
            yield return new WaitForSeconds(0.8f);
            Note("hub line: " + Line());
            if (!Line().Contains("raced 1/")) Fail("the hub does not show the archetype raced");
            yield return Snap("03-progress");

            // Time Attack with the course's authored rival reference.
            Click("Format/Next");
            yield return new WaitForSeconds(0.5f);
            Click("Start");
            yield return Until(() => activeRace != null && activeRace.Phase != MatchPhase.Loading, 60f);
            if (activeRace == null) { Fail("the Time Attack did not start"); Finish(); yield break; }
            activeRace.Autopilot = true;
            OfflineRaceSession ta = activeRace;
            string overlays = string.Join(" | ", ta.Ghosts.Select(g => g.Label));
            Note("time attack overlays: " + (overlays.Length > 0 ? overlays : "none"));
            RivalDef reference = RivalReference.For(LocalSession.Current.Catalogue, Course);
            if (!ta.Ghosts.Any(g => RivalReferenceGhosts.IsReference(g.Recording) && g.Recording.Header.Driver == reference?.Name))
                Fail($"the rival reference ({reference?.Id}) is not on the road");
            yield return Until(() => ta.Phase == MatchPhase.Racing, 20f);
            yield return new WaitForSeconds(6f);
            yield return Snap("04-rival-reference");
            ta.SimulationSpeed = 12;
            yield return Until(() => Router.Current == Results, 500f);
            Note($"time attack: {(LastRunGhost?.Header.ResultMicros ?? 0) / 1e6:F3} s; deltas vs the first overlay [" +
                 string.Join(", ", LastGhostDeltas.Select(d => (d / 1e6).ToString("+0.00;-0.00"))) + "]");
            yield return new WaitForSeconds(1.5f);
            yield return Snap("05-time-attack-results");
            Click("Continue");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
