#requires -Version 5.1
<#
.SYNOPSIS
    Runs a real multi-process race on this machine: one dedicated game server process plus N independent client
    processes, all talking to the locally running control plane (start it first with start-control-plane.ps1).

.DESCRIPTION
    Every process is a separate OS process using real sockets (UDP game traffic, HTTP/WebSocket control plane).
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
    [switch]$DropAfterReset
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }
try { Invoke-RestMethod -Uri 'http://127.0.0.1:5080/healthz' -TimeoutSec 5 | Out-Null }
catch { throw 'Control plane is not running on 127.0.0.1:5080 (run Tools/run/start-control-plane.ps1).' }

$run = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + "-h$Humans"
if ($FreeplayCourse) { $run += "-$FreeplayCourse-ai$FreeplayAi" }
if ($Impair) { $run += '-impair' }
$logs = Join-Path $repo "Builds\NetRuns\$run"
$evidence = "Builds/NetRuns/$run/evidence"
New-Item -ItemType Directory -Force $logs | Out-Null

$procs = @()
$server = Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList @(
    '-batchmode', '-nographics', '-nsServer', '-nsPort', "$Port", '-nsExitAfterMatch',
    '-nsEvidence', $evidence, '-logFile', "`"$logs\server.log`"")
$procs += [pscustomobject]@{ Name = 'server'; Process = $server }
Start-Sleep -Seconds 4

for ($i = 0; $i -lt $Humans; $i++) {
    $role = if ($i -eq 0) { 'leader' } else { 'member' }
    $clientArgs = @('-nsClient', '-nsAuto', '-nsDevAccount', "$i", '-nsAutoRole', $role, '-nsAutoHumans', "$Humans",
              '-nsAutoStage', $Stage, '-nsEvidence', $evidence, '-logFile', "`"$logs\client-$i.log`"")
    if ($FreeplayCourse) { $clientArgs += @('-nsAutoFreeplay', $FreeplayCourse, '-nsAutoFreeplayAi', "$FreeplayAi", '-nsAutoFreeplayMode', $FreeplayMode) }
    if ($Impair) { $clientArgs += @('-nsImpair', $Impair) }
    if ($ResetAt -ge 0 -and $i -eq 0) { $clientArgs += @('-nsAutoResetAt', "$ResetAt") }
    if ($DropAfterReset -and $i -eq 0) { $clientArgs += @('-nsAutoDropAfterReset') }
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

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and ($procs | Where-Object { -not $_.Process.HasExited })) { Start-Sleep -Seconds 2 }
foreach ($p in $procs) {
    if (-not $p.Process.HasExited) { Stop-Process -Id $p.Process.Id -Force; Write-Output "$($p.Name): TIMEOUT (killed)" }
    else { Write-Output "$($p.Name): exit $($p.Process.ExitCode)" }
}

$dest = Join-Path $repo "Evidence\net\$run"
New-Item -ItemType Directory -Force $dest | Out-Null
Get-ChildItem (Join-Path $repo $evidence) -Filter *.json -ErrorAction SilentlyContinue | Copy-Item -Destination $dest
Get-ChildItem $logs -Filter 'camera-client-*.json' -ErrorAction SilentlyContinue | Copy-Item -Destination $dest
Write-Output "evidence: Evidence/net/$run ; logs: Builds/NetRuns/$run"
