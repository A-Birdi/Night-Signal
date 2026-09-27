# Night Signal — architecture

> Scope of this file today: the **control plane** (spec §3.2, §3.3, §4, §10, §11). Game-client, authoritative
> game-server and content-pipeline sections are owned by the Unity side and are added there.

## Topology (spec §3.2)

```
 Unity client ──HTTPS REST────────────┐
   (menus)    ──WSS /v1/control──────┐│        Supabase Auth (prod)  /  DevAuth (dev, loopback only)
              │                      ││               │ JWKS (public keys only)
              │  ticket (≤60 s, 1×)  ▼▼               ▼
              │               ┌─────────────────────────────────┐   service role   ┌──────────────┐
              │               │ Control plane (ASP.NET Core)    │─────────────────▶│ PostgreSQL   │
              │               │  Identity · PlayerStore ·       │  (SQLite locally)│ (Supabase)   │
              │               │  ConvoyDirectory · MatchAllocator│◀─────────────────│  RLS: clients│
              │               │  · ResultLedger · ContentCatalogue│                 │  read own rows│
              │               └──────▲──────────────┬───────────┘                  └──────────────┘
              │     register/heartbeat│  long-poll   │ assignment (frozen config + per-match HMAC secret)
              │     results (key+HMAC)│              ▼
              └──── NGO / Unity Transport ────▶ Authoritative game server (Unity headless)
                    (ticket presented on connect;  validates tickets offline with the ticket JWKS)
```

The **leader is not authority** (spec §3.3): the leader only proposes and presses Start. Wallets, verdicts,
race clock and results belong to the game server (facts) and the control plane (rules + ledger).

## Components (`Services/`)

| Spec interface (§3.2) | Implementation | Notes |
|---|---|---|
| Identity | `Identity/IdentitySetup.cs` (JwtBearer), `DevAuth/*` | One validation path; only issuer + JWKS source differ between Supabase and DevAuth. |
| PlayerStore | `Persistence/Stores.cs` `IPlayerStore` → `SqlGameStore` | Card (versioned), wallet, owned cars, clears, challenges, cosmetics. |
| ConvoyDirectory | `Convoys/ConvoyDirectory.cs` | In-memory, single lock, monotonic revisions, injectable clock. Transport-free. |
| MatchAllocator | `Matches/MatchAllocator.cs`, `GameServerRegistry.cs`, `Tickets.cs`, `MatchWatchdog.cs` | Long-poll assignment, ack before tickets, abort on server loss. |
| ResultLedger | `IResultLedger` → `SqlGameStore`, `Matches/SettlementService.cs` | One DB transaction per settlement; idempotent. |
| ContentCatalogue | `Content/ContentService.cs` over **NightSignal.Core** `ContentCatalogue` | Same documents as the game (Core `RequiredFiles` + present `OptionalFiles`), so `ContentHash` matches; startup fails if `CatalogueValidator` reports errors. |
| GameplayTransport | not in the control plane | NGO + Unity Transport (Unity side). |

