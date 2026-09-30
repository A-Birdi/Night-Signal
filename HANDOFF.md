# Handoff

_Last updated: 2026-09-28 — the meet, offline (V-073): Cedar Lantern Terrace, walking, twelve emotes, host, boombox;
the meet online with three real clients (V-074), the convoy at the meet (V-075), race from the meet and back plus
walking controls (V-076), the Player Card with a driver appearance (V-077), race audio (V-078), Drift Attack only where zones exist (V-079), AI drift skill (V-080), group Time Attack and tables with 4/6 humans (V-081, V-082), Canvas controller pen (V-083), the meet's touring challenges CH61–CH65 and CH67 (V-084), CH33/CH35/CH44/CH45/CH66/CH71 (V-085), car levels of detail (V-086), the contact-prediction experiment (V-087, negative), preset rename/delete (V-088), the meet showing applied liveries (V-089), challenge gates CH03/CH06/CH09 and CH16 (V-090), the Driver Card offline (V-091), card style (V-092), showcase records (V-093), the meet under latency (V-094), drops/refused re-entry/spectating in a full impaired race and the offline showcase (V-095), racecraft CH31/CH32 and the HUD gap (V-096), CH48 Sign Your Car and the offline meet's livery fix (V-097), CH50 Change Without Losing (V-098), the campaign story on screen offline — intros, reactions, race diary (V-099) — and online (V-100), the endings and Shiori's epilogue / CH75 (V-101), the diary's read marks and CH70 (V-102), offline ghosts and CH68 (V-103), online ghosts and CH68 online (V-104), the course-uniqueness report (V-105), crew behaviour telemetry with tendency behaviours (V-106), a convoy member's shared ghost on the server (V-107), benchmarks recertified after the tendency behaviours (V-108), the convoy-member and rival-reference ghosts and CH38/CH73 (V-109), the offline route/elevation chart (V-110), the T00 Driving School and the crew telemetry at the true apexes (V-111), the route chart online (V-112), the published challenge references (V-113), the Gate 4 audit (V-114), the Custom Cup offline (V-115), the Custom Cup online with rival drivers read from the roster in Freeplay settlement and a game server that unloads the previous course (V-116), campaign across mixed progress in built players with locked stages and the neutral shared-frontier line (V-117), a bound test of every string the offline tours pass (V-118), challenge trials with supplied loaners offline — CH11, CH25, CH28, CH30, CH51, CH54, CH55 (V-119) — and online (V-120), CH13 with a fixed Gold ghost (V-121).
Now: the gameplay backlog._

**Rules revision:** `docs/brief/Night_Signal_Addendum_01.txt` supersedes parts of the master (six humans + up to
twelve vehicles, live finale rivals, light contact, course access, offline domain, rejoin grants, voting, friends,
OST). `docs/brief/Night_Signal_Addendum_02.txt` adds five diversions, Continue/Service Break, ≥ 8 loadouts per car
instance with protected references, the Garage Test Yard and meaningful upgrades. `docs/brief/Night_Signal_Addendum_03.txt`
adds two speedometer styles, five mandatory driving views (genuine cockpit), arcade camera motion with comfort
controls, measured elevation, finite 3D gates and safe recovery — topology/progress/recovery before benchmark
certification. `docs/brief/Night_Signal_Addendum_04.txt` makes local network testing loopback-first (server bind
127.0.0.1, LAN only with explicit opt-in, never automate the firewall). Effective rules and impact map:
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
  - Vehicle: fixed 60 Hz raycast chassis, handling harness, 18 detailed procedural car bodies (art pass V-070: glass
    and pillars, projected lamp clusters/grilles/intakes, arch lips and liners, mirrors, shut lines, interior
    silhouette, tyres/rims/brakes; per-car Appendix C cues in `authored/cars.body.json`), fitted cockpits and five
    views on all 18, three levels of detail per car (V-086). Distinctness sheets: editor *Night Signal → Art → Render
    Car Sheets* (`CarSheet`); forced LOD sheets: *Render Car LOD Sheets*.
  - Courses: deterministic generation from `route.json` (D-007); all 29 scenes driven by the autopilot, measured 3D
    profiles, tunnels, bridges, relief; every route landmark built by the parametric kits and a regional kit per biome
    (V-071, `docs/COURSES.md`); editor *Render Landmark Sheets* / `LandmarkSheet.RenderRegions` for review.
  - Control plane (`Services/`): identity (Supabase JWKS path + DevAuth), convoy/readiness, tickets, ledger,
    results settlement, garage, customization, toys; 335 .NET tests. Unity EditMode 255 tests.
  - Netcode (`Assets/Game/Runtime/Net`): dedicated server host (register/poll/ack/results), authoritative
    RaceServer (bind `-nsBindHost`, loopback by default), predicted RaceClient, AutoClient automation, spectating.
  - Benchmarks: all 60 stage sides certified from reference runs under their authored conditions
    (`authored/stage-benchmarks.json`, `authored/stage-conditions.json`), S29 Four Signals judged.
  - Addendum 01 Core (this revision): named capacity limits, RosterPlanner (authored live opposition, 12 vehicles,
    Team Trials, Time Attack non-contact), finale-only rivals, course access + purchases, ballot draw, handles,
    Team Trial scoring, live-rival stage condition, 12-slot grids (C01 approach lengthened, revision 2).

