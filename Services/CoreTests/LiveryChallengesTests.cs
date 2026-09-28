using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Customization;
using NightSignal.Core.Profiles;

namespace NightSignal.CoreTests;

/// <summary>
/// CH48 Sign Your Car (Appendix E, workshop): an applied livery with a decal and a real two-tone is "signed"; stock, a
/// decal alone, a two-tone alone or a two-tone in the body colour is not; the wire form (what a meet room holds) judges
/// the same; the Local profile accepts CH48 at the meet but no other workshop challenge.
/// </summary>
public sealed class LiveryChallengesTests
{
    static string Authored(string file) => File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", file));
    static readonly Lazy<CustomizationCatalogue> catalogue = new(() => CustomizationCatalogue.Load(Authored(CustomizationCatalogue.FileName)));

    static LiveryDocument Signed()
    {
        LiveryDocument d = catalogue.Value.StockLivery("V01");
        d.Paint.Primary = "#101820";
        d.Paint.Secondary = "#C9A24A";
        d.Paint.TwoTone = "roof";
        d.Decals.Add(new DecalLayer { Shape = "num-7", Color = "#FFFFFF", Zone = "left", UMilli = 450, VMilli = 520, ScaleCenti = 60 });
        return d;
    }

    [Fact]
    public void Signed_NeedsADecalAndTwoPaintRegions()
    {
        Assert.True(LiveryChallenges.Signed(Signed()));
        Assert.False(LiveryChallenges.Signed(catalogue.Value.StockLivery("V01")), "stock");
        LiveryDocument noDecal = Signed();
        noDecal.Decals.Clear();
        Assert.False(LiveryChallenges.Signed(noDecal), "no decal");
        LiveryDocument oneColour = Signed();
        oneColour.Paint.TwoTone = "none";
        Assert.False(LiveryChallenges.Signed(oneColour), "one paint region");
        LiveryDocument sameColour = Signed();
        sameColour.Paint.Secondary = sameColour.Paint.Primary.ToLowerInvariant();
        Assert.False(LiveryChallenges.Signed(sameColour), "a two-tone in the body colour is one region");
    }

    [Fact]
    public void SignedWire_JudgesTheRoomsCopyTheSame()
    {
        Assert.True(LiveryChallenges.SignedWire(LiveryWire.Encode(Signed())));
        LiveryDocument noDecal = Signed();
        noDecal.Decals.Clear();
        Assert.False(LiveryChallenges.SignedWire(LiveryWire.Encode(noDecal)));
        Assert.False(LiveryChallenges.SignedWire(""), "stock");
        Assert.False(LiveryChallenges.SignedWire("[1,\"V01\"]"), "unreadable");
    }

    [Fact]
    public void LocalProfile_AcceptsCH48AtTheMeet_ButNoOtherWorkshopChallenge()
    {
        LocalProfile p = LocalProgressionTests.NewProfile("Robin");
        ContentCatalogue cat = TestContent.Catalogue;
        LocalProgressionResult r = LocalProgression.CompleteMeetChallenge(p, cat, LiveryChallenges.SignYourCar, DateTime.UtcNow);
        Assert.Equal(LocalOperationStatus.Applied, r.Status);
        Assert.True(r.Profile.HasCompletedChallenge("CH48"));
        Assert.Equal(LocalOperationStatus.AlreadyApplied, LocalProgression.CompleteMeetChallenge(r.Profile, cat, "CH48", DateTime.UtcNow).Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.CompleteMeetChallenge(p, cat, "CH46", DateTime.UtcNow).Status);
    }

    [Fact]
    public void RestoresFirstPreset_OnlyBackFromALaterPresetToTheFirst_Exactly()
    {
        var presets = new List<VisualPreset>
        {
            new() { PresetId = "vp-1", PayloadJson = "{\"a\":1}" },
            new() { PresetId = "vp-2", PayloadJson = "{\"b\":2}" },
        };
        Assert.True(LiveryChallenges.RestoresFirstPreset(presets, "vp-2", "hb", "vp-1", "{\"a\":1}", "ha"));
        Assert.False(LiveryChallenges.RestoresFirstPreset(presets, "vp-1", "ha", "vp-2", "{\"b\":2}", "hb"), "forward to the later preset");
        Assert.False(LiveryChallenges.RestoresFirstPreset(presets, "", "hx", "vp-1", "{\"a\":1}", "ha"), "from an edited livery");
        Assert.False(LiveryChallenges.RestoresFirstPreset(presets, "vp-2", "hb", "vp-1", "{\"a\":2}", "ha"), "not exactly the saved look");
        Assert.False(LiveryChallenges.RestoresFirstPreset(presets, "vp-2", "ha", "vp-1", "{\"a\":1}", "ha"), "the same look");
        Assert.False(LiveryChallenges.RestoresFirstPreset(presets, "vp-9", "hb", "vp-1", "{\"a\":1}", "ha"), "a deleted preset");
    }

    [Fact]
    public void LocalProfile_AcceptsCH50InTheGarage_Only()
    {
        LocalProfile p = LocalProgressionTests.NewProfile("Robin");
        ContentCatalogue cat = TestContent.Catalogue;
        Assert.Equal(LocalOperationStatus.Applied, LocalProgression.CompleteGarageChallenge(p, cat, "CH50", DateTime.UtcNow).Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.CompleteGarageChallenge(p, cat, "CH48", DateTime.UtcNow).Status);
        Assert.Equal(LocalOperationStatus.Rejected, LocalProgression.CompleteMeetChallenge(p, cat, "CH50", DateTime.UtcNow).Status);
    }
}
