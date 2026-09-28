using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NightSignal.Track;
using UnityEngine;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>
        /// Crew behaviour evidence (<c>-nsCrewTelemetryTour</c>, spec §13: "at least four demonstrably different behavioural
        /// outcomes must be measured per crew on shared benchmark corners. Show telemetry evidence"): for each of the six
        /// crews, its members race C01 together as non-colliding calibration ghosts, every one in the same V01 (so the car
        /// is not the difference). At the course's three sharpest corners each driver's trace gives: where braking began
        /// before the apex, the minimum and exit speeds, the throttle-on point after it, and the line at turn-in and apex.
        /// A crew passes when at least four of those six measures differ across its members by more than noise. The tour
        /// also reports whether a late braker brakes later and an exit specialist trades entry for exit. Automation.
        /// </summary>
        IEnumerator CrewTelemetryTour()
        {
            var failures = new List<string>();
            var report = new StringBuilder();
            void Note(string n) => Debug.Log("[NightSignal.CrewTelemetry] " + n);
            ContentCatalogue cat = NightSignal.Content.ContentLibrary.Load().Catalogue;
            var crews = cat.Rivals.Where(r => !string.IsNullOrEmpty(r.Crew) && r.Role != "normal-final" && r.Role != "hard-final")
                .GroupBy(r => r.Crew).OrderBy(g => g.Key).ToList();
            report.AppendLine("# Crew behaviour telemetry (spec §13): each crew's members on C01 as non-colliding calibration ghosts, all in V01;");
            report.AppendLine("# measured at the three sharpest corners (means over the corners). brake = metres before the apex where braking began;");
            report.AppendLine("# vmin / exit = minimum and apex+60 m speed (km/h); throttle = metres after the apex where speed rose 1 m/s over the minimum;");
            report.AppendLine("# turn-in / apex = lateral line (m, + right). Noise thresholds for a distinct outcome: brake 8 m, vmin 2 km/h, exit 2 km/h,");
            report.AppendLine("# throttle 8 m, lines 0.5 m. Validator autopilot drives the player car (ignored). Automation, not a person.");
            yield return new WaitForSeconds(3f);
            foreach (IGrouping<string, RivalDef> crew in crews)
            {
                List<string> members = crew.Select(r => r.Id).Take(Limits.MaxRaceVehicles - 1).ToList();
                var rules = new RaceEventRules
                {
                    Kind = "freeplay", Contact = ContactPolicy.NonContact, CalibrationGhosts = true, CalibrationCarId = "V01", StageNumber = 15,
                };
                var traces = new Dictionary<RaceEntrant, (float[] Speed, float[] Lateral, int Last)>();
                bool done = false;
                StartCoroutine(RunOfflineRace("C01", "V01", rules, members, false, (r, rev) => done = true));
                float until = Time.realtimeSinceStartup + 60f;
                while ((activeRace == null || activeRace.Sim == null) && Time.realtimeSinceStartup < until) yield return null;
                if (activeRace?.Sim == null) { failures.Add($"{crew.Key}: the race did not start"); continue; }
                OfflineRaceSession race = activeRace;
                TrackData track = race.Sim.Track;
                int length = Mathf.FloorToInt(track.LengthMetres);
                race.TickObserver = (sim, tick) =>
                {
                    foreach (RaceEntrant e in sim.Entrants)
                    {
                        if (e.Human || e.Status != EntrantStatus.Racing) continue;
                        if (!traces.TryGetValue(e, out var t)) t = (new float[length + 1], new float[length + 1], -1);
                        int bin = Mathf.FloorToInt(e.Progress.RaceDistance);
                        if (bin > t.Last && bin >= 0)
                        {
                            float v = e.State.Velocity.magnitude, lat = e.Progress.Location.Lateral;
                            for (int b = Mathf.Max(0, t.Last + 1); b <= Mathf.Min(bin, length); b++) { t.Speed[b] = v; t.Lateral[b] = lat; }
                            t.Last = Mathf.Min(bin, length);
                        }
                        traces[e] = t;
                    }
                };
                race.Autopilot = true;
                race.SimulationSpeed = 12;
                until = Time.realtimeSinceStartup + 600f;
                while (!done && Time.realtimeSinceStartup < until) yield return null;

                // The three sharpest corners (heading change over 30 m), 200 m apart, away from the start and finish.
                var curvature = new List<(int D, float Turn)>();
                for (int d = 200; d < length - 200; d += 5)
                    curvature.Add((d, Vector3.Angle(track.SampleAt(d - 15).Tangent, track.SampleAt(d + 15).Tangent)));
                var corners = new List<int>();
                foreach (var c in curvature.OrderByDescending(x => x.Turn))
                    if (corners.All(k => Mathf.Abs(k - c.D) >= 200)) { corners.Add(c.D); if (corners.Count == 3) break; }
                corners.Sort();

                var rows = new List<(RivalDef R, float Brake, float Vmin, float Exit, float Throttle, float TurnIn, float Apex)>();
                foreach (KeyValuePair<RaceEntrant, (float[] Speed, float[] Lateral, int Last)> kv in traces)
                {
                    RivalDef rival = cat.Rivals.First(x => x.Id == kv.Key.Roster.EntrantId);
                    float[] s = kv.Value.Speed, l = kv.Value.Lateral;
                    if (kv.Value.Last < corners.Last() + 80) continue; // did not get through every corner
                    float brake = 0, vmin = 0, exit = 0, thr = 0, turn = 0, apex = 0;
                    foreach (int a in corners)
                    {
                        int lo = a - 40, hi = a + 40, minAt = lo;
                        for (int d = lo; d <= hi; d++) if (s[d] < s[minAt]) minAt = d;
                        int maxAt = a - 250;
                        for (int d = a - 250; d <= minAt; d++) if (s[d] > s[maxAt]) maxAt = d;
                        int on = minAt;
                        while (on < a + 150 && s[on] < s[minAt] + 1f) on++;
                        brake += a - maxAt; vmin += s[minAt] * 3.6f; exit += s[Mathf.Min(a + 60, length)] * 3.6f; thr += on - a;
                        turn += l[a - 40]; apex += l[a];
                    }
                    int n = corners.Count;
                    rows.Add((rival, brake / n, vmin / n, exit / n, thr / n, turn / n, apex / n));
                }
                report.AppendLine();
                report.AppendLine($"## {crew.Key}: corners at {string.Join(", ", corners)} m of {length} m; {rows.Count} of {members.Count} members measured");
                report.AppendLine("rival  tendency                  brake m  vmin km/h  exit km/h  throttle m  turn-in m  apex m");
                foreach (var r in rows.OrderBy(x => x.R.Id))
                    report.AppendLine($"{r.R.Id}    {r.R.Tendency,-24}  {r.Brake,7:F1}  {r.Vmin,9:F1}  {r.Exit,9:F1}  {r.Throttle,10:F1}  {r.TurnIn,9:F2}  {r.Apex,6:F2}");
                if (rows.Count < 2) { failures.Add($"{crew.Key}: too few members measured"); continue; }
                float Spread(Func<(RivalDef R, float Brake, float Vmin, float Exit, float Throttle, float TurnIn, float Apex), float> f) => rows.Max(f) - rows.Min(f);
                var measures = new List<(string Name, float Spread, float Noise)>
                {
                    ("brake point", Spread(x => x.Brake), 8f), ("minimum speed", Spread(x => x.Vmin), 2f), ("exit speed", Spread(x => x.Exit), 2f),
                    ("throttle-on", Spread(x => x.Throttle), 8f), ("turn-in line", Spread(x => x.TurnIn), 0.5f), ("apex line", Spread(x => x.Apex), 0.5f),
                };
                int distinct = measures.Count(m => m.Spread > m.Noise);
                report.AppendLine("spreads: " + string.Join("; ", measures.Select(m => $"{m.Name} {m.Spread:F1}{(m.Spread > m.Noise ? " ✓" : "")}")) + $" → {distinct} distinct outcomes");
                // The spec's named expectations, reported as observed or not (never forced).
                float medianBrake = Median(rows.Select(x => x.Brake)), medianVmin = Median(rows.Select(x => x.Vmin)), medianGain = Median(rows.Select(x => x.Exit - x.Vmin));
                foreach (var r in rows.Where(x => x.R.Tendency == "late-brake-anchor"))
                    report.AppendLine($"expectation: late braker {r.R.Id} brakes later than the crew median ({r.Brake:F1} m vs {medianBrake:F1} m): {(r.Brake < medianBrake ? "observed" : "NOT observed")}");
                foreach (var r in rows.Where(x => x.R.Tendency == "exit-traction-specialist"))
                    report.AppendLine($"expectation: exit specialist {r.R.Id} trades entry for exit (vmin {r.Vmin:F1} vs median {medianVmin:F1}; gain {r.Exit - r.Vmin:F1} vs {medianGain:F1}): " +
                                      $"{(r.Vmin <= medianVmin && r.Exit - r.Vmin >= medianGain ? "observed" : "NOT observed")}");
                Note($"{crew.Key}: {rows.Count} measured, {distinct} distinct outcomes");
                if (distinct < 4) failures.Add($"{crew.Key}: only {distinct} distinct outcomes");
                yield return new WaitForSeconds(2f);
            }
            report.AppendLine();
            report.AppendLine(failures.Count == 0 ? "# PASS: every crew shows at least four distinct measured outcomes." : "# FAILED: " + string.Join("; ", failures));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine("Builds", "Screenshots", "crew-telemetry"));
            System.IO.File.WriteAllText(System.IO.Path.Combine("Builds", "Screenshots", "crew-telemetry", "crew-telemetry.txt"), report.ToString());
            Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }

        static float Median(IEnumerable<float> v)
        {
            var s = v.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2f;
        }
    }
}
