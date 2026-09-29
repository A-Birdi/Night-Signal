# Gate 4 — integration audit (spec §19)

Status of each Gate 4 item against **executed** evidence (VALIDATION entries, tests), as of 2026-09-29. "Built player" means
real player builds driven by automation (the validator autopilot, AutoClients or button tours) on this PC over loopback —
not people, not a LAN or the internet. Missing human, LAN and WAN checks stay open (see HANDOFF "Human checks").

| Gate 4 item | Status | Evidence | Open |
|---|---|---|---|
| Campaign access rules across mixed progress groups | tests + built player | Core `CampaignProgress.Evaluate` (EditMode `ProgressionTests`), Services `ConvoyDirectoryTests` (a convoy selects only what its members' progress allows; a newer member withdraws an out-of-reach stage); **V-117** two built players at S13 and S07: the out-of-reach S10 withdrawn with its reason, S08–S13 listed locked, S08 refused by the server, S07 raced — the guest's first clear moved the shared frontier to S08, the host got no duplicate first clear | Hard across mixed progress in built players (no account has the Normal finale) |
| All party sizes 1–6 | built player | 1 (`ui-tour-online`, V-104, V-112); 2 (V-022, V-037, V-109); **3 and 5 (V-114)**; 4 and 6 (V-081, V-082); six client processes (V-025, V-035 — a full 12-car grid, V-052) | — |
| All modes | built player, partly | Campaign Normal/Hard (V-064–V-066 runs, V-100), Freeplay sprint/circuit (V-036), Time Attack and Team Trials (V-044, V-081, V-104), Drift Attack (V-079), the meet (V-073, V-084), Driving School (V-111), the Custom Cup offline (V-115) and online — three legs on one long-lived game server (V-116) | — |
| Car legality / loaners | tests, partly built | class caps, legal rosters and final-rival rejection (V-018, Core and Services roster tests); loaner runs in Local progression (Core tests); the tutorial's V01 loaner (V-111) | challenge loaners (fixed trial builds) do not exist yet |
| Class / performance balance | measured | benchmark certification — targets from the slowest starter, rival paces 0–1 % above (V-065, V-066, V-108); crew telemetry (V-106, V-111) | a human feel review of rival pace |
| Currency / RP / first-clear ledger | tests + built | ledger idempotency over 100 retries, wallet clamp (V-013); settlement recomputed with Core, first clears once, challenges once (Services `LedgerTests`, `SettlementComputeTests`, `CourseLedgerTests`; V-018, V-065, V-113) | — |
| Database restart | tests + service restarts | receipts, balances and progress re-read after a control-plane restart (Services `EndToEndTests`, restarted host); the long-lived service restarted on each server/content change and read back (V-107–V-113) | PostgreSQL never run (no local server) |
| Crashes during transactions | tests | **V-114** `SettlementCrashTests`: a settlement failing after money, challenge and cosmetic were written leaves nothing; the retry settles once; the replay pays nothing. Purchases idempotent (V-013, V-018, V-037); a race whose server died is aborted by the watchdog (`MatchWatchdogTests`) | a process kill mid-settlement on PostgreSQL |
| Invalid / tampered requests | tests + built | JWT rejection cases (V-013); ticket signatures, replay and foreign tickets (V-014, `CoreTicketParityTests`); results/ghost signatures, forged money, wrong placement 422 (`EndToEndTests`, V-104, V-107); stale or invalid convoy, garage and meet commands refused (`ConvoyDirectoryTests`, `GarageServiceTests`, `MeetTouringTests`) | — |
| Meet independence; race launch from the meet | built player | the meet runs apart from convoys; a race started from the meet returns to it (V-073–V-078, V-084, V-097) | 4–12 human visitors (only automation) |
| Subtitle / font / localization bounds | partial | label overflow and the missing ellipsis glyph found and fixed (V-020); readability at 720p/ultrawide with large text and HUD, line clipping fixed (V-053) | no localisation beyond English; no bound test of every string |
| Controller focus | partial | every screen declares a default focus (V-020); remappable controls (V-050); walking controls (V-076); the Canvas pen on a pad (V-083) | a full pad-only walkthrough |
| Transitions | built player | tours move through every screen and scene change (V-073, V-099–V-112) | — |
| Frame / memory budgets | partial | camera timing at 30/60/120 fps (V-052); soak memory — generated assets released, per-frame native growth fixed (V-059); car levels of detail (V-086) | the §14 performance profile per hardware tier, character LOD tiers |

## Gate 5 items that are known now

Blocked (need the owner or the outside world): an internet test with independent people and devices; a LAN test (needs
authorisation, Addendum 04); PostgreSQL/Supabase (no local stack); the Linux dedicated-server module (not installed).
Not yet done: all 75 challenges proven achievable (37 have predicates), course-by-course replay coverage, a fresh-checkout
build on a clean machine.
