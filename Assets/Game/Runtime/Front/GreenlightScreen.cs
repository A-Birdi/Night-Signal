using System.Diagnostics;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.Greenlight;
using NightSignal.Toys;
using NightSignal.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Greenlight (Addendum 02 §4): quick reaction micro-attempts — Lights Out, Shift Window, Hold the Mark — at Forgiving
    /// or Narrow settings. The authority issues each attempt with a seed; the hidden cue is derived from it on both ends;
    /// this screen measures the player's input with a local high-resolution clock and reports it; the authority judges
    /// plausibility and the outcome. Personal bests, median of five and the shared clean chain are non-progression.
    /// </summary>
    public sealed class GreenlightScreen : UIScreen
    {
        public override string ScreenName => "Greenlight";
        public override string MusicCue => "MUS_GARAGE";

        ToyConnection toys;
        Stepper variant, setting;
        Button start, back;
        TextMeshProUGUI prompt, outcome, stats, board, status;
        Image[] lights;
        RectTransform lightsRoot, sweepRoot, needle, window;
        InputAction hit;

        enum Phase { Idle, Waiting, Running, Reported }
        Phase phase;
        string attemptId;
        GreenlightCue cue;
        GreenlightVariant runningVariant;
        readonly Stopwatch clock = new Stopwatch();
        float requestedAt;

        /// <summary>UI tours press on schedule (automation, labelled as such): returns true when the tour "presses" now.</summary>
        public System.Func<GreenlightVariant, GreenlightCue, double, bool> AutoPress;
        public int CleanAttempts => Track()?.Clean ?? 0;

        protected override void OnBuild(RectTransform root)
        {
            Image left = UIFactory.Panel("Info", root, new Vector2(0, 0.08f), new Vector2(0.3f, 0.97f), new Vector2(24, 0), Vector2.zero, new Color(0.04f, 0.045f, 0.05f, 0.9f));
            RectTransform col = UIFactory.Column("InfoColumn", left.transform, Vector2.zero, Vector2.one, new Vector2(28, 20), new Vector2(-20, -24), 10f);
            UIFactory.Row("Title", col, "GREENLIGHT", SignalTheme.Heading, SignalTheme.Label, 520, 0, true);
            UIFactory.Row("Domain", col, "While We Wait · reaction practice for fun: it never pays, ranks or unlocks anything.", SignalTheme.Small, SignalTheme.Caution, 520, 48);
            variant = new Stepper(col, "Station", 3, i => i == 0 ? "Lights Out" : i == 1 ? "Shift Window" : "Hold the Mark", 0, 520, 0.28f);
            setting = new Stepper(col, "Setting", 2, i => i == 0 ? "Forgiving" : "Narrow", 0, 520, 0.28f);
            start = UIFactory.Button("GreenlightStart", col, "Start Attempt", StartAttempt, 500, 56);
            stats = UIFactory.Row("Stats", col, "", SignalTheme.Small, SignalTheme.Label, 520, 130);
            stats.richText = true;
            board = UIFactory.Row("Board", col, "", SignalTheme.Small, SignalTheme.Label, 520, 260);
            board.richText = true;
            back = UIFactory.Button("Back", col, "Leave the Station", () => App.Router.Back(), 500, 50);
            status = UIFactory.Row("Status", col, "", SignalTheme.Small, SignalTheme.Caution, 520, 40);

            // Station (centre-right): five start lights, or a sweep with a target window.
            Image stationBg = UIFactory.Panel("Station", root, new Vector2(0.33f, 0.2f), new Vector2(0.97f, 0.85f), Vector2.zero, Vector2.zero, new Color(0.02f, 0.02f, 0.025f, 0.95f));
            prompt = UIFactory.Label("Prompt", stationBg.transform, "", SignalTheme.Heading, SignalTheme.Label, TextAlignmentOptions.Center, true);
            prompt.rectTransform.anchorMin = new Vector2(0, 0.78f);
            prompt.rectTransform.anchorMax = new Vector2(1, 0.95f);
            prompt.rectTransform.offsetMin = prompt.rectTransform.offsetMax = Vector2.zero;
            outcome = UIFactory.Label("Outcome", stationBg.transform, "", SignalTheme.Heading, SignalTheme.Label, TextAlignmentOptions.Center, true);
            outcome.rectTransform.anchorMin = new Vector2(0, 0.05f);
            outcome.rectTransform.anchorMax = new Vector2(1, 0.25f);
            outcome.rectTransform.offsetMin = outcome.rectTransform.offsetMax = Vector2.zero;
            outcome.richText = true;

            lightsRoot = UIFactory.Rect("Lights", stationBg.transform, new Vector2(0.15f, 0.4f), new Vector2(0.85f, 0.7f), Vector2.zero, Vector2.zero);
            lights = new Image[5];
            for (int i = 0; i < 5; i++)
            {
                float x0 = i / 5f + 0.02f, x1 = (i + 1) / 5f - 0.02f;
                Image housing = UIFactory.Panel("Housing" + i, lightsRoot, new Vector2(x0, 0), new Vector2(x1, 1), Vector2.zero, Vector2.zero, new Color(0.08f, 0.08f, 0.09f));
                lights[i] = UIFactory.Panel("Light" + i, housing.transform, new Vector2(0.18f, 0.18f), new Vector2(0.82f, 0.82f), Vector2.zero, Vector2.zero, new Color(0.18f, 0.03f, 0.03f));
            }
            sweepRoot = UIFactory.Rect("Sweep", stationBg.transform, new Vector2(0.08f, 0.45f), new Vector2(0.92f, 0.62f), Vector2.zero, Vector2.zero);
            UIFactory.Panel("Track", sweepRoot, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.12f, 0.12f, 0.14f));
            window = UIFactory.Panel("Window", sweepRoot, new Vector2(0.6f, 0), new Vector2(0.7f, 1), Vector2.zero, Vector2.zero, new Color(0.2f, 0.75f, 0.4f, 0.85f)).rectTransform;
            needle = UIFactory.Panel("Needle", sweepRoot, new Vector2(0, -0.25f), new Vector2(0, 1.25f), new Vector2(-3, 0), new Vector2(3, 0), SignalTheme.Signal).rectTransform;

            hit = new InputAction("Hit", InputActionType.Button);
            hit.AddBinding("<Keyboard>/space");
            hit.AddBinding("<Gamepad>/rightTrigger");
            hit.AddBinding("<Gamepad>/rightShoulder");
            hit.AddBinding("<Mouse>/leftButton");
        }

        public override Selectable DefaultFocus => start;

        public override void OnShow()
        {
            ToyContent content = ContentLibrary.Load()?.Toys;
            toys = content != null ? ToyConnection.Create(content, App) : null;
            if (toys == null) { status.text = "Open a Local profile (or join a convoy) first."; return; }
            toys.Enter(ToyActivityId.Greenlight);
            hit.Enable();
            phase = Phase.Idle;
            prompt.text = "Press START, then react with Space / right trigger / click";
            outcome.text = "";
            ShowStation();
        }

        public override void OnHide()
        {
            hit.Disable();
            if (toys != null)
            {
                if (phase == Phase.Running && attemptId != null)
                    toys.Send(ToyActivityId.Greenlight, "attempt.cancel", new JObject { ["attempt"] = attemptId }); // leaving is never a failure
                toys.Leave(ToyActivityId.Greenlight);
            }
            toys = null;
        }

        GreenlightVariant Variant => (GreenlightVariant)variant.Index;
        GreenlightSetting Setting => (GreenlightSetting)setting.Index;

        void ShowStation()
        {
            bool lightsOut = (phase == Phase.Running || phase == Phase.Reported ? runningVariant : Variant) == GreenlightVariant.LightsOut;
            lightsRoot.gameObject.SetActive(lightsOut);
            sweepRoot.gameObject.SetActive(!lightsOut);
        }

        void StartAttempt()
        {
            if (toys == null || phase == Phase.Waiting) return;
            phase = Phase.Waiting;
            requestedAt = Time.unscaledTime;
            outcome.text = "";
            prompt.text = "…";
            runningVariant = Variant;
            ShowStation();
            toys.Send(ToyActivityId.Greenlight, "attempt.start", new JObject { ["variant"] = Variant.ToString(), ["setting"] = Setting.ToString() }, a =>
            {
                if (!a.Accepted) { phase = Phase.Idle; prompt.text = "Could not start: " + a.Reason; return; }
                attemptId = a.Value;
            });
            EventSystem.current?.SetSelectedGameObject(null); // the reaction input must not also press a menu button
        }

        public override void Tick()
        {
            if (toys == null) return;
            toys.Tick();
            if (phase == Phase.Waiting)
            {
                // The attempt (and its seed) arrive with the authority's state — at once locally, with the next push online.
                GreenlightAttempt open = attemptId != null ? toys.State<GreenlightState>(ToyActivityId.Greenlight)?.Open.FirstOrDefault(x => x.AttemptId == attemptId) : null;
                if (open != null)
                {
                    cue = GreenlightRules.Cue(open.Variant, open.Setting, open.Seed);
                    runningVariant = open.Variant;
                    phase = Phase.Running;
                    clock.Restart();
                    ShowStation();
                }
                else if (Time.unscaledTime - requestedAt > 5f) { phase = Phase.Idle; prompt.text = "The station did not answer. Try again."; }
            }
            if (phase == Phase.Running) RunAttempt();
            RenderStats();
        }

        void RunAttempt()
        {
            double t = clock.Elapsed.TotalMilliseconds;
            bool pressed = AutoPress != null ? AutoPress(runningVariant, cue, t) : hit.WasPressedThisFrame();
            if (runningVariant == GreenlightVariant.LightsOut)
            {
                // Five lights come on at a fixed cadence (not rhythmic with the hidden hold), then all go out at the cue.
                int on = t < cue.HiddenDelayMs ? Mathf.Min(5, 1 + (int)(t / 220.0)) : 0;
                for (int i = 0; i < 5; i++) lights[i].color = i < on ? new Color(0.95f, 0.1f, 0.12f) : new Color(0.18f, 0.03f, 0.03f);
                prompt.text = t < cue.HiddenDelayMs ? "WAIT FOR LIGHTS OUT" : "GO!";
                if (pressed) Report(t, new JObject { ["reactionMs"] = System.Math.Round(t - cue.HiddenDelayMs, 1) });
                else if (t > cue.HiddenDelayMs + cue.MissAfterMs) Report(t, new JObject { ["missed"] = true });
            }
            else
            {
                double pos = t / cue.SweepMs;
                window.anchorMin = new Vector2((float)(cue.Target - cue.Window / 2), 0);
                window.anchorMax = new Vector2((float)(cue.Target + cue.Window / 2), 1);
                float x = Mathf.Clamp01((float)pos);
                needle.anchorMin = new Vector2(x, -0.25f);
                needle.anchorMax = new Vector2(x, 1.25f);
                prompt.text = runningVariant == GreenlightVariant.ShiftWindow ? "SHIFT IN THE GREEN WINDOW" : "STOP ON THE MARK";
                if (pressed) Report(t, new JObject { ["position"] = System.Math.Round(pos, 4) });
                else if (pos > 1.2) Report(t, new JObject { ["missed"] = true });
            }
        }

        void Report(double elapsedMs, JObject measure)
        {
            phase = Phase.Reported;
            measure["attempt"] = attemptId;
            measure["variant"] = runningVariant.ToString();
            measure["elapsedMs"] = System.Math.Round(elapsedMs, 1);
            measure["timing"] = Stopwatch.IsHighResolution ? "HighResolution" : "Coarse";
            toys.Send(ToyActivityId.Greenlight, "attempt.report", measure, a =>
            {
                string result = a.Accepted ? a.Value : "not counted (" + a.Reason + ")";
                string text = result == "Valid" || result == "InWindow" ? $"<color=#3EC6D8>{Label(result)}</color>" : $"<color=#F2A541>{Label(result)}</color>";
                if (a.Accepted && runningVariant == GreenlightVariant.LightsOut && measure["reactionMs"] != null) text += $"  {(double)measure["reactionMs"]:0} ms";
                outcome.text = text;
                prompt.text = "Press START for another";
                EventSystem.current?.SetSelectedGameObject(start.gameObject);
            });
        }

        static string Label(string outcome)
        {
            switch (outcome)
            {
                case "Valid": return "CLEAN REACTION";
                case "InWindow": return "ON TARGET";
                case "Early": return "EARLY";
                case "Late": return "LATE";
                case "Missed": return "MISSED";
                default: return outcome?.ToUpperInvariant();
            }
        }

        GreenlightTrack Track()
        {
            GreenlightState s = toys?.State<GreenlightState>(ToyActivityId.Greenlight);
            return s?.Tracks.FirstOrDefault(t => t.Member == toys.Member && t.Variant == Variant && t.Setting == Setting && !t.PracticeOnly);
        }

        void RenderStats()
        {
            GreenlightState s = toys.State<GreenlightState>(ToyActivityId.Greenlight);
            if (s == null) return;
            GreenlightTrack t = Track();
            GreenlightChain chain = s.Chains.FirstOrDefault(c => c.Variant == Variant);
            string unit = Variant == GreenlightVariant.LightsOut ? "reaction" : "off target";
            stats.text = t == null ? "No attempts yet at this station and setting." :
                $"Best {unit}  <b>{(t.BestMs.HasValue ? t.BestMs.Value.ToString("0") + " ms" : "—")}</b>\n" +
                $"Median of last five  {(t.RecentFiveMedianMs.HasValue ? t.RecentFiveMedianMs.Value.ToString("0") + " ms" : "—")}\n" +
                $"Clean {t.Clean}   ·   early/late/missed {t.VoluntaryFailures}";
            var sb = new System.Text.StringBuilder("<color=#9A968D>SHARED CLEAN CHAIN</color>\n");
            if (chain != null) sb.Append($"{chain.Current} / {chain.Target} in a row   ·   best {chain.Best}   ·   completed {chain.Completed}×\n");
            sb.Append("\n<color=#9A968D>SESSION BESTS (local timing)</color>\n");
            int rank = 0;
            foreach (GreenlightTrack row in s.Tracks.Where(x => x.Variant == Variant && x.Setting == Setting && !x.PracticeOnly && x.BestMs.HasValue).OrderBy(x => x.BestMs).Take(6))
                sb.Append($"{++rank}.  {(row.Member == toys.Member ? "you" : "driver " + row.Member.Substring(System.Math.Max(0, row.Member.Length - 4)))}   {row.BestMs:0} ms\n");
            board.text = sb.ToString();
            status.text = toys.Status;
            start.interactable = phase != Phase.Waiting && phase != Phase.Running;
        }
    }
}
