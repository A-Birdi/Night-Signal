using System.Text.Json;
using Newtonsoft.Json.Linq;
using NightSignal.ControlPlane.Garage;
using NightSignal.ControlPlane.Persistence;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Customization;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// ONLINE Garage (Addendum 02 §8–10) over the real SQLite store: per-instance ownership, workspace operations with
/// optimistic concurrency and confirmation tokens, and Buy-and-Apply atomicity/idempotency.
/// </summary>
public sealed class GarageServiceTests : IAsyncLifetime
{
    GarageTestKit kit = null!;
    GarageService G => kit.Garage;
    static readonly CancellationToken None = CancellationToken.None;

    public async Task InitializeAsync() => kit = await GarageTestKit.CreateAsync();
    public async Task DisposeAsync() => await kit.DisposeAsync();

    static JsonElement Ok(GarageReply r)
    {
        Assert.True(r.Ok, $"{r.Status}: {JsonSerializer.Serialize(r.Body, GarageTestKit.Web)}");
        return GarageTestKit.Body(r);
    }

    static void Fails(GarageReply r, int status, string code)
    {
        Assert.Equal(status, r.Status);
        Assert.Equal(code, GarageTestKit.ErrorOf(r));
    }

    async Task<long> Revision(string account, string instance) =>
        Ok(await G.GetCarAsync(account, instance, None)).GetProperty("workspace").GetProperty("revision").GetInt64();

    Task<GarageReply> Op(string account, string instance, GarageOpRequest req) => G.OperateAsync(account, instance, req, None);

    // ------------------------------------------------------------------ instances, ownership, workspaces

    [Fact]
    public async Task EveryOwnedCar_GetsItsOwnInstance_AndAStockWorkspace_WithServerPiAndHash()
    {
        string a = await kit.PlayerAsync(1, "V01");
        JsonElement list = Ok(await G.ListCarsAsync(a, None));
        Assert.Equal("online", list.GetProperty("domain").GetString());
        JsonElement car = Assert.Single(list.GetProperty("cars").EnumerateArray());
        Assert.Equal("V01", car.GetProperty("carId").GetString());
        Assert.Equal(1, car.GetProperty("revision").GetInt64());
        Assert.Equal(220, car.GetProperty("applied").GetProperty("pi").GetInt32()); // stock estimate == catalogue BasePI
        Assert.Equal(TestData.Content.Catalogue.Car("V01").BasePI, car.GetProperty("applied").GetProperty("pi").GetInt32());
        string hash = car.GetProperty("applied").GetProperty("buildHash").GetString()!;
        BuildContext ctx = BuildContext.Create(TestData.Content.Catalogue, GarageTestKit.Content.Parts, "V01", new PartInventory(), 1);
        Assert.Equal(ctx.Stock.BuildHash, hash);
        Assert.Equal(0, car.GetProperty("loadouts").GetProperty("count").GetInt32()); // no invented presets
        Assert.Equal(CarBuildWorkspace.MinLoadoutSlots, car.GetProperty("loadouts").GetProperty("capacity").GetInt32());
        Assert.Equal(CarBuildWorkspace.MinVisualPresetSlots, car.GetProperty("visualPresets").GetProperty("capacity").GetInt32());

        // Listing twice never duplicates instances or workspaces.
        Ok(await G.ListCarsAsync(a, None));
        Assert.Equal(1, kit.Scalar("SELECT COUNT(*) FROM car_instances WHERE account_id = $a", ("$a", a)));
        Assert.Equal(1, kit.Scalar("SELECT COUNT(*) FROM car_workspaces WHERE account_id = $a", ("$a", a)));
    }

    [Fact]
    public async Task PartOwnership_IsPerInstance_TwoInstancesOfOneModelAreIndependent()
    {
        string a = await kit.PlayerAsync(1, "V01");
        string b = await kit.PlayerAsync(2, "V01");
        string ia = await kit.InstanceAsync(a), ib = await kit.InstanceAsync(b);
        // A second instance of the same model for account A (the schema supports it; ordinal 2).
        string ia2 = SqlGameStore.InstanceIdFor(a, "V01", 2);
        kit.Exec("INSERT INTO car_instances (instance_id, account_id, car_id, ordinal, source) VALUES ($i, $a, 'V01', 2, 'purchase')", ("$i", ia2), ("$a", a));
        Assert.NotEqual(ia, ia2);

        kit.Grant(a, ia, "TYR-T1-STREET");
        // The unique (instance, part) key refuses a second grant of the same part to the same instance.
        Assert.ThrowsAny<Exception>(() => kit.Grant(a, ia, "TYR-T1-STREET"));

        JsonElement partsA = Ok(await G.PartsAsync(a, ia, None));
        JsonElement partsA2 = Ok(await G.PartsAsync(a, ia2, None));
        JsonElement partsB = Ok(await G.PartsAsync(b, ib, None));
        bool Owned(JsonElement parts, string id) => parts.GetProperty("slots").EnumerateArray().SelectMany(s => s.GetProperty("parts").EnumerateArray())
            .Single(p => p.GetProperty("partId").GetString() == id).GetProperty("owned").GetBoolean();
        Assert.True(Owned(partsA, "TYR-T1-STREET"));
        Assert.False(Owned(partsA2, "TYR-T1-STREET")); // same model, same account, other instance
        Assert.False(Owned(partsB, "TYR-T1-STREET"));

        // Applying the part is legal on the owning instance only; the other instance gets an exact repair list.
        long r1 = await Revision(a, ia);
        Ok(await Op(a, ia, new GarageOpRequest("edit-draft", r1, Build: GarageTestKit.Build("TYR-T1-STREET"))));
        Ok(await Op(a, ia, new GarageOpRequest("apply", r1 + 1)));
        long r2 = await Revision(a, ia2);
        Ok(await Op(a, ia2, new GarageOpRequest("edit-draft", r2, Build: GarageTestKit.Build("TYR-T1-STREET"))));
        GarageReply refused = await Op(a, ia2, new GarageOpRequest("apply", r2 + 1));
        Fails(refused, 409, "needs_repair");
        Assert.Contains(GarageTestKit.Body(refused).GetProperty("repairs").EnumerateArray(), x => x.GetProperty("kind").GetString() == "not-owned");

        // Another account can never read or change A's instance.
        Fails(await G.GetCarAsync(b, ia, None), 404, "not_found");
        Fails(await Op(b, ia, new GarageOpRequest("discard-draft", 1)), 404, "not_found");
    }

