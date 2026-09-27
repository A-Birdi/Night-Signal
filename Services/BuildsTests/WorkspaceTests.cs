using NightSignal.Core.Builds;

namespace NightSignal.BuildsTests;

/// <summary>Addendum 02 §9 loadouts, references and safe restoration (E01–E05 at the engine-free data level).</summary>
public sealed class WorkspaceTests
{
    const string Car = "V05";
    static readonly DateTime T = TestData.T0;

    static readonly string[] Owned =
    {
        "TYR-T1-STREET", "TYR-T2-SPORT", "TYR-T2-DRIFT", "BRK-T1-PADS", "BRK-T2-KIT", "GBX-T1-FINAL", "SUS-T2-SPORT",
        "DIF-T1-RWD-CLUTCH", "ENG-T1-INTAKE", "WGT-T1-STRIP", "UTL-SHW-5",
    };

    sealed class Fixture
    {
        public PartInventory Inventory;
        public BuildContext Ctx;
        public CarBuildWorkspace Ws;
        public long Wallet = 250_000;
    }

    static Fixture New(string instance = "ci_a", int shopAct = 2)
    {
        var inv = TestData.Owning(instance, Owned);
        BuildContext ctx = TestData.Ctx(Car, inv, shopAct);
        CarBuildWorkspace ws = GarageOperations.NewWorkspace(instance, ctx, T);
        ws.AppliedLiveryHash = "livery-rev-7";
        ws.AppliedVisualPresetId = "vp-blue";
        return new Fixture { Inventory = inv, Ctx = ctx, Ws = ws };
    }

    static MechanicalSnapshot Wet => TestData.Build("TYR-T2-SPORT", "BRK-T2-KIT", "SUS-T2-SPORT").WithTune(TuningKeys.BrakeBias, 600);
    static MechanicalSnapshot Short => TestData.Build("TYR-T1-STREET", "GBX-T1-FINAL", "DIF-T1-RWD-CLUTCH").WithTune(TuningKeys.FinalDrive, 1080);

    static OperationResult Edit(Fixture f, MechanicalSnapshot b) => Ok(GarageOperations.EditDraft(f.Ws, f.Ws.Revision, b, f.Ctx, T));

    static OperationResult Ok(OperationResult r)
    {
        Assert.True(r.Accepted, r.ToString() + " " + string.Join("; ", r.Repairs));
        return r;
    }

    static OperationResult Apply(Fixture f) => GarageOperations.Apply(f.Ws, f.Ws.Revision, f.Ctx, null, T);

    // ------------------------------------------------------------------ E01 capacity and independence

    [Fact]
    public void TwoInstancesOfOneModel_HaveEightIndependentLoadoutsEach_AndFiveVisualPresets()
    {
        Fixture a = New("ci_a"), b = New("ci_b");
        for (int i = 1; i <= 8; i++)
        {
            Edit(a, i % 2 == 0 ? Wet : Short);
            Ok(GarageOperations.SaveAs(a.Ws, a.Ws.Revision, $"A setup {i}", "", a.Ctx, T));
        }
        Edit(a, Wet);
        OperationResult ninth = GarageOperations.SaveAs(a.Ws, a.Ws.Revision, "A setup 9", "", a.Ctx, T);
        Assert.Equal(OpStatus.CapacityFull, ninth.Status);
        Assert.Equal(8, a.Ws.Loadouts.Count);
        Assert.Empty(b.Ws.Loadouts); // same model, different instance: nothing shared
        Ok(GarageOperations.SaveAs(b.Ws, b.Ws.Revision, "A setup 1", "", b.Ctx, T)); // same name allowed on the other car
        Assert.All(a.Ws.Loadouts, l => Assert.Equal("ci_a", l.CarInstanceId));
        Assert.All(a.Ws.Loadouts, l => Assert.Equal(Car, l.CarModelId));
        Assert.Equal(8, a.Ws.Loadouts.Select(l => l.LoadoutId).Distinct().Count());

        for (int i = 1; i <= 5; i++)
            Ok(GarageOperations.SaveVisualPreset(a.Ws, a.Ws.Revision, $"Look {i}", "night-signal/livery@1", "{\"paint\":" + i + "}", T));
        Assert.Equal(OpStatus.CapacityFull, GarageOperations.SaveVisualPreset(a.Ws, a.Ws.Revision, "Look 6", "x", "{}", T).Status);
        Assert.Empty(b.Ws.VisualPresets);
        Assert.True(CarBuildWorkspace.MinLoadoutSlots >= 8 && CarBuildWorkspace.MinVisualPresetSlots >= 5);
    }

