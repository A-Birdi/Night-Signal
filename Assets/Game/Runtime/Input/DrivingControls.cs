using NightSignal.Vehicle;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NightSignal.InputBindings
{
    /// <summary>
    /// Driving input map (spec §6): arrows/WASD steer-throttle-brake, Space handbrake, Q/E down/upshift, C camera,
    /// hold R reset, Esc pause; gamepad sticks/triggers, South handbrake, bumpers shift, View camera, Start menu.
    /// Bindings are remappable through the Input System's override JSON. Focus loss releases every held control.
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
        }

        public void Enable() => Map.Enable();
        public void Disable() => Map.Disable();
        public void Dispose() => Map.Dispose();

        public bool CameraPressed => camera.WasPressedThisFrame();
        public bool PausePressed => pause.WasPressedThisFrame();
        public bool LookBackHeld => lookBack.IsPressed();

        public string SaveOverrides() => Map.SaveBindingOverridesAsJson();
        public void LoadOverrides(string json) => Map.LoadBindingOverridesFromJson(json);

        /// <summary>
        /// Samples the current tick's input. Shift edges are latched by the caller between ticks; when the
        /// application has no focus every control reads as released (no stuck steering/throttle).
        /// </summary>
        public DriverInput Sample(bool shiftUpEdge, bool shiftDownEdge)
        {
            if (!Application.isFocused) return DriverInput.Neutral;
            var buttons = InputButtons.None;
            if (handbrake.IsPressed()) buttons |= InputButtons.Handbrake;
            if (shiftUpEdge) buttons |= InputButtons.ShiftUp;
            if (shiftDownEdge) buttons |= InputButtons.ShiftDown;
            if (reset.IsPressed()) buttons |= InputButtons.ResetHeld;
            return DriverInput.Quantize(steer.ReadValue<float>(), throttle.ReadValue<float>(), brake.ReadValue<float>(), buttons);
        }

        public bool ShiftUpPressedThisFrame => shiftUp.WasPressedThisFrame();
        public bool ShiftDownPressedThisFrame => shiftDown.WasPressedThisFrame();
    }
}
