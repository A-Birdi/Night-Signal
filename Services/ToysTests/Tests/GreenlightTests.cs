using NightSignal.Core.Toys;
using NightSignal.Core.Toys.Greenlight;
using static NightSignal.Core.Toys.ToyActivityId;

namespace NightSignal.Toys.Tests;

/// <summary>Greenlight (Addendum 02 §4, acceptance C04).</summary>
public sealed class GreenlightTests
{
    static GreenlightAttempt Start(Rig rig, string m, string variant = "LightsOut", string setting = "Forgiving")
    {
        rig.Expect(rig.Do(m, Greenlight, "attempt.start", Payload.Of("variant", variant, "setting", setting)));
        return rig.S.Greenlight.OpenAttempt(m);
    }

    /// <summary>Lights Out: the client measured <paramref name="reactionMs"/> locally from the cue to its input edge.</summary>
    static ToyResult LightsOut(Rig rig, string m, double reactionMs, string timing = "HighResolution", bool wait = true)
    {
        GreenlightAttempt a = rig.S.Greenlight.OpenAttempt(m) ?? Start(rig, m);
        GreenlightCue cue = GreenlightRules.Cue(a.Variant, a.Setting, a.Seed);
        double elapsed = cue.HiddenDelayMs + reactionMs;
        if (wait) rig.Tick((long)Math.Max(0, elapsed) + 60); // report after the local input happened (network delay is irrelevant)
        return rig.Do(m, Greenlight, "attempt.report", Payload.Of("attempt", a.AttemptId, "variant", "LightsOut", "elapsedMs", elapsed, "reactionMs", reactionMs, "timing", timing));
    }

    static ToyResult Sweep(Rig rig, string m, string variant, double offsetFromTarget, string setting = "Forgiving")
    {
        GreenlightAttempt a = Start(rig, m, variant, setting);
        GreenlightCue cue = GreenlightRules.Cue(a.Variant, a.Setting, a.Seed);
        double pos = cue.Target + offsetFromTarget;
        double elapsed = pos * cue.SweepMs;
        rig.Tick((long)elapsed + 60);
        return rig.Do(m, Greenlight, "attempt.report", Payload.Of("attempt", a.AttemptId, "variant", variant, "elapsedMs", elapsed, "position", pos, "timing", "HighResolution"));
    }

    [Fact]
    public void Lights_out_uses_a_randomized_unrevealed_delay()
    {
        var delays = new HashSet<long>();
        var rig = new Rig("a");
        for (int i = 0; i < 12; i++)
        {
            GreenlightAttempt a = Start(rig, "a");
            long d = GreenlightRules.Cue(a.Variant, a.Setting, a.Seed).HiddenDelayMs;
            Assert.InRange(d, 1200, 4200);
            delays.Add(d);
        }
        Assert.True(delays.Count >= 10, "no fixed rhythm to memorize");
    }

    [Fact]
    public void Lights_out_classifies_valid_early_anticipated_and_missed_inputs()
    {
        var rig = new Rig("a");
        Assert.Equal("Valid", LightsOut(rig, "a", 245).Value);
        Assert.Equal("Early", LightsOut(rig, "a", -300).Value);   // pressed before the lights went out
        Assert.Equal("Early", LightsOut(rig, "a", 60).Value);     // faster than a human reaction: anticipation
        Assert.Equal("Missed", LightsOut(rig, "a", 1800).Value);
        GreenlightTrack t = rig.S.Greenlight.Track("a", GreenlightVariant.LightsOut, GreenlightSetting.Forgiving);
        Assert.Equal(245, t.BestMs);
        Assert.Equal(3, t.VoluntaryFailures);
    }

