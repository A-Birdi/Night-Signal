# Handoff

_Last updated: 2026-09-27 — Addendum 03 slice 2: instruments, five driving views with a fitted cockpit on all 18 cars, arcade camera, remappable controls, recovery prompts (V-050)._

**Rules revision:** `docs/brief/Night_Signal_Addendum_01.txt` supersedes parts of the master (six humans + up to
twelve vehicles, live finale rivals, light contact, course access, offline domain, rejoin grants, voting, friends,
OST). `docs/brief/Night_Signal_Addendum_02.txt` adds five diversions, Continue/Service Break, ≥ 8 loadouts per car
instance with protected references, the Garage Test Yard and meaningful upgrades. `docs/brief/Night_Signal_Addendum_03.txt`
adds two speedometer styles, five mandatory driving views (genuine cockpit), arcade camera motion with comfort
controls, measured elevation, finite 3D gates and safe recovery — topology/progress/recovery before benchmark
certification. Effective rules and impact map: `docs/EFFECTIVE_RULES.md`.

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

Evidence tours (built player, run from the repo root; each uses its own profile/preferences folders under `Builds/`):
`-nsUiTour`, `-nsYardTour`, `-nsAppearanceTour`, `-nsInstrumentTour`, `-nsCameraTour` (all 18 cars × 5 views driven,
contact sheets + ledger), always with `-nsPrefsFolder Builds/Screenshots/<tour>/prefs`. Editor: *Night Signal → Art →
Render Cockpit Sheets* renders the mounted views of every car.

## Blockers needing the owner

1. **Local backend stack** — Supabase CLI + a container runtime (Docker Desktop or compatible) are not
   installed; required for real local Supabase Auth/PostgreSQL (§3.2). The Postgres store/migrations/RLS are
   written but unexecuted. Local runs use DevAuth + SQLite (Development-only, loopback-only).
