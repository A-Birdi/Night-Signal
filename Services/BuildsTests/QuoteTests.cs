using NightSignal.Core.Builds;
using Newtonsoft.Json.Linq;

namespace NightSignal.BuildsTests;

/// <summary>Buy-and-Apply decisions (Addendum 02 §10.4, E06 at the engine-free level).</summary>
public sealed class QuoteTests
{
    const string Car = "V05";
    const string Inst = "ci_q";
    static readonly DateTime T = TestData.T0;

    sealed class Fx
    {
        public PartInventory Inv;
        public BuildContext Ctx;
        public CarBuildWorkspace Ws;
        public InMemoryQuoteLedger Ledger = new();
    }

    static Fx New(int shopAct = 3, PartsCatalogue parts = null)
    {
        var inv = TestData.Owning(Inst, "TYR-T1-STREET", "BRK-T1-PADS");
        BuildContext ctx = BuildContext.Create(TestData.Content, parts ?? TestData.Parts, Car, inv, shopAct);
        return new Fx { Inv = inv, Ctx = ctx, Ws = GarageOperations.NewWorkspace(Inst, ctx, T) };
    }

    static MechanicalSnapshot Candidate => TestData.Build("TYR-T2-SPORT", "BRK-T1-PADS", "ENG-T2-STREET").WithTune(TuningKeys.FinalDrive, 1000).With(PartSlot.Gearbox, "GBX-T1-FINAL");

    static PurchaseAndApplyQuote Quote(Fx f, MechanicalSnapshot b = null, long wallet = 500_000)
    {
        QuoteResult q = PurchaseQuotes.Create(f.Ws, b ?? Candidate, f.Ctx, wallet, null, T);
        Assert.Equal(QuoteStatus.Ok, q.Status);
        return q.Quote;
    }

    [Fact]
    public void Quote_ListsExactlyTheMissingPartsAtCurrentPrices()
    {
        Fx f = New();
        QuoteResult q = PurchaseQuotes.Create(f.Ws, Candidate, f.Ctx, 50_000, null, T);
        Assert.Equal(QuoteStatus.Ok, q.Status);
        Assert.Equal(new[] { "ENG-T2-STREET", "GBX-T1-FINAL", "TYR-T2-SPORT" }, q.Quote.Lines.Select(l => l.PartId).OrderBy(x => x));
        Assert.DoesNotContain(q.Quote.Lines, l => l.PartId == "BRK-T1-PADS"); // owned: never bought again
        Assert.Equal(48_000 + 8_000 + 32_000, q.Quote.Total);
        Assert.False(q.AffordableNow);
        Assert.Equal(TestData.Parts.PriceRevision, q.Quote.PriceRevision);
        Assert.Equal(f.Ws.Applied.Revision, q.Quote.AppliedRevision);
        Assert.StartsWith("q-", q.Quote.QuoteId);
        // Same request → same quote id (a duplicate request cannot mint a second quote).
        Assert.Equal(q.Quote.QuoteId, PurchaseQuotes.Create(f.Ws, Candidate, f.Ctx, 50_000, null, T).Quote.QuoteId);
    }

    [Fact]
    public void Quote_RefusesLockedIncompatibleOrOverCapBuilds_AndSaysWhenNothingNeedsBuying()
    {
        Fx f = New(shopAct: 2);
        QuoteResult locked = PurchaseQuotes.Create(f.Ws, Candidate.With(PartSlot.Tyres, "TYR-T3-SEMISLICK"), f.Ctx, 1_000_000, null, T);
        Assert.Equal(QuoteStatus.NotPurchasable, locked.Status);
        Assert.Contains(locked.Repairs, r => r.Kind == RepairKind.Locked);
        QuoteResult incompatible = PurchaseQuotes.Create(f.Ws, Candidate.With(PartSlot.Differential, "DIF-T2-AWD-CENTRE"), f.Ctx, 1_000_000, null, T);
        Assert.Equal(QuoteStatus.NotPurchasable, incompatible.Status);
        QuoteResult overCap = PurchaseQuotes.Create(f.Ws, Candidate, f.Ctx, 1_000_000, new EventConstraints { MaxPi = 399, Label = "S05" }, T);
        Assert.Equal(QuoteStatus.NotPurchasable, overCap.Status);
        QuoteResult owned = PurchaseQuotes.Create(f.Ws, TestData.Build("TYR-T1-STREET", "BRK-T1-PADS"), f.Ctx, 0, null, T);
        Assert.Equal(QuoteStatus.NothingToBuy, owned.Status);
    }

