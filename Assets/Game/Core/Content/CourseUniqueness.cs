using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Content
{
    /// <summary>One course's result: the share of its drivable centreline not shared with its closest other course.</summary>
    public sealed class CourseUniquenessRow
    {
        public string CourseId = "";
        /// <summary>0..1: the centreline (approaches excluded) farther than the tolerance from every other counted course.</summary>
        public double Exclusive;
        /// <summary>The other course it shares most with, and how much (0..1).</summary>
        public string ClosestCourse = "";
        public double SharedWithClosest;
        public double LengthMetres;
        public bool Passes;
    }

    /// <summary>
    /// Spec §14: "At least 70% of a counted course's drivable centreline should be exclusive to that course compared with
    /// another counted course. Shared paddock approaches/short connectors are allowed … A custom validation report should flag
    /// suspicious overlap." Each counted course is compared with every other in the frame they are authored in (every course
    /// starts at its own grid and runs in its own scene, so overlap here means a near-copy of another course's shape); a
    /// point counts as shared when another course's centreline passes within <see cref="ToleranceMetres"/>; the first and last
    /// <see cref="ApproachMetres"/> are the allowed approaches and are left out. Engine-free: centrelines in, rows out.
    /// </summary>
    public static class CourseUniqueness
    {
        public const double Required = 0.70, ToleranceMetres = 8.0, ApproachMetres = 100.0, SampleMetres = 4.0;

        /// <summary>Counted courses: regular stages, the finale and the Freeplay additions — not the tutorial.</summary>
        public static bool Counted(string kind) => kind == "regular" || kind == "finale" || kind == "freeplay";

        /// <summary>Resamples a polyline (x, z) every <see cref="SampleMetres"/>, with the distance along it.</summary>
        public static List<(double X, double Z, double D)> Resample(IReadOnlyList<(double X, double Z)> line)
        {
            var o = new List<(double, double, double)>();
            double d = 0;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                (double x0, double z0) = line[i];
                (double x1, double z1) = line[i + 1];
                double seg = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                int n = Math.Max(1, (int)(seg / SampleMetres));
                for (int k = 0; k < n; k++) o.Add((x0 + (x1 - x0) * k / n, z0 + (z1 - z0) * k / n, d + seg * k / n));
                d += seg;
            }
            if (line.Count > 0) o.Add((line[line.Count - 1].X, line[line.Count - 1].Z, d));
            return o;
        }

        public static List<CourseUniquenessRow> Evaluate(IReadOnlyDictionary<string, IReadOnlyList<(double X, double Z)>> centrelines)
        {
            var sampled = centrelines.ToDictionary(kv => kv.Key, kv => Resample(kv.Value), StringComparer.Ordinal);
            // A coarse grid per course so each point checks only nearby samples of the other.
            double cell = ToleranceMetres;
            var grids = sampled.ToDictionary(kv => kv.Key, kv =>
            {
                var g = new Dictionary<(long, long), List<(double X, double Z)>>();
                foreach ((double x, double z, double _) in kv.Value)
                {
                    var key = ((long)Math.Floor(x / cell), (long)Math.Floor(z / cell));
                    if (!g.TryGetValue(key, out var list)) g[key] = list = new List<(double, double)>();
                    list.Add((x, z));
                }
                return g;
            }, StringComparer.Ordinal);

            var rows = new List<CourseUniquenessRow>();
            foreach (KeyValuePair<string, List<(double X, double Z, double D)>> a in sampled.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                double length = a.Value.Count > 0 ? a.Value[a.Value.Count - 1].D : 0;
                var counted = a.Value.Where(p => p.D >= ApproachMetres && p.D <= length - ApproachMetres).ToList();
                var row = new CourseUniquenessRow { CourseId = a.Key, LengthMetres = length, Exclusive = 1 };
                if (counted.Count > 0)
                    foreach (KeyValuePair<string, List<(double X, double Z, double D)>> b in sampled)
                    {
                        if (b.Key == a.Key) continue;
                        var g = grids[b.Key];
                        int shared = counted.Count(p => Near(g, p.X, p.Z, cell));
                        double frac = (double)shared / counted.Count;
                        if (frac > row.SharedWithClosest)
                        {
                            row.SharedWithClosest = frac;
                            row.ClosestCourse = b.Key;
                        }
                    }
                row.Exclusive = 1 - row.SharedWithClosest;
                row.Passes = row.Exclusive >= Required;
                rows.Add(row);
            }
            return rows;
        }

        static bool Near(Dictionary<(long, long), List<(double X, double Z)>> g, double x, double z, double cell)
        {
            long gx = (long)Math.Floor(x / cell), gz = (long)Math.Floor(z / cell);
            double r2 = ToleranceMetres * ToleranceMetres;
            for (long dx = -1; dx <= 1; dx++)
                for (long dz = -1; dz <= 1; dz++)
                    if (g.TryGetValue((gx + dx, gz + dz), out var list))
                        foreach ((double bx, double bz) in list)
                            if ((bx - x) * (bx - x) + (bz - z) * (bz - z) <= r2) return true;
            return false;
        }
    }
}
