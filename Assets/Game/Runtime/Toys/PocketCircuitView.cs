using System.Collections.Generic;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.PocketCircuit;
using UnityEngine;
using UnityEngine.Rendering;

namespace NightSignal.Toys
{
    /// <summary>
    /// The Pocket Circuit tabletop, built procedurally from the Core <see cref="SlotTrack"/> geometry (the same sampled
    /// lanes the simulation drives): a workshop table under a lamp, the plastic track with six slots and borders, piers
    /// under raised sections, a start gantry and one toy car per lane. Toy units are metres with Z up; Unity is Y up.
    /// Presentation only — positions come from the authoritative table state every frame.
    /// </summary>
    public sealed class PocketCircuitView : MonoBehaviour
    {
        /// <summary>Far below any course so the table never meets course geometry, fog or shadows.</summary>
        public static readonly Vector3 Origin = new Vector3(0f, -6000f, 0f);

        public static readonly Color[] LaneColours =
        {
            new Color(0.85f, 0.15f, 0.18f), new Color(0.15f, 0.45f, 0.9f), new Color(0.95f, 0.75f, 0.15f),
            new Color(0.2f, 0.7f, 0.35f), new Color(0.92f, 0.92f, 0.9f), new Color(0.6f, 0.3f, 0.8f),
        };

        public Camera Camera { get; private set; }
        public bool ChaseView;
        SlotTrack track;
        Transform trackRoot;
        readonly Dictionary<int, Transform> carsByLane = new Dictionary<int, Transform>();
        readonly List<Material> materials = new List<Material>();
        Vector3 centre;
        float span;
        Vector3 camPos;
        Quaternion camRot;
        bool camPlaced;

        public static PocketCircuitView Create(SlotTrack track)
        {
            var go = new GameObject("PocketCircuitTable");
            go.transform.position = Origin;
            var view = go.AddComponent<PocketCircuitView>();
            view.Build(track);
            return view;
        }

        /// <summary>Rebuilds the track after a consented layout change (boards of every layout are kept by Core).</summary>
        public void SetTrack(SlotTrack t)
        {
            if (t == track) return;
            if (trackRoot != null) Destroy(trackRoot.gameObject);
            foreach (Transform c in carsByLane.Values) if (c != null) Destroy(c.gameObject);
            carsByLane.Clear();
            BuildTrack(t);
        }

        void Build(SlotTrack t)
        {
            // Room: dark workshop, a warm lamp over the table and a cool fill, own camera.
            var camGo = new GameObject("TableCamera", typeof(Camera));
            camGo.transform.SetParent(transform, false);
            Camera = camGo.GetComponent<Camera>();
            Cameras.CameraRig.Configure(Camera);
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = new Color(0.03f, 0.03f, 0.04f);
            Camera.nearClipPlane = 0.02f;
            Camera.farClipPlane = 60f;
            Camera.fieldOfView = 42f;
            BuildTrack(t);
            AddLight("Lamp", centre + new Vector3(0.2f, 2.2f, -0.3f), new Color(1f, 0.86f, 0.68f), 3.2f, 6f, LightType.Point, true);
            AddLight("Fill", centre + new Vector3(-2.5f, 1.6f, 2f), new Color(0.55f, 0.65f, 0.85f), 0.9f, 8f, LightType.Point, false);
        }

        void AddLight(string name, Vector3 pos, Color c, float intensity, float range, LightType type, bool shadows)
        {
            var go = new GameObject(name, typeof(Light));
            go.transform.SetParent(transform, false);
            go.transform.position = pos;
            Light l = go.GetComponent<Light>();
            l.type = type;
            l.color = c;
            l.intensity = intensity;
            l.range = range;
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        }

