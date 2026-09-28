using System;
using System.Collections;
using System.Collections.Generic;
using NightSignal.Core.Rules;
using NightSignal.Core.Story;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Endings evidence (<c>-nsEndingTour</c>), buttons only, isolated profile folder: the Normal ending (three terrace scenes
        /// and the post-game note) paged through with Next; the Hard ending's dawn-run scene played by itself; then the Hard finale
        /// is marked cleared on this tour's profile (SEEDED: the validator autopilot is not relied on to win S30 — the trigger
        /// after a real first finale clear is the same PlayEnding call) and at the offline meet Shiori waits at the radio bench:
        /// the epilogue read to its end completes CH75 once. Automation, not a person.
        /// </summary>
        IEnumerator EndingTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "ending"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.EndingTour] " + n);
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
            if (story == null || story.NormalEnding.Count == 0) { Fail("no endings in the content library"); Finish(); yield break; }

            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Ending Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            if (LocalSession.Current?.Profile == null) { Fail("no profile"); Finish(); yield break; }
            string name = LocalSession.Current.Profile.DisplayName;

            // The Normal ending, paged with Next.
            bool ended = false;
            PlayEnding(CampaignMode.Normal, name, () => ended = true);
            yield return Until(() => Router.Current == Story, 5f);
            int pages = 0;
            bool shot = false;
            while (!ended && pages < 60)
            {
                yield return new WaitForSeconds(0.35f);
                if (!shot && Story.LineIndex >= 1) { shot = true; yield return Snap("01-normal-ending"); }
                Click("Story-Next");
                pages++;
            }
            int expected = 1;
            foreach (StoryScene sc in story.EndingAfterFinale(CampaignMode.Normal)) expected += 1 + sc.Lines.Count;
            Note($"Normal ending \"{story.NormalEndingTitle}\": {Story.LineCount} pages (3 scenes with their settings + the post-game note = {expected}), read to the end {Story.Completed}");
            if (!ended || !Story.Completed || Story.LineCount != expected) Fail("the Normal ending did not play through");
            Router.Show(OfflineHub, false);
            yield return new WaitForSeconds(0.8f);

            // The Hard ending's dawn-run scene, by itself.
            ended = false;
            float started = Time.realtimeSinceStartup;
            PlayEnding(CampaignMode.Hard, name, () => ended = true);
            yield return Until(() => Router.Current == Story, 5f);
            yield return new WaitForSeconds(2f);
            yield return Snap("02-hard-ending");
            yield return Until(() => ended, 60f);
            Note($"Hard ending \"{story.HardEndingTitle}\" (the finish scene): {Story.LineCount} lines, by itself in {Time.realtimeSinceStartup - started:F1} s, completed {Story.Completed}");
            if (!ended || !Story.Completed) Fail("the Hard ending did not play through by itself");
            Router.Show(OfflineHub, false);
            yield return new WaitForSeconds(0.8f);
            yield return new WaitForSeconds(0.5f);

            // SEEDED: the Hard finale cleared on this profile, then the epilogue at the meet.
            var p = LocalSession.Current.Profile;
            if (!p.Campaign.For(CampaignMode.Hard).Contains(StoryText.FinaleStage)) p.Campaign.For(CampaignMode.Hard).Add(StoryText.FinaleStage);
            Note("seeded: Hard S30 marked cleared on the tour's profile");
            Click("Meet");
            yield return Until(() => ActiveMeet != null && ActiveMeet.Ready, 40f);
            if (ActiveMeet == null || !ActiveMeet.Ready) { Fail("the offline meet did not open"); Finish(); yield break; }
            yield return new WaitForSeconds(1.5f);
            Note($"epilogue pages at the bench: {ActiveMeet.EpiloguePages}");
            if (ActiveMeet.EpiloguePages == 0) Fail("Shiori's epilogue was not offered");
            ActiveMeet.StandAtRadioBench();
            yield return new WaitForSeconds(0.8f);
            ActiveMeet.Open("epilogue");
            yield return new WaitForSeconds(0.8f);
            for (int k = 0; k < 40 && !ActiveMeet.EpilogueRead; k++)
            {
                if (k == 2) yield return Snap("03-epilogue-radio-bench");
                ActiveMeet.EpilogueNext();
                yield return new WaitForSeconds(0.3f);
            }
            yield return new WaitForSeconds(0.8f);
            Note($"epilogue read to its end {ActiveMeet.EpilogueRead}; CH75 {p.HasCompletedChallenge("CH75")}");
            if (!ActiveMeet.EpilogueRead) Fail("the epilogue could not be read to its end");
            if (!LocalSession.Current.Profile.HasCompletedChallenge("CH75")) Fail("CH75 was not granted");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
