using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
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
        /// <summary>The value text (e.g. a colour swatch tints it).</summary>
        public TextMeshProUGUI Value => value;
        /// <summary>The whole row (label, arrows, value): hide it with SetActive to take it out of a column's layout.</summary>
        public GameObject Root => Left.transform.parent.gameObject;

        public Stepper(Transform parent, string label, int count, Func<int, string> format, int initial = 0, float width = 700f, float labelFraction = 0.26f)
        {
            this.format = format;
            Count = Math.Max(1, count);
            Index = Mathf.Clamp(initial, 0, Count - 1);
            RectTransform row = UIFactory.Rect(label, parent, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            row.sizeDelta = new Vector2(width, 56);
            TextMeshProUGUI l = UIFactory.Label("Label", row, label, SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.MidlineLeft, true);
            l.rectTransform.anchorMin = new Vector2(0, 0);
            l.rectTransform.anchorMax = new Vector2(labelFraction, 1);
            l.rectTransform.offsetMin = Vector2.zero; // TMP starts with a 200×50 rect: clear it or the label spills left
            l.rectTransform.offsetMax = new Vector2(-8, 0);
            Left = UIFactory.Button("Prev", row, "<", () => Step(-1), 56, 52);
            SetX(Left, width * labelFraction);
            value = UIFactory.Label("Value", row, "", SignalTheme.Body, SignalTheme.Label, TextAlignmentOptions.Center);
            value.richText = false;
            value.rectTransform.anchorMin = new Vector2(labelFraction, 0);
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
            rt.pivot = new Vector2(0, 1); // top-left: the button spans [x, x + width] and sits inside the row
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
    /// Offline Play hub (Addendum 01 §8.2) for the open Local profile: the campaign map, and Freeplay on owned courses
    /// with owned cars (Race with light contact and 0..11 AI, or non-contact Time Attack). Everything here is the LOCAL
    /// domain: nothing is uploaded or shown as online.
    /// </summary>
    public sealed class OfflineHubScreen : UIScreen
    {
        public override string ScreenName => "Offline";
        public override string MusicCue => "MUS_MENU_A";
        Stepper course, car, format, ai, rival;
        Button start, campaign;
        TextMeshProUGUI profileLine, note, archetypeLine;
        // The named rival (the lead, spec §13 / CH38, CH73): 0 = a random authored field.
        readonly List<RivalDef> rivals = new List<RivalDef>();
        List<CourseDef> playable = new List<CourseDef>();
        readonly List<LocalCarChoice> cars = new List<LocalCarChoice>();

        protected override void OnBuild(RectTransform root)
        {
            ContentCatalogue cat = ContentLibrary.Load()?.Catalogue;
            if (cat != null) playable = cat.Courses.Where(c => Application.CanStreamedLevelBeLoaded(c.Id)).ToList();
            if (cat != null) rivals.AddRange(cat.Rivals.Where(r => FinalRivals.Allowed(r.Id, AiPlacementContext.FreeplayOpponent)).OrderBy(r => r.Id, StringComparer.Ordinal));
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.44f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.9f));
            RectTransform col = UIFactory.Column("Setup", panel.transform, new Vector2(0, 0.04f), new Vector2(1, 0.92f), new Vector2(64, 0), new Vector2(-32, 0), 12f);
            UIFactory.Row("Heading", col, "OFFLINE PLAY", SignalTheme.Heading, SignalTheme.Label, 640, 0, true);
            profileLine = UIFactory.Row("Profile", col, "", SignalTheme.Body, SignalTheme.Label, 640, 34);
            profileLine.richText = false;
            UIFactory.Row("Domain", col,
                "Local / Offline. Kept on this PC only and separate from any Online profile: never uploaded as online currency, unlocks, rank or records.",
                SignalTheme.Small, SignalTheme.Caution, 640, 52);
            campaign = UIFactory.Button("Campaign", col, "Campaign Map", () => App.Router.Show(App.CampaignMap), 620, 60);
            UIFactory.Button("Garage", col, "Garage", () => App.Router.Show(App.Garage), 620, 52);
            UIFactory.Button("WhileWeWait", col, "While We Wait", () => App.Router.Show(App.WhileWeWait), 620, 52);
            UIFactory.Button("DriverCard", col, "Driver Card", () => App.Router.Show(App.PlayerCard), 620, 52);
            UIFactory.Button("RaceDiary", col, "Race Diary", () => App.Router.Show(App.Diary), 620, 52);
            UIFactory.Button("Meet", col, "Car Meet: Cedar Lantern Terrace", () =>
            {
                if (cars.Count > 0) App.StartOfflineMeet(cars[car.Index], this);
            }, 620, 52);
            UIFactory.Button("Switch", col, "Switch Profile", () => { LocalSession.Current?.Close(); App.Router.Show(App.ProfileSelect, false); }, 620, 52);
            UIFactory.Button("Back", col, "Back to Title", () => App.Router.Show(App.MainMenu, false), 620, 52);

            // Freeplay in its own panel on the right (every row fits at 1080p with the lead rival and its progress line).
            Image fpPanel = UIFactory.Panel("FreeplayPanel", root, new Vector2(0.46f, 0.05f), new Vector2(0.98f, 0.68f), Vector2.zero, Vector2.zero,
                new Color(0.055f, 0.06f, 0.07f, 0.9f));
            RectTransform fcol = UIFactory.Column("Freeplay", fpPanel.transform, new Vector2(0, 0.04f), new Vector2(1, 0.96f), new Vector2(48, 0), new Vector2(-32, 0), 10f);
            UIFactory.Row("FreeplayHeading", fcol, "FREEPLAY", SignalTheme.Small, SignalTheme.LabelDim, 820, 28, true);
            course = new Stepper(fcol, "Course", playable.Count, CourseLabel, 0, 820);
            car = new Stepper(fcol, "Car", 1, i => cars.Count == 0 ? "—" : CarLabel(cars[i]), 0, 820);
            format = new Stepper(fcol, "Format", 2, i => i == 0 ? "Race — light contact" : "Time Attack — no contact, no AI", 0, 820);
            ai = new Stepper(fcol, "Opponents", Limits.MaxRaceVehicles, i => i == 0 ? "none" : $"{i} AI", 5, 820);
            rival = new Stepper(fcol, "Lead rival", rivals.Count + 1, i => i == 0 || i > rivals.Count ? "random authored rivals" : RivalLabel(rivals[i - 1]), 0, 820);
            archetypeLine = UIFactory.Row("Archetypes", fcol, "", SignalTheme.Small, SignalTheme.LabelDim, 820, 28);
            format.Changed += i => { ai.SetCount(i == 1 ? 1 : Limits.MaxRaceVehicles); RefreshRival(); };
            ai.Changed += _ => RefreshRival();
            course.Changed += _ => RefreshStart();
            start = UIFactory.Button("Start", fcol, "Start Freeplay Race", StartRace, 620, 60);
            note = UIFactory.Row("Note", fcol, "", SignalTheme.Small, SignalTheme.LabelDim, 820, 60);
        }

        public override Selectable DefaultFocus => campaign;

        static string RivalLabel(RivalDef r) => $"{r.Name} · {r.Tendency.Replace('-', ' ')}";

        void RefreshRival()
        {
            bool race = format.Index == 0 && ai.Index > 0;
            rival.Root.SetActive(race);
            LocalProfile p = LocalSession.Current?.Profile;
            var s = new ArchetypeState();
            if (p != null)
            {
                s.Raced.UnionWith(p.ArchetypesRaced ?? new List<string>());
                s.WonStreak.UnionWith(p.ArchetypeWinStreak ?? new List<string>());
            }
            archetypeLine.gameObject.SetActive(race);
            archetypeLine.text = ArchetypeChallenges.ProgressLine(s);
        }

        /// <summary>Automation hook (tours): pick the lead rival as the stepper would.</summary>
        public bool SelectRival(string rivalId)
        {
            int i = rivals.FindIndex(r => r.Id == rivalId);
            if (i < 0) return false;
            rival.Set(i + 1);
            return true;
        }

        string CourseLabel(int i)
        {
            if (playable.Count == 0) return "no course scenes built";
            CourseDef c = playable[i];
            LocalProfile p = LocalSession.Current?.Profile;
            bool owned = p != null && p.OwnsCourse(LocalSession.Current.Catalogue, c.Id);
            return $"{c.Id}  {c.Name}" + (owned ? "" : "  (locked)");
        }

        static string CarLabel(LocalCarChoice c)
        {
            LocalSession s = LocalSession.Current;
            CarDef def = s.Catalogue.Car(c.ModelId);
            OwnedCar owned = c.Loaner ? null : s.Profile.FindCar(c.InstanceId);
            return $"{def.Name}  PI {(owned != null ? s.AppliedPi(owned) : def.BasePI)} {def.Drive}";
        }

        public override void OnShow()
        {
            LocalSession s = LocalSession.Current;
            if (s?.Profile == null)
            {
                App.Router.Show(App.ProfileSelect, false);
                return;
            }
            App.Domain = SessionDomain.Local;
            App.DisplayName = s.Profile.DisplayName;
            App.RefreshStrip();
            LocalProfile p = s.Profile;
            profileLine.text = $"{p.DisplayName}   ·   {p.ComputeRank().Name}   ·   {p.WalletBalance:N0} cr   ·   {p.Cars.Count} car(s)";
            cars.Clear();
            foreach (OwnedCar c in p.Cars.OrderByDescending(c => s.AppliedPi(c)))
                cars.Add(new LocalCarChoice { ModelId = c.ModelId, InstanceId = c.InstanceId });
            car.SetCount(Math.Max(1, cars.Count));
            course.Set(course.Index); // re-label locks for this profile
            RefreshRival();
            RefreshStart();
        }

        void RefreshStart()
        {
            LocalSession s = LocalSession.Current;
            if (s?.Profile == null || playable.Count == 0 || cars.Count == 0)
            {
                start.interactable = false;
                return;
            }
            bool ok = LocalProgression.CanStartFreeplay(s.Profile, s.Catalogue, playable[course.Index].Id, out string reason);
            start.interactable = ok;
            note.text = ok ? "Freeplay pays race money and keeps Local personal records." : reason + " " + LocalProgression.AccessHint(s.Catalogue, playable[course.Index].Id);
        }

        void StartRace()
        {
            if (!start.interactable) return;
            bool timeAttack = format.Index == 1;
            LocalCarChoice chosen = cars[car.Index];
            LocalSession s = LocalSession.Current;
            OwnedCar owned = chosen.Loaner ? null : s.Profile.FindCar(chosen.InstanceId);
            int pi = owned != null ? s.AppliedPi(owned) : s.Catalogue.Car(chosen.ModelId).BasePI;
            // Opponents in the class of the player's APPLIED build, not the fastest cars in the game.
            string named = rival.Index > 0 && rival.Index <= rivals.Count ? rivals[rival.Index - 1].Id : null;
            LocalEventPlan plan = LocalEvents.Freeplay(s.Catalogue, playable[course.Index], timeAttack, ai.Index, ClassCeiling(pi), chosen, named);
            App.StartLocalEvent(plan, this);
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
        TextMeshProUGUI heading, table, summary, progress, reaction;
        Button cont;
        List<Core.Story.StoryLine> story = new List<Core.Story.StoryLine>();
        string storyPlayer = "";

        /// <summary>The post-race reaction shown (tours read it): speaker and line, one per row.</summary>
        public string Reaction => reaction != null ? reaction.text : "";
        UIScreen returnTo;
        Core.Profiles.LocalProgressionResult applied;
        string saveNote = "";

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
            table.rectTransform.anchorMin = new Vector2(0, 0.27f);
            table.rectTransform.anchorMax = new Vector2(0.6f, 0.76f);
            table.rectTransform.offsetMin = new Vector2(48, 0);
            table.rectTransform.offsetMax = new Vector2(-24, 0);
            table.richText = true;
            // The featured rival's (or the radio's) reaction to how the stage went (spec §5.3: a 3–6 s post-race quip).
            reaction = UIFactory.Label("Reaction", panel.transform, "", SignalTheme.Body, SignalTheme.Label, TextAlignmentOptions.TopLeft);
            reaction.rectTransform.anchorMin = new Vector2(0, 0.13f);
            reaction.rectTransform.anchorMax = new Vector2(0.6f, 0.26f);
            reaction.rectTransform.offsetMin = new Vector2(48, 0);
            reaction.rectTransform.offsetMax = new Vector2(-24, 0);
            reaction.textWrappingMode = TextWrappingModes.Normal;
            reaction.richText = true;
            // Reward itemisation stays separate from the classification (spec §15).
            progress = UIFactory.Label("Progression", panel.transform, "", SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.TopLeft);
            progress.rectTransform.anchorMin = new Vector2(0.6f, 0.14f);
            progress.rectTransform.anchorMax = new Vector2(1, 0.76f);
            progress.rectTransform.offsetMin = new Vector2(24, 0);
            progress.rectTransform.offsetMax = new Vector2(-48, 0);
            progress.textWrappingMode = TextWrappingModes.Normal;
            progress.overflowMode = TextOverflowModes.Overflow;
            progress.richText = true;
            RectTransform actions = UIFactory.Column("Actions", panel.transform, new Vector2(0, 0), new Vector2(1, 0.12f), new Vector2(48, 8), new Vector2(-48, -8));
            cont = UIFactory.Button("Continue", actions, "Continue", () =>
            {
                Action ending = pendingEnding;
                pendingEnding = null;
                if (ending != null) ending();
                else App.Router.Show(returnTo ?? App.OfflineHub, false);
            }, 360, 60);
        }

        public override Selectable DefaultFocus => cont;

        string pendingCourse;
        Action pendingEnding;

        /// <summary>Continue plays this first (the campaign's ending after its first finale clear), which then returns as usual.</summary>
        public void SetEnding(Action play) => pendingEnding = play;
        List<RaceEntrantResult> pendingResults;

        /// <summary>Stores the classification; the page renders it when shown (it may not be built yet).</summary>
        public void Set(string courseId, RaceEventRules rules, List<RaceEntrantResult> results,
            Core.Profiles.LocalProgressionResult progression, string note, UIScreen back)
        {
            pendingCourse = courseId;
            pendingResults = results;
            applied = progression;
            saveNote = note ?? "";
            returnTo = back;
            story = new List<Core.Story.StoryLine>();
            pendingEnding = null;
            if (heading != null) Render();
        }

        /// <summary>The stage's reaction lines for this result (after <see cref="Set"/>; none outside the campaign).</summary>
        public void SetStory(List<Core.Story.StoryLine> lines, string player)
        {
            story = lines ?? new List<Core.Story.StoryLine>();
            storyPlayer = player ?? "";
            if (heading != null) Render();
        }

        public override void OnShow() => Render();

        void Render()
        {
            string courseId = pendingCourse;
            List<RaceEntrantResult> results = pendingResults;
            heading.text = $"RESULTS  ·  {courseId}";
            ContentCatalogue storyCat = ContentLibrary.Load()?.Catalogue;
            reaction.text = string.Join("\n", story.Select(l => l.Speaker == "narration"
                ? $"<i>{Escape(Core.Story.StoryText.Fill(l.Line, storyPlayer, ""))}</i>"
                : $"<color=#D7263D>{Escape(StoryScreen.SpeakerName(l.Speaker, storyCat))}</color>  {Escape(Core.Story.StoryText.Fill(l.Line, storyPlayer, ""))}"));
            if (results == null || results.Count == 0)
            {
                summary.text = "The race ended without results.";
                table.text = "";
                return;
            }
            RaceEntrantResult me = results.FirstOrDefault(r => r.Entrant.Human);
            summary.text = (me == null ? "" : me.Outcome == RunOutcome.Finished ? $"You placed {me.Placement} of {results.Count}. " : "You did not finish. ")
                + (applied != null ? "Local / Offline result: kept on this PC only, never an online result." : "Local / Offline practice — not recorded.");
            progress.text = ProgressionText(applied, saveNote);
            var sb = new System.Text.StringBuilder();
            // Column stops via TMP <pos> so a proportional font still lines up.
            const string cols = "<pos=0%>{0}<pos=6%>{1}<pos=38%>{2}<pos=47%>{3}<pos=64%>{4}<pos=79%>{5}<pos=90%>{6}";
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

        /// <summary>What the Local profile gained, line by line, from the Core progression result (nothing invented here).</summary>
        static string ProgressionText(Core.Profiles.LocalProgressionResult r, string note)
        {
            if (r == null) return string.IsNullOrEmpty(note) ? "" : note;
            var sb = new System.Text.StringBuilder();
            sb.Append("<color=#9A968D>LOCAL PROFILE</color>\n");
            if (r.Stage != null)
            {
                string verdict = r.Stage.EarnedClear ? (r.Stage.FirstClear ? "<color=#3EC6D8>Stage cleared — first clear</color>" : "<color=#3EC6D8>Stage cleared</color>")
                    : "<color=#F2A541>Stage not cleared</color>";
                sb.Append(verdict).Append("\n<size=85%>").Append(Escape(r.Stage.Reason)).Append("</size>\n\n");
            }
            if (r.Status == Core.Profiles.LocalOperationStatus.Aborted)
                sb.Append(Escape(r.Reason)).Append("\n");
            if (r.Payout != null && r.Changed)
            {
                sb.Append($"Race money  {r.Payout.EventCredits:N0} cr\n");
                if (r.Payout.FirstClearBonus > 0) sb.Append($"First-clear bonus  {r.Payout.FirstClearBonus:N0} cr\n");
                if (r.Payout.ChallengeCash > 0) sb.Append($"Challenges  {r.Payout.ChallengeCash:N0} cr\n");
                if (!string.IsNullOrEmpty(r.Payout.Note)) sb.Append("<size=85%>").Append(Escape(r.Payout.Note)).Append("</size>\n");
                sb.Append($"<b>Wallet  {r.BalanceBefore:N0} → {r.BalanceAfter:N0} cr</b>\n");
                if (r.RankPointsAfter != r.RankPointsBefore) sb.Append($"Rank points  {r.RankPointsBefore} → {r.RankPointsAfter}  ({Escape(r.RankAfter)})\n");
            }
            foreach (Core.Profiles.ProgressionChange c in r.Changes)
                switch (c.Kind)
                {
                    case Core.Profiles.ProgressionChangeKind.MusicUnlocked:
                    case Core.Profiles.ProgressionChangeKind.CourseUnlocked:
                    case Core.Profiles.ProgressionChangeKind.ChallengeCompleted:
                    case Core.Profiles.ProgressionChangeKind.CosmeticGranted:
                    case Core.Profiles.ProgressionChangeKind.TutorialCompleted:
                        sb.Append("<color=#3EC6D8>+</color> ").Append(Escape(c.Detail)).Append("\n");
                        break;
                }
            foreach (Core.Profiles.RecordUpdateResult rec in r.Records)
                if (rec.IsNewPersonalBest)
                    sb.Append($"<color=#D7263D>New personal best</color>  {FormatRaceTime(rec.Value * 1000)}  <size=80%>(Local, unverified)</size>\n");
            foreach (string n in r.Notes) sb.Append("<size=85%><color=#9A968D>").Append(Escape(n)).Append("</color></size>\n");
            if (!string.IsNullOrEmpty(note)) sb.Append("<size=85%><color=#F2A541>").Append(Escape(note)).Append("</color></size>\n");
            return sb.ToString();
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
        UI.SpeedCluster preview;
        float previewTime;
        readonly List<(Stepper Step, Func<float> Get)> strengthRows = new List<(Stepper, Func<float>)>();
        Stepper preset;

        static UI.DrivingPreferences Prefs => UI.DrivingPreferences.Current;

        /// <summary>Applies the stored accessibility choices to the running UI (at start-up and after a change).</summary>
        public static void ApplyAccessibility(UI.DrivingPreferences p)
        {
            SignalTheme.TextScale = p.TextScale;
            SignalTheme.ReducedMotion = p.ReducedMotion;
            SignalTheme.HighContrast = p.HighContrast;
        }

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.44f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.9f));
            RectTransform col = UIFactory.Column("Options", panel.transform, new Vector2(0, 0.04f), new Vector2(1, 0.92f), new Vector2(64, 0), new Vector2(-32, 0), 8f);
            UIFactory.Row("Heading", col, "SETTINGS", SignalTheme.Heading, SignalTheme.Label, 640, 0, true);
            float[] scales = { 0.9f, 1f, 1.15f, 1.3f, 1.5f };
            int textIndex = Mathf.Max(0, Array.FindIndex(scales, v => Mathf.Abs(v - Prefs.TextScale) < 0.01f));
            textSize = new Stepper(col, "Text size", scales.Length, i => $"{scales[i] * 100:0}%  (new screens)", textIndex);
            textSize.Changed += i => Save(p => p.TextScale = scales[i]);
            var motion = new Stepper(col, "Motion", 2, i => i == 0 ? "Full transitions" : "Reduced motion", Prefs.ReducedMotion ? 1 : 0);
            motion.Changed += i => Save(p => p.ReducedMotion = i == 1);
            var contrast = new Stepper(col, "Contrast", 2, i => i == 0 ? "Standard" : "High contrast", Prefs.HighContrast ? 1 : 0);
            contrast.Changed += i => Save(p => p.HighContrast = i == 1);
            float[] hud = { 0.8f, 0.9f, 1f, 1.15f, 1.3f };
            var hudSize = new Stepper(col, "HUD size", hud.Length, i => $"{hud[i] * 100:0}%", Mathf.Max(0, Array.FindIndex(hud, v => Mathf.Abs(v - Prefs.HudScale) < 0.01f)));
            hudSize.Changed += i => Save(p => p.HudScale = hud[i]);
            var style = new Stepper(col, "Speedometer", 2, i => i == 0 ? "Instrument Dial" : "Digital Strip", Prefs.Dial ? 0 : 1);
            style.Changed += i => Save(p => p.SpeedStyle = i == 0 ? "dial" : "strip");
            var units = new Stepper(col, "Speed units", 2, i => i == 0 ? "km/h" : "mph", Prefs.Unit == UI.SpeedUnit.Mph ? 1 : 0);
            units.Changed += i => Save(p => p.Units = i == 0 ? "kmh" : "mph");
            AddVolume(col, "Music", () => GameAudio.GameAudioSettings.Music, v => GameAudio.GameAudioSettings.Music = v);
            AddVolume(col, "Engine", () => GameAudio.GameAudioSettings.Engine, v => GameAudio.GameAudioSettings.Engine = v);
            AddVolume(col, "Effects", () => GameAudio.GameAudioSettings.Impacts, v => { GameAudio.GameAudioSettings.Impacts = v; GameAudio.GameAudioSettings.Tyres = v; });
            AddVolume(col, "Interface", () => GameAudio.GameAudioSettings.Ui, v => GameAudio.GameAudioSettings.Ui = v);
            UIFactory.Button("Controls", col, "Controls…", () => App.Router.Show(App.Controls, true), 620, 52);
            UIFactory.Button("Back", col, "Back", () => App.Router.Back(), 620, 52);

            // Right: driving camera and motion (presentation only — never an assist, a reward or a readiness change).
            Image right = UIFactory.Panel("CameraPanel", root, new Vector2(0.46f, 0.34f), new Vector2(0.99f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.9f));
            RectTransform rc = UIFactory.Column("CameraOptions", right.transform, new Vector2(0, 0.01f), new Vector2(1, 0.97f), new Vector2(40, 0), new Vector2(-24, 0), 2f);
            UIFactory.Row("CameraHeading", rc, "DRIVING VIEW AND MOTION", SignalTheme.Subheading, SignalTheme.Label, 820, 0, true);
            string[] viewNames = { "Chase Close", "Chase Far", "Hood", "Bumper / Road", "Cockpit" };
            var view = new Stepper(rc, "Driving view", viewNames.Length, i => viewNames[i], Math.Max(0, Array.IndexOf(UI.DrivingPreferences.Views, Prefs.View)), 820, 0.3f);
            view.Changed += i => Save(p => p.View = UI.DrivingPreferences.Views[i]);
            int fovSteps = Mathf.RoundToInt((UI.DrivingPreferences.MaxFov - UI.DrivingPreferences.MinFov) / 2f) + 1;
            var fov = new Stepper(rc, "Field of view", fovSteps, i => $"{UI.DrivingPreferences.MinFov + i * 2f:0}° vertical",
                Mathf.RoundToInt((Prefs.VerticalFov - UI.DrivingPreferences.MinFov) / 2f), 820, 0.3f);
            fov.Changed += i => Save(p => p.VerticalFov = UI.DrivingPreferences.MinFov + i * 2f);
            string[] presetNames = { "Arcade", "Comfort", "Custom" };
            preset = new Stepper(rc, "Motion preset", 3, i => presetNames[i], Math.Max(0, Array.IndexOf(UI.DrivingPreferences.Presets, Prefs.MotionPreset)), 820, 0.3f);
            preset.Changed += i => { Save(p => p.MotionPreset = UI.DrivingPreferences.Presets[i]); RefreshStrengths(); };
            AddStrength(rc, "Drift framing", () => Prefs.Effective.DriftFraming, (p, v) => p.Custom.DriftFraming = v);
            AddStrength(rc, "Road / body motion", () => Prefs.Effective.BodyMotion, (p, v) => p.Custom.BodyMotion = v);
            AddStrength(rc, "Impact shake", () => Prefs.Effective.ImpactShake, (p, v) => p.Custom.ImpactShake = v);
            AddStrength(rc, "Camera roll", () => Prefs.Effective.Roll, (p, v) => p.Custom.Roll = v);
            AddStrength(rc, "Speed field of view", () => Prefs.Effective.SpeedFov, (p, v) => p.Custom.SpeedFov = v);
            string[] lines = { "Off", "Subtle", "Strong" };
            var speedLines = new Stepper(rc, "Speed lines", 3, i => lines[i], Prefs.Effective.SpeedLines, 820, 0.3f);
            speedLines.Changed += i => Save(p => { UseCustom(p); p.Custom.SpeedLines = i; });
            strengthRows.Add((speedLines, () => Prefs.Effective.SpeedLines));
            var blur = new Stepper(rc, "Motion blur", 2, i => i == 0 ? "Off" : "On", Prefs.MotionBlur ? 1 : 0, 820, 0.3f);
            blur.Changed += i => Save(p => p.MotionBlur = i == 1);
            // Live instrument preview — simulated values, clearly labelled (not a dyno or a test result).
            RectTransform previewArea = UIFactory.Rect("InstrumentPreview", root, new Vector2(0.46f, 0f), new Vector2(0.99f, 0.33f), Vector2.zero, Vector2.zero);
            RectTransform notes = UIFactory.Column("PreviewNotes", previewArea, new Vector2(0, 0), new Vector2(0.55f, 1), new Vector2(40, 0), new Vector2(0, -24), 6f);
            UIFactory.Row("PreviewLabel", notes, "PREVIEW — SIMULATED VALUES, NOT A READING", SignalTheme.Small, SignalTheme.LabelDim, 520, 26, true);
            UIFactory.Row("CameraNote", notes, "Presentation only: no effect is required for any event. Reduced Motion turns every camera effect off.",
                SignalTheme.Small, SignalTheme.LabelDim, 520, 70);
            preview = new UI.SpeedCluster(previewArea);
            preview.Root.localScale = Vector3.one * 0.9f;
        }

        void AddStrength(Transform col, string label, Func<float> get, Action<UI.DrivingPreferences, float> set)
        {
            var step = new Stepper(col, label, 11, i => i == 0 ? "off" : $"{i * 10}%", Mathf.RoundToInt(get() * 10f), 820, 0.3f);
            step.Changed += i => Save(p => { UseCustom(p); set(p, i / 10f); });
            strengthRows.Add((step, () => get() * 10f));
        }

        /// <summary>Editing a strength switches to Custom, starting from the values in effect (so nothing jumps).</summary>
        void UseCustom(UI.DrivingPreferences p)
        {
            if (p.MotionPreset == "custom") return;
            p.Custom = p.Effective;
            p.MotionPreset = "custom";
            preset?.Set(2);
        }

        void RefreshStrengths()
        {
            foreach ((Stepper step, Func<float> get) in strengthRows) step.Set(Mathf.RoundToInt(get()));
        }

        static void Save(Action<UI.DrivingPreferences> change)
        {
            UI.DrivingPreferences p = Prefs;
            change(p);
            p.Save();
            ApplyAccessibility(p);
        }

        public override void Tick()
        {
            if (preview == null) return;
            // Simulated sweep for the preview only: 0 → 70 m/s and back over eight seconds, with a pretend gear/rev pattern.
            previewTime += Time.unscaledDeltaTime;
            float phase = Mathf.PingPong(previewTime / 4f, 1f);
            float mps = phase * 70f;
            int gear = 1 + Mathf.Min(5, (int)(phase * 6f));
            float rpm = 2500f + Mathf.Repeat(phase * 6f, 1f) * 4800f;
            UI.DrivingPreferences p = Prefs;
            preview.Configure(p.Dial, UI.SpeedDisplay.ScaleFor(70f, p.Unit));
            preview.Render(mps, rpm, 7400f, gear, true, Time.unscaledDeltaTime);
        }

        static void AddVolume(Transform col, string label, Func<float> get, Action<float> set)
        {
            var s = new Stepper(col, label, 11, i => i == 0 ? "off" : $"{i * 10}%", Mathf.RoundToInt(get() * 10f));
            s.Changed += i => set(i / 10f);
        }

        public override Selectable DefaultFocus => textSize.Left;
    }
}
