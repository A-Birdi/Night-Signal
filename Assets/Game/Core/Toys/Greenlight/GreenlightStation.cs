using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NightSignal.Core.Toys.Greenlight
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum GreenlightVariant { LightsOut = 0, ShiftWindow = 1, HoldTheMark = 2 }

    /// <summary>Forgiving practice or a narrower voluntary challenge.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum GreenlightSetting { Forgiving = 0, Narrow = 1 }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum AttemptOutcome
    {
        Pending = 0,
        /// <summary>Voluntary failure: pressed before the cue / before the window / stopped short.</summary>
        Early = 1,
        /// <summary>Lights Out: a valid reaction.</summary>
        Valid = 2,
        /// <summary>Voluntary failure: no input in time.</summary>
        Missed = 3,
        /// <summary>Shift Window: inside the marked window. Hold the Mark: stopped on the mark.</summary>
        InWindow = 4,
        /// <summary>Voluntary failure: after the window / past the mark.</summary>
        Late = 5,
        /// <summary>System interruption (pause, disconnect, restore): neutral, restarted with a fresh hidden cue.</summary>
        Interrupted = 6,
        /// <summary>Closed or cancelled by the player: neutral, never a failure.</summary>
        Abandoned = 7,
    }

    /// <summary>Casual local timing support reported by the client for this attempt.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TimingSupport { HighResolution = 0, Coarse = 1 }

    /// <summary>Everything the client needs to present a cue, derived from the attempt seed on both ends.</summary>
    public struct GreenlightCue
    {
        /// <summary>Lights Out: unrevealed randomized wait before the lights go out (not rhythmic).</summary>
        public long HiddenDelayMs;
        /// <summary>Lights Out: no input within this long after the cue is a miss.</summary>
        public long MissAfterMs;
        /// <summary>Shift Window / Hold the Mark: full sweep duration.</summary>
        public double SweepMs;
        /// <summary>Window centre / mark position as a fraction of the sweep.</summary>
        public double Target;
        /// <summary>Full width of the accepted window (fraction of the sweep).</summary>
        public double Window;
    }

    public static class GreenlightRules
    {
        /// <summary>Faster than this after the cue is anticipation, not a reaction (labelled Early).</summary>
        public const double MinReactionMs = 100;
        /// <summary>Slack between the client's local elapsed time and the server's elapsed time (sanity check only).</summary>
        public const double ElapsedSlackMs = 1000;
        /// <summary>Lights Out: tolerated mismatch between local elapsed and (hidden delay + reaction).</summary>
        public const double SequenceSlackMs = 300;

        public static GreenlightCue Cue(GreenlightVariant v, GreenlightSetting s, long seed)
        {
            ulong u = unchecked((ulong)seed);
            var cue = new GreenlightCue();
            switch (v)
            {
                case GreenlightVariant.LightsOut:
                    cue.HiddenDelayMs = 1200 + (long)(ToyRandom.Unit(u, 0) * 3000);
                    cue.MissAfterMs = s == GreenlightSetting.Forgiving ? 1500 : 1000;
                    break;
                case GreenlightVariant.ShiftWindow:
                    cue.SweepMs = 1400 + Math.Floor(ToyRandom.Unit(u, 1) * 800);
                    cue.Target = 0.62 + ToyRandom.Unit(u, 2) * 0.22;
                    cue.Window = s == GreenlightSetting.Forgiving ? 0.12 : 0.06;
                    break;
                default:
                    cue.SweepMs = 1800 + Math.Floor(ToyRandom.Unit(u, 3) * 900);
                    cue.Target = 0.35 + ToyRandom.Unit(u, 4) * 0.45;
                    cue.Window = s == GreenlightSetting.Forgiving ? 0.07 : 0.03;
                    break;
            }
            return cue;
        }

        public static bool IsClean(AttemptOutcome o) => o == AttemptOutcome.Valid || o == AttemptOutcome.InWindow;
        public static bool IsVoluntaryFailure(AttemptOutcome o) => o == AttemptOutcome.Early || o == AttemptOutcome.Missed || o == AttemptOutcome.Late;
        public static bool IsNeutral(AttemptOutcome o) => o == AttemptOutcome.Interrupted || o == AttemptOutcome.Abandoned;
    }

    /// <summary>One micro-attempt (Addendum 02 §11 'GreenlightAttempt'). Timing is a casual LOCAL estimate.</summary>
    public sealed class GreenlightAttempt : INonProgressionRecord
    {
        [JsonIgnore] public string Domain => NonProgression.Domain;
        public string AttemptId;
        public string Member;
        public GreenlightVariant Variant;
        public GreenlightSetting Setting;
        public long Seed;
        public string SeriesId;
        public int SeriesIndex;
        public long IssuedAtMs;
        public AttemptOutcome Outcome;
        /// <summary>Comparable local measure in ms (reaction time, or distance from the window centre/mark). Lower is better.</summary>
        public double? MeasureMs;
        public bool PracticeOnly;
        public string RestartOf;
        public bool NeedsRestart;
    }

    /// <summary>A member's results for one variant + setting (practice-only results are kept in a separate track).</summary>
    public sealed class GreenlightTrack : INonProgressionRecord
    {
        [JsonIgnore] public string Domain => NonProgression.Domain;
        public string Member;
        public GreenlightVariant Variant;
        public GreenlightSetting Setting;
        public bool PracticeOnly;
        public double? BestMs;
        /// <summary>Most recent clean measures, newest last, at most five.</summary>
        public List<double> Recent = new List<double>();
        public int Clean;
        public int VoluntaryFailures;
        public int Interruptions;

        /// <summary>Median of the most recent five — only when five genuinely exist (never invented from fewer).</summary>
        [JsonIgnore] public double? RecentFiveMedianMs => Recent.Count >= 5 ? ToyMath.Median(Recent) : (double?)null;
    }

    /// <summary>A convoy chain of consecutive clean attempts contributed by anyone; absent members never block it.</summary>
    public sealed class GreenlightChain : INonProgressionRecord
    {
        [JsonIgnore] public string Domain => NonProgression.Domain;
        public GreenlightVariant Variant;
        public int Current;
        public int Best;
        public int Target = 10;
        public int Completed;
        public Dictionary<string, int> Contributions = new Dictionary<string, int>();
    }

    /// <summary>An opt-in shared cue series: members who join get the same hidden cues in the same order.</summary>
    public sealed class GreenlightSeries
    {
        public string SeriesId;
        public GreenlightVariant Variant;
        public GreenlightSetting Setting;
        public List<long> Seeds = new List<long>();
        public string CreatedBy;
    }

    public sealed class GreenlightState
    {
        public ToyRunState Run = new ToyRunState();
        public List<GreenlightAttempt> Open = new List<GreenlightAttempt>();
        public List<GreenlightAttempt> Recent = new List<GreenlightAttempt>();
        public List<GreenlightTrack> Tracks = new List<GreenlightTrack>();
        public List<GreenlightChain> Chains = new List<GreenlightChain>();
        public List<GreenlightSeries> Series = new List<GreenlightSeries>();
    }

    public sealed class GreenlightRankRow
    {
        public int Rank;
        public string Member;
        public double BestMs;
        public double? MedianMs;
    }

    /// <summary>
    /// Greenlight (Addendum 02 §4): Lights Out, Shift Window and Hold the Mark. The server issues attempts (id + seed),
    /// sanity-checks reports (id, variant, legal sequence, plausible ranges, duplicates, identity) and classifies them with
    /// the shared rules. It never ranks by packet arrival: rankings use the client-measured local times only.
    /// </summary>
    public sealed class GreenlightStation : IToyActivity
    {
        public const int RecentLimit = 48;
        public const int SeriesLimit = 4;

        readonly IToyHost host;
        public GreenlightState State { get; private set; }

        public GreenlightStation(IToyHost host, GreenlightState state)
        {
            this.host = host;
            State = state ?? new GreenlightState();
            foreach (GreenlightVariant v in (GreenlightVariant[])Enum.GetValues(typeof(GreenlightVariant)))
                if (!State.Chains.Any(c => c.Variant == v)) State.Chains.Add(new GreenlightChain { Variant = v });
        }

        public ToyActivityId Id => ToyActivityId.Greenlight;
        public ToyRunState Run => State.Run;
        public bool RequiresExplicitResume => false;
        public bool NeedsSimulation => false;
        public object StateObject => State;
        public bool IsTransient(string kind) => false;
        public bool TakesControl(string kind) => kind == "attempt.start" || kind == "attempt.report";

        public GreenlightChain Chain(GreenlightVariant v) => State.Chains.First(c => c.Variant == v);

        public GreenlightTrack Track(string member, GreenlightVariant v, GreenlightSetting s, bool practiceOnly = false) =>
            State.Tracks.FirstOrDefault(t => t.Member == member && t.Variant == v && t.Setting == s && t.PracticeOnly == practiceOnly);

        public GreenlightAttempt OpenAttempt(string member) => State.Open.FirstOrDefault(a => a.Member == member && a.Outcome == AttemptOutcome.Pending);

        public ToyResult Apply(ToyCommandContext ctx)
        {
            switch (ctx.Kind)
            {
                case "attempt.start": return Start(ctx);
                case "attempt.report": return Report(ctx);
                case "attempt.cancel": return Cancel(ctx);
                case "series.create": return CreateSeries(ctx);
                default: return ToyResult.Reject(ToyReason.UnknownKind);
            }
        }

        ToyResult Start(ToyCommandContext ctx)
        {
            GreenlightVariant v = ctx.P.Enum<GreenlightVariant>("variant");
            GreenlightSetting s = ctx.P.Has("setting") ? ctx.P.Enum<GreenlightSetting>("setting") : GreenlightSetting.Forgiving;
            string seriesId = ctx.P.OptionalId("series");
            long seed;
            int index = 0;
            if (seriesId != null)
            {
                GreenlightSeries series = State.Series.FirstOrDefault(x => x.SeriesId == seriesId);
                if (series == null) return ToyResult.Reject(ToyReason.NotFound, "unknown series");
                index = ctx.P.Int("index", 0, series.Seeds.Count - 1);
                v = series.Variant;
                s = series.Setting;
                seed = series.Seeds[index];
            }
            else seed = unchecked((long)host.NextSeed());
            GreenlightAttempt open = OpenAttempt(ctx.Member);
            if (open != null) Close(open, AttemptOutcome.Abandoned); // starting again is not a failure
            var a = new GreenlightAttempt
            {
                AttemptId = host.NextId("gl"), Member = ctx.Member, Variant = v, Setting = s, Seed = seed, SeriesId = seriesId, SeriesIndex = index,
                IssuedAtMs = ctx.NowMs, Outcome = AttemptOutcome.Pending,
            };
            State.Open.Add(a);
            return ToyResult.Ok(a.AttemptId);
        }

        /// <summary>
        /// Report payload: attempt, variant, elapsedMs (local monotonic time from the attempt start to the input edge),
        /// timing ("HighResolution"/"Coarse"), and either missed=true or the variant's measure: reactionMs (negative = before
        /// the cue) for Lights Out, position (0..1 of the sweep at the input edge) for Shift Window / Hold the Mark.
        /// </summary>
        ToyResult Report(ToyCommandContext ctx)
        {
            string id = ctx.P.Id("attempt");
            GreenlightAttempt a = State.Open.FirstOrDefault(x => x.AttemptId == id);
            if (a == null)
            {
                GreenlightAttempt past = State.Recent.FirstOrDefault(x => x.AttemptId == id);
                if (past != null && past.Member == ctx.Member)
                    return ToyResult.Reject(GreenlightRules.IsNeutral(past.Outcome) ? ToyReason.StaleEpoch : ToyReason.AlreadyDone, past.Outcome.ToString());
                return ToyResult.Reject(ToyReason.NotFound);
            }
            if (a.Member != ctx.Member) return ToyResult.Reject(ToyReason.NotOwner);
            if (ctx.P.Enum<GreenlightVariant>("variant") != a.Variant) return ToyResult.Reject(ToyReason.Malformed, "variant mismatch");
            TimingSupport timing = ctx.P.Has("timing") ? ctx.P.Enum<TimingSupport>("timing") : TimingSupport.Coarse;
            double elapsed = ctx.P.Double("elapsedMs", 0, 60_000);
            double serverElapsed = ctx.NowMs - a.IssuedAtMs;
            if (elapsed > serverElapsed + GreenlightRules.ElapsedSlackMs) return ToyResult.Reject(ToyReason.OutOfRange, "local time exceeds the attempt's lifetime");
            bool missed = ctx.P.Bool("missed", false);
            GreenlightCue cue = GreenlightRules.Cue(a.Variant, a.Setting, a.Seed);
            AttemptOutcome outcome;
            double? measure = null;
            if (a.Variant == GreenlightVariant.LightsOut)
            {
                if (missed)
                {
                    if (elapsed < cue.HiddenDelayMs + cue.MissAfterMs - GreenlightRules.SequenceSlackMs) return ToyResult.Reject(ToyReason.OutOfRange, "reported a miss before the window closed");
                    outcome = AttemptOutcome.Missed;
                }
                else
                {
                    double reaction = ctx.P.Double("reactionMs", -cue.HiddenDelayMs - GreenlightRules.SequenceSlackMs, cue.MissAfterMs + 5000);
                    if (Math.Abs(elapsed - (cue.HiddenDelayMs + reaction)) > GreenlightRules.SequenceSlackMs)
                        return ToyResult.Reject(ToyReason.OutOfRange, "reaction does not match the cue sequence");
                    if (reaction < GreenlightRules.MinReactionMs) outcome = AttemptOutcome.Early;
                    else if (reaction > cue.MissAfterMs) outcome = AttemptOutcome.Missed;
                    else { outcome = AttemptOutcome.Valid; measure = Math.Round(reaction, 1); }
                }
            }
            else
            {
                if (missed) outcome = AttemptOutcome.Missed;
                else
                {
                    double pos = ctx.P.Double("position", 0, 1.2);
                    if (Math.Abs(elapsed - pos * cue.SweepMs) > GreenlightRules.SequenceSlackMs + 0.05 * cue.SweepMs)
                        return ToyResult.Reject(ToyReason.OutOfRange, "position does not match the sweep time");
                    double off = pos - cue.Target;
                    if (Math.Abs(off) <= cue.Window / 2) { outcome = AttemptOutcome.InWindow; measure = Math.Round(Math.Abs(off) * cue.SweepMs, 1); }
                    else outcome = off < 0 ? AttemptOutcome.Early : AttemptOutcome.Late;
                }
            }
            a.PracticeOnly = timing != TimingSupport.HighResolution;
            a.MeasureMs = measure;
            Close(a, outcome);
            return ToyResult.Ok(outcome.ToString());
        }

        ToyResult Cancel(ToyCommandContext ctx)
        {
            string id = ctx.P.Id("attempt");
            GreenlightAttempt a = State.Open.FirstOrDefault(x => x.AttemptId == id);
            if (a == null) return ToyResult.Reject(ToyReason.NotFound);
            if (a.Member != ctx.Member) return ToyResult.Reject(ToyReason.NotOwner);
            Close(a, AttemptOutcome.Abandoned);
            return ToyResult.Ok(id);
        }

        ToyResult CreateSeries(ToyCommandContext ctx)
        {
            GreenlightVariant v = ctx.P.Enum<GreenlightVariant>("variant");
            GreenlightSetting s = ctx.P.Has("setting") ? ctx.P.Enum<GreenlightSetting>("setting") : GreenlightSetting.Forgiving;
            int count = ctx.P.Int("count", 1, 5, 5);
            var series = new GreenlightSeries { SeriesId = host.NextId("series"), Variant = v, Setting = s, CreatedBy = ctx.Member };
            for (int i = 0; i < count; i++) series.Seeds.Add(unchecked((long)host.NextSeed()));
            State.Series.Add(series);
            while (State.Series.Count > SeriesLimit) State.Series.RemoveAt(0);
            return ToyResult.Ok(series.SeriesId);
        }

        void Close(GreenlightAttempt a, AttemptOutcome outcome)
        {
            a.Outcome = outcome;
            State.Open.Remove(a);
            State.Recent.Add(a);
            while (State.Recent.Count > RecentLimit) State.Recent.RemoveAt(0);

            GreenlightTrack t = Track(a.Member, a.Variant, a.Setting, a.PracticeOnly);
            if (t == null)
            {
                t = new GreenlightTrack { Member = a.Member, Variant = a.Variant, Setting = a.Setting, PracticeOnly = a.PracticeOnly };
                State.Tracks.Add(t);
            }
            GreenlightChain chain = Chain(a.Variant);
            if (GreenlightRules.IsClean(outcome))
            {
                t.Clean++;
                if (a.MeasureMs.HasValue)
                {
                    if (!t.BestMs.HasValue || a.MeasureMs.Value < t.BestMs.Value) t.BestMs = a.MeasureMs;
                    t.Recent.Add(a.MeasureMs.Value);
                    if (t.Recent.Count > 5) t.Recent.RemoveAt(0);
                }
                chain.Current++;
                chain.Contributions[a.Member] = (chain.Contributions.TryGetValue(a.Member, out int n) ? n : 0) + 1;
                if (chain.Current > chain.Best) chain.Best = chain.Current;
                if (chain.Current >= chain.Target) { chain.Completed++; chain.Current = 0; }
            }
            else if (GreenlightRules.IsVoluntaryFailure(outcome))
            {
                t.VoluntaryFailures++;
                chain.Current = 0;
            }
            else if (outcome == AttemptOutcome.Interrupted) t.Interruptions++;
        }

        /// <summary>
        /// Session comparison for one variant + setting: ordered by the best LOCAL measure only (never packet arrival).
        /// Equal measures share a rank. Practice-only (coarse timing) results are excluded.
        /// </summary>
        public List<GreenlightRankRow> Ranking(GreenlightVariant v, GreenlightSetting s)
        {
            var rows = State.Tracks.Where(t => t.Variant == v && t.Setting == s && !t.PracticeOnly && t.BestMs.HasValue)
                .OrderBy(t => t.BestMs.Value).ThenBy(t => t.Member, StringComparer.Ordinal)
                .Select(t => new GreenlightRankRow { Member = t.Member, BestMs = t.BestMs.Value, MedianMs = t.RecentFiveMedianMs }).ToList();
            for (int i = 0; i < rows.Count; i++)
                rows[i].Rank = i > 0 && rows[i].BestMs == rows[i - 1].BestMs ? rows[i - 1].Rank : i + 1;
            return rows;
        }

        public void Advance(long nowMs) { }

        /// <summary>Pause: every exposed or pending cue is interrupted neutrally and will restart with a fresh hidden cue.</summary>
        public void Freeze(long nowMs)
        {
            Run.Frozen = true;
            foreach (GreenlightAttempt a in State.Open.ToList())
            {
                a.NeedsRestart = true;
                Close(a, AttemptOutcome.Interrupted);
            }
        }

        public void EndPause(long nowMs) { Run.Frozen = false; }

        /// <summary>Resuming restarts that member's one interrupted micro-attempt with a NEW seed (fresh unrevealed delay).</summary>
        public void Resume(string member, long nowMs)
        {
            Run.Frozen = false;
            GreenlightAttempt last = State.Recent.LastOrDefault(a => a.Member == member && a.NeedsRestart);
            foreach (GreenlightAttempt a in State.Recent) if (a.Member == member) a.NeedsRestart = false;
            if (last == null || OpenAttempt(member) != null) return;
            State.Open.Add(new GreenlightAttempt
            {
                AttemptId = host.NextId("gl"), Member = member, Variant = last.Variant, Setting = last.Setting, Seed = unchecked((long)host.NextSeed()),
                IssuedAtMs = nowMs, Outcome = AttemptOutcome.Pending, RestartOf = last.AttemptId,
            });
        }

        public void ReleaseControl(string member, ReleaseCause cause, long nowMs)
        {
            GreenlightAttempt a = OpenAttempt(member);
            if (a == null) return;
            bool system = cause == ReleaseCause.Disconnected || cause == ReleaseCause.FocusLost;
            a.NeedsRestart = system;
            Close(a, system ? AttemptOutcome.Interrupted : AttemptOutcome.Abandoned);
        }

        public void Retire(string member, long nowMs)
        {
            foreach (GreenlightAttempt a in State.Open.Where(x => x.Member == member).ToList()) Close(a, AttemptOutcome.Abandoned);
            foreach (GreenlightAttempt a in State.Recent) if (a.Member == member) a.NeedsRestart = false;
        }
    }
}
