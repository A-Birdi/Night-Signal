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


## V-067 — Addendum 04: loopback-first network testing, measured sockets (2026-09-27)
- Revision: working tree on `2f77016` (committed in the next checkpoint). All processes on this PC; control plane on
  `127.0.0.1:5080` (TCP listener verified loopback). No firewall rule was read, created, changed or removed.
- **Code:** `NetConfig.BindHost` (`-nsBindHost`, default `127.0.0.1`) separate from `PublicHost` (`-nsPublicHost`, default
  `127.0.0.1`); `BindProblem()` refuses an empty/non-numeric bind or a loopback bind advertising another address before
  any socket; `RaceServer` binds `BindHost` (was a hard-coded `0.0.0.0`); the host logs and the server evidence carry
  executable, role, bind, advertised host, port, class (local/lan/wildcard/wan), PID and LAN opt-in; a client connecting
  to a loopback server asks for a loopback local endpoint. `Tools/run/NetGuard.psm1` (resolve + fail closed, free-port
  check that names the owner and kills nothing, read-only socket capture, port-release wait) used by `net-race.ps1`,
  `ui-tour-online.ps1`, `ui-tour-social.ps1` (`-BindHost`, `-PublicHost`, `-AllowLan`).
- **A04-01** EditMode `NetConfigTests` 9/9 (defaults loopback; bind and advertise parsed independently; `""`, blanks,
  `localhost`, a host name and `999.1.1.1` refused; loopback-bind/LAN-advertise refused; class of `::1`, private, wildcard,
  public addresses).
- **Measured on a development build first** (`run-20260927-203743-h2`, 2 clients, PASS): the server's game listener was
  `127.0.0.1:7777`, but every development player (server and both clients) also listened on **TCP `0.0.0.0:55000–55002`**
  and held a UDP `0.0.0.0` socket — Unity's player connection for the editor/profiler, present in any development build
  (it is why the GameSoak and SetupSmoke copies raised firewall prompts although SetupSmoke has no gameplay networking).
  **Fix:** `BuildGame()` and `BuildSetupSmoke()` are now non-development builds; a development build is for an attended
  profiling session only.
- **A04-02** (non-development `Builds/Game/NightSignal.exe`, `run-20260927-204526-h2`): server PID owned exactly one
  socket, **UDP `127.0.0.1:7777`**, no TCP listener, nothing on `0.0.0.0:7777` or a LAN address; both built clients
  finished the authoritative race (RTT 20 ms), receipts settled; UDP 7777 released after the run — **PASS**.
- **A04-03** (`run-20260927-204832-h6-C01-ai6`): 6 humans + 6 AI = 12 vehicles, all 12 finished, six settled receipts, RTT
  17–24 ms; server socket only `127.0.0.1:7777`; port released — **PASS**.
- **A04-04:** `net-race.ps1 -BindHost 0.0.0.0` and `-BindHost <a LAN address>` without `-AllowLan`, and `-BindHost localhost`,
  refused before launching (0 processes, no run folder) — **PASS**.
- **A04-05:** with `-AllowLan` the guard produces a distinct bind/advertise pair (`lan` or `wildcard` class) and passes
  `-nsAllowLan`; a loopback bind advertising a LAN address is refused; an occupied port is refused naming its owner;
  no cross-device LAN test was run (not authorized) — **PASS** (configuration only).
