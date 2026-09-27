# Unity setup

## Editor and modules (observed 2026-09-26)

| Item | Value | Source |
|---|---|---|
| Editor | 6000.6.3f1 (45d8eee7de74) — approved baseline, see D-001 | running editor + `ProjectVersion.txt` |
| Hub | Unity Hub 3.21.3 | process list |
| Installed build support | Windows x64/x86/ARM64 **Mono** player, WebGL | `PlaybackEngines/` |
| Not installed | Windows Dedicated Server, Linux (player/server), Windows IL2CPP | `PlaybackEngines/` |
| Render pipeline | URP 17.6.0; Graphics default + PC quality = `Assets/Settings/PC_RPAsset.asset`; Mobile quality = `Mobile_RPAsset` | editor API |
| Colour space | Linear | editor API |
| Editor bridge | MCP for Unity 10.0.0 (Git tag `v10.0.0`), HTTP transport, loopback only | `mcpforunity://instances` |

The Linux dedicated-server target in the specification needs the "Linux Dedicated Server Build Support"
module (install through Unity Hub — requires owner approval). Until then the authoritative server runs as a
separate headless Windows process (`-batchmode -nographics`).

## Installed packages (direct)

MCP for Unity 10.0.0 · AI Navigation 2.0.14 · Unity Version Control 2.13.6 · Rider 3.0.38 ·
Visual Studio 2.0.26 · Input System 1.20.0 · URP 17.6.0 · Test Framework 1.8.0 · Timeline 6.6.0 ·
uGUI 2.6.0 · Visual Scripting 1.9.12 · built-in modules. Removed in setup: see D-003.

## Assemblies

| Assembly | Folder | Notes |
|---|---|---|
| `NightSignal.Runtime` | `Assets/Game/Runtime` | gameplay/runtime code |
| `NightSignal.Editor` | `Assets/Game/Editor` | editor tools, `BuildCommands` |
| `NightSignal.Tests.EditMode` | `Assets/Tests/EditMode` | baseline guards + rules tests |
| `NightSignal.Tests.PlayMode` | `Assets/Tests/PlayMode` | runtime tests |

## Verification procedure

1. EditMode tests: `NightSignal.Tests.EditMode` (MCP `run_tests`, or Test Runner window).
2. PlayMode tests: `NightSignal.Tests.PlayMode`.
3. Smoke build: `BuildCommands.BuildSetupSmoke()` → `Builds/SetupSmoke/NightSignalSmoke.exe`.
4. Launch: `NightSignalSmoke.exe -nsSmokeTest -nsSmokeOut <dir> -nsSmokeFrames 180 -logFile <dir>/player.log
   -screen-fullscreen 0 -screen-width 1280 -screen-height 720`. Exit code 0 means frames rendered with a
   non-blank back buffer; `<dir>/player-smoke.json` and `player-smoke.png` are the evidence.

## Known console entries

- Player builds log two errors: `Hidden/Core/DebugOccluder` and `Hidden/Core/DebugOcclusionTest` "All
  SubShaders were stripped". These are render-pipelines-core GPU-occlusion debug shaders; the build still
  succeeds (summary: 0 errors). Not yet investigated further.
- The smoke player logs that URP post-processing shaders were stripped; the smoke scene has no volume.
  Race scenes that use post-processing must keep those shaders (verify in their builds).
