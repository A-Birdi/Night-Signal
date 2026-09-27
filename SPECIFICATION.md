# NIGHT SIGNAL: MOUNTAIN CIRCUIT
## Master implementation assignment • revision 1 • 26 September 2026

Working title only; no claim of trademark clearance. Build an original full-3D multiplayer arcade racing game with the atmosphere of mountain-pass rivalries, nighttime car culture, and expressive driver personalities. Initial D is an experiential reference, not an asset library, a licence, or a request to reproduce its characters, cars, tracks, story, branding, music, or interface. The horizontal social-notification reference to Kingdom Hearts II concerns timing and readability only; design original graphics and sounds.

This is a NEW project, separate from The Road of Borrowed Names, Ember Duel, and every other existing repository. Do not import their interface, account systems, restrictions, source, or assets by default. In particular, this project is NOT required to be a single HTML file, entirely procedural at runtime, or serverless. Rich reusable 3D assets are appropriate.

Read this entire specification, including every catalogue and acceptance test. Deliver an implemented game and its source, not just a proposal, component scripts, a project generator, a multiplayer-looking menu, or a single polished test course. Work through the complete assignment in recoverable phases. Passing an early integration gate is not completion or a request for routine approval. A genuine missing credential, installation permission, billing decision, or inaccessible tool is a specific blocker to report honestly, not permission to substitute fake multiplayer.

Structural rules and content minimums below are binding. Handling constants, art budgets, race benchmark times, and income pacing identified as initial tuning targets may be refined based on measurements, with a documented reason. Do not quietly change player capacity, remove courses, substitute reskins for characters, alter the progression formula, or relabel unfinished work. Prefer the smallest architecture that actually fulfils these requirements.

## 1. Product definition and non-negotiable scope

The core loop is: create a driver identity; choose and tune a car; gather a convoy; race or drift together; earn money and mastery; return to a beautiful car meet or garage; show what you have earned; and face a new rival who makes you reconsider your line rather than simply buy more horsepower.

Required release content:

- 1–6 simultaneous HUMAN entrants. No split-screen requirement. One player may use the same campaign alone. Multiplayer over a real network is mandatory.
- No more than SIX registered active racers in one race: humans plus live AI. Disqualified slots are not refilled midrace. Non-colliding recorded replay overlays are explicitly NOT entrants, cannot place, cannot collide, and cannot earn rewards.
- 26 genuinely distinct authored 3D courses: T00 tutorial, C01–C24 regular courses, C25 dedicated finale. A reverse route, wet variant, or new scenery seed does not count as a new course.
- 30 ordered competitive campaign stages per mode: 24 regular races, four lieutenant boss races, one penultimate mastery race, one final boss race. Tutorial T00 is separate. Four lieutenants and the penultimate reuse specified courses rather than inflating the unique-course count.
- A complete Normal campaign and a meaningfully changed Hard campaign, with different final rivals. Hard unlocks per player after their first legitimate Normal finale clear.
- Exactly 48 authored rival identities in the launch roster, including all four lieutenants and both final rivals. They need recognizably different looks, personalities, cars/liveries, and observable driving tendencies.
- 18 distinct fictional purchasable car models, three starter choices, meaningful tuning/parts, and a full visual customization workflow. Each model must be a distinct body design, not the same mesh with headlights changed.
- Exactly 75 named launch challenges with 75 distinct, permanently claimable-once cosmetic rewards. The challenge catalogue in this document is required content, not sample filler. No challenge awards a performance part or makes a mechanical advantage exclusive.
- A persistent online Player Card, account progress, owned cars/parts/cosmetics, wallet, campaign flags, challenge flags, and finite mastery Rank Points.
- A 12-person instanced car meet, separate from the six-person convoy and six-entry race limits. Public meet admission does not require convoy membership. More users go to another instance, never an unbounded room.
- Original arcade-racing UI, original music and sound, controller and keyboard support, readable accessibility options, a structured tutorial, ghosts, results, spectating, reconnection rules, and verified builds.

Primary target is a native Windows 64-bit Unity game, with Windows development-server and Linux dedicated-server builds. Windows-native client plus real multiplayer is mandatory. A browser build is a secondary target, not a second implementation and not an excuse to compromise the primary game. Do not claim GitHub Pages playability until an actual Unity Web build and its network path pass testing. GitHub may hold source and release artefacts independently of whether Pages is used.

Out of launch scope: open-world traffic, police chases, injury simulation, licensed manufacturers, real-money purchases, loot boxes, battle passes, voice chat, a public vehicle-image uploader, vehicle trading, MMO persistence, VR, split-screen, consoles, a full esports ranking service, and arbitrary player-created tracks. Leave sensible extension points without building empty interfaces for these features.

## 2. Resolve the six-racer conflict openly

Six humans plus an additional live AI boss is seven racers. Do not conceal that arithmetic or reduce human capacity to five.

Use the following precise design:

1. Campaign stages have a published, versioned rival benchmark: a target time for race stages and, only where explicitly specified, a raw drift or mixed-mastery target. An authenticated server locks the benchmark, weather, car cap, rival identities, and course version into the event proposal before readiness.
2. With 1–5 humans, fill the remaining grid positions with live AI up to six total entrants. Include the stage's featured rival first, then the authored support pool. They obey the same vehicle limits and geometry. Finish order determines the on-track result and placement payout; the published benchmark determines campaign qualification. Distinguish those two outcomes in the briefing and results.
3. With six humans, run a six-human grid. The featured rival appears in the story and as a translucent, non-colliding certified pace replay, labelled “Rival benchmark — replay, not an entrant.” The HUD shows sector deltas against it. It is not a seventh live AI car and is not listed as a seventh placing driver. Let players hide the visual replay while retaining its timing line.
4. Preserve the boss personality through the fixed driver, bespoke intro, livery, pace/line replay, sector goals, and postrace reaction. Do not substitute an anonymous stopwatch or mislabel the full-convoy mode as a six-versus-one live race.
5. Benchmarks come from completed legal reference runs on the shipped course and handling version. Use a reference build that can actually achieve them. Validate the replay is physically plausible. A spline animation with a typed-in impossible time is not a certified benchmark.
6. Match the lower-party live rival's calibrated pace to its reference target; live traffic can still affect its outcome. The interface must explain “won the race” versus “earned the stage clear” when those differ. Campaign car-to-car collision defaults to off, reducing both obstruction and party-size inconsistency.

This is an intentional product compromise that preserves six human participants and the hard six-entrant cap. Do not later “fix” it by creating an invisible seventh simulated entrant. Freeplay supports real mixed grids independently and always observes the cap.

## 3. Technical architecture and trust boundaries

### 3.1 Engine and authoring

Use Unity 6.3 LTS as the baseline, selecting an actually installed, supported patch after inspecting Unity Hub and package compatibility. Pin the exact editor version in ProjectVersion.txt and packages in manifest.json/packages-lock.json. Do not automatically upgrade unrelated Unity projects. A different supported Unity 6 release requires a concrete compatibility reason recorded before adopting it; no experimental engine versions.

Use Universal Render Pipeline, C#, the Input System, and one coherent primary UI system. Default to uGUI plus TextMeshPro for this controller-first arcade interface, with reusable prefabs, proper navigation, masks, and layout rules. Do not mix three UI stacks to solve minor styling problems. Use Timeline/Cinemachine or equivalent existing Unity facilities for scene cameras where useful; pin actual compatible versions.

Author assets through Unity editor tooling, authored meshes, original textures, modular environment kits, reusable material libraries, and optional Blender scripts. Procedural generation is a production tool, not a justification for generic blocks or changing course geometry on every launch. Bake and version finished content. Preserve source assets and deterministic recipes. Use original content or assets whose permissions are verified; include a provenance register. Ask before any paid asset/service. Do not redistribute restricted source assets through a public repository.

### 3.2 Chosen online topology

Use a dedicated authoritative game server, not the convoy leader's game process, as the source of race truth. The server may run on the user's machine for development or an explicitly provisioned remote machine for internet play. “Dedicated” means a separate authoritative process; it does not require purchasing dedicated physical hardware.

Default implementation stack:

- Unity Netcode for GameObjects plus Unity Transport for gameplay messages, spawned objects, and authoritative simulation. Pin compatible versions, verify their actual features, and use custom vehicle prediction/reconciliation as required.
- A small ASP.NET Core control-plane service on a supported .NET LTS version, with HTTPS APIs and an authenticated control/presence channel. This handles convoys, invitations, readiness, event allocation, account-facing requests, receipts, and directory data. Use established WebSocket/SignalR facilities according to verified Unity-client compatibility; do not stream vehicle transforms through the database or a generic chat channel.
- Supabase Auth for account creation, sign-in, reset/recovery, and token lifecycle; PostgreSQL for game records. Supabase can provide both the local development stack and the managed Auth/database deployment. Do not also add Firebase, PlayFab, Nakama, and Unity account services for the same jobs.
- The control service verifies identity tokens using the provider's supported validation mechanism, including signature, issuer, audience where applicable, expiry, and subject. Prefer asymmetric signing keys with trusted JWKS and a maintained validation library. Never decode a JWT and treat the decoded text as proof of identity.
- Gameplay servers receive short-lived, audience-bound, single-use connection tickets issued by the control plane. Tickets bind account, convoy, match, role, build/protocol/content version, and expiry. Authenticate the issuing control plane; do not trust a caller-supplied player ID.
- Persist wallet changes, ownership, rank, and challenge unlocks through server-owned transactions. Clients can request purchases or cosmetic selections, never set a balance or submit an authoritative finish time. Database row/security policies and service roles must enforce this even when someone bypasses the UI.

Keep these behind small interfaces, not an overgeneralized framework: Identity, PlayerStore, ConvoyDirectory, MatchAllocator, ResultLedger, ContentCatalogue, and GameplayTransport. Public endpoint configuration may be in the client; service-role keys, database passwords, receipt-signing keys, and host-admin tokens may not.

A local integration environment must run actual account/database/control/game-server components, not a fake multiplayer simulator. Development test accounts and any bypass authentication must be confined to isolated local/test configuration and impossible to activate on the production endpoint. A documented local Supabase CLI/container workflow is preferred. System installation or Docker enablement still requires user approval. Do not silently use a personal production database for tests.

### 3.2a Account lifecycle and player data

Use Supabase's email/password account flow as the concrete initial sign-up choice, with verification, sign-in, sign-out, password reset, expired-session recovery, and an explicit account-deletion request path. Configure email delivery and approved reset/verification return URLs for the native application; do not claim an email was sent when the configured environment only captures development mail. A hosted verification page may complete email confirmation before returning the user to the native sign-in screen. Never collect or persist passwords in custom game code beyond passing them to the provider's supported API over TLS. Store refresh credentials using the platform's protected credential facility where practical, not in public PlayerPrefs or Git. Redact credentials from logs.

Account identity is not the editable display name or Player Card. Restore owned items and progress after signing into a different device. Keep graphics, audio, input bindings, and last local screen separate from server-owned progress. Use versioned writes and explicit resolution of conflicting cosmetic drafts; never accept a whole uploaded profile as an authoritative replacement for wallet/unlock records. One account may have one active driving session: a second sign-in may inspect menus, but taking over driving requires a clear consent flow and cannot create two reward-eligible entrants. Do not punish the older client without explaining the takeover.

No identity credential is required to inspect an offline tutorial/practice demo, but an authenticated connection is required for online convoys, car meets, saved online progression, and rewards. Guest practice must be clearly labelled and cannot later upload invented earnings. The campaign's required release mode is the authenticated, persistent one, including when played solo against AI.

### 3.3 Leader is not authority

The convoy leader chooses destinations and initiates launches. The leader does not own wallets, verdicts, checkpoint validation, other players' menus, AI physics, or the race clock. Losing the leader should not end a race when the dedicated server is healthy. This separation avoids complicated race-host migration being necessary for the primary topology.

A relay is transport infrastructure, not automatically a trusted referee, persistence backend, or anti-cheat system. Do not substitute a player-authoritative relay session and call it equally trustworthy without explicit approval and a documented change in security guarantees.

### 3.4 GitHub Pages and optional Web target

GitHub Pages can host static client files or a landing page; it cannot execute the game server, account service, or live database. Browser networking must use a compatible browser transport such as WSS; raw UDP/native sockets are not available to a Web client. Do not point a Unity Transport client at an unrelated generic WebSocket server and assume protocol compatibility.

When the Web target is explicitly undertaken: compile the SAME Unity game, use the compatible Unity Transport WebSocket configuration and tested native/Web crossplay path, arrange HTTPS/WSS endpoints, exact-origin CORS for HTTP APIs, Origin checks for WebSockets in addition to authentication, relative project-subpath URLs, correct MIME/compression handling, and cache/version invalidation. Test on the actual hosted route. Use Unity decompression fallback or a correctly configured host as appropriate, not undocumented GitHub header settings. Respect current Pages size/bandwidth limits and verify large-file handling. Do not load 26 courses into browser memory at once.

Browser deployment and mobile support are separate claims. A desktop Web build does not establish acceptable phone performance, on-screen controls, storage, or background-tab connectivity. The initial mandatory target remains Windows. No browser code path may weaken the authenticated server rules.

### 3.5 Operating costs and permissions

Produce a concise cost/operations worksheet after the first real six-client test: peak/idle server memory, CPU, outgoing bandwidth, database storage, account/email dependencies, concurrency assumptions, and dated provider estimates. Report service free-tier limits as conditional, not “free forever.” Do not assume Claude credits pay for game hosting. Obtain approval before opening paid accounts, enabling auto-reload, provisioning public servers, changing firewall/router rules, publishing a site, or uploading restricted assets.

A local network test is not proof that friends across the internet can join. A working internet test is mandatory release evidence; when credentials or an approved deployment are missing, preserve a runnable local stack and mark external-network acceptance BLOCKED rather than claiming multiplayer completion.

## 4. Multiplayer state model and independent menus

Keep application navigation, convoy coordination, scene presence, and race participation as separate state machines.

- Local screen: Home, CampaignMap, FreeplaySetup, Garage, Dealership, Challenges, PlayerCard, Settings, Results, MeetOverlay, Spectate.
- Presence: InMenus, JoiningMeet, AtMeet, DepartingMeet, LoadingRace, InRace, Spectating, Reconnecting, Offline. Publish only coarse status; do not expose account-management screens, typed text, wallet details, or private selections without consent.
- Convoy coordination: Idle, ProposalOpen, ReadyCheck, Allocating, Loading, Intro, Countdown, Racing, Results, Returning.
- Entrant status: Reserved, Loading, Loaded, ReadyForStart, Racing, Finished, DNF, DQDisconnected, DQQuit, Spectator. Not all convoy members are necessarily entrants after a midrace join/reconnect.

One account may belong to one active convoy and one active race entrant identity. A convoy is at most six members. One character cannot simultaneously occupy a public meet and a race. Keep the lightweight control/presence connection while changing gameplay scenes; release old scene membership and parking reservations reliably.

Store stable player IDs separately from display names. All shared changes carry a monotonic revision and request ID. Discard stale changes; make retries idempotent. Do not infer role or authority from a position in an array.

### 4.1 Convoy home

Create/join by invite code or friend invitation. Support private/invite-only and discoverable convoys with clear privacy labels. Codes must expire or be revocable, rate-limited against guessing, and not reveal public IP addresses. The host label in player-facing UI means “Convoy leader.”

The persistent menu header shows own driver name/card/rank/currency, current vehicle, six convoy positions, leader crown plus text, each member's connection status, readiness, and current broad activity. Inspecting a member opens their public card without changing that person's screen. Pending invites, empty slots, errors, and reconnect states have distinct presentations.

Players independently use Garage, Challenges, Card, or the meet. The leader opening a menu alone sends presence, not a forced scene change. Unsaved paint previews remain local drafts and cannot silently become a race loadout. The top convoy strip stays visible in all ordinary menus, with compact expansion when space is constrained.

### 4.2 Two purposeful ready checks

Ready is an explicit consent tied to a proposal revision, not a global permanent checkbox.

First check: the leader proposes “Enter Normal/Hard Campaign Map” or “Open Freeplay Event.” Members receive a clear request wherever they are, including the meet. When every current connected convoy member, including the leader, consents and remains eligible, the leader may commit that destination. All clients then transition to the shared campaign map/event briefing. Nobody is pulled simply because the leader clicked Campaign.

Second check: after a concrete stage/course, mode, weather, AI count, car cap, collision rule, and roster are selected, each entrant approves THAT event. The UI states the event, own validated car, entry eligibility, and whether six-player benchmark presentation applies. Server revalidates all readiness and frozen loadout hashes atomically when the leader presses Start. Never charge an entry fee.

Changing the course, difficulty, mode, weather, participant roster, target, AI count, or collision rule invalidates event readiness for everyone. Changing a player's performance loadout invalidates that player's readiness and the proposal's corresponding loadout revision. Cosmetic-only changes can remain ready until event allocation freezes all visual/vehicle data. Audio/accessibility settings do not unready a player. Joining/leaving a convoy requires a new roster revision, so old ready votes cannot start a changed group.

Ready members may continue using non-mutating menus or walking at the meet until allocation, with a persistent “Ready for …” indicator and a clear Unready control. The leader can request readiness at most once every 15 seconds. No forced ready, countdown coercion, or punitive auto-kick for browsing. After 120 seconds without any user interaction during a pending proposal, mark that member Away and unready; do not mistake a long active menu operation for inactivity.

### 4.3 Loading barrier and countdown

Start -> server allocates match ID, locks roster/settings/content version/loadouts/seed, issues tickets -> clients gracefully leave meet/menus -> load assets and network state -> report actual readiness -> intro -> synchronized countdown -> racing. Readiness means course collision, player spawns, car assets, input binding, required shader/data initialization, and latest race snapshot are actually ready. A progress bar reaching 100% locally is not sufficient.

Show each participant's loading state and a meaningful error. Use a 90-second initial timeout, with a one-time server-controlled 30-second extension for a connected client showing real progress. A failed or disconnected loading entrant becomes DQ for that event and cannot reclaim its grid slot on reconnect. Continue only after all REMAINING eligible entrants are ready and at least one human remains. If nobody remains, abort without results. Do not silently add substitute AI or relax the content hash.

Use a server-monotonic clock and a future start tick, not “each client sleeps three seconds.” Play 3–2–1–GO against that shared tick. Until T0, authoritative cars are held at their launch positions. Any client that loses required connection before T0 is DQ; network jitter alone is not a disconnect. Visual countdown/audio use clock estimates and may be corrected without granting early movement. Record clock-offset estimates and start-tick evidence.

### 4.4 Disconnection, leader transfer, spectating

Use transport disconnect plus an explicit timeout; initial gameplay heartbeat target is 1 second, warn around 3 seconds without authority, declare loss by 8 seconds unless the transport has already confirmed closure. Tune only with recorded impairment tests. After 250 ms without new driving inputs, the server must not hold full throttle indefinitely: coast, then apply stable braking. Do not accept late commands older than the allowed input window.

Once an entrant is DQ after loading begins or during the race, that entrant cannot resume driving in that event. Their car becomes non-colliding and fades away promptly. Reconnecting to the convoy offers Spectate or Menus. Spectate can cycle active/finished valid players, with a safe empty state when none remain. No spectator input may control cars, change race settings, or impersonate a player.

Reserve the disconnected member's convoy place for 60 seconds; show reconnect status. Transfer leader duties after 15 seconds of confirmed unavailability to the longest-connected eligible remaining member, with stable-ID tie breaking. The former leader does not automatically seize leadership on return. During a live race, the server continues and the new leader cannot restart/abort it for everyone. An individual may leave and forfeit their own result. Kicking a connected opponent during a race is not allowed; moderation can block future entry without deleting that opponent's earned finish.

