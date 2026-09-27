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
    }

    /// <summary>Spatial queries over <see cref="TrackData"/>; one instance per tracked car (holds a search hint).</summary>
    public sealed class TrackLocator
    {
        public const float CorridorToleranceMetres = 1.0f;
        readonly TrackData track;
        int hint = -1;

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
                const int window = 60;
                for (int k = -window; k <= window; k++)
                {
                    int i = Wrap(hint + k);
                    if (i < 0) continue;
                    float d = (s[i].Position - position).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
                // A teleport/reset or a hint far from the car falls back to a global search.
                if (bestD > 60f * 60f) best = GlobalNearest(position);
            }
            hint = best;

            TrackSample c = s[best];
            Vector3 rel = position - c.Position;
            float along = Vector3.Dot(rel, c.Tangent);
            float distance = c.Distance + along;
            if (track.ClosedLoop) distance = Mathf.Repeat(distance, track.LengthMetres);
            float lateral = Vector3.Dot(rel, c.Right);
            float half = c.Width * 0.5f;
            float shoulder = lateral >= 0f ? c.ShoulderRight : c.ShoulderLeft;
            return new TrackLocation
            {
                Index = best,
                Distance = Mathf.Clamp(distance, 0f, track.LengthMetres),
                Lateral = lateral,
                Vertical = Vector3.Dot(rel, c.Up),
                OnPaved = Mathf.Abs(lateral) <= half,
                InCorridor = Mathf.Abs(lateral) <= half + shoulder + CorridorToleranceMetres,
                HeadingDot = heading.sqrMagnitude > 0f ? Vector3.Dot(heading.normalized, c.Tangent) : 1f,
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
