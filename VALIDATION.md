# Validation log

Only executed checks are listed. Each entry names the revision it ran against, how it ran, and the result.
"Working tree on X" means uncommitted changes on top of commit X that were committed in the next checkpoint.
Machine: owner's Windows 11 Pro workstation, NVIDIA GeForce RTX 3080, Unity 6000.6.3f1.

## V-001 — Repository, remote and first push (2026-09-26)
- Revision: `062bd57` (baseline setup commit on `dev/night-signal`, parent `7a5e268`).
- Checked: working directory = Git root = Unity project root; single worktree; remote
  `https://github.com/A-Birdi/Night-Signal.git` (racing project, no embedded credentials).
- `git push -u origin dev/night-signal` → new branch; `git ls-remote` SHA = local HEAD `062bd57a5d8b…`. **Pass.**

## V-002 — Package cleanup through Package Manager (2026-09-26)
- Revision: working tree on `062bd57`.
- Removed `com.unity.ai.assistant`, `com.unity.pipeline`, `com.unity.ai.inference` via MCP `manage_packages`
  (each job `succeeded`); Package Manager dropped `com.unity.dt.app-ui`, `com.unity.2d.sprite`,
  `com.unity.mathematics` from the lockfile. Editor reconnected after each domain reload
  (`mcpforunity://instances` → one instance, `Night Signal@6e8641e8562575b3`, project root unchanged).
- Console after resolution: 0 errors, 0 warnings. The earlier `NoSubscription` exceptions and
  "Claude CLI not found" entry did not recur after the reloads. **Pass.**

## V-003 — Editor write + rendered capture through the bridge (2026-09-26)
- Revision: working tree on `062bd57`.
- Created `Assets/Tests/Verification/SetupSmoke.unity` (camera, directional light, ground, spin target,
  `SmokeTestRunner`) and two URP/Lit materials with MCP tools, saved the scene, captured the Main Camera.
- Evidence: `Evidence/setup/editor-smoke-camera.png` (640×360, URP Lit shading, soft shadow). **Pass.**

## V-004 — EditMode and PlayMode tests (2026-09-26)
- Revision: working tree on `062bd57`.
- `NightSignal.Tests.EditMode`: 5/5 passed (editor version pin, Graphics default URP asset, all quality levels
  URP, Linear colour space, smoke scene contents).
- `NightSignal.Tests.PlayMode`: 1/1 passed (`SmokeScene_RunsUnderUrpAndAnimates`: URP active, update loop
  rotates the target > 5° in 0.5 s).
- Note: the EditMode scene test was then hardened (do not close a scene the developer had open) after this
  run; re-run recorded in V-007.

## V-005 — Windows development player build (2026-09-26)
- Revision: working tree on `062bd57`.
- MCP `manage_build`: StandaloneWindows64, player subtarget, Development, scene `SetupSmoke` →
  `Builds/SetupSmoke/NightSignalSmoke.exe`. Result **Succeeded**, 238 s, 168.27 MB, 0 errors in summary,
  2 warnings. Console also logged two "All SubShaders were stripped" errors for
  `Hidden/Core/DebugOccluder` and `Hidden/Core/DebugOcclusionTest` (see `docs/UNITY_SETUP.md`).

## V-006 — Launch of the built player (2026-09-26)
- Binary from V-005, launched as a separate process with `-nsSmokeTest -nsSmokeFrames 180`, windowed 1280×720.
- Exit code **0** after 18.7 s wall time. Report `Evidence/setup/player-smoke.json`: WindowsPlayer,
  development build, `UniversalRenderPipelineAsset` / `PC_RPAsset`, Direct3D11, Linear, 180 frames,
  non-black pixel fraction 1.0, target rotated 109°. Screenshot `Evidence/setup/player-smoke.png`.
- The 40 fps average covers the first 4.5 s after launch of a development build and is **not** a
  performance measurement.

## V-007 — Test re-run at the committed setup checkpoint (2026-09-26)
- Revision: `6b41a20` (clean working tree).
- `NightSignal.Tests.EditMode`: 5/5 passed. `NightSignal.Tests.PlayMode`: 1/1 passed. **Pass.**

## V-008 — Git LFS upload and retrieval (2026-09-26)
- Revision: `6b41a20`. Push uploaded 2 LFS objects (1.0 MB); remote branch SHA = local.
- Fresh `--depth 1` clone of `dev/night-signal` into a scratch folder: `git lfs ls-files` lists
  `Evidence/setup/editor-smoke-camera.png` and `player-smoke.png`; their SHA-256 in the clone equal the
  originals (`2202add9…`, `a031db4f…`) and the files are real PNG data, not pointers. **Pass.**
- Finding: the template file `Assets/TutorialInfo/Icons/URP.png` (initial check-in) was stored as a normal
  blob despite the `*.png` LFS rule; converted to an LFS pointer with `git add --renormalize` in the next
  commit (no history rewrite).

## V-009 — Rules core and catalogue (2026-09-26)
- Revision: `6d00105`. EditMode 111/111: economy (exact integer payouts, DNF, quit/DQ zero, PvP bonus rules,
  first-clear table, wallet clamp 9,999,998 → 9,999,999), Rank Points (15,000 budget, thresholds, Living Legend
  requirements, malformed records rejected), frontier/convoy access (spec examples [1,1], [8,12,31], [31,31],
  Hard lock), team success/support envelopes/S29 contracts/deadlines, six-entrant grid and freeplay clamps for
  every H/AI combination, drift scoring curves and anti-exploit cases, tie classification, Appendix G validator.

## V-010 — Catalogue vs specification (2026-09-26)
- `node Tools/qa/compare-catalogue-spec.mjs`: 26 courses, 30 stages, 18 cars, 48 rivals, 75 challenges match the
  appendices field by field. `node Tools/authoring/import-catalogue.mjs --check`: generated files reproducible.

## V-011 — Vehicle chassis and handling harness (2026-09-26)
- Revision: `5774386`. Determinism (identical input → identical state), settling, input starvation coast/brake,
  all 18 models within envelopes; `Evidence/handling/harness-report.json` (scripted harness driver on an analytic
  plane — not a human). Skidpad 0.99–1.27 g scaling with tyre grip; 100–0 km/h 32–39 m; drift hold RWD > AWD > FWD
  with no spins. Drift feel for human players is **unverified**.

## V-012 — Course generation and autopilot course drive (2026-09-26)
- Revision: `5774386`. C01 generated from route.json in ~1.9 s (editor). Track tests: 3,079 m, −80 m, 20 m
  hairpin, 7% max grade, six separated grid slots, ordered checkpoints, locator/wrong-way.
- PlayMode: RouteFollower autopilot (same chassis and inputs as a player) finishes C01 in V01 (86.5 s) and V03
  (84.6 s) with 31/31 legal checkpoints, 0 wall incidents, no corridor cut. Evidence `Evidence/courses/`.
  Proves drivability/checkpoint data only — not fun, not human handling.

## V-013 — Control plane service (2026-09-26)
- Revision: `b510285`. `dotnet test Services/NightSignal.Services.slnx`: 149/149 (worker run, re-run by the
  coordinator). Covers JWT rejection cases, DevAuth startup guard, ledger idempotency (100 retries), wallet clamp,
  concurrent purchase exactly-once, convoy/readiness rules, tickets, end-to-end two-account settlement with restart.
  Postgres store, Postgres migration and RLS policies compile but were **never executed** (no PostgreSQL/Docker).

## V-014 — Managed ES256 ticket verification for Unity (2026-09-26)
- Unity's Mono runtime throws NotImplementedException for `ECDsa` (observed in the editor). Core `P256` +
  `MatchTicketValidator` implement verify-only P-256. EditMode: RFC 6979 A.2.5 vectors verify, tampering rejected.
  `dotnet test --filter CoreTicketParityTests`: 3/3 — real control-plane tickets validate once, replay/tamper/foreign
  match/expiry rejected, 200 random .NET ECDSA signatures agree with the managed verifier.

## V-015 — Full game player build with netcode, boot scene and HUD (2026-09-27)
- `manage_build` (StandaloneWindows64, Mono, scenes Boot + C01) → `Builds/Game/NightSignal.exe`: succeeded, 0 errors,
  44 warnings, 184.9 MB, 1153 s (first build compiled every URP shader variant; later builds reuse the cache).
- That build predates the HUD code. Afterwards TMP Essential Resources were imported (LiberationSans SDF, OFL, bundled
  with Unity's uGUI package), the UI layer compiled with no errors or warnings, and EditMode ran 127/127 passed.

## V-016 — First real multi-process race: server + 2 client processes (2026-09-27)
- `Tools/run/net-race.ps1 -Humans 2 -Stage S01` on this PC (localhost; three separate OS processes, UDP game traffic,
  HTTP/WebSocket control plane). Evidence: `Evidence/net/run-20260927-001253-h2/`.
- Both AutoClients (scripted autopilot, not humans): DevAuth sign-in → convoy → both ready checks → allocation →
  ticket-validated connection → loading barrier → countdown → race → HMAC-signed results → settled receipts.
  Humans finished P2 86.623 s and P3 86.633 s; wallets 12,000 → 31,139 / 30,461 (first clear + CH01); RP 0 → 140.
- Problems found in that run (fixed in code afterwards, not yet re-measured):
  RTT reported ~505 ms on loopback (transport reliable-pipeline RTT goes stale) → game-level input-ack RTT;
  37 reconciliations with corrections up to 2.5 m → client now predicts every tick when the clock jumps, server
  counts starved/late inputs; race ended the moment all humans finished, marking AI 3–8 s behind as DNF → now ends
  early only when every entrant is done (spec §6), finish window sent to clients; server receive queue overflowed
  at 128 → 512; headless clients uncapped at ~1.6 cores each → 60 fps.
- These results predate Addendum 01 (six-TOTAL grid, no car contact). They are kept as evidence of the network
  spine, not as evidence for the revised twelve-vehicle contact rules.

## V-017 — Second 2-client run failed at convoy formation (2026-09-27)
- `Evidence/net/run-20260927-002018-h2/`: leader "timed out waiting for all members joined", member "timed out
  waiting for joined a discoverable convoy". Both accounts were still members of the previous run's convoy under
  the old 60-second reserved-seat rule. Addendum 01 §10 replaces reserved seats with server-owned rejoin grants and
  disbands convoys with no active members; the fix lands with that control-plane change.

## V-018 — Addendum 01 Core rules (2026-09-27)
- EditMode 161/161 (Unity 6000.6.3f1): capacity H=1..6 with every legal AI count to 12 vehicles; H=7 and 13 vehicles
  rejected; Time Attack humans-only non-contact (stale AI requests clamped); authored live opposition featured-first;
  finales H+1 with R40/R48 live at H=1 and H=6; R40/R48 rejected in every non-finale placement context; Team Trial
  6 v 6 for H=1..6; course-access table and all 20 dual-unlock mappings pinned; purchase idempotency and
  purchase-vs-unlock race; sponsor/guest rules; weighted one-ticket-per-ballot draw reproducible from the stored random
  value; handle validation/canonicalisation; Team Mean/Best/Drift scoring (a quitting teammate never helps); live-rival
  stage condition incl. Hard ceil(H/2) and broken-event refusal; catalogue loads 29 courses and refuses to load without
  the Addendum overlays; placements 4–12 = 1.00; 12-slot grid on the road; 8 light-contact tests.
- .NET (at commit 2ecc1e2): 150/152 — the two failures assert the superseded reserved-seat semantics (server rework in
  progress).

## V-019 — Twelve-car light-contact race, in-process (2026-09-27)
- PlayMode `FullGridContactTests` on C01 revision 3: autopilot in the human seat + 11 AI through the shared
  RaceSimulation (the dedicated server's race loop), headless, fast-forwarded. Evidence
  `Evidence/courses/C01-12car-contact.json`. Not a network run, not a human playtest.
- First runs exposed two real defects, both fixed and re-verified:
  1. Guardrail body depenetration never ran: `ComputePenetration` returns false for a probe collider on an inactive
     GameObject (measured in the editor: 0 resolved vs 0.85 m with an active probe). Cars already overlapping a rail
     (slides, rotation, contact nudges) passed through it; one AI left C01 at 1521 m and fell off the world.
  2. Barrier responses used tilted edge normals: a graze at 133 km/h converted to +15.6 m/s vertical and vaulted a car
     over the rail. Barrier contacts now respond horizontally.
  Added marshal recovery for cars clearly off course.
- Final run: 12/12 finished, 0 recoveries, 0 corridor cuts, max vertical speed 2.8 m/s, 50 debounced contact
  incidents, 11 wall incidents, simulation 0.51 ms/tick mean (5.5 ms worst) for twelve cars. C01 solo autopilot
  V01/V03 still finish 31/31 with 0 walls. Time Attack rejects live AI (PlayMode 5/5).

## V-020 — Front end in the standalone player (2026-09-27)
- `Builds/Game/NightSignal.exe -nsUiTour` (Windows player, 1600×900 windowed): the tour presses the REAL buttons —
  title → Offline Play → Start Practice Race (1 local seat on autopilot + 5 AI, class-capped to the chosen car's PI
  class) → results — and saves screenshots (`Evidence/ui/01-title.png` … `04-results.png`); exit code 0,
  "results reached". Automation, not a human playtest.
- Title shows the online service status it actually measured ("unreachable" — no control plane running) and keeps
  Offline Play available; the Local / Offline domain is labelled on the strip, the hub and the results page.
- Found and fixed on the way: runtime-created cameras skipped URP post-processing (washed-out image); play mode and
  players stalled when unfocused (`runInBackground` now on for every role); results page rendered before it was built;
  labels overflowed from TMP's default rect; the bundled font has no ellipsis glyph.

## V-021 — Control plane for Addendum 01 and 02 (2026-09-27)
- `dotnet test Services/NightSignal.Services.slnx`: 281/281 (coordinator re-run). Covers rejoin grants + leadership
  epochs (the two superseded 60 s reserved-seat tests rewritten, not deleted), intent → Mode Ready → Enter Mode,
  Freeplay ballots (server deadline, one-ticket-per-ballot draw stored against retransmit), course ledger + idempotent
  purchases + guest passes, handles + friends (idempotent, rate-limited), 12-vehicle rosters, final-rival rejection,
  placements 1–12, live-rival stage settlement, Team Trial settlement (provisional in-code fixture), music
  entitlements, 24 h dormant rooms (restart recovery), convoy-session id + membership generation, diversion field
  that never clears readiness, Continue / Service Break post-event decision.
- Not executed: PostgreSQL migrations/RLS (no server available; SQLite ran everything); Custom Cup multi-leg races;
  live-room recovery after a restart (only dormant rooms recover).
- The Unity client still speaks the removed destination.* protocol; it is updated next.

## V-022 — Network slice re-measured on protocol 2 (2026-09-27)
- First attempts with the post-V-016 netcode (evidence kept): `run-20260927-015012-h2` failed at convoy.create (the
  control plane still spoke protocol 1); `run-20260927-015134-h2` raced 9+ minutes without a result and was killed by
  the runner; `run-20260927-020809-h2` (`trace-excerpt.txt`) showed why with the new 15 s server/client traces: both
  clients sent **0 input packets** (the send throttle started from `int.MinValue`, so `tick - lastSentTick` overflowed
  negative), the server coasted both human cars on the grid (`starved2700`, 0 m) while the AI raced. Fixed.
- The killed match also exposed a control-plane defect: the watchdog aborted the lost match, then threw while
  snapshotting the member-less Dormant room ("A convoy needs at least one member"), leaving the room stuck. Fixed
  (`ConvoyDirectory.Snapshot`); regression test `ALostMatch_AfterEveryMemberDisconnected_AbortsCleanly…` fails
  without the fix and passes with it; the next real abort logged no error.
- **Pass:** `Tools/run/net-race.ps1 -Humans 2 -Stage S01` → `Evidence/net/run-20260927-021237-h2/`. Three OS
  processes, protocol 2, content `ed1fdef41467` (now includes `music.unlocks.json`). 2 humans + live featured rival
  R01, light contact; humans P1 87.010 s / P2 87.210 s, AI finished too; 0 contacts / walls / resets; RTT 25 ms
  (input-ack), 111 / 91 reconciliations, max correction 0.85 m; HMAC-signed results settled: first clear, CH01,
  wallets 12,000 → 32,157 / 31,139, RP 0 → 140, soundtrack `MUS_RACE_MIZUHANA` granted.
- Measured weakness: 160 and 46 of ~5,300 racing ticks simulated without that tick's command (inputs arrived just in
  time, lead 0). The client now predicts 2 ticks past the network clock and sends every tick — **not yet re-measured**.

## V-023 — Local campaign in the standalone player (2026-09-27)
- `NightSignal.exe -nsUiTour` (1920×1080 windowed; isolated save folder under the tour directory): REAL buttons from
  title → Offline Play → Local profiles → New Local Profile ("Tour Driver", starter car) → Offline hub → Campaign Map
  (painted region map, act 1 revealed, S01 pulsing as next) → S01 panel (live featured rival, provisional target,
  record, car) → Race (autopilot, sped up) → Results → map. Exit 0, `PASS`; log: `CampaignStage S01: Applied wallet
  12000 -> 29157; 6 change(s) Saved.` Results listed first clear, race money 9,157 + first-clear 8,000, RP 0 → 100,
  soundtrack unlock and a Local/unverified personal best 01:26.485. After Continue, S01 shows cleared and S02 next.
- Progression is judged only by Core (`LocalProgression.ApplyEvent`, `StageBenchmarks.Provisional` shared with the
  control plane) and saved atomically by `ProfileRepository`. Automation, not a human playtest; the screenshots showed
  layout defects (header in the strip, panel off-screen by half its width, column collision) that were fixed after
  this run and are not yet re-captured.

## V-024 — Builds, Toys and Local-profile cores (2026-09-27)
- `dotnet test`: Services/BuildsTests 222/222 (parts catalogue, resolver + fixed-point build hash, PI estimate,
  8 loadouts + 5 visual presets per instance, protected references, quotes/settlement, migration, upgrade paths),
  Services/ToysTests 92/92 (DowntimeSession + all five diversions, pause/resume, non-progression, sizes),
  Services/CoreTests 86/86 (Local profile, progression, records, atomic persistence), Services/Tests 282/282.
  Unity compiles all of them into NightSignal.Core; EditMode 161/161.
- Data-level only: no driving evidence for builds, no control-plane hosting or presentation for the toys yet. The
  builds agent's open questions (cap-excluded favourite cars, starter PI under the real economy, unmeasured PI
  estimate) are recorded in docs/EFFECTIVE_RULES.md.