A new member joining an available convoy slot during a race joins as a spectator until the next event. No late racing entry, no retroactive stage clear or rewards. Reconnected DQ members must participate normally in the next event. Other members' completed rewards cannot be revoked by someone quitting at results.

A server/system failure is not a normal player loss or quit. Reconcile durable finish receipts where available; otherwise mark the event Aborted, issue no rank/progression, and offer retry without fees. Do not invent winners from unsent client reports. Repeated system-failure compensation is out of initial scope; never provide an exploitable blanket payout. An event whose player finish has already been durably accepted may settle after disconnection; “quit” applies only before that player's valid finish.

## 5. Campaign structure, party progress, and clear rules

### 5.1 Ordered frontier

Campaign stages have IDs S01–S30. Each player's mode-specific frontier is one plus their largest contiguous cleared prefix, or 31 when complete. For a convoy, permitted stage selection is S01 through min(player frontier), capped at S30. Hard mode additionally requires every member to have Normal S30 cleared. Show whose frontier limits the convoy, neutrally: “Next shared stage: S08 — two members have not cleared it.”

Selecting earlier stages is replay, not progression deletion. A successful eligible S08 clear cannot award S09 or fill an arbitrary gap. Existing completion flags survive repeats. A completed player helping a newer friend receives ordinary race money but not duplicate first-clear RP, challenge rewards, or one-time campaign cash.

When a new member changes the shared frontier while in the map, unready everyone, visibly lock newly unavailable stages, and return the event selection to a valid node without moving each person's local camera unexpectedly. No frontend-only enforcement; the server validates stage access again on allocation and results settlement.

### 5.2 Cooperative completion without six separate solo campaigns

Freeze human participant count H at allocation. DQs do not reduce the required success threshold and cannot be used to make a hard event easier.

For time-based stages, a qualifying performance is a valid finish at or faster than the stage benchmark. The personal support envelope is benchmark ×1.50 in Normal and ×1.35 in Hard. For dedicated drift qualification, use raw unboosted score: qualifying >= benchmark, support >=50% in Normal or >=65% in Hard, plus legal completion of the route. For the penultimate, use the explicit sector/mastery conditions in the stage catalogue.

Normal team success requires at least one qualifying human. Hard team success requires max(1, ceil(H/2)) qualifying humans. Each individual earns that stage clear only if team success occurred AND that individual remained eligible, actively drove the legal course, and finished within the support envelope. No AFK spectator, DQ, remote menu user, or disconnected player gets a clear. The purpose is to let a friend help, without allowing parked cars to collect an entire campaign.

After the first human finish, keep a clear countdown for others. The event may finish early when every remaining entrant has finished, DNF, or DQ. Otherwise the deadline is max(first valid human finish +90 seconds, benchmark support-envelope time), bounded by the stage's published hard timeout. That timeout must itself be at least the support-envelope time. Slower active drivers may receive the limited DNF completion allowance defined in the economy, but not stage clear.

Normal competitive race stages remain recognizable races, with finish order and rivalry. Campaign qualification is a separate team goal communicated before the start. Never make the results say everyone “won” when some failed qualification. Show on-track placing, personal qualification, team outcome, stage advancement, and rewards separately.

### 5.3 Story and rivals

Setting: the fictional Shiranami mountain region. A community night-circuit series opens closed sections of road for organized competition. Its unofficial radio frequency, the Night Signal, connects workshops, tea stalls, timing crews, and retired racers. The player convoy arrives as newcomers and earns trust by driving well and showing up for people, not by becoming a criminal supervillain. Preserve the energetic underground-arcade mood without police or pedestrian hazards.

Normal antagonist: R40 Reina Kurogane, “The Surveyor,” a champion who treats a perfect line as proof that nobody else needs to be trusted. She is disciplined, not cruel. Her rival philosophy conflicts with a cooperative convoy.

Hard finale: R48 Shiori Kuze, “Zero Signal,” an older racer whose unbroadcast dawn run became local folklore. Foreshadow her through mismatched accounts, an old timing slip, her quietly maintained car, and observed kindness. She is present in the cast but never randomly drawn as an early AI opponent. Her challenge exposes a different philosophy: preserving speed through observation and restraint rather than attacking every corner.

Use four acts: Learning the Frequency, Reading the Weather, Between the Markers, and Before the First Train. Author each stage's short setup, rival introductions, victory/loss reactions, and connective story beat. A 10–18 second introductory scene and 3–6 second postrace quip are targets, not padding. Player cars and driver appearances must be the actual convoy loadouts. Show up to six drivers through group staging, not six identical full-length monologues. Rival reactions acknowledge solo versus convoy play, a clean run, a loss, and a rematch where appropriate.

Each client can skip a story presentation locally into a calm waiting view; this cannot advance the authoritative event while another player is still reading. Use a finite presentation window and synchronized start barrier. On rematches, default to a shorter intro; make the full story available in a race diary. Avoid mandatory unskippable exposition on every retry.

### 5.4 Hard campaign

Hard repeats the 30-stage route but has authored alternate dialogue, rival lineup changes, different target lines, selected wet/cold/dawn conditions, more demanding execution, and Shiori's finale. No invisible horsepower, teleporting AI, sudden weather mid-corner without warning, or steering sabotage. Keep all conditions visible before readiness. Hard must not reset cars/currency or require buying a second garage.

Normal targets should build skills progressively. Hard targets should be calibrated around legal class-capped reference builds. Loaners ensure money is not the only route to a fair attempt. Document reference-car/tune, raw time/score, benchmark provenance, and target margins. “Hard” is not simply a global AI-speed multiplier.

## 6. Vehicle handling and racecraft

Build a 3D, free-steering arcade vehicle controller, not a car locked to a centre spline. Splines describe course routes, AI intent, checkpoints, and recovery; they must not steer human cars without a clearly enabled assist. Roads have real elevation, banking, crests, and collision geometry. Car mass/traction can be simplified, but the response must be coherent and tunable.

Default chassis model: fixed-step four-contact/raycast suspension with a compact explicit arcade dynamics state. Account for ground normal, longitudinal/lateral velocity, yaw, steering, wheel/contact state, gear/RPM, grip saturation, and recovery. Separate authoritative physics state from interpolated visual suspension/lean. Avoid dependence on unstable stacks of unconstrained WheelCollider settings unless those are actually validated for the chosen prediction strategy.

Initial handling targets:

- 60 Hz fixed simulation; render independently at 60+ fps when hardware permits.
- Accessible brake-to-set and lift-off initiation; a brief handbrake input is an alternative, not the only way to corner.
- Sustained drifts normally live around 15–45 degrees of slip. Wider angles can score in dedicated events but should cost forward speed. Drifting is not always faster than gripping a mild curve.
- Countersteering and throttle modulation affect recovery. The car must not snap to a scripted drift animation or instantly straighten merely because a button was released.
- Useful weight-transfer anticipation, stable low-speed control, forgiving recovery from a minor mistake, and no permanent spin-locks.
- FWD, RWD, mid-engine RWD, and AWD have perceivable differences. FWD emphasizes braking/turn-in and controlled rotation; RWD emphasizes throttle balance; AWD emphasizes exit traction with weight/understeer tradeoffs; MR rewards precision but needs predictable recovery.
- Speed-sensitive steering, adjustable steering sensitivity/dead zones, optional countersteer assist, automatic/manual sequential transmission, traction assist levels, and camera comfort settings.
- No nitrous requirement or mandatory rubber-band speed boost. Turbo response and gearing are mechanical tuning, not a neon boost button disguised as Initial D-like driving.

Separate driving assists from content difficulty. Settings that change measured performance must be tagged into ghost/challenge compatibility. Accessibility aids must be explained without shaming. Official reference challenges use specified car/build/assist rules; ordinary campaign assistance is permitted and visible. No assist may claim online competitive parity without tests.

Keyboard: arrows or WASD steer/throttle/brake, Space handbrake, Q/E down/upshift, C camera, R hold-to-reset, Esc pause/menu overlay. Controller: sticks/triggers with clearly displayed bindings, face button handbrake, bumpers shift, view/select camera, menu overlay. All bindings remappable, conflicts warned, presets restoreable. Treat held modifiers separately from movement actions; no “Run as direction” style bug. Typing, menus, emote wheels, and focus loss must not leave steering/throttle held.

Race camera: chase default, bonnet/hood and bumper alternatives. A basic authored interior/cockpit view is optional, not a false promise of 18 detailed interiors. Camera collision, horizon stability, look-back, adjustable FOV, shake amount, speed effects, and motion blur off by default. Do not manufacture speed by warping the road or obscuring the apex with particles. Gamepad vibration has a master off setting.

### 6.1 Collisions, resets, and fairness

Campaign and drift events default to non-contact car-to-car rules; barriers and road surfaces still collide. Freeplay offers Ghosted and Light Contact before readiness. Light Contact uses capped impulses and anti-pit behaviour, not demolition derby. No public competitive assumption of perfect contact fairness under arbitrary latency. Neutralize wrong-way/stationary blocking cars after a short warning; no teleport punishment of nearby victims.

Wall touches should lose speed naturally and invalidate clean-sector awards; wall riding must not be the fast line. Count a wall-hit incident on contact onset above a measured normal impulse/speed threshold, with a 0.75-second per-surface debounce, not one hit per physics tick. Keep cosmetic scrape and meaningful impact separate. Cosmetic damage resets after an event; no repair bill, fuel tax, or car destruction grind.

Reset requires a hold confirmation and returns to the last valid safe checkpoint on the course, adds 3.0 seconds, clears any drift chain, and briefly ghosts the car for at most 2 seconds. It cannot jump ahead, skip missing checkpoints, or grant speed. Overturned/out-of-bounds cars offer recovery promptly. Race direction, course corridor, checkpoint order, and plausible progress are server-enforced.

## 7. Drift scoring that rewards driving instead of exploits

Use an explicit, inspectable raw score, separate from showcase bonuses and wallet multipliers. Initial formula per fixed step:

rawDelta = legalForwardMetres * 100 * angleFactor * lineFactor * chainMultiplier.

angleFactor: 0 below 10 degrees; ramps to 1.0 at 25; up to 1.25 at 45; tapers to 0 at 80. The exact continuous curve must be documented and tested. A valid scoring segment needs >=35 km/h, forward progress along the legal course direction, road contact, no reset/wall impact, and a route-owned judged zone. lineFactor ranges 0.8–1.25 by closeness to the intended arc/clip zone, not proximity to any random wall. chainMultiplier increases gradually from 1.0 to a hard maximum 3.0 as distinct valid corners/transition gates are linked.

Allow a 1.0-second straightening interval between linked zones. Wall impact or leaving the course loses only the unbanked chain, not points already banked. A spin, wrong-way loop, stationary donut, oscillation in the same judged area, reverse driving, and repeated re-entry at the same progress position earn no additional raw score. Track visited scoring progress per lap/sector. Bank at judged-sector completion or clean chain release.

Slipstream/tandem closeness can yield a bounded style display bonus in social Freeplay, never unlimited official drift points. Dedicated drift events evaluate each player independently even while all six share the course; car-to-car contact is disabled. Mixed time/score events must show both goals, not an opaque combined number whose winning strategy is unclear.

Display current chain, banked score, angle/zone feedback, and why a chain broke without covering the road. A “+score” utility part may multiply SHOWCASE score, but raw score drives campaign qualification, the 75 challenges, benchmark records, and compatible leaderboards. Clearly label both values. Do not let players buy the final mastery rank.

## 8. Freeplay, ghosts, and replay compatibility

Freeplay modes: Sprint, Circuit, Drift Attack, Time Trial/Ghost Chase, and a three-event Custom Cup. The host selects unlocked or practice-accessible course, supported direction, lighting/weather preset, car class/cap, contact rule, and live AI count. Enforce humanCount + AIcount <=6 on server allocation; clamp invalid stale selections with a visible explanation, never silently eject a human.

Pure PvP means zero live AI and at least two human starters. Ghost overlays do not turn Time Trial into PvP, do not occupy slots, and cannot earn a win bonus. A one-person no-AI run is Time Trial. Freeplay can use all 26 courses in unranked practice once the tutorial is available; campaign discovery can hide story previews without preventing friends trying tracks. Performance challenges may have specific unlock prerequisites stated in their definitions.

Allow racing personal best, a chosen convoy member's shared ghost, and an authored rival reference. Display up to three replay overlays at once; additional comparisons appear in timing tables. Never overwhelm the road with 48 ghosts. Ghosts are server-generated/validated replay samples, not just input streams assumed to be deterministic across platforms. Store sampled transforms, time/sector/checkpoint metadata and a bounded input/event record where needed for validation.

Every ghost includes course ID/revision, direction, weather/surface, physics version, tuning/car hash, class, assist flags, game version, raw result, resets, provenance, and validity. Mark incompatible ghosts as reference-only or archive them; do not silently compare obsolete handling times. The player can view exact car/parts/stats before chasing. Ghost playback is interpolation over a validated recorded path, not an independent AI physics car.

Record nearest sector deltas against the reference. Offer a compact postrace route/elevation chart with braking points and lost-time sectors. Personal practice ghosts can exist locally but cannot be uploaded as trusted records without validation. Network outages never promote local fabricated progress into online currency/RP.

Custom Cup holds a published three-event schedule. Each leg still requires participant load readiness, not a new forced setup dialog. Between races allow a short, cancelable results/ready area; permitted loadout changes must obey the cup rule. DQs keep their places in the cup table but cannot regain the missed race. No buy-in or gambling-like currency stakes.

## 9. Garage, car ownership, parts, and measured stats

The garage is a 3D place with a carefully lit hero car, not a catalogue grid filling the whole screen. Inspect orbit, zoom, open available body compartments for display where authored, and switch to an uncluttered engineering panel. Use actual owned vehicle instances and preview transactions. Maintain separate purchase, fit, compare, tune, paint, and save-preset actions.

18 car definitions are in Appendix C. Three starters are available as an initial free choice; the other two remain inexpensive purchases. No starter is a trap: each has viable upgrade paths and fits useful event classes. Provide class-legal loaners for campaign stages and fixed-build challenges so a player can attempt the next event without mandatory grinding. Loaner results are labelled and have identical ordinary participation pay; owning a car provides customization/tuning flexibility, not permission to enter the story.

Keep at least three tune presets and five visual presets per owned vehicle; copying presets is not item duplication. Installed parts remain owned when swapped out. Show compatibility by chassis/family and tier; incompatible parts cannot be purchased accidentally. Confirm expensive purchases with before/after balance and the actual item name. An interrupted purchase either commits exactly once or not at all.

Mechanical categories and required tradeoffs:

- Tyres: dry grip, wet performance, progressive breakaway, heat stability. No long-term tyre-wear bill.
- Suspension: spring/damper balance, ride height within safe bounds, roll stiffness, kerb stability and weight transfer. Slamming the car cannot create hidden downforce.
- Differential: locking behaviour, entry rotation, exit traction, corner stability.
- Gearbox/final drive: acceleration versus reachable top speed; automatic gearing respects the same ratios.
- Engine/intake/exhaust: torque curve and power delivery, mass/cooling effects where implemented; do not invent unsupported stats.
- Forced induction: response/lag versus peak power; naturally aspirated builds remain viable.
- Brakes: effective braking response, modulation window, stability under trail braking. Better braking is not infinite grip.
- Weight reduction: acceleration/braking/rotation tradeoffs with stability where modelled; bounded per chassis.
- Aero: front/rear balance and drag/downforce at speed; not a pure “better” slider.
- Wheels/body kits: visual unless a clearly specified mechanical package changes mass/aero; the same rule applies to AI and players.
- One separate utility slot: economic or showcase bonus only. Initial variants +4%/+8% base currency OR +5%/+10% showcase drift score. Hard cap is +8% income or +10% showcase, not stacking both indefinitely. Official raw score and RP never change.

Use a consistent Performance Index from 100–999 and classes D 100–299, C 300–499, B 500–699, A 700–849, S 850–999. Event caps reference actual PI; do not silently rubber-band installed parts down. Offer a saved legal preset or loaner when over cap. PI is a declared balancing index based on instrumented performance plus handling envelopes, not a random sum of price/tier.

Stat screen: power/torque/weight/drive layout/gears plus measured or explicitly estimated 0–100 km/h time, reachable speed over a stated test length, braking distance, corner-hold/lateral-grip metric, wet stability, and drift recovery behaviour. Run a repeatable test track/dyno harness. Label estimates and test conditions. When comparing parts, display actual changes/tradeoffs, not arbitrary five-star bars unrelated to the controller. A radar diagram can summarize but must not replace interpretable values.

Visual customization: primary/secondary paint, finish, wheels, window tint in readable bounds, headlights within safe visibility limits, legal body kits, spoiler, plate text, and decals from a vetted original catalogue. Provide 64 decal layers per car, transform/mirror/opacity/ordering controls, undo/redo (at least 50 meaningful edits), and view presets. Bake/composite livery textures by revision and transmit bounded decal definitions/hash, not full texture data every frame. Do not accept arbitrary image uploads or executable/vector scripts at launch. Prevent decals from making lights/identity completely unreadable in competitive modes. Plate/name text is length-limited and escaped.

Customization drafts are local until Apply/Save. Cancel restores previous appearance. Server checks ownership on application. Cosmetic updates at a meet use a safe revisioned refresh; performance editing requires leaving the meet into Garage. A draft must never replace the frozen race build while loading or racing. Save a selection on meaningful actions, not every tiny slider movement sent over the network.

## 10. Economy with explicit boundaries

Currency is fictional “Credits,” not real money. Wallet cap is 9,999,999. Clamp gains with a visible cap explanation, never overflow. Most expensive prestige cosmetic costs 1,000,000. No individual performance part costs more than 200,000; highest tiers generally 150,000–200,000. Cars are priced separately in Appendix C, with no model above 350,000 in this launch catalogue.

Initial starter grant: one chosen starter plus 12,000 credits. Tutorial completion awards 3,000 once. Standard cosmetic items often cost 500–5,000; richer paint/decal sets 5,000–20,000; substantial clothing/card effects 15,000–60,000; a small prestige tier 100,000–1,000,000. The million-credit item is a clearly optional long-term display piece, not required by a challenge or rank.

Initial mechanical price ranges: T1 6,000–18,000; T2 24,000–55,000; T3 70,000–120,000; T4 150,000–200,000. Unlock shop availability primarily by act and car compatibility, but loaners prevent progression softlocks. No consumable paid entries, stamina timers, fuel, mandatory repair charges, or daily login requirements.

Per valid completed event, define B = 2,500 + 22 × authoredExpectedSeconds, where authoredExpectedSeconds is a server-owned course/mode value clamped to 120–420. Use expected duration, NOT actual elapsed player time, so idling never increases pay. For short custom configurations below 120 seconds, apply a proportional reduction instead of granting the 120-second minimum. Tutorial repetitions and free stationary meet time do not pay.

Credits = floor(B × difficultyMultiplier × placementMultiplier × cleanlinessMultiplier × utilityMultiplier × eligiblePvPBonus) + eligibleFirstClearBonus + newlyCompletedChallengeCash.

