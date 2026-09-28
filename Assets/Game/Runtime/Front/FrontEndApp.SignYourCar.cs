using System;
using System.Collections;
using System.Collections.Generic;
using NightSignal.Core.Customization;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// CH48 Sign Your Car evidence (<c>-nsSignYourCarTour</c>), buttons only: a fresh Local profile in an isolated folder →
        /// Garage → Appearance: a two-tone with a second colour and one decal layer, applied → the offline meet: the parked car
        /// must wear that livery (before V-097 the offline meet always drew it in stock paint) → inspect the own car → CH61
        /// and CH48 granted once and saved on the profile. Automation, not a person.
        /// </summary>
        IEnumerator SignYourCarTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "sign-your-car"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.SignYourCarTour] " + n);
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
            IEnumerator Step(string row, int times)
            {
                for (int i = 0; i < times; i++)
                {
                    Click(row + "/Next");
                    yield return new WaitForSeconds(0.25f);
                }
            }
            IEnumerator Section(int index)
            {
                for (int guard = 0; guard < 8 && GameObject.Find("Section/Value")?.GetComponent<TMPro.TextMeshProUGUI>()?.text != SectionName(index); guard++)
                {
                    Click("Section/Next");
                    yield return new WaitForSeconds(0.3f);
                }
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
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Signed Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            if (LocalSession.Current?.Profile == null) { Fail("no profile"); Finish(); yield break; }

            Click("Garage");
            yield return new WaitForSeconds(1.5f);
            Click("OpenAppearance");
            yield return Until(() => Router.Current == Appearance, 5f);
            yield return new WaitForSeconds(1f);
            if (Router.Current != Appearance) { Fail("the Appearance screen did not open"); Finish(); yield break; }
            yield return Section(2);
            yield return Step("Two-tone", 1);
            yield return Step("Second colour", 1);
            yield return Section(4);
            Click("Appearance-AddDecal");
            yield return new WaitForSeconds(0.5f);
            Click("Appearance-Apply");
            yield return new WaitForSeconds(1f);
            LiveryDocument applied = Appearance.Editor.Applied;
            Note($"applied: two-tone {applied.Paint.TwoTone} {applied.Paint.Primary}/{applied.Paint.Secondary}, decals {applied.Decals.Count} " +
                 $"({(applied.Decals.Count > 0 ? applied.Decals[0].Shape : "none")}); signed {LiveryChallenges.Signed(applied)}");
            if (!LiveryChallenges.Signed(applied)) Fail("the applied livery is not signed");

            Click("Back");
            yield return new WaitForSeconds(1f);
            Click("Back");
            yield return Until(() => Router.Current == OfflineHub, 5f);
            yield return new WaitForSeconds(0.8f);
            Click("Meet");
            yield return Until(() => ActiveMeet != null && ActiveMeet.Ready, 40f);
            if (ActiveMeet == null || !ActiveMeet.Ready) { Fail("the offline meet did not open"); Finish(); yield break; }
            yield return new WaitForSeconds(1.5f);
            Art.CarAppearance shown = ActiveMeet.Appearance;
            Note($"the parked car as drawn: two-tone {shown?.TwoTone}, decals {shown?.Decals.Count ?? 0}");
            if (shown == null || shown.Decals.Count != applied.Decals.Count || shown.TwoTone != applied.Paint.TwoTone)
                Fail("the offline meet does not draw the applied livery");
            ActiveMeet.Open("own-car");
            yield return new WaitForSeconds(1.5f);
            yield return Snap("01-signed-car-at-the-meet");
            var p = LocalSession.Current.Profile;
            Note($"challenges: CH61 {p.HasCompletedChallenge("CH61")}, CH48 {p.HasCompletedChallenge("CH48")}; reward COS-CH48 owned {p.OwnsCosmetic("COS-CH48")}");
            if (!p.HasCompletedChallenge("CH61")) Fail("CH61 was not granted");
            if (!p.HasCompletedChallenge("CH48")) Fail("CH48 was not granted");
            string id = p.ProfileId;
            bool reread = LocalSession.Current.Open(id, out string why) && LocalSession.Current.Profile.HasCompletedChallenge("CH48");
            Note($"re-read from disk: {reread} {why}");
            if (!reread) Fail("CH48 did not persist");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
