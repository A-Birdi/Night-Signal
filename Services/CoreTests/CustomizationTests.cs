using System.Text;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Customization;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace NightSignal.CoreTests;

/// <summary>
/// Visible customization (Addendum 01 §13, Addendum 02 §9.1, spec §9): the authored appearance catalogue and its coverage
/// floor for all 18 cars, clip-free wheel fitment, the livery document (canonical JSON, hash, strict parse, exact
/// validation, ownership), the draft editor (apply/cancel, ≥ 50 undo steps, 64-layer cap), the bounded wire form, the
/// publish cadence and the resolved appearance the renderer consumes. Uses the real content and customization.json.
/// </summary>
public sealed class CustomizationTests(ITestOutputHelper output)
{
    static ContentCatalogue Content => TestContent.Catalogue;
    static string Authored(string file) => File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", file));
    static readonly Lazy<string> json = new(() => Authored(CustomizationCatalogue.FileName));
    static readonly Lazy<CustomizationCatalogue> catalogue = new(() => CustomizationCatalogue.Load(json.Value));
    static CustomizationCatalogue Cust => catalogue.Value;

    static readonly string[] Cars = Enumerable.Range(1, 18).Select(i => $"V{i:00}").ToArray();

    /// <summary>A livery touching every section (V05: six-cylinder coupe, fits 13–18 in, offset −3..+3).</summary>
    static LiveryDocument Rich()
    {
        LiveryDocument d = Cust.StockLivery("V05");
        d.Body.Front = "aero";
        d.Body.Rear = "diffuser";
        d.Body.Side = "skirt";
        d.Body.RearAero = "gt-wing";
        d.Body.Exhaust = "quad";
        d.Wheels = new WheelSelection { Rim = "R07", DiameterIn = 17, OffsetStep = 2, Finish = "bronze" };
        d.Paint = new PaintSelection { Primary = "#2E8B75", Secondary = "#101010", Accent = "#C9A24A", Finish = "metallic", TwoTone = "side-stripe", Swatch = "P-CH17" };
        d.Lamps = new LampSelection { Head = "amber", Tail = "smoke-light" };
        d.Glass = "light";
        d.Plate = new PlateSelection { Text = "NS-05", Style = "sport" };
        d.Decals.Add(new DecalLayer { Shape = "num-7", Color = "#FFFFFF", Zone = "left", UMilli = 450, VMilli = 520, ScaleCenti = 60 });
        d.Decals.Add(new DecalLayer { Shape = "tea-line-pinstripe", Color = "#C9A24A", Zone = "hood", UMilli = 500, VMilli = 125, ScaleCenti = 150, RotationDeg = 90, OpacityPercent = 80, Mirror = true });
        d.Decals.Add(new DecalLayer { Shape = "chevron", Color = "#E4572E", Zone = "rear", UMilli = 0, VMilli = 1000, ScaleCenti = 5, RotationDeg = 359, OpacityPercent = 10, Flip = true });
        return d;
    }

    static readonly CosmeticOwnership OwnsRichUnlocks = CosmeticOwnership.FromIds(new[] { "COS-CH01", "COS-CH17" });

    static string Mutated(Action<JObject> change)
    {
        JObject o = JObject.Parse(json.Value);
        change(o);
        return o.ToString();
    }

    static JObject ChassisJ(JObject root, string car) => (JObject)((JArray)root["chassis"]).First(c => (string)c["car"] == car);

    // ------------------------------------------------------------------ catalogue

    [Fact]
    public void Catalogue_loads_and_cross_checks_the_content_catalogue()
    {
        Assert.Equal(1, Cust.Revision);
        Assert.Matches("^[0-9a-f]{64}$", Cust.Hash);
        Assert.Empty(Cust.ValidateAgainst(Content));
        Assert.Equal(18, Content.Cars.Count);
        Assert.Equal(Content.Cars.Select(c => c.Id).OrderBy(x => x), Cust.Chassis.Select(c => c.Car).OrderBy(x => x));
    }

    [Fact]
    public void Every_car_meets_the_coverage_floor_or_documents_its_equivalent()
    {
        foreach (string car in Cars)
        {
            ChassisAppearanceDef c = Cust.ChassisFor(car);
            foreach (string family in AppearanceVocabulary.FloorFamilies)
            {
                IReadOnlyList<string> offered = c.Families.Of(family);
                Assert.Contains(AppearanceVocabulary.Stock, offered);
                int nonStock = offered.Distinct().Count(v => v != AppearanceVocabulary.Stock);
                Assert.True(nonStock >= 2, $"{car}/{family}: {nonStock} non-stock");
                if (offered.Any(v => AppearanceVocabulary.IsEquivalent(family, v))) Assert.False(string.IsNullOrWhiteSpace(c.Note(family)), $"{car}/{family} needs a note");
                Assert.All(offered, v => Assert.True(AppearanceVocabulary.IsVariant(family, v), $"{car}/{family}: {v}"));
            }
            Assert.True(c.Families.Exhaust.Count(v => v != AppearanceVocabulary.Stock) >= 2, $"{car}: visible exhaust tips");
            Assert.Equal(AppearanceVocabulary.DecalZones.OrderBy(z => z), c.Decals.Zones.OrderBy(z => z));
            Assert.Contains("side-stripe", c.Paint.TwoTone);
        }
        // The documented rear-aero equivalents: hatches, the wagon and the roadster use lip-spoiler (with notes).
        string[] equivalents = Cars.Where(car => Cust.ChassisFor(car).Families.RearAero.Contains("lip-spoiler")).ToArray();
        Assert.Equal(new[] { "V02", "V04", "V06", "V07" }, equivalents);
        Assert.Equal(new[] { "stock", "ducktail", "lip-spoiler" }, Cust.ChassisFor("V04").Families.RearAero);
        Assert.Contains("roadster", Cust.ChassisFor("V04").Note("rearAero"));
    }