    [Fact]
    public void MechanicalOperations_NeverTouchVisualPresetsOrLivery()
    {
        Fixture f = New();
        Ok(GarageOperations.SaveVisualPreset(f.Ws, f.Ws.Revision, "Blue", "night-signal/livery@1", "{\"paint\":\"blue\"}", T));
        string visuals = Newtonsoft.Json.JsonConvert.SerializeObject(f.Ws.VisualPresets);
        Edit(f, Wet);
        Ok(GarageOperations.SaveAs(f.Ws, f.Ws.Revision, "Wet Grip", "", f.Ctx, T));
        Ok(Apply(f));
        Ok(GarageOperations.LoadIntoDraft(f.Ws, f.Ws.Revision, DraftSource.Reference(BuildReferenceKind.BeforeLastApply), null, f.Ctx, T));
        Ok(Apply(f));
        Assert.Equal(visuals, Newtonsoft.Json.JsonConvert.SerializeObject(f.Ws.VisualPresets));
        Assert.Equal("livery-rev-7", f.Ws.AppliedLiveryHash);
        Assert.Equal("vp-blue", f.Ws.AppliedVisualPresetId);
    }

    // ------------------------------------------------------------------ E02 operations

    [Fact]
    public void SaveAs_Rename_Duplicate_Note_Pin()
    {
        Fixture f = New();
        Edit(f, Wet);
        OperationResult saved = Ok(GarageOperations.SaveAs(f.Ws, f.Ws.Revision, "  Wet Grip  ", "for S09 rain", f.Ctx, T));
        MechanicalLoadout l = f.Ws.Loadout(saved.LoadoutId);
        Assert.Equal("Wet Grip", l.Name);
        Assert.Equal("for S09 rain", l.Note);
        Assert.True(l.Build.ContentEquals(Wet));
        Assert.Equal(TestData.Pi(Car, Wet), l.DerivedPi);
        Assert.Equal(BuildResolver.HandlingModelVersion, l.HandlingModelVersion);
        Assert.False(l.NeedsParts);

        Assert.Equal(OpStatus.DuplicateName, GarageOperations.SaveAs(f.Ws, f.Ws.Revision, "wet grip", "", f.Ctx, T).Status);
        Assert.Equal(OpStatus.InvalidName, GarageOperations.SaveAs(f.Ws, f.Ws.Revision, "   ", "", f.Ctx, T).Status);
        Assert.Equal(OpStatus.InvalidName, GarageOperations.SaveAs(f.Ws, f.Ws.Revision, "<b>x</b>", "", f.Ctx, T).Status);
        Assert.Equal(OpStatus.InvalidName, GarageOperations.SaveAs(f.Ws, f.Ws.Revision, new string('x', 33), "", f.Ctx, T).Status);

        Ok(GarageOperations.Rename(f.Ws, f.Ws.Revision, l.LoadoutId, "Boss Try", T));
        Assert.Equal("Boss Try", f.Ws.Loadout(l.LoadoutId).Name);
        Ok(GarageOperations.EditNote(f.Ws, f.Ws.Revision, l.LoadoutId, "S14 lieutenant", T));

        var ownedBefore = f.Inventory.OwnedBy("ci_a").ToList();
        OperationResult dup = Ok(GarageOperations.Duplicate(f.Ws, f.Ws.Revision, l.LoadoutId, "Boss Try copy", T));
        Assert.NotEqual(l.LoadoutId, dup.LoadoutId);
        Assert.True(f.Ws.Loadout(dup.LoadoutId).Build.ContentEquals(l.Build));
        Assert.Equal(ownedBefore, f.Inventory.OwnedBy("ci_a").ToList()); // a copy of a plan duplicates no parts

        foreach (string name in new[] { "P1", "P2" })
        {
            Edit(f, Short.WithTune(TuningKeys.FinalDrive, name == "P1" ? 1060 : 1040));
            Ok(GarageOperations.SaveAs(f.Ws, f.Ws.Revision, name, "", f.Ctx, T));
        }
        var ids = f.Ws.Loadouts.Select(x => x.LoadoutId).ToList();
        for (int i = 0; i < 3; i++) Ok(GarageOperations.SetPinned(f.Ws, f.Ws.Revision, ids[i], true));
        Assert.Equal(OpStatus.PinLimit, GarageOperations.SetPinned(f.Ws, f.Ws.Revision, ids[3], true).Status);
        Ok(GarageOperations.SetPinned(f.Ws, f.Ws.Revision, ids[0], false));
        Ok(GarageOperations.SetPinned(f.Ws, f.Ws.Revision, ids[3], true));
        Assert.Equal(3, f.Ws.Loadouts.Count(x => x.Pinned));
    }

