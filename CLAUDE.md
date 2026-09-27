# Night Signal: Mountain Circuit — agent instructions

Original full-3D multiplayer arcade racing game (Unity, Windows-first) with an authoritative game server,
an ASP.NET Core control plane, and Supabase Auth/PostgreSQL.

## Read first

1. `SPECIFICATION.md` — the master specification, verbatim (sha256 `5e2d0141…d149`). Authoritative.
   **Revised by `docs/brief/Night_Signal_Addendum_01.txt`** (Revision 1): where they conflict the addendum wins —
   see `docs/EFFECTIVE_RULES.md` for the decisions D01–D10, the superseded rules and the impact map.
2. `docs/DECISIONS.md` — approved decisions that refine/override the specification
   (notably **D-001: editor baseline is Unity 6000.6.3f1, not 6.3 LTS**).
3. `HANDOFF.md` — current state, blockers, next action. Update it at every checkpoint.
4. `REQUIREMENTS.md` (ledger), `VALIDATION.md` (what was actually executed, at which revision).
5. `docs/brief/` — the rest of the original prompt pack (content catalogue JSON, setup guide, launch and
   resume prompts, setup approval with personal paths redacted).

## Ground rules

- Branch `dev/night-signal` only. No merges into `main`, no force-push, no branch deletion, no worktrees.
- Checkpoint often: commit, `git push`, then verify `git rev-parse HEAD` equals
  `git ls-remote origin refs/heads/dev/night-signal`. Keep ≤10–15 minutes of substantive unpushed work.
- One Unity writer. The editor is open on this checkout and driven through MCP for Unity 10.0.0.
  Never start a second editor (including `-batchmode`) against this project while it is open.
- Keep personal absolute paths, credentials and account details out of commits, logs and evidence.
  Service-role keys, DB passwords, signing keys never go in the client or the repository.
- No purchases, paid services, public deployment, system installs, firewall changes without owner approval.
- Honest evidence only: say what was executed (and at which revision) versus inspected or simulated.

## Layout (D-004)

- Unity project at the repo root. `Assets/Game/Core` = engine-free shared rules (`noEngineReferences`),
  `Assets/Game/Runtime` = gameplay/runtime, `Assets/Game/Editor` = tools/builds,
  `Assets/Tests/{EditMode,PlayMode,Verification}`, `Assets/Content`, `Assets/Art`, `Assets/Audio`, `Assets/UI`.
- Outside Unity: `Services/` (control plane), `Backend/` (migrations, seed), `Tools/`, `docs/`, `Evidence/`.
- `Builds/` is git-ignored build output.

## Working with the editor bridge

- After script changes: refresh/compile, wait until `editor_state.advice.ready_for_tools`, then `read_console`.
- `execute_code` compiles with CodeDom (C# 6): no local functions, no tuples, no `is` patterns.
- Package add/remove triggers a domain reload; wait with `refresh_unity(wait_for_ready=true)`.
- Build: `NightSignal.Editor.Build.BuildCommands` or `manage_build`; output under `Builds/`.
- Git Bash rewrites Windows-style paths inside command arguments (MSYS conversion); avoid literal
  `C:\…` strings in inline scripts.
