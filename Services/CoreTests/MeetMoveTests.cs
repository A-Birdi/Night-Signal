using NightSignal.Core.Meet;

namespace NightSignal.CoreTests;

/// <summary>
/// A walking pose at the meet is replicated to every visitor, so the room keeps only finite numbers: a pose with a
/// non-finite position, heading or speed is corrected and the last good pose stands. (The EditMode MeetRulesTests cover
/// the speed envelope and the walkable area.)
/// </summary>
public sealed class MeetMoveTests
{
    [Fact]
    public void NonFinitePoses_AreCorrected_AndTheLastGoodPoseStands()
    {
        var r = new MeetRoom("m1", MeetKind.Public, "", id => id == BoomboxState.DefaultCue ? 120 : 100, 0);
        Assert.Equal(MeetJoinStatus.Ok, r.Join("a", "A", "V01", "", "", 0, out MeetMember m));
        Assert.True(r.CompleteArrival("a", 3500));
        float x = m.X, z = m.Z, yaw = m.Yaw;
        long poseMs = m.PoseMs;

        int seq = 0;
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.Equal(MeetMoveStatus.Corrected, r.Move("a", bad, z, 0f, 1f, ++seq, 4000));
            Assert.Equal(MeetMoveStatus.Corrected, r.Move("a", x, bad, 0f, 1f, ++seq, 4000));
            Assert.Equal(MeetMoveStatus.Corrected, r.Move("a", x, z, bad, 1f, ++seq, 4000));
            Assert.Equal(MeetMoveStatus.Corrected, r.Move("a", x, z, 0f, bad, ++seq, 4000));
        }
        Assert.Equal((x, z, yaw, poseMs), (m.X, m.Z, m.Yaw, m.PoseMs));

        Assert.Equal(MeetMoveStatus.Accepted, r.Move("a", x, z, 1.25f, 99f, ++seq, 4500));
        Assert.Equal(1.25f, m.Yaw);
        Assert.Equal(MeetRoom.MaxSpeed, m.Speed);
        Assert.Equal(MeetMoveStatus.Ignored, r.Move("a", x, z, 0f, 0f, seq, 4600));
    }
}