- **A04-06:** non-development `NightSignalSmoke.exe -nsSmokeTest` rendered, wrote its report and exited 0 owning no UDP
  socket and no TCP listener; it never registers as a game server (no server role) — **PASS**. (It made outgoing TCP
  connections from the LAN interface — Unity's own services — outbound only.)
- **A04-07:** 3-race soak on the canonical non-development build (no GameSoak copy): no UDP socket, no TCP listener at
  all; accumulation checks and the static census clear — **PASS**. Unity's per-category memory counters report −1/0 in a
  non-development player (Unity allocated 433 → 439 MB there, not comparable with the development numbers of V-059);
  the native-memory bisect switches need an attended development build.
- **A04-08:** successful runs release UDP 7777; a forced timeout (`-TimeoutSeconds 25`, `run-20260927-205110-h2`) stopped
  exactly the three processes the harness launched, left no Night Signal process and no UDP 7777 holder, and the next
  preflight could reuse the port — **PASS**.
- **A04-09:** no firewall-mutating command anywhere in project scripts/tooling (search for `New/Set/Remove-NetFirewallRule`,
  `netsh advfirewall/firewall`, `Set-NetFirewallProfile`, edge traversal, elevation — none); CLAUDE.md and the effective
  rules state firewall changes are the owner's — **PASS**.
- **Limits:** Netcode's `UnityTransport` binds a client socket to `0.0.0.0` on an ephemeral port whatever listen address
  is passed (the listen address only applies to servers); a loopback-bound client needs a transport change, not made (no
  package modification). Clients open no listener. Nothing here proves LAN or WAN play.

## V-068 — F08 Hard gates: V03 through Normal and 28/30 Hard on certified targets (2026-09-27)
- Revision: `e6ecc3f`. PlayMode `StarterCampaignRunTests.HardCampaign_AfterNormal("V03")` in the editor: one fresh Local
  profile plays the whole Normal campaign (Hard opens only after a real Normal S30 clear), then the Hard campaign under
  each Hard side's own conditions (damp/wet/dry) and certified targets, buying the recipe's Hard steps; validator
  autopilot (automation, legal inputs, not a human). Evidence `Evidence/progression/campaign/V03-hard.json`.
- Normal: 30/30 again. **Hard: 28/30** — S01–S21 and S23–S28 on the first attempt (lieutenants S07 damp, S14 wet, S21
  damp, S28 wet included), Hard steps H1a/H1b applied, H2 (268,000) and H3 (190,000) bought on schedule; **S22** (factor
  1.011) missed by 1.4 s three times until the wallet reached H4 (350,000), bought and cleared on the fourth attempt;
  **S29 Hard** stops with 3/4 contracts — a wall impact in the Descent in traffic (Entry, Arc, Horizon passed).
- F08's Hard gate is therefore reached and mostly cleared by one starter; V01/V02 cannot reach Hard until their Normal
  S29 Entry apex issue (V-065) is resolved; both S29 failures are the automation's racecraft in a pack, not a missing
  rule — a human S29 run is the outstanding check.

## V-069 — F09 driving: all 18 models on their favourite-car paths meet the certified targets (2026-09-27)
- Revision: working tree on `42bafbf` (committed in the next checkpoint). PlayMode
  `FavouriteCarDrivingTests.EveryModel_OnItsPath_MeetsTheCertifiedTargets` in the editor: each of the 18 models — every
  handling family (FR: V01/V04/V05/V09/V10/V13/V15, FF: V02/V06/V14, AWD: V03/V07/V08/V12/V17, MR: V11/V16, front-mid:
  V18) — driven solo by the validator autopilot (automation, legal inputs, not a human) with the build its recipe
  intends at four sides: the stage it is bought for, a mid-campaign stage, the N:S28 lieutenant and H:S15, each under the
  side's authored surface. Each run must finish, be cap-legal and beat the side's certified target (Normal 1.05×P, Hard
  1.00×P). Evidence `Evidence/progression/favourite-cars.json`. The data half (every path resolvable, shop/cap legal,
  band demands on the PI estimate, affordable on the campaign income) is the .NET `UpgradePathTests` (232/232 pass).
- **First run: 71/72.** V06 (FWD) at H:S15 on `V06-H3` took 146.8 s against 137.9 s, with a wall hit — deterministic
  (the same 146.8 s in three runs), and slower than its own weaker H2 build (132.4 s). Variants on all seven Hard Act III
  sides (`Evidence/progression/v06-hard-act3-variants.txt`): the recipe's rain-sport tyres plus the Act III supercharger
  overpower the FWD front end on C13's dry blue-hour side; with semi-slicks (what the other FWD, V02, runs at H3) V06 is
  the fastest variant on every side, wet S17 included (146.5 s against 162.5 s). The authored H3 step now uses semi-slicks
  (PI 663/849; one extra 95,000 tyre purchase, still affordable per `UpgradePathTests`).
- **After the fix: 72/72** finished, cap-legal and under target. Time/target ratio 0.770–0.987; closest: V07-H3 0.987,
  V01-H3 0.980, V02-H3 0.972 at H:S15 (the Hard Act III entry, where the Hard factor 1.00 leaves the least margin); 8 of
  72 runs had a single wall contact and still beat the target.
- F10 (unchanged starter against its developed build under the same inputs and conditions) was measured for the three
  starters in V-048. F09/F10 are complete **for automation**; the human favourite-car runs remain pending.

## V-070 — Car art pass: detailed bodies for all 18 cars, decals clipped to the body (2026-09-28)
- Revisions: `0fcabb1` (bodies), `afc7eb6` (decals + tests), `7a67d97` (NaN liners, cockpit prewarm, speed),
  `12b489f` (tour blown-frame check); built-player tours on `12b489f` (non-development `Builds/Game/NightSignal.exe`, clean tree).
- **What changed (the spec's car list, master "Art target"):** the procedural generator keeps its loft, per-car cabin
  anchors, decal frames and wheel positions (the handling parameters), and adds: a greenhouse with A-pillars, drip rails,
  belt mouldings (chrome on the GT/sedan cars), blacked-out B/C pillars (the wagon's six-window side), side glass,
  windscreen frit and wipers; lamp clusters, grilles and bumper intakes drawn in elevation and projected onto the rounded
  nose and tail (bezel, lens, internals per authored style: sealed-beam ribs, projectors, triangular internals, recessed
  ovals, split bar), bumper split lines, rear valance, plate recess and reversing lamps; wheel arches cut to the authored
  shape (round / squared / flared) with lips and matte wheel-well liners; shoulder and sill creases; rounded ends;
  mirrors on stalks with glass; door, hood and boot shut lines, handles, the V18 door scallop, V11 buttresses, side
  intakes/cooling channels, hollow exhaust tips; an interior silhouette behind the glass (seat backs, headrests, rear
  headrests where the roof continues, dash, steering wheel where the fitted cockpit puts it) on a dark cabin floor with a
  matte seat-cloth material (new `CarCloth`); tyres with bulged sidewalls, shoulders and tread grooves; rim lip, brake
  disc, hub face, lug nuts and centre cap. Authored per car from Appendix C: grille style and arch shape plus shape
  features (`authored/cars.body.json`; not part of the race ContentHash). Materials now separate rubber, clear-coat
  paint, metal (chrome/rim), seat cloth and glass.
- **Distinctness sheets** (editor `CarSheet`, all 18 in one neutral paint, row-major V01…V18): front ¾, rear ¾, side,
  front and rear elevations and a wheel-arch close-up, before and after, in `Evidence/art/cars/`. Every front face and
  tail is individually recognisable in the elevation sheets (lamp style + grille/intake layout; tail lamp family).
- **Found and fixed on the way:** (1) editor sheets: arch lips first followed the circle below the axle where the loft
  cuts the opening vertically (flaps hanging below the sill) — lip and liner now follow the opening as cut, with loft
  stations either side of the cut; liners used the glossy trim and mirrored the sky (now matte). (2) **Built-player camera
  tour run 1 reported PASS (90/90) while 8 of the 18 cockpit frames (day and night; also some chase/hood frames)
  carried a white disc over the instruments** — the wheel-well liners were wound both ways on shared vertices, `RecalculateNormals` summed them to zero,
  the lit shader produced NaN and bloom spread it (the editor sheets have no bloom). Liners are now one-sided;
  `CarBodyArtTests` asserts a real, finite normal on every drawn triangle; the camera tour now fails any frame with more
  than 6 % at full white (run-1 defect frames measured 11–33 %, good frames 0–2.5 %).
- **Decals (the open "scaled decals leave the body" item):** a max-scale decal used to pile up at the ends or float off
  the body (EditMode against the previous builder: text 3.7 m off a hood, circles above the roof — every zone failed).
  `BodySurface.OnZone` defines each zone's paintable panel (ends, arches and glass excluded); shapes are subdivided
  (10 cm) and clipped along the panel edge (2 cm); glyphs leaving the panel are dropped.
- **Tests:** EditMode `CarBodyArtTests` 36/36 (18 × budget/submeshes/normals/ground/bumper-camera clearance/open-cabin
  seat removal, 18 × decals at scale 1.6 at the corners of all six zones stay on the panel); full EditMode 291/291 (cabin
  anchor test unchanged); .NET `CoreTests` 123/123 (reads `cars.body.json`), `BuildsTests` 232/232.
- **Cost:** ≤ 6.5 k vertices per body; ~21 ms to generate one in the editor (fascia projection with cached end sections).
  The open-cabin body and fitted cockpit are now prepared when a camera takes the car (a cut), not on the first press
  of the view key mid-race.
- **Driving views:** editor cockpit/hood/bumper sheets re-rendered — mirrors visible outside the glass, wipers at the
  screen base, nothing new in the bumper or hood view.
- **Built-player tours** (automation, not a human judgement; `Evidence/art/cars/ingame/`): run 1 (`0fcabb1`+`afc7eb6`
  build) — camera tour PASS 90/90 but visibly wrong (see above; `run1-*-NaN-defect.jpg`); run 2 (`7a67d97` build) — PASS
  90/90, every frame and tile measured ≤ 0.9 % blown out; **run 3 (`12b489f` build, the tour's own blown-frame check
  active) — camera tour PASS 90/90 (18 cars × 5 views driven on C01/C05/C08/C12/C03/C14 plus the C08 tunnel; ledger
  `blown_fraction` max 1 %), `-nsAppearanceTour` PASS (every family on the new body: aero front, diffuser, skirts, wing,
  dual exhaust, paint, lamps and plate, decals, presets, S01 started with the livery), `-nsInstrumentTour` PASS.**
- **Limits:** automation and editor renders only — a human visual review of the 18 cars (and the paint response in the
  courses' own lighting) is outstanding; no LOD levels for the cars yet (≤ 6.5 k vertices, 9 body + 8 wheel draws per car).

## V-071 — Course scenery: every route landmark built, regional kits per biome (2026-09-28)
- Revisions: `7e33d06` (landmark kits), the following commit (regional kits, tests, docs).
- **Before:** the route documents name 111 landmarks; only the three bespoke C01 kits existed — the other 108 (structure
  27, wall 17, tower 16, crossing 13, water 12, sign 10, rail 6, field 5, gate 2) generated nothing and logged "No kit"
  at every course load. The six regions differed only in hill parameters.
- **Landmark kits** (`LandmarkKits.*.cs`, rules in `docs/COURSES.md`): each kit builds from the authored parameters —
  buildings with plinths on slopes, the authored walls and roof (gable/hip/flat/sawtooth/curved) and per-type identity
  (station platform and canopy, roller-door sheds, a lit terminal front, pump houses and pipes, a two-tier shrine roof, the
  workshop's casting wheel, a switchyard's gantries and transformers, open shelters, pavilions and viewing galleries);
  towers (water tank, mast, antenna field with guys, lighthouse, radio dish, chimneys, cooling towers, beacon, pylon line
  with cables, tower cranes, spillway intake towers, observatory); crossings over the road (overpass, a stone aqueduct,
  covered footbridge, gantry, pipeline arch, rail trestle, relay arch, through truss); walls (retaining in the authored
  material, split tiers, stone stair with lanterns, quarry benches, snow fence, sea wall, breakwater with lamps and
  armour units, flood marks and gauge); water (sea and reservoir planes, carved canal with sluice gate, stone creek,
  stepped fishway, waterfall and pool); fields (tea hedges on contours, greenhouse tunnels, orchard, cedar and pine
  groves); rail (coast railway with catenary, funicular, conveyor gallery, cable-car station); signs (marshal posts,
  memorial, enamel mural, shuttered stall row, tunnel-marker gantry); gates (roofed cedar gateway — original form — and a
  flood gate). 101 are built by kits; 7 (3 crossings, 1 two-level bridge, 3 avalanche galleries) are the road's own
  bridge/gallery sections, already drawn by the bridge and tunnel builders from the section style.
- **Regional kits** (`LandmarkKits.Regional.cs`): the biome's scatter along the whole course — cedar forest (Kasumi),
  broadleaf/bamboo/hedges (Mizuhana), conifers and rock (Kurogawa), wind-bent pines and shore rock (Akebono), outcrops and
  a pole line (Hoshimi), firs, snow patches and snow poles (Tsukishiro/Amanagi), lamps and hedges (Hinode campus); 250 m
  chunks for culling; visual only.
- **Driving is unchanged by construction:** landmark colliders only on the Scenery layer (cars collide with Drivable and
  Barrier only; the cameras see Scenery), none on scatter; the server's collision-only build skips water, fields and all
  scatter; terrain carves act only outside the road corridor. No route geometry, gate or benchmark changed.
- **Found and fixed on the way (by the new test and the sheets):** a canal and walls following the offset line crossed
  another leg of the road on hairpins; a funicular ran across a switchback at ground level (now bridged at the
  clearance); a sea plane drowned a coast railway (water now starts at the authored offset); spillway-tower footbridges
  hung in mid-air, then reached the road; the dusk sea and lakes were mirror sheets (smoothness 0.94/0.93 → 0.78/0.80);
  tree crowns, pole-line wires and campus lamp heads overhung the road — each prop now keeps its own reach clear and wires
  never span a road. The terrain carve loop gained a bounding-box reject (C08 generation with its creek 5.1 s → 2.5 s).
- **Tests:** EditMode `LandmarkKitTests` 29/29 — per course: every landmark built or drawn by its section; no vertex of
  any landmark or scatter chunk over the paved road/shoulder (that side's) below 6.3 m above it (water below −2 m allowed);
  no landmark collider on a driving layer (the C01 stone bridge's parapets are its barrier by design); scatter has no
  collider. Full EditMode 320/320.
- **Evidence:** `Evidence/courses/landmarks/landmarks-<course>.jpg` (every landmark photographed from its road in the
  course's lighting, index with vertices/colliders) and `region-<biome>.jpg` (three road views per course, per region).
- **Built player** (`7e33d06`, landmarks without the regional kits): camera tour run 4 PASS 90/90, max 1 % of a frame
  blown out; mean frame rate over the six tour courses ~420–450 fps against ~450–520 before the scenery (C05 with the
  cedar grove 486 → 382). Run 5 on `7d9119d` (landmarks + regional kits, non-development build, 1920×1080 windowed):
  PASS 90/90, max 1 % blown; per-course mean 446–495 fps (C01 479, C03 491, C05 457, C08 473, C12 446, C14 495). That is
  above run 4 on fewer objects, so run-to-run variation on this machine (other load, clocks) is larger than the scatter's
  cost: these tours bound the frame rate (hundreds of fps at 1080p), they are not a benchmark.
- **Limits:** automation and editor renders only; a human look at each region is outstanding; the tall dark terrain
  "cliffs" of the gorge/highland styles (steep terrain at the heightfield resolution) are pre-existing and untouched.

## V-072 — Characters: the 48 rivals modelled, skinned and animated; distinctness measured (2026-09-28)
- Revisions: `fd9cab3` (builder, rig, motion, looks, sheets, tests); the host's look and vertex compaction in `1b34516`.
- **Before:** the 48 rival sheets existed only as text (appearance strings, posture, signature accessory, livery); no
  character geometry, rig, animation or emote existed anywhere in the game.
- **Builder** (`Assets/Game/Runtime/Characters/CharacterBuilder.cs`): an original stylized person from a `CharacterLook`
  — skeleton sized by height, build (7 builds) and limb length; a lofted torso and limbs dressed in an outer garment over
  an under layer, cut to the authored hem (cropped/hip/thigh/knee, skirts split per leg) and sleeves
  (long/rolled/short/none); 21 outfits (jackets, coats, capes, ponchos, aprons, overalls, a jumpsuit tied at the waist…);
  head with eyes (whites, irises, highlights), brows, nose, mouth by expression, ears; facial hair; 22 hair styles;
  ~35 accessories (glasses, caps, scarves, headphones, key reel, goggles at the collar, medallion, sash, thermos…).
  One skinned mesh (18 bones, rigid weights), a submesh per colour slot, 2.2k–3.2k vertices (mean 2.6k), ~3 ms to build.
- **Looks** (`authored/story/rivals.look.json`): all 48 authored from each sheet's appearance, posture, signature
  accessory and livery colours; `CharacterVocabulary.Check` refuses unknown words so no look silently falls back.
- **Motion** (`CharacterMotion.cs`): posture (8 kinds), idle breathing and glances, a walk/jog cycle whose hips height
  comes from leg kinematics (feet stay on the ground), and the twelve meet emotes (`Core/Meet/Emotes.cs`: IDs and bounded
  durations for replication) blended in and out.
- **Tests:** EditMode `CharacterTests` 99/99 — every rival has a valid look; each body is skinned, within budget, finite
  unit normals everywhere (the car pass's NaN lesson: unreferenced vertices and degenerate pole triangles were found and
  removed), soles on the ground, height within −3/+8 %; walking, jogging and every emote keep a foot grounded (except the
  cheer's hop) with finite skinning, the admiration crouch goes down > 0.3 m, stretch/cheer/wave raise the hands above the
  head, each emote ends after its duration; no two rivals confusable.
- **Distinctness** (`CharacterSheet.Measure`): pairwise front+side silhouette IoU at a fixed 1.1 cm/px scale and a
  colour-layout difference over a 6×12 grid of the lit front view. Max IoU 0.930 (R19–R47, colour difference 0.131);
  confusable pairs (IoU > 0.90 and colour < 0.12): 0. Thresholds are this project's, chosen before looking at pairs;
  heights span 1.50–1.92 m.
- **Evidence:** `Evidence/characters/` — front/side/back (common scale), three-quarter, portrait and silhouette sheets
  (row-major R01…R48), the motion strip (idle, walk, jog, 12 emotes) and `distinctness.txt`.
- **Limits:** editor renders and automation; a human look at the 48 is outstanding. Stylized, not high-fidelity: rigid
  skinning (joints covered by spheres), mitt hands (thumbs-up and point read by arm pose), no cloth simulation. Players
  have a default look varied by name; an appearance picker on the Player Card is a follow-up.

## V-073 — The meet: Cedar Lantern Terrace, walking, emotes, boombox, offline visit (2026-09-28)
- Revisions: `735e4a0` (layout, boombox rules), `13d8a29` (scene), `1b34516` (session, controls, HUD, tour), `7b4d1d9`
  (music bootstrap, ribbon overflow fix, panel layout, noren).
- **Before:** R12.1/R12.2/C.6 not started; the meet existed only as authored text (host, placards, quick chat).
- **Layout** (`Core/Meet/MeetLayout.cs`, engine-free, shared by scene and room server): 150 × 110 m plateau; twelve bays
  (six west under cherry trees in planters, six east along the fence), angled 22° and nose to the plaza; service lanes
  behind the bays and the south entry lane carry the arrival spline, separate from the pedestrian centre; tea kiosk,
  awning, timing board, radio bench, boombox and host spot north; dry garden island in the middle; photo marker framing
  the east bays against the ridges; four viewpoint placards; the enclosure. Queries: walkability (fixtures and parked
  cars), driver's-door and rescue spots validated free of fixtures, cars and other avatars, the arrival path and its
  34 m presented stretch (3.5 s, ~35 km/h), bay allocation keeping a convoy together.
- **Scene** (`Facilities/Meet/Meet.unity`, `LandmarkKits.Meet.cs`): generated from the layout on load — paved apron,
  concrete bay rows with markings and wheel stops, asphalt lanes, flagstone forecourt, the garden (edging, raked
  ripples, rocks, shrubs, maple), cherry trees and petals, lamps, stone lanterns, the kiosk (plaster and timber, shutter
  counter, lit interior, tiled gable roof, awning, lantern strings, noren reading 茶), timing board, radio bench and
  boombox, hedges, the timber fence on the repaired retaining wall (paler rebuilt section, as the placard says), the
  berm and maintenance gate, a continuous collision ring and fixture colliders from the same layout; outside, the wooded
  slopes, the overlook falling to a valley and distant ridges, so the horizon is land. Fixed sunset lighting with light
  haze; practical lights at the awning, lamps and lanterns. Generation 0.15 s (collision-only for a server).
- **Session** (`Runtime/Meet/`): `WalkingControls` (keyboard/mouse and controller, remappable and stored with the
  preferences, released while typing or unfocused; direct emote keys 1–0, -, =), `AvatarWalker` (CharacterController:
  scenery, parked cars and the perimeter stop it; no jump; avatars never collide with each other), `MeetCamera` (orbit,
  zoom, recenter, drifts back behind while walking, pulls in instead of clipping, photo mode, arrival presentation),
  `MeetHud` (prompt, hints, SIGNAL ribbon, emote wheel, one modal panel, nameplates, quick-chat bubble), `MeetSession`:
  the player's car (applied livery) drives the presented arrival (skippable), the avatar gets out at the driver's door
  (validated free point); the host Genzo Karasawa greets by name, explains greetings and returns a wave and a bow; five
  rivals stand by their own cars as labelled story characters (idle emotes, a line when spoken to); inspection of their
  cars (PI and class, stock tune, paint, a cosmetic-only like) and of your own (sit in: fitted cockpit, headlight preset,
  wheel turn, a rate-limited rev through the engine synth); viewpoint and photo-point placards; the timing board shows
  "no convoy proposal (offline)" and only the player's own recent slips; the boombox under the Core rules (owned cues
  queue; locked ones show an unlock hint, encounter themes without names) with the music crossfading from the meet bed
  near it; quick chat (authored phrases, no typing); photo mode (HUD and nameplates hidden, save a PNG, never pauses
  anything); rescue-to-car (hold); leave. **Music:** the game never created its `MusicPlayer` — now `MusicPlayer.Ensure()`
  builds it from `Resources/MusicLibrary` and each menu screen plays its cue (races still play none).
- **Built player** (`7b4d1d9`, non-development, 1920×1080 windowed, `-nsMeetTour`, `Evidence/meet/tour-*`): PASS —
  arrival 3.74 s; walked 4.3 m in 3 s at 1.50 m/s, jog 3.80 m/s; the west hedge line held (x −68.15 against a −68.5
  boundary); the garden edging held; the host addressed "Meet Walker", showed the emote help and returned both the wave
  and the bow; all twelve emotes played; placard and timing board shown; a queued owned cue (MUS_TITLE) started and was
  the music heard near the boombox; closing released the lease; sit in, rev once then refused within the 3 s limit,
  get out at a walkable point; photo mode at the marker; rescue back beside the car; leave returned to the Offline
  hub. ~280–560 fps in the tour frames at 1080p.
- **Tests:** EditMode `MeetRulesTests` 10/10 — bays inside the enclosure, clear of fixtures and of each other, noses to
  the plaza; the 6 m central aisle, the 6 m aisles either side of the garden and the cross aisle clear with every bay
  occupied; 3 m clear behind every parked car; door/rescue spots free (and not on top of a standing avatar); arrival
  paths never cross the plaza and the presented stretch starts in a lane; bay allocation; boombox lease, queue,
  interval, ownership, leavers and spoiler protection; SIGNAL ribbon (one current + three queued, bursts coalesce,
  keyed notices never repeat, "disconnected" wording, overflow reaches the event list).
- **Evidence:** `Evidence/meet/` — editor viewpoints with display cars and rivals (overview, photo marker, kiosk, both
  bay rows, overlook, entry lane, garden, boombox) and the built-player tour frames.
- **Limits:** offline only — the networked room (Phase C: server-owned membership, bays, arrivals/departures, avatar
  snapshots, emote replication, shared boombox, ribbon events) is next. Walking bindings are remappable in the data but
  not yet on the Controls screen; players have a default look (no appearance picker yet); the host's lesson is tracked
  in the session but not yet wired to challenge CH63; a human play-through is outstanding.

## V-074 — The meet online: rooms on the control plane, three real clients together (2026-09-28)
- Revisions: `3a60643` (Core `MeetRoom`), `c6d4451` (control-plane `MeetService`), `32c3502` (online client, harness),
  `9011821` (poses only after the arrival is confirmed). Control plane restarted once on `c6d4451`+ (content `d91b31e0…`,
  which also brought it up to date with the V-069 recipe change); it is left running.
- **Design:** the meet is hosted by the control plane over the existing control WebSocket, like the toys — a separate
  room from any race, loopback only, no game server or extra port. Rules in Core `MeetRoom` (≤ 6 humans per D02,
  server-chosen bays and parking transforms, convoy mates together, keyed arrival/departure/disconnect events, pose
  validation, emote ID + start + bounded duration, quick-chat indices, cosmetic likes, 30 s friend reservations, a 30 s
  disconnect grace with a quiet resume, the shared boombox). Protocol: docs/NETWORKING.md §3.7.
- **Tests:** EditMode `MeetRulesTests` 17/17 (room rules). .NET `MeetControlChannelTests` 5/5 over real WebSockets
  (in-process host, DevAuth, SQLite, the real pump, a manual clock): public allocation, arrival events, accepted and
  corrected poses, emote/chat/like replication, leave vs disconnect and rejoin in the same bay, six per room with the
  seventh in a new room, friend's meet refused when full then taken through a 30 s invitation, blocks never sharing a
  public room and refusing likes, convoy members together on one side, the boombox refusing out-of-range and unowned
  requests and reaching the other visitor. Services suite 339/340 (LedgerTests' two-store SQLite concurrency test fails
  under full-suite load and passes 4/4 alone — pre-existing, untouched).
- **Built players** (`9011821`, non-development, three windowed clients, `Tools/run/meet-online.ps1`, development
  accounts from the seed, loopback control plane): **PASS ×3** — all three in the same public room (bays 1, 2, 4; bay 3
  holds a display car); the host saw "Driver 2 arrived" and "Driver 3 arrived"; guest1 walked, waved, said "Nice car!"
  and liked the host's car — the host saw the Wave (animated from the replicated start), the phrase and the like; the
  host jogged ~90 m to the boombox with 0 corrections in 478 poses and queued Workbench Hours — both guests' rooms then
  played it, submitted by the host; guest2 left (host: "Driver 3 left", the car and avatar faded and went); guest1 quit
  without leaving (host: "Driver 2 disconnected", worded apart). Earlier runs on `32c3502`: PASS ×3 twice, with one
  correction each — the first pose was sent before the arrival spot was confirmed (fixed in `9011821`). The offline tour
  also PASSes on `9011821`. Evidence: `Evidence/meet/online-*.jpg`, `online-log.txt`.
- **Limits:** loopback, one machine, three clients (not remote devices; no impairment matrix yet for the meet); remote
  avatars use default looks derived from the display name (no appearance picker); the convoy ribbon/Ready controls are
  not yet shown inside the meet (an allocation there does leave the room and hands over to the race — code path, not yet
  exercised by a run); late joiners start the boombox cue from its beginning (the synth has no seek); "Join Friend's
  Meet" and invitations exist on the server and in its tests but have no screen yet.

## V-075 — The convoy at the meet: header, Ready from the meet, a friend's meet by invitation (2026-09-28)
- Revision: `5001c7a`. Control plane unchanged (the `c6d4451`+ process from V-074, still running).
- **What changed:** at the meet, a convoy member sees a compact convoy header (members, leader, phase, mode/event
  readiness and their own answer) on a dark plate at the top centre; while the leader's ready request waits for them a
  pinned line names the Menu control, and the request goes to the SIGNAL ribbon once per proposal. The meet menu offers
  Event Ready/Unready (or Mode Ready) and "Invite a friend to this meet" (a place held 30 s). The Friends screen lists
  meet invitations ("Driver 1 holds a place for you at the meet · bay 2 · 28 s" → Join meet / Decline) and offers
  "Join meet" for a friend whose presence is At the meet. The interaction prompt is cleared while seated in the car.
- **Built players** (`5001c7a`, non-development, two windowed clients, `Tools/run/meet-online.ps1 -Convoy`, development
  accounts 0 and 1 who are friends, loopback control plane): **PASS ×3** on `5001c7a` (plus one PASS on the source
  just before the header plate). Each run: the host creates a private convoy and shares the code; the guest joins; the host
  proposes Campaign · Normal, both answer Mode Ready, the host enters the mode and proposes the event; both open the
  Convoy Meet and land in the same convoy room on the same side (bays 1 and 2); both answer **Event Ready from the meet
  menu** (convoy state: event ready 2/2); the guest leaves the meet; the host invites it from inside the meet; the
  guest sees the invitation on the Friends screen, joins the friend's meet and gets back into the host's room in its
  held bay; both leave the meet and the convoy. The three-client public run (V-074 scenario) also PASSes on `5001c7a`
  (0 corrections, 472 poses). Evidence: `Evidence/meet/convoy-*.jpg`, `convoy-log.txt`.
- **Fixed on the way (harness only):** the guest could read the shared convoy code while the host was still writing it
  (now written aside and moved into place, read tolerantly); the tour now waits until the meet flow has returned to its
  menu screen before navigating (the flow re-showed Friends after the tour had moved to Convoy).
- **Limits:** loopback, one machine, two clients; the leader did not press Start at the meet, so an allocation handing
  over from the meet to the race (code path `LeaveMeetForRace`) is still not exercised by a run; no impairment run.

## V-076 — Race from the meet and back; walking controls on the Controls screen (2026-09-28)
- Revision: `c60d2db`. Control plane restarted once on `c60d2db` (the `meet.leave` reason; content `d91b31e0…`
  unchanged); it is left running.
- **What changed:** the leader's meet menu offers "Convoy: Start the event" once every racing member is Event Ready
  (the header says so); the allocation leaves the meet with `meet.leave {reason: "race"}`, which the room announces as
  "left to race" — never "left" or "disconnected" (Core `NoticeKind.LeftToRace`). After a race that began at the meet
  the Convoy screen offers **Back to the Meet** (your convoy's / your friend's / a public meet; the bay is allocated
  afresh). Settings → Controls gains a **Walking (meet)** page (walk ×4, jog, interact, emote wheel, quick chat,
  recenter, photo, rescue, meet menu; keyboard and controller; stored apart from the driving bindings). Polish: a meet
  invitation's notice lapses with its 30 s place; the meet menu panel sits below the SIGNAL ribbon and hides the
  interaction prompt while open; the convoy header names the post-race decision.
- **Tests:** EditMode `MeetRulesTests` 17/17 (a race departure is worded apart and still fades and frees the bay).
  .NET `MeetControlChannelTests` 5/5 (`meet.leave {reason: "race"}` → `lefttorace`, no `departed`).
- **Built players** (`c60d2db`, non-development):
  - `meet-online.ps1 -Convoy -Race` (two clients, loopback control plane, one game server bound to 127.0.0.1 UDP 7792
    through NetGuard, released afterwards): **PASS ×2** (the first on the same meet code before the Controls page and
    polish, the second on `c60d2db`). Both answer Event Ready at the convoy meet, the leader starts from the meet menu,
    both clients leave the meet into race loading (validator autopilot), race the next campaign stage (S02, then S03) to the finish (P1/P2, stage
    cleared, receipts settled), return to the Convoy screen, use Back to the Meet and meet again in the same convoy room
    (bays 1 and 2), then leave. Evidence: `Evidence/meet/race-*.jpg`, `race-log.txt`.
  - `meet-online.ps1 -Convoy` (V-075 scenario) PASS and the three-client public run PASS (0 corrections, 476 poses).
  - Offline meet tour (`-nsMeetTour`, `-nsPrefsFolder` under `Builds/`): PASS — Interact remapped to F on the walking
    page by pressing F (the stored override holds `<Keyboard>/f`), and the meet's prompt beside the car then read
    "F / A  Your Kogane Hachi RS". Evidence: `Evidence/meet/controls-walking-interact-on-f.jpg`,
    `tour-02-out-of-car-f-prompt.jpg`.
- **Limits:** loopback, one machine, two clients; the "left to race" notice is covered by tests (both members leave at
  once in the run, so neither sees the other's); the walking page was exercised with a simulated keyboard in one window
  (focus-dependent input is not driven in the multi-window runs); no meet run under impairment.

## V-077 — The Player Card: name, pronouns and a driver appearance, seen by others at the meet (2026-09-28)
- Revisions: `f8ccfd1` (server: Core `PlayerLooks`, migration 0006, card look/pronouns, looks in the meet state),
  `bba67d9` (Player Card screen, preview stage, meet avatars from card looks). Control plane restarted once on
  `f8ccfd1` (migration 0006 applied to the local development SQLite database); it is left running.
- **What changed:** Convoy screen → **Player Card**: display name, optional pronouns (≤ 24 plain characters) and the
  driver's appearance — eight accessible starting looks plus steps for build, height, skin, expression, posture, hair,
  hair colour, facial hair, headwear, outfit and its three colours, sleeves, lower and its colour, shoes and their colour
  and an extra — with a live turntable preview built by the same rig the meet uses. Visual only. Saved with the card
  revision (a conflict reloads); the server validates the look against the builder's vocabulary (≤ 4 distinct
  accessories, colours `#RRGGBB`, height 1.45–1.98), stores it as canonical JSON and hands it to the meet room, where
  your own and other visitors' avatars are built from it (the default look from the name otherwise). The file move of
  `CharacterLook` into Core landed in `8023a0d` (staged by `git mv` before that docs commit; that state compiles).
- **Tests:** .NET `PlayerApiTests` (look validated, stored canonical, kept across name-only edits, cleared by null;
  pronouns; refusals change nothing) and `MeetControlChannelTests` (a card look reaches the other visitor); all
  Services suites 232 + 92 + 123 + 341 pass on `f8ccfd1`. EditMode `CharacterTests` + `MeetRulesTests` 117/117 (starting
  looks valid, canonical round trip unchanged, sound meshes within budget; unknown words and five accessories refused).
- **Built players** (`bba67d9`, non-development, loopback control plane): `meet-online.ps1 -Convoy` **PASS** — the
  host opens the Player Card, starts from Look 3, changes the hair to locs and saves through the screen (server reply
  and `/v1/me` hold the look); at the convoy meet the guest's client built the host's avatar from exactly that look
  (locs, hoodie, athletic). Also PASS on the two builds before (the preview framing and swatch tiles were adjusted
  between them). Race from the meet (`-Convoy -Race`): **PASS (the host saved the card first; both raced S04 to the finish and came back to the convoy's meet)**. Three-client public run: PASS. Evidence:
  `Evidence/meet/card-*.jpg`, `card-log.txt`.
- **Public driver card** (`fd92116`; control plane restarted on it): `GET /v1/players/{id}/card` adds pronouns, campaign
  clears per mode and challenges completed (`SocialApiTests`; Services suites 232 + 92 + 123 + 341 pass). At the meet,
  inspecting another driver's car offers their driver card (local UI; they are not told). Built players (`fd92116`),
  `meet-online.ps1 -Convoy`: **PASS** — the guest opened the host's card: "Driver 1 @nsdriver0 · Night Runner 1,080 RP ·
  Normal 10/30 · Hard 0/30 · Challenges 2/75 · V01 Kogane Hachi RS PI 220 class D". Evidence:
  `Evidence/meet/card-guest-views-host-driver-card.jpg`.
- **Limits:** online only (the offline profile's meet visit still uses the default look); showcase records, flag,
  preferred car and card background/frame/motif/title are not built; the look is
  read when a visitor joins a room (a change is seen after they next join); remote looks were checked in data and in the
  host's own view, not photographed from the guest's camera.

## V-078 — Race audio: engines on every race car, race music by stage and region, results cues (2026-09-28)
- Revisions: `6df7412` (CarAudio, Core RaceMusic, session wiring, tests), `c8cd328` (the tour's full-grid leg).
- **What changed:** every drawn race car — offline races, online races (racing or spectating), the Garage Test Yard and
  practice — carries the existing procedural `EngineAudio` (engine by architecture/rpm/load, turbo, shifts; tyres by
  slip and surface; impacts), fed each frame from `VehicleView.Render`: the local simulation's telemetry where the car
  is simulated, the replicated rpm/gear/boost/suspension otherwise; throttle estimated from acceleration and engine
  speed. Your car and the nearest cars within 170 m synthesize — at most six at once (the stack measured in
  docs/AUDIO.md). Race music (Core `RaceMusic`): the four lieutenant themes, Four Signals and the two finals are chosen
  by the campaign STAGE only (a course never selects a final theme); Team Trials by kind; the tutorial bed; otherwise
  the course region's arrangement. It starts at the countdown and the win/loss results variant plays at the finish.
- **Tests:** EditMode `RaceMusicTests` (encounter themes by stage and not by course, trial kinds, the six regions, every
  chosen cue present in the manifest for every course and stage) with `MeetRulesTests` + `CharacterTests`: 119/119.
- **Built player** (`c8cd328`, non-development, `-nsRaceAudioTour`, tour-scoped preferences): **PASS** — offline S07 on
  C04 (the Mizuhana lieutenant encounter, 4 cars, validator autopilot): `MUS_LT_DAIGO` from the countdown; at the
  listener, cars alone peak −16.4 dBFS RMS and music alone −19.1 (each measured by muting the other's volume setting),
  both together −14.3; all 4 cars synthesizing, yours always; finished P1 → `MUS_RESULTS_WIN`. Then a full grid (you +
  11 AI) on C01: `MUS_RACE_MIZUHANA`, exactly 6 of 12 cars synthesizing throughout. The same tour on `6df7412` (S07
  leg only) also PASSed. Online: **PASS** (`meet-online.ps1 -Convoy -Race`, `c8cd328`) — both clients' race launched from the meet played `MUS_RACE_KASUMI` from the countdown (S05, a regular Kasumi stage), with no exceptions in either log (engine audio is attached to the `RaceClient` cars but its level was not measured in that run); the online results cue shares the offline code path but was not logged in that run. Evidence: `Evidence/audio/race-audio-log.txt`.
- **Limits:** measured levels, not listening — the mix balance, engine realism and music against engines are unheard
  by a person (human check); the in-race CPU cost was not profiled in the player (the six-car stack was measured
  offline in docs/AUDIO.md); remote cars' throttle is an estimate; spoiler protection of encounter titles is not
  involved (themes play in their own races).

## V-079 — Drift Attack only on courses with judged drift zones (2026-09-28)
- Revision: `cce88cd`. Content hash `3677a844…` (a new authored document); the control plane was restarted on it and
  the Unity content library refreshed; both agree (the convoy run below connected and played through).
- **What changed:** `authored/course-drift-zones.json` lists every course with the number of drift-zone gates on its
  route (11 of 29 courses have some: C01, C04, C08, C12, C15, C16, C23, C24, C25, FP01, T00). The Core catalogue loads it
  (`DriftZones`, `SupportsDriftAttack`); `FreeplayRules.Supports` takes the catalogue, so Drift Attack proposals, course
  votes, the convoy's freeplay course list (`freeplayAccess`) and `GET /v1/courses` offer Drift Attack only where there
  is something to judge, and a drift Team Trial must be on such a course. Offline menus never offered Drift Attack.
- **Tests:** EditMode `CatalogueTests.DriftZoneList_MatchesEveryCourseRoute` (every course listed, counts equal to each
  `route.json`) with `RaceMusicTests` + `AddendumRulesTests` 30/30; .NET `SocialApiTests` (C01 offers Drift Attack, C02
  does not, Time Attack stays), `ContentManifestTests` (a drift trial on C02 refused, on C01 accepted); all Services
  suites 232 + 92 + 123 + 341 pass.
- **Built players** (`cce88cd`): `meet-online.ps1 -Convoy` PASS on the new content hash (clients and control plane agree).
- **Limits:** the Convoy screen's Drift Attack course list was not photographed (it shows the server's filtered list);
  zone counts come from the route data — whether each zone is well placed is a separate design review.

## V-080 — AI drift skill per driver (2026-09-28)
- Revision: `21a9897` (EditMode and PlayMode in the open editor; no player build involved).
- **What changed:** `DriverProfile.DriftSkill` (0 = unset = the tuned drift controller exactly — the validator
  autopilot and every earlier result keep it). Rivals get a skill that rises with the campaign stage, follows their
  tendency (rotation/recovery/momentum specialists higher; margin-keepers and straight-line planners lower) and adds a
  little when their sheet's strength names slides or rotation; freeplay opponents spread 0.45–0.8. The skill changes
  **behaviour, not the slide physics**: below the tuned point a driver commits to only a share of the judged zones
  (40 % at skill 0; which zones is fixed per driver from the grid slot); above it the driver re-initiates the slide up
  to twice per zone visit after running out of road, when straight again with enough zone left.
- **How it was chosen (measured, not assumed):** the first mapping (deeper angle for skilled drivers) scored *lower* on
  C01 — every attempt ends at the road edge and a deeper slide reaches it sooner. A one-knob sweep (explicit PlayMode
  `DriftTuningSweep`, `Evidence/courses/drift/sweep.txt`) showed angle, entry speed, countersteer gain and edge margin
  move the banked score in different directions per course (e.g. a 1.5 m edge margin: C01 −14 %, C08 +34 %), and an
  edge-management variant changed nothing; so none of them became a skill.
- **Tests:** EditMode `DriftSkillTests` 2/2 (unset = tuned point; the mapping; rivals rise with the campaign and follow
  their tendency; generic spread); full EditMode suite **442/442**. PlayMode `DriftSkill_RaisesTheBankedScore`
  **PASS**: V04 alone with the validator autopilot at skills 0.15 / 0.65 / 0.95 on C01, C04, C08, C12 — totals **7,563
  < 9,679 < 11,704** pts (`Evidence/courses/drift/skill.txt`). `DriftAttack_ScoresAndRanksByDrift` C01/C08/C12 still
  PASS with the skilled generic opponents (evidence regenerated).
- **Limits:** per course the effect is **not** monotonic — on C04 the skilled driver's re-initiations cost points
  (478 vs 1,717: attempts into the walls), on C08 the novice's skipped zone helped (2,483 vs 2,068); the gain is an
  average over courses, dominated by C01. The drift controller itself (every attempt ends at the road edge) is the real
  limit; a controller that manages the edge is future work. Rivals in drift events now drift differently from before,
  and the S29 featured rival (who drifts the Arc) may re-initiate — the certified S29 benchmark was not re-measured.

## V-081 — Group Time Attack with four and six humans (2026-09-28)
- Revision: the player build of `cce88cd` (content `3677a844…`; it does not contain V-080's drift-skill code, which only
  affects drift events), loopback control plane
  and one dedicated game server on 127.0.0.1 (NetGuard, released after each run). `Tools/run/net-race.ps1 -Humans N
  -FreeplayCourse C01 -FreeplayMode time-attack`: separate client processes (AutoClients with the validator autopilot
  through normal inputs — automation, not people).
- **4 humans:** match on C01 "4 humans, 0 AI, contact non-contact"; all finished clean — 85.957 / 86.612 / 86.716 /
  86.804 s; settled by the control plane as `FreeplayTimeTrial` — **PASS**. `Evidence/net/run-20260928-065043-h4-C01-ai0`.
- **6 humans:** "6 humans, 0 AI, contact non-contact"; all finished clean — 85.957 … 87.049 s; settled — **PASS**.
  `Evidence/net/run-20260928-065317-h6-C01-ai0`. The first four times are identical to the four-human run: with six
  cars on the same line at once nobody touched anybody (non-contact holds).
- **Limits:** one machine, loopback, scripted drivers; no impairment in these runs.

## V-082 — While We Wait with four and six humans at the convoy's hosted tables (2026-09-28)
- Revision: `40a3fae` (player build of that commit; content `3677a844…`), loopback control plane only (no game server,
  no other port). `Tools/run/toys-group.ps1 -Humans N`: N windowed clients (development accounts 0…N−1) sign in through
  the real screens, client 0 creates a private convoy and shares the code through a file under Builds/, the others join
  with it; everyone opens While We Wait (`FrontEndApp.ToyTourGroup`). Automation over real sockets, not people.
- **4 humans — PASS (4/4 clients):** Greenlight — two clean Lights Out attempts each, every client sees the other 3 on
  the shared board; Cap Clash — two shots each through the serialized queue, every client sees all 6 others' shots;
  Pit-Crew — one operation each on the shared model, every client sees the other 3 done; Canvas — every client's marks
  on the hosted sheet (9 others' marks seen by each; 12 on the sheet). Evidence: `Evidence/ui/online/toys-group/h4-*`.
- **6 humans:** **PASS (6/6 clients):** every client saw the other 5 on the Greenlight board, all 10 others' Cap Clash shots through the serialized queue, 5 others' Pit-Crew operations, and 15 others' Canvas marks (18 on the sheet). Evidence: `Evidence/ui/online/toys-group/h6-*`.
- **Limits:** one machine, loopback, scripted inputs; every client draws the same tour strokes, so the Canvas marks
  overlap on screen (the counts come from the hosted sheet); Pocket Circuit (V-030) was not part of this group run.

## V-083 — Canvas controller pen (2026-09-28)
- Revision: `a2779f3` (player build of that commit), one window, tour-scoped preferences, Local profile (no network).
- **What changed:** Convoy Canvas reads one pointer from the mouse or a controller, whichever moved last: the right
  stick moves a crosshair pen over the sheet and the right trigger is the button for every tool (pen strokes, lines,
  shapes, stamps, lettering, eraser) through the same operations as the mouse; the hint line names the controls. The
  app's UI input module no longer navigates with the right stick (the left stick and d-pad still do) — the default
  Input System UI actions bound both sticks, so moving the pen had moved the tool-column focus (seen in the first run).
- **Built player** (`-nsCanvasPadTour`, a virtual gamepad added by the tour): **PASS** — the right stick took the
  pointer (pen at 682, 591 px), one RT stroke gave exactly one new mark with 236 points, the menu focus stayed on
  Tool/Prev throughout, and a mouse move handed the pointer back. Evidence: `Evidence/ui/canvas-pad/`.
- **Limits:** a simulated controller, not a person with a pad; the online Canvas uses the same screen code but was not
  driven by a controller in a networked run.

## V-084 — The meet's touring challenges CH61–CH65 (2026-09-28)
- Revision: `5934d04` (player build of that commit; control plane restarted on it).
- **What changed:** Core `MeetTouring` defines the five touring challenges and where each act counts: CH61 First
  Parking Place (arrive, then inspect your own car beside it), CH62 Four Corners of the Terrace (read all four viewpoint
  placards at the placards), CH63 A Driver's Greeting (wave and bow at the tutorial host and read the emote help there),
  CH64 A Picture With a Horizon (save a photo at the overlook marker that passes the new composition check: marker,
  your car in frame, horizon level), CH65 Bring It Home (read your result slip at the timing board after a finished
  event). **Online** the meet room records arrivals and wave/bow itself and `meet.touring {step, id}` for the rest,
  always against the server-held position (a claim from across the terrace is refused `not_here`; the result slip
  needs a settled finish, else `no_event`); completions go through the new `GrantChallengeAsync` — unlock, cosmetic and
  cash exactly once with an append-only ledger entry (`challenge/<account>/<id>`) — and are pushed as `meet.challenge`
  (a SIGNAL notice). **Offline** the Local profile grants them through `LocalProgression.CompleteMeetChallenge`
  (touring family only, once). The meet menus now say that only these challenges pay out at the terrace.
- **Tests:** .NET `MeetTouringTests` (over the real control channel with a route found on the Core walkable map: refused
  before arriving, CH61 beside the car, emote help refused from the bay, result slip refused at the board with no
  event, CH63 after walking to the host with server-played wave and bow, never twice, wallet and `/v1/me` updated);
  all Services suites 232 + 92 + 123 + 342 pass.
- **Built players:** offline meet tour — **PASS**: CH63 at the host, CH62 after reading the four placards at their
  places (the East placard read from the plaza earlier counted for nothing), CH61 beside the own car, CH64 with the car
  framed from the marker (composition check true), each +3,000 cr once; CH65 *not* granted (the new profile has no
  finished event). Convoy tour online — **PASS**: the host's own-car inspection granted CH61 with the ribbon notice; the
  guest's view of the host's driver card then read 1,120 RP and 3/75 challenges (1,080 and 2/75 before). Evidence:
  `Evidence/meet/touring-log.txt`, `touring-ch64-photo-composed.jpg`.
- **CH67 The Working Landscape** (silver, `333b217`): the three named photo points (tea kiosk, radio bench, maintenance
  gate) are Core `MeetLayout.PhotoPoints`, read in place like the placards; EditMode `MeetRulesTests` 18/18 (in place
  only, each challenge once); the offline tour then earned CH61–CH64 and CH67 (+8,000 cr) and not CH65 — **PASS**.
- **Limits:** the photo composition is judged by the client (the room checks where you stand); CH65 online was
  exercised by the server test's refusal only (no run finished an event and then read the slip); progress towards a
  challenge (e.g. two of four placards) is kept only while the service or the session runs; the cosmetic rewards are
  granted as ownership records — the reward assets themselves are not built.

## V-085 — More challenge predicates: CH33, CH35, CH44/CH45, CH66, CH71 (2026-09-28)
- Revision: `982a22b` (player build of that commit; control plane restarted on it).
- **What changed:** race progress judges the first route sector once it has been driven (no wall incident, reset,
  corridor cut or time outside the corridor) and keeps the wall count at the first reset; the shared predicates add
  **CH33** Clean Opening and **CH35** One Reset, Then Clean, evaluated by the game server online and — new — for
  offline races too (Local facts; offline races previously evaluated no challenges). Settlement adds **CH44/CH45**
  (the Normal/Hard S30 finale cleared personally within the qualifying benchmark, not only the support envelope) and
  the cumulative **CH66** (a legal finish in each of the six regular regions) and **CH71** (T00 and every course
  C01–C25) from the account's settled receipts; the Local profile judges CH66/CH71 from its records (Core
  `CumulativeChallenges`).
- **Tests:** EditMode `RouteProgressTests.ChallengeFacts_FirstSectorAndFirstReset` on the real C01 route (a clean
  opening gives CH33 and CH01; a wall incident before the sector end spoils CH33; one reset then no impact gives CH35,
  an impact after it does not) — 26/26 with `MeetRulesTests`. .NET `Addendum01SettlementTests`: S30 Normal/Hard inside
  the target and ahead of the live rival → CH44/CH45, a slow finish → neither; a finish that completes the sixth region
  → CH66, five regions → nothing. Services 344/344; Builds 232, Toys 92, Core 123.
- **Built players** (`net-race.ps1 -Humans 2 -Stage S03`, loopback game server and control plane, autopilot clients):
  both humans finished (P1, P2) with clean opening sectors; the game server reported `CH33` for each and the settled
  receipts list `challengesUnlocked: ["CH33"]` for both — **PASS**. Evidence: `Evidence/net/run-20260928-080628-h2`.
- **Drift challenges** (`7b69ac6`): Core `DriftScorer` now keeps every banked chain (raw points and the slowest valid
  scoring step), and the shared predicates add **CH18** (two banked chains ≥ 6,000 on C04), **CH20** (25,000 raw in
  C01's Drift Attack), **CH21** (70,000 raw in C08's wet Drift Attack with ≥ 2 chains) and **CH24** (one chain ≥ 60,000
  on C15 never below 45 km/h while scoring), on the game server and offline. EditMode `DriftChallengeTests` (chains
  recorded; each predicate on its course, format and surface only; a finish required) — PASS; .NET Core 123/123. The
  autopilot's scores (a few thousand points) are far below these targets, so no automated run earns them.
- **Limits:** CH35, CH44/CH45, CH66 and CH71 are covered by tests, not by a built-player run; CH71 needs T00, which only
  the offline tutorial runs today (online it cannot complete yet); challenge rewards are ownership records (the
  cosmetic assets are not built).

## V-086 — Car levels of detail (2026-09-28)
- Revision: `201598b` (the LOD code) plus the tour's bracketed measurement, committed with this entry; player build of
  that working tree (non-development, PC quality level, LOD bias 2).
- **What changed:** every car has three bodies. `CarBodyGenerator.BuildBody(..., lod)`: level 1 (mid) lofts 28 stations
  instead of 80, draws the conformed lamp/opening/trim patches and the arch lips about half as finely and leaves out
  the feature lines and the steering-wheel rim (mirrors, seats and dash stay); level 2 (far) lofts 14 stations, samples
  the glasshouse half as finely, draws patches a third as finely and leaves out mirrors and interior. Fascia and lamps
  stay at every level. Mean body triangles over the 18 cars: 8,893 full / 4,503 mid (45–53 %) / 2,577 far (26–30 %);
  length, height and placement identical. `VehicleView` adds a `LODGroup`: full body + plate + livery + wheels; mid
  body + livery + wheels; far body + wheels; culled below 0.8 % of screen height — with the 58° race camera and the PC
  bias of 2, mid from about 45 m, far from about 135 m, culled beyond about 1 km. The fitted cockpit is outside the
  group (never culled by it). Editor *Night Signal → Art → Render Car LOD Sheets* renders the forced levels.
- **Tests:** EditMode `CarLodTests` (18 cars: mid ≤ 60 %, far ≤ 35 % of the full body's triangles, same length/height,
  paint/glass/trim/lamp submeshes at every level, valid normals; the view's LOD membership; Unity's rule by distance;
  the own car on the full body in all five views and the cockpit outside the group) — full EditMode suite 467/467.
- **Built player** (`-nsCarLodTour`: offline freeplay C01, you + 11 AI, autopilot; frozen frames, rendered-triangle
  counter): your car drawn at its full body in all five views; in Cockpit view the open-cabin body is shown and hiding
  the fitted cockpit changes 1.79 M pixels (repeat 1.4 k). Six snapshots through the race: 45 of 45 matched in-view
  car samples drawn at the level of Unity's rule for their distance (21 mid, 12 far; 1 sample of 46 unmatched by
  drift); automatic LOD drew 42–98 k fewer triangles than every car held full (of 450–958 k) in the 5 snapshots with
  a distant car — **PASS**. Evidence: `Evidence/art/lod/` (tour lines, a race frame, the cockpit frame, LOD sheets).
- **How it was measured, and a correction:** the GPU Resident Drawer draws these renderers through BatchRendererGroup,
  so `Renderer.isVisible` reported every level visible at once — the first tour's level table was meaningless and is
  not used. Levels are measured on frozen frames instead: each hold (full, mid, far) bracketed by releases; the hold
  that counts the same as the releases on either side is the level drawn. An intermediate version compared all cars
  against one global baseline; the paused scene's count drifts by a few thousand triangles, which made levels look
  history-dependent, and commit `78606b2` wrongly concluded that the GPU Resident Drawer keeps stale levels and made
  each car choose its own level. Re-measured locally, Unity's automatic selection matched the rule (46 of 46 that run);
  `201598b` returned the choice to the `LODGroup`.
- **Limits:** no frame-time A/B (the tour's first attempt compared blocks at different places on the course, so its
  timings are not a comparison); level changes are instant pops (no cross-fade; the sheets show the mid body is hard to
  tell from the full one at its distance); the six-racer / populated-meet / cold-load performance profile of §14 and
  character LOD tiers are not done; not reviewed by a person.

## V-087 — Heavy-contact hitch: the client's contact predictor tested offline (negative result) (2026-09-28)
- Revision: working tree on `9a88aa1`; test-only (the game's code is unchanged).
- **Hypothesis:** the client predicts contact one-sidedly — each remote car is its latest snapshot extrapolated and
  never gives way, while the server moves both cars of a pair — so sustained contact at ~190 ms RTT would be
  mispredicted and cause the corrections behind the V-061 hitch.
- **Method:** EditMode `ContactPredictionTests` (explicit, a record like the drift tuning sweep). A scripted server
  drives two cars (V01, V05) into sustained side contact on flat ground and resolves the pair both ways each tick; an
  emulated client predicts its car 14 ticks past the newest snapshot (≈ 95 ms each way plus the 2-tick input lead),
  receives snapshots at 20 Hz, reconciles at 3 cm / 0.2 m/s and replays, as RaceClient does. The same server run (same
  contact load) is measured with the one-sided predictor and with remote proxies that take their share of each contact.
- **Result** (three starting gaps; 348–386 contact ticks, 155 snapshots each): one-sided — mean position error 4–6 mm,
  worst 2.8–4.0 cm, 57–62 corrections; remote proxies — mean 7–8 mm, worst 2.1–3.7 cm, 59–63 corrections. The
  alternative is no better and was **not shipped**. With no contact the prediction is exact (0 corrections).
- **Conclusion:** in sustained side contact the one-sided predictor stays within millimetres of the server, so it does
  not explain the p99 tail of V-061. Remaining suspects: remote manoeuvres the client cannot know inside its ~230 ms
  horizon (braking or steering into you), chain contacts with a third car and barrier interplay the client does not
  model, and genuine hits (which should be felt).
- **Limits:** an emulation — it re-implements RaceClient's reconcile and the one-sided predictor rather than calling
  RaceClient; one scripted scenario on flat ground; no built-player run.

## V-088 — Visual presets: rename and delete in the Appearance screen (2026-09-28)
- Revision: working tree on `217b921` (committed with this entry); player build of it.
- **What changed:** each preset row has Rename (the name typed in the name field; Core's name rules and per-car
  uniqueness) and Delete (Core's confirmation: the first press asks "Its saved look is lost. Press Delete again", the
  second deletes; the applied livery itself is untouched). The Local backend gained `visual-preset-rename` (the control
  plane already had both, with .NET tests in `GarageServiceTests`).
- **Built player, Local** (`-nsAppearanceTour`, isolated profile folder): after the two presets of V-046, "Livery 2"
  renamed to "Night Run"; Delete on "Livery 1" asked first (both kept), then deleted; the profile re-read from disk holds
  exactly "Night Run"; S01 still drew the applied livery — **PASS**. Screenshots `Evidence/ui/appearance/10b-delete-asked.jpg`,
  `10c-presets-after.jpg`. (The first launch after the build failed at its first click — "OfflinePlay" not available at
  3 s — and was not reproduced on two relaunches; the tour's Click now logs whether a button was missing or disabled and
  on which screen.)
- **Built player, online** (`ui-tour-online.ps1 -Appearance`, loopback game server, local control plane, seed dev
  account): livery applied (`5ea613e7…` → `c8b69cee…`); a preset saved into the first empty slot, renamed to a
  per-run name, Delete asked (kept), then deleted — all through the control plane; the race then carried the livery
  (roster 277 bytes, plate "NS ONL", 2 decals), P2 of 3, settled — **PASS**.
- **Limits:** the online tour only touches the preset it created; the pearl flip tint is still not rendered.

## V-089 — The meet shows the livery applied in the Garage (2026-09-28)
- Revision: `c1d9d93` (client and Core) plus the control-plane fix committed with this entry; player build of
  `c1d9d93`; control plane restarted twice on the new server code (after `c1d9d93`, then after the fix).
- **Found by the new tour:** a visitor outside a convoy joins the public meet without naming a car instance, and the
  control plane then admitted their first car's model **with no livery** — everyone (themselves included) saw it in stock
  paint whatever livery was applied. The first run of the new leg failed on exactly that ("back … with livery stock").
- **What changed:** such a visitor now arrives in their first car instance as the Garage has it (applied livery, PI,
  tune — `GarageService.DefaultMeetAppearanceAsync`). A member reconnecting after a lost connection takes the car and
  livery they rejoin with, and the control plane refreshes PI, class, tune and card look on that path (`MeetRoom.Join`).
  The member wire carries the visit `generation`; the client rebuilds a visitor's car and avatar when the generation,
  car, livery or look changes instead of keeping the first one.
- **Tests:** EditMode `MeetRulesTests` 19/19 (reconnect in another livery or car takes it; a new visit is a new
  generation). .NET: `AVisitorOutsideAConvoy_ArrivesInTheLiveryAppliedInTheGarage` (stock visit, livery applied over
  HTTP, second visit carries it) and the reconnect test now checks the generation is kept — Builds 232, Toys 92, Core
  123, Services 345, all pass. (Before the fix, one Services run had a single failure that did not reproduce in three
  reruns; its name was not captured.)
- **Built players** (`meet-online.ps1 -Livery`: three clients on the loopback control plane): the usual public-meet
  regression (arrivals announced, wave, quick chat, like, boombox cue for everyone, a departure, a dropped connection) plus
  the new leg — guest2 leaves for the Garage, repaints and applies (`e649b12a…` → `8a6c7116…`), returns; the host's
  drawn car for guest2 has the paint the room's livery decodes to (#5C6670) — **PASS** on all three. Repeated on a
  second run (#4B2E6B, `8a6c7116…` → `b4e11c16…`) — **PASS**.
- **Limits:** the host's screenshot is taken at the boombox, where guest2's car is out of frame — the evidence is the
  colour check, not a picture (the second run's build also turned the host's camera toward the car for the photo; it
  framed the host instead, did not affect the checks and was removed); the reconnect-in-another-livery path is covered
  by tests, not by a built-player run.

## V-090 — Challenge gates judged: CH03, CH06, CH09; CH16 from the drift chains (2026-09-28)
- Revision: `66cfc18` (CH16) and the gate work committed with this entry; player build of it.
- **What changed:** the course routes already tag gates with the challenge they serve; `RouteGateDef.Challenge` now keeps
  that tag and `GateJudge` measures, per entrant and per pass, every challenge apex / precision gate (the body across the
  gate's band, as the Four Signals apexes are judged — on a circuit every lap's pass must touch), every challenge lane
  zone (crossed start to end with a body widened by 0.5 m never overlapping a barrier — a new barrier-only world query,
  `IVehicleWorld.NearBarrier`) and every step with any barrier contact (a guardrail touch, stricter than a meaningful wall
  incident). The race predicates add **CH03** Apex Appointment (C03's three designated apexes, no wall incident),
  **CH06** Cedar Accuracy (C05's six precision gates, not one guardrail touch), **CH09** Bridge Margin (both C13 viaduct
  lanes with the margin kept) and **CH16** First Arc (one banked 8,000-raw chain on T00 and a finish — T00's finish
  stands for "complete the lesson" until a lesson system exists), online (game server) and offline alike.
- **Tests:** EditMode `GateJudgeTests` on the real C03 / C05 / C13 routes (all gates touched on both laps → CH03, one
  apex missed → none, a wall incident → none; six precision gates → CH06, one guardrail touch → none; lanes with the
  margin kept → CH09, a barrier inside the margin in the second zone → none, the same barrier outside the zones → kept),
  `DriftChallengeTests` (CH16) — full EditMode suite 471 passed, 2 skipped (the explicit V-087 record), 0 failed.
- **Built player** (`-nsGateTour`: offline freeplay, the validator autopilot steering through each course's challenge
  gates, no AI): C03 finished, the three CH03 apexes touched on both laps (2/2 each), no wall → CH03 granted; C13
  finished, both viaduct lanes passed with the margin kept against the real barrier colliders → CH09 granted; C05
  finished with all six precision gates touched but one wall incident and 9 guardrail-contact steps → CH06 withheld, as
  the facts say. The tour checks each grant against the raw counts — **PASS**. (A first run of the same races judged
  identically; its tour then demanded CH06 be earned, which the autopilot's line does not do, and was changed to the
  consistency check.) Evidence: `Evidence/courses/gates/gate-tour.txt`.
- **Not implementable yet, and why:** CH04, CH08, CH12 need published gate speeds / windows (the routes author the
  gates with `targetSpeedKmh` 0); CH26, CH29 need fixed Gold drift references; CH41 needs the class-equalized challenge
  race format. 23 of 75 challenges now have predicates.

## V-091 — The Driver Card offline: a Local profile's look at the offline meet (2026-09-28)
- Revision: `0bcd323` plus the Driver Card work committed with this entry; player build of it.
- **What changed:** the Local profile's card (`CardAppearance`) gains `Look` (canonical `CharacterLook` JSON, the online
  card's form; "" = the default look from the name) and `Pronouns` — optional fields, so earlier profiles load unchanged
  with the default look. `LocalProgression.SetCard` validates the name, the look (`PlayerLooks`, as the server does) and
  the pronouns (the server's rule: up to 24 plain characters) and is a no-op when nothing changed; the profile validator
  checks both. The Player Card screen works offline against the open Local profile (reached from the offline hub's new
  Driver Card button) and the offline meet builds the player's avatar from that look. The .NET Core subset now compiles
  `Core/Characters` (engine-free).
- **Tests:** .NET `LocalCardTests` (look stored canonically, re-read identical, repeat is a no-op; five accessories,
  unreadable JSON, 25-character or markup pronouns refused; back to the default look; a profile document without the new
  fields loads and validates) — Core 125/125.
- **Built player** (`-nsDriverCardTour`, buttons only, isolated profile folder): markup in the pronouns refused with the
  rule's message and nothing stored; starting look 5 and "she/they" saved; the profile re-read from disk holds both; the
  offline meet's avatar is built from that look — **PASS**. `Evidence/meet/driver-card-offline.jpg` (the card screen
  and the avatar at the terrace).
- **The offline meet tour on this build:** one run passed its new Driver Card steps (saved, read back, used by the meet)
  but failed its boombox check — an extra Interact reached the game during the 3 s check (a second "interact boombox" in
  the meet log that the tour never issues; Interact only comes from real input), with the game window in front of a
  desktop in use. Two reruns could not get keyboard focus at their first keystroke. Not reproduced under control; the
  boombox code is unchanged since the tour's last PASS (V-084). Keyboard tours need an idle machine.

## V-092 — The Player Card's style: background, frame, motif, title, layout, region, preferred car (2026-09-28)
- Revision: `19ef8a9` (Core), `2548c7d` (control plane), `2b83aee` (client); player build of `2b83aee`; control plane
  restarted on `2548c7d` (customization hash `306dd38e…`; the race content hash is unchanged, `3677a844…`).
- **What changed:** customization.json gains a `card` section — free defaults (three backgrounds, two frames, three motifs,
  three titles, a standard layout) and the fifteen card_customization challenge rewards (COS-CH46…CH60: two more
  backgrounds and an animated dyno trace, four frames, three emblems, a title, four layouts), each locked until owned.
  Core `CardStyle` (canonical wire form; strict parse) and `CardStyleCatalogue.Problems`: unknown items, items not owned
  yet (by name), ISO 3166-1 alpha-2 regions (self-selected, shown as a code badge, not flag art) and a preferred car the
  player owns. Control plane: migration 0007 (`player_cards.style_json`, SQLite executed; PostgreSQL written, not run),
  `POST /v1/me/card` validates a style against the account's owned cosmetics and cars and stores it canonically; `/v1/me`
  and the public card carry it. Local profiles keep it in the card fields and `LocalProgression.SetCard` applies the same
  rules. Client: `CardView` draws the card (procedural backgrounds and motifs, six frame styles, five layouts); the
  Player Card screen has a Card style section (locked items marked) online and offline; at the meet a visitor's public
  card is drawn with its style beside the text panel.
- **Tests:** .NET `CardStyleTests` (all fifteen rewards map to exactly one item; the default needs nothing owned; problems
  named; canonical round trip; 249 region codes), `LocalCardStyleTests`, `PlayerApiTests` card-style test (reward refused
  until owned, then worn; region, car and unknown members refused; public card carries it; null returns to the default) —
  Builds 232, Core 129, Toys 92, Services 346.
- **Built players:** `-nsDriverCardTour` (offline): a locked frame refused as "Not owned yet: Balance Point Frame.", a free
  style saved, the profile re-read from disk with it, the preview drawing it — **PASS**. `meet-online.ps1 -Convoy`: the
  host saves Tea Rows / Double Rule / Lantern / Night Driver / JP / V01 through the screen; the guest, from the host's
  car at the convoy meet, sees the public card drawn with exactly that style — **PASS** on both clients. Screenshots
  `Evidence/meet/card-style-offline.jpg`, `card-style-seen-at-meet.jpg`.
- **Limits:** no account owns a card reward yet (their challenges — lessons, tuning demos — have no predicates), so the
  reward items are shown locked in the built runs and worn only in tests; showcase records are not done; the offline
  meet has no one to show a card to; the art is procedural and has not been reviewed by a person.

## V-093 — Showcase records on the Player Card (2026-09-28)
- Revision: `ee58844` (control plane) and the client work committed with this entry; player build of it; control plane
  restarted on `ee58844`.
- **What changed:** a player's personal records are derived from their settled receipts — the best finish per campaign
  stage and mode, per freeplay course and format (sprint, circuit, time attack) — and their Team Trial bests
  (`GET /v1/me/records`). The card keeps up to three chosen record keys (migration 0008 `player_cards.showcase_json`,
  SQLite executed, PostgreSQL written); each must be one of the player's own when saved; the public card shows them in
  the owner's order with their CURRENT values (a better time later shows at once). The Player Card's style section has
  three showcase slots (online); the styled card and the meet's public card list them.
- **Tests:** .NET `Card_Showcase_OwnRecordsOnly_PublicWithCurrentValues` (records derived from settled receipts, a DNF is
  no record; saved; unknown, duplicate or four keys refused; the public card in order; a better time shows at once;
  cleared) — Builds 232, Core 129, Toys 92, Services 347.
- **Built players** (`meet-online.ps1 -Convoy`): the host's account had 16 records from earlier settled runs; the tour
  put the first two in the showcase (C01 sprint 1:25.992, C01 time attack 1:25.957) and saved; the guest, at the convoy
  meet, saw both on the host's styled card and panel — **PASS** on both clients. (The first run picked the same record
  twice and the control plane refused it, "Choose up to 3 different records." — the tour was fixed.) Screenshot
  `Evidence/meet/card-showcase-seen-at-meet.jpg`.
- **Limits:** online only (the Local profile keeps its own records but its card has no showcase yet); Drift Attack scores
  are not records (receipts do not carry the drift score); the screen lets two slots name the same record and relies on
  the server to refuse it.

## V-094 — The meet under latency; pipelined poses (2026-09-28)
- Revision: `4340191` plus the work committed with this entry; player builds of it (loopback control plane, no game
  server).
- **What changed:** `-nsImpairControl delayMs,jitterMs` holds every control-channel message (both directions) for the
  delay ± jitter, keeping their order (a reliable stream: loss shows as delay, so nothing is dropped; seeded);
  `meet-online.ps1 -ImpairControl`. HTTP calls (card lookups) are not impaired. The meet's pose sender allowed ONE
  `meet.move` in flight, so a slow round trip halved the pose rate; it now pipelines up to three (the channel keeps
  their order; only the reply to the newest pose applies a correction).
- **Built players** (three clients, the public-meet tour: arrivals, walking, wave, quick chat, like, a boombox cue for
  everyone, a departure, a dropped connection):
  - 80 ± 20 ms each way (≈ 160–200 ms round trip), one-in-flight sender: **PASS** on all three; the host's walk to the
    boombox sent 283 poses, 0 corrections.
  - Same latency, pipelined sender: **PASS** on all three; 478 poses, 0 corrections.
  - No latency, pipelined sender (regression): **PASS** on all three; 472 poses, 0 corrections.
- **Limits:** latency only (no loss or reordering, which the reliable channel does not show); the look of remote
  motion under latency was not judged by a person; the race netcode's impairment (`-nsImpair`) is separate (V-054/V-061).

## V-095 — Drops, refused re-entry and spectating in a full impaired race; the offline showcase (2026-09-28)
- Revisions: the race ran on the player build of `3bd3b2c` (control plane on `ee58844`, race content hash `3677a844…`);
  the offline showcase is the work committed with this entry, on a player build of it.
- **Six humans under load** (`Tools/run/net-race.ps1`, C01 freeplay sprint, 6 scripted client processes + 6 AI, every
  client impaired 80 ± 20 ms each way with 1 % application-level loss, `-ResetAt 25 -DropAfterReset -DropAt 40`;
  loopback, the dedicated server bound to 127.0.0.1 — the clients' ephemeral UDP sockets are the known wildcard ones of
  `UnityTransport`, see `docs/EFFECTIVE_RULES.md` Addendum 04): client 0 (the convoy leader) asked for a recovery at 25 s, dropped its connection once
  the server completed it, came back 2 s later with the same ticket and was **refused** (`ticket_Replayed`), then
  spectated the rest of the event (4 target changes, 0 losses; the 40 inputs it forged as a spectator were ignored —
  the server counts 40 ignored inputs from non-entrants and 1 spectator served). Client 1 dropped at 40 s and stayed
  away. Both settled **DisqualifiedDisconnect**: no payout, wallet and RP unchanged, the slot not refilled (the server
  lists entrants 0 and 1 as `DqDisconnected`). Clients 2–5 finished (4th, 6th, 9th, 10th of 12), were credited
  (9,460 / 12,460 / 9,460 / 6,460 cr; RP up) with their challenge unlocks; round trip ≈ 190–204 ms, 81–84 inputs and
  27–29 snapshots dropped by the impairment each, 270–922 reconciliations. The large maximum corrections (36–90 m) on
  clients 3–5 are their own server recoveries (1–2 each), which move the car by design. All six clients **PASS**;
  `Evidence/net/run-20260928-131804-h6-C01-ai6-impair/` (client and server summaries, network sockets).
- **The offline showcase:** the Local profile's card keeps up to three of its own records (`LocalShowcase`: best time per
  campaign stage and difficulty and per freeplay course and format, best raw score per Drift Attack course — the online
  key form, never mixed with online records); `LocalProgression.SetCard` refuses more than three, repeats and records
  the profile does not hold, and is a no-op when nothing changed. The Player Card's three showcase slots now work
  offline and the card preview lists the chosen records.
- **Tests:** .NET `LocalShowcaseTests` (best kept per key, times and raw scores formatted, stored in order; foreign,
  repeated keys refused; repeat is a no-op) — Core 130/130; Unity EditMode 471 passed, 2 skipped (the explicit V-087 experiment), 0 failed.
- **Built player** (`-nsDriverCardTour`, buttons only, isolated profile folder): the new profile gets two seeded records
  (C01 sprint 2:29.000, C08 Drift Attack 71,250 raw — seeded, not raced; a raced record reaches a profile through
  `ApplyEvent`, covered by the progression tests); both are offered, chosen in slots 1 and 2, saved, drawn on the card
  ("Best: C01 Tea Lantern Road · sprint 2:29.000", "Best: C08 Rain Thread Pass · drift attack 71,250 raw") and still
  there after the profile is re-read from disk; the earlier look, pronoun and style steps still pass — **PASS**.
  `Evidence/meet/card-showcase-offline.jpg`, `Evidence/meet/driver-card-offline-log.txt`.
- **Limits:** the drops are scripted (a real crash and the Rejoin UI path were driven in V-060, with two clients); the
  six clients are scripted autopilots on one machine, not people; the offline meet has no one to show the card to.

## V-096 — Racecraft judged: CH31 Clean Pass and CH32 Patient Mirror; the gap to the car ahead on the HUD (2026-09-28)
- Revisions: `aec1794` (judge, predicates, HUD), `6afb1fe` (tour, pass log) and the tour's final setup committed with
  this entry; player builds of each; no control-plane change (it accepts any catalogue challenge a signed result carries).
- **What changed:** `RacecraftJudge` runs inside the race simulation (so on the game server and in the offline race alike)
  for events with live opponents — not Time Attack (non-contact) nor Drift Attack. It reads only the simulation's own
  facts: each car's legal race distance, every car-to-car touch (any contact, stricter than a meaningful incident) and
  every recovery. **A pass** is this car's distance going from behind a live, solid (not a reset ghost), moving (≥ 5 m/s)
  car to ahead of it in one fixed step with neither car recovering; it is **clean** when this car touched no car in the
  2 s before and the 2 s after and kept the place for 3 s (or finished ahead first). A recovery never makes a pass, and
  passing a stopped car is not racing it. **The interval** to the car directly ahead is the time since that car was
  where this one is now (from each car's recent distance history, cleared by a recovery); **a follow** is the same live,
  moving car ahead inside 1–2 s, broken by any touch or recovery; the longest follow is kept. CH31 = a clean pass then a
  finish (any race with live opponents; the master's "eligible six-slot race" read as such under Addendum 01's larger
  grid); CH32 = a follow of ≥ 8 s on C05 then a finish. The HUD shows "GAP AHEAD x.x s <driver>" under the position
  (highlighted inside 1–2 s): offline from the judge, online from the snapshot distances with the same interval rule.
- **Tests:** EditMode `RacecraftJudgeTests` (10: the interval rule; judged only with live opponents; a clean pass kept 3 s
  grants CH31; a touch 1 s before or 1.5 s after spoils it, one 2.5 s after does not; the place lost within 3 s; a
  recovery never makes a pass; a stopped car is not raced; 8 s inside the window grants CH32 on C05 only; a touch, 2.5 s
  or 0.75 s intervals break it; no facts, no racecraft challenges). Full EditMode suite 481 passed, 2 skipped (the explicit
  V-087 experiment), 0 failed.
- **Built player** (`-nsRacecraftTour`, the validator autopilot driving V07 on C05 against AI held to PI 300; automation,
  not a person): the follow race (five AI, 2.5 s hold, the autopilot told to keep ~1.5 s behind) followed Driver AI-3 for
  15.5 s inside the window and **CH32 was granted**; the HUD's gap line equalled the judge's interval in 259 of 259 samples.
  The pass race (seven AI, 8 s hold) made one pass, logged by the judge as "passed Driver AI-2 0.0 s after a touch — not
  clean" (7 car contacts), so CH31 was withheld; it also followed Driver AI-4 for 39.3 s, so CH32 was granted there too;
  the HUD equalled the judge in 276 of 276 samples. Every grant matched the facts — **PASS**.
  `Evidence/courses/racecraft/racecraft-tour.txt` (with the earlier runs), `follow-gap-ahead.jpg`.
- **Limits:** no built run shows a clean pass: the validator autopilot is a poor overtaker (it queues on C05's narrow
  road and its passes there are bumps; on C01, with quicker cars too, it crashed, reset and never got within reach of a
  car), so CH31's positive case rests on the EditMode tests; the online HUD line and the server's grants were not driven
  in a networked run this time (the judge is the same simulation code the server runs); no person has judged whether 1–2 s
  "feels" like following.

## V-097 — CH48 Sign Your Car at the meet; the offline meet draws the applied livery (2026-09-28)
- Revision: the work committed with this entry; player build of it; control plane restarted on it (MeetService changed;
  race content hash `3677a844…` and customization hash `306dd38e…` unchanged).
- **What changed:** Core `LiveryChallenges`: a livery is "signed" when it has at least one decal and two paint regions — a
  two-tone (lower, roof, hood or side stripe) whose second colour differs from the body colour (decals are owned or free
  by construction: the Garage refuses any other when it applies a livery). CH48 completes when the player inspects their
  own parked car at the meet and the livery the room holds for that car is signed — online the meet room judges the
  server's copy (the Garage's applied livery), offline the front end judges the Local profile's applied livery; granted
  once, with its cash, RP and COS-CH48, like the touring challenges (the Local profile now accepts CH48 at the meet and
  still refuses any other workshop challenge there).
- **Bug fixed:** the offline meet read the car's applied livery (wire form) as canonical JSON, which never parsed, so the
  player's car at the offline meet always showed its stock paint whatever livery was applied (V-089 fixed only the
  online meet). It now decodes the wire form (stock when none is applied).
- **Tests:** .NET `LiveryChallengesTests` (signed needs a decal and a real two-tone — stock, no decal, one region or a
  two-tone in the body colour are not; the wire form judges the same; the Local profile accepts CH48 but not CH46) —
  Core 133/133; Services `SignYourCar_CH48_WhenTheParkedCarWearsASignedLivery` (a livery applied in the Garage, the own
  car inspected at the meet over the control channel: CH61 and CH48 for the signed car, CH61 only for a stock one, once) —
  Services 348, Builds 232, Toys 92; Unity EditMode 481 passed, 2 skipped (explicit), 0 failed.
- **Built player** (`-nsSignYourCarTour`, offline, buttons only, isolated profile folder): in Appearance a lower two-tone
  (#E8E4D8 / #111111) and a stripe decal applied → the offline meet hands the parked car exactly that livery (two-tone
  lower, 1 decal; before the fix it would have been stock) → own car inspected → CH61 and CH48 granted, COS-CH48 owned,
  still there after the profile is re-read from disk — **PASS**. `Evidence/meet/sign-your-car-offline-log.txt`,
  `sign-your-car-offline.jpg`.
- **Limits:** the tour checks the appearance handed to the parked car's view, not its pixels (the screenshot's angle does
  not show the decal); the online path is covered by the service test, not a built networked run.

## V-098 — CH50 Change Without Losing: two presets, switched, the first restored exactly (2026-09-28)
- Revision: the work committed with this entry; player build of it; control plane restarted on it (GarageService changed;
  race content hash `3677a844…`, customization `306dd38e…` unchanged).
- **What changed:** Core `LiveryChallenges.RestoresFirstPreset`: an apply that takes the car from one saved visual preset
  to a different-looking preset saved before it, where the applied livery is exactly that earlier preset's saved look.
  That is the challenge's "create two visual presets, switch between them and restore the first exactly"; a switch
  forward, an apply from an edited livery, a different look or a deleted preset do not count. The server judges it in
  `livery-apply` after the new revision is saved and grants CH50 once (cash, RP, cosmetic, ledgered), returning it with
  that revision; the Local Garage makes the same check and grants it on the profile (`LocalProgression.CompleteGarageChallenge`,
  CH50 only). "Meet preview": the meet shows the applied livery (V-089 online, V-097 offline), so no further step is
  asked. The Appearance screen now shows a completed challenge after its own confirmation, online and offline.
- **Tests:** .NET `LiveryChallengesTests` (the rule's six cases; the Local profile takes CH50 in the Garage only) — Core
  135; Services `ChangeWithoutLosing_CH50_TwoPresetsSwitched_TheFirstRestoredExactly_Once` (an edited livery then the
  first preset: nothing; first → second: nothing; second → first exactly: CH50 with revision 7 and cash; once only) —
  Services 349, Builds 232, Toys 92; Unity EditMode 481 passed, 2 skipped (explicit), 0 failed.
- **Built player** (`-nsWorkshopTour` — the V-097 tour extended and renamed; offline, buttons only): look A applied, saved
  as preset 1; the colour changed and saved as preset 2 and applied (no CH50); preset 1 loaded and applied → the applied
  livery equals A exactly, the screen says "Challenge complete · Change Without Losing · +3,000 cr."; then CH48 and CH61
  at the offline meet as in V-097; both still there after the profile is re-read from disk — **PASS**.
  `Evidence/ui/appearance/workshop-tour.txt`, `workshop-ch50-first-preset-restored.jpg`.
- **Limits:** the online path is covered by the service test, not a built networked run.

## V-099 — The campaign story on screen, offline: stage intros, reactions, the race diary (2026-09-28)
- Revision: the work committed with this entry; player build of it. No control-plane change; the race content hash is
  unchanged (`3677a844…`: the story documents are presentation text outside it).
- **What changed:** the authored story (stages.story, crews.diary, radio-records, rivals.story — written long ago, never
  shown: R5.3 said "presentation not built") is now in the content library and on screen for a Local profile. Core
  `StoryText` (engine-free): each stage's intro for Normal and Hard; on a rematch (the profile has raced that stage in
  that mode) only the opening and closing lines, the full scene staying in the diary; the reaction for the outcome read
  from the convoy's side — win, loss, cleared-but-lost, and the fourth case (rival beaten on track, benchmark missed),
  which has no stage line and uses the featured rival's own "loss" line as the story conventions say; {player}/{convoy}
  filled; scene pacing: the scene's length at about 22 characters a second held to the 10–18 s target, shared between
  its lines (at least 1.8 s each; a rematch has no 10 s floor). Front end: before an offline campaign race the intro
  plays by itself with Next and Skip (skipping is local and only starts this player's own race); the results show the
  reaction; the offline hub has a Race Diary — the entry of every cleared stage (Normal, then Hard), the crew
  introductions their clears unlock, and the radio and timing-slip records collected through Normal progression.
- **Tests:** .NET `StoryTextTests` (every stage has both sides' title, intro, the three reactions and a diary entry, all
  spoken by a known rival or the three narrators; 4 acts, 6 crews, 6 records; the rematch intro is the first and last
  line; each outcome's reaction, the fourth from R01's own line; placeholders; the diary for nothing, S01, S01–S06 plus
  S01 Hard, and everything (60 + 6 + 6); every one of the 60 full intros paces to 10–18.5 s and every rematch is shorter
  and at most 10 s) — Core 140; SharedCore builds; Unity EditMode 481 passed, 2 skipped (explicit), 0 failed.
- **Built player** (`-nsStoryTour`, offline, buttons only, isolated profile folder): the diary empty at first; S01's
  intro — five lines, played by itself in 18.0 s; the validator autopilot won S01 (first clear) and the results showed
  Sora Matsuda's win line, matching the verdict; the diary then held S01 and the Tea Hour introduction, readable; S01
  again: the two-line rematch intro, ended at once by Skip, and the race started — **PASS**. A first run measured the
  intro at 27.5 s (each line held by its own length); the scene is now paced as a whole. `Evidence/ui/story/`
  (`story-tour.txt`, intro, reaction, diary and rematch screenshots).
- **Limits:** offline only — online, the intro during loading with the finite window and synchronized start barrier
  (spec §5.3), the reaction on online results and the diary from the account's clears are not built; no crew
  introduction "read" tracking yet (CH70); no portraits or staged 3D scene (text over the menu backdrop); the pacing and
  the words have not been judged by a person.

## V-100 — The campaign story online: the intro inside the loading barrier, the reaction on the receipt, the diary (2026-09-28)
- Revision: the work committed with this entry; player build of it; control plane unchanged (running the V-098 code).
- **What changed:** `RaceClient.Presentation`: after the course has loaded and before the client reports itself loaded,
  the front end plays the stage intro over the course (reporting 0.9 — real progress — meanwhile). The server's existing
  loading barrier therefore starts the countdown only when every driver has finished or skipped their intro: a skip is
  local and leads to the race HUD's "waiting for all drivers" view, and the window is finite (a paced scene is at most
  ~18.5 s, inside the 90 s loading window). A rematch online — the account has seen that intro on this PC (a presentation
  preference, never progression) — gets the short version. When the settled receipt arrives, its stage verdict
  (earned clear, featured rival, beaten or not) picks the reaction, shown with the result on the convoy screen. The
  convoy screen has a Race Diary built from the clears the server reports (/v1/me).
- **Built players** (`Tools/run/ui-tour-online.ps1`, campaign event, one windowed client driving the real menus against a
  loopback dedicated server): the event was S12 Normal; the client log shows phase Loading, then the six-line intro
  ending by itself after 18.0 s, then phase Countdown about 20 s after loading began — the barrier waited — then the race
  (P1 of 4, stage cleared, first clear) and the result carrying Michi Kagawa's win line from the receipt's verdict; the
  tour **PASS**. `Evidence/ui/story/story-online.txt`.
- **Limits:** one human in that run (the barrier across several readers is the same server code the multi-client runs
  exercise, not re-run with intros); the online diary was not driven in a built run; Hard intros and the other outcomes
  online come from the same Core rules the .NET tests cover.

## V-101 — The endings; Shiori's epilogue at the radio bench (CH75) (2026-09-28)
- Revision: the work committed with this entry; player build of it; control plane restarted on it (MeetService: the
  epilogue act); race content hash unchanged (`3677a844…`).
- **What changed:** the two authored endings (endings.json) are presented. After the FIRST clear of S30 in a mode, the
  results' Continue plays the ending before returning — Normal: "The Terrace After Amanagi", its three terrace scenes
  (arrival, the timing board with Reina, the radio bench), then the post-game note; Hard: "Before the First Train", its
  dawn-run scene at the finish, then the note. Online the same plays on each client when its settled receipt shows the
  first finale clear. Each scene opens with its setting and is paced like an intro; Next and Skip as always. Hard's other
  two scenes are the epilogue at the terrace (spec: "Hard ends with the dawn-run story resolved and Shiori at the radio
  bench"): once the Hard finale is cleared, Shiori Kuze waits beside the radio bench at the meet (offline from the Local
  profile, online from /v1/me), and reading her epilogue to its end — not closing it early — is a new touring act. The
  room grants CH75 only near the bench and only when the account's Hard S30 clear is on record ("no_finale" otherwise);
  offline the Local profile applies the same two rules.
- **Tests:** .NET `StoryTextTests.Endings_AfterTheFinale_AndTheRadioBenchEpilogue` (3 + 3 scenes with settings and known
  speakers; the note; what plays after each finale; the epilogue is the two radio-bench scenes, Shiori in both) — Core 141;
  Services `Epilogue_CH75_AtTheRadioBench_OnlyAfterTheHardFinale` (from the bay: not_here; at the bench before the
  finale: no_finale; with a Hard S30 clear recorded: CH75, once) — Services 350, Builds 232, Toys 92; Unity EditMode 481 passed, 2
  skipped (explicit), 0 failed.
- **Built player** (`-nsEndingTour`, offline, buttons only): the Normal ending paged with Next — 21 pages (three scenes
  with their settings and the note), read to the end; the Hard finish scene played by itself (8 pages, 33.6 s including
  the note); then, with the Hard finale SEEDED as cleared on the tour's own profile, the offline meet put Shiori at the
  bench, her epilogue (14 pages) read to its end granted CH75 — **PASS**. `Evidence/ui/story/ending-tour.txt`, ending and
  epilogue screenshots.
- **Limits:** no built run clears S30 for real (the validator autopilot is not relied on to beat the finale; the trigger
  is the same call, and the first-clear flag comes from Core's settled verdict); the tour stands the player at the bench
  (a person walks there); the online epilogue is covered by the service test, not a built networked run; text over the
  backdrop, not a staged scene; the words and pacing have not been judged by a person.

## V-102 — CH70 The Other Side of the Card: the race diary's read marks (2026-09-28)
- Revision: the work committed with this entry; player build of it; control plane restarted on it (diary endpoints,
  settlement); migration 0009 (`diary_reads`; SQLite executed by the service tests and the running control plane's
  startup, PostgreSQL and its RLS written, not run); race content hash unchanged (`3677a844…`).
- **What changed:** opening a crew introduction in the race diary marks it read — offline on the Local profile
  (`LocalProfile.DiaryRead`, validated), online through `POST /v1/me/diary/read` (`GET /v1/me/diary` lists them). An
  introduction counts only once it is open: its crew's stage cleared on Normal (the encounter), checked by the server
  from the account's clears and by the Local rules from the profile. Core `DiaryChallenges`: once all six are read, the
  next legal finish in a race with a crew member among the opponents completes CH70 — in settlement (the caller passes
  the finishers whose six marks are recorded; the check reads the allocation's AI list) and in the Local race facts.
  The control plane now ships crews.diary.json (presentation text, outside the hash). The diary shows "read" beside
  crew introductions.
- **Tests:** .NET Core `DiaryChallenge_CH70_AllSixRead_ThenACrewMemberRaced` and
  `LocalProfile_MarksACrewIntroductionRead_OnlyOnceOpened` — Core 143; Services `DiaryTests` (the endpoint: closed before
  S01's Normal clear 409, unknown 400, recorded once, listed, a later crew still closed; settlement: CH70 with all six
  read and R01 in the field, not with generic AI, not without the six) — Services 352, Builds 232, Toys 92; Unity
  EditMode 481 passed, 2 skipped (explicit), 0 failed.
- **Built player** (`-nsDiaryTour`, offline, buttons only): Normal S01–S24 SEEDED as cleared on the tour's profile; the
  diary held 35 entries, the six crew introductions at rows 24–29; each opened with its row button and marked read; S01
  replayed by the validator autopilot (R01 in the field) and the finish completed CH70 — **PASS**.
  `Evidence/ui/story/diary-tour.txt`, diary and results screenshots.
- **Limits:** the online diary marks are covered by the service tests, not a built networked run; the crew clears were
  seeded, not raced.

## V-103 — Ghosts, offline: recorded runs, the personal ghost in Time Attack, checkpoint deltas; CH68 (2026-09-28)
- Revision: `24c17d4` (Core) and the work committed with this entry; player build of it. No control-plane change.
- **What changed:** R8.2 was "not started". Core `GhostRecording` (night-signal/ghost@1): sampled transforms (10 Hz:
  position, rotation, speed) and the race time at each checkpoint — a replay, never an input stream to re-simulate —
  with the header spec §8 lists (course id and revision, direction, format, surface, physics and scoring versions, game
  version, car model, build hash, class, PI, assists, raw result, resets, corridor legality, provenance). A ghost is a
  personal target only for a legal finish with no reset, and compares only under the same course revision, direction,
  format, surface and physics/scoring rules (otherwise it would be reference-only). Offline every Local run is recorded
  (`GhostRecorder`); the best valid run per course and format is kept beside the profile (provenance local-simulation:
  never uploaded as a trusted record); in Time Attack it runs on the road as a translucent, non-colliding car placed by
  interpolation (`GhostPlayback`, at most three overlays, only compatible ones), listed as REPLAY in the standings; at
  each checkpoint the HUD shows the time against it, and the results say whether the run became the ghost and the delta
  at the first checkpoint and the finish. CH68 "Chasing Your Yesterday": a valid C07 ghost beaten by at least a second
  under the same rules.
- **Tests:** .NET `GhostTests` (round trip; malformed and foreign documents refused; a reset run is no target; a physics
  change makes a ghost incompatible; interpolation index and sector deltas; CH68's five cases) — Core 147; Unity EditMode
  481 passed, 2 skipped (explicit), 0 failed.
- **Built player** (`-nsGhostTour`, offline, buttons only; C07 ownership SEEDED on the tour's profile; the validator
  autopilot drives, varied only by the automation-only start hold): run 1 (3 s hold) 124.413 s, no ghost to race — the
  first ghost (1,246 samples); run 2 (no hold) raced it: 120.012 s, ahead at every checkpoint (−4.68 s at the first,
  −4.40 s at the finish), the ghost replaced and CH68 granted; run 3 (2 s hold) raced the new ghost, visible ahead on the
  road, +2.92 s at the first checkpoint and +2.85 s at the finish, the stored ghost still the 120.012 s run — **PASS**.
  `Evidence/ghosts/` (tour log, the ghost ahead, the results note). A stored C07 ghost is about 80 KB.
- **Limits:** offline and personal only — server-generated, validated online ghosts, a convoy member's shared ghost, an
  authored rival reference and the post-race route/elevation chart are not built; the ghost is drawn with one translucent
  material (no livery); ghosts race in Time Attack only; nobody has judged the look.

## V-104 — Ghosts online: server-generated, kept at settlement, raced in online Time Attack; CH68 online (2026-09-28)
- Revision: the work committed with this entry; player build of it (client and dedicated server); control plane restarted
  on it; migration 0010 (`match_ghosts`, `ghosts`; SQLite executed by the service tests and the running control plane,
  PostgreSQL and its RLS written, not run); race content hash unchanged (`3677a844…`).
- **What changed:** the dedicated game server records every human's run from its authoritative simulation (spec §8:
  "server-generated/validated replay samples") and posts each ghost, signed with the match secret like the results,
  before the results (`POST /v1/matches/{id}/ghosts/{account}`, ≤ 400 KB: a well-formed ghost of an entrant, for the
  match's course, while the match is open). At settlement a ghost is kept only if it is a valid personal ghost whose
  result equals that entrant's settled finish, as the account's best per course, format and ruleset (course revision,
  direction, surface, physics and scoring versions); CH68 is judged against the kept C07 ghost there. Players read their
  kept ghosts (`GET /v1/me/ghosts/{course}/{format}`); online Time Attack fetches them before reporting loaded and races
  the compatible ones (≤ 3) as overlays at this client's race time, with the same REPLAY entry and checkpoint deltas as
  offline (deltas from the server's checkpoint reports, display only).
- **Tests:** Services — the end-to-end test now sends ghosts before the results: a bad signature 401, another course 422,
  a non-entrant 422, A's accepted and kept after settlement (fetched: one ghost, 96 s), B's not kept (it does not match
  B's settled 113 s), a late ghost 409; `GhostSettlementTests` (CH68 only for the human whose ghost was beaten) —
  Services 353, Core 147, Builds 232, Toys 92; Unity EditMode 481 passed, 2 skipped (explicit), 0 failed.
- **Built players** (`ui-tour-online.ps1 -Intent 4 -Course C07`, twice; one windowed client through the real menus, the
  validator autopilot, a loopback dedicated server): run 1 — no kept ghost to race; the server sent a 79 KB ghost
  (1,207 samples, 120.601 s) and settlement kept it; run 2 — the client fetched 1 kept ghost and raced it; 119.534 s,
  1.07 s faster: the new ghost kept and the receipt credits `challenge:CH68` (8,000 cr); both tours **PASS**.
  `Evidence/ghosts/ghost-online.txt`.
- **Limits:** a convoy member's shared ghost and an authored rival reference are not served yet (the kept ghosts are
  read by their owner only); no post-race route/elevation chart; the online checkpoint deltas are as late as the server's
  snapshots; the autopilot's second run was faster by chance of network timing, not by design.

## V-105 — The course-uniqueness validator and report (spec §14) (2026-09-28)
- Revision: the work committed with this entry; executed in the editor on the routes as committed.
- **What changed:** R14.2 was "not started". Core `CourseUniqueness`: every counted course (the 24 regular stages, the
  finale and the three Freeplay additions — not the tutorial) is compared with every other in the frame it is authored
  in (each course is its own scene starting at its grid, so overlap means a near-copy of another course's shape): a
  centreline point is shared when another course passes within 8 m; the first and last 100 m are the allowed approaches;
  at least 70% must be exclusive. The editor report (Night Signal > Content > Course Uniqueness Report) runs it on the
  generated centrelines (every 4 m) and writes `Evidence/courses/uniqueness-report.txt`.
- **Result:** 28 counted courses, none flagged; the lowest are FP01 72.8% (closest FP03), C14 76.1% (FP01), FP03 78.4%
  (FP01); every regular course except C14 is at least 89% exclusive.
- **Tests:** .NET `CourseUniquenessTests` (a copy fails, a distinct course passes, a crossing costs little, a shared
  approach is allowed, a course sharing half its road fails; which kinds count) — Core 148.
- **Limits:** a geometric shape check with an 8 m tolerance, not a judgement of how different two courses feel; FP01,
  C14 and FP03 sit closest to the floor and are worth a visual review.

## V-106 — Crew behaviour telemetry: at least four measured differences per crew (spec §13) (2026-09-28)
- Revision: the work committed with this entry; built player (`BuildGame`, succeeded, errors 0) from this working tree.
- **What changed:** R13.2 was "not started", and the 19 tendencies were only speed-plan biases (corner speed, braking,
  line aggression, lookahead), so crew members mostly differed in pace alone. `DriverProfile` gains four behaviours
  (all 0 = the neutral line, which the validator autopilot keeps): `ApexShift` (the apex moved along the road, m),
  `EntryWidth` (an outside-in swing before turn-in), `ThrottleBias` (sooner/gentler power when the plan allows speed),
  `BrakeGain` (how sharply braking comes on), plus a small exit-speed allowance for a late apex. `AiProfiles` maps each
  tendency to a combination (late-brake-anchor: harder, later braking; exit-traction-specialist: slower entry, later
  apex, earlier power; wide-entry-specialist: outside-in; power-conserver: gentle throttle; early-set-cornerer: early
  apex; inside-line-defender: tight entry; …). The same controller runs offline and on the dedicated server.
- **Measurement** (`-nsCrewTelemetryTour`, built player, automation): each crew's members race C01 together as
  non-colliding calibration ghosts, every one in V01 (so the car is not the difference); traces per metre; at the
  three sharpest corners (945, 1,185, 2,135 m) the brake point, minimum speed, exit speed (apex + 60 m), throttle-on
  point and the line at turn-in and apex; a measure counts when its spread across the crew exceeds a noise threshold
  (brake 8 m, speeds 2 km/h, throttle 8 m, lines 0.5 m). `Evidence/ai/crew-telemetry.txt`.
- **Result: PASS** — distinct outcomes per crew: breakwater 5, datum 5, rainline 4, reservoir 6, tea-hour 5,
  zero-frequency 6 (all 46 crew members measured). The spec's named expectations, reported and not forced: the late
  braker brakes later than its crew median in all three crews that have one (139 m vs 148–150 m before the apex); the
  exit specialist is slower at the apex in every crew (95.4–95.5 vs 97.4–97.5 km/h) but gains more speed out of the
  corner than the crew median only in datum (15.7 vs 15.7) — **not observed** in reservoir (15.7 vs 15.8) or tea-hour
  (15.8 vs 16.4): V01 at ~100 km/h is power-limited on exit, so earlier power does not show; the exit-speed allowance
  changed nothing measurable (same verdicts before and after it).
- **Before:** the first run of the tour on the speed-plan biases alone failed three crews (fewer than four distinct
  outcomes); that run is what added the four behaviours.
- **Tests:** Unity EditMode 481 passed, 2 skipped (explicit), 0 failed (the controller has no dedicated unit test — its
  evidence is the built-player telemetry).
- **Limits:** one course, one car, solo (ghosted) laps, means over three corners — not racecraft in traffic; the exit
  trade is visible in the apex speed, not the exit. **The certified featured-rival paces (V-065/V-066) were bisected on
  the rivals' earlier behaviour** and must be recertified (the reference times P come from the neutral autopilot and
  are unaffected).

## V-107 — A convoy member's shared ghost (spec §8) (2026-09-28)
- Revision: the work committed with this entry (server side; the client chooser and a two-client built-player run
  follow in the next checkpoint, after the benchmark recertification frees the editor).
- **Server:** `GET /v1/convoy/ghosts/{member}/{course}/{format}` returns that member's kept (server-settled) ghosts with
  their owner — only while both accounts are active members of the same convoy (`ConvoyDirectory.SameConvoy`) and
  neither has blocked the other; otherwise 403 `not_in_convoy` (your own id always works). Nothing new is stored: the
  shared ghost is the member's kept best, already validated at settlement (V-104).
- **Tests:** Services end-to-end — B (in A's convoy) reads A's kept 96 s ghost with its owner; the outsider gets 403; A
  blocks B → 403; A unblocks → 200. Full suite from the normal output: Services 353, Core 148, Builds 232, Toys 92.
- **Control plane restarted** for this server change (stopped with its task, tests run, started again as a tracked
  task; health ok, content hashes unchanged `3677a844…`/`306dd38e…`; the new route answers 401 without a token).
- **Note:** `MeetControlChannelTests.PublicMeet_ArriveWalkEmoteChatLike_ThenLeaveFadesOut` failed once in a run built to
  a scratch folder and passed in the two other runs — timing-sensitive; watched.

## V-108 — Benchmarks recertified after the V-106 tendency behaviours (2026-09-28)
- Revision: `2274410` (the editor on that tree; one Unity writer). `BenchmarkCertificationTests.CertifyHard` +
  `CertifyNormal` in PlayMode, **2/2 passed in 55.8 min**; per-stage evidence `Evidence/progression/benchmarks/{N,H}-S*.json`
  and `stage-benchmarks.json`. Automation with legal inputs, not human runs.
- **Reference times unchanged:** every P and every target is identical to V-066 (the reference runs use the neutral
  autopilot profile, which the tendency behaviours leave untouched) — 0 of 60 targets moved.
- **Featured-rival paces:** Normal 13 of 30 moved (0.59–0.80 of the stage profile as before; e.g. S01 R01 0.727 → 0.719,
  S27 R44 0.727 → 0.754), every rival 0–1 % above its target. Hard 15 of 30 moved; S13 R17 and S15 R29, which could not
  reach their targets even at full pace before, now calibrate at 0.81 and 0.86; two still cannot at full pace: S04 R01
  (142.9 s vs 140.4 s; 142.7 s before) and **S30 R48 (309.4 s vs 284.7 s — 291.7 s before: 17.6 s slower)**.
- **Finding:** R48 (the Hard finale rival) is a momentum-reader, to which V-106 gave an earlier apex (`ApexShift` −4 m);
  the other momentum-reader, R01, lost only 0.2 s on H S04, so C25 under the Hard finale's conditions punishes the
  earlier line. Being investigated before the file becomes authored content (next checkpoint).
- **Fix (this checkpoint):** the momentum-reader no longer shifts its apex (an early apex costs exit speed — the opposite
  of carrying momentum, and R48's authored strength is "extraordinary legal speed preservation"). Only R01 and R48 carry
  that tendency, so only their stages were recertified with the new `CertifyListed` (the stage sides listed in
  `Temp/ns-certify-stages.txt`, merged into the file; 1/1 passed in 104 s): **N S01 R01 0.727 → 102.4 s, H S04 R01 at full
  pace 142.7 s, H S30 R48 at full pace 291.741 s — exactly the V-066 values**, which confirms the cause.
- **Authored content:** the certified file is now `Assets/Content/Data/authored/stage-benchmarks.json`: race content hash
  `3677a844…` → **`26709731…`**. Hard: 28 of 30 featured rivals land 0–1 % above their targets, S04 R01 and S30 R48 cannot
  reach them even at full pace (4 before: S13 and S15 now can).
- **Tests:** EditMode 481 passed, 2 skipped (the explicit contact experiment); `CertifiedBenchmarkTests` failed once only
  because the editor's content library kept its catalogue (and hash) cached across the file change — 2/2 after a script
  reload. .NET (control plane stopped): Services 353, Core 148, Builds 232, Toys 92.
- **Control plane restarted** for the content change (tracked task; health ok, race content `26709731…`, customization
  `306dd38e…` unchanged).
- **Follow-up:** the V-106 crew telemetry was measured with the momentum-reader's apex shift; it is rerun with the next
  player build (tea-hour's R01 is the only crew member affected).

## V-109 — Three ghost sources and Freeplay rival archetypes (CH38, CH73) (2026-09-28)
- Revision: code and editor tests at `24f4e2a`; the fixes and the built-player tours below in the checkpoint after it.
- **Convoy member's ghost (client of V-107):** the convoy screen's "Chase ghost" row (Freeplay Time Attack, each member
  for themself: your best only, or your best + a member's); the race client labels every overlay by whose it is and
  tints them (your best cyan, a member's amber, a third violet); `ui-tour-social.ps1 -GhostChase`.
- **Authored rival reference (spec §8):** Core `RivalReference` — the Normal lead of the first stage on the course (a
  finale-only rival is never a replay target, so C25 takes S30's first support R33), the three Freeplay venues the four
  lieutenants in order, T00 none. The explicit PlayMode recorder `RivalReferenceGhostTests` drove each rival alone under
  Time Attack rules and wrote `Assets/Content/Resources/RivalGhosts/<course>.json` (28 courses, 3.1 MB):
  `Evidence/ghosts/rival-references.txt` — all finished and well formed; **C23's reference (R43) contains one reset**
  (239.4 s, marked not clean, kept and stated). Offline and online Time Attack race it beside the personal (and member's)
  ghost, labelled "<rival>'s reference" in a red tint; an incompatible reference stays off the road like any ghost.
- **CH38 Three Different Rivals / CH73 Twelve Different Voices:** Core `ArchetypeChallenges` (archetype = a rival's
  tendency, 19 exist): CH73 = legal Freeplay finishes against 12 distinct archetypes (every authored rival in the field
  counts); CH38 = three Freeplay wins against three different lead archetypes (the lead = the field's first AI, the
  named pick when made) with no race quit between — a loss keeps them, an explicit quit clears them, a disconnect does
  not. Online: settlement replays the account's settled Freeplay races (`FreeplayRacesAsync`, from receipts and the
  frozen match configs — nothing new stored) plus this race; `GET /v1/me/archetypes`; the host names the lead rival
  (`aiRivals`, which the server already validated). Offline: Freeplay now fields authored rivals as online (the named
  pick first, then a shuffled pool without the finale-only rivals; it was anonymous `ai-N`); the profile keeps the two
  sets; a "Lead rival" row and a progress line on both Freeplay screens.
- **Tests:** Core `ArchetypeChallengesTests` (7), `RivalReferenceTests`; Services `ArchetypeSettlementTests` (grant,
  quit, loss, CH73; the store reads settled Freeplay races in order, skipping campaign, aborted and AI-free matches);
  EditMode `LocalFreeplayFieldTests`. Services 356, Core 156, Builds 232, Toys 92; EditMode 483 passed, 2 skipped.
- **Control plane restarted** for the CH38/CH73 server code (tracked task; health ok, race content `26709731…`; the new
  route answers 401 without a token).
- **Process note:** while the certification ran, a patch script meant for a scratch copy ran against the checkout (a
  path substitution failed); it was reverted within a minute with `git checkout` of those files (all clean at HEAD) and
  removal of the new ones — no import or compile happened (assemblies and console unchanged), and the certification
  continued. The copy scripts now refuse to run unless they point away from the checkout.
- **Built players** (the player build of this checkpoint; content `26709731…`; automation with the validator autopilot):
  - `-nsFreeplayRivalTour` (isolated profile, buttons only) **PASS**: C01, one opponent named on the Lead rival row —
    the field led with R08 (late-brake-anchor); the autopilot won, recording late-brake-anchor as raced (CH73 1/12) and
    beaten (CH38 1/3), shown on the hub's progress line; then Time Attack with Sora Matsuda's (R01) reference on the
    road: 86.501 s against its 87.025 s, 0.52 s ahead at the finish. `Evidence/ui/freeplay-rival/`.
  - The first run of that tour exposed two faults, both fixed before the passing run: the Freeplay rows ran off the
    bottom of the offline hub at 1080p (Freeplay now has its own panel), and ghost overlays ignored their colour (the
    ghost paint replaced it; `GhostPlayback` now takes a tint: your best cyan, a member's amber, a reference red).
  - `-nsGhostTour` (C07, personal ghosts) **PASS** with Airi Shiba's (R11) reference as a second overlay: 124.413 →
    120.012 s (CH68) → 122.863 s; the stored ghost is the fastest. `Evidence/ghosts/ghost-tour-with-reference.txt`.
  - `-nsCrewTelemetryTour` **PASS** after the V-108 momentum-reader change: only R01's row moved (tea-hour still 5
    distinct outcomes); every other row identical. `Evidence/ai/crew-telemetry.txt`.
  - `ui-tour-social.ps1 -GhostChase` (two windowed clients, a loopback dedicated server) **PASS**: the guest chose "your
    best + Driver 1's" on the convoy screen; `GET /v1/convoy/ghosts/…` answered 200 with the host's kept 119.535 s C07
    ghost; the guest raced it and R11's reference; the host raced its own best and the reference; both settled.
    `Evidence/ghosts/convoy/`.
- **Limits:** CH38 needs three wins, CH73 twelve archetypes — the tours show one of each recorded, the rest is covered by
  the Core/Services tests; the convoy ghost is shared only while both ride in the same convoy (no friends' archive);
  the rival references are recorded AI runs (C23's includes a reset) and must be re-recorded after a physics, scoring or
  route change; no post-race route/elevation chart yet.

## V-110 — The post-race route/elevation chart (spec §8), offline (2026-09-28)
- Revision: the work committed with this entry; player build of that tree.
- **What changed:** Core `RouteChart` from the run's own recording (the Local ghost every run records): the route in
  plan, distance and elevation, braking points (a speed peak followed by a ≥ 4 m/s drop within 1.5 s, at least 60 m
  apart) and, against the first ghost on the road, the time each sector (checkpoint to checkpoint) gained or lost from
  the cumulative checkpoint deltas, with a one-line summary. The offline Results page offers it with a "Route Chart"
  button (and "Classification" back): `RouteChartTexture` draws the route coloured per sector (red lost, cyan gained,
  grey without a reference), white braking points, start and finish, and the elevation profile with sector lines and
  braking ticks. Presentation only — never a record or a result.
- **Tests:** Core `RouteChartTests` (distance/elevation/bounds, the braking point is the peak before the drop, sector
  times from the cumulative deltas, the summary with and without a reference) — Core 160; EditMode 483 passed, 2
  skipped.
- **Built player:** `-nsFreeplayRivalTour` now opens the chart after its Time Attack — **PASS**: "vs Sora Matsuda's
  reference 87.025 s: most lost in sector 2 (+0.03 s, 105–204 m); most gained in sector 1 (−0.18 s) · 4 braking points".
  `Evidence/ui/freeplay-rival/06-route-chart.png`. The first build sized the chart to the whole panel (an aspect
  fitter), covering the reward column; fixed before this run.
- **Limits:** offline only — online results have no client recording of the player's run yet; sector colours need a
  reference ghost (without one the route is grey); braking points come from speed, not the brake input.

## V-111 — The T00 Driving School (spec §16), and the crew telemetry measured at the true apexes (2026-09-28)
- Revision: the work committed with this entry; player builds of that tree (the last one differs only in writing an empty
  lesson list out of saves).
- **What changed (R16.2 was "not started"):** `authored/tutorial/lessons.json` (night-signal/tutorial-lessons@1; loaded
  by the content library, not part of the race content hash): **8 drive lessons** on the T00 loop — controls and camera,
  braking, turn-in and exits, grip versus drift, countersteering, exits and gearing, resets and checkpoints, ghost deltas
  — and **6 knowledge cards** — parts and stats, raw versus showcase score, convoy readiness, public meets, connectivity
  and disqualification, rewards and rank — each with goal, help text and keywords (help text checked against the real
  bindings). Core `TutorialLessons` (parse, validation, the searchable help index) and `LessonJudge` (engine-free: reach,
  brake, bend, drift, catch, timed, reset, lap — from distance, speed, brake, slip, walls, resets, camera changes and
  drift per tick, with a live feedback line and a Passed / Not yet verdict). The **Driving School** screen (offline hub):
  every lesson with its state, a search box over the help index, "Try the lesson" and "Watch the demonstration" (the
  autopilot drives it, marked "DEMONSTRATION … training aid"; drift lessons give it a drift skill; never counted),
  card questions with feedback; a lesson runs alone and non-contact on T00 with a banner over the race, ends once
  judged and returns to the school for a local retry. Passing is training progress in the Local profile
  (`MarkLessonPassed`: no money, rank or unlock; nothing gates a race). The ghost-deltas lesson races the **instructor's
  demonstration lap** (the validator autopilot's clean T00 lap, 128.858 s, recorded by `RecordInstructorLap` into
  Resources/LessonGhosts). `OfflineRaceSession` gained the camera-change count, the last input, a reset hold (automation)
  and `EndNow`.
- **Found while building it:** race distance counts from the start line, while route features (gates, lesson stretches)
  are in route metres — the lesson judge adds the course's start offset. **The V-106 crew telemetry had the same fault**:
  its corners were found along the route but read from traces indexed by race distance, so every measurement sat 64 m
  (C01's start offset) past the apex. Rerun at the true apexes (944, 1184, 2134 m): **PASS** — 4–5 distinct outcomes
  per crew; the late braker observed 3/3 and **the exit specialist now observed 3/3** (1/3 before). The exit "gain" is
  negative for every driver at these points (still slowing 60 m past the sharpest heading change on C01's long corners)
  — a like-for-like comparison, stated as measured. `Evidence/ai/crew-telemetry.txt`.
- **Tests:** Core `TutorialLessonsTests` (the document covers the spec topics and parses; the help index; broken
  documents refused; each judge type passes and fails on its own condition; cards; progress once, without reward) —
  Core 167; EditMode 483 passed, 2 skipped (two profile-migration tests first failed because the new list was written
  into old saves' tutorial section — it is now written only when non-empty).
- **Built player** (`-nsTutorialTour`, isolated profile) **PASS**: the help index for "brake" lists 4 lessons; attempts
  (the validator autopilot standing in for a player — the judge and overlay are the real ones) pass controls and camera
  (camera cycled through the button path), braking (25 km/h shed from 152 km/h), turn-in and exits (out at 93 km/h),
  exits and gearing (12.7 s of 20 s), resets (+3.0 s, drove on), ghost deltas (a lap with the instructor's ghost); the
  drift demonstration scores 323 raw in the zone and the countersteering demonstration catches a slide — neither is
  recorded as a pass; a card answered wrong then right. `Evidence/ui/tutorial/`. The first build showed the drift
  demonstration scoring 0 (the start-offset fault above) and the lesson banner over the HUD's drift counter; both fixed.
- **Limits:** thresholds are design values for a first-time driver, checked only against the autopilot; the lessons
  run offline — online accounts can open them only with a Local profile (no convoy tutorial session yet, spec §16 asks
  for one with independent progress); the braking lane, skid pad and training bays off the loop are not used (the
  route's progress and recovery cover the loop only); no human has taken the lessons.

## V-112 — The route/elevation chart online (spec §8) (2026-09-28)
- Revision: the work committed with this entry; player build of that tree.
- **What changed:** the race client keeps a display-only trace of its own car (`RaceClient.OwnTrace`: predicted positions
  at 10 Hz and the server's checkpoint times — never sent, never a result; the server records the real ghost). After an
  online race the convoy screen offers "Route Chart — last race", a page (`RouteChartScreen`) with the same chart as the
  offline Results page, against the first ghost on the road.
- **Built player** (`ui-tour-online.ps1 -Intent 4 -Course C07`, loopback dedicated server, one windowed client, validator
  autopilot) **PASS**: "vs your best 119.535 s: most lost in sector 1 (+0.95 s, 0–108 m); most gained in sector 11
  (−0.03 s) · 7 braking points". `Evidence/ui/route-chart/`. The first build stretched the chart and let the summary
  overlap it (the area's proportions and the label's default offsets); fixed before this run. EditMode 483 passed, 2
  skipped.
- **Limits:** the trace is this client's prediction (corrections included as they happened), fine for a chart, not for
  timing; spectators get no chart.

## V-113 — Published challenge references: CH04, CH08, CH10, CH12, CH26, CH29 (2026-09-29)
- Revision: the work committed with this entry; editor measurement and player build of that tree.
- **What changed:** the six challenges V-090 left waiting for published values now have them, as hashed content
  (`authored/challenge-references.json`, night-signal/challenge-references@1, loaded by the catalogue: race content
  `26709731…` → **`aff0ce06…`**). The explicit PlayMode `ChallengeReferenceTests` measures them like the benchmark
  certification — the three starters (V01–V03 stock), solo, validator autopilot, each course's own conditions:
  **CH04** (C02) exit floors = 0.95 × the slowest crossing (74.4 / 72.8 / 75.6 km/h); **CH08** (C08, wet) and **CH12**
  (C20) braking-zone exit windows = 0.85 × lowest … 1.15 × highest (e.g. C08 zone 1 97.9–133.3 km/h); CH08's position
  envelope is the brake-on point, 15 m past the latest starter's (3,616 m and 4,714 m) — the starters brake to the
  zones' ends, so a release point could not be published; **CH10** (C11) Silver = 1.10 × the slowest two-lap time
  (233.7 s), laps within 2.0 s (the spec); **CH26** (wet C12) and **CH29** (C24) Gold = the raw the autopilot earned
  drifting at skill 0.95 in V04 (6,100 and 3,600; the wet run lost 3,462 raw to walls and a reset, so the published Gold
  is its earned score, not its weak banked 2,673); CH29 allows 5 % of the earned raw lost. `Evidence/challenges/references.txt`.
  `GateJudge` now measures exit-speed gates and braking zones (entry/exit speed, braking, brake-on and release points,
  walls, contacts and resets inside); `EntrantProgress.LapMicros` keeps lap times; Core `ChallengeReferenceJudge` judges
  them; the predicates run offline and on the game server alike.
- **Tests:** Core `ChallengeReferencesTests` (the published file loads into the hashed catalogue; each judgement passes
  and fails on its own condition; broken references refused) — Core 173; Services 356, Builds 232, Toys 92; EditMode 483
  passed, 2 skipped.
- **Built player** (`-nsReferenceTour`) **PASS**, every grant matching the measured facts computed apart from the
  predicates: CH04 (C02 exits 78.3 / 76.6 / 79.6 km/h over their floors), CH08 (both wet zones inside their windows and
  braked in time, no wall), CH12 (four late-braking zones inside their windows, clean) and CH29 (3,638 raw banked, none
  lost) granted; CH10 withheld (the autopilot's laps differ by 5.8 s — the standing start; the challenge asks for
  deliberate pacing) and CH26 withheld (2,673 banked of a 6,100 Gold, four walls). `Evidence/challenges/reference-tour.txt`.
- **Control plane restarted** for the content change (tracked task; health ok, race content `aff0ce06…`).
- **Limits:** the references come from the conservative validator autopilot and one drifting AI — a human pass over
  their feel is still owed; CH04/CH08/CH12 references sit inside the reference drivers' own envelopes by construction;
  offline Freeplay has no weather choice, so CH26 (wet C12) is reachable online (the Freeplay weather option) only.
  37 challenges need the fixed challenge-trial format (loaners, fixed builds, cups, scripted AI) — 37 of 75 exist now.

## V-114 — Gate 4 audit; three and five humans; a crash in the middle of a settlement (2026-09-29)
- Revision: the work committed with this entry; the player build of V-113 for the races (no game code changed since).
- **Audit:** `docs/GATE4.md` maps every Gate 4 item to executed evidence (entries and tests) and names what is open —
  a built-player convoy of mixed progress, Custom Cup multi-leg races, challenge loaners, PostgreSQL, a pad-only
  walkthrough, localisation and string bounds, the §14 performance profile — and lists the Gate 5 items known to be
  blocked (internet and LAN tests with people, PostgreSQL/Supabase, the Linux server module).
- **Party sizes 3 and 5** (the two never run): `net-race.ps1 -Humans 3 -FreeplayAi 3` and `-Humans 5 -FreeplayAi 1` on C01
  (loopback dedicated server, separate AutoClient processes with the validator autopilot, content `aff0ce06…`): every
  human finished (3 humans: P1 86.409 s, P3, P5; 5 humans: P1 85.957 s, P2, P3, P5, P6) and the control plane settled
  both — `Evidence/net/run-20260928-223616-h3-C01-ai3`, `run-20260928-223837-h5-C01-ai1`. Every size 1–6 has now run.
- **Crashes during transactions:** Services `SettlementCrashTests` — a trigger refuses the receipt row, i.e. the
  settlement fails after the wallet, ledger, challenge unlock and cosmetic were written: nothing remains (balance,
  ledger, unlocks, cosmetics, receipts, match state all unchanged); the retry settles once; an identical replay is
  recognised and pays nothing. SQLite only (no PostgreSQL here).
- **Limits:** automation over loopback, not people; the crash is an in-process failure inside the transaction, not a
  killed process (SQLite's journal covers that case by its own design; not exercised).

## V-115 — The Custom Cup offline (spec §8) (2026-09-29)
- Revision: the work committed with this entry; player build of that tree.
- **What changed:** the Custom Cup existed only in the control plane (proposals and allocation carry the legs; nothing
  raced more than the first). Offline it is now playable: the Freeplay panel's third format, "Custom Cup — three legs,
  one field", with Leg 2 and Leg 3 rows (sprint and circuit courses); the schedule is published before the start; one
  field of authored rivals (the named lead, if any) races every leg; each leg is an ordinary Freeplay race — paid and
  recorded as one, no stake; between legs the Cup page shows the table with "Next leg" or "Leave the cup" (the short,
  cancelable results/ready area). Core `CupTable`: points 10-8-6-5-4-3 for places 1–6, nothing for a DNF or DQ, every
  entrant keeps its line once raced (a missed leg is not regained), countback on wins, then second places, and so on.
- **Tests:** Core `CupTableTests` (points and order; a DQ keeps its line without regaining the missed leg; countback) —
  Core 176; EditMode 483 passed, 2 skipped.
- **Built player** (`-nsCupTour`, isolated profile, validator autopilot) **PASS**: C01 → C02 → C03 with R12 and R39 in
  every leg; after the legs the table read 10/8/6, 20/16/12 and 30/22/20 (Cup Driver 1-1-1, Rui Takeda 2-2-3, Ayame
  Sugiura 3-3-2), every line's points equal to its placings; the legs paid 12,000 → 54,121 cr. The first run found the Cup
  page starting a leg before the router had ever built it (no app yet); the hub now prepares it. `Evidence/ui/cup/`.
- **Limits:** online the cup still races only its first leg — the game server has no multi-leg match (scene changes
  inside one match, readiness per leg, one settlement); the fixed challenge cups (CH14, CH42, CH69, CH72) need that too.

## V-116 — The Custom Cup online (spec §8); rival drivers in Freeplay settlement; two built-player faults (2026-09-29)
- Revision: the work committed with this entry; player build of that tree. The control plane was restarted once for this
  server code (tracked task; health ok, race content `aff0ce06…`, customization `306dd38e…`) and served every run below.
- **The cup across matches:** each leg is an ordinary match (readiness, allocation, settlement, ordinary race money, no
  stake) — the game server needs no multi-leg match. The convoy keeps a cup run from the first leg's plan: the settings,
  a Core `CupTable` (the V-115 rules), and the course access frozen then (Addendum 01 §5.2: a guest's passes for every leg
  survive the sponsor leaving; a member who joins later needs a sponsor present). `EventSettings.CupLeg`; after a settled
  leg the post-event step is "Next Leg — <course> (2 of 3)", which opens the next leg as a proposal (origin `cup-leg`),
  and after the last "Cup complete — return to Event Setup"; any other new event ends the cup, and an aborted leg ends
  it with a notice (a leg without results cannot be raced again inside the cup). Settlement hands the leg's placings to
  the table (finishers by place; a DNF or DQ scores nothing and keeps its line); the snapshot carries `cup` (schedule,
  legs raced, complete, standings). Event Setup offers Leg 2 / Leg 3 rows for the cup format; the Convoy screen shows
  the schedule and the table.
- **Fix — rival drivers in Freeplay settlement (a V-109 fault online):** online Freeplay AI race under slot ids
  (`ai-1`…) with the authored rival as the roster's driver. Settlement read the entrant ids, so in real online Freeplay
  matches the archetype facts (CH38/CH73), the CH70 "raced a crew member" check and rival names never matched a rival.
  V-109's Services tests had built AI entrants from rival ids, which the allocator never does, so they passed; its
  built-player proof of CH38/CH73 was offline only. Settlement and `SqlGameStore.FreeplayRacesAsync` now read the roster's
  drivers (older configs without a roster fall back to the entrant ids); `ArchetypeSettlementTests` build rosters as the
  allocator does. Because the store replays settled races, earlier online Freeplay races with a frozen roster now count
  towards CH38/CH73; a CH70 missed in an earlier Freeplay race is not granted after the fact.
- **Tests:** Services `CustomCupConvoyTests` (three legs as matches with the table; the first leg's passes carry a guest
  after the only sponsor leaves; the departed sponsor keeps its line; cup complete returns to Event Setup; an aborted leg
  ends the cup) — Services 359, Core 176, Builds 232, Toys 92; EditMode 483 passed, 2 skipped.
- **Built player** (`ui-tour-online.ps1 -Intent 7 -CupLegs C01,C02,C03`: one dedicated game server process for all three
  legs, one client driving the real menus, validator autopilot) **PASS**: legs C01 → C02 → C03 raced as three matches
  with three AI each; the table after each leg: Tsubasa Muraoka 10 / Neri Takase 8 / Driver 1 6 / Michi Kagawa 5; then
  18 / 18 / 11 / 11 (ties broken by the later leg); then Neri Takase 28 (2 1 1), Tsubasa Muraoka 26 (1 2 2), Michi Kagawa
  17 (4 3 3), Driver 1 16 (3 4 4) — every line equal to its placings; after the last leg the step was "Cup complete" and
  the cup closed. The Convoy screen showed the account's server-side archetype progress (rival styles raced 12/12, lead
  styles beaten 3/3) from its settled Freeplay races after the driver fix. `Evidence/ui/online/cup/`.
- **Two faults the built player found (both fixed before the passing run):**
  - *A dedicated server kept every course it had hosted.* `RaceServer` loads the course scene additively and never
    unloaded it; every earlier tour ran one match per server process (`-nsExitAfterMatch`), so a second match on another
    course had not been run. On C02 after C01, all four cars stopped at ~600 m (checkpoint 5) with 50+ recoveries each
    for 13 minutes — C01's collision stood across C02's road. A match now unloads any course scene left by the previous
    one before it loads its own (the server log names it); the passing run unloaded C01 before C02 and C02 before C03,
    and leg 3 (C03, two laps, 5,068 m) ran with no recoveries.
  - *Start Event could be pressed while the screen was still waiting for a reply.* The Convoy screen takes one command at
    a time and dropped a press made during a pending request without a word; the Event Ready snapshot can arrive before
    the ready request's own reply, so a Start pressed at once did nothing (the first online cup run then sat until the
    member went Away). Start Event and Advance are now unavailable while a request is pending, and the screen redraws
    as soon as one is sent. The tour records any refused start as a failure (none in the passing run).
- **Process note:** two failed tour runs were stopped (their own client and server, checked by name and start time); a
  window capture of the running client was taken locally to read its state and not kept.
- **Limits:** the fixed challenge cups (CH14, CH42, CH69, CH72) still need the challenge-trial format (loaners, fixed
  builds, scripted AI); one human only in the built-player cup (the convoy rules for more are covered by the Services
  tests).

## V-117 — Campaign across mixed progress in built players (spec §5.1, Gate 4) (2026-09-29)
- Revision: the work committed with this entry (the code at `2544716`, the tour fixes after it); player build of that
  tree. The control plane was restarted once for the server change below (tracked task; health ok, race content
  `aff0ce06…`) and served both runs.
- **What was missing:** the rules were enforced and tested (V-009, V-013; Services `ConvoyDirectoryTests`), but the
  Convoy screen only shrank its stage row to the shared frontier: it never said whose frontier limits the convoy and
  never showed a stage as locked, both of which spec §5.1 asks for.
- **Now:** the convoy's campaign access also carries the most-progressed member's frontier (`highestStage`). The stage
  row lists every stage up to it; those above the shared frontier read "· locked for this convoy" and cannot be proposed;
  when the shared frontier moves (a member joins or leaves, a stage is cleared) the selection returns to it; every member
  sees the neutral line from the server, "Next shared stage: S07 — one member has not cleared it." (no names). A joining
  member who pulls the frontier below an open proposal withdraws it with the reason (the existing server rule, now seen
  in a built player).
- **Tests:** Services `ANewerMemberJoining_WithdrawsAStageBeyondTheNewSharedFrontier_WithANeutralExplanation` (the
  proposal withdrawn with its reason, `highestStage` 13 against a shared S06, the line without names, S08 refused, S06
  accepted) and `highestStage` in the existing frontier test — Services 360, Core 176, Builds 232, Toys 92; EditMode 483 passed, 2 skipped.
- **Built players** (`ui-tour-social.ps1 -Mixed`: two windowed clients on the real menus, one dedicated game server;
  host = dev account 0 with Normal S01–S12 cleared, guest = dev account 1; validator autopilot) **PASS on both clients**
  (run 2): alone, the host's row listed 13 stages ("Next shared stage: S13.") and it proposed S10; the guest (S01–S06
  cleared) joined from a friend invitation and the proposal was withdrawn — "S10 is no longer available to this convoy.
  Next shared stage: S07 — one member has not cleared it." — with 13 stages still listed, S08–S13 locked, the selection
  back on S07 and the same line on the guest's screen; S08 could not be proposed from the screen, and asked directly the
  server refused it with the same explanation; they raced S07 (C04, 2 humans + 3 AI): the guest's receipt read "Stage
  cleared — first clear" (+30,357 cr) and the host's "Stage cleared" with no first clear (+11,651 cr, ordinary race money
  — spec §5.1), and both screens then showed the shared frontier at S08. Run 1 (the guest from S01–S05) showed the same
  on S06: guest PASS, host FAILED only on the tour's own wait (it read the readiness cooldown from a snapshot that is not
  re-sent as the cooldown runs down — the tour now waits on its own clock); every product check in it held.
  `Evidence/ui/online/mixed-progress/`.
- **Also fixed in the tour:** it read `eventProposal.settings` while the proposal was a JSON null (an exception stopped
  the host's script in the first attempt; nothing was raced).
- **State left by the runs:** dev account 1 (the guest) now has Normal S01–S07 cleared in the local development
  database (two genuine first clears from these races).
- **Limits:** two humans; Hard access across mixed progress stays covered by tests only (no dev account has the Normal
  finale); the spec's separate "leader inspecting" preview is not built (the leader's row is the selection).

## V-118 — A bound test of every string the tours pass (Gate 4) (2026-09-29)
- Revision: the audit at `931162d`; the player build of that tree (before the Challenge Trials button of V-119 existed).
- **What was missing:** labels shrink to fit and never vanish (V-053), but nothing measured every string against its box —
  Gate 4's "no bound test of every string".
- **The audit** (`-nsBoundsAudit` beside any tour; `FrontEndApp.Bounds.cs`): after each screen change, at each tour
  screenshot and every 5 s in a race, every visible label (TextMeshPro, not faded, non-empty) is laid out and its drawn
  size compared with its box less margins. A label too big at its minimum size, or spilling more than 5 % past its box,
  is a failure; fitting labels whose glyph edges reach a few pixels past the box (TMP fits by advance widths) are listed
  with the spill; fixed-size live figures that overflow are listed apart; labels left at their minimum size are noted.
  The report is written at quit; `-nsUiTour` fails on any failure. The first grading counted every 2–13 px glyph-edge
  spill as a failure although those labels sat far above their minimum size; it was corrected before the runs below.
- **Runs** (`Tools/run/bounds-audit.ps1`: one window at a time, seeded preferences and Local profiles under Builds/):
  at 1280×720 with Text size 150 % / HUD size 130 % the UI, instrument (Settings), Driver Card, diary, Custom Cup,
  Driving School, story and appearance tours; at 2560×1080 with the same sizes the UI and instrument tours; at 1920×1080
  with the defaults the UI tour — **11 of 11 tours PASS; 0 failures, 0 labels at their minimum size, 0 fixed-size
  overflows** across 287 (UI tour), 226, 184, 138, 117, 111, 95 and 55 distinct labels; edge spills of 1–12 per run
  (e.g. the title "NIGHT SIGNAL" drawn 12 px wider than its 834 px box at 98.6 pt, minimum 63). `Evidence/ui/bounds/`.
- **Limits:** only what the tours pass is measured (the online screens and the meet were not in these runs); English
  only (no localisation exists); whether a label reads well is still a human check.

## V-119 — Challenge trials, first slice: supplied loaners and measured targets, offline (spec §11) (2026-09-29)
- Revision: the code at `053b05b` and `ee3ef38` with the screen name and briefs tidied in the commit with this entry; the
  player build of that tree. The control plane was restarted once for the new content (tracked task; health ok, race content `366c9322…`);
  nothing online used it yet.
- **The format** (`docs/CHALLENGE_TRIALS.md`): spec §11 requires every challenge to be achievable by one player "through
  sanctioned races, ghosts, AI, the meet's built-in interaction spots, and fixed loaners"; 38 of the 75 name a supplied
  car or build, a fixed reference, scripted AI, a fixed cup or a T00 drill. A challenge trial fixes the course, its
  conditions, the loaner (a car and its parts, resolved exactly like a garage build — `Core.Builds.TrialLoaners`; never
  the player's garage), the rules and the targets; trials that must all be passed for one challenge form a group (CH54's
  two drive layouts).
- **Core** (engine-free, so the game server can judge the same way): `ChallengeTrials` (definitions, `TrialJudge` with a
  named reason for every condition, content problems) loaded and hashed from `authored/challenge-trials.json`; Local
  facts carry the trial id and verdict, the profile keeps `TrialsPassed`, a challenge is granted once its trial (or whole
  group) is passed, and a trial supplies its course as it supplies its loaner (no course purchase — spec §11). Race facts
  added: seconds with the handbrake held, and the judged drift zones in which a chain was banked. The autopilot can start
  slides by power instead of the handbrake (CH25 forbids it).
- **Targets, measured** (explicit PlayMode `ChallengeTrialReferenceTests`, `Evidence/challenges/trials.txt`): each loaner
  driven solo by the validator autopilot; time targets Gold 1.02 ×, Silver 1.10 × its time; drift trials at drift skills
  0.95, 0.80 and 0.65, the cleanest run used (fewest resets, then most scored), drift targets from the raw it scored in
  the zones (the larger of banked and earned, as V-113). Published: CH55 (stock V01, C04) Silver 2:30.4; CH11 (stock V12
  under C12's PI 699 cap) Gold 3:14.3, no reset, at most one wall impact; CH51 (V07 on rain tyres, C08 wet) Silver 2:49.2;
  CH54 (C09, PI cap 450) front drive V06 Silver 2:38.4 and rear drive V05 Silver 2:40.5; CH25 (V09 drift tyres, C16, no
  handbrake) Silver 2,900 raw; CH28 (V15 drift tyres, C23) Gold 4:34.1 and 2,000 raw in one run; CH30 (V16 drift tyres,
  C25) Gold 10,800 raw with a chain banked in every judged zone. **The first measurement was too soft and was redone:**
  CH28's first loaner (V13 on sport tyres) reset six times, so its time and banked raw would have been easy targets; V15
  and V16 on drift tyres were tried; every C23 run still resets two to four times (the drift controller runs out of
  road — the open drift-controller item), so **CH28's Gold time includes two resets and is softer than a clean run's**.
- **Tests:** Core `ChallengeTrialsTests` (9: the file, every loaner within its cap, the judge per condition, drift and
  handbrake and every-zone rules, an unpublished trial never passes, groups, content problems, the Local grant once and
  only with the trial's course and loaner) — Core 185, Services 360, Builds 232, Toys 92; EditMode 483 passed, 2 skipped.
- **Built player** (`-nsTrialTour`, buttons only, isolated profile, run with the bounds audit at 1280×720 and Text size
  150 %): **PASS** — every trial ran from its row and was judged, the profile kept exactly the passes, CH54 was earned only
  after its second layout. The autopilot reached **7 of 8**: CH55 2:16.696, CH11 3:10.459 (one wall impact, no reset),
  CH51 2:33.753, CH54 2:23.999 and 2:25.904, CH25 3,522 raw with no handbrake (at its reference's drift skill 0.80), CH30
  11,002 raw with 6 of 6 zones banked (at skill 0.95 — at its reference's 0.65 it banked 10,168 in 5 zones, the target
  being set from what that reference scored including the points its walls cost it). **CH28 was not reached at any skill**
  (best: 4:28.657 inside the time, but 740 raw banked of the 2,000): that it can be done by a person is not demonstrated.
  Challenges earned: CH55, CH11, CH51, CH54, CH25, CH30. `Evidence/challenges/trials-tour/`.
- **Found by the built player:** the trial list's check mark and the verdict's ✓/✗ were drawn as boxes — the game font has
  no such glyphs; they read "earned" / "1 of 2" and "ok:" / "MISSED:" now, and the bounds audit fails any label drawn with
  the missing-glyph box.
- **V-118 repeated with the glyph check** (the trial build; the eight offline tours plus the trial tour at 1280×720 with
  Text 150 % / HUD 130 %, the UI and instrument tours at 2560×1080, the UI tour at 1080p): **12 of 12 PASS — 0 overflow,
  0 missing glyphs**, 1–13 edge spills per run; one label at its minimum size (the race HUD's ghost row in a trial, which
  fits). `Evidence/ui/bounds/` now holds these reports.
- **Limits:** offline only — online trials (the Challenges intent, the loaner frozen into the plan, the game server judging,
  settlement granting once) are the next step; the targets are the autopilot's, not a human benchmark; 31 challenges remain
  for the later slices (geometry judges, scripted AI, fixed cups, T00 drills and workshop trials).

## V-120 — Challenge trials online (spec §11) (2026-09-29)
- Revision: `33e91ea` and `bbdee6a`, documented in the commit with this entry; the player build of that tree (non-development,
  the canonical automation build). The control plane had stopped with the previous session and was started once on this
  source (tracked task; health ok, race content `7c38b3c4…`, the V-119 trials file); it served every run below.
- **What was paused and when:** this work was written while the owner had paused Play Mode and game windows (2026-09-29):
  the control plane and its tests with `dotnet test`, the game runtime compiled headlessly from Unity's generated project
  (outputs outside the project) — no player, no editor run. The pause was lifted the same evening; the editor compile,
  player build and built-player runs below came after it. An interrupted full .NET run from before the break was rerun.
- **Online path:** under the Challenges intent a convoy proposes a trial (`event.propose {challengeTrialId}`); it is raced
  as a non-contact, AI-free Time Attack on the trial's course and needs no sponsor — now or after a roster change (the
  trial supplies its course as it supplies its car). At the start the control plane resolves the loaner from its own parts
  data exactly as a garage build (`GarageService.TrialLoanerBuild`) and freezes it into every entrant (car, PI, build
  hash; the members' garage builds and car caps do not enter it); the game server re-resolves the same hash (as for any
  frozen build), resolves the loaner itself and judges each human with the same Core `TrialJudge` as offline — a human
  counts as driving the loaner only if the frozen build is that loaner. Results carry `trialId`/`trialPassed`/
  `trialSummary`; settlement refuses trial facts that do not belong to the match, writes the verdict on the receipt
  (`challengeTrial {trialId, challenge, passed, summary, groupPassed, groupSize}`), replays the account's settled passes
  from its receipts and grants the challenge once its trial — or its whole group — is passed. Trial ghosts are kept as
  `trial-<id>` (as offline) and no rival reference is raced (the match info carries the trial id). The Convoy screen has a
  Challenge Trial row under the Challenges intent (hiding the Team Trial rows), names the trial on the proposal, and the
  receipt text gives the verdict in words and **every challenge a result earned — which the online receipt had never
  shown** (they were settled, not displayed).
- **Tests:** Services `ChallengeTrialOnlineTests` (8: the proposal's settings with no sponsor and none needed after a join;
  a chosen cap, an unknown trial and a Team-Trial-bound intent refused; the start freezes the loaner for every entrant
  and refuses without it or with another car; settlement grants only on the server's pass; CH54 only with both layouts
  across settled matches; foreign trial facts refused; the store reads passes back from settled receipts) — Services 368,
  Core 185, Builds 232, Toys 92; EditMode 483 passed, 2 skipped.
- **Built players** (`ui-tour-online.ps1 -Intent 5 -ChallengeTrial <id>`: one game server bound to 127.0.0.1, one
  client on the real menus, dev account 0, validator autopilot): **TR-CH55 PASS** — the server raced the frozen loaner
  (`c12c2bea1a67`, the offline V01 loaner's hash, verified), judged 2:16.713 against 2:30.4 PASSED; the settled receipt
  holds the verdict and CH55 with its cosmetic `COS-CH55`. **TR-CH54-FWD then TR-CH54-RWD PASS** — "Challenge trial passed
  (1 of 2 of CH54's trials)" with nothing granted, then "(2 of 2)" and "Challenge earned CH54 One Index, Two Cars" (+8,000
  cr Silver challenge cash on the second payout); both ghosts kept as `trial-TR-CH54-…`. `Evidence/challenges/trials-online/`.
- **Found on the way:** the CH55 run, made before the ghost-format fix, was kept as the dev account's C04 **Time Attack**
  ghost (a genuine stock-V01 run, in the ordinary bucket); later trial runs are kept apart. Left in the development
  database.
- **Limits:** trials whose conditions differ from their course's are refused online for now (none of the eight does); one
  human per run here (the proposal and start rules for more are in the tests); CH28 is still not shown reachable (V-119).

## V-121 — CH13: the paved-road rule and a fixed Gold ghost, offline and online (spec §11) (2026-09-29)
- Revision: `0d98da7`, documented in the commit with this entry; the player build of that tree. The control plane was
  restarted once for the new content (TR-CH13 changes the hashed trials file; tracked task; health ok, race content
  `0777df53…`) — the first online CH13 run, against the previous content, could not be offered the trial and failed, as it
  should have.
- **What:** CH13 "beat the fixed C21 Gold ghost while keeping all tyres in the paved corridor". Race facts gain seconds
  with a tyre past the paved edge (the car's centre offset plus half its track and half a tyre against the paved
  half-width; shoulders are not paved) and where it happened; a trial rule `allTyresPaved`; a trial can race a **fixed
  Gold ghost** — its measured reference run, recorded by `ChallengeTrialReferenceTests` into
  `Resources/TrialGhosts/<trial>.json` (provenance `trial-reference`), raced in gold offline and online (the match info's
  trial id), and its time is the target ("faster than" the ghost). TR-CH13: stock V14 (FWD, PI 680) on C21.
- **Measured — and why the ghost does not keep its own rule:** the validator's run (136.346 s) puts a tyre off the paved
  road for 0.5 s at two corners (≈4,620 m and ≈4,830 m; centre 3.4–3.55 m from the axis on a 4.25 m paved half-width —
  up to 0.13 m over): it cuts the apex. Three ways to a clean reference were measured and all were worse — a slower pace
  (0.97–0.88 ×: 0.7–0.9 s off), a narrower apex line (line 0.30/0.15/0.05: 6–16 s off, running wide on the exits of the
  narrow 500 m corner), a wider edge margin (1.6/1.9/2.2 m: 1.2–6 s off). So the Gold ghost is the validator's run and
  **CH13 is not shown reachable within its rule by the autopilot** (as CH28). The harness keeps these searches (it tries a
  wider edge margin for paved-only trials and publishes the one it used, so a replay reproduces it).
- **Tests:** Core `ThePavedRule_FailsTheTrial_OnAnyTimeOffThePavedRoad` and the file test (9 trials) — Core 186, Services
  368, Builds 232, Toys 92.
- **Built players:** `-nsTrialTour` (with the bounds audit, 1280×720, Text 150 %) **PASS** — nine trials ran and were judged;
  the Gold ghost raced on the road ("Ghost · Gold ghost 136.347 s"); TR-CH13 not passed (tied the ghost to the
  millisecond, 0.5 s off the paved road); the autopilot reached 7 of 9 (CH13 and CH28 not); bounds 0 failures, 0 missing
  glyphs (the ghost's HUD row fits only at its minimum size — noted). Online `ui-tour-online.ps1 -Intent 5 -ChallengeTrial
  TR-CH13` **PASS** — the client raced the Gold ghost online, the game server raced the frozen V14 loaner (`53f0713d354f`,
  verified) and judged 2:17.196, 0.6 s off the paved road: not passed; the receipt carries the verdict.
  `Evidence/challenges/trials-tour/`, `Evidence/challenges/trials-online/`, `Evidence/challenges/trials.txt`.
- **Limits:** CH13 and CH28 wait for a person (or a better autopilot line on narrow roads) to show them reachable; the other
  slice-2 challenges (CH15, CH17, CH19, CH22, CH27) need gates or drift zones authored into four routes — a change of
  those courses' revisions, so their reference ghosts must be re-recorded and their benchmarks checked with it.

## V-122 — CH15: every marked gate touched, a Gold reference on C25 (spec §11) (2026-09-29)
- Revision: `ea83dbb`, documented in the commit with this entry; the player build of that tree.
- **What:** C25's route already carried CH15's three final-sector apex gates (`C25-FINAL-APEX-1..3`), and the gate judge of
  V-090 already measures challenge apex gates. A trial rule now requires every gate tagged with the trial's challenge
  (`allChallengeGates`; the facts carry the gate count, so a course without them never passes). TR-CH15: stock V17 (AWD, PI
  810) on C25, no reset. The reference aims at the gates, as the V-090 gate tour does.
- **Measured:** 268.259 s with no reset and all three apexes touched — a reference clean against every rule of the trial —
  so the Gold time 273.7 s (1.02 ×) is shown reachable within its rules. `Evidence/challenges/trials.txt`.
- **Tests:** Core `TheGateRule_NeedsEveryMarkedGate` and the file test (10 trials) — Core 187.
- **Built player:** `-nsTrialTour` (with the bounds audit, 1280×720, Text 150 %) **PASS**, ten trials: TR-CH15 **PASSED**
  (4:28.259 against 4:33.7, no reset, 3 of 3 gates) and CH15 earned; the autopilot reached 8 of 10 (CH13, CH28 not); bounds
  0 failures, 0 missing glyphs. `Evidence/challenges/trials-tour/`.
- **Limits:** CH15 online is not run in a built player (the online autopilot does not aim at gates; the game server's judging
  path is the one CH55/CH54/CH13 exercised online).

## V-123 — Challenge-zone chains: CH17, CH19, CH22, CH27 judged (spec §7, §11) (2026-09-30)
- Revision: `01ddf48` (judge, predicates, tests) and `d3293e0` (the autopilot's zone slides and `-nsZoneTour`), documented
  with `the commit with this entry`; the player build of `d3293e0`.
- **What:** C03's link corners (CH17), C05's demonstration zone (CH19), C09's outer clip zones (CH22) and C19's transition
  zones with their bank gate (CH27) were already tagged in the routes, but nothing judged those zone kinds (the raw drift
  scorer and Drift Attack read only `drift-zone` gates, and none of these courses has one). Core `ZoneChainRun` judges them:
  a chain is one continuous legal slide (≥35 km/h, forward in the legal direction, on the road, 10–80° slip) that links
  each challenge zone it slides through — a clip zone only with the car within the clip's tolerance of its marked line —
  and survives the spec's 1.0 s straightening interval; it banks when the slide straightens for longer, at a sector end,
  at the finish or at its challenge's bank gate (a timing gate with the same tag), and it is lost on a meaningful wall
  impact, leaving the road, a reset or a spin, with any barrier touch recorded. A demonstration zone keeps the longest
  continuous legal 20–35° hold. The runtime `ZoneChainJudge` feeds it next to the drift judge for every entrant, offline
  and in the game server's simulation; `ChallengePredicates` grants, after a finish: **CH17** three C03 link corners in
  one banked chain; **CH19** a 3 s hold in C05's zone; **CH22** all three C09 clips, each on its line, in one banked chain
  with no barrier touch; **CH27** all six C19 transition zones in forward order in one chain banked at the final gate.
- **Interpretation (recorded here):** the chain follows the slide, not the zones — a slide held between zones keeps it.
  The raw scorer only scores inside drift zones and banks after 1.0 s without scoring; applied to C09 and C19, whose zones
  are 90–140 m apart, that would make "one chain" through them impossible, so the challenge judge reads the spec's
  "1.0-second straightening interval between linked zones" literally. The raw scorer is unchanged.
- **Tests:** Core `ZoneChainTests` (12: linking, loss by wall / off-road / reset / spin, the 1.0 s interval, a slide held
  between zones, the bank gate, forward order, clip lines and touches, the demo hold, no scoring behind the high-water
  mark, the four routes' tags with no sector boundary inside a chain) — Core 199; EditMode `ZoneChallengeTests` (each
  predicate on its own course only, and only after a finish) — EditMode 484 passed, 2 skipped (explicit), 0 failed.
- **Autopilot (explicit PlayMode `ZoneChallengeMeasureTests`, `Evidence/challenges/zones.txt`):** the validator's tuned
  drift controller ended every slide within 1–3 s at the road edge (best 20–35° hold 0.4 s). Diagnosed with a per-step
  trace: it entered on the racing line's apex side and at 19 m/s — too slow for C05's 50–65 m sweeper, so the slide
  curved inside the road — and overshot the slip past the steering lock. Automation-only knobs, **off by default** in
  `RouteFollower` (every new term is multiplied by zero, so the AI, drift references and trial targets keep the tuned
  controller): hold the zones' marked lines (clip lines 0.8 m inside the clip), enter at 25 m/s, target 28° with
  slip-rate damping, and a 1 s grace to swing through a transition. With them, in V09 on T2 drift tyres (CH25's loaner):
  **CH19 reached** — a 5.55 s legal hold, granted; CH17 links LINK-1 and LINK-2 in one banked chain but runs out of road
  before LINK-3; CH22's slide meets the barrier at the first clip; CH27's slide ends in the first transition zone. Those
  three are **not shown reachable** by automation; a sustained-drift controller is the open work.
- **Built player (`-nsZoneTour`, 1280×720, Text 150 %, with the bounds audit):** **PASS (1 of 4 granted)** — C05: finished 126.3 s at skill 0.95, one 7.9 s banked chain, a 5.55 s legal 20–35° hold → **CH19 granted**; C03: the LINK-1 + LINK-2 chain banked (4.5 s), not LINK-3 → CH17 withheld; C09: the chain was lost against the barrier at CLIP-1 → CH22 withheld; C19: lost in TRANS-1 → CH27 withheld — every grant and withhold matching the raw chain facts (read independently of the predicates' helpers), the same chains and times as the PlayMode measurement. Bounds: 0 overflow, 0 missing glyphs. `Evidence/challenges/zones-tour/zone-tour.txt`.
- **Regression:** the knobs are off by default, and the built-player trial tour on the same build reproduced V-122 exactly — every one of the ten verdict lines identical (times, drift banked, zones), 8 of 10, PASS (`Evidence/challenges/zones-tour/trial-tour-regression.txt`).
- **Limits:** no online run of these four (the online autopilot does not drive zone slides; the game server runs the same
  `RaceSimulation` step and predicates as offline).

## V-124 — CH34: a pass in C02's marked hairpin exit zone, held to the gate (spec §11) (2026-09-30)
- Revision: `27dc764` (judge, predicate, tests) and the tour run committed with this entry; the player build of that tree.
- **What:** C02's route already marks the hairpin's exit (`C02-HP4-EXIT`, overtake zone 2584–2681 m, CH34) and the retain
  gate (`C02-HP4-RETAIN`, 2766 m). The racecraft judge (V-096) now keeps marked-zone passes: a pass of a live, moving car
  (the same legal-progress pass as CH31) made inside a route overtake zone tagged with a challenge — not on its approach —
  that stands once this car crosses that challenge's retain gate still ahead, neither car recovering in between; whether
  the car touched another car from 2 s before the pass to the gate is kept too (CH40 will need it; CH34 does not ask for a
  clean pass). **CH34** is granted after a finish on C02 in any race with live opponents, online (the game server runs the
  same judge) and offline. No trial: the predicate names no loaner or fixed field.
- **Tests:** EditMode `ZonePassTests` on the real C02 route (a pass at ~2660 m held past 2766 m → CH34, not on C05; a pass
  on the approach at ~2540 m → none; the place taken back before the gate → none; the passed car recovering before the
  gate → none; a touch just before the pass → CH34 kept, the touch recorded) and the V-096 `RacecraftJudgeTests` — 14 of 14.
- **Built player (`Tools/run/tour.ps1 -Tour RacecraftTour`, new one-tour launcher with isolated prefs and profile):**
  **PASS.** A third sprint on C02 (V07 against seven AI at PI 300) followed the car ahead at ~0.5 s and was allowed to
  attack only inside the marked exit zone (`AutopilotAttacksMarkedZones`, automation only); it finished P3 but made no
  pass there — the autopilot's own traffic logic keeps it behind (V-096 already found it a poor overtaker) — so CH34 was
  withheld, as the judge's facts say. The two C05 runs reproduced V-096 exactly (P6 in 147.3 s, 7 car contacts; the follow
  run P2 in 133.8 s, CH32). CH34's positive case rests on the EditMode judge tests, as CH31's does.
  `Evidence/courses/racecraft/racecraft-tour-ch34.txt`.

## V-125 — Racecraft trials: a fixed AI field, CH40 and CH41 offline (spec §11) (2026-09-30)
- Revision: `3d3b7ac` (the trial model, runtime, content, tests) and `3c3570c` (the list's pages, placement tests); the
  player build of `3c3570c`.
- **What:** challenge trials gain the **"race"** kind — a race against a fixed AI field instead of a solo run: each field
  car is its own stock car (not one chosen by the cap), an optional rival identity, a role (field, pacing, pressure,
  merge — only "field" is used so far) and a pace; `playerStartsLast` puts the human behind every AI car on the grid while
  the local player stays entrant 0 (`RaceEventRules.TrialField` / `HumansStartLast`; the grid slot is now separate from
  the entrant order). New rules: **win**, **no car-to-car contact**, **no checkpoint cut**, and **the marked clean
  overtake** (a touch-free pass inside the course's overtake zone tagged with the trial's challenge, the place held — the
  V-124 marked-zone pass). Race trials have no targets to measure (the measurement skips them). **TR-CH40** — C17's fixed
  challenge race: the V07 loaner (class C, cap 499) behind three class-C cars (V05, V06, V05 at pace 0.95); the marked
  overtake in the C17 braking zone, no contact, no checkpoint cut, finish. **TR-CH41** — class-equalized: six identical
  stock V07s on C18 (two laps), the player sixth on the grid; win with no reset and no car contact. The Challenge Trials
  list now pages ("More trials"): twelve trials had outgrown its ten rows, so the two new trials could not be reached by
  the buttons (found in the first run's screenshot, where the tour had opened them by id).
- **Online:** refused for now (`trial_unsupported`: the control plane cannot place a fixed AI field yet) — Services
  `ARacecraftTrial_IsRefusedOnline_ForNow`.
- **Tests:** Core `ARacecraftTrial_IsJudgedByItsRules`, `RacecraftTrialContent_IsChecked` and the file test (12 trials,
  each field inside its loaner's class) — Core 201; Services 369 (+1), Builds 232, Toys 92; EditMode `RacecraftTrialTests`
  (the offline plan's field placed with its own cars, roles and paces; the player sixth on C18's grid behind every AI car,
  still entrant 0; an ordinary race unchanged) — EditMode 488 passed, 2 skipped (explicit), 0 failed, plus the 2 new.
- **Built player (`-nsTrialTour`, 1280×720, Text 150 %, with the bounds audit):** **PASS** — the list's page button shows page 2 and its first row opens TR-CH40 (by the buttons); TR-CH40 ran against its three class-C cars with the player last: not passed — no marked overtake, one car contact, no checkpoint cut; TR-CH41 ran six V07s from sixth: not passed — P5, two car contacts, no reset; both verdicts kept by the profile as judged, neither challenge granted. The ten earlier trials judged exactly as in V-122 (every verdict line identical); 8 of 12 passed; bounds 0 overflow, 0 missing glyphs (73 labels). A first run on `3d3b7ac` judged the two race trials identically and exposed the ten-row list. `Evidence/challenges/trials-tour/` (`01-trials-page2.png`, `02-TR-CH41-brief.png`).
- **Limits:** neither race trial is reached by the autopilot (a poor overtaker that touches cars, V-096); the roles other
  than "field" have no behaviour yet (CH36's pacing rival, CH39's pressure car, CH37's merge car are next).

## V-126 — Racecraft trial roles: CH36's pacing rival, CH39's pressure car (spec §11) (2026-09-30)
- Revision: `ea7ffe8` (CH36), `4395f3f` (CH39 and its measured sector pace), `04b724c` (the pressure car's gap and the
  diagnostics); the player build of `04b724c`.
- **What:** two field roles get behaviour. **Pacing** (CH36): the car holds the far side of every challenge-tagged lane
  (`RouteFollower.LineZones`, the lane's offset mirrored) so the marked lane stays open, at its trial pace (0.9). The
  racecraft judge's marked-zone passes now include tagged **lanes** — a pass counts only with the passer inside the lane's
  band — held to the challenge's gate (C10-HOLD-GAIN); TR-CH36 asks for a held pass of the pacing car there. **Pressure**
  (CH39): the car never starts a passing move, treats any car ahead as in its lane, and keeps a car length + 2.5 m + 0.25 s
  behind (`NoPassing`, `FollowAnyLane`, `FollowGapSeconds` — all off by default, so the AI and every published reference
  keep the tuned driver). The racecraft judge times each pass through a challenge's **defence** zone (C14-PRESSURE, the
  course's second sector): its time, whether a pressure car stayed within 1 s behind for all of it, any barrier touch.
  TR-CH39: the player on pole, one V07 pressure car at pace 1.08 behind; pass when a sector is driven clean under pressure
  within the published Silver pace — **28.7 s**, measured as 1.10 × the loaner alone (26.083 s; the measurement now takes a
  list of trials, so this run touched only TR-CH39; `Evidence/challenges/trials-TR-CH39.txt`).
- **Found on the way (targeted built-player runs, `tour.ps1 -TrialOnly TR-CH39`):** (1) the pressure car drove past the
  player on another line (it only followed cars in its own lane) → `FollowAnyLane`; (2) it then ran into the player under
  braking at 941 m (one contact; the sector voided) → a following gap; (3) at 0.45 s it fell just past 1.00 s behind at
  the corner entry → 0.25 s. Each run's racecraft log (now printed by the tour) named the cause.
- **Tests:** Core (the pressure-sector and pacing-role rules, their content checks; 14 trials) — Core 201; Services 369,
  Builds 232, Toys 92; EditMode `RacecraftTrialTests` (the pacing car's mirrored lane, the pressure car's flags and gap, the
  sector pace published) — EditMode 492 passed, 2 skipped (explicit), 0 failed.
- **Built player:** the full trial tour (`-nsTrialTour`, 1280×720, Text 150 %, with the bounds audit) **PASS**, fourteen trials: **TR-CH39 PASSED** — both laps' C14-PRESSURE sectors in 26.08 / 26.10 s with the pressure car within 1 s throughout, no contact, P1 — against 28.7 s, and CH39 earned; TR-CH36 not passed — the autopilot followed the pacing car and made no pass in the marked lane (one car contact, P2); the twelve earlier trials judged exactly as in V-125 (every verdict line identical); 9 of 14 passed; bounds 0 overflow, 0 missing glyphs. `Evidence/challenges/trials-tour/`.
- **Limits:** CH36 is not reached by the autopilot (it makes no pass in the lane); both trials are offline only.

## V-127 — Challenge cups: CH14, CH42, CH69, CH72 offline (spec §8, §11) (2026-09-30)
- Revision: `0fc513f` (the cup kind, its judge, flow, content, measured leg times) and `527601e` (the cup page's per-leg
  lines and title, the targeted-run evidence); the player build of `527601e`.
- **What:** challenge trials gain the **"cup"** kind — three fixed legs run in order in the trial's loaner, each solo and
  non-contact, through the Custom Cup page (Next Leg between legs), in **one continuous session**: a leg not finished ends
  the cup, and leaving before the last leg ends it unpassed. Core `TrialJudge` judges the cup as a whole from every leg's
  facts: all legs finished in order, each inside its time, wall impacts and resets summed across the legs; between legs the
  page shows each leg's own checks. Leg times are measured, not guessed — the loaner's autopilot time on each leg × the
  cup's published factor (the measurement now takes a list of trials; this run touched only the three timed cups,
  `Evidence/challenges/trials-TR-CH42-TR-CH69-TR-CH72.txt`): **TR-CH14** (Gold) — C17 → C18 → C19 in the V07, untimed, not
  one meaningful wall impact across the legs; **TR-CH42** (Gold) — C06 → C13 → C21 in the V12, each leg ahead of its
  designated benchmark (1.02 ×: 2:23.1, 2:11.9, 2:22.0); **TR-CH69** (Silver) — the quiet touring cup C01 → C05 → C09 in
  the starter V01, within generous limits (1.35 ×: 1:56.8, 3:06.8, 3:26.5) and no quit; **TR-CH72** (Gold) — C21 → C22 → C23
  in the V12 at a generous Silver pace (1.20 ×: 2:47.1, 3:22.6, 4:26.8) with no reset in any leg. The legs race each
  course's own lighting (C23's is pre-dawn; C21 night, C22 dawn — CH72's "pre-dawn" is not applied to all three).
- **Online:** refused for now and kept off the online trial list (`trial_unsupported`), as the racecraft trials.
- **Tests:** Core `AChallengeCup_IsJudgedAsAWhole` (all legs in order; two of three; out of order; one slow leg; a reset or
  a wall impact in any leg; a leg not finished; untimed cups published; a leg never passes alone) and the content checks —
  Core 202, Services 369 (the online refusal), Builds 232, Toys 92; EditMode 492 passed, 2 skipped (explicit), 0 failed.
- **Built player:** the four cups alone first (`tour.ps1 -TrialOnly`, build of `0fc513f`): **all four PASSED**, each leg
  driven through the cup page by its buttons, the leg times equal to the measurement to the millisecond, the profile keeping
  each pass (`Evidence/challenges/cups-tour/`). Then the full trial tour: **PASS**, eighteen trials (`-nsTrialTour`, 1280×720, Text 150 %, with the bounds audit, build of `527601e`): all four cups PASSED again through the cup page with the same leg times, CH14 CH42 CH69 CH72 earned; the fourteen earlier trials judged exactly as in V-126 (every verdict line identical); 13 of 18 passed; bounds 0 overflow, 0 missing glyphs (79 labels over 269 moments, the cup page included). `Evidence/challenges/trials-tour/`.
- **Limits:** a cup is solo (no AI field — none of the four predicates asks for one); the autopilot reaching all four shows
  the targets reachable, not that they are hard.

## V-128 — CH43: beat R32's own C20 practice run, both defence gates inside the corridor (spec §11) (2026-09-30)
- Revision: `9e70b37`, documented with the commit of this entry; the player build of `9e70b37`.
- **What:** the gate judge keeps **defence zones** (a challenge-tagged `defence` gate driven start to end without leaving the
  legal corridor, on every pass; a reset or recovery inside breaks it) — it runs in every event, Time Attack included. A
  time trial may take a **rival's own practice run** as its target (`referenceRival`, `referenceStage`): the measurement
  drives the loaner with that rival's driver profile instead of the validator's, and the target is that time itself (no
  factor). **TR-CH43** (Gold): C20 in R32's own stock V16 (PI 790), C20's damp conditions; beat Mako Hoshino's (R32,
  rotation specialist, stage 28) practice run — **167.220 s**, both C20 defence/exit gates inside the corridor on that run
  (`Evidence/challenges/trials-TR-CH43.txt`, measured twice with the same result) — while driving both gates inside the
  legal corridor. No blocking or contact rule (the predicate says there is none).
- **Tests:** Core (the defence-gate rule, the file: 19 trials, TR-CH43's rival reference) — Core 202.
- **Built player (`tour.ps1 -TrialOnly TR-CH43`):** judged — **not passed**: the validator autopilot drove both defence gates
  inside the corridor but finished in 2:55.140, 7.9 s slower than R32's own run in the same car. CH43 is judged, not shown
  reachable by automation (R32's profile out-drives the validator on damp C20). `Evidence/challenges/trials-tour/trial-tour-TR-CH43.txt`.
- **Limits:** offline trial (online trials of this kind work like the others once published — its conditions are the
  course's own — but no online run was made).

## V-129 — Driving School section trials: CH52 and CH58 (spec §11, slice 5) (2026-09-30)
- Revision: `cae8043`, documented with the commit of this entry; the player build of `cae8043`.
- **What:** a trial may time a **marked section** between two route gates instead of its finish (`sectionStartGate` /
  `sectionEndGate`): the race simulation starts the clock when the car drives over the start gate and stops it over the end
  gate — across a circuit's seam too (T00 races two laps, so T00-CFG3, 1314 m → 108 m, crosses its start line) — and a reset,
  recovery or jump in between voids that attempt; the first clean time is judged. Trials may now run on the Driving School
  course (Local validation still keeps T00 out of Freeplay). **CH52** (Silver, group of two): comparison route A in the
  light variant (V07 + lightweight panels and glass, PI 448) and route B in the heavy variant (the stock V07) — the reading
  of "both routes in supplied light/heavy tune variants" chosen here; targets 1.10 × the validator's section (14.6 s, 21.4 s).
  **CH58** (Gold, group of three): configuration 1 in the FWD V06, 2 in the RWD V05, 3 in the AWD V07 — one car per named
  configuration; targets 1.02 × (15.8 s, 16.9 s, 20.0 s). `Evidence/challenges/trials-TR-CH52-HEAVY-…txt`.
- **Online:** refused for now (`trial_unsupported`: T00 is not an online course and the server does not time sections).
- **Tests:** Core (a section's time is judged, not the finish; "not driven"; each named gate is on T00's route with the
  challenge's tag; the three drive layouts) — Core 202; Services 369 (the online refusal).
- **Built player (`tour.ps1 -TrialOnly`, the five):** **all five PASSED**, the section times equal to the measurement
  (13.183 / 19.366 / 15.433 / 16.550 / 19.516 s); CH52 earned after its second trial, CH58 after its third, the profile
  keeping each pass. `Evidence/challenges/trials-tour/trial-tour-sections.txt`.

## V-130 — CH53: C03's two marked corners in two diff setups — drill trials (spec §11, slice 5) (2026-09-30)
- Revision: `127f509`, documented with the commit of this entry; the player build of `127f509`.
- **What:** C03's route already marks CH53's "guided two-corner challenge": two turn-in apexes (C03-DIFF-APEX-1, +3.5 m;
  C03-DIFF-APEX-2, −3.4 m on a 92° left) and two exits (C03-DIFF-EXIT-1/2). A new **"drill"** trial kind is a solo run judged
  by its rules alone (no time target); a new rule clears every exit-speed gate tagged with the challenge at a **measured
  floor** (the gate judge's slowest crossing, every lap), beside the existing every-marked-gate-touched rule. **CH53** (Silver,
  a group of two): the supplied Linea 20 (V05, RWD) with its stock open differential and with the race differential
  (DIF-T3-RWD-RACE, PI 381). Floors: 0.95 × the loaner's slowest exit, as CH04's were measured — **122.4 / 96.9 km/h** for
  both setups (the race differential changed the exits by 0.1 km/h on this car).
- **Found on the way:** the validator missed C03-DIFF-APEX-2 on every lap (crossing at +0.3–0.5 m, the wrong side): its speed
  plan follows the centreline, so the tighter inside line of the long left runs wide; narrower edge margins (0.9, 0.6 m) and
  holding the apex line 30 m past the gate did not help. At pace **0.95** it touches both apexes (−1.42 m) — the floors come
  from that run, so exits and turn-ins are shown reachable together. Automation-only knobs, off by default: a published
  `referencePaceScale` (replayed by the tour, like `referenceEdgeMargin`), an apex hold past a marked gate for drills, and the
  gate judge's lateral at each crossing (a diagnostic). `Evidence/challenges/trials-TR-CH53-OPEN-TR-CH53-RACE.txt`.
- **Tests:** Core (a drill passes on gates and exits with no time; a slow exit, a missed apex, an uncrossed gate fail; the
  floors are measured first; the two setups differ only in the differential) — Core 202.
- **Built player (`tour.ps1 -TrialOnly`):** **both PASSED** — both apexes touched on both laps, exits 128.9 / 102.1 and
  128.9 / 102.0 km/h against 122.4 / 96.9; CH53 earned after the second. `Evidence/challenges/trials-tour/trial-tour-TR-CH53.txt`.
- **Limits:** the two setups drive almost the same on this car (the published floors are equal); online, drills run as time
  trials do but no online run was made.

## V-131 — CH07: the trail-brake envelope in T00's test bend — a drill (spec §11, slice 5) (2026-09-30)
- Revision: `b9989ac`, documented with the commit of this entry; the player build of `b9989ac`.
- **What:** a drill rule judges every braking zone tagged with the challenge against a **trail-brake envelope** measured from
  the loaner's own trace in it (the gate judge's facts: brake-on point, last release, entry and exit speeds): braking begun by
  the reference's brake-on point + 15 m, some brake still held until its release point − 15 m (trailed into the bend; still
  braking at the zone's end counts), the exit speed inside 0.85–1.15 × the reference's, no reset inside. **TR-CH07** (Silver):
  the Driving School's braking lesson in its fixed loaner, the starter V01, T00-TRAIL-BRAKE (1512–1627 m), no handbrake. The
  reference (entered at 151.9 km/h, brake on at 1512.3 m, released at 1623.7 m, exit 61.6 km/h) publishes: **on by 1528 m,
  held to 1608 m, exit 52–71 km/h** (`Evidence/challenges/trials-TR-CH07.txt`).
- **Tests:** Core (inside the envelope; still braking at the end; braking late, releasing early, exiting fast, the handbrake,
  not crossed) — Core 202.
- **Built player (`tour.ps1 -TrialOnly TR-CH07`):** **PASSED** — on at 1512 m, held to 1624 m, exit 62 km/h, no handbrake;
  CH07 earned. `Evidence/challenges/trials-tour/trial-tour-TR-CH07.txt`.
- **Online:** refused, and kept off the online list — every trial on the Driving School is offline only (the control plane's
  `OnlineTrial`: no racecraft trials, cups, sections or tutorial-course trials online yet; found while writing this entry —
  the drill was not yet covered by the section refusal) — Services `ARacecraftTrial_IsRefusedOnline_ForNow` (+ TR-CH07), 369.
- **Limits:** it runs as a challenge trial beside the Driving School's own braking lesson (which stays a lesson).

## V-132 — CH23: four alternating recoveries in T00's countersteer zones — a drill (spec §11, slice 5) (2026-09-30)
- Revision: `0d61db4`, documented with the commit of this entry; the player build of `0d61db4`.
- **What:** the zone judge of V-123 now records **recoveries** in transition zones: a slide past 12° inside a zone caught back
  to 5° or less inside it or within 20 m after it, with the slide's direction; a reset or a spin is kept in the sequence as a
  break. The drill rule passes when every zone of the challenge is recovered in turn — one recovery per zone, consecutively,
  in route order, alternating directions — with no break between them (a reset later in the run does not undo a finished
  drill). **TR-CH23** (Silver): the Driving School's T00-COUNTER-1…4 slalom (264–584 m) in the rear-drive V04 Kestrel S; no
  targets. The autopilot drives it by sliding each zone as a separate drift (`AutopilotSlidesZonesOf`, automation only) at the
  drift skills 0.95 / 0.80 / 0.65 (the published reference skill: 0.65).
- **Measured:** no run makes the four in turn — at 0.65 a reset or spin follows the COUNTER-1 recovery; at 0.95 / 0.80 the
  controller re-flicks and recovers twice per zone, then spins. A trial of the zone tour's damped slides (V-123's knobs) on this
  drill recovered in every zone but still twice per zone with a break — reverted. A first measurement without break markers
  reported the four as made; it had missed the reset between them — corrected before publishing.
  `Evidence/challenges/trials-TR-CH23.txt`.
- **Tests:** Core `ZoneChainTests` (four alternating recoveries; the same way twice; a slide not caught in time; a spin or a
  reset between them; a reset after the drill) — Core 204.
- **Built player (`tour.ps1 -TrialOnly TR-CH23`):** judged at the three skills — **not passed** (10 / 19 / 11 recoveries, never
  four in turn without a break). CH23 is judged, not shown reachable. `Evidence/challenges/trials-tour/trial-tour-TR-CH23.txt`.

## V-133 — CH74: the six story records, then the C24 reference — a gated trial (spec §11) (2026-09-30)
- Revision: `ac6429d`, documented with the commit of this entry; the player build of `ac6429d`.
- **What:** a trial may require **story records** collected through Normal progression (`requiredStoryRecords`), judged from
  the profile's campaign clears exactly as the race diary shows them (the six authored radio / timing-slip records of
  `story/radio-records.json`, awarded after Normal S06, S12, S15, S17, S23, S25), and may race its course's **authored rival
  reference** ghost, whose own time is its target (`raceRivalReference`). **TR-CH74** (Gold): all six records, then beat the
  C24 reference — Satoshi Mibe's authored run in the V14, **240.846 s** — in the same stock V14 with that reference on the road
  as a ghost. The target is read from the reference ghost (a Core test keeps them equal); the trials page shows how many
  records the player holds.
- **Online:** refused (the control plane does not know a profile's story records; `OnlineTrial`) — Services 369.
- **Tests:** Core (the lock with 5 of 6; passes with 6; the target equals the ghost's time; its car is the ghost's) — Core 204.
- **Built player (`tour.ps1 -TrialOnly TR-CH74`):** on the tour's fresh profile the trial is **locked** — 3:56.838, faster
  than 4:00.846, yet "MISSED: the 6 story records collected through Normal progression (0 of 6)"; the tour then seeds Normal
  S01–S25 on its own profile (as the V-102 diary tour does; logged), the diary holds 6 records, and the same run **PASSED**;
  CH74 earned. `Evidence/challenges/trials-tour/trial-tour-TR-CH74.txt`.
- **Limits:** the positive case rests on seeded clears (the automation does not play the campaign to S25).

## V-134 — the 29-trial regression tour; CH37: the T00 merge beside a pace car (spec §11, slice 3) (2026-09-30)
- Revision: documented with the commit of this entry; the player builds named below.
- **Regression (built player of `ac6429d`, `bounds-audit.ps1 -Only 720p-large -Tours TrialTour`, all 29 trials, Text 150 % /
  HUD 130 %):** **PASS**, exit 0; every trial verdict line is identical to V-127's full tour (21 lines) and to the targeted
  runs since (V-128–V-133, 14 lines) — sorted and diffed. 22 of 29 trials passed by the autopilot; bounds: 79 labels over
  382 moments, 0 overflow, 0 missing glyphs, 0 fixed-size overflow. `Evidence/challenges/trials-tour/trial-tour.txt`.
- **What (CH37, committed in `fa5fa46`):** a merge judge in the racecraft judge — T00's two marked MERGE lanes (76–240 m,
  ±2.8 m, tolerance 1.6 m) are one merge span: entered over its start in the lane nearer the car, then every step to its end
  the car in that lane, the "merge" pace car within 15 m along the road and, while inside the span, in the other lane, and no
  touch. **TR-CH37** (Silver): the starter V01 beside one V01 pace car in the "merge" role (it holds the right-hand lane).
- **First built-player run** (build of this work before the fix): **not passed** — the merge broke at 240 m, the lane's last
  metre ("out of T00-MERGE-L (−1.1 m)"): the autopilot aims ahead of the car, so its held lane ended early. **Fix:** a line
  zone may be held a set distance past its end (`RouteFollower.LineZoneHoldMetres`) — 25 m for the merge pace car (it is
  judged to the lane's end, `RaceSimulation.MergeLaneHoldMetres`) and, in automation only, for the tour's autopilot
  (`OfflineRaceSession.AutopilotLaneHoldMetres`).
- **Built player (`tour.ps1 -TrialOnly TR-CH37,TR-CH49,TR-CH46`, the fixed build):** **PASSED** — "CH37 merge kept, both
  cars in their lanes, no touch" on lap 1 (lap 2's merge was not kept: the pace car was 19 m behind); CH37 earned. The two
  cars touched twice later in the race, outside the merge span — the rule judges the merge. Tests: EditMode `MergeTests`
  (side by side kept; out of lane, left behind, a touch not) — 2 of 2 passed in the editor at this revision. Offline only (a racecraft trial).
  `Evidence/challenges/trials-tour/trial-tour-TR-CH37-CH49-CH46.txt`.

## V-135 — CH49: an upshift at each of T00's three GEAR boards on one lap, manual gearbox — a drill (2026-09-30)
- Revision: documented with the commit of this entry (the drill itself in `419ed04`).
- **What:** the simulation records each upshift (lap, route metres); a trial may give the player a **manual gearbox**
  (`manualGearbox`, overriding the assist); **TR-CH49** (Bronze) in the starter V01 passes when one lap has an upshift in
  each board's window — 25 m before to 10 m after T00-GEAR-1/2/3 (1113, 1247, 1381 m). No targets.
- **First built-player run:** **not passed** — 1 of 3 (−4 m, missed, missed): the V01 has five gears and the autopilot
  took the lane in 4th, leaving one upshift. **Fix (automation only):** before the first board the autopilot takes the lane
  in a gear that leaves one upshift per board (top gear − 3 = 2nd), as a player would, and a board crossed during a shift
  waits for the gearbox instead of being lost.
- **Built player (the fixed build):** **PASSED** — 3 of 3 (−4 m, −4 m, −4 m); CH49 earned. Offline only (a Driving School
  trial). `Evidence/challenges/trials-tour/trial-tour-TR-CH37-CH49-CH46.txt`.

## V-136 — tunable trial loaners; CH46: a saved final-drive setup up T00's acceleration lane (spec §11, slice 5) (2026-09-30)
- Revision: `0a20b9e` (the model and the screen) and the commit of this entry (CH46).
- **What:** a trial loaner may be **tunable**: free alternative parts by slot (`choices`; the supplied part always stays
  allowed), the installed parts' tuning the player's to set (`tunable`), and a locked **PI budget** (`piBudget`). Core
  `TrialLoaners.ResolveSetup` resolves the player's setup like a garage build and lists why it is not legal (a part not among
  the trial's, a fixed loaner's tune, the resolver's issues, a PI over the budget), and what it sets (the final drive changed;
  aero at an end — the wing level at its top or the balance at either end of its range, for CH57). **Tune the Loaner**
  (`TrialTuneScreen`, from the trial's page): each part button fits that slot's next part, −/+ rows for the installed parts'
  controls, the PI against the budget, "Save This Setup" enabled only for a legal setup. The Local profile keeps one setup per
  trial (`TrialSetups`, validated; `LocalProgression.SaveTrialSetup`, change kind `TrialSetupSaved`, no money); every run of
  the trial races the saved setup (an illegal one races as supplied and fails "your setup legal"). Tunable trials are offline
  only for now (`OnlineTrial`). **TR-CH46** (Bronze): the starter V01 with the free final-drive kit (GBX-T1-FINAL) as its
  choice; rules: a legal setup, the final drive changed; T00-ACCEL-START → T00-ACCEL-END inside **20 s** — the Driving
  School gearing lesson's own bar for that timed straight (a Core test reads it from `lessons.json`).
- **Tests:** Core (a tunable loaner takes the player's parts and tune within its rules; the budget and the aero ends incl. the
  wing level; a setup saved only when legal, copied, free) — Core 207; Services 369 (one full run had an intermittent failure
  in `SocialStoreTests.HandleUniqueness_IsCaseInsensitive_AndAtomicUnderConcurrentClaims`, unrelated to this work: it passed
  alone three times and in two further full runs).
- **Built player (the fixed build, above):** the loaner as supplied — "MISSED: your tune changes the final drive" (lane
  0:12.700); then Tune the Loaner by buttons only: the kit fitted, FinalDrive 1000 → 1060 (6 steps), PI 220 → 219, legal,
  saved with the profile; the next run raced it — **PASSED** (lane 0:12.633); CH46 earned. Screens: `02-TR-CH46-tune-*.png`,
  `04-TR-CH46-verdict.png`. `Evidence/challenges/trials-tour/trial-tour-TR-CH37-CH49-CH46.txt`.
- **Limits:** the 20 s bar is loose for the autopilot (12.6 s); it is the lesson's published standard, kept as CH46's
  "complete the lane". The aero-level part of "aero at an end" was added after that build (Core-tested; no aero trial yet).

## V-137 — CH57: C15 with a tune-budget loaner, aero not at an end (spec §11, slice 5) (2026-09-30)
- Revision: `3fa956e` (content, tour setup), documented with the commit of this entry.
- **What:** **TR-CH57** (Gold) on C15, damp: the V09 Crestline 26 supplied with the adjustable wing (AER-T3-GTWING), tunable,
  free choices touring or sport tyres and coilovers, **PI budget 530**; rule `aeroNotAtExtreme` (the wing level below its top,
  the balance inside its range). The budget binds: sport tyres alone are PI 555; touring tyres with coilovers are 531–532 and fit
  only with the wing at its top (529) — which the aero rule forbids — so the player picks one (Core-tested). Gold measured with
  the loaner as supplied (`MeasureChallengeTrials`, 141.415 s → **144.3 s**, 1.02 ×; `Evidence/challenges/trials-TR-CH57.txt`).
  C15 marks its three late-apex corners but has no CH57 exit gates, so the "combined grip/exit target" is its measured Gold time
  (the route is not edited: that would change C15's source hash).
- **Tune the Loaner:** the tuning rows now page (**More Controls**, six a page) — a coilover alone has nine controls; the
  tour pages to a control before its row. No tour setup has more than six controls yet, so the paging is built, not exercised.
- **Built player (`tour.ps1 -TrialOnly TR-CH57,TR-CH46`):** as supplied **PASSED** (PI 504, 2:21.415 — the measured time
  exactly); then Tune the Loaner by buttons: touring tyres, AeroLevel 1000 → 900, AeroBalance 440 → 450, PI 522, legal, saved —
  **PASSED** (2:20.187); CH57 earned. TR-CH46 again PASSED with its shortened brief.
  `Evidence/challenges/trials-tour/trial-tour-TR-CH57-CH46.txt`, `02-TR-CH57-tune-*.png`, `04-TR-CH57-verdict.png`.

## V-138 — CH56: C23 inside a locked PI budget; a busy ghost file no longer ends a race's results (2026-09-30)
- Revision: `3fa956e`, documented with the commit of this entry.
- **What:** **TR-CH56** (Gold) on C23: the V11 Vector MR, free challenge parts — intake or exhaust, the final-drive kit or
  close gears, sport suspension, street tyres — inside a **locked PI 615** (the exhaust alone, or the intake with street tyres,
  go over it; Core-tested). Gold measured as supplied: 220.232 s → **224.7 s** (`Evidence/challenges/trials-TR-CH56.txt`).
- **First built-player run:** both runs judged PASSED, but the tour **FAILED**: after the tuned run `LocalGhosts.Offer` threw
  `IOException` ("Unable to remove the file to be replaced") from `File.Replace` of the stored C23 ghost, which ended the
  race-finish coroutine before the results page (the profile had already saved the verdict).
  `Evidence/challenges/trials-tour/trial-tour-TR-CH56-first.txt`. **Fix:** the ghost write falls back to a copy over the stored
  file and, if that fails too, reports "ghost not saved" (logged) instead of throwing into the race flow.
- **Built player (the fixed build):** as supplied **PASSED** (PI 590, 3:40.232); tuned by buttons — intake, final-drive kit,
  FinalDrive 1040, PI 613 of 615 — **PASSED** (3:39.516); CH56 earned; no ghost error this time (the IOException did not recur,
  so the fallback path itself was not exercised). `Evidence/challenges/trials-tour/trial-tour-TR-CH56-CH59.txt`.
- **Limits:** the predicate says the server validates every installed parameter; tunable trials are **offline only** so far —
  online, the control plane refuses them (`OnlineTrial`) and the game server races only the supplied loaner. Planned: the
  setup sent with Ready, validated by the control plane with `TrialLoaners.ResolveSetup`, a per-player loaner build in the
  assignment, and the game server filling the setup facts.

## V-139 — CH59: six V10s at one PI on C19 with the player's own tune — a racecraft trial (2026-09-30)
- Revision: `3fa956e`, documented with the commit of this entry.
- **What:** **TR-CH59** (Gold) on C19: five stock V10 Spiral RXs ("field") and the player's V10, tunable from the provided
  parts — the lip, a brake kit, the adjustable diff, the final-drive kit or close gears — inside **PI 580, the field's own**
  (equal PI); win. The brake kit alone is 581 (the lip pays for it); the final drive moves PI non-monotonically (3 % shorter is
  584, 6 % shorter 580) — Core-tested.
- **Built player:** as supplied **PASSED** — P1 from the grid, no contact, no wall, no reset; tuned by buttons — the diff and the
  final-drive kit, FinalDrive 1060, PI 580 — **PASSED**, P1; CH59 earned. The player does not start last (the predicate names
  no grid), so the autopilot leads from the front; the win is shown, not a comeback.
  `Evidence/challenges/trials-tour/trial-tour-TR-CH56-CH59.txt`. Offline only (racecraft and tunable).
- **Tests (this block):** Core 207 (CH56, CH57, CH59 content and budgets); Services 369; all .NET suites passed at this revision.

## V-140 — CH02 and CH47: Driving School braking-lane lessons on T00 (spec §11 with Addendum 02 §10.1) (2026-09-30)
- Revision: documented with the commit of this entry; the player build of `a00370c` + this work (the lesson panel's live
  count/difference and a dash in its goal line were changed after that build — rendered in the next built run).
- **Reconciling the rules:** CH02 names "T00 braking lane … in one lesson" and CH47 "the garage comparison harness … then
  finish its braking lesson", while Addendum 02 §10.1 says the Garage Test Yard grants no reward or challenge completion.
  Both are therefore **Driving School lessons**, launched from Challenge Trials as a new trial kind **"lane"**: T00's braking
  lane (the area beside the loop: speed gate T00-BRAKE-SPEED at 150 m, the 8 m box T00-BRAKE-BOX at 232–240 m, CH47's window
  T00-BRAKE-COMPARE at 180–300 m), driven with the same simulation and the Test Yard's lane station and stop measurement
  (`TestYardSession` in a lesson mode: the lane only, the trial's supplied car as A and — for a comparison — the same car on
  the loaned package as B, no station or surface switching, every start kept). The Test Yard itself still records nothing.
- **Judged (Core `TrialJudge`, kind "lane"):** each start from rest counts once — its speed crossing the speed gate and where
  its first full stop came to rest. **TR-CH02** (Bronze, V01): 3 starts past the speed gate at ≥ 100 km/h, each stopped inside
  the box (a miss may be followed by another start). **TR-CH47** (Bronze, V01): a stop on the supplied tyres and one on a loaned
  sport tyre package (TYR-T2-SPORT), each from ≥ 100 km/h inside the lesson window; the panel and the verdict show the measured
  difference. A pass is kept by `LocalProgression.ApplyLessonTrial` — the trial kept, its challenge granted exactly once — with
  **no event, no record and no payout** (a lesson is not a race); a lane trial offered through race facts is refused. Offline
  only (T00 trials are not offered online).
- **Tests:** Core (the box stops: a miss allowed, two starts, one below 100 km/h, none; the comparison and its difference text;
  the gates tagged in T00's lane; the lesson pass kept and granted once with only the challenge cash and no record; refused for
  another car, a race drill or through race facts) — Core 209.
- **Built player (`tour.ps1 -TrialOnly TR-CH02,TR-CH47`; the page buttons, then scripted straight-line starts — full
  throttle, full brake when the stop predicted from the deceleration measured so far reaches the middle of the stop gate):**
  TR-CH02 **PASSED** — three starts, 109 km/h at the speed gate, stopped at 233.0 / 236.3 / 236.3 m (inside 232–240); CH02
  earned. TR-CH47 **PASSED** — supplied 52.3 m, loaned sport tyres 49.8 m from 117–118 km/h (−2.5 m); CH47 earned.
  `Evidence/challenges/trials-tour/trial-tour-TR-CH02-CH47.txt`, `03-TR-CH02-lesson.png`, `03-TR-CH47-lesson.png`.
- **Limits:** "server verifies speed before each start" is done by the Local judge from the simulation; online there is no
  lane lesson yet. The automation drives straight lines; a human's steering is not exercised.

## V-141 — CH60: the ten named tuning demonstrations, each with a driving check — the last challenge (spec §11) (2026-09-30)
- Revision: `b733f38` (content, rule, tour setups), documented with the commit of this entry; the player build of `b733f38`.
- **What:** a CH60 **group of ten trials** ("The Complete Notebook — …"), each a tunable T00 loaner supplied with the part
  that exposes one system's control, a rule `changedControls` (the saved setup changes that control from its default; the
  setup's changed keys are a fact, Core `TrialLoanerBuild.ChangedKeys`) and a **driving check** on a marked Driving School
  section. The ten, pairwise distinct controls over five slots: final drive (V01 + GBX-T1-FINAL, acceleration lane), gear
  spread (V01 + six-speed conversion GBX-T2-SIX — the close-ratio box does not fit a 5-speed; configuration 2), brake bias
  (V01 + BRK-T2-KIT, configuration 1 — its configuration-3 reference reset outside the section, so it moved), differential
  lock (V01 + DIF-T2-RWD-ADJ, comparison B), all-wheel-drive split (V07 + DIF-T2-AWD-CENTRE, comparison A), spring rates,
  dampers, anti-roll bars, ride height (V01 + SUS-T2-SPORT; configuration 1, comparison A, comparison B, configuration 2) and
  aero balance (V01 + AER-T4-FULL, configuration 3). CH60 is earned when all ten pass (the group rule). The checks are
  **1.10 ×** each section's measured time with the part at its defaults (`Evidence/challenges/trials-TR-CH60.txt`; the file's
  method records this CH60 factor — a clean run after a tune, not a medal).
- **Tests:** Core (ten in one group; no control shared; ≥ 5 slots; each loaner resolves with its named controls; a changed
  control passes the rule and an unchanged setup does not) — Core 209.
- **Built player (`tour.ps1 -TrialOnly TR-CH47,TR-CH60-*`):** every demonstration as supplied was refused ("MISSED: your tune
  changes … (nothing changed)"), then tuned on Tune the Loaner by buttons — including **More Controls** for the ride height
  (the seventh suspension control, page 2 of 2) — saved and raced: **all ten PASSED**; CH60 earned with the tenth. TR-CH47
  passed again with the live "measured difference" line on the lesson panel (−2.5 m). Tour PASS.
  `Evidence/challenges/trials-tour/trial-tour-TR-CH60.txt`, `02-TR-CH60-HEIGHT-tune-saved.png`, `03-TR-CH47-lesson-live.png`.
- **R11.3:** with CH60 every one of the 75 challenges is judged somewhere (V-084 … V-141).

## V-142 — tunable trials online (CH56's "server validates all installed parameters"); trials race their course's own surface offline (2026-09-30)
- Revision: `ea3e54b` (online code), `fa8fdfb` (surface fix, re-measured targets), documented with the commit of this entry.
- **Online tunable trials:** `event.ready` may carry the player's `trialSetup` (parts by slot, tuning by control). The control
  plane resolves it with Core `TrialLoaners.ResolveSetup` from its own parts data (`GarageService.TrialSetupBuild`) — every
  installed part must be among the trial's, every setting valid for the installed parts, the PI inside the budget — and
  refuses readiness otherwise (`trial_setup_invalid`; a setup for a trial supplied as it is: `trial_setup_unexpected`). The
  validated setup is kept per member with the proposal (dropped on un-ready) and re-resolved at the start into that member's
  own frozen loaner build (others race the supplied one). The game server re-resolves the frozen build (hash verified) and
  fills the setup facts itself (legal, PI, final drive changed, aero at an end, changed controls); `DroveLoaner` accepts the
  member's validated setup. The client keeps an online account's setups on this PC beside the Local profiles
  (`OnlineTrialSetups`) and opens Tune the Loaner from the Convoy page ("Tune the Loaner" while a tunable trial is proposed).
  Now offered online: tunable trials on ordinary courses — TR-CH56 (C23) and TR-CH57 (C15); racecraft (CH59), Driving School
  (CH46, CH60) and lane lessons (CH02, CH47) stay offline. **Found and fixed on the way:** readying for a trial checked the
  member's *own garage car* against the loaner's class cap (a member in a high-PI car could not ready for CH54 online) — a
  trial races its loaner, so that check is skipped.
- **Tests:** Services 372 (a tunable trial proposed; a member's setup kept at ready and frozen as that member's loaner, a
  foreign-car build ignored; the member's own car cap ignored for a trial; un-ready drops the setup; `TrialSetupBuild` refuses
  a part over the budget, a part not offered and an out-of-range setting, and resolves a legal setup to its own hash).
- **Surface parity (found by the first online run):** an offline trial or cup leg in "course" conditions set a **null
  surface, which meant dry grip** in the Local race and in `MeasureChallengeTrials` (whose report still printed the course's
  surface), while the online server races the course's own surface. So C08 (wet), C15 and C20 (damp) trials had been
  measured and toured dry: the first online TR-CH57 run (damp) was 2:26.909 against a dry-measured 2:24.300 — not passed
  (`Evidence/challenges/trials-online/online-TR-CH57-first.txt`). Now a null surface resolves to the course's route surface
  after the course loads, offline and in the measurement. Re-measured: **TR-CH51** (wet C08) 169.614 s → Silver **186.6 s**
  (was 169.2); **TR-CH57** (damp C15) 148.456 s → Gold **151.5 s** (was 144.3) (`Evidence/challenges/trials-TR-CH51-TR-CH57.txt`).
  TR-CH43 (damp C20) races R32's reference time, unchanged. No cup leg is on those courses. Local Freeplay (no weather choice)
  still races dry on those courses while online Freeplay uses the course surface — flagged as a separate task, not changed here.
- **Built players:** offline (`tour.ps1 -TrialOnly TR-CH51,TR-CH57,TR-CH43`, build of `fa8fdfb`): TR-CH51 **PASSED** in the
  wet (2:49.615 < 3:06.6); TR-CH57 **PASSED** as supplied (2:28.45) and tuned (2:26.89 — the online time); TR-CH43 not reached
  (3:04.521 damp). `Evidence/challenges/trials-tour/trial-tour-surfaces.txt`. Online (`ui-tour-online.ps1 -Intent 5
  -ChallengeTrial TR-CH57`, control plane restarted for the new code and content, server bound and advertised 127.0.0.1):
  Tune the Loaner from the Convoy page by buttons (touring tyres, AeroLevel 900, AeroBalance 450, PI 522), Event Ready with
  the setup, the server raced the frozen tuned build `4fabc32160a0` (hash verified) — **PASSED** 2:26.909 < 2:31.500; CH57
  earned online. `Evidence/challenges/trials-online/online-TR-CH57.txt`, `05u-online-tune-saved-TR-CH57.png`.
- **Found on the way:** the online tour client had no `-nsLocalProfiles`, so the first run's online setup was written into
  the game's real Local data folder (dev account 0's file, created by that run) — removed; `ui-tour-online.ps1` now gives the
  client a fresh `local-data` folder (the second run's tour step had tripped over that stale setup; the trial itself passed —
  `online-TR-CH57-second.txt`).
- **Limits:** TR-CH56 is offered online by the same path but was not run online here (only TR-CH57); one human per run.

## V-143 — the 47-trial regression tour; EditMode; the CH60 list rows fitted at Text 150 % (2026-09-30)
- Revision: the player build of `fa8fdfb` (code of `118d778`) for the regression; `1b10fcd` + the bounds-script fix for the
  re-audit; documented with the commit of this entry.
- **Regression (`bounds-audit.ps1 -Only 720p-large -Tours TrialTour`, all 47 trials, buttons only, Text 150 % / HUD 130 %):**
  **PASS**, exit 0; 40 of 47 trials passed by the autopilot and the 26 challenges expected (CH02 … CH74) earned. Every verdict
  line equals a line of an earlier run (V-127 and V-128 … V-142 — sorted and compared both ways); the only earlier lines not
  reproduced are CH43/CH51/CH57's dry-surface results superseded by V-142. Not passed, as before: CH13, CH23, CH28, CH36,
  CH40, CH41, CH43. `Evidence/challenges/trials-tour/trial-tour.txt`.
- **Its bounds audit found 21 overflows** (109 labels over 663 moments; 0 missing glyphs): every one the same three CH60 rows
  of the trial list ("CH60  THE COMPLETE NOTEBOOK — ALL-WHEEL-DRIVE SPLIT  3 OF 10" and two more, up to 84 px past their
  576 px box while the group is in progress) — `Evidence/ui/bounds/bounds-TrialTour-1280x720-text150-47trials.txt`. **Fix:**
  shorter demonstration names (Diff lock, AWD split, Roll bars, …). `bounds-audit.ps1` takes `-TrialOnly` for a targeted
  TrialTour (a first attempt passed the extra arguments wrongly and did not start — fixed). **Re-audit of the ten CH60 trials:**
  all ten PASSED again, CH60 earned; **0 overflow**, 0 missing glyph (91 labels over 187 moments; 16 edge spills and 12 at
  minimum size, which are not failures). `Evidence/ui/bounds/bounds-TrialTour-1280x720-text150-ch60.txt`.
- **EditMode (editor, code of `118d778`):** 494 passed, 0 failed, 2 skipped (the explicit V-087 contact experiments).
  .NET at `118d778`: Core 209, Toys 92, Builds 232, Services 372.

## V-144 — CH56 online: a tuned setup inside the locked PI budget, validated by the control plane and the game server (2026-09-30)
- Revision: the player build of `1b10fcd`; the control plane restarted on `674eba8` (the trials content changed since the
  last start); documented with the commit of this entry.
- **Built players (`ui-tour-online.ps1 -Intent 5 -ChallengeTrial TR-CH56`, server bound and advertised 127.0.0.1, dev
  account 0, the client's Local data in a fresh folder):** Tune the Loaner from the Convoy page by buttons — the intake, the
  final-drive kit, FinalDrive 1040, PI 613 of 615, legal — then Event Ready with the setup; the control plane validated it
  and froze it; the game server raced the frozen build `9ba59ae601f9` (hash verified — the same build the offline run raced)
  and judged it itself: **PASSED**, 3:40.805 < 3:44.700 (offline 3:39.516); the settled receipt holds CH56 earned.
  `Evidence/challenges/trials-online/online-TR-CH56.txt`. With V-142's TR-CH57 run, both tunable trials offered online are
  shown; CH56's "server validates all installed parameters" is met by the control plane (at readiness) and the game server
  (at the finish).
