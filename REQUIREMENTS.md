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
| R1.1 | §1, A01 D01 | 1–6 simultaneous human entrants over a real network | verified on localhost — 2 and 6 client processes on protocol 2 (V-022, V-025); remote/WAN runs blocked (no hosted server) |
| R1.2 | A01 §1 _(A01)_ | Named limits: ≤6 convoy humans, ≤6 event humans, ≤12 race vehicles, ≤6 meet humans; DQ never refilled by AI; spectators/replays never entrants | verified — unit-tested; 12-vehicle network run (6 humans + 6 AI, V-035) |
| R1.3 | A01 §1.3 _(A01)_ | ~~Six-human benchmark replay~~ superseded: featured rival always live; finales are H+1 duels; encounter stages require beating the live rival | implemented (RosterPlanner, StageOutcome) — unit-tested |
| R1.4 | A01 §1.2 _(A01)_ | Authored live opposition per stage (featured first); humans never displace it | implemented (stages.opposition.json, control plane, server) |
| R1.5 | §2.5 | Benchmarks from legal reference runs | not started (control plane uses labelled provisional benchmarks) |
| R1.6 | A01 §2 _(A01)_ | Bounded light car-to-car contact by default; Time Attack non-contact; no damage (D10) | verified — in-process 12 cars (V-019) and 6 humans + rival over the network, 4–9 contacts per car, 0 resets (V-025) |
| R1.7 | A01 §12 _(A01)_ | R40/R48 finale-only, rejected server-side in every other placement | implemented (FinalRivals, RosterPlanner, RaceServer, validator) — unit-tested |
| R1.8 | A01 §5 _(A01)_ | Course access ledger: starters, 45k purchase or regular-stage clear, C25 reward, FP01–03 purchases, guest passes | implemented — Core rules, control plane ledger + idempotent purchase endpoint (tests), Courses screen purchase verified against the server (V-037); Local-domain purchase UI pending |
| R1.9 | A01 §6 _(A01)_ | Mode Ready → vote (server deadline, one-ticket-per-ballot draw, frozen) → Event Ready; group Time Attack | implemented — Core ballot rules, control plane ballots (tests), convoy-screen vote verified with one human against a real server (V-036); Freeplay Time Attack (non-contact) raced and settled through the convoy screen (V-044) — not yet run with several humans |
| R1.10 | A01 §3 _(A01)_ | Three Team Trials (6 v 6), no mastery RP | verified online — TT_BEST and TT_MEAN (V-044), TT_DRIFT with race drift scoring and drifting AI, settled 10,900 vs 18,668 pts (V-047); trial targets and the drift course remain the provisional fixture |
| R1.11 | A01 §8 _(A01)_ | Main menu, full separate Local profile, Go Online boundary | in progress — Local profiles, Offline hub, Local campaign map + races + progression verified in the player (V-023); Go Online boundary not started |
| R1.12 | A01 §9–10 _(A01)_ | @handles, friends panel, rejoin grants keyed to leadership epoch (no reserved seat) | implemented — control plane handles/friends/invites/rejoin grants (tests); Friends screen + convoy invitation verified with two clients (V-037); block UI pending |
| R1.13 | A01 §11 _(A01)_ | 24 authored cues, unlock manifest, shared meet boombox | in progress — 24 synthesized cues; music.unlocks.json in the content hash; unlocks granted online (V-022) and locally (V-023); boombox pending |
| R1.14 | A01 §13 _(A01)_ | Visible per-car customization families, 8+ rim designs | partial — renderer: body-kit variants per family, two-tone, finishes, 8 rims, conformed decals and plate text (V-042); Core catalogue/livery/validation/editor/wire for all 18 chassis (V-045); Garage Appearance screen (all families, wheels, paint, lamps, plate, decal layers, presets, undo), Local and online apply with ownership, liveries in race rosters — Local and online tours PASS (V-046); a second human viewing another's livery not yet run |
| R2.1 | A02 §1–6 _(A02)_ | Five diversions (Cap Clash, Pit-Crew, Greenlight, Pocket Circuit, Canvas), 1–6 humans + solo, preemption/resume, dormant persistence | verified with 1–2 humans — Core + hosting (V-028), all five screens Local (V-034), online: Pocket Circuit while ready (V-030), Greenlight / Cap Clash / Pit-Crew / Canvas with two humans (V-043); 3–6 humans at one table not yet exercised |
| R2.2 | A02 §7 _(A02)_ | Continue / Service Break post-event flow | verified — control plane tests + online tour Continue → Advance → next stage (V-027) |
| R2.3 | A02 §9 _(A02)_ | ≥ 8 mechanical loadouts per car instance, ≥ 5 visual presets, 3 protected references, atomic whole-build apply/restore | implemented — Core/Builds (223 tests), Local profile schema 2 + Garage screen (V-038), online `/v1/me/garage` with idempotent settlement (329 control plane tests, V-040) and the Garage screen online (V-041); visual-preset UI pending |
| R2.4 | A02 §10 _(A02)_ | Garage Test Yard: drivable, same controller, A/B, preview unowned parts, no progression | implemented (Local) — T00 campus stations, A/B reset-and-drive, dry/wet, last 3 runs per side, notes/prefer marker (V-039); online yard entry pending with the online Garage UI |
| R2.5 | A02 §8, master §9 _(A02)_ | Parts/tuning system with real tradeoffs; favourite-car upgrade path for all 18 cars; calibrated pacing | in progress — 67 parts, resolver, recipes for 18 cars; Local races drive the applied build; T2 engine/tyres/brakes measurably improve all three starters in the vehicle simulation (V-038); a part bought online races with a server-verified build hash (V-041); tuning UI, full starter progression runs (F08–F10) pending |
| R2.6 | A02 D208 _(A02)_ | 24 h dormant convoy for all-disconnected case | in progress (control plane) |
| R3.1 | §3.1 | URP, C#, Input System, uGUI+TMP single UI stack | implemented — URP, C#, Input System; uGUI + TMP front end and race HUD used by every evidence tour (V-027 … V-047) |
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
| R4.2 | §4.1 | Convoy create/join, invites, privacy, expiry/rate limit | verified — control plane (V-013) and the client convoy screen/Friends invites (V-027, V-037) |
| R4.3 | §4.1 | Persistent convoy header UI | not started |
| R4.4 | §4.2 | Two revisioned ready checks, invalidation, 15 s / 120 s rules | verified in control plane (V-013) |
| R4.5 | §4.3 | Loading barrier (90 s + 30 s), server start tick, 3-2-1-GO | implemented (RaceServer) — run pending |
| R4.6 | §4.4 | Heartbeats, 250 ms coast, DQ, spectate, slot hold, leader transfer | partial: coast/DQ implemented; spectate/reconnect UI not started |
| R5.1 | §5.1 | Ordered frontier, shared selection, server validation | verified (V-009, V-013) |
| R5.2 | §5.2 | Team success, support envelopes, deadlines | verified rules (V-009); server deadline implemented |
| R5.3 | §5.3 | Acts, stage beats, intros/quips, skip rules | in progress (all text authored; presentation not built) |
| R5.4 | §5.4 | Hard campaign alternate dialogue, conditions, Shiori finale | in progress (text + conditions authored) |
| R6.1 | §6 | Free-steering raycast arcade chassis, 60 Hz | verified by harness/autopilot (V-011, V-012); human feel unverified |
| R6.2 | §6 | Drivetrain identities, assists, bindings | implemented — Settings → Controls remaps every driving action (keyboard + controller), warns shared bindings, restores defaults; typing releases driving input (V-050) |
| R6.3 | §6 | Cameras chase/hood/bumper, FOV, shake, no blur | implemented |
| R6.4 | §6.1 | Non-contact default, wall-hit debounce, hold-reset +3 s | implemented — superseded by Addendum 01 light contact (R1.6 verified); Time Attack non-contact; wall-hit debounce and hold-reset penalty in the race tracker |
| R7.1 | §7 | Raw drift scoring with anti-exploit | verified rules (V-009); not yet wired to races/gates |
| R8.1 | §8 | Freeplay modes; H+AI ≤ 6 | rules verified; modes not built |
| R8.2 | §8 | Server-validated ghosts | not started |
| R9.1 | §9 | 3D garage, presets, purchases exactly-once | purchases verified server-side; garage not built |
| R9.2 | §9 | Parts with tradeoffs; utility slot | in progress — see R2.5 (67 parts with tradeoffs, utility slot in Core/Builds) |
| R9.3 | §9 | PI classes; measured stat harness | classes verified; harness implemented (V-011) |
| R9.4 | §9 | Livery editor (64 layers, undo) | verified — Core LiveryEditor (64 layers, 64-step undo, tests) and the Garage Appearance screen, Local + online (V-045, V-046) |
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

