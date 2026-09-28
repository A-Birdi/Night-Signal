using System.Diagnostics;
using NightSignal.Atmosphere;
using NightSignal.Track.Generation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NightSignal.Meet
{
    /// <summary>
    /// The meet scene's entry point (like a course's): the scene holds only this, the sun, sky and post-processing; Cedar
    /// Lantern Terrace is generated from the shared layout whenever the scene loads (collision only on the headless room
    /// server). Lighting is the fixed launch-scene sunset with lighter haze than a race, so the valley and ridges read.
    /// </summary>
    [ExecuteAlways]
    public sealed class MeetRuntime : MonoBehaviour
    {
        public CourseMaterialSet Materials;
        public Light Sun;
        public Material Sky;
        [Tooltip("Force collision-only generation (room server). Batch-mode players do this automatically.")]
        public bool CollisionOnly;

        public static MeetRuntime Active { get; private set; }
        public float GenerationSeconds { get; private set; }
        public GameObject Generated => generated;

        GameObject generated;

        void OnEnable()
        {
            Active = this;
            if (Application.isPlaying || generated == null) Generate();
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            if (!Application.isPlaying) Clear();
        }

        void OnDestroy()
        {
            if (Application.isPlaying) Clear();
        }

        public void Generate()
        {
            if (Materials == null) return;
            Clear();
            var sw = Stopwatch.StartNew();
            generated = new GameObject("Generated_Meet") { hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave };
            generated.transform.SetParent(transform, false);
            GenerationProfile profile = CollisionOnly || Application.isBatchMode ? GenerationProfile.CollisionOnly : GenerationProfile.Full;
            LandmarkKits.BuildMeet(Materials, generated.transform, profile);
            SetHideFlagsRecursive(generated.transform, generated.hideFlags);
            ApplyLighting();
            GenerationSeconds = (float)sw.Elapsed.TotalSeconds;
            Debug.Log($"[NightSignal.Meet] Cedar Lantern Terrace generated ({profile}) in {GenerationSeconds:F2} s");
        }

        /// <summary>The fixed launch-scene light: sunset over the western cedars, warm on the mountains to the east.</summary>
        public void ApplyLighting()
        {
            if (Sun == null) return;
            LightingPreset p = LightingPresets.For("sunset");
            p.SunElevationDeg = 15f;
            p.SunAzimuthDeg = 245f;
            p.SunIntensity = 1.5f;
            p.SkyExposure = 1.35f;
            p.FogDensity = 0.0004f;
            p.AmbientIntensity = 1.15f;
            LightingPresets.Apply(p, Sun, Sky);
            RenderSettings.fogColor = new Color(0.78f, 0.6f, 0.55f);
        }

        void Clear()
        {
            if (generated == null) return;
            foreach (MeshFilter mf in generated.GetComponentsInChildren<MeshFilter>(true)) Release(mf.sharedMesh);
            foreach (MeshCollider mc in generated.GetComponentsInChildren<MeshCollider>(true)) Release(mc.sharedMesh);
            Release(generated);
            generated = null;
        }

        static void Release(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        static void SetHideFlagsRecursive(Transform t, HideFlags flags)
        {
            t.gameObject.hideFlags = flags;
            foreach (Transform c in t) SetHideFlagsRecursive(c, flags);
        }
    }
}
