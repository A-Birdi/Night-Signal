using System.Net.Http.Json;
using System.Text.Json;
using NightSignal.Core.Meet;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// The meet's touring challenges over the real control channel: acts count only where the server-held position says the
/// visitor is (a claim from across the terrace is refused), completion is granted once with its cash, RP and cosmetic,
/// and the result slip needs a finished event first.
/// </summary>
public sealed class MeetTouringTests : IDisposable
{
    const string Query = "build=b&protocol=2&content=c";
    readonly TempDir dir = new();
    readonly IReadOnlyList<DevAccountFixture> accounts;

    public MeetTouringTests() => accounts = TestData.WriteSeed(dir.File("seed.json"), 2);

    public void Dispose() => dir.Dispose();

    static string Type(JsonElement m) => m.GetProperty("type").GetString()!;
    static JsonElement Result(JsonElement reply) => reply.GetProperty("result");
    static string Error(JsonElement reply) => reply.GetProperty("error").GetProperty("code").GetString()!;

    static void AssertOk(JsonElement reply) =>
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.TryGetProperty("error", out JsonElement e) ? e.GetRawText() : reply.GetRawText());

    /// <summary>A walkable route on a 0.5 m grid (every bay treated as occupied, so it keeps to the aisles and the plaza).</summary>
    static List<(float X, float Z)> Route((float X, float Z) from, (float X, float Z) to)
    {
        const float step = 0.5f, minX = -66f, minZ = -48f;
        int w = (int)(132 / step), h = (int)(96 / step);
        var all = Enumerable.Range(0, MeetLayout.Bays.Length).ToList();
        bool Free(int i, int j) => MeetLayout.Walkable(minX + i * step, minZ + j * step, MeetLayout.AvatarRadius, all);
        (int, int) Cell((float X, float Z) p) => ((int)MathF.Round((p.X - minX) / step), (int)MathF.Round((p.Z - minZ) / step));
        (int si, int sj) = Cell(from);
        (int ti, int tj) = Cell(to);
        var prev = new Dictionary<(int, int), (int, int)>();
        var queue = new Queue<(int, int)>();
        queue.Enqueue((si, sj));
        prev[(si, sj)] = (si, sj);
        (int, int)[] dirs = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };
        while (queue.Count > 0)
        {
            (int i, int j) = queue.Dequeue();
            if (Math.Abs(i - ti) <= 1 && Math.Abs(j - tj) <= 1) { ti = i; tj = j; break; }
            foreach ((int di, int dj) in dirs)
            {
                (int, int) n = (i + di, j + dj);
                if (n.Item1 < 0 || n.Item2 < 0 || n.Item1 >= w || n.Item2 >= h || prev.ContainsKey(n) || !Free(n.Item1, n.Item2)) continue;
                prev[n] = (i, j);
                queue.Enqueue(n);
            }
        }
        Assert.True(prev.ContainsKey((ti, tj)), "a walkable route exists");
        var path = new List<(float, float)>();
        for ((int, int) c = (ti, tj); c != (si, sj); c = prev[c]) path.Add((minX + c.Item1 * step, minZ + c.Item2 * step));
        path.Reverse();
        return path;
    }

    /// <summary>Walks the route at ~3 m/s (inside the room's speed envelope), every pose accepted.</summary>
    static async Task<int> Walk(ControlClient control, ManualClock clock, int seq, List<(float X, float Z)> route)
    {
        for (int k = 0; k < route.Count; k += 3) // 1.5 m per step
        {
            (float x, float z) = route[k];
            clock.Advance(TimeSpan.FromMilliseconds(500));
            JsonElement r = Result(await control.RequestAsync("meet.move", new { x, z, yaw = 0f, speed = 3f, seq = ++seq }));
            Assert.Equal("Accepted", r.GetProperty("status").GetString());
        }
        (float lx, float lz) = route[^1];
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal("Accepted", Result(await control.RequestAsync("meet.move", new { x = lx, z = lz, yaw = 0f, speed = 1f, seq = ++seq })).GetProperty("status").GetString());
        return seq;
    }

    [Fact]
    public async Task TouringChallenges_CountWhereTheyHappen_GrantedOnce()
    {
        var clock = new ManualClock();
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), clock);
        string token = await host.SignInAsync(accounts[0]);
        HttpClient http = host.Authed(token);
        (await http.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night" })).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync("/v1/me/starter", new { carId = "V01" })).EnsureSuccessStatusCode();
        long balance0 = (await http.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("wallet").GetProperty("balance").GetInt64();
        ControlClient control = await host.ConnectAsync(token, Query);

        AssertOk(await control.RequestAsync("meet.join", new { kind = "public" }));
        // Before arriving, nothing counts.
        Assert.Equal("not_yet", Error(await control.RequestAsync("meet.touring", new { step = "own-car" })));
        clock.Advance(TimeSpan.FromSeconds(4));
        JsonElement arrived = Result(await control.RequestAsync("meet.arrived"));
        (float x, float z) = (arrived.GetProperty("x").GetSingle(), arrived.GetProperty("z").GetSingle());

        // CH61: arrived + own car inspected beside it.
        JsonElement own = Result(await control.RequestAsync("meet.touring", new { step = "own-car" }));
        Assert.Equal("CH61", own.GetProperty("completed")[0].GetProperty("challengeId").GetString());
        JsonElement push = await control.WaitForAsync(m => Type(m) == "meet.challenge");
        Assert.Equal("CH61", push.GetProperty("payload").GetProperty("challengeId").GetString());

        // From the bay, the host's emote help does not count.
        Assert.Equal("not_here", Error(await control.RequestAsync("meet.touring", new { step = "emote-help" })));
        // The result slip needs a finished event first (none yet) — once at the board it says so.
        int seq = await Walk(control, clock, 0, Route((x, z), (MeetLayout.TimingBoard.X, MeetLayout.TimingBoard.Z - 1.6f)));
        Assert.Equal("no_event", Error(await control.RequestAsync("meet.touring", new { step = "result-slip" })));

        // CH63 at the host: wave, bow (server-played emotes near the host) and the emote help.
        seq = await Walk(control, clock, seq, Route((MeetLayout.TimingBoard.X, MeetLayout.TimingBoard.Z - 1.6f), (MeetLayout.HostSpot.X - 1.5f, MeetLayout.HostSpot.Z - 1.5f)));
        AssertOk(await control.RequestAsync("meet.emote", new { emote = "Wave" }));
        clock.Advance(TimeSpan.FromSeconds(3));
        AssertOk(await control.RequestAsync("meet.emote", new { emote = "Bow" }));
        clock.Advance(TimeSpan.FromSeconds(3));
        JsonElement greet = Result(await control.RequestAsync("meet.touring", new { step = "emote-help" }));
        JsonElement ch63 = greet.GetProperty("completed")[0];
        Assert.Equal("CH63", ch63.GetProperty("challengeId").GetString());
        Assert.True(ch63.GetProperty("cash").GetInt64() > 0);

        // Granted once: reading the help again completes nothing; the account shows both, with the cash and the cosmetics.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, Result(await control.RequestAsync("meet.touring", new { step = "emote-help" })).GetProperty("completed").GetArrayLength());
        JsonElement me = await http.GetFromJsonAsync<JsonElement>("/v1/me");
        string[] done = me.GetProperty("challengesCompleted").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Contains("CH61", done);
        Assert.Contains("CH63", done);
        long cash = own.GetProperty("completed")[0].GetProperty("cash").GetInt64() + ch63.GetProperty("cash").GetInt64();
        Assert.Equal(balance0 + cash, me.GetProperty("wallet").GetProperty("balance").GetInt64());
        await control.DisposeAsync();
    }

    [Fact]
    public async Task SignYourCar_CH48_WhenTheParkedCarWearsASignedLivery()
    {
        var clock = new ManualClock();
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"), clock);
        async Task<(HttpClient Http, ControlClient Control)> Visitor(int i, bool signed)
        {
            string token = await host.SignInAsync(accounts[i]);
            HttpClient http = host.Authed(token);
            (await http.PostAsJsonAsync("/v1/me/card", new { displayName = i == 0 ? "Aki Night" : "Ben Rainfox" })).EnsureSuccessStatusCode();
            (await http.PostAsJsonAsync("/v1/me/starter", new { carId = "V01" })).EnsureSuccessStatusCode();
            if (signed)
            {
                // In the Garage: a livery with a decal and a roof two-tone, applied (the Garage validates it).
                JsonElement car = (await http.GetFromJsonAsync<JsonElement>("/v1/me/garage/cars")).GetProperty("cars")[0];
                (await http.PostAsJsonAsync($"/v1/me/garage/cars/{car.GetProperty("instanceId").GetString()}/operations", new
                {
                    op = "livery-apply", expectedRevision = car.GetProperty("revision").GetInt64(),
                    liveryJson = NightSignal.Core.Customization.LiveryJson.ToCanonicalJson(GarageTestKit.Livery()),
                })).EnsureSuccessStatusCode();
            }
            return (http, await host.ConnectAsync(token, Query));
        }
        static string[] Completed(JsonElement r) => r.GetProperty("completed").EnumerateArray().Select(c => c.GetProperty("challengeId").GetString()!).ToArray();

        (HttpClient http, ControlClient signedCar) = await Visitor(0, true);
        (_, ControlClient stockCar) = await Visitor(1, false);
        AssertOk(await signedCar.RequestAsync("meet.join", new { kind = "public" }));
        AssertOk(await stockCar.RequestAsync("meet.join", new { kind = "public" }));
        clock.Advance(TimeSpan.FromSeconds(4));
        AssertOk(await signedCar.RequestAsync("meet.arrived"));
        AssertOk(await stockCar.RequestAsync("meet.arrived"));

        // Seen on the parked car: the signed livery completes CH48 with CH61; the stock car only CH61.
        Assert.Equal(new[] { "CH61", "CH48" }, Completed(Result(await signedCar.RequestAsync("meet.touring", new { step = "own-car" }))));
        Assert.Equal(new[] { "CH61" }, Completed(Result(await stockCar.RequestAsync("meet.touring", new { step = "own-car" }))));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(Completed(Result(await signedCar.RequestAsync("meet.touring", new { step = "own-car" }))));
        string[] done = (await http.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("challengesCompleted").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Contains("CH48", done);
    }
}
