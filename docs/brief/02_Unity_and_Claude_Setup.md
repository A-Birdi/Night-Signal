# Night Signal — Unity and Claude setup
## Practical Windows workflow • researched 26 September 2026

This document is setup guidance, not evidence that the tools are already installed or that a game has been built. The example path below is not a claim that the folder exists. Keep this project separate from The Road of Borrowed Names and all other projects.

## Recommended arrangement

Use Claude Code locally on the Windows computer where Unity is installed. Give it the new project repository, connect an editor bridge, and keep Unity available for scene creation, compilation, tests, screenshots, profiling, and builds. Use a dedicated multiplayer server process for the game itself; the development bridge and game server are different systems.

My engine baseline is Unity 6.3 LTS with Universal Render Pipeline. Unity identifies 6.3 as its latest LTS and supports it through December 2027. This is a deliberate version-stability choice, not a claim that Unity officially recommends LTS for every new project: its documentation also recommends supported Update releases for new/mid-production work. Inspect the actual installed versions, choose a supported patch, check package compatibility, and pin it. Do not upgrade an unrelated project. [S1]

Claude Code's Local and Cloud options are execution environments. A Cloud session cannot inspect your local editor merely because the prompt contains a Windows path. Use Local for the editor-connected work. The desktop Code interface and CLI share MCP configuration; use the actual project/worktree that the editor has opened. Matching folder names alone does not establish this. [S2]

## 1. Create an isolated project and repository

A simple proposed layout is:

```text
C:\Dev\NightSignal\
  Game\                    Unity project: Assets, Packages, ProjectSettings
  Services\ControlPlane\  Account/convoy/results control service
  Backend\                 Migrations and local development configuration
  Tools\                   Build, validation and test launch scripts
  Docs\                    Architecture, content and evidence
  Prompts\                 Master brief and authoring catalogue
```

These directory names are a proposed organization, not an instruction to manually create empty Unity internals. Create the real Unity project through Hub/the supported editor workflow. Choose the Universal 3D/URP template appropriate to the pinned editor version. Install only the build modules needed for the Windows client and Windows/Linux server targets, with your approval. Unity's Dedicated Server platform is a separate optimized build target, not a demand to buy dedicated physical hardware. [S3]

Initialize a NEW repository with a README and a real initial commit. Make it private during development unless you deliberately prefer public. Set a Unity-appropriate .gitignore before adding files. Commit source, Assets and their .meta files, Packages and locks, and ProjectSettings. Do not commit Library, Temp, credentials, player data, or downloaded dependencies. Large original assets need a deliberate Git LFS or release-artifact policy with an actual clean-checkout test; pointers without accessible backing files are not preserved art.

Open Claude Code in this repository root. Confirm that its editor bridge is operating on the Game subfolder, not a different Unity window or an isolated worktree that contains older files. Use one Unity instance/writer per project checkout. Parallel workers can edit assigned code/data, but should not independently overwrite scenes, prefabs, import settings, or GUIDs.

## 2. Connect MCP for Unity

A practical community bridge is **CoplayDev's MCP for Unity**. It is MIT-licensed and is not affiliated with Unity Technologies. Its architecture connects an MCP-capable client to a Python server and the Unity editor package. Installing it is a real tool permission decision, not an automatic step hidden inside the game prompt. [S4]

The researched release baseline is **v10.0.0**. In Unity Package Manager, choose Add package from Git URL and use the release-pinned form below rather than following a moving main/beta branch. Check the actual release/tag still exists before installing; a newer release requires a deliberate compatibility decision rather than blindly replacing a working package. [S4]

```text
https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.0.0
```

The installer requires Git on PATH and Python 3.10+ with uv. Its setup wizard can help identify/configure missing prerequisites. After importing, open **Window → MCP for Unity**, inspect dependencies, select **Claude Code** in the client configurator, and start the connection. Its status must actually show a working connection. The documented HTTP default is `http://localhost:8080/mcp`; use the endpoint shown in your own editor, especially if that port is occupied. [S5]

Do not confuse the desktop app's ordinary Chat client configuration with Claude Code's configuration. Prefer the bridge's Claude Code configurator; verify the tools are visible in the Code session rather than assuming that another tab's configuration applies.

### Manual Claude Code configuration, when needed

The following is for the Claude Code CLI installed and signed into your normal account. Change the example directory to your actual repository. First inspect existing entries so you do not create duplicates or replace an unrelated server. The `local` scope means this project's private local configuration, not a public server. [S6]

