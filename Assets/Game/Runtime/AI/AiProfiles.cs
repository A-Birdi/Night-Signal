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
            };
        }

        public static DriverProfile For(RivalDef rival, int stageNumber)
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
            switch (rival.Tendency)
            {
                case "late-brake-anchor": p.BrakingDecel += 1.0f; break;
                case "brake-release-student": p.BrakingDecel -= 0.8f; break;
                case "margin-keeper": p.CornerSpeedFactor -= 0.02f; p.LineAggression -= 0.15f; break;
                case "momentum-reader": p.CornerSpeedFactor += 0.02f; p.BrakingDecel -= 0.4f; break;
                case "geometric-apexer": p.LineAggression += 0.2f; break;
                case "wide-entry-specialist": p.LineAggression += 0.3f; break;
                case "exit-traction-specialist": p.CornerSpeedFactor -= 0.01f; break;
                case "high-speed-arc-reader": p.CornerSpeedFactor += 0.015f; p.LookaheadSeconds = 1.1f; break;
                case "rhythm-linker": p.LookaheadSeconds = 1.05f; break;
                case "power-conserver": p.BrakingDecel -= 0.3f; break;
                case "rotation-specialist": p.CornerSpeedFactor += 0.01f; p.LineAggression += 0.1f; break;
                case "recovery-specialist": p.CornerSpeedFactor += 0.01f; break;
                case "surface-reader":
                case "wet-line-reader": p.LineAggression -= 0.1f; break;
                case "pressure-tester":
                case "inside-line-defender": p.LineAggression -= 0.05f; break;
                case "straight-line-planner": p.LookaheadSeconds = 1.2f; break;
                case "gear-optimizer":
                case "early-set-cornerer": p.BrakingDecel -= 0.2f; break;
            }
            p.LineAggression = Mathf.Clamp01(p.LineAggression);
            return p;
        }
    }
}
