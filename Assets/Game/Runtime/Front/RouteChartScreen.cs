using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Ghosts;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// The post-race route/elevation chart on its own page (spec §8) — for the last online race, from this client's own
    /// display-only trace and the checkpoint deltas against the first ghost on the road. The offline Results page shows the
    /// same chart in place.
    /// </summary>
    public sealed class RouteChartScreen : UIScreen
    {
        public override string ScreenName => "RouteChart";
        TextMeshProUGUI heading, line;
        RawImage image;
        Button back;
        Texture2D texture;
        RouteChart chart;
        string reference, title = "";

        /// <summary>The chart's one-line summary (tours read it); empty without a chart.</summary>
        public string Summary => chart?.Summary(reference ?? "the reference") ?? "";
        public bool HasChart => chart != null && chart.Metres.Count > 1;

        public void Set(GhostRecording run, IReadOnlyList<long> deltas, string referenceLabel, string heading)
        {
            chart = run != null && run.Count > 1 ? RouteChart.Build(run, deltas != null && deltas.Count > 0 ? deltas.ToList() : null) : null;
            reference = string.IsNullOrEmpty(referenceLabel) ? null : referenceLabel.Replace("Ghost · ", "");
            title = heading ?? "";
            if (texture != null) Object.Destroy(texture);
            texture = null;
        }

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0.06f, 0.06f), new Vector2(0.94f, 0.94f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.94f));
            heading = UIFactory.Label("Heading", panel.transform, "", SignalTheme.Heading, SignalTheme.Label, TextAlignmentOptions.TopLeft, true);
            heading.rectTransform.anchorMin = new Vector2(0, 0.87f);
            heading.rectTransform.anchorMax = new Vector2(1, 0.97f);
            heading.rectTransform.offsetMin = new Vector2(48, 0);
            // The chart keeps the texture's proportions inside its area (the fitter sizes to this container, not the panel).
            RectTransform area = UIFactory.Rect("ChartArea", panel.transform, new Vector2(0.06f, 0.2f), new Vector2(0.94f, 0.86f), Vector2.zero, Vector2.zero);
            image = new GameObject("ChartImage", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            image.transform.SetParent(area, false);
            image.rectTransform.anchorMin = Vector2.zero;
            image.rectTransform.anchorMax = Vector2.one;
            image.rectTransform.offsetMin = Vector2.zero;
            image.rectTransform.offsetMax = Vector2.zero;
            var fit = image.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fit.aspectRatio = 1100f / 620f;
            line = UIFactory.Label("ChartLine", panel.transform, "", SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.TopLeft);
            line.rectTransform.anchorMin = new Vector2(0.06f, 0.12f);
            line.rectTransform.anchorMax = new Vector2(0.94f, 0.19f);
            line.rectTransform.offsetMin = Vector2.zero;
            line.rectTransform.offsetMax = Vector2.zero;
            line.textWrappingMode = TextWrappingModes.Normal;
            line.richText = false;
            RectTransform actions = UIFactory.Column("Actions", panel.transform, new Vector2(0, 0), new Vector2(1, 0.11f), new Vector2(48, 8), new Vector2(-48, -8));
            back = UIFactory.Button("Back", actions, "Back", () => App.Router.Back(), 360, 56);
        }

        public override Selectable DefaultFocus => back;

        public override void OnShow()
        {
            heading.text = "ROUTE CHART" + (string.IsNullOrEmpty(title) ? "" : "  ·  " + title);
            if (HasChart && texture == null) texture = RouteChartTexture.Draw(chart);
            image.texture = texture;
            image.gameObject.SetActive(HasChart);
            line.text = HasChart ? Summary + "   ·   route: red lost / cyan gained per sector, white = braking; below: elevation"
                : "No chart: the last race was not recorded on this PC.";
        }
    }
}
