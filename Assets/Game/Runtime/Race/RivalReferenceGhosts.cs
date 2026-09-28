using System.Collections.Generic;
using NightSignal.Core.Ghosts;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>
    /// The recorded authored rival reference ghosts (spec §8), one per course: Resources/RivalGhosts/&lt;course&gt;.json, made by
    /// the explicit PlayMode recorder <c>RivalReferenceGhostTests</c>. A reference recorded under another course revision,
    /// physics or scoring version is not compatible and stays off the road (the caller checks, as for every ghost).
    /// </summary>
    public static class RivalReferenceGhosts
    {
        public const string Folder = "RivalGhosts";
        static readonly Dictionary<string, GhostRecording> cache = new Dictionary<string, GhostRecording>();

        /// <summary>The course's reference ghost, or null (main thread: it reads Resources).</summary>
        public static GhostRecording For(string courseId)
        {
            if (string.IsNullOrEmpty(courseId)) return null;
            if (cache.TryGetValue(courseId, out GhostRecording g)) return g;
            TextAsset t = Resources.Load<TextAsset>(Folder + "/" + courseId);
            g = t == null ? null : GhostRecording.Parse(t.text, out _);
            cache[courseId] = g;
            return g;
        }

        public static bool IsReference(GhostRecording g) => g?.Header?.Provenance == Core.Rules.RivalReference.Provenance;

        /// <summary>The overlay label's owner part: "Aki Night's reference".</summary>
        public static string Owner(GhostRecording g) => $"{g.Header.Driver}'s reference";
    }
}
