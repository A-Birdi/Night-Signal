using System.Text.Json;
using NightSignal.Services.Tests.Infrastructure;

namespace NightSignal.Services.Tests;

/// <summary>
/// Addendum 01 §10 over the real control channel: a dropped socket removes membership at once, the reconnect "hello"
/// carries the server-owned rejoin status (not a client-remembered convoy), "convoy.rejoin" restores membership, and the
/// intent/Mode Ready flow and a course vote run over the wire.
/// </summary>
public sealed class RejoinEndToEndTests : IDisposable
{
    readonly TempDir dir = new();
    readonly IReadOnlyList<DevAccountFixture> accounts;

    public RejoinEndToEndTests() => accounts = TestData.WriteSeed(dir.File("seed.json"), 2);

    public void Dispose() => dir.Dispose();

    static string Type(JsonElement m) => m.GetProperty("type").GetString()!;
    static void AssertOk(JsonElement reply) =>
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.TryGetProperty("error", out JsonElement e) ? e.GetRawText() : reply.GetRawText());
    static string ErrorCode(JsonElement reply) => reply.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task DroppedSocket_LeavesTheConvoy_AndHelloOffersAServerValidatedRejoin()
    {
        using var host = new ControlPlaneHost(dir.Path, dir.File("seed.json"));
        string ta = await host.SignInAsync(accounts[0]), tb = await host.SignInAsync(accounts[1]);
        const string q = "build=b&protocol=2&content=c";
        await using ControlClient ca = await host.ConnectAsync(ta, q);
        JsonElement hello = (await ca.WaitForAsync(m => Type(m) == "hello")).GetProperty("payload");
        Assert.False(hello.GetProperty("rejoin").GetProperty("canRejoin").GetBoolean());
        Assert.Equal("none", hello.GetProperty("rejoin").GetProperty("reason").GetString());

        AssertOk(await ca.RequestAsync("convoy.create", new { privacy = "invite-only" }));
        string code = (await ca.RequestAsync("convoy.invite.create")).GetProperty("result").GetProperty("code").GetString()!;
        ControlClient cb = await host.ConnectAsync(tb, q);
        AssertOk(await cb.RequestAsync("convoy.join", new { code }));
        await ca.WaitForStateAsync(s => s.GetProperty("members").GetArrayLength() == 2);

        // Intent → Mode Ready → Enter Mode, then a Freeplay vote over the wire.
        long mode = (await ca.RequestAsync("intent.set", new { kind = "freeplay", submode = "sprint" })).GetProperty("result").GetProperty("modeRevision").GetInt64();
        AssertOk(await cb.RequestAsync("mode.ready", new { modeRevision = mode, ready = true }));
        AssertOk(await ca.RequestAsync("mode.enter", new { modeRevision = mode }));
        AssertOk(await ca.RequestAsync("voting.set", new { enabled = true, durationSeconds = 15 }));
        long ballot = (await ca.RequestAsync("ballot.open", new { })).GetProperty("result").GetProperty("ballotRevision").GetInt64();
        AssertOk(await cb.RequestAsync("ballot.vote", new { ballotRevision = ballot, courseId = "C02" }));
        Assert.Equal("course_locked", ErrorCode(await cb.RequestAsync("ballot.vote", new { ballotRevision = ballot, courseId = "C20" })));
        await ca.WaitForStateAsync(s => s.GetProperty("ballot").ValueKind == JsonValueKind.Object && s.GetProperty("ballot").GetProperty("totalBallots").GetInt32() == 1);

        // B's socket drops: membership, readiness and the running vote end at once, with one notice.
        await cb.DisposeAsync();
        JsonElement after = await ca.WaitForStateAsync(s => s.GetProperty("members").GetArrayLength() == 1);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("ballot").ValueKind);
        await ca.WaitForAsync(m => Type(m) == "convoy.notice" && m.GetProperty("payload").GetProperty("code").GetString() == "ballot_cancelled");

        // Reconnect with a fresh token: the grant survives transport/token expiry and is offered once.
        await using ControlClient cb2 = await host.ConnectAsync(await host.SignInAsync(accounts[1]), q);
        JsonElement rejoin = (await cb2.WaitForAsync(m => Type(m) == "hello")).GetProperty("payload").GetProperty("rejoin");
        Assert.True(rejoin.GetProperty("canRejoin").GetBoolean());
        Assert.True(rejoin.GetProperty("prompt").GetBoolean());
        AssertOk(await cb2.RequestAsync("rejoin.dismiss", new { forget = false }));
        Assert.False((await cb2.RequestAsync("rejoin.status")).GetProperty("result").GetProperty("prompt").GetBoolean());
        AssertOk(await cb2.RequestAsync("convoy.rejoin"));
        await ca.WaitForStateAsync(s => s.GetProperty("members").GetArrayLength() == 2);
        Assert.Equal("in_convoy", (await cb2.RequestAsync("rejoin.status")).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ADormantRoom_SurvivesAControlPlaneRestart_FromItsDurableSnapshot()
    {
        const string q = "build=b&protocol=2&content=c";
        string convoyId, sessionId;
        using (var host = new ControlPlaneHost(dir.Path, dir.File("seed.json")))
        {
            ControlClient ca = await host.ConnectAsync(await host.SignInAsync(accounts[0]), q);
            ControlClient cb = await host.ConnectAsync(await host.SignInAsync(accounts[1]), q);
            AssertOk(await ca.RequestAsync("convoy.create", new { privacy = "invite-only" }));
            string code = (await ca.RequestAsync("convoy.invite.create")).GetProperty("result").GetProperty("code").GetString()!;
            AssertOk(await cb.RequestAsync("convoy.join", new { code }));
            JsonElement state = await ca.WaitForStateAsync(s => s.GetProperty("members").GetArrayLength() == 2);
            convoyId = state.GetProperty("convoyId").GetString()!;
            sessionId = state.GetProperty("convoySessionId").GetString()!;
            await cb.DisposeAsync(); // every member is lost to disconnection → Dormant, snapshot persisted in the background
            await ca.WaitForStateAsync(s => s.GetProperty("members").GetArrayLength() == 1);
            await ca.DisposeAsync();
            await using ControlClient probe = await host.ConnectAsync(await host.SignInAsync(accounts[0]), q);
            JsonElement dormant = (await probe.WaitForAsync(m => Type(m) == "hello")).GetProperty("payload").GetProperty("rejoin");
            Assert.True(dormant.GetProperty("dormant").GetBoolean());
        } // host shutdown drains pending dormant-room writes

        using (var restarted = new ControlPlaneHost(dir.Path, dir.File("seed.json")))
        {
            await using ControlClient cb = await restarted.ConnectAsync(await restarted.SignInAsync(accounts[1]), q);
            JsonElement rejoin = (await cb.WaitForAsync(m => Type(m) == "hello")).GetProperty("payload").GetProperty("rejoin");
            Assert.True(rejoin.GetProperty("canRejoin").GetBoolean(), rejoin.GetRawText());
            Assert.Equal(convoyId, rejoin.GetProperty("convoyId").GetString());
            AssertOk(await cb.RequestAsync("convoy.rejoin"));
            JsonElement state = (await cb.RequestAsync("convoy.state")).GetProperty("result").GetProperty("convoy");
            Assert.Equal(sessionId, state.GetProperty("convoySessionId").GetString()); // same session, no fabricated members or race
            Assert.Equal(1, state.GetProperty("members").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, state.GetProperty("match").ValueKind);
        }
    }
}