    [Fact]
    public void Overwrite_And_Delete_RequireConfirmation_AndTokensExpireWithTheRevision()
    {
        Fixture f = New();
        Edit(f, Wet);
        string id = Ok(GarageOperations.SaveAs(f.Ws, f.Ws.Revision, "Wet Grip", "", f.Ctx, T)).LoadoutId;
        Edit(f, Short);
        long rev = f.Ws.Revision;
        OperationResult ask = GarageOperations.Overwrite(f.Ws, rev, id, null, f.Ctx, T);
        Assert.Equal(OpStatus.ConfirmationRequired, ask.Status);
        Assert.Equal(rev, f.Ws.Revision);
        Assert.True(f.Ws.Loadout(id).Build.ContentEquals(Wet)); // nothing changed yet
        Assert.NotNull(ask.Comparison);
        Assert.Equal(OpStatus.ConfirmationRequired, GarageOperations.Overwrite(f.Ws, rev, id, "cf-forged", f.Ctx, T).Status);
        Ok(GarageOperations.Overwrite(f.Ws, rev, id, ask.ConfirmationToken, f.Ctx, T));
        Assert.True(f.Ws.Loadout(id).Build.ContentEquals(Short));
        Assert.Equal("Wet Grip", f.Ws.Loadout(id).Name);

        OperationResult askDel = GarageOperations.Delete(f.Ws, f.Ws.Revision, id, ask.ConfirmationToken); // old token: revision moved on
        Assert.Equal(OpStatus.ConfirmationRequired, askDel.Status);
        Assert.NotNull(f.Ws.Loadout(id));
        long appliedRev = f.Ws.Applied.Revision;
        Ok(GarageOperations.Delete(f.Ws, f.Ws.Revision, id, askDel.ConfirmationToken));
        Assert.Null(f.Ws.Loadout(id));
        Assert.Equal(appliedRev, f.Ws.Applied.Revision);
        Assert.True(f.Inventory.Owns("ci_a", "TYR-T2-SPORT"));
    }

    [Fact]
    public void Compare_GivesACompactDeltaListAndPiClassChange()
    {
        Fixture f = New();
        BuildComparison c = GarageOperations.Compare(MechanicalSnapshot.Stock(), Wet, "ci_a", f.Ctx);
        Assert.Equal(new[] { "tyres", "suspension", "brakes" }, c.Parts.Select(p => p.Slot));
        Assert.All(c.Parts, p => Assert.Equal("", p.From));
        Assert.Contains(c.Tuning, t => t.Key == TuningKeys.BrakeBias && t.From == null && t.To == 600);
        Assert.Contains(c.Tuning, t => t.Key == TuningKeys.SpringFront && t.To == 1150); // part default shown explicitly
        Assert.Equal(380, c.PiFrom);
        Assert.True(c.PiTo > 380);
        Assert.Contains(c.Parameters, p => p.Param == SimParam.TyreGrip && p.Change > 0);
        Assert.Contains(c.Lines, l => l.StartsWith("tyres: stock → Sport compound tyres"));
        BuildComparison same = GarageOperations.Compare(Wet, Wet.Clone(), "ci_a", f.Ctx);
        Assert.True(same.Identical);
        Assert.False(same.ClassChanged);
        // Class change is flagged (V05 is C; semi-slicks + sport engine cross into B).
        var inv = TestData.Owning("ci_a");
        BuildComparison cls = GarageOperations.Compare(MechanicalSnapshot.Stock(), TestData.Build("TYR-T3-SEMISLICK", "ENG-T2-STREET"), "ci_a", TestData.Ctx(Car, inv));
        Assert.True(cls.ClassChanged);
    }

    // ------------------------------------------------------------------ E03 atomic apply and exact repairs

