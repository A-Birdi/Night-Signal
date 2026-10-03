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
    /// play; the full scene stays in the race diary. A rival who speaks is shown beside the line as a live portrait — their
    /// own 3D character, the one met at the meet and raced against, idling (a nod when a new line begins, unless motion is
    /// reduced); the radio and the timing crew have their marks; narration has none.
    /// </summary>
    public sealed class StoryScreen : UIScreen
    {
        public override string ScreenName => "Story";
        TextMeshProUGUI act, title, speaker, line, progress, note;
        Button next, skip;
        RawImage portrait, mark;
        CharacterStage stage;
        Texture2D markTex;
        readonly Color32[] markPixels = new Color32[CardAvatarArt.Size * CardAvatarArt.Size];
        string portraitSpeaker = "";
        static readonly Vector3 StageOrigin = new Vector3(0f, -5600f, 0f);

        /// <summary>Whose portrait is beside the line now: a rival id, "radio", "timing-crew", or "" (narration). Tours read it.</summary>
        public string PortraitShown => portraitSpeaker;
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
            // The speaker's portrait on the left, the line beside it.
            var portraitGo = new GameObject("Portrait", typeof(RectTransform), typeof(RawImage));
            portraitGo.transform.SetParent(root, false);
            var prt = (RectTransform)portraitGo.transform;
            prt.anchorMin = new Vector2(0.1f, 0.3f);
            prt.anchorMax = new Vector2(0.1f, 0.3f);
            prt.pivot = Vector2.zero;
            prt.sizeDelta = new Vector2(300, 340);
            portrait = portraitGo.GetComponent<RawImage>();
            portrait.raycastTarget = false;
            portrait.enabled = false;
            var markGo = new GameObject("SpeakerMark", typeof(RectTransform), typeof(RawImage));
            markGo.transform.SetParent(root, false);
            var mrt = (RectTransform)markGo.transform;
            mrt.anchorMin = mrt.anchorMax = new Vector2(0.1f, 0.3f);
            mrt.pivot = Vector2.zero;
            mrt.anchoredPosition = new Vector2(46, 66);
            mrt.sizeDelta = new Vector2(208, 208);
            mark = markGo.GetComponent<RawImage>();
            mark.raycastTarget = false;
            mark.enabled = false;
            markTex = new Texture2D(CardAvatarArt.Size, CardAvatarArt.Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "StorySpeakerMark" };
            mark.texture = markTex;
            speaker = Text("Speaker", root, SignalTheme.Subheading, SignalTheme.Signal, new Vector2(0.33f, 0.54f), new Vector2(0.9f, 0.6f), true);
            line = Text("Line", root, SignalTheme.Subheading, SignalTheme.Label, new Vector2(0.33f, 0.28f), new Vector2(0.9f, 0.54f), false);
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

        public override void OnHide()
        {
            stage?.Dispose();
            stage = null;
            portraitSpeaker = "";
            if (portrait != null) portrait.enabled = false;
            if (mark != null) mark.enabled = false;
        }

        /// <summary>The portrait beside the line: the speaking rival's character, a mark for the radio and the timing crew, none for narration.</summary>
        void ShowPortrait(string who)
        {
            bool rival = ContentLibrary.Load()?.Look(who) != null;
            bool marked = who == "radio" || who == "timing-crew";
            string shown = rival || marked ? who : "";
            if (shown == portraitSpeaker)
            {
                // The same person again: a nod as the next line begins.
                if (rival && stage != null && !SignalTheme.ReducedMotion) stage.Play(Core.Meet.Emote.Nod);
                return;
            }
            portraitSpeaker = shown;
            portrait.enabled = rival;
            mark.enabled = marked;
            if (rival)
            {
                if (stage == null) stage = new CharacterStage(600, 680, StageOrigin);
                portrait.texture = stage.Texture;
                stage.Show(ContentLibrary.Load().Look(who), portraitFraming: true);
            }
            else if (marked)
            {
                var art = who == "radio"
                    ? new Core.Customization.CardAvatarDef { Art = "radio-dial", Colors = new List<string> { "#2A2018", "#E8D9B0", "#D7263D" } }
                    : new Core.Customization.CardAvatarDef { Art = "stopwatch", Colors = new List<string> { "#14181E", "#C9D1D9", "#F2F0EA" } };
                CardAvatarArt.Draw(art, markPixels);
                markTex.SetPixels32(markPixels);
                markTex.Apply(false);
            }
        }

        /// <summary>Writes the portrait as drawn now (evidence runs); false when no rival is shown.</summary>
        public bool SavePortrait(string path)
        {
            if (stage == null || !portrait.enabled) return false;
            stage.SaveTexture(path);
            return true;
        }

        void ShowLine()
        {
            if (line == null || index >= lines.Count) return;
            StoryLine l = lines[index];
            ContentCatalogue cat = ContentLibrary.Load()?.Catalogue;
            bool narration = l.Speaker == "narration" || l.Speaker == "setting";
            speaker.text = narration ? "" : SpeakerName(l.Speaker, cat);
            ShowPortrait(narration ? "" : l.Speaker);
            line.text = narration ? $"<i>{Escape(StoryText.Fill(l.Line, player, ""))}</i>" : Escape(StoryText.Fill(l.Line, player, ""));
            progress.text = $"{index + 1} / {lines.Count}";
            shownAt = Time.unscaledTime;
        }

        public override void Tick()
        {
            if (stage != null && portrait.enabled) stage.Update(Time.unscaledDeltaTime);
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