    [Fact]
    public void Eight_rim_designs_each_fit_every_chassis()
    {
        Assert.Equal(new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08" }, Cust.RimDesigns.Select(r => r.Id));
        Assert.Equal(AppearanceVocabulary.RimStyles.OrderBy(s => s), Cust.RimDesigns.Select(r => r.Style).OrderBy(s => s));
        foreach (string car in Cars)
        {
            ChassisAppearanceDef c = Cust.ChassisFor(car);
            foreach (RimDesignDef rim in Cust.RimDesigns)
                Assert.NotEmpty(CustomizationCatalogue.FittingDiameters(rim, c));
            Assert.True(Cust.TryRim(c.Wheels.StockRim, out RimDesignDef stock));
            Assert.Contains(c.Wheels.StockDiameterIn, CustomizationCatalogue.FittingDiameters(stock, c));
        }
    }

    [Fact]
    public void Fitment_matches_the_body_and_tuning_geometry_and_never_clips()
    {
        var body = JObject.Parse(Authored("cars.body.json"))["cars"].ToDictionary(c => (string)c["id"], c => (JObject)c);
        var rimByBodyStyle = new Dictionary<string, string> { ["5"] = "R01", ["6"] = "R02", ["mesh"] = "R03", ["split"] = "R04", ["dish"] = "R05" };
        foreach (string car in Cars)
        {
            ChassisAppearanceDef c = Cust.ChassisFor(car);
            JObject b = body[car];
            CarTuningDef t = Content.CarTunings[car];
            ChassisWheels w = c.Wheels;
            Assert.Equal((string)b["style"], c.Style);
            Assert.Equal((double)b["wheelRadius"], w.TyreRadiusM);
            Assert.Equal((double)b["tyreWidth"], w.TyreWidthM);
            Assert.Equal((double)b["rimFraction"], w.StockRimFraction);
            Assert.Equal(rimByBodyStyle[(string)b["rimStyle"]], w.StockRim);
            int clearance = (int)Math.Round((t.WidthM / 2 - t.TrackM / 2 - w.TyreWidthM / 2) * 1000, MidpointRounding.AwayFromZero);
            Assert.Equal(clearance, w.ArchClearanceMm);
            for (int d = w.DiameterIn.Min; d <= w.DiameterIn.Max; d++)
            {
                double f = WheelFitment.RimFraction(d, w.TyreRadiusM);
                Assert.InRange(f, WheelFitment.MinRimFraction, WheelFitment.MaxRimFraction);
            }
            // Outward offset keeps the tyre face ≥ 5 mm inside the arch; flush cars (≤ 5 mm) cannot move out at all.
            Assert.True(w.OffsetStep.Max * WheelFitment.OffsetStepMm + WheelFitment.ArchMarginMm <= Math.Max(w.ArchClearanceMm, WheelFitment.ArchMarginMm), car);
            if (w.ArchClearanceMm < 10) Assert.Equal(0, w.OffsetStep.Max);
        }
    }

    [Fact]
    public void Decal_library_maps_every_decal_cosmetic_to_a_render_kind()
    {
        var byCosmetic = Cust.DecalShapes.Where(s => s.CosmeticId != null).ToDictionary(s => s.CosmeticId);
        Assert.Equal(15, byCosmetic.Count);
        foreach (CosmeticDef cos in Content.Cosmetics.Where(c => c.Category == "decal"))
        {
            DecalShapeDef s = byCosmetic[cos.Id];
            Assert.Equal(cos.Name, s.Name);
            Assert.Contains(s.Render, AppearanceVocabulary.DecalRenders);
            Assert.Null(s.Glyph);
        }
        Assert.Equal("pinstripe", byCosmetic["COS-CH01"].Render); // Tea-Line Pinstripe
        Assert.Equal("bars", byCosmetic["COS-CH02"].Render);      // Brake Marker Bars
        Assert.Equal("arcs", byCosmetic["COS-CH03"].Render);      // Orchard Apex Arcs
        for (int n = 0; n <= 9; n++)
        {
            Assert.True(Cust.TryShape($"num-{n}", out DecalShapeDef digit));
            Assert.Equal(("digit", n.ToString()), (digit.Render, digit.Glyph));
        }
        Assert.All(Cust.DecalShapes.Where(s => s.Render == "text"), s => Assert.True(PlateText.IsValid(s.Glyph, 12, false)));
        // Every render kind has a free shape, and every paint cosmetic is a usable signature swatch.
        Assert.Equal(AppearanceVocabulary.DecalRenders.OrderBy(r => r), Cust.DecalShapes.Where(s => s.CosmeticId == null).Select(s => s.Render).Distinct().OrderBy(r => r));
        Assert.Equal(15, Cust.PaintSwatches.Count(s => s.CosmeticId != null));
        Assert.All(Cust.LampPresets, l => Assert.True(l.Transmission >= 0.6));
        Assert.DoesNotContain(Cust.LampPresets, l => l.Id == "amber" && l.Tail);
    }

    [Fact]
    public void Catalogue_validation_errors_are_exact()
    {
        void Expect(string mutated, params string[] errors)
        {
            Assert.False(CustomizationCatalogue.TryLoad(mutated, out CustomizationCatalogue c, out List<string> e));
            Assert.Null(c);
            Assert.Equal(errors, e);
        }

        Expect(Mutated(o => ((JArray)ChassisJ(o, "V01")["families"]["front"]).Replace(new JArray("stock", "lip"))),
            "Chassis V01/front: needs stock plus at least 2 non-stock choice(s), found 1");
        Expect(Mutated(o => ((JObject)ChassisJ(o, "V02")["notes"]).Remove("rearAero")),
            "Chassis V02/rearAero: a documented-equivalent variant needs a note explaining why");
        Expect(Mutated(o => ((JArray)ChassisJ(o, "V03")["families"]["rearAero"]).Add("spoiler")),
            "Chassis V03/rearAero: unknown variant spoiler");
        Expect(Mutated(o => ((JObject)o["decalShapes"][0])["render"] = "laser"),
            "Decal shape stripe: unknown render kind laser");
        Expect(Mutated(o => ((JObject)o["decalShapes"].First(s => (string)s["id"] == "num-3"))["glyph"] = "33"),
            "Decal shape num-3: a digit needs a glyph \"0\"–\"9\"");
        Expect(Mutated(o => ChassisJ(o, "V01")["wheels"]["diameterIn"]["max"] = 18),
            "Chassis V01/wheels: 18 in rim leaves too little sidewall (tyre would clip)");
        Expect(Mutated(o => ChassisJ(o, "V16")["wheels"]["offsetStep"]["max"] = 2),
            "Chassis V16/wheels: offset +2 (10 mm) would push the tyre past the arch (5 mm clearance)");
        Expect(Mutated(o => ((JObject)o["lampPresets"].First(l => (string)l["id"] == "smoke-light"))["transmission"] = 0.3),
            "Lamp preset smoke-light: transmission must be 0.6–1 so lamps stay visible");
        Expect(Mutated(o => ((JArray)o["rimDesigns"]).RemoveAt(7)),
            "At least 8 rim designs are required (found 7)");
        Expect(Mutated(o => ((JObject)o["plateStyles"][0])["text"] = "#E0E0E0"),
            "Plate style standard: text contrast below 4.5:1");
        Expect(Mutated(o => o["schema"] = "night-signal/customization@2"),
            "customization.json: expected schema night-signal/customization@1, found night-signal/customization@2");

        // Cross-check against content: a missing car and a decal shape pointing at a paint cosmetic.
        CustomizationCatalogue broken = CustomizationCatalogue.Load(Mutated(o =>
        {
            ((JArray)o["chassis"]).Remove(ChassisJ(o, "V18"));
            ((JObject)o["decalShapes"].First(s => (string)s["id"] == "rain-window"))["cosmeticId"] = "COS-CH20";
        }));
        Assert.Equal(new[]
        {
            "No customization chassis for V18",
            "Decal shape rain-window: cosmetic COS-CH20 is a paint, not a decal",
            "Decal cosmetic COS-CH08 (Rain Window) has no decal shape",
        }, broken.ValidateAgainst(Content));
    }

    // ------------------------------------------------------------------ document, canonical form, hash

    [Fact]
    public void Stock_livery_is_valid_for_every_car_and_resolves_to_the_factory_look()
    {
        foreach (string car in Cars)
        {
            LiveryDocument stock = Cust.StockLivery(car);
            Assert.Empty(LiveryValidator.Validate(stock, Cust, car, CosmeticOwnership.None, LiveryValidationMode.Apply).Errors);
            ResolvedAppearance r = AppearanceResolver.Resolve(Cust, car, stock, CosmeticOwnership.None);
            Assert.Empty(r.Notices);
            ChassisAppearanceDef c = Cust.ChassisFor(car);
            Assert.Equal(c.Wheels.StockRimFraction, r.RimFraction); // the stock wheel looks exactly as before
            Assert.Equal(c.Paint.Stock.Primary, r.Primary);
            Assert.Equal("stock", r.RearAero);
            Assert.True(stock.ContentEquals(r.Effective));
        }
    }

    [Fact]
    public void Canonical_json_is_pinned_and_round_trips_with_a_stable_hash()
    {
        LiveryDocument stock = Cust.StockLivery("V01");
        Assert.Equal(
            "{\"schema\":\"night-signal/livery@1\",\"car\":\"V01\",\"body\":{\"front\":\"stock\",\"rear\":\"stock\",\"side\":\"stock\",\"rearAero\":\"stock\",\"exhaust\":\"stock\"}," +
            "\"wheels\":{\"rim\":\"R05\",\"diameterIn\":14,\"offsetStep\":0,\"finish\":\"silver\"}," +
            "\"paint\":{\"primary\":\"#E8E4D8\",\"secondary\":\"#1C1D21\",\"accent\":\"#2A2C31\",\"finish\":\"gloss\",\"twoTone\":\"none\",\"swatch\":\"\"}," +
            "\"lamps\":{\"head\":\"clear\",\"tail\":\"clear\"},\"glass\":\"clear\",\"plate\":{\"text\":\"\",\"style\":\"standard\"},\"decals\":[]}",
            LiveryJson.ToCanonicalJson(stock));

        LiveryDocument rich = Rich();
        string canonical = LiveryJson.ToCanonicalJson(rich);
        Assert.Contains("{\"shape\":\"tea-line-pinstripe\",\"color\":\"#C9A24A\",\"zone\":\"hood\",\"u\":0.5,\"v\":0.125,\"scale\":1.5,\"rotationDeg\":90,\"opacity\":0.8,\"mirror\":true,\"flip\":false}", canonical);
        Assert.Contains("\"u\":0,\"v\":1,\"scale\":0.05,\"rotationDeg\":359,\"opacity\":0.1", canonical);

        LiveryParseResult parsed = LiveryJson.Parse(canonical);
        Assert.True(parsed.Ok, string.Join("\n", parsed.Errors));
        Assert.Equal(canonical, LiveryJson.ToCanonicalJson(parsed.Document));
        Assert.Equal(LiveryHash.Of(rich), LiveryHash.Of(parsed.Document));
        // Pinned: the hash is a cross-runtime contract (Unity client, server, control plane).
        Assert.Equal(PinnedRichHash, LiveryHash.Of(rich));

        // Formatting, key order, colour case and off-grid precision do not change the livery.
        JObject shuffled = JObject.Parse(canonical);
        var reordered = new JObject(shuffled.Properties().Reverse());
        reordered["paint"]["primary"] = "#2e8b75";
        ((JObject)reordered["decals"][0])["u"] = 0.4502;
        LiveryParseResult again = LiveryJson.Parse(reordered.ToString(Newtonsoft.Json.Formatting.Indented));
        Assert.True(again.Ok, string.Join("\n", again.Errors));
        Assert.Equal(LiveryHash.Of(rich), LiveryHash.Of(again.Document));

        LiveryDocument changed = rich.Clone();
        changed.Decals[0].UMilli = 451;
        Assert.NotEqual(LiveryHash.Of(rich), LiveryHash.Of(changed));
        Assert.False(rich.ContentEquals(changed));
    }

    // Reproduced independently (Node: sha256 of JSON.stringify with the same key order), so any client can verify it.
    const string PinnedRichHash = "4c33d4f4fd31a2f8c096b59d96b1fb5bbac289f844a92e082b393daf8822e546";

    [Fact]
    public void Parse_errors_are_exact()
    {
        string bad =
            "{\"schema\":\"night-signal/livery@0\",\"car\":\"V01\"," +
            "\"body\":{\"front\":\"stock\",\"rear\":\"stock\",\"side\":\"stock\",\"rearAero\":\"stock\",\"exhaust\":1}," +
            "\"wheels\":{\"rim\":\"R05\",\"diameterIn\":14.5,\"offsetStep\":0,\"finish\":\"silver\",\"size\":\"x\"}," +
            "\"paint\":{\"primary\":\"#FFFFFF\",\"secondary\":\"#000000\",\"accent\":\"#000000\",\"finish\":\"gloss\",\"twoTone\":\"none\"}," +
            "\"lamps\":{\"head\":\"clear\",\"tail\":\"clear\"},\"glass\":\"clear\",\"plate\":{\"text\":\"A\",\"style\":\"standard\"}," +
            "\"decals\":[{\"shape\":\"circle\",\"color\":\"#FFFFFF\",\"zone\":\"left\",\"u\":\"0.5\",\"v\":0.5,\"scale\":0.3,\"rotationDeg\":0,\"opacity\":1,\"mirror\":0,\"flip\":false}]," +
            "\"extra\":true}";
        LiveryParseResult r = LiveryJson.Parse(bad);
        Assert.Null(r.Document);
        Assert.Equal(new[]
        {
            "livery: unknown field 'extra'",
            "schema: expected night-signal/livery@1, found night-signal/livery@0",
            "body.exhaust: expected a string",
            "wheels: unknown field 'size'",
            "wheels.diameterIn: expected a whole number",
            "paint: missing field 'swatch'",
            "decals[0].u: expected a finite number",
            "decals[0].mirror: expected true or false",
        }, r.Errors);

        Assert.Equal(new[] { "livery: not valid JSON" }, LiveryJson.Parse("{\"schema\":1,\"schema\":2}").Errors);
        Assert.Equal(new[] { "livery: trailing content after the document" }, LiveryJson.Parse("{} {}").Errors);
        Assert.Equal(new[] { "livery: expected a JSON object" }, LiveryJson.Parse("[1,2]").Errors);
        Assert.Equal(new[] { "livery: empty payload" }, LiveryJson.Parse(" ").Errors);
        Assert.Equal(new[] { $"livery: payload exceeds {LiveryLimits.MaxPayloadChars} characters" }, LiveryJson.Parse(new string(' ', LiveryLimits.MaxPayloadChars) + "{}").Errors);
    }

    // ------------------------------------------------------------------ validation and ownership

    [Fact]
    public void Validation_errors_are_exact()
    {
        LiveryDocument d = Cust.StockLivery("V04");
        d.Body.Front = "splitter";
        d.Body.RearAero = "wing";
        d.Wheels.Rim = "R04";
        d.Wheels.DiameterIn = 14;
        d.Wheels.OffsetStep = 5;
        d.Wheels.Finish = "chrome";
        d.Paint.Primary = "red";
        d.Paint.Accent = "#12345G";
        d.Paint.Finish = "flake";
        d.Paint.Swatch = "P-CH17";
        d.Lamps.Tail = "amber";
        d.Glass = "limo";
        d.Plate.Text = "TOO LONG1";
        d.Plate.Style = "gold";
        d.Decals.Add(new DecalLayer { Shape = "laser", Zone = "left" });
        d.Decals.Add(new DecalLayer { Shape = "circle", Zone = "underside", UMilli = 1200, ScaleCenti = 300, RotationDeg = 400, OpacityPercent = 5 });

        LiveryValidation v = LiveryValidator.Validate(d, Cust, "V04", CosmeticOwnership.None, LiveryValidationMode.Preview);
        Assert.Equal(new[]
        {
            "body.front: 'splitter' is not offered on V04",
            "body.rearAero: 'wing' is not offered on V04",
            "wheels.diameterIn: R04 is made in 15–21 in, not 14 in",
            "wheels.offsetStep: 5 is outside the V04 fitment -3 to 4",
            "wheels.finish: unknown rim finish 'chrome'",
            "paint.primary: 'red' is not a #RRGGBB color",
            "paint.accent: '#12345G' is not a #RRGGBB color",
            "paint.finish: unknown finish 'flake'",
            "paint.swatch: P-CH17 is #2E8B75 metallic; primary and finish must match it",
            "lamps.tail: 'amber' is not a tail-lamp preset",
            "glass: 'limo' is not a window tint",
            "plate.text: 'TOO LONG1' must be at most 8 characters of A–Z, 0–9, space and '-' without leading or trailing spaces",
            "plate.style: unknown plate style 'gold'",
            "decals[0].shape: unknown decal shape 'laser'",
            "decals[1].zone: 'underside' is not a decal zone on V04",
            "decals[1].u: 1.2 is outside 0–1",
            "decals[1].scale: 3 is outside 0.05–1.6",
            "decals[1].rotationDeg: 400 is outside 0–359",
            "decals[1].opacity: 0.05 is outside 0.1–1",
        }, v.Errors);
        Assert.Equal(new[] { "paint.swatch" }, v.Locked.Select(l => l.Path));

        Assert.Equal(new[] { "car: livery is for V04, not V05" }, LiveryValidator.Validate(d, Cust, "V05", null, LiveryValidationMode.Apply).Errors);
        Assert.Equal(new[] { "car: no customization chassis for V99" }, LiveryValidator.Validate(d, Cust, "V99", null, LiveryValidationMode.Apply).Errors);
        LiveryDocument v01 = Cust.StockLivery("V01");
        v01.Wheels.DiameterIn = 19;
        Assert.Equal(new[] { "wheels.diameterIn: 19 in is outside the V01 fitment 13–17 in" }, LiveryValidator.Validate(v01, Cust, "V01", null, LiveryValidationMode.Apply).Errors);
    }

    [Fact]
    public void Locked_items_can_be_previewed_but_applying_requires_ownership()
    {
        LiveryDocument rich = Rich();
        LiveryValidation preview = LiveryValidator.Validate(rich, Cust, "V05", CosmeticOwnership.None, LiveryValidationMode.Preview);
        Assert.True(preview.IsValid);
        Assert.Equal(new[] { ("paint.swatch", "P-CH17", "COS-CH17", "Canal Jade Metallic"), ("decals[1].shape", "tea-line-pinstripe", "COS-CH01", "Tea-Line Pinstripe") },
            preview.Locked.Select(l => (l.Path, l.ItemId, l.CosmeticId, l.Name)));

        LiveryValidation apply = LiveryValidator.Validate(rich, Cust, "V05", CosmeticOwnership.FromIds(new[] { "COS-CH17" }), LiveryValidationMode.Apply);
        Assert.Equal(new[] { "decals[1].shape: Tea-Line Pinstripe is locked (unlock cosmetic COS-CH01)" }, apply.Errors);
        Assert.True(LiveryValidator.Validate(rich, Cust, "V05", OwnsRichUnlocks, LiveryValidationMode.Apply).IsValid);

        // The editor previews locked items in the draft but refuses to apply them; nothing changes until they are owned.
        var editor = new LiveryEditor(Cust, "V05", null);
        Assert.True(editor.LoadIntoDraft(rich).Changed);
        Assert.Empty(editor.ResolveDraft().Notices); // the Garage preview shows the locked items on the car
        LiveryApplyResult refused = editor.Apply(CosmeticOwnership.None);
        Assert.False(refused.Applied);
        Assert.Null(refused.Document);
        Assert.Equal(2, refused.Validation.Errors.Count);
        Assert.True(editor.IsDirty);
        Assert.Equal(LiveryHash.Of(Cust.StockLivery("V05")), editor.AppliedHash);
        LiveryApplyResult ok = editor.Apply(OwnsRichUnlocks);
        Assert.True(ok.Applied);
        Assert.Equal(LiveryHash.Of(rich), ok.LiveryHash);
    }

    // ------------------------------------------------------------------ editor

    [Fact]
    public void Editor_undo_redo_keeps_at_least_fifty_meaningful_steps()
    {
        var editor = new LiveryEditor(Cust, "V01", null);
        string start = editor.DraftHash;
        var hashes = new List<string> { start };
        for (int i = 0; i < 60; i++)
        {
            LiveryEditResult r = i % 3 == 0 ? editor.SetPaintColor(LiveryEditor.PaintSlot.Primary, $"#{i:X2}{i:X2}40")
                : i % 3 == 1 ? editor.AddDecal(i % 2 == 0 ? "star" : "num-" + (i % 10), "left")
                : editor.MoveDecal(editor.DecalCount - 1, i / 100.0, 0.25);
            Assert.True(r.Changed, r.Reason);
            hashes.Add(editor.DraftHash);
        }
        Assert.Equal(60, editor.UndoCount);
        for (int i = 60; i > 0; i--)
        {
            Assert.True(editor.Undo());
            Assert.Equal(hashes[i - 1], editor.DraftHash);
        }
        Assert.False(editor.Undo());
        Assert.Equal(start, editor.DraftHash);
        Assert.False(editor.IsDirty);
        for (int i = 1; i <= 60; i++)
        {
            Assert.True(editor.Redo());
            Assert.Equal(hashes[i], editor.DraftHash);
        }
        Assert.False(editor.Redo());

        // Bounded history: the oldest steps fall off beyond MaxUndoSteps (≥ the spec's 50).
        for (int i = 0; i < 10; i++) editor.SetPlateText("P" + i);
        Assert.Equal(LiveryEditor.MaxUndoSteps, editor.UndoCount);
        Assert.True(LiveryEditor.MaxUndoSteps >= LiveryLimits.MinUndoSteps);

        // A new edit after Undo discards the redo branch.
        editor.Undo();
        Assert.Equal(1, editor.RedoCount);
        editor.SetGlass("medium");
        Assert.Equal(0, editor.RedoCount);
    }

    [Fact]
    public void Editor_merges_a_drag_into_one_step_and_ignores_no_op_edits()
    {
        var editor = new LiveryEditor(Cust, "V10", null);
        editor.AddDecal("ring", "hood", "#ff8800");
        Assert.Equal("#FF8800", editor.Draft.Decals[0].Color);
        int before = editor.UndoCount;
        for (int i = 1; i <= 20; i++) Assert.True(editor.MoveDecal(0, i / 50.0, 0.5, continuing: true).Changed);
        Assert.Equal(before + 1, editor.UndoCount);
        editor.EndGesture();
        editor.MoveDecal(0, 0.9, 0.9, continuing: true);
        Assert.Equal(before + 2, editor.UndoCount);
        Assert.Equal(LiveryEditStatus.Unchanged, editor.MoveDecal(0, 0.9, 0.9).Status);
        Assert.Equal(LiveryEditStatus.Unchanged, editor.SetVariant("front", "stock").Status);
        Assert.Equal(before + 2, editor.UndoCount);
        editor.Undo();
        Assert.Equal((400, 500), (editor.Draft.Decals[0].UMilli, editor.Draft.Decals[0].VMilli)); // where the first drag ended
        editor.Undo();
        Assert.Equal(500, editor.Draft.Decals[0].UMilli); // the first drag is one step back to the centred default
        Assert.Equal(1, editor.UndoCount);
    }

    [Fact]
    public void Editor_covers_every_field_and_rejects_what_the_chassis_cannot_take()
    {
        var e = new LiveryEditor(Cust, "V04", null);
        Assert.Equal(LiveryEditStatus.Rejected, e.SetVariant("rearAero", "wing").Status);
        Assert.True(e.SetVariant("rearAero", "lip-spoiler").Changed);
        Assert.True(e.SetVariant("front", "track").Changed);
        Assert.True(e.SetVariant("rear", "valance").Changed);
        Assert.True(e.SetVariant("side", "sculpted").Changed);
        Assert.True(e.SetVariant("exhaust", "center").Changed);
        Assert.Equal(LiveryEditStatus.Rejected, e.SetVariant("exhaust", "quad").Status);

        Assert.Equal(LiveryEditStatus.Rejected, e.SetRimDiameter(13).Status); // stock R03 is not made in 13 in
        Assert.True(e.SetRim("R05").Changed);
        Assert.True(e.SetRimDiameter(13).Changed);
        Assert.True(e.SetRim("R04").Changed); // 13 in not made: nearest fitting size is chosen
        Assert.Equal(15, e.Draft.Wheels.DiameterIn);
        Assert.Equal(LiveryEditStatus.Rejected, e.SetRimDiameter(18).Status); // V04 fits 13–17
        Assert.True(e.SetRimOffset(4).Changed);
        Assert.Equal(LiveryEditStatus.Rejected, e.SetRimOffset(5).Status);
        Assert.True(e.SetRimFinish("gold").Changed);

        Assert.True(e.ApplySwatch("P-CH30").Changed);
        Assert.Equal(("#2B2F6B", "pearl", "P-CH30"), (e.Draft.Paint.Primary, e.Draft.Paint.Finish, e.Draft.Paint.Swatch));
        Assert.True(e.SetPaintFinish("matte").Changed);
        Assert.Equal("", e.Draft.Paint.Swatch); // leaving the swatch's finish clears the swatch
        Assert.True(e.SetPaintColor(LiveryEditor.PaintSlot.Secondary, "#101820").Changed);
        Assert.True(e.SetPaintColor(LiveryEditor.PaintSlot.Accent, "#c0c0c0").Changed);
        Assert.Equal(LiveryEditStatus.Rejected, e.SetPaintColor(LiveryEditor.PaintSlot.Primary, "blue").Status);
        Assert.True(e.SetTwoTone("roof").Changed);

        Assert.Equal(LiveryEditStatus.Rejected, e.SetTailLamp("amber").Status);
        Assert.True(e.SetHeadLamp("amber").Changed);
        Assert.True(e.SetTailLamp("smoke-light").Changed);
        Assert.True(e.SetGlass("medium").Changed);
        Assert.True(e.SetPlateText("  ns-86 ").Changed);
        Assert.Equal("NS-86", e.Draft.Plate.Text);
        Assert.Equal(LiveryEditStatus.Rejected, e.SetPlateText("NS_86").Status);
        Assert.Equal(LiveryEditStatus.Rejected, e.SetPlateText("123456789").Status);
        Assert.True(e.SetPlateStyle("night").Changed);

        Assert.True(e.AddDecal("num-8", "left").Changed);
        Assert.True(e.AddDecal("stripe", "hood", "#FFFFFF").Changed);
        Assert.Equal(LiveryEditStatus.Rejected, e.AddDecal("stripe", "underside").Status);
        Assert.True(e.DuplicateDecal(0).Changed);
        Assert.True(e.SetDecalZone(1, "right").Changed);
        Assert.True(e.TransformDecal(1, 0.75, -90).Changed);
        Assert.Equal((75, 270), (e.Draft.Decals[1].ScaleCenti, e.Draft.Decals[1].RotationDeg));
        Assert.True(e.SetDecalMirror(1, true).Changed);
        Assert.True(e.SetDecalFlip(1, true).Changed);
        Assert.True(e.RecolorDecal(2, "#E4572E").Changed);
        Assert.True(e.SetDecalOpacity(2, 0.02).Changed);
        Assert.Equal(10, e.Draft.Decals[2].OpacityPercent); // clamped to the 0.1 floor
        Assert.True(e.ReorderDecal(2, 0).Changed);
        Assert.Equal("stripe", e.Draft.Decals[0].Shape);
        Assert.True(e.RemoveDecal(1).Changed);
        Assert.Equal(new[] { "stripe", "num-8" }, e.Draft.Decals.Select(x => x.Shape));
        Assert.Equal(LiveryEditStatus.Rejected, e.RemoveDecal(5).Status);

        Assert.True(e.Validate(CosmeticOwnership.None, LiveryValidationMode.Apply).IsValid);
        Assert.True(e.ResetToStock().Changed);
        Assert.False(e.IsDirty);
        Assert.True(e.Undo());
        Assert.True(e.IsDirty);
    }

    [Fact]
    public void Apply_returns_the_new_document_and_hash_and_cancel_restores_the_applied_look()
    {
        var editor = new LiveryEditor(Cust, "V12", null);
        string original = editor.AppliedHash;
        editor.SetVariant("rearAero", "gt-wing");
        editor.SetRim("R06");
        editor.AddDecal("hex", "roof", "#FFD23F");
        Assert.True(editor.IsDirty);
        editor.Cancel();
        Assert.False(editor.IsDirty);
        Assert.Equal(original, editor.DraftHash);
        Assert.Equal(0, editor.UndoCount);

        editor.SetVariant("rearAero", "gt-wing");
        editor.SetPaintColor(LiveryEditor.PaintSlot.Primary, "#F2F2EE");
        LiveryApplyResult r = editor.Apply(CosmeticOwnership.None);
        Assert.True(r.Applied);
        Assert.NotEqual(original, r.LiveryHash);
        Assert.Equal(LiveryHash.Of(r.Document), r.LiveryHash);
        Assert.Equal(r.CanonicalJson, LiveryJson.ToCanonicalJson(LiveryJson.Parse(r.CanonicalJson).Document));
        Assert.Equal(r.LiveryHash, editor.AppliedHash);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);

        // Cancel after an apply returns to the NEW applied livery, not the factory one.
        editor.SetVariant("front", "lip");
        editor.Cancel();
        Assert.Equal(r.LiveryHash, editor.DraftHash);
        Assert.Throws<ArgumentException>(() => new LiveryEditor(Cust, "V01", r.Document));
    }

