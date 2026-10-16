using NightSignal.Core.Meet;

namespace NightSignal.CoreTests;

/// <summary>
/// The meet's "SIGNAL" ribbon (spec §12): one current notice and at most three queued, bursts of one kind coalesced,
/// and nothing lost — a notice that cannot queue still reaches the event list. (The EditMode MeetRulesTests cover the
/// timing and coalescing; these cover a full queue.)
/// </summary>
public sealed class SignalRibbonTests
{
    [Fact]
    public void FullOfFourDifferentKinds_NeverQueuesMoreThanThree_AndTheFourthReachesTheEventList()
    {
        var q = new SignalRibbonQueue();
        Assert.True(q.Post(NoticeKind.Arrived, "arrived:a", "Aki"));
        Assert.True(q.Post(NoticeKind.Departed, "left:b", "Bo"));
        Assert.True(q.Post(NoticeKind.Disconnected, "disconnected:c", "Cy"));
        Assert.Equal(SignalRibbonQueue.MaxQueued, q.Queued.Count);

        Assert.False(q.Post(NoticeKind.LeftToRace, "left:d", "Di"));
        Assert.Equal(SignalRibbonQueue.MaxQueued, q.Queued.Count);
        Assert.Equal(new[] { "Di left to race" }, q.History);
        Assert.False(q.Post(NoticeKind.LeftToRace, "left:d", "Di"), "a replay of the same event");
        Assert.Single(q.History);

        // A burst of a kind already waiting still coalesces into it.
        Assert.True(q.Post(NoticeKind.Arrived, "arrived:e", "Eli"));
        Assert.Equal(SignalRibbonQueue.MaxQueued, q.Queued.Count);
        Assert.Equal("2 drivers arrived", q.Queued[0].Display());

        float total = SignalRibbonQueue.EnterSeconds + SignalRibbonQueue.HoldSeconds + SignalRibbonQueue.ExitSeconds;
        var shown = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            q.Tick(0.01f);
            if (q.Current != null) shown.Add(q.Current.Display());
            q.Tick(total);
        }
        Assert.Equal(new[] { "2 drivers arrived", "Bo left", "Cy disconnected" }, shown);
        Assert.Equal(new[] { "Di left to race", "2 drivers arrived", "Bo left", "Cy disconnected" }, q.History);
    }

    [Fact]
    public void Full_AnArrivalDisplacesTheOldestWaitingInfoNotice_AndAnInfoNoticeGoesStraightToTheList()
    {
        var q = new SignalRibbonQueue();
        q.Post(NoticeKind.Info, "i1", "Offline meet");
        q.Post(NoticeKind.Info, "i2", "Boombox: Night Signal");
        q.Post(NoticeKind.Departed, "left:b", "Bo");
        Assert.False(q.Post(NoticeKind.Info, "i3", "Photo saved"));
        Assert.Equal(new[] { "Photo saved" }, q.History);

        Assert.True(q.Post(NoticeKind.Arrived, "arrived:a", "Aki"));
        Assert.Equal(SignalRibbonQueue.MaxQueued, q.Queued.Count);
        Assert.Equal(new[] { "Photo saved", "Offline meet" }, q.History);
        Assert.Equal(new[] { "Boombox: Night Signal", "Bo left", "Aki arrived" }, q.Queued.Select(n => n.Display()));
    }
}
