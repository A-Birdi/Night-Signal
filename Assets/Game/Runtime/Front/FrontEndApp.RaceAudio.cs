using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.GameAudio;
using NightSignal.Race;
using UnityEngine;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Race audio evidence (<c>-nsRaceAudioTour</c>): an offline S07 — the Mizuhana lieutenant encounter on C04 with its
        /// live opponents — driven by the validator autopilot. Checks the encounter theme starts at the countdown, that
        /// engines and music both reach the listener (each measured alone by muting the other's volume setting), that no
        /// more than <see cref="CarAudio.MaxAudible"/> cars synthesize at once and your car always does, and that the
        /// results cue follows the finish. Automation: measured levels, not a listening test.
        /// </summary>
        IEnumerator RaceAudioTour()
        {
            var failures = new List<string>();
            void Note(string n) => Debug.Log("[NightSignal.RaceAudioTour] " + n);
            void Fail(string f) { failures.Add(f); Note("FAIL " + f); }
            float[] buffer = new float[2048];
            float Level()
            {
                // The mixed output reaching the listener (both channels), as dBFS RMS.
                double sum = 0;
                for (int ch = 0; ch < 2; ch++)
                {
                    AudioListener.GetOutputData(buffer, ch);
                    foreach (float s in buffer) sum += s * s;
                }
                double rms = Math.Sqrt(sum / (buffer.Length * 2));
                return rms > 1e-7 ? (float)(20 * Math.Log10(rms)) : -140f;
            }
            IEnumerator Measure(Action<float> result, float seconds)
            {
                float peak = -140f, t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    yield return null;
                    peak = Mathf.Max(peak, Level());
                }
                result(peak);
            }

            yield return new WaitForSeconds(3f);
            ContentLibrary lib = ContentLibrary.Load();
            StageDef stage = lib.Catalogue.Stage("S07");
            StageBenchmark benchmark = StageBenchmarks.For(lib.Catalogue, stage, CampaignMode.Normal);
            RaceRoster roster = RosterPlanner.PlanCampaign(new[] { "local" }, stage.Normal.Opponents, stage.Id, CampaignMode.Normal);
            var rules = new RaceEventRules
            {
                Kind = "campaign", Mode = CampaignMode.Normal, StageId = stage.Id, StageNumber = stage.Number, CarCapPi = stage.MaxPI,
                Contact = ContactPolicy.LightContact, BenchmarkTargetMs = benchmark.TargetTimeMs, HardTimeoutMs = benchmark.HardTimeoutMs,
                RequiresBeatingFeaturedRival = benchmark.RequiresBeatingFeaturedRival,
            };
            List<string> ai = roster.Entries.Where(e => e.Kind == ActorKind.Ai).Select(e => e.DriverId).ToList();
            bool over = false;
            List<RaceEntrantResult> results = null;
            string endCue = null;
            StartCoroutine(RunOfflineRace(stage.Course, "V01", rules, ai, false, (r, rev) => { results = r; over = true; }));
            float until = Time.realtimeSinceStartup + 60f;
            while ((activeRace == null || activeRace.Phase != MatchPhase.Countdown) && Time.realtimeSinceStartup < until) yield return null;
            if (activeRace == null) { Fail("the race did not start"); Finish(); yield break; }
            activeRace.Autopilot = true;
            yield return new WaitForSeconds(0.5f);
            string cue = MusicPlayer.Instance?.CurrentCue;
            Note($"countdown: music {cue}; cars {activeRace.Sim.Entrants.Count}");
            if (cue != "MUS_LT_DAIGO") Fail($"the encounter theme did not start at the countdown ({cue})");

            until = Time.realtimeSinceStartup + 30f;
            while (activeRace != null && activeRace.Phase != MatchPhase.Racing && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSeconds(6f);
            CarAudio own = activeRace?.PlayerView?.Audio;
            float both = -140f, enginesOnly = -140f, musicOnly = -140f;
            yield return Measure(v => both = v, 2f);
            float music = GameAudioSettings.Music, engine = GameAudioSettings.Engine, tyres = GameAudioSettings.Tyres;
            GameAudioSettings.Music = 0f;
            yield return new WaitForSeconds(0.8f);
            yield return Measure(v => enginesOnly = v, 2f);
            GameAudioSettings.Music = music;
            GameAudioSettings.Engine = 0f;
            GameAudioSettings.Tyres = 0f;
            yield return new WaitForSeconds(0.8f);
            yield return Measure(v => musicOnly = v, 2f);
            GameAudioSettings.Engine = engine;
            GameAudioSettings.Tyres = tyres;
            int audible = CarAudio.AudibleCount;
            Note($"racing: listener peak RMS {both:F1} dBFS (music + cars), cars alone {enginesOnly:F1}, music alone {musicOnly:F1}; " +
                 $"cars synthesizing {audible} of {activeRace?.Sim.Entrants.Count}; own car audible {own?.Audible}; own rpm {activeRace?.Player.State.EngineRpm:F0}");
            if (own == null || !own.Audible) Fail("your car has no sound");
            if (audible < 1 || audible > CarAudio.MaxAudible) Fail($"{audible} cars synthesizing (limit {CarAudio.MaxAudible})");
            if (enginesOnly < -50f) Fail($"no engine sound reaches the listener ({enginesOnly:F1} dBFS)");
            if (musicOnly < -50f) Fail($"no music reaches the listener ({musicOnly:F1} dBFS)");

            // Mid-race samples until the finish.
            float nextSample = Time.realtimeSinceStartup + 20f;
            until = Time.realtimeSinceStartup + 420f;
            while (!over && Time.realtimeSinceStartup < until)
            {
                if (activeRace != null && activeRace.Phase == MatchPhase.Results && endCue == null)
                {
                    yield return new WaitForSeconds(1f);
                    endCue = MusicPlayer.Instance?.CurrentCue;
                }
                if (Time.realtimeSinceStartup >= nextSample && activeRace != null && activeRace.Phase == MatchPhase.Racing)
                {
                    nextSample = Time.realtimeSinceStartup + 20f;
                    float l = Level();
                    Note($"t {activeRace.CurrentTick / 60f:F0} s: level {l:F1} dBFS, cars synthesizing {CarAudio.AudibleCount}, music {MusicPlayer.Instance?.CurrentCue}");
                    if (CarAudio.AudibleCount > CarAudio.MaxAudible) Fail("audio budget exceeded");
                }
                yield return null;
            }
            RaceEntrantResult mine = results?.FirstOrDefault(r => r.Entrant.Roster.Index == 0);
            Note($"finish: {(mine == null ? "no result" : $"{mine.Outcome} P{mine.Placement}")}; results music {endCue}");
            if (!over) Fail("the race did not finish in time");
            else if (endCue != RaceMusic.ResultsWin && endCue != RaceMusic.ResultsLoss) Fail($"no results cue at the finish ({endCue})");
            else if (mine != null && (endCue == RaceMusic.ResultsWin) != (mine.Outcome == RunOutcome.Finished && mine.Placement == 1))
                Fail($"results cue {endCue} does not match P{mine.Placement}");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
