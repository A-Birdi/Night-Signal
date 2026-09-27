using System.Collections.Generic;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.CapClash;
using UnityEngine;

namespace NightSignal.Toys
{
    /// <summary>
    /// Cap Clash tabletop (Addendum 02 §2), built from the authored arrangement: felt board, rails and livelier bumpers,
    /// extruded rubber tool props, the 50/25/10 target rings, launch strip and foul line, one cap per participant and an
    /// aim preview traced with the same Core physics the authority uses. Toy units: metres, X across, Y up-table.
    /// </summary>
    public sealed class CapClashView : MonoBehaviour
    {
        public static readonly Vector3 Origin = new Vector3(40f, -6000f, 0f);
        public Camera Camera { get; private set; }
        CapArrangementDef arrangement;
        string targetId;
        Transform boardRoot, capsRoot;
        LineRenderer aimLine;
        readonly Dictionary<string, Transform> caps = new Dictionary<string, Transform>();
        readonly List<Material> materials = new List<Material>();
        Material aimMat;

        public static readonly Color[] MemberColours =
        {
            new Color(0.85f, 0.15f, 0.18f), new Color(0.15f, 0.45f, 0.9f), new Color(0.95f, 0.75f, 0.15f),
            new Color(0.2f, 0.7f, 0.35f), new Color(0.92f, 0.92f, 0.9f), new Color(0.6f, 0.3f, 0.8f),
        };

        public static CapClashView Create()
        {
            var go = new GameObject("CapClashTable");
            go.transform.position = Origin;
            var v = go.AddComponent<CapClashView>();
            v.BuildRoom();
            return v;
        }

        void BuildRoom()
        {
            var camGo = new GameObject("CapCamera", typeof(Camera));
            camGo.transform.SetParent(transform, false);
            Camera = camGo.GetComponent<Camera>();
            Cameras.CameraRig.Configure(Camera);
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = new Color(0.03f, 0.03f, 0.04f);
            Camera.nearClipPlane = 0.02f;
            Camera.farClipPlane = 40f;
            Camera.fieldOfView = 46f;
            Camera.transform.localPosition = new Vector3(0f, 0.95f, -0.55f);
            Camera.transform.LookAt(transform.position + new Vector3(0f, 0f, 0.95f));
            Light("Lamp", new Vector3(0.1f, 1.9f, 0.9f), new Color(1f, 0.87f, 0.7f), 2.6f, true);
            Light("Fill", new Vector3(-1.5f, 1.2f, -0.8f), new Color(0.55f, 0.65f, 0.85f), 0.8f, false);
            aimMat = Mat(new Color(1f, 1f, 1f, 1f), 0f);
            var aimGo = new GameObject("Aim", typeof(LineRenderer));
            aimGo.transform.SetParent(transform, false);
            aimLine = aimGo.GetComponent<LineRenderer>();
            aimLine.useWorldSpace = true;
            aimLine.widthMultiplier = 0.004f;
            aimLine.sharedMaterial = aimMat;
            aimLine.startColor = new Color(1f, 1f, 1f, 0.9f);
            aimLine.endColor = new Color(0.24f, 0.78f, 0.85f, 0.5f);
            aimLine.positionCount = 0;
        }

        void Light(string name, Vector3 local, Color c, float intensity, bool shadows)
        {
            var go = new GameObject(name, typeof(Light));
            go.transform.SetParent(transform, false);
            go.transform.localPosition = local;
            Light l = go.GetComponent<Light>();
            l.type = LightType.Point;
            l.color = c;
            l.intensity = intensity;
            l.range = 6f;
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        }

        /// <summary>(Re)builds the board when the arrangement or target changes (every arrangement keeps its own board).</summary>
        public void SetBoard(CapArrangementDef a, string target)
        {
            if (a == arrangement && target == targetId) return;
            arrangement = a;
            targetId = target;
            if (boardRoot != null) Destroy(boardRoot.gameObject);
            boardRoot = new GameObject("Board").transform;
            boardRoot.SetParent(transform, false);
            float w = (float)a.Width, l = (float)a.Length;
            Box(boardRoot, "TableTop", new Vector3(0, -0.03f, l * 0.5f), new Vector3(w + 0.5f, 0.05f, l + 0.5f), Mat(new Color(0.3f, 0.2f, 0.13f), 0.3f));
            Box(boardRoot, "Felt", new Vector3(0, -0.002f, l * 0.5f), new Vector3(w, 0.004f, l), Mat(new Color(0.13f, 0.28f, 0.2f), 0.05f));
            foreach (CapSegmentDef s in a.Rails)
            {
                bool bumper = s.Kind == "bumper";
                Segment(boardRoot, s.A, s.B, bumper ? 0.02f : 0.016f, bumper ? Mat(new Color(0.75f, 0.2f, 0.18f), 0.4f) : Mat(new Color(0.55f, 0.42f, 0.3f), 0.35f));
            }
            Material rubber = Mat(new Color(0.12f, 0.12f, 0.13f), 0.25f);
            foreach (CapObstacleDef o in a.Obstacles)
                foreach (List<double[]> part in o.Parts) Extrude(boardRoot, o.Name, part, 0.025f, rubber);
            CapTargetDef t = a.Target(target);
            if (t != null)
            {
                Color[] ring = { new Color(0.85f, 0.2f, 0.2f), new Color(0.95f, 0.65f, 0.2f), new Color(0.9f, 0.88f, 0.82f) };
                for (int i = t.Zones.Length - 1; i >= 0; i--)
                    Disc(boardRoot, "Zone" + i, new Vector3((float)t.X, 0.0004f + (t.Zones.Length - i) * 0.0003f, (float)t.Y), (float)t.Zones[i], Mat(ring[i], 0.1f));
            }
            Material line = Mat(new Color(0.95f, 0.95f, 0.95f), 0.1f);
            Box(boardRoot, "LaunchStrip", new Vector3((float)(a.LaunchMinX + a.LaunchMaxX) / 2, 0.0006f, (float)a.LaunchY), new Vector3((float)(a.LaunchMaxX - a.LaunchMinX), 0.001f, 0.004f), line);
            for (float x = -w / 2 + 0.02f; x < w / 2; x += 0.05f)
                Box(boardRoot, "Foul", new Vector3(x + 0.0125f, 0.0006f, (float)a.FoulLineY), new Vector3(0.025f, 0.001f, 0.003f), Mat(new Color(0.9f, 0.4f, 0.3f), 0.1f));
        }

