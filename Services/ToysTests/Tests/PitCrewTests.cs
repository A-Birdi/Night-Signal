using NightSignal.Core.Toys;
using NightSignal.Core.Toys.PitCrew;
using static NightSignal.Core.Toys.ToyActivityId;

namespace NightSignal.Toys.Tests;

/// <summary>Pit-Crew Project (Addendum 02 §3, acceptance C02/C03).</summary>
public sealed class PitCrewTests
{
    static void StartBlueprint(Rig rig, string member, string blueprint)
    {
        if (rig.S.PitCrew.State.Project.BlueprintId == blueprint) return;
        rig.Expect(rig.Do(member, PitCrew, "project.start", Payload.Of("blueprint", blueprint)));
    }

    /// <summary>Claims and completes one operation with in-tolerance steps (a real client would report measured errors).</summary>
    static void Complete(Rig rig, string member, OperationDef op)
    {
        rig.Expect(rig.Do(member, PitCrew, "task.claim", Payload.Of("op", op.Id)), op.Id);
        OperationProgress p = rig.S.PitCrew.State.Project.Ops[op.Id];
        for (int step = p.StepsDone; step < op.Steps; step++)
        {
            rig.Tick(300);
            ToyResult r = rig.Do(member, PitCrew, "task.step", Payload.Of("op", op.Id, "step", step, "error", op.StepTolerance * 0.5));
            rig.Expect(r, op.Id + " step " + step);
        }
    }

    [Fact]
    public void Blueprints_meet_the_authoring_contract()
    {
        PitCrewContent c = ToyData.Content.PitCrew;
        Assert.Equal(new[] { "night-shift-coupe", "cutaway-four", "terrace-pit-diorama" }, c.Blueprints.Select(b => b.Id));
        foreach (BlueprintDef b in c.Blueprints)
        {
            Assert.True(b.Operations.Count >= 24, b.Id);
            Assert.Equal(6, b.Operations.Select(o => o.Family).Distinct().Count());
            Assert.True(b.StartAccessible.Count() >= 6, b.Id);
            Assert.All(b.Operations, o => Assert.InRange(o.Seconds, 3, 15));
            // Real branching: the dependency graph is not a single chain.
            Assert.True(b.Operations.Count(o => b.Operations.Count(x => x.Requires.Contains(o.Id)) >= 2) >= 2, b.Id);
            Assert.Equal(b.Operations.Count, b.TopologicalOrder().Count);
        }
    }

    [Theory]
    [InlineData("night-shift-coupe")]
    [InlineData("cutaway-four")]
    [InlineData("terrace-pit-diorama")]
    public void Every_blueprint_can_be_completed_alone(string blueprint)
    {
        var rig = new Rig("solo");
        StartBlueprint(rig, "solo", blueprint);
        BlueprintDef bp = rig.S.PitCrew.Blueprint;
        int installed = 0;
        foreach (OperationDef op in bp.TopologicalOrder())
        {
            Complete(rig, "solo", op);
            installed += op.Parts.Count;
            Assert.Equal(installed, rig.S.PitCrew.InstalledParts().Count); // the model visibly grows with each task
        }
        Assert.True(rig.S.PitCrew.IsComplete);
        ShelfModel model = rig.S.PitCrew.State.Shelf.Single();
        Assert.Equal(blueprint, model.BlueprintId);
        Assert.Equal("solo", model.Contributions.Single().Member);
        Assert.Equal(bp.Operations.Count, model.Contributions.Single().Operations);
        Assert.Equal(bp.Parts.Count, rig.S.PitCrew.InstalledParts().Distinct().Count());
    }

