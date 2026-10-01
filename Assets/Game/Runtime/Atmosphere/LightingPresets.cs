using System;
using UnityEngine;

namespace NightSignal.Atmosphere
{
    /// <summary>Time-of-day lighting preset. Deterministic and visible before readiness (spec §5.4).</summary>
    [Serializable]
    public struct LightingPreset
    {
        public string Id;
        public float SunElevationDeg;
        public float SunAzimuthDeg;
        public Color SunColor;
        public float SunIntensity;
        public Color SkyTint;
        public Color GroundColor;
        public float SkyExposure;
        public float AtmosphereThickness;
        public Color FogColor;
        public float FogDensity;
        public float AmbientIntensity;
        /// <summary>Road/landmark practical lights and car headlights should be on.</summary>
        public bool PracticalLights;
    }

    public static class LightingPresets
    {
        public static LightingPreset For(string timeOfDay)
        {
            switch (timeOfDay)
            {
                case "day": return P("day", 55, 150, new Color(1f, 0.96f, 0.9f), 1.35f, new Color(0.5f, 0.6f, 0.75f), 1.2f, 1f, new Color(0.72f, 0.78f, 0.84f), 0.0006f, 1f, false);
                case "late-afternoon": return P("late-afternoon", 19, 245, new Color(1f, 0.8f, 0.58f), 1.45f, new Color(0.62f, 0.6f, 0.62f), 1.25f, 1.1f, new Color(0.83f, 0.72f, 0.6f), 0.0011f, 0.9f, false);
                case "sunset": return P("sunset", 6, 255, new Color(1f, 0.58f, 0.34f), 1.25f, new Color(0.7f, 0.5f, 0.5f), 1.15f, 1.35f, new Color(0.86f, 0.55f, 0.42f), 0.0014f, 0.75f, false);
                case "dusk": return P("dusk", -2, 262, new Color(0.9f, 0.45f, 0.3f), 0.55f, new Color(0.45f, 0.4f, 0.55f), 0.8f, 1.4f, new Color(0.42f, 0.36f, 0.45f), 0.0016f, 0.55f, true);
                case "blue-hour": return P("blue-hour", -5, 268, new Color(0.55f, 0.6f, 0.9f), 0.35f, new Color(0.25f, 0.33f, 0.6f), 0.55f, 1.1f, new Color(0.2f, 0.26f, 0.42f), 0.0018f, 0.45f, true);
                case "evening": return P("evening", -6, 270, new Color(0.5f, 0.52f, 0.8f), 0.3f, new Color(0.2f, 0.25f, 0.45f), 0.45f, 1f, new Color(0.16f, 0.2f, 0.32f), 0.0016f, 0.4f, true);
                case "night": return P("night", 35, 120, new Color(0.55f, 0.65f, 0.95f), 0.22f, new Color(0.05f, 0.08f, 0.18f), 0.22f, 0.7f, new Color(0.05f, 0.07f, 0.13f), 0.0013f, 0.35f, true);
                case "pre-dawn": return P("pre-dawn", -8, 80, new Color(0.55f, 0.6f, 0.9f), 0.26f, new Color(0.12f, 0.16f, 0.35f), 0.35f, 1f, new Color(0.12f, 0.15f, 0.26f), 0.0015f, 0.38f, true);
                case "dawn": return P("dawn", 3, 85, new Color(1f, 0.66f, 0.46f), 0.95f, new Color(0.55f, 0.5f, 0.62f), 0.95f, 1.3f, new Color(0.72f, 0.6f, 0.62f), 0.0015f, 0.6f, true);
                case "first-light": return P("first-light", 8, 90, new Color(1f, 0.78f, 0.6f), 1.15f, new Color(0.62f, 0.62f, 0.7f), 1.05f, 1.15f, new Color(0.78f, 0.72f, 0.72f), 0.0012f, 0.75f, false);
                // Freeplay's "Fog, damp" preset (Core ConditionPresets): a weak, diffuse morning sun in dense valley fog — about a
                // third of the scene left at 250 m — with headlights and practical lights on. The sky tint makes the procedural
                // sky's scattering wavelengths equal (0.80 - 0.30 x tint per channel, gamma), so the sky is a neutral grey that the
                // fog colour matches instead of a blue sky or a red low-sun sky.
                case "fog": return P("fog", 18, 100, new Color(0.9f, 0.9f, 0.88f), 0.55f, new Color(1f, 0.733f, 0.417f), 0.9f, 1f, new Color(0.66f, 0.68f, 0.7f), 0.0042f, 1f, true);
                default: throw new ArgumentException($"Unknown time of day '{timeOfDay}'");
            }
        }

        static LightingPreset P(string id, float elev, float az, Color sun, float intensity, Color sky, float exposure,
            float atmosphere, Color fog, float fogDensity, float ambient, bool practical) => new LightingPreset
        {
            Id = id, SunElevationDeg = elev, SunAzimuthDeg = az, SunColor = sun, SunIntensity = intensity,
            SkyTint = sky, GroundColor = new Color(0.25f, 0.24f, 0.22f), SkyExposure = exposure, AtmosphereThickness = atmosphere,
            FogColor = fog, FogDensity = fogDensity, AmbientIntensity = ambient, PracticalLights = practical,
        };

        /// <summary>Applies a preset to the active scene's sun, procedural sky and fog.</summary>
        public static void Apply(LightingPreset p, Light sun, Material proceduralSky)
        {
            // Below the horizon the "sun" light becomes the moon/sky fill from the opposite side.
            bool moon = p.SunElevationDeg <= 0f || p.Id == "night";
            float elevation = moon ? Mathf.Max(20f, p.SunElevationDeg) : p.SunElevationDeg;
            sun.transform.rotation = Quaternion.Euler(elevation, p.SunAzimuthDeg, 0f);
            sun.color = p.SunColor;
            sun.intensity = p.SunIntensity;
            sun.shadows = LightShadows.Soft;
            if (proceduralSky != null)
            {
                proceduralSky.SetColor("_SkyTint", p.SkyTint);
                proceduralSky.SetColor("_GroundColor", p.GroundColor);
                proceduralSky.SetFloat("_Exposure", p.SkyExposure);
                proceduralSky.SetFloat("_AtmosphereThickness", p.AtmosphereThickness);
                proceduralSky.SetFloat("_SunSize", moon ? 0.02f : 0.045f);
                RenderSettings.skybox = proceduralSky;
            }
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
            RenderSettings.ambientIntensity = p.AmbientIntensity;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = p.FogColor;
            RenderSettings.fogDensity = p.FogDensity;
            // Recompute the skybox ambient for the sky just set (Unity's documented step after a runtime skybox change); on the
            // built player it moved C01's default and night frames by only 1-3 levels, so earlier looks are unchanged.
            DynamicGI.UpdateEnvironment();
        }
    }
}
