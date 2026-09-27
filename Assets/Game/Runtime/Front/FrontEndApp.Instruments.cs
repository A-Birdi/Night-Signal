using System;
using System.Collections;
using System.Collections.Generic;
using NightSignal.UI;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Instrument evidence run (<c>-nsInstrumentTour</c>, Addendum 03 G01/G03): with an isolated preferences folder
        /// (<c>-nsPrefsFolder</c>) and profile folder — never the player's own settings — it reports the style/units loaded at
        /// start (so a second launch proves persistence), switches Speedometer and Speed units through the real Settings
        /// controls with their live preview, races S01 on the autopilot with the Instrument Dial in km/h, then changes to
        /// the Digital Strip in mph during the run and checks the race carried on untouched. Leaves Strip + mph saved.
        /// Automation, not a human readability judgement.
        /// </summary>
        IEnumerator InstrumentTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "instruments"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string s) => Debug.Log("[NightSignal.InstrumentTour] " + s);
            void Shot(string name) => ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { failures.Add("button not available: " + name); Note("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }

            DrivingPreferences start = DrivingPreferences.Current;
            Note($"loaded at start: {(start.Dial ? "Instrument Dial" : "Digital Strip")}, {SpeedDisplay.Label(start.Unit)} (file {(System.IO.File.Exists(DrivingPreferences.FilePath) ? "present" : "absent")})");
            yield return new WaitForSeconds(3f);
            Click("Settings");
            yield return new WaitForSeconds(1.5f);
            // Put the documented defaults on screen through the controls themselves.
            if (!DrivingPreferences.Current.Dial) Click("Speedometer/Next");
            if (DrivingPreferences.Current.Unit != SpeedUnit.Kmh) Click("Speed units/Next");
            yield return new WaitForSeconds(2.5f);
            Shot("01-settings-dial-kmh-preview");
            yield return new WaitForSeconds(0.3f);
            Click("Speedometer/Next");
            yield return new WaitForSeconds(2f);
            Shot("02-settings-strip-kmh-preview");
            yield return new WaitForSeconds(0.3f);
            Click("Speed units/Next");
            yield return new WaitForSeconds(2f);
            Shot("03-settings-strip-mph-preview");
            yield return new WaitForSeconds(0.3f);
            if (DrivingPreferences.Current.Dial || DrivingPreferences.Current.Unit != SpeedUnit.Mph) failures.Add("the Settings controls did not change style/units");
            Click("Speedometer/Next");
            Click("Speed units/Next");
            yield return new WaitForSeconds(0.5f);
            Click("Controls");
            yield return new WaitForSeconds(1.5f);
            Shot("07-settings-controls");
            yield return new WaitForSeconds(0.3f);
            Click("Back");
            yield return new WaitForSeconds(1f);
            Click("Back");
            yield return new WaitForSeconds(1.2f);

            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName").GetComponent<TMPro.TMP_InputField>().text = "Gauge Driver";
            Click("Create");
            yield return new WaitForSeconds(1.2f);
            Click("Campaign");
            float until = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < until && Router.Current != CampaignMap) yield return null;
            yield return new WaitForSeconds(2f);
            Click("Node-S01");
            yield return new WaitForSeconds(0.8f);
            Click("Race");
            until = Time.realtimeSinceStartup + 30f;
            while (activeRace == null && Time.realtimeSinceStartup < until) yield return null;
            if (activeRace == null) { failures.Add("the race did not start"); goto done; }
            activeRace.Autopilot = true;
            yield return new WaitForSeconds(11f);
            Shot("04-race-dial-kmh");
            yield return new WaitForSeconds(0.3f);

            // Mid-run change, as the Settings control saves it: presentation only.
            int tickBefore = activeRace.CurrentTick, cpsBefore = activeRace.Player.Progress.CheckpointsPassed;
            string buildBefore = activeRace.PlayerSpec?.BuildHash ?? "stock";
            DrivingPreferences p = DrivingPreferences.Current;
            p.SpeedStyle = "strip";
            p.Units = "mph";
            p.Save();
            yield return new WaitForSeconds(2f);
            Shot("05-race-strip-mph");
            yield return new WaitForSeconds(0.3f);
            bool carriedOn = activeRace.CurrentTick > tickBefore && activeRace.Player.Progress.CheckpointsPassed >= cpsBefore &&
                             (activeRace.PlayerSpec?.BuildHash ?? "stock") == buildBefore && activeRace.Phase == Race.MatchPhase.Racing;
            Note($"mid-run change: tick {tickBefore} -> {activeRace.CurrentTick}, checkpoints {cpsBefore} -> {activeRace.Player.Progress.CheckpointsPassed}, build {buildBefore}, still racing {activeRace.Phase}");
            if (!carriedOn) failures.Add("changing the speedometer disturbed the race");
            yield return new WaitForSeconds(4f);
            Shot("06-race-strip-mph-later");
            yield return new WaitForSeconds(0.3f);

            done:
            Note($"saved for the next launch: {(DrivingPreferences.Current.Dial ? "Instrument Dial" : "Digital Strip")}, {SpeedDisplay.Label(DrivingPreferences.Current.Unit)}");
            string summary = failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures);
            Note(summary);
            yield return new WaitForSeconds(1f);
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}
