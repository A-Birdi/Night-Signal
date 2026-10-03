using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Characters;

namespace NightSignal.Core.Customization
{
    // The driver's reward wardrobe (spec §11 driver appearance; challenge rewards in the driver_clothing and
    // accessory_or_avatar categories: "Clothing/accessories must render on the driver's avatar"). Each item is one garment or
    // accessory with its own construction (builder shape tokens) and colours, locked until its cosmetic is owned. A worn
    // garment replaces the matching part of the player's own outfit; an accessory adds to it. Visual only; never gameplay.

    /// <summary>One reward wardrobe item (customization.json "wardrobe").</summary>
    public sealed class WardrobeItemDef
    {
        public string Id;
        public string Name;
        /// <summary>Where it is worn (<see cref="WardrobeCatalogue.Slots"/>); one item per place, a full suit takes upper and lower.</summary>
        public string Slot;
        /// <summary>Garments: what the item puts on (null = keep the look's own) — vocabulary of <see cref="CharacterVocabulary"/>.</summary>
        public string Outfit, Sleeves, Length, Lower, Shoes;
        /// <summary>
        /// Garments: the colour of the part they cover (#RRGGBB) — the outer garment (upper, full; a full suit's trousers too),
        /// the trousers (lower) or the shoes (feet). Null for accessories.
        /// </summary>
        public string Colour;
        /// <summary>
        /// Upper/full garments: the trim colour (the look's accent — placket, cuffs, collar, pocket flaps) so the garment's
        /// fittings match it; null keeps the look's own trim.
        /// </summary>
        public string Accent;
        /// <summary>The piece's own two colours (#RRGGBB): its stripes, trims, straps — or the accessory itself.</summary>
        public List<string> Gear = new List<string>();
        /// <summary>Builder shape tokens (<see cref="WardrobeCatalogue.Shapes"/>): how it is constructed.</summary>
        public List<string> Shapes = new List<string>();
        /// <summary>Look accessories that occupy the same place: left out while this item is worn.</summary>
        public List<string> Blocks = new List<string>();
        /// <summary>The unlocking cosmetic (a challenge reward).</summary>
        public string CosmeticId;
        /// <summary>What it is and how it is built, for the contact sheet and reviews.</summary>
        public string Description;
    }

    /// <summary>customization.json "wardrobe".</summary>
    public sealed class WardrobeSection
    {
        public string Note;
        public List<WardrobeItemDef> Items = new List<WardrobeItemDef>();
    }

    /// <summary>The validated wardrobe: ownership and fit checks for a look, and the look as it is built.</summary>
    public sealed class WardrobeCatalogue
    {
        public static readonly string[] Slots = { "head", "hands", "upper", "full", "lower", "feet", "neck", "chest", "wrist", "hip", "charm", "bag", "carry", "belt" };

        /// <summary>The shapes the character builder constructs (Runtime CharacterBuilder.WornPieces).</summary>
        public static readonly string[] Shapes =
        {
            "driving-gloves", "crew-cap", "track-stripes", "turn-ups", "knee-panels", "recovery-boots", "storm-hood", "taped-seams",
            "chest-zip", "reflector-cuffs", "button-placket", "pit-vest", "marshal-chevrons", "double-breasted", "knee-pads",
            "chest-pocket", "name-patch", "racing-stripes", "racing-collar", "shoulder-boards", "yoke-block", "hem-toggles",
            "horizon-band", "coat-belt", "survey-ticks", "flat-line", "storm-collar", "lantern-keychain", "route-pin", "woven-cuff",
            "timing-slip-charm", "workshop-satchel", "road-atlas", "travel-scarf", "receiver",
        };

        /// <summary>One-piece outfits: their legs are the garment, so reward trousers would not show under them.</summary>
        public static readonly string[] OnePiece = { "coveralls", "overalls", "jumpsuit-tied" };

        readonly Dictionary<string, WardrobeItemDef> byId = new Dictionary<string, WardrobeItemDef>(StringComparer.Ordinal);

        public IReadOnlyList<WardrobeItemDef> Items { get; private set; } = new List<WardrobeItemDef>();

        public WardrobeItemDef Item(string id) => id != null && byId.TryGetValue(id, out WardrobeItemDef d) ? d : null;

