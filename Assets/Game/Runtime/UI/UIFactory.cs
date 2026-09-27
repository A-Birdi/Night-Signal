using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace NightSignal.UI
{
    /// <summary>Code-built uGUI + TextMeshPro elements in the Signal/Sector style (one UI stack, spec §3.1).</summary>
    public static class UIFactory
    {
        static Sprite white;

        public static Sprite White
        {
            get
            {
                if (white == null) white = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
                return white;
            }
        }

        /// <summary>Root overlay canvas (1920×1080 reference, safe-area aware) and an Input System event system.</summary>
        public static Canvas Root(string name = "UIRoot", int sortOrder = 0)
        {
            var go = new GameObject(name, typeof(RectTransform));
            Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            if (Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                Object.DontDestroyOnLoad(es);
            }
            return canvas;
        }

        public static RectTransform Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            return rt;
        }

        public static Image Panel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            RectTransform rt = Rect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = White;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public static TextMeshProUGUI Label(string name, Transform parent, string text, float size, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, bool heading = false)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size * SignalTheme.TextScale;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Truncate; // the bundled font has no ellipsis glyph
            if (heading)
            {
                t.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
                t.characterSpacing = 2f;
            }
            return t;
        }

        /// <summary>Tabular numerals for timing (monospaced digits so values don't jitter).</summary>
        public static TextMeshProUGUI Numeral(string name, Transform parent, float size, Color color, TextAlignmentOptions align = TextAlignmentOptions.MidlineRight)
        {
            TextMeshProUGUI t = Label(name, parent, "", size, color, align);
            t.fontStyle = FontStyles.Bold;
            t.text = "";
            t.characterSpacing = 0f;
            return t;
        }

        public static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        /// <summary>A focusable Signal/Sector button: graphite body, red selection bar, off-white label.</summary>
        public static Button Button(string name, Transform parent, string label, System.Action onClick, float width = 320, float height = 56)
        {
            RectTransform rt = Rect(name, parent, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            rt.sizeDelta = new Vector2(width, height);
            var bg = rt.gameObject.AddComponent<Image>();
            bg.sprite = White;
            bg.color = SignalTheme.GraphiteRaised;
            var btn = rt.gameObject.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.3f);
            cb.selectedColor = new Color(1.25f, 1.25f, 1.3f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            btn.colors = cb;
            Image bar = Panel("SelectionBar", rt, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(6, 0), SignalTheme.Signal);
            bar.gameObject.AddComponent<SelectionIndicator>().Target = btn;
            TextMeshProUGUI t = Label("Label", rt, label, SignalTheme.Body, SignalTheme.Label, TextAlignmentOptions.MidlineLeft, true);
            Stretch(t.rectTransform);
            t.rectTransform.offsetMin = new Vector2(24, 0);
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return btn;
        }

        /// <summary>
        /// Signal-styled TMP input field. Password fields mask input and are never logged; the caller passes the text
        /// straight to the identity provider and clears the field afterwards.
        /// </summary>
        public static TMP_InputField InputField(string name, Transform parent, string placeholder, bool password = false, int characterLimit = 64,
            float width = 520, float height = 56)
        {
            RectTransform rt = Rect(name, parent, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, Vector2.zero);
            rt.sizeDelta = new Vector2(width, height);
            var bg = rt.gameObject.AddComponent<Image>();
            bg.sprite = White;
            bg.color = SignalTheme.GraphiteRaised;
            RectTransform area = Rect("TextArea", rt, Vector2.zero, Vector2.one, new Vector2(18, 6), new Vector2(-18, -6));
            area.gameObject.AddComponent<RectMask2D>();
            TextMeshProUGUI ph = Label("Placeholder", area, placeholder, SignalTheme.Body, SignalTheme.LabelDim);
            Stretch(ph.rectTransform);
            ph.fontStyle = FontStyles.Italic;
            TextMeshProUGUI text = Label("Text", area, "", SignalTheme.Body, SignalTheme.Label);
            Stretch(text.rectTransform);
            text.richText = false; // user text renders literally
            var field = rt.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = ph;
            field.characterLimit = characterLimit;
            field.richText = false;
            field.contentType = password ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
            field.lineType = TMP_InputField.LineType.SingleLine;
            Image bar = Panel("SelectionBar", rt, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(6, 0), SignalTheme.Signal);
            bar.gameObject.AddComponent<SelectionIndicator>().Target = field;
            return field;
        }

        /// <summary>Vertical stack that lays children top-down with fixed spacing (keeps order stable: no reordering on hover).</summary>
        public static RectTransform Column(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, float spacing = 14f)
        {
            RectTransform rt = Rect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var layout = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            return rt;
        }

        /// <summary>A text row inside a column (fixed height so layout never jumps).</summary>
        public static TextMeshProUGUI Row(string name, Transform column, string text, float size, Color color, float width = 720f, float height = 0f, bool heading = false)
        {
            TextMeshProUGUI t = Label(name, column, text, size * 1f, color, TextAlignmentOptions.TopLeft, heading);
            t.rectTransform.sizeDelta = new Vector2(width, height > 0 ? height : size * SignalTheme.TextScale * 1.5f);
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            return t;
        }

        /// <summary>Draws a course plan polyline into a texture for the HUD minimap (once per course).</summary>
        public static Texture2D MinimapTexture(Vector2[] plan, int size, Color road, out Vector2 min, out float scale)
        {
            min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            foreach (Vector2 p in plan) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            float span = Mathf.Max(max.x - min.x, max.y - min.y);
            scale = (size - 16) / Mathf.Max(1f, span);
            Vector2 centreOffset = new Vector2(size - (max.x - min.x) * scale, size - (max.y - min.y) * scale) * 0.5f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            tex.SetPixels32(px);
            for (int i = 1; i < plan.Length; i++)
            {
                Vector2 a = (plan[i - 1] - min) * scale + centreOffset, b = (plan[i] - min) * scale + centreOffset;
                int steps = Mathf.CeilToInt(Vector2.Distance(a, b) * 2f) + 1;
                for (int s = 0; s <= steps; s++)
                {
                    Vector2 q = Vector2.Lerp(a, b, s / (float)steps);
                    for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int x = Mathf.RoundToInt(q.x) + dx, y = Mathf.RoundToInt(q.y) + dy;
                        if (x < 0 || y < 0 || x >= size || y >= size || dx * dx + dy * dy > 5) continue;
                        px[y * size + x] = road;
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            min -= centreOffset / scale;
            return tex;
        }
    }

    /// <summary>Shows the red selection bar only while the owning selectable is selected (controller focus).</summary>
    public sealed class SelectionIndicator : MonoBehaviour
    {
        public Selectable Target;
        Image image;

        void Awake() => image = GetComponent<Image>();

        void Update()
        {
            if (image == null || Target == null) return;
            image.enabled = EventSystem.current != null && EventSystem.current.currentSelectedGameObject == Target.gameObject;
        }
    }
}
