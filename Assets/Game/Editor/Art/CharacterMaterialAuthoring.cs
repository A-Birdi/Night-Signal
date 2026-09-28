using System.IO;
using NightSignal.Characters;
using UnityEditor;
using UnityEngine;

namespace NightSignal.Editor.Art
{
    /// <summary>Creates the character base materials (skin, hair, cloth, dark, light, metal); looks tint instances at runtime.</summary>
    public static class CharacterMaterialAuthoring
    {
        public const string SetPath = "Assets/Content/Resources/CharacterMaterialSet.asset";
        const string Folder = "Assets/Art/Materials/Characters";

        [MenuItem("Night Signal/Characters/Ensure Character Materials")]
        public static CharacterMaterialSet Ensure()
        {
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(Path.GetDirectoryName(SetPath));
            var set = AssetDatabase.LoadAssetAtPath<CharacterMaterialSet>(SetPath);
            bool create = set == null;
            if (create) set = ScriptableObject.CreateInstance<CharacterMaterialSet>();
            set.Skin = Lit("CharSkin", new Color(0.89f, 0.73f, 0.59f), 0.36f, 0f);
            set.Hair = Lit("CharHair", new Color(0.16f, 0.13f, 0.12f), 0.3f, 0f);
            set.Cloth = Lit("CharCloth", new Color(0.5f, 0.5f, 0.5f), 0.12f, 0f);
            set.Dark = Lit("CharDark", new Color(0.07f, 0.07f, 0.08f), 0.45f, 0f);
            set.Light = Lit("CharLight", new Color(0.95f, 0.94f, 0.91f), 0.55f, 0f);
            set.Metal = Lit("CharMetal", new Color(0.74f, 0.72f, 0.68f), 0.7f, 0.9f);
            if (create) AssetDatabase.CreateAsset(set, SetPath);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return set;
        }

        static Material Lit(string name, Color c, float smooth, float metal)
        {
            string path = $"{Folder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", metal);
            m.enableInstancing = true;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }
    }
}
