using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace NightSignal.Core.Customization
{
    // The Player Card's public styling (spec §11): background, frame, motif, title and layout from customization.json's
    // "card" section — free defaults for everyone plus the fifteen card_customization challenge rewards, each locked until
    // owned — and an optional self-selected region (an ISO 3166-1 alpha-2 code, shown as a code badge) and preferred car.
    // Visual only; never a simulation input.

    public sealed class CardBackgroundDef
    {
        public string Id;
        public string Name;
        /// <summary>plain | gradient | stripes | grid | rain-chart | dyno-trace</summary>
        public string Pattern;
        /// <summary>Two colours (upper-case #RRGGBB): the ground and the pattern.</summary>
        public List<string> Colors = new List<string>();
        public bool Animated;
        /// <summary>Unlocking cosmetic (cosmetics.json category "card_customization"), or null = free.</summary>
        public string CosmeticId;
    }

    public sealed class CardFrameDef
    {
        public string Id;
        public string Name;
        /// <summary>thin | double | columns | nameplate | balance-ticks | balance-point</summary>
        public string Style;
        public string Color;
        public string CosmeticId;
    }

    public sealed class CardMotifDef
    {
        public string Id;
        public string Name;
        /// <summary>none | lantern | bars | ladder | dial | three-drive</summary>
        public string Glyph;
        public string Color;
        public string CosmeticId;
    }

    public sealed class CardTitleDef
    {
        public string Id;
        public string Name;
        /// <summary>The line shown under the name ("" = none).</summary>
        public string Text;
        public string CosmeticId;
    }

    public sealed class CardLayoutDef
    {
        public string Id;
        public string Name;
        /// <summary>standard | two-state | twin | sectors | passport</summary>
        public string Layout;
        public string CosmeticId;
    }

    /// <summary>
    /// The card's avatar: an original emblem drawn beside the name (the free ones, and the seven accessory_or_avatar
    /// challenge rewards — spec "avatar icons are original art … not a generic icon renamed fifteen times").
    /// </summary>
    public sealed class CardAvatarDef
    {
        public string Id;
        public string Name;
        /// <summary>The drawing (<see cref="CardStyleCatalogue.AvatarArts"/>); each is its own composition.</summary>
        public string Art;
        /// <summary>Three colours (upper-case #RRGGBB): ground, main ink, highlight.</summary>
        public List<string> Colors = new List<string>();
        public string CosmeticId;
        /// <summary>What the drawing shows, for reviews.</summary>
        public string Description;
    }

    /// <summary>customization.json "card": the style items and the style everyone starts with.</summary>
    public sealed class CardStyleSection
    {
        public string Note;
        public List<CardBackgroundDef> Backgrounds = new List<CardBackgroundDef>();
        public List<CardFrameDef> Frames = new List<CardFrameDef>();
        public List<CardMotifDef> Motifs = new List<CardMotifDef>();
        public List<CardTitleDef> Titles = new List<CardTitleDef>();
        public List<CardLayoutDef> Layouts = new List<CardLayoutDef>();
        public List<CardAvatarDef> Avatars = new List<CardAvatarDef>();
        public CardStyle Default = new CardStyle();
    }

    /// <summary>A card's chosen style (ids into the catalogue; "" region and car = not shown).</summary>
    public sealed class CardStyle
    {
        public string Background = "";
        public string Frame = "";
        public string Motif = "";
        public string Title = "";
        public string Layout = "";
        /// <summary>ISO 3166-1 alpha-2 region code, self-selected and optional ("" = none).</summary>
        public string Region = "";
        /// <summary>A car model the player owns ("" = none).</summary>
        public string PreferredCar = "";
        /// <summary>The avatar emblem ("" = the catalogue default); left out of the stored form when unset.</summary>
        public string Avatar = "";

        public bool ShouldSerializeAvatar() => !string.IsNullOrEmpty(Avatar);

        static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            MissingMemberHandling = MissingMemberHandling.Error,
            NullValueHandling = NullValueHandling.Include,
        };

        public CardStyle Copy() => (CardStyle)MemberwiseClone();

        /// <summary>The stored and wire form: fixed member order, camelCase, compact.</summary>
        public string Canonical() => JsonConvert.SerializeObject(this, Formatting.None, Json);

        /// <summary>Reads a stored or submitted style (null on malformed JSON or unknown members); absent members are "".</summary>
        public static CardStyle Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                CardStyle s = JsonConvert.DeserializeObject<CardStyle>(json, Json);
                if (s == null) return null;
                s.Background = s.Background ?? "";
                s.Frame = s.Frame ?? "";
                s.Motif = s.Motif ?? "";
                s.Title = s.Title ?? "";
                s.Layout = s.Layout ?? "";
                s.Region = s.Region ?? "";
                s.PreferredCar = s.PreferredCar ?? "";
                s.Avatar = s.Avatar ?? "";
                return s;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public bool ContentEquals(CardStyle o) => o != null && Canonical() == o.Canonical();
    }

    /// <summary>The validated card section of the catalogue.</summary>
    public sealed class CardStyleCatalogue
    {
        public static readonly string[] Patterns = { "plain", "gradient", "stripes", "grid", "rain-chart", "dyno-trace" };
        public static readonly string[] FrameStyles = { "thin", "double", "columns", "nameplate", "balance-ticks", "balance-point" };
        public static readonly string[] Glyphs = { "none", "lantern", "bars", "ladder", "dial", "three-drive" };
        public static readonly string[] LayoutKinds = { "standard", "two-state", "twin", "sectors", "passport" };
        /// <summary>The avatar drawings (Runtime CardView.DrawAvatar): two free, seven reward compositions.</summary>
        public static readonly string[] AvatarArts =
            { "initial", "night-road", "cherry-branch", "six-region", "ghostline", "radio-dial", "road-crest", "mosaic", "dawn-horizon" };
        public const int MaxTitleLength = 32;

        public IReadOnlyList<CardBackgroundDef> Backgrounds { get; private set; } = new List<CardBackgroundDef>();
        public IReadOnlyList<CardFrameDef> Frames { get; private set; } = new List<CardFrameDef>();
        public IReadOnlyList<CardMotifDef> Motifs { get; private set; } = new List<CardMotifDef>();
        public IReadOnlyList<CardTitleDef> Titles { get; private set; } = new List<CardTitleDef>();
        public IReadOnlyList<CardLayoutDef> Layouts { get; private set; } = new List<CardLayoutDef>();
        public IReadOnlyList<CardAvatarDef> Avatars { get; private set; } = new List<CardAvatarDef>();
        /// <summary>The style a new card starts with (every item free).</summary>
        public CardStyle Default { get; private set; } = new CardStyle();

        public CardBackgroundDef Background(string id) => Backgrounds.FirstOrDefault(x => x.Id == id);
        public CardFrameDef Frame(string id) => Frames.FirstOrDefault(x => x.Id == id);
        public CardMotifDef Motif(string id) => Motifs.FirstOrDefault(x => x.Id == id);
        public CardTitleDef Title(string id) => Titles.FirstOrDefault(x => x.Id == id);
        public CardLayoutDef Layout(string id) => Layouts.FirstOrDefault(x => x.Id == id);
        public CardAvatarDef Avatar(string id) => Avatars.FirstOrDefault(x => x.Id == id);
        /// <summary>The avatar a style shows: its own, or the default when unset or unknown.</summary>
        public CardAvatarDef AvatarOf(CardStyle style) => Avatar(style?.Avatar) ?? Avatar(Default.Avatar);

        /// <summary>Every item's unlocking cosmetic (item id → cosmetic id), across all six kinds.</summary>
        public IEnumerable<(string Kind, string Id, string Name, string CosmeticId)> Items() =>
            Backgrounds.Select(x => ("background", x.Id, x.Name, x.CosmeticId))
                .Concat(Frames.Select(x => ("frame", x.Id, x.Name, x.CosmeticId)))
                .Concat(Motifs.Select(x => ("motif", x.Id, x.Name, x.CosmeticId)))
                .Concat(Titles.Select(x => ("title", x.Id, x.Name, x.CosmeticId)))
                .Concat(Layouts.Select(x => ("layout", x.Id, x.Name, x.CosmeticId)))
                .Concat(Avatars.Select(x => ("avatar", x.Id, x.Name, x.CosmeticId)));

        internal static CardStyleCatalogue Validate(CardStyleSection section, List<string> errors)
        {
            var cat = new CardStyleCatalogue();
            if (section == null) { errors.Add("card: section missing"); return cat; }
            void Common(string kind, string id, string name, string cosmetic, HashSet<string> seen)
            {
                if (!CatalogueIds.IsValid(id)) errors.Add($"card {kind} {id}: invalid id");
                else if (!seen.Add(id)) errors.Add($"card: duplicate {kind} {id}");
                if (string.IsNullOrWhiteSpace(name)) errors.Add($"card {kind} {id}: no name");
                if (cosmetic != null && !cosmetic.StartsWith("COS-", StringComparison.Ordinal)) errors.Add($"card {kind} {id}: cosmeticId must be null or a COS- id");
            }
            void Colour(string kind, string id, string c)
            {
                if (!HexColor.IsCanonical(c)) errors.Add($"card {kind} {id}: colour must be upper-case #RRGGBB");
            }
            var seenB = new HashSet<string>(StringComparer.Ordinal);
            foreach (CardBackgroundDef b in section.Backgrounds ?? new List<CardBackgroundDef>())
            {
                if (b == null) { errors.Add("card: empty background"); continue; }
                Common("background", b.Id, b.Name, b.CosmeticId, seenB);
                if (!Patterns.Contains(b.Pattern ?? "")) errors.Add($"card background {b.Id}: unknown pattern {b.Pattern}");
                if (b.Colors == null || b.Colors.Count != 2) errors.Add($"card background {b.Id}: two colours");
                else foreach (string c in b.Colors) Colour("background", b.Id, c);
            }
            var seenF = new HashSet<string>(StringComparer.Ordinal);
            foreach (CardFrameDef f in section.Frames ?? new List<CardFrameDef>())
            {
                if (f == null) { errors.Add("card: empty frame"); continue; }
                Common("frame", f.Id, f.Name, f.CosmeticId, seenF);
                if (!FrameStyles.Contains(f.Style ?? "")) errors.Add($"card frame {f.Id}: unknown style {f.Style}");
                Colour("frame", f.Id, f.Color);
            }
            var seenM = new HashSet<string>(StringComparer.Ordinal);
            foreach (CardMotifDef m in section.Motifs ?? new List<CardMotifDef>())
            {
                if (m == null) { errors.Add("card: empty motif"); continue; }
                Common("motif", m.Id, m.Name, m.CosmeticId, seenM);
                if (!Glyphs.Contains(m.Glyph ?? "")) errors.Add($"card motif {m.Id}: unknown glyph {m.Glyph}");
                if (m.Glyph != "none") Colour("motif", m.Id, m.Color);
            }
            var seenT = new HashSet<string>(StringComparer.Ordinal);
            foreach (CardTitleDef t in section.Titles ?? new List<CardTitleDef>())
            {
                if (t == null) { errors.Add("card: empty title"); continue; }
                Common("title", t.Id, t.Name, t.CosmeticId, seenT);
                string text = t.Text ?? "";
                if (text.Length > MaxTitleLength || text.Any(ch => char.IsControl(ch) || ch == '<' || ch == '>' || ch == '{' || ch == '}'))
                    errors.Add($"card title {t.Id}: text is up to {MaxTitleLength} plain characters");
            }
            var seenL = new HashSet<string>(StringComparer.Ordinal);
            foreach (CardLayoutDef l in section.Layouts ?? new List<CardLayoutDef>())
            {
                if (l == null) { errors.Add("card: empty layout"); continue; }
                Common("layout", l.Id, l.Name, l.CosmeticId, seenL);
                if (!LayoutKinds.Contains(l.Layout ?? "")) errors.Add($"card layout {l.Id}: unknown layout {l.Layout}");
            }
            var seenA = new HashSet<string>(StringComparer.Ordinal);
            foreach (CardAvatarDef av in section.Avatars ?? new List<CardAvatarDef>())
            {
                if (av == null) { errors.Add("card: empty avatar"); continue; }
                Common("avatar", av.Id, av.Name, av.CosmeticId, seenA);
                if (!AvatarArts.Contains(av.Art ?? "")) errors.Add($"card avatar {av.Id}: unknown art {av.Art}");
                if (string.IsNullOrWhiteSpace(av.Description)) errors.Add($"card avatar {av.Id}: no description");
                if (av.Colors == null || av.Colors.Count != 3) errors.Add($"card avatar {av.Id}: three colours");
                else foreach (string c in av.Colors) Colour("avatar", av.Id, c);
            }
            var arts = (section.Avatars ?? new List<CardAvatarDef>()).Where(x => x != null).GroupBy(x => x.Art).Where(g => g.Count() > 1);
            foreach (var g in arts) errors.Add($"card: avatar art {g.Key} is used by more than one avatar (each is its own drawing)");
            cat.Backgrounds = section.Backgrounds ?? new List<CardBackgroundDef>();
            cat.Frames = section.Frames ?? new List<CardFrameDef>();
            cat.Motifs = section.Motifs ?? new List<CardMotifDef>();
            cat.Titles = section.Titles ?? new List<CardTitleDef>();
            cat.Layouts = section.Layouts ?? new List<CardLayoutDef>();
            cat.Avatars = section.Avatars ?? new List<CardAvatarDef>();
            var cosmetics = cat.Items().Where(x => x.CosmeticId != null).GroupBy(x => x.CosmeticId).Where(g => g.Count() > 1);
            foreach (var dup in cosmetics) errors.Add($"card: cosmetic {dup.Key} unlocks more than one item");
            CardStyle d = section.Default ?? new CardStyle();
            cat.Default = d;
            // The default style must be complete and free.
            bool Free<T>(T def, Func<T, string> cosmetic) where T : class => def != null && cosmetic(def) == null;
            if (!Free(cat.Background(d.Background), x => x.CosmeticId)) errors.Add("card default: background must be a free item");
            if (!Free(cat.Frame(d.Frame), x => x.CosmeticId)) errors.Add("card default: frame must be a free item");
            if (!Free(cat.Motif(d.Motif), x => x.CosmeticId)) errors.Add("card default: motif must be a free item");
            if (!Free(cat.Title(d.Title), x => x.CosmeticId)) errors.Add("card default: title must be a free item");
            if (!Free(cat.Layout(d.Layout), x => x.CosmeticId)) errors.Add("card default: layout must be a free item");
            if (!Free(cat.Avatar(d.Avatar), x => x.CosmeticId)) errors.Add("card default: avatar must be a free item");
            if (!string.IsNullOrEmpty(d.Region) || !string.IsNullOrEmpty(d.PreferredCar)) errors.Add("card default: no region or car");
            return cat;
        }

        /// <summary>
        /// Why <paramref name="style"/> cannot be saved (empty = it can): unknown items, items not owned yet (named), an
        /// unknown region code, or a preferred car the player does not own.
        /// </summary>
        public List<string> Problems(CardStyle style, Func<string, bool> ownsCosmetic, Func<string, bool> ownsCar)
        {
            var bad = new List<string>();
            if (style == null) { bad.Add("No card style."); return bad; }
            void Item(string kind, string id, string name, string cosmetic, bool found)
            {
                if (!found) bad.Add($"Unknown card {kind} {id}.");
                else if (cosmetic != null && (ownsCosmetic == null || !ownsCosmetic(cosmetic))) bad.Add($"Not owned yet: {name}.");
            }
            CardBackgroundDef b = Background(style.Background);
            Item("background", style.Background, b?.Name, b?.CosmeticId, b != null);
            CardFrameDef f = Frame(style.Frame);
            Item("frame", style.Frame, f?.Name, f?.CosmeticId, f != null);
            CardMotifDef m = Motif(style.Motif);
            Item("motif", style.Motif, m?.Name, m?.CosmeticId, m != null);
            CardTitleDef t = Title(style.Title);
            Item("title", style.Title, t?.Name, t?.CosmeticId, t != null);
            CardLayoutDef l = Layout(style.Layout);
            Item("layout", style.Layout, l?.Name, l?.CosmeticId, l != null);
            if (!string.IsNullOrEmpty(style.Avatar))
            {
                CardAvatarDef av = Avatar(style.Avatar);
                Item("avatar", style.Avatar, av?.Name, av?.CosmeticId, av != null);
            }
            if (style.Region.Length > 0 && !RegionCodes.IsValid(style.Region)) bad.Add($"Unknown region {style.Region}.");
            if (style.PreferredCar.Length > 0 && (ownsCar == null || !ownsCar(style.PreferredCar))) bad.Add("The preferred car must be one you own.");
            return bad;
        }
    }

    /// <summary>ISO 3166-1 alpha-2 codes: the self-selected region a card may show.</summary>
    public static class RegionCodes
    {
        public static readonly IReadOnlyList<string> All = (
            "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC " +
            "CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE " +
            "GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM " +
            "KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ " +
            "NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH " +
            "SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG " +
            "VI VN VU WF WS YE YT ZA ZM ZW").Split(' ');

        static readonly HashSet<string> set = new HashSet<string>(All, StringComparer.Ordinal);

        public static bool IsValid(string code) => code != null && set.Contains(code);
    }
}
