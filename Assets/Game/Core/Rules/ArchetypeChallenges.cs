using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;

namespace NightSignal.Core.Rules
{
    /// <summary>One Freeplay race as the archetype challenges read it.</summary>
    public sealed class ArchetypeRace
    {
        /// <summary>The field's authored AI rivals in roster order; the first is the lead (the named rival when one was picked).</summary>
        public IReadOnlyList<string> AiRivals = Array.Empty<string>();
        public RunOutcome Outcome;
        public int Placement;
        public bool Tied;
    }

    /// <summary>What a player has done against Freeplay rival archetypes so far.</summary>
    public sealed class ArchetypeState
    {
        /// <summary>Archetypes met in legally finished Freeplay races (CH73).</summary>
        public readonly HashSet<string> Raced = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Lead archetypes beaten since the last quit (CH38).</summary>
        public readonly HashSet<string> WonStreak = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Freeplay rival archetypes (Appendix E; spec §13): an archetype is an authored rival's driving tendency (19 exist,
    /// shared by the 48 rivals). CH38 Three Different Rivals: win three Freeplay races against three different lead-AI
    /// archetypes with no race quit between those wins — a loss keeps what was won, an explicit quit clears it (a
    /// disconnect is not a quit). The lead AI is the field's first AI entrant: the named rival when the host picked one,
    /// otherwise the first of the random authored pool. CH73 Twelve Different Voices: legal finishes against 12 distinct
    /// archetypes in Freeplay — every authored rival in the field demonstrates its own; the named-rival pick makes a rare
    /// style reachable on purpose rather than by luck. Anonymous AI and fields without AI (Time Attack, pure PvP) count
    /// for neither. The control plane replays a player's settled Freeplay races (oldest first); the Local profile keeps
    /// the two sets.
    /// </summary>
    public static class ArchetypeChallenges
    {
        public const string ThreeDifferentRivals = "CH38", TwelveDifferentVoices = "CH73";
        public const int RivalsNeeded = 3, VoicesNeeded = 12;

        /// <summary>Folds races, oldest first, into the archetypes raced and the current CH38 set.</summary>
        public static ArchetypeState Replay(ContentCatalogue catalogue, IEnumerable<ArchetypeRace> races)
        {
            var s = new ArchetypeState();
            foreach (ArchetypeRace r in races ?? Enumerable.Empty<ArchetypeRace>()) Apply(s, catalogue, r);
            return s;
        }

        /// <summary>Applies one race to the state.</summary>
        public static void Apply(ArchetypeState s, ContentCatalogue catalogue, ArchetypeRace r)
        {
            if (s == null || r?.AiRivals == null) return;
            List<string> tendencies = r.AiRivals.Select(id => TendencyOf(catalogue, id)).ToList();
            if (tendencies.All(t => t == null)) return; // no authored rival in the field: not an archetype race
            if (r.Outcome == RunOutcome.Quit)
            {
                s.WonStreak.Clear();
                return;
            }
            if (r.Outcome != RunOutcome.Finished) return;
            foreach (string t in tendencies) if (t != null) s.Raced.Add(t);
            if (r.Placement == 1 && !r.Tied && tendencies[0] != null) s.WonStreak.Add(tendencies[0]);
        }

        /// <summary>The archetype challenges the state satisfies (whether or not they were granted before).</summary>
        public static List<string> Satisfied(ArchetypeState s)
        {
            var met = new List<string>();
            if (s == null) return met;
            if (s.WonStreak.Count >= RivalsNeeded) met.Add(ThreeDifferentRivals);
            if (s.Raced.Count >= VoicesNeeded) met.Add(TwelveDifferentVoices);
            return met;
        }

        /// <summary>The rival's tendency, or null for an anonymous or unknown AI.</summary>
        public static string TendencyOf(ContentCatalogue catalogue, string rivalId) =>
            catalogue != null && catalogue.TryRival(rivalId, out RivalDef r) && !string.IsNullOrEmpty(r.Tendency) ? r.Tendency : null;

        /// <summary>One line for a Freeplay screen: progress on both challenges.</summary>
        public static string ProgressLine(ArchetypeState s) =>
            $"Rival styles raced {Math.Min(s?.Raced.Count ?? 0, VoicesNeeded)}/{VoicesNeeded} · different lead styles beaten without quitting " +
            $"{Math.Min(s?.WonStreak.Count ?? 0, RivalsNeeded)}/{RivalsNeeded}";
    }
}
