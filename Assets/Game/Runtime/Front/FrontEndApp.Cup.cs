using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.Race;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Custom Cup evidence (<c>-nsCupTour</c>), buttons only, isolated profile folder: the offline hub's Custom Cup on three
        /// sprint/circuit courses the new profile can race, two authored opponents; each leg driven by the validator autopilot,
        /// the cup table read after every leg. Checks: three legs raced by the same field, every table line's points equal to
        /// its placings under the published points, each leg paid as an ordinary race. Automation, not a person.
        /// </summary>
        IEnumerator CupTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "cup"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.CupTour] " + n);
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
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Cup Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            LocalSession s = LocalSession.Current;
            if (s?.Profile == null) { Fail("no profile"); FinishCup(); yield break; }
            // Three different sprint/circuit courses this new profile may race.
            List<string> legs = s.Catalogue.Courses.Where(c => (c.Format == "sprint" || c.Format == "circuit") && Application.CanStreamedLevelBeLoaded(c.Id)
                    && LocalProgression.CanStartFreeplay(s.Profile, s.Catalogue, c.Id, out _)).Select(c => c.Id).Take(3).ToList();
            Note("schedule chosen: " + string.Join(" → ", legs));
            if (legs.Count < 3) { Fail("fewer than three raceable sprint/circuit courses"); FinishCup(); yield break; }
            yield return new WaitForSeconds(0.5f);
            Click("Format/Next");
            Click("Format/Next"); // Custom Cup
            for (int i = 0; i < 40 && !Value("Course").StartsWith(legs[0]); i++) { Click("Course/Next"); yield return new WaitForSeconds(0.05f); }
            for (int i = 0; i < 10 && Value("Opponents") != "2 AI"; i++) { Click("Opponents/Prev"); yield return new WaitForSeconds(0.05f); }
            if (!OfflineHub.SelectCupLegs(legs[1], legs[2])) Fail("the cup legs could not be chosen");
            yield return new WaitForSeconds(0.5f);
            Note($"setup: {Value("Format")} · {Value("Course")} · {Value("Leg 2")} · {Value("Leg 3")} · {Value("Opponents")}");
            yield return Snap("01-cup-setup");
            long walletBefore = s.Profile.WalletBalance;
            Click("Start");
            var fields = new List<string>();
            for (int leg = 1; leg <= CupTable.Legs; leg++)
            {
                yield return Until(() => activeRace != null && activeRace.Phase == MatchPhase.Racing, 90f);
                if (activeRace == null) { Fail($"leg {leg} did not start"); break; }
                activeRace.Autopilot = true;
                activeRace.SimulationSpeed = 12;
                fields.Add(string.Join(",", activeRace.OpposingAi));
                yield return Until(() => Router.Current == Results, 600f);
                yield return new WaitForSeconds(1.2f);
                if (leg == 1) yield return Snap("02-leg1-results");
                Click("Continue");
                yield return Until(() => Router.Current == Cup, 10f);
                yield return new WaitForSeconds(0.8f);
                CupTable t = Cup.Table;
                Note($"after leg {leg}: " + string.Join(" | ", t.Standings().Select(e => $"{e.Name} {e.Points} pts ({string.Join(" ", e.Places.Select(p => p?.ToString() ?? "–"))})")));
                yield return Snap($"03-table-after-leg{leg}");
                if (leg < CupTable.Legs) Click("CupNext");
            }
            CupTable table = Cup.Table;
            if (table == null || !table.Complete) Fail("the cup did not complete three legs");
            else
            {
                foreach (CupEntrant e in table.Standings())
                    if (e.Points != e.Places.Sum(p => CupTable.PointsFor(p))) Fail($"{e.Name}: {e.Points} points do not match its placings");
                Note($"final: you are {table.PositionOf("you")} of {table.Standings().Count}; wallet {walletBefore:N0} -> {s.Profile.WalletBalance:N0} cr");
            }
            if (fields.Distinct().Count() != 1) Fail("the field changed between legs: " + string.Join(" / ", fields));
            else Note("the same field raced every leg: " + fields[0]);
            if (s.Profile.WalletBalance <= walletBefore) Fail("the legs were not paid as races");
            FinishCup();

            void FinishCup()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
