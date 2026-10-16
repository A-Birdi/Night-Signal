using NightSignal.Characters;
using NightSignal.Core.Content;
using NightSignal.Core.Customization;
using NightSignal.Core.Profiles;

namespace NightSignal.CoreTests;

/// <summary>
/// The driver's reward wardrobe (customization.json "wardrobe"; spec §11 and the challenge reward rule "Clothing/accessories
/// must render on the driver's avatar"): every driver_clothing reward and every worn accessory_or_avatar reward is one item
/// with its own construction, locked until owned, one per place on the body; a worn garment replaces that part of the
/// player's outfit. Uses the real content and customization.json.
/// </summary>
public sealed class WardrobeTests
{
    static readonly Lazy<CustomizationCatalogue> customization = new(() => CustomizationCatalogue.Load(
        File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", CustomizationCatalogue.FileName))));
    static WardrobeCatalogue Wardrobe => customization.Value.Wardrobe;

    static readonly Lazy<string> json = new(() =>
        File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", CustomizationCatalogue.FileName)));

    static string Mutated(Action<Newtonsoft.Json.Linq.JObject> change)
    {
        var o = Newtonsoft.Json.Linq.JObject.Parse(json.Value);
        change(o);
        return o.ToString();
    }

    static Newtonsoft.Json.Linq.JObject Item(Newtonsoft.Json.Linq.JObject root, string id) =>
        (Newtonsoft.Json.Linq.JObject)root["wardrobe"]!["items"]!.First(i => (string?)i["id"] == id);

    static Newtonsoft.Json.Linq.JObject Avatar(Newtonsoft.Json.Linq.JObject root, string id) =>
        (Newtonsoft.Json.Linq.JObject)root["card"]!["avatars"]!.First(i => (string?)i["id"] == id);

    [Fact]
    public void Validation_RefusesMalformedWardrobeItemsAndAvatars_WithExactErrors()
    {
        void Expect(string mutated, params string[] errors)
        {
            Assert.False(CustomizationCatalogue.TryLoad(mutated, out CustomizationCatalogue c, out List<string> e));
            Assert.Null(c);
            Assert.Equal(errors, e);
        }
        Expect(Mutated(o => Item(o, "tea-hour-gloves")["slot"] = "ankle"), "wardrobe tea-hour-gloves: unknown slot ankle");
        Expect(Mutated(o => ((Newtonsoft.Json.Linq.JArray)Item(o, "tea-hour-gloves")["shapes"]!).Add("rocket")), "wardrobe tea-hour-gloves: unknown shape rocket");
        Expect(Mutated(o => Item(o, "tea-hour-gloves")["gear"]![0] = "#b88a5a"), "wardrobe tea-hour-gloves: gear must be two upper-case #RRGGBB colours");
        Expect(Mutated(o => Item(o, "signal-track-jacket").Remove("colour")), "wardrobe signal-track-jacket: a garment needs its colour (upper-case #RRGGBB)");
        Expect(Mutated(o => Item(o, "signal-track-jacket")["outfit"] = "coveralls"), "wardrobe signal-track-jacket: a one-piece outfit is a full item");
        Expect(Mutated(o => Item(o, "mizuhana-trousers")["accent"] = "#FFFFFF"),
            "wardrobe mizuhana-trousers: accent is an upper-case #RRGGBB trim on an upper/full item");
        Expect(Mutated(o => Item(o, "route-pin-badge")["outfit"] = "coat"),
            "wardrobe route-pin-badge: an accessory does not change the outfit (colour/outfit/sleeves/length/lower/shoes)");
        Expect(Mutated(o => ((Newtonsoft.Json.Linq.JArray)Item(o, "route-pin-badge")["blocks"]!).Add("monocle")), "wardrobe route-pin-badge: blocks unknown accessory monocle");
        Expect(Mutated(o => Item(o, "route-pin-badge")["cosmeticId"] = "COS-CH63"), "wardrobe: cosmetic COS-CH63 unlocks more than one item");
        Expect(Mutated(o => Item(o, "route-pin-badge")["cosmeticId"] = "COS-CH46"), "Cosmetic COS-CH46 unlocks both a card item and a wardrobe item");
        // Card avatars: a known drawing, each used once, three colours, and the default free.
        Expect(Mutated(o => Avatar(o, "initial")["art"] = "comet"), "card avatar initial: unknown art comet");
        Expect(Mutated(o => Avatar(o, "night-road")["art"] = "initial"), "card: avatar art initial is used by more than one avatar (each is its own drawing)");
        Expect(Mutated(o => ((Newtonsoft.Json.Linq.JArray)Avatar(o, "initial")["colors"]!).RemoveAt(2)), "card avatar initial: three colours");
        Expect(Mutated(o => o["card"]!["default"]!["avatar"] = "ghostline"), "card default: avatar must be a free item");
        // The real document is clean.
        Assert.True(CustomizationCatalogue.TryLoad(json.Value, out _, out List<string> none), string.Join("; ", none));
    }

    static CharacterLook Base()
    {
        CharacterLook l = PlayerLooks.Copy(PlayerLooks.Presets[0]);
        l.Accessories = new List<string> { "scarf", "watch" };
        return l;
    }

    static CharacterLook Wearing(params string[] items)
    {
        CharacterLook l = Base();
        l.Wardrobe = items.ToList();
        return l;
    }

    [Fact]
    public void EveryClothingReward_AndEveryWornAccessoryReward_IsExactlyOneItem()
    {
        ContentCatalogue content = TestContent.Catalogue;
        var avatars = customization.Value.Card.Avatars.Where(a => a.CosmeticId != null).Select(a => a.CosmeticId).ToHashSet();
        var want = content.Cosmetics.Where(c => c.Category == "driver_clothing" || (c.Category == "accessory_or_avatar" && !avatars.Contains(c.Id)))
            .Select(c => c.Id).OrderBy(x => x).ToList();
        Assert.Equal(15 + 8, want.Count);
        Assert.Equal(want, Wardrobe.Items.Select(i => i.CosmeticId).OrderBy(x => x));
        // Every reward cosmetic of the three wearable categories has a definition, and nothing points at the wrong category.
        Assert.Empty(customization.Value.ValidateAgainst(content));
        foreach (WardrobeItemDef d in Wardrobe.Items)
        {
            CosmeticDef cos = content.Cosmetics.Single(c => c.Id == d.CosmeticId);
            Assert.Equal(cos.Name, d.Name); // the catalogue's own name, not a renamed stand-in
        }
    }

    [Fact]
    public void EachItem_IsItsOwnConstruction()
    {
        // No two items share a shape set (a hue shift and a new name are not a new item), and every shape the builder
        // knows is used by some item.
        var sets = Wardrobe.Items.Select(i => string.Join("+", i.Shapes.OrderBy(x => x))).ToList();
        Assert.Equal(sets.Count, sets.Distinct().Count());
        Assert.Equal(WardrobeCatalogue.Shapes.OrderBy(x => x), Wardrobe.Items.SelectMany(i => i.Shapes).Distinct().OrderBy(x => x));
        // Garments of the same cut still differ in construction and colour.
        foreach (var cut in Wardrobe.Items.Where(i => i.Outfit != null).GroupBy(i => i.Outfit))
            Assert.Equal(cut.Count(), cut.Select(i => i.Colour).Distinct().Count());
        Assert.Equal(15, Wardrobe.Items.Count(i => i.Slot is "upper" or "full" or "lower" or "feet" or "head" or "hands"));
    }

    [Fact]
    public void Problems_NameLockedItems_SharedPlaces_AndHiddenTrousers()
    {
        Assert.Empty(Wardrobe.Problems(Base(), _ => false));
        Assert.Equal(new[] { "Not owned yet: Signal Track Jacket." }, Wardrobe.Problems(Wearing("signal-track-jacket"), _ => false));
        Assert.Empty(Wardrobe.Problems(Wearing("signal-track-jacket"), id => id == "COS-CH33"));
        Assert.Equal(new[] { "Unknown wardrobe item no-such-coat." }, Wardrobe.Problems(Wearing("no-such-coat"), _ => true));
        Assert.Equal(new[] { "Harbour Marshal Coat and Signal Track Jacket are worn in the same place." },
            Wardrobe.Problems(Wearing("signal-track-jacket", "harbour-marshal-coat"), _ => true));
        // A full suit is both the upper and the lower garment.
        Assert.Single(Wardrobe.Problems(Wearing("datum-racing-suit", "mizuhana-trousers"), _ => true));
        Assert.Single(Wardrobe.Problems(Wearing("reservoir-pit-vest", "quarry-overalls"), _ => true));
        // Reward trousers under the player's own one-piece outfit would not show.
        CharacterLook overalls = Wearing("mizuhana-trousers");
        overalls.Outfit = "coveralls";
        Assert.Equal(new[] { "Mizuhana Canvas Trousers would be hidden under a one-piece outfit; choose another outfit." }, Wardrobe.Problems(overalls, _ => true));
        overalls.Wardrobe.Add("rainline-jacket"); // a reward jacket replaces the coveralls: the trousers show
        Assert.Empty(Wardrobe.Problems(overalls, _ => true));
        // Everything that fits together at once: one garment per place plus all eight accessories.
        CharacterLook all = Wearing("cedar-crew-cap", "tea-hour-gloves", "red-horizon-coat", "mizuhana-trousers", "recovery-boots", "highland-scarf",
            "route-pin-badge", "woven-wrist-cuff", "cedar-lantern-keys", "timing-slip-charm", "workshop-satchel", "road-atlas", "old-frequency-radio");
        Assert.Empty(Wardrobe.Problems(all, _ => true));
        Assert.Empty(PlayerLooks.Problems(all));
    }

    [Fact]
    public void Apply_ReplacesTheCoveredParts_KeepsTheRest_AndLeavesTheLookAlone()
    {
        CharacterLook own = Base();
        Assert.Same(own, Wardrobe.Apply(own)); // nothing worn: built as it is

        CharacterLook look = Wearing("harbour-marshal-coat", "recovery-boots", "highland-scarf");
        string before = PlayerLooks.Canonical(look);
        CharacterLook e = Wardrobe.Apply(look);
        Assert.Equal(before, PlayerLooks.Canonical(look));
        Assert.Equal(("coat", "long", "knee", "#1C2A44"), (e.Outfit, e.Sleeves, e.Length, e.Primary));
        Assert.Equal((look.Secondary, look.Lower, look.LowerColour), (e.Secondary, e.Lower, e.LowerColour));
        Assert.Equal("#F2C230", e.Accent); // the coat's own trim on its placket and cuffs
        Assert.Equal(("boots", "#3A2A1E"), (e.Shoes, e.ShoeColour));
        // The travel scarf takes the place of the plain scarf; the watch stays.
        Assert.Equal(new[] { "watch" }, e.Accessories);
        // Pieces in slot order, each with its own two colours.
        Assert.Equal(new[] { "harbour-marshal-coat", "recovery-boots", "highland-scarf" }, e.Worn.Select(w => w.Item));
        Assert.Equal(("#F2C230", "#C8CCD0"), (e.Worn[0].ColourA, e.Worn[0].ColourB));
        Assert.Equal(new[] { "marshal-chevrons", "double-breasted" }, e.Worn[0].Shapes);

        CharacterLook suit = Wardrobe.Apply(Wearing("surveyor-line-suit"));
        Assert.Equal(("uniform", "trousers", "#B79A62", "#B79A62"), (suit.Outfit, suit.Lower, suit.Primary, suit.LowerColour));
        Assert.Empty(CharacterVocabulary.Check(suit)); // the built look stays inside the builder's vocabulary

        // Every item, worn alone, builds a look the builder understands.
        foreach (WardrobeItemDef d in Wardrobe.Items)
        {
            CharacterLook one = Wardrobe.Apply(Wearing(d.Id));
            Assert.Empty(CharacterVocabulary.Check(one));
            Assert.Single(one.Worn);
        }
    }

    [Fact]
    public void TheStoredLook_ListsTheWardrobeOnlyWhenWorn()
    {
        CharacterLook plain = Base();
        Assert.DoesNotContain("wardrobe", PlayerLooks.Canonical(plain));
        CharacterLook dressed = Wearing("woven-wrist-cuff", "cedar-crew-cap");
        string stored = PlayerLooks.Canonical(dressed);
        Assert.Contains("\"wardrobe\":[\"woven-wrist-cuff\",\"cedar-crew-cap\"]", stored);
        Assert.DoesNotContain("worn", stored); // the resolved pieces are never stored or sent
        Assert.Equal(stored, PlayerLooks.Canonical(PlayerLooks.Parse(stored)!));
        Assert.True(stored.Length < PlayerLooks.MaxJsonLength);
        Assert.Contains("a wardrobe item is listed twice", PlayerLooks.Problems(Wearing("road-atlas", "road-atlas")));
        Assert.Contains("a wardrobe item id is malformed", PlayerLooks.Problems(Wearing("Road Atlas!")));
        // Even a look listing every item (more than fit at once) stays inside the stored size; the count limit refuses it.
        CharacterLook every = Wearing(Wardrobe.Items.Select(i => i.Id).ToArray());
        Assert.True(PlayerLooks.Canonical(every).Length < PlayerLooks.MaxJsonLength);
        Assert.Contains($"at most {PlayerLooks.MaxWardrobe} wardrobe items", PlayerLooks.Problems(every));
        Assert.True(WardrobeCatalogue.Slots.Length - 1 <= PlayerLooks.MaxWardrobe, "everything that fits at once is under the limit");
    }

    [Fact]
    public void LocalCard_WearsOnlyOwnedItems_AndKeepsTheAvatar()
    {
        LocalProfile p = LocalProgressionTests.NewProfile("Robin");
        string look = PlayerLooks.Canonical(Wearing("signal-track-jacket", "road-atlas"));
        LocalProgressionResult refused = LocalProgression.SetCard(p, "Robin", look, "", null, null, null, null, Wardrobe);
        Assert.Equal(LocalOperationStatus.Rejected, refused.Status);
        Assert.Equal("Not owned yet: Signal Track Jacket.", refused.Reason);
        Assert.Equal("No wardrobe catalogue.", LocalProgression.SetCard(p, "Robin", look, "").Reason);

        p.Cosmetics.Add(new OwnedCosmetic { CosmeticId = "COS-CH33", Source = "CH33", AcquiredUtc = TestContent.T0 });
        p.Cosmetics.Add(new OwnedCosmetic { CosmeticId = "COS-CH69", Source = "CH69", AcquiredUtc = TestContent.T0 });
        p.Cosmetics.Add(new OwnedCosmetic { CosmeticId = "COS-CH75", Source = "CH75", AcquiredUtc = TestContent.T0 });
        CardStyleCatalogue card = customization.Value.Card;
        CardStyle style = card.Default.Copy();
        style.Avatar = "dawn-horizon";
        LocalProgressionResult set = LocalProgression.SetCard(p, "Robin", look, "", style, card, null, null, Wardrobe);
        Assert.Equal(LocalOperationStatus.Applied, set.Status);
        Assert.Equal(look, set.Profile.Card.Look);
        Assert.Equal("dawn-horizon", set.Profile.Card.AvatarId);
        Assert.Equal("dawn-horizon", LocalProgression.StyleOf(set.Profile.Card, card.Default).Avatar);
        Assert.Empty(set.Profile.Validate());
        Assert.Equal(LocalOperationStatus.AlreadyApplied, LocalProgression.SetCard(ProfileJson.Clone(set.Profile), "Robin", look, "", style, card, null, null, Wardrobe).Status);
    }
}
