using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Core.Story;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Story presentation evidence (<c>-nsStoryTour</c>), buttons only: a fresh Local profile in an isolated folder → the
        /// empty race diary → Campaign → S01: the full intro plays by itself (its length measured) → the validator autopilot
        /// races S01 → the results carry the reaction for the verdict → the race diary holds what the result unlocked, each entry
        /// readable → S01 again: the rematch intro is the short one and Skip ends it at once. Automation, not a person.
        /// </summary>
        IEnumerator StoryTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "story"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.StoryTour] " + n);
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
            StoryText story = NightSignal.Content.ContentLibrary.Load()?.Story;
            if (story == null) { Fail("no story in the content library"); Finish(); yield break; }

            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Story Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            if (LocalSession.Current?.Profile == null) { Fail("no profile"); Finish(); yield break; }

            // The diary before any stage: empty.
            Click("RaceDiary");
            yield return Until(() => Router.Current == Diary, 5f);
            yield return new WaitForSeconds(0.8f);
            Note($"diary before racing: {Diary.Entries.Count} entries");
            if (Diary.Entries.Count != 0) Fail("the diary is not empty before any stage");
            Click("Back");
            yield return Until(() => Router.Current == OfflineHub, 5f);

            // S01, first time: the whole intro, by itself.
            yield return new WaitForSeconds(0.8f);
            Click("Campaign");
            yield return Until(() => Router.Current == CampaignMap, 30f);
            yield return new WaitForSeconds(2f);
            Click("Node-S01");
            yield return new WaitForSeconds(0.8f);
            Click("Race");
            yield return Until(() => Router.Current == Story && !Story.Finished, 10f);
            if (Router.Current != Story) { Fail("the intro did not show"); Finish(); yield break; }
            int expected = story.Intro("S01", CampaignMode.Normal, false).Count;
            float started = Time.realtimeSinceStartup;
            bool snapped = false;
            var portraits = new Dictionary<int, string>();
            while (!Story.Finished && Time.realtimeSinceStartup - started < 40f)
            {
                if (!portraits.ContainsKey(Story.LineIndex)) portraits[Story.LineIndex] = Story.PortraitShown;
                if (!snapped && Story.LineIndex == 1)
                {
                    snapped = true;
                    yield return new WaitForSeconds(0.4f);
                    yield return Snap("01-intro-S01");
                    if (!Story.SavePortrait(System.IO.Path.Combine(dir, "01b-portrait-" + Story.PortraitShown + ".png"))) Fail("no portrait to save on a rival's line");
                }
                yield return null;
            }
            float took = Time.realtimeSinceStartup - started;
            // Each line's portrait is its speaker: the rival's own character, the timing crew's / radio's mark, none for narration.
            List<StoryLine> authored = story.Intro("S01", CampaignMode.Normal, false);
            Note("portraits by line: " + string.Join(", ", portraits.OrderBy(k => k.Key).Select(k => $"{k.Key + 1} {authored.ElementAtOrDefault(k.Key)?.Speaker}→{(k.Value.Length == 0 ? "none" : k.Value)}")));
            foreach (var kv in portraits)
            {
                string who = authored.ElementAtOrDefault(kv.Key)?.Speaker ?? "";
                string wantPortrait = who == "narration" || who == "setting" ? "" : who;
                if (kv.Value != wantPortrait) Fail($"line {kv.Key + 1} ({who}) showed the portrait \"{kv.Value}\"");
            }
            if (!portraits.Values.Any(v => v.StartsWith("R"))) Fail("no rival portrait was shown");
            Note($"S01 intro: {Story.LineCount} lines (authored {expected}), played by itself in {took:F1} s");
            if (Story.LineCount != expected) Fail("the first intro was not the full scene");
            if (took < 9.5f || took > 19.5f) Fail($"the intro took {took:F1} s (target 10–18 s)");

            yield return Until(() => activeRace != null && activeRace.Phase != Race.MatchPhase.Loading, 40f);
            if (activeRace == null) { Fail("the race did not start after the intro"); Finish(); yield break; }
            activeRace.Autopilot = true;
            yield return new WaitForSeconds(3f);
            if (activeRace != null) activeRace.SimulationSpeed = 12;
            yield return Until(() => Router.Current == Results, 400f);
            yield return new WaitForSeconds(1f);
            var p = LocalSession.Current.Profile;
            bool cleared = p.Campaign.For(CampaignMode.Normal).Contains(1);
            string shown = System.Text.RegularExpressions.Regex.Replace(Results.Reaction, "<[^>]+>", "");
            var candidates = cleared ? new[] { StoryOutcome.Win, StoryOutcome.ClearedButLost } : new[] { StoryOutcome.Loss, StoryOutcome.BeatRivalMissedBenchmark };
            bool matches = candidates.Any(o => story.Reaction("S01", CampaignMode.Normal, o, "R01").All(l => shown.Contains(StoryText.Fill(l.Line, p.DisplayName, "").Replace("<", "(").Replace(">", ")"))));
            Note($"S01 {(cleared ? "cleared" : "not cleared")}; reaction shown: \"{shown}\"; matches the verdict: {matches}");
            yield return Snap("02-results-reaction");
            if (shown.Length == 0) Fail("no reaction on the results");
            else if (!matches) Fail("the reaction does not match the verdict");

            // The diary now holds what the result unlocked.
            Click("Continue");
            yield return Until(() => Router.Current == CampaignMap, 10f);
            yield return new WaitForSeconds(1.5f);
            Click("Back");
            yield return Until(() => Router.Current == OfflineHub, 5f);
            yield return new WaitForSeconds(0.8f);
            Click("RaceDiary");
            yield return Until(() => Router.Current == Diary, 5f);
            yield return new WaitForSeconds(0.8f);
            var want = DiaryScreen.Build(p, NightSignal.Content.ContentLibrary.Load()).Select(e => e.Kind + ":" + e.Id).ToList();
            var got = Diary.Entries.Select(e => e.Kind + ":" + e.Id).ToList();
            Note($"diary after S01: {string.Join(", ", got)}");
            if (!got.SequenceEqual(want)) Fail("the diary does not hold what the profile unlocked");
            if (cleared && !(got.Contains("stage:S01") && got.Contains("crew:tea-hour"))) Fail("a cleared S01 did not open its entry and the Tea Hour introduction");
            if (got.Count > 0)
            {
                Click("Diary-Entry" + (got.Count - 1));
                yield return new WaitForSeconds(0.6f);
                Note($"reading: {Diary.Open?.Title} ({Diary.Open?.Text.Length} characters)");
                if (Diary.Open == null || string.IsNullOrEmpty(Diary.Open.Text)) Fail("a diary entry could not be read");
                yield return Snap("03-diary");
            }
            Click("Back");
            yield return Until(() => Router.Current == OfflineHub, 5f);

            // S01 again: the rematch intro is the short one, and Skip ends it at once.
            yield return new WaitForSeconds(0.8f);
            Click("Campaign");
            yield return Until(() => Router.Current == CampaignMap, 30f);
            yield return new WaitForSeconds(2f);
            Click("Node-S01");
            yield return new WaitForSeconds(0.8f);
            Click("Race");
            yield return Until(() => Router.Current == Story && !Story.Finished, 10f);
            Note($"rematch intro: {Story.LineCount} lines");
            if (Story.LineCount != StoryText.RematchLines) Fail("the rematch intro is not the short one");
            yield return new WaitForSeconds(0.6f);
            yield return Snap("04-rematch-intro");
            float skipAt = Time.realtimeSinceStartup;
            Click("Story-Skip");
            yield return Until(() => Story.Finished, 2f);
            Note($"skipped: {Story.Finished} after {Time.realtimeSinceStartup - skipAt:F2} s at line {Story.LineIndex + 1}");
            if (!Story.Finished) Fail("Skip did not end the intro");
            yield return Until(() => activeRace != null && activeRace.Phase != Race.MatchPhase.Loading, 40f);
            Note($"the rematch race started: {activeRace != null}");
            if (activeRace == null) Fail("the rematch race did not start after the skip");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
