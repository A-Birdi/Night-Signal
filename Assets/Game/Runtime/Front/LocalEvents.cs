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
        /// <summary>A challenge cup's leg (0-based; -1 for every other event).</summary>
        public int TrialLeg = -1;
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

        /// <summary>
        /// A challenge trial on the trial's course and conditions in its supplied loaner (never a garage car): solo and
        /// non-contact, or — a racecraft trial — a race against its fixed field (<paramref name="courseFormat"/>: the course's
        /// sprint / circuit format).
        /// </summary>
        public static LocalEventPlan Trial(ChallengeTrialDef trial, string courseFormat = "sprint")
        {
            LocalEventPlan plan = SoloTrial(trial);
            if (!trial.IsRace) return plan;
            plan.Kind = courseFormat == "circuit" ? EventKind.FreeplayCircuit : EventKind.FreeplaySprint;
            plan.Rules.Contact = ContactPolicy.LightContact;
            plan.Rules.TrialField = trial.Field;
            plan.Rules.HumansStartLast = trial.PlayerStartsLast;
            plan.OpposingAi.AddRange(trial.Field.Select((c, i) => string.IsNullOrEmpty(c.Rival) ? $"ai-{i + 1}" : c.Rival));
            return plan;
        }

        /// <summary>A challenge cup's leg: its loaner, solo and non-contact, on that leg's course and conditions.</summary>
        public static LocalEventPlan CupLeg(ChallengeTrialDef trial, int leg)
        {
            LocalEventPlan plan = SoloTrial(trial);
            TrialCupLeg l = trial.Legs[leg];
            plan.CourseId = l.Course;
            plan.Rules.Surface = l.Conditions == "course" ? null : l.Conditions;
            plan.TrialLeg = leg;
            return plan;
        }

        /// <summary>The challenge cup in progress — one continuous session: its trial and every leg run so far (null / empty: none).</summary>
        public static string CupTrialId { get; private set; }
        public static readonly List<TrialCupLegFacts> CupLegsRun = new List<TrialCupLegFacts>();

        /// <summary>Starts a challenge cup's session (its first leg follows).</summary>
        public static void BeginCup(string trialId)
        {
            CupTrialId = trialId;
            CupLegsRun.Clear();
        }

        /// <summary>Ends the session early (the player left the cup): its legs no longer count.</summary>
        public static void AbandonCup()
        {
            CupTrialId = null;
            CupLegsRun.Clear();
        }

        static LocalEventPlan SoloTrial(ChallengeTrialDef trial) => new LocalEventPlan
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
                SectionStartGate = trial.HasSection ? trial.SectionStartGate : null, SectionEndGate = trial.HasSection ? trial.SectionEndGate : null,
                ManualGearbox = trial.ManualGearbox,
            },
        };

        /// <summary>The verdict of the last challenge trial run (shown on its screen and the results).</summary>
        public static TrialVerdict LastTrialVerdict;
        /// <summary>The player's racecraft log of the last racecraft trial (passes, marked zones, pressure sectors — tours print it).</summary>
        public static List<string> LastTrialRacecraftLog = new List<string>();

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
            if (!string.IsNullOrEmpty(plan.TrialId) && s.Catalogue.ChallengeTrials.Find(plan.TrialId) is ChallengeTrialDef cupTrial && cupTrial.IsCup)
            {
                // A challenge cup leg: kept in the session; the cup is judged after its last leg, or ends at a leg not finished.
                var leg = new TrialCupLegFacts
                {
                    Course = plan.CourseId, Finished = me.Outcome == RunOutcome.Finished,
                    TimeMs = me.Outcome == RunOutcome.Finished ? me.FinishTimeMicros / 1000 : 0,
                    Resets = me.Entrant.Progress.Resets, WallImpacts = me.Entrant.Progress.WallIncidents,
                };
                if (CupTrialId != cupTrial.Id || plan.TrialLeg != CupLegsRun.Count) BeginCup(cupTrial.Id); // only a session's next leg continues it
                CupLegsRun.Add(leg);
                bool last = CupLegsRun.Count >= cupTrial.Legs.Count || !leg.Finished;
                LastTrialVerdict = last
                    ? TrialJudge.Judge(cupTrial, new TrialRunFacts
                    {
                        Finished = leg.Finished, CupLegs = CupLegsRun.ToArray(),
                        DroveLoaner = !string.IsNullOrEmpty(plan.TrialBuildHash) && plan.Car.Loaner && plan.Car.ModelId == cupTrial.Loaner.Car,
                    })
                    : TrialJudge.JudgeCupLeg(cupTrial, CupLegsRun.Count - 1, leg);
                if (last) CupTrialId = null; // the session is over (its legs stay readable until the next cup)
                facts.TrialId = cupTrial.Id;
                facts.TrialPassed = last && LastTrialVerdict.Passed;
                LastTrialRacecraftLog = new List<string>();
            }
            else if (!string.IsNullOrEmpty(plan.TrialId) && s.Catalogue.ChallengeTrials.Find(plan.TrialId) is ChallengeTrialDef trial)
            {
                LastTrialVerdict = TrialJudge.Judge(trial, new TrialRunFacts
                {
                    Finished = me.Outcome == RunOutcome.Finished,
                    TimeMs = me.Outcome == RunOutcome.Finished ? me.FinishTimeMicros / 1000 : 0,
                    Resets = me.Entrant.Progress.Resets, WallImpacts = me.Entrant.Progress.WallIncidents,
                    HandbrakeSeconds = me.Entrant.Progress.HandbrakeSeconds, OffPavedSeconds = me.Entrant.Progress.OffPavedSeconds,
                    ChallengeGatesTouched = me.Entrant.GateRun != null && me.Entrant.GateRun.AllTouched(trial.Challenge),
                    ChallengeGates = me.Entrant.GateRun?.Count(trial.Challenge) ?? 0,
                    DefenceZonesKept = me.Entrant.GateRun != null && me.Entrant.GateRun.DefenceKept(trial.Challenge),
                    DefenceZones = me.Entrant.GateRun?.DefenceCount(trial.Challenge) ?? 0,
                    SectionMs = me.Entrant.SectionMicros > 0 ? me.Entrant.SectionMicros / 1000 : 0,
                    StoryRecords = trial.RequiredStoryRecords > 0 ? DiaryScreen.Build(s.Profile, NightSignal.Content.ContentLibrary.Load()).Count(e => e.Kind == "record") : 0,
                    MergeKept = me.Entrant.Racecraft != null && me.Entrant.Racecraft.Merges.Any(m => m.Challenge == trial.Challenge && m.Kept),
                    ShiftOffsets = trial.ShiftGates.Count == 0 ? null : ShiftWindowJudge.Offsets(
                        trial.ShiftGates.Select(g => NightSignal.Track.CourseRuntime.Active?.Track?.Gates.FirstOrDefault(x => x.Id == g)?.StartMetres ?? -1e6f).ToList(), me.Entrant.Upshifts),
                    RecoveriesAlternating = me.Entrant.ZoneChains != null && me.Entrant.ZoneChains.AlternatingRecoveries(trial.Challenge),
                    Spun = me.Entrant.ZoneChains != null && me.Entrant.ZoneChains.Spun,
                    Recoveries = me.Entrant.ZoneChains?.Recoveries.Count ?? 0,
                    BrakeGates = trial.Targets.Brakes.Select(x => x.Gate).Where(g => me.Entrant.GateRun?.SpeedFact(g)?.Crossed == true).ToArray(),
                    BrakeFacts = trial.Targets.Brakes.Select(x => x.Gate).Where(g => me.Entrant.GateRun?.SpeedFact(g)?.Crossed == true)
                        .Select(g => me.Entrant.GateRun.SpeedFact(g).Value).ToArray(),
                    ExitGates = trial.Targets.ExitFloors.Select(x => x.Gate).Where(g => me.Entrant.GateRun?.SpeedFact(g)?.Crossed == true).ToArray(),
                    ExitKmh = trial.Targets.ExitFloors.Select(x => x.Gate).Where(g => me.Entrant.GateRun?.SpeedFact(g)?.Crossed == true)
                        .Select(g => me.Entrant.GateRun.SpeedFact(g).Value.SpeedKmh).ToArray(),
                    DriftRaw = (long)Math.Floor(me.Entrant.Drift.BankedRaw),
                    ZonesBanked = me.Entrant.Drift.ZonesBanked.Count,
                    ZonesTotal = s.Catalogue.DriftZones.TryGetValue(trial.Course, out int zones) ? zones : 0,
                    DroveLoaner = !string.IsNullOrEmpty(plan.TrialBuildHash) && plan.Car.Loaner && plan.Car.ModelId == trial.Loaner.Car,
                    Placement = me.Outcome == RunOutcome.Finished ? me.Placement : 0,
                    CarContacts = me.Entrant.Progress.VehicleContacts,
                    CheckpointCut = me.Entrant.Progress.CorridorCut,
                    CleanZonePass = me.Entrant.Racecraft != null && me.Entrant.Racecraft.ZonePasses.Any(z => z.Challenge == trial.Challenge && z.TouchFree),
                    PressureSectorMs = me.Entrant.Racecraft == null ? 0 : me.Entrant.Racecraft.DefenceRuns.Where(d => d.Challenge == trial.Challenge && d.PressureHeld && !d.WallTouched)
                        .Select(d => (long)Math.Ceiling(d.Seconds * 1000)).DefaultIfEmpty(0).Min(),
                    ZonePassRoles = me.Entrant.Racecraft == null ? new string[0] : me.Entrant.Racecraft.ZonePasses.Where(z => z.Challenge == trial.Challenge)
                        .Select(z => results.FirstOrDefault(r => r.Entrant.Roster.Index == z.Passed)?.Entrant.Roster.Role ?? "").ToArray(),
                });
                facts.TrialId = trial.Id;
                facts.TrialPassed = LastTrialVerdict.Passed;
                LastTrialRacecraftLog = me.Entrant.Racecraft?.PassLog.ToList() ?? new List<string>();
                LastTrialRacecraftLog.Add($"car contacts {me.Entrant.Progress.VehicleContacts}, walls {me.Entrant.Progress.WallIncidents}, resets {me.Entrant.Progress.Resets}, " +
                                          $"marshal recoveries {me.Entrant.AutoRecoveries}, placement P{me.Placement}");
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
