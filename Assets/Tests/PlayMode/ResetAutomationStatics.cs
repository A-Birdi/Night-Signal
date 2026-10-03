using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace NightSignal.Tests
{
    /// <summary>
    /// Before every PlayMode test: the game's automation and session statics back to their defaults
    /// (<see cref="Race.AutomationStatics"/>). One Play Mode session runs every test of a run without a domain reload, so a
    /// knob that one test set — or failed before restoring — otherwise reached the races of the tests after it (V-152).
    /// On every PlayMode fixture: the Unity Test Framework gathers test actions from the method and its fixture classes, not
    /// from the assembly (BeforeAfterTestCommandBase.GetTestActions), so an assembly-level attribute never ran.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class, Inherited = true)]
    public sealed class ResetAutomationStaticsAttribute : NUnitAttribute, ITestAction
    {
        public ActionTargets Targets => ActionTargets.Test;

        public void BeforeTest(ITest test) => Race.AutomationStatics.Reset();

        public void AfterTest(ITest test) { }
    }
}
