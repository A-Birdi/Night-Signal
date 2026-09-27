using NightSignal.ControlPlane.Players;

namespace NightSignal.Services.Tests;

public sealed class DisplayNameTests
{
    [Theory]
    [InlineData("Ren", "Ren")]
    [InlineData("  Kaede   Ueno  ", "Kaede Ueno")]
    [InlineData("楓の走り屋", "楓の走り屋")]
    [InlineData("Zoë", "Zoë")]
    [InlineData("👨‍👩‍👧 Crew", "👨‍👩‍👧 Crew")]           // a ZWJ family emoji is one character
    [InlineData("Twenty Characters!!!", "Twenty Characters!!!")]
    public void AcceptsReasonableNames(string input, string expected)
    {
        Assert.True(DisplayNameRules.TryNormalize(input, out string name, out string error), error);
        Assert.Equal(expected, name);
    }

    [Fact]
    public void CountsGraphemesNotUtf16Units()
    {
        // 20 flag emoji = 20 user-perceived characters (80 UTF-16 units) but 160 UTF-8 bytes: refused by the byte bound.
        Assert.False(DisplayNameRules.TryNormalize(string.Concat(Enumerable.Repeat("🇯🇵", 20)), out _, out _));
        Assert.True(DisplayNameRules.TryNormalize(string.Concat(Enumerable.Repeat("🇯🇵", 5)), out _, out string error), error);
        Assert.True(DisplayNameRules.TryNormalize("ééé", out string composed, out _)); // NFC-normalised
        Assert.Equal("ééé", composed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("   ")]
    [InlineData("Twenty-one characters")]
    [InlineData("<color=red>Boss</color>")]
    [InlineData("<b>Bold</b>")]
    [InlineData("Name<sprite=1>")]
    [InlineData("Tab\tName")]
    [InlineData("Line\nBreak")]
    [InlineData("Evil‮gnp.exe")]      // right-to-left override
    [InlineData("Zero​width")]        // zero-width space (format character)
    [InlineData("PrivateUse")]
    [InlineData("Ideographic　Space")]
    public void RejectsMarkupControlAndOutOfRangeNames(string? input) =>
        Assert.False(DisplayNameRules.TryNormalize(input, out _, out _));

    [Fact]
    public void RejectsCombiningMarkStacking() =>
        Assert.False(DisplayNameRules.TryNormalize("Z" + new string('́', 30) + "alg", out _, out _));
}
