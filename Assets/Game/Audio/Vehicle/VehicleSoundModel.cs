using System;
using System.Threading;

namespace NightSignal.AudioSynth
{
    /// <summary>Independent vehicle mix buses; each can be rendered by its own AudioSource / mixer group.</summary>
    public enum VehicleBus : byte { Engine = 0, Road = 1, Impacts = 2 }

    /// <summary>
    /// Complete sound of one car, split into three independent buses — engine (incl. turbo, shifts, limiter,
    /// crackle), road (tyres, kerbs, scrape, wind) and impacts — each with its own gain and look-ahead limiter.
    ///
    /// Threading: the game thread writes inputs every frame through <see cref="SetDrive"/>/<see cref="SetRoad"/>
    /// (plain float fields; a torn frame is harmless because the audio side smooths everything) and queues
    /// one-shots with <see cref="Impact"/>/<see cref="Thump"/>/<see cref="RevBlip"/> through a lock-free ring.
    /// The audio thread calls <see cref="RenderBus"/> (one call per bus, possibly from different sources).
    /// Nothing allocates after construction.
    /// </summary>
    public sealed class VehicleSoundModel
    {
        const int Block = 64;

        struct OneShot
        {
            public byte Kind; // 1 collide, 2 thump
            public float Amount;
        }

        readonly EngineVoice engine;
        readonly RoadVoice road;
        readonly ImpactBank impacts;
        readonly Limiter[] limiters = new Limiter[3];
        readonly float[][] buffers = new float[3][];
        readonly int[] bufferPos = { Block, Block, Block };
        readonly SpscRing<OneShot> oneShots = new SpscRing<OneShot>(32);
        readonly int sampleRate;

        // Inputs (game thread -> audio thread).
        float rpm, throttle, load, boost, speed, slip, grounded = 1f, kerb, scrape;
        int gear = 1;
        bool shifting;
        TyreSurface surface;
        int revRequests, revServed;

        /// <summary>Bus gains (0..1) — engine, tyres, wind and impacts are independently controllable.</summary>
        public float EngineGain = 1f;
        public float TyreGain = 1f;
        public float WindGain = 1f;
        public float ImpactGain = 1f;

        public EngineConfig Config { get; }
        public int SampleRate => sampleRate;

        public VehicleSoundModel(EngineConfig config, int sampleRate)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            SynthMath.Warm();
            Config = config;
            this.sampleRate = sampleRate;
            engine = new EngineVoice(config, sampleRate);
            road = new RoadVoice(sampleRate, config.Seed + 17u);
            impacts = new ImpactBank(sampleRate, config.Seed + 31u);
            for (int i = 0; i < 3; i++)
            {
                limiters[i] = new Limiter(sampleRate, -1f, 1.5f, 120f);
                buffers[i] = new float[Block];
            }
            rpm = config.IdleRpm;
        }

        public void SetDrive(float engineRpm, float throttle01, float load01, float boost01, int gearIndex, bool shiftInProgress)
        {
            rpm = engineRpm;
            throttle = throttle01;
            load = load01;
            boost = boost01;
            gear = gearIndex;
            shifting = shiftInProgress;
        }

        public void SetRoad(float speedMps, float slip01, TyreSurface tyreSurface, float grounded01, float kerb01, float scrape01)
        {
            speed = speedMps;
            slip = slip01;
            surface = tyreSurface;
            grounded = grounded01;
            kerb = kerb01;
            scrape = scrape01;
        }

        public void Impact(float severity) => oneShots.TryEnqueue(new OneShot { Kind = 1, Amount = severity });

        public void Thump(float amount) => oneShots.TryEnqueue(new OneShot { Kind = 2, Amount = amount });

        /// <summary>Parked free-rev blip (meet). Callers rate-limit it; see EngineAudio.TryMeetRev.</summary>
        public void RevBlip() => Interlocked.Increment(ref revRequests);

        void Fill(VehicleBus bus)
        {
            var buf = buffers[(int)bus];
            Array.Clear(buf, 0, Block);
            switch (bus)
            {
                case VehicleBus.Engine:
                    if (Volatile.Read(ref revRequests) != revServed) { revServed = Volatile.Read(ref revRequests); engine.RevBlip(); }
                    engine.SetInputs(rpm, throttle, load, boost, gear, shifting);
                    engine.Render(buf, 0, Block, 1f);
                    break;
                case VehicleBus.Road:
                    road.SetInputs(speed, slip, surface, grounded, kerb, scrape);
                    road.Render(buf, 0, Block, TyreGain, WindGain); // two gains inside one bus: tyres and wind
                    break;
                default:
                    OneShot o;
                    while (oneShots.TryDequeue(out o))
                    {
                        if (o.Kind == 1) impacts.Collide(o.Amount);
                        else impacts.Thump(o.Amount);
                    }
                    impacts.Render(buf, 0, Block, 1f);
                    break;
            }
            limiters[(int)bus].ProcessMono(buf, Block);
        }

        float BusGain(VehicleBus bus)
        {
            switch (bus)
            {
                case VehicleBus.Engine: return EngineGain;
                case VehicleBus.Road: return 1f; // applied per layer in Fill
                default: return ImpactGain;
            }
        }

        /// <summary>
        /// Renders one bus into interleaved <paramref name="data"/>. With <paramref name="multiply"/> the samples
        /// multiply what is already there (Unity: a spatialised constant-1 clip carries distance attenuation and
        /// panning), otherwise they overwrite every channel.
        /// </summary>
        public void RenderBus(VehicleBus bus, float[] data, int frames, int channels, bool multiply)
        {
            int b = (int)bus;
            var buf = buffers[b];
            float g = BusGain(bus);
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                if (bufferPos[b] >= Block) { Fill(bus); bufferPos[b] = 0; }
                float v = buf[bufferPos[b]++] * g;
                if (multiply) for (int c = 0; c < channels; c++) data[idx++] *= v;
                else for (int c = 0; c < channels; c++) data[idx++] = v;
            }
        }

        /// <summary>Mono render of one bus (tools/tests). Writes <paramref name="frames"/> samples.</summary>
        public void RenderBusMono(VehicleBus bus, float[] mono, int offset, int frames)
        {
            int b = (int)bus;
            var buf = buffers[b];
            float g = BusGain(bus);
            for (int f = 0; f < frames; f++)
            {
                if (bufferPos[b] >= Block) { Fill(bus); bufferPos[b] = 0; }
                mono[offset + f] = buf[bufferPos[b]++] * g;
            }
        }
    }
}
