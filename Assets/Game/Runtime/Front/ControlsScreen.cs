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
    /// </summary>
    public sealed class ControlsScreen : UIScreen
    {
        public override string ScreenName => "Controls";

        sealed class Row
        {
            public string Label, Action, Part;
            public Button Keyboard, Pad;
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

        DrivingControls model;
        readonly List<Row> rows = new List<Row>();
        TextMeshProUGUI status, conflicts;
        InputActionRebindingExtensions.RebindingOperation pending;

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.62f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.9f));
            RectTransform col = UIFactory.Column("Bindings", panel.transform, new Vector2(0, 0.03f), new Vector2(1, 0.93f), new Vector2(64, 0), new Vector2(-32, 0), 4f);
            UIFactory.Row("Heading", col, "CONTROLS", SignalTheme.Heading, SignalTheme.Label, 980, 0, true);
            UIFactory.Row("Hint", col, "Select a binding, then press the new key or button. Esc cancels. Arrow keys and the left stick also steer and drive.",
                SignalTheme.Small, SignalTheme.LabelDim, 980, 48);
            RectTransform heads = UIFactory.Rect("Columns", col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            heads.sizeDelta = new Vector2(980, 30);
            foreach ((string text, float x) in new[] { ("KEYBOARD", 0.36f), ("CONTROLLER", 0.68f) })
            {
                TextMeshProUGUI h = UIFactory.Label(text, heads, text, SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft, true);
                h.rectTransform.anchorMin = new Vector2(x, 0);
                h.rectTransform.anchorMax = new Vector2(x + 0.3f, 1);
                h.rectTransform.offsetMin = h.rectTransform.offsetMax = Vector2.zero;
            }
            foreach (var l in Layout)
            {
                RectTransform r = UIFactory.Rect(l.Label, col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
                r.sizeDelta = new Vector2(980, 50);
                TextMeshProUGUI name = UIFactory.Label("Label", r, l.Label, SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.MidlineLeft, true);
                name.rectTransform.anchorMin = new Vector2(0, 0);
                name.rectTransform.anchorMax = new Vector2(0.36f, 1);
                name.rectTransform.offsetMin = Vector2.zero;
                name.rectTransform.offsetMax = Vector2.zero;
                var row = new Row { Label = l.Label, Action = l.Action, Part = l.Part };
                row.Keyboard = UIFactory.Button($"{l.Action}{l.Part}/Keyboard", r, "", () => Rebind(row, "<Keyboard>"), 300, 46);
                Place(row.Keyboard, 0.36f);
                if (l.Pad)
                {
                    row.Pad = UIFactory.Button($"{l.Action}/Controller", r, "", () => Rebind(row, "<Gamepad>"), 300, 46);
                    Place(row.Pad, 0.68f);
                }
                rows.Add(row);
            }
            conflicts = UIFactory.Row("Conflicts", col, "", SignalTheme.Small, SignalTheme.Caution, 980, 56);
            status = UIFactory.Row("Status", col, "", SignalTheme.Small, SignalTheme.Label, 980, 30);
            RectTransform buttons = UIFactory.Rect("Buttons", col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            buttons.sizeDelta = new Vector2(980, 56);
            Button restore = UIFactory.Button("RestoreDefaults", buttons, "Restore defaults", RestoreDefaults, 300, 52);
            Place(restore, 0f);
            Button back = UIFactory.Button("Back", buttons, "Back", () => App.Router.Back(), 300, 52);
            Place(back, 0.36f);
        }

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
            status.text = "";
            Refresh();
        }

        public override void OnHide()
        {
            Cancel();
            model?.Dispose();
            model = null;
        }

        public override bool OnBack()
        {
            if (pending == null) return false;
            Cancel(); // Esc while waiting for a key cancels the capture, not the screen
            return true;
        }

        void Refresh()
        {
            if (model == null) return;
            foreach (Row r in rows)
            {
                InputAction a = model.Map.FindAction(r.Action);
                SetText(r.Keyboard, Display(a, DrivingControls.BindingIndex(a, "<Keyboard>", r.Part)));
                if (r.Pad != null) SetText(r.Pad, Display(a, DrivingControls.BindingIndex(a, "<Gamepad>", null)));
            }
            List<string> shared = DrivingControls.Conflicts(model.Map);
            conflicts.text = shared.Count == 0 ? "" : "Shared bindings (both actions respond): " + string.Join("; ", shared);
        }

        static string Display(InputAction a, int index) =>
            index < 0 ? "—" : a.GetBindingDisplayString(index, InputBinding.DisplayStringOptions.DontIncludeInteractions);

        static void SetText(Button b, string text) => b.GetComponentInChildren<TextMeshProUGUI>().text = text;

        void Rebind(Row row, string device)
        {
            if (model == null || pending != null) return;
            InputAction a = model.Map.FindAction(row.Action);
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
                p.BindingOverrides = model.SaveOverrides();
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
            model.Map.RemoveAllBindingOverrides();
            DrivingPreferences p = DrivingPreferences.Current;
            p.BindingOverrides = "";
            p.Save();
            status.text = "Default bindings restored.";
            Refresh();
        }
    }
}
