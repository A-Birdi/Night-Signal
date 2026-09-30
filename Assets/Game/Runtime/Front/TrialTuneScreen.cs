using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// A tunable loaner's workshop (slice 5: CH46, CH56, CH57, CH59): the trial's free part choices by slot, the tuning
    /// controls of the installed parts, the PI against the trial's budget and why a setup is not legal — saved with the Local
    /// profile as this trial's own setup (the "tune preset" CH46 asks for), raced by every later run of the trial. Nothing is
    /// bought; the loaner is never a garage car.
    /// </summary>
    public sealed class TrialTuneScreen : UIScreen
    {
        public override string ScreenName => "Trial Tune";
        public const int SlotRows = 4, TuneRows = 6;

        ChallengeTrialDef trial;
        MechanicalSnapshot setup = new MechanicalSnapshot();
        TrialLoanerBuild current;
        readonly List<string> slots = new List<string>();
        TextMeshProUGUI heading, summary, note;
        readonly List<Button> slotButtons = new List<Button>();
        readonly List<(GameObject Root, TextMeshProUGUI Label, Button Minus, Button Plus)> tuneRows = new List<(GameObject, TextMeshProUGUI, Button, Button)>();
        Button save, back, page;
        string pendingNote;
        int tunePage;

        /// <summary>The page of tuning controls shown (<see cref="TuneRows"/> a page; tours read it).</summary>
        public int TunePage => tunePage;
        List<TuningControlInfo> Controls => trial != null && trial.Loaner.Tunable && current != null ? current.Controls : new List<TuningControlInfo>();
        int TunePages => Mathf.Max(1, (Controls.Count + TuneRows - 1) / TuneRows);

        static ContentLibrary Lib => ContentLibrary.Load();

        /// <summary>The setup being edited and how it resolves (tours read them).</summary>
        public MechanicalSnapshot Setup => setup;
        public TrialLoanerBuild Current => current;

        /// <summary>The online account whose setup this is (null: the Local profile's).</summary>
        string onlineAccount;

        /// <summary>Opens a tunable trial's workshop for an online event: the account's setup kept on this PC, sent with Event Ready.</summary>
        public void OpenOnline(ChallengeTrialDef t, string account)
        {
            Open(t);
            onlineAccount = account;
            MechanicalSnapshot saved = OnlineTrialSetups.Get(account, t.Id);
            setup = Clone(saved) ?? new MechanicalSnapshot();
            Resolve();
            pendingNote = saved != null ? "Your saved setup for this online event." : "The loaner as supplied — change it, then save; the server checks every part and setting.";
        }

        /// <summary>Opens a tunable trial's workshop on the profile's saved setup (or the loaner as supplied).</summary>
        public void Open(ChallengeTrialDef t)
        {
            trial = t;
            onlineAccount = null;
            MechanicalSnapshot saved = null;
            LocalSession.Current?.Profile?.TrialSetups?.TryGetValue(t.Id, out saved);
            setup = Clone(saved) ?? new MechanicalSnapshot();
            tunePage = 0;
            slots.Clear();
            slots.AddRange((t.Loaner.Choices ?? new Dictionary<string, List<string>>()).Keys.OrderBy(k => k, System.StringComparer.Ordinal).Take(SlotRows));
            Resolve();
            pendingNote = saved != null ? "Your saved setup for this trial." : "The loaner as supplied — change it, then save.";
        }

        static MechanicalSnapshot Clone(MechanicalSnapshot s) => s == null ? null
            : new MechanicalSnapshot { Parts = new SortedDictionary<string, string>(s.Parts ?? new SortedDictionary<string, string>(), System.StringComparer.Ordinal), Tuning = s.Tuning?.Clone() ?? new TuningSetup() };

        void Resolve()
        {
            if (trial == null) return;
            CarDef car = Lib.Catalogue.Car(trial.Loaner.Car);
            current = TrialLoaners.ResolveSetup(trial.Loaner, setup, car, Lib.Catalogue.CarTunings[car.Id], Lib.Parts);
        }

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0.14f, 0.05f), new Vector2(0.86f, 0.95f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.94f));
            RectTransform col = UIFactory.Column("TrialTune", panel.transform, new Vector2(0, 0.03f), new Vector2(1, 0.97f), new Vector2(48, 0), new Vector2(-48, 0), 8f);
            heading = UIFactory.Row("Heading", col, "TUNE THE LOANER", SignalTheme.Heading, SignalTheme.Label, 1000, 0, true);
            summary = UIFactory.Row("TuneSummary", col, "", SignalTheme.Body, SignalTheme.Label, 1000, 64);
            summary.textWrappingMode = TextWrappingModes.Normal;
            summary.richText = true;
            for (int i = 0; i < SlotRows; i++)
            {
                int index = i;
                slotButtons.Add(UIFactory.Button("TuneSlot" + i, col, "", () => CycleSlot(index), 900, 44));
            }
            for (int i = 0; i < TuneRows; i++)
            {
                int index = i;
                RectTransform row = UIFactory.Rect("TuneRow" + i, col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
                row.sizeDelta = new Vector2(900, 46);
                TextMeshProUGUI label = UIFactory.Label("Label", row, "", SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.MidlineLeft);
                label.rectTransform.anchorMin = Vector2.zero;
                label.rectTransform.anchorMax = Vector2.one;
                label.rectTransform.offsetMin = new Vector2(12, 0);
                label.rectTransform.offsetMax = new Vector2(-136, 0);
                label.richText = true;
                Button minus = UIFactory.Button("TrialTuneMinus" + i, row, "−", () => Nudge(index, -1), 60, 42);
                Button plus = UIFactory.Button("TrialTunePlus" + i, row, "+", () => Nudge(index, +1), 60, 42);
                Place(minus, 900 - 128);
                Place(plus, 900 - 62);
                row.gameObject.SetActive(false);
                tuneRows.Add((row.gameObject, label, minus, plus));
            }
            page = UIFactory.Button("TrialTunePage", col, "", () => { tunePage = (tunePage + 1) % TunePages; Render(); }, 520, 44);
            note = UIFactory.Row("TuneNote", col, "", SignalTheme.Small, SignalTheme.LabelDim, 1000, 60);
            note.textWrappingMode = TextWrappingModes.Normal;
            save = UIFactory.Button("TrialTuneSave", col, "Save This Setup", Save, 520, 52);
            back = UIFactory.Button("TrialTuneBack", col, "Back", () => App.Router.Back(), 520, 48);
        }

        static void Place(Button b, float x)
        {
            RectTransform r = (RectTransform)b.transform;
            r.anchorMin = r.anchorMax = new Vector2(0, 0.5f);
            r.pivot = new Vector2(0, 0.5f);
            r.anchoredPosition = new Vector2(x, 0);
        }

        public override Selectable DefaultFocus => slotButtons.Count > 0 && slotButtons[0].gameObject.activeSelf ? slotButtons[0] : save;

        public override void OnShow()
        {
            if (pendingNote != null) { note.text = pendingNote; pendingNote = null; }
            Resolve();
            Render();
        }

        /// <summary>The parts a slot may hold here: the supplied one (or stock) first, then the trial's free choices.</summary>
        List<string> Options(string slot)
        {
            var o = new List<string> { trial.Loaner.Parts.TryGetValue(slot, out string given) ? given : "" };
            foreach (string p in trial.Loaner.Choices[slot]) if (!o.Contains(p)) o.Add(p);
            return o;
        }

        string Installed(string slot) => setup.Parts.TryGetValue(slot, out string p) ? p : trial.Loaner.Parts.TryGetValue(slot, out string g) ? g : "";

        /// <summary>The next part for a slot (tours click it).</summary>
        public void CycleSlot(int i)
        {
            if (trial == null || i >= slots.Count) return;
            string slot = slots[i];
            List<string> o = Options(slot);
            string next = o[(o.IndexOf(Installed(slot)) + 1) % o.Count];
            if (string.IsNullOrEmpty(next)) setup.Parts.Remove(slot);
            else setup.Parts[slot] = next;
            Resolve();
            Render();
        }

        /// <summary>One step of a tuning control (tours click it).</summary>
        public void Nudge(int i, int direction)
        {
            i += tunePage * TuneRows;
            if (trial == null || !trial.Loaner.Tunable || current == null || i >= current.Controls.Count) return;
            TuningControlInfo c = current.Controls[i];
            int v = Mathf.Clamp(TuningModel.ValueOrDefault(setup.Tuning, c) + direction * c.Step, c.Min, c.Max);
            if (v == c.Default) setup.Tuning.Values.Remove(c.Key);
            else setup.Tuning.Values[c.Key] = v;
            Resolve();
            Render();
        }

        void Save()
        {
            if (onlineAccount != null)
            {
                // Online: kept on this PC and sent with Event Ready; the control plane and the game server validate it.
                string why = null;
                bool ok = current != null && current.Ok && OnlineTrialSetups.Save(onlineAccount, trial.Id, Clone(setup), out why);
                note.text = ok ? "Saved for this online event: it goes with your Event Ready, and the server checks every part and setting."
                    : "Not saved: " + (why ?? string.Join("; ", current?.Problems ?? new List<string>()));
                Debug.Log($"[NightSignal.Trial] {trial.Id} online setup {(ok ? "saved" : "not saved")} " +
                          $"(parts {string.Join(", ", setup.Parts.Select(kv => kv.Key + "=" + kv.Value))}; tune {string.Join(", ", setup.Tuning.Values.Select(kv => kv.Key + "=" + kv.Value))})");
                Render();
                return;
            }
            LocalSession s = LocalSession.Current;
            if (s?.Profile == null || trial == null) { note.text = "Open a Local profile to keep a setup."; return; }
            LocalProgressionResult r = LocalProgression.SaveTrialSetup(s.Profile, Lib.Catalogue, Lib.Parts, trial.Id, setup);
            string saveNote = "";
            bool kept = r.Status == LocalOperationStatus.Applied && s.Commit(r, out saveNote);
            note.text = kept ? "Saved: every run of this trial now races this setup."
                : r.Status == LocalOperationStatus.Applied ? "Not saved: " + saveNote : r.Reason;
            Debug.Log($"[NightSignal.Trial] {trial.Id} setup {(kept ? "saved" : "not saved: " + (r.Status == LocalOperationStatus.Applied ? saveNote : r.Reason))} " +
                      $"(parts {string.Join(", ", setup.Parts.Select(kv => kv.Key + "=" + kv.Value))}; tune {string.Join(", ", setup.Tuning.Values.Select(kv => kv.Key + "=" + kv.Value))})");
            Render();
        }

        void Render()
        {
            if (trial == null || heading == null) return;
            CarDef car = Lib.Catalogue.Car(trial.Loaner.Car);
            heading.text = $"TUNE THE LOANER · {trial.Challenge}";
            string pi = current?.Pi != null ? current.Pi.Value.ToString() : "?";
            summary.text = $"{car.Name} ({car.Drive}) — PI {pi}{(trial.Loaner.PiBudget > 0 ? $" of a budget of {trial.Loaner.PiBudget}" : "")}; the parts here are free" +
                           (slots.Count > 0 ? " (each part button fits that slot's next part)." : ".") +
                           (current != null && !current.Ok ? $"\n<color=#F2A541>Not legal: {Esc(string.Join("; ", current.Problems.Distinct()))}</color>" : "\n<color=#3EC6D8>A legal setup.</color>");
            for (int i = 0; i < slotButtons.Count; i++)
            {
                bool on = i < slots.Count;
                slotButtons[i].gameObject.SetActive(on);
                if (!on) continue;
                string id = Installed(slots[i]);
                string name = string.IsNullOrEmpty(id) ? "stock" : Lib.Parts.TryPart(id, out PartDef pd) ? pd.Name : id;
                slotButtons[i].GetComponentInChildren<TextMeshProUGUI>().text = $"{slots[i]}: {name}";
            }
            List<TuningControlInfo> controls = Controls;
            if (tunePage >= TunePages) tunePage = 0;
            page.gameObject.SetActive(TunePages > 1);
            page.GetComponentInChildren<TextMeshProUGUI>().text = $"More Controls ({tunePage + 1} of {TunePages})";
            for (int i = 0; i < tuneRows.Count; i++)
            {
                int k = tunePage * TuneRows + i;
                bool on = k < controls.Count;
                tuneRows[i].Root.SetActive(on);
                if (!on) continue;
                TuningControlInfo c = controls[k];
                int v = TuningModel.ValueOrDefault(setup.Tuning, c);
                tuneRows[i].Label.text = $"<b>{Esc(c.Key)}</b>  {v} <size=80%>{Esc(c.Unit)}</size>{(v != c.Default ? "  (changed)" : "")}  <size=75%><color=#9A968D>{c.Min}–{c.Max}, default {c.Default}</color></size>";
                tuneRows[i].Minus.interactable = v > c.Min;
                tuneRows[i].Plus.interactable = v < c.Max;
            }
            save.interactable = current != null && current.Ok;
        }

        static string Esc(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");
    }
}
