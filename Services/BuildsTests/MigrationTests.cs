using NightSignal.Core.Builds;
using Newtonsoft.Json.Linq;

namespace NightSignal.BuildsTests;

/// <summary>E05 (data level): legacy preset documents migrate without dropping named presets; documents round-trip.</summary>
public sealed class MigrationTests
{
    static readonly DateTime T = TestData.T0;

    static JObject LegacyV1(int presets)
    {
        var list = new JArray();
        for (int i = 1; i <= presets; i++)
            list.Add(new JObject
            {
                ["name"] = i == 3 ? "Wet Grip" : i == 4 ? "wet grip" : $"Preset {i}",
                ["partIds"] = i == 2
                    ? new JArray("TYR-T2-SPORT", "TYR-T1-STREET", "OLD-NITROUS-KIT", "UTL-SHW-5")
                    : new JArray("TYR-T1-STREET", "BRK-T1-PADS", "GBX-T1-FINAL"),
                ["tune"] = new JObject { ["finalDrive"] = 1.06, ["camberFront"] = -2.5 },
                ["note"] = $"note {i}",
            });
        return new JObject
        {
            ["schema"] = CarBuildWorkspace.SchemaId,
            ["schemaVersion"] = 1,
            ["instanceId"] = "ci_old",
            ["modelId"] = "V01",
            ["appliedPartIds"] = new JArray("TYR-T1-STREET"),
            ["appliedTune"] = new JObject(),
            ["tunePresets"] = list,
            ["visualPresets"] = new JArray(new JObject { ["name"] = "Red", ["payload"] = new JObject { ["paint"] = "red" } }),
        };
    }

    [Fact]
    public void LegacyWorkspace_WithMoreThanEightPresets_KeepsEveryNamedPreset()
    {
        MigrationResult m = BuildDocumentCodec.DeserializeWorkspace(LegacyV1(11).ToString(), TestData.Parts, T);
        CarBuildWorkspace ws = m.Workspace;
        Assert.Equal(11, ws.Loadouts.Count);
        Assert.Equal(11, ws.LoadoutCapacity);
        Assert.Equal(CarBuildWorkspace.CurrentSchemaVersion, ws.SchemaVersion);
        Assert.Contains(m.Notices, n => n.Contains("Kept all 11"));
        Assert.Equal("Preset 1", ws.Loadouts[0].Name);
        Assert.Equal("note 1", ws.Loadouts[0].Note);
        // Duplicate names are kept, made distinguishable.
        Assert.Contains(ws.Loadouts, l => l.Name == "Wet Grip");
        Assert.Contains(ws.Loadouts, l => l.Name == "wet grip (2)");
        // Ids placed by slot (not array position); tune values converted; unknown values kept as notices.
        MechanicalLoadout first = ws.Loadouts[0];
        Assert.Equal("GBX-T1-FINAL", first.Build.PartIn(PartSlot.Gearbox));
        Assert.Equal(1060, first.Build.Tuning.Values[TuningKeys.FinalDrive]);
        Assert.Contains(first.Notices, n => n.Contains("camberFront"));
        // Visual presets and applied build preserved; no protected reference invented.
        Assert.Single(ws.VisualPresets);
        Assert.Contains("red", ws.VisualPresets[0].PayloadJson);
        Assert.Equal("TYR-T1-STREET", ws.Applied.Build.PartIn(PartSlot.Tyres));
        Assert.Empty(ws.References);
        Assert.True(ws.VisualPresetCapacity >= 5);
    }

