using NightSignal.Art;
using NightSignal.Characters;
using NightSignal.Core.Meet;
using UnityEngine;

namespace NightSignal.Front
{
    /// <summary>
    /// The Player Card's driver preview: one character on a dark floor far below the world, turning slowly, rendered into a
    /// texture by its own camera each frame with its own lights (switched on only while it renders, like the Garage's
    /// <see cref="AppearanceStage"/>). Built by the same <see cref="CharacterRig"/> the meet uses — what you see is what
    /// other drivers see.
    /// </summary>
    public sealed class CharacterStage
    {
        static readonly Vector3 Origin = new Vector3(0f, -5200f, 0f);

        readonly GameObject root;
        readonly Camera cam;
        readonly Light[] lights;
        readonly RenderTexture target;
        readonly Material floorMaterial;
        CharacterRig rig;
        CharacterMotion motion;
        float yaw = 205f;

        public Texture Texture => target;

        public CharacterStage(int width, int height)
        {
            root = new GameObject("CharacterStage");
            Object.DontDestroyOnLoad(root);
            root.transform.position = Origin;

            var camGo = new GameObject("StageCamera");
            camGo.transform.SetParent(root.transform, false);
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false; // rendered on demand
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.075f, 0.09f);
            cam.fieldOfView = 26f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 30f;
            target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "CharacterPreview" };
            cam.targetTexture = target;

            lights = new[]
            {
                Directional("Key", 1.25f, Quaternion.Euler(32f, 150f, 0f), LightShadows.Soft),
                Directional("Fill", 0.5f, Quaternion.Euler(18f, -40f, 0f), LightShadows.None),
                Directional("Rim", 0.45f, Quaternion.Euler(10f, 20f, 0f), LightShadows.None),
            };

            var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
            floor.name = "Floor";
            Object.Destroy(floor.GetComponent<Collider>());
            floor.transform.SetParent(root.transform, false);
            floor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            floor.transform.localScale = new Vector3(10f, 10f, 1f);
            var mats = Resources.Load<CarMaterialSet>("CarMaterialSet");
            floorMaterial = new Material(mats.Trim) { name = "CharacterStageFloor" };
            floorMaterial.SetColor("_BaseColor", new Color(0.11f, 0.115f, 0.13f));
            floorMaterial.SetFloat("_Smoothness", 0.3f);
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

        /// <summary>Rebuilds the character (its own mesh: previews change often and must not grow the shared cache).</summary>
        public void Show(CharacterLook look)
        {
            if (rig != null) Object.Destroy(rig.gameObject);
            rig = CharacterRig.Create(look, null, root.transform, "CardAvatar", GameLayers.Avatar, cacheMesh: false);
            rig.transform.localPosition = Vector3.zero;
            rig.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            motion = rig.gameObject.AddComponent<CharacterMotion>();
            // Framed head to toe (and a raised arm) with a margin, whatever the look's height.
            float h = Mathf.Clamp(look.Height, 1.4f, 2f);
            cam.transform.localPosition = new Vector3(0f, h * 0.62f, 6.4f * (h / 1.72f));
            cam.transform.LookAt(root.transform.position + new Vector3(0f, h * 0.56f, 0f));
        }

        /// <summary>Plays an emote on the preview (e.g. a wave after saving).</summary>
        public void Play(Emote e) => motion?.Play(e);

        /// <summary>Once per frame: a slow turntable, then a render with the stage lights on (and only then).</summary>
        public void Update(float dt)
        {
            if (rig == null) return;
            yaw += dt * 12f;
            rig.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            foreach (Light l in lights) l.enabled = true;
            cam.Render();
            foreach (Light l in lights) l.enabled = false;
        }

        /// <summary>Writes the current preview to a PNG (evidence runs).</summary>
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

        public void Dispose()
        {
            if (rig != null) Object.Destroy(rig.gameObject);
            cam.targetTexture = null;
            target.Release();
            Object.Destroy(target);
            Object.Destroy(floorMaterial);
            Object.Destroy(root);
        }
    }
}
