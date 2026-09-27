using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NightSignal.Core.Toys.PocketCircuit
{
    public sealed class PocketCircuitContent
    {
        public const double MaxClosureErrorM = 0.002;
        public const double MaxClosureHeadingDeg = 0.1;

        public int Schema;
        public SlotCarPhysics Physics = new SlotCarPhysics();
        public List<SlotLayoutDef> Layouts = new List<SlotLayoutDef>();

        public SlotLayoutDef Layout(string id)
        {
            foreach (SlotLayoutDef l in Layouts) if (l.Id == id) return l;
            return null;
        }

        /// <summary>Parses, builds every layout's geometry and lane statistics, and validates the authored tracks.</summary>
        public static PocketCircuitContent Parse(string json)
        {
            PocketCircuitContent c = JsonConvert.DeserializeObject<PocketCircuitContent>(json);
            if (c == null) throw new FormatException("pocket circuit content is empty");
            if (c.Physics == null) c.Physics = new SlotCarPhysics();
            var errors = new List<string>();
            if (c.Layouts.Count < 3) errors.Add("three Pocket Circuit layouts are required (D210)");
            var ids = new HashSet<string>();
            foreach (SlotLayoutDef l in c.Layouts)
            {
                if (!ToyCommandCodec.ValidId(l.Id) || !ids.Add(l.Id)) { errors.Add("bad or duplicate layout id " + l.Id); continue; }
                if (l.Lanes < 1 || l.Lanes > 6) errors.Add(l.Id + ": 1..6 lanes");
                if (l.LaneSpacing <= 0.03) errors.Add(l.Id + ": lane spacing too small");
                bool badPiece = false;
                foreach (SlotPieceDef p in l.Pieces)
                {
                    bool ok = p.Type == "straight" ? p.Length > 0 : p.Type == "arc" && p.Radius > (l.Lanes - 1) * l.LaneSpacing / 2 + 0.05 && p.Angle != 0 && Math.Abs(p.Angle) <= 360;
                    if (!ok) { errors.Add(l.Id + ": invalid piece " + p.Type); badPiece = true; }
                }
                if (badPiece) continue;
                l.Track = SlotTrack.Build(l, c.Physics);
                if (l.Track.ClosureErrorM > MaxClosureErrorM || l.Track.ClosureHeadingErrorDeg > MaxClosureHeadingDeg)
                    errors.Add(l.Id + ": the slot does not close (" + l.Track.ClosureErrorM.ToString("0.0000") + " m)");
                if (l.Track.HasCrossing && l.Track.CrossingClearanceM < SlotTrack.MinCrossingClearance)
                    errors.Add(l.Id + ": crossing without enough over/under clearance");
            }
            if (errors.Count > 0) throw new FormatException("pocket circuit content invalid: " + string.Join("; ", errors));
            return c;
        }
    }
}
