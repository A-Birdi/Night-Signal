using UnityEngine;

namespace NightSignal.Track.Generation
{
    /// <summary>Shared course materials and terrain layers (committed assets referenced by every course scene).</summary>
    [CreateAssetMenu(menuName = "Night Signal/Course Material Set")]
    public sealed class CourseMaterialSet : ScriptableObject
    {
        public Material Asphalt;
        public Material Gravel;
        public Material LinePaint;
        public Material Guardrail;
        public Material Post;
        public Material Stone;
        public Material WoodDark;
        public Material WoodLight;
        public Material RoofTiles;
        public Material LanternPaper;
        public Material Rope;
        public Material Water;
        public Material OffWhite;
        public Material Graphite;
        public Material TerrainTemplate;
        /// <summary>Order: verge, field grass, tea rows, soil, rock.</summary>
        public TerrainLayer[] TerrainLayers;
    }

    /// <summary>What to generate: players need visuals + collision; the headless server only collision.</summary>
    public enum GenerationProfile { Full = 0, CollisionOnly = 1 }
}