- difficultyMultiplier: Normal/casual standard 1.00; Hard campaign 1.35. Freeplay AI difficulty does not permit arbitrary cash multipliers. Fixed challenge payouts are defined separately.
- placementMultiplier: first 1.35; second 1.20; third 1.10; fourth–sixth 1.00. For time trials use performance bands: reference beaten 1.20, otherwise valid finish 1.00. Drift placement uses raw score.
- cleanlinessMultiplier: 1.05 only for no meaningful wall impacts, no resets, and all checkpoints legal; otherwise 1.00. Never deduct the base completion income for minor errors.
- utilityMultiplier: 1.00, 1.04, or 1.08 according to the single valid utility item, applied to ordinary event pay only.
- eligiblePvPBonus: 1.20 for first place only when this was pure PvP, at least two authenticated human entrants both legally finished, and the configuration is eligible. Otherwise 1.00. No bonus for being the last car left after everybody quit. Do not claim this fully prevents intentional collusion.
- firstClearBonus: ordinary campaign stage 8,000 Normal / 12,000 Hard; lieutenant 20,000 / 30,000; penultimate 25,000 / 35,000; finale 40,000 / 60,000. Granted per eligible account/stage/mode only once.

Losing while completing legally still earns full base participation pay. An active non-quitting DNF may earn 0.25 × B, with no placement/clean/utility/PvP multiplier, only if it crossed at least 80% of legal checkpoints and the server verifies meaningful control/progress. No RP, stage clear, or race-completion challenge for DNF. Voluntary quit, confirmed midrace DQ/disconnect before a valid finish, AFK, or invalid-course exploit earns zero. Do not award a “disconnect consolation” that rewards pulling the cable. Do not punish a completed finisher because they leave the results UI.

Make rewards server-computed from validated event facts. Use idempotency keys based on event/account/reward type and an append-only ledger with unique constraints. Store money, inventory, challenge unlocks, and first-clear flags atomically. Reconnecting or retrying HTTP settlement cannot duplicate them. Show pending settlement honestly; do not deduct items locally and promise later reconciliation.

Balance targets, not claims of measured results: an early cosmetic after the tutorial; a useful T1 part after roughly 1–3 ordinary completed races; a starter-alternative car early in Act I; several meaningful upgrades and another car across Normal; expensive T4 parts as later goals; prestige cosmetics after substantial replay. Measure with modelled loss rates, first clears, and fixed challenge payouts. Preserve sensible saving goals instead of filling the store with equal-priced items.

## 11. Player Cards, challenges, and finite Rank Points

Create the Player Card after authentication: display name, avatar, accessible driver appearance choices, country/region flag optional and self-selected, preferred car, card background, frame, motif, title, and pronouns optional. No real identity/photo is required. Provide initial accessible defaults rather than forcing a twenty-minute creator before driving. Avatar appearance never changes hitboxes, steering, or performance.

Public card shows mastery rank/RP, campaign milestones, challenge progress, selected car/build class, chosen showcase records, and public cosmetics. Do not show email, auth ID/token, real-name information, exact location, or full wallet. Inspection is local UI; the viewed player is not interrupted. Names have Unicode-safe length limits, no rich-text injection, a report/block route, and no automatic assumption of uniqueness.

Rank Points are mastery points, NOT Elo, a zero-sum PvP rating, or a reward for farming the same opponent. Thresholds:

0 New Signal; 200 Local Line; 600 Night Runner; 1,200 Corner Scholar; 2,400 Pass Specialist; 4,000 Convoy Ace; 6,500 Sector Master; 9,000 Mountain Elite; 12,000 Midnight Vanguard; 15,000 Living Legend.

Finite point budget:
- First valid Normal clear of each S01–S30: 100 RP each = 3,000.
- First valid Hard clear of each S01–S30: 200 RP each = 6,000.
- 25 Bronze challenges ×40 RP =1,000; 25 Silver ×80=2,000; 25 Gold ×120=3,000. Challenge total 6,000.
- Maximum launch mastery total is exactly 15,000. Tutorial, repeated races, purchases, meets, random drops, score utility, and private PvP farming award no RP.

Living Legend additionally requires all Hard stages and all 75 challenges, which should follow from the finite ledger but must be checked explicitly against malformed/imported data. Do not round up or silently award an extra RP source. No demotion from disconnects. Unit-test every threshold and the final sum.

All 75 challenges in Appendix E are achievable by one player through sanctioned races, ghosts, AI, the meet's built-in interaction spots, and fixed loaners. They may be more fun with friends, but the final rank must not depend on finding a public lobby, receiving votes, persuading strangers to emote, or owning the million-credit cosmetic. No challenge requires a real-money purchase or a rare random drop.

Each challenge has ID, exact predicate, context, allowed rules, difficulty tier, RP, one-time cash, and one unique cosmetic reward ID. Bronze/Silver/Gold cash is 3,000/8,000/15,000. Challenge cash does not receive utility or race multipliers. Every unlocked item is equippable or visible in its proper customization interface; a string in an inventory is not a completed reward asset. Show pinned progress during appropriate events without flooding the race HUD. Validate claims on server. Threshold-based objectives use authoritative raw metrics, not client screenshots or UI counters.

Never reset purchased items or first-clear records during a content patch. Stable IDs, explicit migrations, backups before schema changes, transaction tests, and recovery paths are required. Client-local file editing must not grant online mastery.

## 12. The car meet: Cedar Lantern Terrace

Build a dedicated roughly 150×110 metre level plateau with a Japanese-inspired landscaped setting, not a racetrack with a parking menu on top. Use the scene brief in Appendix F. It holds 12 parked display cars and 12 walking player avatars, plus scenery/NPC ambience. It is a separate scene/server room from race instances.

“Anybody can join” means any authenticated, non-banned user may request an available public instance, subject to capacity and rate limits. Offer Join Public Meet, Join Friend's Meet, and Convoy Meet. Default allocation keeps convoy members together when enough spaces exist; otherwise offer a new instance or queue with clear status. Never silently scatter a convoy across instances while showing them as co-present. Friend invitations reserve a bay for a short, visible 30-second window.

Arrival sequence: authenticate and reserve bay; load meet and currently present appearances; server publishes Joining; drive a non-colliding presentation car along a short approach spline to the assigned bay; stop; let the player exit. Target 3–4 seconds. The arriving client can skip its camera flourish without teleporting through others' collision. Other visitors see a bounded arrival animation ending at the same parked state. Server owns the final parking transform; random local bay selection is forbidden.

Arrival cars have no damage/impulse against avatars, other cars, or furniture. Parked cars remain static display objects. Players may sit in their own car, inspect it, turn wheels, change a safe headlight preset, or trigger a brief rate-limited rev; they cannot drive into the pedestrian plaza. Driving freedom belongs in races/practice, not griefing at the meet. Getting out places the avatar at a validated free point beside the car. Walking collisions prevent passing through scenery; avatar-avatar collision can be soft/disabled so nobody can imprison another player.

Walking controls and camera must feel finished: walk/jog, turn, idle, interact, inspect, emote wheel, recenter camera. Include recognizable animations for wave, polite bow, thumbs-up, clap, point, camera pose, stretch, cheer, shoulder shrug, nod, quick footwork dance, and car-admiration crouch. Rig reuse is allowed; identical outfits/faces are not the entire player system. Emotes replicate ID, start tick, and bounded duration; local animation is interpolated, not networked bone by bone.

Inspection shows car name, owner, legal PI/class, selected tune summary, visible customizations, public card, and optional like (cosmetic social feedback only, no currency/RP). Never expose hidden inventory or enable modifying someone else's vehicle. Respect block lists and distance for social actions. Include a photo mode that does not pause the server, hide/report/mute tools, and a short predefined quick-chat wheel. Free text chat is not required at launch; enabling it would require additional moderation and safety work.

Horizontal notifications: original “SIGNAL” ribbon near upper centre below the persistent convoy header. Enter from the side, hold about 2.5 seconds, exit cleanly; use a quiet original two-note cue. Show who arrived/departed, not a giant modal. Limit to one current and three queued notices; coalesce busy bursts into “3 drivers arrived.” Never steal focus or obstruct the emote wheel or walking view. Screen-reader/accessible equivalent via event list where supported. A leader's ready request has higher priority and remains actionable through a small pinned convoy prompt after the ribbon goes away.

Departure: mark Leaving; avatar and car fade/dissolve over about 0.5 seconds, using reduced-motion fade fallback; release the bay only once the room state confirms departure. One event per departure, not repeated notices during reconnect retries. A network-loss departure is labelled disconnected, not “left to race.” A menu transition into Garage genuinely leaves the meet; an inspect overlay does not.

Keep a compact convoy ribbon, own broad state, leader request, and Ready/Unready access while at the meet. Once an agreed event allocates, cancel emote/inspection safely, save only approved cosmetic state, leave the room, and move into race loading. On return from the race, retain a clear option to return to the prior meet/convoy destination; do not presume the same parking bay remains available.

Use visible hedges/fences, buildings, retaining walls and slopes backed by invisible collision to prevent leaving the area. Boundaries must correspond to scenery. No inexplicable invisible wall in the middle of the plaza. Add a rescue/reset-to-car action for a stuck avatar without granting teleport exploits in races.

## 13. Rival AI: observable identities, not 48 speed numbers

Create a shared AI controller with the nineteen reusable driving tendencies defined in Appendix D, then individual parameters and decision preferences for all 48 people. Inputs go through the same vehicle controller as humans. AI can know authored route geometry and its chosen target line; it cannot read a player's future input, ignore grip/collision, teleport, or receive secret infinite acceleration.

Parameters include braking-point bias, release shape, turn-in timing, preferred slip angle, corner-entry versus exit emphasis, risk margin, overtake side, following gap, pressure sensitivity, mistake recovery, wet-line preference, tyre/build fit, and consistency. Shared archetypes reduce engineering duplication; distinct combined preferences and authored moments make individual drivers recognizable.

At least four demonstrably different behavioural outcomes must be measured per crew on shared benchmark corners. Show telemetry evidence, not just JSON values. A late braker should brake later, an exit specialist should sacrifice entry for exit, a wet reader should choose safer surface/line, and a feint specialist should make an observable permitted positioning choice. A personality weakness must be exploitable by skilled play without becoming an unavoidable scripted defeat.

Normal AI difficulty escalates by stage. Hard substitutes support rivals and changes their decisions/weather preparation, while preserving physical fairness. Freeze random seed/roster for an accepted event; retries offer the same matchup by default so learning matters. Freeplay can explicitly randomize. Boss and legendary IDs never come from an unrestricted random pool.

Roster art acceptance: unique profile silhouette, face/hair/headwear combination, posture/gesture, outfit construction and material blocks, signature accessory, and livery motif. Shared skeletons and clothing modules are welcome, but a hue shift and different name are not a new character. Render a 48-person contact sheet for review. Every rival must appear in an actual race, story segment, or authored challenge across Normal/Hard/Freeplay; none may exist only in an unreachable data row.

## 14. Course engineering and environment art

Each Appendix A route is an actual authored place. Use a route tool to edit centreline, widths, elevation, camber, surface boundaries, sector/checkpoint gates, start/finish, marshal/rescue points, AI lines, camera rails, and scenery zones. Store stable IDs and source geometry. Reuse regional kits, never the same four-corner spline with a different skybox.

Count uniqueness by drivable geometry and route decisions. At least 70% of a counted course's drivable centreline should be exclusive to that course compared with another counted course. Shared paddock approaches/short connectors are allowed. Reverse/weather variants share the base ID and a variant descriptor, not an extra counted course. A custom validation report should flag suspicious overlap; visual review still matters.

Every course has: legal start grid for six, at least three meaningful sectors, readable road edges, varied corner sequence, at least three landmarks, terrain beyond the barrier, appropriate buildings/vegetation, authored lighting, atmospheric/audio zones, several camera beats, recovery locations, complete collision/occlusion, AI navigation, checkpoint legality, a working minimap/elevation trace, race/drift judging data as applicable, and legal finish/return flow. Avoid a floating road with trees pasted at intervals.

The 26-course minimum is not satisfied by names in a menu. Capture each in a build, drive from start to finish, and validate every checkpoint. Constrain user steering only at required course boundaries; no hidden autopilot. Guardrails must stop tunnelling at top plausible speed; use swept collision/substeps/continuous checks appropriate to the controller. Curbs, drains, shoulder gravel, puddles, and wet sheen must agree with physical meaning; decorative highlights must not look like impassable walls.

Art target: stylized high-detail 3D with anime-adjacent character design and clean modern arcade readability. Not photorealism, not low-poly cubes, not pixel art. Rich car paint/specular response, intentional shape language, readable road material, baked/managed lighting, restrained bloom, convincing wetness, deep blue mountain silhouettes, warm occupied spaces, and purposeful colour contrast. Avoid exaggerated blur, excessive black crush, universal purple neon, and a tiny repeating texture across kilometres of road.

Car geometry should have convincing wheel arches, fenders, bumpers, lights, glass, interior silhouette, tyres/rims, mirrors, and exhaust; wheel rotation, steering, body roll and suspension visual motion must match driving. Materials distinguish rubber, clearcoat, metal, cloth, glass and asphalt. Player/rival drivers are fully modelled enough for meet/cutscene use, not flat cards pretending to be walking avatars. Use level of detail and bounds/culling; don't render every course or all 48 characters together during a race.

Initial performance budgets are guardrails to measure, not promises: six active high-detail cars and a bounded local set of track props; efficient instancing; 2k vehicle livery textures with scalable settings; character/vehicle LOD tiers; limited shadow-casting lights; baked environment lighting plus controlled headlights; streaming/bundles per course/region; pooled particles and skidmarks. No per-frame allocations in the main driving/network hot path. Profile six racers, a populated meet, and shader/cold-load transitions separately. Do not hide poor performance by lowering physics rate without retesting handling.

## 15. Arcade UI art direction from the first build

Name the UI language “Signal / Sector.” Its objects are timing slips, route charts, tachometer markings, pit boards, start lights, radio labels and driver credentials. It is not a fantasy folio and not a SaaS dashboard with a racing background.

Use graphite/ink surfaces, warm off-white labels, a controlled signal-red selection accent, amber caution, and a cool cyan timing/reference accent. Reserve colour for function. Use condensed, high-legibility racing headings and tabular numerical timing; readable regular-width body text. Do not use decorative pseudo-Japanese, ripped franchise logos, overused carbon-fibre texture everywhere, emoji icons, generic rounded cards, or unbounded neon/glass blur. Build a small original icon family. All assets/fonts require appropriate permissions and bundled provenance.

Menu composition: persistent status/convoy bar at top; clear primary menu navigation below/within it; central hero 3D car or meaningful route view; contextual engineering/event details beside it; bottom contextual actions and controller prompts. The car is the focal object. Avoid a giant empty central panel with a row of template buttons. Rival portraits/cards, track/elevation maps, and garage comparisons should look authored for racing.

Top bar must show convoy positions/readiness/leader and own wallet/card/car at ordinary menu sizes. At narrow widths use two deliberate compact rows and an explicit convoy expansion, not clipping or microscopic text. “Always visible” applies to ordinary menus; in an active race provide a compact racing HUD rather than spending a third of the road view on storefront tabs. Car meets retain a compact convoy header as specified.

Each screen has an information job:

- Home/Convoy: who is here, what they are doing, current proposal, what stops launch.
- Campaign map: legible route/act progression, shared frontier, selected rival, benchmark, course conditions, group eligibility.
- Freeplay: actual participant/AI count, format, class cap, ghost/contact status, race preview and readiness.
- Garage: vehicle first, selected subsystem second, comparison/price/tuning third. Unsaved draft clearly indicated.
- Dealership: owned versus buyable, drive layout/class/cost, distinctive silhouette, no fake stock countdown.
- Challenges: five families, pinned goals, exact progress, reward preview, conditions, locked reason.
- Card: restrained credentials/records/customization preview, not a wall of achievement badges.
- Results: outcome, legal placing/time/score, best sector, wall incidents/resets, separate itemized money/RP/unlocks, rematch/return readiness.
- Meet: sparse spatial UI, inspect only what is targeted, social alerts below persistent group info.

Transition wipes: short diagonal/sector-strip wipe of about 180–280 ms, tied to local navigation. Input is routed to exactly one screen; hide obsolete panels and dispose listeners. The host's wipe must not animate other clients except on a committed shared destination. When loading is genuinely slow, show real progress/connection status after the brief wipe rather than stretching an animation to conceal a stall. Reduced motion uses an immediate or short crossfade. Back restores focus/scroll/drafts appropriately. Never let a transition leave an invisible input-blocking layer.

Race HUD: position, course progress/lap, time, sector/rival delta, speed/gear/revs, compact minimap, and optional drift panel. Notify penalties/errors without obscuring the apex. Currency and shop tabs do not need to remain over live driving. Units selectable km/h or mph without changing simulation. Show connection-quality warnings meaningfully, not a permanently animated icon with no telemetry.

Implement keyboard/controller focus, mouse, adjustable text size, high contrast, colour-independent statuses, safe areas, ultrawide and 1280×720 minimum desktop layouts, subtitle timing/background options, motion effects toggles, audio mixing, and separate gameplay-assist settings. Maintain readable panel spacing; buttons must not reorder on hover. On any optional touch target, use adequate target size and test actual viewport/keyboard changes rather than equating a desktop screenshot with mobile support.

Research a focused set of primary references: official racing-game presentation examples, road/civil-engineering photographs from lawful sources, real timing sheets/instrument typography, Unity UI/input/accessibility documentation. Write a short design decision record: what principle was taken, how it serves a task, and what was explicitly not copied. Reference research is not proof of user testing. Establish a polished garage/convoy/one-race HUD slice before mass-producing 26 courses, then carry the same system across every screen. Do not stop after that slice.

## 16. Original audio and tutorial

Music identity: original energetic eurobeat-adjacent dance, synth-rock, and late-night breakbeat, contrasted with an airy meet/garage theme. No lifted melodies, sampled franchise dialogue, or soundalikes that reproduce a recognizable recording. Minimum musical set: title/convoy, garage, meet, six regional race arrangements, four lieutenant cues, Normal finale, Hard legend finale, results win/loss variants, and a tutorial bed. Motifs can recur as authored arrangements; 15 seconds repeated under 30 different filenames is not a full soundtrack.

Use original MIDI/sequencer composition rendered to efficient clips or an appropriate runtime synthesizer. Procedural notes must follow a musical structure, not random pitches. Fade/duck between menus, intro, race, results and meet; no overlapping abandoned tracks. Playback state is local and does not determine race clocks. Expose music, engine, effects, UI, ambient and voice/subtitle controls. TTS is optional and honestly labelled, not a substitute for promised acted rival performances. Fully authored subtitles/dialogue are required even without voices.

Engine audio varies by architecture and actual RPM/load: at least inline-four, six-cylinder, compact triple, and rotary-like fictional tonal families for suitable cars. Blend acceleration/coast/idle bands; use turbo spool where fitted, gearshift transients, tyre slip tied to surface/contact, wind, suspension/kerb sounds, and collision severity. Not one pitch-shifted beep for every car. Limit rev spam at meets. Spatialize other cars and footsteps; keep important notification cues audible but gentle. Expose subtitles/captions for important nonverbal race cues where practical.

Tutorial T00 is a closed training loop and skid pad. Teach controls, camera, braking, turn-in, grip versus drift, countersteering, exits/gearing, resets/checkpoints, parts/stat comparisons, ghost deltas, raw versus showcase score, convoy readiness, public meets, race connectivity and disqualification, rewards and rank. Use small interactive segments with visible feedback, a model demonstration, local retry, and a searchable help index. Do not force mastery of an advanced technique just to access a first casual race.

Tutorial playable solo or in a convoy with independent progress in a non-contact session. A ready-check practice cannot require a stranger; simulated teaching controls must be visibly marked as training and are NOT networking acceptance evidence. Experienced users can demonstrate basics in a short check or revisit any lesson. Tutorial content and story-cutscene skipping are separate controls.

