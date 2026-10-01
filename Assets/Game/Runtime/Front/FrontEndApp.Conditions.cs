using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Ghosts;
using NightSignal.Race;
using NightSignal.Track;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Freeplay conditions evidence (<c>-nsFreeplayConditionsTour</c>), buttons only, isolated profile folder: offline Time
        /// Attack on C15 (the course's own conditions: damp) and on C01 (dry), each course SEEDED as owned when the new
        /// profile lacks it, then C01 under two lighting/weather presets chosen with the hub's Conditions row (Night, wet and
        /// Fog, damp). The hub names the conditions; the race, its personal ghost and its Local record key carry the surface,
        /// and the course is lit for the preset — as the game server resolves the same preset online. Automation, not a person.
        /// </summary>
        IEnumerator FreeplayConditionsTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "conditions"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.FreeplayConditionsTour] " + n);
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
            string Text(string path) => GameObject.Find(path)?.GetComponent<TMPro.TextMeshProUGUI>()?.text ?? "";

            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Weather Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            var p = LocalSession.Current?.Profile;
            if (p == null) { Fail("no profile"); Finish(); yield break; }
            foreach (string id in new[] { "C15", "C01" })
                if (!p.OwnsCourse(LocalSession.Current.Catalogue, id))
                {
                    p.Courses.Add(new Core.Profiles.CourseEntitlement { CourseId = id, Reference = "tour-seed", AcquiredUtc = DateTime.UtcNow });
                    Note($"seeded: {id} marked owned on the tour's profile");
                }
            OfflineHub.OnShow(); // re-label the course locks
            yield return new WaitForSeconds(0.5f);
            Click("Format/Next"); // Time Attack
            yield return new WaitForSeconds(0.3f);

            foreach ((string course, string preset, string expected, string lighting) in new[]
                     { ("C15", "stage-default", "damp", "evening"), ("C01", "stage-default", "dry", "late-afternoon"), ("C01", "wet-night", "wet", "night"), ("C01", "fog", "damp", "fog") })
            {
                yield return Until(() => Router.Current == OfflineHub, 15f);
                yield return new WaitForSeconds(0.8f);
                for (int i = 0; i < 40 && !Text("Course/Value").StartsWith(course); i++) { Click("Course/Next"); yield return new WaitForSeconds(0.1f); }
                if (!Text("Course/Value").StartsWith(course)) { Fail(course + " could not be chosen"); continue; }
                string wanted = Core.Rules.ConditionPresets.Find(preset).Id == Core.Rules.ConditionPresets.Default ? "Course's own" : Core.Rules.ConditionPresets.Find(preset).Label;
                for (int i = 0; i < 10 && Text("Conditions/Value") != wanted; i++) { Click("Conditions/Next"); yield return new WaitForSeconds(0.1f); }
                if (Text("Conditions/Value") != wanted) { Fail($"{preset} could not be chosen"); continue; }
                yield return new WaitForSeconds(0.4f);
                string run = $"{course}-{preset}";
                string note = Text("Freeplay/Note");
                Note($"{run} hub: {Text("Course/Value")} · {Text("Format/Value")} · {Text("Conditions/Value")} — \"{note}\"");
                yield return Snap($"01-{run}-hub");
                if (!note.Contains("Conditions:")) Fail($"{run}: the hub does not name the conditions");

                Click("Start");
                yield return Until(() => activeRace != null && activeRace.Phase != MatchPhase.Loading, 60f);
                if (activeRace == null) { Fail($"{run}: the race did not start"); continue; }
                OfflineRaceSession race = activeRace;
                string lit = CourseRuntime.Active?.TimeOfDay;
                bool headlights = CourseRuntime.Active != null && CourseRuntime.Active.Dark;
                yield return Until(() => race.Phase == MatchPhase.Racing, 30f);
                race.Autopilot = true;
                race.SimulationSpeed = 1;
                yield return new WaitForSeconds(4f);
                yield return Snap($"02-{run}-racing"); // the course as lit for the conditions, a few seconds in at real speed
                race.SimulationSpeed = 12;
                string raced = race.Rules.Surface;
                yield return Until(() => Router.Current == Results, 600f);
                yield return new WaitForSeconds(1.2f);
                yield return Snap($"03-{run}-results");
                GhostRecording ghost = LastRunGhost;
                var record = LocalSession.Current.Profile.Records.Entries.FirstOrDefault(r => r?.Key != null && r.Key.EventId.StartsWith(course + "/") && r.Key.Conditions == expected);
                Note($"{run}: raced surface {raced}, lit for {lit}, headlights {(headlights ? "on" : "off")}; ghost surface {ghost?.Header.Surface ?? "none"} ({(ghost?.Header.ResultMicros ?? 0) / 1e6:F3} s); " +
                     $"Local record {(record != null ? record.Key.EventId + " conditions " + record.Key.Conditions : "none")}");
                if (raced != expected) Fail($"{run} raced {raced}, expected {expected}");
                if (lit != lighting) Fail($"{run} lit for {lit}, expected {lighting}");
                if (headlights != Atmosphere.LightingPresets.For(lighting).PracticalLights) Fail($"{run}: headlights {(headlights ? "on" : "off")} under {lighting}");
                if (ghost == null || ghost.Header.Surface != expected) Fail($"{run}: the ghost's surface is {ghost?.Header.Surface ?? "missing"}, expected {expected}");
                if (record == null) Fail($"{run}: no Local record in {expected} conditions");
                Click("Continue");
            }
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
