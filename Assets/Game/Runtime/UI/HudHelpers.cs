using NightSignal.Track;
using UnityEngine;

namespace NightSignal.UI
{
    public static class HudHelpers
    {
        /// <summary>Plan-view polyline of the course for the minimap (every 8 m).</summary>
        public static Vector2[] Plan(TrackData track)
        {
            int n = Mathf.Max(2, track.Samples.Length / 8);
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = track.Samples[Mathf.Min(track.Samples.Length - 1, i * 8)].Position;
                pts[i] = new Vector2(p.x, p.z);
            }
            return pts;
        }

        /// <summary>3-2-1-GO text from seconds remaining until the start tick.</summary>
        public static string Countdown(float secondsToStart)
        {
            if (secondsToStart > 3f) return "";
            if (secondsToStart > 0f) return Mathf.CeilToInt(secondsToStart).ToString();
            return secondsToStart > -1f ? "GO" : "";
        }
    }
}
