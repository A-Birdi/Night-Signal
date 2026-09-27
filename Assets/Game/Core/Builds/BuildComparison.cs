using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NightSignal.Core.Builds
{
    public sealed class PartDelta
    {
        public string Slot = "";
        /// <summary>Part id, or "" for stock/none.</summary>
        public string From = "";
        public string FromName = "";
        public string To = "";
        public string ToName = "";
    }

    public sealed class TuneDelta
    {
        public string Key = "";
        /// <summary>Effective value (explicit or the part default); null when not adjustable in that build.</summary>
        public int? From;
        public int? To;
    }

    public sealed class ParamDelta
    {
        public SimParam Param;
        public double From;
        public double To;
        public string Unit = "";
        public double Change => To - From;
    }

    /// <summary>Compact A→B delta list with the resulting PI/class of both builds (Addendum 02 §9.2 Compare).</summary>
    public sealed class BuildComparison
    {
        public List<PartDelta> Parts = new List<PartDelta>();
        public List<TuneDelta> Tuning = new List<TuneDelta>();
        public PartDelta Utility;
        public List<ParamDelta> Parameters = new List<ParamDelta>();
        public int? PiFrom;
        public int? PiTo;
        public string ClassFrom = "";
        public string ClassTo = "";
        public bool ClassChanged;
        public List<RepairItem> RepairsFrom = new List<RepairItem>();
        public List<RepairItem> RepairsTo = new List<RepairItem>();
        /// <summary>Human-readable compact lines (UI text; data, never markup).</summary>
        public List<string> Lines = new List<string>();

        public bool Identical => Parts.Count == 0 && Tuning.Count == 0 && Utility == null;

        public static BuildComparison Of(MechanicalSnapshot a, MechanicalSnapshot b, string instanceId, BuildContext ctx)
        {
            var c = new BuildComparison();
            a = a ?? MechanicalSnapshot.Stock();
            b = b ?? MechanicalSnapshot.Stock();
            foreach (string slot in (a.Parts.Keys.Concat(b.Parts.Keys)).Distinct(StringComparer.Ordinal).OrderBy(s => SlotOrder(s)))
            {
                a.Parts.TryGetValue(slot, out string from);
                b.Parts.TryGetValue(slot, out string to);
                if (string.Equals(from ?? "", to ?? "", StringComparison.Ordinal)) continue;
                c.Parts.Add(Delta(slot, from, to, ctx));
            }
            if (!string.Equals(a.UtilityPartId ?? "", b.UtilityPartId ?? "", StringComparison.Ordinal))
                c.Utility = Delta(PartSlots.Id(PartSlot.Utility), a.UtilityPartId, b.UtilityPartId, ctx);

            BuildEvaluation ea = BuildEvaluator.Evaluate(a, instanceId, ctx);
            BuildEvaluation eb = BuildEvaluator.Evaluate(b, instanceId, ctx);
            c.RepairsFrom = ea.Repairs.Where(r => r.Kind != RepairKind.NotOwned).ToList();
            c.RepairsTo = eb.Repairs.Where(r => r.Kind != RepairKind.NotOwned).ToList();
            Dictionary<string, int> ta = EffectiveTune(a, ctx), tb = EffectiveTune(b, ctx);
            foreach (string key in ta.Keys.Concat(tb.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            {
                int? fa = ta.TryGetValue(key, out int x) ? x : (int?)null;
                int? fb = tb.TryGetValue(key, out int y) ? y : (int?)null;
                if (fa != fb) c.Tuning.Add(new TuneDelta { Key = key, From = fa, To = fb });
            }
            if (ea.Resolved)
            {
                c.PiFrom = ea.Pi.Value;
                c.ClassFrom = ea.Pi.Class.ToString();
            }
            if (eb.Resolved)
            {
                c.PiTo = eb.Pi.Value;
                c.ClassTo = eb.Pi.Class.ToString();
            }
            c.ClassChanged = c.ClassFrom != c.ClassTo;
            if (ea.Resolved && eb.Resolved)
            {
                foreach (SimParam p in SimParams.All)
                {
                    long va = ea.Spec.GetMicro(p), vb = eb.Spec.GetMicro(p);
                    if (va != vb) c.Parameters.Add(new ParamDelta { Param = p, From = va / 1e6, To = vb / 1e6, Unit = SimParams.Info(p).Unit });
                }
            }

            foreach (PartDelta d in c.Parts) c.Lines.Add($"{d.Slot}: {Label(d.From, d.FromName)} → {Label(d.To, d.ToName)}");
            if (c.Utility != null) c.Lines.Add($"utility: {Label(c.Utility.From, c.Utility.FromName)} → {Label(c.Utility.To, c.Utility.ToName)}");
            foreach (TuneDelta t in c.Tuning)
                c.Lines.Add($"{t.Key}: {(t.From.HasValue ? t.From.Value.ToString(CultureInfo.InvariantCulture) : "fixed")} → {(t.To.HasValue ? t.To.Value.ToString(CultureInfo.InvariantCulture) : "fixed")}");
            c.Lines.Add($"PI (estimate): {(c.PiFrom?.ToString(CultureInfo.InvariantCulture) ?? "?")} {c.ClassFrom} → {(c.PiTo?.ToString(CultureInfo.InvariantCulture) ?? "?")} {c.ClassTo}");
            return c;
        }

        static Dictionary<string, int> EffectiveTune(MechanicalSnapshot s, BuildContext ctx)
        {
            var installed = new List<PartDef>();
            foreach (var kv in s.Parts)
                if (ctx.Parts.TryPart(kv.Value, out PartDef p) && PartSlots.Id(p.SlotValue) == kv.Key) installed.Add(p);
            ResolvedCarSpec stock = ctx.Stock;
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (TuningControlInfo c in TuningModel.Controls(installed, stock.Get)) d[c.Key] = TuningModel.ValueOrDefault(s.Tuning, c);
            return d;
        }

        static PartDelta Delta(string slot, string from, string to, BuildContext ctx) => new PartDelta
        {
            Slot = slot,
            From = from ?? "",
            FromName = NameOf(from, ctx),
            To = to ?? "",
            ToName = NameOf(to, ctx),
        };

        static string NameOf(string id, BuildContext ctx) =>
            string.IsNullOrEmpty(id) ? "" : ctx.Parts.TryPart(id, out PartDef p) ? p.Name : "(removed part)";

        static string Label(string id, string name) => string.IsNullOrEmpty(id) ? "stock" : string.IsNullOrEmpty(name) ? id : name;

        static int SlotOrder(string slotId) => PartSlots.TryParse(slotId, out PartSlot s) ? (int)s : 100;
    }
}
