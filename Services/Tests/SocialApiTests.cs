using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// HTTP surface for Addendum 01 §5 and §9 against the real control plane (DevAuth + SQLite): course access listing and
/// purchase with Idempotency-Key, handle claim and exact @handle lookup (no e-mail, token or wallet exposed), friend
/// requests idempotent over HTTP, block, and the friend list with coarse status.
/// </summary>
public sealed class SocialApiTests : IDisposable
{
    readonly TempDir dir = new();
    readonly ControlPlaneHost host;
    readonly IReadOnlyList<DevAccountFixture> accounts;

    public SocialApiTests()
    {
        accounts = TestData.WriteSeed(dir.File("seed.json"), 3);
        host = new ControlPlaneHost(dir.Path, dir.File("seed.json"));
    }

    public void Dispose()
    {
        host.Dispose();
        dir.Dispose();
    }

    async Task<HttpClient> As(int i)
    {
        HttpClient c = host.Authed(await host.SignInAsync(accounts[i]));
        (await c.GetAsync("/v1/me")).EnsureSuccessStatusCode();
        return c;
    }

    static async Task<string> Error(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    void SetBalance(string account, long balance)
    {
        using var c = new SqliteConnection($"Data Source={dir.File("controlplane.db")}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE wallets SET balance = $b WHERE account_id = $a";
        cmd.Parameters.AddWithValue("$b", balance);
        cmd.Parameters.AddWithValue("$a", account);
        cmd.ExecuteNonQuery();
    }

    static HttpRequestMessage Buy(string course, string? key, object? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/me/courses/{course}/purchase");
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task Me_ListsStarterCourses_NeedsAHandle_AndOwnsNoPaidCourse()
    {
        JsonElement me = await (await As(0)).GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.True(me.GetProperty("needsHandle").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("handle").ValueKind);
        var owned = me.GetProperty("courses").GetProperty("owned").EnumerateArray()
            .Select(c => (c.GetProperty("courseId").GetString()!, c.GetProperty("source").GetString()!)).ToList();
        Assert.Equal(new[] { ("T00", "starter"), ("C01", "starter"), ("C02", "starter"), ("C03", "starter"), ("C04", "starter") }, owned);
        Assert.Equal("online", me.GetProperty("courses").GetProperty("domain").GetString());
        Assert.Equal(JsonValueKind.Array, me.GetProperty("music").GetProperty("owned").ValueKind);
    }

    [Fact]
    public async Task CourseCatalogue_StatesBothAlternatives()
    {
        JsonElement list = await (await As(0)).GetFromJsonAsync<JsonElement>("/v1/courses");
        JsonElement c20 = list.GetProperty("courses").EnumerateArray().Single(c => c.GetProperty("courseId").GetString() == "C20");
        Assert.Equal("purchase-or-clear", c20.GetProperty("access").GetProperty("kind").GetString());
        Assert.Equal(45_000, c20.GetProperty("access").GetProperty("price").GetInt64());
        Assert.Equal("S23", c20.GetProperty("access").GetProperty("unlockStage").GetString());
        JsonElement fp03 = list.GetProperty("courses").EnumerateArray().Single(c => c.GetProperty("courseId").GetString() == "FP03");
        Assert.Equal(("purchase-only", 63_000L), (fp03.GetProperty("access").GetProperty("kind").GetString(), fp03.GetProperty("access").GetProperty("price").GetInt64()));
        Assert.DoesNotContain("sprint", fp03.GetProperty("freeplayModes").EnumerateArray().Select(m => m.GetString()));
        JsonElement c25 = list.GetProperty("courses").EnumerateArray().Single(c => c.GetProperty("courseId").GetString() == "C25");
        Assert.Equal("reward-only", c25.GetProperty("access").GetProperty("kind").GetString());
        // Drift Attack only where the course has judged drift zones (authored/course-drift-zones.json).
        string[] Modes(string id) => list.GetProperty("courses").EnumerateArray().Single(c => c.GetProperty("courseId").GetString() == id)
            .GetProperty("freeplayModes").EnumerateArray().Select(m => m.GetString()!).ToArray();
        Assert.Contains("drift-attack", Modes("C01"));
        Assert.DoesNotContain("drift-attack", Modes("C02"));
        Assert.Contains("time-attack", Modes("C02"));
    }

    [Fact]
    public async Task CoursePurchase_OverHttp_IsIdempotent_AndShowsTheResultingBalance()
    {
        HttpClient c = await As(0);
        SetBalance(accounts[0].AccountId, 100_000);
        Assert.Equal("invalid_idempotency_key", await Error(await c.SendAsync(Buy("C20", null))));
        Assert.Equal("unknown_course", await Error(await c.SendAsync(Buy("C99", "key-000000001"))));
        Assert.Equal("price_changed", await Error(await c.SendAsync(Buy("C20", "key-000000002", new { expectedPrice = 1 }))));
        Assert.Equal("not_purchasable", await Error(await c.SendAsync(Buy("C25", "key-000000003"))));

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => c.SendAsync(Buy("C20", "key-000000004", new { expectedPrice = 45_000 }))));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        JsonElement[] bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Single(bodies, b => !b.GetProperty("replayed").GetBoolean());
        Assert.All(bodies, b => Assert.Equal(55_000, b.GetProperty("balance").GetInt64()));

        JsonElement again = await (await c.SendAsync(Buy("C20", "key-000000005"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("already_owned", 0L), (again.GetProperty("outcome").GetString(), again.GetProperty("charged").GetInt64()));
        Assert.Equal("insufficient_funds", await Error(await c.SendAsync(Buy("FP03", "key-000000006"))));
        JsonElement me = await c.GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal(55_000, me.GetProperty("wallet").GetProperty("balance").GetInt64());
        Assert.Contains(me.GetProperty("courses").GetProperty("owned").EnumerateArray(),
            x => x.GetProperty("courseId").GetString() == "C20" && x.GetProperty("source").GetString() == "purchase");
    }

    [Fact]
    public async Task Handles_ClaimCaseInsensitively_AndPublicLookupExposesNoPrivateData()
    {
        HttpClient a = await As(0), b = await As(1);
        Assert.Equal("invalid_handle", await Error(await a.PutAsJsonAsync("/v1/me/handle", new { handle = "9lives" })));
        Assert.Equal("invalid_handle", await Error(await a.PutAsJsonAsync("/v1/me/handle", new { handle = "admin" })));
        Assert.Equal("invalid_handle", await Error(await a.PutAsJsonAsync("/v1/me/handle", new { handle = "<b>x</b>" })));
        JsonElement claimed = await (await a.PutAsJsonAsync("/v1/me/handle", new { handle = "Robin_Birdi" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Robin_Birdi", claimed.GetProperty("handle").GetString());
        Assert.Equal("handle_taken", await Error(await b.PutAsJsonAsync("/v1/me/handle", new { handle = "ROBIN_birdi" })));
        await a.PostAsJsonAsync("/v1/me/card", new { displayName = "Robin", pronouns = "they/them" });

        HttpResponseMessage lookup = await b.GetAsync("/v1/players/by-handle/@robin_BIRDI");
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        string raw = await lookup.Content.ReadAsStringAsync();
        JsonElement card = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Robin_Birdi", card.GetProperty("handle").GetString());
        Assert.Equal("Robin", card.GetProperty("displayName").GetString());
        // The public card (spec §11): pronouns and progress counts, never the wallet or inventories.
        Assert.Equal("they/them", card.GetProperty("pronouns").GetString());
        Assert.Equal(0, card.GetProperty("campaign").GetProperty("normalClears").GetInt32());
        Assert.Equal(30, card.GetProperty("campaign").GetProperty("stages").GetInt32());
        Assert.Equal(0, card.GetProperty("challenges").GetProperty("completed").GetInt32());
        JsonElement byId = await b.GetFromJsonAsync<JsonElement>($"/v1/players/{accounts[0].AccountId}/card");
        Assert.Equal("they/them", byId.GetProperty("pronouns").GetString());
        Assert.DoesNotContain(accounts[0].Email, raw);
        Assert.DoesNotContain("balance", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/v1/players/by-handle/nobody_here")).StatusCode);
        JsonElement me = await a.GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.False(me.GetProperty("needsHandle").GetBoolean()); // existing account claimed a handle; nothing else reset
    }

    [Fact]
    public async Task FriendRequests_OverHttp_AreIdempotent_AndTheListShowsCoarseStatus()
    {
        HttpClient a = await As(0), b = await As(1), c = await As(2);
        await b.PutAsJsonAsync("/v1/me/handle", new { handle = "Ben_Rainfox" });
        HttpResponseMessage[] sends = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => a.PostAsJsonAsync("/v1/friends/requests", new { handle = "@ben_rainfox" })));
        Assert.All(sends, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        JsonElement bList = await b.GetFromJsonAsync<JsonElement>("/v1/friends");
        Assert.Equal(accounts[0].AccountId, bList.GetProperty("incoming").EnumerateArray().Single().GetProperty("accountId").GetString());

        string aId = accounts[0].AccountId;
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsync($"/v1/friends/requests/{aId}/accept", null)).StatusCode);
        JsonElement retry = await (await b.PostAsync($"/v1/friends/requests/{aId}/accept", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(retry.GetProperty("changed").GetBoolean());

        JsonElement aList = await a.GetFromJsonAsync<JsonElement>("/v1/friends");
        JsonElement friend = aList.GetProperty("friends").EnumerateArray().Single();
        Assert.Equal("Ben_Rainfox", friend.GetProperty("handle").GetString());
        Assert.Equal("Offline", friend.GetProperty("status").GetString()); // no control connection: not a false "online"
        Assert.False(friend.GetProperty("canRejoin").GetBoolean());
        Assert.DoesNotContain(accounts[1].Email, aList.GetRawText());

        // c blocks a: a's request fails, and the block is visible only to c.
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsync($"/v1/blocks/{aId}", null)).StatusCode);
        Assert.Equal("blocked", await Error(await a.PostAsJsonAsync("/v1/friends/requests", new { accountId = accounts[2].AccountId })));
        Assert.Single((await c.GetFromJsonAsync<JsonElement>("/v1/friends")).GetProperty("blocked").EnumerateArray());
        Assert.Equal("invalid_state", await Error(await b.DeleteAsync($"/v1/friends/requests/{aId}"))); // they are friends, not a pending request
        Assert.Equal(HttpStatusCode.OK, (await b.DeleteAsync($"/v1/friends/{aId}")).StatusCode);
    }

    [Fact]
    public async Task FriendRequests_AreRateLimited()
    {
        HttpClient a = await As(0);
        HttpStatusCode last = HttpStatusCode.OK;
        for (int i = 0; i < 21; i++)
            last = (await a.PostAsJsonAsync("/v1/friends/requests", new { accountId = accounts[1].AccountId })).StatusCode;
        Assert.Equal((HttpStatusCode)429, last);
    }
}
