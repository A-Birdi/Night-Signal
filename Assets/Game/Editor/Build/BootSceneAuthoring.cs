using System.IO;
using NightSignal.Boot;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NightSignal.Editor.Build
{
    /// <summary>Creates the Boot scene (GameEntry + a fallback camera). Idempotent.</summary>
    public static class BootSceneAuthoring
    {
        [MenuItem("Night Signal/Build/Author Boot Scene")]
        public static void Author()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BuildCommands.BootScene));
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("GameEntry").AddComponent<GameEntry>();
            var cam = new GameObject("BootCamera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.08f, 0.1f);
            EditorSceneManager.SaveScene(scene, BuildCommands.BootScene);
        }
    }
}