## V-025 — Six client processes + live rival on protocol 2 (2026-09-27)
- First attempts failed at convoy formation (evidence kept: `run-20260927-022917-h6`, `run-20260927-023357-h6`): the
  control plane replays a cached reply for the same (account, type, requestId) for 10 minutes, and the Unity client
  numbered requests `r1, r2…` from scratch in every process, so a new process's `convoy.create` received the previous
  run's reply (an old, dormant convoy) and no convoy was created. Request ids now carry a random per-process prefix;
  the rule is documented in docs/NETWORKING.md §3.
- **Pass:** `net-race.ps1 -Humans 6 -Stage S01` → `Evidence/net/run-20260927-023610-h6/`. Seven OS processes
  (server + 6 clients), 6 humans + featured rival R01 = 7 vehicles, light contact. All six AutoClients PASS; humans
  90.884–99.075 s, AI finished after one marshal recovery; settled receipts (first clears for new accounts,
  `MUS_RACE_MIZUHANA` for the four that did not own it).
- Netcode with the 2-tick input lead (first measurement): **0 starved and 0 late commands for every human** (V-022 had
  160 / 46 starved without the lead), server-side input lead 2–3 ticks throughout, RTT 18–20 ms, input-ack max 50 ms.
- Contact under the network: 4–9 vehicle contacts per human, 0–4 wall incidents, 0 resets; reconciliations 225–481 per
  client with max correction 2.3 m (contacts are predicted against extrapolated remote cars, so a real bump corrects).
  Localhost only; WAN and packet-loss conditions are untested.

## V-026 — Circuits and crossings; course sweep 29/29 (2026-09-27)
- Found by the online UI tour on S03 (C03): both cars stuck, distances past the lap length, the AI driving backwards
  with 23 marshal recoveries. Two generator defects:
  1. Every closed-loop circuit (C03, C11, C14, C18, FP01, FP03, T00) had ONE checkpoint per lap — the start line — so
     GO counted a lap. Gates now run all the way round the lap (wrapping the loop seam) and close at the lap line; the
     tracker measures crossings as forward travel with wrap, race distance counts from the start line (negative on the
     grid) and stays continuous through laps and the finish.
  2. Where two stretches of road cross at different heights (C03's orchard overpass 13 m above the road; C25's
     two-level valley bridge) the terrain followed the nearest sample — the upper road — and buried the lower road in a
     hill. Terrain now follows the LOWER road wherever both corridors cover a cell and the roads are > 3.5 m apart.
- EditMode 188/188 (new: circuit gate order for all seven loops; a virtual car driven two laps round C03 passes 50/50
  gates once, never loses race distance, finishes clean; build parity 19).
- **PlayMode CourseSweepTests 29/29** (`Evidence/courses/sweep/*.json`): the validator autopilot drives every course
  scene start to finish with the real chassis (V05, 30× fast-forward): all checkpoints, 0 resets, 0 corridor cuts,
  3 wall touches in total (C12 ×2, C25 ×1). Before the fixes: C03 stopped at 434 m, C25 at 7,930 m.
- Still missing (not blocking driving): bridge decks/piers, tunnel shells and the `crossing`/`water`/`field`/
  `structure` landmark kits — the upper road of a crossing currently spans a gap without supports.

## V-027 — Online play through the real menus (2026-09-27)
- New interactive Online flow: sign-in → Convoy screen (roster with Mode/Event Ready, create/join/list/code, rejoin
  prompt, starter choice, leader Intent → Mode Ready → Enter Mode → stage/course proposal → Event Ready → Start, the
  15 s readiness-request cooldown shown as a countdown, Continue / Service Break / Advance) → server race with the
  player's controls → settled receipt on the convoy screen.
- `Tools/run/ui-tour-online.ps1` (dedicated server process + one windowed client pressing the REAL buttons; validator
  autopilot drives through normal inputs; dev account from the project seed): **PASS** twice —
  S02 (C02) P1 of 3 02:25.285 first clear +18,248 cr, Next Stage S03 opened by Advance; and after V-026 S03 (C03,
  two laps) P1 of 2 03:02.753 first clear +18,716 cr. Screenshots `Evidence/ui/online/` (and the Local campaign tour
  re-captured after the layout fixes in `Evidence/ui/campaign/`). Automation, not a human playtest.
- Found on the way: the tour's own Mode Ready click unreadied the leader (the proposal already counts as the leader's
  Mode Ready — UI now labels the state); rate-limited event proposal after the intent (cooldown now shown);
  missing ✓ glyph in the bundled font; one native client crash (0xC0000005) right after loading C03, not reproduced
  in two further runs — kept open (minidump only, no symbols).

## V-028 — Diversions hosted by the control plane (2026-09-27)
- `dotnet test Services/NightSignal.Services.slnx`: Services.Tests 307/307 (25 new: toy.command/toy.snapshot routing
  and limits, pause at match commit and resume after settlement/abort, snapshots surviving a control-plane restart for a
  dormant room, 24 h expiry, non-progression rejection in settlement, readiness untouched by toy commands, real
  WebSocket round trips), BuildsTests 222/222, Toys.Tests 92/92, CoreTests 86/86. SQLite migration 0004 applied on a
  real control-plane restart; the Postgres migration is written but not executed.
- Not yet: the Unity client's tabletop presentation and toy.* handling (Pocket Circuit first).

## V-029 — Pocket Circuit playable offline (2026-09-27)
- Local play hosts the SAME Core `DowntimeSession` the control plane runs, in-process (commands through the full
  envelope: sequence, request id, epoch, rate budget); the table is rendered procedurally from the Core `SlotTrack`
  lanes (table, six-lane deck with slots/rails/borders, piers under raised pieces, start gantry, one toy car per lane,
  table and chase cameras). Analog throttle (trigger, or a smoothed held key) sent at ~20 Hz; lanes 1–6; layouts by
  consent (solo: immediate). The table snapshot is saved in the profile's non-progression toy workspace.
- Standalone tour (`-nsUiTour`, continues after the campaign): Offline hub → "While We Wait: Pocket Circuit" → scripted
  curvature-aware throttle → **3 clean laps** on Workshop Oval (0:13.800, 0:13.658, 0:13.658; lane-1 ratio 1.059
  disclosed; normalized best 0:12.893; clean-lap collection 3/12) → leave → table saved (`toy table saved True`).
  Screenshots `Evidence/ui/campaign/10…12`. Automation, not a human playtest.
- Not yet: the online table (client `toy.*` handling against the hosted session), the other four diversions' views,
  the While We Wait selector on the convoy screen.

## V-030 — The convoy's shared Pocket Circuit table while ready (2026-09-27)
- Client side of the hosted diversions: `toy.state` / `toy.activity` pushes, `toy.command` in the Core envelope
  (convoy session id, activity epoch, membership generation, strictly increasing sequence, unique request id),
  `toy.snapshot` on entry, `diversion.set` on entering/leaving. The client mirrors the server's table with the same
  Core types and dead-reckons cars between pushes with the shared `SlotSim`; every push replaces the mirror. A compact
  convoy strip on the table shows the pending ready check and toggles Mode/Event Ready without leaving the table.
- Online tour (`ui-tour-online.ps1`): after Event Ready for S04 the player opened "While We Wait: Pocket Circuit" from
  the convoy screen → **the shared table** (server-hosted session) → the car completed its out lap (progress
  7.14 → 1005.60) → **Event Ready still set** → back → Start → S04 P1 of 3 02:16.422, first clear +22,651 cr → Continue
  → Advance. PASS. Screenshot `Evidence/ui/online/06b-table-while-ready.png`.
- A match start closes the table view on the client; the server pauses the toys at the match commit (V-028 tests).
  Not yet exercised end to end with several humans sharing one table at once.

