# Handoff

_Last updated: 2026-09-27 — online play through the menus; 6-client network slice; all 29 courses drivable._

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

## Done since the addenda (VALIDATION V-015 … V-028)

- Addendum 01 Core rules and data overlays; control plane for Addendum 01 + 02 incl. hosted diversions (307 .NET
  tests); light car contact, shared RaceSimulation (server + offline), protocol 2 per-client snapshots.
- **Network slice:** 2 humans + live rival (V-022) and 6 humans + live rival (V-025) pass on protocol 2 with 0 starved
  commands (2-tick input lead); fixed an input-send overflow, a watchdog crash on dormant rooms and replayed request ids.
- **Online play through the menus** (V-027): sign-in → convoy → Intent/Mode Ready/Enter/Propose/Event Ready/Start →
  server race → settled receipt → Continue/Advance, verified by an automated windowed tour against a real server.
- **Local play** (V-023): profiles, Offline hub, campaign map (painted region, act reveal, sliding stage panel), Local
  races judged by Core and saved atomically.
- **Courses:** all 29 scenes driven start to finish by the autopilot (V-026) after fixing circuit lap gates and terrain
  burying the lower road at crossings.
- Cores (engine-free, tested, in Assets/Game/Core): Builds (+ VehicleFactory parity), Toys, Profiles.

## Next actions

1. Unity presentation for the diversions (Pocket Circuit first — mandatory), client toy.* handling, While We Wait
   selector on the convoy screen; Garage (loadouts, references, Buy-and-Apply) on Core/Builds + Test Yard A/B.
2. Online convoy extras: Freeplay ballots/voting UI, friends panel, course purchase UI, Time Attack/Team Trials,
   6 + 6 Freeplay network run, reconnect/rejoin and DQ under load, the open native crash (V-027).
3. Generator: bridge decks/piers, tunnel shells, `crossing`/`water`/`field`/`structure` kits; visual pass per biome.
4. Content hash for parts/recipes/toys; visible customization; car art; meet + boombox; release builds and evidence.

## Recovery notes

- On resume: `git status`, `git log -1`, `git ls-remote origin refs/heads/dev/night-signal`,
  `mcpforunity://instances` / `project/info`, running Unity/player/dotnet processes, `Builds/NetRuns/*` logs.
- Build outputs live in `Builds/` (ignored). Raw player logs contain local paths — never commit them;
  copy JSON evidence into `Evidence/`.
- MCP `execute_code` is C# 6: pass `ref` for `in` parameters, no local functions.
