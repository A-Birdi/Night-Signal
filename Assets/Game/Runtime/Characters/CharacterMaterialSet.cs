using System.Collections.Generic;
using UnityEngine;

namespace NightSignal.Characters
{
    /// <summary>
    /// Base materials for characters (skin, hair, cloth, dark, light, metal); <see cref="For"/> tints instances per look.
    /// Instances are cached by base and colour so characters that share a colour share a material (SRP-batcher friendly).
    /// </summary>
    [CreateAssetMenu(menuName = "Night Signal/Character Material Set")]
    public sealed class CharacterMaterialSet : ScriptableObject
    {
        public Material Skin;
        public Material Hair;
        public Material Cloth;
        public Material Dark;
        public Material Light;
        public Material Metal;

        static readonly Dictionary<(Material, Color32), Material> Cache = new Dictionary<(Material, Color32), Material>();

        public static CharacterMaterialSet Load() => Resources.Load<CharacterMaterialSet>("CharacterMaterialSet");

        /// <summary>One material per <see cref="CharacterBuilder.Slot"/>.</summary>
        public Material[] For(CharacterLook look)
        {
            var m = new Material[(int)CharacterBuilder.Slot.Count];
            m[(int)CharacterBuilder.Slot.Skin] = Tint(Skin, look.Skin, "#E3B996");
            m[(int)CharacterBuilder.Slot.Hair] = Tint(Hair, look.HairColour, "#2A2220");
            m[(int)CharacterBuilder.Slot.Top] = Tint(Cloth, look.Primary, "#3A5A7A");
            m[(int)CharacterBuilder.Slot.Under] = Tint(Cloth, look.Secondary, "#E8E2D0");
            m[(int)CharacterBuilder.Slot.Accent] = Tint(Cloth, look.Accent, "#D0602A");
            m[(int)CharacterBuilder.Slot.Lower] = Tint(Cloth, look.LowerColour, "#34363C");
            m[(int)CharacterBuilder.Slot.Shoes] = Tint(Cloth, look.ShoeColour, "#E8E8E4");
            m[(int)CharacterBuilder.Slot.Dark] = Dark;
            m[(int)CharacterBuilder.Slot.Light] = Light;
            m[(int)CharacterBuilder.Slot.Metal] = Metal;
            return m;
        }

        public static Color Parse(string hex, string fallback)
        {
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out Color c)) return c;
            ColorUtility.TryParseHtmlString(fallback, out Color f);
            return f;
        }

        static Material Tint(Material baseMat, string hex, string fallback)
        {
            if (baseMat == null) return null;
            Color32 col = Parse(hex, fallback);
            if (Cache.TryGetValue((baseMat, col), out Material m) && m != null) return m;
            m = new Material(baseMat) { name = $"{baseMat.name}_{ColorUtility.ToHtmlStringRGB(col)}", hideFlags = HideFlags.DontSave };
            m.SetColor("_BaseColor", col);
            Cache[(baseMat, col)] = m;
            return m;
        }
    }
}
