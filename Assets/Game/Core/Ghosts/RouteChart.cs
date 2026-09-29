using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Ghosts
{
    /// <summary>One sector of a run (between two checkpoints): where it lies and the time it gained (−) or lost (+).</summary>
    public sealed class RouteChartSector
    {
        public int Index;
        public int FromSample, ToSample;
        public float FromMetres, ToMetres;
        /// <summary>Time lost against the reference over this sector (µs; negative = gained); null without a reference.</summary>
        public long? DeltaMicros;
    }

    /// <summary>
    /// The compact post-race route/elevation chart (spec §8: "a compact postrace route/elevation chart with braking points
    /// and lost-time sectors"), computed from the run's own recording: the route in plan (x, z), the elevation along the
    /// distance driven, the braking points (a clear speed drop from a local peak) and, against a reference ghost, the time
    /// each sector gained or lost (from the cumulative checkpoint deltas). Presentation only: never a record or a result.
    /// </summary>
    public sealed class RouteChart
    {
        /// <summary>A braking point: a speed peak followed by at least this drop (m/s) within <see cref="BrakeWindowSeconds"/>.</summary>
        public const float BrakeDropMps = 4f, BrakeWindowSeconds = 1.5f, BrakeSpacingMetres = 60f;

        public readonly List<float> X = new List<float>(), Z = new List<float>(), Elevation = new List<float>(), Metres = new List<float>(), Speed = new List<float>();
        public readonly List<int> BrakingPoints = new List<int>();
        public readonly List<RouteChartSector> Sectors = new List<RouteChartSector>();
        public float MinX, MaxX, MinZ, MaxZ, MinElevation, MaxElevation;
        public float LengthMetres => Metres.Count == 0 ? 0f : Metres[Metres.Count - 1];
        /// <summary>The sector that lost the most time, or null (no reference, or no sector lost time).</summary>
        public RouteChartSector WorstSector => Sectors.Where(s => s.DeltaMicros > 0).OrderByDescending(s => s.DeltaMicros).FirstOrDefault();
        /// <summary>The sector that gained the most time, or null.</summary>
        public RouteChartSector BestSector => Sectors.Where(s => s.DeltaMicros < 0).OrderBy(s => s.DeltaMicros).FirstOrDefault();

        /// <param name="run">The player's recording (positions at 10 Hz and checkpoint times).</param>
        /// <param name="cumulativeDeltasMicros">Run minus reference at each checkpoint (as the HUD shows them), or null.</param>
        public static RouteChart Build(GhostRecording run, IReadOnlyList<long> cumulativeDeltasMicros = null)
        {
            var c = new RouteChart();
            if (run == null || run.Count < 2) return c;
            float metres = 0f;
            for (int i = 0; i < run.Count; i++)
            {
                if (i > 0)
                {
                    float dx = run.Px[i] - run.Px[i - 1], dz = run.Pz[i] - run.Pz[i - 1];
                    metres += (float)Math.Sqrt(dx * dx + dz * dz);
                }
                c.X.Add(run.Px[i]);
                c.Z.Add(run.Pz[i]);
                c.Elevation.Add(run.Py[i]);
                c.Metres.Add(metres);
                c.Speed.Add(run.Speed[i]);
            }
            c.MinX = c.X.Min(); c.MaxX = c.X.Max(); c.MinZ = c.Z.Min(); c.MaxZ = c.Z.Max();
            c.MinElevation = c.Elevation.Min(); c.MaxElevation = c.Elevation.Max();

            // Braking points: a local speed peak followed by a clear drop, at least BrakeSpacingMetres apart.
            int window = Math.Max(1, (int)Math.Round(BrakeWindowSeconds * GhostRecording.SampleHz));
            for (int i = 1; i + 1 < run.Count; i++)
            {
                if (run.Speed[i] < run.Speed[i - 1] || run.Speed[i] < run.Speed[i + 1]) continue;
                float low = run.Speed[i];
                for (int j = i + 1; j <= Math.Min(run.Count - 1, i + window); j++) low = Math.Min(low, run.Speed[j]);
                if (run.Speed[i] - low < BrakeDropMps) continue;
                if (c.BrakingPoints.Count > 0 && c.Metres[i] - c.Metres[c.BrakingPoints[c.BrakingPoints.Count - 1]] < BrakeSpacingMetres) continue;
                c.BrakingPoints.Add(i);
            }

            // Sectors between checkpoints (the last runs to the end of the recording).
            int from = 0;
            long previous = 0;
            for (int k = 0; k <= run.CheckpointMicros.Count; k++)
            {
                int to = k < run.CheckpointMicros.Count ? SampleAt(run, run.CheckpointMicros[k] / 1e6f) : run.Count - 1;
                if (to < from) to = from;
                long? delta = null;
                if (cumulativeDeltasMicros != null && k < cumulativeDeltasMicros.Count && k < run.CheckpointMicros.Count)
                {
                    delta = cumulativeDeltasMicros[k] - previous;
                    previous = cumulativeDeltasMicros[k];
                }
                c.Sectors.Add(new RouteChartSector { Index = k, FromSample = from, ToSample = to, FromMetres = c.Metres[from], ToMetres = c.Metres[to], DeltaMicros = delta });
                from = to;
            }
            return c;
        }

        static int SampleAt(GhostRecording g, float t)
        {
            int i = g.IndexAt(t);
            return i + 1 < g.Count && Math.Abs(g.T[i + 1] - t) < Math.Abs(g.T[i] - t) ? i + 1 : i;
        }

        /// <summary>One line for the results: where the most time went (and came back) against the reference.</summary>
        public string Summary(string referenceLabel)
        {
            RouteChartSector worst = WorstSector, best = BestSector;
            if (Sectors.All(s => s.DeltaMicros == null))
                return $"{LengthMetres / 1000f:F2} km · climb {MaxElevation - MinElevation:F0} m · {BrakingPoints.Count} braking points";
            string lost = worst == null ? "no sector lost time" : $"most lost in sector {worst.Index + 1} (+{worst.DeltaMicros / 1e6:F2} s, {worst.FromMetres:F0}–{worst.ToMetres:F0} m)";
            string gained = best == null ? "" : $"; most gained in sector {best.Index + 1} (−{-best.DeltaMicros / 1e6:F2} s)";
            return $"vs {referenceLabel}: {lost}{gained} · {BrakingPoints.Count} braking points";
        }
    }
}
