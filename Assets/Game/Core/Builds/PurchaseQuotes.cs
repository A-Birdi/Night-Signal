using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NightSignal.Core.Builds
{
    public sealed class QuoteLine
    {
        public string PartId = "";
        public string Slot = "";
        public string Name = "";
        public int Tier;
        public long Price;
    }

    /// <summary>
    /// Explicit Buy-and-Apply quote (Addendum 02 §10.4): the exact missing parts at current prices for one car instance's
    /// candidate build, pinned to the price/catalogue revision and the applied revision it was made against.
    /// </summary>
    public sealed class PurchaseAndApplyQuote
    {
        public string QuoteId = "";
        public string InstanceId = "";
        public string ModelId = "";
        public MechanicalSnapshot Build = new MechanicalSnapshot();
        public string BuildHash = "";
        public int Pi;
        public string PiClass = "";
        public List<QuoteLine> Lines = new List<QuoteLine>();
        public long Total;
        public int PriceRevision;
        public int CatalogueRevision;
        /// <summary>Applied revision the quote was made against; a change elsewhere invalidates it.</summary>
        public long AppliedRevision;
        public DateTime IssuedUtc;
        public DateTime ExpiresUtc;
    }

    public enum QuoteStatus
    {
        Ok = 0,
        /// <summary>Every part is already owned: use a plain Apply (reusing owned parts is free).</summary>
        NothingToBuy = 1,
        /// <summary>The build has non-purchasable problems (see Repairs): locked, retired, removed, incompatible, tune, cap.</summary>
        NotPurchasable = 2,
    }

    public sealed class QuoteResult
    {
        public QuoteStatus Status;
        public PurchaseAndApplyQuote Quote;
        public List<RepairItem> Repairs = new List<RepairItem>();
        public bool AffordableNow;
        public long WalletBalance;
        public string Message = "";
    }

    public enum SettlementOutcome
    {
        Settled = 0,
        /// <summary>This quote id already settled: nothing is charged or granted again; the stored record is returned.</summary>
        AlreadySettled = 1,
        RejectedNotConfirmed = 2,
        RejectedWrongCar = 3,
        RejectedExpired = 4,
        RejectedPriceChanged = 5,
        RejectedStaleBuild = 6,
        RejectedUnavailable = 7,
        RejectedIncompatible = 8,
        RejectedAlreadyOwned = 9,
        RejectedInsufficientFunds = 10,
        RejectedNotApplicable = 11,
    }

    /// <summary>What the authoritative store persists atomically with the debit, grants and applied build.</summary>
    public sealed class SettlementRecord
    {
        public string QuoteId = "";
        public string InstanceId = "";
        public long Debit;
        public List<string> Grants = new List<string>();
        public long AppliedRevision;
        public string BuildHash = "";
        public long BalanceAfter;
        public DateTime SettledUtc;
    }

    /// <summary>Durable settled-quote lookup (unique by quote id) provided by the control plane or the local profile store.</summary>
    public interface IQuoteLedger
    {
        bool TryGet(string quoteId, out SettlementRecord record);
    }

    public sealed class InMemoryQuoteLedger : IQuoteLedger
    {
        public readonly Dictionary<string, SettlementRecord> Records = new Dictionary<string, SettlementRecord>(StringComparer.Ordinal);

        public bool TryGet(string quoteId, out SettlementRecord record) => Records.TryGetValue(quoteId ?? "", out record);

        /// <summary>Returns false when the id already exists (models the store's unique constraint).</summary>
        public bool TryAdd(SettlementRecord record)
        {
            if (Records.ContainsKey(record.QuoteId)) return false;
            Records.Add(record.QuoteId, record);
            return true;
        }
    }

    public sealed class SettlementResult
    {
        public SettlementOutcome Outcome;
        public bool Success => Outcome == SettlementOutcome.Settled;
        public string Message = "";
        public long Debit;
        public long NewBalance;
        public List<string> Grants = new List<string>();
        /// <summary>The new workspace state (applied) when Settled; null otherwise — the caller's copy stays intact.</summary>
        public CarBuildWorkspace Workspace;
        public SettlementRecord Record;
        /// <summary>Current prices when the quote's prices no longer hold.</summary>
        public List<QuoteLine> CurrentLines = new List<QuoteLine>();
        public List<RepairItem> Repairs = new List<RepairItem>();
        public bool PerformanceChanged;
    }

    /// <summary>
    /// Engine-free Buy-and-Apply decisions. The control plane (Online) and the local profile (Local) call these inside their
    /// own atomic transaction: persist <see cref="SettlementResult.Record"/> (unique quote id), debit, grants and the new
    /// workspace together, or nothing. Owned parts are never bought again; failure leaves the applied build intact.
    /// </summary>
    public static class PurchaseQuotes
    {
        public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

        public static QuoteResult Create(CarBuildWorkspace ws, MechanicalSnapshot build, BuildContext ctx, long walletBalance,
            EventConstraints constraints, DateTime nowUtc, TimeSpan? lifetime = null)
        {
            if (ws == null) throw new ArgumentNullException(nameof(ws));
            if (build == null) throw new ArgumentNullException(nameof(build));
            BuildEvaluation ev = BuildEvaluator.Evaluate(build, ws.Car.InstanceId, ctx, constraints);
            var result = new QuoteResult { WalletBalance = walletBalance };
            List<RepairItem> blocking = ev.Repairs.Where(r => r.Kind != RepairKind.NotOwned).ToList();
            if (!ev.Resolved || blocking.Count > 0)
            {
                result.Status = QuoteStatus.NotPurchasable;
                result.Repairs = ev.Repairs;
                result.Message = "These items cannot be bought and applied: " + string.Join("; ", blocking);
                return result;
            }
            List<QuoteLine> lines = ev.MissingParts
                .GroupBy(r => r.PartId, StringComparer.Ordinal).Select(g => g.First())
                .Select(r => Line(ctx.Parts.Part(r.PartId))).ToList();
            if (lines.Count == 0)
            {
                result.Status = QuoteStatus.NothingToBuy;
                result.Message = "Every part is already owned by this car: Apply it directly (no charge).";
                return result;
            }
            var q = new PurchaseAndApplyQuote
            {
                InstanceId = ws.Car.InstanceId,
                ModelId = ws.Car.ModelId,
                Build = build.Clone(),
                BuildHash = ev.Spec.BuildHash,
                Pi = ev.Pi.Value,
                PiClass = ev.Pi.Class.ToString(),
                Lines = lines,
                Total = lines.Sum(l => l.Price),
                PriceRevision = ctx.Parts.PriceRevision,
                CatalogueRevision = ctx.Parts.Revision,
                AppliedRevision = ws.Applied.Revision,
                IssuedUtc = nowUtc,
                ExpiresUtc = nowUtc + (lifetime ?? DefaultLifetime),
            };
            q.QuoteId = "q-" + BuildHashing.Sha256Hex(string.Join("|", q.InstanceId, q.AppliedRevision.ToString(CultureInfo.InvariantCulture),
                build.SelectionHash(), q.PriceRevision.ToString(CultureInfo.InvariantCulture), q.IssuedUtc.Ticks.ToString(CultureInfo.InvariantCulture))).Substring(0, 24);
            result.Status = QuoteStatus.Ok;
            result.Quote = q;
            result.AffordableNow = walletBalance >= q.Total;
            result.Message = $"Buy {lines.Count} part(s) for {q.Total} Credits and apply (PI {q.Pi} {q.PiClass}).";
            return result;
        }

        /// <summary>
        /// Authoritative decision for a confirmed quote. Order: idempotency (quote id) → confirmation → car → expiry → price
        /// revision → applied revision → availability/compatibility/ownership per line → funds → whole-build apply validation.
        /// Never mutates <paramref name="ws"/>; on success returns a new workspace with the build applied.
        /// </summary>
        public static SettlementResult Settle(PurchaseAndApplyQuote quote, bool confirmed, CarBuildWorkspace ws, BuildContext ctx,
            long walletBalance, IQuoteLedger ledger, EventConstraints constraints, DateTime nowUtc)
        {
            if (quote == null) throw new ArgumentNullException(nameof(quote));
            if (ws == null) throw new ArgumentNullException(nameof(ws));
            if (ledger != null && ledger.TryGet(quote.QuoteId, out SettlementRecord prior))
                return new SettlementResult
                {
                    Outcome = SettlementOutcome.AlreadySettled, Record = prior, Debit = 0, NewBalance = walletBalance,
                    Message = "Already settled; nothing charged or granted again.",
                };
            if (!confirmed) return Reject(SettlementOutcome.RejectedNotConfirmed, walletBalance, "Buy and Apply needs explicit confirmation.");
            if (quote.InstanceId != ws.Car.InstanceId) return Reject(SettlementOutcome.RejectedWrongCar, walletBalance, "Quote is for a different car.");
            if (nowUtc > quote.ExpiresUtc) return Reject(SettlementOutcome.RejectedExpired, walletBalance, "Quote expired; request a new one.");
            if (quote.PriceRevision != ctx.Parts.PriceRevision || quote.CatalogueRevision != ctx.Parts.Revision)
            {
                SettlementResult changed = Reject(SettlementOutcome.RejectedPriceChanged, walletBalance, "Prices or the catalogue changed; review the new quote.");
                foreach (QuoteLine l in quote.Lines)
                    if (ctx.Parts.TryPart(l.PartId, out PartDef p)) changed.CurrentLines.Add(Line(p));
                return changed;
            }
            if (quote.AppliedRevision != ws.Applied.Revision)
                return Reject(SettlementOutcome.RejectedStaleBuild, walletBalance, "This car's applied build changed since the quote; request a new one.");

            foreach (QuoteLine l in quote.Lines)
            {
                if (!ctx.Parts.TryPart(l.PartId, out PartDef p) || p.Retired || p.UnlockAct > ctx.ShopAct)
                    return Reject(SettlementOutcome.RejectedUnavailable, walletBalance, $"{l.Name} is no longer available.");
                if (p.Price != l.Price)
                    return Reject(SettlementOutcome.RejectedPriceChanged, walletBalance, $"{l.Name} price changed.");
                if (!ctx.Parts.CheckCompatibility(p, ctx.Car, ctx.Tuning).Compatible)
                    return Reject(SettlementOutcome.RejectedIncompatible, walletBalance, $"{l.Name} does not fit this car.");
                if (ctx.Ownership.Owns(ws.Car.InstanceId, p.Id))
                    return Reject(SettlementOutcome.RejectedAlreadyOwned, walletBalance, $"{l.Name} is already owned by this car and is never bought again; request a new quote.");
            }
            long total = quote.Lines.Sum(l => l.Price);
            if (total != quote.Total) return Reject(SettlementOutcome.RejectedPriceChanged, walletBalance, "Quote total does not match its lines.");
            if (!Rules.Wallet.TryDebit(walletBalance, total, out long newBalance))
                return Reject(SettlementOutcome.RejectedInsufficientFunds, walletBalance, $"Needs {total} Credits; wallet has {walletBalance}.");

            List<string> grants = quote.Lines.Select(l => l.PartId).ToList();
            CarBuildWorkspace next = ws.Clone();
            BuildContext withGrants = ctx.WithOwnership(new OwnershipWithGrants(ctx.Ownership, ws.Car.InstanceId, grants));
            OperationResult applied = GarageOperations.ApplySnapshot(next, quote.Build, "quote:" + quote.QuoteId, withGrants, constraints, nowUtc);
            if (applied.Status != OpStatus.Ok)
            {
                SettlementResult bad = Reject(SettlementOutcome.RejectedNotApplicable, walletBalance, "The bought build could not be applied; nothing was charged. " + applied.Message);
                bad.Repairs = applied.Repairs;
                return bad;
            }
            if (next.Draft != null && next.Draft.Build.ContentEquals(quote.Build)) next.Draft.PreviewPartIds.Clear();
            var record = new SettlementRecord
            {
                QuoteId = quote.QuoteId, InstanceId = ws.Car.InstanceId, Debit = total, Grants = grants,
                AppliedRevision = next.Applied.Revision, BuildHash = next.Applied.BuildHash, BalanceAfter = newBalance, SettledUtc = nowUtc,
            };
            return new SettlementResult
            {
                Outcome = SettlementOutcome.Settled, Debit = total, NewBalance = newBalance, Grants = grants, Workspace = next,
                Record = record, PerformanceChanged = applied.PerformanceChanged,
                Message = $"Bought {grants.Count} part(s) for {total} Credits and applied revision {next.Applied.Revision}.",
            };
        }

        static QuoteLine Line(PartDef p) => new QuoteLine { PartId = p.Id, Slot = p.Slot, Name = p.Name, Tier = p.Tier, Price = p.Price };

        static SettlementResult Reject(SettlementOutcome o, long balance, string message) =>
            new SettlementResult { Outcome = o, NewBalance = balance, Message = message };
    }
}