    [Fact]
    public void Sixty_four_decal_layers_is_a_hard_cap()
    {
        var editor = new LiveryEditor(Cust, "V09", null);
        for (int i = 0; i < LiveryLimits.MaxDecalLayers; i++) Assert.True(editor.AddDecal("circle", "left").Changed);
        Assert.Equal(64, editor.DecalCount);
        Assert.Equal("All 64 decal layers are used.", editor.AddDecal("circle", "left").Reason);
        Assert.Equal(LiveryEditStatus.Rejected, editor.DuplicateDecal(3).Status);
        Assert.True(editor.Apply(CosmeticOwnership.None).Applied);

        LiveryDocument over = editor.Applied;
        over.Decals.Add(new DecalLayer { Shape = "circle", Zone = "left" });
        Assert.Equal(new[] { "decals: 65 layers exceed the 64-layer limit" }, LiveryValidator.Validate(over, Cust, "V09", null, LiveryValidationMode.Apply).Errors);
        Assert.Throws<ArgumentException>(() => LiveryWire.Encode(over));
        ResolvedAppearance shown = AppearanceResolver.Resolve(Cust, "V09", over);
        Assert.Equal(64, shown.Decals.Count);
        Assert.Equal(new[] { "decals: layers above 64 are not shown (1 dropped)." }, shown.Notices);
    }

