using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>
    /// The game's mutable automation and session statics, reset in one place (V-152). The editor enters Play Mode without a
    /// domain reload or scene reload (Enter Play Mode Options), so statics are not re-initialised between Play Mode sessions:
    /// an automation knob that a tour or test set and did not restore carried into every later race of the editor session
    /// (a twelve-car race after such a test finished differently). Unity's documented remedy is to reset statics on entering
    /// Play Mode; the PlayMode tests also call this before each test (one Play Mode session runs many tests). In a player it
    /// runs once at start-up, before any scene, when everything is at its default anyway.
    /// </summary>
    public static class AutomationStatics
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset()
        {
            OfflineRaceSession.ResetAutomationKnobs();
            Front.FrontEndApp.LessonAutopilot = false;
            Front.LocalEvents.ResetSession();
        }
    }
}
