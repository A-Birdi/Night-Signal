using System.Collections.Generic;
using NightSignal.Core.Customization;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// A Player Card drawn with its style (spec §11 avatar emblem, background, frame, motif, title, layout; the self-selected
    /// region as a code badge; the preferred car) — the Player Card screen's preview and the public card at the meet. Backgrounds and
    /// motifs are procedural textures (an animated background redraws a small texture a few times a second); frames are UI
    /// strips. Text is literal (never markup).
    /// </summary>
    public sealed class CardView
    {
        const int TexW = 320, TexH = 180, MotifSize = 96;

        public RectTransform Root { get; }
        readonly RawImage background, motif, avatar;
        readonly Texture2D avatarTex;
        readonly Color32[] avatarPixels = new Color32[CardAvatarArt.Size * CardAvatarArt.Size];
        readonly TextMeshProUGUI initial;
        CardAvatarDef shownAvatar;
        readonly Image band, nameplate, header, regionBox;
        readonly RectTransform frameRoot;
        readonly TextMeshProUGUI name, sub, lines, lines2, region, car, headerText;
        readonly Texture2D bgTex, motifTex;
        readonly Color32[] bgPixels = new Color32[TexW * TexH], motifPixels = new Color32[MotifSize * MotifSize];
        readonly List<GameObject> frameParts = new List<GameObject>();
        CardBackgroundDef shownBackground;
        float phase, nextRedraw;

        /// <summary>The style last drawn (tests and evidence read it).</summary>
        public CardStyle Shown { get; private set; }
        /// <summary>The avatar emblem last drawn.</summary>
        public CardAvatarDef ShownAvatar => shownAvatar;
        /// <summary>The avatar texture (evidence reads it).</summary>
        public Texture2D AvatarTexture => avatarTex;

        /// <summary>The public lines last drawn (tests and evidence read it).</summary>
        public IReadOnlyList<string> ShownLines { get; private set; } = new List<string>();

        public CardView(Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            Root = UIFactory.Rect("CardView", parent, anchorMin, anchorMax, Vector2.zero, Vector2.zero);
            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(RawImage));
            bgGo.transform.SetParent(Root, false);
            Stretch((RectTransform)bgGo.transform, Vector2.zero, Vector2.one);
            background = bgGo.GetComponent<RawImage>();
            background.raycastTarget = false;
            bgTex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "CardBackground" };
            background.texture = bgTex;

            band = UIFactory.Panel("Band", Root, new Vector2(0, 0), new Vector2(1, 0.42f), Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.45f));
            band.raycastTarget = false;
            header = UIFactory.Panel("Header", Root, new Vector2(0, 0.86f), new Vector2(1, 1f), Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.55f));
            header.raycastTarget = false;
            headerText = UIFactory.Label("HeaderText", header.transform, "NIGHT SIGNAL · DRIVER PASSPORT", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.MidlineLeft);
            Stretch(headerText.rectTransform, Vector2.zero, Vector2.one, new Vector2(24, 0), new Vector2(-24, 0));
            nameplate = UIFactory.Panel("Nameplate", Root, new Vector2(0.04f, 0.62f), new Vector2(0.72f, 0.84f), Vector2.zero, Vector2.zero, Color.clear);
            nameplate.raycastTarget = false;

            var motifGo = new GameObject("Motif", typeof(RectTransform), typeof(RawImage));
            motifGo.transform.SetParent(Root, false);
            var mrt = (RectTransform)motifGo.transform;
            mrt.anchorMin = mrt.anchorMax = new Vector2(1f, 1f);
            mrt.pivot = new Vector2(1f, 1f);
            mrt.sizeDelta = new Vector2(96, 96);
            mrt.anchoredPosition = new Vector2(-22, -22);
            motif = motifGo.GetComponent<RawImage>();
            motif.raycastTarget = false;
            motifTex = new Texture2D(MotifSize, MotifSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "CardMotif" };
            motif.texture = motifTex;

            // The avatar emblem, top left beside the name.
            var avatarGo = new GameObject("Avatar", typeof(RectTransform), typeof(RawImage));
            avatarGo.transform.SetParent(Root, false);
            var art = (RectTransform)avatarGo.transform;
            art.anchorMin = art.anchorMax = new Vector2(0f, 1f);
            art.pivot = new Vector2(0f, 1f);
            art.sizeDelta = new Vector2(104, 104);
            art.anchoredPosition = new Vector2(20, -20);
            avatar = avatarGo.GetComponent<RawImage>();
            avatar.raycastTarget = false;
            avatarTex = new Texture2D(CardAvatarArt.Size, CardAvatarArt.Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "CardAvatar" };
            avatar.texture = avatarTex;
            initial = UIFactory.Label("Initial", art, "", 52f, SignalTheme.Label, TextAlignmentOptions.Center, true);
            initial.richText = false;
            initial.raycastTarget = false;
            Stretch(initial.rectTransform, Vector2.zero, Vector2.one);

            name = Text("Name", SignalTheme.Heading, SignalTheme.Label, true);
            sub = Text("Sub", SignalTheme.Small, SignalTheme.Caution, false);
            // The region badge: a light box drawn first, the code over it.
            regionBox = UIFactory.Panel("RegionBox", Root, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, new Color(1f, 1f, 1f, 0.14f));
            regionBox.raycastTarget = false;
            region = Text("Region", SignalTheme.Small, SignalTheme.Label, true);
            region.alignment = TextAlignmentOptions.Center;
            lines = Text("Lines", SignalTheme.Small, SignalTheme.Label, false);
            lines.alignment = TextAlignmentOptions.TopLeft;
            lines2 = Text("Lines2", SignalTheme.Small, SignalTheme.Label, false);
            lines2.alignment = TextAlignmentOptions.TopLeft;
            car = Text("Car", SignalTheme.Small, SignalTheme.LabelDim, false);
            frameRoot = UIFactory.Rect("Frame", Root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        }

        TextMeshProUGUI Text(string n, float size, Color c, bool heading)
        {
            TextMeshProUGUI t = UIFactory.Label(n, Root, "", size, c, TextAlignmentOptions.MidlineLeft, heading);
            t.richText = false;
            t.raycastTarget = false;
            t.enableAutoSizing = true;
            t.fontSizeMin = size * 0.5f;
            t.fontSizeMax = size * SignalTheme.TextScale;
            return t;
        }

        static void Stretch(RectTransform rt, Vector2 min, Vector2 max, Vector2? offMin = null, Vector2? offMax = null)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offMin ?? Vector2.zero;
            rt.offsetMax = offMax ?? Vector2.zero;
        }

        /// <summary>Draws a card. <paramref name="stats"/> are the public lines (rank, clears, challenges); text is shown as written.</summary>
        public void Show(CardStyleCatalogue cat, CardStyle style, string displayName, string pronouns, IReadOnlyList<string> stats, string preferredCarName)
        {
            style = style ?? cat.Default;
            Shown = style.Copy();
            ShownLines = new List<string>(stats ?? new List<string>());
            CardBackgroundDef bg = cat.Background(style.Background) ?? cat.Background(cat.Default.Background);
            CardFrameDef frame = cat.Frame(style.Frame) ?? cat.Frame(cat.Default.Frame);
            CardMotifDef mo = cat.Motif(style.Motif) ?? cat.Motif(cat.Default.Motif);
            CardTitleDef title = cat.Title(style.Title) ?? cat.Title(cat.Default.Title);
            CardLayoutDef layout = cat.Layout(style.Layout) ?? cat.Layout(cat.Default.Layout);

            shownBackground = bg;
            phase = 0f;
            DrawBackground(bg, 0f);
            CardAvatarDef av = cat.AvatarOf(style);
            if (av != shownAvatar)
            {
                shownAvatar = av;
                CardAvatarArt.Draw(av, avatarPixels);
                avatarTex.SetPixels32(avatarPixels);
                avatarTex.Apply(false);
            }
            bool letter = av == null || av.Art == "initial";
            initial.gameObject.SetActive(letter);
            string trimmed = (displayName ?? "").Trim();
            initial.text = trimmed.Length > 0 ? char.ToUpperInvariant(trimmed[0]).ToString() : "?";
            initial.color = av != null && av.Colors.Count > 2 ? Hex(av.Colors[2], SignalTheme.Label) : SignalTheme.Label;
            DrawMotif(mo);
            Color frameColour = Hex(frame?.Color, SignalTheme.Timing);
            BuildFrame(frame?.Style ?? "thin", frameColour);

            name.text = displayName ?? "";
            string titleText = title?.Text ?? "";
            sub.text = string.IsNullOrEmpty(pronouns) ? titleText : string.IsNullOrEmpty(titleText) ? $"({pronouns})" : $"{titleText}  ·  ({pronouns})";
            region.text = style.Region ?? "";
            region.gameObject.SetActive(!string.IsNullOrEmpty(style.Region));
            regionBox.gameObject.SetActive(!string.IsNullOrEmpty(style.Region));
            car.text = string.IsNullOrEmpty(preferredCarName) ? "" : "Preferred car · " + preferredCarName;
            nameplate.color = frame?.Style == "nameplate" ? new Color(frameColour.r, frameColour.g, frameColour.b, 0.22f) : Color.clear;

            // Layouts arrange the same parts.
            string kind = layout?.Layout ?? "standard";
            header.gameObject.SetActive(kind == "passport");
            band.gameObject.SetActive(kind == "two-state" || kind == "sectors");
            band.rectTransform.anchorMax = new Vector2(1, kind == "sectors" ? 0.36f : 0.46f);
            float top = kind == "passport" ? 0.84f : 0.96f;
            // The avatar sits under the passport header when there is one; the name and title start right of it.
            ((RectTransform)avatar.transform).anchoredPosition = new Vector2(20, kind == "passport" ? -(Root.rect.height * 0.14f + 8f) : -20f);
            const float textLeft = 0.26f;
            Place(name.rectTransform, textLeft, top - 0.2f, 0.7f, top);
            Place(sub.rectTransform, textLeft, top - 0.29f, 0.8f, top - 0.2f);
            Place(region.rectTransform, 0.72f, top - 0.17f, 0.8f, top - 0.04f);
            Place(regionBox.rectTransform, 0.72f, top - 0.17f, 0.8f, top - 0.04f);
            Place(nameplate.rectTransform, textLeft - 0.02f, top - 0.21f, 0.71f, top + 0.005f);
            var list = stats ?? new List<string>();
            if (kind == "twin")
            {
                int half = (list.Count + 1) / 2;
                lines.text = string.Join("\n", Take(list, 0, half));
                lines2.text = string.Join("\n", Take(list, half, list.Count - half));
                Place(lines.rectTransform, 0.05f, 0.16f, 0.5f, top - 0.33f);
                Place(lines2.rectTransform, 0.52f, 0.16f, 0.97f, top - 0.33f);
            }
            else
            {
                lines.text = string.Join("\n", list);
                lines2.text = "";
                Place(lines.rectTransform, 0.05f, 0.16f, 0.95f, kind == "two-state" || kind == "sectors" ? 0.44f : top - 0.33f);
            }
            Place(car.rectTransform, 0.05f, 0.03f, 0.95f, 0.14f);
        }

        static IEnumerable<string> Take(IReadOnlyList<string> list, int from, int count)
        {
            for (int i = from; i < from + count && i < list.Count; i++) yield return list[i];
        }

        static void Place(RectTransform rt, float x0, float y0, float x1, float y1)
        {
            rt.anchorMin = new Vector2(x0, y0);
            rt.anchorMax = new Vector2(x1, y1);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        /// <summary>Advances an animated background (call each frame while shown).</summary>
        public void Tick(float dt)
        {
            if (shownBackground == null || !shownBackground.Animated || SignalTheme.ReducedMotion) return;
            phase += dt;
            if (Time.unscaledTime < nextRedraw) return;
            nextRedraw = Time.unscaledTime + 1f / 12f;
            DrawBackground(shownBackground, phase);
        }

        public void Dispose()
        {
            Object.Destroy(bgTex);
            Object.Destroy(motifTex);
            Object.Destroy(avatarTex);
        }

        // ------------------------------------------------------------------ procedural art

        static Color Hex(string hex, Color fallback) => hex != null && ColorUtility.TryParseHtmlString(hex, out Color c) ? c : fallback;

        void DrawBackground(CardBackgroundDef def, float t)
        {
            Color ground = Hex(def?.Colors != null && def.Colors.Count > 0 ? def.Colors[0] : null, SignalTheme.Graphite);
            Color ink = Hex(def?.Colors != null && def.Colors.Count > 1 ? def.Colors[1] : null, SignalTheme.Rule);
            string pattern = def?.Pattern ?? "plain";
            for (int y = 0; y < TexH; y++)
            {
                float v = y / (float)(TexH - 1);
                for (int x = 0; x < TexW; x++)
                {
                    float u = x / (float)(TexW - 1);
                    float k;
                    switch (pattern)
                    {
                        case "gradient": k = v; break;
                        case "stripes": k = ((x + y) / 14) % 2 == 0 ? 0.55f : 0f; break;
                        case "grid": k = x % 20 == 0 || y % 20 == 0 ? 0.8f : (x % 5 == 0 && y % 5 == 0 ? 0.35f : 0f); break;
                        case "rain-chart":
                        {
                            float streak = Mathf.Repeat(u * 37f + v * 3f, 1f) < 0.05f ? 0.35f : 0f;
                            float chart = 0.5f + 0.28f * Mathf.Sin(u * 9f) * Mathf.Cos(u * 3.1f);
                            k = Mathf.Abs(v - chart) < 0.012f ? 1f : streak;
                            break;
                        }
                        case "dyno-trace":
                        {
                            float trace = 0.25f + 0.5f * (1f - Mathf.Exp(-u * 3f)) + 0.05f * Mathf.Sin(u * 30f - t * 4f);
                            float glow = Mathf.Clamp01(1f - Mathf.Abs(v - trace) * 60f);
                            k = Mathf.Max(glow, x % 32 == 0 ? 0.25f : 0f);
                            break;
                        }
                        default: k = 0.08f * v; break; // plain: a faint lift toward the top
                    }
                    bgPixels[y * TexW + x] = Color.Lerp(ground, ink, k);
                }
            }
            bgTex.SetPixels32(bgPixels);
            bgTex.Apply(false);
        }

        void DrawMotif(CardMotifDef def)
        {
            for (int i = 0; i < motifPixels.Length; i++) motifPixels[i] = new Color32(0, 0, 0, 0);
            string glyph = def?.Glyph ?? "none";
            motif.gameObject.SetActive(glyph != "none");
            Color c = Hex(def?.Color, SignalTheme.Label);
            void Box(int x0, int y0, int x1, int y1)
            {
                for (int y = Mathf.Max(0, y0); y < Mathf.Min(MotifSize, y1); y++)
                    for (int x = Mathf.Max(0, x0); x < Mathf.Min(MotifSize, x1); x++) motifPixels[y * MotifSize + x] = c;
            }
            void Ring(float cx, float cy, float r, float width)
            {
                for (int y = 0; y < MotifSize; y++)
                    for (int x = 0; x < MotifSize; x++)
                        if (Mathf.Abs(Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r) < width) motifPixels[y * MotifSize + x] = c;
            }
            void Line(float x0, float y0, float x1, float y1, float width)
            {
                for (int y = 0; y < MotifSize; y++)
                    for (int x = 0; x < MotifSize; x++)
                    {
                        Vector2 p = new Vector2(x, y), a = new Vector2(x0, y0), b = new Vector2(x1, y1);
                        float h = Mathf.Clamp01(Vector2.Dot(p - a, b - a) / Mathf.Max(1e-3f, (b - a).sqrMagnitude));
                        if (Vector2.Distance(p, a + (b - a) * h) < width) motifPixels[y * MotifSize + x] = c;
                    }
            }
            switch (glyph)
            {
                case "lantern":
                    Box(36, 22, 60, 70);
                    Box(30, 70, 66, 76);
                    Box(30, 16, 66, 22);
                    Box(46, 76, 50, 88);
                    break;
                case "bars":
                    Box(18, 14, 32, 40);
                    Box(41, 14, 55, 60);
                    Box(64, 14, 78, 82);
                    break;
                case "ladder":
                    Box(26, 10, 32, 86);
                    Box(64, 10, 70, 86);
                    for (int i = 0; i < 5; i++) Box(32, 16 + i * 15, 64, 20 + i * 15);
                    break;
                case "dial":
                    Ring(48, 48, 34, 3f);
                    Line(48, 48, 70, 66, 2.5f);
                    for (int i = 0; i < 7; i++)
                    {
                        float a = Mathf.Deg2Rad * (210f - i * 40f);
                        Box(48 + (int)(Mathf.Cos(a) * 28) - 2, 48 + (int)(Mathf.Sin(a) * 28) - 2, 48 + (int)(Mathf.Cos(a) * 28) + 2, 48 + (int)(Mathf.Sin(a) * 28) + 2);
                    }
                    break;
                case "three-drive":
                    Ring(26, 30, 14, 3.5f);
                    Ring(70, 30, 14, 3.5f);
                    Ring(48, 68, 14, 3.5f);
                    Line(26, 30, 70, 30, 1.5f);
                    Line(26, 30, 48, 68, 1.5f);
                    Line(70, 30, 48, 68, 1.5f);
                    break;
            }
            motifTex.SetPixels32(motifPixels);
            motifTex.Apply(false);
        }

        void BuildFrame(string style, Color c)
        {
            foreach (GameObject g in frameParts) Object.Destroy(g);
            frameParts.Clear();
            void Strip(Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax, Color colour)
            {
                Image i = UIFactory.Panel("Edge", frameRoot, min, max, offMin, offMax, colour);
                i.raycastTarget = false;
                frameParts.Add(i.gameObject);
            }
            void Border(float inset, float width, Color colour)
            {
                Strip(new Vector2(0, 1), new Vector2(1, 1), new Vector2(inset, -inset - width), new Vector2(-inset, -inset), colour);
                Strip(new Vector2(0, 0), new Vector2(1, 0), new Vector2(inset, inset), new Vector2(-inset, inset + width), colour);
                Strip(new Vector2(0, 0), new Vector2(0, 1), new Vector2(inset, inset), new Vector2(inset + width, -inset), colour);
                Strip(new Vector2(1, 0), new Vector2(1, 1), new Vector2(-inset - width, inset), new Vector2(-inset, -inset), colour);
            }
            switch (style)
            {
                case "double":
                    Border(0, 3, c);
                    Border(9, 1.5f, c);
                    break;
                case "columns":
                    Border(0, 2, c);
                    Strip(new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0), new Vector2(12, 0), c);
                    Strip(new Vector2(1, 0), new Vector2(1, 1), new Vector2(-12, 0), new Vector2(0, 0), c);
                    Strip(new Vector2(0, 0), new Vector2(0, 1), new Vector2(18, 0), new Vector2(21, 0), new Color(c.r, c.g, c.b, 0.6f));
                    Strip(new Vector2(1, 0), new Vector2(1, 1), new Vector2(-21, 0), new Vector2(-18, 0), new Color(c.r, c.g, c.b, 0.6f));
                    break;
                case "balance-ticks":
                    Border(0, 2, c);
                    for (int i = 1; i < 20; i++)
                    {
                        float x = i / 20f;
                        float h = i % 5 == 0 ? 16 : 8;
                        Strip(new Vector2(x, 1), new Vector2(x, 1), new Vector2(-1, -h), new Vector2(1, 0), c);
                        Strip(new Vector2(x, 0), new Vector2(x, 0), new Vector2(-1, 0), new Vector2(1, h), c);
                    }
                    break;
                case "balance-point":
                    Border(0, 2, c);
                    Image point = UIFactory.Panel("Point", frameRoot, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-12, -12), new Vector2(12, 12), c);
                    point.raycastTarget = false;
                    point.rectTransform.localRotation = Quaternion.Euler(0, 0, 45f);
                    frameParts.Add(point.gameObject);
                    Strip(new Vector2(0.1f, 0), new Vector2(0.9f, 0), new Vector2(0, 16), new Vector2(0, 18), new Color(c.r, c.g, c.b, 0.6f));
                    break;
                case "nameplate":
                    Border(0, 2, c);
                    break;
                default:
                    Border(0, 2, c);
                    break;
            }
        }
    }
}
