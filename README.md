# Night Signal: Mountain Circuit

An original full-3D arcade racing game: night touge roads across six Japan-inspired regions, a 30-stage campaign told
through 48 rival drivers and their crews, a walkable car meet (Cedar Lantern Terrace) between races, and convoys of up to
six friends racing together. Unity 6000.6.3f1 (Windows first), an authoritative dedicated game server, and an ASP.NET Core
control plane.

**Status: pre-release, not yet released.** Everything listed in `VALIDATION.md` was executed on one Windows PC by
automation (built players, tests, loopback networking). No test with independent people, other devices, a LAN or the
internet has been run, and those stay marked blocked until they are (`docs/GATE4.md`, Gate 5 checklist).

## Playing (a build of this repository)

The player is `Builds/Game/NightSignal.exe` after building it in the Unity editor (`NightSignal.Editor.Build.BuildCommands`
→ `BuildGame()`); there is no published download.

- **Offline Play** needs nothing else: create a Local profile, take a starter car, and play the campaign, Freeplay (race,
  Time Attack, Drift Attack on courses with judged zones), the Driving School, challenge trials, the Garage and its Test
  Yard, the Player Card and the offline meet. Local profiles are separate from online accounts and never mix with them.
- **Online** needs the control plane, and a dedicated game server for races. Both are development services that run on
  this machine only (127.0.0.1); there are no public servers. See "Running locally" below.

### Controls (defaults; every action can be rebound under Controls)

| Driving | Keyboard | Gamepad |
|---|---|---|
| Steer | A / D or ← / → | Left stick |
| Throttle / brake (reverse) | W / S or ↑ / ↓ | Right / left trigger |
| Handbrake | Space | A (south) |
| Shift up / down (manual gearbox) | E / Q | Right / left shoulder |
| Change view | C | View / Select |
| Look back | B | Right stick press |
| Reset to the road | R | Y (north) |
| Pause / menu | Esc | Start |

| At the meet | Keyboard | Gamepad |
|---|---|---|
| Walk / jog | W A S D, Shift to jog | Left stick, press to jog |
| Interact | E | A |
| Emote wheel | T (or 1–0, -, =) | RB |
| Quick chat | V | LB |
| Recenter camera | C | Right stick press |
| Photo mode | P | D-pad up |
| Rescue to your car | hold R | hold Y |
| Menu | Esc | Start |

### Settings worth knowing

Settings holds the driving view (chase close/far, hood, bumper/road, cockpit — every car has a fitted cockpit), field of
view, a motion preset and its parts (camera roll, impact shake, motion blur, speed lines, speed field of view), the
speedometer (instrument dial or digital strip) and speed units, text size, HUD size, high contrast and reduced motion. Appearance is visual
only: nothing on the Player Card or in the Garage's Appearance screen changes how a car drives.

### Where things are saved

Local profiles and settings are under Unity's per-user data folder for the game
(`%USERPROFILE%\AppData\LocalLow\<company>\Night Signal\` on Windows: `LocalProfiles\` and `settings\`). Online progress
lives on the control plane's database.

## Running locally (development)

```
powershell -ExecutionPolicy Bypass -File Tools/run/start-control-plane.ps1      # control plane on 127.0.0.1:5080
powershell -ExecutionPolicy Bypass -File Tools/run/net-race.ps1 -Humans 2 -Stage S01   # clients + a game server on 127.0.0.1
```

Development accounts come from `Backend/seed/dev-accounts.example.json`. Every network harness is loopback-only by default
and guarded by `Tools/run/NetGuard.psm1`; a LAN run needs `-AllowLan` and the owner's authorisation for that run, and no
script ever changes firewall rules (Addendum 04). Evidence tours run a built player with its own profile and settings
folders under `Builds/` (`Tools/run/tour.ps1 -Tour <Name>`, `Tools/run/bounds-audit.ps1`).

## Repository map

- `SPECIFICATION.md` — the master specification, revised by `docs/brief/Night_Signal_Addendum_01…04.txt`; the effective
  rules and decisions are in `docs/EFFECTIVE_RULES.md` and `docs/DECISIONS.md`.
- `HANDOFF.md` (current state, next actions, blockers), `REQUIREMENTS.md` (the requirement ledger), `VALIDATION.md` (what
  was executed, at which revision), `docs/GATE4.md` (integration audit and the Gate 5 release checklist).
- `Assets/Game/Core` — engine-free rules shared with the server; `Assets/Game/Runtime` — the game; `Assets/Game/Editor` —
  tools and builds; `Assets/Tests` — EditMode/PlayMode tests; `Assets/Content` — courses and data.
- `Services/` — the control plane and its tests; `Backend/` — migrations and seed data; `Tools/` — run scripts, QA reports
  (content coverage, the build manifest), art generators; `Evidence/` — screenshots, logs and reports cited by
  `VALIDATION.md`.
- Asset sources and licences: `docs/PROVENANCE.md`.
