using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.UI
{
    /// <summary>
    /// The driving instrument cluster (Addendum 03 §1): an Instrument Dial or a Digital Strip for ROAD SPEED, and — clearly
    /// separate in both — a labelled RPM bar with shift lights and the gear. Both styles occupy the same bottom-right area
    /// so switching never moves other HUD elements. The face (ticks, numerals) is built once per style/unit/scale and only
    /// the needle, fill and numbers change per frame; nothing is allocated in the driving loop. The needle uses a small
    /// frame-rate-independent filter (~80 ms); the number is the live value. Over range, the needle rests at the end stop
    /// and the number keeps reading the true speed.
    /// </summary>
    public sealed class SpeedCluster
    {
        public const float Width = 360f, Height = 300f;
        const float SweepDeg = 250f, StartDeg = 125f, NeedleTau = 0.08f;

        readonly RectTransform root;
        RectTransform face;
        RectTransform needle;
        Image stripFill, revFill, overRange;
        TextMeshProUGUI number, unitLabel, gearLabel, rpmLabel;
        readonly List<GameObject> built = new List<GameObject>();
        bool dial;
        SpeedDisplay.Scale scale;
        float needleFraction;
        bool hasNeedle;
        int lastShown = int.MinValue, lastGear = int.MinValue;
        static readonly string[] Digits = BuildDigits(999, false), Tabular = BuildDigits(999, true);

        public RectTransform Root => root;
        /// <summary>Tests: how many UI objects the current face uses (must not grow on repeated changes).</summary>
        public int ObjectCount => root.GetComponentsInChildren<RectTransform>(true).Length;
        public bool IsDial => dial;
        public SpeedDisplay.Scale CurrentScale => scale;
        /// <summary>Tests: the needle's current angle (degrees, z rotation; 0 km/h at +125°, full scale at −125°).</summary>
        public float NeedleAngle => needle != null ? needle.localEulerAngles.z : 0f;
        public string ShownNumber => number != null ? number.text.Replace("<mspace=0.58em>", "").Replace("</mspace>", "") : "";

        public SpeedCluster(Transform parent)
        {
            root = UIFactory.Rect("SpeedCluster", parent, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-Width - 32, 32), new Vector2(-32, Height + 32));
        }

        /// <summary>Builds the face when the style, unit or scale changed (a no-op otherwise).</summary>
        public void Configure(bool useDial, SpeedDisplay.Scale newScale)
        {
            if (face != null && useDial == dial && newScale.Equals(scale)) return;
            foreach (GameObject g in built)
            {
                if (Application.isPlaying) Object.Destroy(g);
                else Object.DestroyImmediate(g);
            }
            built.Clear();
            dial = useDial;
            scale = newScale;
            lastShown = int.MinValue;
            lastGear = int.MinValue;
            hasNeedle = false;
            if (dial) BuildDial(); else BuildStrip();
            BuildRevAndGear();
        }

        /// <summary>Forget the needle history (teleport, car switch, target lost): the next frame jumps to the true value.</summary>
        public void ResetFilter() => hasNeedle = false;

        /// <summary>One frame: road speed (m/s, canonical), engine revs and gear. Non-finite or unavailable → a neutral face.</summary>
        public void Render(float roadSpeedMps, float rpm, float redline, int gear, bool available, float dt)
        {
            if (face == null) return;
            if (!available || float.IsNaN(roadSpeedMps) || float.IsInfinity(roadSpeedMps))
            {
                SetNumber(int.MinValue + 1, "—");
                SetFraction(0f, 1f);
                revFill.fillAmount = 0f;
                SetGear(int.MinValue + 1, "–");
                return;
            }
            float target = SpeedDisplay.Fraction(roadSpeedMps, scale);
            needleFraction = hasNeedle ? Mathf.Lerp(needleFraction, target, 1f - Mathf.Exp(-Mathf.Max(0f, dt) / NeedleTau)) : target;
            hasNeedle = true;
            SetFraction(needleFraction, target);
            int shown = Mathf.RoundToInt(SpeedDisplay.Convert(roadSpeedMps, scale.Unit));
            SetNumber(shown, null);
            float rev = redline > 0f ? Mathf.Clamp01(rpm / redline) : 0f;
            revFill.fillAmount = rev;
            revFill.color = rev > 0.93f ? SignalTheme.Signal : rev > 0.8f ? SignalTheme.Caution : SignalTheme.Label;
            SetGear(gear, null);
        }

        void SetFraction(float shownFraction, float trueFraction)
        {
            float f = Mathf.Clamp01(shownFraction);
            if (dial) needle.localRotation = Quaternion.Euler(0f, 0f, StartDeg - SweepDeg * f);
            else stripFill.fillAmount = f;
            overRange.enabled = trueFraction > 1f;
        }

        void SetNumber(int shown, string text)
        {
            if (shown == lastShown) return;
            lastShown = shown;
            number.text = text ?? (shown >= 0 && shown < Tabular.Length ? Tabular[shown] : shown.ToString(CultureInfo.InvariantCulture));
            number.color = overRange.enabled ? SignalTheme.Caution : SignalTheme.Text;
        }

        void SetGear(int gear, string text)
        {
            if (gear == lastGear) return;
            lastGear = gear;
            gearLabel.text = text ?? (gear < 0 ? "R" : gear == 0 ? "N" : Digits[Mathf.Min(gear, Digits.Length - 1)]);
        }

        // ------------------------------------------------------------------ faces

        void BuildDial()
        {
            const float size = 236f, radius = size * 0.5f;
            face = UIFactory.Rect("Dial", root, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-radius, -size), new Vector2(radius, 0f));
            built.Add(face.gameObject);
            Image disc = Add<Image>("Face", face);
            Stretch(disc.rectTransform);
            disc.sprite = Disc;
            disc.color = new Color(0.03f, 0.035f, 0.045f, 0.82f);
            disc.raycastTarget = false;

            // Ticks and numerals: measured major/minor intervals of the chosen unit.
            int minors = Mathf.RoundToInt(scale.Max / scale.Minor);
            for (int i = 0; i <= minors; i++)
            {
                float value = i * scale.Minor;
                bool major = Mathf.Abs(value / scale.Major - Mathf.Round(value / scale.Major)) < 1e-3f;
                float f = value / scale.Max;
                float angle = StartDeg - SweepDeg * f;
                RectTransform pivot = UIFactory.Rect("Tick", face, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
                pivot.localRotation = Quaternion.Euler(0f, 0f, angle);
                Image tick = UIFactory.Panel("Mark", pivot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero,
                    major ? SignalTheme.Label : SignalTheme.LabelDim);
                float len = major ? 16f : 8f;
                tick.rectTransform.sizeDelta = new Vector2(major ? 3f : 2f, len);
                tick.rectTransform.anchoredPosition = new Vector2(0f, radius - 10f - len * 0.5f);
                if (!major) continue;
                float rad = angle * Mathf.Deg2Rad;
                TextMeshProUGUI n = UIFactory.Label("Numeral", face, ((int)value).ToString(CultureInfo.InvariantCulture), SignalTheme.Small * 0.85f, SignalTheme.Label, TextAlignmentOptions.Center);
                n.rectTransform.anchorMin = n.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                n.rectTransform.sizeDelta = new Vector2(44, 20);
                n.rectTransform.anchoredPosition = new Vector2(-Mathf.Sin(rad), Mathf.Cos(rad)) * (radius - 42f);
            }

            // Needle: speed only (the RPM has its own bar), with a controlled accent tip.
            RectTransform hub = UIFactory.Rect("NeedlePivot", face, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            needle = hub;
            Image shaft = UIFactory.Panel("Needle", hub, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, SignalTheme.Label);
            shaft.rectTransform.sizeDelta = new Vector2(3f, radius - 22f);
            shaft.rectTransform.anchoredPosition = new Vector2(0f, (radius - 22f) * 0.5f - 6f);
            Image tip = UIFactory.Panel("NeedleTip", hub, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, SignalTheme.Timing);
            tip.rectTransform.sizeDelta = new Vector2(4f, 22f);
            tip.rectTransform.anchoredPosition = new Vector2(0f, radius - 22f - 11f - 6f);
            Image cap = Add<Image>("Hub", face);
            cap.sprite = Disc;
            cap.color = SignalTheme.GraphiteRaised;
            cap.rectTransform.sizeDelta = new Vector2(14, 14);
            cap.raycastTarget = false;

            // Over-range end stop marker (the number keeps reading true speed).
            float endRad = (StartDeg - SweepDeg) * Mathf.Deg2Rad;
            overRange = UIFactory.Panel("OverRange", face, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, SignalTheme.Caution);
            overRange.rectTransform.sizeDelta = new Vector2(10, 10);
            overRange.rectTransform.anchoredPosition = new Vector2(-Mathf.Sin(endRad), Mathf.Cos(endRad)) * (radius - 4f);
            overRange.enabled = false;

            number = UIFactory.Numeral("Speed", face, SignalTheme.HudNumeral * 0.72f, SignalTheme.Text, TextAlignmentOptions.Center);
            number.overflowMode = TextOverflowModes.Overflow; // a clipped line would vanish entirely
            number.rectTransform.anchorMin = number.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            number.rectTransform.sizeDelta = new Vector2(160, 64);
            number.rectTransform.anchoredPosition = new Vector2(0, -36);
            unitLabel = UIFactory.Label("Unit", face, SpeedDisplay.Label(scale.Unit), SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.Center);
            unitLabel.rectTransform.anchorMin = unitLabel.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            unitLabel.rectTransform.sizeDelta = new Vector2(120, 22);
            unitLabel.rectTransform.anchoredPosition = new Vector2(0, -70);
        }

        void BuildStrip()
        {
            face = UIFactory.Rect("Strip", root, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 70), new Vector2(0, 186));
            built.Add(face.gameObject);
            Image bg = Add<Image>("Panel", face);
            Stretch(bg.rectTransform);
            bg.sprite = UIFactory.White;
            bg.color = new Color(0, 0, 0, 0.55f);
            bg.raycastTarget = false;
            number = UIFactory.Numeral("Speed", face, SignalTheme.HudNumeral * 1.1f, SignalTheme.Text);
            number.overflowMode = TextOverflowModes.Overflow; // a clipped line would vanish entirely
            number.rectTransform.anchorMin = new Vector2(0, 0.3f);
            number.rectTransform.anchorMax = new Vector2(0.72f, 1f);
            number.rectTransform.offsetMin = number.rectTransform.offsetMax = Vector2.zero;
            unitLabel = UIFactory.Label("Unit", face, SpeedDisplay.Label(scale.Unit), SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft, true);
            unitLabel.rectTransform.anchorMin = new Vector2(0.74f, 0.45f);
            unitLabel.rectTransform.anchorMax = new Vector2(1f, 0.8f);
            unitLabel.rectTransform.offsetMin = unitLabel.rectTransform.offsetMax = Vector2.zero;
            // Speed fill on the same measured scale (major marks), separate from the RPM bar below.
            Image track = UIFactory.Panel("SpeedTrack", face, new Vector2(0, 0), new Vector2(1, 0), new Vector2(14, 12), new Vector2(-14, 22), SignalTheme.Rule);
            stripFill = UIFactory.Panel("SpeedFill", track.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, SignalTheme.Timing);
            stripFill.type = Image.Type.Filled;
            stripFill.fillMethod = Image.FillMethod.Horizontal;
            int majors = Mathf.RoundToInt(scale.Max / scale.Major);
            for (int i = 1; i < majors; i++)
                UIFactory.Panel("Mark", track.transform, new Vector2((float)i / majors, 0), new Vector2((float)i / majors, 1), new Vector2(-1, 0), new Vector2(1, 0), SignalTheme.Ink);
            overRange = UIFactory.Panel("OverRange", track.transform, new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, -3), new Vector2(6, 3), SignalTheme.Caution);
            overRange.enabled = false;
        }

        /// <summary>Shared by both styles: an RPM bar labelled RPM with shift colours and ticks, and the gear.</summary>
        void BuildRevAndGear()
        {
            RectTransform rev = UIFactory.Rect("Rev", root, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 62));
            built.Add(rev.gameObject);
            Image bg = UIFactory.Panel("RevPanel", rev, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0, 0, 0, 0.55f));
            gearLabel = UIFactory.Numeral("Gear", rev, SignalTheme.Numeral * 1.2f, SignalTheme.Caution, TextAlignmentOptions.Center);
            gearLabel.rectTransform.anchorMin = new Vector2(0, 0);
            gearLabel.rectTransform.anchorMax = new Vector2(0.18f, 1);
            gearLabel.rectTransform.offsetMin = gearLabel.rectTransform.offsetMax = Vector2.zero;
            rpmLabel = UIFactory.Label("RpmLabel", rev, "RPM", SignalTheme.Small * 0.8f, SignalTheme.LabelDim, TextAlignmentOptions.TopLeft, true);
            rpmLabel.rectTransform.anchorMin = new Vector2(0.2f, 0.55f);
            rpmLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
            rpmLabel.rectTransform.offsetMin = new Vector2(0, 0);
            rpmLabel.rectTransform.offsetMax = new Vector2(-10, -4);
            Image track = UIFactory.Panel("RevBg", rev, new Vector2(0.2f, 0.15f), new Vector2(1f, 0.5f), new Vector2(0, 0), new Vector2(-12, 0), SignalTheme.Rule);
            revFill = UIFactory.Panel("RevFill", track.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, SignalTheme.Label);
            revFill.type = Image.Type.Filled;
            revFill.fillMethod = Image.FillMethod.Horizontal;
            for (int i = 1; i < 10; i++)
                UIFactory.Panel("Tick" + i, track.transform, new Vector2(i / 10f, 0), new Vector2(i / 10f, 1), new Vector2(-1, 0), new Vector2(1, 0), SignalTheme.Ink);
        }

        // ------------------------------------------------------------------ helpers

        static T Add<T>(string name, Transform parent) where T : Component
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.AddComponent<T>();
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        static string[] BuildDigits(int max, bool tabular)
        {
            var d = new string[max + 1];
            for (int i = 0; i <= max; i++) d[i] = tabular ? RaceHud.Tabular(i.ToString(CultureInfo.InvariantCulture)) : i.ToString(CultureInfo.InvariantCulture);
            return d;
        }

        static Sprite disc;

        /// <summary>An anti-aliased white disc sprite (generated once, shared).</summary>
        public static Sprite Disc
        {
            get
            {
                if (disc != null) return disc;
                const int n = 128;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "Disc", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[n * n];
                float c = (n - 1) * 0.5f;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                        byte a = (byte)(Mathf.Clamp01(c - d + 0.5f) * 255f);
                        px[y * n + x] = new Color32(255, 255, 255, a);
                    }
                tex.SetPixels32(px);
                tex.Apply(false, true);
                disc = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
                return disc;
            }
        }
    }
}
