using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NightSignal.UI;
using TMPro;
using UnityEngine;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// String bounds audit (Gate 4; <c>-nsBoundsAudit</c> beside any tour): every visible label the tour passes — measured
        /// after each screen change, at each tour screenshot and every few seconds in a race — must fit its box. Factory
        /// labels shrink to fit down to half size and overflow (stay visible) only as a last resort, so a label that is too
        /// big at its minimum size, or spills more than 5 % past its box, is a failure. TMP fits by advance widths, so the
        /// visible glyph edges of a fitting label can reach a few pixels past the box: those are listed with the spill.
        /// Fixed-size live figures (clock, speed) that overflow are listed apart; labels left at their minimum size are
        /// noted. The report is written at quit to Builds/Screenshots/bounds/.
        /// </summary>
        public static bool BoundsAuditOn => boundsAuditOn ?? (bool)(boundsAuditOn = Array.IndexOf(Environment.GetCommandLineArgs(), "-nsBoundsAudit") >= 0);
        static bool? boundsAuditOn;

        sealed class BoundsFinding
        {
            public string Where, Path, Text, Kind;
            public Vector2 Box, Rendered;
            public float Size, Min, Spill;
        }

        readonly Dictionary<string, BoundsFinding> boundsFindings = new Dictionary<string, BoundsFinding>();
        readonly List<string> boundsMoments = new List<string>();
        readonly HashSet<string> boundsLabels = new HashSet<string>();
        int boundsMeasurements;

        /// <summary>Auto-sized labels that did not fit even at their minimum size (the tour's failure count).</summary>
        public int BoundsAutoSizeOverflows => boundsFindings.Values.Count(f => f.Kind == "overflow");

        IEnumerator BoundsAuditLoop()
        {
            UIScreen last = null;
            float due = 0f, nextRaceCheck = 0f;
            while (true)
            {
                bool racing = activeRace != null || onlineRace != null;
                if (Router.Current != last)
                {
                    last = Router.Current;
                    due = Time.realtimeSinceStartup + 0.8f; // let the screen build, fade and lay out
                }
                if (due > 0f && Time.realtimeSinceStartup >= due)
                {
                    due = 0f;
                    AuditBounds((racing ? "race / " : "") + (last?.ScreenName ?? "no screen"));
                }
                if (racing && Time.realtimeSinceStartup >= nextRaceCheck)
                {
                    nextRaceCheck = Time.realtimeSinceStartup + 5f;
                    AuditBounds("race HUD");
                }
                yield return null;
            }
        }

        /// <summary>Measures every visible label now (a tour calls it with its screenshot name).</summary>
        public void AuditBounds(string where)
        {
            if (!BoundsAuditOn) return;
            Canvas.ForceUpdateCanvases();
            boundsMoments.Add(where);
            foreach (TMP_Text t in FindObjectsByType<TMP_Text>())
            {
                if (!t.isActiveAndEnabled || t.canvas == null || string.IsNullOrWhiteSpace(t.text) || t.color.a < 0.05f || Faded(t.transform)) continue;
                t.ForceMeshUpdate();
                Vector2 rendered = t.GetRenderedValues(true);
                if (!(rendered.x > 0f) || !(rendered.y > 0f) || float.IsInfinity(rendered.x) || float.IsInfinity(rendered.y)) continue; // nothing visible drawn
                Rect r = t.rectTransform.rect;
                var box = new Vector2(r.width - t.margin.x - t.margin.z, r.height - t.margin.y - t.margin.w);
                string path = PathOf(t.transform);
                boundsLabels.Add(path);
                boundsMeasurements++;
                const float slack = 2f; // sub-pixel glyph bearings
                float spill = Mathf.Max(rendered.x - box.x, rendered.y - box.y);
                float share = Mathf.Max((rendered.x - box.x) / Mathf.Max(1f, box.x), (rendered.y - box.y) / Mathf.Max(1f, box.y));
                bool atMinimum = t.enableAutoSizing && t.fontSize <= t.fontSizeMin + 0.05f;
                string kind = spill <= slack ? (atMinimum ? "minimum" : null)
                    : !t.enableAutoSizing ? "overflow-fixed"
                    : atMinimum || share > 0.05f ? "overflow" : "spill";
                if (kind == null) continue;
                string text = t.GetParsedText().Replace("\n", " ⏎ ");
                string key = kind + "|" + path + "|" + text;
                if (boundsFindings.ContainsKey(key)) continue;
                boundsFindings[key] = new BoundsFinding
                {
                    Where = where, Path = path, Text = text.Length > 60 ? text.Substring(0, 57) + "..." : text, Kind = kind,
                    Box = box, Rendered = rendered, Size = t.fontSize, Min = t.fontSizeMin, Spill = spill,
                };
            }
        }

        static bool Faded(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                CanvasGroup g = p.GetComponent<CanvasGroup>();
                if (g != null && g.alpha < 0.05f) return true;
            }
            return false;
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (Transform p = t; p != null && parts.Count < 5; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        void OnApplicationQuit()
        {
            if (!BoundsAuditOn || boundsMoments.Count == 0) return;
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                string tour = args.FirstOrDefault(a => a.StartsWith("-ns") && a.EndsWith("Tour"))?.Substring(3) ?? "run";
                DrivingPreferences p = DrivingPreferences.Current;
                string name = $"bounds-{tour}-{Screen.width}x{Screen.height}-text{Mathf.RoundToInt(p.TextScale * 100)}";
                string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "bounds"));
                System.IO.Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.AppendLine($"# String bounds audit — {tour} at {Screen.width}x{Screen.height}, text size {p.TextScale * 100:F0} %, HUD size {p.HudScale * 100:F0} %");
                sb.AppendLine($"# {boundsMoments.Count} moments audited, {boundsLabels.Count} distinct labels, {boundsMeasurements} measurements");
                sb.AppendLine("# moments: " + string.Join(" · ", boundsMoments.Distinct()));
                foreach (var group in new[]
                {
                    new { Kind = "overflow", Title = "Labels too big at their minimum size, or spilling more than 5 % past their box (failures)" },
                    new { Kind = "spill", Title = "Fitting labels whose glyph edges reach a few pixels past the box (TMP fits by advance widths; within 5 %)" },
                    new { Kind = "overflow-fixed", Title = "Fixed-size live figures drawn beyond their box (they never clip; inspect for collisions)" },
                    new { Kind = "minimum", Title = "Labels that fit only at their minimum size (legible, but the smallest allowed)" },
                })
                {
                    List<BoundsFinding> list = boundsFindings.Values.Where(f => f.Kind == group.Kind).ToList();
                    sb.AppendLine();
                    sb.AppendLine($"## {group.Title}: {list.Count}");
                    foreach (BoundsFinding f in list)
                        sb.AppendLine($"- [{f.Where}] {f.Path} — \"{f.Text}\" box {f.Box.x:F0}x{f.Box.y:F0}, drawn {f.Rendered.x:F0}x{f.Rendered.y:F0} " +
                                      $"(spill {Mathf.Max(0f, f.Spill):F0} px), size {f.Size:F1} (min {f.Min:F1})");
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name + ".txt"), sb.ToString());
                Debug.Log($"[NightSignal.Bounds] {name}: {boundsLabels.Count} labels over {boundsMoments.Count} moments — " +
                          $"{BoundsAutoSizeOverflows} overflow, {boundsFindings.Values.Count(f => f.Kind == "spill")} edge spill, " +
                          $"{boundsFindings.Values.Count(f => f.Kind == "overflow-fixed")} fixed-size overflow, " +
                          $"{boundsFindings.Values.Count(f => f.Kind == "minimum")} at minimum size");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NightSignal.Bounds] report not written: " + e.Message);
            }
        }
    }
}
