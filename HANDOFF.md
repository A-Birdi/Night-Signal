# Handoff

_Last updated: 2026-09-27 — Addendum 01 reconciliation (Priority A: rules/schema/network) in progress._

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

## Done since the addenda (VALIDATION V-015 … V-021)

- Addendum 01 Core rules and data overlays; control plane for Addendum 01 + 02 (281 .NET tests); light car contact,
  shared RaceSimulation (server + offline), protocol 2 per-client snapshots, 12-car in-process race; two real physics
  defects fixed (barrier depenetration never ran; tilted barrier normals vaulted cars).
- Front end: title (Online Login / Offline Play / Settings / Quit), offline practice races, results; standalone UI tour.
  29 course scenes authored from the new routes; Test Yard facility + session; audio synthesis (24 cues).

## In flight (background agents, engine-free, staged outside Assets until they compile)

- `Services/BuildsCore` → Core/Builds: parts catalogue, tuning, BuildResolver, loadouts (8 per instance), visual
  presets, protected references, Buy-and-Apply quotes, favourite-car upgrade paths.
- `Services/ToysCore` → Core/Toys: authoritative simulations for the five diversions and DowntimeSession.
- `Assets/Game/Core/Profiles` (already compiles): Local profile, progression, records, atomic persistence.

## Next actions

1. Unity client on the new control protocol (AutoClient done; interactive convoy UI next); re-run the network slice:
   2 humans + AI, 6 humans + live rival, 6 + 6; reconnect/DQ/contact.
2. Course sweep: autopilot every course; generator gaps (tunnels/viaducts/crossings, new landmark kits, biomes).
3. Integrate Core/Builds into VehicleFactory + Garage (A/B Test Yard, loadouts, references); Local profile into Offline
   Play; host the toys in the control plane and build their tabletop presentation.
4. Campaign map + right panel + records, intent strip, friends, course access, voting, Time Attack, Team Trials UI;
   Continue / Service Break results strip.
5. Visible customization, car art pass, meet, OST boombox; release builds and evidence.

## Recovery notes

- On resume: `git status`, `git log -1`, `git ls-remote origin refs/heads/dev/night-signal`,
  `mcpforunity://instances` / `project/info`, running Unity/player/dotnet processes, `Builds/NetRuns/*` logs.
- Build outputs live in `Builds/` (ignored). Raw player logs contain local paths — never commit them;
  copy JSON evidence into `Evidence/`.
- MCP `execute_code` is C# 6: pass `ref` for `in` parameters, no local functions.
