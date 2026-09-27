using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.UI
{
    public struct HudEntrant
    {
        public string Name;
        public string Status;
        public float Distance;
        public Vector3 Position;
        public bool IsYou;
        public bool IsReplay;
    }

    /// <summary>Per-frame HUD facts supplied by the active session (offline or networked).</summary>
    public sealed class HudState
    {
        public float SpeedKmh;
        public int Gear;
        public float Rpm, Redline;
        public double RaceSeconds;
        public string Banner = "";
        public int Position, Entrants;
        public int Checkpoints, TotalCheckpoints;
        public int WallIncidents, Resets;
        public int RttMs = -1;
        /// <summary>Seconds left before the event closes after the first human finish; negative when not running.</summary>
        public float FinishWindowSeconds = -1f;
        public bool UseMph;
        /// <summary>Drift formats: banked raw score, the running chain and its multiplier, and a short bank/loss note.</summary>
        public bool DriftEvent;
        public long DriftBanked, DriftUnbanked;
        public float DriftChain = 1f;
        public string DriftNote = "";
        public readonly List<HudEntrant> Field = new List<HudEntrant>();
    }

    /// <summary>
    /// Compact racing HUD (spec §15): position, progress, time, speed/gear/revs, minimap, standings, banner.
    /// No shop/currency over live driving; the road stays clear in the centre.
    /// </summary>
    public sealed class RaceHud : MonoBehaviour
    {
        /// <summary>Player preference: display mph instead of km/h (never changes the simulation).</summary>
        public static bool UseMphGlobal;

        TextMeshProUGUI position, time, speed, unit, gear, banner, progress, incidents, connection, drift;
        Image revFill;
        RawImage minimap;
        RectTransform minimapRect;
        readonly List<RectTransform> dots = new List<RectTransform>();
        readonly List<TextMeshProUGUI> standings = new List<TextMeshProUGUI>();
        Vector2 mapMin;
        float mapScale;
        int mapSize;

        public static RaceHud Create()
        {
            Canvas canvas = UIFactory.Root("RaceHud", 10);
            var hud = canvas.gameObject.AddComponent<RaceHud>();
            hud.Build(canvas.transform);
            return hud;
        }

        void Build(Transform root)
        {
            // Top-left: position and progress.
            Image posPanel = UIFactory.Panel("Position", root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(32, -150), new Vector2(300, -32), new Color(0, 0, 0, 0.55f));
            position = UIFactory.Label("Pos", posPanel.transform, "P–", SignalTheme.HudNumeral, SignalTheme.Label, TextAlignmentOptions.TopLeft, true);
            UIFactory.Stretch(position.rectTransform, 12);
            progress = UIFactory.Label("Progress", posPanel.transform, "", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft);
            UIFactory.Stretch(progress.rectTransform, 12);

            // Top-centre: race clock and banner.
            time = UIFactory.Numeral("Time", root, SignalTheme.Numeral, SignalTheme.Timing, TextAlignmentOptions.Top);
            time.rectTransform.anchorMin = time.rectTransform.anchorMax = new Vector2(0.5f, 1);
            time.rectTransform.sizeDelta = new Vector2(360, 50);
            time.rectTransform.anchoredPosition = new Vector2(0, -44);
            drift = UIFactory.Label("Drift", root, "", SignalTheme.Subheading, SignalTheme.Label, TextAlignmentOptions.Top, true);
            drift.rectTransform.anchorMin = drift.rectTransform.anchorMax = new Vector2(0.5f, 1);
            drift.rectTransform.sizeDelta = new Vector2(760, 80);
            drift.rectTransform.anchoredPosition = new Vector2(0, -130);
            banner = UIFactory.Label("Banner", root, "", SignalTheme.HudNumeral * 1.2f, SignalTheme.Label, TextAlignmentOptions.Center, true);
            banner.rectTransform.anchorMin = banner.rectTransform.anchorMax = new Vector2(0.5f, 0.66f);
            banner.rectTransform.sizeDelta = new Vector2(1400, 140);

            // Bottom-right: speed, gear, rev bar with tachometer markings.
            Image speedPanel = UIFactory.Panel("Speedo", root, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-380, 32), new Vector2(-32, 196), new Color(0, 0, 0, 0.55f));
            speed = UIFactory.Numeral("Speed", speedPanel.transform, SignalTheme.HudNumeral * 1.25f, SignalTheme.Label);
            speed.rectTransform.anchorMin = new Vector2(0, 0.35f);
            speed.rectTransform.anchorMax = new Vector2(0.72f, 1);
            speed.rectTransform.offsetMin = speed.rectTransform.offsetMax = Vector2.zero;
            unit = UIFactory.Label("Unit", speedPanel.transform, "KM/H", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft, true);
            unit.rectTransform.anchorMin = new Vector2(0.73f, 0.4f);
            unit.rectTransform.anchorMax = new Vector2(1, 0.75f);
            unit.rectTransform.offsetMin = unit.rectTransform.offsetMax = Vector2.zero;
            gear = UIFactory.Numeral("Gear", speedPanel.transform, SignalTheme.HudNumeral, SignalTheme.Caution, TextAlignmentOptions.Center);
            gear.rectTransform.anchorMin = new Vector2(0.73f, 0.62f);
            gear.rectTransform.anchorMax = new Vector2(1, 1);
            gear.rectTransform.offsetMin = gear.rectTransform.offsetMax = Vector2.zero;
            Image revBg = UIFactory.Panel("RevBg", speedPanel.transform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(14, 14), new Vector2(-14, 34), SignalTheme.Rule);
            revFill = UIFactory.Panel("RevFill", revBg.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, SignalTheme.Label);
            revFill.type = Image.Type.Filled;
            revFill.fillMethod = Image.FillMethod.Horizontal;
            for (int i = 1; i < 10; i++)
                UIFactory.Panel("Tick" + i, revBg.transform, new Vector2(i / 10f, 0), new Vector2(i / 10f, 1), new Vector2(-1, 0), new Vector2(1, 0), SignalTheme.Ink);

            // Bottom-left: incidents and connection quality (only when meaningful).
            incidents = UIFactory.Label("Incidents", root, "", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft);
            incidents.rectTransform.anchorMin = incidents.rectTransform.anchorMax = new Vector2(0, 0);
            incidents.rectTransform.sizeDelta = new Vector2(600, 60);
            incidents.rectTransform.anchoredPosition = new Vector2(332, 60);
            connection = UIFactory.Label("Connection", root, "", SignalTheme.Small, SignalTheme.Caution, TextAlignmentOptions.TopRight);
            connection.rectTransform.anchorMin = connection.rectTransform.anchorMax = new Vector2(1, 1);
            connection.rectTransform.sizeDelta = new Vector2(500, 40);
            connection.rectTransform.anchoredPosition = new Vector2(-282, -40);

            // Top-right: minimap; below it the standings.
            Image mapBg = UIFactory.Panel("Minimap", root, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-272, -272), new Vector2(-32, -32), new Color(0, 0, 0, 0.45f));
            minimapRect = (RectTransform)mapBg.transform;
            var raw = new GameObject("Map", typeof(RectTransform)).AddComponent<RawImage>();
            raw.transform.SetParent(mapBg.transform, false);
            UIFactory.Stretch(raw.rectTransform, 4);
            raw.raycastTarget = false;
            minimap = raw;
            for (int i = 0; i < 7; i++)
            {
                TextMeshProUGUI s = UIFactory.Label("Standing" + i, root, "", SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.TopLeft);
                s.rectTransform.anchorMin = s.rectTransform.anchorMax = new Vector2(1, 1);
                s.rectTransform.sizeDelta = new Vector2(240, 26);
                s.rectTransform.anchoredPosition = new Vector2(-152, -292 - i * 26);
                standings.Add(s);
            }
        }

        public void SetCourse(Vector2[] plan)
        {
            mapSize = 256;
            minimap.texture = UIFactory.MinimapTexture(plan, mapSize, new Color(0.93f, 0.9f, 0.85f, 0.9f), out mapMin, out mapScale);
        }

        public void Render(HudState s)
        {
            speed.text = Tabular(Mathf.RoundToInt((s.UseMph || UseMphGlobal) ? s.SpeedKmh * 0.621371f : s.SpeedKmh).ToString());
            unit.text = (s.UseMph || UseMphGlobal) ? "MPH" : "KM/H";
            gear.text = s.Gear < 0 ? "R" : s.Gear == 0 ? "N" : s.Gear.ToString();
            float rev = s.Redline > 0 ? Mathf.Clamp01(s.Rpm / s.Redline) : 0f;
            revFill.fillAmount = rev;
            revFill.color = rev > 0.93f ? SignalTheme.Signal : rev > 0.8f ? SignalTheme.Caution : SignalTheme.Label;
            position.text = s.Entrants > 0 ? $"P{s.Position}<size=45%><color=#9A968D> / {s.Entrants}</color></size>" : "";
            progress.text = (s.TotalCheckpoints > 0 ? $"CHECKPOINT {s.Checkpoints} / {s.TotalCheckpoints}" : "")
                + (s.FinishWindowSeconds >= 0 ? $"\n<color=#{ColorUtility.ToHtmlStringRGB(SignalTheme.Caution)}>FINISH WINDOW {FormatClock(s.FinishWindowSeconds)}</color>" : "");
            time.text = Tabular(FormatTime(s.RaceSeconds));
            banner.text = s.Banner;
            drift.text = !s.DriftEvent ? ""
                : $"DRIFT {Tabular(s.DriftBanked.ToString("N0"))}"
                  + (s.DriftUnbanked > 0 ? $"   <color=#{ColorUtility.ToHtmlStringRGB(SignalTheme.Caution)}>+{Tabular(s.DriftUnbanked.ToString("N0"))}  ×{s.DriftChain:0.00}</color>" : "")
                  + (s.DriftNote.Length > 0 ? "\n<size=80%>" + s.DriftNote + "</size>" : "");
            incidents.text = s.WallIncidents > 0 || s.Resets > 0 ? $"WALL CONTACTS {s.WallIncidents}   RESETS {s.Resets}" : "";
            connection.text = s.RttMs > 180 ? $"CONNECTION  {s.RttMs} MS" : "";

            while (dots.Count < s.Field.Count)
            {
                Image d = UIFactory.Panel("Dot", minimap.transform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(-5, -5), new Vector2(5, 5), SignalTheme.Label);
                dots.Add((RectTransform)d.transform);
            }
            Rect r = minimap.rectTransform.rect;
            for (int i = 0; i < dots.Count; i++)
            {
                bool on = i < s.Field.Count;
                dots[i].gameObject.SetActive(on);
                if (!on || mapScale <= 0f) continue;
                HudEntrant e = s.Field[i];
                Vector2 p = (new Vector2(e.Position.x, e.Position.z) - mapMin) * mapScale / mapSize;
                dots[i].anchorMin = dots[i].anchorMax = p;
                dots[i].GetComponent<Image>().color = e.IsYou ? SignalTheme.Signal : e.IsReplay ? SignalTheme.Timing : SignalTheme.Label;
                dots[i].sizeDelta = e.IsYou ? new Vector2(14, 14) : new Vector2(10, 10);
            }
            for (int i = 0; i < standings.Count; i++)
            {
                if (i >= s.Field.Count) { standings[i].text = ""; continue; }
                HudEntrant e = s.Field[i];
                string tag = e.IsReplay ? " <color=#3EC6D8>REPLAY</color>" : e.Status == "" ? "" : $" <color=#9A968D>{e.Status}</color>";
                standings[i].text = e.IsReplay ? $"—  {e.Name}{tag}" : $"{i + 1}  {(e.IsYou ? "<color=#D7263D>" : "")}{e.Name}{(e.IsYou ? "</color>" : "")}{tag}";
            }
        }

        /// <summary>Tabular figures: fixed advance per glyph so changing digits don't shift (Liberation Sans has proportional digits).</summary>
        public static string Tabular(string digits) => "<mspace=0.58em>" + digits + "</mspace>";

        static string FormatClock(float seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

        public static string FormatTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int m = (int)(seconds / 60);
            double s = seconds - m * 60;
            return $"{m}:{s:00.000}";
        }
    }
}
