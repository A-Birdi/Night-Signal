using Newtonsoft.Json.Linq;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.CapClash;
using Xunit.Abstractions;
using static NightSignal.Core.Toys.ToyActivityId;

namespace NightSignal.Toys.Tests;

/// <summary>Cap Clash (Addendum 02 §2, acceptance C01) exercised through the authoritative session.</summary>
public sealed class CapClashTests
{
    readonly ITestOutputHelper output;
    public CapClashTests(ITestOutputHelper output) { this.output = output; }

    static JObject Shot(double angle, double power, double x, long seen) => Payload.Of("angle", angle, "power", power, "launchX", x, "seen", seen);

    static long Seen(Rig r) => r.S.CapClash.Board.BoardRevision;

    static void Settle(Rig rig, long maxMs = 15_000)
    {
        long end = rig.Now + maxMs;
        rig.Tick(100);
        while (rig.Now < end && (!rig.S.CapClash.IsSettled || rig.S.CapClash.State.Queue.Count > 0)) rig.Tick(100);
        Assert.True(rig.S.CapClash.IsSettled, "board did not settle");
    }

    static void SelectSolo(Rig rig, string member, string kind, string arg)
    {
        rig.Expect(rig.Do(member, CapClash, kind, Payload.Of(kind == "target.select" ? "target" : "arrangement", arg)));
    }

    [Fact]
    public void Both_arrangements_are_authored_with_six_bank_configurations()
    {
        CapClashContent c = ToyData.Content.CapClash;
        Assert.Equal(new[] { "workbench-bullseye", "toolbox-banks" }, c.Arrangements.Select(a => a.Id));
        CapArrangementDef banks = c.Arrangement("toolbox-banks");
        Assert.True(banks.Targets.Count(t => t.Bank) >= 6);
        Assert.True(banks.Obstacles.Count >= 4);
        Assert.All(c.Arrangements, a => Assert.Equal(6, a.Card.Count));
    }