## V-031 — While We Wait selector and Greenlight (2026-09-27)
- A generic `ToyConnection` (Local in-process session or the convoy's hosted session) now serves every diversion view:
  typed Core state per activity, commands in the Core envelope, answers with the authority's value.
- Greenlight station: Lights Out (five lights at a fixed cadence, all out at the seeded hidden cue), Shift Window and
  Hold the Mark (sweep with the seeded window/mark), Forgiving/Narrow; the attempt seed comes from the authority, the
  input is timed with the local high-resolution clock and reported; the authority judges plausibility and outcome.
  Personal best, median of five, the shared clean chain and session bests are shown (non-progression).
- Local tour: While We Wait selector → Pocket Circuit (3 clean laps) → Greenlight: **3/3 clean Lights Out attempts**
  (scripted press 230 ms after lights out → "CLEAN REACTION 230 ms", chain 3/10). Screenshots
  `Evidence/ui/campaign/10a, 13, 14`. Cap Clash, Pit-Crew and Convoy Canvas are listed but their presentation is not
  built yet (Core + hosting exist).

## V-032 — Cap Clash playable; Local toy request ids (2026-09-27)
- Cap Clash table built from the authored arrangement (felt board, rails and bumpers, extruded rubber props, 50/25/10
  rings, launch strip, foul line, one cap per participant); aim angle/power/launch with a preview traced by the Core
  physics; one queued shot per person with reconfirm; standings with the crown, bests, the six-mark card; target and
  table changes by consent. Online, moving caps are dead-reckoned between pushes with the same physics.
- Defect found by the tour: the Cap Clash screen's first shots were silently answered as duplicates — the Local host
  numbered toy request ids from 1 in every new host instance, and a restored session answers a remembered id with the
  ORIGINAL result without executing it. Request ids now carry a random per-instance prefix (the online paths already
  did); reproduced and verified in the editor (save → restore → command now executes).
- Local tour: Pocket Circuit 3 clean laps → Greenlight 3/3 clean → **Cap Clash 3/3 shots on the scoring area** (50 pts
  at 0.9 cm, crown, card 3/6). Screenshots `Evidence/ui/campaign/15, 16`.

## V-033 — Pit-Crew Project playable (2026-09-27)
- Workbench view: the shared miniature on a turntable, parts laid out by group (chassis, wheels, body, lamps, …);
  installed parts solid, the rest as blueprint-blue placeholders. Task board (mine first, then available in dependency
  order, with steps done/total and "in use"), claim/release, a forgiving precision gauge per step in the family's unit
  (mm / degrees / gauge fraction, tolerance band shown), lease renewal while working, "not quite — retry that step",
  shelf with contribution notes, next model when finished.
- Local tour: **3 operations completed** on the Night-Shift Coupe (claim → steps locked near the centre → "Part
  installed!", 3/30). Screenshots `Evidence/ui/campaign/17, 18`. Art is placeholder primitives; the camera framing puts
  part of the model behind the task panel.

## V-034 — All five While We Wait diversions playable (2026-09-27)
- Convoy Canvas: the shared vector document rasterized on the client (strokes, lines, rectangles, ellipses, stamps from
  the authored library; lettering rendered as literal text, never markup); tools pen/line/rect/ellipse/text/stamp/
  eraser (own marks), palette, widths, undo/redo of own actions, new sheets, whole-sheet clear by consent; every
  operation carries the sheet AND its epoch; strokes are sent in ≤ 128-point chunks and closed once the authority has
  named them. Online the document arrives as Core's compact encoded form.
- One standalone Local tour now drives every diversion through its REAL screen: Pocket Circuit 3 clean laps,
  Greenlight 3/3 clean, Cap Clash 3/3 on the scoring area, Pit-Crew 3 operations, **Convoy Canvas 3 strokes** — PASS,
  toy table saved in the profile. Screenshots `Evidence/ui/campaign/10a…19`.
- Online: Pocket Circuit verified at the convoy's shared table (V-030); the other four use the same connection code
  path but have not yet been exercised against the hosted session. Controller input for the Canvas (a virtual cursor)
  is not built; mouse drawing only.

## V-035 — Six humans + six AI, full 12-car grid over the network (2026-09-27)
- `net-race.ps1 -Humans 6 -FreeplayCourse C01 -FreeplayAi 6` → `Evidence/net/run-20260927-042251-h6-C01-ai6/`: seven OS
  processes, Freeplay sprint, **12 vehicles** (6 humans + 6 opposing AI), light contact. All six AutoClients PASS;
  12/12 finished (humans 86.4–100.7 s); settled Freeplay receipts.
- Netcode: **0 starved / 0 late commands** for every human, input lead 1–2 ticks (racing ticks only), RTT 18–31 ms,
  input-ack max 67 ms, 168–327 reconciliations per client, max correction 1.73 m. Contact: 2–9 vehicle contacts per car,
  0–4 wall incidents, 0 resets. Localhost only.

## V-036 — Freeplay course vote through the convoy screen (2026-09-27)
- Convoy screen ballot panel (Addendum 01 §6): leader's Voting On/Off (15 s), Open a Course Vote (with the opponent
  count), members' course ballot with a countdown to the SERVER deadline, live tallies and chances, the leader's Draw
  after the freeze (or Cancel), and the drawn course's proposal. Direct selection is hidden while a vote is live (the
  server refuses proposals then).
- `Tools/run/ui-tour-online.ps1 -Freeplay` against a real dedicated server: sign-in → convoy → Freeplay Sprint →
  Mode Ready → Enter → Voting On → Open vote → Cast → frozen at the deadline → Draw ("ballot 1 of 1, 100%") → Event
  Ready (shared table while ready, readiness kept) → server race C01, P3 of 4 → settled receipt → Continue → Advance —
  **PASS**. Screenshots `Evidence/ui/online/freeplay-vote/`.
- Found and fixed on the way: JSON `null` sub-objects indexed with `?[...]` throw in Newtonsoft (guarded as `as
  JObject`), and a click during an in-flight request was silently dropped (the tour now waits for the screen's `Busy`;
  toggles still must not double-send). One human only: multi-member tallies are covered by control plane tests.

## V-037 — Friends, convoy invitations and course purchases through the UI, two clients (2026-09-27)
- New screens: **Friends** (claim an @username; send a request by @username; incoming requests Accept/Decline,
  outgoing Cancel; friend list with the server's presence and rank; Invite / Join / Rejoin exactly where the server
  allows it; Remove asks twice; convoy invitations received as `convoy.invited` pushes, Join/Decline) and **Courses**
  (every course with how the online profile holds it — starter, owned by purchase or clear, buyable, clear-only
  reward — and a two-press Buy that sends the shown price and a fresh Idempotency-Key per attempt).
- `Tools/run/ui-tour-social.ps1`: two windowed clients (dev accounts 0 and 1) against the local control plane, each
  driving its own real screens: usernames @nsdriver0/@nsdriver1 → host sends the request (retrying while the guest's
  username does not exist yet: "No player has that username") → guest accepts → host creates a convoy → the guest
  shows as Available and invitable → Invite → guest's invitation → Join → both in one convoy (roster "Driver 1,
  Driver 2"); host buys **C05 for 45,000 cr (balance 113,105 → 68,105)** → both leave — **PASS / PASS**. Screenshots
  `Evidence/ui/online/social/`.
- Found on the way: the convoy screen threw when signed in without a convoy (a new ballot line indexed a null
  snapshot) — fixed; the Friends screen kept errors only until the next list refresh — action results now persist.
- Not covered by this run: block/unblock UI (server endpoints exist), invitations to offline friends, rejoin through a
  friend row.

## V-038 — Local Garage through the UI; upgrades race and are measurably meaningful (2026-09-27)
- Garage screen (Offline hub → Garage) on Core `GarageOperations`/`LocalGarage` (profile schema 2): slots with the
  draft's parts (preview parts in amber), compatible parts by tier with owned / price / shop-act lock, draft vs race
  build (PI estimate and class, top speed, power/weight, grip, braking, every changed part/tune), Apply, Buy & Apply
  (Core quote, then a confirming press; settled once), 8 loadout slots, the three protected references restored into
  the draft (a dirty draft asks before being replaced), workshop session on enter/leave.
- Local races now drive the car's frozen APPLIED build (resolved parts → `VehicleFactory.Build(spec)`), cap checks and
  Freeplay class ceilings use the applied build's PI, and Last Race Build is recorded when driving begins.
- Local UI tour (`-nsUiTour`) — **PASS**: after S01 and the five diversions, Garage → tyres → TYR-T1-TOURING →
  Buy & Apply (**29,157 → 21,157 cr**) → Save loadout → restore "Before last apply" (stock tyres in the draft) → revert;
  profile re-opened from disk: part owned by that instance, applied build, 1/8 loadouts, reference present. Then **S02
  raced with build `ab6c3d46f265` (PI 240)**, cleared, Last Race Build recorded under that event id. Screenshots
  `Evidence/ui/campaign/20…26`.
- EditMode `Tier2Upgrades_MeasurablyImproveTheStarters` (flat-plane harness, identical inputs), V01 / V02 / V03:
  T2 engine 0–100 7.52→7.32 / 6.90→6.57 / 6.23→5.65 s and +6 km/h at 1,000 m; T2 tyres skidpad 0.992→1.082 /
  1.027→1.110 / 1.056→1.142 g; brake kit at a 60 % pedal 52.4→47.2 / 52.8→47.6 / 53.1→47.7 m; all three: full ABS
  stop 38.6→35.8 / 36.2→33.7 / 36.5→33.7 m. BuildParity 22/22.
- Honest limitation: a brake kit does not shorten a full-pedal ABS stop (tyre-limited, see docs/EFFECTIVE_RULES.md
  builds §5); an ABS-hold-point change was tried and rejected (noisier, sometimes longer stops). No brake fade yet.
  Tuning controls, the Test Yard and the online Garage are not built yet.

## V-039 — Garage Test Yard (A/B) and tuning controls (2026-09-27)
- Test Yard (Addendum 02 §10) on the T00 service-campus geometry (independently entered from the Garage, no lesson):
  launch & braking straight (340 m, painted stop boxes), 30 m skid-pad ring, handling loop; dry/wet presets with the
  SAME `CourseRuntime.SurfaceGrip` rules as events (explicit reset on change). A = the race build, B = the Garage draft
  (preview parts allowed — driving never buys), both frozen when the yard opens; "reset & drive A/B" always restarts at
  rest on a station start; the same controller, simulation, assists and camera as a race; last three runs per side kept
  (0–60, 0–100 or "not reached in this test length", stop distance and from-speed, stop-box result, peak one-second
  lateral g, loop time, top speed seen); overlay labels the limits and "no reward, record or purchase". Back in the
  Garage: last visit per side, a notes field and a Prefer A / Prefer B marker kept on this device.
- `-nsYardTour` (fresh Local profile, preview tyres in B, same scripted inputs): A straight 0–100 7.55 s, stop 38.3 m
  from 100 km/h; B 7.33 s, 36.7 m from 99 km/h; skid pad peak lateral A 0.68 g, B 0.71 g, B wet 0.55 g; wallet
  unchanged; Back from the Garage reaches the Offline hub — **PASS**. Screenshots `Evidence/ui/yard/`. In the full Local
  tour: A (bought touring tyres) 7.43 s / 37.5 m vs B (stock draft) 7.57 s / 38.2 m (`Evidence/ui/campaign/27…29`).
- Tuning page in the Garage: every control the installed parts expose (bounds, step, default, owning part), −/+ edits
  the draft, reset to defaults, and an explicit "fit the tune to these parts" after a part swap (Core `Normalize`,
  every change listed). Compiled and rendered; not yet driven by a tour (no tuneable part in the tour builds).
- Found on the way: (1) the router handled Back while the menus were hidden (a race or the yard owning the screen) —
  now ignored there; (2) returning from the yard with a non-push Show cleared the screen stack (Back went nowhere) —
  the Garage is refreshed in place; (3) tour captures taken in the same frame as a reset showed a one-frame-stale view;
  a new car view is now placed at the start pose immediately.

## V-040 — Online Garage in the control plane; frozen builds race online; two humans at the hosted diversions (2026-09-27)
- Control plane (background agent, reviewed and re-run here): `/v1/me/garage` — car instances, workspaces (≥ 8
  loadouts, ≥ 5 presets, protected references), compatible parts with shop act/ownership/tuning, one-to-one
  `GarageOperations`, server quotes and idempotent settlement (debit + grants + workspace in one transaction; retry =
  `replayed`, charged 0); migration `0005_garage` (SQLite applied; Postgres/RLS written, not executed — no server);
  `loadout.set` computes the performance hash/PI server-side; a performance change clears only that player's Event
  Ready; caps use the server PI estimate; assignments carry `entrants[].vehicleBuild`; Last Race Build recorded at the
  game server's acknowledgement; utility income from the frozen build. `dotnet test Services/Tests` **329/329**
  (test classes now run one at a time: parallel classes raced on SQLite's global pool clear).
- Game server: re-resolves each human's `vehicleBuild` with Core and requires the same BuildHash (else the match aborts
  with the reason); clients resolve the same build from the roster for prediction and remote cars. Online tour against
  the new control plane: "Driver 1 races the frozen build `c12c2bea1a67` of `ci_702e…` (applied revision 1, 0 parts,
  PI 220) — hash verified", race → settled receipt → **PASS**. A build WITH parts online needs the online Garage UI
  (next); the verified hash path is the same.
- Two humans at the convoy's HOSTED tables (`ui-tour-social.ps1`): Cap Clash 2 shots each and each sees the other's 2;
  Pit-Crew 1 operation each on the shared model; Convoy Canvas 3 strokes each, **94/94 points** of each player's
  strokes on the hosted sheet — **PASS / PASS**. Screenshots `Evidence/ui/online/social-toys/`.
- Found and fixed: (1) `ControlPlaneClient` issued overlapping `ClientWebSocket.SendAsync` calls (only one may be
  outstanding) — all requests now go through one ordered send loop; (2) Core keeps one stroke in progress per person, so
  online a stroke begun before the previous stroke's id arrived finalized it and its points were refused (strokes became
  dots, `AlreadyDone`) — the client now holds the next stroke's begin until the previous one is complete.

