using NightSignal.Core.Builds;
using NightSignal.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace NightSignal.BuildsTests;

/// <summary>
/// The applied livery and the visual preset library on a <see cref="CarBuildWorkspace"/> (Addendum 02 §9.1): ApplyLivery,
/// UpdateVisualPreset (confirmed overwrite), RenameVisualPreset, and persistence of the applied livery through the stored
/// documents, the whole-workspace JSON and the Local profile. Builds treats the livery as opaque text; validation and hashing
/// belong to Customization (tested in CoreTests and the control plane).
/// </summary>
public sealed class LiveryWorkspaceTests
{
    const string Car = "V05";
    static readonly DateTime T = TestData.T0;

    // Opaque to Builds: any text the customization system produced.
    const string Blue = "{\"schema\":\"night-signal/livery@1\",\"car\":\"V05\",\"paint\":{\"primary\":\"#1F4E8C\"}}";
    const string BlueHash = "1111111111111111111111111111111111111111111111111111111111111111";
    const string Red = "{\"schema\":\"night-signal/livery@1\",\"car\":\"V05\",\"paint\":{\"primary\":\"#C8102E\"}}";
    const string RedHash = "2222222222222222222222222222222222222222222222222222222222222222";
    const string LiverySchema = "night-signal/livery@1";

    static (CarBuildWorkspace Ws, BuildContext Ctx) New(string instance = "ci_liv")
    {
        var inv = TestData.Owning(instance, "TYR-T2-SPORT", "BRK-T2-KIT");
        BuildContext ctx = TestData.Ctx(Car, inv, 2);
        return (GarageOperations.NewWorkspace(instance, ctx, T), ctx);
    }

    static OperationResult Ok(OperationResult r)
    {
        Assert.True(r.Accepted, r.ToString());
        return r;
    }

    static string SavePreset(CarBuildWorkspace ws, string name, string payload) =>
        Ok(GarageOperations.SaveVisualPreset(ws, ws.Revision, name, LiverySchema, payload, T)).LoadoutId;

    // ------------------------------------------------------------------ ApplyLivery

    [Fact]
    public void ApplyLivery_SetsLiveryHashAndPreset_AdvancesOnlyTheWorkspaceRevision()
    {
        (CarBuildWorkspace ws, BuildContext ctx) = New();
        Assert.True(GarageOperations.EditDraft(ws, ws.Revision, TestData.Build("TYR-T2-SPORT"), ctx, T).Accepted);
        string blue = SavePreset(ws, "Night Blue", Blue);
        string mechanical = Newtonsoft.Json.JsonConvert.SerializeObject(new { ws.Applied, ws.Loadouts, ws.Draft, ws.References });
        string presets = Newtonsoft.Json.JsonConvert.SerializeObject(ws.VisualPresets);
        long rev = ws.Revision;

        OperationResult r = Ok(GarageOperations.ApplyLivery(ws, rev, Blue, BlueHash, blue, T));
        Assert.Equal(rev + 1, ws.Revision);
        Assert.Equal(rev + 1, r.Revision);
        Assert.Equal(blue, r.LoadoutId); // the preset id, like SaveVisualPreset
        Assert.Equal(Blue, ws.AppliedLivery);
        Assert.Equal(BlueHash, ws.AppliedLiveryHash);
        Assert.Equal(blue, ws.AppliedVisualPresetId);
        Assert.False(r.PerformanceChanged);
        Assert.Equal(1, ws.Applied.Revision); // the mechanical applied build and its revision never move
        Assert.Equal(mechanical, Newtonsoft.Json.JsonConvert.SerializeObject(new { ws.Applied, ws.Loadouts, ws.Draft, ws.References }));
        Assert.Equal(presets, Newtonsoft.Json.JsonConvert.SerializeObject(ws.VisualPresets));

        // The same livery + preset again: unchanged, no revision.
        OperationResult same = GarageOperations.ApplyLivery(ws, ws.Revision, Blue, BlueHash, blue, T);
        Assert.True(same.Accepted);
        Assert.Equal(rev + 1, ws.Revision);
        // The same livery as an edit (no preset link) IS a change of the link.
        Ok(GarageOperations.ApplyLivery(ws, ws.Revision, Blue, BlueHash, "", T));
        Assert.Equal("", ws.AppliedVisualPresetId);
        Assert.Equal(rev + 2, ws.Revision);

        // Back to stock.
        Ok(GarageOperations.ApplyLivery(ws, ws.Revision, "", "", null, T));
        Assert.Equal("", ws.AppliedLivery);
        Assert.Equal("", ws.AppliedLiveryHash);
        Assert.Equal(rev + 3, ws.Revision);
        Assert.True(GarageOperations.ApplyLivery(ws, ws.Revision, null, null, null, T).Accepted);
        Assert.Equal(rev + 3, ws.Revision); // stock again: unchanged
    }