        /// <summary>The items worn in <paramref name="slot"/>, in catalogue order.</summary>
        public IEnumerable<WardrobeItemDef> InSlot(string slot) => Items.Where(x => x.Slot == slot);

        static bool IsGarment(string slot) => slot == "upper" || slot == "full" || slot == "lower" || slot == "feet";

        /// <summary>The places an item occupies (a full suit is both upper and lower).</summary>
        static IEnumerable<string> Places(WardrobeItemDef d) => d.Slot == "full" ? new[] { "upper", "lower" } : new[] { d.Slot };

        internal static WardrobeCatalogue Validate(WardrobeSection section, List<string> errors)
        {
            var cat = new WardrobeCatalogue();
            if (section == null) { errors.Add("wardrobe: section missing"); return cat; }
            var list = section.Items ?? new List<WardrobeItemDef>();
            foreach (WardrobeItemDef d in list)
            {
                if (d == null) { errors.Add("wardrobe: empty item"); continue; }
                string w = $"wardrobe {d.Id}";
                if (!CatalogueIds.IsValid(d.Id)) { errors.Add($"{w}: invalid id"); continue; }
                if (cat.byId.ContainsKey(d.Id)) { errors.Add($"wardrobe: duplicate item {d.Id}"); continue; }
                cat.byId.Add(d.Id, d);
                if (string.IsNullOrWhiteSpace(d.Name)) errors.Add($"{w}: no name");
                if (string.IsNullOrWhiteSpace(d.Description)) errors.Add($"{w}: no description");
                if (d.CosmeticId == null || !d.CosmeticId.StartsWith("COS-", StringComparison.Ordinal)) errors.Add($"{w}: cosmeticId must be a COS- id");
                if (Array.IndexOf(Slots, d.Slot ?? "") < 0) { errors.Add($"{w}: unknown slot {d.Slot}"); continue; }
                if (d.Gear == null || d.Gear.Count != 2 || d.Gear.Any(c => !HexColor.IsCanonical(c))) errors.Add($"{w}: gear must be two upper-case #RRGGBB colours");
                if (d.Shapes == null || d.Shapes.Count == 0) errors.Add($"{w}: no shapes");
                else foreach (string s in d.Shapes) if (Array.IndexOf(Shapes, s ?? "") < 0) errors.Add($"{w}: unknown shape {s}");
                foreach (string b in d.Blocks ?? new List<string>())
                    if (Array.IndexOf(CharacterVocabulary.Accessories, b ?? "") < 0) errors.Add($"{w}: blocks unknown accessory {b}");
                void In(string field, string value, string[] set, bool required)
                {
                    if (value == null) { if (required) errors.Add($"{w}: {field} required for a {d.Slot} item"); return; }
                    if (Array.IndexOf(set, value) < 0) errors.Add($"{w}: {field} '{value}' not in the character vocabulary");
                }
                if (IsGarment(d.Slot))
                {
                    if (!HexColor.IsCanonical(d.Colour)) errors.Add($"{w}: a garment needs its colour (upper-case #RRGGBB)");
                    bool upper = d.Slot == "upper" || d.Slot == "full";
                    In("outfit", d.Outfit, CharacterVocabulary.Outfits, upper);
                    In("sleeves", d.Sleeves, CharacterVocabulary.Sleeves, false);
                    In("length", d.Length, CharacterVocabulary.Lengths, false);
                    In("lower", d.Lower, CharacterVocabulary.Lowers, d.Slot == "lower");
                    In("shoes", d.Shoes, CharacterVocabulary.Shoes, d.Slot == "feet");
                    if (!upper && (d.Outfit != null || d.Sleeves != null || d.Length != null)) errors.Add($"{w}: only upper/full items change the outfit");
                    if (d.Slot != "lower" && d.Slot != "full" && d.Lower != null) errors.Add($"{w}: only lower/full items change the lower garment");
                    if (d.Slot != "feet" && d.Shoes != null) errors.Add($"{w}: only feet items change the shoes");
                    if (d.Slot == "upper" && OnePiece.Contains(d.Outfit)) errors.Add($"{w}: a one-piece outfit is a full item");
                    if (d.Accent != null && (!upper || !HexColor.IsCanonical(d.Accent))) errors.Add($"{w}: accent is an upper-case #RRGGBB trim on an upper/full item");
                }
                else if (d.Colour != null || d.Accent != null || d.Outfit != null || d.Sleeves != null || d.Length != null || d.Lower != null || d.Shoes != null)
                    errors.Add($"{w}: an accessory does not change the outfit (colour/outfit/sleeves/length/lower/shoes)");
            }
            foreach (var dup in list.Where(x => x?.CosmeticId != null).GroupBy(x => x.CosmeticId).Where(g => g.Count() > 1))
                errors.Add($"wardrobe: cosmetic {dup.Key} unlocks more than one item");
            cat.Items = list.Where(x => x != null).ToList();
            return cat;
        }

