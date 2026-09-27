using System.Collections.Generic;
using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Deterministic course generation from a versioned route document (decision D-007). The same route text and
    /// kit recipes always produce the same track data and geometry; clients and the server both generate it and
    /// agree through the content hash.
    /// </summary>
    public static class CourseGenerator
    {
        public static TrackData BuildTrackData(RouteDefinition route, string sourceHash)
        {
            TrackSample[] samples = RouteSampler.Sample(route, 1f);
            var t = ScriptableObject.CreateInstance<TrackData>();
            t.name = $"{route.Course}.track";
            t.hideFlags = HideFlags.DontSave;
            t.CourseId = route.Course;
            t.Revision = route.Revision;
            t.SourceHash = sourceHash;
            t.ClosedLoop = route.ClosedLoop;
            t.Laps = route.ClosedLoop ? 2 : 1;
            t.Samples = samples;
            t.LengthMetres = samples[samples.Length - 1].Distance;
            t.StartMetres = route.StartMetres;
            t.Sectors = route.Sectors;
            t.Gates = route.Gates;

            float finish = FinishMetres(t);
            var cps = new List<float>();
            if (t.ClosedLoop)
            {
                // A lap runs from the start line all the way round to it again: gates every spacing (wrapping past the
                // loop seam at distance 0), then the lap line itself. Without this a circuit had ONE gate — the start
                // line — so crossing it at GO counted a lap (found on C03 in a real network race).
                float spacing = route.CheckpointSpacingMetres;
                for (float u = spacing; u < t.LengthMetres - spacing * 0.5f; u += spacing)
                    cps.Add(Mathf.Repeat(t.StartMetres + u, t.LengthMetres));
            }
            else
                for (float d = t.StartMetres + route.CheckpointSpacingMetres; d < finish; d += route.CheckpointSpacingMetres) cps.Add(d);
            cps.Add(finish);
            t.CheckpointMetres = cps.ToArray();

            // Twelve staggered grid slots behind the start line (Addendum 01 D01): two columns, six rows, 9 m row pitch,
            // 4.5 m column stagger. The last slot sits ~55 m back, so routes need startMetres >= 62 (docs/COURSES.md).
            var grid = new GridSlot[Core.Rules.Limits.MaxRaceVehicles];
            for (int k = 0; k < grid.Length; k++)
            {
                int row = k / 2, col = k % 2;
                float d = t.StartMetres - 5f - row * 9f - col * 4.5f;
                TrackSample s = t.SampleAt(d);
                float lateral = (col == 0 ? -1f : 1f) * Mathf.Min(2.4f, s.Width * 0.25f);
                grid[k] = new GridSlot
                {
                    Position = s.Position + s.Right * lateral + s.Up * 0.6f,
                    Rotation = Quaternion.LookRotation(s.Tangent, s.Up),
                    Distance = d,
                };
            }
            t.Grid = grid;
            return t;
        }

        /// <summary>Sprints finish 20 m before the end of the paved route (room to stop); circuits at the start line.</summary>
        public static float FinishMetres(TrackData t) => t.ClosedLoop ? t.StartMetres : t.LengthMetres - 20f;

        public static void BuildGeometry(TrackData track, RouteDefinition route, Transform root, CourseMaterialSet mats,
            TerrainStyle style, GenerationProfile profile)
        {
            var plan = new LandmarkPlan();
            foreach (RouteLandmarkDef lm in route.Landmarks) LandmarkKits.Plan(track, lm, plan);

            RoadGeometry.Build(track, root, mats, plan.NoBarrier, profile);
            var pads = new List<TerrainPad>();
            if (route.Areas != null)
                foreach (RouteAreaDef a in route.Areas)
                    if (a.Size != null && a.Size.Length >= 2 && a.Centre != null && a.Centre.Length >= 3) pads.Add(AreaGeometry.PadOf(a));
            GameObject terrain = TerrainGeometry.Build(track, root, mats, style, plan.Carves, profile, pads, BridgeGeometry.Spans(route));
            AreaGeometry.Build(route, root, mats, profile);
            var ground = terrain.GetComponent<TerrainCollider>();
            Physics.SyncTransforms();
            TunnelGeometry.Build(track, route, root, mats, ground, profile);
            BridgeGeometry.Build(track, route, root, mats, ground, profile);

            var landmarks = new GameObject("Landmarks").transform;
            landmarks.SetParent(root, false);
            foreach (RouteLandmarkDef lm in route.Landmarks)
                LandmarkKits.Build(track, lm, ground, landmarks, mats, profile);

            if (profile == GenerationProfile.Full) BuildLines(track, root, mats);
        }

        static void BuildLines(TrackData track, Transform parent, CourseMaterialSet mats)
        {
            var mb = new MeshBuilder(2);
            AddLine(mb, track.SampleAt(track.StartMetres), 0.6f, false);
            AddLine(mb, track.SampleAt(FinishMetres(track)), 1.2f, true);
            RoadGeometry.Emit("StartFinishLines", parent, mb.Build($"{track.CourseId}_lines"), -1, Vehicle.SurfaceKind.Asphalt, mats.OffWhite, mats.Graphite);
        }

        static void AddLine(MeshBuilder mb, TrackSample s, float depth, bool checkered)
        {
            const int cells = 12;
            float w = s.Width;
            for (int i = 0; i < cells; i++)
            for (int j = 0; j < (checkered ? 2 : 1); j++)
            {
                int sub = checkered && (i + j) % 2 == 1 ? 1 : 0;
                float x0 = -w / 2 + i * w / cells, x1 = x0 + w / cells;
                float z0 = j * depth / 2 - depth / 2, z1 = z0 + depth / (checkered ? 2 : 1);
                Vector3 up = s.Up * 0.014f;
                mb.AddFlatQuad(sub,
                    s.Position + s.Right * x0 + s.Tangent * z0 + up,
                    s.Position + s.Right * x0 + s.Tangent * z1 + up,
                    s.Position + s.Right * x1 + s.Tangent * z1 + up,
                    s.Position + s.Right * x1 + s.Tangent * z0 + up, Vector2.one);
            }
        }
    }
}
