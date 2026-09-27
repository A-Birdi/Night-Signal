using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Toys.PitCrew;
using UnityEngine;

namespace NightSignal.Toys
{
    /// <summary>
    /// The Pit-Crew workbench (Addendum 02 §3): the shared miniature grows on a turntable as operations complete.
    /// Each authored part has a place in its group's zone of the model (chassis low and long, wheels at the corners,
    /// body over the cabin, lamps as small lights…); installed parts are solid and coloured, missing ones are dark
    /// placeholders so everyone sees what is left. Late joiners render the current model directly from shared facts.
    /// </summary>
    public sealed class PitCrewView : MonoBehaviour
    {
        public static readonly Vector3 Origin = new Vector3(80f, -6000f, 0f);
        public Camera Camera { get; private set; }
        string blueprintId;
        Transform model;
        readonly Dictionary<string, Renderer> parts = new Dictionary<string, Renderer>();
        readonly Dictionary<string, Color> partColours = new Dictionary<string, Color>();
        readonly List<Material> materials = new List<Material>();
        Material ghost;

        public static PitCrewView Create()
        {
            var go = new GameObject("PitCrewBench");
            go.transform.position = Origin;
            var v = go.AddComponent<PitCrewView>();
            v.BuildRoom();
            return v;
        }

        void BuildRoom()
        {
            var camGo = new GameObject("BenchCamera", typeof(Camera));
            camGo.transform.SetParent(transform, false);
            Camera = camGo.GetComponent<Camera>();
            Cameras.CameraRig.Configure(Camera);
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = new Color(0.035f, 0.035f, 0.04f);
            Camera.nearClipPlane = 0.02f;
            Camera.fieldOfView = 38f;
            Camera.transform.localPosition = new Vector3(0.1f, 0.5f, -0.95f);
            Camera.transform.LookAt(transform.position + new Vector3(0.1f, 0.03f, 0f));
            AddLight(new Vector3(0.3f, 1.2f, -0.4f), new Color(1f, 0.9f, 0.76f), 2.2f, true);
            AddLight(new Vector3(-0.8f, 0.6f, 0.6f), new Color(0.5f, 0.6f, 0.85f), 0.7f, false);
            GameObject bench = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(bench.GetComponent<Collider>());
            bench.transform.SetParent(transform, false);
            bench.transform.localPosition = new Vector3(0, -0.03f, 0.05f);
            bench.transform.localScale = new Vector3(1.4f, 0.05f, 0.7f);
            bench.GetComponent<MeshRenderer>().sharedMaterial = Mat(new Color(0.35f, 0.26f, 0.17f), 0.3f);
            GameObject plate = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(plate.GetComponent<Collider>());
            plate.transform.SetParent(transform, false);
            plate.transform.localPosition = new Vector3(0, -0.002f, 0);
            plate.transform.localScale = new Vector3(0.46f, 0.004f, 0.46f);
            plate.GetComponent<MeshRenderer>().sharedMaterial = Mat(new Color(0.18f, 0.18f, 0.2f), 0.6f);
            ghost = Mat(new Color(0.24f, 0.3f, 0.42f), 0.05f); // blueprint-blue placeholders: what is still to build
        }

        void AddLight(Vector3 local, Color c, float intensity, bool shadows)
        {
            var go = new GameObject("Light", typeof(Light));
            go.transform.SetParent(transform, false);
            go.transform.localPosition = local;
            Light l = go.GetComponent<Light>();
            l.type = LightType.Point;
            l.color = c;
            l.intensity = intensity;
            l.range = 4f;
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        }

        /// <summary>Builds the part placeholders for a blueprint (once per project blueprint).</summary>
        public void SetBlueprint(BlueprintDef bp)
        {
            if (bp.Id == blueprintId) return;
            blueprintId = bp.Id;
            if (model != null) Destroy(model.gameObject);
            parts.Clear();
            model = new GameObject("Model").transform;
            model.SetParent(transform, false);
            foreach (IGrouping<string, BlueprintPartDef> g in bp.Parts.GroupBy(p => p.Group))
            {
                List<BlueprintPartDef> list = g.ToList();
                for (int i = 0; i < list.Count; i++) Place(list[i], g.Key, i, list.Count);
            }
        }

