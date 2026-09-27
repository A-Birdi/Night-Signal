using System.IO;
using NightSignal.InputBindings;
using NightSignal.UI;
using NUnit.Framework;
using UnityEngine.InputSystem;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// Remappable driving controls (spec §6, Addendum 03 §2 "remappable Cycle Camera", §7.1 remappable reset): the rows
    /// the Controls screen edits, stored overrides reaching every new session, shared-binding warnings, and restore.
    /// </summary>
    public sealed class ControlsTests
    {
        string folder;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "ns-controls-" + System.Guid.NewGuid().ToString("N"));
            DrivingPreferences.FolderOverride = folder;
            DrivingPreferences.ResetCache();
        }

        [TearDown]
        public void TearDown()
        {
            DrivingPreferences.FolderOverride = null;
            DrivingPreferences.ResetCache();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        [Test]
        public void DefaultsAreTheDocumentedKeys()
        {
            using (var c = new DrivingControls())
            {
                InputAction cam = c.Map.FindAction("Camera");
                Assert.That(cam.bindings[DrivingControls.BindingIndex(cam, "<Keyboard>", null)].effectivePath, Is.EqualTo("<Keyboard>/c"));
                Assert.That(cam.bindings[DrivingControls.BindingIndex(cam, "<Gamepad>", null)].effectivePath, Is.EqualTo("<Gamepad>/select"));
                InputAction steer = c.Map.FindAction("Steer");
                Assert.That(steer.bindings[DrivingControls.BindingIndex(steer, "<Keyboard>", "negative")].effectivePath, Is.EqualTo("<Keyboard>/a"));
                Assert.That(steer.bindings[DrivingControls.BindingIndex(steer, "<Keyboard>", "positive")].effectivePath, Is.EqualTo("<Keyboard>/d"));
                InputAction reset = c.Map.FindAction("Reset");
                Assert.That(reset.bindings[DrivingControls.BindingIndex(reset, "<Keyboard>", null)].effectivePath, Is.EqualTo("<Keyboard>/r"));
                Assert.That(DrivingControls.Conflicts(c.Map), Is.Empty, "the defaults share nothing");
            }
        }

        [Test]
        public void ARemapIsStored_LoadedByTheNextSession_ConflictsWarned_DefaultsRestored()
        {
            using (var c = new DrivingControls())
            {
                InputAction cam = c.Map.FindAction("Camera");
                cam.ApplyBindingOverride(DrivingControls.BindingIndex(cam, "<Keyboard>", null), "<Keyboard>/v");
                InputAction steer = c.Map.FindAction("Steer");
                steer.ApplyBindingOverride(DrivingControls.BindingIndex(steer, "<Keyboard>", "negative"), "<Keyboard>/space"); // also the handbrake
                DrivingPreferences p = DrivingPreferences.Current;
                p.BindingOverrides = c.SaveOverrides();
                Assert.That(p.Save(), Is.True);
            }
            DrivingPreferences.ResetCache(); // a restart
            using (var next = new DrivingControls())
            {
                InputAction cam = next.Map.FindAction("Camera");
                Assert.That(cam.bindings[DrivingControls.BindingIndex(cam, "<Keyboard>", null)].effectivePath, Is.EqualTo("<Keyboard>/v"), "Change View remapped to V in a new session");
                Assert.That(cam.bindings[DrivingControls.BindingIndex(cam, "<Gamepad>", null)].effectivePath, Is.EqualTo("<Gamepad>/select"), "the controller binding is untouched");
                var conflicts = DrivingControls.Conflicts(next.Map);
                Assert.That(conflicts.Count, Is.EqualTo(1));
                StringAssert.Contains("Steer", conflicts[0]);
                StringAssert.Contains("Handbrake", conflicts[0]);

                next.Map.RemoveAllBindingOverrides();
                DrivingPreferences.Current.BindingOverrides = "";
                DrivingPreferences.Current.Save();
            }
            DrivingPreferences.ResetCache();
            using (var restored = new DrivingControls())
            {
                InputAction cam = restored.Map.FindAction("Camera");
                Assert.That(cam.bindings[DrivingControls.BindingIndex(cam, "<Keyboard>", null)].effectivePath, Is.EqualTo("<Keyboard>/c"), "defaults restored");
            }
        }

        [Test]
        public void ThePromptLabelIsCached_AndStillFollowsARemap()
        {
            using (var c = new DrivingControls())
            {
                string before = c.BindingLabel("Reset");
                StringAssert.StartsWith("R", before, "the keyboard key leads the prompt");
                Assert.That(c.BindingLabel("Reset"), Is.SameAs(before), "asked again (every frame): the cached string, no new lookup");
                InputAction reset = c.Map.FindAction("Reset");
                reset.ApplyBindingOverride(DrivingControls.BindingIndex(reset, "<Keyboard>", null), "<Keyboard>/t");
                string after = c.BindingLabel("Reset");
                StringAssert.StartsWith("T", after, "a remap changes the prompt");
                Assert.That(after, Is.Not.EqualTo(before));
            }
        }

        [Test]
        public void CorruptStoredBindingsFallBackToDefaults()
        {
            DrivingPreferences p = DrivingPreferences.Current;
            p.BindingOverrides = "{ this is not binding json";
            p.Save();
            DrivingPreferences.ResetCache();
            using (var c = new DrivingControls())
            {
                InputAction cam = c.Map.FindAction("Camera");
                Assert.That(cam.bindings[DrivingControls.BindingIndex(cam, "<Keyboard>", null)].effectivePath, Is.EqualTo("<Keyboard>/c"));
            }
        }
    }
}
