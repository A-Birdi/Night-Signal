using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;

namespace NightSignal.Core.Builds
{
    public enum RepairKind
    {
        /// <summary>A stored slot id this version does not know.</summary>
        UnknownSlot = 0,
        /// <summary>The part id no longer exists in the catalogue.</summary>
        RemovedPart = 1,
        /// <summary>The part exists but belongs to a different slot.</summary>
        WrongSlot = 2,
        /// <summary>Not compatible with this car model (chassis/family/layout rule).</summary>
        Incompatible = 3,
        /// <summary>Not owned and not yet in the shop for this player's act (not previewable).</summary>
        Locked = 4,
        /// <summary>Compatible and in the shop but not owned by THIS car instance (previewable, purchasable).</summary>
        NotOwned = 5,
        /// <summary>Not owned and no longer sold (retired).</summary>
        Unavailable = 6,
        /// <summary>Tuning value invalid for the installed parts (unknown key, not adjustable, out of range, off step, version).</summary>
        TuningInvalid = 7,
        /// <summary>A resolved parameter left its validated safe range (authoring error; never clamped silently).</summary>
        OutOfSafeRange = 8,
        /// <summary>Resulting PI exceeds the event cap.</summary>
        OverCap = 9,
        /// <summary>The event/Cup rule freezes builds right now.</summary>
        BuildLocked = 10,
        /// <summary>A migrated legacy preset referenced an id that could not be placed in a slot.</summary>
        UnresolvedLegacyPart = 11,
    }

    /// <summary>One exact repair line. Nothing is ever silently substituted with stock, bought or dropped.</summary>
    public sealed class RepairItem
    {
        public RepairKind Kind;
        public string Slot = "";
        public string PartId = "";
        public string PartName = "";
        public string Detail = "";
        /// <summary>Current price for NotOwned items (the missing-part list); 0 otherwise.</summary>
        public long Price;

        /// <summary>Items that also stop a Test Yard preview (only ownership, cap and build lock allow previewing).</summary>
        public bool BlocksPreview => Kind != RepairKind.NotOwned && Kind != RepairKind.OverCap && Kind != RepairKind.BuildLocked;

        public override string ToString()
        {
            string what = string.IsNullOrEmpty(PartId) ? Slot : $"{Slot} {PartId}{(string.IsNullOrEmpty(PartName) ? "" : " (" + PartName + ")")}";
            return $"{Kind}: {what}{(string.IsNullOrEmpty(Detail) ? "" : " — " + Detail)}";
        }
    }

    /// <summary>Per-car-INSTANCE part ownership. Parts are bought for, and stay owned by, one instance (swapped-out parts remain owned).</summary>
    public interface IPartOwnership
    {
        bool Owns(string carInstanceId, string partId);
    }

    /// <summary>Simple in-memory ownership (Local profile, tests). The control plane backs the interface with its ledger.</summary>
    public sealed class PartInventory : IPartOwnership
    {
        public SortedDictionary<string, SortedSet<string>> ByInstance =
            new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        public bool Owns(string carInstanceId, string partId) =>
            ByInstance.TryGetValue(carInstanceId ?? "", out SortedSet<string> set) && set.Contains(partId ?? "");

        /// <summary>Returns false when already owned (a part is never granted twice).</summary>
        public bool Grant(string carInstanceId, string partId)
        {
            if (!ByInstance.TryGetValue(carInstanceId, out SortedSet<string> set))
                ByInstance[carInstanceId] = set = new SortedSet<string>(StringComparer.Ordinal);
            return set.Add(partId);
        }

        public IReadOnlyCollection<string> OwnedBy(string carInstanceId) =>
            ByInstance.TryGetValue(carInstanceId ?? "", out SortedSet<string> set) ? (IReadOnlyCollection<string>)set : Array.Empty<string>();

        public PartInventory Clone()
        {
            var c = new PartInventory();
            foreach (var kv in ByInstance) c.ByInstance[kv.Key] = new SortedSet<string>(kv.Value, StringComparer.Ordinal);
            return c;
        }
    }

    /// <summary>Ownership plus pending grants (used to validate Buy-and-Apply before anything is committed).</summary>
    public sealed class OwnershipWithGrants : IPartOwnership
    {
        readonly IPartOwnership inner;
        readonly string instanceId;
        readonly HashSet<string> grants;

        public OwnershipWithGrants(IPartOwnership inner, string instanceId, IEnumerable<string> grants)
        {
            this.inner = inner;
            this.instanceId = instanceId;
            this.grants = new HashSet<string>(grants, StringComparer.Ordinal);
        }

        public bool Owns(string carInstanceId, string partId) =>
            (carInstanceId == instanceId && grants.Contains(partId ?? "")) || inner.Owns(carInstanceId, partId);
    }

    /// <summary>Optional event rules at apply time (class cap, frozen event / build-locked Cup leg).</summary>
    public sealed class EventConstraints
    {
        /// <summary>Maximum PI for the event; 0 = uncapped.</summary>
        public int MaxPi;
        public bool BuildLocked;
        public string Label = "";
    }

    /// <summary>Everything needed to evaluate builds for one car model: catalogues, ownership and the player's shop act.</summary>
    public sealed class BuildContext
    {
        public PartsCatalogue Parts;
        public CarDef Car;
        public CarTuningDef Tuning;
        public IPartOwnership Ownership;
        /// <summary>Highest act whose shop is open for this player (1–4); parts with UnlockAct above it are Locked.</summary>
        public int ShopAct = 1;

