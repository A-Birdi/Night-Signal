using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// The ONLINE Garage through the real control plane: REST workspace + Buy-and-Apply, loadout.set taking the server hash,
/// readiness invalidated only by that player's performance change, the start freezing the server build into the assignment,
/// Last Race Build recorded at the authorized start, and the frozen utility item paying in settlement.
/// </summary>
public sealed class GarageEndToEndTests : IDisposable
{
    const string Build = "garage-build";
    readonly TempDir dir = new();
    readonly IReadOnlyList<DevAccountFixture> accounts;

    public GarageEndToEndTests() => accounts = TestData.WriteSeed(dir.File("seed.json"), 2);

    public void Dispose() => dir.Dispose();

    static string Type(JsonElement m) => m.GetProperty("type").GetString()!;

    static JsonElement Result(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.GetRawText());
        return reply.TryGetProperty("result", out JsonElement r) ? r : default;
    }

    void Sql(string sql, params (string, object)[] args)
    {
        using var c = new SqliteConnection($"Data Source={dir.File("controlplane.db")}");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string n, object v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    static async Task<JsonElement> OkJson(HttpResponseMessage r)
    {
        string text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    static async Task<JsonElement> PollAndAckAsync(HttpClient gs, CancellationToken ct)
    {
        while (true)
        {
            HttpResponseMessage r = await gs.GetAsync($"/v1/servers/{ControlPlaneHost.ServerId}/assignments?waitSeconds=5", ct);
            r.EnsureSuccessStatusCode();
            JsonElement list = (await r.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("assignments");
            if (list.GetArrayLength() == 0) continue;
            JsonElement assignment = list[0].Clone();
            (await gs.PostAsync($"/v1/servers/{ControlPlaneHost.ServerId}/assignments/{assignment.GetProperty("matchId").GetString()}/ack", null, ct))
                .EnsureSuccessStatusCode();
            return assignment;
        }
    }

    [Fact]
    public async Task Garage_BuyAndApply_ServerLoadout_FrozenBuild_LastRaceBuild_AndUtilityIncome()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        DevAccountFixture a = accounts[0], b = accounts[1];
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), clock);
        string ta = await host.SignInAsync(a), tb = await host.SignInAsync(b);
        HttpClient ha = host.Authed(ta), hb = host.Authed(tb);
        (await ha.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night" })).EnsureSuccessStatusCode();
        (await hb.PostAsJsonAsync("/v1/me/card", new { displayName = "Ben Rainfox" })).EnsureSuccessStatusCode();
        (await ha.PostAsJsonAsync("/v1/me/starter", new { carId = "V01" })).EnsureSuccessStatusCode();
        (await hb.PostAsJsonAsync("/v1/me/starter", new { carId = "V03" })).EnsureSuccessStatusCode();
        JsonElement health = await host.CreateClient().GetFromJsonAsync<JsonElement>("/healthz");
        string contentHash = health.GetProperty("contentHash").GetString()!;
        Assert.Equal(64, health.GetProperty("garageContentHash").GetString()!.Length);

        // ---- REST Garage: auth, listing, errors
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/v1/me/garage/cars")).StatusCode);
        JsonElement cars = await OkJson(await ha.GetAsync("/v1/me/garage/cars"));
        JsonElement car = Assert.Single(cars.GetProperty("cars").EnumerateArray());
        string instance = car.GetProperty("instanceId").GetString()!;
        Assert.Equal(220, car.GetProperty("applied").GetProperty("pi").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await hb.GetAsync($"/v1/me/garage/cars/{instance}")).StatusCode); // not B's car
        HttpResponseMessage noRevision = await ha.PostAsJsonAsync($"/v1/me/garage/cars/{instance}/operations", new { op = "save-as", name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, noRevision.StatusCode);
        Assert.Equal("invalid_request", (await Json(noRevision)).GetProperty("error").GetString());

        // Test setup only: A already owns a +4 % income utility and has savings (production grants only via settled quotes).
        Sql("INSERT INTO car_part_ownership (instance_id, part_id, account_id, source, price) VALUES ($i, 'UTL-INC-4', $a, 'purchase', 0)",
            ("$i", instance), ("$a", a.AccountId));
        Sql("UPDATE wallets SET balance = 50000 WHERE account_id = $a", ("$a", a.AccountId));

        // ---- Buy-and-Apply over HTTP: draft → quote → confirm → settle once; a retry replays.
        JsonElement draft = await OkJson(await ha.PostAsJsonAsync($"/v1/me/garage/cars/{instance}/operations", new
        {
            op = "edit-draft", expectedRevision = 1,
            build = new { parts = new Dictionary<string, string> { ["tyres"] = "TYR-T1-STREET", ["brakes"] = "BRK-T1-PADS" }, utilityPartId = "UTL-INC-4" },
        }));
        Assert.Equal(2, draft.GetProperty("revision").GetInt64());
        JsonElement quote = (await OkJson(await ha.PostAsJsonAsync($"/v1/me/garage/cars/{instance}/quote", new { source = "draft" }))).GetProperty("quote");
        string quoteId = quote.GetProperty("quoteId").GetString()!;
        Assert.Equal(16_000, quote.GetProperty("total").GetInt64());
        Assert.DoesNotContain(quote.GetProperty("lines").EnumerateArray(), l => l.GetProperty("partId").GetString() == "UTL-INC-4"); // owned: not bought again
        HttpResponseMessage unconfirmed = await ha.PostAsJsonAsync($"/v1/me/garage/cars/{instance}/quote/{quoteId}/settle", new { });
        Assert.Equal(HttpStatusCode.Conflict, unconfirmed.StatusCode);
        Assert.Equal("confirmation_required", (await Json(unconfirmed)).GetProperty("error").GetString());
        JsonElement settled = await OkJson(await ha.PostAsJsonAsync($"/v1/me/garage/cars/{instance}/quote/{quoteId}/settle", new { confirm = true }));
        Assert.False(settled.GetProperty("replayed").GetBoolean());
        Assert.Equal(34_000, settled.GetProperty("balance").GetInt64());
        JsonElement replay = await OkJson(await ha.PostAsJsonAsync($"/v1/me/garage/cars/{instance}/quote/{quoteId}/settle", new { confirm = true }));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(0, replay.GetProperty("charged").GetInt64());
        Assert.Equal(34_000, (await ha.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("wallet").GetProperty("balance").GetInt64());

        JsonElement carA = await OkJson(await ha.GetAsync($"/v1/me/garage/cars/{instance}"));
        string serverHash = carA.GetProperty("appliedEvaluation").GetProperty("buildHash").GetString()!;
        int serverPi = carA.GetProperty("appliedEvaluation").GetProperty("pi").GetProperty("value").GetInt32();
        Assert.Equal(settled.GetProperty("buildHash").GetString(), serverHash);
        Assert.InRange(serverPi, 221, 299); // upgraded, still inside the S01 D cap

        // ---- Game server and convoy
        HttpClient gs = host.GameServer();
        (await gs.PostAsJsonAsync("/v1/servers/register",
            new { endpoint = new { host = "127.0.0.1", port = 7777 }, build = Build, protocol = 2, contentHash, maxMatches = 2 })).EnsureSuccessStatusCode();
        using var stopPolling = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<JsonElement> assignmentTask = PollAndAckAsync(gs, stopPolling.Token);
        string q = $"build={Build}&protocol=2&content={contentHash}";
        await using ControlClient ca = await host.ConnectAsync(ta, q);
        await using ControlClient cb = await host.ConnectAsync(tb, q);
        await ca.WaitForAsync(m => Type(m) == "hello");
        Result(await ca.RequestAsync("convoy.create", new { privacy = "invite-only" }));
        string code = Result(await ca.RequestAsync("convoy.invite.create")).GetProperty("code").GetString()!;
        Result(await cb.RequestAsync("convoy.join", new { code }));

        // loadout.set: the client's performance claim is ignored; the server hash/PI of the applied build are used.
        JsonElement la = Result(await ca.RequestAsync("loadout.set", new { carId = "V01", performanceHash = "i-am-fast", cosmeticHash = "paint-1" }));
        Assert.Equal(serverHash, la.GetProperty("performanceHash").GetString());
        Assert.Equal(serverPi, la.GetProperty("carPi").GetInt32());
        Assert.Equal(instance, la.GetProperty("instanceId").GetString());
        Assert.Equal(1, la.GetProperty("loadoutRevision").GetInt64());
        Result(await cb.RequestAsync("loadout.set", new { carId = "V03", cosmeticHash = "paint-7" })); // performanceHash is optional now

        JsonElement intent = Result(await ca.RequestAsync("intent.set", new { kind = "campaign", mode = "normal" }));
        long modeRev = intent.GetProperty("modeRevision").GetInt64();
        Result(await cb.RequestAsync("mode.ready", new { modeRevision = modeRev, ready = true }));
        Result(await ca.RequestAsync("mode.enter", new { modeRevision = modeRev }));
        clock.Advance(TimeSpan.FromSeconds(15));
        long eRev = Result(await ca.RequestAsync("event.propose", new { stageId = "S01" })).GetProperty("proposalRevision").GetInt64();
        Result(await ca.RequestAsync("event.ready", new { proposalRevision = eRev, loadoutRevision = 1, ready = true }));
        Result(await cb.RequestAsync("event.ready", new { proposalRevision = eRev, loadoutRevision = 1, ready = true }));

        // Cosmetic-only change keeps B ready; A's Garage PERFORMANCE apply unreadies only A.
        Result(await cb.RequestAsync("loadout.set", new { carId = "V03", cosmeticHash = "paint-9" }));
        JsonElement next = await OkJson(await ha.PostAsJsonAsync($"/v1/me/garage/cars/{instance}/operations", new
        {
            op = "edit-draft", expectedRevision = carA.GetProperty("workspace").GetProperty("revision").GetInt64(),
            build = new { parts = new Dictionary<string, string> { ["tyres"] = "TYR-T1-STREET" }, utilityPartId = "UTL-INC-4" },
        }));
        JsonElement applied = await OkJson(await ha.PostAsJsonAsync($"/v1/me/garage/cars/{instance}/operations",
            new { op = "apply", expectedRevision = next.GetProperty("revision").GetInt64() }));
        Assert.True(applied.GetProperty("performanceChanged").GetBoolean());
        static JsonElement Member(JsonElement st, string id) => st.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("accountId").GetString() == id);
        JsonElement state = await ca.WaitForStateAsync(s => s.GetProperty("members").EnumerateArray()
            .Any(m => m.GetProperty("accountId").GetString() == a.AccountId && m.GetProperty("loadoutRevision").GetInt64() == 2));
        Assert.False(Member(state, a.AccountId).GetProperty("eventReady").GetBoolean());
        Assert.Equal(2, Member(state, a.AccountId).GetProperty("loadoutRevision").GetInt64());
        Assert.True(Member(state, b.AccountId).GetProperty("eventReady").GetBoolean());
        Assert.Equal(1, Member(state, b.AccountId).GetProperty("loadoutRevision").GetInt64());
        Assert.Equal("stale_revision", (await ca.RequestAsync("event.ready", new { proposalRevision = eRev, loadoutRevision = 1, ready = true }))
            .GetProperty("error").GetProperty("code").GetString());
        Result(await ca.RequestAsync("event.ready", new { proposalRevision = eRev, loadoutRevision = 2, ready = true }));
        string raceHash = applied.GetProperty("workspace").GetProperty("applied").GetProperty("buildHash").GetString()!;
        long raceRevision = applied.GetProperty("workspace").GetProperty("applied").GetProperty("revision").GetInt64();

        // ---- Start: the assignment carries each entrant's frozen server build.
        Result(await ca.RequestAsync("event.start", new { proposalRevision = eRev }));
        JsonElement assignment = await assignmentTask.WaitAsync(TimeSpan.FromSeconds(30));
        string matchId = assignment.GetProperty("matchId").GetString()!;
        JsonElement entrantA = assignment.GetProperty("entrants").EnumerateArray().Single(e => e.GetProperty("accountId").GetString() == a.AccountId);
        JsonElement vb = entrantA.GetProperty("vehicleBuild");
        Assert.Equal(raceHash, entrantA.GetProperty("performanceHash").GetString());
        Assert.Equal(raceHash, vb.GetProperty("buildHash").GetString());
        Assert.Equal(instance, vb.GetProperty("instanceId").GetString());
        Assert.Equal(raceRevision, vb.GetProperty("appliedRevision").GetInt64());
        Assert.Equal("TYR-T1-STREET", vb.GetProperty("parts").GetProperty("tyres").GetString());
        Assert.Equal(4, vb.GetProperty("utility").GetProperty("incomePercent").GetInt32());
        Assert.Equal(health.GetProperty("garageContentHash").GetString(), vb.GetProperty("partsCatalogueHash").GetString());
        Assert.True(vb.GetProperty("paramsMicro").GetProperty("TyreGrip").GetInt64() > 0);
        Assert.True(vb.GetProperty("chassis").GetProperty("brakeFrontBias").GetDouble() > 0);
        await ca.WaitForAsync(m => Type(m) == "match.allocated");

        // ---- Authorized start → Last Race Build is the frozen build of this match.
        JsonElement lastRace = default;
        for (int i = 0; i < 100; i++)
        {
            JsonElement refs = (await OkJson(await ha.GetAsync($"/v1/me/garage/cars/{instance}"))).GetProperty("workspace").GetProperty("references");
            if (refs.TryGetProperty("last-race-build", out lastRace)) break;
            await Task.Delay(50);
        }
        Assert.Equal(JsonValueKind.Object, lastRace.ValueKind);
        Assert.Equal(matchId, lastRace.GetProperty("context").GetString());
        Assert.Equal(raceHash, lastRace.GetProperty("buildHash").GetString());
        Assert.Equal(raceRevision, lastRace.GetProperty("sourceAppliedRevision").GetInt64());

        // ---- Settlement pays the frozen utility item (+4 % ordinary event pay).
        string[] ai = assignment.GetProperty("aiEntrants").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Single(ai); // S01 Normal: a duel with its featured rival (stages.opposition.json)
        object Facts(string id, bool human, long seconds, int place) => new
        {
            entrantId = id, human, outcome = "Finished", finishTimeMicros = seconds * 1_000_000, placement = place, clean = false,
            checkpointFraction = 1.0, activeProgressVerified = true, activelyDroveLegalCourse = true, legalProgressMetres = 3100.0,
            rawDriftScore = 0, contractsPassed = 0, challengesCompleted = Array.Empty<string>(),
        };
        var entrants = new List<object> { Facts(a.AccountId, true, 170, 1), Facts(b.AccountId, true, 190, 3) };
        entrants.AddRange(ai.Select((id, k) => Facts(id, false, 175 + k, 2 + k * 2)));
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { matchId, contentHash, aborted = false, entrants });
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/matches/{matchId}/results") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-NightSignal-Signature",
            "sha256=" + Hashing.HmacSha256Hex(Base64UrlEncoder.DecodeBytes(assignment.GetProperty("resultsSecret").GetString()!), body));
        HttpResponseMessage results = await gs.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, results.StatusCode);
        JsonElement receipt = await ha.GetFromJsonAsync<JsonElement>($"/v1/matches/{matchId}/receipt");
        Assert.Equal(104, receipt.GetProperty("payout").GetProperty("utilityX100").GetInt32());
        PayoutBreakdown core = Economy.Compute(new PayoutFacts
        {
            AuthoredExpectedSeconds = 180, Kind = EventKind.CampaignStage, Mode = CampaignMode.Normal, Outcome = RunOutcome.Finished,
            Placement = 1, Clean = false, UtilityIncomePercent = 4,
        });
        Assert.Equal(core.EventCredits, receipt.GetProperty("payout").GetProperty("eventCredits").GetInt64());
        JsonElement receiptB = await hb.GetFromJsonAsync<JsonElement>($"/v1/matches/{matchId}/receipt");
        Assert.Equal(100, receiptB.GetProperty("payout").GetProperty("utilityX100").GetInt32());
        stopPolling.Cancel();
    }
}
