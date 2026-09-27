using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Track;

namespace NightSignal.Race
{
    /// <summary>
    /// The surface (grip) and time of day an event races under: a campaign stage side's authored conditions
    /// (stage-conditions.json — Hard sides carry their own damp/wet/night variations), otherwise the course's authored
    /// defaults. The Local race, the dedicated server and the benchmark certification use this one rule, so records,
    /// targets and settlement agree on the conditions.
    /// </summary>
    public static class RaceConditions
    {
        public static string Surface(ContentCatalogue cat, string kind, string stageId, CampaignMode mode, CourseRuntime course)
        {
            StageConditions c = kind == "campaign" && cat != null ? cat.Conditions(stageId, mode) : null;
            return c?.Surface ?? course?.Route?.Surface ?? "dry";
        }

        public static string TimeOfDay(ContentCatalogue cat, string kind, string stageId, CampaignMode mode, CourseRuntime course)
        {
            StageConditions c = kind == "campaign" && cat != null ? cat.Conditions(stageId, mode) : null;
            return c?.TimeOfDay ?? course?.DefaultTimeOfDay ?? "day";
        }
    }
}
