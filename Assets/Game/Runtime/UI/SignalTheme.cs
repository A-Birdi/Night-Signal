using UnityEngine;

namespace NightSignal.UI
{
    /// <summary>
    /// "Signal / Sector" UI language (spec §15): graphite/ink surfaces, warm off-white labels, a controlled
    /// signal-red selection accent, amber caution, cool cyan timing/reference. Colour carries function only;
    /// every status also has a text/shape cue (colour-independent).
    /// </summary>
    public static class SignalTheme
    {
        public static readonly Color Ink = Hex(0x0E0F12);
        public static readonly Color Graphite = Hex(0x17191E);
        public static readonly Color GraphiteRaised = Hex(0x20232A);
        public static readonly Color Rule = Hex(0x343842);
        public static readonly Color Label = Hex(0xECE6D8);
        public static readonly Color LabelDim = Hex(0x9A968D);
        public static readonly Color Signal = Hex(0xD7263D);
        public static readonly Color Caution = Hex(0xF2A541);
        public static readonly Color Timing = Hex(0x3EC6D8);
        public static readonly Color Positive = Hex(0x6CC28A);

        // Type scale at the 1920×1080 reference; accessibility text scaling multiplies these.
        public const float Heading = 40f;
        public const float Subheading = 26f;
        public const float Body = 22f;
        public const float Small = 17f;
        public const float Numeral = 30f;
        public const float HudNumeral = 64f;

        public static float TextScale = 1f;
        public static bool HighContrast;
        public static bool ReducedMotion;

        /// <summary>Wipe duration for local navigation (spec §15: 180–280 ms; reduced motion → short crossfade).</summary>
        public static float WipeSeconds => ReducedMotion ? 0.12f : 0.22f;

        public static Color Surface => HighContrast ? Color.black : Graphite;
        public static Color Text => HighContrast ? Color.white : Label;

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
    }
}
