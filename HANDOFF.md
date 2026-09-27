# Handoff

_Last updated: 2026-09-26 — setup checkpoint (Gate 0)._

## Where things are

- Branch `dev/night-signal` (tracks `origin/dev/night-signal`). Never merge to `main` without approval.
- Unity 6000.6.3f1 editor open on this checkout, MCP for Unity 10.0.0 connected over loopback HTTP.
- Setup done: baseline commit/push, URP Graphics default (D-002), AI/editor-control packages removed (D-003),
  verification scene, EditMode + PlayMode tests, Windows development player built and launched.

## Blockers needing the owner

1. **Local backend stack** — Supabase CLI and a container runtime (Docker Desktop or compatible) are not
   installed. Required for the real local Auth/PostgreSQL environment (§3.2). Needs approval to install.
2. **Server build modules** — Unity Hub modules "Linux Dedicated Server Build Support" (for the Linux server
   target) and optionally "Windows Dedicated Server Build Support". Needs approval to install. Meanwhile the
   authoritative server runs as a headless Windows player process.
3. **Hosted services / internet test** — a Supabase project, a reachable game/control server and a budget
   are needed for WAN acceptance (§3.5, Gate 5). Not approved; stays BLOCKED, not faked.

## Next actions (Gate 1 spine)

1. Engine-free rules library (`Assets/Game/Core`) with unit tests: economy, RP, frontier/team success,
   drift scoring curves, PI classes, entrant-cap rules.
2. Typed content import from `docs/brief/Night_Signal_Content_Catalogue.json` + validator/coverage report.
3. Vehicle simulation (fixed 60 Hz raycast chassis) + input + cameras; first authored course section.
4. Netcode for GameObjects + Unity Transport: headless authoritative server + two independent clients.
5. ASP.NET Core control plane (convoy, readiness, tickets, result ledger) — local-only until approvals.

## Recovery notes

- On resume: check `git status`, `git log -1`, `git ls-remote origin refs/heads/dev/night-signal`,
  `mcpforunity://instances`/`project/info`, and running Unity/player processes before retrying anything.
- Build outputs live in `Builds/` (ignored). Evidence worth keeping is copied to `Evidence/`.
