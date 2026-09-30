using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.Race;

namespace NightSignal.Front
{
    /// <summary>What the player chose to drive in a Local event: an owned car instance, or a loaner for a capped stage.</summary>
    public sealed class LocalCarChoice
    {
        public string ModelId;
        public string InstanceId;
        public bool Loaner => string.IsNullOrEmpty(InstanceId);
    }

    /// <summary>A Local event being driven: everything needed to judge it afterwards, frozen before the start.</summary>
    public sealed class LocalEventPlan
    {
        public string EventId;
        public EventKind Kind;
        public string CourseId;
        public StageDef Stage;
        public CampaignMode Mode;
        public StageBenchmark Benchmark;
        public RaceEventRules Rules;
        public List<string> OpposingAi = new List<string>();
        public LocalCarChoice Car;
        public string FreeplayFormat = "";
        /// <summary>A challenge trial run (docs/CHALLENGE_TRIALS.md): its id, and the build hash of the loaner actually driven.</summary>
        public string TrialId, TrialBuildHash;
    }

    /// <summary>
    /// Builds Local events (Addendum 01 §8) from the same Core rules the online services use, and turns the offline
    /// race classification into <see cref="LocalEventFacts"/> for <see cref="LocalProgression.ApplyEvent"/>.
    /// </summary>
    public static class LocalEvents
    {
        public static LocalEventPlan Campaign(LocalSession s, StageDef stage, CampaignMode mode, LocalCarChoice car)
        {
            StageBenchmark benchmark = StageBenchmarks.For(s.Catalogue, stage, mode);
            List<string> opponents = (mode == CampaignMode.Hard ? stage.Hard : stage.Normal).Opponents;
            RaceRoster roster = RosterPlanner.PlanCampaign(new[] { "local" }, opponents, stage.Id, mode);
            return new LocalEventPlan
            {
                EventId = NewEventId(),
                Kind = EventKind.CampaignStage,
                CourseId = stage.Course,
                Stage = stage,
                Mode = mode,
                Benchmark = benchmark,
                Car = car,
                OpposingAi = roster.Entries.Where(e => e.Kind == ActorKind.Ai).Select(e => e.DriverId).ToList(),
                Rules = new RaceEventRules
                {
                    Kind = "campaign",
                    Mode = mode,
                    StageId = stage.Id,
                    StageNumber = stage.Number,
                    CarCapPi = stage.MaxPI,
                    Contact = ContactPolicy.LightContact,
                    BenchmarkTargetMs = benchmark.TargetTimeMs,
                    HardTimeoutMs = benchmark.HardTimeoutMs,
                    RequiresBeatingFeaturedRival = benchmark.RequiresBeatingFeaturedRival,
                },
            };
        }

        /// <summary>
        /// A Freeplay event. Opponents are authored rivals, as online (spec §13): <paramref name="namedRival"/> first when the
        /// player picked one (the lead — CH38/CH73 read its archetype), then a shuffled pool without the finale-only rivals.
        /// </summary>
        public static LocalEventPlan Freeplay(ContentCatalogue catalogue, CourseDef course, bool timeAttack, int opponents, int carCapPi, LocalCarChoice car,
            string namedRival = null, Random random = null)
        {
            var plan = new LocalEventPlan
            {
                EventId = NewEventId(),
                Kind = timeAttack ? EventKind.FreeplayTimeTrial : course.Format == "circuit" ? EventKind.FreeplayCircuit : EventKind.FreeplaySprint,
                CourseId = course.Id,
                Car = car,
                FreeplayFormat = timeAttack ? "time-attack" : course.Format,
                Rules = new RaceEventRules
                {
                    Kind = "freeplay",
                    Contact = timeAttack ? ContactPolicy.NonContact : ContactPolicy.LightContact,
                    StageNumber = 10,
                    CarCapPi = carCapPi,
                },
            };
            int count = timeAttack ? 0 : Math.Max(0, Math.Min(opponents, Limits.MaxRaceVehicles - 1));
            if (count > 0 && !string.IsNullOrEmpty(namedRival) && FinalRivals.Allowed(namedRival, AiPlacementContext.FreeplayOpponent) &&
                catalogue.TryRival(namedRival, out _))
                plan.OpposingAi.Add(namedRival);
            List<string> pool = catalogue.Rivals.Select(r => r.Id)
                .Where(id => !plan.OpposingAi.Contains(id) && FinalRivals.Allowed(id, AiPlacementContext.RandomPool)).ToList();
            random = random ?? new Random();
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                string t = pool[i];
                pool[i] = pool[j];
                pool[j] = t;
            }
            plan.OpposingAi.AddRange(pool.Take(count - plan.OpposingAi.Count));
            return plan;
        }