## V-041 — Online Garage UI; a bought part races online; build data in the content hash (2026-09-27)
- The Garage screen now runs on a `GarageBackend`: Local (Core in-process, atomic profile saves — unchanged behaviour)
  or Online (`/v1/me/garage`: the control plane runs the same Core operations; the client converts its workspace wire
  shape back to Core and evaluates with Core). Convoy screen → Garage. Online shows the account wallet, locks Apply
  while the build is frozen for an event, quotes and settles on the server.
- `ui-tour-online.ps1 -Garage` (dev account 0, real screens): online Garage → tyres → TYR-T1-TOURING → Buy & Apply
  (server quote, then settle: **8,000 cr**, "The parts belong to this car") → applied hash `ab6c3d46f265` → convoy →
  Event Ready → start → the game server: "Driver 1 races the frozen build **ab6c3d46f265** of ci_702e… (applied
  revision 2, 1 part(s), PI 240) — hash verified" → P1 of 4, stage cleared, settled receipt — **PASS**. The same build
  hash as the Local V01 + touring tyres (V-038): Core resolves identically on .NET 10 and the Unity player. The online
  Garage also listed "Last race build" recorded by the control plane from the previous online race. Screenshots
  `Evidence/ui/online/garage/`.
- `parts.json` and `build-recipes.json` joined `ContentCatalogue.AuthoredFiles`: the race content hash is now
  `5065000bb142…` on both the control plane (/healthz) and the Unity library (13 documents), so a client with different
  build data is refused at connect. .NET: Core 100, Builds 223, Toys 92, control plane 329 — all pass.
- On the new hash: a game server still carrying the old catalogue was refused at registration ("content_mismatch" —
  the check works); on the rebuilt player the same tour passed again — server "content 5065000bb142", tyres re-applied
  from ownership with **no second charge** ("Applied: this is now the car's race build."), "races the frozen build
  ab6c3d46f265 … applied revision 4, 1 part(s) — hash verified", P1 of 4.
- Not covered: two clients editing the same online car at once (the server's revision check answers `stale_revision`;
  the screen then shows the server's car), online Test Yard entry (the yard is local practice either way).

## V-042 — Visible customization renderer: body-kit families, two-tone, finishes, eight rim designs (2026-09-27)
- `CarAppearance` → `CarBodyGenerator` / `VehicleView` (Addendum 01 §13): front stock|lip|aero|track, rear
  stock|diffuser|valance, side stock|skirt|sculpted, rear aero stock|ducktail|wing|gt-wing|lip-spoiler, exhaust
  stock|dual|quad|center; paint primary/secondary/accent with finishes gloss|metallic|pearl|matte|satin; two-tone
  lower|roof|hood-stripe|side-stripe on real mesh edges (extra loft ring points, three-panel roof); lamp tints
  clear|amber|smoke-light (never invisible); rear plate text (literal); eight rim designs 5-spoke, 6-spoke, mesh,
  split, dish, turbofan, multi-spoke, 3-spoke. Appearance never touches `VehicleParams`. Tyres are now closed (sidewalls,
  dark barrel) instead of open tubes.
- Evidence sheets rendered from real `VehicleView`s in an isolated preview scene (`Night Signal/Art/Render
  Customization Sheets`): `Evidence/art/customization/families-V01-front|rear.png`, `families-V06-front|rear.png`
  (six before/after tiles each), `rims.png`. EditMode 191/191; the yard tour still passes on the stock view path.
- Decals (up to 64 layers): stripe, twin-stripe, pinstripe, circle, ring, arrow, chevron, star, bars, arcs, flame, hex
  as procedural shapes, digits and lettering from the font baked into a mesh; every vertex conformed onto the body
  surface from the same loft as the mesh (no floating planes), later layers lifted above earlier ones, mirror places the
  symmetric copy (lettering stays readable, shapes are flipped), zones hood / roof / left / right / front / rear.
  `Evidence/art/customization/decals-V01.png`.
- Honest limits: procedural prototype art (the A-pillar paint panel and the mirrors' placement predate this work);
  fitment per chassis, ownership and presets come with the Core customization model (in progress); the variants are
  not yet selectable in the Garage nor sent to other players.

## V-043 — All five diversions online; toy data checked between client and control plane (2026-09-27)
- Two clients at the convoy's hosted tables (`ui-tour-social.ps1`): Greenlight 2 clean attempts each and each sees the
  other on the shared board; Cap Clash 2 + 2 shots; Pit-Crew 1 + 1 operations on one model; Canvas 3 strokes each,
  94/94 points — **PASS / PASS**. With Pocket Circuit at the shared table (V-030) every diversion has been exercised
  online. Screenshots `Evidence/ui/online/social-toys/`.
- `/healthz` publishes `toyContentHash`; the client compares it with its own toy documents at sign-in and, if they
  differ, keeps the online shared tables closed with an "update the game" note (Local toys unaffected). Today both are
  `698f4f53193d…`. Not exercised: 3–6 humans at one table.

## V-044 — Team Trials and group Time Attack online through the convoy screen (2026-09-27)
- Convoy screen: "Challenges · Team Trial" intent with the trial and difficulty the convoy snapshot lists (`teamTrials`,
  control plane); the game server splits the frozen roster by team (friendly AI drive for the humans) and uses the
  trial's hard timeout; the results line shows the team verdict and both team values.
- `ui-tour-online.ps1 -Trial TT_BEST` (C01 sprint, best time): "Team Trial TT_BEST (standard): 1 human + 5 friendly AI
  vs 6 opposing AI", 12 cars, P1 of 12, settled, `MUS_TT_BEST` unlocked — **PASS**. `-Trial TT_MEAN` (C03 circuit, team
  mean): P1 of 12, **Team Trial VICTORY**, `MUS_TT_MEAN` unlocked — **PASS**; rerun after the results line divided the
  opponents' total by the side size: "your team 3:07.480 mean, opponents 3:11.443 mean, provisional targets" — **PASS**.
  Screenshots `Evidence/ui/online/team-trial/`.
- `ui-tour-online.ps1 -Intent 4` (Freeplay Time Attack): 1 human, 0 AI, non-contact, settled — **PASS** (group Time
  Attack with several humans uses the same path; not yet run with more than one).
- Limits: the Combined Drift trial (TT_DRIFT) and Freeplay Drift Attack need drift scoring in the race server, which is
  not implemented; the trial definitions are the control plane's provisional fixture (no authored team.trials.json).

## V-045 — Customization Core model and catalogue (2026-09-27)
- Background agent, reviewed and re-run here: `Assets/Game/Core/Customization` — catalogue
  (`Assets/Content/Data/authored/customization.json`: all 18 chassis with stock + ≥ 2 variants per front/rear/side/rear-aero
  family or documented equivalents, e.g. roadster lip-spoiler instead of a wing; 8 rims with per-chassis fitment that
  keeps the tyre outer radius and never pushes a wheel past its arch; paints/finishes/two-tone; lamp tints; plates;
  decal library with render kinds, the 15 decal cosmetics mapped), `LiveryDocument` with canonical JSON and LiveryHash,
  strict validation and cosmetic ownership (preview vs apply), `LiveryEditor` (apply/cancel draft, 64-step undo/redo,
  64-layer cap), `AppearanceResolver` (falls back to stock with notices), compact wire form (≤ 5,120 bytes, measured
  worst case 4,833) and a publish gate. `dotnet test Services/CoreTests` **123/123**; compiles in Unity.
- Not wired yet: the Garage appearance editor, mapping to the renderer's `CarAppearance`, the online livery endpoint and
  roster sync, and `customization.json` in the content hash.

## V-046 — Garage appearance: livery editor, Local and online apply, liveries in race rosters (2026-09-27)
- **Renderer mapping** (`Runtime/Art/AppearanceMapping.cs`): Core `ResolvedAppearance` → `CarAppearance`; the renderer gained
  glass tint, rim finish, visual wheel offset, plate-style colours (backing + lettering) and decal opacity (blended toward
  the paint, decals stay opaque). EditMode `AppearanceMappingTests`: every chassis' stock livery draws exactly the body
  definition's car (rim fraction, no offset, shared glass, no decals) and a livery survives the roster wire form —
  EditMode **211/211**.
- **Appearance screen** (Garage → Appearance, `Front/AppearanceScreen.cs` + `AppearanceStage.cs`): body kit (families the
  chassis offers, with its notes), wheels (8 rims, diameter, offset, finish), paint (swatches, finish, two-tone, second
  and accent colours), lamps/glass/plate, decal layers (library shapes, zone, move, size, turn, mirror, flip, colour,
  opacity, order) and five+ presets; undo/redo, Stock, Cancel, Apply; locked cosmetics can be tried on, Apply refuses them;
  a turntable preview rendered on demand (seven views). The preview renders one frame after posing: with the PC pipeline's
  GPU Resident Drawer, rendering straight after moving the car drew its previous pose (found by the evidence run —
  "Right side" showed the "Front" pose — and fixed).
- **Store/apply** (background agent, reviewed and re-run here): `CarBuildWorkspace.AppliedLivery` (canonical JSON, persisted
  in the state document; old documents unchanged), `GarageOperations.ApplyLivery/UpdateVisualPreset/RenameVisualPreset`.
  Control plane: `livery-apply` / `visual-preset-update` / `-rename` validated with Core against `customization.json` and
  the account's `cosmetics_owned` (400 `invalid_livery` with the exact errors and locked items); canonical form and hash are
  computed server-side; a livery change refreshes only the convoy's cosmetic hash (readiness kept); `entrants[].livery`
  (wire form) + `cosmeticHash` frozen at start; `/healthz` publishes `customizationContentHash` (not in the race hash, which
  stays `5065000bb142…`); the client compares it and blocks online Apply on a mismatch.
  `dotnet test`: Core **123**, Builds **232**, Toys **92**, control plane **335** — all pass.
- **Race rosters:** `RosterEntry.Livery` (wire form); the game server relays a human's livery only when it decodes for that
  car against its own catalogue; clients and Local races draw it (`AppearanceMapping.ForWire`). The transport's
  `MaxPayloadSize` is 64 KiB so six worst-case liveries (≤ 5,120 B each) fit the match message.
- `-nsAppearanceTour` (built player, fresh Local profile): every section edited with the real controls, undo/redo, a locked
  swatch (Canal Jade Metallic) refused on Apply, applied (hash 7570c3b807c5…), two presets, the profile re-read from disk
  (livery, hash, presets persisted), S01 started with the car showing the livery (roster livery 258 bytes) — **PASS**.
  Screenshots `Evidence/ui/appearance/`.
- `ui-tour-online.ps1 -Appearance` (control plane + game server + client): livery applied online (hash 7ac36514a97c…),
  the event raced with it — the game server's roster carried 229 bytes, the car showed the lip, ducktail, "NS ONL" plate and
  decal — P2 of 3, settled — **PASS**. Screenshots `Evidence/ui/online/appearance/`.
- Limits: the pearl flip tint is not rendered (URP Lit has no view-dependent tint); decal opacity is a blend toward the
  primary paint, not transparency; TMP plate text also shows mirrored from the front at steep top views; a second human
  seeing another's livery in the same race was not run (the roster path is the same for every entrant).

## V-047 — Drift scoring in the race, AI that drifts, Drift Attack and the drift Team Trial online (2026-09-27)
- **Scoring:** `Race/DriftJudge.cs` feeds Core `DriftScorer` every tick for every entrant in the shared race simulation
  (server and offline): the route's judged drift zones and intended lines, route sectors (a chain banks at a sector end
  or finish), road contact, legal direction, debounced wall impacts and resets (they lose the unbanked chain). Results
  report the banked raw score (whole points); Drift Attack ranks finishers by it exactly as the control plane recomputes
  (ties share a place, then DNFs by distance) — the control plane's placement cross-check accepted every drift event
  below. Local results carry the score into Local progression facts.
