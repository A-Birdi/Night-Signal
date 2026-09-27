using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Garage (Addendum 02 §8–10) for one car INSTANCE — of the open Local profile, or of the signed-in ONLINE account (the
    /// control plane runs the same Core operations; see <see cref="GarageBackend"/>): a planning draft edited part by part
    /// (preview parts allowed), the draft compared with the applied race build (PI estimate, class, every changed
    /// simulation input), Apply when every part is owned, Buy-and-Apply for the missing ones (a quote, then a deliberate
    /// second press; Core settles it once), loadouts (8+ slots) and the three protected references restored into the draft.
    /// Core <see cref="GarageOperations"/> / <see cref="LocalGarage"/> decide everything; the screen only shows and asks.
    /// </summary>
    public sealed class GarageScreen : UIScreen
    {
        public override string ScreenName => "Garage";
        public override string MusicCue => "MUS_MENU_B";

        const int PartRows = 8, LoadoutRows = 8;
        static readonly PartSlot[] SlotOrder =
        {
            PartSlot.Tyres, PartSlot.Suspension, PartSlot.Brakes, PartSlot.Differential, PartSlot.Gearbox, PartSlot.Engine,
            PartSlot.ForcedInduction, PartSlot.WeightReduction, PartSlot.Aero, PartSlot.BodyKit, PartSlot.Utility,
        };
        static readonly BuildReferenceKind[] ReferenceOrder = { BuildReferenceKind.BeforeWorkshop, BuildReferenceKind.BeforeLastApply, BuildReferenceKind.LastRaceBuild };

        ContentCatalogue cat;
        PartsCatalogue parts;
        Stepper carStep;
        TextMeshProUGUI carLine, walletLine, partsTitle, partInfo, compare, message, yardLine;
        TMP_InputField yardNotes;
        Button preferA, preferB;
        readonly Dictionary<PartSlot, Button> slotButtons = new Dictionary<PartSlot, Button>();
        readonly List<Button> partButtons = new List<Button>();
        // Tuning page: one line per control the installed parts expose (label, −, +).
        readonly List<(GameObject Root, TextMeshProUGUI Label, Button Minus, Button Plus)> tuneRows = new List<(GameObject, TextMeshProUGUI, Button, Button)>();
        Button tuneSlot, tuneDefaults, tuneNormalize, appearance;
        bool keepWorkshop; // the Appearance screen is part of this Garage visit
        bool tuning;
        List<TuningControlInfo> controls = new List<TuningControlInfo>();
        readonly List<Button> loadoutButtons = new List<Button>();
        readonly Dictionary<BuildReferenceKind, Button> referenceButtons = new Dictionary<BuildReferenceKind, Button>();
        Button apply, buyApply, discard, saveLoadout, partPrev, partNext;
        List<GarageCarRef> cars = new List<GarageCarRef>();
        GarageBackend backend;
        bool busy, buildLocked;
        CarBuildWorkspace ws;
        BuildContext ctx;
        PartSlot slot = PartSlot.Tyres;
        List<PartDef> slotParts = new List<PartDef>();
        int partPage, loadoutSelected = -1;
        GarageQuote pendingQuote; // shown after the first Buy press; the second press settles it
        string pendingToken, pendingTokenFor;
        bool dirty = true;

        LocalSession L => LocalSession.Current;

        /// <summary>The workspace on screen (tours read it).</summary>
        public CarBuildWorkspace Workspace => ws;
        public string Message => message != null ? message.text : "";
        /// <summary>True while the (online) garage is answering; clicks are ignored meanwhile.</summary>
        public bool Busy => busy;
        public bool IsOnline => backend != null && backend.Online;

        protected override void OnBuild(RectTransform root)
        {
            ContentLibrary lib = ContentLibrary.Load();
            cat = lib?.Catalogue;
            parts = lib?.Parts;
            UIFactory.Panel("Backdrop", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.035f, 0.04f, 0.05f, 0.92f));

            // Left: the car and its slots (draft).
            Image left = UIFactory.Panel("Car", root, new Vector2(0, 0), new Vector2(0.3f, 1), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.95f));
            RectTransform lcol = UIFactory.Column("CarColumn", left.transform, new Vector2(0, 0.02f), new Vector2(1, 0.95f), new Vector2(40, 0), new Vector2(-20, 0), 6f);
            UIFactory.Row("Heading", lcol, "GARAGE", SignalTheme.Heading, SignalTheme.Label, 520, 0, true);
            carStep = new Stepper(lcol, "Car", 1, i => i < cars.Count ? CarName(cars[i]) : "—", 0, 520, 0.18f);
            carStep.Changed += _ => { LoadCar(); dirty = true; };
            carLine = UIFactory.Row("CarLine", lcol, "", SignalTheme.Small, SignalTheme.Label, 520, 54);
            carLine.richText = true;
            walletLine = UIFactory.Row("Wallet", lcol, "", SignalTheme.Small, SignalTheme.Label, 520, 26);
            walletLine.richText = true;
            foreach (PartSlot s in SlotOrder)
            {
                PartSlot captured = s;
                Button b = UIFactory.Button("Slot-" + PartSlots.Id(s), lcol, "", () => { slot = captured; tuning = false; partPage = 0; dirty = true; }, 520, 44);
                b.GetComponentInChildren<TextMeshProUGUI>().richText = true;
                b.GetComponentInChildren<TextMeshProUGUI>().fontSize = SignalTheme.Small * SignalTheme.TextScale;
                slotButtons[s] = b;
            }
            tuneSlot = UIFactory.Button("Slot-tuning", lcol, "", () => { tuning = true; dirty = true; }, 520, 44);
            tuneSlot.GetComponentInChildren<TextMeshProUGUI>().richText = true;
            tuneSlot.GetComponentInChildren<TextMeshProUGUI>().fontSize = SignalTheme.Small * SignalTheme.TextScale;
            appearance = UIFactory.Button("OpenAppearance", lcol, "Appearance  (body kit, wheels, paint, decals)", OpenAppearance, 520, 44);
            appearance.GetComponentInChildren<TextMeshProUGUI>().fontSize = SignalTheme.Small * SignalTheme.TextScale;

            // Middle: parts for the chosen slot.
            RectTransform mcol = UIFactory.Column("Parts", root, new Vector2(0.31f, 0.02f), new Vector2(0.62f, 0.95f), Vector2.zero, Vector2.zero, 6f);
            partsTitle = UIFactory.Row("PartsTitle", mcol, "", SignalTheme.Body, SignalTheme.Label, 580, 40);
            partsTitle.richText = true;
            for (int i = 0; i < PartRows; i++)
            {
                int index = i;
                Button b = UIFactory.Button("Part" + i, mcol, "", () => ChoosePart(index), 580, 50);
                b.GetComponentInChildren<TextMeshProUGUI>().richText = true;
                b.GetComponentInChildren<TextMeshProUGUI>().fontSize = SignalTheme.Small * SignalTheme.TextScale;
                partButtons.Add(b);
            }
            for (int i = 0; i < PartRows; i++)
            {
                int index = i;
                RectTransform row = UIFactory.Rect("Tune" + i, mcol, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
                row.sizeDelta = new Vector2(580, 50);
                TextMeshProUGUI label = UIFactory.Label("Label", row, "", SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.MidlineLeft);
                label.rectTransform.anchorMin = Vector2.zero;
                label.rectTransform.anchorMax = Vector2.one;
                label.rectTransform.offsetMin = new Vector2(12, 0);
                label.rectTransform.offsetMax = new Vector2(-136, 0);
                label.richText = true;
                Button minus = UIFactory.Button("TuneMinus" + i, row, "−", () => Nudge(index, -1), 60, 46);
                Button plus = UIFactory.Button("TunePlus" + i, row, "+", () => Nudge(index, +1), 60, 46);
                Place(minus, 580 - 128);
                Place(plus, 580 - 62);
                row.gameObject.SetActive(false);
                tuneRows.Add((row.gameObject, label, minus, plus));
            }
            tuneDefaults = UIFactory.Button("TuneDefaults", mcol, "Reset Tune to the Parts' Defaults", ResetTune, 580, 40);
            tuneNormalize = UIFactory.Button("TuneNormalize", mcol, "Fit the Tune to These Parts", NormalizeTune, 580, 40);
            partPrev = UIFactory.Button("PartsPrev", mcol, "Previous", () => { partPage = Mathf.Max(0, partPage - 1); dirty = true; }, 280, 40);
            partNext = UIFactory.Button("PartsNext", mcol, "More parts", () => { partPage++; dirty = true; }, 280, 40);
            partInfo = UIFactory.Row("PartInfo", mcol, "", SignalTheme.Small, SignalTheme.LabelDim, 580, 96);
            partInfo.richText = true;

            // Test Yard (Addendum 02 §10): drive A (the race build) or B (this draft) before committing.
            (Button yardA, Button yardB) = Pair("TestYard", mcol, 580, 46);
            Bind(yardA, "Yard: A (race build)", () => OpenYard(false));
            Bind(yardB, "Yard: B (draft)", () => OpenYard(true));
            yardLine = UIFactory.Row("YardRuns", mcol, "", SignalTheme.Small, SignalTheme.Label, 580, 90);
            yardLine.richText = true;
            yardNotes = UIFactory.InputField("YardNotes", mcol, "Notes on how A and B felt (kept on this device)", false, 200, 580, 46);
            yardNotes.onEndEdit.AddListener(_ => SaveYardNotes());
            (preferA, preferB) = Pair("Prefer", mcol, 580, 40);
            Bind(preferA, "Prefer A", () => SetPreference("A"));
            Bind(preferB, "Prefer B", () => SetPreference("B"));

            // Right: draft vs applied, actions, loadouts and references.
            RectTransform rcol = UIFactory.Column("Draft", root, new Vector2(0.64f, 0.02f), new Vector2(0.99f, 0.95f), Vector2.zero, Vector2.zero, 6f);
            compare = UIFactory.Row("Compare", rcol, "", SignalTheme.Small, SignalTheme.Label, 660, 200);
            compare.richText = true;
            apply = UIFactory.Button("ApplyDraft", rcol, "Apply", ApplyDraft, 660, 50);
            buyApply = UIFactory.Button("BuyAndApply", rcol, "Buy & Apply", BuyAndApply, 660, 50);
            discard = UIFactory.Button("DiscardDraft", rcol, "Revert Draft to Applied", Discard, 660, 42);
            saveLoadout = UIFactory.Button("SaveLoadout", rcol, "Save Draft as Loadout", SaveLoadout, 660, 42);
            message = UIFactory.Row("Message", rcol, "", SignalTheme.Small, SignalTheme.Caution, 660, 40);
            UIFactory.Row("LoadoutsTitle", rcol, "LOADOUTS  (select to load into the draft)", SignalTheme.Small, SignalTheme.LabelDim, 660, 24);
            for (int i = 0; i < LoadoutRows; i++)
            {
                int index = i;
                Button b = UIFactory.Button("Loadout" + i, rcol, "", () => LoadLoadout(index), 660, 34);
                b.GetComponentInChildren<TextMeshProUGUI>().fontSize = SignalTheme.Small * SignalTheme.TextScale;
                b.GetComponentInChildren<TextMeshProUGUI>().richText = true;
                loadoutButtons.Add(b);
            }
            UIFactory.Row("ReferencesTitle", rcol, "PROTECTED  (restore into the draft)", SignalTheme.Small, SignalTheme.LabelDim, 660, 24);
            foreach (BuildReferenceKind k in ReferenceOrder)
            {
                BuildReferenceKind captured = k;
                Button b = UIFactory.Button("Ref-" + BuildReferenceKinds.Id(k), rcol, "", () => LoadReference(captured), 660, 34);
                b.GetComponentInChildren<TextMeshProUGUI>().fontSize = SignalTheme.Small * SignalTheme.TextScale;
                referenceButtons[k] = b;
            }
            UIFactory.Button("Back", rcol, "Back", () => App.Router.Back(), 300, 42);
        }

        /// <summary>Two half-width buttons on one line.</summary>
        static (Button, Button) Pair(string name, Transform parent, float width, float height)
        {
            RectTransform row = UIFactory.Rect(name + "Row", parent, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            row.sizeDelta = new Vector2(width, height);
            float half = (width - 8f) * 0.5f;
            Button a = UIFactory.Button(name + "A", row, "", null, half, height);
            Button b = UIFactory.Button(name + "B", row, "", null, half, height);
            Place(a, 0f);
            Place(b, half + 8f);
            return (a, b);
        }

        static void Place(Button btn, float x)
        {
            var r = (RectTransform)btn.transform;
            r.anchorMin = r.anchorMax = new Vector2(0, 0.5f);
            r.pivot = new Vector2(0, 0.5f);
            r.anchoredPosition = new Vector2(x, 0);
            btn.GetComponentInChildren<TextMeshProUGUI>().fontSize = SignalTheme.Small * SignalTheme.TextScale;
        }

        static void Bind(Button b, string label, Action action)
        {
            b.GetComponentInChildren<TextMeshProUGUI>().text = label;
            b.onClick.AddListener(() => action());
        }

        public override void OnShow()
        {
            bool online = App.Domain == SessionDomain.Online && OnlineSession.Current != null;
            if (cat == null || parts == null || (!online && L?.Profile == null))
            {
                App.Router.Show(online ? (UIScreen)App.Convoy : App.ProfileSelect, false);
                return;
            }
            if (backend == null || backend.Online != online)
                backend = online ? (GarageBackend)new OnlineGarageBackend(cat, parts) : new LocalGarageBackend(cat, parts);
            busy = true;
            backend.Cars((list, error) =>
            {
                busy = false;
                cars = list;
                carStep.SetCount(Mathf.Max(1, cars.Count));
                if (cars.Count == 0)
                {
                    ws = null;
                    message.text = error.Length > 0 ? error : "No cars in this garage yet.";
                    dirty = true;
                    return;
                }
                // Entering the workshop captures "Before Workshop" (kept until the session ends; re-entering keeps it).
                LoadCar(beginWorkshop: true);
            });
            dirty = true;
        }

        public override void OnHide()
        {
            if (keepWorkshop)
            {
                keepWorkshop = false;
                return;
            }
            if (ws != null && ws.Workshop.Open && backend != null && Car != null)
                backend.Run(Car.InstanceId, ws, new GarageOp { Kind = "end-workshop" }, _ => { });
        }

        public override Selectable DefaultFocus => slotButtons.TryGetValue(PartSlot.Tyres, out Button b) ? b : null;

        public override void Tick()
        {
            if (!dirty) return;
            dirty = false;
            Render();
        }

        GarageCarRef Car => cars.Count == 0 ? null : cars[Mathf.Clamp(carStep.Index, 0, cars.Count - 1)];

        /// <summary>Plain text (the stepper label is not rich text); a second instance of the same model is numbered.</summary>
        string CarName(GarageCarRef c)
        {
            string name = cat != null && cat.TryCar(c.ModelId, out CarDef d) ? d.Name : c.ModelId;
            int same = cars.Count(x => x.ModelId == c.ModelId);
            return same > 1 ? $"{name}  #{cars.Where(x => x.ModelId == c.ModelId).ToList().IndexOf(c) + 1}" : name;
        }

        /// <summary>Selects a car instance by model (tours).</summary>
        public void SelectModel(string modelId)
        {
            int i = cars.FindIndex(c => c.ModelId == modelId);
            if (i < 0) return;
            carStep.Set(i);
            LoadCar();
            dirty = true;
        }

        void LoadCar(bool beginWorkshop = false)
        {
            GarageCarRef car = Car;
            pendingQuote = null;
            pendingToken = null;
            if (car == null) { ws = null; return; }
            busy = true;
            backend.Load(car.InstanceId, (state, error) =>
            {
                busy = false;
                if (state == null)
                {
                    ws = null;
                    message.text = error;
                    dirty = true;
                    return;
                }
                Adopt(state);
                message.text = state.Notice;
                LoadYardNotes();
                if (beginWorkshop && !ws.Workshop.Open) Do(new GarageOp { Kind = "begin-workshop" }, state.Notice);
                dirty = true;
            });
        }

        void Adopt(GarageState state)
        {
            ws = state.Workspace;
            ctx = state.Context;
            buildLocked = state.BuildLocked;
        }

        MechanicalSnapshot Draft => ws?.Draft?.Build ?? ws?.Applied.Build;

        /// <summary>
        /// Runs one garage operation where this garage's decisions happen (Local: Core in-process, saved atomically; Online:
        /// the control plane) and shows the car it answers with. Any change voids a pending quote.
        /// </summary>
        void Do(GarageOp op, string ok, Action<GarageAnswer> after = null)
        {
            if (ws == null || busy || Car == null) return;
            busy = true;
            backend.Run(Car.InstanceId, ws, op, a =>
            {
                busy = false;
                if (a.State != null) Adopt(a.State);
                else if (!a.Accepted && !backend.Online) LoadCar(); // a Local save failed: show what is stored
                message.text = a.Accepted ? (ok ?? a.Message) : a.Message + (a.Repairs.Count > 0 ? "  " + string.Join("; ", a.Repairs.Take(3)) : "");
                pendingQuote = null;
                after?.Invoke(a);
                dirty = true;
            });
        }

        // ------------------------------------------------------------------ render

        void Render()
        {
            if (ws == null || cat == null) return;
            CarDef model = cat.Car(ws.Car.ModelId);
            MechanicalSnapshot draft = Draft;
            BuildEvaluation applied = BuildEvaluator.Evaluate(ws.Applied.Build, ws.Car.InstanceId, ctx);
            BuildEvaluation ev = BuildEvaluator.Evaluate(draft, ws.Car.InstanceId, ctx);
            carLine.text = $"<b>{Esc(model.Name)}</b>  <size=80%>{Esc(model.Maker)} · {model.Drive} · stock PI {model.BasePI}</size>\n" +
                           $"Race build: <b>PI {ws.Applied.Pi} {Esc(ws.Applied.PiClass)}</b>  <size=80%>(estimate)</size>";
            walletLine.text = $"Balance <b>{backend.Balance:N0} cr</b>   ·   shop act {ctx.ShopAct}   ·   {(backend.Online ? "online" : "Local")}" +
                              (buildLocked ? "   ·   <color=#F2A541>locked for the event</color>" : "");

            foreach (PartSlot s in SlotOrder)
            {
                string inDraft = s == PartSlot.Utility ? draft.UtilityPartId : draft.PartIn(s);
                string inApplied = s == PartSlot.Utility ? ws.Applied.Build.UtilityPartId : ws.Applied.Build.PartIn(s);
                bool changed = !string.Equals(inDraft ?? "", inApplied ?? "", StringComparison.Ordinal);
                bool preview = inDraft != null && !ctx.Ownership.Owns(ws.Car.InstanceId, inDraft);
                // A preview (not owned) part shows in amber; the draft panel lists what it would cost.
                string label = inDraft == null ? "<color=#9A968D>stock</color>" : preview ? $"<color=#F2A541>{Esc(PartName(inDraft))}</color>" : Esc(PartName(inDraft));
                string mark = s == slot ? "<color=#E5484D>›</color> " : "";
                slotButtons[s].GetComponentInChildren<TextMeshProUGUI>().text =
                    $"{mark}<size=80%>{SlotLabel(s).ToUpperInvariant()}</size>  {label}{(changed ? "  <color=#3EC6D8>●</color>" : "")}";
            }

            controls = TuningModel.Controls(draft.Parts.Values.Select(id => parts.TryPart(id, out PartDef pd) ? pd : null).Where(pd => pd != null && pd.SlotValue != PartSlot.Utility), ctx.Stock.Get);
            int changedTunes = controls.Count(c => TuningModel.ValueOrDefault(draft.Tuning, c) != c.Default);
            tuneSlot.GetComponentInChildren<TextMeshProUGUI>().text = (tuning ? "<color=#E5484D>›</color> " : "") +
                $"<size=80%>TUNING</size>  {(controls.Count == 0 ? "<color=#9A968D>no adjustable parts</color>" : $"{controls.Count} control{(controls.Count == 1 ? "" : "s")}{(changedTunes > 0 ? $", {changedTunes} changed" : "")}")}";
            if (tuning)
            {
                RenderTuning(draft, ev);
                RenderCompare(ws, draft, applied, ev);
                return;
            }
            foreach (var t in tuneRows) t.Root.SetActive(false);
            tuneDefaults.gameObject.SetActive(false);
            tuneNormalize.gameObject.SetActive(false);

            // Parts for the chosen slot: stock first, then by tier and price.
            slotParts = parts.CompatibleParts(model, cat.CarTunings[model.Id], slot)
                .Where(p => !p.Retired || p.Id == (slot == PartSlot.Utility ? draft.UtilityPartId : draft.PartIn(slot)))
                .OrderBy(p => p.Tier).ThenBy(p => p.Price).ToList();
            int total = slotParts.Count + 1;
            int pages = Mathf.Max(1, (total + PartRows - 1) / PartRows);
            partPage = Mathf.Clamp(partPage, 0, pages - 1);
            partsTitle.text = $"<b>{SlotLabel(slot)}</b>  <size=80%>{slotParts.Count} compatible part{(slotParts.Count == 1 ? "" : "s")}</size>";
            string current = slot == PartSlot.Utility ? draft.UtilityPartId : draft.PartIn(slot);
            for (int i = 0; i < partButtons.Count; i++)
            {
                int index = partPage * PartRows + i;
                bool on = index < total;
                partButtons[i].gameObject.SetActive(on);
                if (!on) continue;
                PartDef p = index == 0 ? null : slotParts[index - 1];
                string id = p?.Id;
                string mark = string.Equals(id ?? "", current ?? "", StringComparison.Ordinal) ? "<color=#E5484D>›</color> " : "";
                string state;
                if (p == null) state = "<color=#9A968D>factory part</color>";
                else if (ctx.Ownership.Owns(ws.Car.InstanceId, p.Id)) state = "<color=#3EC6D8>owned</color>";
                else if (p.UnlockAct > ctx.ShopAct) state = $"<color=#6F6C66>shop act {p.UnlockAct}</color>";
                else state = $"{p.Price:N0} cr";
                partButtons[i].GetComponentInChildren<TextMeshProUGUI>().text = p == null
                    ? $"{mark}Stock  <size=85%>{state}</size>"
                    : $"{mark}<size=80%>T{p.Tier}</size>  {Esc(p.Name)}   <size=85%>{state}</size>";
            }
            RenderYard();
            partPrev.gameObject.SetActive(pages > 1);
            partNext.gameObject.SetActive(pages > 1);
            partPrev.interactable = partPage > 0;
            partNext.interactable = partPage < pages - 1;
            PartDef shown = current != null && parts.TryPart(current, out PartDef cp) ? cp : null;
            partInfo.text = shown == null ? "Factory part: the car's stock specification." :
                $"<b>{Esc(shown.Name)}</b>  <size=85%>T{shown.Tier} · {shown.Price:N0} cr</size>\n{Esc(shown.Tradeoff ?? "")}";

            RenderCompare(ws, draft, applied, ev);
        }

        void RenderTuning(MechanicalSnapshot draft, BuildEvaluation ev)
        {
            RenderYard();
            foreach (Button b in partButtons) b.gameObject.SetActive(false);
            partPrev.gameObject.SetActive(false);
            partNext.gameObject.SetActive(false);
            partsTitle.text = "<b>Tuning</b>  <size=80%>bounded by the installed parts; every step changes a simulation input</size>";
            for (int i = 0; i < tuneRows.Count; i++)
            {
                bool on = i < controls.Count;
                tuneRows[i].Root.SetActive(on);
                if (!on) continue;
                TuningControlInfo c = controls[i];
                int v = TuningModel.ValueOrDefault(draft.Tuning, c);
                string partName = parts.TryPart(c.PartId, out PartDef pd) ? pd.Name : c.PartId;
                tuneRows[i].Label.text = $"<b>{Esc(c.Key)}</b>  {v} <size=80%>{Esc(c.Unit)}</size>{(v != c.Default ? "  <color=#3EC6D8>●</color>" : "")}\n" +
                                         $"<size=75%><color=#9A968D>{c.Min}–{c.Max}, step {c.Step}, default {c.Default} · {Esc(partName)}</color></size>";
                tuneRows[i].Minus.interactable = v > c.Min;
                tuneRows[i].Plus.interactable = v < c.Max;
            }
            tuneDefaults.gameObject.SetActive(controls.Count > 0);
            tuneNormalize.gameObject.SetActive(ev.Repairs.Any(r => r.Kind == RepairKind.TuningInvalid));
            partInfo.text = controls.Count == 0
                ? "Adjustable parts (gearbox, differential, suspension, brakes, aero) add tuning controls here."
                : "Change one value at a time and feel it in the Test Yard before applying.";
        }

        void RenderCompare(CarBuildWorkspace ws, MechanicalSnapshot draft, BuildEvaluation applied, BuildEvaluation ev)
        {
            // Draft vs applied.
            BuildComparison c = BuildComparison.Of(ws.Applied.Build, draft, ws.Car.InstanceId, ctx);
            var sb = new System.Text.StringBuilder();
            string piTo = ev.Resolved ? $"{ev.Pi.Value} {ev.Pi.Class}" : "?";
            string piFrom = applied.Resolved ? $"{applied.Pi.Value} {applied.Pi.Class}" : "?";
            sb.Append(c.Identical ? $"<b>Draft = race build</b>   PI {piFrom}\n" : $"<b>Draft vs race build</b>   PI {piFrom} → <b>{piTo}</b>{(c.ClassChanged ? "  <color=#F2A541>class change</color>" : "")}\n");
            if (ev.Resolved && applied.Resolved)
            {
                DerivedFigures a = applied.Pi.Figures, b = ev.Pi.Figures;
                sb.Append($"<size=85%>Top speed {a.ReachableTopSpeedKmh:0} → {b.ReachableTopSpeedKmh:0} km/h   ·   power/weight {a.PowerToWeightKwPerTonne:0} → {b.PowerToWeightKwPerTonne:0} kW/t\n");
                sb.Append($"Cornering grip {a.CorneringGrip:0.00} → {b.CorneringGrip:0.00}   ·   braking {a.BrakingDecelG:0.00} → {b.BrakingDecelG:0.00} g</size>\n");
            }
            foreach (string line in c.Lines.Where(l => !l.StartsWith("PI ")).Take(6)) sb.Append($"<size=80%>{Esc(line)}</size>\n");
            List<RepairItem> missing = ev.MissingParts.ToList();
            long missingTotal = missing.Sum(m => m.Price);
            if (missing.Count > 0) sb.Append($"<color=#F2A541>To buy: {string.Join(", ", missing.Select(m => Esc(m.PartName)))} — {missingTotal:N0} cr</color>\n");
            foreach (RepairItem r in ev.Repairs.Where(r => r.Kind != RepairKind.NotOwned).Take(3)) sb.Append($"<color=#E5484D>{Esc(r.ToString())}</color>\n");
            compare.text = sb.ToString();

            bool draftDiffers = !c.Identical;
            apply.gameObject.SetActive(draftDiffers && missing.Count == 0);
            apply.interactable = ev.CanApply && !busy && !buildLocked;
            buyApply.gameObject.SetActive(draftDiffers && missing.Count > 0);
            buyApply.interactable = !busy && !buildLocked && ev.Repairs.All(r => r.Kind == RepairKind.NotOwned) && missingTotal <= backend.Balance;
            buyApply.GetComponentInChildren<TextMeshProUGUI>().text = pendingQuote != null
                ? $"Confirm: spend {pendingQuote.Total:N0} cr and apply"
                : missingTotal > backend.Balance ? $"Need {missingTotal - backend.Balance:N0} cr more" : $"Buy & Apply — {missingTotal:N0} cr";
            discard.gameObject.SetActive(ws.Draft != null && draftDiffers);
            saveLoadout.interactable = ws.Loadouts.Count < ws.LoadoutCapacity;
            saveLoadout.GetComponentInChildren<TextMeshProUGUI>().text = $"Save Draft as Loadout   ({ws.Loadouts.Count}/{ws.LoadoutCapacity})";

            for (int i = 0; i < loadoutButtons.Count; i++)
            {
                MechanicalLoadout l = i < ws.Loadouts.Count ? ws.Loadouts[i] : null;
                loadoutButtons[i].interactable = l != null;
                string mark = i == loadoutSelected ? "<color=#E5484D>›</color> " : "";
                loadoutButtons[i].GetComponentInChildren<TextMeshProUGUI>().text = l == null
                    ? $"<color=#6F6C66>{i + 1}. empty slot</color>"
                    : $"{mark}{i + 1}. {Esc(l.Name)}   <size=85%>PI {l.DerivedPi} {Esc(l.DerivedClass)}{(l.NeedsParts ? "  <color=#F2A541>needs parts</color>" : "")}</size>";
            }
            foreach (BuildReferenceKind k in ReferenceOrder)
            {
                BuildReference r = ws.Reference(k);
                referenceButtons[k].interactable = r != null;
                referenceButtons[k].GetComponentInChildren<TextMeshProUGUI>().text = r == null
                    ? $"<color=#6F6C66>{ReferenceLabel(k)} — none yet</color>"
                    : $"{ReferenceLabel(k)}   <size=85%>PI {r.Pi} · {r.CapturedUtc.ToLocalTime():MMM d HH:mm}</size>";
                referenceButtons[k].GetComponentInChildren<TextMeshProUGUI>().richText = true;
            }
        }

        string PartName(string id) => parts.TryPart(id, out PartDef p) ? p.Name : id;

        static string SlotLabel(PartSlot s)
        {
            switch (s)
            {
                case PartSlot.ForcedInduction: return "Forced induction";
                case PartSlot.WeightReduction: return "Weight reduction";
                case PartSlot.BodyKit: return "Body kit";
                default: return s.ToString();
            }
        }

        static string ReferenceLabel(BuildReferenceKind k) =>
            k == BuildReferenceKind.BeforeWorkshop ? "Before this visit" : k == BuildReferenceKind.BeforeLastApply ? "Before last apply" : "Last race build";

        static string Esc(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");

        // ------------------------------------------------------------------ actions

        void Nudge(int row, int direction)
        {
            if (ws == null || row >= controls.Count) return;
            TuningControlInfo c = controls[row];
            int v = Mathf.Clamp(TuningModel.ValueOrDefault(Draft.Tuning, c) + direction * c.Step, c.Min, c.Max);
            Do(new GarageOp { Kind = "edit-draft", Build = Draft.WithTune(c.Key, v) }, "");
        }

        void ResetTune()
        {
            if (ws == null) return;
            MechanicalSnapshot d = Draft.Clone();
            d.Tuning = new TuningSetup();
            Do(new GarageOp { Kind = "edit-draft", Build = d }, "Tune reset to the parts' defaults.");
        }

        /// <summary>Explicit action after a part swap: drop values the parts cannot adjust, snap the rest (listed).</summary>
        void NormalizeTune()
        {
            if (ws == null) return;
            var changes = new List<string>();
            MechanicalSnapshot d = Draft.Clone();
            d.Tuning = TuningModel.Normalize(Draft.Tuning, controls, changes);
            Do(new GarageOp { Kind = "edit-draft", Build = d }, changes.Count > 0 ? string.Join(" ", changes) : "The tune already fits these parts.");
        }

        void ChoosePart(int row)
        {
            if (ws == null) return;
            int index = partPage * PartRows + row;
            string id = index == 0 ? null : index - 1 < slotParts.Count ? slotParts[index - 1].Id : null;
            Do(new GarageOp { Kind = "edit-draft", Build = Draft.With(slot, id) }, "");
        }

        /// <summary>Puts a part into the draft by id (tours; the same path as a click).</summary>
        public bool TryDraftPart(string partId)
        {
            if (ws == null || !parts.TryPart(partId, out PartDef p)) return false;
            slot = p.SlotValue;
            bool accepted = false;
            Do(new GarageOp { Kind = "edit-draft", Build = Draft.With(p.SlotValue, partId) }, "", a => accepted = a.Accepted);
            return accepted; // Local answers at once; online the answer arrives later (see Busy)
        }

        // ------------------------------------------------------------------ Test Yard

        /// <summary>A frozen A/B side: resolved physics of a snapshot (preview parts allowed — driving never buys).</summary>
        Race.TestYardBuild YardBuild(MechanicalSnapshot snap, string label)
        {
            CarDef model = cat.Car(ws.Car.ModelId);
            ResolveResult r = BuildResolver.Resolve(model, cat.CarTunings[model.Id], parts, snap);
            if (!r.Ok) return null;
            BuildEvaluation ev = BuildEvaluator.Evaluate(snap, ws.Car.InstanceId, ctx);
            return new Race.TestYardBuild
            {
                Label = label, BuildHash = r.Spec.BuildHash, Pi = ev.Resolved ? ev.Pi.Value : model.BasePI,
                Params = Vehicle.VehicleFactory.Build(r.Spec, Vehicle.AssistSettings.Default, ContentLibrary.Load().Body(model.Id).WheelRadius),
            };
        }

        void OpenYard(bool driveB)
        {
            if (ws == null) return;
            bool preview = Draft.AllPartIds().Any(id => !ctx.Ownership.Owns(ws.Car.InstanceId, id));
            string candidate = ws.Draft != null && ws.DraftIsDirty
                ? "Draft candidate" + (preview ? " (includes parts you do not own yet)" : "")
                : "Draft (same as A: change a part first)";
            Race.TestYardBuild a = YardBuild(ws.Applied.Build, "Current race build");
            Race.TestYardBuild b = YardBuild(Draft, candidate);
            if (a == null || b == null)
            {
                message.text = "This build cannot be driven: resolve its repairs first.";
                dirty = true;
                return;
            }
            App.StartTestYard(ws.Car.ModelId, a, b, driveB, this);
        }

        void RenderYard()
        {
            List<Race.TestYardRun> runs = App.LastYardRuns;
            if (runs.Count == 0)
            {
                yardLine.text = "<color=#9A968D>Drive A and B on the service campus: launch & braking straight, skid pad, handling loop, dry or wet.</color>";
                return;
            }
            var sb = new System.Text.StringBuilder("<b>Last visit</b>\n");
            Race.TestYardRun lastA = runs.LastOrDefault(r => !r.B), lastB = runs.LastOrDefault(r => r.B);
            foreach (Race.TestYardRun r in new[] { lastA, lastB })
                if (r != null) sb.Append($"<size=85%>{(r.B ? "B" : "A")} · {r.Station} · {r.Surface}: {Esc(r.Summary())}</size>\n");
            yardLine.text = sb.ToString();
        }

        string NotesKey => ws == null || backend == null ? null : "ns.yard." + backend.OwnerKey + "." + ws.Car.InstanceId;

        void LoadYardNotes()
        {
            if (NotesKey == null) return;
            yardNotes.SetTextWithoutNotify(PlayerPrefs.GetString(NotesKey + ".notes", ""));
            MarkPreference(PlayerPrefs.GetString(NotesKey + ".prefer", ""));
        }

        void SaveYardNotes()
        {
            if (NotesKey == null) return;
            PlayerPrefs.SetString(NotesKey + ".notes", yardNotes.text ?? "");
            PlayerPrefs.Save();
        }

        void SetPreference(string side)
        {
            if (NotesKey == null) return;
            string now = PlayerPrefs.GetString(NotesKey + ".prefer", "") == side ? "" : side; // pressing again clears it
            PlayerPrefs.SetString(NotesKey + ".prefer", now);
            PlayerPrefs.Save();
            MarkPreference(now);
        }

        void MarkPreference(string side)
        {
            preferA.GetComponentInChildren<TextMeshProUGUI>().text = side == "A" ? "Prefer A (marked)" : "Prefer A";
            preferB.GetComponentInChildren<TextMeshProUGUI>().text = side == "B" ? "Prefer B (marked)" : "Prefer B";
        }

        /// <summary>Appearance of this car instance: its own screen and preview; answers come back into this workspace.</summary>
        void OpenAppearance()
        {
            if (ws == null || busy || Car == null || ContentLibrary.Load()?.Customization == null) return;
            keepWorkshop = true;
            App.Appearance.Open(backend, Car.InstanceId, CarName(Car), ws, state => { Adopt(state); dirty = true; });
            App.Router.Show(App.Appearance);
        }

        void ApplyDraft()
        {
            if (ws == null) return;
            Do(new GarageOp { Kind = "apply" }, "Applied: this is now the car's race build.");
        }

        void BuyAndApply()
        {
            if (ws == null || busy) return;
            if (pendingQuote == null)
            {
                // First press: a quote (prices pinned by Core, server-side online); nothing is spent yet.
                busy = true;
                backend.Quote(Car.InstanceId, ws, Draft, q =>
                {
                    busy = false;
                    if (!q.Ok) message.text = q.Message;
                    else
                    {
                        pendingQuote = q;
                        message.text = $"{string.Join(", ", q.Lines)}: {q.Total:N0} cr. Press again to buy and apply.";
                    }
                    dirty = true;
                });
                return;
            }
            GarageQuote quote = pendingQuote;
            pendingQuote = null;
            busy = true;
            backend.Settle(Car.InstanceId, quote, a =>
            {
                busy = false;
                if (a.State != null) Adopt(a.State);
                message.text = a.Message;
                dirty = true;
            });
        }

        void Discard()
        {
            if (ws == null) return;
            Do(new GarageOp { Kind = "discard-draft" }, "Draft reverted to the race build.");
        }

        void SaveLoadout()
        {
            if (ws == null) return;
            string name = "Loadout " + (ws.Loadouts.Count + 1);
            for (int n = ws.Loadouts.Count + 1; ws.Loadouts.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = "Loadout " + (n + 1);
            Do(new GarageOp { Kind = "save-as", Name = name, Note = "" }, $"Saved \"{name}\".");
        }

        void LoadLoadout(int index)
        {
            if (ws == null || index >= ws.Loadouts.Count) return;
            loadoutSelected = index;
            LoadInto(DraftSource.Loadout(ws.Loadouts[index].LoadoutId), $"Loaded \"{ws.Loadouts[index].Name}\" into the draft.");
        }

        void LoadReference(BuildReferenceKind k) => LoadInto(DraftSource.Reference(k), $"Restored \"{ReferenceLabel(k)}\" into the draft — Apply to race it.");

        /// <summary>Loads into the draft; replacing an unsaved draft asks for a second press (Core's confirmation token).</summary>
        void LoadInto(DraftSource source, string ok)
        {
            string key = source.Kind + ":" + source.Id;
            Do(new GarageOp { Kind = "load-into-draft", Source = source, ConfirmationToken = pendingTokenFor == key ? pendingToken : null }, ok, a =>
            {
                if (a.ConfirmationRequired)
                {
                    pendingToken = a.ConfirmationToken;
                    pendingTokenFor = key;
                    message.text = a.Message + " Select again to replace it.";
                }
                else pendingToken = pendingTokenFor = null;
            });
        }
    }
}
