using System.Collections.Generic;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Art
{
    /// <summary>
    /// Layered decals (Addendum 01 §13: up to 64 layers) drawn as thin procedural shapes on the body surface. Each layer is
    /// placed in its zone's surface frame (<see cref="CarBodyGenerator.DecalFrame"/>), lifted a little more than the layer
    /// below it so the order is visible and nothing z-fights; mirror adds the symmetric copy, flip reverses the shape.
    /// Digits and text are literal TextMeshPro (never markup).
    /// </summary>
    public static class CarDecals
    {
        public const int MaxLayers = 64;

        public static void Build(Transform body, CarBodyDef def, VehicleParams p, IReadOnlyList<CarDecal> decals, Material baseMaterial, List<Object> owned)
        {
            if (decals == null || decals.Count == 0) return;
            var root = new GameObject("Decals").transform;
            root.SetParent(body, false);
            var byColor = new Dictionary<Color, Material>();
            var mb = new Dictionary<Color, MeshBuilder>();
            CarBodyGenerator.BodySurface surface = CarBodyGenerator.Surface(def, p);
            int layer = 0;
            foreach (CarDecal d in decals)
            {
                if (layer >= MaxLayers) break;
                Place(root, surface, d, d.Zone, d.U, d.Flip, layer, byColor, mb, baseMaterial, owned);
                if (d.Mirror)
                {
                    string zone = d.Zone == "left" ? "right" : d.Zone == "right" ? "left" : d.Zone;
                    float u = zone == d.Zone ? 1f - d.U : d.U;
                    Place(root, surface, d, zone, u, !d.Flip, layer, byColor, mb, baseMaterial, owned);
                }
                layer++;
            }
            foreach (KeyValuePair<Color, MeshBuilder> kv in mb)
            {
                var go = new GameObject("DecalLayer");
                go.transform.SetParent(root, false);
                Mesh m = kv.Value.Build("decals");
                go.AddComponent<MeshFilter>().sharedMesh = m;
                go.AddComponent<MeshRenderer>().sharedMaterial = byColor[kv.Key];
            }
        }

        static void Place(Transform root, CarBodyGenerator.BodySurface surface, CarDecal d, string zone, float u, bool flip, int layer,
            Dictionary<Color, Material> byColor, Dictionary<Color, MeshBuilder> builders, Material baseMaterial, List<Object> owned)
        {
            if (!surface.Frame(zone, u, d.V, out Vector3 pos, out Vector3 n, out Vector3 t)) return;
            Vector3 b = Vector3.Cross(n, t).normalized;
            float scale = Mathf.Clamp(d.Scale, 0.05f, 1.6f);
            float lift = 0.004f + layer * 0.0006f;
            Quaternion spin = Quaternion.AngleAxis(d.RotationDeg, n);
            Vector3 right = spin * t * (flip ? -1f : 1f), up = spin * b;
            Vector3 origin = pos + n * lift;

            if (d.Render == "digit" || d.Render == "text")
            {
                var go = new GameObject("DecalText");
                go.transform.SetParent(root, false);
                go.transform.localPosition = origin;
                // TextMeshPro reads along +x, faces −z: map x → right, y → up, −z → the surface normal.
                go.transform.localRotation = Quaternion.LookRotation(-n, up);
                var tmp = go.AddComponent<TMPro.TextMeshPro>();
                tmp.richText = false;
                tmp.text = d.Glyph ?? "";
                tmp.color = d.Color;
                tmp.alignment = TMPro.TextAlignmentOptions.Center;
                // Scale = glyph height in metres (TextMeshPro 3D: about 0.075 m of cap height per font-size unit).
                tmp.fontSize = scale * 13f;
                tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                tmp.rectTransform.sizeDelta = new Vector2(scale * 8f, scale * 1.6f);
                tmp.ForceMeshUpdate();
                // Bake the lettering into a plain mesh wrapped onto the curved body (TextMeshPro would regenerate a flat
                // quad at render time), keep the font material, and drop the text component.
                Transform tr = go.transform;
                Mesh baked = Object.Instantiate(tmp.mesh);
                baked.name = "DecalLettering";
                Vector3[] vertices = baked.vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 bodyPoint = tr.localPosition + tr.localRotation * vertices[i];
                    Vector3 onSurface = surface.Conform(zone, bodyPoint, lift + 0.001f);
                    vertices[i] = Quaternion.Inverse(tr.localRotation) * (onSurface - tr.localPosition);
                }
                baked.vertices = vertices;
                baked.RecalculateBounds();
                owned.Add(baked);
                var lettering = new GameObject("DecalLettering");
                lettering.transform.SetParent(root, false);
                lettering.transform.localPosition = tr.localPosition;
                lettering.transform.localRotation = tr.localRotation;
                lettering.AddComponent<MeshFilter>().sharedMesh = baked;
                lettering.AddComponent<MeshRenderer>().sharedMaterial = tmp.fontSharedMaterial;
                if (Application.isPlaying) Object.Destroy(go);
                else Object.DestroyImmediate(go);
                return;
            }

            if (!byColor.ContainsKey(d.Color))
            {
                var m = new Material(baseMaterial) { name = "Decal" };
                m.SetColor("_BaseColor", d.Color);
                (float metallic, float smoothness) = CarAppearance.FinishValues("satin");
                m.SetFloat("_Metallic", metallic);
                m.SetFloat("_Smoothness", smoothness);
                owned.Add(m);
                byColor[d.Color] = m;
                builders[d.Color] = new MeshBuilder(1);
            }
            MeshBuilder mb = builders[d.Color];
            // Every vertex is conformed back onto the body, so large shapes follow the curvature.
            Vector3 P(float x, float y) => surface.Conform(zone, origin + (right * x + up * y) * scale, lift);
            void Tri(Vector3 a, Vector3 c, Vector3 e)
            {
                int i0 = mb.AddVertex(a, n, Vector2.zero), i1 = mb.AddVertex(c, n, Vector2.zero), i2 = mb.AddVertex(e, n, Vector2.zero);
                mb.AddTriangle(0, i0, i1, i2);
                mb.AddTriangle(0, i0, i2, i1); // both windings: visible whichever way the frame turned
            }
            void Rect(float x0, float y0, float x1, float y1)
            {
                Tri(P(x0, y0), P(x1, y0), P(x1, y1));
                Tri(P(x0, y0), P(x1, y1), P(x0, y1));
            }
            void Fan(IList<Vector2> ring)
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    Vector2 a = ring[i], c = ring[(i + 1) % ring.Count];
                    Tri(P(0f, 0f), P(a.x, a.y), P(c.x, c.y));
                }
            }
            void Annulus(float r0, float r1, float fromDeg, float toDeg, int segments)
            {
                for (int i = 0; i < segments; i++)
                {
                    float a0 = Mathf.Lerp(fromDeg, toDeg, i / (float)segments) * Mathf.Deg2Rad, a1 = Mathf.Lerp(fromDeg, toDeg, (i + 1) / (float)segments) * Mathf.Deg2Rad;
                    Vector3 i0 = P(Mathf.Cos(a0) * r0, Mathf.Sin(a0) * r0), o0 = P(Mathf.Cos(a0) * r1, Mathf.Sin(a0) * r1);
                    Vector3 i1 = P(Mathf.Cos(a1) * r0, Mathf.Sin(a1) * r0), o1 = P(Mathf.Cos(a1) * r1, Mathf.Sin(a1) * r1);
                    Tri(i0, o0, o1);
                    Tri(i0, o1, i1);
                }
            }
            List<Vector2> Polygon(int sides, float radius, float innerRadius = -1f, float phaseDeg = 90f)
            {
                var ring = new List<Vector2>();
                int count = innerRadius > 0f ? sides * 2 : sides;
                for (int i = 0; i < count; i++)
                {
                    float r = innerRadius > 0f && i % 2 == 1 ? innerRadius : radius;
                    float a = (phaseDeg + i * 360f / count) * Mathf.Deg2Rad;
                    ring.Add(new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r));
                }
                return ring;
            }

            switch (d.Render)
            {
                case "twin-stripe": Rect(-0.5f, 0.06f, 0.5f, 0.13f); Rect(-0.5f, -0.13f, 0.5f, -0.06f); break;
                case "pinstripe": Rect(-0.5f, -0.012f, 0.5f, 0.012f); break;
                case "circle": Fan(Polygon(28, 0.5f)); break;
                case "ring": Annulus(0.36f, 0.5f, 0f, 360f, 32); break;
                case "arrow":
                    Rect(-0.5f, -0.08f, 0.1f, 0.08f);
                    Tri(P(0.1f, 0.28f), P(0.5f, 0f), P(0.1f, -0.28f));
                    break;
                case "chevron":
                    Tri(P(-0.4f, 0.4f), P(0.05f, 0f), P(-0.15f, 0.4f));
                    Tri(P(-0.15f, 0.4f), P(0.05f, 0f), P(0.3f, 0f));
                    Tri(P(-0.4f, -0.4f), P(-0.15f, -0.4f), P(0.05f, 0f));
                    Tri(P(-0.15f, -0.4f), P(0.3f, 0f), P(0.05f, 0f));
                    break;
                case "star": Fan(Polygon(5, 0.5f, 0.2f)); break;
                case "bars": for (int i = 0; i < 4; i++) Rect(-0.5f + i * 0.27f, -0.22f, -0.34f + i * 0.27f, 0.22f); break;
                case "arcs": Annulus(0.18f, 0.24f, 30f, 150f, 12); Annulus(0.31f, 0.37f, 30f, 150f, 14); Annulus(0.44f, 0.5f, 30f, 150f, 16); break;
                case "flame":
                    for (int i = 0; i < 4; i++)
                    {
                        float y = -0.24f + i * 0.16f, len = 0.55f + 0.35f * Mathf.Sin(i * 1.7f + 0.5f);
                        Tri(P(-0.5f, y - 0.07f), P(-0.5f + len, y + 0.03f), P(-0.5f, y + 0.08f));
                    }
                    break;
                case "hex": Fan(Polygon(6, 0.5f, -1f, 0f)); break;
                default: Rect(-0.5f, -0.07f, 0.5f, 0.07f); break; // stripe
            }
        }
    }
}
