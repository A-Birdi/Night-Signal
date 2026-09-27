using System.Reflection;
using NightSignal.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace NightSignal.CoreTests;

/// <summary>Domain-separation and Addendum 02 reservation checks on the Local profile document itself.</summary>
public sealed class LocalProfileShapeTests
{
    static readonly string[] Forbidden = { "password", "handle", "email", "token", "secret", "signature", "receipt", "matchid" };

    [Fact]
    public void LocalProfile_CarriesNoCredentialsHandleOrOnlineReceiptShape()
    {
        foreach (Type t in new[] { typeof(LocalProfile), typeof(CardAppearance), typeof(LocalProgressionResult), typeof(RecordEntry), typeof(RecordProvenance) })
            foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance))
                Assert.DoesNotContain(Forbidden, f => m.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProgressionDomain.Local, new LocalProgressionResult().Domain);
    }

    [Fact]
    public void NoProgressionOperation_ReadsTheToyWorkspace_OrOpaqueBuildDocuments()
    {
        // D209: the toy workspace is a non-progression domain. No LocalProgression entry point accepts it.
        foreach (MethodInfo m in typeof(LocalProgression).GetMethods(BindingFlags.Public | BindingFlags.Static))
            foreach (ParameterInfo parameter in m.GetParameters())
            {
                Assert.NotEqual(typeof(ToyWorkspace), parameter.ParameterType);
                Assert.NotEqual(typeof(VersionedDocument), parameter.ParameterType);
            }
        Assert.DoesNotContain(typeof(LocalEventFacts).GetFields(), f => f.FieldType == typeof(ToyWorkspace) || f.FieldType == typeof(VersionedDocument));
    }

    [Fact]
    public void ToyAndCarWorkspaces_RoundTripOpaqueDocumentsWithoutLossOrTrimming()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        JObject canvas = ProfileJson.ParseObject("""{"sheets":[{"id":"s1","strokes":[[0,0,1.5],[2,3,0.25]],"savedAt":"2026-09-27T12:00:00Z"}],"big":12345678901234567}""");
        p.Toys.Put(ToyWorkspace.Canvas, new VersionedDocument { Schema = "night-signal/canvas@1", SchemaVersion = 1, UpdatedUtc = TestContent.T0, Data = canvas });
        p.Toys.Put(ToyWorkspace.PitCrew, new VersionedDocument { Schema = "night-signal/pit-crew@1", Data = new JObject { ["shelf"] = new JArray("BP01") } });
        Assert.Throws<ArgumentException>(() => p.Toys.Put("Bad Id!", new VersionedDocument { Schema = "x", Data = new JObject() }));

        CarWorkspace ws = p.Cars[0].Workspace;
        for (int i = 0; i < 10; i++) // more than the minimum eight: nothing may trim them
            ws.MechanicalLoadouts.Add(new NamedDocument
            {
                SlotId = $"m{i}", Name = i == 0 ? "Wet Grip" : $"Setup {i}",
                Document = new VersionedDocument { Schema = "night-signal/mechanical-loadout@1", Data = new JObject { ["gears"] = i } },
            });
        for (int i = 0; i < CarWorkspace.MinVisualPresetSlots; i++)
            ws.VisualPresets.Add(new NamedDocument { SlotId = $"v{i}", Name = $"Look {i}", Document = new VersionedDocument { Schema = "night-signal/visual-preset@1" } });
        ws.References[CarWorkspace.LastRaceBuild] = new VersionedDocument { Schema = "night-signal/build-reference@1", Data = new JObject { ["pi"] = 512 } };
        ws.AppliedBuild = new VersionedDocument { Schema = "night-signal/applied-build@1", Data = new JObject { ["pi"] = 512 } };
        ws.GarageDraft = new VersionedDocument { Schema = "night-signal/garage-draft@1", Data = new JObject { ["pi"] = 530 } };
        p.Parts.Add(new OwnedPart { PartId = "P-TYRE-T2", Quantity = 1, Source = "purchase", AcquiredUtc = TestContent.T0 });

        var disk = new InMemoryProfileStorage();
        var repo = new ProfileRepository(disk);
        Assert.True(repo.Create(p).Ok);
        LocalProfile back = repo.Load(p.ProfileId).Profile;

        Assert.True(JToken.DeepEquals(canvas, back.Toys.Get(ToyWorkspace.Canvas).Data));
        Assert.Equal("2026-09-27T12:00:00Z", (string)back.Toys.Get(ToyWorkspace.Canvas).Data["sheets"][0]["savedAt"]); // not re-parsed as a date
        Assert.Equal(ToyWorkspace.NonProgressionDomain, back.Toys.ActivityDomain);
        CarWorkspace backWs = back.Cars[0].Workspace;
        Assert.Equal(10, backWs.MechanicalLoadouts.Count);
        Assert.Equal("Wet Grip", backWs.MechanicalLoadouts[0].Name);
        Assert.Equal(5, backWs.VisualPresets.Count);
        Assert.Equal(512, (int)backWs.References[CarWorkspace.LastRaceBuild].Data["pi"]);
        Assert.Equal(530, (int)backWs.GarageDraft.Data["pi"]);
        Assert.Equal("P-TYRE-T2", back.Parts.Single().PartId);
        Assert.True(back.Toys.Reset(ToyWorkspace.PitCrew));
        Assert.Null(back.Toys.Get(ToyWorkspace.PitCrew));
    }

    [Fact]
    public void UnknownMembers_ArePreservedAcrossLoadAndSave()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        JObject doc = ProfileJson.FromObject(p);
        doc["futureOptionalSection"] = new JObject { ["kept"] = true };
        ((JObject)doc["cars"][0])["futureCarField"] = "keep me";
        ((JObject)doc["cars"][0]["workspace"])["pinnedMechanicalLoadouts"] = new JArray("m0", "m3");
        var disk = new InMemoryProfileStorage();
        disk.Write(ProfileRepository.MainName(p.ProfileId), ProfileFileCodec.EncodePayload(doc.ToString(), 1, 0, p.ProfileId));

        var repo = new ProfileRepository(disk);
        LocalProfile loaded = repo.Load(p.ProfileId).Profile;
        Assert.True(repo.Save(loaded).Ok);
        JObject again = ProfileJson.FromObject(repo.Load(p.ProfileId).Profile);
        Assert.True((bool)again["futureOptionalSection"]["kept"]);
        Assert.Equal("keep me", (string)again["cars"][0]["futureCarField"]);
        Assert.Equal(new[] { "m0", "m3" }, again["cars"][0]["workspace"]["pinnedMechanicalLoadouts"].Values<string>());
    }
}
