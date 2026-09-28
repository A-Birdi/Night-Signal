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
        /// CH70 evidence (<c>-nsDiaryTour</c>), buttons only, isolated profile folder: Normal S01–S24 are marked cleared on this
        /// tour's profile (SEEDED — they open all six crew introductions; racing 24 stages is not the point here), the race diary
        /// is opened and each crew introduction read with its row button (each marked read and saved), then S01 is raced again by
        /// the validator autopilot — its field has crew members — and the finish completes CH70. Automation, not a person.
        /// </summary>
        IEnumerator DiaryTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "diary"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.DiaryTour] " + n);
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

            yield return Until(() => GameObject.Find("OfflinePlay") != null, 20f);
            yield return new WaitForSeconds(1f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Diary Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            if (LocalSession.Current?.Profile == null) { Fail("no profile"); Finish(); yield break; }
            var p = LocalSession.Current.Profile;
            for (int n = 1; n <= 24; n++) if (!p.Campaign.For(CampaignMode.Normal).Contains(n)) p.Campaign.For(CampaignMode.Normal).Add(n);
            Note("seeded: Normal S01–S24 marked cleared on the tour's profile");

            Click("RaceDiary");
            yield return Until(() => Router.Current == Diary, 5f);
            yield return new WaitForSeconds(0.8f);
            var crewIdx = Diary.Entries.Select((e, i) => (e, i)).Where(x => x.e.Kind == "crew").Select(x => x.i).ToList();
            Note($"diary: {Diary.Entries.Count} entries, crew introductions at {string.Join(", ", crewIdx)}");
            if (crewIdx.Count != 6) Fail($"expected six crew introductions, found {crewIdx.Count}");
            int page = 0;
            foreach (int k in crewIdx)
            {
                while (page < k / DiaryScreen.PerPage) { Click("Diary-NextPage"); page++; yield return new WaitForSeconds(0.3f); }
                Click("Diary-Entry" + (k % DiaryScreen.PerPage));
                yield return new WaitForSeconds(0.6f);
            }
            yield return Snap("01-diary-crews-read");
            Note($"read: {string.Join(", ", p.DiaryRead)} — all six {DiaryChallenges.AllCrewsRead(p.DiaryRead, NightSignal.Content.ContentLibrary.Load().Story.Crews)}");
            if (!DiaryChallenges.AllCrewsRead(p.DiaryRead, NightSignal.Content.ContentLibrary.Load().Story.Crews)) Fail("not every crew introduction was marked read");
            Click("Back");
            yield return Until(() => Router.Current == OfflineHub, 5f);

            // S01 again: crew members in the field; the finish completes CH70.
            yield return new WaitForSeconds(0.8f);
            Click("Campaign");
            yield return Until(() => Router.Current == CampaignMap, 30f);
            yield return new WaitForSeconds(2f);
            Click("Node-S01");
            yield return new WaitForSeconds(0.8f);
            Click("Race");
            yield return Until(() => Router.Current == Story && !Story.Finished, 10f);
            if (Router.Current == Story) Click("Story-Skip");
            yield return Until(() => activeRace != null && activeRace.Phase != Race.MatchPhase.Loading, 40f);
            if (activeRace == null) { Fail("the race did not start"); Finish(); yield break; }
            Note($"S01 field: {string.Join(", ", activeRace.OpposingAi)}");
            activeRace.Autopilot = true;
            yield return new WaitForSeconds(3f);
            if (activeRace != null) activeRace.SimulationSpeed = 12;
            yield return Until(() => Router.Current == Results, 400f);
            yield return new WaitForSeconds(1f);
            yield return Snap("02-results-ch70");
            p = LocalSession.Current.Profile;
            Note($"CH70 completed: {p.HasCompletedChallenge("CH70")}");
            if (!p.HasCompletedChallenge("CH70")) Fail("CH70 was not granted");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
