using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Controller pen evidence (<c>-nsCanvasPadTour</c>, one window, run with <c>-nsPrefsFolder</c>): a Local profile opens
        /// the Canvas; a virtual controller moves the pen with the right stick and draws a stroke by holding the right
        /// trigger; the mark must reach the sheet through the same operations as a mouse stroke, and moving the mouse hands
        /// the pointer back. Automation (simulated device), not a human.
        /// </summary>
        IEnumerator CanvasPadTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "canvas-pad"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.CanvasPad] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { Fail("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }
            IEnumerator Hold(Gamepad pad, Vector2 stick, float trigger, float seconds)
            {
                InputSystem.QueueStateEvent(pad, new GamepadState { rightStick = stick, rightTrigger = trigger });
                yield return new WaitForSeconds(seconds);
            }

            yield return new WaitForSeconds(3f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName").GetComponent<TMP_InputField>().text = "Pen Tester";
            Click("Create");
            yield return new WaitForSeconds(1.2f);
            Click("WhileWeWait");
            yield return new WaitForSeconds(1.2f);
            Click("Toy-Canvas");
            yield return new WaitForSeconds(2.5f);
            int marksBefore = ConvoyCanvas.MyObjects, pointsBefore = ConvoyCanvas.MyStrokePoints;

            Gamepad pad = InputSystem.AddDevice<Gamepad>("CanvasTourPad");
            GameObject focus = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            yield return Hold(pad, new Vector2(-0.7f, 0.35f), 0f, 0.7f);    // move the pen up-left from the centre
            yield return Hold(pad, Vector2.zero, 0f, 0.3f);
            Note($"pen active {ConvoyCanvas.ControllerPen} at {ConvoyCanvas.ControllerPenPosition}");
            if (!ConvoyCanvas.ControllerPen) Fail("the right stick did not take the pointer");
            // Draw: hold RT and sweep right, then down-right, then release.
            yield return Hold(pad, Vector2.zero, 1f, 0.2f);
            yield return Hold(pad, new Vector2(0.9f, 0f), 1f, 0.8f);
            yield return Hold(pad, new Vector2(0.6f, -0.6f), 1f, 0.6f);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "01-controller-pen-drawing.png"));
            yield return null;
            yield return Hold(pad, Vector2.zero, 0f, 1.5f);
            GameObject focusAfter = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            Note($"menu focus before the pen {(focus != null ? focus.transform.parent?.name + "/" + focus.name : "none")}, after {(focusAfter != null ? focusAfter.transform.parent?.name + "/" + focusAfter.name : "none")}");
            if (focus != focusAfter) Fail("moving the pen moved the menu focus");
            int marks = ConvoyCanvas.MyObjects - marksBefore, points = ConvoyCanvas.MyStrokePoints - pointsBefore;
            Note($"controller stroke: {marks} new mark(s), {points} stroke points");
            if (marks != 1) Fail($"{marks} marks from one controller stroke");
            if (points < 8) Fail($"the controller stroke carried only {points} points");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "02-controller-stroke-on-sheet.png"));
            yield return null;

            // Moving the mouse hands the pointer back.
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                InputSystem.QueueDeltaStateEvent(mouse.delta, new Vector2(40f, 10f));
                yield return null;
                yield return null;
                Note($"after a mouse move the controller pen is {(ConvoyCanvas.ControllerPen ? "still active" : "handed back")}");
                if (ConvoyCanvas.ControllerPen) Fail("moving the mouse did not take the pointer back");
            }
            InputSystem.RemoveDevice(pad);
            Click("Back");
            yield return new WaitForSeconds(1f);
            Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}
