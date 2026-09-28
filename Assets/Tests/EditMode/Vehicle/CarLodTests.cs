using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Art;
using NightSignal.Cameras;
using NightSignal.Content;
using NightSignal.UI;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// Car levels of detail (spec §15 vehicle LOD tiers; Addendum 03 "preserve correct own-car culling and LOD selection
    /// when switching from exterior to cockpit"). Every model's mid and far bodies are much lighter than the full body, keep
    /// its length, height, lamps and paint, and shade with real normals. The view's LODGroup shows the full body, plate and
    /// livery up close, the livery at mid distance and the wheels at every level; the driver's own car stays on the full
    /// body in all five views, and the fitted cockpit is never culled by the group.
    /// </summary>
    public sealed class CarLodTests
    {
        const float Dt = 1f / 60f;
        string folder;
        readonly List<Object> made = new List<Object>();
        ContentLibrary lib;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "ns-carlod-" + System.Guid.NewGuid().ToString("N"));
            DrivingPreferences.FolderOverride = folder;
            DrivingPreferences.ResetCache();
            lib = ContentLibrary.Load();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
            DrivingPreferences.FolderOverride = null;
            DrivingPreferences.ResetCache();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        static IEnumerable<string> CarIds()
        {
            foreach (var c in ContentLibrary.Load().Catalogue.Cars) yield return c.Id;
        }

        static int Triangles(Mesh m)
        {
            int n = 0;
            for (int s = 0; s < m.subMeshCount; s++) n += (int)m.GetIndexCount(s) / 3;
            return n;
        }

        [Test]
        public void LodBodies_AreLighter_AndKeepTheSilhouette([ValueSource(nameof(CarIds))] string carId)
        {
            VehicleParams p = lib.Params(carId, AssistSettings.Default);
            CarBodyDef def = lib.Body(carId);
            Mesh full = CarBodyGenerator.BuildBody(def, p);
            Mesh mid = CarBodyGenerator.BuildBody(def, p, null, false, 1);
            Mesh far = CarBodyGenerator.BuildBody(def, p, null, false, 2);
            made.Add(full);
            made.Add(mid);
            made.Add(far);
            int t0 = Triangles(full), t1 = Triangles(mid), t2 = Triangles(far);
            TestContext.WriteLine($"{carId}: triangles {t0} / {t1} / {t2}, vertices {full.vertexCount} / {mid.vertexCount} / {far.vertexCount}");
            Assert.That(t1, Is.LessThanOrEqualTo(t0 * 0.6f), $"{carId}: the mid body is at most 60 % of the full body");
            Assert.That(t2, Is.LessThanOrEqualTo(t0 * 0.35f), $"{carId}: the far body is at most 35 % of the full body");
            foreach (Mesh m in new[] { mid, far })
            {
                Assert.That(m.subMeshCount, Is.EqualTo(full.subMeshCount));
                // Paint, glass, trim and both lamp sets on every level: the car still reads, and glows, at night.
                foreach (int sub in new[] { 0, 1, 2, 3, 4 })
                    Assert.That(m.GetSubMesh(sub).indexCount, Is.GreaterThan(0), $"{carId} {m.name}: submesh {sub} empty");
                Assert.That(m.bounds.size.z, Is.EqualTo(full.bounds.size.z).Within(0.01f), $"{carId} {m.name}: length");
                Assert.That(m.bounds.size.y, Is.EqualTo(full.bounds.size.y).Within(0.01f), $"{carId} {m.name}: height");
                Assert.That(m.bounds.center.z, Is.EqualTo(full.bounds.center.z).Within(0.01f), $"{carId} {m.name}: placement");
                Assert.That(m.bounds.min.y, Is.GreaterThanOrEqualTo(-0.001f), $"{carId} {m.name}: below the ground");
                Vector3[] v = m.vertices, nm = m.normals;
                for (int sub = 0; sub < m.subMeshCount; sub++)
                {
                    int[] t = m.GetTriangles(sub);
                    for (int k = 0; k < t.Length; k += 3)
                    {
                        if (Vector3.Cross(v[t[k + 1]] - v[t[k]], v[t[k + 2]] - v[t[k]]).sqrMagnitude < 1e-14f) continue;
                        for (int j = 0; j < 3; j++)
                            Assert.That(nm[t[k + j]].sqrMagnitude, Is.GreaterThan(0.5f), $"{carId} {m.name} submesh {sub}: zero or NaN normal");
                    }
                }
            }
            // The mid body keeps the mirrors (same width) and the seats through the glass; the far body drops both (its
            // interior submesh is only the wheel-well liners).
            Assert.That(mid.bounds.size.x, Is.EqualTo(full.bounds.size.x).Within(0.005f), $"{carId}: mid body keeps the mirrors");
            Assert.That(far.bounds.size.x, Is.LessThan(full.bounds.size.x - 0.05f), $"{carId}: far body without mirrors");
            Assert.That(far.GetSubMesh(8).indexCount, Is.LessThan(mid.GetSubMesh(8).indexCount), $"{carId}: far body leaves out the seats");
        }

        VehicleView Car(string carId, bool livery)
        {
            VehicleParams p = lib.Params(carId, AssistSettings.Default);
            CarAppearance a = CarAppearance.Stock(Color.red);
            if (livery)
            {
                a.Decals.Add(new CarDecal { Render = "stripe", Glyph = "", Zone = "left", U = 0.5f, V = 0.5f, Scale = 1f, Color = Color.white, Mirror = true });
                a.PlateText = "NS 0451";
            }
            VehicleView view = VehicleView.Create("LodTestCar", p, lib.Body(carId), Resources.Load<CarMaterialSet>("CarMaterialSet"), Color.red, a);
            made.Add(view.gameObject);
            view.transform.SetPositionAndRotation(new Vector3(0f, p.CgHeightM + 500f, 0f), Quaternion.identity); // clear of any scene collider
            VehicleState rest = VehicleState.AtRest(view.transform.position, view.transform.rotation);
            view.Render(rest, rest, 1f, default, 0f);
            return view;
        }

        [Test]
        public void View_LodGroup_ShowsTheRightPartsAtEachLevel()
        {
            VehicleView car = Car(lib.Catalogue.Cars[0].Id, livery: true);
            LODGroup g = car.Lods;
            Assert.That(g, Is.Not.Null);
            LOD[] lods = g.GetLODs();
            Assert.That(lods.Length, Is.EqualTo(3));
            Assert.That(lods[0].screenRelativeTransitionHeight, Is.GreaterThan(lods[1].screenRelativeTransitionHeight));
            Assert.That(lods[1].screenRelativeTransitionHeight, Is.GreaterThan(lods[2].screenRelativeTransitionHeight));

            Renderer bodyR = car.Body.GetComponent<MeshRenderer>();
            Renderer mid = car.Body.Find("BodyLod1").GetComponent<Renderer>();
            Renderer far = car.Body.Find("BodyLod2").GetComponent<Renderer>();
            Renderer[] livery = car.Body.Find("Decals").GetComponentsInChildren<Renderer>();
            Renderer[] plate = car.Body.Find("Plate").GetComponentsInChildren<Renderer>();
            Renderer[] wheels = Enumerable.Range(0, 4).SelectMany(i => car.transform.Find($"Wheel{i}").GetComponentsInChildren<Renderer>()).ToArray();
            Assert.That(livery, Is.Not.Empty);
            Assert.That(plate, Is.Not.Empty);
            Assert.That(wheels.Length, Is.EqualTo(4));

            Assert.That(lods[0].renderers, Does.Contain(bodyR).And.No.Member(mid).And.No.Member(far));
            Assert.That(lods[1].renderers, Does.Contain(mid).And.No.Member(bodyR).And.No.Member(far));
            Assert.That(lods[2].renderers, Does.Contain(far).And.No.Member(bodyR).And.No.Member(mid));
            foreach (Renderer w in wheels)
                for (int i = 0; i < 3; i++) Assert.That(lods[i].renderers, Does.Contain(w), $"wheel at LOD{i}");
            foreach (Renderer r in livery)
            {
                Assert.That(lods[0].renderers, Does.Contain(r), "livery up close");
                Assert.That(lods[1].renderers, Does.Contain(r), "livery at mid distance");
                Assert.That(lods[2].renderers, Has.No.Member(r), "no livery far away");
            }
            foreach (Renderer r in plate)
            {
                Assert.That(lods[0].renderers, Does.Contain(r));
                Assert.That(lods[1].renderers, Has.No.Member(r));
            }
            // Every renderer of the car belongs to some level: nothing is left drawing at every distance.
            var grouped = new HashSet<Renderer>(lods.SelectMany(l => l.renderers));
            foreach (Renderer r in car.GetComponentsInChildren<Renderer>(true))
                Assert.That(grouped, Does.Contain(r), $"{r.name} is outside the LOD group");
            // The mid and far bodies paint with the same materials as the full body.
            Assert.That(mid.sharedMaterials, Is.EqualTo(bodyR.sharedMaterials));
            Assert.That(far.sharedMaterials, Is.EqualTo(bodyR.sharedMaterials));
        }

        /// <summary>Unity's LOD choice for a perspective camera: the group's world size over the view height at its distance.</summary>
        static int SelectedLod(LODGroup g, Camera cam, float lodBias)
        {
            Vector3 s = g.transform.lossyScale;
            float size = g.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            float d = Vector3.Distance(cam.transform.position, g.transform.TransformPoint(g.localReferencePoint));
            float h = size * 0.5f / (Mathf.Max(d, 1e-4f) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) * lodBias;
            LOD[] lods = g.GetLODs();
            for (int i = 0; i < lods.Length; i++)
                if (h >= lods[i].screenRelativeTransitionHeight) return i;
            return -1;
        }

        [Test]
        public void OwnCar_StaysOnTheFullBody_InEveryView_AndTheCockpitIsNeverCulled()
        {
            VehicleView car = Car(lib.Catalogue.Cars[0].Id, livery: false);
            Mesh closed = car.Body.GetComponent<MeshFilter>().sharedMesh;
            foreach (DrivingView v in System.Enum.GetValues(typeof(DrivingView)))
            {
                var go = new GameObject("LodTestRig", typeof(Camera));
                made.Add(go);
                DrivingCamera cam = go.AddComponent<DrivingCamera>();
                cam.SetView(v, save: false);
                cam.SetTarget(car);
                cam.SetMotion(new CameraMotion());
                for (int i = 0; i < 120; i++) cam.Step(Dt);
                // The smallest LOD bias of any quality level (1): the full body at that bias is the full body at every level.
                Assert.That(SelectedLod(car.Lods, cam.Camera, 1f), Is.EqualTo(0), $"{v}: own car on the full body");
                Object.DestroyImmediate(go);
            }
            car.SetCockpitMode(true);
            LOD[] lods = car.Lods.GetLODs();
            MeshFilter mf = car.Body.GetComponent<MeshFilter>();
            Assert.That(mf.sharedMesh, Is.Not.SameAs(closed), "cockpit view swaps to the open-cabin body");
            Assert.That(lods[0].renderers, Does.Contain(mf.GetComponent<Renderer>()), "the open-cabin body is the full level's body");
            Assert.That(car.Cockpit, Is.Not.Null);
            var grouped = new HashSet<Renderer>(lods.SelectMany(l => l.renderers));
            foreach (Renderer r in car.Cockpit.Root.GetComponentsInChildren<Renderer>(true))
                Assert.That(grouped, Has.No.Member(r), $"cockpit part {r.name} must never be culled by the car's LOD group");
            car.SetCockpitMode(false);
            Assert.That(mf.sharedMesh, Is.SameAs(closed));
        }
    }
}
