using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    /// <summary>One Freeplay lighting/weather preset: a fixed surface (grip) and lighting, or the event's own (null).</summary>
    public sealed class ConditionPreset
    {
        public string Id;
        /// <summary>What the player reads ("Night, wet").</summary>
        public string Label;
        /// <summary>dry | damp | wet; null = the event's own surface.</summary>
        public string Surface;
        /// <summary>A lighting preset id (night, dawn, blue-hour, fog …); null = the event's own time of day.</summary>
        public string Lighting;
    }

    /// <summary>
    /// Freeplay's lighting/weather presets (spec §8: the host selects a "lighting/weather preset"), one table shared by the
    /// control plane, the game server and both clients. "stage-default" races the event's own conditions — a campaign side's
    /// authored ones, else the course's; every other preset fixes both the surface and the lighting, and is shown before
    /// readiness. Only Freeplay chooses: campaign stages, Team Trials and challenge trials race their own conditions, because
    /// their benchmarks and targets are certified in them. Records and ghosts key on the surface, which a preset sets.
    /// </summary>
    public static class ConditionPresets
    {
        public const string Default = "stage-default";

        static readonly ConditionPreset[] all =
        {
            new ConditionPreset { Id = Default, Label = "Course conditions", Surface = null, Lighting = null },
            new ConditionPreset { Id = "dry-night", Label = "Night, dry", Surface = "dry", Lighting = "night" },
            new ConditionPreset { Id = "wet-night", Label = "Night, wet", Surface = "wet", Lighting = "night" },
            new ConditionPreset { Id = "dawn", Label = "Dawn, dry", Surface = "dry", Lighting = "dawn" },
            new ConditionPreset { Id = "blue-hour", Label = "Blue hour, dry", Surface = "dry", Lighting = "blue-hour" },
            new ConditionPreset { Id = "fog", Label = "Fog, damp", Surface = "damp", Lighting = "fog" },
        };

        public static IReadOnlyList<ConditionPreset> All => all;

        public static readonly string[] Ids = all.Select(p => p.Id).ToArray();

        /// <summary>The preset with this id; null (or empty) means <see cref="Default"/>; an unknown id is null.</summary>
        public static ConditionPreset Find(string id) =>
            string.IsNullOrEmpty(id) ? all[0] : all.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

        public static bool IsKnown(string id) => Find(id) != null;

        public static bool IsDefault(string id) => string.IsNullOrEmpty(id) || id == Default;

        /// <summary>The surface an event races: the preset's, else <paramref name="own"/> (the stage's or course's).</summary>
        public static string Surface(string id, string own) => Find(id)?.Surface ?? own;

        /// <summary>The lighting an event races under: the preset's, else <paramref name="own"/>.</summary>
        public static string Lighting(string id, string own) => Find(id)?.Lighting ?? own;

        /// <summary>
        /// The conditions as the player reads them before readiness: a preset's label, or the event's own
        /// (<paramref name="ownConditions"/>, e.g. a course's "Early night, damp") under the default.
        /// </summary>
        public static string Describe(string id, string ownConditions)
        {
            ConditionPreset p = Find(id);
            if (p == null) return "unknown conditions";
            if (p.Surface != null) return p.Label;
            return string.IsNullOrEmpty(ownConditions) ? "the course's own" : ownConditions;
        }

        /// <summary>A lighting id and surface as words: ("late-afternoon", "dry") → "Late afternoon, dry".</summary>
        public static string Words(string lighting, string surface)
        {
            string time = string.IsNullOrEmpty(lighting) ? "" : char.ToUpperInvariant(lighting[0]) + lighting.Substring(1).Replace('-', ' ');
            if (string.IsNullOrEmpty(surface)) return time;
            return time.Length == 0 ? surface : time + ", " + surface;
        }
    }
}
