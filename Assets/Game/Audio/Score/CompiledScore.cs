using System;
using System.Collections.Generic;

namespace NightSignal.AudioSynth
{
    /// <summary>One note (or drum hit) on the flattened score timeline. For drum tracks, Pitch is the DrumPiece.</summary>
    public struct ScoreEvent
    {
        public const byte FlagGlide = 1;
        public int Tick;
        public int Duration;
        public short Track;
        public byte Flags;
        public float Pitch;
        public float Velocity;
    }

    public sealed class SectionMark
    {
        public string Name;
        public int StartTick;
        public int Bars;
        public bool Looping;
    }

    /// <summary>Per-cue mix: master level, shared reverb/delay buses, sidechain release and limiter ceiling.</summary>
    public sealed class MixSpec
    {
        public float MasterDb = -3f;
        public float ReverbSize = 0.72f;
        public float ReverbDamp = 0.45f;
        public float ReverbWidth = 1f;
        public float ReverbWet = 0.28f;
        public float DelayBeats = 0.75f;
        public float DelayFeedback = 0.35f;
        public float DelayDampHz = 3500f;
        public float DelayWet = 0.7f;
        public float DelayToReverb = 0.3f;
        public float DuckReleaseBeats = 0.45f;
        public float CeilingDb = -1f;
    }

    /// <summary>
    /// A score compiled to a flat, sorted event timeline: intro sections play once, then
    /// [LoopStartTick, EndTick) repeats seamlessly. Immutable once built; safe to share between players.
    /// </summary>
    public sealed class CompiledScore
    {
        public const int Ppq = 96;

        public string Id = "";
        public string Title = "";
        public string Description = "";
        public string Key = "";
        public string Style = "";
        public string[] Motifs = new string[0];
        public float Tempo = 120f;
        public int BeatsPerBar = 4;
        public int BeatUnit = 4;
        public int StepsPerBeat = 4;
        public int StepsPerBar = 16;
        public int TicksPerStep = 24;
        public int TicksPerBar = 384;
        public TrackSpec[] Tracks = new TrackSpec[0];
        public ScoreEvent[] Events = new ScoreEvent[0];
        public SectionMark[] Sections = new SectionMark[0];
        public int LoopStartTick;
        public int EndTick;
        public int LoopStartEvent;
        public MixSpec Mix = new MixSpec();
        public List<string> Warnings = new List<string>();

        public double SecondsPerTick => 60.0 / (Tempo * Ppq);
        public double IntroSeconds => LoopStartTick * SecondsPerTick;
        public double LoopSeconds => (EndTick - LoopStartTick) * SecondsPerTick;
        public double TotalSeconds => EndTick * SecondsPerTick;
        public int TotalBars => TicksPerBar > 0 ? EndTick / TicksPerBar : 0;

        public string FormSummary()
        {
            var parts = new List<string>();
            foreach (var s in Sections) parts.Add((s.Looping ? "" : "(") + s.Name + ":" + s.Bars + (s.Looping ? "" : ")"));
            return string.Join(" ", parts.ToArray());
        }
    }

    public sealed class ScoreException : Exception
    {
        public readonly IList<string> Errors;

        public ScoreException(IList<string> errors) : base(Summarise(errors)) { Errors = errors; }

        static string Summarise(IList<string> errors)
        {
            if (errors == null || errors.Count == 0) return "score error";
            var n = Math.Min(errors.Count, 12);
            var lines = new string[n];
            for (int i = 0; i < n; i++) lines[i] = errors[i];
            return string.Join("\n", lines) + (errors.Count > n ? "\n(+" + (errors.Count - n) + " more)" : "");
        }
    }
}
