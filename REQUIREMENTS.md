# Requirements ledger

States: **not started** · **in progress** · **implemented** (code/content exists, not yet proven end-to-end) ·
**unverified** (exercised partially or only by simulation/automation) · **verified** (see `VALIDATION.md`) · **blocked**.
Section numbers refer to `SPECIFICATION.md`. Content counts refer to playable, instantiated content, not data rows.
Automated drivers are never counted as human playtests.

**Addendum 01** (`docs/brief/Night_Signal_Addendum_01.txt`) revises several rows below; `docs/EFFECTIVE_RULES.md`
lists the superseded rules and where each lands. Rows marked _(A01)_ carry the revised requirement.

## Gate 0 — environment (§19)

| ID | Requirement | State | Evidence / note |
|---|---|---|---|
| G0.1 | Correct NEW repository/remote (A-Birdi/Night-Signal) | verified | V-001 |
| G0.2 | Real Unity version/project path; editor access through the bridge | verified | V-001, V-003 |
| G0.3 | Editor baseline pinned (D-001: 6000.6.3f1) | verified | V-004, V-007 |
| G0.4 | URP configured; Graphics default explicit (D-002) | verified | V-004, V-006 |
| G0.5 | Optional AI/editor-control packages removed after dependency check (D-003) | verified | V-002 |
| G0.6 | First verified source push | verified | V-001 |
| G0.7 | Built-client smoke test (build + launch + render) | verified | V-005, V-006 |
| G0.8 | Local account/control/database stack | in progress | DevAuth + SQLite control plane runs locally (V-013); Supabase CLI/Docker **blocked** (owner approval) |
| G0.9 | Server build targets: Windows dev server, Linux dedicated server | blocked (Linux) | Windows player in batch mode used as server; Linux/Dedicated Server modules not installed |
| G0.10 | Git LFS objects uploaded and retrievable | verified | V-008 |
| G0.11 | Credentials scope documented (public vs server-only secrets) | implemented | docs/ARCHITECTURE.md |

## Core rules and systems