## Addendum 03 — instruments, cameras, motion, elevation, gates, recovery (`docs/brief/Night_Signal_Addendum_03.txt`)

| ID | Source | Requirement | Status |
|---|---|---|---|
| A3.1 | §1, D301, G01–G08 | Instrument Dial + Digital Strip, km/h/mph from one canonical m/s, calibrated scales, RPM separate, smoothing, preferences + migration | implemented — `SpeedDisplay`, `SpeedCluster`, `DrivingPreferences`, road-speed telemetry; G01–G03, G07, G08 executed (V-050: `InstrumentTests`, built `-nsInstrumentTour` ×3); pending G04 extremes in a built race, G05 shift/speed review, G06 720p/ultrawide/large-text layouts |
| A3.2 | §2, D302/D304, C01–C05 | Five views (Chase Close/Far, Hood, Bumper, genuine fitted Cockpit) on all 18 cars, per-model anchors, cycling/look-back, persistence, temporary overrides | implemented — `DrivingCamera`, per-car `CabinFrame` anchors, `CockpitBuilder` + open-cabin body, Settings → Controls remapping; V-050 camera tour 90/90 car×view driven with contact sheets, remap to V, controller cycling, typing guard, look-back; pending spectator target loss, replays/cinematic overrides, human visual review |
| A3.3 | §3–4, D303, C06–C11 | Arcade tracking, drift framing, impacts, speed-FOV, perimeter speed lines; Arcade/Comfort/Custom presets; Reduced Motion honoured | implemented — V-050 `CameraTests` (C04–C08, C10) pass; C09 occlusion measured in every view at FOV 50°/80° incl. the C08 tunnel, C11 30/60/120 fps offline and online, C12 six rendered clients with different views/styles/units (V-052); pending correction stress under packet loss, bridge supports |
| A3.4 | §5, D305, T01–T08 | Real elevation/banking on every full-size course; measured 3D profile report; circuit seams; flat facilities stay flat | in progress — all 29 measured (V-049 `CourseProfileTests`), relief added to C14/FP03/T00; tunnels/galleries built for all eight authored sections (V-051 `TunnelShellTests`); pending counter-slopes/compressions on descents and bridge/viaduct decks |
| A3.5 | §6, D307, R01–R05 | Ordered directional finite 3D gates, legal continuity, stacked roads/overpasses, lap arming, ranking by legal progress | implemented — V-049 `RouteProgressTests` R01–R05 and `CourseSweepTests` 29/29 (re-run in V-050) |
| A3.6 | §7, D306, R06–R12 | Safe recovery: prompt/automatic rescue by route state (no global Y), validated anchors at/behind the last gate, idempotent events, one +3 s penalty, ≤ 2 s ghost | implemented offline/server — anchors, events, penalty, ghost cap (V-049); HUD offer/countdown/hold progress, overturned rescue, stopped-car offer, physical fall/roof PlayMode tests (V-050); R08 simultaneous recoveries tested, R07 holds by construction (V-052); pending online off-route countdown, R11 network loss after recovery |
| A3.7 | §8.2, I01 | Test Yard uses the same views/HUD without touching loadouts, A/B, readiness | implemented — same `DrivingCamera`, cluster, speed lines and recovery display; A/B swap keeps the view; exit moved to Pause/menu (Select conflict fixed) — V-050 camera tour yard section |
| A3.8 | §8.1, I02–I06 | Versioned geometry/records, migration, soak, real client/server evidence | partial — course revisions bumped for relief (V-049), `ScoringVersion` classify-2 (V-050); pending soak, stored-record migration report, client/server runs of the new camera/recovery paths |