    [Fact]
    public void ApplyLivery_RefusesUnknownPresetsStaleRevisionsInconsistentHashesAndOversizeDocuments_ChangingNothing()
    {
        (CarBuildWorkspace ws, _) = New();
        Ok(GarageOperations.ApplyLivery(ws, ws.Revision, Blue, BlueHash, "", T));
        long rev = ws.Revision;

        Assert.Equal(OpStatus.NotFound, GarageOperations.ApplyLivery(ws, rev, Red, RedHash, "vp-missing", T).Status);
        Assert.Equal(OpStatus.StaleRevision, GarageOperations.ApplyLivery(ws, rev - 1, Red, RedHash, "", T).Status);
        Assert.Equal(OpStatus.Rejected, GarageOperations.ApplyLivery(ws, rev, Red, "", "", T).Status);   // a livery needs its hash
        Assert.Equal(OpStatus.Rejected, GarageOperations.ApplyLivery(ws, rev, "", RedHash, "", T).Status); // stock has none
        Assert.Equal(OpStatus.Rejected, GarageOperations.ApplyLivery(ws, rev, new string('x', GarageOperations.MaxLiveryChars + 1), RedHash, "", T).Status);
        Assert.Equal(OpStatus.Rejected, GarageOperations.ApplyLivery(ws, rev, Red, new string('a', GarageOperations.MaxLiveryHashChars + 1), "", T).Status);
        Assert.Equal(rev, ws.Revision);
        Assert.Equal(Blue, ws.AppliedLivery);
        Assert.Equal(BlueHash, ws.AppliedLiveryHash);

        // A document of exactly the bound is accepted (Builds does not look inside).
        Ok(GarageOperations.ApplyLivery(ws, rev, new string('x', GarageOperations.MaxLiveryChars), RedHash, "", T));
    }

    [Fact]
    public void MechanicalOperations_KeepTheAppliedLivery()
    {
        (CarBuildWorkspace ws, BuildContext ctx) = New();
        Ok(GarageOperations.ApplyLivery(ws, ws.Revision, Blue, BlueHash, "", T));
        Ok(GarageOperations.EditDraft(ws, ws.Revision, TestData.Build("TYR-T2-SPORT", "BRK-T2-KIT"), ctx, T));
        Ok(GarageOperations.Apply(ws, ws.Revision, ctx, null, T));
        Ok(GarageOperations.SaveAs(ws, ws.Revision, "Wet", "", ctx, T, fromApplied: true));
        Ok(GarageOperations.LoadIntoDraft(ws, ws.Revision, DraftSource.Reference(BuildReferenceKind.BeforeLastApply), null, ctx, T));
        Ok(GarageOperations.Apply(ws, ws.Revision, ctx, null, T));
        Assert.Equal(Blue, ws.AppliedLivery);
        Assert.Equal(BlueHash, ws.AppliedLiveryHash);
    }

    // ------------------------------------------------------------------ visual preset update / rename / delete

