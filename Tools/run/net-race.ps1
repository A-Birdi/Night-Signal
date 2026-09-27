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
#>
param(
    [ValidateRange(1, 6)][int]$Humans = 2,
    [string]$Stage = 'S01',
    [int]$Port = 7777,
    [int]$TimeoutSeconds = 900,
    [switch]$WindowedFirstClient
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }
try { Invoke-RestMethod -Uri 'http://127.0.0.1:5080/healthz' -TimeoutSec 5 | Out-Null }
catch { throw 'Control plane is not running on 127.0.0.1:5080 (run Tools/run/start-control-plane.ps1).' }

$run = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + "-h$Humans"
$logs = Join-Path $repo "Builds\NetRuns\$run"
$evidence = "Builds/NetRuns/$run/evidence"
New-Item -ItemType Directory -Force $logs | Out-Null

$procs = @()
$server = Start-Process -FilePath $exe -PassThru -ArgumentList @(
    '-batchmode', '-nographics', '-nsServer', '-nsPort', "$Port", '-nsExitAfterMatch',
    '-nsEvidence', $evidence, '-logFile', "`"$logs\server.log`"")
$procs += [pscustomobject]@{ Name = 'server'; Process = $server }
Start-Sleep -Seconds 4

for ($i = 0; $i -lt $Humans; $i++) {
    $role = if ($i -eq 0) { 'leader' } else { 'member' }
    $args = @('-nsClient', '-nsAuto', '-nsDevAccount', "$i", '-nsAutoRole', $role, '-nsAutoHumans', "$Humans",
              '-nsAutoStage', $Stage, '-nsEvidence', $evidence, '-logFile', "`"$logs\client-$i.log`"")
    if (-not ($WindowedFirstClient -and $i -eq 0)) { $args = @('-batchmode', '-nographics') + $args }
    else { $args += @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720') }
    $procs += [pscustomobject]@{ Name = "client-$i ($role)"; Process = (Start-Process -FilePath $exe -PassThru -ArgumentList $args) }
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
Write-Output "evidence: Evidence/net/$run ; logs: Builds/NetRuns/$run"
