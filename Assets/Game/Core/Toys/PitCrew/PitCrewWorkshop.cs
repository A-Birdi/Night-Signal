using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NightSignal.Core.Toys.PitCrew
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum OperationStatus { Locked = 0, Available = 1, Claimed = 2, Done = 3 }

    /// <summary>Authoritative progress of one operation of the active project (only operations that were touched are stored).</summary>
    public sealed class OperationProgress
    {
        public string Holder;
        public long LeaseUntilMs;
        /// <summary>Accepted checkpoints; partial progress survives lease loss, pauses and reconnects.</summary>
        public int StepsDone;
        public int Misses;
        public string CompletedBy;
        public long CompletedAtMs;
        public long LastStepMs;
        /// <summary>Who produced each accepted step (shared attribution when someone continues another person's work).</summary>
        public List<string> StepBy = new List<string>();
    }

    /// <summary>The one active assembly project (Addendum 02 §11 'AssemblyProjectState').</summary>
    public sealed class AssemblyProjectState
    {
        public string ProjectId;
        public string BlueprintId;
        public long StartedAtMs;
        public long CompletedAtMs;
        public Dictionary<string, OperationProgress> Ops = new Dictionary<string, OperationProgress>();
    }

    public sealed class ShelfContribution
    {
        public string Member;
        public int Operations;
        public int Steps;
    }

    /// <summary>A finished session model on the shelf, with brief contribution notes. Not a performance item or reward.</summary>
    public sealed class ShelfModel : INonProgressionRecord
    {
        [JsonIgnore] public string Domain => NonProgression.Domain;
        public string ProjectId;
        public string BlueprintId;
        public string Name;
        public long CompletedAtMs;
        public List<ShelfContribution> Contributions = new List<ShelfContribution>();
    }

    public sealed class PitCrewState
    {
        public ToyRunState Run = new ToyRunState();
        public AssemblyProjectState Project;
        public List<ShelfModel> Shelf = new List<ShelfModel>();
        /// <summary>Recoverable checkpoint taken before a destructive reset.</summary>
        public AssemblyProjectState ResetCheckpoint;
        public ConsentProposal Proposal;
        public ConsentProposal LastProposal;
    }

    /// <summary>Per-operation view for rendering "who is working on what" and the growing model.</summary>
    public sealed class OperationView
    {
        public string Id;
        public OperationStatus Status;
        public string Holder;
        public int StepsDone;
        public int Steps;
    }

    /// <summary>
    /// Pit-Crew Project (Addendum 02 §3): a persistent cooperative miniature assembly. Claims are ordered by the server and
    /// carry a short lease renewed only by real interaction; completion happens exactly once per operation id; a missed
    /// step only retries that step. Completed parts are shared facts, so late joiners render the current model directly.
    /// </summary>
    public sealed class PitCrewWorkshop : IToyActivity
    {
        public const long ClaimLeaseMs = 12_000;
        public const long MinStepIntervalMs = 250;
        public const int ShelfCapacity = 6;

        readonly IToyHost host;
        readonly PitCrewContent content;
        public PitCrewState State { get; private set; }

        public PitCrewWorkshop(IToyHost host, PitCrewContent content, PitCrewState state)
        {
            this.host = host;
            this.content = content;
            State = state ?? new PitCrewState();
            if (State.Project != null && content.Blueprint(State.Project.BlueprintId) == null)
            {
                // Content changed under a stored project: keep it recoverable instead of silently dropping it.
                State.ResetCheckpoint = State.Project;
                State.Project = null;
            }
            if (State.Project == null)
                State.Project = NewProject(content.Blueprints[0].Id, host.NowMs);
        }

        public ToyActivityId Id => ToyActivityId.PitCrew;
        public ToyRunState Run => State.Run;
        public bool RequiresExplicitResume => false;
        public bool NeedsSimulation => false;
        public object StateObject => State;
        public BlueprintDef Blueprint => content.Blueprint(State.Project.BlueprintId);
        public bool IsTransient(string kind) => false;
        public bool TakesControl(string kind) => kind == "task.claim" || kind == "task.renew" || kind == "task.step";

        AssemblyProjectState NewProject(string blueprintId, long nowMs) =>
            new AssemblyProjectState { ProjectId = host.NextId("project"), BlueprintId = blueprintId, StartedAtMs = nowMs };

        OperationProgress Progress(string opId, bool create)
        {
            if (State.Project.Ops.TryGetValue(opId, out OperationProgress p)) return p;
            if (!create) return null;
            p = new OperationProgress();
            State.Project.Ops[opId] = p;
            return p;
        }

        public bool IsDone(string opId) => State.Project.Ops.TryGetValue(opId, out OperationProgress p) && p.CompletedBy != null;

        public OperationStatus StatusOf(OperationDef op)
        {
            OperationProgress p = Progress(op.Id, false);
            if (p != null && p.CompletedBy != null) return OperationStatus.Done;
            if (!op.Requires.All(IsDone)) return OperationStatus.Locked;
            if (p != null && p.Holder != null) return OperationStatus.Claimed;
            return OperationStatus.Available;
        }

        public bool IsComplete => State.Project.CompletedAtMs > 0;

        /// <summary>Parts currently installed on the model (render these; everything else is still on the bench).</summary>
        public List<string> InstalledParts()
        {
            var parts = new List<string>();
            foreach (OperationDef op in Blueprint.Operations)
                if (IsDone(op.Id)) parts.AddRange(op.Parts);
            return parts;
        }

        public List<OperationView> Operations() =>
            Blueprint.Operations.Select(op =>
            {
                OperationProgress p = Progress(op.Id, false);
                return new OperationView { Id = op.Id, Status = StatusOf(op), Holder = p?.Holder, StepsDone = p?.StepsDone ?? 0, Steps = op.Steps };
            }).ToList();

        public string StatusLine()
        {
            int done = Blueprint.Operations.Count(o => IsDone(o.Id));
            return Blueprint.Name + " " + done + "/" + Blueprint.Operations.Count;
        }

        // --------------------------------------------------------------------------------------------------------------

        public ToyResult Apply(ToyCommandContext ctx)
        {
            switch (ctx.Kind)
            {
                case "task.claim": return Claim(ctx);
                case "task.renew": return Renew(ctx);
                case "task.step": return Step(ctx);
                case "task.release": return Release(ctx);
                case "project.start": return Start(ctx);
                case "project.reset": return ProposeReset(ctx);
                case "project.restore": return RestoreCheckpoint(ctx);
                case "proposal.vote": return Vote(ctx);
                default: return ToyResult.Reject(ToyReason.UnknownKind);
            }
        }

        OperationDef Op(ToyCommandContext ctx)
        {
            OperationDef op = Blueprint.Operation(ctx.P.Id("op"));
            if (op == null) throw new ToyPayloadException(ToyReason.NotFound, "unknown operation");
            return op;
        }

        ToyResult Claim(ToyCommandContext ctx)
        {
            OperationDef op = Op(ctx);
            OperationStatus status = StatusOf(op);
            if (status == OperationStatus.Done) return ToyResult.Reject(ToyReason.AlreadyDone);
            if (status == OperationStatus.Locked) return ToyResult.Reject(ToyReason.NotAllowed, "earlier assembly steps come first");
            OperationProgress p = Progress(op.Id, true);
            if (p.Holder != null && p.Holder != ctx.Member) return ToyResult.Reject(ToyReason.LeaseHeld, p.Holder);
            // One claim per person: claiming another operation releases the previous one (its progress stays).
            foreach (KeyValuePair<string, OperationProgress> kv in State.Project.Ops)
                if (kv.Key != op.Id && kv.Value.Holder == ctx.Member) kv.Value.Holder = null;
            p.Holder = ctx.Member;
            p.LeaseUntilMs = ctx.NowMs + ClaimLeaseMs;
            return ToyResult.Ok(op.Id);
        }

        ToyResult Renew(ToyCommandContext ctx)
        {
            OperationDef op = Op(ctx);
            OperationProgress p = Progress(op.Id, false);
            if (p == null || p.Holder != ctx.Member) return ToyResult.Reject(ToyReason.NoLease);
            p.LeaseUntilMs = ctx.NowMs + ClaimLeaseMs;
            return ToyResult.Ok(op.Id);
        }

        ToyResult Release(ToyCommandContext ctx)
        {
            OperationDef op = Op(ctx);
            OperationProgress p = Progress(op.Id, false);
            if (p == null || p.Holder != ctx.Member) return ToyResult.Reject(ToyReason.NoLease);
            p.Holder = null;
            return ToyResult.Ok(op.Id);
        }

        /// <summary>
        /// Reports one checkpoint of the local interaction: the measured error of that step (family units). Within tolerance
        /// the step is accepted (partial progress is durable); outside it only that step retries locally — nothing is undone.
        /// </summary>
        ToyResult Step(ToyCommandContext ctx)
        {
            OperationDef op = Op(ctx);
            int step = ctx.P.Int("step", 0, 16);
            double error = ctx.P.Double("error", 0, OperationFamilies.MaxReportedError(op.Family));
            OperationProgress p = Progress(op.Id, false);
            if (p != null && p.CompletedBy != null) return ToyResult.Reject(ToyReason.AlreadyDone);
            if (p == null || p.Holder != ctx.Member) return ToyResult.Reject(ToyReason.NoLease, "claim the task first");
            if (step < p.StepsDone) return ToyResult.Reject(ToyReason.AlreadyDone, "step already accepted");
            if (step > p.StepsDone || step >= op.Steps) return ToyResult.Reject(ToyReason.OutOfOrder);
            if (p.LastStepMs > 0 && ctx.NowMs - p.LastStepMs < MinStepIntervalMs) return ToyResult.Reject(ToyReason.RateLimited);
            p.LastStepMs = ctx.NowMs;
            p.LeaseUntilMs = ctx.NowMs + ClaimLeaseMs; // real interaction renews the lease
            if (error > op.StepTolerance)
            {
                p.Misses++;
                return ToyResult.Ok("retry");
            }
            p.StepsDone++;
            p.StepBy.Add(ctx.Member);
            if (p.StepsDone < op.Steps) return ToyResult.Ok("step");
            p.CompletedBy = ctx.Member;
            p.CompletedAtMs = ctx.NowMs;
            p.Holder = null;
            if (Blueprint.Operations.All(o => IsDone(o.Id))) CompleteProject(ctx.NowMs);
            return ToyResult.Ok("done");
        }

        void CompleteProject(long nowMs)
        {
            AssemblyProjectState proj = State.Project;
            proj.CompletedAtMs = nowMs;
            var contributions = new Dictionary<string, ShelfContribution>(StringComparer.Ordinal);
            foreach (OperationProgress p in proj.Ops.Values)
            {
                if (p.CompletedBy != null) Note(contributions, p.CompletedBy).Operations++;
                foreach (string m in p.StepBy) Note(contributions, m).Steps++;
            }
            State.Shelf.Add(new ShelfModel
            {
                ProjectId = proj.ProjectId, BlueprintId = proj.BlueprintId, Name = Blueprint.Name, CompletedAtMs = nowMs,
                Contributions = contributions.Values.OrderByDescending(c => c.Operations).ThenBy(c => c.Member, StringComparer.Ordinal).ToList(),
            });
            // Explained shelf limit: the oldest finished model makes room for the newest.
            while (State.Shelf.Count > ShelfCapacity) State.Shelf.RemoveAt(0);
        }

        static ShelfContribution Note(Dictionary<string, ShelfContribution> contributions, string member)
        {
            if (!contributions.TryGetValue(member, out ShelfContribution c)) contributions[member] = c = new ShelfContribution { Member = member };
            return c;
        }

        bool HasProgress(AssemblyProjectState p) => p.Ops.Values.Any(o => o.StepsDone > 0 || o.CompletedBy != null);

        ToyResult Start(ToyCommandContext ctx)
        {
            string blueprint = ctx.P.Id("blueprint");
            if (content.Blueprint(blueprint) == null) return ToyResult.Reject(ToyReason.NotFound);
            if (!IsComplete && HasProgress(State.Project))
                return ToyResult.Reject(ToyReason.ConsentRequired, "an unfinished project can only be replaced by an approved reset");
            State.Project = NewProject(blueprint, ctx.NowMs);
            return ToyResult.Ok(State.Project.ProjectId);
        }

        /// <summary>Contributors of the current project who are still active members (their approval is required).</summary>
        List<string> ActiveContributors()
        {
            var list = new SortedSet<string>(StringComparer.Ordinal);
            foreach (OperationProgress p in State.Project.Ops.Values)
            {
                if (p.CompletedBy != null && host.IsActiveMember(p.CompletedBy)) list.Add(p.CompletedBy);
                foreach (string m in p.StepBy) if (host.IsActiveMember(m)) list.Add(m);
                if (p.Holder != null && host.IsActiveMember(p.Holder)) list.Add(p.Holder);
            }
            foreach (string m in host.ActiveUsers(Id)) list.Add(m);
            return list.ToList();
        }

        ToyResult ProposeReset(ToyCommandContext ctx)
        {
            string blueprint = ctx.P.Id("blueprint");
            if (content.Blueprint(blueprint) == null) return ToyResult.Reject(ToyReason.NotFound);
            if (State.Proposal != null && State.Proposal.State == ProposalState.Open) return ToyResult.Reject(ToyReason.Busy);
            State.Proposal = ConsentProposal.Open(host.NextId("prop"), "reset", blueprint, ctx.Member, ActiveContributors(), ctx.NowMs);
            string id = State.Proposal.Id;
            ResolveProposal(ctx.NowMs);
            return ToyResult.Ok(id);
        }

        ToyResult Vote(ToyCommandContext ctx)
        {
            string id = ctx.P.Id("proposal");
            bool accept = ctx.P.Bool("accept");
            if (State.Proposal == null || State.Proposal.Id != id) return ToyResult.Reject(ToyReason.NotFound);
            if (!State.Proposal.Vote(ctx.Member, accept)) return ToyResult.Reject(ToyReason.ProposalClosed);
            ResolveProposal(ctx.NowMs);
            return ToyResult.Ok(id);
        }

        void ResolveProposal(long nowMs)
        {
            ConsentProposal p = State.Proposal;
            if (p == null) return;
            p.Retain(host.IsActiveMember);
            p.Tick(nowMs);
            if (p.State == ProposalState.Open) return;
            if (p.State == ProposalState.Accepted)
            {
                // Recoverable checkpoint first, then the approved reset.
                State.ResetCheckpoint = JsonClone(State.Project);
                foreach (OperationProgress o in State.ResetCheckpoint.Ops.Values) o.Holder = null;
                State.Project = NewProject(p.Argument, nowMs);
                Run.Epoch++;
            }
            State.LastProposal = p;
            State.Proposal = null;
        }

        /// <summary>Brings back the pre-reset checkpoint when the current project has no accepted work yet (non-destructive).</summary>
        ToyResult RestoreCheckpoint(ToyCommandContext ctx)
        {
            if (State.ResetCheckpoint == null) return ToyResult.Reject(ToyReason.NotFound);
            if (!IsComplete && HasProgress(State.Project)) return ToyResult.Reject(ToyReason.ConsentRequired, "the current project already has work in it");
            State.Project = State.ResetCheckpoint;
            State.ResetCheckpoint = null;
            Run.Epoch++;
            return ToyResult.Ok(State.Project.ProjectId);
        }

        static AssemblyProjectState JsonClone(AssemblyProjectState p) =>
            JsonConvert.DeserializeObject<AssemblyProjectState>(JsonConvert.SerializeObject(p));

        // --------------------------------------------------------------------------------------------------------------

        public void Advance(long nowMs)
        {
            foreach (OperationProgress p in State.Project.Ops.Values)
                if (p.Holder != null && nowMs >= p.LeaseUntilMs) p.Holder = null; // idle: control released, progress kept
            ResolveProposal(nowMs);
        }

        public void Freeze(long nowMs)
        {
            Run.Frozen = true;
            foreach (OperationProgress p in State.Project.Ops.Values) p.Holder = null;
        }

        public void EndPause(long nowMs) { Run.Frozen = false; }

        public void Resume(string member, long nowMs) { Run.Frozen = false; }

        public void ReleaseControl(string member, ReleaseCause cause, long nowMs)
        {
            foreach (OperationProgress p in State.Project.Ops.Values)
                if (p.Holder == member) p.Holder = null;
            ResolveProposal(nowMs);
        }

        public void Retire(string member, long nowMs) => ReleaseControl(member, ReleaseCause.Departed, nowMs);
    }
}
