using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.PitCrew;
using NightSignal.Toys;
using NightSignal.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Pit-Crew Project (Addendum 02 §3): claim an available operation (the authority orders claims and holds a short
    /// lease renewed only by real interaction), then do its steps — a forgiving precision moment per step in the
    /// operation's own unit (millimetres, degrees or gauge fraction). A miss only retries that step; nothing is undone.
    /// Completed parts appear on the shared model; finished models go on the shelf with contribution notes. Nothing here
    /// is progression.
    /// </summary>
    public sealed class PitCrewScreen : UIScreen
    {
        public override string ScreenName => "Pit-Crew";
        public override string MusicCue => "MUS_GARAGE";

        ToyConnection toys;
        PitCrewContent content;
        PitCrewView view;
        TextMeshProUGUI header, taskLine, shelf, status, gaugeLabel;
        readonly List<Button> taskButtons = new List<Button>();
        readonly List<string> taskIds = new List<string>();
        Button lockIn, release, nextModel, back;
        Stepper blueprint;
        RectTransform gauge, needle, band;
        InputAction lockAction;
        string workingOp;
        float stepStartedAt, lastRenew;
        float phase;
        bool stepSent;
        public System.Func<double, bool> AutoLock;
        public int OperationsDoneByMe => Workshop()?.State.Project.Ops.Values.Count(o => o.CompletedBy == toys?.Member) ?? 0;
        /// <summary>Operations on the shared model completed by anyone else.</summary>
        public int OperationsDoneByOthers => Workshop()?.State.Project.Ops.Values.Count(o => !string.IsNullOrEmpty(o.CompletedBy) && o.CompletedBy != toys?.Member) ?? 0;

        protected override void OnBuild(RectTransform root)
        {
            Image left = UIFactory.Panel("Info", root, new Vector2(0, 0.08f), new Vector2(0.34f, 0.97f), new Vector2(24, 0), Vector2.zero, new Color(0.04f, 0.045f, 0.05f, 0.9f));
            RectTransform col = UIFactory.Column("InfoColumn", left.transform, Vector2.zero, Vector2.one, new Vector2(28, 20), new Vector2(-20, -24), 7f);
            UIFactory.Row("Title", col, "PIT-CREW PROJECT", SignalTheme.Heading, SignalTheme.Label, 580, 0, true);
            UIFactory.Row("Domain", col, "While We Wait · build a model together, one task each. For fun only — it never pays, ranks or unlocks.", SignalTheme.Small, SignalTheme.Caution, 580, 48);
            header = UIFactory.Row("Project", col, "", SignalTheme.Body, SignalTheme.Label, 580, 62);
            header.richText = true;
            UIFactory.Row("TasksHeading", col, "AVAILABLE TASKS", SignalTheme.Small, SignalTheme.LabelDim, 580, 24, true);
            for (int i = 0; i < 7; i++)
            {
                int index = i;
                Button b = UIFactory.Button("Task" + i, col, "", () => Claim(index), 560, 44);
                TextMeshProUGUI l = b.GetComponentInChildren<TextMeshProUGUI>();
                l.fontStyle = FontStyles.Normal;
                l.fontSize = SignalTheme.Small * SignalTheme.TextScale;
                l.richText = false;
                taskButtons.Add(b);
            }
            shelf = UIFactory.Row("Shelf", col, "", SignalTheme.Small, SignalTheme.Label, 580, 120);
            shelf.richText = true;

            RectTransform right = UIFactory.Column("Controls", root, new Vector2(0.72f, 0.08f), new Vector2(0.99f, 0.97f), Vector2.zero, Vector2.zero, 8f);
            taskLine = UIFactory.Row("TaskLine", right, "", SignalTheme.Body, SignalTheme.Label, 500, 110);
            taskLine.richText = true;
            gauge = UIFactory.Rect("Gauge", right, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            gauge.sizeDelta = new Vector2(500, 70);
            UIFactory.Panel("GaugeTrack", gauge, new Vector2(0, 0.3f), new Vector2(1, 0.7f), Vector2.zero, Vector2.zero, new Color(0.12f, 0.12f, 0.14f));
            band = UIFactory.Panel("Band", gauge, new Vector2(0.45f, 0.2f), new Vector2(0.55f, 0.8f), Vector2.zero, Vector2.zero, new Color(0.2f, 0.75f, 0.4f, 0.9f)).rectTransform;
            needle = UIFactory.Panel("Needle", gauge, new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(-3, 0), new Vector2(3, 0), SignalTheme.Signal).rectTransform;
            gaugeLabel = UIFactory.Row("GaugeLabel", right, "", SignalTheme.Small, SignalTheme.LabelDim, 500, 30);
            lockIn = UIFactory.Button("PitLock", right, "Lock In  (Space / A)", LockIn, 480, 56);
            release = UIFactory.Button("PitRelease", right, "Release Task", Release, 480, 46);
            blueprint = new Stepper(right, "Next", 1, i => content != null && i < content.Blueprints.Count ? content.Blueprints[i].Name : "—", 0, 500, 0.2f);
            nextModel = UIFactory.Button("PitNextModel", right, "Start This Model", StartModel, 480, 46);
            back = UIFactory.Button("Back", right, "Leave the Bench", () => App.Router.Back(), 480, 50);
            status = UIFactory.Row("Status", right, "", SignalTheme.Small, SignalTheme.Caution, 500, 60);

            lockAction = new InputAction("Lock", InputActionType.Button);
            lockAction.AddBinding("<Keyboard>/space");
            lockAction.AddBinding("<Gamepad>/rightTrigger");
        }

        public override Selectable DefaultFocus => back;

        public override void OnShow()
        {
            ToyContent all = ContentLibrary.Load()?.Toys;
            content = all?.PitCrew;
            toys = all != null ? ToyConnection.Create(all, App) : null;
            if (toys == null) { status.text = "Open a Local profile (or join a convoy) first."; return; }
            toys.Enter(ToyActivityId.PitCrew);
            view = PitCrewView.Create();
            App.SetBackdropVisible(false);
            blueprint.SetCount(content.Blueprints.Count);
            lockAction.Enable();
            workingOp = null;
        }

        public override void OnHide()
        {
            lockAction.Disable();
            if (toys != null && workingOp != null) toys.Send(ToyActivityId.PitCrew, "task.release", new JObject { ["op"] = workingOp }); // progress is kept
            toys?.Leave(ToyActivityId.PitCrew);
            toys = null;
            if (view != null) Object.Destroy(view.gameObject);
            view = null;
            App.SetBackdropVisible(true);
        }

        PitCrewWorkshop Workshop()
        {
            PitCrewState st = toys?.State<PitCrewState>(ToyActivityId.PitCrew);
            return st == null ? null : new PitCrewWorkshop(MirrorToyHost.Instance, content, st);
        }

        /// <summary>Half-range of the precision gauge per family, in that family's unit.</summary>
        static double Range(OperationFamily f)
        {
            switch (f)
            {
                case OperationFamily.RotateIndex: return 60;
                case OperationFamily.MatchMarks: return 45;
                case OperationFamily.ControlledTighten: return 0.5;
                case OperationFamily.ConnectRoute: return 25;
                default: return 20;
            }
        }

        static string Unit(OperationFamily f) => f == OperationFamily.ControlledTighten ? "" : f == OperationFamily.RotateIndex || f == OperationFamily.MatchMarks ? "°" : " mm";

        static string Verb(OperationFamily f)
        {
            switch (f)
            {
                case OperationFamily.AlignFit: return "Slide the part until it sits centred — lock in on the green.";
                case OperationFamily.RotateIndex: return "Turn it until the index lines up — lock in on the green.";
                case OperationFamily.ControlledTighten: return "Tighten to the mark, not past it — lock in on the green.";
                case OperationFamily.ConnectRoute: return "Guide the clip onto its peg — lock in on the green.";
                case OperationFamily.MatchMarks: return "Match the paint marks — lock in on the green.";
                default: return "Place the detail on its spot — lock in on the green.";
            }
        }

        public override void Tick()
        {
            if (toys == null) return;
            toys.Tick();
            PitCrewWorkshop w = Workshop();
            if (w == null) return;
            view.SetBlueprint(w.Blueprint);
            view.Render(new HashSet<string>(w.InstalledParts()));
            List<OperationView> ops = w.Operations();

            // Tasks: mine first, then available ones in dependency order.
            OperationView mine = ops.FirstOrDefault(o => o.Status == OperationStatus.Claimed && o.Holder == toys.Member);
            workingOp = mine?.Id;
            taskIds.Clear();
            foreach (OperationView o in ops.Where(o => o.Status == OperationStatus.Available || o.Status == OperationStatus.Claimed).Take(taskButtons.Count))
            {
                OperationDef def = w.Blueprint.Operation(o.Id);
                string who = o.Status == OperationStatus.Claimed ? (o.Holder == toys.Member ? "  · yours" : "  · in use") : "";
                taskButtons[taskIds.Count].GetComponentInChildren<TextMeshProUGUI>().text = $"{def.Label}  ({o.StepsDone}/{o.Steps}){who}";
                taskButtons[taskIds.Count].interactable = o.Status == OperationStatus.Available || o.Holder == toys.Member;
                taskIds.Add(o.Id);
            }
            for (int i = 0; i < taskButtons.Count; i++) taskButtons[i].gameObject.SetActive(i < taskIds.Count);

            int done = ops.Count(o => o.Status == OperationStatus.Done);
            header.text = $"<b>{w.Blueprint.Name}</b>  ·  {done} / {ops.Count} operations\n<size=85%>{w.StatusLine()}</size>";
            var sb = new System.Text.StringBuilder("<color=#9A968D>SHELF</color>\n");
            foreach (ShelfModel m in w.State.Shelf.AsEnumerable().Reverse().Take(3))
                sb.Append($"{m.Name}  <size=80%>({string.Join(", ", m.Contributions.Select(c => (c.Member == toys.Member ? "you" : "driver") + " ×" + c.Operations))})</size>\n");
            if (w.State.Shelf.Count == 0) sb.Append("Finished models go here.");
            shelf.text = sb.ToString();

            bool complete = w.IsComplete;
            blueprint.Root.SetActive(complete);
            nextModel.gameObject.SetActive(complete);
            gauge.gameObject.SetActive(mine != null);
            lockIn.gameObject.SetActive(mine != null);
            release.gameObject.SetActive(mine != null);
            if (mine != null) WorkOn(w, mine);
            else
            {
                taskLine.text = complete ? "<b>Model finished!</b>  Choose the next one." : "Pick a task on the left to claim it.";
                gaugeLabel.text = "";
            }
            if (!string.IsNullOrEmpty(toys.Status)) status.text = toys.Status;
        }

        void WorkOn(PitCrewWorkshop w, OperationView o)
        {
            OperationDef def = w.Blueprint.Operation(o.Id);
            double range = Range(def.Family), tol = def.StepTolerance;
            taskLine.text = $"<b>{def.Label}</b>  ·  step {o.StepsDone + 1} of {o.Steps}\n<size=85%>{Verb(def.Family)}</size>";
            band.anchorMin = new Vector2((float)(0.5 - tol / range / 2), 0.2f);
            band.anchorMax = new Vector2((float)(0.5 + tol / range / 2), 0.8f);
            // A slow, forgiving sweep (the authored seconds set the pace).
            phase += Time.unscaledDeltaTime * (float)(Mathf.PI * 2 / System.Math.Max(2.0, def.Seconds / o.Steps));
            double value = System.Math.Sin(phase) * range;
            float x = (float)(0.5 + value / range / 2);
            needle.anchorMin = new Vector2(x, 0);
            needle.anchorMax = new Vector2(x, 1);
            gaugeLabel.text = $"off by {System.Math.Abs(value):0.0}{Unit(def.Family)}   ·   within {tol:0.##}{Unit(def.Family)} counts";
            // Keep the lease while really working on it (Core lease 12 s; interaction renews it).
            if (Time.unscaledTime - lastRenew > 5f) { lastRenew = Time.unscaledTime; toys.Send(ToyActivityId.PitCrew, "task.renew", new JObject { ["op"] = o.Id }); }
            bool press = AutoLock != null ? AutoLock(System.Math.Abs(value) / range) : lockAction.WasPressedThisFrame() && EventSystem.current?.currentSelectedGameObject == null;
            if (press && !stepSent) Step(o, System.Math.Abs(value));
        }

        void Step(OperationView o, double error)
        {
            stepSent = true;
            toys.Send(ToyActivityId.PitCrew, "task.step", new JObject { ["op"] = o.Id, ["step"] = o.StepsDone, ["error"] = System.Math.Round(error, 3) }, a =>
            {
                stepSent = false;
                status.text = !a.Accepted ? a.Reason : a.Value == "retry" ? "Not quite — try that step again." : a.Value == "done" ? "Part installed!" : "Step done.";
            });
        }

        void LockIn()
        {
            PitCrewWorkshop w = Workshop();
            OperationView o = w?.Operations().FirstOrDefault(x => x.Status == OperationStatus.Claimed && x.Holder == toys.Member);
            if (o == null || stepSent) return;
            double value = System.Math.Abs(System.Math.Sin(phase) * Range(w.Blueprint.Operation(o.Id).Family));
            Step(o, value);
        }

        void Claim(int index)
        {
            if (index >= taskIds.Count) return;
            string op = taskIds[index];
            if (op == workingOp) return;
            if (workingOp != null) toys.Send(ToyActivityId.PitCrew, "task.release", new JObject { ["op"] = workingOp });
            phase = Random.value * 6f;
            lastRenew = Time.unscaledTime;
            toys.Send(ToyActivityId.PitCrew, "task.claim", new JObject { ["op"] = op }, a => status.text = a.Accepted ? "" : a.Reason);
            EventSystem.current?.SetSelectedGameObject(null); // Space locks in, not a menu button
        }

        void Release()
        {
            if (workingOp == null) return;
            toys.Send(ToyActivityId.PitCrew, "task.release", new JObject { ["op"] = workingOp });
        }

        void StartModel()
        {
            toys.Send(ToyActivityId.PitCrew, "project.start", new JObject { ["blueprint"] = content.Blueprints[blueprint.Index].Id }, a => status.text = a.Accepted ? "" : a.Reason);
        }
    }
}