    [Fact]
    public void UnplaceableLegacyParts_AreKeptWithAnExplanation_AndBlockPartialApply()
    {
        CarBuildWorkspace ws = BuildDocumentCodec.DeserializeWorkspace(LegacyV1(3).ToString(), TestData.Parts, T).Workspace;
        MechanicalLoadout odd = ws.Loadouts[1];
        Assert.Equal("TYR-T2-SPORT", odd.Build.PartIn(PartSlot.Tyres));
        Assert.Equal("UTL-SHW-5", odd.Build.UtilityPartId);
        Assert.Equal(new[] { "TYR-T1-STREET", "OLD-NITROUS-KIT" }, odd.UnresolvedPartIds);
        Assert.True(odd.NeedsParts);

        var inv = TestData.Owning("ci_old", "TYR-T2-SPORT", "TYR-T1-STREET", "UTL-SHW-5", "BRK-T1-PADS", "GBX-T1-FINAL");
        BuildContext ctx = TestData.Ctx("V01", inv);
        ws.Applied.BuildHash = ""; // legacy: derived values recomputed on demand
        OperationResult apply = GarageOperations.ApplyLoadout(ws, ws.Revision, odd.LoadoutId, ctx, null, T);
        Assert.Equal(OpStatus.NeedsRepair, apply.Status);
        Assert.Contains(apply.Repairs, r => r.Kind == RepairKind.UnresolvedLegacyPart && r.PartId == "OLD-NITROUS-KIT");
        // Draft route: still blocked until the player explicitly edits the draft.
        OperationResult load = GarageOperations.LoadIntoDraft(ws, ws.Revision, DraftSource.Loadout(odd.LoadoutId), null, ctx, T);
        Assert.True(load.Accepted);
        Assert.Equal(OpStatus.NeedsRepair, GarageOperations.Apply(ws, ws.Revision, ctx, null, T).Status);
        MechanicalSnapshot chosen = ws.Draft.Build.Clone();
        chosen.Tuning.Values.Remove(TuningKeys.FinalDrive); // the legacy final drive has no gearbox part to adjust it here
        Assert.True(GarageOperations.EditDraft(ws, ws.Revision, chosen, ctx, T).Accepted); // explicit choice
        Assert.True(GarageOperations.Apply(ws, ws.Revision, ctx, null, T).Accepted);

        // Revalidation re-derives stats and records repair notices; it never deletes a preset.
        OperationResult rv = GarageOperations.Revalidate(ws, ctx, T);
        Assert.True(rv.Accepted);
        Assert.Equal(3, ws.Loadouts.Count);
        Assert.Contains(ws.Loadouts[1].Notices, n => n.Contains("OLD-NITROUS-KIT"));
        Assert.True(ws.Loadouts[0].DerivedPi > 220);
    }