```powershell
Set-Location -LiteralPath "C:\Dev\NightSignal"
claude mcp list
# Only if unityMCP is not already correctly configured:
claude mcp add --transport http --scope local unityMCP http://localhost:8080/mcp
claude mcp get unityMCP
```

Start/reopen the Code session in that root and inspect `/mcp` where supported. Permit the editor tools deliberately. Do not disable every permission or use a bypass mode to make a connection work. Keep the bridge on loopback/local transport; do not open it to the internet, add a port-forward, or publish credentials.

### Verify editor access before launching the full assignment

Send this small bootstrap request first:

```text
Verify the Unity connection for this NEW racing project before building.
Read the actual active editor version, project path, active scene, and
compile/console status through the available Unity tools. Confirm they
match the project selected in Claude Code.

Do not modify or discard an existing dirty scene. In an isolated scratch
scene owned by this verification, create a camera, light and a temporary
primitive, capture an actual editor/Game-view image, and demonstrate a
real script compile or a small EditMode test. Remove only the disposable
objects/files you created, or retain them clearly under test fixtures.

Report tool results, actual paths and errors. Do not claim editor access
from filesystem access alone. Do not alter another Unity project, install
paid assets, publish anything, or weaken permissions. Stop and explain
any unresolved connection/version mismatch.
```

A screenshot/scene/test result returned by a real tool is meaningful. Text saying “Unity ready” without a tool result is not. The bridge exposes editor and testing operations, but capability names and availability should be discovered in the installed version. Some asset-generation tool groups use external providers/BYOK; those are not automatically free just because the editor bridge is open source. Leave paid asset-generation integrations disabled unless you approve their provider, credentials, and budget. [S7]

## 3. Provide backend access without sharing secrets publicly

The master brief chooses Supabase Auth and PostgreSQL for identity and durable player data, an ASP.NET Core control service for convoy/readiness and reward operations, and an authoritative Unity simulation server for live races. This is the proposed architecture, not an assertion that these services are already provisioned.

For development, prefer an isolated local Supabase stack. The provider documents its CLI workflow and Docker-compatible container requirement. Get your approval before installing Docker, enabling virtualization, or changing system networking. Pin development tools instead of depending on whatever version happens to be latest. Local development mail is not evidence that production account emails were sent. [S8]

An example provider-documented CLI sequence, to be adapted to the chosen repository and pinned dependency policy, is:

```text
npm install supabase --save-dev
npx supabase init
npx supabase start
```

The real project must supply scripts/configuration, migrations and test accounts, not leave you to guess the pieces. Keep local ports limited to the intended machine/test network. Do not expose database/admin dashboards publicly.

For hosted authentication, you will need to create/approve the project and supply its public endpoint and permitted public client key separately from its secrets. Administrative/service-role/database credentials belong only in protected server-side configuration. Claude should tell you exactly which secret is needed, why, and where to put it; never place one in a game build, screenshot, public repository, or shared Player Card. Supported Auth flows and JWT verification should be used rather than handwritten password handling or treating decoded JWT text as proof of identity. [S9, S10]

Before real internet testing, approve a game/control server deployment and a budget. The development machine can run the services, but using it as the internet host requires an explicitly configured reachable network path. Do not assume private local addresses are reachable by friends. A public remote deployment requires suitable ports/TLS/authentication, operational monitoring, and cost controls. No provider is approved solely by this document.

## 4. Use the full assignment with an execution prompt

Place or attach **01_Night_Signal_Master_Specification.txt** and **Night_Signal_Content_Catalogue.json**. The JSON is a structured duplicate of the authored launch catalogue, not a game implementation. Then send **03_Claude_Launch_Prompt.txt**. The master is authoritative if a duplicate contradicts it; report a discrepancy instead of randomly following whichever file was read last.

For this long connected build, I would start with the model you have chosen and its higher coding effort setting, while watching the first integration gate before leaving it unattended. Effort does not grant permissions or remove usage limits. Do not choose Plan-only mode for the actual build. Keep normal tool protections. The source checkpoint policy matters more than asking it to “work until morning.”

The first substantive milestone must be two actual clients, an authoritative server, a race, a validated result and persistent account progress; then a six-client test. Do not accept six animated cars in one client as multiplayer verification.

## 5. Local work while you are away

A local Code/Unity workflow depends on the local computer, editor/service processes, and network remaining available. A sleeping or powered-off machine is not running its Unity tools. Configure your own power/sleep policy deliberately before an unattended run; do not ask an agent to silently change system policy. Locking the screen and hardware-accelerated editor capture can have tool-specific effects, so test the exact arrangement first rather than assuming capture works in every state. [S2]

