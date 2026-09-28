using System;
using System.Collections.Generic;
using NightSignal.Core.Meet;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NightSignal.Meet
{
    /// <summary>
    /// The meet's screen layer: location header, interaction prompt, control hints, the SIGNAL notification ribbon
    /// (upper centre, slides in and out, never takes focus), the twelve-slot emote wheel, one modal panel (boombox,
    /// inspection, dialogue, placards, timing board, menu) with controller-navigable buttons, world nameplates, a quick-chat
    /// bubble, and a photo-mode state that hides everything but a one-line note.
    /// </summary>
    public sealed class MeetHud : IDisposable
    {
        public readonly Canvas Canvas;
        public readonly SignalRibbonQueue Ribbon = new SignalRibbonQueue();
        readonly RectTransform root, ribbonRect, wheel, panel, buttonColumn, rescueBar, rescueFill;
        readonly TextMeshProUGUI title, subtitle, prompt, hints, status, ribbonText, wheelCentre, panelTitle, panelBody, photoNote, bubble;
        readonly List<TextMeshProUGUI> wheelLabels = new List<TextMeshProUGUI>();
        readonly List<Image> wheelSlots = new List<Image>();
        readonly Dictionary<object, TextMeshProUGUI> nameplates = new Dictionary<object, TextMeshProUGUI>();
        readonly List<Button> buttons = new List<Button>();
        bool photo;

        public bool PanelOpen => panel.gameObject.activeSelf;
        public bool WheelOpen => wheel.gameObject.activeSelf;
        public string PanelTitle => panelTitle.text;
        public string PanelBody => panelBody.text;
        public string Prompt => prompt.text;
        public IReadOnlyList<Button> PanelButtons => buttons;

        public MeetHud(string location, string mode)
        {
            Canvas = UIFactory.Root("MeetHud", 20);
            root = (RectTransform)Canvas.transform;
            var header = UIFactory.Rect("Header", root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(40, -118), new Vector2(760, -36));
            title = UIFactory.Label("Title", header, location, 30, SignalTheme.Text, TextAlignmentOptions.TopLeft, true);
            UIFactory.Stretch(title.rectTransform);
            subtitle = UIFactory.Label("Mode", header, mode, 20, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft);
            UIFactory.Stretch(subtitle.rectTransform);

            Image promptBack = UIFactory.Panel("PromptBack", root, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-360, 150), new Vector2(360, 204), new Color(0.05f, 0.06f, 0.07f, 0.72f));
            prompt = UIFactory.Label("Prompt", promptBack.transform, "", 24, SignalTheme.Text, TextAlignmentOptions.Center);
            UIFactory.Stretch(prompt.rectTransform, 8);

            hints = UIFactory.Label("Hints", root, "", 17, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft);
            hints.rectTransform.anchorMin = new Vector2(0, 0);
            hints.rectTransform.anchorMax = new Vector2(0, 0);
            hints.rectTransform.offsetMin = new Vector2(40, 36);
            hints.rectTransform.offsetMax = new Vector2(760, 250);
            hints.textWrappingMode = TextWrappingModes.Normal;

            status = UIFactory.Label("Status", root, "", 20, SignalTheme.Caution, TextAlignmentOptions.Center);
            status.rectTransform.anchorMin = new Vector2(0.5f, 0);
            status.rectTransform.anchorMax = new Vector2(0.5f, 0);
            status.rectTransform.offsetMin = new Vector2(-400, 214);
            status.rectTransform.offsetMax = new Vector2(400, 246);

            rescueBar = UIFactory.Panel("RescueBar", root, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-160, 252), new Vector2(160, 260), SignalTheme.Rule).rectTransform;
            rescueFill = UIFactory.Panel("Fill", rescueBar, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, Vector2.zero, SignalTheme.Caution).rectTransform;
            rescueBar.gameObject.SetActive(false);

            // The compact convoy header (online, in a convoy): upper centre, above the SIGNAL ribbon.
            // A dark plate sized to the text keeps it readable against the low sun.
            convoyBack = UIFactory.Panel("ConvoyHeader", root, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-520, -92), new Vector2(520, -58), new Color(0.05f, 0.06f, 0.07f, 0.78f)).rectTransform;
            convoy = UIFactory.Label("Text", convoyBack, "", 19, SignalTheme.Text, TextAlignmentOptions.Center);
            convoy.rectTransform.anchorMin = Vector2.zero;
            convoy.rectTransform.anchorMax = Vector2.one;
            convoy.rectTransform.offsetMin = new Vector2(18, 2);
            convoy.rectTransform.offsetMax = new Vector2(-18, -2);
            convoy.textWrappingMode = TextWrappingModes.Normal;
            convoyBack.gameObject.SetActive(false);
            convoy.outlineWidth = 0.15f;
            convoy.outlineColor = new Color32(0, 0, 0, 180);

            // SIGNAL ribbon: upper centre, below where the convoy header sits.
            Image rb = UIFactory.Panel("SignalRibbon", root, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-330, -176), new Vector2(330, -128), new Color(0.07f, 0.08f, 0.1f, 0.9f));
            ribbonRect = rb.rectTransform;
            UIFactory.Panel("Edge", ribbonRect, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(8, 0), SignalTheme.Signal);
            TextMeshProUGUI tag = UIFactory.Label("Tag", ribbonRect, "SIGNAL", 16, SignalTheme.Signal, TextAlignmentOptions.MidlineLeft, true);
            tag.rectTransform.anchorMin = new Vector2(0, 0);
            tag.rectTransform.anchorMax = new Vector2(0, 1);
            tag.rectTransform.offsetMin = new Vector2(22, 0);
            tag.rectTransform.offsetMax = new Vector2(122, 0);
            ribbonText = UIFactory.Label("Text", ribbonRect, "", 22, SignalTheme.Text, TextAlignmentOptions.MidlineLeft);
            ribbonText.rectTransform.anchorMin = new Vector2(0, 0);
            ribbonText.rectTransform.anchorMax = new Vector2(1, 1);
            ribbonText.rectTransform.offsetMin = new Vector2(128, 0);
            ribbonText.rectTransform.offsetMax = new Vector2(-16, 0);
            ribbonGroup = ribbonRect.gameObject.AddComponent<CanvasGroup>();
            ribbonGroup.blocksRaycasts = false;
            ribbonGroup.interactable = false;
            ribbonRect.gameObject.SetActive(false);

            // Emote wheel.
            wheel = UIFactory.Rect("EmoteWheel", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-330, -330), new Vector2(330, 330));
            UIFactory.Panel("Disc", wheel, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.04f, 0.05f, 0.06f, 0.55f));
            wheelCentre = UIFactory.Label("Centre", wheel, "", 26, SignalTheme.Text, TextAlignmentOptions.Center, true);
            wheelCentre.rectTransform.anchorMin = wheelCentre.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            wheelCentre.rectTransform.sizeDelta = new Vector2(260, 60);
            for (int i = 0; i < 12; i++)
            {
                float a = Mathf.PI * 0.5f - i * Mathf.PI * 2f / 12f;
                var c = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 250f;
                Image slot = UIFactory.Panel($"Slot{i + 1}", wheel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), c - new Vector2(80, 26), c + new Vector2(80, 26), SignalTheme.GraphiteRaised);
                TextMeshProUGUI l = UIFactory.Label("Label", slot.transform, "", 19, SignalTheme.Text, TextAlignmentOptions.Center);
                UIFactory.Stretch(l.rectTransform, 4);
                wheelSlots.Add(slot);
                wheelLabels.Add(l);
            }
            wheel.gameObject.SetActive(false);

            // Modal panel on the right: title, wrapped body, a column of buttons (its top clears the SIGNAL ribbon).
            Image p = UIFactory.Panel("Panel", root, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-700, -440), new Vector2(-40, 360), new Color(0.06f, 0.07f, 0.08f, 0.94f));
            p.raycastTarget = true;
            panel = p.rectTransform;
            UIFactory.Panel("Edge", panel, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(6, 0), SignalTheme.Signal);
            panelTitle = UIFactory.Label("Title", panel, "", 28, SignalTheme.Text, TextAlignmentOptions.TopLeft, true);
            panelTitle.rectTransform.anchorMin = new Vector2(0, 1);
            panelTitle.rectTransform.anchorMax = new Vector2(1, 1);
            panelTitle.rectTransform.offsetMin = new Vector2(32, -76);
            panelTitle.rectTransform.offsetMax = new Vector2(-24, -24);
            panelBody = UIFactory.Label("Body", panel, "", 20, SignalTheme.Text, TextAlignmentOptions.TopLeft);
            panelBody.rectTransform.anchorMin = new Vector2(0, 0);
            panelBody.rectTransform.anchorMax = new Vector2(1, 1);
            panelBody.rectTransform.offsetMin = new Vector2(32, 300);
            panelBody.rectTransform.offsetMax = new Vector2(-28, -88);
            panelBody.textWrappingMode = TextWrappingModes.Normal;
            panelBody.enableAutoSizing = true;
            panelBody.fontSizeMin = 13f;
            buttonColumn = UIFactory.Column("Buttons", panel, new Vector2(0, 0), new Vector2(1, 0), new Vector2(32, 24), new Vector2(-28, 290), 8f);
            panel.gameObject.SetActive(false);

            photoNote = UIFactory.Label("PhotoNote", root, "", 18, SignalTheme.LabelDim, TextAlignmentOptions.BottomRight);
            photoNote.rectTransform.anchorMin = photoNote.rectTransform.anchorMax = new Vector2(1, 0);
            photoNote.rectTransform.offsetMin = new Vector2(-900, 24);
            photoNote.rectTransform.offsetMax = new Vector2(-32, 60);
            photoNote.gameObject.SetActive(false);

            // Quick-chat bubble: a light card over the speaker's head.
            Image bubbleBg = UIFactory.Panel("Bubble", root, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, new Color(0.95f, 0.93f, 0.88f, 0.92f));
            bubble = UIFactory.Label("Text", bubbleBg.transform, "", 22, SignalTheme.Ink, TextAlignmentOptions.Center);
            UIFactory.Stretch(bubble.rectTransform, 6);
            bubbleRect = bubbleBg.rectTransform;
            bubbleRect.sizeDelta = new Vector2(300, 46);
            bubbleRect.gameObject.SetActive(false);
        }

        readonly RectTransform bubbleRect;
        readonly CanvasGroup ribbonGroup;
        readonly TextMeshProUGUI convoy;
        readonly RectTransform convoyBack;

        public void SetPrompt(string text)
        {
            prompt.text = text ?? "";
            prompt.transform.parent.gameObject.SetActive(!photo && !(panel != null && panel.gameObject.activeSelf) && !string.IsNullOrEmpty(text));
        }

        public void SetHints(string text) => hints.text = text;
        public void SetConvoy(string text)
        {
            text = photo ? "" : text ?? "";
            if (text == convoy.text && convoyBack.gameObject.activeSelf == (text.Length > 0)) return;
            convoy.text = text;
            convoyBack.gameObject.SetActive(text.Length > 0);
            if (text.Length == 0) return;
            Vector2 size = convoy.GetPreferredValues(text, 1004, 0);
            float w = Mathf.Min(1040, size.x + 40), h = Mathf.Max(34, size.y + 10);
            convoyBack.offsetMin = new Vector2(-w / 2, -58 - h);
            convoyBack.offsetMax = new Vector2(w / 2, -58);
        }
        public void SetStatus(string text) => status.text = text ?? "";
        public void SetMode(string text) => subtitle.text = text;

        public void SetRescue(float fraction)
        {
            rescueBar.gameObject.SetActive(fraction > 0f && !photo);
            rescueFill.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);
        }

        public void Notify(string text, string key = null) => Ribbon.Post(NoticeKind.Info, key, text);

        public void Tick(float dt)
        {
            Ribbon.Tick(dt);
            Notice n = Ribbon.Current;
            ribbonRect.gameObject.SetActive(n != null && !photo);
            if (n == null) return;
            ribbonText.text = n.Display();
            float phase = Ribbon.Phase;
            // Enter from the left, exit to the right (a short fade instead when motion is reduced).
            float x = SignalTheme.ReducedMotion ? 0f : phase < 1f ? Mathf.Lerp(-900f, 0f, Smooth(phase)) : phase > 1f ? Mathf.Lerp(0f, 900f, Smooth(phase - 1f)) : 0f;
            ribbonRect.anchoredPosition = new Vector2(x, ribbonRect.anchoredPosition.y);
            ribbonGroup.alpha = SignalTheme.ReducedMotion ? (phase < 1f ? phase : phase > 1f ? 2f - phase : 1f) : 1f;
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);

        // ------------------------------------------------------------------ emote wheel

        public void ShowWheel(IList<string> labels, int selected, string centre)
        {
            wheel.gameObject.SetActive(true);
            for (int i = 0; i < wheelLabels.Count; i++)
            {
                wheelLabels[i].text = i < labels.Count ? labels[i] : "";
                wheelSlots[i].color = i == selected ? SignalTheme.Signal : SignalTheme.GraphiteRaised;
            }
            wheelCentre.text = centre;
        }

        public void HideWheel() => wheel.gameObject.SetActive(false);

        // ------------------------------------------------------------------ panel

        public void ShowPanel(string heading, string body, IList<(string Label, Action Act)> actions)
        {
            panel.gameObject.SetActive(true);
            prompt.transform.parent.gameObject.SetActive(false); // the panel owns the interaction while open
            panelTitle.text = heading;
            panelBody.text = body;
            foreach (Button b in buttons) UnityEngine.Object.Destroy(b.gameObject);
            buttons.Clear();
            int n = actions?.Count ?? 0;
            // The buttons take what they need from the bottom (up to most of the panel); the text gets the rest.
            float rowH = n > 9 ? 36f : 44f, colH = Mathf.Min(560f, n * (rowH + 8f) + 8f);
            buttonColumn.offsetMax = new Vector2(buttonColumn.offsetMax.x, 24f + colH);
            panelBody.rectTransform.offsetMin = new Vector2(panelBody.rectTransform.offsetMin.x, 36f + colH);
            for (int i = 0; i < n; i++)
            {
                (string label, Action act) = actions[i];
                Button b = UIFactory.Button($"MeetAction{i}", buttonColumn, label, act, 560, rowH);
                buttons.Add(b);
            }
            if (buttons.Count > 0 && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(buttons[0].gameObject);
        }

        public void SetPanelBody(string body) => panelBody.text = body;

        public void HidePanel()
        {
            panel.gameObject.SetActive(false);
            SetPrompt(prompt.text);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }

        // ------------------------------------------------------------------ world labels

        /// <summary>A nameplate over a world point (hidden behind the camera, in photo mode, or when <paramref name="show"/> is false).</summary>
        public void Nameplate(object key, Camera cam, Vector3 world, string text, bool show)
        {
            if (!nameplates.TryGetValue(key, out TextMeshProUGUI t))
            {
                t = UIFactory.Label("Nameplate", root, "", 18, SignalTheme.Text, TextAlignmentOptions.Center);
                t.rectTransform.anchorMin = t.rectTransform.anchorMax = Vector2.zero;
                t.rectTransform.sizeDelta = new Vector2(320, 30);
                t.outlineWidth = 0.18f;
                t.outlineColor = new Color32(0, 0, 0, 200);
                nameplates[key] = t;
            }
            Vector3 sp = cam != null ? cam.WorldToScreenPoint(world) : Vector3.back;
            bool visible = show && !photo && sp.z > 0.5f && sp.z < 28f;
            t.gameObject.SetActive(visible);
            if (!visible) return;
            float scale = ((RectTransform)Canvas.transform).rect.width / Mathf.Max(1f, Screen.width);
            t.rectTransform.anchoredPosition = new Vector2(sp.x, sp.y) * scale;
            t.text = text;
            t.alpha = Mathf.Clamp01((28f - sp.z) / 8f);
        }

        public void Bubble(Camera cam, Vector3 world, string text)
        {
            bool show = !string.IsNullOrEmpty(text) && !photo && cam != null;
            Vector3 sp = show ? cam.WorldToScreenPoint(world) : Vector3.back;
            show &= sp.z > 0.3f;
            bubbleRect.gameObject.SetActive(show);
            if (!show) return;
            float scale = ((RectTransform)Canvas.transform).rect.width / Mathf.Max(1f, Screen.width);
            bubbleRect.anchoredPosition = new Vector2(sp.x, sp.y) * scale + new Vector2(0, 30);
            bubble.text = text;
            bubbleRect.sizeDelta = new Vector2(Mathf.Clamp(text.Length * 12f + 40f, 140f, 520f), 46f);
        }

        public void SetPhotoMode(bool on, string note)
        {
            photo = on;
            photoNote.gameObject.SetActive(on);
            photoNote.text = note;
            title.gameObject.SetActive(!on);
            subtitle.gameObject.SetActive(!on);
            hints.gameObject.SetActive(!on);
            status.gameObject.SetActive(!on);
            if (on)
            {
                SetPrompt("");
                HideWheel();
                ribbonRect.gameObject.SetActive(false);
            }
        }

        public void Dispose()
        {
            if (Canvas != null) UnityEngine.Object.Destroy(Canvas.gameObject);
        }
    }
}