    [Fact]
    public void Settle_DebitsGrantsAndAppliesTogether_WithoutMutatingTheCallersWorkspace()
    {
        Fx f = New();
        PurchaseAndApplyQuote q = Quote(f);
        string before = BuildDocumentCodec.SerializeWorkspace(f.Ws);
        SettlementResult s = PurchaseQuotes.Settle(q, true, f.Ws, f.Ctx, 200_000, f.Ledger, null, T);
        Assert.Equal(SettlementOutcome.Settled, s.Outcome);
        Assert.Equal(88_000, s.Debit);
        Assert.Equal(112_000, s.NewBalance);
        Assert.Equal(q.Lines.Select(l => l.PartId), s.Grants);
        Assert.True(s.Workspace.Applied.Build.ContentEquals(Candidate));
        Assert.Equal(2, s.Workspace.Applied.Revision);
        Assert.StartsWith("quote:", s.Workspace.Applied.Source);
        Assert.True(s.Workspace.Reference(BuildReferenceKind.BeforeLastApply).Build.ContentEquals(MechanicalSnapshot.Stock()));
        Assert.True(s.PerformanceChanged);
        Assert.Equal(before, BuildDocumentCodec.SerializeWorkspace(f.Ws)); // caller persists s.Workspace atomically with the debit
        Assert.Equal(q.QuoteId, s.Record.QuoteId);
    }

    [Fact]
    public void DuplicateAndRetriedRequests_SettleAtMostOnce()
    {
        Fx f = New();
        PurchaseAndApplyQuote q = Quote(f);
        long wallet = 200_000;
        SettlementResult first = PurchaseQuotes.Settle(q, true, f.Ws, f.Ctx, wallet, f.Ledger, null, T);
        Assert.True(first.Success);
        // Two concurrent handlers decided from the same state: the store's unique quote id admits only one.
        SettlementResult racing = PurchaseQuotes.Settle(q, true, f.Ws, f.Ctx, wallet, f.Ledger, null, T);
        Assert.True(f.Ledger.TryAdd(first.Record));
        Assert.False(f.Ledger.TryAdd(racing.Record));
        // Commit: persist the winner.
        foreach (string id in first.Grants) f.Inv.Grant(Inst, id);
        f.Ws = first.Workspace;
        wallet = first.NewBalance;
        // Retry after a disconnect / server restart: already settled, nothing charged or granted again.
        for (int i = 0; i < 5; i++)
        {
            SettlementResult retry = PurchaseQuotes.Settle(q, true, f.Ws, f.Ctx, wallet, f.Ledger, null, T.AddMinutes(1));
            Assert.Equal(SettlementOutcome.AlreadySettled, retry.Outcome);
            Assert.Equal(0, retry.Debit);
            Assert.Equal(wallet, retry.NewBalance);
            Assert.Null(retry.Workspace);
        }
        // Even without the ledger (lost record), the owned parts are never bought again.
        SettlementResult noLedger = PurchaseQuotes.Settle(q, true, f.Ws, f.Ctx, wallet, new InMemoryQuoteLedger(), null, T);
        Assert.NotEqual(SettlementOutcome.Settled, noLedger.Outcome);
        Assert.Equal(wallet, noLedger.NewBalance);
    }

