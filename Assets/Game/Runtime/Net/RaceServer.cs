using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NightSignal.Art;
using NightSignal.Content;
using NightSignal.Core.Rules;
using NightSignal.Core.Security;
using NightSignal.Race;
using NightSignal.Track;
using NightSignal.Vehicle;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NightSignal.Net
{
    public sealed class MatchInfo
    {
        public string MatchId, CourseId, Kind, Mode, StageId, Weather, GridNote, Contact;
        public int YourIndex = -1;
        public List<RosterEntry> Roster = new List<RosterEntry>();
    }

    /// <summary>
    /// Networked host for one match (spec §3.2, §4.3, §18). Only ticket-validated entrants from the frozen assignment are
    /// admitted; loading barrier, countdown and transport live here, while the race itself — physics, progress, light
    /// contact, finish window and classification — is the shared <see cref="RaceSimulation"/> (also used offline).
    /// </summary>
    public sealed class RaceServer : MonoBehaviour
    {
        /// <summary>Network-side state for a human entrant.</summary>
        sealed class Link
        {
            public RaceEntrant Entrant;
            public ulong ClientId = ulong.MaxValue;
            public bool Connected;
            public readonly DriverInput[] Inputs = new DriverInput[256];
            public readonly int[] InputTicks = Enumerable.Repeat(-1, 256).ToArray();
            public int LatestInputTick = -1;
            public DriverInput LastInput;
            public double LoadingProgress;
            /// <summary>Racing ticks simulated without that tick's command (input arrived late or was lost).</summary>
            public int StarvedTicks;
            /// <summary>Commands first seen after their tick was already simulated.</summary>
            public int LateInputs;
        }

        NetworkManager nm;
        MatchAssignment assignment;
        MatchTicketValidator validator;
        ContentLibrary lib;
        RaceSimulation sim;
        readonly Dictionary<RaceEntrant, Link> links = new Dictionary<RaceEntrant, Link>();
        readonly Dictionary<ulong, Link> byClient = new Dictionary<ulong, Link>();
        readonly Dictionary<ulong, Link> pendingApproval = new Dictionary<ulong, Link>();
        Action<MatchResults> onFinished;
        MatchPhase phase = MatchPhase.WaitingForEntrants;
        float phaseStartedAt;
        bool finishedReported;
        public MatchPhase Phase => phase;
        public string MatchId => assignment?.MatchId;
        List<RaceEntrant> Entrants => sim != null ? sim.Entrants : new List<RaceEntrant>();
        int StartTick => sim != null ? sim.StartTick : int.MaxValue;

        /// <summary>Per-entrant transport and contact health for evidence.</summary>
        public object Diagnostics() => Entrants.Select(e =>
        {
            links.TryGetValue(e, out Link l);
            return new
            {
                entrant = e.Roster.Index, human = e.Human, team = e.Roster.Team, role = e.Roster.Role, status = e.Status.ToString(),
                starvedTicks = l?.StarvedTicks ?? 0, lateInputs = l?.LateInputs ?? 0,
                vehicleContacts = e.Progress.VehicleContacts, wallIncidents = e.Progress.WallIncidents, resets = e.Progress.Resets,
            };
        }).ToList();

        public void Begin(MatchAssignment a, List<TicketKey> jwks, Action<MatchResults> finished)
        {
            assignment = a;
            onFinished = finished;
            lib = ContentLibrary.Load();
            validator = new MatchTicketValidator(a.TicketIssuer, jwks, a.MatchId, a.Build, a.Protocol, a.ContentHash,
                () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            StartCoroutine(LoadAndListen());
        }

        System.Collections.IEnumerator LoadAndListen()
        {
            AsyncOperation load = SceneManager.LoadSceneAsync(assignment.CourseId, LoadSceneMode.Additive);
            if (load == null)
            {
                Abort($"course scene {assignment.CourseId} is not in the build");
                yield break;
            }
            yield return load;
            yield return null;
            CourseRuntime course = CourseRuntime.Active;
            if (course == null || course.Track == null)
            {
                Abort("course failed to generate");
                yield break;
            }
            Physics.SyncTransforms();
            try
            {
                sim = BuildSimulation(course.Track);
            }
            catch (Exception e)
            {
                // A roster that cannot be built (e.g. featured rival missing) is a broken event, never a free win.
                Abort("roster could not be built: " + e.Message);
                yield break;
            }
            sim.HumanInput = HumanInput;
            sim.DeadlineSet += () => { foreach (ulong id in byClient.Keys.ToList()) SendPhase(id); }; // clients show the finish window

            nm = NetBootstrap.Ensure();
            nm.ConnectionApprovalCallback = Approve;
            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
            NetConfig cfg = NetConfig.FromCommandLine();
            NetBootstrap.Transport(nm).SetConnectionData("0.0.0.0", cfg.Port, "0.0.0.0");
            if (!nm.StartServer())
            {
                Abort("could not start the game server socket");
                yield break;
            }
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgInput, OnInput);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Wire.MsgLoaded, OnLoaded);
            nm.NetworkTickSystem.Tick += OnTick;
            SetPhase(MatchPhase.Loading);
            Debug.Log($"[NightSignal.Server] match {assignment.MatchId} on {assignment.CourseId}: {Entrants.Count(e => e.Human)} humans, " +
                      $"{Entrants.Count(e => !e.Human)} AI, contact {assignment.Collision}, port {cfg.Port}");
        }

        RaceSimulation BuildSimulation(TrackData track)
        {
            var rules = new RaceEventRules
            {
                Kind = assignment.Kind,
                Mode = assignment.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal,
                StageId = assignment.StageId,
                StageNumber = Math.Max(1, assignment.StageNumber),
                CarCapPi = assignment.CarCapPi,
                Contact = assignment.Collision == "non-contact" ? ContactPolicy.NonContact : ContactPolicy.LightContact,
                BenchmarkTargetMs = assignment.Benchmark?.TargetTimeMs ?? 0,
                HardTimeoutMs = assignment.Benchmark?.HardTimeoutMs ?? 0,
                RequiresBeatingFeaturedRival = assignment.Kind == "campaign" && StageBenchmark.IsFeaturedEncounter(assignment.StageType),
                // Weather preset wins; "stage-default" uses the course's authored Normal surface.
                Surface = assignment.Weather != null && assignment.Weather.Contains("wet") ? "wet"
                    : CourseRuntime.Active?.Route?.Surface ?? "dry",
            };
            List<HumanSlot> humans = assignment.Entrants.Where(x => x.Role == "racer")
                .Select(h => new HumanSlot { EntrantId = h.AccountId, DisplayName = h.DisplayName, CarId = h.CarId }).ToList();
            var world = new PhysicsVehicleWorld(Physics.defaultPhysicsScene, GameLayers.DrivableMask, GameLayers.BarrierMask);
            RaceSimulation s = RaceSimulation.Build(track, lib, rules, humans, assignment.AiEntrants, world);
            foreach (RaceEntrant e in s.Entrants.Where(x => x.Human))
                links[e] = new Link { Entrant = e };
            return s;
        }

        // ------------------------------------------------------------------ connections

        void Approve(NetworkManager.ConnectionApprovalRequest req, NetworkManager.ConnectionApprovalResponse res)
        {
            res.CreatePlayerObject = false;
            string ticket = req.Payload != null ? Encoding.UTF8.GetString(req.Payload) : null;
            TicketFailure failure = validator.Validate(ticket, out MatchTicketClaims claims);
            Link link = failure == TicketFailure.None
                ? links.Values.FirstOrDefault(x => x.Entrant.Roster.EntrantId == claims.Subject)
                : null;
            if (failure != TicketFailure.None || claims.Role != "racer" || link == null || link.Connected || phase >= MatchPhase.Countdown)
            {
                res.Approved = false;
                res.Reason = failure != TicketFailure.None ? $"ticket_{failure}" : link == null ? "not_an_entrant" : "late_or_duplicate";
                Debug.Log($"[NightSignal.Server] connection refused: {res.Reason}");
                return;
            }
            pendingApproval[req.ClientNetworkId] = link;
            res.Approved = true;
        }

        void OnClientConnected(ulong clientId)
        {
            if (!pendingApproval.TryGetValue(clientId, out Link l)) return;
            pendingApproval.Remove(clientId);
            l.ClientId = clientId;
            l.Connected = true;
            l.Entrant.Status = EntrantStatus.Loading;
            byClient[clientId] = l;
            var info = new MatchInfo
            {
                MatchId = assignment.MatchId, CourseId = assignment.CourseId, Kind = assignment.Kind, Mode = assignment.Mode,
                StageId = assignment.StageId, Weather = assignment.Weather, Contact = assignment.Collision,
                GridNote = assignment.GridNote, YourIndex = l.Entrant.Roster.Index, Roster = Entrants.Select(x => x.Roster).ToList(),
            };
            FastBufferWriter w = Wire.JsonWriter(JsonConvert.SerializeObject(info));
            using (w) nm.CustomMessagingManager.SendNamedMessage(Wire.MsgMatch, clientId, w, NetworkDelivery.ReliableFragmentedSequenced);
            SendPhase(clientId);
            Debug.Log($"[NightSignal.Server] entrant {l.Entrant.Roster.Index} ({l.Entrant.Roster.DisplayName}) connected as client {clientId}");
        }

        void OnClientDisconnected(ulong clientId)
        {
            pendingApproval.Remove(clientId);
            if (!byClient.TryGetValue(clientId, out Link l)) return;
            l.Connected = false;
            RaceEntrant e = l.Entrant;
            // A lost entrant after loading began cannot resume driving in this event (spec §4.4); its car stops colliding.
            if (!e.Progress.Finished && e.Status != EntrantStatus.Dnf)
                e.Status = EntrantStatus.DqDisconnected;
            Debug.Log($"[NightSignal.Server] entrant {e.Roster.Index} disconnected → {e.Status}");
        }

        void OnLoaded(ulong clientId, FastBufferReader reader)
        {
            if (!byClient.TryGetValue(clientId, out Link l)) return;
            reader.ReadValueSafe(out float progress);
            l.LoadingProgress = progress;
            if (progress >= 1f && l.Entrant.Status == EntrantStatus.Loading) l.Entrant.Status = EntrantStatus.Loaded;
        }

        void OnInput(ulong clientId, FastBufferReader reader)
        {
            if (!byClient.TryGetValue(clientId, out Link l)) return;
            if (l.Entrant.Status != EntrantStatus.Racing && l.Entrant.Status != EntrantStatus.Loaded) return;
            reader.ReadValueSafe(out int latestTick);
            reader.ReadValueSafe(out byte count);
            int now = nm.LocalTime.Tick;
            count = (byte)Mathf.Min(count, Wire.InputRedundancy);
            for (int i = 0; i < count; i++)
            {
                DriverInput input = Wire.ReadInput(reader);
                int tick = latestTick - (count - 1 - i);
                // Late commands for ticks already simulated are ignored; far-future ones are rejected.
                if (tick <= now - 1)
                {
                    if (tick > l.LatestInputTick) l.LateInputs++;
                    continue;
                }
                if (tick > now + 120) continue;
                int slot = tick & 255;
                l.Inputs[slot] = input;
                l.InputTicks[slot] = tick;
                if (tick > l.LatestInputTick) l.LatestInputTick = tick;
            }
        }

        DriverInput HumanInput(RaceEntrant e, int tick)
        {
            Link l = links[e];
            int slot = tick & 255;
            if (l.InputTicks[slot] == tick)
            {
                l.LastInput = l.Inputs[slot];
                return l.LastInput;
            }
            // Missing input: repeat the last one briefly, then coast and brake (spec §4.4: never hold throttle).
            l.StarvedTicks++;
            float starved = (tick - l.LatestInputTick) * VehicleSimulation.TickDt;
            return l.LastInput.Starved(starved, Limits.InputStarvationCoastMs / 1000f);
        }

        // ------------------------------------------------------------------ phases

        void OnTick()
        {
            int tick = nm.LocalTime.Tick;
            switch (phase)
            {
                case MatchPhase.Loading:
                    TickLoading(tick);
                    break;
                case MatchPhase.Countdown:
                    if (tick >= sim.StartTick) SetPhase(MatchPhase.Racing);
                    break;
                case MatchPhase.Racing:
                    sim.Tick(tick);
                    if (sim.Complete) FinishRace();
                    break;
            }
            if (phase >= MatchPhase.Countdown && phase <= MatchPhase.Results && tick % 3 == 0) BroadcastSnapshot(tick);
        }

        void TickLoading(int tick)
        {
            float elapsed = Time.realtimeSinceStartup - phaseStartedAt;
            bool anyProgressing = links.Values.Any(l => l.Connected && l.Entrant.Status == EntrantStatus.Loading && l.LoadingProgress > 0.2);
            float limit = Limits.LoadingTimeoutMs / 1000f + (anyProgressing ? Limits.LoadingExtensionMs / 1000f : 0f);
            bool allLoaded = links.Keys.All(e => e.Status == EntrantStatus.Loaded || e.Status == EntrantStatus.DqDisconnected);
            if (!allLoaded && elapsed < limit) return;
            foreach (RaceEntrant e in links.Keys.Where(x => x.Status != EntrantStatus.Loaded))
                e.Status = EntrantStatus.DqDisconnected; // failed to load in time: DQ for this event, never replaced by AI
            if (!links.Keys.Any(e => e.Status == EntrantStatus.Loaded))
            {
                Abort("no human entrant finished loading");
                return;
            }
            sim.StartTick = tick + 60 * 4; // one second of grid settle, then 3-2-1-GO against the shared tick
            SetPhase(MatchPhase.Countdown);
        }

        void FinishRace()
        {
            SetPhase(MatchPhase.Results);
            MatchResults results = BuildResults();
            FastBufferWriter w = Wire.JsonWriter(JsonConvert.SerializeObject(results));
            using (w) nm.CustomMessagingManager.SendNamedMessageToAll(Wire.MsgResults, w, NetworkDelivery.ReliableFragmentedSequenced);
            ReportOnce(results);
        }

        MatchResults BuildResults()
        {
            var results = new MatchResults { MatchId = assignment.MatchId, ContentHash = assignment.ContentHash };
            foreach (RaceEntrantResult c in sim.Classify())
            {
                var r = new ResultEntrant
                {
                    EntrantId = c.Entrant.Roster.EntrantId,
                    Human = c.Entrant.Human,
                    Outcome = c.Outcome.ToString(),
                    FinishTimeMicros = c.FinishTimeMicros,
                    Placement = c.Placement,
                    Clean = c.Entrant.Progress.Clean,
                    CheckpointFraction = c.CheckpointFraction,
                    ActiveProgressVerified = c.ActiveProgressVerified,
                    ActivelyDroveLegalCourse = c.ActivelyDroveLegalCourse,
                    LegalProgressMetres = c.LegalProgressMetres,
                };
                if (c.Entrant.Human && c.Outcome == RunOutcome.Finished)
                    r.ChallengesCompleted.AddRange(ChallengePredicates.Evaluate(assignment, c.Entrant.Progress));
                results.Entrants.Add(r);
            }
            return results;
        }

        void Abort(string reason)
        {
            Debug.LogWarning($"[NightSignal.Server] match aborted: {reason}");
            SetPhase(MatchPhase.Aborted);
            ReportOnce(new MatchResults { MatchId = assignment?.MatchId, ContentHash = assignment?.ContentHash, Aborted = true, AbortReason = reason });
        }

        void ReportOnce(MatchResults results)
        {
            if (finishedReported) return;
            finishedReported = true;
            onFinished?.Invoke(results);
        }

        // ------------------------------------------------------------------ messaging

        void SetPhase(MatchPhase p)
        {
            phase = p;
            phaseStartedAt = Time.realtimeSinceStartup;
            int now = nm != null && nm.IsListening ? nm.LocalTime.Tick : -1;
            Debug.Log($"[NightSignal.Server] phase {p} at tick {now}" + (p == MatchPhase.Countdown ? $", start tick {StartTick}" : ""));
            if (nm == null || !nm.IsServer) return;
            foreach (ulong id in byClient.Keys.ToList()) SendPhase(id);
        }

        void SendPhase(ulong clientId)
        {
            using (var w = new FastBufferWriter(16, Allocator.Temp))
            {
                w.WriteValueSafe((byte)phase);
                w.WriteValueSafe(StartTick);
                w.WriteValueSafe(sim == null || sim.DeadlineMicros == long.MaxValue ? -1L : sim.DeadlineMicros);
                nm.CustomMessagingManager.SendNamedMessage(Wire.MsgPhase, clientId, w, NetworkDelivery.ReliableSequenced);
            }
        }

        /// <summary>
        /// Per-client snapshot (protocol 2): the recipient's own car in full, every other car compact — about 0.75 KB for
        /// twelve cars, well inside one unfragmented datagram.
        /// </summary>
        void BroadcastSnapshot(int tick)
        {
            foreach (KeyValuePair<ulong, Link> kv in byClient)
            {
                if (!kv.Value.Connected) continue;
                using (var w = new FastBufferWriter(1200, Allocator.Temp))
                {
                    w.WriteValueSafe(tick);
                    w.WriteValueSafe((byte)phase);
                    w.WriteValueSafe((byte)Entrants.Count);
                    foreach (RaceEntrant e in Entrants)
                    {
                        bool own = e == kv.Value.Entrant;
                        links.TryGetValue(e, out Link l);
                        w.WriteValueSafe((byte)e.Roster.Index);
                        w.WriteValueSafe((byte)e.Status);
                        w.WriteValueSafe((ushort)e.Progress.CheckpointsPassed);
                        w.WriteValueSafe(e.Progress.RaceDistance);
                        w.WriteValueSafe((int)(e.Progress.FinishTimeMicros / 1000));
                        w.WriteValueSafe(l != null ? l.LatestInputTick : -1); // input acknowledgement (client RTT + lead)
                        byte flags = (byte)((e.GhostUntilTick >= 0 || !e.Collides ? 1 : 0) | (own ? 2 : 0)); // bit0 not colliding, bit1 full state
                        w.WriteValueSafe(flags);
                        VehicleState s = e.State;
                        s.Tick = (uint)tick;
                        if (own) Wire.Write(w, s);
                        else Wire.WriteCompact(w, s);
                    }
                    nm.CustomMessagingManager.SendNamedMessage(Wire.MsgSnapshot, kv.Key, w, NetworkDelivery.UnreliableSequenced);
                }
            }
        }

        public void Shutdown()
        {
            if (nm != null && nm.IsListening)
            {
                nm.NetworkTickSystem.Tick -= OnTick;
                nm.Shutdown();
            }
        }
    }
}
