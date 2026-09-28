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