    // ------------------------------------------------------------------ wire, cadence, presets

    [Fact]
    public void Wire_form_round_trips_exactly_and_stays_within_its_bound()
    {
        LiveryDocument rich = Rich();
        string wire = LiveryWire.Encode(rich);
        LiveryWireResult back = LiveryWire.Decode(wire);
        Assert.True(back.Ok, string.Join("\n", back.Errors));
        Assert.True(rich.ContentEquals(back.Document));
        Assert.Equal(LiveryHash.Of(rich), LiveryHash.Of(back.Document));
        Assert.StartsWith("[1,\"V05\",[\"aero\",\"diffuser\",\"skirt\",\"gt-wing\",\"quad\"],[\"R07\",17,2,\"bronze\"],[\"2E8B75\",", wire);
        int typical = Encoding.UTF8.GetByteCount(wire);
        Assert.True(typical < 400, $"typical {typical}");

        // Worst case: every catalogue-controlled id at the 24-character limit, 64 layers at maximal numeric widths.
        string id24 = new string('x', LiveryLimits.MaxIdLength);
        var worst = new LiveryDocument
        {
            Car = id24,
            Body = new BodySelection { Front = "track", Rear = "diffuser", Side = "sculpted", RearAero = "lip-spoiler", Exhaust = "center" },
            Wheels = new WheelSelection { Rim = id24, DiameterIn = 22, OffsetStep = -9, Finish = id24 },
            Paint = new PaintSelection { Primary = "#FFFFFF", Secondary = "#FFFFFF", Accent = "#FFFFFF", Finish = "metallic", TwoTone = "side-stripe", Swatch = id24 },
            Lamps = new LampSelection { Head = id24, Tail = id24 },
            Glass = id24,
            Plate = new PlateSelection { Text = "ABCD-123", Style = id24 },
        };
        for (int i = 0; i < 64; i++)
            worst.Decals.Add(new DecalLayer { Shape = id24, Color = "#FFFFFF", Zone = "front", UMilli = 1000, VMilli = 1000, ScaleCenti = 160, RotationDeg = 359, OpacityPercent = 100, Mirror = true, Flip = true });
        string max = LiveryWire.Encode(worst);
        int maxBytes = Encoding.UTF8.GetByteCount(max);
        output.WriteLine($"wire bytes: typical 3-layer {typical}, worst case {maxBytes}, bound {LiveryWire.MaxWireBytes}");
        Assert.Equal(4833, maxBytes); // 354-byte header + 64 × 69-byte layers + 63 separators
        Assert.True(maxBytes <= LiveryWire.MaxWireBytes);
        Assert.True(LiveryWire.Decode(max).Ok);

        Assert.Equal(new[] { $"wire: larger than {LiveryWire.MaxWireBytes} bytes" }, LiveryWire.Decode(max + new string(' ', 300)).Errors);
        string tooMany = "[1,\"V09\",[\"stock\",\"stock\",\"stock\",\"stock\",\"stock\"],[\"R03\",17,0,\"silver\"],[\"FFFFFF\",\"000000\",\"000000\",\"gloss\",\"none\",\"\"],[\"clear\",\"clear\",\"clear\"],[\"\",\"standard\"],[" +
                         string.Join(",", Enumerable.Repeat("[\"circle\",\"FFFFFF\",\"left\",500,500,30,0,100,0]", 65)) + "]]";
        Assert.Equal(new[] { "decals: 65 layers exceed the 64-layer limit" }, LiveryWire.Decode(tooMany).Errors);
        Assert.Equal(new[] { "wire: unsupported version 2" }, LiveryWire.Decode("[2,0,0,0,0,0,0,0]").Errors);
        Assert.Equal(new[] { "decals[0].u: expected a whole number 0..1000" },
            LiveryWire.Decode(tooMany.Replace(string.Join(",", Enumerable.Repeat("[\"circle\",\"FFFFFF\",\"left\",500,500,30,0,100,0]", 65)), "[\"circle\",\"FFFFFF\",\"left\",1001,500,30,0,100,0]")).Errors);
    }

