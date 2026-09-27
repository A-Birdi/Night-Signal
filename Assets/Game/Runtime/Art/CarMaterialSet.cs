using UnityEngine;

namespace NightSignal.Art
{
    /// <summary>Shared car materials. Paint is instanced per car (livery colour via property block/instance).</summary>
    [CreateAssetMenu(menuName = "Night Signal/Car Material Set")]
    public sealed class CarMaterialSet : ScriptableObject
    {
        public Material Paint;
        public Material Glass;
        public Material Trim;
        public Material HeadLamp;
        public Material TailLamp;
        public Material Chrome;
        public Material Rubber;
        public Material Rim;
        public Material GhostPaint;

        public CarMaterials ForPaint(Material paintInstance) => new CarMaterials
        {
            Paint = paintInstance != null ? paintInstance : Paint,
            Glass = Glass, Trim = Trim, HeadLamp = HeadLamp, TailLamp = TailLamp, Chrome = Chrome, Rubber = Rubber, Rim = Rim,
        };
    }
}