Shared rules: `Services/SharedCore/NightSignal.Core.csproj` compiles `Assets/Game/Core/**/*.cs` **by link**
(netstandard2.1, C# 9). Economy, Wallet, RankPoints, CampaignProgress, StageOutcome, GridPlanner,
RaceClassification and PerformanceIndex are the exact code the Unity game runs; the control plane never
re-implements them.

`Services/TicketValidation/` is a dependency-light netstandard2.1 validator for the game server (see NETWORKING.md).

## Trust boundaries

1. **Client → control plane.** Only a verified access token identifies the caller (`sub` = account ID; display
   names are never identity). Clients can request: card edits, starter choice, purchases (price from the
   catalogue), convoy actions, readiness. They can never submit money, RP, clears, finish times or placements.
2. **Game server → control plane.** Authenticated by a server key (hash-configured) — never a player token.
   Result bodies are additionally HMAC-signed with a per-match secret that only the allocated server received.
   The server reports *facts*; the control plane recomputes placement (Core `RaceClassification`, cross-checked),
   stage verdicts (Core `StageOutcome`), payouts (Core `Economy`, trusted `ExpectedSeconds`), first clears
   (Core `CampaignProgress.ApplyClear` + UNIQUE constraint) and RP (Core `RankPoints`).
3. **Control plane → game server (via client).** Single-use ES256 match tickets bound to account, convoy, match,
   role, build, protocol and content hash; ≤ 60 s. The game server verifies them offline with the public JWKS.
4. **Control plane → database.** Service-role connection only. Clients hold at most read-only, own-row access
   through RLS (`Backend/postgres/rls.sql`); every write is a server transaction.

## Where secrets live

| Secret | Production | Local development | Never in |
|---|---|---|---|
| Supabase JWT signing keys | Supabase (asymmetric; control plane only reads the public JWKS) | DevAuth ES256 key generated into `Services/ControlPlane/.devkeys/devauth-signing.pem` (git-ignored) | repo, client |
| Ticket signing key (ES256) | PEM file from the host's secret store, `Tickets:SigningKeyFile` (startup fails without it) | generated `.devkeys/ticket-signing.pem` | repo, client |
| Game-server keys | host secret store → server process; control plane config holds only SHA-256 hashes (`GameServers:Credentials`) | generated `.devkeys/gameserver-dev.key` (id `dev-local`) | repo, client |
| Per-match results secret | generated per allocation, stored in `matches.results_secret` (no client grant), delivered once to the game server | same (SQLite) | client, logs |
| Database password / service role | `Storage:PostgresConnectionString` via environment/secret store | none (SQLite file in `.data/`) | repo, client |
| Dev account passwords | n/a (Supabase handles passwords) | `Backend/seed/dev-accounts.example.json` (synthetic, dev-only; the service reads only the PBKDF2 hash) | production |

Logs: JSON console lines through `RedactingJsonFormatter` (tokens, `password`, `secret`, `key`, JWT-shaped strings
redacted); the code logs IDs and counts only. The end-to-end test asserts no token, password, server key, ticket
or results secret appears in the captured logs.

## Data model and consistency

Migrations: `Backend/migrations/{sqlite,postgres}/0001_initial.sql` (equivalent; application SQL is shared).

- `accounts(account_id = JWT sub)`, `player_cards(revision)`, `wallets(balance CHECK 0..9,999,999)`.
- `ledger_entries` — append-only (UPDATE/DELETE blocked by triggers), `UNIQUE(idempotency_key)`;
  keys: `<matchId>/<accountId>/<event|first-clear|challenge:CHxx>`, `purchase/<accountId>/<clientKey>`,
  `starter/<accountId>`. Each row records requested, applied and clamped amounts plus the balance after.
- `owned_cars`, `stage_clears UNIQUE(account, mode, stage)`, `challenge_unlocks UNIQUE(account, challenge)`,
  `cosmetics_owned`, `matches` (frozen config, per-match secret, state allocated|settled|aborted, result-body hash),
  `match_results` (one stored receipt per account per match).

Guarantees: a settlement (money + first clear + challenges + cosmetics + receipts + match state) is one transaction.
Writers serialize per wallet (SQLite `BEGIN IMMEDIATE`; PostgreSQL `SELECT … FOR UPDATE`, lost UNIQUE races are
re-run). Replaying a result body returns the stored receipts; a different body for a settled match is a 409.
Wallet credits use Core `Wallet.Credit` (clamp at 9,999,999, clamped amount recorded).

## Convoy state (spec §4)

Presence (`InMenus, AtMeet, LoadingRace, InRace, Spectating` client-set; `Reconnecting, Offline` server-set) is
separate from the convoy phase (`Idle → DestinationCheck → EventSelection → ReadyCheck → Allocating → InMatch`).
Loading/intro/countdown/racing/results are the game server's; the control plane sees them as `InMatch` + presence.
Every change bumps the convoy revision and pushes a snapshot. Rules and timers are listed in NETWORKING.md.

## Known limits (see also HOSTING.md)

- Stage benchmarks are **provisional** (derived from course `expectedSeconds`, labelled in proposals and receipts)
  because the content catalogue has no certified reference runs yet (spec §2.5).
- Utility items, parts, loaners, cosmetic purchases and tutorial completion credits are not modelled yet.
- Challenge predicates are evaluated by the game server; the control plane validates IDs, once-only and payout.
- Convoy state is in memory (single control-plane instance); a restart drops convoys (players re-form them;
  the ledger and receipts are durable).
