using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Bridges and viaducts for route sections of kind "bridge"/"viaduct" (the course's authored construction; Addendum 03
    /// T04, C09 "bridge supports"). The road surface, shoulders and barriers stay as generated; this adds what carries
    /// them: a deck girder under the road, piers down to the ground (never onto another road — a pier that would land on
    /// one is left out), and truss sides for the steel styles. Free spans over open ground get a valley carved beneath
    /// them (<see cref="Spans"/> feeds the terrain); spans that pass over another road (an overpass, the upper deck of a
    /// two-level bridge) keep the terrain the lower road needs. Piers are Barrier colliders on every profile (the server
    /// builds them too); girder and truss are visual.
    /// </summary>
    public static class BridgeGeometry
    {
        const float Step = 2f;
        const float GirderDepth = 1.5f;

        /// <summary>A stretch of route the terrain should drop away beneath (distance range and depth at mid-span).</summary>
        public struct Span
        {
            public float From, To, Depth;
            /// <summary>Terrain drop under the road at route distance <paramref name="d"/>: full over the span, easing out at the ends.</summary>
            public float DepthAt(float d)
            {
                if (d <= From || d >= To) return 0f;
                float ramp = Mathf.Min(d - From, To - d);
                return Depth * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ramp / 45f));
            }
        }

        static bool IsBridge(RouteSectionDef s) => (s.Kind == "bridge" || s.Kind == "viaduct") && s.ToMetres - s.FromMetres >= 10f;

        /// <summary>A road passes under this one (overpass / two-level bridge): the terrain must keep following that road.</summary>
        static bool OverAnotherRoad(RouteSectionDef s) => (s.Style ?? "").Contains("overpass") || (s.Style ?? "").Contains("two-level");

        /// <summary>Terrain spans for the free bridges and viaducts of a route.</summary>
        public static List<Span> Spans(RouteDefinition route)
        {
            var spans = new List<Span>();
            if (route?.Sections == null) return spans;
            foreach (RouteSectionDef s in route.Sections)
                if (IsBridge(s) && !OverAnotherRoad(s))
                    spans.Add(new Span { From = s.FromMetres, To = s.ToMetres, Depth = s.Kind == "viaduct" ? 16f : 13f });
            return spans;
        }

        public static IEnumerable<RouteSectionDef> Bridges(RouteDefinition route)
        {
            if (route?.Sections == null) yield break;
            foreach (RouteSectionDef s in route.Sections)
                if (IsBridge(s)) yield return s;
        }

        public static void Build(TrackData track, RouteDefinition route, Transform parent, CourseMaterialSet mats, TerrainCollider ground, GenerationProfile profile)
        {
            bool visuals = profile == GenerationProfile.Full;
            Transform root = null;
            int index = 0;
            foreach (RouteSectionDef sec in Bridges(route))
            {
                if (root == null)
                {
                    root = new GameObject("Bridges").transform;
                    root.SetParent(parent, false);
                }
                float from = Mathf.Clamp(sec.FromMetres, 0f, track.LengthMetres), to = Mathf.Clamp(sec.ToMetres, 0f, track.LengthMetres);
                BuildOne(track, sec, from, to, $"{track.CourseId}_B{index}", root, mats, ground, visuals);
                index++;
            }
        }

        static void BuildOne(TrackData track, RouteSectionDef sec, float from, float to, string tag, Transform root, CourseMaterialSet mats, TerrainCollider ground, bool visuals)
        {
            string style = sec.Style ?? "";
            bool steel = style.Contains("steel") || style.Contains("truss") || style.Contains("lattice") || style.Contains("span");
            Material deckMat = steel ? (style.Contains("rust") ? mats.SteelRed : style.Contains("quarry") ? mats.SteelYellow : mats.SteelGrey) : mats.Concrete;
            var girder = new MeshBuilder(1);
            var truss = new MeshBuilder(1);
            var piers = new MeshBuilder(1);
            float pierSpacing = sec.Kind == "viaduct" ? 32f : 40f;
            // Per side: the next station a pier is due; a pier that cannot stand there (on another road, too short) is
            // tried again at the following stations, so an overpass gets piers beside the road it crosses.
            float first = from + Mathf.Min(pierSpacing * 0.5f, (to - from) * 0.5f);
            var nextPier = new Dictionary<int, float> { [-1] = first, [1] = first };

            TrackSample prev = track.SampleAt(from);
            for (float d = from + Step; d <= to + 0.01f; d += Step)
            {
                TrackSample s = track.SampleAt(Mathf.Min(d, to));
                float llA = RoadGeometry.BarrierLateral(prev, -1) - 0.3f, lrA = RoadGeometry.BarrierLateral(prev, 1) + 0.3f;
                float llB = RoadGeometry.BarrierLateral(s, -1) - 0.3f, lrB = RoadGeometry.BarrierLateral(s, 1) + 0.3f;
                Vector3 a0 = prev.Position + prev.Right * llA - prev.Up * 0.12f, a1 = prev.Position + prev.Right * lrA - prev.Up * 0.12f;
                Vector3 b0 = s.Position + s.Right * llB - s.Up * 0.12f, b1 = s.Position + s.Right * lrB - s.Up * 0.12f;
                Vector3 da = -prev.Up * GirderDepth, db = -s.Up * GirderDepth;
                // Underside and the two fascias.
                Quad(girder, a0 + da, a1 + da, b1 + db, b0 + db, Vector3.down);
                Quad(girder, a0, a0 + da, b0 + db, b0, -prev.Right);
                Quad(girder, a1, a1 + da, b1 + db, b1, prev.Right);

                if (steel && visuals)
                {
                    // Truss sides outside the barriers: verticals every 6 m, top chord, alternating diagonals.
                    foreach (int side in new[] { -1, 1 })
                    {
                        float lat = RoadGeometry.BarrierLateral(s, side) + side * 0.7f;
                        Vector3 foot = s.Position + s.Right * lat - s.Up * 0.2f, top = foot + s.Up * 4.2f;
                        float latP = RoadGeometry.BarrierLateral(prev, side) + side * 0.7f;
                        Vector3 footP = prev.Position + prev.Right * latP - prev.Up * 0.2f, topP = footP + prev.Up * 4.2f;
                        Beam(truss, topP, top, 0.14f);
                        Beam(truss, footP, foot, 0.14f);
                        int station = Mathf.RoundToInt((d - from) / Step);
                        if (station % 3 == 0) Beam(truss, foot, top, 0.12f);
                        if (station % 3 == 1) Beam(truss, footP, top, 0.08f);
                    }
                }

                foreach (int side in new[] { -1, 1 })
                {
                    if (d < nextPier[side] || d >= to - 8f) continue;
                    float lat = RoadGeometry.BarrierLateral(s, side) - side * 1.2f;
                    Vector3 topAt = s.Position + s.Right * lat - s.Up * (GirderDepth + 0.1f);
                    if (!PierFoot(topAt, ground, out Vector3 foot)) continue;
                    float h = topAt.y - foot.y;
                    if (h < 1.5f) continue;
                    var centre = new Vector3(topAt.x, (topAt.y + foot.y) * 0.5f - 0.5f, topAt.z);
                    piers.AddBox(0, centre, new Vector3(0.7f, h * 0.5f + 0.5f, 0.7f), Quaternion.LookRotation(Flat(s.Tangent), Vector3.up), 0.5f);
                    nextPier[side] = d + pierSpacing;
                }
                prev = s;
            }
            if (visuals)
            {
                RoadGeometry.Emit(tag + "_Girder", root, girder.Build(tag + "_girder", true), -1, SurfaceKind.Concrete, deckMat);
                if (steel) RoadGeometry.Emit(tag + "_Truss", root, truss.Build(tag + "_truss", true), -1, SurfaceKind.Concrete, deckMat);
            }
            Mesh pierMesh = piers.Build(tag + "_piers", true);
            if (pierMesh.vertexCount > 0) RoadGeometry.Emit(tag + "_Piers", root, pierMesh, GameLayers.Barrier, SurfaceKind.Concrete, visuals ? mats.Concrete : null);
        }

        /// <summary>The ground under a pier, or false when the first thing below is another road (no pier on a carriageway).</summary>
        static bool PierFoot(Vector3 top, TerrainCollider ground, out Vector3 foot)
        {
            foot = top;
            var down = new Ray(top, Vector3.down);
            float terrainDistance = float.MaxValue;
            if (ground != null && ground.Raycast(down, out RaycastHit t, 400f)) terrainDistance = t.distance;
            if (Physics.Raycast(down, out RaycastHit hit, Mathf.Min(400f, terrainDistance + 0.5f), GameLayers.DrivableMask | GameLayers.BarrierMask, QueryTriggerInteraction.Ignore)
                && hit.collider != ground && hit.distance < terrainDistance - 0.2f)
                return false; // a road, shoulder or barrier below: leave this pier out
            if (terrainDistance == float.MaxValue) return false;
            foot = top + Vector3.down * terrainDistance;
            return true;
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        static void Beam(MeshBuilder mb, Vector3 a, Vector3 b, float half)
        {
            Vector3 d = b - a;
            if (d.sqrMagnitude < 1e-4f) return;
            mb.AddBox(0, (a + b) * 0.5f, new Vector3(half, half, d.magnitude * 0.5f), Quaternion.LookRotation(d.normalized, Mathf.Abs(Vector3.Dot(d.normalized, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up), 0.5f);
        }

        static void Quad(MeshBuilder mb, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            var scale = new Vector2(0.25f, 0.25f);
            if (Vector3.Dot(n, facing) >= 0f) mb.AddFlatQuad(0, a, b, c, d, scale);
            else mb.AddFlatQuad(0, a, d, c, b, scale);
        }
    }
}
