using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Core.Ghosts;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>
    /// Records one car as a ghost (spec §8): its simulated transform ten times a second from the start, the race time at each
    /// checkpoint, and at the finish the result, resets and legality that decide whether it can ever be a target.
    /// </summary>
    public sealed class GhostRecorder
    {
        readonly GhostRecording ghost = new GhostRecording();
        int checkpoints;
        bool done;

        public GhostRecorder(GhostHeader header) => ghost.Header = header;

        static int Every => Mathf.Max(1, VehicleSimulation.TickRate / GhostRecording.SampleHz);

        /// <summary>After each racing tick.</summary>
        public void Step(RaceEntrant e, long raceMicros, int tick)
        {
            if (done || e == null) return;
            while (checkpoints < e.Progress.CheckpointsPassed)
            {
                ghost.CheckpointMicros.Add(e.Progress.Finished && checkpoints == e.Progress.CheckpointsPassed - 1 ? e.Progress.FinishTimeMicros : raceMicros);
                checkpoints++;
            }
            if (e.Progress.Finished)
            {
                Sample(e.State, e.Progress.FinishTimeMicros / 1e6f);
                done = true;
                return;
            }
            if (tick % Every == 0) Sample(e.State, raceMicros / 1e6f);
        }

        void Sample(in VehicleState s, float t)
        {
            if (ghost.Count > 0 && t <= ghost.T[ghost.Count - 1]) return;
            Quaternion q = s.Rotation;
            ghost.Add(t, s.Position.x, s.Position.y, s.Position.z, q.x, q.y, q.z, q.w, s.Velocity.magnitude);
        }

        /// <summary>The recording with the run's verdict in its header (a DNF has no result and is never a target).</summary>
        public GhostRecording Finish(EntrantProgress p)
        {
            done = true;
            ghost.Header.ResultMicros = p.Finished ? p.FinishTimeMicros : 0;
            ghost.Header.Resets = p.Resets;
            ghost.Header.CorridorCut = p.CorridorCut;
            return ghost;
        }
    }

    /// <summary>
    /// A ghost on the road: a translucent, non-colliding copy of the recorded car placed by interpolating the recording at the
    /// race time (never simulated). It occupies no slot and cannot touch anything.
    /// </summary>
    public sealed class GhostPlayback
    {
        public readonly GhostRecording Recording;
        public readonly VehicleView View;
        public readonly string Label;
        VehicleState last;
        bool hasLast;

        static readonly Dictionary<Color, Material> tinted = new Dictionary<Color, Material>();

        /// <summary>The ghost paint, or a copy of it in <paramref name="tint"/> (the paint's transparency kept; one per colour).</summary>
        static Material Paint(CarMaterialSet mats, Color? tint)
        {
            if (mats?.GhostPaint == null || tint == null) return mats?.GhostPaint;
            if (tinted.TryGetValue(tint.Value, out Material m) && m != null) return m;
            m = new Material(mats.GhostPaint) { name = "GhostPaint (tinted)" };
            Color c = tint.Value;
            m.SetColor("_BaseColor", new Color(c.r, c.g, c.b, mats.GhostPaint.GetColor("_BaseColor").a));
            tinted[tint.Value] = m;
            return m;
        }

        /// <param name="tint">Overlay colour (null = the standard cyan ghost paint): tells whose ghost it is at a glance.</param>
        public GhostPlayback(GhostRecording recording, VehicleView view, string label, Color? tint = null)
        {
            Recording = recording;
            View = view;
            Label = label;
            Material paint = Paint(Resources.Load<CarMaterialSet>("CarMaterialSet"), tint);
            foreach (Renderer r in view.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (paint != null)
                {
                    var shared = new Material[r.sharedMaterials.Length];
                    for (int i = 0; i < shared.Length; i++) shared[i] = paint;
                    r.sharedMaterials = shared;
                }
            }
            foreach (Collider c in view.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        }

        /// <summary>The ghost's state at race time <paramref name="t"/> (seconds): interpolated position, rotation and speed.</summary>
        public VehicleState At(float t)
        {
            GhostRecording g = Recording;
            int i = g.IndexAt(t), j = Mathf.Min(i + 1, g.Count - 1);
            float a = j > i ? Mathf.Clamp01((t - g.T[i]) / (g.T[j] - g.T[i])) : 0f;
            Vector3 p = Vector3.Lerp(new Vector3(g.Px[i], g.Py[i], g.Pz[i]), new Vector3(g.Px[j], g.Py[j], g.Pz[j]), a);
            Quaternion q = Quaternion.Slerp(new Quaternion(g.Qx[i], g.Qy[i], g.Qz[i], g.Qw[i]), new Quaternion(g.Qx[j], g.Qy[j], g.Qz[j], g.Qw[j]), a);
            VehicleState s = VehicleState.AtRest(p, q);
            s.Velocity = (q * Vector3.forward) * Mathf.Lerp(g.Speed[i], g.Speed[j], a);
            return s;
        }

        /// <summary>Places the ghost at race time <paramref name="t"/>; hidden before the start and after its finish.</summary>
        public void Show(float t, float dt)
        {
            bool on = t >= 0f && t <= Recording.T[Recording.Count - 1] + 0.5f;
            if (View.gameObject.activeSelf != on) View.gameObject.SetActive(on);
            if (!on) return;
            VehicleState s = At(t);
            View.Render(hasLast ? last : s, s, 1f, default, dt);
            last = s;
            hasLast = true;
        }
    }
}
