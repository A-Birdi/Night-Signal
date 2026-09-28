#requires -Version 5.1
<#
.SYNOPSIS
    Runs a real multi-process race on this machine: one dedicated game server process plus N independent client
    processes, all talking to the locally running control plane (start it first with start-control-plane.ps1).

.DESCRIPTION
    Every process is a separate OS process using real sockets (UDP game traffic, HTTP/WebSocket control plane).
    Addendum 04: the dedicated server binds the IPv4 loopback (127.0.0.1) and advertises it unless -BindHost/-PublicHost
    ask otherwise, and a non-loopback bind is refused before anything starts unless -AllowLan is given. The run records
    the resolved endpoint and the sockets each process really owned (network.json); it never touches Windows Firewall.
    Clients are scripted AutoClients driving with the route-following autopilot through normal inputs — this is
    automated evidence, not a human playtest. Raw player logs go to Builds/NetRuns/<run> (git-ignored: they contain
    local paths); JSON evidence is copied to Evidence/net/<run>.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools/run/net-race.ps1 -Humans 2 -Stage S01

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools/run/net-race.ps1 -Humans 6 -FreeplayCourse C12 -FreeplayAi 6 -CameraClients
    Addendum 03 C11/C12: every client is a small rendered window with its own seeded preferences (view, speedometer
    style, units) and frame-rate cap (30/60/120), and a camera probe; client 0 alone cycles its view mid-race.
#>
param(
    [ValidateRange(1, 6)][int]$Humans = 2,
    [string]$Stage = 'S01',
    [int]$Port = 7777,
    [int]$TimeoutSeconds = 900,
    [switch]$WindowedFirstClient,
    # Freeplay instead of a campaign stage: course id, live AI count (0..12-H) and mode (sprint | circuit | time-attack).
    [string]$FreeplayCourse = '',
    [ValidateRange(0, 11)][int]$FreeplayAi = 0,
    [string]$FreeplayMode = 'sprint',
    # Rendered clients with per-client driving preferences, frame-rate caps and camera probes (Addendum 03 C11/C12).
    [switch]$CameraClients,
    # Application-level impairment on every client: 'delayMs,jitterMs,dropPercent' each way (evidence runs only).
    [string]$Impair = '',
    # Client 0 holds reset this many seconds after the start (a scripted manual recovery; negative = none).
    [int]$ResetAt = -1,
    # Client 0 drops its connection right after that recovery and tries to come back (Addendum 03 R11).
    [switch]$DropAfterReset,
    # Client 1 leaves the race this many seconds after the start; client 0 (if spectating) settles on entrant 1.
    [int]$DropAt = -1,
    # Extra arguments for every client (e.g. '-nsCorrectionBlend position' for an A/B run).
    [string]$ClientExtra = '',
    # Addendum 04: where the server listens and what it advertises. Loopback unless a LAN test is explicitly intended.
    [string]$BindHost = '127.0.0.1',
    [string]$PublicHost = '127.0.0.1',
    # Explicit opt-in for a non-loopback/wildcard bind (a deliberate, separately authorized LAN test only).
    [switch]$AllowLan
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
Import-Module (Join-Path $PSScriptRoot 'NetGuard.psm1') -Force
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }
# Fail closed before anything is launched: loopback unless -AllowLan, a numeric address, a free port.
$endpoint = Resolve-ServerEndpoint -BindHost $BindHost -PublicHost $PublicHost -Port $Port -AllowLan:$AllowLan -Executable $exe
Write-Output "server endpoint: bind $($endpoint.bindHost) advertise $($endpoint.publicHost) udp $($endpoint.port) ($($endpoint.classification), LAN opt-in $($endpoint.lanOptIn))"
try { Invoke-RestMethod -Uri 'http://127.0.0.1:5080/healthz' -TimeoutSec 5 | Out-Null }
catch { throw 'Control plane is not running on 127.0.0.1:5080 (run Tools/run/start-control-plane.ps1).' }

$run = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + "-h$Humans"
if ($FreeplayCourse) { $run += "-$FreeplayCourse-ai$FreeplayAi" }
if ($Impair) { $run += '-impair' }
$logs = Join-Path $repo "Builds\NetRuns\$run"
$evidence = "Builds/NetRuns/$run/evidence"
New-Item -ItemType Directory -Force $logs | Out-Null

