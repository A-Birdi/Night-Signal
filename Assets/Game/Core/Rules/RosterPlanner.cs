using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    public enum ActorKind { Human = 0, Ai = 1 }

    /// <summary>Side of the event. Humans are always on the Player team; friendly AI only exist in Team Trials.</summary>
    public enum RosterTeam { Player = 0, Opposing = 1 }

    public enum RosterRole { Driver = 0, FeaturedRival = 1, SupportRival = 2, FriendlyAi = 3, OpposingAi = 4 }

    /// <summary>Car-to-car contact policy (Addendum 01 §2): Light Contact normally; Time Attack is non-contact.</summary>
    public enum ContactPolicy { LightContact = 0, NonContact = 1 }

    public enum EventFormat { Campaign = 0, FreeplaySprint = 1, FreeplayCircuit = 2, DriftAttack = 3, TimeAttack = 4, TeamTrial = 5, Cup = 6 }

    /// <summary>
    /// One actor in a frozen race roster. An AI has a rival/profile ID and server-owned controls — never an account,
    /// card or receipt identity.
    /// </summary>
    public sealed class RaceRosterEntry
    {
        public string EntrantId;
        public ActorKind Kind;
        public RosterTeam Team;
        public RosterRole Role;
        /// <summary>Account ID for humans; rival/AI-profile ID for AI.</summary>
        public string DriverId;
    }

    public sealed class RaceRoster
    {
        public EventFormat Format;
        public ContactPolicy Contact;
        public List<RaceRosterEntry> Entries = new List<RaceRosterEntry>();
        public bool WasClamped;
        public string Explanation = "";
        public int Humans => Entries.Count(e => e.Kind == ActorKind.Human);
        public int FriendlyAi => Entries.Count(e => e.Kind == ActorKind.Ai && e.Team == RosterTeam.Player);
        public int OpposingAi => Entries.Count(e => e.Kind == ActorKind.Ai && e.Team == RosterTeam.Opposing);
        public int Vehicles => Entries.Count;
        public string FeaturedRival => Entries.FirstOrDefault(e => e.Role == RosterRole.FeaturedRival)?.DriverId;
    }

    /// <summary>
    /// Event rosters under Addendum 01 §1 (supersedes the master's six-TOTAL cap and six-human benchmark replay):
    /// 1 ≤ H ≤ 6 humans, H + friendly AI + opposing AI ≤ 12 vehicles, campaign opposition authored per stage (the
    /// featured rival is always a live, solid car — finales are H + 1 duels), Freeplay AI chosen up to the remaining
    /// capacity, Team Trials six versus six, Time Attack humans only and non-contact. Rosters freeze at allocation; a
    /// DQ is never replaced by AI.
    /// </summary>
    public static class RosterPlanner
    {
        public static void ValidateCounts(int humans, int friendlyAi, int opposingAi)
        {
            if (humans < 1 || humans > Limits.MaxEventHumanEntrants)
                throw new ArgumentOutOfRangeException(nameof(humans), $"An event needs 1–{Limits.MaxEventHumanEntrants} humans, got {humans}");
            if (friendlyAi < 0 || opposingAi < 0) throw new ArgumentOutOfRangeException(nameof(friendlyAi));
            if (humans + friendlyAi + opposingAi > Limits.MaxRaceVehicles)
                throw new ArgumentOutOfRangeException(nameof(opposingAi), $"{humans + friendlyAi + opposingAi} vehicles exceed the {Limits.MaxRaceVehicles}-vehicle limit");
        }

        public static void Validate(RaceRoster roster)
        {
            ValidateCounts(roster.Humans, roster.FriendlyAi, roster.OpposingAi);
            if (roster.Entries.Select(e => e.EntrantId).Distinct().Count() != roster.Entries.Count)
                throw new ArgumentException("Duplicate entrant ID in roster");
            if (roster.Entries.Where(e => e.Kind == ActorKind.Human).Select(e => e.DriverId).Distinct().Count() != roster.Humans)
                throw new ArgumentException("A human account appears twice in one roster");
            if (roster.Format == EventFormat.TimeAttack && (roster.Entries.Any(e => e.Kind == ActorKind.Ai) || roster.Contact != ContactPolicy.NonContact))
                throw new ArgumentException("Time Attack is humans only and non-contact");
        }

        /// <summary>Campaign stage: the humans plus the stage's authored live opposition (featured first).</summary>
        public static RaceRoster PlanCampaign(IReadOnlyList<string> humanAccountIds, IReadOnlyList<string> authoredOpponents, string stageId, CampaignMode mode)
        {
            if (authoredOpponents == null || authoredOpponents.Count == 0)
                throw new ArgumentException("A campaign stage always has authored live opposition, featured rival first");
            foreach (string id in authoredOpponents) FinalRivals.Require(id, AiPlacementContext.CampaignEncounter, stageId, mode);
            var roster = new RaceRoster { Format = EventFormat.Campaign, Contact = ContactPolicy.LightContact };
            AddHumans(roster, humanAccountIds);
            for (int i = 0; i < authoredOpponents.Count; i++)
                roster.Entries.Add(Ai(authoredOpponents[i], RosterTeam.Opposing, i == 0 ? RosterRole.FeaturedRival : RosterRole.SupportRival));
            Validate(roster);
            return roster;
        }

        /// <summary>
        /// Freeplay: the leader picks 0..(12 − H) opponents from <paramref name="aiPool"/>. A stale selection larger than the
        /// remaining capacity is clamped with a visible explanation; humans are never ejected. Time Attack adds no AI.
        /// </summary>
        public static RaceRoster PlanFreeplay(IReadOnlyList<string> humanAccountIds, EventFormat format, int requestedAi, IReadOnlyList<string> aiPool)
        {
            if (format == EventFormat.Campaign || format == EventFormat.TeamTrial)
                throw new ArgumentException("Use PlanCampaign / PlanTeamTrial for those formats");
            var roster = new RaceRoster
            {
                Format = format,
                Contact = format == EventFormat.TimeAttack ? ContactPolicy.NonContact : ContactPolicy.LightContact,
            };
            AddHumans(roster, humanAccountIds);
            if (requestedAi < 0) throw new ArgumentOutOfRangeException(nameof(requestedAi));
            int capacity = format == EventFormat.TimeAttack ? 0 : Limits.MaxRaceVehicles - roster.Humans;
            int ai = Math.Min(requestedAi, capacity);
            if (requestedAi > capacity)
            {
                roster.WasClamped = true;
                roster.Explanation = format == EventFormat.TimeAttack
                    ? "Time Attack has no live opponents; target recordings are replays."
                    : $"AI reduced from {requestedAi} to {ai}: {roster.Humans} drivers joined and a race holds {Limits.MaxRaceVehicles} cars.";
            }
            List<string> pool = (aiPool ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            foreach (string id in pool) FinalRivals.Require(id, AiPlacementContext.FreeplayOpponent);
            for (int i = 0; i < ai; i++)
            {
                string driver = i < pool.Count ? pool[i] : $"ai-{i + 1}";
                roster.Entries.Add(Ai(driver, RosterTeam.Opposing, RosterRole.OpposingAi, $"ai-{i + 1}"));
            }
            Validate(roster);
            return roster;
        }

        /// <summary>Team Trial: six Player-team positions (H humans + 6 − H friendly AI) versus six opposing AI.</summary>
        public static RaceRoster PlanTeamTrial(IReadOnlyList<string> humanAccountIds, IReadOnlyList<string> allyPool, IReadOnlyList<string> opponentPool)
        {
            var roster = new RaceRoster { Format = EventFormat.TeamTrial, Contact = ContactPolicy.LightContact };
            AddHumans(roster, humanAccountIds);
            int allies = Limits.TeamTrialSideSize - roster.Humans;
            List<string> allyIds = Distinct(allyPool, AiPlacementContext.FriendlyAi, allies, "allies");
            List<string> oppIds = Distinct(opponentPool, AiPlacementContext.TeamTrial, Limits.TeamTrialSideSize, "opponents");
            if (allyIds.Intersect(oppIds).Any()) throw new ArgumentException("A rival cannot drive on both teams");
            foreach (string id in allyIds) roster.Entries.Add(Ai(id, RosterTeam.Player, RosterRole.FriendlyAi));
            foreach (string id in oppIds) roster.Entries.Add(Ai(id, RosterTeam.Opposing, RosterRole.OpposingAi));
            Validate(roster);
            return roster;
        }

        static List<string> Distinct(IReadOnlyList<string> pool, AiPlacementContext context, int needed, string what)
        {
            List<string> ids = (pool ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Distinct().Take(needed).ToList();
            if (ids.Count < needed) throw new ArgumentException($"Team Trial needs {needed} {what}; the pool has {ids.Count}");
            foreach (string id in ids) FinalRivals.Require(id, context);
            return ids;
        }

        static void AddHumans(RaceRoster roster, IReadOnlyList<string> humanAccountIds)
        {
            if (humanAccountIds == null) throw new ArgumentNullException(nameof(humanAccountIds));
            if (humanAccountIds.Count < 1 || humanAccountIds.Count > Limits.MaxEventHumanEntrants)
                throw new ArgumentOutOfRangeException(nameof(humanAccountIds), $"An event needs 1–{Limits.MaxEventHumanEntrants} humans, got {humanAccountIds.Count}");
            foreach (string id in humanAccountIds)
                roster.Entries.Add(new RaceRosterEntry { EntrantId = id, Kind = ActorKind.Human, Team = RosterTeam.Player, Role = RosterRole.Driver, DriverId = id });
        }

        static RaceRosterEntry Ai(string driverId, RosterTeam team, RosterRole role, string entrantId = null) =>
            new RaceRosterEntry { EntrantId = entrantId ?? driverId, Kind = ActorKind.Ai, Team = team, Role = role, DriverId = driverId };
    }
}
