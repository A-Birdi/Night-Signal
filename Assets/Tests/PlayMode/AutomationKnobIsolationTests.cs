using NightSignal.Race;
using NUnit.Framework;

namespace NightSignal.Tests
{
    /// <summary>
    /// Race tests must not depend on what ran before them (V-152). The editor enters Play Mode without a domain reload (Enter
    /// Play Mode Options), so a static automation knob that a tour or test set — and did not put back, e.g. after a failed
    /// assertion — used to carry into every later race of the editor session. The first test here leaves knobs set on
    /// purpose; the fixture's name sorts it before the race tests (FullGridContactTests), so a run of both shows whether
    /// the leak reaches them.
    /// </summary>
    [ResetAutomationStatics]
    public sealed class AutomationKnobIsolationTests
    {
        [Test, Order(1)]
        public void A_ATestLeavesKnobsSet()
        {
            OfflineRaceSession.AutopilotHoldSeconds = 5f; // the autopilot would hold its brakes for five seconds after GO
            OfflineRaceSession.ZoneSlideSlipDeg = 99f;
            Front.FrontEndApp.LessonAutopilot = true;
            Assert.Pass("left set on purpose");
        }

        [Test, Order(2)]
        public void B_TheNextTestStartsFromTheDefaults()
        {
            Assert.AreEqual(0f, OfflineRaceSession.AutopilotHoldSeconds, "AutopilotHoldSeconds");
            Assert.AreEqual(28f, OfflineRaceSession.ZoneSlideSlipDeg, "ZoneSlideSlipDeg");
            Assert.IsFalse(Front.FrontEndApp.LessonAutopilot, "LessonAutopilot");
        }

        [Test]
        public void EveryPlayModeFixture_ResetsTheStaticsBeforeEachTest()
        {
            // The Unity Test Framework only applies test actions found on the method or its fixture classes, so a new fixture
            // without the attribute would silently inherit whatever the test before it left set.
            var missing = new System.Collections.Generic.List<string>();
            foreach (System.Type t in typeof(AutomationKnobIsolationTests).Assembly.GetTypes())
            {
                bool hasTests = false;
                foreach (System.Reflection.MethodInfo m in t.GetMethods())
                    if (m.IsDefined(typeof(TestAttribute), true) || m.IsDefined(typeof(UnityEngine.TestTools.UnityTestAttribute), true)) hasTests = true;
                if (hasTests && !t.IsDefined(typeof(ResetAutomationStaticsAttribute), true)) missing.Add(t.Name);
            }
            Assert.IsEmpty(missing, "PlayMode fixtures without [ResetAutomationStatics]");
        }

        [Test]
        public void Reset_RestoresEveryKnobToItsDefault()
        {
            OfflineRaceSession.AutopilotAimsChallengeGates = OfflineRaceSession.AutopilotDrivesChallengeZones = true;
            OfflineRaceSession.ZoneSlideSlipDeg = OfflineRaceSession.ZoneSlideRateSteer = OfflineRaceSession.ZoneSlideRateThrottle = 7f;
            OfflineRaceSession.ZoneSlidePathFollow = OfflineRaceSession.ZoneSlidePathThrottle = OfflineRaceSession.ZoneSlideEntrySpeed = 7f;
            OfflineRaceSession.ZoneSlideTransitionGrace = OfflineRaceSession.ZoneSlideClipInset = 7f;
            OfflineRaceSession.AutopilotHoldSeconds = OfflineRaceSession.AutopilotFollowSeconds = 7f;
            OfflineRaceSession.AutopilotLaneHoldMetres = OfflineRaceSession.AutopilotApexHoldMetres = 7f;
            OfflineRaceSession.AutopilotAttacksMarkedZones = OfflineRaceSession.AutopilotHoldsMarkedLanes = true;
            OfflineRaceSession.AutopilotSlidesZonesOf = "CH23";
            OfflineRaceSession.AutopilotShiftAtGates = new[] { "G1" };
            Front.FrontEndApp.LessonAutopilot = true;
            Race.AutomationStatics.Reset();
            Assert.IsFalse(OfflineRaceSession.AutopilotAimsChallengeGates || OfflineRaceSession.AutopilotDrivesChallengeZones);
            Assert.AreEqual(new[] { 28f, 0.15f, 0.02f, 0.6f, 0f, 25f, 1f, 0.8f },
                new[] { OfflineRaceSession.ZoneSlideSlipDeg, OfflineRaceSession.ZoneSlideRateSteer, OfflineRaceSession.ZoneSlideRateThrottle,
                    OfflineRaceSession.ZoneSlidePathFollow, OfflineRaceSession.ZoneSlidePathThrottle, OfflineRaceSession.ZoneSlideEntrySpeed,
                    OfflineRaceSession.ZoneSlideTransitionGrace, OfflineRaceSession.ZoneSlideClipInset });
            Assert.AreEqual(new[] { 0f, 0f, 0f, 0f }, new[] { OfflineRaceSession.AutopilotHoldSeconds, OfflineRaceSession.AutopilotFollowSeconds,
                OfflineRaceSession.AutopilotLaneHoldMetres, OfflineRaceSession.AutopilotApexHoldMetres });
            Assert.IsFalse(OfflineRaceSession.AutopilotAttacksMarkedZones || OfflineRaceSession.AutopilotHoldsMarkedLanes);
            Assert.IsNull(OfflineRaceSession.AutopilotSlidesZonesOf);
            Assert.IsNull(OfflineRaceSession.AutopilotShiftAtGates);
            Assert.IsFalse(Front.FrontEndApp.LessonAutopilot);
        }
    }
}
