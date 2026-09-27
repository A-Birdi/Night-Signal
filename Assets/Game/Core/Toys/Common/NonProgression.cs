using System;

namespace NightSignal.Core.Toys
{
    /// <summary>
    /// The explicit non-progression activity domain (Addendum 02 §1.6, D209, §11, C09). Everything under
    /// <c>NightSignal.Core.Toys</c> belongs to it: session scores, crowns, completed toy models, lap times of toy cars and
    /// shared artwork. None of these types carries Credits, RP, challenge progress, soundtrack unlocks or official course
    /// records, and none references the official race rules/economy namespace of Core (verified by tests).
    /// Settlement pipelines must refuse any envelope whose domain is <see cref="Domain"/>.
    /// </summary>
    public static class NonProgression
    {
        /// <summary>Wire/domain tag for anything produced by a toy. Never a result kind accepted by settlement.</summary>
        public const string Domain = "toy-nonprogression";

        /// <summary>True for any string that claims to be (or is prefixed as) a toy-domain result kind.</summary>
        public static bool IsToyDomain(string resultKindOrDomain) =>
            resultKindOrDomain != null && (resultKindOrDomain.Equals(Domain, StringComparison.OrdinalIgnoreCase) ||
                                           resultKindOrDomain.StartsWith("toy", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Implemented by every toy record type that could be displayed as an 'outcome'.</summary>
    public interface INonProgressionRecord
    {
        /// <summary>Always <see cref="NonProgression.Domain"/>.</summary>
        string Domain { get; }
    }
}
