using System;
using System.Collections.Generic;

namespace NightSignal.Characters
{
    /// <summary>
    /// How one person looks (rivals from <c>authored/story/rivals.look.json</c>, players from their card): proportions, hair,
    /// outfit, colours, accessories and posture. Drives <see cref="CharacterBuilder"/>; purely visual (never gameplay).
    /// </summary>
    [Serializable]
    public sealed class CharacterLook
    {
        public string Id = "";
        /// <summary>Standing height in metres (1.45–1.98).</summary>
        public float Height = 1.72f;
        /// <summary>slim | average | athletic | sturdy | broad | petite | heavy</summary>
        public string Build = "average";
        /// <summary>Limb length relative to the torso (0.94–1.08; "long limbs" ≈ 1.06).</summary>
        public float Limbs = 1f;
        /// <summary>Skin tone, #RRGGBB.</summary>
        public string Skin = "#E3B996";
        /// <summary>
        /// cropped | buzz | shaved | receding | bob | shoulder | long | low-tie | ponytail | bun | topknot | twin-tails | twin-braids |
        /// braid | side-braid | crown-braid | locs | spiky | swept | curly | high-top | undercut
        /// </summary>
        public string Hair = "cropped";
        public string HairColour = "#2A2220";
        /// <summary>none | moustache | beard | goatee | stubble</summary>
        public string FacialHair = "none";
        /// <summary>
        /// tee | longsleeve | shirt | tunic | sweater | smock | hoodie | vest | waistcoat | cardigan | jacket | bomber |
        /// windbreaker | coat | uniform | cape | poncho | coveralls | overalls | jumpsuit-tied | apron
        /// </summary>
        public string Outfit = "jacket";
        /// <summary>long | rolled | short | none (the visible sleeves: the outer layer's, or the under layer's for vests and capes)</summary>
        public string Sleeves = "long";
        /// <summary>Outer layer hem: cropped | hip | thigh | knee ("" = the outfit's usual length).</summary>
        public string Length = "";
        /// <summary>Outfit colours, #RRGGBB (outer garment, under layer, accent trim).</summary>
        public string Primary = "#3A5A7A", Secondary = "#E8E2D0", Accent = "#D0602A";
        /// <summary>trousers | narrow | shorts | skirt | cargo</summary>
        public string Lower = "trousers";
        public string LowerColour = "#34363C";
        /// <summary>sneakers | boots | loafers</summary>
        public string Shoes = "sneakers";
        public string ShoeColour = "#E8E8E4";
        /// <summary>
        /// glasses | round-glasses | sunglasses | cap | beanie | headband | bandana | barrette | scarf | headphones | gloves |
        /// glove-one | cuffs | watch | wristband | earrings | ear-cuff | goggles | lanyard | pin | medallion | whistle | belt |
        /// tool-belt | sash | key-reel | pouch | bag | satchel | camera | thermos | clipboard | helmet | patches; modifiers:
        /// open (an unfastened coat/jacket), high-collar, asymmetric (bob/ponytail), braided (undercut), glasses-cord,
        /// bright-laces
        /// </summary>
        public List<string> Accessories = new List<string>();
        /// <summary>neutral | forward | upright | slouch | relaxed | proud | guarded | stooped</summary>
        public string Posture = "neutral";
        /// <summary>calm | grin | serious | smile | sleepy</summary>
        public string Face = "calm";
    }

    [Serializable]
    public sealed class CharacterLookFile
    {
        public string Schema;
        public List<CharacterLook> Looks = new List<CharacterLook>();
    }

    /// <summary>The values <see cref="CharacterBuilder"/> understands (anything else silently falls back, so tests check looks against these).</summary>
    public static class CharacterVocabulary
    {
        public static readonly string[] Builds = { "slim", "average", "athletic", "sturdy", "broad", "petite", "heavy" };
        public static readonly string[] Hair =
        {
            "cropped", "buzz", "shaved", "receding", "bob", "shoulder", "long", "low-tie", "ponytail", "bun", "topknot", "twin-tails",
            "twin-braids", "braid", "side-braid", "crown-braid", "locs", "spiky", "swept", "curly", "high-top", "undercut",
        };
        public static readonly string[] FacialHair = { "none", "moustache", "beard", "goatee", "stubble" };
        public static readonly string[] Outfits =
        {
            "tee", "longsleeve", "shirt", "tunic", "sweater", "smock", "hoodie", "vest", "waistcoat", "cardigan", "jacket", "bomber",
            "windbreaker", "coat", "uniform", "cape", "poncho", "coveralls", "overalls", "jumpsuit-tied", "apron",
        };
        public static readonly string[] Sleeves = { "long", "rolled", "short", "none" };
        public static readonly string[] Lengths = { "", "cropped", "hip", "thigh", "knee" };
        public static readonly string[] Lowers = { "trousers", "narrow", "shorts", "skirt", "cargo" };
        public static readonly string[] Shoes = { "sneakers", "boots", "loafers" };
        public static readonly string[] Accessories =
        {
            "glasses", "round-glasses", "sunglasses", "cap", "beanie", "headband", "bandana", "barrette", "scarf", "headphones", "gloves",
            "glove-one", "cuffs", "watch", "wristband", "earrings", "ear-cuff", "goggles", "lanyard", "pin", "medallion", "whistle", "belt",
            "tool-belt", "sash", "key-reel", "pouch", "bag", "satchel", "camera", "thermos", "clipboard", "helmet", "patches",
            "open", "high-collar", "asymmetric", "braided", "glasses-cord", "bright-laces",
        };
        public static readonly string[] Postures = { "neutral", "forward", "upright", "slouch", "relaxed", "proud", "guarded", "stooped" };
        public static readonly string[] Faces = { "calm", "grin", "serious", "smile", "sleepy" };

        /// <summary>Problems with a look (unknown words, bad colours, out-of-range sizes); empty when it builds as authored.</summary>
        public static List<string> Check(CharacterLook l)
        {
            var bad = new List<string>();
            void In(string field, string value, string[] set)
            {
                if (Array.IndexOf(set, value ?? "") < 0) bad.Add($"{l.Id}: {field} '{value}'");
            }
            void Colour(string field, string value)
            {
                if (value == null || value.Length != 7 || value[0] != '#' || !int.TryParse(value.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out _))
                    bad.Add($"{l.Id}: {field} colour '{value}'");
            }
            In("build", l.Build, Builds);
            In("hair", l.Hair, Hair);
            In("facialHair", l.FacialHair, FacialHair);
            In("outfit", l.Outfit, Outfits);
            In("sleeves", l.Sleeves, Sleeves);
            In("length", l.Length, Lengths);
            In("lower", l.Lower, Lowers);
            In("shoes", l.Shoes, Shoes);
            In("posture", l.Posture, Postures);
            In("face", l.Face, Faces);
            foreach (string a in l.Accessories ?? new List<string>()) In("accessory", a, Accessories);
            Colour("skin", l.Skin); Colour("hairColour", l.HairColour); Colour("primary", l.Primary); Colour("secondary", l.Secondary);
            Colour("accent", l.Accent); Colour("lowerColour", l.LowerColour); Colour("shoeColour", l.ShoeColour);
            if (l.Height < 1.45f || l.Height > 1.98f) bad.Add($"{l.Id}: height {l.Height}");
            if (l.Limbs < 0.92f || l.Limbs > 1.1f) bad.Add($"{l.Id}: limbs {l.Limbs}");
            return bad;
        }
    }
}
