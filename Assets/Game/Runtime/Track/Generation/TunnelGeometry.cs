using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Tunnels and galleries for route sections of kind "tunnel" (the course's authored construction; Addendum 03 C03/C09,
    /// T04). Built as self-contained structures over the existing road so the heightmap terrain is untouched: a lining
    /// (walls to the springline, then an arch or a flat roof) outside the road's own barriers, portals, a rock mound over
    /// enclosed tunnels, and lights along the roof. Galleries (avalanche/snow sheds) keep the valley side open between
    /// pillars. Walls are Barrier colliders on every profile (the server builds them too, so contact agrees); the roof is a
    /// Scenery collider the driving camera respects; visuals and lights only on the Full profile.
    /// </summary>
    public static class TunnelGeometry
    {
        const float Step = 2f;
        const float WallOutset = 0.7f;   // beyond the barrier line
        const float Springline = 4.6f;
        const float Crown = 6.6f;
        const float FlatRoof = 5.6f;

        enum Shape { Arch, Box, Gallery }

        sealed class Style
        {
            public Shape Shape;
            public Color Light;
            public float LightSpacing;
            public bool Rock; // rock lining + mound; otherwise concrete/stone
        }

        static Style StyleFor(string id)
        {
            id = id ?? "";
            if (id.Contains("gallery") || id.Contains("shed"))
                return new Style { Shape = Shape.Gallery, Light = new Color(1f, 0.93f, 0.8f), LightSpacing = 24f };
            if (id.Contains("service"))
                return new Style { Shape = Shape.Box, Light = new Color(1f, 0.74f, 0.42f), LightSpacing = 18f };
            if (id.Contains("blue"))
                return new Style { Shape = Shape.Arch, Light = new Color(0.72f, 0.85f, 1f), LightSpacing = 20f, Rock = true };
            if (id.Contains("rock"))
                return new Style { Shape = Shape.Arch, Light = new Color(1f, 0.74f, 0.42f), LightSpacing = 20f, Rock = true };
            return new Style { Shape = Shape.Arch, Light = new Color(0.95f, 0.95f, 0.9f), LightSpacing = 20f, Rock = true };
        }

        /// <summary>The route's tunnel sections, for other systems (spectator cameras, recovery tests).</summary>
        public static IEnumerable<RouteSectionDef> Tunnels(RouteDefinition route)
        {
            if (route?.Sections == null) yield break;
            foreach (RouteSectionDef s in route.Sections)
                if (s.Kind == "tunnel" && s.ToMetres - s.FromMetres >= 10f) yield return s;
        }

        public static void Build(TrackData track, RouteDefinition route, Transform parent, CourseMaterialSet mats, TerrainCollider ground, GenerationProfile profile)
        {
            bool visuals = profile == GenerationProfile.Full;
            Transform root = null;
            int index = 0;
            foreach (RouteSectionDef sec in Tunnels(route))
            {
                if (root == null)
                {
                    root = new GameObject("Tunnels").transform;
                    root.SetParent(parent, false);
                }
                float from = Mathf.Clamp(sec.FromMetres, 0f, track.LengthMetres), to = Mathf.Clamp(sec.ToMetres, 0f, track.LengthMetres);
                BuildOne(track, from, to, StyleFor(sec.Style), $"{track.CourseId}_T{index}", root, mats, ground, visuals);
                index++;
            }
        }

        static void BuildOne(TrackData track, float from, float to, Style style, string tag, Transform root, CourseMaterialSet mats, TerrainCollider ground, bool visuals)
        {
            var stations = new List<TrackSample>();
            for (float d = from; d < to; d += Step) stations.Add(track.SampleAt(d));
            stations.Add(track.SampleAt(to));

            // Which side the mountain is on (galleries open toward the valley): compare the terrain a little beyond each wall.
            int mountain = 1;
            if (style.Shape == Shape.Gallery)
            {
                float left = 0f, right = 0f;
                foreach (TrackSample s in stations)
                {
                    left += TerrainY(ground, s.Position + s.Right * (RoadGeometry.BarrierLateral(s, -1) - 8f), s.Position.y);
                    right += TerrainY(ground, s.Position + s.Right * (RoadGeometry.BarrierLateral(s, 1) + 8f), s.Position.y);
                }
                mountain = right >= left ? 1 : -1;
            }

            var lining = new MeshBuilder(2);    // 0 lining, 1 portal frame
            var walls = new MeshBuilder(1);     // Barrier collider
            var roof = new MeshBuilder(1);      // Scenery collider (camera)
            var mound = new MeshBuilder(1);
            var fixtures = new MeshBuilder(1);
            var lightPoints = new List<Vector3>();
            float nextLight = from + style.LightSpacing * 0.5f;

            for (int i = 0; i < stations.Count - 1; i++)
            {
                TrackSample a = stations[i], b = stations[i + 1];
                Vector3[] pa = Profile(a, style, mountain), pb = Profile(b, style, mountain);
                Vector3 axisA = a.Position + a.Up * (Springline * 0.6f), axisB = b.Position + b.Up * (Springline * 0.6f);
                for (int k = 0; k < pa.Length - 1; k++)
                {
                    if (style.Shape == Shape.Gallery && IsGalleryGap(k, pa.Length, mountain)) continue;
                    Vector3 mid = (pa[k] + pa[k + 1] + pb[k] + pb[k + 1]) * 0.25f;
                    Vector3 inward = ((axisA + axisB) * 0.5f - mid).normalized;
                    Quad(lining, 0, pa[k], pb[k], pb[k + 1], pa[k + 1], inward, 0.25f);
                    bool wall = IsWall(k, pa.Length, style.Shape, mountain);
                    MeshBuilder col = wall ? walls : roof;
                    Quad(col, 0, pa[k], pb[k], pb[k + 1], pa[k + 1], inward, 1f);
                    Quad(col, 0, pa[k], pb[k], pb[k + 1], pa[k + 1], -inward, 1f); // both faces for queries from either side
                }
                if (visuals) Mound(mound, a, b, style, mountain, ground);
                if (style.Shape == Shape.Gallery && i % 3 == 0)
                {
                    // Pillars along the open valley side.
                    float lat = RoadGeometry.BarrierLateral(a, -mountain) - mountain * WallOutset; // just outside the valley-side barrier
                    Vector3 foot = a.Position + a.Right * lat;
                    Quaternion rot = Quaternion.LookRotation(a.Tangent, a.Up);
                    Vector3 centre = foot + a.Up * (FlatRoof * 0.5f - 0.2f);
                    lining.AddBox(0, centre, new Vector3(0.28f, FlatRoof * 0.5f + 0.2f, 0.28f), rot, 0.5f);
                    walls.AddBox(0, centre, new Vector3(0.28f, FlatRoof * 0.5f + 0.2f, 0.28f), rot);
                }
                if (a.Distance >= nextLight)
                {
                    nextLight += style.LightSpacing;
                    float roofY = style.Shape == Shape.Arch ? Crown - 0.35f : FlatRoof - 0.3f;
                    Vector3 p = a.Position + a.Up * roofY + a.Right * CentreLateral(a);
                    lightPoints.Add(p);
                    fixtures.AddBox(0, p + a.Up * 0.08f, new Vector3(0.5f, 0.06f, 0.18f), Quaternion.LookRotation(a.Tangent, a.Up));
                }
            }
            Portal(lining, stations[0], style, mountain, -1);
            Portal(lining, stations[stations.Count - 1], style, mountain, 1);
            if (visuals)
            {
                PortalFace(mound, stations[0], style, mountain, -1, ground);
                PortalFace(mound, stations[stations.Count - 1], style, mountain, 1, ground);
            }

            Material liningMat = style.Rock ? mats.TunnelLining : style.Shape == Shape.Gallery ? mats.Stone : mats.Concrete;
            if (visuals)
            {
                RoadGeometry.Emit(tag + "_Lining", root, lining.Build(tag + "_lining", true), -1, SurfaceKind.Concrete, liningMat, mats.Concrete);
                RoadGeometry.Emit(tag + "_Mound", root, mound.Build(tag + "_mound", true), -1, SurfaceKind.Concrete, mats.TunnelLining);
                RoadGeometry.Emit(tag + "_Lamps", root, fixtures.Build(tag + "_lamps"), -1, SurfaceKind.Concrete, mats.WindowLit);
                foreach (Vector3 p in lightPoints)
                {
                    var l = new GameObject("TunnelLight").AddComponent<Light>();
                    l.transform.SetParent(root, false);
                    l.transform.position = p - Vector3.up * 0.3f;
                    l.type = LightType.Point;
                    l.range = style.LightSpacing * 0.95f;
                    l.intensity = 2.2f;
                    l.color = style.Light;
                    l.shadows = LightShadows.None;
                }
            }
            RoadGeometry.Emit(tag + "_WallCollider", root, walls.Build(tag + "_walls"), GameLayers.Barrier, SurfaceKind.Concrete, null);
            RoadGeometry.Emit(tag + "_RoofCollider", root, roof.Build(tag + "_roof"), GameLayers.Scenery, SurfaceKind.Concrete, null);
        }

        /// <summary>
        /// The rock face around a portal: the region between the concrete ring and the mound's end outline, as a strip of
        /// quads between the two outlines resampled to the same count (both run left to right), facing out along the road.
        /// </summary>
        static void PortalFace(MeshBuilder mb, TrackSample s, Style style, int mountain, int end, TerrainCollider ground)
        {
            Vector3[] profile = Profile(s, style, mountain);
            Vector3 centre = s.Position + s.Up * (Springline * 0.6f) + s.Right * CentreLateral(s);
            var inner = new List<Vector3>();
            int first = 0, last = profile.Length - 1;
            if (style.Shape == Shape.Gallery) { if (mountain > 0) first = 1; else last = profile.Length - 2; }
            for (int i = first; i <= last; i++) inner.Add(profile[i] + (profile[i] - centre).normalized * 0.9f);
            Vector3[] outer = MoundOutline(s, style, mountain, ground);
            const int n = 24;
            Vector3[] a = Resample(inner, n), b = Resample(new List<Vector3>(outer), n);
            Vector3 outDir = s.Tangent * end;
            for (int k = 0; k < n - 1; k++)
                Quad(mb, 0, a[k] + outDir * 0.3f, a[k + 1] + outDir * 0.3f, b[k + 1], b[k], outDir, 0.2f);
        }

        /// <summary>Points at equal arc-length spacing along a polyline.</summary>
        static Vector3[] Resample(List<Vector3> line, int count)
        {
            var cumulative = new float[line.Count];
            for (int i = 1; i < line.Count; i++) cumulative[i] = cumulative[i - 1] + Vector3.Distance(line[i - 1], line[i]);
            float total = cumulative[line.Count - 1];
            var result = new Vector3[count];
            int seg = 0;
            for (int k = 0; k < count; k++)
            {
                float target = total * k / (count - 1);
                while (seg < line.Count - 2 && cumulative[seg + 1] < target) seg++;
                float len = cumulative[seg + 1] - cumulative[seg];
                result[k] = Vector3.Lerp(line[seg], line[seg + 1], len > 1e-5f ? (target - cumulative[seg]) / len : 0f);
            }
            return result;
        }

        static float CentreLateral(TrackSample s) => (RoadGeometry.BarrierLateral(s, -1) + RoadGeometry.BarrierLateral(s, 1)) * 0.5f;

        /// <summary>
        /// Inside outline of the lining at a station, left wall foot → roof → right wall foot (world space). Arch: walls to
        /// the springline and an elliptical vault to the crown; box and gallery: walls to a flat roof.
        /// </summary>
        static Vector3[] Profile(TrackSample s, Style style, int mountain)
        {
            float ll = RoadGeometry.BarrierLateral(s, -1) - WallOutset, lr = RoadGeometry.BarrierLateral(s, 1) + WallOutset;
            var pts = new List<Vector2> { new Vector2(ll, -0.4f), new Vector2(ll, style.Shape == Shape.Arch ? Springline : FlatRoof) };
            if (style.Shape == Shape.Arch)
            {
                float cx = (ll + lr) * 0.5f, a = (lr - ll) * 0.5f;
                for (int k = 1; k < 9; k++)
                {
                    float t = Mathf.PI - k * Mathf.PI / 9f;
                    pts.Add(new Vector2(cx + a * Mathf.Cos(t), Springline + (Crown - Springline) * Mathf.Sin(t)));
                }
                pts.Add(new Vector2(lr, Springline));
            }
            else
            {
                pts.Add(new Vector2((ll + lr) * 0.5f, FlatRoof));
                pts.Add(new Vector2(lr, FlatRoof));
            }
            pts.Add(new Vector2(lr, -0.4f));
            var world = new Vector3[pts.Count];
            for (int i = 0; i < pts.Count; i++) world[i] = s.Position + s.Right * pts[i].x + s.Up * pts[i].y;
            return world;
        }

        /// <summary>Segment k runs from outline point k to k+1: the first and last are the walls.</summary>
        static bool IsWall(int k, int count, Shape shape, int mountain) => k == 0 || k == count - 2;

        /// <summary>A gallery has no wall on its valley side (pillars instead).</summary>
        static bool IsGalleryGap(int k, int count, int mountain) => mountain > 0 ? k == 0 : k == count - 2;

        static void Portal(MeshBuilder mb, TrackSample s, Style style, int mountain, int end)
        {
            // A concrete ring around the opening, facing out along the road, 0.6 m proud of the rock.
            Vector3[] inner = Profile(s, style, mountain);
            Vector3 outDir = s.Tangent * end;
            for (int k = 0; k < inner.Length - 1; k++)
            {
                if (style.Shape == Shape.Gallery && IsGalleryGap(k, inner.Length, mountain)) continue;
                Vector3 centre = s.Position + s.Up * (Springline * 0.6f) + s.Right * CentreLateral(s);
                Vector3 o0 = inner[k] + (inner[k] - centre).normalized * 0.9f, o1 = inner[k + 1] + (inner[k + 1] - centre).normalized * 0.9f;
                Quad(mb, 1, inner[k] + outDir * 0.6f, inner[k + 1] + outDir * 0.6f, o1 + outDir * 0.6f, o0 + outDir * 0.6f, outDir, 0.5f);
                Quad(mb, 1, inner[k], inner[k + 1], inner[k + 1] + outDir * 0.6f, inner[k] + outDir * 0.6f, (centre - (inner[k] + inner[k + 1]) * 0.5f).normalized, 0.5f);
            }
        }

        /// <summary>
        /// Rock over the tunnel: a ridge from the terrain on each side up over the roof (galleries: only on the mountain side,
        /// covering the roof slab). Its feet follow the terrain so it neither floats nor ends in a slot.
        /// </summary>
        static void Mound(MeshBuilder mb, TrackSample a, TrackSample b, Style style, int mountain, TerrainCollider ground)
        {
            Vector3[] ra = MoundOutline(a, style, mountain, ground), rb = MoundOutline(b, style, mountain, ground);
            for (int k = 0; k < ra.Length - 1; k++)
            {
                Vector3 mid = (ra[k] + ra[k + 1] + rb[k] + rb[k + 1]) * 0.25f;
                Vector3 up = (mid - (a.Position + a.Up * Springline)).normalized;
                Quad(mb, 0, ra[k], rb[k], rb[k + 1], ra[k + 1], up, 0.2f);
            }
        }

        static Vector3[] MoundOutline(TrackSample s, Style style, int mountain, TerrainCollider ground)
        {
            float ll = RoadGeometry.BarrierLateral(s, -1) - WallOutset, lr = RoadGeometry.BarrierLateral(s, 1) + WallOutset;
            float top = style.Shape == Shape.Arch ? Crown : FlatRoof;
            var lateral = new List<Vector2>();
            if (style.Shape == Shape.Gallery)
            {
                // Roof slab edge on the valley side, then up the mountain side to the slope.
                float valley = mountain > 0 ? ll - 0.8f : lr + 0.8f, hill = mountain > 0 ? lr : ll;
                lateral.Add(new Vector2(valley, top - 0.1f));
                lateral.Add(new Vector2(valley, top + 0.7f));
                lateral.Add(new Vector2((valley + hill) * 0.5f, top + 1.1f));
                lateral.Add(new Vector2(hill + mountain * 2f, top + 1.8f));
                lateral.Add(new Vector2(hill + mountain * 12f, float.NaN));
                if (mountain < 0) lateral.Reverse();
            }
            else
            {
                lateral.Add(new Vector2(ll - 14f, float.NaN));
                lateral.Add(new Vector2(ll - 3f, top + 1.2f));
                lateral.Add(new Vector2((ll + lr) * 0.5f, top + 3f));
                lateral.Add(new Vector2(lr + 3f, top + 1.2f));
                lateral.Add(new Vector2(lr + 14f, float.NaN));
            }
            var world = new Vector3[lateral.Count];
            for (int i = 0; i < lateral.Count; i++)
            {
                Vector3 p = s.Position + s.Right * lateral[i].x;
                if (float.IsNaN(lateral[i].y))
                    world[i] = new Vector3(p.x, TerrainY(ground, p, s.Position.y - 6f) - 0.6f, p.z); // bury the foot in the terrain
                else
                    world[i] = p + s.Up * lateral[i].y;
            }
            return world;
        }

        static float TerrainY(TerrainCollider ground, Vector3 at, float fallback)
        {
            if (ground == null) return fallback;
            var ray = new Ray(new Vector3(at.x, at.y + 400f, at.z), Vector3.down);
            return ground.Raycast(ray, out RaycastHit hit, 1000f) ? hit.point.y : fallback;
        }

        /// <summary>A flat quad whose visible side faces <paramref name="facing"/>.</summary>
        static void Quad(MeshBuilder mb, int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing, float uv)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            var scale = new Vector2(uv, uv);
            if (Vector3.Dot(n, facing) >= 0f) mb.AddFlatQuad(sub, a, b, c, d, scale);
            else mb.AddFlatQuad(sub, a, d, c, b, scale);
        }
    }
}
