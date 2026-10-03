using System.IO;
using NightSignal.Art;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NightSignal.Editor.Art
{
    /// <summary>Creates the shared car material set (Complex Lit clear-coat paint, glass, trim, lamps, chrome, tyres, seat cloth).</summary>
    public static class CarMaterialAuthoring
    {
        /// <summary>In a Resources folder so runtime code can load it without scene wiring.</summary>
        public const string SetPath = "Assets/Content/Resources/CarMaterialSet.asset";
        const string Folder = "Assets/Art/Materials/Cars";

        [MenuItem("Night Signal/Cars/Ensure Car Materials")]
        public static CarMaterialSet Ensure()
        {
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(Path.GetDirectoryName(SetPath));
            var set = AssetDatabase.LoadAssetAtPath<CarMaterialSet>(SetPath);
            bool create = set == null;
            if (create) set = ScriptableObject.CreateInstance<CarMaterialSet>();
            set.Paint = Paint("CarPaint", new Color(0.8f, 0.1f, 0.1f), 0.72f, 0.35f, clearCoat: true);
            set.PaintFlip = FlipPaint("CarPaintFlip", set.Paint);
            set.GhostPaint = Transparent("GhostPaint", new Color(0.35f, 0.85f, 1f, 0.28f), 0.8f);
            set.Glass = Transparent("CarGlass", new Color(0.06f, 0.08f, 0.1f, 0.62f), 0.95f);
            set.Trim = Lit("CarTrim", new Color(0.05f, 0.05f, 0.055f), 0.35f, 0f);
            set.HeadLamp = Lit("HeadLamp", new Color(0.95f, 0.95f, 0.9f), 0.9f, 0f, new Color(1.6f, 1.55f, 1.4f));
            set.TailLamp = Lit("TailLamp", new Color(0.55f, 0.02f, 0.02f), 0.85f, 0f, new Color(1.4f, 0.05f, 0.03f));
            set.Chrome = Lit("Chrome", new Color(0.85f, 0.86f, 0.88f), 0.9f, 1f);
            set.Rubber = Lit("TyreRubber", new Color(0.035f, 0.035f, 0.04f), 0.22f, 0f);
            set.Rim = Lit("RimAlloy", new Color(0.72f, 0.73f, 0.75f), 0.7f, 0.9f);
            set.Cloth = Lit("CarCloth", new Color(0.13f, 0.13f, 0.14f), 0.06f, 0f);
            if (create) AssetDatabase.CreateAsset(set, SetPath);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return set;
        }

        static Material Paint(string name, Color c, float smooth, float metal, bool clearCoat)
        {
            string path = $"{Folder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Complex Lit")) { name = name };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", metal);
            if (clearCoat)
            {
                m.SetFloat("_ClearCoat", 1f);
                m.SetFloat("_ClearCoatMask", 1f);
                m.SetFloat("_ClearCoatSmoothness", 0.95f);
                m.EnableKeyword("_CLEARCOAT");
            }
            m.enableInstancing = true;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        /// <summary>The body paint's settings (clear coat included) on the Car Paint shader, flip tint off until a livery sets it.</summary>
        static Material FlipPaint(string name, Material paint)
        {
            string path = $"{Folder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(paint) { name = name, shader = Shader.Find("Night Signal/Car Paint") };
            m.SetColor("_FlipColor", new Color(1f, 1f, 1f, 0f));
            m.SetFloat("_FlipPower", 2.5f);
            m.enableInstancing = true;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material Lit(string name, Color c, float smooth, float metal, Color? emission = null)
        {
            string path = $"{Folder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", metal);
            if (emission.HasValue)
            {
                m.SetColor("_EmissionColor", emission.Value);
                m.EnableKeyword("_EMISSION");
            }
            m.enableInstancing = true;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material Transparent(string name, Color c, float smooth)
        {
            string path = $"{Folder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            m.SetFloat("_Surface", 1f); // transparent
            m.SetFloat("_Blend", 0f);   // alpha
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }
    }
}
