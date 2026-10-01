using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
        /// Local Drift Attack evidence (<c>-nsFreeplayDriftTour</c>), buttons only, isolated profile folder: the hub's fourth format
        /// refuses a course without judged zones (C02), then Drift Attack on C01 against two authored AI in the course's own
        /// conditions, on C08 (its own wet surface: CH21's course) and on C12 under the Night, wet preset (CH26's course), C08
        /// and C12 SEEDED as owned. The autopilot drifts the judged zones with the drift skill the challenge references were
        /// measured with (0.95). Checks: finishers ranked by banked raw score, the table shows drift points, the Local record
        /// keeps the raw score, and which drift challenges the run earned. Automation, not a person.
        /// </summary>
        IEnumerator FreeplayDriftTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "drift"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.FreeplayDriftTour] " + n);
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
                AuditBounds(name);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
                yield return new WaitForEndOfFrame();
                yield return null;
            }
            string Text(string path) => GameObject.Find(path)?.GetComponent<TMPro.TextMeshProUGUI>()?.text ?? "";
            IEnumerator Choose(string row, Func<string, bool> wanted)
            {
                for (int i = 0; i < 40 && !wanted(Text(row + "/Value")); i++) { Click(row + "/Next"); yield return new WaitForSeconds(0.1f); }
            }

            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Drift Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            LocalProfile p = LocalSession.Current?.Profile;
            if (p == null) { Fail("no profile"); Finish(); yield break; }
            foreach (string id in new[] { "C08", "C12" })
                if (!p.OwnsCourse(LocalSession.Current.Catalogue, id))
                {
                    p.Courses.Add(new CourseEntitlement { CourseId = id, Reference = "tour-seed", AcquiredUtc = DateTime.UtcNow });
                    Note($"seeded: {id} marked owned on the tour's profile");
                }
            OfflineHub.OnShow();
            yield return new WaitForSeconds(0.5f);
            yield return Choose("Format", v => v.StartsWith("Drift Attack"));
            if (!Text("Format/Value").StartsWith("Drift Attack")) { Fail("the hub offers no Drift Attack format"); Finish(); yield break; }

            // A course without judged zones has no Drift Attack.
            yield return Choose("Course", v => v.StartsWith("C02"));
            yield return new WaitForSeconds(0.4f);
            bool c02Startable = GameObject.Find("Start")?.GetComponent<Button>()?.interactable == true;
            Note($"C02 Drift Attack: start {(c02Startable ? "AVAILABLE" : "unavailable")} — \"{Text("Freeplay/Note")}\"");
            yield return Snap("01-C02-no-zones");
            if (c02Startable || !Text("Freeplay/Note").Contains("no judged drift zones")) Fail("C02 offered Drift Attack");

            pendingAutopilotDriftSkill = 0.95f;
            foreach ((string course, string preset, int opponents, string surface) in new[]
                     { ("C01", "stage-default", 2, "dry"), ("C08", "stage-default", 0, "wet"), ("C12", "wet-night", 0, "wet") })
            {
                yield return Until(() => Router.Current == OfflineHub, 15f);
                yield return new WaitForSeconds(0.8f);
                yield return Choose("Course", v => v.StartsWith(course));
                string presetLabel = preset == Core.Rules.ConditionPresets.Default ? "Course's own" : Core.Rules.ConditionPresets.Find(preset).Label;
                yield return Choose("Conditions", v => v == presetLabel);
                yield return Choose("Opponents", v => v == (opponents == 0 ? "none" : $"{opponents} AI"));
                if (!Text("Course/Value").StartsWith(course) || Text("Conditions/Value") != presetLabel) { Fail($"{course} {preset} could not be chosen"); continue; }
                yield return new WaitForSeconds(0.4f);
                string run = $"{course}-{preset}";
                Note($"{run} hub: {Text("Format/Value")} · {Text("Course/Value")} · {Text("Conditions/Value")} · {Text("Opponents/Value")} — " +
                     $"start \"{Text("Start/Label")}\"; \"{Text("Freeplay/Note")}\"");
                yield return Snap($"02-{run}-hub");
                int challengesBefore = LocalSession.Current.Profile.Challenges.Count;
                Click("Start");
                yield return Until(() => activeRace != null && activeRace.Phase != MatchPhase.Loading, 60f);
                if (activeRace == null) { Fail($"{run}: the race did not start"); continue; }
                OfflineRaceSession race = activeRace;
                race.Autopilot = true;
                race.SimulationSpeed = 12;
                bool ranked = race.Rules.DriftRanking;
                string raced = race.Rules.Surface;
                yield return Until(() => Router.Current == Results, 900f);
                yield return new WaitForSeconds(1.2f);
                yield return Snap($"03-{run}-results");
                List<RaceEntrantResult> results = race.Results?.ToList() ?? new List<RaceEntrantResult>();
                RaceEntrantResult me = results.FirstOrDefault(r => r.Entrant.Human);
                List<RaceEntrantResult> finishers = results.Where(r => r.Outcome == RunOutcome.Finished).OrderBy(r => r.Placement).ToList();
                bool orderedByDrift = finishers.Zip(finishers.Skip(1), (a, b) => a.RawDriftScore >= b.RawDriftScore).All(x => x);
                bool tableShowsDrift = FindObjectsByType<TMPro.TextMeshProUGUI>(FindObjectsSortMode.None).Any(t => t.isActiveAndEnabled && t.name == "Table" && t.text.Contains(">DRIFT<"));
                string progression = FindObjectsByType<TMPro.TextMeshProUGUI>(FindObjectsSortMode.None).FirstOrDefault(t => t.isActiveAndEnabled && t.name == "Progression")?.text ?? "";
                string bestLine = progression.Split('\n').FirstOrDefault(l => l.Contains("New personal best")) ?? "";
                if (bestLine.Length > 0)
                {
                    Note($"{run}: results say \"{System.Text.RegularExpressions.Regex.Replace(bestLine, "<[^>]+>", "")}\"");
                    if (!bestLine.Contains("drift pts")) Fail($"{run}: the new personal best is not shown in drift points");
                }
                LocalProfile now = LocalSession.Current.Profile;
                RecordEntry record = now.Records.Entries.FirstOrDefault(r => r?.Key != null && r.Key.EventId == course + "/drift-attack" && r.Key.Conditions == surface);
                List<string> earned = now.Challenges.Skip(challengesBefore).Select(c => c.ChallengeId).ToList();
                Note($"{run}: drift-ranked {ranked}, surface {raced}; " + string.Join(", ", finishers.Select(r => $"P{r.Placement} {r.Entrant.Roster.DisplayName} {r.RawDriftScore:N0}")) +
                     $"; me {me?.Outcome} {me?.RawDriftScore:N0} raw, walls {me?.Entrant.Progress.WallIncidents}, chains {me?.Entrant.Drift.ChainsBanked}; table DRIFT column {tableShowsDrift}; " +
                     $"Local record {(record != null ? $"{record.Key.EventId} {record.Key.Metric} {record.Value:N0} ({record.Key.Conditions})" : "none")}; " +
                     $"challenges earned: {(earned.Count == 0 ? "none" : string.Join(", ", earned))}");
                if (!ranked) Fail($"{run}: not drift-ranked");
                if (raced != surface) Fail($"{run}: raced {raced}, expected {surface}");
                if (finishers.Count != opponents + 1) Note($"{run}: {finishers.Count} of {opponents + 1} finished");
                if (!orderedByDrift) Fail($"{run}: finishers are not ordered by banked drift score");
                if (!tableShowsDrift) Fail($"{run}: the results table does not show drift points");
                if (me?.Outcome == RunOutcome.Finished && (record == null || record.Key.Metric != MetricKind.RawDriftScore || record.Value != me.RawDriftScore))
                    Fail($"{run}: the Local record does not keep the raw score");
                Click("Continue");
            }
            pendingAutopilotDriftSkill = 0f;
            Finish();

            void Finish()
            {
                if (BoundsAuditOn && BoundsAutoSizeOverflows > 0) Fail($"{BoundsAutoSizeOverflows} label(s) do not fit their box (see the bounds report)");
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
