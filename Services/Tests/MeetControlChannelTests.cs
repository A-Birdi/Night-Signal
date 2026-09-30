using System.Net.Http.Json;
using System.Text.Json;
using NightSignal.ControlPlane.Control;
using NightSignal.Core.Meet;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// The meet over the real <c>/v1/control</c> WebSocket (in-process host, DevAuth, SQLite, the real meet pump at ≈10 Hz,
/// a manual clock for the room rules): public allocation, arrivals as keyed events, pose validation, emote ID + start,
/// quick chat, likes, leave vs disconnect (and a quiet rejoin), the six-human cap, friends' and convoy meets, blocks, and
/// the shared boombox's range and ownership rules.
/// </summary>
public sealed class MeetControlChannelTests : IDisposable
{
    const string Query = "build=b&protocol=2&content=c";
    readonly TempDir dir = new();
    readonly IReadOnlyList<DevAccountFixture> accounts;

    public MeetControlChannelTests() => accounts = TestData.WriteSeed(dir.File("seed.json"), 8);

    public void Dispose() => dir.Dispose();

    static string Type(JsonElement m) => m.GetProperty("type").GetString()!;

    static void AssertOk(JsonElement reply) =>
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.TryGetProperty("error", out JsonElement e) ? e.GetRawText() : reply.GetRawText());

    static string Error(JsonElement reply) => reply.GetProperty("error").GetProperty("code").GetString()!;

    static JsonElement Result(JsonElement reply) => reply.GetProperty("result");

    sealed record Visitor(ControlClient Control, HttpClient Http, string Token, string AccountId, string Name);

    async Task<Visitor> Arrive(ControlPlaneHost host, int i, string name, string car = "V01")
    {
        string token = await host.SignInAsync(accounts[i]);
        HttpClient http = host.Authed(token);
        (await http.PostAsJsonAsync("/v1/me/card", new { displayName = name })).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync("/v1/me/starter", new { carId = car })).EnsureSuccessStatusCode();
        ControlClient control = await host.ConnectAsync(token, Query);
        return new Visitor(control, http, token, accounts[i].AccountId, name);
    }

    static JsonElement Member(JsonElement state, string accountId)
    {
        try { return state.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("accountId").GetString() == accountId); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { throw new Xunit.Sdk.XunitException($"member {accountId} not in {state.GetRawText()}"); }
    }

    static bool HasMember(JsonElement state, string accountId) =>
        state.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("accountId").GetString() == accountId);

    static IEnumerable<JsonElement> Events(JsonElement state) => state.GetProperty("events").EnumerateArray();

    static async Task<JsonElement> WaitForMeet(ControlClient c, Func<JsonElement, bool> match) =>
        (await c.WaitForAsync(m => Type(m) == "meet.state" && match(m.GetProperty("payload")))).GetProperty("payload");

    [Fact]
    public async Task PublicMeet_ArriveWalkEmoteChatLike_ThenLeaveFadesOut()
    {
        var clock = new ManualClock();
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), clock);
        Visitor a = await Arrive(host, 0, "Aki Night"), b = await Arrive(host, 1, "Ben Rainfox", "V03");
        JsonElement ja = await a.Control.RequestAsync("meet.join", new { kind = "public" });
        AssertOk(ja);
        JsonElement jb = await b.Control.RequestAsync("meet.join", new { kind = "public" });
        AssertOk(jb);
        string room = Result(ja).GetProperty("roomId").GetString()!;
        Assert.Equal(room, Result(jb).GetProperty("roomId").GetString());
        Assert.Equal("arriving", Member(Result(ja).GetProperty("state"), a.AccountId).GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, Member(Result(ja).GetProperty("state"), a.AccountId).GetProperty("look").ValueKind);

        clock.Advance(TimeSpan.FromSeconds(3.5));
        JsonElement arrivedA = Result(await a.Control.RequestAsync("meet.arrived"));
        Assert.True(arrivedA.GetProperty("arrived").GetBoolean());
        AssertOk(await b.Control.RequestAsync("meet.arrived"));
        JsonElement seen = await WaitForMeet(b.Control, s => Events(s).Any(e => e.GetProperty("kind").GetString() == "arrived" && e.GetProperty("accountId").GetString() == a.AccountId));
        Assert.Equal("present", Member(seen, a.AccountId).GetProperty("state").GetString());
        int bayA = Member(seen, a.AccountId).GetProperty("bay").GetInt32(), bayB = Member(seen, b.AccountId).GetProperty("bay").GetInt32();
        Assert.NotEqual(bayA, bayB);
        Assert.DoesNotContain(bayA - 1, MeetLayout.AmbienceBays);

        // A walking step is accepted; a teleport is corrected back to the last good pose.
        float x = arrivedA.GetProperty("x").GetSingle(), z = arrivedA.GetProperty("z").GetSingle();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        float step = x < 0 ? 0.6f : -0.6f;
        Assert.Equal("Accepted", Result(await a.Control.RequestAsync("meet.move", new { x = x + step, z, yaw = 90f, speed = 1.4f, seq = 1 })).GetProperty("status").GetString());
        clock.Advance(TimeSpan.FromMilliseconds(100));
        JsonElement corrected = Result(await a.Control.RequestAsync("meet.move", new { x = 0f, z = 20f, yaw = 0f, speed = 1.4f, seq = 2 }));
        Assert.Equal("Corrected", corrected.GetProperty("status").GetString());
        Assert.Equal(x + step, corrected.GetProperty("x").GetSingle(), 3);
        JsonElement poses = await b.Control.WaitForAsync(m => Type(m) == "meet.poses");
        Assert.Contains(poses.GetProperty("payload").GetProperty("poses").EnumerateArray(), p => p.GetProperty("accountId").GetString() == a.AccountId);

        // An emote replicates as ID + server start time; quick chat as a phrase index; a like is cosmetic.
        JsonElement wave = Result(await a.Control.RequestAsync("meet.emote", new { emote = "Wave" }));
        long start = wave.GetProperty("startMs").GetInt64();
        JsonElement emoted = await WaitForMeet(b.Control, s => Member(s, a.AccountId).GetProperty("emote").ValueKind == JsonValueKind.String);
        Assert.Equal("Wave", Member(emoted, a.AccountId).GetProperty("emote").GetString());
        Assert.Equal(start, Member(emoted, a.AccountId).GetProperty("emoteStartMs").GetInt64());
        Assert.Equal("invalid_request", Error(await a.Control.RequestAsync("meet.emote", new { emote = "Moonwalk" })));
        AssertOk(await a.Control.RequestAsync("meet.chat", new { index = 1 }));
        await WaitForMeet(b.Control, s => Member(s, a.AccountId).GetProperty("chat").ValueKind == JsonValueKind.Object);
        Assert.Equal("chat_refused", Error(await a.Control.RequestAsync("meet.chat", new { index = 999 })));
        JsonElement liked = Result(await b.Control.RequestAsync("meet.like", new { accountId = a.AccountId }));
        Assert.Equal(1, liked.GetProperty("likes").GetInt32());
        Assert.True(liked.GetProperty("cosmeticOnly").GetBoolean());

        // A Player Card look reaches the other visitors (after a rejoin the room holds the new look).
        (await a.Http.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night", look = new { hair = "bun", primary = "#5A3A7A" } })).EnsureSuccessStatusCode();
        AssertOk(await a.Control.RequestAsync("meet.leave"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForMeet(b.Control, s => !HasMember(s, a.AccountId));
        AssertOk(await a.Control.RequestAsync("meet.join", new { kind = "public" }));
        JsonElement styled = await WaitForMeet(b.Control, s => HasMember(s, a.AccountId) && Member(s, a.AccountId).GetProperty("look").ValueKind == JsonValueKind.Object);
        Assert.Equal("bun", Member(styled, a.AccountId).GetProperty("look").GetProperty("hair").GetString());
        clock.Advance(TimeSpan.FromSeconds(4));
        AssertOk(await a.Control.RequestAsync("meet.arrived"));

        // Leaving: "departed" once, then the member fades out and the bay is free.
        AssertOk(await a.Control.RequestAsync("meet.leave"));
        await WaitForMeet(b.Control, s => Events(s).Any(e => e.GetProperty("kind").GetString() == "departed" && e.GetProperty("accountId").GetString() == a.AccountId));
        clock.Advance(TimeSpan.FromSeconds(1));
        JsonElement after = await WaitForMeet(b.Control, s => !HasMember(s, a.AccountId));
        Assert.Single(after.GetProperty("members").EnumerateArray());
    }

    [Fact]
    public async Task AVisitorOutsideAConvoy_ArrivesInTheLiveryAppliedInTheGarage()
    {
        var clock = new ManualClock();
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), clock);
        Visitor a = await Arrive(host, 0, "Aki Night");
        JsonElement stock = Result(await a.Control.RequestAsync("meet.join", new { kind = "public" }));
        Assert.Equal(JsonValueKind.Null, Member(stock.GetProperty("state"), a.AccountId).GetProperty("livery").ValueKind);
        AssertOk(await a.Control.RequestAsync("meet.leave"));

        // In the Garage: a livery on the first car.
        JsonElement cars = await (await a.Http.GetAsync("/v1/me/garage/cars")).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement car = cars.GetProperty("cars")[0];
        string instance = car.GetProperty("instanceId").GetString()!;
        HttpResponseMessage applied = await a.Http.PostAsJsonAsync($"/v1/me/garage/cars/{instance}/operations", new
        {
            op = "livery-apply", expectedRevision = car.GetProperty("revision").GetInt64(),
            liveryJson = NightSignal.Core.Customization.LiveryJson.ToCanonicalJson(GarageTestKit.Livery()),
        });
        applied.EnsureSuccessStatusCode();

        // Back at the meet, still outside any convoy: the room admits the car with that livery.
        clock.Advance(TimeSpan.FromSeconds(2));
        JsonElement dressed = Result(await a.Control.RequestAsync("meet.join", new { kind = "public" }));
        JsonElement me = Member(dressed.GetProperty("state"), a.AccountId);
        Assert.Equal(JsonValueKind.String, me.GetProperty("livery").ValueKind);
        Assert.Equal(car.GetProperty("carId").GetString(), me.GetProperty("carId").GetString());
    }

    [Fact]
    public async Task ADroppedConnection_IsDisconnected_ThenRejoinsQuietlyInTheSameBay()
    {
        var clock = new ManualClock();
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), clock);
        Visitor a = await Arrive(host, 0, "Aki Night"), b = await Arrive(host, 1, "Ben Rainfox");
        AssertOk(await a.Control.RequestAsync("meet.join", new { kind = "public" }));
        JsonElement jb = Result(await b.Control.RequestAsync("meet.join", new { kind = "public" }));
        int bay = Member(jb.GetProperty("state"), b.AccountId).GetProperty("bay").GetInt32();
        clock.Advance(TimeSpan.FromSeconds(4));
        AssertOk(await a.Control.RequestAsync("meet.arrived"));
        AssertOk(await b.Control.RequestAsync("meet.arrived"));
        await WaitForMeet(a.Control, s => Events(s).Count(e => e.GetProperty("kind").GetString() == "arrived") == 2);

        await b.Control.DisposeAsync();
        JsonElement lost = await WaitForMeet(a.Control, s => Events(s).Any(e => e.GetProperty("kind").GetString() == "disconnected"));
        Assert.Equal("disconnected", Member(lost, b.AccountId).GetProperty("state").GetString());
        Assert.DoesNotContain(Events(lost), e => e.GetProperty("kind").GetString() == "departed");

        ControlClient again = await host.ConnectAsync(b.Token, Query);
        JsonElement rj = Result(await again.RequestAsync("meet.join", new { kind = "public" }));
        Assert.Equal("Rejoined", rj.GetProperty("status").GetString());
        Assert.Equal(bay, Member(rj.GetProperty("state"), b.AccountId).GetProperty("bay").GetInt32());
        Assert.Equal(Member(jb.GetProperty("state"), b.AccountId).GetProperty("generation").GetInt64(),
            Member(rj.GetProperty("state"), b.AccountId).GetProperty("generation").GetInt64()); // a reconnect is the same visit
        // A snapshot after the drop with Ben present again. WaitForMeet scans every snapshot Aki has received from the first:
        // Aki's earliest can predate Ben's join (then Member() would throw), and Ben's pre-drop "present" must not count.
        long lostRevision = lost.GetProperty("revision").GetInt64();
        JsonElement back = await WaitForMeet(a.Control, s => s.GetProperty("revision").GetInt64() > lostRevision
            && HasMember(s, b.AccountId) && Member(s, b.AccountId).GetProperty("state").GetString() == "present");
        Assert.Equal(2, Events(back).Count(e => e.GetProperty("kind").GetString() == "arrived"));

        // Leaving because the convoy's event allocated is worded apart from both "left" and "disconnected".
        AssertOk(await again.RequestAsync("meet.leave", new { reason = "race" }));
        JsonElement raced = await WaitForMeet(a.Control, s => Events(s).Any(e => e.GetProperty("kind").GetString() == "lefttorace" && e.GetProperty("accountId").GetString() == b.AccountId));
        Assert.Equal("leaving", Member(raced, b.AccountId).GetProperty("state").GetString());
        Assert.DoesNotContain(Events(raced), e => e.GetProperty("kind").GetString() == "departed");
        await again.DisposeAsync();
    }

    [Fact]
    public async Task SixPerRoom_FriendsFollow_BlocksSeparate_ConvoysTogether()
    {
        var clock = new ManualClock();
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), clock);
        var v = new List<Visitor>();
        for (int i = 0; i < 8; i++) v.Add(await Arrive(host, i, $"Driver {(char)('A' + i)}"));
        var roomOf = new Dictionary<string, string>();
        for (int i = 0; i < 6; i++)
            roomOf[v[i].AccountId] = Result(await v[i].Control.RequestAsync("meet.join", new { kind = "public" })).GetProperty("roomId").GetString()!;
        Assert.Single(roomOf.Values.Distinct());
        // The seventh goes to a new public room rather than a seventh place (D02).
        string seventh = Result(await v[6].Control.RequestAsync("meet.join", new { kind = "public" })).GetProperty("roomId").GetString()!;
        Assert.NotEqual(roomOf[v[0].AccountId], seventh);

        // Friends: v7 befriends v0; joining v0's full meet says so; an invitation holds a place once one frees up.
        HttpResponseMessage req = await v[7].Http.PostAsJsonAsync("/v1/friends/requests", new { accountId = v[0].AccountId });
        req.EnsureSuccessStatusCode();
        (await v[0].Http.PostAsync($"/v1/friends/requests/{v[7].AccountId}/accept", null)).EnsureSuccessStatusCode();
        Assert.Equal("not_friends", Error(await v[6].Control.RequestAsync("meet.join", new { kind = "friend", friendAccountId = v[0].AccountId })));
        Assert.Equal("meet_full", Error(await v[7].Control.RequestAsync("meet.join", new { kind = "friend", friendAccountId = v[0].AccountId })));
        AssertOk(await v[5].Control.RequestAsync("meet.leave"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await v[0].Control.WaitForAsync(m => Type(m) == "meet.state" && m.GetProperty("payload").GetProperty("members").GetArrayLength() == 5);
        JsonElement invite = await v[0].Control.RequestAsync("meet.invite", new { accountId = v[7].AccountId });
        AssertOk(invite);
        JsonElement invited = await v[7].Control.WaitForAsync(m => Type(m) == "meet.invited");
        Assert.Equal(v[0].AccountId, invited.GetProperty("payload").GetProperty("fromAccountId").GetString());
        // The reservation holds the place against strangers…
        AssertOk(await v[6].Control.RequestAsync("meet.leave"));
        string other = Result(await v[6].Control.RequestAsync("meet.join", new { kind = "public" })).GetProperty("roomId").GetString()!;
        Assert.NotEqual(roomOf[v[0].AccountId], other);
        // …and the friend takes it.
        JsonElement friendJoin = Result(await v[7].Control.RequestAsync("meet.join", new { kind = "friend", friendAccountId = v[0].AccountId }));
        Assert.Equal(roomOf[v[0].AccountId], friendJoin.GetProperty("roomId").GetString());
        Assert.Equal(Result(invite).GetProperty("bay").GetInt32(), Member(friendJoin.GetProperty("state"), v[7].AccountId).GetProperty("bay").GetInt32());

        // Blocks: v6 blocks v1; v6 is never put in v1's public room.
        (await v[6].Http.PutAsync($"/v1/blocks/{v[1].AccountId}", null)).EnsureSuccessStatusCode();
        AssertOk(await v[6].Control.RequestAsync("meet.leave"));
        AssertOk(await v[1].Control.RequestAsync("meet.leave"));
        AssertOk(await v[5].Control.RequestAsync("meet.join", new { kind = "public" })); // fills v0's room back up to six
        JsonElement blockerJoin = await v[1].Control.RequestAsync("meet.join", new { kind = "public" });
        AssertOk(blockerJoin);
        string roomOfBlocker = Result(blockerJoin).GetProperty("roomId").GetString()!;
        string roomOfBlocked = Result(await v[6].Control.RequestAsync("meet.join", new { kind = "public" })).GetProperty("roomId").GetString()!;
        Assert.NotEqual(roomOfBlocker, roomOfBlocked);
        Assert.Equal("not_available", Error(await v[6].Control.RequestAsync("meet.like", new { accountId = v[1].AccountId })));
    }

    [Fact]
    public async Task ConvoyMeet_KeepsTheConvoyTogether_OnOneSide()
    {
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), new ManualClock());
        Visitor a = await Arrive(host, 0, "Aki Night"), b = await Arrive(host, 1, "Ben Rainfox"), c = await Arrive(host, 2, "Cho Lantern");
        Assert.Equal("not_in_convoy", Error(await a.Control.RequestAsync("meet.join", new { kind = "convoy" })));
        AssertOk(await a.Control.RequestAsync("convoy.create", new { privacy = "invite-only" }));
        string code = Result(await a.Control.RequestAsync("convoy.invite.create")).GetProperty("code").GetString()!;
        AssertOk(await b.Control.RequestAsync("convoy.join", new { code }));
        AssertOk(await c.Control.RequestAsync("convoy.join", new { code }));
        var bays = new List<int>();
        string? room = null;
        foreach (Visitor x in new[] { a, b, c })
        {
            JsonElement r = Result(await x.Control.RequestAsync("meet.join", new { kind = "convoy" }));
            room ??= r.GetProperty("roomId").GetString();
            Assert.Equal(room, r.GetProperty("roomId").GetString());
            Assert.Equal("convoy", r.GetProperty("state").GetProperty("kind").GetString());
            bays.Add(Member(r.GetProperty("state"), x.AccountId).GetProperty("bay").GetInt32());
        }
        Assert.Single(bays.Select(bay => MeetLayout.Bays[bay - 1].Side).Distinct());
    }

    [Fact]
    public async Task Boombox_NeedsRangeAndOwnership()
    {
        var clock = new ManualClock();
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), clock);
        Visitor a = await Arrive(host, 0, "Aki Night"), b = await Arrive(host, 1, "Ben Rainfox");
        AssertOk(await a.Control.RequestAsync("meet.join", new { kind = "public" }));
        AssertOk(await b.Control.RequestAsync("meet.join", new { kind = "public" }));
        clock.Advance(TimeSpan.FromSeconds(4));
        JsonElement at = Result(await a.Control.RequestAsync("meet.arrived"));
        AssertOk(await b.Control.RequestAsync("meet.arrived"));
        Assert.Equal("OutOfRange", Result(await a.Control.RequestAsync("meet.boombox", new { op = "acquire" })).GetProperty("status").GetString());

        // Walk (in real walking steps) from the bay across the plaza to the boombox.
        var path = new List<(float X, float Z)>
        {
            (at.GetProperty("x").GetSingle(), at.GetProperty("z").GetSingle()), (-45f, -18f), (12f, 34f), (13.6f, 40.0f),
        };
        int seq = 0;
        for (int i = 1; i < path.Count; i++)
        {
            (float x0, float z0) = path[i - 1];
            (float x1, float z1) = path[i];
            int steps = (int)Math.Ceiling(Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / 3.5);
            for (int k = 1; k <= steps; k++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                float x = x0 + (x1 - x0) * k / steps, z = z0 + (z1 - z0) * k / steps;
                JsonElement moved = Result(await a.Control.RequestAsync("meet.move", new { x, z, yaw = 0f, speed = 3.5f, seq = ++seq }));
                Assert.Equal("Accepted", moved.GetProperty("status").GetString());
            }
        }
        AssertOk(await a.Control.RequestAsync("meet.boombox", new { op = "acquire" }));
        Assert.Equal("boombox_outofrange", Error(await b.Control.RequestAsync("meet.boombox", new { op = "acquire" }))); // only someone at the boombox can take it
        Assert.Equal("boombox_notowned", Error(await a.Control.RequestAsync("meet.boombox", new { op = "queue", trackId = "MUS_FINAL_SHIORI" })));
        JsonElement queued = Result(await a.Control.RequestAsync("meet.boombox", new { op = "queue", trackId = "MUS_GARAGE" }));
        Assert.Equal("MUS_GARAGE", queued.GetProperty("boombox").GetProperty("trackId").GetString());
        JsonElement heard = await WaitForMeet(b.Control, s => s.GetProperty("boombox").GetProperty("trackId").GetString() == "MUS_GARAGE");
        Assert.Equal(a.AccountId, heard.GetProperty("boombox").GetProperty("submittedBy").GetString());
        AssertOk(await a.Control.RequestAsync("meet.boombox", new { op = "release" }));
        await WaitForMeet(b.Control, s => s.GetProperty("boombox").GetProperty("leaseHolder").ValueKind == JsonValueKind.Null);
    }
}
