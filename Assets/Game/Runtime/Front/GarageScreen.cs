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
    /// Local Garage (Addendum 02 §8–10) for one car INSTANCE of the open Local profile: a planning draft edited part by part
    /// (preview parts allowed), the draft compared with the applied race build (PI estimate, class, every changed
    /// simulation input), Apply when every part is owned, Buy-and-Apply for the missing ones (a quote, then a deliberate
    /// second press; Core settles it once), loadouts (8+ slots) and the three protected references restored into the draft.
    /// Core <see cref="GarageOperations"/> / <see cref="LocalGarage"/> decide everything; the screen only shows and asks.
    /// </summary>
    public sealed class GarageScreen : UIScreen
    {
        public override string ScreenName => "Garage";
        public override string MusicCue => "MUS_MENU_B";

        const int PartRows = 9, LoadoutRows = 8;
        static readonly PartSlot[] SlotOrder =
        {
            PartSlot.Tyres, PartSlot.Suspension, PartSlot.Brakes, PartSlot.Differential, PartSlot.Gearbox, PartSlot.Engine,
            PartSlot.ForcedInduction, PartSlot.WeightReduction, PartSlot.Aero, PartSlot.BodyKit, PartSlot.Utility,
        };
        static readonly BuildReferenceKind[] ReferenceOrder = { BuildReferenceKind.BeforeWorkshop, BuildReferenceKind.BeforeLastApply, BuildReferenceKind.LastRaceBuild };

        ContentCatalogue cat;
        PartsCatalogue parts;
        Stepper carStep;
        TextMeshProUGUI carLine, walletLine, partsTitle, partInfo, compare, message;
        readonly Dictionary<PartSlot, Button> slotButtons = new Dictionary<PartSlot, Button>();
        readonly List<Button> partButtons = new List<Button>();
        readonly List<Button> loadoutButtons = new List<Button>();
        readonly Dictionary<BuildReferenceKind, Button> referenceButtons = new Dictionary<BuildReferenceKind, Button>();
        Button apply, buyApply, discard, saveLoadout, partPrev, partNext;
        List<OwnedCar> cars = new List<OwnedCar>();
        CarBuildWorkspace ws;
        long storedRevision;
        BuildContext ctx;
        PartSlot slot = PartSlot.Tyres;
        List<PartDef> slotParts = new List<PartDef>();
        int partPage, loadoutSelected = -1;
        PurchaseAndApplyQuote pendingQuote; // shown after the first Buy press; the second press settles it
        string pendingToken, pendingTokenFor;
        bool dirty = true;

        LocalSession L => LocalSession.Current;

        /// <summary>The workspace on screen (tours read it).</summary>
        public CarBuildWorkspace Workspace => ws;
        public string Message => message != null ? message.text : "";

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
                Button b = UIFactory.Button("Slot-" + PartSlots.Id(s), lcol, "", () => { slot = captured; partPage = 0; dirty = true; }, 520, 44);
                b.GetComponentInChildren<TextMeshProUGUI>().richText = true;
                b.GetComponentInChildren<TextMeshProUGUI>().fontSize = SignalTheme.Small * SignalTheme.TextScale;
                slotButtons[s] = b;
            }

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
            partPrev = UIFactory.Button("PartsPrev", mcol, "Previous", () => { partPage = Mathf.Max(0, partPage - 1); dirty = true; }, 280, 40);
            partNext = UIFactory.Button("PartsNext", mcol, "More parts", () => { partPage++; dirty = true; }, 280, 40);
            partInfo = UIFactory.Row("PartInfo", mcol, "", SignalTheme.Small, SignalTheme.LabelDim, 580, 120);
            partInfo.richText = true;

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

        public override void OnShow()
        {
            if (L?.Profile == null || cat == null || parts == null)
            {
                App.Router.Show(App.ProfileSelect, false);
                return;
            }
            cars = L.Profile.Cars.ToList();
            carStep.SetCount(Mathf.Max(1, cars.Count));
            LoadCar();
            // Entering the workshop captures "Before Workshop" (kept until the session ends).
            if (ws != null)
            {
                OperationResult r = GarageOperations.BeginWorkshopSession(ws, ctx, DateTime.UtcNow);
                if (r.Accepted) Save("");
            }
            dirty = true;
        }

        public override void OnHide()
        {
            if (ws != null && ws.Workshop.Open)
            {
                OperationResult r = GarageOperations.EndWorkshopSession(ws, ws.Revision);
                if (r.Accepted) Save("");
            }
        }

        public override Selectable DefaultFocus => slotButtons.TryGetValue(PartSlot.Tyres, out Button b) ? b : null;

        public override void Tick()
        {
            if (!dirty) return;
            dirty = false;
            Render();
        }

        OwnedCar Car => cars.Count == 0 ? null : cars[Mathf.Clamp(carStep.Index, 0, cars.Count - 1)];

        /// <summary>Plain text (the stepper label is not rich text); a second instance of the same model is numbered.</summary>
        string CarName(OwnedCar c)
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

        void LoadCar()
        {
            OwnedCar car = Car;
            pendingQuote = null;
            pendingToken = null;
            if (car == null) { ws = null; return; }
            LocalWorkspaceLoad load = LocalGarage.LoadWorkspace(L.Profile, cat, parts, car.InstanceId, DateTime.UtcNow);
            if (!load.Ok)
            {
                ws = null;
                message.text = load.Reason;
                return;
            }
            ws = load.Workspace;
            storedRevision = load.StoredRevision;
            ctx = LocalGarage.Context(L.Profile, cat, parts, car.InstanceId);
            // A catalogue or handling revision may have changed since this car was stored: re-derive and keep notices.
            OperationResult rv = GarageOperations.Revalidate(ws, ctx, DateTime.UtcNow);
            message.text = load.Notices.Count > 0 ? string.Join(" ", load.Notices) : rv.Changes.Count > 0 ? string.Join(" ", rv.Changes) : "";
        }

        MechanicalSnapshot Draft => ws?.Draft?.Build ?? ws?.Applied.Build;

        /// <summary>Stores the workspace atomically in the profile; the caller's message is shown on success.</summary>
        bool Save(string ok)
        {
            LocalProgressionResult r = LocalGarage.SaveWorkspace(L.Profile, Car.InstanceId, ws, storedRevision);
            if (r.Status == LocalOperationStatus.AlreadyApplied) { message.text = ok; return true; }
            if (!r.Changed)
            {
                message.text = "Not saved: " + r.Reason;
                LoadCar();
                return false;
            }
            if (!L.Commit(r, out string saveMessage))
            {
                message.text = "Not saved: " + saveMessage;
                LoadCar();
                return false;
            }
            storedRevision = ws.Revision;
            ctx = LocalGarage.Context(L.Profile, cat, parts, Car.InstanceId);
            message.text = ok;
            return true;
        }

        void Result(OperationResult r, string ok)
        {
            if (r.Accepted) Save(ok ?? r.Message);
            else message.text = r.Message + (r.Repairs.Count > 0 ? "  " + string.Join("; ", r.Repairs.Take(3).Select(x => x.ToString())) : "");
            pendingQuote = null;
            dirty = true;
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
            walletLine.text = $"Balance <b>{L.Profile.WalletBalance:N0} cr</b>   ·   shop act {ctx.ShopAct}";

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
            partPrev.gameObject.SetActive(pages > 1);
            partNext.gameObject.SetActive(pages > 1);
            partPrev.interactable = partPage > 0;
            partNext.interactable = partPage < pages - 1;
            PartDef shown = current != null && parts.TryPart(current, out PartDef cp) ? cp : null;
            partInfo.text = shown == null ? "Factory part: the car's stock specification." :
                $"<b>{Esc(shown.Name)}</b>  <size=85%>T{shown.Tier} · {shown.Price:N0} cr</size>\n{Esc(shown.Tradeoff ?? "")}";

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
            apply.interactable = ev.CanApply;
            buyApply.gameObject.SetActive(draftDiffers && missing.Count > 0);
            buyApply.interactable = ev.Repairs.All(r => r.Kind == RepairKind.NotOwned) && missingTotal <= L.Profile.WalletBalance;
            buyApply.GetComponentInChildren<TextMeshProUGUI>().text = pendingQuote != null
                ? $"Confirm: spend {pendingQuote.Total:N0} cr and apply"
                : missingTotal > L.Profile.WalletBalance ? $"Need {missingTotal - L.Profile.WalletBalance:N0} cr more" : $"Buy & Apply — {missingTotal:N0} cr";
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

        void ChoosePart(int row)
        {
            if (ws == null) return;
            int index = partPage * PartRows + row;
            string id = index == 0 ? null : index - 1 < slotParts.Count ? slotParts[index - 1].Id : null;
            OperationResult r = GarageOperations.EditDraft(ws, ws.Revision, Draft.With(slot, id), ctx, DateTime.UtcNow);
            Result(r, "");
        }

        /// <summary>Puts a part into the draft by id (tours; the same path as a click).</summary>
        public bool TryDraftPart(string partId)
        {
            if (ws == null || !parts.TryPart(partId, out PartDef p)) return false;
            slot = p.SlotValue;
            OperationResult r = GarageOperations.EditDraft(ws, ws.Revision, Draft.With(p.SlotValue, partId), ctx, DateTime.UtcNow);
            Result(r, "");
            return r.Accepted;
        }

        void ApplyDraft()
        {
            if (ws == null) return;
            Result(GarageOperations.Apply(ws, ws.Revision, ctx, null, DateTime.UtcNow), "Applied: this is now the car's race build.");
        }

        void BuyAndApply()
        {
            if (ws == null) return;
            if (pendingQuote == null)
            {
                // First press: a quote from Core (prices pinned); nothing is spent yet.
                QuoteResult q = LocalGarage.Quote(L.Profile, cat, parts, Car.InstanceId, Draft, DateTime.UtcNow);
                if (q.Status != QuoteStatus.Ok)
                {
                    message.text = q.Message + (q.Repairs.Count > 0 ? "  " + string.Join("; ", q.Repairs.Take(3).Select(x => x.ToString())) : "");
                    dirty = true;
                    return;
                }
                pendingQuote = q.Quote;
                message.text = $"{string.Join(", ", q.Quote.Lines.Select(l => l.Name))}: {q.Quote.Total:N0} cr. Press again to buy and apply.";
                dirty = true;
                return;
            }
            PurchaseAndApplyQuote quote = pendingQuote;
            pendingQuote = null;
            LocalProgressionResult r = LocalGarage.BuyAndApply(L.Profile, cat, parts, Car.InstanceId, quote, true, "buy-" + quote.QuoteId, DateTime.UtcNow);
            if (!r.Changed)
            {
                message.text = r.Status == LocalOperationStatus.AlreadyApplied ? "Already bought and applied." : "Not bought: " + r.Reason;
                dirty = true;
                return;
            }
            if (!L.Commit(r, out string saveMessage))
            {
                message.text = "Not saved: " + saveMessage;
                LoadCar();
                dirty = true;
                return;
            }
            LoadCar();
            message.text = $"Bought and applied — {quote.Total:N0} cr. The parts belong to this car.";
            dirty = true;
        }

        void Discard()
        {
            if (ws == null) return;
            Result(GarageOperations.DiscardDraft(ws, ws.Revision), "Draft reverted to the race build.");
        }

        void SaveLoadout()
        {
            if (ws == null) return;
            string name = "Loadout " + (ws.Loadouts.Count + 1);
            for (int n = ws.Loadouts.Count + 1; ws.Loadouts.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = "Loadout " + (n + 1);
            Result(GarageOperations.SaveAs(ws, ws.Revision, name, "", ctx, DateTime.UtcNow), $"Saved \"{name}\".");
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
            OperationResult r = GarageOperations.LoadIntoDraft(ws, ws.Revision, source, pendingTokenFor == key ? pendingToken : null, ctx, DateTime.UtcNow);
            if (r.Status == OpStatus.ConfirmationRequired)
            {
                pendingToken = r.ConfirmationToken;
                pendingTokenFor = key;
                message.text = r.Message + " Select again to replace it.";
                dirty = true;
                return;
            }
            pendingToken = pendingTokenFor = null;
            Result(r, ok);
        }
    }
}
