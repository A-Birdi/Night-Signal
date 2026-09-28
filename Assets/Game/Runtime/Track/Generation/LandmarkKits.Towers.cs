using System.Collections.Generic;
using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Towers (kit <c>tower</c>): water tanks, masts and antennas, lighthouse, radio dish, chimneys, cooling towers, beacons,
    /// pylon lines, cranes, spillway intake towers, an observatory. <c>count</c> of them stand along the road, each kept
    /// clear of the course; built in world space as one mesh per landmark.
    /// </summary>
    public static partial class LandmarkKits
    {
        static GameObject Tower(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            string type = TypeOf(lm);
            float h = P(lm, "height", 20f);
            int count = Mathf.Clamp(Mathf.RoundToInt(P(lm, "count", 1f)), 1, 12);
            float foot = type == "cooling-tower" ? h * 0.42f : type == "observatory" ? 10f : type == "crane" ? 4f : type == "spillway-tower" ? 4f : Mathf.Max(3f, h * 0.12f);
            float spacing = type == "pylon-line" ? 75f : type == "antenna" ? 14f : type == "cooling-tower" ? h * 0.95f : type == "crane" ? 30f
                : type == "spillway-tower" ? 26f : Mathf.Max(foot * 2.6f, 12f);
            var rng = Rng(track, lm);
            var mb = Kit();
            var tops = new List<Vector3>();
            for (int i = 0; i < count; i++)
            {
                var one = new RouteLandmarkDef
                {
                    Id = lm.Id, Name = lm.Name, Side = lm.Side, Kit = lm.Kit, Params = lm.Params,
                    AtMetres = Mathf.Repeat(lm.AtMetres + (i - (count - 1) * 0.5f) * spacing, Mathf.Max(1f, track.LengthMetres)),
                    OffsetMetres = lm.OffsetMetres + (type == "antenna" ? (float)rng.NextDouble() * 10f : 0f),
                };
                Site site = SiteOf(track, one, ground, foot, type == "spillway-tower" ? foot + 14f : foot);
                float hi = h * (type == "antenna" ? 0.7f + 0.3f * (float)rng.NextDouble() : 1f);
                tops.Add(OneTower(mb, type, site, hi, foot, rng));
            }
            if (type == "pylon-line")
                for (int i = 0; i + 1 < tops.Count; i++)
                    for (int c = -1; c <= 1; c++)
                    {
                        Vector3 a = tops[i] + Vector3.right * 0f, b = tops[i + 1];
                        Vector3 across = Vector3.Cross(Vector3.up, (b - a).normalized) * (c * 4.5f);
                        Cable(mb, M.Graphite, a + across + Vector3.down * 3f, b + across + Vector3.down * 3f, 2.5f, 0.03f, 12);
                    }
            return Emit(lm.Name, mb, track, lm, "", mats, parent, Vector3.zero, Quaternion.identity, false, GameLayers.Scenery, profile);
        }

        /// <summary>One tower at <paramref name="site"/> (world space); returns its top (for cables).</summary>
        static Vector3 OneTower(MeshBuilder mb, string type, Site site, float h, float foot, System.Random rng)
        {
            Vector3 p = site.Pos;
            Quaternion face = site.Rot;
            Vector3 top = p + Vector3.up * h;
            switch (type)
            {
                case "water-tank":
                {
                    float legs = h * 0.62f, r = Mathf.Max(2.2f, h * 0.2f);
                    Lattice(mb, M.SteelGrey, p, face, r * 0.75f, r * 0.62f, legs, 0.12f, 3);
                    mb.AddBox((int)M.Concrete, p + Vector3.up * 0.3f, new Vector3(r, 0.3f, r), face, 0.3f);
                    Lathe(mb, M.OffWhite, p + Vector3.up * legs, new[] { new Vector2(r * 0.4f, -0.8f), new Vector2(r, 0.4f), new Vector2(r, h - legs - r * 0.35f) }, 20, true);
                    Lathe(mb, M.SteelRed, p + Vector3.up * (h - r * 0.35f), new[] { new Vector2(r + 0.2f, 0f), new Vector2(0.15f, r * 0.35f) }, 20, true);
                    Beam(mb, M.SteelGrey, p + face * new Vector3(0f, 0f, r * 0.8f), p + face * new Vector3(0f, legs, r * 0.8f), 0.05f); // ladder
                    Beam(mb, M.SteelGrey, p + face * new Vector3(0.4f, 0f, r * 0.8f), p + face * new Vector3(0.4f, legs, r * 0.8f), 0.05f);
                    break;
                }
                case "antenna":
                case "mast":
                case "beacon":
                {
                    bool guyed = type != "mast";
                    float w0 = type == "beacon" ? 1.6f : type == "mast" ? 1.4f : 0.5f;
                    Lattice(mb, type == "beacon" ? M.SteelRed : M.SteelGrey, p, face, w0, w0 * (guyed ? 1f : 0.35f), h, type == "antenna" ? 0.05f : 0.09f);
                    if (type == "beacon")
                        for (int band = 1; band < 7; band += 2) // red and white bands
                            Lattice(mb, M.OffWhite, p + Vector3.up * (band * h / 7f), face, w0 * 1.01f, w0 * 1.01f, h / 7f, 0.1f, 1);
                    mb.AddBox((int)M.WindowLit, top + Vector3.up * 0.4f, new Vector3(0.35f, 0.4f, 0.35f), face, 0.5f);
                    if (guyed)
                        for (int k = 0; k < 3; k++)
                        {
                            float a = k * Mathf.PI * 2f / 3f + 0.4f;
                            Vector3 anchor = p + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * h * 0.55f;
                            mb.AddBox((int)M.Concrete, anchor + Vector3.up * 0.2f, new Vector3(0.6f, 0.4f, 0.6f), Quaternion.identity, 0.5f);
                            Beam(mb, M.Graphite, anchor, p + Vector3.up * h * 0.85f, 0.02f);
                            Beam(mb, M.Graphite, anchor, p + Vector3.up * h * 0.5f, 0.02f);
                        }
                    if (type == "mast") // dish and panel antennas near the top
                        for (int k = 0; k < 3; k++)
                            mb.AddBox((int)M.OffWhite, p + face * new Vector3((k - 1) * 0.9f, h * 0.8f, 0.9f), new Vector3(0.3f, 0.9f, 0.08f), face, 0.5f);
                    break;
                }
                case "lighthouse":
                {
                    float r0 = Mathf.Max(2.4f, h * 0.13f), r1 = r0 * 0.62f, shaft = h * 0.8f;
                    mb.AddBox((int)M.Stone, p + Vector3.up * 0.6f, new Vector3(r0 * 2f, 1.2f, r0 * 2f), Quaternion.Euler(0f, 17f, 0f), 0.3f);
                    Frustum(mb, M.OffWhite, p + Vector3.up * 1.2f, r0, r1, shaft, 20);
                    for (int band = 0; band < 3; band++) // red bands
                    {
                        float y0 = 1.2f + shaft * (0.2f + band * 0.25f), t0 = (y0 - 1.2f) / shaft;
                        Frustum(mb, M.SteelRed, p + Vector3.up * y0, Mathf.Lerp(r0, r1, t0) + 0.02f, Mathf.Lerp(r0, r1, t0 + 0.1f) + 0.02f, shaft * 0.1f, 20);
                    }
                    Vector3 gallery = p + Vector3.up * (1.2f + shaft);
                    Frustum(mb, M.Graphite, gallery, r1 + 0.9f, r1 + 0.9f, 0.3f, 20);
                    for (int k = 0; k < 16; k++)
                    {
                        float a = k * Mathf.PI * 2f / 16f;
                        mb.AddBox((int)M.Graphite, gallery + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (r1 + 0.85f) + Vector3.up * 0.8f, new Vector3(0.04f, 0.5f, 0.04f), Quaternion.identity, 0.5f);
                    }
                    Frustum(mb, M.WindowLit, gallery + Vector3.up * 0.3f, r1 * 0.72f, r1 * 0.72f, 2.4f, 16);
                    Frustum(mb, M.SteelRed, gallery + Vector3.up * 2.7f, r1 * 0.85f, 0.2f, 1.6f, 16);
                    top = gallery + Vector3.up * 4.3f;
                    break;
                }
                case "radio-dish":
                {
                    float dishR = h * 0.42f;
                    Frustum(mb, M.Concrete, p, 3f, 2.2f, h * 0.45f, 16);
                    Vector3 hub = p + Vector3.up * (h * 0.55f);
                    Quaternion tilt = face * Quaternion.Euler(-35f, 0f, 0f);
                    var prof = new List<Vector2>();
                    for (int i = 0; i <= 8; i++)
                    {
                        float t = i / 8f;
                        prof.Add(new Vector2(t * dishR + 0.01f, t * t * dishR * 0.35f));
                    }
                    LatheTilted(mb, M.OffWhite, hub, tilt, prof, 28);
                    Beam(mb, M.SteelGrey, hub, hub + tilt * (Vector3.up * dishR * 0.75f), 0.12f);
                    mb.AddBox((int)M.SteelGrey, hub + tilt * (Vector3.up * dishR * 0.78f), new Vector3(0.5f, 0.5f, 0.5f), tilt, 0.5f);
                    mb.AddBox((int)M.SteelGrey, p + Vector3.up * (h * 0.48f), new Vector3(2.6f, 0.9f, 2.6f), face, 0.4f);
                    break;
                }
                case "chimney":
                {
                    float r0 = Mathf.Max(1.2f, h * 0.05f + 0.6f);
                    Frustum(mb, M.Brick, p, r0, r0 * 0.62f, h, 16);
                    Frustum(mb, M.SteelRed, p + Vector3.up * (h * 0.86f), r0 * 0.67f + 0.03f, r0 * 0.64f + 0.03f, h * 0.05f, 16);
                    Frustum(mb, M.OffWhite, p + Vector3.up * (h * 0.91f), r0 * 0.65f + 0.03f, r0 * 0.63f + 0.03f, h * 0.05f, 16);
                    Frustum(mb, M.Graphite, p + Vector3.up * h, r0 * 0.66f, r0 * 0.66f, 0.4f, 16);
                    break;
                }
                case "cooling-tower":
                {
                    float rb = h * 0.4f, waist = h * 0.25f, rt = h * 0.29f, yw = h * 0.72f;
                    var outside = new List<Vector2>();
                    for (int i = 0; i <= 12; i++)
                    {
                        float y = i / 12f * h;
                        float r = y < yw ? Mathf.Lerp(rb, waist, Mathf.Sqrt(y / yw)) : Mathf.Lerp(waist, rt, (y - yw) / (h - yw));
                        outside.Add(new Vector2(r, y));
                    }
                    Lathe(mb, M.Concrete, p, outside, 32);
                    var inside = new List<Vector2>();
                    for (int i = outside.Count - 1; i >= 0; i--) inside.Add(new Vector2(outside[i].x - 0.3f, outside[i].y));
                    Lathe(mb, M.Graphite, p, inside, 32);
                    for (int k = 0; k < 24; k++) // the legs of the open base
                    {
                        float a = k * Mathf.PI * 2f / 24f;
                        Beam(mb, M.Concrete, p + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (rb + 0.8f), p + new Vector3(Mathf.Cos(a + 0.13f), 0f, Mathf.Sin(a + 0.13f)) * rb + Vector3.up * 3f, 0.3f);
                    }
                    break;
                }
                case "pylon-line":
                {
                    // A lattice pylon with three cross-arms.
                    Lattice(mb, M.SteelGrey, p, face, 2.4f, 0.7f, h, 0.12f);
                    foreach (float y in new[] { h - 3f, h - 9f })
                        Beam(mb, M.SteelGrey, p + Vector3.up * y + Vector3.Cross(Vector3.up, site.Out) * -6f, p + Vector3.up * y + Vector3.Cross(Vector3.up, site.Out) * 6f, 0.25f);
                    top = p + Vector3.up * h;
                    break;
                }
                case "crane":
                {
                    // A tower crane: lattice mast, jib and counter-jib, counterweight, cab and a hook on its cable.
                    Lattice(mb, M.SteelYellow, p, face, 1.1f, 1.1f, h, 0.09f);
                    Vector3 slew = p + Vector3.up * h;
                    Quaternion jibDir = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                    Vector3 fwd = jibDir * Vector3.forward;
                    float jib = h * 1.1f, counter = h * 0.35f;
                    Beam(mb, M.SteelYellow, slew, slew + fwd * jib, 0.6f, 0.9f);
                    Beam(mb, M.SteelYellow, slew, slew - fwd * counter, 0.7f, 0.5f);
                    mb.AddBox((int)M.Concrete, slew - fwd * (counter * 0.85f) + Vector3.down * 1.2f, new Vector3(1.4f, 1.2f, 1.4f), jibDir, 0.4f);
                    Lattice(mb, M.SteelYellow, slew, jibDir, 0.6f, 0.2f, 7f, 0.07f, 2);
                    Beam(mb, M.Graphite, slew + Vector3.up * 7f, slew + fwd * jib, 0.03f);
                    Beam(mb, M.Graphite, slew + Vector3.up * 7f, slew - fwd * counter, 0.03f);
                    mb.AddBox((int)M.OffWhite, slew + Vector3.down * 1.4f + fwd * 1.6f, new Vector3(1f, 1.1f, 1f), jibDir, 0.5f);
                    float trolley = jib * (0.4f + 0.4f * (float)rng.NextDouble()), drop = h * (0.3f + 0.4f * (float)rng.NextDouble());
                    Beam(mb, M.Graphite, slew + fwd * trolley, slew + fwd * trolley + Vector3.down * drop, 0.03f);
                    mb.AddBox((int)M.SteelRed, slew + fwd * trolley + Vector3.down * (drop + 0.4f), new Vector3(0.4f, 0.4f, 0.4f), jibDir, 0.5f);
                    top = slew;
                    break;
                }
                case "spillway-tower":
                {
                    // A concrete intake tower rising from the water with a hoist house, reached by a footbridge.
                    float r = 2.6f;
                    Frustum(mb, M.Concrete, p + Vector3.down * 2f, r * 1.2f, r, h + 2f, 16);
                    mb.AddBox((int)M.Concrete, p + Vector3.up * (h + 1.6f), new Vector3(r + 0.6f, 1.6f, r + 0.6f), face, 0.3f);
                    Roof(mb, M.MetalRoof, p + Vector3.up * (h + 3.2f), face, (r + 0.6f) * 2f, (r + 0.6f) * 2f, 0f, "hip", 0.4f);
                    Windows(mb, p + Vector3.up * h, face, (r + 0.6f) * 2f, (r + 0.6f) * 2f, 0f, 3.2f, 2f, false, 0.8f, 0.9f);
                    // Footbridge from the hoist house down to the bank, running along the road (never toward it), on two trestles.
                    Vector3 from = p + site.Along * (r + 0.6f) + Vector3.up * (h + 0.1f);
                    Vector3 shore = p + site.Along * 14f;
                    shore.y = site.S.Position.y + 0.3f;
                    Beam(mb, M.SteelGrey, from, shore, 0.9f, 0.1f);
                    foreach (float w in new[] { -0.9f, 0.9f })
                        Beam(mb, M.SteelGrey, from + Vector3.up * 1f + site.Along * w, shore + Vector3.up * 1f + site.Along * w, 0.03f);
                    foreach (float t in new[] { 0.35f, 0.7f })
                    {
                        Vector3 q = Vector3.Lerp(from, shore, t);
                        Beam(mb, M.SteelGrey, q, new Vector3(q.x, Mathf.Min(shore.y, p.y) - 1f, q.z), 0.2f);
                    }
                    break;
                }
                case "observatory":
                {
                    float r = 6f;
                    mb.AddBox((int)M.OffWhite, p + Vector3.up * (h * 0.3f), new Vector3(r + 1f, h * 0.3f, r + 1f), face, 0.3f);
                    Frustum(mb, M.OffWhite, p + Vector3.up * (h * 0.6f), r, r, 1.2f, 24);
                    Dome(mb, M.OffWhite, p + Vector3.up * (h * 0.6f + 1.2f), r, 24);
                    mb.AddBox((int)M.Graphite, p + Vector3.up * (h * 0.6f + 1.2f + r * 0.55f) + face * new Vector3(0f, 0f, r * 0.62f), new Vector3(0.7f, r * 0.5f, 0.5f), face * Quaternion.Euler(-38f, 0f, 0f), 0.5f);
                    Windows(mb, p, face, (r + 1f) * 2f, (r + 1f) * 2f, 0f, h * 0.6f, 3f, false);
                    top = p + Vector3.up * (h * 0.6f + 1.2f + r);
                    break;
                }
                default: // transmitter or unknown: a plain mast
                    Lattice(mb, M.SteelGrey, p, face, 1f, 0.3f, h, 0.08f);
                    break;
            }
            return top;
        }

        /// <summary>A lathe (profile about local +y) turned by <paramref name="rot"/> and placed at <paramref name="origin"/>, both faces.</summary>
        static void LatheTilted(MeshBuilder mb, M sub, Vector3 origin, Quaternion rot, IList<Vector2> profile, int segments)
        {
            int n = profile.Count;
            for (int face = 0; face < 2; face++)
            {
                int start = mb.VertexCount;
                for (int j = 0; j < segments; j++)
                {
                    float a = j * Mathf.PI * 2f / segments;
                    var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    foreach (Vector2 q in profile) mb.AddVertex(origin + rot * (dir * q.x + Vector3.up * q.y), Vector3.up, Vector2.zero);
                }
                for (int i = 0; i < n - 1; i++)
                for (int j = 0; j < segments; j++)
                {
                    int a = start + j * n + i, b = start + ((j + 1) % segments) * n + i;
                    if (face == 0) mb.AddQuad((int)sub, a, a + 1, b + 1, b);
                    else mb.AddQuad((int)sub, a, b, b + 1, a + 1);
                }
            }
        }
    }
}
