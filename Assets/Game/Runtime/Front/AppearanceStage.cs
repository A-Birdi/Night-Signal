using NightSignal.Art;
using NightSignal.Content;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Front
{
    /// <summary>
    /// The Garage's appearance preview: one parked car on a dark floor far below the world, rendered on demand into a texture
    /// by its own camera. Its lights are switched on only while it renders, so nothing else in the scene is lit by them.
    /// Visual only — the car is built from the same <see cref="VehicleView"/> the races use.
    /// </summary>
    public sealed class AppearanceStage
    {
        static readonly Vector3 Origin = new Vector3(0f, -5000f, 0f);

        /// <summary>Views: name, the car's yaw for the fixed camera, and whether the camera looks down from above.</summary>
        public static readonly (string Name, float Yaw, bool High)[] Views =
        {
            ("Front ¾", 0f, false), ("Front", 36f, false), ("Right side", -54f, false), ("Left side", 126f, false),
            ("Rear ¾", 180f, false), ("Rear", -144f, false), ("From above", 20f, true),
        };

        readonly GameObject root;
        readonly Camera cam;
        readonly Light[] lights;
        readonly RenderTexture target;
        readonly Material floorMaterial;
        VehicleView view;
        VehicleParams parameters;
        int viewIndex, renderAtFrame = -1;

        public Texture Texture => target;
        /// <summary>Where the car and camera are (evidence runs log it next to each capture).</summary>
        public string Describe() => view == null ? "no car" :
            $"view {Views[viewIndex].Name}: car at {view.transform.position - Origin} facing {view.transform.forward}, camera at {cam.transform.position - Origin} looking {cam.transform.forward}";
        public int ViewIndex => viewIndex;

        public AppearanceStage(int width, int height)
        {
            root = new GameObject("AppearanceStage");
            Object.DontDestroyOnLoad(root);
            root.transform.position = Origin;

            var camGo = new GameObject("StageCamera");
            camGo.transform.SetParent(root.transform, false);
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false; // rendered on demand
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.075f, 0.09f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 40f;
            target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "AppearancePreview" };
            cam.targetTexture = target;

            lights = new[]
            {
                Directional("Key", 1.3f, Quaternion.Euler(38f, 140f, 0f), LightShadows.Soft),
                Directional("Fill", 0.55f, Quaternion.Euler(20f, -40f, 0f), LightShadows.None),
                Directional("Rim", 0.35f, Quaternion.Euler(12f, 10f, 0f), LightShadows.None),
            };

            var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
            floor.name = "Floor";
            Object.Destroy(floor.GetComponent<Collider>());
            floor.transform.SetParent(root.transform, false);
            floor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            floor.transform.localScale = new Vector3(14f, 14f, 1f);
            var mats = Resources.Load<CarMaterialSet>("CarMaterialSet");
            floorMaterial = new Material(mats.Trim) { name = "StageFloor" };
            floorMaterial.SetColor("_BaseColor", new Color(0.11f, 0.115f, 0.13f));
            floorMaterial.SetFloat("_Smoothness", 0.35f);
            floor.GetComponent<MeshRenderer>().sharedMaterial = floorMaterial;
        }

        Light Directional(string name, float intensity, Quaternion rotation, LightShadows shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.rotation = rotation;
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.shadows = shadows;
            l.enabled = false;
            return l;
        }

        /// <summary>
        /// Rebuilds the car with <paramref name="appearance"/> and renders the current view on the NEXT frame: with the GPU
        /// Resident Drawer (PC pipeline) a moved or new renderer's transform reaches the GPU during the frame, so rendering
        /// straight after posing drew the previous pose.
        /// </summary>
        public void Show(string carId, CarAppearance appearance)
        {
            if (view != null) Object.Destroy(view.gameObject);
            ContentLibrary lib = ContentLibrary.Load();
            parameters = lib.Params(carId, AssistSettings.Default);
            view = VehicleView.Create("PreviewCar", parameters, lib.Body(carId), Resources.Load<CarMaterialSet>("CarMaterialSet"), appearance.Primary, appearance);
            view.transform.SetParent(root.transform, true);
            Pose();
            renderAtFrame = Time.frameCount + 1;
        }

        public void SetView(int index)
        {
            viewIndex = ((index % Views.Length) + Views.Length) % Views.Length;
            Pose();
            renderAtFrame = Time.frameCount + 1;
        }

        /// <summary>Call once per frame: renders a requested frame once the posed transforms have been submitted.</summary>
        public void Update()
        {
            if (renderAtFrame < 0 || Time.frameCount < renderAtFrame) return;
            renderAtFrame = -1;
            Render();
        }

        /// <summary>True while a requested render has not happened yet (evidence runs wait for it).</summary>
        public bool RenderPending => renderAtFrame >= 0;

        void Pose()
        {
            if (view == null) return;
            (string _, float yaw, bool high) = Views[viewIndex];
            view.ShowParked(Origin, Quaternion.Euler(0f, yaw, 0f));
            float length = parameters != null ? Mathf.Max(3.6f, parameters.WheelbaseM + 1.6f) : 4.2f;
            float k = length / 4.2f;
            // Framed so the whole car and its wheels fit with a margin at 30° vertical field of view.
            cam.transform.position = Origin + (high ? new Vector3(3.2f, 7.4f, 4.6f) : new Vector3(4.5f, 1.9f, 6.2f)) * k;
            cam.transform.LookAt(Origin + new Vector3(0f, high ? 0.2f : 0.5f, 0f));
        }

        /// <summary>Writes the current preview texture to a PNG (evidence runs).</summary>
        public void SaveTexture(string path)
        {
            var read = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            read.Apply();
            RenderTexture.active = previous;
            System.IO.File.WriteAllBytes(path, read.EncodeToPNG());
            Object.Destroy(read);
        }

        /// <summary>Renders one frame into <see cref="Texture"/> with the stage lights on (and only then).</summary>
        public void Render()
        {
            if (view == null) return;
            foreach (Light l in lights) l.enabled = true;
            cam.Render();
            foreach (Light l in lights) l.enabled = false;
        }

        public void Dispose()
        {
            if (view != null) Object.Destroy(view.gameObject);
            cam.targetTexture = null;
            target.Release();
            Object.Destroy(target);
            Object.Destroy(floorMaterial);
            Object.Destroy(root);
        }
    }
}
