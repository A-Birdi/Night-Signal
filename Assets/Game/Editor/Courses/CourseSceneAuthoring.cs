using System.IO;
using NightSignal.Editor.Art;
using NightSignal.Track;
using NightSignal.Track.Generation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace NightSignal.Editor.Courses
{
    /// <summary>
    /// Authors a course scene (decision D-007): the scene holds only the course entry point, sun, sky and
    /// post-processing; geometry is generated from the versioned route document whenever the scene loads.
    /// </summary>
    public static class CourseSceneAuthoring
    {
        public const string MaterialSetPath = "Assets/Art/Materials/CourseMaterialSet.asset";

        public static string CourseFolder(string id) => $"Assets/Content/Courses/{id}";
        public static string ScenePath(string id) => $"{CourseFolder(id)}/{id}.unity";

        [MenuItem("Night Signal/Courses/Author C01 Scene")]
        public static void AuthorC01() => Author("C01", "late-afternoon", new TerrainStyle { TeaRowCoverage = 0.35f, Seed = 101 });

        public static string Author(string courseId, string timeOfDay, TerrainStyle style)
        {
            // Create the scene first: opening a new single scene unloads unused assets, invalidating earlier loads.
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CourseMaterialSet mats = EnsureMaterialSet();
            var route = AssetDatabase.LoadAssetAtPath<TextAsset>(RouteIO.RoutePath(courseId));
            if (route == null) throw new FileNotFoundException("Route document missing", RouteIO.RoutePath(courseId));

            string folder = CourseFolder(courseId);
            string skyPath = $"{folder}/{courseId}_Sky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Procedural")) { name = $"{courseId}_Sky" };
                AssetDatabase.CreateAsset(sky, skyPath);
            }
            VolumeProfile profile = EnsureVolumeProfile($"{folder}/{courseId}_Volume.asset");

            var root = new GameObject($"Course_{courseId}");
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(root.transform, false);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            var volGo = new GameObject("GlobalVolume");
            volGo.transform.SetParent(root.transform, false);
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = profile;

            root.SetActive(false);
            var rt = root.AddComponent<CourseRuntime>();
            rt.RouteJson = route;
            rt.Materials = mats;
            rt.Style = style;
            rt.DefaultTimeOfDay = timeOfDay;
            rt.Sun = sun;
            rt.Sky = sky;
            root.SetActive(true); // generates the preview (DontSave) and applies lighting

            EditorUtility.SetDirty(sky);
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(scene, ScenePath(courseId));
            return $"{courseId} scene authored; generation {rt.GenerationSeconds:F2} s, length {rt.Track.LengthMetres:F0} m";
        }

        public static CourseMaterialSet EnsureMaterialSet()
        {
            var set = AssetDatabase.LoadAssetAtPath<CourseMaterialSet>(MaterialSetPath);
            bool create = set == null;
            if (create) set = ScriptableObject.CreateInstance<CourseMaterialSet>();
            set.Asphalt = MaterialLibrary.Asphalt;
            set.Gravel = MaterialLibrary.Gravel;
            set.LinePaint = MaterialLibrary.LinePaint;
            set.Guardrail = MaterialLibrary.Guardrail;
            set.Post = MaterialLibrary.Post;
            set.Stone = MaterialLibrary.Stone;
            set.WoodDark = MaterialLibrary.WoodDark;
            set.WoodLight = MaterialLibrary.WoodLight;
            set.RoofTiles = MaterialLibrary.RoofTiles;
            set.LanternPaper = MaterialLibrary.LanternPaper;
            set.Rope = MaterialLibrary.Rope;
            set.Water = MaterialLibrary.Water;
            set.OffWhite = MaterialLibrary.OffWhite;
            set.Graphite = MaterialLibrary.Graphite;
            set.Concrete = MaterialLibrary.Concrete;
            set.Brick = MaterialLibrary.Brick;
            set.SteelRed = MaterialLibrary.SteelRed;
            set.SteelGrey = MaterialLibrary.SteelGrey;
            set.SteelYellow = MaterialLibrary.SteelYellow;
            set.WindowLit = MaterialLibrary.WindowLit;
            set.MetalRoof = MaterialLibrary.MetalRoof;
            set.Foliage = MaterialLibrary.Foliage;
            set.Bark = MaterialLibrary.Bark;
            set.Sea = MaterialLibrary.Sea;
            set.TunnelLining = MaterialLibrary.TunnelLining;
            set.TerrainTemplate = MaterialLibrary.TerrainTemplate;
            set.TerrainLayers = new[]
            {
                MaterialLibrary.TerrainLayer("Verge", ProceduralTextures.Gravel(), 4f),
                MaterialLibrary.TerrainLayer("FieldGrass", ProceduralTextures.Grass(), 9f),
                MaterialLibrary.TerrainLayer("TeaRows", ProceduralTextures.TeaRows(), 18f),
                MaterialLibrary.TerrainLayer("Soil", ProceduralTextures.Soil(), 7f),
                MaterialLibrary.TerrainLayer("Rock", ProceduralTextures.Rock(), 14f),
            };
            if (create) AssetDatabase.CreateAsset(set, MaterialSetPath);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return set;
        }

        static VolumeProfile EnsureVolumeProfile(string path)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile != null) return profile;
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.1f);
            bloom.intensity.Override(0.35f);
            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.15f);
            color.contrast.Override(8f);
            color.saturation.Override(6f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.18f);
            foreach (VolumeComponent c in profile.components) AssetDatabase.AddObjectToAsset(c, profile);
            AssetDatabase.SaveAssets();
            return profile;
        }
    }
}