        ResolvedCarSpec stock;

        public ResolvedCarSpec Stock => stock ?? (stock = BuildResolver.ResolveStock(Car, Tuning, Parts));

        public static BuildContext Create(ContentCatalogue content, PartsCatalogue parts, string modelId, IPartOwnership ownership, int shopAct)
        {
            CarDef car = content.Car(modelId);
            if (!content.CarTunings.TryGetValue(modelId, out CarTuningDef tuning)) throw new KeyNotFoundException($"No car tuning for {modelId}");
            return new BuildContext { Parts = parts, Car = car, Tuning = tuning, Ownership = ownership ?? new PartInventory(), ShopAct = shopAct };
        }

        public BuildContext WithOwnership(IPartOwnership ownership) =>
            new BuildContext { Parts = Parts, Car = Car, Tuning = Tuning, Ownership = ownership, ShopAct = ShopAct, stock = stock };
    }

    public sealed class BuildEvaluation
    {
        public ResolvedCarSpec Spec;
        public PiEstimate Pi;
        public List<RepairItem> Repairs = new List<RepairItem>();
        /// <summary>Compatible, shop-unlocked parts this instance does not own: "Preview only — not owned".</summary>
        public List<string> PreviewPartIds = new List<string>();
        /// <summary>Structurally valid: parameters resolved.</summary>
        public bool Resolved => Spec != null;
        /// <summary>May be driven in the private Test Yard (unowned-but-available parts allowed; never locked ones).</summary>
        public bool CanPreview;
        /// <summary>May be saved as a named planning loadout ("needs parts" when PreviewPartIds is not empty).</summary>
        public bool CanSaveAsPlan;
        /// <summary>Owned, legal, within the event cap: may become the applied build.</summary>
        public bool CanApply;

        /// <summary>Exact missing-part list with current prices.</summary>
        public IEnumerable<RepairItem> MissingParts => Repairs.Where(r => r.Kind == RepairKind.NotOwned);
    }

    public static class BuildEvaluator
    {
        /// <summary>
        /// Validates a whole build for one car instance: structure/compatibility/tuning (resolver), then ownership and shop
        /// availability per part, then event constraints. Returns the exact repair list; never substitutes or buys.
        /// </summary>
        public static BuildEvaluation Evaluate(MechanicalSnapshot build, string instanceId, BuildContext ctx, EventConstraints constraints = null)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var ev = new BuildEvaluation();
            ResolveResult r = BuildResolver.Resolve(ctx.Car, ctx.Tuning, ctx.Parts, build);
            ev.Repairs.AddRange(r.Issues);
            ev.Spec = r.Spec;

            foreach (var slotAndPart in SlotParts(build))
            {
                if (!ctx.Parts.TryPart(slotAndPart.Value, out PartDef part)) continue; // already reported by the resolver
                if (!ctx.Parts.CheckCompatibility(part, ctx.Car, ctx.Tuning).Compatible) continue;
                if (ctx.Ownership.Owns(instanceId, part.Id)) continue;
                string slot = slotAndPart.Key;
                if (part.Retired)
                    ev.Repairs.Add(new RepairItem { Kind = RepairKind.Unavailable, Slot = slot, PartId = part.Id, PartName = part.Name, Detail = "Not owned and no longer sold." });
                else if (part.UnlockAct > ctx.ShopAct)
                    ev.Repairs.Add(new RepairItem { Kind = RepairKind.Locked, Slot = slot, PartId = part.Id, PartName = part.Name, Detail = $"Shop unlocks in Act {part.UnlockAct}." });
                else
                {
                    ev.Repairs.Add(new RepairItem { Kind = RepairKind.NotOwned, Slot = slot, PartId = part.Id, PartName = part.Name, Price = part.Price, Detail = "Preview only — not owned." });
                    ev.PreviewPartIds.Add(part.Id);
                }
            }

            if (ev.Spec != null)
            {
                ev.Pi = PerformanceIndexEstimator.Estimate(ev.Spec, ctx.Stock, ctx.Car.BasePI);
                if (constraints != null && constraints.MaxPi > 0 && ev.Pi.Value > constraints.MaxPi)
                    ev.Repairs.Add(new RepairItem { Kind = RepairKind.OverCap, Detail = $"PI {ev.Pi.Value} is above the {constraints.Label} cap {constraints.MaxPi}." });
            }
            if (constraints != null && constraints.BuildLocked)
                ev.Repairs.Add(new RepairItem { Kind = RepairKind.BuildLocked, Detail = $"Builds are frozen for {constraints.Label}." });

            ev.CanPreview = ev.Spec != null && ev.Repairs.All(x => !x.BlocksPreview);
            ev.CanSaveAsPlan = ev.CanPreview;
            ev.CanApply = ev.Spec != null && ev.Repairs.Count == 0;
            return ev;
        }

        static IEnumerable<KeyValuePair<string, string>> SlotParts(MechanicalSnapshot build)
        {
            foreach (var kv in TuningSetup.Ordinal(build.Parts))
                if (!string.IsNullOrEmpty(kv.Value)) yield return kv;
            if (!string.IsNullOrEmpty(build.UtilityPartId))
                yield return new KeyValuePair<string, string>(PartSlots.Id(PartSlot.Utility), build.UtilityPartId);
        }
    }
}