        /// <summary>A challenge trial: solo, non-contact, on the trial's course and conditions in its supplied loaner (never a garage car).</summary>
        public static LocalEventPlan Trial(ChallengeTrialDef trial) => new LocalEventPlan
        {
            EventId = NewEventId(),
            Kind = EventKind.FreeplayTimeTrial,
            CourseId = trial.Course,
            Car = new LocalCarChoice { ModelId = trial.Loaner.Car },
            FreeplayFormat = "trial-" + trial.Id,
            TrialId = trial.Id,
            Rules = new RaceEventRules
            {
                Kind = "freeplay", Contact = ContactPolicy.NonContact, StageNumber = 10,
                CarCapPi = trial.Loaner.PiCap > 0 ? trial.Loaner.PiCap : PerformanceIndex.Max,
                Surface = trial.Conditions == "course" ? null : trial.Conditions, DriftRanking = trial.JudgesDrift,
            },
        };

        /// <summary>The verdict of the last challenge trial run (shown on its screen and the results).</summary>
        public static TrialVerdict LastTrialVerdict;

        /// <summary>The Hinode Campus tutorial drive: no opponents; the first completion pays once.</summary>
        public static LocalEventPlan Tutorial(CourseDef course, LocalCarChoice car) => new LocalEventPlan
        {
            EventId = NewEventId(),
            Kind = EventKind.Tutorial,
            CourseId = course.Id,
            Car = car,
            Rules = new RaceEventRules { Kind = "tutorial", Contact = ContactPolicy.LightContact, StageNumber = 1 },
        };

        static string NewEventId() =>"le-" + Guid.NewGuid().ToString("N").Substring(0, 20);

        public static RecordRuleset Ruleset(RaceEventRules rules, string courseRevision) => new RecordRuleset
        {
            CourseRevision = courseRevision ?? "",
            PhysicsVersion = RaceSimulation.PhysicsVersion,
            ScoringVersion = RaceSimulation.ScoringVersion,
            Contact = rules.Contact,
            Conditions = string.IsNullOrEmpty(rules.Surface) ? "dry" : rules.Surface,
            CarCapPi = rules.CarCapPi,
        };

