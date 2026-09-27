using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NightSignal.AI;
using NightSignal.Art;
using NightSignal.Content;
using NightSignal.Core.Content;
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
    /// <summary>Roster entry sent to clients in <see cref="Wire.MsgMatch"/>.</summary>
    public sealed class RosterEntry
    {
        public int Index;
        public string EntrantId;
        public string DisplayName;
        public bool Human;
        public string CarId;
        public float[] Paint;
        public int GridSlot;
    }

    public sealed class MatchInfo
    {
        public string MatchId, CourseId, Kind, Mode, StageId, Weather, BenchmarkReplayRival, GridNote;
        public int YourIndex = -1;
        public List<RosterEntry> Roster = new List<RosterEntry>();
    }

    /// <summary>
    /// Authoritative race for one match (spec §3.2, §4.3, §18). Only ticket-validated entrants from the frozen
    /// assignment are admitted; the server owns the clock, physics, progress, finish and classification.
    /// </summary>
    public sealed class RaceServer : MonoBehaviour
    {
        sealed class Entrant
        {
            public RosterEntry Roster;
            public bool Human;
            public ulong ClientId = ulong.MaxValue;
            public bool Connected;
            public EntrantStatus Status;
            public VehicleParams Params;
            public VehicleSimulation Sim;
            public VehicleState State;
            public EntrantProgress Progress;
            public RouteFollower Ai;
            public readonly DriverInput[] Inputs = new DriverInput[256];
            public readonly int[] InputTicks = Enumerable.Repeat(-1, 256).ToArray();
            public int LatestInputTick = -1;
            public DriverInput LastInput;
            public float ResetHeld;
            public double LoadingProgress;
        }

        NetworkManager nm;
        MatchAssignment assignment;
        MatchTicketValidator validator;
        ContentLibrary lib;
        TrackData track;
        RaceProgressTracker tracker;
        readonly List<Entrant> entrants = new List<Entrant>();
        readonly Dictionary<ulong, Entrant> byClient = new Dictionary<ulong, Entrant>();
        readonly Dictionary<ulong, Entrant> pendingApproval = new Dictionary<ulong, Entrant>();
        Action<MatchResults> onFinished;
        MatchPhase phase = MatchPhase.WaitingForEntrants;
        int startTick = int.MaxValue;
        float phaseStartedAt;
        long firstHumanFinishMicros = -1;
        long deadlineMicros = long.MaxValue;
        bool finishedReported;
        public MatchPhase Phase => phase;
        public string MatchId => assignment?.MatchId;

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
            track = course.Track;
            tracker = new RaceProgressTracker(track);
            Physics.SyncTransforms();
            BuildEntrants();

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
            Debug.Log($"[NightSignal.Server] match {assignment.MatchId} on {assignment.CourseId}: {entrants.Count(e => e.Human)} humans, {entrants.Count(e => !e.Human)} AI, port {cfg.Port}");
        }

        void BuildEntrants()
        {
            ContentCatalogue cat = lib.Catalogue;
            var world = new PhysicsVehicleWorld(Physics.defaultPhysicsScene, GameLayers.DrivableMask, GameLayers.BarrierMask);
            int slot = 0;
            foreach (AssignmentEntrant h in assignment.Entrants.Where(x => x.Role == "racer"))
                entrants.Add(CreateEntrant(slot++, h.AccountId, h.DisplayName, true, h.CarId, world));
            foreach (string rivalId in assignment.AiEntrants)
            {
                if (slot >= Limits.MaxRaceEntrants) break; // never more than six registered racers
                RivalDef rival = cat.Rival(rivalId);
                string car = LegalCarFor(cat, rival.PrimaryCar, assignment.CarCapPi);
                Entrant e = CreateEntrant(slot++, rival.Id, rival.Name, false, car, world);
                e.Ai = new RouteFollower(track, e.Params, AiProfiles.For(rival, assignment.StageNumber));
                e.Status = EntrantStatus.Loaded;
                entrants.Add(e);
            }
        }

        Entrant CreateEntrant(int slot, string id, string name, bool human, string carId, IVehicleWorld world)
        {
            VehicleParams p = lib.Params(carId, AssistSettings.Default);
            GridSlot g = track.Grid[slot];
            var e = new Entrant
            {
                Human = human,
                Params = p,
                Sim = new VehicleSimulation(p, world),
                State = VehicleState.AtRest(g.Position, g.Rotation),
                Progress = new EntrantProgress(track),
                Status = human ? EntrantStatus.Reserved : EntrantStatus.Loaded,
                Roster = new RosterEntry { Index = slot, EntrantId = id, DisplayName = name, Human = human, CarId = carId, GridSlot = slot, Paint = Palette(slot, human) },
            };
            tracker.Start(e.Progress, e.State.Position);
            return e;
        }

        /// <summary>A rival whose primary car exceeds the event cap uses a declared legal alternate (spec App. B).</summary>
        static string LegalCarFor(ContentCatalogue cat, string primary, int capPi)
        {
            CarDef p = cat.Car(primary);
            if (capPi <= 0 || p.BasePI <= capPi) return primary;
            CarDef alt = cat.Cars.Where(c => c.BasePI <= capPi && c.Drive == p.Drive).OrderByDescending(c => c.BasePI).FirstOrDefault()
                         ?? cat.Cars.Where(c => c.BasePI <= capPi).OrderByDescending(c => c.BasePI).First();
            return alt.Id;
        }

        static float[] Palette(int slot, bool human)
        {
            Color[] humans = { new Color(0.84f, 0.12f, 0.12f), new Color(0.12f, 0.45f, 0.85f), new Color(0.95f, 0.72f, 0.12f), new Color(0.15f, 0.6f, 0.35f), new Color(0.92f, 0.92f, 0.9f), new Color(0.55f, 0.25f, 0.75f) };
            Color c = human ? humans[slot % humans.Length] : Color.Lerp(humans[(slot + 3) % humans.Length], Color.gray, 0.45f);
            return new[] { c.r, c.g, c.b };
        }

        // ------------------------------------------------------------------ connections

        void Approve(NetworkManager.ConnectionApprovalRequest req, NetworkManager.ConnectionApprovalResponse res)
        {
            res.CreatePlayerObject = false;
            string ticket = req.Payload != null ? Encoding.UTF8.GetString(req.Payload) : null;
            TicketFailure failure = validator.Validate(ticket, out MatchTicketClaims claims);
            Entrant e = failure == TicketFailure.None
                ? entrants.FirstOrDefault(x => x.Human && x.Roster.EntrantId == claims.Subject)
                : null;
            if (failure != TicketFailure.None || claims.Role != "racer" || e == null || e.Connected || phase >= MatchPhase.Countdown)
            {
                res.Approved = false;
                res.Reason = failure != TicketFailure.None ? $"ticket_{failure}" : e == null ? "not_an_entrant" : "late_or_duplicate";
                Debug.Log($"[NightSignal.Server] connection refused: {res.Reason}");
                return;
            }
            pendingApproval[req.ClientNetworkId] = e;
            res.Approved = true;
        }

        void OnClientConnected(ulong clientId)
        {
            if (!pendingApproval.TryGetValue(clientId, out Entrant e)) return;
            pendingApproval.Remove(clientId);
            e.ClientId = clientId;
            e.Connected = true;
            e.Status = EntrantStatus.Loading;
            byClient[clientId] = e;
            var info = new MatchInfo
            {
                MatchId = assignment.MatchId, CourseId = assignment.CourseId, Kind = assignment.Kind, Mode = assignment.Mode,
                StageId = assignment.StageId, Weather = assignment.Weather, BenchmarkReplayRival = assignment.BenchmarkReplayRival,
                GridNote = assignment.GridNote, YourIndex = e.Roster.Index, Roster = entrants.Select(x => x.Roster).ToList(),
            };
            FastBufferWriter w = Wire.JsonWriter(JsonConvert.SerializeObject(info));
            using (w) nm.CustomMessagingManager.SendNamedMessage(Wire.MsgMatch, clientId, w, NetworkDelivery.ReliableFragmentedSequenced);
            SendPhase(clientId);
            Debug.Log($"[NightSignal.Server] entrant {e.Roster.Index} ({e.Roster.DisplayName}) connected as client {clientId}");
        }

        void OnClientDisconnected(ulong clientId)
        {
            pendingApproval.Remove(clientId);
            if (!byClient.TryGetValue(clientId, out Entrant e)) return;
            e.Connected = false;
            // A lost entrant after loading began cannot resume driving in this event (spec §4.4).
            if (!e.Progress.Finished && e.Status != EntrantStatus.Dnf)
                e.Status = EntrantStatus.DqDisconnected;
            Debug.Log($"[NightSignal.Server] entrant {e.Roster.Index} disconnected → {e.Status}");
        }

        void OnLoaded(ulong clientId, FastBufferReader reader)
        {
            if (!byClient.TryGetValue(clientId, out Entrant e)) return;
            reader.ReadValueSafe(out float progress);
            e.LoadingProgress = progress;
            if (progress >= 1f && e.Status == EntrantStatus.Loading) e.Status = EntrantStatus.Loaded;
        }

        void OnInput(ulong clientId, FastBufferReader reader)
        {
            if (!byClient.TryGetValue(clientId, out Entrant e) || e.Status != EntrantStatus.Racing && e.Status != EntrantStatus.Loaded) return;
            reader.ReadValueSafe(out int latestTick);
            reader.ReadValueSafe(out byte count);
            int now = nm.LocalTime.Tick;
            count = (byte)Mathf.Min(count, Wire.InputRedundancy);
            for (int i = 0; i < count; i++)
            {
                DriverInput input = Wire.ReadInput(reader);
                int tick = latestTick - (count - 1 - i);
                // Late commands for ticks already simulated are ignored; far-future ones are rejected.
                if (tick <= now - 1 || tick > now + 120) continue;
                int slot = tick & 255;
                e.Inputs[slot] = input;
                e.InputTicks[slot] = tick;
                if (tick > e.LatestInputTick) e.LatestInputTick = tick;
            }
        }

        // ------------------------------------------------------------------ simulation

        void OnTick()
        {
            int tick = nm.LocalTime.Tick;
            switch (phase)
            {
                case MatchPhase.Loading:
                    TickLoading(tick);
                    break;
                case MatchPhase.Countdown:
                    if (tick >= startTick) SetPhase(MatchPhase.Racing);
                    break;
                case MatchPhase.Racing:
                    TickRacing(tick);
                    break;
            }
            if (phase >= MatchPhase.Countdown && phase <= MatchPhase.Results && tick % 3 == 0) BroadcastSnapshot(tick);
        }

        void TickLoading(int tick)
        {
            float elapsed = Time.realtimeSinceStartup - phaseStartedAt;
            bool anyProgressing = entrants.Any(e => e.Human && e.Connected && e.Status == EntrantStatus.Loading && e.LoadingProgress > 0.2);
            float limit = Limits.LoadingTimeoutMs / 1000f + (anyProgressing ? Limits.LoadingExtensionMs / 1000f : 0f);
            bool allLoaded = entrants.Where(e => e.Human).All(e => e.Status == EntrantStatus.Loaded || e.Status == EntrantStatus.DqDisconnected);
            if (!allLoaded && elapsed < limit) return;
            foreach (Entrant e in entrants.Where(x => x.Human && x.Status != EntrantStatus.Loaded))
                e.Status = EntrantStatus.DqDisconnected; // failed to load in time: DQ for this event
            if (!entrants.Any(e => e.Human && e.Status == EntrantStatus.Loaded))
            {
                Abort("no human entrant finished loading");
                return;
            }
            startTick = tick + 60 * 4; // one second of grid settle, then 3-2-1-GO against the shared tick
            SetPhase(MatchPhase.Countdown);
        }

        void TickRacing(int tick)
        {
            long raceMicros = NetBootstrap.RaceMicros(tick, startTick);
            foreach (Entrant e in entrants)
            {
                if (e.Status == EntrantStatus.Loaded) e.Status = EntrantStatus.Racing;
                if (e.Status != EntrantStatus.Racing) continue;
                DriverInput input = e.Human ? HumanInput(e, tick) : e.Ai.Drive(e.State);
                VehicleState prev = e.State;
                e.Sim.Step(ref e.State, input);
                tracker.Step(e.Progress, prev, e.State, e.Sim.Telemetry, raceMicros, VehicleSimulation.TickDt);

                e.ResetHeld = input.ResetHeld ? e.ResetHeld + VehicleSimulation.TickDt : 0f;
                if (e.ResetHeld >= 0.7f)
                {
                    e.State = tracker.ResetPose(e.Progress, e.Params);
                    e.ResetHeld = 0f;
                }
                if (e.Progress.Finished)
                {
                    e.Status = EntrantStatus.Finished;
                    if (e.Human && firstHumanFinishMicros < 0)
                    {
                        firstHumanFinishMicros = e.Progress.FinishTimeMicros;
                        deadlineMicros = ComputeDeadline(firstHumanFinishMicros);
                    }
                }
            }
            bool anyActive = entrants.Any(e => e.Status == EntrantStatus.Racing);
            bool anyHumanActive = entrants.Any(e => e.Human && e.Status == EntrantStatus.Racing);
            if (!anyActive || !anyHumanActive && firstHumanFinishMicros >= 0 || raceMicros >= deadlineMicros || raceMicros > 15L * 60 * 1_000_000)
                FinishRace();
        }

        DriverInput HumanInput(Entrant e, int tick)
        {
            int slot = tick & 255;
            if (e.InputTicks[slot] == tick)
            {
                e.LastInput = e.Inputs[slot];
                return e.LastInput;
            }
            // Missing input: repeat the last one briefly, then coast and brake (spec §4.4: never hold throttle).
            float starved = (tick - e.LatestInputTick) * VehicleSimulation.TickDt;
            return e.LastInput.Starved(starved, Limits.InputStarvationCoastMs / 1000f);
        }

        long ComputeDeadline(long firstFinish)
        {
            if (assignment.Kind == "campaign" && assignment.Benchmark != null && assignment.Benchmark.TargetTimeMs > 0)
            {
                CampaignMode mode = assignment.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal;
                long envelope = StageOutcome.SupportEnvelopeMs(assignment.Benchmark.TargetTimeMs, mode);
                long hard = Math.Max(envelope, assignment.Benchmark.HardTimeoutMs);
                return StageOutcome.DeadlineMs(firstFinish / 1000, envelope, hard) * 1000L;
            }
            return firstFinish + Limits.FirstFinishGraceMs * 1000L;
        }

        void FinishRace()
        {
            foreach (Entrant e in entrants.Where(x => x.Status == EntrantStatus.Racing))
                e.Status = EntrantStatus.Dnf;
            SetPhase(MatchPhase.Results);
            MatchResults results = BuildResults();
            FastBufferWriter w = Wire.JsonWriter(JsonConvert.SerializeObject(results));
            using (w) nm.CustomMessagingManager.SendNamedMessageToAll(Wire.MsgResults, w, NetworkDelivery.ReliableFragmentedSequenced);
            ReportOnce(results);
        }

        MatchResults BuildResults()
        {
            int totalCps = tracker.TotalCheckpoints;
            var finishes = entrants.Select(e => new EntrantFinish
            {
                EntrantId = e.Roster.EntrantId,
                Outcome = OutcomeOf(e),
                FinishTimeMicros = e.Progress.FinishTimeMicros,
                LegalProgressMetres = e.Progress.RaceDistance - track.StartMetres,
            }).ToList();
            Dictionary<string, Placing> placings = RaceClassification.Classify(finishes).ToDictionary(p => p.EntrantId);
            var results = new MatchResults { MatchId = assignment.MatchId, ContentHash = assignment.ContentHash };
            foreach (Entrant e in entrants)
            {
                RunOutcome outcome = OutcomeOf(e);
                var r = new ResultEntrant
                {
                    EntrantId = e.Roster.EntrantId,
                    Human = e.Human,
                    Outcome = outcome.ToString(),
                    FinishTimeMicros = outcome == RunOutcome.Finished ? e.Progress.FinishTimeMicros : 0,
                    Placement = placings[e.Roster.EntrantId].Place,
                    Clean = e.Progress.Clean,
                    CheckpointFraction = totalCps > 0 ? (double)e.Progress.CheckpointsPassed / totalCps : 0,
                    ActiveProgressVerified = e.Progress.RaceDistance - track.StartMetres > 200f,
                    ActivelyDroveLegalCourse = outcome == RunOutcome.Finished && !e.Progress.CorridorCut && e.Progress.OutOfCorridorSeconds < 5f,
                    LegalProgressMetres = Math.Max(0, e.Progress.RaceDistance - track.StartMetres),
                };
                if (e.Human && outcome == RunOutcome.Finished)
                    r.ChallengesCompleted.AddRange(ChallengePredicates.Evaluate(assignment, e.Progress));
                results.Entrants.Add(r);
            }
            return results;
        }

        static RunOutcome OutcomeOf(Entrant e)
        {
            switch (e.Status)
            {
                case EntrantStatus.Finished: return RunOutcome.Finished;
                case EntrantStatus.Dnf: return RunOutcome.DidNotFinish;
                case EntrantStatus.DqQuit: return RunOutcome.Quit;
                default: return RunOutcome.DisqualifiedDisconnect;
            }
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
            if (nm == null || !nm.IsServer) return;
            foreach (ulong id in byClient.Keys.ToList()) SendPhase(id);
        }

        void SendPhase(ulong clientId)
        {
            using (var w = new FastBufferWriter(16, Allocator.Temp))
            {
                w.WriteValueSafe((byte)phase);
                w.WriteValueSafe(startTick);
                nm.CustomMessagingManager.SendNamedMessage(Wire.MsgPhase, clientId, w, NetworkDelivery.ReliableSequenced);
            }
        }

        void BroadcastSnapshot(int tick)
        {
            if (byClient.Count == 0) return;
            using (var w = new FastBufferWriter(1400, Allocator.Temp))
            {
                w.WriteValueSafe(tick);
                w.WriteValueSafe((byte)phase);
                w.WriteValueSafe((byte)entrants.Count);
                foreach (Entrant e in entrants)
                {
                    w.WriteValueSafe((byte)e.Roster.Index);
                    w.WriteValueSafe((byte)e.Status);
                    w.WriteValueSafe((ushort)e.Progress.CheckpointsPassed);
                    w.WriteValueSafe(e.Progress.RaceDistance);
                    w.WriteValueSafe((int)(e.Progress.FinishTimeMicros / 1000));
                    VehicleState s = e.State;
                    s.Tick = (uint)tick;
                    Wire.Write(w, s);
                }
                var targets = byClient.Where(kv => kv.Value.Connected).Select(kv => kv.Key).ToList();
                if (targets.Count > 0)
                    nm.CustomMessagingManager.SendNamedMessage(Wire.MsgSnapshot, targets, w, NetworkDelivery.UnreliableSequenced);
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