    [Fact]
    public void Six_contributors_work_in_parallel_to_completion()
    {
        string[] crew = { "a", "b", "c", "d", "e", "f" };
        var rig = new Rig(crew);
        BlueprintDef bp = rig.S.PitCrew.Blueprint;
        // Six simultaneous claims on six start-accessible tasks all succeed.
        List<OperationDef> starts = bp.StartAccessible.Take(6).ToList();
        for (int i = 0; i < 6; i++) rig.Expect(rig.Do(crew[i], PitCrew, "task.claim", Payload.Of("op", starts[i].Id)));
        Assert.Equal(6, rig.S.PitCrew.Operations().Count(o => o.Status == OperationStatus.Claimed));

        // Round-robin: each member takes the next available task until the model is done.
        int turn = 0, guard = 0;
        while (!rig.S.PitCrew.IsComplete && guard++ < 500)
        {
            string m = crew[turn++ % crew.Length];
            OperationDef mine = bp.Operations.FirstOrDefault(o => rig.S.PitCrew.State.Project.Ops.TryGetValue(o.Id, out OperationProgress p) && p.Holder == m);
            mine ??= bp.Operations.FirstOrDefault(o => rig.S.PitCrew.StatusOf(o) == OperationStatus.Available);
            if (mine == null) { rig.Tick(100); continue; }
            Complete(rig, m, mine);
        }
        Assert.True(rig.S.PitCrew.IsComplete);
        ShelfModel model = rig.S.PitCrew.State.Shelf.Single();
        Assert.Equal(crew.OrderBy(x => x), model.Contributions.Select(c => c.Member).OrderBy(x => x));
        Assert.Equal(bp.Operations.Count, model.Contributions.Sum(c => c.Operations));
    }

    [Fact]
    public void Conflicting_claims_are_ordered_by_the_server()
    {
        var rig = new Rig("a", "b");
        rig.Expect(rig.Do("a", PitCrew, "task.claim", Payload.Of("op", "co-01")));
        ToyResult b = rig.Do("b", PitCrew, "task.claim", Payload.Of("op", "co-01"));
        Assert.Equal(ToyReason.LeaseHeld, b.Reason);
        Assert.Equal(ToyReason.NotAllowed, rig.Do("b", PitCrew, "task.claim", Payload.Of("op", "co-02")).Reason); // depends on co-01
        Assert.Equal(ToyReason.NoLease, rig.Do("b", PitCrew, "task.step", Payload.Of("op", "co-01", "step", 0, "error", 0.0)).Reason);
    }

    [Fact]
    public void Interrupted_lease_keeps_partial_progress_for_the_next_contributor()
    {
        var rig = new Rig("a", "b");
        OperationDef tighten = rig.S.PitCrew.Blueprint.Operation("co-21"); // 3-step route, start-accessible
        rig.Expect(rig.Do("a", PitCrew, "task.claim", Payload.Of("op", tighten.Id)));
        rig.Tick(400);
        rig.Expect(rig.Do("a", PitCrew, "task.step", Payload.Of("op", tighten.Id, "step", 0, "error", 1.0)));
        // a walks away: no real interaction renews the lease (keeping the window open does not).
        rig.Tick(PitCrewWorkshop.ClaimLeaseMs + 200);
        OperationProgress p = rig.S.PitCrew.State.Project.Ops[tighten.Id];
        Assert.Null(p.Holder);
        Assert.Equal(1, p.StepsDone);
        // b continues from the accepted checkpoint, without stealing anything unsaved.
        rig.Expect(rig.Do("b", PitCrew, "task.claim", Payload.Of("op", tighten.Id)));
        rig.Tick(300);
        rig.Expect(rig.Do("b", PitCrew, "task.step", Payload.Of("op", tighten.Id, "step", 1, "error", 1.0)));
        rig.Tick(300);
        Assert.Equal("done", rig.Do("b", PitCrew, "task.step", Payload.Of("op", tighten.Id, "step", 2, "error", 1.0)).Value);
        Assert.Equal(new[] { "a", "b", "b" }, p.StepBy);
        Assert.Equal("b", p.CompletedBy);
    }

