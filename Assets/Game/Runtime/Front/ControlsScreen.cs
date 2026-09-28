using System;
using System.Collections.Generic;
using NightSignal.InputBindings;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Settings → Controls (spec §6, Addendum 03 §2/§7.1): every driving action's keyboard and controller binding, changed by
    /// pressing the new key/button (Esc cancels), a warning when a binding is shared with another action, and one button
    /// back to the documented defaults. Stored as the Input System's override JSON in the local preferences; every
    /// driving session (race, online, practice, Test Yard) loads it. Change View keeps C / Select unless remapped.
    /// A second page holds the meet's walking controls (spec §12), stored separately and loaded by every meet visit.
    /// </summary>
    public sealed class ControlsScreen : UIScreen
    {
        public override string ScreenName => "Controls";

        sealed class Row
        {
            public string Label, Action, Part;
            public bool Walking;
            public Button Keyboard, Pad;
            public GameObject Root;
        }

        static readonly (string Label, string Action, string Part, bool Pad)[] Layout =
        {
            ("Steer left", "Steer", "negative", false),
            ("Steer right", "Steer", "positive", false),
            ("Throttle", "Throttle", null, true),
            ("Brake / reverse", "Brake", null, true),
            ("Handbrake", "Handbrake", null, true),
            ("Shift up", "ShiftUp", null, true),
            ("Shift down", "ShiftDown", null, true),
            ("Change view", "Camera", null, true),
            ("Look back (hold)", "LookBack", null, true),
            ("Reset to track (hold)", "Reset", null, true),
            ("Pause / menu", "Pause", null, true),
        };

        /// <summary>The meet (spec §12): walk, jog, interact, emote wheel, recenter, photo, quick chat, rescue, menu.</summary>
        static readonly (string Label, string Action, string Part, bool Pad)[] WalkingLayout =
        {
            ("Walk forward", "Move", "up", false),
            ("Walk back", "Move", "down", false),
            ("Walk left", "Move", "left", false),
            ("Walk right", "Move", "right", false),
            ("Jog (hold)", "Jog", null, true),
            ("Interact", "Interact", null, true),
            ("Emote wheel (hold)", "EmoteWheel", null, true),
            ("Quick chat", "QuickChat", null, true),
            ("Recenter camera", "Recenter", null, true),
            ("Photo mode", "Photo", null, true),
            ("Rescue to car (hold)", "Rescue", null, true),
            ("Meet menu", "Menu", null, true),
        };

        const string DrivingHint = "Select a binding, then press the new key or button. Esc cancels. Arrow keys and the left stick also steer and drive.";
        const string WalkingHint = "The meet: select a binding, then press the new key or button. Esc cancels. Arrow keys and the left stick also walk; the mouse and right stick look; 1–9, 0, - and = play emotes directly.";

        DrivingControls model;
        WalkingControls walkModel;
        bool walking;
        TextMeshProUGUI heading, hint;
        Button modeButton;
        readonly List<Row> rows = new List<Row>();
        TextMeshProUGUI status, conflicts;
        InputActionRebindingExtensions.RebindingOperation pending;

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.62f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.9f));
            RectTransform col = UIFactory.Column("Bindings", panel.transform, new Vector2(0, 0.03f), new Vector2(1, 0.93f), new Vector2(64, 0), new Vector2(-32, 0), 4f);
            heading = UIFactory.Row("Heading", col, "CONTROLS · DRIVING", SignalTheme.Heading, SignalTheme.Label, 980, 0, true);
            hint = UIFactory.Row("Hint", col, DrivingHint, SignalTheme.Small, SignalTheme.LabelDim, 980, 48);
            RectTransform heads = UIFactory.Rect("Columns", col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            heads.sizeDelta = new Vector2(980, 30);
            foreach ((string text, float x) in new[] { ("KEYBOARD", 0.36f), ("CONTROLLER", 0.68f) })
            {
                TextMeshProUGUI h = UIFactory.Label(text, heads, text, SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft, true);
                h.rectTransform.anchorMin = new Vector2(x, 0);
                h.rectTransform.anchorMax = new Vector2(x + 0.3f, 1);
                h.rectTransform.offsetMin = h.rectTransform.offsetMax = Vector2.zero;
            }
            foreach (var l in Layout) AddRow(col, l, false);
            foreach (var l in WalkingLayout) AddRow(col, l, true);
            conflicts = UIFactory.Row("Conflicts", col, "", SignalTheme.Small, SignalTheme.Caution, 980, 56);
            status = UIFactory.Row("Status", col, "", SignalTheme.Small, SignalTheme.Label, 980, 30);
            RectTransform buttons = UIFactory.Rect("Buttons", col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            buttons.sizeDelta = new Vector2(980, 56);
            Button restore = UIFactory.Button("RestoreDefaults", buttons, "Restore defaults", RestoreDefaults, 300, 52);
            Place(restore, 0f);
            Button back = UIFactory.Button("Back", buttons, "Back", () => App.Router.Back(), 300, 52);
            Place(back, 0.33f);
            modeButton = UIFactory.Button("ControlsMode", buttons, "Walking (meet)", () => ShowPage(!walking), 300, 52);
            Place(modeButton, 0.66f);
        }

        void AddRow(RectTransform col, (string Label, string Action, string Part, bool Pad) l, bool walk)
        {
            string prefix = walk ? "Walk." : "";
            RectTransform r = UIFactory.Rect(prefix + l.Label, col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            r.sizeDelta = new Vector2(980, walk ? 46 : 50);
            TextMeshProUGUI name = UIFactory.Label("Label", r, l.Label, SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.MidlineLeft, true);
            name.rectTransform.anchorMin = new Vector2(0, 0);
            name.rectTransform.anchorMax = new Vector2(0.36f, 1);
            name.rectTransform.offsetMin = Vector2.zero;
            name.rectTransform.offsetMax = Vector2.zero;
            var row = new Row { Label = l.Label, Action = l.Action, Part = l.Part, Walking = walk, Root = r.gameObject };
            row.Keyboard = UIFactory.Button($"{prefix}{l.Action}{l.Part}/Keyboard", r, "", () => Rebind(row, "<Keyboard>"), 300, walk ? 42 : 46);
            Place(row.Keyboard, 0.36f);
            if (l.Pad)
            {
                row.Pad = UIFactory.Button($"{prefix}{l.Action}/Controller", r, "", () => Rebind(row, "<Gamepad>"), 300, walk ? 42 : 46);
                Place(row.Pad, 0.68f);
            }
            else if (walk)
            {
                TextMeshProUGUI stick = UIFactory.Label("Stick", r, "Left stick", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.MidlineLeft);
                stick.rectTransform.anchorMin = new Vector2(0.68f, 0);
                stick.rectTransform.anchorMax = new Vector2(1f, 1);
                stick.rectTransform.offsetMin = new Vector2(16, 0);
                stick.rectTransform.offsetMax = Vector2.zero;
            }
            rows.Add(row);
        }

        /// <summary>Driving or the meet's walking controls (tours and the page button).</summary>
        public void ShowPage(bool walkingPage)
        {
            Cancel();
            walking = walkingPage;
            foreach (Row r in rows) r.Root.SetActive(r.Walking == walking);
            heading.text = walking ? "CONTROLS · WALKING (MEET)" : "CONTROLS · DRIVING";
            hint.text = walking ? WalkingHint : DrivingHint;
            SetText(modeButton, walking ? "Driving" : "Walking (meet)");
            status.text = "";
            Refresh();
        }

        InputActionMap MapFor(Row r) => r.Walking ? walkModel?.Map : model?.Map;

        static void Place(Button b, float x)
        {
            var rt = (RectTransform)b.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(x, 0.5f);
            rt.pivot = new Vector2(0, 0.5f);
            rt.anchoredPosition = Vector2.zero;
        }

        public override void OnShow()
        {
            model?.Dispose();
            model = new DrivingControls(); // loads the stored overrides
            walkModel?.Dispose();
            walkModel = new WalkingControls();
            ShowPage(false);
        }

        public override void OnHide()
        {
            Cancel();
            model?.Dispose();
            model = null;
            walkModel?.Dispose();
            walkModel = null;
        }

        public override bool OnBack()
        {
            if (pending == null) return false;
            Cancel(); // Esc while waiting for a key cancels the capture, not the screen
            return true;
        }

        void Refresh()
        {
            if (model == null || walkModel == null) return;
            foreach (Row r in rows)
            {
                if (r.Walking != walking) continue;
                InputAction a = MapFor(r).FindAction(r.Action);
                SetText(r.Keyboard, Display(a, DrivingControls.BindingIndex(a, "<Keyboard>", r.Part)));
                if (r.Pad != null) SetText(r.Pad, Display(a, DrivingControls.BindingIndex(a, "<Gamepad>", null)));
            }
            List<string> shared = DrivingControls.Conflicts(walking ? walkModel.Map : model.Map);
            conflicts.text = shared.Count == 0 ? "" : "Shared bindings (both actions respond): " + string.Join("; ", shared);
        }

        static string Display(InputAction a, int index) =>
            index < 0 ? "—" : a.GetBindingDisplayString(index, InputBinding.DisplayStringOptions.DontIncludeInteractions);

        static void SetText(Button b, string text) => b.GetComponentInChildren<TextMeshProUGUI>().text = text;

        void Rebind(Row row, string device)
        {
            if (model == null || walkModel == null || pending != null) return;
            InputAction a = MapFor(row).FindAction(row.Action);
            int index = DrivingControls.BindingIndex(a, device, device == "<Keyboard>" ? row.Part : null);
            if (index < 0) return;
            status.text = $"{row.Label}: press a {(device == "<Keyboard>" ? "key" : "controller button")}… (Esc cancels)";
            SetText(device == "<Keyboard>" ? row.Keyboard : row.Pad, "…");
            pending = a.PerformInteractiveRebinding(index)
                .WithControlsHavingToMatchPath(device)
                .WithCancelingThrough("<Keyboard>/escape")
                .WithControlsExcluding("<Mouse>")
                .OnMatchWaitForAnother(0.1f)
                .OnComplete(op => Finish(row, true))
                .OnCancel(op => Finish(row, false))
                .Start();
        }

        void Finish(Row row, bool changed)
        {
            pending?.Dispose();
            pending = null;
            if (changed)
            {
                DrivingPreferences p = DrivingPreferences.Current;
                if (row.Walking) p.WalkingBindingOverrides = walkModel.SaveOverrides();
                else p.BindingOverrides = model.SaveOverrides();
                p.Save();
                status.text = $"{row.Label} changed.";
            }
            else status.text = "Cancelled — nothing changed.";
            Refresh();
        }

        void Cancel()
        {
            if (pending == null) return;
            pending.Cancel();
            pending?.Dispose();
            pending = null;
        }

        void RestoreDefaults()
        {
            Cancel();
            DrivingPreferences p = DrivingPreferences.Current;
            if (walking)
            {
                walkModel.Map.RemoveAllBindingOverrides();
                p.WalkingBindingOverrides = "";
            }
            else
            {
                model.Map.RemoveAllBindingOverrides();
                p.BindingOverrides = "";
            }
            p.Save();
            status.text = walking ? "Default walking bindings restored." : "Default driving bindings restored.";
            Refresh();
        }
    }
}
