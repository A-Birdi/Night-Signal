using NightSignal.Core.Meet;

namespace NightSignal.CoreTests;

/// <summary>
/// The meet's twelve emotes (spec §12): the wheel holds each once, each has a bounded duration and a label, and the id
/// the room server accepts from a client is one of the twelve names — never a number or a combination of them.
/// </summary>
public sealed class EmoteTests
{
    [Fact]
    public void TheWheel_HoldsEveryEmoteOnce_WithABoundedDurationAndALabel()
    {
        Emote[] all = Enum.GetValues<Emote>().Where(e => e != Emote.None).ToArray();
        Assert.Equal(Emotes.Count, all.Length);
        Assert.Equal(all.OrderBy(e => e), Emotes.Wheel.OrderBy(e => e));
        foreach (Emote e in Emotes.Wheel)
        {
            Assert.InRange(Emotes.Duration(e), 1.0f, 4.0f);
            Assert.False(string.IsNullOrWhiteSpace(Emotes.Label(e)));
        }
        Assert.Equal(0f, Emotes.Duration(Emote.None));
    }

    [Fact]
    public void TryParse_AcceptsTheTwelveNamesInAnyCase_AndNothingElse()
    {
        foreach (Emote e in Emotes.Wheel)
        {
            Assert.True(Emotes.TryParse(e.ToString(), out Emote parsed));
            Assert.Equal(e, parsed);
            Assert.True(Emotes.TryParse(e.ToString().ToLowerInvariant(), out parsed));
            Assert.Equal(e, parsed);
        }
        foreach (string bad in new[] { null!, "", "None", "none", "0", "1", "12", "200", "-1", "Wave, Bow", "Wave,Bow", " wave", "wave ", "Wave2", "Thumbs-up" })
        {
            Assert.False(Emotes.TryParse(bad, out Emote e), bad ?? "null");
            Assert.Equal(Emote.None, e);
        }
    }
}