    [Fact]
    public void Duplicate_completion_messages_complete_exactly_once()
    {
        var rig = new Rig("a", "b");
        OperationDef op = rig.S.PitCrew.Blueprint.Operation("co-05"); // 1 step
        rig.Expect(rig.Do("a", PitCrew, "task.claim", Payload.Of("op", op.Id)));
        rig.Tick(300);
        ToyCommand finish = rig.Cmd("a", PitCrew, "task.step", Payload.Of("op", op.Id, "step", 0, "error", 0.5));
        rig.Expect(rig.S.Submit(finish, rig.Now));
        ToyResult dup = rig.Resend(finish);
        Assert.True(dup.Duplicate);
        ToyResult retry = rig.Do("a", PitCrew, "task.step", Payload.Of("op", op.Id, "step", 0, "error", 0.5)); // new request id
        Assert.Equal(ToyReason.AlreadyDone, retry.Reason);
        Assert.Equal(ToyReason.AlreadyDone, rig.Do("b", PitCrew, "task.claim", Payload.Of("op", op.Id)).Reason);
        Assert.Single(rig.S.PitCrew.InstalledParts(), "engine-block");
    }

    [Fact]
    public void A_missed_step_only_retries_that_step()
    {
        var rig = new Rig("a");
        OperationDef op = rig.S.PitCrew.Blueprint.Operation("co-01"); // 2-step align
        rig.Expect(rig.Do("a", PitCrew, "task.claim", Payload.Of("op", op.Id)));
        rig.Tick(300);
        rig.Expect(rig.Do("a", PitCrew, "task.step", Payload.Of("op", op.Id, "step", 0, "error", 1.0)));
        rig.Tick(300);
        ToyResult miss = rig.Do("a", PitCrew, "task.step", Payload.Of("op", op.Id, "step", 1, "error", op.StepTolerance * 3));
        Assert.Equal("retry", miss.Value);
        OperationProgress p = rig.S.PitCrew.State.Project.Ops[op.Id];
        Assert.Equal(1, p.StepsDone); // nothing was undone
        Assert.Equal(1, p.Misses);
        rig.Tick(300);
        Assert.Equal("done", rig.Do("a", PitCrew, "task.step", Payload.Of("op", op.Id, "step", 1, "error", 1.0)).Value);
        Assert.Equal(ToyReason.OutOfRange, rig.Do("a", PitCrew, "task.step", Payload.Of("op", "co-05", "step", 0, "error", -1.0)).Reason);
    }

    [Fact]
    public void Unapproved_reset_cannot_destroy_work_and_an_approved_one_keeps_a_checkpoint()
    {
        var rig = new Rig("a", "b");
        OperationDef op = rig.S.PitCrew.Blueprint.Operation("co-05");
        rig.Expect(rig.Do("a", PitCrew, "task.claim", Payload.Of("op", op.Id)));
        rig.Tick(300);
        rig.Expect(rig.Do("a", PitCrew, "task.step", Payload.Of("op", op.Id, "step", 0, "error", 0.5)));
        string projectId = rig.S.PitCrew.State.Project.ProjectId;

        // Nobody can discard an unfinished project with one click.
        Assert.Equal(ToyReason.ConsentRequired, rig.Do("b", PitCrew, "project.start", Payload.Of("blueprint", "cutaway-four")).Reason);
        // A reset proposal needs a's approval: silence lapses, a decline declines.
        string p1 = rig.Do("b", PitCrew, "project.reset", Payload.Of("blueprint", "cutaway-four")).Value;
        rig.Tick(ToyLimits.ProposalLifetimeMs + 200);
        Assert.Equal(ProposalState.Lapsed, rig.S.PitCrew.State.LastProposal.State);
        Assert.Equal(projectId, rig.S.PitCrew.State.Project.ProjectId);
        string p2 = rig.Do("b", PitCrew, "project.reset", Payload.Of("blueprint", "cutaway-four")).Value;
        rig.Expect(rig.Do("a", PitCrew, "proposal.vote", Payload.Of("proposal", p2, "accept", false)));
        Assert.Equal(ProposalState.Declined, rig.S.PitCrew.State.LastProposal.State);
        Assert.Equal(projectId, rig.S.PitCrew.State.Project.ProjectId);
        Assert.NotEqual(p1, p2);

        // Approved: a recoverable checkpoint is kept and can be restored while the new project is still empty.
        string p3 = rig.Do("b", PitCrew, "project.reset", Payload.Of("blueprint", "cutaway-four")).Value;
        rig.Expect(rig.Do("a", PitCrew, "proposal.vote", Payload.Of("proposal", p3, "accept", true)));
        Assert.Equal("cutaway-four", rig.S.PitCrew.State.Project.BlueprintId);
        Assert.Equal(projectId, rig.S.PitCrew.State.ResetCheckpoint.ProjectId);
        rig.Expect(rig.Do("a", PitCrew, "project.restore"));
        Assert.Equal(projectId, rig.S.PitCrew.State.Project.ProjectId);
        Assert.Contains("engine-block", rig.S.PitCrew.InstalledParts());
    }

