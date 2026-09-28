using System;
using System.Collections.Generic;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Core.Story;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// A stage's introductory scene (spec §5.3): the act and stage title, then the authored lines one at a time with their
    /// speaker — about 10–18 s in all, each line held long enough to read and advanced by itself. Next moves on, Skip ends
    /// the scene at once (locally: it never starts anything for anyone else). On a rematch only the opening and closing lines
    /// play; the full scene stays in the race diary.
    /// </summary>
    public sealed class StoryScreen : UIScreen
    {
        public override string ScreenName => "Story";
        TextMeshProUGUI act, title, speaker, line, progress, note;
        Button next, skip;
        List<StoryLine> lines = new List<StoryLine>();
        float[] holds = new float[0];
        int index;
        float shownAt;
        Action done;
        string player = "";

        /// <summary>The line on screen now (tours read it) and whether the scene has ended.</summary>
        public int LineIndex => index;
        public int LineCount => lines.Count;
        public bool Finished { get; private set; } = true;
        /// <summary>The last scene was read to its end (not skipped).</summary>
        public bool Completed { get; private set; }

        protected override void OnBuild(RectTransform root)
        {
            UIFactory.Panel("Shade", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.03f, 0.035f, 0.045f, 0.96f));
            act = Text("Act", root, SignalTheme.Small, SignalTheme.LabelDim, new Vector2(0.1f, 0.84f), new Vector2(0.9f, 0.9f), true);
            title = Text("Title", root, SignalTheme.Heading, SignalTheme.Label, new Vector2(0.1f, 0.74f), new Vector2(0.9f, 0.84f), true);
            speaker = Text("Speaker", root, SignalTheme.Subheading, SignalTheme.Signal, new Vector2(0.1f, 0.54f), new Vector2(0.9f, 0.6f), true);
            line = Text("Line", root, SignalTheme.Subheading, SignalTheme.Label, new Vector2(0.1f, 0.3f), new Vector2(0.9f, 0.54f), false);
            line.textWrappingMode = TextWrappingModes.Normal;
            progress = Text("Progress", root, SignalTheme.Small, SignalTheme.LabelDim, new Vector2(0.1f, 0.24f), new Vector2(0.5f, 0.29f), false);
            note = Text("Note", root, SignalTheme.Small, SignalTheme.Timing, new Vector2(0.1f, 0.19f), new Vector2(0.9f, 0.24f), false);
            RectTransform actions = UIFactory.Column("Actions", root, new Vector2(0.1f, 0.06f), new Vector2(0.9f, 0.16f), Vector2.zero, Vector2.zero, 10f);
            var row = new GameObject("Buttons", typeof(RectTransform), typeof(HorizontalLayoutGroup)).GetComponent<RectTransform>();
            row.SetParent(actions, false);
            row.sizeDelta = new Vector2(760, 60);
            var h = row.GetComponent<HorizontalLayoutGroup>();
            h.spacing = 16f;
            h.childForceExpandWidth = false;
            h.childControlWidth = false;
            next = UIFactory.Button("Story-Next", row, "Next", Next, 300, 56);
            skip = UIFactory.Button("Story-Skip", row, "Skip", Skip, 300, 56);
        }

        static TextMeshProUGUI Text(string name, Transform parent, float size, Color color, Vector2 min, Vector2 max, bool heading)
        {
            TextMeshProUGUI t = UIFactory.Label(name, parent, "", size, color, TextAlignmentOptions.TopLeft, heading);
            t.rectTransform.anchorMin = min;
            t.rectTransform.anchorMax = max;
            t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
            return t;
        }

        public override Selectable DefaultFocus => next;

        /// <summary>
        /// Plays scenes (an ending): each scene opens with its setting in italics, then its lines; each scene is paced like an
        /// intro. <paramref name="closing"/> (the post-game note) is shown last. <paramref name="onDone"/> runs when it ends or
        /// is skipped; <see cref="Completed"/> says which.
        /// </summary>
        public void PlayScenes(string heading, string sceneTitle, List<StoryScene> scenes, string closing, string playerName, Action onDone)
        {
            lines = new List<StoryLine>();
            var h = new List<float>();
            foreach (StoryScene sc in scenes ?? new List<StoryScene>())
            {
                var part = new List<StoryLine> { new StoryLine { Speaker = "setting", Line = sc.Setting } };
                part.AddRange(sc.Lines);
                lines.AddRange(part);
                h.AddRange(StoryText.Holds(part, false));
            }
            if (!string.IsNullOrEmpty(closing))
            {
                lines.Add(new StoryLine { Speaker = "setting", Line = closing });
                h.Add(Mathf.Max(StoryText.LineMinSeconds, closing.Length / StoryText.CharactersPerSecond));
            }
            holds = h.ToArray();
            player = playerName ?? "";
            done = onDone;
            index = 0;
            Completed = false;
            Finished = lines.Count == 0;
            if (act != null)
            {
                act.text = heading ?? "";
                title.text = sceneTitle ?? "";
                note.text = "";
            }
            if (Finished) End();
            else ShowLine();
        }

        /// <summary>Plays the intro of <paramref name="stage"/> in <paramref name="mode"/>; <paramref name="onDone"/> runs when it ends or is skipped.</summary>
        public void Play(StageDef stage, CampaignMode mode, bool rematch, string playerName, Action onDone)
        {
            StoryText story = ContentLibrary.Load()?.Story;
            lines = story?.Intro(stage.Id, mode, rematch) ?? new List<StoryLine>();
            holds = StoryText.Holds(lines, rematch);
            player = playerName ?? "";
            done = onDone;
            index = 0;
            Completed = false;
            Finished = lines.Count == 0;
            ActStory a = story?.Act(stage.Act);
            string stageTitle = story != null && story.TryStage(stage.Id, out StageStory st) ? st.Side(mode).Title : "";
            if (act != null)
            {
                act.text = a != null ? $"ACT {a.Act}  ·  {a.Title.ToUpperInvariant()}" : "";
                title.text = $"{stage.Id}{(mode == CampaignMode.Hard ? " HARD" : "")}  ·  {stageTitle}";
                note.text = rematch ? "A rematch: the short version. The full scene is in the race diary." : "";
            }
            if (Finished) End();
            else ShowLine();
        }

        public override void OnShow()
        {
            if (!Finished) ShowLine();
        }

        void ShowLine()
        {
            if (line == null || index >= lines.Count) return;
            StoryLine l = lines[index];
            ContentCatalogue cat = ContentLibrary.Load()?.Catalogue;
            bool narration = l.Speaker == "narration" || l.Speaker == "setting";
            speaker.text = narration ? "" : SpeakerName(l.Speaker, cat);
            line.text = narration ? $"<i>{Escape(StoryText.Fill(l.Line, player, ""))}</i>" : Escape(StoryText.Fill(l.Line, player, ""));
            progress.text = $"{index + 1} / {lines.Count}";
            shownAt = Time.unscaledTime;
        }

        public override void Tick()
        {
            if (Finished || index >= lines.Count) return;
            if (Time.unscaledTime - shownAt >= (index < holds.Length ? holds[index] : StoryText.LineMinSeconds)) Next();
        }

        public void Next()
        {
            if (Finished) return;
            index++;
            if (index >= lines.Count)
            {
                Completed = true;
                End();
            }
            else ShowLine();
        }

        public void Skip()
        {
            if (!Finished) End();
        }

        void End()
        {
            Finished = true;
            Action d = done;
            done = null;
            d?.Invoke();
        }

        /// <summary>A rival's name, or the narrators' labels.</summary>
        public static string SpeakerName(string speaker, ContentCatalogue cat)
        {
            switch (speaker)
            {
                case "setting": return "";
                case "radio": return "Night Signal radio";
                case "timing-crew": return "Timing crew";
                case "narration": return "";
            }
            return cat != null && cat.TryRival(speaker, out RivalDef r) ? r.Name : speaker;
        }

        static string Escape(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");
    }
}
