using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.Core.Story;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// The race diary (spec §5.3: "make the full story available in a race diary"): an entry for every stage the player has
    /// cleared (Normal, then Hard), the crew introductions their clears unlock, and the radio and timing-slip records
    /// collected through Normal progression. Read-only; entries appear as the campaign opens them.
    /// </summary>
    public sealed class DiaryScreen : UIScreen
    {
        public override string ScreenName => "Diary";
        public const int PerPage = 11;
        TextMeshProUGUI heading, count, title, body;
        readonly List<Button> rows = new List<Button>();
        Button prev, nextPage, back;
        List<DiaryEntry> entries = new List<DiaryEntry>();
        int page, selected = -1;
        readonly HashSet<string> read = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The entries read (crew introductions, "crew:&lt;crew&gt;"), as the profile or the server records them.</summary>
        public IReadOnlyCollection<string> Read => read;

        /// <summary>The entries on offer and the one open (tours read them).</summary>
        public IReadOnlyList<DiaryEntry> Entries => entries;
        public DiaryEntry Open => selected >= 0 && selected < entries.Count ? entries[selected] : null;

        protected override void OnBuild(RectTransform root)
        {
            Image list = UIFactory.Panel("ListPanel", root, new Vector2(0, 0), new Vector2(0.42f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.92f));
            RectTransform col = UIFactory.Column("List", list.transform, new Vector2(0, 0.02f), new Vector2(1, 0.94f), new Vector2(56, 0), new Vector2(-28, 0), 8f);
            heading = UIFactory.Row("Heading", col, "RACE DIARY", SignalTheme.Heading, SignalTheme.Label, 600, 0, true);
            count = UIFactory.Row("Count", col, "", SignalTheme.Small, SignalTheme.LabelDim, 600, 30);
            for (int i = 0; i < PerPage; i++)
            {
                int slot = i;
                rows.Add(UIFactory.Button("Diary-Entry" + i, col, "", () => Select(page * PerPage + slot), 600, 44));
            }
            prev = UIFactory.Button("Diary-PrevPage", col, "Previous page", () => Page(page - 1), 600, 44);
            nextPage = UIFactory.Button("Diary-NextPage", col, "Next page", () => Page(page + 1), 600, 44);
            back = UIFactory.Button("Back", col, "Back", () => App.Router.Back(), 600, 48);

            Image reader = UIFactory.Panel("Reader", root, new Vector2(0.44f, 0.06f), new Vector2(0.97f, 0.94f), Vector2.zero, Vector2.zero, new Color(0.04f, 0.045f, 0.055f, 0.9f));
            title = UIFactory.Label("EntryTitle", reader.transform, "", SignalTheme.Subheading, SignalTheme.Label, TextAlignmentOptions.TopLeft, true);
            title.rectTransform.anchorMin = new Vector2(0, 0.86f);
            title.rectTransform.anchorMax = new Vector2(1, 0.97f);
            title.rectTransform.offsetMin = new Vector2(40, 0);
            title.rectTransform.offsetMax = new Vector2(-40, 0);
            body = UIFactory.Label("EntryText", reader.transform, "", SignalTheme.Body, SignalTheme.Label, TextAlignmentOptions.TopLeft);
            body.rectTransform.anchorMin = new Vector2(0, 0.04f);
            body.rectTransform.anchorMax = new Vector2(1, 0.85f);
            body.rectTransform.offsetMin = new Vector2(40, 0);
            body.rectTransform.offsetMax = new Vector2(-40, 0);
            body.textWrappingMode = TextWrappingModes.Normal;
            body.richText = false;
        }

        public override Selectable DefaultFocus => rows.Count > 0 && rows[0].interactable ? rows[0] : back;

        public override void OnShow()
        {
            entries = App.Domain == SessionDomain.Local
                ? Build(LocalSession.Current?.Profile, ContentLibrary.Load())
                : BuildOnline(OnlineSession.Current?.Me, ContentLibrary.Load());
            page = 0;
            selected = -1;
            read.Clear();
            if (App.Domain == SessionDomain.Local) foreach (string r in LocalSession.Current?.Profile?.DiaryRead ?? new List<string>()) read.Add(r);
            else LoadReadMarks();
            count.text = entries.Count == 0
                ? "Nothing yet: each stage you clear adds its entry."
                : $"{entries.Count(e => e.Kind == "stage")} stage entries · {entries.Count(e => e.Kind == "crew")} crews · {entries.Count(e => e.Kind == "record")} records";
            Page(0);
            Select(entries.Count > 0 ? 0 : -1);
        }

        /// <summary>The diary a Local profile has opened (the campaign's cleared stages decide it).</summary>
        public static List<DiaryEntry> Build(LocalProfile p, ContentLibrary lib)
        {
            StoryText story = lib?.Story;
            ContentCatalogue cat = lib?.Catalogue;
            if (story == null || cat == null || p == null) return new List<DiaryEntry>();
            var number = cat.Stages.ToDictionary(s => s.Id, s => s.Number);
            return story.Diary(cat.Stages.OrderBy(s => s.Number).Select(s => s.Id),
                (id, mode) => number.TryGetValue(id ?? "", out int n) && p.Campaign.For(mode).Contains(n));
        }

        /// <summary>The diary an online account has opened, from the campaign clears the server reports (/v1/me).</summary>
        public static List<DiaryEntry> BuildOnline(Newtonsoft.Json.Linq.JObject me, ContentLibrary lib)
        {
            StoryText story = lib?.Story;
            ContentCatalogue cat = lib?.Catalogue;
            var campaign = me?["campaign"] as Newtonsoft.Json.Linq.JObject;
            if (story == null || cat == null || campaign == null) return new List<DiaryEntry>();
            var normal = campaign["normalCleared"] as Newtonsoft.Json.Linq.JArray;
            var hard = campaign["hardCleared"] as Newtonsoft.Json.Linq.JArray;
            var number = cat.Stages.ToDictionary(s => s.Id, s => s.Number);
            return story.Diary(cat.Stages.OrderBy(s => s.Number).Select(s => s.Id), (id, mode) =>
            {
                Newtonsoft.Json.Linq.JArray flags = mode == CampaignMode.Hard ? hard : normal;
                return number.TryGetValue(id ?? "", out int n) && flags != null && n >= 1 && n <= flags.Count && (bool?)flags[n - 1] == true;
            });
        }

        void Page(int to)
        {
            int pages = Math.Max(1, (entries.Count + PerPage - 1) / PerPage);
            page = Mathf.Clamp(to, 0, pages - 1);
            for (int i = 0; i < rows.Count; i++)
            {
                int k = page * PerPage + i;
                bool on = k < entries.Count;
                rows[i].gameObject.SetActive(on);
                if (!on) continue;
                DiaryEntry e = entries[k];
                string kind = e.Kind == "crew" ? "Crew" : e.Kind == "record" ? "Record" : "Stage";
                bool isRead = e.Kind == "crew" && read.Contains(DiaryChallenges.CrewEntry(e.Id));
                rows[i].GetComponentInChildren<TextMeshProUGUI>().text = $"{kind} · {e.Title}" + (isRead ? "  <color=#9A968D>· read</color>" : "");
            }
            prev.interactable = page > 0;
            nextPage.interactable = page < pages - 1;
        }

        void Select(int k)
        {
            selected = k;
            DiaryEntry e = Open;
            title.text = e == null ? "" : e.Title;
            body.text = e == null ? "" : e.Text;
            if (e != null && e.Kind == "crew") MarkRead(DiaryChallenges.CrewEntry(e.Id));
        }

        /// <summary>A crew introduction opened is read (CH70 counts all six): on the Local profile, or recorded by the server.</summary>
        async void MarkRead(string entry)
        {
            if (read.Contains(entry)) return;
            if (App.Domain == SessionDomain.Local)
            {
                LocalSession s = LocalSession.Current;
                if (s?.Profile == null) return;
                LocalProgressionResult r = LocalProgression.MarkDiaryRead(s.Profile, s.Catalogue, ContentLibrary.Load()?.Story?.Crews, entry);
                if (r.Status == LocalOperationStatus.Applied && s.Commit(r, out _)) read.Add(entry);
            }
            else
            {
                OnlineSession o = OnlineSession.Current;
                if (o == null) return;
                (int status, Newtonsoft.Json.Linq.JObject reply) = await o.Client.Send(System.Net.Http.HttpMethod.Post, "/v1/me/diary/read",
                    new Newtonsoft.Json.Linq.JObject { ["entry"] = entry });
                if (status >= 200 && status < 300) read.Add(entry);
            }
            Page(page);
        }

        async void LoadReadMarks()
        {
            OnlineSession o = OnlineSession.Current;
            if (o == null) return;
            Newtonsoft.Json.Linq.JObject r = await o.Client.Get("/v1/me/diary");
            foreach (Newtonsoft.Json.Linq.JToken t in (r?["read"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray()) read.Add((string)t);
            Page(page);
        }
    }
}
