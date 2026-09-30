using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    /// <summary>A trial's supplied car: a model and its fixed parts (slot → part id) — never the player's garage build.</summary>
    public sealed class TrialLoaner
    {
        public string Car = "";
        public Dictionary<string, string> Parts = new Dictionary<string, string>();
        /// <summary>The class cap the predicate names (0 = none): the resolved loaner must be within it.</summary>
        public int PiCap;
    }

    /// <summary>
    /// One scripted car in a racecraft trial's fixed field (slice 3: CH40's fixed C17 race, CH41's six-entrant C18 race): its
    /// car (stock), an optional rival identity (name and tendencies; "" = a generic driver), what the challenge asks of it,
    /// and its pace against the tuned AI (0 = the default).
    /// </summary>
    public sealed class TrialFieldCar
    {
        public string Car = "";
        public string Rival = "";
        /// <summary>field | pacing | pressure | merge.</summary>
        public string Role = "field";
        public float Pace;

        public static readonly string[] Roles = { "field", "pacing", "pressure", "merge" };
    }

    /// <summary>
    /// One leg of a challenge cup (slice 4: CH14, CH42, CH69, CH72): its course and conditions, and the time to beat there
    /// (measured: the loaner's autopilot time × the cup's <see cref="ChallengeTrialDef.LegFactor"/>; 0 = no time judged).
    /// </summary>
    public sealed class TrialCupLeg
    {
        public string Course = "";
        public string Conditions = "course";
        public long TimeMs;
    }

    /// <summary>Conditions of a legal trial run beyond finishing.</summary>
    public sealed class TrialRules
    {
        public bool NoReset;
        /// <summary>Meaningful wall impacts allowed (-1 = not judged).</summary>
        public int MaxWallImpacts = -1;
        /// <summary>No handbrake from the start (CH25).</summary>
        public bool NoHandbrake;
        /// <summary>Every judged drift zone of the course banked at least once (CH30: "a valid chain in every judged sector").</summary>
        public bool BankEveryZone;
        /// <summary>All four tyres on the paved road the whole run (CH13): a shoulder touch fails the trial, not the race.</summary>
        public bool AllTyresPaved;
        /// <summary>Every route gate tagged with the trial's challenge touched (CH15: the three final-sector apex gates on C25).</summary>
        public bool AllChallengeGates;
        /// <summary>Racecraft trials: first across the line (CH41).</summary>
        public bool Win;
        /// <summary>Racecraft trials: no car-to-car contact at all (CH40, CH41).</summary>
        public bool NoCarContact;
        /// <summary>No checkpoint cut (CH40).</summary>
        public bool NoCheckpointCut;
        /// <summary>
        /// A pass inside the course's overtake zone tagged with the trial's challenge, the place held to its retain gate (or
        /// 3 s), with no car touched from 2 s before the pass (CH40's marked clean braking-zone overtake).
        /// </summary>
        public bool CleanZonePass;
        /// <summary>
        /// A pass of the field car in this role ("pacing": CH36's pacing rival) inside the challenge's marked zone or lane, the
        /// gain held to its gate ("" = not judged).
        /// </summary>
        public string ZonePassRole = "";
        /// <summary>
        /// CH39: a pass through the challenge's defence zone (one sector) with the field's pressure car within 1 s behind the
        /// whole way, no barrier touched in it, at the published sector pace (<see cref="TrialTargets.SectorTimeMs"/>).
        /// </summary>
        public bool PressureSector;
        /// <summary>Every defence zone tagged with the challenge driven start to end inside the legal corridor (CH43's defence/exit gates).</summary>
        public bool AllDefenceZones;
        /// <summary>Every exit-speed gate tagged with the challenge cleared at its measured floor on every pass (CH53's exit criteria).</summary>
        public bool ChallengeExits;
        /// <summary>Every braking zone tagged with the challenge driven inside its measured trail-brake envelope (CH07).</summary>
        public bool BrakeEnvelope;
        /// <summary>CH23: every transition zone of the challenge recovered in turn, alternating directions, no spin.</summary>
        public bool AlternatingRecoveries;
    }

    /// <summary>
    /// A measured trail-brake envelope at one braking zone (CH07): braking begun by <see cref="BrakeByMetres"/>, the brake still
    /// held (trailed into the bend) until at least <see cref="ReleaseAfterMetres"/>, and the exit speed inside the window.
    /// </summary>
    public sealed class TrialBrakeEnvelope
    {
        public string Gate = "";
        public float BrakeByMetres, ReleaseAfterMetres, MinExitKmh, MaxExitKmh;
    }

    /// <summary>A measured exit-speed floor at one route gate (a drill's exit criterion).</summary>
    public sealed class TrialExitFloor
    {
        public string Gate = "";
        public float Kmh;
    }

    /// <summary>Published targets (measured, see the file's method); 0 = not judged.</summary>
    public sealed class TrialTargets
    {
        /// <summary>The finish time to beat (strictly faster).</summary>
        public long TimeMs;
        /// <summary>The banked raw drift score to reach.</summary>
        public long DriftRaw;
        /// <summary>The autopilot drift skill of the measured reference run (drift trials; how a replay reproduces it).</summary>
        public float ReferenceDriftSkill;
        /// <summary>
        /// The margin the autopilot's line kept from the road's edge in the measured reference run (0 = the validator's own
        /// 1.3 m): a trial whose rules the validator's line breaks (CH13 — it cuts two apexes onto the shoulder) is measured with
        /// the fastest wider margin that keeps them, so the target is shown reachable within its own rules.
        /// </summary>
        public float ReferenceEdgeMargin;
        /// <summary>
        /// The autopilot's corner-speed pace in the measured reference run (0 = the validator's own): a drill whose marked apex the
        /// validator's pace cannot hold (CH53's long left) is measured at the fastest pace that touches every marked gate, so its
        /// exit floors come from a run that meets the turn-in criteria too.
        /// </summary>
        public float ReferencePaceScale;
        /// <summary>The time through the challenge's defence zone to match (CH39's "valid Silver pace"; measured, the tier's factor).</summary>
        public long SectorTimeMs;
        /// <summary>Exit-speed floors at the challenge's exit gates (a drill's exit criteria; measured: 0.95 × the loaner's slowest exit there).</summary>
        public List<TrialExitFloor> ExitFloors = new List<TrialExitFloor>();
        /// <summary>Trail-brake envelopes at the challenge's braking zones (measured from the loaner's reference trace).</summary>
        public List<TrialBrakeEnvelope> Brakes = new List<TrialBrakeEnvelope>();
    }

    /// <summary>One challenge trial (docs/CHALLENGE_TRIALS.md): a fixed course, loaner, rules and targets for one challenge.</summary>
    public sealed class ChallengeTrialDef
    {
        public string Id = "", Challenge = "", Title = "", Course = "", Tier = "";
        /// <summary>
        /// What is judged: against measured targets "time", "drift" or "time+drift"; or "race" — a racecraft trial against
        /// its fixed <see cref="Field"/>, judged by its rules alone (no targets).
        /// </summary>
        public string Kind = "time";
        public bool JudgesTime => Kind == "time" || Kind == "time+drift";
        public bool JudgesDrift => Kind == "drift" || Kind == "time+drift";
        public bool IsRace => Kind == "race";
        /// <summary>A drill: a solo run judged by its rules alone — gates, exits — with no time or score target (CH53's guided corners).</summary>
        public bool IsDrill => Kind == "drill";
        /// <summary>A challenge cup: <see cref="Legs"/> run in order in the loaner, one continuous session, judged as a whole.</summary>
        public bool IsCup => Kind == "cup";
        /// <summary>A challenge cup's legs, in order (empty otherwise).</summary>
        public List<TrialCupLeg> Legs = new List<TrialCupLeg>();
        /// <summary>
        /// A cup's leg times: this × the loaner's measured time on each leg (CH42's Gold benchmark 1.02, CH72's generous Silver pace,
        /// CH69's generous limits; 0 = the legs are not timed, CH14).
        /// </summary>
        public double LegFactor;
        /// <summary>
        /// The time target is this rival's own practice run in the loaner (its driver profile at <see cref="ReferenceStage"/>; the time
        /// itself, no factor) instead of the validator's — CH43 "beat R32's C20 Gold practice reference"; "" = the validator's.
        /// </summary>
        public string ReferenceRival = "";
        public int ReferenceStage;
        /// <summary>
        /// A timed section instead of the whole run (slice 5: T00's comparison routes and configurations, CH52 and CH58): the
        /// route gates that start and end it ("" = the finish time is judged). The time target is the section's.
        /// </summary>
        public string SectionStartGate = "", SectionEndGate = "";
        /// <summary>
        /// The story records that must already be collected through Normal progression (CH74: all six radio / timing-slip
        /// records; 0 = none). Judged from the profile's campaign clears, as the race diary shows them.
        /// </summary>
        public int RequiredStoryRecords;
        /// <summary>The trial races its course's authored rival reference ghost, whose time is its target (CH74's C24 reference).</summary>
        public bool RaceRivalReference;
        public bool HasSection => !string.IsNullOrEmpty(SectionStartGate) && !string.IsNullOrEmpty(SectionEndGate);
        /// <summary>A racecraft trial's fixed AI field, in grid order (empty for time and drift trials, which run alone).</summary>
        public List<TrialFieldCar> Field = new List<TrialFieldCar>();
        /// <summary>The player starts from the last grid slot, behind the whole field (CH41 "from the last grid position").</summary>
        public bool PlayerStartsLast;
        /// <summary>"course" = the course's own conditions; otherwise a surface (e.g. "wet").</summary>
        public string Conditions = "course";
        /// <summary>Laps for a circuit (0 = the course's own format).</summary>
        public int Laps;
        public TrialLoaner Loaner = new TrialLoaner();
        public TrialRules Rules = new TrialRules();
        public TrialTargets Targets = new TrialTargets();
        /// <summary>Trials sharing a group must all be passed for the challenge (CH54's two drive layouts); "" = this trial alone.</summary>
        public string Group = "";
        /// <summary>What the player is told before the start (the loaner, the rules), beside the challenge's predicate.</summary>
        public string Brief = "";
        /// <summary>
        /// The trial races its fixed Gold ghost — the measured reference run, recorded with the targets (CH13): the time target
        /// is the ghost's own time.
        /// </summary>
        public bool Ghost;

        /// <summary>Measured targets exist for everything this trial judges.</summary>
        public bool Published => (!JudgesTime || Targets.TimeMs > 0) && (!JudgesDrift || Targets.DriftRaw > 0) && (!Rules.PressureSector || Targets.SectorTimeMs > 0) &&
                                 (!IsCup || LegFactor <= 0 || Legs.All(l => l.TimeMs > 0)) &&
                                 (!Rules.ChallengeExits || (Targets.ExitFloors.Count > 0 && Targets.ExitFloors.All(x => x.Kmh > 0f))) &&
                                 (!Rules.BrakeEnvelope || (Targets.Brakes.Count > 0 && Targets.Brakes.All(x => x.BrakeByMetres > 0f && x.MaxExitKmh > 0f)));
    }

    public sealed class ChallengeTrialsFile
    {
        public string Schema = "", Method = "";
        public List<ChallengeTrialDef> Trials = new List<ChallengeTrialDef>();

        public ChallengeTrialDef Find(string id) => Trials.FirstOrDefault(t => t.Id == id);

        /// <summary>Every trial that must be passed for <paramref name="challenge"/> (one, or its whole group).</summary>
        public IReadOnlyList<ChallengeTrialDef> ForChallenge(string challenge) => Trials.Where(t => t.Challenge == challenge).ToList();
    }

    /// <summary>One finished (or abandoned) leg of a challenge cup, from that race's facts.</summary>
    public struct TrialCupLegFacts
    {
        public string Course;
        public bool Finished;
        public long TimeMs;
        public int Resets, WallImpacts;
    }

    /// <summary>What happened in one trial run, from the race's authoritative facts.</summary>
    public struct TrialRunFacts
    {
        public bool Finished;
        public long TimeMs;
        public int Resets, WallImpacts;
        /// <summary>Seconds with the handbrake held after the start.</summary>
        public float HandbrakeSeconds;
        /// <summary>Seconds with a tyre beyond the paved road (on the shoulder or off it).</summary>
        public float OffPavedSeconds;
        /// <summary>Every route gate tagged with the trial's challenge was touched, and how many there are.</summary>
        public bool ChallengeGatesTouched;
        public int ChallengeGates;
        public long DriftRaw;
        public int ZonesBanked, ZonesTotal;
        /// <summary>The run drove this trial's loaner (the runtime resolved it; any other car or build fails the trial).</summary>
        public bool DroveLoaner;
        /// <summary>Racecraft trials: the finishing position (1 = won; 0 = not classified), car-to-car contacts, a checkpoint cut.</summary>
        public int Placement, CarContacts;
        public bool CheckpointCut;
        /// <summary>A touch-free pass inside the challenge's marked overtake zone, the place held (see <see cref="TrialRules.CleanZonePass"/>).</summary>
        public bool CleanZonePass;
        /// <summary>The roles of the field cars passed in the challenge's marked zone or lane with the gain held.</summary>
        public string[] ZonePassRoles;
        /// <summary>The fastest pass through the challenge's defence zone with the pressure car within 1 s all the way and no barrier touched (ms; 0 = none).</summary>
        public long PressureSectorMs;
        /// <summary>A challenge cup: every leg run so far in this session, in order (the verdict needs all of them).</summary>
        public TrialCupLegFacts[] CupLegs;
        /// <summary>Every defence zone of the challenge driven inside the legal corridor, and how many there are.</summary>
        public bool DefenceZonesKept;
        public int DefenceZones;
        /// <summary>A section trial: the first time the section was driven start to end without a reset (ms; 0 = never).</summary>
        public long SectionMs;
        /// <summary>The story records the player has collected through Normal progression (for a trial that requires them).</summary>
        public int StoryRecords;
        /// <summary>The slowest exit over each of the challenge's exit gates (km/h; a gate never crossed is absent).</summary>
        public string[] ExitGates;
        public float[] ExitKmh;
        /// <summary>What the car did at each of the challenge's braking zones (the gate judge's facts; a zone never crossed is absent).</summary>
        public string[] BrakeGates;
        public GateSpeedFact[] BrakeFacts;
        /// <summary>CH23: the challenge's zones recovered in turn with alternating directions and no spin; the recoveries made, and a spin.</summary>
        public bool RecoveriesAlternating, Spun;
        public int Recoveries;
    }

    public sealed class TrialVerdict
    {
        public bool Passed;
        /// <summary>Each judged condition in order: (passed, what).</summary>
        public List<KeyValuePair<bool, string>> Checks = new List<KeyValuePair<bool, string>>();
        /// <summary>Each check in words ("ok:" / "MISSED:") — the game font has no check-mark glyphs.</summary>
        public string Summary => string.Join(" · ", Checks.Select(c => (c.Key ? "ok: " : "MISSED: ") + c.Value));
    }

    /// <summary>Judges trial runs and the challenges they complete. Engine-free: the game server and the Local race agree.</summary>
    public static class TrialJudge
    {
        public static TrialVerdict Judge(ChallengeTrialDef t, TrialRunFacts f)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            var v = new TrialVerdict();
            void Check(bool ok, string what) => v.Checks.Add(new KeyValuePair<bool, string>(ok, what));
            Check(f.DroveLoaner, "the supplied loaner");
            if (t.RequiredStoryRecords > 0)
                Check(f.StoryRecords >= t.RequiredStoryRecords,
                    $"the {t.RequiredStoryRecords} story records collected through Normal progression ({f.StoryRecords} of {t.RequiredStoryRecords})");
            if (t.IsCup) return JudgeCup(t, f, v);
            Check(f.Finished, "a valid finish");
            if (t.Targets.TimeMs > 0 && t.HasSection)
                Check(f.Finished && f.SectionMs > 0 && f.SectionMs < t.Targets.TimeMs,
                    $"{t.SectionStartGate} to {t.SectionEndGate} faster than {Clock(t.Targets.TimeMs)} ({(f.SectionMs > 0 ? Clock(f.SectionMs) : "not driven")})");
            else if (t.Targets.TimeMs > 0)
                Check(f.Finished && f.TimeMs > 0 && f.TimeMs < t.Targets.TimeMs, $"faster than {Clock(t.Targets.TimeMs)} ({(f.TimeMs > 0 ? Clock(f.TimeMs) : "no time")})");
            if (t.Targets.DriftRaw > 0)
                Check(f.DriftRaw >= t.Targets.DriftRaw, $"{t.Targets.DriftRaw:N0} raw drift banked ({f.DriftRaw:N0})");
            if (t.Rules.NoReset) Check(f.Resets == 0, $"no reset ({f.Resets})");
            if (t.Rules.MaxWallImpacts >= 0)
                Check(f.WallImpacts <= t.Rules.MaxWallImpacts, t.Rules.MaxWallImpacts == 0 ? $"no wall impact ({f.WallImpacts})" : $"at most {t.Rules.MaxWallImpacts} wall impact{(t.Rules.MaxWallImpacts == 1 ? "" : "s")} ({f.WallImpacts})");
            if (t.Rules.NoHandbrake) Check(f.HandbrakeSeconds <= 0f, "no handbrake after the start" + (f.HandbrakeSeconds > 0f ? $" (held {f.HandbrakeSeconds:F1} s)" : ""));
            if (t.Rules.AllChallengeGates)
                Check(f.ChallengeGates > 0 && f.ChallengeGatesTouched, $"every marked gate touched ({f.ChallengeGates} of them)");
            if (t.Rules.AllTyresPaved)
                Check(f.OffPavedSeconds <= 0f, "all tyres on the paved road" + (f.OffPavedSeconds > 0f ? $" (off it {f.OffPavedSeconds:F1} s)" : ""));
            if (t.Rules.BankEveryZone) Check(f.ZonesTotal > 0 && f.ZonesBanked >= f.ZonesTotal, $"a chain banked in every judged zone ({f.ZonesBanked}/{f.ZonesTotal})");
            if (t.Rules.AllDefenceZones) Check(f.DefenceZones > 0 && f.DefenceZonesKept, $"every marked defence gate inside the legal corridor ({f.DefenceZones} of them)");
            if (t.Rules.AlternatingRecoveries)
                Check(f.RecoveriesAlternating, $"alternating recoveries, one in each marked zone in turn, no spin or reset between them ({f.Recoveries} recoveries in the run)");
            if (t.Rules.BrakeEnvelope)
                foreach (TrialBrakeEnvelope env in t.Targets.Brakes)
                {
                    int i = f.BrakeGates == null ? -1 : Array.IndexOf(f.BrakeGates, env.Gate);
                    GateSpeedFact b = i >= 0 && f.BrakeFacts != null && i < f.BrakeFacts.Length ? f.BrakeFacts[i] : default;
                    bool on = b.Braked && b.BrakeOnMetres >= 0f && b.BrakeOnMetres <= env.BrakeByMetres;
                    bool trailed = b.Braked && (b.ReleaseMetres < 0f || b.ReleaseMetres >= env.ReleaseAfterMetres);
                    bool exit = b.ExitKmh >= env.MinExitKmh && b.ExitKmh <= env.MaxExitKmh;
                    Check(i >= 0 && b.Crossed && on && trailed && exit && b.ResetsInside == 0,
                        $"the trail-brake envelope at {env.Gate}: on by {env.BrakeByMetres:F0} m ({(b.Braked ? b.BrakeOnMetres.ToString("F0") : "never")}), " +
                        $"held to {env.ReleaseAfterMetres:F0} m ({(!b.Braked ? "–" : b.ReleaseMetres < 0f ? "to the end" : b.ReleaseMetres.ToString("F0"))}), " +
                        $"exit {env.MinExitKmh:F0}–{env.MaxExitKmh:F0} km/h ({(i >= 0 && b.Crossed ? b.ExitKmh.ToString("F0") : "not crossed")})");
                }
            if (t.Rules.ChallengeExits)
                foreach (TrialExitFloor floor in t.Targets.ExitFloors)
                {
                    int i = f.ExitGates == null ? -1 : Array.IndexOf(f.ExitGates, floor.Gate);
                    float kmh = i >= 0 && f.ExitKmh != null && i < f.ExitKmh.Length ? f.ExitKmh[i] : 0f;
                    Check(i >= 0 && kmh >= floor.Kmh, $"{floor.Gate} exit at least {floor.Kmh:F1} km/h ({(i >= 0 ? kmh.ToString("F1") : "not crossed")})");
                }
            if (t.Rules.CleanZonePass) Check(f.CleanZonePass, "the marked overtake, clean and held");
            if (t.Rules.PressureSector)
                Check(f.PressureSectorMs > 0 && t.Targets.SectorTimeMs > 0 && f.PressureSectorMs <= t.Targets.SectorTimeMs,
                    $"the sector under pressure, clean, within {Clock(t.Targets.SectorTimeMs)} ({(f.PressureSectorMs > 0 ? Clock(f.PressureSectorMs) : "not held")})");
            if (!string.IsNullOrEmpty(t.Rules.ZonePassRole))
                Check(f.ZonePassRoles != null && Array.IndexOf(f.ZonePassRoles, t.Rules.ZonePassRole) >= 0, $"the {t.Rules.ZonePassRole} car passed in the marked lane, the gain held");
            if (t.Rules.NoCarContact) Check(f.CarContacts == 0, $"no car-to-car contact ({f.CarContacts})");
            if (t.Rules.NoCheckpointCut) Check(!f.CheckpointCut, "no checkpoint cut");
            if (t.Rules.Win) Check(f.Finished && f.Placement == 1, $"first across the line ({(f.Placement > 0 ? "P" + f.Placement : "not classified")})");
            v.Passed = t.Published && v.Checks.All(c => c.Key);
            if (!t.Published) v.Checks.Add(new KeyValuePair<bool, string>(false, "targets not published yet"));
            return v;
        }

        /// <summary>One leg of a challenge cup on its own (shown between legs; the cup's verdict comes after the last leg).</summary>
        public static TrialVerdict JudgeCupLeg(ChallengeTrialDef t, int leg, TrialCupLegFacts f)
        {
            var v = new TrialVerdict();
            void Check(bool ok, string what) => v.Checks.Add(new KeyValuePair<bool, string>(ok, what));
            Check(f.Finished, $"leg {leg + 1} of {t.Legs.Count} ({f.Course}) finished");
            if (leg < t.Legs.Count && t.Legs[leg].TimeMs > 0)
                Check(f.Finished && f.TimeMs > 0 && f.TimeMs < t.Legs[leg].TimeMs, $"faster than {Clock(t.Legs[leg].TimeMs)} ({(f.TimeMs > 0 ? Clock(f.TimeMs) : "no time")})");
            if (t.Rules.MaxWallImpacts == 0) Check(f.WallImpacts == 0, $"no wall impact ({f.WallImpacts})");
            if (t.Rules.NoReset) Check(f.Resets == 0, $"no reset ({f.Resets})");
            v.Passed = false; // a leg never passes the trial on its own
            return v;
        }

        /// <summary>
        /// A challenge cup as a whole: every leg run in order in one session and finished, each leg inside its time, and the
        /// cup's rules summed over all legs (wall impacts, resets). A leg that was not finished ends the cup.
        /// </summary>
        static TrialVerdict JudgeCup(ChallengeTrialDef t, TrialRunFacts f, TrialVerdict v)
        {
            void Check(bool ok, string what) => v.Checks.Add(new KeyValuePair<bool, string>(ok, what));
            TrialCupLegFacts[] legs = f.CupLegs ?? Array.Empty<TrialCupLegFacts>();
            bool inOrder = legs.Length == t.Legs.Count && legs.Select(l => l.Course).SequenceEqual(t.Legs.Select(l => l.Course));
            Check(inOrder && legs.All(l => l.Finished), $"all {t.Legs.Count} legs finished in one session ({legs.Count(l => l.Finished)} of {t.Legs.Count})");
            for (int i = 0; i < t.Legs.Count; i++)
                if (t.Legs[i].TimeMs > 0)
                {
                    bool ran = i < legs.Length && legs[i].Finished && legs[i].TimeMs > 0;
                    Check(ran && legs[i].TimeMs < t.Legs[i].TimeMs,
                        $"{t.Legs[i].Course} faster than {Clock(t.Legs[i].TimeMs)} ({(ran ? Clock(legs[i].TimeMs) : "not run")})");
                }
            int walls = legs.Sum(l => l.WallImpacts), resets = legs.Sum(l => l.Resets);
            if (t.Rules.MaxWallImpacts >= 0)
                Check(walls <= t.Rules.MaxWallImpacts, t.Rules.MaxWallImpacts == 0 ? $"no wall impact in any leg ({walls})" : $"at most {t.Rules.MaxWallImpacts} wall impacts in the cup ({walls})");
            if (t.Rules.NoReset) Check(resets == 0, $"no reset in any leg ({resets})");
            v.Passed = t.Published && v.Checks.All(c => c.Key);
            if (!t.Published) v.Checks.Add(new KeyValuePair<bool, string>(false, "targets not published yet"));
            return v;
        }

        /// <summary>
        /// True once every trial of <paramref name="challenge"/> has been passed (<paramref name="passedTrials"/> includes
        /// this run's pass): one trial alone, or its whole group.
        /// </summary>
        public static bool ChallengeEarned(ChallengeTrialsFile file, string challenge, ICollection<string> passedTrials)
        {
            IReadOnlyList<ChallengeTrialDef> needed = file?.ForChallenge(challenge) ?? Array.Empty<ChallengeTrialDef>();
            return needed.Count > 0 && needed.All(t => passedTrials.Contains(t.Id));
        }

        /// <summary>Content checks: known ids, one challenge per group, unique trial ids, a loaner car, sane rules.</summary>
        public static List<string> Problems(ChallengeTrialsFile file, Func<string, bool> courseExists, Func<string, bool> challengeExists, Func<string, bool> carExists)
        {
            var problems = new List<string>();
            if (file == null) return problems;
            foreach (IGrouping<string, ChallengeTrialDef> dup in file.Trials.GroupBy(t => t.Id).Where(g => g.Count() > 1))
                problems.Add($"trial {dup.Key} is listed {dup.Count()} times");
            foreach (ChallengeTrialDef t in file.Trials)
            {
                if (string.IsNullOrEmpty(t.Id)) problems.Add("a trial has no id");
                if (!challengeExists(t.Challenge ?? "")) problems.Add($"{t.Id}: unknown challenge {t.Challenge}");
                if (!courseExists(t.Course ?? "")) problems.Add($"{t.Id}: unknown course {t.Course}");
                if (!carExists(t.Loaner?.Car ?? "")) problems.Add($"{t.Id}: unknown loaner car {t.Loaner?.Car}");
                if (t.Targets == null || t.Targets.TimeMs < 0 || t.Targets.DriftRaw < 0) problems.Add($"{t.Id}: negative target");
                if (t.Rules != null && t.Rules.MaxWallImpacts < -1) problems.Add($"{t.Id}: invalid wall allowance");
                if (!t.JudgesTime && !t.JudgesDrift && !t.IsRace && !t.IsCup && !t.IsDrill) problems.Add($"{t.Id}: unknown kind {t.Kind}");
                if (t.IsDrill && t.Rules != null && !(t.Rules.AllChallengeGates || t.Rules.ChallengeExits || t.Rules.AllDefenceZones || t.Rules.BrakeEnvelope || t.Rules.AlternatingRecoveries))
                    problems.Add($"{t.Id}: a drill judges nothing");
                if (t.IsCup)
                {
                    if (t.Legs == null || t.Legs.Count != CupTable.Legs) problems.Add($"{t.Id}: a challenge cup has {CupTable.Legs} legs");
                    foreach (TrialCupLeg leg in t.Legs ?? new List<TrialCupLeg>())
                    {
                        if (!courseExists(leg.Course ?? "")) problems.Add($"{t.Id}: unknown leg course {leg.Course}");
                        if (leg.TimeMs < 0) problems.Add($"{t.Id}: negative target");
                    }
                    if (t.Legs != null && t.Legs.Count > 0 && t.Course != t.Legs[0].Course) problems.Add($"{t.Id}: a cup's course is its first leg's");
                    if (t.LegFactor < 0) problems.Add($"{t.Id}: negative leg factor");
                if (!string.IsNullOrEmpty(t.ReferenceRival) && !t.JudgesTime) problems.Add($"{t.Id}: a rival's reference is a time");
                if (string.IsNullOrEmpty(t.SectionStartGate) != string.IsNullOrEmpty(t.SectionEndGate)) problems.Add($"{t.Id}: a section needs its start and end gates");
                if (t.HasSection && !t.JudgesTime) problems.Add($"{t.Id}: a section is timed");
                if (t.RequiredStoryRecords < 0) problems.Add($"{t.Id}: negative story records");
                if (t.RaceRivalReference && !t.JudgesTime) problems.Add($"{t.Id}: a rival reference is raced against its time");
                }
                else if (t.Legs != null && t.Legs.Count > 0) problems.Add($"{t.Id}: only challenge cups have legs");
                if (t.IsRace && (t.Field == null || t.Field.Count == 0)) problems.Add($"{t.Id}: a racecraft trial needs its fixed field");
                if (!t.IsRace && t.Field != null && t.Field.Count > 0) problems.Add($"{t.Id}: only racecraft trials have a field");
                if (t.IsRace && t.Rules != null && !(t.Rules.Win || t.Rules.CleanZonePass || t.Rules.PressureSector || !string.IsNullOrEmpty(t.Rules.ZonePassRole)))
                    problems.Add($"{t.Id}: a racecraft trial judges nothing of the race");
                if (!string.IsNullOrEmpty(t.Rules?.ZonePassRole) && (t.Field == null || !t.Field.Any(c => c.Role == t.Rules.ZonePassRole)))
                    problems.Add($"{t.Id}: no {t.Rules.ZonePassRole} car in the field to pass");
                if (t.Rules != null && t.Rules.PressureSector && (t.Field == null || !t.Field.Any(c => c.Role == "pressure")))
                    problems.Add($"{t.Id}: no pressure car in the field");
                if (t.Targets != null && t.Targets.SectorTimeMs < 0) problems.Add($"{t.Id}: negative target");
                foreach (TrialFieldCar c in t.Field ?? new List<TrialFieldCar>())
                {
                    if (!carExists(c.Car ?? "")) problems.Add($"{t.Id}: unknown field car {c.Car}");
                    if (Array.IndexOf(TrialFieldCar.Roles, c.Role ?? "") < 0) problems.Add($"{t.Id}: unknown field role {c.Role}");
                    if (c.Pace < 0f || c.Pace > 1.5f) problems.Add($"{t.Id}: field pace {c.Pace} out of range");
                }
            }
            foreach (IGrouping<string, ChallengeTrialDef> g in file.Trials.Where(t => !string.IsNullOrEmpty(t.Group)).GroupBy(t => t.Group))
                if (g.Select(t => t.Challenge).Distinct().Count() > 1) problems.Add($"group {g.Key} spans several challenges");
            foreach (IGrouping<string, ChallengeTrialDef> c in file.Trials.GroupBy(t => t.Challenge).Where(c => c.Count() > 1))
                if (c.Any(t => string.IsNullOrEmpty(t.Group)) || c.Select(t => t.Group).Distinct().Count() > 1)
                    problems.Add($"{c.Key} has several trials that are not one group");
            return problems;
        }

        static string Clock(long ms) => $"{ms / 60000}:{ms / 1000 % 60:00}.{ms % 1000:000}";
    }
}
