using System;

namespace NightSignal.Core.Rules
{
    /// <summary>Performance Index classes, spec §9: D 100–299, C 300–499, B 500–699, A 700–849, S 850–999.</summary>
    public static class PerformanceIndex
    {
        public const int Min = 100;
        public const int Max = 999;

        public static PerformanceClass ClassOf(int pi)
        {
            if (pi < Min || pi > Max) throw new ArgumentOutOfRangeException(nameof(pi), $"PI must be {Min}–{Max}");
            if (pi < 300) return PerformanceClass.D;
            if (pi < 500) return PerformanceClass.C;
            if (pi < 700) return PerformanceClass.B;
            if (pi < 850) return PerformanceClass.A;
            return PerformanceClass.S;
        }

        /// <summary>Event caps compare actual PI; an over-cap build is offered a legal preset or loaner instead.</summary>
        public static bool IsLegalFor(int pi, int eventMaxPi) => pi >= Min && pi <= eventMaxPi;
    }
}