| ID | Spec | Requirement | State |
|---|---|---|---|
| R1.1 | §1, A01 D01 | 1–6 simultaneous human entrants over a real network | unverified — 2-client localhost runs passed (V-016 pre-addendum, V-022 protocol 2); 6-client and remote runs pending |
| R1.2 | A01 §1 _(A01)_ | Named limits: ≤6 convoy humans, ≤6 event humans, ≤12 race vehicles, ≤6 meet humans; DQ never refilled by AI; spectators/replays never entrants | implemented (Limits, RosterPlanner, RaceServer) — unit-tested; 12-vehicle network run pending |
| R1.3 | A01 §1.3 _(A01)_ | ~~Six-human benchmark replay~~ superseded: featured rival always live; finales are H+1 duels; encounter stages require beating the live rival | implemented (RosterPlanner, StageOutcome) — unit-tested |
| R1.4 | A01 §1.2 _(A01)_ | Authored live opposition per stage (featured first); humans never displace it | implemented (stages.opposition.json, control plane, server) |
| R1.5 | §2.5 | Benchmarks from legal reference runs | not started (control plane uses labelled provisional benchmarks) |
| R1.6 | A01 §2 _(A01)_ | Bounded light car-to-car contact by default; Time Attack non-contact; no damage (D10) | implemented (VehicleContact, RaceSimulation, client prediction) — 12-car in-process run (V-019); contact under network load pending |
| R1.7 | A01 §12 _(A01)_ | R40/R48 finale-only, rejected server-side in every other placement | implemented (FinalRivals, RosterPlanner, RaceServer, validator) — unit-tested |
| R1.8 | A01 §5 _(A01)_ | Course access ledger: starters, 45k purchase or regular-stage clear, C25 reward, FP01–03 purchases, guest passes | in progress (Core rules + tests; control-plane ledger/endpoints pending) |
| R1.9 | A01 §6 _(A01)_ | Mode Ready → vote (server deadline, one-ticket-per-ballot draw, frozen) → Event Ready; group Time Attack | in progress (Core ballot rules + tests) |
| R1.10 | A01 §3 _(A01)_ | Three Team Trials (6 v 6), no mastery RP | in progress (Core scoring + roster + tests) |
| R1.11 | A01 §8 _(A01)_ | Main menu, full separate Local profile, Go Online boundary | in progress — Local profiles, Offline hub, Local campaign map + races + progression verified in the player (V-023); Go Online boundary not started |
| R1.12 | A01 §9–10 _(A01)_ | @handles, friends panel, rejoin grants keyed to leadership epoch (no reserved seat) | in progress (handle rules; interim disconnect removal) |
| R1.13 | A01 §11 _(A01)_ | 24 authored cues, unlock manifest, shared meet boombox | in progress — 24 synthesized cues; music.unlocks.json in the content hash; unlocks granted online (V-022) and locally (V-023); boombox pending |
| R1.14 | A01 §13 _(A01)_ | Visible per-car customization families, 8+ rim designs | not started |
| R2.1 | A02 §1–6 _(A02)_ | Five diversions (Cap Clash, Pit-Crew, Greenlight, Pocket Circuit, Canvas), 1–6 humans + solo, preemption/resume, dormant persistence | in progress — Core/Toys simulation + DowntimeSession, 92 tests (V-024); hosting and presentation pending |
| R2.2 | A02 §7 _(A02)_ | Continue / Service Break post-event flow | in progress (control plane) |
| R2.3 | A02 §9 _(A02)_ | ≥ 8 mechanical loadouts per car instance, ≥ 5 visual presets, 3 protected references, atomic whole-build apply/restore | in progress — Core/Builds data model, 222 tests (V-024); Garage UI pending |
| R2.4 | A02 §10 _(A02)_ | Garage Test Yard: drivable, same controller, A/B, preview unowned parts, no progression | not started |
| R2.5 | A02 §8, master §9 _(A02)_ | Parts/tuning system with real tradeoffs; favourite-car upgrade path for all 18 cars; calibrated pacing | in progress — 67 parts, resolver, recipes for 18 cars (data level); VehicleFactory integration and measurement pending; cap question in EFFECTIVE_RULES |
| R2.6 | A02 D208 _(A02)_ | 24 h dormant convoy for all-disconnected case | in progress (control plane) |
| R3.1 | §3.1 | URP, C#, Input System, uGUI+TMP single UI stack | in progress (UI stack not built; developer OnGUI HUDs only) |
| R3.2 | §3.2 | Dedicated authoritative server process (NGO + Unity Transport) | implemented — run pending |
| R3.3 | §3.2 | ASP.NET Core control plane (.NET 10 LTS), HTTP + authenticated control channel | verified (V-013) |
| R3.4 | §3.2 | Supabase Auth; JWT verification via JWKS | implemented; DevAuth path verified; Supabase itself **blocked** |
| R3.5 | §3.2 | Short-lived single-use audience-bound match tickets | verified (V-013, V-014) |
| R3.6 | §3.2 | Server-owned wallet/ownership/rank/challenge transactions | verified on SQLite (V-013); Postgres/RLS unexecuted |
| R3.7 | §3.2 | Small interfaces (Identity, PlayerStore, ConvoyDirectory, MatchAllocator, ResultLedger, ContentCatalogue, GameplayTransport) | in progress |
| R3.8 | §3.2 | Local integration env of real components; dev bypass impossible in production | in progress (DevAuth guard verified; Supabase local blocked) |
| R3.9 | §3.2a | Email/password lifecycle incl. reset, deletion request | not started (client side) |
| R3.10 | §3.2a | One active driving session per account; takeover consent | implemented in control plane (takeover) |
| R3.11 | §3.2a | Guest/offline practice clearly labelled, never uploads earnings | in progress (offline practice exists; labelling UI pending) |
| R3.12 | §3.3 | Leader is not authority; race survives leader loss | implemented (control plane transfer; server owns race) |
| R3.13 | §3.4 | Web target (secondary, later) | not started |
| R3.14 | §3.5 | Cost/operations worksheet after first six-client test | not started |
| R4.1 | §4 | Separate state machines: screen, presence, convoy, entrant | in progress (presence/convoy/entrant done; screen FSM with UI) |
| R4.2 | §4.1 | Convoy create/join, invites, privacy, expiry/rate limit | verified in control plane (V-013); client UI pending |
| R4.3 | §4.1 | Persistent convoy header UI | not started |
| R4.4 | §4.2 | Two revisioned ready checks, invalidation, 15 s / 120 s rules | verified in control plane (V-013) |
| R4.5 | §4.3 | Loading barrier (90 s + 30 s), server start tick, 3-2-1-GO | implemented (RaceServer) — run pending |
| R4.6 | §4.4 | Heartbeats, 250 ms coast, DQ, spectate, slot hold, leader transfer | partial: coast/DQ implemented; spectate/reconnect UI not started |
| R5.1 | §5.1 | Ordered frontier, shared selection, server validation | verified (V-009, V-013) |
| R5.2 | §5.2 | Team success, support envelopes, deadlines | verified rules (V-009); server deadline implemented |
| R5.3 | §5.3 | Acts, stage beats, intros/quips, skip rules | in progress (all text authored; presentation not built) |
| R5.4 | §5.4 | Hard campaign alternate dialogue, conditions, Shiori finale | in progress (text + conditions authored) |
| R6.1 | §6 | Free-steering raycast arcade chassis, 60 Hz | verified by harness/autopilot (V-011, V-012); human feel unverified |
| R6.2 | §6 | Drivetrain identities, assists, bindings | implemented; bindings UI/remap screen pending |
| R6.3 | §6 | Cameras chase/hood/bumper, FOV, shake, no blur | implemented |
| R6.4 | §6.1 | Non-contact default, wall-hit debounce, hold-reset +3 s | implemented; Light Contact not started |
| R7.1 | §7 | Raw drift scoring with anti-exploit | verified rules (V-009); not yet wired to races/gates |
| R8.1 | §8 | Freeplay modes; H+AI ≤ 6 | rules verified; modes not built |
| R8.2 | §8 | Server-validated ghosts | not started |
| R9.1 | §9 | 3D garage, presets, purchases exactly-once | purchases verified server-side; garage not built |
| R9.2 | §9 | Parts with tradeoffs; utility slot | not started |
| R9.3 | §9 | PI classes; measured stat harness | classes verified; harness implemented (V-011) |
| R9.4 | §9 | Livery editor (64 layers, undo) | not started |
| R10.1 | §10 | Economy formula and caps | verified (V-009, V-013) |
| R10.2 | §10 | Idempotent append-only ledger; wallet clamp | verified (V-013) |
| R11.1 | §11 | Player Card creation and public view | partial (display name API); UI not built |
| R11.2 | §11 | RP thresholds, finite 15,000 budget | verified (V-009) |
| R11.3 | §11 | 75 server-validated challenge predicates with equippable rewards | in progress (CH01, CH05 predicates; reward assets not built) |
| R12.1 | §12 | Cedar Lantern Terrace meet, 12-person instances | not started (meet text authored) |
| R12.2 | §12 | Avatars, emotes, inspection, photo mode, ribbon | not started |
| R13.1 | §13 | Shared AI controller with 19 composable tendencies | in progress (RouteFollower + parameter biases) |
| R13.2 | §13 | ≥4 measured behavioural differences per crew | not started |
| R14.1 | §14 | Route authoring (centreline, width, elevation, camber, gates, AI lines) | implemented as route.json + generator; visual editor tooling pending |
| R14.2 | §14 | Course uniqueness validator (≥70% exclusive centreline) | not started |
| R15.1 | §15 | Signal/Sector UI across all screens; accessibility | not started |
| R16.1 | §16 | Original soundtrack; engine audio families | not started |
| R16.2 | §16 | Tutorial T00 lessons | not started |
| R17.1 | §17 | Typed schemas, reference validator, coverage report | in progress |
| R17.2 | §17 | Editor tools: generation/validation, builds, smoke, capture | in progress (course/scene/car/material/content tools, BuildCommands) |
| R18.1 | §18 | Prediction/reconciliation; interpolated remotes; 60/30/20 Hz | implemented (RaceClient) — run pending |
| R18.2 | §18 | Server validation of inputs/progress; tick finish; ties | implemented (RaceServer, RaceClassification verified) |
| R18.3 | §18 | Network impairment test matrix | not started |
| R20.1 | §20 | Maintained docs | in progress |

## Content minimums (Appendices A–F)

| ID | Content | Required | Playable now | State |
|---|---|---|---|---|
| C.1 | Courses T00 + C01–C25 (authored, drivable, validated) | 26 | 1 (C01, autopilot-validated; scenery sparse) | in progress |
| C.2 | Campaign stages S01–S30 per mode | 60 | 0 (data + scripts authored) | in progress |
| C.3 | Rival identities (look, personality, livery, behaviour) | 48 | 0 in-game (48 character sheets/lines/livery specs authored) | in progress |
| C.4 | Distinct car models | 18 | 18 procedural first-pass bodies; distinctness review pending | in progress |
| C.5 | Challenges with unique cosmetic rewards | 75 | 2 predicates; 0 reward assets | in progress |
| C.6 | Cedar Lantern Terrace meet scene | 1 | 0 | not started |
| C.7 | Music set | ≥17 cues | 0 | not started |
