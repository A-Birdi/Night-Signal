using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// The offline Custom Cup (spec §8): a published three-leg schedule raced by the same field; each leg is an ordinary
    /// Freeplay race (paid and recorded as one), and between legs this page shows the cup table with the next leg and a way
    /// out — the short, cancelable results/ready area. DQs and DNFs keep their line and score nothing for that leg.
    /// </summary>
    public sealed class CupScreen : UIScreen
    {
        public override string ScreenName => trial != null ? "Challenge Cup" : "CustomCup";
        TextMeshProUGUI heading, schedule, table, note;
        Button next, leave;
        List<LocalEventPlan> legs = new List<LocalEventPlan>();
        CupTable cup;
        bool awaitingLeg;
        /// <summary>A challenge cup (slice 4) in progress on this page; null for a Custom Cup.</summary>
        ChallengeTrialDef trial;
        readonly List<string> legLines = new List<string>();

        /// <summary>The challenge cup on this page (tours read it; null for a Custom Cup).</summary>
        public ChallengeTrialDef Trial => trial;

        /// <summary>The cup in progress (tours read it).</summary>
        public CupTable Table => cup;

        /// <summary>Starts a cup: the schedule is published and the first leg runs at once.</summary>
        public void Begin(List<LocalEventPlan> plans)
        {
            trial = null;
            legs = plans;
            cup = new CupTable(plans.Select(p => p.CourseId));
            Debug.Log($"[NightSignal.Cup] schedule {string.Join(" → ", cup.Schedule)}; field {string.Join(", ", plans[0].OpposingAi)}");
            RunLeg();
        }

        /// <summary>Starts a challenge cup: its legs in order, one continuous session; leaving before the end ends it unpassed.</summary>
        public void BeginTrial(ChallengeTrialDef t, List<LocalEventPlan> plans)
        {
            trial = t;
            legs = plans;
            legLines.Clear();
            cup = new CupTable(plans.Select(p => p.CourseId));
            LocalEvents.BeginCup(t.Id);
            Debug.Log($"[NightSignal.Cup] challenge cup {t.Id}: {string.Join(" → ", cup.Schedule)} in the {t.Loaner.Car}");
            RunLeg();
        }

        void RunLeg()
        {
            awaitingLeg = true;
            App.StartLocalEvent(legs[cup.LegsRaced], this);
        }

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0.18f, 0.08f), new Vector2(0.82f, 0.92f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.94f));
            RectTransform col = UIFactory.Column("Cup", panel.transform, new Vector2(0, 0.04f), new Vector2(1, 0.96f), new Vector2(56, 0), new Vector2(-56, 0), 12f);
            heading = UIFactory.Row("Heading", col, "CUSTOM CUP", SignalTheme.Heading, SignalTheme.Label, 1000, 0, true);
            schedule = UIFactory.Row("Schedule", col, "", SignalTheme.Body, SignalTheme.Caution, 1000, 36);
            table = UIFactory.Row("CupTable", col, "", SignalTheme.Body, SignalTheme.Label, 1000, 330);
            table.richText = true;
            note = UIFactory.Row("CupNote", col, "", SignalTheme.Small, SignalTheme.LabelDim, 1000, 52);
            note.textWrappingMode = TextWrappingModes.Normal;
            next = UIFactory.Button("CupNext", col, "Next Leg", RunLeg, 620, 60);
            leave = UIFactory.Button("CupLeave", col, "Leave the Cup", Leave, 620, 52);
        }

        public override Selectable DefaultFocus => next.gameObject.activeSelf ? next : leave;

        void Leave()
        {
            if (trial != null)
            {
                // A challenge cup is one session: leaving before its last leg ends it, unpassed.
                if (LocalEvents.CupTrialId == trial.Id)
                {
                    LocalEvents.AbandonCup();
                    App.Trials.SetVerdict(trial.Id, "Cup left before its last leg — not passed (a challenge cup is one continuous session).");
                }
                App.Router.Show(App.Trials, false);
                return;
            }
            App.Router.Show(App.OfflineHub, false);
        }

        /// <summary>The challenge cup's session ended early (a leg not finished) or completed.</summary>
        bool TrialOver => trial != null && LocalEvents.CupTrialId != trial.Id;

        public override void OnShow()
        {
            if (awaitingLeg && cup != null && App.LastLocalResults != null)
            {
                cup.AddLeg(App.LastLocalResults.Select(r => new CupLegResult
                {
                    Id = r.Entrant.Human ? "you" : r.Entrant.Roster.EntrantId, Name = r.Entrant.Roster.DisplayName, Human = r.Entrant.Human,
                    Place = r.Outcome == Core.Rules.RunOutcome.Finished && r.Placement > 0 ? r.Placement : (int?)null,
                }));
                awaitingLeg = false;
                Debug.Log($"[NightSignal.Cup] after leg {cup.LegsRaced}: " + string.Join(" | ", cup.Standings().Select(e => $"{e.Name} {e.Points} ({Places(e)})")));
                if (trial != null && LocalEvents.CupLegsRun.Count >= cup.LegsRaced)
                {
                    // Each leg's own checks (after the last leg the verdict on the whole cup goes in the note).
                    string own = TrialJudge.JudgeCupLeg(trial, cup.LegsRaced - 1, LocalEvents.CupLegsRun[cup.LegsRaced - 1]).Summary;
                    legLines.Add($"{cup.Schedule[cup.LegsRaced - 1]}: {own}");
                    Debug.Log($"[NightSignal.Cup] {trial.Id} leg {cup.LegsRaced}: {own}");
                }
            }
            Render();
        }

        static string Places(CupEntrant e) => string.Join(" ", e.Places.Select(p => p?.ToString() ?? "–"));

        void Render()
        {
            if (cup == null) { schedule.text = ""; table.text = ""; return; }
            if (trial != null) { RenderTrial(); return; }
            schedule.text = string.Join("   →   ", cup.Schedule.Select((c, i) => (i < cup.LegsRaced ? "done " : i == cup.LegsRaced ? "next " : "") + c));
            var sb = new System.Text.StringBuilder("<color=#9A968D><pos=0%>POS<pos=8%>DRIVER<pos=52%>POINTS<pos=66%>LEGS</color>\n");
            int pos = 0;
            foreach (CupEntrant e in cup.Standings())
            {
                pos++;
                string line = $"<pos=0%>{pos}<pos=8%>{Escape(e.Name)}<pos=52%>{e.Points}<pos=66%>{Places(e)}";
                sb.Append(e.Human ? $"<color=#D7263D>{line}</color>\n" : line + "\n");
            }
            table.text = sb.ToString();
            bool more = !cup.Complete;
            next.gameObject.SetActive(more);
            if (more) next.GetComponentInChildren<TextMeshProUGUI>().text = $"Next Leg — {cup.Schedule[cup.LegsRaced]} ({cup.LegsRaced + 1} of {CupTable.Legs})";
            leave.GetComponentInChildren<TextMeshProUGUI>().text = more ? "Leave the Cup" : "Back";
            note.text = more
                ? $"Points {string.Join("-", CupTable.PointsByPlace)} for places 1–{CupTable.PointsByPlace.Length}; a DNF or DQ keeps its line and scores nothing for that leg. Each leg pays as an ordinary race; the cup has no stake."
                : $"Cup complete: you finished {cup.PositionOf("you")} of {cup.Standings().Count}.";
        }

        /// <summary>A challenge cup: each leg's course, its time to beat and what happened, then the cup's verdict.</summary>
        void RenderTrial()
        {
            heading.text = $"CHALLENGE CUP · {trial.Challenge}";
            schedule.text = string.Join("   →   ", cup.Schedule.Select((c, i) => (i < cup.LegsRaced ? "done " : i == cup.LegsRaced && !TrialOver ? "next " : "") + c));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < trial.Legs.Count; i++)
            {
                TrialCupLeg l = trial.Legs[i];
                string target = l.TimeMs > 0 ? $"beat {l.TimeMs / 60000}:{l.TimeMs / 1000 % 60:00}.{l.TimeMs % 1000 / 100}" : "no time to beat";
                sb.Append($"Leg {i + 1} — {l.Course} ({target}): ").Append(i < legLines.Count ? Escape(legLines[i].Substring(legLines[i].IndexOf(':') + 1).Trim()) : "not run yet").Append('\n');
            }
            table.text = sb.ToString();
            bool more = !cup.Complete && !TrialOver;
            next.gameObject.SetActive(more);
            if (more) next.GetComponentInChildren<TextMeshProUGUI>().text = $"Next Leg — {cup.Schedule[cup.LegsRaced]} ({cup.LegsRaced + 1} of {CupTable.Legs})";
            leave.GetComponentInChildren<TextMeshProUGUI>().text = more ? "Leave the Cup" : "Back";
            note.text = more
                ? "A challenge cup is one continuous session: leaving before the last leg ends it unpassed."
                : (LocalEvents.LastTrialVerdict?.Passed == true ? "CUP PASSED — " : "Cup not passed — ") + (LocalEvents.LastTrialVerdict?.Summary ?? "");
        }

        static string Escape(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");
    }
}
