using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using NightSignal.Characters;
using NightSignal.Core.Meet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NightSignal.Editor.ArtTools
{
    /// <summary>
    /// Character contact sheets and the silhouette distinctness check for the 48 rivals (spec: "recognizable silhouette/colour
    /// sheets"; "identical outfits/faces are not the entire player system"): front, side and three-quarter views, portraits,
    /// white-on-black silhouettes, and an animation strip (walk, jog, the twelve emotes). <see cref="Measure"/> compares every
    /// pair's front+side silhouette (intersection over union at a fixed metric scale) and colour layout.
    /// Output: Builds/Screenshots/characters/.
    /// </summary>
    public static partial class CharacterSheet
    {
        public const string LooksPath = "Assets/Content/Data/authored/story/rivals.look.json";

        public enum View { Front, Side, Quarter, Portrait, Back }

        public static List<CharacterLook> LoadLooks(string path = LooksPath) =>
            JsonConvert.DeserializeObject<CharacterLookFile>(File.ReadAllText(path)).Looks;

        [MenuItem("Night Signal/Art/Render Character Sheets")]
        public static void RenderDefault() => RenderAll(Path.GetFullPath(Path.Combine("Builds", "Screenshots", "characters")));

        public static void RenderAll(string dir)
        {
            Directory.CreateDirectory(dir);
            List<CharacterLook> looks = LoadLooks();
            Render(Path.Combine(dir, "rivals-front.png"), looks, View.Front);
            Render(Path.Combine(dir, "rivals-quarter.png"), looks, View.Quarter);
            Render(Path.Combine(dir, "rivals-side.png"), looks, View.Side);
            Render(Path.Combine(dir, "rivals-back.png"), looks, View.Back);
            Render(Path.Combine(dir, "rivals-portrait.png"), looks, View.Portrait);
            Distinctness d = Measure(looks, Path.Combine(dir, "rivals-silhouette.png"));
            File.WriteAllText(Path.Combine(dir, "distinctness.txt"), d.Report(looks));
            RenderMotion(Path.Combine(dir, "motion-strip.png"), looks[0]);
            Debug.Log($"[NightSignal.Art] character sheets written to {dir}; max silhouette IoU {d.MaxIoU:F3} ({d.MaxPair})");
        }

        // ------------------------------------------------------------------ stage

        sealed class Stage : System.IDisposable
        {
            public Scene Scene;
            public Camera Cam;
            public RenderTexture Rt;
            public Texture2D Read;
            public GameObject Ground;
            readonly List<Object> temp = new List<Object>();
            readonly List<GameObject> lights = new List<GameObject>();

            public Stage(int w, int h, bool lit, Color background, int aa = 8)
            {
                Scene = EditorSceneManager.NewPreviewScene();
                if (lit)
                {
                    AddLight("Key", 1.2f, Color.white, Quaternion.Euler(32f, 150f, 0f), true);
                    AddLight("Fill", 0.45f, new Color(0.78f, 0.84f, 1f), Quaternion.Euler(18f, -40f, 0f), false);
                    AddLight("Rim", 0.5f, new Color(1f, 0.92f, 0.85f), Quaternion.Euler(25f, 20f, 0f), false);
                    Ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Object.DestroyImmediate(Ground.GetComponent<Collider>());
                    SceneManager.MoveGameObjectToScene(Ground, Scene);
                    Ground.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(90f, 0f, 0f));
                    Ground.transform.localScale = new Vector3(60f, 60f, 1f);
                    var gm = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "SheetGround" };
                    gm.SetColor("_BaseColor", new Color(0.34f, 0.35f, 0.37f));
                    gm.SetFloat("_Smoothness", 0.1f);
                    temp.Add(gm);
                    Ground.GetComponent<MeshRenderer>().sharedMaterial = gm;
                }
                var camGo = new GameObject("SheetCamera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(camGo, Scene);
                Cam = camGo.GetComponent<Camera>();
                Cam.scene = Scene;
                Cam.clearFlags = CameraClearFlags.SolidColor;
                Cam.backgroundColor = background;
                Cam.nearClipPlane = 0.05f;
                Cam.farClipPlane = 100f;
                Rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = aa };
                Cam.targetTexture = Rt;
                Read = new Texture2D(w, h, TextureFormat.RGB24, false);
            }

            void AddLight(string name, float intensity, Color c, Quaternion rot, bool shadows)
            {
                var go = new GameObject(name, typeof(Light));
                SceneManager.MoveGameObjectToScene(go, Scene);
                Light l = go.GetComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = intensity;
                l.color = c;
                l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
                go.transform.rotation = rot;
                lights.Add(go);
            }

            public CharacterRig Spawn(CharacterLook look)
            {
                CharacterRig rig = CharacterRig.Create(look, CharacterMaterialSet.Load());
                SceneManager.MoveGameObjectToScene(rig.gameObject, Scene);
                return rig;
            }

            public Color[] Shoot()
            {
                Cam.Render();
                RenderTexture.active = Rt;
                Read.ReadPixels(new Rect(0, 0, Rt.width, Rt.height), 0, 0);
                Read.Apply();
                RenderTexture.active = null;
                return Read.GetPixels();
            }

            public void Dispose()
            {
                Cam.targetTexture = null;
                Object.DestroyImmediate(Rt);
                Object.DestroyImmediate(Read);
                EditorSceneManager.ClosePreviewScene(Scene);
                foreach (Object o in temp) Object.DestroyImmediate(o);
            }
        }

        static void Aim(Camera cam, View view, float height)
        {
            cam.orthographic = view == View.Front || view == View.Side || view == View.Back;
            switch (view)
            {
                case View.Front:
                case View.Back:
                case View.Side:
                {
                    cam.orthographicSize = 1.05f;
                    var target = new Vector3(0f, 1.02f, 0f);
                    Vector3 dir = view == View.Front ? Vector3.forward : view == View.Back ? Vector3.back : Vector3.right;
                    cam.transform.SetPositionAndRotation(target + dir * 6f, Quaternion.LookRotation(-dir, Vector3.up));
                    break;
                }
                case View.Quarter:
                {
                    var target = new Vector3(0f, height * 0.55f, 0f);
                    Vector3 eye = target + Quaternion.Euler(0f, 32f, 0f) * new Vector3(0f, 0.25f, 5.2f);
                    cam.fieldOfView = 26f;
                    cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
                    break;
                }
                case View.Portrait:
                {
                    var target = new Vector3(0f, height * 0.9f, 0f);
                    Vector3 eye = target + Quaternion.Euler(0f, 24f, 0f) * new Vector3(0f, 0.02f, 1.35f);
                    cam.fieldOfView = 24f;
                    cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
                    break;
                }
            }
        }

        /// <summary>A sheet of every look in one view, row-major (12 per row).</summary>
        public static void Render(string path, List<CharacterLook> looks, View view, int columns = 12)
        {
            int tw = view == View.Portrait ? 256 : 200, th = view == View.Portrait ? 256 : 400;
            int rows = (looks.Count + columns - 1) / columns;
            var sheet = new Texture2D(tw * columns, th * rows, TextureFormat.RGB24, false);
            using (var stage = new Stage(tw, th, true, new Color(0.6f, 0.68f, 0.78f)))
            {
                for (int i = 0; i < looks.Count; i++)
                {
                    CharacterRig rig = stage.Spawn(looks[i]);
                    var motion = rig.gameObject.AddComponent<CharacterMotion>();
                    motion.Pose(0f);
                    Aim(stage.Cam, view, rig.Skeleton.H);
                    Color[] px = stage.Shoot();
                    int cx = i % columns, cy = rows - 1 - i / columns;
                    sheet.SetPixels(cx * tw, cy * th, tw, th, px);
                    Object.DestroyImmediate(rig.gameObject);
                }
            }
            sheet.Apply();
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Object.DestroyImmediate(sheet);
        }

        /// <summary>One character through idle, walk, jog and the twelve emotes (each near its peak).</summary>
        public static void RenderMotion(string path, CharacterLook look)
        {
            const int tw = 240, th = 400;
            var frames = new List<System.Action<CharacterMotion>>
            {
                m => m.Pose(0f),
                m => { m.Speed = CharacterMotion.WalkSpeed; for (int i = 0; i < 40; i++) m.Pose(1f / 60f); },
                m => { m.Speed = CharacterMotion.JogSpeed; for (int i = 0; i < 40; i++) m.Pose(1f / 60f); },
            };
            foreach (Emote e in Emotes.Wheel)
            {
                Emote em = e;
                frames.Add(m => { m.Play(em, Emotes.Duration(em) * 0.45f); m.Pose(0.0001f); });
            }
            int columns = 5, rows = (frames.Count + columns - 1) / columns;
            var sheet = new Texture2D(tw * columns, th * rows, TextureFormat.RGB24, false);
            using (var stage = new Stage(tw, th, true, new Color(0.6f, 0.68f, 0.78f)))
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    CharacterRig rig = stage.Spawn(look);
                    var motion = rig.gameObject.AddComponent<CharacterMotion>();
                    frames[i](motion);
                    var target = new Vector3(0f, rig.Skeleton.H * 0.5f, 0f);
                    Vector3 eye = target + Quaternion.Euler(0f, 40f, 0f) * new Vector3(0f, 0.3f, 6.2f);
                    stage.Cam.orthographic = false;
                    stage.Cam.fieldOfView = 24f;
                    stage.Cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
                    Color[] px = stage.Shoot();
                    int cx = i % columns, cy = rows - 1 - i / columns;
                    sheet.SetPixels(cx * tw, cy * th, tw, th, px);
                    Object.DestroyImmediate(rig.gameObject);
                }
            }
            sheet.Apply();
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Object.DestroyImmediate(sheet);
        }

        // ------------------------------------------------------------------ distinctness

        public sealed class Distinctness
        {
            /// <summary>Pairwise front+side silhouette IoU and mean colour-layout difference (0–1).</summary>
            public float[,] IoU, Colour;
            public float MaxIoU;
            public string MaxPair = "";
            /// <summary>Pairs whose silhouettes overlap above the limit AND whose colour layouts are close: confusable.</summary>
            public List<string> Confusable = new List<string>();

            public const float SilhouetteLimit = 0.9f, ColourLimit = 0.12f;

            public string Report(List<CharacterLook> looks)
            {
                var sb = new StringBuilder();
                int n = looks.Count;
                sb.AppendLine("Character distinctness: front+side silhouette IoU (fixed 1.1 cm/px scale, rest pose) and colour-layout");
                sb.AppendLine($"difference (mean RGB distance over a 6x12 grid of the lit front view). Confusable = IoU > {SilhouetteLimit:F2} and colour < {ColourLimit:F2}.");
                sb.AppendLine($"Max IoU {MaxIoU:F3} ({MaxPair}); confusable pairs: {Confusable.Count}");
                foreach (string c in Confusable) sb.AppendLine("  CONFUSABLE " + c);
                sb.AppendLine();
                sb.AppendLine("id    nearest-silhouette  IoU    colour-diff-to-it  nearest-colour  diff");
                for (int i = 0; i < n; i++)
                {
                    int bs = -1, bc = -1;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i) continue;
                        if (bs < 0 || IoU[i, j] > IoU[i, bs]) bs = j;
                        if (bc < 0 || Colour[i, j] < Colour[i, bc]) bc = j;
                    }
                    sb.AppendLine($"{looks[i].Id,-5} {looks[bs].Id,-18}  {IoU[i, bs]:F3}  {Colour[i, bs],-17:F3}  {looks[bc].Id,-14}  {Colour[i, bc]:F3}");
                }
                return sb.ToString();
            }
        }

        /// <summary>Front and side masks (white-on-black, unlit, no AA) and colour signatures; optional silhouette sheet.</summary>
        public static Distinctness Measure(List<CharacterLook> looks, string sheetPath = null)
        {
            const int tw = 96, th = 192, gx = 6, gy = 12;
            int n = looks.Count;
            var masks = new bool[n][];
            var sig = new Vector3[n][];
            var unlit = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Silhouette" };
            unlit.SetColor("_BaseColor", Color.white);
            Texture2D sheet = sheetPath != null ? new Texture2D(tw * 2 * 12, th * ((n + 11) / 12), TextureFormat.RGB24, false) : null;
            try
            {
                using (var stage = new Stage(tw, th, false, Color.black, 1))
                {
                    for (int i = 0; i < n; i++)
                    {
                        CharacterRig rig = stage.Spawn(looks[i]);
                        rig.gameObject.AddComponent<CharacterMotion>().Pose(0f);
                        var mats = new Material[rig.Body.sharedMaterials.Length];
                        for (int k = 0; k < mats.Length; k++) mats[k] = unlit;
                        rig.Body.sharedMaterials = mats;
                        masks[i] = new bool[tw * th * 2];
                        for (int v = 0; v < 2; v++)
                        {
                            Aim(stage.Cam, v == 0 ? View.Front : View.Side, rig.Skeleton.H);
                            Color[] px = stage.Shoot();
                            for (int p = 0; p < px.Length; p++) masks[i][v * tw * th + p] = px[p].r > 0.5f;
                            if (sheet != null)
                            {
                                int cx = (i % 12) * 2 + v, cy = (n + 11) / 12 - 1 - i / 12;
                                sheet.SetPixels(cx * tw, cy * th, tw, th, px);
                            }
                        }
                        Object.DestroyImmediate(rig.gameObject);
                    }
                }
                using (var stage = new Stage(tw, th, true, Color.black, 1))
                {
                    for (int i = 0; i < n; i++)
                    {
                        CharacterRig rig = stage.Spawn(looks[i]);
                        rig.gameObject.AddComponent<CharacterMotion>().Pose(0f);
                        stage.Ground.SetActive(false);
                        Aim(stage.Cam, View.Front, rig.Skeleton.H);
                        Color[] px = stage.Shoot();
                        sig[i] = new Vector3[gx * gy];
                        var count = new int[gx * gy];
                        for (int y = 0; y < th; y++)
                        for (int x = 0; x < tw; x++)
                        {
                            int p = y * tw + x;
                            if (!masks[i][p]) continue;
                            int cell = (y * gy / th) * gx + x * gx / tw;
                            sig[i][cell] += new Vector3(px[p].r, px[p].g, px[p].b);
                            count[cell]++;
                        }
                        for (int c = 0; c < sig[i].Length; c++) if (count[c] > 0) sig[i][c] /= count[c];
                        Object.DestroyImmediate(rig.gameObject);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(unlit);
            }
            var d = new Distinctness { IoU = new float[n, n], Colour = new float[n, n] };
            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                int inter = 0, union = 0;
                bool[] a = masks[i], b = masks[j];
                for (int p = 0; p < a.Length; p++)
                {
                    if (a[p] && b[p]) inter++;
                    if (a[p] || b[p]) union++;
                }
                float iou = union == 0 ? 0f : inter / (float)union;
                float col = 0f;
                int cells = 0;
                for (int c = 0; c < sig[i].Length; c++)
                {
                    if (sig[i][c] == Vector3.zero && sig[j][c] == Vector3.zero) continue;
                    col += (sig[i][c] - sig[j][c]).magnitude / Mathf.Sqrt(3f);
                    cells++;
                }
                col = cells == 0 ? 0f : col / cells;
                d.IoU[i, j] = d.IoU[j, i] = iou;
                d.Colour[i, j] = d.Colour[j, i] = col;
                if (iou > d.MaxIoU) { d.MaxIoU = iou; d.MaxPair = $"{looks[i].Id}–{looks[j].Id}"; }
                if (iou > Distinctness.SilhouetteLimit && col < Distinctness.ColourLimit)
                    d.Confusable.Add($"{looks[i].Id}–{looks[j].Id} IoU {iou:F3} colour {col:F3}");
            }
            if (sheet != null)
            {
                sheet.Apply();
                File.WriteAllBytes(sheetPath, sheet.EncodeToPNG());
                Object.DestroyImmediate(sheet);
            }
            return d;
        }
    }
}
