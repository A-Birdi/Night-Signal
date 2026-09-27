using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightSignal.Track
{
    [Serializable]
    public struct GridSlot
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Distance;
    }

    /// <summary>
    /// Baked, versioned course data used identically by the authoritative server and clients: 1 m centreline
    /// samples, checkpoints, sectors, judged gates and the twelve-slot grid. Produced by the course baker from
    /// route.json; <see cref="SourceHash"/> ties it to the exact source revision.
    /// </summary>
    [CreateAssetMenu(menuName = "Night Signal/Track Data")]
    public sealed class TrackData : ScriptableObject
    {
        public string CourseId;
        public int Revision;
        public string SourceHash;
        public bool ClosedLoop;
        public int Laps = 1;
        public float LengthMetres;
        public float StartMetres;
        public TrackSample[] Samples = Array.Empty<TrackSample>();
        public float[] CheckpointMetres = Array.Empty<float>();
        public List<RouteSectorDef> Sectors = new List<RouteSectorDef>();
        public List<RouteGateDef> Gates = new List<RouteGateDef>();
        public GridSlot[] Grid = Array.Empty<GridSlot>();

        public TrackSample SampleAt(float distance)
        {
            if (Samples.Length == 0) return default;
            if (ClosedLoop) distance = Mathf.Repeat(distance, LengthMetres);
            float f = Mathf.Clamp(distance, 0f, LengthMetres);
            int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, Samples.Length - 2);
            float u = f - Samples[i].Distance;
            TrackSample a = Samples[i], b = Samples[i + 1];
            a.Position = Vector3.Lerp(a.Position, b.Position, u);
            a.Tangent = Vector3.Slerp(a.Tangent, b.Tangent, u).normalized;
            a.Right = Vector3.Slerp(a.Right, b.Right, u).normalized;
            a.Width = Mathf.Lerp(a.Width, b.Width, u);
            a.Distance = f;
            return a;
        }
    }

    public struct TrackLocation
    {
        public int Index;
        public float Distance;
        /// <summary>Signed lateral offset from the centreline along the banked right vector (m).</summary>
        public float Lateral;
        public float Vertical;
        public bool OnPaved;
        /// <summary>Within paved road + shoulders + a small tolerance (the legal corridor).</summary>
        public bool InCorridor;
        /// <summary>Dot of a supplied heading with the route tangent (−1 = wrong way).</summary>
        public float HeadingDot;
        /// <summary>
        /// Within the road layer's vertical envelope (Addendum 03 D307): a car that fell below the road — onto a lower
        /// switchback or the valley floor — or is far above it is not on this road, whatever its X/Z.
        /// </summary>
        public bool OnLayer;
        /// <summary>The car left the tracked route neighbourhood; progress holds the last legal place (no jump to another road).</summary>
        public bool Lost;
    }

    /// <summary>Spatial queries over <see cref="TrackData"/>; one instance per tracked car (holds a search hint).</summary>
    public sealed class TrackLocator
    {
        public const float CorridorToleranceMetres = 1.0f;
        /// <summary>Road-layer envelope relative to the road surface (the car's centre rides ~0.5 m above it; crests fly a few metres).</summary>
        public const float LayerBelowMetres = -2.5f, LayerAboveMetres = 12f;
        /// <summary>Tracking window around the last located sample (m of route either way).</summary>
        public const int WindowMetres = 60;
        readonly TrackData track;
        int hint = -1;

        /// <summary>
        /// When the car is not near its tracked stretch: search the whole route (AI steering, which only needs a nearby line)
        /// or stay put and report <see cref="TrackLocation.Lost"/> (race progress: never jump to a spatially close but later
        /// road — stacked switchbacks, overpasses). A reset or spawn re-anchors explicitly with <see cref="Reset"/>.
        /// </summary>
        public bool AllowGlobalRecovery = true;

        public TrackLocator(TrackData track)
        {
            this.track = track ?? throw new ArgumentNullException(nameof(track));
        }

        public void Reset(float distance) => hint = Mathf.Clamp(Mathf.RoundToInt(distance), 0, track.Samples.Length - 1);

        public TrackLocation Locate(Vector3 position, Vector3 heading)
        {
            TrackSample[] s = track.Samples;
            int best;
            if (hint < 0) best = GlobalNearest(position);
            else
            {
                best = hint;
                float bestD = (s[hint].Position - position).sqrMagnitude;
                for (int k = -WindowMetres; k <= WindowMetres; k++)
                {
                    int i = Wrap(hint + k);
                    if (i < 0) continue;
                    float d = (s[i].Position - position).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (bestD > WindowMetres * WindowMetres)
                {
                    if (!AllowGlobalRecovery) return Located(hint, position, heading, lost: true);
                    best = GlobalNearest(position); // AI steering only: find the nearest line again
                }
            }
            hint = best;
            return Located(best, position, heading, lost: false);
        }

        TrackLocation Located(int best, Vector3 position, Vector3 heading, bool lost)
        {
            TrackSample[] s = track.Samples;

            TrackSample c = s[best];
            Vector3 rel = position - c.Position;
            float along = Vector3.Dot(rel, c.Tangent);
            float distance = c.Distance + along;
            if (track.ClosedLoop) distance = Mathf.Repeat(distance, track.LengthMetres);
            float lateral = Vector3.Dot(rel, c.Right);
            float vertical = Vector3.Dot(rel, c.Up);
            float half = c.Width * 0.5f;
            float shoulder = lateral >= 0f ? c.ShoulderRight : c.ShoulderLeft;
            bool onLayer = !lost && vertical >= LayerBelowMetres && vertical <= LayerAboveMetres;
            return new TrackLocation
            {
                Index = best,
                Distance = Mathf.Clamp(distance, 0f, track.LengthMetres),
                Lateral = lateral,
                Vertical = vertical,
                OnPaved = onLayer && Mathf.Abs(lateral) <= half,
                InCorridor = onLayer && Mathf.Abs(lateral) <= half + shoulder + CorridorToleranceMetres,
                HeadingDot = heading.sqrMagnitude > 0f ? Vector3.Dot(heading.normalized, c.Tangent) : 1f,
                OnLayer = onLayer,
                Lost = lost,
            };
        }

        int GlobalNearest(Vector3 position)
        {
            TrackSample[] s = track.Samples;
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < s.Length; i += 2)
            {
                float d = (s[i].Position - position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        int Wrap(int i)
        {
            int n = track.Samples.Length;
            if (track.ClosedLoop) return ((i % n) + n) % n;
            return i < 0 || i >= n ? -1 : i;
        }
    }
}
