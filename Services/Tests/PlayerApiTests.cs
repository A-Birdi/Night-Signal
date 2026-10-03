using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>/v1/me bootstrap: card rules, starter-once, purchases (validation, funds, exactly-once over HTTP).</summary>
public sealed class PlayerApiTests : IDisposable
{
    readonly TempDir dir = new();
    readonly ControlPlaneHost host;
    readonly IReadOnlyList<DevAccountFixture> accounts;

    public PlayerApiTests()
    {
        accounts = TestData.WriteSeed(dir.File("seed.json"), 1);
        host = new ControlPlaneHost(dir.Path, dir.File("seed.json"));
    }

    public void Dispose()
    {
        host.Dispose();
        dir.Dispose();
    }

    async Task<HttpClient> Me()
    {
        HttpClient c = host.Authed(await host.SignInAsync(accounts[0]));
        (await c.GetAsync("/v1/me")).EnsureSuccessStatusCode(); // creates the account and wallet rows
        return c;
    }

    static async Task<string> Error(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    void SetBalance(long balance)
    {
        using var c = new SqliteConnection($"Data Source={dir.File("controlplane.db")}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE wallets SET balance = $b";
        cmd.Parameters.AddWithValue("$b", balance);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task NewAccount_BootstrapDocument()
    {
        JsonElement me = await (await Me()).GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal(JsonValueKind.Null, me.GetProperty("card").ValueKind);
        Assert.Equal(0, me.GetProperty("wallet").GetProperty("balance").GetInt64());
        Assert.Equal(1, me.GetProperty("campaign").GetProperty("normalFrontier").GetInt32());
        Assert.False(me.GetProperty("campaign").GetProperty("hardUnlocked").GetBoolean());
        Assert.Equal("New Signal", me.GetProperty("rank").GetProperty("name").GetString());
        Assert.Equal(0, me.GetProperty("rank").GetProperty("rankPoints").GetInt32());
    }

    [Fact]
    public async Task Card_ValidatesNames_AndUsesRevisions()
    {
        HttpClient c = await Me();
        Assert.Equal("invalid_display_name", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "<color=#f00>Boss</color>" })));
        Assert.Equal("invalid_display_name", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "ab" })));
        JsonElement created = await (await c.PostAsJsonAsync("/v1/me/card", new { displayName = "  Night   Runner " })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Night Runner", created.GetProperty("displayName").GetString());
        Assert.Equal(1, created.GetProperty("revision").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Night Runner 2", revision = 1 })).StatusCode);
        Assert.Equal("revision_conflict", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Stale Edit", revision = 1 })));
    }

    [Fact]
    public async Task Card_Appearance_ValidatedStoredAndKeptAcrossNameEdits()
    {
        HttpClient c = await Me();
        var look = new
        {
            height = 1.7, build = "athletic", skin = "#c68e63", hair = "ponytail", hairColour = "#3B2A20", outfit = "bomber",
            sleeves = "long", primary = "#2F4A3A", secondary = "#F2F0EA", accent = "#E0B040", lower = "cargo", lowerColour = "#3A3A44",
            shoes = "boots", shoeColour = "#161616", accessories = new[] { "cap", "watch" }, face = "grin", id = "R40",
        };
        JsonElement saved = await (await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", look, pronouns = " they/them " })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ponytail", saved.GetProperty("look").GetProperty("hair").GetString());
        Assert.Equal("#C68E63", saved.GetProperty("look").GetProperty("skin").GetString()); // canonical: colours upper-case
        Assert.Equal("", saved.GetProperty("look").GetProperty("id").GetString());         // the account is the id
        Assert.Equal("they/them", saved.GetProperty("pronouns").GetString());

        // Refused: words the builder does not know, too many accessories, markup in pronouns — nothing changes.
        Assert.Equal("invalid_look", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", look = new { hair = "mohawk" } })));
        Assert.Equal("invalid_look", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", look = new { accessories = new[] { "cap", "watch", "scarf", "pin", "belt" } } })));
        Assert.Equal("invalid_look", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", look = "tall" })));
        Assert.Equal("invalid_pronouns", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", pronouns = "<b>she</b>" })));

        // A name-only edit keeps the look and pronouns; null clears the look back to the default.
        await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Nightfall" });
        JsonElement card = (await c.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("card");
        Assert.Equal("Aki Nightfall", card.GetProperty("displayName").GetString());
        Assert.Equal("athletic", card.GetProperty("look").GetProperty("build").GetString());
        Assert.Equal(2, card.GetProperty("look").GetProperty("accessories").GetArrayLength());
        Assert.Equal("they/them", card.GetProperty("pronouns").GetString());
        await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Nightfall", look = (object?)null });
        card = (await c.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("card");
        Assert.Equal(JsonValueKind.Null, card.GetProperty("look").ValueKind);
    }

    void Own(string cosmeticId)
    {
        using var c = new SqliteConnection($"Data Source={dir.File("controlplane.db")}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO cosmetics_owned (account_id, cosmetic_id, source) SELECT account_id, $c, 'test' FROM accounts";
        cmd.Parameters.AddWithValue("$c", cosmeticId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Card_Style_RewardsOnlyWhenOwned_PublicOnTheCard_AndKeptAcrossEdits()
    {
        HttpClient c = await Me();
        (await c.PostAsJsonAsync("/v1/me/starter", new { carId = "V02" })).EnsureSuccessStatusCode();
        var style = new { background = "tea-rows", frame = "double", motif = "lantern", title = "night-driver", layout = "standard", region = "JP", preferredCar = "V02" };
        JsonElement saved = await (await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", style })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tea-rows", saved.GetProperty("style").GetProperty("background").GetString());
        Assert.Equal("JP", saved.GetProperty("style").GetProperty("region").GetString());

        // Refused: a reward not owned yet (named), an unknown region, a car not owned, an unknown member — nothing changes.
        HttpResponseMessage locked = await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", style = new { background = "workshop-grid", frame = "thin", motif = "none", title = "none", layout = "standard" } });
        JsonElement why = await locked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_card_style", why.GetProperty("error").GetString());
        Assert.Contains("Not owned yet: Workshop Grid Background.", why.GetProperty("message").GetString());
        Assert.Equal("invalid_card_style", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", style = new { background = "night", frame = "thin", motif = "none", title = "none", layout = "standard", region = "ZZ" } })));
        Assert.Equal("invalid_card_style", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", style = new { background = "night", frame = "thin", motif = "none", title = "none", layout = "standard", preferredCar = "V09" } })));
        Assert.Equal("invalid_card_style", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", style = new { background = "night", glitter = true } })));

        // Once owned (a challenge reward), the item can be worn; the public card shows it; a name edit keeps it.
        Own("COS-CH46");
        JsonElement worn = await (await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", style = new { background = "workshop-grid", frame = "double", motif = "lantern", title = "night-driver", layout = "standard", region = "JP", preferredCar = "V02" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workshop-grid", worn.GetProperty("style").GetProperty("background").GetString());
        await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Nightfall" });
        JsonElement me = await c.GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal("workshop-grid", me.GetProperty("card").GetProperty("style").GetProperty("background").GetString());
        string id = me.GetProperty("accountId").GetString()!;
        JsonElement pub = await c.GetFromJsonAsync<JsonElement>($"/v1/players/{id}/card");
        Assert.Equal("workshop-grid", pub.GetProperty("style").GetProperty("background").GetString());
        Assert.Equal("V02", pub.GetProperty("style").GetProperty("preferredCar").GetString());
        // null: back to the catalogue's default style.
        await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Nightfall", style = (object?)null });
        Assert.Equal(JsonValueKind.Null, (await c.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("card").GetProperty("style").ValueKind);
    }

    [Fact]
    public async Task Card_WardrobeAndAvatar_OnlyOwnedRewards_OnePerPlace_PublicOnTheCard()
    {
        HttpClient c = await Me();
        var jacket = new { build = "athletic", outfit = "hoodie", wardrobe = new[] { "signal-track-jacket" } };
        HttpResponseMessage locked = await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", look = jacket });
        JsonElement why = await locked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_look", why.GetProperty("error").GetString());
        Assert.Contains("Not owned yet: Signal Track Jacket.", why.GetProperty("message").GetString());
        Assert.Equal("invalid_look", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", look = new { wardrobe = new[] { "no-such-coat" } } })));

        // Earned (challenge rewards CH33 and CH39): the jacket is worn and public; two jackets at once are refused.
        Own("COS-CH33");
        Own("COS-CH39");
        JsonElement saved = await (await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", look = jacket })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("signal-track-jacket", saved.GetProperty("look").GetProperty("wardrobe")[0].GetString());
        HttpResponseMessage twice = await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", look = new { wardrobe = new[] { "signal-track-jacket", "harbour-marshal-coat" } } });
        Assert.Contains("worn in the same place", (await twice.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        // The avatar emblem (accessory_or_avatar rewards CH64…CH75) is part of the card style, owned or refused.
        var ghost = new { background = "night", frame = "thin", motif = "none", title = "none", layout = "standard", avatar = "ghostline" };
        HttpResponseMessage noAvatar = await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", style = ghost });
        Assert.Contains("Not owned yet: Ghostline Avatar Icon.", (await noAvatar.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Own("COS-CH68");
        (await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", style = ghost })).EnsureSuccessStatusCode();
        string id = (await c.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("accountId").GetString()!;
        JsonElement pub = await c.GetFromJsonAsync<JsonElement>($"/v1/players/{id}/card");
        Assert.Equal("ghostline", pub.GetProperty("style").GetProperty("avatar").GetString());
        Assert.Equal("signal-track-jacket", (await c.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("card").GetProperty("look").GetProperty("wardrobe")[0].GetString());
    }

    void Settled(string matchId, string receiptJson)
    {
        using var c = new SqliteConnection($"Data Source={dir.File("controlplane.db")}");
        c.Open();
        using SqliteCommand m = c.CreateCommand();
        m.CommandText = "INSERT OR IGNORE INTO matches (match_id, convoy_id, server_id, config_json, results_secret, state) VALUES ($m, 'cv', 'srv', '{}', 's', 'settled')";
        m.Parameters.AddWithValue("$m", matchId);
        m.ExecuteNonQuery();
        using SqliteCommand r = c.CreateCommand();
        r.CommandText = "INSERT INTO match_results (match_id, account_id, receipt_json) SELECT $m, account_id, $r FROM accounts";
        r.Parameters.AddWithValue("$m", matchId);
        r.Parameters.AddWithValue("$r", receiptJson);
        r.ExecuteNonQuery();
    }

    [Fact]
    public async Task Records_DriftAttack_KeepsTheBestRawScore_AndCanBeShowcased()
    {
        HttpClient c = await Me();
        Settled("m_drift1", "{\"eventKind\":\"FreeplayDriftAttack\",\"courseId\":\"C01\",\"outcome\":\"Finished\",\"finishTimeMs\":120444,\"rawDriftScore\":12000}");
        Settled("m_drift2", "{\"eventKind\":\"FreeplayDriftAttack\",\"courseId\":\"C01\",\"outcome\":\"Finished\",\"finishTimeMs\":110000,\"rawDriftScore\":15500}");
        Settled("m_drift3", "{\"eventKind\":\"FreeplayDriftAttack\",\"courseId\":\"C01\",\"outcome\":\"Finished\",\"finishTimeMs\":100000,\"rawDriftScore\":9000}");
        Settled("m_drift_old", "{\"eventKind\":\"FreeplayDriftAttack\",\"courseId\":\"C04\",\"outcome\":\"Finished\",\"finishTimeMs\":100000}");
        JsonElement records = (await c.GetFromJsonAsync<JsonElement>("/v1/me/records")).GetProperty("records");
        var byKey = records.EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!, r => r.GetProperty("value").GetString());
        Assert.Equal("15,500 raw", byKey["course:C01:drift-attack"]); // the highest score, not the fastest run
        Assert.False(byKey.ContainsKey("course:C04:drift-attack"), "a receipt without a stored score is no drift record");
        Assert.False(byKey.Keys.Any(k => k.StartsWith("course:C01:sprint")), "a Drift Attack is not a sprint time");
        JsonElement saved = await (await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", showcase = new[] { "course:C01:drift-attack" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, saved.GetProperty("showcase").GetArrayLength());
    }

    [Fact]
    public async Task Card_Showcase_OwnRecordsOnly_PublicWithCurrentValues()
    {
        HttpClient c = await Me();
        Settled("m_sprint1", "{\"eventKind\":\"FreeplaySprint\",\"courseId\":\"C01\",\"outcome\":\"Finished\",\"finishTimeMs\":151408}");
        Settled("m_stage", "{\"eventKind\":\"CampaignStage\",\"stageId\":\"S07\",\"mode\":\"normal\",\"courseId\":\"C04\",\"outcome\":\"Finished\",\"finishTimeMs\":190329}");
        Settled("m_dnf", "{\"eventKind\":\"FreeplaySprint\",\"courseId\":\"C02\",\"outcome\":\"DnfTimeout\",\"finishTimeMs\":null}");
        JsonElement records = (await c.GetFromJsonAsync<JsonElement>("/v1/me/records")).GetProperty("records");
        var byKey = records.EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!, r => r.GetProperty("value").GetString());
        Assert.Equal("2:31.408", byKey["course:C01:sprint"]);
        Assert.Equal("3:10.329", byKey["stage:S07:normal"]);
        Assert.False(byKey.ContainsKey("course:C02:sprint"), "an unfinished run is no record");

        JsonElement saved = await (await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", showcase = new[] { "stage:S07:normal", "course:C01:sprint" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, saved.GetProperty("showcase").GetArrayLength());
        Assert.Equal("invalid_showcase", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", showcase = new[] { "course:C09:sprint" } })));
        Assert.Equal("invalid_showcase", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", showcase = new[] { "stage:S07:normal", "stage:S07:normal" } })));
        Assert.Equal("invalid_showcase", await Error(await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", showcase = new[] { "a", "b", "c", "d" } })));

        // The public card: the chosen records in the owner's order with their current values; a better time shows at once.
        string id = (await c.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("accountId").GetString()!;
        JsonElement pub = await c.GetFromJsonAsync<JsonElement>($"/v1/players/{id}/card");
        Assert.Equal(new[] { "stage:S07:normal", "course:C01:sprint" }, pub.GetProperty("showcase").EnumerateArray().Select(r => r.GetProperty("key").GetString()).ToArray());
        Settled("m_sprint2", "{\"eventKind\":\"FreeplaySprint\",\"courseId\":\"C01\",\"outcome\":\"Finished\",\"finishTimeMs\":149000}");
        pub = await c.GetFromJsonAsync<JsonElement>($"/v1/players/{id}/card");
        Assert.Equal("2:29.000", pub.GetProperty("showcase")[1].GetProperty("value").GetString());
        await c.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", showcase = Array.Empty<string>() });
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>($"/v1/players/{id}/card")).GetProperty("showcase").GetArrayLength());
    }

    [Fact]
    public async Task Starter_OnlyStarterCars_OnlyOnce()
    {
        HttpClient c = await Me();
        Assert.Equal("invalid_starter", await Error(await c.PostAsJsonAsync("/v1/me/starter", new { carId = "V04" })));
        JsonElement first = await (await c.PostAsJsonAsync("/v1/me/starter", new { carId = "V02" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12_000, first.GetProperty("balance").GetInt64());
        JsonElement retry = await (await c.PostAsJsonAsync("/v1/me/starter", new { carId = "V02" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(retry.GetProperty("replayed").GetBoolean());
        Assert.Equal("starter_already_claimed", await Error(await c.PostAsJsonAsync("/v1/me/starter", new { carId = "V01" })));
        JsonElement me = await c.GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal(12_000, me.GetProperty("wallet").GetProperty("balance").GetInt64());
        Assert.Single(me.GetProperty("ownedCars").EnumerateArray());
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("\"NaN\"")]
    [InlineData("\"42000\"")]
    [InlineData("1e308")]
    [InlineData("350001")]
    public async Task Purchase_RejectsMalformedPrices(string expectedPriceJson)
    {
        HttpClient c = await Me();
        SetBalance(1_000_000);
        string json = $"{{\"idempotencyKey\":\"key-00000001\",\"itemKind\":\"car\",\"itemId\":\"V04\",\"expectedPrice\":{expectedPriceJson}}}";
        HttpResponseMessage r = await c.PostAsync("/v1/me/purchases", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(1_000_000, (await c.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("wallet").GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Purchase_ChargesTheCataloguePrice_NotAClientPrice()
    {
        HttpClient c = await Me();
        SetBalance(100_000);
        Assert.Equal("price_changed", await Error(await c.PostAsJsonAsync("/v1/me/purchases",
            new { idempotencyKey = "key-00000002", itemKind = "car", itemId = "V04", expectedPrice = 1 })));
        HttpResponseMessage ok = await c.PostAsJsonAsync("/v1/me/purchases", new { idempotencyKey = "key-00000003", itemKind = "car", itemId = "V04", price = 1 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(100_000 - 42_000, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Purchase_WithInsufficientFunds_IsRejected()
    {
        HttpClient c = await Me();
        await c.PostAsJsonAsync("/v1/me/starter", new { carId = "V01" }); // 12,000 < 42,000
        Assert.Equal("insufficient_funds", await Error(await c.PostAsJsonAsync("/v1/me/purchases",
            new { idempotencyKey = "key-00000004", itemKind = "car", itemId = "V04", expectedPrice = 42_000 })));
        Assert.Equal(Limits.StarterGrantCredits, (await c.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("wallet").GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task ConcurrentDuplicatePurchaseRequests_DebitOnce()
    {
        HttpClient c = await Me();
        SetBalance(100_000);
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            c.PostAsJsonAsync("/v1/me/purchases", new { idempotencyKey = "same-key-0001", itemKind = "car", itemId = "V05", expectedPrice = 65_000 })));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        JsonElement[] bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Single(bodies, b => !b.GetProperty("replayed").GetBoolean());
        JsonElement me = await c.GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal(35_000, me.GetProperty("wallet").GetProperty("balance").GetInt64());
        Assert.Single(me.GetProperty("ownedCars").EnumerateArray(), x => x.GetProperty("carId").GetString() == "V05");
    }
}
