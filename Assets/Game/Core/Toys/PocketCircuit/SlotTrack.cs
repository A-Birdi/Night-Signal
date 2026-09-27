using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NightSignal.Core.Toys.PocketCircuit
{
    /// <summary>
    /// The one toy-car model shared by every lane and every participant (Addendum 02 §5.1: ALL toy cars have equal
    /// performance; parts, purchases, ranks and account bonuses never touch it). Units: metres, seconds.
    /// Throttle sets the motor's target speed (DC-motor slot car): a = K·(u·V − v) − drag·v² − rolling − brake − g'·slope.
    /// Grip: lateral demand v²·|κ| must stay under <see cref="LateralGrip"/>, scaled by vertical curvature (crests reduce it).
    /// </summary>
    public sealed class SlotCarPhysics
    {
        public double ResponseRate = 1.5;
        public double MotorTopSpeed = 0.8;
        /// <summary>Electric brake when the trigger is released (u &lt; 0.02).</summary>
        public double ReleaseBrake = 1.1;
        public double Drag = 0.12;
        public double Rolling = 0.03;
        public double LateralGrip = 0.68;
        /// <summary>Toy-scale gravity used for slopes (a tuning constant for a magnet-traction toy, not real g).</summary>
        public double SlopeGravity = 1.6;
        /// <summary>How strongly crests/dips change grip: scale = 1 + CrestFactor·v²·κz/9.81.</summary>
        public double CrestFactor = 5.0;
        public int StepHz = 120;
        public long DeslotReturnMs = 1200;
        /// <summary>A held throttle expires without fresh input (disconnect/focus loss cannot hold it indefinitely).</summary>
        public long ThrottleHoldMs = 500;
        public double GridBehindLine = 0.15;

        public double Acceleration(double throttle, double v, double slope)
        {
            double a = ResponseRate * (throttle * MotorTopSpeed - v) - Drag * v * v - (v > 0 ? Rolling : 0) - SlopeGravity * slope;
            if (throttle < 0.02 && v > 0) a -= ReleaseBrake;
            return a;
        }

        public double GripLimit(double v, double verticalCurvature) =>
            LateralGrip * ToyMath.Clamp(1 + CrestFactor * v * v * verticalCurvature / 9.81, 0.5, 1.5);
    }

    public sealed class SlotPieceDef
    {
        public string Type;
        public double Length;
        public double Radius;
        public double Angle;
        public double Rise;
        public string Label;

        public double CentreLength => Type == "arc" ? Math.Abs(Angle * ToyMath.Deg2Rad) * Radius : Length;
    }

    public sealed class SlotStartDef
    {
        public double X, Y, Z, Heading;
    }

    public sealed class SlotLayoutDef
    {
        public string Id;
        public string Name;
        public string Summary;
        public int Lanes;
        public double LaneSpacing;
        public SlotStartDef Start = new SlotStartDef();
        public List<SlotPieceDef> Pieces = new List<SlotPieceDef>();

        [JsonIgnore] public SlotTrack Track { get; internal set; }
    }

    /// <summary>Disclosed per-lane geometry and reference-driving numbers (Addendum 02 §5.2 lane fairness).</summary>
    public sealed class SlotLaneStats
    {
        public int Lane;
        public double LengthM;
        public double MaxCurvature;
        /// <summary>∫|κ| ds / length.</summary>
        public double MeanAbsCurvature;
        /// <summary>Clean lap of the shared reference driver (same policy on every lane).</summary>
        public double ReferenceLapMs;
        public int ReferenceDeslots;
        /// <summary>Common constant-throttle input on every lane.</summary>
        public double CommonInputLapMs;
        public int CommonInputDeslots;
        /// <summary>Lane reference time / mean reference time across lanes. Normalized lap = raw lap / ratio.</summary>
        public double LaneRatio;
    }

    /// <summary>Sampled path of one lane (lap distance s ∈ [0, Length)).</summary>
    public sealed class SlotLane
    {
        public int Number;
        public double Offset;
        public double Length;
        public Vec3[] Points;
        public double[] S;
        public double[] Heading;
        public double[] Curvature;
        public double[] Slope;
        public double[] VerticalCurvature;
        public int[] Piece;
        /// <summary>Lap distance at the start of each piece (de-slot reset points).</summary>
        public double[] PieceStart;

        public int IndexAt(double s)
        {
            if (s <= 0) return 0;
            int lo = 0, hi = S.Length - 1;
            if (s >= S[hi]) return hi;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (S[mid] <= s) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Interpolated world position at lap distance s (for rendering and prediction).</summary>
        public Vec3 PositionAt(double s)
        {
            s = Wrap(s);
            int i = IndexAt(s);
            int j = (i + 1) % Points.Length;
            double segEnd = j == 0 ? Length : S[j];
            double t = segEnd > S[i] ? (s - S[i]) / (segEnd - S[i]) : 0;
            return Vec3.Lerp(Points[i], Points[j], t);
        }

        public double Wrap(double s)
        {
            s %= Length;
            return s < 0 ? s + Length : s;
        }
    }

    /// <summary>
    /// Built 3D slot geometry for one layout: a centreline integrated from authored pieces and one sampled path per lane
    /// (lanes are parallel offsets; lane 1 is rightmost at the gantry). Validates closure and over/under clearance.
    /// </summary>
    public sealed class SlotTrack
    {
        public const double SampleSpacing = 0.01;
        public const double MinCrossingClearance = 0.05;
        public const double BorderWidth = 0.06;

        public SlotLayoutDef Layout { get; private set; }
        public SlotLane[] Lanes { get; private set; }
        public Vec3[] Centreline { get; private set; }
        public double CentreLength { get; private set; }
        public double ClosureErrorM { get; private set; }
        public double ClosureHeadingErrorDeg { get; private set; }
        /// <summary>Smallest vertical separation where the track passes over itself (∞ when it never does).</summary>
        public double CrossingClearanceM { get; private set; }
        public bool HasCrossing { get; private set; }
        public List<SlotLaneStats> Stats { get; private set; }

        public static SlotTrack Build(SlotLayoutDef layout, SlotCarPhysics physics)
        {
            var t = new SlotTrack { Layout = layout };
            t.BuildGeometry();
            t.Stats = ReferenceDriver.Measure(t, physics);
            return t;
        }

        void BuildGeometry()
        {
            SlotLayoutDef L = Layout;
            var cx = new List<Vec3>();
            var ch = new List<double>();
            var cp = new List<int>();
            double x = L.Start.X, y = L.Start.Y, z = L.Start.Z, h = L.Start.Heading * ToyMath.Deg2Rad;
            for (int pi = 0; pi < L.Pieces.Count; pi++)
            {
                SlotPieceDef p = L.Pieces[pi];
                double len = p.CentreLength;
                int n = Math.Max(1, (int)Math.Round(len / SampleSpacing));
                double ds = len / n;
                double k = p.Type == "arc" ? Math.Sign(p.Angle) / p.Radius : 0;
                double z0 = z;
                for (int i = 0; i < n; i++)
                {
                    cx.Add(new Vec3(x, y, z));
                    ch.Add(h);
                    cp.Add(pi);
                    if (k == 0) { x += ds * Math.Cos(h); y += ds * Math.Sin(h); }
                    else
                    {
                        double h2 = h + k * ds;
                        x += (Math.Sin(h2) - Math.Sin(h)) / k;
                        y += -(Math.Cos(h2) - Math.Cos(h)) / k;
                        h = h2;
                    }
                    z = z0 + p.Rise * ToyMath.Ease((i + 1.0) / n);
                }
                z = z0 + p.Rise;
            }
            ClosureErrorM = new Vec3(x - L.Start.X, y - L.Start.Y, z - L.Start.Z).Length();
            ClosureHeadingErrorDeg = Math.Abs(ToyMath.WrapPi(h - L.Start.Heading * ToyMath.Deg2Rad)) * ToyMath.Rad2Deg;
            Centreline = cx.ToArray();
            CentreLength = 0;
            foreach (SlotPieceDef p in L.Pieces) CentreLength += p.CentreLength;

            Lanes = new SlotLane[L.Lanes];
            for (int li = 0; li < L.Lanes; li++)
            {
                double o = (li - (L.Lanes - 1) / 2.0) * L.LaneSpacing;
                int m = cx.Count;
                var lane = new SlotLane
                {
                    Number = li + 1, Offset = o, Points = new Vec3[m], S = new double[m], Heading = new double[m], Curvature = new double[m],
                    Slope = new double[m], VerticalCurvature = new double[m], Piece = cp.ToArray(), PieceStart = new double[L.Pieces.Count],
                };
                for (int i = 0; i < m; i++)
                {
                    double hh = ch[i];
                    lane.Points[i] = new Vec3(cx[i].X - Math.Sin(hh) * o, cx[i].Y + Math.Cos(hh) * o, cx[i].Z);
                }
                var seg = new double[m];
                var horiz = new double[m];
                double acc = 0;
                for (int i = 0; i < m; i++)
                {
                    Vec3 a = lane.Points[i], b = lane.Points[(i + 1) % m];
                    seg[i] = (b - a).Length();
                    horiz[i] = Math.Max(1e-9, new Vec2(b.X - a.X, b.Y - a.Y).Length());
                    lane.S[i] = acc;
                    lane.Heading[i] = Math.Atan2(b.Y - a.Y, b.X - a.X);
                    lane.Slope[i] = (b.Z - a.Z) / horiz[i];
                    acc += seg[i];
                }
                lane.Length = acc;
                for (int i = 0; i < m; i++)
                {
                    int prev = (i - 1 + m) % m;
                    double span = 0.5 * (horiz[prev] + horiz[i]);
                    lane.Curvature[i] = ToyMath.WrapPi(lane.Heading[i] - lane.Heading[prev]) / span;
                    lane.VerticalCurvature[i] = (lane.Slope[i] - lane.Slope[prev]) / span;
                }
                for (int i = m - 1; i >= 0; i--) lane.PieceStart[lane.Piece[i]] = lane.S[i];
                Lanes[li] = lane;
            }

            // Over/under check on the centreline (every 2 cm): wherever the plan views overlap, the decks must clear.
            double width = (L.Lanes - 1) * L.LaneSpacing + 2 * BorderWidth;
            CrossingClearanceM = double.PositiveInfinity;
            int stride = 2;
            for (int i = 0; i < Centreline.Length; i += stride)
                for (int j = i + stride; j < Centreline.Length; j += stride)
                {
                    double along = Math.Min((j - i) * SampleSpacing, CentreLength - (j - i) * SampleSpacing);
                    if (along < 3 * width) continue; // neighbours along the path, not a crossing
                    Vec3 a = Centreline[i], b = Centreline[j];
                    if (new Vec2(a.X - b.X, a.Y - b.Y).Length() >= width) continue;
                    HasCrossing = true;
                    double dz = Math.Abs(a.Z - b.Z);
                    if (dz < CrossingClearanceM) CrossingClearanceM = dz;
                }
        }

        public SlotLane Lane(int number) => number >= 1 && number <= Lanes.Length ? Lanes[number - 1] : null;
    }

    /// <summary>
    /// Deterministic reference driving on each lane with the SAME policy and physics: (1) a curvature-aware reference
    /// driver (speed profile under the grip limit with a braking pass) and (2) a common constant-throttle input.
    /// </summary>
    public static class ReferenceDriver
    {
        public const double Safety = 0.9;
        public const double Lookahead = 0.04;
        public const double CommonThrottle = 0.28;
        public const int Laps = 3;

        public static List<SlotLaneStats> Measure(SlotTrack track, SlotCarPhysics ph)
        {
            var list = new List<SlotLaneStats>();
            foreach (SlotLane lane in track.Lanes)
            {
                var st = new SlotLaneStats { Lane = lane.Number, LengthM = lane.Length };
                double sumAbs = 0;
                for (int i = 0; i < lane.S.Length; i++)
                {
                    double k = Math.Abs(lane.Curvature[i]);
                    if (k > st.MaxCurvature) st.MaxCurvature = k;
                    double next = i + 1 < lane.S.Length ? lane.S[i + 1] : lane.Length;
                    sumAbs += k * (next - lane.S[i]);
                }
                st.MeanAbsCurvature = sumAbs / lane.Length;
                double[] profile = SpeedProfile(lane, ph);
                Drive(lane, ph, (s, v) =>
                {
                    double target = Math.Min(profile[lane.IndexAt(s)], profile[lane.IndexAt(lane.Wrap(s + Lookahead))]);
                    if (v > target + 0.005) return 0;
                    return ToyMath.Clamp(target / ph.MotorTopSpeed + 2.0 * (target - v) / ph.MotorTopSpeed, 0, 1);
                }, out st.ReferenceLapMs, out st.ReferenceDeslots);
                Drive(lane, ph, (s, v) => CommonThrottle, out st.CommonInputLapMs, out st.CommonInputDeslots);
                list.Add(st);
            }
            double mean = 0;
            foreach (SlotLaneStats s in list) mean += s.ReferenceLapMs;
            mean /= list.Count;
            foreach (SlotLaneStats s in list) s.LaneRatio = Math.Round(s.ReferenceLapMs / mean, 4);
            return list;
        }

        /// <summary>Highest speed at each sample that respects the grip limit (with safety) and the release-brake distance.</summary>
        public static double[] SpeedProfile(SlotLane lane, SlotCarPhysics ph)
        {
            int m = lane.S.Length;
            var prof = new double[m];
            for (int i = 0; i < m; i++)
            {
                double k = Math.Abs(lane.Curvature[i]);
                double lim = k > 1e-6 ? Math.Sqrt(ph.LateralGrip * Safety * ToyMath.Clamp(1 + Math.Min(0, lane.VerticalCurvature[i]) * 0.5, 0.5, 1) / k) : ph.MotorTopSpeed;
                prof[i] = Math.Min(ph.MotorTopSpeed * 0.98, lim);
            }
            double brake = ph.ReleaseBrake * 0.8;
            for (int rep = 0; rep < 2; rep++)
                for (int i = m - 1; i >= 0; i--)
                {
                    int j = (i + 1) % m;
                    double seg = (j == 0 ? lane.Length : lane.S[j]) - lane.S[i];
                    double vmax = Math.Sqrt(prof[j] * prof[j] + 2 * brake * seg);
                    if (prof[i] > vmax) prof[i] = vmax;
                }
            return prof;
        }

        /// <summary>Drives a standing-start out lap plus <see cref="Laps"/> timed laps; returns the best timed lap.</summary>
        static void Drive(SlotLane lane, SlotCarPhysics ph, Func<double, double, double> policy, out double bestLapMs, out int deslots)
        {
            var car = new SlotCarState { S = lane.Wrap(-ph.GridBehindLine), Mode = SlotCarMode.Driving };
            deslots = 0;
            bestLapMs = double.PositiveInfinity;
            long tick = 0;
            long maxTicks = (long)ph.StepHz * 400;
            int laps = 0;
            while (laps < Laps && tick < maxTicks)
            {
                car.Throttle = policy(car.S, car.V);
                SlotStepResult r = SlotSim.Step(lane, ph, car);
                tick++;
                if (r.Deslotted)
                {
                    deslots++;
                    car.S = car.LastSafeS; car.V = 0; car.Mode = SlotCarMode.Driving;
                    car.LapTicks += ph.DeslotReturnMs * ph.StepHz / 1000;
                }
                if (r.LapCompleted)
                {
                    if (r.CompletedLapTicks > 0 && car.Lap > 1)
                    {
                        double ms = r.CompletedLapTicks * 1000.0 / ph.StepHz;
                        if (ms < bestLapMs) bestLapMs = ms;
                        laps++;
                    }
                }
            }
            bestLapMs = Math.Round(bestLapMs, 1);
        }
    }
}
