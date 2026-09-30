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
        /// <summary>sprint | circuit | drift-attack | time-attack … (drift-attack: the HUD and autopilot drift the judged zones).</summary>
        public string FreeplayMode;
        /// <summary>A challenge trial's id (docs/CHALLENGE_TRIALS.md): its ghosts are kept apart, and no rival reference is raced.</summary>
        public string ChallengeTrialId;
        /// <summary>The surface the server simulates (dry | damp | wet): the client predicts its own car with the same grip.</summary>
        public string Surface = "dry";
        /// <summary>The time of day the event is lit for (the stage side's conditions, else the course's).</summary>
        public string TimeOfDay;
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
        /// <summary>
        /// Ghosts (spec §8: "server-generated/validated replay samples"): every human's run recorded by this authoritative
        /// simulation, by account, ready when the race completes; the host posts them to the control plane before the results.
        /// </summary>
        public readonly Dictionary<string, Core.Ghosts.GhostRecording> Ghosts = new Dictionary<string, Core.Ghosts.GhostRecording>();
        Dictionary<RaceEntrant, GhostRecorder> recorders;

        /// <summary>The format a ghost is kept under (as the Local ghosts are): the campaign stage and mode, else the Freeplay mode.</summary>
        /// <summary>The ghost format of an event: a campaign stage side, a challenge trial ("trial-&lt;id&gt;", as offline), else the Freeplay format.</summary>
        public static string GhostFormat(string kind, string stageId, string mode, string freeplayMode, string challengeTrialId = null) =>
            !string.IsNullOrEmpty(challengeTrialId) ? "trial-" + challengeTrialId :
            kind == "campaign" ? $"{stageId}-{(string.Equals(mode, "hard", StringComparison.OrdinalIgnoreCase) ? "hard" : "normal")}" : string.IsNullOrEmpty(freeplayMode) ? "race" : freeplayMode;

        void RecordGhosts(int tick)
        {
            if (recorders == null)
            {
                recorders = new Dictionary<RaceEntrant, GhostRecorder>();
                foreach (RaceEntrant e in sim.Entrants.Where(x => x.Human))
                {
                    AssignmentEntrant a = assignment.Entrants.FirstOrDefault(x => x.AccountId == e.Roster.EntrantId);
                    recorders[e] = new GhostRecorder(new Core.Ghosts.GhostHeader
                    {
                        CourseId = assignment.CourseId, CourseRevision = CourseRuntime.Active != null ? CourseRuntime.Active.SourceHash ?? "" : "",
                        Format = GhostFormat(assignment.Kind, assignment.StageId, assignment.Mode, assignment.FreeplayMode, assignment.ChallengeTrialId),
                        Surface = string.IsNullOrEmpty(sim.Rules.Surface) ? "dry" : sim.Rules.Surface,
                        PhysicsVersion = RaceSimulation.PhysicsVersion, ScoringVersion = RaceSimulation.ScoringVersion, GameVersion = Application.version,
                        CarModelId = e.Roster.CarId, BuildHash = a?.VehicleBuild?.BuildHash ?? a?.PerformanceHash ?? "", Pi = a?.CarPi ?? 0,
                        Driver = e.Roster.DisplayName, Provenance = "server-settlement", RecordedUtc = DateTime.UtcNow,
                    });
                }
            }
            long micros = sim.RaceMicros(tick);
            foreach (KeyValuePair<RaceEntrant, GhostRecorder> kv in recorders) kv.Value.Step(kv.Key, micros, tick);
        }

        readonly Dictionary<ulong, Link> byClient = new Dictionary<ulong, Link>();
        readonly Dictionary<ulong, Link> pendingApproval = new Dictionary<ulong, Link>();
        readonly Dictionary<ulong, string> pendingSpectators = new Dictionary<ulong, string>(), spectators = new Dictionary<ulong, string>();
        /// <summary>Spectator connections accepted at once (the control plane only issues spectator tickets to convoy members).</summary>
        public const int MaxSpectators = 6;
        /// <summary>Evidence: spectators served, and input packets from connections that are not racing (ignored).</summary>
        public int SpectatorsServed, IgnoredInputs;
        Action<MatchResults> onFinished;
        MatchPhase phase = MatchPhase.WaitingForEntrants;
        float phaseStartedAt;
        bool finishedReported;
        public MatchPhase Phase => phase;
        public string MatchId => assignment?.MatchId;
        List<RaceEntrant> Entrants => sim != null ? sim.Entrants : new List<RaceEntrant>();
        int StartTick => sim != null ? sim.StartTick : int.MaxValue;

        /// <summary>Per-entrant transport and contact health for evidence.</summary>
        public object Diagnostics() => new
        {
            entrants = Entrants.Select(e =>
            {
                links.TryGetValue(e, out Link l);
                return new
                {
                    entrant = e.Roster.Index, human = e.Human, team = e.Roster.Team, role = e.Roster.Role, status = e.Status.ToString(),
                    starvedTicks = l?.StarvedTicks ?? 0, lateInputs = l?.LateInputs ?? 0,
                    vehicleContacts = e.Progress.VehicleContacts, wallIncidents = e.Progress.WallIncidents, resets = e.Progress.Resets,
                };
            }).ToList(),
            spectatorsServed = SpectatorsServed,
            ignoredInputsFromNonEntrants = IgnoredInputs,
            network = NetworkBoundary(),
        };

        /// <summary>Addendum 04 evidence: what this server really listened on and why.</summary>
        public static object NetworkBoundary()
        {
            NetConfig c = NetConfig.FromCommandLine();
            return new
            {
                executable = System.IO.Path.GetFileName(System.Environment.GetCommandLineArgs()[0]),
                role = "dedicated-server",
                bindHost = c.BindHost, publicHost = c.PublicHost, port = c.Port, protocol = "udp",
                classification = c.BindClass(), lanOptIn = c.AllowLan, pid = System.Diagnostics.Process.GetCurrentProcess().Id,
            };
        }

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
            // A long-lived server hosts one match after another in the same physics scene: the previous match's course
            // (loaded additively, as this one is) goes first, or its collision stands across the new course's road.
            Scene boot = SceneManager.GetActiveScene();
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                Scene left = SceneManager.GetSceneAt(i);
                if (left == boot || !left.isLoaded) continue;
                Debug.Log($"[NightSignal.Server] unloading the previous course scene {left.name}");
                AsyncOperation unload = SceneManager.UnloadSceneAsync(left);
                if (unload != null) yield return unload;
            }
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
            sim.DeadlineSet += () =>
            {
                Debug.Log($"[NightSignal.Server] first human finish; finish window closes at race time {sim.DeadlineMicros / 1e6:F1} s");
                foreach (ulong id in byClient.Keys.Concat(spectators.Keys).ToList()) SendPhase(id); // clients show the finish window
            };

            nm = NetBootstrap.Ensure();
            nm.ConnectionApprovalCallback = Approve;
            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
            NetConfig cfg = NetConfig.FromCommandLine();
            // Addendum 04: listen where asked (loopback by default), never an implicit wildcard; checked before the socket.
            string bindProblem = cfg.BindProblem();
            if (bindProblem != null)
            {
                Abort("refusing to open the game server socket: " + bindProblem);
                yield break;
            }
            string bind = cfg.BindHost.Trim();
            NetBootstrap.Transport(nm).SetConnectionData(bind, cfg.Port, bind);
            Debug.Log($"[NightSignal.Server] listening UDP {bind}:{cfg.Port} ({cfg.BindClass()}), advertised {cfg.PublicHost}:{cfg.Port}, " +
                      $"LAN opt-in {(cfg.AllowLan ? "yes" : "no")}, pid {System.Diagnostics.Process.GetCurrentProcess().Id}");
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

        static string Short(string hash) => string.IsNullOrEmpty(hash) ? "none" : hash.Substring(0, Math.Min(12, hash.Length));

        /// <summary>
        /// The livery the other drivers will see: relayed only when it decodes for this car against this server's catalogue
        /// (the control plane validated ownership when it was applied); anything else races with the palette colour.
        /// </summary>
        string EventTimeOfDay() => RaceConditions.TimeOfDay(lib?.Catalogue, assignment.Kind, assignment.StageId,
            assignment.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal, CourseRuntime.Active);

        static string RelayLivery(ContentLibrary lib, AssignmentEntrant h)
        {
            if (string.IsNullOrEmpty(h.Livery)) return "";
            if (AppearanceMapping.ForWire(lib.Customization, h.CarId, h.Livery) != null) return h.Livery;
            Debug.LogWarning($"[NightSignal.Server] {h.DisplayName}'s livery could not be read for {h.CarId}; showing the palette colour");
            return "";
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
                DriftRanking = assignment.FreeplayMode == "drift-attack",
                BenchmarkTargetMs = assignment.Benchmark?.TargetTimeMs ?? 0,
                HardTimeoutMs = assignment.Benchmark?.HardTimeoutMs ?? assignment.Trial?.HardTimeoutMs ?? 0,
                RequiresBeatingFeaturedRival = assignment.Kind == "campaign" && StageBenchmark.IsFeaturedEncounter(assignment.StageType),
                // Weather preset wins; "stage-default" uses the stage side's authored conditions (else the course's surface).
                Surface = assignment.Weather != null && assignment.Weather.Contains("wet") ? "wet"
                    : RaceConditions.Surface(lib.Catalogue, assignment.Kind, assignment.StageId, assignment.Mode == "hard" ? CampaignMode.Hard : CampaignMode.Normal, CourseRuntime.Active),
            };
            var humans = new List<HumanSlot>();
            foreach (AssignmentEntrant h in assignment.Entrants.Where(x => x.Role == "racer"))
            {
                var slot = new HumanSlot { EntrantId = h.AccountId, DisplayName = h.DisplayName, CarId = h.CarId, Livery = RelayLivery(lib, h) };
                if (h.VehicleBuild != null)
                {
                    // Race exactly the build the control plane froze: re-resolve it here with Core and require the same hash.
                    // A mismatch means different build data on the two sides — a broken event, never a silent stock car.
                    if (lib.Parts == null) throw new InvalidOperationException("this game server has no parts catalogue");
                    Core.Builds.MechanicalSnapshot snap = h.VehicleBuild.Snapshot();
                    Core.Builds.ResolveResult r = Core.Builds.BuildResolver.Resolve(lib.Catalogue.Car(h.CarId), lib.Catalogue.CarTunings[h.CarId], lib.Parts, snap);
                    if (!r.Ok || r.Spec.BuildHash != h.VehicleBuild.BuildHash)
                        throw new InvalidOperationException($"{h.DisplayName}'s build {Short(h.VehicleBuild.BuildHash)} resolves to {(r.Ok ? Short(r.Spec.BuildHash) : "nothing")} here " +
                                                            $"(parts data {Short(h.VehicleBuild.PartsCatalogueHash)} vs {Short(lib.Parts.Hash)})");
                    slot.Spec = r.Spec;
                    slot.Build = snap;
                    Debug.Log($"[NightSignal.Server] {h.DisplayName} races the frozen build {Short(r.Spec.BuildHash)} of {h.VehicleBuild.InstanceId} " +
                              $"(applied revision {h.VehicleBuild.AppliedRevision}, {snap.Parts.Count} part(s), PI {h.VehicleBuild.Pi}) — hash verified");
                }
                else Debug.Log($"[NightSignal.Server] {h.DisplayName}: no vehicleBuild in the assignment; racing the stock {h.CarId}");
                humans.Add(slot);
            }
            if (!string.IsNullOrEmpty(assignment.ChallengeTrialId))
            {
                // A challenge trial: the loaner resolved here too; a human counts as driving it only if the frozen build the
                // control plane sent (verified above) is that loaner.
                trialDef = lib.Catalogue.ChallengeTrials.Find(assignment.ChallengeTrialId)
                           ?? throw new InvalidOperationException($"unknown challenge trial {assignment.ChallengeTrialId}");
                Core.Content.CarDef loanerCar = lib.Catalogue.Car(trialDef.Loaner.Car);
                Core.Builds.ResolveResult loaner = Core.Builds.TrialLoaners.Resolve(trialDef.Loaner, loanerCar, lib.Catalogue.CarTunings[loanerCar.Id], lib.Parts, out _);
                trialLoanerHash = loaner.Ok ? loaner.Spec.BuildHash : null;
                Debug.Log($"[NightSignal.Server] challenge trial {trialDef.Id} ({trialDef.Challenge}) on {trialDef.Course}: loaner {loanerCar.Id} " +
                          $"{(trialLoanerHash != null ? Short(trialLoanerHash) : "does not resolve")}");
            }
            var world = new PhysicsVehicleWorld(Physics.defaultPhysicsScene, GameLayers.DrivableMask, GameLayers.BarrierMask);
            // Team Trials (Addendum 01 §3): the frozen roster says which AI drive for the humans' team.
            List<string> opposing = assignment.AiEntrants, friendly = new List<string>();
            if (assignment.Kind == "trial" && assignment.Roster != null && assignment.Roster.Count > 0)
            {
                friendly = assignment.Roster.Where(r => r.Kind == "ai" && r.Team == "player").Select(r => r.EntrantId).ToList();
                opposing = assignment.Roster.Where(r => r.Kind == "ai" && r.Team != "player").Select(r => r.EntrantId).ToList();
                Debug.Log($"[NightSignal.Server] Team Trial {assignment.Trial?.TrialId} ({assignment.Trial?.Difficulty}): {humans.Count} human(s) + " +
                          $"{friendly.Count} friendly AI vs {opposing.Count} opposing AI");
            }
            RaceSimulation s = RaceSimulation.Build(track, lib, rules, humans, opposing, world, friendly);
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
            if (failure == TicketFailure.None && claims.Role == "spectator")
            {
                // Spectators (spec §4.4): convoy members the control plane issued a spectator ticket to — a disqualified
                // entrant, a late joiner. They receive the race and send nothing that is used; bounded in number.
                bool full = spectators.Count + pendingSpectators.Count >= MaxSpectators;
                res.Approved = !full && phase < MatchPhase.Results;
                if (!res.Approved)
                {
                    res.Reason = full ? "spectators_full" : "match_over";
                    Debug.Log($"[NightSignal.Server] spectator refused: {res.Reason}");
                    return;
                }
                pendingSpectators[req.ClientNetworkId] = claims.Subject;
                return;
            }
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
            if (pendingSpectators.TryGetValue(clientId, out string subject))
            {
                pendingSpectators.Remove(clientId);
                spectators[clientId] = subject;
                SpectatorsServed++;
                var watch = new MatchInfo
                {
                    MatchId = assignment.MatchId, CourseId = assignment.CourseId, Kind = assignment.Kind, Mode = assignment.Mode,
                    StageId = assignment.StageId, Weather = assignment.Weather, Contact = assignment.Collision, FreeplayMode = assignment.FreeplayMode, ChallengeTrialId = assignment.ChallengeTrialId, Surface = sim?.Rules.Surface ?? "dry", TimeOfDay = EventTimeOfDay(),
                    GridNote = assignment.GridNote, YourIndex = -1, Roster = Entrants.Select(x => x.Roster).ToList(),
                };
                FastBufferWriter sw = Wire.JsonWriter(JsonConvert.SerializeObject(watch));
                using (sw) nm.CustomMessagingManager.SendNamedMessage(Wire.MsgMatch, clientId, sw, NetworkDelivery.ReliableFragmentedSequenced);
                SendPhase(clientId);
                Debug.Log($"[NightSignal.Server] spectator connected as client {clientId} ({spectators.Count} watching)");
                return;
            }
            if (!pendingApproval.TryGetValue(clientId, out Link l)) return;
            pendingApproval.Remove(clientId);
            l.ClientId = clientId;
            l.Connected = true;
            l.Entrant.Status = EntrantStatus.Loading;
            byClient[clientId] = l;
            var info = new MatchInfo
            {
                MatchId = assignment.MatchId, CourseId = assignment.CourseId, Kind = assignment.Kind, Mode = assignment.Mode,
                StageId = assignment.StageId, Weather = assignment.Weather, Contact = assignment.Collision, FreeplayMode = assignment.FreeplayMode, ChallengeTrialId = assignment.ChallengeTrialId, Surface = sim?.Rules.Surface ?? "dry", TimeOfDay = EventTimeOfDay(),
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
            pendingSpectators.Remove(clientId);
            if (spectators.Remove(clientId)) { Debug.Log($"[NightSignal.Server] spectator client {clientId} left"); return; }
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
            if (!byClient.TryGetValue(clientId, out Link l))
            {
                IgnoredInputs++; // a spectator (or anyone not racing) cannot drive a car
                return;
            }
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
                    RecordGhosts(tick);
                    if ((tick - sim.StartTick) % (60 * 15) == 0) LogProgress(tick);
                    if (sim.Complete) FinishRace();
                    break;
            }
            if (phase >= MatchPhase.Countdown && phase <= MatchPhase.Results && tick % 3 == 0) BroadcastSnapshot(tick);
            if (phase == MatchPhase.Racing && sim.Rules.DriftRanking && tick % 6 == 0) SendDrift();
            if (phase == MatchPhase.Racing && tick % 6 == 3) SendRecovery();
        }

        /// <summary>
        /// Each driver's own recovery offer (Addendum 03 §7.1) — the kind (off route, overturned, stopped), seconds until
        /// the marshal acts, the number of completed recoveries and the last one's reason — so the client can show the
        /// same countdown as offline play. The server alone decides and applies every recovery.
        /// </summary>
        void SendRecovery()
        {
            foreach (KeyValuePair<ulong, Link> kv in byClient)
            {
                if (!kv.Value.Connected) continue;
                RaceEntrant e = kv.Value.Entrant;
                RecoveryStatus r = sim.Recovery(e);
                List<RecoveryEvent> done = e.Progress.Recoveries;
                string last = done.Count > 0 ? done[done.Count - 1].Reason : "";
                using (var w = new FastBufferWriter(24, Allocator.Temp))
                {
                    w.WriteValueSafe((byte)r.Kind);
                    w.WriteValueSafe(r.SecondsToAuto);
                    w.WriteValueSafe(done.Count);
                    w.WriteValueSafe((byte)(last == "manual" ? 1 : last == "off-route" ? 2 : last == "overturned" ? 3 : last == "stuck" ? 4 : 0));
                    nm.CustomMessagingManager.SendNamedMessage(Wire.MsgRecovery, kv.Key, w, NetworkDelivery.UnreliableSequenced);
                }
            }
        }

        /// <summary>Drift formats: each driver's own banked/unbanked/lost figures and chain for the HUD (the result counts).</summary>
        void SendDrift()
        {
            foreach (KeyValuePair<ulong, Link> kv in byClient)
            {
                if (!kv.Value.Connected) continue;
                Core.Rules.DriftScorer d = kv.Value.Entrant.Drift;
                using (var w = new FastBufferWriter(32, Allocator.Temp))
                {
                    w.WriteValueSafe((long)d.BankedRaw);
                    w.WriteValueSafe((long)d.UnbankedRaw);
                    w.WriteValueSafe((long)d.LostRaw);
                    w.WriteValueSafe((float)d.ChainMultiplier);
                    nm.CustomMessagingManager.SendNamedMessage(Wire.MsgDrift, kv.Key, w, NetworkDelivery.UnreliableSequenced);
                }
            }
        }

        /// <summary>Periodic headless-server trace: where every entrant is and how its commands are arriving.</summary>
        void LogProgress(int tick)
        {
            var sb = new StringBuilder($"[NightSignal.Server] t={(tick - sim.StartTick) / 60f:F0}s");
            foreach (RaceEntrant e in sim.Entrants)
            {
                sb.Append($" | #{e.Roster.Index} {(e.Human ? "H" : "AI")} {e.Status} {e.Progress.RaceDistance:F0}m {e.State.Velocity.magnitude * 3.6f:F0}km/h cp{e.Progress.CheckpointsPassed}");
                if (e.AutoRecoveries > 0) sb.Append($" rec{e.AutoRecoveries}");
                if (links.TryGetValue(e, out Link l))
                    sb.Append($" starved{l.StarvedTicks} late{l.LateInputs} lead{(l.LatestInputTick < 0 ? "-" : (l.LatestInputTick - tick).ToString())}");
            }
            Debug.Log(sb.ToString());
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
            if (recorders != null)
                foreach (KeyValuePair<RaceEntrant, GhostRecorder> kv in recorders)
                    Ghosts[kv.Key.Roster.EntrantId] = kv.Value.Finish(kv.Key.Progress);
            MatchResults results = BuildResults();
            FastBufferWriter w = Wire.JsonWriter(JsonConvert.SerializeObject(results));
            using (w) nm.CustomMessagingManager.SendNamedMessageToAll(Wire.MsgResults, w, NetworkDelivery.ReliableFragmentedSequenced);
            ReportOnce(results);
        }

        /// <summary>A challenge trial's definition and its loaner's build hash as this server resolves it (null outside a trial).</summary>
        Core.Rules.ChallengeTrialDef trialDef;
        string trialLoanerHash;

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
                    RawDriftScore = c.RawDriftScore,
                    ContractsPassed = System.Math.Max(0, c.ContractsPassed),
                };
                if (c.ContractsPassed >= 0) Debug.Log($"[NightSignal.Server] {r.EntrantId} Four Signals {c.ContractsPassed}/4: {c.ContractDetail}");
                if (c.Entrant.Human && c.Outcome == RunOutcome.Finished)
                    r.ChallengesCompleted.AddRange(ChallengePredicates.Evaluate(assignment, c.Entrant.Progress, c.Entrant.Drift, sim.Rules.Surface, c.Entrant.GateRun, c.Entrant.Racecraft,
                        c.Entrant.ZoneChains));
                if (c.Entrant.Human && trialDef != null)
                {
                    AssignmentEntrant a = assignment.Entrants.FirstOrDefault(x => x.AccountId == r.EntrantId);
                    Core.Rules.TrialVerdict v = Core.Rules.TrialJudge.Judge(trialDef, new Core.Rules.TrialRunFacts
                    {
                        Finished = c.Outcome == RunOutcome.Finished,
                        TimeMs = c.Outcome == RunOutcome.Finished ? c.FinishTimeMicros / 1000 : 0,
                        Resets = c.Entrant.Progress.Resets, WallImpacts = c.Entrant.Progress.WallIncidents,
                        HandbrakeSeconds = c.Entrant.Progress.HandbrakeSeconds, OffPavedSeconds = c.Entrant.Progress.OffPavedSeconds,
                        ChallengeGatesTouched = c.Entrant.GateRun != null && c.Entrant.GateRun.AllTouched(trialDef.Challenge),
                        ChallengeGates = c.Entrant.GateRun?.Count(trialDef.Challenge) ?? 0,
                        DriftRaw = (long)System.Math.Floor(c.Entrant.Drift.BankedRaw),
                        ZonesBanked = c.Entrant.Drift.ZonesBanked.Count,
                        ZonesTotal = lib.Catalogue.DriftZones.TryGetValue(trialDef.Course, out int zones) ? zones : 0,
                        DroveLoaner = trialLoanerHash != null && a != null && a.CarId == trialDef.Loaner.Car && a.VehicleBuild?.BuildHash == trialLoanerHash,
                    });
                    r.TrialId = trialDef.Id;
                    r.TrialPassed = v.Passed;
                    r.TrialSummary = v.Summary;
                    Debug.Log($"[NightSignal.Server] {r.EntrantId} challenge trial {trialDef.Id}: {(v.Passed ? "PASSED" : "not passed")} — {v.Summary}");
                }
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
            foreach (ulong id in byClient.Keys.Concat(spectators.Keys).ToList()) SendPhase(id);
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
            if (spectators.Count == 0) return;
            // Spectators: every car in compact form, none flagged as their own.
            using (var w = new FastBufferWriter(1200, Allocator.Temp))
            {
                w.WriteValueSafe(tick);
                w.WriteValueSafe((byte)phase);
                w.WriteValueSafe((byte)Entrants.Count);
                foreach (RaceEntrant e in Entrants)
                {
                    w.WriteValueSafe((byte)e.Roster.Index);
                    w.WriteValueSafe((byte)e.Status);
                    w.WriteValueSafe((ushort)e.Progress.CheckpointsPassed);
                    w.WriteValueSafe(e.Progress.RaceDistance);
                    w.WriteValueSafe((int)(e.Progress.FinishTimeMicros / 1000));
                    w.WriteValueSafe(-1);
                    w.WriteValueSafe((byte)(e.GhostUntilTick >= 0 || !e.Collides ? 1 : 0));
                    VehicleState s = e.State;
                    s.Tick = (uint)tick;
                    Wire.WriteCompact(w, s);
                }
                foreach (ulong id in spectators.Keys)
                    nm.CustomMessagingManager.SendNamedMessage(Wire.MsgSnapshot, id, w, NetworkDelivery.UnreliableSequenced);
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
