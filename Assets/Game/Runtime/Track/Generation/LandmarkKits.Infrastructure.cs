using System.Collections.Generic;
using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Things built along or across the road: crossings over it (kit <c>crossing</c>), walls beside it (<c>wall</c>), railways
    /// and lifts (<c>rail</c>) and signs (<c>sign</c>). Everything stays outside the barriers; anything over the road clears
    /// its highest point by <see cref="OverheadClearance"/>. Crossings and galleries that coincide with a route section of
    /// the same kind are already drawn by the bridge/tunnel builders and are not doubled.
    /// </summary>
    public static partial class LandmarkKits
    {
        /// <summary>Free height kept above the road under anything that crosses it (camera included).</summary>
        public const float OverheadClearance = 6.5f;

        /// <summary>The road's cross-section at a station: its highest point and the two barrier lines (world).</summary>
        static void RoadAcross(TrackData track, float metres, out TrackSample s, out Vector3 across, out float top, out Vector3 left, out Vector3 right)
        {
            s = track.SampleAt(metres);
            across = new Vector3(s.Right.x, 0f, s.Right.z).normalized;
            left = s.Position + s.Right * RoadGeometry.BarrierLateral(s, -1);
            right = s.Position + s.Right * RoadGeometry.BarrierLateral(s, 1);
            top = Mathf.Max(s.Position.y, Mathf.Max(left.y, right.y)) + 1.2f;
            for (float d = -12f; d <= 12f; d += 4f) top = Mathf.Max(top, track.SampleAt(metres + d).Position.y + 1.2f);
        }

        // ------------------------------------------------------------------ crossings

        static GameObject Crossing(TrackData track, RouteDefinition route, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            RouteSectionDef sec = SectionAt(route, lm.AtMetres);
            if (sec != null && (sec.Kind == "bridge" || sec.Kind == "viaduct")) return null; // the road is on this bridge: BridgeGeometry draws it
            string type = TypeOf(lm);
            RoadAcross(track, lm.AtMetres, out TrackSample s, out Vector3 across, out float roadTop, out Vector3 bl, out Vector3 br);
            float span = Mathf.Max(P(lm, "span", 24f), (br - bl).magnitude + 6f);
            Vector3 mid = new Vector3((bl.x + br.x) * 0.5f, 0f, (bl.z + br.z) * 0.5f);
            Vector3 along = new Vector3(s.Tangent.x, 0f, s.Tangent.z).normalized;
            // Ends: the span centred on the road, at least 3 m beyond each barrier.
            float halfSpan = span * 0.5f;
            Vector3 endL = mid - across * halfSpan, endR = mid + across * halfSpan;
            float gL = Ground(ground, endL, s.Position.y), gR = Ground(ground, endR, s.Position.y);
            float deck = roadTop + OverheadClearance;
            M steel = SteelOf(S(lm, "colour", type == "maintenance-bridge" ? "yellow" : type == "covered-footbridge" ? "red" : "grey"));
            var mb = Kit();
            Vector3 D(Vector3 end, float y) => new Vector3(end.x, y, end.z);
            // Supports: two legs at each end, just outside the span's ends (never over the road).
            void Legs(float width, M m, float thick)
            {
                foreach (Vector3 e in new[] { endL, endR })
                {
                    float g = e == endL ? gL : gR;
                    foreach (float w in new[] { -width, width })
                        mb.AddBox((int)m, D(e, (g - 2f + deck) * 0.5f) + along * w, new Vector3(thick, (deck - g + 2f) * 0.5f, thick), Quaternion.LookRotation(across), 0.4f);
                }
            }
            Quaternion rAcross = Quaternion.LookRotation(across, Vector3.up); // local z across the road
            switch (type)
            {
                case "overpass":
                {
                    // An aqueduct (by name) is a stone channel carrying water; otherwise a concrete road overpass.
                    bool aqueduct = (lm.Id ?? "").Contains("AQUEDUCT") || (lm.Name ?? "").ToLowerInvariant().Contains("aqueduct");
                    if (aqueduct)
                    {
                        Legs(2.2f, M.Stone, 1.4f);
                        mb.AddBox((int)M.Stone, D(mid, deck + 1f), new Vector3(3f, 1f, halfSpan), rAcross, 0.3f);
                        foreach (float w in new[] { -2.6f, 2.6f })
                            mb.AddBox((int)M.Stone, D(mid, deck + 2.6f) + along * w, new Vector3(0.4f, 0.6f, halfSpan), rAcross, 0.3f);
                        mb.AddBox((int)M.Water, D(mid, deck + 2.7f), new Vector3(2.2f, 0.02f, halfSpan), rAcross, 0.2f);
                        // Arch rings between the legs on both faces.
                        const int segs = 10;
                        foreach (float w in new[] { -3.05f, 3.05f })
                            for (int i = 0; i < segs; i++)
                            {
                                float t0 = i / (float)segs, t1 = (i + 1) / (float)segs;
                                float y0 = deck + (1f - Mathf.Sin(t0 * Mathf.PI)) * -2.5f;
                                float y1 = deck + (1f - Mathf.Sin(t1 * Mathf.PI)) * -2.5f;
                                Beam(mb, M.Stone, D(Vector3.Lerp(endL, endR, t0), y0) + along * w, D(Vector3.Lerp(endL, endR, t1), y1) + along * w, 0.35f);
                            }
                        break;
                    }
                    Legs(3.5f, M.Concrete, 0.8f);
                    mb.AddBox((int)M.Concrete, D(mid, deck + 0.6f), new Vector3(5.5f, 0.6f, halfSpan), rAcross, 0.3f);
                    mb.AddBox((int)M.Graphite, D(mid, deck + 1.22f), new Vector3(5f, 0.02f, halfSpan), rAcross, 0.3f);
                    foreach (float w in new[] { -5.3f, 5.3f })
                        mb.AddBox((int)M.Concrete, D(mid, deck + 1.75f) + along * w, new Vector3(0.2f, 0.55f, halfSpan), rAcross, 0.3f);
                    mb.AddBox((int)M.OffWhite, D(mid, deck + 0.6f) + along * 5.52f, new Vector3(0.02f, 0.12f, halfSpan * 0.95f), rAcross, 0.3f);
                    break;
                }
                case "covered-footbridge":
                {
                    // An enclosed walkway on steel frames, stair towers at both ends.
                    Legs(1.4f, steel, 0.3f);
                    mb.AddBox((int)steel, D(mid, deck + 0.25f), new Vector3(1.6f, 0.25f, halfSpan), rAcross, 0.3f);
                    mb.AddBox((int)M.MetalRoof, D(mid, deck + 3.2f), new Vector3(1.8f, 0.12f, halfSpan + 0.3f), rAcross, 0.3f);
                    int frames = Mathf.Max(4, Mathf.RoundToInt(span / 2.5f));
                    for (int i = 0; i <= frames; i++)
                    {
                        Vector3 c = D(Vector3.Lerp(endL, endR, i / (float)frames), deck);
                        foreach (float w in new[] { -1.55f, 1.55f })
                            mb.AddBox((int)steel, c + along * w + Vector3.up * 1.7f, new Vector3(0.08f, 1.45f, 0.08f), rAcross, 0.5f);
                    }
                    foreach (float w in new[] { -1.55f, 1.55f })
                        mb.AddBox((int)M.Graphite, D(mid, deck + 2f) + along * w, new Vector3(0.03f, 0.8f, halfSpan), rAcross, 0.3f);
                    foreach (Vector3 e in new[] { endL, endR })
                    {
                        float g = e == endL ? gL : gR;
                        Vector3 outward = (e - mid).normalized;
                        mb.AddBox((int)steel, D(e + outward * 2.2f, (g + deck + 3.2f) * 0.5f), new Vector3(2f, (deck + 3.2f - g) * 0.5f, 2f), rAcross, 0.3f);
                        Windows(mb, D(e + outward * 2.2f, g), rAcross, 4f, 4f, 0f, deck + 3f - g, 1.5f, false, 0.7f, 1f);
                    }
                    break;
                }
                case "maintenance-bridge":
                {
                    // A gantry: two lattice legs and a truss beam carrying a railed walkway.
                    foreach (Vector3 e in new[] { endL, endR })
                        Lattice(mb, steel, D(e, e == endL ? gL : gR), rAcross, 0.7f, 0.7f, deck - (e == endL ? gL : gR) + 1.8f, 0.08f);
                    TrussBeam(mb, steel, D(endL, deck), D(endR, deck), 1.8f, 0.9f);
                    mb.AddBox((int)M.Graphite, D(mid, deck - 0.1f), new Vector3(0.9f, 0.05f, halfSpan), rAcross, 0.3f);
                    foreach (float w in new[] { -0.9f, 0.9f })
                        mb.AddBox((int)steel, D(mid, deck + 1f) + along * w, new Vector3(0.03f, 0.03f, halfSpan), rAcross, 0.3f);
                    break;
                }
                case "pipeline-arch":
                {
                    // Two pipes crossing on an arched steel support.
                    float rise = Mathf.Min(span * 0.2f, 6f);
                    const int segs = 12;
                    Vector3 prev = D(endL, gL);
                    for (int i = 1; i <= segs; i++)
                    {
                        float t = i / (float)segs;
                        float gy = Mathf.Lerp(gL, gR, t);
                        float y = Mathf.Lerp(gy, deck + rise * 0.3f, Mathf.Sin(t * Mathf.PI));
                        Vector3 p = D(Vector3.Lerp(endL, endR, t), Mathf.Max(y, t > 0.15f && t < 0.85f ? deck - 0.6f : y));
                        foreach (float w in new[] { -1.4f, 1.4f }) Beam(mb, M.SteelGrey, prev + along * w, p + along * w, 0.18f);
                        prev = p;
                    }
                    foreach (float w in new[] { -0.7f, 0.7f })
                    {
                        Vector3 a = D(endL, deck + 0.8f) + along * w - across * 20f, b = D(endR, deck + 0.8f) + along * w + across * 20f;
                        Beam(mb, M.SteelGrey, a, b, 0.55f);
                        for (int i = 0; i <= 8; i++) // flanges
                            mb.AddBox((int)M.Graphite, Vector3.Lerp(a, b, i / 8f), new Vector3(0.62f, 0.62f, 0.08f), rAcross, 0.5f);
                    }
                    break;
                }
                case "rail-trestle":
                {
                    // A railway over the road: a plate-girder deck with rails and sleepers on steel trestle bents.
                    foreach (Vector3 e in new[] { endL, endR })
                        Lattice(mb, M.SteelGrey, D(e, e == endL ? gL : gR), rAcross, 1.6f, 1.1f, deck - (e == endL ? gL : gR), 0.12f);
                    foreach (float w in new[] { -1.6f, 1.6f })
                        mb.AddBox((int)M.SteelGrey, D(mid, deck + 0.7f) + along * w, new Vector3(0.12f, 0.9f, halfSpan + 1.5f), rAcross, 0.3f);
                    int sleepers = Mathf.RoundToInt(span / 0.6f);
                    for (int i = 0; i <= sleepers; i++)
                        mb.AddBox((int)M.WoodDark, D(Vector3.Lerp(endL, endR, i / (float)sleepers), deck + 1.6f), new Vector3(1.4f, 0.08f, 0.12f), Quaternion.LookRotation(along) * Quaternion.Euler(0f, 90f, 0f), 0.5f);
                    foreach (float w in new[] { -0.72f, 0.72f })
                        mb.AddBox((int)M.SteelGrey, D(mid, deck + 1.75f) + along * w, new Vector3(0.04f, 0.07f, halfSpan + 1.5f), rAcross, 0.5f);
                    break;
                }
                case "relay-arch":
                {
                    // A steel arch over the road carrying a banner board.
                    const int segs = 16;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vector3 prev = D(endL, gL) + along * (side * 0.4f);
                        for (int i = 1; i <= segs; i++)
                        {
                            float t = i / (float)segs;
                            float y = Mathf.Lerp(Mathf.Lerp(gL, gR, t), deck + 2.5f, Mathf.Sin(t * Mathf.PI));
                            Vector3 p = D(Vector3.Lerp(endL, endR, t), y) + along * (side * 0.4f);
                            Beam(mb, steel == M.SteelGrey ? M.SteelRed : steel, prev, p, 0.25f);
                            prev = p;
                        }
                    }
                    mb.AddBox((int)M.OffWhite, D(mid, deck + 1.4f), new Vector3(0.1f, 0.8f, Mathf.Min(halfSpan * 0.6f, 7f)), rAcross, 0.3f);
                    mb.AddBox((int)M.Graphite, D(mid, deck + 1.4f) + along * 0.12f, new Vector3(0.02f, 0.3f, Mathf.Min(halfSpan * 0.45f, 5f)), rAcross, 0.3f);
                    break;
                }
                default: // steel-truss, lattice, two-level (off-section): a through-truss carrying a road over ours
                {
                    Legs(4f, M.Concrete, 1f);
                    mb.AddBox((int)M.Concrete, D(mid, deck + 0.5f), new Vector3(4.5f, 0.5f, halfSpan), rAcross, 0.3f);
                    foreach (float w in new[] { -4.6f, 4.6f })
                        TrussBeam(mb, steel, D(endL, deck + 1f) + along * w, D(endR, deck + 1f) + along * w, 5f, 0.25f, true);
                    int ties = Mathf.Max(3, Mathf.RoundToInt(span / 8f));
                    for (int i = 0; i <= ties; i++)
                    {
                        Vector3 c = D(Vector3.Lerp(endL, endR, i / (float)ties), deck + 6f);
                        Beam(mb, steel, c - along * 4.6f, c + along * 4.6f, 0.18f);
                    }
                    break;
                }
            }
            return Emit(lm.Name, mb, track, lm, "", mats, parent, Vector3.zero, Quaternion.identity, true, GameLayers.Scenery, profile);
        }

        /// <summary>How far a straight line (a conveyor, a cable) must rise to clear any road it passes over.</summary>
        static float ClearanceLift(TrackData track, Vector3 a, Vector3 b)
        {
            float lift = 0f;
            for (int i = 0; i <= 40; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, i / 40f);
                if (OverRoad(track, p, 2f, out float ry)) lift = Mathf.Max(lift, ry + OverheadClearance + 0.5f - p.y);
            }
            return lift;
        }

        /// <summary>A Warren truss between two points: chords <paramref name="depth"/> apart and diagonals.</summary>
        static void TrussBeam(MeshBuilder mb, M sub, Vector3 a, Vector3 b, float depth, float halfWidth, bool vertical = true)
        {
            int panels = Mathf.Max(3, Mathf.RoundToInt((b - a).magnitude / Mathf.Max(1.5f, depth)));
            Vector3 up = Vector3.up * depth;
            float member = Mathf.Max(0.06f, depth * 0.05f);
            Beam(mb, sub, a, b, member * 1.5f);
            Beam(mb, sub, a + up, b + up, member * 1.5f);
            for (int i = 0; i < panels; i++)
            {
                Vector3 p0 = Vector3.Lerp(a, b, i / (float)panels), p1 = Vector3.Lerp(a, b, (i + 1) / (float)panels);
                if (i % 2 == 0) Beam(mb, sub, p0, p1 + up, member);
                else Beam(mb, sub, p0 + up, p1, member);
                if (vertical) Beam(mb, sub, p0, p0 + up, member * 0.8f);
            }
            Beam(mb, sub, b, b + up, member);
        }

        // ------------------------------------------------------------------ walls

        static GameObject Wall(TrackData track, RouteDefinition route, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            string type = TypeOf(lm);
            if (type == "avalanche-gallery")
            {
                RouteSectionDef sec = SectionAt(route, lm.AtMetres);
                if (sec != null && sec.Kind == "tunnel") return null; // the gallery tunnel section already draws it
            }
            float length = P(lm, "length", 100f), height = P(lm, "height", 5f);
            int side = lm.Side == "left" ? -1 : 1;
            TrackSample s0 = track.SampleAt(lm.AtMetres);
            float lateral = Mathf.Max(1.6f, lm.OffsetMetres - Mathf.Abs(RoadGeometry.BarrierLateral(s0, side)));
            var mb = Kit();
            switch (type)
            {
                case "stone-stair": StoneStair(mb, track, ground, lm, side, lateral, length, height); break;
                case "quarry-steps": QuarrySteps(mb, track, ground, lm, side, lateral, length, height); break;
                default: RoadsideWall(mb, track, ground, lm, type, side, lateral, length, height); break;
            }
            return Emit(lm.Name, mb, track, lm, "", mats, parent, Vector3.zero, Quaternion.identity, lateral < 12f, GameLayers.Scenery, profile);
        }

        /// <summary>A wall following the road at a fixed distance outside the barrier, in segments seated on the ground.</summary>
        static void RoadsideWall(MeshBuilder mb, TrackData track, TerrainCollider ground, RouteLandmarkDef lm, string type, int side, float lateral, float length, float height)
        {
            float step = type == "snow-fence" ? 3f : 4f;
            int lamps = Mathf.RoundToInt(P(lm, "lamps", 0f));
            float lampEvery = lamps > 0 ? length / lamps : float.MaxValue, nextLamp = lampEvery * 0.5f;
            bool tiers = type == "split-retaining";
            var rng = Rng(track, lm);
            // Authored face material: a rock cutting in stone (red-oxide stone reads as brick-red), else cast concrete.
            string material = S(lm, "material", "concrete");
            M face = S(lm, "colour", "") == "red-oxide" ? M.Brick : WallMat(material);
            bool joints = face == M.Concrete;
            for (float t = 0f; t < length; t += step)
            {
                float d0 = lm.AtMetres - length * 0.5f + t, d1 = d0 + step;
                Vector3 a = Beside(track, ground, d0, side, lateral), b = Beside(track, ground, d1, side, lateral);
                Vector3 dir = b - a;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) continue;
                if (OverRoad(track, a + Vector3.Cross(Vector3.up, dir.normalized) * side * 3.5f, 1f, out _)) continue; // a hairpin's other leg
                Quaternion r = Quaternion.LookRotation(dir.normalized, Vector3.up);
                Vector3 outward = Vector3.Cross(Vector3.up, dir.normalized) * side; // away from the road
                float baseY = Mathf.Min(a.y, b.y) - 1f;
                Vector3 c = (a + b) * 0.5f;
                float half = dir.magnitude * 0.5f + 0.02f;
                switch (type)
                {
                    case "snow-fence":
                        mb.AddBox((int)M.WoodDark, new Vector3(a.x, baseY + (height + 1f) * 0.5f, a.z), new Vector3(0.09f, (height + 1f) * 0.5f, 0.09f), r, 0.5f);
                        for (int k = 0; k < 7; k++)
                            mb.AddBox((int)M.WoodLight, new Vector3(c.x, baseY + 1.4f + k * (height - 0.6f) / 7f, c.z) + outward * 0.08f, new Vector3(0.025f, 0.09f, half), r, 0.5f);
                        break;
                    case "sea-wall":
                    case "breakwater":
                    {
                        float w = type == "breakwater" ? 3.5f : 1.2f;
                        mb.AddBox((int)M.Concrete, new Vector3(c.x, baseY + (height + 1f) * 0.5f, c.z) + outward * (w * 0.5f), new Vector3(w * 0.5f, (height + 1f) * 0.5f, half), r, 0.3f);
                        mb.AddBox((int)M.Concrete, new Vector3(c.x, baseY + height + 1.35f, c.z) + outward * (w - 0.3f), new Vector3(0.3f, 0.35f, half), r, 0.3f); // wave return
                        if (type == "breakwater" && (int)(t / step) % 2 == 0) // armour units below on the sea side
                            for (int k = 0; k < 3; k++)
                                mb.AddBox((int)M.Concrete, new Vector3(c.x, baseY + 0.4f, c.z) + outward * (w + 1.2f + k * 1.3f) + r * new Vector3(0f, 0f, (float)rng.NextDouble() * 2f - 1f),
                                    new Vector3(0.7f, 0.7f, 0.7f), r * Quaternion.Euler((float)rng.NextDouble() * 90f, (float)rng.NextDouble() * 90f, 0f), 0.5f);
                        break;
                    }
                    default: // retaining, split-retaining, flood-marks
                    {
                        float hh = tiers ? height * 0.55f : height;
                        mb.AddBox((int)face, new Vector3(c.x, baseY + (hh + 1f) * 0.5f, c.z) + outward * 0.4f, new Vector3(0.4f, (hh + 1f) * 0.5f, half), r, 0.25f);
                        mb.AddBox((int)face, new Vector3(c.x, baseY + hh + 1.1f, c.z) + outward * 0.4f, new Vector3(0.5f, 0.1f, half), r, 0.3f); // coping
                        if (joints)
                        {
                            mb.AddBox((int)M.Graphite, new Vector3(a.x, baseY + (hh + 1f) * 0.5f, a.z) + outward * -0.01f, new Vector3(0.02f, (hh + 1f) * 0.5f, 0.02f), r, 0.5f); // joint
                            mb.AddBox((int)M.Graphite, new Vector3(c.x, baseY + 1.6f, c.z) - outward * 0.02f, new Vector3(0.02f, 0.08f, 0.08f), r, 0.5f); // weep hole
                        }
                        else if (hh > 12f) // a tall cutting: benches break the face
                            for (float y = 10f; y < hh; y += 10f)
                                mb.AddBox((int)face, new Vector3(c.x, baseY + y, c.z) + outward * 0.9f, new Vector3(0.5f, 0.15f, half), r, 0.3f);
                        if (tiers)
                        {
                            mb.AddBox((int)M.Concrete, new Vector3(c.x, baseY + hh + (height * 0.45f + 1f) * 0.5f + 0.2f, c.z) + outward * 3.4f, new Vector3(0.4f, (height * 0.45f + 1f) * 0.5f, half), r, 0.25f);
                            mb.AddBox((int)M.Foliage, new Vector3(c.x, baseY + hh + 1.35f, c.z) + outward * 1.9f, new Vector3(1.1f, 0.25f, half), r, 0.3f);
                        }
                        if (type == "flood-marks" && (int)(t / step) % 3 == 0)
                            for (int k = 1; k <= 3; k++)
                                mb.AddBox((int)(k == 3 ? M.SteelRed : M.SteelYellow), new Vector3(c.x, baseY + 1f + k * hh / 4f, c.z) - outward * 0.03f, new Vector3(0.02f, 0.06f, half * 0.8f), r, 0.5f);
                        break;
                    }
                }
                if (t >= nextLamp)
                {
                    nextLamp += lampEvery;
                    Vector3 post = new Vector3(c.x, baseY + height + 1f, c.z) + outward * 1.5f;
                    mb.AddBox((int)M.SteelGrey, post + Vector3.up * 2.5f, new Vector3(0.08f, 2.5f, 0.08f), r, 0.5f);
                    mb.AddBox((int)M.WindowLit, post + Vector3.up * 5.1f, new Vector3(0.25f, 0.2f, 0.25f), r, 0.5f);
                }
            }
            if (type == "flood-marks") // a staff gauge at the landmark itself
            {
                Vector3 g = Beside(track, ground, lm.AtMetres, side, lateral - 0.5f);
                mb.AddBox((int)M.OffWhite, g + Vector3.up * (height * 0.5f), new Vector3(0.12f, height * 0.5f, 0.03f), Quaternion.identity, 0.5f);
                for (int k = 0; k < height; k++) mb.AddBox((int)M.Graphite, g + Vector3.up * (k + 0.5f), new Vector3(0.13f, 0.03f, 0.035f), Quaternion.identity, 0.5f);
            }
        }

        /// <summary>A stone stair climbing away from the road between low walls, with lanterns at the landings.</summary>
        static void StoneStair(MeshBuilder mb, TrackData track, TerrainCollider ground, RouteLandmarkDef lm, int side, float lateral, float length, float height)
        {
            Vector3 start = Beside(track, ground, lm.AtMetres, side, lateral);
            TrackSample s = track.SampleAt(lm.AtMetres);
            Vector3 outward = new Vector3(s.Right.x, 0f, s.Right.z).normalized * side;
            Quaternion r = Quaternion.LookRotation(outward, Vector3.up);
            float run = Mathf.Min(length, 40f);
            int steps = Mathf.Max(8, Mathf.RoundToInt(height / 0.18f));
            float tread = run / steps;
            for (int i = 0; i < steps; i++)
            {
                Vector3 p = start + outward * (i * tread + tread * 0.5f);
                float g = Ground(ground, p, start.y);
                float y = Mathf.Max(g, start.y + height * i / steps);
                float bottom = Mathf.Min(g, y) - 0.6f;
                mb.AddBox((int)M.Stone, new Vector3(p.x, (y + bottom) * 0.5f, p.z), new Vector3(1.6f, (y - bottom) * 0.5f, tread * 0.5f + 0.02f), r, 0.5f);
                foreach (int k in new[] { -1, 1 })
                    mb.AddBox((int)M.Stone, new Vector3(p.x, y + 0.3f, p.z) + r * new Vector3(k * 1.8f, 0f, 0f), new Vector3(0.2f, 0.4f, tread * 0.5f + 0.02f), r, 0.5f);
                if (i % Mathf.Max(1, steps / 4) == 0)
                    foreach (int k in new[] { -1, 1 })
                    {
                        Vector3 lp = new Vector3(p.x, y + 0.7f, p.z) + r * new Vector3(k * 2.4f, 0f, 0f);
                        mb.AddBox((int)M.Stone, lp, new Vector3(0.2f, 0.4f, 0.2f), r, 0.5f);
                        mb.AddBox((int)M.LanternPaper, lp + Vector3.up * 0.6f, new Vector3(0.2f, 0.2f, 0.2f), r, 0.5f);
                        mb.AddBox((int)M.Stone, lp + Vector3.up * 0.85f, new Vector3(0.3f, 0.06f, 0.3f), r, 0.5f);
                    }
            }
        }

        /// <summary>Quarry benches: stepped rock faces rising away from the road along it.</summary>
        static void QuarrySteps(MeshBuilder mb, TrackData track, TerrainCollider ground, RouteLandmarkDef lm, int side, float lateral, float length, float height)
        {
            int benches = Mathf.Clamp(Mathf.RoundToInt(height / 7.5f), 2, 8);
            float rise = height / benches;
            for (float t = 0f; t < length; t += 10f)
            {
                float d0 = lm.AtMetres - length * 0.5f + t;
                Vector3 a = Beside(track, ground, d0, side, lateral), b = Beside(track, ground, d0 + 10f, side, lateral);
                Vector3 dir = b - a;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) continue;
                Quaternion r = Quaternion.LookRotation(dir.normalized, Vector3.up);
                Vector3 outward = Vector3.Cross(Vector3.up, dir.normalized) * side; // away from the road
                float baseY = Mathf.Min(a.y, b.y) - 1f;
                Vector3 c = (a + b) * 0.5f;
                for (int k = 0; k < benches; k++)
                {
                    float top = baseY + 1f + (k + 1) * rise;
                    Vector3 centre = new Vector3(c.x, (top + baseY) * 0.5f, c.z) + outward * (k * 6f + 3f);
                    mb.AddBox((int)(k % 2 == 0 ? M.Stone : M.Concrete), centre, new Vector3(3f, (top - baseY) * 0.5f, dir.magnitude * 0.5f + 0.02f), r, 0.2f);
                }
            }
        }

        // ------------------------------------------------------------------ rail

        static GameObject Rail(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            string type = TypeOf(lm);
            float length = P(lm, "length", 200f);
            int side = lm.Side == "left" ? -1 : 1;
            var mb = Kit();
            switch (type)
            {
                case "coast-railway":
                {
                    TrackSample s0 = track.SampleAt(lm.AtMetres);
                    float lateral = Mathf.Max(6f, lm.OffsetMetres - Mathf.Abs(RoadGeometry.BarrierLateral(s0, side)));
                    Vector3 prev = default;
                    for (float t = 0f; t <= length; t += 5f)
                    {
                        Vector3 p = Beside(track, ground, lm.AtMetres - length * 0.5f + t, side, lateral);
                        if (OverRoad(track, p, 2.5f, out _)) { prev = p; continue; }
                        if (t > 0f && !OverRoad(track, prev, 2.5f, out _)) RailSegment(mb, prev, p, 1.6f); // seen from the road, 30 m and more away
                        if (Mathf.Repeat(t, 50f) < 0.1f) // catenary masts
                        {
                            Vector3 dir = t > 0f ? (p - prev) : Vector3.forward;
                            dir.y = 0f;
                            Vector3 sideDir = Vector3.Cross(Vector3.up, dir.normalized);
                            mb.AddBox((int)M.SteelGrey, p + sideDir * 2.2f + Vector3.up * 3.2f, new Vector3(0.12f, 3.2f, 0.12f), Quaternion.LookRotation(dir.normalized), 0.5f);
                            Beam(mb, M.SteelGrey, p + sideDir * 2.2f + Vector3.up * 6f, p + Vector3.up * 6f, 0.05f);
                        }
                        prev = p;
                    }
                    break;
                }
                case "funicular":
                case "conveyor":
                case "cable-station":
                {
                    Site site = SiteOf(track, lm, ground, 6f, 6f);
                    Vector3 up = site.Out;
                    float runLen = type == "cable-station" ? 220f : length;
                    Vector3 far = site.Pos + up * runLen;
                    far.y = Mathf.Max(Ground(ground, far, site.Pos.y), site.Pos.y + runLen * (type == "conveyor" ? 0.18f : 0.3f));
                    if (type == "funicular")
                    {
                        Vector3 prev = site.Pos;
                        for (int i = 1; i <= 40; i++)
                        {
                            float t = i / 40f;
                            Vector3 p = Vector3.Lerp(site.Pos, far, t);
                            float g = Ground(ground, p, p.y);
                            p.y = Mathf.Max(p.y, g + 0.6f);
                            // Crossing another part of the road: carry the track over it at the overhead clearance.
                            if (OverRoad(track, p, 3f, out float ry)) { p.y = Mathf.Max(p.y, ry + OverheadClearance + 0.8f); g = Mathf.Min(g, ry); }
                            RailSegment(mb, prev, p);
                            if (p.y - g > 1.2f && !OverRoad(track, p, 1.5f, out _))
                                mb.AddBox((int)M.Concrete, new Vector3(p.x, (p.y + g) * 0.5f, p.z), new Vector3(0.4f, (p.y - g) * 0.5f, 0.4f), Quaternion.LookRotation(up), 0.5f);
                            prev = p;
                        }
                        Vector3 car = Vector3.Lerp(site.Pos, far, 0.38f);
                        car.y = Mathf.Max(car.y, Ground(ground, car, car.y) + 0.6f);
                        if (OverRoad(track, car, 3f, out float carRoad)) car.y = Mathf.Max(car.y, carRoad + OverheadClearance + 0.8f);
                        Quaternion tilt = Quaternion.LookRotation((far - site.Pos).normalized, Vector3.up);
                        mb.AddBox((int)M.SteelRed, car + Vector3.up * 1.6f, new Vector3(1.3f, 1.3f, 3.5f), tilt, 0.4f);
                        mb.AddBox((int)M.Graphite, car + Vector3.up * 1.9f, new Vector3(1.32f, 0.5f, 3.2f), tilt, 0.4f);
                    }
                    else if (type == "conveyor")
                    {
                        Vector3 a = site.Pos + Vector3.up * 4f, b = far + Vector3.up * 6f;
                        float lift = ClearanceLift(track, a, b);
                        a += Vector3.up * lift;
                        b += Vector3.up * lift;
                        Beam(mb, M.SteelGrey, a, b, 1.1f, 0.3f);
                        Beam(mb, M.MetalRoof, a + Vector3.up * 1.4f, b + Vector3.up * 1.4f, 1.3f, 0.08f);
                        for (int i = 0; i <= 12; i++)
                        {
                            Vector3 p = Vector3.Lerp(a, b, i / 12f);
                            if (OverRoad(track, p, 2f, out _)) continue; // no trestle leg on the road
                            float g = Ground(ground, p, p.y - 4f);
                            Lattice(mb, M.SteelGrey, new Vector3(p.x, g, p.z), Quaternion.LookRotation(up), 1f, 0.8f, Mathf.Max(1f, p.y - g), 0.07f, 2);
                        }
                        mb.AddBox((int)M.SteelGrey, site.Pos + Vector3.up * 3f, new Vector3(3f, 3f, 3f), Quaternion.LookRotation(up), 0.3f);
                        Frustum(mb, M.SteelYellow, site.Pos + Vector3.up * 6f, 0.8f, 3f, 3f, 12);
                    }
                    else
                    {
                        // Cable-car station: a building with the bull-wheel under its roof, cables up to a far tower and a cabin on the line.
                        Block(mb, site.Pos, site.Rot, 14f, 10f, 7f, "flat", M.Concrete);
                        Vector3 wheel = site.Pos + Vector3.up * 5.5f + up * 3f;
                        Frustum(mb, M.SteelRed, wheel, 2.4f, 2.4f, 0.4f, 20);
                        Lattice(mb, M.SteelGrey, new Vector3(far.x, Ground(ground, far, far.y), far.z), Quaternion.LookRotation(up), 1.2f, 0.6f, 18f, 0.1f);
                        Vector3 towerTop = new Vector3(far.x, Ground(ground, far, far.y) + 18f, far.z);
                        towerTop += Vector3.up * ClearanceLift(track, wheel + Vector3.down * 4.5f, towerTop + Vector3.down * 4.5f); // the cabin hangs below the cable
                        foreach (float w in new[] { -2.2f, 2.2f })
                            Cable(mb, M.Graphite, wheel + site.Along * w, towerTop + site.Along * w, 4f, 0.04f, 14);
                        Vector3 cabin = Vector3.Lerp(wheel, towerTop, 0.45f) + Vector3.down * 4.2f + site.Along * 2.2f;
                        mb.AddBox((int)M.SteelYellow, cabin + Vector3.down * 1.4f, new Vector3(1.2f, 1.1f, 1.6f), Quaternion.LookRotation(up), 0.4f);
                        Beam(mb, M.SteelGrey, cabin, cabin + Vector3.down * 0.4f, 0.05f);
                    }
                    break;
                }
                default: // trestle: a railway viaduct in view beside the road
                {
                    Site site = SiteOf(track, lm, ground, 4f, length * 0.5f);
                    Vector3 a = site.Pos - site.Along * length * 0.5f + Vector3.up * 12f, b = site.Pos + site.Along * length * 0.5f + Vector3.up * 12f;
                    RailSegment(mb, a, b);
                    for (int i = 0; i <= Mathf.RoundToInt(length / 12f); i++)
                    {
                        Vector3 p = Vector3.Lerp(a, b, i / Mathf.Max(1f, Mathf.Round(length / 12f)));
                        float g = Ground(ground, p, p.y - 12f);
                        Lattice(mb, M.WoodDark, new Vector3(p.x, g, p.z), site.Rot, 2f, 1.3f, p.y - g, 0.15f);
                    }
                    break;
                }
            }
            return Emit(lm.Name, mb, track, lm, "", mats, parent, Vector3.zero, Quaternion.identity, false, GameLayers.Scenery, profile);
        }

        /// <summary>A length of railway between two points: ballast, sleepers (every <paramref name="sleeperPitch"/> m) and two rails.</summary>
        static void RailSegment(MeshBuilder mb, Vector3 a, Vector3 b, float sleeperPitch = 0.65f)
        {
            Vector3 dir = b - a;
            float len = dir.magnitude;
            if (len < 0.05f) return;
            Quaternion r = Quaternion.LookRotation(dir / len, Vector3.up);
            Vector3 c = (a + b) * 0.5f;
            mb.AddBox((int)M.Stone, c + Vector3.down * 0.25f, new Vector3(1.8f, 0.3f, len * 0.5f + 0.05f), r, 0.4f);
            int sleepers = Mathf.Max(1, Mathf.RoundToInt(len / sleeperPitch));
            for (int i = 0; i < sleepers; i++)
                mb.AddBox((int)M.WoodDark, Vector3.Lerp(a, b, (i + 0.5f) / sleepers) + Vector3.up * 0.08f, new Vector3(1.3f, 0.07f, 0.12f), r, 0.5f);
            foreach (float x in new[] { -0.72f, 0.72f })
                mb.AddBox((int)M.SteelGrey, c + r * new Vector3(x, 0.22f, 0f), new Vector3(0.04f, 0.07f, len * 0.5f + 0.02f), r, 0.5f);
        }

        /// <summary>A plain box building with windows and a roof (station houses and the like), in world space.</summary>
        static void Block(MeshBuilder mb, Vector3 at, Quaternion rot, float w, float d, float h, string roof, M wall)
        {
            mb.AddBox((int)wall, at + rot * new Vector3(0f, h * 0.5f, 0f), new Vector3(w * 0.5f, h * 0.5f, d * 0.5f), rot, 0.3f);
            Windows(mb, at, rot, w, d, 0f, h, 3f, false);
            Roof(mb, M.MetalRoof, at, rot, w, d, h, roof, roof == "flat" ? 0f : 0.6f);
        }

        // ------------------------------------------------------------------ signs

        static GameObject Sign(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            string type = TypeOf(lm);
            int count = Mathf.Clamp(Mathf.RoundToInt(P(lm, "count", 1f)), 1, 12);
            var mb = Kit();
            if (type == "tunnel-marker")
            {
                // A board over the road on a gantry, in the authored colour (blue → the sea-blue paint).
                RoadAcross(track, lm.AtMetres, out TrackSample s, out Vector3 across, out float top, out Vector3 bl, out Vector3 br);
                float deck = top + OverheadClearance;
                Vector3 l = bl - across * 1.5f, rr = br + across * 1.5f;
                foreach (Vector3 e in new[] { l, rr })
                {
                    float g = Ground(ground, e, s.Position.y);
                    mb.AddBox((int)M.SteelGrey, new Vector3(e.x, (g + deck + 1.5f) * 0.5f, e.z), new Vector3(0.18f, (deck + 1.5f - g) * 0.5f, 0.18f), Quaternion.identity, 0.5f);
                }
                Beam(mb, M.SteelGrey, new Vector3(l.x, deck + 0.3f, l.z), new Vector3(rr.x, deck + 0.3f, rr.z), 0.15f);
                Vector3 mid = new Vector3((l.x + rr.x) * 0.5f, deck + 0.9f, (l.z + rr.z) * 0.5f);
                Quaternion face = Quaternion.LookRotation(new Vector3(s.Tangent.x, 0f, s.Tangent.z).normalized, Vector3.up);
                mb.AddBox((int)(S(lm, "colour", "blue") == "blue" ? M.Sea : M.SteelYellow), mid, new Vector3(3.2f, 0.7f, 0.05f), face, 0.5f);
                for (int k = 0; k < 5; k++) mb.AddBox((int)M.OffWhite, mid + face * new Vector3(-2.4f + k * 1.2f, 0f, -0.06f), new Vector3(0.4f, 0.28f, 0.01f), face, 0.5f);
                return Emit(lm.Name, mb, track, lm, "", mats, parent, Vector3.zero, Quaternion.identity, true, GameLayers.Scenery, profile);
            }
            Site site = SiteOf(track, lm, ground, 3f, type == "placard" ? count * 2.2f : 3f, 4f);
            Quaternion r = Quaternion.identity;
            var rng = Rng(track, lm);
            switch (type)
            {
                case "marshal-beacons":
                {
                    // A marshal post: a small booth, a flag board and amber beacons on a pole.
                    mb.AddBox((int)M.OffWhite, new Vector3(0f, 1.2f, 0f), new Vector3(1f, 1.2f, 0.8f), r, 0.5f);
                    mb.AddBox((int)M.Graphite, new Vector3(0f, 1.5f, 0.82f), new Vector3(0.7f, 0.35f, 0.02f), r, 0.5f);
                    mb.AddBox((int)M.SteelYellow, new Vector3(0f, 2.5f, 0f), new Vector3(1.15f, 0.1f, 0.95f), r, 0.5f);
                    mb.AddBox((int)M.SteelGrey, new Vector3(1.4f, 2f, 0.4f), new Vector3(0.06f, 2f, 0.06f), r, 0.5f);
                    for (int k = 0; k < Mathf.Max(2, count); k++)
                        mb.AddBox((int)(S(lm, "colour", "amber") == "amber" ? M.LanternPaper : M.WindowLit), new Vector3(1.4f, 3.2f + k * 0.45f, 0.4f), new Vector3(0.18f, 0.15f, 0.18f), r, 0.5f);
                    break;
                }
                case "memorial":
                    mb.AddBox((int)M.Stone, new Vector3(0f, 0.3f, 0f), new Vector3(1.8f, 0.3f, 1.2f), r, 0.5f);
                    mb.AddBox((int)M.Stone, new Vector3(0f, 1.6f, 0f), new Vector3(0.6f, 1.1f, 0.18f), r, 0.5f);
                    mb.AddBox((int)M.SteelGrey, new Vector3(0f, 1.8f, 0.19f), new Vector3(0.4f, 0.3f, 0.01f), r, 0.5f);
                    for (int k = 0; k < 6; k++)
                        mb.AddBox((int)(k % 2 == 0 ? M.SteelRed : M.OffWhite), new Vector3(-1f + k * 0.4f, 0.7f, 0.9f), new Vector3(0.1f, 0.12f, 0.1f), r, 0.5f);
                    break;
                case "mural":
                {
                    // Enamel panels on a workshop wall.
                    mb.AddBox((int)M.Brick, new Vector3(0f, 3f, -0.4f), new Vector3(6f, 3f, 0.4f), r, 0.3f);
                    M[] enamel = { M.SteelRed, M.SteelYellow, M.OffWhite, M.Sea, M.SteelGrey };
                    for (int i = 0; i < 5; i++)
                    for (int j = 0; j < 3; j++)
                        mb.AddBox((int)enamel[rng.Next(enamel.Length)], new Vector3(-4.8f + i * 2.4f, 1.4f + j * 1.6f, 0.02f), new Vector3(1f, 0.65f, 0.02f), r, 0.5f);
                    Roof(mb, M.MetalRoof, new Vector3(0f, 0f, -0.4f), r, 12.4f, 1.2f, 6f, "flat", 0f);
                    break;
                }
                case "placard":
                {
                    // A row of shuttered stalls, each with its sign board.
                    for (int i = 0; i < count; i++)
                    {
                        float x = (i - (count - 1) * 0.5f) * 2.3f;
                        mb.AddBox((int)M.WoodDark, new Vector3(x, 1.3f, 0f), new Vector3(1.05f, 1.3f, 1.2f), r, 0.4f);
                        mb.AddBox((int)M.SteelGrey, new Vector3(x, 1.1f, 1.22f), new Vector3(0.95f, 1f, 0.02f), r, 0.4f);
                        for (int k = 0; k < 8; k++) mb.AddBox((int)M.Graphite, new Vector3(x, 0.25f + k * 0.25f, 1.25f), new Vector3(0.95f, 0.015f, 0.01f), r, 0.5f);
                        mb.AddBox((int)(i % 3 == 0 ? M.SteelRed : i % 3 == 1 ? M.OffWhite : M.SteelYellow), new Vector3(x, 2.45f, 1.25f), new Vector3(0.9f, 0.22f, 0.04f), r, 0.5f);
                        mb.AddBox((int)M.MetalRoof, new Vector3(x, 2.75f, 0.3f), new Vector3(1.15f, 0.06f, 1.5f), Quaternion.Euler(8f, 0f, 0f), 0.4f);
                    }
                    break;
                }
                default: // flood-marker, timing-board or unknown: a board on two posts
                    foreach (float x in new[] { -1.2f, 1.2f }) mb.AddBox((int)M.SteelGrey, new Vector3(x, 1.5f, 0f), new Vector3(0.06f, 1.5f, 0.06f), r, 0.5f);
                    mb.AddBox((int)M.OffWhite, new Vector3(0f, 2.4f, 0.08f), new Vector3(1.5f, 0.6f, 0.03f), r, 0.5f);
                    break;
            }
            return Emit(lm.Name, mb, track, lm, "", mats, parent, site.Pos, site.Rot, site.Offset < 20f, GameLayers.Scenery, profile);
        }
    }
}
