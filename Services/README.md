# Services — Night Signal control plane

ASP.NET Core (.NET 10) control plane for convoys, readiness, match allocation, tickets and the reward ledger
(spec §3.2). Everything here builds and tests without Unity, Docker or a database server.

| Project | What |
|---|---|
| `SharedCore/NightSignal.Core.csproj` | netstandard2.1 / C# 9 build of `Assets/Game/Core/**/*.cs` (linked, not copied) — the game's own rules. Do not add files here. |
| `TicketValidation/` | netstandard2.1 match-ticket validator for the authoritative game server (ES256, single-use `jti`). |
| `ControlPlane/` | The service: Identity (JwtBearer; DevAuth in Development), PlayerStore/ResultLedger (SQLite + PostgreSQL), ConvoyDirectory, MatchAllocator, settlement, `/v1/control` WebSocket. |
| `Tests/` | xUnit: unit tests plus WebApplicationFactory integration and end-to-end tests. |

```powershell
dotnet build Services/NightSignal.Services.slnx
dotnet test  Services/NightSignal.Services.slnx
powershell -ExecutionPolicy Bypass -File Tools/run/start-control-plane.ps1   # Development, http://127.0.0.1:5080
```

Docs: `docs/ARCHITECTURE.md` (components, trust boundaries, secrets), `docs/NETWORKING.md` (REST, control-channel
messages, tickets, game-server protocol, result HMAC), `docs/HOSTING.md` (local run, configuration, what is blocked).
Schema: `Backend/migrations/{sqlite,postgres}`, Supabase RLS: `Backend/postgres/rls.sql`,
dev accounts: `Backend/seed/dev-accounts.example.json` (synthetic, Development only).

Git-ignored at runtime (see `Services/.gitignore`): `bin/`, `obj/`, `ControlPlane/.devkeys/` (generated dev keys),
`ControlPlane/.data/` (SQLite). This folder re-includes `*.csproj`/`*.slnx`, which the Unity root `.gitignore` ignores.

Unity game-server integration notes:

- Ticket validation: reference or port `TicketValidation/TicketValidator.cs` (needs Newtonsoft.Json, already a Core
  dependency). It uses `System.Security.Cryptography.ECDsa`; verify that the Unity player/server runtime (Mono or
  IL2CPP) supports `ECDsa.Create(ECParameters)` + `VerifyData` before relying on it — not verified here.
- The results HMAC signs the exact bytes sent; see `docs/NETWORKING.md` §6.
