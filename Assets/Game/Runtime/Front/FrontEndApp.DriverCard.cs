using System;
using System.Collections;
using System.Collections.Generic;
using NightSignal.Characters;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Offline Driver Card evidence (<c>-nsDriverCardTour</c>), buttons only (no keyboard, so no window focus is needed):
        /// a fresh Local profile in an isolated folder → Driver Card → pronouns with markup refused → a starting look and
        /// pronouns saved → the profile re-read from disk → the offline meet builds the avatar from that look.
        /// </summary>
        IEnumerator DriverCardTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "driver-card"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.DriverCardTour] " + n);
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
            GameObject.Find("ProfileName")?.GetComponent<TMPro.TMP_InputField>()?.SetTextWithoutNotify("Card Driver");
            Click("Create");
            yield return Until(() => LocalSession.Current?.Profile != null && Router.Current == OfflineHub, 10f);
            if (LocalSession.Current?.Profile == null) { Fail("no profile"); Finish(); yield break; }

            Click("DriverCard");
            yield return Until(() => Router.Current == PlayerCard, 5f);
            yield return new WaitForSeconds(1f);
            if (Router.Current != PlayerCard) { Fail("the Driver Card did not open"); Finish(); yield break; }
            var pronouns = GameObject.Find("CardPronouns")?.GetComponent<TMPro.TMP_InputField>();
            PlayerCard.ChooseStart(5);
            pronouns.text = "<b>";
            Click("SaveCard");
            yield return new WaitForSeconds(0.5f);
            string refused = PlayerCard.Status;
            Note($"markup in pronouns: \"{refused}\"; stored look still \"{LocalSession.Current.Profile.Card.Look}\"");
            if (!refused.Contains("Pronouns") || LocalSession.Current.Profile.Card.Look.Length != 0) Fail("pronouns with markup were not refused");
            pronouns.text = "she/they";
            Click("SaveCard");
            yield return new WaitForSeconds(0.8f);
            yield return Snap("01-driver-card-saved");
            string saved = LocalSession.Current.Profile.Card.Look;
            string expected = PlayerLooks.Canonical(PlayerLooks.Presets[4]);
            Note($"saved: \"{PlayerCard.Status}\" look {saved.Length} chars (starting look 5: {saved == expected}), pronouns \"{LocalSession.Current.Profile.Card.Pronouns}\"");
            if (saved != expected) Fail("the saved look is not starting look 5");

            // The card's style: a reward item not owned yet is refused by name; free items are saved.
            PlayerCard.ShowSection(1);
            yield return new WaitForSeconds(0.6f);
            var styleWanted = new Core.Customization.CardStyle
            {
                Background = "dusk", Frame = "balance-point", Motif = "signal-bars", Title = "touring-driver", Layout = "standard", Region = "PT",
                PreferredCar = LocalSession.Current.Profile.Cars[0].ModelId,
            };
            PlayerCard.SetStyle(styleWanted);
            Click("SaveCard");
            yield return new WaitForSeconds(0.5f);
            string lockedWhy = PlayerCard.Status;
            Note($"locked frame: \"{lockedWhy}\"");
            if (!lockedWhy.StartsWith("Not owned yet: Balance Point Frame")) Fail("a locked card frame was not refused");
            styleWanted.Frame = "double";
            PlayerCard.SetStyle(styleWanted);
            Click("SaveCard");
            yield return new WaitForSeconds(0.8f);
            yield return Snap("01b-card-style");
            Core.Customization.CardStyle styleSaved = Core.Profiles.LocalProgression.StyleOf(LocalSession.Current.Profile.Card, null);
            Note($"style saved: {styleSaved.Canonical()} (preview {PlayerCard.Card?.Shown?.Canonical()})");
            if (!styleSaved.ContentEquals(styleWanted)) Fail("the card style was not saved");

            string id = LocalSession.Current.Profile.ProfileId;
            bool reread = LocalSession.Current.Open(id, out string reopen);
            bool same = reread && LocalSession.Current.Profile.Card.Look == saved && LocalSession.Current.Profile.Card.Pronouns == "she/they" &&
                        Core.Profiles.LocalProgression.StyleOf(LocalSession.Current.Profile.Card, null).ContentEquals(styleWanted);
            Note($"re-read from disk: {same} {reopen}");
            if (!same) Fail("the card did not persist");

            Click("Back");
            yield return Until(() => Router.Current == OfflineHub, 5f);
            yield return new WaitForSeconds(0.8f);
            Click("Meet");
            yield return Until(() => ActiveMeet != null && ActiveMeet.Ready, 40f);
            if (ActiveMeet == null || !ActiveMeet.Ready) { Fail("the offline meet did not open"); Finish(); yield break; }
            yield return new WaitForSeconds(1.5f);
            yield return Snap("02-meet-avatar");
            bool used = ActiveMeet.PlayerLook != null && PlayerLooks.Canonical(ActiveMeet.PlayerLook) == saved;
            Note($"the offline meet's avatar is built from the Driver Card's look: {used}");
            if (!used) Fail("the offline meet did not use the Driver Card's look");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