    [Fact]
    public void Apply_IsAtomicWholeBuild_ReusingOwnedPartsIsFree_AndSetsBeforeLastApplyOnlyOnSuccess()
    {
        Fixture f = New();
        AppliedVehicleBuild original = f.Ws.Applied.Clone();
        Edit(f, Wet);
        OperationResult r = Ok(Apply(f));
        Assert.Equal(original.Revision + 1, f.Ws.Applied.Revision);
        Assert.True(f.Ws.Applied.Build.ContentEquals(Wet));
        Assert.True(r.PerformanceChanged);
        Assert.Equal("apply:draft", f.Ws.Applied.Source);
        BuildReference bla = f.Ws.Reference(BuildReferenceKind.BeforeLastApply);
        Assert.True(bla.Build.ContentEquals(original.Build));
        Assert.Equal(original.Revision, bla.SourceAppliedRevision);
        Assert.Equal(250_000, f.Wallet); // apply never touches money

        // A failing apply (one unowned part) changes NOTHING: no partial build, no BeforeLastApply update.
        long rev = f.Ws.Revision;
        Edit(f, Wet.With(PartSlot.Engine, "ENG-T2-STREET"));
        rev = f.Ws.Revision;
        OperationResult fail = Apply(f);
        Assert.Equal(OpStatus.NeedsRepair, fail.Status);
        RepairItem missing = Assert.Single(fail.Repairs);
        Assert.Equal(RepairKind.NotOwned, missing.Kind);
        Assert.Equal("ENG-T2-STREET", missing.PartId);
        Assert.Equal(48_000, missing.Price);
        Assert.Equal(rev, f.Ws.Revision);
        Assert.True(f.Ws.Applied.Build.ContentEquals(Wet));
        Assert.True(f.Ws.Reference(BuildReferenceKind.BeforeLastApply).Build.ContentEquals(original.Build));
        Assert.False(f.Inventory.Owns("ci_a", "ENG-T2-STREET")); // never bought

        // Re-applying the already-applied build is a no-op (no revision, no reference churn).
        Edit(f, Wet);
        long appliedRev = f.Ws.Applied.Revision;
        OperationResult same = Ok(Apply(f));
        Assert.Equal(appliedRev, f.Ws.Applied.Revision);
        Assert.False(same.PerformanceChanged);

        // Quick selector applies a saved owned loadout whole, leaving the draft alone.
        Edit(f, Short);
        string id = Ok(GarageOperations.SaveAs(f.Ws, f.Ws.Revision, "Short Gears", "", f.Ctx, T)).LoadoutId;
        Edit(f, Wet.WithTune(TuningKeys.BrakeBias, 640));
        Ok(GarageOperations.ApplyLoadout(f.Ws, f.Ws.Revision, id, f.Ctx, null, T));
        Assert.True(f.Ws.Applied.Build.ContentEquals(Short));
        Assert.True(f.Ws.Draft.Build.ContentEquals(Wet.WithTune(TuningKeys.BrakeBias, 640)));
    }

    [Fact]
    public void RepairList_IsExact_ForMissingUnownedLockedRemovedIncompatibleAndBadTune()
    {
        Fixture f = New(shopAct: 2);
        var b = new MechanicalSnapshot();
        b.Parts["tyres"] = "TYR-T3-SEMISLICK";        // act 3: locked for an act-2 player, not owned
        b.Parts["engine"] = "ENG-T2-STREET";          // not owned, in the shop
        b.Parts["differential"] = "DIF-T2-AWD-CENTRE"; // AWD only: incompatible with the RWD V05
        b.Parts["aero"] = "AER-T9-IMAGINARY";         // removed from the catalogue
        b.Parts["brakes"] = "SUS-T2-SPORT";           // wrong slot
        b.Parts["gearbox"] = "GBX-T1-FINAL";          // owned
        b.Parts["hover-pods"] = "X";                   // unknown slot
        b.Tuning.Values[TuningKeys.FinalDrive] = 1300;  // out of range
        BuildEvaluation ev = BuildEvaluator.Evaluate(b, "ci_a", f.Ctx);
        Assert.False(ev.Resolved);
        Assert.False(ev.CanApply);
        var kinds = ev.Repairs.Select(r => (r.Kind, r.PartId)).OrderBy(x => x.Kind).ToList();
        Assert.Equal(new[]
        {
            (RepairKind.UnknownSlot, "X"), (RepairKind.RemovedPart, "AER-T9-IMAGINARY"), (RepairKind.WrongSlot, "SUS-T2-SPORT"),
            (RepairKind.Incompatible, "DIF-T2-AWD-CENTRE"), (RepairKind.Locked, "TYR-T3-SEMISLICK"), (RepairKind.NotOwned, "ENG-T2-STREET"),
            (RepairKind.TuningInvalid, ""),
        }, kinds);
        Assert.Contains("AWD", ev.Repairs.Single(r => r.Kind == RepairKind.Incompatible).Detail);
        Assert.Contains("Act 3", ev.Repairs.Single(r => r.Kind == RepairKind.Locked).Detail);

        // Loading it as a draft keeps every id (no silent stock substitution), and Apply refuses with the same list.
        Ok(GarageOperations.EditDraft(f.Ws, f.Ws.Revision, b, f.Ctx, T));
        Assert.True(f.Ws.Draft.Build.ContentEquals(b));
        OperationResult r = Apply(f);
        Assert.Equal(OpStatus.NeedsRepair, r.Status);
        Assert.Equal(ev.Repairs.Count, r.Repairs.Count);
        Assert.Equal(1, f.Ws.Applied.Revision);
    }