## 17. Data, content manifests, and reproducibility

Use stable typed content definitions (ScriptableObjects and/or versioned JSON with import validation). Required schemas: CourseDefinition, CourseVariant, CampaignStage, RivalProfile, CarModel, PartDefinition, VehicleBuild, CosmeticDefinition, ChallengeDefinition, RankThreshold, EventProposal, MatchTicket, RaceSnapshot, DriverInput, RaceResult, RewardReceipt, PlayerProfile, ConvoyState, MeetMembership, and GhostHeader.

Every content ID referenced anywhere must resolve. Manifests contain actual course assets, start/finish/checkpoints, rival/vehicle assets, dialogue, qualifying targets, compatible caps/rules, unique rewards, and renderable previews. Store machine-readable coverage reports. Never satisfy “48 rivals” by generating 48 empty ScriptableObjects. Reuse source catalogue data in game build and validation rather than maintaining hand-edited duplicate lists.

Keep presentation code separate from authoritative rules. C# shared logic for progression, payouts, parts validation, event configuration, and challenge predicates must have tests and should be shared/ported consistently between control/game servers as applicable. The server loads trusted catalogue versions, not uploaded client definitions. The Unity client may preview a calculation but the server recomputes it.

Suggested repository structure:
Game/Assets/{Game,Art,Audio,UI,Content,Editor,Tests}; Game/Packages; Game/ProjectSettings;
Services/ControlPlane; Services/SharedContracts; Services/Tests; Backend/{migrations,seed};
Tools/{authoring,build,qa}; Docs; Evidence; Builds ignored except selected release manifests.

Commit Unity .meta files, necessary source models/textures/audio, project/package settings, content manifests, backend migrations, test fixtures, build scripts, provenance, and version locks. Exclude Library, Temp, Obj, Logs, user settings, installed packages/cache, real credentials, personal player data and most large test captures. Use Git LFS only after confirming remote support and retrieval; a pointer without accessible objects is not a preserved asset. No licence change or asset publication without approval.

Produce actual Editor tools for idempotent content generation/validation, building all selected targets, smoke launching, and collecting images/logs. A generator that has never run is not a scene. Save generated scenes/prefabs/assets in the correct project and verify no missing scripts/materials/shaders. Do not rely on manually assembled objects that vanish on a fresh checkout.

## 18. Multiplayer performance, prediction, and validation detail

A racing game needs more than “NetworkTransform on each car.” Use a server-authoritative fixed simulation, timestamped sequenced driving inputs, locally predicted own-car motion, authoritative correction, and interpolated remote vehicles. Unity Netcode's client anticipation is not automatically a full rollback/replay prediction implementation. Verify package capabilities and implement the missing loop rather than claiming it exists.

Start at 60 Hz physics/server simulation, 30 Hz input packets containing recent redundant inputs, and 20 Hz compact snapshots. These are initial measured tuning targets. Keep snapshots below appropriate transport/MTU budgets or use documented fragmentation deliberately. Use latest-wins unreliable sequencing for high-rate state where supported; reliable ordered messages for setup, validated state transitions, results, and cosmetics. Do not put continuous steering behind a long queue of reliable chat messages.

Own car: immediate local control; snapshots acknowledge the last processed input; retain a bounded history; reconcile and replay unacknowledged input through the same compatible simulation code. Correct visual discrepancies smoothly except for safety-critical corrections. Do not depend on bit-identical cross-platform PhysX determinism. Vehicle dynamic contacts and prediction are tested explicitly; forgiving/ghosted default contact reduces a major desync source but is not an excuse to ignore wall/road differences.

Remote cars: buffer roughly 100 ms initially, interpolate with authoritative velocity/orientation, bound extrapolation to 150 ms, then display degraded connection rather than driving through walls forever. Server validates input ranges/tick windows, rate limits, speed/acceleration envelopes, and course progress. No kernel anti-cheat claim. Maintain a clear threat model: server verification makes simple client tampering ineffective but does not guarantee immunity to bots, collaboration, or every exploit.

Race clock and finish: use server ticks with sub-tick checkpoint-crossing interpolation when necessary. Ordered finish gates, no missing checkpoints, no elapsed-time tampering. For finishes within measurement precision, explicitly declare a tie; do not break it by account ID while claiming a faster lap. Time values serialized in integer micro/milliseconds with documented rounding for UI. Drift uses server integration. Compare results across all connected clients.

Meet avatars can use lower-rate snapshots and interpolation, but membership, parking assignment, appearance ownership, and emote timing remain authoritative. Radius/interest management reduces unnecessary updates; do not stop replicating a friend merely because a UI panel is open. All join/leave notices are keyed events so replay/resync does not spam.

Network test matrix must include 0/50/100/150 ms round-trip delay; 0/1/3% loss where applicable; controlled jitter/reordering; a 3-second outage; and a hard disconnect. Record how impairment was introduced, not just an arbitrary displayed “ping.” Include a 250 ms degraded case and document limitations without promising tournament-quality contact at that delay. Distinguish six independent clients, six in-process bots, six localhost processes, and six real remote devices. Only real sockets across isolated client identities establish multi-client support; WAN acceptance additionally needs a remote-network run.

## 19. Acceptance gates — no fake completion

Gate 0 / environment: verify selected NEW repository, real Unity version/project path, editor access, package compatibility, credentials scope, local account/control/database stack, first verified source push, and an actual built-client smoke test. No unrelated project edits.

Gate 1 / driving and network spine: one finished representative course section, two distinctive cars, input/cameras, two independent clients and authoritative server, checkpoint/finish/reward transaction, reload persistence. Before mass content, prove a six-client race, readiness revisions, sync countdown, disconnection/DQ, and spectating. This gate protects investment; it is not the final scope.

Gate 2 / production slice: a polished garage, convoy header, event map, one rival intro, race HUD/results, two-level parts comparison, one useful challenge/reward, and a four-to-twelve-visitor meet test. Validate the visual direction from real screenshots/video and the controls from actual input. Continue without routine approval when unblocked.

Gate 3 / content: all 26 courses; all 48 identity sheets and behavioural assets; all 18 car models; 30 Normal and 30 Hard stages; all 75 predicates and visible unique rewards; complete story beats/music/tutorial. Content counts require instantiated accessible content, not placeholder objects.

Gate 4 / integration: campaign access rules across mixed progress groups; all party sizes 1–6; all modes; car legality/loaners; class/performance balance; currency/RP/first-clear ledger; database restart; crashes during transactions; invalid/tampered requests; meet independence and race launch from meet; subtitle/font/localization bounds; controller focus; transitions; frame/memory budgets.

Gate 5 / release validation: fresh checkout/import/build, standalone clients, dedicated server, actual internet test with multiple independent people/devices where tools/user permit, course-by-course replay coverage, all 75 challenges proven achievable, preserved source/asset hashes, current instructions, and an honest release report. Missing WAN/human tests stay marked unverified/blocked; do not substitute a green count from AI tests.

Minimum specific tests:

- Every combination H=1..6 and live AI=0..6-H supported in Freeplay; reject >6; exactly six humans trigger only the labelled benchmark overlay in campaign.
- Every stage accessible under each legal party size; sample baseline new-account-to-finale Normal/Hard continuous runs, plus automated invariant/route checks for all stage/party combinations. Evidence must say what used scripted drivers and what used a person.
- Frontier examples [1,1]->S01; [8,12,31]->S01..S08; [31,31]->all Normal; Hard denied if any member lacks Normal S30; DQ never grants progression; replay never double-awards.
- Ready races: member edits car while leader clicks Start; member joins/leaves during commit; a stale ready packet; a network retry; delayed load; leader loss; all humans disappear; one client with wrong content version. Each yields one coherent authoritative outcome.
- Leader browsing alone never moves other clients. All-ready Campaign transition does. Shared stage update never discards another person's unsaved garage draft; warn and require explicit apply/discard before ready.
- Midload/midrace disconnect at each lifecycle step; no player re-enters driving; other entrants continue; spectate cycling handles target departure; no duplicate car/avatar/parking slot after reconnect.
- Pure-PvP winner bonus only with two eligible human finishers, never ghost/AI/forfeit farming conditions. Exiting after accepted finish retains settlement.
- Purchase, reward, challenge and rank settlement retried 100 times has one economic effect. Wallet at 9,999,998 receiving a reward clamps to 9,999,999. Invalid negative prices, NaN values, mismatched inventory, and oversized decal payloads rejected.
- Total RP exactly15,000; 75 unique reward IDs; tier counts25/25/25; all items equippable; no part in challenge rewards; no final-rank dependence on another human or prestige purchase.
- All courses: legal navigation/checkpoints, six-spawn clearance, no boundary escape/fast wall ride, no missing material, correct AI/ghost data, return to menus, telemetry screenshots. Drive scenery transitions at expected max speed.
- A UI screenshot/contact-sheet comparison for all main screens and all six regional kits; not only screenshots of the first course. All 48 rivals recognizable in silhouette/monochrome checks and named records.
- 30-minute populated meet soak and repeated menu/scene changes; no growing allocation/listener queues, ghost visitors, duplicate alerts, or stranded ready states. Measures need labelled machine/build/transport and bounds, not universal fps claims.

Do not delete tests to make a redesign pass. Fix actual regressions; when a harness is wrong, explain and prove why. A bot that teleports to the finish or writes quest flags does not prove the race playable. A content validator can confirm counts/references but not whether driving is fun or an outfit distinctive.

## 20. Execution, checkpoints, and completion honesty

Keep a compact requirements ledger with states not started/in progress/implemented/unverified/verified/blocked. Map every section and catalogue item to source, actual assets, tests, and representative evidence. Separate current source, last committed source, verified remote source, latest built client/server, and tested release. Use content/physics/protocol/build hashes so “last updated two hours ago” can be explained from actual history, not guessed.

Checkpoint after meaningful milestones and before long/risky operations, aiming for no more than about 10–15 minutes of substantive unpushed work at safe boundaries. Commit and push to the authorized task branch, verify the remote SHA, and confirm necessary LFS assets can be obtained. Do not force-push, delete branches, merge main, publish, or deploy without authorization. One coordinator owns integration/Unity scene writes; parallel workers have bounded files/content areas and cannot independently overwrite the same scene, .meta GUIDs, or package settings.

Maintain SPECIFICATION.md, CLAUDE.md, HANDOFF.md, REQUIREMENTS.md, VALIDATION.md, CONTENT_COVERAGE.json, BUILD_MANIFEST.json, docs/{ARCHITECTURE,ART_DIRECTION,ECONOMY,NETWORKING,HOSTING,UNITY_SETUP}. Keep docs concise except for real authored content. No self-referential commit hash loop: a report can identify the tested source and parent checkpoint; the final message reports the verified tip.

If usage/context/tool execution is interrupted, inspect actual working files, jobs, logs and last outcomes before retrying. Preserve newer work, don't pull main over it, don't start a second Unity editor on the same active project, and don't assume a background build still runs. Reuse valid evidence; invalidate tests affected by changes. Recover, state next action briefly, then continue unblocked work. Never claim a Cloud session can reach a Windows editor simply because a path was written into the prompt.

Final deliverables: source and assets; reproducible project; Windows playable build; Windows/Linux server build and local stack/start scripts; migration/config templates; content and build manifests; validated networking instructions; selected real screenshots/video; original audio and provenance; complete campaign/cosmetic content; and a report separating actual execution, simulations, code inspection, human testing, blocked deployment, and known bugs.

A source-only handoff is not a verified native build. A local build is not an internet deployment. A successful sign-in is not secure reward validation. A connected socket is not fun six-person racing. A green test suite is not proof that all art and narrative goals have been met. State precisely what is and is not delivered.

Build the game through the complete assignment, using the following authored catalogues as binding launch content. Do not stop at the first polished slice or silently rewrite minimums to fit a tool-output limit.

# APPENDIX A — THE 26 AUTHORED COURSE BRIEFS

These are original course concepts. Dimensions are initial authoring targets, not measurements of built content. Record actual shipped length/elevation. Preserve distinctive geometry, sector rhythm and landmarks. Normal stages use the default conditions; Hard variations are explicit in Appendix B. Expected seconds are economy inputs, not fabricated leaderboard records. Publish real measured benchmark times only after driving/validation.

Road width starts around 7–9 metres on ordinary sections, with clearly telegraphed 6-metre technical constrictions and 10–12 metre grids/passing areas. Use smooth transitions. Build full surrounding terrain, not a ribbon in a void. Circuit length is per lap; listed expected event duration includes the prescribed two laps.

## T00 — Hinode Driving Grounds
Region: Training campus. Target length 1.8 km; net elevation +0 m; expected event duration for economy 180 s. Format: Loop, skid pad, braking lanes.
Sector composition: Wide apron into a gentle slalom; a banked demonstration bend; separate drift circle and recovery bay.
Required landmarks: Timing pavilion; striped braking lane; wind-sock gantry.
Default conditions: Dry daylight with a separately selected wet practice section.
Specific art/handling requirement: The skid pad is not part of a paid race route; six independent training bays and a shared non-contact loop.

## C01 — Tea Lantern Road
Region: Mizuhana foothills. Target length 3.1 km; net elevation -80 m; expected event duration for economy 180 s. Format: Accessible downhill sprint.
Sector composition: Broad opening left; two mild alternating bends through fields; one clearly sighted hairpin before a flat finish.
Required landmarks: Tea-drying shed; low stone bridge; hanging lantern row.
Default conditions: Late afternoon, dry.
Specific art/handling requirement: Teach braking and exit speed; broad safe shoulders, no blind punishment on the first race.

## C02 — Mizuhana Switchback
Region: Mizuhana foothills. Target length 3.8 km; net elevation +160 m; expected event duration for economy 215 s. Format: Uphill sprint.
Sector composition: Village-edge acceleration; five deliberately unequal hairpins; an opening-radius final S.
Required landmarks: Terraced tea rows; split retaining wall; hilltop water tank.
Default conditions: Blue hour, dry.
Specific art/handling requirement: Different climbing ratios matter; never mirror the same hairpin five times.

## C03 — Canal Orchard Ring
Region: Mizuhana foothills. Target length 2.6 km; net elevation +0 m; expected event duration for economy 230 s. Format: Two-lap circuit.
Sector composition: Canal-side sweep; tight orchard offset; raised bridge braking into long exit.
Required landmarks: Canal sluice; greenhouse frames; covered packing yard.
Default conditions: Clear evening, dry.
Specific art/handling requirement: Provide a genuinely closed loop, clear lap gates and a distinct overpass silhouette.

## C04 — Bellmaker Descent
Region: Mizuhana foothills. Target length 4.5 km; net elevation -260 m; expected event duration for economy 260 s. Format: Downhill sprint.
Sector composition: Fast hill shoulder; compression into linked corners; late double-apex right by workshops.
Required landmarks: Bell workshop wheel; stone stair wall outside course; red covered footbridge.
Default conditions: Dusk, dry.
Specific art/handling requirement: Lieutenant rematch location; visually teach not every entrance should be attacked at full speed.

## C05 — Cedar Ribbon
Region: Kasumi forest. Target length 4.2 km; net elevation +70 m; expected event duration for economy 230 s. Format: Undulating sprint.
Sector composition: Dark cedar corridor; three rhythm esses; an open overlook followed by an off-camber turn.
Required landmarks: Forest rail trestle; timber shelter; illuminated reservoir glimpse.
Default conditions: Night, dry.
Specific art/handling requirement: Foliage density varies by sector; readable edge reflectors and uncluttered apexes.

## C06 — Shrine Approach
Region: Kasumi forest. Target length 3.9 km; net elevation +240 m; expected event duration for economy 235 s. Format: Technical uphill sprint.
Sector composition: Long braking approach; steep zigzag below a wooded ridge; final paired bends onto a plateau.
Required landmarks: Distant shrine roof; cedar gate well outside road; old tram station.
Default conditions: Night, dry.
Specific art/handling requirement: The event uses the access road, not a sacred courtyard; retain cultural specificity without decorative nonsense.

## C07 — Mistglass Hollow
Region: Kasumi forest. Target length 4.7 km; net elevation -210 m; expected event duration for economy 275 s. Format: Forest downhill sprint.
Sector composition: Long visible entry sweeps; low valley mist over straight sections; tightening woodland sequence.
Required landmarks: Mossed aqueduct; fog-measuring mast; abandoned viewing platform.
Default conditions: Cool mist with maintained apex visibility.
Specific art/handling requirement: Fog is atmospheric and telegraphed; never conceal road geometry beyond the safe stopping sight line.

## C08 — Rain Thread Pass
Region: Kasumi forest. Target length 5.2 km; net elevation +90 m; expected event duration for economy 300 s. Format: Mixed-elevation wet sprint.
Sector composition: Sheltered launch; wet cambered arcs; short tunnel into a long drainage-side descent.
Required landmarks: Rain chains at a shelter; yellow maintenance bridge; waterfall beside a tunnel exit.
Default conditions: Light rain at night.
Specific art/handling requirement: Wet sheen, grip and AI line agree; no randomly placed invisible ice.

## C09 — Spillway Run
Region: Kurogawa reservoir. Target length 4.9 km; net elevation -140 m; expected event duration for economy 265 s. Format: Reservoir sprint.
Sector composition: Dam-crest crosswind section; braking past spillway gates; sheltered rising exit bends.
Required landmarks: Spillway towers; concrete fishway; flood-level markings.
Default conditions: Overcast dusk, dry.
Specific art/handling requirement: Crosswind is primarily audiovisual in the baseline; do not add untested random steering forces.

## C10 — Hydroline Climb
Region: Kurogawa reservoir. Target length 5.6 km; net elevation +360 m; expected event duration for economy 310 s. Format: Long uphill sprint.
Sector composition: Powerhouse launch; multi-radius climbing turns; fast final shelf road.
Required landmarks: Switchyard silhouettes; pipeline arch; cable-service station.
Default conditions: Clear night.
Specific art/handling requirement: Sustained ascent differentiates torque/gearing; road markings and power assets are original.

## C11 — Floodmark Circuit
Region: Kurogawa reservoir. Target length 3.2 km; net elevation +0 m; expected event duration for economy 280 s. Format: Two-lap technical circuit.
Sector composition: Lakeside kink; stepped retaining-wall sector; wide pump-yard exit.
Required landmarks: Water-level stair; twin pump houses; circular service tank.
Default conditions: Dawn, damp.
Specific art/handling requirement: No impossible loop connection: use a separate service viaduct to return to the starting basin.

## C12 — Blackwater Hairpins
Region: Kurogawa reservoir. Target length 6.2 km; net elevation -470 m; expected event duration for economy 350 s. Format: Difficult downhill sprint.
Sector composition: Quiet launch shelf; asymmetrical hairpin ladder; long braking turn after a tunnel.
Required landmarks: Flood memorial sign outside course; blue tunnel marker; spillway overlook.
Default conditions: Night, dry.
Specific art/handling requirement: A late-boss course: descent rhythm and brake release matter more than maximum power.

## C13 — Saltwind Viaduct
Region: Akebono coast. Target length 5.4 km; net elevation +80 m; expected event duration for economy 260 s. Format: Fast coastal sprint.
Sector composition: Rising sea viaduct; linked cliff arcs; slow port-side finishing chicane.
Required landmarks: Lighthouse beam; rust-red bridge truss; coast railway below.
Default conditions: Sunset, dry.
Specific art/handling requirement: Fast sectors have safe barrier continuity and visibly different sea/land edges.