    [Fact]
    public void Server_sanity_checks_sequence_ranges_identity_and_duplicates()
    {
        var rig = new Rig("a", "b");
        GreenlightAttempt a = Start(rig, "a");
        GreenlightCue cue = GreenlightRules.Cue(a.Variant, a.Setting, a.Seed);
        // A local elapsed time longer than the attempt has existed on the server is implausible.
        Assert.Equal(ToyReason.OutOfRange, rig.Do("a", Greenlight, "attempt.report", Payload.Of("attempt", a.AttemptId, "variant", "LightsOut", "elapsedMs", cue.HiddenDelayMs + 250.0, "reactionMs", 250.0, "timing", "HighResolution")).Reason);
        rig.Tick(cue.HiddenDelayMs + 400);
        // Reaction that does not match the hidden delay + elapsed sequence.
        Assert.Equal(ToyReason.OutOfRange, rig.Do("a", Greenlight, "attempt.report", Payload.Of("attempt", a.AttemptId, "variant", "LightsOut", "elapsedMs", 100.0, "reactionMs", 250.0, "timing", "HighResolution")).Reason);
        // Wrong variant, wrong identity.
        Assert.Equal(ToyReason.Malformed, rig.Do("a", Greenlight, "attempt.report", Payload.Of("attempt", a.AttemptId, "variant", "ShiftWindow", "elapsedMs", 100.0, "position", 0.5)).Reason);
        Assert.Equal(ToyReason.NotOwner, rig.Do("b", Greenlight, "attempt.report", Payload.Of("attempt", a.AttemptId, "variant", "LightsOut", "elapsedMs", cue.HiddenDelayMs + 250.0, "reactionMs", 250.0)).Reason);
        rig.Expect(LightsOut(rig, "a", 250, wait: false));
        // Dedup: the same attempt cannot be reported twice (even with a new request id).
        Assert.Equal(ToyReason.AlreadyDone, rig.Do("a", Greenlight, "attempt.report", Payload.Of("attempt", a.AttemptId, "variant", "LightsOut", "elapsedMs", cue.HiddenDelayMs + 250.0, "reactionMs", 250.0)).Reason);
        Assert.Equal(1, rig.S.Greenlight.Track("a", GreenlightVariant.LightsOut, GreenlightSetting.Forgiving).Clean);
    }

    [Fact]
    public void Ranking_uses_local_measurements_never_packet_arrival_and_ties_share_a_rank()
    {
        var rig = new Rig("a", "b", "c");
        rig.Expect(LightsOut(rig, "a", 310)); // arrives first, slower
        rig.Expect(LightsOut(rig, "b", 220)); // arrives later, faster
        rig.Expect(LightsOut(rig, "c", 310)); // same time as a
        List<GreenlightRankRow> rows = rig.S.Greenlight.Ranking(GreenlightVariant.LightsOut, GreenlightSetting.Forgiving);
        Assert.Equal("b", rows[0].Member);
        Assert.Equal(1, rows[0].Rank);
        Assert.Equal(2, rows[1].Rank);
        Assert.Equal(2, rows[2].Rank);
    }

    [Fact]
    public void Median_of_recent_five_only_when_five_exist()
    {
        var rig = new Rig("a");
        double[] times = { 300, 250, 280, 260, 400, 240 };
        for (int i = 0; i < 4; i++) rig.Expect(LightsOut(rig, "a", times[i]));
        GreenlightTrack t = rig.S.Greenlight.Track("a", GreenlightVariant.LightsOut, GreenlightSetting.Forgiving);
        Assert.Null(t.RecentFiveMedianMs); // never invented from fewer than five
        rig.Expect(LightsOut(rig, "a", times[4]));
        Assert.Equal(280, t.RecentFiveMedianMs);
        rig.Expect(LightsOut(rig, "a", times[5]));
        Assert.Equal(260, t.RecentFiveMedianMs); // the most recent five: 250 280 260 400 240
        Assert.Equal(240, t.BestMs);
    }

    [Fact]
    public void Shift_window_and_hold_the_mark_classify_early_late_and_in_window()
    {
        var rig = new Rig("a");
        Assert.Equal("InWindow", Sweep(rig, "a", "ShiftWindow", 0.02).Value);
        Assert.Equal("Early", Sweep(rig, "a", "ShiftWindow", -0.1).Value);
        Assert.Equal("Late", Sweep(rig, "a", "ShiftWindow", 0.1).Value);
        Assert.Equal("InWindow", Sweep(rig, "a", "HoldTheMark", 0.025, "Forgiving").Value);
        Assert.Equal("Late", Sweep(rig, "a", "HoldTheMark", 0.025, "Narrow").Value); // same stop fails the narrow challenge
        Assert.Equal("Early", Sweep(rig, "a", "HoldTheMark", -0.05, "Narrow").Value);
    }

