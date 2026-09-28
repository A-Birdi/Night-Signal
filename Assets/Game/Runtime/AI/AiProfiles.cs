using NightSignal.Core.Content;
using UnityEngine;

namespace NightSignal.AI
{
    /// <summary>
    /// Driver parameters for a rival at a given campaign stage. Normal AI escalates by stage (spec §13); each of the
    /// nineteen tendencies biases the shared controller's braking, line and corner-speed choices.
    /// First pass: parameter biases only — the composable tendency behaviours with measured corner telemetry are
    /// tracked separately (REQUIREMENTS R13.1/R13.2).
    /// </summary>
    public static class AiProfiles
    {
        /// <summary>Freeplay opponents without a rival identity ("ai-N"): neutral, mildly varied profiles.</summary>
        public static DriverProfile Generic(int index)
        {
            float t = (index % 6) / 5f;
            return new DriverProfile
            {
                CornerSpeedFactor = Mathf.Lerp(0.83f, 0.88f, t),
                BrakingDecel = Mathf.Lerp(6.5f, 7.1f, 1f - t),
                LineAggression = Mathf.Lerp(0.35f, 0.55f, t),
                LookaheadSeconds = 0.9f,
                MinLookahead = 10f,
                // Freeplay opponents without an identity: a spread around the tuned point (some tidy, some ragged).
                DriftSkill = Mathf.Lerp(0.45f, 0.8f, ((index * 7) % 6) / 5f),
            };
        }

        /// <summary>
        /// A rival's drift skill (spec §13: rivals differ in how they read a corner): rises with the campaign, and the
        /// tendencies built on rotation, recovery and momentum drift better than the tidy line-keepers; a sheet whose
        /// strength names slides or rotation adds a little.
        /// </summary>
        public static float DriftSkillFor(RivalDef rival, int stageNumber)
        {
            float progress = Mathf.Clamp01((stageNumber - 1) / 29f);
            float skill = Mathf.Lerp(0.5f, 0.78f, progress);
            switch (rival.Tendency)
            {
                case "rotation-specialist": skill += 0.18f; break;
                case "recovery-specialist": skill += 0.1f; break;
                case "momentum-reader": skill += 0.06f; break;
                case "wide-entry-specialist": skill += 0.04f; break;
                case "surface-reader":
                case "wet-line-reader": skill += 0.03f; break;
                case "margin-keeper": skill -= 0.15f; break;
                case "straight-line-planner": skill -= 0.1f; break;
                case "geometric-apexer":
                case "early-set-cornerer": skill -= 0.05f; break;
            }
            string strength = (rival.Strength ?? "").ToLowerInvariant();
            if (strength.Contains("drift") || strength.Contains("slide") || strength.Contains("rotation")) skill += 0.06f;
            return Mathf.Clamp(skill, 0.2f, 0.98f);
        }

        public static DriverProfile For(RivalDef rival, int stageNumber, float paceScale = 1f)
        {
            float progress = Mathf.Clamp01((stageNumber - 1) / 29f);
            var p = new DriverProfile
            {
                CornerSpeedFactor = Mathf.Lerp(0.8f, 0.9f, progress),
                BrakingDecel = Mathf.Lerp(6.2f, 7.2f, progress),
                LineAggression = 0.45f,
                LookaheadSeconds = 0.9f,
                MinLookahead = 10f,
            };
            // Each tendency is a combination of speed-plan biases and line/pedal behaviours (DriverProfile), measured per crew on
            // shared corners by the crew telemetry tour (V-106): who brakes later, who apexes late, who swings wide, who is gentle
            // on the throttle.
            switch (rival.Tendency)
            {
                case "late-brake-anchor": p.BrakingDecel += 1.0f; p.BrakeGain = 1.5f; break;
                case "brake-release-student": p.BrakingDecel -= 0.8f; p.BrakeGain = 0.7f; break;
                case "margin-keeper": p.CornerSpeedFactor -= 0.02f; p.LineAggression -= 0.15f; p.ThrottleBias = -0.08f; break;
                // No apex shift: an early apex costs exit speed, the opposite of carrying momentum (V-108: R48 lost 17.6 s on C25).
                case "momentum-reader": p.CornerSpeedFactor += 0.02f; p.BrakingDecel -= 0.4f; break;
                case "geometric-apexer": p.LineAggression += 0.2f; break;
                case "wide-entry-specialist": p.LineAggression += 0.3f; p.EntryWidth = 0.6f; break;
                case "exit-traction-specialist": p.CornerSpeedFactor -= 0.04f; p.ApexShift = 8f; p.ThrottleBias = 0.15f; break;
                case "high-speed-arc-reader": p.CornerSpeedFactor += 0.015f; p.LookaheadSeconds = 1.1f; p.EntryWidth = 0.3f; break;
                case "rhythm-linker": p.LookaheadSeconds = 1.05f; p.ApexShift = -3f; break;
                case "power-conserver": p.BrakingDecel -= 0.3f; p.ThrottleBias = -0.15f; break;
                case "rotation-specialist": p.CornerSpeedFactor += 0.01f; p.LineAggression += 0.1f; p.ApexShift = -6f; break;
                case "recovery-specialist": p.CornerSpeedFactor += 0.01f; p.ThrottleBias = 0.05f; break;
                case "surface-reader":
                case "wet-line-reader": p.LineAggression -= 0.1f; p.ThrottleBias = -0.05f; break;
                case "pressure-tester": p.LineAggression -= 0.05f; p.BrakeGain = 1.2f; break;
                case "inside-line-defender": p.LineAggression -= 0.05f; p.EntryWidth = -0.3f; break;
                case "straight-line-planner": p.LookaheadSeconds = 1.2f; p.ApexShift = 5f; break;
                case "gear-optimizer": p.BrakingDecel -= 0.2f; p.ThrottleBias = 0.08f; break;
                case "early-set-cornerer": p.BrakingDecel -= 0.2f; p.ApexShift = -8f; break;
            }
            p.LineAggression = Mathf.Clamp01(p.LineAggression);
            p.PaceScale = paceScale;
            p.DriftSkill = DriftSkillFor(rival, stageNumber);
            return p;
        }
    }
}
