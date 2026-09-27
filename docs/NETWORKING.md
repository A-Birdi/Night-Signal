# Night Signal — networking

> This file currently documents the **control plane** protocols (implemented in `Services/`, protocol version 1).
> Gameplay transport (Netcode for GameObjects / Unity Transport, prediction, snapshots — spec §18) is documented
> by the Unity side. All JSON is UTF-8, camelCase, and times are ISO-8601 UTC unless stated otherwise.

## 1. Authentication

| Caller | Credential | Where |
|---|---|---|
| Player (REST and WebSocket upgrade) | `Authorization: Bearer <access token>` | Supabase Auth in production; DevAuth locally |
| Game server | `X-NightSignal-Server-Key: <server key>` | configured per server (control plane stores only the SHA-256) |
| Game server results | server key **and** `X-NightSignal-Signature` (HMAC, §6) | per-match secret from the assignment |

Access tokens are verified (signature via JWKS, `iss`, `aud = "authenticated"`, `exp`/`nbf` with 30 s skew,
`sub` present and 8–64 of `[A-Za-z0-9-]`). Accepted algorithms: ES256, RS256 (Supabase projects must use
asymmetric JWT signing keys). `alg: none`, HS256 and unknown keys are rejected.

**DevAuth** (Development + loopback only) mimics Supabase's token shape:

- `POST /dev/auth/token` `{"email","password"}` → `{"access_token","token_type":"bearer","expires_in":900,"expires_at","refresh_token","user":{"id","email"}}`; wrong credentials → `400 {"error":"invalid_grant"}`.
- `POST /dev/auth/refresh` `{"refresh_token"}` → new pair; refresh tokens are single-use (rotation).
- `GET /dev/auth/.well-known/jwks.json` → public ES256 JWKS. Issuer: `urn:night-signal:devauth`.

## 2. REST API (player)

| Method, path | Body | Result |
|---|---|---|
| `GET /healthz` | – | `{"status":"ok","contentHash"}` (no auth) |
| `GET /v1/me` | – | `{accountId, card{displayName,revision}|null, wallet{balance,cap}, starterCarId, ownedCars[{carId,source}], campaign{normalCleared[30], hardCleared[30], normalFrontier, hardFrontier, hardUnlocked}, challengesCompleted[], cosmeticsOwned[], rank{rankPoints,index,name,threshold,next,nextThreshold}}` — RP/rank recomputed with Core `RankPoints` |
| `POST /v1/me/card` | `{"displayName", "revision"?}` | `{displayName, revision}`; 400 `invalid_display_name`; 409 `revision_conflict` (`revision` = expected current, `0` = none yet) |
| `POST /v1/me/starter` | `{"carId":"V01"|"V02"|"V03"}` | `{carId, balance, credited, replayed}`; once per account (+12,000 credits); other car later → 409 `starter_already_claimed` |
| `POST /v1/me/purchases` | `{"idempotencyKey","itemKind":"car","itemId","expectedPrice"?}` | `{itemId,itemName,price,balance,replayed}`; 400 `invalid_price`/`invalid_idempotency_key`/`unknown_item`; 409 `price_changed`/`insufficient_funds`/`already_owned`/`idempotency_conflict`. The catalogue price is charged; `expectedPrice` only confirms what the UI showed. |
| `GET /v1/matches/{matchId}/receipt` | – | own itemized receipt (below); `202 {"status":"pending"}`; `{"status":"aborted"}`; 404 if not an entrant |

