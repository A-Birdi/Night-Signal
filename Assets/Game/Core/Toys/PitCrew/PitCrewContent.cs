using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace NightSignal.Core.Toys.PitCrew
{
    /// <summary>The six recognizable interaction families (Addendum 02 §3.1).</summary>
    public enum OperationFamily { AlignFit = 0, RotateIndex = 1, ControlledTighten = 2, ConnectRoute = 3, MatchMarks = 4, PlaceDetail = 5 }

    public static class OperationFamilies
    {
        public static OperationFamily Parse(string wire)
        {
            switch (wire)
            {
                case "align": return OperationFamily.AlignFit;
                case "rotate": return OperationFamily.RotateIndex;
                case "tighten": return OperationFamily.ControlledTighten;
                case "route": return OperationFamily.ConnectRoute;
                case "marks": return OperationFamily.MatchMarks;
                case "detail": return OperationFamily.PlaceDetail;
                default: throw new FormatException("unknown operation family '" + wire + "'");
            }
        }

        /// <summary>
        /// Forgiving default tolerance per step, in the unit the client measures for that family: align/detail = offset in
        /// millimetres at model scale, rotate/marks = angle error in degrees, tighten = distance of the gauge stop from its
        /// mark (fraction of the gauge), route = distance of the routed clip from its guide in millimetres.
        /// </summary>
        public static double DefaultTolerance(OperationFamily f)
        {
            switch (f)
            {
                case OperationFamily.AlignFit: return 4.0;
                case OperationFamily.RotateIndex: return 8.0;
                case OperationFamily.ControlledTighten: return 0.12;
                case OperationFamily.ConnectRoute: return 6.0;
                case OperationFamily.MatchMarks: return 6.0;
                default: return 6.0;
            }
        }

        /// <summary>Upper bound of a plausible reported error (anything above is malformed input, not a miss).</summary>
        public static double MaxReportedError(OperationFamily f) => f == OperationFamily.ControlledTighten ? 1.0 : (f == OperationFamily.RotateIndex || f == OperationFamily.MatchMarks ? 180.0 : 500.0);
    }

    public sealed class BlueprintPartDef
    {
        public string Id;
        public string Name;
        public string Group;
    }

    public sealed class OperationDef
    {
        public string Id;
        public string Label;
        [JsonProperty("family")] public string FamilyWire;
        public List<string> Parts = new List<string>();
        public List<string> Requires = new List<string>();
        public int Steps = 1;
        public double Seconds = 5;
        public double? Tolerance;

        [JsonIgnore] public OperationFamily Family { get; internal set; }
        [JsonIgnore] public double StepTolerance => Tolerance ?? OperationFamilies.DefaultTolerance(Family);
    }

    public sealed class BlueprintDef
    {
        public string Id;
        public string Name;
        public string Summary;
        public List<BlueprintPartDef> Parts = new List<BlueprintPartDef>();
        public List<OperationDef> Operations = new List<OperationDef>();

        [JsonIgnore] Dictionary<string, OperationDef> byId;

        public OperationDef Operation(string id) => byId != null && id != null && byId.TryGetValue(id, out OperationDef o) ? o : null;

        public IEnumerable<OperationDef> StartAccessible => Operations.Where(o => o.Requires.Count == 0);

        /// <summary>Operations in a valid dependency order (used by tests and the solo walkthrough).</summary>
        public List<OperationDef> TopologicalOrder()
        {
            var done = new HashSet<string>();
            var order = new List<OperationDef>();
            while (order.Count < Operations.Count)
            {
                OperationDef next = Operations.FirstOrDefault(o => !done.Contains(o.Id) && o.Requires.All(done.Contains));
                if (next == null) throw new FormatException(Id + ": dependency cycle");
                done.Add(next.Id);
                order.Add(next);
            }
            return order;
        }

        internal void Build(List<string> errors)
        {
            byId = new Dictionary<string, OperationDef>(StringComparer.Ordinal);
            var partIds = new HashSet<string>(Parts.Select(p => p.Id));
            foreach (OperationDef o in Operations)
            {
                if (!ToyCommandCodec.ValidId(o.Id) || byId.ContainsKey(o.Id)) { errors.Add(Id + ": bad or duplicate operation id " + o.Id); continue; }
                byId[o.Id] = o;
                try { o.Family = OperationFamilies.Parse(o.FamilyWire); }
                catch (FormatException e) { errors.Add(Id + "/" + o.Id + ": " + e.Message); }
                if (o.Steps < 1 || o.Steps > 4) errors.Add(Id + "/" + o.Id + ": steps must be 1..4");
                if (o.Seconds < 3 || o.Seconds > 15) errors.Add(Id + "/" + o.Id + ": forgiving duration must be 3..15 s");
                if (o.Parts.Count == 0) errors.Add(Id + "/" + o.Id + ": every operation visibly installs at least one part");
                foreach (string p in o.Parts) if (!partIds.Contains(p)) errors.Add(Id + "/" + o.Id + ": unknown part " + p);
            }
            foreach (OperationDef o in Operations)
                foreach (string r in o.Requires)
                    if (!byId.ContainsKey(r)) errors.Add(Id + "/" + o.Id + ": unknown dependency " + r);
            if (Operations.Count < 24) errors.Add(Id + ": at least 24 assembly operations are required");
            if (Operations.Select(o => o.Family).Distinct().Count() < 6) errors.Add(Id + ": all six interaction families are required");
            if (StartAccessible.Count() < 6) errors.Add(Id + ": at least six start-accessible tasks are required");
            if (errors.Count == 0)
            {
                try { TopologicalOrder(); }
                catch (FormatException e) { errors.Add(e.Message); }
            }
        }
    }

    public sealed class PitCrewContent
    {
        public int Schema;
        public List<BlueprintDef> Blueprints = new List<BlueprintDef>();

        public BlueprintDef Blueprint(string id)
        {
            foreach (BlueprintDef b in Blueprints) if (b.Id == id) return b;
            return null;
        }

        public static PitCrewContent Parse(string json)
        {
            PitCrewContent c = JsonConvert.DeserializeObject<PitCrewContent>(json);
            if (c == null) throw new FormatException("pitcrew content is empty");
            var errors = new List<string>();
            if (c.Blueprints.Count < 3) errors.Add("three Pit-Crew blueprints are required (D210)");
            var ids = new HashSet<string>();
            foreach (BlueprintDef b in c.Blueprints)
            {
                if (!ToyCommandCodec.ValidId(b.Id) || !ids.Add(b.Id)) errors.Add("bad or duplicate blueprint id " + b.Id);
                b.Build(errors);
            }
            if (errors.Count > 0) throw new FormatException("pitcrew content invalid: " + string.Join("; ", errors));
            return c;
        }
    }
}
