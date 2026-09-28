using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using NightSignal.ControlPlane.Security;
using NightSignal.Core.Rules;
using NightSignal.Services.Tests.Infrastructure;
using NightSignal.TicketValidation;

namespace NightSignal.Services.Tests;

/// <summary>
/// Whole control-plane flow in-process: two DevAuth accounts, WebSocket convoy + both ready checks, start,
/// allocation on a fake registered game server, single-use tickets, HMAC-signed results, Core-computed receipts,
/// and persistence across a service restart (same SQLite file).
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    const string Build = "e2e-build";
    readonly TempDir dir = new();
    readonly IReadOnlyList<DevAccountFixture> accounts;
    string Seed => dir.File("seed.json");

    public EndToEndTests() => accounts = TestData.WriteSeed(dir.File("seed.json"), 3);

    public void Dispose() => dir.Dispose();

    static string Type(JsonElement m) => m.GetProperty("type").GetString()!;

    static void AssertOk(JsonElement reply) =>
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.TryGetProperty("error", out JsonElement e) ? e.GetRawText() : reply.GetRawText());

    static string ErrorCode(JsonElement reply) => reply.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>A fake authoritative game server: long-polls for the assignment and acknowledges it.</summary>
    static async Task<JsonElement> PollAndAckAsync(HttpClient gameServer, CancellationToken ct)
    {
        while (true)
        {
            HttpResponseMessage r = await gameServer.GetAsync($"/v1/servers/{ControlPlaneHost.ServerId}/assignments?waitSeconds=5", ct);
            r.EnsureSuccessStatusCode();
            JsonElement list = (await r.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("assignments");
            if (list.GetArrayLength() == 0) continue;
            JsonElement assignment = list[0].Clone();
            (await gameServer.PostAsync($"/v1/servers/{ControlPlaneHost.ServerId}/assignments/{assignment.GetProperty("matchId").GetString()}/ack", null, ct))
                .EnsureSuccessStatusCode();
            return assignment;
        }
    }

    static HttpRequestMessage SignedResults(string matchId, byte[] body, string signature)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/matches/{matchId}/results") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-NightSignal-Signature", signature);
        return req;
    }

    static string Sign(string secret, byte[] body) => "sha256=" + Hashing.HmacSha256Hex(Base64UrlEncoder.DecodeBytes(secret), body);

    [Fact]
    public async Task TwoDrivers_FromSignInToSettledReceipts_AndTheWalletSurvivesARestart()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        DevAccountFixture a = accounts[0], b = accounts[1], outsider = accounts[2];
        long expectedA, expectedB;
        string matchId;
        var secrets = new List<string>();

        using (var host = new ControlPlaneHost(dir.Path, Seed, clock))
        {
            string ta = await host.SignInAsync(a), tb = await host.SignInAsync(b);
            secrets.AddRange(new[] { ta, tb, a.Password, b.Password, host.ServerKey });
            HttpClient ha = host.Authed(ta), hb = host.Authed(tb);

            // Player bootstrap: card + starter (12,000 credits, once).
            (await ha.PostAsJsonAsync("/v1/me/card", new { displayName = "Aki Night" })).EnsureSuccessStatusCode();
            (await hb.PostAsJsonAsync("/v1/me/card", new { displayName = "Ben Rainfox" })).EnsureSuccessStatusCode();
            JsonElement starter = await (await ha.PostAsJsonAsync("/v1/me/starter", new { carId = "V01" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(Limits.StarterGrantCredits, starter.GetProperty("balance").GetInt64());
            (await hb.PostAsJsonAsync("/v1/me/starter", new { carId = "V03" })).EnsureSuccessStatusCode();
            string contentHash = (await host.CreateClient().GetFromJsonAsync<JsonElement>("/healthz")).GetProperty("contentHash").GetString()!;

            // A game server registers with its server key (never a player token) and long-polls.
            HttpClient gs = host.GameServer();
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.Authed(ta).PostAsJsonAsync("/v1/servers/register", new { })).StatusCode);
            HttpResponseMessage reg = await gs.PostAsJsonAsync("/v1/servers/register",
                new { endpoint = new { host = "127.0.0.1", port = 7777 }, build = Build, protocol = 2, contentHash, maxMatches = 2 });
            Assert.Equal(HttpStatusCode.OK, reg.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await gs.PostAsJsonAsync($"/v1/servers/{ControlPlaneHost.ServerId}/heartbeat", new { activeMatches = 0 })).StatusCode);
            using var stopPolling = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Task<JsonElement> assignmentTask = PollAndAckAsync(gs, stopPolling.Token);

            string q = $"build={Build}&protocol=2&content={contentHash}";
            await using ControlClient ca = await host.ConnectAsync(ta, q);
            await using ControlClient cb = await host.ConnectAsync(tb, q);
            await ca.WaitForAsync(m => Type(m) == "hello");

            // Convoy: create (retried requestId is idempotent), invite, join.
            JsonElement created = await ca.RequestAsync("convoy.create", new { privacy = "invite-only" }, "create-1");
            AssertOk(created);
            JsonElement createdAgain = await ca.RequestAsync("convoy.create", new { privacy = "invite-only" }, "create-1");
            Assert.Equal(created.GetRawText(), createdAgain.GetRawText());
            string code = (await ca.RequestAsync("convoy.invite.create")).GetProperty("result").GetProperty("code").GetString()!;
            AssertOk(await cb.RequestAsync("convoy.join", new { code }));

            // Loadouts: only owned cars.
            Assert.Equal("not_owned", ErrorCode(await cb.RequestAsync("loadout.set", new { carId = "V18", performanceHash = "p", cosmeticHash = "c" })));
            AssertOk(await ca.RequestAsync("loadout.set", new { carId = "V01", performanceHash = "stock-v01", cosmeticHash = "paint-1" }));
            AssertOk(await cb.RequestAsync("loadout.set", new { carId = "V03", performanceHash = "stock-v03", cosmeticHash = "paint-7" }));

            // Consent level 1: Intent → Mode Ready → Enter Mode (Addendum 01 §7).
            JsonElement proposed = await ca.RequestAsync("intent.set", new { kind = "campaign", mode = "normal" }, "intent-1");
            AssertOk(proposed);
            AssertOk(await ca.RequestAsync("intent.set", new { kind = "campaign", mode = "normal" }, "intent-1")); // retry, not rate-limited
            long dRev = proposed.GetProperty("result").GetProperty("modeRevision").GetInt64();
            Assert.Equal("not_all_ready", ErrorCode(await ca.RequestAsync("mode.enter", new { modeRevision = dRev })));
            AssertOk(await cb.RequestAsync("mode.ready", new { modeRevision = dRev, ready = true }));
            AssertOk(await ca.RequestAsync("mode.enter", new { modeRevision = dRev }));
            Assert.Equal("unknown_type", ErrorCode(await ca.RequestAsync("destination.propose", new { destination = "campaign-normal" }))); // replaced

            // Ready check 2: the event, per proposal revision and loadout revision.
            clock.Advance(TimeSpan.FromSeconds(15));
            JsonElement ev = await ca.RequestAsync("event.propose", new { stageId = "S01" });
            AssertOk(ev);
            long eRev = ev.GetProperty("result").GetProperty("proposalRevision").GetInt64();
            Assert.Equal("stale_revision", ErrorCode(await cb.RequestAsync("event.ready", new { proposalRevision = dRev, loadoutRevision = 1, ready = true })));
            AssertOk(await ca.RequestAsync("event.ready", new { proposalRevision = eRev, loadoutRevision = 1, ready = true }));
            AssertOk(await cb.RequestAsync("event.ready", new { proposalRevision = eRev, loadoutRevision = 1, ready = true }, "ready-b"));
            AssertOk(await cb.RequestAsync("event.ready", new { proposalRevision = eRev, loadoutRevision = 1, ready = true }, "ready-b"));
            JsonElement state = await ca.WaitForStateAsync(s => s.GetProperty("members").EnumerateArray().All(m => m.GetProperty("eventReady").GetBoolean()));
            Assert.Equal("ReadyCheck", state.GetProperty("phase").GetString());
            // S01 Normal has a certified benchmark (stage-benchmarks.json): not provisional, the published target.
            Assert.False(state.GetProperty("eventProposal").GetProperty("settings").GetProperty("benchmarkProvisional").GetBoolean());

            // Start → allocation on the registered server → private tickets.
            Assert.Equal("not_leader", ErrorCode(await cb.RequestAsync("event.start", new { proposalRevision = eRev })));
            JsonElement start = await ca.RequestAsync("event.start", new { proposalRevision = eRev });
            AssertOk(start);
            JsonElement assignment = await assignmentTask.WaitAsync(TimeSpan.FromSeconds(30));
            string secret = assignment.GetProperty("resultsSecret").GetString()!;
            secrets.Add(secret);
            matchId = assignment.GetProperty("matchId").GetString()!;
            JsonElement ticketA = (await ca.WaitForAsync(m => Type(m) == "match.allocated")).GetProperty("payload");
            JsonElement ticketB = (await cb.WaitForAsync(m => Type(m) == "match.allocated")).GetProperty("payload");
            Assert.Equal(matchId, ticketA.GetProperty("matchId").GetString());
            Assert.Equal(7777, ticketA.GetProperty("server").GetProperty("port").GetInt32());
            await ca.WaitForStateAsync(s => s.GetProperty("phase").GetString() == "InMatch");
            Assert.DoesNotContain(ca.Messages, m => m.GetRawText().Contains(ticketB.GetProperty("ticket").GetString()!)); // tickets are private
            secrets.Add(ticketA.GetProperty("ticket").GetString()!);

            // The game server verifies tickets with the published JWKS and the netstandard2.1 validator.
            string jwks = await host.CreateClient().GetStringAsync("/v1/servers/ticket-jwks.json");
            var validator = new TicketValidator(TicketKeySet.FromJwks(jwks),
                new TicketValidationParameters { Issuer = assignment.GetProperty("ticketIssuer").GetString()! },
                new InMemoryTicketReplayCache(), () => clock.GetUtcNow());
            var expected = new ExpectedTicketContext { MatchId = matchId, Build = Build, Protocol = 2, ContentHash = contentHash };
            TicketValidationResult va = validator.Validate(ticketA.GetProperty("ticket").GetString()!, expected);
            TicketValidationResult vb = validator.Validate(ticketB.GetProperty("ticket").GetString()!, expected);
            Assert.True(va.IsValid, va.Failure.ToString());
            Assert.True(vb.IsValid, vb.Failure.ToString());
            Assert.Equal(a.AccountId, va.Claims.Subject);
            Assert.Equal("racer", va.Claims.Role);
            Assert.Equal(TicketFailure.Replayed, validator.Validate(ticketA.GetProperty("ticket").GetString()!, expected).Failure);

            // Server-observed facts. S01's certified Normal benchmark is 102,070 ms (support envelope 150 % = 153.1 s): A qualifies,
            // B only supports.
            string[] ai = assignment.GetProperty("aiEntrants").EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.Equal(TestData.Content.Catalogue.Stage("S01").Normal.Opponents, ai); // authored live opposition (Addendum 01 §1.2)
            object Human(string id, long seconds, int place, bool clean, string[] challenges) => new
            {
                entrantId = id, human = true, outcome = "Finished", finishTimeMicros = seconds * 1_000_000, placement = place, clean,
                checkpointFraction = 1.0, activeProgressVerified = true, activelyDroveLegalCourse = true, legalProgressMetres = 3100.0,
                rawDriftScore = 0, contractsPassed = 0, challengesCompleted = challenges,
            };
            object Ai(string id, long seconds, int place) => new
            {
                entrantId = id, human = false, outcome = "Finished", finishTimeMicros = seconds * 1_000_000, placement = place, clean = true,
                checkpointFraction = 1.0, activeProgressVerified = true, activelyDroveLegalCourse = true, legalProgressMetres = 3100.0,
                rawDriftScore = 0, contractsPassed = 0, challengesCompleted = Array.Empty<string>(),
            };
            object Body(int placeB, object? extra = null) => new
            {
                matchId, contentHash, aborted = false,
                entrants = new[]
                {
                    // S01 Normal is authored as a duel with its featured rival (stages.opposition.json).
                    Human(a.AccountId, 96, 1, clean: true, new[] { "CH01" }), Ai(ai[0], 99, 2), Human(b.AccountId, 113, placeB, clean: false, Array.Empty<string>()),
                },
            };
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(Body(3));

            // Rejections: bad signature, wrong server key, client-supplied money, placement that disagrees with Core.
            Assert.Equal(HttpStatusCode.Unauthorized, (await gs.SendAsync(SignedResults(matchId, body, "sha256=" + new string('0', 64)))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().SendAsync(SignedResults(matchId, body, Sign(secret, body)))).StatusCode);
            byte[] withCredits = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("\"aborted\":false", "\"aborted\":false,\"credits\":999999"));
            Assert.Equal(HttpStatusCode.BadRequest, (await gs.SendAsync(SignedResults(matchId, withCredits, Sign(secret, withCredits)))).StatusCode);
            byte[] wrongPlace = JsonSerializer.SerializeToUtf8Bytes(Body(2));
            Assert.Equal((HttpStatusCode)422, (await gs.SendAsync(SignedResults(matchId, wrongPlace, Sign(secret, wrongPlace)))).StatusCode);
            HttpResponseMessage pending = await ha.GetAsync($"/v1/matches/{matchId}/receipt");
            Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);

            // Ghosts (spec §8): the game server's recording for A, signed like the results and sent before them; a bad
            // signature and another course are refused.
            string course = assignment.GetProperty("courseId").GetString()!;
            byte[] Ghost(string courseId, long resultMs)
            {
                var g = new NightSignal.Core.Ghosts.GhostRecording
                {
                    Header = new NightSignal.Core.Ghosts.GhostHeader
                    {
                        CourseId = courseId, CourseRevision = "r", Format = "S01-normal", PhysicsVersion = "p", ScoringVersion = "s", CarModelId = "V01",
                        ResultMicros = resultMs * 1000, Provenance = "server-settlement", Driver = "Aki Night",
                    },
                };
                for (int k = 0; k <= resultMs / 100; k++) g.Add(k / 10f, k, 0f, 0f, 0f, 0f, 0f, 1f, 30f);
                g.CheckpointMicros.Add(resultMs * 500);
                return Encoding.UTF8.GetBytes(g.ToJson());
            }
            HttpRequestMessage SignedGhost(string account, byte[] ghost, string signature)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/matches/{matchId}/ghosts/{account}") { Content = new ByteArrayContent(ghost) };
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                req.Headers.Add("X-NightSignal-Signature", signature);
                return req;
            }
            byte[] ghostA = Ghost(course, 96_000);
            Assert.Equal(HttpStatusCode.Unauthorized, (await gs.SendAsync(SignedGhost(a.AccountId, ghostA, "sha256=" + new string('0', 64)))).StatusCode);
            byte[] elsewhere = Ghost("C25", 96_000);
            Assert.Equal((HttpStatusCode)422, (await gs.SendAsync(SignedGhost(a.AccountId, elsewhere, Sign(secret, elsewhere)))).StatusCode);
            Assert.Equal((HttpStatusCode)422, (await gs.SendAsync(SignedGhost(outsider.AccountId, ghostA, Sign(secret, ghostA)))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await gs.SendAsync(SignedGhost(a.AccountId, ghostA, Sign(secret, ghostA)))).StatusCode);
            // B's ghost does not match B's settled finish (113 s): it is never kept.
            byte[] ghostB = Ghost(course, 110_000);
            Assert.Equal(HttpStatusCode.OK, (await gs.SendAsync(SignedGhost(b.AccountId, ghostB, Sign(secret, ghostB)))).StatusCode);

            // Accepted settlement, then an identical retry.
            HttpResponseMessage settled = await gs.SendAsync(SignedResults(matchId, body, Sign(secret, body)));
            Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
            Assert.Equal(2, (await settled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receipts").GetArrayLength());
            JsonElement replay = await (await gs.SendAsync(SignedResults(matchId, body, Sign(secret, body)))).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(replay.GetProperty("replayed").GetBoolean());
            // A's ghost was kept (it matches the settled 96 s finish); B's was not; a late ghost is refused.
            JsonElement ghostsA = await ha.GetFromJsonAsync<JsonElement>($"/v1/me/ghosts/{course}/S01-normal");
            Assert.Single(ghostsA.GetProperty("ghosts").EnumerateArray());
            Assert.Equal(96_000_000, ghostsA.GetProperty("ghosts")[0].GetProperty("header").GetProperty("resultMicros").GetInt64());
            Assert.Empty((await hb.GetFromJsonAsync<JsonElement>($"/v1/me/ghosts/{course}/S01-normal")).GetProperty("ghosts").EnumerateArray());
            Assert.Equal(HttpStatusCode.Conflict, (await gs.SendAsync(SignedGhost(a.AccountId, ghostA, Sign(secret, ghostA)))).StatusCode);

            // Receipts recomputed with Core: B = 2,500 + 22 × 180; placement/clean multipliers; first clear; challenge cash.
            PayoutBreakdown coreA = Economy.Compute(new PayoutFacts
            {
                AuthoredExpectedSeconds = 180, Kind = EventKind.CampaignStage, Mode = CampaignMode.Normal, Outcome = RunOutcome.Finished,
                Placement = 1, Clean = true, FirstClearBonus = 8_000, NewlyCompletedChallengeCash = 3_000,
            });
            PayoutBreakdown coreB = Economy.Compute(new PayoutFacts
            {
                AuthoredExpectedSeconds = 180, Kind = EventKind.CampaignStage, Mode = CampaignMode.Normal, Outcome = RunOutcome.Finished,
                Placement = 3, Clean = false, FirstClearBonus = 8_000,
            });
            expectedA = Limits.StarterGrantCredits + coreA.Total;
            expectedB = Limits.StarterGrantCredits + coreB.Total;

            JsonElement ra = await ha.GetFromJsonAsync<JsonElement>($"/v1/matches/{matchId}/receipt");
            JsonElement rb = await hb.GetFromJsonAsync<JsonElement>($"/v1/matches/{matchId}/receipt");
            Assert.Equal(a.AccountId, ra.GetProperty("accountId").GetString());
            Assert.Equal(6_460, ra.GetProperty("payout").GetProperty("base").GetInt64());
            Assert.Equal(135, ra.GetProperty("payout").GetProperty("placementX100").GetInt32());
            Assert.Equal(105, ra.GetProperty("payout").GetProperty("cleanlinessX100").GetInt32());
            Assert.Equal(coreA.EventCredits, ra.GetProperty("payout").GetProperty("eventCredits").GetInt64());
            Assert.Equal(8_000, ra.GetProperty("payout").GetProperty("firstClearBonus").GetInt64());
            Assert.Equal(3_000, ra.GetProperty("payout").GetProperty("challengeCash").GetInt64());
            Assert.Equal(coreA.Total, ra.GetProperty("payout").GetProperty("total").GetInt64());
            Assert.Equal(new[] { "event", "first-clear", "challenge:CH01" },
                ra.GetProperty("credits").EnumerateArray().Select(c => c.GetProperty("type").GetString()));
            Assert.True(ra.GetProperty("stage").GetProperty("qualified").GetBoolean());
            Assert.True(ra.GetProperty("stage").GetProperty("teamSuccess").GetBoolean());
            Assert.False(ra.GetProperty("stage").GetProperty("benchmarkProvisional").GetBoolean());
            Assert.Equal(expectedA, ra.GetProperty("balanceAfter").GetInt64());
            Assert.Equal(RankPoints.NormalFirstClear + RankPoints.BronzeChallenge, ra.GetProperty("rankPointsAfter").GetInt32());
            Assert.Contains("COS-CH01", ra.GetProperty("cosmeticsGranted").EnumerateArray().Select(x => x.GetString()));

            Assert.Equal(3, rb.GetProperty("placement").GetInt32());
            Assert.False(rb.GetProperty("stage").GetProperty("qualified").GetBoolean());
            Assert.True(rb.GetProperty("stage").GetProperty("earnedClear").GetBoolean()); // supported by A's qualifying run
            Assert.Equal(coreB.Total, rb.GetProperty("payout").GetProperty("total").GetInt64());
            Assert.Equal(expectedB, rb.GetProperty("balanceAfter").GetInt64());

            string tOut = await host.SignInAsync(outsider);
            Assert.Equal(HttpStatusCode.NotFound, (await host.Authed(tOut).GetAsync($"/v1/matches/{matchId}/receipt")).StatusCode);

            // A convoy member's shared ghost (spec §8): B, riding with A, reads A's kept ghost; the outsider cannot.
            JsonElement shared = await hb.GetFromJsonAsync<JsonElement>($"/v1/convoy/ghosts/{a.AccountId}/{course}/S01-normal");
            Assert.Equal(a.AccountId, shared.GetProperty("owner").GetString());
            Assert.Equal(96_000_000, Assert.Single(shared.GetProperty("ghosts").EnumerateArray()).GetProperty("header").GetProperty("resultMicros").GetInt64());
            Assert.Equal(HttpStatusCode.Forbidden, (await host.Authed(tOut).GetAsync($"/v1/convoy/ghosts/{a.AccountId}/{course}/S01-normal")).StatusCode);
            // A block either way hides it, and lifting the block restores it.
            Assert.Equal(HttpStatusCode.OK, (await ha.PutAsync($"/v1/blocks/{b.AccountId}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await hb.GetAsync($"/v1/convoy/ghosts/{a.AccountId}/{course}/S01-normal")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ha.DeleteAsync($"/v1/blocks/{b.AccountId}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await hb.GetAsync($"/v1/convoy/ghosts/{a.AccountId}/{course}/S01-normal")).StatusCode);

            // The convoy returns to event selection with refreshed progress.
            await ca.WaitForStateAsync(s => s.GetProperty("phase").GetString() == "EventSelection");
            stopPolling.Cancel();

            // Logs never contain credentials, tokens, keys or secrets.
            string logs = string.Join('\n', host.Logs.Lines);
            Assert.NotEmpty(host.Logs.Lines);
            foreach (string s in secrets) Assert.DoesNotContain(s, logs);
        }

        SqliteConnection.ClearAllPools();

        // Restart: a new process-equivalent host on the same SQLite file and keys.
        using (var restarted = new ControlPlaneHost(dir.Path, Seed))
        {
            HttpClient ha = restarted.Authed(await restarted.SignInAsync(a));
            JsonElement me = await ha.GetFromJsonAsync<JsonElement>("/v1/me");
            Assert.Equal(expectedA, me.GetProperty("wallet").GetProperty("balance").GetInt64());
            Assert.True(me.GetProperty("campaign").GetProperty("normalCleared")[0].GetBoolean());
            Assert.Equal(2, me.GetProperty("campaign").GetProperty("normalFrontier").GetInt32());
            Assert.Equal(140, me.GetProperty("rank").GetProperty("rankPoints").GetInt32());
            Assert.Equal("V01", me.GetProperty("starterCarId").GetString());
            JsonElement receipt = await ha.GetFromJsonAsync<JsonElement>($"/v1/matches/{matchId}/receipt");
            Assert.Equal(expectedA, receipt.GetProperty("balanceAfter").GetInt64());
            HttpClient hb = restarted.Authed(await restarted.SignInAsync(b));
            Assert.Equal(expectedB, (await hb.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("wallet").GetProperty("balance").GetInt64());
        }
    }

    [Fact]
    public async Task OneControlSessionPerAccount_TakeoverNeedsConsent()
    {
        using var host = new ControlPlaneHost(dir.Path, Seed);
        string token = await host.SignInAsync(accounts[0]);
        await using ControlClient first = await host.ConnectAsync(token, "build=b&protocol=2&content=c");
        await first.WaitForAsync(m => Type(m) == "hello");

        await using ControlClient second = await host.ConnectAsync(token, "build=b&protocol=2&content=c");
        await second.WaitForAsync(m => Type(m) == "session.rejected");
        await first.RequestAsync("ping"); // the original session is unaffected

        await using ControlClient third = await host.ConnectAsync(token, "build=b&protocol=2&content=c&takeover=1");
        await third.WaitForAsync(m => Type(m) == "hello");
        await first.WaitForAsync(m => Type(m) == "session.superseded"); // the older client is told why
        AssertOk(await third.RequestAsync("convoy.create", new { privacy = "discoverable" }));
    }

    [Fact]
    public async Task ControlChannel_RequiresAValidToken()
    {
        using var host = new ControlPlaneHost(dir.Path, Seed);
        await Assert.ThrowsAnyAsync<Exception>(() => host.ConnectAsync("not-a-token", "build=b&protocol=2&content=c"));
    }

    [Fact]
    public async Task ControlChannel_ReauthenticatesWithARefreshedToken_ForTheSameAccountOnly()
    {
        using var host = new ControlPlaneHost(dir.Path, Seed);
        string token = await host.SignInAsync(accounts[0]);
        await using ControlClient c = await host.ConnectAsync(token, "build=b&protocol=2&content=c");
        AssertOk(await c.RequestAsync("session.reauth", new { accessToken = await host.SignInAsync(accounts[0]) }));
        Assert.Equal("unauthorized", ErrorCode(await c.RequestAsync("session.reauth", new { accessToken = await host.SignInAsync(accounts[1]) })));
        Assert.Equal("unauthorized", ErrorCode(await c.RequestAsync("session.reauth", new { accessToken = "garbage" })));
    }

    [Fact]
    public async Task MalformedMessages_GetAnErrorAndTheSessionContinues()
    {
        using var host = new ControlPlaneHost(dir.Path, Seed);
        await using ControlClient c = await host.ConnectAsync(await host.SignInAsync(accounts[0]), "build=b&protocol=2&content=c");
        await c.SendRawAsync("{not json");
        await c.WaitForAsync(m => Type(m) == "error");
        Assert.Equal("unknown_type", ErrorCode(await c.RequestAsync("wallet.set", new { balance = 9_999_999 })));
        Assert.Equal("invalid_request", ErrorCode(await c.RequestAsync("event.ready", new { proposalRevision = "seven" })));
        AssertOk(await c.RequestAsync("ping"));
    }
}
