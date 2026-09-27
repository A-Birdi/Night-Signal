# Night Signal — project decisions

Decisions that refine or override `SPECIFICATION.md`. The specification is kept verbatim;
where a decision below conflicts with it, the decision wins and says so explicitly.
Newest last. Each entry names who decided and why.

## D-001 — Unity editor baseline is 6000.6.3f1 (overrides §3.1 "Unity 6.3 LTS")

- Date: 2026-09-26. Decided by: project owner (written approval, `docs/brief/06_Setup_Approval_2026-09-26.txt`).
- The project was created with, and is open in, Unity **6000.6.3f1** (revision 45d8eee7de74), the only editor
  installed on the development machine. `ProjectSettings/ProjectVersion.txt` pins that exact version.
- Specification §3.1 proposed Unity 6.3 LTS (6000.3.x) and requires a recorded reason for any other Unity 6
  release. Reason: owner decision to keep the working, already-connected editor rather than recreate the
  project on another stream; 6000.6.3f1 is a final (`f`) release, not an alpha/beta. The support tier of the
  6000.6 stream was not independently verified during setup.
- Consequences: package versions follow what this editor resolves (URP 17.6.0, Input System 1.20.0,
  Test Framework 1.8.0, Timeline 6.6.0, uGUI 2.6.0). New packages are verified against this editor when
  introduced. Do not downgrade, upgrade automatically, or replace core packages to match older examples.
- Guarded by `SetupVerificationTests.EditorVersion_MatchesPinnedProjectVersion`.

## D-002 — Graphics default render pipeline is `Assets/Settings/PC_RPAsset.asset`

- Date: 2026-09-26. Decided by: project owner.
- Before: Graphics Settings had no default pipeline; URP was active only through the Mobile/PC quality-level
  overrides, so a new quality level without an override would silently fall back to the Built-in renderer.
- Now: `GraphicsSettings.defaultRenderPipeline` = PC_RPAsset (set through the Unity API, not by editing GUIDs).
  Mobile → `Mobile_RPAsset`, PC → `PC_RPAsset` overrides, the PC renderer's SSAO feature and Linear colour
  space are unchanged.
- Guarded by `SetupVerificationTests.GraphicsDefault_IsThePcUrpAsset` and `EveryQualityLevel_OverridesWithAUrpAsset`.

## D-003 — Optional Unity AI / editor-control packages removed

- Date: 2026-09-26. Decided by: project owner (no Unity AI subscription wanted); dependency check by Claude.
- Removed through Package Manager after confirming no dependents and no project references:
  - `com.unity.ai.assistant` 2.20.0-pre.1 — generative editor assistant; logged `NoSubscription` exceptions.
  - `com.unity.ai.inference` (Sentis) 2.6.1 — neural-network runtime, unused; it was the only reason
    `com.unity.dt.app-ui` was installed.
  - `com.unity.pipeline` 0.8.0-exp.1 — experimental external editor-control endpoint, unused and redundant
    with the MCP for Unity bridge.
- Package Manager then dropped their orphaned dependencies (`com.unity.dt.app-ui`, `com.unity.2d.sprite`,
  `com.unity.mathematics`). Their leftovers were cleaned: scripting defines `SENTIS_ANALYTICS_ENABLED` and
  `APP_UI_EDITOR_ONLY`, the dangling `com.unity.dt.app-ui` config object, and the Assistant's
  `ProjectSettings/Packages/com.unity.ai.assistant/Settings.json`.
- Kept deliberately: `com.unity.ai.navigation` (NavMesh; not a subscription product), MCP for Unity 10.0.0,
  URP and its core/shadergraph dependencies, Input System, Test Framework. `com.unity.collab-proxy` and
  `com.unity.visualscripting` were not in scope for this cleanup and remain installed.

## D-004 — Unity project stays at the repository root

- The setup guide suggested `Game/Assets`. Moving the project would break the connected editor and the MCP
  bridge binding, and the owner asked that the project not be moved or recreated.
- Layout: Unity project at the root (`Assets/`, `Packages/`, `ProjectSettings/`); sibling folders hold
  non-Unity work: `Services/` (control plane), `Backend/` (migrations/seed), `Tools/`, `docs/`, `Evidence/`.
- Inside `Assets/`: `Game/Runtime`, `Game/Editor`, `Game/Core` (engine-free shared rules), `Content`, `Art`,
  `Audio`, `UI`, `Tests/{EditMode,PlayMode,Verification}`.

## D-005 — Branch and history policy

- Development happens on `dev/night-signal`, created from the setup HEAD in the original checkout.
  No worktrees, no merges into `main`, no force-pushes, no branch deletion without explicit approval.
- Commits in this repository use the repository-local identity already used by the initial commit
  (GitHub no-reply address), so no personal email is published.
- Every checkpoint push is verified by comparing `git rev-parse HEAD` with `git ls-remote` for the branch.

## D-006 — Content catalogue is imported, not hand-copied; Hard support pools derived by rule

- The brief's `Night_Signal_Content_Catalogue.json` (sha256 `45fb1a45…`) was compared field-by-field with the
  appendices of `SPECIFICATION.md` by `Tools/qa/compare-catalogue-spec.mjs`: courses, stages, cars, rivals and
  challenges all match (2026-09-26). The specification stays authoritative; rerun the script if either changes.
- `Tools/authoring/import-catalogue.mjs` generates `Assets/Content/Data/generated/*.json` deterministically
  (`--check` detects drift). Generated files are never hand-edited; authored additions (dialogue, conditions,
  tuning, predicates) live in `Assets/Content/Data/authored/` and are merged by ID.
- Gap in both sources: Hard-mode support pools. Rule `swap-rule-v1` (Appendix B wording "crew's remaining
  members and previously introduced rivals"): take the Normal support pool, replace the Hard lead with the
  Normal lead, never list the Hard lead as support. The validator rejects lieutenants/finals appearing as
  support before their featured stage and R48 anywhere before Hard S30.
- A catalogue row is data, not delivered content: coverage counts in `REQUIREMENTS.md` only move when the
  course/car/rival/challenge is playable and validated.

## D-007 — Course geometry is generated deterministically at load from versioned route recipes

- A first "bake everything to assets" pass for C01 produced 28 MB of text-serialized meshes/terrain for one
  course (13 MB terrain alone) — roughly 0.7 GB for 26 courses, unsuitable for the repository and LFS quota.
- Now: `Assets/Content/Courses/<ID>/route.json` (control points, widths, banking, sectors, gates, landmarks) plus
  the kit recipes in `Assets/Game/Runtime/Track/Generation/` are the versioned source. `CourseRuntime` generates
  track data, road/shoulders/markings/guardrails, terrain and landmarks on load (C01: 1.9 s in the editor).
  The same text always yields the same geometry (no geometry change between launches, spec §3.1); the route's
  SHA-256 (`TrackData.SourceHash`) is part of the content identity checked between server and clients.
- Players generate full visuals; the dedicated server (batch mode) generates collision only.
- Committed per course: route.json, a ~9 KB scene (entry point, sun, sky material, volume profile).
  Shared: generated texture PNGs (LFS) and material/terrain-layer assets.
- Consequence: any change to a generator recipe changes course geometry, so recipe changes must bump the
  affected course revisions and re-validate benchmarks/ghosts (spec §8 compatibility headers).