        /// <summary>
        /// Why the look's wardrobe cannot be saved (empty = it can): unknown items, items not owned yet (named), two items in one
        /// place, reward trousers under a one-piece outfit.
        /// </summary>
        public List<string> Problems(CharacterLook look, Func<string, bool> ownsCosmetic)
        {
            var bad = new List<string>();
            if (look?.Wardrobe == null || look.Wardrobe.Count == 0) return bad;
            var taken = new Dictionary<string, string>(StringComparer.Ordinal);
            string outfit = look.Outfit;
            WardrobeItemDef lower = null;
            bool suit = false;
            foreach (string id in look.Wardrobe)
            {
                WardrobeItemDef d = Item(id);
                if (d == null) { bad.Add($"Unknown wardrobe item {id}."); continue; }
                if (d.CosmeticId != null && (ownsCosmetic == null || !ownsCosmetic(d.CosmeticId))) bad.Add($"Not owned yet: {d.Name}.");
                foreach (string place in Places(d))
                {
                    if (taken.TryGetValue(place, out string other)) bad.Add($"{d.Name} and {other} are worn in the same place.");
                    else taken[place] = d.Name;
                }
                if (d.Slot == "upper" || d.Slot == "full") outfit = d.Outfit ?? outfit;
                if (d.Slot == "lower") lower = d;
                if (d.Slot == "full") suit = true;
            }
            if (lower != null && !suit && OnePiece.Contains(outfit))
                bad.Add($"{lower.Name} would be hidden under a one-piece outfit; choose another outfit.");
            return bad;
        }

        /// <summary>
        /// The look as it is built: worn garments replace the outfit, sleeves, length, trousers or shoes and their colours;
        /// accessories an item blocks are left out; <see cref="CharacterLook.Worn"/> lists the pieces in slot order with their
        /// colours. Unknown ids are skipped (a look from a newer catalogue still builds). The look itself is not changed.
        /// </summary>
        public CharacterLook Apply(CharacterLook look)
        {
            if (look?.Wardrobe == null || look.Wardrobe.Count == 0) return look;
            CharacterLook e = PlayerLooks.Copy(look);
            e.Id = look.Id;
            e.Worn = new List<WornPiece>();
            var worn = look.Wardrobe.Select(Item).Where(d => d != null).Distinct()
                .OrderBy(d => Array.IndexOf(Slots, d.Slot)).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
            var blocked = new HashSet<string>(worn.SelectMany(d => d.Blocks ?? new List<string>()), StringComparer.Ordinal);
            e.Accessories = (e.Accessories ?? new List<string>()).Where(a => !blocked.Contains(a)).ToList();
            foreach (WardrobeItemDef d in worn)
            {
                switch (d.Slot)
                {
                    case "upper":
                    case "full":
                        e.Outfit = d.Outfit ?? e.Outfit;
                        e.Sleeves = d.Sleeves ?? e.Sleeves;
                        e.Length = d.Length ?? "";
                        e.Primary = d.Colour;
                        e.Accent = d.Accent ?? e.Accent;
                        if (d.Slot == "full")
                        {
                            e.Lower = d.Lower ?? "trousers";
                            e.LowerColour = d.Colour;
                        }
                        break;
                    case "lower":
                        e.Lower = d.Lower ?? e.Lower;
                        e.LowerColour = d.Colour;
                        break;
                    case "feet":
                        e.Shoes = d.Shoes ?? e.Shoes;
                        e.ShoeColour = d.Colour;
                        break;
                }
                e.Worn.Add(new WornPiece { Item = d.Id, Shapes = new List<string>(d.Shapes), ColourA = d.Gear[0], ColourB = d.Gear[1] });
            }
            return e;
        }
    }
}
