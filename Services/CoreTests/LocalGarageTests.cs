using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using Newtonsoft.Json.Linq;

namespace NightSignal.CoreTests;

/// <summary>
/// The Local Garage (Addendum 02 §8–10, D204/D205) on a Local profile: per-instance part ownership, per-car workspaces with
/// ≥ 8 loadouts / ≥ 5 visual presets / protected references, Buy and Apply against the Local wallet, Last Race Build, and
/// the schema 1 → 2 migration. Uses the real content and parts catalogues.
/// </summary>
public sealed class LocalGarageTests
{
    static ContentCatalogue Cat => TestContent.Catalogue;
    static readonly Lazy<string> partsJson = new(() => File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Assets", "Content", "Data", "authored", "parts.json")));
    static readonly Lazy<PartsCatalogue> parts = new(() => PartsCatalogue.Load(partsJson.Value));
    static PartsCatalogue Parts => parts.Value;
    static readonly DateTime T = TestContent.T0.AddHours(3);

    static readonly string[] Pair = { "TYR-T1-STREET", "BRK-T1-PADS" }; // 9,000 + 7,000
    static MechanicalSnapshot Candidate => Build(Pair);

    // ------------------------------------------------------------------ fixtures

    /// <summary>Starter V01 plus a second, independent V01 instance; the wallet is then set for the test.</summary>
    static LocalProfile TwoV01(long wallet = 200_000)
    {
        LocalProfile p = ProfileJson.Clone(LocalProgressionTests.NewProfile());
        p.WalletBalance = 100_000; // test funding; a real profile earns it from events
        LocalProgressionResult bought = LocalProgression.PurchaseCar(p, Cat, "V01", "buy-second-v01", T, new SequenceIds(100));
        Assert.True(bought.Status == LocalOperationStatus.Applied, bought.Reason);
        p = ProfileJson.Clone(bought.Profile);
        p.WalletBalance = wallet;
        Assert.Equal(2, p.Cars.Count);
        Assert.Equal(p.Cars[0].ModelId, p.Cars[1].ModelId);
        return p;
    }

    static string A(LocalProfile p) => p.Cars[0].InstanceId;
    static string B(LocalProfile p) => p.Cars[1].InstanceId;

    static MechanicalSnapshot Build(params string[] ids)
    {
        var s = new MechanicalSnapshot();
        foreach (string id in ids)
        {
            PartDef part = Parts.Part(id);
            if (part.SlotValue == PartSlot.Utility) s.UtilityPartId = id;
            else s.Parts[PartSlots.Id(part.SlotValue)] = id;
        }
        return s;
    }

    static LocalWorkspaceLoad Load(LocalProfile p, string instanceId)
    {
        LocalWorkspaceLoad load = LocalGarage.LoadWorkspace(p, Cat, Parts, instanceId, T);
        Assert.True(load.Ok, load.Reason);
        return load;
    }

    static LocalProfile Save(LocalProfile p, CarBuildWorkspace ws)
    {
        LocalProgressionResult r = LocalGarage.SaveWorkspace(p, ws.Car.InstanceId, ws);
        Assert.True(r.Status == LocalOperationStatus.Applied, r.Reason);
        Assert.Equal(r.BalanceBefore, r.BalanceAfter);
        return r.Profile;
    }

    static OperationResult Ok(OperationResult r)
    {
        Assert.True(r.Accepted, r + " " + string.Join("; ", r.Repairs));
        return r;
    }

    static PurchaseAndApplyQuote Quote(LocalProfile p, string instanceId, MechanicalSnapshot build)
    {
        QuoteResult q = LocalGarage.Quote(p, Cat, Parts, instanceId, build, T);
        Assert.True(q.Status == QuoteStatus.Ok, q.Message);
        return q.Quote;
    }

    static LocalProgressionResult Buy(LocalProfile p, string instanceId, PurchaseAndApplyQuote q, string op, bool confirmed = true,
        PartsCatalogue catalogue = null, DateTime? utc = null) =>
        LocalGarage.BuyAndApply(p, Cat, catalogue ?? Parts, instanceId, q, confirmed, op, utc ?? T);

    /// <summary>Saves through a fresh repository and loads it back (the real persisted contract).</summary>
    static LocalProfile RoundTrip(LocalProfile p)
    {
        var repo = new ProfileRepository(new InMemoryProfileStorage());
        ProfileSaveResult created = repo.Create(ProfileJson.Clone(p));
        Assert.True(created.Ok, created.Message);
        ProfileLoadResult loaded = repo.Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Loaded, loaded.Status);
        return loaded.Profile;
    }

    static void AssertUnchanged(string before, LocalProgressionResult r)
    {
        Assert.Equal(before, ProfileJson.Serialize(r.Profile));
        Assert.Equal(r.BalanceBefore, r.BalanceAfter);
    }

    /// <summary>Garage operations never touch RP, clears, challenges, records, music, courses, cosmetics, tutorial or toys.</summary>
    static void AssertNoProgressionChange(LocalProfile before, LocalProfile after)
    {
        Assert.Equal(before.ComputeRankPoints(), after.ComputeRankPoints());
        Assert.Equal(before.ComputeRank().Name, after.ComputeRank().Name);
        foreach (Func<LocalProfile, object> section in new Func<LocalProfile, object>[]
                 {
                     x => x.Campaign, x => x.Challenges, x => x.Records, x => x.Music, x => x.Courses, x => x.Cosmetics, x => x.Tutorial, x => x.Toys,
                 })
            Assert.Equal(ProfileJson.Serialize(section(before)), ProfileJson.Serialize(section(after)));
    }

    // ------------------------------------------------------------------ ownership, context, shop act