## C14 — Fishmarket Midnight
Region: Akebono coast. Target length 3.0 km; net elevation +0 m; expected event duration for economy 255 s. Format: Two-lap urban-port circuit.
Sector composition: Closed warehouse lane; market-roof S bends; quay-side braking into a square turn.
Required landmarks: Shuttered market signs; crane silhouettes; tiled freight office.
Default conditions: Night, dry.
Specific art/handling requirement: No active pedestrians or public traffic on the closed course; scenery extends beyond fence lines.

## C15 — Tide Lantern Coast
Region: Akebono coast. Target length 5.8 km; net elevation -100 m; expected event duration for economy 300 s. Format: Coastal rhythm sprint.
Sector composition: Upland sweep; sea-wall undulations; three late-apex corners by the inlet.
Required landmarks: Storm gate; lit ferry terminal; stacked fishing floats behind fencing.
Default conditions: Early night, damp.
Specific art/handling requirement: Road crowns and wet patches are authored/consistent for every entrant.

## C16 — Breakwater Ascent
Region: Akebono coast. Target length 6.1 km; net elevation +310 m; expected event duration for economy 330 s. Format: Coast-to-hill uphill sprint.
Sector composition: Harbour launch; steep cliff zigzag; broad high-road acceleration to finish.
Required landmarks: Breakwater lamps; hillside funicular; ridge radio dish.
Default conditions: Night, dry.
Specific art/handling requirement: Course rises out of the port instead of copying a forest track with a sea skybox.

## C17 — Slatecut Service Road
Region: Hoshimi uplands. Target length 5.2 km; net elevation +120 m; expected event duration for economy 290 s. Format: Quarry-edge sprint.
Sector composition: Concrete loading apron; rock-cut alternating bends; a bridge over an inactive quarry.
Required landmarks: Stepped quarry walls; conveyor frame; surveyor hut.
Default conditions: Late afternoon, dry.
Specific art/handling requirement: Road is paved and closed; gravel shoulder has a clear physical boundary.

## C18 — Kilnlight Ring
Region: Hoshimi uplands. Target length 3.5 km; net elevation +0 m; expected event duration for economy 310 s. Format: Two-lap industrial circuit.
Sector composition: Broad kiln approach; tight cooling-yard loop; under-bridge compression with safe sight line.
Required landmarks: Brick kiln chimneys; cooling-tower ribs; enamel workshop mural.
Default conditions: Night, dry.
Specific art/handling requirement: Warm industrial windows and cooler exterior lighting create material contrast without neon overload.

## C19 — Switchyard Skyline
Region: Hoshimi uplands. Target length 6.4 km; net elevation +290 m; expected event duration for economy 350 s. Format: High-speed technical climb.
Sector composition: Reservoir-link approach; descending-radius arcs beside pylons; climbing final hairpin group.
Required landmarks: Powerline crown; cantilever overlook; illuminated service lift.
Default conditions: Clear night.
Specific art/handling requirement: Rhythm changes deliberately; no endless straight that decides the event solely by top speed.

## C20 — Ember Quarry Drop
Region: Hoshimi uplands. Target length 6.7 km; net elevation -520 m; expected event duration for economy 375 s. Format: Expert downhill sprint.
Sector composition: High quarry shelf; alternating braking/compression; long final descent into workshop valley.
Required landmarks: Red stone cutting face; steel lattice bridge; restored machine hall.
Default conditions: Night, damp.
Specific art/handling requirement: Final lieutenant location; fast road has broad recovery zones before the most severe bends.

## C21 — Moonridge Contour
Region: Tsukishiro highland. Target length 6.0 km; net elevation +170 m; expected event duration for economy 325 s. Format: Highland contour sprint.
Sector composition: Open ridge arcs; old snow-shed transition; fast plateau S section.
Required landmarks: Moon-view pavilion; disused snow fence; distant observatory.
Default conditions: Moonlit dry night.
Specific art/handling requirement: Cold visual palette without automatically adding ice; headlight falloff remains readable.

## C22 — White Pine Descent
Region: Tsukishiro highland. Target length 6.8 km; net elevation -430 m; expected event duration for economy 370 s. Format: Highland downhill sprint.
Sector composition: Pine-lined openers; long cambered descent; final triple bend around a frozen-looking rock face.
Required landmarks: White-bark pine grove; stone avalanche gallery; weather station.
Default conditions: Cold dawn, dry.
Specific art/handling requirement: Frost on scenery is not road ice; road-grip state is explicit and consistent.

## C23 — Signal Tower Climb
Region: Tsukishiro highland. Target length 7.2 km; net elevation +580 m; expected event duration for economy 400 s. Format: Expert uphill sprint.
Sector composition: Low antenna valley; steep and slow middle; high-flow ridge approach.
Required landmarks: Cable anchorage; transmitter service building; beacon mast.
Default conditions: Clear pre-dawn.
Specific art/handling requirement: Acceleration cannot replace a good exit; apex lighting makes the final climb learnable.

## C24 — Four Signals Traverse
Region: Tsukishiro highland. Target length 8.4 km; net elevation +110 m; expected event duration for economy 420 s. Format: Four-sector mastery sprint.
Sector composition: Four different sectors: technical entry; sustained drift arcs; braking descent; smooth high-road finish.
Required landmarks: Four coloured marshal beacons; stone gallery; old timing cabin.
Default conditions: Pre-dawn, dry.
Specific art/handling requirement: Normal regular race first; S29 reuses the course with explicit four-contract mastery judging, not a new course ID.

## C25 — Amanagi Dawnline
Region: Finale mountain. Target length 10.2 km; net elevation -640 m; expected event duration for economy 420 s. Format: Dedicated final-boss sprint.
Sector composition: Ridge launch and linked switchbacks; long horizon sweep into tunnels; changing-radius descent into sunrise.
Required landmarks: Old radio relay arch; two-level valley bridge; sunrise finish above the sea.
Default conditions: Normal: night to dawn lighting; Hard: authored mist clearing into dawn.
Specific art/handling requirement: At least five sectors and a unique route; lighting progression is deterministic, never changed grip or hidden hazards during the race.

# APPENDIX B — 30 STAGES, NOT 30 CLAIMED UNIQUE COURSES

Stage order is absolute and mode-specific progress follows it. Tutorial T00 is not S01. Course variants keep their base course ID. Regular stages use the course's sprint/circuit format; drift-focused challenges and Freeplay provide dedicated score events. The penultimate is the one mixed mastery race.

For regular stages, use the listed normal lead and a deduplicated legal support pool. Hard lead replaces it; fill support from that crew's remaining members and the current stage's previously introduced rivals without selecting locked captains/final rivals early. Never exceed five live AI with a solo player. Where a primary car cannot fit an event cap, use a declared legal alternate loaner and show it; do not falsify its PI. Featured normal/hard final cars have authored legal reference tunes, not impossible stock statistics.

Difficulty calibration: produce a real clean reference run in a freely available class-legal loaner per event/ruleset. Let P be that representative reference time. Initial Normal targets progress from approximately 1.18×P in the earliest events toward 1.05×P in the final act; lieutenant targets interpolate appropriately. Hard targets begin near 1.04×P and reach approximately P for the legend, using each Hard condition's OWN reference. Freeze exact measured milliseconds in content after calibration. These margins are tuning targets, not a substitute for real human checks. Do not force a fragile distinction of fractions of a second on an untested physics build. The UI exposes the final number, not this authoring formula.

An AI's race placing and the benchmark-clear goal remain distinct. A change of car class or physics invalidates/requires revalidating reference records. Reversing a track does not automatically yield a valid reverse benchmark.


## S01 — REGULAR / C01 / Act 1
Normal featured rival: R01; support pool: R05, R06, R07, R02. Hard featured rival: R05. Maximum PI: 299.
Normal story beat: An invitation arrives with a timing slip, not a dare. Learn why the tea-road people race.
Hard variation: dawn, damp; greater emphasis on smooth exits

## S02 — REGULAR / C02 / Act 1
Normal featured rival: R02; support pool: R05, R06, R07, R03. Hard featured rival: R06. Maximum PI: 299.
Normal story beat: Kei says a convoy is a promise to wait at the next turnout; Toma calls that slow. Test the difference.
Hard variation: blue hour, dry; tighter uphill benchmark

## S03 — REGULAR / C03 / Act 1
Normal featured rival: R03; support pool: R05, R06, R07, R04. Hard featured rival: R07. Maximum PI: 299.
Normal story beat: The orchard loop exposes a disagreement between a good single sector and a repeatable lap.
Hard variation: night, dry; two-lap consistency emphasis

## S04 — REGULAR / C04 / Act 1
Normal featured rival: R04; support pool: R05, R06, R07, R01. Hard featured rival: R01. Maximum PI: 499.
Normal story beat: Hanae repairs a cracked cup while everyone argues about the fastest line. The return matters.
Hard variation: pre-dawn, damp; late brake-release emphasis

## S05 — REGULAR / C05 / Act 1
Normal featured rival: R09; support pool: R13, R14, R15, R10. Hard featured rival: R13. Maximum PI: 499.
Normal story beat: Rainline arrives prepared for weather nobody else noticed. Preparation becomes a personality.
Hard variation: night, damp; alternate legal exit lines

## S06 — REGULAR / C06 / Act 1
Normal featured rival: R10; support pool: R13, R14, R15, R11. Hard featured rival: R14. Maximum PI: 499.
Normal story beat: The roadside radio mentions an unrecorded dawn run, then changes the subject.
Hard variation: clear dawn, dry; reduced margin for early braking

## S07 — LIEUTENANT / C04 / Act 1
Normal featured rival: R08; support pool: R05, R06, R07, R01. Hard featured rival: R08. Maximum PI: 499.
Normal story beat: Daigo asks for a repeatable controlled run and, after it, gives the convoy a repaired radio aerial.
Hard variation: Damp Bellmaker surface; later controlled braking and deliberate wider exits, with new mentor dialogue.

## S08 — REGULAR / C07 / Act 2
Normal featured rival: R11; support pool: R13, R14, R15, R12. Hard featured rival: R15. Maximum PI: 699.
Normal story beat: A weather log contradicts the dry-road advice copied in the player diary.
Hard variation: controlled mist, damp; maintain sight-line readability

## S09 — REGULAR / C08 / Act 2
Normal featured rival: R12; support pool: R13, R14, R15, R09. Hard featured rival: R09. Maximum PI: 699.
Normal story beat: Emi admits that uncertainty is not the same as randomness. Read the actual surface.
Hard variation: steady rain, wet; calibrated wet reference

## S10 — REGULAR / C09 / Act 2
Normal featured rival: R17; support pool: R21, R22, R23, R18. Hard featured rival: R21. Maximum PI: 699.
Normal story beat: Reservoir Section challenges the convoy to name its reference point before chasing a number.
Hard variation: dawn, damp; precision across expansion joints

## S11 — REGULAR / C10 / Act 2
Normal featured rival: R18; support pool: R21, R22, R23, R19. Hard featured rival: R22. Maximum PI: 699.
Normal story beat: A loose mechanical pencil rolls across a map. Kaede points out how elevation changes the argument.
Hard variation: clear night, dry; gear/exit emphasis

## S12 — REGULAR / C11 / Act 2
Normal featured rival: R19; support pool: R21, R22, R23, R20. Hard featured rival: R23. Maximum PI: 699.
Normal story beat: Michi finds an old timing slip with a missing final sector, the first concrete sign of Zero Signal.
Hard variation: night, wet; consistency across both laps

## S13 — REGULAR / C12 / Act 2
Normal featured rival: R20; support pool: R21, R22, R23, R17. Hard featured rival: R17. Maximum PI: 699.
Normal story beat: Jun refuses to call the fastest accident a good run. The convoy has to show control.
Hard variation: pre-dawn, damp; no arbitrary visibility loss

## S14 — LIEUTENANT / C08 / Act 2
Normal featured rival: R16; support pool: R13, R14, R15, R09. Hard featured rival: R16. Maximum PI: 699.
Normal story beat: Emi challenges an assumption made in the previous rain stage; she shares a weather notebook, not a victory sermon.
Hard variation: Steady wet Rain Thread; longer safe lines and sharper traction-management benchmark, not secret grip.

## S15 — REGULAR / C13 / Act 3
Normal featured rival: R25; support pool: R29, R30, R31, R26. Hard featured rival: R29. Maximum PI: 849.
Normal story beat: Harbour voices break the technical tension; a bold line is welcome if it remains legal.
Hard variation: blue hour, dry; tighter long-arc exits

## S16 — REGULAR / C14 / Act 3
Normal featured rival: R26; support pool: R29, R30, R31, R27. Hard featured rival: R30. Maximum PI: 849.
Normal story beat: Maya turns a rivalry into a poster and accidentally tells both sides what the other values.
Hard variation: dawn, damp; measured urban-braking precision

## S17 — REGULAR / C15 / Act 3
Normal featured rival: R27; support pool: R29, R30, R31, R28. Hard featured rival: R31. Maximum PI: 849.
Normal story beat: The convoy earns a place at a closed night event after helping the timing crew solve a small problem.
Hard variation: night, wet; authored surface transitions

## S18 — REGULAR / C16 / Act 3
Normal featured rival: R28; support pool: R29, R30, R31, R25. Hard featured rival: R25. Maximum PI: 849.
Normal story beat: Mako watches instead of racing, and notices who checks their friends' results first.
Hard variation: pre-dawn, dry; torque-management emphasis

## S19 — REGULAR / C17 / Act 3
Normal featured rival: R33; support pool: R37, R38, R39, R34. Hard featured rival: R37. Maximum PI: 849.
Normal story beat: Datum Works offers exact numbers and a very selective definition of success.
Hard variation: dusk, damp; clean shoulder boundaries

## S20 — REGULAR / C18 / Act 3
Normal featured rival: R34; support pool: R37, R38, R39, R35. Hard featured rival: R38. Maximum PI: 849.
Normal story beat: Chika asks why everyone talks about power before asking what the car feels like.
Hard variation: night, dry; alternate support-rival lineup

## S21 — LIEUTENANT / C12 / Act 3
Normal featured rival: R24; support pool: R21, R22, R23, R17. Hard featured rival: R24. Maximum PI: 849.
Normal story beat: Jun measures a technically perfect run against a run that leaves options; the convoy earns a more useful respect.
Hard variation: Cold damp Blackwater; altered braking-release pattern and legal car setup; Jun admits a changed assumption.

## S22 — REGULAR / C19 / Act 4
Normal featured rival: R35; support pool: R37, R38, R39, R36. Hard featured rival: R39. Maximum PI: 999.
Normal story beat: Reina appears briefly and recognizes the old timing slip without acknowledging it.
Hard variation: pre-dawn, damp; composure after compression

## S23 — REGULAR / C20 / Act 4
Normal featured rival: R36; support pool: R37, R38, R39, R33. Hard featured rival: R33. Maximum PI: 999.
Normal story beat: A workshop argument reveals that Reina once depended on somebody she no longer mentions.
Hard variation: night, wet; tighter safe braking envelope

## S24 — REGULAR / C21 / Act 4
Normal featured rival: R41; support pool: R45, R46, R47, R42. Hard featured rival: R45. Maximum PI: 999.
Normal story beat: Fuyu describes the high road without turning it into a supernatural test.
Hard variation: pre-dawn, dry; no artificial ice

## S25 — REGULAR / C22 / Act 4
Normal featured rival: R42; support pool: R45, R46, R47, R43. Hard featured rival: R46. Maximum PI: 999.
Normal story beat: Kazuma plays an old, nearly silent radio recording. What is absent becomes important.
Hard variation: cold night, damp; published wet setup

## S26 — REGULAR / C23 / Act 4
Normal featured rival: R43; support pool: R45, R46, R47, R44. Hard featured rival: R47. Maximum PI: 999.
Normal story beat: Yuna reveals the restored dawn car. Shiori is nearby, asking about tyre temperatures.
Hard variation: first light, dry; momentum through steep final sector

## S27 — REGULAR / C24 / Act 4
Normal featured rival: R44; support pool: R45, R46, R47, R41. Hard featured rival: R41. Maximum PI: 999.
Normal story beat: The four marshal beacons are lit. Each represents a different way to lose or preserve momentum.
Hard variation: dawn, dry; all four sectors assessed more tightly

## S28 — LIEUTENANT / C20 / Act 4
Normal featured rival: R32; support pool: R29, R30, R31, R25. Hard featured rival: R32. Maximum PI: 999.
Normal story beat: Mako drops the performance persona and asks whether the convoy can stay composed when everyone is watching.
Hard variation: Wet Ember Drop; restrained exit-focused strategy replaces showy rotation; Mako foreshadows Shiori explicitly.

## S29 — PENULTIMATE / C24 / Act 4
Normal featured rival: R39; support pool: R37, R38, R45, R46. Hard featured rival: R47. Maximum PI: 999.
Normal story beat: Four Signals: the convoy must reconcile precision, controlled drift, braking and flow before the final invitation is accepted.
Hard variation: Yuna conducts the same four-contract test with tighter legal targets and a different dialogue about preserving the car, before Shiori agrees to race.

## S30 — FINALE / C25 / Act 4
Normal featured rival: R40; support pool: R33, R34, R35, R36. Hard featured rival: R48. Maximum PI: 999.
Normal story beat: Reina races the convoy at Amanagi and must confront why a shared result need not erase individual skill. Include a real ending and meet epilogue.
Hard variation: Shiori replaces Reina as the fixed final featured rival. Her restored V04 and measured momentum-focused dawn run are entirely legal; the ending completes the radio/timing-slip story.

## S29 Four Signals judging contract

Use C24's four authored sectors in this order: Entry, Arc, Descent, Horizon. Complete the whole route; it is one race, not four disconnected menus.
- Entry: beat the published legal time for that sector and touch its two apex gates.
- Arc: bank the published raw drift target across the three marked corners; no donut/repeat scoring.
- Descent: finish the sector with no meaningful wall impact or reset, passing the brake-release target zone legally.
- Horizon: beat its published momentum/exit-speed targets and the full-route time limit.
A qualifying driver passes all four contracts. A supporting driver passes at least three, finishes legally, and meets the normal/Hard support time envelope. Normal team success needs one qualifying human; Hard needs ceil(H/2) as in the core rule. Use freely supplied appropriate loaners; tuning ownership is never mandatory. Partial results show which contract failed and offer specific practice.

## Finale and post-story contract

Normal S30 must always feature R40 Reina. Hard S30 must always feature R48 Shiori. Neither can be silently replaced by random selection, even when using the six-human benchmark presentation. Normal ends with a short terrace reunion and individual rival reactions. Hard ends with the dawn-run story resolved and Shiori at the radio bench. After either, retain campaign replay, freeplay, ghosts, customization, meet access and the remaining challenges. Do not delete the world or force New Game. Avoid a new infinite grind currency system after the ending.


# APPENDIX C — 18 FICTIONAL CAR MODELS

All shapes, makes and liveries are original. Numerical vehicle specs are fictional initial tuning targets in kW/Nm/kg, not statements about real cars. Validate actual performance and revise PI if measurements disagree. No badge-swapped real models. Every car needs a recognizable 3D silhouette, lawful original asset source, and meaningful handling identity.

## V01 — Kogane Hachi RS
Body: compact 1980s liftback. Layout: front engine / RWD. Base target PI 220; mass 900 kg; power 108 kW; torque 160 Nm. Price: 24,000 credits.
Silhouette: short wheelbase, narrow angular glasshouse, rectangular sealed lamps.
Handling: Responsive rotation and gentle power; lower straight-line speed; free starter choice.