Display names: NFC-normalized, spaces collapsed, 3–20 user-perceived characters (extended grapheme clusters),
≤120 UTF-8 bytes, no `< > { } \`, no control/format (except ZWJ)/private-use/bidi characters. Not unique.

## 3. Control channel — `GET /v1/control` (WebSocket)

Upgrade with `Authorization: Bearer <token>` and query `build=<client build>&protocol=1&content=<ContentHash>`
(optional `takeover=1`). Clients without all three version parameters can use the channel but cannot start events.

**Client → server:** `{"type": string, "requestId": string(1–64), "payload": object}`.
**Server → client:** `{"type": string, "revision": number, "payload": object}` — `revision` is the convoy's
monotonic revision for convoy messages (discard anything older than what you have), otherwise 0.

Every request gets `{"type":"reply","payload":{"requestId","ok","result"?,"error"?:{"code","message","retryAfterMs"?}}}`.
A **retried requestId** (same account and type, within 10 minutes, even after reconnecting) returns the original
reply without re-executing. Max message 16 KiB.

Close codes: `4400 unsupported_protocol`, `4401 token_expired` (send `session.reauth` before `tokenExpiresAt`),
`4409 active_elsewhere` (one control session per account; reconnect with `takeover=1` to move it — the older
client gets `session.superseded` then `4410`), `1009` message too big.

### Requests

| type | payload | who | notes |
|---|---|---|---|
| `ping` | – | any | `{pong, serverTime}` |
| `activity` | – | any | user interaction heartbeat (prevents Away during long menu operations) |
| `session.reauth` | `{accessToken}` | any | same account only; extends the socket's token expiry |
| `convoy.state` | – | any | `{revision, convoy}` resync |
| `convoy.list` | – | any | discoverable convoys with open slots |
| `convoy.create` | `{privacy:"invite-only"|"discoverable"}` | not in a convoy | `{convoyId}` |
| `convoy.join` | `{code}` or `{convoyId}` (discoverable) | not in a convoy | codes: rate-limited 10 attempts / 10 min / account |
| `convoy.leave` | – | member | leader leaving hands leadership on |
| `convoy.invite.create` | – | leader | `{code, expiresAt}`: 8 chars from `23456789ABCDEFGHJKMNPQRSTUVWXYZ`, 15 min, ≤5 active |
| `convoy.invite.revoke` | `{code}` | leader | |
| `presence.set` | `{presence: "InMenus"|"AtMeet"|"LoadingRace"|"InRace"|"Spectating"}` | member | `Reconnecting`/`Offline` are server-set |
| `loadout.set` | `{carId, performanceHash, cosmeticHash}` | member | must own the car; PI from the catalogue. New car/performance hash → `loadoutRevision+1` and **only this member** unreadies; cosmetic-only → `cosmeticRevision+1`, stays ready. Refused (`event_frozen`) while Allocating. |
| `destination.propose` | `{destination:"campaign-normal"|"campaign-hard"|"freeplay"}` | leader | ready request #1; Hard needs every member's Normal S30 (`mode_locked`) |
| `destination.consent` | `{proposalRevision, consent}` | member | must match the open revision |
| `destination.commit` | `{proposalRevision}` | leader | every **connected** member (leader included) consented and not Away |
| `event.propose` | campaign `{stageId, weather?, collision?}`; freeplay `{courseId, freeplayMode, aiCount, carCapPi?, weather?, collision?}` | leader | ready request #2; campaign stage must pass Core `CampaignProgress.Evaluate` for all members (`stage_locked`) |
| `event.ready` | `{proposalRevision, loadoutRevision, ready}` | member | both revisions must be current (`stale_revision`); car PI ≤ cap |
| `event.start` | `{proposalRevision}` | leader | atomic revalidation (below) → `{status:"allocating"}` |
| `match.ticket` | `{role:"racer"|"spectator"}` | member | fresh ticket for the current match (racer only for frozen entrants) |

Weather presets (`stage-default, dry-night, wet-night, dawn, blue-hour, fog`), Freeplay modes
(`sprint, circuit, drift-attack, time-trial`) and collision rules (`off, light-contact`) are provisional until
the content catalogue defines them.

**Rules and timers** (Core `Limits`): max 6 members, one convoy per account; the leader may request readiness
(destination or event proposal) at most once per 15 s (`rate_limited` + `retryAfterMs`); any event-setting change
creates a new proposal revision and unreadies everyone; any join/leave/removal bumps `rosterRevision`, re-issues
open proposals and unreadies everyone (a stage the new roster cannot access is withdrawn with a notice);
120 s without interaction while a proposal is open → `away: true` and unready; disconnect → slot reserved 60 s,
presence `Reconnecting`, readiness withdrawn; a leader unavailable for 15 s is replaced by the longest continuously
connected member (ties: lowest account ID); a returning former leader does not regain it.

**Start** re-checks, under one lock: leader, proposal and roster revision, every connected member ready against the
current proposal *and* loadout revision and not Away, car caps, identical client build/protocol/content, and stage
access with progress freshly read from the database. It then freezes the plan (Core `GridPlanner`: campaign fills to
six with the featured rival first, six humans → benchmark replay, not a seventh racer; Freeplay clamps AI with an
explanation) and moves to `Allocating`.

### Server messages

| type | payload |
|---|---|
| `hello` | `{accountId, serverTime, protocol, tokenExpiresAt}` |
| `convoy.state` | snapshot: `{convoyId, privacy, privacyLabel, phase, rosterRevision, leaderId, leaderLabel:"Convoy leader", maxMembers, members[{slot, accountId, displayName, isLeader, connection, presence, away, destinationConsent, eventReady, carId, loadoutRevision, cosmeticRevision, spectator}], destinationProposal{revision,destination,consents}, committedDestination, eventProposal{revision,rosterRevision,settings,ready}, campaignAccess{normal,hard: {allowed,maxSelectableStage,explanation,limitingPlayers}}, match{matchId,entrants}, readyRequestCooldownMs, notice}` |
| `ready.requested` | `{kind:"destination"|"event", proposalRevision, destination?}` |
| `match.allocated` | private per entrant: `{matchId, role, ticket, expiresAt, server{host,port}, build, protocol, contentHash}` |
| `match.aborted` | `{matchId, reason}` — server lost or no results in time; no results/rank/progression |
| `convoy.closed` | `{convoyId, reason:"left"|"reservation_expired"}` |
| `session.rejected` / `session.superseded` / `session.expired` | explanation before the close code |
| `error` | `{error:"malformed"}` for unparseable envelopes (the session continues) |

## 4. Match tickets

Compact JWS, header `{"alg":"ES256","kid":"<RFC 7638 thumbprint>","typ":"JWT"}`, signed by the control plane's
ticket key. Public key: `GET /v1/servers/ticket-jwks.json` (`{"keys":[{"kty":"EC","crv":"P-256","x","y","kid","alg":"ES256","use":"sig"}]}`).

| claim | type | meaning |
|---|---|---|
| `iss` | string | control plane ticket issuer (default `night-signal-control-plane`; returned by registration) |
| `aud` | string | `night-signal-gameserver` |
| `sub` | string | account ID (JWT `sub` of the player) |
| `convoy` | string | convoy ID |
| `match` | string | match ID from the assignment |
| `role` | string | `racer` or `spectator` |
| `build` | string | exact game build the match was allocated for |
| `protocol` | integer | protocol version (1) |
| `content` | string | Core `ContentCatalogue.ContentHash` (lowercase hex SHA-256) |
| `jti` | string | unique ticket ID (single use) |
| `iat`, `nbf`, `exp` | integer (Unix s) | `exp − iat ≤ 60` |

Game-server validation (`Services/TicketValidation/TicketValidator.cs`, netstandard2.1 + Newtonsoft.Json):
reject unless `alg` is exactly `ES256` and there is no `crit`; key chosen **only** by `kid` from the configured
JWKS (ignore `jku`/`jwk`/`x5u`); 64-byte IEEE-P1363 signature over `ASCII(header "." payload)`; `iss`, `aud`;
`exp`/`nbf` with ≤5 s skew; lifetime ≤ 60 s; all claims present; `match/build/protocol/content` equal to this
server's assignment and build; `role` known; then burn `jti` (replay cache until expiry). The validator returns a
`TicketFailure` code — log the code, never the ticket. Racer tickets admit only entrants listed in the assignment.

## 5. Game-server protocol

1. `POST /v1/servers/register` `{"endpoint":{"host","port"},"build","protocol":1,"contentHash","maxMatches"}` →
   `{serverId, heartbeatIntervalSeconds, assignmentsUrl, ticketIssuer, ticketAudience, ticketJwksUrl}`. The server
   ID comes from the key, not the body. Content or protocol mismatch → 409. Re-register after a restart.
2. `POST /v1/servers/{serverId}/heartbeat` `{"activeMatches"}` every `heartbeatIntervalSeconds` (5 s). A server not
   heard from (heartbeat or poll) for `StaleAfterSeconds` (20 s) is not allocated, and its running matches are
   **aborted** by the watchdog (no results, no rewards; convoy released).
3. `GET /v1/servers/{serverId}/assignments?waitSeconds=0..30` — long-poll. Returns `{"assignments":[…]}`
   (possibly empty). Unacknowledged assignments are re-delivered on every poll; de-duplicate by `matchId`.
4. `POST /v1/servers/{serverId}/assignments/{matchId}/ack` — accept. The control plane issues tickets only after the
   ack; no ack within 10 s aborts the allocation (convoy stays ready and can retry).

Assignment (frozen match config): `{matchId, convoyId, serverId, kind:"campaign"|"freeplay", mode, stageId,
stageNumber, stageType, courseId, freeplayMode, weather, collision, carCapPi, entrants[{accountId, displayName,
role, carId, carPi, performanceHash, cosmeticHash, loadoutRevision}], aiEntrants[ids, featured rival first],
benchmarkReplayRival, benchmark{kind, targetTimeMs, rawDriftTarget, hardTimeoutMs, provisional, source}, purePvP,
gridNote, build, protocol, contentHash, seed, resultsUrl, ticketIssuer, ticketAudience, resultsSecret}`.
`resultsSecret` is base64url (32 bytes); keep it in server memory only.

## 6. Result submission

`POST /v1/matches/{matchId}/results` with headers `X-NightSignal-Server-Key` and
`X-NightSignal-Signature: sha256=<lowercase hex HMAC-SHA256(key = base64url_decode(resultsSecret), message = the exact raw body bytes)>`.
Sign the bytes you send; do not re-serialize after signing. Body (unknown members are rejected — clients and
servers can never send money, RP or clears):

```json
{
  "matchId": "m_…", "contentHash": "…", "aborted": false, "abortReason": null,
  "entrants": [
    { "entrantId": "<accountId or AI id>", "human": true, "outcome": "Finished",
      "finishTimeMicros": 170000000, "placement": 1, "clean": true,
      "checkpointFraction": 1.0, "activeProgressVerified": true, "activelyDroveLegalCourse": true,
      "legalProgressMetres": 3100.0, "rawDriftScore": 0, "contractsPassed": 0, "challengesCompleted": ["CH01"] }
  ]
}
```

- `outcome` ∈ Core `RunOutcome`: `Finished, DidNotFinish, Quit, DisqualifiedDisconnect, DisqualifiedAfk, DisqualifiedInvalid` (names only).
- Every allocated human (DQs included — H is frozen) and every AI entrant appears exactly once.
- `placement` must equal Core `RaceClassification` (1 ms precision, equal ms = tie; DNFs by legal progress;
  quits/DQs = 0). Drift Attack ranks finishers by `rawDriftScore`.
- `challengesCompleted` are predicates the game server evaluated; ignored unless the entrant finished.
- `{"aborted": true, "abortReason": "…"}` (entrants may be empty) marks a system failure: no results or progression.

Responses: `200 {"status":"settled","receipts":[…]}` (retry of the identical body → `"replayed": true`),
`200 {"status":"aborted"}`, 400 malformed/unknown member, 401 key/signature, 403 not your match, 404 unknown match,
409 already settled with a different body / aborted, 413 > 64 KiB, 422 facts inconsistent with the allocation.

**Receipt** (per human; also `GET /v1/matches/{id}/receipt`): `{matchId, accountId, status, eventKind, mode,
stageId, courseId, outcome, placement, tied, finishTimeMs, stage{qualified, withinSupport, earnedClear, teamSuccess,
qualifiers, requiredQualifiers, frozenHumanCount, reason, benchmarkTargetMs, benchmarkProvisional, benchmarkSource},
payout{base, difficultyX100, placementX100, cleanlinessX100, utilityX100, pvpX100, eventCredits, firstClearBonus,
challengeCash, total, note}, credits[{type, requested, credited, clampedAway}], balanceAfter, clampedAwayTotal,
firstClearAwarded, challengesUnlocked[], cosmeticsGranted[], rankPointsBefore, rankPointsAfter, rank, notes[]}`.
`payout` mirrors Core `Economy.Compute`; `credits` shows each wallet credit after Core `Wallet.Credit` clamping.

## 7. Not yet covered

Browser (WSS) clients would additionally need an Origin check and a non-header way to present the token (browser
WebSockets cannot set `Authorization`); not implemented because the Web target is not in scope yet (spec §3.4).

## 8. Gameplay transport (Unity: Netcode for GameObjects 2.13.3 + Unity Transport 6.6.0)

One authoritative match per dedicated game-server process (`NightSignal.exe -batchmode -nographics -nsServer`).
NGO runs with `TickRate = 60`, connection approval on, scene management off, no NetworkObjects for cars: all race
traffic uses named messages (`Assets/Game/Runtime/Net/Wire.cs`).

**Admission.** The client puts its match ticket (UTF-8 JWS) in `NetworkConfig.ConnectionData`. The server's approval
callback validates it with Core `MatchTicketValidator` (managed P-256; Unity's Mono has no ECDSA) against this
assignment (match/build/protocol/content), burns the `jti`, and maps `sub` to an allocated racer. Unknown, late
(countdown started), duplicate or spectator tickets are refused with a reason code.

| message | direction | delivery | payload |
|---|---|---|---|
| `ns.match` | S→C | reliable fragmented | JSON `MatchInfo`: match/course/kind/mode/stage/weather, roster `[{index, entrantId, displayName, human, carId, paint, gridSlot}]`, `yourIndex`, benchmark replay rival |
| `ns.loaded` | C→S | reliable | `float` loading progress (1.0 = course collision, car assets, input and first state ready) |
| `ns.phase` | S→C | reliable | `byte phase` (Loading, Countdown, Racing, Results, Aborted), `int startTick`, `long deadlineMicros` (−1 until the first human finishes; re-sent when set so clients show the finish window) |
| `ns.input` | C→S | unreliable sequenced, every 2nd tick (30 Hz) | `int latestTick`, `byte count ≤ 8`, then `count` × input (`sbyte steer`, `byte throttle`, `byte brake`, `byte buttons`) for ticks `latestTick-count+1 … latestTick` |
| `ns.snap` | S→C | unreliable sequenced, every 3rd tick (20 Hz) | `int tick`, `byte phase`, `byte n`, then per entrant `byte index`, `byte status`, `ushort checkpoints`, `float raceDistance`, `int finishMs`, `int inputAckTick` (latest command tick received from that human, −1 for AI), `byte flags` (bit0 = not colliding: reset safety ghost / DQ / not racing; bit1 = full state follows), then the vehicle state: **full** (93 bytes) for the recipient's own car, **compact** (40 bytes: exact position, smallest-three rotation, half-precision velocities/steer, rpm, gear, suspension) for every other car. Protocol 2 sends one snapshot per client — ≈ 0.75 KB for twelve cars, one unfragmented datagram (a shared full-state snapshot would be ≈ 1.33 KB, too close to the ~1.4 KB payload limit) |
| `ns.results` | S→C | reliable fragmented | JSON `MatchResults` (the same facts sent to the control plane) |

**Clock and start.** The server simulates tick T at its NGO `LocalTime.Tick`. After the loading barrier (90 s, one
30 s extension while a connected client reports real progress; unloaded entrants are DQ; no humans → abort) it sets
`startTick = now + 240` and broadcasts it; cars are held on the grid until T ≥ startTick. Clients show 3-2-1-GO from
`ServerTime` against that tick. Race time = `(T − startTick) × 10⁶ / 60` µs; finishes are sub-tick interpolated.

**Inputs.** Clients stamp inputs with their `LocalTime.Tick` (ahead of the server by the RTT estimate) so they arrive
before the server simulates that tick. The server ignores inputs for ticks already simulated or > 120 ticks ahead.
A missing input repeats the last one for 250 ms, then coasts and brakes (spec §4.4).

**Prediction.** The client steps its own car with the same `VehicleSimulation` every local tick and keeps 256 ticks
of inputs/states. On each snapshot it compares its prediction at the snapshot tick; beyond 3 cm / 0.2 m/s it rewinds
to the authoritative state and replays stored inputs, blending the visual error out over ~0.1 s (snapping above 3 m).
The client predicts every network tick even when the clock advances several ticks in one frame (time-sync
corrections, hitches), so both sides step each car the same number of times. Round trip is measured by the game from
input send time to the first snapshot acknowledging that tick — the transport's reliable-pipeline RTT goes stale once
setup traffic stops. The server counts starved ticks (simulated without that tick's command) and late commands per
entrant and writes them into the server evidence.

Car-to-car contact (Addendum 01 §2): the race loop is `Race/RaceSimulation` — the same code in the dedicated server
and in offline play. After every car steps, the server resolves each colliding pair in entrant order with
`VehicleContact.Resolve` (oriented body boxes, one capped restitution/friction impulse, yaw-only response ≤ 1.2 rad/s,
Δv ≤ 3.5 m/s, separation ≤ 0.2 m per tick) and re-runs the barrier pass for moved cars so a nudge can never carry a
car through a guardrail. Time Attack (`contact: non-contact`) skips contact entirely. Reset safety ghosts and DQ cars
are flagged non-colliding in snapshots. The client applies the same function to its own car only, against remote cars
extrapolated from their latest authoritative state (≤ 15 ticks); reconciliation corrects any difference. Contact
incidents are debounced (750 ms) and counted separately from wall incidents; there is no damage state (D10). Cars
clearly off course for 1.5 s (> 20 m past the road edge or 6 m below it), or 40 m below, get a marshal recovery with
the normal reset penalty.
Remote cars render at `ServerTime − 6 ticks` (100 ms) with at most 9 ticks (150 ms) of extrapolation.

**Disconnects.** A racer disconnecting after admission becomes `DqDisconnected` and cannot resume driving in that
event; the race continues for everyone else. Results list every allocated human (H frozen) and every AI entrant.
