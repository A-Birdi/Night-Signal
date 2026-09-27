using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Course access (Addendum 01 §5): every course with how this ONLINE profile holds it — starter, owned (bought or
    /// cleared), buyable for credits, or earned only by a campaign clear — and a two-step Buy. The price shown is the
    /// catalogue's; the server decides inside its debit transaction (Idempotency-Key per purchase attempt, the shown
    /// price echoed so a changed price is refused rather than charged).
    /// </summary>
    public sealed class CourseAccessScreen : UIScreen
    {
        public override string ScreenName => "Courses";
        public override string MusicCue => "MUS_MENU_B";

        const int PageSize = 10;

        ContentCatalogue cat;
        List<CourseDef> courses = new List<CourseDef>();
        readonly List<Button> rows = new List<Button>();
        TextMeshProUGUI wallet, detail, error, pageLine;
        Button buy, prev, next;
        int page, selected;
        bool confirming, busy, dirty = true;
        string purchaseKey; // one key per purchase attempt: a retry of the same attempt can never charge twice
        string message = "";

        OnlineSession S => OnlineSession.Current;

        /// <summary>True while a purchase is in flight.</summary>
        public bool Busy => busy;

        protected override void OnBuild(RectTransform root)
        {
            cat = ContentLibrary.Load()?.Catalogue;
            if (cat != null) courses = cat.Courses.Where(c => c.Kind != "tutorial").OrderBy(c => c.Id.StartsWith("FP") ? 1 : 0).ThenBy(c => c.Id).ToList();
            UIFactory.Panel("Backdrop", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.035f, 0.04f, 0.05f, 0.9f));

            Image left = UIFactory.Panel("CourseDetail", root, new Vector2(0, 0), new Vector2(0.42f, 1), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.95f));
            RectTransform lcol = UIFactory.Column("DetailColumn", left.transform, new Vector2(0, 0.03f), new Vector2(1, 0.95f), new Vector2(48, 0), new Vector2(-24, 0), 12f);
            UIFactory.Row("Heading", lcol, "COURSES", SignalTheme.Heading, SignalTheme.Label, 700, 0, true);
            wallet = UIFactory.Row("Wallet", lcol, "", SignalTheme.Body, SignalTheme.Label, 700, 34);
            wallet.richText = true;
            detail = UIFactory.Row("Detail", lcol, "", SignalTheme.Body, SignalTheme.Label, 700, 330);
            detail.richText = true;
            buy = UIFactory.Button("BuyCourse", lcol, "Buy", Buy, 560, 58);
            error = UIFactory.Row("Error", lcol, "", SignalTheme.Small, SignalTheme.Caution, 700, 60);
            UIFactory.Row("Legend", lcol, "Starter and owned courses can be chosen for Freeplay; one member who holds a course lets the whole convoy race it.",
                SignalTheme.Small, SignalTheme.LabelDim, 700, 60);

            RectTransform col = UIFactory.Column("CourseList", root, new Vector2(0.44f, 0.03f), new Vector2(0.98f, 0.95f), Vector2.zero, Vector2.zero, 8f);
            for (int i = 0; i < PageSize; i++)
            {
                int index = i;
                Button b = UIFactory.Button("Course" + i, col, "", () => Select(page * PageSize + index), 1000, 56);
                b.GetComponentInChildren<TextMeshProUGUI>().richText = true;
                rows.Add(b);
            }
            pageLine = UIFactory.Row("Page", col, "", SignalTheme.Small, SignalTheme.LabelDim, 1000, 28);
            prev = UIFactory.Button("PrevPage", col, "Previous", () => { page = Mathf.Max(0, page - 1); dirty = true; }, 300, 48);
            next = UIFactory.Button("NextPage", col, "Next", () => { page = Mathf.Min(Pages - 1, page + 1); dirty = true; }, 300, 48);
            UIFactory.Button("Back", col, "Back", () => App.Router.Back(), 300, 48);
        }

        int Pages => Mathf.Max(1, (courses.Count + PageSize - 1) / PageSize);

        public override void OnShow()
        {
            if (S == null)
            {
                App.Router.Show(App.SignIn, false);
                return;
            }
            S.Changed -= MarkDirty;
            S.Changed += MarkDirty;
            confirming = false;
            dirty = true;
            _ = S.RefreshMe();
        }

        public override void OnHide()
        {
            if (S != null) S.Changed -= MarkDirty;
        }

        void MarkDirty() => dirty = true;

        public override Selectable DefaultFocus => rows.Count > 0 ? rows[0] : null;

        public override void Tick()
        {
            if (!dirty || S == null) return;
            dirty = false;
            Render();
        }

        /// <summary>Selects a course by ID (tours) and shows its page.</summary>
        public void SelectCourse(string courseId)
        {
            int i = courses.FindIndex(c => c.Id == courseId);
            if (i >= 0) Select(i);
        }

        void Select(int index)
        {
            if (index < 0 || index >= courses.Count) return;
            selected = index;
            page = index / PageSize;
            confirming = false;
            purchaseKey = null;
            message = "";
            dirty = true;
        }

        Dictionary<string, string> Owned()
        {
            var owned = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JToken o in ((S.Me?["courses"] as JObject)?["owned"] as JArray) ?? new JArray())
                owned[(string)o["courseId"]] = (string)o["source"];
            return owned;
        }

        long Balance => (long?)(S.Me?["wallet"] as JObject)?["balance"] ?? 0;

        /// <summary>How this profile holds the course, for the list and the detail panel.</summary>
        string AccessText(CourseDef c, CourseAccessRule rule, Dictionary<string, string> owned, bool shortForm)
        {
            if (rule.Kind == CourseAccessKind.Starter) return "<color=#3EC6D8>starter</color>";
            if (owned.TryGetValue(c.Id, out string source))
                return source == "purchase" ? "<color=#3EC6D8>owned — bought</color>" : source == "campaign-clear" ? "<color=#3EC6D8>owned — campaign clear</color>" : "<color=#3EC6D8>owned</color>";
            switch (rule.Kind)
            {
                case CourseAccessKind.PurchaseOrCampaignClear:
                    return shortForm ? $"{rule.Price:N0} cr  <size=80%>or clear {rule.UnlockStage}</size>"
                                     : $"Buy for <b>{rule.Price:N0} cr</b>, or clear stage <b>{rule.UnlockStage}</b> on Normal for free.";
                case CourseAccessKind.PurchaseOnly:
                    return shortForm ? $"{rule.Price:N0} cr" : $"Freeplay-only course: buy for <b>{rule.Price:N0} cr</b>.";
                case CourseAccessKind.CampaignRewardOnly:
                    return shortForm ? $"<color=#9A968D>reward: {rule.UnlockStage}</color>" : $"Earned only by clearing <b>{rule.UnlockStage}</b> on Normal; it cannot be bought.";
                default:
                    return "<color=#9A968D>not available</color>";
            }
        }

        void Render()
        {
            if (cat == null) return;
            Dictionary<string, string> owned = Owned();
            wallet.text = $"Balance  <b>{Balance:N0} cr</b>   ·   {owned.Count} courses available";
            for (int i = 0; i < rows.Count; i++)
            {
                int index = page * PageSize + i;
                bool on = index < courses.Count;
                rows[i].gameObject.SetActive(on);
                if (!on) continue;
                CourseDef c = courses[index];
                string mark = index == selected ? "<color=#E5484D>›</color> " : "   ";
                rows[i].GetComponentInChildren<TextMeshProUGUI>().text =
                    $"{mark}<b>{c.Id}</b>  {Esc(c.Name)}   <size=80%>{c.Format}</size>   ·   {AccessText(c, CourseAccess.RuleFor(cat, c.Id), owned, true)}";
            }
            pageLine.text = $"Page {page + 1} of {Pages}";
            prev.interactable = page > 0;
            next.interactable = page < Pages - 1;

            CourseDef sel = courses.Count > 0 ? courses[Mathf.Clamp(selected, 0, courses.Count - 1)] : null;
            if (sel == null) return;
            CourseAccessRule rule = CourseAccess.RuleFor(cat, sel.Id);
            bool holds = rule.Kind == CourseAccessKind.Starter || owned.ContainsKey(sel.Id);
            string laps = sel.Format == "circuit" && sel.Laps > 0 ? $" · {sel.Laps} laps" : "";
            detail.text = $"<b>{sel.Id}  {Esc(sel.Name)}</b>\n<size=85%>{Esc(sel.Region)} · {sel.Format}{laps} · {sel.TargetLengthKm:0.0} km</size>\n\n" +
                          $"{AccessText(sel, rule, owned, false)}\n\n<size=80%><color=#9A968D>{Esc(sel.FormatDescription)}</color></size>";
            bool canBuy = !holds && rule.Purchasable;
            buy.gameObject.SetActive(canBuy);
            buy.interactable = !busy && Balance >= rule.Price;
            buy.GetComponentInChildren<TextMeshProUGUI>().text = !canBuy ? "" :
                Balance < rule.Price ? $"Need {rule.Price - Balance:N0} cr more" :
                confirming ? $"Confirm: spend {rule.Price:N0} cr" : $"Buy for {rule.Price:N0} cr";
            error.text = !string.IsNullOrEmpty(S.LastError) ? S.LastError : message;
        }

        async void Buy()
        {
            if (busy || cat == null) return;
            CourseDef sel = courses[Mathf.Clamp(selected, 0, courses.Count - 1)];
            CourseAccessRule rule = CourseAccess.RuleFor(cat, sel.Id);
            if (!confirming)
            {
                confirming = true; // spending credits takes a second, deliberate press
                purchaseKey = "course-" + Guid.NewGuid().ToString("N");
                dirty = true;
                return;
            }
            busy = true;
            try
            {
                JObject r = await S.Rest(HttpMethod.Post, $"/v1/me/courses/{Uri.EscapeDataString(sel.Id)}/purchase", new { expectedPrice = rule.Price },
                    new Dictionary<string, string> { { "Idempotency-Key", purchaseKey } });
                if (r != null)
                {
                    confirming = false;
                    purchaseKey = null;
                    await S.RefreshMe();
                    message = (string)r["outcome"] == "already_owned" ? "You already hold this course." : $"Bought {sel.Name}. Balance {(long?)r["balance"] ?? 0:N0} cr.";
                }
            }
            finally
            {
                busy = false;
                dirty = true;
            }
        }

        static string Esc(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");
    }
}