    [Fact]
    public void Publish_gate_sends_changes_only_at_a_network_safe_cadence()
    {
        DateTime t = TestContent.T0;
        var gate = new LiveryPublishGate();
        Assert.True(gate.Offer("a", t));
        Assert.Equal((1L, "a"), (gate.Revision, gate.PublishedHash));
        Assert.False(gate.Offer("a", t.AddSeconds(10)));       // unchanged: never re-sent
        Assert.True(gate.Offer("b", t.AddSeconds(10.5)));      // a real change long after the last send goes out at once
        Assert.Equal((2L, "b"), (gate.Revision, gate.PublishedHash));
    }

    [Fact]
    public void Publish_gate_holds_rapid_changes_and_releases_the_newest()
    {
        DateTime t = TestContent.T0;
        var gate = new LiveryPublishGate();
        Assert.True(gate.Offer("a", t));
        Assert.False(gate.Offer("b", t.AddSeconds(0.5)));
        Assert.False(gate.Offer("c", t.AddSeconds(1.0)));
        Assert.True(gate.HasPending);
        Assert.False(gate.Poll(t.AddSeconds(1.9)));
        Assert.True(gate.Poll(t.AddSeconds(2.0)));
        Assert.Equal((2L, "c"), (gate.Revision, gate.PublishedHash));
        Assert.False(gate.Poll(t.AddSeconds(9)));
        Assert.False(gate.Offer("c", t.AddSeconds(9)));
    }