## V02 — Mizuno Kite GT
Body: 1990s three-door hatch. Layout: front engine / FWD. Base target PI 245; mass 1030 kg; power 125 kW; torque 175 Nm. Price: 26,000 credits.
Silhouette: tall rear hatch, offset grille, wedge side windows.
Handling: Predictable entry and wet confidence; throttle understeer; free starter choice.

## V03 — Arata Rainfox 4
Body: compact 1990s sedan. Layout: front engine / AWD. Base target PI 270; mass 1210 kg; power 142 kW; torque 215 Nm. Price: 32,000 credits.
Silhouette: squared arches, slim boot, divided rear lamps.
Handling: Stable traction and forgiving exits; more weight and less free rotation; free starter choice.

## V04 — Kogane Kestrel S
Body: light roadster. Layout: front engine / RWD. Base target PI 295; mass 940 kg; power 135 kW; torque 170 Nm. Price: 42,000 credits.
Silhouette: low oval nose, small cabin, long open rear deck.
Handling: Momentum-focused; needs clean exits; removable roof is cosmetic.

## V05 — Shinmei Linea 20
Body: 1990s long-nose coupe. Layout: front engine / RWD. Base target PI 380; mass 1220 kg; power 183 kW; torque 250 Nm. Price: 65,000 credits.
Silhouette: notched rear quarter, long bonnet, stacked corner lamps.
Handling: Flexible tuning; moderate chassis mass; lieutenant-friendly early build.

## V06 — Mizuno Sparrow Type-S
Body: wide-track hot hatch. Layout: front engine / FWD. Base target PI 405; mass 1120 kg; power 174 kW; torque 215 Nm. Price: 72,000 credits.
Silhouette: short overhangs, arched roof, horizontal split grille.
Handling: Quick front-end response; must manage front grip when powering out.

## V07 — Arata Shigure GT
Body: touring wagon. Layout: front engine / AWD. Base target PI 430; mass 1410 kg; power 205 kW; torque 300 Nm. Price: 80,000 credits.
Silhouette: long cargo roof, roof rails, six-window side profile.
Handling: Composed rough-road exits; slow rotation and higher braking demand.

## V08 — Hokusei Lanceray R
Body: rally-derived four-door. Layout: front engine / AWD. Base target PI 530; mass 1290 kg; power 246 kW; torque 355 Nm. Price: 115,000 credits.
Silhouette: compact trunk, raised hood intake, triangular lamp internals.
Handling: Strong exits and wet lines; can understeer with an aggressive diff.

## V09 — Shinmei Crestline 26
Body: six-cylinder grand-tourer. Layout: front engine / RWD. Base target PI 510; mass 1450 kg; power 231 kW; torque 330 Nm. Price: 108,000 credits.
Silhouette: broad shoulder, very long hood, recessed oval tail lamps.
Handling: Stable fast arcs; less agile in hairpins; strong linear pull.

## V10 — Kogane Spiral RX
Body: rotary-like lightweight coupe. Layout: front engine / RWD. Base target PI 580; mass 1190 kg; power 250 kW; torque 285 Nm. Price: 145,000 credits.
Silhouette: smooth teardrop cabin, rounded rear quarters, twin-slot lights.
Handling: Fast response/high revs; careful ratio choice matters on steep climbs.

## V11 — Mizuno Vector MR
Body: compact mid-engine coupe. Layout: mid engine / RWD. Base target PI 590; mass 1110 kg; power 236 kW; torque 275 Nm. Price: 150,000 credits.
Silhouette: short nose, side cooling channels, angular buttresses.
Handling: Precise rotation and balance; needs smooth braking release.

## V12 — Shinmei Stormline 32
Body: heavy performance sedan. Layout: front engine / AWD. Base target PI 650; mass 1510 kg; power 294 kW; torque 410 Nm. Price: 190,000 credits.
Silhouette: broad rectangular silhouette, twin-intake nose, squared wing mounts.
Handling: Powerful traction but expensive momentum mistakes; not best on every course.

## V13 — Arata Comet S6
Body: modern lightweight sports coupe. Layout: front engine / RWD. Base target PI 710; mass 1260 kg; power 306 kW; torque 370 Nm. Price: 220,000 credits.
Silhouette: low shark nose, tapered trunk, separate slim running lamps.
Handling: Good high-speed balance; steeper costs and grip-sensitive peak tune.

## V14 — Hokusei Vale R
Body: sport fastback. Layout: front engine / FWD. Base target PI 680; mass 1230 kg; power 278 kW; torque 355 Nm. Price: 205,000 credits.
Silhouette: large fastback hatch, blade spoiler, tall front shoulders.
Handling: Unusual high-power FWD challenge; braking/exit planning beats brute force.

## V15 — Kogane Halo T
Body: compact turbo coupe. Layout: front engine / RWD. Base target PI 740; mass 1240 kg; power 320 kW; torque 425 Nm. Price: 235,000 credits.
Silhouette: short sculpted body, boxed rear haunches, low wraparound glass.
Handling: Big midrange torque; boost delivery can unbalance poor throttle control.

## V16 — Mizuno Meridian X
Body: mid-engine flagship. Layout: mid engine / RWD. Base target PI 790; mass 1320 kg; power 351 kW; torque 430 Nm. Price: 300,000 credits.
Silhouette: low cabin-forward wedge, wide side intakes, deep rear diffuser.
Handling: Quick and exacting; never a mandatory purchase or universal best car.

## V17 — Shinmei Aurora 4S
Body: modern all-wheel grand-tourer. Layout: front engine / AWD. Base target PI 810; mass 1580 kg; power 390 kW; torque 540 Nm. Price: 330,000 credits.
Silhouette: long roofline, muscular four-seat coupe stance, split rear bar.
Handling: Strong acceleration/stability; heavier braking and tyre loading.

## V18 — Hokusei Solstice Z
Body: balanced six-cylinder sport coupe. Layout: front-mid engine / RWD. Base target PI 835; mass 1430 kg; power 375 kW; torque 485 Nm. Price: 350,000 credits.
Silhouette: compact cabin, deep sculpted doors, round-over rear deck.
Handling: High performance with controllable rotation; an alternative, not a required finale key.

# APPENDIX D — 48 DISTINCT RIVAL IDENTITIES

The roster below supplies required initial identities, not 48 interchangeable dialogue templates. Expand each into a modest full character sheet with relationships, gestures, at least four race-intro/reaction lines, a defeat/rematch response, a distinctive original livery, an observable driving parameter profile and appropriate scene/Freeplay appearances. Do not repeatedly use the provided sample line as their entire personality. Every rival needs an authored victory and loss reaction; record dynamic player/convoy names safely, not by rewriting player dialogue.

Shared skeletons/controllers are allowed. Silhouette differences, body/hair, outfit cuts, accessory shapes, posture and performance preferences must survive the contact-sheet review. Livery motifs take their cue from the occupation/crew without copying logos. These adults need not be uniformly attractive, youthful, angry, or mysterious. Friendliness, humour and dignity should vary as much as driving technique.

The nineteen reusable driving tendencies are: momentum reader; early-set cornerer; exit traction specialist; brake-release student; inside-line defender; surface reader; rhythm linker; late-brake anchor; wet-line reader; straight-line planner; wide-entry specialist; gear optimizer; margin keeper; power conserver; geometric apexer; recovery specialist; rotation specialist; pressure tester; high-speed arc reader. Implement these as composable behaviours in the shared controller, not nineteen unrelated physics systems; preserve each named driver's observable combination and weakness. The Touring challenge Twelve Different Voices uses twelve explicitly selectable demonstrations from this list, so it never depends on random availability.


## Crew 1: Tea Hour Motor Club — Mizuhana
People who keep the morning tea deliveries and workshops moving; warm rivalries and small practical jokes.

### R01 — Sora Matsuda (23, he/him)
Look: slim; cropped hair; oversized delivery vest over striped sleeves; round key reel.
Personality: friendly fast talker who remembers everybody's order but forgets his own gloves.
Primary car: V01. Driving tendency: momentum reader. Strength: preserves speed in open third-gear bends. Weakness: overprotects momentum in very slow hairpins.
Sample introduction: “A clean exit buys more than a brave entrance.”

### R02 — Hanae Okuno (31, she/her)
Look: broad shoulders; tied locs; canvas apron folded over dark overalls; ceramic pin.
Personality: a potter who treats practice as craft and finds boasting mildly funny.
Primary car: V02. Driving tendency: early-set cornerer. Strength: stable brake release and repeatable entries. Weakness: gives away space to committed late overtakes.
Sample introduction: “I can do that corner again. Can you?”

### R03 — Kei Naruse (26, they/them)
Look: tall; asymmetric bob; cropped rain cape; checker cuff and ankle boots.
Personality: patient map collector who gets unexpectedly competitive about sector traces.
Primary car: V03. Driving tendency: exit traction specialist. Strength: strong uphill exits and wet stability. Weakness: carries too little speed into long sweepers.
Sample introduction: “The map ends. The next bend does not.”

### R04 — Toma Fujii (22, he/him)
Look: stocky; curly high top; patched racing cardigan; bright laces.
Personality: a bicycle mechanic learning to trust four wheels without hiding his nerves.
Primary car: V04. Driving tendency: brake-release student. Strength: quick low-speed rotation. Weakness: brakes too early under pressure.
Sample introduction: “I fixed the brakes. Now I have to stop apologizing to them.”

### R05 — Yui Morisaki (29, she/her)
Look: petite; short side-swept hair; fitted bomber with tool-loop belt; enamel moth badge.
Personality: a night-shift technician with dry humour and meticulous notebooks.
Primary car: V02. Driving tendency: inside-line defender. Strength: protects legal inner lines without swerving. Weakness: sacrifices exit when defending too long.
Sample introduction: “There is room. It just is not where you hoped.”

### R06 — Akio Endo (38, he/him)
Look: heavyset; shaved head; long work coat over knitwear; flour-dusted shoes.
Personality: an easygoing baker who dislikes being mistaken for an easy opponent.
Primary car: V03. Driving tendency: surface reader. Strength: finds composed lines across rough transitions. Weakness: slow to commit to a narrow passing opening.
Sample introduction: “The road has a crust too.”

### R07 — Noa Ishida (24, they/them)
Look: athletic; braided undercut; sleeveless utility layer; large translucent watch.
Personality: a candid volunteer timekeeper who loves tidy data and untidy jokes.
Primary car: V01. Driving tendency: rhythm linker. Strength: links alternating corners with little steering correction. Weakness: loses rhythm after an unexpected slow sector.
Sample introduction: “That was almost a sentence. Try the next corner.”

### R08 — Daigo Ibuki (41, he/him)
Look: very tall; salt-and-pepper crop; rolled-sleeve shop jacket; heavy brass stopwatch.
Personality: first lieutenant; generous teacher who will not congratulate careless speed.
Primary car: V05. Driving tendency: late-brake anchor. Strength: deep but controlled braking into tight turns. Weakness: long gears leave a weak exit when baited into overbraking.
Sample introduction: “Do not follow my brake lights. Find your own marker.”

## Crew 2: Rainline Atelier — Kasumi
A weather-conscious workshop collective; thoughtful preparation, different attitudes toward risk.

### R09 — Rika Aonuma (27, she/her)
Look: long limbs; high ponytail; fitted waterproof smock; asymmetric reflective piping.
Personality: a botanist who notices changing grip before anyone notices rain.
Primary car: V06. Driving tendency: wet-line reader. Strength: chooses predictable outside wet arcs. Weakness: less decisive on a rapidly drying ideal line.
Sample introduction: “Listen to the tyres, not the weather report.”

### R10 — Masato Kido (35, he/him)
Look: compact muscular build; swept-back hair; padded vest with narrow trousers; thumb ring.
Personality: a former courier who turns every trip into a carefully timed promise.
Primary car: V05. Driving tendency: straight-line planner. Strength: sets up two corners in advance for acceleration. Weakness: can be surprised by a slower tightening apex.
Sample introduction: “I promised I would arrive. I did not promise quietly.”

### R11 — Airi Shiba (21, she/her)
Look: short; twin braids; long knit scarf tucked safely into a short jacket; star barrette.
Personality: a music student who counts corners like phrases and hides frustration poorly.
Primary car: V04. Driving tendency: rhythm linker. Strength: natural left-right transitions. Weakness: rushes the first turn after a mistake.
Sample introduction: “You changed the beat. Good.”

### R12 — Rui Takeda (30, they/them)
Look: lean; shaved sides; quilted poncho shape; broad goggles at collar.
Personality: a reserved trail photographer with surprising appetite for a clean overtake.
Primary car: V07. Driving tendency: wide-entry specialist. Strength: turns wide setup into strong exits. Weakness: gives away an inside opportunity on narrow roads.
Sample introduction: “There is a better angle from out here.”

### R13 — Fumio Senda (46, he/him)
Look: round silhouette; moustache; short waxed coat; repair patches and knitted cap.
Personality: a forest-road maintainer protective of newcomers, impatient with excuses.
Primary car: V06. Driving tendency: surface reader. Strength: unsettled-road composure. Weakness: conservative high-speed commitments.
Sample introduction: “That patch was not there yesterday. It is there now.”

### R14 — Ena Kisaragi (28, she/her)
Look: tall angular build; close-cropped silver-dyed hair; long split-hem jacket; geometric earrings.
Personality: a sound engineer who hears a bad shift and cannot resist mentioning it.
Primary car: V04. Driving tendency: gear optimizer. Strength: keeps the engine in the useful band uphill. Weakness: rarely risks a sudden alternate line.
Sample introduction: “Your engine asked a question. You changed the subject.”

### R15 — Itsuki Hase (33, he/him)
Look: broad chest; wavy shoulder-length hair; sleeveless padded coat; stitched wristwrap.
Personality: a relaxed climber whose driving becomes exceptionally precise near exposure.
Primary car: V05. Driving tendency: margin keeper. Strength: repeatable safe arcs on narrow elevated roads. Weakness: lets an aggressive but clean rival dictate entry pace.
Sample introduction: “Close to the edge is not the same as in control.”

### R16 — Emi Takanashi (39, she/her)
Look: medium height; coiled bun; long storm-grey coat with a distinctive asymmetric collar; cedar clasp.
Personality: second lieutenant; courteous meteorologist with a fierce dislike of lazy assumptions.
Primary car: V08. Driving tendency: wet-line reader. Strength: anticipates traction changes from surface and camber. Weakness: dry-road maximum pace is less intimidating.
Sample introduction: “The rain is not against you. It is information.”

## Crew 3: Reservoir Section — Kurogawa
Infrastructure workers, researchers and local competitors; precision without a single shared personality.

### R17 — Shun Minato (25, he/him)
Look: slim; short curls; utility jumpsuit tied at waist; orange tape on one glove.
Personality: dam electrician, cheerfully impatient until precision is required.
Primary car: V09. Driving tendency: power conserver. Strength: smooth long-arc torque use. Weakness: slow direction changes in the heavy chassis.
Sample introduction: “Make the current work for you.”

### R18 — Kaede Ueno (32, she/her)
Look: tall strong build; long braid; cropped technical coat; silver mechanical pencil.
Personality: civil drafter, observant and gently sarcastic about extravagant racing lines.
Primary car: V08. Driving tendency: geometric apexer. Strength: precise apex placement. Weakness: rigid commitment makes variable-radius bends awkward.
Sample introduction: “You drew that line with your elbow.”

### R19 — Michi Kagawa (24, they/them)
Look: short rounded silhouette; fluffy hair; wide sleeves on a fitted waistcoat; water-level tag.
Personality: archivist who loves old mistakes because they make good warnings.
Primary car: V10. Driving tendency: recovery specialist. Strength: restores pace quickly after a slide. Weakness: occasionally accepts a risk that was not necessary.
Sample introduction: “I know this mistake. It has a good ending.”

### R20 — Ryo Senba (37, he/him)
Look: athletic; topknot; open-collar workshop tunic; narrow rectangular spectacles.
Personality: quiet machinist who explains little and demonstrates a great deal.
Primary car: V11. Driving tendency: rotation specialist. Strength: fast deliberate MR rotation. Weakness: needs clean braking on downhill entries.
Sample introduction: “Watch the release, not the turn.”

### R21 — Natsumi Hori (28, she/her)
Look: compact; jagged bob; thick short jacket over tapered trousers; oversized headphones at neck.
Personality: radio editor who speaks in concise observations rather than slogans.
Primary car: V09. Driving tendency: pressure tester. Strength: stays close without contact and waits for an error. Weakness: can overinvest in following instead of passing.
Sample introduction: “You looked in the mirror twice there.”

### R22 — Kenta Omi (43, he/him)
Look: large build; receding hair and beard; high-collar vest; fabric lunch bag.
Personality: maintenance supervisor with a long memory and infectious laugh.
Primary car: V08. Driving tendency: exit traction specialist. Strength: excellent repeated hairpin exits. Weakness: braking distance can be exploited.
Sample introduction: “Good. Now do it when your hands are tired.”

### R23 — Sae Tsukada (34, she/her)
Look: long silhouette; straight waist-length hair in a low tie; tailored coveralls; thin red belt.
Personality: hydrologist, laconic until someone mistakes caution for fear.
Primary car: V10. Driving tendency: wide-entry specialist. Strength: carries speed through opening-radius arcs. Weakness: loses time when the radius unexpectedly tightens.
Sample introduction: “The wide line only works when it comes back.”

### R24 — Jun Saegusa (36, they/them)
Look: slender; close shave; long white mechanic's coat with dark inner panels; small steel caliper.
Personality: third lieutenant; exacting builder who gradually admits that numbers need interpretation.
Primary car: V12. Driving tendency: geometric apexer. Strength: exceptional consistency and apex control. Weakness: predictable committed line gives a patient rival an opening.
Sample introduction: “Measure twice. Brake once.”

## Crew 4: Breakwater Union — Akebono
Coastal craftspeople, night workers and performers; bold silhouettes, varied relationships to the port.

### R25 — Hiroto Namioka (26, he/him)
Look: tall; loose swept hair; deck jacket with broad shoulder stripes; rope bracelet.
Personality: ferry mechanic, restless on land, hospitable in the garage.
Primary car: V13. Driving tendency: high-speed arc reader. Strength: stable sweeping-corner pace. Weakness: impatient through slow compound corners.
Sample introduction: “The long way can still be the quick way.”

### R26 — Maya Kuroda (30, she/her)
Look: strong compact build; sharp bob; short leatherless racing jacket; triangular earrings.
Personality: poster artist who enjoys making an overtake look inevitable.
Primary car: V14. Driving tendency: inside-line defender. Strength: assertive legal positioning. Weakness: stubborn defence can consume front grip.
Sample introduction: “I left you a space. Not the one you chose.”

### R27 — Renji Sudo (40, he/him)
Look: broad; greying braided hair; long fisherman-style work coat; knitted wrist cuffs.
Personality: a patient boat repairer with an unexpectedly explosive sense of humour.
Primary car: V15. Driving tendency: gear optimizer. Strength: uses torque and ratios efficiently. Weakness: boost can punish a rushed exit.
Sample introduction: “That gear was a choice. Make it a good one.”

### R28 — Koharu Akiyama (23, she/her)
Look: very short; cropped curls; oversized varsity-style windbreaker; shell-shaped clip.
Personality: a dancer who treats the road as rhythm but hates being called naturally gifted.
Primary car: V13. Driving tendency: rhythm linker. Strength: smooth linked drifts. Weakness: hesitates on isolated late-braking hairpins.
Sample introduction: “It took practice to make that look easy.”