    [Fact]
    public void UpdateVisualPreset_NeedsTheConfirmationTokenOfThisRevision_AndAnIdenticalPayloadIsUnchanged()
    {
        (CarBuildWorkspace ws, _) = New();
        string id = SavePreset(ws, "Night Blue", Blue);
        long rev = ws.Revision;

        OperationResult ask = GarageOperations.UpdateVisualPreset(ws, rev, id, LiverySchema, Red, null, T);
        Assert.Equal(OpStatus.ConfirmationRequired, ask.Status);
        Assert.StartsWith("cf-", ask.ConfirmationToken);
        Assert.Equal(rev, ws.Revision);
        Assert.Equal(Blue, ws.VisualPresets[0].PayloadJson); // nothing changed yet
        Assert.Equal(OpStatus.ConfirmationRequired, GarageOperations.UpdateVisualPreset(ws, rev, id, LiverySchema, Red, "cf-forged", T).Status);
        // The loadout overwrite token for the same id is not this operation's token.
        Assert.NotEqual(GarageOperations.ConfirmationTokenFor(ws, "overwrite", id), ask.ConfirmationToken);

        DateTime later = T.AddMinutes(5);
        OperationResult done = Ok(GarageOperations.UpdateVisualPreset(ws, rev, id, LiverySchema, Red, ask.ConfirmationToken, later));
        Assert.Equal(id, done.LoadoutId);
        Assert.Equal(rev + 1, ws.Revision);
        VisualPreset p = Assert.Single(ws.VisualPresets);
        Assert.Equal(Red, p.PayloadJson);
        Assert.Equal("Night Blue", p.Name);
        Assert.Equal(later, p.UpdatedUtc);

        // Identical payload: unchanged without any token. A token from an older revision is no longer valid.
        Assert.True(GarageOperations.UpdateVisualPreset(ws, ws.Revision, id, LiverySchema, Red, null, T).Accepted);
        Assert.Equal(rev + 1, ws.Revision);
        Assert.Equal(OpStatus.ConfirmationRequired, GarageOperations.UpdateVisualPreset(ws, ws.Revision, id, LiverySchema, Blue, ask.ConfirmationToken, T).Status);
        Assert.Equal(OpStatus.NotFound, GarageOperations.UpdateVisualPreset(ws, ws.Revision, "vp-missing", LiverySchema, Blue, null, T).Status);
        Assert.Equal(OpStatus.StaleRevision, GarageOperations.UpdateVisualPreset(ws, rev, id, LiverySchema, Blue, null, T).Status);
    }

    [Fact]
    public void UpdatingOrDeletingTheAppliedPreset_KeepsTheAppliedLivery_ButUnlinksItWhenTheyDiffer()
    {
        (CarBuildWorkspace ws, _) = New();
        string id = SavePreset(ws, "Night Blue", Blue);
        Ok(GarageOperations.ApplyLivery(ws, ws.Revision, Blue, BlueHash, id, T));

        // Overwriting ANOTHER preset leaves the applied link alone.
        string other = SavePreset(ws, "Scratch", Red);
        Ok(GarageOperations.UpdateVisualPreset(ws, ws.Revision, other, LiverySchema, Blue,
            GarageOperations.ConfirmationTokenFor(ws, "overwrite-visual", other), T));
        Assert.Equal(id, ws.AppliedVisualPresetId);

        // A different look saved over the applied preset: the applied livery stays, the link goes.
        Ok(GarageOperations.UpdateVisualPreset(ws, ws.Revision, id, LiverySchema, Red, GarageOperations.ConfirmationTokenFor(ws, "overwrite-visual", id), T));
        Assert.Equal("", ws.AppliedVisualPresetId);
        Assert.Equal(Blue, ws.AppliedLivery);

        Ok(GarageOperations.ApplyLivery(ws, ws.Revision, Red, RedHash, id, T));
        Assert.Equal(id, ws.AppliedVisualPresetId);
        Ok(GarageOperations.DeleteVisualPreset(ws, ws.Revision, id, GarageOperations.ConfirmationTokenFor(ws, "delete-visual", id)));
        Assert.Equal("", ws.AppliedVisualPresetId);
        Assert.Equal(Red, ws.AppliedLivery);
        Assert.Equal(RedHash, ws.AppliedLiveryHash);
    }

