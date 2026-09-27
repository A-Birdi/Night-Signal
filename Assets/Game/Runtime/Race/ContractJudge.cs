using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Track;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Race
{
    /// <summary>One entrant's measurements through the Four Signals sectors (every car is measured; only humans are judged).</summary>
    public sealed class ContractRun
    {
        public float LastDistance = -1f;
        public bool[] ApexHit;
        public long EntryMs = -1;
        public double ArcStartRaw = -1, ArcRaw = -1;
        public int DescentWalls0 = -1, DescentResets0 = -1, DescentWalls = -1, DescentResets = -1;
        public bool BrakedNearZone, BrakeOffAtExit, WasBraking, ReleasePointSeen, BrakeOffAtReleasePoint;
        /// <summary>Route distance of the last brake release around the Descent zone (−1 = none), and where braking began.</summary>
        public float BrakeReleaseMetres = -1f, BrakeStartMetres = -1f;
        public float BrakeExitKmh = -1f;
        public float[] ExitKmh;

        public ContractRun(int apexes, int exits)
        {
            ApexHit = new bool[apexes];
            ExitKmh = Enumerable.Repeat(-1f, exits).ToArray();
        }
    }

    /// <summary>The four contracts' verdict for one run, with what failed (spec: "partial results show which contract failed").</summary>
    public sealed class ContractVerdict
    {
        public bool Entry, Arc, Descent, Horizon;
        public int Passed => (Entry ? 1 : 0) + (Arc ? 1 : 0) + (Descent ? 1 : 0) + (Horizon ? 1 : 0);
        public string Detail = "";
    }

    /// <summary>
    /// S29 "Four Signals" judging (spec: C24's four authored sectors in order — Entry: beat the published sector time and
    /// touch both apex gates; Arc: bank the published raw drift across the three marked corners; Descent: no meaningful
    /// wall impact or reset, passing the brake-release zone legally — a braking phase there, the brake off by the published
    /// release point, speed inside the published window at the zone's end; Horizon: the published exit speeds and the
    /// full-route time limit). One race, judged from the same server-
    /// observed facts as progress and drift, offline and on the dedicated server alike. Published targets come from the
    /// certified benchmark (stage-benchmarks.json); without them the judge only measures (calibration).
    /// </summary>
    public sealed class ContractJudge
    {
        readonly float arcStart, descentStart, horizonStart;
        readonly List<RouteGateDef> apexes, exits;
        readonly RouteGateDef brakeZone;
        /// <summary>Published targets (null: measuring only).</summary>
        public readonly FourSignalsTargets Targets;
        /// <summary>The targets are borrowed from another mode's certificate (Hard until it has its own reference runs).</summary>
        public readonly bool Provisional;

        ContractJudge(TrackData track, FourSignalsTargets targets, bool provisional)
        {
            RouteGateDef Contract(string id) => track.Gates.First(g => g.Kind == "contract" && g.Id == id);
            RouteGateDef entry = Contract("Entry"), arc = Contract("Arc"), descent = Contract("Descent"), horizon = Contract("Horizon");
            arcStart = arc.StartMetres;
            descentStart = descent.StartMetres;
            horizonStart = horizon.StartMetres;
            apexes = track.Gates.Where(g => g.Kind == "apex" && g.StartMetres >= entry.StartMetres && g.StartMetres < entry.EndMetres).OrderBy(g => g.StartMetres).ToList();
            exits = track.Gates.Where(g => g.Kind == "exit-speed" && g.StartMetres >= horizon.StartMetres && g.StartMetres <= horizon.EndMetres).OrderBy(g => g.StartMetres).ToList();
            brakeZone = track.Gates.FirstOrDefault(g => g.Kind == "brake-zone" && g.StartMetres >= descent.StartMetres && g.EndMetres <= descent.EndMetres);
            Targets = targets;
            Provisional = provisional;
        }

        public int ApexCount => apexes.Count;
        public int ExitCount => exits.Count;

        /// <summary>A judge for a campaign event on a course with the four authored contract sectors; null otherwise.</summary>
        public static ContractJudge ForEvent(TrackData track, ContentCatalogue cat, string kind, string stageId, CampaignMode mode, bool measureAlways = false)
        {
            string[] ids = { "Entry", "Arc", "Descent", "Horizon" };
            if (!ids.All(id => track.Gates.Any(g => g.Kind == "contract" && g.Id == id))) return null;
            if (kind != "campaign" && !measureAlways) return null;
            FourSignalsTargets targets = null;
            bool provisional = false;
            if (cat != null && !string.IsNullOrEmpty(stageId))
            {
                if (cat.TryCertifiedBenchmark(stageId, mode, out CertifiedBenchmark own) && own.Contracts != null) targets = own.Contracts;
                else if (mode == CampaignMode.Hard && cat.TryCertifiedBenchmark(stageId, CampaignMode.Normal, out CertifiedBenchmark normal) && normal.Contracts != null)
                {
                    targets = normal.Contracts;
                    provisional = true;
                }
            }
            return new ContractJudge(track, targets, provisional);
        }

        /// <summary>One fixed step for one entrant, after its simulation, progress, reset and drift handling.</summary>
        public void Step(RaceEntrant e, DriverInput input, long raceMicros, bool reset)
        {
            ContractRun r = e.ContractRun ?? (e.ContractRun = new ContractRun(apexes.Count, exits.Count));
            float d = e.Progress.Location.Distance;
            float last = r.LastDistance;
            r.LastDistance = d;
            // No crossings across a discontinuity (a reset, a recovery, the first step).
            if (reset || last < 0f || d < last || d - last > 30f) return;
            bool Crossed(float m) => last < m && d >= m;
            float kmh = e.State.Velocity.magnitude * 3.6f;
            for (int i = 0; i < apexes.Count; i++)
                if (Crossed(apexes[i].StartMetres) && Mathf.Abs(e.Progress.Location.Lateral - apexes[i].LineOffset) <= apexes[i].LineTolerance) r.ApexHit[i] = true;
            if (r.EntryMs < 0 && Crossed(arcStart))
            {
                r.EntryMs = raceMicros / 1000;
                r.ArcStartRaw = e.Drift.BankedRaw;
            }
            if (r.DescentWalls0 < 0 && Crossed(descentStart))
            {
                r.DescentWalls0 = e.Progress.WallIncidents;
                r.DescentResets0 = e.Progress.Resets + e.AutoRecoveries;
            }
            if (brakeZone != null)
            {
                if (d >= brakeZone.StartMetres - 120f && d <= brakeZone.EndMetres && input.Brake > 0.05f) r.BrakedNearZone = true;
                if (d >= brakeZone.StartMetres - 200f && d <= brakeZone.EndMetres + 300f)
                {
                    if (input.Brake > 0.05f) { if (!r.WasBraking) r.BrakeStartMetres = d; r.WasBraking = true; }
                    else if (r.WasBraking) { r.WasBraking = false; r.BrakeReleaseMetres = d; }
                }
                if (Targets != null && !r.ReleasePointSeen && Targets.BrakeReleaseByMetres > 0f && Crossed(Targets.BrakeReleaseByMetres))
                {
                    r.ReleasePointSeen = true;
                    r.BrakeOffAtReleasePoint = input.Brake <= 0.05f;
                }
                if (r.BrakeExitKmh < 0f && Crossed(brakeZone.EndMetres))
                {
                    r.BrakeExitKmh = kmh;
                    r.BrakeOffAtExit = input.Brake <= 0.05f;
                }
            }
            if (r.DescentWalls < 0 && Crossed(horizonStart))
            {
                // Chains bank at the sector ends, so the Arc's drift is final by the start of the Horizon (no zones in between).
                r.ArcRaw = r.ArcStartRaw >= 0 ? e.Drift.BankedRaw - r.ArcStartRaw : -1;
                if (r.DescentWalls0 >= 0)
                {
                    r.DescentWalls = e.Progress.WallIncidents - r.DescentWalls0;
                    r.DescentResets = e.Progress.Resets + e.AutoRecoveries - r.DescentResets0;
                }
            }
            for (int i = 0; i < exits.Count; i++)
                if (r.ExitKmh[i] < 0f && Crossed(exits[i].StartMetres)) r.ExitKmh[i] = kmh;
        }

        /// <summary>The verdict for a run: <paramref name="routeLimitMs"/> is the stage's full-route time limit (0 = none).</summary>
        public ContractVerdict Evaluate(ContractRun r, bool finished, long finishMs, long routeLimitMs)
        {
            var v = new ContractVerdict();
            if (r == null || Targets == null)
            {
                v.Detail = Targets == null ? "no published contract targets" : "no run";
                return v;
            }
            var fails = new List<string>();
            int apexHits = r.ApexHit.Count(x => x);
            v.Entry = r.EntryMs > 0 && r.EntryMs <= Targets.EntrySectorMs && apexHits == apexes.Count;
            if (!v.Entry) fails.Add(r.EntryMs <= 0 ? "Entry: not completed" : $"Entry: {r.EntryMs / 1000.0:F2}s / {Targets.EntrySectorMs / 1000.0:F2}s, apex gates {apexHits}/{apexes.Count}");
            v.Arc = r.ArcRaw >= Targets.ArcDriftRaw;
            if (!v.Arc) fails.Add($"Arc: drift {System.Math.Max(0, r.ArcRaw):N0} / {Targets.ArcDriftRaw:N0}");
            bool clean = r.DescentWalls == 0 && r.DescentResets == 0;
            bool inWindow = r.BrakeExitKmh >= Targets.BrakeExitMinKmh && r.BrakeExitKmh <= Targets.BrakeExitMaxKmh;
            bool released = r.BrakedNearZone && r.ReleasePointSeen && r.BrakeOffAtReleasePoint;
            v.Descent = clean && released && inWindow;
            if (!v.Descent)
                fails.Add(!clean ? $"Descent: {System.Math.Max(0, r.DescentWalls)} wall impact(s), {System.Math.Max(0, r.DescentResets)} reset(s)"
                    : $"Descent: brake {(!r.BrakedNearZone ? "not used in the zone" : released ? "released" : $"still on at the release point ({Targets.BrakeReleaseByMetres:F0} m)")}, {r.BrakeExitKmh:F0} km/h (window {Targets.BrakeExitMinKmh:F0}–{Targets.BrakeExitMaxKmh:F0})");
            bool speeds = true;
            for (int i = 0; i < exits.Count; i++)
                if (i < Targets.HorizonExitKmh.Count && r.ExitKmh[i] < Targets.HorizonExitKmh[i]) speeds = false;
            bool inTime = finished && (routeLimitMs <= 0 || finishMs <= routeLimitMs);
            v.Horizon = speeds && inTime;
            if (!v.Horizon)
                fails.Add(!inTime ? "Horizon: full-route time limit missed"
                    : "Horizon: exit speeds " + string.Join(", ", r.ExitKmh.Select((k, i) => $"{k:F0}/{(i < Targets.HorizonExitKmh.Count ? Targets.HorizonExitKmh[i] : 0):F0}")) + " km/h");
            v.Detail = fails.Count == 0 ? "all four contracts passed" : string.Join("; ", fails);
            return v;
        }
    }
}
