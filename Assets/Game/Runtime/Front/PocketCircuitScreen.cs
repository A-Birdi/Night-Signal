using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.PocketCircuit;
using NightSignal.Toys;
using NightSignal.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Pocket Circuit (Addendum 02 §5), Local play: the tabletop slot-car toy driven by the authoritative Core table in
    /// an in-process session. Analog throttle (trigger or held key), take any free lane, harmless de-slots, ACTIVE-time
    /// laps with categories, per-lane bests with the disclosed lane ratio, the shared clean-lap collection, and layout
    /// changes by consent (solo: immediate). Toy results are non-progression: they never pay, rank or unlock anything.
    /// </summary>
    public sealed class PocketCircuitScreen : UIScreen
    {
        public override string ScreenName => "Pocket Circuit";
        public override string MusicCue => "MUS_GARAGE";

        IPocketCircuitSource source;
        ToyContent content;
        PocketCircuitView view;
        TextMeshProUGUI convoyLine;
        Button readyButton;
        TextMeshProUGUI title, layoutLine, lapText, boardText, status, help;
        Button[] laneButtons;
        Button[] layoutButtons;
        Button viewButton, back;
        InputAction throttleAction, viewAction;
        InputAction[] laneKeys;
        float heldKey, lastSent = -1f, nextSend;
        PocketCircuitTable Table => source?.Table;

        protected override void OnBuild(RectTransform root)
        {
            // Left column: title, live lap, board. The centre stays clear for the table.
            Image left = UIFactory.Panel("Info", root, new Vector2(0, 0.08f), new Vector2(0.28f, 0.97f), new Vector2(24, 0), Vector2.zero, new Color(0.04f, 0.045f, 0.05f, 0.82f));
            RectTransform col = UIFactory.Column("InfoColumn", left.transform, new Vector2(0, 0), new Vector2(1, 1), new Vector2(28, 20), new Vector2(-20, -24), 8f);
            title = UIFactory.Row("Title", col, "POCKET CIRCUIT", SignalTheme.Heading, SignalTheme.Label, 480, 0, true);
            layoutLine = UIFactory.Row("LayoutName", col, "", SignalTheme.Small, SignalTheme.Label, 480, 48);
            UIFactory.Row("Domain", col, "While We Wait · toy laps are for fun: they never pay, rank or unlock anything.", SignalTheme.Small, SignalTheme.Caution, 480, 48);
            lapText = UIFactory.Row("Lap", col, "", SignalTheme.Body, SignalTheme.Label, 480, 150);
            lapText.richText = true;
            boardText = UIFactory.Row("Board", col, "", SignalTheme.Small, SignalTheme.Label, 480, 330);
            boardText.richText = true;

            // Right column: lanes, layouts, view, back.
            RectTransform right = UIFactory.Column("Controls", root, new Vector2(0.78f, 0.08f), new Vector2(0.99f, 0.97f), Vector2.zero, Vector2.zero, 8f);
            convoyLine = UIFactory.Row("ConvoyLine", right, "", SignalTheme.Small, SignalTheme.Label, 380, 64);
            convoyLine.richText = true;
            readyButton = UIFactory.Button("TableReady", right, "Ready", ToggleReady, 380, 46);
            UIFactory.Row("LanesHeading", right, "LANES", SignalTheme.Small, SignalTheme.LabelDim, 380, 26, true);
            laneButtons = new Button[6];
            for (int i = 0; i < 6; i++)
            {
                int lane = i + 1;
                laneButtons[i] = UIFactory.Button("Lane" + lane, right, $"Lane {lane}", () => TakeLane(lane), 380, 46);
                Image swatch = UIFactory.Panel("Swatch", laneButtons[i].transform, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-34, 10), new Vector2(-12, -10), PocketCircuitView.LaneColours[i]);
                swatch.raycastTarget = false;
            }
            UIFactory.Row("LayoutsHeading", right, "LAYOUT", SignalTheme.Small, SignalTheme.LabelDim, 380, 26, true);
            layoutButtons = new Button[3];
            for (int i = 0; i < layoutButtons.Length; i++)
            {
                int index = i;
                layoutButtons[i] = UIFactory.Button("Layout" + i, right, "", () => ProposeLayout(index), 380, 46);
                layoutButtons[i].GetComponentInChildren<TextMeshProUGUI>().fontStyle = FontStyles.Normal;
            }
            viewButton = UIFactory.Button("View", right, "Chase View", ToggleView, 380, 46);
            back = UIFactory.Button("Back", right, "Leave the Table", () => App.Router.Back(), 380, 50);
            status = UIFactory.Row("Status", right, "", SignalTheme.Small, SignalTheme.Caution, 380, 60);

            help = UIFactory.Label("Help", root, "Throttle: right trigger · W / ↑ / Space (held)   ·   Lanes: 1–6   ·   View: C   ·   Back: Esc / B",
                SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.Center);
            help.rectTransform.anchorMin = new Vector2(0.2f, 0.01f);
            help.rectTransform.anchorMax = new Vector2(0.8f, 0.06f);
            help.rectTransform.offsetMin = help.rectTransform.offsetMax = Vector2.zero;

            throttleAction = new InputAction("Throttle", InputActionType.Value);
            throttleAction.AddBinding("<Gamepad>/rightTrigger");
            viewAction = new InputAction("View", InputActionType.Button);
            viewAction.AddBinding("<Keyboard>/c");
            viewAction.AddBinding("<Gamepad>/buttonNorth");
            laneKeys = new InputAction[6];
            for (int i = 0; i < 6; i++)
            {
                laneKeys[i] = new InputAction("Lane" + (i + 1), InputActionType.Button);
                laneKeys[i].AddBinding($"<Keyboard>/{i + 1}");
            }
        }

        public override Selectable DefaultFocus => back;

        public override void OnShow()
        {
            shownAt = Time.unscaledTime;
            content = ContentLibrary.Load()?.Toys;
            if (content == null)
            {
                status.text = "Toy content is missing from this build.";
                return;
            }
            OnlineSession online = OnlineSession.Current;
            if (App.Domain == SessionDomain.Online && online != null && online.InConvoy)
                source = new OnlineCircuitSource(online.Client, () => online.Convoy, content); // the convoy's shared table
            else
            {
                LocalSession s = LocalSession.Current;
                if (s?.Profile == null)
                {
                    status.text = "Open a Local profile first.";
                    return;
                }
                var host = new LocalToyHost(content, s.Profile.ProfileId, s.ToySnapshot(LocalToyHost.DocumentKey));
                source = new LocalCircuitSource(host, json => s.SaveToys(LocalToyHost.DocumentKey, LocalToyHost.DocumentSchema, json, out _));
            }
            convoyLine.gameObject.SetActive(source.Online);
            readyButton.gameObject.SetActive(source.Online);
            view = PocketCircuitView.Create(Table.Track);
            App.SetBackdropVisible(false);
            throttleAction.Enable();
            viewAction.Enable();
            foreach (InputAction a in laneKeys) a.Enable();
            // Returning to the table: take the lane held last time, or the first free lane (online: once the state arrives).
            laneChosen = false;
            ChooseLaneIfNeeded();
            for (int i = 0; i < layoutButtons.Length; i++)
            {
                SlotLayoutDef def = i < content.PocketCircuit.Layouts.Count ? content.PocketCircuit.Layouts[i] : null;
                layoutButtons[i].gameObject.SetActive(def != null);
                if (def != null) layoutButtons[i].GetComponentInChildren<TextMeshProUGUI>().text = def.Name;
            }
        }

        /// <summary>Leaves the table at once (a race is starting); safe to call again from the router.</summary>
        public void CloseNow() => OnHide();

        public override void OnHide()
        {
            throttleAction.Disable();
            viewAction.Disable();
            foreach (InputAction a in laneKeys) a.Disable();
            source?.Leave(); // parks this car; the board and records are kept (Local: saved in the profile)
            if (view != null) Object.Destroy(view.gameObject);
            view = null;
            source = null;
            App.SetBackdropVisible(true);
        }

        public override void Tick()
        {
            if (source == null) return;
            ChooseLaneIfNeeded();
            for (int i = 0; i < laneKeys.Length; i++) if (laneKeys[i].WasPressedThisFrame()) TakeLane(i + 1);
            if (viewAction.WasPressedThisFrame()) ToggleView();

            // Analog trigger wins; a held key ramps smoothly (digital players still get a controllable car).
            Keyboard kb = Keyboard.current;
            bool key = kb != null && (kb.wKey.isPressed || kb.upArrowKey.isPressed || kb.spaceKey.isPressed);
            heldKey = Mathf.MoveTowards(heldKey, key ? 0.72f : 0f, Time.unscaledDeltaTime * (key ? 1.4f : 4f));
            float u = Mathf.Max(throttleAction.ReadValue<float>(), heldKey);
            if (AutoThrottle != null) u = AutoThrottle(Table, source.Member);
            source.PredictThrottle(u);
            // ~20 commands/s (Core's per-member budget is 40/s) or at once on a clear change; a hold expires after 0.5 s.
            if (Time.unscaledTime >= nextSend || Mathf.Abs(u - lastSent) > 0.15f)
            {
                nextSend = Time.unscaledTime + 0.05f;
                lastSent = u;
                source.Send("throttle", new JObject { ["value"] = System.Math.Round(u, 3) });
            }
            source.Tick();
            view.Render(Table, source.Member);
            Render();
        }

        /// <summary>This member's car progress (laps × 1000 + distance), −1 without a car (tour evidence).</summary>
        public double MyCarProgress => source != null && Table?.Car(source.Member) is SlotCarState c ? c.Lap * 1000 + c.S : -1;

        public bool OnlineTable => source?.Online == true;

        /// <summary>Completed toy laps on the current board (tour evidence).</summary>
        public int CompletedLaps => Table?.Board.RecentLaps.Count ?? 0;

        /// <summary>UI tours drive the car with a scripted throttle (automation, labelled as such).</summary>
        public System.Func<PocketCircuitTable, string, float> AutoThrottle;

        void Render()
        {
            PocketCircuitTable t = Table;
            SlotCarState car = t.Car(source.Member);
            SlotLayoutDef layout = t.ActiveLayout;
            title.text = "POCKET CIRCUIT";
            layoutLine.text = layout.Name + (layout.Summary != null ? "  ·  " + layout.Summary : "");
            if (car == null) lapText.text = "Take a lane to drive.";
            else
            {
                SlotLaneStats stats = t.Track.Stats[car.Lane - 1];
                long lapMs = car.LapTicks * 1000 / t.Physics.StepHz;
                string mode = car.Mode == SlotCarMode.DeSlotted ? "<color=#F2A541>DE-SLOTTED — returning to the last piece</color>"
                    : car.Mode == SlotCarMode.Orienting ? "<color=#3EC6D8>Getting ready…</color>"
                    : car.Mode == SlotCarMode.Parked ? "Parked" : car.Lap == 0 ? "Out lap (not timed)" : $"Lap {car.Lap}";
                SlotLap last = t.Board.RecentLaps.LastOrDefault(l => l.Member == source.Member);
                SlotLaneBest best = t.Board.Bests.FirstOrDefault(b => b.Member == source.Member && b.Lane == car.Lane);
                lapText.text = $"Lane <b>{car.Lane}</b>   {mode}\n" +
                               $"<size=150%><mspace=0.58em>{Fmt(car.Lap >= 1 ? lapMs : 0)}</mspace></size>\n" +
                               $"Last {(last != null ? Fmt(last.ActiveMs) + "  " + Category(last.Category) : "—")}\n" +
                               $"Best {(best != null ? Fmt(best.BestMs) : "—")}   <size=80%>lane ratio {stats.LaneRatio:0.000} (disclosed)</size>";
            }
            var sb = new System.Text.StringBuilder("<color=#9A968D>CLEAN-LAP COLLECTION</color>\n");
            sb.Append($"{t.Board.CoopProgress} / {t.Board.CoopTarget} clean laps   ·   completed {t.Board.CoopCompleted}×\n\n<color=#9A968D>BESTS (normalized across lanes)</color>\n");
            foreach (SlotLaneBest b in t.NormalizedBoard().Take(6))
                sb.Append($"L{b.Lane}  {Fmt(b.BestMs)}  <size=80%>norm {Fmt(b.NormalizedMs)}</size>\n");
            sb.Append("\n<color=#9A968D>RECENT</color>\n");
            foreach (SlotLap l in t.Board.RecentLaps.AsEnumerable().Reverse().Take(5))
                sb.Append($"L{l.Lane}  {Fmt(l.ActiveMs)}  {Category(l.Category)}\n");
            boardText.text = sb.ToString();
            for (int i = 0; i < laneButtons.Length; i++)
            {
                bool exists = i < t.Track.Lanes.Length;
                laneButtons[i].gameObject.SetActive(exists);
                SlotCarState holder = t.Board.Cars.FirstOrDefault(c => c.Lane == i + 1);
                laneButtons[i].GetComponentInChildren<TextMeshProUGUI>().text = holder == null ? $"Lane {i + 1}" : holder.Member == source.Member ? $"Lane {i + 1}  (you)" : $"Lane {i + 1}  (taken)";
            }
            for (int i = 0; i < layoutButtons.Length; i++)
                if (layoutButtons[i].gameObject.activeSelf)
                    layoutButtons[i].interactable = content.PocketCircuit.Layouts[i].Id != layout.Id;
            status.text = source.Status;
            if (source.Online) RenderConvoyStrip();
        }

        static string Category(LapCategory c) => c == LapCategory.Clean ? "<color=#3EC6D8>clean</color>" : "<color=#F2A541>" + c.ToString().ToLowerInvariant() + "</color>";

        static string Fmt(long ms) => $"{ms / 60000}:{ms / 1000 % 60:00}.{ms % 1000:000}";

        bool laneChosen;

        void ChooseLaneIfNeeded()
        {
            if (laneChosen || source == null) return;
            PocketCircuitTable t = Table;
            if (source.Online && t.Board.Cars.Count == 0 && Time.unscaledTime - shownAt < 1.5f) return; // wait for the shared state
            laneChosen = true;
            SlotCarState mine = t.Car(source.Member);
            if (mine != null) { source.Send("throttle", new JObject { ["value"] = 0 }); return; }
            int free = Enumerable.Range(1, t.Track.Lanes.Length).FirstOrDefault(l => t.Board.Cars.All(c => c.Lane != l));
            if (free > 0) TakeLane(free);
        }

        float shownAt;

        void TakeLane(int lane) => source?.Send("lane.take", new JObject { ["lane"] = lane });

        void ProposeLayout(int index)
        {
            if (source == null) return;
            source.Send("layout.select", new JObject { ["layout"] = content.PocketCircuit.Layouts[index].Id });
        }

        /// <summary>Online: the convoy's state at a glance and one Ready control — using the table never unreadies anyone.</summary>
        void RenderConvoyStrip()
        {
            OnlineSession o = OnlineSession.Current;
            JObject c = o?.Convoy;
            if (c == null) { convoyLine.text = "Not in a convoy."; readyButton.gameObject.SetActive(false); return; }
            JToken me = o.MyMember;
            bool proposal = c["eventProposal"] is JObject;
            bool modeOpen = c["intent"] is JObject && (bool?)c["modeEntered"] != true;
            bool ready = proposal ? (bool?)me?["eventReady"] == true : (bool?)me?["modeReady"] == true;
            string phase = proposal ? "Event: " + ((string)c["eventProposal"]?["settings"]?["stageId"] ?? (string)c["eventProposal"]?["settings"]?["courseId"])
                : modeOpen ? "Mode: " + (string)c["intent"]?["label"] : (string)c["phase"];
            convoyLine.text = $"<color=#9A968D>CONVOY</color>  {phase}\n" + (proposal || modeOpen ? (ready ? "<color=#3EC6D8>You are ready.</color>" : "<color=#F2A541>Ready check waiting for you.</color>") : "Nothing to ready yet.");
            readyButton.gameObject.SetActive(proposal || modeOpen);
            readyButton.GetComponentInChildren<TextMeshProUGUI>().text = ready ? "Unready" : "Ready";
        }

        async void ToggleReady()
        {
            OnlineSession o = OnlineSession.Current;
            JObject c = o?.Convoy;
            if (c == null) return;
            JToken me = o.MyMember;
            if (c["eventProposal"] is JObject p)
            {
                if (me?["carId"]?.Type != JTokenType.String && o.StarterCarId != null)
                    await o.Request("loadout.set", new { carId = o.StarterCarId, performanceHash = "stock", cosmeticHash = "default" });
                me = o.MyMember;
                await o.Request("event.ready", new { proposalRevision = (long)p["revision"], loadoutRevision = (long?)me?["loadoutRevision"] ?? 0, ready = (bool?)me?["eventReady"] != true });
            }
            else if (c["intent"] is JObject)
                await o.Request("mode.ready", new { modeRevision = (long)c["modeRevision"], ready = (bool?)me?["modeReady"] != true });
        }

        void ToggleView()
        {
            if (view == null) return;
            view.ChaseView = !view.ChaseView;
            viewButton.GetComponentInChildren<TextMeshProUGUI>().text = view.ChaseView ? "Table View" : "Chase View";
        }
    }
}