    [Fact]
    public void PriceChange_InsufficientFunds_StaleBuild_Expiry_Unconfirmed_AllLeaveTheAppliedBuildIntact()
    {
        Fx f = New();
        PurchaseAndApplyQuote q = Quote(f);
        string before = BuildDocumentCodec.SerializeWorkspace(f.Ws);

        Assert.Equal(SettlementOutcome.RejectedNotConfirmed, PurchaseQuotes.Settle(q, false, f.Ws, f.Ctx, 1_000_000, f.Ledger, null, T).Outcome);
        SettlementResult poor = PurchaseQuotes.Settle(q, true, f.Ws, f.Ctx, 87_999, f.Ledger, null, T);
        Assert.Equal(SettlementOutcome.RejectedInsufficientFunds, poor.Outcome);
        Assert.Equal(87_999, poor.NewBalance);
        Assert.Equal(SettlementOutcome.RejectedExpired, PurchaseQuotes.Settle(q, true, f.Ws, f.Ctx, 1_000_000, f.Ledger, null, q.ExpiresUtc.AddSeconds(1)).Outcome);

        // A price revision.
        JObject doc = JObject.Parse(TestData.PartsJson);
        doc["priceRevision"] = 2;
        ((JArray)doc["parts"]).First(p => (string)p["id"] == "TYR-T2-SPORT")["price"] = 34_000;
        Fx repriced = New(parts: PartsCatalogue.Load(doc.ToString()));
        SettlementResult changed = PurchaseQuotes.Settle(q, true, f.Ws, repriced.Ctx, 1_000_000, f.Ledger, null, T);
        Assert.Equal(SettlementOutcome.RejectedPriceChanged, changed.Outcome);
        Assert.Contains(changed.CurrentLines, l => l.PartId == "TYR-T2-SPORT" && l.Price == 34_000);

        // Another device applied something else first.
        CarBuildWorkspace moved = f.Ws.Clone();
        Assert.True(GarageOperations.EditDraft(moved, moved.Revision, TestData.Build("TYR-T1-STREET"), f.Ctx, T).Accepted);
        Assert.True(GarageOperations.Apply(moved, moved.Revision, f.Ctx, null, T).Accepted);
        Assert.Equal(SettlementOutcome.RejectedStaleBuild, PurchaseQuotes.Settle(q, true, moved, f.Ctx, 1_000_000, f.Ledger, null, T).Outcome);

        // A part became owned meanwhile (e.g. bought with another quote): never bought twice.
        f.Inv.Grant(Inst, "TYR-T2-SPORT");
        Assert.Equal(SettlementOutcome.RejectedAlreadyOwned, PurchaseQuotes.Settle(q, true, f.Ws, f.Ctx, 1_000_000, f.Ledger, null, T).Outcome);

        Assert.Equal(before, BuildDocumentCodec.SerializeWorkspace(f.Ws));
        Assert.Empty(f.Ledger.Records);
    }

    [Fact]
    public void Settlement_RechecksAvailabilityCompatibilityCapAndCar()
    {
        Fx f = New();
        PurchaseAndApplyQuote q = Quote(f);
        // Shop availability re-checked (e.g. a Local profile loaded at a lower act).
        Fx early = New(shopAct: 1);
        Assert.Equal(SettlementOutcome.RejectedUnavailable, PurchaseQuotes.Settle(q, true, f.Ws, early.Ctx, 1_000_000, f.Ledger, null, T).Outcome);
        // Event cap re-checked at the authoritative transaction: nothing is charged.
        SettlementResult cap = PurchaseQuotes.Settle(q, true, f.Ws, f.Ctx, 1_000_000, f.Ledger, new EventConstraints { MaxPi = 390, Label = "S05" }, T);
        Assert.Equal(SettlementOutcome.RejectedNotApplicable, cap.Outcome);
        Assert.Equal(1_000_000, cap.NewBalance);
        Assert.Contains(cap.Repairs, r => r.Kind == RepairKind.OverCap);
        // Wrong car instance.
        CarBuildWorkspace other = GarageOperations.NewWorkspace("ci_other", f.Ctx, T);
        Assert.Equal(SettlementOutcome.RejectedWrongCar, PurchaseQuotes.Settle(q, true, other, f.Ctx, 1_000_000, f.Ledger, null, T).Outcome);
        // Compatibility re-checked: the same quote presented for a different model fails.
        var inv = TestData.Owning(Inst, "TYR-T1-STREET", "BRK-T1-PADS");
        BuildContext awd = BuildContext.Create(TestData.Content, TestData.Parts, "V03", inv, 3);
        var q2 = Quote(f, TestData.Build("TYR-T2-SPORT", "DIF-T2-RWD-ADJ"));
        Assert.Equal(SettlementOutcome.RejectedIncompatible, PurchaseQuotes.Settle(q2, true, f.Ws, awd, 1_000_000, f.Ledger, null, T).Outcome);
    }
}
