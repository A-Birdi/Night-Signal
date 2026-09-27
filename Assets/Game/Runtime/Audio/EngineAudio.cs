using NightSignal.AudioSynth;
using NightSignal.Vehicle;
using UnityEngine;
using UnityEngine.Audio;

namespace NightSignal.GameAudio
{
    /// <summary>
    /// Per-car procedural sound (spec §16): engine by architecture/rpm/load with turbo, shifts, limiter and
    /// overrun; tyres by slip and surface; kerbs, wall scrape, wind; suspension thumps and collision severity.
    /// Creates three spatialised child AudioSources (engine / road / impacts) so each is an independent bus.
    /// Call <see cref="Configure(VehicleParams, string)"/> once, then <see cref="Feed"/> every frame (or
    /// <see cref="SetInputs"/> for cars without a local simulation, e.g. interpolated remote cars).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EngineAudio : MonoBehaviour
    {
        /// <summary>Minimum seconds between meet rev blips (spec §16: limit rev spam at meets).</summary>
        public const float MeetRevCooldown = 3f;

        [SerializeField] AudioMixerGroup engineGroup;
        [SerializeField] AudioMixerGroup roadGroup;
        [SerializeField] AudioMixerGroup impactGroup;
        [SerializeField, Range(0f, 1f)] float spatialBlend = 1f;
        [SerializeField, Min(0.1f)] float minDistance = 6f;
        [SerializeField, Min(1f)] float maxDistance = 220f;
        [SerializeField, Range(0f, 1f)] float localVolume = 1f;

        VehicleSoundModel model;
        readonly VehicleBusFilter[] filters = new VehicleBusFilter[3];
        readonly float[] lastCompression = new float[4];
        bool haveCompression;
        bool turbo;
        float lastImpactTime = -10f, lastThumpTime = -10f, lastRevTime = -10f;

        public VehicleSoundModel Model => model;

        public void Configure(VehicleParams p, string carId)
        {
            Configure(p.Engine, p.IdleRpm, p.RedlineRpm, p.Turbocharged, Rng.Hash(carId ?? name));
        }

        public void Configure(EngineFamily family, float idleRpm, float redlineRpm, bool turbocharged, uint seed)
        {
            turbo = turbocharged;
            var cfg = new EngineConfig
            {
                Family = (EngineFamilyKind)(byte)family,
                IdleRpm = idleRpm,
                RedlineRpm = redlineRpm,
                Turbo = turbocharged,
                // Six-cylinder turbos vent through a valve; the others flutter. A per-car choice could come from data later.
                BlowOff = family == EngineFamily.Six ? BlowOffStyle.Valve : BlowOffStyle.Flutter,
                Seed = seed,
            };
            SynthMath.Warm();
            var m = new VehicleSoundModel(cfg, ProceduralAudio.SampleRate);
            EnsureSources();
            for (int i = 0; i < 3; i++) filters[i].Model = m;
            model = m;
            haveCompression = false;
            ApplyGains();
        }

        void EnsureSources()
        {
            if (filters[0] != null) return;
            var groups = new[] { engineGroup, roadGroup, impactGroup };
            var names = new[] { "EngineBus", "RoadBus", "ImpactBus" };
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject(names[i]);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.outputAudioMixerGroup = groups[i];
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.minDistance = minDistance;
                src.maxDistance = maxDistance;
                var f = go.AddComponent<VehicleBusFilter>();
                f.Bus = (VehicleBus)i;
                ProceduralAudio.Prepare(src, spatialBlend);
                filters[i] = f;
            }
        }

        void Update() => ApplyGains();

        void ApplyGains()
        {
            var m = model;
            if (m == null) return;
            float master = GameAudioSettings.ToGain(GameAudioSettings.Master) * localVolume;
            m.EngineGain = master * GameAudioSettings.ToGain(GameAudioSettings.Engine);
            m.TyreGain = master * GameAudioSettings.ToGain(GameAudioSettings.Tyres);
            m.ImpactGain = master * GameAudioSettings.ToGain(GameAudioSettings.Impacts);
            m.WindGain = master * GameAudioSettings.ToGain(GameAudioSettings.Ambient);
        }

