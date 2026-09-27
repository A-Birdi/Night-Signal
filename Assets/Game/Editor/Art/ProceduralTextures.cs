using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NightSignal.Editor.Art
{
    /// <summary>
    /// Deterministic original texture recipes (seeded noise), baked to PNG under Assets/Art/Textures/Generated.
    /// Re-running produces identical files; recipes are the preserved source (spec §3.1, §17).
    /// </summary>
    public static class ProceduralTextures
    {
        public const string Folder = "Assets/Art/Textures/Generated";

        public delegate Color PixelFn(float u, float v);

        public static Texture2D GetOrCreate(string name, int size, PixelFn fn, bool normalMap = false, bool linear = false)
        {
            string path = $"{Folder}/{name}.png";
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Folder);
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, linear || normalMap);
                var px = new Color[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = fn((x + 0.5f) / size, (y + 0.5f) / size);
                tex.SetPixels(px);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var imp = (TextureImporter)AssetImporter.GetAtPath(path);
                imp.textureType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                imp.sRGBTexture = !(linear || normalMap);
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.mipmapEnabled = true;
                imp.anisoLevel = 8;
                imp.filterMode = FilterMode.Trilinear;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---------- noise ----------

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 144665;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
            }
        }

        /// <summary>Tileable value noise with period <paramref name="period"/> cells.</summary>
        public static float Value(float u, float v, int period, int seed)
        {
            float x = u * period, y = v * period;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            int X0 = Mod(x0, period), Y0 = Mod(y0, period), X1 = Mod(x0 + 1, period), Y1 = Mod(y0 + 1, period);
            float a = Hash(X0, Y0, seed), b = Hash(X1, Y0, seed), c = Hash(X0, Y1, seed), d = Hash(X1, Y1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        public static float Fbm(float u, float v, int basePeriod, int octaves, int seed, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            int period = basePeriod;
            for (int o = 0; o < octaves; o++)
            {
                sum += Value(u, v, period, seed + o * 31) * amp;
                norm += amp;
                amp *= gain;
                period *= 2;
            }
            return sum / norm;
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;

        // ---------- recipes ----------

        public static Texture2D Asphalt() => GetOrCreate("asphalt_albedo", 1024, (u, v) =>
        {
            float grain = Fbm(u, v, 64, 4, 11);
            float speck = Hash(Mathf.FloorToInt(u * 1024), Mathf.FloorToInt(v * 1024), 3) > 0.985f ? 0.12f : 0f;
            float patch = Fbm(u, v, 4, 3, 17) * 0.06f;
            float g = 0.16f + grain * 0.07f + speck + patch;
            return new Color(g, g * 1.01f, g * 1.04f, 1f);
        });

        public static Texture2D AsphaltNormal() => GetOrCreate("asphalt_normal", 1024, (u, v) =>
        {
            const float e = 1f / 1024f;
            float h = Fbm(u, v, 128, 3, 21);
            float hx = Fbm(u + e, v, 128, 3, 21), hy = Fbm(u, v + e, 128, 3, 21);
            var n = new Vector3((h - hx) * 6f, (h - hy) * 6f, 1f).normalized;
            return new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
        }, normalMap: true);

        public static Texture2D Gravel() => GetOrCreate("gravel_albedo", 512, (u, v) =>
        {
            float stones = Fbm(u, v, 48, 3, 41);
            float light = Hash(Mathf.FloorToInt(u * 256), Mathf.FloorToInt(v * 256), 5) * 0.12f;
            float g = 0.33f + stones * 0.18f + light;
            return new Color(g * 1.05f, g, g * 0.9f, 1f);
        });

        public static Texture2D Grass() => GetOrCreate("grass_albedo", 1024, (u, v) =>
        {
            float n = Fbm(u, v, 16, 5, 51);
            float blades = Fbm(u, v, 256, 2, 53);
            return new Color(0.20f + n * 0.12f, 0.33f + n * 0.14f + blades * 0.05f, 0.12f + n * 0.05f, 1f);
        });

        public static Texture2D TeaRows() => GetOrCreate("tea_rows_albedo", 1024, (u, v) =>
        {
            float row = Mathf.Abs(Mathf.Sin(v * Mathf.PI * 16f));
            float bush = Fbm(u, v, 64, 3, 61);
            float gap = Mathf.SmoothStep(0.0f, 0.35f, row);
            Color leaf = new Color(0.10f + bush * 0.08f, 0.30f + bush * 0.12f, 0.10f, 1f);
            Color soil = new Color(0.28f, 0.22f, 0.16f, 1f);
            return Color.Lerp(soil, leaf, gap);
        });

        public static Texture2D Soil() => GetOrCreate("soil_albedo", 512, (u, v) =>
        {
            float n = Fbm(u, v, 24, 4, 71);
            return new Color(0.30f + n * 0.12f, 0.24f + n * 0.09f, 0.17f + n * 0.06f, 1f);
        });

        public static Texture2D Rock() => GetOrCreate("rock_albedo", 1024, (u, v) =>
        {
            float n = Fbm(u, v, 12, 5, 81, 0.55f);
            float strata = Mathf.Sin((v + n * 0.2f) * 60f) * 0.04f;
            float g = 0.36f + n * 0.2f + strata;
            return new Color(g, g * 0.98f, g * 0.95f, 1f);
        });

        public static Texture2D Stone() => GetOrCreate("stone_block_albedo", 512, (u, v) =>
        {
            float rowF = v * 6f;
            int row = Mathf.FloorToInt(rowF);
            float shift = (row % 2) * 0.5f;
            float colF = u * 4f + shift;
            float mortar = Mathf.Min(Mathf.Min(rowF - row, 1f - (rowF - row)), Mathf.Min(colF - Mathf.Floor(colF), 1f - (colF - Mathf.Floor(colF))) * 1.5f);
            float face = 0.42f + Hash(Mathf.FloorToInt(colF), row, 91) * 0.12f + Fbm(u, v, 32, 3, 93) * 0.1f;
            return mortar < 0.04f ? new Color(0.30f, 0.29f, 0.27f, 1f) : new Color(face, face * 0.97f, face * 0.92f, 1f);
        });

        public static Texture2D WoodPlanks() => GetOrCreate("wood_planks_albedo", 512, (u, v) =>
        {
            float plank = Mathf.Floor(u * 8f);
            float grain = Fbm(u * 0.25f + plank * 0.37f, v, 32, 3, 101);
            float seam = (u * 8f - plank) < 0.04f ? 0.55f : 1f;
            float tone = 0.26f + Hash((int)plank, 0, 103) * 0.08f + grain * 0.1f;
            return new Color(tone * 1.15f * seam, tone * 0.82f * seam, tone * 0.55f * seam, 1f);
        });

        public static Texture2D RoofTiles() => GetOrCreate("roof_tiles_albedo", 512, (u, v) =>
        {
            float ridge = Mathf.Abs(Mathf.Sin(u * Mathf.PI * 10f));
            float course = (v * 12f) - Mathf.Floor(v * 12f);
            float shade = 0.18f + ridge * 0.1f - (course < 0.12f ? 0.06f : 0f) + Fbm(u, v, 16, 2, 111) * 0.05f;
            return new Color(shade * 0.95f, shade, shade * 1.08f, 1f);
        });

        public static Texture2D Paper() => GetOrCreate("lantern_paper_albedo", 256, (u, v) =>
        {
            float ribs = Mathf.Abs(Mathf.Sin(v * Mathf.PI * 9f)) < 0.08f ? 0.7f : 1f;
            float fibre = 0.92f + Fbm(u, v, 32, 2, 121) * 0.08f;
            return new Color(1f * fibre * ribs, 0.93f * fibre * ribs, 0.82f * fibre * ribs, 1f);
        });
    }
}
