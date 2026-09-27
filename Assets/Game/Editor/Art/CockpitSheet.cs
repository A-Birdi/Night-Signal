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
    /// Renders the mounted views (Cockpit, Hood, Bumper) of every car from the same anchors the driving camera uses, in an
    /// isolated preview scene with a road-grey ground, in daylight and in a dark (night/tunnel) setting. A design aid and
    /// supporting evidence for Addendum 03 C02/C03 — the driven contact sheet from the camera tour is the primary proof.
    /// Output: Builds/Screenshots/cameras/cockpit-*.png.
    /// </summary>
    public static class CockpitSheet
    {
        public enum Mount { Cockpit, Hood, Bumper }

        [MenuItem("Night Signal/Art/Render Cockpit Sheets")]
        public static void RenderDefault()
        {
            string dir = Path.GetFullPath(Path.Combine("Builds", "Screenshots", "cameras"));
            Directory.CreateDirectory(dir);
            Render(Path.Combine(dir, "cockpit-day.png"), Mount.Cockpit, false);
            Render(Path.Combine(dir, "cockpit-night.png"), Mount.Cockpit, true);
            Render(Path.Combine(dir, "hood-day.png"), Mount.Hood, false);
            Render(Path.Combine(dir, "bumper-day.png"), Mount.Bumper, false);
            Debug.Log("[NightSignal.Art] cockpit sheets written to " + dir);
        }

        public static void Render(string path, Mount mount, bool dark, List<string> cars = null, int columns = 6, int tileWidth = 640, int tileHeight = 360)
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
                key.intensity = dark ? 0.04f : 1.2f;
                lightGo.transform.rotation = Quaternion.Euler(35f, 150f, 0f);

                var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(ground.GetComponent<Collider>());
                SceneManager.MoveGameObjectToScene(ground, scene);
                ground.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(90f, 0f, 0f));
                ground.transform.localScale = new Vector3(400f, 400f, 1f);
                var groundMat = new Material(mats.Trim) { name = "SheetGround" };
                groundMat.SetColor("_BaseColor", new Color(0.23f, 0.235f, 0.25f));
                temp.Add(groundMat);
                ground.GetComponent<MeshRenderer>().sharedMaterial = groundMat;
                // Lane marks ahead, so the road reads through the glass.
                for (int i = 0; i < 12; i++)
                {
                    var mark = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Object.DestroyImmediate(mark.GetComponent<Collider>());
                    SceneManager.MoveGameObjectToScene(mark, scene);
                    mark.transform.SetPositionAndRotation(new Vector3(0f, 0.01f, 6f + i * 9f), Quaternion.Euler(90f, 0f, 0f));
                    mark.transform.localScale = new Vector3(0.15f, 3f, 1f);
                    var mm = new Material(mats.Trim);
                    mm.SetColor("_BaseColor", new Color(0.85f, 0.85f, 0.8f));
                    temp.Add(mm);
                    mark.GetComponent<MeshRenderer>().sharedMaterial = mm;
                }

                var camGo = new GameObject("SheetCamera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(camGo, scene);
                Camera cam = camGo.GetComponent<Camera>();
                cam.scene = scene;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = dark ? new Color(0.02f, 0.025f, 0.04f) : new Color(0.55f, 0.66f, 0.8f);
                cam.fieldOfView = 58f + (mount == Mount.Cockpit ? 4f : 0f);
                cam.nearClipPlane = mount == Mount.Cockpit ? 0.03f : 0.05f;
                cam.farClipPlane = 800f;
                var rt = new RenderTexture(tileWidth, tileHeight, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;
                var read = new Texture2D(tileWidth, tileHeight, TextureFormat.RGB24, false);
                for (int i = 0; i < cars.Count; i++)
                {
                    VehicleParams p = lib.Params(cars[i], AssistSettings.Default);
                    VehicleView view = VehicleView.Create("SheetCar", p, lib.Body(cars[i]), mats, new Color(0.72f, 0.1f, 0.12f));
                    SceneManager.MoveGameObjectToScene(view.gameObject, scene);
                    view.transform.SetPositionAndRotation(new Vector3(0f, p.CgHeightM, 0f), Quaternion.identity);
                    var rest = VehicleState.AtRest(view.transform.position, view.transform.rotation);
                    view.Render(rest, rest, 1f, default, 0f);
                    view.SetHeadlights(dark);
                    CarBodyGenerator.CabinFrame f = CarBodyGenerator.Cabin(view.Def, p);
                    if (mount == Mount.Cockpit)
                    {
                        view.SetCockpitMode(true);
                        view.Cockpit.Update(0.05f, 26.8224f, UI.SpeedUnit.Kmh, 4200f, p.RedlineRpm, 3);
                    }
                    Transform mountT = mount == Mount.Bumper ? view.transform : view.Body;
                    Vector3 local = mount == Mount.Cockpit ? f.Eye : mount == Mount.Hood ? f.Hood : f.Bumper;
                    if (mount == Mount.Bumper) local += Vector3.down * (view.Body != null ? -view.Body.localPosition.y : 0f);
                    cam.transform.SetPositionAndRotation(mountT.TransformPoint(local), Quaternion.LookRotation(mountT.forward, mountT.up));
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
                foreach (Object o in temp) Object.DestroyImmediate(o);
            }
            sheet.Apply();
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Object.DestroyImmediate(sheet);
        }
    }
}
