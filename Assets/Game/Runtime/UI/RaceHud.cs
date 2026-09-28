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
        /// <summary>Canonical road speed (m/s, Addendum 03 §1.3) — converted only for display.</summary>
        public float RoadSpeedMps;
        /// <summary>The car's speed envelope (m/s) for choosing a stable dial/strip scale once per car.</summary>
        public float EnvelopeMps = 70f;
        /// <summary>False when there is no valid target (disconnect, lost spectate target): a neutral instrument, never a stale car.</summary>
        public bool SpeedAvailable = true;
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
        /// <summary>
        /// Recovery (Addendum 03 §7.1): the offer/countdown text (empty = none), the reset-hold progress (0..1), progress
        /// toward an automatic recovery (−1 = none pending) and a brief notice after a completed recovery.
        /// </summary>
        public string RecoveryPrompt = "";
        public float ResetHoldFraction;
        public float RecoveryAutoFraction = -1f;
        public string RecoveryNotice = "";
        /// <summary>The interval to the car directly ahead (s; negative = none or out of reach) and its driver.</summary>
        public float GapAheadSeconds = -1f;
        /// <summary>A brief "checkpoint n ±x.xx s vs ghost" line after each checkpoint when a ghost is racing (spec §8).</summary>
        public string GhostDelta = "";
        public string GapAheadName = "";
        public readonly List<HudEntrant> Field = new List<HudEntrant>();
    }

    /// <summary>
    /// Compact racing HUD (spec §15): position, progress, time, speed/gear/revs, minimap, standings, banner.
    /// No shop/currency over live driving; the road stays clear in the centre.
    /// </summary>
    public sealed class RaceHud : MonoBehaviour
    {
        TextMeshProUGUI position, time, banner, progress, incidents, connection, drift, recovery, gap, ghost;
        GameObject recoveryPanel;
        RectTransform recoveryBar;
        SpeedCluster cluster;
        /// <summary>Evidence runs read what the instrument actually shows.</summary>
        public SpeedCluster Cluster => cluster;

        /// <summary>A discontinuity (reset, recovery, respawn, car switch): the instrument jumps to the new truth instead of gliding from the old car's speed.</summary>
        public void NotifyDiscontinuity() => cluster?.ResetFilter();
        CanvasScaler scaler;
        Image standingsPanel;
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
            hud.scaler = canvas.GetComponent<CanvasScaler>();
            hud.Build(canvas.transform);
            return hud;
        }

        void Build(Transform root)
        {
            // Top-left: position and progress.
            Image posPanel = UIFactory.Panel("Position", root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(32, -150), new Vector2(300, -32), new Color(0, 0, 0, 0.55f));
            // Position above, progress below: separate bands so larger text shrinks within its own band, never over the other.
            position = UIFactory.Label("Pos", posPanel.transform, "P–", SignalTheme.HudNumeral, SignalTheme.Label, TextAlignmentOptions.TopLeft, true);
            UIFactory.Stretch(position.rectTransform, 12);
            position.rectTransform.anchorMin = new Vector2(0f, 0.36f);
            position.rectTransform.offsetMin = new Vector2(12f, 0f);
            progress = UIFactory.Label("Progress", posPanel.transform, "", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft);
            UIFactory.Stretch(progress.rectTransform, 12);
            progress.rectTransform.anchorMax = new Vector2(1f, 0.36f);
            progress.textWrappingMode = TextWrappingModes.Normal; // the finish-window line goes under the checkpoint count
            // Under it: the interval to the car ahead (racecraft: CH32 follows inside the 1–2 s window shown here).
            gap = UIFactory.Label("GapAhead", root, "", SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.TopLeft);
            gap.rectTransform.anchorMin = gap.rectTransform.anchorMax = new Vector2(0, 1);
            gap.rectTransform.pivot = new Vector2(0, 1);
            gap.rectTransform.sizeDelta = new Vector2(420, 30);
            gap.rectTransform.anchoredPosition = new Vector2(34, -158);

            // Top-centre: race clock and banner.
            time = UIFactory.Numeral("Time", root, SignalTheme.Numeral, SignalTheme.Timing, TextAlignmentOptions.Top);
            time.rectTransform.anchorMin = time.rectTransform.anchorMax = new Vector2(0.5f, 1);
            time.rectTransform.sizeDelta = new Vector2(360, 50);
            time.rectTransform.anchoredPosition = new Vector2(0, -44);
            drift = UIFactory.Label("Drift", root, "", SignalTheme.Subheading, SignalTheme.Label, TextAlignmentOptions.Top, true);
            drift.rectTransform.anchorMin = drift.rectTransform.anchorMax = new Vector2(0.5f, 1);
            drift.rectTransform.sizeDelta = new Vector2(760, 80);
            drift.rectTransform.anchoredPosition = new Vector2(0, -130);
            ghost = UIFactory.Label("GhostDelta", root, "", SignalTheme.Body, SignalTheme.Timing, TextAlignmentOptions.Top);
            ghost.rectTransform.anchorMin = ghost.rectTransform.anchorMax = new Vector2(0.5f, 1);
            ghost.rectTransform.sizeDelta = new Vector2(900, 40);
            ghost.rectTransform.anchoredPosition = new Vector2(0, -96);
            banner = UIFactory.Label("Banner", root, "", SignalTheme.HudNumeral * 1.2f, SignalTheme.Label, TextAlignmentOptions.Center, true);
            banner.rectTransform.anchorMin = banner.rectTransform.anchorMax = new Vector2(0.5f, 0.66f);
            banner.rectTransform.sizeDelta = new Vector2(1400, 140);

            // Lower centre: the recovery offer / countdown / hold progress (clear of the road ahead and of the cluster).
            Image rp = UIFactory.Panel("Recovery", root, new Vector2(0.5f, 0.2f), new Vector2(0.5f, 0.2f), new Vector2(-380, -54), new Vector2(380, 54), new Color(0, 0, 0, 0.62f));
            recoveryPanel = rp.gameObject;
            recovery = UIFactory.Label("RecoveryText", rp.transform, "", SignalTheme.Body, SignalTheme.Label, TextAlignmentOptions.Center, true);
            UIFactory.Stretch(recovery.rectTransform, 8);
            recovery.rectTransform.offsetMin = new Vector2(12, 14);
            Image track = UIFactory.Panel("RecoveryTrack", rp.transform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(12, 6), new Vector2(-12, 12), new Color(1, 1, 1, 0.15f));
            Image fill = UIFactory.Panel("RecoveryFill", track.transform, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, Vector2.zero, SignalTheme.Caution);
            recoveryBar = (RectTransform)fill.transform;
            recoveryPanel.SetActive(false);

            // Bottom-right: the instrument cluster (Instrument Dial or Digital Strip for road speed; RPM and gear beside it).
            cluster = new SpeedCluster(root);

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
            // A backing panel so the names read against a bright sky as well as a night one (G06).
            standingsPanel = UIFactory.Panel("StandingsPanel", root, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-272, -284 - 7 * 26), new Vector2(-32, -276), new Color(0, 0, 0, 0.45f));
            standingsPanel.raycastTarget = false;
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
            DrivingPreferences prefs = DrivingPreferences.Current;
            // HUD size: the whole race HUD scales about its anchors (a smaller reference resolution = larger elements).
            if (scaler != null) scaler.referenceResolution = new Vector2(1920f, 1080f) / Mathf.Clamp(prefs.HudScale, 0.8f, 1.4f);
            SpeedUnit u = s.UseMph ? SpeedUnit.Mph : prefs.Unit;
            cluster.Configure(prefs.Dial, SpeedDisplay.ScaleFor(s.EnvelopeMps, u));
            cluster.Render(s.RoadSpeedMps, s.Rpm, s.Redline, s.Gear, s.SpeedAvailable, Time.unscaledDeltaTime);
            position.text = s.Entrants > 0 ? $"P{s.Position}<size=45%><color=#9A968D> / {s.Entrants}</color></size>" : "";
            progress.text = (s.TotalCheckpoints > 0 ? $"CHECKPOINT {s.Checkpoints} / {s.TotalCheckpoints}" : "")
                + (s.FinishWindowSeconds >= 0 ? $"\n<color=#{ColorUtility.ToHtmlStringRGB(SignalTheme.Caution)}>FINISH WINDOW {FormatClock(s.FinishWindowSeconds)}</color>" : "");
            time.text = Tabular(FormatTime(s.RaceSeconds));
            ghost.text = s.GhostDelta ?? "";
            bool window = s.GapAheadSeconds >= 1f && s.GapAheadSeconds <= 2f;
            gap.text = s.GapAheadSeconds < 0f ? ""
                : $"GAP AHEAD  <color=#{ColorUtility.ToHtmlStringRGB(window ? SignalTheme.Timing : SignalTheme.Label)}>{Tabular(s.GapAheadSeconds.ToString("0.0"))} s</color>" +
                  $"  <color=#9A968D>{s.GapAheadName}</color>";
            banner.text = s.Banner;
            drift.text = !s.DriftEvent ? ""
                : $"DRIFT {Tabular(s.DriftBanked.ToString("N0"))}"
                  + (s.DriftUnbanked > 0 ? $"   <color=#{ColorUtility.ToHtmlStringRGB(SignalTheme.Caution)}>+{Tabular(s.DriftUnbanked.ToString("N0"))}  ×{s.DriftChain:0.00}</color>" : "")
                  + (s.DriftNote.Length > 0 ? "\n<size=80%>" + s.DriftNote + "</size>" : "");
            incidents.text = s.WallIncidents > 0 || s.Resets > 0 ? $"WALL CONTACTS {s.WallIncidents}   RESETS {s.Resets}" : "";
            bool holding = s.ResetHoldFraction > 0.01f;
            string offer = holding ? "RESETTING TO THE TRACK" : s.RecoveryPrompt.Length > 0 ? s.RecoveryPrompt : s.RecoveryNotice;
            recoveryPanel.SetActive(offer.Length > 0);
            if (offer.Length > 0)
            {
                recovery.text = offer;
                float bar = holding ? s.ResetHoldFraction : s.RecoveryPrompt.Length > 0 ? s.RecoveryAutoFraction : -1f;
                recoveryBar.parent.gameObject.SetActive(bar >= 0f);
                recoveryBar.anchorMax = new Vector2(Mathf.Clamp01(bar), 1f);
            }
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
            if (standingsPanel != null)
            {
                int rows = Mathf.Min(standings.Count, s.Field.Count);
                standingsPanel.gameObject.SetActive(rows > 0);
                standingsPanel.rectTransform.offsetMin = new Vector2(-272, -284 - rows * 26); // rows are 26 px, the first centred at -292
            }
            for (int i = 0; i < standings.Count; i++)
            {
                if (i >= s.Field.Count) { standings[i].text = ""; continue; }
                HudEntrant e = s.Field[i];
                string tag = e.IsReplay ? " <color=#3EC6D8>REPLAY</color>" : e.Status == "" ? "" : $" <color=#9A968D>{e.Status}</color>";
                standings[i].text = e.IsReplay ? $"—  {e.Name}{tag}" : $"{i + 1}  {(e.IsYou ? "<color=#D7263D>" : "")}{e.Name}{(e.IsYou ? "</color>" : "")}{tag}";
            }
        }

        /// <summary>
        /// Fills the recovery fields from the simulation's offer for the driven car. <paramref name="resetLabel"/> is the
        /// current (remappable) reset binding; <paramref name="penalty"/> is shown where a recovery costs time.
        /// </summary>
        public static void SetRecovery(HudState h, Race.RecoveryStatus r, string resetLabel, bool penalty)
        {
            string cost = penalty ? "  ·  +3.000 s" : "";
            h.ResetHoldFraction = r.HoldFraction;
            h.RecoveryAutoFraction = -1f;
            switch (r.Kind)
            {
                case Race.RecoveryKind.OffRoute:
                case Race.RecoveryKind.Overturned:
                    string what = r.Kind == Race.RecoveryKind.OffRoute ? "OFF ROUTE" : "OVERTURNED";
                    if (r.SecondsToAuto < 0f)
                    {
                        h.RecoveryPrompt = $"{what}  —  Hold {resetLabel} to reset<size=70%><color=#9A968D>{cost}</color></size>";
                        break;
                    }
                    h.RecoveryPrompt = $"{what} — RECOVERING IN {r.SecondsToAuto:0.0} s\n<size=70%><color=#9A968D>Hold {resetLabel} to reset now{cost}</color></size>";
                    float window = r.Kind == Race.RecoveryKind.OffRoute ? Race.RaceSimulation.AutoRescueSeconds
                        : Race.RaceSimulation.OverturnedRescueSeconds - Race.RaceSimulation.OverturnedPromptSeconds;
                    h.RecoveryAutoFraction = Mathf.Clamp01(1f - r.SecondsToAuto / window);
                    break;
                case Race.RecoveryKind.Stopped:
                    h.RecoveryPrompt = $"STUCK?  Hold {resetLabel} to reset to the track<size=70%><color=#9A968D>{cost}</color></size>";
                    break;
                default:
                    h.RecoveryPrompt = "";
                    break;
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
