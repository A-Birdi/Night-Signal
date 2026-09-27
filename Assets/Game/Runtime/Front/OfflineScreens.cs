using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>A left/right value selector usable with controller, keyboard and pointer.</summary>
    public sealed class Stepper
    {
        readonly TextMeshProUGUI value;
        readonly Func<int, string> format;
        public int Index { get; private set; }
        public int Count { get; private set; }
        public event Action<int> Changed;
        public Button Left { get; }

        public Stepper(Transform parent, string label, int count, Func<int, string> format, int initial = 0, float width = 700f)
        {
            this.format = format;
            Count = Math.Max(1, count);
            Index = Mathf.Clamp(initial, 0, Count - 1);
            RectTransform row = UIFactory.Rect(label, parent, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            row.sizeDelta = new Vector2(width, 56);
            TextMeshProUGUI l = UIFactory.Label("Label", row, label, SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.MidlineLeft, true);
            l.rectTransform.anchorMin = new Vector2(0, 0);
            l.rectTransform.anchorMax = new Vector2(0.26f, 1);
            l.rectTransform.offsetMin = Vector2.zero; // TMP starts with a 200×50 rect: clear it or the label spills left
            l.rectTransform.offsetMax = new Vector2(-8, 0);
            Left = UIFactory.Button("Prev", row, "<", () => Step(-1), 56, 52);
            SetX(Left, width * 0.26f);
            value = UIFactory.Label("Value", row, "", SignalTheme.Body, SignalTheme.Label, TextAlignmentOptions.Center);
            value.richText = false;
            value.rectTransform.anchorMin = new Vector2(0.26f, 0);
            value.rectTransform.anchorMax = new Vector2(1, 1);
            value.rectTransform.offsetMin = new Vector2(64, 0);
            value.rectTransform.offsetMax = new Vector2(-64, 0);
            Button right = UIFactory.Button("Next", row, ">", () => Step(1), 56, 52);
            SetX(right, width - 56);
            Refresh();
        }

        static void SetX(Button b, float x)
        {
            var rt = (RectTransform)b.transform;
            rt.anchoredPosition = new Vector2(x, -2);
            foreach (Transform child in rt) if (child.name == "Label") ((RectTransform)child).offsetMin = new Vector2(20, 0);
        }

        public void SetCount(int count)
        {
            Count = Math.Max(1, count);
            Index = Mathf.Clamp(Index, 0, Count - 1);
            Refresh();
        }

        public void Set(int index)
        {
            Index = Mathf.Clamp(index, 0, Count - 1);
            Refresh();
        }

        void Step(int d)
        {
            Index = (Index + d + Count) % Count;
            Refresh();
            Changed?.Invoke(Index);
        }

        void Refresh() => value.text = format(Index);
    }

    /// <summary>
    /// Offline Play hub (Addendum 01 §8.2). Everything here is the LOCAL domain: nothing is uploaded or shown as online.
    /// Until the Local progression profile lands, races run as labelled local practice that records nothing.
    /// </summary>
    public sealed class OfflineHubScreen : UIScreen
    {
        public override string ScreenName => "Offline";
        public override string MusicCue => "MUS_MENU_A";
        Stepper course, car, format, ai;
        Button start;
        TextMeshProUGUI note;
        List<CourseDef> playable = new List<CourseDef>();
        List<CarDef> cars = new List<CarDef>();

        protected override void OnBuild(RectTransform root)
        {
            ContentCatalogue cat = ContentLibrary.Load()?.Catalogue;
            if (cat != null)
            {
                playable = cat.Courses.Where(c => Application.CanStreamedLevelBeLoaded(c.Id)).ToList();
                cars = cat.Cars.OrderBy(c => c.BasePI).ToList();
            }
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.44f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.9f));
            RectTransform col = UIFactory.Column("Setup", panel.transform, new Vector2(0, 0.06f), new Vector2(1, 0.9f), new Vector2(64, 0), new Vector2(-32, 0), 12f);
            UIFactory.Row("Heading", col, "OFFLINE PLAY", SignalTheme.Heading, SignalTheme.Label, 640, 0, true);
            UIFactory.Row("Domain", col,
                "Local / Offline. Local progress is kept on this PC only and is separate from any Online profile: it is never uploaded as online currency, unlocks, rank or records.",
                SignalTheme.Small, SignalTheme.Caution, 640, 76);
            course = new Stepper(col, "Course", playable.Count, i => playable.Count == 0 ? "no course scenes built" : $"{playable[i].Id}  {playable[i].Name}");
            car = new Stepper(col, "Car", cars.Count, i => cars.Count == 0 ? "—" : $"{cars[i].Name}  PI {cars[i].BasePI} {cars[i].Drive}",
                Math.Max(0, cars.FindIndex(c => c.Starter)));
            format = new Stepper(col, "Format", 2, i => i == 0 ? "Race — light contact" : "Time Attack — no contact, no AI");
            ai = new Stepper(col, "Opponents", Limits.MaxRaceVehicles, i => i == 0 ? "none" : $"{i} AI", 5);
            format.Changed += i => ai.SetCount(i == 1 ? 1 : Limits.MaxRaceVehicles);
            start = UIFactory.Button("Start", col, "Start Practice Race", StartRace, 620, 60);
            UIFactory.Button("Back", col, "Back to Title", () => App.Router.Show(App.MainMenu, false), 620, 52);
            note = UIFactory.Row("Note", col, "Practice races do not record progression yet: the Local campaign profile is being connected.",
                SignalTheme.Small, SignalTheme.LabelDim, 640, 60);
        }

        public override Selectable DefaultFocus => start;

        public override void OnShow()
        {
            App.Domain = SessionDomain.Local;
            if (string.IsNullOrEmpty(App.DisplayName)) App.DisplayName = "Local driver";
            App.RefreshStrip();
            start.interactable = playable.Count > 0 && cars.Count > 0;
        }

        void StartRace()
        {
            if (playable.Count == 0 || cars.Count == 0) return;
            bool timeAttack = format.Index == 1;
            CarDef chosen = cars[car.Index];
            var rules = new RaceEventRules
            {
                Kind = "freeplay",
                Contact = timeAttack ? ContactPolicy.NonContact : ContactPolicy.LightContact,
                StageNumber = 10,
                CarCapPi = ClassCeiling(chosen.BasePI), // opponents in the player's class, not the fastest cars in the game
            };
            var opponents = new List<string>();
            int count = timeAttack ? 0 : ai.Index;
            for (int i = 1; i <= count; i++) opponents.Add($"ai-{i}");
            App.StartOfflineRace(playable[course.Index].Id, chosen.Id, rules, opponents);
        }

        static int ClassCeiling(int pi)
        {
            switch (PerformanceIndex.ClassOf(pi))
            {
                case PerformanceClass.D: return 299;
                case PerformanceClass.C: return 499;
                case PerformanceClass.B: return 699;
                case PerformanceClass.A: return 849;
                default: return PerformanceIndex.Max;
            }
        }
    }

    /// <summary>
    /// Results (spec §15): outcome, legal placing/time, incidents, separate from any reward itemisation. Offline results
    /// say plainly that they are Local and unverified.
    /// </summary>
    public sealed class ResultsScreen : UIScreen
    {
        public override string ScreenName => "Results";
        TextMeshProUGUI heading, table, summary;
        Button cont;

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0.06f, 0.06f), new Vector2(0.94f, 0.94f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.94f));
            heading = UIFactory.Label("Heading", panel.transform, "", SignalTheme.Heading, SignalTheme.Label, TextAlignmentOptions.TopLeft, true);
            heading.rectTransform.anchorMin = new Vector2(0, 0.86f);
            heading.rectTransform.anchorMax = new Vector2(1, 0.97f);
            heading.rectTransform.offsetMin = new Vector2(48, 0);
            summary = UIFactory.Label("Summary", panel.transform, "", SignalTheme.Body, SignalTheme.Caution, TextAlignmentOptions.TopLeft);
            summary.rectTransform.anchorMin = new Vector2(0, 0.76f);
            summary.rectTransform.anchorMax = new Vector2(1, 0.86f);
            summary.rectTransform.offsetMin = new Vector2(48, 0);
            summary.textWrappingMode = TextWrappingModes.Normal;
            table = UIFactory.Label("Table", panel.transform, "", SignalTheme.Body, SignalTheme.Label, TextAlignmentOptions.TopLeft);
            table.rectTransform.anchorMin = new Vector2(0, 0.14f);
            table.rectTransform.anchorMax = new Vector2(1, 0.76f);
            table.rectTransform.offsetMin = new Vector2(48, 0);
            table.rectTransform.offsetMax = new Vector2(-48, 0);
            table.richText = true;
            RectTransform actions = UIFactory.Column("Actions", panel.transform, new Vector2(0, 0), new Vector2(1, 0.12f), new Vector2(48, 8), new Vector2(-48, -8));
            cont = UIFactory.Button("Continue", actions, "Continue", () => App.Router.Show(App.OfflineHub, false), 360, 60);
        }

        public override Selectable DefaultFocus => cont;

        string pendingCourse;
        List<RaceEntrantResult> pendingResults;

        /// <summary>Stores the classification; the page renders it when shown (it may not be built yet).</summary>
        public void Set(string courseId, RaceEventRules rules, List<RaceEntrantResult> results)
        {
            pendingCourse = courseId;
            pendingResults = results;
            if (heading != null) Render();
        }

        public override void OnShow() => Render();

        void Render()
        {
            string courseId = pendingCourse;
            List<RaceEntrantResult> results = pendingResults;
            heading.text = $"RESULTS  ·  {courseId}";
            if (results == null || results.Count == 0)
            {
                summary.text = "The race ended without results.";
                table.text = "";
                return;
            }
            RaceEntrantResult me = results.FirstOrDefault(r => r.Entrant.Human);
            summary.text = (me == null ? "" : me.Outcome == RunOutcome.Finished ? $"You placed {me.Placement} of {results.Count}. " : "You did not finish. ")
                + "Local / Offline practice — not an online result and not recorded yet.";
            var sb = new System.Text.StringBuilder();
            // Column stops via TMP <pos> so a proportional font still lines up.
            const string cols = "<pos=0%>{0}<pos=7%>{1}<pos=38%>{2}<pos=48%>{3}<pos=66%>{4}<pos=78%>{5}<pos=88%>{6}";
            sb.Append("<color=#9A968D>").Append(string.Format(cols, "POS", "DRIVER", "CAR", "TIME", "CONTACTS", "WALLS", "RESETS")).Append("</color>\n");
            foreach (RaceEntrantResult r in results.OrderBy(x => x.Placement == 0 ? 99 : x.Placement))
            {
                string time = r.Outcome == RunOutcome.Finished ? FormatRaceTime(r.FinishTimeMicros) : r.Outcome == RunOutcome.DidNotFinish ? "DNF" : "DQ";
                string name = Escape(r.Entrant.Roster.DisplayName);
                string line = string.Format(cols, r.Placement == 0 ? "-" : r.Placement.ToString(), name, r.Entrant.Roster.CarId, time,
                    r.Entrant.Progress.VehicleContacts, r.Entrant.Progress.WallIncidents, r.Entrant.Progress.Resets);
                sb.Append(r.Entrant.Human ? $"<color=#D7263D>{line}</color>\n" : line + "\n");
            }
            table.text = sb.ToString();
        }

        /// <summary>Race time as mm:ss.mmm (Addendum 01 §4.3).</summary>
        public static string FormatRaceTime(long micros)
        {
            long ms = micros / 1000;
            return $"{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";
        }

        /// <summary>Names are data, never markup.</summary>
        static string Escape(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");
    }

    /// <summary>Settings (spec §15 accessibility): text size, reduced motion, high contrast, units and audio buses.</summary>
    public sealed class SettingsScreen : UIScreen
    {
        public override string ScreenName => "Settings";
        Stepper textSize;

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.44f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.9f));
            RectTransform col = UIFactory.Column("Options", panel.transform, new Vector2(0, 0.06f), new Vector2(1, 0.9f), new Vector2(64, 0), new Vector2(-32, 0), 10f);
            UIFactory.Row("Heading", col, "SETTINGS", SignalTheme.Heading, SignalTheme.Label, 640, 0, true);
            float[] scales = { 0.9f, 1f, 1.15f, 1.3f, 1.5f };
            textSize = new Stepper(col, "Text size", scales.Length, i => $"{scales[i] * 100:0}%  (applies to newly opened screens)", 1);
            textSize.Changed += i => SignalTheme.TextScale = scales[i];
            var motion = new Stepper(col, "Motion", 2, i => i == 0 ? "Full transitions" : "Reduced motion", SignalTheme.ReducedMotion ? 1 : 0);
            motion.Changed += i => SignalTheme.ReducedMotion = i == 1;
            var contrast = new Stepper(col, "Contrast", 2, i => i == 0 ? "Standard" : "High contrast", SignalTheme.HighContrast ? 1 : 0);
            contrast.Changed += i => SignalTheme.HighContrast = i == 1;
            var units = new Stepper(col, "Speed units", 2, i => i == 0 ? "km/h" : "mph", UI.RaceHud.UseMphGlobal ? 1 : 0);
            units.Changed += i => UI.RaceHud.UseMphGlobal = i == 1;
            AddVolume(col, "Music", () => GameAudio.GameAudioSettings.Music, v => GameAudio.GameAudioSettings.Music = v);
            AddVolume(col, "Engine", () => GameAudio.GameAudioSettings.Engine, v => GameAudio.GameAudioSettings.Engine = v);
            AddVolume(col, "Effects", () => GameAudio.GameAudioSettings.Impacts, v => { GameAudio.GameAudioSettings.Impacts = v; GameAudio.GameAudioSettings.Tyres = v; });
            AddVolume(col, "Interface", () => GameAudio.GameAudioSettings.Ui, v => GameAudio.GameAudioSettings.Ui = v);
            UIFactory.Button("Back", col, "Back", () => App.Router.Back(), 620, 52);
        }

        static void AddVolume(Transform col, string label, Func<float> get, Action<float> set)
        {
            var s = new Stepper(col, label, 11, i => i == 0 ? "off" : $"{i * 10}%", Mathf.RoundToInt(get() * 10f));
            s.Changed += i => set(i / 10f);
        }

        public override Selectable DefaultFocus => textSize.Left;
    }
}
