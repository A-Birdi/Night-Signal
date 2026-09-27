using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.UI
{
    /// <summary>
    /// Perimeter speed lines (Addendum 03 §4): narrow streaks confined to a feathered band at the viewport edge — the central
    /// 70 % of width AND height stays clear — on their own canvas beneath the HUD, so they never cover time, speed,
    /// warnings or subtitles. Strength comes only from real road speed and a qualified drift (never wheelspin at a stop, a
    /// fall, a reset tow or a score multiplier), scaled by the Off / Subtle / Strong setting; no strobing, no full-screen
    /// colour, nothing at low speed. A fixed pool of lines, no allocation while driving.
    /// </summary>
    public sealed class SpeedLines : MonoBehaviour
    {
        const int Count = 36;
        /// <summary>Clear centre: lines live only beyond this half-extent (0.35 of the screen = the central 70 %).</summary>
        public const float ClearHalfExtent = 0.35f;

        readonly List<(RectTransform Rt, Image Img, float Angle, float Phase, float Speed)> lines = new List<(RectTransform, Image, float, float, float)>();
        float strength;

        /// <summary>Tests: the current overall strength (0 = invisible).</summary>
        public float Strength => strength;

        /// <summary>Canvas order: under the race HUD (10), so no streak can cover its time, speed, warnings or subtitles.</summary>
        public const int SortOrder = 5;

        public static SpeedLines Create() => CreateOn(UIFactory.Root("SpeedLines", SortOrder));

        /// <summary>On an existing canvas (tests build one without the persistent root).</summary>
        public static SpeedLines CreateOn(Canvas canvas)
        {
            var lines = canvas.gameObject.AddComponent<SpeedLines>();
            lines.Build((RectTransform)canvas.transform);
            return lines;
        }

        void Build(RectTransform root)
        {
            var rng = new System.Random(24601);
            for (int i = 0; i < Count; i++)
            {
                Image img = UIFactory.Panel("Streak", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, new Color(0.9f, 0.95f, 1f, 0f));
                img.raycastTarget = false;
                float angle = (i + (float)rng.NextDouble() * 0.6f) / Count * 360f;
                lines.Add((img.rectTransform, img, angle, (float)rng.NextDouble(), 0.8f + (float)rng.NextDouble() * 0.6f));
            }
        }

        /// <summary>
        /// One frame: road speed (m/s), the drift framing amount (0..1, from the camera's qualified drift state) and the
        /// setting (0 off, 1 subtle, 2 strong). Paused/menus pass 0 speed.
        /// </summary>
        public void Render(float roadSpeedMps, float driftAmount, int setting, float dt)
        {
            float target = setting <= 0 ? 0f
                : Mathf.InverseLerp(28f, 70f, roadSpeedMps) * (setting == 1 ? 0.35f : 0.7f) * (1f + 0.5f * Mathf.Clamp01(Mathf.Abs(driftAmount)));
            strength = Mathf.Lerp(strength, target, 1f - Mathf.Exp(-3f * Mathf.Max(0f, dt)));
            Rect screen = ((RectTransform)transform).rect;
            float hw = screen.width * 0.5f, hh = screen.height * 0.5f;
            for (int i = 0; i < lines.Count; i++)
            {
                var (rt, img, angle, phase, speed) = lines[i];
                if (strength < 0.01f) { img.enabled = false; continue; }
                img.enabled = true;
                float t = Mathf.Repeat(phase + Time.unscaledTime * speed * (0.6f + strength), 1f);
                // A point on the edge band along this line's direction: start just outside the clear centre, run outward.
                Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
                float scaleToEdge = 1f / Mathf.Max(Mathf.Abs(dir.x) / hw, Mathf.Abs(dir.y) / hh); // distance to the screen edge
                float inner = scaleToEdge * (ClearHalfExtent / 0.5f) * 1.02f;
                float r = Mathf.Lerp(inner, scaleToEdge * 1.05f, t);
                float len = Mathf.Lerp(40f, 140f, strength) * (0.6f + t);
                rt.anchoredPosition = dir * (r + len * 0.5f);
                rt.sizeDelta = new Vector2(len, 2f);
                rt.localRotation = Quaternion.Euler(0f, 0f, angle);
                // Feathered: fade in from the inner edge, out at the frame.
                float fade = Mathf.SmoothStep(0f, 1f, t * 4f) * (1f - Mathf.SmoothStep(0.75f, 1f, t));
                Color c = img.color;
                c.a = strength * fade * 0.55f;
                img.color = c;
            }
        }

        /// <summary>Tests: every visible streak lies outside the clear centre (normalised half-extents).</summary>
        public bool AllOutsideClearCentre()
        {
            Rect screen = ((RectTransform)transform).rect;
            foreach (var (rt, img, _, _, _) in lines)
            {
                if (!img.enabled || img.color.a < 0.01f) continue;
                Vector2 p = rt.anchoredPosition;
                Vector2 half = new Vector2(Mathf.Abs(Mathf.Cos(rt.localEulerAngles.z * Mathf.Deg2Rad)), Mathf.Abs(Mathf.Sin(rt.localEulerAngles.z * Mathf.Deg2Rad))) * rt.sizeDelta.x * 0.5f;
                Vector2 nearest = p - new Vector2(Mathf.Sign(p.x) * half.x, Mathf.Sign(p.y) * half.y);
                if (Mathf.Abs(nearest.x) < screen.width * ClearHalfExtent && Mathf.Abs(nearest.y) < screen.height * ClearHalfExtent) return false;
            }
            return true;
        }
    }
}
