using Newtonsoft.Json.Linq;
using NightSignal.Core.Toys;

namespace NightSignal.Toys.Tests;

/// <summary>Loads the authored toy content copied next to the test assembly (see the csproj).</summary>
public static class ToyData
{
    static readonly Lazy<ToyContent> content = new(() => ToyContent.Load(Documents()));

    public static ToyContent Content => content.Value;

    public static Dictionary<string, string> Documents()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "content", "toys");
        var docs = new Dictionary<string, string>();
        foreach (string f in ToyContent.Files) docs[f] = File.ReadAllText(Path.Combine(dir, f));
        return docs;
    }

    public static string RepoRoot()
    {
        for (DirectoryInfo d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "Assets")) && Directory.Exists(Path.Combine(d.FullName, "Services"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root not found above the test output folder");
    }
}

/// <summary>
/// A convoy-session harness: host clock, per-member sequences and unique request ids, exactly like a control-plane host
/// that serializes all calls for one session.
/// </summary>
public sealed class Rig
{
    public readonly DowntimeSession S;
    public long Now;
    readonly Dictionary<string, long> seq = new();
    int req;

    public Rig(params string[] members)
    {
        Now = 1_000_000;
        S = new DowntimeSession("convoy-1", ToyData.Content, Now, 42);
        foreach (string m in members) Assert.Equal(JoinOutcome.NewSeat, S.Join(m, 1, Now));
    }

    public Rig(DowntimeSession restored, long now)
    {
        S = restored;
        Now = now;
        foreach (MemberSeat seat in restored.Seats) seq[seat.MemberId] = seat.LastSequence;
    }

    public string NextRequest() => "r-" + (++req);

    public ToyCommand Cmd(string member, ToyActivityId activity, string kind, JObject payload = null, string requestId = null, int? epoch = null)
    {
        long next = (seq.TryGetValue(member, out long s) ? s : 0) + 1;
        seq[member] = next;
        ToyCommand cmd = S.Command(member, activity, kind, payload, next, requestId ?? NextRequest());
        if (epoch.HasValue) cmd.Epoch = epoch.Value;
        return cmd;
    }

    public ToyResult Do(string member, ToyActivityId activity, string kind, JObject payload = null, string requestId = null, int? epoch = null) =>
        S.Submit(Cmd(member, activity, kind, payload, requestId, epoch), Now);

    /// <summary>Submits an existing command again (a retry/duplicate), with a fresh sequence number unless told otherwise.</summary>
    public ToyResult Resend(ToyCommand cmd, bool freshSequence = true)
    {
        ToyCommand copy = cmd.Clone();
        if (freshSequence)
        {
            long next = (seq.TryGetValue(cmd.MemberId, out long s) ? s : 0) + 1;
            seq[cmd.MemberId] = next;
            copy.Sequence = next;
        }
        return S.Submit(copy, Now);
    }

    public void ResetSequence(string member) => seq[member] = 0;

    /// <summary>Advances the host clock in 50 ms host ticks (the simulation itself runs fixed steps).</summary>
    public void Tick(long ms)
    {
        long end = Now + ms;
        while (Now < end)
        {
            Now = Math.Min(end, Now + 50);
            S.Advance(Now);
        }
    }

    public void Expect(ToyResult r, string because = "")
    {
        Assert.True(r.Accepted, "expected Accepted but got " + r + " " + because);
    }
}