        /// <summary>The local human's classified result as progression facts. Null when the race produced no human result.</summary>
        public static LocalEventFacts Facts(LocalSession s, LocalEventPlan plan, IReadOnlyList<RaceEntrantResult> results, string courseRevision)
        {
            RaceEntrantResult me = results?.FirstOrDefault(r => r.Entrant.Human);
            if (me == null) return null;
            CourseDef course = s.Catalogue.Course(plan.CourseId);
            var facts = new LocalEventFacts
            {
                EventId = plan.EventId,
                Kind = plan.Kind,
                CourseId = plan.CourseId,
                StageId = plan.Stage?.Id,
                Mode = plan.Mode,
                CompletedUtc = DateTime.UtcNow,
                Outcome = me.Outcome,
                FinishTimeMicros = me.Outcome == RunOutcome.Finished ? me.FinishTimeMicros : 0,
                Placement = me.Placement,
                Clean = me.Entrant.Progress.Clean,
                CheckpointFraction = me.CheckpointFraction,
                ActiveProgressVerified = me.ActiveProgressVerified,
                ActivelyDroveLegalCourse = me.ActivelyDroveLegalCourse,
                RawDriftScore = me.RawDriftScore,
                ContractsPassed = System.Math.Max(0, me.ContractsPassed),
                CarModelId = plan.Car.ModelId,
                CarInstanceId = plan.Car.InstanceId,
                Loaner = plan.Car.Loaner,
                Benchmark = plan.Benchmark,
                BeatFeaturedRival = me.BeatFeaturedRival,
                // The featured rival is built into the grid before the start; if it could not be, the race never runs.
                FeaturedRivalStarted = plan.Kind != EventKind.CampaignStage || results.Any(r => r.Entrant.Roster.Role == "featured"),
                OpposingAi = plan.OpposingAi.ToList(),
                Tied = me.Tied,
            };
            // The same race predicates the game server evaluates online, from this run's facts.
            facts.ChallengesCompleted.AddRange(Net.ChallengePredicates.Evaluate(plan.CourseId, me.Entrant.Progress, me.Entrant.Drift,
                plan.FreeplayFormat, plan.Rules?.Surface, me.Entrant.GateRun, me.Entrant.Racecraft, me.Entrant.ZoneChains));
            // A challenge trial: judged by the Core TrialJudge from this run's facts; the profile keeps the pass.
            if (!string.IsNullOrEmpty(plan.TrialId) && s.Catalogue.ChallengeTrials.Find(plan.TrialId) is ChallengeTrialDef trial)
            {
                LastTrialVerdict = TrialJudge.Judge(trial, new TrialRunFacts
                {
                    Finished = me.Outcome == RunOutcome.Finished,
                    TimeMs = me.Outcome == RunOutcome.Finished ? me.FinishTimeMicros / 1000 : 0,
                    Resets = me.Entrant.Progress.Resets, WallImpacts = me.Entrant.Progress.WallIncidents,
                    HandbrakeSeconds = me.Entrant.Progress.HandbrakeSeconds, OffPavedSeconds = me.Entrant.Progress.OffPavedSeconds,
                    ChallengeGatesTouched = me.Entrant.GateRun != null && me.Entrant.GateRun.AllTouched(trial.Challenge),
                    ChallengeGates = me.Entrant.GateRun?.Count(trial.Challenge) ?? 0,
                    DriftRaw = (long)Math.Floor(me.Entrant.Drift.BankedRaw),
                    ZonesBanked = me.Entrant.Drift.ZonesBanked.Count,
                    ZonesTotal = s.Catalogue.DriftZones.TryGetValue(trial.Course, out int zones) ? zones : 0,
                    DroveLoaner = !string.IsNullOrEmpty(plan.TrialBuildHash) && plan.Car.Loaner && plan.Car.ModelId == trial.Loaner.Car,
                });
                facts.TrialId = trial.Id;
                facts.TrialPassed = LastTrialVerdict.Passed;
            }
            IReadOnlyList<Core.Story.CrewIntroduction> crews = NightSignal.Content.ContentLibrary.Load()?.Story?.Crews;
            if (me.Outcome == RunOutcome.Finished && crews != null && Core.Story.DiaryChallenges.AllCrewsRead(s.Profile.DiaryRead, crews) &&
                Core.Story.DiaryChallenges.RacedCrewMember(plan.OpposingAi, s.Catalogue, crews))
                facts.ChallengesCompleted.Add(Core.Story.DiaryChallenges.OtherSideOfTheCard);
            if (plan.Kind == EventKind.Tutorial || plan.TrialId != null) return facts; // lessons and trials keep no personal record
            RecordRuleset rules = Ruleset(plan.Rules, courseRevision);
            RecordKey key = plan.Kind == EventKind.CampaignStage
                ? RecordKey.ForCampaignStage(ProgressionDomain.Local, plan.Stage, course, plan.Mode, MetricKind.ElapsedTime, rules)
                : RecordKey.ForFreeplay(ProgressionDomain.Local, course.Id, plan.FreeplayFormat, MetricKind.ElapsedTime, rules);
            facts.Records.Add(new RecordCandidate
            {
                Key = key,
                Value = me.Outcome == RunOutcome.Finished ? RaceClassification.ToReportedMillis(me.FinishTimeMicros) : (long?)null,
            });
            return facts;
        }
    }
}
