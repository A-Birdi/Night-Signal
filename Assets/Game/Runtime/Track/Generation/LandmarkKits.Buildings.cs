using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>
    /// Buildings (kit <c>structure</c>) and gates (kit <c>gate</c>): one parametric building — plinth seated on sloping ground,
    /// walls in the authored material, rows of windows, doors, the authored roof — plus what makes each type read as the
    /// named place: a station's platform canopy, a shed's roller doors, a terminal's lit glass front, a switchyard's gantries.
    /// </summary>
    public static partial class LandmarkKits
    {
        static GameObject Structure(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            string type = TypeOf(lm);
            float w = P(lm, "width", 10f), d = P(lm, "depth", 8f), h = P(lm, "height", 5f);
            int count = Mathf.Clamp(Mathf.RoundToInt(P(lm, "count", 1f)), 1, 6);
            string roof = S(lm, "roof", "gable"), material = S(lm, "material", "concrete");
            bool lit = B(lm, "lit");
            // A row of buildings stands along the road; the footprint covers them all plus the type's extras (platform, yard).
            float gap = 4f;
            float extraFront = type == "station" ? 5f : type == "gallery" ? 3f : type == "workshop" ? 6f : 0f;
            float rowAlong = count * w + (count - 1) * gap;
            Site site = SiteOf(track, lm, ground, d * 0.5f + extraFront, rowAlong * 0.5f + 2f);
            var mb = Kit();
            Quaternion local = Quaternion.identity; // mesh built in the site frame: +z toward the road, x along it
            for (int k = 0; k < count; k++)
            {
                float x = -rowAlong * 0.5f + w * 0.5f + k * (w + gap);
                var centre = new Vector3(x, 0f, 0f);
                GroundRange(ground, new Site { Pos = site.Pos + site.Along * x, Out = site.Out, Along = site.Along }, d * 0.5f, w * 0.5f, out float low, out float high);
                float baseY = high - site.Pos.y + 0.3f, plinthDepth = high - low + 1.2f;
                // Plinth: from below the lowest ground up to the floor, so nothing floats on a slope.
                mb.AddBox((int)M.Stone, centre + new Vector3(0f, baseY - plinthDepth * 0.5f, 0f), new Vector3(w * 0.5f + 0.4f, plinthDepth * 0.5f, d * 0.5f + 0.4f), local, 0.4f);
                BuildingType(mb, type, centre + Vector3.up * baseY, w, d, h, roof, material, lit, lm, track);
            }
            bool nearRoad = site.Offset - d * 0.5f - extraFront < 25f;
            return Emit(lm.Name, mb, track, lm, "", mats, parent, site.Pos, site.Rot, nearRoad, GameLayers.Scenery, profile);
        }

        /// <summary>One building of <paramref name="type"/> standing on <paramref name="floor"/> (site frame, +z faces the road).</summary>
        static void BuildingType(MeshBuilder mb, string type, Vector3 floor, float w, float d, float h, string roof, string material, bool lit, RouteLandmarkDef lm, TrackData track)
        {
            Quaternion r = Quaternion.identity;
            M wall = WallMat(material);
            M roofMat = material == "timber" || material == "brick" || B(lm, "tiles") ? M.RoofTiles : M.MetalRoof;
            switch (type)
            {
                case "switchyard":
                    Switchyard(mb, floor, w, d, h);
                    return;
                case "gallery":
                case "pavilion":
                case "shelter":
                {
                    // Open on the road side: posts, a low back wall, a raised floor, the roof; benches under it.
                    bool pavilion = type == "pavilion";
                    mb.AddBox((int)M.WoodLight, floor + new Vector3(0f, 0.2f, 0f), new Vector3(w * 0.5f, 0.2f, d * 0.5f), r, 0.4f);
                    int posts = Mathf.Max(2, Mathf.RoundToInt(w / 3f));
                    for (int i = 0; i <= posts; i++)
                    {
                        float x = -w * 0.5f + i * w / posts;
                        foreach (float z in new[] { -d * 0.5f + 0.2f, d * 0.5f - 0.2f })
                            mb.AddBox((int)M.WoodDark, floor + new Vector3(x, h * 0.5f, z), new Vector3(0.14f, h * 0.5f, 0.14f), r, 0.5f);
                    }
                    if (!pavilion) mb.AddBox((int)M.WoodLight, floor + new Vector3(0f, h * 0.35f, -d * 0.5f + 0.1f), new Vector3(w * 0.5f, h * 0.35f, 0.06f), r, 0.35f);
                    mb.AddBox((int)M.WoodDark, floor + new Vector3(0f, h, d * 0.5f - 0.2f), new Vector3(w * 0.5f + 0.2f, 0.16f, 0.16f), r, 0.5f);
                    mb.AddBox((int)M.WoodDark, floor + new Vector3(0f, h, -d * 0.5f + 0.2f), new Vector3(w * 0.5f + 0.2f, 0.16f, 0.16f), r, 0.5f);
                    if (type == "gallery") // a railing along the front: a viewing gallery
                    {
                        mb.AddBox((int)M.WoodDark, floor + new Vector3(0f, 1.1f, d * 0.5f), new Vector3(w * 0.5f, 0.05f, 0.05f), r, 0.5f);
                        for (int i = 0; i <= posts * 2; i++)
                            mb.AddBox((int)M.WoodDark, floor + new Vector3(-w * 0.5f + i * w / (posts * 2), 0.65f, d * 0.5f), new Vector3(0.04f, 0.45f, 0.04f), r, 0.5f);
                        for (int step = 0; step < 3; step++) // tiered benches
                            mb.AddBox((int)M.WoodLight, floor + new Vector3(0f, 0.45f + step * 0.45f, d * 0.2f - step * 0.9f), new Vector3(w * 0.45f, 0.06f, 0.3f), r, 0.4f);
                    }
                    else
                        mb.AddBox((int)M.WoodLight, floor + new Vector3(0f, 0.45f, -d * 0.25f), new Vector3(w * 0.4f, 0.05f, 0.25f), r, 0.4f);
                    Roof(mb, M.RoofTiles, floor, r, w, d, h, roof == "flat" ? "flat" : roof, pavilion ? 1.2f : 0.7f);
                    if (pavilion) Frustum(mb, M.WoodDark, floor + Vector3.up * (h + Mathf.Min(w, d) * 0.32f), 0.2f, 0.05f, 0.8f, 8);
                    return;
                }
            }

            // A closed building: walls, windows, doors, roof.
            mb.AddBox((int)wall, floor + new Vector3(0f, h * 0.5f, 0f), new Vector3(w * 0.5f, h * 0.5f, d * 0.5f), r, 0.25f);
            if (material == "timber") // posts and a sill beam frame the timber walls
            {
                for (int i = 0; i <= Mathf.RoundToInt(w / 2.5f); i++)
                {
                    float x = -w * 0.5f + i * w / Mathf.Max(1, Mathf.RoundToInt(w / 2.5f));
                    mb.AddBox((int)M.WoodLight, floor + new Vector3(x, h * 0.5f, d * 0.5f + 0.05f), new Vector3(0.1f, h * 0.5f, 0.06f), r, 0.5f);
                }
                mb.AddBox((int)M.WoodLight, floor + new Vector3(0f, h * 0.55f, d * 0.5f + 0.06f), new Vector3(w * 0.5f, 0.08f, 0.06f), r, 0.5f);
            }
            float pitch = type == "office" ? 2.6f : type == "machine-hall" || type == "pump-house" ? 4.5f : 3.2f;
            float paneH = type == "machine-hall" || type == "pump-house" ? Mathf.Min(3.2f, h * 0.3f) : 1.3f;
            Windows(mb, floor, r, w, d, 0f, h, pitch, lit, type == "pump-house" ? 1.2f : 1.4f, paneH);
            // Doors facing the road.
            switch (type)
            {
                case "shed":
                case "machine-hall":
                {
                    int doors = Mathf.Max(1, Mathf.RoundToInt(w / 14f));
                    float dw = Mathf.Min(6f, w / (doors * 1.6f)), dh = Mathf.Min(h * 0.6f, 6f);
                    for (int i = 0; i < doors; i++)
                    {
                        float x = -w * 0.5f + (i + 0.5f) * w / doors;
                        mb.AddBox((int)M.SteelGrey, floor + new Vector3(x, dh * 0.5f, d * 0.5f + 0.06f), new Vector3(dw * 0.5f, dh * 0.5f, 0.05f), r, 0.3f);
                        for (int rib = 1; rib < 8; rib++) // roller-door ribs
                            mb.AddBox((int)M.Graphite, floor + new Vector3(x, rib * dh / 8f, d * 0.5f + 0.12f), new Vector3(dw * 0.5f, 0.03f, 0.02f), r, 0.5f);
                        mb.AddBox((int)M.SteelYellow, floor + new Vector3(x, dh + 0.25f, d * 0.5f + 0.15f), new Vector3(dw * 0.55f, 0.2f, 0.12f), r, 0.5f);
                    }
                    if (type == "machine-hall") // roof vents and a stack
                    {
                        for (int i = -1; i <= 1; i++)
                            mb.AddBox((int)M.SteelGrey, floor + new Vector3(i * w * 0.3f, h + 1.2f, 0f), new Vector3(1.5f, 0.8f, 1.5f), r, 0.4f);
                        Frustum(mb, M.Concrete, floor + new Vector3(w * 0.45f, 0f, -d * 0.3f), 1.4f, 1.0f, h + 12f, 14);
                    }
                    break;
                }
                case "terminal":
                {
                    // A lit glass front under a canopy, with mullions.
                    mb.AddBox((int)(lit ? M.WindowLit : M.Graphite), floor + new Vector3(0f, h * 0.42f, d * 0.5f + 0.08f), new Vector3(w * 0.46f, h * 0.38f, 0.04f), r, 0.3f);
                    for (int i = 0; i <= 12; i++)
                        mb.AddBox((int)M.SteelGrey, floor + new Vector3(-w * 0.46f + i * w * 0.92f / 12f, h * 0.42f, d * 0.5f + 0.14f), new Vector3(0.1f, h * 0.38f, 0.06f), r, 0.5f);
                    mb.AddBox((int)M.SteelGrey, floor + new Vector3(0f, h * 0.84f, d * 0.5f + 2.5f), new Vector3(w * 0.5f, 0.2f, 2.6f), r, 0.3f);
                    break;
                }
                case "pump-house":
                {
                    mb.AddBox((int)M.WoodDark, floor + new Vector3(0f, 1.6f, d * 0.5f + 0.05f), new Vector3(1.2f, 1.6f, 0.06f), r, 0.5f);
                    // Big pipes leaving the back of the house toward the water.
                    foreach (float x in new[] { -w * 0.25f, w * 0.25f })
                        Beam(mb, M.SteelGrey, floor + new Vector3(x, 1.2f, -d * 0.5f), floor + new Vector3(x, -3f, -d * 0.5f - 18f), 0.6f);
                    Frustum(mb, M.Brick, floor + new Vector3(w * 0.35f, h, -d * 0.25f), 0.7f, 0.55f, h * 0.9f, 10);
                    break;
                }
                default:
                    mb.AddBox((int)M.WoodDark, floor + new Vector3(0f, 1.1f, d * 0.5f + 0.05f), new Vector3(0.6f, 1.1f, 0.06f), r, 0.5f);
                    break;
            }
            if (type == "station")
            {
                // A platform along the road side with a canopy on posts and a name board.
                mb.AddBox((int)M.Concrete, floor + new Vector3(0f, -0.15f, d * 0.5f + 2.5f), new Vector3(w * 0.5f + 6f, 0.45f, 2.5f), r, 0.3f);
                mb.AddBox((int)M.OffWhite, floor + new Vector3(0f, 0.31f, d * 0.5f + 4.9f), new Vector3(w * 0.5f + 6f, 0.02f, 0.12f), r, 0.5f);
                for (int i = 0; i <= 6; i++)
                    mb.AddBox((int)M.SteelGrey, floor + new Vector3(-w * 0.5f - 4f + i * (w + 8f) / 6f, h * 0.4f, d * 0.5f + 3.6f), new Vector3(0.1f, h * 0.4f, 0.1f), r, 0.5f);
                mb.AddBox((int)M.MetalRoof, floor + new Vector3(0f, h * 0.8f + 0.1f, d * 0.5f + 2.4f), new Vector3(w * 0.5f + 6f, 0.1f, 2.6f), Quaternion.Euler(-6f, 0f, 0f), 0.3f);
                mb.AddBox((int)M.OffWhite, floor + new Vector3(0f, h * 0.8f - 0.6f, d * 0.5f + 4.6f), new Vector3(1.8f, 0.3f, 0.04f), r, 0.5f);
            }
            if (type == "workshop" && S(lm, "feature", "") == "bell-casting-wheel")
            {
                // The casting wheel beside the workshop: a large spoked wheel on a timber frame.
                Vector3 hub = floor + new Vector3(w * 0.5f + 3.5f, 3.2f, d * 0.5f + 2.5f);
                Quaternion face = Quaternion.Euler(0f, 90f, 0f);
                for (int i = 0; i < 16; i++)
                {
                    float a0 = i * Mathf.PI * 2f / 16f, a1 = (i + 1) * Mathf.PI * 2f / 16f;
                    Beam(mb, M.WoodDark, hub + face * new Vector3(Mathf.Cos(a0) * 2.8f, Mathf.Sin(a0) * 2.8f, 0f), hub + face * new Vector3(Mathf.Cos(a1) * 2.8f, Mathf.Sin(a1) * 2.8f, 0f), 0.12f);
                    if (i % 2 == 0) Beam(mb, M.WoodDark, hub, hub + face * new Vector3(Mathf.Cos(a0) * 2.8f, Mathf.Sin(a0) * 2.8f, 0f), 0.07f);
                }
                foreach (float z in new[] { -0.6f, 0.6f })
                {
                    Beam(mb, M.WoodDark, floor + new Vector3(w * 0.5f + 3.5f + z, 0f, d * 0.5f + 1f), hub, 0.1f);
                    Beam(mb, M.WoodDark, floor + new Vector3(w * 0.5f + 3.5f + z, 0f, d * 0.5f + 4f), hub, 0.1f);
                }
                mb.AddBox((int)M.SteelGrey, floor + new Vector3(w * 0.5f + 3.5f, 0.6f, d * 0.5f + 2.5f), new Vector3(0.7f, 0.6f, 0.7f), r, 0.4f);
            }
            if (type == "shrine-roof")
            {
                // A deep two-tier roof with a ridge crest on a stone base: the roof is the landmark.
                mb.AddBox((int)M.Stone, floor + new Vector3(0f, -0.1f, 0f), new Vector3(w * 0.5f + 2f, 0.5f, d * 0.5f + 2f), r, 0.4f);
                Roof(mb, M.RoofTiles, floor, r, w, d, h * 0.62f, "hip", 2.2f);
                mb.AddBox((int)M.WoodDark, floor + new Vector3(0f, h * 0.62f + d * 0.32f * 0.5f + 0.3f, 0f), new Vector3(w * 0.3f, d * 0.08f, d * 0.3f), r, 0.4f);
                Roof(mb, M.RoofTiles, floor + Vector3.up * (h * 0.62f + d * 0.18f), r, w * 0.6f, d * 0.55f, d * 0.1f, "hip", 1f);
                mb.AddBox((int)M.WoodDark, floor + new Vector3(0f, h * 0.62f + d * 0.18f + d * 0.1f + d * 0.55f * 0.32f + 0.2f, 0f), new Vector3(w * 0.3f + 0.6f, 0.25f, 0.3f), r, 0.4f);
                return;
            }
            Roof(mb, roof == "flat" ? M.Concrete : material == "steel" ? M.MetalRoof : roofMat, floor, r, w, d, h, roof, roof == "flat" ? 0f : 0.7f);
        }

        /// <summary>A substation: steel gantries carrying conductors over transformers and insulator stacks, inside a fence.</summary>
        static void Switchyard(MeshBuilder mb, Vector3 floor, float w, float d, float h)
        {
            Quaternion r = Quaternion.identity;
            mb.AddBox((int)M.Concrete, floor + new Vector3(0f, -0.05f, 0f), new Vector3(w * 0.5f, 0.1f, d * 0.5f), r, 0.2f);
            int bays = Mathf.Max(2, Mathf.RoundToInt(w / 14f));
            for (int i = 0; i <= bays; i++)
            {
                float x = -w * 0.45f + i * w * 0.9f / bays;
                foreach (float z in new[] { -d * 0.3f, d * 0.3f })
                    Lattice(mb, M.SteelGrey, floor + new Vector3(x, 0f, z), r, 0.6f, 0.35f, h, 0.06f);
                Beam(mb, M.SteelGrey, floor + new Vector3(x, h, -d * 0.3f), floor + new Vector3(x, h, d * 0.3f), 0.25f);
                if (i < bays)
                {
                    float x1 = -w * 0.45f + (i + 1) * w * 0.9f / bays;
                    for (int c = -1; c <= 1; c++) Cable(mb, M.Graphite, floor + new Vector3(x, h - 0.4f, c * d * 0.12f), floor + new Vector3(x1, h - 0.4f, c * d * 0.12f), 0.8f, 0.03f);
                    Vector3 tx = floor + new Vector3((x + x1) * 0.5f, 0f, 0f);
                    mb.AddBox((int)M.SteelGrey, tx + new Vector3(0f, 1.6f, 0f), new Vector3(2.2f, 1.6f, 1.6f), r, 0.3f);
                    for (int f = -3; f <= 3; f++) mb.AddBox((int)M.Graphite, tx + new Vector3(f * 0.6f, 1.6f, 1.7f), new Vector3(0.05f, 1.3f, 0.12f), r, 0.5f);
                    for (int c = -1; c <= 1; c++)
                    {
                        Vector3 ins = tx + new Vector3(c * 1.2f, 3.2f, 0f);
                        for (int disc = 0; disc < 5; disc++) Frustum(mb, M.OffWhite, ins + Vector3.up * disc * 0.28f, 0.22f, 0.22f, 0.12f, 10);
                        Cable(mb, M.Graphite, ins + Vector3.up * 1.4f, floor + new Vector3(tx.x - floor.x + c * 1.2f, h - 0.4f, c * d * 0.12f), 0.3f, 0.02f, 4);
                    }
                }
            }
            // Perimeter fence.
            Vector3[] corners =
            {
                floor + new Vector3(-w * 0.5f, 0f, -d * 0.5f), floor + new Vector3(w * 0.5f, 0f, -d * 0.5f),
                floor + new Vector3(w * 0.5f, 0f, d * 0.5f), floor + new Vector3(-w * 0.5f, 0f, d * 0.5f),
            };
            for (int k = 0; k < 4; k++)
            {
                Vector3 a = corners[k], b = corners[(k + 1) % 4];
                int posts = Mathf.Max(2, Mathf.RoundToInt((b - a).magnitude / 3f));
                for (int i = 0; i <= posts; i++)
                    mb.AddBox((int)M.SteelGrey, Vector3.Lerp(a, b, i / (float)posts) + Vector3.up * 1.1f, new Vector3(0.04f, 1.1f, 0.04f), r, 0.5f);
                foreach (float y in new[] { 0.3f, 1.2f, 2.1f }) Beam(mb, M.SteelGrey, a + Vector3.up * y, b + Vector3.up * y, 0.015f);
            }
        }

        // ------------------------------------------------------------------ gates

        static GameObject Gate(TrackData track, RouteLandmarkDef lm, TerrainCollider ground, Transform parent, CourseMaterialSet mats, GenerationProfile profile)
        {
            string type = TypeOf(lm);
            Site site = SiteOf(track, lm, ground, 4f, 8f);
            var mb = Kit();
            Quaternion r = Quaternion.identity;
            Vector3 o = Vector3.zero;
            if (type == "storm-gate")
            {
                // A flood gate: two concrete towers, a steel leaf raised between them on its hoist beam, a channel below.
                foreach (float x in new[] { -5f, 5f })
                {
                    mb.AddBox((int)M.Concrete, o + new Vector3(x, 5f, 0f), new Vector3(1.4f, 5f, 2.2f), r, 0.3f);
                    mb.AddBox((int)M.Concrete, o + new Vector3(x, 10.6f, 0f), new Vector3(1.8f, 0.6f, 2.6f), r, 0.3f);
                    mb.AddBox((int)M.SteelYellow, o + new Vector3(x, 1.6f, 2.25f), new Vector3(1.45f, 0.15f, 0.05f), r, 0.5f); // flood marks
                }
                mb.AddBox((int)M.SteelGrey, o + new Vector3(0f, 11.5f, 0f), new Vector3(6.8f, 0.5f, 1.2f), r, 0.3f);
                mb.AddBox((int)M.SteelRed, o + new Vector3(0f, 7.2f, 0f), new Vector3(3.6f, 2.6f, 0.3f), r, 0.3f);
                for (int i = -2; i <= 2; i++) mb.AddBox((int)M.SteelGrey, o + new Vector3(i * 1.4f, 7.2f, 0.35f), new Vector3(0.08f, 2.5f, 0.08f), r, 0.5f);
                foreach (float x in new[] { -2.5f, 2.5f }) Beam(mb, M.Graphite, o + new Vector3(x, 11f, 0f), o + new Vector3(x, 9.8f, 0f), 0.04f);
                mb.AddBox((int)M.Water, o + new Vector3(0f, -0.4f, 0f), new Vector3(3.6f, 0.02f, 14f), r, 0.2f);
                mb.AddBox((int)M.Concrete, o + new Vector3(0f, -1.2f, 0f), new Vector3(3.8f, 0.8f, 14f), r, 0.2f);
            }
            else
            {
                // A roofed cedar gateway: four posts, tie beams, a small gabled roof, a plaque, lanterns on stone bases and
                // fence runs to either side. Original form (no curved lintel).
                foreach (float x in new[] { -2.4f, 2.4f })
                foreach (float z in new[] { -0.9f, 0.9f })
                    mb.AddBox((int)M.WoodDark, o + new Vector3(x, 2.3f, z), new Vector3(0.18f, 2.3f, 0.18f), r, 0.5f);
                foreach (float z in new[] { -0.9f, 0.9f })
                    mb.AddBox((int)M.WoodDark, o + new Vector3(0f, 3.9f, z), new Vector3(2.7f, 0.16f, 0.14f), r, 0.5f);
                foreach (float x in new[] { -2.4f, 2.4f })
                    mb.AddBox((int)M.WoodDark, o + new Vector3(x, 4.5f, 0f), new Vector3(0.14f, 0.14f, 1.1f), r, 0.5f);
                Roof(mb, M.RoofTiles, o, r, 5.6f, 2.4f, 4.65f, "gable", 0.8f);
                mb.AddBox((int)M.WoodLight, o + new Vector3(0f, 3.4f, 1.02f), new Vector3(0.7f, 0.35f, 0.04f), r, 0.5f);
                foreach (float x in new[] { -4.2f, 4.2f })
                {
                    mb.AddBox((int)M.Stone, o + new Vector3(x, 0.45f, 1.6f), new Vector3(0.35f, 0.45f, 0.35f), r, 0.5f);
                    mb.AddBox((int)M.LanternPaper, o + new Vector3(x, 1.2f, 1.6f), new Vector3(0.28f, 0.3f, 0.28f), r, 0.5f);
                    mb.AddBox((int)M.Stone, o + new Vector3(x, 1.58f, 1.6f), new Vector3(0.42f, 0.08f, 0.42f), r, 0.5f);
                    for (int i = 0; i < 5; i++)
                    {
                        float fx = x + Mathf.Sign(x) * (0.8f + i * 1.6f);
                        mb.AddBox((int)M.WoodDark, o + new Vector3(fx, 0.9f, 0f), new Vector3(0.07f, 0.9f, 0.07f), r, 0.5f);
                    }
                    Beam(mb, M.WoodLight, o + new Vector3(x + Mathf.Sign(x) * 0.8f, 1.5f, 0f), o + new Vector3(x + Mathf.Sign(x) * 7.2f, 1.5f, 0f), 0.05f);
                    Beam(mb, M.WoodLight, o + new Vector3(x + Mathf.Sign(x) * 0.8f, 0.8f, 0f), o + new Vector3(x + Mathf.Sign(x) * 7.2f, 0.8f, 0f), 0.05f);
                }
            }
            return Emit(lm.Name, mb, track, lm, "", mats, parent, site.Pos, site.Rot, site.Offset < 25f, GameLayers.Scenery, profile);
        }
    }
}
