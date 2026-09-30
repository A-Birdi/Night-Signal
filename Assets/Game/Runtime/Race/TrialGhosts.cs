using System.Collections.Generic;
using NightSignal.Core.Ghosts;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>
    /// A challenge trial's fixed Gold ghost (docs/CHALLENGE_TRIALS.md; CH13 "beat the fixed C21 Gold ghost"): the measured
    /// reference run of the trial's loaner, recorded by the explicit PlayMode <c>ChallengeTrialReferenceTests</c> into
    /// Resources/TrialGhosts/&lt;trial&gt;.json. Its time is the trial's time target. Raced offline and online like any ghost —
    /// one recorded under another course revision, physics or scoring version stays off the road.
    /// </summary>
    public static class TrialGhosts
    {
        public const string Folder = "TrialGhosts";
        public const string Provenance = "trial-reference";
        static readonly Dictionary<string, GhostRecording> cache = new Dictionary<string, GhostRecording>();

        /// <summary>The trial's Gold ghost, or null (main thread: it reads Resources).</summary>
        public static GhostRecording For(string trialId)
        {
            if (string.IsNullOrEmpty(trialId)) return null;
            if (cache.TryGetValue(trialId, out GhostRecording g)) return g;
            TextAsset t = Resources.Load<TextAsset>(Folder + "/" + trialId);
            g = t == null ? null : GhostRecording.Parse(t.text, out _);
            cache[trialId] = g;
            return g;
        }

        public static bool IsTrialGhost(GhostRecording g) => g?.Header?.Provenance == Provenance;

        /// <summary>The Gold ghost's overlay colour (a personal ghost is cyan, a member's amber, a rival reference red).</summary>
        public static readonly Color Tint = new Color(1f, 0.82f, 0.25f);

        public const string Label = "Gold ghost";
    }
}