    [Fact]
    public async Task Migrations_ApplyOnSqlite_WithTheGarageTablesAndConstraints()
    {
        Assert.Equal(1, kit.Scalar("SELECT COUNT(*) FROM schema_migrations WHERE version = '0005_garage'"));
        foreach (string table in new[] { "car_instances", "car_part_ownership", "car_workspaces", "garage_quotes", "garage_quote_settlements" })
            Assert.Equal(1, kit.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $t", ("$t", table)));
        // Re-running the migrator is a no-op (recorded versions are skipped).
        await kit.Store.InitializeAsync();
        Assert.Equal(1, kit.Scalar("SELECT COUNT(*) FROM schema_migrations WHERE version = '0005_garage'"));
    }

    [Fact]
    public void GarageContent_LoadsPartsAndRecipesFromContentAuthored_AndRefusesInconsistentData()
    {
        GarageContent c = GarageTestKit.Content;
        Assert.True(c.Parts.Revision >= 1);
        Assert.True(c.Parts.PriceRevision >= 1);
        Assert.Equal(TestData.Content.Catalogue.Cars.Count, c.Recipes.File.Cars.Count);
        Assert.Equal(64, c.Hash.Length);
        string authored = Path.Combine(AppContext.BaseDirectory, "content", "authored");
        string parts = File.ReadAllText(Path.Combine(authored, GarageContent.PartsFile));
        string recipes = File.ReadAllText(Path.Combine(authored, GarageContent.RecipesFile));
        Assert.Equal(c.Hash, GarageContent.From(parts, recipes, TestData.Content.Catalogue).Hash); // deterministic
        Assert.Throws<InvalidOperationException>(() => GarageContent.From(parts.Replace("\"car\": \"V18\"", "\"car\": \"V99\""), recipes, TestData.Content.Catalogue));
        Assert.Throws<InvalidOperationException>(() => GarageContent.From(parts, recipes.Replace("\"TYR-T1-STREET\"", "\"TYR-NOPE\""), TestData.Content.Catalogue));
    }

    [Fact]
    public void CustomizationContent_LoadsFromContentAuthored_OutsideTheRaceContentHash_AndRefusesInconsistentData()
    {
        CustomizationContent c = GarageTestKit.Customization;
        Assert.Matches("^[0-9a-f]{64}$", c.Hash);
        Assert.True(c.Revision >= 1);
        Assert.All(TestData.Content.Catalogue.Cars, car => Assert.True(c.Catalogue.TryChassis(car.Id, out _), car.Id));
        // Appearance never changes a simulation input, so the race ContentHash does not cover customization.json.
        Assert.DoesNotContain(CustomizationCatalogue.FileName, ContentCatalogue.AuthoredFiles);
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "authored", CustomizationContent.FileName));
        Assert.Equal(c.Hash, CustomizationContent.From(json, TestData.Content.Catalogue).Hash); // deterministic
        string lf = json.Replace("\r\n", "\n");
        Assert.Equal(c.Hash, CustomizationContent.From(lf, TestData.Content.Catalogue).Hash); // LF-normalised: CRLF and LF
        Assert.Equal(c.Hash, CustomizationContent.From(lf.Replace("\n", "\r\n"), TestData.Content.Catalogue).Hash); // copies agree
        JObject noV18 = JObject.Parse(json);
        ((JArray)noV18["chassis"]!).OfType<JObject>().Single(x => (string?)x["car"] == "V18").Remove();
        Assert.Throws<InvalidOperationException>(() => CustomizationContent.From(noV18.ToString(), TestData.Content.Catalogue));
        Assert.Throws<InvalidOperationException>(() => CustomizationContent.From("{}", TestData.Content.Catalogue));
        Assert.Equal(LiveryHash.Of(c.Catalogue.StockLivery("V01")), c.StockHash("V01"));
    }

    // ------------------------------------------------------------------ liveries and visual presets

    static string Canonical(LiveryDocument d) => LiveryJson.ToCanonicalJson(d);

    /// <summary>The same livery as a client might send it: indented, lower-case colours, decal placement as plain decimals.</summary>
    static string Sloppy(LiveryDocument d)
    {
        JObject o = JObject.Parse(Canonical(d));
        o["paint"]!["primary"] = ((string)o["paint"]!["primary"]!).ToLowerInvariant();
        return o.ToString(Newtonsoft.Json.Formatting.Indented);
    }

    [Fact]
    public async Task LiveryApply_StoresTheCanonicalLiveryAndTheServerHash_AndNeverTouchesTheBuild()
    {
        string a = await kit.PlayerAsync(1);
        string i = await kit.InstanceAsync(a);
        LiveryDocument doc = GarageTestKit.Livery();
        string canonical = Canonical(doc), hash = LiveryHash.Of(doc);
        JsonElement before = Ok(await G.GetCarAsync(a, i, None)).GetProperty("workspace");
        Assert.Equal("", before.GetProperty("appliedLivery").GetString());
        Assert.Equal("", before.GetProperty("appliedLiveryHash").GetString());

        JsonElement applied = Ok(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: Sloppy(doc))));
        Assert.Equal("ok", applied.GetProperty("status").GetString());
        Assert.False(applied.GetProperty("performanceChanged").GetBoolean());
        JsonElement ws = applied.GetProperty("workspace");
        Assert.Equal(2, ws.GetProperty("revision").GetInt64());
        Assert.Equal(canonical, ws.GetProperty("appliedLivery").GetString()); // canonical, not the client's text
        Assert.Equal(hash, ws.GetProperty("appliedLiveryHash").GetString()); // computed by the server
        Assert.Equal("", ws.GetProperty("appliedVisualPresetId").GetString());
        Assert.Equal(1, ws.GetProperty("applied").GetProperty("revision").GetInt64()); // the mechanical applied build never moves
        Assert.Equal(before.GetProperty("applied").GetProperty("buildHash").GetString(), ws.GetProperty("applied").GetProperty("buildHash").GetString());
        Assert.Equal(2, kit.WorkspaceRevision(i));

        // Stored: a fresh read returns it; the same livery again is unchanged.
        Assert.Equal(canonical, Ok(await G.GetCarAsync(a, i, None)).GetProperty("workspace").GetProperty("appliedLivery").GetString());
        Assert.Equal("unchanged", Ok(await Op(a, i, new GarageOpRequest("livery-apply", 2, LiveryJson: canonical))).GetProperty("status").GetString());
        Assert.Equal(2, kit.WorkspaceRevision(i));
        // The frozen appearance the game server gets is the compact wire form of exactly this livery.
        (EntrantBuild? frozen, _) = await G.FreezeAsync(a, i, None);
        Assert.Equal(hash, frozen!.Appearance!.CosmeticHash);
        LiveryWireResult wire = LiveryWire.Decode(frozen.Appearance.Livery!);
        Assert.True(wire.Ok, string.Join("; ", wire.Errors));
        Assert.Equal(hash, LiveryHash.Of(wire.Document));

        // Back to stock (null or "").
        JsonElement stock = Ok(await Op(a, i, new GarageOpRequest("livery-apply", 2)));
        Assert.Equal("", stock.GetProperty("workspace").GetProperty("appliedLivery").GetString());
        Assert.Equal("", stock.GetProperty("workspace").GetProperty("appliedLiveryHash").GetString());
        Assert.Equal(3, kit.WorkspaceRevision(i));
        (frozen, _) = await G.FreezeAsync(a, i, None);
        Assert.Null(frozen!.Appearance!.Livery);
        Assert.Equal(GarageTestKit.Customization.StockHash("V01"), frozen.Appearance.CosmeticHash);
        Fails(await Op(a, i, new GarageOpRequest("livery-apply", 2, LiveryJson: canonical)), 409, "stale_revision");
    }

    [Fact]
    public async Task LiveryApply_RefusesAnotherCarsLivery_InvalidDocuments_AndLockedCosmeticsUntilOwned()
    {
        string a = await kit.PlayerAsync(1);
        string i = await kit.InstanceAsync(a);

        static (JsonElement Body, string[] Errors) Invalid(GarageReply r)
        {
            Fails(r, 400, "invalid_livery");
            JsonElement body = GarageTestKit.Body(r);
            return (body, body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray());
        }

        // A V03 livery never applies to a V01.
        (_, string[] wrongCar) = Invalid(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: Canonical(GarageTestKit.Livery("V03")))));
        Assert.Equal(new[] { "car: livery is for V03, not V01" }, wrongCar);
        // Not JSON / not a livery document / semantically invalid: the exact Core messages.
        Assert.Equal(new[] { "livery: not valid JSON" }, Invalid(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: "{not json"))).Errors);
        Assert.Contains("livery: unknown field 'x'",
            Invalid(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: Canonical(GarageTestKit.Livery()).Replace("{\"schema\"", "{\"x\":1,\"schema\"")))).Errors);
        LiveryDocument tooBig = GarageTestKit.Livery();
        tooBig.Wheels.DiameterIn = 20;
        Assert.Contains("wheels.diameterIn: 20 in is outside the V01 fitment 13–17 in",
            Invalid(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: Canonical(tooBig)))).Errors);
        Fails(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: new string(' ', GarageWire.MaxLiveryJsonChars + 1))), 400, "invalid_request");

        // A locked decal is refused in Apply mode until THIS account owns the cosmetic (no client flag can unlock it).
        LiveryDocument locked = GarageTestKit.Livery(locked: true);
        (JsonElement body, string[] errors) = Invalid(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: Canonical(locked))));
        string lockedError = Assert.Single(errors);
        Assert.StartsWith("decals[1].shape: ", lockedError);
        Assert.EndsWith(" is locked (unlock cosmetic COS-CH01)", lockedError);
        JsonElement item = Assert.Single(body.GetProperty("locked").EnumerateArray());
        Assert.Equal("COS-CH01", item.GetProperty("cosmeticId").GetString());
        Assert.Equal("decals[1].shape", item.GetProperty("path").GetString());
        Assert.Equal(1, kit.WorkspaceRevision(i)); // every refusal changed nothing

        // Another account owning it does not help; owning it on THIS account does.
        string b = await kit.PlayerAsync(2);
        kit.Exec("INSERT INTO cosmetics_owned (account_id, cosmetic_id, source) VALUES ($a, 'COS-CH01', 'challenge')", ("$a", b));
        Invalid(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: Canonical(locked))));
        kit.Exec("INSERT INTO cosmetics_owned (account_id, cosmetic_id, source) VALUES ($a, 'COS-CH01', 'challenge')", ("$a", a));
        JsonElement ok = Ok(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: Canonical(locked))));
        Assert.Equal(LiveryHash.Of(locked), ok.GetProperty("workspace").GetProperty("appliedLiveryHash").GetString());
    }

    [Fact]
    public async Task AMaximal64LayerLivery_IsAcceptedPrettyPrintedUpTo32KiB_AndItsCanonicalFormFitsTheStoredPayloadBound()
    {
        string a = await kit.PlayerAsync(1);
        string i = await kit.InstanceAsync(a);
        CustomizationCatalogue cat = GarageTestKit.Customization.Catalogue;
        string longest = cat.DecalShapes.Where(s => s.CosmeticId is null).OrderByDescending(s => s.Id.Length).First().Id;
        LiveryDocument max = GarageTestKit.Livery();
        max.Decals.Clear();
        for (int k = 0; k < LiveryLimits.MaxDecalLayers; k++)
            max.Decals.Add(new DecalLayer
            {
                Shape = longest, Color = "#ABCDEF", Zone = "right", UMilli = 999, VMilli = 999, ScaleCenti = 155, RotationDeg = 359, OpacityPercent = 99,
                Mirror = true, Flip = true,
            });
        string canonical = Canonical(max), pretty = JObject.Parse(canonical).ToString(Newtonsoft.Json.Formatting.Indented);
        Assert.True(canonical.Length <= GarageWire.MaxPayloadJsonChars, $"canonical {canonical.Length} chars");
        Assert.True(pretty.Length > GarageWire.MaxPayloadJsonChars && pretty.Length <= GarageWire.MaxLiveryJsonChars, $"pretty {pretty.Length} chars");

        JsonElement applied = Ok(await Op(a, i, new GarageOpRequest("livery-apply", 1, LiveryJson: pretty)));
        Assert.Equal(canonical, applied.GetProperty("workspace").GetProperty("appliedLivery").GetString());
        JsonElement preset = Ok(await Op(a, i, new GarageOpRequest("visual-preset-save", 2, Name: "Max", PayloadSchema: LiveryDocument.SchemaId, PayloadJson: pretty)));
        Assert.Equal(canonical, preset.GetProperty("workspace").GetProperty("visualPresets")[0].GetProperty("payloadJson").GetString());
        // Opaque (non-livery) payloads keep the 16 KiB bound.
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-save", 3, Name: "Big", PayloadSchema: "other", PayloadJson: pretty)), 400, "invalid_request");
        (EntrantBuild? frozen, _) = await G.FreezeAsync(a, i, None);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(frozen!.Appearance!.Livery!) <= LiveryWire.MaxWireBytes);
    }

    [Fact]
    public async Task VisualPresets_LiveryPayloadsAreValidatedAndCanonical_UpdateNeedsConfirmation_RenameRefusesDuplicates()
    {
        string a = await kit.PlayerAsync(1);
        string i = await kit.InstanceAsync(a);
        LiveryDocument blue = GarageTestKit.Livery(primary: "#1F4E8C"), locked = GarageTestKit.Livery(locked: true);

        // Save: validated in PREVIEW mode for this car (a locked item may be kept as a plan), stored canonically.
        JsonElement saved = Ok(await Op(a, i, new GarageOpRequest("visual-preset-save", 1, Name: "Night Blue", PayloadSchema: LiveryDocument.SchemaId,
            PayloadJson: Sloppy(blue))));
        string id = saved.GetProperty("loadoutId").GetString()!;
        Assert.Equal(Canonical(blue), saved.GetProperty("workspace").GetProperty("visualPresets")[0].GetProperty("payloadJson").GetString());
        string plan = Ok(await Op(a, i, new GarageOpRequest("visual-preset-save", 2, Name: "Pinstripe plan", PayloadSchema: LiveryDocument.SchemaId,
            PayloadJson: Canonical(locked)))).GetProperty("loadoutId").GetString()!;
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-save", 3, Name: "Other car", PayloadSchema: LiveryDocument.SchemaId,
            PayloadJson: Canonical(GarageTestKit.Livery("V03")))), 400, "invalid_livery");
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-save", 3, Name: "Empty", PayloadSchema: LiveryDocument.SchemaId)), 400, "invalid_livery");

        // Update: confirmation token of this revision (like a loadout overwrite); identical payload is unchanged.
        LiveryDocument red = GarageTestKit.Livery(primary: "#C8102E");
        GarageReply ask = await Op(a, i, new GarageOpRequest("visual-preset-update", 3, PresetId: id, PayloadSchema: LiveryDocument.SchemaId, PayloadJson: Sloppy(red)));
        Fails(ask, 409, "confirmation_required");
        string token = GarageTestKit.Body(ask).GetProperty("confirmationToken").GetString()!;
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-update", 3, PresetId: id, PayloadSchema: LiveryDocument.SchemaId, PayloadJson: Sloppy(red),
            ConfirmationToken: "cf-forged")), 409, "confirmation_required");
        Assert.Equal(3, kit.WorkspaceRevision(i));
        JsonElement updated = Ok(await Op(a, i, new GarageOpRequest("visual-preset-update", 3, PresetId: id, PayloadSchema: LiveryDocument.SchemaId,
            PayloadJson: Sloppy(red), ConfirmationToken: token)));
        Assert.Equal(Canonical(red), updated.GetProperty("workspace").GetProperty("visualPresets")[0].GetProperty("payloadJson").GetString());
        Assert.Equal("unchanged", Ok(await Op(a, i, new GarageOpRequest("visual-preset-update", 4, PresetId: id, PayloadSchema: LiveryDocument.SchemaId,
            PayloadJson: Canonical(red)))).GetProperty("status").GetString());
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-update", 4, PayloadSchema: LiveryDocument.SchemaId, PayloadJson: Canonical(red))), 400, "invalid_request");
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-update", 4, PresetId: "vp-missing", PayloadSchema: LiveryDocument.SchemaId,
            PayloadJson: Canonical(red))), 404, "not_found");

        // Rename: same rules as save (unique per car, case-insensitive).
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-rename", 4, PresetId: id, Name: "pinstripe PLAN")), 409, "duplicate_name");
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-rename", 4, PresetId: id, Name: " ")), 400, "invalid_name");
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-rename", 4, Name: "x")), 400, "invalid_request");
        Assert.Equal("Signal Red", Ok(await Op(a, i, new GarageOpRequest("visual-preset-rename", 4, PresetId: id, Name: "Signal Red")))
            .GetProperty("workspace").GetProperty("visualPresets")[0].GetProperty("name").GetString());

        // Applying from a preset links it; the link must name the preset that holds exactly this look.
        Fails(await Op(a, i, new GarageOpRequest("livery-apply", 5, LiveryJson: Canonical(blue), PresetId: id)), 400, "invalid_request");
        Fails(await Op(a, i, new GarageOpRequest("livery-apply", 5, LiveryJson: Canonical(red), PresetId: "vp-missing")), 404, "not_found");
        JsonElement linked = Ok(await Op(a, i, new GarageOpRequest("livery-apply", 5, LiveryJson: Canonical(red), PresetId: id))).GetProperty("workspace");
        Assert.Equal(id, linked.GetProperty("appliedVisualPresetId").GetString());
        // The planned (locked) preset cannot be applied until the cosmetic is owned.
        Fails(await Op(a, i, new GarageOpRequest("livery-apply", 6, LiveryJson: Canonical(locked), PresetId: plan)), 400, "invalid_livery");
        Assert.Equal(6, kit.WorkspaceRevision(i));
    }

    [Fact]
    public async Task ChangeWithoutLosing_CH50_TwoPresetsSwitched_TheFirstRestoredExactly_Once()
    {
        string a = await kit.PlayerAsync(1);
        string i = await kit.InstanceAsync(a);
        LiveryDocument blue = GarageTestKit.Livery(primary: "#1F4E8C"), red = GarageTestKit.Livery(primary: "#C8102E");
        static string? Challenge(JsonElement r) =>
            r.TryGetProperty("challenge", out JsonElement c) && c.ValueKind == JsonValueKind.Object ? c.GetProperty("challengeId").GetString() : null;

        string first = Ok(await Op(a, i, new GarageOpRequest("visual-preset-save", 1, Name: "Night Blue", PayloadSchema: LiveryDocument.SchemaId,
            PayloadJson: Canonical(blue)))).GetProperty("loadoutId").GetString()!;
        string second = Ok(await Op(a, i, new GarageOpRequest("visual-preset-save", 2, Name: "Signal Red", PayloadSchema: LiveryDocument.SchemaId,
            PayloadJson: Canonical(red)))).GetProperty("loadoutId").GetString()!;
        // An edited livery (no preset) then the first preset: not a switch between presets.
        Assert.Null(Challenge(Ok(await Op(a, i, new GarageOpRequest("livery-apply", 3, LiveryJson: Canonical(red))))));
        Assert.Null(Challenge(Ok(await Op(a, i, new GarageOpRequest("livery-apply", 4, LiveryJson: Canonical(blue), PresetId: first)))));
        // First to second: a switch forward, nothing restored yet.
        Assert.Null(Challenge(Ok(await Op(a, i, new GarageOpRequest("livery-apply", 5, LiveryJson: Canonical(red), PresetId: second)))));
        // Second back to the first, exactly: CH50, with the new saved revision.
        JsonElement back = Ok(await Op(a, i, new GarageOpRequest("livery-apply", 6, LiveryJson: Canonical(blue), PresetId: first)));
        Assert.Equal("CH50", Challenge(back));
        Assert.Equal(7, back.GetProperty("revision").GetInt64());
        Assert.True(back.GetProperty("challenge").GetProperty("cash").GetInt64() > 0);
        // Once only.
        Ok(await Op(a, i, new GarageOpRequest("livery-apply", 7, LiveryJson: Canonical(red), PresetId: second)));
        Assert.Null(Challenge(Ok(await Op(a, i, new GarageOpRequest("livery-apply", 8, LiveryJson: Canonical(blue), PresetId: first)))));
    }

    // ------------------------------------------------------------------ operations, revisions, confirmation tokens

    [Fact]
    public async Task EveryOperation_QuotesTheCurrentRevision_StaleEditorsAreRejected()
    {
        string a = await kit.PlayerAsync(1);
        string i = await kit.InstanceAsync(a);
        JsonElement saved = Ok(await Op(a, i, new GarageOpRequest("save-as", 1, Name: "Wet Grip", Note: "for rain")));
        Assert.Equal(2, saved.GetProperty("revision").GetInt64());
        string id = saved.GetProperty("loadoutId").GetString()!;

        // A second device still at revision 1 cannot overwrite the accepted state.
        GarageReply stale = await Op(a, i, new GarageOpRequest("rename", 1, LoadoutId: id, Name: "Other"));
        Fails(stale, 409, "stale_revision");
        Assert.Equal(2, GarageTestKit.Body(stale).GetProperty("revision").GetInt64());
        Assert.Equal(2, kit.WorkspaceRevision(i));

        Ok(await Op(a, i, new GarageOpRequest("rename", 2, LoadoutId: id, Name: "Short Gears")));
        Ok(await Op(a, i, new GarageOpRequest("note", 3, LoadoutId: id, Note: "tight hairpins")));
        JsonElement dup = Ok(await Op(a, i, new GarageOpRequest("duplicate", 4, LoadoutId: id, Name: "Boss Try")));
        Ok(await Op(a, i, new GarageOpRequest("pin", 5, LoadoutId: id, Pinned: true)));
        Fails(await Op(a, i, new GarageOpRequest("save-as", 6, Name: "boss try")), 409, "duplicate_name");
        Fails(await Op(a, i, new GarageOpRequest("save-as", 6, Name: "   ")), 400, "invalid_name");
        // A livery-schema payload must be a valid livery for this car (not just any JSON).
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-save", 6, Name: "Night livery", PayloadSchema: "night-signal/livery@1",
            PayloadJson: "{\"paint\":\"#101820\"}")), 400, "invalid_livery");
        Ok(await Op(a, i, new GarageOpRequest("visual-preset-save", 6, Name: "Night livery", PayloadSchema: "night-signal/livery@1",
            PayloadJson: LiveryJson.ToCanonicalJson(GarageTestKit.Livery()))));
        Fails(await Op(a, i, new GarageOpRequest("visual-preset-save", 7, Name: "Bad", PayloadJson: "<script>")), 400, "invalid_request");

        JsonElement ws = Ok(await G.GetCarAsync(a, i, None)).GetProperty("workspace");
        Assert.Equal(7, ws.GetProperty("revision").GetInt64());
        Assert.Equal(new[] { "Short Gears", "Boss Try" }, ws.GetProperty("loadouts").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.True(ws.GetProperty("loadouts")[0].GetProperty("pinned").GetBoolean());
        Assert.Equal("tight hairpins", ws.GetProperty("loadouts")[0].GetProperty("note").GetString());
        Assert.Equal(dup.GetProperty("loadoutId").GetString(), ws.GetProperty("loadouts")[1].GetProperty("loadoutId").GetString());
        Assert.Single(ws.GetProperty("visualPresets").EnumerateArray());
        Assert.Equal(7, kit.WorkspaceRevision(i));

        // Missing revision / unknown op / missing target are request errors that change nothing.
        Fails(await Op(a, i, new GarageOpRequest("rename", null, LoadoutId: id, Name: "x")), 400, "invalid_request");
        Fails(await Op(a, i, new GarageOpRequest("teleport", 7)), 400, "invalid_request");
        Fails(await Op(a, i, new GarageOpRequest("delete", 7)), 400, "invalid_request");
        Fails(await Op(a, i, new GarageOpRequest("delete", 7, LoadoutId: "ld-nope")), 404, "not_found");
        Assert.Equal(7, kit.WorkspaceRevision(i));
    }

    [Fact]
    public async Task DeleteOverwriteAndReplacingADirtyDraft_NeedTheConfirmationToken_OfThisRevision()
    {
        string a = await kit.PlayerAsync(1);
        string i = await kit.InstanceAsync(a);
        kit.Grant(a, i, "TYR-T1-STREET", "BRK-T1-PADS");
        string id = Ok(await Op(a, i, new GarageOpRequest("save-as", 1, Name: "Stock"))).GetProperty("loadoutId").GetString()!;

        GarageReply ask = await Op(a, i, new GarageOpRequest("delete", 2, LoadoutId: id));
        Fails(ask, 409, "confirmation_required");
        string token = GarageTestKit.Body(ask).GetProperty("confirmationToken").GetString()!;
        Fails(await Op(a, i, new GarageOpRequest("delete", 2, LoadoutId: id, ConfirmationToken: "cf-forged")), 409, "confirmation_required");
        Assert.Equal(2, kit.WorkspaceRevision(i));

        // Overwrite (with the dirty draft) asks too and shows the comparison; a token from an older revision is not valid.
        Ok(await Op(a, i, new GarageOpRequest("edit-draft", 2, Build: GarageTestKit.Build("TYR-T1-STREET", "BRK-T1-PADS"))));
        Fails(await Op(a, i, new GarageOpRequest("delete", 3, LoadoutId: id, ConfirmationToken: token)), 409, "confirmation_required");
        GarageReply askOverwrite = await Op(a, i, new GarageOpRequest("overwrite", 3, LoadoutId: id));
        Fails(askOverwrite, 409, "confirmation_required");
        Assert.True(GarageTestKit.Body(askOverwrite).GetProperty("comparison").GetProperty("parts").GetArrayLength() >= 2);
        string overwriteToken = GarageTestKit.Body(askOverwrite).GetProperty("confirmationToken").GetString()!;
        JsonElement overwritten = Ok(await Op(a, i, new GarageOpRequest("overwrite", 3, LoadoutId: id, ConfirmationToken: overwriteToken)));
        Assert.Equal("TYR-T1-STREET", overwritten.GetProperty("workspace").GetProperty("loadouts")[0].GetProperty("build").GetProperty("parts")
            .GetProperty("tyres").GetString());

        // Loading the applied build over the dirty draft needs confirmation; the applied (race) build never changes.
        GarageReply askLoad = await Op(a, i, new GarageOpRequest("load-into-draft", 4, Source: new DraftSourceInput("applied")));
        Fails(askLoad, 409, "confirmation_required");
        Ok(await Op(a, i, new GarageOpRequest("load-into-draft", 4, Source: new DraftSourceInput("applied"),
            ConfirmationToken: GarageTestKit.Body(askLoad).GetProperty("confirmationToken").GetString())));

        GarageReply askDelete = await Op(a, i, new GarageOpRequest("delete", 5, LoadoutId: id));
        Ok(await Op(a, i, new GarageOpRequest("delete", 5, LoadoutId: id, ConfirmationToken: GarageTestKit.Body(askDelete).GetProperty("confirmationToken").GetString())));
        JsonElement ws = Ok(await G.GetCarAsync(a, i, None)).GetProperty("workspace");
        Assert.Empty(ws.GetProperty("loadouts").EnumerateArray());
        Assert.Equal(1, ws.GetProperty("applied").GetProperty("revision").GetInt64());
        Assert.Equal(2, kit.OwnedCount(i)); // deleting a plan never touches ownership
    }

    [Fact]
    public async Task DraftApply_IsAtomicWholeBuild_KeepsProtectedReferences_AndApplyLoadoutRestores()
    {
        string a = await kit.PlayerAsync(1);
        string i = await kit.InstanceAsync(a);
        kit.Grant(a, i, "TYR-T1-STREET", "BRK-T1-PADS", "GBX-T1-FINAL");
        Ok(await Op(a, i, new GarageOpRequest("begin-workshop")));
        long r = await Revision(a, i);
        string stock = Ok(await Op(a, i, new GarageOpRequest("save-as", r, Name: "Stock", FromApplied: true))).GetProperty("loadoutId").GetString()!;

        // A draft with an unowned part cannot be applied (exact repair list; nothing bought or substituted).
        JsonElement preview = Ok(await Op(a, i, new GarageOpRequest("edit-draft", r + 1, Build: GarageTestKit.Build("TYR-T1-STREET", "ENG-T1-INTAKE"))));
        Assert.Contains("ENG-T1-INTAKE", preview.GetProperty("evaluation").GetProperty("previewPartIds").EnumerateArray().Select(x => x.GetString()));
        Fails(await Op(a, i, new GarageOpRequest("apply", r + 2)), 409, "needs_repair");

        var tune = new Dictionary<string, int> { ["FinalDrive"] = 1060 };
        Ok(await Op(a, i, new GarageOpRequest("edit-draft", r + 2, Build: GarageTestKit.Build(tune, "TYR-T1-STREET", "BRK-T1-PADS", "GBX-T1-FINAL"))));
        JsonElement applied = Ok(await Op(a, i, new GarageOpRequest("apply", r + 3)));
        Assert.True(applied.GetProperty("performanceChanged").GetBoolean());
        JsonElement ws = applied.GetProperty("workspace");
        Assert.Equal(2, ws.GetProperty("applied").GetProperty("revision").GetInt64());
        Assert.Equal(1060, ws.GetProperty("applied").GetProperty("build").GetProperty("tuning").GetProperty("values").GetProperty("FinalDrive").GetInt32());
        Assert.True(ws.GetProperty("references").TryGetProperty("before-workshop", out _));
        Assert.Equal(1, ws.GetProperty("references").GetProperty("before-last-apply").GetProperty("sourceAppliedRevision").GetInt64());
        Assert.NotEqual(ws.GetProperty("references").GetProperty("before-last-apply").GetProperty("buildHash").GetString(),
            ws.GetProperty("applied").GetProperty("buildHash").GetString());

        // Quick selector: apply the saved stock loadout in one action (reusing owned parts is free).
        long now = applied.GetProperty("revision").GetInt64();
        JsonElement restored = Ok(await Op(a, i, new GarageOpRequest("apply-loadout", now, LoadoutId: stock)));
        Assert.Equal(3, restored.GetProperty("workspace").GetProperty("applied").GetProperty("revision").GetInt64());
        Assert.Empty(restored.GetProperty("workspace").GetProperty("applied").GetProperty("build").GetProperty("parts").EnumerateObject());
        Ok(await Op(a, i, new GarageOpRequest("discard-draft", now + 1)));
        Ok(await Op(a, i, new GarageOpRequest("end-workshop", now + 2)));
        Assert.Equal(3, kit.OwnedCount(i));
        // Timestamps survive the stored-document round trip in UTC (this machine's local zone must not leak in).
        JsonElement stored = Ok(await G.GetCarAsync(a, i, None)).GetProperty("workspace");
        Assert.Equal(kit.Clock.GetUtcNow(), stored.GetProperty("applied").GetProperty("appliedUtc").GetDateTimeOffset());
        Assert.Equal(kit.Clock.GetUtcNow(), stored.GetProperty("references").GetProperty("before-last-apply").GetProperty("capturedUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task StoreCompareAndSwap_RefusesAWriteComputedFromAnOlderRevision()
    {
        string a = await kit.PlayerAsync(1);
        string i = await kit.InstanceAsync(a);
        StoredWorkspace ws = (await kit.Store.GetWorkspaceAsync(i))!;
        var write = new WorkspaceWrite(2, 2, ws.Json.Replace("\"Revision\":1", "\"Revision\":2"), 1, "h", 220);
        Assert.Equal(WorkspaceSaveStatus.Saved, await kit.Store.SaveWorkspaceAsync(a, i, 1, write));
        Assert.Equal(WorkspaceSaveStatus.Stale, await kit.Store.SaveWorkspaceAsync(a, i, 1, write with { Revision = 3 }));
        Assert.Equal(WorkspaceSaveStatus.NotFound, await kit.Store.SaveWorkspaceAsync("00000000-0000-4000-8000-0000000000ff", i, 2, write));
        Assert.Equal(2, kit.WorkspaceRevision(i));
    }

    // ------------------------------------------------------------------ Buy-and-Apply

    async Task<(string Account, string Instance, JsonElement Quote)> QuotedAsync(long balance, int normalCleared = 0, params string[] parts)
    {
        string a = await kit.PlayerAsync(1, "V01", balance, normalCleared);
        string i = await kit.InstanceAsync(a);
        Ok(await Op(a, i, new GarageOpRequest("edit-draft", 1, Build: GarageTestKit.Build(parts))));
        JsonElement quote = Ok(await G.CreateQuoteAsync(a, i, new QuoteRequest(), None));
        return (a, i, quote);
    }

    void AssertUntouched(string account, string instance, long balance, long revision)
    {
        Assert.Equal(balance, kit.Balance(account));
        Assert.Equal(0, kit.OwnedCount(instance));
        Assert.Equal(revision, kit.WorkspaceRevision(instance));
        Assert.Equal(0, kit.Scalar("SELECT COUNT(*) FROM garage_quote_settlements"));
        Assert.Equal(0, kit.Scalar("SELECT COUNT(*) FROM ledger_entries WHERE reward_type = 'part-purchase'"));
    }

    [Fact]
    public async Task BuyAndApply_ConcurrentSettlesOfOneQuote_ChargeAndGrantExactlyOnce()
    {
        (string a, string i, JsonElement q) = await QuotedAsync(50_000, 0, "TYR-T1-STREET", "BRK-T1-PADS");
        JsonElement quote = q.GetProperty("quote");
        string quoteId = quote.GetProperty("quoteId").GetString()!;
        Assert.Equal(16_000, quote.GetProperty("total").GetInt64());
        Assert.True(q.GetProperty("affordableNow").GetBoolean());
        Assert.Equal(1, quote.GetProperty("appliedRevision").GetInt64()); // quoted against applied revision 1

        GarageReply[] replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => G.SettleQuoteAsync(a, i, quoteId, true, None))));
        Assert.All(replies, r => Assert.Equal(200, r.Status));
        JsonElement[] bodies = replies.Select(GarageTestKit.Body).ToArray();
        Assert.Single(bodies, b => !b.GetProperty("replayed").GetBoolean());
        Assert.Equal(7, bodies.Count(b => b.GetProperty("replayed").GetBoolean() && b.GetProperty("charged").GetInt64() == 0));
        JsonElement settled = bodies.Single(b => !b.GetProperty("replayed").GetBoolean());
        Assert.Equal(16_000, settled.GetProperty("debit").GetInt64());
        Assert.Equal(34_000, settled.GetProperty("balance").GetInt64());
        Assert.Equal(2, settled.GetProperty("appliedRevision").GetInt64());

        Assert.Equal(34_000, kit.Balance(a));
        Assert.Equal(2, kit.OwnedCount(i));
        Assert.Equal(1, kit.Scalar("SELECT COUNT(*) FROM garage_quote_settlements WHERE quote_id = $q", ("$q", quoteId)));
        Assert.Equal(1, kit.Scalar("SELECT COUNT(*) FROM ledger_entries WHERE reward_type = 'part-purchase' AND account_id = $a", ("$a", a)));
        Assert.Equal(-16_000, kit.Scalar("SELECT applied_amount FROM ledger_entries WHERE idempotency_key = $k", ("$k", $"garage-quote/{a}/{quoteId}")));

        JsonElement car = Ok(await G.GetCarAsync(a, i, None));
        JsonElement applied = car.GetProperty("workspace").GetProperty("applied");
        Assert.Equal("quote:" + quoteId, applied.GetProperty("source").GetString());
        Assert.Equal(settled.GetProperty("buildHash").GetString(), car.GetProperty("appliedEvaluation").GetProperty("buildHash").GetString());

        // A later retry still replays; a new quote for the now-owned build has nothing to buy (owned parts are never bought again).
        Assert.True(Ok(await G.SettleQuoteAsync(a, i, quoteId, true, None)).GetProperty("replayed").GetBoolean());
        Fails(await G.CreateQuoteAsync(a, i, new QuoteRequest(Build: GarageTestKit.Build("TYR-T1-STREET", "BRK-T1-PADS")), None), 409, "nothing_to_buy");
        Assert.Equal(34_000, kit.Balance(a));
        // The settlement ledger is append-only.
        Assert.ThrowsAny<Exception>(() => kit.Exec("DELETE FROM garage_quote_settlements"));
    }

    [Fact]
    public async Task InsufficientFunds_LeavesWalletOwnershipAndWorkspaceUntouched()
    {
        (string a, string i, JsonElement q) = await QuotedAsync(5_000, 0, "TYR-T1-STREET", "BRK-T1-PADS");
        Assert.False(q.GetProperty("affordableNow").GetBoolean());
        long revision = kit.WorkspaceRevision(i);
        GarageReply r = await G.SettleQuoteAsync(a, i, q.GetProperty("quote").GetProperty("quoteId").GetString()!, true, None);
        Fails(r, 409, "insufficient_funds");
        AssertUntouched(a, i, 5_000, revision);
        // The planning draft is still there for later.
        Assert.True(Ok(await G.GetCarAsync(a, i, None)).GetProperty("workspace").GetProperty("draftDirty").GetBoolean());
    }

    [Fact]
    public async Task PriceChange_LeavesEverythingUntouched_AndReportsTheCurrentPrices()
    {
        (string a, string i, JsonElement q) = await QuotedAsync(50_000, 0, "TYR-T1-STREET", "BRK-T1-PADS");
        long revision = kit.WorkspaceRevision(i);
        GarageService repriced = kit.ServiceWith(GarageTestKit.WithPriceChange("TYR-T1-STREET", 9_500));
        GarageReply r = await repriced.SettleQuoteAsync(a, i, q.GetProperty("quote").GetProperty("quoteId").GetString()!, true, None);
        Fails(r, 409, "price_changed");
        Assert.Contains(GarageTestKit.Body(r).GetProperty("currentLines").EnumerateArray(),
            l => l.GetProperty("partId").GetString() == "TYR-T1-STREET" && l.GetProperty("price").GetInt64() == 9_500);
        AssertUntouched(a, i, 50_000, revision);
    }

    [Fact]
    public async Task ExpiredQuote_AndMissingConfirmation_LeaveEverythingUntouched()
    {
        (string a, string i, JsonElement q) = await QuotedAsync(50_000, 0, "TYR-T1-STREET");
        string quoteId = q.GetProperty("quote").GetProperty("quoteId").GetString()!;
        long revision = kit.WorkspaceRevision(i);
        Fails(await G.SettleQuoteAsync(a, i, quoteId, false, None), 409, "confirmation_required");
        AssertUntouched(a, i, 50_000, revision);
        kit.Clock.Advance(PurchaseQuotes.DefaultLifetime + TimeSpan.FromSeconds(1));
        Fails(await G.SettleQuoteAsync(a, i, quoteId, true, None), 409, "quote_expired");
        AssertUntouched(a, i, 50_000, revision);
        Fails(await G.SettleQuoteAsync(a, i, "q-unknown", true, None), 404, "unknown_quote");
    }

    [Fact]
    public async Task AppliedBuildChangedAfterTheQuote_IsStale_AndLockedOrUnavailablePartsCannotBeQuoted()
    {
        (string a, string i, JsonElement q) = await QuotedAsync(500_000, 0, "TYR-T1-STREET");
        kit.Grant(a, i, "BRK-T1-PADS");
        long r = kit.WorkspaceRevision(i);
        Ok(await Op(a, i, new GarageOpRequest("edit-draft", r, Build: GarageTestKit.Build("BRK-T1-PADS"))));
        Ok(await Op(a, i, new GarageOpRequest("apply", r + 1)));
        Fails(await G.SettleQuoteAsync(a, i, q.GetProperty("quote").GetProperty("quoteId").GetString()!, true, None), 409, "stale_revision");
        Assert.Equal(500_000, kit.Balance(a));
        // Act I shop: a T2 part is visible but locked, so it is not purchasable (never unlocked by spending).
        GarageReply locked = await G.CreateQuoteAsync(a, i, new QuoteRequest(Build: GarageTestKit.Build("TYR-T2-SPORT")), None);
        Fails(locked, 409, "not_purchasable");
        Assert.Contains(GarageTestKit.Body(locked).GetProperty("repairs").EnumerateArray(), x => x.GetProperty("kind").GetString() == "locked");
    }

    [Fact]
    public async Task PartsCatalogue_ShowsCompatiblePartsPricesOwnershipShopActAndRecipes()
    {
        string a = await kit.PlayerAsync(1, "V01", 0, normalCleared: 7); // frontier S08 → Act II shop
        string i = await kit.InstanceAsync(a);
        kit.Grant(a, i, "TYR-T1-STREET");
        JsonElement parts = Ok(await G.PartsAsync(a, i, None));
        Assert.Equal(2, parts.GetProperty("shopAct").GetInt32());
        var all = parts.GetProperty("slots").EnumerateArray().SelectMany(s => s.GetProperty("parts").EnumerateArray()).ToList();
        JsonElement Part(string id) => all.Single(p => p.GetProperty("partId").GetString() == id);
        Assert.True(Part("TYR-T1-STREET").GetProperty("owned").GetBoolean());
        Assert.Equal(9_000, Part("TYR-T1-STREET").GetProperty("price").GetInt64());
        Assert.True(Part("TYR-T2-SPORT").GetProperty("available").GetBoolean());
        Assert.False(Part("TYR-T3-SEMISLICK").GetProperty("available").GetBoolean());
        Assert.DoesNotContain(all, p => p.GetProperty("partId").GetString() == "DIF-T1-FWD-HELICAL"); // V01 is RWD: incompatible parts are not listed
        Assert.True(Part("GBX-T1-FINAL").GetProperty("tuning").GetArrayLength() > 0);
        JsonElement steps = parts.GetProperty("recipes").GetProperty("steps");
        JsonElement first = steps.EnumerateArray().First(s => s.GetProperty("id").GetString() == "V01-A");
        Assert.Empty(first.GetProperty("missingParts").EnumerateArray()); // the owned tyre is not listed as missing
        Assert.Contains(steps.EnumerateArray(), s => s.GetProperty("missingTotal").GetInt64() > 0);
    }
}
