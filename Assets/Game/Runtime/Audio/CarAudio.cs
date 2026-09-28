using System.Collections.Generic;
using NightSignal.Core.Content;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.GameAudio
{
    /// <summary>
    /// A race car's sound (spec §16): an <see cref="EngineAudio"/> fed from the car as it is drawn each frame — the local
    /// simulation's telemetry where there is one (your car, offline opponents), otherwise the replicated state (rpm,
    /// gear, boost, suspension). The driver's throttle is used when known, else it is estimated from the car's forward
    /// acceleration and engine speed. Only your car and the nearest others within range synthesize at once
    /// (<see cref="MaxAudible"/>), so a full grid costs what the measured six-car stack costs (docs/AUDIO.md).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CarAudio : MonoBehaviour
    {
        public const int MaxAudible = 6;
        public const float AudibleRange = 170f;

        static readonly List<CarAudio> all = new List<CarAudio>();
        static int budgetFrame = -1;
        static AudioListener listener;

        EngineAudio engine;
        bool audible = true;
        float estimate, lastRpm = -1f;
        uint lastTick;
        Vector3 lastVelocity;

        /// <summary>Your own car: always audible (the others compete for the remaining places by distance).</summary>
        public bool Own { get; private set; }
        /// <summary>The driver's throttle when the session knows it (your car); null = estimated.</summary>
        public float? Throttle;
        public bool Audible => audible;
        public EngineAudio Engine => engine;
        /// <summary>How many cars are synthesizing right now (evidence runs log it).</summary>
        public static int AudibleCount { get; private set; }

        /// <summary>Adds sound to a drawn race car (nothing in batch mode: servers and headless runs stay silent).</summary>
        public static CarAudio Attach(VehicleView view, VehicleParams p, string carId, bool own)
        {
            if (view == null || Application.isBatchMode) return null;
            var a = view.gameObject.AddComponent<CarAudio>();
            a.Own = own;
            a.engine = view.gameObject.AddComponent<EngineAudio>();
            a.engine.Configure(p, carId);
            view.Audio = a;
            return a;
        }

        void OnEnable() => all.Add(this);

        void OnDisable()
        {
            all.Remove(this);
            if (listener != null && all.Count == 0) listener = null;
        }

        /// <summary>From <see cref="VehicleView.Render"/>: this frame's drawn state and (if any) local telemetry.</summary>
        public void OnRender(in VehicleState previous, in VehicleState current, in StepTelemetry telemetry, float dt)
        {
            Budget();
            if (engine == null || !audible || dt <= 0f) return;

            // Forward acceleration from the latest distinct tick (replicated snapshots arrive every few ticks).
            if (current.Tick != lastTick)
            {
                uint ticks = lastTick == 0 || current.Tick < lastTick ? 1u : current.Tick - lastTick;
                Vector3 fwd = current.Rotation * Vector3.forward;
                float accel = Vector3.Dot(current.Velocity - lastVelocity, fwd) / (ticks * VehicleSimulation.TickDt);
                float rpmRate = lastRpm < 0f ? 0f : (current.EngineRpm - lastRpm) / (ticks * VehicleSimulation.TickDt);
                float target = current.Gear != 0 && current.ShiftTimer <= 0f ? Mathf.Clamp01(0.12f + accel / 4f) : 0.08f;
                if (rpmRate > 900f) target = Mathf.Max(target, 0.85f);  // revving (also held on the grid)
                if (accel < -3.5f) target = 0f;                          // braking
                estimate = Mathf.MoveTowards(estimate, target, 0.35f);
                lastTick = current.Tick;
                lastVelocity = current.Velocity;
                lastRpm = current.EngineRpm;
            }
            float throttle = Throttle ?? estimate;

            bool hasTelemetry = telemetry.GroundedWheels > 0 || telemetry.Airborne || telemetry.RoadSpeedMps > 0.01f;
            if (hasTelemetry)
            {
                engine.Feed(current, telemetry, throttle, dt);
                return;
            }
            int grounded = 0;
            for (int i = 0; i < 4; i++) if (current.GetCompression(i) > 0.001f) grounded++;
            bool shifting = current.ShiftTimer > 0f;
            float load = current.Gear != 0 && !shifting ? throttle : throttle * 0.15f;
            engine.SetInputs(current.EngineRpm, throttle, load, current.Boost, current.Gear, shifting, current.Velocity.magnitude,
                0f, SurfaceKind.Asphalt, grounded / 4f, 0f, 0f);
        }

        /// <summary>Once per frame: your car plus the nearest cars within range synthesize; the rest are silent.</summary>
        static void Budget()
        {
            if (budgetFrame == Time.frameCount) return;
            budgetFrame = Time.frameCount;
            if (listener == null || !listener.isActiveAndEnabled) listener = Object.FindAnyObjectByType<AudioListener>();
            Vector3 ear = listener != null ? listener.transform.position : Vector3.zero;
            all.Sort((a, b) =>
            {
                if (a.Own != b.Own) return a.Own ? -1 : 1;
                return (a.transform.position - ear).sqrMagnitude.CompareTo((b.transform.position - ear).sqrMagnitude);
            });
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                CarAudio c = all[i];
                bool on = c.Own || (n < MaxAudible && (c.transform.position - ear).sqrMagnitude < AudibleRange * AudibleRange);
                if (on) n++;
                if (on != c.audible)
                {
                    c.audible = on;
                    c.engine?.SetAudible(on);
                }
            }
            AudibleCount = n;
        }
    }

    /// <summary>Race music (spec §16): the event's cue at the countdown, the win/loss results variant at the finish.</summary>
    public static class RaceMusicPlayer
    {
        public static void Start(ContentCatalogue catalogue, string courseId, string kind, string stageId, bool hard, string trialKind = null, bool driftRanked = false)
        {
            if (Application.isBatchMode) return;
            string cue = RaceMusic.CueFor(catalogue, courseId, kind, stageId, hard, trialKind, driftRanked);
            MusicPlayer m = MusicPlayer.Ensure();
            if (m != null && m.Play(cue, 2.5f)) Debug.Log($"[NightSignal.Music] race cue {cue} ({kind} {stageId ?? courseId})");
        }

        public static void Results(bool win)
        {
            if (Application.isBatchMode) return;
            string cue = win ? RaceMusic.ResultsWin : RaceMusic.ResultsLoss;
            if (MusicPlayer.Ensure()?.Play(cue, 1.5f) == true) Debug.Log($"[NightSignal.Music] results cue {cue}");
        }
    }
}