    [Fact]
    public void PreviewParts_CanBeTestedAndPlanned_ButNeverApplied_EvenIfTheClientDropsTheFlag()
    {
        Fixture f = New(shopAct: 2);
        MechanicalSnapshot candidate = Wet.With(PartSlot.Engine, "ENG-T2-STREET").With(PartSlot.Differential, "DIF-T2-RWD-ADJ");
        BuildEvaluation test = GarageOperations.PreviewForTest(f.Ws, candidate, f.Ctx);
        Assert.True(test.CanPreview);
        Assert.False(test.CanApply);
        Assert.Equal(new[] { "ENG-T2-STREET", "DIF-T2-RWD-ADJ" }.OrderBy(x => x), test.PreviewPartIds.OrderBy(x => x));
        Assert.Equal(48_000 + 42_000, test.MissingParts.Sum(m => m.Price));
        // A locked part cannot even be previewed.
        Assert.False(GarageOperations.PreviewForTest(f.Ws, candidate.With(PartSlot.Tyres, "TYR-T3-SEMISLICK"), f.Ctx).CanPreview);

        // Planning loadout with "needs parts" status, no ownership granted.
        Ok(GarageOperations.EditDraft(f.Ws, f.Ws.Revision, candidate, f.Ctx, T));
        string plan = Ok(GarageOperations.SaveAs(f.Ws, f.Ws.Revision, "Next Step", "", f.Ctx, T)).LoadoutId;
        Assert.True(f.Ws.Loadout(plan).NeedsParts);
        Assert.False(f.Inventory.Owns("ci_a", "ENG-T2-STREET"));

        f.Ws.Draft.PreviewPartIds.Clear(); // a tampering client
        Assert.Equal(OpStatus.NeedsRepair, Apply(f).Status);
        Assert.Equal(OpStatus.NeedsRepair, GarageOperations.ApplyLoadout(f.Ws, f.Ws.Revision, plan, f.Ctx, null, T).Status);
        Assert.Equal(1, f.Ws.Applied.Revision);
    }

    [Fact]
    public void EventCapAndBuildLock_AreEnforcedAtApply()
    {
        Fixture f = New();
        Edit(f, Wet);
        int pi = TestData.Pi(Car, Wet);
        OperationResult over = GarageOperations.Apply(f.Ws, f.Ws.Revision, f.Ctx, new EventConstraints { MaxPi = pi - 1, Label = "S04" }, T);
        Assert.Equal(OpStatus.NeedsRepair, over.Status);
        Assert.Equal(RepairKind.OverCap, Assert.Single(over.Repairs).Kind);
        OperationResult locked = GarageOperations.Apply(f.Ws, f.Ws.Revision, f.Ctx, new EventConstraints { BuildLocked = true, Label = "Cup leg 2" }, T);
        Assert.Equal(OpStatus.BuildLocked, locked.Status);
        Assert.Equal(1, f.Ws.Applied.Revision);
        Ok(GarageOperations.Apply(f.Ws, f.Ws.Revision, f.Ctx, new EventConstraints { MaxPi = pi, Label = "S08" }, T));
    }

    // ------------------------------------------------------------------ E04 protected references