Cloud can work on committed code, catalogues, and headless jobs that exist in that Cloud environment, but it does not inherit your local editor, packages, licence, or MCP listener. Any separate cloud editor/build setup requires explicit installation, licence/environment verification, and tool access. Avoid an improvised public tunnel to the local bridge merely to use Cloud credits.

Keep a verified Git checkpoint at every meaningful milestone. If switching execution environments, stop the old writer, preserve the newer working tree, fetch the actual task branch, read HANDOFF/VALIDATION, and continue. Do not run two independent agents changing the same Unity scene/project in parallel. No automatic Cloud-to-Local failover is presumed.

## 6. Editor automation without the community bridge

MCP is convenient, not the only possible workflow. Claude Code can create C# editor scripts and use Unity's documented command-line entry points to run named static editor methods and builds. This is a real alternative only when Unity is installed, the correct path/licence/modules are available, and the commands actually execute. The editor supports `-projectPath`, `-batchmode`, `-executeMethod`, and log output. [S11]

Example after Claude has implemented and compiled the named build method:

```powershell
$UnityExe = "<actual installed Unity Editor path>\Unity.exe"
$GamePath = "C:\Dev\NightSignal\Game"
$LogPath = "C:\Dev\NightSignal\Evidence\build-windows.log"
# Run only when no other editor is using this same project checkout.
# NightSignal.Editor.BuildCommands.BuildWindows must really exist first.
& $UnityExe -batchmode -projectPath $GamePath `
  -executeMethod NightSignal.Editor.BuildCommands.BuildWindows `
  -logFile $LogPath -quit
```

Do not paste a guessed editor version path. Do not use `-nographics` for a task that needs actual visual evidence. Launching a build is not proof that it finished: inspect its exit state, log, build artifacts and a running player.

Test Framework commands have their own completion behavior. A typical EditMode invocation is below; do not add an indiscriminate `-quit` that may interrupt asynchronous tests. Check generated results and test counts, not just a zero exit code. Pin the Test Framework version and follow its installed documentation. [S12]

```powershell
& $UnityExe -runTests -batchmode -projectPath $GamePath `
  -testPlatform EditMode `
  -testResults "C:\Dev\NightSignal\Evidence\editmode-results.xml" `
  -logFile "C:\Dev\NightSignal\Evidence\editmode.log"
```

## 7. What to expect at delivery

A Unity game is not a single index.html. Expect the source project, assets, Windows playable build with its accompanying files, dedicated-server builds, control service/database configuration, launch scripts, and evidence. GitHub Pages is a static client/landing-page option, not the multiplayer service. A browser edition additionally needs a real Unity Web build, compatible browser transport and hosted HTTPS/WSS endpoints. [S13, S14]

The project is ambitious enough that an interrupted session should be expected and recoverable. A continuation must reuse the latest source and resume the actual unfinished gate; it must not regenerate the project. The brief protects all 26 courses, 48 rivals, 75 rewards and the full campaign instead of quietly replacing them with a small demo. Their mere presence in a catalogue does not prove they were authored or tested.

## Sources

S1. Unity release/support policy: https://unity.com/releases/unity-6/support
S2. Claude Code desktop environments and shared configuration: https://code.claude.com/docs/en/desktop
S3. Unity Dedicated Server overview: https://docs.unity3d.com/6000.3/Documentation/Manual/dedicated-server.html
S4. MCP for Unity project, licence and release: https://github.com/CoplayDev/unity-mcp ; https://github.com/CoplayDev/unity-mcp/releases/tag/v10.0.0
S5. Bridge installation/configuration: https://coplaydev.github.io/unity-mcp/getting-started/install
S6. Claude Code MCP commands/scopes: https://code.claude.com/docs/en/mcp
S7. Bridge tools and optional provider integrations: https://coplaydev.github.io/unity-mcp/reference/tools
S8. Supabase local development: https://supabase.com/docs/guides/local-development
S9. Supabase authentication: https://supabase.com/docs/guides/auth
S10. JWT verification: https://supabase.com/docs/guides/auth/jwts
S11. Unity editor command line: https://docs.unity3d.com/6000.0/Documentation/Manual/EditorCommandLineArguments.html
S12. Unity Test Framework CLI: https://docs.unity3d.com/Packages/com.unity.test-framework@1.4/manual/reference-command-line.html
S13. GitHub Pages definition: https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages
S14. Unity Transport WebSocket compatibility: https://docs.unity3d.com/Packages/com.unity.transport@2.4/manual/websockets.html

Consult the matching installed versions before execution. Public technical documentation can change; dated research is a starting point, not permission to assume compatibility.