    [Fact]
    public void Car_presets_store_liveries_in_visual_preset_slots()
    {
        CarBuildWorkspace ws = CarBuildWorkspace.CreateNew("inst-v05", "V05", TestContent.T0);
        LiveryDocument rich = Rich();
        OperationResult saved = LiveryPresets.Save(ws, ws.Revision, "Jade Night", rich, Cust, TestContent.T0);
        Assert.True(saved.Accepted, saved.Message);
        VisualPreset preset = ws.VisualPresets.Single();
        Assert.Equal(LiveryDocument.SchemaId, preset.PayloadSchema);
        Assert.Equal(LiveryJson.ToCanonicalJson(rich), preset.PayloadJson);
        LiveryParseResult read = LiveryPresets.Read(preset);
        Assert.True(read.Ok);
        Assert.Equal(LiveryHash.Of(rich), LiveryHash.Of(read.Document));
        Assert.Empty(ws.Loadouts); // mechanical state untouched
        Assert.Equal(MechanicalSnapshot.Stock().SelectionHash(), ws.Applied.Build.SelectionHash());

        LiveryDocument wrongCar = Cust.StockLivery("V01");
        OperationResult refused = LiveryPresets.Save(ws, ws.Revision, "Other", wrongCar, Cust, TestContent.T0);
        Assert.Equal(OpStatus.Rejected, refused.Status);
        Assert.Equal("Livery is not valid for this car: car: livery is for V01, not V05", refused.Message);

        preset.PayloadSchema = "night-signal/visual-preset";
        Assert.Equal(new[] { "preset: payload schema is 'night-signal/visual-preset', not night-signal/livery@1" }, LiveryPresets.Read(preset).Errors);

        var editor = new LiveryEditor(Cust, "V05", null);
        Assert.True(editor.LoadIntoDraft(read.Document).Changed);
        Assert.Equal(1, editor.UndoCount);
        Assert.Equal(LiveryEditStatus.Rejected, editor.LoadIntoDraft(wrongCar).Status);
    }

