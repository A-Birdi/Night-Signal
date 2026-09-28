using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Content;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// The car art pass: every model builds a detailed body within budget with every submesh in use, and decals — even at
    /// the largest scale, at the corners of their zone — stay on the paintable panel instead of leaving the body.
    /// </summary>
    public sealed class CarBodyArtTests
    {
        static ContentLibrary Lib => ContentLibrary.Load();

        static IEnumerable<string> CarIds()
        {
            foreach (var c in ContentLibrary.Load().Catalogue.Cars) yield return c.Id;
        }

        [Test]
        public void Body_IsDetailedAndWithinBudget([ValueSource(nameof(CarIds))] string carId)
        {
            ContentLibrary lib = Lib;
            VehicleParams p = lib.Params(carId, AssistSettings.Default);
            CarBodyDef def = lib.Body(carId);
            Mesh closed = CarBodyGenerator.BuildBody(def, p);
            Mesh open = CarBodyGenerator.BuildBody(def, p, null, openCabin: true);
            Mesh wheel = CarBodyGenerator.BuildWheel(def);
            try
            {
                Assert.That(closed.subMeshCount, Is.EqualTo(9));
                // Paint, glass, trim, head lamps, tail lamps, chrome and interior are all drawn on the stock car.
                foreach (int sub in new[] { 0, 1, 2, 3, 4, 5, 8 })
                    Assert.That(closed.GetSubMesh(sub).indexCount, Is.GreaterThan(0), $"{carId}: submesh {sub} empty");
                Assert.That(closed.vertexCount, Is.InRange(3000, 20000), $"{carId}: body vertex budget");
                Assert.That(open.GetSubMesh(8).indexCount, Is.LessThan(closed.GetSubMesh(8).indexCount),
                    $"{carId}: the cockpit view's body leaves out the seats the fitted cockpit replaces");
                Assert.That(wheel.vertexCount, Is.InRange(500, 6000), $"{carId}: wheel vertex budget");
                // Every drawn triangle shades with a real normal: a zero normal (a face wound both ways on shared vertices)
                // or a non-finite value renders NaN, which bloom spreads into a white disc.
                foreach (Mesh m in new[] { closed, open, wheel })
                {
                    Vector3[] v = m.vertices, nm = m.normals;
                    for (int sub = 0; sub < m.subMeshCount; sub++)
                    {
                        int[] t = m.GetTriangles(sub);
                        for (int k = 0; k < t.Length; k += 3)
                        {
                            if (Vector3.Cross(v[t[k + 1]] - v[t[k]], v[t[k + 2]] - v[t[k]]).sqrMagnitude < 1e-14f) continue;
                            for (int j = 0; j < 3; j++)
                            {
                                Vector3 q = v[t[k + j]], nn = nm[t[k + j]];
                                Assert.That(float.IsNaN(q.x + q.y + q.z) || float.IsInfinity(q.x + q.y + q.z), Is.False, $"{carId} {m.name}: non-finite vertex");
                                Assert.That(nn.sqrMagnitude, Is.GreaterThan(0.5f), $"{carId} {m.name} submesh {sub}: zero or NaN normal at {q}");
                            }
                        }
                    }
                }
                // Nothing of the body reaches below the ground or ahead of the bumper camera.
                CarBodyGenerator.CabinFrame f = CarBodyGenerator.Cabin(def, p);
                Assert.That(closed.bounds.min.y, Is.GreaterThanOrEqualTo(-0.001f), $"{carId}: below the ground");
                Assert.That(closed.bounds.max.z, Is.LessThan(f.Bumper.z - 0.02f), $"{carId}: body reaches the bumper camera");
            }
            finally
            {
                Object.DestroyImmediate(closed);
                Object.DestroyImmediate(open);
                Object.DestroyImmediate(wheel);
            }
        }

        [Test]
        public void Decals_AtMaximumScale_StayOnTheirPanel([ValueSource(nameof(CarIds))] string carId)
        {
            ContentLibrary lib = Lib;
            VehicleParams p = lib.Params(carId, AssistSettings.Default);
            CarBodyDef def = lib.Body(carId);
            CarBodyGenerator.BodySurface surface = CarBodyGenerator.Surface(def, p);
            var owned = new List<Object>();
            var failures = new List<string>();
            var baseMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            owned.Add(baseMaterial);
            foreach (string zone in new[] { "left", "right", "hood", "roof", "front", "rear" })
            foreach (string render in new[] { "circle", "stripe", "text" })
            foreach (float u in new[] { 0f, 0.5f, 1f })
            {
                if (zone == "roof" && def.Style == "roadster") continue;
                var root = new GameObject("DecalTest").transform;
                try
                {
                    var decal = new CarDecal { Render = render, Glyph = "NIGHT", Zone = zone, U = u, V = u == 0.5f ? 0.5f : 1f - u, Scale = 1.6f, Color = Color.red };
                    CarDecals.Build(root, def, p, new List<CarDecal> { decal }, baseMaterial, owned);
                    int drawn = 0;
                    foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>())
                    {
                        Mesh m = mf.sharedMesh;
                        Vector3[] v = m.vertices;
                        foreach (int t in m.triangles)
                        {
                            Vector3 body = root.InverseTransformPoint(mf.transform.TransformPoint(v[t]));
                            if (!surface.OnZone(zone, body)) { failures.Add($"{zone} {render} u={u}: vertex {body} off the panel"); break; }
                            drawn++;
                        }
                    }
                    if (u == 0.5f && render != "text" && drawn == 0) failures.Add($"{zone} {render}: a centred decal drew nothing");
                }
                finally
                {
                    Object.DestroyImmediate(root.gameObject);
                }
            }
            foreach (Object o in owned) Object.DestroyImmediate(o);
            Assert.That(failures, Is.Empty, carId + ":\n" + string.Join("\n", failures));
        }
    }
}
