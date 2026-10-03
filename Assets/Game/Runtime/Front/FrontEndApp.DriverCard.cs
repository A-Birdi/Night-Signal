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
        /// pronouns saved → a card style with a locked frame refused → two records showcased → the profile re-read from disk
        /// → reward wardrobe: a locked item refused by name, two owned items worn and saved → an owned avatar emblem on the card
        /// → the offline meet builds the avatar from that look, dressed. The profile is new, so its two records and three
        /// challenge rewards are seeded into it (records and rewards reach a profile through ApplyEvent, covered by the .NET
        /// progression tests).
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
            foreach (var seed in new[] { ("C01", "sprint", 149_000L), ("C08", "drift-attack", 71_250L) })
                LocalSession.Current.Profile.Records.Entries.Add(new Core.Profiles.RecordEntry
                {
                    Key = new Core.Profiles.RecordKey
                    {
                        EventType = Core.Profiles.RecordEventType.Freeplay, EventId = seed.Item1 + "/" + seed.Item2, CourseId = seed.Item1, Format = seed.Item2,
                        Metric = seed.Item2 == "drift-attack" ? Core.Profiles.MetricKind.RawDriftScore : Core.Profiles.MetricKind.ElapsedTime,
                    },
                    Value = seed.Item3,
                });
            foreach (string cosmetic in new[] { "COS-CH39", "COS-CH72", "COS-CH68" })
                LocalSession.Current.Profile.Cosmetics.Add(new Core.Profiles.OwnedCosmetic { CosmeticId = cosmetic, Source = "CH" + cosmetic.Substring(6), AcquiredUtc = DateTime.UtcNow });

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

            // The showcase: the profile's own records, one per slot (slot N takes record N).
            Note($"records offered: {PlayerCard.RecordCount}");
            if (PlayerCard.RecordCount != 2) Fail($"expected the 2 seeded records, got {PlayerCard.RecordCount}");
            for (int s = 0; s < Math.Min(2, PlayerCard.RecordCount); s++)
                for (int k = 0; k <= s; k++) Click($"Showcase {s + 1}/Next");
            var chosen = new List<string>(PlayerCard.Showcase);
            Click("SaveCard");
            yield return new WaitForSeconds(0.8f);
            yield return Snap("01c-showcase");
            var stored = LocalSession.Current.Profile.Card.Showcase ?? new List<string>();
            var best = new List<string>();
            foreach (string l in PlayerCard.Card?.ShownLines ?? new List<string>()) if (l.StartsWith("Best: ")) best.Add(l);
            Note($"showcase chosen {string.Join(", ", chosen)}; stored {string.Join(", ", stored)}; drawn: {string.Join(" | ", best)} (\"{PlayerCard.Status}\")");
            if (chosen.Count != 2 || string.Join(",", stored) != string.Join(",", chosen)) Fail("the showcase was not saved");
            if (best.Count != 2) Fail("the card does not draw the showcased records");

            // The reward wardrobe: a satchel not earned yet is refused by name; the earned marshal coat and travel scarf are
            // worn and saved; the earned Ghostline avatar goes on the card.
            PlayerCard.ShowSection(2);
            yield return new WaitForSeconds(0.6f);
            if (!PlayerCard.Wear("workshop-satchel")) Fail("the satchel row is missing");
            yield return new WaitForSeconds(0.6f);
            yield return Snap("01d-wardrobe-locked");
            AuditBounds("Player Card / wardrobe");
            Click("SaveCard");
            yield return new WaitForSeconds(0.5f);
            string satchelWhy = PlayerCard.Status;
            Note($"locked satchel: \"{satchelWhy}\"");
            if (!satchelWhy.StartsWith("Not owned yet: Workshop Cloth Satchel")) Fail("a locked wardrobe item was not refused");
            PlayerCard.Wear("workshop-satchel", false);
            if (!PlayerCard.Wear("harbour-marshal-coat") || !PlayerCard.Wear("highland-scarf")) Fail("the coat or scarf rows are missing");
            Click("SaveCard");
            yield return new WaitForSeconds(1f);
            yield return Snap("01e-wardrobe-worn");
            CharacterLook dressed = PlayerLooks.Parse(LocalSession.Current.Profile.Card.Look);
            string worn = string.Join(",", dressed?.Wardrobe ?? new List<string>());
            Note($"wardrobe saved: [{worn}] (\"{PlayerCard.Status}\")");
            if (worn != "harbour-marshal-coat,highland-scarf") Fail("the worn wardrobe was not saved");
            saved = LocalSession.Current.Profile.Card.Look;
            PlayerCard.ShowSection(1);
            yield return new WaitForSeconds(0.4f);
            styleWanted.Avatar = "ghostline";
            PlayerCard.SetStyle(styleWanted);
            Click("SaveCard");
            yield return new WaitForSeconds(0.8f);
            yield return Snap("01f-card-avatar");
            AuditBounds("Player Card / card style");
            Note($"avatar: stored \"{LocalSession.Current.Profile.Card.AvatarId}\", drawn {PlayerCard.Card?.ShownAvatar?.Id}");
            if (LocalSession.Current.Profile.Card.AvatarId != "ghostline" || PlayerCard.Card?.ShownAvatar?.Id != "ghostline") Fail("the avatar emblem was not saved and drawn");

            string id = LocalSession.Current.Profile.ProfileId;
            bool reread = LocalSession.Current.Open(id, out string reopen);
            bool same = reread && LocalSession.Current.Profile.Card.Look == saved && LocalSession.Current.Profile.Card.Pronouns == "she/they" &&
                        Core.Profiles.LocalProgression.StyleOf(LocalSession.Current.Profile.Card, null).ContentEquals(styleWanted) &&
                        string.Join(",", LocalSession.Current.Profile.Card.Showcase ?? new List<string>()) == string.Join(",", chosen);
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
            CharacterRig avatar = GameObject.Find("PlayerAvatar")?.GetComponent<CharacterRig>();
            string pieces = avatar?.Look?.Worn == null ? "none" : string.Join(",", avatar.Look.Worn.ConvertAll(w => w.Item));
            Note($"the meet avatar wears: [{pieces}], {avatar?.Body?.sharedMaterials?.Length ?? 0} materials");
            if (pieces != "harbour-marshal-coat,highland-scarf") Fail("the meet avatar is not dressed in the saved wardrobe");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
