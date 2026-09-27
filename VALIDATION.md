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
