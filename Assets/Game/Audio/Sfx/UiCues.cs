namespace NightSignal.AudioSynth
{
    public enum UiCue : byte
    {
        SignalRibbon = 0,
        MenuMove = 1,
        MenuConfirm = 2,
        MenuBack = 3,
        ReadyChime = 4,
        Countdown3 = 5,
        Countdown2 = 6,
        Countdown1 = 7,
        CountdownGo = 8,
        ResultsWin = 9,
        ResultsLoss = 10,
        Count = 11,
    }

    public enum StrikeKind : byte
    {
        /// <summary>Two-operator FM bell (soft, glassy).</summary>
        Bell = 0,
        /// <summary>Sine with a touch of second harmonic.</summary>
        Soft = 1,
        /// <summary>Low-passed square beep (countdown).</summary>
        Beep = 2,
        /// <summary>Very short filtered tick (menu move).</summary>
        Tick = 3,
        /// <summary>Warm detuned saw pad through a low-pass (results stings).</summary>
        Pad = 4,
    }

    /// <summary>One note of a UI cue.</summary>
    public struct Strike
    {
        public float Delay;     // seconds after the cue starts
        public StrikeKind Kind;
        public float Note;      // MIDI
        public float Duration;  // seconds (decay length)
        public float GainDb;
        public float Pan;

        public Strike(float delay, StrikeKind kind, float note, float duration, float gainDb, float pan = 0f)
        {
            Delay = delay;
            Kind = kind;
            Note = note;
            Duration = duration;
            GainDb = gainDb;
            Pan = pan;
        }
    }

    /// <summary>
    /// Original UI cue designs. Gentle, short and distinct: the SIGNAL ribbon is a quiet rising fifth with a
    /// soft octave shimmer; countdown beeps share a pitch and GO is an octave-plus-fifth brighter; results
    /// stings are short chord flourishes (win bright, loss warm and unpunishing).
    /// </summary>
    public static class UiCueLibrary
    {
        static readonly Strike[][] cues = Build();

        public static Strike[] Get(UiCue cue) => cues[(int)cue];

        /// <summary>Caption for important non-verbal cues (spec §16), or null when none is needed.</summary>
        public static string Caption(UiCue cue)
        {
            switch (cue)
            {
                case UiCue.SignalRibbon: return "[Signal chime]";
                case UiCue.ReadyChime: return "[Ready chime]";
                case UiCue.Countdown3: return "[Countdown: 3]";
                case UiCue.Countdown2: return "[Countdown: 2]";
                case UiCue.Countdown1: return "[Countdown: 1]";
                case UiCue.CountdownGo: return "[Countdown: GO]";
                case UiCue.ResultsWin: return "[Results fanfare]";
                case UiCue.ResultsLoss: return "[Results chime]";
                default: return null;
            }
        }

        static Strike[][] Build()
        {
            var c = new Strike[(int)UiCue.Count][];
            // A5 then E6 (rising fifth), second note softer and longer, faint octave shimmer.
            c[(int)UiCue.SignalRibbon] = new[]
            {
                new Strike(0f, StrikeKind.Bell, 81f, 0.55f, -17f, -0.15f),
                new Strike(0.14f, StrikeKind.Bell, 88f, 1.1f, -18f, 0.15f),
                new Strike(0.14f, StrikeKind.Soft, 76f, 0.9f, -27f, 0f),
            };
            c[(int)UiCue.MenuMove] = new[] { new Strike(0f, StrikeKind.Tick, 91f, 0.035f, -17f) };
            c[(int)UiCue.MenuConfirm] = new[]
            {
                new Strike(0f, StrikeKind.Soft, 84f, 0.12f, -19f),
                new Strike(0.055f, StrikeKind.Soft, 91f, 0.2f, -19f),
            };
            c[(int)UiCue.MenuBack] = new[]
            {
                new Strike(0f, StrikeKind.Soft, 86f, 0.1f, -21f),
                new Strike(0.05f, StrikeKind.Soft, 79f, 0.16f, -22f),
            };
            c[(int)UiCue.ReadyChime] = new[]
            {
                new Strike(0f, StrikeKind.Bell, 74f, 0.5f, -19f, -0.2f),
                new Strike(0.075f, StrikeKind.Bell, 78f, 0.5f, -19f, -0.05f),
                new Strike(0.15f, StrikeKind.Bell, 81f, 0.6f, -19f, 0.05f),
                new Strike(0.225f, StrikeKind.Bell, 86f, 0.9f, -20f, 0.2f),
            };
            c[(int)UiCue.Countdown3] = new[] { new Strike(0f, StrikeKind.Beep, 81f, 0.16f, -15f) };
            c[(int)UiCue.Countdown2] = new[] { new Strike(0f, StrikeKind.Beep, 81f, 0.16f, -15f) };
            c[(int)UiCue.Countdown1] = new[] { new Strike(0f, StrikeKind.Beep, 81f, 0.16f, -15f) };
            c[(int)UiCue.CountdownGo] = new[]
            {
                new Strike(0f, StrikeKind.Beep, 93f, 0.55f, -15f),
                new Strike(0f, StrikeKind.Beep, 100f, 0.45f, -24f),
                new Strike(0f, StrikeKind.Soft, 81f, 0.6f, -22f),
            };
            // Win: D major add9 rising flourish over a warm pad.
            c[(int)UiCue.ResultsWin] = new[]
            {
                new Strike(0f, StrikeKind.Pad, 62f, 1.6f, -22f, -0.2f),
                new Strike(0f, StrikeKind.Pad, 69f, 1.6f, -22f, 0.2f),
                new Strike(0f, StrikeKind.Bell, 74f, 0.6f, -18f),
                new Strike(0.09f, StrikeKind.Bell, 78f, 0.6f, -18f),
                new Strike(0.18f, StrikeKind.Bell, 81f, 0.7f, -18f),
                new Strike(0.27f, StrikeKind.Bell, 86f, 1.2f, -18f),
                new Strike(0.36f, StrikeKind.Bell, 88f, 1.4f, -21f),
            };
            // Loss: gentle B minor(add9) → G major colour, slower, no buzzer.
            c[(int)UiCue.ResultsLoss] = new[]
            {
                new Strike(0f, StrikeKind.Pad, 59f, 1.8f, -23f),
                new Strike(0f, StrikeKind.Pad, 66f, 1.8f, -24f),
                new Strike(0f, StrikeKind.Bell, 71f, 0.8f, -20f),
                new Strike(0.16f, StrikeKind.Bell, 74f, 0.8f, -20f),
                new Strike(0.32f, StrikeKind.Bell, 78f, 1.0f, -20f),
                new Strike(0.48f, StrikeKind.Bell, 73f, 1.5f, -21f),
            };
            return c;
        }
    }
}