    // ------------------------------------------------------------------ resolved appearance

    [Fact]
    public void Resolved_appearance_falls_back_to_stock_with_notices()
    {
        LiveryDocument d = Rich();
        d.Body.RearAero = "lip-spoiler";   // not offered on V05
        d.Wheels.DiameterIn = 19;           // outside V05 fitment 13–18
        d.Wheels.OffsetStep = 7;
        d.Paint.Secondary = "nope";
        d.Lamps.Tail = "amber";
        d.Plate.Text = "bad!";
        d.Decals.Insert(0, new DecalLayer { Shape = "laser", Zone = "left" });

        ResolvedAppearance r = AppearanceResolver.Resolve(Cust, "V05", d, OwnsRichUnlocks);
        Assert.Equal(new[]
        {
            "body.rearAero: 'lip-spoiler' is not offered on V05; showing stock.",
            "wheels.diameterIn: 19 in does not fit R07 on V05; showing 15 in.",
            "wheels.offsetStep: 7 is outside the V05 fitment; showing 0.",
            "paint.secondary: 'nope' is not a #RRGGBB color; showing stock.",
            "lamps.tail: 'amber' is not a tail-lamp preset; showing clear.",
            "plate.text: invalid plate text; showing a blank plate.",
            "decals[0]: unknown decal shape 'laser'; layer not shown.",
        }, r.Notices);
        Assert.Equal(("stock", "trunk-lid"), (r.RearAero, r.RearAeroMount));
        Assert.Equal(("R07", "multi-spoke", 15, 0), (r.RimId, r.RimStyle, r.RimDiameterIn, r.OffsetStep));
        Assert.Equal(Cust.ChassisFor("V05").Paint.Stock.Secondary, r.Secondary);
        Assert.Equal(("amber", 0.85, "clear", ""), (r.HeadTint, r.HeadTransmission, r.TailTint, r.PlateText));
        Assert.Equal(("P-CH17", "#5FC4A8"), (r.SwatchId, r.FlipTint));
        Assert.Equal(new[] { ("digit", "7"), ("pinstripe", (string)null), ("chevron", null) }, r.Decals.Select(x => (x.Render, x.Glyph)));
        Assert.Equal((0.45, 0.52, 0.6), (r.Decals[0].U, r.Decals[0].V, r.Decals[0].Scale));
        Assert.Equal(LiveryHash.Of(r.Effective), r.EffectiveHash);
        Assert.Equal(LiveryHash.Of(d), r.SourceHash);
        Assert.Equal("sides", r.TwoToneZone);
        Assert.Equal(3, r.DecalKeepOut["front"].Count);
        Assert.True(r.Anchors.ContainsKey("exhaust"));

        // Without the unlocks, a local preview hides locked items (the swatch keeps its free colour, loses the flip tint).
        ResolvedAppearance locked = AppearanceResolver.Resolve(Cust, "V05", Rich(), CosmeticOwnership.None);
        Assert.Equal(new[]
        {
            "paint.swatch: Canal Jade Metallic is locked; showing the colour without its flip tint.",
            "decals[1]: Tea-Line Pinstripe is locked; layer not shown.",
        }, locked.Notices);
        Assert.Equal(("#2E8B75", (string)null, 2), (locked.Primary, locked.FlipTint, locked.Decals.Count));

        // A server-accepted livery (ownership null) shows as sent; wrong car or none shows the stock car.
        Assert.Empty(AppearanceResolver.Resolve(Cust, "V05", Rich()).Notices);
        ResolvedAppearance other = AppearanceResolver.Resolve(Cust, "V06", Rich());
        Assert.Equal(new[] { "Livery is for V05, not V06: showing the stock appearance." }, other.Notices);
        Assert.Equal(LiveryHash.Of(Cust.StockLivery("V06")), other.EffectiveHash);
        Assert.Equal(new[] { "No livery: showing the stock appearance." }, AppearanceResolver.Resolve(Cust, "V06", null).Notices);
    }