    /// <summary>
    /// C01: bank/target scoring comes from the actual simulation. Known aims (found by searching the same physics) must
    /// bank into each Toolbox Banks configuration through the real session, and the straight line to the socket shadow
    /// is blocked by the rubber socket.
    /// </summary>
    [Theory]
    [InlineData("b1", -0.165, 22.5, 0.9, 25)]
    [InlineData("b2", -0.11, 19.5, 0.975, 25)]
    [InlineData("b3", 0.0, -6.0, 0.975, 25)]
    [InlineData("b4", 0.165, -6.0, 1.0, 25)]
    [InlineData("b6", 0.11, -9.0, 1.0, 25)]
    public void Bank_shots_score_through_the_real_simulation(string target, double x, double angle, double power, int minPoints)
    {
        var rig = new Rig("a");
        SelectSolo(rig, "a", "arrangement.select", "toolbox-banks");
        SelectSolo(rig, "a", "target.select", target);
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(angle, power, x, Seen(rig))));
        Settle(rig);
        ShotOutcome o = rig.S.CapClash.Board.History.Last();
        output.WriteLine($"{target}: distance {o.Distance:0.0000} points {o.Points} banked {o.Banked}");
        Assert.True(o.OnBoard);
        Assert.True(o.Banked);
        Assert.True(o.Points >= minPoints);
    }

    [Fact]
    public void Straight_shot_at_the_socket_shadow_hits_the_socket()
    {
        var rig = new Rig("a");
        SelectSolo(rig, "a", "arrangement.select", "toolbox-banks");
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(0, 0.8, 0.0, Seen(rig))));
        Settle(rig);
        ShotOutcome o = rig.S.CapClash.Board.History.Last();
        Assert.True(o.Banked); // contact with the socket prop
        Assert.True(!o.OnBoard || o.Distance > 0.07);
    }

    [Fact]
    public void Simultaneous_submissions_serialize_one_request_per_person_with_reconfirm_and_cancel()
    {
        var rig = new Rig("a", "b", "c");
        long seen = Seen(rig);
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(0, 0.72, 0.0, seen)));
        rig.Expect(rig.Do("b", CapClash, "shot.submit", Shot(-5, 0.7, 0.1, seen)));
        rig.Expect(rig.Do("c", CapClash, "shot.submit", Shot(5, 0.7, -0.1, seen)));
        // a fired immediately; b and c wait in submission order, one entry each.
        Assert.NotNull(rig.S.CapClash.Board.InFlight);
        Assert.Equal("a", rig.S.CapClash.Board.InFlight.Member);
        Assert.Equal(new[] { "b", "c" }, rig.S.CapClash.State.Queue.Select(q => q.Member));
        // Resubmitting updates b's aim without a second queue entry.
        rig.Expect(rig.Do("b", CapClash, "shot.submit", Shot(-4, 0.7, 0.1, seen)));
        Assert.Equal(2, rig.S.CapClash.State.Queue.Count);
        // c cancels.
        rig.Expect(rig.Do("c", CapClash, "shot.cancel"));
        Assert.Equal(new[] { "b" }, rig.S.CapClash.State.Queue.Select(q => q.Member));

        // Bodies settle before the next shot; the board changed since b aimed, so b must reconfirm.
        long end = rig.Now + 10_000;
        while (rig.Now < end && rig.S.CapClash.State.Queue.FirstOrDefault()?.State != ShotQueueState.AwaitingReconfirm) rig.Tick(50);
        Assert.Equal(ShotQueueState.AwaitingReconfirm, rig.S.CapClash.State.Queue[0].State);
        Assert.Null(rig.S.CapClash.Board.InFlight);
        Assert.Equal(ToyReason.InvalidState, rig.Do("c", CapClash, "shot.confirm").Reason); // not c's turn
        rig.Expect(rig.Do("b", CapClash, "shot.confirm", Payload.Of("angle", -3.0)));
        Assert.Equal("b", rig.S.CapClash.Board.InFlight.Member);
        Settle(rig);
        Assert.Equal(new[] { "a", "b" }, rig.S.CapClash.Board.History.Select(h => h.Member));
    }

    [Fact]
    public void Idle_users_do_not_hold_the_table()
    {
        var rig = new Rig("a", "b", "c");
        long seen = Seen(rig);
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(0, 0.7, 0.0, seen)));
        rig.Expect(rig.Do("b", CapClash, "shot.submit", Shot(0, 0.7, 0.1, seen)));
        rig.Expect(rig.Do("c", CapClash, "shot.submit", Shot(0, 0.7, -0.1, seen)));
        // b never answers the reconfirm prompt: the queue is released within a few seconds.
        long end = rig.Now + 30_000;
        while (rig.Now < end && rig.S.CapClash.State.Queue.Any(q => q.Member == "b")) rig.Tick(100);
        Assert.DoesNotContain(rig.S.CapClash.State.Queue, q => q.Member == "b");
        Assert.DoesNotContain(rig.S.CapClash.Board.History, h => h.Member == "b");
        // A disconnected (or closed/minimised) panel never holds a queue slot.
        rig.S.Disconnect("c", rig.Now);
        Assert.Empty(rig.S.CapClash.State.Queue);
    }

    [Fact]
    public void Cap_contact_knocks_the_crown_away_but_not_the_personal_best()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(-11.5, 0.825, 0.22, Seen(rig)))); // lands on the centre 50
        Settle(rig);
        CapStanding aNow = rig.S.CapClash.Standings().Single(s => s.Member == "a");
        Assert.True(aNow.Crown);
        Assert.Equal(50, aNow.CurrentPoints);
        double aBest = aNow.BestDistance.Value;
        CapBody aCap = rig.S.CapClash.Board.Caps.Single(c => c.Owner == "a");

        // b fires straight at a's cap from the same line: a hard hit.
        double dx = aCap.Pos.X - 0.0, dy = aCap.Pos.Y - 0.1;
        double angle = Math.Atan2(dx, dy) * 180 / Math.PI;
        rig.Expect(rig.Do("b", CapClash, "shot.submit", Shot(angle, 1.0, 0.0, Seen(rig))));
        Settle(rig);
        ShotOutcome bShot = rig.S.CapClash.Board.History.Last();
        Assert.True(bShot.CapContacts > 0);
        CapStanding aAfter = rig.S.CapClash.Standings().Single(s => s.Member == "a");
        Assert.True(!aAfter.CurrentDistance.HasValue || aAfter.CurrentDistance.Value > aBest + 0.01, "a's cap was knocked away");
        Assert.False(aAfter.Crown); // lost the current-board crown ...
        Assert.Equal(aBest, aAfter.BestDistance.Value); // ... but not the historical best
    }

    [Fact]
    public void Exact_mirror_shots_tie_and_both_hold_the_crown()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(3, 0.8, -0.1, Seen(rig))));
        Settle(rig);
        rig.Expect(rig.Do("b", CapClash, "shot.submit", Shot(-3, 0.8, 0.1, Seen(rig))));
        Settle(rig);
        List<CapStanding> st = rig.S.CapClash.Standings();
        CapStanding a = st.Single(s => s.Member == "a"), b = st.Single(s => s.Member == "b");
        Assert.Equal(a.CurrentDistance.Value, b.CurrentDistance.Value, 9);
        Assert.True(a.Crown && b.Crown); // ties remain ties
    }

    [Fact]
    public void Falling_off_returns_the_cap_and_never_duplicates_it()
    {
        var rig = new Rig("a");
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(0, 1.0, 0.0, Seen(rig)))); // far too hard: off the open end
        Settle(rig);
        ShotOutcome o = rig.S.CapClash.Board.History.Last();
        Assert.False(o.OnBoard);
        Assert.Empty(rig.S.CapClash.Board.Caps);
        for (int i = 0; i < 3; i++)
        {
            rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(0, 0.7, 0.0, Seen(rig))));
            Settle(rig);
        }
        Assert.Single(rig.S.CapClash.Board.Caps);
        Assert.Equal("cap:a", rig.S.CapClash.Board.Caps[0].CapId);
    }

    [Fact]
    public void Solo_player_fills_the_whole_cooperative_card()
    {
        var rig = new Rig("a");
        SelectSolo(rig, "a", "arrangement.select", "toolbox-banks");
        var aims = new (string target, double x, double angle, double power)[]
        {
            ("b1", -0.165, 22.5, 0.9), ("b2", -0.11, 19.5, 0.975), ("b3", 0.0, -6.0, 0.975),
            ("b4", 0.165, -6.0, 1.0), ("b5", 0.22, -9.0, 0.9), ("b6", 0.11, -9.0, 1.0),
        };
        foreach (var aim in aims)
        {
            SelectSolo(rig, "a", "target.select", aim.target);
            rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(aim.angle, aim.power, aim.x, Seen(rig))));
            Settle(rig);
        }
        CapBoardState b = rig.S.CapClash.Board;
        Assert.Equal(6, b.Card.Count);
        Assert.All(b.Card.Values, m => Assert.Equal("a", m));
        Assert.Equal(1, b.CardsCompleted);
        rig.Expect(rig.Do("a", CapClash, "card.reset"));
        Assert.Empty(b.Card);
    }

    [Fact]
    public void Switching_arrangement_needs_active_users_consent_and_preserves_the_previous_board()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(-11.5, 0.825, 0.22, Seen(rig))));
        Settle(rig);
        rig.Expect(rig.Do("b", CapClash, "shot.submit", Shot(3, 0.8, -0.1, Seen(rig))));
        Settle(rig);
        Vec2 aPos = rig.S.CapClash.Board.Caps.Single(c => c.Owner == "a").Pos;

        // b is an active table user: a's request opens a proposal; silence lapses (never consent).
        ToyResult p = rig.Do("a", CapClash, "arrangement.select", Payload.Of("arrangement", "toolbox-banks"));
        rig.Expect(p);
        Assert.Equal("workbench-bullseye", rig.S.CapClash.State.Active);
        rig.Tick(ToyLimits.ProposalLifetimeMs + 500);
        Assert.Equal(ProposalState.Lapsed, rig.S.CapClash.State.LastProposal.State);
        Assert.Equal("workbench-bullseye", rig.S.CapClash.State.Active);

        // With b's explicit approval it switches; the old board is saved, and switching back restores it exactly.
        rig.Expect(rig.Do("b", CapClash, "shot.submit", Shot(0, 0.5, 0, Seen(rig)))); // keep b active
        Settle(rig);
        string prop = rig.Do("a", CapClash, "arrangement.select", Payload.Of("arrangement", "toolbox-banks")).Value;
        rig.Expect(rig.Do("b", CapClash, "proposal.vote", Payload.Of("proposal", prop, "accept", true)));
        Assert.Equal("toolbox-banks", rig.S.CapClash.State.Active);
        CapBoardState saved = rig.S.CapClash.State.Boards["workbench-bullseye"];
        Assert.Equal(aPos, saved.Caps.Single(c => c.Owner == "a").Pos);
        string back = rig.Do("a", CapClash, "arrangement.select", Payload.Of("arrangement", "workbench-bullseye")).Value;
        rig.Expect(rig.Do("b", CapClash, "proposal.vote", Payload.Of("proposal", back, "accept", true)));
        Assert.Equal(aPos, rig.S.CapClash.Board.Caps.Single(c => c.Owner == "a").Pos);
    }

    [Fact]
    public void Late_spectator_watches_without_a_cap_or_queue_slot()
    {
        var rig = new Rig("a");
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(0, 0.72, 0.0, Seen(rig))));
        Settle(rig);
        Assert.Equal(JoinOutcome.NewSeat, rig.S.Join("late", 1, rig.Now));
        rig.Expect(rig.Do("late", CapClash, "view"));
        Assert.DoesNotContain(rig.S.CapClash.Board.Caps, c => c.Owner == "late");
        Assert.Empty(rig.S.CapClash.State.Queue);
        Assert.Null(rig.S.Seat("late").Controlled);
    }

    [Fact]
    public void Disconnect_during_a_shot_lets_it_finish_and_rejoin_restores_the_same_cap()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(0, 0.72, 0.0, Seen(rig))));
        rig.Tick(200);
        Assert.True(rig.S.CapClash.Board.Caps.Single(c => c.Owner == "a").Moving);
        rig.S.Disconnect("a", rig.Now);
        Settle(rig);
        Assert.Equal("a", rig.S.CapClash.Board.History.Last().Member);
        CapBody cap = rig.S.CapClash.Board.Caps.Single(c => c.Owner == "a");
        Assert.Equal(JoinOutcome.RestoredDormant, rig.S.Join("a", 2, rig.Now));
        rig.ResetSequence("a");
        Assert.Single(rig.S.CapClash.Board.Caps, c => c.Owner == "a");
        Assert.Same(cap, rig.S.CapClash.Board.Caps.Single(c => c.Owner == "a"));
    }

    [Fact]
    public void Resume_from_motion_after_a_pause_continues_the_same_slide()
    {
        // Reference: an uninterrupted shot.
        var reference = new Rig("a");
        reference.Expect(reference.Do("a", CapClash, "shot.submit", Shot(4, 0.75, -0.05, Seen(reference))));
        Settle(reference);
        Vec2 expected = reference.S.CapClash.Board.Caps.Single().Pos;

        var rig = new Rig("a");
        rig.Expect(rig.Do("a", CapClash, "shot.submit", Shot(4, 0.75, -0.05, Seen(rig))));
        rig.Tick(250);
        rig.S.Pause(rig.Now);
        CapBody moving = rig.S.CapClash.Board.Caps.Single();
        Assert.True(moving.Moving);
        Vec2 pos = moving.Pos, vel = moving.Vel;
        rig.Tick(90_000); // the real race: nothing moves, nothing waits for the cap
        Assert.Equal(pos, moving.Pos);
        Assert.Equal(vel, moving.Vel);
        rig.S.EndPause(rig.Now);
        rig.Tick(5_000);
        Assert.Equal(pos, moving.Pos); // moving toys stay frozen until a participant resumes
        rig.Expect(rig.Do("a", CapClash, "resume"));
        rig.Tick(ToyLimits.OrientationMs - 100);
        Assert.Equal(pos, moving.Pos); // orientation: no motion yet
        Settle(rig);
        Vec2 final = rig.S.CapClash.Board.Caps.Single().Pos;
        Assert.Equal(expected.X, final.X, 9);
        Assert.Equal(expected.Y, final.Y, 9);
    }
}
