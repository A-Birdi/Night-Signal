using System.Collections.Generic;
using UnityEngine;

namespace NightSignal.Art
{
    /// <summary>
    /// The visible choices for one car, already resolved for its chassis (every id valid; stock where nothing was chosen).
    /// Appearance never changes a simulation input: mechanical parts live in Core/Builds. Vocabulary: front stock|lip|aero|
    /// track, rear stock|diffuser|valance, side stock|skirt|sculpted, rear aero stock|ducktail|wing|gt-wing|lip-spoiler,
    /// exhaust stock|dual|quad|center, rims 5-spoke|6-spoke|mesh|split|dish|turbofan|multi-spoke|3-spoke.
    /// </summary>
    public sealed class CarAppearance
    {
        public string Front = "stock", Rear = "stock", Side = "stock", RearAero = "stock", Exhaust = "stock";
        /// <summary>Rim render style; null = the chassis' stock rim.</summary>
        public string RimStyle;
        /// <summary>Rim diameter as a fraction of the tyre radius; ≤ 0 = the chassis' stock fitment.</summary>
        public float RimFraction;
        public Color Primary = new Color(0.82f, 0.12f, 0.12f), Secondary = new Color(0.1f, 0.1f, 0.11f), Accent = new Color(0.08f, 0.08f, 0.09f);
        public Color RimColor = new Color(0.72f, 0.73f, 0.75f);
        /// <summary>gloss | metallic | pearl | matte | satin</summary>
        public string Finish = "gloss";
        /// <summary>none | lower | roof | hood-stripe | side-stripe</summary>
        public string TwoTone = "none";
        /// <summary>clear | amber | smoke-light</summary>
        public string HeadTint = "clear", TailTint = "clear";
        public string PlateText = "";
        public readonly List<CarDecal> Decals = new List<CarDecal>();

        public static CarAppearance Stock(Color paint) => new CarAppearance { Primary = paint };

        /// <summary>Key of everything that changes the generated meshes (colours and decals are material/overlay work).</summary>
        public string GeometryKey => $"{Front}|{Rear}|{Side}|{RearAero}|{Exhaust}|{TwoTone}|{RimStyle}|{RimFraction:0.000}";

        /// <summary>URP Lit metallic/smoothness for a paint finish.</summary>
        public static (float Metallic, float Smoothness) FinishValues(string finish)
        {
            switch (finish)
            {
                case "metallic": return (0.65f, 0.82f);
                case "pearl": return (0.35f, 0.9f);
                case "matte": return (0.05f, 0.18f);
                case "satin": return (0.15f, 0.45f);
                default: return (0.2f, 0.86f); // gloss
            }
        }

        public static Color LampTint(string tint, Color baseColor)
        {
            switch (tint)
            {
                case "amber": return Color.Lerp(baseColor, new Color(1f, 0.62f, 0.15f), 0.7f);
                case "smoke-light": return baseColor * 0.72f; // darker, never invisible
                default: return baseColor;
            }
        }
    }

    /// <summary>One decal layer (shape from the library, zone-relative placement).</summary>
    public sealed class CarDecal
    {
        public string ShapeId = "";
        public Color Color = Color.white;
        /// <summary>hood | roof | left | right | rear | front</summary>
        public string Zone = "left";
        public float U = 0.5f, V = 0.5f, Scale = 0.3f, RotationDeg;
        public bool Mirror, Flip;
    }
}