$procs = @()
$server = Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList (@(
    '-batchmode', '-nographics', '-nsServer', '-nsExitAfterMatch',
    '-nsEvidence', $evidence, '-logFile', "`"$logs\server.log`"") + (Get-ServerArgs $endpoint))
$procs += [pscustomobject]@{ Name = 'server'; Process = $server }
Start-Sleep -Seconds 4

for ($i = 0; $i -lt $Humans; $i++) {
    $role = if ($i -eq 0) { 'leader' } else { 'member' }
    $clientArgs = @('-nsClient', '-nsAuto', '-nsDevAccount', "$i", '-nsAutoRole', $role, '-nsAutoHumans', "$Humans",
              '-nsAutoStage', $Stage, '-nsEvidence', $evidence, '-logFile', "`"$logs\client-$i.log`"")
    if ($FreeplayCourse) { $clientArgs += @('-nsAutoFreeplay', $FreeplayCourse, '-nsAutoFreeplayAi', "$FreeplayAi", '-nsAutoFreeplayMode', $FreeplayMode) }
    if ($Impair) { $clientArgs += @('-nsImpair', $Impair) }
    if ($ClientExtra) { $clientArgs += ($ClientExtra -split ' ') }
    if ($ResetAt -ge 0 -and $i -eq 0) { $clientArgs += @('-nsAutoResetAt', "$ResetAt") }
    if ($DropAfterReset -and $i -eq 0) { $clientArgs += @('-nsAutoDropAfterReset') }
    if ($DropAt -ge 0 -and $i -eq 1) { $clientArgs += @('-nsAutoDropAt', "$DropAt") }
    if ($DropAt -ge 0 -and $i -eq 0) { $clientArgs += @('-nsAutoSpectateWatch', '1') }
    if ($CameraClients) {
        $views = @('chase-close', 'chase-far', 'hood', 'bumper', 'cockpit', 'chase-far')
        $styles = @('dial', 'strip', 'dial', 'strip', 'dial', 'strip')
        $units = @('kmh', 'mph', 'mph', 'kmh', 'kmh', 'mph')
        $fps = @(60, 30, 120, 60, 30, 120)
        $prefs = Join-Path $logs "prefs-$i"
        New-Item -ItemType Directory -Force $prefs | Out-Null
        $seed = '{"Schema":1,"SpeedStyle":"' + $styles[$i] + '","Units":"' + $units[$i] + '","View":"' + $views[$i] + '"}'
        [System.IO.File]::WriteAllText((Join-Path $prefs 'driving.json'), $seed)
        $clientArgs += @('-nsPrefsFolder', "`"$prefs`"", '-nsTargetFps', "$($fps[$i])", '-nsCameraProbe', "`"$logs\camera-client-$i.json`"",
                         '-screen-fullscreen', '0', '-screen-width', '640', '-screen-height', '360')
        if ($i -eq 0) { $clientArgs += @('-nsProbeCycleAt', '25') }
    }
    elseif (-not ($WindowedFirstClient -and $i -eq 0)) { $clientArgs = @('-batchmode', '-nographics') + $clientArgs }
    else { $clientArgs += @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720') }
    $procs += [pscustomobject]@{ Name = "client-$i ($role)"; Process = (Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList $clientArgs) }
    Start-Sleep -Milliseconds 800
}

# Sample the sockets every launched process really owns while the run lasts (read-only).
$seen = @{}
function Note-Sockets {
    foreach ($p in $procs) {
        if ($p.Process.HasExited) { continue }
        if (-not $seen.ContainsKey($p.Name)) { $seen[$p.Name] = @{ pid = $p.Process.Id; udp = New-Object System.Collections.Generic.HashSet[string]; tcpListen = New-Object System.Collections.Generic.HashSet[string] } }
        foreach ($u in @(Get-NetUDPEndpoint -OwningProcess $p.Process.Id -ErrorAction SilentlyContinue)) { [void]$seen[$p.Name].udp.Add("$($u.LocalAddress):$($u.LocalPort)") }
        foreach ($t in @(Get-NetTCPConnection -OwningProcess $p.Process.Id -State Listen -ErrorAction SilentlyContinue)) { [void]$seen[$p.Name].tcpListen.Add("$($t.LocalAddress):$($t.LocalPort)") }
    }
}
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and ($procs | Where-Object { -not $_.Process.HasExited })) { Note-Sockets; Start-Sleep -Seconds 2 }
# Graceful exits first (the server leaves after the match, clients after their receipts); the timeout only for the rest,
# and only for the processes this run launched.
foreach ($p in $procs) {
    if (-not $p.Process.HasExited) { Stop-Process -Id $p.Process.Id -Force; Write-Output "$($p.Name): TIMEOUT (killed)" }
    else { Write-Output "$($p.Name): exit $($p.Process.ExitCode)" }
}
$released = Wait-PortReleased -Port $Port
Write-Output "udp $Port released after the run: $released"
$serverSockets = if ($seen.ContainsKey('server')) { $seen['server'] } else { $null }
$network = [pscustomobject]@{
    endpoint = $endpoint
    serverPid = $server.Id
    serverGameListener = if ($serverSockets) { @($serverSockets.udp | Where-Object { $_ -like "*:$Port" }) } else { @() }
    processes = @($seen.Keys | Sort-Object | ForEach-Object { [pscustomobject]@{ name = $_; pid = $seen[$_].pid; udp = @($seen[$_].udp); tcpListen = @($seen[$_].tcpListen) } })
    portReleasedAfterRun = $released
    firewall = 'not read or changed by this harness (user-controlled)'
}
$network | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 (Join-Path $logs 'network.json')
Write-Output ("server game listener: " + ($network.serverGameListener -join ', '))

$dest = Join-Path $repo "Evidence\net\$run"
New-Item -ItemType Directory -Force $dest | Out-Null
Get-ChildItem (Join-Path $repo $evidence) -Filter *.json -ErrorAction SilentlyContinue | Copy-Item -Destination $dest
Get-ChildItem $logs -Filter 'camera-client-*.json' -ErrorAction SilentlyContinue | Copy-Item -Destination $dest
Copy-Item (Join-Path $logs 'network.json') -Destination $dest -ErrorAction SilentlyContinue
Write-Output "evidence: Evidence/net/$run ; logs: Builds/NetRuns/$run"