        /// <summary>Raw inputs for cars without local telemetry (remote/interpolated cars, previews).</summary>
        public void SetInputs(float rpm, float throttle, float load, float boost, int gear, bool shifting,
                              float speedMps, float slip01, SurfaceKind surface, float grounded01, float kerb01, float scrape01)
        {
            var m = model;
            if (m == null) return;
            m.SetDrive(rpm, throttle, load, turbo ? boost : 0f, gear, shifting);
            m.SetRoad(speedMps, slip01, (TyreSurface)(byte)surface, grounded01, kerb01, scrape01);
        }

        /// <summary>
        /// Derives every audio input from the authoritative/predicted chassis state and the latest step telemetry.
        /// <paramref name="throttle"/> is the driver's (quantized) throttle; <paramref name="dt"/> the frame time.
        /// </summary>
        public void Feed(in VehicleState state, in StepTelemetry telemetry, float throttle, float dt)
        {
            var m = model;
            if (m == null) return;
            float now = Time.time;

            int grounded = 0, kerb = 0, loose = 0, grass = 0, concrete = 0;
            float maxSlip = 0f;
            for (int i = 0; i < 4; i++)
            {
                var w = telemetry.Wheel(i);
                if (haveCompression && dt > 1e-4f)
                {
                    float dc = (w.Compression - lastCompression[i]) / dt;
                    if (dc > 1.2f && now - lastThumpTime > 0.12f)
                    {
                        m.Thump(Mathf.Clamp01((dc - 1.2f) / 4f));
                        lastThumpTime = now;
                    }
                }
                lastCompression[i] = w.Compression;
                if (!w.Grounded) continue;
                grounded++;
                float s = Mathf.Abs(w.SlipAngleDeg);
                if (w.Sliding) s = Mathf.Max(s, 12f);
                if (s > maxSlip) maxSlip = s;
                switch (w.Surface)
                {
                    case SurfaceKind.Kerb: kerb++; break;
                    case SurfaceKind.Shoulder: loose++; break;
                    case SurfaceKind.Grass: loose++; grass++; break;
                    case SurfaceKind.Concrete: concrete++; break;
                }
            }
            haveCompression = true;

            float slip = Mathf.InverseLerp(4f, 30f, maxSlip);
            slip = Mathf.Max(slip, Mathf.InverseLerp(5f, 40f, Mathf.Abs(telemetry.BodySlipDeg)));
            if (telemetry.Wheelspin) slip = Mathf.Max(slip, 0.6f);

            SurfaceKind surface = SurfaceKind.Asphalt;
            if (loose >= 2) surface = grass * 2 >= loose ? SurfaceKind.Grass : SurfaceKind.Shoulder;
            else if (concrete >= 2) surface = SurfaceKind.Concrete;

            float speed = state.Velocity.magnitude;
            float scrape = telemetry.WallContact ? Mathf.Clamp01(speed / 20f) : 0f;
            if (telemetry.WallContact && telemetry.WallImpactSpeed > 1.5f && now - lastImpactTime > 0.25f)
            {
                m.Impact(Mathf.Clamp01(telemetry.WallImpactSpeed / 14f));
                lastImpactTime = now;
            }

            bool shifting = state.ShiftTimer > 0f;
            float load = state.Gear != 0 && !shifting ? throttle : throttle * 0.15f;
            m.SetDrive(state.EngineRpm, throttle, load, turbo ? state.Boost : 0f, state.Gear, shifting);
            m.SetRoad(speed, slip, (TyreSurface)(byte)surface, grounded / 4f, Mathf.Clamp01(kerb / 2f), scrape);
        }

        public void Impact(float severity) => model?.Impact(severity);

        public void Thump(float amount) => model?.Thump(amount);

        /// <summary>Meet rev blip; rate-limited so repeated presses cannot spam the room.</summary>
        public bool TryMeetRev()
        {
            if (model == null || Time.time - lastRevTime < MeetRevCooldown) return false;
            lastRevTime = Time.time;
            model.RevBlip();
            return true;
        }

        void OnDestroy()
        {
            for (int i = 0; i < 3; i++) if (filters[i] != null) filters[i].Model = null;
        }
    }
}
