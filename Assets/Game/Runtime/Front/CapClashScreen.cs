using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.CapClash;
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
    /// Cap Clash (Addendum 02 §2): aim (angle, power, launch position) with a preview traced by the Core physics, then
    /// queue ONE shot per person; if the board changed since you aimed you get a short reconfirm. The authority fires,
    /// settles and scores; this screen shows the board, standings with the crown, your bests and the cooperative
    /// six-mark card. Equal physics for every cap; nothing here is progression.
    /// </summary>
    public sealed class CapClashScreen : UIScreen
    {
        public override string ScreenName => "Cap Clash";
        public override string MusicCue => "MUS_GARAGE";

        ToyConnection toys;
        CapClashContent content;
        CapClashView view;
        TextMeshProUGUI aimText, board, card, status, prompt;
        Button shoot, back;
        Stepper target, arrangement;
        InputAction fire, aimX, aimY, slide;
        float angle, power = 0.6f, launchX;
        bool aimDirty = true;
        CapClashState lastState;
        List<CapBody> reckoned = new List<CapBody>();
        double pendingSeconds;
        public System.Func<CapArrangementDef, CapTargetDef, (float angle, float power, float launchX)?> AutoAim;
        public int MyShots => Table()?.Board.History.Count(h => h.Member == toys?.Member) ?? 0;
        public int MyOnBoardShots => Table()?.Board.History.Count(h => h.Member == toys?.Member && h.OnBoard) ?? 0;

        protected override void OnBuild(RectTransform root)
        {
            Image left = UIFactory.Panel("Info", root, new Vector2(0, 0.08f), new Vector2(0.28f, 0.97f), new Vector2(24, 0), Vector2.zero, new Color(0.04f, 0.045f, 0.05f, 0.88f));
            RectTransform col = UIFactory.Column("InfoColumn", left.transform, Vector2.zero, Vector2.one, new Vector2(28, 20), new Vector2(-20, -24), 8f);
            UIFactory.Row("Title", col, "CAP CLASH", SignalTheme.Heading, SignalTheme.Label, 480, 0, true);
            UIFactory.Row("Domain", col, "While We Wait · equal caps for everyone; for fun only — no pay, rank or unlocks.", SignalTheme.Small, SignalTheme.Caution, 480, 48);
            aimText = UIFactory.Row("Aim", col, "", SignalTheme.Body, SignalTheme.Label, 480, 110);
            aimText.richText = true;
            board = UIFactory.Row("Board", col, "", SignalTheme.Small, SignalTheme.Label, 480, 230);
            board.richText = true;
            card = UIFactory.Row("Card", col, "", SignalTheme.Small, SignalTheme.Label, 480, 190);
            card.richText = true;

            RectTransform right = UIFactory.Column("Controls", root, new Vector2(0.76f, 0.08f), new Vector2(0.99f, 0.97f), Vector2.zero, Vector2.zero, 8f);
            shoot = UIFactory.Button("CapShoot", right, "Queue Shot", Shoot, 420, 60);
            target = new Stepper(right, "Target", 1, i => TargetName(i), 0, 420, 0.24f);
            UIFactory.Button("ProposeTarget", right, "Propose Target", () => Propose("target.select", "target", TargetId(target.Index)), 420, 46);
            arrangement = new Stepper(right, "Table", 1, i => content != null && i < content.Arrangements.Count ? content.Arrangements[i].Name : "—", 0, 420, 0.24f);
            UIFactory.Button("ProposeArrangement", right, "Propose Table", () => Propose("arrangement.select", "arrangement", content.Arrangements[arrangement.Index].Id), 420, 46);
            back = UIFactory.Button("Back", right, "Leave the Table", () => App.Router.Back(), 420, 50);
            status = UIFactory.Row("Status", right, "", SignalTheme.Small, SignalTheme.Caution, 420, 80);
            prompt = UIFactory.Label("Prompt", root, "", SignalTheme.Subheading, SignalTheme.Label, TextAlignmentOptions.Center, true);
            prompt.rectTransform.anchorMin = new Vector2(0.3f, 0.9f);
            prompt.rectTransform.anchorMax = new Vector2(0.75f, 0.97f);
            prompt.rectTransform.offsetMin = prompt.rectTransform.offsetMax = Vector2.zero;
            TextMeshProUGUI help = UIFactory.Label("Help", root, "Aim: A/D or left stick · Power: W/S or triggers · Launch: Q/E or shoulders · Shoot: Space / A",
                SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.Center);
            help.rectTransform.anchorMin = new Vector2(0.2f, 0.01f);
            help.rectTransform.anchorMax = new Vector2(0.8f, 0.06f);
            help.rectTransform.offsetMin = help.rectTransform.offsetMax = Vector2.zero;

            fire = new InputAction("Fire", InputActionType.Button);
            fire.AddBinding("<Keyboard>/space");
            fire.AddBinding("<Gamepad>/buttonSouth");
            aimX = new InputAction("AimX", InputActionType.Value);
            aimX.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/a").With("Positive", "<Keyboard>/d");
            aimX.AddBinding("<Gamepad>/leftStick/x");
            aimY = new InputAction("Power", InputActionType.Value);
            aimY.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/s").With("Positive", "<Keyboard>/w");
            aimY.AddCompositeBinding("1DAxis").With("Negative", "<Gamepad>/leftTrigger").With("Positive", "<Gamepad>/rightTrigger");
            slide = new InputAction("Slide", InputActionType.Value);
            slide.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/q").With("Positive", "<Keyboard>/e");
            slide.AddCompositeBinding("1DAxis").With("Negative", "<Gamepad>/leftShoulder").With("Positive", "<Gamepad>/rightShoulder");
        }

        public override Selectable DefaultFocus => shoot;

        public override void OnShow()
        {
            ToyContent all = ContentLibrary.Load()?.Toys;
            content = all?.CapClash;
            toys = all != null ? ToyConnection.Create(all, App) : null;
            if (toys == null) { status.text = "Open a Local profile (or join a convoy) first."; return; }
            toys.Enter(ToyActivityId.CapClash);
            view = CapClashView.Create();
            App.SetBackdropVisible(false);
            arrangement.SetCount(content.Arrangements.Count);
            foreach (InputAction a in new[] { fire, aimX, aimY, slide }) a.Enable();
            lastState = null;
            aimDirty = true;
        }

        public override void OnHide()
        {
            foreach (InputAction a in new[] { fire, aimX, aimY, slide }) a.Disable();
            toys?.Leave(ToyActivityId.CapClash);
            toys = null;
            if (view != null) Object.Destroy(view.gameObject);
            view = null;
            App.SetBackdropVisible(true);
        }

        /// <summary>The Core table over the latest state (the authority locally; a query-only mirror online).</summary>
        CapClashTable Table()
        {
            CapClashState st = toys?.State<CapClashState>(ToyActivityId.CapClash);
            return st == null ? null : new CapClashTable(MirrorToyHost.Instance, content, st);
        }

        string TargetId(int i)
        {
            CapClashTable t = Table();
            return t != null && i < t.ActiveArrangement.Targets.Count ? t.ActiveArrangement.Targets[i].Id : null;
        }

        string TargetName(int i)
        {
            CapClashTable t = Table();
            if (t == null || i >= t.ActiveArrangement.Targets.Count) return "—";
            CapTargetDef d = t.ActiveArrangement.Targets[i];
            return d.Name + (d.Bank ? " (bank)" : "");
        }

        public override void Tick()
        {
            if (toys == null) return;
            toys.Tick();
            CapClashTable t = Table();
            if (t == null) return;
            CapArrangementDef a = t.ActiveArrangement;
            view.SetBoard(a, t.Board.TargetId);
            if (target.Count != a.Targets.Count) { target.SetCount(a.Targets.Count); target.Set(a.Targets.FindIndex(x => x.Id == t.Board.TargetId)); }

            // Aim input.
            float dt = Time.unscaledDeltaTime;
            float ax = aimX.ReadValue<float>(), ay = aimY.ReadValue<float>(), sl = slide.ReadValue<float>();
            if (Mathf.Abs(ax) + Mathf.Abs(ay) + Mathf.Abs(sl) > 0.01f) aimDirty = true;
            angle = Mathf.Clamp(angle + ax * 25f * dt, -(float)a.MaxAngleDeg, (float)a.MaxAngleDeg);
            power = Mathf.Clamp(power + ay * 0.45f * dt, (float)t.Physics.MinPower, 1f);
            launchX = Mathf.Clamp(launchX + sl * 0.2f * dt, (float)a.LaunchMinX, (float)a.LaunchMaxX);
            var auto = AutoAim?.Invoke(a, t.ActiveTarget);
            if (auto.HasValue)
            {
                (angle, power, launchX) = auto.Value;
                aimDirty = true;
            }
            if (aimDirty) { view.ShowAim(PreviewPath(t, a)); aimDirty = false; }

            // Caps: the authority's positions locally; online, reckon moving caps between pushes with the same physics.
            IEnumerable<CapBody> bodies = t.Board.Caps;
            if (toys.Online)
            {
                CapClashState st = toys.State<CapClashState>(ToyActivityId.CapClash);
                if (!ReferenceEquals(st, lastState))
                {
                    lastState = st;
                    reckoned = t.Board.Caps.Select(c => new CapBody { CapId = c.CapId, Owner = c.Owner, Pos = c.Pos, Vel = c.Vel, Moving = c.Moving }).ToList();
                    pendingSeconds = 0;
                }
                if (!toys.Frozen(ToyActivityId.CapClash) && reckoned.Any(c => c.Moving))
                {
                    pendingSeconds = System.Math.Min(1.0, pendingSeconds + dt);
                    double step = 1.0 / t.Physics.StepHz;
                    for (; pendingSeconds >= step; pendingSeconds -= step) CapPhysics.Step(a, t.Physics, reckoned, null);
                }
                bodies = reckoned;
            }
            view.Render(bodies, toys.Member);

            ShotRequest mine = t.State.Queue.FirstOrDefault(q => q.Member == toys.Member);
            bool reconfirm = mine != null && mine.State == ShotQueueState.AwaitingReconfirm;
            if (fire.WasPressedThisFrame() && EventSystem.current?.currentSelectedGameObject == null) Shoot();
            prompt.text = reconfirm ? "The board changed since you aimed — Queue Shot again to confirm" :
                mine != null ? $"Queued · {t.State.Queue.IndexOf(mine) + 1} of {t.State.Queue.Count}" :
                !t.IsSettled ? "Caps are moving…" : "";
            shoot.GetComponentInChildren<TextMeshProUGUI>().text = reconfirm ? "Confirm Shot" : mine != null ? "Update Aim" : "Queue Shot";
            Render(t);
        }

        List<Vector2> PreviewPath(CapClashTable t, CapArrangementDef a)
        {
            // The same launch + step the authority runs; other caps as currently settled. The authority's result wins.
            var copy = t.Board.Caps.Where(c => c.Owner != toys.Member).Select(c => new CapBody { CapId = c.CapId, Owner = c.Owner, Pos = c.Pos, Vel = c.Vel, Moving = false }).ToList();
            var mine = new CapBody { CapId = "preview", Owner = toys.Member };
            copy.Add(mine);
            CapPhysics.Launch(a, t.Physics, mine, angle, power, launchX);
            var path = new List<Vector2> { new Vector2((float)mine.Pos.X, (float)mine.Pos.Y) };
            for (int i = 0; i < t.Physics.StepHz * 8 && mine.Moving && copy.Contains(mine); i++)
            {
                CapPhysics.Step(a, t.Physics, copy, null);
                if (i % 6 == 0) path.Add(new Vector2((float)mine.Pos.X, (float)mine.Pos.Y));
            }
            path.Add(new Vector2((float)mine.Pos.X, (float)mine.Pos.Y));
            return path;
        }

        void Shoot()
        {
            CapClashTable t = Table();
            if (t == null) return;
            ShotRequest mine = t.State.Queue.FirstOrDefault(q => q.Member == toys.Member);
            var payload = new JObject { ["angle"] = System.Math.Round(angle, 2), ["power"] = System.Math.Round(power, 3), ["launchX"] = System.Math.Round(launchX, 4), ["seen"] = t.Board.BoardRevision };
            bool reconfirm = mine != null && mine.State == ShotQueueState.AwaitingReconfirm && t.State.Queue.IndexOf(mine) == 0;
            toys.Send(ToyActivityId.CapClash, reconfirm ? "shot.confirm" : "shot.submit", payload, a => { if (!a.Accepted) status.text = a.Reason; });
        }

        void Propose(string kind, string field, string id)
        {
            if (toys == null || id == null) return;
            toys.Send(ToyActivityId.CapClash, kind, new JObject { [field] = id }, a => status.text = a.Accepted ? "Proposed — everyone at the table agrees or it lapses." : a.Reason);
        }

        void Render(CapClashTable t)
        {
            CapArrangementDef a = t.ActiveArrangement;
            aimText.text = $"<color=#9A968D>{a.Name.ToUpperInvariant()}</color>  ·  {t.ActiveTarget?.Name}\n" +
                           $"Angle <b>{angle:+0.0;-0.0;0.0}°</b>   Power <b>{power * 100:0}%</b>   Launch <b>{launchX * 100:+0;-0;0} cm</b>";
            var sb = new System.Text.StringBuilder("<color=#9A968D>STANDINGS (closest settled cap)</color>\n");
            foreach (CapStanding s in t.Standings().Take(6))
                sb.Append($"{(s.Crown ? "<color=#F2A541>CROWN</color> " : "")}{Who(s.Member)}  {(s.CurrentDistance.HasValue ? (s.CurrentDistance.Value * 100).ToString("0.0") + " cm" : "—")}  {s.CurrentPoints} pts" +
                          $"  <size=80%>best {(s.BestDistance.HasValue ? (s.BestDistance.Value * 100).ToString("0.0") + " cm" : "—")}</size>\n");
            ShotOutcome last = t.Board.History.LastOrDefault();
            if (last != null) sb.Append($"\nLast shot: {Who(last.Member)} — {(last.OnBoard ? $"{last.Points} pts at {last.Distance * 100:0.0} cm" : "off the scoring area")}{(last.Banked ? " · banked" : "")}{(last.CapContacts > 0 ? $" · {last.CapContacts} cap contact(s)" : "")}");
            board.text = sb.ToString();
            var cb = new System.Text.StringBuilder($"<color=#9A968D>TARGET CARD ({t.Board.Card.Count}/{a.Card.Count}) · completed {t.Board.CardsCompleted}×</color>\n");
            foreach (CapMarkDef m in a.Card)
                cb.Append(t.Board.Card.TryGetValue(m.Id, out string by) ? $"<color=#3EC6D8>done</color>  {m.Label} <size=80%>({Who(by)})</size>\n" : $"·  {m.Label}\n");
            card.text = cb.ToString();
            if (string.IsNullOrEmpty(status.text) || status.text == toys.Status) status.text = toys.Status;
        }

        string Who(string member) => member == toys.Member ? "you" : "driver " + (member ?? "").Substring(System.Math.Max(0, (member ?? "").Length - 4));
    }
}
