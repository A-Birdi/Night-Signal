using System.Collections.Generic;
using System.IO;
using NightSignal.Art;
using NightSignal.Characters;
using NightSignal.Content;
using NightSignal.Core.Meet;
using NightSignal.Editor.Courses;
using NightSignal.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace NightSignal.Editor.ArtTools
{
    /// <summary>
    /// Photographs Cedar Lantern Terrace from fixed viewpoints (overview, photo marker, kiosk, both bay rows, the overlook,
    /// the entry lane, the garden, the boombox) with display cars in the bays and rivals standing about, in the scene's own
    /// lighting. Opens the meet scene, renders, then leaves an empty scene open again (the editor's resting state).
    /// Output: Builds/Screenshots/meet/.
    /// </summary>
    public static class MeetSheet
    {
        public struct Shot
        {
            public string Name;
            public Vector3 Eye, Target;
            public float Fov;
            public Shot(string name, Vector3 eye, Vector3 target, float fov = 55f) { Name = name; Eye = eye; Target = target; Fov = fov; }
        }

        public static List<Shot> Shots()
        {
            var pm = new Vector3(MeetLayout.PhotoMarker.X, 0f, MeetLayout.PhotoMarker.Z);
            return new List<Shot>
            {
                new Shot("01-overview", new Vector3(-10f, 58f, -62f), new Vector3(2f, 0f, 8f), 60f),
                new Shot("02-photo-marker", pm + new Vector3(0f, 1.55f, 0f), pm + new Vector3(60f, 0.2f, 0f), 58f),
                new Shot("03-kiosk", new Vector3(-6f, 1.65f, 20f), new Vector3(0f, 2f, 42f), 60f),
                new Shot("04-west-bays", new Vector3(-36f, 1.7f, -30f), new Vector3(-54f, 1.2f, 4f), 60f),
                new Shot("05-east-bays", new Vector3(38f, 1.7f, 30f), new Vector3(54f, 1.0f, -6f), 60f),
                new Shot("06-overlook", new Vector3(62f, 1.7f, -8f), new Vector3(400f, -60f, 40f), 62f),
                new Shot("07-entry-lane", new Vector3(20f, 1.7f, -33f), new Vector3(-50f, 0.8f, -41f), 60f),
                new Shot("08-garden", new Vector3(9f, 1.65f, -17f), new Vector3(-1f, 0.2f, -1f), 60f),
                new Shot("09-boombox", new Vector3(11.5f, 1.55f, 37f), new Vector3(14.5f, 0.9f, 41.6f), 52f),
                new Shot("10-north-from-south", new Vector3(0f, 1.7f, -34f), new Vector3(0f, 3f, 40f), 62f),
                new Shot("11-kiosk-noren", new Vector3(9.8f, 1.6f, 45.2f), new Vector3(7.1f, 1.75f, 46.2f), 50f),
                new Shot("12-viewpoint-placard", new Vector3(65.4f, 1.5f, 7.6f), new Vector3(67.4f, 0.95f, 9f), 50f),
            };
        }

        [MenuItem("Night Signal/Art/Render Meet Sheets")]
        public static void RenderDefault() => RenderAll(Path.GetFullPath(Path.Combine("Builds", "Screenshots", "meet")));

        public static string RenderAll(string dir, int width = 1600, int height = 900)
        {
            Directory.CreateDirectory(dir);
            Scene scene = EditorSceneManager.OpenScene(CourseSceneAuthoring.MeetScene, OpenSceneMode.Single);
            var temp = new List<Object>();
            var log = new System.Text.StringBuilder();
            try
            {
                var meet = Object.FindAnyObjectByType<NightSignal.Meet.MeetRuntime>();
                if (meet == null) return "no MeetRuntime in the meet scene";
                meet.Generate();
                Stage(temp);
                var camGo = new GameObject("MeetSheetCamera", typeof(Camera)) { hideFlags = HideFlags.DontSave };
                temp.Add(camGo);
                Camera cam = camGo.GetComponent<Camera>();
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 9000f;
                camGo.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = true;
                var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;
                var read = new Texture2D(width, height, TextureFormat.RGB24, false);
                foreach (Shot s in Shots())
                {
                    cam.fieldOfView = s.Fov;
                    cam.transform.SetPositionAndRotation(s.Eye, Quaternion.LookRotation(s.Target - s.Eye, Vector3.up));
                    cam.Render();
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    read.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes(Path.Combine(dir, s.Name + ".png"), read.EncodeToPNG());
                    log.AppendLine(s.Name);
                }
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(read);
                log.AppendLine($"generation {meet.GenerationSeconds:F2} s");
            }
            finally
            {
                foreach (Object o in temp) if (o != null) Object.DestroyImmediate(o);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            return log.ToString();
        }

        /// <summary>Display cars in ten of the bays (two left free) and a dozen rivals about the plaza, for the photographs.</summary>
        static void Stage(List<Object> temp)
        {
            ContentLibrary lib = ContentLibrary.Load();
            var carMats = Resources.Load<CarMaterialSet>("CarMaterialSet");
            string[] cars = { "V01", "V04", "V07", "V10", "V13", "V16", "V02", "V05", "V08", "V11", "V14", "V17" };
            Color[] paints =
            {
                new Color(0.18f, 0.42f, 0.31f), new Color(0.85f, 0.34f, 0.16f), new Color(0.1f, 0.12f, 0.14f), new Color(0.72f, 0.25f, 0.06f),
                new Color(0.95f, 0.95f, 0.93f), new Color(0.12f, 0.2f, 0.34f), new Color(0.55f, 0.4f, 0.3f), new Color(0.12f, 0.16f, 0.2f),
                new Color(0.18f, 0.23f, 0.2f), new Color(0.75f, 0.77f, 0.8f), new Color(0.24f, 0.35f, 0.5f), new Color(0.42f, 0.44f, 0.36f),
            };
            for (int i = 0; i < 12; i++)
            {
                if (i == 3 || i == 9) continue; // free bays
                MeetBay b = MeetLayout.Bays[i];
                VehicleParams p = lib.Params(cars[i], AssistSettings.Default);
                VehicleView v = VehicleView.Create($"Display_{cars[i]}", p, lib.Body(cars[i]), carMats, paints[i]);
                v.gameObject.hideFlags = HideFlags.DontSave;
                v.ShowParked(new Vector3(b.X, 0f, b.Z), Quaternion.Euler(0f, b.Yaw, 0f), 0f);
                temp.Add(v.gameObject);
            }
            List<CharacterLook> looks = CharacterSheet.LoadLooks();
            var spots = new[]
            {
                (Id: "R01", X: -46.5f, Z: -12f, Yaw: 250f, E: Emote.Admire), (Id: "R07", X: -44f, Z: -10.5f, Yaw: 230f, E: Emote.Point),
                (Id: "R11", X: -18f, Z: 9.5f, Yaw: 90f, E: Emote.CameraPose), (Id: "R25", X: -15.5f, Z: 8f, Yaw: 270f, E: Emote.Wave),
                (Id: "R17", X: 3f, Z: 30f, Yaw: 200f, E: Emote.None), (Id: "R04", X: 5f, Z: 31f, Yaw: 160f, E: Emote.Clap),
                (Id: "R22", X: -4f, Z: 38.5f, Yaw: 180f, E: Emote.None), (Id: "R37", X: 16.5f, Z: 40.5f, Yaw: 200f, E: Emote.None),
                (Id: "R30", X: 46f, Z: 7f, Yaw: 100f, E: Emote.ThumbsUp), (Id: "R14", X: 44f, Z: 9f, Yaw: 120f, E: Emote.None),
                (Id: "R28", X: 12f, Z: -14f, Yaw: 330f, E: Emote.Cheer), (Id: "R42", X: 14f, Z: 38f, Yaw: 20f, E: Emote.Nod),
            };
            foreach (var s in spots)
            {
                CharacterLook look = looks.Find(l => l.Id == s.Id);
                CharacterRig rig = CharacterRig.Create(look, CharacterMaterialSet.Load());
                rig.gameObject.hideFlags = HideFlags.DontSave;
                rig.transform.SetPositionAndRotation(new Vector3(s.X, 0f, s.Z), Quaternion.Euler(0f, s.Yaw, 0f));
                var m = rig.gameObject.AddComponent<CharacterMotion>();
                if (s.E != Emote.None) m.Play(s.E, Emotes.Duration(s.E) * 0.45f);
                m.Pose(0.0001f);
                temp.Add(rig.gameObject);
            }
        }
    }
}