    [Fact]
    public void Convoy_chain_is_fed_by_anyone_reset_by_voluntary_failures_and_untouched_by_interruptions()
    {
        var rig = new Rig("a", "b", "absent");
        GreenlightChain chain = rig.S.Greenlight.Chain(GreenlightVariant.LightsOut);
        rig.Expect(LightsOut(rig, "a", 250));
        rig.Expect(LightsOut(rig, "b", 260));
        rig.Expect(LightsOut(rig, "a", 270));
        Assert.Equal(3, chain.Current); // "absent" never played and never blocks

        Start(rig, "b");
        rig.S.Pause(rig.Now); // system interruption mid-attempt: neutral
        rig.S.EndPause(rig.Now);
        Assert.Equal(3, chain.Current);
        GreenlightTrack bt = rig.S.Greenlight.Track("b", GreenlightVariant.LightsOut, GreenlightSetting.Forgiving);
        Assert.Equal(1, bt.Interruptions);
        Assert.Equal(0, bt.VoluntaryFailures);

        rig.Expect(LightsOut(rig, "a", -100)); // genuine voluntary false start
        Assert.Equal(0, chain.Current);
        Assert.Equal(3, chain.Best);

        // Solo can complete the same chain.
        for (int i = 0; i < chain.Target; i++) rig.Expect(LightsOut(rig, "a", 250 + i));
        Assert.Equal(1, chain.Completed);
    }

    [Fact]
    public void Exposed_cue_interrupted_by_the_main_event_restarts_neutrally_with_a_fresh_hidden_cue()
    {
        var rig = new Rig("a");
        GreenlightAttempt a = Start(rig, "a");
        GreenlightCue cue = GreenlightRules.Cue(a.Variant, a.Setting, a.Seed);
        rig.Tick(cue.HiddenDelayMs + 100); // lights are out: the cue is exposed
        rig.S.Pause(rig.Now);
        Assert.Equal(AttemptOutcome.Interrupted, a.Outcome);
        // A late report of the interrupted attempt is refused (it belongs to the old epoch).
        rig.S.EndPause(rig.Now);
        ToyResult late = rig.Do("a", Greenlight, "attempt.report", Payload.Of("attempt", a.AttemptId, "variant", "LightsOut", "elapsedMs", cue.HiddenDelayMs + 100.0, "reactionMs", 100.0));
        Assert.False(late.Accepted);
        rig.Expect(rig.Do("a", Greenlight, "resume"));
        GreenlightAttempt restart = rig.S.Greenlight.OpenAttempt("a");
        Assert.NotNull(restart);
        Assert.Equal(a.AttemptId, restart.RestartOf);
        Assert.NotEqual(a.Seed, restart.Seed); // fresh unrevealed delay
        GreenlightTrack t = rig.S.Greenlight.Track("a", GreenlightVariant.LightsOut, GreenlightSetting.Forgiving);
        Assert.Equal(0, t.VoluntaryFailures);
        Assert.Equal(1, t.Interruptions);
    }

    [Fact]
    public void Closing_or_disconnecting_is_never_a_failure()
    {
        var rig = new Rig("a", "b");
        Start(rig, "a");
        rig.S.Close("a", Greenlight, rig.Now);
        Start(rig, "b");
        rig.S.Disconnect("b", rig.Now);
        Assert.Contains(rig.S.Greenlight.State.Recent, x => x.Member == "a" && x.Outcome == AttemptOutcome.Abandoned);
        Assert.Contains(rig.S.Greenlight.State.Recent, x => x.Member == "b" && x.Outcome == AttemptOutcome.Interrupted);
        Assert.All(rig.S.Greenlight.State.Tracks, t => Assert.Equal(0, t.VoluntaryFailures));
    }

    [Fact]
    public void Coarse_timing_is_labelled_practice_only_and_kept_out_of_comparisons()
    {
        var rig = new Rig("a", "b");
        rig.Expect(LightsOut(rig, "a", 150, "Coarse"));
        rig.Expect(LightsOut(rig, "b", 280));
        Assert.True(rig.S.Greenlight.Track("a", GreenlightVariant.LightsOut, GreenlightSetting.Forgiving, practiceOnly: true).BestMs == 150);
        List<GreenlightRankRow> rows = rig.S.Greenlight.Ranking(GreenlightVariant.LightsOut, GreenlightSetting.Forgiving);
        Assert.Equal(new[] { "b" }, rows.Select(r => r.Member));
    }

    [Fact]
    public void Shared_cue_series_gives_opted_in_members_the_same_cues()
    {
        var rig = new Rig("a", "b");
        string series = rig.Do("a", Greenlight, "series.create", Payload.Of("variant", "LightsOut", "count", 5)).Value;
        rig.Expect(rig.Do("a", Greenlight, "attempt.start", Payload.Of("variant", "LightsOut", "series", series, "index", 2)));
        rig.Expect(rig.Do("b", Greenlight, "attempt.start", Payload.Of("variant", "LightsOut", "series", series, "index", 2)));
        Assert.Equal(rig.S.Greenlight.OpenAttempt("a").Seed, rig.S.Greenlight.OpenAttempt("b").Seed);
    }
}
