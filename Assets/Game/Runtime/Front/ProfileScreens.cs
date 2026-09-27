using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Local profile choice (Addendum 01 §8.1): every Local profile on this PC with its rank and wallet, or a new one.
    /// Damaged saves are listed with the reason instead of being hidden.
    /// </summary>
    public sealed class ProfileSelectScreen : UIScreen
    {
        public override string ScreenName => "Local profiles";
        public override string MusicCue => "MUS_MENU_A";
        RectTransform list;
        TextMeshProUGUI status;
        Button create;
        Selectable first;

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.44f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.92f));
            RectTransform col = UIFactory.Column("Column", panel.transform, new Vector2(0, 0.06f), new Vector2(1, 0.9f), new Vector2(64, 0), new Vector2(-32, 0), 12f);
            UIFactory.Row("Heading", col, "LOCAL PROFILES", SignalTheme.Heading, SignalTheme.Label, 640, 0, true);
            UIFactory.Row("Domain", col,
                "Local / Offline progress lives on this PC only. It never becomes Online money, cars, rank or records.",
                SignalTheme.Small, SignalTheme.Caution, 640, 52);
            list = UIFactory.Column("Profiles", col, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero, 10f);
            list.sizeDelta = new Vector2(640, 8 * 66);
            create = UIFactory.Button("NewProfile", col, "New Local Profile", () => App.Router.Show(App.NewProfile), 620, 60);
            UIFactory.Button("Back", col, "Back to Title", () => App.Router.Show(App.MainMenu, false), 620, 52);
            status = UIFactory.Row("Status", col, "", SignalTheme.Small, SignalTheme.LabelDim, 640, 60);
        }

        public override Selectable DefaultFocus => first ?? create;

        public override void OnShow()
        {
            App.Domain = SessionDomain.Local;
            App.RefreshStrip();
            foreach (Transform child in list) Object.Destroy(child.gameObject);
            first = null;
            LocalSession s = LocalSession.Current;
            if (s == null)
            {
                status.text = "Content library missing: Local play is unavailable in this build.";
                create.interactable = false;
                return;
            }
            IReadOnlyList<ProfileSummary> profiles = s.ListProfiles();
            foreach (ProfileSummary p in profiles)
            {
                string id = p.ProfileId;
                bool ok = p.Status != ProfileLoadStatus.Failed;
                string label = ok ? $"{p.DisplayName}   ·   {p.Rank}   ·   {p.WalletBalance:N0} cr" : $"Damaged save {id}";
                Button b = UIFactory.Button("Profile-" + id, list, label, () => Open(id), 620, 56);
                b.GetComponentInChildren<TextMeshProUGUI>().richText = false; // names are data, never markup
                b.GetComponentInChildren<TextMeshProUGUI>().fontStyle = FontStyles.Normal;
                b.interactable = ok;
                if (first == null && ok) first = b;
            }
            create.interactable = profiles.Count < ProfileRepository.MaxProfiles;
            status.text = profiles.Count == 0 ? "No Local profile on this PC yet: create one to start the campaign."
                : $"{profiles.Count} of {ProfileRepository.MaxProfiles} profile slots used. Saves: {s.StorageFolder.Replace('\\', '/').Split('/').Last()} folder in the game's data directory.";
        }

        void Open(string id)
        {
            LocalSession s = LocalSession.Current;
            if (s.Open(id, out string message))
            {
                App.DisplayName = s.Profile.DisplayName;
                App.Router.Show(App.OfflineHub, false);
            }
            else status.text = message;
        }
    }

    /// <summary>New Local profile: a display name and one of the starter cars (spec §5.3). Starts with the 12,000 grant.</summary>
    public sealed class NewProfileScreen : UIScreen
    {
        public override string ScreenName => "New profile";
        TMP_InputField nameField;
        Stepper starter;
        TextMeshProUGUI status, carNote;
        List<CarDef> starters = new List<CarDef>();

        protected override void OnBuild(RectTransform root)
        {
            ContentCatalogue cat = LocalSession.Current?.Catalogue;
            if (cat != null) starters = cat.Cars.Where(c => c.Starter).OrderBy(c => c.BasePI).ToList();
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.44f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.92f));
            RectTransform col = UIFactory.Column("Column", panel.transform, new Vector2(0, 0.06f), new Vector2(1, 0.9f), new Vector2(64, 0), new Vector2(-32, 0), 14f);
            UIFactory.Row("Heading", col, "NEW LOCAL PROFILE", SignalTheme.Heading, SignalTheme.Label, 640, 0, true);
            UIFactory.Row("NameLabel", col, "Display name", SignalTheme.Small, SignalTheme.LabelDim, 640, 24);
            nameField = UIFactory.InputField("ProfileName", col, "Up to 24 characters", false, 96, 620, 56);
            starter = new Stepper(col, "Starter car", starters.Count,
                i => starters.Count == 0 ? "—" : $"{starters[i].Name}   PI {starters[i].BasePI}   {starters[i].Drive}");
            carNote = UIFactory.Row("CarNote", col, "", SignalTheme.Small, SignalTheme.LabelDim, 640, 52);
            starter.Changed += _ => RefreshCar();
            UIFactory.Button("Create", col, "Create Profile", Create, 620, 60);
            UIFactory.Button("Back", col, "Back", () => App.Router.Back(), 620, 52);
            status = UIFactory.Row("Status", col, "You start with 12,000 credits and the baseline soundtrack. Nothing here is uploaded.",
                SignalTheme.Small, SignalTheme.LabelDim, 640, 60);
            RefreshCar();
        }

        public override Selectable DefaultFocus => nameField;

        public override void OnShow() => nameField.text = "";

        void RefreshCar()
        {
            if (starters.Count == 0) return;
            CarDef c = starters[starter.Index];
            carNote.text = $"{c.Maker} {c.Name}: {c.Drive}, PI {c.BasePI}. Every starter has a viable upgrade path.";
        }

        void Create()
        {
            LocalSession s = LocalSession.Current;
            if (s == null || starters.Count == 0) return;
            if (s.Create(nameField.text, starters[starter.Index].Id, out string message))
            {
                App.DisplayName = s.Profile.DisplayName;
                App.Router.Show(App.OfflineHub, false);
            }
            else status.text = message;
        }
    }
}