        void Place(BlueprintPartDef part, string group, int i, int n)
        {
            float t = n <= 1 ? 0.5f : i / (float)(n - 1);
            PrimitiveType shape = PrimitiveType.Cube;
            Vector3 pos, size;
            Color colour;
            switch (group)
            {
                case "chassis": case "base": case "platform":
                    pos = new Vector3(Mathf.Lerp(-0.12f, 0.12f, t), 0.012f + i * 0.004f, (i % 2 == 0 ? -1 : 1) * 0.03f * (i > 0 ? 1 : 0));
                    size = i == 0 ? new Vector3(0.3f, 0.01f, 0.12f) : new Vector3(0.28f, 0.008f, 0.012f);
                    colour = new Color(0.62f, 0.64f, 0.68f);
                    break;
                case "wheels": case "rotating":
                    shape = PrimitiveType.Cylinder;
                    float wx = (i % 2 == 0 ? -1 : 1) * 0.1f, wz = (i / 2 % 2 == 0 ? -1 : 1) * 0.065f;
                    pos = new Vector3(wx + (i / 4) * 0.02f, 0.03f, wz);
                    size = new Vector3(0.05f, 0.012f, 0.05f);
                    colour = new Color(0.08f, 0.08f, 0.08f);
                    break;
                case "lights": case "lamps":
                    shape = PrimitiveType.Sphere;
                    pos = new Vector3((i % 2 == 0 ? -1 : 1) * 0.15f, 0.06f + (i / 2) * 0.03f, Mathf.Lerp(-0.05f, 0.05f, t));
                    size = Vector3.one * 0.018f;
                    colour = new Color(1f, 0.85f, 0.5f);
                    break;
                case "body": case "block":
                    pos = new Vector3(Mathf.Lerp(-0.08f, 0.08f, t), 0.07f + (i % 2) * 0.012f, 0);
                    size = new Vector3(0.1f, 0.04f, 0.13f);
                    colour = new Color(0.78f, 0.12f, 0.16f);
                    break;
                case "cabin": case "bench": case "miniature":
                    pos = new Vector3(Mathf.Lerp(-0.05f, 0.05f, t), 0.1f, Mathf.Lerp(-0.03f, 0.03f, t));
                    size = new Vector3(0.05f, 0.03f, 0.05f);
                    colour = new Color(0.15f, 0.2f, 0.28f);
                    break;
                case "drivetrain": case "timing": case "pipes": case "wiring":
                    pos = new Vector3(Mathf.Lerp(-0.1f, 0.1f, t), 0.035f, 0.0f);
                    size = new Vector3(0.04f, 0.025f, 0.04f);
                    colour = new Color(0.55f, 0.55f, 0.58f);
                    break;
                default: // trim, decoration, signage, fence, display
                    pos = new Vector3(Mathf.Lerp(-0.16f, 0.16f, t), 0.1f + (i % 2) * 0.02f, 0.07f);
                    size = new Vector3(0.03f, 0.012f, 0.012f);
                    colour = new Color(0.85f, 0.75f, 0.35f);
                    break;
            }
            GameObject go = GameObject.CreatePrimitive(shape);
            go.name = part.Id;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(model, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            if (shape == PrimitiveType.Cylinder) go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Renderer r = go.GetComponent<Renderer>();
            r.sharedMaterial = ghost;
            parts[part.Id] = r;
            partColours[part.Id] = colour;
        }

        /// <summary>Installed parts become solid; the model turns slowly on its plate.</summary>
        public void Render(ICollection<string> installed)
        {
            foreach (KeyValuePair<string, Renderer> kv in parts)
            {
                bool on = installed.Contains(kv.Key);
                Material want = on ? MatFor(kv.Key) : ghost;
                if (kv.Value.sharedMaterial != want) kv.Value.sharedMaterial = want;
            }
            if (model != null) model.localRotation = Quaternion.Euler(0f, Time.unscaledTime * 12f, 0f);
        }

        readonly Dictionary<string, Material> solid = new Dictionary<string, Material>();

        Material MatFor(string part)
        {
            if (!solid.TryGetValue(part, out Material m)) solid[part] = m = Mat(partColours[part], 0.55f);
            return m;
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