- **AI drift driving** (`RouteFollower` drift mode, drift formats only): per zone visit, a handbrake flick toward the bend
  when up to speed and straight in the first half of the zone, then countersteer holding ~28° of slip (the handling
  harness' drifter) with the target leaning with the road; the attempt ends before the predicted path reaches the road
  edge, on a spin or wrong-way slide (the racing line catches it), and a zone that went wrong is not retried after a
  reset. Autopilots (tours) also hold reset when wedged, as a player would.
- **PlayMode `DriftAttackTests`** (validator autopilot V04 + three AI, 30× speed, `Evidence/courses/drift/*.json`), all
  four cars finish and finishers rank by score — **PASS** on C01 (8,071 / 5,538 / 1,468 / 293 pts), C08 (2,749 / 1,124 /
  1,063 / 11) and C12 (2,118 / 1,374 / 1,273 / 1,100). `FullGridContactTests` 2/2 still pass; EditMode **211/211**.
- **HUD:** drift readout under the clock (banked score, the running chain with its multiplier, a short BANKED / CHAIN
  LOST note) — offline from the simulation, online from a 10 Hz per-driver server message (`ns.drift`; the snapshot format
  and protocol version are unchanged). The match message now carries the freeplay format.
- **Online** (control plane + game server + client, `ui-tour-online.ps1`): convoy screen "Freeplay · Drift Attack" on
  C01 (`-Intent 6 -Course C01`): 1 human + 3 AI, the autopilot banked **6,983 pts**, P4 of 4 by drift, settled as
  `FreeplayDriftAttack` — **PASS**. `-Trial TT_DRIFT`: 1 human + 5 friendly AI vs 6 opposing AI on C01, autopilot
  **2,545 pts** (P5 of 12 by drift), **Team Trial DEFEAT 10,900 vs 18,668 pts** settled — **PASS**. Screenshots
  `Evidence/ui/online/drift/`.
- Found and fixed: the provisional TT_DRIFT fixture used C02, which has no judged drift zones (it could never score);
  it now uses C01 (three zones). Control plane tests 335/335.
- Limits: the AI's drift is a scripted controller (no difficulty scaling of drift skill yet); drift targets for
  campaign benchmarks were not exercised here; the convoy screen does not yet restrict Drift Attack to courses with
  judged zones (C01, C04, C08, C12, C15, C16, C23–C25, FP01 have them).

## V-048 — Starter paths measured; Normal reference runs; wet-grip fixes for AI and online prediction (2026-09-27)
- **F10 (hardware, identical scripted inputs):** EditMode `StarterPathTests` walks every main recipe step of V01/V02/V03
  through the handling harness (`Evidence/progression/starter-paths/*.json`). Normal-path end vs stock — V01: 0-100
  −13 %, 1,000 m +7.7 %, 100-0 −12 %, skid pad +14.5 %; V02: −15.5 % / +10.5 % / −10 % / +13.4 %; V03: −19 % / +9.7 % /
  −10 % / +13.1 %; every step within the PI cap of the stage it is meant for — **PASS 3/3** (every measure improves, at
  least two by ≥ 10 %). EditMode **214/214**.
- **Reference runs (PlayMode `ReferenceRunTests`, 30 Normal stages, validator autopilot, solo, stage surface):** each
  starter stock vs its intended build by that stage (`Evidence/progression/reference/N-S*.json`) — all finish, all
  intended builds legal; the build is faster on every stage, median 4 % (1–2 % in Act I up to 5–7 % in Act IV). The
  provisional time targets (catalogue expectedSeconds) are 1.2–2.4× slower than these runs, i.e. they gate nothing.
  A candidate rule (median intended-build time + 5 %) would leave stock starters sufficient in Acts I–II and insufficient
  in Act IV (23/27 stock misses, 0/27 developed misses). **Not adopted:** Addendum 03 §9 requires real elevation, 3D
  gates and recovery before certifying benchmarks, and the validator is a conservative driver far from the car's limit
  (a more aggressive reference profile was tried and hit walls; reverted).
- **Found by the reference runs and fixed:** AI planned wet/damp corners and braking with dry tyre grip (V03's build
  took 451 s on wet C08 with repeated walls); `RouteFollower.SurfaceGrip` now carries the event's weather grip for every
  AI and autopilot — rerun: C08 172–174 s, walls 0–1. The online client also predicted its own car with dry grip in
  wet/damp events while the server simulated the real surface; the match message now carries the server's surface and
  the client predicts with it (not yet exercised online in a wet event).
- `StarterCampaignRunTests` (F08 full campaign per starter, buying the path with earned credits) is written but **not
  run yet** (deferred behind Addendum 03's topology work, since benchmarks and stage geometry will change).

## V-049 — Addendum 03 slice 1: measured course profiles, finite 3D gates, layer-aware progress, safe recovery (2026-09-27)
- **T01 measured profiles** (PlayMode `CourseProfileTests`, all 29 courses, `Evidence/courses/profile/*.json`): from the
  generated centreline the race/AI/progress use AND the road collider under it (raycast every 5 m — 100 % of probes on
  the centreline on every course, worst gap 0.01 m): 3D driven length, start/end/min/max height, ascent/descent, grade
  (20 m windows) and banking ranges, a 25 m trace, against the authored targets. Descents to 640 m (C25), climbs to
  580 m (C23), grades up to ±11 %, circuits closing at start height. Found flat: C14 (3.3 m, 2°), FP03 (0 m) and the T00
  loop (0 m). Fixed with restrained, identity-preserving relief (route revisions bumped): C14 a raised market deck
  (+5 m) and banked S bends/square turn (6.0–13.5 m, grades ±5 %, 5°); FP03 taxiway esses dip/rise (378–383 m, ±6 %,
  7° maintenance turn); T00 a 6 m crest on the far banked loop (the flat braking lane, skid pad, bays and wet pad
  untouched). Re-measured and each driven start to finish by the autopilot, clean — **PASS**. Descents are still
  perfectly monotonic (no counter-slopes/compressions yet — noted for an authoring pass).
- **Progress (D307, R01–R05):** the progress locator never jumps to another stretch of road (no global nearest search
  after the start; a car away from its tracked stretch is Lost) and every location carries a road-layer envelope
  (−2.5…+12 m from the surface): a car below its road — fallen onto a lower switchback — is off the route. Checkpoints
  are finite directional 3D gates: accepted only for a swept forward crossing of the gate plane, inside its lateral
  corridor and vertical envelope (−1.5…+8 m), with plausible travel through it, by a car tracked beside the gate.
  Ranking uses legal progress, capped at the next un-crossed gate and held while off route. EditMode
  `RouteProgressTests` on the real routes: R01 every gate once at 1.5 and 12 m per tick; R02 oscillating ×10 counts
  once, a vertical drop into a gate does not count (found: numerical plane straddling credited it — fixed with the
  plausible-travel rule); R03 C25's upper road 41 m above a later stretch — a fall gives no gate, no finish, no ranking
  gain, off-route > 2.5 s, recovery on the upper road at/behind the last gate with one +3 s; R04 C03's orchard bridge —
  passing under its gate on the lower road is refused, on the deck accepted; R05 C03 two laps — backing over the line
  adds no lap, finishes only on the final lap; anchors never move forward — **PASS**. EditMode **221/221**.
- **Recovery (D306, §7):** hold 0.75 s, then the button must be released before another reset; the marshal recovers a
  car off the legal route for 2.5 s (by route and road layer — the global "40 m below" rule is gone) or a wedged AI;
  anchors at the last accepted gate, else a side lane, else stepping back up to 60 m (never forward, never before the
  start), skipping placements another car occupies; moving protection ends at 2 s — a car still overlapping is moved
  to a free non-forward anchor (same recovery, no second penalty); each completed recovery is one event (time, reason,
  from/to, 3 s).
- **Regression:** PlayMode `CourseSweepTests` 29/29 (every gate accepted in real driving), `FullGridContactTests` 2/2,
  `DriftAttackTests` 3/3 after re-tuning drift for the wet-grip planning (drift zones now cap the approach at a
  controllable entry speed, the flick holds full lock up to 0.75 s like the harness drifter; C08 every flick now
  becomes a held drift, no spins; C01 810–4,225, C08 1,797–3,806, C12 527–2,163 pts).
- Not yet: recovery prompt/countdown on the HUD, occupancy against physics obstacles (only cars), online recovery
  through the client (server authority unchanged), physical fall test in PlayMode, overturned-car prompt.

## V-050 — Addendum 03 slice 2: instruments, five driving views with a fitted cockpit, arcade camera, remappable controls, recovery prompts (2026-09-27)
- Revision: working tree on `791ea43` (committed in the next checkpoint). Windows development player built from it with
  `BuildCommands.BuildGame` (three builds during the slice; the evidence below is from the last two). 1920×1080 windowed,
  uncapped frame rate (380–720 fps measured — 30/60/120 fps runs NOT yet done). Isolated preference and profile folders
  (`-nsPrefsFolder`, tour profile folders under `Builds/`), never the player's own settings.
- **Instruments (A3.1, G01–G03, G07, G08):** `SpeedDisplay` (one canonical road speed → km/h ×3.6, mph ÷0.44704; scale
  from the car's envelope rounded to majors; 250° dial), `SpeedCluster` (Instrument Dial or Digital Strip, RPM bar and
  gear kept separate), `DrivingPreferences` (versioned file, defaults Dial/km/h/Chase Close/Arcade, corrupt file set
  aside, unknown values fall back per field). `VehicleSimulation` telemetry now carries road speed (velocity on the
  support plane; the last plane while airborne). EditMode `InstrumentTests` (G02 conversions at 0/10/26.8224/100 m/s,
  needle vs scale in both units and end-stop over range; G04 standstill wheelspin/fall/crest/reverse; G07 defaults,
  round trip, corruption, Reduced Motion; G08 repeated changes do not grow the cluster) — pass. Built-player
  `-nsInstrumentTour` (Settings controls with live preview → S01 on the autopilot with the Dial in km/h → Digital Strip
  in mph mid-run: tick advanced, checkpoints kept, build unchanged, still racing) — **PASS** on two launches (the second
  loaded Strip/mph from the file written by the first).
- **Five views on all 18 cars (A3.2, C01, C03–C05):** `DrivingCamera` — Chase Close, Chase Far, Hood, Bumper/Road and
  Cockpit — anchored per car from its own body loft (`CarBodyGenerator.Cabin`: seated eye over the authored driver side,
  hood and bumper points); `CockpitBuilder` fits a cabin into each chassis (door cards, liner, pillars, inner glass,
  floor/firewall/bulkhead, dashboard, binnacle with live speed/unit/gear/rev bar, a three-spoke wheel that turns with
  the steering at 13:1); in Cockpit view the car swaps to an open-cabin body variant. Built-player `-nsCameraTour`
  (run 2, final cockpit): every one of the 18 cars raced solo on one of C01/C05/C08/C12/C03/C14 (day, night, wet, circuit)
  by the autopilot, each view held ~3 s while driving — **coverage 90/90 car×view combinations driven** (48–178 m per
  view), contact sheets + ledger in `Evidence/ui/cameras/` (camera pose in car space, FOV, road speed, distance driven,
  drift framing, collision distance, speed-line strength/peripheral check, cockpit visible, wheel angle, image
  statistics, fps). Hood vs Bumper: ≥ 0.73 m apart in height and ≥ 0.65 m fore/aft on every car; the view preference
  survived each of the 18 race scene loads; the tour's temporary views never overwrote it. Five views also shot inside
  the C08 tunnel section at 3,077–3,231 m. **PASS** (automation — not a human comfort/readability judgement).
  Found and fixed on the way: run 1 PASSED its automated checks while the cockpit was visibly wrong — the lower body's
  top skin (a closed loft) covered the dash in paint, the windscreen header hung 6 cm into the view, a spoke and the
  hub hid the dials and a cabin light made a hot spot (`Evidence/ui/cameras/run1-before-cockpit-fix/`). Fixed with the
  open-cabin body, a slim header, a lower seated eye, spokes at 9/3/6 o'clock, dials placed on the sightline through
  the wheel, and no cabin light (self-lit figures). Editor sheets `cockpit-day.png`/`cockpit-night.png` show all 18.
- **Cycling, look-back, remapping (C04):** Settings → Controls lists every driving action's keyboard/controller binding;
  a binding is changed by pressing the new key/button (Esc cancels), shared bindings are warned, Restore defaults clears
  them; remaps are stored in the driving preferences (by action + binding index — code-built maps get new binding ids
  each session, so the Input System's own override JSON would not reload; found by `ControlsTests`) and loaded by every
  driving session. In the built tour: Change View remapped to **V through the Controls screen**; afterwards C did nothing,
  V (virtual keyboard) and Select (virtual controller) alternately cycled Chase Close → Chase Far → Hood → Bumper →
  Cockpit → Chase Close; V typed into a focused text field did not change the view; Look Back held (camera facing −0.85
  along the car) and released back to Hood (+1.00). Typing also releases steering/throttle and ignores reset/pause.
  Found and fixed: in the Test Yard the controller's Select both changed the view and left the yard — leaving is now
  the remappable Pause/menu action (Esc/Start).
- **Camera behaviour (A3.3, C05–C08, C10):** EditMode `CameraTests` with a synthetic clock — at rest nothing moves in any
  view (no idle wobble/zoom); five seconds of wall grinding stays ≤ 0.4 m and decays < 1 s after contact, Comfort has no
  impact response; drift framing ignores a 0.1 s flick and frame-to-frame slip noise, a linked right→left drift changes
  side once and only after the framing released (found: noise across the threshold flipped sides — fixed with a
  same-side hold), no framing airborne or at walking pace; a teleport clears history and cuts; cycle order, persistence,
  temporary overrides and look-back (found: look-back aimed forward from ahead of the car — fixed); Arcade/Comfort/
  Custom/Reduced Motion change drift framing and speed FOV observably; speed lines none at a standstill or 20 m/s,
  Strong > Subtle, a valid fast drift strengthens them, every streak outside the central 70 %, canvas below the HUD;
  anchors inside every car's own cabin — **8/8 pass**. EditMode total **237/237**.
- **Recovery prompts (A3.6, R03, R10):** the simulation reports each car's offer — reset hold progress, OFF ROUTE and
  OVERTURNED countdowns to the marshal (2.5 s off the legal route; 3 s on its side/roof and nearly stopped — new), and
  a plain offer for a human stopped 3 s (never taken away); the HUD shows it with the current reset binding, then
  "RECOVERED +3.000 s · clock running". Practice and the Test Yard share the 0.75 s hold with release-to-rearm. Online
  the client shows the hold, an overturned countdown from its predicted car and a notice when the server moves it (no
  off-route countdown online yet). PlayMode `RecoveryPhysicalTests` with real physics on real scenes: C25 — a car
  released beside the upper road at 6,482 m **fell 42.1 m** toward the later lower road and was recovered after 3.35 s,
  on the upper road at/behind its last gate, one +3 s, no gate and no ranking gain; C01 — dropped on its roof, offered
  the reset after 1.27 s, recovered upright after 3.52 s, not again afterwards; C01 — parked on the handbrake 5.9 s:
  offered, not recovered — **3/3 pass**. `ScoringVersion` → `classify-2` (records set under the old classification no
  longer compare). Regression: `CourseSweepTests` 29/29 (evidence unchanged byte for byte), `FullGridContactTests` 2/2
  (12/12 finished, 0 resets, winner 83.137 s as before), `DriftAttackTests` 3/3.
- Not yet: 30/60/120 fps and correction timing (C11); six clients with different views (C12); camera collision at cliff
  walls/bridge supports/tunnel mouths in every view and FOV extremes (C09); spectator target loss, replays and
  cinematic overrides; human playtest of comfort. **Tunnels are not built**: the eight authored tunnel sections
  render as open road (no shell), so the "tunnel" shots are night road, not a tunnel.

## V-051 — Tunnels and galleries built for every authored tunnel section (2026-09-27)
- Revision: working tree on `f9c4e6a` (committed in the next checkpoint). Unity 6000.6.3f1 editor PlayMode on the real
  course scenes (runtime generation, Full profile).
- `TunnelGeometry` builds each route section of kind `tunnel` (previously parsed but never built — the eight sections
  rendered as open road): lining walls outside the road's own barriers, an elliptical vault (rock/horizon/blue-marked/
  service tunnels) or a flat roof (galleries), concrete portal rings, rock faces closing the portals, a rock mound over
  enclosed tunnels whose feet follow the terrain, galleries open to the valley between pillars with the mound on the
  mountain side only, lamps and Forward+ point lights along the roof (warm sodium for rock/service, cool for the blue-marked
  tunnel). Walls and pillars are Barrier colliders on every profile (the headless server builds them too); the roof is a
  Scenery collider for the driving camera. Route revisions of C08, C12, C21, C22, C24, C25 and FP02 bumped to 2 (records
  key on the route hash).
- PlayMode `TunnelShellTests` (7 courses, 8 sections): roof over every 5 m probe (6.6 m arches, 5.6 m flat roofs), road
  still drivable at every probe, **0** tunnel colliders inside the driving corridor between the barriers (0.3–4 m),
  9–26 lights per course — **7/7 pass**; portal and interior renders in `Evidence/courses/tunnels/`.
- Regression: `CourseSweepTests` 29/29 and `CourseProfileTests` 29/29 — the seven tunnel courses' evidence changed only in
  `routeSourceHash` (finish times, distances and profiles identical).
- Not yet: bridge/viaduct decks and piers (C03, C11, C13, C17, C20, C25 sections); tunnel shots in the built camera tour
  (the V-050 tour predates the shells); camera collision measurements at portals (C09).
- Also since V-050: Arcade camera roll 0.15 → 0.2 so the Settings row (10 % steps) shows the value in use; text-size row
  label shortened; Controls columns headed KEYBOARD / CONTROLLER (seen in the V-050 instrument tour's last run).

## V-052 — Camera timing at 30/60/120 fps, six clients with different views, occlusion at FOV extremes, simultaneous recoveries (2026-09-27)
- Revision: working tree on `bc38bf0` (committed in the next checkpoint). Windows development players built from it; local
  control plane (loopback) and dedicated server process; Unity 6000.6.3f1.
- New launch arguments for every role (`CameraProbe`): `-nsPrefsFolder` (per-process driving preferences), `-nsTargetFps`
  (vsync off + cap), `-nsCameraProbe` (per view: frames, achieved fps, on-screen car jitter as the second difference of
  its viewport position in 1080p pixels, frames with the car→camera line blocked (exterior) or the camera inside a
  collider (mounted), largest impact offset, view timeline, style/units), `-nsProbeCycleAt`. `net-race.ps1
  -CameraClients` seeds each client's preferences and cap.
- **C12 + C11 online** (`Evidence/net/run-20260927-120209-h6-C01-ai6`): 6 rendered clients + 6 AI on C01 (a first attempt on
  C12 failed honestly — the dev test accounts do not own it, `course_locked`; no course was bought to get round it;
  `run-20260927-114426-h6-C12-ai6`). Clients seeded Chase Close/dial/km/h@60, Chase Far/strip/mph@30, Hood/dial/mph@120,
  Bumper/strip/km/h@60, Cockpit/dial/km/h@30, Chase Far/strip/mph@120; achieved 58.5, 29.3, 117.0, 58.5, 29.3, 116.9 fps.
  Every client kept its own view, style and units for the whole race; only client 0's scripted cycle changed a view
  (Chase Close → Chase Far at 57.7 s) and no other client's timeline moved. All six finished (exit 0).
  Found and fixed: the online client drew its own car at the newest predicted tick without interpolation — Chase Far
  jitter mean/p99 was 16.1/171.7 px at 30 fps and 4.7/25.7 px at 120 fps (`run-20260927-115531-h6-C01-ai6`); after
  interpolating between the last two predicted ticks: 0.73/11.0 px at 30, 0.12/0.96 px at 120, Chase Close 0.16/0.26 px at
  60. The bumper camera touched the road collider in 94 of 7,143 frames — now kept ≥ 0.16 m above the road: 2 of 6,811.
  Exterior views: 0 occluded frames on every client.
- **C11/C09 offline** (built camera tour with `-nsTourCars V01,V04,V11`, probe, `Evidence/ui/cameras/fps-fov/`): 30 fps cap
  (29.9 achieved) chase jitter mean 0.16–0.29 px, p99 3.5–5.6 px; 120 fps (119.0) mean ≤ 0.1 px, p99 ≤ 0.7 px; FOV 80° and
  50° (uncapped ~660 fps) mean ≤ 0.15 px. **0 occluded and 0 inside-collider frames in every view** across the yard, the
  three races and the C08 tunnel run, at both FOV extremes and both caps; coverage 15/15 driven each pass — **PASS ×4**.
  Tunnel frames now show the V-051 shell (tunnel lights were brightened afterwards: 2.2 → 3.6, range ×1.15; re-rendered in
  `Evidence/courses/tunnels/`, not re-driven).
- **R08** PlayMode `RecoveryPhysicalTests.R08_SimultaneousRecoveries_GetSeparateNonForwardAnchors`: the player and three
  AI thrown off C01 at the same tick were each recovered once on the same tick at 164/156/148/140 m (from 214–216 m —
  stepping back, never forward), laps kept, one +3 s each, closest pair 8.0 m, protection ended — **pass** (4/4 in the
  class).
- **R07** by construction (inspected): a client's only message into the race is its quantized steer/throttle/brake and a
  button byte (`Wire.ReadInput`); a reset is the held bit, and anchor, penalty, lap and progress are chosen by the server —
  no field can carry a forged pose, lap, checkpoint or penalty. No fuzz test of the input channel yet.
- Not yet: authoritative-correction stress (packet loss/latency) on camera jitter; spectator target loss; R11 (recovery then
  network loss/rejoin); bridges/viaducts.

## V-053 — Readability at 720p/ultrawide with large text and HUD (G06); bridges and viaducts built (2026-09-27)
- Revision: working tree on `dd6402f` (committed in the next checkpoint). Windows development players; Unity 6000.6.3f1.
- **G06** built `-nsInstrumentTour` at 1280×720 and 2560×1080 with Text size 150 % and HUD size 130 % (seeded isolated
  preferences), and at 1920×1080 defaults. The first 720p/ultrawide runs FAILED on inspection (the automated tour passed):
  the race position and clock vanished, the dial lost its scale numbers, Settings row labels were cut mid-word and the
  preview caption overlapped its note (screenshots not kept — replaced by the rerun). Causes: every factory label clipped
  with TMP Truncate, which drops a whole line that is too tall for its box; and the HUD size setting was stored but never
  applied. Fixed: labels shrink to fit (to 50 %) and overflow rather than vanish as a last resort; live figures (clock,
  speed, gear) keep a fixed size and never clip; instrument figures follow HUD size, not menu text size; the HUD canvas
  scales with HUD size; position and progress have separate bands; a backing panel behind the standings (names were faint
  on a bright sky); post-creation size changes go through `UIFactory.Resize`. Reruns at 720p, ultrawide and 1080p —
  **PASS**, with every HUD element present and legible on inspection (`Evidence/ui/instruments/g06/`). `-nsUiTour` (all
  offline screens) — **PASS** after the label change.
- **Bridges/viaducts** (`BridgeGeometry`): each route section of kind `bridge`/`viaduct` (previously parsed, not built)
  gets a deck girder under the road, piers to the ground (Barrier colliders on every profile; a pier that would land on
  another road is moved along to the next station that can stand), and truss sides for steel styles; free spans get
  ground carved away beneath them (13 m bridges, 16 m viaducts, easing out over 45 m at the abutments); the C03 overpass
  and C25's two-level bridge keep the terrain the road below needs. Route revisions of C03, C11, C13, C17, C20 bumped to
  2 and C25 to 3. PlayMode `BridgeTests` (6 courses, 7 sections): deck drivable at every 5 m probe, **0** bridge colliders
  in the driving corridor, **0** in the corridor of the road passing under the C03 overpass (8 probes) and the C25 upper
  deck (7), open ground ≥ 11.7 m under free spans, 2–102 piers per course — **6/6 pass** (found: the C03 overpass first had
  no piers — every slot landed on the lower road). Renders in `Evidence/courses/bridges/`. Regression: `CourseSweepTests`
  29/29 and `CourseProfileTests` 29/29 (only route hashes changed), `TunnelShellTests` 7/7. EditMode 237/237.
- Not yet: bridge approaches drive-tested in the built camera tour; the C25 lower deck stands on its embankment (the upper
  road's two-level structure is not modelled as one bridge).

## V-054 — Online recovery offers, camera under impaired networking, soak, record versioning (2026-09-27)
- Revision: working tree on `1108e35` (committed in the next checkpoint). Windows development players, dedicated server
  process and local control plane — all on this machine (loopback), so every network figure below is localhost plus the
  application-level impairment, not a remote network.
- **Online recovery display:** the server now sends each driver its own recovery offer 10×/s (`ns.recovery`: off route /
  overturned / stopped, seconds to the marshal, completed count and last reason); the client counts down from it, shows a
  notice when the server completes a recovery and logs both. `-nsAutoResetAt` makes an automated client hold reset.
- **Impairment:** Unity Transport's debug simulator is a no-op in this Netcode version (its replacement is a package not
  approved here), so `-nsImpair delay,jitter,drop` holds back, jitters and drops incoming snapshots and outgoing input
  packets inside the client (stale snapshots discarded as the sequenced channel would). `net-race.ps1 -Impair -ResetAt`.
- **Run** `Evidence/net/run-20260927-130057-h6-C01-ai6-impair` (6 rendered camera clients + 6 AI, C01, 80 ± 20 ms each way,
  3 % loss): all six finished; RTT 175–235 ms; 47–91 snapshots and 200–242 input packets dropped per client; 32–418
  reconciliations. Client 0's scripted reset: one recovery completed by the server ("manual", total 1). Clients 1 and 2
  stopped in traffic, were offered the reset on the HUD ("Stopped", from the server message) and the autopilot's stuck
  fallback held it — one recovery each. Chase Far jitter under these corrections was 4.9/140 px (30 fps) and 6.0/77 px
  (60 fps) mean/p99. Found and fixed: correction hiding decayed exponentially from the moment a correction landed (a
  velocity kick) and snapped anything over 3 m — now a critically damped blend with a snap only beyond 8 m (a real
  discontinuity, where the camera cuts). **Rerun** `run-20260927-130520-h6-C01-ai6-impair`: all six finished; 30 fps Chase
  Far 0.64/10.2 px, 120 fps 0.37/6.7 px, but client 0 (390 reconciliations, heavy contact) still 8.2/73.9 px at 60 fps —
  **not solved**: under 190 ms RTT with contact the predicted car is corrected often enough to show hitches. 0 occluded
  and 0 inside-collider frames on every client; each client's view/style/units unchanged except client 0's scripted cycle.
- **I04 soak** built `-nsSoakTour 8` (`Evidence/ui/soak/soak.csv`): 8 back-to-back twelve-car light-contact races
  (autopilot + 11 AI) on C01/C08/C12/C03, each 70 s of racing with the view cycled every 1.5 s through all five, look-back,
  speedometer style/units switched, and a held reset every 12 s — after every race, back in the menus: 1 camera, 0
  driving cameras, 0 vehicle views, 0 race HUDs, 0 speed-line canvases, 0 preference listeners, 1 light; every one of 48
  reset requests gave exactly one reset; authoritative progress 9–15 gates per race; frame time 1.77–2.22 ms mean, 3.2–3.8
  ms p99 with no drift — **PASS**. Managed heap after a full collection grew 7.2 → 10.6 MB (~0.5 MB per race) — small but
  monotonic; source not yet identified (noted, not claimed fixed).
- **I02/I03** EditMode `RecordVersionTests` through the game's own key builder: a best set under `classify-1` survives a
  JSON round trip, is shown as a legacy result for the same event under `classify-2` (never compared), a slower current
  result becomes the current best, and the old entry is not erased — pass. EditMode **238/238**.

## V-055 — Relief pass: no more mathematically monotonic sprints (Addendum 03 §5.1) (2026-09-27)
- Revision: working tree on `ca4551b` (committed in the next checkpoint). Unity 6000.6.3f1 editor PlayMode on the real
  generated scenes.
- The profile measurements (V-049) showed 16 of the 22 sprints strictly monotonic — every descent without a single rise
  and every climb without a dip, against §5.1 ("do not force a mathematically monotonic descent at every vertex"; allow
  compressions, flat staging areas and small counter-slopes). `Tools/courses-relief-pass.py` (deterministic per course)
  re-shapes only control-point heights of C01, C02, C04, C06, C07, C10, C12, C16, C17, C19, C20, C21, C22, C23, C25, FP02:
  the grade varies along the route (steeper and gentler stretches — compressions and sightline changes), three short
  windows per course reverse it gently (about a quarter of the local grade for ~100 m) between flatter approach/exit
  stretches, start and finish heights are exact, tunnels/bridges/viaducts (±60 m), the first/last 100 m and stacked
  crossings (±80 m; separation never reduced below 6 m) are untouched. Report: `Evidence/courses/relief/relief-pass.txt`.
  Route revisions bumped (+1 each).
- Measured on the generated road (`CourseProfileTests`, `Evidence/courses/profile/`): every descent now has counter-slopes
  (max grade +1.1 … +3.2 %, was 0.0), every climb dips (min grade −1.0 … −2.9 %, was 0.0); steepest measured grades
  12.0–13.8 % (were up to 11.4 %). Regression on the new geometry: `CourseProfileTests` 29/29, `CourseSweepTests` 29/29
  (autopilot start to finish, no corridor cuts), `TunnelShellTests` 7/7, `BridgeTests` 6/6, `RecoveryPhysicalTests` 4/4
  (C25 fall, simultaneous recoveries, roof, stopped), `DriftAttackTests` 3/3, `FullGridContactTests` 2/2 — **80/80**;
  EditMode 238/238.
- Soak follow-up: 10 races with the same car every race — PASS, managed heap 7.3 → 11.3 MB (~0.44 MB per race), so not
  per-car caching; no growing static collection found. A census of loaded Unity objects after each of 6 races (built
  `-nsSoakTour 6 -nsSoakSameCar`) stays flat — meshes 206, materials 113–114, textures 126, audio clips 0, GameObjects
  246 — so no scene, car, HUD or asset leaks; only the managed heap creeps (~0.5 MB/race; Mono's collector is
  conservative and non-moving, retention cause unidentified). A 40-race soak is running to see whether it plateaus.
- `ReferenceRunTests` (30 Normal campaign reference runs, stock and intended build, autopilot) on the new geometry:
  **30/30 pass** (every stage finished in both builds, legal PI); 20 of the 30 evidence files changed (times on the
  reshaped courses). Benchmark certification stays deferred (Addendum 03 §9).

## V-056 — Instrument extremes in a built Test Yard session (G04/G05) (2026-09-27)
- Revision: working tree on `9660e29` (committed in the next checkpoint). Windows development player; isolated
  preferences/profiles; Instrument Dial, km/h.
- Found and fixed first: the dial needle is smoothed (80 ms) and nothing reset that history on a discontinuity, so after a
  reset or recovery at speed the needle glided down from the old speed — the "stale previous-car telemetry" G04 forbids.
  `RaceHud.NotifyDiscontinuity` now snaps it wherever the camera already cuts (offline recovery, practice reset, Test Yard
  run/A-B switch, online server move > 8 m).
- Built `-nsInstrumentExtremes` (`Evidence/ui/instruments/extremes/`), scripted inputs, every rendered frame sampled
  (13,677): standstill wheelspin (handbrake + full throttle, engine to 3,588 rpm) showed ≤ 2 km/h; a full-throttle launch
  through 3 gear changes never moved the displayed speed at a shift beyond the physical change; a reset at 112 km/h
  showed 1 km/h two frames later with the needle at zero (124.3°, zero = 125°); braking to rest with the brake held
  engaged reverse, shown as a positive 43 km/h; a handbrake slide on the skid pad reached 67° body slip. In all frames the
  number equalled the road speed within 1 km/h, with 0 negative values, 0 needle positions outside the 250° sweep and 0
  NaN/blank — **PASS**. Crest airtime and void falls stay covered by EditMode `InstrumentTests` (G04), not this run.
- Long soak (40 races, same car) is still running from a separate copy of the previous build.

## V-057 — R11: recovery then network loss and a re-entry attempt (2026-09-27)
- Revision: working tree on `716ec13` (committed in the next checkpoint). Built dedicated server + 3 client player processes
  (batch mode) + local control plane, loopback; `net-race.ps1 -Humans 3 -FreeplayCourse C01 -FreeplayAi 3 -ResetAt 20
  -DropAfterReset` (`Evidence/net/run-20260927-141143-h3-C01-ai3`).
- Client 0 held reset at 20 s; the server completed the recovery (client received total 1); the client then dropped its
  connection and, 2.5 s later, tried to come back into the race with the same ticket: **refused** (`ticket_Replayed` —
  single-use tickets; the server also refuses any entry after the countdown). The server recorded the entrant once as
  `DisqualifiedDisconnect` (checkpoint fraction 0.19, not clean — the reset counts), the event still had 6 entrants (no
  new entrant, no AI replacement), the two other humans finished and were credited normally (6,460 and 7,461), and client
  0's receipt settled once as DisqualifiedDisconnect with a payout of 0 and an unchanged wallet — **PASS**. Localhost only.

## V-058 — Spectating a running race (spec §4.4; Addendum 03 C05 target loss) (2026-09-27)
- Revision: working tree on `d15b669` (committed in the next checkpoint). Built dedicated server + 3 client processes + local
  control plane, loopback (`Evidence/net/run-20260927-142546-h3-C01-ai3`; the first attempt,
  `run-20260927-142202-h3-C01-ai3`, connected the spectator but a headless spectator never chose a target — its target
  rule wrongly required a rendered car and its upkeep ran after the headless early return; both fixed).
- Race server: accepts spectator tickets (the control plane issues them only to members of the convoy's match), at most 6,
  until results; a spectator gets the match with no car of its own, phases, compact snapshots of every car and the
  results; anything it sends as input is counted and ignored. Client: spectate mode — no prediction, no input; the camera
  follows one entrant (humans first), next/previous by the shift bindings (E/Q, RB/LB), moves on by itself when the
  watched car leaves the event, shows "No drivers to watch" when nobody is left; each change is a camera cut and an
  instrument snap (C05). Convoy screen: "Spectate the Race" while the convoy's race runs for a member flagged as a
  spectator (presence "Spectating").
- Run: client 0 reset at 15 s, dropped, was refused re-entry (`ticket_Replayed`), requested a spectator ticket and
  watched the rest of the race: first Driver 2 (a human), then Driver 3, AI-1, AI-2 (next ×3), then held Driver 2; client
  1 (Driver 2) left the race 45 s after the start and the spectator moved to Driver 3 by itself ("target left the event"),
  then received the results as a non-entrant — 6 target changes, 1 target loss handled. The spectator also sent 40 input
  packets (full throttle + held reset): the server counted **40 ignored**, and every result stands (client 2 finished P4
  and was credited 6,460; the three AI finished 1–3). Clients 0 and 1 settled once each as DisqualifiedDisconnect with 0
  payout — **PASS**. Localhost only; the Spectate button itself was not driven by automation (the protocol path it calls
  was).

## V-059 — Soak memory: generated course assets released; per-frame native growth found and fixed (2026-09-27)
- Revision: course-asset release and census in `182ac6e`; bisect switches, the display-string fix and the final soak on the
  working tree of `df0d13a` (committed in the next checkpoint). Built Windows development players on this machine,
  `-nsSoakTour N -nsSoakSameCar` (the V-054 soak: twelve cars, light contact, view cycled every 1.5 s, look-back, style/units
  switched, a held reset every 12 s, then back to the menus), working set sampled every 15 s from outside the process.
- **Before:** the 40-race run of `6c69aa0` was stopped at race 20 to fix what it showed: managed heap after a full
  collection 7.2 → 16.0 MB (+0.46 MB every race, never levelling) and working set ~497 → ~892 MB, while the object census
  (meshes, materials, textures, clips, GameObjects) stayed flat.
- **Cause and fix:** `CourseRuntime` released its generated road/terrain meshes, terrain data and `TrackData` only in edit
  mode; in play they were left to scene unloading and asset garbage collection. `OnDestroy` now destroys them explicitly
  in play too (the exact reference that had kept them alive was not traced; the growth stopped with the change). The census gained terrain data, ScriptableObjects, Unity's
  allocated/reserved memory, Mono heap, graphics-driver memory and rigidbodies, and a reflection census of every static
  collection and static event in the game's assemblies (by field name, race 1 vs the end).
- **After, 8 races** (`soak-fix`): **PASS** — every accumulation check clear, 48/48 reset requests → one reset each, 9–10
  gates per race; managed **7.1–7.4 MB flat**; terrain data 1 (the menu backdrop's), ScriptableObjects 98 throughout;
  frame 1.83–2.50 ms mean, 3.61–4.48 ms p99; working set 516 → 678 MB.
- **After, 12 races** (`soak-native`; races 1–3 overlapped an online tour and two editor builds on the same machine, so
  their frame times are not clean): **PASS**; managed 7.0–7.7 MB; 84 non-empty static collections/handlers, **none grew**;
  Mono heap 90 MB, graphics driver 121 MB and 0 live rigidbodies in the menus throughout — but Unity's own allocated memory
  rose 228 → 340 MB (~9–10 MB per race, steady) inside an unchanged 822–826 MB reservation; working set 536 → 691 MB.
  Not explained by any counted object type, static, handler, texture, graphics or physics body.
- **Bisect of the native growth** (built players, `-nsSoakTour` switches; per-category `ProfilerRecorder` counters):
  course loads alone (`-nsSoakLoadsOnly`, 16 loads) flat at 212 MB; races without view/style switching (`-nsSoakPlain`)
  still +~12 MB/race; the player's car alone (`-nsSoakAi 0`) +~17 MB/race at ~560 fps but only +~1.4 MB/race capped at 60
  fps → **per rendered frame** (~0.35–0.43 KB each), not per car or per simulation tick. Every Unity memory category
  (textures, meshes, materials, graphics, audio, profiler, 4,214 objects, 2,752 assets) stayed flat. Camera rendering off,
  HUD/speed lines off, audio off and the driving camera's logic off all kept growing at the same rate per frame; 60,000
  camera sphere casts and 60,000 raycasts allocated nothing; skipping car/HUD drawing or dropping the local controls
  stopped it. The one call both bypass: the recovery prompt asked `DrivingControls.BindingLabel("Reset")` every frame,
  and the Input System's `GetBindingDisplayString` keeps native memory on every call.
- **Fix:** labels are cached per action and recomputed only when its effective binding changes (a remap still updates
  the prompt; EditMode `ControlsTests.ThePromptLabelIsCached_AndStillFollowsARemap`). The spectator prompt used the same
  path. **After:** the uncapped player-only case that grew +~10 MB per ~23k frames holds at 222 → 223 → 223 MB over
  43–48k frames per race.
- **Final standard soak** (`Evidence/ui/soak/soak-final`, 10 races, the full V-054 soak: twelve cars, view/look-back/style/
  units cycling, a held reset every 12 s, alone on the machine): **PASS** — every accumulation check clear, 60/60 reset
  requests → one reset each, 9–10 gates per race; managed 7.1–7.8 MB; **Unity allocated 223 → 225 MB, flat from race 3**
  (was 228 → 340 MB over 12 races); system-used 542 → 577 MB, levelling (+3 MB over the last five races); working set
  552–577 MB (was 516 → 678 MB over 8); frame 1.59–2.21 ms mean, 2.73–4.11 ms p99; 88 non-empty static collections,
  none grew.

## V-060 — Spectate the Race through the real screens; NaN wheels after a disconnect (2026-09-27)
- Revision: working tree on `182ac6e` (committed in the next checkpoint). Two windowed development clients (development
  accounts 0 and 1 from the project's seed file), a dedicated game server process and the local control plane — all on
  this machine (loopback). `Tools/run/ui-tour-social.ps1 -Race` (`-nsUiTourSocialRace`). Automation, not a human session.
- A solo attempt first (`ui-tour-online.ps1`, one human) could not work and was removed: the server settles a race the
  moment no human can still finish (`RaceSimulation`, by design), so a lone driver who leaves has nothing to come back
  and watch. The UI also offers Sign Out only outside a convoy, so the tour simulates the crash itself (race and convoy
  connections closed, back at the title as a relaunch would be); every later step uses the real buttons.
- **Run** (`Evidence/ui/online/spectate`): host and guest form a convoy through Friends (invite from the friend list,
  join from the invitation), Campaign · Normal → both Mode Ready → Enter Mode → Propose Event (S02 Mizuhana Switchback,
  2 drivers + 2 AI) → both Event Ready → Start. 12 s into the race the guest's game "crashes"; the server records
  `DqDisconnected` and the race continues for the host. The guest signs in again, is offered "Rejoin Driver 1's convoy",
  rejoins (`spectator: true`, convoy phase `InMatch`, notice "rejoined as a spectator until the next event"), presses
  **Spectate the Race**, is admitted by the server as a spectator (1 watching), follows Driver 1 (the human, preferred),
  then the next target (an AI) — 2 target changes. Host: P1 of 4, Stage cleared, +10,248 credits. Guest: settled once as
  `DisqualifiedDisconnect`, stage not cleared, +0 — **PASS** on both clients.
- **Found and fixed on the way:**
  - *NaN wheel transforms* — after the race connection closes (left, lost, or the server finished and shut down) the
    client kept drawing for its short exit delay from Netcode's local clock, whose partial tick then reads NaN (logged:
    "local tick NaN, last predicted 1163"): 267 frames of NaN wheel positions/front-wheel rotations in the first run, and
    the same errors at the end of the rendered six-client runs of V-052/V-054 (626–803 lines per run). Now the client holds
    its last frame once the connection is gone; non-finite own-car values (prediction, contact prediction, server
    snapshot, render factor, correction blend) are logged by source and never drawn. Final run: **0** invalid-transform
    errors and **0** non-finite events on both clients.
  - *Spectator "CONNECTION 2092 MS"* — a spectator sends no inputs to time and the transport's own figure goes stale; the
    indicator is now hidden for spectators.
  - *"The race ended without results … nothing was settled"* after leaving — untrue (the server settles a departure as a
    disqualification); now "You left the race before the finish: the server counts a lost connection as a
    disqualification", and the receipt is fetched when the session is still signed in.

## V-061 — Correction blend A/B under impairment; NaN fix under six rendered clients (2026-09-27)
- Revision: working tree on `321e83e`. The V-054 configuration on this machine (loopback + application-level impairment):
  dedicated server, 6 rendered camera clients (30/60/120 fps, own views) + 6 AI on C01, 80 ± 20 ms each way, 3 % loss.
  Same build twice: the new correction blend (position **and** velocity kept continuous when a correction lands, the
  velocity difference clamped to 3 m/s) — `Evidence/net/run-20260927-162645-h6-C01-ai6-impair` — and the old one
  (`-nsCorrectionBlend position`) — `run-20260927-162933-h6-C01-ai6-impair`.
- Chase Far on-screen jitter, mean / p99 px (reconciliations): new — client 0 (60 fps) 5.31 / 79.2 (185), client 1
  (30 fps) 6.03 / 151.8 (160), client 5 (120 fps) 0.12 / 0.63 (28); old — 10.33 / 73.7 (568), 11.51 / 170.1 (343),
  0.16 / 1.62 (104). All finished; 0 occluded, 0 inside-collider frames.
- **Inconclusive:** contact load differed between the runs (reconciliations 2–4× higher in the old one), and the p99 tail
  is set by real contacts in both. The new blend is kept (continuous by construction, clamped, lower means, no sign of
  harm) but it is **not shown** to fix the heavy-contact hitch; that limitation stands.
- **NaN fix confirmed under load:** 0 invalid-transform errors and 0 non-finite events on all six clients in both runs
  (the V-052/V-054 six-client runs logged 626–803 such lines each).

## V-062 — A second human draws another human's livery in the same online race (2026-09-27)
- Revision: working tree on `7f7583d` (committed in the next checkpoint). Two windowed development clients, dedicated game
  server, local control plane — loopback. `Tools/run/ui-tour-social.ps1 -Race` (`Evidence/ui/online/livery-seen`).
- Before proposing the event, the host changes its livery through the real Garage → Appearance screen (front kit, paint
  colour, plate "NS H34" — a per-run tag) and applies it: the control plane validates and stores it (hash `d290c279…` →
  `5ea613e7…`). At the start of the race the guest looks up the other human in the roster the game server relayed,
  decodes that roster livery (wire form, 234 bytes) and compares it with the car it draws: plate "NS H34", front
  "track", rear aero "ducktail", 1 decal — all equal — and the grid screenshot shows the host's car with the plate
  readable. The rest of the tour (guest crash → rejoin → Spectate the Race; host P1, +10,248) passed as in V-060 — **PASS**
  on both clients. (An earlier attempt compared against the roster as canonical JSON and failed; rosters carry the wire
  form — the check was wrong, not the game.)
- Seen on the way (not fixed here): a decal scaled up in an earlier tour renders as a flat quad that leaves the body and
  crosses the windscreen/pillar, in the Appearance preview and on the race car.

## V-063 — Migration: pre-Addendum-03 Local saves and record versions (I02/I03 report) (2026-09-27)
- Revision: working tree on `3dbcf05` (committed in the next checkpoint). EditMode **241/241**.
- **What Addendum 03 changed in persisted data:** Local profile schema unchanged (`local-profile@1`, schema version 2 —
  no profile-code change since the addendum arrived); driving presentation and control bindings live in a separate
  `driving.json` (defaults when absent or corrupt — `InstrumentTests`, `ControlsTests`); records are keyed by
  `ScoringVersion` (`classify-1` → `classify-2`), physics version and route revision, and route revisions were bumped
  for every course whose geometry changed (tunnels, bridges, relief pass — V-049/V-051/V-053/V-055). Control plane: no
  code or schema change since the addendum arrived (`git log b42abc9..HEAD -- Services` is empty), so online profiles,
  receipts and settlement are untouched by construction.
- **I02 — real old saves:** `ProfileMigrationTests` loads two saves written by the built game's isolated automation
  tours before the first Addendum 03 commit (Test Yard 06:59, Appearance 08:11; committed as fixtures, marked binary so
  their SHA-256 survives checkout): header, checksum and schema accepted, `Loaded` (not recovered, nothing to migrate),
  `Validate()` clean, starter car owned; saved again by this build and reloaded, and wallet + history, starter, cars
  (frozen builds, applied livery), parts, courses, campaign, challenges, cosmetics, music, records and tutorial are
  identical data — **pass**. Records from before the addendum: `RecordVersionTests` (V-054) — a `classify-1` best
  survives a round trip, is shown as legacy for the same event under `classify-2`, never compared, never erased.
- **I03:** geometry/progress-rule changes bump route revisions and the scoring version, which are part of the record key,
  so an older result is kept under its own key and never recomputed with the new mesh or penalty policy (V-054 test);
  receipts are server records and unchanged.
- Limits: the old saves carry no records or campaign clears (the automation profiles that existed before the addendum
  had none); the record part rests on the synthetic `RecordVersionTests` fixture.

## V-064 — F08 starter campaign runs: stalled at the lieutenants (benchmarks not yet certified) (2026-09-27)
- Revision: working tree on `6a76c5a`. PlayMode `StarterCampaignRunTests` in the editor (one Unity writer): for each
  starter a fresh Local profile plays the whole Normal campaign through the game's Core calls — event plan (roster, live
  featured rival, benchmark, cap), a real headless race on the stage's course at 30× simulation speed driven by the
  validator autopilot (legal inputs; conservative, not a human), Core progression settling it, and the Garage buying the
  starter's intended upgrade path (build-recipes.json) with the credits earned. Nothing is written as a clear.
  Evidence: `Evidence/progression/campaign/V0x-normal.json`.
- **Run 1** (intended schedule only): V01 13/30 and V02 13/30 — stopped at **S14** (lieutenant R16, C08); V03 20/30 —
  stopped at **S21** (lieutenant R24, C12). Every retry was identical (deterministic races) with 156k–162k credits unspent.
- **Run 2** (the run now also buys the next recipe step ahead of schedule after a lost attempt, as a player with savings
  would): V01 bought V01-E early (PI 314 → 351): 176.8 → 174.7 s vs R16 160.2 s; V02 bought V02-E (PI 351 → 396):
  173.8 → 172.2 s vs 160.3 s; later steps "not in the shop yet"; V03 at S21 could not afford V03-G (95,000 vs ~71,000)
  and stayed 12.1 s behind R24. **F08 not passed.**
- **Cause:** lieutenants race their identity cars within the stage cap — R16 a V08 (base PI 530) under cap 699 at S14,
  R24 a V12 (650) under 849 — with stage-escalated profiles close to the validator's, while the starters' intended paths
  are at PI 314–454 there; upgrades are worth ~2 s against a 12–17 s gap. Stage targets are still the provisional
  catalogue values (S14 300 s vs ~160–177 s actual) and featured-rival pace is not calibrated to them: this is the
  pending **benchmark certification** (spec: reference run P in a freely available class-legal car, Normal targets
  ~1.18×P → 1.05×P, lieutenants interpolated, featured cars on authored legal reference tunes). Addendum 02 F09 rules
  out a compulsory model change, so the fix belongs in certification, not in the test.

## V-065 — Normal benchmarks certified; S29 Four Signals judged; F08 with certified targets (2026-09-27)
- Revision: `72d1369` plus the S29 recertification and autopilot apex blending on its working tree (committed in the next
  checkpoint). PlayMode in the editor (one Unity writer), automation with legal inputs — not human runs.
- **Certification** (`BenchmarkCertificationTests.CertifyNormal`, 29.6 min; `Evidence/progression/benchmarks/`): per Normal
  stage, P = the slowest of the three starters' intended builds solo (so every starter path stays viable, Addendum 02
  F09); target = P × factor, factor 1.18 (S01) → 1.05 (S30), lieutenants on the same line; the featured rival's pace
  (a new `PaceScale` on its whole speed plan, never above its stage profile) bisected so it finishes 0–1 % above the
  target, measured solo with every car ghosted (`CalibrationGhosts`, certification only). Examples: S01 P 86.5 s → target
  102.1 s (provisional was 180 s), R01 pace 0.727; S14 P 174.3 s (V01-D) → 195.5 s, R16 pace 0.719 → 195.9 s; S30 P
  297.8 s → 312.7 s, R40 pace 0.703. Rival paces land at 0.59–0.80: the reference is the conservative validator, so the
  calibrated rivals run well below their stage profiles — a human-feel review of that pace is still to be done.
- **Content:** `authored/stage-benchmarks.json` (content hash; the control plane loads it — Normal proposals and
  settlement now say certified, Hard stays provisional and labelled); `StageBenchmarks.For` shared by Local, the control
  plane and settlement; campaign screen drops "(provisional)" where certified.
- **S29 "Four Signals"** (spec contract; nothing judged it before — `ContractsPassed` was always 0, so S29 could never be
  cleared online or offline): `ContractJudge` on C24's authored sectors, from server-observed facts, offline and on the
  dedicated server — Entry: sector time and the car's body across both marked apex gates; Arc: raw drift banked across
  the three marked corners; Descent: no wall impact/reset, a braking phase at the zone, brake off by the published release
  point, speed in the published window at the zone's end; Horizon: published exit speeds and the full-route limit. The
  verdict says what failed (with the numbers). S29 references drive the contracts (drifting the Arc) and publish them:
  Entry 77.6 s, Arc 1,271 raw (0.6 × the weakest solo, room for the live field), brake window 60.5–83.7 km/h with the
  release point at 4,837.8 m (the references feather the brake down the Descent and release ~10 m past the authored zone:
  the spec takes the release point from calibration), Horizon 160/176/177 km/h. Hard borrows these as provisional.
  EditMode `FourSignalsTests` (each contract passes and fails on its own condition), `CertifiedBenchmarkTests`;
  **245/245**; services **335/335**.
- **F08 with certified targets** (`Evidence/progression/campaign/`; earlier runs kept in `certified-before-contracts/` and
  `certified-with-contracts/`): **V03 30/30 Normal stages** — every stage on its first attempt, all eight recipe steps
  bought on schedule (592,000 spent, 127,555 left), S29 4/4 contracts, the S30 finale won as a duel. **V01 and V02 28/30**:
  lieutenants S07/S14/S21 cleared first time (V01 needed step H early for S28), both stop at **S29** with 3/4 contracts —
  Entry's apex gates missed in the pack right after the grid start (V01 1/2, V02 0/2: crossing at +0.7…+0.8 m against a
  marked +3.0 m). Solo, every starter touches both gates; the autopilot now blends toward marked apexes and does not start
  a pass near one, but its racecraft cannot hold the line in a four-car pack (aiming straight at the gate made it worse
  and was reverted). **F08 not passed for V01/V02** — an automation limit on one contract, recorded as such; a human run
  of S29 is still needed.
- **Online with certified targets** (rebuilt player + dedicated server + control plane restarted on content
  `2ac19629…`, loopback; `ui-tour-online.ps1`, `Evidence/ui/online/certified`): the convoy proposal for S10 shows
  "target 02:47.988" with no provisional label (the certified 167,988 ms); the server-authoritative race (1 human + 3 AI,
  C09) finished in 2:46.660 — qualified, first clear, +17,996 credits, receipt settled — **PASS**.

## V-066 — Stage conditions in races; Hard certified; Normal recertified under authored conditions (2026-09-27)
- Revision: `58080e8` working tree (committed in the next checkpoint). PlayMode in the editor, automation (validator
  autopilot, legal inputs) — not human runs; EditMode **246/246**; services **335/335** (at `58080e8`).
- **Conditions were never applied:** `story/conditions.json` (each stage side's time of day, surface, weather, derived
  from the catalogue with documented rules) was loaded by nothing — every campaign race, Local and online, used the
  course's default surface, so no Hard side was ever damp or wet (12 damp + 6 wet Hard sides; 6 damp Normal sides, C08's
  S09/S14 raced "wet" from the route instead of the authored "damp"). Now `authored/stage-conditions.json` is in the
  content hash; `RaceConditions` gives a campaign side's surface and time of day (else the course's) to the Local race,
  the dedicated server (which also sends the time of day to clients for lighting and headlights) and the certification.
  EditMode `StageConditionsTests`.
- **Certification of both modes** (`CertifyHard` + `CertifyNormal`, 56 min, `Evidence/progression/benchmarks/N-*`,
  `H-*`): each side under its own conditions; Hard factor 1.04 (S01) → 1.00 (S30) with the Hard lead rivals and the
  recipes' Hard steps (e.g. H S01 damp: P 88.7 s → 92.3 s, R05 pace 0.969; H S14 wet: P 168.6 s → 172.3 s; H S30: P 284.7
  s → 284.7 s, R48 at full pace 291.7 s); Hard rivals run at 0.72–1.0 of their profiles (four cannot reach the target even
  at full pace). Hard S29 has its own, tighter contracts (Entry 64.5 s vs Normal 77.6 s). Normal S09/S14 now certified on
  damp (targets 191.5 s / 187.8 s). The whole file is authored content (hash `2661eaf8…`); Hard no longer provisional.
- Hard campaign run (`StarterCampaignRunTests.HardCampaign_AfterNormal`: Normal first, then Hard with the Hard recipe
  steps) is in place and not yet run.