    [Fact]
    public void Remote_players_resolve_the_same_appearance_from_the_wire()
    {
        LiveryDocument rich = Rich();
        ResolvedAppearance local = AppearanceResolver.Resolve(Cust, "V05", rich);
        ResolvedAppearance remote = AppearanceResolver.Resolve(Cust, "V05", LiveryWire.Decode(LiveryWire.Encode(rich)).Document);
        Assert.Equal(local.EffectiveHash, remote.EffectiveHash);
        Assert.Equal(LiveryJson.ToCanonicalJson(local.Effective), LiveryJson.ToCanonicalJson(remote.Effective));
    }

    [Fact]
    public void Appearance_never_changes_a_simulation_input()
    {
        foreach (string car in Cars)
        {
            ChassisAppearanceDef c = Cust.ChassisFor(car);
            foreach (RimDesignDef rim in Cust.RimDesigns)
                foreach (int size in CustomizationCatalogue.FittingDiameters(rim, c))
                {
                    LiveryDocument d = Cust.StockLivery(car);
                    d.Wheels.Rim = rim.Id;
                    d.Wheels.DiameterIn = size;
                    d.Wheels.OffsetStep = c.Wheels.OffsetStep.Max;
                    ResolvedAppearance r = AppearanceResolver.Resolve(Cust, car, d);
                    Assert.Empty(r.Notices);
                    Assert.Equal(c.Wheels.TyreRadiusM, r.TyreRadiusM); // tyre size (gearing, ride height) never changes
                    Assert.True(r.OffsetM * 1000 + WheelFitment.ArchMarginMm <= Math.Max(c.Wheels.ArchClearanceMm, WheelFitment.ArchMarginMm) + 1e-9);
                }
        }
        // No customization type carries mechanical or physics state.
        string[] mechanical = { "NightSignal.Core.Rules", "NightSignal.Core.Builds" };
        foreach (Type t in new[] { typeof(LiveryDocument), typeof(DecalLayer), typeof(WheelSelection), typeof(BodySelection), typeof(ResolvedAppearance), typeof(ChassisAppearanceDef) })
            Assert.All(t.GetFields(), f => Assert.DoesNotContain(f.FieldType.Namespace ?? "", mechanical));
    }
}
