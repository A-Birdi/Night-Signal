using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Race;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Short Test Yard evidence run (<c>-nsYardTour</c>): a fresh Local profile in an isolated folder → Garage → a preview
        /// tyre in the draft → Test Yard B then A with the same scripted launch-and-stop on the straight, one lap of the skid
        /// pad each, a wet run → back to the Garage. Logs cameras and positions so the view can be checked. Automation.
        /// </summary>
        IEnumerator YardTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "yard"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Shot(string name) => ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
            void Note(string s) => Debug.Log("[NightSignal.YardTour] " + s);
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { failures.Add("button not available: " + name); Note("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }

            yield return new WaitForSeconds(3f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName").GetComponent<TMPro.TMP_InputField>().text = "Yard Driver";
            Click("Create");
            yield return new WaitForSeconds(1.2f);
            Click("Garage");
            yield return new WaitForSeconds(1.5f);
            Click("Slot-tyres");
            yield return new WaitForSeconds(0.5f);
            Click("Part2"); // a tyre this new profile does not own: B is a preview build
            yield return new WaitForSeconds(0.8f);
            Shot("01-garage-draft");
            Click("TestYardB");
            float until = Time.realtimeSinceStartup + 40f;
            while ((ActiveYard == null || !ActiveYard.Ready) && Time.realtimeSinceStartup < until) yield return null;
            if (ActiveYard == null) { failures.Add("the yard did not open"); yield break; }

            bool stopPhase = false;
            ActiveYard.Script = (st, t) =>
            {
                if (t < 0.05f) stopPhase = false;
                if (st.SpeedKmh >= 100f) stopPhase = true;
                // Release at rest: holding the brake at a standstill selects reverse.
                if (stopPhase && st.SpeedKmh < 0.3f) return Vehicle.DriverInput.Neutral;
                return stopPhase ? Vehicle.DriverInput.Quantize(0f, 0f, 1f, Vehicle.InputButtons.None) : Vehicle.DriverInput.Quantize(0f, 1f, 0f, Vehicle.InputButtons.None);
            };
            for (int i = 0; i < 12; i++)
            {
                LogView(i);
                if (i == 3) Shot("02-yard-b-launch");
                yield return new WaitForSeconds(1f);
            }
            Shot("03-yard-b-stopped");
            yield return new WaitForSeconds(0.3f); // the capture lands at the end of the frame: let it before resetting
            ActiveYard.ResetAndDrive(false, 0);
            until = Time.realtimeSinceStartup + 30f;
            while (ActiveYard.CurrentRun != null && ActiveYard.CurrentRun.StopMetres < 0f && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSeconds(1f);
            Shot("04-yard-a-stopped");
            yield return new WaitForSeconds(0.3f);

            // Skid pad: steady right-hand circle for 12 s on each side.
            ActiveYard.Script = (st, t) => Vehicle.DriverInput.Quantize(0.55f, st.SpeedKmh < 45f ? 0.7f : 0.25f, 0f, Vehicle.InputButtons.None);
            ActiveYard.ResetAndDrive(false, 1);
            yield return new WaitForSeconds(12f);
            Note("A pad: " + ActiveYard.CameraDebug);
            Shot("05-yard-skid-pad-a");
            yield return new WaitForSeconds(0.3f);
            ActiveYard.ResetAndDrive(true, 1);
            yield return new WaitForSeconds(12f);
            Shot("06-yard-skid-pad-b");
            yield return new WaitForSeconds(0.3f);
            // Wet preset: explicit reset, same station.
            ActiveYard.SetSurface("wet");
            yield return new WaitForSeconds(12f);
            Shot("07-yard-skid-pad-b-wet");
            yield return new WaitForSeconds(0.3f);
            ActiveYard.ResetAndDrive(true, 1);
            yield return new WaitForSeconds(0.5f);
            foreach (TestYardRun r in ActiveYard.RunsA.Concat(ActiveYard.RunsB)) Note($"{(r.B ? "B" : "A")} {r.Station} {r.Surface}: {r.Summary()}");
            ActiveYard.RequestExit();
            until = Time.realtimeSinceStartup + 40f;
            while ((ActiveYard != null || Router.Current != Garage) && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSeconds(1.5f);
            Shot("08-garage-after");
            Click("Back");
            yield return new WaitForSeconds(1.2f);
            if (Router.Current != OfflineHub) failures.Add("Back from the Garage after the yard did not reach the Offline hub");
            TestYardRun a = LastYardRuns.FirstOrDefault(r => !r.B && r.Station == "straight"), b = LastYardRuns.FirstOrDefault(r => r.B && r.Station == "straight");
            if (a == null || b == null || a.StopMetres <= 0 || b.StopMetres <= 0) failures.Add("straight runs not measured for both A and B");
            if (!LastYardRuns.Any(r => r.Station == "skid-pad" && r.PeakLateralG > 0.4f)) failures.Add("skid pad lateral g not measured");
            if (!LastYardRuns.Any(r => r.Surface == "wet")) failures.Add("no wet run kept");
            if (LocalSession.Current.Profile.WalletBalance != 12000) failures.Add("the yard changed the wallet");
            string summary = failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures);
            Note(summary);
            yield return new WaitForSeconds(1f);
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }

        void LogView(int second)
        {
            Camera main = Camera.main;
            string cams = string.Join(", ", Camera.allCameras.Select(c => $"{c.name}(depth {c.depth}, {c.transform.position})"));
            Debug.Log($"[NightSignal.YardTour] t={second}s car {ActiveYard?.State.Position} {ActiveYard?.State.SpeedKmh:0} km/h · main {main?.name} · cameras: {cams}");
        }
    }
}
