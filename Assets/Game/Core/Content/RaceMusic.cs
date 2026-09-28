using System.Linq;

namespace NightSignal.Core.Content
{
    /// <summary>
    /// Which music cue plays for a race (spec §16, Addendum 01 §11; contexts in docs/AUDIO.md): the four lieutenants,
    /// the penultimate trial and the two finals have their own encounter themes, chosen only by the campaign STAGE (a
    /// course lookup can never select a final theme); Team Trials have theirs by kind; every other race plays its
    /// course region's arrangement; the training loop plays the tutorial bed. Results play the win or loss variant.
    /// </summary>
    public static class RaceMusic
    {
        public const string ResultsWin = "MUS_RESULTS_WIN", ResultsLoss = "MUS_RESULTS_LOSS";

        static readonly string[] RegionCues =
        {
            "MUS_RACE_MIZUHANA", "MUS_RACE_KASUMI", "MUS_RACE_KUROGAWA", "MUS_RACE_AKEBONO", "MUS_RACE_HOSHIMI", "MUS_RACE_TSUKISHIRO",
        };

        /// <param name="kind">campaign | freeplay | trial | tutorial</param>
        /// <param name="trialKind">mean | best | drift for a Team Trial (null when unknown).</param>
        /// <param name="driftRanked">Drift Attack or a drift trial (finishers ranked by drift score).</param>
        public static string CueFor(ContentCatalogue catalogue, string courseId, string kind, string stageId, bool hard, string trialKind = null, bool driftRanked = false)
        {
            if (kind == "tutorial") return "MUS_TUTORIAL";
            if (kind == "trial")
                return trialKind == "drift" || driftRanked ? "MUS_TT_DRIFT" : trialKind == "best" ? "MUS_TT_BEST" : "MUS_TT_MEAN";
            StageDef stage = kind == "campaign" && stageId != null ? catalogue?.Stages?.FirstOrDefault(s => s.Id == stageId) : null;
            if (stage != null)
            {
                switch (stage.Type)
                {
                    case "finale": return hard ? "MUS_FINAL_SHIORI" : "MUS_FINAL_REINA";
                    case "penultimate": return "MUS_PENULTIMATE";
                    case "lieutenant":
                        switch (stage.Number)
                        {
                            case 7: return "MUS_LT_DAIGO";
                            case 14: return "MUS_LT_EMI";
                            case 21: return "MUS_LT_JUN";
                            case 28: return "MUS_LT_MAKO";
                        }
                        break;
                }
            }
            return RegionCue(catalogue, courseId);
        }

        /// <summary>The course region's race arrangement; venues outside the six regions borrow one by a stable hash.</summary>
        public static string RegionCue(ContentCatalogue catalogue, string courseId)
        {
            CourseDef course = null;
            catalogue?.TryCourse(courseId, out course);
            switch (course?.Region)
            {
                case "mizuhana": return RegionCues[0];
                case "kasumi": return RegionCues[1];
                case "kurogawa": return RegionCues[2];
                case "akebono": return RegionCues[3];
                case "hoshimi": return RegionCues[4];
                case "tsukishiro": return RegionCues[5];
                case "training": return "MUS_TUTORIAL";
            }
            uint h = 2166136261;
            foreach (char c in courseId ?? "") h = (h ^ c) * 16777619;
            return RegionCues[h % (uint)RegionCues.Length];
        }
    }
}
