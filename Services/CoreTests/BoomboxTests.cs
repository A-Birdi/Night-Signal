using NightSignal.Core.Meet;

namespace NightSignal.CoreTests;

/// <summary>
/// The meet's shared boombox (Addendum 01 §11.4): a request made while the default bed plays starts as soon as the
/// room-wide ten-second interval allows, even when nobody touches the boombox again. (The EditMode MeetRulesTests cover
/// the lease, queue and leaver rules.)
/// </summary>
public sealed class BoomboxTests
{
    static double Len(string id) => id == BoomboxState.DefaultCue ? 120 : id.StartsWith("MUS_") ? 100 : 0;

    [Fact]
    public void ARequestHeldByTheInterval_StartsByItselfWhenTheIntervalEnds()
    {
        var b = new BoomboxState("room", Len, 0);
        b.Acquire("a", 0);
        Assert.Equal(BoomboxStatus.Ok, b.Enqueue("a", "MUS_GARAGE", true, 0));
        Assert.Equal("MUS_GARAGE", b.TrackId);
        Assert.Equal(BoomboxStatus.Ok, b.Skip("a", 10_000));
        Assert.Equal(BoomboxState.DefaultCue, b.TrackId);
        b.Release("a");

        // Two seconds after that skip: queued, held by the room-wide interval.
        b.Acquire("b", 12_000);
        Assert.Equal(BoomboxStatus.Ok, b.Enqueue("b", "MUS_TITLE", true, 12_000));
        b.Release("b");
        Assert.Equal(BoomboxState.DefaultCue, b.TrackId);
        b.Tick(19_999);
        Assert.Equal(BoomboxState.DefaultCue, b.TrackId);

        int revision = b.Revision;
        b.Tick(20_000);
        Assert.Equal("MUS_TITLE", b.TrackId);
        Assert.Equal("b", b.SubmittedBy);
        Assert.Empty(b.Queue);
        Assert.True(b.Revision > revision, "clients are told");
        Assert.Equal(0.0, b.PositionSeconds(20_000));
        Assert.Equal(20_000, b.LastUserChangeMs);

        // It then plays through and the default bed returns, as for any request.
        b.Tick(120_000);
        Assert.Equal(BoomboxState.DefaultCue, b.TrackId);
        b.Tick(500_000);
        Assert.Equal(BoomboxState.DefaultCue, b.TrackId);
    }

    [Fact]
    public void TheDefaultBed_WithNothingQueued_StaysPut()
    {
        var b = new BoomboxState("room", Len, 0);
        int revision = b.Revision;
        b.Tick(60_000);
        b.Tick(600_000);
        Assert.Equal(BoomboxState.DefaultCue, b.TrackId);
        Assert.Equal(revision, b.Revision);
        Assert.Equal(0.0, b.PositionSeconds(240_000), 9);
    }
}
