# Night Signal — hosting

> Current scope: the **control plane** (`Services/`). Game-server/client build hosting is documented by the Unity side.

## Status at a glance

| Item | State |
|---|---|
| Control plane on this machine (Development, loopback, DevAuth, SQLite) | runs; automated tests + real-socket smoke test executed |
| PostgreSQL / Supabase store (`PostgresGameStore`, `Backend/migrations/postgres`, `Backend/postgres/rls.sql`) | **compiles, never executed** — no PostgreSQL server here |
| Local Supabase stack (Auth + Postgres) | **blocked** — needs Docker (or compatible) + Supabase CLI; installing either needs owner approval |
| Hosted Supabase project, public control-plane deployment, TLS certificate, firewall/router changes | **not approved / not done** |
| Internet (WAN) play | **blocked** until the above are approved and provisioned (spec §3.5) |

## Run locally (Windows, .NET 10 SDK)

```powershell
# from the repository root
powershell -ExecutionPolicy Bypass -File Tools/run/start-control-plane.ps1            # http://127.0.0.1:5080
powershell -ExecutionPolicy Bypass -File Tools/run/start-control-plane.ps1 -Port 5090 -NoBuild
```

The script forces `ASPNETCORE_ENVIRONMENT=Development` and `ASPNETCORE_URLS=http://127.0.0.1:<port>` and clears
variables that could widen the binding. Equivalent without the script:

```powershell
cd Services/ControlPlane
$env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'
dotnet run --no-launch-profile
```

(`dotnet run` with the launch profile also binds `http://127.0.0.1:5080`.)

What Development mode uses (`Services/ControlPlane/appsettings.Development.json`):

- **DevAuth** issuer with accounts from `Backend/seed/dev-accounts.example.json` (six synthetic `driverN@devauth.localhost`
  accounts; their dev-only passwords are in that file). DevAuth **refuses to start** unless the environment is
  Development and every configured/bound address is loopback — there is no override.
- **SQLite** at `Services/ControlPlane/.data/controlplane.db` (git-ignored). Delete the folder to reset.
- **Generated keys** in `Services/ControlPlane/.devkeys/` (git-ignored): `devauth-signing.pem`, `ticket-signing.pem`,
  `gameserver-dev.key` (the key a local Unity game server sends as `X-NightSignal-Server-Key`; server id `dev-local`).
- Content: the catalogue JSON is copied from `Assets/Content/Data/generated/` (required) and `authored/` (Core
  `OptionalFiles`, e.g. `cars.tuning.json`) at build time — the same document set the game bundles; `/healthz`
  reports its `contentHash`, which clients and game servers must present.

Quick check: `curl http://127.0.0.1:5080/healthz` → `{"status":"ok","contentHash":"…"}`.
Create more dev accounts: `echo <password> | dotnet run --project Services/ControlPlane --no-launch-profile -- hash-password`.

Tests: `dotnet test Services/NightSignal.Services.slnx` (in-process WebApplicationFactory + real SQLite files in the
temp folder; no network, no Docker).

## Configuration reference (production shape)

`appsettings.json` holds no secrets. Supply the rest through environment variables (`Section__Key`) or the host's
secret store:

| Setting | Purpose |
|---|---|
| `Identity__Issuer` | Supabase issuer, e.g. `https://<project-ref>.supabase.co/auth/v1` |
| `Identity__JwksUrl` | `https://<project-ref>.supabase.co/auth/v1/.well-known/jwks.json` (https required) |
| `Identity__Audience` | `authenticated` (default) |
| `Storage__Provider` / `Storage__PostgresConnectionString` | `Postgres` + service-role connection string (secret) |
| `Storage__ApplyMigrations` | `false` if migrations are applied with the Supabase CLI (recommended) |
| `Tickets__SigningKeyFile` | path to an EC P-256 private key PEM (secret; startup fails without it outside Development). Create with `openssl ecparam -name prime256v1 -genkey -noout -out ticket.pem` |
| `GameServers__Credentials__0__Id` / `__KeySha256` | server id + lowercase hex SHA-256 of that server's key (the key itself goes only to the game server) |
| `Compatibility__Protocol` | protocol version clients/servers must present (1) |
| `ASPNETCORE_URLS` + TLS | serve HTTPS/WSS (Kestrel certificate or a TLS-terminating reverse proxy) |

Supabase project prerequisites: **asymmetric JWT signing keys** (ES256/RS256 — legacy HS256 secrets are rejected by
design), email/password auth with verification and reset URLs configured (spec §3.2a), migrations then `rls.sql`
applied, and the control plane connecting as a role that bypasses RLS (service role / owner). Clients get only the
public anon key and read-only own-row access.

## Blocked / needs owner approval

1. **Local Supabase** (`supabase start`) requires Docker Desktop (or compatible) and the Supabase CLI — not installed,
   installation needs approval. Until then the local stack is control plane + SQLite + DevAuth, and the Postgres path
   is untested.
2. **Hosted Supabase project and any public deployment** of the control plane or game servers (cost, TLS, DNS,
   firewall) — not approved; nothing has been deployed or exposed beyond loopback.
3. **Operations limits** to revisit before hosting: convoy state is in memory (one instance; a restart drops convoys,
   not ledgers); no HTTP rate limiting beyond invite-code attempts and the 15 s ready-request limit.