### R29 — Seiji Tanabe (48, he/him)
Look: thin and stooped; long untidy hair; layered rain jacket; folded harbour timetable.
Personality: night dispatcher, soft-spoken and impossible to hurry in conversation.
Primary car: V14. Driving tendency: margin keeper. Strength: finishes clean through changing surface. Weakness: gives away top-end aggression.
Sample introduction: “There is still time to do the corner properly.”

### R30 — Akane Furuya (29, she/her)
Look: tall athletic; side braid; sleeveless quilted coat over bright undershirt; square medallion.
Personality: fitness instructor who is direct, supportive and intensely competitive.
Primary car: V15. Driving tendency: late-brake anchor. Strength: powerful controlled braking attacks. Weakness: can be baited into a poor exit gear.
Sample introduction: “You can be brave after you are accurate.”

### R31 — Yori Kamei (27, they/them)
Look: medium broad build; shaved head; long open utility shirt; woven pouch.
Personality: ceramic sign painter who prefers clever positioning to showing force.
Primary car: V13. Driving tendency: pressure tester. Strength: patient no-contact passes after pressure. Weakness: rarely capitalizes on a short straight alone.
Sample introduction: “I am not rushing you. The finish is.”

### R32 — Mako Hoshino (38, she/her)
Look: tall; windswept short hair; dramatic cropped cape-like race jacket; copper ear cuff.
Personality: fourth lieutenant; charismatic former champion whose bravado protects a careful strategist.
Primary car: V16. Driving tendency: rotation specialist. Strength: bold but controlled combined-corner rotation. Weakness: can sacrifice a straight by overrotating for style.
Sample introduction: “Show me the line you would choose without an audience.”

## Crew 5: Datum Works — Hoshimi
A technically rigorous racing shop with internal disagreements about what a perfect result means.

### R33 — Tetsu Arimura (33, he/him)
Look: square build; close buzz cut; split-tone workshop suit; magnetic parts band.
Personality: test engineer who believes a repeated result is more interesting than a lucky one.
Primary car: V17. Driving tendency: geometric apexer. Strength: repeatable technical-sector times. Weakness: predictable decisions under pressure.
Sample introduction: “One good lap is a story. Three are evidence.”

### R34 — Chika Sakurai (25, she/her)
Look: slim; long asymmetric ponytail; high-waisted overalls; bright measuring-tape sash.
Personality: upholsterer with a sharp eye for a car's balance and a sharper wit.
Primary car: V18. Driving tendency: exit traction specialist. Strength: clean throttle application. Weakness: gives away early entry speed.
Sample introduction: “The seat is comfortable. Do not get comfortable.”

### R35 — Naozumi Fuda (45, he/him)
Look: long-limbed; full beard; old racing waistcoat; repaired driving gloves.
Personality: engine builder, proud of naturally aspirated response and grudgingly fair about turbo cars.
Primary car: V16. Driving tendency: power conserver. Strength: efficient throttle and minimal drag. Weakness: less aggressive in crowded opening sectors.
Sample introduction: “Power is useful. Wasting it is expensive.”

### R36 — Lina Hayase (28, she/her)
Look: short athletic; tight curls; sculpted padded jacket; transparent tool pouch.
Personality: a bilingual workshop coordinator who translates everyone's excuses into useful tasks.
Primary car: V17. Driving tendency: recovery specialist. Strength: quick correction without a second overcorrection. Weakness: sometimes relies on recovery instead of avoiding the first mistake.
Sample introduction: “That is recoverable. Your pride may take longer.”

### R37 — Osamu Kitani (52, he/him)
Look: large relaxed build; side-parted grey hair; long shop apron over a knit vest; magnifier.
Personality: veteran tuner with elaborate tea preferences and very simple racing advice.
Primary car: V18. Driving tendency: gear optimizer. Strength: perfect ratios for known terrain. Weakness: less flexible when forced off his planned rhythm.
Sample introduction: “The engine is not tired. You chose the wrong ratio.”

### R38 — Tsubasa Muraoka (24, they/them)
Look: tall narrow silhouette; spiked fringe; cropped reflective jacket; fingerless cuff on one arm.
Personality: industrial designer who loves experiments and keeps their failed prototypes.
Primary car: V16. Driving tendency: wide-entry specialist. Strength: creative legal alternative arcs. Weakness: overexperiments on a closing-radius corner.
Sample introduction: “The usual line was getting lonely.”

### R39 — Ayame Sugiura (35, she/her)
Look: medium build; short blunt fringe; tailored long vest; dark oval glasses.
Personality: quality inspector who notices an inconsistency before she notices a compliment.
Primary car: V17. Driving tendency: pressure tester. Strength: error-inducing proximity without dirty contact. Weakness: can be outpaced by someone who ignores the pressure.
Sample introduction: “You were quicker before you started watching me.”

### R40 — Reina Kurogane (34, she/her)
Look: tall poised silhouette; sleek chin-length black hair; sharply tailored white-and-graphite race coat; single red seam.
Personality: Normal final boss, The Surveyor; believes solitary precision is the only reliable form of trust.
Primary car: V18. Driving tendency: geometric apexer. Strength: near-perfect linked braking and repeatable legal pace. Weakness: commits too strongly to an ideal line when a different rhythm is needed.
Sample introduction: “A convoy still reaches the corner one driver at a time.”

## Crew 6: Zero Frequency — Tsukishiro
Independent highland veterans and newer observers linked by the old dawn-run legend, not a uniform team.

### R41 — Fuyu Tachibana (22, she/her)
Look: small; thick bob under a fleece cap; long insulated vest; mountain-flower patch.
Personality: observatory assistant whose nervous chatter disappears when driving.
Primary car: V11. Driving tendency: margin keeper. Strength: clear judgement on narrow highland roads. Weakness: overcautious through broad lowland corners.
Sample introduction: “I can see the exit. That is enough.”

### R42 — Kazuma Yorita (47, he/him)
Look: broad stooped build; tousled grey curls; cable-knit jacket; old radio receiver.
Personality: radio repairer who heard the legendary run and refuses to embellish it.
Primary car: V12. Driving tendency: surface reader. Strength: sensitive to camber and cold-road transition. Weakness: does not enjoy aggressive defending.
Sample introduction: “The quiet part is where you hear the car.”

### R43 — Neri Takase (31, they/them)
Look: long angular silhouette; braided crown; asymmetrical long windbreaker; constellation brooch.
Personality: hiking guide who laughs at heroic stories but quietly makes sure everyone returns.
Primary car: V13. Driving tendency: high-speed arc reader. Strength: flowing ridge-line speed. Weakness: needs discipline at blind-seeming but marked braking zones.
Sample introduction: “Leave yourself a road home.”

### R44 — Satoshi Mibe (39, he/him)
Look: compact; shaved scalp and short beard; quilted jumpsuit; cloth-wrapped thermos.
Personality: snow-shed technician with blunt manners and an excellent memory for apprentices.
Primary car: V14. Driving tendency: late-brake anchor. Strength: controlled hard braking on descents. Weakness: loses flow across a long gentle sector.
Sample introduction: “The marker is there for a reason. Then there is your reason.”

### R45 — Minae Otsuka (44, she/her)
Look: broad athletic; high knot; short heavy coat over narrow trousers; mountain rescue whistle.
Personality: former endurance mechanic who values consistency and checks other cars before her own.
Primary car: V15. Driving tendency: power conserver. Strength: low-waste lines over a long event. Weakness: rarely uses a surprising short-term attack.
Sample introduction: “Keep some of the car for the last hill.”

### R46 — Gaku Nishio (57, he/him)
Look: very tall thin; shoulder-length silver hair; old flight-style coat; round spectacles on cord.
Personality: retired train driver whose jokes arrive late and whose braking never does.
Primary car: V09. Driving tendency: brake-release student. Strength: exceptionally smooth long braking releases. Weakness: modest power requires perfect exits.
Sample introduction: “On time is a kind of fast.”

### R47 — Yuna Senzaki (28, she/her)
Look: short muscular; blunt cropped hair; loose mechanic's shirt; two embroidered glove cuffs.
Personality: a restoration specialist rebuilding the old dawn car with respect rather than nostalgia.
Primary car: V10. Driving tendency: rhythm linker. Strength: rapid but composed transitions. Weakness: tries too hard when the legend is watching.
Sample introduction: “Old does not mean finished.”

### R48 — Shiori Kuze (54, she/her)
Look: compact upright silhouette; silver braid; worn indigo driving coat; repaired cream scarf tucked safely away.
Personality: Hard final boss, Zero Signal; the legendary dawn racer, patient and unshowy, testing observation rather than bravado.
Primary car: V04. Driving tendency: momentum reader. Strength: extraordinary legal speed preservation, minimal steering and measured late release. Weakness: lower power makes any actual loss of flow costly; no magical recovery.
Sample introduction: “You do not have to hurry every part of the road.”

# APPENDIX E — THE 75 CHALLENGES AND 75 COSMETIC REWARDS

Every row is required. All five families contain five Bronze, five Silver and five Gold challenges: 25 of each overall. Bronze =40 RP /3,000 credits; Silver =80 RP /8,000; Gold =120 RP /15,000. Each has one distinct COS-CHxx reward. No performance parts, random drops, purchasable rank boosts, arbitrary likes, or another human's required cooperation.

Predicates mentioning a reference speed/time/score must bind a named versioned challenge configuration generated from a real legal demonstration run. Show the final numeric target in the game. Do not leave “Silver target” as a blank comparison. A player may pin any accessible challenge; all qualifying actions are still tracked without pinning. Driving challenges require a legal event/lesson finish unless the predicate explicitly defines a non-race completion. Default compatibility: server-tracked modes, fixed or capped legal builds, allowed assists stated in the challenge, raw unboosted metrics, no debug flags, no DQ. Ordinary losing finishes can still complete a challenge whose predicate does not require winning.

The fixed challenge vehicle, parts, and necessary configuration are loaned free. For challenges requiring normal ownership-state actions, use starter/default cosmetic inventory. Record exactly which objects/items unlock; avatar icons are original art or properly generated portraits, not a generic icon renamed fifteen times. Clothing/accessories must render on the driver's avatar. Paint rewards require their actual material preset; card effects respect reduced motion.


## Precision

### CH01 — First Clean Signal [Bronze]
Completion: Finish C01 with no meaningful wall impacts and no reset.
Reward: COS-CH01 — Tea-Line Pinstripe (decal); 40 RP; 3,000 credits, once.

### CH02 — One Deliberate Stop [Bronze]
Completion: On T00 braking lane, stop from 100 km/h inside the marked 8 m target box, three distinct starts in one lesson; server verifies speed before each start.
Reward: COS-CH02 — Brake Marker Bars (decal); 40 RP; 3,000 credits, once.

### CH03 — Apex Appointment [Bronze]
Completion: Finish C03 crossing all three designated apex gates without a wall incident.
Reward: COS-CH03 — Orchard Apex Arcs (decal); 40 RP; 3,000 credits, once.

### CH04 — The Exit Matters [Bronze]
Completion: Finish C02 with three specified uphill exit gates crossed above their published Bronze reference speeds.
Reward: COS-CH04 — Rising Tea Arrow (decal); 40 RP; 3,000 credits, once.

### CH05 — No Recovery Needed [Bronze]
Completion: Finish C04 without reset, wrong-way warning, or leaving the legal corridor.
Reward: COS-CH05 — Bellmaker Thread (decal); 40 RP; 3,000 credits, once.

### CH06 — Cedar Accuracy [Silver]
Completion: Finish C05 touching all six alternating precision gates without touching a guardrail.
Reward: COS-CH06 — Cedar Chevron (decal); 80 RP; 8,000 credits, once.

### CH07 — One Release [Silver]
Completion: In the fixed T00 loaner braking lesson, pass the trail-brake trace envelope through the test bend; no handbrake, valid lesson finish.
Reward: COS-CH07 — Release Trace (decal); 80 RP; 8,000 credits, once.

### CH08 — Wet Window [Silver]
Completion: Finish C08 with the two judged wet braking zones completed within the displayed speed/position envelopes and no wall incident.
Reward: COS-CH08 — Rain Window (decal); 80 RP; 8,000 credits, once.

### CH09 — Bridge Margin [Silver]
Completion: Finish C13 crossing both viaduct lane gates while staying at least the published 0.5 m safety margin from barriers in those zones.
Reward: COS-CH09 — Viaduct Twin Lines (decal); 80 RP; 8,000 credits, once.

### CH10 — Equal Splits [Silver]
Completion: Complete C11's two laps with lap-time difference <=2.0 seconds, both legal and within Silver reference time.
Reward: COS-CH10 — Reservoir Twinmark (decal); 80 RP; 8,000 credits, once.

### CH11 — Blackwater Needle [Gold]
Completion: In the supplied class-capped C12 challenge loaner, beat its Gold reference with no reset and at most one meaningful wall impact.
Reward: COS-CH11 — Blackwater Needle (decal); 120 RP; 15,000 credits, once.

### CH12 — The Last Ten Metres [Gold]
Completion: Finish C20, passing all four late-braking target gates within their published speed windows; no collision/reset in those sectors.
Reward: COS-CH12 — Quarry Last Marker (decal); 120 RP; 15,000 credits, once.

### CH13 — Narrow Without Fear [Gold]
Completion: Beat the fixed C21 Gold ghost while keeping all tyres in the paved corridor; a normal shoulder touch invalidates this challenge, not the entire race.
Reward: COS-CH13 — Moonridge Edgework (decal); 120 RP; 15,000 credits, once.

### CH14 — Unbroken Datum [Gold]
Completion: Finish a C17/C18/C19 three-event challenge cup with zero meaningful wall impacts across all legs; individual stage resets cannot carry progress.
Reward: COS-CH14 — Datum Continuum (decal); 120 RP; 15,000 credits, once.

### CH15 — Dawn Precision [Gold]
Completion: Beat C25's fixed challenge reference with all final-sector apex gates and no reset.
Reward: COS-CH15 — Amanagi Dawnseal (decal); 120 RP; 15,000 credits, once.

## Drift

### CH16 — First Arc [Bronze]
Completion: Bank a single legal 8,000-point raw chain in T00's guided drift route and complete the lesson.
Reward: COS-CH16 — Warm Apricot Solid (paint); 40 RP; 3,000 credits, once.

### CH17 — Change of Direction [Bronze]
Completion: Link three distinct left-right judged corners on C03 without losing the unbanked chain, then finish.
Reward: COS-CH17 — Canal Jade Metallic (paint); 40 RP; 3,000 credits, once.

### CH18 — Settle and Go [Bronze]
Completion: On C04 bank two separate raw drift chains, each >=6,000; return below 8 degrees slip for 2 seconds between them; finish.
Reward: COS-CH18 — Bell Bronze Pearl (paint); 40 RP; 3,000 credits, once.

### CH19 — A Useful Angle [Bronze]
Completion: Hold a legal 20–35 degree drift for at least 3 seconds in the C05 demonstration zone and finish.
Reward: COS-CH19 — Cedar Mist Satin (paint); 40 RP; 3,000 credits, once.

### CH20 — No Doughnuts [Bronze]
Completion: Score 25,000 raw in C01 drift variant using forward judged zones with no repeated-progress scoring, then finish.
Reward: COS-CH20 — Tea Cream Gloss (paint); 40 RP; 3,000 credits, once.

### CH21 — Rain Thread [Silver]
Completion: Score 70,000 raw on C08 wet Drift Attack, with at least two banked chains and a legal finish.
Reward: COS-CH21 — Rainline Blue Pearl (paint); 80 RP; 8,000 credits, once.

### CH22 — Outer Clip Reader [Silver]
Completion: On C09 connect its three marked outer clip zones within one valid chain, without wall contact, then finish.
Reward: COS-CH22 — Spillway Graphite (paint); 80 RP; 8,000 credits, once.

### CH23 — Countersteer Conversation [Silver]
Completion: Complete T00 advanced countersteer drill with four alternating valid recoveries into the next arc and no spin/reset.
Reward: COS-CH23 — Signal Red Metallic (paint); 80 RP; 8,000 credits, once.

### CH24 — A Long Sentence [Silver]
Completion: Bank one >=60,000 raw chain on C15 without slowing below 45 km/h during the scoring portion; finish.
Reward: COS-CH24 — Tide Lilac Pearl (paint); 80 RP; 8,000 credits, once.

### CH25 — Both Hands Light [Silver]
Completion: In the fixed C16 drift loaner, score its Silver target without using handbrake after the start gate; finish.
Reward: COS-CH25 — Harbour Copper Satin (paint); 80 RP; 8,000 credits, once.

### CH26 — Needle in the Rain [Gold]
Completion: Beat the fixed wet C12 Gold drift reference and finish with no meaningful wall impact.
Reward: COS-CH26 — Blackwater Oil-Pearl (paint); 120 RP; 15,000 credits, once.

### CH27 — Six Connected Corners [Gold]
Completion: Link all six unique C19 judged transition zones in one forward chain and bank it at the final gate; finish.
Reward: COS-CH27 — Skyline Ice Metallic (paint); 120 RP; 15,000 credits, once.

### CH28 — One Car, Two Languages [Gold]
Completion: In a fixed C23 challenge build, beat both its published time and raw drift targets in the same legal run.
Reward: COS-CH28 — Transmitter Violet Pearl (paint); 120 RP; 15,000 credits, once.

### CH29 — No Wasted Motion [Gold]
Completion: Bank the C24 Gold raw target with total unbanked points lost <=5% of earned raw points, no reset, and a legal finish.
Reward: COS-CH29 — Four-Signal White Pearl (paint); 120 RP; 15,000 credits, once.

### CH30 — Dawn Ribbon [Gold]
Completion: Complete the dedicated C25 Gold drift route in its loaner, scoring the published raw target and banking a valid chain in every judged sector.
Reward: COS-CH30 — Zero-Signal Indigo Shift (paint); 120 RP; 15,000 credits, once.

## Racecraft

### CH31 — Clean Pass [Bronze]
Completion: Pass one live AI or human in an eligible six-slot race without contact within 2 seconds before/after the pass; retain position for 3 seconds and finish.
Reward: COS-CH31 — Tea-Hour Driving Gloves (driver_clothing); 40 RP; 3,000 credits, once.

### CH32 — Patient Mirror [Bronze]
Completion: Follow an eligible moving AI or human within the displayed 1–2 second gap for 8 seconds on C05 without contact, then finish.
Reward: COS-CH32 — Cedar Crew Cap (driver_clothing); 40 RP; 3,000 credits, once.

### CH33 — Start in Control [Bronze]
Completion: In an eligible race, complete the first sector without false-start input penalty, wall impact, or off-course excursion; finish.
Reward: COS-CH33 — Signal Track Jacket (driver_clothing); 40 RP; 3,000 credits, once.

### CH34 — Let the Corner End [Bronze]
Completion: Pass a rival on the exit zone of C02's marked hairpin, not its approach, retain the gain to the next gate, then finish.
Reward: COS-CH34 — Mizuhana Canvas Trousers (driver_clothing); 40 RP; 3,000 credits, once.

### CH35 — Graceful Return [Bronze]
Completion: Use one permitted reset in a race, then finish the remainder with no meaningful wall impact and all checkpoints legal.
Reward: COS-CH35 — Roadside Recovery Boots (driver_clothing); 40 RP; 3,000 credits, once.

### CH36 — Outside Invitation [Silver]
Completion: In a fixed C10 racecraft trial, pass its pacing rival using the outside lane gate and hold the gain through the next straight; finish.
Reward: COS-CH36 — Rainline Technical Jacket (driver_clothing); 80 RP; 8,000 credits, once.