    [Fact]
    public void NewCar_LoadsAStockWorkspace_ThatOnlyASaveStores()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        string a = A(p);
        LocalWorkspaceLoad load = Load(p, a);
        Assert.True(load.Created);
        Assert.Equal(0, load.StoredRevision);
        Assert.Equal(1, load.Workspace.Revision);
        Assert.Equal(a, load.Workspace.Car.InstanceId);
        Assert.Equal("V01", load.Workspace.Car.ModelId);
        Assert.True(load.Workspace.Applied.Build.ContentEquals(MechanicalSnapshot.Stock()));
        Assert.Equal(Cat.Car("V01").BasePI, load.Workspace.Applied.Pi);
        Assert.NotEmpty(load.Workspace.Applied.BuildHash);
        Assert.Empty(load.Workspace.Loadouts); // no invented presets
        Assert.True(p.Cars[0].Workspace.IsEmpty); // a read never writes

        LocalProgressionResult saved = LocalGarage.SaveWorkspace(p, a, load.Workspace);
        Assert.Equal(LocalOperationStatus.Applied, saved.Status);
        Assert.Contains(saved.Changes, c => c.Kind == ProgressionChangeKind.WorkspaceSaved);
        Assert.True(p.Cars[0].Workspace.IsEmpty); // the input profile is never modified
        Assert.False(saved.Profile.Cars[0].Workspace.IsEmpty);

