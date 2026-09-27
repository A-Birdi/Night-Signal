# Requirements ledger

States: **not started** · **in progress** · **implemented** (code/content exists, not yet proven) ·
**unverified** (exercised only partially or by simulation) · **verified** (see `VALIDATION.md` entry) · **blocked**.
Section numbers refer to `SPECIFICATION.md`. Content counts refer to playable, instantiated content, not data rows.

## Gate 0 — environment (§19)

| ID | Requirement | State | Evidence / note |
|---|---|---|---|
| G0.1 | Correct NEW repository/remote (A-Birdi/Night-Signal), not the RPG | verified | V-001 |
| G0.2 | Real Unity version/project path; editor access through the bridge | verified | V-001, V-003 |
| G0.3 | Editor baseline pinned (D-001: 6000.6.3f1) | verified | V-004, V-007 EditMode guard |
| G0.4 | URP configured; Graphics default explicit (D-002) | verified | V-004, V-006 |
| G0.5 | Optional AI/editor-control packages removed after dependency check (D-003) | verified | V-002 |
| G0.6 | First verified source push | verified | V-001 (`062bd57`) |
| G0.7 | Built-client smoke test (build + launch + render) | verified | V-005, V-006 |
| G0.8 | Local account/control/database stack (Supabase CLI + containers) | blocked | Docker/Supabase CLI not installed; owner approval needed |
| G0.9 | Server build targets: Windows dev server, Linux dedicated server | blocked (Linux) | Linux/Dedicated Server modules not installed |
| G0.10 | Git LFS objects uploaded and retrievable | verified | V-008 (re-verify whenever new LFS asset types appear) |
| G0.11 | Credentials scope documented (public vs server-only secrets) | not started | |

## Core rules and systems