### CH37 — Keep the Door Open [Silver]
Completion: Complete the scripted T00 merge lesson beside an AI pace car with both cars inside their legal lanes and no contact for the entire merge.
Reward: COS-CH37 — Reflector-Cuff Shirt (driver_clothing); 80 RP; 8,000 credits, once.

### CH38 — Three Different Rivals [Silver]
Completion: Win three eligible Freeplay races against three different authored lead-AI archetypes, with no race quit between these wins; losses do not erase completed archetypes.
Reward: COS-CH38 — Reservoir Pit Vest (driver_clothing); 80 RP; 8,000 credits, once.

### CH39 — No Panic Reply [Silver]
Completion: In the C14 pressure trial, maintain a valid Silver pace while its AI pressure car follows within 1 second for one sector; finish without wall contact in that sector.
Reward: COS-CH39 — Harbour Marshal Coat (driver_clothing); 80 RP; 8,000 credits, once.

### CH40 — Late, Not Dirty [Silver]
Completion: Perform the marked clean braking-zone overtake in C17's fixed challenge race, no contact and no checkpoint cut, then finish.
Reward: COS-CH40 — Quarry Mechanic Overalls (driver_clothing); 80 RP; 8,000 credits, once.

### CH41 — From Sixth [Gold]
Completion: Win a class-equalized C18 six-entrant challenge race from the last grid position, with no reset and no car-to-car contact. AI fills all missing human slots.
Reward: COS-CH41 — Datum Racing Suit (driver_clothing); 120 RP; 15,000 credits, once.

### CH42 — Four Roads Ahead [Gold]
Completion: Complete a fixed C06/C13/C21 three-event cup ahead of its designated benchmark in every leg; valid finish each time, no need for another human.
Reward: COS-CH42 — Highland Windbreaker (driver_clothing); 120 RP; 15,000 credits, once.

### CH43 — Leave No Opening [Gold]
Completion: Beat R32's C20 Gold practice reference while completing both marked defence/exit gates within the legal corridor; no blocking/contact requirement.
Reward: COS-CH43 — Red-Horizon Driver Coat (driver_clothing); 120 RP; 15,000 credits, once.

### CH44 — Read the Surveyor [Gold]
Completion: Clear the Normal S30 finale personally within the qualifying benchmark, not only the support envelope.
Reward: COS-CH44 — Surveyor-Line Suit (driver_clothing); 120 RP; 15,000 credits, once.

### CH45 — Answer the Quiet [Gold]
Completion: Clear the Hard S30 finale personally within Shiori's qualifying benchmark, not only the support envelope.
Reward: COS-CH45 — Zero-Frequency Driving Coat (driver_clothing); 120 RP; 15,000 credits, once.

## Workshop

### CH46 — Your First Setup [Bronze]
Completion: Save a legal personalized tune preset with a changed final drive and complete T00's acceleration lane with it; loaner tools are free.
Reward: COS-CH46 — Workshop Grid Background (card_customization); 40 RP; 3,000 credits, once.

### CH47 — Before and After [Bronze]
Completion: Use the garage comparison harness on stock and a loaned tyre package, inspect the measured braking difference, then finish its braking lesson.
Reward: COS-CH47 — Comparison Columns Frame (card_customization); 40 RP; 3,000 credits, once.

### CH48 — Sign Your Car [Bronze]
Completion: Apply and save a legal livery with one owned/free decal and two paint regions; verify it appears on the meet display car.
Reward: COS-CH48 — Signed Build Nameplate (card_customization); 40 RP; 3,000 credits, once.

### CH49 — A Place for Every Gear [Bronze]
Completion: Complete the guided gearing lesson at its target shift windows using a supplied car; no purchase required.
Reward: COS-CH49 — Ratio Ladder Motif (card_customization); 40 RP; 3,000 credits, once.

### CH50 — Change Without Losing [Bronze]
Completion: Create two visual presets, switch between them and restore the first exactly; confirm its saved server revision and meet preview.
Reward: COS-CH50 — Two-State Card Back (card_customization); 40 RP; 3,000 credits, once.

### CH51 — Rain Has a Setup [Silver]
Completion: Complete the fixed C08 challenge using the supplied wet tune, beating its Silver benchmark; ownership of parts not required.
Reward: COS-CH51 — Rain Chart Background (card_customization); 80 RP; 8,000 credits, once.

### CH52 — Weight Is Not Free [Silver]
Completion: Finish both T00 handling comparison routes in supplied light/heavy tune variants within their separate reference envelopes.
Reward: COS-CH52 — Mass Balance Frame (card_customization); 80 RP; 8,000 credits, once.

### CH53 — Differential Diagnosis [Silver]
Completion: Complete the guided C03 two-corner challenge in both supplied diff setups, with each passing its declared turn-in/exit criteria.
Reward: COS-CH53 — Differential Dial Emblem (card_customization); 80 RP; 8,000 credits, once.

### CH54 — One Index, Two Cars [Silver]
Completion: Beat Silver reference targets on C09 in two different equalized loaner drive layouts; same PI cap, separate fair target times.
Reward: COS-CH54 — Twin-Chassis Card Layout (card_customization); 80 RP; 8,000 credits, once.

### CH55 — No Power Purchase [Silver]
Completion: Beat the supplied stock V01 Silver reference on C04 without performance upgrades; cosmetics allowed.
Reward: COS-CH55 — Stock-Line Driver Title (card_customization); 80 RP; 8,000 credits, once.

### CH56 — A Tune for the Hill [Gold]
Completion: Using free challenge tuning parts within a locked PI budget, beat C23 Gold reference; server validates all installed parameters.
Reward: COS-CH56 — Hill-Dyno Animated Trace (card_customization); 120 RP; 15,000 credits, once.

### CH57 — Balanced, Not Maximum [Gold]
Completion: Beat C15's fixed Gold combined grip/exit target with its tune-budget loaner; neither front nor rear aero may be at the range maximum.
Reward: COS-CH57 — Balance Point Frame (card_customization); 120 RP; 15,000 credits, once.

### CH58 — Three Layouts, One Driver [Gold]
Completion: Complete Gold short reference trials in the supplied FWD, RWD and AWD cars on three named T00 configurations; no ownership requirement.
Reward: COS-CH58 — Three-Drive Emblem (card_customization); 120 RP; 15,000 credits, once.

### CH59 — Build Under Pressure [Gold]
Completion: Win the scripted C19 six-slot equal-PI AI challenge using any legal self-authored loaner tune within the provided parts budget.
Reward: COS-CH59 — Engineer's Sector Card (card_customization); 120 RP; 15,000 credits, once.

### CH60 — The Complete Notebook [Gold]
Completion: Complete the 10 named advanced tuning demonstrations and their driving checks; they cover distinct systems, not ten copies of one slider task.
Reward: COS-CH60 — Night-Signal Technical Passport (card_customization); 120 RP; 15,000 credits, once.

## Touring

### CH61 — First Parking Place [Bronze]
Completion: Enter Cedar Lantern Terrace, complete or skip the arrival presentation, leave the car and inspect your own parked vehicle.
Reward: COS-CH61 — Cedar Lantern Keychain (accessory_or_avatar); 40 RP; 3,000 credits, once.

### CH62 — Four Corners of the Terrace [Bronze]
Completion: Walk to the four marked meet viewpoints without leaving scene bounds; inspect each location placard.
Reward: COS-CH62 — Route-Pin Lapel Badge (accessory_or_avatar); 40 RP; 3,000 credits, once.

### CH63 — A Driver's Greeting [Bronze]
Completion: Use wave and bow at the tutorial meet host NPC, then inspect the emote help. No other human is required.
Reward: COS-CH63 — Woven Wrist Cuff (accessory_or_avatar); 40 RP; 3,000 credits, once.

### CH64 — A Picture With a Horizon [Bronze]
Completion: Take an in-game photo at the terrace overlook with the horizon marker and own car in frame; use the built-in composition check.
Reward: COS-CH64 — Cherry-Branch Avatar Emblem (accessory_or_avatar); 40 RP; 3,000 credits, once.

### CH65 — Bring It Home [Bronze]
Completion: Complete any eligible event, return to a meet and inspect the result slip at the timing board.
Reward: COS-CH65 — Timing-Slip Card Charm (accessory_or_avatar); 40 RP; 3,000 credits, once.

### CH66 — Six Places, Not Six Skies [Silver]
Completion: Finish one legal course in each of the six regular regions; variants do not create extra regions.
Reward: COS-CH66 — Six-Region Avatar Crest (accessory_or_avatar); 80 RP; 8,000 credits, once.

### CH67 — The Working Landscape [Silver]
Completion: Discover and inspect the three named non-race photo points in the terrace scene: tea kiosk, radio bench, maintenance gate.
Reward: COS-CH67 — Workshop Cloth Satchel (accessory_or_avatar); 80 RP; 8,000 credits, once.

### CH68 — Chasing Your Yesterday [Silver]
Completion: Record a valid C07 personal ghost, then beat it by at least 1 second in a compatible ruleset; a reset-created artificially slow ghost does not qualify.
Reward: COS-CH68 — Ghostline Avatar Icon (accessory_or_avatar); 80 RP; 8,000 credits, once.

### CH69 — A Quiet Cup [Silver]
Completion: Complete the noncompetitive C01/C05/C09 touring cup within its generous legal time limits, with no quit; photo stops occur only after finishes.
Reward: COS-CH69 — Road Atlas Accessory (accessory_or_avatar); 80 RP; 8,000 credits, once.

### CH70 — The Other Side of the Card [Silver]
Completion: Read the six crew introductions in the race diary after encountering each crew, then complete one legal race against any crew member.
Reward: COS-CH70 — Radio-Dial Avatar Icon (accessory_or_avatar); 80 RP; 8,000 credits, once.

### CH71 — Every Real Road [Gold]
Completion: Legally finish T00's loop and all C01–C25 courses in any eligible normal/practice event; track IDs, not variants.
Reward: COS-CH71 — 26-Road Crest Avatar (accessory_or_avatar); 120 RP; 15,000 credits, once.

### CH72 — Before the First Train [Gold]
Completion: Finish the fixed pre-dawn C21/C22/C23 touring challenge cup without a reset, meeting each generous Silver pace; one continuous cup session.
Reward: COS-CH72 — Highland Travel Scarf (accessory_or_avatar); 120 RP; 15,000 credits, once.

### CH73 — Twelve Different Voices [Gold]
Completion: Complete legal races against 12 distinct rival driving archetype demonstrations in Freeplay; use named loaner trials for any rare style, not random luck.
Reward: COS-CH73 — Twelve-Voice Avatar Mosaic (accessory_or_avatar); 120 RP; 15,000 credits, once.

### CH74 — The Radio Remembered [Gold]
Completion: Collect the six authored radio/timing-slip story records through Normal progression, then win the C24 post-story reference trial.
Reward: COS-CH74 — Old-Frequency Receiver Accessory (accessory_or_avatar); 120 RP; 15,000 credits, once.

### CH75 — After the Last Signal [Gold]
Completion: Complete Hard S30, return to the terrace alone or with friends, and finish the final short story epilogue with Shiori at the radio bench.
Reward: COS-CH75 — Dawn Horizon Avatar (accessory_or_avatar); 120 RP; 15,000 credits, once.

# APPENDIX F — CEDAR LANTERN TERRACE SCENE PLAN

Build a 150×110 metre plateau with a gently graded non-slip central apron, on the edge of a wooded overlook. The camera must see a coherent distant mountain/valley horizon instead of an empty skybox.

West: six angled display bays beneath widely spaced cherry trees, with roots/curbs kept outside walking paths. East: six more bays along a low timber fence and a repaired stone retaining wall. Leave at least 3 metres clear pedestrian circulation behind cars and a 6-metre central pedestrian aisle. Bay layouts and walking space matter more than decorative density.

North: a tea kiosk with a shuttered service counter, benches, lantern strings and a covered awning. Adjacent timing board displays the current convoy proposal and personal recent results without publishing anybody's wallet. A radio bench hosts the tutorial NPC and post-story legend scenes. NPC dialogue interactions stay private to the interacting player; they do not freeze the room.

South: the non-colliding arrival spline passes through a landscaped entry lane to allocated bays. Visually separate arrival movement from the pedestrian centre. A maintenance gate and planted berm block the edge; behind them, scenic road/forest is not traversable.

Middle: a shallow dry garden island with stone edging, seasonal plants and a few scattered petals. The flat apron remains comfortably navigable, not a maze of props. A photo marker frames cars against the mountain without forcing strangers into the photograph. Photo mode hides nameplates locally on request.

At perimeter: hedges, fences, kiosk walls and believable retaining edges backed by collision. Collision is continuous, corners do not leak, and decorative trees are not the sole barrier. The rescue-to-car action finds a validated free spawn. No jump ability is required, avoiding needless fence exploits.

Use original wood joinery, low stone walls, appropriate roof/lantern detailing and local plant variation. Do not cover every object with random Japanese glyphs or mix motifs merely because they read as 'Asian.' Japanese signage, when used, must be correct and translated where functional. Cinematic dawn/dusk lighting and a quiet soundscape contrast with racing. Meet weather may be fixed for the launch scene; don't build a global dynamic weather MMO.

# APPENDIX G — CONTENT COUNTS AND VALIDATOR CONTRACT

A machine-readable companion catalogue is supplied with this brief. It is an authoring seed, not compiled game content. Import/adapt it into the game's own typed schemas while preserving stable IDs and required semantics. If a genuine wording conflict is found between this catalogue and the core rules, fix the authoring ambiguity explicitly before implementation; do not silently choose the easier interpretation.

Validator assertions: 26 base course IDs; 24 regular course IDs C01–C24; 30 ordered stages; 24 regular, 4 lieutenant, 1 penultimate, 1 finale; 18 unique car models; 48 unique rivals; 75 challenges/rewards; 25 Bronze,25 Silver,25 Gold; 6,000 challenge RP; 3,000 Normal RP; 6,000 Hard RP; 15,000 maximum. Fixed finals R40/R48. Six entrant cap, 12 meet-person cap, six convoy-person cap. Unique content references, legal car caps, legal support pools and all required meshes/scenes/dialogue/reward assets.

Report geometry overlap, absent textures/materials, identical model meshes masquerading as new cars, near-identical rival appearances, missing playable routes and missing challenge target measurements. These checks flag possible problems; human review still decides whether art/narrative differences are meaningful. No evidence file may say a real-person playtest occurred merely because a scripted agent drove the course.

# APPENDIX H — TECHNICAL RESEARCH AND VERSION CHECKS

Research baseline: 26 September 2026. The game's setting, course/rival/challenge catalogues, economy and behavioural rules are original design proposals, not facts asserted by these references. These primary sources support technical feasibility and setup constraints. Consult installed-version documentation before using an API. A reference URL is not permission to copy game art, logos, music, characters or third-party code without its required licence.

1. Unity release support: https://unity.com/releases/unity-6/support
   Unity lists 6.3 as its current LTS with support through December 2027. The master chooses it for a stable pinned baseline; supported Update releases also exist and are officially recommended for new/mid-cycle productions. Do not confuse the design choice with an official assertion that LTS is always best.

2. Unity Dedicated Server: https://docs.unity3d.com/6000.3/Documentation/Manual/dedicated-server.html
   Reference for optimized server-target builds and the appropriate platform build workflow. Pin the matching installed editor/modules and prove the produced executable runs.

3. Unity Netcode client anticipation: https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@3.0/manual/advanced-topics/client-anticipation.html
   Do not assume NGO provides a full ready-made prediction/reconciliation solution for an arcade car. The documented anticipation facility is not equivalent to complete rollback/replay prediction. The game's motor and reconciliation require deliberate implementation and tests.

4. Unity Transport WebSocket support: https://docs.unity3d.com/Packages/com.unity.transport@2.4/manual/websockets.html
   A Web target needs a compatible browser transport. Unity Transport's WebSocket payload is not automatically compatible with an arbitrary generic WebSocket application server. Confirm the actual native/Web server configuration and crossplay, rather than assuming a changed URL enables it.

5. Unity Web deployment/compression: https://docs.unity3d.com/6000.3/Documentation/Manual/webgl-deploying.html
   Hosting requires appropriate loader, MIME/compression handling and server capabilities; decompression fallback is an available route where matching headers cannot be configured. Test the actual delivered build.

6. GitHub Pages definition: https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages
   Pages publishes static website content. It is not the runtime for the authoritative simulation, account API or SQL database.

7. GitHub Pages limits: https://docs.github.com/en/pages/getting-started-with-github-pages/github-pages-limits
   Published-site size and bandwidth/build limits matter for a 26-course 3D client. Check the live policy instead of promising unlimited free hosting; a release download and a Pages site are different delivery mechanisms.

8. Supabase Auth: https://supabase.com/docs/guides/auth
   Use its actual identity/account APIs, not a fake local account screen. Authentication, database authorization and race-results validation are separate responsibilities.

9. Supabase JWTs: https://supabase.com/docs/guides/auth/jwts
   Verify signatures and claims with supported methods/maintained libraries; use the project's actual signing configuration. Merely parsing a token is not verification. Never embed private signing/service keys in the Unity client.

10. Supabase local development: https://supabase.com/docs/guides/local-development
    An actual local Auth/Postgres development environment needs the CLI and a compatible container runtime. Bind development services locally and protect credentials. Do not confuse captured test mail with hosted email delivery.

11. ASP.NET Core WebSockets: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0
    Use maintained server connection facilities for the control channel. The specific Unity client library/protocol must be verified; this control channel is not a substitute for the gameplay transport.

12. Claude Code desktop execution: https://code.claude.com/docs/en/desktop
    Match the actual Local/Cloud/SSH environment and checkout. A Cloud worker does not acquire access to a local Unity editor from a Windows path in a prompt. Use an explicit tested tool connection.

13. Claude Code MCP configuration: https://code.claude.com/docs/en/mcp
    Follow the actual transport, scope and trust configuration. A local project MCP connection is not permission to expose editor automation publicly or bypass normal approvals.

14. MCP for Unity community project: https://github.com/CoplayDev/unity-mcp
    Release baseline: https://github.com/CoplayDev/unity-mcp/releases/tag/v10.0.0
    Community-maintained, MIT-licensed editor bridge; not affiliated with Unity Technologies. Pin the selected version and assess its tool permissions rather than following a moving beta branch unknowingly.

15. MCP for Unity setup: https://coplaydev.github.io/unity-mcp/getting-started/install
    Use the Unity package, Python/uv prerequisites and the Claude Code configurator; verify the connection. Read the actual local HTTP endpoint from the installed tool rather than assuming a port can never differ.

16. MCP for Unity tools: https://coplaydev.github.io/unity-mcp/reference/tools
    Discover available scene/script/test/build operations. Optional external asset-generation groups can require provider keys/costs and are not approved by installing the bridge.

17. Unity editor command line: https://docs.unity3d.com/6000.0/Documentation/Manual/EditorCommandLineArguments.html
    Reference for real batch execution and named static editor methods. Inspect the correct installed-version flags and logs. Do not launch two writers against a locked project or invent methods that have never been implemented.

18. Unity Test Framework CLI: https://docs.unity3d.com/Packages/com.unity.test-framework@1.4/manual/reference-command-line.html
    Use the test runner's supported invocation and completion behavior. Verify nonempty test results, not merely process exit. A test run and an interactive visual inspection prove different things.

19. Experiential racing reference, not an asset source: https://initiald.sega.jp/inidac/
    Inspect official material for clarity, speed, rival framing and arcade presentation. Record what was observed and build an original interface, fiction and soundscape. The master does not claim that a specific copied feature is necessary to the experience.

End of master specification. This document and its JSON companion define the assignment; they are not evidence of an implemented or tested racing game.
