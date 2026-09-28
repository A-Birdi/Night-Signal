using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace NightSignal.Characters
{
    /// <summary>
    /// A player's driver appearance on their Player Card (spec §11: accessible appearance choices, initial defaults instead of
    /// a long creator; purely visual, never gameplay). Shared by the game and the control plane: the server validates a
    /// submitted look against the vocabulary, stores its canonical JSON with the card and hands it to the meet room so
    /// other visitors build the same avatar.
    /// </summary>
    public static class PlayerLooks
    {
        /// <summary>At most this many accessories/modifiers on a player look (the rivals' sheets stay unrestricted).</summary>
        public const int MaxAccessories = 4;
        /// <summary>Upper bound on the stored JSON (a full look is ≈ 450 bytes).</summary>
        public const int MaxJsonLength = 2048;

        static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            MissingMemberHandling = MissingMemberHandling.Ignore,
            MaxDepth = 4,
        };

        /// <summary>A look from JSON (field names in any case), or null when it is not a look at all.</summary>
        public static CharacterLook Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength) return null;
            try { return JsonConvert.DeserializeObject<CharacterLook>(json, Settings); }
            catch (JsonException) { return null; }
        }

        /// <summary>Why a submitted player look is refused; empty when it is acceptable.</summary>
        public static List<string> Problems(CharacterLook look)
        {
            if (look == null) return new List<string> { "not a look" };
            var bad = CharacterVocabulary.Check(look);
            List<string> acc = look.Accessories ?? new List<string>();
            if (acc.Count > MaxAccessories) bad.Add($"at most {MaxAccessories} accessories");
            if (acc.Distinct().Count() != acc.Count) bad.Add("an accessory is listed twice");
            return bad;
        }

        /// <summary>The stored form: no id (the account is the id), camelCase fields, colours upper-case.</summary>
        public static string Canonical(CharacterLook look)
        {
            var copy = JsonConvert.DeserializeObject<CharacterLook>(JsonConvert.SerializeObject(look, Settings), Settings);
            copy.Id = "";
            copy.Height = (float)Math.Round(copy.Height, 3);
            copy.Limbs = (float)Math.Round(copy.Limbs, 3);
            copy.Skin = copy.Skin?.ToUpperInvariant();
            copy.HairColour = copy.HairColour?.ToUpperInvariant();
            copy.Primary = copy.Primary?.ToUpperInvariant();
            copy.Secondary = copy.Secondary?.ToUpperInvariant();
            copy.Accent = copy.Accent?.ToUpperInvariant();
            copy.LowerColour = copy.LowerColour?.ToUpperInvariant();
            copy.ShoeColour = copy.ShoeColour?.ToUpperInvariant();
            return JsonConvert.SerializeObject(copy, Formatting.None, Settings);
        }

        // ------------------------------------------------------------------ choices offered by the Player Card

        /// <summary>Skin tones, light to deep.</summary>
        public static readonly string[] SkinTones = { "#F6DCC8", "#F1D3BC", "#E3B996", "#D2A07A", "#C68E63", "#A9744E", "#9A6644", "#7C5036", "#6E4630", "#4E3122" };
        /// <summary>Natural and dyed hair colours.</summary>
        public static readonly string[] HairColours = { "#161212", "#231C18", "#3B2A20", "#5A3A24", "#8A5A34", "#B98A56", "#D8C090", "#9A9A9A", "#E8E4DC", "#7A2E2E", "#2E4A7A", "#C2408A" };
        /// <summary>Garment colours (outer, under, trim, trousers, shoes).</summary>
        public static readonly string[] Colours =
        {
            "#23313F", "#3A5A7A", "#2F4A3A", "#2F6B4F", "#5A2A2A", "#D7263D", "#D0602A", "#E0B040", "#6B4E2E", "#3A3A44",
            "#2B2D33", "#6E6E78", "#E6E0D2", "#F2F0EA", "#161616", "#5A3A7A",
        };

        /// <summary>Starter looks: sensible, varied defaults so nobody has to build a person before driving.</summary>
        public static readonly CharacterLook[] Presets =
        {
            Preset(1.74f, "average", "#E3B996", "cropped", "#231C18", "jacket", "#23313F", "#E6E0D2", "#D7263D", "trousers", "#2B2D33", "sneakers", "#F2F0EA", "smile"),
            Preset(1.66f, "slim", "#F1D3BC", "bob", "#161212", "bomber", "#2F4A3A", "#F2F0EA", "#E0B040", "narrow", "#161616", "sneakers", "#F2F0EA", "calm"),
            Preset(1.80f, "athletic", "#9A6644", "high-top", "#161212", "hoodie", "#5A2A2A", "#3A3A44", "#F2F0EA", "cargo", "#3A3A44", "boots", "#161616", "grin"),
            Preset(1.62f, "petite", "#C68E63", "ponytail", "#3B2A20", "windbreaker", "#3A5A7A", "#E6E0D2", "#D0602A", "shorts", "#23313F", "sneakers", "#E6E0D2", "smile"),
            Preset(1.77f, "sturdy", "#6E4630", "locs", "#161212", "coat", "#3A3A44", "#E6E0D2", "#5A3A7A", "trousers", "#2B2D33", "boots", "#6B4E2E", "serious"),
            Preset(1.70f, "average", "#F6DCC8", "swept", "#B98A56", "shirt", "#E6E0D2", "#3A5A7A", "#23313F", "narrow", "#23313F", "loafers", "#6B4E2E", "calm"),
            Preset(1.68f, "average", "#D2A07A", "curly", "#5A3A24", "cardigan", "#6B4E2E", "#F2F0EA", "#2F6B4F", "skirt", "#2B2D33", "boots", "#161616", "smile"),
            Preset(1.84f, "broad", "#A9744E", "buzz", "#161212", "tee", "#2F6B4F", "#F2F0EA", "#E0B040", "cargo", "#6E6E78", "sneakers", "#F2F0EA", "grin"),
        };

        static CharacterLook Preset(float height, string build, string skin, string hair, string hairColour, string outfit, string primary,
            string secondary, string accent, string lower, string lowerColour, string shoes, string shoeColour, string face) =>
            new CharacterLook
            {
                Id = "", Height = height, Build = build, Skin = skin, Hair = hair, HairColour = hairColour, Outfit = outfit,
                Sleeves = outfit == "tee" ? "short" : "long", Primary = primary, Secondary = secondary, Accent = accent, Lower = lower,
                LowerColour = lowerColour, Shoes = shoes, ShoeColour = shoeColour, Face = face,
            };

        /// <summary>A copy that can be edited without touching the preset.</summary>
        public static CharacterLook Copy(CharacterLook look) =>
            JsonConvert.DeserializeObject<CharacterLook>(JsonConvert.SerializeObject(look, Settings), Settings);
    }
}
