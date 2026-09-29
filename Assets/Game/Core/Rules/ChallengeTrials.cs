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
    }

    /// <summary>Published targets (measured, see the file's method); 0 = not judged.</summary>
    public sealed class TrialTargets
    {
        /// <summary>The finish time to beat (strictly faster).</summary>
        public long TimeMs;
        /// <summary>The banked raw drift score to reach.</summary>
        public long DriftRaw;
    }

    /// <summary>One challenge trial (docs/CHALLENGE_TRIALS.md): a fixed course, loaner, rules and targets for one challenge.</summary>
    public sealed class ChallengeTrialDef
    {
        public string Id = "", Challenge = "", Title = "", Course = "", Tier = "";
        /// <summary>What is judged against measured targets: "time", "drift" or "time+drift".</summary>
        public string Kind = "time";
        public bool JudgesTime => Kind == "time" || Kind == "time+drift";
        public bool JudgesDrift => Kind == "drift" || Kind == "time+drift";
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

        /// <summary>Measured targets exist for everything this trial judges.</summary>
        public bool Published => (!JudgesTime || Targets.TimeMs > 0) && (!JudgesDrift || Targets.DriftRaw > 0);
    }

    public sealed class ChallengeTrialsFile
    {
        public string Schema = "", Method = "";
        public List<ChallengeTrialDef> Trials = new List<ChallengeTrialDef>();

        public ChallengeTrialDef Find(string id) => Trials.FirstOrDefault(t => t.Id == id);

        /// <summary>Every trial that must be passed for <paramref name="challenge"/> (one, or its whole group).</summary>
        public IReadOnlyList<ChallengeTrialDef> ForChallenge(string challenge) => Trials.Where(t => t.Challenge == challenge).ToList();
    }

    /// <summary>What happened in one trial run, from the race's authoritative facts.</summary>
    public struct TrialRunFacts
    {
        public bool Finished;
        public long TimeMs;
        public int Resets, WallImpacts;
        /// <summary>Seconds with the handbrake held after the start.</summary>
        public float HandbrakeSeconds;
        public long DriftRaw;
        public int ZonesBanked, ZonesTotal;
        /// <summary>The run drove this trial's loaner (the runtime resolved it; any other car or build fails the trial).</summary>
        public bool DroveLoaner;
    }

    public sealed class TrialVerdict
    {
        public bool Passed;
        /// <summary>Each judged condition in order: (passed, what).</summary>
        public List<KeyValuePair<bool, string>> Checks = new List<KeyValuePair<bool, string>>();
        public string Summary => string.Join(" · ", Checks.Select(c => (c.Key ? "✓ " : "✗ ") + c.Value));
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
            Check(f.Finished, "a valid finish");
            if (t.Targets.TimeMs > 0)
                Check(f.Finished && f.TimeMs > 0 && f.TimeMs < t.Targets.TimeMs, $"faster than {Clock(t.Targets.TimeMs)} ({(f.TimeMs > 0 ? Clock(f.TimeMs) : "no time")})");
            if (t.Targets.DriftRaw > 0)
                Check(f.DriftRaw >= t.Targets.DriftRaw, $"{t.Targets.DriftRaw:N0} raw drift banked ({f.DriftRaw:N0})");
            if (t.Rules.NoReset) Check(f.Resets == 0, $"no reset ({f.Resets})");
            if (t.Rules.MaxWallImpacts >= 0)
                Check(f.WallImpacts <= t.Rules.MaxWallImpacts, t.Rules.MaxWallImpacts == 0 ? $"no wall impact ({f.WallImpacts})" : $"at most {t.Rules.MaxWallImpacts} wall impact{(t.Rules.MaxWallImpacts == 1 ? "" : "s")} ({f.WallImpacts})");
            if (t.Rules.NoHandbrake) Check(f.HandbrakeSeconds <= 0f, "no handbrake after the start" + (f.HandbrakeSeconds > 0f ? $" (held {f.HandbrakeSeconds:F1} s)" : ""));
            if (t.Rules.BankEveryZone) Check(f.ZonesTotal > 0 && f.ZonesBanked >= f.ZonesTotal, $"a chain banked in every judged zone ({f.ZonesBanked}/{f.ZonesTotal})");
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
                if (!t.JudgesTime && !t.JudgesDrift) problems.Add($"{t.Id}: unknown kind {t.Kind}");
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
