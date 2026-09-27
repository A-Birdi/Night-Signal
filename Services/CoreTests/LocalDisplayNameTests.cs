using NightSignal.Core.Profiles;

namespace NightSignal.CoreTests;

public sealed class LocalDisplayNameTests
{
    [Theory]
    [InlineData("R", "R")]                                        // one grapheme is enough locally
    [InlineData("Robin", "Robin")]
    [InlineData("  Kaede   Ueno  ", "Kaede Ueno")]
    [InlineData("楓の走り屋", "楓の走り屋")]
    [InlineData("Zoë", "Zoë")]
    [InlineData("👨‍👩‍👧 Crew", "👨‍👩‍👧 Crew")]                     // a ZWJ family emoji is one character on .NET 5+
    [InlineData("Twenty-four characters!!", "Twenty-four characters!!")]
    [InlineData("<b>Bold</b>", "<b>Bold</b>")]                    // data, rendered as literal text (never parsed as rich text)
    public void AcceptsReasonableNames(string input, string expected)
    {
        Assert.True(LocalDisplayName.TryNormalize(input, out string name, out string error), error);
        Assert.Equal(expected, name);
        Assert.True(LocalDisplayName.IsValid(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Twenty-five characters!!!")]
    [InlineData("Tab\tName")]
    [InlineData("Line\nBreak")]
    [InlineData("Evil‮gnp.exe")]      // right-to-left override
    [InlineData("Zero​width")]        // zero-width space (format character)
    [InlineData("PrivateUse")]
    [InlineData("Ideographic　Space")]
    public void RejectsControlFormattingAndOutOfRangeNames(string input) =>
        Assert.False(LocalDisplayName.TryNormalize(input, out _, out _));

    [Fact]
    public void RejectsLoneSurrogates()
    {
        // Built at runtime: attribute metadata is UTF-8 and would silently turn a lone surrogate into U+FFFD.
        Assert.False(LocalDisplayName.TryNormalize("Lone" + (char)0xD800 + "High", out _, out _));
        Assert.False(LocalDisplayName.TryNormalize("Lone" + (char)0xDC00 + "Low", out _, out _));
        Assert.False(LocalDisplayName.TryNormalize("Trailing" + (char)0xD83D, out _, out _));
    }

    [Fact]
    public void CountsGraphemesNotUtf16Units_AndBoundsCombiningStacks()
    {
        Assert.Equal(1, LocalDisplayName.CountGraphemes("é"));
        Assert.True(LocalDisplayName.TryNormalize(string.Concat(Enumerable.Repeat("楓", 24)), out _, out _));
        Assert.False(LocalDisplayName.TryNormalize(string.Concat(Enumerable.Repeat("楓", 25)), out _, out _));
        Assert.False(LocalDisplayName.TryNormalize("Z" + new string('́', 30) + "alg", out _, out _));
        Assert.True(LocalDisplayName.TryNormalize("éé", out string composed, out _)); // NFC-normalised
        Assert.Equal("éé", composed);
    }
}