        void BuildTrack(SlotTrack t)
        {
            track = t;
            trackRoot = new GameObject("Track").transform;
            trackRoot.SetParent(transform, false);
            Bounds b = new Bounds(ToUnity(t.Centreline[0]), Vector3.zero);
            foreach (Vec3 p in t.Centreline) b.Encapsulate(ToUnity(p));
            centre = transform.position + new Vector3(b.center.x, 0f, b.center.z);
            span = Mathf.Max(b.size.x, b.size.z);
            camPlaced = false;

            // Table top and legs.
            float tw = b.size.x + 0.9f, td = b.size.z + 0.9f;
            Box("TableTop", new Vector3(b.center.x, -0.025f, b.center.z), new Vector3(tw, 0.05f, td), Mat(new Color(0.32f, 0.22f, 0.14f), 0.35f));
            Box("TableMat", new Vector3(b.center.x, 0.001f, b.center.z), new Vector3(tw - 0.16f, 0.002f, td - 0.16f), Mat(new Color(0.12f, 0.16f, 0.13f), 0.1f));
            for (int i = 0; i < 4; i++)
            {
                float sx = (i % 2 == 0 ? -1 : 1) * (tw * 0.5f - 0.08f), sz = (i < 2 ? -1 : 1) * (td * 0.5f - 0.08f);
                Box("Leg" + i, new Vector3(b.center.x + sx, -0.42f, b.center.z + sz), new Vector3(0.07f, 0.8f, 0.07f), Mat(new Color(0.25f, 0.17f, 0.11f), 0.3f));
            }

            // Track deck, border walls, slots, piers under raised pieces.
            float half = (float)((t.Layout.Lanes - 1) * t.Layout.LaneSpacing * 0.5 + SlotTrack.BorderWidth);
            Material deck = Mat(new Color(0.09f, 0.09f, 0.1f), 0.45f);
            Material border = Mat(new Color(0.78f, 0.76f, 0.72f), 0.3f);
            Material slot = Mat(new Color(0.01f, 0.01f, 0.01f), 0.1f);
            Material rail = Mat(new Color(0.75f, 0.7f, 0.6f), 0.8f);
            int stride = 3;
            var pts = new List<Vector3>();
            var rights = new List<Vector3>();
            for (int i = 0; i < t.Centreline.Length; i += stride)
            {
                pts.Add(ToUnity(t.Centreline[i]));
                rights.Add(RightAt(t.Lanes[0], i));
            }
            pts.Add(pts[0]);
            rights.Add(rights[0]);
            Ribbon("Deck", pts, rights, -half, half, 0.012f, deck, true);
            Ribbon("BorderL", pts, rights, -half - 0.018f, -half, 0.02f, border, false);
            Ribbon("BorderR", pts, rights, half, half + 0.018f, 0.02f, border, false);
            foreach (SlotLane lane in t.Lanes)
            {
                var lp = new List<Vector3>();
                var lr = new List<Vector3>();
                for (int i = 0; i < lane.Points.Length; i += stride) { lp.Add(ToUnity(lane.Points[i])); lr.Add(RightAt(lane, i)); }
                lp.Add(lp[0]);
                lr.Add(lr[0]);
                Ribbon("Slot" + lane.Number, lp, lr, -0.0025f, 0.0025f, 0.0136f, slot, false);
                Ribbon("RailL" + lane.Number, lp, lr, -0.009f, -0.0065f, 0.0142f, rail, false);
                Ribbon("RailR" + lane.Number, lp, lr, 0.0065f, 0.009f, 0.0142f, rail, false);
            }
            Material pier = Mat(new Color(0.55f, 0.55f, 0.58f), 0.5f);
            for (int i = 0; i < t.Centreline.Length; i += 30)
            {
                Vector3 p = ToUnity(t.Centreline[i]);
                if (p.y < 0.015f) continue;
                Box("Pier", new Vector3(p.x, p.y * 0.5f, p.z), new Vector3(0.03f, p.y, 0.03f), pier);
            }

            // Start/finish gantry across lap distance 0.
            Vector3 s0 = ToUnity(t.Centreline[0]);
            Vector3 r0 = RightAt(t.Lanes[0], 0);
            Quaternion rot = Quaternion.LookRotation(Vector3.Cross(r0, Vector3.up) * -1f, Vector3.up);
            Material white = Mat(new Color(0.95f, 0.95f, 0.95f), 0.2f);
            GameObject line = Box("StartLine", s0 + Vector3.up * 0.0146f, new Vector3(half * 2f, 0.001f, 0.012f), white);
            line.transform.rotation = rot;
            Material gantryMat = Mat(new Color(0.84f, 0.15f, 0.24f), 0.4f);
            foreach (float side in new[] { -1f, 1f })
                Box("GantryPost", s0 + r0 * side * (half + 0.03f) + Vector3.up * 0.09f, new Vector3(0.015f, 0.18f, 0.015f), gantryMat).transform.rotation = rot;
            GameObject beam = Box("GantryBeam", s0 + Vector3.up * 0.18f, new Vector3(half * 2f + 0.08f, 0.025f, 0.02f), gantryMat);
            beam.transform.rotation = rot;

            // One toy car per lane, shown only while a member holds that lane.
            foreach (SlotLane lane in t.Lanes)
            {
                Transform car = BuildCar(LaneColours[(lane.Number - 1) % LaneColours.Length]).transform;
                car.gameObject.SetActive(false);
                carsByLane[lane.Number] = car;
            }
        }

        GameObject BuildCar(Color colour)
        {
            var root = new GameObject("ToyCar");
            root.transform.SetParent(transform, false);
            Material body = Mat(colour, 0.75f);
            Material dark = Mat(new Color(0.05f, 0.05f, 0.05f), 0.2f);
            Material glass = Mat(new Color(0.1f, 0.14f, 0.18f), 0.9f);
            Part(root, "Body", new Vector3(0, 0.009f, 0), new Vector3(0.028f, 0.009f, 0.058f), body);
            Part(root, "Cabin", new Vector3(0, 0.0165f, -0.004f), new Vector3(0.022f, 0.007f, 0.026f), glass);
            Part(root, "Wing", new Vector3(0, 0.017f, -0.026f), new Vector3(0.03f, 0.002f, 0.008f), body);
            foreach (float x in new[] { -0.0145f, 0.0145f })
            foreach (float z in new[] { -0.019f, 0.02f })
                Part(root, "Wheel", new Vector3(x, 0.006f, z), new Vector3(0.006f, 0.012f, 0.012f), dark);
            return root;
        }

