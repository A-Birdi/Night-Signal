# Handoff

_Last updated: 2026-09-27 — network slice passing on protocol 2; Local campaign playable; builds/toys cores landed._

**Rules revision:** `docs/brief/Night_Signal_Addendum_01.txt` supersedes parts of the master (six humans + up to
twelve vehicles, live finale rivals, light contact, course access, offline domain, rejoin grants, voting, friends,
OST). `docs/brief/Night_Signal_Addendum_02.txt` adds five diversions, Continue/Service Break, ≥ 8 loadouts per car
instance with protected references, the Garage Test Yard and meaningful upgrades. Effective rules and impact map:
`docs/EFFECTIVE_RULES.md`.

## Where things are

- Branch `dev/night-signal` (tracks `origin/dev/night-signal`). Never merge to `main` without approval.
- Unity 6000.6.3f1 editor open on this checkout, MCP for Unity 10.0.0 (loopback HTTP).
- Implemented and tested (see `VALIDATION.md`):
  - `Assets/Game/Core`: engine-free rules (economy, RP, frontier, stage outcome, grid cap, drift scoring, PI,
    classification), content catalogue + Appendix G validator, managed P-256 + match-ticket validator.
  - Content: brief catalogue imported to `Assets/Content/Data/generated`; authored overlays: car tuning, car
    bodies, story (48 rival sheets/lines, 30 Normal+Hard stage scripts, endings, radio records, crew diary, meet
    text, conditions).
  - Vehicle: fixed 60 Hz raycast chassis, handling harness, 18 procedurally modelled car bodies (first pass).
  - Courses: deterministic generation from `route.json` (D-007); C01 Tea Lantern Road drivable; autopilot finishes.
  - Control plane (`Services/`): identity (Supabase JWKS path + DevAuth), convoy/readiness, tickets, ledger,
    results settlement; 149 tests + ticket parity tests.
  - Netcode (`Assets/Game/Runtime/Net`): dedicated server host (register/poll/ack/results), authoritative
    RaceServer, predicted RaceClient, AutoClient automation, Boot scene role selection. First real multi-process
    race (server + 2 clients, localhost) passed end to end under the pre-addendum rules (V-016).
  - Addendum 01 Core (this revision): named capacity limits, RosterPlanner (authored live opposition, 12 vehicles,
    Team Trials, Time Attack non-contact), finale-only rivals, course access + purchases, ballot draw, handles,
    Team Trial scoring, live-rival stage condition, 12-slot grids (C01 approach lengthened, revision 2).

## How to run locally

```
powershell -ExecutionPolicy Bypass -File Tools/run/start-control-plane.ps1     # control plane on 127.0.0.1:5080
# build Builds/Game/NightSignal.exe (Unity: BuildCommands.BuildGame)
powershell -ExecutionPolicy Bypass -File Tools/run/net-race.ps1 -Humans 2 -Stage S01
```

## Blockers needing the owner

1. **Local backend stack** — Supabase CLI + a container runtime (Docker Desktop or compatible) are not
   installed; required for real local Supabase Auth/PostgreSQL (§3.2). The Postgres store/migrations/RLS are
   written but unexecuted. Local runs use DevAuth + SQLite (Development-only, loopback-only).
2. **Server build modules** — Unity Hub "Linux Dedicated Server Build Support" (and optionally "Windows
   Dedicated Server Build Support"). Meanwhile the server runs as the Windows player in `-batchmode -nographics`.
3. **Hosted services / internet test** — a Supabase project, a reachable server and a budget are needed for
   WAN acceptance (§3.5, Gate 5). Not approved; stays BLOCKED, not faked.

## Done since the addenda (VALIDATION V-015 … V-024)

- Addendum 01 Core rules and data overlays; control plane for Addendum 01 + 02 (282 .NET tests); light car contact,
  shared RaceSimulation (server + offline), protocol 2 per-client snapshots, 12-car in-process race; two real physics
  defects fixed (barrier depenetration never ran; tilted barrier normals vaulted cars).
- Front end: title, Local profiles (create/choose), Offline hub, **Local campaign map** (painted region map, nodes,
  act reveal, right panel with 220 ms slide + 8 px overshoot, Find Next Stage), Local races judged by Core and saved
  atomically, results with itemised progression; standalone UI tour passes (V-023).
- Network slice: 2 humans + live rival on protocol 2 passes end to end (V-022) after fixing a client input-send
  overflow and a watchdog crash on dormant rooms; 15 s server/client progress traces in every headless run.
- Cores (engine-free, tested, in Assets/Game/Core): Builds (parts, resolver, loadouts, references, quotes, upgrade
  paths), Toys (DowntimeSession + five diversions), Profiles (Local profile/progression/records/persistence).
- 29 course scenes; Test Yard facility + session; audio synthesis (24 cues) + soundtrack unlock table.

## Next actions

1. Re-measure the network slice with the 2-tick input lead; then 6 humans + live rival, 6 + 6 Freeplay; reconnect,
   DQ and contact under load. Re-capture the campaign tour screenshots after the layout fixes.
2. Integrate Core/Builds into VehicleFactory (parity test first), Garage screens and the Test Yard A/B flow; add
   parts/recipes to the content hash.
3. Host DowntimeSession in the control plane (pause on match commit, snapshots, dormant rooms) and build the
   tabletop presentation for the five toys (Pocket Circuit first — mandatory).
4. Online convoy UI: intent strip, Mode/Event Ready, voting, friends, course access/purchase, Time Attack, Team
   Trials, Continue / Service Break strip, While We Wait selector.
5. Course sweep (PlayMode CourseSweepTests over 29 courses) and generator gaps; visible customization, car art pass,
   meet + boombox; release builds and evidence.

## Recovery notes

- On resume: `git status`, `git log -1`, `git ls-remote origin refs/heads/dev/night-signal`,
  `mcpforunity://instances` / `project/info`, running Unity/player/dotnet processes, `Builds/NetRuns/*` logs.
- Build outputs live in `Builds/` (ignored). Raw player logs contain local paths — never commit them;
  copy JSON evidence into `Evidence/`.
- MCP `execute_code` is C# 6: pass `ref` for `in` parameters, no local functions.
