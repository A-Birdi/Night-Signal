using System.Diagnostics;
using NightSignal.Atmosphere;
using NightSignal.Track.Generation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NightSignal.Track
{
    /// <summary>
    /// Scene entry point for a course. Generates the course deterministically from its versioned route document
    /// (full visuals for players, collision only for the dedicated server), exposes the track data, and applies
    /// the event's locked conditions before the loading barrier completes.
    /// </summary>
    [ExecuteAlways]
    public sealed class CourseRuntime : MonoBehaviour
    {
        public TextAsset RouteJson;
        public CourseMaterialSet Materials;
        public TerrainStyle Style = new TerrainStyle();
        public string DefaultTimeOfDay = "day";
        public Light Sun;
        public Material Sky;
        [Tooltip("Force collision-only generation (dedicated server). Batch-mode players do this automatically.")]
        public bool CollisionOnly;

        public TrackData Track { get; private set; }
        public RouteDefinition Route { get; private set; }
        public string SourceHash { get; private set; }
        public float GenerationSeconds { get; private set; }
        public static CourseRuntime Active { get; private set; }

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

        /// <summary>
        /// Leaving the course in play: release what generation created (road/terrain meshes, the ~24 MB terrain data, the
        /// track data). They are runtime assets, not scene objects, so unloading the scene alone kept every course ever
        /// loaded in memory (found by the soak: native memory grew ~20 MB per race).
        /// </summary>
        void OnDestroy()
        {
            if (Application.isPlaying) Clear();
        }

        public void Generate()
        {
            if (RouteJson == null || Materials == null) return;
            Clear();
            var sw = Stopwatch.StartNew();
            Route = RouteIO.Parse(RouteJson.text);
            SourceHash = RouteIO.SourceHash(RouteJson.text);
            Track = CourseGenerator.BuildTrackData(Route, SourceHash);

            generated = new GameObject($"Generated_{Route.Course}");
            // Generated content is rebuilt from source on load; it never serializes into the scene.
            generated.hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave;
            generated.transform.SetParent(transform, false);
            GenerationProfile profile = CollisionOnly || Application.isBatchMode ? GenerationProfile.CollisionOnly : GenerationProfile.Full;
            CourseGenerator.BuildGeometry(Track, Route, generated.transform, Materials, Style, profile);
            SetHideFlagsRecursive(generated.transform, generated.hideFlags);
            ApplyConditions(DefaultTimeOfDay);
            GenerationSeconds = (float)sw.Elapsed.TotalSeconds;
            Debug.Log($"[NightSignal.Course] {Route.Course} generated ({profile}) in {GenerationSeconds:F2} s — {Track.LengthMetres:F0} m, source {SourceHash.Substring(0, 12)}");
        }

        void Clear()
        {
            if (generated != null)
            {
                // Generated meshes and terrain data are runtime objects owned by this component.
                foreach (MeshFilter mf in generated.GetComponentsInChildren<MeshFilter>(true)) Release(mf.sharedMesh);
                foreach (MeshCollider mc in generated.GetComponentsInChildren<MeshCollider>(true)) Release(mc.sharedMesh);
                foreach (TerrainCollider tc in generated.GetComponentsInChildren<TerrainCollider>(true)) Release(tc.terrainData);
                Release(generated);
                generated = null;
            }
            if (Track != null)
            {
                Release(Track);
                Track = null;
            }
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

        /// <summary>Surface grip for the chassis: dry 1.0, damp 0.88, wet 0.76.</summary>
        public static float SurfaceGrip(string surface) => surface == "wet" ? 0.76f : surface == "damp" ? 0.88f : 1f;

        /// <summary>The time of day the course is lit for (set by <see cref="ApplyConditions"/>).</summary>
        public string TimeOfDay { get; private set; }

        /// <summary>Practical lights and car headlights belong on at this time of day.</summary>
        public bool Dark => LightingPresets.For(string.IsNullOrEmpty(TimeOfDay) ? DefaultTimeOfDay : TimeOfDay).PracticalLights;

        public void ApplyConditions(string timeOfDay)
        {
            TimeOfDay = string.IsNullOrEmpty(timeOfDay) ? DefaultTimeOfDay : timeOfDay;
            LightingPreset preset = LightingPresets.For(TimeOfDay);
            if (Sun != null) LightingPresets.Apply(preset, Sun, Sky);
        }
    }
}