        /// <summary>Places every cap from the (authoritative or dead-reckoned) state; the viewer's own cap is ringed.</summary>
        public void Render(IEnumerable<CapBody> bodies, string me)
        {
            var seen = new HashSet<string>();
            foreach (CapBody b in bodies)
            {
                seen.Add(b.CapId);
                if (!caps.TryGetValue(b.CapId, out Transform t))
                {
                    t = BuildCap(ColourFor(b.Owner), b.Owner == me).transform;
                    caps[b.CapId] = t;
                }
                t.position = transform.position + new Vector3((float)b.Pos.X, 0.003f, (float)b.Pos.Y);
            }
            var gone = new List<string>();
            foreach (KeyValuePair<string, Transform> kv in caps) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (string id in gone) { Destroy(caps[id].gameObject); caps.Remove(id); }
        }

        public static Color ColourFor(string member)
        {
            int h = 0;
            foreach (char c in member ?? "") h = h * 31 + c;
            return MemberColours[(h & 0x7fffffff) % MemberColours.Length];
        }

        public void ShowAim(List<Vector2> path)
        {
            if (path == null || path.Count < 2) { aimLine.positionCount = 0; return; }
            aimLine.positionCount = path.Count;
            for (int i = 0; i < path.Count; i++) aimLine.SetPosition(i, transform.position + new Vector3(path[i].x, 0.008f, path[i].y));
        }

        GameObject BuildCap(Color c, bool mine)
        {
            GameObject cap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cap.name = "Cap";
            Destroy(cap.GetComponent<Collider>());
            cap.transform.SetParent(transform, false);
            cap.transform.localScale = new Vector3(0.032f, 0.003f, 0.032f);
            cap.GetComponent<MeshRenderer>().sharedMaterial = Mat(c, 0.85f);
            if (mine)
            {
                GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(ring.GetComponent<Collider>());
                ring.transform.SetParent(cap.transform, false);
                ring.transform.localScale = new Vector3(1.35f, 0.2f, 1.35f);
                ring.transform.localPosition = new Vector3(0, -0.8f, 0);
                ring.GetComponent<MeshRenderer>().sharedMaterial = Mat(Color.white, 0.2f);
            }
            return cap;
        }

        void Segment(Transform parent, double[] a, double[] b, float height, Material m)
        {
            var pa = new Vector3((float)a[0], 0, (float)a[1]);
            var pb = new Vector3((float)b[0], 0, (float)b[1]);
            GameObject go = Box(parent, "Rail", (pa + pb) * 0.5f + Vector3.up * height * 0.5f, new Vector3(0.012f, height, Vector3.Distance(pa, pb) + 0.012f), m);
            go.transform.localRotation = Quaternion.LookRotation(pb - pa, Vector3.up);
        }

        void Extrude(Transform parent, string name, List<double[]> outline, float height, Material m)
        {
            int n = outline.Count;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i < n; i++) verts.Add(new Vector3((float)outline[i][0], height, (float)outline[i][1]));
            for (int i = 1; i < n - 1; i++) tris.AddRange(new[] { 0, i + 1, i }); // top fan (counter-clockwise outline seen from above)
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int k = verts.Count;
                verts.Add(new Vector3((float)outline[i][0], height, (float)outline[i][1]));
                verts.Add(new Vector3((float)outline[j][0], height, (float)outline[j][1]));
                verts.Add(new Vector3((float)outline[i][0], 0, (float)outline[i][1]));
                verts.Add(new Vector3((float)outline[j][0], 0, (float)outline[j][1]));
                tris.AddRange(new[] { k, k + 1, k + 2, k + 1, k + 3, k + 2 });
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
            // Outlines are counter-clockwise in toy X/Y; seen from Unity's +Y the winding flips, so render both sides.
            go.GetComponent<MeshRenderer>().sharedMaterial.SetFloat("_Cull", 0f);
        }

        void Disc(Transform parent, string name, Vector3 pos, float radius, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(radius * 2f, 0.0002f, radius * 2f);
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
        }

        GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
            return go;
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

        void OnDestroy()
        {
            foreach (Material m in materials) if (m != null) Destroy(m);
        }
    }
}
