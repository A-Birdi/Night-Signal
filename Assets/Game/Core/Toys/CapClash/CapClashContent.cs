using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NightSignal.Core.Toys.CapClash
{
    /// <summary>Equal physics for every cap (a paid car never improves a cap, Addendum 02 §2.1). Metres, seconds.</summary>
    public sealed class CapPhysicsDef
    {
        public double CapRadius = 0.016;
        public double MaxLaunchSpeed = 2.1;
        /// <summary>Kinetic sliding friction (constant deceleration, m/s²).</summary>
        public double SlideDeceleration = 1.1;
        /// <summary>Speed-proportional damping (1/s).</summary>
        public double LinearDamping = 0.2;
        public double CapRestitution = 0.85;
        public double StopSpeed = 0.004;
        public double MinPower = 0.05;
        public int StepHz = 240;
        /// <summary>Two settled distances within this tolerance are a tie (simulation/display tolerance).</summary>
        public double TieTolerance = 0.0005;
    }

    public sealed class CapSegmentDef
    {
        public double[] A;
        public double[] B;
        /// <summary>"rail" (plain edge), "bumper" (livelier rail) or "obstacle".</summary>
        public string Kind = "rail";
        public double Restitution = 0.7;
    }

    /// <summary>A rubberized tool-shaped prop, authored as one or more CONVEX outlines (counter-clockwise).</summary>
    public sealed class CapObstacleDef
    {
        public string Id;
        public string Name;
        public List<List<double[]>> Parts = new List<List<double[]>>();
        public double Restitution = 0.5;
    }

    /// <summary>A target configuration: centre and zone radii for 50/25/10 points (ascending radii).</summary>
    public sealed class CapTargetDef
    {
        public string Id;
        public string Name;
        public string Hint;
        public double X;
        public double Y;
        public double[] Zones = { 0.045, 0.09, 0.15 };
        /// <summary>Authored as a bank-shot configuration (Toolbox Banks).</summary>
        public bool Bank;

        public int PointsAt(double distance)
        {
            if (distance <= Zones[0]) return 50;
            if (distance <= Zones[1]) return 25;
            if (distance <= Zones[2]) return 10;
            return 0;
        }
    }

    /// <summary>One mark of the cooperative six-mark target card (a checklist, not a card game).</summary>
    public sealed class CapMarkDef
    {
        public string Id;
        public string Label;
        public string Target;
        public int MinPoints;
        public bool RequireBank;
    }

    public sealed class CapArrangementDef
    {
        public string Id;
        public string Name;
        public string Summary;
        public double Width;
        public double Length;
        public double LaunchY;
        public double LaunchMinX;
        public double LaunchMaxX;
        public double MaxAngleDeg = 30;
        /// <summary>Caps that settle short of this line are returned to their owner (keeps the launch strip clear).</summary>
        public double FoulLineY;
        public List<CapSegmentDef> Rails = new List<CapSegmentDef>();
        public List<CapObstacleDef> Obstacles = new List<CapObstacleDef>();
        public List<CapTargetDef> Targets = new List<CapTargetDef>();
        public string DefaultTarget;
        public List<CapMarkDef> Card = new List<CapMarkDef>();

        [JsonIgnore] internal CapSeg[] Segments;

        public CapTargetDef Target(string id)
        {
            foreach (CapTargetDef t in Targets) if (t.Id == id) return t;
            return null;
        }
    }

    internal struct CapSeg
    {
        public Vec2 A;
        public Vec2 B;
        public double Restitution;
        public bool Obstacle;
    }

    public sealed class CapClashContent
    {
        public int Schema;
        public CapPhysicsDef Physics = new CapPhysicsDef();
        public List<CapArrangementDef> Arrangements = new List<CapArrangementDef>();

        public CapArrangementDef Arrangement(string id)
        {
            foreach (CapArrangementDef a in Arrangements) if (a.Id == id) return a;
            return null;
        }

        public static CapClashContent Parse(string json)
        {
            CapClashContent c = JsonConvert.DeserializeObject<CapClashContent>(json);
            if (c == null) throw new FormatException("capclash content is empty");
            c.Build();
            return c;
        }

        void Build()
        {
            var errors = new List<string>();
            if (Arrangements.Count < 2) errors.Add("at least two Cap Clash arrangements are required (D210)");
            var ids = new HashSet<string>();
            foreach (CapArrangementDef a in Arrangements)
            {
                if (!ToyCommandCodec.ValidId(a.Id) || !ids.Add(a.Id)) errors.Add("bad or duplicate arrangement id " + a.Id);
                if (a.Width <= 0 || a.Length <= 0 || a.FoulLineY <= a.LaunchY) errors.Add(a.Id + ": bad tray geometry");
                if (a.Target(a.DefaultTarget) == null) errors.Add(a.Id + ": default target missing");
                if (a.Card.Count != 6) errors.Add(a.Id + ": the cooperative card needs exactly six marks");
                foreach (CapMarkDef m in a.Card)
                    if (a.Target(m.Target) == null) errors.Add(a.Id + ": mark " + m.Id + " uses unknown target");
                foreach (CapTargetDef t in a.Targets)
                    if (t.Zones == null || t.Zones.Length != 3 || !(t.Zones[0] < t.Zones[1] && t.Zones[1] < t.Zones[2])) errors.Add(a.Id + "/" + t.Id + ": zones must be three ascending radii");
                var segs = new List<CapSeg>();
                foreach (CapSegmentDef r in a.Rails)
                    segs.Add(new CapSeg { A = new Vec2(r.A[0], r.A[1]), B = new Vec2(r.B[0], r.B[1]), Restitution = r.Restitution });
                foreach (CapObstacleDef o in a.Obstacles)
                    foreach (List<double[]> part in o.Parts)
                    {
                        if (part.Count < 3) { errors.Add(a.Id + "/" + o.Id + ": obstacle part needs ≥ 3 points"); continue; }
                        for (int i = 0; i < part.Count; i++)
                        {
                            double[] p = part[i], q = part[(i + 1) % part.Count];
                            segs.Add(new CapSeg { A = new Vec2(p[0], p[1]), B = new Vec2(q[0], q[1]), Restitution = o.Restitution, Obstacle = true });
                        }
                    }
                a.Segments = segs.ToArray();
            }
            if (errors.Count > 0) throw new FormatException("capclash content invalid: " + string.Join("; ", errors));
        }
    }
}