| ID | Spec | Requirement | State |
|---|---|---|---|
| R1.1 | §1 | 1–6 simultaneous human entrants over a real network | not started |
| R1.2 | §1, §2 | Hard cap 6 registered active racers incl. live AI; no refill of DQ slots; replays never entrants | not started |
| R1.3 | §2 | Six-human campaign uses labelled non-colliding certified benchmark replay; no seventh racer | not started |
| R1.4 | §2 | 1–5 humans: fill with featured rival first, then support pool | not started |
| R1.5 | §2.5 | Benchmarks from legal reference runs (not typed-in times) | not started |
| R3.1 | §3.1 | URP, C#, Input System, uGUI+TextMeshPro single UI stack | in progress (URP/Input done) |
| R3.2 | §3.2 | Dedicated authoritative server process (NGO + Unity Transport) | not started |
| R3.3 | §3.2 | ASP.NET Core control plane (.NET LTS), HTTPS + authenticated control channel | not started |
| R3.4 | §3.2 | Supabase Auth; JWT verified via JWKS (signature/issuer/audience/expiry/subject) | not started |
| R3.5 | §3.2 | Short-lived single-use audience-bound match tickets | not started |
| R3.6 | §3.2 | Server-owned wallet/ownership/rank/challenge transactions, RLS/service roles | not started |
| R3.7 | §3.2 | Small interfaces: Identity, PlayerStore, ConvoyDirectory, MatchAllocator, ResultLedger, ContentCatalogue, GameplayTransport | not started |
| R3.8 | §3.2 | Local integration env runs real components; dev bypass impossible in production | blocked (needs local Supabase/Postgres) |
| R3.9 | §3.2a | Email/password lifecycle: verify, sign-in/out, reset, expiry recovery, deletion request | not started |
| R3.10 | §3.2a | One active driving session per account; takeover consent flow | not started |
| R3.11 | §3.2a | Guest/offline practice clearly labelled, never uploads earnings | not started |
| R3.12 | §3.3 | Leader is not authority; race survives leader loss | not started |
| R3.13 | §3.4 | Web target (secondary, later): WSS transport, CORS/Origin, hosted test | not started |
| R3.14 | §3.5 | Cost/operations worksheet after first six-client test | not started |
| R4.1 | §4 | Separate state machines: screen, presence, convoy coordination, entrant status | not started |
| R4.2 | §4.1 | Convoy create/join (invite code, friend invite), privacy labels, code expiry/rate limit | not started |
| R4.3 | §4.1 | Persistent convoy header (6 slots, leader crown+text, status, readiness) | not started |
| R4.4 | §4.2 | Two revisioned ready checks; invalidation rules; 15 s request limit; 120 s Away | not started |
| R4.5 | §4.3 | Loading barrier (90 s + one 30 s extension), server start tick, 3-2-1-GO | not started |
| R4.6 | §4.4 | Heartbeats 1/3/8 s; 250 ms input coast; DQ rules; spectate; 60 s slot hold; 15 s leader transfer | not started |
| R5.1 | §5.1 | Ordered frontier per mode; shared selection = min frontier; server validation | not started |
| R5.2 | §5.2 | Team success (Normal ≥1, Hard ceil(H/2)); support envelopes 1.50/1.35; deadlines | not started |
| R5.3 | §5.3 | Four acts, per-stage story beats, intros 10–18 s, quips, skip rules | not started |
| R5.4 | §5.4 | Hard campaign: alternate dialogue, lineups, conditions, Shiori finale | not started |
| R6.1 | §6 | Free-steering raycast-suspension arcade chassis, 60 Hz fixed step | not started |
| R6.2 | §6 | Drivetrain identities FWD/RWD/MR/AWD; assists; manual/auto; bindings per spec | not started |
| R6.3 | §6 | Cameras: chase/hood/bumper, FOV, shake, motion blur off by default | not started |
| R6.4 | §6.1 | Non-contact default; Light Contact; wall-hit onset + 0.75 s debounce; hold-reset +3.0 s | not started |
| R7.1 | §7 | Raw drift score formula, angleFactor curve, lineFactor, chain ≤3.0, anti-exploit | not started |
| R8.1 | §8 | Freeplay modes (Sprint, Circuit, Drift Attack, Time Trial/Ghost, Custom Cup); H+AI ≤ 6 | not started |
| R8.2 | §8 | Server-validated ghosts with full compatibility header; ≤3 overlays | not started |
| R9.1 | §9 | 3D garage, owned instances, presets (3 tune / 5 visual), purchase exactly-once | not started |
| R9.2 | §9 | Parts categories with tradeoffs; one utility slot (+4/+8% income or +5/+10% showcase) | not started |
| R9.3 | §9 | PI 100–999, classes D/C/B/A/S; measured stat harness | not started |
| R9.4 | §9 | Livery editor: 64 decal layers, ≥50 undo steps, bounded decal payload | not started |
| R10.1 | §10 | Economy formula B = 2500 + 22×expectedSeconds (clamped 120–420), multipliers, caps | not started |
| R10.2 | §10 | Idempotent append-only ledger; wallet cap 9,999,999 clamp | not started |
| R11.1 | §11 | Player Card creation and public view (no private data) | not started |
| R11.2 | §11 | RP thresholds and finite 15,000 budget; Living Legend extra check | not started |
| R11.3 | §11 | 75 challenges with server-validated predicates and unique equippable rewards | not started |
| R12.1 | §12 | Cedar Lantern Terrace meet (150×110 m), 12-person instances, arrival/departure flow | not started |
| R12.2 | §12 | Walking avatars, 12 emotes, inspection, photo mode, quick chat, SIGNAL ribbon | not started |
| R13.1 | §13 | Shared AI controller with 19 composable tendencies; same vehicle controller as humans | not started |
| R13.2 | §13 | ≥4 measured behavioural differences per crew with telemetry evidence | not started |
| R14.1 | §14 | Route authoring tool (centreline, width, elevation, camber, gates, AI lines) | not started |
| R14.2 | §14 | Course uniqueness validator (≥70% exclusive centreline) | not started |
| R15.1 | §15 | "Signal / Sector" UI language across all screens; accessibility options | not started |
| R16.1 | §16 | Original soundtrack set; engine audio families; mixing | not started |
| R16.2 | §16 | Tutorial T00 lessons, help index | not started |
| R17.1 | §17 | Typed schemas for all listed definitions; reference validator; coverage report | not started |
| R17.2 | §17 | Editor tools: content generation/validation, builds, smoke launch, capture | in progress (build + smoke) |
| R18.1 | §18 | Prediction/reconciliation for own car; interpolated remotes; 60/30/20 Hz | not started |
| R18.2 | §18 | Server validation of inputs/progress; tick-based finish, ties declared | not started |
| R18.3 | §18 | Network impairment test matrix | not started |
| R20.1 | §20 | Maintained docs: SPECIFICATION, CLAUDE, HANDOFF, REQUIREMENTS, VALIDATION, CONTENT_COVERAGE.json, BUILD_MANIFEST.json, docs/{ARCHITECTURE, ART_DIRECTION, ECONOMY, NETWORKING, HOSTING, UNITY_SETUP} | in progress |

## Content minimums (Appendices A–F)

| ID | Content | Required | Playable now | State |
|---|---|---|---|---|
| C.1 | Courses T00 + C01–C25 (authored, drivable, validated) | 26 | 0 | not started |
| C.2 | Campaign stages S01–S30 per mode (Normal + Hard) | 60 | 0 | not started |
| C.3 | Rival identities with look/personality/livery/behaviour | 48 | 0 | not started |
| C.4 | Distinct car models (unique bodies) | 18 | 0 | not started |
| C.5 | Challenges with unique cosmetic rewards | 75 | 0 | not started |
| C.6 | Cedar Lantern Terrace meet scene | 1 | 0 | not started |
| C.7 | Music set (title, garage, meet, 6 regional, 4 lieutenant, 2 finales, results ×2, tutorial) | ≥17 cues | 0 | not started |
