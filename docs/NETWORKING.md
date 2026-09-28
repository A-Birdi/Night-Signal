# Night Signal — networking

> This file currently documents the **control plane** protocols (implemented in `Services/`, protocol version 1,
> revised for Addendum 01 and the control-plane parts of Addendum 02). Gameplay transport (§8) is documented by the
> Unity side. All JSON is UTF-8, camelCase, and times are ISO-8601 UTC unless stated otherwise.
>
> **Change markers:** items tagged **NEW** or **CHANGED** below are the Addendum 01/02 contract the Unity client must be
> wired to. `destination.propose / destination.consent / destination.commit` are **REMOVED** (they now answer
> `unknown_type`); use `intent.set → mode.ready → mode.enter` (§3.3).

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

The public **@handle** (§2.2) is an identity/lookup key only — never a login credential. Sign-in stays e-mail/password
through the provider.

## 2. REST API (player)

Errors are always `{"error": code, "message": text}` (+ `retryAfterMs` for `429 rate_limited`). The server only ever
serves the ONLINE progression domain; nothing here accepts local/offline progress.

### 2.1 Profile, wallet, purchases

| Method, path | Body | Result / errors |
|---|---|---|
| `GET /healthz` **CHANGED** | – | `{"status":"ok","contentHash","garageContentHash","customizationContentHash","toyContentHash"}` (no auth). `garageContentHash` = SHA-256 over the LF-normalised `parts.json` + `build-recipes.json` the server evaluates builds with — published separately because neither document is in Core `ContentHash` yet (§2.6 note). `customizationContentHash` = SHA-256 of the LF-normalised `customization.json` (Core `CustomizationCatalogue.Hash`) the server validates liveries with; deliberately never part of `ContentHash` (appearance is not a simulation input). |
| `GET /v1/me` **CHANGED** | – | `{accountId, card{displayName,revision}\|null, handle{handle,revision}\|null, needsHandle, wallet{balance,cap}, starterCarId, ownedCars[{carId,source}], campaign{normalCleared[30], hardCleared[30], normalFrontier, hardFrontier, hardUnlocked}, courses{domain:"online", owned[{courseId, source:"starter"\|"purchase"\|"campaign-clear"}]}, music{owned[{cueId, source}]}, teamTrialBests[{trialId, difficulty, humans, kind:"mean"\|"best"\|"drift", value, displayMeanMs\|null, matchId, category:"team"}], challengesCompleted[], cosmeticsOwned[], rank{rankPoints,index,name,threshold,next,nextThreshold}}`. `needsHandle: true` = show the handle-claim step (existing accounts keep everything else). `music.owned` = the manifest's baseline cues plus granted cues. Team bests are TEAM records, never personal bests. |
| `POST /v1/me/card` | `{"displayName", "revision"?}` | `{displayName, revision}`; 400 `invalid_display_name`; 409 `revision_conflict` |
| `POST /v1/me/starter` | `{"carId":"V01"\|"V02"\|"V03"}` | `{carId, balance, credited, replayed}`; 409 `starter_already_claimed` |
| `POST /v1/me/purchases` | `{"idempotencyKey","itemKind":"car","itemId","expectedPrice"?}` | `{itemId,itemName,price,balance,replayed}`; 400 `invalid_price`/`invalid_idempotency_key`/`unknown_item`; 409 `price_changed`/`insufficient_funds`/`already_owned`/`idempotency_conflict` |
| `GET /v1/matches/{matchId}/receipt` **CHANGED** | – | own itemized receipt (§6); `202 {"status":"pending"}`; `{"status":"aborted"}`; 404 if not an entrant |

### 2.2 Handles and public cards — NEW (Addendum 01 §9, D08)

| Method, path | Body | Result / errors |
|---|---|---|
| `PUT /v1/me/handle` | `{"handle"}` | `{handle, revision, status:"claimed"\|"changed"\|"unchanged"}`; 400 `invalid_handle` (message explains: 3–20 ASCII, letter first, then letters/digits/`_`, reserved words such as admin/system/support refused; a leading `@` is NOT accepted here); 409 `handle_taken`; 429 `rate_limited` (10/h). Uniqueness is case-insensitive via a DB `UNIQUE` on the lowercase canonical value; display casing is kept. |
| `GET /v1/players/by-handle/{handle}` | – | exact lookup (one leading `@` stripped, case-insensitive) → `{accountId, handle, displayName, rank{rankPoints,index,name}}`; 400 `invalid_handle`; 404 `not_found`; 429 (30/min). Never e-mail, IP, tokens, wallet or inventory. |
| `GET /v1/players/{accountId}/card` | – | same card by stable account ID; 400 `invalid_account`; 404 `not_found`; 429 |

### 2.3 Friends and blocks — NEW (Addendum 01 §9.1)

Relationships are keyed by stable account IDs (one row per unordered pair); every operation is idempotent and returns
the resulting state, so retries never duplicate an edge. `state` ∈ `none | outgoing | incoming | friends`.

| Method, path | Body | Result / errors |
|---|---|---|
| `GET /v1/friends` | – | `{statusAsOf, friends[{accountId, handle, displayName, rank{name,index}\|null, status, convoyId\|null, inYourConvoy, canRejoin, canInvite, since}], incoming[{accountId,handle,displayName,since}], outgoing[…], blocked[…], counts{friends,incoming,outgoing}}`. `status` ∈ `Available, Garage, AtMeet, Preparing, Loading, Racing, Spectating, Away, Offline, Unknown` (Unknown = connected but silent > 90 s: stale, not a false Online/Offline). `convoyId` only when actionable/public (yours, one you may rejoin, or discoverable). `canRejoin`/`canInvite` are computed by the server now; they are revalidated again when used. |
| `POST /v1/friends/requests` | `{"handle"}` (leading `@` ok) or `{"accountId"}` | `{accountId, state, changed}` — a request to someone who already asked you makes you friends; 400 `invalid_handle`/`invalid_request`; 403 `blocked`; 404 `not_found`; 409 `invalid_state` (self); 429 (20/10 min) |
| `DELETE /v1/friends/requests/{accountId}` | – | cancel your outgoing request → `{accountId, state:"none", changed}`; 409 `invalid_state` if it is incoming or you are friends |
| `POST /v1/friends/requests/{accountId}/accept` | – | → `friends`; retry → `changed:false`; 404 `not_found` (no pending request); 409 `invalid_state` (your own request) |
| `POST /v1/friends/requests/{accountId}/decline` | – | → `none`; retry → `changed:false`; 409 `invalid_state` |
| `DELETE /v1/friends/{accountId}` | – | remove friend → `none`; 409 `invalid_state` for a pending request |
| `PUT /v1/blocks/{accountId}` | – | block: deletes any friendship/request, refuses future requests/invitations either way, revokes that account's rejoin grant into a convoy you lead → `{accountId, state:"none", changed}`; 404 `not_found` |
| `DELETE /v1/blocks/{accountId}` | – | unblock → `{…, changed}` |

All friend/block changes: 400 `invalid_account` for a malformed ID; 429 after 60 changes / 10 min.

### 2.4 Course access — NEW (Addendum 01 §5, D03/D04)

| Method, path | Headers / body | Result / errors |
|---|---|---|
| `GET /v1/courses` | – | `{courses[{courseId, name, kind:"tutorial"\|"regular"\|"finale"\|"freeplay", format, access{kind:"starter"\|"purchase-or-clear"\|"reward-only"\|"purchase-only"\|"none", price, unlockStage, purchasable}, freeplayModes[]}]}` — e.g. C20: purchase-or-clear 45,000 or free by clearing Normal S23; C25: reward-only (Normal S30); FP01/02/03: purchase-only 45,000/54,000/63,000. |
| `POST /v1/me/courses/{courseId}/purchase` | header `Idempotency-Key` (8–64 `[A-Za-z0-9_-]`, required); optional body `{"expectedPrice"}` | `200 {courseId, courseName, outcome:"purchased"\|"already_owned", price, charged, balance, replayed}` (`already_owned` charges 0 — e.g. the free unlock committed first); 400 `invalid_idempotency_key`/`invalid_price`; 404 `unknown_course`; 409 `price_changed`/`not_purchasable` (starter or reward-only; message says how it unlocks)/`insufficient_funds`/`idempotency_conflict` (key reused for another course); 429 (20/min) |

