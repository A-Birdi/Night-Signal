using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightSignal.Track
{
    /// <summary>Authored route control point (source geometry, stable ID).</summary>
    [Serializable]
    public sealed class RouteControlPoint
    {
        public string Id;
        public float[] P = new float[3];
        /// <summary>Paved road width, metres (7–9 ordinary, 6 technical, 10–12 grids/passing).</summary>
        public float Width = 8f;
        /// <summary>Camber/bank in degrees; positive raises the left edge (banks into a right-hand turn).</summary>
        public float Bank;
        public float ShoulderLeft = 1.5f;
        public float ShoulderRight = 1.5f;

        public Vector3 Position => new Vector3(P[0], P[1], P[2]);
    }

    [Serializable]
    public sealed class RouteSectorDef
    {
        public string Id;
        public string Name;
        public float StartMetres;
    }

    /// <summary>Judged zone or gate placed along the route (apex gates, drift zones, braking targets…).</summary>
    [Serializable]
    public sealed class RouteGateDef
    {
        public string Id;
        /// <summary>apex | drift-zone | brake-zone | exit-speed | precision | timing</summary>
        public string Kind;
        public float StartMetres;
        public float EndMetres;
        /// <summary>Intended line offset from the centreline (m, + = right) and its tolerance.</summary>
        public float LineOffset;
        public float LineTolerance = 2f;
        public float TargetSpeedKmh;
    }

    [Serializable]
    public sealed class RouteLandmarkDef
    {
        public string Id;
        public string Name;
        public float AtMetres;
        /// <summary>left | right</summary>
        public string Side;
        public float OffsetMetres;
        /// <summary>Kit prefab key used by the baker (e.g. tea-shed, stone-bridge, lantern-row).</summary>
        public string Kit;
    }

    /// <summary>
    /// Source geometry for one course (Assets/Content/Courses/&lt;ID&gt;/route.json). The baker turns this into
    /// meshes, terrain and a <see cref="TrackData"/> asset; the source stays the versioned truth.
    /// </summary>
    [Serializable]
    public sealed class RouteDefinition
    {
        public string Schema;
        public string Course;
        public int Revision = 1;
        public bool ClosedLoop;
        public float StartMetres = 30f;
        public float CheckpointSpacingMetres = 100f;
        public string Biome = "foothills";
        public List<RouteControlPoint> ControlPoints = new List<RouteControlPoint>();
        public List<RouteSectorDef> Sectors = new List<RouteSectorDef>();
        public List<RouteGateDef> Gates = new List<RouteGateDef>();
        public List<RouteLandmarkDef> Landmarks = new List<RouteLandmarkDef>();
    }

    /// <summary>One resampled point of the centreline.</summary>
    [Serializable]
    public struct TrackSample
    {
        public Vector3 Position;
        public Vector3 Tangent;
        /// <summary>Banked right vector across the road surface.</summary>
        public Vector3 Right;
        public float Distance;
        public float Width;
        public float ShoulderLeft;
        public float ShoulderRight;
        public float BankDeg;
        /// <summary>Signed curvature (1/m, + = turning right).</summary>
        public float Curvature;

        /// <summary>Road-surface normal (Unity is left-handed: forward × right = up).</summary>
        public Vector3 Up => Vector3.Cross(Tangent, Right).normalized;
    }
}