        static void Part(GameObject root, string name, Vector3 pos, Vector3 size, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
        }

        GameObject Box(string name, Vector3 localPos, Vector3 size, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(trackRoot, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
            return go;
        }

        /// <summary>A strip between two lateral offsets along a closed path; thick strips get a skirt so raised decks read as solid.</summary>
        void Ribbon(string name, List<Vector3> pts, List<Vector3> rights, float from, float to, float height, Material m, bool skirt)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 up = Vector3.up * height;
                verts.Add(pts[i] + rights[i] * from + up);
                verts.Add(pts[i] + rights[i] * to + up);
                if (skirt)
                {
                    verts.Add(pts[i] + rights[i] * from + Vector3.up * (height - 0.012f));
                    verts.Add(pts[i] + rights[i] * to + Vector3.up * (height - 0.012f));
                }
            }
            int stride = skirt ? 4 : 2;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                int a = i * stride, c = (i + 1) * stride;
                tris.AddRange(new[] { a, c, a + 1, a + 1, c, c + 1 });
                if (skirt)
                {
                    tris.AddRange(new[] { a + 2, a, c + 2, c + 2, a, c });          // left side
                    tris.AddRange(new[] { a + 1, a + 3, c + 1, c + 1, a + 3, c + 3 }); // right side
                }
            }
            var mesh = new Mesh { name = name, indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(trackRoot, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
        }

        Material Mat(Color c, float smoothness)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(sh) { color = c };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            materials.Add(m);
            return m;
        }

        static Vector3 ToUnity(Vec3 p) => new Vector3((float)p.X, (float)p.Z, (float)p.Y);

        static Vector3 RightAt(SlotLane lane, int i)
        {
            double h = lane.Heading[Mathf.Clamp(i, 0, lane.Heading.Length - 1)];
            // Toy lane offsets are +left (−sin h, cos h); the Unity "right" of travel is the negative of that.
            return new Vector3((float)System.Math.Sin(h), 0f, (float)-System.Math.Cos(h));
        }

        /// <summary>Places every car from the authoritative table state; the viewer's own car can be highlighted.</summary>
        public void Render(PocketCircuitTable table, string me)
        {
            SetTrack(table.Track);
            var held = new HashSet<int>();
            foreach (SlotCarState car in table.Board.Cars)
            {
                if (!carsByLane.TryGetValue(car.Lane, out Transform t)) continue;
                held.Add(car.Lane);
                SlotLane lane = track.Lane(car.Lane);
                Vector3 pos = ToUnity(lane.PositionAt(car.S)) + Vector3.up * 0.0142f;
                int i = lane.IndexAt(car.S);
                float yaw = 90f - (float)(lane.Heading[i] * Mathf.Rad2Deg);
                float pitch = -Mathf.Atan((float)lane.Slope[i]) * Mathf.Rad2Deg;
                Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
                if (car.Mode == SlotCarMode.DeSlotted)
                {
                    // Harmless de-slot: the car skids off the outside of the bend and rests askew until it is returned.
                    Vector3 outward = RightAt(lane, i) * (lane.Curvature[i] > 0 ? 1f : -1f);
                    pos += outward * 0.05f;
                    rot *= Quaternion.Euler(0f, 35f, 12f);
                }
                t.gameObject.SetActive(true);
                t.position = transform.position + pos;
                t.rotation = rot;
            }
            foreach (KeyValuePair<int, Transform> kv in carsByLane) if (!held.Contains(kv.Key)) kv.Value.gameObject.SetActive(false);
            PlaceCamera(table, me);
        }

        void PlaceCamera(PocketCircuitTable table, string me)
        {
            Vector3 targetPos;
            Quaternion targetRot;
            SlotCarState mine = table.Car(me);
            if (ChaseView && mine != null && carsByLane.TryGetValue(mine.Lane, out Transform car) && car.gameObject.activeSelf)
            {
                targetPos = car.position - car.forward * 0.32f + Vector3.up * 0.16f;
                targetRot = Quaternion.LookRotation(car.position + car.forward * 0.25f - targetPos, Vector3.up);
            }
            else
            {
                targetPos = centre + new Vector3(0f, span * 0.95f + 0.4f, -span * 0.75f - 0.3f);
                targetRot = Quaternion.LookRotation(centre - targetPos, Vector3.up);
            }
            float k = camPlaced ? 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime) : 1f;
            camPos = Vector3.Lerp(camPos, targetPos, k);
            camRot = Quaternion.Slerp(camRot, targetRot, k);
            camPlaced = true;
            Camera.transform.SetPositionAndRotation(camPos, camRot);
        }

        void OnDestroy()
        {
            foreach (Material m in materials) if (m != null) Destroy(m);
        }
    }
}