Core `CourseAccess.DecidePurchase` runs inside the same DB transaction as the wallet debit, ledger entry and entitlement
insert (serialized per wallet). A purchase grants ownership only — never a clear, Hard access, RP, a rival or a cue. Buying
then clearing keeps the purchase (no refund, no duplicate). Stored Normal clears made before the ledger existed still
count as ownership. A successful purchase updates the convoy's accessible pool live (no car or course is changed).

### 2.5 Rate limits (per account, sliding window)

| Action | Limit |
|---|---|
| handle lookups / card reads | 30 / min |
| handle claim/change | 10 / h |
| friend requests | 20 / 10 min |
| accept/decline/cancel/remove/block/unblock | 60 / 10 min |
| convoy friend invitations (`convoy.invite.friend`) | 20 / 10 min |
| course purchases | 20 / min |
| garage operations / quotes / settlements | 120 / 30 / 30 per min |

Display names: NFC-normalized, spaces collapsed, 3–20 user-perceived characters, ≤120 UTF-8 bytes, no `< > { } \`, no
control/format (except ZWJ)/private-use/bidi characters. Not unique. Render as literal text.

### 2.6 ONLINE Garage — NEW (Addendum 02 §8–10, D204–D206)

The server owns part ownership **per car instance**, each instance's whole Core `CarBuildWorkspace` (applied build, ≥ 8
named mechanical loadouts, ≥ 5 visual presets, the protected references `before-workshop` / `before-last-apply` /
`last-race-build`, the draft and the workshop session) and Buy-and-Apply quotes. Every rule is Core
`NightSignal.Core.Builds` (`GarageOperations`, `PurchaseQuotes`, `BuildResolver`, `PerformanceIndexEstimator`); the
control plane adds persistence, atomicity and access control. Build data: `content/authored/parts.json` and
`build-recipes.json` (validated against the race catalogue at startup; startup fails without them).

- **Car instances.** Every owned car gets one instance (`instanceId`, stable, `ci_…`) the first time the garage, a
  `loadout.set` or an event touches it; its workspace starts as the stock applied build (revision 1) with an empty
  library (no invented presets). Two instances of one model are independent (the schema supports several; purchases of a
  second copy are not offered yet). Only the owner can see or change an instance (`404 not_found` otherwise).
- **Optimistic concurrency.** Every mutation quotes `expectedRevision` = the workspace `revision` it was computed from.
  Core rejects a stale one (`409 stale_revision`, body carries the current `revision`), and the store writes with
  compare-and-swap on the revision it read, so two devices can never overwrite each other's accepted change.
- **Confirmation tokens.** Overwrite, Delete, Delete visual preset and replacing a dirty draft answer
  `409 confirmation_required {confirmationToken, message, comparison?}`; repeat the SAME request with that token. A token is
  bound to (operation, target, instance, revision), so it dies with any other change.
- **Performance truth.** PI is the Core estimate (`piIsEstimate: true`, labelled everywhere) and `buildHash` the Core
  physics hash (utility excluded), both computed by the server from the stored APPLIED build — never a draft, a preview or
  a client claim.

| Method, path | Body | Result / errors |
|---|---|---|
| `GET /v1/me/garage/cars` | – | `{domain:"online", shopAct, wallet{balance,cap}, partsCatalogue{revision, priceRevision, hash}, handlingModelVersion, cars[{instanceId, carId, carName, source, ordinal, revision, applied{revision, buildHash, pi, piClass, piIsEstimate, source, appliedUtc, needsRepair, repairs[]}, loadouts{count, capacity, pinned[{loadoutId, name, derivedPi, derivedClass, needsParts}]}, visualPresets{count, capacity}, draft{dirty, loadedFrom}\|null, references[kind], ownedParts}]}`. `shopAct` = act of the Normal frontier stage (parts unlock by act, never by spending). |
| `GET /v1/me/garage/cars/{instanceId}` | – | `{domain, instanceId, carId, carName, source, ordinal, shopAct, partsCatalogue, handlingModelVersion, ownedParts[], buildFrozen\|null, workspace, appliedEvaluation, draftEvaluation\|null, draftComparison\|null}` — `workspace` = `{schema, schemaVersion, instanceId, carId, revision, applied{revision, build, buildHash, pi, piClass, piIsEstimate, handlingModelVersion, partsCatalogueRevision, appliedUtc, source}, loadouts[{loadoutId, name, carInstanceId, carModelId, build, performanceAppearance, derivedPi, derivedClass, buildHash, handlingModelVersion, partsCatalogueRevision, note, updatedUtc, pinned, needsParts, unresolvedPartIds, notices, keptLegacyDocument}], loadoutCapacity, pinnedLimit, visualPresets[{presetId, name, payloadSchema, payloadJson, updatedUtc}], visualPresetCapacity, appliedVisualPresetId, appliedLiveryHash, appliedLivery, references{kind: {kind, build, sourceAppliedRevision, buildHash, pi, handlingModelVersion, partsCatalogueRevision, capturedUtc, context}}, draft{build, loadedFrom, basedOnAppliedRevision, previewPartIds, unresolvedPartIds, updatedUtc}\|null, draftDirty, workshop{open, openedUtc}}`. `appliedLivery` is the applied livery as canonical JSON (`night-signal/livery@1`; "" = stock appearance), `appliedLiveryHash` its server-computed `LiveryHash` ("" for stock), `appliedVisualPresetId` the preset it was applied from ("" = edited). A `build` is `{parts:{slotId: partId}, utilityPartId, tuning:{version, values:{key: int}}}` (ids, never positions). An evaluation is `{resolved, buildHash, pi{value, class, isEstimate, basis}, canPreview, canSaveAsPlan, canApply, previewPartIds, missingParts[{partId, slot, name, price}], missingTotal, repairs[{kind, slot, partId, partName, detail, price, text}], utility{partId, incomePercent, showcasePercent}, …}`; repair kinds: `unknown-slot, removed-part, wrong-slot, incompatible, locked, not-owned, unavailable, tuning-invalid, out-of-safe-range, over-cap, build-locked, unresolved-legacy-part`. |
| `GET /v1/me/garage/cars/{instanceId}/parts` | – | `{instanceId, carId, shopAct, partsCatalogue, slots[{slot, parts[{partId, name, tier, price, unlockAct, retired, available, owned, installed, inDraft, tradeoff, appearance, utility{kind, percent}\|null, tuning[{key, min, max, step, default, unit}]}]}], recipes{role, identity, capExcludedBands, steps[{id, label, kind:"main"\|"incremental"\|"longTerm", by, bands, note, build, pi, piClass, piIsEstimate, missingParts[{partId, name, price}], missingTotal, lockedParts[], canApplyNow}]}\|null}` — only parts compatible with this model; `owned` means owned by THIS instance; recipes are the authored explained options (never auto-purchases). |
| `POST /v1/me/garage/cars/{instanceId}/operations` | `{op, expectedRevision, confirmationToken?, …}` | One Core `GarageOperations` call. `op` → fields: `save-as` {name, note?, fromApplied?} · `rename` {loadoutId, name} · `note` {loadoutId, note} · `overwrite` {loadoutId, fromApplied?, confirmationToken} · `duplicate` {loadoutId, name} · `delete` {loadoutId, confirmationToken} · `pin` {loadoutId, pinned} · `visual-preset-save` {name, payloadSchema?, payloadJson? (JSON ≤ 16 KiB, data only; with payloadSchema `night-signal/livery@1` ≤ 32 KiB, must be a valid livery for this car in Preview mode — locked items allowed — and is stored canonical)} · `visual-preset-update` {presetId, payloadSchema?, payloadJson?, confirmationToken} (same payload rules; identical payload → `unchanged`) · `visual-preset-rename` {presetId, name} · `visual-preset-delete` {presetId, confirmationToken} · `livery-apply` {liveryJson? (≤ 32 KiB; null/"" = stock appearance), presetId? (must hold exactly this look; "" = edited)} (validated in Apply mode against `customization.json` and the account's `cosmetics_owned`; stores the canonical JSON and the server hash; never touches the applied build; in a convoy a cosmetic-only change, readiness kept) · `load-into-draft` {source:{kind:"applied"}\|{kind:"loadout",id}\|{kind:"reference",id:"before-workshop"\|"before-last-apply"\|"last-race-build"}, confirmationToken?} · `edit-draft` {build} (may contain "Preview only — not owned" parts) · `discard-draft` · `apply` (the whole draft atomically) · `apply-loadout` {loadoutId} (quick selector) · `begin-workshop` (no revision; captures Before Workshop once) · `end-workshop` · `accept-baseline`. → `200 {status:"ok"\|"unchanged", op, message, revision, loadoutId, performanceChanged, evaluation, comparison, repairs[], changes[], convoy{selected, performanceChanged}\|null, workspace}`. Errors: 400 `invalid_request` (unknown op, missing `expectedRevision`/target, bad build/payload) / `invalid_name` / `invalid_livery` (+`errors[]` = Core `LiveryJson`/`LiveryValidator` messages, `locked[{path, itemId, name, cosmeticId}]`); 404 `not_found`; 409 `stale_revision` / `confirmation_required` / `duplicate_name` / `capacity_full` / `pin_limit` / `needs_repair` (+`repairs`, nothing bought, substituted or changed) / `build_locked` (this car is in an event being allocated) / `no_draft` / `rejected`; 429 (120/min). |
| `POST /v1/me/garage/cars/{instanceId}/quote` | `{source?:"draft"}` (default) or `{build}` | Core `PurchaseQuotes.Create` → `200 {quote{quoteId, instanceId, carId, build, buildHash, pi, piClass, piIsEstimate, lines[{partId, slot, name, tier, price}], total, priceRevision, catalogueRevision, appliedRevision, issuedUtc, expiresUtc}, affordableNow, walletBalance, message, confirmation}`. Lines are exactly the parts THIS instance does not own (owned parts are never bought again); the quote is stored server-side for 10 minutes. 409 `no_draft` / `nothing_to_buy` (everything owned: use `apply`) / `not_purchasable` (+`repairs`: locked by act, retired, incompatible, tune, cap, build lock); 429 (30/min). |
| `POST /v1/me/garage/cars/{instanceId}/quote/{quoteId}/settle` | `{"confirm": true}` | Core `PurchaseQuotes.Settle` INSIDE one transaction with the wallet debit (ledger `garage-quote/<account>/<quoteId>`, type `part-purchase`), the part grants, the workspace change (applied revision + 1, `before-last-apply`) and the quote-ledger row (unique quote id). → `200 {status:"settled", replayed:false, quoteId, charged, debit, grants[], balance, appliedRevision, buildHash, pi, piClass, piIsEstimate, performanceChanged, message, convoy, revision, workspace}`; a retry (or a concurrent duplicate) → `200 {status:"settled", replayed:true, charged:0, debit, grants, balance, balanceAfterSettlement, appliedRevision, buildHash, settledUtc}` — never a second charge or grant. Rejections change nothing (wallet, ownership, workspace and draft stay as they were): 404 `unknown_quote`; 409 `confirmation_required` (confirm missing/false), `quote_expired`, `price_changed` (+`currentLines`: price or catalogue revision moved), `stale_revision` (this car's applied build changed since the quote), `unavailable`, `incompatible`, `already_owned`, `insufficient_funds` (+`total`, `balance`), `needs_repair`, `wrong_car`; 429 (30/min). |

A Garage change of the car SELECTED in the player's convoy (apply, apply-loadout, restore, Buy-and-Apply) updates that
member's server loadout at once: a performance-hash change bumps `loadoutRevision` and clears only THAT player's Event
Ready; a utility-only change (same physics hash) keeps readiness. While the convoy is Allocating an event with that car,
applying answers `build_locked`.

## 3. Control channel — `GET /v1/control` (WebSocket)

Upgrade with `Authorization: Bearer <token>` and query `build=<client build>&protocol=1&content=<ContentHash>`
(optional `takeover=1`). Clients without all three version parameters can use the channel but cannot start events.

**Client → server:** `{"type": string, "requestId": string(1–64), "payload": object}`.
**Server → client:** `{"type": string, "revision": number, "payload": object}` — `revision` is the convoy's
monotonic revision for convoy messages (discard anything older than what you have), otherwise 0.

Every request gets `{"type":"reply","payload":{"requestId","ok","result"?,"error"?:{"code","message","retryAfterMs"?}}}`.
A **retried requestId** (same account and type, within 10 minutes, even after reconnecting) returns the original
reply without re-executing (read-only types `ping`, `convoy.state`, `convoy.list`, `rejoin.status` are never cached).
Clients must therefore make requestIds **unique across sessions**, not just within one connection: the Unity client
uses a random per-process prefix plus a counter. (A counter restarting at `r1` replayed the previous process's
`convoy.create` reply in a real 6-client run — V-025.)
Max message 16 KiB. Send `ping` at least every 30 s: an account silent for > 90 s shows as `Unknown` to friends.

Close codes: `4400 unsupported_protocol`, `4401 token_expired` (send `session.reauth` before `tokenExpiresAt`),
`4409 active_elsewhere` (reconnect with `takeover=1` to move the session; the older client gets `session.superseded`
then `4410`), `1009` message too big.

**A closed control socket is a confirmed disconnect** (Addendum 01 §10.1): the account leaves ACTIVE convoy membership
immediately — readiness, Mode Ready and any ballot are cleared, the seat is released, one roster change is published — and
a server-owned **rejoin grant** is recorded (§3.4). There is no reserved seat and no silent AI replacement.

### 3.1 Requests — membership, invitations, rejoin

| type | payload | who | result / errors |
|---|---|---|---|
| `ping` | – | any | `{pong, serverTime}` |
| `activity` | – | any | user-interaction heartbeat (clears Away) |
| `session.reauth` | `{accessToken}` | any | same account only; `unauthorized` |
| `convoy.state` | – | any | `{revision, convoy}` resync (`convoy` null when not a member) |
| `convoy.list` | – | any | discoverable, non-dormant convoys with open seats: `[{convoyId, leaderName, members, maxMembers, privacy, privacyLabel, phase, intent}]` |
| `convoy.create` | `{privacy:"invite-only"\|"discoverable"}` | not in a convoy | `{convoyId}`; `already_in_convoy`. Creating/joining any convoy drops your old rejoin grant. |
| `convoy.join` **CHANGED** | `{code}` / `{convoyId}` (discoverable) / **`{inviteId}`** (friend invitation) | not in a convoy | `{convoyId, spectator}`; `already_in_convoy`, `invite_invalid`, `invite_required`, `not_found`, `rate_limited` (codes: 10 / 10 min), `convoy_full`, **`convoy_dormant`**, **`invite_denied`** (friendship gone / block with inviter or leader — checked at use) |
| `convoy.leave` | – | member | explicit Leave: no rejoin grant. A leader leaving hands leadership on at once (epoch + 1). The last member leaving ends the session. |
| `convoy.kick` **NEW** | `{accountId}` | leader | removes an active member (no grant; `convoy.closed` reason `kicked`) → `{removed:true, rejoinRevoked:true}`, or revokes a departed member's grant → `{removed:false, rejoinRevoked:true}`; `not_leader`, `leader_unavailable`, `not_found`, `invalid_request` (self) |
| `convoy.disband` **NEW** | – | leader | ends the convoy session now for everyone (`convoy.closed` reason `disbanded`; all grants end) → `{convoyId, disbanded:true}` |
| `convoy.invite.create` | – | leader | `{code, expiresAt}` (8 chars, 15 min, ≤5 active) |
| `convoy.invite.revoke` | `{code}` | leader | `not_found` |
| `convoy.invite.friend` **NEW** | `{accountId}` | leader (private) / any member (discoverable) | accepted friends only, never across a block → `{inviteId, expiresAt, delivered}`; the target gets `convoy.invited`. Re-inviting refreshes the same invitation. `invite_denied`, `not_leader`, `already_member`, `convoy_full`, `too_many_invites` (12 pending), `rate_limited`, `not_in_convoy` |
| `convoy.invite.decline` **NEW** | `{inviteId}` | invitee | `{}` (idempotent) |
| `rejoin.status` **NEW** | – | any | the RejoinStatus object (§3.4) |
| `convoy.rejoin` **NEW** | – | not in a convoy | atomic revalidation of YOUR grant → `{convoyId, leader, spectator}`; distinct errors: **`rejoin_unavailable`** (no grant), **`rejoin_revoked`** (kicked/blocked), **`rejoin_disbanded`** (convoy gone or dormant room expired), **`rejoin_leader_changed`** (epoch changed — ask for a new invitation), **`convoy_full`** (six active; permission kept), `already_in_convoy`. Restores membership only: during a match you spectate and a departed entrant never gets a racer ticket again. |
| `rejoin.dismiss` **NEW** | `{forget?: bool}` | any | "Not Now" (`forget:false`) keeps eligibility and sets `prompt:false`; "Forget convoy" (`forget:true`) discards the grant → RejoinStatus |

### 3.2 Requests — presence, loadout, diversions

| type | payload | who | result / errors |
|---|---|---|---|
| `presence.set` **CHANGED** | `{presence:"InMenus"\|"Garage"\|"AtMeet"\|"Browsing"\|"LoadingRace"\|"InRace"\|"Spectating"}` | any connected account (members also publish it to the convoy) | actual coarse screen/activity, separate from the convoy Intent; never clears readiness. `Reconnecting`/`Offline` are server-only (`invalid_request`). |
| `loadout.set` **CHANGED** | `{carId \| instanceId, cosmeticHash?, performanceHash?}` | member | Selects an owned car INSTANCE (`instanceId`, or the account's instance of `carId`); `not_owned` otherwise. The performance hash, PI and applied revision are computed by the SERVER from that instance's stored applied build (§2.6), and the cosmetic hash from its stored applied livery (`LiveryHash`; for stock, of the chassis' stock livery); client `performanceHash`/`cosmeticHash` are accepted for older clients (≤ 128 characters) but **ignored**. A Garage `livery-apply` on the selected car updates the cosmetic hash (`cosmeticRevision+1`, readiness kept). → `{loadoutRevision, cosmeticRevision, performanceChanged, carId, instanceId, performanceHash, carPi, piClass, appliedRevision}`. Performance change (other car/instance or other server hash) → `loadoutRevision+1`, only this member unreadies; cosmetic-only → `cosmeticRevision+1`, stays ready; `event_frozen` while Allocating; `loadout_illegal` when the applied build needs repair (e.g. a removed part). |
| `diversion.set` **NEW** (Addendum 02 §1.2) | `{toy: "cap-clash"\|"pit-crew"\|"greenlight"\|"pocket-circuit"\|"convoy-canvas"\|"test-yard"\|null}` | member | coarse participation → `{diversion}`. Counts as real interaction (clears Away). Entering, changing or leaving a diversion NEVER clears Mode Ready or Event Ready and never readies anyone. `invalid_request` for other values. |

### 3.3 Requests — Intent → Mode Ready → vote → Event Ready (Addendum 01 §6.2, §7)

| type | payload | who | result / errors |
|---|---|---|---|
| `intent.set` **NEW** | `{kind:"campaign", mode:"normal"\|"hard"}` / `{kind:"freeplay", submode?:"sprint"\|"circuit"\|"drift-attack"\|"time-attack"\|"cup"}` / `{kind:"challenges", trialId?}` | leader | new mode-proposal revision → `{modeRevision}`; clears every Mode Ready (the leader's proposal is their own), ends any ballot (one notice), event proposal and open post-event decision; `ready.requested{kind:"mode"}` to all. `invalid_request`, `not_leader`, `leader_unavailable`, `event_frozen`, `rate_limited` (15 s ready-request cooldown), `mode_locked` (Hard needs every member's Normal S30), `unknown_trial` |
| `mode.ready` **NEW** | `{modeRevision, ready: bool}` | member | Mode Ready for THIS revision; `stale_revision`, `bad_phase`, `event_frozen`. Unready while a vote runs cancels it (one notice). |
| `mode.enter` **NEW** | `{modeRevision}` | leader | commits once every current member is Mode Ready for this revision and not Away → `{intent, modeRevision}`; phase → `EventSelection`. **Never starts an event.** `bad_phase`, `stale_revision`, `not_all_ready`, `mode_locked`. Solo: immediate. |
| `voting.set` **NEW** | `{enabled: bool, durationSeconds?: 15\|30\|45\|60}` | leader | prepare the ON/OFF toggle any time except during a vote → `{enabled, durationSeconds}`; `ballot_open`, `invalid_request` |
| `ballot.open` **NEW** | `{durationSeconds?, weather?, aiCount?, carCapPi?, aiRivals?[]}` | leader | Freeplay only, mode entered, submode known (not cup), voting ON, EVERY current member (leader included) Mode Ready → `{ballotRevision, deadline, durationSeconds}`; server deadline (default 30 s); `ready.requested{kind:"vote"}`. An unstarted event proposal gives way to the vote. `bad_phase`, `invalid_request`, `ballot_unsupported` (cup), `voting_off`, `ballot_open`, `ballot_already_drawn` (one draw per mode agreement), `not_all_ready`, `capacity_exceeded`, `rival_not_allowed`, `post_event_open` |
| `ballot.vote` **NEW** | `{ballotRevision, courseId \| null}` | member | one changeable ballot per active member until the deadline (`null` withdraws) → `{ballotRevision, courseId}`; `stale_revision`, `ballot_closed` (deadline passed — ballots frozen), `invalid_request` (unknown course), `mode_unsupported`, `course_locked` (no current member owns it) |
| `ballot.draw` **NEW** | `{ballotRevision}` | leader | after the deadline only: one server-CSPRNG draw over accepted ballots (Core `Ballot.Draw`; chance = votes / total ballots), stored with the ballot revision → `{ballotRevision, courseId, method:"draw", ballotIndex, totalBallots, votes, replayed}`; a retransmit returns the SAME winner (`replayed:true`). Creates the frozen event proposal (origin `draw`) — Event Ready from everyone is still required. `ballot_open` (no early draw), `no_votes` (select directly), `ballot_resolved`, `stale_revision`, `bad_phase` |
| `ballot.cancel` **NEW** | `{ballotRevision}` | leader | cancels an undrawn vote (one notice); `ballot_resolved` (a draw cannot be cancelled to fish for another), `stale_revision` |
| `event.propose` **CHANGED** | campaign `{stageId, weather?}`; freeplay `{courseId, freeplayMode?, aiCount?, aiRivals?[], carCapPi?, weather?}`; cup `{freeplayMode?:"cup", cupLegs[2–5], aiCount?, aiRivals?, carCapPi?, weather?}`; challenges `{trialId?, difficulty?, weather?}` | leader | needs an entered mode → `{proposalRevision}`; `ready.requested{kind:"event"}`. Freeplay: course must support the agreed submode and be convoy-accessible (≥1 current member owns it; others get guest passes at start); AI 0..(12 − H), none in Time Attack; `aiRivals` checked with Core `FinalRivals` (R40/R48 refused as Freeplay opponents/Cup substitutes). After a frozen vote this is a visible "leader selection"; replacing a DRAWN course is a visible override (`convoy.notice draw_overridden`) that invalidates all Event Ready. Challenges: roster/AI/cap fixed by the trial. Errors: `bad_phase`, `post_event_open`, `ballot_open`, `rate_limited`, `invalid_request`, `stage_locked`, `mode_locked`, `mode_unsupported`, `course_locked`, `capacity_exceeded`, `rival_not_allowed`, `unknown_trial` |
| `event.ready` **CHANGED** | `{proposalRevision, loadoutRevision, ready}` | member | both revisions current (`stale_revision`); the server re-reads the selected car's applied build first — if its performance hash changed (e.g. applied from another device) the member's `loadoutRevision` is bumped and the answer is `stale_revision`; car cap checked with the SERVER PI (`loadout_illegal`); `loadout_required`, `event_frozen`, `bad_phase` |
| `event.start` **CHANGED** | `{proposalRevision}` | leader | atomic revalidation (below) → `{status:"allocating", entrants, aiEntrants, vehicles, guestPasses}`; `stale_revision`, `not_all_ready` (also: an entrant's applied build changed since they readied — only they are unreadied), `loadout_illegal` (server PI over the cap, or an applied build the server cannot validate), `version_mismatch`, `stage_locked`, **`course_locked`** (no sponsor in fresh storage), **`roster_invalid`** |
| `match.ticket` **CHANGED** | `{role:"racer"\|"spectator"}` | member | fresh ticket for the current match; racer only for frozen entrants who have not left since allocation (`not_entrant`), `not_found` |

### 3.4 Requests — post-event Continue / Service Break — NEW (Addendum 02 §7, D203)

After a match's results are **settled** (never after an abort), the server opens a PostEventDecision
`{sourceResultId, rosterRevision, destinationRevision, destination, choices}` for the CURRENT members.

| type | payload | who | result / errors |
|---|---|---|---|
| `postevent.choose` | `{destinationRevision, choice:"continue"\|"service-break"\|"undecided"}` | member | → `{destinationRevision, choice}`. Any `service-break` puts the convoy in `intermission` (one `convoy.notice service_break`, nobody forced to the Garage). Choices can change before the leader commits (≤12 changes/min → `rate_limited`). `stale_revision`, `bad_phase`, `invalid_request` |
| `postevent.advance` | `{destinationRevision}` | leader | only when every other current member chose `continue` (the leader's Advance is their own Continue; solo = immediate). Revalidates roster, progress and destination; → `{destination, stageId?, proposalRevision?}`: `next-stage`/`retry-stage` open that stage's event proposal (origin `post-event`) that still needs everyone's Event Ready; `campaign-complete`/`event-setup` return to selection. Never starts a race, never an S31, never a silent Hard start. `not_all_continue`, `stale_revision` (the destination changed — choices reset), `bad_phase`, `not_leader`, `leader_unavailable`, `stage_locked` |

Destinations: campaign → **Next Stage S(n+1)** when every current member may select it, else **Retry Stage S(n)** on the
limiting frontier (`needs[]` lists who has not cleared it, neutrally), else back to the map; after S30 with everyone cleared
→ `campaign-complete` (Normal: Hard needs its own `intent.set`; Hard: no S31). Freeplay/Cup → `event-setup`
("Return to Event Setup"); Challenges → `event-setup` ("Return to Challenges"). While a decision is open, `event.propose`
and `ballot.open` answer `post_event_open`; `intent.set` closes it. A roster change that changes the destination issues a new
`destinationRevision` and resets choices (one `convoy.notice post_event_changed`); otherwise remaining Continue choices are
kept (including through a service break). Undecided is never Continue; after 30 s the snapshot only sets `quiet:true`.

### 3.5 Rules and timers

- Capacities (Core `Limits`): ≤6 active members; ≤6 human entrants; ≤12 race vehicles (humans + friendly AI + opposing AI).
- The leader may request readiness (`intent.set`, `event.propose`) at most once per 15 s (`rate_limited` + `retryAfterMs`).
- Any event-setting change → new proposal revision, everyone unreadies. Any join/leave/removal → `rosterRevision+1`, a new
  mode revision (Mode Ready cleared; the entered mode stays entered), an open vote is cancelled (one notice), the event
  proposal is re-issued (readiness cleared) and **withdrawn with a notice** when the new roster cannot access it (campaign
  frontier, or the course lost its last sponsor).
- 120 s without interaction while a mode or event proposal is open → `away:true`, unready.
- **Disconnect** removes active membership at once and records a rejoin grant `{grantId, accountId, convoyId,
  leadershipEpoch, reason, permissions:"member", createdAt}` (at most one per account). A grant is invalid once the convoy's
  **leadership epoch** changes (even if leadership later returns to the same person), the convoy disbands, the account is
  kicked/blocked by the leader, or chooses Leave/Forget, or joins/creates another convoy. No hidden time expiry.
- **Leader loss**: 15 s leader-unavailable grace (`leaderUnavailable{since, transferAt}`; leader-only requests answer
  `leader_unavailable`). A leader who explicitly rejoins inside it keeps leadership (same epoch). Otherwise the
  longest-connected member (ties: lowest account ID) leads and the epoch increments; holders are pushed `rejoin.status`.
- **Dormant rooms** (Addendum 02 D208): when the last active member is lost *solely to disconnection* the convoy becomes
  Dormant for at most 24 h from that loss (server clock; reconnect attempts, `ping`s and status polls never extend it). It is
  not listed or joinable (`convoy_dormant`), holds no seats and runs nothing; a compact snapshot with the valid grants is
  persisted and survives a control-plane restart. A valid `convoy.rejoin` restores it (a new active period; an absent leader
  gets the ordinary 15 s grace). After 24 h it is retired (`rejoin_disbanded`). A last member who LEAVES, or
  `convoy.disband`, ends the session at once.
- **Sessions**: `convoySessionId` is stable across leadership changes, events and dormancy; every membership has a new
  `membershipGeneration` (a rejoin never inherits a stale generation's input rights).

**Start** re-checks, under one lock: leader, proposal and roster revision, every member ready against the current proposal
*and* loadout revision and not Away, every entrant's applied build freshly re-resolved by the server (it must still have the
performance hash they readied with; it is frozen into the plan), car caps against those server PIs, identical client build/protocol/content, stage access with progress freshly
read from the database, and course sponsorship with entitlements freshly read from the database. It then freezes the plan
with Core `RosterPlanner` — campaign: the stage's authored live opposition (featured rival first; finales are H + 1 duels
with R40/R48); Freeplay: 0..(12 − H) opposing AI (explicit picks first, then a server-shuffled pool without R40/R48; a stale
request is clamped with a notice, never ejecting a human; Time Attack none and non-contact); Team Trial: six player seats
(H humans + 6 − H friendly AI) v six opposing AI — plus event-scoped **guest passes** for every entrant who does not own a
non-campaign course (sponsor recorded; they survive the sponsor's later departure or DQ) and moves to `Allocating`.

### 3.6 Server messages

| type | payload |
|---|---|
| `hello` **CHANGED** | `{accountId, serverTime, protocol, tokenExpiresAt, rejoin: RejoinStatus}` |
| `convoy.state` **CHANGED** | snapshot (below) |
| `ready.requested` **CHANGED** | `{kind:"mode", modeRevision, intent}` / `{kind:"vote", ballotRevision, mode, deadline}` / `{kind:"event", proposalRevision}` / `{kind:"post-event", destinationRevision, destination, label}` |
| `convoy.notice` **NEW** | `{code, message}` — sent once per event; the snapshot keeps the latest as `notice`/`noticeCode`. Codes: `ballot_cancelled`, `draw_overridden`, `proposal_withdrawn`, `leader_unavailable`, `leader_changed`, `leader_returned`, `joined_as_spectator`, `grid_clamped`, `allocation_failed`, `match_aborted`, `service_break`, `post_event_changed` |
| `rejoin.status` **NEW** | RejoinStatus, pushed when your grant changes validity (epoch change, disband, revoke, dormancy) |
| `convoy.invited` **NEW** | `{inviteId, convoyId, fromAccountId, fromName, leaderName, members, maxMembers, privacy, intent, expiresAt}` |
| `match.allocated` | private per entrant: `{matchId, role, ticket, expiresAt, server{host,port}, build, protocol, contentHash}` (only to entrants who are still members) |
| `match.aborted` | `{matchId, reason}` — server lost, no results in time, the server reported an abort, or a live opponent never started; no results/rank/progression |
| `convoy.closed` **CHANGED** | `{convoyId, reason:"left"\|"disconnected"\|"kicked"\|"disbanded"}` |
| `session.rejected` / `session.superseded` / `session.expired` | explanation before the close code |
| `error` | `{error:"malformed"}` for unparseable envelopes (the session continues) |

**RejoinStatus** `{canRejoin, reason:"eligible"\|"full"\|"leader_changed"\|"disbanded"\|"revoked"\|"none"\|"in_convoy",
grantId, convoyId, leaderName, members, maxMembers, leadershipEpoch, youAreLeaderInGrace, dormant, dormantExpiresAt,
prompt, message}`. Show ONE "Rejoin [convoy]?" prompt when `prompt` is true (Rejoin → `convoy.rejoin`, Not Now →
`rejoin.dismiss`); dedupe by `grantId` across reconnect retries; never offer Rejoin from local storage alone.

**convoy.state snapshot** `{convoyId, convoySessionId, privacy, privacyLabel, phase:"Idle"\|"ModeCheck"\|"EventSelection"\|
"ReadyCheck"\|"Allocating"\|"InMatch", rosterRevision, leaderId, leaderName, leaderLabel, leadershipEpoch,
leaderUnavailable{since,transferAt}\|null, maxMembers, members[{slot, accountId, displayName, membershipGeneration, isLeader,
connection, presence, away, modeReady, eventReady, carId, loadoutRevision, cosmeticRevision, diversion, spectator}],
intent{kind, mode, submode, trialId, label}\|null, modeRevision, modeEntered, modeReadyCount, voting{enabled,
durationSeconds, choices[15,30,45,60]}, ballot\|null, freeplayAccess\|null, eventProposal\|null, postEvent\|null,
campaignAccess{normal,hard:{allowed,maxSelectableStage,explanation,limitingPlayers}}, match{matchId, entrants, departed}\|null,
readyRequestCooldownMs, notice, noticeCode}` where

- `ballot` = `{revision, modeRevision, state:"open"\|"frozen"\|"resolved", mode, durationSeconds, openedAt, deadline,
  remainingMs, ballots{accountId: courseId}, tallies[{courseId, votes, chance}], totalBallots, noVotes, options{weather,
  aiCount, carCapPi, aiRivals}, result{courseId, method:"draw"\|"leader-selection", ballotIndex, totalBallots, votes, chance,
  overridden, at, order[{accountId, courseId}]}\|null}` — `order` is the canonical ballot order the draw indexed; animate
  towards `result.courseId` (late viewers use `at`); presentation never decides the winner.
- `freeplayAccess` = `{submode, courses[{courseId, sponsors[], guests[]}]}` — the convoy-accessible union for the intent's
  submode ("Available with Robin — guest access for this event").
- `eventProposal` = `{revision, rosterRevision, origin:"leader"\|"draw"\|"post-event", ballotRevision, settings{kind:
  "campaign"\|"freeplay"\|"trial", mode, stageId, stageNumber, stageType, courseId, freeplayMode, weather, aiCount, carCapPi,
  collision:"light-contact"\|"non-contact", benchmarkTargetMs, benchmarkProvisional, benchmarkSource,
  requiresBeatingFeaturedRival, cupLegs, aiRivals, trialId, difficulty}, ready{accountId: loadoutRevision}, sponsors{courseId:
  [accountIds]}, rosterPreview{humans, friendlyAi, opposingAi, vehicles, contact}}`.
- `postEvent` = `{sourceResultId, rosterRevision, destinationRevision, destination{kind:"next-stage"\|"retry-stage"\|
  "campaign-complete"\|"event-setup", label, stageId, mode, needs[]}, state:"deciding"\|"intermission", choices[{accountId,
  choice}], continueCount, serviceBreakCount, undecidedCount, advanceEnabled, solo, openedAt, quietAfter, quiet}`.

No wallet, e-mail or private selections appear in convoy state.

### 3.7 The meet — Cedar Lantern Terrace rooms **NEW** (spec §12, D02)

Meet rooms are hosted by the control plane on this channel (like the toys): a separate room from any race, never
touching money, RP, unlocks, convoys or readiness. Rules are Core `MeetRoom` (shared with the game), under the
`MeetService` lock; a pump (~10 Hz) ticks rooms and pushes. At most **six humans** per room (D02); the other bays hold
display cars (`MeetLayout.AmbienceBays` 3, 5, 8, 10, 12).

| type | payload → result |
|---|---|
| `meet.join` | `{kind:"public"\|"friend"\|"convoy", friendAccountId?, instanceId?}` → `{roomId, status:"Ok"\|"Rejoined", state}`. Public: the fullest public room with a place, never one holding someone either side has blocked; else a new room. Friend: friends only (`not_friends`), not blocked (`blocked`), the friend must be at a meet (`friend_not_at_meet`), a place or your reservation (`meet_full`). Convoy: one room per convoy session (`not_in_convoy`, `meet_full`), members parked together on one side. The car is the owned `instanceId` (its APPLIED livery, PI/class and a tune summary) or the first owned car (stock); `needs_card`, `needs_car`, `not_owned`. The server chooses the bay (never the client). Joining again within the disconnect grace resumes the same bay (`Rejoined`) with no new arrival notice. 10 joins/min. |
| `meet.arrived` | `{}` → `{arrived, x, z, yaw}` — the arrival drive (3.5 s) ended; the avatar stands at the server's validated free point beside the car. The room also completes an arrival itself 6 s after the join. |
| `meet.move` | `{x, z, yaw, speed, seq}` → `{status:"Accepted"\|"Ignored"}` or `{status:"Corrected", x, z, yaw}` — accepted only when newer (seq), present, inside the enclosure and clear of fixtures and parked cars, and within 4.6 m/s (+0.6 m) of the last accepted pose; otherwise the last good pose stands and the client snaps to it. Not cached by requestId; 30/s. |
| `meet.emote` | `{emote:"Wave"\|"Bow"\|"ThumbsUp"\|"Clap"\|"Point"\|"CameraPose"\|"Stretch"\|"Cheer"\|"Shrug"\|"Nod"\|"Footwork"\|"Admire"}` → `{emote, startMs, durationMs}`; ≥ 0.4 s apart (`emote_refused`). |
| `meet.chat` | `{index}` → quick-chat phrase index into `story/meet.text.json` `quickChat` (no free text); one per 1.5 s (`chat_refused`). |
| `meet.like` | `{accountId}` → `{accountId, likes, cosmeticOnly:true}` — toggles a cosmetic like on someone's car; never your own, never across a block (`not_available`). |
| `meet.invite` | `{accountId}` (a friend) → `{accountId, bay, untilMs}`; holds a bay for 30 s and pushes `meet.invited` to the friend. |
| `meet.boombox` | `{op:"acquire"\|"release"\|"queue"\|"withdraw"\|"skip", trackId?}` → `{status, boombox}` or error `boombox_<status>` (`leaseheld`, `nolease`, `notowned`, `queuefull`, `toosoon`, `outofrange`, `unknowntrack`, `nothingqueued`). Core `BoomboxState`: 15 s renewable lease (acquire again to renew; the UI does every 8 s), one request per person, six queued, ≥ 10 s between user-triggered changes, only owned cues (baseline + granted), acquire/queue/skip within 3.2 m of the boombox. Lengths come from `authored/music.cues.json`. |
| `meet.leave` | `{}` → `{left}` — an explicit departure (menu, Garage, a race allocation): "departed" once, the member fades for 0.5 s, then the bay is released. |
| `meet.state` | read-only → the state below. |

Pushes: **`meet.state`** (ordered lane, when the room's revision changed) = `{roomId, kind, revision, serverTimeMs,
capacity, you, members[{accountId, displayName, carId, livery (LiveryWire)|null, bay (1–12), state:"arriving"|"present"|
"leaving"|"disconnected", stateSinceMs, x, z, yaw, speed, poseMs, emote|null, emoteStartMs, chat{index, atMs}|null (hidden
across a block), likes, likedByYou, blocked, pi, piClass, tune}], reservations[{bay, untilMs, forYou}], events[{seq, key,
kind:"arrived"|"departed"|"disconnected", accountId, name, atMs}] (last 32; show each key once), boombox{trackId, startedMs,
submittedBy, revision, leaseHolder, leaseUntilMs, queue[{accountId, trackId}]}}`; **`meet.poses`** (low-priority lane,
~10 Hz, latest wins) = `{roomId, serverTimeMs, poses[{accountId, x, z, yaw, speed, poseMs}]}`; **`meet.invited`** =
`{roomId, fromAccountId, fromName, untilMs, bay}`.

A dropped control connection marks the member `disconnected` (announced once as disconnected, never as left), holds
the avatar and bay for 30 s, and releases them quietly afterwards. Clients animate emotes from `emoteStartMs` (server
clock estimated from `serverTimeMs`), interpolate poses ~150 ms behind, play the bundled boombox cue locally (hearing
grants nothing; unreached boss themes stay protected locally), and never stream audio.

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
`TicketFailure` code — log the code, never the ticket. Racer tickets admit only entrants listed in the assignment; the
control plane stops issuing racer tickets to an entrant who left the convoy after allocation (a DQ entry never resumes).

## 5. Game-server protocol

1. `POST /v1/servers/register` `{"endpoint":{"host","port"},"build","protocol":1,"contentHash","maxMatches"}` →
   `{serverId, heartbeatIntervalSeconds, assignmentsUrl, ticketIssuer, ticketAudience, ticketJwksUrl}`. The server
   ID comes from the key, not the body. Content or protocol mismatch → 409. Re-register after a restart.
2. `POST /v1/servers/{serverId}/heartbeat` `{"activeMatches"}` every `heartbeatIntervalSeconds` (5 s). A server not
   heard from for `StaleAfterSeconds` (20 s) is not allocated, and its running matches are **aborted** by the watchdog.
3. `GET /v1/servers/{serverId}/assignments?waitSeconds=0..30` — long-poll → `{"assignments":[…]}`; unacknowledged
   assignments are re-delivered on every poll; de-duplicate by `matchId`.
4. `POST /v1/servers/{serverId}/assignments/{matchId}/ack` — accept (tickets are issued only after the ack; no ack within
   10 s aborts the allocation).

**Assignment CHANGED** (frozen match config): `{matchId, convoyId, serverId, kind:"campaign"|"freeplay"|"trial", mode,
stageId, stageNumber, stageType, courseId, freeplayMode, weather, collision:"light-contact"|"non-contact", carCapPi,
entrants[{accountId, displayName, role, carId, carPi, performanceHash, cosmeticHash, loadoutRevision, vehicleBuild, livery}],
aiEntrants[entrantIds, featured rival first], roster[{entrantId, kind:"human"|"ai", team:"player"|"opposing",
role:"driver"|"featured-rival"|"support-rival"|"friendly-ai"|"opposing-ai", driverId}], featuredRival,
guestPasses[{accountId, courseId, sponsorId}], sponsors{courseId:[accountIds]}, cupLegs[], trial{trialId,
kind:"mean"|"best"|"drift", difficulty, hardTimeoutMs, participationEnvelopeMs, victoryPlacement, defeatPlacement,
tiePolicy, provisional}, benchmark{kind, targetTimeMs, rawDriftTarget, hardTimeoutMs, provisional, source,
requiresBeatingFeaturedRival}, purePvP, gridNote, build, protocol, contentHash, seed, resultsUrl, ticketIssuer,
ticketAudience, resultsSecret}`. At most 12 vehicles (`entrants` + `aiEntrants`); an AI's `driverId` is a rival/profile
ID, never an account. `resultsSecret` is base64url (32 bytes); keep it in server memory only.

**`entrants[].vehicleBuild` NEW** (Addendum 02 §9–10): the entrant's APPLIED build frozen by the control plane at
`event.start` (server-resolved from the stored workspace; never a draft, preview or client claim). `carPi`/`performanceHash`
are its values. `{instanceId, carId, appliedRevision, buildHash, pi, piClass, piIsEstimate, handlingModelVersion ("hm-1"),
partsCatalogueRevision, partsCatalogueHash (= /healthz garageContentHash), parts{slotId: partId} (absent slot = stock),
utilityPartId, tuningVersion, tuning{key: int} (absent key = the installed part's default), utility{partId, incomePercent
(0/4/8), showcasePercent (0/5/10)}, paramsMicro{SimParam: value × 10⁶} (every resolved simulation input; the exact integers
`buildHash` covers), chassis{finalDriveScale, gearSpreadScale, shiftSecondsScale, brakeForceScale, brakeFrontBias,
springScaleFront/Rear, damperScaleFront/Rear, antiRollScaleFront/Rear, cgHeightOffsetM, restLengthOffsetM,
maxCompressionOffsetM, peakSlipDeg, slideGripFraction, slideFalloffDeg, powerSlideGripLoss, aeroFrontShare}}`. To build the
same `VehicleParams`: resolve `parts` + `tuning` with Core `BuildResolver.Resolve` over the same `parts.json` (check
`spec.BuildHash == buildHash`), then `VehicleFactory.Build(spec.Car, spec.Tuning, assists)` and apply `spec.Chassis`
(= `chassis` here). Absent (null) only for allocations made before builds were frozen. Unity-side consumption is not
implemented yet.

**`entrants[].livery` NEW** (visual only): the entrant's APPLIED livery frozen at `event.start` from the same stored
workspace as `vehicleBuild`, as a STRING holding the compact Core `LiveryWire` form (a JSON array text, version 1,
≤ 5,120 bytes; decode with `LiveryWire.Decode`), or `null` for the stock appearance. `cosmeticHash` is then the server
`LiveryHash` of that livery (`LiveryHash.Of(LiveryWire.Decode(livery).Document)` reproduces it; for `null`, the hash of the
chassis' stock livery, `LiveryHash.Of(catalogue.StockLivery(carId))`). A stored livery that no longer validates against the
server's `customization.json` is sent as `null` (stock). Resolve it against the receiver's own catalogue before rendering.

**Last Race Build.** When the game server acknowledges the assignment and a racer ticket is issued, the control plane
records each such entrant's frozen `vehicleBuild` as that car's protected `last-race-build` reference (Core
`GarageOperations.RecordRaceBegan`, context = matchId). A failed or unacknowledged allocation, a Test Yard run or a toy
never records it. (The control plane has no per-entrant "began driving" signal yet, so a load failure after the ack still
records it.)

## 6. Result submission

`POST /v1/matches/{matchId}/results` with headers `X-NightSignal-Server-Key` and
`X-NightSignal-Signature: sha256=<lowercase hex HMAC-SHA256(key = base64url_decode(resultsSecret), message = the exact raw body bytes)>`.
Sign the bytes you send; do not re-serialize after signing. Body (unknown members are rejected — clients and
servers can never send money, RP, clears, course access or cues):

```json
{
  "matchId": "m_…", "contentHash": "…", "aborted": false, "abortReason": null,
  "entrants": [
    { "entrantId": "<accountId or AI entrant id>", "human": true, "outcome": "Finished",
      "finishTimeMicros": 170000000, "placement": 1, "clean": true,
      "checkpointFraction": 1.0, "activeProgressVerified": true, "activelyDroveLegalCourse": true,
      "legalProgressMetres": 3100.0, "rawDriftScore": 0, "contractsPassed": 0, "challengesCompleted": ["CH01"],
      "started": null }
  ]
}
```

- `outcome` ∈ Core `RunOutcome`: `Finished, DidNotFinish, Quit, DisqualifiedDisconnect, DisqualifiedAfk, DisqualifiedInvalid`.
- Every allocated human (DQs included — H is frozen) and every AI entrant (friendly and opposing) appears exactly once.
- `finishTimeMicros` must be the **adjusted legal** time including ordinary penalties (Team Trial MEAN/BEST use it).
- `placement` must equal Core `RaceClassification` over ALL entrants (1 ms precision, equal ms = tie; DNFs by legal
  progress; quits/DQs = 0). Drift formats (Drift Attack, TT_DRIFT) rank finishers by `rawDriftScore`.
- **`started` NEW (AI only, optional, default true):** `false` when the AI never spawned/initialised. Any live opponent that
  never started makes the event broken: the match is aborted (`200 {"status":"aborted","reason"}`, `match.aborted` to the
  convoy), never a free boss win. `started` on a human → 422.
- `challengesCompleted` are PERSONAL predicates the game server evaluated from that entrant's own metrics (a team sum can
  never complete one); ignored unless the entrant finished; AI may not claim any.
- `{"aborted": true, "abortReason": "…"}` marks a system failure: no results or progression (`match.aborted` sent).

Settlement (Core rules, one transaction, idempotent per match):

- **Encounter stages** (lieutenant, penultimate, finale; `benchmark.requiresBeatingFeaturedRival`): a human qualifies only
  if they meet the target AND beat the live featured rival — finished strictly ahead of it (a tie shares the placing and
  does not beat it), or the rival did not finish while the human finished. Normal needs ≥1 such human, Hard ceil(H/2).
- **Utility income CHANGED:** `payout.utilityX100` = 100 + the income utility of the entrant's FROZEN build
  (`vehicleBuild.utility.incomePercent`, 4 or 8; anything else or no build = 100), on ordinary event pay only.
- Placements 1–12 pay Core `Economy.PlacementX100` (4th–12th = 1.00). AI never receive transactions. The pure-PvP bonus
  never applies to AI-containing events, Team Trials or Time Attack.
- **Team Trials:** Core `TeamTrials.Score` per six-seat side (MEAN: sum of six contributions, a DNF/DQ counts hard timeout
  + 30 s; BEST: fastest legal finisher; DRIFT: summed raw scores, DQ = 0). Humans are paid as the declared placement
  (`victoryPlacement` on a team win, else `defeatPlacement`) — never ×6 or ×12; only active human finishers are paid, and in
  BEST only if some human finished within `participationEnvelopeMs` (otherwise `payout.note` explains). No first clear, no
  RP from the trial itself; team results update TEAM bests only.
- A valid **Normal** clear grants Core `CourseAccess.GrantedByNormalClear` courses (idempotent; a purchased course is kept,
  with a note, never refunded). **Soundtrack cues** are granted once per (account, cue) from the manifest's source events:
  a valid clear of the declared stage (Normal/lieutenant or Hard), or the first eligible Team Trial victory.

Responses: `200 {"status":"settled","receipts":[…]}` (retry of the identical body → `"replayed": true`),
`200 {"status":"aborted", "reason"?}`, 400 malformed/unknown member, 401 key/signature, 403 not your match, 404 unknown
match, 409 already settled with a different body / aborted, 413 > 64 KiB, 422 facts inconsistent with the allocation.

**Receipt CHANGED** (per human; also `GET /v1/matches/{id}/receipt`): `{matchId, accountId, status,
eventKind ("CampaignStage"|"FreeplaySprint"|…|"TeamTrial"), mode, stageId, courseId, outcome, placement, tied, finishTimeMs,
stage{qualified, withinSupport, earnedClear, teamSuccess, qualifiers, requiredQualifiers, frozenHumanCount, reason,
benchmarkTargetMs, benchmarkProvisional, benchmarkSource, requiresBeatingFeaturedRival, featuredRival, beatFeaturedRival},
payout{base, difficultyX100, placementX100, cleanlinessX100, utilityX100, pvpX100, eventCredits, firstClearBonus,
challengeCash, total, note}, credits[{type, requested, credited, clampedAway}], balanceAfter, clampedAwayTotal,
firstClearAwarded, challengesUnlocked[], cosmeticsGranted[], coursesUnlocked[], musicUnlocked[],
guestPass{courseId, sponsorId}|null, teamTrial{trialId, kind, difficulty, humans, friendlyAi, playerTeamValue,
opposingTeamValue, playerTeamMeanMs, verdict:"victory"|"defeat"|"tie", contributions[{entrantId, human, team, value}],
completionPayable, newTeamBest, recordCategory:"team", provisional}|null, rankPointsBefore, rankPointsAfter, rank, notes[]}`.

## 7. Authored data proposals and what is not covered yet

**Proposed authored files** (the control plane loads them from `content/authored/` when present; neither is part of Core's
`ContentHash` yet — see the report's Core recommendations):

- `team.trials.json`, schema `night-signal/team-trials@1`: `{schema, trials[{id, name, kind:"mean"|"best"|"drift", course
  (existing non-exclusive regular course), format:"sprint"|"circuit"|"drift-attack" (drift needs drift-attack), hardTimeoutMs,
  participationEnvelopeMs (≤ hardTimeoutMs), carCapPi, assists, tiePolicy, victoryPlacement, defeatPlacement, provisional,
  difficulties[{id, label, allyPool[≥5 rival IDs], opponentPool[≥6 rival IDs]}]}]}` — pools disjoint, never R40/R48 (Core
  `FinalRivals`). Until it exists a clearly flagged PROVISIONAL in-code fixture (TT_MEAN on C03, TT_BEST on C01, TT_DRIFT on
  C02) is used; thresholds are uncalibrated.
- `music.unlocks.json`, schema `night-signal/music-unlocks@1`: `{schema, cues[{cueId, source{kind:"baseline"|
  "stage-first-normal-clear"|"stage-first-hard-clear"|"lieutenant-first-defeat"|"trial-first-victory", stageId?, trialId?}}]}`
  — exactly one source per cue; lieutenant sources must name a lieutenant stage. Without the file nothing is granted.

Not covered here: browser (WSS) clients (Origin check, non-header token) — out of scope (spec §3.4); Addendum 02 toy
services (DowntimeSession, toy commands) — a separate follow-up that will attach to the `IConvoySessionObserver` extension
point (membership start/end with generation, preemption at mode entry/race allocation, dormant/restored/ended) keyed by
`convoySessionId`.

**Garage content hash (open).** `parts.json` and `build-recipes.json` are loaded by the control plane (§2.6) but are NOT
part of Core `ContentCatalogue.ContentHash` (not in `ContentCatalogue.AuthoredFiles`), so the client/server/ticket
`content` check does not cover them yet. Until Core and the Unity content hash change together, `/healthz`
`garageContentHash` and `vehicleBuild.partsCatalogueHash` carry their hash separately.

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