    [Fact]
    public void BeforeWorkshop_SurvivesExperimentsTestsAndReconnects_AndRestoresThroughOneApply()
    {
        Fixture f = New();
        Edit(f, Short);
        Ok(Apply(f)); // the car arrives at the Garage with the "Short" setup
        MechanicalSnapshot entry = f.Ws.Applied.Build.Clone();
        Ok(GarageOperations.BeginWorkshopSession(f.Ws, f.Ctx, T));
        Assert.True(f.Ws.Reference(BuildReferenceKind.BeforeWorkshop).Build.ContentEquals(entry));

        // Several experiments, applied and tested.
        Edit(f, Wet);
        Ok(Apply(f));
        long rev = f.Ws.Revision;
        GarageOperations.PreviewForTest(f.Ws, Wet.With(PartSlot.Engine, "ENG-T2-STREET"), f.Ctx); // Test Yard preview
        Assert.Equal(rev, f.Ws.Revision);
        Edit(f, Wet.WithTune(TuningKeys.BrakeBias, 680));
        Ok(Apply(f));
        OperationResult reconnect = GarageOperations.BeginWorkshopSession(f.Ws, f.Ctx, T.AddMinutes(5)); // reconnect/menu hop
        Assert.Equal(OpStatus.Ok, reconnect.Status);
        Assert.True(f.Ws.Reference(BuildReferenceKind.BeforeWorkshop).Build.ContentEquals(entry));

        // Restore: preview via draft (shows the delta), then ONE deliberate apply. No refund, no livery change.
        var ownedBefore = f.Inventory.OwnedBy("ci_a").ToList();
        OperationResult load = Ok(GarageOperations.LoadIntoDraft(f.Ws, f.Ws.Revision, DraftSource.Reference(BuildReferenceKind.BeforeWorkshop), null, f.Ctx, T));
        Assert.NotEmpty(load.Comparison.Parts);
        Assert.Empty(load.Repairs);
        Ok(Apply(f));
        Assert.True(f.Ws.Applied.Build.ContentEquals(entry)); // actual components and tuning, not a label
        Assert.Equal(1080, f.Ws.Applied.Build.Tuning.Values[TuningKeys.FinalDrive]);
        Assert.Equal("restore:before-workshop", f.Ws.Applied.Source);
        Assert.Equal(ownedBefore, f.Inventory.OwnedBy("ci_a").ToList());
        Assert.Equal(250_000, f.Wallet);
        Assert.Equal("livery-rev-7", f.Ws.AppliedLiveryHash);

        // Explicit baseline acceptance and session end are the only ways it moves.
        Edit(f, Wet);
        Ok(Apply(f));
        Ok(GarageOperations.AcceptNewWorkshopBaseline(f.Ws, f.Ws.Revision, f.Ctx, T));
        Assert.True(f.Ws.Reference(BuildReferenceKind.BeforeWorkshop).Build.ContentEquals(Wet));
        Ok(GarageOperations.EndWorkshopSession(f.Ws, f.Ws.Revision));
        Assert.False(f.Ws.Workshop.Open);
    }

    [Fact]
    public void BeforeLastApply_RestoresTheStateBeforeTheAcceptedChange()
    {
        Fixture f = New();
        Edit(f, Short);
        Ok(Apply(f));
        Edit(f, Wet);
        Ok(Apply(f));
        Assert.True(f.Ws.Reference(BuildReferenceKind.BeforeLastApply).Build.ContentEquals(Short));
        Ok(GarageOperations.LoadIntoDraft(f.Ws, f.Ws.Revision, DraftSource.Reference(BuildReferenceKind.BeforeLastApply), null, f.Ctx, T));
        Ok(Apply(f));
        Assert.True(f.Ws.Applied.Build.ContentEquals(Short));
        Assert.True(f.Ws.Reference(BuildReferenceKind.BeforeLastApply).Build.ContentEquals(Wet)); // swapped, so it can be undone again
    }

