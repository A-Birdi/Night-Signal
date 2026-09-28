using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Net;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Group While We Wait (<c>-nsToyTourGroup &lt;index&gt; &lt;count&gt;</c>, <c>-nsDevAccount N</c>): 3–6 clients join one
        /// convoy (client 0 creates it and shares the code through a file under Builds/), open the convoy's HOSTED tables and
        /// each acts at every table — Greenlight (two clean attempts), Cap Clash (two shots through the serialized queue),
        /// Pit-Crew (one operation on the shared model), Canvas (two marks) — and must see every other client's actions
        /// arrive from the control plane. Screenshots in Builds/Screenshots/toys-group. Automation over real sockets.
        /// </summary>
        IEnumerator ToyTourGroup(int index, int count)
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "toys-group"));
            string share = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "NetRuns", "toys-group"));
            System.IO.Directory.CreateDirectory(dir);
            System.IO.Directory.CreateDirectory(share);
            string codeFile = System.IO.Path.Combine(share, "convoy-code.txt");
            string tag = "c" + index;
            var failures = new List<string>();
            void Note(string n) => Debug.Log($"[NightSignal.ToyGroup:{tag}] {n}");
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            IEnumerator Snap(string name)
            {
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"{tag}-{name}.png"));
                yield return new WaitForEndOfFrame();
                yield return null;
            }
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable || !b.gameObject.activeInHierarchy) { Fail("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }
            IEnumerator Until(Func<bool> condition, float seconds, string what)
            {
                float until = Time.realtimeSinceStartup + seconds;
                while (!condition() && Time.realtimeSinceStartup < until) yield return null;
                if (!condition()) Fail("timed out: " + what + (string.IsNullOrEmpty(OnlineSession.Current?.LastError) ? "" : " (" + OnlineSession.Current.LastError + ")"));
            }
            OnlineSession S() => OnlineSession.Current;
            JObject State() => S()?.Convoy;
            string ReadCode()
            {
                try { return System.IO.File.Exists(codeFile) ? System.IO.File.ReadAllText(codeFile).Trim() : ""; }
                catch (System.IO.IOException) { return ""; }
            }
            NetConfig cfg = NetConfig.FromCommandLine();
            JToken account = JObject.Parse(System.IO.File.ReadAllText(cfg.DevSeedFile))["accounts"][cfg.DevAccount];
            bool host = index == 0;
            int others = count - 1;
            if (host && System.IO.File.Exists(codeFile)) System.IO.File.Delete(codeFile);

            yield return new WaitForSeconds(3f + index * 0.7f);
            Click("OnlineLogin");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("Email").GetComponent<TMP_InputField>().text = (string)account["email"];
            GameObject.Find("Password").GetComponent<TMP_InputField>().text = (string)account["devOnlyPassword"];
            Click("SignIn");
            yield return Until(() => Router.Current == Convoy && S() != null && S().Me != null, 30f, "signed in");
            if (S() == null) { Finish(); yield break; }
            yield return new WaitForSeconds(1.5f);
            if (S().StarterCarId == null) { Click("ChooseStarter"); yield return Until(() => S().StarterCarId != null, 10f, "starter chosen"); }
            if (S().InConvoy) { Click("Leave"); yield return Until(() => !S().InConvoy, 10f, "left an earlier convoy"); }
            yield return new WaitForSeconds(1f);

            if (host)
            {
                Click("CreatePrivate");
                yield return Until(() => S().InConvoy, 10f, "convoy created");
                System.Threading.Tasks.Task<JToken> inv = S().Request("convoy.invite.create");
                while (!inv.IsCompleted) yield return null;
                System.IO.File.WriteAllText(codeFile + ".tmp", (string)inv.Result?["code"] ?? "");
                System.IO.File.Move(codeFile + ".tmp", codeFile);
            }
            else
            {
                yield return Until(() => ReadCode().Length > 0, 90f, "the convoy code");
                TMP_InputField codeField = GameObject.Find("InviteCode")?.GetComponent<TMP_InputField>();
                if (codeField != null) codeField.text = ReadCode();
                Click("JoinCode");
                yield return Until(() => S().InConvoy, 20f, "joined the convoy");
            }
            yield return Until(() => (State()?["members"] as JArray)?.Count >= count, 120f, $"all {count} in the convoy");
            Note($"convoy of {(State()?["members"] as JArray)?.Count}");
            yield return new WaitForSeconds(2f);

            Click("WhileWeWait");
            yield return Until(() => Router.Current == WhileWeWait, 10f, "While We Wait open");
            yield return new WaitForSeconds(1f);

            // Greenlight: two clean Lights Out attempts each; everyone else on the shared board.
            Click("Toy-Greenlight");
            yield return new WaitForSeconds(2.5f);
            Greenlight.AutoPress = (variant, cue, t) => variant == Core.Toys.Greenlight.GreenlightVariant.LightsOut
                ? t >= cue.HiddenDelayMs + 220 + index * 15 : t >= cue.Target * cue.SweepMs;
            float until = Time.realtimeSinceStartup + 150f;
            while ((Greenlight.CleanAttempts < 2 || Greenlight.OthersOnBoard < others) && Time.realtimeSinceStartup < until)
            {
                if (Greenlight.CleanAttempts < 2)
                {
                    Button go = GameObject.Find("GreenlightStart")?.GetComponent<Button>();
                    if (go != null && go.interactable) go.onClick.Invoke();
                }
                yield return new WaitForSeconds(6.5f);
            }
            yield return Snap("01-greenlight");
            Note($"greenlight: my clean attempts {Greenlight.CleanAttempts}, others on the board {Greenlight.OthersOnBoard}/{others}");
            if (Greenlight.CleanAttempts < 2 || Greenlight.OthersOnBoard < others) Fail($"Greenlight: clean {Greenlight.CleanAttempts}, others {Greenlight.OthersOnBoard}/{others}");
            Greenlight.AutoPress = null;
            Click("Back");
            yield return new WaitForSeconds(1.5f);

            // Cap Clash: two shots each through the serialized queue; everyone's shots in the shared history.
            Click("Toy-CapClash");
            yield return new WaitForSeconds(2.5f);
            CapClash.AutoAim = TourCapAim;
            until = Time.realtimeSinceStartup + 240f;
            while ((CapClash.MyShots < 2 || CapClash.OthersShots < 2 * others) && Time.realtimeSinceStartup < until)
            {
                if (CapClash.MyShots < 2)
                {
                    Button shoot = GameObject.Find("CapShoot")?.GetComponent<Button>();
                    if (shoot != null && shoot.interactable) shoot.onClick.Invoke();
                }
                yield return new WaitForSeconds(2f);
            }
            yield return Snap("02-cap-clash");
            Note($"cap clash: mine {CapClash.MyShots}, others {CapClash.OthersShots}/{2 * others}");
            if (CapClash.MyShots < 2 || CapClash.OthersShots < 2 * others) Fail($"Cap Clash: mine {CapClash.MyShots}, others {CapClash.OthersShots}/{2 * others}");
            CapClash.AutoAim = null;
            Click("Back");
            yield return new WaitForSeconds(1.5f);

            // Pit-Crew: one operation each on the shared model (start from a different task so claims spread out).
            Click("Toy-PitCrew");
            yield return new WaitForSeconds(2.5f);
            PitCrew.AutoLock = rel => rel < 0.05;
            until = Time.realtimeSinceStartup + 180f;
            while ((PitCrew.OperationsDoneByMe < 1 || PitCrew.OperationsDoneByOthers < others) && Time.realtimeSinceStartup < until)
            {
                if (PitCrew.OperationsDoneByMe < 1)
                {
                    for (int k = 0; k < 7; k++)
                    {
                        Button task = GameObject.Find("Task" + ((index + k) % 7))?.GetComponent<Button>();
                        if (task != null && task.interactable && task.gameObject.activeInHierarchy) { task.onClick.Invoke(); break; }
                    }
                }
                yield return new WaitForSeconds(3f);
            }
            yield return Snap("03-pit-crew");
            Note($"pit-crew: mine {PitCrew.OperationsDoneByMe}, others {PitCrew.OperationsDoneByOthers}/{others}");
            if (PitCrew.OperationsDoneByMe < 1 || PitCrew.OperationsDoneByOthers < others) Fail($"Pit-Crew: mine {PitCrew.OperationsDoneByMe}, others {PitCrew.OperationsDoneByOthers}/{others}");
            PitCrew.AutoLock = null;
            Click("Back");
            yield return new WaitForSeconds(1.5f);

            // Canvas: two marks each; everyone else's marks on the hosted sheet.
            Click("Toy-Canvas");
            yield return new WaitForSeconds(2.5f);
            ConvoyCanvas.AutoDraw = TourStrokes();
            until = Time.realtimeSinceStartup + 150f;
            while ((ConvoyCanvas.MyObjects < 2 || ConvoyCanvas.OthersObjects < 2 * others) && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSeconds(1.5f);
            yield return Snap("04-canvas");
            Note($"canvas: mine {ConvoyCanvas.MyObjects}, others {ConvoyCanvas.OthersObjects}/{2 * others}");
            if (ConvoyCanvas.MyObjects < 2 || ConvoyCanvas.OthersObjects < 2 * others) Fail($"Canvas: mine {ConvoyCanvas.MyObjects}, others {ConvoyCanvas.OthersObjects}/{2 * others}");
            ConvoyCanvas.AutoDraw = null;
            yield return new WaitForSeconds(6f); // let the others finish seeing these marks
            Click("Back");
            yield return new WaitForSeconds(1.5f);
            Click("Back");
            yield return Until(() => Router.Current == Convoy, 10f, "back at the convoy");
            // The host leaves last, so nobody's view of the tables is cut short.
            yield return new WaitForSeconds(host ? 12f : 2f);
            if (S().InConvoy) { Click("Leave"); yield return Until(() => !S().InConvoy, 10f, "left the convoy"); }
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
