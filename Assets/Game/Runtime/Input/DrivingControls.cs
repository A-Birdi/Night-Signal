using System.Collections.Generic;
using NightSignal.Vehicle;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace NightSignal.InputBindings
{
    /// <summary>
    /// Driving input map (spec §6): arrows/WASD steer-throttle-brake, Space handbrake, Q/E down/upshift, C camera,
    /// hold R reset, Esc pause; gamepad sticks/triggers, South handbrake, bumpers shift, View camera, Start menu.
    /// Bindings are remappable (Settings → Controls) through the Input System's override JSON kept in the local driving
    /// preferences, loaded by every session. Focus loss — or typing in a text field — releases every held control and
    /// ignores the view, look-back, reset and pause buttons (Addendum 03 §2, §7.1).
    /// </summary>
    public sealed class DrivingControls : System.IDisposable
    {
        public readonly InputActionMap Map;
        readonly InputAction steer, throttle, brake, handbrake, shiftUp, shiftDown, camera, reset, pause, lookBack;

        public DrivingControls()
        {
            Map = new InputActionMap("Driving");
            steer = Map.AddAction("Steer", InputActionType.Value, expectedControlLayout: "Axis");
            steer.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/a").With("Positive", "<Keyboard>/d");
            steer.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/leftArrow").With("Positive", "<Keyboard>/rightArrow");
            steer.AddBinding("<Gamepad>/leftStick/x").WithProcessor("axisDeadzone(min=0.08,max=0.98)");

            throttle = Map.AddAction("Throttle", InputActionType.Value, expectedControlLayout: "Axis");
            throttle.AddBinding("<Keyboard>/w");
            throttle.AddBinding("<Keyboard>/upArrow");
            throttle.AddBinding("<Gamepad>/rightTrigger");

            brake = Map.AddAction("Brake", InputActionType.Value, expectedControlLayout: "Axis");
            brake.AddBinding("<Keyboard>/s");
            brake.AddBinding("<Keyboard>/downArrow");
            brake.AddBinding("<Gamepad>/leftTrigger");

            handbrake = Map.AddAction("Handbrake", InputActionType.Button);
            handbrake.AddBinding("<Keyboard>/space");
            handbrake.AddBinding("<Gamepad>/buttonSouth");

            shiftUp = Map.AddAction("ShiftUp", InputActionType.Button);
            shiftUp.AddBinding("<Keyboard>/e");
            shiftUp.AddBinding("<Gamepad>/rightShoulder");
            shiftDown = Map.AddAction("ShiftDown", InputActionType.Button);
            shiftDown.AddBinding("<Keyboard>/q");
            shiftDown.AddBinding("<Gamepad>/leftShoulder");

            camera = Map.AddAction("Camera", InputActionType.Button);
            camera.AddBinding("<Keyboard>/c");
            camera.AddBinding("<Gamepad>/select");
            reset = Map.AddAction("Reset", InputActionType.Button);
            reset.AddBinding("<Keyboard>/r");
            reset.AddBinding("<Gamepad>/buttonNorth");
            pause = Map.AddAction("Pause", InputActionType.Button);
            pause.AddBinding("<Keyboard>/escape");
            pause.AddBinding("<Gamepad>/start");
            lookBack = Map.AddAction("LookBack", InputActionType.Button);
            lookBack.AddBinding("<Keyboard>/b");
            lookBack.AddBinding("<Gamepad>/rightStickPress");

            string overrides = UI.DrivingPreferences.Current.BindingOverrides;
            if (!string.IsNullOrEmpty(overrides))
            {
                try { LoadOverrides(overrides); }
                catch (System.Exception e) { Debug.LogWarning($"[NightSignal.Input] stored bindings unreadable ({e.GetType().Name}); using the defaults"); }
            }
        }

        /// <summary>A text field has keyboard focus (chat, names, a shared canvas caption): driving buttons must not fire.</summary>
        public static bool TypingFocused()
        {
            EventSystem es = EventSystem.current;
            GameObject selected = es != null ? es.currentSelectedGameObject : null;
            if (selected == null) return false;
            TMPro.TMP_InputField tmp = selected.GetComponent<TMPro.TMP_InputField>();
            if (tmp != null && tmp.isFocused) return true;
            UnityEngine.UI.InputField legacy = selected.GetComponent<UnityEngine.UI.InputField>();
            return legacy != null && legacy.isFocused;
        }

        /// <summary>
        /// The binding a Controls row edits: the first binding on <paramref name="device"/> ("&lt;Keyboard&gt;" or
        /// "&lt;Gamepad&gt;"); for a composite part (steer "negative"/"positive") that part of the first composite on the device.
        /// </summary>
        public static int BindingIndex(InputAction action, string device, string part)
        {
            if (action == null) return -1;
            for (int i = 0; i < action.bindings.Count; i++)
            {
                InputBinding b = action.bindings[i];
                if (b.isComposite) continue;
                if (part != null ? !b.isPartOfComposite || !string.Equals(b.name, part, System.StringComparison.OrdinalIgnoreCase) : b.isPartOfComposite) continue;
                if (b.path != null && b.path.StartsWith(device)) return i;
            }
            return -1;
        }

        /// <summary>Human-readable list of controls bound to more than one action (warned, not refused).</summary>
        public static List<string> Conflicts(InputActionMap map)
        {
            var byPath = new Dictionary<string, List<string>>();
            foreach (InputAction a in map.actions)
                foreach (InputBinding b in a.bindings)
                {
                    if (b.isComposite || string.IsNullOrEmpty(b.effectivePath)) continue;
                    string path = b.effectivePath.ToLowerInvariant();
                    if (!byPath.TryGetValue(path, out List<string> names)) byPath[path] = names = new List<string>();
                    if (!names.Contains(a.name)) names.Add(a.name);
                }
            var result = new List<string>();
            foreach (KeyValuePair<string, List<string>> kv in byPath)
                if (kv.Value.Count > 1) result.Add($"{InputControlPath.ToHumanReadableString(kv.Key, InputControlPath.HumanReadableStringOptions.OmitDevice)} → {string.Join(" + ", kv.Value)}");
            return result;
        }

        public void Enable() => Map.Enable();
        public void Disable() => Map.Disable();
        public void Dispose() => Map.Dispose();

        public bool CameraPressed => camera.WasPressedThisFrame() && !TypingFocused();
        public bool PausePressed => pause.WasPressedThisFrame() && !TypingFocused();
        public bool LookBackHeld => lookBack.IsPressed() && !TypingFocused();
        /// <summary>The reset button is down now (for the hold-progress display; the simulation times the real hold).</summary>
        public bool ResetHeld => Application.isFocused && reset.IsPressed() && !TypingFocused();

        [System.Serializable]
        sealed class StoredOverride { public string Action; public int Index; public string Path; }
        [System.Serializable]
        sealed class StoredOverrides { public int Schema = 1; public List<StoredOverride> Items = new List<StoredOverride>(); }

        /// <summary>
        /// The remapped bindings as JSON keyed by action name and binding index. (The map is built in code, so its binding
        /// ids are new every session and the Input System's own id-keyed override JSON would not reload.)
        /// </summary>
        public string SaveOverrides()
        {
            var stored = new StoredOverrides();
            foreach (InputAction a in Map.actions)
                for (int i = 0; i < a.bindings.Count; i++)
                    if (!string.IsNullOrEmpty(a.bindings[i].overridePath))
                        stored.Items.Add(new StoredOverride { Action = a.name, Index = i, Path = a.bindings[i].overridePath });
            return stored.Items.Count == 0 ? "" : JsonUtility.ToJson(stored);
        }

        /// <summary>Applies stored remaps; unknown actions or indices are skipped. Throws on unreadable JSON (callers fall back).</summary>
        public void LoadOverrides(string json)
        {
            StoredOverrides stored = JsonUtility.FromJson<StoredOverrides>(json);
            if (stored?.Items == null) return;
            foreach (StoredOverride o in stored.Items)
            {
                InputAction a = Map.FindAction(o.Action);
                if (a == null || o.Index < 0 || o.Index >= a.bindings.Count || string.IsNullOrEmpty(o.Path) || a.bindings[o.Index].isComposite) continue;
                a.ApplyBindingOverride(o.Index, o.Path);
            }
        }

        /// <summary>
        /// Samples the current tick's input. Shift edges are latched by the caller between ticks; when the
        /// application has no focus every control reads as released (no stuck steering/throttle).
        /// </summary>
        public DriverInput Sample(bool shiftUpEdge, bool shiftDownEdge)
        {
            if (!Application.isFocused || TypingFocused()) return DriverInput.Neutral;
            var buttons = InputButtons.None;
            if (handbrake.IsPressed()) buttons |= InputButtons.Handbrake;
            if (shiftUpEdge) buttons |= InputButtons.ShiftUp;
            if (shiftDownEdge) buttons |= InputButtons.ShiftDown;
            if (reset.IsPressed()) buttons |= InputButtons.ResetHeld;
            return DriverInput.Quantize(steer.ReadValue<float>(), throttle.ReadValue<float>(), brake.ReadValue<float>(), buttons);
        }

        /// <summary>The current keyboard / controller binding of an action for prompts (follows remapping), e.g. "R / Y".</summary>
        public string BindingLabel(string actionName)
        {
            InputAction a = Map.FindAction(actionName);
            if (a == null) return actionName;
            int k = BindingIndex(a, "<Keyboard>", null), g = BindingIndex(a, "<Gamepad>", null);
            string keyPath = k >= 0 ? a.bindings[k].effectivePath : null, padPath = g >= 0 ? a.bindings[g].effectivePath : null;
            // Cached per effective binding: the Input System's display-string lookup kept native memory on every call, and
            // the HUD asks every frame (the soak measured ~0.4 KB per frame, ~10-17 MB per race). A remap changes the
            // effective path, so the prompt still follows it.
            if (labels.TryGetValue(actionName, out (string key, string pad, string label) cached) && cached.key == keyPath && cached.pad == padPath)
                return cached.label;
            string key = k >= 0 ? a.GetBindingDisplayString(k) : "";
            string pad = g >= 0 ? a.GetBindingDisplayString(g) : "";
            string label = key.Length > 0 && pad.Length > 0 ? key + " / " + pad : key + pad;
            labels[actionName] = (keyPath, padPath, label);
            return label;
        }

        readonly System.Collections.Generic.Dictionary<string, (string key, string pad, string label)> labels =
            new System.Collections.Generic.Dictionary<string, (string key, string pad, string label)>();

        public bool ShiftUpPressedThisFrame => shiftUp.WasPressedThisFrame();
        public bool ShiftDownPressedThisFrame => shiftDown.WasPressedThisFrame();
    }
}
