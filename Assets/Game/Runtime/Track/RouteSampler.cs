using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightSignal.Track
{
    /// <summary>
    /// Turns authored control points into an arc-length-uniform centreline using centripetal Catmull-Rom.
    /// Deterministic: the same route definition always yields the same samples (server, client and baker agree).
    /// </summary>
    public static class RouteSampler
    {
        const int DenseStepsPerSegment = 48;

        public static TrackSample[] Sample(RouteDefinition route, float spacingMetres = 1f)
        {
            if (route == null) throw new ArgumentNullException(nameof(route));
            List<RouteControlPoint> cps = route.ControlPoints;
            if (cps.Count < 3) throw new ArgumentException("A route needs at least three control points");

            // Dense polyline with interpolated per-point attributes.
            var dense = new List<Vector3>();
            var attr = new List<Vector4>(); // width, bank, shoulderL, shoulderR
            int segments = route.ClosedLoop ? cps.Count : cps.Count - 1;
            for (int seg = 0; seg < segments; seg++)
            {
                Vector3 p0 = Point(cps, seg - 1, route.ClosedLoop);
                Vector3 p1 = Point(cps, seg, route.ClosedLoop);
                Vector3 p2 = Point(cps, seg + 1, route.ClosedLoop);
                Vector3 p3 = Point(cps, seg + 2, route.ClosedLoop);
                RouteControlPoint a = cps[seg % cps.Count];
                RouteControlPoint b = cps[(seg + 1) % cps.Count];
                for (int k = 0; k < DenseStepsPerSegment; k++)
                {
                    float t = (float)k / DenseStepsPerSegment;
                    dense.Add(CentripetalCatmullRom(p0, p1, p2, p3, t));
                    float s = Mathf.SmoothStep(0f, 1f, t);
                    attr.Add(new Vector4(Mathf.Lerp(a.Width, b.Width, s), Mathf.Lerp(a.Bank, b.Bank, s),
                        Mathf.Lerp(a.ShoulderLeft, b.ShoulderLeft, s), Mathf.Lerp(a.ShoulderRight, b.ShoulderRight, s)));
                }
            }
            RouteControlPoint last = route.ClosedLoop ? cps[0] : cps[cps.Count - 1];
            dense.Add(last.Position);
            attr.Add(new Vector4(last.Width, last.Bank, last.ShoulderLeft, last.ShoulderRight));

            // Cumulative length.
            var cum = new float[dense.Count];
            for (int i = 1; i < dense.Count; i++)
                cum[i] = cum[i - 1] + Vector3.Distance(dense[i - 1], dense[i]);
            float total = cum[cum.Length - 1];

            // Uniform resampling.
            int count = Mathf.FloorToInt(total / spacingMetres) + 1;
            var samples = new TrackSample[count];
            int j = 0;
            for (int i = 0; i < count; i++)
            {
                float d = Mathf.Min(i * spacingMetres, total);
                while (j < cum.Length - 2 && cum[j + 1] < d) j++;
                float segLen = Mathf.Max(1e-5f, cum[j + 1] - cum[j]);
                float u = Mathf.Clamp01((d - cum[j]) / segLen);
                Vector4 at = Vector4.Lerp(attr[j], attr[j + 1], u);
                samples[i] = new TrackSample
                {
                    Position = Vector3.Lerp(dense[j], dense[j + 1], u),
                    Distance = d,
                    Width = at.x,
                    BankDeg = at.y,
                    ShoulderLeft = at.z,
                    ShoulderRight = at.w,
                };
            }

            // Frames: tangent, banked right vector, signed curvature.
            for (int i = 0; i < count; i++)
            {
                Vector3 prev = samples[Mathf.Max(0, i - 1)].Position;
                Vector3 next = samples[Mathf.Min(count - 1, i + 1)].Position;
                if (route.ClosedLoop && (i == 0 || i == count - 1))
                {
                    prev = samples[i == 0 ? count - 2 : i - 1].Position;
                    next = samples[i == count - 1 ? 1 : i + 1].Position;
                }
                Vector3 tangent = (next - prev).normalized;
                Vector3 flatRight = Vector3.Cross(Vector3.up, tangent).normalized;
                // Positive bank raises the left edge (banking into a right-hand turn).
                Vector3 right = Quaternion.AngleAxis(-samples[i].BankDeg, tangent) * flatRight;
                samples[i].Tangent = tangent;
                samples[i].Right = right.normalized;
            }
            for (int i = 0; i < count; i++)
            {
                Vector3 t0 = samples[Mathf.Max(0, i - 3)].Tangent;
                Vector3 t1 = samples[Mathf.Min(count - 1, i + 3)].Tangent;
                float ds = samples[Mathf.Min(count - 1, i + 3)].Distance - samples[Mathf.Max(0, i - 3)].Distance;
                float angle = Vector3.SignedAngle(new Vector3(t0.x, 0f, t0.z), new Vector3(t1.x, 0f, t1.z), Vector3.up) * Mathf.Deg2Rad;
                samples[i].Curvature = ds > 0f ? angle / ds : 0f;
            }
            return samples;
        }

        static Vector3 Point(List<RouteControlPoint> cps, int i, bool loop)
        {
            if (loop) return cps[((i % cps.Count) + cps.Count) % cps.Count].Position;
            if (i < 0) return cps[0].Position * 2f - cps[1].Position;
            if (i >= cps.Count) return cps[cps.Count - 1].Position * 2f - cps[cps.Count - 2].Position;
            return cps[i].Position;
        }

        /// <summary>Centripetal (alpha = 0.5) Catmull-Rom: no cusps or self-intersections within a segment.</summary>
        public static Vector3 CentripetalCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t0 = 0f;
            float t1 = t0 + Mathf.Pow(Vector3.Distance(p0, p1), 0.5f) + 1e-4f;
            float t2 = t1 + Mathf.Pow(Vector3.Distance(p1, p2), 0.5f) + 1e-4f;
            float t3 = t2 + Mathf.Pow(Vector3.Distance(p2, p3), 0.5f) + 1e-4f;
            float tt = Mathf.Lerp(t1, t2, t);
            Vector3 a1 = (t1 - tt) / (t1 - t0) * p0 + (tt - t0) / (t1 - t0) * p1;
            Vector3 a2 = (t2 - tt) / (t2 - t1) * p1 + (tt - t1) / (t2 - t1) * p2;
            Vector3 a3 = (t3 - tt) / (t3 - t2) * p2 + (tt - t2) / (t3 - t2) * p3;
            Vector3 b1 = (t2 - tt) / (t2 - t0) * a1 + (tt - t0) / (t2 - t0) * a2;
            Vector3 b2 = (t3 - tt) / (t3 - t1) * a2 + (tt - t1) / (t3 - t1) * a3;
            return (t2 - tt) / (t2 - t1) * b1 + (tt - t1) / (t2 - t1) * b2;
        }
    }
}
