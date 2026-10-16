using NightSignal.ControlPlane.Toys;
using NightSignal.Core.Toys;

namespace NightSignal.Services.Tests;

/// <summary>The toy routes name an activity by its Core name only.</summary>
public sealed class ToyWireTests
{
    [Fact]
    public void TryActivity_AcceptsTheFiveNamesInAnyCase_AndNothingElse()
    {
        foreach (ToyActivityId a in Enum.GetValues<ToyActivityId>())
        {
            Assert.True(ToyWire.TryActivity(a.ToString(), out ToyActivityId id));
            Assert.Equal(a, id);
            Assert.True(ToyWire.TryActivity(a.ToString().ToLowerInvariant(), out id));
            Assert.Equal(a, id);
        }
        foreach (string? bad in new[] { null, "", "3", "-1", "PitCrew, Greenlight", "PitCrew,Greenlight", " Canvas", "Canvas ", "Toys" })
            Assert.False(ToyWire.TryActivity(bad, out _), bad ?? "null");
    }
}