2. **Server build modules** — Unity Hub "Linux Dedicated Server Build Support" (and optionally "Windows
   Dedicated Server Build Support"). Meanwhile the server runs as the Windows player in `-batchmode -nographics`.
3. **Hosted services / internet test** — a Supabase project, a reachable server and a budget are needed for
   WAN acceptance (§3.5, Gate 5). Not approved; stays BLOCKED, not faked.

## Done since the addenda (VALIDATION V-015 … V-050)

- Addendum 01 Core rules and data overlays; control plane for Addendum 01 + 02 incl. hosted diversions (307 .NET
  tests); light car contact, shared RaceSimulation (server + offline), protocol 2 per-client snapshots.
- **Network slice:** 2 humans + live rival (V-022), 6 humans + live rival (V-025) and 6 humans + 6 AI = 12 cars
  (V-035) pass on protocol 2 with 0 starved commands (2-tick input lead).
- **Online play through the menus** (V-027): sign-in → convoy → Intent/Mode Ready/Enter/Propose/Event Ready/Start →
  server race → settled receipt → Continue/Advance; automated windowed tour against a real server. Freeplay course
  vote (open → ballot → server deadline → draw → proposal) through the same screen (V-036).
- **Friends and course access** (V-037): Friends screen (usernames, requests, presence, invite/join) and Courses
  screen (two-press purchase, idempotent) verified with two real clients.
- **Local play** (V-023): profiles, Offline hub, campaign map, Local races judged by Core and saved atomically.
- **While We Wait** (V-029 … V-034): all five diversions playable through real screens — Pocket Circuit, Greenlight,
  Cap Clash, Pit-Crew Project, Convoy Canvas — on one `ToyConnection` (Local in-process session or the convoy's hosted
  session). Online verified for Pocket Circuit at the shared table while Event Ready (readiness kept).
- **Courses:** all 29 scenes driven start to finish by the autopilot (V-026).
- Cores (engine-free, tested, in Assets/Game/Core): Builds (+ VehicleFactory parity), Toys, Profiles. Local Garage
  (profile schema 2: parts owned per car instance, ≥ 8 loadouts / 5 presets / 3 protected references per instance,
  quote → Buy-and-Apply settled once, Last Race Build) in `Core/Profiles/LocalGarage.cs`; **Garage screen** (Local)
  verified in the UI tour, Local races drive the applied build and record Last Race Build (V-038).
- **Test Yard + tuning** (V-039): A/B reset-and-drive on the T00 campus, dry/wet, run observations; tuning page.
- **Online Garage (control plane)** and frozen builds racing online with a verified BuildHash; two humans at the hosted
  Cap Clash, Pit-Crew and Canvas; control requests now go through one ordered send loop (V-040).
- **Online Garage UI** on the same screen (backend switch); a part bought online races with a server-verified hash;
  parts and recipes are in the content hash (V-041).
- **Customization renderer** (V-042): body-kit variants per family, two-tone, finishes, 8 rims, decals conformed to the
  body, plate text; evidence sheets in `Evidence/art/customization/`.
- **All five diversions online** with two humans; the client compares its toy-document hash with the control plane at
  sign-in and keeps the shared tables closed on a mismatch (V-043).
- **Team Trials + Time Attack online** through the convoy screen: TT_BEST, TT_MEAN (1 human + 5 friendly AI vs 6),
  Freeplay Time Attack (V-044). TT_DRIFT needs race-server drift scoring (not built).
- **Customization Core** (V-045): `Core/Customization` + `authored/customization.json` — catalogue, livery document and
  hash, validation with ownership, editor (undo, 64 layers), resolver, wire form.
- **Garage Appearance** (V-046): Appearance screen with a live preview; Local apply (profile) and online apply (control plane
  validates with `cosmetics_owned`, stores canonical livery + hash); liveries travel in race rosters (`entrants[].livery` →
  game server → clients) and show on the race car. `/healthz` publishes `customizationContentHash`.
- **Drift** (V-047): Core `DriftScorer` fed by `Race/DriftJudge` for every car; Drift Attack ranks by banked raw score;
  AI drift the judged zones in drift formats; HUD drift readout; Freeplay Drift Attack and TT_DRIFT settled online.
- **Progression measured** (V-048): starter paths, 30 Normal reference runs, wet-grip planning for AI and prediction.
- **Addendum 03 slice 1** (V-049): measured 3D profiles of all 29 courses, finite directional 3D gates, layer-aware
  progress, legal-progress ranking, safe non-forward recovery anchors, recovery events, ≤ 2 s ghost.
- **Addendum 03 slice 2** (V-050): Instrument Dial / Digital Strip in km/h or mph; five driving views on all 18 cars
  with a fitted cockpit (open-cabin body, live binnacle, turning wheel); arcade camera (drift framing with hysteresis,
  bounded impacts, speed FOV, perimeter speed lines, Arcade/Comfort/Custom, Reduced Motion); Settings → Controls
  remapping; typing never drives; recovery offers/countdowns on the HUD, overturned rescue, physical fall/roof tests;
  Test Yard on the same camera/HUD.

## Next actions

0. Addendum 03 remaining (before benchmark certification; tunnels V-051, camera timing/occlusion and six-client views
   V-052, readability and bridges V-053 are done): correction stress under
   packet loss; online off-route countdown and R11 (recovery then network loss/rejoin); G04/G05 instrument checks in built races;
   counter-slopes on the long descents; soak and record migration report (A3.8); then F08 and benchmark certification.
1. Customization follow-ups: two humans in one race seeing each other's liveries; meet refresh of liveries; pearl flip
   tint; rename/delete presets in the UI (the ops exist).
2. F08–F10 starter progression runs with upgrades; drift skill per AI profile and campaign drift benchmarks; restrict
   Drift Attack to courses with judged zones; group Time Attack and toy tables with 3–6 humans; block UI,
   reconnect/rejoin and DQ under load; Canvas controller cursor.
3. Generator: bridge decks/piers, `crossing`/`water`/`field`/`structure` kits; visual pass per biome.
4. Car art; meet + boombox; release builds and evidence.

## Recovery notes

- On resume: `git status`, `git log -1`, `git ls-remote origin refs/heads/dev/night-signal`,
  `mcpforunity://instances` / `project/info`, running Unity/player/dotnet processes, `Builds/NetRuns/*` logs.
- Build outputs live in `Builds/` (ignored). Raw player logs contain local paths — never commit them;
  copy JSON evidence into `Evidence/`.
- MCP `execute_code` is C# 6: pass `ref` for `in` parameters, no local functions.
