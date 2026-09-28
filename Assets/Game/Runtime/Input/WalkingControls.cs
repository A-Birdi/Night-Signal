using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NightSignal.InputBindings
{
    /// <summary>
    /// The meet's walking controls (spec §12: walk/jog, turn, idle, interact, inspect, emote wheel, recenter camera, plus
    /// photo mode, quick chat and rescue-to-car). Keyboard + mouse and controller, remappable and stored with the driving
    /// preferences; like driving, every control reads as released while a text field has focus or the window is not focused,
    /// so menus and focus loss never leave the avatar walking.
    /// </summary>
    public sealed class WalkingControls : System.IDisposable
    {
        public readonly InputActionMap Map;
        readonly InputAction move, look, jog, interact, emoteWheel, recenter, rescue, photo, quickChat, menu, zoom;
        readonly InputAction[] emoteKeys = new InputAction[12];

        public WalkingControls()
        {
            Map = new InputActionMap("Walking");
            move = Map.AddAction("Move", InputActionType.Value, expectedControlLayout: "Vector2");
            move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow").With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
            move.AddBinding("<Gamepad>/leftStick").WithProcessor("stickDeadzone(min=0.15,max=0.95)");
            look = Map.AddAction("Look", InputActionType.Value, expectedControlLayout: "Vector2");
            look.AddBinding("<Mouse>/delta").WithProcessor("scaleVector2(x=0.12,y=0.12)");
            look.AddBinding("<Gamepad>/rightStick").WithProcessor("stickDeadzone(min=0.12,max=0.95)").WithProcessor("scaleVector2(x=2.6,y=2.0)");
            jog = Map.AddAction("Jog", InputActionType.Button);
            jog.AddBinding("<Keyboard>/leftShift");
            jog.AddBinding("<Gamepad>/leftStickPress");
            interact = Map.AddAction("Interact", InputActionType.Button);
            interact.AddBinding("<Keyboard>/e");
            interact.AddBinding("<Gamepad>/buttonSouth");
            emoteWheel = Map.AddAction("EmoteWheel", InputActionType.Button);
            emoteWheel.AddBinding("<Keyboard>/t");
            emoteWheel.AddBinding("<Gamepad>/rightShoulder");
            recenter = Map.AddAction("Recenter", InputActionType.Button);
            recenter.AddBinding("<Keyboard>/c");
            recenter.AddBinding("<Gamepad>/rightStickPress");
            rescue = Map.AddAction("Rescue", InputActionType.Button);
            rescue.AddBinding("<Keyboard>/r");
            rescue.AddBinding("<Gamepad>/buttonNorth");
            photo = Map.AddAction("Photo", InputActionType.Button);
            photo.AddBinding("<Keyboard>/p");
            photo.AddBinding("<Gamepad>/dpad/up");
            quickChat = Map.AddAction("QuickChat", InputActionType.Button);
            quickChat.AddBinding("<Keyboard>/v");
            quickChat.AddBinding("<Gamepad>/leftShoulder");
            menu = Map.AddAction("Menu", InputActionType.Button);
            menu.AddBinding("<Keyboard>/escape");
            menu.AddBinding("<Gamepad>/start");
            zoom = Map.AddAction("Zoom", InputActionType.Value, expectedControlLayout: "Axis");
            zoom.AddBinding("<Mouse>/scroll/y").WithProcessor("scale(factor=0.004)");
            zoom.AddCompositeBinding("1DAxis").With("Negative", "<Gamepad>/dpad/down").With("Positive", "<Gamepad>/dpad/right");
            // Direct emote keys (1–9, 0, -, =) in wheel order, so the wheel is never the only way.
            string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "minus", "equals" };
            for (int i = 0; i < 12; i++)
            {
                emoteKeys[i] = Map.AddAction($"Emote{i + 1}", InputActionType.Button);
                emoteKeys[i].AddBinding($"<Keyboard>/{keys[i]}");
            }
            string overrides = UI.DrivingPreferences.Current.WalkingBindingOverrides;
            if (!string.IsNullOrEmpty(overrides))
            {
                try { LoadOverrides(overrides); }
                catch (System.Exception e) { Debug.LogWarning($"[NightSignal.Input] stored walking bindings unreadable ({e.GetType().Name}); using the defaults"); }
            }
        }

        public void Enable() => Map.Enable();
        public void Disable() => Map.Disable();
        public void Dispose() => Map.Dispose();

        static bool Live => Application.isFocused && !DrivingControls.TypingFocused();

        /// <summary>Movement intent (x right, y forward), magnitude ≤ 1; zero without focus.</summary>
        public Vector2 Move => Live ? Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f) : Vector2.zero;
        /// <summary>Camera look this frame (degrees-ish; mouse delta is per frame, the stick is per second — scaled by the caller).</summary>
        public Vector2 Look => Live ? look.ReadValue<Vector2>() : Vector2.zero;
        /// <summary>True when the last look input came from a stick (rate) rather than a mouse (delta).</summary>
        public bool LookIsRate => look.activeControl != null && look.activeControl.device is Gamepad;
        public float Zoom => Live ? zoom.ReadValue<float>() : 0f;
        public bool JogHeld => Live && jog.IsPressed();
        public bool InteractPressed => Live && interact.WasPressedThisFrame();
        public bool EmoteWheelHeld => Live && emoteWheel.IsPressed();
        public bool EmoteWheelReleased => emoteWheel.WasReleasedThisFrame();
        public bool RecenterPressed => Live && recenter.WasPressedThisFrame();
        public bool RescueHeld => Live && rescue.IsPressed();
        public bool PhotoPressed => Live && photo.WasPressedThisFrame();
        public bool QuickChatPressed => Live && quickChat.WasPressedThisFrame();
        public bool MenuPressed => Live && menu.WasPressedThisFrame();

        /// <summary>A direct emote key pressed this frame (wheel index), or −1.</summary>
        public int EmoteKeyPressed
        {
            get
            {
                if (!Live) return -1;
                for (int i = 0; i < emoteKeys.Length; i++) if (emoteKeys[i].WasPressedThisFrame()) return i;
                return -1;
            }
        }

        [System.Serializable]
        sealed class StoredOverride { public string Action; public int Index; public string Path; }
        [System.Serializable]
        sealed class StoredOverrides { public int Schema = 1; public List<StoredOverride> Items = new List<StoredOverride>(); }

        public string SaveOverrides()
        {
            var stored = new StoredOverrides();
            foreach (InputAction a in Map.actions)
                for (int i = 0; i < a.bindings.Count; i++)
                    if (!string.IsNullOrEmpty(a.bindings[i].overridePath))
                        stored.Items.Add(new StoredOverride { Action = a.name, Index = i, Path = a.bindings[i].overridePath });
            return stored.Items.Count == 0 ? "" : JsonUtility.ToJson(stored);
        }

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

        readonly Dictionary<string, string> labels = new Dictionary<string, string>();

        /// <summary>Prompt label for an action's keyboard / controller binding (follows remapping), e.g. "E / A".</summary>
        public string BindingLabel(string actionName)
        {
            InputAction a = Map.FindAction(actionName);
            if (a == null) return actionName;
            int k = DrivingControls.BindingIndex(a, "<Keyboard>", null), g = DrivingControls.BindingIndex(a, "<Gamepad>", null);
            string key = (k >= 0 ? a.bindings[k].effectivePath : "") + "|" + (g >= 0 ? a.bindings[g].effectivePath : "");
            if (labels.TryGetValue(actionName + key, out string cached)) return cached;
            string kl = k >= 0 ? a.GetBindingDisplayString(k) : "", gl = g >= 0 ? a.GetBindingDisplayString(g) : "";
            string label = kl.Length > 0 && gl.Length > 0 ? kl + " / " + gl : kl + gl;
            labels[actionName + key] = label;
            return label;
        }
    }
}