    [Fact]
    public void RenameVisualPreset_UsesTheSaveNameRules_AndRefusesDuplicates()
    {
        (CarBuildWorkspace ws, _) = New();
        string blue = SavePreset(ws, "Night Blue", Blue);
        SavePreset(ws, "Signal Red", Red);
        long rev = ws.Revision;

        Assert.Equal(OpStatus.DuplicateName, GarageOperations.RenameVisualPreset(ws, rev, blue, "signal red", T).Status);
        Assert.Equal(OpStatus.InvalidName, GarageOperations.RenameVisualPreset(ws, rev, blue, "   ", T).Status);
        Assert.Equal(OpStatus.InvalidName, GarageOperations.RenameVisualPreset(ws, rev, blue, "<b>x</b>", T).Status);
        Assert.Equal(OpStatus.InvalidName, GarageOperations.RenameVisualPreset(ws, rev, blue, new string('x', GarageOperations.MaxNameLength + 1), T).Status);
        Assert.Equal(OpStatus.NotFound, GarageOperations.RenameVisualPreset(ws, rev, "vp-missing", "Anything", T).Status);
        Assert.Equal(OpStatus.StaleRevision, GarageOperations.RenameVisualPreset(ws, rev - 1, blue, "Anything", T).Status);
        Assert.Equal(rev, ws.Revision);
        Assert.Equal("Night Blue", ws.VisualPresets[0].Name);

        Assert.True(GarageOperations.RenameVisualPreset(ws, rev, blue, " Night Blue ", T).Accepted); // same name: unchanged
        Assert.Equal(rev, ws.Revision);
        OperationResult renamed = Ok(GarageOperations.RenameVisualPreset(ws, rev, blue, "  Midnight  ", T));
        Assert.Equal(blue, renamed.LoadoutId);
        Assert.Equal("Midnight", ws.VisualPresets[0].Name);
        Assert.Equal(rev + 1, ws.Revision);
        // A case-only change of its own name is allowed (only OTHER presets count as duplicates).
        Ok(GarageOperations.RenameVisualPreset(ws, ws.Revision, blue, "MIDNIGHT", T));
        Assert.Equal("MIDNIGHT", ws.VisualPresets[0].Name);
        Assert.Equal(Blue, ws.VisualPresets[0].PayloadJson);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void AppliedLivery_RoundTripsThroughStoredDocumentsAndWholeWorkspaceJson()
    {
        (CarBuildWorkspace ws, _) = New();
        string id = SavePreset(ws, "Night Blue", Blue);
        Ok(GarageOperations.ApplyLivery(ws, ws.Revision, Blue, BlueHash, id, T));

        StoredBuildDocuments docs = BuildDocumentCodec.ToDocuments(ws);
        Assert.Equal(Blue, (string)docs.WorkspaceState.Data["appliedLivery"]);
        // Through JSON text, as the Local profile stores it.
        docs.WorkspaceState.Data = JToken.Parse(docs.WorkspaceState.Data.ToString(Newtonsoft.Json.Formatting.None));
        CarBuildWorkspace back = BuildDocumentCodec.FromDocuments(ws.Car.InstanceId, Car, docs, TestData.Parts, T).Workspace;
        Assert.Equal(Blue, back.AppliedLivery);
        Assert.Equal(BlueHash, back.AppliedLiveryHash);
        Assert.Equal(id, back.AppliedVisualPresetId);
        Assert.Equal(ws.Revision, back.Revision);

        CarBuildWorkspace again = BuildDocumentCodec.DeserializeWorkspace(BuildDocumentCodec.SerializeWorkspace(ws), TestData.Parts, T).Workspace;
        Assert.Equal(Blue, again.AppliedLivery);
        Assert.Equal(BlueHash, again.AppliedLiveryHash);
        Assert.Equal(Blue, ws.Clone().AppliedLivery);
    }

    [Fact]
    public void OlderDocumentsWithoutAppliedLivery_ReadAsStock_AndAStockWorkspaceKeepsItsEarlierStoredForm()
    {
        (CarBuildWorkspace ws, _) = New();
        // A workspace state document exactly as written before the applied livery existed.
        var docs = new StoredBuildDocuments
        {
            WorkspaceState = new StoredBuildDocument
            {
                SlotId = "state", Name = "state", Schema = BuildDocumentCodec.WorkspaceStateSchema, SchemaVersion = 1,
                Data = new JObject
                {
                    ["instanceId"] = "ci_old", ["modelId"] = Car, ["revision"] = 7, ["loadoutCapacity"] = 8, ["visualPresetCapacity"] = 5,
                    ["appliedVisualPresetId"] = "vp-blue", ["appliedLiveryHash"] = "liv-1", ["workshopOpen"] = false,
                    ["workshopOpenedUtc"] = "0001-01-01T00:00:00Z",
                },
            },
        };
        CarBuildWorkspace old = BuildDocumentCodec.FromDocuments("ci_old", Car, docs, TestData.Parts, T).Workspace;
        Assert.Equal("", old.AppliedLivery);
        Assert.Equal("liv-1", old.AppliedLiveryHash);
        Assert.Equal("vp-blue", old.AppliedVisualPresetId);
        Assert.Equal(7, old.Revision);

        // The stock state document carries no appliedLivery key at all: byte-identical to the earlier form.
        JObject stockState = (JObject)BuildDocumentCodec.ToDocuments(ws).WorkspaceState.Data;
        Assert.Null(stockState.Property("appliedLivery"));
        Assert.Equal(new[] { "instanceId", "modelId", "revision", "loadoutCapacity", "visualPresetCapacity", "appliedVisualPresetId",
            "appliedLiveryHash", "workshopOpen", "workshopOpenedUtc" }, stockState.Properties().Select(p => p.Name));

        // A whole-workspace v2 document from before the field existed.
        JObject whole = JObject.Parse(BuildDocumentCodec.SerializeWorkspace(ws));
        Assert.True(whole.Remove("AppliedLivery"));
        CarBuildWorkspace fromWhole = BuildDocumentCodec.DeserializeWorkspace(whole.ToString(), TestData.Parts, T).Workspace;
        Assert.Equal("", fromWhole.AppliedLivery);
        Assert.Equal(CarBuildWorkspace.CurrentSchemaVersion, fromWhole.SchemaVersion);
    }

    [Fact]
    public void LocalGarage_SavesAndLoadsTheAppliedLivery_WithoutAnUpgradeOnRead()
    {
        LocalProgressionResult created = LocalProgression.NewProfile(TestData.Content, null,
            new NewLocalProfileRequest { DisplayName = "Robin", StarterCarModelId = "V01", Utc = T });
        Assert.Equal(LocalOperationStatus.Applied, created.Status);
        LocalProfile profile = created.Profile;
        string instance = profile.Cars[0].InstanceId;

        LocalWorkspaceLoad fresh = LocalGarage.LoadWorkspace(profile, TestData.Content, TestData.Parts, instance, T);
        Assert.True(fresh.Ok, fresh.Reason);
        Assert.True(fresh.Created);
        CarBuildWorkspace ws = fresh.Workspace;

        // A stock workspace saved and loaded again is not "upgraded on read".
        LocalProgressionResult savedStock = LocalGarage.SaveWorkspace(profile, instance, ws, fresh.StoredRevision);
        Assert.Equal(LocalOperationStatus.Applied, savedStock.Status);
        profile = savedStock.Profile;
        LocalWorkspaceLoad stock = LocalGarage.LoadWorkspace(profile, TestData.Content, TestData.Parts, instance, T);
        Assert.True(stock.Ok, stock.Reason);
        Assert.False(stock.Migrated, string.Join("; ", stock.Notices));
        Assert.Equal("", stock.Workspace.AppliedLivery);

        ws = stock.Workspace;
        string id = SavePreset(ws, "Night Blue", Blue);
        Ok(GarageOperations.ApplyLivery(ws, ws.Revision, Blue, BlueHash, id, T));
        LocalProgressionResult saved = LocalGarage.SaveWorkspace(profile, instance, ws, stock.StoredRevision);
        Assert.Equal(LocalOperationStatus.Applied, saved.Status);
        Assert.Equal(saved.BalanceBefore, saved.BalanceAfter); // appearance never touches the wallet

        LocalWorkspaceLoad back = LocalGarage.LoadWorkspace(saved.Profile, TestData.Content, TestData.Parts, instance, T);
        Assert.True(back.Ok, back.Reason);
        Assert.False(back.Migrated, string.Join("; ", back.Notices));
        Assert.Equal(ws.Revision, back.StoredRevision);
        Assert.Equal(Blue, back.Workspace.AppliedLivery);
        Assert.Equal(BlueHash, back.Workspace.AppliedLiveryHash);
        Assert.Equal(id, back.Workspace.AppliedVisualPresetId);
        Assert.Equal(Blue, Assert.Single(back.Workspace.VisualPresets).PayloadJson);

        // Saving the identical workspace again is idempotent.
        Assert.Equal(LocalOperationStatus.AlreadyApplied, LocalGarage.SaveWorkspace(saved.Profile, instance, back.Workspace).Status);
    }
}
