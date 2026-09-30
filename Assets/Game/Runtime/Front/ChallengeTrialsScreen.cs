using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Challenge trials offline (docs/CHALLENGE_TRIALS.md; spec §11 "fixed loaners"): every trial in a list with its state, and
    /// the open trial on the right — the challenge's predicate, the supplied loaner (car, parts, PI), the rules, the published
    /// targets and the last run's verdict, with "Start Trial". A pass is kept in the Local profile; a grouped challenge (CH54)
    /// is earned once every trial of its group is passed.
    /// </summary>
    public sealed class ChallengeTrialsScreen : UIScreen
    {
        public override string ScreenName => "Challenge Trials";
        public const int Rows = 10;
        TextMeshProUGUI count, title, predicate, loaner, rules, targets, verdict;
        readonly List<Button> rows = new List<Button>();
        Button start, back;
        ChallengeTrialDef open;
        readonly Dictionary<string, string> lastVerdict = new Dictionary<string, string>();

        /// <summary>The trial open on the right and its last verdict (tours read them).</summary>
        public ChallengeTrialDef Open => open;
        public string Verdict => verdict != null ? verdict.text : "";

        static ContentLibrary Lib => ContentLibrary.Load();
        static IReadOnlyList<ChallengeTrialDef> All => Lib?.Catalogue?.ChallengeTrials?.Trials ?? (IReadOnlyList<ChallengeTrialDef>)new List<ChallengeTrialDef>();
        HashSet<string> PassedTrials => new HashSet<string>(LocalSession.Current?.Profile?.TrialsPassed ?? new List<string>(), StringComparer.Ordinal);
        bool Earned(string challenge) => LocalSession.Current?.Profile?.HasCompletedChallenge(challenge) == true;

        protected override void OnBuild(RectTransform root)
        {
            Image list = UIFactory.Panel("ListPanel", root, new Vector2(0, 0), new Vector2(0.4f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.92f));
            RectTransform col = UIFactory.Column("List", list.transform, new Vector2(0, 0.02f), new Vector2(1, 0.94f), new Vector2(56, 0), new Vector2(-28, 0), 6f);
            UIFactory.Row("Heading", col, "CHALLENGE TRIALS", SignalTheme.Heading, SignalTheme.Label, 600, 0, true);
            count = UIFactory.Row("Count", col, "", SignalTheme.Small, SignalTheme.LabelDim, 600, 28);
            for (int i = 0; i < Rows; i++)
            {
                int slot = i;
                rows.Add(UIFactory.Button("Trial" + i, col, "", () => Select(slot < All.Count ? All[slot] : null), 600, 46));
            }
            back = UIFactory.Button("Back", col, "Back", () => App.Router.Back(), 600, 46);

            Image detail = UIFactory.Panel("TrialPanel", root, new Vector2(0.42f, 0.06f), new Vector2(0.97f, 0.94f), Vector2.zero, Vector2.zero, new Color(0.04f, 0.045f, 0.055f, 0.9f));
            RectTransform dcol = UIFactory.Column("TrialColumn", detail.transform, new Vector2(0, 0.03f), new Vector2(1, 0.97f), new Vector2(40, 0), new Vector2(-40, 0), 10f);
            title = UIFactory.Row("TrialTitle", dcol, "", SignalTheme.Subheading, SignalTheme.Label, 900, 44, true);
            predicate = Wrapped("TrialPredicate", dcol, SignalTheme.Body, SignalTheme.Caution, 90);
            loaner = Wrapped("TrialLoaner", dcol, SignalTheme.Body, SignalTheme.Label, 110);
            rules = Wrapped("TrialRules", dcol, SignalTheme.Small, SignalTheme.LabelDim, 60);
            targets = Wrapped("TrialTargets", dcol, SignalTheme.Body, SignalTheme.Label, 50);
            start = UIFactory.Button("StartTrial", dcol, "Start Trial", () => { if (open != null) App.StartTrial(open, this); }, 520, 56);
            verdict = Wrapped("TrialVerdict", dcol, SignalTheme.Body, SignalTheme.Label, 120);
            verdict.richText = false;
        }

        static TextMeshProUGUI Wrapped(string name, RectTransform col, float size, Color color, float height)
        {
            TextMeshProUGUI t = UIFactory.Row(name, col, "", size, color, 900, height);
            t.textWrappingMode = TextWrappingModes.Normal;
            return t;
        }

        public override Selectable DefaultFocus => rows.Count > 0 && rows[0].gameObject.activeSelf ? rows[0] : back;

        public override void OnShow()
        {
            Refresh();
            Select(open ?? All.FirstOrDefault());
        }

        /// <summary>Records the verdict of the run that just ended (shown when the screen returns).</summary>
        public void SetVerdict(string trialId, string text)
        {
            lastVerdict[trialId] = text;
            if (open?.Id == trialId && verdict != null) verdict.text = text;
        }

        /// <summary>Opens a trial by id (tours).</summary>
        public bool SelectTrial(string id)
        {
            ChallengeTrialDef t = All.FirstOrDefault(x => x.Id == id);
            if (t == null) return false;
            Select(t);
            return true;
        }

        void Refresh()
        {
            HashSet<string> passed = PassedTrials;
            int earned = All.Select(t => t.Challenge).Distinct().Count(Earned);
            count.text = $"{All.Count} trials · {earned} of {All.Select(t => t.Challenge).Distinct().Count()} challenges earned";
            for (int i = 0; i < rows.Count; i++)
            {
                bool on = i < All.Count;
                rows[i].gameObject.SetActive(on);
                if (!on) continue;
                ChallengeTrialDef t = All[i];
                // Words, as the Driving School marks its lessons (the game font has no check-mark glyph).
                int group = string.IsNullOrEmpty(t.Group) ? 1 : All.Count(x => x.Group == t.Group);
                int groupPassed = string.IsNullOrEmpty(t.Group) ? 0 : All.Count(x => x.Group == t.Group && passed.Contains(x.Id));
                string state = Earned(t.Challenge) ? "  <color=#3EC6D8>earned</color>"
                    : groupPassed > 0 ? $"  <color=#F2A541>{groupPassed} of {group}</color>" : "";
                rows[i].GetComponentInChildren<TextMeshProUGUI>().text = $"{t.Challenge}  {t.Title}{state}";
            }
        }

        void Select(ChallengeTrialDef t)
        {
            open = t;
            if (t == null || title == null) return;
            ContentCatalogue cat = Lib.Catalogue;
            cat.TryChallenge(t.Challenge, out ChallengeDef ch);
            CarDef car = cat.Car(t.Loaner.Car);
            ResolveResult r = TrialLoaners.Resolve(t.Loaner, car, cat.CarTunings[car.Id], Lib.Parts, out PiEstimate pi);
            string parts = t.Loaner.Parts.Count == 0 ? "stock" : string.Join(", ", t.Loaner.Parts.Values.Select(id => Lib.Parts.TryPart(id, out PartDef p) ? p.Name : id));
            title.text = $"{t.Challenge} · {t.Title}";
            predicate.text = $"{(ch?.Tier ?? t.Tier).ToUpperInvariant()} — {ch?.PredicateText}";
            loaner.text = $"Supplied loaner: {car.Name} ({car.Drive}) — {parts}; PI {(r.Ok ? pi.Value.ToString() : "?")}" +
                          (t.Loaner.PiCap > 0 ? $" (cap {t.Loaner.PiCap})" : "") + $"\nCourse {t.Course} {cat.Course(t.Course).Name}, " +
                          (t.Conditions == "course" ? "its own conditions" : t.Conditions) + ". " + t.Brief;
            var said = new List<string>();
            if (t.Rules.NoReset) said.Add("no reset");
            if (t.Rules.MaxWallImpacts == 0) said.Add("no wall impact");
            else if (t.Rules.MaxWallImpacts > 0) said.Add($"at most {t.Rules.MaxWallImpacts} meaningful wall impact{(t.Rules.MaxWallImpacts == 1 ? "" : "s")}");
            if (t.Rules.NoHandbrake) said.Add("no handbrake after the start");
            if (t.Rules.BankEveryZone) said.Add("a chain banked in every judged zone");
            if (t.Rules.AllTyresPaved) said.Add("all four tyres on the paved road");
            if (t.Rules.AllChallengeGates) said.Add("every marked gate touched");
            if (t.Rules.NoCarContact) said.Add("no car-to-car contact");
            if (t.Rules.NoCheckpointCut) said.Add("no checkpoint cut");
            string field = t.IsRace
                ? $"A race against a fixed field of {t.Field.Count}: {string.Join(", ", t.Field.GroupBy(c => c.Car).Select(g => $"{g.Count()} × {cat.Car(g.Key).Name}"))}" +
                  (t.PlayerStartsLast ? "; you start last" : "")
                : "Solo, non-contact";
            rules.text = field + "; your garage and upgrades are not used" + (said.Count > 0 ? "; " + string.Join(", ", said) : "") + ".";
            var goals = new List<string>();
            if (t.Rules.Win) goals.Add("win");
            if (t.Rules.CleanZonePass) goals.Add("make the marked overtake cleanly and keep the place");
            if (t.JudgesTime) goals.Add(t.Targets.TimeMs > 0 ? $"beat {t.Targets.TimeMs / 60000}:{t.Targets.TimeMs / 1000 % 60:00}.{t.Targets.TimeMs % 1000 / 100}" : "time target not published yet");
            if (t.JudgesDrift) goals.Add(t.Targets.DriftRaw > 0 ? $"bank {t.Targets.DriftRaw:N0} raw drift" : "drift target not published yet");
            targets.text = $"{(ch?.Tier ?? t.Tier)} target: {string.Join(" and ", goals)}" + (string.IsNullOrEmpty(t.Group) ? "" : "  (one of a pair: both earn the challenge)");
            bool done = Earned(t.Challenge);
            verdict.text = lastVerdict.TryGetValue(t.Id, out string v) ? v
                : done ? "Challenge earned." : PassedTrials.Contains(t.Id) ? "This trial is passed; its pair is still open." : "";
            start.interactable = r.Ok;
            start.GetComponentInChildren<TextMeshProUGUI>().text = done || PassedTrials.Contains(t.Id) ? "Run Again" : "Start Trial";
        }
    }
}