    [Fact]
    public void C03_shelf_and_partial_project_survive_race_and_reconnect_cycles()
    {
        var rig = new Rig("a", "b");
        BlueprintDef bp = rig.S.PitCrew.Blueprint;
        foreach (OperationDef op in bp.TopologicalOrder()) Complete(rig, "a", op);
        rig.Expect(rig.Do("b", PitCrew, "project.start", Payload.Of("blueprint", "terrace-pit-diorama")));
        OperationDef partial = rig.S.PitCrew.Blueprint.Operation("td-11");
        foreach (OperationDef op in rig.S.PitCrew.Blueprint.TopologicalOrder().TakeWhile(o => o.Id != "td-11")) Complete(rig, "b", op);
        rig.Expect(rig.Do("b", PitCrew, "task.claim", Payload.Of("op", partial.Id)));
        rig.Tick(300);
        rig.Expect(rig.Do("b", PitCrew, "task.step", Payload.Of("op", partial.Id, "step", 0, "error", 1.0)));

        for (int cycle = 0; cycle < 3; cycle++)
        {
            rig.S.Pause(rig.Now);                 // race start
            rig.S.Disconnect("b", rig.Now);       // b drops during the race
            string json = DowntimeCodec.Serialize(rig.S.Snapshot());
            rig.Tick(60_000);
            DowntimeSession restored = DowntimeSession.Restore(DowntimeCodec.Deserialize(json), ToyData.Content, rig.Now);
            restored.EndPause(rig.Now);           // back from the race
            Assert.Equal(JoinOutcome.RestoredDormant, restored.Join("b", 2 + cycle, rig.Now));
            var r2 = new Rig(restored, rig.Now);
            Assert.Single(r2.S.PitCrew.State.Shelf);
            Assert.Equal(1, r2.S.PitCrew.State.Project.Ops[partial.Id].StepsDone);
            Assert.Null(r2.S.PitCrew.State.Project.Ops[partial.Id].Holder); // preempted contributors release control
            rig = r2;
        }
    }

    [Fact]
    public void Shelf_is_bounded_and_starting_after_completion_preserves_models()
    {
        var rig = new Rig("a");
        for (int i = 0; i < PitCrewWorkshop.ShelfCapacity + 1; i++)
        {
            string bp = new[] { "terrace-pit-diorama", "cutaway-four" }[i % 2];
            if (rig.S.PitCrew.IsComplete || rig.S.PitCrew.State.Project.BlueprintId != bp)
                rig.Expect(rig.Do("a", PitCrew, "project.start", Payload.Of("blueprint", bp)));
            foreach (OperationDef op in rig.S.PitCrew.Blueprint.TopologicalOrder()) Complete(rig, "a", op);
        }
        Assert.Equal(PitCrewWorkshop.ShelfCapacity, rig.S.PitCrew.State.Shelf.Count);
        Assert.True(PitCrewWorkshop.ShelfCapacity >= 3);
    }
}
