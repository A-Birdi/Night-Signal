using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Tutorial;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// The Driving School (spec §16, T00): every lesson in a list with its state, a searchable help index, and the lesson
    /// on the right — a drive lesson's goal and help with "Try the lesson" and "Watch the demonstration" (the autopilot,
    /// marked as a training aid), or a knowledge card's check question. Passing is training progress kept in the Local
    /// profile; nothing here gates a race.
    /// </summary>
    public sealed class TutorialScreen : UIScreen
    {
        public override string ScreenName => "DrivingSchool";
        public const int Rows = 14;
        TextMeshProUGUI count, title, goal, help, question, result;
        TMP_InputField search;
        readonly List<Button> rows = new List<Button>();
        readonly List<Button> choices = new List<Button>();
        Button tryLesson, watch, back;
        List<TutorialLesson> shown = new List<TutorialLesson>();
        TutorialLesson open;
        readonly Dictionary<string, string> lastResult = new Dictionary<string, string>();

        /// <summary>Lessons listed under the current search, the one open, and its last feedback (tours read them).</summary>
        public IReadOnlyList<TutorialLesson> Shown => shown;
        public TutorialLesson Open => open;
        public string Result => result != null ? result.text : "";

        TutorialLessons Lessons => ContentLibrary.Load()?.Tutorial;
        HashSet<string> Passed => new HashSet<string>(LocalSession.Current?.Profile?.Tutorial?.LessonsPassed ?? new List<string>(), StringComparer.Ordinal);

        protected override void OnBuild(RectTransform root)
        {
            Image list = UIFactory.Panel("ListPanel", root, new Vector2(0, 0), new Vector2(0.4f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.92f));
            RectTransform col = UIFactory.Column("List", list.transform, new Vector2(0, 0.02f), new Vector2(1, 0.94f), new Vector2(56, 0), new Vector2(-28, 0), 6f);
            UIFactory.Row("Heading", col, "DRIVING SCHOOL", SignalTheme.Heading, SignalTheme.Label, 600, 0, true);
            count = UIFactory.Row("Count", col, "", SignalTheme.Small, SignalTheme.LabelDim, 600, 28);
            search = UIFactory.InputField("HelpSearch", col, "Search the help index (e.g. brake, camera, convoy)", false, 40, 600, 48);
            search.onValueChanged.AddListener(_ => Refresh());
            for (int i = 0; i < Rows; i++)
            {
                int slot = i;
                rows.Add(UIFactory.Button("Lesson" + i, col, "", () => Select(slot < shown.Count ? shown[slot] : null), 600, 42));
            }
            back = UIFactory.Button("Back", col, "Back", () => App.Router.Back(), 600, 46);

            Image detail = UIFactory.Panel("Lesson", root, new Vector2(0.42f, 0.06f), new Vector2(0.97f, 0.94f), Vector2.zero, Vector2.zero, new Color(0.04f, 0.045f, 0.055f, 0.9f));
            RectTransform dcol = UIFactory.Column("LessonColumn", detail.transform, new Vector2(0, 0.03f), new Vector2(1, 0.97f), new Vector2(40, 0), new Vector2(-40, 0), 10f);
            title = UIFactory.Row("LessonTitle", dcol, "", SignalTheme.Subheading, SignalTheme.Label, 900, 44, true);
            goal = UIFactory.Row("LessonGoal", dcol, "", SignalTheme.Body, SignalTheme.Caution, 900, 60);
            goal.textWrappingMode = TextWrappingModes.Normal;
            help = UIFactory.Row("LessonHelp", dcol, "", SignalTheme.Body, SignalTheme.Label, 900, 190);
            help.textWrappingMode = TextWrappingModes.Normal;
            help.richText = false;
            tryLesson = UIFactory.Button("TryLesson", dcol, "Try the Lesson", () => { if (open != null) App.StartLesson(open, false); }, 520, 56);
            watch = UIFactory.Button("WatchDemonstration", dcol, "Watch the Demonstration", () => { if (open != null) App.StartLesson(open, true); }, 520, 52);
            question = UIFactory.Row("CardQuestion", dcol, "", SignalTheme.Body, SignalTheme.Label, 900, 40);
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                choices.Add(UIFactory.Button("Choice" + i, dcol, "", () => Answer(k), 900, 48));
            }
            result = UIFactory.Row("LessonResult", dcol, "", SignalTheme.Body, SignalTheme.Label, 900, 80);
            result.textWrappingMode = TextWrappingModes.Normal;
            result.richText = true;
        }

        public override Selectable DefaultFocus => rows.Count > 0 && rows[0].gameObject.activeSelf ? rows[0] : back;

        public override void OnShow()
        {
            Refresh();
            Select(open ?? shown.FirstOrDefault());
        }

        /// <summary>A lesson run came back: its feedback is shown on the lesson it belongs to.</summary>
        public void ShowResult(TutorialLesson lesson, string text)
        {
            if (lesson == null) return;
            lastResult[lesson.Id] = text;
            open = lesson;
        }

        /// <summary>Automation hook (tours): type into the help index as a player would.</summary>
        public void Search(string text) => search.text = text ?? "";

        /// <summary>Automation hook (tours): open a lesson by id (it must be listed under the current search).</summary>
        public bool SelectLesson(string id)
        {
            TutorialLesson l = shown.FirstOrDefault(x => x.Id == id);
            if (l == null) return false;
            Select(l);
            return true;
        }

        void Refresh()
        {
            TutorialLessons t = Lessons;
            shown = t == null ? new List<TutorialLesson>() : t.Search(search != null ? search.text : "");
            HashSet<string> passed = Passed;
            int drive = t?.Lessons.Count(l => l.IsDrive) ?? 0, cards = (t?.Lessons.Count ?? 0) - drive;
            count.text = t == null ? "The lessons are missing from this build."
                : LocalSession.Current?.Profile == null ? $"{drive} drive lessons · {cards} cards · practice (no profile open: progress is not kept)"
                : $"{passed.Count(p => t.Find(p) != null)} of {t.Lessons.Count} passed · {drive} drive lessons, {cards} cards · none is needed to race";
            for (int i = 0; i < rows.Count; i++)
            {
                bool on = i < shown.Count;
                rows[i].gameObject.SetActive(on);
                if (!on) continue;
                TutorialLesson l = shown[i];
                rows[i].GetComponentInChildren<TextMeshProUGUI>().text = $"{(l.IsDrive ? "Drive" : "Card")} · {l.Title}{(passed.Contains(l.Id) ? "  <color=#3EC6D8>passed</color>" : "")}";
            }
        }

        void Select(TutorialLesson l)
        {
            open = l;
            bool has = l != null, drive = has && l.IsDrive;
            title.text = has ? l.Title : "No lesson matches that search.";
            goal.text = has ? l.Goal : "";
            help.text = has ? l.Help : "";
            tryLesson.gameObject.SetActive(drive);
            watch.gameObject.SetActive(drive);
            question.gameObject.SetActive(has && !drive);
            question.text = has && !drive ? l.Question : "";
            for (int i = 0; i < choices.Count; i++)
            {
                bool on = has && !drive && i < l.Choices.Count;
                choices[i].gameObject.SetActive(on);
                if (on) choices[i].GetComponentInChildren<TextMeshProUGUI>().text = l.Choices[i];
            }
            result.text = has && lastResult.TryGetValue(l.Id, out string r) ? r : has && Passed.Contains(l.Id) ? "<color=#3EC6D8>Passed.</color> Retry any time." : "";
        }

        void Answer(int choice)
        {
            if (open == null || open.IsDrive) return;
            bool right = LessonJudge.AnswerCard(open, choice, out string feedback);
            result.text = right ? $"<color=#3EC6D8>{feedback}</color>" : $"<color=#F2A541>{feedback}</color>";
            lastResult[open.Id] = result.text;
            Debug.Log($"[NightSignal.Tutorial] card {open.Id}: chose {choice} — {(right ? "right" : "not quite")}");
            if (right) App.MarkLessonPassed(open);
            Refresh();
        }
    }
}