    [Fact]
    public void LastRaceBuild_IsRecordedOnlyWhenAuthorizedFullSizeDrivingBegins()
    {
        Fixture f = New();
        Edit(f, Short);
        Ok(Apply(f));
        AppliedVehicleBuild frozen = f.Ws.Applied.Clone(); // what the server froze at allocation

        foreach (DrivingSessionKind k in new[] { DrivingSessionKind.TestYard, DrivingSessionKind.SlotCarToy, DrivingSessionKind.OtherToy, DrivingSessionKind.MenuPreview })
            Assert.Equal(OpStatus.Rejected, GarageOperations.RecordRaceBegan(f.Ws, k, frozen, "test", T).Status);
        Assert.Null(f.Ws.Reference(BuildReferenceKind.LastRaceBuild));

        Ok(GarageOperations.RecordRaceBegan(f.Ws, DrivingSessionKind.FullSizeEvent, frozen, "race-S08-001", T));
        BuildReference lrb = f.Ws.Reference(BuildReferenceKind.LastRaceBuild);
        Assert.True(lrb.Build.ContentEquals(Short));
        Assert.Equal("race-S08-001", lrb.Context);

        // A bad test afterwards: new experiment applied and tested. Last Race Build is untouched and restorable.
        Edit(f, Wet.With(PartSlot.Tyres, "TYR-T2-DRIFT"));
        Ok(Apply(f));
        GarageOperations.PreviewForTest(f.Ws, f.Ws.Applied.Build, f.Ctx);
        Assert.Equal(OpStatus.Rejected, GarageOperations.RecordRaceBegan(f.Ws, DrivingSessionKind.TestYard, f.Ws.Applied, "yard", T).Status);
        Assert.True(f.Ws.Reference(BuildReferenceKind.LastRaceBuild).Build.ContentEquals(Short));
        Ok(GarageOperations.LoadIntoDraft(f.Ws, f.Ws.Revision, DraftSource.Reference(BuildReferenceKind.LastRaceBuild), null, f.Ctx, T));
        Ok(Apply(f));
        Assert.True(f.Ws.Applied.Build.ContentEquals(Short));
        Assert.Equal(250_000, f.Wallet);
    }

    [Fact]
    public void LoadIntoDraft_WarnsBeforeReplacingADirtyDraft_AndNeverChangesTheAppliedBuild()
    {
        Fixture f = New();
        Edit(f, Short);
        string id = Ok(GarageOperations.SaveAs(f.Ws, f.Ws.Revision, "Short Gears", "", f.Ctx, T)).LoadoutId;
        Edit(f, Wet); // dirty draft (differs from applied stock)
        OperationResult ask = GarageOperations.LoadIntoDraft(f.Ws, f.Ws.Revision, DraftSource.Loadout(id), null, f.Ctx, T);
        Assert.Equal(OpStatus.ConfirmationRequired, ask.Status);
        Assert.True(f.Ws.Draft.Build.ContentEquals(Wet));
        Ok(GarageOperations.LoadIntoDraft(f.Ws, f.Ws.Revision, DraftSource.Loadout(id), ask.ConfirmationToken, f.Ctx, T));
        Assert.True(f.Ws.Draft.Build.ContentEquals(Short));
        Assert.True(f.Ws.Applied.Build.ContentEquals(MechanicalSnapshot.Stock()));
        Assert.Equal(1, f.Ws.Applied.Revision);
    }

    // ------------------------------------------------------------------ E05 concurrency

    [Fact]
    public void AStaleEditor_CannotOverwriteAnotherDevicesAcceptedSetup()
    {
        Fixture f = New();
        string json = BuildDocumentCodec.SerializeWorkspace(f.Ws);
        CarBuildWorkspace deviceA = BuildDocumentCodec.DeserializeWorkspace(json, TestData.Parts, T).Workspace;
        CarBuildWorkspace deviceB = BuildDocumentCodec.DeserializeWorkspace(json, TestData.Parts, T).Workspace;
        long seen = deviceA.Revision;

        // Server copy accepts device A's apply.
        Ok(GarageOperations.EditDraft(f.Ws, seen, Wet, f.Ctx, T));
        Ok(GarageOperations.Apply(f.Ws, f.Ws.Revision, f.Ctx, null, T));
        AppliedVehicleBuild accepted = f.Ws.Applied.Clone();

        // Device B, still at the old revision, tries to edit/apply/save/delete on the server copy.
        Assert.Equal(OpStatus.StaleRevision, GarageOperations.EditDraft(f.Ws, seen, Short, f.Ctx, T).Status);
        Assert.Equal(OpStatus.StaleRevision, GarageOperations.Apply(f.Ws, seen, f.Ctx, null, T).Status);
        Assert.Equal(OpStatus.StaleRevision, GarageOperations.SaveAs(f.Ws, seen, "B's idea", "", f.Ctx, T).Status);
        Assert.True(f.Ws.Applied.Build.ContentEquals(accepted.Build));
        Assert.Equal(accepted.Revision, f.Ws.Applied.Revision);
        Assert.Equal(deviceB.Revision, seen); // B must reload
    }
}
