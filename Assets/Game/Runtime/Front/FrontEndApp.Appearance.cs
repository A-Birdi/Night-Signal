using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Customization;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Appearance evidence run (<c>-nsAppearanceTour</c>): a fresh Local profile in an isolated folder → Garage → Appearance.
        /// Every section is edited with the real controls (body kit, wheels, paint, lamps and plate, two decal layers), undo and
        /// redo are exercised, a locked swatch is refused on Apply, the livery is applied and saved as two presets, the profile is
        /// re-read from disk, and S01 starts with the car showing the applied livery. Automation, labelled as such.
        /// </summary>
        IEnumerator AppearanceTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "appearance"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string s) => Debug.Log("[NightSignal.AppearanceTour] " + s);
            void Shot(string name)
            {
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
                if (Router.Current != Appearance) return;
                Note($"{name}: {Appearance.PreviewDebug}");
                Appearance.SavePreview(System.IO.Path.Combine(dir, name + "-preview.png"));
            }
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { failures.Add("button not available: " + name); Note("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }
            // A stepper row is named after its label; its arrows are "Prev" and "Next".
            IEnumerator Step(string row, int times, bool back = false)
            {
                for (int i = 0; i < times; i++)
                {
                    Click(row + "/" + (back ? "Prev" : "Next"));
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

            yield return new WaitForSeconds(3f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName").GetComponent<TMPro.TMP_InputField>().text = "Livery Driver";
            Click("Create");
            yield return new WaitForSeconds(1.2f);
            Click("Garage");
            yield return new WaitForSeconds(1.5f);
            Click("OpenAppearance");
            yield return new WaitForSeconds(1.5f);
            if (Router.Current != Appearance) { failures.Add("the Appearance screen did not open"); goto done; }
            string carId = Appearance.Editor.CarId;
            Note($"car {carId}: {Appearance.Editor.Chassis.Style}");
            Shot("01-appearance-stock");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame

            // Body kit: the second/next variant of every family this chassis offers.
            yield return Step("Front", 2);
            yield return Step("Rear", 1);
            yield return Step("Sides", 1);
            yield return Step("Rear aero", 2);
            yield return Step("Exhaust", 1);
            yield return new WaitForSeconds(0.6f);
            Shot("02-body-kit");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            LiveryDocument d = Appearance.Editor.Draft;
            Note($"body: front {d.Body.Front}, rear {d.Body.Rear}, side {d.Body.Side}, rearAero {d.Body.RearAero}, exhaust {d.Body.Exhaust}");
            if (d.Body.Front == "stock" || d.Body.Rear == "stock" || d.Body.Side == "stock" || d.Body.RearAero == "stock")
                failures.Add("body kit: a family did not change");

            yield return Section(1);
            yield return Step("Rim", 3);
            yield return Step("Diameter", 1);
            yield return Step("Offset", 1);
            yield return Step("Rim finish", 4);
            yield return Step("View", 2); // right side
            yield return new WaitForSeconds(0.6f);
            Shot("03-wheels");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            d = Appearance.Editor.Draft;
            Note($"wheels: {d.Wheels.Rim} {d.Wheels.DiameterIn} in, offset {d.Wheels.OffsetStep}, finish {d.Wheels.Finish}");

            yield return Section(2);
            yield return Step("Colour", 4);
            yield return Step("Finish", 1);
            yield return Step("Two-tone", 1);
            yield return Step("Second colour", 1);
            yield return Step("View", 2, true); // front ¾
            yield return new WaitForSeconds(0.6f);
            Shot("04-paint");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            d = Appearance.Editor.Draft;
            Note($"paint: {d.Paint.Swatch} {d.Paint.Primary} {d.Paint.Finish}, two-tone {d.Paint.TwoTone} {d.Paint.Secondary}");

            yield return Section(3);
            yield return Step("Headlamps", 1);
            yield return Step("Glass", 1);
            yield return Step("Plate", 3);
            var plate = GameObject.Find("Appearance-PlateText")?.GetComponent<TMPro.TMP_InputField>();
            if (plate != null) { plate.text = "NS 24"; plate.onEndEdit.Invoke(plate.text); }
            else failures.Add("plate text field missing");
            yield return Step("View", 4); // rear ¾
            yield return new WaitForSeconds(0.6f);
            Shot("05-lights-plate");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            d = Appearance.Editor.Draft;
            Note($"lamps: head {d.Lamps.Head}, tail {d.Lamps.Tail}, glass {d.Glass}, plate '{d.Plate.Text}' ({d.Plate.Style})");
            if (d.Plate.Text != "NS 24") failures.Add($"plate text is '{d.Plate.Text}'");

            // Decals: a mirrored stripe on the flanks, then a race number moved to the hood.
            yield return Section(4);
            CustomizationCatalogue cc = NightSignal.Content.ContentLibrary.Load().Customization;
            List<string> zones = Appearance.Editor.Chassis.Decals.Zones;
            yield return Step("Zone", zones.IndexOf("left"));
            Click("Appearance-AddDecal");
            yield return new WaitForSeconds(0.3f);
            Click("Appearance-Mirror");
            yield return new WaitForSeconds(0.3f);
            for (int i = 0; i < 8; i++)
            {
                Click("Appearance-Larger"); // a 1.2 m flank stripe
                yield return new WaitForSeconds(0.2f);
            }
            yield return Step("Layer colour", 3);
            int digit = cc.DecalShapes.ToList().FindIndex(s => s.Render == "digit" && string.IsNullOrEmpty(s.CosmeticId));
            yield return Step("Shape", Math.Max(0, digit));
            Click("Appearance-AddDecal");
            yield return new WaitForSeconds(0.3f);
            yield return Step("Zone", zones.IndexOf("left") - zones.IndexOf("hood"), true);
            Click("Appearance-Larger");
            yield return new WaitForSeconds(0.3f);
            int undoBefore = Appearance.Editor.UndoCount;
            Click("Appearance-Undo");
            yield return new WaitForSeconds(0.3f);
            Click("Appearance-Redo");
            yield return new WaitForSeconds(0.3f);
            if (Appearance.Editor.UndoCount != undoBefore) failures.Add($"undo/redo: {Appearance.Editor.UndoCount} steps after, {undoBefore} before");
            yield return Step("View", 2); // from above
            yield return new WaitForSeconds(0.6f);
            Shot("06-decals-above");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            yield return Step("View", 1); // front ¾ (wraps from the last view)
            yield return new WaitForSeconds(0.6f);
            Shot("07-decals-front");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            d = Appearance.Editor.Draft;
            Note($"decals: {string.Join("; ", d.Decals.Select(l => $"{l.Shape} {l.Color} {l.Zone} {l.U:0.00},{l.V:0.00} {l.Scale:0.00} m{(l.Mirror ? " mirrored" : "")}"))}; undo steps {Appearance.Editor.UndoCount}");
            if (d.Decals.Count != 2 || d.Decals[1].Zone != "hood") failures.Add("decals: expected a flank stripe and a hood number");

            // A locked colour: tried on, refused on Apply, then undone.
            int locked = cc.PaintSwatches.ToList().FindIndex(s => !string.IsNullOrEmpty(s.CosmeticId));
            if (locked >= 0)
            {
                yield return Section(2);
                int current = cc.PaintSwatches.ToList().FindIndex(s => s.Id == Appearance.Editor.Draft.Paint.Swatch);
                int n = cc.PaintSwatches.Count, presses = ((locked - current) % n + n) % n;
                yield return Step("Colour", presses);
                Click("Appearance-Apply");
                yield return new WaitForSeconds(0.8f);
                Shot("08-locked-refused");
                yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
                Note($"locked {cc.PaintSwatches[locked].Id}: {Appearance.Message}");
                if (!Appearance.Message.StartsWith("Not owned yet") || !string.IsNullOrEmpty(Appearance.Workspace.AppliedLivery))
                    failures.Add("a locked swatch was not refused: " + Appearance.Message);
                for (int i = 0; i < presses; i++)
                {
                    Click("Appearance-Undo"); // each colour press was one undo step
                    yield return new WaitForSeconds(0.25f);
                }
            }
            else Note("no locked swatch in the catalogue");

            // Apply, then two presets (the applied livery, and a variation), then load the first back.
            Click("Appearance-Apply");
            float until = Time.realtimeSinceStartup + 10f;
            while ((Appearance.Busy || string.IsNullOrEmpty(Appearance.Workspace.AppliedLivery)) && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSeconds(0.6f);
            Shot("09-applied");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            string appliedJson = Appearance.Workspace.AppliedLivery, appliedHash = Appearance.Workspace.AppliedLiveryHash;
            Note($"applied: {Appearance.Message} hash {appliedHash} ({appliedJson.Length} chars)");
            if (string.IsNullOrEmpty(appliedJson) || Appearance.Editor.IsDirty) failures.Add("apply: " + Appearance.Message);
            yield return Section(5);
            Click("Appearance-PresetSave0");
            yield return new WaitForSeconds(0.8f);
            yield return Section(2);
            yield return Step("Colour", 1);
            yield return Section(5);
            Click("Appearance-PresetSave1");
            yield return new WaitForSeconds(0.8f);
            Click("Appearance-PresetLoad0");
            yield return new WaitForSeconds(0.8f);
            Shot("10-presets");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            int presets = Appearance.Workspace.VisualPresets.Count(p => p.PayloadSchema == LiveryDocument.SchemaId);
            Note($"presets: {presets} ({string.Join(", ", Appearance.Workspace.VisualPresets.Select(p => p.Name))}); draft dirty after loading preset 1: {Appearance.Editor.IsDirty}");
            if (presets != 2) failures.Add($"presets: {presets} saved, expected 2");
            if (Appearance.Editor.IsDirty) failures.Add("loading the first preset did not give back the applied livery");

            Click("Back");
            yield return new WaitForSeconds(1.2f);
            if (Router.Current != Garage) failures.Add("Back from Appearance did not return to the Garage");
            Shot("11-garage");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            Click("Back");
            yield return new WaitForSeconds(1.2f);

            // Re-read the profile from disk.
            {
                string profileId = LocalSession.Current.Profile.ProfileId;
                string instance = LocalSession.Current.Profile.Cars[0].InstanceId;
                if (!LocalSession.Current.Open(profileId, out string reopen)) failures.Add("re-open profile: " + reopen);
                var reload = Core.Profiles.LocalGarage.LoadWorkspace(LocalSession.Current.Profile, LocalSession.Current.Catalogue,
                    NightSignal.Content.ContentLibrary.Load().Parts, instance, DateTime.UtcNow);
                bool persisted = reload.Ok && reload.Workspace.AppliedLivery == appliedJson && reload.Workspace.AppliedLiveryHash == appliedHash &&
                                 reload.Workspace.VisualPresets.Count(p => p.PayloadSchema == LiveryDocument.SchemaId) == 2;
                Note($"persisted: {persisted}");
                if (!persisted) failures.Add("the applied livery or the presets were not persisted");
            }

            // Race S01: the player's car shows the applied livery.
            Click("Campaign");
            until = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < until && Router.Current != CampaignMap) yield return null;
            yield return new WaitForSeconds(2f);
            Click("Node-S01");
            yield return new WaitForSeconds(0.8f);
            Click("Race");
            until = Time.realtimeSinceStartup + 30f;
            while ((activeRace == null || activeRace.PlayerView == null) && Time.realtimeSinceStartup < until) yield return null;
            if (activeRace?.PlayerView == null) failures.Add("the race did not start");
            else
            {
                yield return new WaitForSeconds(2.5f);
                Shot("12-race-grid");
                yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
                Art.CarAppearance a = activeRace.PlayerView.Appearance;
                LiveryDocument applied = LiveryJson.Parse(appliedJson).Document;
                Note($"race car: front {a.Front}, rear aero {a.RearAero}, rim {a.RimStyle}, plate '{a.PlateText}', {a.Decals.Count} decals, livery {activeRace.PlayerLivery?.Length} bytes");
                if (applied == null || a.Front != applied.Body.Front || a.RearAero != applied.Body.RearAero || a.PlateText != applied.Plate.Text || a.Decals.Count != applied.Decals.Count)
                    failures.Add("the race car does not show the applied livery");
                activeRace.Autopilot = true;
                yield return new WaitForSeconds(8f);
                Shot("13-race-driving");
                yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame
            }

            done:
            string summary = failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures);
            Note(summary);
            yield return new WaitForSeconds(1f);
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }

        static string SectionName(int index) => new[] { "Body kit", "Wheels", "Paint", "Lights & plate", "Decals", "Presets" }[index];
    }
}
