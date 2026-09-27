#requires -Version 5.1
<#
.SYNOPSIS
    Online UI evidence run: one dedicated game server process plus one windowed client that drives the REAL menu buttons
    through sign-in, convoy, Mode Ready, event proposal, Event Ready, a server-authoritative race and the post-event
    decision (FrontEndApp.UiTourOnline). Requires the local control plane (start-control-plane.ps1).

.DESCRIPTION
    Automation, not a human playtest: the client signs in with a development account from the project's seed file and
    races with the validator autopilot through normal inputs. Screenshots: Builds/Screenshots/tour-online. Raw logs stay
    under Builds/ (git-ignored: they contain local paths).
#>
param([int]$DevAccount = 0, [int]$Port = 7777, [int]$TimeoutSeconds = 600, [switch]$Freeplay, [switch]$Garage, [int]$Intent = -1, [string]$Trial = "")

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }
try { Invoke-RestMethod -Uri 'http://127.0.0.1:5080/healthz' -TimeoutSec 5 | Out-Null }
catch { throw 'Control plane is not running on 127.0.0.1:5080 (run Tools/run/start-control-plane.ps1).' }

$logs = Join-Path $repo 'Builds\NetRuns\tour-online'
New-Item -ItemType Directory -Force $logs | Out-Null
$server = Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList @(
    '-batchmode', '-nographics', '-nsServer', '-nsPort', "$Port", '-nsExitAfterMatch',
    '-nsEvidence', 'Builds/NetRuns/tour-online/evidence', '-logFile', "`"$logs\server.log`"")
Start-Sleep -Seconds 4
$clientArgs = @('-nsUiTourOnline', '-nsDevAccount', "$DevAccount", '-screen-fullscreen', '0', '-screen-width', '1920', '-screen-height', '1080',
    '-logFile', "`"$logs\client.log`"")
if ($Freeplay) { $clientArgs += '-nsUiTourFreeplay' } # Freeplay sprint decided by a course vote
if ($Garage) { $clientArgs += "-nsUiTourGarage" } # online Garage tyre change before the event
if ($Intent -ge 0) { $clientArgs += @('-nsUiTourIntent', "$Intent") } # 4 = Freeplay Time Attack (group, non-contact)
if ($Trial) { $clientArgs += @('-nsUiTourTrial', $Trial) }          # Team Trial id, e.g. TT_BEST
$client = Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList $clientArgs

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and -not $client.HasExited) { Start-Sleep -Seconds 2 }
if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force; Write-Output 'client: TIMEOUT (killed)' } else { Write-Output "client: exit $($client.ExitCode)" }
Start-Sleep -Seconds 3
if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force; Write-Output 'server: stopped' } else { Write-Output "server: exit $($server.ExitCode)" }
Select-String -Path "$logs\client.log" -Pattern 'NightSignal.UiTourOnline' | ForEach-Object { $_.Line }
