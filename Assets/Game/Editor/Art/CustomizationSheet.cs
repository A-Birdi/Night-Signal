using System.Collections.Generic;
using System.IO;
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
    /// Renders customization evidence sheets (Addendum 01 §13 art acceptance: before/after pairs, the rim catalogue) in an
    /// isolated preview scene: each tile is one real <see cref="VehicleView"/> built from a <see cref="CarAppearance"/>.
    /// Output: Builds/Screenshots/customization/*.png (the Builds folder is not committed; copy chosen sheets to Evidence).
    /// </summary>
    public static class CustomizationSheet
    {
        public sealed class Tile
        {
            public string CarId;
            public string Label;
            public CarAppearance Appearance;
            public float YawDeg = 215f;
        }

        [MenuItem("Night Signal/Art/Render Customization Sheets")]
        public static void RenderDefault()
        {
            string dir = Path.GetFullPath(Path.Combine("Builds", "Screenshots", "customization"));
            Directory.CreateDirectory(dir);
            foreach (string car in new[] { "V01", "V06" })
            {
                RenderSheet(Path.Combine(dir, $"families-{car}-front.png"), FamilyTiles(car, -8f), 3);
                RenderSheet(Path.Combine(dir, $"families-{car}-rear.png"), FamilyTiles(car, 172f), 3);
            }
            RenderSheet(Path.Combine(dir, "rims.png"), RimTiles("V01"), 4, closeUp: true);
            Debug.Log("[NightSignal.Art] customization sheets written to " + dir);
        }

        public static List<Tile> FamilyTiles(string car, float yaw)
        {
            var red = new Color(0.78f, 0.1f, 0.12f);
            var tiles = new List<Tile>
            {
                new Tile { CarId = car, Label = "stock", Appearance = CarAppearance.Stock(red) },
                new Tile { CarId = car, Label = "lip · diffuser · skirt · ducktail · dual", Appearance = new CarAppearance
                    { Primary = red, Front = "lip", Rear = "diffuser", Side = "skirt", RearAero = "ducktail", Exhaust = "dual", RimStyle = "split" } },
                new Tile { CarId = car, Label = "aero · valance · sculpted · wing · quad · lower two-tone", Appearance = new CarAppearance
                    { Primary = new Color(0.95f, 0.95f, 0.93f), Secondary = new Color(0.08f, 0.08f, 0.1f), TwoTone = "lower", Front = "aero", Rear = "valance",
                      Side = "sculpted", RearAero = "wing", Exhaust = "quad", RimStyle = "mesh", Finish = "pearl" } },
                new Tile { CarId = car, Label = "track · gt-wing · center · hood stripe", Appearance = new CarAppearance
                    { Primary = new Color(0.1f, 0.35f, 0.75f), Secondary = new Color(0.95f, 0.95f, 0.95f), Accent = new Color(0.9f, 0.55f, 0.1f),
                      TwoTone = "hood-stripe", Front = "track", Rear = "diffuser", Side = "sculpted", RearAero = "gt-wing", Exhaust = "center",
                      RimStyle = "turbofan", Finish = "metallic", RimColor = new Color(0.15f, 0.15f, 0.16f) } },
                new Tile { CarId = car, Label = "roof two-tone · matte · amber lamps", Appearance = new CarAppearance
                    { Primary = new Color(0.3f, 0.45f, 0.3f), Secondary = new Color(0.07f, 0.07f, 0.08f), TwoTone = "roof", Finish = "matte",
                      Front = "lip", HeadTint = "amber", RimStyle = "3-spoke", RimColor = new Color(0.8f, 0.65f, 0.25f), PlateText = "NS-001" } },
                new Tile { CarId = car, Label = "side stripe · satin · multi-spoke", Appearance = new CarAppearance
                    { Primary = new Color(0.95f, 0.8f, 0.1f), Secondary = new Color(0.1f, 0.1f, 0.1f), TwoTone = "side-stripe", Finish = "satin",
                      Front = "aero", Side = "skirt", RearAero = "wing", RimStyle = "multi-spoke", TailTint = "smoke-light" } },
            };
            foreach (Tile t in tiles) t.YawDeg = yaw;
            return tiles;
        }

        public static List<Tile> RimTiles(string car)
        {
            var tiles = new List<Tile>();
            foreach (string rim in new[] { "5-spoke", "6-spoke", "mesh", "split", "dish", "turbofan", "multi-spoke", "3-spoke" })
                tiles.Add(new Tile { CarId = car, Label = rim, YawDeg = 262f, Appearance = new CarAppearance { Primary = new Color(0.2f, 0.2f, 0.22f), RimStyle = rim } });
            return tiles;
        }

        public static void RenderSheet(string path, List<Tile> tiles, int columns, bool closeUp = false, int tileWidth = 640, int tileHeight = 400)
        {
            ContentLibrary lib = ContentLibrary.Load();
            CarMaterialSet mats = Resources.Load<CarMaterialSet>("CarMaterialSet");
            int rows = (tiles.Count + columns - 1) / columns;
            var sheet = new Texture2D(tileWidth * columns, tileHeight * rows, TextureFormat.RGB24, false);
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var lightGo = new GameObject("Key", typeof(Light));
                SceneManager.MoveGameObjectToScene(lightGo, scene);
                Light key = lightGo.GetComponent<Light>();
                key.type = LightType.Directional;
                key.intensity = 1.3f;
                lightGo.transform.rotation = Quaternion.Euler(38f, 140f, 0f);
                var fillGo = new GameObject("Fill", typeof(Light));
                SceneManager.MoveGameObjectToScene(fillGo, scene);
                Light fill = fillGo.GetComponent<Light>();
                fill.type = LightType.Directional;
                fill.intensity = 0.55f;
                fillGo.transform.rotation = Quaternion.Euler(20f, -40f, 0f);
                var camGo = new GameObject("SheetCamera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(camGo, scene);
                Camera cam = camGo.GetComponent<Camera>();
                cam.scene = scene;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.16f, 0.17f, 0.19f);
                cam.fieldOfView = closeUp ? 24f : 32f;
                cam.nearClipPlane = 0.05f;
                var rt = new RenderTexture(tileWidth, tileHeight, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;
                var read = new Texture2D(tileWidth, tileHeight, TextureFormat.RGB24, false);
                for (int i = 0; i < tiles.Count; i++)
                {
                    Tile t = tiles[i];
                    VehicleParams p = lib.Params(t.CarId, AssistSettings.Default);
                    VehicleView view = VehicleView.Create("Tile", p, lib.Body(t.CarId), mats, t.Appearance.Primary, t.Appearance);
                    SceneManager.MoveGameObjectToScene(view.gameObject, scene);
                    view.transform.SetPositionAndRotation(new Vector3(0f, p.CgHeightM, 0f), Quaternion.Euler(0f, t.YawDeg, 0f));
                    var rest = VehicleState.AtRest(view.transform.position, view.transform.rotation);
                    // Settled on its springs (static compression) as it would stand on the ground.
                    float settle = p.MassKg * 9.81f * 0.25f / Mathf.Max(1f, (p.SpringFront + p.SpringRear) * 0.5f);
                    for (int w = 0; w < 4; w++) rest.SetCompression(w, Mathf.Clamp(settle, 0.01f, p.MaxCompressionM));
                    view.Render(rest, rest, 1f, default, 0f);
                    if (closeUp)
                    {
                        // Front-left wheel from the side.
                        Vector3 wheel = view.transform.TransformPoint(new Vector3(-p.WidthM * 0.5f, 0.3f - p.CgHeightM, p.FrontAxleZ));
                        cam.transform.position = wheel + view.transform.rotation * new Vector3(-2.2f, 0.25f, 0.3f);
                        cam.transform.LookAt(wheel);
                    }
                    else
                    {
                        cam.transform.position = new Vector3(3.1f, 1.45f, 4.3f);
                        cam.transform.LookAt(new Vector3(0f, 0.5f, 0f));
                    }
                    cam.Render();
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, tileWidth, tileHeight), 0, 0);
                    read.Apply();
                    RenderTexture.active = null;
                    int cx = i % columns, cy = rows - 1 - i / columns;
                    sheet.SetPixels(cx * tileWidth, cy * tileHeight, tileWidth, tileHeight, read.GetPixels());
                    Object.DestroyImmediate(view.gameObject);
                }
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(read);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
            sheet.Apply();
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Object.DestroyImmediate(sheet);
        }
    }
}