    [Fact]
    public void StoredDocuments_RoundTrip_AndUnknownDocumentsArePreservedVerbatim()
    {
        var inv = TestData.Owning("ci_rt", "TYR-T2-SPORT", "BRK-T2-KIT");
        BuildContext ctx = TestData.Ctx("V05", inv);
        CarBuildWorkspace ws = GarageOperations.NewWorkspace("ci_rt", ctx, T);
        Assert.True(GarageOperations.EditDraft(ws, ws.Revision, TestData.Build("TYR-T2-SPORT", "BRK-T2-KIT").WithTune(TuningKeys.BrakeBias, 600), ctx, T).Accepted);
        Assert.True(GarageOperations.SaveAs(ws, ws.Revision, "Wet Grip", "rain", ctx, T).Accepted);
        Assert.True(GarageOperations.Apply(ws, ws.Revision, ctx, null, T).Accepted);
        Assert.True(GarageOperations.BeginWorkshopSession(ws, ctx, T).Accepted);
        Assert.True(GarageOperations.SaveVisualPreset(ws, ws.Revision, "Blue", "night-signal/livery@1", "{\"paint\":\"blue\"}", T).Accepted);
        ws.AppliedLiveryHash = "liv-1";

        StoredBuildDocuments docs = BuildDocumentCodec.ToDocuments(ws);
        Assert.Equal(BuildDocumentCodec.LoadoutSchema, docs.MechanicalLoadouts[0].Schema);
        Assert.Equal(ws.Loadouts[0].LoadoutId, docs.MechanicalLoadouts[0].SlotId); // stable ids, not positions
        Assert.Contains("before-workshop", docs.References.Keys);
        Assert.Contains("before-last-apply", docs.References.Keys);

        // An unknown future document in the list must survive.
        docs.MechanicalLoadouts.Add(new StoredBuildDocument { SlotId = "ld-future", Name = "From v9", Schema = "night-signal/mechanical-loadout", SchemaVersion = 9, Data = new JObject { ["x"] = 1 } });
        MigrationResult back = BuildDocumentCodec.FromDocuments("ci_rt", "V05", docs, TestData.Parts, T);
        CarBuildWorkspace r = back.Workspace;
        Assert.Equal(ws.Revision, r.Revision);
        Assert.True(r.Applied.Build.ContentEquals(ws.Applied.Build));
        Assert.Equal(ws.Applied.BuildHash, r.Applied.BuildHash);
        Assert.True(r.Loadouts[0].Build.ContentEquals(ws.Loadouts[0].Build));
        Assert.Equal("Wet Grip", r.Loadouts[0].Name);
        Assert.Equal(2, r.Loadouts.Count);
        Assert.Contains("\"x\":1", r.Loadouts[1].LegacyDocumentJson);
        Assert.Equal(OpStatus.NeedsRepair, GarageOperations.ApplyLoadout(r, r.Revision, "ld-future", ctx, null, T).Status);
        Assert.Equal("liv-1", r.AppliedLiveryHash);
        Assert.True(r.Workshop.Open);
        Assert.True(r.Reference(BuildReferenceKind.BeforeWorkshop).Build.ContentEquals(ws.Reference(BuildReferenceKind.BeforeWorkshop).Build));
        Assert.Equal("Blue", Assert.Single(r.VisualPresets).Name);

        // Whole-document JSON round-trip (current version).
        CarBuildWorkspace again = BuildDocumentCodec.DeserializeWorkspace(BuildDocumentCodec.SerializeWorkspace(ws), TestData.Parts, T).Workspace;
        Assert.Equal(BuildDocumentCodec.SerializeWorkspace(ws), BuildDocumentCodec.SerializeWorkspace(again));
        // Equality and hashes do not depend on the comparer a deserialised dictionary happens to carry.
        Assert.True(again.Applied.Build.ContentEquals(ws.Applied.Build));
        Assert.Equal(ws.Applied.Build.SelectionHash(), again.Applied.Build.SelectionHash());
        var reordered = new MechanicalSnapshot();
        reordered.Parts = new SortedDictionary<string, string>(ws.Applied.Build.Parts, StringComparer.OrdinalIgnoreCase);
        reordered.Tuning.Values = new SortedDictionary<string, int>(ws.Applied.Build.Tuning.Values, Comparer<string>.Create((x, y) => -string.CompareOrdinal(x, y)));
        Assert.True(reordered.ContentEquals(ws.Applied.Build));
        Assert.Equal(ws.Applied.Build.SelectionHash(), reordered.SelectionHash());
        Assert.Equal(TestData.Resolve("V05", ws.Applied.Build).BuildHash, TestData.Resolve("V05", reordered).BuildHash);
    }

    [Fact]
    public void LegacyTunePresetDocument_InTheProfileEnvelope_Migrates()
    {
        var docs = new StoredBuildDocuments();
        docs.MechanicalLoadouts.Add(new StoredBuildDocument
        {
            SlotId = "old-slot-1", Name = "Drift Setup", Schema = "night-signal/tune-preset", SchemaVersion = 1,
            Data = new JObject { ["partIds"] = new JArray("TYR-T2-DRIFT", "DIF-T1-RWD-CLUTCH"), ["tune"] = new JObject { ["diffLock"] = 0.6 } },
        });
        MigrationResult m = BuildDocumentCodec.FromDocuments("ci_x", "V01", docs, TestData.Parts, T);
        MechanicalLoadout l = Assert.Single(m.Workspace.Loadouts);
        Assert.Equal("old-slot-1", l.LoadoutId);
        Assert.Equal("Drift Setup", l.Name);
        Assert.Equal("TYR-T2-DRIFT", l.Build.PartIn(PartSlot.Tyres));
        Assert.Equal(60, l.Build.Tuning.Values[TuningKeys.DiffLock]);
        Assert.Equal(8, m.Workspace.LoadoutCapacity);
    }
}
