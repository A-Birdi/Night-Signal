using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Art;
using NightSignal.Content;
using NightSignal.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NightSignal.Editor.ArtTools
{
    /// <summary>
    /// Exterior contact sheets of all 18 cars (V01…V18, row-major, six per row) in one neutral paint so the shapes — not the
    /// liveries — are compared: front three-quarter, rear three-quarter, side and front elevation, in an isolated preview
    /// scene. The distinctness sheet of the car art pass (Gate 3 C.4). Output: Builds/Screenshots/cars/cars-*.png.
    /// </summary>
    public static class CarSheet
    {
        public enum View { FrontQuarter, RearQuarter, Side, Front, Rear, Wheel }

        [MenuItem("Night Signal/Art/Render Car Sheets")]
        public static void RenderDefault() => RenderAll(Path.GetFullPath(Path.Combine("Builds", "Screenshots", "cars")), "");

        public static void RenderAll(string dir, string suffix)
        {
            Directory.CreateDirectory(dir);
            foreach (View v in new[] { View.FrontQuarter, View.RearQuarter, View.Side, View.Front, View.Rear, View.Wheel })
                Render(Path.Combine(dir, $"cars-{Slug(v)}{suffix}.png"), v);
            Debug.Log("[NightSignal.Art] car sheets written to " + dir);
        }

        /// <summary>
        /// The distant levels of detail forced up close (front and rear three-quarter, side) for every car — the inspection
        /// sheets of the LOD pass: cars-{view}-lod1.png and -lod2.png.
        /// </summary>
        [MenuItem("Night Signal/Art/Render Car LOD Sheets")]
        public static void RenderLodDefault() => RenderLods(Path.GetFullPath(Path.Combine("Builds", "Screenshots", "cars")));

        public static void RenderLods(string dir)
        {
            Directory.CreateDirectory(dir);
            for (int lod = 1; lod <= 2; lod++)
            foreach (View v in new[] { View.FrontQuarter, View.RearQuarter, View.Side })
                Render(Path.Combine(dir, $"cars-{Slug(v)}-lod{lod}.png"), v, forceLod: lod);
            Debug.Log("[NightSignal.Art] car LOD sheets written to " + dir);
        }

        static string Slug(View v)
        {
            switch (v)
            {
                case View.FrontQuarter: return "front34";
                case View.RearQuarter: return "rear34";
                case View.Side: return "side";
                case View.Front: return "front";
                case View.Wheel: return "wheel";
                default: return "rear";
            }
        }

        /// <summary>
        /// The signature paints' flip tint (customization.json paintSwatches with a "flipTint"): one car in every such swatch,
        /// top row without the tint (plain Complex Lit), bottom row with it (the Car Paint shader) — the colour shift shows
        /// toward the silhouette and on surfaces turned away from the camera.
        /// </summary>
        [MenuItem("Night Signal/Art/Render Flip Tint Sheet")]
        public static void RenderFlipDefault() => RenderFlipTints(Path.GetFullPath(Path.Combine("Builds", "Screenshots", "cars", "flip-tints.png")));

        public static string RenderFlipTints(string path, string car = "V04")
        {
            NightSignal.Core.Customization.CustomizationCatalogue look = ContentLibrary.Load().Customization;
            var swatches = look.PaintSwatches.Where(s => s.FlipTint != null).ToList();
            var cars = new List<string>();
            var looks = new List<CarAppearance>();
            foreach (bool flip in new[] { false, true })
                foreach (var s in swatches)
                {
                    ColorUtility.TryParseHtmlString(s.Color, out Color c);
                    ColorUtility.TryParseHtmlString(s.FlipTint, out Color t);
                    cars.Add(car);
                    looks.Add(new CarAppearance { Primary = c, Finish = s.Finish, FlipTint = flip ? t : (Color?)null });
                }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Render(path, View.FrontQuarter, cars, swatches.Count, 300, 200, appearances: looks);
            return string.Join("\n", swatches.Select(s => $"{s.Id} {s.Name} {s.Finish} {s.Color} -> {s.FlipTint} ({s.CosmeticId})"));
        }

        /// <param name="forceLod">A level of detail to draw regardless of distance (−1: the level the camera picks).</param>
        /// <param name="appearances">Per tile, the appearance to draw instead of the plain paint (null: the plain paint).</param>
        public static void Render(string path, View view, List<string> cars = null, int columns = 6, int tileWidth = 520, int tileHeight = 320,
            Color? paint = null, int forceLod = -1, List<CarAppearance> appearances = null)
        {
            ContentLibrary lib = ContentLibrary.Load();
            CarMaterialSet mats = Resources.Load<CarMaterialSet>("CarMaterialSet");
            if (cars == null)
            {
                cars = new List<string>();
                foreach (var c in lib.Catalogue.Cars) cars.Add(c.Id);
            }
            int rows = (cars.Count + columns - 1) / columns;
            var sheet = new Texture2D(tileWidth * columns, tileHeight * rows, TextureFormat.RGB24, false);
            Scene scene = EditorSceneManager.NewPreviewScene();
            var temp = new List<Object>();
            try
            {
                var lightGo = new GameObject("Key", typeof(Light));
                SceneManager.MoveGameObjectToScene(lightGo, scene);
                Light key = lightGo.GetComponent<Light>();
                key.type = LightType.Directional;
                key.intensity = 1.25f;
                key.shadows = LightShadows.Soft;
                lightGo.transform.rotation = Quaternion.Euler(38f, 140f, 0f);
                var fillGo = new GameObject("Fill", typeof(Light));
                SceneManager.MoveGameObjectToScene(fillGo, scene);
                Light fill = fillGo.GetComponent<Light>();
                fill.type = LightType.Directional;
                fill.intensity = 0.35f;
                fill.color = new Color(0.75f, 0.82f, 1f);
                fillGo.transform.rotation = Quaternion.Euler(20f, -60f, 0f);

                var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(ground.GetComponent<Collider>());
                SceneManager.MoveGameObjectToScene(ground, scene);
                ground.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(90f, 0f, 0f));
                ground.transform.localScale = new Vector3(400f, 400f, 1f);
                var groundMat = new Material(mats.Trim) { name = "SheetGround" };
                groundMat.SetColor("_BaseColor", new Color(0.3f, 0.31f, 0.33f));
                temp.Add(groundMat);
                ground.GetComponent<MeshRenderer>().sharedMaterial = groundMat;

                var camGo = new GameObject("SheetCamera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(camGo, scene);
                Camera cam = camGo.GetComponent<Camera>();
                cam.scene = scene;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.62f, 0.7f, 0.8f);
                cam.fieldOfView = 26f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 200f;
                var rt = new RenderTexture(tileWidth, tileHeight, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
                cam.targetTexture = rt;
                var read = new Texture2D(tileWidth, tileHeight, TextureFormat.RGB24, false);
                for (int i = 0; i < cars.Count; i++)
                {
                    VehicleParams p = lib.Params(cars[i], AssistSettings.Default);
                    VehicleView car = VehicleView.Create("SheetCar", p, lib.Body(cars[i]), mats, paint ?? new Color(0.46f, 0.56f, 0.66f),
                        appearances != null && i < appearances.Count ? appearances[i] : null);
                    SceneManager.MoveGameObjectToScene(car.gameObject, scene);
                    car.ShowParked(Vector3.zero, Quaternion.identity, view == View.FrontQuarter ? 12f : 0f);
                    if (forceLod >= 0) car.HoldLod(forceLod);
                    float mid = (p.FrontAxleZ + p.RearAxleZ) * 0.5f;
                    Vector3 target = new Vector3(0f, 0.62f, mid);
                    Vector3 eye;
                    switch (view)
                    {
                        case View.FrontQuarter: eye = target + Quaternion.Euler(0f, 38f, 0f) * new Vector3(0f, 0f, 8.4f) + Vector3.up * 1.7f; break;
                        case View.RearQuarter: eye = target + Quaternion.Euler(0f, 218f, 0f) * new Vector3(0f, 0f, 8.4f) + Vector3.up * 1.7f; break;
                        case View.Side: eye = target + new Vector3(9.6f, 0.25f, 0f); break;
                        case View.Front: eye = target + new Vector3(0f, 0.55f, 12.5f); break;
                        case View.Wheel:
                            target = new Vector3(p.WidthM * 0.5f, lib.Body(cars[i]).WheelRadius + 0.05f, p.FrontAxleZ);
                            eye = target + new Vector3(2.6f, 0.55f, 1.5f);
                            break;
                        default: eye = target + new Vector3(0f, 0.75f, -12.5f); break;
                    }
                    cam.fieldOfView = view == View.Front || view == View.Rear ? 10.5f : 25f;
                    cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
                    cam.Render();
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, tileWidth, tileHeight), 0, 0);
                    read.Apply();
                    RenderTexture.active = null;
                    int cx = i % columns, cy = rows - 1 - i / columns;
                    sheet.SetPixels(cx * tileWidth, cy * tileHeight, tileWidth, tileHeight, read.GetPixels());
                    Object.DestroyImmediate(car.gameObject);
                }
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(read);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (Object o in temp) Object.DestroyImmediate(o);
            }
            sheet.Apply();
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Object.DestroyImmediate(sheet);
        }
    }
}
