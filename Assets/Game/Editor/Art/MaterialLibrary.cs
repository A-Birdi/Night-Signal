using System.IO;
using UnityEditor;
using UnityEngine;

namespace NightSignal.Editor.Art
{
    /// <summary>Shared URP/Lit material assets built from the procedural textures (created once, reused by all courses).</summary>
    public static class MaterialLibrary
    {
        public const string Folder = "Assets/Art/Materials/Generated";
        static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
        static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        static readonly int Metallic = Shader.PropertyToID("_Metallic");
        static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        public static Material Get(string name, Texture2D albedo, Color tint, float smoothness, float metallic = 0f,
            Texture2D normal = null, Vector2? tiling = null, Color? emission = null)
        {
            string path = $"{Folder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            Directory.CreateDirectory(Folder);
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            if (albedo != null) mat.SetTexture(BaseMap, albedo);
            mat.SetColor(BaseColor, tint);
            mat.SetFloat(Smoothness, smoothness);
            mat.SetFloat(Metallic, metallic);
            if (tiling.HasValue) mat.SetTextureScale(BaseMap, tiling.Value);
            if (normal != null)
            {
                mat.SetTexture(BumpMap, normal);
                if (tiling.HasValue) mat.SetTextureScale(BumpMap, tiling.Value);
                mat.EnableKeyword("_NORMALMAP");
            }
            if (emission.HasValue)
            {
                mat.SetColor(EmissionColor, emission.Value);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            mat.enableInstancing = true;
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        public static TerrainLayer TerrainLayer(string name, Texture2D tex, float tile)
        {
            string path = $"{Folder}/Terrain_{name}.terrainlayer";
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer != null) return layer;
            Directory.CreateDirectory(Folder);
            layer = new TerrainLayer { diffuseTexture = tex, tileSize = new Vector2(tile, tile), smoothness = 0.05f };
            AssetDatabase.CreateAsset(layer, path);
            return layer;
        }

        public static Material TerrainTemplate
        {
            get
            {
                string path = $"{Folder}/TerrainLit.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat != null) return mat;
                Directory.CreateDirectory(Folder);
                mat = new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit")) { name = "TerrainLit" };
                AssetDatabase.CreateAsset(mat, path);
                return mat;
            }
        }

        public static Material Asphalt => Get("Asphalt", ProceduralTextures.Asphalt(), Color.white, 0.28f, 0f, ProceduralTextures.AsphaltNormal());
        public static Material Gravel => Get("Gravel", ProceduralTextures.Gravel(), Color.white, 0.12f);
        public static Material LinePaint => Get("LinePaint", null, new Color(0.92f, 0.92f, 0.88f), 0.45f);
        public static Material Guardrail => Get("GuardrailSteel", null, new Color(0.72f, 0.74f, 0.76f), 0.55f, 0.85f);
        public static Material Post => Get("PostGalvanised", null, new Color(0.55f, 0.57f, 0.58f), 0.4f, 0.7f);
        public static Material Stone => Get("StoneBlock", ProceduralTextures.Stone(), Color.white, 0.18f);
        public static Material WoodDark => Get("WoodPlanksDark", ProceduralTextures.WoodPlanks(), new Color(0.8f, 0.75f, 0.7f), 0.22f);
        public static Material WoodLight => Get("WoodPlanksLight", ProceduralTextures.WoodPlanks(), new Color(1.1f, 1.0f, 0.9f), 0.25f);
        public static Material RoofTiles => Get("RoofTiles", ProceduralTextures.RoofTiles(), Color.white, 0.35f);
        public static Material LanternPaper => Get("LanternPaper", ProceduralTextures.Paper(), new Color(1f, 0.92f, 0.8f), 0.2f, 0f, null, null, new Color(2.2f, 1.25f, 0.55f));
        public static Material Rope => Get("RopeDark", null, new Color(0.12f, 0.1f, 0.08f), 0.1f);
        public static Material Water => Get("CreekWater", null, new Color(0.10f, 0.16f, 0.18f, 1f), 0.93f, 0.1f);
        public static Material TeaLeaf => Get("TeaLeaf", ProceduralTextures.Grass(), new Color(0.55f, 0.95f, 0.55f), 0.35f);
        public static Material RedLacquer => Get("RedLacquer", null, new Color(0.62f, 0.11f, 0.08f), 0.55f);
        public static Material SignalRed => Get("SignalRed", null, new Color(0.84f, 0.12f, 0.12f), 0.5f);
        public static Material Graphite => Get("Graphite", null, new Color(0.14f, 0.15f, 0.17f), 0.4f);
        public static Material OffWhite => Get("OffWhite", null, new Color(0.9f, 0.88f, 0.82f), 0.3f);
    }
}