## How to run locally

```
powershell -ExecutionPolicy Bypass -File Tools/run/start-control-plane.ps1     # control plane on 127.0.0.1:5080
# build Builds/Game/NightSignal.exe (Unity: BuildCommands.BuildGame() — non-development, the canonical automation build)
powershell -ExecutionPolicy Bypass -File Tools/run/net-race.ps1 -Humans 2 -Stage S01   # server on UDP 127.0.0.1:7777
```

The control plane is a **long-lived development service**: leave it running while it is healthy (process alive,
TCP listener only on 127.0.0.1:5080, `GET /healthz` ok, no error/restart loop in its log, `contentHash` equal to the
content the next run uses). Restart it once, cleanly, only when it is unhealthy or its content hash is stale after a
content change (content documents feed the hash; a mismatched game server is refused), and after a change to its own
code. Last restart: 2026-09-29 for the V-113 content (published challenge references), race content hash `aff0ce06…`,
customization `306dd38e…`. Network harnesses go through
`Tools/run/NetGuard.psm1` (loopback unless `-AllowLan`, free-port check, socket evidence in `network.json`).

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

## Done since the addenda (VALIDATION V-015 … V-071)

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
- **Addendum 03 after slice 2**: tunnels (V-051), camera timing/occlusion and six-client views (V-052), readability and
  bridges (V-053), online recovery/impairment/soak (V-054), relief pass (V-055), instrument extremes (V-056), R11
  (V-057), spectating (V-058), soak memory (V-059: generated course assets released in play), Spectate the Race through
  the real screens with two clients and the NaN-after-disconnect fix (V-060).
- **After V-060:** correction-blend A/B (V-061, inconclusive); a second human sees another's livery (V-062); pre-A03
  Local saves migrate intact (V-063); F08 runs (V-064: stalled at lieutenants before certification).
- **Benchmarks certified** (V-065/V-066): P = slowest intended starter build solo; Normal 1.18→1.05×P, Hard 1.04→1.00×P
  under each side's own conditions (conditions were never applied before: Hard is now really damp/wet/night); featured
  rival pace calibrated to the target; S29 Four Signals judged (it could never be cleared before).
- **F08 with certified targets** (V-065/V-068, automation): V03 clears all 30 Normal and 28/30 Hard; V01/V02 28/30
  Normal; every stop is at S29 (autopilot racecraft in the pack: Entry apex gates / Descent wall contact).
- **F09 driving** (V-069, automation): all 18 models on their favourite-car paths beat the certified targets at four
  sides each (bought-for stage, mid-campaign, N:S28, H:S15), 72/72, after `V06-H3` moved from rain-sport tyres to
  semi-slicks (its supercharged FWD front end hit a wall on the dry H:S15 side; semi-slicks are faster on all seven
  Hard Act III sides). The data half is the .NET `UpgradePathTests`; F10 starters V-048.
- **Car art pass** (V-070): all 18 bodies detailed from the same loft (anchors, decal frames, wheel positions kept);
  decals clipped to each zone's paintable panel; `CarBodyArtTests` (budget, normals, ground/bumper clearance, decals on
  the panel). Built-player camera tour caught NaN-shaded liners blooming into white discs at night that its checks
  passed — fixed, and the tour now fails blown-out frames. Built-player camera (90/90), appearance and instrument tours PASS on `12b489f`.
