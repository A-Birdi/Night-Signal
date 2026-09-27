using System.Collections.Concurrent;
using System.Text.Json;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Convoys;
using NightSignal.Core.Rules;

namespace NightSignal.Services.Tests.Infrastructure;

/// <summary>Records every control message the directory sends.</summary>
public sealed class RecordingNotifier : IConvoyNotifier
{
    public readonly ConcurrentQueue<(string Account, string Type, long Revision, object Payload)> Sent = new();
    public void Send(string accountId, string type, long revision, object payload) => Sent.Enqueue((accountId, type, revision, payload));

    public IEnumerable<JsonElement> To(string account, string type) =>
        Sent.Where(m => m.Account == account && m.Type == type).Select(m => JsonSerializer.SerializeToElement(m.Payload, ConvoyTestBase.Web));
}

/// <summary>Deterministic "server CSPRNG" for tests: returns the queued values, then 0.</summary>
public sealed class FixedRandom(params uint[] values) : IRandomSource
{
    readonly Queue<uint> queue = new(values);
    public uint NextUInt32() => queue.Count > 0 ? queue.Dequeue() : 0;
}

/// <summary>Shared helpers for ConvoyDirectory tests (injectable clock, stable account IDs, flows).</summary>
public abstract class ConvoyTestBase
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    protected static readonly ClientVersion V = new("build-1", 1, "content-1");
    protected readonly ManualClock clock = new(new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero));
    protected readonly RecordingNotifier notifier = new();
    protected ConvoyDirectory dir;

    protected ConvoyTestBase(IRandomSource? random = null) =>
        dir = new ConvoyDirectory(clock, TestData.Content, notifier, TeamTrialCatalog.Fixture(TestData.Content.Catalogue), random);

    protected static string Id(int i) => $"00000000-0000-4000-8000-{i:000000000000}";

    protected static MemberProgress Progress(string id, int normalCleared = 0, int hardCleared = 0) =>
        new(id, Enumerable.Range(0, 30).Select(i => i < normalCleared).ToArray(), Enumerable.Range(0, 30).Select(i => i < hardCleared).ToArray());

    protected static MemberInfo Info(int i, int normalCleared = 0, int hardCleared = 0, params string[] courses) =>
        new($"Driver {i}", Progress(Id(i), normalCleared, hardCleared), courses);

    protected static long Value(ConvoyResult r, string name)
    {
        Assert.True(r.Ok, r.Error?.Message);
        return JsonSerializer.SerializeToElement(r.Value).GetProperty(name).GetInt64();
    }

    protected static JsonElement Result(ConvoyResult r)
    {
        Assert.True(r.Ok, r.Error?.Message);
        return JsonSerializer.SerializeToElement(r.Value, Web);
    }

    protected JsonElement State(int member) => JsonSerializer.SerializeToElement(dir.SnapshotFor(Id(member)).Snapshot, Web);

    protected JsonElement MemberState(int viewer, int member) =>
        State(viewer).GetProperty("members").EnumerateArray().Single(m => m.GetProperty("accountId").GetString() == Id(member));

    protected int MemberCount(int viewer) => State(viewer).GetProperty("members").GetArrayLength();

    protected JsonElement Rejoin(int member) => JsonSerializer.SerializeToElement(dir.RejoinStatus(Id(member)), Web);

    protected static LoadoutInfo Car(string perf = "perf-1", string cosmetic = "cos-1", string car = "V01", int pi = 220) => new(car, pi, perf, cosmetic);

    protected static string Code(ConvoyResult r)
    {
        Assert.True(r.Ok, r.Error?.Message);
        return JsonSerializer.SerializeToElement(r.Value).GetProperty("code").GetString()!;
    }

    /// <summary>Leader 1 plus members 2..n, each connected and with a car. <paramref name="courses"/>[i] = stored courses of member i+1.</summary>
    protected void Convoy(int members, int normalCleared = 0, int hardCleared = 0, Dictionary<int, string[]>? courses = null)
    {
        for (int i = 1; i <= members; i++) dir.Connected(Id(i), V);
        string[] Owned(int i) => courses is not null && courses.TryGetValue(i, out string[]? c) ? c : Array.Empty<string>();
        Assert.True(dir.Create(Id(1), Info(1, normalCleared, hardCleared, Owned(1)), ConvoyPrivacy.InviteOnly).Ok);
        string code = Code(dir.CreateInvite(Id(1)));
        for (int i = 2; i <= members; i++) Assert.True(dir.JoinByCode(Id(i), Info(i, normalCleared, hardCleared, Owned(i)), code).Ok);
        for (int i = 1; i <= members; i++) Assert.True(dir.UpdateLoadout(Id(i), Car()).Ok);
    }

    /// <summary>Joins a new connected member through a fresh invite code created by <paramref name="leader"/>.</summary>
    protected void Join(int member, int leader = 1, int normalCleared = 0, params string[] courses)
    {
        dir.Connected(Id(member), V);
        Assert.True(dir.JoinByCode(Id(member), Info(member, normalCleared, 0, courses), Code(dir.CreateInvite(Id(leader)))).Ok);
        Assert.True(dir.UpdateLoadout(Id(member), Car()).Ok);
    }

    /// <summary>Leader sets an intent, every current member readies for it and the leader enters the mode. Returns the mode revision.</summary>
    protected long EnterMode(ConvoyIntent intent, int leader = 1)
    {
        long rev = Value(dir.SetIntent(Id(leader), intent), "modeRevision");
        foreach (JsonElement m in State(leader).GetProperty("members").EnumerateArray())
            Assert.True(dir.SetModeReady(m.GetProperty("accountId").GetString()!, rev, true).Ok);
        Assert.True(dir.EnterMode(Id(leader), rev).Ok);
        return rev;
    }

    protected static ConvoyIntent Campaign(string mode = "normal") => new(IntentKind.Campaign, mode, null, null);
    protected static ConvoyIntent Freeplay(string submode) => new(IntentKind.Freeplay, null, submode, null);
    protected static ConvoyIntent Challenges(string? trial = null) => new(IntentKind.Challenges, null, null, trial);

    /// <summary>Enters Normal (or Hard) campaign and opens an event proposal; returns its revision.</summary>
    protected long OpenEvent(string stage = "S01", string mode = "normal")
    {
        EnterMode(Campaign(mode));
        clock.Advance(TimeSpan.FromSeconds(15));
        return Value(dir.ProposeEvent(Id(1), new EventRequest(stage, null, null, null, null, null, null)), "proposalRevision");
    }

    protected long ProposeFreeplay(string course, string mode, int ai = 0, IReadOnlyList<string>? rivals = null, IReadOnlyList<string>? legs = null)
    {
        clock.Advance(TimeSpan.FromSeconds(15));
        return Value(dir.ProposeEvent(Id(1), new EventRequest(null, course, mode, null, ai, null, null, legs, AiRivals: rivals)), "proposalRevision");
    }

    protected void ReadyAll(long rev)
    {
        foreach (JsonElement m in State(1).GetProperty("members").EnumerateArray())
            Assert.True(dir.SetReady(m.GetProperty("accountId").GetString()!, rev, m.GetProperty("loadoutRevision").GetInt64(), true).Ok);
    }

    protected bool Ready(int member) => MemberState(1, member).GetProperty("eventReady").GetBoolean();

    protected Dictionary<string, MemberProgress> Fresh(int members, int normalCleared = 0, int hardCleared = 0) =>
        Enumerable.Range(1, members).ToDictionary(Id, i => Progress(Id(i), normalCleared, hardCleared));

    protected Dictionary<string, MemberProgress> FreshFor(IEnumerable<int> members, int normalCleared = 0, int hardCleared = 0) =>
        members.ToDictionary(Id, i => Progress(Id(i), normalCleared, hardCleared));

    protected int NoticeCount(int member, string code) =>
        notifier.To(Id(member), "convoy.notice").Count(n => n.GetProperty("code").GetString() == code);
}
