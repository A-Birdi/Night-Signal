using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NightSignal.Cameras
{
    /// <summary>
    /// One place that creates/configures gameplay cameras for URP: post-processing on (so each course's authored Volume —
    /// tonemapping, bloom, grading — actually applies), SMAA, sensible clip planes. Cameras created with a bare
    /// <c>new GameObject(typeof(Camera))</c> skip post-processing and look washed out.
    /// </summary>
    public static class CameraRig
    {
        public static Camera EnsureMain(string name = "MainCamera")
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject(name, typeof(Camera));
                go.tag = "MainCamera";
                cam = go.GetComponent<Camera>();
            }
            Configure(cam);
            return cam;
        }

        public static void Configure(Camera cam)
        {
            if (cam == null) return;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 4000f;
            cam.allowHDR = true;
            UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.Medium;
            if (cam.GetComponent<AudioListener>() == null && Object.FindAnyObjectByType<AudioListener>() == null)
                cam.gameObject.AddComponent<AudioListener>();
        }
    }
}