        LocalWorkspaceLoad again = Load(saved.Profile, a);
        Assert.False(again.Created);
        Assert.False(again.Migrated);
        Assert.Equal(1, again.StoredRevision);
        Assert.Equal(BuildDocumentCodec.SerializeWorkspace(load.Workspace), BuildDocumentCodec.SerializeWorkspace(again.Workspace));
        LocalProgressionResult replay = LocalGarage.SaveWorkspace(saved.Profile, a, again.Workspace);
        Assert.Equal(LocalOperationStatus.AlreadyApplied, replay.Status);
        Assert.Same(saved.Profile, replay.Profile);
    }

    [Fact]
    public void ShopAct_FollowsTheNormalFrontier_AndTheContextUsesLiveOwnership()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        Assert.Equal(1, LocalGarage.ShopAct(p, Cat));
        BuildContext ctx = LocalGarage.Context(p, Cat, Parts, A(p));
        Assert.Equal(1, ctx.ShopAct);
        Assert.Equal("V01", ctx.Car.Id);
        Assert.IsType<LocalPartOwnership>(ctx.Ownership);
        Assert.Throws<KeyNotFoundException>(() => LocalGarage.Context(p, Cat, Parts, "ci_not_mine"));

        // An act-2 part is Locked (not even quotable) until the Normal frontier reaches Act II.
        QuoteResult locked = LocalGarage.Quote(p, Cat, Parts, A(p), Build("TYR-T2-SPORT"), T);
        Assert.Equal(QuoteStatus.NotPurchasable, locked.Status);
        Assert.Contains(locked.Repairs, r => r.Kind == RepairKind.Locked);

        int lastActOne = Cat.Stages.Where(s => s.Act == 1).Max(s => s.Number);
        LocalProfile further = LocalProgressionTests.ClearNormalThrough(p, lastActOne);
        Assert.Equal(2, LocalGarage.ShopAct(further, Cat));
        Assert.Equal(QuoteStatus.Ok, LocalGarage.Quote(further, Cat, Parts, A(further), Build("TYR-T2-SPORT"), T).Status);
    }

    // ------------------------------------------------------------------ loadouts, presets, references

    [Fact]
    public void EightLoadoutsAndFivePresetsPerInstance_PersistThroughSaveAndLoad_WithoutSharing()
    {
        LocalProfile p = TwoV01();
        string a = A(p), b = B(p);
        BuildContext ctx = LocalGarage.Context(p, Cat, Parts, a);
        CarBuildWorkspace ws = Load(p, a).Workspace;
        string[] names = { "Wet Grip", "Short Gears", "Drift Setup", "Boss Try", "Touge Night", "Rain Brakes", "Low Drag", "Street A" };
        string[][] builds =
        {
            new[] { "TYR-T1-STREET" }, new[] { "GBX-T1-SHORTFD" }, new[] { "TYR-T1-TOURING" }, new[] { "SUS-T1-SPRINGS" },
            new[] { "ENG-T1-INTAKE" }, new[] { "BRK-T1-PADS" }, new[] { "AER-T1-LIP" }, Pair,
        };
        for (int i = 0; i < 8; i++)
        {
            Ok(GarageOperations.EditDraft(ws, ws.Revision, Build(builds[i]), ctx, T));
            Ok(GarageOperations.SaveAs(ws, ws.Revision, names[i], "note " + i, ctx, T));
        }
        Assert.Equal(OpStatus.CapacityFull, GarageOperations.SaveAs(ws, ws.Revision, "Ninth", "", ctx, T).Status);
        for (int i = 1; i <= 5; i++)
            Ok(GarageOperations.SaveVisualPreset(ws, ws.Revision, $"Look {i}", "night-signal/livery@1", "{\"paint\":" + i + "}", T));
        Assert.Equal(OpStatus.CapacityFull, GarageOperations.SaveVisualPreset(ws, ws.Revision, "Look 6", "x", "{}", T).Status);

        LocalProfile saved = Save(p, ws);
        LocalProfile back = RoundTrip(saved);
        Assert.Empty(back.Validate());
        LocalWorkspaceLoad load = Load(back, a);
        Assert.False(load.Migrated);
        CarBuildWorkspace wsBack = load.Workspace;
        Assert.Equal(BuildDocumentCodec.SerializeWorkspace(ws), BuildDocumentCodec.SerializeWorkspace(wsBack));
        Assert.Equal(names, wsBack.Loadouts.Select(l => l.Name));
        Assert.Equal(ws.Loadouts.Select(l => l.LoadoutId), wsBack.Loadouts.Select(l => l.LoadoutId));
        for (int i = 0; i < 8; i++) Assert.True(wsBack.Loadouts[i].Build.ContentEquals(Build(builds[i])), names[i]);
        Assert.All(wsBack.Loadouts, l => Assert.Equal(a, l.CarInstanceId));
        Assert.All(wsBack.Loadouts, l => Assert.True(l.NeedsParts)); // planning drafts: nothing was bought or granted
        Assert.Equal(new[] { "Look 1", "Look 2", "Look 3", "Look 4", "Look 5" }, wsBack.VisualPresets.Select(v => v.Name));
        Assert.Equal("{\"paint\":5}", wsBack.VisualPresets[4].PayloadJson);
        Assert.Equal(8, back.Cars[0].Workspace.MechanicalLoadouts.Count);
        Assert.Equal(5, back.Cars[0].Workspace.VisualPresets.Count);
        Assert.Empty(back.Cars[0].Parts);

        // The other instance of the same model is independent: empty library, same names allowed.
        LocalWorkspaceLoad other = Load(back, b);
        Assert.True(other.Created);
        Assert.Empty(other.Workspace.Loadouts);
        Assert.Empty(other.Workspace.VisualPresets);
        BuildContext ctxB = LocalGarage.Context(back, Cat, Parts, b);
        Ok(GarageOperations.SaveAs(other.Workspace, other.Workspace.Revision, "Wet Grip", "", ctxB, T));
        LocalProfile both = Save(back, other.Workspace);
        Assert.Equal(8, Load(both, a).Workspace.Loadouts.Count);
        Assert.Single(Load(both, b).Workspace.Loadouts);
        Assert.Equal(b, Load(both, b).Workspace.Loadouts[0].CarInstanceId);
    }

    [Fact]
    public void ProtectedReferences_SurviveASaveRoundTrip_AndRestoreKeepsOwnership()
    {
        LocalProfile p = TwoV01();
        string a = A(p);
        CarBuildWorkspace ws = Load(p, a).Workspace;
        Ok(GarageOperations.BeginWorkshopSession(ws, LocalGarage.Context(p, Cat, Parts, a), T));
        p = Save(p, ws);

        LocalProgressionResult bought = Buy(p, a, Quote(p, a, Candidate), "op-refs-buy");
        Assert.Equal(LocalOperationStatus.Applied, bought.Status);
        p = bought.Profile;
        AppliedVehicleBuild frozen = LocalGarage.FrozenRaceBuild(p, Cat, Parts, a, T);
        LocalProgressionResult raced = LocalGarage.RecordLocalRaceBuild(p, Cat, Parts, a, frozen, "le_refs_race", T);
        Assert.Equal(LocalOperationStatus.Applied, raced.Status);

        LocalProfile back = RoundTrip(raced.Profile);
        CarBuildWorkspace w = Load(back, a).Workspace;
        BuildReference beforeWorkshop = w.Reference(BuildReferenceKind.BeforeWorkshop);
        BuildReference beforeLastApply = w.Reference(BuildReferenceKind.BeforeLastApply);
        BuildReference lastRace = w.Reference(BuildReferenceKind.LastRaceBuild);
        Assert.True(beforeWorkshop.Build.ContentEquals(MechanicalSnapshot.Stock()));
        Assert.Equal(1, beforeWorkshop.SourceAppliedRevision);
        Assert.True(beforeLastApply.Build.ContentEquals(MechanicalSnapshot.Stock()));
        Assert.Equal(1, beforeLastApply.SourceAppliedRevision);
        Assert.True(lastRace.Build.ContentEquals(Candidate));
        Assert.Equal(2, lastRace.SourceAppliedRevision);
        Assert.Equal("le_refs_race", lastRace.Context);
        Assert.Equal(w.Applied.BuildHash, lastRace.BuildHash);
        Assert.True(w.Workshop.Open);
        Assert.Equal(T, w.Workshop.OpenedUtc);
        Assert.Equal(DateTimeKind.Utc, w.Workshop.OpenedUtc.Kind);

        // Restore Before Last Apply through preview + one Apply: no refund, parts stay owned, wallet unchanged.
        BuildContext ctx = LocalGarage.Context(back, Cat, Parts, a);
        Ok(GarageOperations.LoadIntoDraft(w, w.Revision, DraftSource.Reference(BuildReferenceKind.BeforeLastApply), null, ctx, T));
        Ok(GarageOperations.Apply(w, w.Revision, ctx, null, T));
        LocalProfile restored = Save(back, w);
        Assert.Equal(back.WalletBalance, restored.WalletBalance);
        Assert.Equal(Pair.OrderBy(x => x, StringComparer.Ordinal), new LocalPartOwnership(restored).OwnedBy(a));
        CarBuildWorkspace after = Load(RoundTrip(restored), a).Workspace;
        Assert.True(after.Applied.Build.ContentEquals(MechanicalSnapshot.Stock()));
        Assert.True(after.Reference(BuildReferenceKind.LastRaceBuild).Build.ContentEquals(Candidate)); // not replaced by the restore
        Assert.True(after.Reference(BuildReferenceKind.BeforeLastApply).Build.ContentEquals(Candidate));
    }

    // ------------------------------------------------------------------ Buy and Apply

    [Fact]
    public void BuyAndApply_DebitsExactlyOnce_GrantsToThatInstanceOnly_AndStoresTheAppliedBuild()
    {
        LocalProfile p = TwoV01(200_000);
        string a = A(p), b = B(p);
        string before = ProfileJson.Serialize(p);
        PurchaseAndApplyQuote q = Quote(p, a, Candidate);
        Assert.Equal(16_000, q.Total);
        Assert.Equal(Pair.OrderBy(x => x), q.Lines.Select(l => l.PartId).OrderBy(x => x));

        LocalProgressionResult r = Buy(p, a, q, "op-buy-a-1");
        Assert.True(r.Status == LocalOperationStatus.Applied, r.Reason);
        Assert.Equal(before, ProfileJson.Serialize(p)); // input never modified
        LocalProfile n = r.Profile;
        Assert.Empty(n.Validate());
        Assert.Equal(184_000, n.WalletBalance);
        Assert.Equal(200_000, r.BalanceBefore);
        Assert.Equal(184_000, r.BalanceAfter);
        Assert.Equal(SettlementOutcome.Settled, r.Garage.Settlement.Outcome);
        Assert.Equal(Pair.OrderBy(x => x), r.Garage.GrantedPartIds.OrderBy(x => x));

        // Owned by A only, with provenance; B (same model) owns nothing.
        Assert.Equal(Pair.OrderBy(x => x, StringComparer.Ordinal), new LocalPartOwnership(n).OwnedBy(a));
        Assert.Empty(new LocalPartOwnership(n).OwnedBy(b));
        Assert.False(LocalGarage.Ownership(n).Owns(b, "TYR-T1-STREET"));
        Assert.All(n.Cars[0].Parts, part =>
        {
            Assert.Equal(LocalGarage.PurchaseSource, part.Source);
            Assert.Equal(q.QuoteId, part.Reference);
            Assert.Equal(1, part.Quantity);
            Assert.Equal(T, part.AcquiredUtc);
        });
        Assert.Equal(9_000, n.Cars[0].Parts.Single(x => x.PartId == "TYR-T1-STREET").PricePaid);
        Assert.Empty(n.Cars[1].Parts);
        Assert.Empty(n.UnassignedParts);

        WalletEntry entry = n.WalletHistory.Last();
        Assert.Equal(LocalGarage.WalletKind, entry.Kind);
        Assert.Equal(q.QuoteId, entry.Reference);
        Assert.Equal(-16_000, entry.Amount);
        Assert.Equal(184_000, entry.BalanceAfter);
        Assert.True(n.HasApplied("op-buy-a-1"));
        Assert.Equal(-16_000, r.Of(ProgressionChangeKind.Debit).Single().Amount);
        Assert.Equal(2, r.Of(ProgressionChangeKind.PartGranted).Count());
        Assert.Single(r.Of(ProgressionChangeKind.BuildApplied));

        CarBuildWorkspace ws = Load(n, a).Workspace;
        Assert.True(ws.Applied.Build.ContentEquals(Candidate));
        Assert.Equal(2, ws.Applied.Revision);
        Assert.Equal("quote:" + q.QuoteId, ws.Applied.Source);
        Assert.True(ws.Reference(BuildReferenceKind.BeforeLastApply).Build.ContentEquals(MechanicalSnapshot.Stock()));
        Assert.True(Load(n, b).Created); // B's workspace untouched

        // The other instance still has to buy its own parts: nothing is shared.
        Assert.Equal(16_000, Quote(n, b, Candidate).Total);
        Assert.Equal(QuoteStatus.NothingToBuy, LocalGarage.Quote(n, Cat, Parts, a, Candidate, T).Status);
        AssertNoProgressionChange(p, n);
    }

    [Fact]
    public void BuyAndApply_IsIdempotent_ByOperationId_AndByQuoteId()
    {
        LocalProfile p = TwoV01(200_000);
        string a = A(p);
        PurchaseAndApplyQuote q = Quote(p, a, Candidate);
        LocalProfile once = Buy(p, a, q, "op-idem-1").Profile;

        LocalProgressionResult replay = Buy(once, a, q, "op-idem-1");
        Assert.Equal(LocalOperationStatus.AlreadyApplied, replay.Status);
        Assert.Same(once, replay.Profile);
        Assert.Equal(184_000, replay.Profile.WalletBalance);

        LocalProgressionResult sameQuote = Buy(once, a, q, "op-idem-2");
        Assert.Equal(LocalOperationStatus.AlreadyApplied, sameQuote.Status);
        Assert.Equal(SettlementOutcome.AlreadySettled, sameQuote.Garage.Settlement.Outcome);
        Assert.Same(once, sameQuote.Profile);
        Assert.Equal(2, sameQuote.Profile.Cars[0].Parts.Count);
        Assert.Single(sameQuote.Profile.WalletHistory, w => w.Kind == LocalGarage.WalletKind);

        Assert.True(new LocalQuoteLedger(once).TryGet(q.QuoteId, out SettlementRecord record));
        Assert.Equal(a, record.InstanceId);
        Assert.Equal(16_000, record.Debit);
        Assert.Equal(184_000, record.BalanceAfter);
        Assert.Equal(Pair.OrderBy(x => x), record.Grants.OrderBy(x => x));
        Assert.False(new LocalQuoteLedger(once).TryGet("q-unknown", out _));
    }

    [Fact]
    public void BuyAndApply_Rejections_LeaveTheProfileExactlyUnchanged()
    {
        // Insufficient funds: the starter wallet (12,000) cannot pay 16,000.
        LocalProfile starter = LocalProgressionTests.NewProfile();
        QuoteResult unaffordable = LocalGarage.Quote(starter, Cat, Parts, A(starter), Candidate, T);
        Assert.Equal(QuoteStatus.Ok, unaffordable.Status);
        Assert.False(unaffordable.AffordableNow);
        string starterJson = ProfileJson.Serialize(starter);
        LocalProgressionResult poor = Buy(starter, A(starter), unaffordable.Quote, "op-poor");
        Assert.Equal(LocalOperationStatus.Rejected, poor.Status);
        Assert.Equal(SettlementOutcome.RejectedInsufficientFunds, poor.Garage.Settlement.Outcome);
        AssertUnchanged(starterJson, poor);
        Assert.Empty(poor.Profile.Cars[0].Parts);
        Assert.True(poor.Profile.Cars[0].Workspace.IsEmpty);

        LocalProfile p = TwoV01(200_000);
        string a = A(p), b = B(p);
        string json = ProfileJson.Serialize(p);
        PurchaseAndApplyQuote q = Quote(p, a, Candidate);

        LocalProgressionResult unconfirmed = Buy(p, a, q, "op-unconfirmed", confirmed: false);
        Assert.Equal(SettlementOutcome.RejectedNotConfirmed, unconfirmed.Garage.Settlement.Outcome);
        AssertUnchanged(json, unconfirmed);

        LocalProgressionResult expired = Buy(p, a, q, "op-expired", utc: T + PurchaseQuotes.DefaultLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(SettlementOutcome.RejectedExpired, expired.Garage.Settlement.Outcome);
        AssertUnchanged(json, expired);

        // Price change: a catalogue with a new price revision refuses the old quote and shows current prices.
        JObject changed = JObject.Parse(partsJson.Value);
        changed["priceRevision"] = (int)changed["priceRevision"] + 1;
        changed["parts"].First(x => (string)x["id"] == "TYR-T1-STREET")["price"] = 9_500;
        PartsCatalogue repriced = PartsCatalogue.Load(changed.ToString());
        LocalProgressionResult priceChanged = Buy(p, a, q, "op-repriced", catalogue: repriced);
        Assert.Equal(SettlementOutcome.RejectedPriceChanged, priceChanged.Garage.Settlement.Outcome);
        Assert.Contains(priceChanged.Garage.Settlement.CurrentLines, l => l.PartId == "TYR-T1-STREET" && l.Price == 9_500);
        AssertUnchanged(json, priceChanged);

        LocalProgressionResult wrongCar = Buy(p, b, q, "op-wrong-car");
        Assert.Equal(LocalOperationStatus.Rejected, wrongCar.Status);
        AssertUnchanged(json, wrongCar);
        Assert.Equal(LocalOperationStatus.Rejected, Buy(p, a, q, "x").Status); // malformed operation id
        Assert.Equal(LocalOperationStatus.Rejected, Buy(p, a, null, "op-no-quote").Status);

        // A frozen (build-locked) event refuses the apply, so nothing is bought either.
        LocalProgressionResult locked = LocalGarage.BuyAndApply(p, Cat, Parts, a, q, true, "op-locked", T, new EventConstraints { BuildLocked = true, Label = "Cup leg 2" });
        Assert.Equal(SettlementOutcome.RejectedNotApplicable, locked.Garage.Settlement.Outcome);
        AssertUnchanged(json, locked);

        // A hand-edited quote that lists one part twice can never grant (or charge) it twice.
        var doubled = Newtonsoft.Json.JsonConvert.DeserializeObject<PurchaseAndApplyQuote>(Newtonsoft.Json.JsonConvert.SerializeObject(q));
        QuoteLine tyres = doubled.Lines.Single(l => l.PartId == "TYR-T1-STREET");
        doubled.Lines.Add(new QuoteLine { PartId = tyres.PartId, Slot = tyres.Slot, Name = tyres.Name, Tier = tyres.Tier, Price = tyres.Price });
        doubled.Total += tyres.Price;
        doubled.QuoteId = "q-doubled-by-hand";
        LocalProgressionResult twice = Buy(p, a, doubled, "op-doubled");
        Assert.Equal(LocalOperationStatus.Rejected, twice.Status);
        AssertUnchanged(json, twice);
        Assert.Empty(twice.Changes);

        // Stale quote: the applied build changed after the quote was issued.
        LocalProfile moved = Buy(p, a, Quote(p, a, Build("WGT-T1-STRIP")), "op-other-first").Profile;
        string movedJson = ProfileJson.Serialize(moved);
        LocalProgressionResult stale = Buy(moved, a, q, "op-stale");
        Assert.Equal(SettlementOutcome.RejectedStaleBuild, stale.Garage.Settlement.Outcome);
        AssertUnchanged(movedJson, stale);
        Assert.False(stale.Profile.HasApplied("op-stale"));
    }

    // ------------------------------------------------------------------ workspace saves

    [Fact]
    public void SaveWorkspace_RefusesStaleForeignOrOwnershipSmugglingCopies()
    {
        LocalProfile p = TwoV01();
        string a = A(p), b = B(p);
        BuildContext ctx = LocalGarage.Context(p, Cat, Parts, a);
        LocalWorkspaceLoad first = Load(p, a), second = Load(p, a);
        Ok(GarageOperations.SaveAs(first.Workspace, first.Workspace.Revision, "Wet Grip", "", ctx, T, fromApplied: true));
        LocalProfile saved = Save(p, first.Workspace);
        string json = ProfileJson.Serialize(saved);

        // A second editor that loaded before the save must not overwrite it.
        Ok(GarageOperations.SaveAs(second.Workspace, second.Workspace.Revision, "Drift Setup", "", ctx, T, fromApplied: true));
        AssertUnchanged(json, LocalGarage.SaveWorkspace(saved, a, second.Workspace));
        LocalProgressionResult expected = LocalGarage.SaveWorkspace(saved, a, second.Workspace, second.StoredRevision);
        Assert.Equal(LocalOperationStatus.Rejected, expected.Status);
        AssertUnchanged(json, expected);

        // Another instance's workspace, an older copy, a trimmed capacity or an unowned applied part are refused.
        CarBuildWorkspace current = Load(saved, a).Workspace;
        Assert.Equal(LocalOperationStatus.Rejected, LocalGarage.SaveWorkspace(saved, b, current).Status);
        CarBuildWorkspace trimmed = current.Clone();
        trimmed.LoadoutCapacity = 3;
        trimmed.Revision++;
        Assert.Equal(LocalOperationStatus.Rejected, LocalGarage.SaveWorkspace(saved, a, trimmed).Status);
        CarBuildWorkspace smuggled = current.Clone();
        smuggled.Applied.Build = Build("TYR-T1-STREET");
        smuggled.Applied.Revision++;
        smuggled.Revision++;
        LocalProgressionResult refused = LocalGarage.SaveWorkspace(saved, a, smuggled);
        Assert.Equal(LocalOperationStatus.Rejected, refused.Status);
        Assert.Contains("does not own", refused.Reason);
        AssertUnchanged(json, refused);
        Assert.Equal(LocalOperationStatus.Rejected, LocalGarage.SaveWorkspace(saved, "ci_not_mine", current).Status);

        // The current copy saves normally (a preview part may stay in the DRAFT: it never reaches the applied build).
        Ok(GarageOperations.EditDraft(current, current.Revision, Build("TYR-T1-STREET"), ctx, T));
        LocalProfile withDraft = Save(saved, current);
        CarBuildWorkspace reread = Load(withDraft, a).Workspace;
        Assert.True(reread.Draft.Build.ContentEquals(Build("TYR-T1-STREET")));
        Assert.Contains("TYR-T1-STREET", reread.Draft.PreviewPartIds);
        Assert.True(reread.Applied.Build.ContentEquals(MechanicalSnapshot.Stock()));
        Assert.Empty(withDraft.Cars[0].Parts);
        AssertNoProgressionChange(p, withDraft);
    }

    // ------------------------------------------------------------------ Last Race Build

    [Fact]
    public void LastRaceBuild_IsRecordedWhenALocalRaceBegins_OnlyForAFullSizeEvent_AndNeverClaimsTheEvent()
    {
        LocalProfile start = TwoV01();
        LocalProfile p = Buy(start, A(start), Quote(start, A(start), Candidate), "op-race-buy").Profile;
        string a = A(p);
        AppliedVehicleBuild frozen = LocalGarage.FrozenRaceBuild(p, Cat, Parts, a, T);
        Assert.Equal(2, frozen.Revision);
        Assert.True(frozen.Build.ContentEquals(Candidate));
        string json = ProfileJson.Serialize(p);

        foreach (DrivingSessionKind kind in new[] { DrivingSessionKind.TestYard, DrivingSessionKind.SlotCarToy, DrivingSessionKind.OtherToy, DrivingSessionKind.MenuPreview })
        {
            LocalProgressionResult notARace = LocalGarage.RecordLocalRaceBuild(p, Cat, Parts, a, frozen, "le_not_a_race", T, kind);
            Assert.Equal(LocalOperationStatus.Rejected, notARace.Status);
            AssertUnchanged(json, notARace);
        }
        // A Test Yard preview of an unowned part changes nothing and cannot race.
        BuildEvaluation preview = GarageOperations.PreviewForTest(Load(p, a).Workspace, Build("TYR-T1-STREET", "BRK-T1-PADS", "WGT-T1-STRIP"), LocalGarage.Context(p, Cat, Parts, a));
        Assert.True(preview.CanPreview);
        Assert.Contains("WGT-T1-STRIP", preview.PreviewPartIds);
        var previewCar = new AppliedVehicleBuild { Revision = 1, Build = Build("WGT-T1-STRIP") };
        AssertUnchanged(json, LocalGarage.RecordLocalRaceBuild(p, Cat, Parts, a, previewCar, "le_preview", T));
        AppliedVehicleBuild tampered = frozen.Clone();
        tampered.Build = Build("TYR-T1-STREET");
        AssertUnchanged(json, LocalGarage.RecordLocalRaceBuild(p, Cat, Parts, a, tampered, "le_tampered", T));

        LocalProgressionResult raced = LocalGarage.RecordLocalRaceBuild(p, Cat, Parts, a, frozen, "le_s01_start", T);
        Assert.True(raced.Status == LocalOperationStatus.Applied, raced.Reason);
        Assert.Single(raced.Of(ProgressionChangeKind.RaceBuildRecorded));
        BuildReference last = Load(raced.Profile, a).Workspace.Reference(BuildReferenceKind.LastRaceBuild);
        Assert.True(last.Build.ContentEquals(Candidate));
        Assert.Equal("le_s01_start", last.Context);
        Assert.Equal(p.WalletBalance, raced.Profile.WalletBalance);
        AssertNoProgressionChange(p, raced.Profile);

        LocalProgressionResult again = LocalGarage.RecordLocalRaceBuild(raced.Profile, Cat, Parts, a, frozen, "le_s01_start", T);
        Assert.Equal(LocalOperationStatus.AlreadyApplied, again.Status);
        Assert.Same(raced.Profile, again.Profile);

        // The race's result is still settled under the same event id afterwards (Last Race Build never claims it).
        Assert.False(raced.Profile.HasApplied("le_s01_start"));
        LocalEventFacts facts = LocalProgressionTests.StageRun(raced.Profile, "S01");
        facts.EventId = "le_s01_start";
        LocalProgressionResult settled = LocalProgression.ApplyEvent(raced.Profile, Cat, TestContent.Music, facts);
        Assert.Equal(LocalOperationStatus.Applied, settled.Status);
        Assert.True(settled.BalanceAfter > settled.BalanceBefore);
        Assert.True(Load(settled.Profile, a).Workspace.Reference(BuildReferenceKind.LastRaceBuild).Build.ContentEquals(Candidate));
    }

    // ------------------------------------------------------------------ schema migration and round trips

    static JObject AsV1(LocalProfile p, JArray legacyParts)
    {
        JObject doc = ProfileJson.FromObject(p);
        doc["schemaVersion"] = 1;
        doc.Remove("unassignedParts");
        foreach (JObject car in doc["cars"].Cast<JObject>())
        {
            car.Remove("parts");
            ((JObject)car["workspace"]).Remove("workspaceState");
        }
        doc["parts"] = legacyParts;
        return doc;
    }

    static ProfileLoadResult LoadV1(JObject v1, string profileId, out InMemoryProfileStorage disk)
    {
        disk = new InMemoryProfileStorage();
        disk.Write(ProfileRepository.MainName(profileId), ProfileFileCodec.EncodePayload(v1.ToString(), 1, 0, profileId));
        return new ProfileRepository(disk).Load(profileId);
    }

    static JObject Stack(string partId, int quantity, string source) =>
        new() { ["partId"] = partId, ["quantity"] = quantity, ["source"] = source, ["acquiredUtc"] = "2026-09-01T10:00:00Z" };

    [Fact]
    public void V1Profile_WithoutPerInstanceParts_MigratesAndLoadsCleanly()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        string a = A(p);
        ProfileLoadResult r = LoadV1(AsV1(p, new JArray()), p.ProfileId, out InMemoryProfileStorage disk);
        Assert.Equal(ProfileLoadStatus.Loaded, r.Status);
        Assert.True(r.Migrated);
        Assert.Equal(1, r.MigratedFromVersion);
        Assert.Equal(LocalProfile.CurrentSchemaVersion, r.Profile.SchemaVersion);
        Assert.Empty(r.Profile.Validate());
        Assert.Empty(r.Profile.Cars[0].Parts);
        Assert.Empty(r.Profile.UnassignedParts);
        Assert.Single(disk.List(ProfileRepository.Folder(p.ProfileId) + "premigration/"));
        Assert.True(Load(r.Profile, a).Created);

        // The migrated profile saves as v2 and then reloads without another migration.
        var repo = new ProfileRepository(disk);
        Assert.True(repo.Save(r.Profile).Ok);
        ProfileLoadResult again = repo.Load(p.ProfileId);
        Assert.False(again.Migrated);
        Assert.Equal(ProfileJson.Serialize(r.Profile), ProfileJson.Serialize(again.Profile));
    }

    [Fact]
    public void V1PartStacks_MoveToTheOnlyCar_OrStayUnassignedWithoutOwnership()
    {
        LocalProfile one = LocalProgressionTests.NewProfile();
        var legacy = new JArray(Stack("TYR-T1-STREET", 1, "purchase"), Stack("BRK-T1-PADS", 2, "reward"), Stack("", 1, "odd"), Stack("SUS-T1-SPRINGS", 0, "empty"));
        LocalProfile migrated = LoadV1(AsV1(one, legacy), one.ProfileId, out _).Profile;
        Assert.NotNull(migrated);
        Assert.Empty(migrated.Validate());
        OwnedCar car = migrated.Cars[0];
        Assert.Equal(new[] { "TYR-T1-STREET", "BRK-T1-PADS" }, car.Parts.Select(x => x.PartId));
        Assert.Equal("migrated:purchase", car.Parts[0].Source);
        Assert.All(car.Parts, x => Assert.Equal(1, x.Quantity));
        Assert.Equal(3, migrated.UnassignedParts.Count); // BRK remainder (1), the id-less stack, the empty stack
        Assert.Equal(1, migrated.UnassignedParts.Single(x => x.PartId == "BRK-T1-PADS").Quantity);
        Assert.True(LocalGarage.Ownership(migrated).Owns(car.InstanceId, "BRK-T1-PADS"));
        Assert.False(LocalGarage.Ownership(migrated).Owns(car.InstanceId, "SUS-T1-SPRINGS"));
        Assert.Equal(QuoteStatus.NothingToBuy, LocalGarage.Quote(migrated, Cat, Parts, car.InstanceId, Candidate, T).Status);

        LocalProfile two = TwoV01();
        LocalProfile ambiguous = LoadV1(AsV1(two, new JArray(Stack("TYR-T1-STREET", 1, "purchase"))), two.ProfileId, out _).Profile;
        Assert.NotNull(ambiguous);
        Assert.Empty(ambiguous.Validate());
        Assert.All(ambiguous.Cars, c => Assert.Empty(c.Parts)); // never guessed onto one of several cars
        Assert.Equal("TYR-T1-STREET", ambiguous.UnassignedParts.Single().PartId);
        Assert.False(LocalGarage.Ownership(ambiguous).Owns(A(ambiguous), "TYR-T1-STREET"));
        Assert.False(LocalGarage.Ownership(ambiguous).Owns(B(ambiguous), "TYR-T1-STREET"));
    }

    [Fact]
    public void LegacyBuildDocuments_InACarWorkspace_AreMigratedByTheBuildCodec_AndThenStored()
    {
        LocalProfile p = LocalProgressionTests.NewProfile();
        string a = A(p);
        JObject v1 = AsV1(p, new JArray());
        ((JArray)v1["cars"][0]["workspace"]["mechanicalLoadouts"]).Add(new JObject
        {
            ["slotId"] = "m1", ["name"] = "Wet Grip",
            ["document"] = new JObject
            {
                ["schema"] = "night-signal/tune-preset", ["schemaVersion"] = 1, ["updatedUtc"] = "2026-09-01T10:00:00Z",
                ["data"] = new JObject { ["name"] = "Wet Grip", ["partIds"] = new JArray("TYR-T1-STREET", "NOPE-GONE"), ["note"] = "old era" },
            },
        });
        LocalProfile migrated = LoadV1(v1, p.ProfileId, out _).Profile;
        Assert.NotNull(migrated);

        LocalWorkspaceLoad load = Load(migrated, a);
        Assert.True(load.Migrated);
        Assert.Equal(1, load.StoredRevision);
        Assert.Equal(2, load.Workspace.Revision);
        Assert.NotEmpty(load.Notices);
        MechanicalLoadout wet = Assert.Single(load.Workspace.Loadouts);
        Assert.Equal("Wet Grip", wet.Name);
        Assert.Equal("m1", wet.LoadoutId);
        Assert.Equal("TYR-T1-STREET", wet.Build.PartIn(PartSlot.Tyres));
        Assert.Equal(new[] { "NOPE-GONE" }, wet.UnresolvedPartIds); // kept with an explanation, never dropped
        Assert.True(load.Workspace.Applied.Build.ContentEquals(MechanicalSnapshot.Stock()));
        Assert.Equal(Cat.Car("V01").BasePI, load.Workspace.Applied.Pi);

        LocalProfile stored = Save(migrated, load.Workspace);
        LocalWorkspaceLoad reread = Load(RoundTrip(stored), a);
        Assert.False(reread.Migrated);
        Assert.Equal(2, reread.StoredRevision);
        Assert.Equal(BuildDocumentCodec.SerializeWorkspace(load.Workspace), BuildDocumentCodec.SerializeWorkspace(reread.Workspace));
    }

    [Fact]
    public void ProfileJson_RoundTrip_IsStable_WithGarageData()
    {
        LocalProfile p = TwoV01();
        string a = A(p);
        p = Buy(p, a, Quote(p, a, Candidate), "op-json-buy").Profile;
        CarBuildWorkspace ws = Load(p, a).Workspace;
        BuildContext ctx = LocalGarage.Context(p, Cat, Parts, a);
        Ok(GarageOperations.BeginWorkshopSession(ws, ctx, T));
        Ok(GarageOperations.SaveAs(ws, ws.Revision, "Boss Try", "S07", ctx, T, fromApplied: true));
        Ok(GarageOperations.SaveVisualPreset(ws, ws.Revision, "Midnight", "night-signal/livery@1", "{\"paint\":\"#101820\",\"t\":\"2026-09-27T12:00:00Z\"}", T));
        Ok(GarageOperations.EditDraft(ws, ws.Revision, Build("TYR-T1-STREET", "BRK-T1-PADS", "WGT-T1-STRIP"), ctx, T));
        p = Save(p, ws);
        p = LocalGarage.RecordLocalRaceBuild(p, Cat, Parts, a, LocalGarage.FrozenRaceBuild(p, Cat, Parts, a, T), "le_json_race", T).Profile;
        Assert.Empty(p.Validate());

        string once = ProfileJson.Serialize(p);
        Assert.Equal(once, ProfileJson.Serialize(ProfileJson.Clone(p)));
        Assert.Equal(once, ProfileJson.Serialize(ProfileJson.Deserialize<LocalProfile>(once)));
        LocalProfile back = RoundTrip(p);
        Assert.Empty(back.Validate());
        Assert.Equal(ProfileJson.Serialize(p.Cars), ProfileJson.Serialize(back.Cars));
        Assert.Equal(ProfileJson.Serialize(p.WalletHistory), ProfileJson.Serialize(back.WalletHistory));
        Assert.Equal(BuildDocumentCodec.SerializeWorkspace(Load(p, a).Workspace), BuildDocumentCodec.SerializeWorkspace(Load(back, a).Workspace));
        Assert.Equal("{\"paint\":\"#101820\",\"t\":\"2026-09-27T12:00:00Z\"}", Load(back, a).Workspace.VisualPresets[0].PayloadJson);
    }

    [Fact]
    public void Validate_RefusesMissingDuplicateOrStackedCarParts()
    {
        LocalProfile p = TwoV01();
        LocalProfile dup = ProfileJson.Clone(p);
        dup.Cars[0].Parts.Add(new OwnedPart { PartId = "TYR-T1-STREET" });
        dup.Cars[0].Parts.Add(new OwnedPart { PartId = "TYR-T1-STREET" });
        Assert.Contains(dup.Validate(), e => e.Contains("owned parts"));
        LocalProfile stacked = ProfileJson.Clone(p);
        stacked.Cars[0].Parts.Add(new OwnedPart { PartId = "TYR-T1-STREET", Quantity = 2 });
        Assert.Contains(stacked.Validate(), e => e.Contains("owned parts"));
        LocalProfile missing = ProfileJson.Clone(p);
        missing.Cars[1].Parts = null;
        Assert.Contains(missing.Validate(), e => e.Contains("parts list"));
        LocalProfile fine = ProfileJson.Clone(p);
        fine.Cars[0].Parts.Add(new OwnedPart { PartId = "TYR-T1-STREET" });
        fine.Cars[1].Parts.Add(new OwnedPart { PartId = "TYR-T1-STREET" }); // each instance owns its own copy
        Assert.Empty(fine.Validate());
    }
}
