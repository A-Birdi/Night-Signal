using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Characters;
using NightSignal.Core.Customization;
using NightSignal.Core.Meet;
using NightSignal.Meet;
using NightSignal.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// The Player Card (spec §11): display name, optional pronouns and the driver's appearance, chosen from accessible
    /// starting looks and simple steps (build, height, skin, face, posture, hair, clothes, colours, two accessories) with a
    /// live turntable preview. Visual only — never hitboxes, steering or performance. Online it is saved to the server-owned
    /// card with a revision; the server validates the look and hands it to the meet, where other drivers see the same
    /// person. Offline (a Local profile, no online session) the same card is saved in the profile and used at the offline meet.
    /// </summary>
    public sealed class PlayerCardScreen : UIScreen
    {
        public override string ScreenName => "Player Card";

        OnlineSession S => OnlineSession.Current;
        /// <summary>No online session but a Local profile open: the card belongs to that profile.</summary>
        LocalSession Local => S == null ? LocalSession.Current?.Profile != null ? LocalSession.Current : null : null;

        static readonly float[] Heights = { 1.50f, 1.55f, 1.60f, 1.65f, 1.70f, 1.75f, 1.80f, 1.85f, 1.90f, 1.95f };
        static readonly string[] Headwear = { "", "glasses", "round-glasses", "sunglasses", "cap", "beanie", "headband", "bandana", "headphones", "goggles", "earrings" };
        static readonly string[] Extras = { "", "scarf", "watch", "gloves", "wristband", "lanyard", "pin", "bag", "satchel", "camera", "belt", "open" };

        TMP_InputField nameField, pronounsField;
        TextMeshProUGUI status;
        RawImage preview;
        CharacterStage stage;
        CharacterLook look;
        CharacterLook loaded;
        bool loading, busy, dirtyPreview;
        readonly List<(Stepper Step, Action<int> Apply, Func<int> Read)> fields = new List<(Stepper, Action<int>, Func<int>)>();
        readonly List<(Stepper Step, Func<string> Read)> colourRows = new List<(Stepper, Func<string>)>();
        readonly Dictionary<Stepper, Image> swatches = new Dictionary<Stepper, Image>();
        Stepper preset;
        // The card's style (spec §11): background, frame, motif, title, layout, region, preferred car.
        Stepper editing, bgStep, frameStep, motifStep, titleStep, layoutStep, regionStep, carStep;
        // Showcase (spec §11 chosen records): up to three of the player's own records, online.
        readonly Stepper[] showcaseSteps = new Stepper[3];
        List<(string Key, string Label, string Value)> records = new List<(string, string, string)>();
        readonly string[] showcase = { "", "", "" };
        readonly List<GameObject> lookRows = new List<GameObject>(), styleRows = new List<GameObject>();
        CardView cardView;
        CardStyle style;
        List<string> ownedCars = new List<string>();
        HashSet<string> ownedCosmetics = new HashSet<string>(StringComparer.Ordinal);
        List<string> stats = new List<string>();
        static CardStyleCatalogue Cat => Content.ContentLibrary.Load()?.Customization?.Card;

        public CharacterLook Look => look;
        /// <summary>The card style being edited.</summary>
        public CardStyle Style => style;
        /// <summary>The styled card preview.</summary>
        public CardView Card => cardView;
        /// <summary>The showcase keys chosen (empty slots left out).</summary>
        public IReadOnlyList<string> Showcase => showcase.Where(k => k.Length > 0).ToList();
        /// <summary>The player's own records offered for the showcase (online).</summary>
        public int RecordCount => records.Count;
        public bool Busy => busy;
        public string Status => status.text;

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.66f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.92f));
            RectTransform top = UIFactory.Column("CardTop", panel.transform, new Vector2(0, 0.8f), new Vector2(1, 0.96f), new Vector2(56, 0), new Vector2(-32, 0), 6f);
            UIFactory.Row("Heading", top, "PLAYER CARD", SignalTheme.Heading, SignalTheme.Label, 1180, 0, true);
            UIFactory.Row("Hint", top, "How other drivers see you at the meet. Appearance is visual only — it never changes your car, your hitbox or your driving.",
                SignalTheme.Small, SignalTheme.LabelDim, 1180, 30);
            RectTransform names = UIFactory.Rect("Names", top, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            names.sizeDelta = new Vector2(1180, 56);
            nameField = UIFactory.InputField("CardName", names, "Display name", false, 24, 560, 52);
            Place((RectTransform)nameField.transform, 0f);
            pronounsField = UIFactory.InputField("CardPronouns", names, "Pronouns (optional)", false, 24, 560, 52);
            Place((RectTransform)pronounsField.transform, 590f);

            RectTransform a = UIFactory.Column("Who", panel.transform, new Vector2(0, 0.13f), new Vector2(0.5f, 0.79f), new Vector2(56, 0), new Vector2(-12, 0), 4f);
            RectTransform b = UIFactory.Column("Wear", panel.transform, new Vector2(0.5f, 0.13f), new Vector2(1f, 0.79f), new Vector2(12, 0), new Vector2(-32, 0), 4f);

            editing = new Stepper(b, "Editing", 2, i => i == 0 ? "Driver look" : "Card style", 0, 570, 0.34f);
            editing.Changed += ShowSection;
            preset = new Stepper(a, "Start from", PlayerLooks.Presets.Length + 1, i => i == 0 ? "Your look" : $"Look {i}", 0, 570, 0.34f);
            preset.Changed += i =>
            {
                if (loading) return;
                look = PlayerLooks.Copy(i == 0 ? loaded : PlayerLooks.Presets[i - 1]);
                Sync();
            };
            Field(a, "Build", CharacterVocabulary.Builds.Length, i => Words(CharacterVocabulary.Builds[i]), i => look.Build = CharacterVocabulary.Builds[i], () => Idx(CharacterVocabulary.Builds, look.Build));
            Field(a, "Height", Heights.Length, i => $"{Heights[i]:0.00} m", i => look.Height = Heights[i], () => NearestHeight(look.Height));
            Colours(a, "Skin", PlayerLooks.SkinTones, v => look.Skin = v, () => look.Skin);
            Field(a, "Expression", CharacterVocabulary.Faces.Length, i => Words(CharacterVocabulary.Faces[i]), i => look.Face = CharacterVocabulary.Faces[i], () => Idx(CharacterVocabulary.Faces, look.Face));
            Field(a, "Posture", CharacterVocabulary.Postures.Length, i => Words(CharacterVocabulary.Postures[i]), i => look.Posture = CharacterVocabulary.Postures[i], () => Idx(CharacterVocabulary.Postures, look.Posture));
            Field(a, "Hair", CharacterVocabulary.Hair.Length, i => Words(CharacterVocabulary.Hair[i]), i => look.Hair = CharacterVocabulary.Hair[i], () => Idx(CharacterVocabulary.Hair, look.Hair));
            Colours(a, "Hair colour", PlayerLooks.HairColours, v => look.HairColour = v, () => look.HairColour);
            Field(a, "Facial hair", CharacterVocabulary.FacialHair.Length, i => Words(CharacterVocabulary.FacialHair[i]), i => look.FacialHair = CharacterVocabulary.FacialHair[i], () => Idx(CharacterVocabulary.FacialHair, look.FacialHair));
            Field(a, "Headwear", Headwear.Length, i => i == 0 ? "None" : Words(Headwear[i]), i => SetAccessory(Headwear, i), () => AccessoryIndex(Headwear));

            Field(b, "Outfit", CharacterVocabulary.Outfits.Length, i => Words(CharacterVocabulary.Outfits[i]), i => look.Outfit = CharacterVocabulary.Outfits[i], () => Idx(CharacterVocabulary.Outfits, look.Outfit));
            Colours(b, "Outfit colour", PlayerLooks.Colours, v => look.Primary = v, () => look.Primary);
            Colours(b, "Under layer", PlayerLooks.Colours, v => look.Secondary = v, () => look.Secondary);
            Colours(b, "Trim", PlayerLooks.Colours, v => look.Accent = v, () => look.Accent);
            Field(b, "Sleeves", CharacterVocabulary.Sleeves.Length, i => Words(CharacterVocabulary.Sleeves[i]), i => look.Sleeves = CharacterVocabulary.Sleeves[i], () => Idx(CharacterVocabulary.Sleeves, look.Sleeves));
            Field(b, "Lower", CharacterVocabulary.Lowers.Length, i => Words(CharacterVocabulary.Lowers[i]), i => look.Lower = CharacterVocabulary.Lowers[i], () => Idx(CharacterVocabulary.Lowers, look.Lower));
            Colours(b, "Lower colour", PlayerLooks.Colours, v => look.LowerColour = v, () => look.LowerColour);
            Field(b, "Shoes", CharacterVocabulary.Shoes.Length, i => Words(CharacterVocabulary.Shoes[i]), i => look.Shoes = CharacterVocabulary.Shoes[i], () => Idx(CharacterVocabulary.Shoes, look.Shoes));
            Colours(b, "Shoe colour", PlayerLooks.Colours, v => look.ShoeColour = v, () => look.ShoeColour);
            Field(b, "Extra", Extras.Length, i => i == 0 ? "None" : i == Extras.Length - 1 ? "Open coat" : Words(Extras[i]), i => SetAccessory(Extras, i), () => AccessoryIndex(Extras));
            lookRows.Add(preset.Root);
            foreach (var f in fields) lookRows.Add(f.Step.Root);

            // Card style rows (hidden until "Card style" is chosen above): reward items say when they are not owned yet.
            CardStyleCatalogue cat = Cat;
            if (cat != null)
            {
                string Lock(string name, string cosmetic) => cosmetic == null || ownedCosmetics.Contains(cosmetic) ? name : name + "  (locked)";
                bgStep = StyleStep(a, "Background", cat.Backgrounds.Count, i => Lock(cat.Backgrounds[i].Name, cat.Backgrounds[i].CosmeticId), i => style.Background = cat.Backgrounds[i].Id);
                frameStep = StyleStep(a, "Frame", cat.Frames.Count, i => Lock(cat.Frames[i].Name, cat.Frames[i].CosmeticId), i => style.Frame = cat.Frames[i].Id);
                motifStep = StyleStep(a, "Motif", cat.Motifs.Count, i => Lock(cat.Motifs[i].Name, cat.Motifs[i].CosmeticId), i => style.Motif = cat.Motifs[i].Id);
                titleStep = StyleStep(a, "Title", cat.Titles.Count, i => Lock(cat.Titles[i].Name, cat.Titles[i].CosmeticId), i => style.Title = cat.Titles[i].Id);
                layoutStep = StyleStep(b, "Layout", cat.Layouts.Count, i => Lock(cat.Layouts[i].Name, cat.Layouts[i].CosmeticId), i => style.Layout = cat.Layouts[i].Id);
                regionStep = StyleStep(b, "Region", RegionCodes.All.Count + 1, i => i == 0 ? "None" : RegionCodes.All[i - 1], i => style.Region = i == 0 ? "" : RegionCodes.All[i - 1]);
                carStep = StyleStep(b, "Preferred car", 1, i => i == 0 || i > ownedCars.Count ? "None" : CarName(ownedCars[i - 1]), i => style.PreferredCar = i == 0 || i > ownedCars.Count ? "" : ownedCars[i - 1]);
                for (int s = 0; s < showcaseSteps.Length; s++)
                {
                    int slot = s;
                    showcaseSteps[s] = StyleStep(b, $"Showcase {s + 1}", 1, i => i == 0 || i > records.Count ? "None" : $"{records[i - 1].Label} {records[i - 1].Value}",
                        i => showcase[slot] = i == 0 || i > records.Count ? "" : records[i - 1].Key);
                }

            }

            RectTransform bottom = UIFactory.Column("CardBottom", panel.transform, new Vector2(0, 0.02f), new Vector2(1, 0.12f), new Vector2(56, 0), new Vector2(-32, 0), 6f);
            status = UIFactory.Row("CardStatus", bottom, "", SignalTheme.Small, SignalTheme.Label, 1180, 30);
            status.richText = false;
            RectTransform buttons = UIFactory.Rect("Buttons", bottom, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            buttons.sizeDelta = new Vector2(1180, 56);
            Button save = UIFactory.Button("SaveCard", buttons, "Save Card", Save, 300, 52);
            Place((RectTransform)save.transform, 0f);
            Button back = UIFactory.Button("Back", buttons, "Back", () => App.Router.Back(), 300, 52);
            Place((RectTransform)back.transform, 330f);

            var previewGo = new GameObject("CardPreview", typeof(RectTransform), typeof(RawImage));
            var prt = (RectTransform)previewGo.transform;
            prt.SetParent(root, false);
            prt.anchorMin = new Vector2(0.67f, 0.06f);
            prt.anchorMax = new Vector2(0.99f, 0.94f);
            prt.offsetMin = prt.offsetMax = Vector2.zero;
            preview = previewGo.GetComponent<RawImage>();
            preview.color = Color.white;
            cardView = new CardView(root, new Vector2(0.675f, 0.3f), new Vector2(0.985f, 0.7f));
            cardView.Root.gameObject.SetActive(false);
        }

        Stepper StyleStep(Transform col, string label, int count, Func<int, string> format, Action<int> apply)
        {
            var step = new Stepper(col, label, count, format, 0, 570, 0.34f);
            step.Changed += i =>
            {
                if (loading || style == null) return;
                apply(i);
                RefreshCard();
            };
            styleRows.Add(step.Root);
            step.Root.SetActive(false);
            return step;
        }

        bool recordsLoaded;

        /// <summary>The player's own records for the showcase (online; the steppers offer them once they arrive).</summary>
        async void LoadRecords()
        {
            recordsLoaded = false;
            try
            {
                JObject r = await S.Client.Get("/v1/me/records");
                records = ((r?["records"] as JArray) ?? new JArray()).OfType<JObject>()
                    .Select(x => ((string)x["key"] ?? "", (string)x["label"] ?? "", (string)x["value"] ?? "")).ToList();
                recordsLoaded = r != null;
            }
            catch (Exception)
            {
                records = new List<(string, string, string)>();
            }
            SyncStyle();
        }

        static string CarName(string carId) => Content.ContentLibrary.Load()?.Catalogue?.TryCar(carId, out Core.Content.CarDef car) == true ? car.Name : carId;

        /// <summary>"Driver look" (0) or "Card style" (1): which rows and which preview show.</summary>
        public void ShowSection(int index)
        {
            if (editing.Index != index) editing.Set(index);
            foreach (GameObject g in lookRows) g.SetActive(index == 0);
            foreach (GameObject g in styleRows) g.SetActive(index == 1);
            preview.gameObject.SetActive(index == 0);
            cardView.Root.gameObject.SetActive(index == 1);
            if (index == 1) RefreshCard();
        }

        /// <summary>Automation: choose a whole style as the steppers would.</summary>
        public void SetStyle(CardStyle s)
        {
            style = s.Copy();
            SyncStyle();
        }

        void SyncStyle()
        {
            CardStyleCatalogue cat = Cat;
            if (cat == null || style == null || bgStep == null) return;
            loading = true;
            int Of<T>(IReadOnlyList<T> list, Func<T, string> id, string value)
            {
                for (int i = 0; i < list.Count; i++) if (id(list[i]) == value) return i;
                return 0;
            }
            bgStep.Set(Of(cat.Backgrounds, x => x.Id, style.Background));
            frameStep.Set(Of(cat.Frames, x => x.Id, style.Frame));
            motifStep.Set(Of(cat.Motifs, x => x.Id, style.Motif));
            titleStep.Set(Of(cat.Titles, x => x.Id, style.Title));
            layoutStep.Set(Of(cat.Layouts, x => x.Id, style.Layout));
            int r = style.Region.Length == 0 ? 0 : 1 + Math.Max(0, RegionCodes.All.ToList().IndexOf(style.Region));
            regionStep.Set(r);
            carStep.SetCount(ownedCars.Count + 1);
            carStep.Set(style.PreferredCar.Length == 0 ? 0 : 1 + Math.Max(0, ownedCars.IndexOf(style.PreferredCar)));
            for (int s = 0; s < showcaseSteps.Length; s++)
            {
                showcaseSteps[s].SetCount(records.Count + 1);
                showcaseSteps[s].Set(showcase[s].Length == 0 ? 0 : 1 + Math.Max(0, records.FindIndex(r => r.Key == showcase[s])));
            }
            loading = false;
            RefreshCard();
        }

        void RefreshCard()
        {
            CardStyleCatalogue cat = Cat;
            if (cat == null || cardView == null || style == null) return;
            var lines = new List<string>(stats);
            foreach (string key in showcase)
            {
                int i = records.FindIndex(r => r.Key == key);
                if (key.Length > 0 && i >= 0) lines.Add($"Best: {records[i].Label} {records[i].Value}");
            }
            cardView.Show(cat, style, nameField.text, pronounsField.text.Trim(), lines, style.PreferredCar.Length == 0 ? "" : CarName(style.PreferredCar));
        }

        static void Place(RectTransform rt, float x)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
        }

        static string Words(string word) => string.IsNullOrEmpty(word) ? "Usual" : char.ToUpperInvariant(word[0]) + word.Substring(1).Replace('-', ' ');

        static int Idx(string[] set, string value) => Math.Max(0, Array.IndexOf(set, value ?? ""));

        static int NearestHeight(float h)
        {
            int best = 0;
            for (int i = 1; i < Heights.Length; i++) if (Mathf.Abs(Heights[i] - h) < Mathf.Abs(Heights[best] - h)) best = i;
            return best;
        }

        void Field(Transform col, string label, int count, Func<int, string> format, Action<int> apply, Func<int> read)
        {
            var step = new Stepper(col, label, count, format, 0, 570, 0.34f);
            step.Changed += i =>
            {
                if (loading || look == null) return;
                apply(i);
                dirtyPreview = true;
            };
            fields.Add((step, apply, read));
        }

        /// <summary>A palette stepper: a swatch tile in the colour itself (light-edged so dark colours still read) and its number.</summary>
        void Colours(Transform col, string label, string[] palette, Action<string> apply, Func<string> read)
        {
            Stepper step = null;
            Field(col, label, palette.Length, i => $"{i + 1} / {palette.Length}", i => { apply(palette[i]); Tint(step, palette[i]); },
                () => Math.Max(0, Array.FindIndex(palette, c => string.Equals(c, read(), StringComparison.OrdinalIgnoreCase))));
            step = fields[fields.Count - 1].Step;
            step.Value.alignment = TextAlignmentOptions.MidlineRight;
            step.Value.margin = new Vector4(0, 0, 14, 0);
            Image edge = UIFactory.Panel("SwatchEdge", step.Value.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(10, -16), new Vector2(110, 16), new Color(0.78f, 0.77f, 0.74f, 1f));
            edge.raycastTarget = false;
            Image fill = UIFactory.Panel("Swatch", edge.rectTransform, Vector2.zero, Vector2.one, new Vector2(2, 2), new Vector2(-2, -2), Color.white);
            fill.raycastTarget = false;
            swatches[step] = fill;
            colourRows.Add((step, read));
        }

        void Tint(Stepper step, string hex)
        {
            if (step != null && swatches.TryGetValue(step, out Image fill) && ColorUtility.TryParseHtmlString(hex, out Color c)) fill.color = c;
        }

        void SetAccessory(string[] slot, int index)
        {
            look.Accessories = (look.Accessories ?? new List<string>()).Where(x => Array.IndexOf(slot, x) < 0).ToList();
            if (index > 0) look.Accessories.Add(slot[index]);
        }

        int AccessoryIndex(string[] slot)
        {
            foreach (string a in look.Accessories ?? new List<string>())
            {
                int i = Array.IndexOf(slot, a);
                if (i > 0) return i;
            }
            return 0;
        }

        /// <summary>Steppers follow the working look (after loading or choosing a starting look); the preview rebuilds.</summary>
        void Sync()
        {
            loading = true;
            foreach (var f in fields) f.Step.Set(f.Read());
            // The swatch shows the look's actual colour: one outside the palette (an older look) stays until stepped.
            foreach (var c in colourRows) Tint(c.Step, c.Read());
            loading = false;
            dirtyPreview = true;
        }

        public override void OnShow()
        {
            bool hasLook;
            if (Local != null)
            {
                Core.Profiles.LocalProfile profile = Local.Profile;
                nameField.text = profile.DisplayName;
                pronounsField.text = profile.Card?.Pronouns ?? "";
                CharacterLook stored = string.IsNullOrEmpty(profile.Card?.Look) ? null : PlayerLooks.Parse(profile.Card.Look);
                hasLook = stored != null;
                loaded = stored ?? MeetSession.DefaultPlayerLook(profile.DisplayName);
            }
            else if (S == null)
            {
                App.Router.Show(App.SignIn, false);
                return;
            }
            else
            {
                JObject card = S.Me?["card"] as JObject;
                nameField.text = (string)card?["displayName"] ?? S.DisplayName;
                pronounsField.text = (string)card?["pronouns"] ?? "";
                hasLook = S.CardLook != null;
                loaded = S.CardLook ?? MeetSession.DefaultPlayerLook(S.DisplayName);
            }
            loaded.Id = "";
            look = PlayerLooks.Copy(loaded);
            loading = true;
            preset.Set(0);
            loading = false;
            Sync();
            status.text = !hasLook ? "You have the default look — choose a starting look or change anything, then Save Card." : "";
            // The card's style and what this player owns for it.
            CardStyleCatalogue styles = Cat;
            if (Local != null)
            {
                Core.Profiles.LocalProfile profile = Local.Profile;
                style = Core.Profiles.LocalProgression.StyleOf(profile.Card, styles?.Default);
                ownedCosmetics = new HashSet<string>((profile.Cosmetics ?? new List<Core.Profiles.OwnedCosmetic>()).Select(x => x.CosmeticId), StringComparer.Ordinal);
                ownedCars = (profile.Cars ?? new List<Core.Profiles.OwnedCar>()).Select(x => x.ModelId).Distinct().ToList();
                stats = new List<string> { "Local profile", $"Challenges {profile.Challenges?.Count ?? 0}/75" };
            }
            else
            {
                JToken stored = (S.Me?["card"] as JObject)?["style"];
                style = (stored is JObject so ? CardStyle.Parse(so.ToString(Newtonsoft.Json.Formatting.None)) : null) ?? styles?.Default.Copy();
                ownedCosmetics = new HashSet<string>(((S.Me?["cosmeticsOwned"] as JArray) ?? new JArray()).Select(x => (string)x), StringComparer.Ordinal);
                ownedCars = ((S.Me?["ownedCars"] as JArray) ?? new JArray()).Select(x => (string)x["carId"]).Where(x => x != null).Distinct().ToList();
                JToken rank = S.Me?["rank"];
                stats = new List<string>
                {
                    $"Rank {(string)rank?["name"]} · {(int?)rank?["rankPoints"] ?? 0:N0} RP",
                    $"Challenges {((S.Me?["challengesCompleted"] as JArray)?.Count ?? 0)}/75",
                };
            }
            for (int s = 0; s < showcase.Length; s++) showcase[s] = "";
            records = new List<(string, string, string)>();
            if (Local != null)
            {
                // Offline: the Local profile's own records (never mixed with online ones).
                records = Core.Profiles.LocalShowcase.Records(Local.Profile, Content.ContentLibrary.Load()?.Catalogue);
                int n = 0;
                foreach (string k in Local.Profile.Card?.Showcase ?? new List<string>())
                    if (n < showcase.Length) showcase[n++] = k ?? "";
                recordsLoaded = true;
            }
            else
            {
                int n = 0;
                foreach (JToken k in ((S.Me?["card"] as JObject)?["showcase"] as JArray) ?? new JArray())
                    if (n < showcase.Length) showcase[n++] = (string)k ?? "";
                LoadRecords();
            }
            SyncStyle();
            ShowSection(0);
            if (stage == null)
            {
                Rect r = preview.rectTransform.rect;
                float aspect = r.height > 1f ? r.width / r.height : 0.62f;
                stage = new CharacterStage(Mathf.RoundToInt(900f * aspect), 900);
            }
            preview.texture = stage.Texture;
        }

        public override void OnHide()
        {
            stage?.Dispose();
            stage = null;
        }

        public override void Tick()
        {
            cardView?.Tick(Time.unscaledDeltaTime);
            if (stage == null || look == null) return;
            if (dirtyPreview)
            {
                dirtyPreview = false;
                stage.Show(look);
            }
            stage.Update(Time.unscaledDeltaTime);
        }

        /// <summary>Automation: choose a starting look (0 = the current one, 1..8 = the presets) as the stepper would.</summary>
        public void ChooseStart(int index)
        {
            preset.Set(index);
            look = PlayerLooks.Copy(index == 0 ? loaded : PlayerLooks.Presets[index - 1]);
            Sync();
        }

        /// <summary>Automation: set one appearance row by its label (e.g. "Hair", 8) as the stepper would.</summary>
        public bool SetField(string label, int index)
        {
            foreach (var f in fields)
            {
                if (f.Step.Root.name != label) continue;
                f.Step.Set(index);
                f.Apply(f.Step.Index);
                dirtyPreview = true;
                return true;
            }
            return false;
        }

        public void SavePreview(string path) => stage?.SaveTexture(path);

        async void Save()
        {
            if (Local != null && !busy && look != null)
            {
                // Offline: the Local profile's card, validated like the online one and saved atomically.
                Core.Profiles.LocalProgressionResult r = Core.Profiles.LocalProgression.SetCard(Local.Profile, nameField.text,
                    PlayerLooks.Canonical(look), pronounsField.text, style, Cat, Showcase, Content.ContentLibrary.Load()?.Catalogue);
                if (r.Status == Core.Profiles.LocalOperationStatus.AlreadyApplied)
                {
                    status.text = "Nothing changed.";
                    return;
                }
                if (r.Status != Core.Profiles.LocalOperationStatus.Applied)
                {
                    status.text = r.Reason;
                    return;
                }
                if (!Local.Commit(r, out string message))
                {
                    status.text = "The card could not be saved: " + message;
                    return;
                }
                loaded = PlayerLooks.Copy(look);
                App.DisplayName = Local.Profile.DisplayName;
                App.RefreshStrip();
                status.text = "Saved. You look like this at the offline meet.";
                stage?.Play(Emote.Wave);
                return;
            }
            if (busy || S == null || look == null) return;
            busy = true;
            status.text = "Saving…";
            try
            {
                long revision = (long?)(S.Me?["card"] as JObject)?["revision"] ?? 0;
                var payload = new JObject
                {
                    ["displayName"] = nameField.text,
                    ["revision"] = revision,
                    ["pronouns"] = pronounsField.text.Trim(),
                    ["look"] = JObject.Parse(PlayerLooks.Canonical(look)),
                };
                if (style != null) payload["style"] = JObject.Parse(style.Canonical());
                if (recordsLoaded) payload["showcase"] = new JArray(Showcase.Cast<object>().ToArray());
                (int code, JObject body) = await S.Client.Post("/v1/me/card", payload);
                if (code == 409)
                {
                    await S.RefreshMe();
                    status.text = "Your card changed elsewhere — it has been reloaded; apply your edit again.";
                    OnShow();
                    return;
                }
                if (code >= 300)
                {
                    status.text = (string)body?["message"] ?? $"The card could not be saved ({code}).";
                    return;
                }
                await S.RefreshMe();
                loaded = PlayerLooks.Copy(look);
                App.DisplayName = S.DisplayName;
                App.RefreshStrip();
                status.text = "Saved. Other drivers see this look at the meet.";
                stage?.Play(Emote.Wave);
            }
            finally
            {
                busy = false;
            }
        }
    }
}
