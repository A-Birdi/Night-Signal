using Newtonsoft.Json.Linq;
using NightSignal.Core.Toys;

namespace NightSignal.Toys.Tests;

/// <summary>
/// Strict payload access: whatever a client sends, a bad number is refused as a toy reason (never an unexpected
/// exception, which the host would report as a toy fault to the whole convoy).
/// </summary>
public sealed class PayloadReaderTests
{
    const string Huge = "123456789012345678901234567890";

    static ToyReason Refusal(Action read)
    {
        var e = Assert.Throws<ToyPayloadException>(read);
        return e.Reason;
    }

    [Fact]
    public void IntegersBeyondSixtyFourBits_AreRefusedAsOutOfRange_InEveryReader()
    {
        var p = new PayloadReader(JObject.Parse("{\"n\":" + Huge + ",\"pts\":[1,2," + Huge + "],\"neg\":-" + Huge + "}"));
        Assert.Equal(ToyReason.OutOfRange, Refusal(() => p.Long("n", 0, 10)));
        Assert.Equal(ToyReason.OutOfRange, Refusal(() => p.Int("neg", 0, 10)));
        Assert.Equal(ToyReason.OutOfRange, Refusal(() => p.IntArray("pts", 8, 0, 100)));
        Assert.Equal(ToyReason.OutOfRange, Refusal(() => p.Double("n", 0, 10)));
        Assert.Equal(ToyReason.OutOfRange, Refusal(() => p.Double("neg", -10, 10)));
    }

    [Fact]
    public void Numbers_TypesAndEnums_AreStrict()
    {
        var p = new PayloadReader(JObject.Parse("{\"a\":[1,2,3],\"f\":1.5,\"s\":\"x\",\"k\":\"Canvas\",\"num\":\"1\",\"two\":\"Canvas, Greenlight\"}"));
        Assert.Equal(new[] { 1, 2, 3 }, p.IntArray("a", 3, 0, 3));
        Assert.Equal(ToyReason.TooLarge, Refusal(() => p.IntArray("a", 2, 0, 3)));
        Assert.Equal(ToyReason.OutOfRange, Refusal(() => p.IntArray("a", 3, 0, 2)));
        Assert.Equal(ToyReason.Malformed, Refusal(() => p.IntArray("f", 3, 0, 2)));
        Assert.Equal(1.5, p.Double("f", 0, 2));
        Assert.Equal(ToyReason.Malformed, Refusal(() => p.Long("f", 0, 2)));
        Assert.Equal(ToyReason.Malformed, Refusal(() => p.Double("s", 0, 2)));
        Assert.Equal(ToyReason.Malformed, Refusal(() => p.Long("missing", 0, 2)));
        Assert.Equal(ToyActivityId.Canvas, p.Enum<ToyActivityId>("k"));
        Assert.Equal(ToyReason.Malformed, Refusal(() => p.Enum<ToyActivityId>("num")));
        Assert.Equal(ToyReason.Malformed, Refusal(() => p.Enum<ToyActivityId>("two")));
    }
}