- **Course scenery** (V-071): the 108 parametric landmarks that generated nothing now build (101 by kits, 7 are the
  road's own bridge/gallery sections); a regional kit per biome along every course; `LandmarkKitTests` keeps landmarks
  and scatter off the road (6.5 m overhead clearance) and off the cars' collision layers — driving unchanged.
- **Characters** (V-072): `Runtime/Characters` builds an original skinned person from a `CharacterLook` (21 outfits,
  22 hair styles, ~35 accessories, 7 builds); all 48 rivals authored in `authored/story/rivals.look.json` (+ the meet
  host in `npcs.look.json`); procedural posture/idle/walk/jog with grounded feet and the twelve emotes (`Core/Meet/Emotes`);
  `CharacterSheet` renders sheets and measures silhouette/colour distinctness (0 confusable pairs); `CharacterTests` 99/99.
- **The meet, offline** (V-073): `Core/Meet/MeetLayout` (bays, lanes, fixtures, walkability, door/rescue spots, arrival
  spline, allocation), `BoomboxState`, `SignalRibbonQueue`; scene `Facilities/Meet/Meet.unity` generated by
  `LandmarkKits.BuildMeet`; `Runtime/Meet` session, camera, walker, HUD; Offline hub → "Car Meet"; `-nsMeetTour` PASS on
  `7b4d1d9`. The game now creates its `MusicPlayer` (`MusicPlayer.Ensure`, `Resources/MusicLibrary`) and menu screens
  play their cues — races still play no music or engine audio.
- **The meet online** (V-074): rooms hosted by the control plane (`Services/ControlPlane/Meet/MeetService.cs`, Core
  `MeetRoom`; protocol docs/NETWORKING.md §3.7); the Convoy screen offers Car Meet (public) and Convoy Meet;
  `Tools/run/meet-online.ps1` runs three clients (PASS ×3 on `9011821`). The convoy at the meet (V-075): header,
  Event Ready from the meet menu, invite a friend from the meet, meet invitations and "Join meet" on Friends;
  `meet-online.ps1 -Convoy` runs two friends through it (PASS ×3 on `5001c7a`). Race from the meet (V-076): the
  leader's Start in the meet menu, "left to race", Back to the Meet after the race; `-Convoy -Race` starts one loopback
  game server (PASS ×2). Settings → Controls has a Walking (meet) page.
- **Player Card** (V-077): Convoy screen → Player Card (`PlayerCardScreen`, `CharacterStage` preview); Core
  `PlayerLooks` validates looks on both sides; migration 0006 adds `player_cards.look_json`/`pronouns`; the meet builds
  avatars from card looks.
- **Addendum 04** (V-067): game server binds `127.0.0.1` by default (`-nsBindHost`, separate from `-nsPublicHost`),
  harnesses fail closed on non-loopback binds without `-AllowLan` (`Tools/run/NetGuard.psm1`), automation builds are
  non-development (no editor/profiler listener), measured sockets in the evidence; never touch Windows Firewall.

## Next actions

Pipeline in dependency order (items at one level do not wait for later ones; human checks are listed where they gate).

0. **Challenges: all 75 judged** (R11.3; `docs/CHALLENGE_TRIALS.md`); tunable trials online (V-142) — next: a full regression tour of all 47 trials. Done so far in the trial work:
   slice 1 (V-119/V-120, online too), slice 2 (CH13 V-121, CH15 V-122, zone chains CH17/19/22/27 V-123), slice 3 (CH34 in
   ordinary races V-124; racecraft trials with a fixed AI field CH40/41 V-125, CH36/39 V-126), slice 4 (challenge cups
   CH14/42/69/72 V-127), CH43 (R32's own run, V-128), Driving School section trials CH52/58 (V-129), drills CH53 (V-130),
   CH07 (V-131), CH23 (V-132, not reached), CH74 (story-gated, V-133), CH37 (merge, V-134 — with the 29-trial regression
   tour, identical verdicts), CH49 (manual gearbox, V-135), tunable loaners and CH46 (V-136: `choices`/`tunable`/`piBudget`,
   Tune the Loaner, `TrialSetups` in the Local profile), CH57 (V-137), CH56 (V-138, with the ghost-write fix), CH59 (V-139).
   Not shown reachable by the autopilot: CH13, CH17, CH22, CH23, CH27, CH28, CH34, CH36, CH40, CH41, CH43 (see each entry).
   CH02 and CH47 as Driving School braking-lane lessons (V-140; kind "lane", `LocalProgression.ApplyLessonTrial`), CH60's
   ten tuning demonstrations (V-141); tunable trials online and trials racing their course's own surface offline (V-142:
   CH51/CH57 re-measured). **Open:** the full regression tour of all 47 trials on the current build (`bounds-audit.ps1 -Only
   720p-large -Tours TrialTour`); TR-CH56 online not yet run; Local Freeplay surface parity (a separate task). The control
   plane (task started this session) serves `fa8fdfb`'s code and content. **Also:** tunable trials online (CH56's "server validates all installed
   parameters" — see V-138's limits). The tour's setup of each tunable trial is `FrontEndApp.TourSetup`. Targeted built-player runs: `Tools/run/tour.ps1 -Tour TrialTour -TrialOnly
   <ids>`; a trial's measurement: `Builds/diag/measure-trials.txt` + `ChallengeTrialReferenceTests.MeasureChallengeTrials`.
   Racecraft, cup, Driving School and story-gated trials are offline only (`ConvoyDirectory.OnlineTrial`). The control
   plane (task b2kshjyh5) still serves the content of `0777df53…`; the trials JSON changed since — restart it before any
   online run. `Assets/_Recovery/0.unity` is Unity's crash-recovery copy of an unsaved scene from 2026-09-29 18:10 — left
   untouched for the owner.
0. **After any AI tendency change**: rerun `-nsCrewTelemetryTour` and `CertifyListed` for the stages whose featured
   rival has that tendency (V-108; the crew telemetry was rerun on the V-109 build and passes).

1. **Course scenery follow-ups** (small): a human look per region (the built-player tour with the regional kits passed,
   run 5 on `7d9119d`); the steep dark terrain "cliffs" of the gorge/highland terrain styles (heightfield resolution)
   are untouched.
2. **Card and meet follow-ups** (smaller): the card is done — look (V-077, offline V-091), style (V-092), showcase online
   and offline (V-093, V-095); the meet under latency is done (V-094); the remaining challenge predicates (R11.3:
   44 of 75 are judged: CH01, CH03–CH06, CH08–CH10, CH12, CH16, CH18, CH20, CH21, CH24, CH26, CH29, CH31–CH33, CH35, CH38, CH44, CH45, CH48, CH50, CH61–CH68, CH70, CH71, CH73, CH75,
   and offline as challenge trials CH11, CH25, CH28, CH30, CH51, CH54, CH55 (V-119) — see REQUIREMENTS R11.3; the published references are
   measured by `ChallengeReferenceTests` (V-113) and the trial targets by `ChallengeTrialReferenceTests` (V-119; rerun both after a physics,
   route, part or car change). Next: challenge trials online, then the later slices of `docs/CHALLENGE_TRIALS.md` — geometry judges, scripted
   AI, fixed cups, T00 drills and workshop trials; CH28 needs the drift controller to manage the road edge before its reference is clean).
3. **Gameplay backlog**: ghosts — personal (V-103, V-104), a convoy member's (V-107, V-109) and the authored rival
   reference (V-109) are raced offline and online; the route/elevation chart offline (V-110) and online (V-112); left: re-recording
   the references (`RivalReferenceGhostTests`) whenever physics, scoring or a route changes. The story is on screen offline and online (V-099, V-100); left: portraits or a
   staged scene instead of text over the backdrop. Then a drift controller that manages the road edge (every AI attempt still ends at the edge; the
   per-driver drift skill of V-080 raises scores on average, not per course) and a re-measure of S29 with it (group
   Time Attack and the hosted tables with 4 and 6 humans passed — V-081, V-082); (reconnect/rejoin/DQ under load — V-095 — is done); customization follow-up: pearl flip tint (meet livery refresh — V-089 — and preset rename/delete — V-088 — are done). (Drift Attack is now limited to courses with judged zones — V-079.)
4. **Open technical items**: heavy-contact prediction hitch at ~190 ms RTT (V-061 inconclusive; V-087: the one-sided
   contact predictor is within millimetres of the server in sustained side contact offline and a two-sided alternative
   was no better — next, a controlled built-player contact scenario to separate real hits from unpredicted remote
   manoeuvres and chain contacts); S29 autopilot racecraft
   (optional — the rule is implemented and V03 passes). Car LOD levels are done (V-086: full/mid/far bodies, Unity's
   LODGroup chooses; measure a level under the GPU Resident Drawer with the frozen-frame bracketed holds of
   `-nsCarLodTour`, never `Renderer.isVisible`); character LOD tiers and the §14 performance profile remain.
5. **Gate 4/5**: `docs/GATE4.md` is the audit (V-114: every party size 1–6 run; a crash mid-settlement rolls back) — open:
   challenge loaners, pad-only walkthrough, string bounds of the online screens, the
   §14 performance profile; release validation;
   blocked parts stay blocked (local Supabase/Postgres stack, Linux server module, WAN test with real people).

Human checks outstanding (cannot be automated): S29 run, featured-rival pace feel after calibration, camera/comfort
feel, cockpit/visual review including the 18 detailed car bodies (V-070), a look at the 48 rivals (V-072) and a walk
round the meet (V-073), a listen to a race (engines, tyres, music balance; V-078), a real cross-device LAN test once
authorized.

## Recovery notes

- On resume: `git status`, `git log -1`, `git ls-remote origin refs/heads/dev/night-signal`,
  `mcpforunity://instances` / `project/info`, running Unity/player/dotnet processes, `Builds/NetRuns/*` logs.
- Build outputs live in `Builds/` (ignored). Raw player logs contain local paths — never commit them;
  copy JSON evidence into `Evidence/`.
- MCP `execute_code` is C# 6: pass `ref` for `in` parameters, no local functions.
